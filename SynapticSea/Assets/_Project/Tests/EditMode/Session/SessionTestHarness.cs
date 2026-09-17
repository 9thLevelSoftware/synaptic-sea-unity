using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Headless stand-in for the player + camera (IRunSceneState).</summary>
    public sealed class FakeSceneState : IRunSceneState
    {
        public bool HasPlayer { get; set; }
        public Vec3 PlayerPosition { get; set; }
        public double PlayerDefaultMoveSpeed => 5.0;
        public double PlayerMoveSpeed { get; set; } = 5.0;
        public double MovementSpeedMultiplier = 1.0;
        public bool IsPlayerFrozen { get; private set; }
        public int SpawnCount;
        public int DespawnCount;

        public void TeleportPlayer(Vec3 position) => PlayerPosition = position;

        public void SpawnPlayer(Vec3 position)
        {
            HasPlayer = true;
            PlayerPosition = position;
            SpawnCount++;
        }

        public void DespawnPlayer()
        {
            HasPlayer = false;
            DespawnCount++;
        }

        public void SetMovementSpeedMultiplier(double multiplier) => MovementSpeedMultiplier = multiplier;
        public void SetPlayerFrozen(bool frozen) => IsPlayerFrozen = frozen;
    }

    /// <summary>A ship scene root with a Godot-frame transform (a plain Node3D parented to the session).</summary>
    public class FakeShipRoot : IShipSceneRoot, IShipInteriorView
    {
        public bool IsValid { get; set; } = true;
        public bool IsInsideTree { get; set; }
        public Xform3 Transform { get; set; } = Xform3.Identity;
        public Xform3 GlobalTransform => Transform;
        public readonly List<Vec3> RoomPositions = new List<Vec3>();
        public IReadOnlyList<Vec3> StructureRoomLocalPositions() => RoomPositions;
    }

    /// <summary>
    /// A loader view backed by the real <see cref="GeneratedShipLayout"/> (the pure half of generated_ship_loader.gd), so
    /// objective / loot / hazard positions are the same values the Runtime ShipView reports.
    /// </summary>
    public sealed class FakeLoaderView : FakeShipRoot, IShipLoaderView
    {
        public readonly GeneratedShipLayout Model;
        public readonly GdDict Layout;
        public readonly GdDict Gameplay;

        public FakeLoaderView(GdDict layout, GdDict gameplay, string gameplaySlicePath)
        {
            Layout = layout;
            Gameplay = gameplay;
            Model = new GeneratedShipLayout(layout, gameplay);
            Model.ObjectiveSpecs = Model.BuildObjectiveSpecs(gameplaySlicePath);
            Model.LootContainerSpecs = Model.BuildLootContainerSpecs();
            GdDict proto = layout.GetDictOrEmpty("prototype");
            Model.StartPosition = Model.RoomCenter(V.Str(gameplay.Get("start_room", proto.Get("start_room", ""))));
            Model.GoalPosition = Model.RoomCenter(V.Str(gameplay.Get("goal_room", proto.Get("goal_room", ""))));
            Model.BuildVerticalLinks();
            Model.BuildCoherenceMarkers();
            foreach (object roomV in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(roomV is GdDict room))
                    continue;
                foreach (object pV in room.GetArrayOrEmpty("structural_placements"))
                {
                    if (pV is GdDict p && p.Get("world_position", null) is GdArray a && a.Count >= 3)
                        RoomPositions.Add(new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2])));
                }
            }
        }

        public bool HasLoadedShip => true;
        public GdDict LayoutDoc => Model.LayoutDoc;
        public GdDict GameplayDoc => Model.GameplayDoc;
        public GdDict GetLayoutCopy() => Model.LayoutDoc.DeepCopy();
        public Xform3 GetStartTransform() => new Xform3(Basis3.Identity, Model.StartPosition == Vec3.Inf ? Vec3.Zero : Model.StartPosition);
        public Vec3 GetGoalPosition() => Model.GoalPosition;
        public GdArray GetObjectiveSpecsCopy() => Model.ObjectiveSpecs.DeepCopy();
        public GdArray GetLootContainerSpecsCopy() => Model.LootContainerSpecs.DeepCopy();
        public Vec3 GetRoomCenter(string roomId) => Model.GetRoomCenter(roomId);

        public IReadOnlyList<Vec3> GetBlockedRoutePositions()
        {
            var output = new List<Vec3>();
            foreach (GeneratedShipLayout.MarkerSpec m in Model.BlockedRoutes)
                output.Add(m.Position);
            return output;
        }

        public IReadOnlyList<Vec3> GetBreachZoneMarkers() => Model.BreachZoneMarkers;
        public GdArray GetBreachZoneSpecs() => Model.BreachZoneSpecs.DeepCopy();
        public IReadOnlyList<Vec3> GetFireZoneMarkers() => Model.FireZoneMarkers;
        public GdArray GetFireZoneSpecs() => Model.FireZoneSpecs.DeepCopy();
        public IReadOnlyList<Vec3> GetArcZoneMarkers() => Model.ArcZoneMarkers;
        public GdArray GetArcZoneSpecs() => Model.ArcZoneSpecs.DeepCopy();
        public GdArray GetRadiationZoneSpecs() => Model.RadiationZoneSpecs.DeepCopy();
        public GdDict GetRadiationZoneAt(Vec3 localPosition) => Model.GetRadiationZoneAt(localPosition);
        public GdArray AuthoredAtmosphereSpecs => Model.AuthoredAtmosphereSpecs;
        public GdDict GetAuthoredAtmosphereAt(Vec3 localPosition) => Model.GetAuthoredAtmosphereAt(localPosition);
        public double GetAuthoredAtmosphereDrainMultiplierAt(Vec3 localPosition) => Model.GetAuthoredAtmosphereDrainMultiplierAt(localPosition);
        public GdArray GetEncounterMarkers() => Model.GetEncounterMarkers();
        public IReadOnlyList<IAuthoredPortal> GetAuthoredPortals() => new List<IAuthoredPortal>();
        public long CountCollisionShapes() => 0;
        public GdArray DressingPropSlots() => new GdArray();
        public IReadOnlyList<IStructuralModuleNode> StructuralModuleNodes() => new List<IStructuralModuleNode>();
        public IEnumerable<IModuleSceneView> StructuralModuleViews() => new List<IModuleSceneView>();
    }

    /// <summary>Headless IShipSceneHost: loads documents through the real procgen/loader pure halves, tracks parenting.</summary>
    public sealed class FakeShipHost : IShipSceneHost
    {
        public readonly List<IShipSceneRoot> Attached = new List<IShipSceneRoot>();
        public readonly List<IShipSceneRoot> Freed = new List<IShipSceneRoot>();
        public int HomeLoads;

        public IShipLoaderView LoadHomeShip(string layoutPath, string kitPath, string gameplaySlicePath, out string failureReason)
        {
            failureReason = "";
            GdDict layout = CatalogRegistry.LoadDict(layoutPath);
            GdDict gameplay = CatalogRegistry.LoadDict(gameplaySlicePath);
            if (layout == null || gameplay == null)
            {
                failureReason = "missing documents";
                return null;
            }
            HomeLoads++;
            var view = new FakeLoaderView(layout, gameplay, gameplaySlicePath) { IsInsideTree = true };
            Attached.Add(view);
            return view;
        }

        public IShipLoaderView BuildShipScene(ShipDocuments documents)
        {
            if (documents == null || documents.Layout == null)
                return null;
            return new FakeLoaderView(documents.Layout, documents.GameplaySlice ?? new GdDict(), "");
        }

        public IShipSceneRoot BuildLifeboatScene(LifeBoatBuilder.BuildResult lifeboat)
        {
            var root = new FakeShipRoot();
            foreach (LifeBoatBuilder.RoomNode room in lifeboat.Rooms)
                root.RoomPositions.Add(room.Position);
            return root;
        }

        public void AttachShipRoot(IShipSceneRoot root)
        {
            if (root is FakeShipRoot f)
                f.IsInsideTree = true;
            if (!Attached.Contains(root))
                Attached.Add(root);
        }

        public void FreeShipRoot(IShipSceneRoot root)
        {
            if (root is FakeShipRoot f)
            {
                f.IsValid = false;
                f.IsInsideTree = false;
            }
            Attached.Remove(root);
            Freed.Add(root);
        }

        public void SetShipRootPosition(IShipSceneRoot root, Vec3 position) =>
            root.Transform = new Xform3(root.Transform.Basis, position);

        public void SetShipRootGlobalTransform(IShipSceneRoot root, Xform3 xform) => root.Transform = xform;

        public bool IsParentedToSession(IShipSceneRoot root) => Attached.Contains(root);
    }

    /// <summary>Records audio sink calls.</summary>
    public sealed class RecordingAudioSink : IAudioSink
    {
        public readonly List<string> Calls = new List<string>();
        public void ApplyBusVolumes(AudioBusConfig busConfig) => Calls.Add("bus_volumes");
        public void PlayOnBus(string busId, double volumeDb, string eventId, string streamPath) => Calls.Add("bus:" + busId + ":" + eventId);
        public void PlaySpatial(string eventId, Vec3 position, string busId, double volumeDb) => Calls.Add("spatial:" + eventId);
        public void SetMusicVolumeDb(double volumeDb) { }
        public void AttachListenerToPlayer() => Calls.Add("listener");
        public long ApplySpatialAttenuation(SpatialAudioResolver resolver) => 0;
    }

    /// <summary>Builds a headless session from the golden coherent_ship_001 documents.</summary>
    public static class SessionHarness
    {
        public const string GoldenDir = "res://data/procgen/golden/coherent_ship_001/";
        public const string GodotVersion = "4.7.1-stable (official)";

        public sealed class Rig
        {
            public RunSession Session;
            public FakeSceneState Scene;
            public FakeShipHost Host;
            public RecordingAudioSink Audio;
            public MemoryStorage Storage;
            public ManualClock Clock;
            public CollectingLog Log;
        }

        public static RunSessionDeps GoldenDeps(out Rig rig)
        {
            rig = new Rig
            {
                Scene = new FakeSceneState(),
                Host = new FakeShipHost(),
                Audio = new RecordingAudioSink(),
                Storage = new MemoryStorage(),
                Clock = new ManualClock(),
                Log = new CollectingLog(),
            };
            return new RunSessionDeps
            {
                Storage = rig.Storage,
                Clock = rig.Clock,
                Log = rig.Log,
                Engine = new FixedEngineInfo(GodotVersion),
                Scene = rig.Scene,
                ShipHost = rig.Host,
                AudioSink = rig.Audio,
                LayoutPath = GoldenDir + "layout.json",
                KitPath = "res://data/kits/ship_structural_v0.json",
                GameplaySlicePath = GoldenDir + "gameplay_slice.json",
                BlueprintPath = GoldenDir + "blueprint.json",
            };
        }

        public static Rig CreateGolden()
        {
            RunSessionDeps deps = GoldenDeps(out Rig rig);
            rig.Session = RunSession.Create(deps);
            return rig;
        }
    }
}
