using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.Unity
{
    public class HudVitalsClusterTests
    {
        [Test]
        public void PresentsTheVitalsModelWithTextAndSeverity()
        {
            var model = new PlayerVitalsModel();
            model.ApplyVitalsSummary(new GdDict { { "health", 22.0 }, { "stamina", 80.0 } });
            model.ApplyOxygenSummary(new GdDict { { "oxygen", 18.0 }, { "breach_open", true }, { "recovery_threshold", 30.0 } });
            model.ApplyInventoryLoad(1.2, 0.8);

            var hud = new HudVitalsCluster();
            hud.Refresh(model);

            Assert.AreEqual("22", hud.Health.ValueText);
            Assert.AreEqual(Meter.Severity.Danger, hud.Health.CurrentSeverity);
            Assert.AreEqual("18%", hud.Oxygen.ValueText);
            Assert.AreEqual(Meter.Severity.Danger, hud.Oxygen.CurrentSeverity);
            Assert.AreEqual(Meter.Severity.Normal, hud.Stamina.CurrentSeverity);
            CollectionAssert.IsSubsetOf(new[] { "BREACH", "O2 LOW", "OVERLOADED" }, hud.StatusChipTexts);
            Assert.AreEqual("BREACH", hud.StatusChipTexts[0], "danger states rank first");
        }

        [Test]
        public void CalmVitalsShowNoChipsAndHideTheWorkLine()
        {
            var hud = new HudVitalsCluster();
            hud.Refresh(new GdDict { { "health", 100L }, { "oxygen", 100L }, { "stamina", 100L }, { "sanity", 100L } });
            Assert.IsEmpty(hud.StatusChipTexts);
            Assert.AreEqual("", hud.WorkLine);
        }

        [Test]
        public void TextScaleIsAppliedByRootClass()
        {
            var go = new UnityEngine.GameObject("hud");
            try
            {
                var doc = go.AddComponent<UIDocument>();
                var root = go.AddComponent<HudRoot>();
                var tree = root.Build(new VisualElement());
                root.ApplyTextScale(HudRoot.TextScale.X150);
                Assert.IsTrue(tree.ClassListContains("scale-150"));
                root.ApplyTextScale(HudRoot.TextScale.X200);
                Assert.IsFalse(tree.ClassListContains("scale-150"));
                Assert.IsTrue(tree.ClassListContains("scale-200"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
