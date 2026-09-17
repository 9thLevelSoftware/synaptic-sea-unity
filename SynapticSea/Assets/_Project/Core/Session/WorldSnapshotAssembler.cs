// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_world_snapshot (10830-10917),
// _current_dock_edges / _opened_port_marker_ids (10923-10972), _apply_world_snapshot (11043-11161) and the docking
// snapshot model half _apply_docking_snapshot / _redock_bayed (11168-11221).
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// Builds a <see cref="WorldSnapshot"/> (home run slice + world model + every retained ship + dock topology) and
    /// applies one back through the single-ship reload, the retained-ship registry, derelict re-activation and the dock
    /// edges. Transform re-pegs go through <see cref="IShipSceneHost"/> / <see cref="DockingManager"/>.
    /// </summary>
    public static class WorldSnapshotAssembler
    {
        public static WorldSnapshot Build(RunSession s)
        {
            s.SyncCombatSummaryForSave();
            s.SyncArcSummaryForSave();
            s.SyncBreachEnvironmentForSave();
            s.SyncPillarSummariesForSave();
            var ws = new WorldSnapshot();
            if (s.SynapticSeaWorld != null)
                ws.WorldSummary = s.SynapticSeaWorld.GetSummary();
            if (s.MetaProgressionState != null)
                ws.MetaProgressionSummary = s.MetaProgressionState.ToDict();
            if (s.UniqueItemState != null)
                ws.UniqueItemSummary = s.UniqueItemState.GetSummary();
            RunSnapshot homeSnap = RunSnapshotAssembler.Build(s, s.AwayFromStart);
            if (homeSnap != null)
            {
                if (s.AwayFromStart)
                {
                    Vec3 hp = s.HomePlayerPosition;
                    homeSnap.PlayerPosition = GdArray.Of((double)hp.X, (double)hp.Y, (double)hp.Z);
                }
                ws.HomeShip = homeSnap.ToDict();
            }
            if (s.HomeShip != null)
            {
                ws.HomeLootedContainers = s.HomeShip.LootedContainerIds.ShallowCopy();
                ws.HomeShipInventory = s.HomeShip.GetInventory().GetSummary();
                ws.HomeBreachEnvironment = s.HomeShip.BreachEnvironmentSummary.DeepCopy();
                var homeCartDicts = new GdArray();
                foreach (CartState c in s.HomeShip.GetCarts())
                    homeCartDicts.Add(c.GetSummary());
                ws.HomeShipCarts = homeCartDicts;
            }
            if (s.EquipmentState != null)
                ws.PlayerEquipment = s.EquipmentState.GetSummary();
            ws.CurrentLocation = s.CurrentShip != null ? s.CurrentShip.MarkerId : "";
            ws.WorldTime = s.WorldTime;
            if (s.Scene != null && s.Scene.HasPlayer)
            {
                Vec3 p = s.Scene.PlayerPosition;
                ws.PlayerPositionInShip = GdArray.Of((double)p.X, (double)p.Y, (double)p.Z);
            }
            ws.DockEdges = CurrentDockEdges(s);
            ws.PilotedShipId = s.PilotedShip != null ? s.PilotedShip.ShipId : "";
            ws.AboardShipId = s.CurrentOccupancy != null ? s.CurrentOccupancy.ShipId : "";
            ws.OpenedPorts = OpenedPortMarkerIds(s);
            ws.VisitedShips = VisitedShipsForSave(s, ws.DockEdges, ws.PilotedShipId, ws.AboardShipId);
            ws.RunId = s.RunIdInternal;
            ws.SliceVersion = WorldSnapshot.WorldSliceVersion;
            ws.GodotVersion = s.Deps.Engine.VersionString;
            ws.SavedAt = s.Clock.DateTimeString(true);
            return ws;
        }

        /// <summary>Every known ship with a parent_ship as {host: marker_id, mobile: ship_id, port_type, slot_index}.</summary>
        public static GdArray CurrentDockEdges(RunSession s)
        {
            var edges = new GdArray();
            var seen = new HashSet<string>();
            foreach (ShipInstance inst in s.AllKnownShipsInternal())
            {
                if (inst == null || !(inst.ParentShip is ShipInstance parent))
                    continue;
                string key = inst.ShipId + ">" + parent.MarkerId;
                if (!seen.Add(key))
                    continue;
                string portType = "airlock";
                long slotIndex = -1;
                if (parent.Hangar != null)
                {
                    int slot = parent.Hangar.SlotOf(inst.ShipId);
                    if (slot != -1)
                    {
                        portType = "hangar";
                        slotIndex = slot;
                    }
                }
                edges.Add(new GdDict
                {
                    { "host", parent.MarkerId },
                    { "mobile", inst.ShipId },
                    { "port_type", portType },
                    { "slot_index", slotIndex },
                });
            }
            return edges;
        }

        static GdArray OpenedPortMarkerIds(RunSession s)
        {
            var output = new GdArray();
            foreach (DockPortBarrier b in s.DockBarriers)
            {
                if (b.IsValid && b.Opened)
                    output.Add(b.MarkerId);
            }
            return output;
        }

        /// <summary>
        /// <c>_apply_world_snapshot(ws)</c>: rebuild home through the single-ship reload, restore home loot/carts/inventory
        /// and equipment, meta/unique/world state, the retained-ship registry and world_time, re-activate a saved
        /// derelict, re-open barriers, regenerate dock-edge endpoints, then re-apply the docking snapshot.
        /// </summary>
        public static bool Apply(RunSession s, WorldSnapshot ws)
        {
            if (ws == null)
                return false;
            RunSnapshot homeSnap = RunSnapshot.FromDict(ws.HomeShip, SaveLoadService.CURRENT_SLICE_VERSION, s.Deps.Engine.VersionString);
            if (homeSnap == null)
            {
                s.Log.Warning("PlayableGeneratedShip: world load rejected — embedded home slice incompatible");
                return false;
            }
            if (homeSnap.PlayerPosition.Count >= 3)
                s.HomePlayerPosition = new Vec3(V.F64(homeSnap.PlayerPosition[0]), V.F64(homeSnap.PlayerPosition[1]), V.F64(homeSnap.PlayerPosition[2]));
            if (!s.ApplyRunSnapshotInternal(homeSnap))
                return false;
            if (s.HomeShip != null)
            {
                s.HomeShip.LootedContainerIds = ws.HomeLootedContainers.ShallowCopy();
                ApplyHomeBreachEnvironment(s, ws.HomeBreachEnvironment);
                if (!ws.HomeShipInventory.IsEmpty)
                    s.HomeShip.GetInventory().ApplySummary(ws.HomeShipInventory);
                ApplyHomeCarts(s, ws.HomeShipCarts);
                if (!s.AwayFromStart)
                    s.RebuildHomeLootAndHatchesForWorldLoad();
            }
            if (s.EquipmentState != null)
            {
                if (!ws.PlayerEquipment.IsEmpty)
                {
                    s.EquipmentState.ApplySummary(ws.PlayerEquipment);
                    foreach (object slot in new List<object>(s.EquipmentState.Slots.Keys))
                    {
                        string equippedId = V.Str(s.EquipmentState.Slots[slot]);
                        if (equippedId != "" && !s.EquipmentState.CanEquip(equippedId))
                        {
                            s.EquipmentState.Unequip(V.Str(slot));
                            s.InventoryState?.AddItem(equippedId, 1);
                        }
                    }
                }
                else
                {
                    s.EquipmentState.Slots.Clear();
                }
            }
            s.RecomputeEncumbranceInternal();
            if (s.MetaProgressionState != null && !ws.MetaProgressionSummary.IsEmpty)
                s.MetaProgressionState.ApplySummary(ws.MetaProgressionSummary);
            s.UniqueItemState?.ApplySummary(ws.UniqueItemSummary);
            if (s.SynapticSeaWorld != null && !ws.WorldSummary.IsEmpty)
                s.SynapticSeaWorld.ApplySummary(ws.WorldSummary);
            ApplyVisitedShips(s, ws.VisitedShips);
            s.WorldTime = ws.WorldTime;
            if (ws.CurrentLocation != "")
            {
                ShipInstance active = s.VisitedShips.GetOrDefault(ws.CurrentLocation);
                if (active == null)
                {
                    s.Log.Warning("PlayableGeneratedShip: world load — current_location '" + ws.CurrentLocation + "' missing from visited_ships");
                    return true;
                }
                if (!s.ActivateDerelictFromInstanceInternal(active, ws.PlayerPositionInShip))
                {
                    s.Log.Warning("PlayableGeneratedShip: world load — failed to re-activate derelict '" + ws.CurrentLocation + "'");
                    return true;
                }
            }
            foreach (DockPortBarrier b in s.DockBarriers)
            {
                if (b.IsValid && ws.OpenedPorts.Contains(b.MarkerId))
                    b.SetOpened(true);
            }
            foreach (object edgeV in ws.DockEdges)
            {
                if (!(edgeV is GdDict edge))
                    continue;
                s.EnsureDerelictGeometryInternal(s.FindShipByIdInternal(V.Str(edge.Get("mobile", ""))));
                s.EnsureDerelictGeometryInternal(s.FindShipByIdOrMarkerInternal(V.Str(edge.Get("host", ""))));
            }
            ApplyDockingSnapshot(s, ws);
            return true;
        }

        /// <summary>
        /// Every retained ship's summary keyed by marker id; under the demo's <c>world_persistence.cross_run</c> block only
        /// the active derelict, the piloted / boarded ships and dock-edge endpoints are kept (Godot _build_world_snapshot).
        /// </summary>
        internal static GdDict VisitedShipsForSave(RunSession s, GdArray dockEdges, string pilotedShipId, string aboardShipId)
        {
            var visited = new GdDict();
            foreach (KeyValuePair<string, ShipInstance> kv in s.VisitedShips)
                visited[kv.Key] = kv.Value.GetSummary();
            if (!s.DemoCrossRunBlocked)
                return visited;
            var keepMarkers = new HashSet<string>();
            var keepShipIds = new HashSet<string>();
            if (s.AwayFromStart && s.CurrentShip != null)
                keepMarkers.Add(s.CurrentShip.MarkerId);
            if (pilotedShipId != "")
                keepShipIds.Add(pilotedShipId);
            if (aboardShipId != "")
                keepShipIds.Add(aboardShipId);
            foreach (object edgeV in dockEdges)
            {
                if (!(edgeV is GdDict edge))
                    continue;
                string edgeHost = V.Str(edge.Get("host", ""));
                string edgeMobile = V.Str(edge.Get("mobile", ""));
                if (edgeHost.Length > 0)
                    keepMarkers.Add(edgeHost);
                if (edgeMobile.Length > 0)
                    keepShipIds.Add(edgeMobile);
            }
            var demoKept = new GdDict();
            foreach (object keptMid in visited.Keys)
            {
                object summV = visited[keptMid];
                string summShipId = summV is GdDict sd ? V.Str(sd.Get("ship_id", "")) : "";
                if (keepMarkers.Contains(V.Str(keptMid)) || (summShipId.Length > 0 && keepShipIds.Contains(summShipId)))
                    demoKept[V.Str(keptMid)] = summV;
            }
            return demoKept;
        }

        /// <summary>The retained-ship registry as saved from the live session (run snapshots carry it since gate2-current-run-6).</summary>
        internal static GdDict VisitedShipsForSave(RunSession s) => VisitedShipsForSave(
            s,
            CurrentDockEdges(s),
            s.PilotedShip != null ? s.PilotedShip.ShipId : "",
            s.CurrentOccupancy != null ? s.CurrentOccupancy.ShipId : "");

        /// <summary>Home breach environment restore; the breach zone is rebuilt when a saved environment exists and the player is home.</summary>
        internal static void ApplyHomeBreachEnvironment(RunSession s, GdDict environment)
        {
            if (s.HomeShip == null)
                return;
            s.HomeShip.BreachEnvironmentSummary = environment.DeepCopy();
            if (!environment.IsEmpty && !s.AwayFromStart)
                s.RebuildBreachZoneForWorldLoad();
        }

        /// <summary>Replaces the home ship's carts with the saved ones and respawns their controls (idempotent per cart id).</summary>
        internal static void ApplyHomeCarts(RunSession s, GdArray carts)
        {
            if (s.HomeShip == null)
                return;
            s.HomeShip.GetCarts().Clear();
            foreach (object cd in carts)
            {
                if (cd is GdDict cdDict)
                {
                    CartState cart = CartState.Create();
                    cart.ApplySummary(cdDict);
                    s.HomeShip.GetCarts().Add(cart);
                }
            }
            s.SpawnCartControlsForShipInternal(s.HomeShip);
        }

        /// <summary>Rebuilds the retained-ship registry from saved summaries (entries that fail to apply are dropped).</summary>
        internal static void ApplyVisitedShips(RunSession s, GdDict visited)
        {
            s.VisitedShips.Clear();
            foreach (object mid in visited.Keys)
            {
                ShipInstance inst = ShipInstance.Create("", "", new ShipBlueprint(), null, null);
                if (inst.ApplySummary(visited[mid]))
                    s.VisitedShips[V.Str(mid)] = inst;
            }
        }

        /// <summary>Restores the piloted pointer, dock-edge set, and occupancy (every saved docking field is read here).</summary>
        public static void ApplyDockingSnapshot(RunSession s, WorldSnapshot ws)
        {
            if (ws.PilotedShipId != "")
            {
                ShipInstance p = s.FindShipByIdInternal(ws.PilotedShipId);
                if (p != null)
                    s.PilotedShip = p;
            }
            foreach (object edgeV in ws.DockEdges)
            {
                if (!(edgeV is GdDict edge))
                    continue;
                ShipInstance mobile = s.FindShipByIdInternal(V.Str(edge.Get("mobile", "")));
                ShipInstance host = s.FindShipByIdOrMarkerInternal(V.Str(edge.Get("host", "")));
                if (mobile == null || host == null)
                    continue;
                if (V.Str(edge.Get("port_type", "airlock")) == "hangar")
                {
                    s.RedockBayedInternal(mobile, host, V.I64(edge.Get("slot_index", -1L)));
                }
                else if (mobile.ParentShip != host)
                {
                    ShipInstance savedPiloted = s.PilotedShip;
                    s.PilotedShip = mobile;
                    s.DockPilotedToInternal(host);
                    s.PilotedShip = savedPiloted;
                }
            }
            if (ws.AboardShipId != "")
            {
                ShipInstance a = s.FindShipByIdInternal(ws.AboardShipId);
                if (a != null)
                    s.CurrentOccupancy = a;
            }
        }
    }
}
