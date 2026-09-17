// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: wound treatment (_try_bandage_selected_wound /
// _try_treat_selected_wound / _first_inventory_item, 3876-3920), plus Unity-port additions for inherited Godot gaps:
// wounds from combat damage, the per-frame wounds stage (E1) and the Wounds panel queries (A6).
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>Severity healed per second by a treated wound (0.35 severity closes in about 90 s).</summary>
        public const double WOUND_TREATED_HEAL_PER_SECOND = 0.004;

        /// <summary>Severity healed per second by a bandaged, untreated wound.</summary>
        public const double WOUND_BANDAGED_HEAL_PER_SECOND = 0.001;

        public const string WOUND_ACTION_BANDAGE = "bandage";
        public const string WOUND_ACTION_TREAT = "treat";

        // ------------------------------------------------------------------ E1: tick + damage
        /// <summary>
        /// The <c>wounds</c> stage (both branches): ages wounds (infection creep) and heals bandaged / treated ones. The
        /// bleed itself is health damage inside <c>survival_attrition</c> (<c>wound_health_drain</c> /
        /// <c>wound_thirst_mult</c> in the vitals context), so it shares the vitals death check.
        /// </summary>
        internal void StageWounds(double delta)
        {
            if (WoundState == null || WoundState.Wounds.IsEmpty)
                return;
            WoundState.Tick(delta);
            if (WoundState.Heal(delta, WOUND_TREATED_HEAL_PER_SECOND, WOUND_BANDAGED_HEAL_PER_SECOND) > 0)
                Events.RaiseWoundsChanged(WoundState);
        }

        /// <summary>Combat damage opens (or worsens) a wound via <see cref="WoundState.SuggestFromDamage"/> (below 2 damage: none).</summary>
        void ApplyWoundFromCombatDamage(double damage, GdDict ev)
        {
            if (WoundState == null || damage <= 0.0)
                return;
            GdDict suggestion = WoundState.SuggestFromDamage(damage, V.Str((ev ?? new GdDict()).Get("damage_type", "")));
            if (suggestion.IsEmpty)
                return;
            suggestion["source_id"] = V.Str((ev ?? new GdDict()).Get("source_id", ""));
            if (WoundState.ApplyOrWorsenWound(suggestion).Length > 0)
                Events.RaiseWoundsChanged(WoundState);
        }

        // ------------------------------------------------------------------ A6: treatment
        string FirstInventoryItem(IReadOnlyList<string> itemIds)
        {
            if (InventoryState == null)
                return "";
            foreach (string id in itemIds)
            {
                if (InventoryState.GetQuantity(id) > 0)
                    return id;
            }
            return "";
        }

        /// <summary>
        /// What <paramref name="action"/> would do to <paramref name="woundId"/> without applying it:
        /// <c>{ok, action, wound_id, item_id, reason}</c>. Refusal reasons: <c>wounds_unavailable</c>,
        /// <c>unknown_action</c>, <c>unknown_wound</c>, <c>wound_healed</c>, <c>already_bandaged</c> (bandage on a
        /// bandaged or treated wound), <c>already_treated</c>, <c>no_bandage_item</c>, <c>no_treatment_item</c>.
        /// </summary>
        public GdDict EvaluateWoundTreatment(string action, string woundId)
        {
            var result = new GdDict { { "ok", false }, { "action", action ?? "" }, { "wound_id", woundId ?? "" }, { "item_id", "" }, { "reason", "" } };
            if (WoundState == null || InventoryState == null)
            {
                result["reason"] = "wounds_unavailable";
                return result;
            }
            bool bandage = action == WOUND_ACTION_BANDAGE;
            if (!bandage && action != WOUND_ACTION_TREAT)
            {
                result["reason"] = "unknown_action";
                return result;
            }
            GdDict wound = WoundState.GetWound(woundId ?? "");
            if (wound.IsEmpty)
            {
                result["reason"] = "unknown_wound";
                return result;
            }
            if (V.F64(wound.Get("severity", 0.0)) <= 0.001)
            {
                result["reason"] = "wound_healed";
                return result;
            }
            if (V.Bool(wound.Get("treated", false)))
            {
                result["reason"] = bandage ? "already_bandaged" : "already_treated";
                return result;
            }
            if (bandage && V.Bool(wound.Get("bandaged", false)))
            {
                result["reason"] = "already_bandaged";
                return result;
            }
            string itemId = FirstInventoryItem(bandage ? BANDAGE_ITEM_IDS : TREAT_ITEM_IDS);
            result["item_id"] = itemId;
            if (itemId.Length == 0)
            {
                result["reason"] = bandage ? "no_bandage_item" : "no_treatment_item";
                return result;
            }
            result["ok"] = true;
            return result;
        }

        /// <summary>
        /// Bandage a wound with the first bandage item in the inventory (<see cref="BANDAGE_ITEM_IDS"/>): consumes 1, plays
        /// <c>SFX_WOUND_BANDAGE</c>, emits the <c>bandage_wound</c> training event. A refusal plays <c>UI_PANEL_CLOSE</c>.
        /// Returns <see cref="EvaluateWoundTreatment"/>'s shape and raises <see cref="SessionEvents.WoundTreatmentResult"/>.
        /// </summary>
        public GdDict BandageWound(string woundId) => ApplyWoundTreatment(WOUND_ACTION_BANDAGE, woundId);

        /// <summary>
        /// Treat a wound with the first medical item (<see cref="TREAT_ITEM_IDS"/>): severity -0.35, infection cleared,
        /// consumes 1, plays <c>SFX_WOUND_TREAT</c>, emits <c>treat_wound</c>. Same result shape as <see cref="BandageWound"/>.
        /// </summary>
        public GdDict TreatWound(string woundId) => ApplyWoundTreatment(WOUND_ACTION_TREAT, woundId);

        /// <summary><see cref="BandageWound"/> as a bool (Godot <c>_try_bandage_selected_wound</c>).</summary>
        public bool TryBandageWound(string woundId) => BandageWound(woundId).GetBool("ok");

        /// <summary><see cref="TreatWound"/> as a bool (Godot <c>_try_treat_selected_wound</c>).</summary>
        public bool TryTreatWound(string woundId) => TreatWound(woundId).GetBool("ok");

        GdDict ApplyWoundTreatment(string action, string woundId)
        {
            GdDict result = EvaluateWoundTreatment(action, woundId);
            if (result.GetBool("ok"))
            {
                bool bandage = action == WOUND_ACTION_BANDAGE;
                bool applied = bandage ? WoundState.Bandage(woundId) : WoundState.Treat(woundId, 0.35);
                if (!applied)
                {
                    result["ok"] = false;
                    result["reason"] = "unknown_wound";
                }
                else
                {
                    InventoryState.RemoveItem(V.Str(result["item_id"]), 1);
                    PlaySfx(bandage ? AudioEventSeam.SFX_WOUND_BANDAGE : AudioEventSeam.SFX_WOUND_TREAT);
                    EmitTrainingEvent(bandage ? "bandage_wound" : "treat_wound", woundId);
                    EnsureConsumableHotbarAssignments();
                    RecomputePlayerEncumbrance();
                    RefreshConsumableUi();
                    Events.RaiseWoundsChanged(WoundState);
                }
            }
            if (!result.GetBool("ok"))
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
            Events.RaiseWoundTreatmentResult(result);
            return result;
        }

        /// <summary>
        /// The Wounds panel list: every open wound (severity above 0.001) in wound order, each
        /// <c>{wound_id, kind, body_part, severity, bleed_rate, infection_chance, bandaged, treated,
        /// can_bandage, bandage_item_id, bandage_reason, can_treat, treat_item_id, treat_reason}</c>
        /// (<c>*_reason</c> is "" when allowed; see <see cref="EvaluateWoundTreatment"/>).
        /// </summary>
        public GdArray GetTreatableWounds()
        {
            var output = new GdArray();
            if (WoundState == null)
                return output;
            foreach (object w in WoundState.Wounds)
            {
                if (!(w is GdDict e) || V.F64(e.Get("severity", 0.0)) <= 0.001)
                    continue;
                string id = V.Str(e.Get("wound_id", ""));
                GdDict bandage = EvaluateWoundTreatment(WOUND_ACTION_BANDAGE, id);
                GdDict treat = EvaluateWoundTreatment(WOUND_ACTION_TREAT, id);
                output.Add(new GdDict
                {
                    { "wound_id", id },
                    { "kind", V.Str(e.Get("kind", "")) },
                    { "body_part", V.Str(e.Get("body_part", "")) },
                    { "severity", V.F64(e.Get("severity", 0.0)) },
                    { "bleed_rate", V.F64(e.Get("bleed_rate", 0.0)) },
                    { "infection_chance", V.F64(e.Get("infection_chance", 0.0)) },
                    { "bandaged", V.Bool(e.Get("bandaged", false)) },
                    { "treated", V.Bool(e.Get("treated", false)) },
                    { "can_bandage", bandage.GetBool("ok") },
                    { "bandage_item_id", V.Str(bandage["item_id"]) },
                    { "bandage_reason", V.Str(bandage["reason"]) },
                    { "can_treat", treat.GetBool("ok") },
                    { "treat_item_id", V.Str(treat["item_id"]) },
                    { "treat_reason", V.Str(treat["reason"]) },
                });
            }
            return output;
        }
    }
}
