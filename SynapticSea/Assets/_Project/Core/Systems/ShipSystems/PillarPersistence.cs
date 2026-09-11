// Ported from scripts/systems/pillar_persistence.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-D8: pure pack/unpack for the pre-polish pillar models that need save survival: ModuleIntegrityMap,
    /// ComponentPlacementState and in-progress WorkActionState. Nested under RunSnapshot fields; empty defaults keep
    /// historical fixtures loadable.
    /// </summary>
    public static class PillarPersistence
    {
        /// <summary>
        /// GDScript <c>unpack_all()</c> returns a Dictionary of model objects, which a GdDict cannot hold; the keys map
        /// 1:1 onto these fields.
        /// </summary>
        public sealed class UnpackedPillars
        {
            public ModuleIntegrityMap ModuleIntegrity;
            public ComponentPlacementState ComponentPlacement;
            public WorkActionState WorkAction;
        }

        public static GdDict PackModuleIntegrity(ModuleIntegrityMap map)
        {
            if (map == null) return new GdDict { { "schema", "module_integrity_v1" }, { "deltas", new GdArray() } };
            GdArray deltas = map.ToSparseDeltas() ?? new GdArray();
            return new GdDict { { "schema", "module_integrity_v1" }, { "deltas", deltas } };
        }

        public static ModuleIntegrityMap UnpackModuleIntegrity(GdDict summary)
        {
            var map = new ModuleIntegrityMap();
            if (summary == null || summary.IsEmpty) return map;
            if (summary.Get("deltas", new GdArray()) is GdArray deltas) map.ApplySparseDeltas(deltas);
            return map;
        }

        public static GdDict PackComponentPlacement(ComponentPlacementState placement)
        {
            if (placement == null)
                return new GdDict { { "schema", "component_placement_v1" }, { "seed", 0L }, { "count", 0L }, { "placed", new GdArray() } };
            GdDict s = placement.GetSummary();
            s["schema"] = "component_placement_v1";
            return s;
        }

        public static ComponentPlacementState UnpackComponentPlacement(GdDict summary)
        {
            var place = new ComponentPlacementState();
            if (summary == null || summary.IsEmpty) return place;
            place.ApplySummary(summary);
            return place;
        }

        public static GdDict PackWorkAction(WorkActionState work)
        {
            if (work == null) return new GdDict { { "schema", "work_action_v1" }, { "active", false } };
            GdDict s = work.GetSummary();
            string status = V.Str(s.Get("status", "idle"));
            return new GdDict
            {
                { "schema", "work_action_v1" },
                { "active", status == "active" || status == "interrupted" },
                { "summary", s },
            };
        }

        public static WorkActionState UnpackWorkAction(GdDict summary)
        {
            var work = new WorkActionState();
            if (summary == null || summary.IsEmpty || !V.Bool(summary.Get("active", false))) return work;
            if (summary.Get("summary", new GdDict()) is GdDict inner) work.ApplySummary(inner);
            return work;
        }

        /// <summary>Bundle for RunSnapshot / ship instance attachment.</summary>
        public static GdDict PackAll(ModuleIntegrityMap map, ComponentPlacementState placement, WorkActionState work) => new GdDict
        {
            { "schema", "pillar_persistence_v1" },
            { "module_integrity", PackModuleIntegrity(map) },
            { "component_placement", PackComponentPlacement(placement) },
            { "work_action", PackWorkAction(work) },
        };

        public static UnpackedPillars UnpackAll(GdDict bundle) => new UnpackedPillars
        {
            ModuleIntegrity = UnpackModuleIntegrity(Dict(bundle.Get("module_integrity", new GdDict()))),
            ComponentPlacement = UnpackComponentPlacement(Dict(bundle.Get("component_placement", new GdDict()))),
            WorkAction = UnpackWorkAction(Dict(bundle.Get("work_action", new GdDict()))),
        };

        static GdDict Dict(object v) => v as GdDict ?? new GdDict();

        /// <summary>Fuzz: tolerate missing pillar fields (historical fixtures); garbage non-dict values become {}.</summary>
        public static GdDict SanitizeHistorical(GdDict raw)
        {
            GdDict output = raw.DeepCopy();
            // Always ensure pillar keys exist as empty dicts for loaders.
            if (!output.Has("module_integrity_summary")) output["module_integrity_summary"] = new GdDict();
            if (!output.Has("component_placement_summary")) output["component_placement_summary"] = new GdDict();
            if (!output.Has("work_action_summary")) output["work_action_summary"] = new GdDict();
            if (!output.Has("ship_modification_summary")) output["ship_modification_summary"] = new GdDict();
            foreach (string k in new[] { "module_integrity_summary", "component_placement_summary", "work_action_summary", "ship_modification_summary" })
            {
                if (!(output.Get(k, null) is GdDict)) output[k] = new GdDict();
            }
            return output;
        }
    }
}
