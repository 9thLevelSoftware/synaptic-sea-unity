using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// HUD zoning acceptance (ui_presentation_program.md "Persistent HUD and disclosure") measured on a REAL UI Toolkit
    /// layout: the HUD tree is mounted in a RenderTexture-backed runtime panel using the project PanelSettings/theme,
    /// styles and layout are resolved, and the union of visible backed regions is measured.
    /// </summary>
    public class HudLayoutTests : UiTestBase
    {
        static readonly (int w, int h)[] Resolutions = { (1280, 720), (1920, 1080), (1280, 800) };

        [Test]
        public void ObjectiveChipShowsOneActionableLine()
        {
            var chip = new ObjectiveChip();
            string prompt = null;
            chip.PromptChanged += p => prompt = p;
            chip.SetObjectives(GdArray.Of(
                new GdDict { { "sequence", 1L }, { "type", "restore_systems" }, { "kind", "repair_junction" }, { "room_id", "engine_room" } },
                new GdDict { { "sequence", 2L }, { "type", "reach_extraction" }, { "room_id", "docking_bay" } }));
            chip.SetStepProgress(1, new GdDict { { "required_steps", 2L }, { "completed_steps", 1L } });
            Assert.AreEqual("01 Repair junction (1/2) @ Engine Room", chip.ChipText);
            Assert.AreEqual("0/2", chip.ProgressText);
            chip.MarkCompleted(1);
            chip.SetCurrentSequence(2);
            chip.SetStepProgress(2, new GdDict { { "required_steps", 1L }, { "completed_steps", 0L } });
            Assert.AreEqual("02 Reach Extraction @ Docking Bay", chip.ChipText);
            chip.SetInteractionPrompt("[E] Open hatch");
            Assert.AreEqual("[E] Open hatch", prompt, "the prompt goes to the transient context slot, not the chip");
            StringAssert.Contains(ObjectiveChip.ControlsLine, chip.GetHudText(), "controls/systems stay behind disclosure");
            chip.MarkRunComplete();
            StringAssert.StartsWith("COMPLETE", chip.ChipText);
            Assert.IsTrue(chip.ClassListContains(UiClasses.SevSuccess));
        }

        [Test]
        public void WorkStripAppearsOnlyWhileActiveAndWordsBlockers()
        {
            var work = new WorkActionStrip();
            Assert.IsFalse(UiFactory.IsShown(work));
            work.SetWorkState(new GdDict { { "action_id", "cut_panel" }, { "target_id", "hull_04" }, { "verb", "cut" }, { "progress", 0.46 }, { "status", "active" }, { "noise", 0.3 } });
            Assert.IsTrue(UiFactory.IsShown(work));
            Assert.AreEqual("CUT · hull_04", work.TitleText);
            Assert.AreEqual("46%", work.ProgressMeter.ValueText);
            CollectionAssert.AreEqual(new[] { "CUT (cut_panel)", "Target: hull_04", "Status: active", "Progress: [####------] 46%", "Noise: 0.30", WorkActionStrip.DefaultHint }, work.GetStatusLines());
            work.SetWorkState(new GdDict { { "status", "blocked" }, { "verb", "cut" } });
            StringAssert.StartsWith("▲ Caution: blocked", work.StateText);
            work.SetWorkState(new GdDict { { "status", "idle" } });
            Assert.IsFalse(UiFactory.IsShown(work));
            Assert.IsEmpty(work.GetStatusLines());
        }

        sealed class Hud : IDisposable
        {
            public readonly UiHarness Harness;
            public readonly HudRoot Root;
            readonly GameObject _go;

            public Hud(int w, int h)
            {
                Harness = new UiHarness(w, h, UiHarness.HudPanelSettings);
                _go = new GameObject("hud");
                _go.AddComponent<UIDocument>();
                Root = _go.AddComponent<HudRoot>();
            }

            public void Dispose()
            {
                Object.DestroyImmediate(_go);
                Harness.Dispose();
            }
        }

        /// <summary>Worst-case persistent HUD: every urgent chip, repair line, five quick-use slots, a long objective.</summary>
        static void FillPersistent(HudRoot hud)
        {
            var vitals = new PlayerVitalsModel();
            vitals.ApplyVitalsSummary(new GdDict { { "health", 18.0 }, { "stamina", 12.0 } });
            vitals.ApplyOxygenSummary(new GdDict { { "oxygen", 14.0 }, { "breach_open", true }, { "recovery_threshold", 30.0 } });
            vitals.ApplyInventoryLoad(1.3, 0.6);
            vitals.ApplyRadiationSummary(new GdDict { { "radiation", 62.0 } });
            vitals.ApplySanitySummary(new GdDict { { "sanity", 20.0 } });
            vitals.SetRepairProgress(true, 0.4);
            hud.Vitals.Refresh(vitals);
            var hotbar = new HotbarStrip();
            hotbar.SetSlots(new[] { "Medkit", "Ration Pack", "Sealant", "(empty)", "(empty)" }, 0, "[E]");
            hud.Vitals.QuickUseSlot.Add(hotbar);
            hud.Objective.SetObjectives(GdArray.Of(new GdDict { { "sequence", 1L }, { "type", "restore_systems" }, { "kind", "repair_junction" }, { "room_id", "auxiliary_power_distribution_room" } }));
            hud.Objective.SetStepProgress(1, new GdDict { { "required_steps", 3L }, { "completed_steps", 1L } });
            hud.SetContextPrompt("");
        }

        static void FillTransients(HudRoot hud)
        {
            hud.SetContextPrompt("[E] Pry open the jammed hatch");
            hud.Work.SetWorkState(new GdDict { { "verb", "pry" }, { "target_id", "hatch_03" }, { "progress", 0.3 }, { "status", "active" }, { "noise", 0.4 } });
            var tooltip = new TooltipCard();
            tooltip.SetPayload("Jammed hatch", "Needs leverage.", "[E] Pry");
            hud.AddTransient(tooltip, HudRoot.PriorityTooltip);
            var tutorial = new TutorialBanner();
            tutorial.ShowTutorial("Oxygen", "Your suit supply drains faster while the room is venting.");
            hud.AddTransient(tutorial, HudRoot.PriorityTutorial);
            hud.RefreshTransients();
        }

        static List<Rect> BackedRects(VisualElement root) =>
            root.Query<VisualElement>().Where(e => (e.ClassListContains(UiClasses.Panel) || e.ClassListContains("hud-context-prompt")) && Displayed(e))
                .ToList().Select(e => e.worldBound).Where(r => r.width > 0 && r.height > 0).ToList();

        static bool Displayed(VisualElement el)
        {
            for (VisualElement e = el; e != null; e = e.parent)
            {
                if (e.resolvedStyle.display == DisplayStyle.None) return false;
            }
            return true;
        }

        /// <summary>Area of the union of rectangles (coordinate compression; overlaps are not double-counted).</summary>
        static double UnionArea(List<Rect> rects, Rect viewport)
        {
            var clipped = rects.Select(r => Rect.MinMaxRect(Mathf.Max(r.xMin, viewport.xMin), Mathf.Max(r.yMin, viewport.yMin),
                Mathf.Min(r.xMax, viewport.xMax), Mathf.Min(r.yMax, viewport.yMax))).Where(r => r.width > 0 && r.height > 0).ToList();
            var xs = clipped.SelectMany(r => new[] { r.xMin, r.xMax }).Distinct().OrderBy(x => x).ToList();
            var ys = clipped.SelectMany(r => new[] { r.yMin, r.yMax }).Distinct().OrderBy(y => y).ToList();
            double area = 0;
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                for (int j = 0; j + 1 < ys.Count; j++)
                {
                    float cx = (xs[i] + xs[i + 1]) / 2f, cy = (ys[j] + ys[j + 1]) / 2f;
                    if (clipped.Any(r => r.Contains(new Vector2(cx, cy)))) area += (xs[i + 1] - xs[i]) * (double)(ys[j + 1] - ys[j]);
                }
            }
            return area;
        }

        static Rect Protected(float w, float h) => Rect.MinMaxRect(0.30f * w, 0.275f * h, 0.70f * w, 0.725f * h);
        static Rect Corridor(float w, float h) => Rect.MinMaxRect(0.35f * w, 0.70f * h, 0.65f * w, h);

        [UnityTest]
        public IEnumerator HudCoverageAndProtectedZonesAtEveryTextScale()
        {
            var report = new List<string>();
            var failures = new List<string>();
            foreach ((int w, int h) in Resolutions)
            {
                foreach (HudRoot.TextScale scale in new[] { HudRoot.TextScale.X100, HudRoot.TextScale.X150, HudRoot.TextScale.X200 })
                {
                    using (var hud = new Hud(w, h))
                    {
                        yield return null;
                        hud.Root.Build(hud.Harness.Root);
                        FillPersistent(hud.Root);
                        hud.Root.ApplyTextScale(scale);
                        hud.Harness.Layout();
                        var viewport = new Rect(0, 0, w, h);
                        Assert.AreEqual(w, hud.Harness.Root.layout.width, 0.5f, "harness panel size");

                        List<Rect> persistent = BackedRects(hud.Root.Root);
                        Assert.GreaterOrEqual(persistent.Count, 2, "chip + cluster are measured");
                        double coverage = UnionArea(persistent, viewport) / (w * (double)h);
                        double target = scale == HudRoot.TextScale.X100 ? 0.20 : 0.30;

                        FillTransients(hud.Root);
                        hud.Harness.Layout();
                        List<Rect> withTransients = BackedRects(hud.Root.Root);
                        double peak = UnionArea(withTransients, viewport) / (w * (double)h);
                        double ceiling = scale == HudRoot.TextScale.X100 ? 0.25 : 0.35;
                        string label = $"{w}x{h} {scale}";
                        report.Add($"{label}: persistent {coverage:P1} (target {target:P0}), with transients {peak:P1} (ceiling {ceiling:P0})");
                        TestContext.WriteLine(report[report.Count - 1]);

                        void Check(bool ok, string message)
                        {
                            if (!ok) failures.Add(label + ": " + message);
                        }

                        Check(coverage <= target, $"persistent HUD coverage {coverage:P1} > {target:P0}");
                        Check(peak <= ceiling, $"coverage with transients {peak:P1} > {ceiling:P0}");
                        string dump = string.Join("; ", hud.Root.LeftColumn.Query<VisualElement>().ToList().Where(Displayed)
                            .Where(e => e.parent == hud.Root.LeftColumn || e.parent == hud.Root.Vitals || e.parent == hud.Root.Transients)
                            .Select(e => (e.name.Length != 0 ? e.name : string.Join(".", e.GetClasses())) + "=" + e.worldBound));
                        TestContext.WriteLine(label + " " + dump);
                        int shownTransients = hud.Root.Transients.Children().Count(Displayed);
                        Check(shownTransients <= hud.Root.TransientLimit, "transient disclosure limit");
                        Check(Displayed(hud.Root.Work), "active work (highest priority) stays visible");
                        Check(!hud.Root.Objective.worldBound.Overlaps(hud.Root.LeftColumn.worldBound),
                            $"objective chip {hud.Root.Objective.worldBound} overlaps the lower-left column {hud.Root.LeftColumn.worldBound}");
                        foreach (Rect r in withTransients)
                        {
                            Check(!r.Overlaps(Protected(w, h)), $"{r} enters the protected centre");
                            Check(!r.Overlaps(Corridor(w, h)), $"{r} enters the lower-middle corridor");
                            Check(r.yMin >= -0.5f, $"{r} is clipped off the top");
                            Check(r.yMax <= h + 0.5f, $"{r} is clipped off the bottom");
                        }

                        float minBody = scale == HudRoot.TextScale.X100 ? 18f : scale == HudRoot.TextScale.X150 ? 27f : 36f;
                        float minMeta = scale == HudRoot.TextScale.X100 ? 16f : scale == HudRoot.TextScale.X150 ? 24f : 32f;
                        foreach (Label l in hud.Root.Root.Query<Label>().ToList().Where(Displayed))
                        {
                            bool meta = l.ClassListContains(UiClasses.LabelSecondary) || l.ClassListContains("hud-status-chip") || l.ClassListContains("hud-hotbar__slot") || l.ClassListContains(UiClasses.Glyph);
                            Check(l.resolvedStyle.fontSize >= (meta ? minMeta : minBody), $"'{l.text}' text size {l.resolvedStyle.fontSize}");
                        }
                    }
                }
            }
            TestContext.WriteLine(string.Join("\n", report));
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [UnityTest]
        public IEnumerator InspectionSurfaceLeavesTheSurvivalClusterVisibleAndRowsAre44px()
        {
            using (var hud = new Hud(1280, 720))
            using (var menu = new UiHarness(1280, 720))
            {
                yield return null;
                hud.Root.Build(hud.Harness.Root);
                FillPersistent(hud.Root);
                hud.Harness.Layout();
                Rect cluster = hud.Root.Vitals.worldBound;

                var player = new InventoryState();
                player.AddItem("scrap_metal", 2);
                var panel = new InventoryPanel();
                var layer = UiFactory.Box("ss-layer", "ss-layer--inspection");
                var root = UiFactory.Box(UiClasses.Root, "menu-root");
                root.Add(layer);
                layer.Add(panel);
                menu.Mount(root);
                panel.OpenTransfer(player, ShipInventory.Create(), "HOLD", EquipmentState.Create());
                menu.Layout();
                Assert.Greater(panel.worldBound.xMin, cluster.xMax, "LIVE inspection keeps the lower-left survival readings uncovered");
                VisualElement row = panel.SelfList.RowAt(0);
                Assert.GreaterOrEqual(row.layout.height, 44f, "44 px row baseline");
                Assert.GreaterOrEqual(panel.CloseAction.layout.height, 44f, "44 px control baseline");
                Assert.Greater(panel.CloseAction.worldBound.yMax, 0);
                Assert.LessOrEqual(panel.CloseAction.worldBound.yMax, 720.5f, "Close stays reachable");
            }
        }
    }
}
