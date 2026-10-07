using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed class HomeJoinControl : SessionInteractable
    {
        public override string Kind => "home_join_control";
        public string ShipId = "";
        public string ActionId = "secure_connection";
        public bool DoorOpen;
        public WebInfestationState Web;
        public HullIntegrityState Hull;
        public string Prompt => ActionId=="cut_web_attachment" ? "Hold: cut biomatter mooring (plasma cutter, repair 2) | web " + GdMath.RoundI((Web?.Coverage ?? 0)*100) + "% | hull " + GdMath.RoundI((Hull?.AverageIntegrity() ?? 1)*100) + "%" : ActionId=="connection_door" ? (DoorOpen?"Close home connection door":"Open home connection door") : ActionId=="secure_connection" ? "Hold: weld home connection (2 hull plating, welder, repair 2)"
            : "Hold: install home propulsion (nozzle, fuel line, circuit board, welder, repair 4)";
    }
    public sealed partial class RunSession
    {
        public readonly List<HomeJoinControl> HomeJoinControls = new List<HomeJoinControl>();
        internal bool RestoringConnections;
        bool _switchingBoardedContext;
        // Sea coordinates are distinct from the retained local scene frame used for walking.
        internal Vec3 StartingHomeSeaPosition;
        public Vec3 HomeSeaPosition = Vec3.Zero;
        public string HomeSeaMarkerId = "";
        internal Vec3? RestoredActiveScenePosition;
        internal bool HasSecuredHomeExtension()
        {
            if (HomeShip == null) return false;
            foreach (var child in HomeShip.DockedShips)
                if (child.DockingPorts.Count > 0 && child.DockingPorts[0] is GdDict edge
                    && edge.GetString("connection_kind") == "secured") return true;
            return false;
        }
        public bool IsHomeMember(ShipInstance ship)
        {
            var seen=new HashSet<IDockableShip>();
            for(var cursor=(IDockableShip)ship;cursor!=null && seen.Add(cursor);cursor=cursor.ParentShip)
            {
                if(ReferenceEquals(cursor,HomeShip)) return true;
                if(cursor.DockingPorts.Count==0 || !(cursor.DockingPorts[0] is GdDict edge)
                    ||edge.GetString("connection_kind")!="secured") return false;
            }
            return false;
        }
        bool MoorRecoveredVesselAtHome()
        {
            var ship=PilotedShip;
            RecomputeOccupancy();
            if(ship==null||CurrentOccupancy!=ship||!ship.GetAccess().HasAccess(PLAYER_LOCAL_ID)
                ||!CurrentSystemsOps().GetBool("propulsion"))
            { Log.Warning("Reclamation requires boarding, owned repaired propulsion and sufficient load capacity");return false; }
            if(!HomeJoinPlanner.TryPlan(HomeShip,ship,out var h,out var m,out string reason))
            { Log.Warning("Home mooring denied: "+reason);EmitTravelDeniedSfx();return false; }
            SyncCurrentShipCombatSummary();SyncCurrentShipArcSummary();SyncCurrentShipBreachEnvironment();SyncCurrentShipPillarSummaries();
            var carry=CapturePlayerCarry();var children=CaptureSubtree();
            var previousPose=ship.SceneRoot.GlobalTransform;
            var result=DockingManager.Dock(HomeShip,ship,DockingManager.HostPortToWorld(HomeShip,h),m);
            if(!result.GetBool("success")) {Log.Warning("Home mooring denied: "+result.GetString("reason"));return false;}
            ApplyPlayerCarry(carry);RepositionSubtree(children);
            RebaseCombatPositions(ship,previousPose,ship.SceneRoot.GlobalTransform);
            SynapticSeaWorld?.SetPlayerPosition(HomeSeaPosition);
            CurrentOccupancy=ship;
            if(CurrentShip!=ship) ActivateBoardedContext(ship);
            AwayFromStart=true; // active context and local services remain the recovered vessel's.
            ConfigureThreatRuntimeForCurrentShip();RebuildHomeJoinControls();RequestSave();EmitDockLandSfx();return true;
        }
        internal void RefreshThreatsAfterConnectionRestore() => ConfigureThreatRuntimeForCurrentShip();
        static void RebaseCombatPositions(ShipInstance ship, Xform3 from, Xform3 to)
        {
            var inverse=SessionMath.AffineInverse(from);
            foreach(var value in ship.CombatSummary.GetArrayOrEmpty("threats"))
                if(value is GdDict threat)
                    foreach(string key in new[]{"world_position","last_known_position"})
                    {
                        var position=threat.GetArrayOrEmpty(key);if(position.Count<3)continue;
                        Vec3 moved=to*(inverse*Vec3.FromArray(position));
                        threat[key]=GdArray.Of((double)moved.X,(double)moved.Y,(double)moved.Z);
                    }
        }

        public void RebuildHomeJoinControls()
        {
            foreach(var old in HomeJoinControls) Despawn(old); HomeJoinControls.Clear();
            foreach(var ship in AllKnownShipsInternal())
                if(ship!=null && RootValid(ship.SceneRoot) && WebFor(ship).AttachedToWeb
                    && ((ship==HomeShip || ship==LifeboatShip) ? HomeObjectivesComplete : ship==CurrentShip))
                {
                    Vec3 at;
                    if(ship==LifeboatShip)
                        at=BridgeTerminals.FirstOrDefault(t=>t.ShipId==ship.ShipId)?.LocalPosition ?? new Vec3(0,.4f,0);
                    else if(ship==HomeShip)
                        at=(Loader?.GetLootContainerSpecsCopy().Cast<GdDict>().FirstOrDefault(c=>c.GetString("room_id")=="cargo_01")?.Get("position") as Vec3?) ?? new Vec3(0,.4f,0);
                    else
                    {
                        var port=DockPorts.ForDerelict(ship.BuiltLayout);
                        if(!(port.Get("position") is Vec3 point) || !(port.Get("facing") is Vec3 facing))continue;
                        at=point-facing*1.1f+new Vec3(0,.4f,0);
                    }
                    HomeJoinControls.Add(Spawn(new HomeJoinControl {ShipId=ship.ShipId,ActionId="cut_web_attachment",NodeName="WebMooring_"+ship.ShipId,
                        Parent=ship.SceneRoot,LocalPosition=at,InteractionRadius=1.8,Web=WebFor(ship),Hull=HullFor(ship)}));
                }
            if(HomeObjectivesComplete && HomeShip!=null && RootValid(HomeShip.SceneRoot) && HomeShip.Mobility.GetString("engine_id").Length==0)
            {
                var floor=AssemblyMobility.Floors(HomeShip.BuiltLayout).OrderByDescending(c=>c.DistanceSquaredTo(Vec3.Zero)).FirstOrDefault();
                var control=new HomeJoinControl { ShipId=HomeShip.ShipId, ActionId="commission_home_propulsion",
                    NodeName="HomePropulsionInstallation", Parent=HomeShip.SceneRoot, LocalPosition=floor+new Vec3(0,0.4,0), InteractionRadius=1.8 };
                HomeJoinControls.Add(Spawn(control));
            }
            foreach(var ship in AllKnownShipsInternal())
                if(ship!=null && ReferenceEquals(ship.ParentShip,HomeShip) && ship.DockingPorts.Count>0
                    && ship.DockingPorts[0] is GdDict edge && edge.GetString("connection_kind")=="moored"
                    && edge.GetDictOrEmpty("mobile_local_port").GetString("site_id").StartsWith("exterior-v1:"))
                {
                    var port=DockingManager.UnpackPort(edge.GetDictOrEmpty("mobile_local_port"));
                    var control=new HomeJoinControl { ShipId=ship.ShipId, NodeName="HomeConnectionWeld_"+ship.ShipId,
                        Parent=ship.SceneRoot, LocalPosition=(Vec3)port["position"]-(Vec3)port["facing"]*0.9f+new Vec3(0,0.4,0), InteractionRadius=1.8 };
                    HomeJoinControls.Add(Spawn(control));
                }
            foreach(var ship in AllKnownShipsInternal())
                if(ship!=null && ReferenceEquals(ship.ParentShip,HomeShip) && ship.DockingPorts.Count>0
                    && ship.DockingPorts[0] is GdDict edge && edge.GetString("connection_kind")=="secured")
                    foreach(bool hostSide in new[]{true,false})
                    {
                        var port=DockingManager.UnpackPort(edge.GetDictOrEmpty(hostSide?"host_local_port":"mobile_local_port"));
                        var control=new HomeJoinControl { ShipId=ship.ShipId,ActionId="connection_door",DoorOpen=edge.GetBool("connection_open"),
                            NodeName="HomeConnectionDoor_"+ship.ShipId+(hostSide?"_home":"_wreck"),Parent=hostSide?HomeShip.SceneRoot:ship.SceneRoot,
                            LocalPosition=(Vec3)port["position"]-(Vec3)port["facing"]*0.9f+new Vec3(0,0.4,0),InteractionRadius=1.8 };
                        HomeJoinControls.Add(Spawn(control));
                    }
        }
        internal bool TryHomeJoinWork(Vec3 player)
        {
            foreach(var control in NearestInteractables(HomeJoinControls,player))
            {
                if(!control.IsPlayerInDirectRangeStrict(player)||!HasInteractionSightAndReach(control)) continue;
                var ship=FindShipByIdInternal(control.ShipId);
                if(control.ActionId=="connection_door")
                {
                    if(ship!=null && ship.GetAccess().HasAccess(PLAYER_LOCAL_ID) && ship.DockingPorts.Count>0 && ship.DockingPorts[0] is GdDict edge)
                    {edge["connection_open"]=!edge.GetBool("connection_open");RebuildHomeJoinControls();RequestSave();}
                    return true;
                }
                if(WorkActionDriver==null || WorkActionDriver.IsWorking()) return true;
                var inv=InventoryState?.Items.DeepCopy() ?? new GdDict();
                bool cutting=control.ActionId=="cut_web_attachment";
                bool tool=cutting ? inv.GetInt("plasma_cutter")>0 : inv.GetInt("welder")>0 || inv.GetInt("welding_lance")>0;
                var ctx=new GdDict {{"tool_class",tool?(cutting?"plasma_cutter":"welder"):""},{"skill_id","repair"},
                    {"skill_level",PlayerProgression?.GetSkillLevel("repair") ?? 0},{"inventory",inv}};
                if(ship==null||!(cutting ? CanCutWeb(ship) : ship.GetAccess().HasAccess(PLAYER_LOCAL_ID)))
                {BlockWorkAction(control.ActionId,control.ShipId,"access");EmitTravelDeniedSfx();return true;}
                if(VitalsState!=null&&VitalsState.Stamina<=.001)
                {BlockWorkAction(control.ActionId,control.ShipId,"exhausted");EmitTravelDeniedSfx();return true;}
                if(!WorkActionDriver.StartAction(control.ActionId,control.ShipId,ctx))
                { Log.Warning("Home work blocked: "+WorkActionDriver.Work?.BlockReason);RefreshWorkActionHud(); EmitTravelDeniedSfx();return true; }
                _workRequiresHold=HoldToWorkEnabled;RefreshWorkActionHud();return true;
            }
            return false;
        }
        GdDict CompleteHomeWork(string action)
        {
            string id=WorkActionDriver.Work.TargetId;
            var ship=FindShipByIdInternal(id);
            var control=HomeJoinControls.FirstOrDefault(c=>c.IsValid && c.ShipId==id && c.ActionId==action);
            var def=WorkActionDriver.Catalog.GetAction(action);
            var consumed=def.GetDictOrEmpty("materials_consumed");
            if(ship==null||control==null||!HasInteractionSightAndReach(control)||!(action=="cut_web_attachment" ? CanCutWeb(ship) : ship.GetAccess().HasAccess(PLAYER_LOCAL_ID))
                ||InventoryState==null || !(action=="cut_web_attachment" ? InventoryState.GetQuantity("plasma_cutter")>0 : InventoryState.GetQuantity("welder")>0||InventoryState.GetQuantity("welding_lance")>0)
                ) return HomeWorkFailure("conditions_changed");
            foreach(var item in consumed) if(InventoryState.GetQuantity(V.Str(item.Key))<V.I64(item.Value)) return HomeWorkFailure("materials_changed");
            if(action=="cut_web_attachment")
            {
                if(!WebFor(ship).AttachedToWeb)return HomeWorkFailure("already_detached");
                WebFor(ship).CutFree();
                if(ship==CurrentShip)BuildBreachSealPoints();
                SetHazardFeedbackLine("Biomatter mooring cut. Residual infestation recedes; repair hull breaches before departure.");
            }
            else if(action=="secure_connection")
            {
                if(!ReferenceEquals(ship.ParentShip,HomeShip)||ship.DockingPorts.Count==0||!(ship.DockingPorts[0] is GdDict edge)) return HomeWorkFailure("connection_changed");
                var h=DockingManager.UnpackPort(edge.GetDictOrEmpty("host_local_port"));
                var m=DockingManager.UnpackPort(edge.GetDictOrEmpty("mobile_local_port"));
                if(!HomeJoinPlanner.Validate(HomeShip,ship,h,m,out string reason)) return HomeWorkFailure(reason);
                edge["connection_kind"]="secured";
                edge["connection_open"]=false;
                SpawnHomeBridge();
            }
            else
            {
                if(ship!=HomeShip||ship.Mobility.GetString("engine_id").Length>0) return HomeWorkFailure("installation_changed");
                ship.Mobility=AssemblyMobility.CreateSpecification(ship,true);
                ship.GetAccess().Claim(PLAYER_LOCAL_ID);
                SpawnHomeBridge();
            }
            foreach(var item in consumed) InventoryState.RemoveItem(V.Str(item.Key),V.I64(item.Value));
            EmitTrainingEvent(action=="cut_web_attachment"?"decontaminate_zone":"weld_panel",id); RefreshInventoryHud();RebuildHomeJoinControls();RequestSave();
            return new GdDict {{"ok",true}};
        }
        bool CanCutWeb(ShipInstance ship) => ship!=null && (ship.GetAccess().OwnerId.Length==0 || ship.GetAccess().HasAccess(PLAYER_LOCAL_ID));
        GdDict HomeWorkFailure(string reason)
        {
            WorkActionDriver.Work.Interrupt();WorkActionDriver.Work.BlockReason=reason; Log.Warning("Home work cancelled without consuming materials: "+reason);
            return new GdDict {{"ok",false},{"reason",reason}};
        }
        void BlockWorkAction(string actionId,string targetId,string reason)
        {
            WorkActionDriver.Work=new WorkActionState();
            WorkActionDriver.Work.ConfigureAction(actionId,WorkActionDriver.Catalog.GetAction(actionId));
            WorkActionDriver.Work.TargetId=targetId;WorkActionDriver.Work.Status=WorkActionState.STATUS_BLOCKED;
            WorkActionDriver.Work.BlockReason=reason;_workRequiresHold=false;RefreshWorkActionHud();
            Log.Warning("Work blocked: "+reason);
        }
        internal void SpawnHomeBridge()
        {
            if(HomeShip==null||(!HasSecuredHomeExtension()&&HomeShip.Mobility.GetString("engine_id").Length==0)||!RootValid(HomeShip.SceneRoot)) return;
            foreach(var old in BridgeTerminals.Where(t=>t.ShipId==HomeShip.ShipId).ToList()) {Despawn(old);BridgeTerminals.Remove(old);}
            var floor=AssemblyMobility.Floors(HomeShip.BuiltLayout).OrderBy(c=>c.DistanceSquaredTo(Vec3.Zero)).FirstOrDefault();
            var terminal=new BridgeTerminal();terminal.Configure(HomeShip.ShipId,floor+new Vec3(0,0.4,1),1.8);
            terminal.Parent=HomeShip.SceneRoot;terminal.LoginRequested+=OnLoginRequested;BridgeTerminals.Add(Spawn(terminal));
        }
        GdDict TravelHomeAssembly(ShipMarker marker, GdDict capacity)
        {
            if(marker==null || ScannerState==null || !SynapticSeaWorld.MarkersInRange(ScannerState.RangeRadius).Any(m=>m.MarkerId==marker.MarkerId))
                return new GdDict{{"success",false},{"reason","out_of_range"}};
            if(!HomeObjectivesComplete || VisitedShips.Count==0)
                return new GdDict{{"success",false},{"reason","home_assembly_requires_onboarding_and_first_expedition"}};
            if(marker.MarkerId==HomeSeaMarkerId) return new GdDict{{"success",false},{"reason","already_here"}};
            if(HomeShip.ParentShip!=null || !DockingManager.TryConnectedMembers(HomeShip,out var members,out string reason))
                return new GdDict{{"success",false},{"reason","invalid_home_assembly"}};
            foreach(var member in members)
                if(member is ShipInstance ship && WebFor(ship).AttachedToWeb)
                    return new GdDict{{"success",false},{"reason","assembly_member_moored_to_biomatter:"+ship.ShipId}};
            if(!CurrentSystemsOps().GetBool("propulsion") || !CurrentSystemsOps().GetBool("navigation"))
                return new GdDict{{"success",false},{"reason","local_control_or_propulsion_offline"},{"capability",capacity}};
            // A large home parks at the surveyed contact. It does not force its whole hull
            // through a shuttle berth, generate/claim the contact, or detach secured members.
            HomeSeaPosition=marker.Position;HomeSeaMarkerId=marker.MarkerId;
            SynapticSeaWorld.SetPlayerPosition(HomeSeaPosition);
            SetHazardFeedbackLine("Home assembly arrived. Board the independent shuttle to explore this contact.");
            RequestSave();EmitTrainingEvent("plot_course",marker.MarkerId);EmitDockLandSfx();
            return new GdDict{{"success",true},{"reason","ok"},{"mode","home_assembly_transit"},{"marker_id",marker.MarkerId},{"capability",capacity}};
        }
        Vec3 DerelictScenePosition(IShipLoaderView scene)
        {
            if(RestoredActiveScenePosition.HasValue) return RestoredActiveScenePosition.Value;
            if(!HasSecuredHomeExtension() || !DockingManager.TryConnectedMembers(HomeShip,out var members,out _)) return DERELICT_DOCK_OFFSET;
            var occupied=new List<Vec3>();
            foreach(var member in members)
                if(member is ShipInstance ship && RootValid(ship.SceneRoot))
                    occupied.AddRange(AssemblyMobility.Floors(ship.BuiltLayout).Select(p=>ship.SceneRoot.GlobalTransform*p));
            var destination=AssemblyMobility.Floors(scene.GetLayoutCopy());
            if(occupied.Count==0 || destination.Count==0) return DERELICT_DOCK_OFFSET;
            // Both hulls retain their authored scale. Reserve floor half-extents, walls and
            // the independent shuttle's approach rather than overlapping the retained home.
            return new Vec3(occupied.Min(p=>p.X)-destination.Max(p=>p.X)-16,0,0);
        }
        void ActivateBoardedContext(ShipInstance target)
        {
            if(_switchingBoardedContext || target==null || target==CurrentShip) return;
            _switchingBoardedContext=true;
            try
            {
                SyncCurrentShipCombatSummary();SyncCurrentShipArcSummary();SyncCurrentShipBreachEnvironment();SyncCurrentShipPillarSummaries();
                CurrentShip=target;AwayFromStart=target!=HomeShip;
                ResetTooltipFocus();ClearDerelictObjectives();ClearLootContainers();ClearRepairPoints();ClearBreachSealPoints();
                HallucinationManager?.ClearAll();ClearFireZones();ClearFireSuppressionPoints();ClearExtinguisherRechargePort();
                RestoreAuthoredPortalStates();RestoreModuleIntegrityForCurrentShip();RestoreOrPopulateComponentPlacementForCurrentShip();
                ConfigureThreatRuntimeForCurrentShip();
                if(target!=HomeShip) BuildDerelictObjectives();
                BuildLootContainers();BuildSealedHatches();BuildRepairPoints();BuildBreachZone(false);BuildBreachSealPoints();
                BuildFireZones();BuildArcZone();RestoreArcSummaryForCurrentShip();BuildFireSuppressionPoints();BuildExtinguisherRechargePort();
                BuildCraftingStations();BuildProductionStations();
                if(target.SceneRoot is IShipLoaderView view) Events.RaiseAffordancesRebuilt(view);
                if(target==HomeShip && Loader!=null) {Events.RaiseTrackerObjectivesSet(Loader.GetObjectiveSpecsCopy());RefreshHomeTrackerCompleted();}
            }
            finally {_switchingBoardedContext=false;}
        }
    }
}
