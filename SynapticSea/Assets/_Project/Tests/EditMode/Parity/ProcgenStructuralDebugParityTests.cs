using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// Replays scripts/validation/procgen_structural_debug_export.gd for the seeds captured in
    /// fixtures/godot/procgen/structural_debug/seed_&lt;n&gt;/: SMALL/WRECKED blueprint with room_count_range (5, 8), the
    /// exporter's derelict_a archetype, generate_with_options(..., "", "", true), then the exporter's input
    /// canonicalization (sorted rooms/cells/portals), compile + validate, and its sorted documents
    /// (topology, occupancy, edge_map, placements, validation). The exporter's sorting is re-implemented here; its
    /// files are written without full precision, so floats compare within 1e-12.
    /// </summary>
    public class ProcgenStructuralDebugParityTests
    {
        public static IEnumerable<long> Seeds() => new long[] { 42, 777, 999, 7777 };

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        static object Canon(object v) => ProcgenPipelineParityTests.Canon(v);

        static bool StrLess(string a, string b) => V.CompareCodePoints(a, b) < 0;

        static string RecordSortKey(object value)
        {
            if (!(value is GdDict record)) return V.Str(value);
            string primary = "";
            foreach (string field in new[] { "placement_id", "edge_key", "cell_key", "id", "key", "room_id" })
            {
                if (record.Has(field))
                {
                    primary = field + "|" + V.Str(record[field]);
                    break;
                }
            }
            return primary + "|" + GdJson.Stringify(Canon(record), "");
        }

        static void SortRecords(GdArray records) => records.SortCustom((a, b) => StrLess(RecordSortKey(a), RecordSortKey(b)));

        static GdArray SortedRecords(object raw)
        {
            var records = new GdArray();
            if (raw is GdDict dict)
            {
                var keys = new List<string>();
                foreach (var k in dict.Keys) keys.Add(V.Str(k));
                GdString.SortStrings(keys);
                foreach (string key in keys)
                {
                    if (!(dict[key] is GdDict r)) continue;
                    GdDict record = r.DeepCopy();
                    if (!record.Has("edge_key") && key.Contains("|")) record["edge_key"] = key;
                    records.Append(record);
                }
            }
            else if (raw is GdArray arr)
            {
                foreach (var r in arr)
                    if (r is GdDict rd) records.Append(rd.DeepCopy());
            }
            SortRecords(records);
            return records;
        }

        static GdArray SortedOccupancy(object raw)
        {
            var records = new GdArray();
            if (raw is GdDict dict)
            {
                var keys = new List<string>();
                foreach (var k in dict.Keys) keys.Add(V.Str(k));
                GdString.SortStrings(keys);
                foreach (string key in keys)
                {
                    if (!(dict[key] is GdDict r)) continue;
                    GdDict record = r.DeepCopy();
                    record["cell_key"] = key;
                    records.Append(record);
                }
            }
            return SortedRecords(records);
        }

        static GdArray SortedStrings(object raw)
        {
            var values = new List<string>();
            if (raw is GdArray arr) foreach (var v in arr) values.Add(V.Str(v));
            GdString.SortStrings(values);
            return GdString.ToGdArray(values);
        }

        static string CellSortKey(object value) =>
            value is Vec2i c ? GdString.FormatInt(c.X) + "|" + GdString.FormatInt(c.Y) : GdJson.Stringify(Canon(value), "");

        static void SwapFieldPair(GdDict container, string first, string second)
        {
            if (!container.Has(first) && !container.Has(second)) return;
            object a = container.Get(first, null);
            container[first] = container.Get(second, null);
            container[second] = a;
        }

        static GdDict CanonicalizePortalRecord(GdDict source)
        {
            GdDict portal = source.DeepCopy();
            string fromRoom = V.Str(portal.Get("from_room", portal.Get("room_a", "")));
            string toRoom = V.Str(portal.Get("to_room", portal.Get("room_b", "")));
            if (fromRoom.Length != 0 && toRoom.Length != 0 && StrLess(toRoom, fromRoom))
            {
                if (portal.Has("from_room") || portal.Has("to_room"))
                {
                    portal["from_room"] = toRoom;
                    portal["to_room"] = fromRoom;
                }
                if (portal.Has("room_a") || portal.Has("room_b"))
                {
                    portal["room_a"] = toRoom;
                    portal["room_b"] = fromRoom;
                }
                SwapFieldPair(portal, "from_cell", "to_cell");
                SwapFieldPair(portal, "from_direction", "to_direction");
                if (portal.Get("source_cells", null) is GdArray sc && sc.Count == 2) portal["source_cells"] = GdArray.Of(sc[1], sc[0]);
                if (portal.Has("from_cell")) portal["cell"] = portal["from_cell"];
                else if (portal.Get("source_cells", null) is GdArray sc2 && sc2.Count == 2) portal["cell"] = sc2[0];
                if (portal.Has("direction") && portal.Has("opposite_direction"))
                {
                    object direction = portal["direction"];
                    portal["direction"] = portal["opposite_direction"];
                    portal["opposite_direction"] = direction;
                }
                else if (portal.Has("normal_direction") && portal.Has("opposite_direction"))
                {
                    object normal = portal["normal_direction"];
                    portal["normal_direction"] = portal["opposite_direction"];
                    portal["opposite_direction"] = normal;
                }
            }
            return portal;
        }

        static GdDict CanonicalizeLayoutInput(GdDict source)
        {
            GdDict layout = source.DeepCopy();
            if (layout.Get("rooms", null) is GdArray rawRooms)
            {
                var rooms = new GdArray();
                foreach (var roomVariant in rawRooms)
                {
                    if (!(roomVariant is GdDict r)) continue;
                    GdDict room = r.DeepCopy();
                    var cells = new GdArray();
                    if (room.Get("cells", new GdArray()) is GdArray rawCells) foreach (var c in rawCells) cells.Append(c);
                    cells.SortCustom((a, b) => StrLess(CellSortKey(a), CellSortKey(b)));
                    room["cells"] = cells;
                    rooms.Append(room);
                }
                SortRecords(rooms);
                layout["rooms"] = rooms;
            }
            if (layout.Get("portals", null) is GdArray rawPortals)
            {
                var portals = new GdArray();
                foreach (var p in rawPortals)
                    if (p is GdDict pd) portals.Append(CanonicalizePortalRecord(pd));
                SortRecords(portals);
                layout["portals"] = portals;
            }
            return layout;
        }

        static GdDict TopologyDocument(GdDict layout, long seed)
        {
            GdDict topology = layout.DeepCopy();
            topology["schema_version"] = "1.0.0";
            topology["document_kind"] = "procgen_structural_topology";
            topology["seed"] = seed;
            foreach (string field in new[]
                     {
                         "rooms", "portals", "adjacency_intents", "room_links", "structural_room_links", "arc_zones",
                         "blocked_links", "breach_zones", "encounters", "fire_zones", "landmarks", "vertical_connections",
                     })
            {
                if (topology.Get(field, null) is GdArray arr) topology[field] = SortedRecords(arr);
            }
            return topology;
        }

        [TestCaseSource(nameof(Seeds))]
        public void StructuralDebugDocumentsMatchGodot(long seed)
        {
            string dir = $"godot/procgen/structural_debug/seed_{seed}";
            Fixtures.Require(dir + "/validation.json");

            var blueprint = new ShipBlueprint(ShipBlueprint.Size.Small, ShipBlueprint.Condition.Wrecked, seed)
            {
                RoomCountRange = new Vec2i(5, 8),
            };
            var archetype = new GdDict
            {
                { "name", "Derelict" },
                { "type", "derelict" },
                { "template", "derelict_a" },
                { "guaranteed_roles", new GdArray() },
                { "role_weights", new GdDict() },
                { "max_duplicates", 3L },
            };
            GdDict generated = new ShipLayoutGenerator().GenerateWithOptions(blueprint, archetype, "", "", true);
            Assert.IsFalse(generated.IsEmpty);
            GdDict layout = CanonicalizeLayoutInput(generated);
            long roomCount = layout.GetArrayOrEmpty("rooms").Count;
            Assert.That(roomCount, Is.InRange(5, 8));

            GdDict plan = new StructuralEdgeCompiler().Compile(layout);
            GdDict verdict = new StructuralPlanValidator().Validate(plan, layout);

            var documents = new Dictionary<string, GdDict>
            {
                ["topology.json"] = TopologyDocument(layout, seed),
                ["occupancy.json"] = new GdDict
                {
                    { "schema_version", "1.0.0" },
                    { "document_kind", "procgen_structural_occupancy" },
                    { "seed", seed },
                    { "occupancy", SortedOccupancy(plan.Get("occupancy", new GdDict())) },
                },
                ["edge_map.json"] = new GdDict
                {
                    { "schema_version", "1.0.0" },
                    { "document_kind", "procgen_structural_edge_map" },
                    { "seed", seed },
                    { "edges", SortedRecords(plan.Get("edges", new GdDict())) },
                },
                ["placements.json"] = new GdDict
                {
                    { "schema_version", "1.0.0" },
                    { "document_kind", "procgen_structural_placements" },
                    { "seed", seed },
                    { "placements", SortedRecords(plan.Get("placements", new GdArray())) },
                    { "floor_placements", SortedRecords(plan.Get("floor_placements", new GdArray())) },
                },
                ["validation.json"] = new GdDict
                {
                    { "schema_version", "1.0.0" },
                    { "document_kind", "procgen_structural_validation" },
                    { "seed", seed },
                    { "ok", verdict.GetBool("ok") },
                    { "errors", SortedStrings(verdict.Get("errors", new GdArray())) },
                    { "compiler_errors", SortedStrings(plan.Get("errors", new GdArray())) },
                    { "stats", verdict.Get("stats", new GdDict()) },
                },
            };

            foreach (var kv in documents)
            {
                // The exporter writes JSON.stringify(v, "", false): 14-15 significant digits, so floats compare to 1e-12.
                var diffs = TreeDiff.Compare(Fixtures.ReadDict(dir + "/" + kv.Key), Canon(kv.Value), new TreeDiff.Options { FloatTolerance = 1e-12 });
                Assert.IsEmpty(diffs, $"seed {seed} {kv.Key}: " + TreeDiff.Format(diffs));
            }
        }
    }
}
