// Ported from scripts/systems/class_definition.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// One player class: starting skill levels and per-category XP multipliers.
    /// Pure data; loaded from data/player/classes.json.
    /// </summary>
    public class ClassDefinition
    {
        public const string DefaultClassesPath = "res://data/player/classes.json";

        public string ClassId = "";
        public string DisplayName = "";
        public string Description = "";
        public GdDict StartingSkills = new GdDict();  // skill_id -> int
        public GdDict XpMultipliers = new GdDict();   // category -> float
        public bool Unlockable = false;               // Domain 6: base classes false; earned classes true

        public static ClassDefinition FromDict(GdDict d)
        {
            var c = new ClassDefinition();
            c.ClassId = V.Str(d.Get("class_id", ""));
            c.DisplayName = V.Str(d.Get("name", ""));
            c.Description = V.Str(d.Get("description", ""));
            if (d.Get("starting_skills", new GdDict()) is GdDict skills)
            {
                foreach (var kv in skills) c.StartingSkills[V.Str(kv.Key)] = V.I64(kv.Value);
            }
            if (d.Get("xp_multipliers", new GdDict()) is GdDict mults)
            {
                foreach (var kv in mults) c.XpMultipliers[V.Str(kv.Key)] = V.F64(kv.Value);
            }
            c.Unlockable = V.Bool(d.Get("unlockable", false));
            return c;
        }

        /// <summary>Returns { class_id -> ClassDefinition } (insertion-ordered; never removed from). Empty on a malformed file.</summary>
        public static Dictionary<string, ClassDefinition> LoadAll(string path = DefaultClassesPath)
        {
            var output = new Dictionary<string, ClassDefinition>();
            object parsed = CatalogRegistry.Load(path);
            if (!(parsed is GdDict root)) return output;
            object classesVariant = root.Get("classes", new GdArray());
            if (!(classesVariant is GdArray classes)) return output;
            foreach (var entry in classes)
            {
                if (!(entry is GdDict entryDict)) continue;
                ClassDefinition c = FromDict(entryDict);
                if (c.ClassId.Length != 0) output[c.ClassId] = c;
            }
            return output;
        }

        public double XpMultiplier(string category) => V.F64(XpMultipliers.Get(category, 1.0));
    }
}
