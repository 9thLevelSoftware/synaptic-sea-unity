using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    /// <summary>Phase 1.1b: stage time domains, per-game-hour hunger/thirst, and the survival tuning file.</summary>
    public class TimeDomainTests
    {
        const double HoursToSeconds = 3600.0;
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

        static SessionHarness.Rig BootGameGolden()
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            rig.Session = RunSession.Create(deps);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            return rig;
        }

        static WorldClock ClockAt(double scale)
        {
            var clock = new WorldClock();
            clock.SetScale(scale);
            return clock;
        }

        static SurvivalTuning LoadedTuning()
        {
            GdDict file = CatalogRegistry.LoadDict(SurvivalTuning.Path);
            Assert.IsNotNull(file, SurvivalTuning.Path);
            return SurvivalTuning.FromDict(file);
        }

        // ------------------------------------------------------------------ stage domains

        [Test]
        public void OnlyFoodIsGameDomain_AndSurvivalAttritionIsMixed_EverythingElseIsReal()
        {
            foreach (ITickStage stage in TickOrder.Stages)
            {
                TimeDomain expected = stage.Id == TickOrder.Food ? TimeDomain.Game
                    : stage.Id == TickOrder.SurvivalAttrition ? TimeDomain.Mixed
                    : TimeDomain.Real;
                Assert.AreEqual(expected, stage.Domain, stage.Id);
            }
            foreach (string id in new[] { TickOrder.Oxygen, TickOrder.ActiveFire, TickOrder.ElectricalArc, TickOrder.Threat, TickOrder.Audio, TickOrder.WorkAction })
                Assert.AreEqual(TimeDomain.Real, TickOrder.Get(id).Domain, id + " hazards and actions stay in real time");
        }

        // ------------------------------------------------------------------ survival.json

        [Test]
        public void SurvivalFile_LoadsThroughTheCatalogAndMatchesTheDocumentedPacing()
        {
            var catalog = new TuningCatalog();
            Assert.IsTrue(catalog.LoadFile(SurvivalTuning.Path));
            Assert.Greater(catalog.LoadDefaults(), 1, "survival.json is part of the default balance files");
            SurvivalTuning t = LoadedTuning();
            Assert.AreEqual(24.0, 100.0 / t.HungerPerGameHour, 0.01, "hunger empties in about 24 game hours");
            Assert.AreEqual(12.0, 100.0 / t.ThirstPerGameHour, 0.01, "thirst empties in about 12 game hours");
            Assert.AreEqual(72.0, t.SpoilageMultiplier);
            Assert.AreEqual(120.0, t.ProductionMultiplier);
        }

        [Test]
        public void MissingOrInvalidValues_KeepTheDefaults()
        {
            var t = SurvivalTuning.FromDict(new GdDict { { "survival", new GdDict { { "hunger_per_game_hour", -3.0 }, { "thirst_per_game_hour", "x" } } } });
            Assert.AreEqual(SurvivalTuning.DefaultHungerPerGameHour, t.HungerPerGameHour);
            Assert.AreEqual(SurvivalTuning.DefaultThirstPerGameHour, t.ThirstPerGameHour);
            Assert.AreEqual(SurvivalTuning.DefaultSpoilageMultiplier, SurvivalTuning.FromDict(null).SpoilageMultiplier);
        }

        [Test]
        public void RealTimeClock_KeepsTheLegacyPerSecondRatesAndNoDivisors()
        {
            SurvivalTuning t = LoadedTuning();
            var vitals = new VitalsState();
            vitals.HungerDrainRate = 9.0;
            vitals.ThirstDrainRate = 9.0;
            t.ApplyTo(vitals, ClockAt(1.0));
            Assert.AreEqual(0.5, vitals.HungerDrainRate);
            Assert.AreEqual(0.8, vitals.ThirstDrainRate);
            Assert.AreEqual(1.0, t.SpoilageDivisor(ClockAt(1.0)));
            Assert.AreEqual(1.0, t.ProductionDivisor(ClockAt(1.0)));
        }

        // ------------------------------------------------------------------ hunger and thirst follow game time

        [Test]
        public void ScaledPacing_HungerAndThirstEmptyInGameHours()
        {
            SurvivalTuning t = LoadedTuning();
            WorldClock clock = ClockAt(60.0);
            var vitals = new VitalsState();
            t.ApplyTo(vitals, clock);
            double thirstEmptyAt = -1.0, hungerEmptyAt = -1.0;
            for (int second = 1; second <= 25 * 60; second++)
            {
                double game = clock.Advance(1.0);
                vitals.Tick(1.0, new GdDict { { SimKeys.GameDelta, game }, { SimKeys.Moving, false } });
                if (thirstEmptyAt < 0 && vitals.Thirst <= 0.0) thirstEmptyAt = clock.GameSeconds / HoursToSeconds;
                if (hungerEmptyAt < 0 && vitals.Hunger <= 0.0) hungerEmptyAt = clock.GameSeconds / HoursToSeconds;
            }
            Assert.AreEqual(12.0, thirstEmptyAt, 0.1, "thirst empties after about 12 game hours (12 real minutes)");
            Assert.AreEqual(24.0, hungerEmptyAt, 0.1, "hunger empties after about 24 game hours (24 real minutes)");
        }

        [Test]
        public void RealTimeClock_HungerMatchesTheLegacyNumbersSecondForSecond()
        {
            SurvivalTuning t = LoadedTuning();
            WorldClock clock = ClockAt(1.0);
            var scaled = new VitalsState();
            var legacy = new VitalsState();
            t.ApplyTo(scaled, clock);
            for (int i = 0; i < 100; i++)
            {
                double game = clock.Advance(0.25);
                scaled.Tick(0.25, new GdDict { { SimKeys.GameDelta, game } });
                legacy.Tick(0.25);
            }
            Assert.AreEqual(legacy.Hunger, scaled.Hunger, 0.0);
            Assert.AreEqual(legacy.Thirst, scaled.Thirst, 0.0);
            Assert.AreEqual(100.0 - 12.5, scaled.Hunger, 1e-9);
        }

        [Test]
        public void GameDelta_OnlyTouchesHungerAndThirst_StaminaAndHazardsStayReal()
        {
            var a = new VitalsState();
            var b = new VitalsState();
            var context = new GdDict { { SimKeys.Moving, true }, { SimKeys.FireHealthDrain, 3.0 }, { SimKeys.RadiationHealthDrain, 1.0 } };
            var scaledContext = (GdDict)context.DeepCopy();
            scaledContext[SimKeys.GameDelta] = 600.0;
            a.Tick(10.0, context);
            b.Tick(10.0, scaledContext);
            Assert.AreEqual(a.Stamina, b.Stamina, 0.0, "stamina drain follows the real delta");
            Assert.AreEqual(a.Health, b.Health, 0.0, "fire and radiation damage follow the real delta");
            Assert.Less(b.Hunger, a.Hunger, "hunger follows the game delta");
            Assert.Less(b.Thirst, a.Thirst, "thirst follows the game delta");
        }

        [Test]
        public void WithoutGameDelta_VitalsTickIsUnchanged()
        {
            var v = new VitalsState();
            v.Tick(1.0);
            Assert.AreEqual(99.5, v.Hunger, 1e-12);
            Assert.AreEqual(99.2, v.Thirst, 1e-12);
        }

        // ------------------------------------------------------------------ session wiring

        [Test]
        public void SessionAtScaleOne_UsesLegacyRates_AndScaledSessionUsesPerGameHourRates()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            Assert.AreEqual(0.5, s.VitalsState.HungerDrainRate);
            Assert.AreEqual(0.8, s.VitalsState.ThirstDrainRate);
            s.GameClock.SetScale(60.0);
            s.ApplySurvivalTuning();
            Assert.AreEqual(SurvivalTuning.DefaultHungerPerGameHour / HoursToSeconds, s.VitalsState.HungerDrainRate, 1e-9);
            Assert.AreEqual(SurvivalTuning.DefaultThirstPerGameHour / HoursToSeconds, s.VitalsState.ThirstDrainRate, 1e-9);
        }

        [Test]
        public void Hydroponics_FollowsGameTimeStretchedByTheProductionMultiplier()
        {
            foreach (double scale in new[] { 1.0, 60.0 })
            {
                SessionHarness.Rig rig = BootGameGolden();
                RunSession s = rig.Session;
                s.ThreatManager.Threats.Clear();
                s.GameClock.SetScale(scale);
                s.ApplySurvivalTuning();
                var crop = new GdDict { { "crop_id", "greens" }, { "growth_seconds", 120.0 }, { "produce_item_id", "hydroponic_greens" }, { "produce_quantity", 3L } };
                Assert.IsTrue(s.HydroponicsState.Plant(crop, 0, 10.0, 10.0).GetBool("ok"));
                for (int i = 0; i < 24; i++)
                {
                    rig.Clock.Advance(0.25);
                    s.Tick(TickContext.Frame(0.25, rig.Scene.PlayerPosition));
                }
                double expected = scale == 1.0 ? 6.0 : 6.0 * 60.0 / 120.0;
                Assert.AreEqual(expected, s.HydroponicsState.ProgressSeconds, 1e-6, "scale " + scale);
            }
        }

        [Test]
        public void ScaledClockText_ShowsDayAndHour_AndRealTimeShowsNothing()
        {
            var clock = ClockAt(60.0);
            Assert.AreEqual("Day 1  06:00", clock.ClockText());
            clock.Advance(60.0 * 8.5);
            Assert.AreEqual("Day 1  14:30", clock.ClockText());
            clock.Advance(60.0 * 12.0);
            Assert.AreEqual("Day 2  02:30", clock.ClockText());
            Assert.AreEqual("", ClockAt(1.0).ClockText());
        }

        // ------------------------------------------------------------------ Continue

        [Test]
        public void Continue_ReappliesConfigRates_OverRatesRestoredFromTheSave()
        {
            var rig = SessionHarness.CreateGolden();
            RunSession s = rig.Session;
            s.GameClock.SetScale(60.0);
            s.ApplySurvivalTuning();
            RunSnapshot snapshot = RunSnapshotAssembler.Build(s);
            snapshot.VitalsSummary["hunger_drain_rate"] = 9.9;
            snapshot.VitalsSummary["thirst_drain_rate"] = 9.9;
            var roundTrip = RunSnapshot.FromDict(snapshot.ToDict(), snapshot.SliceVersion, snapshot.GodotVersion);

            var loaded = SessionHarness.CreateGolden();
            Assert.AreEqual(0.5, loaded.Session.VitalsState.HungerDrainRate, "a fresh session starts at real-time pacing");
            Assert.IsTrue(RunSnapshotAssembler.Apply(loaded.Session, roundTrip));
            Assert.AreEqual(60.0, loaded.Session.GameClock.Scale);
            Assert.AreEqual(SurvivalTuning.DefaultHungerPerGameHour / HoursToSeconds, loaded.Session.VitalsState.HungerDrainRate, 1e-9, "config wins over the saved rate");
            Assert.AreEqual(SurvivalTuning.DefaultThirstPerGameHour / HoursToSeconds, loaded.Session.VitalsState.ThirstDrainRate, 1e-9);
        }

        [Test]
        public void Continue_OfARealTimeSave_KeepsTheLegacyRates()
        {
            var rig = SessionHarness.CreateGolden();
            RunSnapshot snapshot = RunSnapshotAssembler.Build(rig.Session);
            var roundTrip = RunSnapshot.FromDict(snapshot.ToDict(), snapshot.SliceVersion, snapshot.GodotVersion);
            var loaded = SessionHarness.CreateGolden();
            Assert.IsTrue(RunSnapshotAssembler.Apply(loaded.Session, roundTrip));
            Assert.AreEqual(1.0, loaded.Session.GameClock.Scale);
            Assert.AreEqual(0.5, loaded.Session.VitalsState.HungerDrainRate);
            Assert.AreEqual(0.8, loaded.Session.VitalsState.ThirstDrainRate);
        }
    }
}
