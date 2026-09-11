// Ported from scripts/systems/ship_instance.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// RUNTIME: the scene-root members <see cref="ShipInstance.InteriorAabb"/> reads beyond <see cref="IShipSceneRoot"/>.
    /// The Runtime ship root implements it next to IShipSceneRoot. When the scene root does not implement it, the ship
    /// is treated as having no built structure (the "unbuilt retained instance" fallback).
    /// </summary>
    public interface IShipInteriorView
    {
        /// <summary>
        /// LOCAL positions (Godot frame, float32) of the Node3D children of the scene root's <c>ShipStructure</c> node,
        /// in child order. Godot fell back to the first scene-root child that has children when there is no
        /// <c>ShipStructure</c>. Empty when neither exists.
        /// </summary>
        IReadOnlyList<Vec3> StructureRoomLocalPositions();
    }

    /// <summary>
    /// Lightweight per-ship handle. Bundles the identity + data + systems + scene root that genuinely must be
    /// per-ship for multi-ship travel/docking. Pure data plus a systems handle and a scene-root reference; it never
    /// adds or frees its own scene_root — the coordinator owns scene-tree lifecycle (single ownership).
    /// </summary>
    public class ShipInstance : IDockableShip
    {
        /// <summary>Generous per-room half-box in X/Z (covers 2x1 rooms + module chains).</summary>
        public const double ROOM_HALF_EXTENT = 4.0;

        /// <summary>Half deck height + headroom.</summary>
        public const double ROOM_HALF_HEIGHT = 3.0;

        public string ShipId = "";

        /// <summary>"" for the starting ship; cell:cell:index for traveled ships.</summary>
        public string MarkerId = "";

        public ShipBlueprint Blueprint;

        /// <summary>This ship's own systems.</summary>
        public ShipSystemsManager SystemsManager;

        /// <summary>RUNTIME: the generated/loaded scene tree (Godot Node3D); null when not instantiated.</summary>
        public IShipSceneRoot SceneRoot { get; set; }

        /// <summary>The layout dict scene_root was built from (for dock-port derivation).</summary>
        public GdDict BuiltLayout = new GdDict();

        /// <summary>
        /// 5a: <c>ship_root</c> is the ship's positioned root — it IS scene_root, exposed under the docking-domain name.
        /// </summary>
        public IShipSceneRoot ShipRoot
        {
            get => SceneRoot;
            set => SceneRoot = value;
        }

        // Phase 5 docking fields.
        public IDockableShip ParentShip { get; set; }
        public IList<IDockableShip> DockedShips { get; } = new List<IDockableShip>();
        public GdArray DockingPorts { get; set; } = new GdArray();

        /// <summary>Sub-project 5c: per-ship ownership/access. Lazily created; persisted under "access".</summary>
        public ShipAccessState Access;

        /// <summary>Sub-project 5d: per-ship hangar bay. Lazily created; persisted under "hangar" only when it has slots.</summary>
        public HangarBay Hangar;

        /// <summary>Sub-project #6 (cargo): per-ship cargo hold. Persisted under "inventory" only when it holds something.</summary>
        public ShipInventory Inventory;

        /// <summary>Sub-project #6 (carts): carts parked on this ship. Persisted under "carts" only when non-empty.</summary>
        public List<CartState> Carts = new List<CartState>();

        /// <summary>Sub-project #2: per-derelict objective loop state. Lazily created; null for the home ship.</summary>
        public DerelictObjectiveController ObjectiveController;

        /// <summary>Sub-project #3: ids of scattered loot containers already searched on this ship.</summary>
        public GdArray LootedContainerIds = new GdArray();

        /// <summary>
        /// Domain 2 follow-up: unsearched combat corpse drops. Each entry:
        /// {container_id, loot_table, seed_source, position: [x,y,z]} (ship-local pos).
        /// </summary>
        public GdArray PendingCorpseLoot = new GdArray();

        /// <summary>Domain 5: ids of sealed hatches already bypassed on this ship.</summary>
        public GdArray BypassedHatchIds = new GdArray();

        /// <summary>Authored portal interaction state (unlock identity is separate from open state).</summary>
        public GdArray AuthoredUnlockedPortalIds = new GdArray();
        public GdArray AuthoredOpenPortalIds = new GdArray();

        /// <summary>Task 06: per-ship combat/threat persistence.</summary>
        public GdDict CombatSummary = new GdDict();

        /// <summary>Derelict-side fire: per-ship authoritative FireSuppressionState. Lazily created.</summary>
        public FireSuppressionState Fire;

        /// <summary>Per-ship electrical arc summary (live ElectricalArcState belongs to the coordinator).</summary>
        public GdDict ArcSummary = new GdDict();

        /// <summary>True once the coordinator has run its one-time environmental fire pre-seed for this derelict.</summary>
        public bool FireSeeded = false;

        /// <summary>True once the coordinator has run its one-time variant-driven breach pre-seed for this derelict.</summary>
        public bool BreachSeeded = false;

        /// <summary>Scene-level breach environment belongs to this ship.</summary>
        public GdDict BreachEnvironmentSummary = new GdDict();

        /// <summary>Live Persistent Ships Phase 2a: per-ship structural state, mirroring <see cref="Fire"/>.</summary>
        public HullIntegrityState Hull;
        public WebInfestationState Web;

        /// <summary>Live Persistent Ships Phase 1: world_time at which this ship's sim was last advanced.</summary>
        public double LastSimTime = 0.0;

        /// <summary>PKG-D6.1: sparse pillar deltas that must survive leave/revisit.</summary>
        public GdDict ModuleIntegritySummary = new GdDict();
        public GdDict ComponentPlacementSummary = new GdDict();

        /// <summary>The GDScript load()-self-reference factory.</summary>
        public static ShipInstance Create(string pShipId, string pMarkerId, ShipBlueprint pBlueprint, ShipSystemsManager pSystemsManager, IShipSceneRoot pSceneRoot)
        {
            var inst = new ShipInstance();
            inst.ShipId = pShipId;
            inst.MarkerId = pMarkerId;
            inst.Blueprint = pBlueprint;
            inst.SystemsManager = pSystemsManager;
            inst.SceneRoot = pSceneRoot;
            return inst;
        }

        public GdDict GetSummary()
        {
            var bpDict = new GdDict();
            if (Blueprint != null)
                bpDict = Blueprint.ToDict();
            var sysDict = new GdDict();
            if (SystemsManager != null)
                sysDict = SystemsManager.GetSummary();
            var result = new GdDict
            {
                { "ship_id", ShipId },
                { "marker_id", MarkerId },
                { "blueprint", bpDict },
                { "systems", sysDict },
            };
            if (ObjectiveController != null)
                result["objective"] = ObjectiveController.GetSummary();
            if (!LootedContainerIds.IsEmpty)
                result["looted_containers"] = LootedContainerIds.ShallowCopy();
            if (!PendingCorpseLoot.IsEmpty)
                result["pending_corpse_loot"] = PendingCorpseLoot.DeepCopy();
            if (!BypassedHatchIds.IsEmpty)
                result["bypassed_hatches"] = BypassedHatchIds.ShallowCopy();
            if (!AuthoredUnlockedPortalIds.IsEmpty)
                result["authored_unlocked_portals"] = AuthoredUnlockedPortalIds.ShallowCopy();
            if (!AuthoredOpenPortalIds.IsEmpty)
                result["authored_open_portals"] = AuthoredOpenPortalIds.ShallowCopy();
            if (!CombatSummary.IsEmpty)
                result["combat"] = CombatSummary.DeepCopy();
            if (Access != null)
                result["access"] = Access.GetSummary();
            if (Hangar != null && Hangar.SlotCount > 0)
                result["hangar"] = Hangar.GetSummary();
            if (HasCargo())
                result["inventory"] = Inventory.GetSummary();
            if (Carts.Count > 0)
            {
                var cartDicts = new GdArray();
                foreach (CartState c in Carts)
                    cartDicts.Add(c.GetSummary());
                result["carts"] = cartDicts;
            }
            // Persist whenever seeded or vented, not only while something still burns. A vents-only / extinguished
            // derelict keeps fire_seeded=true; omitting the blob would skip seed on load and drop vented_compartments.
            if (Fire != null && (FireSeeded || HasFire() || !Fire.VentedCompartments.IsEmpty))
                result["fire"] = Fire.GetSummary();
            if (!ArcSummary.IsEmpty)
                result["arc"] = ArcSummary.DeepCopy();
            if (FireSeeded)
                result["fire_seeded"] = true;
            if (BreachSeeded)
                result["breach_seeded"] = true;
            if (!BreachEnvironmentSummary.IsEmpty)
                result["breach_environment"] = BreachEnvironmentSummary.DeepCopy();
            if (HasHull())
                result["hull"] = Hull.GetSummary();
            if (Web != null && (!Web.AttachedToWeb || Web.Coverage > 0.0))
                result["web"] = Web.GetSummary();
            if (LastSimTime != 0.0)
                result["last_sim_time"] = LastSimTime;
            if (!ModuleIntegritySummary.IsEmpty)
                result["module_integrity"] = ModuleIntegritySummary.DeepCopy();
            if (!ComponentPlacementSummary.IsEmpty)
                result["component_placement"] = ComponentPlacementSummary.DeepCopy();
            return result;
        }

        static GdArray StringsOf(GdArray source)
        {
            var output = new GdArray();
            foreach (object v in source)
                output.Add(V.Str(v));
            return output;
        }

        public bool ApplySummary(object summaryVariant)
        {
            if (!(summaryVariant is GdDict summary) || summary.IsEmpty)
                return false;
            ShipId = V.Str(summary.Get("ship_id", ShipId));
            MarkerId = V.Str(summary.Get("marker_id", MarkerId));
            object bpDict = summary.Get("blueprint", null);
            if (bpDict is GdDict bp && !bp.IsEmpty)
                Blueprint = ShipBlueprint.FromDict(bp);
            object sysDict = summary.Get("systems", null);
            if (sysDict is GdDict sys && !sys.IsEmpty)
            {
                if (SystemsManager == null)
                {
                    SystemsManager = new ShipSystemsManager();
                    SystemsManager.Configure(SystemsManager.LoadDefinitions(), 0, 0);
                }
                SystemsManager.ApplySummary(sys);
            }
            object objSummary = summary.Get("objective", null);
            if (objSummary is GdDict obj && !obj.IsEmpty)
            {
                if (ObjectiveController == null)
                    ObjectiveController = DerelictObjectiveController.Create();
                ObjectiveController.ApplySummary(obj);
            }
            if (summary.Get("looted_containers", null) is GdArray looted)
                LootedContainerIds = StringsOf(looted);
            if (summary.Get("pending_corpse_loot", null) is GdArray corpse)
            {
                PendingCorpseLoot = new GdArray();
                foreach (object entry in corpse)
                {
                    if (entry is GdDict e)
                        PendingCorpseLoot.Add(e.DeepCopy());
                }
            }
            if (summary.Get("bypassed_hatches", null) is GdArray bypassed)
                BypassedHatchIds = StringsOf(bypassed);
            if (summary.Get("authored_unlocked_portals", null) is GdArray unlockedPortals)
                AuthoredUnlockedPortalIds = StringsOf(unlockedPortals);
            if (summary.Get("authored_open_portals", null) is GdArray openPortals)
                AuthoredOpenPortalIds = StringsOf(openPortals);
            if (summary.Get("combat", null) is GdDict combat)
                CombatSummary = combat.DeepCopy();
            if (summary.Get("access", null) is GdDict access && !access.IsEmpty)
                GetAccess().ApplySummary(access);
            if (summary.Get("hangar", null) is GdDict hangar && !hangar.IsEmpty)
                GetHangar().ApplySummary(hangar);
            if (summary.Get("inventory", null) is GdDict inventory && !inventory.IsEmpty)
                GetInventory().ApplySummary(inventory);
            if (summary.Get("carts", null) is GdArray cartsArr)
            {
                Carts = new List<CartState>();
                foreach (object cd in cartsArr)
                {
                    if (cd is GdDict cdDict)
                    {
                        var cart = CartState.Create();
                        cart.ApplySummary(cdDict);
                        Carts.Add(cart);
                    }
                }
            }
            if (summary.Get("fire", null) is GdDict fire && !fire.IsEmpty)
                GetFire().ApplySummary(fire);
            if (summary.Get("arc", null) is GdDict arc)
                ArcSummary = arc.DeepCopy();
            FireSeeded = V.Bool(summary.Get("fire_seeded", FireSeeded));
            BreachSeeded = V.Bool(summary.Get("breach_seeded", BreachSeeded));
            if (summary.Get("breach_environment", null) is GdDict breachEnv)
                BreachEnvironmentSummary = breachEnv.DeepCopy();
            if (summary.Get("hull", null) is GdDict hull && !hull.IsEmpty)
                GetHull().ApplySummary(hull);
            if (summary.Get("web", null) is GdDict web && !web.IsEmpty)
                GetWeb().ApplySummary(web);
            else if (summary.Has("web_attached"))
                GetWeb().AttachedToWeb = V.Bool(summary.Get("web_attached", true));
            LastSimTime = V.F64(summary.Get("last_sim_time", 0.0));
            // PKG-D6.1: pillar sparse packs (empty/missing = pristine regenerate-from-seed).
            if (summary.Get("module_integrity", null) is GdDict mi)
                ModuleIntegritySummary = mi.DeepCopy();
            if (summary.Get("component_placement", null) is GdDict cp)
                ComponentPlacementSummary = cp.DeepCopy();
            return true;
        }

        /// <summary>Returns this ship's DerelictObjectiveController, creating it on first access.</summary>
        public DerelictObjectiveController GetObjectiveController()
        {
            if (ObjectiveController == null)
                ObjectiveController = DerelictObjectiveController.Create();
            return ObjectiveController;
        }

        /// <summary>Returns this ship's ShipAccessState, creating it on first access.</summary>
        public ShipAccessState GetAccess()
        {
            if (Access == null)
                Access = ShipAccessState.Create();
            return Access;
        }

        /// <summary>Returns this ship's HangarBay, creating an empty (0-slot) one on first access.</summary>
        public HangarBay GetHangar()
        {
            if (Hangar == null)
                Hangar = HangarBay.Create(0, 0);
            return Hangar;
        }

        /// <summary>True iff this ship has a configured bay (at least one slot).</summary>
        public bool HasHangar() => Hangar != null && Hangar.SlotCount > 0;

        /// <summary>Returns this ship's ShipInventory cargo hold, creating an empty one on first access.</summary>
        public ShipInventory GetInventory()
        {
            if (Inventory == null)
                Inventory = ShipInventory.Create();
            return Inventory;
        }

        /// <summary>True iff this ship's hold exists and holds at least one item.</summary>
        public bool HasCargo() => Inventory != null && !Inventory.Items.IsEmpty;

        /// <summary>Returns this ship's FireSuppressionState, creating a bare one on first access.</summary>
        public FireSuppressionState GetFire()
        {
            if (Fire == null)
                Fire = new FireSuppressionState();
            return Fire;
        }

        /// <summary>True iff this ship has at least one burning compartment.</summary>
        public bool HasFire() => Fire != null && !Fire.GetBurningCompartments().IsEmpty;

        /// <summary>Returns this ship's HullIntegrityState, creating a bare one on first access.</summary>
        public HullIntegrityState GetHull()
        {
            if (Hull == null)
                Hull = new HullIntegrityState();
            return Hull;
        }

        /// <summary>True iff this ship has a configured hull (at least one compartment).</summary>
        public bool HasHull() => Hull != null && !Hull.Compartments.IsEmpty;

        /// <summary>Returns this ship's WebInfestationState, creating a bare one on first access.</summary>
        public WebInfestationState GetWeb()
        {
            if (Web == null)
                Web = new WebInfestationState();
            return Web;
        }

        /// <summary>True iff this ship is still in contact with the biomatter web (web model is authoritative).</summary>
        public bool IsWebAttached() => GetWeb().AttachedToWeb;

        /// <summary>Returns this ship's live carts list (parked carts).</summary>
        public List<CartState> GetCarts() => Carts;

        /// <summary>A "working vessel" can be piloted: its own propulsion system is operational.</summary>
        public bool IsWorkingVessel() => SystemsManager != null && SystemsManager.IsOperational("propulsion");

        /// <summary>Validation/runtime seam: the layout dict this ship's scene_root was built from.</summary>
        public GdDict BlueprintLayoutForValidation() => BuiltLayout;

        /// <summary>
        /// World-space AABB enclosing this ship's interior, derived from the built ShipStructure's room-node LOCAL
        /// positions (robust off-tree / headless). The merged local AABB is transformed by scene_root's world
        /// transform. Null/empty scene_root or no room nodes -> zero-size AABB at the root origin.
        /// </summary>
        public Aabb3 InteriorAabb()
        {
            if (SceneRoot == null || !SceneRoot.IsValid)
                return new Aabb3(Vec3.Zero, Vec3.Zero);
            // RUNTIME: Godot walked scene_root/ShipStructure (or the first child with children) and read each
            // Node3D room child's local position; the Runtime root supplies them through IShipInteriorView.
            IReadOnlyList<Vec3> roomPositions = (SceneRoot as IShipInteriorView)?.StructureRoomLocalPositions();
            var local = new Aabb3(Vec3.Zero, Vec3.Zero);
            bool seeded = false;
            if (roomPositions != null)
            {
                var half = new Vec3(ROOM_HALF_EXTENT, ROOM_HALF_HEIGHT, ROOM_HALF_EXTENT);
                foreach (Vec3 p in roomPositions)
                {
                    var box = new Aabb3(p - half, half * 2.0f);
                    if (!seeded)
                    {
                        local = box;
                        seeded = true;
                    }
                    else
                    {
                        local = Merge(local, box);
                    }
                }
            }
            if (!seeded)
            {
                Vec3 o = SceneRoot.IsInsideTree ? SceneRoot.GlobalTransform.Origin : SceneRoot.Transform.Origin;
                return new Aabb3(o, Vec3.Zero);
            }
            Xform3 xform = SceneRoot.IsInsideTree
                ? SceneRoot.GlobalTransform
                : new Xform3(Basis3.Identity, SceneRoot.Transform.Origin);
            return XformAabb(xform, local);
        }

        /// <summary>Godot <c>AABB.merge()</c> (core/math/aabb.cpp merge_with), float32.</summary>
        static Aabb3 Merge(Aabb3 a, Aabb3 b)
        {
            Vec3 beg1 = a.Position;
            Vec3 beg2 = b.Position;
            Vec3 end1 = a.Size + beg1;
            Vec3 end2 = b.Size + beg2;
            var min = new Vec3(
                beg1.X < beg2.X ? beg1.X : beg2.X,
                beg1.Y < beg2.Y ? beg1.Y : beg2.Y,
                beg1.Z < beg2.Z ? beg1.Z : beg2.Z);
            var max = new Vec3(
                end1.X > end2.X ? end1.X : end2.X,
                end1.Y > end2.Y ? end1.Y : end2.Y,
                end1.Z > end2.Z ? end1.Z : end2.Z);
            return new Aabb3(min, max - min);
        }

        static float Axis(Vec3 v, int i) => i == 0 ? v.X : (i == 1 ? v.Y : v.Z);

        /// <summary>Godot <c>Transform3D * AABB</c> (Transform3D::xform(AABB), core/math/transform_3d.h), float32.</summary>
        static Aabb3 XformAabb(Xform3 xform, Aabb3 aabb)
        {
            Vec3 min = aabb.Position;
            Vec3 max = aabb.Position + aabb.Size;
            var tmin = new float[3];
            var tmax = new float[3];
            for (int i = 0; i < 3; i++)
            {
                Vec3 row = i == 0 ? xform.Basis.Row0 : (i == 1 ? xform.Basis.Row1 : xform.Basis.Row2);
                tmin[i] = tmax[i] = Axis(xform.Origin, i);
                for (int j = 0; j < 3; j++)
                {
                    float e = (float)(Axis(row, j) * Axis(min, j));
                    float f = (float)(Axis(row, j) * Axis(max, j));
                    if (e < f)
                    {
                        tmin[i] = (float)(tmin[i] + e);
                        tmax[i] = (float)(tmax[i] + f);
                    }
                    else
                    {
                        tmin[i] = (float)(tmin[i] + f);
                        tmax[i] = (float)(tmax[i] + e);
                    }
                }
            }
            var position = new Vec3(tmin[0], tmin[1], tmin[2]);
            var end = new Vec3(tmax[0], tmax[1], tmax[2]);
            return new Aabb3(position, end - position);
        }
    }
}
