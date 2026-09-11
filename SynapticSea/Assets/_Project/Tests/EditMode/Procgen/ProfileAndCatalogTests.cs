using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// BiomeProfile / DifficultyProfile / KitCatalog / ModularSocketCatalog / ModularAssetSpec / TopologyTemplate,
    /// following biome_profile_smoke, difficulty_profile_smoke, kit_catalog_smoke, hive_biomatter_kit_smoke and
    /// topology_template_smoke.
    /// </summary>
    public class ProfileAndCatalogTests
    {
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _log = new CollectingLog();
            CoreServices.Log = _log;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void Biome_FromFile_RoundTripsAndSelectsDeterministically()
        {
            var abyssal = BiomeProfile.FromFile("res://data/procgen/biomes/abyssal_synaptic_sea.json");
            Assert.IsNotNull(abyssal);
            Assert.AreEqual("abyssal_synaptic_sea", abyssal.Id);
            Assert.AreEqual(1.0, abyssal.Modifier(BiomeProfile.DIAL_HAZARD));
            Assert.AreEqual(1.0, abyssal.Modifier("unknown_dial"));

            var copy = BiomeProfile.FromDict(abyssal.ToDict());
            Assert.IsTrue(V.VariantEquals(abyssal.ToDict(), copy.ToDict()));

            var breach = BiomeProfile.FromDict(new GdDict
            {
                { "id", "breach_field" }, { "hazard_overrides", new GdDict { { "oxygen_breach", 1.6 } } },
            });
            Assert.AreEqual(1.6, breach.HazardOverride("oxygen_breach"));
            Assert.AreEqual(1.0, breach.HazardOverride("unknown_hazard"));
            Assert.AreEqual("unknown", BiomeProfile.FromDict(new GdDict()).Id);

            var pool = new List<string> { "abyssal_synaptic_sea", "breach_field", "dead_fleet" };
            string pick = BiomeProfile.SelectBiome(314, pool);
            Assert.AreEqual(pick, BiomeProfile.SelectBiome(314, pool));
            CollectionAssert.Contains(pool, pick);
        }

        [Test]
        public void Difficulty_ResolvesPresets_AndCombinedModifierClamps()
        {
            var deep = DifficultyProfile.ForId(DifficultyProfile.DEEP_DIVE_ID);
            Assert.AreEqual(1.7, deep.Modifier(DifficultyProfile.DIAL_HAZARD));
            Assert.AreEqual(1.0, deep.Modifier("unknown_dial"));
            Assert.That(DifficultyProfile.ForId("hardened").HazardModifier,
                Is.GreaterThan(DifficultyProfile.ForId("standard").HazardModifier));
            Assert.AreEqual("standard", DifficultyProfile.ResolveDict("").GetString("id"));
            Assert.AreEqual(DifficultyProfile.STANDARD_ID, DifficultyProfile.FromDict(new GdDict()).Id);

            var breach = BiomeProfile.FromDict(new GdDict { { "id", "breach_field" }, { "hazard_modifier", 1.4 } });
            Assert.AreEqual(2.38, DifficultyProfile.CombinedModifier(breach, deep, DifficultyProfile.DIAL_HAZARD), 0.01);
            var extreme = BiomeProfile.FromDict(new GdDict { { "id", "extreme" }, { "hazard_modifier", 5.0 } });
            Assert.AreEqual(3.0, DifficultyProfile.CombinedModifier(extreme, deep, DifficultyProfile.DIAL_HAZARD));
            Assert.AreEqual(1.7, DifficultyProfile.CombinedModifier(null, deep, DifficultyProfile.DIAL_HAZARD), 1e-12);

            var ids = new List<string> { "standard", "hardened", "deep_dive" };
            string pick = DifficultyProfile.SelectDifficulty(314, ids);
            Assert.AreEqual(pick, DifficultyProfile.SelectDifficulty(314, ids));
            CollectionAssert.Contains(ids, pick);
            Assert.IsEmpty(_log.Warnings);
        }

        [Test]
        public void KitCatalog_LoadsKits_AndResolvesRoles()
        {
            var catalog = new KitCatalog();
            long loaded = catalog.Configure("res://data/kits/");
            Assert.That(loaded, Is.GreaterThanOrEqualTo(3));
            Assert.AreEqual(KitCatalog.DEFAULT_KIT_ID, catalog.DefaultKitId());
            Assert.IsTrue(catalog.IsLoaded(catalog.DefaultKitId()));
            Assert.IsFalse(catalog.IsLoaded("nonexistent_kit"));

            Assert.That(catalog.KitsForRole("airlock"), Is.Not.Empty);
            Assert.That(catalog.KitsForRole("not_a_real_role"), Is.Not.Empty);
            Assert.That(catalog.KitsForRole("engineering", "breach_field"), Is.Not.Empty);
            Assert.IsTrue(catalog.HasRoleFor("airlock"));
            Assert.IsFalse(catalog.HasRoleFor("not_a_real_role"));
            Assert.IsNotEmpty(catalog.ModuleIdForRole(catalog.DefaultKitId(), "airlock"));

            List<string> ids = catalog.LoadedKitIds();
            var sorted = new List<string>(ids);
            sorted.Sort(System.StringComparer.Ordinal);
            CollectionAssert.AreEqual(sorted, ids);
            CollectionAssert.Contains(ids, catalog.DefaultKitId());
        }

        [Test]
        public void SocketCatalog_FallsBackToV0_AndAppliesWallJoinAliases()
        {
            var catalog = new ModularSocketCatalog();
            Assert.IsTrue(catalog.LoadKit("ship_structural_biomatter"));
            Assert.AreEqual(ModularSocketCatalog.DEFAULT_KIT_ID, catalog.KitId);
            Assert.IsFalse(catalog.Modules.IsEmpty);

            GdDict SocketOfKind(string moduleId, string kind)
            {
                foreach (var s in catalog.SocketsOf(moduleId))
                    if (s is GdDict d && d.GetString("kind") == kind) return d;
                return new GdDict();
            }

            var innerA = SocketOfKind("wall_inner_corner", "inner_corner_vertex");
            var wallEnd = SocketOfKind("wall_straight_1x1", "wall_end");
            var portal = SocketOfKind("pressure_door_1x1", "portal_edge");
            Assert.IsFalse(innerA.IsEmpty || wallEnd.IsEmpty || portal.IsEmpty);
            Assert.IsFalse(catalog.SocketsCompatible(innerA, innerA.DeepCopy()));
            Assert.IsTrue(catalog.SocketsCompatible(wallEnd, wallEnd.DeepCopy()));
            Assert.IsTrue(catalog.SocketsCompatible(wallEnd, portal));
            Assert.IsTrue(catalog.SocketsCompatible(innerA, wallEnd));
            Assert.AreEqual("floor_1x1", catalog.ChooseModule(GdArray.Of("floor_top"), "floor_1x1"));
        }

        [Test]
        public void SocketCatalog_WorldSocketPosition_RotatesAboutUp()
        {
            var catalog = new ModularSocketCatalog();
            var placement = new Vec3(8f, 4f, 12f);
            Vec3 w0 = catalog.WorldSocketPosition(placement, 0.0, new Vec3(2f, 0.5f, 0f));
            Assert.AreEqual(new Vec3(10f, 4.5f, 12f), w0);

            // Godot: Vector3(2, 0, 0).rotated(UP, deg_to_rad(90)) = (-8.742278e-08, 0, -2) in float32.
            Vec3 r90 = ProcgenMath.RotatedUpDegrees(new Vec3(2f, 0f, 0f), 90.0);
            Assert.AreEqual(-2f, r90.Z);
            Assert.AreEqual(0f, r90.Y);
            Assert.AreEqual(-8.742278e-08f, r90.X, 1e-13f);
            Vec3 w90 = catalog.WorldSocketPosition(placement, 90.0, new Vec3(2f, 0f, 0f));
            Assert.IsTrue(catalog.PositionsAgree(w90, new Vec3(8f, 4f, 10f)));
            Assert.IsFalse(catalog.PositionsAgree(w90, new Vec3(10f, 4f, 12f)));
        }

        [Test]
        public void ModularAssetSpec_BuildsFromContractJson()
        {
            GdDict contract = CatalogRegistry.LoadDict(
                "res://data/placement/contracts/structural/ship_structural_v0/floor_1x1_contract.json");
            var spec = ModularAssetSpec.FromDict(contract);
            Assert.AreEqual("floor_1x1", spec.ModuleId);
            Assert.AreEqual("floor", spec.ModuleFamily);
            Assert.AreEqual(4.0, spec.GridStepM);
            CollectionAssert.AreEqual(new List<long> { 1, 1 }, spec.FootprintCells);
            Assert.That(spec.Sockets.Count, Is.GreaterThan(0));
            Assert.IsTrue(V.VariantEquals(spec.ToDict(), ModularAssetSpec.FromDict(spec.ToDict()).ToDict()));
        }

        [Test]
        public void TopologyTemplate_ParsesZonesConnectionsAndDeckConfig()
        {
            var template = TopologyTemplate.FromDict(CatalogRegistry.LoadDict("res://data/procgen/templates/stacked_v2.json"));
            Assert.AreEqual("stacked_v2", template.Id);
            Assert.AreEqual(3L, template.DeckConfig["max_decks"]);
            Assert.AreEqual(1.0, template.DeckConfig["vertical_transition_probability"]);
            Assert.AreEqual(2L, template.GetZone("upper_hub")["deck"]);
            Assert.IsTrue(template.GetZone("nope").IsEmpty);
            Assert.AreEqual(3, template.GetZonesAttachedTo("service_corridor").Count);
            Assert.AreEqual("adjacent", template.Connections[0]["distribution"]);
            Assert.IsTrue(template.GetZone("service_room")["count"] is GdArray);
        }
    }
}
