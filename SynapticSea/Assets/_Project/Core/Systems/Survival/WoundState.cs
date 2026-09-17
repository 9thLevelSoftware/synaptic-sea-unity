// Ported from scripts/systems/wound_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-C3.1a typed wounds (laceration/burn/fracture/puncture): severity, bleed, infection risk, treatment,
    /// and the WorkAction work-speed multiplier. Never touches the scene tree.
    /// </summary>
    public sealed class WoundState : ISimModel, IStatusLineProvider
    {
        public const string KIND_LACERATION = "laceration";
        public const string KIND_BURN = "burn";
        public const string KIND_FRACTURE = "fracture";
        public const string KIND_PUNCTURE = "puncture";

        public const string BODY_TORSO = "torso";
        public const string BODY_ARM = "arm";
        public const string BODY_LEG = "leg";
        public const string BODY_HEAD = "head";

        public static readonly IReadOnlyList<string> VALID_KINDS = new[] { KIND_LACERATION, KIND_BURN, KIND_FRACTURE, KIND_PUNCTURE };
        public static readonly IReadOnlyList<string> VALID_BODY = new[] { BODY_TORSO, BODY_ARM, BODY_LEG, BODY_HEAD };

        /// <summary>
        /// Array of { wound_id, kind, body_part, severity (0..1), bleed_rate, infection_chance,
        /// treated, bandaged, age_seconds, source_id }.
        /// </summary>
        public GdArray Wounds = new GdArray();

        long _nextId = 1;

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Wounds.Clear();
            _nextId = 1;
            object raw = config.Get("wounds", new GdArray());
            if (raw is GdArray entries)
            {
                foreach (object entry in entries)
                {
                    if (entry is GdDict d) IngestWound(d);
                }
            }
        }

        public void Clear()
        {
            Wounds.Clear();
            _nextId = 1;
        }

        public long WoundCount() => Wounds.Count;

        public long ActiveCount()
        {
            long n = 0;
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                if (V.F64(e.Get("severity", 0.0)) > 0.001) n += 1;
            }
            return n;
        }

        /// <summary>Applies a wound from a damage event (kind, body_part, severity, optional source_id). Returns the wound_id or "" on reject.</summary>
        public string ApplyWound(GdDict @event)
        {
            string kind = V.Str(@event.Get("kind", KIND_LACERATION));
            if (!Contains(VALID_KINDS, kind)) return "";
            string body = V.Str(@event.Get("body_part", BODY_TORSO));
            if (!Contains(VALID_BODY, body)) body = BODY_TORSO;
            double severity = GdMath.Clampf(V.F64(@event.Get("severity", 0.3)), 0.05, 1.0);
            string woundId = V.Str(@event.Get("wound_id", ""));
            if (woundId.Length == 0)
            {
                woundId = "w" + SurvivalCompat.FormatD(_nextId);
                _nextId += 1;
            }
            double bleed = BaseBleed(kind, severity);
            double infection = BaseInfection(kind, severity);
            var entry = new GdDict
            {
                { "wound_id", woundId },
                { "kind", kind },
                { "body_part", body },
                { "severity", severity },
                { "bleed_rate", bleed },
                { "infection_chance", infection },
                { "treated", false },
                { "bandaged", false },
                { "age_seconds", 0.0 },
                { "source_id", V.Str(@event.Get("source_id", "")) },
            };
            Wounds.Append(entry);
            return woundId;
        }

        void IngestWound(GdDict entry)
        {
            string kind = V.Str(entry.Get("kind", KIND_LACERATION));
            if (!Contains(VALID_KINDS, kind)) return;
            string woundId = V.Str(entry.Get("wound_id", ""));
            if (woundId.Length == 0)
            {
                woundId = "w" + SurvivalCompat.FormatD(_nextId);
                _nextId += 1;
            }
            else
            {
                long num = V.StringToInt(SurvivalCompat.TrimPrefix(woundId, "w"));
                if (num >= _nextId) _nextId = num + 1;
            }
            double severity = GdMath.Clampf(V.F64(entry.Get("severity", 0.3)), 0.0, 1.0);
            Wounds.Append(new GdDict
            {
                { "wound_id", woundId },
                { "kind", kind },
                { "body_part", V.Str(entry.Get("body_part", BODY_TORSO)) },
                { "severity", severity },
                { "bleed_rate", V.F64(entry.Get("bleed_rate", BaseBleed(kind, severity))) },
                { "infection_chance", V.F64(entry.Get("infection_chance", BaseInfection(kind, severity))) },
                { "treated", V.Bool(entry.Get("treated", false)) },
                { "bandaged", V.Bool(entry.Get("bandaged", false)) },
                { "age_seconds", V.F64(entry.Get("age_seconds", 0.0)) },
                { "source_id", V.Str(entry.Get("source_id", "")) },
            });
        }

        static double BaseBleed(string kind, double severity)
        {
            switch (kind)
            {
                case KIND_LACERATION: return severity * 0.35;
                case KIND_PUNCTURE: return severity * 0.45;
                case KIND_BURN: return severity * 0.08;
                case KIND_FRACTURE: return severity * 0.05;
                default: return severity * 0.2;
            }
        }

        static double BaseInfection(string kind, double severity)
        {
            switch (kind)
            {
                case KIND_BURN: return GdMath.Clampf(severity * 0.55, 0.0, 0.95);
                case KIND_PUNCTURE: return GdMath.Clampf(severity * 0.40, 0.0, 0.90);
                case KIND_LACERATION: return GdMath.Clampf(severity * 0.25, 0.0, 0.80);
                case KIND_FRACTURE: return GdMath.Clampf(severity * 0.10, 0.0, 0.50);
                default: return GdMath.Clampf(severity * 0.2, 0.0, 0.7);
            }
        }

        public GdDict GetWound(string woundId)
        {
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                if (V.Str(e.Get("wound_id", "")) == woundId) return e.DeepCopy();
            }
            return new GdDict();
        }

        /// <summary>Total bleed rate across wounds (health/sec contribution for vitals); bandage x0.25, treated x0.1.</summary>
        public double TotalBleedRate()
        {
            double total = 0.0;
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                if (V.F64(e.Get("severity", 0.0)) <= 0.001) continue;
                double rate = V.F64(e.Get("bleed_rate", 0.0));
                if (V.Bool(e.Get("bandaged", false))) rate *= 0.25;
                if (V.Bool(e.Get("treated", false))) rate *= 0.1;
                total += rate;
            }
            return total;
        }

        /// <summary>Peak infection chance among open (untreated) wounds.</summary>
        public double PeakInfectionChance()
        {
            double peak = 0.0;
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                if (V.Bool(e.Get("treated", false))) continue;
                if (V.F64(e.Get("severity", 0.0)) <= 0.001) continue;
                peak = Math.Max(peak, V.F64(e.Get("infection_chance", 0.0)));
            }
            return peak;
        }

        /// <summary>Fracture / arm injury slows work (WorkActionState context work_speed_mult).</summary>
        public double WorkSpeedMultiplier()
        {
            double mult = 1.0;
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                double sev = V.F64(e.Get("severity", 0.0));
                if (sev <= 0.001) continue;
                string kind = V.Str(e.Get("kind", ""));
                string body = V.Str(e.Get("body_part", ""));
                if (kind == KIND_FRACTURE)
                {
                    // Fractures always tax work speed; arm fractures hit harder.
                    double tax = 0.15 + sev * 0.35;
                    if (body == BODY_ARM) tax = 0.25 + sev * 0.50;
                    mult *= GdMath.Clampf(1.0 - tax, 0.15, 1.0);
                }
                else if (body == BODY_ARM && (kind == KIND_LACERATION || kind == KIND_PUNCTURE))
                {
                    mult *= GdMath.Clampf(1.0 - sev * 0.20, 0.4, 1.0);
                }
            }
            return GdMath.Clampf(mult, 0.05, 1.0);
        }

        /// <summary>Movement speed multiplier for leg fractures.</summary>
        public double MovementSpeedMultiplier()
        {
            double mult = 1.0;
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                double sev = V.F64(e.Get("severity", 0.0));
                if (sev <= 0.001) continue;
                if (V.Str(e.Get("kind", "")) == KIND_FRACTURE && V.Str(e.Get("body_part", "")) == BODY_LEG)
                    mult *= GdMath.Clampf(1.0 - (0.20 + sev * 0.40), 0.2, 1.0);
            }
            return GdMath.Clampf(mult, 0.1, 1.0);
        }

        /// <summary>Thirst drain multiplier: open bleeding wounds raise thirst.</summary>
        public double ThirstDrainMultiplier()
        {
            double bleed = TotalBleedRate();
            return 1.0 + GdMath.Clampf(bleed * 0.8, 0.0, 1.5);
        }

        /// <summary>Bandage reduces bleed immediately. Returns false if the wound is missing.</summary>
        public bool Bandage(string woundId)
        {
            for (int i = 0; i < Wounds.Count; i++)
            {
                if (!(Wounds[i] is GdDict e)) continue;
                if (V.Str(e.Get("wound_id", "")) != woundId) continue;
                e["bandaged"] = true;
                e["bleed_rate"] = V.F64(e.Get("bleed_rate", 0.0)) * 0.4;
                Wounds[i] = e;
                return true;
            }
            return false;
        }

        /// <summary>Full treatment (medicine): clears infection risk, marks treated, reduces severity.</summary>
        public bool Treat(string woundId, double severityReduce = 0.35)
        {
            for (int i = 0; i < Wounds.Count; i++)
            {
                if (!(Wounds[i] is GdDict e)) continue;
                if (V.Str(e.Get("wound_id", "")) != woundId) continue;
                e["treated"] = true;
                e["bandaged"] = true;
                e["infection_chance"] = 0.0;
                e["severity"] = Math.Max(0.0, V.F64(e.Get("severity", 0.0)) - Math.Max(0.0, severityReduce));
                e["bleed_rate"] = BaseBleed(V.Str(e.Get("kind", "")), V.F64(e.Get("severity", 0.0))) * 0.15;
                Wounds[i] = e;
                return true;
            }
            return false;
        }

        /// <summary>Ages wounds; untreated severity slowly creeps infection_chance. (GDScript returns void.)</summary>
        public void Tick(double deltaSeconds)
        {
            if (deltaSeconds <= 0.0) return;
            for (int i = 0; i < Wounds.Count; i++)
            {
                if (!(Wounds[i] is GdDict e)) continue;
                e["age_seconds"] = V.F64(e.Get("age_seconds", 0.0)) + deltaSeconds;
                if (!V.Bool(e.Get("treated", false)) && V.F64(e.Get("severity", 0.0)) > 0.0)
                {
                    double inf = V.F64(e.Get("infection_chance", 0.0));
                    e["infection_chance"] = GdMath.Clampf(inf + deltaSeconds * 0.002 * V.F64(e.Get("severity", 0.0)), 0.0, 0.98);
                }
                Wounds[i] = e;
            }
        }

        /// <summary>
        /// Unity-port addition (E1; Godot wounds never healed): reduces the severity of treated wounds by
        /// <paramref name="treatedPerSecond"/> and of bandaged-only wounds by <paramref name="bandagedPerSecond"/>, scaling
        /// their bleed rate with the severity ratio. Untreated, unbandaged wounds do not heal. Returns how many wounds
        /// reached zero severity this call.
        /// </summary>
        public long Heal(double deltaSeconds, double treatedPerSecond, double bandagedPerSecond)
        {
            if (deltaSeconds <= 0.0) return 0;
            long closed = 0;
            for (int i = 0; i < Wounds.Count; i++)
            {
                if (!(Wounds[i] is GdDict e)) continue;
                double sev = V.F64(e.Get("severity", 0.0));
                if (sev <= 0.0) continue;
                double rate = V.Bool(e.Get("treated", false)) ? treatedPerSecond : (V.Bool(e.Get("bandaged", false)) ? bandagedPerSecond : 0.0);
                if (rate <= 0.0) continue;
                double next = Math.Max(0.0, sev - rate * deltaSeconds);
                e["bleed_rate"] = next <= 0.0 ? 0.0 : V.F64(e.Get("bleed_rate", 0.0)) * (next / sev);
                e["severity"] = next;
                if (sev > 0.001 && next <= 0.001) closed += 1;
            }
            return closed;
        }

        /// <summary>
        /// Unity-port addition (E1): applies a damage wound, or worsens an open wound of the same kind and body part that
        /// is neither bandaged nor treated (severity adds, bleed and infection are re-derived). Returns the wound id or "".
        /// </summary>
        public string ApplyOrWorsenWound(GdDict @event)
        {
            string kind = V.Str(@event.Get("kind", KIND_LACERATION));
            if (!Contains(VALID_KINDS, kind)) return "";
            string body = V.Str(@event.Get("body_part", BODY_TORSO));
            if (!Contains(VALID_BODY, body)) body = BODY_TORSO;
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                if (V.Str(e.Get("kind", "")) != kind || V.Str(e.Get("body_part", "")) != body) continue;
                if (V.Bool(e.Get("treated", false)) || V.Bool(e.Get("bandaged", false))) continue;
                if (V.F64(e.Get("severity", 0.0)) <= 0.001) continue;
                double sev = GdMath.Clampf(V.F64(e.Get("severity", 0.0)) + GdMath.Clampf(V.F64(@event.Get("severity", 0.3)), 0.05, 1.0), 0.05, 1.0);
                e["severity"] = sev;
                e["bleed_rate"] = BaseBleed(kind, sev);
                e["infection_chance"] = Math.Max(V.F64(e.Get("infection_chance", 0.0)), BaseInfection(kind, sev));
                return V.Str(e.Get("wound_id", ""));
            }
            return ApplyWound(@event);
        }

        /// <summary>Converts a damage-pipeline result into a wound event suggestion ({} below 2 damage).</summary>
        public static GdDict SuggestFromDamage(double finalDamage, string damageType = "", string bodyPart = BODY_TORSO)
        {
            if (finalDamage < 2.0) return new GdDict();
            string kind;
            switch (damageType)
            {
                case "burn":
                case "fire":
                case "heat":
                    kind = KIND_BURN;
                    break;
                case "blunt":
                case "crush":
                case "impact":
                    kind = KIND_FRACTURE;
                    break;
                case "pierce":
                case "bullet":
                case "stab":
                    kind = KIND_PUNCTURE;
                    break;
                default:
                    kind = KIND_LACERATION;
                    break;
            }
            double severity = GdMath.Clampf(finalDamage / 40.0, 0.1, 1.0);
            return new GdDict
            {
                { "kind", kind },
                { "body_part", bodyPart.Length != 0 ? bodyPart : BODY_TORSO },
                { "severity", severity },
            };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", "wound_state_v1" },
                { "next_id", _nextId },
                { "count", (long)Wounds.Count },
                { "wounds", Wounds.DeepCopy() },
                { "bleed_rate", TotalBleedRate() },
                { "work_speed_mult", WorkSpeedMultiplier() },
                { "move_speed_mult", MovementSpeedMultiplier() },
                { "thirst_mult", ThirstDrainMultiplier() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            Wounds.Clear();
            _nextId = Math.Max(1L, V.I64(summary.Get("next_id", 1L)));
            object raw = summary.Get("wounds", new GdArray());
            if (raw is GdArray entries)
            {
                foreach (object entry in entries)
                {
                    if (entry is GdDict d) Wounds.Append(d.DeepCopy());
                }
            }
            return true;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (Wounds.IsEmpty)
            {
                lines.Add("Wounds: none");
                return lines;
            }
            lines.Add("Wounds: " + SurvivalCompat.FormatD(ActiveCount()) +
                      " bleed=" + SurvivalCompat.FormatF(TotalBleedRate(), 2) +
                      " work×" + SurvivalCompat.FormatF(WorkSpeedMultiplier(), 2));
            foreach (object w in Wounds)
            {
                if (!(w is GdDict e)) continue;
                if (V.F64(e.Get("severity", 0.0)) <= 0.001) continue;
                string flags = "";
                if (V.Bool(e.Get("treated", false))) flags += "T";
                if (V.Bool(e.Get("bandaged", false))) flags += "B";
                if (flags.Length == 0) flags = "-";
                lines.Add("  " + V.Str(e.Get("wound_id", "")) + " " + V.Str(e.Get("kind", "")) + "@" +
                          V.Str(e.Get("body_part", "")) + " sev=" + SurvivalCompat.FormatF(V.F64(e.Get("severity", 0.0)), 2) +
                          " [" + flags + "]");
            }
            return lines;
        }

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }
    }
}
