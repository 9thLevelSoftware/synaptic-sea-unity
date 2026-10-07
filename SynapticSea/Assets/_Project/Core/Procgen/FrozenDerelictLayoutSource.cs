using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>Explicit offline review seam. Captures verified raw pairs once; never generates or rerolls.
    /// Geometry, room state, exported hazards and loot belong to Rust; objectives and encounter markers belong to Unity.
    /// This source is not installed by RunSession or by the default ShipGenerator.</summary>
    public sealed class FrozenDerelictLayoutSource : IDerelictLayoutSource
    {
        public const string Root = "res://data/worldgen-fixtures/reviewed-export-parity/";
        public const string SourceCommit = "eb7845efffb9557e1e89352c534950c5d10d79b9";
        sealed class Pair { public long Seed; public string Archetype; public long Intactness; public string Layout, Gameplay; public GdDict Provenance; }
        readonly List<Pair> _pairs = new List<Pair>();
        public FrozenDerelictLayoutSource(IResourceReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            string manifestText = reader.ReadText(Root + "manifest.json");
            Require(manifestText != null && Hash(manifestText) == "7607eea310a8a98915df7872e87a0e57169c06083adb0d258319381eec3363bb", "reviewed manifest hash");
            GdDict manifest = Parse(manifestText);
            Require(manifest.GetString("schema_version") == "1" && manifest.GetString("source_commit") == SourceCommit &&
                manifest.GetInt("generator_version") == 4 && manifest.GetString("kit_id") == ShipGenerator.WORLDGEN_KIT_ID &&
                manifest.GetString("layout_schema") == "1.2.0" && manifest.GetString("gameplay_schema") == "1.1.0", "manifest identity");
            Require(manifest.Get("fixtures") is GdArray, "fixture list");
            var kitModules = new HashSet<string>();
            GdDict kit = Parse(reader.ReadText(ShipGenerator.WORLDGEN_KIT_PATH));
            Require(kit.Get("modules") is GdArray, "kit modules");
            foreach (object module in (GdArray)kit["modules"]) { Require(module is GdDict, "kit module"); kitModules.Add(((GdDict)module).GetString("module_id")); }
            var ids = new HashSet<string>(); var requests = new HashSet<string>();
            foreach (object raw in (GdArray)manifest["fixtures"])
            {
                Require(raw is GdDict, "fixture row"); var row = (GdDict)raw;
                string id = row.GetString("fixture_id");
                Require(id.Length > 0 && id.IndexOfAny(new[] { '/', '\\', '.' }) < 0 && ids.Add(id), "fixture identity");
                Require(long.TryParse(row.GetString("seed"), NumberStyles.None, CultureInfo.InvariantCulture, out long seed) && seed <= 9007199254740991L, "seed");
                long size = row.GetInt("size"), condition = row.GetInt("condition");
                Require((seed == 42 || seed == 777) && (size == 0 || size == 2) && (condition == 0 || condition == 2) && row.Has("size") && row.Has("condition") && ShipGenerator.WORLDGEN_ARCHETYPE_BY_SIZE.Has(size) && ShipGenerator.WORLDGEN_INTACTNESS_BY_CONDITION.Has(condition), "request parameters");
                string archetype = row.GetString("archetype_id"); long intactness = row.GetInt("intactness_bp");
                Require(archetype == V.Str(ShipGenerator.WORLDGEN_ARCHETYPE_BY_SIZE[size]) && intactness == V.I64(ShipGenerator.WORLDGEN_INTACTNESS_BY_CONDITION[condition]) && requests.Add(seed + ":" + archetype + ":" + intactness), "request identity");
                Require(row.GetString("layout_path") == id + "/layout.json" && row.GetString("gameplay_path") == id + "/gameplay_slice.json", "fixture paths");
                string layoutText = reader.ReadText(Root + row.GetString("layout_path")), gameplayText = reader.ReadText(Root + row.GetString("gameplay_path"));
                Require(layoutText != null && gameplayText != null && Hash(layoutText) == row.GetString("layout_sha256") && Hash(gameplayText) == row.GetString("gameplay_sha256"), "raw hashes");
                GdDict layout = Parse(layoutText), gameplay = Parse(gameplayText), generator = layout.GetDictOrEmpty("generator");
                string program = "worldgen-" + archetype + "-" + seed.ToString(CultureInfo.InvariantCulture);
                Require(layout.GetString("schema_version") == "1.2.0" && layout.GetString("document_kind") == "ship_layout" && gameplay.GetString("schema_version") == "1.1.0" && gameplay.GetString("document_kind") == "ship_gameplay_slice", "document schemas");
                Require(layout.GetString("program_id") == program && gameplay.GetString("program_id") == program && layout.GetString("kit_id") == ShipGenerator.WORLDGEN_KIT_ID && generator.GetInt("generator_version") == 4 && generator.GetInt("seed") == seed && generator.GetString("archetype_id") == archetype && generator.GetInt("intactness_bp") == intactness, "pair identity");
                Require(ValidateGeometry(layout, kitModules), "structural geometry");
                Require(ValidateAuthority(layout, gameplay), "unsupported authority records");
                _pairs.Add(new Pair { Seed = seed, Archetype = archetype, Intactness = intactness, Layout = layoutText, Gameplay = gameplayText,
                    Provenance = new GdDict { { "source_commit", SourceCommit }, { "generator_version", 4L }, { "fixture_id", id }, { "layout_sha256", Hash(layoutText) }, { "gameplay_sha256", Hash(gameplayText) }, { "objectives_authority", "unity_gameplay_slice_builder" }, { "encounters_authority", "unity_encounter_injector" }, { "geometry_hazards_loot_authority", "rust_export" } } });
            }
            Require(_pairs.Count == 8, "reviewed eight-pair matrix");
        }
        static GdDict Parse(string text) { var doc = text == null ? null : GdJson.ParseString(text) as GdDict; Require(doc != null, "JSON document"); return doc; }
        static void Require(bool ok, string reason) { if (!ok) throw new ArgumentException("Frozen worldgen rejected: " + reason); }
        public static string Hash(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        Pair Find(long seed, GdDict parameters) => _pairs.Find(p => p.Seed == seed && p.Archetype == parameters.GetString("archetype_id") && p.Intactness == parameters.GetInt("intactness_override"));
        public long GeneratorVersion() => 4;
        public string ExportLayoutJson(long seedValue, GdDict parameters, string kitId) => kitId == ShipGenerator.WORLDGEN_KIT_ID ? Find(seedValue, parameters)?.Layout ?? "" : "";
        public string ExportGameplaySliceJson(long seedValue, GdDict parameters) => Find(seedValue, parameters)?.Gameplay ?? "";
        internal GdDict Provenance(long seed, GdDict parameters) => Find(seed, parameters)?.Provenance.DeepCopy();

        static bool Integer(object value) { if (!(value is double) && !(value is long) && !(value is int)) return false; double n = V.F64(value); return !double.IsNaN(n) && !double.IsInfinity(n) && n == Math.Truncate(n); }
        internal static bool ValidateGeometry(GdDict layout, HashSet<string> modules)
        {
            GdDict plan = layout.GetDictOrEmpty("structural_plan");
            if (!new StructuralPlanValidator().Validate(plan, layout).GetBool("ok")) return false;
            var rooms = new HashSet<string>();
            foreach (object raw in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(raw is GdDict room) || !(room.Get("cells") is GdArray cells) || cells.IsEmpty || !rooms.Add(room.GetString("id"))) return false;
                foreach (object rawCell in cells) if (!(rawCell is GdArray cell) || cell.Count != 2 || !Integer(cell[0]) || !Integer(cell[1])) return false;
            }
            foreach (string key in new[] { "portals", "room_links", "vertical_connections" })
            {
                if (!(layout.Get(key) is GdArray links)) return false;
                var ids = new HashSet<string>();
                foreach (object raw in links)
                {
                    if (!(raw is GdDict link) || link.GetString("id").Length == 0 || !ids.Add(link.GetString("id")) || !rooms.Contains(link.GetString("from_room")) || !rooms.Contains(link.GetString("to_room"))) return false;
                }
            }
            foreach (string key in new[] { "placements", "floor_placements", "ceiling_placements" })
            {
                if (!(plan.Get(key) is GdArray placements)) return false;
                var ids = new HashSet<string>();
                foreach (object raw in placements)
                {
                    if (!(raw is GdDict row)) return false;
                    string id = row.GetString("placement_id", row.GetString("id"));
                    if (id.Length == 0 || !ids.Add(id)) return false;
                    string module = row.GetString("module_id");
                    if (module.Length == 0 ? row.GetBool("placement_required", true) : !modules.Contains(module)) return false;
                    if (!(row.Get("position") is GdArray position) || position.Count != 3) return false;
                    foreach (object coord in position) { if (!(coord is double) && !(coord is long) && !(coord is int)) return false; double n = V.F64(coord); if (double.IsNaN(n) || double.IsInfinity(n)) return false; }
                }
            }
            // These exports are runtime-hazard layouts. Additional authored encounter/blocked-link semantics
            // are outside this reviewed adapter; reject them rather than silently dropping or overwriting them.
            return layout.GetString("hazard_source") == "runtime" && layout.Get("blocked_links") is GdArray blocked && blocked.IsEmpty && layout.Get("encounters") is GdArray encounters && encounters.IsEmpty;
        }
        static bool CellInRoom(object rawCell, GdDict room)
        {
            if (!(rawCell is GdArray cell) || (cell.Count != 2 && cell.Count != 3) || !Integer(cell[0]) || !Integer(cell[1]) || (cell.Count == 3 && !Integer(cell[2]))) return false;
            long deck = room.GetInt("deck", 0L);
            if (cell.Count == 3 && V.I64(cell[2]) != deck) return false;
            foreach (object raw in room.GetArrayOrEmpty("cells"))
                if (raw is GdArray candidate && candidate.Count == 2 && V.I64(candidate[0]) == V.I64(cell[0]) && V.I64(candidate[1]) == V.I64(cell[1])) return true;
            return false;
        }
        internal static readonly string[] HazardKeys = { "fire_zones", "arc_zones", "breach_zones", "radiation_zones" };
        internal static bool ValidateAuthority(GdDict layout, GdDict gameplay)
        {
            if (!(layout.Get("rooms") is GdArray rooms) || rooms.IsEmpty) return false;
            var roomIds = new HashSet<string>(); var roomDocs = new Dictionary<string, GdDict>();
            foreach (object raw in rooms) { if (!(raw is GdDict room) || room.GetString("id").Length == 0 || !roomIds.Add(room.GetString("id"))) return false; roomDocs.Add(room.GetString("id"), room); }
            var ids = new HashSet<string>();
            foreach (string key in HazardKeys)
            {
                if (!(layout.Get(key) is GdArray zones)) return false;
                foreach (object raw in zones)
                {
                    if (!(raw is GdDict zone) || zone.GetString("id").Length == 0 || !ids.Add(zone.GetString("id")) || !roomIds.Contains(zone.GetString("from_room")) || !roomIds.Contains(zone.GetString("to_room")) || !CellInRoom(zone.Get("from_cell"), roomDocs[zone.GetString("from_room")]) || !CellInRoom(zone.Get("to_cell"), roomDocs[zone.GetString("to_room")])) return false;
                }
            }
            if (!(gameplay.Get("fire_zones") is GdArray fire) || GdJson.Stringify(fire) != GdJson.Stringify(layout["fire_zones"])) return false;
            if (!(gameplay.Get("loot_containers") is GdArray loot)) return false;
            var anchors = new HashSet<string>();
            foreach (object raw in loot)
            {
                if (!(raw is GdDict row) || row.GetString("id").Length == 0 || !ids.Add(row.GetString("id")) || !roomIds.Contains(row.GetString("room_id")) || !(row.Get("approach_cell") is GdArray cell) || cell.Count != 3 || !CellInRoom(cell, roomDocs[row.GetString("room_id")]) || !anchors.Add(row.GetString("room_id") + ":" + GdJson.Stringify(cell))) return false;
            }
            return true;
        }
        /// <summary>Precisely shared v4 adapter boundary: builder objectives survive, builder loot/hazards do not.
        /// No legacy same-cell suppression. Every exported identity and field survives, except declared loot table mapping.</summary>
        internal static bool ApplyAuthority(GdDict layout, GdDict gameplay, GdDict exported, GdDict tables)
        {
            if (!ValidateAuthority(layout, exported)) return false;
            var loot = new GdArray();
            foreach (object raw in (GdArray)exported["loot_containers"])
            {
                var row = ((GdDict)raw).DeepCopy(); string table = row.GetString("loot_table");
                if (table == "worldgen_seeded")
                {
                    switch (row.GetString("kind"))
                    {
                        case "cargo_crate": case "supply_crate": table = "salvage_cargo"; break;
                        case "parts_locker": case "tool_rack": table = "salvage_engineering"; break;
                        case "bridge_locker": case "footlocker": case "med_cabinet": case "food_locker": case "weapon_locker": case "ammo_crate": case "filter_cabinet": case "suit_locker": table = "generic_locker"; break;
                        default: return false;
                    }
                }
                if (!tables.Has(table)) return false;
                row["loot_table"] = table; loot.Append(row);
            }
            gameplay["loot_containers"] = loot;
            foreach (string key in HazardKeys) gameplay[key] = ((GdArray)layout[key]).DeepCopy();
            return true;
        }
    }
}
