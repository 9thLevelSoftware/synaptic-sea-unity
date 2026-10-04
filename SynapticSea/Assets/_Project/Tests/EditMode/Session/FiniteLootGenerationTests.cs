using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Engine-free owned storage: establishes persistence, not physical source reachability.
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class FiniteLootGenerationTests : PaidCraftFixture
    {
        IEngineInfo _previousEngine;
        [SetUp] public void MatchHarnessEngine()
        {
            _previousEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }
        [TearDown] public void RestoreHarnessEngine() => CoreServices.Engine = _previousEngine;
        static string SaveDetail(RunSession session) => PaidSnapshotCodec.Stringify(session.LastSaveResult);
        const string Directory = "res://data/diagnostics/earned-entry-home-v1/";
        static RunSessionDeps Deps(out SessionHarness.Rig rig)
        {
            var deps = SessionHarness.GoldenDeps(out rig); SessionHarness.OverlayGamePlayability(deps);
            deps.LayoutPath = Directory + "layout.json"; deps.GameplaySlicePath = Directory + "gameplay_slice.json";
            deps.BlueprintPath = Directory + "blueprint.json"; deps.EnablePaidCrafting = true; deps.EnableManualStudy = true;
            return deps;
        }
        [Test] public void PartialKitSurvivesCompleteGenerationFreshContinueAndCannotRefillAfterDepletion()
        {
            var deps = Deps(out var original); RunSession first = RunSession.Create(deps), restored = null;
            try
            {
                Assert.IsTrue(first.PlayableStarted, first.LastFailureReason); first.InventoryState.Items.Clear();
                var kit = first.LootContainers.Single(row => row.FiniteSource != null);
                long ceiling = ItemDefs.MaxStack(ItemDefs.LoadDefinitions(), "scrap_metal"); first.InventoryState.AddItem("scrap_metal", ceiling - 2);
                original.Scene.PlayerPosition = kit.GlobalPosition;
                Assert.IsTrue(kit.TryInteract(original.Scene.PlayerPosition)); Assert.IsFalse(kit.Searched);
                Assert.AreEqual(2, kit.FiniteSource.GetDictOrEmpty("remaining").GetInt("scrap_metal"));
                Assert.AreEqual(1, first.InventoryState.GetQuantity("fabrication_schematic_basic"));
                int searchEvents = first.TrainingEventBus.GetLog().OfType<GdDict>().Count(row => row.GetString("event_id") == "scavenge_container");
                Assert.AreEqual(1, searchEvents);
                Assert.IsTrue(first.RequestSave(), SaveDetail(first));
                GdDict selection = first.SaveLoadService.SelectGeneration("world"); Assert.IsTrue(selection.GetBool("ok"));
                var nextDeps = Deps(out var next); nextDeps.Storage = original.Storage; nextDeps.SelectedSaveGeneration = selection;
                restored = RunSession.Create(nextDeps); Assert.IsTrue(restored.PlayableStarted, restored.LastFailureReason);
                var resumed = restored.LootContainers.Single(row => row.FiniteSource != null);
                Assert.AreEqual(2, resumed.FiniteSource.GetDictOrEmpty("remaining").GetInt("scrap_metal"));
                Assert.AreEqual(0, resumed.FiniteSource.GetDictOrEmpty("remaining").GetInt("fabrication_schematic_basic"));
                restored.InventoryState.RemoveItem("scrap_metal", 2); next.Scene.PlayerPosition = resumed.GlobalPosition;
                Assert.IsTrue(resumed.TryInteract(next.Scene.PlayerPosition)); Assert.IsTrue(resumed.Searched);
                Assert.AreEqual(1, restored.InventoryState.GetQuantity("fabrication_schematic_basic"));
                Assert.AreEqual(searchEvents, restored.TrainingEventBus.GetLog().OfType<GdDict>().Count(row => row.GetString("event_id") == "scavenge_container"));
                Assert.IsTrue(restored.RequestSave(), SaveDetail(restored));
                var depleted = restored.SaveLoadService.SelectGeneration("world"); Assert.IsTrue(restored.ApplySelectedGeneration(depleted));
                var exhausted = restored.LootContainers.Single(row => row.FiniteSource != null); Assert.IsTrue(exhausted.Searched);
                Assert.IsFalse(exhausted.TryInteract(exhausted.GlobalPosition)); Assert.AreEqual(ceiling, restored.InventoryState.GetQuantity("scrap_metal"));
            }
            finally { restored?.Dispose(); first.Dispose(); }
        }
    }
}
