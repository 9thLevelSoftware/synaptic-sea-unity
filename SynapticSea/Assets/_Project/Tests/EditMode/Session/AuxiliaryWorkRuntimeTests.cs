using System;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
    // Pure shadow arithmetic/admission fixtures, not a live work runtime or resource-lease proof.
    public sealed class AuxiliaryWorkRuntimeTests : InfraDataTestBase
    {
        static AuxiliaryWorkRuntime Kernel(double progress = 0, double eligible = 0, double stamina = 100, long steps = 0, int capacity = 256, double duration = 12)
            => new AuxiliaryWorkRuntime("ship-home", "run", "actor", "maintenance_fabricator_feed_01", 7, PaidHashContext.BitsV2.Algorithm,
                duration, progress, eligible, stamina, steps, capacity);
        static void Bits(double a, double b) => Assert.AreEqual(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
        static void Same(AuxiliaryWorkSnapshot a, AuxiliaryWorkSnapshot b)
        { Bits(a.ProgressSeconds, b.ProgressSeconds); Bits(a.EligibleSeconds, b.EligibleSeconds); Bits(a.LastAcceptedStamina, b.LastAcceptedStamina); Assert.AreEqual(a.EligibleSteps, b.EligibleSteps); }
        [Test]
        public void EveryAcceptedFrameMatchesLegacyFormulaInExactOperationOrder()
        {
            int cases = 0;
            foreach(double stamina in new[]{.01,.5,7.0,25.0,100.0,150.0})
            foreach(double maximum in new[]{0.0,1.0,100.0,200.0})
            foreach(double wound in new[]{.15,.5,.875,1.0})
            foreach(double delta in new[]{BitConverter.Int64BitsToDouble(0x3fa81f8b6a300d00L),1.0/60.0,10.0,200.0})
            foreach(double progress in new[]{0.0,11.99})
            {
                var kernel = Kernel(progress, progress + .125, stamina, 3);
                // This intentionally restates the old live formula, without using the new step helper.
                double ratio = Math.Max(0, Math.Min(1, stamina / Math.Max(1, maximum)));
                double speed = wound * (.35 + .65 * ratio);
                double remaining = 12 - progress;
                double elapsed = Math.Min(delta, Math.Min(remaining / speed, stamina / 8));
                double advance = Math.Min(remaining, elapsed * speed);
                double after = Math.Max(0, stamina - 8 * elapsed);
                var result = kernel.Step(new AuxiliaryWorkFrame(delta, stamina, maximum, wound));
                Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, result.Status);
                var step = result.AcceptedStep;
                Bits(elapsed, step.ElapsedSeconds); Bits(advance, step.DeltaSeconds); Bits(speed, step.Speed);
                Bits(after, result.StaminaAfter); Bits(Math.Min(12, progress + advance), result.Snapshot.ProgressSeconds);
                Bits((progress + .125) + elapsed, result.Snapshot.EligibleSeconds); Assert.AreEqual(4, result.Snapshot.EligibleSteps);
                Assert.AreEqual(1, kernel.RetainedStepCount); cases++;
            }
            Assert.AreEqual(768, cases);
        }
        [Test]
        public void LegacyApplyProgressProducesSameBitsAsSequentialShadowSteps()
        {
            const string id = "maintenance_fabricator_feed_01";
            var descriptors = AuxiliaryServiceState.Descriptors(CatalogRegistry.LoadDict(AuxiliaryServiceState.SourcePath).GetArrayOrEmpty("auxiliary_services"));
            var state = AuxiliaryServiceState.New("run", "actor", descriptors, PaidHashContext.BitsV2);
            double duration = descriptors.GetDictOrEmpty(id).GetFloat("required_seconds");
            state["job"] = new GdDict {{"service_id", id}, {"progress_seconds", 0.0}, {"eligible_seconds", 0.0}, {"status", "running"}, {"resume_required", false}, {"reason", ""}};
            var inventory = new InventoryState();
            var domain = new GdDict {{"schema_version", 6L}, {"feature_schema", 5L}, {"hash_algorithm", PaidHashContext.BitsV2.Algorithm},
                {"receipts", new GdDict()}, {"participating_state", new GdDict {{"auxiliary_services", state}, {"inventory", inventory.GetSummary()}, {"progression", new GdDict()}, {"training", new GdDict()}}}};
            var kernel = Kernel(duration: duration);
            double stamina = 100;
            foreach(double delta in new[]{1.0/60.0,.04711566611515572,.5,2.0,.12345678901234567})
            {
                var before = kernel.Snapshot();
                var result = kernel.Step(new AuxiliaryWorkFrame(delta, stamina, 100, .875));
                Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, result.Status);
                var step = result.AcceptedStep;
                var effect = AuxiliaryServiceState.Apply(domain, "aux_progress", id, step.DeltaSeconds, step.ElapsedSeconds, step.Speed, stamina, step.StaminaAfter);
                var job = AuxiliaryServiceState.State(domain).GetDictOrEmpty("job");
                Bits(job.GetFloat("progress_seconds"), result.Snapshot.ProgressSeconds);
                Bits(job.GetFloat("eligible_seconds"), result.Snapshot.EligibleSeconds);
                Bits(effect.GetFloat("stamina_after"), result.StaminaAfter);
                Bits(before.ProgressSeconds, effect.GetDictOrEmpty("job_before").GetFloat("progress_seconds"));
                // Retain a minimal pure Apply witness so its existing cumulative eligible_steps lookup sees this step.
                domain.GetDictOrEmpty("receipts")["step"] = new GdDict {{"commit_id", "step"}, {"revision", result.Snapshot.EligibleSteps}, {"result", effect}};
                Assert.AreEqual(result.Snapshot.EligibleSteps, effect.GetInt("eligible_steps"));
                stamina = result.StaminaAfter;
            }
        }
        [Test]
        public void GatePriorityAndHeldReleasePreserveProgressStaminaAndAcceptedHistory()
        {
            var kernel = Kernel(); Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, kernel.Step(new AuxiliaryWorkFrame(.1, 100, 100, 1)).Status);
            var before = kernel.Snapshot();
            var invalid = kernel.Step(new AuxiliaryWorkFrame(double.NaN, 50, 100, 1, "no_line_of_sight", false, true, false, true, true));
            Assert.AreEqual(AuxiliaryWorkStepStatus.IgnoredDelta, invalid.Status);
            var gate = kernel.Step(new AuxiliaryWorkFrame(.1, 50, 100, 1, "no_line_of_sight", false, true, false, true, true));
            Assert.AreEqual("damage", gate.Reason);
            Assert.AreEqual("moving", kernel.Step(new AuxiliaryWorkFrame(.1, 50, 100, 1, consent:false, moving:true)).Reason);
            Assert.AreEqual("explicit_resume_required", kernel.Step(new AuxiliaryWorkFrame(.1, 50, 100, 1, consent:false)).Reason);
            Assert.AreEqual("no_line_of_sight", kernel.Step(new AuxiliaryWorkFrame(.1, 50, 100, 1, "no_line_of_sight")).Reason);
            var released = kernel.Step(new AuxiliaryWorkFrame(.1, 50, 100, 1, held:false));
            Assert.AreEqual(AuxiliaryWorkStepStatus.HeldReleased, released.Status);Assert.AreEqual("", released.Reason);
            Bits(50, released.StaminaAfter); Same(before, kernel.Snapshot());Assert.AreEqual(1, kernel.RetainedStepCount);
            var resumed = kernel.Step(new AuxiliaryWorkFrame(.1, 50, 100, 1)); Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, resumed.Status);
            Bits(before.ProgressSeconds, resumed.AcceptedStep.ProgressBefore); Bits(50, resumed.StaminaBefore);
        }
        [Test]
        public void CompletionAndExhaustionDoNotInventRewardsOrExtraSteps()
        {
            var completed = Kernel(progress: 12, eligible: 12);
            Assert.AreEqual(AuxiliaryWorkStepStatus.HeldReleased, completed.Step(new AuxiliaryWorkFrame(1, 100, 100, 1, held:false)).Status);
            Assert.AreEqual(AuxiliaryWorkStepStatus.CompletionReady, completed.Step(new AuxiliaryWorkFrame(1, 100, 100, 1)).Status);
            Assert.AreEqual(0, completed.RetainedStepCount);
            var exhausted = Kernel(stamina: 0);
            Assert.AreEqual(AuxiliaryWorkStepStatus.Exhausted, exhausted.Step(new AuxiliaryWorkFrame(.1, 0, 100, 1)).Status);
            Bits(0, exhausted.Snapshot().ProgressSeconds);Assert.AreEqual(0, exhausted.RetainedStepCount);
            var clipped = Kernel(progress: 11.99, eligible: 11.99);
            var result = clipped.Step(new AuxiliaryWorkFrame(100, 100, 100, 1));
            Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, result.Status);Bits(12, result.Snapshot.ProgressSeconds);
            Assert.AreEqual(AuxiliaryWorkStepStatus.CompletionReady, clipped.Step(new AuxiliaryWorkFrame(.1, result.StaminaAfter, 100, 1)).Status);
        }
        [Test]
        public void FiniteAdmissionAndBoundedBackpressureNeverRefundOrLoseAcceptedWork()
        {
            var kernel = Kernel(capacity: 2);
            var first = kernel.Step(new AuxiliaryWorkFrame(.1, 100, 100, 1));
            var second = kernel.Step(new AuxiliaryWorkFrame(.1, first.StaminaAfter, 100, .5));
            Assert.AreEqual(AuxiliaryWorkStepStatus.Accepted, second.Status);
            var before = kernel.Snapshot(); var blocked = kernel.Step(new AuxiliaryWorkFrame(.1, second.StaminaAfter, 100, 1));
            Assert.AreEqual(AuxiliaryWorkStepStatus.Backpressure, blocked.Status);Bits(second.StaminaAfter, blocked.StaminaAfter);Same(before, kernel.Snapshot());
            var rows = kernel.CopyAcceptedSteps(); Assert.AreEqual(2, rows.Length);Assert.AreEqual(1, rows[0].Sequence);Assert.AreEqual(2, rows[1].Sequence);
            rows[0] = null; Assert.IsNotNull(kernel.CopyAcceptedSteps()[0]);
            var empty = Kernel(); var original = empty.Snapshot();
            foreach(var frame in new[]{new AuxiliaryWorkFrame(.1,double.NaN,100,1),new AuxiliaryWorkFrame(.1,100,double.PositiveInfinity,1),new AuxiliaryWorkFrame(.1,100,100,double.NaN),new AuxiliaryWorkFrame(.1,100,100,2)})
            { Assert.AreEqual(AuxiliaryWorkStepStatus.Refused, empty.Step(frame).Status);Same(original,empty.Snapshot()); }
            var overflow = Kernel(eligible: double.MaxValue, duration: double.MaxValue);Assert.AreEqual(AuxiliaryWorkStepStatus.Refused, overflow.Step(new AuxiliaryWorkFrame(double.MaxValue,double.MaxValue,double.MaxValue,1)).Status);
            var sequence = Kernel(steps: long.MaxValue);Assert.AreEqual("sequence_overflow", sequence.Step(new AuxiliaryWorkFrame(.1,100,100,1)).Reason);
            Assert.Throws<ArgumentException>(()=>Kernel(capacity:0));Assert.Throws<ArgumentException>(()=>Kernel(capacity:257));Assert.Throws<ArgumentException>(()=>Kernel(progress:double.NaN));Assert.Throws<ArgumentException>(()=>Kernel(progress:13));Assert.Throws<ArgumentException>(()=>Kernel(progress:1,eligible:.5));
        }
    }
}
