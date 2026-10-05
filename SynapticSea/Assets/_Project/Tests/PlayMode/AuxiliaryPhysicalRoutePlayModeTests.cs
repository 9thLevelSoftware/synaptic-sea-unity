using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace SynapticSea.Tests.PlayMode
{
    public partial class RunLifecyclePlayModeTests
    {
        readonly List<double> _auxWorkTickMilliseconds=new List<double>();
        readonly List<double> _auxWorkingFrameMilliseconds=new List<double>();
        bool _auxMeasuring, _auxTickWasRunning;
        long _auxStageStart;
        static string AuxiliaryLatencySummary(List<double> values)
        {
            if(values.Count==0)return "samples=0";
            var sorted=values.OrderBy(v=>v).ToArray();
            return "samples="+sorted.Length+" median_ms="+sorted[sorted.Length/2].ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +" p95_ms="+sorted[(int)System.Math.Floor((sorted.Length-1)*.95)].ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +" worst_ms="+sorted[sorted.Length-1].ToString("F3",System.Globalization.CultureInfo.InvariantCulture);
        }

        // Functional completion budget: measured earned prefix ~152s plus two racks, rest, repairs,
        // crafting, walking and save/travel checks (~300s estimated); 2x bounded headroom.
        // A functional pass does not establish acceptable work-tick or frame performance.
        [UnityTest, Timeout(600000)] public IEnumerator CookPhysicallyEarnsAuxiliaryUtilitiesAndFiniteRacks() => AuxiliaryHomeRoute("cook");
        [UnityTest] public IEnumerator MedicPhysicallyEarnsAuxiliaryUtilitiesAndFiniteRacks() => AuxiliaryHomeRoute("medic");

        IEnumerator AuxiliaryDeck(int deck)
        {
            if ((_boot.Host.SceneState.Player.GodotPosition.Y > 3 ? 1 : 0) == deck) yield break;
            _s.RefreshDeckTransitions();
            yield return WalkTo(_s.DeckTransitions.First(d => d.DestinationDeck == deck), 2.4f);
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            Assert.AreEqual(deck, _boot.Host.SceneState.Player.GodotPosition.Y > 3 ? 1 : 0);
        }

        IEnumerator AuxiliaryManualStow(params string[] items)
        {
            var hold = _s.CargoHoldControls.First(c => c.IsValid && c.CarrierId == _s.HomeShip.ShipId);
            yield return AuxiliaryDeck(hold.GlobalPosition.Y > 3 ? 1 : 0);
            yield return WalkTo(hold, .5f);
            for (int n=0;n<6&&!_boot.Ui.Inventory.IsOpen();n++)
            { _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8); }
            Assert.IsTrue(_boot.Ui.Inventory.IsOpen(), "physical manual cargo handle");
            foreach (string item in items)
            {
                long amount = _s.InventoryState.GetQuantity(item);
                if (amount > 0) Assert.AreEqual(amount, _boot.Ui.Inventory.TransferQuantity(InventoryPanel.PaneSelf, item, amount), "manual cargo remains usable: " + item);
            }
            _boot.Ui.Inventory.Close(); yield return FixedSteps(8);
            Assert.LessOrEqual(_s.InventoryState.GetLoadRatio(), 1, "stow haul before industrial work");
        }

        IEnumerator AuxiliaryWalkToFixture(AuxiliaryServicePoint point)
        {
            // The prop is 1.08m above the authored floor. Walk to standing ground beneath it,
            // then require actual 3D strict range and production LOS; never position the player.
            Vec3 target=point.GlobalPosition;
            yield return WalkTo(new Vec3(target.X,_boot.Host.SceneState.Player.GodotPosition.Y,target.Z),1.1f);
        }

        IEnumerator AuxiliaryPhysicalWork(string id, bool interrupted = false)
        {
            var point = _s.AuxiliaryServicePoints.Single(p => p.ServiceId == id);
            yield return AuxiliaryDeck(point.GlobalPosition.Y > 3 ? 1 : 0);
            yield return AuxiliaryWalkToFixture(point);
            yield return FixedSteps(8); // Let normal movement settle before held industrial work.
            Assert.IsTrue(point.IsPlayerInDirectRangeStrict(_boot.Host.SceneState.Player.GodotPosition), "strict physical range: " + id);
            Debug.Log("[AuxiliaryAnchor] id="+id+" target="+point.GlobalPosition+" standing="+_boot.Host.SceneState.Player.GodotPosition);
            _auxMeasuring=true;
            _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            Assert.AreEqual(id, _s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetString("service_id"), "normal focus selects authored fixture");
            Assert.IsTrue(_s.AuxiliaryWorkRunning, GdJson.Stringify(_s.GetAuxiliaryServiceState()));
            if (interrupted)
            {
                float until = Time.realtimeSinceStartup + 8;
                while (_s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds") < 1 && Time.realtimeSinceStartup < until) yield return null;
                _boot.Host.SceneState.Player.SetScriptedMoveDirection(new Vec3(.3,0,0)); yield return FixedSteps(12);
                _boot.Host.SceneState.Player.ClearScriptedMoveDirection();
                Assert.IsFalse(_s.AuxiliaryWorkRunning, "movement pauses while interact remains held");
                _s.EndWorkHold();
                Assert.AreEqual(4,_s.InventoryState.GetQuantity("scrap_metal"),"partial work has not paid raw material");
                Assert.AreEqual(4,_s.InventoryState.GetQuantity("wiring_bundle"));
                double partial = _s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds");
                Assert.IsTrue(_s.RequestSave(), GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
                Assert.IsFalse(_s.AuxiliaryWorkRunning, "Continue restores zero held consent");
                Assert.AreEqual(partial, _s.GetAuxiliaryServiceState().GetDictOrEmpty("job").GetFloat("progress_seconds"));
                point = _s.AuxiliaryServicePoints.Single(p=>p.ServiceId==id); yield return AuxiliaryWalkToFixture(point);
                yield return FixedSteps(8);
                var gateMethod=typeof(RunSession).GetMethod("AuxiliaryGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var actual=_boot.Host.SceneState.Player.GodotPosition;
                Vec3 focusHit=Vec3.Zero,gateHit=Vec3.Zero;
                bool focusBlocked=_s.Deps.LosProbe?.HasSpace==true&&_s.Deps.LosProbe.IntersectRay(actual+new Vec3(0,1,0),point.GlobalPosition+new Vec3(0,1,0),out focusHit);
                bool gateBlocked=_s.Deps.LosProbe?.HasSpace==true&&_s.Deps.LosProbe.IntersectRay(actual+new Vec3(0,.8,0),point.GlobalPosition,out gateHit);
                Debug.Log("[AuxiliaryPreResume] gate="+gateMethod.Invoke(_s,new object[]{id,false,false})+" focus="+_s.CanFocusInteractable(point)+" valid="+point.IsValid+" tree="+point.IsInsideTree+" target="+point.GlobalPosition+" local="+point.LocalPosition+" owner="+point.OwnerId+" home="+_s.HomeShip.ShipId+" root_valid="+point.Parent.IsValid+" root_tree="+point.Parent.IsInsideTree+" root_transform="+point.Parent.GlobalTransform+" range="+point.IsPlayerInDirectRangeStrict(actual)+" actionable="+point.Actionable?.Invoke(id)+" crowbar="+_s.InventoryState.GetQuantity("crowbar")+" scrap="+_s.InventoryState.GetQuantity("scrap_metal")+" wire="+_s.InventoryState.GetQuantity("wiring_bundle")+" generic_work="+_s.WorkActionDriver.IsWorking()+" focus_blocked="+focusBlocked+" focus_hit="+focusHit+" gate_blocked="+gateBlocked+" gate_hit="+gateHit);
                _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
                Debug.Log("[AuxiliaryResume] handler="+_s.LastInteractHandlerId+" moving="+_boot.Host.SceneState.Player.IsMoving()+" held="+_s.IsWorkInteractHeld+" stamina="+_s.VitalsState.Stamina+" player="+_boot.Host.SceneState.Player.GodotPosition+" state="+GdJson.Stringify(_s.GetAuxiliaryServiceState().GetDictOrEmpty("job")));
            }
            float deadline = Time.realtimeSinceStartup + 100;
            while (_s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetString("completion_commit_id").Length == 0 && !_s.SliceComplete && Time.realtimeSinceStartup < deadline)
            {
                if (_s.VitalsState.Stamina < 25 && _s.AuxiliaryWorkRunning)
                { _s.EndWorkHold(); _s.PauseAuxiliaryService("rest"); }
                if (!_s.AuxiliaryWorkRunning && _s.VitalsState.Stamina >= 75)
                { _s.BeginWorkHold(); _boot.Host.SceneState.Player.RequestInteract(); }
                float frameStart=Time.realtimeSinceStartup;
                yield return null;
                _auxWorkingFrameMilliseconds.Add((Time.realtimeSinceStartup-frameStart)*1000.0);
            }
            _s.EndWorkHold(); _auxMeasuring=false;
            Debug.Log("[AuxiliaryWorkGate] id="+id+" handler="+_s.LastInteractHandlerId+" moving="+_boot.Host.SceneState.Player.IsMoving()+" stamina="+_s.VitalsState.Stamina+" work_tick "+AuxiliaryLatencySummary(_auxWorkTickMilliseconds)+" work_rest_frame "+AuxiliaryLatencySummary(_auxWorkingFrameMilliseconds)+" job="+GdJson.Stringify(_s.GetAuxiliaryServiceState().GetDictOrEmpty("job")));
            Assert.IsFalse(_s.SliceComplete, "survive eligible work and stationary rest");
            Assert.IsNotEmpty(_s.GetAuxiliaryServiceState().GetDictOrEmpty("services").GetDictOrEmpty(id).GetString("completion_commit_id"), GdJson.Stringify(_s.GetAuxiliaryServiceState()));
        }

        const string AuxiliaryCaptureEnv = "SYNAPTIC_AUXILIARY_CHECKPOINT_CAPTURE_DIR";
        const string AuxiliaryReplayEnv = "SYNAPTIC_AUXILIARY_CHECKPOINT_DIR";
        static readonly System.Text.UTF8Encoding AuxiliaryUtf8 = new System.Text.UTF8Encoding(false, true);
        static GdArray AuxiliaryPosition(Vec3 position) => new GdArray { position.X, position.Y, position.Z };
        static string AuxiliaryHash(byte[] bytes)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return System.BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        static bool AuxiliaryRelativePath(string path) => !string.IsNullOrEmpty(path) && !System.IO.Path.IsPathRooted(path)
            && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0 && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        static string AuxiliaryReadVerified(string directory, string relative, string hash)
        {
            Assert.IsTrue(AuxiliaryRelativePath(relative), "checkpoint path must stay in its owned directory");
            byte[] bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory, relative));
            Assert.AreEqual(hash, AuxiliaryHash(bytes), "immutable checkpoint bytes: " + relative);
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf, "checkpoint UTF8 must be BOM-free");
            return AuxiliaryUtf8.GetString(bytes);
        }
        static GdDict AuxiliaryLaunchMetadata(RunLaunchRequest request) => new GdDict {
            { "mode", request.Mode.ToString() }, { "slot_id", request.SlotId }, { "class_id", request.ClassId },
            { "seed", request.Seed }, { "biome_id", request.BiomeId }, { "difficulty_id", request.DifficultyId },
            { "layout_override_path", request.LayoutOverridePath }, { "enable_auxiliary_services", request.EnableAuxiliaryServices },
            { "enable_manual_study", request.EnableManualStudy }, { "enable_component_integration", request.EnableComponentIntegration } };
        GdDict AuxiliaryOrdinaryProbeState(string phase, BreachSealPoint intended)
        {
            Vec3 position = _boot.Host.SceneState.Player.GodotPosition;
            var repairs = new GdArray(); var seals = new GdArray();
            var precheck = typeof(RepairPoint).GetMethod("PrecheckReason", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(precheck);
            foreach (var point in _s.RepairPoints.OrderBy(p => p.GlobalPosition.DistanceSquaredTo(position)))
            {
                var sub = point.TargetManager?.GetSystem(point.SystemId)?.GetSubcomponent(point.SubcomponentId);
                var parts = new GdDict(); var tools = new GdDict();
                if (sub != null)
                {
                    foreach (string part in sub.RequiredParts) parts[part] = _s.InventoryState.GetQuantity(part);
                    foreach (string tool in sub.RequiredTools) tools[tool] = _s.InventoryState.GetQuantity(tool);
                }
                repairs.Add(new GdDict { { "source_order_index", _s.RepairPoints.IndexOf(point) }, { "id", point.SystemId + "." + point.SubcomponentId }, { "node_name", point.NodeName },
                    { "position", AuxiliaryPosition(point.GlobalPosition) }, { "distance_squared", point.GlobalPosition.DistanceSquaredTo(position) },
                    { "strict_range", point.IsPlayerInDirectRangeStrict(position) }, { "valid", point.IsValid }, { "inside_tree", point.IsInsideTree },
                    { "repaired", point.Repaired }, { "channeling", point.Channeling }, { "can_begin", point.CanBeginRepair() },
                    { "can_focus", _s.CanFocusInteractable(point) }, { "precheck_reason", sub == null ? "missing_subcomponent" : (string)precheck.Invoke(point, new object[] { sub, _s.PlayerProgression.GetSkillLevel("repair") }) },
                    { "manager_binding", ReferenceEquals(point.TargetManager, _s.ShipSystemsManager) }, { "min_skill", point.MinSkill }, { "part_quantities", parts }, { "tool_quantities", tools },
                    { "functional", sub?.IsFunctional() ?? false }, { "inventory_binding", ReferenceEquals(point.InventoryState, _s.InventoryState) },
                    { "progression_binding", ReferenceEquals(point.PlayerProgression, _s.PlayerProgression) } });
            }
            foreach (var point in _s.BreachSealPoints.OrderBy(p => p.GlobalPosition.DistanceSquaredTo(position)))
                seals.Add(new GdDict { { "source_order_index", _s.BreachSealPoints.IndexOf(point) }, { "id", point.CompartmentId }, { "node_name", point.NodeName },
                    { "position", AuxiliaryPosition(point.GlobalPosition) }, { "distance_squared", point.GlobalPosition.DistanceSquaredTo(position) },
                    { "strict_range", point.IsPlayerInDirectRangeStrict(position) }, { "valid", point.IsValid }, { "inside_tree", point.IsInsideTree },
                    { "can_focus", _s.CanFocusInteractable(point) }, { "sealed", point.Sealed }, { "channeling", point.Channeling }, { "required_item", point.RequiredItem },
                    { "required_item_quantity", point.InventoryState?.GetQuantity(point.RequiredItem) ?? 0 },
                    { "breach_open", point.HullState?.Compartments.GetDictOrEmpty(point.CompartmentId).GetBool("breach_open") ?? false },
                    { "hull_binding", ReferenceEquals(point.HullState, _s.HullIntegrityState) },
                    { "inventory_binding", ReferenceEquals(point.InventoryState, _s.InventoryState) } });
            return new GdDict { { "phase", phase }, { "intended_caller", "AuxiliaryHomeRoute.cargo_seal" },
                { "intended_target", intended?.CompartmentId ?? "cargo" }, { "intended_position", intended == null ? new GdArray() : AuxiliaryPosition(intended.GlobalPosition) },
                { "cargo_hull", _s.HullIntegrityState.Compartments.GetDictOrEmpty("cargo").DeepCopy() },
                { "player_position", AuxiliaryPosition(position) }, { "focused_kind", _boot.Host.FocusedView?.Model?.Kind ?? "" },
                { "focused_node", _boot.Host.FocusedView?.Model?.NodeName ?? "" }, { "los_probe_has_space", _s.Deps.LosProbe?.HasSpace ?? false }, { "actual_handler", _s.LastInteractHandlerId ?? "" },
                { "natural_channel_active", NaturalChannelActive() }, { "repair_skill", _s.PlayerProgression.GetSkillLevel("repair") },
                { "inventory", _s.InventoryState.GetSummary() }, { "vitals", _s.VitalsState.GetSummary() },
                { "repair_candidates", repairs }, { "seal_candidates", seals } };
        }
        void AuxiliaryCaptureCheckpoint(string directory, string classId, string boundary = "post_racks_saved_and_continued_before_ordinary_channel")
        {
            Assert.IsFalse(System.IO.Directory.Exists(directory) || System.IO.File.Exists(directory), "capture destination must be new; existing evidence is immutable");
            string revision = System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_CHECKPOINT_REVISION");
            Assert.IsTrue(revision != null && revision.Length == 40 && revision.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "capture requires full source HEAD in SYNAPTIC_AUXILIARY_CHECKPOINT_REVISION");
            GdDict selected = _s.SaveLoadService.SelectGeneration("world"); Assert.IsTrue(selected.GetBool("ok"), GdJson.Stringify(selected));
            var intended = _s.BreachSealPoints.SingleOrDefault(point => point.CompartmentId == "cargo");
            var request = new RunLaunchRequest { Mode = RunLaunchMode.Continue, SlotId = RunLaunchRequest.WorldSlotId,
                ClassId = classId, Seed = _boot.Launch.Seed, BiomeId = _boot.Launch.BiomeId, DifficultyId = _boot.Launch.DifficultyId,
                LayoutOverridePath = _boot.Launch.LayoutOverridePath, EnableAuxiliaryServices = true };
            var metadata = new GdDict { { "schema_version", 1L }, { "boundary", boundary },
                { "source_commit", revision }, { "test_source_sha256", System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_CHECKPOINT_TEST_SOURCE_SHA256") ?? "" }, { "unity_version", Application.unityVersion }, { "application_version", Application.version },
                { "run_id", selected.GetString("run_id") }, { "generation_id", selected.GetString("generation_id") },
                { "generation_manifest_sha256", selected.GetString("manifest_sha256") }, { "initial_launch", AuxiliaryLaunchMetadata(_boot.Launch) },
                { "continue_launch", AuxiliaryLaunchMetadata(request) }, { "world_time", _s.WorldTime },
                { "progression", _s.PlayerProgression.GetSummary() }, { "auxiliary_state", _s.GetAuxiliaryServiceState() },
                { "boundary_state", AuxiliaryOrdinaryProbeState("checkpoint_boundary", intended) } };
            var catalogHashes = new GdArray();
            foreach (string resource in new[] { "res://data/items/item_definitions.json", "res://data/tools/tool_definitions.json", "res://data/player/classes.json", "res://data/player/skills.json", "res://data/player/skill_books.json", "res://data/ship_systems/systems.json", "res://data/work_actions/work_action_catalog.json" })
            {
                Assert.IsTrue(SynapticSea.Core.Services.CoreServices.Resources.Exists(resource), resource);
                catalogHashes.Add(new GdDict { { "logical_path", resource }, { "sha256", AuxiliaryHash(AuxiliaryUtf8.GetBytes(SynapticSea.Core.Services.CoreServices.Resources.ReadText(resource))) } });
            }
            metadata["catalog_hashes"] = catalogHashes;
            metadata["runtime"] = new GdDict { { "framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
                { "architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() },
                { "engine_version", _s.Deps.Engine.VersionString }, { "core_mvid", typeof(RunSession).Assembly.ManifestModule.ModuleVersionId.ToString() },
                { "runtime_mvid", typeof(RunSessionHost).Assembly.ManifestModule.ModuleVersionId.ToString() },
                { "test_mvid", typeof(RunLifecyclePlayModeTests).Assembly.ManifestModule.ModuleVersionId.ToString() } };
            System.IO.Directory.CreateDirectory(directory);
            var rows = new GdArray(); var dirs = new GdArray();
            void ExportDirectory(string relative)
            {
                foreach (string name in _storage.ListFiles(relative))
                {
                    string storagePath = relative.Length == 0 ? name : relative + "/" + name;
                    Assert.IsTrue(AuxiliaryRelativePath(storagePath));
                    string artifact = "files/" + storagePath; byte[] bytes = AuxiliaryUtf8.GetBytes(_storage.ReadText(storagePath));
                    string target = System.IO.Path.Combine(directory, artifact); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    System.IO.File.WriteAllBytes(target, bytes);
                    rows.Add(new GdDict { { "storage_path", storagePath }, { "file", artifact }, { "bytes", (long)bytes.Length }, { "sha256", AuxiliaryHash(bytes) } });
                }
                foreach (string name in _storage.ListDirectories(relative))
                {
                    string child = relative.Length == 0 ? name : relative + "/" + name; Assert.IsTrue(AuxiliaryRelativePath(child)); dirs.Add(child); ExportDirectory(child);
                }
            }
            ExportDirectory("");
            byte[] selectionBytes = AuxiliaryUtf8.GetBytes(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(selected));
            byte[] metadataBytes = AuxiliaryUtf8.GetBytes(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(metadata));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "selection.json"), selectionBytes);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "metadata.json"), metadataBytes);
            var manifest = new GdDict { { "schema_version", 1L }, { "source_commit", revision }, { "files", rows }, { "directories", dirs },
                { "selection_file", "selection.json" }, { "selection_sha256", AuxiliaryHash(selectionBytes) },
                { "metadata_file", "metadata.json" }, { "metadata_sha256", AuxiliaryHash(metadataBytes) },
                { "generation_id", selected.GetString("generation_id") }, { "generation_manifest_sha256", selected.GetString("manifest_sha256") } };
            byte[] manifestBytes = AuxiliaryUtf8.GetBytes(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(manifest));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "manifest.json"), manifestBytes);
            Debug.Log("[AuxiliaryCheckpointCaptured] directory=" + directory + " manifest_sha256=" + AuxiliaryHash(manifestBytes)
                + " generation=" + selected.GetString("generation_id") + " files=" + rows.Count + " boundary=" + GdJson.Stringify(metadata.GetDictOrEmpty("boundary_state")));
        }
        [UnityTest, Explicit("Requires an immutable captured auxiliary checkpoint and pinned manifest environment variables"), Timeout(120000)]
        public IEnumerator ContinueAuxiliaryCheckpointAndProbeFirstOrdinaryChannel()
        {
            yield return AuxiliaryBootImmutableCheckpoint("post_racks_saved_and_continued_before_ordinary_channel");
            yield return AuxiliaryProbeSealAndCapture();
        }

        IEnumerator AuxiliaryBootImmutableCheckpoint(string expectedBoundary)
        {
            string directory = System.Environment.GetEnvironmentVariable(AuxiliaryReplayEnv);
            Assert.IsFalse(string.IsNullOrEmpty(directory), "explicit owned checkpoint directory required in " + AuxiliaryReplayEnv);
            string manifestHash = System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_CHECKPOINT_MANIFEST_SHA256");
            Assert.IsFalse(string.IsNullOrEmpty(manifestHash), "pin exact checkpoint manifest SHA256 before replay");
            var manifest = SynapticSea.Core.Systems.PaidSnapshotCodec.Parse(AuxiliaryReadVerified(directory, "manifest.json", manifestHash));
            Assert.AreEqual(1, manifest.GetInt("schema_version"));
            var selected = SynapticSea.Core.Systems.PaidSnapshotCodec.Parse(AuxiliaryReadVerified(directory, manifest.GetString("selection_file"), manifest.GetString("selection_sha256")));
            var metadata = SynapticSea.Core.Systems.PaidSnapshotCodec.Parse(AuxiliaryReadVerified(directory, manifest.GetString("metadata_file"), manifest.GetString("metadata_sha256")));
            Assert.AreEqual(expectedBoundary, metadata.GetString("boundary"), "exact checkpoint lifecycle boundary");
            Assert.AreEqual(manifest.GetString("generation_id"), selected.GetString("generation_id"));
            Assert.AreEqual(manifest.GetString("generation_manifest_sha256"), selected.GetString("manifest_sha256"));
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var imported = new Dictionary<string,string>(System.StringComparer.Ordinal);
            var importedDirs = new List<string>();
            foreach (object value in manifest.GetArrayOrEmpty("directories"))
            { string path = V.Str(value); Assert.IsTrue(AuxiliaryRelativePath(path)); importedDirs.Add(path); }
            foreach (GdDict row in manifest.GetArrayOrEmpty("files"))
            {
                string path = row.GetString("storage_path"); Assert.IsTrue(AuxiliaryRelativePath(path) && seen.Add(path));
                string text = AuxiliaryReadVerified(directory, row.GetString("file"), row.GetString("sha256"));
                Assert.AreEqual(row.GetInt("bytes"), AuxiliaryUtf8.GetByteCount(text)); imported.Add(path, text);
            }
            // Validate every owned file before importing any bytes into the new MemoryStorage.
            foreach (string directoryPath in importedDirs) _storage.MakeDirRecursive(directoryPath);
            foreach (var row in imported) _storage.WriteText(row.Key, row.Value);
            var launch = metadata.GetDictOrEmpty("continue_launch");
            Assert.AreEqual("Continue", launch.GetString("mode")); Assert.AreEqual("world", launch.GetString("slot_id"));
            Assert.IsTrue(launch.GetBool("enable_auxiliary_services"));
            // Compare both reader policies with the same immutable package and normal app ports.
            // Neither returned reader is used to substitute for normal bootstrap admission.
            SynapticSea.App.AppServices.Ensure();
            bool legacyReaderOk = SynapticSea.Core.Systems.SaveGenerationArtifacts.TryCreateReader(selected,
                SynapticSea.Core.Services.CoreServices.Resources, out var legacyReader, out string legacyReaderReason);
            string expectedMode = launch.GetBool("enable_component_integration")
                ? SynapticSea.Core.Systems.PaidSnapshotCodec.DiagnosticMode : SynapticSea.Core.Systems.PaidSnapshotCodec.OrdinaryMode;
            bool paidReaderOk = SynapticSea.Core.Systems.SaveGenerationArtifacts.TryCreateReader(selected,
                SynapticSea.Core.Services.CoreServices.Resources, out var paidReader, out string paidReaderReason, true, expectedMode);
            var package = selected.GetDictOrEmpty("payloads");
            Debug.Log("[AuxiliaryReaderPolicyProbe] " + GdJson.Stringify(new GdDict {
                { "run_id", selected.GetString("run_id") }, { "generation_id", selected.GetString("generation_id") },
                { "selected_mode", selected.GetString("save_mode") }, { "expected_mode", expectedMode },
                { "legacy_reader_ok", legacyReaderOk }, { "legacy_reason", legacyReaderReason },
                { "paid_reader_ok", paidReaderOk }, { "paid_reason", paidReaderReason },
                { "selected_payload_sha256", selected.GetString("payloads_sha256") },
                { "legacy_payload_sha256", SynapticSea.Core.Systems.SaveGenerationArtifacts.Hash(GdJson.Stringify(package)) },
                { "paid_payload_sha256", SynapticSea.Core.Systems.SaveGenerationArtifacts.Hash(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(package)) } }));
            _recordJourneyTelemetry = true;
            yield return BootPlayable(new RunLaunchRequest { Mode = RunLaunchMode.Continue, SlotId = launch.GetString("slot_id"),
                ClassId = launch.GetString("class_id"), Seed = launch.GetInt("seed"), BiomeId = launch.GetString("biome_id"), DifficultyId = launch.GetString("difficulty_id"),
                LayoutOverridePath = launch.GetString("layout_override_path"), EnableAuxiliaryServices = true,
                EnableManualStudy = launch.GetBool("enable_manual_study"), EnableComponentIntegration = launch.GetBool("enable_component_integration"), SelectedSaveGeneration = selected });
            // Normal scene boot installs resource/catalog ports before typed-owner admission can validate.
            var reread = _s.SaveLoadService.ReadGeneration(selected.GetString("run_id"), selected.GetString("slot_id"), selected.GetString("generation_id"), selected.GetString("manifest_sha256"));
            Assert.IsTrue(reread.GetBool("ok"), GdJson.Stringify(reread));
            foreach (string key in new[] { "run_id", "slot_id", "generation_id", "manifest_sha256" }) Assert.AreEqual(selected.GetString(key), reread.GetString(key), key);
            Assert.IsTrue(V.VariantEquals(selected.GetDictOrEmpty("payloads"), reread.GetDictOrEmpty("payloads")), "normal ReadGeneration must verify the complete selected package after raw import");
            foreach (GdDict catalog in metadata.GetArrayOrEmpty("catalog_hashes"))
                Assert.AreEqual(catalog.GetString("sha256"), AuxiliaryHash(AuxiliaryUtf8.GetBytes(SynapticSea.Core.Services.CoreServices.Resources.ReadText(catalog.GetString("logical_path")))), "same catalog bytes on checkpoint replay");
            var actual = _s.SaveLoadService.SelectGeneration("world");
            Assert.IsTrue(actual.GetBool("ok")); Assert.AreEqual(selected.GetString("generation_id"), actual.GetString("generation_id"));
            Assert.AreEqual(selected.GetString("manifest_sha256"), actual.GetString("manifest_sha256"));
            Assert.IsTrue(V.VariantEquals(selected.GetDictOrEmpty("payloads"), actual.GetDictOrEmpty("payloads")), "Continue kept the exact selected payload package");
        }

        IEnumerator AuxiliaryProbeSealAndCapture()
        {
            var intended = _s.BreachSealPoints.Single(point => point.CompartmentId == "cargo" && !point.Sealed);
            yield return AuxiliaryDeck(intended.GlobalPosition.Y > 3 ? 1 : 0);
            yield return WalkTo(intended.GlobalPosition, 1.6f);
            yield return FixedSteps(2);
            var player = _boot.Host.SceneState.Player;
            Assert.IsTrue(intended.IsPlayerInDirectRangeStrict(player.GodotPosition), "ordinary strict seal reach");
            Assert.IsTrue(_s.RepairPoints.Any(point => point.IsPlayerInDirectRangeStrict(player.GodotPosition)),
                "the selection regression retains the proven competing repair overlap");
            var floor = SpawnClearance.FloorUnder(player.transform.position);
            Assert.IsNotNull(floor, "physical floor supports the actual standing spot");
            var controller = player.GetComponent<CharacterController>();
            Assert.IsNotNull(controller); Assert.IsTrue(controller.isGrounded, "collision-authoritative player is grounded");
            Assert.IsTrue(_s.CanFocusInteractable(intended), "production focus reach and LOS admit the intended seal");
            Debug.Log("[AuxiliarySealStandingProof] " + GdJson.Stringify(new GdDict {
                { "player", new GdArray { player.GodotPosition.X, player.GodotPosition.Y, player.GodotPosition.Z } },
                { "floor", floor.name }, { "grounded", controller.isGrounded }, { "production_focus_los", true },
                { "repair_strict_range_count", _s.RepairPoints.Count(point => point.IsPlayerInDirectRangeStrict(player.GodotPosition)) } }));
            Assert.AreEqual(1, _s.PlayerProgression.GetSkillLevel("repair"));
            Assert.AreEqual(188, _s.PlayerProgression.GetSkillXp("repair"), "immutable Cook checkpoint before exact 12 XP seal reward");
            long sealantBefore = _s.InventoryState.GetQuantity(intended.RequiredItem);
            int sealCompletions = 0;
            System.Action<string> completed = id => { Assert.AreEqual("cargo", id); sealCompletions++; };
            intended.BreachSealed += completed;
            var events = new GdArray(); var detach = new List<System.Action>();
            foreach (var point in _s.RepairPoints)
            {
                var captured = point; System.Action<string,string,string> blocked = (system, sub, reason) => events.Add(new GdDict { { "kind", "repair_blocked" }, { "id", system + "." + sub }, { "reason", reason } });
                System.Action<string,string> started = (system, sub) => events.Add(new GdDict { { "kind", "repair_started" }, { "id", system + "." + sub } });
                point.RepairBlocked += blocked; point.RepairStarted += started;
                detach.Add(() => { captured.RepairBlocked -= blocked; captured.RepairStarted -= started; });
            }
            foreach (var point in _s.BreachSealPoints)
            {
                var captured = point; System.Action<string,string> blocked = (id, reason) => events.Add(new GdDict { { "kind", "seal_blocked" }, { "id", id }, { "reason", reason } });
                point.SealBlocked += blocked; detach.Add(() => captured.SealBlocked -= blocked);
            }
            try
            {
                Debug.Log("[AuxiliaryOrdinaryProbeBefore] " + GdJson.Stringify(AuxiliaryOrdinaryProbeState("before_request", intended)));
                var gravity = _s.RepairPoints.Single(point => point.SystemId == "gravity" && point.SubcomponentId == "field_emitter");
                Assert.IsTrue(gravity.IsPlayerInDirectRangeStrict(player.GodotPosition), "proven unavailable repair remains physically overlapping");
                var inventoryBeforeDenial = _s.InventoryState.GetSummary().DeepCopy();
                var progressionBeforeDenial = _s.PlayerProgression.GetSummary().DeepCopy();
                AuxiliaryOpenWorkPickerFromInventory();
                Assert.IsFalse(NaturalChannelActive(), "opening the chooser never confirms work");
                AuxiliarySelectWorkRow(gravity);
                StringAssert.Contains("insufficient_skill", _boot.Ui.NearbyWorkPicker.DetailText);
                AuxiliarySubmit(_boot.Ui.NearbyWorkPicker.List.RowAt(_boot.Ui.NearbyWorkPicker.List.SelectedIndex));
                Assert.IsTrue(_boot.Ui.NearbyWorkPicker.IsOpen(), "denied selection remains visible for feedback");
                StringAssert.Contains("insufficient_skill", _boot.Ui.NearbyWorkPicker.StatusDisplay);
                Assert.IsTrue(events.Cast<GdDict>().Any(row => row.GetString("kind") == "repair_blocked" && row.GetString("id") == "gravity.field_emitter" && row.GetString("reason") == "insufficient_skill"));
                Assert.IsFalse(NaturalChannelActive(), "deliberately unavailable repair cannot fall back to the seal");
                Assert.IsTrue(V.VariantEquals(inventoryBeforeDenial, _s.InventoryState.GetSummary()), "repair denial changes no inventory");
                Assert.IsTrue(V.VariantEquals(progressionBeforeDenial, _s.PlayerProgression.GetSummary()), "repair denial awards no XP");
                _boot.Ui.NearbyWorkPicker.Close();
                Assert.IsFalse(NaturalChannelActive(), "cancel starts no work");
                AuxiliaryOpenWorkPickerFromInventory();
                Assert.IsFalse(NaturalChannelActive(), "reopening starts no work");
                AuxiliarySelectWorkRow(intended);
                yield return CaptureHud("work-picker-cargo-selection.png");
                AuxiliarySubmit(_boot.Ui.NearbyWorkPicker.List.RowAt(_boot.Ui.NearbyWorkPicker.List.SelectedIndex));
                Assert.IsFalse(_boot.Ui.NearbyWorkPicker.IsOpen(), "successful player selection returns to world work");
                Assert.IsTrue(NaturalChannelActive(), "explicit player selection must start the selected seal channel");
                Assert.IsTrue(intended.Channeling, "cargo seal alone receives confirmation");
                Assert.IsFalse(_s.RepairPoints.Any(point => point.Channeling), "selection starts no competing repair");
                Debug.Log("[AuxiliaryPickerSelection] " + GdJson.Stringify(new GdDict { { "selected", "seal:cargo" }, { "events", events.DeepCopy() },
                    { "inventory_before", inventoryBeforeDenial }, { "progression_before", progressionBeforeDenial }, { "state", AuxiliaryOrdinaryProbeState("explicit_seal_started", intended) } }));
                float completionDeadline = Time.realtimeSinceStartup + 30f;
                while (intended.Channeling)
                {
                    Assert.Less(Time.realtimeSinceStartup, completionDeadline, "ordinary Process must complete the four-second seal channel");
                    Assert.IsFalse(_s.SliceComplete, "player survives normal seal work");
                    yield return null;
                }
                Assert.IsTrue(intended.Sealed); Assert.AreEqual(1, sealCompletions);
                Assert.AreEqual(sealantBefore - 1, _s.InventoryState.GetQuantity(intended.RequiredItem), "exact one sealant consumed");
                Assert.AreEqual(2, _s.PlayerProgression.GetSkillLevel("repair"));
                Assert.AreEqual(0, _s.PlayerProgression.GetSkillXp("repair"), "Cook repair 188 + 12 reaches level 2 exactly");
                var completedInventory = _s.InventoryState.GetSummary().DeepCopy();
                var completedProgression = _s.PlayerProgression.GetSummary().DeepCopy();
                AuxiliaryOpenWorkPickerFromInventory();
                for (int row = 0; row < _boot.Ui.NearbyWorkPicker.List.Count; row++)
                {
                    Assert.IsFalse(ReferenceEquals(_boot.Ui.NearbyWorkPicker.SelectedTarget, intended), "completed seal is not an available chooser row");
                    _boot.Ui.NearbyWorkPicker.MoveSelection(1);
                }
                _boot.Ui.NearbyWorkPicker.Close();
                yield return FixedSteps(8);
                Assert.AreEqual(1, sealCompletions, "completed seal cannot award twice");
                Assert.IsFalse(NaturalChannelActive(), "repeat request cannot start another local channel");
                Assert.IsTrue(V.VariantEquals(completedInventory, _s.InventoryState.GetSummary()), "repeat preserves exact inventory");
                Assert.IsTrue(V.VariantEquals(completedProgression, _s.PlayerProgression.GetSummary()), "repeat preserves every skill reward");
                Debug.Log("[AuxiliaryOrdinaryProbeCompleted] " + GdJson.Stringify(AuxiliaryOrdinaryProbeState("completed_and_reopened", intended)));
            }
            finally { intended.BreachSealed -= completed; foreach (var unsubscribe in detach) unsubscribe(); }
            string postSealDirectory = System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_POST_SEAL_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(postSealDirectory))
            {
                Assert.IsTrue(_s.RequestSave(), GdJson.Stringify(_s.LastSaveResult));
                Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
                AuxiliaryAssertCargoClosedAfterRestore();
                Assert.AreEqual(2, _s.PlayerProgression.GetSkillLevel("repair"));
                Assert.AreEqual(0, _s.PlayerProgression.GetSkillXp("repair"));
                Assert.AreEqual(sealantBefore - 1, _s.InventoryState.GetQuantity(intended.RequiredItem));
                AuxiliaryCaptureCheckpoint(postSealDirectory, "cook", "post_seal_saved_and_continued_before_machinery");
            }
        }



        void AuxiliaryAssertCargoClosedAfterRestore()
        {
            Assert.IsTrue(_s.HullIntegrityState.Compartments.Has("cargo"), "authoritative cargo compartment remains after Continue");
            var cargo = _s.HullIntegrityState.Compartments.GetDictOrEmpty("cargo");
            Assert.IsTrue(cargo.Has("breach_open"), "closure field remains explicitly serialized");
            Assert.IsTrue(cargo["breach_open"] is bool && !(bool)cargo["breach_open"], "paid seal closure persisted as explicit false in authoritative hull");
            Debug.Log("[AuxiliaryRestoredCargoClosure] " + SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(cargo));
            Assert.IsFalse(_s.BreachSealPoints.Any(point => point.CompartmentId == "cargo" && !point.Sealed), "Continue creates no actionable cargo breach after closure");
        }

        IEnumerator AuxiliaryCutMooringOnActualDeck(SynapticSea.Core.Systems.ShipInstance ship)
        {
            _s.RebuildHomeJoinControls();
            var target = _s.HomeJoinControls.Single(control => control.ShipId == ship.ShipId && control.ActionId == "cut_web_attachment");
            Debug.Log("[AuxiliaryMooringApproach] " + GdJson.Stringify(new GdDict { { "ship", ship.ShipId },
                { "live_target", AuxiliaryPosition(target.GlobalPosition) }, { "player", AuxiliaryPosition(_boot.Host.SceneState.Player.GodotPosition) } }));
            yield return AuxiliaryDeck(target.GlobalPosition.Y > 3 ? 1 : 0);
            yield return CutBiomatterMooring(ship);
            AuxiliaryContinuationState("after_mooring_cut", ship.ShipId);
        }

        void AuxiliaryContinuationState(string stage, string detail = "")
        {
            Debug.Log("[AuxiliaryContinuation] " + GdJson.Stringify(new GdDict {
                { "stage", stage }, { "detail", detail }, { "inventory", _s.InventoryState.GetSummary() },
                { "progression", _s.PlayerProgression.GetSummary() }, { "vitals", _s.VitalsState.GetSummary() },
                { "world_time", _s.WorldTime }, { "travel_capability", _s.TravelCapability() },
                { "away", _s.AwayFromStart }, { "handler", _s.LastInteractHandlerId },
                { "player", new GdArray { _boot.Host.SceneState.Player.GodotPosition.X, _boot.Host.SceneState.Player.GodotPosition.Y, _boot.Host.SceneState.Player.GodotPosition.Z } } }));
        }

        [UnityTest, Explicit("Requires pinned immutable post-seal checkpoint; no utility prefix"), Timeout(300000)]
        public IEnumerator ContinueAuxiliaryPostSealCheckpointThroughEarnedDepartureAndReturn()
        {
            yield return AuxiliaryBootImmutableCheckpoint("post_seal_saved_and_continued_before_machinery");
            AuxiliaryAssertCargoClosedAfterRestore();
            Assert.AreEqual(2, _s.PlayerProgression.GetSkillLevel("repair"));
            Assert.AreEqual(0, _s.PlayerProgression.GetSkillXp("repair"));
            Assert.AreEqual(5, _s.InventoryState.GetQuantity("hull_sealant"));
            AuxiliaryContinuationState("post_seal_admitted");
            foreach (string subId in new[] { "star_charts", "nav_linkage" })
            {
                var repair = _s.RepairPoints.Single(point => point.IsValid && !point.Repaired && point.SubcomponentId == subId);
                AuxiliaryContinuationState("before_repair", repair.SystemId + "." + subId);
                Assert.LessOrEqual(repair.MinSkill, _s.PlayerProgression.GetSkillLevel("repair"), "genuine skill shortfall; do not grant XP");
                yield return AuxiliaryDeck(repair.GlobalPosition.Y > 3 ? 1 : 0);
                yield return WalkTo(repair, 1.6f);
                Assert.IsTrue(_s.CanFocusInteractable(repair), "actual standing, range and LOS admit repair");
                var sub = repair.TargetManager.GetSystem(repair.SystemId).GetSubcomponent(subId);
                var partsBefore = sub.RequiredParts.ToDictionary(part => part, part => _s.InventoryState.GetQuantity(part));
                long xpBefore = _s.PlayerProgression.GetSkillXp("repair");
                long crossTrainingBefore = _s.PlayerProgression.CrossTraining.GetInt("repair");
                int trainingLogBefore = _s.TrainingEventBus.GetLog().Count;
                int completions = 0;
                System.Action<string,string> completed = (system, component) => completions++;
                repair.RepairCompleted += completed;
                try
                {
                    AuxiliaryOpenWorkPickerFromInventory(); AuxiliarySelectWorkRow(repair);
                    Debug.Log("[AuxiliaryRepairSelection] " + repair.NodeName + " " + _boot.Ui.NearbyWorkPicker.DetailText);
                    AuxiliarySubmit(_boot.Ui.NearbyWorkPicker.List.RowAt(_boot.Ui.NearbyWorkPicker.List.SelectedIndex));
                    AuxiliaryContinuationState("repair_confirmation", _boot.Ui.NearbyWorkPicker.StatusDisplay);
                    Assert.IsTrue(repair.Channeling, "exact paid repair refused; inspect chooser denial and state, no fallback");
                    float deadline = Time.realtimeSinceStartup + 60f;
                    while (repair.Channeling && !_s.SliceComplete && Time.realtimeSinceStartup < deadline) yield return null;
                    Assert.IsFalse(_s.SliceComplete); Assert.IsTrue(repair.Repaired); Assert.AreEqual(1, completions);
                    foreach (var part in partsBefore) Assert.AreEqual(part.Value - 1, _s.InventoryState.GetQuantity(part.Key), "exact machinery part payment: " + part.Key);
                    var repairEvents = _s.TrainingEventBus.GetLog().Cast<GdDict>().Skip(trainingLogBefore)
                        .Where(row => row.GetString("event_id") == "repair_subcomponent" && row.GetString("target_id") == repair.SystemId + "." + subId).ToArray();
                    Assert.AreEqual(1, repairEvents.Length, "one ordinary completion training event");
                    Assert.AreEqual(50, repairEvents[0].GetInt("base_xp"));
                    Assert.IsTrue(repairEvents[0].GetBool("is_cross_training")); Assert.IsFalse(repairEvents[0].GetBool("gated"));
                    Assert.AreEqual(crossTrainingBefore + 50, _s.PlayerProgression.CrossTraining.GetInt("repair"));
                    Assert.AreEqual(xpBefore + 40, _s.PlayerProgression.GetSkillXp("repair"), "existing direct 25*.8 plus training 50*.5*.8 Cook XP");
                    Debug.Log("[AuxiliaryRepairReward] " + SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(repairEvents[0]));
                    yield return FixedSteps(4); Assert.AreEqual(1, completions);
                    Assert.AreEqual(xpBefore + 40, _s.PlayerProgression.GetSkillXp("repair"), "no repeated repair reward");
                    Assert.AreEqual(crossTrainingBefore + 50, _s.PlayerProgression.CrossTraining.GetInt("repair"));
                }
                finally { repair.RepairCompleted -= completed; }
                AuxiliaryContinuationState("after_repair", subId);
            }
            Assert.IsTrue(_s.ShipSystemsManager.IsOperational("navigation"));
            Assert.IsTrue(_s.ShipSystemsManager.IsOperational("propulsion"));
            var bench = _s.CraftingStations.Single(c => c.StationKind == "workbench");
            yield return AuxiliaryDeck(bench.GlobalPosition.Y > 3 ? 1 : 0); yield return WalkTo(bench, 1.1f);
            var benchEvents = new GdArray();
            System.Action<string> requested = kind => benchEvents.Add(new GdDict { { "kind", "recipe_requested" }, { "station", kind } });
            System.Action<string,string> blocked = (kind, reason) => benchEvents.Add(new GdDict { { "kind", "craft_blocked" }, { "station", kind }, { "reason", reason } });
            bench.RecipePickerRequested += requested; bench.CraftBlocked += blocked;
            void LogBench(string phase)
            {
                Debug.Log("[AuxiliaryBenchInteraction] " + GdJson.Stringify(new GdDict {
                    { "phase", phase }, { "bench", bench.NodeName }, { "bench_position", AuxiliaryPosition(bench.GlobalPosition) },
                    { "valid", bench.IsValid }, { "inside_tree", bench.IsInsideTree }, { "strict_range", bench.IsPlayerInDirectRangeStrict(_boot.Host.SceneState.Player.GodotPosition) },
                    { "can_focus", _s.CanFocusInteractable(bench) }, { "inventory_bound", ReferenceEquals(bench.InventoryState, _s.InventoryState) },
                    { "crafting_bound", bench.CraftingState != null }, { "powered", bench.Powered }, { "events", benchEvents.DeepCopy() },
                    { "recipe_open", _boot.Ui.RecipePicker.IsOpen() }, { "work_picker_open", _boot.Ui.NearbyWorkPicker.IsOpen() },
                    { "inventory_open", _s.Deps.UiState != null && _s.Deps.UiState.InventoryOpen }, { "scanner_open", _s.Deps.UiState != null && _s.Deps.UiState.ScannerOpen }, { "menus_closed", _s.Deps.UiState == null || _s.Deps.UiState.MenusClosed },
                    { "ordinary_state", AuxiliaryOrdinaryProbeState(phase, null) } }));
            }
            try
            {
                var benchInventoryBefore = _s.InventoryState.GetSummary().DeepCopy();
                var benchProgressionBefore = _s.PlayerProgression.GetSummary().DeepCopy();
                LogBench("before_bench_request");
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
                LogBench("after_bench_request");
                Assert.AreEqual("home_join", _s.LastInteractHandlerId, "unchanged ordinary E retains overlapping installation dispatch");
                Assert.IsFalse(_s.WorkActionDriver.IsWorking(), "unavailable installation starts no free work");
                Assert.IsFalse(_boot.Ui.RecipePicker.IsOpen()); Assert.AreEqual(0, benchEvents.Count);
                Assert.IsTrue(V.VariantEquals(benchInventoryBefore, _s.InventoryState.GetSummary()));
                Assert.IsTrue(V.VariantEquals(benchProgressionBefore, _s.PlayerProgression.GetSummary()));
                AuxiliaryOpenWorkPickerFromInventory(); AuxiliarySelectWorkRow(bench);
                AuxiliarySubmit(_boot.Ui.NearbyWorkPicker.List.RowAt(_boot.Ui.NearbyWorkPicker.List.SelectedIndex));
                yield return FixedSteps(2);
                LogBench("after_exact_bench_selection");
                Assert.IsFalse(_boot.Ui.NearbyWorkPicker.IsOpen());
                Assert.IsTrue(_boot.Ui.RecipePicker.IsOpen(), "explicit workbench choice opens normal recipe picker after chooser closes");
                Assert.IsTrue(_boot.Ui.Coordinator.Stack.IsTop(_boot.Ui.RecipePicker), "normal recipe surface owns top modal");
                Assert.IsNotNull(UiFocus.FocusedWithin(_boot.Ui.RecipePicker), "normal mounted recipe surface owns UI focus");
                Assert.AreEqual(1, benchEvents.Count);
                Assert.AreEqual("recipe_requested", ((GdDict)benchEvents[0]).GetString("kind"));
                Assert.AreEqual("workbench", ((GdDict)benchEvents[0]).GetString("station"));
                Assert.IsFalse(bench.CraftingState.IsCrafting(), "choosing bench opens recipes without paid craft start");
                Assert.IsTrue(V.VariantEquals(benchInventoryBefore, _s.InventoryState.GetSummary()), "chooser opens no materials transaction");
                Assert.IsTrue(V.VariantEquals(benchProgressionBefore, _s.PlayerProgression.GetSummary()), "chooser awards no training");
            }
            finally { bench.RecipePickerRequested -= requested; bench.CraftBlocked -= blocked; }
            for (int n = 0; n < _boot.Ui.RecipePicker.List.Count && _boot.Ui.RecipePicker.GetSelectedId() != "craft_lockpick_set"; n++) _boot.Ui.RecipePicker.MoveSelection(1);
            Assert.AreEqual("craft_lockpick_set", _boot.Ui.RecipePicker.GetSelectedId());
            AuxiliaryContinuationState("before_paid_lockpick_craft");
            long scrapBeforeCraft = _s.InventoryState.GetQuantity("scrap_metal");
            Assert.GreaterOrEqual(scrapBeforeCraft, 2, "genuine material shortfall; no grants");
            Debug.Log("[AuxiliaryCraftSelection] " + GdJson.Stringify(new GdDict { { "recipe", _boot.Ui.RecipePicker.GetSelectedId() }, { "status", _boot.Ui.RecipePicker.GetStatus() }, { "rows", new GdArray(_boot.Ui.RecipePicker.GetStatusLines().Cast<object>()) } }));
            AuxiliarySubmit(_boot.Ui.RecipePicker.List.RowAt(_boot.Ui.RecipePicker.List.SelectedIndex));
            Debug.Log("[AuxiliaryCraftConfirmation] " + _boot.Ui.RecipePicker.GetStatus());
            float craftEnd = Time.realtimeSinceStartup + 45f;
            while (_s.InventoryState.GetQuantity("lockpick_set") == 0 && !_s.SliceComplete && Time.realtimeSinceStartup < craftEnd) yield return null;
            Assert.IsFalse(_s.SliceComplete); Assert.AreEqual(1, _s.InventoryState.GetQuantity("lockpick_set"));
            Assert.AreEqual(scrapBeforeCraft - 2, _s.InventoryState.GetQuantity("scrap_metal"), "paid lockpick exact two scrap input");
            AuxiliaryContinuationState("after_paid_lockpick_craft");
            yield return AuxiliaryManualStow("scrap_metal", "wiring_bundle", "circuit_board", "hull_sealant", "wrench", "fabrication_schematic_basic");
            AuxiliaryContinuationState("before_moorings");
            yield return AuxiliaryCutMooringOnActualDeck(_s.HomeShip); yield return AuxiliaryCutMooringOnActualDeck(_s.LifeboatShip);
            float propelEnd = Time.realtimeSinceStartup + 30f;
            while (!_s.PropulsionExpandedState.CanPropel() && !_s.SliceComplete && Time.realtimeSinceStartup < propelEnd) yield return null;
            AuxiliaryContinuationState("before_departure");
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel(), GdJson.Stringify(_s.TravelCapability()));
            var bridge = _s.BridgeTerminals.Single(t => t.ShipId == _s.PilotedShip.ShipId);
            yield return AuxiliaryDeck(bridge.GlobalPosition.Y > 3 ? 1 : 0); yield return WalkTo(bridge, 1.2f);
            _boot.Ui.OnPanelToggle("toggle_scanner"); Assert.IsTrue(_boot.Ui.Scanner.IsOpen()); Assert.Greater(_boot.Ui.Scanner.List.Count, 0);
            var travelResults = new List<GdDict>();
            System.Action<GdDict> travelResolved = result => travelResults.Add(result.DeepCopy());
            _boot.Ui.Scanner.TravelResolved += travelResolved;
            try
            {
                AuxiliarySubmit(_boot.Ui.Scanner.List.RowAt(_boot.Ui.Scanner.List.SelectedIndex));
                Assert.AreEqual(1, travelResults.Count, "one actual mounted scanner confirmation resolves once");
                var travel = travelResults[0];
                Debug.Log("[AuxiliaryActualTravelResult] " + GdJson.Stringify(new GdDict {
                    { "result", travel }, { "status", _boot.Ui.Scanner.GetStatus() }, { "selected_index", _boot.Ui.Scanner.GetSelectedIndex() },
                    { "current_ship_id", _s.CurrentShip?.ShipId ?? "" }, { "current_marker_id", _s.CurrentShip?.MarkerId ?? "" },
                    { "piloted_ship_id", _s.PilotedShip?.ShipId ?? "" }, { "home_ship_id", _s.HomeShip?.ShipId ?? "" },
                    { "away_context", _s.AwayFromStart }, { "world_position", AuxiliaryPosition(_s.SynapticSeaWorld.PlayerPosition) },
                    { "player_position", AuxiliaryPosition(_boot.Host.SceneState.Player.GodotPosition) } }));
                AuxiliaryContinuationState("scanner_confirmation_resolved", travel.GetString("reason"));
                Assert.IsTrue(travel.GetBool("success"), "actual travel refused; stop at exact content/resource gate: " + _boot.Ui.Scanner.GetStatus());
                Assert.AreNotSame(_s.HomeShip, _s.CurrentShip, "successful travel boards destination rather than home");
                Assert.AreNotSame(_s.LifeboatShip, _s.CurrentShip, "lifeboat away-context is not a departure witness");
            }
            finally { _boot.Ui.Scanner.TravelResolved -= travelResolved; }
            yield return FixedSteps(8); AuxiliaryContinuationState("departed");
            Assert.IsTrue(_s.AwayFromStart); Assert.IsFalse(_s.SliceComplete, "departure is not extraction");
            yield return CaptureHud("cook-checkpoint-earned-departure.png");
            var loader = _s.CurrentShip.SceneRoot as SynapticSea.Core.Session.IShipLoaderView;
            Assert.IsNotNull(loader);
            var exit = loader.GetAuthoredPortals().Where(portal => portal.IsValid && portal.IsExterior)
                .OrderBy(portal => portal.GlobalPosition.DistanceSquaredTo(_boot.Host.SceneState.Player.GodotPosition)).FirstOrDefault();
            Assert.IsNotNull(exit, "ordinary exterior return portal exists");
            yield return AuxiliaryDeck(exit.GlobalPosition.Y > 3 ? 1 : 0);
            yield return WalkTo(exit.GlobalPosition, 1.2f);
            Assert.IsTrue(exit.IsInRange(_boot.Host.SceneState.Player.GodotPosition));
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(8);
            AuxiliaryContinuationState("return_portal_requested", exit.PortalId);
            Assert.IsFalse(_s.AwayFromStart, "normal exterior interaction returns home; no direct TravelHome call");
            Assert.IsTrue(_s.RequestSave(), GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
            Assert.IsTrue(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId, "maintenance_fabricator_feed_01"));
            AuxiliaryContinuationState("returned_saved_and_continued");
            yield return CaptureHud("cook-checkpoint-earned-return.png");
        }

        [UnityTest, Explicit("Pinned immutable post-seal checkpoint; isolated first-away generation diagnosis"), Timeout(120000)]
        public IEnumerator DiagnoseAuxiliaryFirstAwayContractFromImmutableCheckpoint()
        {
            yield return AuxiliaryBootImmutableCheckpoint("post_seal_saved_and_continued_before_machinery");
            Assert.AreEqual(0, _s.VisitedShips.Count, "same first-away profile as refused earned route");
            var orderedMarkers = _s.SynapticSeaWorld.MarkersInRange(_s.ScannerState.RangeRadius);
            var marker = orderedMarkers.First();
            Assert.AreEqual(Vec3.Zero, _s.SynapticSeaWorld.PlayerPosition, "same sea position as recorded v7 first selection index zero");
            var contract = _s.FirstRunContract;
            Assert.IsNotNull(contract); Assert.IsFalse(contract.Contract.IsEmpty);
            var live = _s.ShipGenerator;
            var generator = new SynapticSea.Core.Procgen.ShipGenerator {
                DerelictSource = live.DerelictSource,
                EnableReviewedFrozenVersion4 = live.EnableReviewedFrozenVersion4,
                ExpeditionProfile = SynapticSea.Core.Procgen.ConstrainedExpedition.Profile, RichExpeditions = false };
            Assert.IsNull(live.DerelictSource, "unexpected external generator needs isolated provider-specific diagnosis, not shared mutable source");
            object[] liveStages = { live.LayoutGenerator.TemplateSelectorStage, live.LayoutGenerator.RoomAssignerStage, live.LayoutGenerator.CellLayoutEngineStage, live.LayoutGenerator.WallDoorResolverStage, live.LayoutGenerator.LayoutSerializerStage };
            object[] freshStages = { generator.LayoutGenerator.TemplateSelectorStage, generator.LayoutGenerator.RoomAssignerStage, generator.LayoutGenerator.CellLayoutEngineStage, generator.LayoutGenerator.WallDoorResolverStage, generator.LayoutGenerator.LayoutSerializerStage };
            for (int stage = 0; stage < liveStages.Length; stage++) Assert.AreEqual(freshStages[stage].GetType(), liveStages[stage].GetType(), "custom layout stage requires separate diagnosis");
            string liveBiome = live.BiomeId, liveDifficulty = live.DifficultyId, liveProfile = live.ExpeditionProfile;
            string liveLayoutBiome = live.LayoutGenerator.BiomeId, liveLayoutDifficulty = live.LayoutGenerator.DifficultyId;
            bool liveRich = live.RichExpeditions; var liveSelector = live.LayoutGenerator.VariantSelector;
            long originalMarkerSeed = marker.SeedValue;
            generator.ConfigureRunContext(contract.Contract.GetString("biome_id"), contract.Contract.GetString("difficulty_id"));
            string output = System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_CONTRACT_DIAGNOSTIC_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                Assert.IsFalse(System.IO.Directory.Exists(output) || System.IO.File.Exists(output), "diagnostic artifact destination must be fresh");
                System.IO.Directory.CreateDirectory(output);
            }
            var report = new GdDict { { "source_commit", System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_CHECKPOINT_REVISION") ?? "" },
                { "test_source_sha256", System.Environment.GetEnvironmentVariable("SYNAPTIC_AUXILIARY_CHECKPOINT_TEST_SOURCE_SHA256") ?? "" },
                { "resource_reader_type", SynapticSea.Core.Services.CoreServices.Resources.GetType().AssemblyQualifiedName },
                { "streaming_assets_path", Application.streamingAssetsPath },
                { "contract_catalog_sha256", AuxiliaryHash(AuxiliaryUtf8.GetBytes(SynapticSea.Core.Services.CoreServices.Resources.ReadText(SynapticSea.Core.Procgen.FirstRunContract.CONTRACT_PATH))) },
                { "room_variant_effects_sha256", AuxiliaryHash(AuxiliaryUtf8.GetBytes(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(SynapticSea.Core.Procgen.RoomVariantSelector.VARIANT_EFFECTS))) },
                { "marker_order", new GdArray(orderedMarkers.Select(item => new GdDict { { "marker_id", item.MarkerId }, { "size_class", item.SizeClass }, { "condition", item.Condition }, { "position", AuxiliaryPosition(item.Position) } })) },
                { "generator_type", live.GetType().AssemblyQualifiedName }, { "layout_generator_type", live.LayoutGenerator.GetType().AssemblyQualifiedName },
                { "layout_stage_types", new GdArray(liveStages.Select(stage => stage.GetType().AssemblyQualifiedName)) },
                { "variant_selector_type", liveSelector?.GetType().AssemblyQualifiedName ?? "null" },
                { "derelict_source_type", live.DerelictSource?.GetType().AssemblyQualifiedName ?? "null" },
                { "route", live.DerelictSource == null ? "ordinary_csharp_layout_pipeline" : "derelict_layout_source_worldgen" },
                { "enable_reviewed_frozen_version4", live.EnableReviewedFrozenVersion4 }, { "contract", contract.Contract.DeepCopy() },
                { "marker_id", marker.MarkerId }, { "size_class", marker.SizeClass }, { "condition", marker.Condition },
                { "world_position", AuxiliaryPosition(_s.SynapticSeaWorld.PlayerPosition) },
                { "generation_profile", generator.ExpeditionProfile }, { "rich_expeditions", generator.RichExpeditions } };
            var candidates = new GdArray();
            foreach (object preferred in contract.Contract.GetArrayOrEmpty("preferred_seeds"))
            {
                long seed = V.I64(preferred);
                var docs = generator.GenerateFromSeed(seed, marker.SizeClass, marker.Condition);
                Assert.IsNotNull(docs, "candidate generation returned no documents: " + seed);
                var layout = docs.Layout; var gameplay = docs.GameplaySlice;
                var hazards = new GdArray(); var available = new HashSet<string>();
                foreach (var pair in new[] { new KeyValuePair<string,GdDict>("layout", layout), new KeyValuePair<string,GdDict>("gameplay", gameplay) })
                    foreach (string kind in new[] { "fire_zone", "breach_zone" })
                    {
                        var rows = pair.Value.GetArrayOrEmpty(kind == "fire_zone" ? "fire_zones" : "breach_zones");
                        if (!rows.IsEmpty) available.Add(kind);
                        hazards.Add(new GdDict { { "source", pair.Key }, { "kind", kind }, { "count", rows.Count }, { "rows", rows.DeepCopy() } });
                    }
                var roles = new HashSet<string>(new[] { "bridge", "cockpit", "engineering", "reactor", "engine_bay", "hydroponics", "cargo", "storage" });
                var variants = new GdArray(); var selector = new SynapticSea.Core.Procgen.RoomVariantSelector();
                foreach (GdDict room in layout.GetArrayOrEmpty("rooms"))
                {
                    string role = room.GetString("room_role", room.GetString("role")); string variant = room.GetString("variant", "standard");
                    var effect = selector.EffectsFor(variant).GetDictOrEmpty("sim").GetDictOrEmpty("hazard");
                    string kind = effect.GetString("kind"); bool eligible = roles.Contains(role);
                    if (eligible && kind == "fire") available.Add("fire_zone");
                    if (eligible && kind == "breach") available.Add("breach_zone");
                    variants.Add(new GdDict { { "room_id", room.GetString("id") }, { "role", role }, { "variant", variant }, { "eligible_hazard_role", eligible }, { "effect", effect.DeepCopy() } });
                }
                long loot = gameplay.GetArrayOrEmpty("loot_containers").Count, encounters = layout.GetArrayOrEmpty("encounters").Count;
                bool hazardPass = contract.Contract.GetArrayOrEmpty("require_any").Cast<object>().Any(value => available.Contains(V.Str(value)));
                var checks = new GdDict {
                    { "layout_nonempty", !layout.IsEmpty }, { "gameplay_nonempty", !gameplay.IsEmpty },
                    { "biome", layout.Has("biome_id") && layout.GetString("biome_id") == contract.Contract.GetString("biome_id") },
                    { "difficulty", layout.Has("difficulty_id") && layout.GetString("difficulty_id") == contract.Contract.GetString("difficulty_id") },
                    { "loot_minimum", gameplay.Get("loot_containers") is GdArray && loot >= contract.Contract.GetInt("require_min_loot_containers") },
                    { "encounter_minimum", layout.Get("encounters") is GdArray && encounters >= contract.Contract.GetInt("require_min_encounters") },
                    { "required_hazard", hazardPass }, { "standing_start_to_goal", SynapticSea.Core.Procgen.FirstRunAwayGate.HasStandingStartToGoal(layout) },
                    { "objective_minimum", SynapticSea.Core.Procgen.FirstRunAwayGate.ObjectiveCount(gameplay) >= 1 },
                    { "interior_loot", SynapticSea.Core.Procgen.FirstRunAwayGate.HasInteriorLootSlot(layout, gameplay) },
                    { "wreck_overlay", !SynapticSea.Core.Procgen.FirstRunAwayGate.RequiresWreckOverlay(marker.Condition) || SynapticSea.Core.Procgen.FirstRunAwayGate.HasWreckOverlay(layout) } };
                string layoutText = SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(layout), gameplayText = SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(gameplay);
                var row = new GdDict { { "seed", seed }, { "checks", checks }, { "production_validate", contract.Validate(layout, gameplay) },
                    { "production_first_reject", SynapticSea.Core.Procgen.FirstRunAwayGate.RejectReason(contract, layout, gameplay, marker.Condition) },
                    { "actual_biome", layout.GetString("biome_id") }, { "actual_difficulty", layout.GetString("difficulty_id") },
                    { "loot_count", loot }, { "encounter_count", encounters }, { "objective_count", SynapticSea.Core.Procgen.FirstRunAwayGate.ObjectiveCount(gameplay) },
                    { "hazards", hazards }, { "room_variants", variants }, { "available_hazard_kinds", new GdArray(available) },
                    { "layout_sha256", AuxiliaryHash(AuxiliaryUtf8.GetBytes(layoutText)) }, { "gameplay_sha256", AuxiliaryHash(AuxiliaryUtf8.GetBytes(gameplayText)) } };
                candidates.Add(row);
                if (!string.IsNullOrEmpty(output))
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(output, seed + "-layout.json"), layoutText, AuxiliaryUtf8);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(output, seed + "-gameplay.json"), gameplayText, AuxiliaryUtf8);
                }
                Debug.Log("[AuxiliaryFirstAwayCandidate] " + SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(row));
            }
            Assert.AreEqual(liveBiome, live.BiomeId); Assert.AreEqual(liveDifficulty, live.DifficultyId);
            Assert.AreEqual(liveProfile, live.ExpeditionProfile); Assert.AreEqual(liveRich, live.RichExpeditions);
            Assert.AreEqual(liveLayoutBiome, live.LayoutGenerator.BiomeId); Assert.AreEqual(liveLayoutDifficulty, live.LayoutGenerator.DifficultyId);
            Assert.AreSame(liveSelector, live.LayoutGenerator.VariantSelector); Assert.AreEqual(originalMarkerSeed, marker.SeedValue);
            report["candidates"] = candidates;
            Debug.Log("[AuxiliaryFirstAwayProvider] " + SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(report));
            if (!string.IsNullOrEmpty(output)) System.IO.File.WriteAllText(System.IO.Path.Combine(output, "report.json"), SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(report), AuxiliaryUtf8);
        }

        static void AuxiliarySubmit(VisualElement element)
        {
            Assert.IsNotNull(element); Assert.IsNotNull(element.panel, "actual mounted UI control");
            element.Focus();
            Assert.AreSame(element, element.panel.focusController.focusedElement, "player confirmation targets the focused UI control");
            using (var submit = NavigationSubmitEvent.GetPooled())
            { submit.target = element; element.SendEvent(submit); }
        }

        void AuxiliaryOpenWorkPickerFromInventory()
        {
            _boot.Ui.OpenInventorySelf();
            Assert.IsTrue(_boot.Ui.Inventory.IsOpen());
            AuxiliarySubmit(_boot.Ui.NearbyWorkAction);
            Assert.IsTrue(_boot.Ui.NearbyWorkPicker.IsOpen(), "actual inventory action opens the chooser");
            Assert.IsFalse(_boot.Ui.Inventory.IsOpen(), "chooser replaces inventory modal");
        }

        void AuxiliarySelectWorkRow(SessionInteractable target)
        {
            var picker = _boot.Ui.NearbyWorkPicker;
            for (int index = 0; index < picker.List.Count; index++)
            {
                if (ReferenceEquals(picker.SelectedTarget, target)) return;
                picker.MoveSelection(1);
            }
            Assert.Fail("exact live target was absent from player chooser: " + target.NodeName);
        }

        IEnumerator AuxiliaryHomeRoute(string classId)
        {
            var routeWall=System.Diagnostics.Stopwatch.StartNew();
            _auxWorkTickMilliseconds.Clear();_auxWorkingFrameMilliseconds.Clear();_auxMeasuring=false;
            _recordJourneyTelemetry = true;
            yield return BootPlayable(new RunLaunchRequest { ClassId=classId, EnableAuxiliaryServices=true,
                LayoutOverridePath="res://data/diagnostics/earned-services-home-v1/layout.json" });
            _s.StageRan+=(id,location)=>
            {
                if(!_auxMeasuring)return;
                if(id==TickOrder.ElectricalArc)
                {
                    _auxTickWasRunning=_s.AuxiliaryWorkRunning;
                    _auxStageStart=System.Diagnostics.Stopwatch.GetTimestamp();
                }
                else if(id==TickOrder.WorkAction&&_auxTickWasRunning)
                {
                    _auxWorkTickMilliseconds.Add((System.Diagnostics.Stopwatch.GetTimestamp()-_auxStageStart)*1000.0/System.Diagnostics.Stopwatch.Frequency);
                }
            };
            Assert.AreEqual(6,_s.AuxiliaryServicePoints.Count);
            yield return AuxiliaryDeck(1);
            var supply = _s.LootContainers.Single(l=>l.ContainerId=="start_supply_a");
            yield return WalkTo(supply); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.IsTrue(supply.Searched);
            _boot.Ui.Inventory.OpenSelf(_s.InventoryState,_s.EquipmentState);
            Assert.IsTrue(_boot.Ui.Inventory.UnequipSlot("primary_hand"),"normal inventory returns the auto-equipped crowbar to carried tools");
            _boot.Ui.Inventory.Close(); yield return FixedSteps(2);
            yield return AuxiliaryManualStow("reactor_core","sensor_module","power_cell","fire_extinguisher");
            yield return AuxiliaryDeck(1);
            var kit = _s.LootContainers.Single(l=>l.ContainerId=="home_service_kit_01");
            yield return WalkTo(kit,1.1f); _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.IsTrue(kit.Searched);
            StudyThroughInventory();
            float studyEnd=Time.realtimeSinceStartup+50;
            while(!_s.PlayerProgression.HasReadBook("fabrication_schematic_basic")&&!_s.SliceComplete&&Time.realtimeSinceStartup<studyEnd)yield return null;
            Assert.IsTrue(_s.PlayerProgression.HasReadBook("fabrication_schematic_basic"));
            for(int n=0;n<16&&!_s.HomeObjectivesComplete;n++)
            {
                var objective=_s.Interactables.First(o=>o.Active&&!o.Completed);
                yield return AuxiliaryDeck(objective.GlobalPosition.Y>3?1:0); yield return WalkTo(objective);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
            }
            Assert.IsTrue(_s.HomeObjectivesComplete);
            Assert.IsFalse(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId,"maintenance_fabricator_feed_01"), "objective ForceRepair does not complete utility work");
            foreach(string id in new[]{"maintenance_fabricator_feed_01","maintenance_cargo_relay_01","medbay_task_light_01","airlock_dock_beacon_01"})
                yield return AuxiliaryPhysicalWork(id,id=="maintenance_fabricator_feed_01");
            Assert.AreEqual(0,_s.InventoryState.GetQuantity("scrap_metal")); Assert.AreEqual(0,_s.InventoryState.GetQuantity("wiring_bundle"));
            yield return CaptureHud(classId+"-auxiliary-utilities-earned.png");
            foreach(string id in new[]{"home_spare_harness_rack_01","home_spare_harness_rack_02"})
            {
                yield return AuxiliaryManualStow("scrap_metal","wiring_bundle");
                long before=_s.InventoryState.GetQuantity("scrap_metal");
                yield return AuxiliaryPhysicalWork(id);
                Assert.AreEqual(before,_s.InventoryState.GetQuantity("scrap_metal"), "recovery releases into finite rack");
                Assert.AreEqual(4,_s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2);
                Assert.AreEqual(before+4,_s.InventoryState.GetQuantity("scrap_metal")); Assert.IsTrue(_s.GetAuxRackRemaining(id).IsEmpty || _s.GetAuxRackRemaining(id).GetInt("scrap_metal")==0);
                _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.AreEqual(before+4,_s.InventoryState.GetQuantity("scrap_metal"));
                Assert.LessOrEqual(_s.InventoryState.GetLoadRatio(),1);
            }
            Assert.IsTrue(_s.RequestSave(),GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
            Assert.IsTrue(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId,"maintenance_cargo_relay_01"));
            foreach(string id in new[]{"home_spare_harness_rack_01","home_spare_harness_rack_02"})Assert.AreEqual(0,_s.GetAuxRackRemaining(id).GetInt("scrap_metal"));
            yield return CaptureHud(classId+"-finite-racks-restored.png");
            string checkpointDirectory = System.Environment.GetEnvironmentVariable(AuxiliaryCaptureEnv);
            if (!string.IsNullOrEmpty(checkpointDirectory))
            { AuxiliaryCaptureCheckpoint(checkpointDirectory, classId); yield break; }
            // Existing ordinary first-away content contract remains unchanged; no frozen-source gate bypass.
            var seal=_s.BreachSealPoints.Single(p=>p.CompartmentId=="cargo"&&!p.Sealed);
            yield return AuxiliaryDeck(seal.GlobalPosition.Y>3?1:0); yield return WalkAndFinishChannel(seal.GlobalPosition);
            foreach(string subId in new[]{"star_charts","nav_linkage"})
            {
                var repair=_s.RepairPoints.Single(p=>p.IsValid&&!p.Repaired&&p.SubcomponentId==subId);
                Assert.LessOrEqual(repair.MinSkill,_s.PlayerProgression.GetSkillLevel("repair"),"earned machinery skill gate");
                yield return AuxiliaryDeck(repair.GlobalPosition.Y>3?1:0); yield return WalkAndFinishChannel(repair.GlobalPosition);
            }
            Assert.GreaterOrEqual(_s.PlayerProgression.GetSkillLevel("repair"),2);
            Assert.IsTrue(_s.ShipSystemsManager.IsOperational("navigation")); Assert.IsTrue(_s.ShipSystemsManager.IsOperational("propulsion"));
            // Paid workbench lockpick remains useful preparation; required Rust door awaits authentic hazard exports.
            var bench=_s.CraftingStations.Single(c=>c.StationKind=="workbench");
            yield return AuxiliaryDeck(bench.GlobalPosition.Y>3?1:0); yield return WalkTo(bench,1.1f);
            _boot.Host.SceneState.Player.RequestInteract(); yield return FixedSteps(2); Assert.IsTrue(_boot.Ui.RecipePicker.IsOpen());
            for(int n=0;n<100&&_boot.Ui.RecipePicker.GetSelectedId()!="craft_lockpick_set";n++)_boot.Ui.RecipePicker.MoveSelection(1);
            var paid=_boot.Ui.RecipePicker.ConfirmSelection(); Assert.IsTrue(paid.GetBool("ok"),GdJson.Stringify(paid));
            float craftEnd=Time.realtimeSinceStartup+45;
            while(_s.InventoryState.GetQuantity("lockpick_set")==0&&!_s.SliceComplete&&Time.realtimeSinceStartup<craftEnd)yield return null;
            Assert.AreEqual(1,_s.InventoryState.GetQuantity("lockpick_set"));
            yield return AuxiliaryManualStow("scrap_metal","wiring_bundle","circuit_board","hull_sealant","wrench","fabrication_schematic_basic");
            yield return CutBiomatterMooring(_s.HomeShip); yield return CutBiomatterMooring(_s.LifeboatShip);
            float propelEnd=Time.realtimeSinceStartup+30;
            while(!_s.PropulsionExpandedState.CanPropel()&&!_s.SliceComplete&&Time.realtimeSinceStartup<propelEnd)yield return null;
            Assert.IsTrue(_s.PropulsionExpandedState.CanPropel(),GdJson.Stringify(_s.TravelCapability()));
            var bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.PilotedShip.ShipId); yield return WalkTo(bridge,1.2f);
            _boot.Ui.Scanner.Open(); var contacts=_s.Scan().GetArrayOrEmpty("markers"); Assert.Greater(contacts.Count,0);
            GdDict travel=null;
            for(int n=0;n<contacts.Count;n++)
            { travel=_boot.Ui.Scanner.ConfirmSelection(); if(travel.GetBool("success"))break; _boot.Ui.Scanner.MoveSelection(1); }
            Assert.IsTrue(travel!=null&&travel.GetBool("success"),GdJson.Stringify(travel)); yield return FixedSteps(8);
            Assert.IsTrue(_s.AwayFromStart); Assert.IsFalse(_s.SliceComplete,"departure is not extraction");
            yield return CaptureHud(classId+"-earned-ordinary-departure.png");
            bridge=_s.BridgeTerminals.Single(t=>t.ShipId==_s.PilotedShip.ShipId); yield return WalkTo(bridge,1.2f);
            Assert.IsTrue(_s.TravelHome()); yield return FixedSteps(8); Assert.IsFalse(_s.AwayFromStart);
            Assert.IsTrue(_s.RequestSave(),GdJson.Stringify(_s.LastSaveResult)); Assert.IsTrue(_s.RequestLoad()); yield return FixedSteps(8);
            Assert.IsTrue(_s.IsAuxiliaryHardwareReady(_s.HomeShip.ShipId,"maintenance_fabricator_feed_01"));
            yield return CaptureHud(classId+"-earned-return-restored.png");
            Debug.Log("[AuxiliaryPhysicalPerformance] class="+classId+" completed_route_wall_seconds="+routeWall.Elapsed.TotalSeconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                +" work_tick "+AuxiliaryLatencySummary(_auxWorkTickMilliseconds)+" work_rest_frame "+AuxiliaryLatencySummary(_auxWorkingFrameMilliseconds));
            Debug.Log("[AuxiliaryPhysical] class="+classId+" progression="+GdJson.Stringify(_s.PlayerProgression.GetSummary())+" state="+GdJson.Stringify(_s.GetAuxiliaryServiceState())+" vitals="+GdJson.Stringify(_s.VitalsState.GetSummary())+" time="+_s.WorldTime);
        }
    }
}
