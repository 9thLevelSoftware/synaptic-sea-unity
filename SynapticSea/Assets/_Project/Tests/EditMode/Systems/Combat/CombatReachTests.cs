using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class CombatReachTests
    {
        [SetUp] public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }
        [TearDown] public void TearDown() => CatalogRegistry.Clear();

        static ThreatAIState Threat(string id, Vec3 position, string room = "a")
        {
            var threat = new ThreatAIState();
            threat.Configure(new GdDict {
                { "instance_id", id }, { "room_id", room }, { "health", 100.0 }, { "max_health", 100.0 },
                { "world_position", GdArray.Of((double)position.X, (double)position.Y, (double)position.Z) },
                { "attack_range", 2.0 }, { "attack_damage", 10.0 }, { "anchored", true },
                { "noise_sensitivity", 10.0 }, { "telegraph_seconds", 0.2 }, { "flee_threshold", 0.0 }
            });
            return threat;
        }
        static GdDict Layout() => new GdDict {
            { "rooms", GdArray.Of(
                new GdDict { { "id", "a" }, { "structural_placements", GdArray.Of(new GdDict {
                    { "module", "floor_1x1" }, { "world_position", GdArray.Of(0.0, 0.0, 0.0) } }) } },
                new GdDict { { "id", "b" }, { "structural_placements", GdArray.Of(new GdDict {
                    { "module", "floor_1x1" }, { "world_position", GdArray.Of(4.0, 0.0, 0.0) } }) } }) },
            { "room_links", GdArray.Of(new GdDict { { "from_room", "a" }, { "to_room", "b" }, { "module_id", "hatch_closed" } }) }
        };
        static EquipmentState Equipped(string weapon = "crowbar")
        {
            var equipment = new EquipmentState();
            equipment.Slots["primary_hand"] = weapon;
            return equipment;
        }
        static GdDict Swing(ThreatRuntime runtime, string target = "", Vec3? direction = null) =>
            runtime.AttackWithWeapon("crowbar", new InventoryState(), Equipped(), targetId: target,
                playerPosition: Vec3.Zero, attackDirection: direction ?? Vec3.Forward);
        static void Tick(ThreatRuntime runtime, VitalsState vitals, Vec3 position, double delta = 0.25)
        {
            runtime.SetPlayerSignals(1.0, 1.0, 1.0, false, "a");
            runtime.TickThreats(delta, vitals, null, new GdDict { { "durability", 0.0 } }, position);
        }

        [Test] public void Swing_SelectsNearestVisibleThreatInHeading_NotListOrder()
        {
            var runtime = new ThreatRuntime();
            runtime.Threats.Add(Threat("far", new Vec3(0, 0, -20)));
            runtime.Threats.Add(Threat("behind", new Vec3(0, 0, 1)));
            runtime.Threats.Add(Threat("blocked", new Vec3(0, 0, -0.5)));
            runtime.Threats.Add(Threat("near", new Vec3(0, 0, -1)));
            runtime.SetEngagedLos("blocked", false);
            Assert.AreEqual("near", Swing(runtime).GetString("target_id"));
            Assert.AreEqual(100.0, runtime.Threats[0].Health);
            Assert.AreEqual(100.0, runtime.Threats[1].Health);
            Assert.AreEqual(100.0, runtime.Threats[2].Health);
        }
        [TestCase(20, false)] [TestCase(1, true)]
        public void ExplicitTargetCannotBypassRangeOrObstruction(double distance, bool obstructed)
        {
            var runtime = new ThreatRuntime();
            var threat = Threat("target", new Vec3(0, 0, -distance));
            runtime.Threats.Add(threat);
            runtime.SetEngagedLos("target", !obstructed);
            Assert.AreEqual("no_target", Swing(runtime, "target").GetString("reason"));
            Assert.AreEqual(100.0, threat.Health);
        }
        [Test] public void CooldownBlocksRepeatedSwings_AndSurvivesSummary()
        {
            var runtime = new ThreatRuntime();
            runtime.Threats.Add(Threat("target", new Vec3(0, 0, -1)));
            Assert.IsTrue(Swing(runtime).GetBool("ok"));
            Assert.AreEqual("cooldown", Swing(runtime).GetString("reason"));
            var restored = new ThreatRuntime();
            restored.ApplySummary(runtime.GetSummary());
            Assert.AreEqual("cooldown", Swing(restored).GetString("reason"));
            Tick(restored, null, Vec3.Zero, 1.0);
            Assert.IsTrue(Swing(restored).GetBool("ok"));
        }
        [Test] public void RangedMissSpendsOneRound_AndCannotSpamDuringCooldown()
        {
            var runtime = new ThreatRuntime();
            var ammo = new AmmoState();
            ammo.Configure(new GdDict { { "magazines", new GdDict { { "flare_pistol", 2L } } } });
            var equipment = Equipped("flare_pistol");
            var inventory = new InventoryState();
            Assert.AreEqual("no_target", runtime.AttackWithWeapon("flare_pistol", inventory, equipment, ammo).GetString("reason"));
            Assert.AreEqual(1, ammo.Loaded("flare_pistol"));
            Assert.AreEqual("cooldown", runtime.AttackWithWeapon("flare_pistol", inventory, equipment, ammo).GetString("reason"));
            Assert.AreEqual(1, ammo.Loaded("flare_pistol"));
        }
        [Test] public void AnchoredEnemyMustReachPlayer_AndRetreatCancelsWindup()
        {
            var runtime = new ThreatRuntime();
            var threat = Threat("tendril", Vec3.Zero);
            runtime.Threats.Add(threat);
            var vitals = new VitalsState { Health = 100.0 };
            Tick(runtime, vitals, new Vec3(10, 0, 0));
            Tick(runtime, vitals, new Vec3(10, 0, 0));
            Assert.AreEqual(100.0, vitals.Health);
            Assert.AreEqual(ThreatAIState.STATE_HUNT, threat.State);
            Tick(runtime, vitals, new Vec3(1, 0, 0));
            Assert.AreEqual(ThreatAIState.STATE_TELEGRAPH, threat.State);
            Tick(runtime, vitals, new Vec3(10, 0, 0));
            Assert.AreEqual(100.0, vitals.Health);
            Tick(runtime, vitals, new Vec3(1, 0, 0));
            Tick(runtime, vitals, new Vec3(1, 0, 0));
            Assert.AreEqual(90.0, vitals.Health);
        }
        [Test] public void SealedPortalBlocksHitsEvenWithClearPhysicsRay_OpeningRestoresCombat()
        {
            var runtime = new ThreatRuntime();
            runtime.ConfigureSpatialPerception(Layout());
            runtime.PlayerRoomIdValue = "a";
            runtime.Threats.Add(Threat("across", new Vec3(0, 0, -1), "b"));
            runtime.SetEngagedLos("across", true);
            Assert.AreEqual("no_target", Swing(runtime).GetString("reason"));
            var vitals = new VitalsState { Health = 100.0 };
            Tick(runtime, vitals, Vec3.Zero, 1.0);
            Assert.AreEqual(100.0, vitals.Health);
            Assert.Less(runtime.SpatialPerception.AttenuateNoise("a", "b", 1), 0.2);
            runtime.SpatialPerception.SetDoorState("a", "b", "open");
            Assert.IsTrue(Swing(runtime).GetBool("ok"));
        }
        [Test] public void PlayerRoomUsesTranslatedActiveGraph()
        {
            var runtime = new ThreatRuntime { FallbackAnchor = new Vec3(100, 0, 50) };
            runtime.ConfigureNavGraph(Layout());
            var session = new RunSession(new RunSessionDeps()) { ThreatManager = runtime };
            Assert.AreEqual("a", session.ResolvePlayerRoom(new Vec3(100, 0, 50)));
            Assert.AreEqual("b", session.ResolvePlayerRoom(new Vec3(104, 0, 50)));
        }
        [Test] public void ThreatArmorWearsAcrossHits_AndBrokenArmorStopsReducingDamage()
        {
            var threat = Threat("armored", Vec3.Zero);
            threat.ArmorProfile = new GdDict { { "resistance", new GdDict { { "physical", 0.5 } } },
                { "durability", 1.0 }, { "max_durability", 1.0 }, { "wear_factor", 1.0 } };
            var pipeline = new DamagePipeline();
            var hit = new GdDict { { "damage_type", "physical" }, { "amount", 10.0 } };
            pipeline.ApplyToThreat(threat, hit);
            Assert.AreEqual(0.0, threat.ArmorProfile.GetFloat("durability"));
            pipeline.ApplyToThreat(threat, hit);
            Assert.AreEqual(85.0, threat.Health);
            var unarmored = Threat("bare", Vec3.Zero);
            pipeline.ApplyToThreat(unarmored, hit);
            Assert.AreEqual(90.0, unarmored.Health, "pipeline must not borrow the previous target's armor");
        }
        [Test] public void PlayerArmorWearMutatesSharedProfile_EquipmentSummaryPersistsIt()
        {
            var profile = new GdDict { { "resistance", new GdDict { { "physical", 0.5 } } },
                { "durability", 1.0 }, { "max_durability", 1.0 }, { "wear_factor", 1.0 } };
            var vitals = new VitalsState { Health = 100.0 };
            var pipeline = new DamagePipeline();
            var hit = new GdDict { { "amount", 10.0 } };
            pipeline.ApplyToVitals(vitals, null, profile, hit);
            pipeline.ApplyToVitals(vitals, null, profile, hit);
            Assert.AreEqual(85.0, vitals.Health);
            var equipment = Equipped();
            equipment.ArmorDurability["hardsuit"] = profile["durability"];
            var restored = new EquipmentState();
            restored.ApplySummary(equipment.GetSummary());
            Assert.AreEqual(0.0, restored.ArmorDurability.GetFloat("hardsuit"));
        }
    }
}
