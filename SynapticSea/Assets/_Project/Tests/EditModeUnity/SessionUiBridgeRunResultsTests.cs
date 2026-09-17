using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Game;
using SynapticSea.Runtime.Input;
using SynapticSea.Runtime.Session;
using SynapticSea.Tests.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// Godot title_main.gd <c>_on_gameplay_slice_completed</c> → <c>_show_run_results</c>: death and extract open
    /// <see cref="RunResultsPanel"/> on the modal stack (TERMINAL). <see cref="RunReturnInfo"/> is the title last-run
    /// line after confirm — it must not replace this panel.
    /// </summary>
    public class SessionUiBridgeRunResultsTests : UiTestBase
    {
        IEngineInfo _previousEngine;
        readonly List<Object> _owned = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            RunReturnInfo.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _owned)
                if (o != null) Object.DestroyImmediate(o);
            _owned.Clear();
            RunReturnInfo.Clear();
            CoreServices.Engine = _previousEngine;
        }

        SessionUiBridge BindBridge(RunSession session, UIDocument menuDocument)
        {
            var hudGo = new GameObject("hud");
            hudGo.SetActive(false);
            _owned.Add(hudGo);
            hudGo.AddComponent<UIDocument>();
            var hud = hudGo.AddComponent<HudRoot>();
            hud.Build(new VisualElement());
            var input = new SynapticSeaInput();
            var bridge = new SessionUiBridge(hud, menuDocument, input, new AccessibilitySettings(_ => null));
            bridge.PersistedSettings = () => null;
            bridge.SettingsPersist = _ => { };
            bridge.BindSessionEvents(session);
            bridge.BuildCoordinator(session, host: null, manager: null);
            return bridge;
        }

        [Test]
        public void DeathOpensRunResultsPanelWithOutcome()
        {
            RunSession session = SessionHarness.CreateGolden().Session;
            Assert.IsTrue(session.PlayableStarted, session.LastFailureReason);
            using (var menus = new UiHarness())
            {
                SessionUiBridge bridge = BindBridge(session, menus.Document);
                session.EndRun("death");

                RunResultsPanel results = bridge.Results;
                Assert.IsNotNull(results, "death opens RunResultsPanel (not a silent title dump)");
                Assert.AreSame(results, bridge.Coordinator.Stack.Top);
                Assert.IsTrue(bridge.Coordinator.Stack.SimulationPaused, "TERMINAL results pause the simulation");
                Assert.AreEqual("death", results.NormalizedOutcome());
                StringAssert.Contains("Outcome: death", results.BodyText);
                StringAssert.Contains("seed " + session.RunSeed, results.ContextLabel.text);
                Assert.AreEqual("", RunReturnInfo.LastRunOutcome, "RunReturnInfo is not a substitute for the panel");
            }
        }

        [Test]
        public void ExtractOpensRunResultsPanelWithOutcome()
        {
            RunSession session = SessionHarness.CreateGolden().Session;
            Assert.IsTrue(session.PlayableStarted, session.LastFailureReason);
            using (var menus = new UiHarness())
            {
                SessionUiBridge bridge = BindBridge(session, menus.Document);
                session.EndRun("extraction");

                RunResultsPanel results = bridge.Results;
                Assert.IsNotNull(results, "extract opens RunResultsPanel (not a silent title dump)");
                Assert.AreSame(results, bridge.Coordinator.Stack.Top);
                Assert.IsTrue(bridge.Coordinator.Stack.SimulationPaused);
                Assert.AreEqual("extraction", results.NormalizedOutcome());
                StringAssert.Contains("Outcome: extraction", results.BodyText);
                StringAssert.Contains("You extracted", results.Banner.text);
                StringAssert.Contains("seed " + session.RunSeed, results.ContextLabel.text);
                Assert.AreEqual("", RunReturnInfo.LastRunOutcome, "RunReturnInfo is not a substitute for the panel");
            }
        }

        [Test]
        public void SliceCompleteReasonOpensExtractionResults()
        {
            RunSession session = SessionHarness.CreateGolden().Session;
            using (var menus = new UiHarness())
            {
                SessionUiBridge bridge = BindBridge(session, menus.Document);
                // Objective completion emits reason=complete; the panel (and Godot) treats that as extraction.
                session.EndRun("complete");
                Assert.IsNotNull(bridge.Results, "slice complete opens RunResultsPanel");
                Assert.AreEqual("extraction", bridge.Results.NormalizedOutcome());
                StringAssert.Contains("Outcome: extraction", bridge.Results.BodyText);
            }
        }

        [Test]
        public void ResultsReturnToTitleRaisesTheHostSeam()
        {
            RunSession session = SessionHarness.CreateGolden().Session;
            using (var menus = new UiHarness())
            {
                SessionUiBridge bridge = BindBridge(session, menus.Document);
                int toTitle = 0;
                bridge.ResultsReturnToTitleRequested += () => toTitle++;
                session.EndRun("death");
                Assert.IsNotNull(bridge.Results);
                UiHarness.Submit(bridge.Results.ReturnButton);
                Assert.AreEqual(1, toTitle, "Return to Title leaves through the host, after the panel");
            }
        }

        [Test]
        public void QuitToTitleDoesNotOpenResults()
        {
            RunSession session = SessionHarness.CreateGolden().Session;
            using (var menus = new UiHarness())
            {
                SessionUiBridge bridge = BindBridge(session, menus.Document);
                session.QuitToTitle();
                Assert.IsNull(bridge.Results, "pause quit is not death/extract — no results panel");
            }
        }

        [Test]
        public void QuitAfterDeathDoesNotReplaceTheResultsPanel()
        {
            RunSession session = SessionHarness.CreateGolden().Session;
            using (var menus = new UiHarness())
            {
                SessionUiBridge bridge = BindBridge(session, menus.Document);
                session.EndRun("death");
                Assert.AreSame(bridge.Results, bridge.Coordinator.Stack.Top);
                session.QuitToTitle();
                Assert.AreSame(bridge.Results, bridge.Coordinator.Stack.Top, "quit must not dump over results");
                Assert.IsTrue(bridge.Coordinator.HandleUiInput(UiCommand.Pause), "pause is consumed on TERMINAL");
                Assert.AreSame(bridge.Results, bridge.Coordinator.Stack.Top);
            }
        }
    }
}
