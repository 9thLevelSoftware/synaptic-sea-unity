// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: _build_world_snapshot (10830-10917),
// _current_dock_edges / _opened_port_marker_ids (10923-10972), _apply_world_snapshot (11043-11161) and the docking
// snapshot model half _apply_docking_snapshot / _redock_bayed (11168-11221).
using System;
using System.Collections.Generic;
using System.Linq;
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
            if (s.ComponentGenerationRestoreInProgress) return null;
            s.SyncCombatSummaryForSave(); s.SyncArcSummaryForSave();
            s.SyncBreachEnvironmentForSave(); s.SyncPillarSummariesForSave();
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
            if (!s.GameClock.IsDefaultConfiguration) ws.WorldClock = s.GameClock.GetSummary();
            if (s.Scene != null && s.Scene.HasPlayer)
            {
                Vec3 p = s.Scene.PlayerPosition;
                ws.PlayerPositionInShip = GdArray.Of((double)p.X, (double)p.Y, (double)p.Z);
            }
            if (!s.CompleteGenerationEnabled && !s.LifeboatCommissioned && s.LifeboatShip?.SystemsManager != null && s.ShipSystemsManager != null)
                s.LifeboatShip.SystemsManager.ApplySummary(s.ShipSystemsManager.GetSummary());
            ws.MobileHomeState = new GdDict { { "version", 1L }, { "lifeboat_commissioned", s.LifeboatCommissioned },
                { "lifeboat", s.LifeboatShip?.GetSummary() ?? new GdDict() }, { "home_mobility", s.HomeShip?.Mobility.DeepCopy() ?? new GdDict() } };
            if(s.HomeSeaMarkerId.Length>0)
                ws.MobileHomeState["home_location"]=new GdDict{{"version",1L},{"marker_id",s.HomeSeaMarkerId},
                    {"sea_position",GdArray.Of((double)s.HomeSeaPosition.X,(double)s.HomeSeaPosition.Y,(double)s.HomeSeaPosition.Z)}};
            if(s.HasSecuredHomeExtension() && s.CurrentShip!=null && !s.IsHomeMember(s.CurrentShip) && s.CurrentShip.SceneRoot!=null)
            {
                Vec3 position=s.CurrentShip.SceneRoot.GlobalTransform*Vec3.Zero;
                ws.MobileHomeState["active_scene_position"]=GdArray.Of((double)position.X,(double)position.Y,(double)position.Z);
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
                var edge = new GdDict
                {
                    { "host", parent.MarkerId },
                    { "host_ship_id", parent.ShipId },
                    { "mobile", inst.ShipId },
                    { "port_type", portType },
                    { "slot_index", slotIndex },
                };
                if (portType == "airlock" && inst.DockingPorts.Count > 0 && inst.DockingPorts[0] is GdDict connection
                    && connection.GetInt("connection_version") == 1)
                {
                    foreach (string field in new[] { "connection_version", "host_local_port", "mobile_local_port", "connection_kind" })
                        edge[field] = connection.Get(field);
                }
                if(inst.DockingPorts.Count>0 && inst.DockingPorts[0] is GdDict doorConnection && doorConnection.Has("connection_open"))
                    edge["connection_open"]=doorConnection.GetBool("connection_open");
                edges.Add(edge);
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
            var homeLocation=ws.MobileHomeState.GetDictOrEmpty("home_location");
            if (ws.MobileHomeState.Has("starting_home_anchor")) return false;
            if((ws.MobileHomeState.Has("home_location") && (homeLocation.GetInt("version")!=1 || homeLocation.GetString("marker_id").Length==0
                || !ValidScenePosition(homeLocation.GetArrayOrEmpty("sea_position"))))
                || (ws.MobileHomeState.Has("active_scene_position") && !ValidScenePosition(ws.MobileHomeState.GetArrayOrEmpty("active_scene_position"))))
            {s.Log.Warning("World load rejected invalid mobile-home position before changing live state");return false;}
            if (!ws.MobileHomeState.IsEmpty && (ws.MobileHomeState.GetInt("version") != 1
                || ws.MobileHomeState.GetDictOrEmpty("lifeboat").GetString("ship_id") != "lifeboat"
                || !AssemblyMobility.ValidSpecification(ws.MobileHomeState.GetDictOrEmpty("home_mobility"))
                || !AssemblyMobility.ValidSpecification(ws.MobileHomeState.GetDictOrEmpty("lifeboat").GetDictOrEmpty("mobility"))))
            { s.Log.Warning("World load rejected unsupported mobile-home ownership before changing live state"); return false; }
            foreach (var value in ws.VisitedShips.Values)
                if (value is GdDict ship && ship.Has("mobility")
                    && (!AssemblyMobility.ValidSpecification(ship.GetDictOrEmpty("mobility"))
                        || !OwnedInstallation(ship.GetString("ship_id"), ship.GetDictOrEmpty("mobility"))))
                { s.Log.Warning("World load rejected invalid retained propulsion ownership before changing live state"); return false; }
            if (!ws.MobileHomeState.IsEmpty
                && (!OwnedInstallation(s.HomeShip?.ShipId ?? "ship_start", ws.MobileHomeState.GetDictOrEmpty("home_mobility"))
                    || !OwnedInstallation("lifeboat", ws.MobileHomeState.GetDictOrEmpty("lifeboat").GetDictOrEmpty("mobility"))))
            { s.Log.Warning("World load rejected invalid home propulsion ownership before changing live state"); return false; }
            if (!ValidateConnectionSnapshot(s, ws, out _, out string graphFailure))
            {
                s.Log.Warning("World load rejected connection graph before changing live state: " + graphFailure);
                return false;
            }
            RunSnapshot homeSnap = RunSnapshot.FromDict(ws.HomeShip, s.ComponentIntegrationEnabled ? RunSnapshot.ComponentIntegrationVersion : SaveLoadService.CURRENT_SLICE_VERSION, s.Deps.Engine.VersionString);
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
            s.HomeSeaPosition = !homeLocation.IsEmpty ? Vec3.FromArray(homeLocation.GetArrayOrEmpty("sea_position")) : s.StartingHomeSeaPosition;
            s.HomeSeaMarkerId=homeLocation.GetString("marker_id");
            ApplyVisitedShips(s, ws.VisitedShips);
            if(!ws.MobileHomeState.IsEmpty && s.HomeShip!=null) s.HomeShip.Mobility = ws.MobileHomeState.GetDictOrEmpty("home_mobility").DeepCopy();
            s.LifeboatCommissioned = ws.MobileHomeState.IsEmpty ? ws.VisitedShips.Count > 0
                : ws.MobileHomeState.GetBool("lifeboat_commissioned");
            if (s.LifeboatShip != null)
            {
                if (!ws.MobileHomeState.IsEmpty) s.LifeboatShip.ApplySummary(ws.MobileHomeState.GetDictOrEmpty("lifeboat"));
                else s.LifeboatShip.SystemsManager.ApplySummary(s.ShipSystemsManager.GetSummary());
            }
            s.WorldTime = ws.WorldTime;
            if (!ws.WorldClock.IsEmpty) s.GameClock.ApplySummary(ws.WorldClock);
            if (ws.CurrentLocation != "")
            {
                ShipInstance active = s.VisitedShips.GetOrDefault(ws.CurrentLocation);
                if (active == null)
                {
                    s.Log.Warning("PlayableGeneratedShip: world load - current_location '" + ws.CurrentLocation + "' missing from visited_ships");
                    return !s.CompleteGenerationEnabled;
                }
                s.RestoringConnections=true;
                s.RestoredActiveScenePosition=ws.MobileHomeState.Has("active_scene_position")
                    ? Vec3.FromArray(ws.MobileHomeState.GetArrayOrEmpty("active_scene_position")) : (Vec3?)null;
                bool activated;
                try { activated=s.ActivateDerelictFromInstanceInternal(active, ws.PlayerPositionInShip); }
                finally { s.RestoringConnections=false;s.RestoredActiveScenePosition=null; }
                if (!activated)
                {
                    s.Log.Warning("PlayableGeneratedShip: world load - failed to re-activate derelict '" + ws.CurrentLocation + "'");
                    return !s.CompleteGenerationEnabled;
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
                ShipInstance mobileEndpoint = s.FindShipByIdInternal(V.Str(edge.Get("mobile", "")));
                ShipInstance hostEndpoint = edge.Has("host_ship_id")
                    ? s.FindShipByIdInternal(edge.GetString("host_ship_id"))
                    : s.FindShipByIdOrMarkerInternal(V.Str(edge.Get("host", "")));
                s.EnsureDerelictGeometryInternal(mobileEndpoint); s.EnsureDerelictGeometryInternal(hostEndpoint);
                if (s.CompleteGenerationEnabled && (mobileEndpoint?.SceneRoot?.IsValid != true || hostEndpoint?.SceneRoot?.IsValid != true)) return false;
            }
            ApplyDockingSnapshot(s, ws);
            if (s.ComponentIntegrationEnabled)
            {
                ComponentRawShipSystems.ApplyExactHealth(s.HomeShip?.SystemsManager, homeSnap.ShipSystemsSummary);
                ComponentRawShipSystems.ApplyExactHealth(s.LifeboatShip?.SystemsManager, ws.MobileHomeState.GetDictOrEmpty("lifeboat").Get("systems"));
                foreach (var pair in ws.VisitedShips)
                    ComponentRawShipSystems.ApplyExactHealth(s.VisitedShips.GetOrDefault(V.Str(pair.Key))?.SystemsManager, ((GdDict)pair.Value).Get("systems"));
            }
            if(s.PilotedShip!=null && s.IsHomeMember(s.PilotedShip))s.SetPilotedShip(s.PilotedShip);
            s.RefreshThreatsAfterConnectionRestore();
            s.SpawnHomeBridge();
            s.RebuildHomeJoinControls();
            s.RecomputeOccupancy();
            if (s.CompleteGenerationEnabled) s.RestoreGenerationPlayerPose(ws);
            return true;
        }

        public static bool OwnedInstallation(string shipId, GdDict spec) => spec.GetString("engine_id").Length == 0
            || spec.GetString("engine_id") == "propulsion:" + shipId;
        static bool ValidScenePosition(GdArray position)
        {
            if(position.Count!=3)return false;
            foreach(var coordinate in position)
            {double value=V.F64(coordinate,double.NaN);if(double.IsNaN(value)||double.IsInfinity(value))return false;}
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

        public static bool ValidateConnectionSnapshot(RunSession s, WorldSnapshot ws, out GdArray ordered, out string reason)
            => ValidateConnectionSnapshot(ws, s.HomeShip?.ShipId ?? "ship_start", s.LifeboatShip?.ShipId ?? "lifeboat", out ordered, out reason);

        /// <summary>Validates saved graph records without constructing or mutating a live session.</summary>
        public static bool ValidateConnectionSnapshot(WorldSnapshot ws, string homeShipId, string lifeboatShipId, out GdArray ordered, out string reason)
        {
            ordered = new GdArray(); reason = "ok";
            var ids = new HashSet<string>(StringComparer.Ordinal) { homeShipId, lifeboatShipId };
            var markers = new Dictionary<string, string>(StringComparer.Ordinal) { { "", homeShipId } };
            foreach (object marker in ws.VisitedShips.Keys)
            {
                if (!(ws.VisitedShips[marker] is GdDict ship)) continue;
                string id = ship.GetString("ship_id");
                if (id.Length == 0 || !ids.Add(id)) { reason = "duplicate_or_missing_ship_identity"; return false; }
                markers[V.Str(marker)] = id;
            }
            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var pending = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in ws.DockEdges)
            {
                if (!(value is GdDict edge)) { reason = "malformed_dock_edge"; return false; }
                string mobile = edge.GetString("mobile");
                string host = edge.GetString("host_ship_id");
                if (host.Length == 0) markers.TryGetValue(edge.GetString("host"), out host);
                if (string.IsNullOrEmpty(host) || !ids.Contains(host) || !ids.Contains(mobile) || host == mobile || parentOf.ContainsKey(mobile))
                { reason = "invalid_connection_member"; return false; }
                if (edge.Has("connection_version"))
                {
                    if (edge.GetInt("connection_version") != 1) { reason = "unsupported_connection_contract"; return false; }
                    var h = DockingManager.UnpackPort(edge.GetDictOrEmpty("host_local_port"));
                    var m = DockingManager.UnpackPort(edge.GetDictOrEmpty("mobile_local_port"));
                    string hsite = h.GetString("site_id"), msite = m.GetString("site_id");
                    if (hsite.Length == 0 || msite.Length == 0 || !DockPorts.PortsCompatible(h, m))
                    { reason = "invalid_connection_endpoints"; return false; }
                    if (!occupied.Add(host + ":" + hsite) || !occupied.Add(mobile + ":" + msite))
                    { reason = "connection_site_occupied"; return false; }
                    string kind = edge.GetString("connection_kind", "moored");
                    if (kind != "moored" && kind != "secured") { reason = "invalid_connection_kind"; return false; }
                }
                parentOf[mobile] = host; pending[mobile] = edge;
            }
            // Restore host transforms before descendants, independent of JSON/registry ordering.
            var ready = new HashSet<string>(ids, StringComparer.Ordinal);
            foreach (string mobile in pending.Keys) ready.Remove(mobile);
            while (pending.Count > 0)
            {
                string next = null;
                foreach (string mobile in pending.Keys)
                    if (ready.Contains(parentOf[mobile]) && (next == null || string.CompareOrdinal(mobile, next) < 0)) next = mobile;
                if (next == null) { reason = "dock_cycle"; return false; }
                ordered.Add(pending[next]); ready.Add(next); pending.Remove(next);
            }
            return true;
        }

        /// <summary>Restores the piloted pointer, dock-edge set, and occupancy (every saved docking field is read here).</summary>
        public static void ApplyDockingSnapshot(RunSession s, WorldSnapshot ws)
        {
            if (!ValidateConnectionSnapshot(s, ws, out GdArray restoreEdges, out string failure))
            { s.Log.Warning("Rejected docking restore: " + failure); if (s.ComponentIntegrationEnabled) throw new InvalidOperationException("required_connection_failed"); return; }
            if (ws.PilotedShipId != "")
            {
                ShipInstance p = s.FindShipByIdInternal(ws.PilotedShipId);
                if (p != null)
                    s.PilotedShip = p;
            }
            foreach (object edgeV in restoreEdges)
            {
                if (!(edgeV is GdDict edge))
                    continue;
                ShipInstance mobile = s.FindShipByIdInternal(V.Str(edge.Get("mobile", "")));
                ShipInstance host = edge.Has("host_ship_id")
                    ? s.FindShipByIdInternal(edge.GetString("host_ship_id"))
                    : s.FindShipByIdOrMarkerInternal(V.Str(edge.Get("host", "")));
                if (mobile == null || host == null)
                { if (s.ComponentIntegrationEnabled) throw new InvalidOperationException("required_connection_member_missing"); continue; }
                if (V.Str(edge.Get("port_type", "airlock")) == "hangar")
                {
                    s.RedockBayedInternal(mobile, host, V.I64(edge.Get("slot_index", -1L)));
                }
                else if (edge.Has("connection_version"))
                {
                    string hostId = edge.GetString("host_ship_id");
                    if (hostId.Length == 0 || host.ShipId != hostId)
                    { s.Log.Warning("World load rejected inconsistent connection host identity"); if (s.ComponentIntegrationEnabled) throw new InvalidOperationException("required_connection_host_mismatch"); continue; }
                    GdDict restored = DockingManager.RestoreConnection(host, mobile, edge);
                    if (!restored.GetBool("success")) { s.Log.Warning("World load rejected connection: " + restored.GetString("reason")); if (s.ComponentIntegrationEnabled) throw new InvalidOperationException("required_connection_failed"); }
                }
                else if (mobile.ParentShip != host)
                {
                    ShipInstance savedPiloted = s.PilotedShip;
                    s.PilotedShip = mobile;
                    try { GdDict docked = s.DockPilotedToInternal(host); if (s.ComponentIntegrationEnabled && !docked.GetBool("success")) throw new InvalidOperationException("required_connection_failed"); }
                    finally { s.PilotedShip = savedPiloted; }
                }
                if (s.ComponentIntegrationEnabled && (mobile.ParentShip != host || mobile.SceneRoot?.IsValid != true || host.SceneRoot?.IsValid != true)) throw new InvalidOperationException("required_connection_failed");
            }
            if (ws.AboardShipId != "")
            {
                ShipInstance a = s.FindShipByIdInternal(ws.AboardShipId);
                if (a != null)
                    s.CurrentOccupancy = a;
                else if (s.ComponentIntegrationEnabled) throw new InvalidOperationException("player_pose_owner_missing");
            }
        }
    }
}
