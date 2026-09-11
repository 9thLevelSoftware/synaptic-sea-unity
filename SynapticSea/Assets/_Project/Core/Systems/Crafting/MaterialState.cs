// Ported from scripts/systems/material_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure model for material inventory with per-stack quality tracking.
    /// Materials are also items (shared IDs with item_definitions.json).
    /// This model tracks quality per material ID; quantities live in InventoryState.
    /// Never touches the scene tree.
    /// </summary>
    public sealed class MaterialState : IStatusLineProvider
    {
        public const string MATERIAL_DEFINITIONS_PATH = "res://data/materials/material_definitions.json";

        GdDict _definitions = new GdDict();
        readonly GdDict _materialQuality = new GdDict(); // material_id -> quality float [0.0, 1.0]
        bool _loaded;

        public MaterialState()
        {
            LoadDefinitions();
        }

        /// <summary>GDScript <c>_loaded</c>.</summary>
        public bool Loaded => _loaded;

        void LoadDefinitions()
        {
            if (!CatalogRegistry.Exists(MATERIAL_DEFINITIONS_PATH)) return;
            if (CatalogRegistry.Load(MATERIAL_DEFINITIONS_PATH) is GdDict root)
            {
                if (root.Get("materials", new GdDict()) is GdDict mats) _definitions = mats;
            }
            _loaded = true;
        }

        public bool HasDefinition(string materialId) => _definitions.Has(materialId);

        public GdDict GetDefinition(string materialId)
        {
            object def = _definitions.Get(materialId, new GdDict());
            return def as GdDict ?? new GdDict();
        }

        public string GetDisplayName(string materialId)
        {
            GdDict def = GetDefinition(materialId);
            string name = V.Str(def.Get("display_name", ""));
            return name.Length != 0 ? name : ItemsCompat.Capitalize(materialId.Replace("_", " "));
        }

        public string GetCategory(string materialId) => V.Str(GetDefinition(materialId).Get("category", ""));

        public double GetWeight(string materialId) => V.F64(GetDefinition(materialId).Get("weight", 0.0));

        public long GetMaxStack(string materialId) => V.I64(GetDefinition(materialId).Get("max_stack", 99L));

        public double GetBaseQuality(string materialId) =>
            GdMath.Clampf(V.F64(GetDefinition(materialId).Get("base_quality", 0.5)), 0.0, 1.0);

        public GdArray GetAllMaterialIds()
        {
            var ids = new GdArray(_definitions.Keys);
            GdSort.Sort(ids);
            return ids;
        }

        public long CountDefined() => _definitions.Count;

        // --- quality tracking ---

        public void SetQuality(string materialId, double quality)
        {
            if (string.IsNullOrEmpty(materialId)) return;
            _materialQuality[materialId] = GdMath.Clampf(quality, 0.0, 1.0);
        }

        public double GetQuality(string materialId) =>
            GdMath.Clampf(V.F64(_materialQuality.Get(materialId, GetBaseQuality(materialId))), 0.0, 1.0);

        public void RemoveQuality(string materialId) => _materialQuality.Erase(materialId);

        public void Reset()
        {
            _materialQuality.Clear();
            LoadDefinitions();
        }

        /// <summary>
        /// Given an ingredients dict {material_id: qty}, returns the average quality of those materials
        /// weighted by quantity. Falls back to base_quality if no per-stack quality is set.
        /// </summary>
        public double AverageIngredientQuality(GdDict ingredients)
        {
            if (ingredients == null || ingredients.IsEmpty) return 0.5;
            long totalQty = 0;
            double weighted = 0.0;
            foreach (var kv in ingredients)
            {
                long qty = Math.Max(1L, V.I64(kv.Value));
                double q = GetQuality(V.Str(kv.Key));
                weighted += q * (double)qty;
                totalQty += qty;
            }
            if (totalQty <= 0) return 0.5;
            return GdMath.Clampf(weighted / (double)totalQty, 0.0, 1.0);
        }

        // --- save/load ---

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "material_quality", _materialQuality.ShallowCopy() },
                { "defined_count", CountDefined() },
            };
        }

        /// <summary>Returns true when any quality changed, like the GDScript.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            if (summary.Get("material_quality", new GdDict()) is GdDict mq)
            {
                foreach (var kv in mq)
                {
                    double newQ = GdMath.Clampf(V.F64(kv.Value), 0.0, 1.0);
                    double oldQ = GetQuality(V.Str(kv.Key));
                    if (Math.Abs(newQ - oldQ) > 0.001)
                    {
                        _materialQuality[V.Str(kv.Key)] = newQ;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            var ids = new List<object>(_materialQuality.Keys);
            GdSort.Sort(ids);
            foreach (object matId in ids)
                lines.Add(GetDisplayName(V.Str(matId)) + " q=" + ItemsCompat.Fmt(GetQuality(V.Str(matId)), 2));
            return lines;
        }
    }
}
