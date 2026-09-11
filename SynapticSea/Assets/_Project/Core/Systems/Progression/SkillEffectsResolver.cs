// Ported from scripts/systems/skill_effects_resolver.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The duck-typed <c>get_skill_level(skill_id)</c> seam <see cref="SkillEffectsResolver"/> reads
    /// (PlayerProgressionState implements it in its port).
    /// </summary>
    public interface ISkillLevelSource
    {
        long GetSkillLevel(string skillId);
    }

    /// <summary>
    /// PKG-D7: pure skill -> gameplay effect consumers. Every catalog skill maps to live multipliers
    /// (work speed, craft quality, salvage yield, heal, travel, scan, ...).
    /// </summary>
    public class SkillEffectsResolver
    {
        public const string DefaultPath = "res://data/player/skill_effects.json";
        public const string SkillsPath = "res://data/player/skills.json";

        GdDict _effects = new GdDict();     // skill_id -> effect row
        GdDict _classKits = new GdDict();   // class_id -> kit hooks
        bool _loaded = false;

        public bool LoadDefault() => LoadFile(DefaultPath);

        public bool LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !InfraCompat.FileExists(path, null)) return false;
            object parsed = InfraCompat.LoadJson(path, null);
            if (!(parsed is GdDict root)) return false;
            object eff = root.Get("effects", new GdDict());
            object kits = root.Get("class_kit_hooks", new GdDict());
            if (!(eff is GdDict effDict)) return false;
            _effects = effDict.DeepCopy();
            if (kits is GdDict kitsDict) _classKits = kitsDict.DeepCopy();
            _loaded = !_effects.IsEmpty;
            return _loaded;
        }

        public bool IsLoaded() => _loaded;

        public int EffectCount() => _effects.Count;

        public bool HasEffect(string skillId) => _effects.Has(skillId);

        public GdDict GetEffect(string skillId)
        {
            if (!_effects.Has(skillId)) return new GdDict();
            return ((GdDict)_effects[skillId]).DeepCopy();
        }

        public List<string> ConsumersFor(string skillId)
        {
            GdDict e = GetEffect(skillId);
            object raw = e.Get("consumers", new GdArray());
            var output = new List<string>();
            if (raw is GdArray arr)
            {
                foreach (var c in arr) output.Add(V.Str(c));
            }
            return output;
        }

        /// <summary>Audit: every skill in skills.json must have a non-empty consumers list.</summary>
        public GdDict AuditCatalogCoverage(string skillsCatalogPath = SkillsPath)
        {
            var report = new GdDict
            {
                { "ok", false },
                { "catalog_count", 0L },
                { "covered", 0L },
                { "missing", new GdArray() },
                { "emit_only", new GdArray() },
            };
            if (!InfraCompat.FileExists(skillsCatalogPath, null))
            {
                report["missing"] = GdArray.Of("catalog_file");
                return report;
            }
            object parsed = InfraCompat.LoadJson(skillsCatalogPath, null);
            if (!(parsed is GdDict root)) return report;
            object skillsV = root.Get("skills", new GdArray());
            if (!(skillsV is GdArray skills)) return report;
            var missing = new GdArray();
            var emitOnly = new GdArray();
            long covered = 0;
            foreach (var entry in skills)
            {
                if (!(entry is GdDict entryDict)) continue;
                string sid = V.Str(entryDict.Get("skill_id", ""));
                if (sid.Length == 0) continue;
                report["catalog_count"] = V.I64(report["catalog_count"]) + 1;
                if (!_effects.Has(sid))
                {
                    missing.Add(sid);
                    continue;
                }
                List<string> cons = ConsumersFor(sid);
                if (cons.Count == 0)
                {
                    emitOnly.Add(sid);
                    continue;
                }
                covered += 1;
            }
            report["covered"] = covered;
            report["missing"] = missing;
            report["emit_only"] = emitOnly;
            report["ok"] = missing.IsEmpty && emitOnly.IsEmpty && covered >= 22;
            return report;
        }

        /// <summary>
        /// Skill level from a progression object: an <see cref="ISkillLevelSource"/>, or a dictionary carrying a
        /// <c>skills</c> map (Godot's <c>progression.get("skills")</c> branch). Clamped at 0.
        /// </summary>
        static long LevelOf(object progression, string skillId)
        {
            if (progression == null) return 0;
            if (progression is ISkillLevelSource source) return Math.Max(0L, source.GetSkillLevel(skillId));
            if (progression is GdDict d && d.Get("skills") is GdDict skills)
                return Math.Max(0L, V.I64(skills.Get(skillId, 0L)));
            return 0;
        }

        double PerLevel(string skillId, string key)
        {
            GdDict e = GetEffect(skillId);
            return V.F64(e.Get(key, 0.0));
        }

        /// <summary>WorkAction work_speed_mult for a verb (and optional primary skill).</summary>
        public double WorkSpeedMultiplier(object progression, string verb = "", string primarySkill = "", string classId = "")
        {
            double mult = 1.0;
            string verbL = (verb ?? "").ToLowerInvariant();
            foreach (var sidKey in _effects.Keys)
            {
                string sid = V.Str(sidKey);
                var e = (GdDict)_effects[sidKey];
                object verbsV = e.Get("work_verbs", new GdArray());
                bool matches = false;
                if (!string.IsNullOrEmpty(primarySkill) && sid == primarySkill)
                {
                    matches = true;
                }
                else if (verbsV is GdArray verbs)
                {
                    foreach (var v in verbs)
                    {
                        if (V.Str(v) == verbL)
                        {
                            matches = true;
                            break;
                        }
                    }
                }
                if (!matches) continue;
                double per = V.F64(e.Get("work_speed_per_level", 0.0));
                if (per <= 0.0) continue;
                long lvl = LevelOf(progression, sid);
                mult += per * (double)lvl;
            }
            mult += ClassFlat(classId, "work_speed_flat");
            return GdMath.Clampf(mult, 0.25, 3.0);
        }

        public double CraftQualityBonus(object progression, string skillId = "fabrication", string classId = "")
        {
            double bonus = 0.0;
            foreach (string sid in new[] { skillId, "fabrication", "welding", "welding_mastery", "cooking", "repair" })
            {
                double per = PerLevel(sid, "craft_quality_bonus_per_level");
                if (per <= 0.0) continue;
                bonus += per * (double)LevelOf(progression, sid);
            }
            bonus += ClassFlat(classId, "craft_quality_flat");
            return GdMath.Clampf(bonus, 0.0, 0.75);
        }

        public double CraftSpeedMultiplier(object progression, string skillId = "fabrication")
        {
            double mult = 1.0;
            foreach (string sid in new[] { skillId, "fabrication", "cooking" })
            {
                double per = PerLevel(sid, "craft_speed_per_level");
                mult += per * (double)LevelOf(progression, sid);
            }
            return GdMath.Clampf(mult, 0.5, 2.5);
        }

        public double SalvageYieldMultiplier(object progression, string classId = "")
        {
            double mult = 1.0;
            foreach (string sid in new[] { "scavenging", "welding", "welding_mastery" })
            {
                double per = PerLevel(sid, "salvage_yield_per_level");
                mult += per * (double)LevelOf(progression, sid);
            }
            mult += ClassFlat(classId, "salvage_yield_flat");
            return GdMath.Clampf(mult, 0.5, 2.5);
        }

        /// <summary>Higher skill means shorter duration (divide seconds by the factor).</summary>
        public double RepairDurationFactor(object progression)
        {
            double factor = 1.0;
            double per = PerLevel("repair", "repair_duration_factor_per_level");
            factor += per * (double)LevelOf(progression, "repair");
            double perC = PerLevel("construction", "module_repair_per_level");
            factor += perC * (double)LevelOf(progression, "construction") * 0.5;
            return GdMath.Clampf(factor, 1.0, 3.0);
        }

        public double HealMultiplier(object progression, string classId = "")
        {
            double mult = 1.0;
            foreach (string sid in new[] { "first_aid", "surgery" })
            {
                double per = PerLevel(sid, "heal_mult_per_level");
                mult += per * (double)LevelOf(progression, sid);
            }
            mult += ClassFlat(classId, "heal_mult_flat");
            return GdMath.Clampf(mult, 1.0, 2.5);
        }

        public double WoundTreatBonus(object progression)
        {
            double bonus = 0.0;
            foreach (string sid in new[] { "first_aid", "surgery" })
                bonus += PerLevel(sid, "wound_treat_bonus_per_level") * (double)LevelOf(progression, sid);
            return GdMath.Clampf(bonus, 0.0, 0.8);
        }

        /// <summary>&lt;1.0 saves fuel.</summary>
        public double TravelFuelMultiplier(object progression)
        {
            double save = 0.0;
            foreach (string sid in new[] { "piloting", "astrogation" })
                save += PerLevel(sid, "travel_fuel_save_per_level") * (double)LevelOf(progression, sid);
            return GdMath.Clampf(1.0 - save, 0.5, 1.0);
        }

        public double TravelFoodMultiplier(object progression)
        {
            double save = PerLevel("resource_management", "travel_food_save_per_level") * (double)LevelOf(progression, "resource_management");
            return GdMath.Clampf(1.0 - save, 0.5, 1.0);
        }

        public double ScanDetailBonus(object progression, string classId = "")
        {
            double bonus = 0.0;
            foreach (string sid in new[] { "scanner_operation", "signal_analysis", "diagnostics", "astrogation", "comms", "biomatter_diagnostics" })
                bonus += PerLevel(sid, "scan_detail_bonus_per_level") * (double)LevelOf(progression, sid);
            bonus += ClassFlat(classId, "scan_detail_flat");
            return GdMath.Clampf(bonus, 0.0, 2.0);
        }

        public double InfectionResist(object progression)
        {
            return GdMath.Clampf(PerLevel("quarantine", "infection_resist_per_level") * (double)LevelOf(progression, "quarantine"), 0.0, 0.8);
        }

        public double XpMultiplier(object progression)
        {
            return GdMath.Clampf(1.0 + PerLevel("leadership", "xp_mult_per_level") * (double)LevelOf(progression, "leadership"), 1.0, 1.5);
        }

        double ClassFlat(string classId, string key)
        {
            if (string.IsNullOrEmpty(classId)) return 0.0;
            object kit = _classKits.Get(classId, _classKits.Get("default", new GdDict()));
            if (!(kit is GdDict kitDict)) return 0.0;
            return V.F64(kitDict.Get(key, 0.0));
        }

        /// <summary>WorkAction context fragment from progression + verb.</summary>
        public GdDict BuildWorkContext(object progression, string verb, string primarySkill = "", string classId = "")
        {
            long skillLevel = 0;
            if (!string.IsNullOrEmpty(primarySkill))
            {
                skillLevel = LevelOf(progression, primarySkill);
            }
            else if (!string.IsNullOrEmpty(verb))
            {
                // Best matching skill among verb bindings.
                foreach (var sidKey in _effects.Keys)
                {
                    object verbsV = ((GdDict)_effects[sidKey]).Get("work_verbs", new GdArray());
                    if (verbsV is GdArray verbs)
                    {
                        foreach (var v in verbs)
                        {
                            if (V.Str(v) == verb) skillLevel = Math.Max(skillLevel, LevelOf(progression, V.Str(sidKey)));
                        }
                    }
                }
            }
            return new GdDict
            {
                { "skill_level", skillLevel },
                { "work_speed_mult", WorkSpeedMultiplier(progression, verb, primarySkill, classId) },
                { "salvage_yield_mult", SalvageYieldMultiplier(progression, classId) },
            };
        }

        /// <summary>Apply the craft quality bonus into a 0..1 material quality base.</summary>
        public double ApplyCraftQuality(double baseQuality, object progression, string skillId = "fabrication", string classId = "")
        {
            return GdMath.Clampf(baseQuality + CraftQualityBonus(progression, skillId, classId), 0.0, 1.0);
        }
    }
}
