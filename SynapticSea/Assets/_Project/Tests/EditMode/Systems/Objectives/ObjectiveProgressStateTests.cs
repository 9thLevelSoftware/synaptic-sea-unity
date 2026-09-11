using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ObjectiveProgressStateTests
    {
        [Test]
        public void Steps_CompleteOnce_AndSequenceCompletes()
        {
            var model = new ObjectiveProgressState();
            model.RegisterObjective(2, "restore_systems", 2);
            Assert.AreEqual(2L, model.GetStepProgress(2)["required_steps"]);
            Assert.IsTrue(model.CompleteStep(2, "secondary_coupling"));
            Assert.IsFalse(model.IsSequenceComplete(2));
            Assert.IsFalse(model.CompleteStep(2, "secondary_coupling"));
            Assert.AreEqual(1L, model.GetStepProgress(2)["completed_steps"]);
            Assert.IsTrue(model.CompleteStep(2, "primary_coupling"));
            Assert.IsTrue(model.IsSequenceComplete(2));
            var seq = (GdDict)model.GetSummary()[2L];
            Assert.AreEqual("restore_systems", seq["objective_type"]);
            Assert.AreEqual(2, ((GdArray)seq["completed_step_ids"]).Count);
        }

        [Test]
        public void Summary_RoundTrips_ThroughJsonStringKeys()
        {
            var model = new ObjectiveProgressState();
            model.RegisterObjective(2, "restore_systems", 3);
            model.CompleteStep(2, "a");
            Assert.IsTrue(model.ApplyJunctionCalibrator(2));
            Assert.IsFalse(model.ApplyJunctionCalibrator(2));
            GdDict summary = model.GetSummary();
            var viaJson = (GdDict)GdJson.ParseString(GdJson.Stringify(summary));
            var restored = new ObjectiveProgressState();
            Assert.IsTrue(restored.ApplySummary(viaJson));
            Assert.IsTrue(V.VariantEquals(summary, restored.GetSummary()));
            Assert.IsTrue(restored.HasCalibratorApplied(2));
            Assert.IsFalse(restored.ApplySummary(restored.GetSummary()), "re-applying an identical summary changes nothing");
            Assert.IsFalse(restored.ApplySummary(new GdDict { { "abc", new GdDict() }, { "-3", new GdDict() } }));
        }

        [Test]
        public void Reconcile_KeepsCalibratorReduction()
        {
            var model = new ObjectiveProgressState();
            model.RegisterObjective(4, "repair_junction", 1);
            model.ApplySummary(new GdDict { { "4", new GdDict { { "required_steps", 3L }, { "calibrator_applied", true }, { "completed_step_ids", GdArray.Of("x") } } } });
            model.ReconcileObjective(4, "repair_junction", 3);
            GdDict progress = model.GetStepProgress(4);
            Assert.AreEqual(2L, progress["required_steps"]);
            Assert.AreEqual(1L, progress["completed_steps"]);
            Assert.IsFalse(V.Bool(progress["complete"]));
        }
    }
}
