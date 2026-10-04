using System;
using System.IO;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Core.Session;
using SynapticSea.Tests.Session;

static class Program
{
    static List<object> rows = new List<object>();
    static void Record(string id, string hypothesis, object actual, string boundary)
    { rows.Add(new GdDict { { "id", id }, { "hypothesis", hypothesis }, { "actual", actual }, { "scope", "freshly_compiled_engine_free_Core_provisioned_diagnostic" }, { "boundary", boundary } }); }
    static GdDict Round(GdDict d) => (GdDict)GdJson.Parse(GdJson.Stringify(d));
    static GdDict CompartmentDoc() => new GdDict { { "compartments", GdArray.Of(new GdDict { { "compartment_id", "A" }, { "health", 1.0 }, { "breach_open", false } }, new GdDict { { "compartment_id", "B" }, { "health", 1.0 }, { "breach_open", false } }) } };
    static ShipRuntime TimeShip(string id)
    {
        var ship = ShipInstance.Create(id, "audit", null, null, null);
        ship.GetHull().Configure(CompartmentDoc());
        ship.GetWeb().Configure(new GdDict { { "attached_to_web", true }, { "seed_coverage", 0.1 }, { "growth_rate", 0.00001 }, { "damage_rate", 0.0000001 } });
        var runtime = new ShipRuntime(); runtime.Configure(ship); return runtime;
    }
    static void ComponentIdentity()
    {
        var catalog = new ComponentCatalog(); if (!catalog.LoadDefault()) throw new Exception("component catalog missing");
        foreach(string item in new[] { "power_coupling", "reactor_console" })
        {
        var source = new ComponentPlacementState();
        source.Placed.Add(new GdDict { { "component_instance_id", "original:A" }, { "component_id", item }, { "item_form", item }, { "room_id", "A" }, { "slot_kind", "wall" }, { "slot_index", 0L }, { "condition", 0.23 }, { "mass", 15.0 }, { "mounted", true } });
        var inventory = new GdDict(); var work = new WorkActionState { TargetId = "original:A", Status = WorkActionState.STATUS_COMPLETED };
        var down = ComponentMountResolver.ResolveDismount(work, source, inventory);
        var savedInventory = Round(inventory); var savedSource = new ComponentPlacementState(); savedSource.ApplySummary(Round(source.GetSummary()));
        var same = savedSource.Mount(item, "A", "wall", 0, savedInventory, catalog);
        var sameEntry = savedSource.GetEntry(same.GetString("instance_id"));
        var otherInventory = Round(inventory); var destination = new ComponentPlacementState();
        var other = destination.Mount(item, "B", "wall", 0, otherInventory, catalog);
        var otherEntry = destination.GetEntry(other.GetString("instance_id"));
        Record("component_identity_transfer_"+item, "Same-slot and cross-vessel remount should retain damage/identity unless authored replacement explicitly consumes repair resources", new GdDict { { "dismount", down }, { "inventory_roundtrip", inventory }, { "same_slot_result", same }, { "same_slot_entry", sameEntry }, { "other_slot_result", other }, { "other_slot_entry", otherEntry }, { "other_inventory_after", otherInventory } }, "Real placement/resolver/catalog; reactor_console is an actual definition, power_coupling is deliberately unknown negative case. Provisioned condition; normal wrench acquisition may be blocked. No UI reach or earned source asserted.");
        }
    }
    static void ElapsedTime()
    {
        foreach (double seconds in new[] { 300.0, 1800.0, 3600.0, 86400.0 })
        {
            var active = TimeShip("active"); for(double t=0;t<seconds;t+=3) active.Advance(Math.Min(3, seconds-t), Math.Min(seconds,t+3));
            var absent = TimeShip("absent"); absent.CatchUp(seconds);
            var segmented = TimeShip("segmented"); for(double t=0;t<seconds;t+=300) segmented.CatchUp(Math.Min(seconds,t+300));
            double afterFirst = absent.Ship.GetWeb().Coverage; absent.CatchUp(seconds);
            Record("elapsed_"+seconds, "Equal elapsed time in active, one long absence and multiple short absences; same-time catch-up must not double tick", new GdDict { { "seconds", seconds }, { "active_coverage", active.Ship.GetWeb().Coverage }, { "absent_coverage", afterFirst }, { "segmented_coverage", segmented.Ship.GetWeb().Coverage }, { "segmented_hull", segmented.Ship.GetHull().AverageIntegrity() }, { "absent_repeat_coverage", absent.Ship.GetWeb().Coverage }, { "active_hull", active.Ship.GetHull().AverageIntegrity() }, { "absent_hull", absent.Ship.GetHull().AverageIntegrity() }, { "absent_last_sim_time", absent.Ship.LastSimTime }, { "absent_substeps", absent.LazyBandFires } }, "Web/hull-only provisioned models; no active RunSession fire/food inference, no offline wall-clock assertion.");
        }
    }
    static void ProductionOutage()
    {
        var crop = new GdDict { { "crop_id", "diagnostic" }, { "display_name", "Diagnostic" }, { "produce_item_id", "ration_pack" }, { "produce_quantity", 2L }, { "growth_seconds", 60.0 }, { "water_cost", 2.0 }, { "power_cost", 3.0 }, { "required_skill_level", 0L } };
        var live = new HydroponicsState(); var started = live.Plant(crop, 0, 2, 3); live.Tick(30);
        var restored = new HydroponicsState(); restored.ApplySummary(Round(live.GetSummary()));
        // Production Tick signature has no power argument. Caller trace must establish whether it is suppressed during outage.
        restored.Tick(30); var ready = restored.GetSummary(); var first = restored.Harvest(); var twice = restored.Harvest();
        var insufficient = new HydroponicsState(); var denied = insufficient.Plant(crop, 0, 2, 0);
        Record("production_start_restore_outage", "Power is checked at start; diagnose whether post-start outage reaches the authoritative timer", new GdDict { { "start", started }, { "halfway_saved", live.GetSummary() }, { "restored_ready", ready }, { "harvest", first }, { "second_harvest", twice }, { "start_without_power", denied } }, "Provisioned crop; demonstrates model behavior/one-harvest guard. Runtime caller must be traced before declaring outage defect; continuous fuel consumption remains a design choice.");
    }
    static void AtmosphereScalar()
    {
        var powered = new GdDict { { "powered_ratio", 1.0 }, { "breach_count", 1L } };
        var air = new LifeSupportState(); air.Configure(new GdDict()); air.Tick(10,powered);
        Record("atmosphere_scalar", "Determine represented scope before claiming room isolation or distinguishing internal/exterior cuts", air.GetSummary(), "Ship scalar model has no room/portal input. This does not reproduce a particular internal-wall cut; geometric classification still requires Unity/caller diagnostics.");
    }
    static void CraftPowerGuard()
    {
        var station=new StationState(); station.Configure(new GdDict { { "station_kind","fabricator" }, { "powered",true } });
        station.StartRecipe("diagnostic",60); station.Tick(20); station.Powered=false; station.Tick(1000);
        var paused=Round(station.GetSummary()); var restored=new StationState(); restored.ApplySummary(paused); restored.Tick(60); var stillPaused=Round(restored.GetSummary());
        restored.Powered=true; restored.Tick(40); var completed=Round(restored.GetSummary());
        Record("craft_station_outage_guard", "Ordinary StationState must pause without adding outage time, persist progress and resume", new GdDict { { "paused",paused }, { "restored_after_unpowered_tick",stillPaused }, { "after_repowered",completed } }, "Real station model, provisioned recipe. Negative finding distinguishes functioning crafting outage guard from production timers; no reserved inventory or UI action path claimed.");
    }
    static ShipInstance MassShip(string id, int cells)
    {
        var manager=new ShipSystemsManager(); manager.Configure(manager.LoadDefinitions(),0,17);
        var ship=ShipInstance.Create(id,"audit",null,manager,null); ship.Inventory=ShipInventory.Create(5000);
        var floors=new GdArray(); for(int i=0;i<cells;i++) floors.Add(new GdDict { { "position",new Vec3(i*4,0,0) } });
        ship.BuiltLayout=new GdDict { { "structural_plan",new GdDict { { "floor_placements",floors } } } }; ship.Mobility=AssemblyMobility.CreateSpecification(ship,true); return ship;
    }
    static void NestedPayload()
    {
        var root=MassShip("carrier",10); var child=MassShip("scout",3); var nested=MassShip("nested",2);
        child.ParentShip=root; root.DockedShips.Add(child); child.DockingPorts.Add(new GdDict { { "connection_kind","moored" } });
        nested.ParentShip=child; child.DockedShips.Add(nested); nested.DockingPorts.Add(new GdDict { { "connection_kind","moored" } });
        root.Hangar=HangarBay.Create(1,3); root.Hangar.Dock(child.ShipId,1);
        root.Inventory.AddItem("water_bottle",2); child.Inventory.AddItem("ration_pack",3); nested.Inventory.AddItem("reactor_console",1);
        double expected=24000+100+root.Inventory.GetTotalWeight()+child.Inventory.GetTotalWeight()+nested.Inventory.GetTotalWeight();
        var report=AssemblyMobility.Evaluate(root,100);
        Record("nested_stowed_payload", "Valid nested carried hulls and cargo counted once; excluded carried engines not confused with excluded mass", new GdDict { { "expected_mass_from_actual_itemdefs",expected }, { "report",report }, { "nested_carried_component_weight",nested.Inventory.GetTotalWeight() } }, "Provisioned valid parent graph and catalog items; unknown component item weight tested separately. No actual Hangar UI/detach occupancy/reservation proof.");
    }
    static void WorkHoldRestore()
    {
        var deps=SessionHarness.GoldenDeps(out var rig); SessionHarness.OverlayGamePlayability(deps);
        var session=RunSession.Create(deps);
        var work=new WorkActionState(); work.ConfigureAction("cut",new GdDict { { "duration",100.0 }, { "verb","cut" } });
        work.Status=WorkActionState.STATUS_ACTIVE; work.Progress=5; work.TargetId="diagnostic-uninstantiated-wall";
        session.WorkActionDriver.Work=work;
        var requires=typeof(RunSession).GetField("_workRequiresHold",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        requires.SetValue(session,true); session.EndWorkHold();
        TickOrder.Get(TickOrder.WorkAction).Run(session,SessionLocation.Home,1);
        double paused=work.Progress;
        var snapshot=RunSnapshotAssembler.Build(session); if(snapshot==null) throw new Exception("snapshot unexpectedly unavailable");
        var restoredDeps=SessionHarness.GoldenDeps(out var restoredRig); SessionHarness.OverlayGamePlayability(restoredDeps);
        var restored=RunSession.Create(restoredDeps); bool applied=restored.ApplyManualSlot(snapshot);
        bool requiresAfter=(bool)requires.GetValue(restored); double before=restored.WorkActionDriver.Work.Progress;
        restored.EndWorkHold(); TickOrder.Get(TickOrder.WorkAction).Run(restored,SessionLocation.Home,1);
        Record("session_work_hold_restore", "Released held work must remain paused after save/load unless user explicitly selects tap mode",new GdDict { { "snapshot_applied",applied }, { "before_save_paused_progress",paused }, { "restored_progress_before_tick",before }, { "restored_requires_hold",requiresAfter }, { "hold_setting_enabled",restored.HoldToWorkEnabled }, { "held_input",restored.IsWorkInteractHeld }, { "restored_progress_after_unheld_tick",restored.WorkActionDriver.Work.Progress }, { "snapshot_work",snapshot.WorkActionSummary } },"Current RunSession snapshot/manual-apply and stage code with golden data, live playability knobs, fake scene/MemoryStorage. Active job and private input requirement provisioned to isolate serialization; no real wall/tool acquisition or UI input asserted.");
        session.Dispose();restored.Dispose();
    }
    static void SessionTravelCost()
    {
        var deps=SessionHarness.GoldenDeps(out var rig); SessionHarness.OverlayGamePlayability(deps); var session=RunSession.Create(deps);
        session.ForceRepairAll(); session.ThreatManager.Threats.Clear();
        GdDict before=Round(session.InventoryState.GetSummary()); double time=session.WorldTime;
        GdDict result=new GdDict(); foreach(string marker in session.ScannableMarkerIds()) { result=session.TravelToMarkerId(marker); if(result.GetBool("success")) break; }
        Record("session_actual_travel_cost", "Record actual travel mutation rather than assuming displayed SeaGraph cost helpers are called",new GdDict { { "result",result }, { "inventory_before",before }, { "inventory_after",session.InventoryState.GetSummary() }, { "world_time_before",time }, { "world_time_after",session.WorldTime }, { "away_after",session.AwayFromStart } },"Actual RunSession travel with fake generated scene and provisioned repaired systems/threat removal; golden fixture bypasses natural onboarding. This proves only this callable travel transaction, not earned normal access or Unity physical traversal.");
        session.Dispose();
    }
    static void DiskReplacementBoundary(string output)
    {
        var directory = Path.Combine(output, "disposable-profile"); Directory.CreateDirectory(directory);
        var storage = new FileSystemStorage(directory); storage.WriteText("user://audit.json", "old-good");
        string file = storage.Globalize("user://audit.json"), temporary = file+".tmp";
        // Hold a read-only-share handle on the prepared temp file: writing/deleting it must fail before destination deletion.
        File.WriteAllText(temporary, "blocked-temp"); string error="";
        using(var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
        { try { storage.WriteText("user://audit.json","new-good"); } catch(Exception ex) { error=ex.GetType().Name; } }
        string afterFailure = storage.ReadText("user://audit.json");
        storage.WriteText("user://audit.json","new-good");
        Record("disk_prewrite_failure", "Prewrite exception should retain last good destination; final delete/move interruption remains separate", new GdDict { { "exception", error }, { "destination_after_failed_prewrite", afterFailure }, { "destination_after_success", storage.ReadText("user://audit.json") }, { "tmp_exists_after_success", File.Exists(temporary) } }, "Real FileSystemStorage on disposable audit path. Fault occurs BEFORE destination deletion; not proof of recovery during final replace interval and not MemoryStorage.");
    }
    static void WorkLocalityAndBoundary()
    {
        var deps=SessionHarness.GoldenDeps(out var rig); SessionHarness.OverlayGamePlayability(deps);
        var session=RunSession.Create(deps);
        // Provision one real catalog tool and a minimal structural target; exercise the actual interact/work stage.
        session.InventoryState.AddItem("welding_lance",1);
        session.CurrentShip.BuiltLayout=new GdDict { {"rooms",GdArray.Of(new GdDict {{"id","audit-room"},{"structural_placements",GdArray.Of(new GdDict {{"module_id","wall_straight_1x1"},{"name","interior-wall"},{"position",new Vec3(0,0,0)}})}})} };
        session.ModuleIntegrityMap=new ModuleIntegrityMap();rig.Scene.PlayerPosition=Vec3.Zero;
        bool started=session.TryWorkActionInteract(Vec3.Zero);string target=session.WorkActionDriver.Work?.TargetId??"";
        session.BeginWorkHold();rig.Scene.PlayerPosition=new Vec3(1000,0,1000);
        double progressBefore=session.WorkActionDriver.Work?.Progress??-1;
        for(int i=0;i<60;i++) session.StageWorkAction(0.1);
        Record("session_generic_cut_work_locality","A legitimately started work channel should validate its original site while completing",new GdDict {{"started",started},{"target",target},{"distance_after_start",rig.Scene.PlayerPosition.DistanceTo(Vec3.Zero)},{"progress_before",progressBefore},{"work_after",session.WorkActionDriver.Work?.GetSummary()??new GdDict()},{"module_after",session.ModuleIntegrityMap.GetModule(target)?.GetSummary()??new GdDict()},{"derived_breaches",session.GetDerivedBreachCount()},{"inventory",session.InventoryState.GetSummary()}},"Actual current session interact handler and timed stage; catalog tool and simple target provisioned; movement is fake scene position, no physical/input or earned acquisition assertion.");
        var boundary=new GdArray();
        foreach(string side in new[]{"internal","exterior"}) {
            var map=new ModuleIntegrityMap();var m=map.EnsureModule(side+"/wall","wall_straight_1x1",new GdDict(),side);m.State=ModuleIntegrityState.STATE_BREACHED;m.Integrity=0.2;
            boundary.Append(new GdDict {{"declared_boundary",side},{"wall_breach_count",map.CountWallBreaches()},{"summary",m.GetSummary()}});
        }
        Record("module_boundary_representation","Determine whether internal and exterior identity affects atmospheric breach classification",new GdDict {{"rows",boundary}},"Two declared model labels; no geometric ownership field is accepted or persisted by these APIs. Confirms classification contract, not a particular generated internal edge cut.");session.Dispose();
    }
    static void ShelterAndScale()
    {
        var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);var s=RunSession.Create(deps);
        int homeCraft=s.CraftingStations.Count,homeProduction=s.ProductionStations.Count;
        var other=ShipInstance.Create("audit-owned-shelter","audit",null,s.HomeShip.SystemsManager,s.HomeShip.SceneRoot);other.BuiltLayout=s.HomeShip.BuiltLayout.DeepCopy();other.Access=ShipAccessState.Create();other.Access.Claim(s.HomeShip.GetAccess().OwnerId);
        var activate=typeof(RunSession).GetMethod("ActivateBoardedContext",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        activate.Invoke(s,new object[]{other});int otherCraft=s.CraftingStations.Count,otherProduction=s.ProductionStations.Count;bool away=s.AwayFromStart;
        activate.Invoke(s,new object[]{s.HomeShip});
        Record("session_secondary_shelter_services","Boarded non-original shelter should be distinguished from original onboarding identity",new GdDict {{"home_craft",homeCraft},{"home_production",homeProduction},{"home_owner",s.HomeShip.GetAccess().OwnerId},{"other_owner",other.Access.OwnerId},{"other_craft",otherCraft},{"other_production",otherProduction},{"other_away",away},{"return_craft",s.CraftingStations.Count},{"return_production",s.ProductionStations.Count}},"Provisioned second ShipInstance claimed by same player, sharing the home fake loader/systems; invokes actual context rebuild; does not claim an earned secured connection or powered station UI test.");
        var samples=new GdArray();foreach(int count in new[]{0,10,100,1000}) {
            s.VisitedShips.Clear();for(int i=0;i<count;i++)s.VisitedShips["audit:"+i]=ShipInstance.Create("audit:"+i,"audit:"+i,null,null,null);
            var times=new List<double>();for(int repeat=0;repeat<30;repeat++){var watch=System.Diagnostics.Stopwatch.StartNew();s.StagePresentShips(0.001);watch.Stop();times.Add(watch.Elapsed.TotalMilliseconds);}times.Sort();
            var snapshotWatch=System.Diagnostics.Stopwatch.StartNew();var snap=RunSnapshotAssembler.Build(s);var json=GdJson.Stringify(snap.ToDict());snapshotWatch.Stop();
            samples.Append(new GdDict{{"uninstantiated_visited_ships",count},{"present_tick_median_ms",times[times.Count/2]},{"snapshot_encode_ms",snapshotWatch.Elapsed.TotalMilliseconds},{"snapshot_chars",json.Length}});
        }
        Record("known_ship_tick_save_scale","Measure enumeration and save growth separately from rendered roots",new GdDict{{"samples",samples}},"Current session stage and snapshot encoding with minimal uninstantiated visited models; 30 tick samples/count, warm JIT; not loaded hull/AI/GPU or full historic-save performance certification.");s.Dispose();
    }
    static void Main(string[] args)
    {
        string root = args[0], output = args[1]; Directory.CreateDirectory(output);
        CoreServices.Resources = new FileSystemResourceReader(Path.Combine(root,"SynapticSea","Assets","StreamingAssets"));
        ComponentIdentity(); ElapsedTime(); ProductionOutage(); CraftPowerGuard(); NestedPayload(); AtmosphereScalar(); WorkHoldRestore(); SessionTravelCost(); DiskReplacementBoundary(output); WorkLocalityAndBoundary(); ShelterAndScale(); StagedSaveRecovery(output); LaunchAndNormalSupplyCommands(); CompiledBoundaryCuts();
        var result = new GdDict { { "schema", "deep-audit-model-results-v1" }, { "results", new GdArray(rows) } };
        File.WriteAllText(Path.Combine(output,"model-results.json"), GdJson.Stringify(result));
        Console.WriteLine("Audit model experiments recorded: "+rows.Count);
        foreach (var row in rows) Console.WriteLine(((GdDict)row).GetString("id"));
    }
    static void StagedSaveRecovery(string output)
    {
        var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);var s=RunSession.Create(deps);
        var storage=new FileSystemStorage(Path.Combine(output,"staged-save-profile"));var service=new SaveLoadService(storage);var snapshot=RunSnapshotAssembler.Build(s);bool wrote=service.SaveCurrentRun(snapshot);
        string full=storage.Globalize(SaveLoadService.SAVE_PATH);string temporary=full+".tmp";File.Move(full,temporary,true);
        bool stagedLoad=service.LoadCurrentRun()!=null;bool temporaryStill=File.Exists(temporary);File.Move(temporary,full,true);bool controlLoad=service.LoadCurrentRun()!=null;
        Record("staged_delete_move_save_recovery","A valid temporary save with missing destination should have an explicit recovery policy",new GdDict{{"initial_write_ok",wrote},{"staged_missing_destination_load",stagedLoad},{"temporary_retained",temporaryStill},{"restored_destination_control_load",controlLoad}},"Actual FileSystemStorage/SaveLoadService with current valid snapshot; staged reachable delete-to-move state, not observed crash or killed user process; no Title/native Continue proof.");s.Dispose();
    }
    static void LaunchAndNormalSupplyCommands()
    {
        var launchRows=new GdArray();foreach(var settings in new[]{(17L,"breach_field","standard"),(18L,"breach_field","standard"),(17L,"abyssal_synaptic_sea","standard"),(17L,"breach_field","hardened")}){
            bool accepted=SynapticSea.Core.Procgen.MilestoneALaunch.TryAccept(settings.Item1,settings.Item2,settings.Item3,out string reason);launchRows.Append(new GdDict{{"seed",settings.Item1},{"biome",settings.Item2},{"difficulty",settings.Item3},{"accepted",accepted},{"reason",reason}});
        }
        Record("real_launch_validation","Default versus alternatives offered by UI must use authoritative launch predicate",new GdDict{{"rows",launchRows}},"Actual gate called directly, no SceneLoader stub; no boot/Title transition test.");
        var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);var s=RunSession.Create(deps);var before=Round(s.InventoryState.GetSummary());string cacheHandler="not_found";
        foreach(var loot in s.LootContainers) if(loot.NodeName.Contains("start_supply")){rig.Scene.PlayerPosition=loot.GlobalPosition;cacheHandler=s.RequestInteract();break;}
        var after=Round(s.InventoryState.GetSummary());string componentHandler="no_marker";bool began=false;string markerId="";
        foreach(GdDict marker in s.ComponentMarkers){if(marker.Get("world_position",null) is Vec3 pos){rig.Scene.PlayerPosition=pos;markerId=marker.GetString("component_instance_id");componentHandler=s.RequestInteract();began=s.WorkActionDriver.IsWorking();break;}}
        Record("normal_supply_component_command_boundary","Empty starting bag acquires live authored cache through dispatch, then attempts first component through same command",new GdDict{{"inventory_before",before},{"cache_handler",cacheHandler},{"inventory_after_cache",after},{"component_marker",markerId},{"component_handler",componentHandler},{"began_work",began},{"has_wrench",s.InventoryState.GetSummary().GetDictOrEmpty("items").Get("wrench",0L)},{"has_tool_wrench",s.InventoryState.GetSummary().GetDictOrEmpty("items").Get("tool_wrench",0L)}},"No inventory/tool/skill grants; default current session and real authored cache/dispatch, but fake scene directly positions player at anchors. This is command/acquisition boundary evidence, NOT real walking/Unity/native input journey.");s.Dispose();
    }
    static void CompiledBoundaryCuts()
    {
        var gen=new SynapticSea.Core.Procgen.ShipGenerator();gen.ConfigureRunContext("breach_field","standard");
        var docs=gen.Generate(new SynapticSea.Core.Procgen.ShipBlueprint(1,0,17){GenerationProfile="reclamation_expedition_v4"});var plan=docs.Layout.GetDictOrEmpty("structural_plan");var edges=plan.GetDictOrEmpty("edges");var outcomes=new GdArray();
        foreach(bool exterior in new[]{false,true}){
            string selected="";GdDict edge=null;foreach(var pair in edges){if(pair.Value is GdDict e && e.GetString("kind")=="SOLID" && e.GetBool("exterior")==exterior && e.GetString("module_id").Contains("wall")){selected=V.Str(pair.Key);edge=e;break;}}
            if(edge==null)throw new Exception("missing representative boundary");var map=new ModuleIntegrityMap();ModuleIntegrityConsequences.SeedMapFromCompiledLayout(map,docs.Layout,false);long before=map.CountWallBreaches();
            var driver=new WorkActionDriver();driver.Configure(new GdDict());bool began=driver.StartAction("cut_wall","edge/"+selected,new GdDict{{"tool_class","welding_lance"},{"skill_id","salvage"},{"skill_level",0L},{"inventory",new GdDict{{"welding_lance",1L}}}});driver.Tick(6,new GdDict{{"work_speed_mult",1.0}});var resolved=driver.Complete(map,new GdDict{{"welding_lance",1L}});
            var restored=new ModuleIntegrityMap();restored.ApplySummary(Round(map.GetSummary()));outcomes.Append(new GdDict{{"exterior",exterior},{"edge",edge},{"target","edge/"+selected},{"began",began},{"resolve",resolved},{"breaches_before",before},{"breaches_after",map.CountWallBreaches()},{"breaches_restored",restored.CountWallBreaches()},{"nav_gap_rooms",GdString.ToGdArray(map.RoomsWithNavGaps())}});
        }
        Record("compiled_internal_exterior_cuts","Compare authoritative mutation of real internal and exterior boundaries in the SAME compiled layout",new GdDict{{"seed",17L},{"profile","reclamation_expedition_v4"},{"outcomes",outcomes}},"Actual generated compiled edges, catalog timed work driver and map JSON restore. Tool provisioned, no player reach/hold, physical collider/nav rebuild or room-pressure simulation proof.");
    }
}
