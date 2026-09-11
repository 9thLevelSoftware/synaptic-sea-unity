using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Records the scene effects the Core resolvers request on one structural wrapper.</summary>
    internal sealed class FakeModuleSceneView : IModuleSceneView
    {
        public readonly HashSet<string> Visuals = new HashSet<string>();
        public readonly Dictionary<string, bool> Visible = new Dictionary<string, bool>();
        public readonly Dictionary<string, object> Meta = new Dictionary<string, object>();
        public readonly List<string> Tinted = new List<string>();
        public bool? CollisionEnabled;
        public bool Group = true;

        public FakeModuleSceneView(string key, params string[] visuals)
        {
            ModuleKey = key;
            foreach (string v in visuals)
            {
                Visuals.Add(v);
                Visible[v] = true;
            }
        }

        public string ModuleKey { get; }
        public bool HasVisualGroup => Group;
        public bool HasVisual(string childName) => Group && Visuals.Contains(childName);
        public void SetVisualVisible(string childName, bool visible) => Visible[childName] = visible;
        public void TintMeshes(string childName, double r, double g, double b, double a) => Tinted.Add(childName ?? "<wrapper>");
        public void SetCollisionEnabled(bool enabled) => CollisionEnabled = enabled;
        public void SetMeta(string key, object value) => Meta[key] = value;
    }

    public class ModuleIntegrityConsequencesTests
    {
        static GdDict Layout() => new GdDict
        {
            {
                "rooms", GdArray.Of(
                    new GdDict
                    {
                        { "id", "eng_1" },
                        { "room_role", "engineering" },
                        {
                            "structural_placements", GdArray.Of(
                                new GdDict { { "module_id", "wall_straight_1x1" }, { "name", "wall_a" } },
                                new GdDict { { "module_id", "floor_1x1" }, { "name", "floor_a" } })
                        },
                    },
                    new GdDict
                    {
                        { "id", "br_1" },
                        { "room_role", "bridge" },
                        { "structural_placements", GdArray.Of(new GdDict { { "module_id", "bulkhead_portal_2x1" }, { "name", "portal_a" } }) },
                    })
            },
        };

        static readonly GdDict Roles = new GdDict { { "engineering", "engineering" }, { "bridge", "bridge" } };

        [Test]
        public void ConsequenceTable_MatchesStates()
        {
            Assert.IsTrue(ModuleIntegrityConsequences.ConsequenceForState("intact").GetBool("collision_enabled"));
            GdDict dest = ModuleIntegrityConsequences.ConsequenceForState("destroyed");
            Assert.IsFalse(dest.GetBool("collision_enabled", true));
            Assert.IsTrue(dest.GetBool("atmosphere_link") && dest.GetBool("nav_gap"));
            Assert.IsTrue(ModuleIntegrityConsequences.ConsequenceForState("breached").GetBool("crawl_passable"));
            Assert.AreEqual("", ModuleIntegrityConsequences.ConsequenceForState("bogus").GetString("mesh_suffix"));
            foreach (string kind in new[] { "floor_1x1", "corridor_floor_1x1", "pillar_support_1x1", "ramp_up_1x2", "wall_straight_1x1", "doorway_frame_open_1x1" })
                Assert.IsTrue(ModuleIntegrityConsequences.IsStructuralKind(kind), kind);
            Assert.IsFalse(ModuleIntegrityConsequences.IsWallKind("floor_1x1"));
        }

        [Test]
        public void FireDamage_BreachesWallsAndPrefersCompiledKeys()
        {
            var map = new ModuleIntegrityMap();
            Assert.GreaterOrEqual(ModuleIntegrityConsequences.SeedMapFromLayout(map, Layout()), 2);
            Assert.IsFalse(map.HasModule("eng_1/floor_a"), "floor should not be wall-seeded");
            var burning = new GdDict { { "engineering", 1.0 } };
            var changed = new List<string>();
            for (int i = 0; i < 40; i++)
                foreach (object mid in ModuleIntegrityConsequences.ApplyFireDamage(map, Layout(), burning, Roles, 0.5, 0.2))
                    if (!changed.Contains(V.Str(mid))) changed.Add(V.Str(mid));
            CollectionAssert.Contains(changed, "eng_1/wall_a");
            Assert.AreNotEqual("intact", map.GetState("eng_1/wall_a"));
            Assert.AreEqual("intact", map.GetState("br_1/portal_a"), "bridge is not burning");
            Assert.GreaterOrEqual(ModuleIntegrityConsequences.DerivedBreachCount(map), 1L);

            // Shared compiled wall takes the hotter compartment regardless of room order.
            var shared = new GdDict
            {
                { "rooms", GdArray.Of(new GdDict { { "id", "eng_1" }, { "room_role", "engineering" } }, new GdDict { { "id", "br_1" }, { "room_role", "bridge" } }) },
                {
                    "structural_plan", new GdDict
                    {
                        { "placements", GdArray.Of(new GdDict { { "module_id", "wall_straight_1x1" }, { "edge_key", "shared|v|0|0" }, { "room_id", "eng_1" }, { "room_ids", GdArray.Of("eng_1", "br_1") } }) },
                        { "floor_placements", new GdArray() },
                        { "ceiling_placements", new GdArray() },
                    }
                },
            };
            var sharedMap = new ModuleIntegrityMap();
            Assert.AreEqual(1L, ModuleIntegrityConsequences.SeedMapFromCompiledLayout(sharedMap, shared));
            CollectionAssert.AreEqual(new[] { "eng_1", "br_1" }, sharedMap.GetModule("edge/shared|v|0|0").OwnerRooms);
            GdArray hit = ModuleIntegrityConsequences.ApplyFireDamage(sharedMap, shared, new GdDict { { "engineering", 0.1 }, { "bridge", 1.0 } }, Roles, 1.0, 1.0);
            CollectionAssert.AreEqual(new object[] { "edge/shared|v|0|0" }, hit);
            Assert.LessOrEqual(sharedMap.GetModule("edge/shared|v|0|0").Integrity, 0.05);
        }

        [Test]
        public void ApplyToNode_DestroyedDropsCollisionAndTintsLegacyOnly()
        {
            var legacy = new FakeModuleSceneView("eng_1/wall_a") { Group = false };
            ModuleIntegrityConsequences.ApplyToNode(legacy, "destroyed");
            Assert.AreEqual(true, legacy.Meta["nav_gap"]);
            Assert.AreEqual("_destroyed", legacy.Meta["mesh_suffix"]);
            Assert.AreEqual(false, legacy.CollisionEnabled);
            CollectionAssert.AreEqual(new[] { "<wrapper>" }, legacy.Tinted);

            var variant = new FakeModuleSceneView("eng_1/wall_b", IntegrityVisualResolver.VISUAL_INTACT, IntegrityVisualResolver.VISUAL_DAMAGED);
            ModuleIntegrityConsequences.ApplyToNode(variant, "damaged");
            Assert.IsEmpty(variant.Tinted, "variant wrappers are not tinted");
            Assert.AreEqual(true, variant.CollisionEnabled);
        }
    }

    public class IntegrityVisualResolverTests
    {
        sealed class FakeShip : IShipModuleScene
        {
            public readonly List<IModuleSceneView> Views = new List<IModuleSceneView>();
            public IEnumerable<IModuleSceneView> StructuralModuleViews() => Views;
        }

        [Test]
        public void VariantWrapper_ShowsOnlyRequestedState()
        {
            var w = new FakeModuleSceneView("m", IntegrityVisualResolver.VISUAL_INTACT, IntegrityVisualResolver.VISUAL_DAMAGED, IntegrityVisualResolver.VISUAL_BREACHED);
            foreach (string state in new[] { "intact", "damaged", "breached" })
            {
                Assert.IsTrue(IntegrityVisualResolver.ApplyVisualState(w, state), state);
                int visible = 0;
                foreach (var kv in w.Visible) if (kv.Value) visible++;
                Assert.AreEqual(1, visible, state);
            }
            Assert.IsTrue(w.Visible[IntegrityVisualResolver.VISUAL_BREACHED]);
            Assert.IsTrue(IntegrityVisualResolver.ApplyVisualState(w, "destroyed"));
            CollectionAssert.DoesNotContain(w.Visible.Values, true);

            var partial = new FakeModuleSceneView("p", IntegrityVisualResolver.VISUAL_INTACT);
            Assert.IsFalse(IntegrityVisualResolver.ApplyVisualState(partial, "breached"), "missing variant is not applied");
            var legacy = new FakeModuleSceneView("l", IntegrityVisualResolver.VISUAL_LEGACY);
            Assert.IsTrue(IntegrityVisualResolver.ApplyVisualState(legacy, "destroyed"));
            Assert.IsFalse(legacy.Visible[IntegrityVisualResolver.VISUAL_LEGACY]);
            CollectionAssert.AreEqual(new[] { IntegrityVisualResolver.VISUAL_LEGACY }, legacy.Tinted);
        }

        [Test]
        public void ApplyToShip_UsesMapStatePerModuleKey()
        {
            var map = new ModuleIntegrityMap();
            map.EnsureModule("room/wall_a", "wall_straight_1x1");
            map.ApplyDamage("room/wall_a", 0.7, "wall_straight_1x1"); // 0.3 -> breached
            var ship = new FakeShip();
            var a = new FakeModuleSceneView("room/wall_a", IntegrityVisualResolver.VISUAL_INTACT, IntegrityVisualResolver.VISUAL_BREACHED);
            var b = new FakeModuleSceneView("room/wall_b", IntegrityVisualResolver.VISUAL_INTACT);
            var none = new FakeModuleSceneView("room/prop") { Group = false };
            ship.Views.Add(a);
            ship.Views.Add(b);
            ship.Views.Add(none);
            Assert.AreEqual(2L, IntegrityVisualResolver.ApplyToShip(ship, map));
            Assert.IsTrue(a.Visible[IntegrityVisualResolver.VISUAL_BREACHED]);
            Assert.IsFalse(a.Visible[IntegrityVisualResolver.VISUAL_INTACT]);
            Assert.IsTrue(b.Visible[IntegrityVisualResolver.VISUAL_INTACT], "unregistered module reads intact");
        }
    }
}
