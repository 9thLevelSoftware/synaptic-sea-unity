using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// Replays fixtures/godot/procgen_validation/pipeline_extras.json (Godot 4.7.1): LifeBoatBuilder layouts, the
    /// StartSceneBuilder and ShipGenerator data paths (hashes of the exact JSON texts Godot wrote for the loader),
    /// ComponentPlacementState populate/link/mount/dismount, PillarPersistence and a WorkActionDriver trace.
    /// </summary>
    public class ProcgenExtrasParityTests
    {
        const string ExtrasPath = "godot/procgen_validation/pipeline_extras.json";

        GdDict _expected;

        [SetUp]
        public void SetUp()
        {
            Fixtures.Require(ExtrasPath);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            _expected = Fixtures.ReadDict(ExtrasPath);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        static object Canon(object v)
        {
            switch (v)
            {
                case List<string> list: return GdString.ToGdArray(list);
                default: return ProcgenPipelineParityTests.Canon(V.Normalize(v));
            }
        }

        static void AssertSame(object expected, object actual, string where, bool intFloat = false)
        {
            var diffs = TreeDiff.Compare(expected, Canon(actual), new TreeDiff.Options { IntFloatEquivalent = intFloat });
            Assert.IsEmpty(diffs, where + ": " + TreeDiff.Format(diffs));
        }

        [Test]
        public void LifeBoatLayoutsMatchGodot()
        {
            GdDict lifeBoat = _expected.GetDict("life_boat");
            foreach (string biome in new[] { "", "breach_field", "dead_fleet" })
                AssertSame(lifeBoat[biome.Length == 0 ? "default" : biome], LifeBoatBuilder.BuildLayout(biome), "build_layout(" + biome + ")");
            AssertSame(lifeBoat["graph"], LifeBoatBuilder.BuildGraph().ToDict(), "build_graph");
        }

        [Test]
        public void StartSceneDataPathMatchesGodot()
        {
            GdDict archetype = CatalogRegistry.LoadDict(StartSceneBuilder.DERELICT_ARCHETYPE_PATH);
            foreach (GdDict c in _expected.GetArray("start_scene"))
            {
                long seed = c.GetInt("seed");
                ShipBlueprint blueprint = StartSceneBuilder.BuildBlueprint(archetype, seed);
                GdDict derelictLayout = new ShipLayoutGenerator().Generate(blueprint, archetype.DeepCopy());
                GdDict lbLayout = LifeBoatBuilder.BuildLayout();
                var sliceBuilder = new GameplaySliceBuilder();
                AssertSame(c["derelict_gameplay"], sliceBuilder.Build(derelictLayout), $"seed {seed} derelict gameplay");
                AssertSame(c["life_boat_gameplay"], sliceBuilder.Build(lbLayout), $"seed {seed} life boat gameplay");
                // module_damage amounts are 14-digit JSON floats where kernel GdFloatFormat can differ from Godot (see
                // ProcgenPipelineParityTests.KnownKernelFloatFormatTags): compare them at full precision and hash the rest.
                if (c.GetInt("derelict_layout_fnv1a") != SeedDeterminismContract.Fnv1a64(GdJson.Stringify(derelictLayout, "  ")))
                {
                    AssertSame(c["derelict_module_damage"], derelictLayout.Get("module_damage", new GdArray()), $"seed {seed} module_damage");
                    GdDict noDamage = derelictLayout.DeepCopy();
                    noDamage.Erase("module_damage");
                    Assert.AreEqual(c.GetInt("derelict_layout_no_damage_fnv1a"), SeedDeterminismContract.Fnv1a64(GdJson.Stringify(noDamage, "  ")), $"seed {seed} derelict layout text");
                }
                Assert.AreEqual(c.GetInt("life_boat_layout_fnv1a"), SeedDeterminismContract.Fnv1a64(GdJson.Stringify(lbLayout, "  ")), $"seed {seed} life boat layout text");
                Vec3 dock = StartSceneBuilder.FindDockPosition(derelictLayout);
                Vec3 lifeBoatPosition = dock + new Vec3(0.0, 0.0, StartSceneBuilder.DOCK_GAP);
                AssertSame(c["life_boat_position"], lifeBoatPosition, $"seed {seed} life boat position");
                // The legacy template pool has no dock zone, so Godot's build() fails here too.
                if (dock == Vec3.Inf) Assert.IsNull(StartSceneBuilder.Build(seed));
            }
        }

        [Test]
        public void ShipGeneratorDocumentsMatchGodot()
        {
            foreach (GdDict c in _expected.GetArray("ship_generator"))
            {
                var gen = new ShipGenerator();
                gen.ConfigureRunContext(c.GetString("biome"), c.GetString("difficulty"));
                ShipDocuments docs = c.GetString("biome").Length == 0 && c.GetString("difficulty").Length == 0
                    ? gen.GenerateFromSeed(c.GetInt("seed"), c.GetInt("size"), c.GetInt("condition"))
                    : gen.Generate(new ShipBlueprint(c.GetInt("size"), c.GetInt("condition"), c.GetInt("seed")));
                string where = $"seed {c.GetInt("seed")} {c.GetString("biome")}/{c.GetString("difficulty")}";
                Assert.IsNotNull(docs, where);
                Assert.IsTrue(docs.IsAway);
                Assert.AreEqual(c.GetString("kit_path"), docs.KitPath, where + " kit_path");
                Assert.IsFalse(docs.Kit.IsEmpty);
                Assert.AreEqual(c.GetInt("layout_json_length"), (long)docs.LayoutJson.Length, where + " layout.json length");
                if (c.GetInt("layout_json_fnv1a") != SeedDeterminismContract.Fnv1a64(docs.LayoutJson))
                {
                    // Kernel float formatting of module_damage amounts (see StartSceneDataPathMatchesGodot).
                    AssertSame(c["module_damage"], docs.SourceLayout.Get("module_damage", new GdArray()), where + " module_damage");
                    GdDict noDamage = docs.SourceLayout.DeepCopy();
                    noDamage.Erase("module_damage");
                    Assert.AreEqual(c.GetInt("layout_no_damage_fnv1a"), SeedDeterminismContract.Fnv1a64(GdJson.Stringify(noDamage, "  ")), where + " layout.json text");
                }
                Assert.AreEqual(c.GetInt("gameplay_json_fnv1a"), SeedDeterminismContract.Fnv1a64(docs.GameplaySliceJson), where + " gameplay_slice.json text");
                AssertSame(c["gameplay_slice"], docs.GameplaySlice, where + " gameplay slice", intFloat: true);
                Assert.IsTrue(V.VariantEquals(GdJson.ParseDict(docs.LayoutJson), docs.Layout), "Layout is the JSON round trip of LayoutJson");
            }
        }

        static GdDict LayoutByName(string name)
        {
            if (name.StartsWith("coherent_ship_")) return CatalogRegistry.LoadDict("res://data/procgen/golden/" + name + "/layout.json");
            string fp = $"godot/procgen/layout_{name}.fullprec.json";
            return GdJson.ParseDict(Fixtures.ReadText(Fixtures.Exists(fp) ? fp : $"godot/procgen/layout_{name}.json"));
        }

        [Test]
        public void ComponentPlacementMatchesGodot()
        {
            var cat = new ComponentCatalog();
            Assert.IsTrue(cat.LoadDefault());
            GdDict systemsDoc = CatalogRegistry.LoadDict("res://data/ship_systems/systems.json");
            Assert.IsNotNull(systemsDoc);
            foreach (GdDict c in _expected.GetArray("component_placement"))
            {
                string name = c.GetString("layout");
                long seed = c.GetInt("seed");
                var place = new ComponentPlacementState();
                long n = place.Populate(LayoutByName(name), cat, seed, new GdDict { { "dock_01|0|0", true } });
                var got = new GdDict
                {
                    { "layout", name }, { "seed", seed }, { "count", n },
                    { "occupancy_keys", GdString.ToGdArray(place.OccupancyKeys()) }, { "collisions", place.HasSlotCollisions() },
                };
                got["linked"] = place.LinkShipSystems(systemsDoc, cat);
                got["summary"] = place.GetSummary();
                got["fingerprint"] = place.Fingerprint();
                var ops = new GdArray();
                if (!place.Placed.IsEmpty)
                {
                    var first = ((GdDict)place.Placed[0]).DeepCopy();
                    string iid = first.GetString("component_instance_id");
                    string form = first.GetString("item_form"), room = first.GetString("room_id"), kind = first.GetString("slot_kind");
                    long idx = first.GetInt("slot_index");
                    var inv = new GdDict();
                    ops.Append(place.Dismount(iid));
                    ops.Append(place.Dismount(iid));
                    ops.Append(place.Mount(form, room, kind, idx, inv, cat));
                    inv[form] = 2L;
                    ops.Append(place.Mount("wrong_form", room, kind, idx, new GdDict { { "wrong_form", 1L } }, cat));
                    ops.Append(place.Mount(form, room, kind, idx, inv, cat));
                    ops.Append(place.Mount(form, "fresh_room", "center", 7, inv, cat));
                    ops.Append(place.Mount(form, "fresh_room", "center", 8, inv, null));
                    ops.Append(inv);
                    ops.Append(GdArray.Of(place.MountedCount(), place.DismountedCount(), place.IsMounted(iid), place.FindIndex("nope")));
                }
                got["ops"] = ops;
                got["summary_after_ops"] = place.GetSummary();
                AssertSame(c, got, $"{name} seed {seed}");
            }
        }

        [Test]
        public void PillarPersistenceMatchesGodot()
        {
            var map = new ModuleIntegrityMap();
            map.EnsureModule("eng/wall_a", "wall_straight_1x1", new GdDict(), "eng");
            map.ApplyDamage("eng/wall_a", 0.45, "wall_straight_1x1");
            map.ApplyDamage("cargo/floor_2", 0.9, "floor_1x1");
            var cat = new ComponentCatalog();
            cat.LoadDefault();
            var place = new ComponentPlacementState();
            place.Populate(CatalogRegistry.LoadDict("res://data/procgen/golden/coherent_ship_001/layout.json"), cat, 5);
            var waCat = new WorkActionCatalog();
            waCat.LoadDefault();
            var work = new WorkActionState();
            work.ConfigureAction("pry_panel", waCat.GetAction("pry_panel"));
            work.Start("panel_1", new GdDict { { "tool_class", "prybar" }, { "skill_id", "salvage" }, { "skill_level", 0L }, { "inventory", new GdDict() } });
            work.Tick(1.0, new GdDict());
            GdDict bundle = PillarPersistence.PackAll(map, place, work);
            PillarPersistence.UnpackedPillars unpacked = PillarPersistence.UnpackAll(bundle);
            var got = new GdDict
            {
                { "bundle", bundle },
                { "repacked", PillarPersistence.PackAll(unpacked.ModuleIntegrity, unpacked.ComponentPlacement, unpacked.WorkAction) },
                { "null_bundle", PillarPersistence.PackAll(null, null, null) },
                { "idle_work", PillarPersistence.PackWorkAction(new WorkActionState()) },
                { "unpacked_idle", PillarPersistence.UnpackWorkAction(new GdDict { { "active", false } }).GetSummary() },
                { "sanitized", PillarPersistence.SanitizeHistorical(new GdDict { { "module_integrity_summary", 5L }, { "other", GdArray.Of(1L) } }) },
            };
            AssertSame(_expected["pillar_persistence"], got, "pillar_persistence");
        }

        static GdDict StripCtx(string skillId, GdDict inventory, string tool = "welding_lance") => new GdDict
        {
            { "tool_class", tool }, { "skill_id", skillId }, { "skill_level", 0L }, { "inventory", inventory },
        };

        [Test]
        public void WorkActionDriverTraceMatchesGodot()
        {
            var trace = new GdArray();
            var driver = new WorkActionDriver();
            driver.Configure(new GdDict { { "cart_capacity", 100.0 }, { "cart_mass", 0.0 } });
            var map = new ModuleIntegrityMap();
            map.EnsureModule("eng/wall_a", "wall_straight_1x1", new GdDict(), "eng");
            var inv = new GdDict();
            trace.Append(GdArray.Of("start", driver.StartAction("cut_wall", "eng/wall_a", StripCtx("salvage", inv)), driver.GetStatus()));
            trace.Append(GdArray.Of("tick", driver.Tick(0.5, new GdDict()), driver.ProgressRatio(), driver.LastProgressNoise, driver.LastNoisePulse));
            trace.Append(GdArray.Of("tick", driver.Tick(0.7, new GdDict()), driver.ProgressRatio(), driver.LastProgressNoise, driver.LastNoisePulse));
            trace.Append(GdArray.Of("tick", driver.Tick(0.1, new GdDict { { "damaged", true } }), driver.GetStatus()));
            trace.Append(GdArray.Of("complete", driver.Complete(map, inv)));
            driver.Reset();
            trace.Append(GdArray.Of("start", driver.StartAction("cut_wall", "eng/wall_a", StripCtx("salvage", new GdDict())), driver.GetStatus()));
            trace.Append(GdArray.Of("tick", driver.Tick(20.0, new GdDict { { "work_speed_mult", 1.0 } }), driver.ProgressRatio()));
            trace.Append(GdArray.Of("complete", driver.Complete(map, inv), inv.DeepCopy(), map.GetState("eng/wall_a"), driver.LastNoisePulse, driver.LastXpEvent, driver.CartMass, driver.Overloaded));
            var det = new DetectionState();
            det.Configure(new GdDict { { "noise_level", 0.1 } });
            var dictTarget = new GdDict { { "player_noise", 0.2 } };
            trace.Append(GdArray.Of("noise", driver.ApplyNoiseToDetection(det), det.NoiseLevel, driver.ApplyNoiseToDetection(dictTarget), dictTarget));
            trace.Append(GdArray.Of("targets", driver.ListTargets("cut", map, null)));
            driver.CartMass = 200.0;
            driver.Overloaded = true;
            trace.Append(GdArray.Of("start_overloaded", driver.StartAction("cut_wall", "eng/wall_b", StripCtx("salvage", new GdDict()))));
            map.EnsureModule("eng/wall_b", "wall_straight_1x1", new GdDict(), "eng");
            map.ApplyDamage("eng/wall_b", 0.4, "wall_straight_1x1");
            trace.Append(GdArray.Of("start_weld", driver.StartAction("weld_patch", "eng/wall_b", StripCtx("repair", new GdDict { { "hull_plate", 1L } }))));
            trace.Append(GdArray.Of("tick", driver.Tick(20.0, new GdDict())));
            var inv2 = new GdDict { { "hull_plate", 1L } };
            trace.Append(GdArray.Of("complete_weld", driver.Complete(map, inv2), inv2, map.GetState("eng/wall_b"), driver.CartMass));
            driver.Reset();
            driver.Configure(new GdDict());
            trace.Append(GdArray.Of("start_pry", driver.StartAction("pry_panel", "panel_1", StripCtx("salvage", new GdDict(), "prybar"))));
            trace.Append(GdArray.Of("tick", driver.Tick(1.0, new GdDict()), driver.ProgressRatio(), driver.LastProgressNoise));
            trace.Append(GdArray.Of("tick", driver.Tick(0.4, new GdDict()), driver.LastProgressNoise, driver.LastNoisePulse));
            GdDict snap = driver.GetPersistenceSummary();
            var d2 = new WorkActionDriver();
            d2.Configure(new GdDict());
            trace.Append(GdArray.Of("persist", snap, d2.ApplyPersistenceSummary(snap), d2.GetStatus(), d2.ProgressRatio()));
            trace.Append(GdArray.Of("context", driver.BuildContext("prybar", "salvage", 2, new GdDict { { "scrap_metal", 3L } }, null, "", true)));
            trace.Append(GdArray.Of("complete_not_done", driver.Complete(null, new GdDict())));
            driver.Reset();
            trace.Append(GdArray.Of("complete_no_work", driver.Complete(null, new GdDict()), driver.GetStatus(), driver.IsWorking()));
            trace.Append(GdArray.Of("unknown_action", driver.StartAction("no_such_action", "x", new GdDict())));
            AssertSame(_expected["work_action_driver"], trace, "work_action_driver trace");
        }
    }
}
