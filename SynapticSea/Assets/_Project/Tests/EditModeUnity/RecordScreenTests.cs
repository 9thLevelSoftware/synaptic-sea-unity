using System.Collections;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.Unity
{
    public class RecordScreenTests : UiTestBase
    {
        [Test]
        public void RunResultsReportsOnlyTrackedValuesWithAnEpitaph()
        {
            var panel = new RunResultsPanel();
            int toTitle = 0, newRun = 0;
            panel.ReturnToTitleRequested += () => toTitle++;
            panel.NewRunRequested += () => newRun++;
            panel.SetRunSummary(new GdDict { { "reason", "death" }, { "cause", "suffocation" }, { "play_time_seconds", 754.0 }, { "threats_killed", 3L } });
            Assert.AreEqual("death", panel.NormalizedOutcome());
            StringAssert.Contains("Outcome: death", panel.BodyText);
            StringAssert.Contains("Cause: suffocation", panel.BodyText);
            StringAssert.Contains("Epitaph: ", panel.BodyText);
            StringAssert.Contains("Time survived: 12:34", panel.BodyText);
            StringAssert.Contains("Threats killed: 3", panel.BodyText);
            StringAssert.DoesNotContain("Loot value", panel.BodyText, "untracked stats are never invented");
            Assert.AreEqual(SurfaceTime.Terminal, panel.Time);
            panel.Consume(UiCommand.Cancel);
            Assert.AreEqual(1, toTitle, "Cancel returns to title");
            panel.SetRunSummary(new GdDict { { "outcome", "complete" } });
            Assert.AreEqual("extraction", panel.NormalizedOutcome());
            StringAssert.Contains("No additional run statistics were recorded.", panel.BodyText);
            Assert.AreEqual(0, newRun);
        }

        [UnityTest]
        public IEnumerator RunResultsInitialFocusIsReturnToTitle()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                var panel = new RunResultsPanel();
                harness.Mount(panel);
                panel.SetRunSummary(new GdDict { { "reason", "extraction" } });
                panel.RestoreFocus("");
                Assert.AreSame(panel.ReturnButton, harness.Focused);
            }
        }

        [Test]
        public void AchievementsRenderUnlockedWithWordingAndSymbol()
        {
            var state = new AchievementState(Storage, new ManualClock());
            state.Configure(CatalogRegistry.LoadDict(AchievementsPanel.CatalogPath));
            string first = SynapticSea.Core.Variant.V.Str(state.GetCatalogIds()[0]);
            state.Unlock(first);
            var panel = new AchievementsPanel();
            Assert.Greater(panel.LoadCatalog(), 0);
            panel.SetState(state);
            panel.Render();
            Assert.AreEqual(1, panel.GetUnlockedCount());
            StringAssert.StartsWith("[X] ", panel.RenderedText);
            SelectableList.Item row = panel.List.Items.First(i => i.Id == first);
            Assert.AreEqual("✓", row.Chip);
            StringAssert.EndsWith("— Unlocked", row.Text);
            StringAssert.Contains("1 / " + panel.GetTotalCount() + " unlocked", panel.SummaryText);
        }

        [Test]
        public void CreditsAppendTheTypographyOverlayWithoutEditingCreditsJson()
        {
            var screen = new CreditsScreen();
            int n = screen.LoadCatalog("{\"credits\": [{\"role\": \"Design\", \"name\": \"Someone\"}, {\"role\": \"\", \"name\": \"skipped\"}]}");
            Assert.AreEqual(1 + CreditsOverlay.Entries.Count, n);
            Assert.AreEqual("Typography", screen.GetEntries()[1].GetString("role"));
            StringAssert.Contains("SIL Open Font License 1.1", string.Join("\n", screen.VisibleTexts()));
            bool dismissed = false;
            screen.CreditsDismissed += () => dismissed = true;
            screen.Consume(UiCommand.Cancel);
            Assert.IsTrue(dismissed);
        }

        [Test]
        public void ReleaseBadgeKinds()
        {
            var badge = new ReleaseBadgeOverlay();
            Assert.AreEqual("DEV", badge.GetBadgeText());
            var meta = new BuildMetadataState();
            meta.Configure(new GdDict { { "version", "v0.2.0" }, { "build_kind", "release" }, { "store", "itch" } });
            int changed = 0;
            badge.MetadataChanged += () => changed++;
            badge.SetMetadata(meta);
            Assert.AreEqual(1, changed);
            Assert.AreEqual("RELEASE", badge.BadgeLabelText);
            Assert.AreEqual(ReleaseBadgeOverlay.ReleaseColor, badge.GetBadgeColor());
        }
    }
}
