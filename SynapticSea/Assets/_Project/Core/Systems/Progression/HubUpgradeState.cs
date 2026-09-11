// Ported from scripts/systems/hub_upgrade_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The duck-typed meta-state surface <see cref="HubUpgradeState"/> calls (Godot guarded with
    /// <c>has_method("get_meta_currency")</c> / <c>has_method("is_hub_upgrade_unlocked")</c>).
    /// Implemented by <see cref="MetaProgressionState"/>.
    /// </summary>
    public interface IHubUpgradeWallet
    {
        long GetMetaCurrency();
        bool IsHubUpgradeUnlocked(string upgradeId);
        bool SpendMetaCurrency(long amount);
        bool UnlockHubUpgrade(string upgradeId);
        bool AddMetaCurrency(long amount);
    }

    /// <summary>
    /// REQ-PM-007 / ADR-0033 hub-upgrade catalog + purchase gates over <c>data/player/hub_upgrades.json</c>.
    /// Purchases gate on catalog membership, prerequisite upgrades, and meta currency.
    /// </summary>
    public class HubUpgradeState : IStatusLineProvider
    {
        public const string DefaultUpgradesPath = "res://data/player/hub_upgrades.json";

        readonly GdDict _upgradesById = new GdDict(); // upgrade_id -> full entry dict

        public static GdDict LoadDefault(string path = DefaultUpgradesPath)
        {
            if (!CatalogRegistry.Exists(path)) return new GdDict();
            object parsed = CatalogRegistry.Load(path);
            if (!(parsed is GdDict dict)) return new GdDict();
            return dict;
        }

        /// <summary>Configures from a parsed catalog, or loads the default file when it is null/empty.</summary>
        public bool Configure(GdDict catalog = null)
        {
            _upgradesById.Clear();
            GdDict src = catalog;
            if (src == null || src.IsEmpty) src = LoadDefault();
            if (src.IsEmpty) return false;
            object variant = src.Get("upgrades", new GdArray());
            if (!(variant is GdArray upgrades)) return false;
            foreach (var entry in upgrades)
            {
                if (!(entry is GdDict entryDict)) continue;
                string uid = V.Str(entryDict.Get("upgrade_id", ""));
                if (uid.Length == 0) continue;
                _upgradesById[uid] = entryDict.DeepCopy();
            }
            return true;
        }

        public bool IsKnown(string upgradeId) => _upgradesById.Has(upgradeId);

        public int GetUpgradeCount() => _upgradesById.Count;

        public GdArray GetUpgradeIds() => InfraCompat.SortedKeys(_upgradesById);

        public long GetCost(string upgradeId)
        {
            if (!IsKnown(upgradeId)) return -1;
            return V.I64(Entry(upgradeId).Get("cost", 0L));
        }

        public GdArray GetRequires(string upgradeId)
        {
            if (!IsKnown(upgradeId)) return new GdArray();
            object variant = Entry(upgradeId).Get("requires", new GdArray());
            if (!(variant is GdArray requires)) return new GdArray();
            var output = new GdArray();
            foreach (var r in requires) output.Add(V.Str(r));
            return output;
        }

        /// <summary><c>get_effect(id, key)</c> with the GDScript default fallback (the int 0).</summary>
        public object GetEffect(string upgradeId, string effectKey) => GetEffect(upgradeId, effectKey, 0L);

        public object GetEffect(string upgradeId, string effectKey, object fallback)
        {
            if (!IsKnown(upgradeId)) return fallback;
            GdDict effects = Entry(upgradeId).Get("effects", new GdDict()) as GdDict;
            if (effects == null || !effects.Has(effectKey)) return fallback;
            return effects[effectKey];
        }

        public string GetDisplayName(string upgradeId)
        {
            if (!IsKnown(upgradeId)) return "";
            return V.Str(Entry(upgradeId).Get("display_name", upgradeId));
        }

        public string GetDescription(string upgradeId)
        {
            if (!IsKnown(upgradeId)) return "";
            return V.Str(Entry(upgradeId).Get("description", ""));
        }

        /// <summary>
        /// <c>{can, reason, ...}</c> for whether the player can afford + satisfy the prereqs. <paramref name="metaState"/>
        /// is any object; a non-<see cref="IHubUpgradeWallet"/> yields <c>invalid_meta_state</c>.
        /// </summary>
        public GdDict CanPurchase(string upgradeId, object metaState)
        {
            if (!IsKnown(upgradeId)) return new GdDict { { "can", false }, { "reason", "unknown_upgrade" } };
            long cost = GetCost(upgradeId);
            if (cost < 0) return new GdDict { { "can", false }, { "reason", "invalid_cost" } };
            if (metaState == null) return new GdDict { { "can", false }, { "reason", "no_meta_state" } };
            if (!(metaState is IHubUpgradeWallet meta)) return new GdDict { { "can", false }, { "reason", "invalid_meta_state" } };
            if (meta.IsHubUpgradeUnlocked(upgradeId)) return new GdDict { { "can", false }, { "reason", "already_owned" } };
            GdArray prereqs = GetRequires(upgradeId);
            var missing = new GdArray();
            foreach (var reqId in prereqs)
            {
                if (!meta.IsHubUpgradeUnlocked(V.Str(reqId))) missing.Add(reqId);
            }
            if (!missing.IsEmpty) return new GdDict { { "can", false }, { "reason", "missing_prereqs" }, { "missing", missing } };
            if (meta.GetMetaCurrency() < cost)
            {
                return new GdDict
                {
                    { "can", false }, { "reason", "insufficient_currency" }, { "cost", cost }, { "currency", meta.GetMetaCurrency() },
                };
            }
            return new GdDict { { "can", true }, { "reason", "ok" }, { "cost", cost } };
        }

        /// <summary>Deducts the cost and unlocks the upgrade on success; returns true on a state change.</summary>
        public bool Purchase(string upgradeId, object metaState)
        {
            if (metaState == null) return false;
            GdDict check = CanPurchase(upgradeId, metaState);
            if (!V.Bool(check.Get("can", false))) return false;
            var meta = (IHubUpgradeWallet)metaState;
            long cost = GetCost(upgradeId);
            if (!meta.SpendMetaCurrency(cost)) return false;
            if (!meta.UnlockHubUpgrade(upgradeId))
            {
                // Roll back on idempotency miss (defensive).
                meta.AddMetaCurrency(cost);
                return false;
            }
            return true;
        }

        /// <summary>Per-upgrade entries for the hub upgrade panel UI, sorted by upgrade_id.</summary>
        public GdArray GetUpgradeEntries(object metaState = null)
        {
            var output = new GdArray();
            var meta = metaState as IHubUpgradeWallet;
            foreach (var uidKey in _upgradesById.Keys)
            {
                string uid = V.Str(uidKey);
                GdDict entry = Entry(uid);
                long cost = V.I64(entry.Get("cost", 0L));
                GdArray prereqs = GetRequires(uid);
                bool owned = meta != null && meta.IsHubUpgradeUnlocked(uid);
                bool affordable = meta != null && meta.GetMetaCurrency() >= cost;
                output.Add(new GdDict
                {
                    { "upgrade_id", uid },
                    { "display_name", V.Str(entry.Get("display_name", uid)) },
                    { "description", V.Str(entry.Get("description", "")) },
                    { "cost", cost },
                    { "requires", prereqs },
                    { "effects", (entry.Get("effects", new GdDict()) as GdDict ?? new GdDict()).DeepCopy() },
                    { "owned", owned },
                    { "affordable", affordable },
                });
            }
            output.SortCustom((a, b) => V.CompareCodePoints(V.Str(((GdDict)a).Get("upgrade_id", "")), V.Str(((GdDict)b).Get("upgrade_id", ""))) < 0);
            return output;
        }

        /// <summary>Effective XP multipliers per category: 1.0 times every owned upgrade's xp_multiplier_bonus.</summary>
        public GdDict ComposeXpMultipliers(object metaState)
        {
            var output = new GdDict
            {
                { "technical", 1.0 },
                { "medical", 1.0 },
                { "navigation", 1.0 },
                { "survival", 1.0 },
                { "social", 1.0 },
            };
            if (!(metaState is IHubUpgradeWallet meta)) return output;
            foreach (var uidKey in _upgradesById.Keys)
            {
                string uid = V.Str(uidKey);
                if (!meta.IsHubUpgradeUnlocked(uid)) continue;
                object bonus = GetEffect(uid, "xp_multiplier_bonus", new GdDict());
                if (!(bonus is GdDict bonusDict)) continue;
                foreach (var cat in bonusDict.Keys)
                {
                    double mult = V.F64(bonusDict[cat]);
                    output[V.Str(cat)] = V.F64(output.Get(V.Str(cat), 1.0)) * mult;
                }
            }
            return output;
        }

        /// <summary>Effective starting-skill bonuses: sum of every owned upgrade's starting_skill_bonus.</summary>
        public GdDict ComposeStartingSkillBonuses(object metaState)
        {
            var output = new GdDict();
            if (!(metaState is IHubUpgradeWallet meta)) return output;
            foreach (var uidKey in _upgradesById.Keys)
            {
                string uid = V.Str(uidKey);
                if (!meta.IsHubUpgradeUnlocked(uid)) continue;
                object bonus = GetEffect(uid, "starting_skill_bonus", new GdDict());
                if (!(bonus is GdDict bonusDict)) continue;
                foreach (var sid in bonusDict.Keys)
                {
                    long v = V.I64(bonusDict[sid]);
                    output[V.Str(sid)] = V.I64(output.Get(V.Str(sid), 0L)) + v;
                }
            }
            return output;
        }

        public List<string> GetStatusLines()
        {
            return new List<string> { InfraCompat.Fmt("Hub Upgrades Catalog: {0}", _upgradesById.Count) };
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        GdDict Entry(string upgradeId) => (GdDict)_upgradesById[upgradeId];
    }
}
