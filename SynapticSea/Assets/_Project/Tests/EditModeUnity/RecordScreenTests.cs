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

        [UnityTest]
        public IEnumerator RunResultsRowsAre44pxWithSeverityBannerAndEpitaph()
        {
            using (var harness = new UiHarness())
            {
                yield return null;
                var panel = new RunResultsPanel();
                harness.Mount(panel);
                panel.SetRunSummary(new GdDict { { "reason", "death" }, { "cause", "suffocation" }, { "play_time_seconds", 754.0 }, { "threats_killed", 3L } });
                panel.SetContextLine("seed 17 · breach_field · standard");
                harness.Layout();
                Assert.AreEqual(4, panel.StatRows.Count, "outcome, cause, time survived, threats killed");
                foreach (var row in panel.StatRows)
                    Assert.GreaterOrEqual(row.layout.height, 44f, "results rows are 44 px");
                Assert.GreaterOrEqual(panel.ReturnButton.layout.height, 44f);
                Assert.IsTrue(panel.Banner.ClassListContains(UiClasses.SevDanger), "death banner is danger");
                StringAssert.Contains("Danger", panel.Banner.text, "severity carries wording, not hue alone");
                Assert.IsTrue(UiFactory.IsShown(panel.EpitaphLabel), "death shows the epitaph");
                Assert.IsFalse(UiFactory.IsShown(panel.EmptyLabel));
                Assert.IsTrue(UiFactory.IsShown(panel.ContextLabel));
                Assert.AreEqual("seed 17 · breach_field · standard", panel.ContextLabel.text);

                panel.SetRunSummary(new GdDict { { "reason", "extraction" } });
                harness.Layout();
                Assert.IsTrue(panel.Banner.ClassListContains(UiClasses.SevSuccess), "extraction banner is success");
                Assert.IsFalse(panel.Banner.ClassListContains(UiClasses.SevDanger));
                Assert.IsFalse(UiFactory.IsShown(panel.EpitaphLabel), "no epitaph without a death");
                Assert.IsTrue(UiFactory.IsShown(panel.EmptyLabel), "no tracked stats says so");
                Assert.AreEqual(1, panel.StatRows.Count);
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
