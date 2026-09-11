using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// Loads the Godot loader parity cases (fixtures/godot/loader, captured from GeneratedShipLoader) through
    /// <see cref="ShipSceneBuilder"/> and compares the <c>ship_loaded</c> summary, every getter, and the scene nodes
    /// (converted back to Godot's frame through <see cref="Frame"/>) with a 1e-4 tolerance on positions.
    /// </summary>
    public class ShipSceneBuilderTests
    {
        GameObject _parent;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            _parent = new GameObject("ShipSceneBuilderTests");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_parent);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            LogAssert.ignoreFailingMessages = false;
        }

        static IEnumerable<string> Cases => LoaderParity.Cases;

        ShipSceneBuilder NewBuilder() => ShipSceneBuilder.Create(_parent.transform);

        [TestCaseSource(nameof(Cases))]
        public void LoadMatchesGodotLoader(string caseName)
        {
            Fixtures.Require($"{LoaderParity.Dir}/{caseName}.json");
            GdDict fixture = LoaderParity.Fixture(caseName);
            GdDict inputs = fixture.GetDict("inputs");
            var builder = NewBuilder();
            GdDict emitted = null;
            string failure = "";
            builder.ShipLoaded += s => emitted = s;
            builder.LoadFailed += r => failure = r;

            bool loaded = builder.LoadFromPaths(LoaderParity.InputPath(inputs.GetString("layout")), LoaderParity.InputPath(inputs.GetString("kit")),
                LoaderParity.InputPath(inputs.GetString("gameplay_slice")), fixture.GetBool("is_away"));
            Assert.IsTrue(loaded, failure);
            Assert.IsNotNull(emitted, "ShipLoaded must be raised synchronously before LoadFromPaths returns");
            Assert.AreSame(emitted, builder.View.Summary);

            // Summary paths are absolute in both engines; compare their repo-relative tails.
            GdDict fixtureSummary = fixture.GetDict("summary");
            foreach (string key in new[] { "layout_path", "kit_path", "gameplay_slice_path" })
                StringAssert.EndsWith(fixtureSummary.GetString(key), emitted.GetString(key), key);

            GdDict actual = Record(builder.View, caseName, loaded, fixture.GetBool("is_away"), failure, emitted);
            var diffs = TreeDiff.Compare(fixture, actual, LoaderParity.Options("inputs", "layout_path", "kit_path", "gameplay_slice_path"));
            Assert.IsEmpty(diffs, TreeDiff.Format(diffs));
        }

        [Test]
        public void FailuresMatchGodotReasons()
        {
            Fixtures.Require($"{LoaderParity.Dir}/failures.json");
            LogAssert.ignoreFailingMessages = true;
            var cases = Fixtures.ReadDict($"{LoaderParity.Dir}/failures.json").GetArray("cases");
            GdDict layout = LoaderParity.LoadInput("data/procgen/golden/coherent_ship_001/layout.json");
            GdDict gameplay = LoaderParity.LoadInput("data/procgen/golden/coherent_ship_001/gameplay_slice.json");
            GdDict kit = LoaderParity.LoadInput(LoaderParity.Kit);
            Assert.AreEqual(8, cases.Count);
            foreach (GdDict expected in cases)
            {
                string name = expected.GetString("case");
                GdDict l = layout.DeepCopy(), g = gameplay.DeepCopy(), k = kit.DeepCopy();
                switch (name)
                {
                    case "layout_missing_rooms": l["rooms"] = new GdDict(); break;
                    case "layout_missing_prototype": l["prototype"] = new GdArray(); break;
                    case "no_objectives": g["objectives"] = new GdArray(); break;
                    case "objective_sequence_mismatch": ((GdDict)g.GetArray("objectives")[1])["sequence"] = 7L; break;
                    case "unknown_start_room": g["start_room"] = "no_such_room"; break;
                    case "missing_goal_room":
                        g["goal_room"] = "";
                        l.GetDict("prototype").Erase("goal_room");
                        break;
                    case "kit_without_modules": k["modules"] = new GdArray(); break;
                    case "unknown_structural_module":
                        ((GdDict)l.GetDict("structural_plan").GetArray("floor_placements")[3])["module_id"] = "no_such_module";
                        break;
                    default: Assert.Fail("unknown failure case " + name); break;
                }
                var builder = NewBuilder();
                bool summaryEmitted = false;
                string reason = null;
                builder.ShipLoaded += _ => summaryEmitted = true;
                builder.LoadFailed += r => reason = r;
                bool loaded = builder.LoadFromDocuments(l, k, g, false, new GdDict());
                Assert.IsFalse(loaded, name);
                Assert.IsFalse(summaryEmitted, name);
                Assert.AreEqual(0, builder.View.transform.childCount, name + ": nothing is published on failure");
                Assert.AreEqual(expected.GetString("reason"), reason, name);
            }
        }

        [Test]
        public void LockedPortalDrivesItsStructuralBlocker()
        {
            var builder = NewBuilder();
            Assert.IsTrue(builder.LoadFromPaths("res://data/procgen/smoke/seed_000017/layout.json", "res://data/kits/ship_structural_v0.json",
                "res://data/procgen/smoke/seed_000017/gameplay_slice.json"));
            var locked = builder.View.GetAuthoredPortalNodes().First(p => p.portalKind == AuthoredPortalRuntime.LOCKED && p.StructuralBlocker != null);
            int shapes = locked.StructuralBlocker.GetComponentsInChildren<Collider>(true).Length;
            Assert.That(shapes, Is.GreaterThan(0));
            Assert.AreEqual(shapes, locked.GetStructuralBlockerCollisionEnabledCount());
            Assert.IsTrue(locked.IsStructuralBlockerVisible());

            var events = new List<(string, bool)>();
            locked.PortalStateChanged += (id, open) => events.Add((id, open));
            locked.SetValidationPlayerInRange(true);
            GdDict denied = locked.TryInteract(new GdDict());
            Assert.AreEqual("locked", denied.GetString("reason"));
            GdDict opened = locked.TryInteract(new GdDict { { "lockpick", true } });
            Assert.IsTrue(opened.GetBool("open"));
            Assert.IsTrue(opened.GetBool("unlocked_now"));
            Assert.AreEqual(0, locked.GetStructuralBlockerCollisionEnabledCount());
            Assert.IsFalse(locked.IsStructuralBlockerVisible());
            Assert.IsFalse(locked.GetBlockerCollider().enabled);
            Assert.AreEqual(1, events.Count);

            locked.RestorePersistentState(true, false);
            Assert.AreEqual(shapes, locked.GetStructuralBlockerCollisionEnabledCount());
            Assert.IsTrue(locked.GetBlockerCollider().enabled);
        }

        [Test]
        public void SceneLayersAndCollidersFollowThePortConvention()
        {
            var builder = NewBuilder();
            Assert.IsTrue(builder.LoadFromPaths(LoaderParity.InputPath("fixtures/godot/loader/inputs/seed_000017_augmented.layout.json"),
                LoaderParity.InputPath(LoaderParity.Kit), LoaderParity.InputPath("fixtures/godot/loader/inputs/seed_000017_augmented.gameplay_slice.json")));
            var view = builder.View;
            foreach (var v in view.GetObjectiveVolumes())
            {
                Assert.AreEqual(PhysicsLayers.Sensor, v.gameObject.layer);
                Assert.IsTrue(v.GetComponent<SphereCollider>().isTrigger);
            }
            foreach (var z in view.GetRadiationZoneVolumes().Concat(view.GetAuthoredAtmosphereVolumes()))
            {
                Assert.AreEqual(PhysicsLayers.Sensor, z.gameObject.layer);
                Assert.IsTrue(z.GetComponent<BoxCollider>().isTrigger);
            }
            foreach (var p in view.GetAuthoredPortalNodes())
            {
                Assert.IsTrue(p.GetComponent<SphereCollider>().isTrigger);
                Assert.AreEqual(PhysicsLayers.Portal, p.GetBlockerCollider().gameObject.layer);
            }
            foreach (var prop in view.GetPlacedPropNodes()) Assert.IsEmpty(prop.GetComponentsInChildren<Collider>(true), prop.name);
            foreach (var d in view.GetDressingNodes()) Assert.IsEmpty(d.GetComponentsInChildren<Collider>(true), d.name);
            Assert.That(view.GetDressingNodes().Count(d => d.role == "light"), Is.GreaterThan(0));
            Assert.That(view.GetDressingNodes().Where(d => d.role == "light").All(d => d.GetComponent<Light>().type == LightType.Point));
            // World helpers agree with the Godot-frame getters through Frame.
            Vec3 goal = view.GetGoalPosition();
            Assert.That(Vector3.Distance(view.GetGoalPositionWorld().Value, view.transform.TransformPoint(Frame.ToUnity(goal))), Is.LessThan(1e-5f));
            Assert.AreEqual(goal, view.ToLocalGodot(view.GetGoalPositionWorld().Value));
        }

        // ------------------------------------------------------------------ record (same shape as parity_loader.gd)

        static GdDict Record(ShipView view, string caseName, bool loaded, bool isAway, string failure, GdDict summary)
        {
            GeneratedShipLayout m = view.Layout;
            var rooms = new GdDict();
            foreach (object roomVariant in view.GetLayoutCopy().GetArrayOrEmpty("rooms"))
            {
                string rid = V.Str(((GdDict)roomVariant).Get("id", ""));
                Vec3 center = view.GetRoomCenter(rid);
                rooms[rid] = new GdDict
                {
                    { "center", center == Vec3.Inf ? null : LoaderParity.Canonical(center) },
                    { "role", view.GetRoomRole(rid) },
                    { "deck", view.GetRoomDeck(rid) },
                };
            }
            return new GdDict
            {
                { "case", caseName },
                { "is_away", isAway },
                { "loaded", loaded },
                { "load_failed_reason", failure },
                { "summary", LoaderParity.Canonical(summary) },
                { "has_loaded_ship", view.HasLoadedShip() },
                { "start_transform_origin", LoaderParity.Canonical(view.GetStartTransform().Origin) },
                { "goal_position", LoaderParity.Canonical(view.GetGoalPosition()) },
                { "objective_specs", LoaderParity.Canonical(view.GetObjectiveSpecsCopy()) },
                { "loot_container_specs", LoaderParity.Canonical(view.GetLootContainerSpecsCopy()) },
                { "placed_prop_specs", LoaderParity.Canonical(view.GetPlacedPropSpecsCopy()) },
                { "placed_prop_errors", LoaderParity.Canonical(view.GetPlacedPropErrors()) },
                { "authored_portal_specs", LoaderParity.Canonical(view.GetAuthoredPortalSpecsCopy()) },
                { "rooms", rooms },
                { "unknown_room", new GdDict { { "role", view.GetRoomRole("no_such_room") }, { "deck", view.GetRoomDeck("no_such_room") } } },
                { "critical_path", LoaderParity.Canonical(view.GetCriticalPath()) },
                { "room_links", LoaderParity.Canonical(view.GetRoomLinks()) },
                { "encounter_markers", LoaderParity.Canonical(view.GetEncounterMarkers()) },
                { "blocked_links", LoaderParity.Canonical(view.GetBlockedLinks()) },
                { "landmark_specs", LoaderParity.Canonical(view.GetLandmarkSpecs()) },
                { "breach_zone_markers", LoaderParity.Canonical(view.GetBreachZoneMarkers()) },
                { "breach_zone_specs", LoaderParity.Canonical(view.GetBreachZoneSpecs()) },
                { "fire_zone_markers", LoaderParity.Canonical(view.GetFireZoneMarkers()) },
                { "fire_zone_specs", LoaderParity.Canonical(view.GetFireZoneSpecs()) },
                { "arc_zone_markers", LoaderParity.Canonical(view.GetArcZoneMarkers()) },
                { "arc_zone_specs", LoaderParity.Canonical(view.GetArcZoneSpecs()) },
                { "radiation_zone_markers", LoaderParity.Canonical(view.GetRadiationZoneMarkers()) },
                { "radiation_zone_specs", LoaderParity.Canonical(view.GetRadiationZoneSpecs()) },
                { "radiation_zone_segments", LoaderParity.Canonical(view.GetRadiationZoneSegments()) },
                { "authored_atmosphere_specs", LoaderParity.Canonical(view.GetAuthoredAtmosphereSpecs()) },
                { "room_variant_descriptors", LoaderParity.Canonical(view.GetRoomVariantDescriptors()) },
                { "count_collision_shapes", (long)view.CountCollisionShapes() },
                { "probes", LoaderParity.Probes(m) },
                { "nodes", Nodes(view) },
            };
        }

        static GdDict Xform(Transform t) => Xform(t, t.name);

        static GdDict Xform(Transform t, string name)
        {
            Frame.ToGodotBasis(t.localRotation, out Vec3 x, out Vec3 y, out Vec3 z);
            return LoaderParity.Xform(name, Frame.ToGodot(t.localPosition), x, y, z);
        }

        static GdArray Markers(IEnumerable<RuntimeMarker> markers) => new GdArray(markers.Select(m => (object)Xform(m.transform)));

        static GdDict Nodes(ShipView view)
        {
            var placed = new GdArray();
            foreach (var p in view.GetPlacedPropNodes())
            {
                var info = Xform(p.transform);
                info["placed_prop_id"] = p.placedPropId;
                info["gameplay_prop_id"] = p.gameplayPropId;
                info["authored_position"] = LoaderParity.Canonical(p.GodotAuthoredPosition);
                info["children"] = new GdArray(p.transform.Cast<Transform>().Select(c => (object)c.name));
                placed.Add(info);
            }
            var portals = new GdArray();
            foreach (var p in view.GetAuthoredPortalNodes())
            {
                var info = Xform(p.transform);
                info["portal_id"] = p.portalId;
                info["portal_kind"] = p.portalKind;
                info["is_open"] = p.isOpen;
                info["is_unlocked"] = p.isUnlocked;
                info["is_unsafe"] = p.isUnsafe;
                info["is_exterior"] = p.isExterior;
                info["required_flag"] = p.RequiredFlag();
                info["structural_blocker"] = p.StructuralBlocker != null ? p.StructuralBlocker.name : "";
                info["structural_blocker_visible"] = p.IsStructuralBlockerVisible();
                info["structural_blocker_enabled_shapes"] = (long)p.GetStructuralBlockerCollisionEnabledCount();
                info["visual_visible"] = p.IsVisualVisible;
                info["blocker_disabled"] = p.GetBlockerCollider() != null ? (object)!p.GetBlockerCollider().enabled : null;
                portals.Add(info);
            }
            var volumes = new GdArray();
            foreach (var v in view.GetObjectiveVolumes())
            {
                var info = Xform(v.transform);
                info["objective_id"] = v.objectiveId;
                info["sequence"] = v.sequence;
                info["objective_type"] = v.objectiveType;
                info["room_id"] = v.roomId;
                volumes.Add(info);
            }
            var triggers = new GdArray();
            foreach (var z in view.GetRadiationZoneVolumes().Concat(view.GetAuthoredAtmosphereVolumes()))
            {
                var info = Xform(z.transform);
                info["size"] = GdArray.Of((double)z.size.x, (double)z.size.y, (double)z.size.z);
                if (z.kind == ZoneVolume.KindAtmosphere) info["atmosphere"] = LoaderParity.Canonical(z.Spec);
                triggers.Add(info);
            }
            var dressing = new GdArray();
            foreach (var d in view.GetDressingNodes())
            {
                var info = Xform(d.transform);
                info["dressing"] = d.dressing;
                switch (d.role)
                {
                    case "light":
                        {
                            var light = d.GetComponent<Light>();
                            info["class"] = "OmniLight3D";
                            info["prop_density"] = d.propDensity;
                            info["fog_density"] = d.fogDensity;
                            if (d.Tint != null) info["tint"] = LoaderParity.Canonical(d.Tint);
                            info["light_energy"] = (double)(light.intensity / AtmosphereApplier.OmniEnergyScale);
                            info["omni_range"] = (double)light.range;
                            info["light_color"] = GdArray.Of((double)light.color.r, (double)light.color.g, (double)light.color.b, (double)light.color.a);
                            break;
                        }
                    case "fog":
                        {
                            var renderer = d.GetComponent<MeshRenderer>();
                            Color c = renderer.sharedMaterial.GetColor("_BaseColor");
                            info["class"] = "MeshInstance3D";
                            info["fog_density"] = d.fogDensity;
                            info["sphere_radius"] = (double)(d.transform.localScale.x * 0.5f);
                            info["sphere_height"] = (double)d.transform.localScale.y;
                            info["albedo"] = GdArray.Of((double)c.r, (double)c.g, (double)c.b, (double)c.a);
                            break;
                        }
                    default:
                        {
                            var mesh = d.transform.Find("DressingMesh");
                            info["class"] = "Node3D";
                            info["collision_policy"] = "none_visual_only";
                            info["dressing_kind"] = d.dressingKind;
                            info["slot_kind"] = d.slotKind;
                            info["slot_index"] = d.slotIndex;
                            info["slot_cell"] = LoaderParity.Canonical(d.SlotCell);
                            info["mesh_child"] = Xform(mesh);
                            Mesh shared = mesh.GetComponent<MeshFilter>().sharedMesh;
                            info["mesh_class"] = shared == RuntimeVisualCatalog.Sphere ? "SphereMesh" : shared == RuntimeVisualCatalog.Cube ? "BoxMesh" : "CylinderMesh";
                            break;
                        }
                }
                dressing.Add(info);
            }
            var integrity = new GdDict();
            foreach (var module in view.Modules)
                if (module.integrityState != StructuralModule.IntegrityIntact) integrity[module.moduleKey] = module.integrityState;
            var verticalLinks = new GdArray();
            foreach (var link in view.GetVerticalLinks())
                verticalLinks.Add(new GdDict { { "name", link.Name }, { "start", LoaderParity.Canonical(link.Start) }, { "end", LoaderParity.Canonical(link.End) } });

            var atmosphere = new GdDict();
            if (view.AtmosphereSummary != null)
            {
                foreach (Transform child in view.transform)
                {
                    var light = child.GetComponent<Light>();
                    if (light == null) continue;
                    if (light.type == LightType.Directional) atmosphere["key_light_energy"] = (double)(light.intensity / AtmosphereApplier.DirectionalEnergyScale);
                    else if (child.name == AtmosphereApplier.AccentLightName) atmosphere["accent_energy"] = (double)(light.intensity / AtmosphereApplier.OmniEnergyScale);
                }
                atmosphere["fog_enabled"] = RenderSettings.fog;
                atmosphere["fog_density"] = (double)RenderSettings.fogDensity;
            }

            return new GdDict
            {
                { "landmarks", Markers(view.GetLandmarkNodes()) },
                { "blocked_routes", Markers(view.GetBlockedRouteNodes()) },
                { "vertical_transitions", Markers(view.GetVisibleVerticalTransitionNodes()) },
                { "placed_props", placed },
                { "authored_portals", portals },
                { "objective_volumes", volumes },
                { "trigger_volumes", triggers },
                { "dressing", dressing },
                { "module_keys", new GdArray(view.Modules.Select(mod => (object)mod.moduleKey)) },
                { "integrity_states", integrity },
                { "vertical_links", verticalLinks },
                { "atmosphere", atmosphere },
            };
        }
    }
}
