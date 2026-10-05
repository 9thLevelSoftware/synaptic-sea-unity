using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.PlayMode
{
    public partial class RunLifecyclePlayModeTests
    {
        readonly List<double> _auxWorkTickMilliseconds=new List<double>();
        readonly List<double> _auxWorkingFrameMilliseconds=new List<double>();
        bool _auxMeasuring, _auxTickWasRunning;
        long _auxStageStart;
        static string AuxiliaryLatencySummary(List<double> values)
        {
            if(values.Count==0)return "samples=0";
            var sorted=values.OrderBy(v=>v).ToArray();
            return "samples="+sorted.Length+" median_ms="+sorted[sorted.Length/2].ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +" p95_ms="+sorted[(int)System.Math.Floor((sorted.Length-1)*.95)].ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +" worst_ms="+sorted[sorted.Length-1].ToString("F3",System.Globalization.CultureInfo.InvariantCulture);
        }

        // Functional completion budget: measured earned prefix ~152s plus two racks, rest, repairs,
        // crafting, walking and save/travel checks (~300s estimated); 2x bounded headroom.
        // A functional pass does not establish acceptable work-tick or frame performance.
        [UnityTest, Timeout(600000)] public IEnumerator CookPhysicallyEarnsAuxiliaryUtilitiesAndFiniteRacks() => AuxiliaryHomeRoute("cook");
        [UnityTest] public IEnumerator MedicPhysicallyEarnsAuxiliaryUtilitiesAndFiniteRacks() => AuxiliaryHomeRoute("medic");

        IEnumerator AuxiliaryDeck(int deck)
        {
            if ((_boot.Host.SceneState.Player.GodotPosition.Y > 3 ? 1 : 0) == deck) yield break;
            _s.RefreshDeckTransitions();
            yield return WalkTo(_s.DeckTransitions.First(d => d.DestinationDeck == deck), 2.4f);
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            Assert.AreEqual(deck, _boot.Host.SceneState.Player.GodotPosition.Y > 3 ? 1 : 0);
        }

        IEnumerator AuxiliaryManualStow(params string[] items)
        {
            var hold = _s.CargoHoldControls.First(c => c.IsValid && c.CarrierId == _s.HomeShip.ShipId);
            yield return AuxiliaryDeck(hold.GlobalPosition.Y > 3 ? 1 : 0);
            yield return WalkTo(hold, .5f);
            for (int n=0;n<6&&!_boot.Ui.Inventory.IsOpen();n++)
            { _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8); }
            Assert.IsTrue(_boot.Ui.Inventory.IsOpen(), "physical manual cargo handle");
            foreach (string item in items)
            {
                long amount = _s.InventoryState.GetQuantity(item);
                if (amount > 0) Assert.AreEqual(amount, _boot.Ui.Inventory.TransferQuantity(InventoryPanel.PaneSelf, item, amount), "manual cargo remains usable: " + item);
            }
            _boot.Ui.Inventory.Close(); yield return FixedSteps(8);
            Assert.LessOrEqual(_s.InventoryState.GetLoadRatio(), 1, "stow haul before industrial work");
        }

        IEnumerator AuxiliaryWalkToFixture(AuxiliaryServicePoint point)
        {
            // The prop is 1.08m above the authored floor. Walk to standing ground beneath it,
            // then require actual 3D strict range and production LOS; never position the player.
            Vec3 target=point.GlobalPosition;
            yield return WalkTo(new Vec3(target.X,_boot.Host.SceneState.Player.GodotPosition.Y,target.Z),1.1f);
        }

        IEnumerator AuxiliaryPhysicalWork(string id, bool interrupted = false)
        {
            var point = _s.AuxiliaryServicePoints.Single(p => p.ServiceId == id);
            yield return AuxiliaryDeck(point.GlobalPosition.Y > 3 ? 1 : 0);
            yield return AuxiliaryWalkToFixture(point);
            yield return FixedSteps(8); // Let normal movement settle before held industrial work.
            Assert.IsTrue(point.IsPlayerInDirectRangeStrict(_boot.Host.SceneState.Player.GodotPosition), "strict physical range: " + id);
            Debug.Log("[AuxiliaryAnchor] id="+id+" target="+point.GlobalPosition+" standing="+_boot.Host.SceneState.Player.GodotPosition);
            _auxMeasuring=true;
            _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            Assert.AreEqual(id, _s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetString("service_id"), "normal focus selects authored fixture");
            Assert.IsTrue(_s.AuxiliaryWorkRunning, GdJson.Stringify(_s.GetAuxiliaryServiceState()));
            if (interrupted)
            {
                float until = Time.realtimeSinceStartup + 8;
                while (_s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds") < 1 && Time.realtimeSinceStartup < until) yield return null;
                _boot.Host.SceneState.Player.SetScriptedMoveDirection(new Vec3(.3,0,0)); yield return FixedSteps(12);
                _boot.Host.SceneState.Player.ClearScriptedMoveDirection();
                Assert.IsFalse(_s.AuxiliaryWorkRunning, "movement pauses while interact remains held");
                _s.EndWorkHold();
                Assert.AreEqual(4,_s.InventoryState.GetQuantity("scrap_metal"),"partial work has not paid raw material");
                Assert.AreEqual(4,_s.InventoryState.GetQuantity("wiring_bundle"));
                double partial = _s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds");
                Assert.IsTrue(_s.RequestSave(), GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
                Assert.IsFalse(_s.AuxiliaryWorkRunning, "Continue restores zero held consent");
                Assert.AreEqual(partial, _s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
                point = _s.AuxiliaryServicePoints.Single(p=>p.ServiceId==id); yield return AuxiliaryWalkToFixture(point);
                yield return FixedSteps(8);
                var gateMethod=typeof(RunSession).GetMethod("AuxiliaryGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var actual=_boot.Host.SceneState.Player.GodotPosition;
                Vec3 focusHit=Vec3.Zero,gateHit=Vec3.Zero;
                bool focusBlocked=_s.Deps.LosProbe?.HasSpace==true&&_s.Deps.LosProbe.IntersectRay(actual+new Vec3(0,1,0),point.GlobalPosition+new Vec3(0,1,0),out focusHit);
                bool gateBlocked=_s.Deps.LosProbe?.HasSpace==true&&_s.Deps.LosProbe.IntersectRay(actual+new Vec3(0,.8,0),point.GlobalPosition,out gateHit);
                Debug.Log("[AuxiliaryPreResume] gate="+gateMethod.Invoke(_s,new object[]{id,false,false})+" focus="+_s.CanFocusInteractable(point)+" valid="+point.IsValid+" tree="+point.IsInsideTree+" target="+point.GlobalPosition+" local="+point.LocalPosition+" owner="+point.OwnerId+" home="+_s.HomeShip.ShipId+" root_valid="+point.Parent.IsValid+" root_tree="+point.Parent.IsInsideTree+" root_transform="+point.Parent.GlobalTransform+" range="+point.IsPlayerInDirectRangeStrict(actual)+" actionable="+point.Actionable?.Invoke(id)+" crowbar="+_s.InventoryState.GetQuantity("crowbar")+" scrap="+_s.InventoryState.GetQuantity("scrap_metal")+" wire="+_s.InventoryState.GetQuantity("wiring_bundle")+" generic_work="+_s.WorkActionDriver.IsWorking()+" focus_blocked="+focusBlocked+" focus_hit="+focusHit+" gate_blocked="+gateBlocked+" gate_hit="+gateHit);
                _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
                Debug.Log("[AuxiliaryResume] handler="+_s.LastInteractHandlerId+" moving="+_boot.Host.SceneState.Player.IsMoving()+" held="+_s.IsWorkInteractHeld+" stamina="+_s.VitalsState.Stamina+" player="+_boot.Host.SceneState.Player.GodotPosition+" state="+GdJson.Stringify(_s.GetAuxiliaryServiceState().GetDictOrEmpty("job")));
            }
            float deadline = Time.realtimeSinceStartup + 100;
            while (_s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetString("completion_commit_id").Length == 0 && !_s.SliceComplete && Time.realtimeSinceStartup < deadline)
            {
                if (_s.VitalsState.Stamina < 25 && _s.AuxiliaryWorkRunning)
                { _s.EndWorkHold(); _s.PauseAuxiliaryService("rest"); }
                if (!_s.AuxiliaryWorkRunning && _s.VitalsState.Stamina >= 75)
                { _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); }
                float frameStart=Time.realtimeSinceStartup;
                yield return null;
                _auxWorkingFrameMilliseconds.Add((Time.realtimeSinceStartup-frameStart)*1000.0);
            }
            _s.EndWorkHold(); _auxMeasuring=false;
            Debug.Log("[AuxiliaryWorkGate] id="+id+" handler="+_s.LastInteractHandlerId+" moving="+_boot.Host.SceneState.Player.IsMoving()+" stamina="+_s.VitalsState.Stamina+" work_tick "+AuxiliaryLatencySummary(_auxWorkTickMilliseconds)+" work_rest_frame "+AuxiliaryLatencySummary(_auxWorkingFrameMilliseconds)+" job="+GdJson.Stringify(_s.GetAuxiliaryServiceState().GetDictOrEmpty("job")));
            Assert.IsFalse(_s.SliceComplete, "survive eligible work and stationary rest");
            Assert.IsNotEmpty(_s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetString("completion_commit_id"), GdJson.Stringify(_s.GetAuxiliaryServiceState()));
        }

        IEnumerator AuxiliaryHomeRoute(string classId)
        {
            var routeWall=System.Diagnostics.Stopwatch.StartNew();
            _auxWorkTickMilliseconds.Clear();_auxWorkingFrameMilliseconds.Clear();_auxMeasuring=false;
            _recordJourneyTelemetry = true;
            yield return BootPlayable(new RunLaunchRequest { ClassId=classId, EnableAuxiliaryServices=true,
                LayoutOverridePath="res://data/diagnostics/earned-services-home-v1/layout.json" });
            _s.StageRan+=(id,location)=>
            {
                if(!_auxMeasuring)return;
                if(id==TickOrder.ElectricalArc)
                {
                    _auxTickWasRunning=_s.AuxiliaryWorkRunning;
                    _auxStageStart=System.Diagnostics.Stopwatch.GetTimestamp();
                }
                else if(id==TickOrder.WorkAction&&_auxTickWasRunning)
                {
                    _auxWorkTickMilliseconds.Add((System.Diagnostics.Stopwatch.GetTimestamp()-_auxStageStart)*1000.0/System.Diagnostics.Stopwatch.Frequency);
                }
            };
            Assert.AreEqual(6,_s.AuxiliaryServicePoints.Count);
            yield return AuxiliaryDeck(1);
            var supply = _s.LootContainers.Single(l=>l.ContainerId=="start_supply_a");
            yield return WalkTo(supply); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.IsTrue(supply.Searched);
            _boot.Ui.Inventory.OpenSelf(_s.InventoryState,_s.EquipmentState);
            Assert.IsTrue(_boot.Ui.Inventory.UnequipSlot("primary_hand"),"normal inventory returns the auto-equipped crowbar to carried tools");
            _boot.Ui.Inventory.Close(); yield return FixedSteps(2);
            yield return AuxiliaryManualStow("reactor_core","sensor_module","power_cell","fire_extinguisher");
            yield return AuxiliaryDeck(1);
            var kit = _s.LootContainers.Single(l=>l.ContainerId=="home_service_kit_01");
            yield return WalkTo(kit,1.1f); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.IsTrue(kit.Searched);
            StudyThroughInventory();
            float studyEnd=Time.realtimeSinceStartup+50;
            while(!_s.PlayerProgression.HasReadBook("fabrication_schematic_basic")&&!_s.SliceComplete&&Time.realtimeSinceStartup<studyEnd)yield return null;
            Assert.IsTrue(_s.PlayerProgression.HasReadBook("fabrication_schematic_basic"));
            for(int n=0;n<16&&!_s.HomeObjectivesComplete;n++)
            {
                var objective=_s.Interactables.First(o=>o.Active&&!o.Completed);
                yield return AuxiliaryDeck(objective.GlobalPosition.Y>3?1:0); yield return WalkTo(objective);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            }
            Assert.IsTrue(_s.HomeObjectivesComplete);
            Assert.IsFalse(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId,"maintenance_fabricator_feed_01"), "objective ForceRepair does not complete utility work");
            foreach(string id in new[]{"maintenance_fabricator_feed_01","maintenance_cargo_relay_01","medbay_task_light_01","airlock_dock_beacon_01"})
                yield return AuxiliaryPhysicalWork(id,id=="maintenance_fabricator_feed_01");
            Assert.AreEqual(0,_s.InventoryState.GetQuantity("scrap_metal")); Assert.AreEqual(0,_s.InventoryState.GetQuantity("wiring_bundle"));
            yield return CaptureHud(classId+"-auxiliary-utilities-earned.png");
            foreach(string id in new[]{"home_spare_harness_rack_01","home_spare_harness_rack_02"})
            {
                yield return AuxiliaryManualStow("scrap_metal","wiring_bundle");
                long before=_s.InventoryState.GetQuantity("scrap_metal");
                yield return AuxiliaryPhysicalWork(id);
                Assert.AreEqual(before,_s.InventoryState.GetQuantity("scrap_metal"), "recovery releases into finite rack");
                Assert.AreEqual(4,_s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
                Assert.AreEqual(before+4,_s.InventoryState.GetQuantity("scrap_metal")); Assert.IsTrue(_s.GetAuxRackRemaining(id).IsEmpty || _s.GetAuxRackRemaining(id).GetInt("scrap_metal")==0);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.AreEqual(before+4,_s.InventoryState.GetQuantity("scrap_metal"));
                Assert.LessOrEqual(_s.InventoryState.GetLoadRatio(),1);
            }
            Assert.IsTrue(_s.RequestSave(),GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
            Assert.IsTrue(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId,"maintenance_cargo_relay_01"));
            foreach(string id in new[]{"home_spare_harness_rack_01","home_spare_harness_rack_02"})Assert.AreEqual(0,_s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
            yield return CaptureHud(classId+"-finite-racks-restored.png");
            // Existing ordinary first-away content contract remains unchanged; no frozen-source gate bypass.
            var seal=_s.BreachSealPoints.Single(p=>p.CompartmentId=="cargo"&&!p.Sealed);
            yield return AuxiliaryDeck(seal.GlobalPosition.Y>3?1:0); yield return WalkAndFinishChannel(seal.GlobalPosition);
            foreach(string subId in new[]{"star_charts","nav_linkage"})
            {
                var repair=_s.RepairPoints.Single(p=>p.IsValid&&!p.Repaired&&p.SubcomponentId==subId);
                Assert.LessOrEqual(repair.MinSkill,_s.PlayerProgression.GetSkillLevel("repair"),"earned machinery skill gate");
                yield return AuxiliaryDeck(repair.GlobalPosition.Y>3?1:0); yield return WalkAndFinishChannel(repair.GlobalPosition);
            }
            Assert.GreaterOrEqual(_s.PlayerProgression.GetSkillLevel("repair"),2);
            Assert.IsTrue(_s.ShipSystemsManager.IsOperational("navigation")); Assert.IsTrue(_s.ShipSystemsManager.IsOperational("propulsion"));
            // Paid workbench lockpick remains useful preparation; required Rust door awaits authentic hazard exports.
            var bench=_s.CraftingStations.Single(c=>c.StationKind=="workbench");
            yield return AuxiliaryDeck(bench.GlobalPosition.Y>3?1:0); yield return WalkTo(bench,1.1f);
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.IsTrue(_boot.Ui.RecipePicker.IsOpen());
            for(int n=0;n<100&&_boot.Ui.RecipePicker.GetSelectedId()!="craft_lockpick_set";n++)_boot.Ui.RecipePicker.MoveSelection(1);
            var paid=_boot.Ui.RecipePicker.ConfirmSelection(); Assert.IsTrue(paid.GetBool("ok"),GdJson.Stringify(paid));
            float craftEnd=Time.realtimeSinceStartup+45;
            while(_s.InventoryState.GetQuantity("lockpick_set")==0&&!_s.SliceComplete&&Time.realtimeSinceStartup<craftEnd)yield return null;
            Assert.AreEqual(1,_s.InventoryState.GetQuantity("lockpick_set"));
            yield return AuxiliaryManualStow("scrap_metal","wiring_bundle","circuit_board","hull_sealant","wrench","fabrication_schematic_basic");
            yield return CutBiomatterMooring(_s.HomeShip); yield return CutBiomatterMooring(_s.LifeboatShip);
            float propelEnd=Time.realtimeSinceStartup+30;
            while(!_s.PropulsionExpandedState.CanPropel()&&!_s.SliceComplete&&Time.realtimeSinceStartup<propelEnd)yield return null;
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel(),GdJson.Stringify(_s.TravelCapability()));
            var bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.PilotedShip.ShipId); yield return WalkTo(bridge,1.2f);
            _boot.Ui.Scanner.Open(); var contacts=_s.Scan().GetArrayOrEmpty("markers"); Assert.Greater(contacts.Count,0);
            GdDict travel=null;
            for(int n=0;n<contacts.Count;n++)
            { travel=_boot.Ui.Scanner.ConfirmSelection(); if(travel.GetBool("success"))break; _boot.Ui.Scanner.MoveSelection(1); }
            Assert.IsTrue(travel!=null&&travel.GetBool("success"),GdJson.Stringify(travel)); yield return FixedSteps(8);
            Assert.IsTrue(_s.AwayFromStart); Assert.IsFalse(_s.SliceComplete,"departure is not extraction");
            yield return CaptureHud(classId+"-earned-ordinary-departure.png");
            bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.PilotedShip.ShipId); yield return WalkTo(bridge,1.2f);
            Assert.IsTrue(_s.TravelHome()); yield return FixedSteps(8); Assert.IsFalse(_s.AwayFromStart);
            Assert.IsTrue(_s.RequestSave(),GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
            Assert.IsTrue(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId,"maintenance_fabricator_feed_01"));
            yield return CaptureHud(classId+"-earned-return-restored.png");
            Debug.Log("[AuxiliaryPhysicalPerformance] class="+classId+" completed_route_wall_seconds="+routeWall.Elapsed.TotalSeconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +" work_tick "+AuxiliaryLatencySummary(_auxWorkTickMilliseconds)+" work_rest_frame "+AuxiliaryLatencySummary(_auxWorkingFrameMilliseconds));
            Debug.Log("[AuxiliaryPhysical] class="+classId+" progression="+GdJson.Stringify(_s.PlayerProgression.GetSummary())+" state="+GdJson.Stringify(_s.GetAuxiliaryServiceState())+" vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" time="+_s.WorldTime);
        }
    }
}
