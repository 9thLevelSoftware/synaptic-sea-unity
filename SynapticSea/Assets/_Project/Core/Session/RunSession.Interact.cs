// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _on_player_interact_requested (7953-8077) and the
// per-kind try helpers (8091-8176), sealed hatches (3065-3104, 6123-6282) and authored portals (6176-6238).
using System.Collections.Generic;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>The handler id that claimed the last interact request ("miss_sfx" when none did).</summary>
        public string LastInteractHandlerId { get; private set; } = "";

        /// <summary>
        /// <c>player.request_interact()</c> -> <c>_on_player_interact_requested(player)</c>: walk the ordered
        /// <see cref="InteractionRegistry"/> for the current location until a handler claims the request; otherwise play
        /// the soft-miss cue. Returns the claiming handler id.
        /// </summary>
        public string RequestInteract()
        {
            TriggerTutorial("player_interacted", "any");
            SessionLocation location = AwayFromStart ? SessionLocation.Away : SessionLocation.Home;
            Vec3 playerPosition = PlayerPos;
            foreach (InteractionHandler handler in InteractionRegistry.Handlers)
            {
                if (!handler.AppliesTo(location))
                    continue;
                if (handler.TryHandle(this, playerPosition))
                {
                    LastInteractHandlerId = handler.Id;
                    return handler.Id;
                }
            }
            EmitInteractMissSfx();
            LastInteractHandlerId = InteractionRegistry.MissHandlerId;
            return LastInteractHandlerId;
        }

        void EmitInteractMissSfx() => PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);

        internal bool TryDockBarriers(Vec3 p)
        {
            foreach (DockPortBarrier b in new List<DockPortBarrier>(DockBarriers))
            {
                if (b.IsValid && !b.Opened && b.TryStart(p))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Unity-port fix: a terminal of the ship the player already pilots does not claim the interact. Godot's
        /// <c>t.try_login(player)</c> claimed every in-range press, and the terminal sits on its command room's centre,
        /// where the life boat's repair and fire suppression points are also placed (<c>LifeboatLocalRepairPositions</c>),
        /// so those points could never be used. Logging in again changes nothing (access is already claimed and the ship
        /// is already piloted), so the press falls through to the next handler instead.
        /// </summary>
        internal bool TryBridgeTerminals(Vec3 p)
        {
            foreach (BridgeTerminal t in new List<BridgeTerminal>(BridgeTerminals))
            {
                if (!t.IsValid || (PilotedShip != null && PilotedShip.ShipId == t.ShipId))
                    continue;
                if (t.TryLogin(p))
                    return true;
            }
            return false;
        }

        internal bool TryFireSuppressionPoints(Vec3 p)
        {
            foreach (FireSuppressionPoint fp in new List<FireSuppressionPoint>(FireSuppressionPoints))
            {
                if (fp.IsValid && fp.TryStart(p))
                    return true;
            }
            return false;
        }

        internal bool TryRepairPoints(Vec3 p)
        {
            foreach (RepairPoint rp in new List<RepairPoint>(RepairPoints))
            {
                if (rp.IsValid && rp.TryStart(p))
                    return true;
            }
            return false;
        }

        internal bool TryBreachSealPoints(Vec3 p)
        {
            foreach (BreachSealPoint sp in new List<BreachSealPoint>(BreachSealPoints))
            {
                if (sp.IsValid && sp.TryStart(p))
                    return true;
            }
            return false;
        }

        internal bool TryCraftingStations(Vec3 p)
        {
            foreach (CraftingStation st in new List<CraftingStation>(CraftingStations))
            {
                if (st.IsValid && st.TryInteract(p))
                    return true;
            }
            return false;
        }

        internal bool TryProductionStations(Vec3 p)
        {
            foreach (ProductionStation st in new List<ProductionStation>(ProductionStations))
            {
                if (st.IsValid && st.TryInteract(p))
                    return true;
            }
            return false;
        }

        internal bool TryLootContainers(Vec3 p)
        {
            foreach (LootContainer lc in new List<LootContainer>(LootContainers))
            {
                if (lc.IsValid && lc.TryInteract(p))
                    return true;
            }
            return false;
        }

        internal bool TryDerelictObjectives(Vec3 p)
        {
            foreach (ObjectiveInteractable it in new List<ObjectiveInteractable>(DerelictInteractables))
            {
                if (it.IsValid && it.TryInteract(p))
                    return true;
            }
            return false;
        }

        internal bool TryHomeObjectives(Vec3 p)
        {
            foreach (ObjectiveInteractable it in new List<ObjectiveInteractable>(Interactables))
            {
                if (it.TryInteract(p))
                    return true;
            }
            return false;
        }

        /// <summary>A ToolPickup: success acquires; in-range already-owned / full-stack deny consumes interact with a deny cue.</summary>
        internal bool TryToolPickupInteract(ToolPickup pickup, Vec3 p)
        {
            if (pickup == null || !pickup.IsValid)
                return false;
            if (pickup.TryInteract(p))
                return true;
            if (pickup.IsInteractCandidate(p))
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return true;
            }
            return false;
        }

        /// <summary>Scoop cart-overload floor piles; a denied scoop still consumes interact with a soft cue.</summary>
        internal bool TryWorkYieldDropInteract(Vec3 p)
        {
            foreach (WorkYieldDrop d in new List<WorkYieldDrop>(WorkYieldDrops))
            {
                if (!d.IsValid)
                    continue;
                if (d.TryInteract(p))
                {
                    if (!d.IsValid)
                        Events.RaiseInteractableDespawned(d);
                    return true;
                }
                if (d.IsInteractCandidate(p))
                {
                    PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Walk-up cargo deposit (strict in-range gate at a cargo control).</summary>
        internal bool TryCargoDeposit(Vec3 p)
        {
            foreach (CargoHoldControl ch in new List<CargoHoldControl>(CargoHoldControls))
            {
                if (ch.IsValid && ch.TryDeposit(p))
                    return true;
            }
            return false;
        }

        /// <summary>Hangar: prefer docking a co-present candidate, else launch the first bayed ship.</summary>
        internal bool TryHangarInteract(Vec3 p)
        {
            foreach (HangarBayControl c in new List<HangarBayControl>(HangarControls))
            {
                if (!c.IsValid)
                    continue;
                ShipInstance carrier = FindShipById(c.CarrierId);
                if (carrier == null)
                    continue;
                HangarBay bay = carrier.GetHangar();
                if (bay == null || bay.SlotCount <= 0)
                    continue;
                if (BayDockCandidate(carrier) != null && c.TryDock(p, -1))
                    return true;
                if (FirstOccupiedSlot(bay) >= 0 && c.TryLaunch(p, -1))
                    return true;
            }
            return false;
        }

        /// <summary>Walk-up cart: grab if not held, else load salvage into it.</summary>
        internal bool TryCartInteract(Vec3 p)
        {
            foreach (CartControl c in new List<CartControl>(CartControls))
            {
                if (!c.IsValid)
                    continue;
                if (GrabbedCart == null)
                {
                    if (c.TryGrab(p))
                        return true;
                }
                else
                {
                    if (c.TryLoad(p))
                        return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ sealed hatches
        /// <summary>Domain 5: seeds two locked hatches on the active derelict, deterministic per marker_id.</summary>
        void BuildSealedHatches()
        {
            ClearSealedHatches();
            if (CurrentShip == null)
                return;
            if (!AwayFromStart)
                return;
            List<Vec3> positions = DistributedRoomPositions();
            if (positions.Count == 0)
                return;
            GdArray bypassed = CurrentShip.BypassedHatchIds;
            var rng = SynapticSea.Core.Rng.GodotRandom.FromSeed(GodotHash.StringHash(CurrentShip.MarkerId));
            long count = System.Math.Min(SEALED_HATCH_COUNT, positions.Count);
            for (long i = 0; i < count; i++)
            {
                int idx = (int)((rng.Randi() + i * 7) % positions.Count);
                Vec3 pos = positions[idx];
                string hid = CurrentShip.MarkerId + ":hatch_" + i;
                string lockKind = i % 2 == 0 ? SealedHatch.MECHANICAL : SealedHatch.ELECTRONIC;
                var hatch = new SealedHatch();
                var link = (GdArray)HATCH_BULKHEAD_LINKS[(int)(i % HATCH_BULKHEAD_LINKS.Count)];
                hatch.Configure(hid, lockKind, pos, 1.8, V.Str(link[0]), V.Str(link[1]));
                if (bypassed.Contains(hid))
                    hatch.SetBypassed(true);
                hatch.HatchBypassed += OnHatchBypassed;
                hatch.HatchResealed += OnHatchResealed;
                if (RootValid(CurrentShip.SceneRoot))
                    hatch.Parent = CurrentShip.SceneRoot;
                SealedHatches.Add(Spawn(hatch));
            }
        }

        void ClearSealedHatches()
        {
            foreach (SealedHatch h in SealedHatches)
                Despawn(h);
            SealedHatches.Clear();
        }

        /// <summary>Domain 5: a hatch was bypassed — consume the flag, clear its status when depleted, persist, open the bulkhead link.</summary>
        void OnHatchBypassed(string hatchId, string lockKind)
        {
            string flag = lockKind == SealedHatch.MECHANICAL ? "lockpick" : "hack_chip";
            bool depleted = false;
            if (UtilityItemState != null)
                depleted = UtilityItemState.ConsumeFlag(flag);
            if (depleted && StatusEffectsState != null)
                StatusEffectsState.RemoveEffect("utility_" + flag + "_ready", 9999);
            if (CurrentShip != null && !CurrentShip.BypassedHatchIds.Contains(hatchId))
                CurrentShip.BypassedHatchIds.Add(hatchId);
            EmitTrainingEvent("build_shelter", hatchId);
            PlaySfx(AudioEventSeam.SFX_DOOR_OPEN);
            foreach (SealedHatch h in SealedHatches)
            {
                if (!h.IsValid || h.HatchId != hatchId)
                    continue;
                FireSuppressionState afs = ActiveFireState();
                if (afs != null && h.CompartmentA.Length > 0 && h.CompartmentB.Length > 0)
                    afs.SetLinkClosed(h.CompartmentA, h.CompartmentB, false);
                break;
            }
            RefreshInventoryHud();
        }

        internal bool TryBypassNearestHatch(Vec3 p)
        {
            if (!HasPlayer)
                return false;
            GdDict flags = UtilityItemState != null ? UtilityItemState.ActiveFlags : new GdDict();
            bool deniedInRange = false;
            foreach (SealedHatch h in new List<SealedHatch>(SealedHatches))
            {
                if (!h.IsValid || h.Bypassed)
                    continue;
                GdDict res = h.TryBypass(p, flags);
                if (res.GetBool("ok"))
                    return true;
                string reason = V.Str(res.Get("reason", ""));
                if (reason == "locked" || reason == "already_open")
                    deniedInRange = true;
            }
            if (deniedInRange)
            {
                PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                return true;
            }
            return false;
        }

        internal bool TryResealNearestHatch(Vec3 p)
        {
            if (!HasPlayer)
                return false;
            foreach (SealedHatch h in new List<SealedHatch>(SealedHatches))
            {
                if (!h.IsValid || !h.Bypassed)
                    continue;
                if (h.TryReseal(p).GetBool("ok"))
                    return true;
            }
            return false;
        }

        void OnHatchResealed(string hatchId, string lockKind)
        {
            if (CurrentShip != null && CurrentShip.BypassedHatchIds.Contains(hatchId))
                CurrentShip.BypassedHatchIds.Remove(hatchId);
            EmitTrainingEvent("build_shelter", hatchId);
            PlaySfx(AudioEventSeam.SFX_DOOR_CLOSE);
            SetHazardFeedbackLine("Hatch sealed: " + hatchId);
            foreach (SealedHatch h in SealedHatches)
            {
                if (!h.IsValid || h.HatchId != hatchId)
                    continue;
                FireSuppressionState afs = ActiveFireState();
                if (afs != null && h.CompartmentA.Length > 0 && h.CompartmentB.Length > 0)
                    afs.SetLinkClosed(h.CompartmentA, h.CompartmentB, true);
                break;
            }
            RefreshInventoryHud();
        }

        // ------------------------------------------------------------------ authored portals
        internal bool TryAuthoredPortalInteract(Vec3 p)
        {
            if (!HasPlayer || CurrentShip == null)
                return false;
            if (!(CurrentShip.SceneRoot is IShipLoaderView activeLoader) || !activeLoader.IsValid)
                return false;
            GdDict flags = UtilityItemState != null ? UtilityItemState.ActiveFlags : new GdDict();
            foreach (IAuthoredPortal portal in activeLoader.GetAuthoredPortals())
            {
                if (portal == null || !portal.IsValid)
                    continue;
                GdDict result = portal.TryInteract(flags, p);
                if (result.GetBool("ok"))
                {
                    RecordAuthoredPortalState(portal, result);
                    if (portal.PortalKind == "LOCKED" && result.GetBool("unlocked_now") && UtilityItemState != null)
                        UtilityItemState.ConsumeFlag(portal.RequiredFlag());
                    PlaySfx(result.GetBool("open") ? AudioEventSeam.SFX_DOOR_OPEN : AudioEventSeam.SFX_DOOR_CLOSE, portal.GlobalPosition);
                    if (result.GetBool("exterior"))
                        return TravelHome();
                    return true;
                }
                if (V.Str(result.Get("reason", "")) == "locked")
                {
                    PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
                    return true;
                }
            }
            return false;
        }

        void RecordAuthoredPortalState(IAuthoredPortal portal, GdDict result)
        {
            if (CurrentShip == null || portal == null)
                return;
            string portalId = portal.PortalId ?? "";
            if (portalId.Length == 0)
                return;
            if (result.GetBool("unlocked_now") && !CurrentShip.AuthoredUnlockedPortalIds.Contains(portalId))
                CurrentShip.AuthoredUnlockedPortalIds.Add(portalId);
            if (result.Has("open"))
            {
                if (result.GetBool("open") && !portal.IsExterior)
                {
                    if (!CurrentShip.AuthoredOpenPortalIds.Contains(portalId))
                        CurrentShip.AuthoredOpenPortalIds.Add(portalId);
                }
                else
                {
                    CurrentShip.AuthoredOpenPortalIds.Remove(portalId);
                }
            }
        }

        void RestoreAuthoredPortalStates()
        {
            if (CurrentShip == null || !(CurrentShip.SceneRoot is IShipLoaderView root) || !root.IsValid)
                return;
            foreach (IAuthoredPortal portal in root.GetAuthoredPortals())
            {
                if (portal == null || !portal.IsValid)
                    continue;
                string portalId = portal.PortalId ?? "";
                portal.RestorePersistentState(CurrentShip.AuthoredUnlockedPortalIds.Contains(portalId), CurrentShip.AuthoredOpenPortalIds.Contains(portalId));
            }
        }
    }
}
