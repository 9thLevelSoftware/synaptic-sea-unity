// Scene half of the GeneratedShipLoader queries scripts/procgen/playable_generated_ship.gd @ 96ecb2b0 made
// (generated_ship_loader.gd getters, the wrapper metas it walked and the AuthoredPortalRuntime children).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// <see cref="IShipLoaderView"/> over a built <see cref="ShipView"/>. Every query answers from the loader's pure half
    /// (<see cref="GeneratedShipLayout"/>), so positions are exactly the Godot loader's (Godot frame, local to the root).
    /// Room-node positions mirror the structural placements' <c>world_position</c>s, as the headless harness does.
    /// </summary>
    public sealed class ShipLoaderNode : SceneShipRoot, IShipLoaderView
    {
        public readonly ShipView View;
        readonly List<IStructuralModuleNode> _modules = new List<IStructuralModuleNode>();
        readonly List<IAuthoredPortal> _portals = new List<IAuthoredPortal>();

        public ShipLoaderNode(ShipView view) : base(view != null ? view.gameObject : null)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
            foreach (StructuralModule module in view.Modules)
                if (module != null) _modules.Add(new SceneModuleNode(module, this));
            foreach (AuthoredPortalRuntime portal in view.GetAuthoredPortalNodes())
                if (portal != null) _portals.Add(new ScenePortalNode(portal, this));
            GdDict layout = L.LayoutDoc;
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomV is GdDict room)) continue;
                foreach (object pV in room.GetArrayOrEmpty("structural_placements"))
                {
                    if (pV is GdDict p && p.Get("world_position", null) is GdArray a && a.Count >= 3)
                        RoomPositions.Add(new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2])));
                }
            }
        }

        GeneratedShipLayout L => View.Layout ?? new GeneratedShipLayout();

        public bool HasLoadedShip => View.HasLoadedShip();
        public GdDict LayoutDoc => L.LayoutDoc;
        public GdDict GameplayDoc => L.GameplayDoc;
        public GdDict GetLayoutCopy() => View.GetLayoutCopy();
        public Xform3 GetStartTransform() => View.GetStartTransform();
        public Vec3 GetGoalPosition() => View.GetGoalPosition();
        public GdArray GetObjectiveSpecsCopy() => View.GetObjectiveSpecsCopy();
        public GdArray GetLootContainerSpecsCopy() => View.GetLootContainerSpecsCopy();
        public Vec3 GetRoomCenter(string roomId) => View.GetRoomCenter(roomId);

        public IReadOnlyList<Vec3> GetBlockedRoutePositions()
        {
            var output = new List<Vec3>();
            foreach (GeneratedShipLayout.MarkerSpec m in L.BlockedRoutes) output.Add(m.Position);
            return output;
        }

        public void SetBlockedRouteCollisionEnabled(int index, bool enabled)
        {
            List<RuntimeMarker> nodes = View._blockedRouteNodes;
            if (index < 0 || index >= nodes.Count || nodes[index] == null) return;
            foreach (Collider c in nodes[index].GetComponentsInChildren<Collider>(true))
                c.enabled = enabled;
        }

        public IReadOnlyList<Vec3> GetBreachZoneMarkers() => L.BreachZoneMarkers;
        public GdArray GetBreachZoneSpecs() => View.GetBreachZoneSpecs();
        public IReadOnlyList<Vec3> GetFireZoneMarkers() => L.FireZoneMarkers;
        public GdArray GetFireZoneSpecs() => View.GetFireZoneSpecs();
        public IReadOnlyList<Vec3> GetArcZoneMarkers() => L.ArcZoneMarkers;
        public GdArray GetArcZoneSpecs() => View.GetArcZoneSpecs();
        public GdArray GetRadiationZoneSpecs() => View.GetRadiationZoneSpecs();
        public GdDict GetRadiationZoneAt(Vec3 localPosition) => View.GetRadiationZoneAt(localPosition);
        public GdArray AuthoredAtmosphereSpecs => L.AuthoredAtmosphereSpecs;
        public GdDict GetAuthoredAtmosphereAt(Vec3 localPosition) => View.GetAuthoredAtmosphereAt(localPosition);
        public double GetAuthoredAtmosphereDrainMultiplierAt(Vec3 localPosition) => View.GetAuthoredAtmosphereDrainMultiplierAt(localPosition);
        public GdArray GetEncounterMarkers() => View.GetEncounterMarkers();
        public IReadOnlyList<IAuthoredPortal> GetAuthoredPortals() => _portals;
        public long CountCollisionShapes() => View.CountCollisionShapes();

        public GdArray DressingPropSlots()
        {
            var output = new GdArray();
            foreach (DressingVisual d in View.GetDressingNodes())
            {
                if (d == null || d.role != "prop") continue;
                output.Add(new GdDict { { "name", d.name }, { "slot_cell", d.SlotCell != null ? d.SlotCell.DeepCopy() : new GdArray() } });
            }
            return output;
        }

        public IReadOnlyList<IStructuralModuleNode> StructuralModuleNodes() => _modules;
        public IEnumerable<IModuleSceneView> StructuralModuleViews() => _modules;
    }

    /// <summary>
    /// A structural wrapper (<see cref="StructuralModule"/>) as the integrity/work code walked it. Visual toggles go
    /// through <see cref="StructuralModule.SetIntegrity"/> when the <c>integrity_state</c> meta is written (the
    /// resolver's per-child calls are folded into that one variant switch); colliders follow the consequence table.
    /// </summary>
    public sealed class SceneModuleNode : IStructuralModuleNode
    {
        public readonly StructuralModule Module;
        readonly SceneShipRoot _root;
        readonly Dictionary<string, object> _meta = new Dictionary<string, object>(StringComparer.Ordinal);

        public SceneModuleNode(StructuralModule module, SceneShipRoot root)
        {
            Module = module;
            _root = root;
        }

        public string ModuleKey => Module != null ? Module.moduleKey ?? "" : "";
        public bool HasModuleKeyMeta => !string.IsNullOrEmpty(ModuleKey);
        public string ModuleKind => Module != null ? Module.moduleId ?? "" : "";
        public string StructuralPlacementId => Module != null ? Module.placementId ?? "" : "";
        public string NodeName => Module != null ? Module.name : "";

        public Vec3 GlobalPosition =>
            Module == null ? Vec3.Zero : _root.GlobalTransform * new Vec3(Module.godotPosition.x, Module.godotPosition.y, Module.godotPosition.z);

        public bool HasVisualGroup => Module != null && Module.intactVisual != null;

        public bool HasVisual(string childName)
        {
            if (Module == null) return false;
            if (Module.HasSingleVisual) return childName == IntegrityVisualResolver.VISUAL_LEGACY;
            switch (childName)
            {
                case IntegrityVisualResolver.VISUAL_INTACT: return Module.intactVisual != null;
                case IntegrityVisualResolver.VISUAL_DAMAGED: return Module.damagedVisual != null;
                case IntegrityVisualResolver.VISUAL_BREACHED: return Module.breachedVisual != null;
                default: return false;
            }
        }

        public void SetVisualVisible(string childName, bool visible)
        {
            // Folded into SetIntegrity (the integrity_state meta), which applies the same per-variant rule.
        }

        public void TintMeshes(string childName, double r, double g, double b, double a)
        {
            // Not ported (docs/port-status.md decision 19: the legacy per-state albedo tint).
        }

        public void SetCollisionEnabled(bool enabled)
        {
            if (Module == null) return;
            foreach (Collider c in Module.GetComponentsInChildren<Collider>(true)) c.enabled = enabled;
        }

        public void SetMeta(string key, object value)
        {
            _meta[key] = value;
            if (key == "integrity_state" && Module != null) Module.SetIntegrity(V.Str(value));
        }

        public object GetMeta(string key) => _meta.TryGetValue(key, out object v) ? v : null;
    }

    /// <summary>An <see cref="AuthoredPortalRuntime"/> as the session's portal port.</summary>
    public sealed class ScenePortalNode : IAuthoredPortal
    {
        public readonly AuthoredPortalRuntime Portal;
        readonly SceneShipRoot _root;

        public ScenePortalNode(AuthoredPortalRuntime portal, SceneShipRoot root)
        {
            Portal = portal;
            _root = root;
        }

        public bool IsValid => Portal != null && _root.IsValid;
        public string PortalId => Portal.portalId;
        public string PortalKind => Portal.portalKind;
        public bool IsExterior => Portal.isExterior;
        public string RequiredFlag() => Portal.RequiredFlag();
        public Vec3 GlobalPosition => _root.GlobalTransform * Portal.GodotPosition;
        public GdDict TryInteract(GdDict flags, Vec3 playerPosition) => Portal.TryInteractAt(flags, Frame.ToUnity(playerPosition));
        public void RestorePersistentState(bool unlocked, bool open) => Portal.RestorePersistentState(unlocked, open);
    }
}
