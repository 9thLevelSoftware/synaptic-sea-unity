using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public partial class SaveLoadService
    {
        public const string ComponentGenerationRoot = "user://saves/.component-live-generations";
        public bool ComponentIntegrationEnabled { get; }
        public const string PaidGenerationRoot = "user://saves/.paid-craft-generations";
        public bool PaidCraftingEnabled { get; }
        /// <summary>Explicit reviewed profile capability; off preserves the prior compatibility catalog.</summary>
        public bool CompleteGenerationEnabled => ComponentIntegrationEnabled || PaidCraftingEnabled;
        string GenerationRoot => ComponentIntegrationEnabled ? ComponentGenerationRoot : PaidGenerationRoot;
        string AdmissionVersion => ComponentIntegrationEnabled ? "component-run-admission-1" : "paid-run-admission-1";
        string CreationKind => ComponentIntegrationEnabled ? "diagnostic_new_run" : "ordinary_new_run";
        internal GdDict ReadSelectedSnapshot(GdDict selected, string role)
        {
            if (role != "run" && role != "world") return null;
            string text = selected.GetDictOrEmpty("payloads").GetString(role + "_text");
            return PaidCraftingEnabled ? PaidSnapshotCodec.Parse(text, PaidSnapshotCodec.SnapshotPolicy(ComponentIntegrationEnabled, role == "world")) : GdJson.ParseString(text) as GdDict;
        }
        Func<string, string, string, bool> _componentSave;
        string _authorizedDiagnosticNewRun = "";
        readonly HashSet<string> _componentTerminalRuns = new HashSet<string>(StringComparer.Ordinal);
        internal void AuthorizeDiagnosticNewRun(string run) { _authorizedDiagnosticNewRun = run ?? ""; }
        internal void BindComponentSave(Func<string, string, string, bool> save) { _componentSave = save; }
        public GdDict LastGenerationResult { get; private set; } = new GdDict();
        static readonly string[] ComponentSlotsIds = { "world", "autosave_active", "autosave_a", "autosave_b", "autosave_c", "quicksave", "slot_01", "slot_02", "slot_03", "slot_04", "slot_05", "slot_06" };
        internal static string ComponentSlotKind(string id) => id == "world" ? "world" : id == "quicksave" ? "quick" : id != null && id.StartsWith("autosave_", StringComparison.Ordinal) ? "auto" : "manual";
        internal GdDict ComponentCompatibility()
        {
            var compatibility = new GdDict
        {
            { "engine_version", EngineVersionString }, { "catalog_id", ComponentIntegrationEnabled ? "component-live-diagnostic" : "paid-crafting-ordinary" }, { "catalog_version", "1" },
            { "library_id", "" }, { "library_version", "" },
            { "profiles", new GdDict { { ConstrainedExpedition.Profile, ConstrainedExpedition.Profile }, { ConstrainedExpedition.LegacyProfile, ConstrainedExpedition.LegacyProfile } } }
        };
            return compatibility;
        }
        internal SaveCommitCoordinator ComponentCoordinator(Action<string> fault = null)
            => new SaveCommitCoordinator(Storage, GenerationRoot, new ComponentTerminalAuthority(this), ComponentCompatibility(), fault, ComponentIntegrationEnabled, PaidCraftingEnabled);
        internal GdDict ReadComponentCommitParent(string run, string slot) => ComponentCoordinator().ReadCommitParent(run, slot);
        public string GetComponentEpitaph(string slotId)
        {
            if (!CompleteGenerationEnabled) return "";
            GdDict metadata = ComponentCoordinator().ReadSlotMetadata(slotId);
            return metadata.GetBool("ok") && metadata.GetBool("frozen") ? metadata.GetString("epitaph") : "";
        }
        sealed class ComponentTerminalAuthority : ISaveGenerationTerminalAuthority
        {
            readonly SaveLoadService _service;
            public ComponentTerminalAuthority(SaveLoadService service) { _service = service; }
            public GdDict Query(string runId, string slotId)
            {
                if (_service._componentTerminalRuns.Contains(runId)) return Answer(true, "terminal", runId, slotId);
                bool explicitNew = _service._authorizedDiagnosticNewRun == runId;
                bool admitted = explicitNew;
                string path = _service.Admission(runId);
                if (_service.Storage.FileExists(path))
                {
                    string text = _service.Storage.ReadText(path);
                    GdDict declaration = _service.PaidCraftingEnabled ? PaidSnapshotCodec.Parse(text) : GdJson.ParseString(text) as GdDict;
                    if (declaration == null || declaration.GetString("schema_version") != _service.AdmissionVersion || declaration.GetString("run_id") != runId || declaration.GetString("creation_kind") != _service.CreationKind)
                        return Answer(false, "ambiguous", runId, slotId);
                    admitted = true;
                }
                if (!admitted) return Answer(false, "unbound", runId, slotId);
                return Answer(true, _service.OriginalRunAuthority(runId), runId, slotId);
            }
            static GdDict Answer(bool ok, string status, string run, string slot) => new GdDict { { "ok", ok }, { "run_id", run }, { "slot_id", slot }, { "status", status }, { "legacy_witnesses", new GdArray() } };
        }
        string Admission(string run) => GenerationRoot + "/r/" + SaveGenerationArtifacts.Hash(run) + "/admission.json";
        string OriginalRunAuthority(string run)
        {
            bool indexPresent = Storage.FileExists(INDEX_PATH);
            GdDict index = indexPresent ? GdJson.ParseString(Storage.ReadText(INDEX_PATH)) as GdDict : null;
            bool indexValid = !indexPresent || index != null && index.Get("slots") is GdArray;
            var rows = new List<GdDict>();
            if (index != null && index.Get("slots") is GdArray slots)
                foreach (object value in slots)
                {
                    if (!(value is GdDict row) || !(row.Get("slot_id") is string id) || !ComponentSlotsIds.Contains(id) ||
                        !(row.Get("run_id") is string owner) || owner.Length == 0 || !(row.Get("frozen") is bool)) { indexValid = false; continue; }
                    // Scan every surviving witness before considering ambiguity; later duplicates cannot erase it.
                    if (owner == run && row.GetBool("frozen")) return "terminal";
                    rows.Add(row);
                }
            bool ambiguous = false;
            foreach (string id in ComponentSlotsIds)
            {
                if (!NewResolver().HasDiedIn(id)) continue;
                string path = id == "world" ? WORLD_SLOT_FILE : id == ACTIVE_AUTOSAVE_SLOT_ID ? SAVE_PATH : SAVES_DIR + "/" + id + ".json";
                bool payloadPresent = Storage.FileExists(path);
                GdDict old = payloadPresent ? GdJson.ParseString(Storage.ReadText(path)) as GdDict : null;
                string payloadOwner = old?.Get("run_id") is string owner ? owner : "";
                if (payloadOwner == run) return "terminal";
                List<GdDict> members = rows.Where(row => row.GetString("slot_id") == id).ToList();
                if (!indexValid || members.Count > 1 || payloadPresent && payloadOwner.Length == 0) { ambiguous = true; continue; }
                string indexOwner = members.Count == 1 ? members[0].GetString("run_id") : "";
                if (payloadOwner.Length > 0 && indexOwner.Length > 0 && payloadOwner != indexOwner) { ambiguous = true; continue; }
                if (!payloadPresent && indexOwner == run) return "terminal";
                // Missing payloads without independent ownership remain unavailable, including explicitly new runs.
                if (payloadOwner.Length == 0 && indexOwner.Length == 0) ambiguous = true;
            }
            return ambiguous ? "ambiguous" : "live";
        }
        public GdDict SelectGeneration(string slotId)
        {
            if (!CompleteGenerationEnabled) return new GdDict { { "ok", false }, { "reason", "component_integration_not_enabled" } };
            GdDict selected = ComponentCoordinator().ReadSelected(slotId);
            if (!PaidCraftingEnabled && selected.GetString("reason") == "not_found") return LegacyComponentPreflight(slotId);
            return selected.DeepCopy();
        }
        public GdDict ReadGeneration(string runId, string slotId, string generationId, string manifestSha256)
            => CompleteGenerationEnabled ? ComponentCoordinator().ReadGeneration(runId, slotId, generationId, manifestSha256) : new GdDict { { "ok", false }, { "reason", "component_integration_not_enabled" } };
        internal GdDict CommitComponentGeneration(GdDict payload, bool explicitNewRun)
        {
            string run = payload.GetString("run_id"), slot = payload.GetString("slot_id");
            GdDict validation = ComponentCoordinator().ValidateSuppliedPayload(payload, run, slot);
            if (!validation.GetBool("ok")) { LastGenerationResult = validation; return validation.DeepCopy(); }
            string admission = Admission(run);
            if (!Storage.FileExists(admission))
            {
                if (_authorizedDiagnosticNewRun != run) return new GdDict { { "ok", false }, { "reason", "terminal_unbound" } };
                try
                {
                    var declaration = new GdDict { { "schema_version", AdmissionVersion }, { "run_id", run }, { "creation_kind", CreationKind } };
                    Storage.WriteText(admission, PaidCraftingEnabled ? PaidSnapshotCodec.Stringify(declaration) : GdJson.Stringify(declaration));
                }
                catch (Exception e) { LastGenerationResult = new GdDict { { "ok", false }, { "reason", "admission_write_failed" }, { "detail", e.GetType().Name } }; return LastGenerationResult.DeepCopy(); }
            }
            LastGenerationResult = explicitNewRun && _authorizedDiagnosticNewRun == run ? ComponentCoordinator().CommitNewRun(payload, run, slot) : ComponentCoordinator().Commit(payload, run, slot);
            return LastGenerationResult.DeepCopy();
        }
        GdDict LegacyComponentPreflight(string slot)
        {
            string path = slot == "world" ? WORLD_SLOT_FILE : slot == ACTIVE_AUTOSAVE_SLOT_ID ? SAVE_PATH : SAVES_DIR + "/" + slot + ".json";
            if (!Storage.FileExists(path)) return new GdDict { { "ok", false }, { "reason", "not_found" } };
            // Original bytes remain available to ordinary legacy SaveLoadService/Open original save.
            try
            {
                if (NewResolver().HasDiedIn(slot)) return new GdDict { { "ok", false }, { "reason", "legacy_death" } };
                GdDict source = GdJson.ParseString(Storage.ReadText(path)) as GdDict;
                if (source == null) return new GdDict { { "ok", false }, { "reason", "legacy_payload_invalid" } };
                string run = source.GetString("run_id");
                if (run.Length == 0) return new GdDict { { "ok", false }, { "reason", "legacy_world_binding_missing" } };
                foreach (string id in ComponentSlotsIds)
                {
                    if (!NewResolver().HasDiedIn(id)) continue;
                    string oldPath = id == "world" ? WORLD_SLOT_FILE : id == ACTIVE_AUTOSAVE_SLOT_ID ? SAVE_PATH : SAVES_DIR + "/" + id + ".json";
                    GdDict old = Storage.FileExists(oldPath) ? GdJson.ParseString(Storage.ReadText(oldPath)) as GdDict : null;
                    if (old == null || old.GetString("run_id").Length == 0 || old.GetString("run_id") == run)
                        return new GdDict { { "ok", false }, { "reason", "legacy_death" } };
                }
                if (SavePayloadAssembler.HasUnverifiedCraft(source)) return new GdDict { { "ok", false }, { "reason", "craft_payment_unverified" } };
                GdDict paired = slot == "world" ? source : Storage.FileExists(WORLD_SLOT_FILE) ? GdJson.ParseString(Storage.ReadText(WORLD_SLOT_FILE)) as GdDict : null;
                bool bound = paired != null && paired.GetString("run_id") == run && paired.Get("home_ship") is GdDict;
                return new GdDict { { "ok", false }, { "reason", bound ? "legacy_component_conversion_unavailable" : "legacy_world_binding_missing" } };
            }
            catch (Exception) { return new GdDict { { "ok", false }, { "reason", "legacy_payload_unreadable" } }; }
        }
        internal GdDict FreezeComponentRun(string run, string cause, string epitaph)
        {
            _componentTerminalRuns.Add(run);
            GdDict terminal = new GdDict { { "schema_version", SaveCommitCoordinator.TerminalVersion }, { "run_id", run }, { "state", "terminal" },
                { "reason", "death" }, { "cause", cause ?? "death" }, { "epitaph", epitaph ?? "" }, { "terminal_revision", 1L }, { "legacy_witnesses", new GdArray() } };
            GdDict intent = ComponentCoordinator().RecordTerminalIntent(terminal, run);
            LastGenerationResult = intent.GetBool("ok") ? ComponentCoordinator().RecordTerminal(intent.GetDictOrEmpty("terminal"), run) : intent;
            if (!LastGenerationResult.GetBool("ok"))
            {
                LastGenerationResult = new GdDict { { "ok", false }, { "reason", "terminal_publication_failed" }, { "terminal_pending", true }, { "detail", LastGenerationResult.GetString("reason") }, { "intent_durable", intent.GetBool("ok") } };
                CoreServices.Log.Warning("Diagnostic freeze pending: " + LastGenerationResult.GetString("detail"));
            }
            return LastGenerationResult.DeepCopy();
        }
        List<SaveSlotState> ComponentSlots()
        {
            var rows = new List<SaveSlotState>();
            foreach (string slot in ComponentSlotsIds)
            {
                GdDict selected = ComponentCoordinator().ReadSlotMetadata(slot);
                if (!selected.GetBool("ok")) continue;
                RunSnapshot run = RunSnapshot.FromDict(selected.Get("run_snapshot"), ComponentIntegrationEnabled ? RunSnapshot.ComponentIntegrationVersion : CURRENT_SLICE_VERSION, EngineVersionString);
                if (run == null) continue;
                rows.Add(new SaveSlotState { SlotId = slot, SlotKind = ComponentSlotKind(slot), DisplayName = slot, RunId = run.RunId, SchemaVersion = run.SliceVersion,
                    SavedAt = run.SavedAt, SavedAtEpoch = run.SavedAtEpoch, CurrentLocation = run.CurrentLocation, PlayTimeSeconds = run.PlayTimeSeconds, ObjectiveSequence = run.CurrentObjectiveSequence,
                    SynapticSeaSeed = run.WorldSeed, PlayerClass = run.PlayerProgressionSummary.GetString("class_id"), Frozen = selected.GetBool("frozen") });
            }
            return rows;
        }
    }
}
