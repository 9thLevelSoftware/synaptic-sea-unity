using System.Collections.Generic;
using System.IO;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests
{
    /// <summary>
    /// Shared helpers for the GeneratedShipLoader parity fixtures (fixtures/godot/loader, captured by
    /// scripts/validation/parity_fixtures/parity_loader.gd): canonical conversion of loader values (Vec3 → [x, y, z])
    /// and the record sections that the pure <see cref="GeneratedShipLayout"/> can reproduce. The Unity
    /// ShipSceneBuilder tests add the scene-node sections on top.
    /// </summary>
    public static class LoaderParity
    {
        public const string Dir = "godot/loader";
        public const string Kit = "data/kits/ship_structural_v0.json";

        /// <summary>Fixture case name → (layout, gameplay slice) paths relative to the repo root.</summary>
        public static readonly string[] Cases =
        {
            "coherent_ship_001_home", "coherent_ship_001_away",
            "coherent_ship_002_home", "coherent_ship_002_away",
            "coherent_ship_003_home", "coherent_ship_003_away",
            "seed_000017_home", "seed_000017_away",
            "seed_000017_augmented_home", "seed_000017_augmented_away",
        };

        public static GdDict Fixture(string caseName) => Fixtures.ReadDict($"{Dir}/{caseName}.json");

        /// <summary>Absolute path of a fixture "inputs" entry (data/... under StreamingAssets, fixtures/... under the repo).</summary>
        public static string InputPath(string relative)
        {
            relative = relative.Replace('\\', '/');
            if (relative.StartsWith("fixtures/")) return Path.Combine(Fixtures.RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            return Path.Combine(Fixtures.StreamingDataRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        public static GdDict LoadInput(string relative) => GdJson.ParseDict(File.ReadAllText(InputPath(relative)));

        public static TreeDiff.Options Options(params string[] ignoreKeys)
        {
            var o = new TreeDiff.Options { FloatTolerance = 1e-4, MaxDifferences = 40 };
            foreach (string k in ignoreKeys) o.IgnoreKeys.Add(k);
            return o;
        }

        /// <summary>Godot writer's <c>canonical()</c>: Vec3 → [x, y, z] doubles (inf stays inf), containers recursively.</summary>
        public static object Canonical(object value)
        {
            switch (value)
            {
                case Vec3 v:
                    return GdArray.Of((double)v.X, (double)v.Y, (double)v.Z);
                case Vec2i v2:
                    return GdArray.Of((long)v2.X, (long)v2.Y);
                case GdDict d:
                    {
                        var output = new GdDict();
                        foreach (var pair in d) output[V.Str(pair.Key)] = Canonical(pair.Value);
                        return output;
                    }
                case GdArray a:
                    {
                        var output = new GdArray();
                        foreach (object item in a) output.Add(Canonical(item));
                        return output;
                    }
                case IEnumerable<Vec3> vs:
                    {
                        var output = new GdArray();
                        foreach (var v in vs) output.Add(Canonical(v));
                        return output;
                    }
                case IEnumerable<string> ss:
                    {
                        var output = new GdArray();
                        foreach (var s in ss) output.Add(s);
                        return output;
                    }
                case float f:
                    return (double)f;
                case int i:
                    return (long)i;
                default:
                    return value;
            }
        }

        public static GdArray Basis(Vec3 x, Vec3 y, Vec3 z) => GdArray.Of(Canonical(x), Canonical(y), Canonical(z));

        public static GdDict Xform(string name, Vec3 position, Vec3 bx, Vec3 by, Vec3 bz) =>
            new GdDict { { "name", name }, { "position", Canonical(position) }, { "basis", Basis(bx, by, bz) } };

        static GdArray Floats(float[] values)
        {
            var output = new GdArray();
            foreach (float v in values) output.Add((double)v);
            return output;
        }

        /// <summary>
        /// Runs the pure half of <c>load_from_documents</c> (everything but scene nodes) the way ShipSceneBuilder does.
        /// Returns null when the documents would fail to load.
        /// </summary>
        public static GeneratedShipLayout BuildModel(GdDict layout, GdDict gameplay, GdDict gameplayPropCatalog, PropVisualBindingCatalog visualCatalog,
            out List<GeneratedShipLayout.PlacedPropEntry> placedProps, out List<GeneratedShipLayout.PortalPlan> portals,
            out List<GeneratedShipLayout.DressingItem> dressing, out int verticalLinkCount)
        {
            var model = new GeneratedShipLayout(layout, gameplay);
            placedProps = null;
            portals = null;
            dressing = null;
            verticalLinkCount = 0;
            var prototype = layout.GetDictOrEmpty("prototype");
            string start = V.Str(gameplay.Get("start_room", prototype.Get("start_room", "")));
            string goal = V.Str(gameplay.Get("goal_room", prototype.Get("goal_room", "")));
            model.ObjectiveSpecs = model.BuildObjectiveSpecs("");
            if (model.ObjectiveSpecs.IsEmpty) return null;
            model.LootContainerSpecs = model.BuildLootContainerSpecs();
            model.StartPosition = model.RoomCenter(start);
            model.GoalPosition = model.RoomCenter(goal);
            if (model.StartPosition == Vec3.Inf || model.GoalPosition == Vec3.Inf || !model.HasNavigableFloor()) return null;
            verticalLinkCount = model.BuildVerticalLinks();
            model.BuildCoherenceMarkers();
            placedProps = model.BuildPlacedPropPlan(gameplayPropCatalog, visualCatalog);
            portals = model.BuildPortalPlans();
            dressing = model.BuildDressingPlan();
            return model;
        }

        /// <summary>The record sections computed from Core data only (same shapes as the Godot capture).</summary>
        public static GdDict CoreRecord(GeneratedShipLayout m, List<GeneratedShipLayout.PlacedPropEntry> placedProps,
            List<GeneratedShipLayout.DressingItem> dressing, int verticalLinkCount)
        {
            var rooms = new GdDict();
            foreach (object roomVariant in m.LayoutDoc.GetArrayOrEmpty("rooms"))
            {
                string rid = V.Str(((GdDict)roomVariant).Get("id", ""));
                Vec3 center = m.GetRoomCenter(rid);
                rooms[rid] = new GdDict
                {
                    { "center", center == Vec3.Inf ? null : Canonical(center) },
                    { "role", m.GetRoomRole(rid) },
                    { "deck", m.GetRoomDeck(rid) },
                };
            }
            var placedSpecs = new GdArray();
            var placedErrors = new GdArray();
            foreach (var entry in placedProps)
            {
                if (entry.Error != null) placedErrors.Add(entry.Error);
                else placedSpecs.Add(Canonical(entry.Spec));
            }
            var record = new GdDict
            {
                { "summary_partial", new GdDict
                    {
                        { "vertical_link_count", (long)verticalLinkCount },
                        { "objective_count", (long)m.ObjectiveSpecs.Count },
                        { "start_position", Canonical(m.StartPosition) },
                        { "goal_position", Canonical(m.GoalPosition) },
                    }
                },
                { "start_transform_origin", Canonical(m.StartPosition) },
                { "goal_position", Canonical(m.GoalPosition) },
                { "objective_specs", Canonical(m.ObjectiveSpecs) },
                { "loot_container_specs", Canonical(m.LootContainerSpecs) },
                { "placed_prop_specs", placedSpecs },
                { "placed_prop_errors", placedErrors },
                { "authored_portal_specs", Canonical(m.AuthoredPortalSpecs) },
                { "rooms", rooms },
                { "unknown_room", new GdDict { { "role", m.GetRoomRole("no_such_room") }, { "deck", m.GetRoomDeck("no_such_room") } } },
                { "critical_path", Canonical(m.GetCriticalPath()) },
                { "room_links", Canonical(m.GetRoomLinks()) },
                { "encounter_markers", Canonical(m.GetEncounterMarkers()) },
                { "blocked_links", Canonical(m.GetBlockedLinks()) },
                { "landmark_specs", Canonical(m.GetLandmarkSpecs()) },
                { "breach_zone_markers", Canonical(m.BreachZoneMarkers) },
                { "breach_zone_specs", Canonical(m.BreachZoneSpecs) },
                { "fire_zone_markers", Canonical(m.FireZoneMarkers) },
                { "fire_zone_specs", Canonical(m.FireZoneSpecs) },
                { "arc_zone_markers", Canonical(m.ArcZoneMarkers) },
                { "arc_zone_specs", Canonical(m.ArcZoneSpecs) },
                { "radiation_zone_markers", Canonical(m.RadiationZoneMarkers) },
                { "radiation_zone_specs", Canonical(m.RadiationZoneSpecs) },
                { "radiation_zone_segments", Canonical(m.RadiationZoneSegments) },
                { "authored_atmosphere_specs", Canonical(m.AuthoredAtmosphereSpecs) },
                { "room_variant_descriptors", Canonical(m.RoomVariantDescriptors) },
                { "probes", Probes(m) },
            };
            var nodes = new GdDict
            {
                { "landmarks", Markers(m.Landmarks) },
                { "blocked_routes", Markers(m.BlockedRoutes) },
                { "vertical_transitions", Markers(m.VerticalTransitions) },
                { "trigger_volumes", TriggerVolumes(m) },
                { "dressing", Dressing(dressing) },
                { "vertical_links", VerticalLinks(m) },
            };
            record["nodes"] = nodes;
            return record;
        }

        static GdArray Markers(List<GeneratedShipLayout.MarkerSpec> markers)
        {
            var output = new GdArray();
            foreach (var s in markers) output.Add(Xform(s.Name, s.Position, s.BasisX, s.BasisY, s.BasisZ));
            return output;
        }

        static GdArray TriggerVolumes(GeneratedShipLayout m)
        {
            var output = new GdArray();
            foreach (var list in new[] { m.RadiationVolumes, m.AtmosphereVolumes })
            {
                foreach (var s in list)
                {
                    var info = Xform(s.Name, s.Position, s.BasisX, s.BasisY, s.BasisZ);
                    info["size"] = Canonical(s.Size);
                    if (s.Kind == "atmosphere") info["atmosphere"] = Canonical(s.Spec);
                    output.Add(info);
                }
            }
            return output;
        }

        static GdArray VerticalLinks(GeneratedShipLayout m)
        {
            var output = new GdArray();
            foreach (var link in m.VerticalLinks)
                output.Add(new GdDict { { "name", link.Name }, { "start", Canonical(link.Start) }, { "end", Canonical(link.End) } });
            return output;
        }

        public static GdArray Probes(GeneratedShipLayout m)
        {
            var points = new List<Vec3>();
            foreach (var marker in m.RadiationZoneMarkers)
            {
                points.Add(marker);
                points.Add(marker + new Vec3(0.9f, 0.3f, 0f));
                points.Add(marker + new Vec3(0f, 0f, 1.6f));
            }
            foreach (object specVariant in m.AuthoredAtmosphereSpecs)
            {
                var p = ((GdDict)specVariant).Get("position", Vec3.Zero) is Vec3 v ? v : Vec3.Zero;
                points.Add(p + new Vec3(0f, 1f, 0f));
                points.Add(p + new Vec3(1.9f, 2.4f, -1.9f));
                points.Add(p + new Vec3(0f, -0.1f, 0f));
            }
            points.Add(new Vec3(1000f, 0f, 1000f));
            var output = new GdArray();
            foreach (var point in points)
            {
                output.Add(new GdDict
                {
                    { "point", Canonical(point) },
                    { "radiation_zone_at", Canonical(m.GetRadiationZoneAt(point)) },
                    { "authored_atmosphere_at", Canonical(m.GetAuthoredAtmosphereAt(point)) },
                    { "drain_multiplier", m.GetAuthoredAtmosphereDrainMultiplierAt(point) },
                });
            }
            return output;
        }

        static readonly Vec3 IdentityX = Vec3.Right, IdentityY = Vec3.Up, IdentityZ = Vec3.Back;

        public static GdArray Dressing(List<GeneratedShipLayout.DressingItem> items)
        {
            var output = new GdArray();
            if (items == null) return output;
            foreach (var item in items)
            {
                var info = Xform(item.Name, item.Position, IdentityX, IdentityY, IdentityZ);
                info["dressing"] = item.Dressing;
                switch (item.Kind)
                {
                    case GeneratedShipLayout.DressingKind.Light:
                        info["class"] = "OmniLight3D";
                        info["prop_density"] = item.PropDensity;
                        info["fog_density"] = item.FogDensity;
                        if (item.Tint != null) info["tint"] = Canonical(item.Tint);
                        info["light_energy"] = item.LightEnergy;
                        info["omni_range"] = item.OmniRange;
                        info["light_color"] = Floats(item.LightColor);
                        break;
                    case GeneratedShipLayout.DressingKind.Fog:
                        info["class"] = "MeshInstance3D";
                        info["fog_density"] = item.FogDensity;
                        info["sphere_radius"] = (double)item.SphereRadius;
                        info["sphere_height"] = (double)item.SphereHeight;
                        info["albedo"] = Floats(item.Albedo);
                        break;
                    default:
                        info["class"] = "Node3D";
                        info["collision_policy"] = "none_visual_only";
                        info["dressing_kind"] = item.PropKind;
                        info["slot_kind"] = "wall";
                        info["slot_index"] = item.SlotIndex;
                        info["slot_cell"] = Canonical(item.SlotCell);
                        info["mesh_child"] = DressingMeshChild(item.PropKind);
                        info["mesh_class"] = item.PropKind == "pipe" ? "CylinderMesh" : item.PropKind == "growth" ? "SphereMesh" : "BoxMesh";
                        break;
                }
                output.Add(info);
            }
            return output;
        }

        /// <summary>The Godot DressingMesh transform per kind (_create_dressing_prop).</summary>
        public static GdDict DressingMeshChild(string kind)
        {
            switch (kind)
            {
                case "pipe":
                    // rotation_degrees (0, 0, 90).
                    return Xform("DressingMesh", new Vec3(0f, 0.7f, 0f), new Vec3(-4.371139E-08f, 1f, 0f), new Vec3(-1f, -4.371139E-08f, 0f), Vec3.Back);
                case "growth":
                    return Xform("DressingMesh", new Vec3(0f, 0.28f, 0f), IdentityX, IdentityY, IdentityZ);
                default:
                    return Xform("DressingMesh", new Vec3(0f, 0.225f, 0f), IdentityX, IdentityY, IdentityZ);
            }
        }

        /// <summary>Returns <paramref name="source"/>'s entries for <paramref name="keys"/> only.</summary>
        public static GdDict Pick(GdDict source, IEnumerable<string> keys)
        {
            var output = new GdDict();
            foreach (string k in keys)
                if (source.Has(k)) output[k] = source[k];
            return output;
        }
    }
}
