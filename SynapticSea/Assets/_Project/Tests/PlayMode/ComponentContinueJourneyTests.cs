using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.App;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Game;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>Real scenes with in-memory saves and explicit fixture equipment. No earned-resource or OS-input claim.</summary>
    public abstract class ComponentRuntimeFixture : InputTestFixture
    {
        IStorage _previousStorage;
        IResourceReader _previousResources;
        ILog _previousLog;
        float _previousTimeScale;
        protected MemoryStorage Storage;
        protected ComponentWriteFaultStorage FaultStorage;
        protected PlayableBootstrap Boot;
        protected RunSession Session => Boot.Session;

        public override void Setup()
        {
            base.Setup();
            _previousStorage = CoreServices.UserStorage;
            _previousResources = CoreServices.Resources;
            _previousLog = CoreServices.Log;
            _previousTimeScale = Time.timeScale;
            Storage = new MemoryStorage();
            FaultStorage = new ComponentWriteFaultStorage(Storage);
            AppServices.StorageOverride = FaultStorage;
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
        }

        public override void TearDown()
        {
            Time.timeScale = _previousTimeScale;
            foreach (RunSessionHost host in Object.FindObjectsByType<RunSessionHost>()) Object.DestroyImmediate(host.gameObject);
            foreach (PlayableBootstrap boot in Object.FindObjectsByType<PlayableBootstrap>()) Object.DestroyImmediate(boot.gameObject);
            foreach (TitleScreen title in Object.FindObjectsByType<TitleScreen>()) Object.DestroyImmediate(title.gameObject);
            AppServices.Shutdown();
            AppServices.StorageOverride = null;
            RunLaunchRequest.Pending = null;
            RunReturnInfo.Clear();
            HallucinationFx.SetGlobalIntensity(0);
            CatalogRegistry.Clear();
            GameplayPropFactory.ClearCache();
            CoreServices.UserStorage = _previousStorage;
            CoreServices.Resources = _previousResources;
            CoreServices.Log = _previousLog;
            base.TearDown();
        }

        protected IEnumerator Launch(RunLaunchRequest request)
        {
            PlayableBootstrap previous = PlayableBootstrap.Current;
            RunLaunchRequest.Pending = request;
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            yield return WaitForBoot(previous);
        }

        protected IEnumerator WaitForBoot(PlayableBootstrap previous = null)
        {
            float deadline = Time.realtimeSinceStartup + 60;
            while (Time.realtimeSinceStartup < deadline && (PlayableBootstrap.Current == null || ReferenceEquals(PlayableBootstrap.Current, previous) || !PlayableBootstrap.Current.IsBooted)) yield return null;
            Boot = PlayableBootstrap.Current;
            Assert.IsNotNull(Boot);
            Assert.IsTrue(Boot.IsBooted, Boot.BootFailure);
            Session.ThreatManager.InjectValidationEncounter(new GdArray(), Vec3.Zero); // Isolate UI/work/save from combat, explicitly.
            yield return null;
        }

        protected IEnumerator ReturnToTitle(Action<TitleScreen> ready)
        {
            Session.QuitToTitle();
            yield return WaitForTitle(ready);
        }

        protected IEnumerator WaitForTitle(Action<TitleScreen> ready)
        {
            float deadline = Time.realtimeSinceStartup + 20;
            TitleScreen title = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                title = Object.FindFirstObjectByType<TitleScreen>();
                if (title != null && title.IsBuilt) break;
                yield return null;
            }
            Assert.IsNotNull(title);
            Assert.IsTrue(title.IsBuilt);
            ready(title);
        }

        protected static GdDict Instances(GdDict domain) => domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances");
        protected GdDict Instance(string id) => Instances(Session.CaptureComponentDomain()).GetDictOrEmpty(id);
        protected GdDict TargetFor(string id)
        {
            string holder = Instance(id).GetString("holder");
            return Session.ListInstallTargets(id).OfType<GdDict>().Single(row => row.GetString("holder_id") == holder);
        }
        protected static Vec3 Anchor(GdDict target) => target.Get("world_position") is Vec3 position ? position : Vec3.FromArray(target.GetArrayOrEmpty("world_position"));

        protected void StandAt(GdDict target)
        {
            Boot.Host.SceneState.TeleportPlayer(Anchor(target) + Vec3.Up * (float)RunSession.PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR);
            Boot.Host.ApplyViews();
        }

        protected static void Submit(VisualElement element)
        {
            Assert.IsNotNull(element);
            using (NavigationSubmitEvent evt = NavigationSubmitEvent.GetPooled()) { evt.target = element; element.SendEvent(evt); }
        }

        protected static void Click(VisualElement element)
        {
            Assert.IsNotNull(element);
            using (PointerDownEvent evt = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 }))
            { evt.target = element; element.SendEvent(evt); }
        }

        protected static void ResolvePanelLayout(VisualElement element)
        {
            IPanel panel = element.panel;
            Assert.IsNotNull(panel, "The synthetic pointer targets an actually mounted runtime UI element.");
            foreach (string method in new[] { "ApplyStyles", "ValidateLayout" })
            {
                MethodInfo found = null;
                for (Type type = panel.GetType(); type != null && found == null; type = type.BaseType)
                    found = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                Assert.IsNotNull(found, "Runtime panel layout seam " + method);
                found.Invoke(panel, null);
            }
        }

        protected IEnumerator CompleteHeldWork(Keyboard keyboard, string instanceId, string holder)
        {
            Assert.AreEqual("active", Session.GetComponentWorkState().GetString("status"));
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.E));
            float deadline = Time.realtimeSinceStartup + 20;
            while (Time.realtimeSinceStartup < deadline && Instance(instanceId).GetString("holder") != holder)
            {
                Session.VitalsState.Stamina = Session.VitalsState.MaxStamina; // Provisioned work fixture; no stamina/source claim.
                yield return null;
            }
            GdDict completionState = new GdDict
            {
                { "work", Session.GetComponentWorkState() }, { "instance_id", instanceId }, { "expected_holder", holder },
                { "live_holder", Instance(instanceId).GetString("holder") }, { "interact_held", Session.IsWorkInteractHeld },
                { "player_input_enabled", Boot.Input.Player.enabled }, { "interact_action_pressed", Boot.Input.Player.interact.IsPressed() },
                { "ui_top", Boot.Coordinator.Stack.Top?.SurfaceId ?? "" }, { "ui_blocks_gameplay", Boot.Coordinator.Stack.BlocksGameplay },
                { "simulation_paused", Boot.Coordinator.Stack.SimulationPaused }, { "player_position", Boot.Host.SceneState.PlayerPosition },
            };
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null;
            Assert.AreEqual(holder, Instance(instanceId).GetString("holder"), GdJson.Stringify(completionState));
            Assert.AreEqual("committed", Session.GetComponentWorkState().GetString("status"));
        }

        protected IEnumerator OpenRealCargo()
        {
            var control = Session.CargoHoldControls.First(c => c.IsValid && c.CarrierId == Session.CurrentShip.ShipId);
            Boot.Host.SceneState.TeleportPlayer(control.GlobalPosition);
            Boot.Host.RequestInteract();
            yield return null;
            Assert.IsTrue(Boot.Ui.Inventory.IsOpen(), "Actual reachable cargo control opens the transfer panel.");
            Assert.AreEqual("transfer", Boot.Ui.Inventory.GetMode());
            Assert.IsNotEmpty(Session.GetComponentHolderIds().GetString("ship_cargo"));
        }

        protected void SelectInventory(string pane, string id)
        {
            int index = Boot.Ui.Inventory.GetPaneIds(pane).IndexOf("instance:" + id);
            Assert.GreaterOrEqual(index, 0);
            Boot.Ui.Inventory.SelectRow(pane, index, false, false);
        }

        protected void StartOrdinaryFieldCraft()
        {
            Assert.AreEqual(2, Session.InventoryState.AddItem("synth_fiber", 2), "Explicit craft ingredient fixture.");
            Assert.AreEqual(1, Session.InventoryState.AddItem("medical_gauze", 1));
            GdDict result = Session.BeginCraftFromPicker("field_crafting", "field_bandage");
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.IsTrue(Session.FieldCraftingState.IsCrafting());
        }

        protected IEnumerator RecordRuntimeEvidence(string phase, GdDict details, bool screenshot = false)
        {
            yield return null;
            string runnerEvidence = Environment.GetEnvironmentVariable("SYNAPTIC_CATALOG_EVIDENCE_DIR");
            string evidenceRoot = string.IsNullOrWhiteSpace(runnerEvidence)
                ? Path.Combine(Application.dataPath, "../../artifacts/component-live-integration-2026-10-02") : runnerEvidence;
            string folder = Path.GetFullPath(Path.Combine(evidenceRoot, "runtime-ui-evidence"));
            Directory.CreateDirectory(folder);
            GdDict evidence = details.DeepCopy();
            evidence["phase"] = phase;
            evidence["proof_scope"] = "explicitly_provisioned_component_diagnostic_virtual_input";
            evidence["graphics_device"] = SystemInfo.graphicsDeviceType.ToString();
            evidence["screenshot_status"] = "not_requested";
            if (screenshot)
            {
                if (Application.isBatchMode)
                    evidence["screenshot_status"] = "unavailable_batch_gameview";
                else if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    evidence["screenshot_status"] = "unavailable_null_graphics";
                else
                {
                    Texture2D pixels = null;
                    try
                    {
                        pixels = ScreenCapture.CaptureScreenshotAsTexture();
                        if (pixels == null) evidence["screenshot_status"] = "unavailable_no_pixels";
                        else
                        {
                            string filename = phase + ".png";
                            File.WriteAllBytes(Path.Combine(folder, filename), pixels.EncodeToPNG());
                            evidence["screenshot_status"] = "captured";
                            evidence["screenshot_file"] = filename;
                        }
                    }
                    finally { if (pixels != null) Object.Destroy(pixels); }
                }
            }
            File.WriteAllText(Path.Combine(folder, phase + ".json"), GdJson.Stringify(evidence, "  "));
        }

        protected sealed class ComponentWriteFaultStorage : IStorage
        {
            readonly MemoryStorage _inner;
            public bool RejectComponentWrites;
            public ComponentWriteFaultStorage(MemoryStorage inner) { _inner = inner; }
            public bool FileExists(string path) => _inner.FileExists(path);
            public bool DirExists(string path) => _inner.DirExists(path);
            public string ReadText(string path) => _inner.ReadText(path);
            public void WriteText(string path, string text)
            {
                if (RejectComponentWrites && path.Contains(".component-live-generations")) throw new IOException("Provisioned terminal publication fault");
                _inner.WriteText(path, text);
            }
            public bool Delete(string path) => _inner.Delete(path);
            public bool Rename(string from, string to) => _inner.Rename(from, to);
            public void MakeDirRecursive(string path) => _inner.MakeDirRecursive(path);
            public IReadOnlyList<string> ListFiles(string dir) => _inner.ListFiles(dir);
            public IReadOnlyList<string> ListDirectories(string dir) => _inner.ListDirectories(dir);
            public bool DeleteDirectory(string path) => _inner.DeleteDirectory(path);
            public string Globalize(string path) => _inner.Globalize(path);
        }
    }

    public class ComponentContinueJourneyTests : ComponentRuntimeFixture
    {
        static GdDict BoundedProgression(GdDict progression)
        {
            var result = new GdDict { { "class_id", progression.GetString("class_id") } };
            foreach (string field in new[] { "skills", "skill_xp", "skill_xp_fractional", "cross_training" })
            {
                GdDict source = progression.GetDictOrEmpty(field), values = new GdDict();
                foreach (var entry in source.OrderBy(entry => V.Str(entry.Key), StringComparer.Ordinal).Take(64))
                    values[entry.Key] = new GdDict { { "value", entry.Value }, { "clr_type", entry.Value?.GetType().Name ?? "null" } };
                result[field] = values;
                result[field + "_count"] = (long)source.Count;
            }
            return result;
        }

        static GdDict BoundedTraining(GdDict training)
        {
            var rows = new GdArray();
            foreach (GdDict row in training.GetArrayOrEmpty("log").OfType<GdDict>().Reverse().Take(8).Reverse())
            {
                var retained = new GdDict();
                foreach (string field in new[] { "event_id", "target_id", "skill_id", "base_xp", "gated", "is_cross_training", "receipt_owned", "commit_id", "sequence" })
                    if (row.Has(field)) retained[field] = row.Get(field);
                rows.Add(retained);
            }
            return new GdDict { { "event_count", training.Get("event_count") }, { "xp_total", training.Get("xp_total") },
                { "dropped", training.Get("dropped") }, { "last_eight_events", rows } };
        }

        GdDict ProgressionEvidence(GdDict domain)
        {
            GdDict participants = domain.GetDictOrEmpty("participating_state"), receipts = domain.GetDictOrEmpty("receipts");
            var retained = new GdArray();
            foreach (GdDict receipt in receipts.Values.OfType<GdDict>().OrderBy(row => row.GetInt("revision")).Reverse().Take(8).Reverse())
                retained.Add(new GdDict { { "transaction_id", receipt.Get("transaction_id") }, { "command_id", receipt.Get("command_id") },
                    { "revision", receipt.Get("revision") }, { "result", receipt.GetDictOrEmpty("result").DeepCopy() } });
            return new GdDict { { "live_progression", BoundedProgression(Session.PlayerProgression.GetSummary()) },
                { "owner_progression", BoundedProgression(participants.GetDictOrEmpty("progression")) },
                { "live_training", BoundedTraining(Session.TrainingEventBus.ToDict()) },
                { "owner_training", BoundedTraining(participants.GetDictOrEmpty("training")) },
                { "receipt_count", (long)receipts.Count }, { "last_eight_receipts", retained },
                { "work", Session.GetComponentWorkState() }, { "world_time", Session.WorldTime },
                { "work_held", Session.IsWorkInteractHeld } };
        }

        [UnityTest]
        public IEnumerator DiagnosticProvisionedTimedRemovalBagCargoChosenInstallSaveAndTitleContinuePreserveExactEvidence()
        {
            Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            Assert.IsTrue(Session.ComponentIntegrationEnabled);
            GdDict domain = Session.CaptureComponentDomain();
            var pair = Instances(domain).Values.OfType<GdDict>().Where(row => row.GetString("holder").StartsWith("slot:" + Session.CurrentShip.ShipId + ":", StringComparison.Ordinal))
                .GroupBy(row => row.GetString("item_form")).Where(group => group.Count() >= 2)
                .OrderByDescending(group => group.Any(row => domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).Has("machinery_id")))
                .First().OrderByDescending(row => domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).Has("machinery_id")).Take(2).ToArray();
            string low = pair[0].GetString("instance_id"), high = pair[1].GetString("instance_id");
            Instances(domain).GetDictOrEmpty(low)["condition"] = 0.23;
            Instances(domain).GetDictOrEmpty(high)["condition"] = 0.81;
            string machineLow = domain.GetDictOrEmpty("holders").GetDictOrEmpty(pair[0].GetString("holder")).GetString("machinery_id");
            Assert.IsNotEmpty(machineLow, "The same-form fixture uses an actual established machinery link.");
            GdDict lowMachine = domain.GetDictOrEmpty("machinery").GetDictOrEmpty(machineLow);
            lowMachine["health"] = 0.8;
            string machineDamaged = domain.GetDictOrEmpty("machinery").Keys.Select(key => V.Str(key)).First(machine => machine != machineLow);
            domain.GetDictOrEmpty("machinery").GetDictOrEmpty(machineDamaged)["health"] = 0.2;
            Assert.IsTrue(Session.RestoreComponentDomain(domain), "Exact condition fixture on existing generated IDs.");
            Session.InventoryState.AddItem("wrench", 1); // Explicit equipment fixture, never an earned route.
            GdDict source = TargetFor(low);
            string originalHolder = source.GetString("holder_id"), player = Session.GetComponentHolderIds().GetString("player");
            double mass = Instance(low).GetFloat("mass");
            GdDict origin = Instance(low).GetDictOrEmpty("origin").DeepCopy(), provenance = Instance(low).GetDictOrEmpty("provenance").DeepCopy();
            StandAt(source);
            Boot.Ui.OnPanelToggle("toggle_ship_mod");
            Assert.IsTrue(Boot.Ui.ShipMod.SelectTarget(source.GetString("ship_id"), source.GetString("slot_id")));
            Submit(Boot.Ui.ShipMod.Query<Button>().ToList().Single(button => button.text == "Uninstall"));
            Assert.IsTrue(Boot.Ui.ShipMod.LastCommandResult.GetBool("ok"), Boot.Ui.ShipMod.LastCommandResult.GetString("reason"));
            Assert.IsFalse(Boot.Ui.ShipMod.LastCommandResult.GetBool("committed"));
            Assert.AreEqual(originalHolder, Instance(low).GetString("holder"), "Submit only starts timed work.");
            yield return CompleteHeldWork(keyboard, low, player);
            Assert.AreEqual(0.23, Instance(low).GetFloat("condition"), 1e-9);
            Assert.AreEqual(0.81, Instance(high).GetFloat("condition"), 1e-9);

            yield return OpenRealCargo();
            string cargo = Session.GetComponentHolderIds().GetString("ship_cargo");
            SelectInventory(InventoryPanel.PaneSelf, low);
            Click(Boot.Ui.Inventory.SelfList.RowAt(Boot.Ui.Inventory.SelfList.SelectedIndex));
            Assert.AreEqual(1, Boot.Ui.Inventory.TransferSelected(InventoryPanel.PaneSelf));
            Assert.AreEqual(cargo, Instance(low).GetString("holder"));
            SelectInventory(InventoryPanel.PaneContainer, low);
            Submit(Boot.Ui.Inventory.ContainerList.RowAt(Boot.Ui.Inventory.ContainerList.SelectedIndex));
            Assert.AreEqual(player, Instance(low).GetString("holder"));
            Boot.Ui.Inventory.Close();
            Assert.IsEmpty(Session.GetComponentHolderIds().GetString("ship_cargo"), "Closing the panel revokes cargo permission.");

            StandAt(source);
            Boot.Ui.OnPanelToggle("toggle_ship_mod");
            Assert.IsTrue(Boot.Ui.ShipMod.SelectComponent(low));
            Assert.IsTrue(Boot.Ui.ShipMod.SelectTarget(source.GetString("ship_id"), source.GetString("slot_id")));
            // Provision a legal diagnostic budget from actual installed definitions; never weaken the production gate.
            GdDict installing = Session.CaptureComponentDomain();
            double demand = Session.ShipModificationState.PowerDemandBaseline + Instances(installing).Values.OfType<GdDict>()
                .Where(row => row.GetString("holder").StartsWith("slot:" + Session.CurrentShip.ShipId + ":", StringComparison.Ordinal))
                .Sum(row => Session.ComponentCatalog.GetComponent(row.GetString("definition_id")).GetFloat("power_draw"));
            Session.ShipModificationState.PowerSupply = Math.Max(Session.ShipModificationState.PowerSupply,
                demand + Session.ComponentCatalog.GetComponent(Instance(low).GetString("definition_id")).GetFloat("power_draw"));
            yield return RecordRuntimeEvidence("chosen-instance-and-mount", new GdDict
            {
                { "instance", Instance(low).DeepCopy() }, { "same_form_sibling", Instance(high).DeepCopy() },
                { "target", Session.ListInstallTargets(low).OfType<GdDict>().Single(row => row.GetString("holder_id") == originalHolder) },
                { "ui_selected_instance", Boot.Ui.ShipMod.GetSelectedInstanceId() }, { "ui_selected_slot", Boot.Ui.ShipMod.GetSelectedSlotId() },
                { "provisioned_wrench", true }, { "provisioned_legal_power_supply", Session.ShipModificationState.PowerSupply },
                { "machinery", Session.CaptureComponentDomain().GetDictOrEmpty("machinery").DeepCopy() },
            }, screenshot: true);
            Submit(Boot.Ui.ShipMod.Query<Button>().ToList().Single(button => button.text == "Install"));
            Assert.IsTrue(Boot.Ui.ShipMod.LastCommandResult.GetBool("ok"), Boot.Ui.ShipMod.LastCommandResult.GetString("reason"));
            Assert.IsFalse(Boot.Ui.ShipMod.LastCommandResult.GetBool("committed"));
            yield return CompleteHeldWork(keyboard, low, originalHolder);
            Assert.AreEqual(0.8, Session.CaptureComponentDomain().GetDictOrEmpty("machinery").GetDictOrEmpty(machineLow).GetFloat("health"), 1e-9);
            Assert.AreEqual(0.2, Session.CaptureComponentDomain().GetDictOrEmpty("machinery").GetDictOrEmpty(machineDamaged).GetFloat("health"), 1e-9);
            Vec3 pose = Boot.Host.SceneState.PlayerPosition;
            Assert.IsTrue(Session.RequestSave());
            GdDict selected = Session.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(selected.GetBool("ok"), selected.GetString("reason"));
            string generation = selected.GetString("generation_id");
            GdDict savedInstances = Instances(Session.CaptureComponentDomain()).DeepCopy();
            GdDict savedMachines = Session.CaptureComponentDomain().GetDictOrEmpty("machinery").DeepCopy();
            GdDict savedXp = Session.PlayerProgression.SkillXp.DeepCopy();
            GdDict savedProgressionEvidence = ProgressionEvidence(Session.CaptureComponentDomain());
            GdDict selectedWorld = GdJson.ParseString(selected.GetDictOrEmpty("payloads").GetString("world_text")) as GdDict;
            Assert.IsTrue(ComponentDomainCodec.TryDecode(selectedWorld.GetDictOrEmpty("component_domain"), out GdDict selectedDomain, out string selectedCodecReason), selectedCodecReason);
            GdDict selectedProgressionEvidence = ProgressionEvidence(selectedDomain);
            long output = Session.InventoryState.GetQuantity("field_bandage");
            // Observe exact restore before ordinary timed events resume; TearDown restores the prior scale.
            Time.timeScale = 0;
            TitleScreen title = null;
            yield return ReturnToTitle(value => title = value);
            Assert.IsFalse(title.EnableComponentIntegration, "Title never infers diagnostic mode from saved files.");
            title.ConfigureComponentDiagnostic(true); // Explicit dev/fixture selection, not an ordinary menu activation.
            title.Coordinator.MenuPanel.FocusRow(title.Coordinator.MenuPanel.Rows.ToList().FindIndex(item => item.Id == "continue"));
            title.Coordinator.HandleUiInput(UiCommand.Accept);
            yield return WaitForBoot();
            Assert.IsTrue(Boot.Launch.EnableComponentIntegration);
            Assert.AreEqual(generation, Boot.Launch.SelectedSaveGeneration.GetString("generation_id"));
            Assert.IsTrue(V.VariantEquals(savedInstances, Instances(Session.CaptureComponentDomain())), "Continue restores exact holders/conditions/mass/evidence once.");
            Assert.IsTrue(V.VariantEquals(savedMachines, Session.CaptureComponentDomain().GetDictOrEmpty("machinery")), "Machinery health is retained without recovery or replay.");
            Assert.AreEqual(mass, Instance(low).GetFloat("mass"), 1e-9);
            Assert.IsTrue(V.VariantEquals(origin, Instance(low).GetDictOrEmpty("origin")));
            Assert.IsTrue(V.VariantEquals(provenance, Instance(low).GetDictOrEmpty("provenance")));
            Assert.AreEqual(output, Session.InventoryState.GetQuantity("field_bandage"), "No craft output is replayed.");
            Assert.IsTrue(V.VariantEquals(savedXp, Session.PlayerProgression.SkillXp), "Continue cannot replay work XP. " +
                GdJson.Stringify(new GdDict { { "expected", savedProgressionEvidence },
                    { "selected_payload", selectedProgressionEvidence },
                    { "actual", ProgressionEvidence(Session.CaptureComponentDomain()) } }));
            Assert.Less(Boot.Host.SceneState.PlayerPosition.DistanceTo(pose), 0.35, "Selected world pose survives scene bootstrap.");
            Boot.Ui.OnPanelToggle("toggle_ship_mod");
            Boot.Ui.ShipMod.SelectTarget(source.GetString("ship_id"), source.GetString("slot_id"));
            yield return RecordRuntimeEvidence("post-title-continue-exact-owner", new GdDict
            {
                { "instance", Instance(low).DeepCopy() }, { "same_form_sibling", Instance(high).DeepCopy() },
                { "machinery", Session.CaptureComponentDomain().GetDictOrEmpty("machinery").DeepCopy() },
                { "selected_generation_id", Boot.Launch.SelectedSaveGeneration.GetString("generation_id") },
                { "expected_generation_id", generation }, { "slot_id", Boot.Launch.SlotId },
                { "player_position", Boot.Host.SceneState.PlayerPosition }, { "saved_player_position", pose },
                { "instance_evidence_equal", V.VariantEquals(savedInstances, Instances(Session.CaptureComponentDomain())) },
                { "raw_machinery_equal", V.VariantEquals(savedMachines, Session.CaptureComponentDomain().GetDictOrEmpty("machinery")) },
                { "xp_equal", V.VariantEquals(savedXp, Session.PlayerProgression.SkillXp) },
            }, screenshot: true);
        }

        [UnityTest]
        public IEnumerator OrdinaryActiveCraftSaveAndTitleContinueRemainOnLegacyBootstrapPath()
        {
            yield return Launch(RunLaunchRequest.NewRun());
            Assert.IsFalse(Session.ComponentIntegrationEnabled);
            StartOrdinaryFieldCraft();
            Assert.AreEqual(2, Session.CraftingState.EnqueueCraft("field_bandage", 2), "Explicit ordinary queued-job fixture using the established queue API.");
            GdArray queue = Session.CraftingState.GetSummary().GetDictOrEmpty("station_summaries").GetDictOrEmpty("field_crafting").GetArrayOrEmpty("queue").DeepCopy();
            Assert.AreEqual(2, queue.Count);
            long fiberAfterCharge = Session.InventoryState.GetQuantity("synth_fiber");
            long outputBefore = Session.InventoryState.GetQuantity("field_bandage");
            Assert.IsTrue(Session.RequestSave(), "Ordinary active craft retains established legacy save behavior.");
            GdDict saved = GdJson.ParseString(Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE)) as GdDict;
            Assert.AreEqual("world-4", saved.GetString("slice_version"));
            Assert.AreEqual("gate2-current-run-6", saved.GetDictOrEmpty("home_ship").GetString("slice_version"));
            TitleScreen title = null;
            yield return ReturnToTitle(value => title = value);
            title.Coordinator.MenuPanel.FocusRow(title.Coordinator.MenuPanel.Rows.ToList().FindIndex(item => item.Id == "continue"));
            title.Coordinator.HandleUiInput(UiCommand.Accept);
            yield return WaitForBoot();
            Assert.AreEqual(RunLaunchMode.Continue, Boot.Launch.Mode);
            Assert.IsFalse(Boot.Launch.EnableComponentIntegration);
            Assert.IsNull(Boot.Launch.SelectedSaveGeneration);
            Assert.IsTrue(Session.FieldCraftingState.IsCrafting(), "Existing active craft summary loads through actual Title/Playable bootstrap.");
            Assert.AreEqual("field_bandage", Session.FieldCraftingState.GetActiveRecipeId());
            Assert.AreEqual(fiberAfterCharge, Session.InventoryState.GetQuantity("synth_fiber"));
            Assert.AreEqual(outputBefore, Session.InventoryState.GetQuantity("field_bandage"));
            Assert.IsTrue(V.VariantEquals(queue, Session.CraftingState.GetSummary().GetDictOrEmpty("station_summaries").GetDictOrEmpty("field_crafting").GetArrayOrEmpty("queue")), "Actual legacy bootstrap retains queued jobs unchanged.");
        }

        [UnityTest]
        public IEnumerator DiagnosticLegacyBootstrapFailureReturnsToTitleWithWorkingOriginalSaveRecovery()
        {
            yield return Launch(RunLaunchRequest.NewRun());
            StartOrdinaryFieldCraft();
            Assert.IsTrue(Session.RequestSave());
            string original = Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE);
            RunLaunchRequest.Pending = RunLaunchRequest.DiagnosticContinue();
            SceneManager.LoadScene(RunLaunchRequest.PlayableSceneName);
            TitleScreen title = null;
            yield return WaitForTitle(value => title = value);
            Assert.IsFalse(title.EnableComponentIntegration);
            Button recovery = title.StatusBox.Query<Button>().ToList().Single(button => button.text == "Open original save");
            Submit(recovery);
            yield return WaitForBoot();
            Assert.IsFalse(Boot.Launch.EnableComponentIntegration);
            Assert.IsNull(Boot.Launch.SelectedSaveGeneration);
            Assert.IsTrue(Session.FieldCraftingState.IsCrafting());
            Assert.AreEqual(original, Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
        }

        [UnityTest]
        public IEnumerator TitleExplicitSelectedManualGenerationBootsItsEvidenceDespiteNewerWorldAndCallerMutation()
        {
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            GdDict domain = Session.CaptureComponentDomain();
            string id = V.Str(Instances(domain).Keys.First());
            Instances(domain).GetDictOrEmpty(id)["condition"] = 0.23;
            Assert.IsTrue(Session.RestoreComponentDomain(domain));
            Assert.IsTrue(Session.RequestSaveToSlot("slot_01", "manual", "Exact manual"));
            GdDict selected = Session.SaveLoadService.SelectGeneration("slot_01");
            string generation = selected.GetString("generation_id");
            domain = Session.CaptureComponentDomain();
            Instances(domain).GetDictOrEmpty(id)["condition"] = 0.81;
            Assert.IsTrue(Session.RestoreComponentDomain(domain));
            Assert.IsTrue(Session.RequestSave());
            TitleScreen title = null;
            yield return ReturnToTitle(value => title = value);
            title.ConfigureComponentDiagnostic(true);
            title.LaunchRequested += request => selected["generation_id"] = "caller-mutated";
            Assert.IsTrue(title.Launch(RunLaunchRequest.DiagnosticLoadSlot("slot_01", selected)));
            yield return WaitForBoot();
            Assert.AreEqual(generation, Boot.Launch.SelectedSaveGeneration.GetString("generation_id"));
            Assert.AreEqual(0.23, Instance(id).GetFloat("condition"), 1e-9);
            Assert.AreEqual("slot_01", Boot.Launch.SlotId);
        }

        [UnityTest]
        public IEnumerator DiagnosticLegacyRefusalOffersOriginalSaveRecoveryAndKeepsLegacyBytes()
        {
            yield return Launch(RunLaunchRequest.NewRun());
            StartOrdinaryFieldCraft();
            Assert.IsTrue(Session.RequestSave());
            string originalWorld = Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE);
            Assert.IsNotEmpty(originalWorld);
            TitleScreen title = null;
            yield return ReturnToTitle(value => title = value);
            title.ConfigureComponentDiagnostic(true);
            title.Coordinator.MenuPanel.FocusRow(title.Coordinator.MenuPanel.Rows.ToList().FindIndex(item => item.Id == "continue"));
            title.Coordinator.HandleUiInput(UiCommand.Accept);
            yield return null;
            Assert.IsNull(RunLaunchRequest.Pending, "Conversion refusal never starts a partial runtime.");
            Button original = title.StatusBox.Query<Button>().ToList().Single(button => button.text == "Open original save");
            Submit(original);
            yield return WaitForBoot();
            Assert.IsFalse(Boot.Launch.EnableComponentIntegration);
            Assert.IsNull(Boot.Launch.SelectedSaveGeneration);
            Assert.IsTrue(Session.FieldCraftingState.IsCrafting());
            Assert.AreEqual(originalWorld, Storage.ReadText(SaveLoadService.WORLD_SLOT_FILE));
            Assert.IsTrue(Session.RequestSave(), "Recovered ordinary session still saves with existing legacy rules.");
        }

        [UnityTest]
        public IEnumerator DiagnosticActiveCraftFullSaveRefusesVisiblyAndRetainsEarlierGeneration()
        {
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            Assert.IsTrue(Session.RequestSave());
            string earlier = Session.SaveLoadService.SelectGeneration("world").GetString("generation_id");
            StartOrdinaryFieldCraft();
            Assert.IsFalse(Session.RequestSave());
            Assert.AreEqual(earlier, Session.SaveLoadService.SelectGeneration("world").GetString("generation_id"));
            Boot.Coordinator.OpenMetaScreen("save_load");
            Boot.Coordinator.SaveSlots.SelectRow(Boot.Coordinator.SaveSlots.Rows().FindIndex(row => row.SlotId == "slot_01"));
            GdDict result = Boot.Coordinator.SaveLoadScreen.ConfirmVerb(SaveSlotScreenModel.VerbSave);
            Assert.IsFalse(result.GetBool("ok"));
            StringAssert.Contains("active and queued crafting", Boot.Coordinator.SaveLoadScreen.StatusDisplay);
            StringAssert.Contains("earlier save is preserved", Boot.Coordinator.SaveLoadScreen.StatusDisplay);
        }

        [UnityTest]
        public IEnumerator DiagnosticDeathPublicationFailureShowsReachableRetryAndCompletesOnceAfterStorageRecovers()
        {
            yield return Launch(RunLaunchRequest.DiagnosticNewRun());
            Assert.IsTrue(Session.RequestSave());
            int completions = 0;
            Session.PlayableSliceCompleted += _ => completions++;
            FaultStorage.RejectComponentWrites = true;
            Session.EndRun("death");
            yield return null;
            Assert.IsFalse(Session.SliceComplete);
            Assert.AreEqual(0, completions);
            Assert.IsNull(Boot.Ui.Results);
            Assert.IsTrue(Session.LastSaveResult.GetBool("terminal_pending"));
            SurfacePanel pending = Boot.Ui.TerminalSaveRetry;
            Assert.IsNotNull(pending);
            Assert.AreSame(pending, Boot.Coordinator.Stack.Top);
            Assert.IsTrue(Boot.Coordinator.Stack.SimulationPaused);
            Assert.IsNull(RunLaunchRequest.Pending);
            FaultStorage.RejectComponentWrites = false;
            Submit(pending.Query<Button>().ToList().Single(button => button.text == "Retry saving death record"));
            yield return null;
            Assert.IsTrue(Session.SliceComplete);
            Assert.AreEqual(1, completions);
            Assert.IsNotNull(Boot.Ui.Results);
            Assert.IsNull(Boot.Ui.TerminalSaveRetry);
            Assert.IsTrue(Boot.Coordinator.SaveSlots.Rows().Single(row => row.SlotId == "world").Frozen);
            string ownedEpitaph = Session.SaveLoadService.GetComponentEpitaph("world");
            Assert.IsNotEmpty(ownedEpitaph);
            Assert.AreEqual(ownedEpitaph, Boot.Coordinator.SaveSlots.Epitaph("world"), "Diagnostic death UI reads its owned tombstone rather than ordinary death files.");
            Session.EndRun("death");
            Assert.AreEqual(1, completions, "Retrying a committed death does not replay completion or payout.");
        }
    }
}
