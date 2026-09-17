// Ported from scripts/procgen/generated_ship_loader.gd @ 96ecb2b0
// (the pure spec/query half lives in Core/Procgen/GeneratedShipLayout.cs; structural wrappers in StructuralLayoutBuilder)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Builds a generated / golden ship from its layout, kit and gameplay-slice documents into a <see cref="ShipView"/>
    /// (port of <c>GeneratedShipLoader.load_from_paths</c> / <c>load_from_documents</c>). Same validation order,
    /// failure reasons, and <c>ship_loaded</c> summary keys as Godot. <see cref="ShipLoaded"/> and
    /// <see cref="LoadFailed"/> are raised <b>synchronously</b> before the load call returns (the run coordinator
    /// depends on it).
    ///
    /// Build order: structural wrappers + integrity visuals → vertical links (data) → coherence markers (landmarks,
    /// blocked routes, vertical transitions) → hazard zones (breach / fire / arc / radiation volumes) → authored
    /// atmosphere volumes → placed props → authored portals → room dressing (lights, fog markers, props) → biome
    /// atmosphere → objective volumes. Everything is built under an inactive staging root and parented to the view
    /// only on success.
    ///
    /// Dropped: <c>_build_navigation_region</c> / <c>_orient_navigation_polygons_up</c> — the NavigationRegion3D bake
    /// was debug-only in Godot (runtime AI uses the pure ShipNavGraph); only its "no floor placements" failure is kept.
    /// Vertical NavigationLink3D nodes become <see cref="ShipView.GetVerticalLinks"/> data (the count stays in the
    /// summary).
    /// </summary>
    public sealed class ShipSceneBuilder
    {
        public const float ObjectiveTriggerRadius = (float)GeneratedShipLayout.OBJECTIVE_TRIGGER_RADIUS;

        /// <summary><c>ship_loaded(summary)</c>: layout_path, kit_path, gameplay_slice_path, instantiated_count,
        /// vertical_link_count, objective_count, start_position, goal_position (Vec3, Godot frame).</summary>
        public event Action<GdDict> ShipLoaded;

        /// <summary><c>load_failed(reason)</c>.</summary>
        public event Action<string> LoadFailed;

        public ShipView View { get; }

        /// <summary>
        /// Kit prefab catalog override; by default <c>Resources/Catalogs/KitCatalog_&lt;kit_id&gt;</c>.
        /// Set a layout's <c>kit_id</c> to <see cref="SynapticSea.Core.Procgen.KitCatalog.ITHAPPY_KIT_ID"/>
        /// to load the additive KEEP ithappy catalog without changing ship_structural_v0.
        /// </summary>
        public KitPrefabCatalog KitCatalog { get; set; }

        /// <summary>Prop prefab catalog override; by default <c>Resources/Catalogs/PropCatalog</c>.</summary>
        public PropCatalog PropCatalog { get; set; }

        public ShipSceneBuilder(ShipView view)
        {
            View = view != null ? view : throw new ArgumentNullException(nameof(view));
        }

        /// <summary>Creates a <c>GeneratedShipLoader</c> GameObject with a <see cref="ShipView"/> under <paramref name="parent"/>.</summary>
        public static ShipSceneBuilder Create(Transform parent = null, string name = "GeneratedShipLoader")
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return new ShipSceneBuilder(go.AddComponent<ShipView>());
        }

        public void ClearLoadedShip() => View.Clear();

        // ------------------------------------------------------------------ entry points

        public bool LoadFromPaths(string layoutPath, string kitPath, string gameplaySlicePath, bool isAway = false)
        {
            ClearLoadedShip();
            string layoutAbs = ResolvePath(layoutPath);
            string kitAbs = ResolvePath(kitPath);
            string sliceAbs = ResolvePath(gameplaySlicePath);
            if (!File.Exists(layoutAbs)) return FailLoad("layout not found: " + layoutAbs);
            if (!File.Exists(kitAbs)) return FailLoad("kit not found: " + kitAbs);
            if (!File.Exists(sliceAbs)) return FailLoad("gameplay slice not found: " + sliceAbs);

            GdDict layout = LoadJsonDict(layoutAbs, "layout");
            if (layout.IsEmpty) return FailLoad("layout JSON is invalid: " + layoutAbs);
            GdDict kit = LoadJsonDict(kitAbs, "kit");
            if (kit.IsEmpty) return FailLoad("kit JSON is invalid: " + kitAbs);
            GdDict gameplay = LoadJsonDict(sliceAbs, "gameplay slice");
            if (gameplay.IsEmpty) return FailLoad("gameplay slice JSON is invalid: " + sliceAbs);

            return LoadFromDocuments(layout, kit, gameplay, isAway, new GdDict
            {
                { "layout", layoutAbs },
                { "kit", kitAbs },
                { "gameplay_slice", sliceAbs },
            });
        }

        /// <param name="isAway">Godot names this parameter <c>apply_atmosphere</c> but forwards it as the atmosphere
        /// pass's <c>is_away</c> (denser fog on derelicts); the atmosphere itself applies whenever the layout names a biome.</param>
        public bool LoadFromDocuments(GdDict layout, GdDict kit, GdDict gameplaySlice, bool isAway = false, GdDict sourcePaths = null)
        {
            ClearLoadedShip();
            // Catalog reads (biomes, gameplay props, prop bindings) go through CoreServices; default to StreamingAssets
            // when no composition root configured it.
            if (CoreServices.Resources == null) CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            layout = layout ?? new GdDict();
            kit = kit ?? new GdDict();
            gameplaySlice = gameplaySlice ?? new GdDict();
            sourcePaths = sourcePaths ?? new GdDict();
            var model = new GeneratedShipLayout(layout, gameplaySlice);
            View.Layout = model;

            string layoutAbs = V.Str(sourcePaths.Get("layout", ""));
            string kitAbs = V.Str(sourcePaths.Get("kit", ""));
            string sliceAbs = V.Str(sourcePaths.Get("gameplay_slice", ""));

            if (!(layout.Get("rooms", new GdArray()) is GdArray)) return FailLoad("layout missing rooms array: " + layoutAbs);
            if (!(layout.Get("prototype", new GdDict()) is GdDict prototype)) return FailLoad("layout missing prototype object: " + layoutAbs);

            string startRoomId = V.Str(gameplaySlice.Get("start_room", prototype.Get("start_room", "")));
            string goalRoomId = V.Str(gameplaySlice.Get("goal_room", prototype.Get("goal_room", "")));
            if (startRoomId.Length == 0) return FailLoad("gameplay slice missing start_room: " + sliceAbs);
            if (goalRoomId.Length == 0) return FailLoad("gameplay slice missing goal_room: " + sliceAbs);

            HashSet<string> moduleIds = BuildModuleMap(kit, kitAbs);
            if (moduleIds.Count == 0) return FailLoad("kit contains no usable module wrapper scenes: " + kitAbs);

            GdDict verdict = ValidateStructuralPlan(layout);
            if (!V.Bool(verdict.Get("ok", false))) return FailLoad("layout structural plan validation failed: " + V.Str(verdict.Get("errors", new GdArray())));
            KitPrefabCatalog kitCatalog = ResolveKitCatalog(kit);
            if (!PreflightStructuralWrappers(moduleIds, kitCatalog, layout.Get("structural_plan", new GdDict()) as GdDict ?? new GdDict()))
                return FailLoad("structural wrapper preflight failed");

            model.ObjectiveSpecs = model.BuildObjectiveSpecs(sliceAbs);
            if (model.ObjectiveSpecs.IsEmpty) return FailLoad("gameplay slice contains no valid objectives: " + sliceAbs);
            model.LootContainerSpecs = model.BuildLootContainerSpecs();

            model.StartPosition = model.RoomCenter(startRoomId);
            model.GoalPosition = model.RoomCenter(goalRoomId);
            if (model.StartPosition == Vec3.Inf) return FailLoad("start room not found in layout: " + startRoomId);
            if (model.GoalPosition == Vec3.Inf) return FailLoad("goal room not found in layout: " + goalRoomId);

            // Build detached: nothing reaches the view unless every step succeeds.
            var staging = new GameObject("GeneratedShipStaging");
            staging.SetActive(false);
            try
            {
                var structural = new StructuralLayoutBuilder().Build(layout, kitCatalog, staging.transform);
                if (structural == null)
                {
                    DestroyObject(staging);
                    ClearLoadedShip();
                    return FailLoad("failed to instantiate structural wrapper scenes");
                }
                Transform structuralRoot = structural.Root.transform;
                var objectiveRoot = new GameObject("ObjectiveRoot").transform;
                objectiveRoot.SetParent(staging.transform, false);

                if (!model.HasNavigableFloor())
                {
                    Debug.LogError("no floor/corridor floor placements found for navigation mesh");
                    DestroyObject(staging);
                    ClearLoadedShip();
                    return FailLoad("no floor/corridor floor placements found for navigation mesh");
                }
                int verticalLinkCount = model.BuildVerticalLinks();

                model.BuildCoherenceMarkers();
                AddCoherenceNodes(model, structuralRoot);
                AddPlacedProps(model, structuralRoot);
                AddAuthoredPortals(model, structural, structuralRoot);
                AddDressingVisuals(model, structuralRoot);

                // Publish.
                structuralRoot.SetParent(View.transform, false);
                objectiveRoot.SetParent(View.transform, false);
                DestroyObject(staging);
                staging = null;
                View.StructuralRoot = structuralRoot;
                View.ObjectiveRoot = objectiveRoot;
                foreach (var module in structural.Modules) View._modules.Add(module);
                foreach (var pair in structural.ByModuleKey) View._modulesByKey[pair.Key] = pair.Value;

                ApplySliceAtmosphere(layout, isAway);
                AddObjectiveVolumes(model, objectiveRoot);

                var summary = new GdDict
                {
                    { "layout_path", layoutAbs },
                    { "kit_path", kitAbs },
                    { "gameplay_slice_path", sliceAbs },
                    { "instantiated_count", (long)structural.Modules.Count },
                    { "vertical_link_count", (long)verticalLinkCount },
                    { "objective_count", (long)model.ObjectiveSpecs.Count },
                    { "start_position", model.StartPosition },
                    { "goal_position", model.GoalPosition },
                };
                View.Summary = summary;
                ShipLoaded?.Invoke(summary);
                return true;
            }
            catch
            {
                if (staging != null) DestroyObject(staging);
                ClearLoadedShip();
                throw;
            }
        }

        // ------------------------------------------------------------------ validation / preflight

        bool FailLoad(string reason)
        {
            Debug.LogError(reason);
            LoadFailed?.Invoke(reason);
            return false;
        }

        /// <summary>Port of <c>_build_module_scene_map</c>: module ids that declare a Godot wrapper scene.</summary>
        static HashSet<string> BuildModuleMap(GdDict kit, string kitPath)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!(kit.Get("modules", new GdArray()) is GdArray modules))
            {
                Debug.LogError("kit missing modules array: " + kitPath);
                return ids;
            }
            foreach (object moduleVariant in modules)
            {
                if (!(moduleVariant is GdDict module)) continue;
                string moduleId = V.Str(module.Get("module_id", ""));
                string scenePath = V.Str(module.Get("godot_wrapper_scene", ""));
                if (moduleId.Length == 0 || scenePath.Length == 0) continue;
                ids.Add(moduleId);
            }
            return ids;
        }

        static GdDict ValidateStructuralPlan(GdDict layout)
        {
            if (!(layout.Get("structural_plan", null) is GdDict plan))
                return new GdDict { { "ok", false }, { "errors", GdArray.Of("layout missing validated structural_plan") } };
            return new StructuralPlanValidator().Validate(plan, layout);
        }

        KitPrefabCatalog ResolveKitCatalog(GdDict kit)
        {
            if (KitCatalog != null) return KitCatalog;
            string kitId = V.Str(kit.Get("kit_id", ""));
            return kitId.Length == 0 ? null : Resources.Load<KitPrefabCatalog>("Catalogs/KitCatalog_" + kitId);
        }

        /// <summary>
        /// Port of <c>_preflight_structural_wrappers</c>: every edge / floor / ceiling record is an object whose module
        /// has a wrapper (kit JSON) and a prefab (kit prefab catalog; Godot probed the PackedScene).
        /// </summary>
        static bool PreflightStructuralWrappers(HashSet<string> moduleIds, KitPrefabCatalog catalog, GdDict plan)
        {
            if (!(plan.Get("placements", null) is GdArray edges) || !(plan.Get("floor_placements", null) is GdArray floors))
            {
                Debug.LogError("structural plan wrapper preflight requires edge and floor placement arrays");
                return false;
            }
            var all = new List<object>(edges);
            all.AddRange(floors);
            if (plan.Get("ceiling_placements", new GdArray()) is GdArray ceilings) all.AddRange(ceilings);
            var probed = new HashSet<string>(StringComparer.Ordinal);
            foreach (object recordVariant in all)
            {
                if (!(recordVariant is GdDict record))
                {
                    Debug.LogError("structural plan wrapper preflight found non-object placement");
                    return false;
                }
                string moduleId = V.Str(record.Get("module_id", ""));
                if (probed.Contains(moduleId)) continue;
                if (moduleId.Length == 0 || !moduleIds.Contains(moduleId))
                {
                    Debug.LogError("structural plan wrapper preflight missing wrapper for module " + moduleId);
                    return false;
                }
                if (catalog == null || !catalog.TryGetPrefab(moduleId, out StructuralModule prefab) || prefab == null)
                {
                    Debug.LogError($"structural plan wrapper preflight missing prefab for module {moduleId} (kit catalog {(catalog != null ? catalog.kitId : "<none>")})");
                    return false;
                }
                probed.Add(moduleId);
            }
            return true;
        }

        // ------------------------------------------------------------------ coherence markers + zones

        void AddCoherenceNodes(GeneratedShipLayout model, Transform root)
        {
            foreach (var spec in model.Landmarks)
                View._landmarkNodes.Add(MakeMarkerNode(spec, RuntimeMarker.KindLandmark, PhysicsLayers.Structure, root));
            foreach (var spec in model.BlockedRoutes)
                View._blockedRouteNodes.Add(MakeMarkerNode(spec, RuntimeMarker.KindBlockedRoute, PhysicsLayers.ZoneBlocker, root));
            foreach (var spec in model.VerticalTransitions)
                View._verticalTransitionNodes.Add(MakeMarkerNode(spec, RuntimeMarker.KindVerticalTransition, PhysicsLayers.Structure, root));
            foreach (var spec in model.RadiationVolumes)
                View._radiationVolumes.Add(MakeTriggerVolume(spec, root));
            foreach (var spec in model.AtmosphereVolumes)
                View._atmosphereVolumes.Add(MakeTriggerVolume(spec, root));
        }

        /// <summary>Port of <c>_make_marker_node</c> (always collidable in the loader).</summary>
        static RuntimeMarker MakeMarkerNode(GeneratedShipLayout.MarkerSpec spec, string kind, int collisionLayer, Transform parent)
        {
            var go = new GameObject(GodotNodeName.Validate(spec.Name)) { layer = collisionLayer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Frame.ToUnity(spec.Position);
            go.transform.localRotation = Frame.BasisRotation(spec.BasisX, spec.BasisY, spec.BasisZ);
            var marker = go.AddComponent<RuntimeMarker>();
            marker.kind = kind;
            marker.GodotPosition = spec.Position;
            marker.GodotBasisX = spec.BasisX;
            marker.GodotBasisY = spec.BasisY;
            marker.GodotBasisZ = spec.BasisZ;

            Vector3 size = Frame.SizeToUnity(spec.Size);
            Vector3 center = Frame.ToUnity(new Vec3(0f, spec.Size.Y * 0.5f, 0f));
            RuntimeVisualCatalog.AddMesh(go.transform, "Mesh", RuntimeVisualCatalog.Cube,
                RuntimeVisualCatalog.Material(ToColor(spec.Color), emissionEnergy: 0.4f), center, Quaternion.identity, size, collisionLayer);
            var body = new GameObject("CollisionRoot") { layer = collisionLayer };
            body.transform.SetParent(go.transform, false);
            var box = body.AddComponent<BoxCollider>();
            box.size = size;
            box.center = center;
            return marker;
        }

        /// <summary>Port of <c>_make_trigger_volume</c> / <c>_make_oriented_trigger_volume</c>.</summary>
        static ZoneVolume MakeTriggerVolume(GeneratedShipLayout.TriggerVolumeSpec spec, Transform parent)
        {
            var go = new GameObject(GodotNodeName.Validate(spec.Name)) { layer = PhysicsLayers.Sensor };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Frame.ToUnity(spec.Position);
            go.transform.localRotation = Frame.BasisRotation(spec.BasisX, spec.BasisY, spec.BasisZ);
            Vector3 size = Frame.SizeToUnity(spec.Size);
            Vector3 center = Frame.ToUnity(new Vec3(0f, spec.Size.Y * 0.5f, 0f));
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            box.center = center;
            RuntimeVisualCatalog.AddMesh(go.transform, "Mesh", RuntimeVisualCatalog.Cube,
                RuntimeVisualCatalog.Material(ToColor(spec.Color), unshaded: true, transparent: true), center, Quaternion.identity, size,
                PhysicsLayers.Sensor, castShadows: false);
            var zone = go.AddComponent<ZoneVolume>();
            zone.kind = spec.Kind;
            zone.size = size;
            zone.Spec = spec.Spec?.DeepCopy() ?? new GdDict();
            zone.GodotPosition = spec.Position;
            return zone;
        }

        // ------------------------------------------------------------------ placed props / portals

        void AddPlacedProps(GeneratedShipLayout model, Transform root)
        {
            GdDict propCatalog = GameplayPropFactory.LoadCatalog().Get("props", new GdDict()) as GdDict ?? new GdDict();
            var visualCatalog = new PropVisualBindingCatalog();
            bool visualCatalogLoaded = visualCatalog.LoadFromPath();
            PropCatalog prefabs = PropCatalog != null ? PropCatalog : RuntimePropVisualBinder.DefaultCatalog;
            foreach (var entry in model.BuildPlacedPropPlan(propCatalog, visualCatalogLoaded ? visualCatalog : null))
            {
                if (entry.Error != null)
                {
                    View._placedPropErrors.Add(entry.Error);
                    continue;
                }
                GameObject prop;
                if (!entry.DressingBinding.IsEmpty)
                {
                    GameObject imported = RuntimePropVisualBinder.CreateDressingVisual(entry.DressingBinding, prefabs);
                    if (imported == null)
                    {
                        View._placedPropErrors.Add($"authored placed prop visual_id '{entry.PropId}' could not be materialized");
                        continue;
                    }
                    prop = new GameObject("PlacedProp") { layer = PhysicsLayers.Prop };
                    prop.transform.SetParent(root, false);
                    prop.transform.localPosition = Frame.ToUnity(entry.Position);
                    imported.transform.SetParent(prop.transform, false);
                }
                else
                {
                    prop = GameplayPropFactory.Build(entry.PropId, entry.Position, root);
                }
                prop.name = GodotNodeName.Validate("PlacedProp_" + V.Str(entry.AuthoredId ?? (long)View._placedPropSpecs.Count));
                prop.transform.localRotation = Frame.YawRotation(entry.QuarterTurns * 90);
                var meta = prop.AddComponent<PlacedProp>();
                meta.placedPropId = entry.AuthoredId != null ? V.Str(entry.AuthoredId) : "";
                meta.gameplayPropId = entry.DressingBinding.IsEmpty ? entry.PropId : "";
                meta.visualId = entry.PropId;
                meta.quarterTurns = entry.QuarterTurns;
                meta.Spec = entry.Spec.DeepCopy();
                meta.GodotAuthoredPosition = entry.Position;
                View._placedPropSpecs.Add(entry.Spec);
                View._placedPropNodes.Add(meta);
            }
        }

        void AddAuthoredPortals(GeneratedShipLayout model, StructuralLayoutBuilder.Result structural, Transform root)
        {
            foreach (var plan in model.BuildPortalPlans())
            {
                var go = new GameObject(GodotNodeName.Validate("AuthoredPortal_" + V.Str(plan.Spec.Get("id", (long)plan.Index))));
                go.transform.SetParent(root, false);
                var portal = go.AddComponent<AuthoredPortalRuntime>();
                portal.Configure(plan.Spec, plan.Position);
                if (portal.portalKind == AuthoredPortalRuntime.LOCKED || portal.portalKind == AuthoredPortalRuntime.HATCH)
                {
                    StructuralModule blocker = FindStructuralPortalBlocker(structural, V.Str(plan.Spec.Get("edge_key", "")), portal.portalKind);
                    if (blocker != null) portal.BindStructuralBlocker(blocker);
                }
                View._authoredPortalNodes.Add(portal);
            }
        }

        static StructuralModule FindStructuralPortalBlocker(StructuralLayoutBuilder.Result structural, string edgeKey, string portalKind)
        {
            if (structural == null || edgeKey.Length == 0) return null;
            string expectedModule = portalKind == AuthoredPortalRuntime.LOCKED ? "doorway_frame_blocked_1x1" : "bulkhead_portal_2x1";
            foreach (var module in structural.Modules)
                if (module.layer == "edge" && module.placementKey == edgeKey && module.moduleId == expectedModule) return module;
            return null;
        }

        // ------------------------------------------------------------------ dressing (PKG-B5.1)

        static readonly Color PipeColor = new Color(0.45f, 0.48f, 0.42f);
        static readonly Color GrowthColor = new Color(0.42f, 0.16f, 0.22f);
        static readonly Color CrateColor = new Color(0.55f, 0.42f, 0.28f);

        /// <summary>Per-room light + fog marker at the room center and wall-slot dressing props (_apply_dressing_visuals).</summary>
        void AddDressingVisuals(GeneratedShipLayout model, Transform root)
        {
            if (model.RoomVariantDescriptors.IsEmpty || !(model.LayoutDoc.Get("rooms", new GdArray()) is GdArray)) return;
            var dressingRoot = new GameObject("DressingVisuals").transform;
            dressingRoot.SetParent(root, false);
            View.DressingRoot = dressingRoot;
            foreach (var item in model.BuildDressingPlan())
            {
                GameObject go;
                switch (item.Kind)
                {
                    case GeneratedShipLayout.DressingKind.Light:
                        {
                            var c = item.LightColor;
                            go = RuntimeVisualCatalog.AddOmniLight(dressingRoot, GodotNodeName.Validate(item.Name), Frame.ToUnity(item.Position),
                                new Color(c[0], c[1], c[2], 1f), (float)item.LightEnergy, (float)item.OmniRange, PhysicsLayers.Default).gameObject;
                            break;
                        }
                    case GeneratedShipLayout.DressingKind.Fog:
                        go = RuntimeVisualCatalog.AddMesh(dressingRoot, GodotNodeName.Validate(item.Name), RuntimeVisualCatalog.Sphere,
                            RuntimeVisualCatalog.Material(ToColor(item.Albedo), unshaded: true, transparent: true),
                            Frame.ToUnity(item.Position), Quaternion.identity,
                            new Vector3(item.SphereRadius * 2f, item.SphereHeight, item.SphereRadius * 2f), PhysicsLayers.Default, castShadows: false);
                        break;
                    default:
                        go = CreateDressingProp(item, dressingRoot);
                        break;
                }
                var meta = go.AddComponent<DressingVisual>();
                meta.roomId = item.RoomId;
                meta.dressing = item.Dressing;
                meta.role = item.Kind == GeneratedShipLayout.DressingKind.Light ? "light" : item.Kind == GeneratedShipLayout.DressingKind.Fog ? "fog" : "prop";
                meta.propDensity = item.PropDensity;
                meta.fogDensity = item.FogDensity;
                meta.Tint = item.Tint;
                meta.dressingKind = item.PropKind ?? "";
                meta.slotKind = item.Kind == GeneratedShipLayout.DressingKind.Prop ? "wall" : "";
                meta.slotIndex = item.SlotIndex;
                meta.SlotCell = item.SlotCell;
                meta.GodotPosition = item.Position;
                View._dressingNodes.Add(meta);
            }
        }

        /// <summary>Port of <c>_create_dressing_prop</c>: an unshaded crate / pipe / growth primitive, visual only.</summary>
        static GameObject CreateDressingProp(GeneratedShipLayout.DressingItem item, Transform parent)
        {
            var root = new GameObject(GodotNodeName.Validate(item.Name)) { layer = PhysicsLayers.Prop };
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Frame.ToUnity(item.Position);
            switch (item.PropKind)
            {
                case "pipe":
                    RuntimeVisualCatalog.AddMesh(root.transform, "DressingMesh", RuntimeVisualCatalog.Cylinder(0.12f, 0.12f, 1.4f),
                        RuntimeVisualCatalog.Material(PipeColor, unshaded: true), Frame.ToUnity(new Vec3(0f, 0.7f, 0f)),
                        Frame.Rotation(new Vec3(0f, 0f, 90f)), Vector3.one, PhysicsLayers.Prop);
                    break;
                case "growth":
                    RuntimeVisualCatalog.AddMesh(root.transform, "DressingMesh", RuntimeVisualCatalog.Sphere,
                        RuntimeVisualCatalog.Material(GrowthColor, unshaded: true), Frame.ToUnity(new Vec3(0f, 0.28f, 0f)),
                        Quaternion.identity, Vector3.one * 0.56f, PhysicsLayers.Prop);
                    break;
                default:
                    RuntimeVisualCatalog.AddMesh(root.transform, "DressingMesh", RuntimeVisualCatalog.Cube,
                        RuntimeVisualCatalog.Material(CrateColor, unshaded: true), Frame.ToUnity(new Vec3(0f, 0.225f, 0f)),
                        Quaternion.identity, Frame.SizeToUnity(new Vec3(0.55f, 0.45f, 0.55f)), PhysicsLayers.Prop);
                    break;
            }
            return root;
        }

        // ------------------------------------------------------------------ atmosphere / objectives

        /// <summary>Port of <c>_apply_slice_atmosphere</c>: only when the layout names a biome.</summary>
        void ApplySliceAtmosphere(GdDict layout, bool isAway)
        {
            string biomeId = V.Str(layout.Get("biome_id", ""));
            if (biomeId.Length == 0) return;
            string biomePath = $"res://data/procgen/biomes/{biomeId}.json";
            if (!CatalogRegistry.Exists(biomePath))
            {
                Debug.LogWarning("GeneratedShipLoader: atmosphere biome file missing: " + biomePath);
                return;
            }
            GdDict biome = CatalogRegistry.LoadDict(biomePath) ?? new GdDict();
            if (!(biome.Get("atmosphere", new GdDict()) is GdDict atmosphere)) return;
            View.AtmosphereSummary = AtmosphereApplier.Apply(View.transform, atmosphere, isAway);
        }

        void AddObjectiveVolumes(GeneratedShipLayout model, Transform objectiveRoot)
        {
            foreach (object objectiveVariant in model.ObjectiveSpecs)
            {
                if (!(objectiveVariant is GdDict objective)) continue;
                Vec3 worldPosition = objective.Get("position", Vec3.Zero) is Vec3 v ? v : Vec3.Zero;
                var go = new GameObject("ObjectiveVolume");
                go.transform.SetParent(objectiveRoot, false);
                var volume = go.AddComponent<GameplayObjectiveVolume>();
                volume.Configure(objective, worldPosition, GeneratedShipLayout.OBJECTIVE_TRIGGER_RADIUS);
                View._objectiveVolumes.Add(volume);
            }
        }

        // ------------------------------------------------------------------ paths / io

        /// <summary>
        /// Port of <c>_resolve_path</c>: <c>res://</c> maps onto the resource root (StreamingAssets, which mirrors the
        /// Godot project's <c>data/</c>), <c>user://</c> onto the user storage, absolute paths pass through, and
        /// relative paths try the working directory, <c>$PWD</c>, then <c>res://</c>. Returns '/'-separated paths.
        /// </summary>
        public static string ResolvePath(string rawPath)
        {
            rawPath = rawPath ?? "";
            string resolved;
            if (rawPath.StartsWith(ResPath.ResScheme, StringComparison.Ordinal)) resolved = Path.Combine(ResourceRoot(), ResPath.StripRes(rawPath));
            else if (rawPath.StartsWith(ResPath.UserScheme, StringComparison.Ordinal)) resolved = CoreServices.UserStorage.Globalize(rawPath);
            else if (Path.IsPathRooted(rawPath)) resolved = rawPath;
            else if (File.Exists(rawPath) || Directory.Exists(rawPath)) resolved = Path.GetFullPath(rawPath);
            else
            {
                string cwd = Environment.GetEnvironmentVariable("PWD") ?? "";
                string cwdPath = cwd.Length > 0 ? Path.Combine(cwd, rawPath) : "";
                resolved = cwdPath.Length > 0 && (File.Exists(cwdPath) || Directory.Exists(cwdPath)) ? cwdPath : Path.Combine(ResourceRoot(), rawPath);
            }
            return resolved.Replace('\\', '/');
        }

        static string ResourceRoot() =>
            CoreServices.Resources is FileSystemResourceReader reader ? reader.Root : Application.streamingAssetsPath;

        static GdDict LoadJsonDict(string path, string label)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            if (!(GdJson.ParseString(text) is GdDict parsed))
            {
                Debug.LogError($"{label} JSON is not an object: {path}");
                return new GdDict();
            }
            return parsed;
        }

        static Color ToColor(float[] rgba) => new Color(rgba[0], rgba[1], rgba[2], rgba.Length > 3 ? rgba[3] : 1f);

        static void DestroyObject(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
