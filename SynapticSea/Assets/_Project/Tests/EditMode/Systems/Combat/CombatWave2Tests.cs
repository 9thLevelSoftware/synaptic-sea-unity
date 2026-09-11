using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class DamagePipelineTests
    {
        sealed class FakeVitals : IDamageVitalsTarget
        {
            public double Health { get; set; }
        }

        [SetUp]
        public void SetUp() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        static GdDict PlayerArmor() => new GdDict
        {
            { "resistance", new GdDict { { "physical", 0.25 } } },
            { "durability", 20.0 },
            { "max_durability", 20.0 },
        };

        static GdDict Bite() => new GdDict
        {
            { "damage_type", "physical" }, { "amount", 20.0 }, { "noise", 0.4 },
            { "status_effect_id", "bleed" }, { "source_id", "test_bite" },
        };

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var pipeline = new DamagePipeline();
            pipeline.Configure();
            pipeline.ApplyToVitals(new FakeVitals { Health = 80.0 }, new StatusEffectsState(), PlayerArmor(), Bite());
            GdDict summary = pipeline.GetSummary();
            var restored = new DamagePipeline();
            restored.Configure();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
        }

        [Test]
        public void ApplyToVitalsAndThreat_MatchSmoke()
        {
            double callbackDamage = 0.0;
            var pipeline = new DamagePipeline();
            pipeline.Configure(new GdDict(), (dmg, ev) => callbackDamage = dmg);
            var vitals = new FakeVitals { Health = 80.0 };
            var statuses = new StatusEffectsState();
            GdDict hit = pipeline.ApplyToVitals(vitals, statuses, PlayerArmor(), Bite());
            Assert.AreEqual(65.0, vitals.Health, 0.01);
            Assert.AreEqual(15.0, callbackDamage, 0.01);
            Assert.IsTrue(statuses.HasEffect("bleed"));
            Assert.AreEqual("test_bite", hit.GetString("source_id"));

            var threat = new ThreatAIState();
            threat.Configure(new GdDict
            {
                { "instance_id", "target_01" }, { "archetype_id", "stalker" }, { "health", 24.0 }, { "max_health", 24.0 },
                { "armor_profile", new GdDict { { "resistance", new GdDict { { "physical", 0.5 } } } } },
            });
            pipeline.ApplyToThreat(threat, new GdDict
            {
                { "damage_type", "physical" }, { "amount", 10.0 }, { "noise", 0.2 }, { "stun_seconds", 1.5 }, { "source_id", "crowbar" },
            });
            Assert.AreEqual(19.0, threat.Health, 0.01);
            Assert.AreEqual(ThreatAIState.STATE_STUN, threat.State);
            Assert.AreEqual(2, pipeline.GetSummary().GetInt("processed_hits"));
            Assert.AreEqual(0.6, pipeline.TotalNoiseGenerated, 1e-9);
        }
    }

    public class ThreatAIStateTests
    {
        static ThreatAIState MakeStalker()
        {
            var threat = new ThreatAIState();
            threat.Configure(new GdDict
            {
                { "instance_id", "threat_01" }, { "archetype_id", "stalker" }, { "display_name", "Stalker" }, { "room_id", "bridge" },
                { "memory_seconds", 4.0 }, { "noise_sensitivity", 1.0 }, { "light_sensitivity", 0.5 }, { "sight_sensitivity", 0.5 },
                { "attack_interval", 1.2 },
            });
            return threat;
        }

        static GdDict Loud() => new GdDict
        {
            { "noise_level", 1.0 }, { "light_level", 0.4 }, { "sight_level", 0.4 }, { "crouching", false },
            { "room_id", "bridge" }, { "same_room", true }, { "detect_threshold", 0.85 },
            { "player_position", new Vec3(1.0, 0.0, 2.0) },
        };

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            var threat = MakeStalker();
            threat.Tick(0.1, Loud());
            GdDict summary = GdJson.ParseString(GdJson.Stringify(threat.GetSummary())) as GdDict;
            var restored = new ThreatAIState();
            Assert.IsTrue(restored.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(threat.GetSummary(), restored.GetSummary()));
            Assert.AreEqual(new Vec3(1.0, 0.0, 2.0), restored.LastKnownWorldPosition());
        }

        [Test]
        public void Fsm_MatchesSmoke()
        {
            var threat = MakeStalker();
            Assert.AreEqual(Vec3.Inf, threat.LastKnownWorldPosition());
            threat.Tick(0.1, Loud());
            Assert.AreEqual(ThreatAIState.STATE_ATTACK, threat.State);
            Assert.IsTrue(threat.CanAttack());
            threat.ConsumeAttack();
            Assert.IsFalse(threat.CanAttack());
            threat.Tick(0.6, new GdDict
            {
                { "noise_level", 0.0 }, { "light_level", 0.0 }, { "sight_level", 0.0 }, { "crouching", true },
                { "room_id", "bridge" }, { "same_room", false }, { "detect_threshold", 0.85 },
            });
            Assert.That(threat.State, Is.EqualTo(ThreatAIState.STATE_HUNT).Or.EqualTo(ThreatAIState.STATE_INVESTIGATE));
            Assert.AreEqual(threat.MoveSpeed * threat.HuntSpeedMult, threat.EffectiveMoveSpeed(), 1e-9);
            threat.ApplyDamage(new GdDict { { "final_damage", 50.0 } });
            Assert.AreEqual(ThreatAIState.STATE_DEAD, threat.State);
        }

        [Test]
        public void Telegraph_WindsUpBeforeAttack()
        {
            var threat = MakeStalker();
            threat.Configure(new GdDict { { "behavior", new GdDict { { "telegraph_seconds", 0.5 }, { "player_verb", "hide" } } } });
            threat.Tick(0.1, Loud());
            Assert.AreEqual(ThreatAIState.STATE_TELEGRAPH, threat.State);
            threat.Tick(0.5, Loud());
            Assert.AreEqual(ThreatAIState.STATE_ATTACK, threat.State);
            Assert.AreEqual("hide", threat.GetPlayerVerb());
        }
    }

    public class ThreatPathfinderTests
    {
        static ShipNavGraph Corridor()
        {
            var graph = new ShipNavGraph();
            var layout = new GdDict
            {
                { "cell_size", 4.0 },
                { "deck_height", 4.0 },
                {
                    "rooms", GdArray.Of(
                        new GdDict
                        {
                            { "id", "a" },
                            {
                                "structural_placements", GdArray.Of(
                                    new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(0.0, 0.0, 0.0) } },
                                    new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(4.0, 0.0, 0.0) } },
                                    new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(8.0, 0.0, 0.0) } })
                            },
                        },
                        new GdDict
                        {
                            { "id", "b" },
                            { "structural_placements", GdArray.Of(new GdDict { { "module", "floor_1x1" }, { "world_position", GdArray.Of(8.0, 0.0, 4.0) } }) },
                        })
                },
            };
            Assert.GreaterOrEqual(graph.BuildFromLayout(layout), 4);
            return graph;
        }

        [Test]
        public void FindPath_StepAndFlee_MatchSmoke()
        {
            ShipNavGraph graph = Corridor();
            GdArray path = ThreatPathfinder.FindPath(graph, new Vec3(0, 0, 0), new Vec3(8, 0, 4));
            Assert.AreEqual(4, path.Count);
            Assert.AreEqual(new Vec3(8, 0, 4), (Vec3)path[3]);
            var p0 = (Vec3)path[0];
            GdDict step = ThreatPathfinder.StepAlongPath(path, 0, p0, 4.0, 0.5);
            var moved = (Vec3)step["position"];
            Assert.AreEqual(2.0, moved.DistanceTo(p0), 1e-5);
            Assert.AreEqual(1, step.GetInt("path_index"));
            Assert.IsFalse(step.GetBool("arrived"));
            Vec3 flee = ThreatPathfinder.FarthestPoint(graph, new Vec3(0, 0, 0), new Vec3(0, 0, 0));
            Assert.AreEqual(new Vec3(8, 0, 4), flee);
        }

        [Test]
        public void EmptyGraphAndArrival()
        {
            Assert.AreEqual(0, ThreatPathfinder.FindPath(new ShipNavGraph(), Vec3.Zero, Vec3.One).Count);
            GdDict done = ThreatPathfinder.StepAlongPath(GdArray.Of(GdArray.Of(1.0, 0.0, 0.0)), 0, Vec3.Zero, 10.0, 1.0);
            Assert.IsTrue(done.GetBool("arrived"));
            Assert.AreEqual(new Vec3(1, 0, 0), (Vec3)done["position"]);
        }
    }
}
