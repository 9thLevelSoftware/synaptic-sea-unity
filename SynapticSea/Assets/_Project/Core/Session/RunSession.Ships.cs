// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: per-ship runtime + hub recompute (1580-1707),
// occupancy / login / piloting (1905-2006), systems ops (2155-2167), player carry + subtree re-peg (2295-2357), dock
// barriers / bridge terminals / hangar / cargo / cart controls (2361-2637), encumbrance + equipment (2642-2736), hangar
// dock/launch (2740-2896).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // ------------------------------------------------------------------ per-ship runtime
        List<string> ManagerBrokenSystems()
        {
            var broken = new List<string>();
            if (ShipSystemsManager == null)
                return broken;
            foreach (string subsystemId in new[] { "life_support", "propulsion" })
            {
                if (!ShipSystemsManager.Systems.ContainsKey(subsystemId) || !ShipSystemsManager.IsOperational(subsystemId))
                    broken.Add(subsystemId);
            }
            return broken;
        }

        /// <summary>Foundation contagion seed: away + docked to a still-web-attached derelict boosts hub web growth.</summary>
        bool ActiveDerelictWebAttached() => AwayFromStart && CurrentShip != null && CurrentShip.IsWebAttached();

        void AdvanceShip(ShipInstance ship, double delta)
        {
            if (ship == null)
                return;
            RuntimeFor(ship).Advance(delta, WorldTime);
        }

        /// <summary>A ShipRuntime for a ship handle; the hub injects the coordinator-owned hull/web/module models.</summary>
        ShipRuntime RuntimeFor(ShipInstance ship)
        {
            bool isHome = ship == HomeShip;
            var opts = new ShipRuntimeOptions { IsHome = isHome };
            if (isHome)
            {
                opts.HullOverride = HullIntegrityState;
                opts.WebOverride = HullWebState;
                opts.ContactBoostProvider = ActiveDerelictWebAttached;
                opts.ModuleIntegrity = ModuleIntegrityMap;
            }
            var rt = new ShipRuntime();
            rt.Configure(ship, opts);
            return rt;
        }

        /// <summary>Live Persistent Ships Phase 4: fast-forward an absent ship's sim.</summary>
        void CatchUpShip(ShipInstance inst)
        {
            if (inst == null || inst == HomeShip)
                return;
            RuntimeFor(inst).CatchUp(WorldTime);
        }

        /// <summary><c>_recompute_expanded_ship_systems(delta)</c>: power grid, propulsion, life support, stations, sustenance.</summary>
        void RecomputeExpandedShipSystems(double delta)
        {
            if (PowerGridState == null)
                return;
            double powerHealth = 0.0;
            if (ShipSystemsManager != null && ShipSystemsManager.GetSystem("power") != null)
                powerHealth = ShipSystemsManager.GetSystem("power").Health();
            PowerGridState.Rebalance(powerHealth, ManagerBrokenSystems());
            if (PropulsionExpandedState != null && HullIntegrityState != null)
            {
                PropulsionExpandedState.Tick(delta, new GdDict
                {
                    { "powered_ratio", PowerGridState.GetAllocationRatio("propulsion") },
                    { "manager_operational", ShipSystemsManager != null && ShipSystemsManager.IsOperational("propulsion") },
                    { "hull_penalty", 1.0 - HullIntegrityState.AverageIntegrity() },
                });
            }
            if (LifeSupportExpandedState != null && HullIntegrityState != null)
            {
                double recycledWater = 0.0;
                if (WaterRecyclerState != null)
                    recycledWater = WaterRecyclerState.OutputReady;
                LifeSupportExpandedState.Tick(delta, new GdDict
                {
                    { "powered_ratio", PowerGridState.GetAllocationRatio("life_support") },
                    { "breach_count", DerivedBreachCount() },
                    { "recycled_water", recycledWater },
                });
            }
            if (CraftingState != null)
            {
                bool stationsPowered = PowerGridState.GetAllocationRatio("stations") > 0.0;
                if (ShipModificationState != null && !ShipModificationState.IsPowerBudgetOk())
                    stationsPowered = false;
                foreach (string kind in CRAFTING_STATION_KINDS)
                    CraftingState.GetOrCreateStation(kind).SetPower(stationsPowered);
                foreach (CraftingStation st in CraftingStations)
                {
                    if (st.IsValid)
                        st.SetPowered(stationsPowered);
                }
                if (CraftingState.Tick(delta))
                    OnCraftCompleted();
            }
            if (ExtinguisherRechargePort != null && ExtinguisherRechargePort.IsValid)
                ExtinguisherRechargePort.SetPowered(PowerGridState.GetAllocationRatio("stations") > 0.0);
            if (SustenanceState != null)
            {
                SustenanceState.Tick(delta, new GdDict
                {
                    { "powered_ratio", PowerGridState.GetAllocationRatio("sustenance") },
                    { "hydroponics_summary", HydroponicsState != null ? HydroponicsState.GetSummary() : new GdDict() },
                    { "water_recycler_summary", WaterRecyclerState != null ? WaterRecyclerState.GetSummary() : new GdDict() },
                    { "meals_active", CraftingState != null && CraftingState.IsCrafting() && (CraftingState.GetActiveStationKind() == "kitchen" || CraftingState.GetActiveStationKind() == "synthesizer") },
                });
            }
        }

        GdDict ExpandedShipSystemsSummary() => new GdDict
        {
            { "power_grid_summary", PowerGridState != null ? PowerGridState.GetSummary() : new GdDict() },
            { "life_support_state_summary", LifeSupportExpandedState != null ? LifeSupportExpandedState.GetSummary() : new GdDict() },
            { "hull_integrity_summary", HullIntegrityState != null ? HullIntegrityState.GetSummary() : new GdDict() },
            { "web_infestation_summary", HullWebState != null ? HullWebState.GetSummary() : new GdDict() },
            { "fire_suppression_summary", FireSuppressionState != null ? FireSuppressionState.GetSummary() : new GdDict() },
            { "extinguisher_summary", ExtinguisherState != null ? ExtinguisherState.GetSummary() : new GdDict() },
            { "propulsion_state_summary", PropulsionExpandedState != null ? PropulsionExpandedState.GetSummary() : new GdDict() },
            { "sustenance_state_summary", SustenanceState != null ? SustenanceState.GetSummary() : new GdDict() },
        };

        public GdDict GetShipSystemsExpandedSummary() => ExpandedShipSystemsSummary();

        /// <summary>Manual power route + immediate recompute (was <c>set_manual_power_route_for_validation</c>).</summary>
        public bool SetManualPowerRoute(string subsystemId, double units)
        {
            if (PowerGridState == null)
                return false;
            bool ok = PowerGridState.SetManualRoute(subsystemId, units);
            RecomputeExpandedShipSystems(0.0);
            RefreshTrackerSystemStatusLines();
            return ok;
        }

        /// <summary>Force a hub hull breach (was <c>force_hull_breach_for_validation</c>).</summary>
        public bool ForceHullBreach(string compartmentId, double amount = 0.6)
        {
            if (HullIntegrityState == null)
                return false;
            bool ok = HullIntegrityState.DamageCompartment(compartmentId, amount, true);
            if (ok)
                EmitMetaHullGroan();
            RecomputeExpandedShipSystems(0.0);
            RefreshTrackerSystemStatusLines();
            return ok;
        }

        /// <summary>Seal a hub hull breach (was <c>seal_hull_breach_for_validation</c>).</summary>
        public bool SealHullBreach(string compartmentId, double amount = 1.0)
        {
            if (HullIntegrityState == null)
                return false;
            bool ok = HullIntegrityState.SealCompartment(compartmentId, amount);
            RecomputeExpandedShipSystems(0.0);
            RefreshTrackerSystemStatusLines();
            return ok;
        }

        /// <summary>Ignite in the home fire model with a recompute (was <c>ignite_compartment_for_validation</c>).</summary>
        public bool IgniteCompartment(string compartmentId, double intensity = 1.0)
        {
            if (FireSuppressionState == null)
                return false;
            bool ok = FireSuppressionState.Ignite(compartmentId, intensity);
            RecomputeExpandedShipSystems(0.0);
            RefreshTrackerSystemStatusLines();
            return ok;
        }

        // ------------------------------------------------------------------ occupancy / piloting
        bool ParentedToSession(IShipSceneRoot root) => RootValid(root) && (ShipHost == null || ShipHost.IsParentedToSession(root));

        List<object> OccupancyEntries()
        {
            var entries = new List<object>();
            if (PilotedShip != null && PilotedShip.SceneRoot != null && ParentedToSession(PilotedShip.SceneRoot))
                entries.Add(new OccupancyEntry(PilotedShip, PilotedShip.InteriorAabb()));
            if (LifeboatShip != null && LifeboatShip != PilotedShip && LifeboatShip.SceneRoot != null && ParentedToSession(LifeboatShip.SceneRoot))
                entries.Add(new OccupancyEntry(LifeboatShip, LifeboatShip.InteriorAabb()));
            if (HomeShip != null && RootValid(HomeShip.SceneRoot) && ShipBoardable(HomeShip))
                entries.Add(new OccupancyEntry(HomeShip, HomeShip.InteriorAabb()));
            if (CurrentShip != null && CurrentShip != HomeShip && RootValid(CurrentShip.SceneRoot) && ShipBoardable(CurrentShip))
                entries.Add(new OccupancyEntry(CurrentShip, CurrentShip.InteriorAabb()));
            return entries;
        }

        /// <summary>True if <paramref name="inst"/> can be boarded: no dock barrier, or its barrier is open.</summary>
        bool ShipBoardable(ShipInstance inst)
        {
            if (inst == null)
                return false;
            if (inst == PilotedShip)
                return true;
            foreach (DockPortBarrier b in DockBarriers)
            {
                if (b.IsValid && b.MarkerId == inst.MarkerId)
                    return b.Opened;
            }
            return true;
        }

        /// <summary>Derive current_occupancy from the player's world position; keeps away_from_start consistent.</summary>
        public void RecomputeOccupancy()
        {
            if (HomeShip == null)
                return;
            ShipInstance resolved = HomeShip;
            if (HasPlayer)
            {
                if (ShipOccupancy.Resolve(PlayerPos, OccupancyEntries()) is ShipInstance r)
                    resolved = r;
            }
            CurrentOccupancy = resolved;
            bool atHomeComplex = CurrentOccupancy == HomeShip
                || (PilotedShip != null && CurrentOccupancy == PilotedShip && PilotedShip.ParentShip == HomeShip);
            AwayFromStart = !atHomeComplex;
        }

        /// <summary>A bridge terminal requested login: claim + pilot a working vessel.</summary>
        void OnLoginRequested(string shipId)
        {
            ShipInstance inst = FindShipById(shipId);
            if (inst == null)
                return;
            if (!inst.IsWorkingVessel())
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return;
            }
            inst.GetAccess().Claim(PLAYER_LOCAL_ID);
            SetPilotedShip(inst);
            PlaySfx(AudioEventSeam.UI_PANEL_OPEN);
        }

        /// <summary>Re-points piloted_ship (gated on access). Returns {success, reason}.</summary>
        public GdDict SetPilotedShip(ShipInstance inst)
        {
            if (inst == null)
                return new GdDict { { "success", false }, { "reason", "unknown_ship" } };
            if (!inst.GetAccess().HasAccess(PLAYER_LOCAL_ID))
                return new GdDict { { "success", false }, { "reason", "no_access" } };
            PilotedShip = inst;
            RecomputeOccupancy();
            return new GdDict { { "success", true }, { "reason", "ok" } };
        }

        /// <summary>Travel capability comes from the PILOTED ship's systems (fallback: the coordinator's starting manager).</summary>
        GdDict CurrentSystemsOps()
        {
            ShipSystemsManager mgr = PilotedShip != null && PilotedShip.SystemsManager != null ? PilotedShip.SystemsManager : ShipSystemsManager;
            bool propulsionOk = mgr != null && mgr.IsOperational("propulsion");
            if (PropulsionExpandedState != null)
                propulsionOk = propulsionOk && PropulsionExpandedState.CanPropel();
            return new GdDict
            {
                { "navigation", mgr != null && mgr.IsOperational("navigation") },
                { "scanners", mgr != null && mgr.IsOperational("scanners") },
                { "propulsion", propulsionOk },
            };
        }

        /// <summary>Force-repair every subcomponent of the shared manager (was <c>force_repair_all_for_validation</c>).</summary>
        public void ForceRepairAll()
        {
            if (ShipSystemsManager == null)
                return;
            foreach (string sid in new List<string>(ShipSystemsManager.Systems.Keys))
            {
                ShipSystem sys = ShipSystemsManager.GetSystem(sid);
                if (sys == null)
                    continue;
                foreach (ShipSubcomponent sub in sys.Subcomponents)
                    ShipSystemsManager.ForceRepair(sid, sub.SubcomponentId);
            }
        }

        // ------------------------------------------------------------------ player carry / subtree re-peg
        GdDict CapturePlayerCarry()
        {
            if (PilotedShip == null || !RootValid(PilotedShip.SceneRoot))
                return new GdDict();
            IShipSceneRoot root = PilotedShip.SceneRoot;
            if (!HasPlayer || !root.IsInsideTree)
                return new GdDict();
            Vec3 local = SessionMath.AffineInverse(root.GlobalTransform) * PlayerPos;
            return new GdDict { { "local", local } };
        }

        void ApplyPlayerCarry(GdDict carry)
        {
            if (carry == null || carry.IsEmpty || PilotedShip == null || !RootValid(PilotedShip.SceneRoot))
                return;
            IShipSceneRoot root = PilotedShip.SceneRoot;
            if (!HasPlayer || !root.IsInsideTree)
                return;
            TeleportPlayer(root.GlobalTransform * (Vec3)carry["local"]);
        }

        sealed class SubtreeCapture
        {
            public ShipInstance Inst;
            public Xform3 LocalXform;
        }

        /// <summary>Captures every transitive dock descendant of the piloted ship relative to its root (DFS).</summary>
        List<SubtreeCapture> CaptureSubtree()
        {
            var output = new List<SubtreeCapture>();
            if (PilotedShip == null || !RootValid(PilotedShip.SceneRoot))
                return output;
            IShipSceneRoot rootNode = PilotedShip.SceneRoot;
            if (!rootNode.IsInsideTree)
                return output;
            Xform3 inv = SessionMath.AffineInverse(rootNode.GlobalTransform);
            var stack = new List<IDockableShip>(PilotedShip.DockedShips);
            var seen = new HashSet<IDockableShip>();
            while (stack.Count > 0)
            {
                IDockableShip child = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (child == null || seen.Contains(child))
                    continue;
                seen.Add(child);
                if (RootInTree(child.SceneRoot) && child is ShipInstance childInst)
                    output.Add(new SubtreeCapture { Inst = childInst, LocalXform = SessionMath.Mul(inv, child.SceneRoot.GlobalTransform) });
                foreach (IDockableShip grandchild in child.DockedShips)
                {
                    if (grandchild != null && !seen.Contains(grandchild))
                        stack.Add(grandchild);
                }
            }
            return output;
        }

        void RepositionSubtree(List<SubtreeCapture> captured)
        {
            if (PilotedShip == null || !RootValid(PilotedShip.SceneRoot))
                return;
            IShipSceneRoot rootNode = PilotedShip.SceneRoot;
            if (!rootNode.IsInsideTree)
                return;
            foreach (SubtreeCapture entry in captured)
            {
                ShipInstance child = entry.Inst;
                if (child == null || !RootInTree(child.SceneRoot))
                    continue;
                ShipHost?.SetShipRootGlobalTransform(child.SceneRoot, SessionMath.Mul(rootNode.GlobalTransform, entry.LocalXform));
            }
        }

        // ------------------------------------------------------------------ dock barriers / bridge terminals
        void SpawnDockBarrier(ShipInstance inst)
        {
            ClearDockBarriers();
            if (inst == null || !RootValid(inst.SceneRoot))
                return;
            GdDict local = DockPorts.ForDerelict(inst.BuiltLayout, ShipSeed(inst), ShipConditionClass(inst));
            if (local.IsEmpty)
                return;
            string cond = inst == HomeShip ? "intact" : V.Str(local.Get("condition", "intact"));
            var barrier = new DockPortBarrier();
            barrier.Configure(inst.MarkerId, cond, PlayerProgression, (Vec3)local["position"], 6.0, 1.8);
            barrier.Parent = inst.SceneRoot;
            barrier.BreachOpened += OnDockBarrierOpened;
            DockBarriers.Add(Spawn(barrier));
        }

        void ClearDockBarriers()
        {
            foreach (DockPortBarrier b in DockBarriers)
                Despawn(b);
            DockBarriers.Clear();
        }

        void OnDockBarrierOpened(string markerId)
        {
            RecomputeOccupancy();
            PlaySfx(AudioEventSeam.SFX_DOOR_OPEN);
        }

        /// <summary>Ship-local bridge-room center, or <see cref="Vec3.Inf"/> (no bridge -> not claimable).</summary>
        static Vec3 CommandRoomLocalCenter(ShipInstance inst)
        {
            if (inst == null || inst.BuiltLayout == null || inst.BuiltLayout.IsEmpty)
                return Vec3.Inf;
            return DockPorts.BridgeCenter(inst.BuiltLayout);
        }

        void SpawnBridgeTerminal(ShipInstance inst)
        {
            if (inst == null || inst == HomeShip || !RootValid(inst.SceneRoot))
                return;
            Vec3 localCenter = CommandRoomLocalCenter(inst);
            if (localCenter == Vec3.Inf)
                return;
            var kept = new List<BridgeTerminal>();
            foreach (BridgeTerminal t in BridgeTerminals)
            {
                if (!t.IsValid)
                    continue;
                if (t.ShipId == inst.ShipId)
                {
                    Despawn(t);
                    continue;
                }
                kept.Add(t);
            }
            BridgeTerminals.Clear();
            BridgeTerminals.AddRange(kept);
            var terminal = new BridgeTerminal();
            terminal.Configure(inst.ShipId, localCenter, 1.8);
            terminal.Parent = inst.SceneRoot;
            terminal.LoginRequested += OnLoginRequested;
            BridgeTerminals.Add(Spawn(terminal));
        }

        void ClearBridgeTerminals()
        {
            foreach (BridgeTerminal t in BridgeTerminals)
                Despawn(t);
            BridgeTerminals.Clear();
        }

        // ------------------------------------------------------------------ hangar / cargo / carts
        GdDict ConfigureBayFromLayout(ShipInstance inst)
        {
            if (inst == null || inst.BuiltLayout == null || inst.BuiltLayout.IsEmpty)
                return new GdDict();
            GdDict desc = DockPorts.ForHangar(inst.BuiltLayout, ShipSeed(inst));
            if (desc.IsEmpty)
                return new GdDict();
            HangarBay bay = inst.GetHangar();
            if (bay.SlotCount == 0)
            {
                bay.SlotCount = V.I64(desc.Get("slot_count", 0L));
                bay.SlotSizeClass = V.I64(desc.Get("slot_size_class", 0L));
                bay.Slots.Clear();
                for (long i = 0; i < bay.SlotCount; i++)
                    bay.Slots.Add("");
            }
            return desc;
        }

        void SpawnHangarControl(ShipInstance inst)
        {
            if (inst == null || !RootValid(inst.SceneRoot))
                return;
            GdDict desc = ConfigureBayFromLayout(inst);
            if (desc.IsEmpty)
                return;
            GdArray anchors = desc.GetArrayOrEmpty("slot_anchors");
            if (anchors.IsEmpty)
                return;
            Vec3 center = Vec3.Zero;
            foreach (object a in anchors)
                center += (Vec3)a;
            center = center / (float)anchors.Count;
            var kept = new List<HangarBayControl>();
            foreach (HangarBayControl c in HangarControls)
            {
                if (!c.IsValid)
                    continue;
                if (c.CarrierId == inst.ShipId)
                {
                    Despawn(c);
                    continue;
                }
                kept.Add(c);
            }
            HangarControls.Clear();
            HangarControls.AddRange(kept);
            var control = new HangarBayControl();
            control.Configure(inst.ShipId, center, 1.8);
            control.Parent = inst.SceneRoot;
            control.BayDockRequested += OnBayDockRequested;
            control.BayLaunchRequested += OnBayLaunchRequested;
            HangarControls.Add(Spawn(control));
        }

        void ClearHangarControls()
        {
            foreach (HangarBayControl c in HangarControls)
                Despawn(c);
            HangarControls.Clear();
        }

        void SpawnCargoHoldControl(ShipInstance inst)
        {
            if (inst == null || !RootValid(inst.SceneRoot))
                return;
            ApplyDemoCargoCap(inst);
            Vec3 center = DockPorts.RoomFloorCenter(inst.BuiltLayout, "cargo", "cargo");
            if (center == Vec3.Inf)
                return;
            var kept = new List<CargoHoldControl>();
            foreach (CargoHoldControl c in CargoHoldControls)
            {
                if (!c.IsValid)
                    continue;
                if (c.CarrierId == inst.ShipId)
                {
                    Despawn(c);
                    continue;
                }
                kept.Add(c);
            }
            CargoHoldControls.Clear();
            CargoHoldControls.AddRange(kept);
            var control = new CargoHoldControl();
            control.Configure(inst.ShipId, center, 1.8);
            control.Parent = inst.SceneRoot;
            control.CargoDepositRequested += OnCargoDepositRequested;
            control.CargoWithdrawRequested += OnCargoWithdrawRequested;
            CargoHoldControls.Add(Spawn(control));
        }

        void ClearCargoHoldControls()
        {
            foreach (CargoHoldControl c in CargoHoldControls)
                Despawn(c);
            CargoHoldControls.Clear();
        }

        void SpawnCartControl(ShipInstance inst, CartState cart)
        {
            if (inst == null || cart == null || !RootValid(inst.SceneRoot))
                return;
            var kept = new List<CartControl>();
            foreach (CartControl c in CartControls)
            {
                if (!c.IsValid)
                    continue;
                if (c.CartId == cart.CartId)
                {
                    Despawn(c);
                    continue;
                }
                kept.Add(c);
            }
            CartControls.Clear();
            CartControls.AddRange(kept);
            var control = new CartControl();
            control.Configure(cart.CartId, cart.ParkedPosition, 1.8);
            control.Parent = inst.SceneRoot;
            control.CartGrabRequested += OnCartGrabRequested;
            control.CartLoadRequested += OnCartLoadRequested;
            control.CartUnloadRequested += OnCartUnloadRequested;
            CartControls.Add(Spawn(control));
        }

        /// <summary>Ensures a cargo-capable ship has one parked salvage cart (organically reachable cart loop).</summary>
        void EnsureOrganicCart(ShipInstance inst)
        {
            if (inst == null)
                return;
            if (inst.GetCarts().Count > 0)
                return;
            GdDict layout = inst.BuiltLayout ?? new GdDict();
            if (layout.IsEmpty)
                return;
            Vec3 center = DockPorts.RoomFloorCenter(layout, "cargo", "cargo");
            if (center == Vec3.Inf)
                center = DockPorts.RoomFloorCenter(layout, "hangar", "hangar");
            if (center == Vec3.Inf)
                return;
            CartState cart = CartState.Create("organic_cart_" + inst.ShipId, 200.0);
            cart.ParkedShipId = inst.ShipId;
            cart.ParkedPosition = center + new Vec3(1.5f, 0.0f, 0.0f);
            inst.GetCarts().Add(cart);
        }

        void SpawnCartControlsForShip(ShipInstance inst)
        {
            if (inst == null)
                return;
            EnsureOrganicCart(inst);
            foreach (CartState cart in inst.GetCarts())
                SpawnCartControl(inst, cart);
        }

        void ClearCartControls()
        {
            foreach (CartControl c in CartControls)
                Despawn(c);
            CartControls.Clear();
        }

        /// <summary><c>_find_cart_by_id(cart_id)</c>: (cart, ship) or (null, null).</summary>
        (CartState cart, ShipInstance ship) FindCartById(string cartId)
        {
            foreach (ShipInstance inst in AllKnownShips())
            {
                foreach (CartState cart in inst.GetCarts())
                {
                    if (cart.CartId == cartId)
                        return (cart, inst);
                }
            }
            return (null, null);
        }

        void OnCartGrabRequested(string cartId)
        {
            (CartState cart, ShipInstance _) = FindCartById(cartId);
            if (cart == null)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return;
            }
            if (GrabbedCart != null)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return;
            }
            GrabbedCart = cart;
            PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP);
            RecomputePlayerEncumbrance();
        }

        void OnCartLoadRequested(string cartId) => OpenTransferPanelForCart(cartId);
        void OnCartUnloadRequested(string cartId, string category) => OpenTransferPanelForCart(cartId);
        void OnCargoDepositRequested(string shipId) => OpenTransferPanelForShip(shipId);
        void OnCargoWithdrawRequested(string shipId, string category) => OpenTransferPanelForShip(shipId);

        void OpenTransferPanelForShip(string shipId)
        {
            if (InventoryState == null)
                return;
            ShipInstance inst = FindShipById(shipId);
            if (inst == null)
                return;
            inst.GetInventory();
            Events.RaisePanelRequested("transfer", new GdDict { { "ship_id", shipId }, { "label", "HOLD" } });
            PlaySfx(AudioEventSeam.UI_PANEL_OPEN);
            FreezePlayerForPanel();
        }

        void OpenTransferPanelForCart(string cartId)
        {
            if (InventoryState == null)
                return;
            (CartState cart, ShipInstance _) = FindCartById(cartId);
            if (cart == null)
                return;
            Events.RaisePanelRequested("transfer", new GdDict { { "cart_id", cartId }, { "label", "CART" } });
            PlaySfx(AudioEventSeam.UI_PANEL_OPEN);
            FreezePlayerForPanel();
        }

        void FreezePlayerForPanel() => Scene?.SetPlayerFrozen(true);
        void UnfreezePlayerAfterPanel() => Scene?.SetPlayerFrozen(false);

        /// <summary>Direct deposit-all into a ship hold (was <c>cargo_deposit_for_validation</c>).</summary>
        public long CargoDeposit(string shipId)
        {
            ShipInstance inst = FindShipById(shipId);
            if (inst == null || InventoryState == null)
                return 0;
            long moved = V.I64(CargoTransfer.DepositAll(InventoryState, inst.GetInventory()).Get("total_moved", 0L));
            RecomputePlayerEncumbrance();
            PlaySfx(moved > 0 ? AudioEventSeam.SFX_DROP_ITEM : AudioEventSeam.UI_PANEL_CLOSE);
            return moved;
        }

        /// <summary>Direct category withdraw from a ship hold (was <c>cargo_withdraw_for_validation</c>).</summary>
        public long CargoWithdraw(string shipId, string category)
        {
            ShipInstance inst = FindShipById(shipId);
            if (inst == null || InventoryState == null)
                return 0;
            long moved = V.I64(CargoTransfer.WithdrawCategory(inst.GetInventory(), InventoryState, category).Get("total_moved", 0L));
            RecomputePlayerEncumbrance();
            PlaySfx(moved > 0 ? AudioEventSeam.SFX_TOOL_PICKUP : AudioEventSeam.UI_PANEL_CLOSE);
            return moved;
        }

        /// <summary>Deposit-all into a cart hold (was <c>cart_load_for_validation</c>).</summary>
        public long CartLoad(string cartId)
        {
            (CartState cart, ShipInstance _) = FindCartById(cartId);
            if (cart == null || InventoryState == null)
                return 0;
            long moved = V.I64(CargoTransfer.DepositAll(InventoryState, cart.GetHold()).Get("total_moved", 0L));
            PlaySfx(moved > 0 ? AudioEventSeam.SFX_DROP_ITEM : AudioEventSeam.UI_PANEL_CLOSE);
            return moved;
        }

        /// <summary>Withdraw a category from a cart hold (was <c>cart_unload_for_validation</c>).</summary>
        public long CartUnload(string cartId, string category)
        {
            (CartState cart, ShipInstance _) = FindCartById(cartId);
            if (cart == null || InventoryState == null)
                return 0;
            long moved = V.I64(CargoTransfer.WithdrawCategory(cart.GetHold(), InventoryState, category).Get("total_moved", 0L));
            PlaySfx(moved > 0 ? AudioEventSeam.SFX_TOOL_PICKUP : AudioEventSeam.UI_PANEL_CLOSE);
            return moved;
        }

        // ------------------------------------------------------------------ encumbrance / equipment
        /// <summary>Recompute the carry budget from worn equipment and apply the Heavy Load (x cart push) movement penalty.</summary>
        void RecomputePlayerEncumbrance()
        {
            if (InventoryState == null)
                return;
            double bonus = 0.0;
            double saved = 0.0;
            if (EquipmentState != null)
            {
                bonus = EquipmentState.GetCarryCapacityBonus();
                saved = Encumbrance.WeightReductionSaved(InventoryState.GetTotalWeight(), EquipmentState.GetContainerReductions());
            }
            InventoryState.BonusCapacity = bonus;
            InventoryState.WeightReduction = saved;
            double loadRatio = InventoryState.GetLoadRatio();
            bool overloaded = loadRatio > 1.0;
            if (overloaded && !_prevEncumbranceOverloaded)
            {
                PlaySfx(AudioEventSeam.UI_VITALS_LOW);
                SetHazardFeedbackLine("Heavy Load — movement slowed");
            }
            _prevEncumbranceOverloaded = overloaded;
            if (HasPlayer && Scene != null)
            {
                double mult = Encumbrance.MoveSpeedMultiplier(loadRatio);
                Scene.PlayerMoveSpeed = Scene.PlayerDefaultMoveSpeed * mult * CartPushMultiplier();
            }
        }

        /// <summary>Equip from inventory (auto = pickup path, empty slots only). Returns true on success.</summary>
        bool EquipFromInventory(string itemId, bool auto)
        {
            if (EquipmentState == null || InventoryState == null)
                return false;
            if (!EquipmentState.CanEquip(itemId))
            {
                if (!auto)
                    EmitEquipDeniedSfx();
                return false;
            }
            string slot = ItemDefs.EquipSlot(DefinitionsForEquip(), itemId);
            if ((slot == "primary_hand" || slot == "secondary_hand") && IsCartGrabbed())
            {
                if (!auto)
                    EmitEquipDeniedSfx();
                return false;
            }
            if (auto && EquipmentState.IsSlotOccupied(slot))
                return false;
            if (InventoryState.GetQuantity(itemId) <= 0)
            {
                if (!auto)
                    EmitEquipDeniedSfx();
                return false;
            }
            InventoryState.RemoveItem(itemId, 1);
            GdDict res = EquipmentState.Equip(itemId);
            if (!res.GetBool("ok"))
            {
                InventoryState.AddItem(itemId, 1);
                if (!auto)
                    EmitEquipDeniedSfx();
                return false;
            }
            string displaced = V.Str(res.Get("displaced", ""));
            if (displaced != "")
                InventoryState.AddItem(displaced, 1);
            PlaySfx(AudioEventSeam.SFX_TOOL_PICKUP);
            RecomputePlayerEncumbrance();
            return true;
        }

        void EmitEquipDeniedSfx() => PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        /// <summary>Unequip a slot back into the inventory; returns the item id ("" when the slot was empty).</summary>
        public string UnequipToInventory(string slot)
        {
            if (EquipmentState == null || InventoryState == null)
                return "";
            string itemId = EquipmentState.Unequip(slot);
            if (itemId != "")
            {
                InventoryState.AddItem(itemId, 1);
                PlaySfx(AudioEventSeam.SFX_DROP_ITEM);
                RecomputePlayerEncumbrance();
            }
            else
            {
                EmitEquipDeniedSfx();
            }
            return itemId;
        }

        /// <summary>Manual equip (was <c>equip_for_validation</c>, minus the seed-one-into-inventory convenience).</summary>
        public bool Equip(string itemId) => EquipFromInventory(itemId, false);

        GdDict DefinitionsForEquip()
        {
            if (_itemDefs.IsEmpty)
                _itemDefs = ItemDefs.LoadDefinitions();
            return _itemDefs;
        }

        bool IsCartGrabbed() => GrabbedCart != null;
        double CartPushMultiplier() => GrabbedCart != null ? GrabbedCart.PushSpeedMultiplier : 1.0;

        // ------------------------------------------------------------------ hangar dock / launch
        ShipInstance BayDockCandidate(ShipInstance carrier)
        {
            if (carrier == null)
                return null;
            HangarBay bay = carrier.GetHangar();
            foreach (IDockableShip childD in carrier.DockedShips)
            {
                if (!(childD is ShipInstance child) || !RootValid(child.SceneRoot))
                    continue;
                if (bay.SlotOf(child.ShipId) != -1)
                    continue;
                long sizeClass = ShipDockSizeClass(child);
                if (bay.FreeSlotFor(sizeClass) != -1)
                    return child;
            }
            return null;
        }

        static long ShipDockSizeClass(ShipInstance inst)
        {
            if (inst == null || inst.BuiltLayout == null)
                return DockPorts.AIRLOCK_SIZE_CLASS;
            GdDict p = DockPorts.ForLifeboat(inst.BuiltLayout);
            if (p.IsEmpty)
                p = DockPorts.ForDerelict(inst.BuiltLayout);
            return V.I64(p.Get("size_class", DockPorts.AIRLOCK_SIZE_CLASS));
        }

        static int FirstOccupiedSlot(HangarBay bay)
        {
            for (int i = 0; i < bay.Slots.Count; i++)
            {
                if (bay.Slots[i] != "")
                    return i;
            }
            return -1;
        }

        void PlaceInSlot(ShipInstance carrier, ShipInstance mobile, long slotIndex)
        {
            if (carrier == null || mobile == null)
                return;
            if (!RootValid(carrier.SceneRoot) || !RootValid(mobile.SceneRoot))
                return;
            if (!carrier.SceneRoot.IsInsideTree || !mobile.SceneRoot.IsInsideTree)
                return;
            GdDict desc = DockPorts.ForHangar(carrier.BuiltLayout, ShipSeed(carrier));
            GdArray anchors = desc.GetArrayOrEmpty("slot_anchors");
            if (slotIndex < 0 || slotIndex >= anchors.Count)
                return;
            var anchor = (Vec3)anchors[(int)slotIndex];
            ShipHost?.SetShipRootGlobalTransform(mobile.SceneRoot, SessionMath.Mul(carrier.SceneRoot.GlobalTransform, SessionMath.Translation(anchor)));
        }

        void OnBayDockRequested(string carrierId, long slotIndex)
        {
            ShipInstance carrier = FindShipById(carrierId);
            if (carrier == null)
            {
                EmitHangarDeniedSfx();
                return;
            }
            HangarBay bay = carrier.GetHangar();
            if (bay.SlotCount <= 0)
            {
                EmitHangarDeniedSfx();
                return;
            }
            ShipInstance candidate = BayDockCandidate(carrier);
            if (candidate == null)
            {
                EmitHangarDeniedSfx();
                return;
            }
            long sizeClass = ShipDockSizeClass(candidate);
            int idx = bay.Dock(candidate.ShipId, sizeClass);
            if (idx == -1)
            {
                EmitHangarDeniedSfx();
                return;
            }
            DockingManager.Undock(candidate);
            candidate.ParentShip = carrier;
            if (!carrier.DockedShips.Contains(candidate))
                carrier.DockedShips.Add(candidate);
            PlaceInSlot(carrier, candidate, idx);
            RecomputeOccupancy();
            EmitTrainingEvent("negotiate_truce", carrierId);
            PlaySfx(AudioEventSeam.SFX_DOCK_LAND);
        }

        void OnBayLaunchRequested(string carrierId, long slotIndex)
        {
            ShipInstance carrier = FindShipById(carrierId);
            if (carrier == null)
            {
                EmitHangarDeniedSfx();
                return;
            }
            HangarBay bay = carrier.GetHangar();
            long idx = slotIndex;
            if (idx < 0)
                idx = FirstOccupiedSlot(bay);
            if (idx < 0)
            {
                EmitHangarDeniedSfx();
                return;
            }
            string shipId = bay.Launch(idx);
            if (shipId == "")
            {
                EmitHangarDeniedSfx();
                return;
            }
            ShipInstance launched = FindShipById(shipId);
            if (launched == null)
            {
                EmitHangarDeniedSfx();
                return;
            }
            launched.ParentShip = null;
            carrier.DockedShips.Remove(launched);
            if (RootValid(launched.SceneRoot) && RootValid(carrier.SceneRoot) && carrier.SceneRoot.IsInsideTree && launched.SceneRoot.IsInsideTree)
                ShipHost?.SetShipRootGlobalTransform(launched.SceneRoot, SessionMath.Mul(carrier.SceneRoot.GlobalTransform, SessionMath.Translation(new Vec3(0.0f, 0.0f, -8.0f))));
            RecomputeOccupancy();
            PlaySfx(AudioEventSeam.SFX_DOOR_OPEN);
        }

        void EmitHangarDeniedSfx() => PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        /// <summary>Clears <paramref name="inst"/>'s slot in its parent's bay before a non-launch departure (PR#16 P2).</summary>
        static void UnbayFromParent(ShipInstance inst)
        {
            if (inst == null || !(inst.ParentShip is ShipInstance parent))
                return;
            if (parent.Hangar == null)
                return;
            int s = parent.Hangar.SlotOf(inst.ShipId);
            if (s != -1)
                parent.Hangar.Launch(s);
        }

        /// <summary>Hangar dock via the real handler (was <c>bay_dock_for_validation</c>): the slot the candidate landed in.</summary>
        public long BayDock(string carrierId)
        {
            ShipInstance carrier = FindShipById(carrierId);
            if (carrier == null)
                return -1;
            int before = FirstOccupiedSlot(carrier.GetHangar());
            OnBayDockRequested(carrierId, -1);
            long candidateSlot = -1;
            List<string> slots = carrier.GetHangar().Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] != "")
                {
                    candidateSlot = i;
                    if (i != before)
                        break;
                }
            }
            return candidateSlot;
        }

        /// <summary>Hangar launch via the real handler (was <c>bay_launch_for_validation</c>).</summary>
        public long BayLaunch(string carrierId)
        {
            ShipInstance carrier = FindShipById(carrierId);
            if (carrier == null)
                return -1;
            int idx = FirstOccupiedSlot(carrier.GetHangar());
            OnBayLaunchRequested(carrierId, -1);
            return idx;
        }

        // ------------------------------------------------------------------ ship lookup
        ShipInstance FindShipById(string id)
        {
            if (id == "")
                return null;
            if (HomeShip != null && HomeShip.ShipId == id) return HomeShip;
            if (LifeboatShip != null && LifeboatShip.ShipId == id) return LifeboatShip;
            if (CurrentShip != null && CurrentShip.ShipId == id) return CurrentShip;
            foreach (ShipInstance inst in VisitedShips.Values)
            {
                if (inst.ShipId == id)
                    return inst;
            }
            return null;
        }

        public ShipInstance GetShipById(string id) => FindShipById(id);

        ShipInstance FindShipByIdOrMarker(string key)
        {
            if (key == "" && HomeShip != null) return HomeShip;
            if (CurrentShip != null && CurrentShip.MarkerId == key) return CurrentShip;
            if (HomeShip != null && HomeShip.MarkerId == key) return HomeShip;
            foreach (ShipInstance inst in VisitedShips.Values)
            {
                if (inst.MarkerId == key)
                    return inst;
            }
            return FindShipById(key);
        }

        /// <summary>Every ShipInstance the coordinator tracks (home, lifeboat, current, visited), de-duplicated.</summary>
        List<ShipInstance> AllKnownShips()
        {
            var output = new List<ShipInstance>();
            var seen = new HashSet<ShipInstance>();
            foreach (ShipInstance inst in new[] { HomeShip, LifeboatShip, CurrentShip })
            {
                if (inst != null && seen.Add(inst))
                    output.Add(inst);
            }
            foreach (ShipInstance inst in VisitedShips.Values)
            {
                if (inst != null && seen.Add(inst))
                    output.Add(inst);
            }
            return output;
        }

        void ApplyDemoCargoCap(ShipInstance inst)
        {
            if (inst == null || DemoScopeGate == null)
                return;
            if (!DemoScopeGate.IsBlocked("cargo_hold.full_inventory"))
                return;
            double cap = V.F64(DemoScopeGate.GetParams("cargo_hold.full_inventory").Get("max_weight_kg", 6.0));
            ShipInventory hold = inst.GetInventory();
            if (hold != null && hold.GetMaxWeight() > cap)
                hold.MaxWeight = cap;
        }
    }
}
