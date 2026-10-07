using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// A RunSession built from the golden coherent_ship_001 documents with no scene (fake ports, in-memory storage,
    /// manual clock). Mirrors the Godot main_playable_slice_{completion,ship_systems,route_control} smoke checks.
    /// </summary>
    public class HeadlessSessionTests
    {
        IEngineInfo _previousEngine;

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Engine = _previousEngine;
        }

        static void TickSeconds(SessionHarness.Rig rig, double seconds, double step = 0.25)
        {
            for (double t = 0; t < seconds - 1e-9; t += step)
            {
                rig.Clock.Advance(step);
                rig.Session.Tick(TickContext.Frame(step, rig.Scene.PlayerPosition));
            }
        }

        [TestCase(SynapticSea.Core.Procgen.ConstrainedExpedition.Profile, true)]
        [TestCase(SynapticSea.Core.Procgen.ConstrainedExpedition.LegacyProfile, false)]
        public void NormalTickRefreshesPhysicalFloorOwnerForEligibleProfilesAndPreservesLegacy(string profile, bool refresh)
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            try
            {
                var root = new FakeShipRoot { IsInsideTree = true };
                var bp = new SynapticSea.Core.Procgen.ShipBlueprint(0, 2, 42) { GenerationProfile = profile };
                var ship = ShipInstance.Create("ship_tick_owner", "tick_owner", bp, new ShipSystemsManager(), root);
                ship.BuiltLayout = new GdDict { { "structural_plan", new GdDict { { "floor_placements", GdArray.Of(
                    new GdDict { { "position", new Vec3(800, 0, 800) } }) } } } };
                s.VisitedShips["tick_owner"] = ship; s.CurrentShip = ship; s.AwayFromStart = true;
                s.CurrentOccupancy = s.LifeboatShip;
                // Feed successive player positions through the ordinary tick input; never call the resolver explicitly.
                for (int i = 0; i <= 4; i++)
                {
                    rig.Scene.PlayerPosition = new Vec3(800 + (4 - i), .5, 800);
                    s.Tick(TickContext.Frame(.02, rig.Scene.PlayerPosition));
                }
                Assert.AreSame(refresh ? ship : s.LifeboatShip, s.CurrentOccupancy);
                Assert.AreSame(ship, s.CurrentShip, "refresh does not force a different boarded context");
                Assert.AreEqual(profile, ship.Blueprint.GenerationProfile);
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void RepairedRecoveryHabitatAirRequiresPhysicalOwnershipLocalServicesSealingAndNoAuthoredVent()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ForceRepairAll();
            var bp=new SynapticSea.Core.Procgen.ShipBlueprint(1,0,17){GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.Profile};
            var ownSystems=new ShipSystemsManager();ownSystems.Configure(ownSystems.LoadDefinitions(),0,17);ownSystems.ApplySummary(s.ShipSystemsManager.GetSummary());
            var ship=ShipInstance.Create("recovered_test","test",bp,ownSystems,s.HomeShip.SceneRoot);ship.BuiltLayout=s.HomeShip.BuiltLayout;
            ship.GetHull().Configure(new GdDict{{"compartments",GdArray.Of(new GdDict{{"compartment_id","cargo"},{"health",1.0},{"breach_open",false}})}});
            var floor=AssemblyMobility.Floors(ship.BuiltLayout)[0];rig.Scene.PlayerPosition=floor+new Vec3(0,.55,0);
            s.CurrentShip=ship;s.CurrentOccupancy=ship;s.AwayFromStart=true;
            Assert.IsFalse(s.OwnedRecoveryHabitatAir(),"unclaimed wreck is not powered shelter");ship.GetAccess().Claim("player_local");
            Assert.IsTrue(s.OwnedRecoveryHabitatAir());s.OxygenState.Oxygen=40;s.StageOxygen(1);Assert.Greater(s.OxygenState.Oxygen,40);
            ship.SystemsManager.DamageSubcomponent("life_support","air_recycler",1);Assert.IsFalse(s.OwnedRecoveryHabitatAir());
            Assert.IsTrue(s.ShipSystemsManager.IsOperational("life_support"),"working home services do not supply the recovered hull");
            double offlineBefore=s.OxygenState.Oxygen;s.StageOxygen(1);Assert.Less(s.OxygenState.Oxygen,offlineBefore,"offline local life support cannot refill from another ship");
            ship.SystemsManager.ForceRepair("life_support","air_recycler");
            ship.GetHull().DamageCompartment("cargo",1,true);Assert.IsFalse(s.OwnedRecoveryHabitatAir());ship.GetHull().SealCompartment("cargo",1);
            var model=((FakeLoaderView)s.Loader).Model;model.AuthoredAtmosphereSpecs=GdArray.Of(new GdDict{{"position",floor},{"vented",true},{"oxygen_bp",0L}});
            Assert.IsFalse(s.OwnedRecoveryHabitatAir(),"explicit ventilation remains hazardous");model.AuthoredAtmosphereSpecs.Clear();
            var explicitAir=new GdDict{{"position",floor},{"depressurized",true},{"oxygen_bp",0L},{"oxygen_source","initial_hull_condition_v1"}};
            model.AuthoredAtmosphereSpecs.Add(explicitAir);double before=s.OxygenState.Oxygen;s.StageOxygen(1);
            Assert.IsFalse(s.OwnedRecoveryHabitatAir());Assert.Less(s.OxygenState.Oxygen,before);
            Assert.IsTrue(explicitAir.GetBool("depressurized"),"shelter refresh must not overwrite explicit room depressurization");
            model.AuthoredAtmosphereSpecs.Clear();
            rig.Scene.PlayerPosition=floor+new Vec3(400,.55,400);Assert.IsFalse(s.OwnedRecoveryHabitatAir(),"stale ownership outside the physical floor cannot refill a suit");
        }

        [Test]
        public void WreckFireDoesNotConsumeAirWhilePhysicallyShelteringInIndependentShuttle()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ForceRepairAll();
            s.LifeboatShip.SystemsManager.ApplySummary(s.ShipSystemsManager.GetSummary());
            var boatFloor=AssemblyMobility.Floors(s.LifeboatShip.BuiltLayout)[0];
            rig.Scene.PlayerPosition=s.LifeboatShip.SceneRoot.GlobalTransform*(boatFloor+new Vec3(0,.55,0));
            var bp=new SynapticSea.Core.Procgen.ShipBlueprint(1,0,17);
            var wreck=ShipInstance.Create("burning_wreck","test",bp,s.ShipSystemsManager,s.HomeShip.SceneRoot);
            wreck.BuiltLayout=s.HomeShip.BuiltLayout;
            wreck.GetFire().Ignite("engineering",1);
            s.CurrentShip=wreck;s.CurrentOccupancy=s.LifeboatShip;s.AwayFromStart=true;
            s.OxygenState.Oxygen=40;s.StageOxygen(1);
            Assert.Greater(s.OxygenState.Oxygen,40,"shuttle air is independent of the adjacent burning wreck");
            var wreckFloor=AssemblyMobility.Floors(wreck.BuiltLayout)[0];
            rig.Scene.PlayerPosition=wreck.SceneRoot.GlobalTransform*(wreckFloor+new Vec3(0,.55,0));
            s.CurrentOccupancy=wreck;s.StageOxygen(1);
            Assert.Less(s.OxygenState.Oxygen,40,"boarding the burning wreck retains its real oxygen hazard");
            Assert.Greater(wreck.GetFire().GetTotalIntensity(),0,"shelter does not extinguish another ship's fire");
        }

        [Test]
        public void HomeAssemblyDenialPreservesLocationAndReturnNeverSelfDocks()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ForceRepairAll();
            Assert.IsTrue(s.SetPilotedShip(s.HomeShip).GetBool("success"));
            var destination=s.SynapticSeaWorld.MarkersInRange(s.ScannerState.RangeRadius).First();
            Vec3 before=s.SynapticSeaWorld.PlayerPosition;
            var denied=s.TravelToMarkerId(destination.MarkerId);
            Assert.IsFalse(denied.GetBool("success"));
            Assert.AreEqual("insufficient_propulsion_capacity",denied.GetString("reason"));
            Assert.AreEqual(before,s.SynapticSeaWorld.PlayerPosition);
            Assert.AreEqual(Vec3.Zero,s.HomeSeaPosition);Assert.AreEqual("",s.HomeSeaMarkerId);
            Assert.IsFalse(s.TravelHome());Assert.IsNull(s.HomeShip.ParentShip);
            Assert.AreSame(s.HomeShip,s.LifeboatShip.ParentShip);
        }

        [Test]
        public void InstalledHomeTransitFixturePreservesGraphAndRejectsOutOfRangeContact()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ThreatManager.Threats.Clear();
            for(long sequence=1;sequence<=4;sequence++)Assert.IsTrue(s.CompleteObjectiveSequence(sequence));
            s.ForceRepairAll();s.HullWebState.CutFree();s.HomeShip.GetWeb().CutFree();s.LifeboatShip.GetWeb().CutFree();
            s.HomeShip.GetHull().Configure(new GdDict{{"compartments",GdArray.Of(new GdDict{{"compartment_id","fixture"},{"health",1.0},{"breach_open",false}})}});
            s.HomeShip.Mobility=AssemblyMobility.CreateSpecification(s.HomeShip,true);
            s.PropulsionExpandedState.Configure(new GdDict{{"thrust_percent",100.0},{"operational",true}});
            var history=ShipInstance.Create("fixture_history","fixture_history",new SynapticSea.Core.Procgen.ShipBlueprint(1,0,17),new ShipSystemsManager(),null);
            s.VisitedShips[history.MarkerId]=history;
            Assert.IsTrue(s.SetPilotedShip(s.HomeShip).GetBool("success"));
            var destination=s.SynapticSeaWorld.MarkersInRange(s.ScannerState.RangeRadius).First();
            var boatParent=s.LifeboatShip.ParentShip;var pose=s.HomeShip.SceneRoot.GlobalTransform;
            var moved=s.TravelToMarkerId(destination.MarkerId);
            Assert.IsTrue(moved.GetBool("success"),GdJson.Stringify(moved));Assert.AreEqual("home_assembly_transit",moved.GetString("mode"));
            Assert.AreEqual(destination.Position,s.HomeSeaPosition);Assert.AreEqual(destination.Position,s.SynapticSeaWorld.PlayerPosition);
            Assert.AreSame(boatParent,s.LifeboatShip.ParentShip);Assert.AreEqual(pose,s.HomeShip.SceneRoot.GlobalTransform);Assert.IsNull(s.HomeShip.ParentShip);
            var distant=new ShipMarker{MarkerId="unreachable-fixture",Position=new Vec3(100000,0,100000)};
            var denied=s.TravelTo(distant);Assert.IsFalse(denied.GetBool("success"));Assert.AreEqual("out_of_range",denied.GetString("reason"));
            Assert.AreEqual(destination.Position,s.HomeSeaPosition);
        }

        [Test]
        public void SecuredMemberBridgeControlsHomeButMooredShuttleRemainsIndependent()
        {
            var s=SessionHarness.CreateGolden().Session;
            var child=ShipInstance.Create("fixture_extension","fixture_extension",new SynapticSea.Core.Procgen.ShipBlueprint(1,0,17),new ShipSystemsManager(),null);
            child.ParentShip=s.HomeShip;s.HomeShip.DockedShips.Add(child);child.DockingPorts.Add(new GdDict{{"connection_kind","secured"}});
            child.GetAccess().Claim("player_local");s.VisitedShips[child.MarkerId]=child;
            Assert.IsTrue(s.SetPilotedShip(child).GetBool("success"));Assert.AreSame(s.HomeShip,s.PilotedShip);
            Assert.AreSame(s.HomeShip,child.ParentShip);Assert.AreEqual("secured",((GdDict)child.DockingPorts[0]).GetString("connection_kind"));
            Assert.IsTrue(s.SetPilotedShip(s.LifeboatShip).GetBool("success"));Assert.AreSame(s.LifeboatShip,s.PilotedShip);
        }

        [Test]
        public void StartingHomeAnchorIsRetiredAndMovedLocationRemainsAuthoritative()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            try
            {
                s.HomeSeaPosition = new Vec3(42, 0, -72);
                var saved = WorldSnapshotAssembler.Build(s); Assert.IsFalse(saved.MobileHomeState.Has("starting_home_anchor"));
                // The reviewed first-away profile that wrote this anchor is gone: a world carrying one is refused.
                saved.MobileHomeState["starting_home_anchor"] = new GdDict { { "version", 1L }, { "source", "starting_world_position_v1" },
                    { "sea_position", GdArray.Of(42.0, 0.0, -72.0) } };
                s.HomeSeaPosition = new Vec3(999, 0, 999);
                Assert.IsFalse(WorldSnapshotAssembler.Apply(s, saved)); Assert.AreEqual(new Vec3(999, 0, 999), s.HomeSeaPosition);
                s.HomeSeaPosition = new Vec3(84, 0, 96); s.HomeSeaMarkerId = "fixture-contact";
                var moved = WorldSnapshotAssembler.Build(s); Assert.IsFalse(moved.MobileHomeState.Has("starting_home_anchor"));
                Assert.IsTrue(WorldSnapshotAssembler.Apply(s, moved)); Assert.AreEqual(new Vec3(84, 0, 96), s.HomeSeaPosition);
                Assert.AreEqual("fixture-contact", s.HomeSeaMarkerId);
            }
            finally { s.Dispose(); }
        }

        [Test]
        public void MobileHomePositionRoundTripsAndMalformedLocationRejectsBeforeMutation()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;
            var old=WorldSnapshotAssembler.Build(s);
            Assert.IsFalse(old.MobileHomeState.Has("home_location"),"old stationary homes keep the optional-field contract");
            s.HomeSeaPosition=new Vec3(42,0,-72);s.HomeSeaMarkerId="fixture-contact";
            var moved=WorldSnapshotAssembler.Build(s);
            Assert.AreEqual(42,V.F64(moved.MobileHomeState.GetDictOrEmpty("home_location").GetArrayOrEmpty("sea_position")[0]));
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s,moved));
            Assert.AreEqual(new Vec3(42,0,-72),s.HomeSeaPosition);Assert.AreEqual("fixture-contact",s.HomeSeaMarkerId);
            moved.MobileHomeState.GetDictOrEmpty("home_location")["sea_position"]=GdArray.Of(double.NaN,0.0,0.0);
            var live=s.HomeShip;
            Assert.IsFalse(WorldSnapshotAssembler.Apply(s,moved));Assert.AreSame(live,s.HomeShip);
            Assert.AreEqual(new Vec3(42,0,-72),s.HomeSeaPosition);
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s,old));Assert.AreEqual(Vec3.Zero,s.HomeSeaPosition);
        }

        [Test]
        public void DamageObserverAllocatesOnlyActualClampedLossAndInventoryReportsActualDebits()
        {
            var vitals=new VitalsState();vitals.Health=3;vitals.HealthDrainRate=0;vitals.Hunger=100;
            var loss=new Dictionary<string,double>();vitals.HealthDamageObserved+=(source,amount)=>loss[source]=amount;
            vitals.Tick(1,new GdDict{{"fire_health_drain",4.0},{"wound_health_drain",2.0},{"moving",false}});
            Assert.AreEqual(0,vitals.Health);Assert.AreEqual(2,loss["fire_health_drain"],1e-9);
            Assert.AreEqual(1,loss["wound_health_drain"],1e-9);Assert.AreEqual(3,loss.Values.Sum(),1e-9);
            var inventory=new InventoryState();inventory.AddItem("purified_water",2);long debits=0;
            inventory.ItemsRemoved+=(id,qty)=>{Assert.AreEqual("purified_water",id);debits+=qty;};
            Assert.AreEqual(2,inventory.RemoveItem("purified_water",5));Assert.AreEqual(0,inventory.RemoveItem("purified_water",1));
            Assert.AreEqual(2,debits,"failed/excess removals cannot fabricate resource use");
        }

        [Test]
        public void SharedCargoBayRoomFocusAndDispatchSelectNearestEligibleConsoleWithoutMovingCraft()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;
            s.HangarControls.Clear();s.CargoHoldControls.Clear();
            var cargo=new CargoHoldControl();cargo.Configure(s.HomeShip.ShipId,new Vec3(1000,0,1000));
            var hangar=new HangarBayControl();hangar.Configure(s.HomeShip.ShipId,new Vec3(1001.25,0,1000));
            int deposits=0,docks=0;cargo.CargoDepositRequested+=_=>deposits++;hangar.BayDockRequested+=(_,__)=>docks++;
            s.CargoHoldControls.Add(cargo);s.HangarControls.Add(hangar);
            var pose=s.LifeboatShip.SceneRoot.GlobalTransform;var parent=s.LifeboatShip.ParentShip;
            rig.Scene.PlayerPosition=cargo.GlobalPosition;
            Assert.IsTrue(s.CanFocusInteractable(cargo));Assert.IsFalse(s.CanFocusInteractable(hangar));
            Assert.AreEqual("cargo_deposit",s.RequestInteract());Assert.AreEqual(1,deposits);Assert.AreEqual(0,docks);
            Assert.AreSame(parent,s.LifeboatShip.ParentShip);Assert.AreEqual(pose,s.LifeboatShip.SceneRoot.GlobalTransform);
            rig.Scene.PlayerPosition=hangar.GlobalPosition;
            Assert.IsTrue(s.CanFocusInteractable(hangar));Assert.IsFalse(s.CanFocusInteractable(cargo));
            Assert.AreEqual("hangar",s.RequestInteract());Assert.AreEqual(1,docks);Assert.AreEqual(1,deposits);
        }

        [Test]
        public void PublishedRationsAndWaterHaveRealInventoryAndHotbarUseWithSpoilageScaling()
        {
            var s=SessionHarness.CreateGolden().Session;
            s.InventoryState.AddItem("ration_pack",2);s.InventoryState.AddItem("purified_water",1);
            s.VitalsState.Hunger=30;s.VitalsState.Thirst=30;
            Assert.IsTrue(s.ConsumableState.AssignHotbarSlot(0,"ration_pack"));Assert.IsTrue(s.ConsumableState.AssignHotbarSlot(1,"purified_water"));
            Assert.IsFalse(s.ConsumableState.HasUseAction("hull_sealant"));Assert.IsFalse(s.ConsumableState.HasUseAction("power_cell"));
            Assert.IsTrue(s.UseConsumableItem("ration_pack").GetBool("ok"));Assert.AreEqual(45,s.VitalsState.Hunger);Assert.AreEqual(35,s.VitalsState.Thirst);
            Assert.IsTrue(s.UseConsumableItem("purified_water").GetBool("ok"));Assert.AreEqual(50,s.VitalsState.Thirst);
            var food=s.SpoilageState.AddFood("ration_pack",s.ConsumableState.Definitions.GetDictOrEmpty("ration_pack"));food.CurrentStage=(long)FoodState.Stage.STALE;
            Assert.IsTrue(s.UseConsumableItem("ration_pack").GetBool("ok"));Assert.AreEqual(54,s.VitalsState.Hunger,.001);Assert.AreEqual(53,s.VitalsState.Thirst,.001);
            Assert.AreEqual(0,s.InventoryState.GetQuantity("ration_pack"));Assert.AreEqual(0,s.InventoryState.GetQuantity("purified_water"));
        }

        [Test]
        public void CutMooringRequiresToolSkillReachAndPreservesDamageOtherShipsAndSaveState()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;
            s.ThreatManager.Threats.Clear();
            var at=new Vec3(800,.4,800);
            var control=new HomeJoinControl {ShipId=s.HomeShip.ShipId,ActionId="cut_web_attachment",Parent=s.HomeShip.SceneRoot,
                LocalPosition=at,InteractionRadius=1.8,Web=s.HullWebState,Hull=s.HullIntegrityState};
            s.HomeJoinControls.Clear();s.HomeJoinControls.Add(control);rig.Scene.PlayerPosition=control.GlobalPosition;
            s.PlayerProgression.Skills["repair"]=1L;
            s.BeginWorkHold();Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));Assert.IsFalse(s.WorkActionDriver.IsWorking(),"missing cutter blocks work");
            Assert.AreEqual("tool",s.WorkActionDriver.Work.BlockReason);
            s.InventoryState.AddItem("plasma_cutter",1);
            Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));Assert.IsTrue(s.WorkActionDriver.IsWorking(),"repair work is universal: too little skill is slower, not blocked");
            Assert.AreEqual(4.0*1.25,s.WorkActionDriver.Work.EffectiveDuration,1e-9,"one missing repair level adds 25% to the 4 s cut");
            s.EndWorkHold();Assert.IsTrue(s.CancelWorkAction());
            s.PlayerProgression.Skills["repair"]=2L;s.BeginWorkHold();
            GdDict workHud=null;s.Events.WorkActionHudState+=state=>workHud=state;
            s.VitalsState.Stamina=0;
            Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));Assert.IsFalse(s.WorkActionDriver.IsWorking());
            Assert.AreEqual("exhausted",workHud.GetString("block_reason"),"denial reaches the player-facing work event");
            s.VitalsState.Stamina=s.VitalsState.MaxStamina;
            Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));TickSeconds(rig,.5);
            Assert.IsTrue(s.WorkActionDriver.IsWorking());s.EndWorkHold();Assert.IsTrue(s.CancelWorkAction());
            Assert.IsTrue(s.HullWebState.AttachedToWeb,"interruption preserves attachment");
            s.BeginWorkHold();Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));
            rig.Scene.PlayerPosition+=new Vec3(8,0,0);TickSeconds(rig,.25);
            Assert.IsFalse(s.WorkActionDriver.IsWorking(),"leaving the actual work site interrupts");Assert.IsTrue(s.HullWebState.AttachedToWeb);
            Assert.AreEqual("left_work_site",s.WorkActionDriver.Work.BlockReason);
            rig.Scene.PlayerPosition=control.GlobalPosition;s.BeginWorkHold();Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));
            double before=s.HullIntegrityState.AverageIntegrity();string inventory=GdJson.Stringify(s.InventoryState.Items);
            TickSeconds(rig,8);s.EndWorkHold();
            Assert.IsFalse(s.HullWebState.AttachedToWeb);Assert.IsTrue(s.LifeboatShip.GetWeb().AttachedToWeb,"other vessel remains attached");
            Assert.LessOrEqual(s.HullIntegrityState.AverageIntegrity(),before,"no automatic hull restoration");
            Assert.AreEqual(inventory,GdJson.Stringify(s.InventoryState.Items),"no resource grant or consumption");
            var snapshot=WorldSnapshotAssembler.Build(s);s.HullWebState.AttachedToWeb=true;
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s,snapshot));Assert.IsFalse(s.HullWebState.AttachedToWeb,"cut-free state survives world restore");
        }

        [Test]
        public void CutMooringRejectsAnotherOwnersShipAndAChangedToolAtCompletion()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ThreatManager.Threats.Clear();
            var at=new Vec3(800,.4,800);var control=new HomeJoinControl {ShipId=s.HomeShip.ShipId,ActionId="cut_web_attachment",
                Parent=s.HomeShip.SceneRoot,LocalPosition=at,InteractionRadius=1.8};
            s.HomeJoinControls.Clear();s.HomeJoinControls.Add(control);rig.Scene.PlayerPosition=control.GlobalPosition;
            s.PlayerProgression.Skills["repair"]=2L;s.InventoryState.AddItem("plasma_cutter",1);
            s.HomeShip.GetAccess().OwnerId="other_player";s.HomeShip.GetAccess().AccessIds.Clear();
            Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));Assert.IsFalse(s.WorkActionDriver.IsWorking());
            s.HomeShip.GetAccess().OwnerId="";s.BeginWorkHold();Assert.IsTrue(s.TryHomeJoinWork(rig.Scene.PlayerPosition));
            Assert.IsTrue(s.WorkActionDriver.IsWorking());s.InventoryState.RemoveItem("plasma_cutter",1);
            TickSeconds(rig,8);s.EndWorkHold();Assert.IsTrue(s.HullWebState.AttachedToWeb,"completion revalidates actual tool possession");
        }

        [Test]
        public void RecoveryHullSealUsesItsProtectedCargoAnchorAndLegacyPlacementIsRetained()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.CurrentShip=s.HomeShip;s.AwayFromStart=true;
            s.CurrentShip.Blueprint.GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.Profile;
            var anchor=new Vec3(125,.12,17);
            ((FakeLoaderView)s.Loader).Model.LootContainerSpecs=GdArray.Of(new GdDict{{"id","loot_cargo_01"},{"room_id","cargo_01"},{"position",anchor}});
            s.HullIntegrityState.DamageCompartment("cargo",1,true);
            var build=typeof(RunSession).GetMethod("BuildBreachSealPoints",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            build.Invoke(s,null);var seal=s.BreachSealPoints.Find(p=>p.CompartmentId=="cargo");
            Assert.IsNotNull(seal);Assert.AreEqual(anchor,seal.LocalPosition);
            s.CurrentShip.Blueprint.GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.LegacyProfile;
            build.Invoke(s,null);Assert.AreNotEqual(anchor,s.BreachSealPoints.Find(p=>p.CompartmentId=="cargo").LocalPosition);
        }

        [Test]
        public void CommissionedShuttleHasIndependentSavedSystemsAndLegacyMigrationKeepsEarnedRepairs()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session; s.ForceRepairAll();
            TickSeconds(rig, 1);
            s.LifeboatCommissioned = true;
            s.HomeShip.SystemsManager.DamageSubcomponent("power", "reactor_core", 1);
            Assert.IsTrue(s.LifeboatShip.SystemsManager.IsOperational("power"), "home damage cannot affect commissioned craft");
            var snapshot = WorldSnapshotAssembler.Build(s);
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s, snapshot));
            Assert.AreNotSame(s.HomeShip.SystemsManager, s.LifeboatShip.SystemsManager);
            Assert.IsFalse(s.HomeShip.SystemsManager.IsOperational("power"));
            Assert.IsTrue(s.LifeboatShip.SystemsManager.IsOperational("power"));
            s.ForceRepairAll(); snapshot = WorldSnapshotAssembler.Build(s); snapshot.MobileHomeState.Clear();
            Assert.IsTrue(WorldSnapshotAssembler.Apply(s, snapshot), "old saves explicitly migrate the previously shared repaired systems");
            Assert.AreNotSame(s.HomeShip.SystemsManager, s.LifeboatShip.SystemsManager);
            Assert.IsTrue(s.LifeboatShip.SystemsManager.IsOperational("power"));
        }

        [Test]
        public void CommissionedShuttleShelterRequiresItsOwnLifeSupportAndPhysicalOccupancy()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ForceRepairAll();TickSeconds(rig,1);
            s.LifeboatCommissioned=true;s.CurrentShip=s.HomeShip;s.AwayFromStart=true;
            s.CurrentShip.Blueprint.GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.Profile;
            s.CurrentOccupancy=s.LifeboatShip;s.SanityState.Sanity=50;
            s.StageSanityHallucination(2,SessionLocation.Away);Assert.Greater(s.SanityState.Sanity,50);
            s.LifeboatShip.SystemsManager.DamageSubcomponent("life_support","air_recycler",1);
            s.SanityState.Sanity=50;s.StageSanityHallucination(2,SessionLocation.Away);Assert.Less(s.SanityState.Sanity,50,"broken boat services are not shelter");
            s.LifeboatShip.SystemsManager.ForceRepair("life_support","air_recycler");s.CurrentOccupancy=s.CurrentShip;
            s.SanityState.Sanity=50;s.StageSanityHallucination(2,SessionLocation.Away);Assert.Less(s.SanityState.Sanity,50,"remote repaired boat is not a global safe zone");
        }

        [Test]
        public void RecoveryProfileRepairsUseProtectedFunctionalAnchorWithLegacyPlacementUnchanged()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ForceRepairAll();
            s.CurrentShip=s.HomeShip;s.AwayFromStart=true;
            s.CurrentShip.Blueprint.GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.Profile;
            var anchor=new Vec3(125,.12,17);
            ((FakeLoaderView)s.Loader).Model.LootContainerSpecs=GdArray.Of(new GdDict{{"id","loot_medical_01"},{"room_id","medical_01"},{"position",anchor}});
            s.ShipSystemsManager.DamageSubcomponent("life_support","air_recycler",1);
            var build=typeof(RunSession).GetMethod("BuildRepairPoints",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            build.Invoke(s,null);var repair=s.RepairPoints.Find(p=>p.SubcomponentId=="air_recycler");
            Assert.IsNotNull(repair);Assert.AreEqual(anchor,repair.LocalPosition);Assert.AreEqual(2,repair.MinSkill);
            Assert.AreSame(s.ShipSystemsManager,repair.TargetManager);
            s.CurrentShip.Blueprint.GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.LegacyProfile;
            build.Invoke(s,null);Assert.AreNotEqual(anchor,s.RepairPoints.Find(p=>p.SubcomponentId=="air_recycler").LocalPosition);
        }

        [Test]
        public void SuppressedFireExposesComponentsBrokenAfterBoardingForRealRepair()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;s.ForceRepairAll();
            s.ShipSystemsManager.DamageSubcomponent("power","reactor_core",1);
            typeof(RunSession).GetMethod("OnFireExtinguished",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(s,new object[]{"power"});
            var point=s.RepairPoints.Find(p=>p.SystemId=="power"&&p.SubcomponentId=="reactor_core");
            Assert.IsNotNull(point,"new fire damage must remain repairable after suppression");
            Assert.AreSame(s.ShipSystemsManager,point.TargetManager);
            Assert.IsFalse(point.Repaired);Assert.AreEqual(4,point.MinSkill);
        }

        [Test]
        public void NewRecoveryProfileHasOnlyAuthoredRadiationWhileLegacyFallbackRemains()
        {
            var rig=SessionHarness.CreateGolden();var s=rig.Session;
            var loader=new FakeLoaderView(s.HomeShip.BuiltLayout,new GdDict(),"")
                {IsInsideTree=true,Transform=new Xform3(Basis3.Identity,new Vec3(800,0,800))};
            loader.Model.AuthoredAtmosphereSpecs.Clear();loader.Model.RadiationZoneSpecs.Clear();
            var bp=new SynapticSea.Core.Procgen.ShipBlueprint(1,0,17){GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.Profile};
            s.CurrentShip=ShipInstance.Create("radiation_test","test",bp,s.ShipSystemsManager,loader);
            s.CurrentShip.BuiltLayout=s.HomeShip.BuiltLayout;s.AwayFromStart=true;s.RadiationState.Radiation=0;
            var floor=AssemblyMobility.Floors(s.CurrentShip.BuiltLayout)[0];
            rig.Scene.PlayerPosition=s.CurrentShip.SceneRoot.GlobalTransform*(floor+new Vec3(0,.55,0));
            s.CurrentOccupancy=s.CurrentShip;
            TickSeconds(rig,2);Assert.AreSame(s.CurrentShip,s.CurrentOccupancy,"the fixture must stand on the tested hull");
            Assert.AreEqual(0,s.RadiationState.Radiation,"a new ship without a radiation source is not universally radioactive");
            s.CurrentShip.Blueprint.GenerationProfile=SynapticSea.Core.Procgen.ConstrainedExpedition.LegacyProfile;
            TickSeconds(rig,2);Assert.Greater(s.RadiationState.Radiation,0,"legacy saves retain their prior fallback contract");
        }

        [Test]
        public void ExplicitNonRadiatingAtmosphereDoesNotUseLegacyShipWideHazardFallback()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            var loader = (FakeLoaderView)s.Loader;
            loader.Model.AuthoredAtmosphereSpecs = GdArray.Of(new GdDict {{ "radiation_bp", 0L }});
            s.RadiationState.Radiation = 0;
            s.AwayFromStart = true;
            s.CurrentShip = s.HomeShip;
            TickSeconds(rig, 5);
            Assert.AreEqual(0, s.RadiationState.Radiation, "zero-authored radiation must not become a positive global source");
        }

        [Test]
        public void SuppressionControlUsesMatchingFireLocationRatherThanObjectiveOrder()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            s.FireSuppressionState.Ignite("power", 1);
            var firePosition = new Vec3(8, .12f, -12);
            s.FireZoneNodes["power"] = new SessionZone { CompartmentOrRoomId = "power", LocalPosition = firePosition, Parent = s.LifeboatShip.SceneRoot };
            typeof(RunSession).GetMethod("BuildFireSuppressionPoints", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null);
            var control = s.FireSuppressionPoints.Find(p => p.CompartmentId == "power");
            Assert.IsNotNull(control);
            Assert.AreEqual(firePosition, control.LocalPosition);
            Assert.AreSame(s.LifeboatShip.SceneRoot, control.Parent);
        }

        [Test]
        public void InvalidOwnedMobilityRejectsSaveBeforeChangingLiveShip()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            var snapshot = WorldSnapshotAssembler.Build(s); var original = s.HomeShip;
            snapshot.MobileHomeState.GetDictOrEmpty("lifeboat").GetDictOrEmpty("mobility")["engine_id"] = "propulsion:ship_start";
            Assert.IsFalse(WorldSnapshotAssembler.Apply(s, snapshot)); Assert.AreSame(original, s.HomeShip);
            snapshot = WorldSnapshotAssembler.Build(s);
            snapshot.MobileHomeState.GetDictOrEmpty("home_mobility")["dry_mass_kg"] = double.PositiveInfinity;
            Assert.IsFalse(WorldSnapshotAssembler.Apply(s, snapshot)); Assert.AreSame(original, s.HomeShip);
        }

        [Test]
        public void DepartureUsesPilotedHullRatherThanDeterioratedHomeHull()
        {
            var rig=SessionHarness.CreateGolden(); var s=rig.Session; s.ForceRepairAll();
            foreach(object id in new List<object>(s.HullIntegrityState.Compartments.Keys)) s.HullIntegrityState.DamageCompartment(V.Str(id),1.0);
            TickSeconds(rig,10);
            Assert.AreEqual(0,s.HullIntegrityState.AverageIntegrity(),"home deterioration is retained");
            Assert.IsTrue(s.PropulsionExpandedState.CanPropel(),"the intact piloted boat has its own hull authority");
            s.PilotedShip.GetHull().Configure(new GdDict {{"compartments",GdArray.Of(new GdDict {{"compartment_id","boat"},{"health",.2}})}});
            TickSeconds(rig,10);
            Assert.IsFalse(s.PropulsionExpandedState.CanPropel(),"an actually damaged piloted hull still gates propulsion");
        }

        [Test]
        public void OperationalDepartureSurvivesHomeWorldSaveAndContinue()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            s.ForceRepairAll(); TickSeconds(rig, 10);
            Assert.IsTrue(s.PropulsionExpandedState.CanPropel(), GdJson.Stringify(s.GetShipSystemsExpandedSummary()));
            var before = s.GetShipSystemsExpandedSummary();
            Assert.IsTrue(s.RequestSave()); Assert.IsTrue(s.RequestLoad());
            Assert.IsTrue(s.PropulsionExpandedState.CanPropel(), "before="+GdJson.Stringify(before)+" after="+GdJson.Stringify(s.GetShipSystemsExpandedSummary()));
            Assert.IsTrue(s.PilotedShip.SystemsManager.IsOperational("propulsion"));
        }

        [Test]
        public void GoldenSession_BootsLikeTheGodotSlice()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.PlayableStarted, "session did not start: " + s.LastFailureReason);
            Assert.IsTrue(rig.Scene.HasPlayer, "player spawned");
            Assert.AreEqual(4, s.SequenceInteractables.Count, "four objective sequences");
            Assert.AreEqual(5, s.Interactables.Count, "four objectives, one of them a two-step junction");
            Assert.AreEqual(1, s.CurrentObjectiveSequence);
            Assert.IsNotNull(s.HomeShip);
            Assert.AreSame(s.HomeShip, s.CurrentShip);
            Assert.IsNotNull(s.LifeboatShip, "lifeboat built");
            Assert.AreSame(s.LifeboatShip, s.PilotedShip, "the lifeboat is the ride");
            Assert.AreSame(s.HomeShip, s.LifeboatShip.ParentShip, "lifeboat port-docked to home");
            Assert.AreEqual(1, s.DockBarriers.Count, "boot home seam barrier");

            // route_control smoke: one powered gate, blocking, closed.
            GdDict route = s.GetRouteControlSummary();
            Assert.GreaterOrEqual(V.I64(route.Get("route_gate_count", 0L)), 1);
            Assert.GreaterOrEqual(V.I64(route.Get("active_blocker_count", 0L)), 1);
            Assert.AreEqual(0, V.I64(route.Get("opened_gate_count", -1L)));
            Assert.IsFalse(route.GetBool("extraction_unlocked"));
            Assert.GreaterOrEqual(s.GetRouteGateCollisionEnabledCount(), 1);

            // ship_systems smoke: power restored/extraction start false.
            GdDict sys = s.GetShipSystemsSummary();
            Assert.IsFalse(sys.GetBool("main_power_restored"));
            Assert.IsFalse(sys.GetBool("extraction_unlocked"));

            // Component placement matches the Godot capture (14 placements, home seed 1).
            Assert.AreEqual(14, s.ComponentPlacementState.Placed.Count);
            Assert.AreEqual(1, s.ComponentPlacementState.SeedValue);

            // Threats fall back to the five layout archetypes around the home anchor (Godot capture).
            Assert.AreEqual(5, s.ThreatManager.Threats.Count);
            Assert.AreEqual("fallback_0_0", s.ThreatManager.Threats[0].InstanceId);
        }

        [Test]
        public void GoldenSession_TicksAndCompletesTheObjectiveChain()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            var interactions = new List<long>();
            GdDict completion = null;
            s.PlayableInteractionCompleted += (iid, oid, seq, type, room) => interactions.Add(seq);
            s.PlayableSliceCompleted += summary => completion = summary;
            // The golden ship has no encounter markers, so the five fallback threats spawn around the ship origin, next to
            // the start room; an idle player dies in ~10 s (see IdlePlayerInReach_IsKilledByTheFallbackStalker). This
            // test is about the objective chain, so the threats are removed.
            s.ThreatManager.Threats.Clear();

            TickSeconds(rig, 10.0);
            Assert.AreEqual(10.0, s.WorldTime, 1e-9);
            Assert.AreEqual(10.0, s.RunPlayTimeSeconds, 1e-9);
            Assert.IsFalse(s.SliceComplete);

            Assert.IsTrue(s.CompleteObjectiveSequence(1), "objective 1");
            Assert.IsTrue(s.GetShipSystemsSummary().GetBool("emergency_supplies_recovered"));
            Assert.AreEqual(0, V.I64(s.GetRouteControlSummary().Get("opened_gate_count", -1L)), "gates stay closed after obj 1");

            TickSeconds(rig, 5.0);
            Assert.IsTrue(s.CompleteObjectiveSequence(2), "objective 2 (two-step junction)");
            GdDict s2 = s.GetShipSystemsSummary();
            Assert.IsTrue(s2.GetBool("main_power_restored"));
            Assert.IsTrue(s2.GetBool("blocked_routes_cleared"));
            Assert.AreEqual(0, V.I64(s2.Get("blocked_affordance_visible_count", -1L)));
            Assert.IsTrue(s.GetOxygenSummary().GetBool("breach_sealed"), "restore_systems seals the breach");
            Assert.GreaterOrEqual(V.I64(s.GetRouteControlSummary().Get("opened_gate_count", 0L)), 1);
            Assert.IsTrue(s.GetRouteControlSummary().GetBool("powered_gates_open"));
            Assert.AreEqual(0, V.I64(s.GetRouteControlSummary().Get("active_blocker_count", -1L)));
            Assert.IsFalse(s2.GetBool("extraction_unlocked"));
            // OBJECTIVE_REPAIR_MAP: restore_systems -> power_distribution + battery_cells.
            Assert.IsTrue(s.ShipSystemsManager.GetSystem("power").GetSubcomponent("power_distribution").IsFunctional());
            Assert.IsTrue(s.ShipSystemsManager.GetSystem("power").GetSubcomponent("battery_cells").IsFunctional());

            // Objectives 2-4 back to back, like the smoke: the damaged power grid keeps wearing while time passes, so
            // ticking after restore_systems would pull power_percent below the smoke's 100.
            Assert.IsTrue(s.CompleteObjectiveSequence(3), "objective 3");
            Assert.IsTrue(s.GetShipSystemsSummary().GetBool("navigation_logs_downloaded"));
            Assert.IsTrue(s.ShipSystemsManager.GetSystem("navigation").GetSubcomponent("nav_computer").IsFunctional());

            Assert.IsTrue(s.CompleteObjectiveSequence(4), "objective 4");
            GdDict s4 = s.GetShipSystemsSummary();
            Assert.IsTrue(s4.GetBool("reactor_stabilized"));
            Assert.IsTrue(s4.GetBool("extraction_unlocked"));
            Assert.AreEqual(100, V.I64(s4.Get("power_percent", 0L)));
            Assert.AreEqual(100, V.I64(s4.Get("reactor_stability_percent", 0L)));

            // Onboarding completion keeps this life and world active.
            Assert.IsTrue(s.HomeObjectivesComplete);
            Assert.IsFalse(s.SliceComplete);
            Assert.AreEqual(4, s.ObjectiveCompletionCount);
            Assert.AreEqual(5, s.CurrentObjectiveSequence);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, interactions);
            Assert.IsNull(completion, "onboarding must not emit terminal results");
            Assert.IsFalse(s.GetSliceCompletionSummary().GetBool("run_complete"));
            Assert.IsTrue(s.GetSliceCompletionSummary().GetBool("home_objectives_complete"));

            // Survival and saving continue after onboarding.
            double playTime = s.RunPlayTimeSeconds;
            TickSeconds(rig, 2.0);
            Assert.AreEqual(playTime + 2.0, s.RunPlayTimeSeconds, 1e-12);
            Assert.IsTrue(rig.Storage.FileExists(SaveLoadService.WORLD_SLOT_FILE), "onboarding checkpoints the ongoing world");
            Assert.IsTrue(s.RequestSave());
            Assert.IsTrue(s.RequestLoad());
            Assert.IsTrue(s.HomeObjectivesComplete);
            Assert.IsFalse(s.SliceComplete);
            foreach (var objective in s.Interactables)
            {
                Assert.IsTrue(objective.Completed, "restored onboarding marker " + objective.InteractionId);
                Assert.IsFalse(objective.Active);
            }
        }

        [Test]
        public void OccupiedHomeBerthRejectsReturnWithoutClearingActiveShip()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            var wreck = ShipInstance.Create("reclaimed", "1:1:1", null, null,
                new SynapticSea.Tests.Systems.FakeRoot());
            wreck.BuiltLayout = s.HomeShip.BuiltLayout.DeepCopy();
            s.CurrentShip = wreck; s.PilotedShip = wreck; s.AwayFromStart = true;
            var homeRoot = s.HomeShip.SceneRoot; var player = rig.Scene.PlayerPosition;
            Assert.IsFalse(s.TravelHome(), "the shuttle already occupies this home endpoint");
            Assert.AreSame(wreck, s.CurrentShip); Assert.IsTrue(s.AwayFromStart);
            Assert.AreSame(homeRoot, s.HomeShip.SceneRoot); Assert.AreEqual(player, rig.Scene.PlayerPosition);
            Assert.IsNull(wreck.ParentShip);
            Assert.AreSame(s.HomeShip, s.LifeboatShip.ParentShip);
        }

        [Test]
        public void UnsupportedConnectionRestorePreservesLiveWorldAndSave()
        {
            var rig = SessionHarness.CreateGolden(); var s = rig.Session;
            Assert.IsTrue(s.RequestSave());
            var ws = s.SaveLoadService.LoadWorld();
            Assert.Greater(ws.DockEdges.Count, 0);
            ((GdDict)ws.DockEdges[0])["connection_version"] = 99L;
            Assert.IsTrue(s.SaveLoadService.SaveWorld(ws));
            var root = s.HomeShip.SceneRoot; var player = s.Scene.PlayerPosition;
            Assert.IsFalse(s.RequestLoad());
            Assert.AreSame(root, s.HomeShip.SceneRoot, "preflight must reject before freeing/rebuilding live roots");
            Assert.AreEqual(player, s.Scene.PlayerPosition);
            Assert.IsTrue(s.SaveLoadService.HasSave(), "incompatible save is preserved for recovery");
        }

        [Test]
        public void PartialJunctionRestorePreservesStepEligibilityWithoutReplayingCompletion()
        {
            var s = SessionHarness.CreateGolden().Session;
            Assert.IsTrue(s.CompleteObjectiveSequence(1));
            var first = s.SequenceInteractables[2][0];
            first.SetValidationPlayerInRange(true);
            Assert.IsTrue(first.TryInteract(first.GlobalPosition));
            Assert.AreEqual(2, s.CurrentObjectiveSequence);
            Assert.IsTrue(s.RequestSave());
            int replayed = 0;
            s.Events.TrackerCompleted += _ => replayed++;
            Assert.IsTrue(s.RequestLoad());
            var restored = s.SequenceInteractables[2];
            Assert.IsTrue(restored[0].Completed);
            Assert.IsFalse(restored[0].Active);
            Assert.IsFalse(restored[0].TryInteract(restored[0].GlobalPosition));
            Assert.IsFalse(restored[1].Completed);
            Assert.IsTrue(restored[1].Active);
            Assert.AreEqual(1, replayed, "only completed sequence 1 is replayed to the tracker, not junction completion/rewards");
            Assert.IsTrue(s.CompleteObjectiveSequence(2));
            Assert.AreEqual(3, s.CurrentObjectiveSequence);
            Assert.AreEqual(2, s.ObjectiveCompletionCount);
        }

        [Test]
        public void GoldenSession_CheckpointSavesAndReloadsTheWorld()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.CompleteObjectiveSequence(1));
            Assert.IsTrue(rig.Storage.FileExists(SaveLoadService.WORLD_SLOT_FILE), "objective boundary writes the world checkpoint");
            Assert.IsNotNull(s.LastSavedSnapshot);
            Assert.AreEqual(2, s.LastSavedSnapshot.CurrentObjectiveSequence, "the checkpoint captures the resumed sequence");

            s.ThreatManager.Threats.Clear();
            Vec3 savedAt = rig.Scene.PlayerPosition;
            TickSeconds(rig, 2.0);
            Assert.IsTrue(s.RequestSave(), "F5 world save");
            double savedPlayTime = s.RunPlayTimeSeconds;

            // Diverge without crossing an objective boundary (that would overwrite world.json with a checkpoint).
            TickSeconds(rig, 3.0);
            rig.Scene.PlayerPosition = new Vec3(1.0f, 2.0f, 3.0f);
            Assert.AreEqual(savedPlayTime + 3.0, s.RunPlayTimeSeconds, 1e-9);

            Assert.IsTrue(s.RequestLoad(), "world load");
            Assert.AreEqual(2, s.CurrentObjectiveSequence);
            Assert.IsTrue(s.CompletedObjectiveTypes.Has("recover_supplies"));
            Assert.IsFalse(s.CompletedObjectiveTypes.Has("restore_systems"));
            Assert.AreEqual(savedPlayTime, s.RunPlayTimeSeconds, 1e-9, "play time restored");
            Assert.AreEqual(savedAt, rig.Scene.PlayerPosition, "player restored to the saved position (scene half)");
            Assert.AreEqual(2, rig.Host.HomeLoads, "the reload re-drives the home loader");
            Assert.IsTrue(s.CompleteObjectiveSequence(2), "the chain continues after load");
            Assert.AreEqual(3, s.CurrentObjectiveSequence);
        }

        [Test]
        public void IdlePlayerInReach_IsKilledByTheFallbackStalker()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            var stalker = s.ThreatManager.Threats.Find(t => t.InstanceId == "fallback_2_0");
            s.ThreatManager.Threats.RemoveAll(t => t != stalker);
            stalker.RoomId = s.ResolvePlayerRoom(rig.Scene.PlayerPosition);
            Vec3 attackPosition = rig.Scene.PlayerPosition + new Vec3(1, 0, 0);
            stalker.WorldPosition = GdArray.Of((double)attackPosition.X, (double)attackPosition.Y, (double)attackPosition.Z);
            GdDict completion = null;
            s.PlayableSliceCompleted += summary => completion = summary;
            TickSeconds(rig, 15.0);
            Assert.IsTrue(s.SliceComplete, "the run ended");
            Assert.IsNotNull(completion);
            Assert.AreEqual("death", completion.GetString("reason"));
            Assert.IsTrue(s.VitalsState.IsIncapacitated());
            Assert.AreEqual("fallback_2_0", V.Str(s.ThreatManager.GetSummary().GetDictOrEmpty("last_attack_result").Get("source_id", "")));
            Assert.Less(s.RunPlayTimeSeconds, 15.0, "play time stops at death");
        }

        [Test]
        public void GoldenSession_InteractDispatch_MissesWhenNothingIsInRange()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            rig.Scene.PlayerPosition = new Vec3(500.0f, 0.0f, 500.0f);
            Assert.AreEqual(InteractionRegistry.MissHandlerId, s.RequestInteract());
            ObjectiveInteractable first = s.GetInteractableBySequence(1);
            rig.Scene.PlayerPosition = first.GlobalPosition;
            string handler = s.RequestInteract();
            Assert.AreNotEqual(InteractionRegistry.MissHandlerId, handler);
        }

        [Test]
        public void RouteGateOpening_DisablesItsBlockedRouteNodeCollider()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            var loader = (FakeLoaderView)s.Loader;
            Assert.AreEqual((0, true), loader.BlockedRouteCollisionCalls[loader.BlockedRouteCollisionCalls.Count - 1], "a closed gate keeps its route node collidable");
            Assert.AreEqual(0L, V.I64(s.RouteGateNodes[0].Meta["blocked_route_index"]));
            Assert.IsTrue(s.CompleteObjectiveSequence(1));
            Assert.IsTrue(s.CompleteObjectiveSequence(2), "restore_systems opens the powered gates");
            Assert.AreEqual((0, false), loader.BlockedRouteCollisionCalls[loader.BlockedRouteCollisionCalls.Count - 1], "the open gate's route node stops colliding");
        }

        [Test]
        public void ApplyManualSlot_DerivesGameplaySliceBesideLayoutWhenMissing()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            RunSnapshot snap = RunSnapshotAssembler.Build(s);
            Assert.IsNotNull(snap);
            string expected = snap.GameplaySlicePath;
            Assert.IsNotEmpty(expected);
            snap.GameplaySlicePath = "";
            Assert.IsTrue(s.ApplyManualSlot(snap), "a legacy empty slice path must still apply");
            Assert.AreEqual(expected, s.GameplaySlicePath);
            Assert.IsTrue(s.PlayableStarted);
        }

        [Test]
        public void RequestLoad_DerivesGameplaySliceBesideLayoutWhenMissing()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.IsTrue(s.RequestSave(), "world save written");
            WorldSnapshot world = s.SaveLoadService.LoadWorld();
            Assert.IsNotNull(world);
            world.HomeShip["gameplay_slice_path"] = "";
            Assert.IsTrue(s.SaveLoadService.SaveWorld(world));

            Assert.IsTrue(s.RequestLoad(), "Continue must apply a save that omitted gameplay_slice_path");
            Assert.AreEqual(SessionHarness.GoldenDir + "gameplay_slice.json", s.GameplaySlicePath);
            Assert.IsTrue(s.PlayableStarted);
        }
    }
}
