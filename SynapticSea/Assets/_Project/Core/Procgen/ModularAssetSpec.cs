// Ported from scripts/placement/modular_asset_spec.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Plain data twin of the Godot <c>ModularAssetSpec</c> Resource (<c>@export</c> fields). Godot loads the
    /// <c>*_contract.tres</c>; the port builds it from the matching <c>*_contract.json</c> via <see cref="FromDict"/>.
    /// </summary>
    public sealed class ModularAssetSpec
    {
        public string SchemaVersion = "1.0.0";
        public string DocumentKind = "modular_asset_spec";
        public string AssetId = "";
        public string ModuleId = "";
        public string Category = "";
        public string KitId = "";
        public string ModuleFamily = "";
        public double GridStepM = 4.0;

        /// <summary><c>Array[int]</c>.</summary>
        public List<long> FootprintCells = new List<long>();

        public GdDict Bounds = new GdDict();

        /// <summary><c>Array[Dictionary]</c>.</summary>
        public List<GdDict> Sockets = new List<GdDict>();

        public GdDict Collision = new GdDict();
        public GdDict Provenance = new GdDict();
        public string SourceAssetPath = "";
        public string WrapperScene = "";
        public string ContractPath = "";
        public string InspectionPath = "";
        public GdDict Asset = new GdDict();

        /// <summary>
        /// Builds a spec from a parsed contract JSON (keys equal the exported property names). Missing or
        /// wrongly typed keys keep the Resource defaults, as an unset <c>@export</c> would. Nested dictionaries
        /// are deep-copied; JSON numbers inside them stay doubles (the .tres twin stores some as ints).
        /// </summary>
        public static ModularAssetSpec FromDict(GdDict data)
        {
            var spec = new ModularAssetSpec();
            if (data == null) return spec;
            if (data.Get("schema_version") is string schema) spec.SchemaVersion = schema;
            if (data.Get("document_kind") is string kind) spec.DocumentKind = kind;
            if (data.Get("asset_id") is string assetId) spec.AssetId = assetId;
            if (data.Get("module_id") is string moduleId) spec.ModuleId = moduleId;
            if (data.Get("category") is string category) spec.Category = category;
            if (data.Get("kit_id") is string kitId) spec.KitId = kitId;
            if (data.Get("module_family") is string family) spec.ModuleFamily = family;
            if (V.IsNumber(data.Get("grid_step_m"))) spec.GridStepM = V.F64(data.Get("grid_step_m"));
            if (data.Get("footprint_cells") is GdArray footprint)
            {
                foreach (var item in footprint) spec.FootprintCells.Add(V.I64(item));
            }
            if (data.Get("bounds") is GdDict bounds) spec.Bounds = bounds.DeepCopy();
            if (data.Get("sockets") is GdArray sockets)
            {
                foreach (var item in sockets)
                    if (item is GdDict socket) spec.Sockets.Add(socket.DeepCopy());
            }
            if (data.Get("collision") is GdDict collision) spec.Collision = collision.DeepCopy();
            if (data.Get("provenance") is GdDict provenance) spec.Provenance = provenance.DeepCopy();
            if (data.Get("source_asset_path") is string source) spec.SourceAssetPath = source;
            if (data.Get("wrapper_scene") is string wrapper) spec.WrapperScene = wrapper;
            if (data.Get("contract_path") is string contract) spec.ContractPath = contract;
            if (data.Get("inspection_path") is string inspection) spec.InspectionPath = inspection;
            if (data.Get("asset") is GdDict asset) spec.Asset = asset.DeepCopy();
            return spec;
        }

        /// <summary>The exported properties as a dictionary, in declaration order.</summary>
        public GdDict ToDict()
        {
            var footprint = new GdArray();
            foreach (long cell in FootprintCells) footprint.Add(cell);
            var sockets = new GdArray();
            foreach (var socket in Sockets) sockets.Add(socket.DeepCopy());
            return new GdDict
            {
                { "schema_version", SchemaVersion },
                { "document_kind", DocumentKind },
                { "asset_id", AssetId },
                { "module_id", ModuleId },
                { "category", Category },
                { "kit_id", KitId },
                { "module_family", ModuleFamily },
                { "grid_step_m", GridStepM },
                { "footprint_cells", footprint },
                { "bounds", Bounds.DeepCopy() },
                { "sockets", sockets },
                { "collision", Collision.DeepCopy() },
                { "provenance", Provenance.DeepCopy() },
                { "source_asset_path", SourceAssetPath },
                { "wrapper_scene", WrapperScene },
                { "contract_path", ContractPath },
                { "inspection_path", InspectionPath },
                { "asset", Asset.DeepCopy() },
            };
        }
    }
}
