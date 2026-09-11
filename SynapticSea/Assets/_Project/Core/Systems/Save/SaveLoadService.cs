// Ported from scripts/systems/save_load_service.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-012 current-run save/load service + Task 11 multi-slot extension.
    ///
    /// Owned by PlayableGeneratedShip, not an autoload. Single save slot at <c>user://saves/current_run.json</c>
    /// (legacy REQ-012 path) plus the Task 11 slot families:
    ///
    ///   - manual slots: slot_01..slot_06 (user://saves/&lt;slot_id&gt;.json)
    ///   - autosave slots: autosave_a..autosave_c (rotation)
    ///   - quicksave: quicksave (single dedicated slot)
    ///   - world slot: world (alias for the legacy save_world path)
    ///
    /// Per ADR-0007/0031: this service is current-run only. No hub/meta/cross-run state is serialized through it.
    ///
    /// Per ADR-0031/0032: corruption is detected and the bad file is moved to
    /// user://saves/.corrupt/&lt;slot_id&gt;.&lt;epoch&gt;.&lt;file&gt;.bak; migration is deterministic and writes the migrated
    /// form to &lt;slot_id&gt;.migrated.json; permadeath freezes a slot via user://saves/&lt;slot_id&gt;.death.json.
    /// </summary>
    /// <remarks>
    /// <c>FileAccess</c> / <c>DirAccess</c> / <c>ProjectSettings.globalize_path</c> on <c>user://</c> go through an
    /// injected <see cref="IStorage"/>; <c>Time.*</c> through an injected <see cref="IClock"/>; both default to
    /// <see cref="CoreServices"/>. <c>Engine.get_version_info()["string"]</c> is <see cref="CoreServices.Engine"/>
    /// (the saved key stays <c>godot_version</c>). Godot's <c>_safe_new</c> headless class-registry workaround has no
    /// C# equivalent and is dropped.
    /// </remarks>
    public class SaveLoadService : ITitleSaveService
    {
        public const string SAVE_PATH = "user://saves/current_run.json";
        public const string CURRENT_SLICE_VERSION = "gate2-current-run-4";
        public const string SAVES_DIR = "user://saves";
        public const string INDEX_PATH = "user://saves/index.json";
        public const string CORRUPT_DIR = "user://saves/.corrupt";
        public const string CLOUD_DIR = "user://saves/.cloud";
        public const string WORLD_SLOT_FILE = "user://saves/world.json";
        // Legacy slot file paths preserved so existing REQ-012 autosave-sequence smoke and world_save_service smoke
        // keep their on-disk contract intact.
        public const string LEGACY_CURRENT_RUN_PATH = SAVE_PATH;
        // Active autosave (the slot_id the legacy autosave_sequence smoke expects to land at SAVE_PATH). We write
        // the active autosave to SAVE_PATH so the existing `user://saves/current_run.json` invariant is preserved.
        public const string ACTIVE_AUTOSAVE_SLOT_ID = "autosave_active";

        IStorage _storage;
        IClock _clock;

        public SaveLoadService(IStorage storage = null, IClock clock = null)
        {
            _storage = storage;
            _clock = clock;
        }

        /// <summary><c>user://</c> backing store; defaults to <see cref="CoreServices.UserStorage"/>.</summary>
        public IStorage Storage
        {
            get => _storage ?? CoreServices.UserStorage;
            set => _storage = value;
        }

        public IClock Clock
        {
            get => _clock ?? CoreServices.Clock;
            set => _clock = value;
        }

        static string EngineVersionString => CoreServices.Engine.VersionString;

        PermadeathResolver NewResolver() => new PermadeathResolver(Storage, Clock);

        // run_id slot-ownership rework: the identity of the run currently driving this service. Stamped onto every
        // write (save_world/save_to_slot) so the shared slot family (world, autosave_a/b/c, autosave_active,
        // quickslot) is owned structurally instead of by convention-tracked flags. Set by the coordinator at session
        // start and on a successful Continue/F9 load.
        string _activeRunId = "";

        public void SetActiveRunId(string id)
        {
            _activeRunId = id;
        }

        public string GetActiveRunId() => _activeRunId;

        // Maps a slot_id to the on-disk path. The active autosave is the legacy SAVE_PATH so existing REQ-012 +
        // autosave-sequence smokes stay green; the world slot lives at its own file; everything else lives at
        // user://saves/<slot_id>.json.
        string SlotPath(string slotId, string slotKind)
        {
            if (slotId == ACTIVE_AUTOSAVE_SLOT_ID)
                return SAVE_PATH;
            if (slotKind == SaveSlotState.SlotKindWorld || slotId == "world")
                return WORLD_SLOT_FILE;
            return "user://saves/" + slotId + ".json";
        }

        public bool SaveCurrentRun(RunSnapshot snapshot)
        {
            // Legacy REQ-012 alias: the active autosave slot is the current_run.json path.
            // Preserves the smoke contracts that depend on SAVE_PATH.
            return SaveToSlot(ACTIVE_AUTOSAVE_SLOT_ID, snapshot, SaveSlotState.SlotKindAuto, false, "current_run_alias");
        }

        public RunSnapshot LoadCurrentRun() => LoadFromSlot(ACTIVE_AUTOSAVE_SLOT_ID);

        /// <summary>
        /// REQ-0012 world save: serializes a whole WorldSnapshot to the world slot file. The world slot is its own
        /// file (world.json), distinct from the current_run.json path the autosave writes to.
        /// </summary>
        public bool SaveWorld(WorldSnapshot worldSnapshot)
        {
            if (worldSnapshot == null)
            {
                CoreServices.Log.Warning("SaveLoadService: cannot save null world snapshot");
                return false;
            }
            if (!EnsureSaveDir())
                return false;
            // run_id slot-ownership rework: stamp the writing run's identity onto the payload before serializing,
            // so a later freeze_run(run_id) can find this slot via the index.
            worldSnapshot.RunId = _activeRunId;
            string path = WORLD_SLOT_FILE;
            string json = GdJson.Stringify(worldSnapshot.ToDict(), "\t");
            if (!TryWrite(path, json, out string error))
            {
                CoreServices.Log.Warning("SaveLoadService: cannot open world save file for writing, error=" + error);
                return false;
            }
            // Reclaim-on-write (ADR-0043): a death record describes the run that died in this slot, not the run
            // writing now. Clear any stale death record -- but only AFTER the write is confirmed on disk
            // (PR #57 Codex round 3 P2).
            NewResolver().ClearDeath("world");
            // Index the world slot row + write its cloud manifest.
            IndexWorldSlot(worldSnapshot);
            WriteCloudManifest("world", path, WorldSnapshot.WorldSliceVersion);
            return true;
        }

        /// <summary>
        /// Reads the world save from WORLD_SLOT_FILE. Returns null when no save exists, the file is empty/not a JSON
        /// object, or the WorldSnapshot version markers do not match. On parse/version failure, the bad file is moved
        /// to .corrupt/ before returning null.
        /// </summary>
        public WorldSnapshot LoadWorld()
        {
            string path = WORLD_SLOT_FILE;
            if (!Storage.FileExists(path))
                return null;
            // ADR-0043 permadeath gate -- mirrors the load_from_slot gate. Old saves have no world.death.json, so
            // has_died_in defaults false and legacy loads are unaffected.
            if (NewResolver().HasDiedIn("world"))
                return null;
            string json = Storage.ReadText(path);
            if (json == null)
            {
                CoreServices.Log.Warning("SaveLoadService: cannot open world save file for reading, error=unreadable");
                return null;
            }
            if (json.Length == 0)
            {
                BackupCorruptFile(path, "world", NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: world save file is empty");
                return null;
            }
            object parsed = GdJson.ParseString(json);
            if (parsed == null || !(parsed is GdDict parsedDict))
            {
                BackupCorruptFile(path, "world", NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: world save file is not valid JSON object");
                return null;
            }
            string expectedGodot = EngineVersionString;
            GdDict migrationResult = new SaveMigrationService().MigrateWorld(parsedDict);
            if (migrationResult.Get("dict", null) == null)
            {
                BackupCorruptFile(path, "world", NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: world save rejected by migration (newer than current version)");
                return null;
            }
            if (V.Bool(migrationResult.Get("newer_than_current", false)))
            {
                CoreServices.Log.Warning("SaveLoadService: world save rejected by migration (newer than current version)");
                return null;
            }
            WorldSnapshot ws = WorldSnapshot.FromDict(migrationResult["dict"], WorldSnapshot.WorldSliceVersion, expectedGodot);
            if (ws == null)
            {
                BackupCorruptFile(path, "world", NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: world save rejected by from_dict (missing fields or version mismatch)");
                return null;
            }
            return ws;
        }

        public bool DeleteCurrentRun()
        {
            // Legacy REQ-012 contract: delete the current_run autosave file. Also remove the world slot and index
            // entries so a stale world save cannot survive a finished run.
            bool ok = true;
            if (Storage.FileExists(SAVE_PATH))
            {
                if (!Storage.Delete(SAVE_PATH))
                {
                    CoreServices.Log.Warning("SaveLoadService: failed to delete save file, error=delete_failed");
                    ok = false;
                }
            }
            if (Storage.FileExists(WORLD_SLOT_FILE))
            {
                if (!Storage.Delete(WORLD_SLOT_FILE))
                {
                    CoreServices.Log.Warning("SaveLoadService: failed to delete world save file, error=delete_failed");
                    ok = false;
                }
            }
            // Also remove the world slot's cloud manifest (mirrors delete_slot()'s manifest removal) so a finished
            // run does not leak user://saves/.cloud/world.manifest.json.
            string worldManifestPath = CLOUD_DIR + "/world.manifest.json";
            if (Storage.FileExists(worldManifestPath))
                Storage.Delete(worldManifestPath);
            // Remove the active autosave from the index so a fresh run does not see a phantom autosave row.
            SaveIndexState idx = LoadIndex();
            idx.Remove(ACTIVE_AUTOSAVE_SLOT_ID);
            idx.Remove("world");
            SaveIndex(idx);
            return ok;
        }

        public bool HasSave()
        {
            // Legacy REQ-012 contract: true when EITHER the current_run autosave exists OR a world save exists.
            return Storage.FileExists(SAVE_PATH) || Storage.FileExists(WORLD_SLOT_FILE);
        }

        /// <summary>
        /// Ensures the save slot's parent directory exists. Returns false only when directory creation genuinely fails.
        /// </summary>
        bool EnsureSaveDir()
        {
            string dirPath = ResPath.GetBaseDir(SAVE_PATH);
            if (!Storage.DirExists(dirPath))
            {
                try
                {
                    Storage.MakeDirRecursive(dirPath);
                }
                catch (Exception e)
                {
                    CoreServices.Log.Warning("SaveLoadService: failed to create save dir, error=" + e.Message);
                    return false;
                }
            }
            return true;
        }

        // ----------------------------------------------------------------------------
        // Task 11 multi-slot API (ADR-0031, ADR-0032).
        // ----------------------------------------------------------------------------

        /// <summary>
        /// Write a RunSnapshot to a named slot. The slot_kind stamps the row in the index; the slot_id controls the
        /// on-disk path. Returns false on I/O failure or a null snapshot.
        /// </summary>
        public bool SaveToSlot(string slotId, RunSnapshot snapshot, string slotKind, bool isQuicksave, string displayName)
        {
            if (string.IsNullOrEmpty(slotId))
            {
                CoreServices.Log.Warning("SaveLoadService: save_to_slot called with empty slot_id");
                return false;
            }
            if (snapshot == null)
            {
                CoreServices.Log.Warning("SaveLoadService: save_to_slot called with null snapshot");
                return false;
            }
            if (!EnsureSaveDir())
                return false;
            // Stamp the slot identity onto the snapshot before serializing so a future load round-trips the slot
            // metadata without inspecting the file name. We only stamp slice_version when the caller did not
            // explicitly set it; this preserves the REQ-012 contract that an incompatible-version save is rejected
            // on load.
            snapshot.SlotId = slotId;
            snapshot.SlotKind = slotKind;
            snapshot.IsAutosave = slotKind == SaveSlotState.SlotKindAuto;
            snapshot.IsQuicksave = isQuicksave;
            // run_id slot-ownership rework: stamp the writing run's identity onto the payload before serializing.
            snapshot.RunId = _activeRunId;
            if (string.IsNullOrEmpty(snapshot.SliceVersion))
                snapshot.SliceVersion = CURRENT_SLICE_VERSION;
            snapshot.GodotVersion = EngineVersionString;
            if (string.IsNullOrEmpty(snapshot.SavedAt))
                snapshot.SavedAt = Clock.DateTimeString(true);
            if (snapshot.SavedAtEpoch == 0)
                snapshot.SavedAtEpoch = NowEpoch();
            string path = SlotPath(slotId, slotKind);
            GdDict data = snapshot.ToDict();
            string json = GdJson.Stringify(data, "\t");
            if (!TryWrite(path, json, out string error))
            {
                CoreServices.Log.Warning("SaveLoadService: cannot open slot file for writing, slot_id=" + slotId + " error=" + error);
                return false;
            }
            // Reclaim-on-write (ADR-0043): clear any stale death record -- but only AFTER the write is confirmed on
            // disk (PR #57 Codex round 3 P2).
            NewResolver().ClearDeath(slotId);
            // Update the index row + write the cloud manifest.
            IndexRunSlot(slotId, slotKind, displayName, snapshot, path);
            WriteCloudManifest(slotId, path, CURRENT_SLICE_VERSION);
            return true;
        }

        /// <summary>
        /// Read a RunSnapshot from a named slot. Returns null on missing file, parse failure, or version mismatch. On
        /// parse/version failure, the bad file is moved to .corrupt/ and the slot row is flagged in the index.
        /// </summary>
        public RunSnapshot LoadFromSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId))
            {
                CoreServices.Log.Warning("SaveLoadService: load_from_slot called with empty slot_id");
                return null;
            }
            string path = SlotPath(slotId, IndexedKindFor(slotId));
            if (!Storage.FileExists(path))
                return null;
            // Permadeath: refuse to load from a slot that has a death record.
            if (NewResolver().HasDiedIn(slotId))
            {
                // The slot's run is dead; return null to force a fresh run.
                return null;
            }
            string json = Storage.ReadText(path);
            if (json == null)
            {
                CoreServices.Log.Warning("SaveLoadService: cannot open slot file for reading, slot_id=" + slotId + " error=unreadable");
                return null;
            }
            if (json.Length == 0)
            {
                BackupCorruptFile(path, slotId, NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: slot file is empty, slot_id=" + slotId);
                return null;
            }
            object parsed = GdJson.ParseString(json);
            if (parsed == null || !(parsed is GdDict parsedDict))
            {
                BackupCorruptFile(path, slotId, NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: slot file is not valid JSON object, slot_id=" + slotId);
                return null;
            }
            // Migration first (deterministic; runs on a parsed Dictionary).
            GdDict migrationResult = new SaveMigrationService().MigrateRun(parsedDict);
            if (migrationResult.Get("dict", null) == null)
            {
                BackupCorruptFile(path, slotId, NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: slot rejected by migration (newer than current), slot_id=" + slotId);
                return null;
            }
            if (V.Bool(migrationResult.Get("migrated", false)))
            {
                // Persist the migrated form so subsequent loads skip the migration step.
                string migratedPath2 = GdString.TrimSuffix(path, ".json") + ".migrated.json";
                TryWrite(migratedPath2, GdJson.Stringify(migrationResult["dict"], "\t"), out _);
            }
            string expectedGodot = EngineVersionString;
            RunSnapshot snapshot = RunSnapshot.FromDict(migrationResult["dict"], CURRENT_SLICE_VERSION, expectedGodot);
            if (snapshot == null)
            {
                BackupCorruptFile(path, slotId, NowEpoch());
                CoreServices.Log.Warning("SaveLoadService: slot rejected by from_dict (missing fields or version mismatch), slot_id=" + slotId);
                return null;
            }
            // Cloud manifest sha gate: if a manifest exists and the recomputed sha does not match, refuse the load.
            // A future cloud adapter relies on this contract.
            string manifestPath = CLOUD_DIR + "/" + slotId + ".manifest.json";
            if (Storage.FileExists(manifestPath))
            {
                string mjson = Storage.ReadText(manifestPath);
                if (mjson != null)
                {
                    object mparsed = GdJson.ParseString(mjson);
                    if (mparsed is GdDict mdict)
                    {
                        string storedSha = V.Str(mdict.Get("payload_sha256", ""));
                        string computedSha = CloudManifestState.RecomputeSha256(path, Storage);
                        if (storedSha.Length != 0 && computedSha.Length != 0 && storedSha != computedSha)
                        {
                            BackupCorruptFile(path, slotId, NowEpoch());
                            CoreServices.Log.Warning("SaveLoadService: slot manifest sha mismatch, slot_id=" + slotId);
                            return null;
                        }
                    }
                }
            }
            return snapshot;
        }

        public bool DeleteSlot(string slotId)
        {
            string kind = IndexedKindFor(slotId);
            string path = SlotPath(slotId, kind);
            bool ok = true;
            if (Storage.FileExists(path))
            {
                if (!Storage.Delete(path))
                {
                    CoreServices.Log.Warning("SaveLoadService: failed to delete slot file, slot_id=" + slotId + " error=delete_failed");
                    ok = false;
                }
            }
            // Godot quirk kept for parity: this is "<slot>.json.migrated.json", while load_from_slot writes
            // "<slot>.migrated.json", so the migrated sidecar written on load is not removed here.
            string migratedPath = path + ".migrated.json";
            if (Storage.FileExists(migratedPath))
                Storage.Delete(migratedPath);
            string manifestPath = CLOUD_DIR + "/" + slotId + ".manifest.json";
            if (Storage.FileExists(manifestPath))
                Storage.Delete(manifestPath);
            string deathPath = NewResolver().DeathPathFor(slotId);
            if (Storage.FileExists(deathPath))
                Storage.Delete(deathPath);
            SaveIndexState idx = LoadIndex();
            idx.Remove(slotId);
            SaveIndex(idx);
            return ok;
        }

        public bool HasSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId))
                return false;
            return Storage.FileExists(SlotPath(slotId, IndexedKindFor(slotId)));
        }

        /// <summary>
        /// run_id slot-ownership rework: every slot_id whose index row (or the world row) is stamped with run_id.
        /// An empty run_id matches NOTHING -- a fresh run that has neither loaded nor saved anything must never
        /// freeze a prior run's still-live slots.
        /// </summary>
        public GdArray SlotIdsForRun(string runId)
        {
            if (string.IsNullOrEmpty(runId))
                return new GdArray();
            var result = new GdArray();
            SaveIndexState idx = LoadIndex();
            foreach (SaveSlotState row in idx.Slots)
            {
                if (row != null && row.RunId == runId)
                {
                    string indexedId = row.SlotId;
                    if (!result.Contains(indexedId))
                        result.Append(indexedId);
                }
            }
            // PR #58 (Codex P2): the index is a derived cache; payload files carry the authoritative run_id stamp,
            // so union in a direct disk scan; the index remains a fast path, never the gate.
            foreach (string diskId in AllSlotIdsOnDisk())
            {
                string slotId = diskId;
                if (slotId == "index" || result.Contains(slotId))
                    continue;
                if (PayloadRunId(slotId) == runId)
                    result.Append(slotId);
            }
            return result;
        }

        /// <summary>
        /// Reads only the top-level run_id key from a slot's payload file. Returns "" for missing/unreadable/corrupt
        /// payloads and legacy saves written before the run_id rework -- both fail OPEN by design.
        /// </summary>
        string PayloadRunId(string slotId)
        {
            string path = SlotPath(slotId, IndexedKindFor(slotId));
            if (!Storage.FileExists(path))
                return "";
            string text = Storage.ReadText(path);
            if (text == null)
                return "";
            object parsed = GdJson.ParseString(text);
            if (!(parsed is GdDict dict))
                return "";
            return V.Str(dict.Get("run_id", ""));
        }

        /// <summary>
        /// run_id slot-ownership rework: freezes every slot owned by run_id (per slot_ids_for_run) with a
        /// PermadeathResolver death record.
        /// </summary>
        public void FreezeRun(string runId, string cause, string epitaph, double runTime, long finalSeq)
        {
            PermadeathResolver resolver = NewResolver();
            foreach (object slotId in SlotIdsForRun(runId))
                resolver.RecordDeath(V.Str(slotId), cause, epitaph, runTime, finalSeq);
        }

        /// <summary>
        /// Returns SaveSlotState rows sorted by saved_at desc. Reclassifies rows whose slot file is missing on disk
        /// as <c>corrupt=true</c>.
        /// </summary>
        public List<SaveSlotState> ListSlots()
        {
            SaveIndexState idx = LoadIndex();
            var present = new List<object>();
            foreach (string slotId in AllSlotIdsOnDisk())
                present.Add(slotId);
            idx.ReclassifyCorrupt(present);
            SaveIndex(idx);
            return idx.SortedBySavedAtDesc();
        }

        /// <remarks>
        /// Godot's <c>DirAccess.get_next()</c> order is filesystem-dependent; <see cref="IStorage.ListFiles"/> is
        /// ordinal. Directories (<c>.corrupt</c>, <c>.cloud</c>) start with '.' and were skipped in Godot too.
        /// </remarks>
        List<string> AllSlotIdsOnDisk()
        {
            var result = new List<string>();
            if (!Storage.DirExists(SAVES_DIR))
                return result;
            foreach (string entry in Storage.ListFiles(SAVES_DIR))
            {
                if (!GdString.BeginsWith(entry, ".") && GdString.EndsWith(entry, ".json")
                    && !GdString.EndsWith(entry, ".migrated.json") && !GdString.EndsWith(entry, ".death.json"))
                {
                    string slotId = GdString.TrimSuffix(entry, ".json");
                    if (slotId == "current_run")
                        slotId = ACTIVE_AUTOSAVE_SLOT_ID;
                    result.Add(slotId);
                }
            }
            return result;
        }

        string IndexedKindFor(string slotId)
        {
            SaveIndexState idx = LoadIndex();
            SaveSlotState row = idx.Find(slotId);
            if (row != null)
                return row.SlotKind;
            if (slotId == ACTIVE_AUTOSAVE_SLOT_ID)
                return SaveSlotState.SlotKindAuto;
            if (slotId == "world")
                return SaveSlotState.SlotKindWorld;
            if (SaveSlotState.ManualSlotIds.Contains(slotId))
                return SaveSlotState.SlotKindManual;
            if (SaveSlotState.AutosaveSlotIds.Contains(slotId))
                return SaveSlotState.SlotKindAuto;
            if (slotId == SaveSlotState.QuicksaveSlotId)
                return SaveSlotState.SlotKindQuick;
            return SaveSlotState.SlotKindManual; // safe default; loader will validate
        }

        SaveIndexState LoadIndex()
        {
            if (!Storage.FileExists(INDEX_PATH))
                return new SaveIndexState();
            string json = Storage.ReadText(INDEX_PATH);
            if (json == null)
                return new SaveIndexState();
            object parsed = GdJson.ParseString(json);
            return SaveIndexState.FromDict(parsed);
        }

        void SaveIndex(SaveIndexState idx)
        {
            if (!EnsureSaveDir())
                return;
            idx.UpdatedAt = Clock.DateTimeString(true);
            idx.GodotVersion = EngineVersionString;
            if (!TryWrite(INDEX_PATH, GdJson.Stringify(idx.ToDict(), "\t"), out string error))
                CoreServices.Log.Warning("SaveLoadService: cannot open index file for writing, error=" + error);
        }

        void IndexRunSlot(string slotId, string slotKind, string displayName, RunSnapshot snapshot, string payloadPath)
        {
            SaveIndexState idx = LoadIndex();
            var row = new SaveSlotState();
            row.SlotId = slotId;
            row.SlotKind = slotKind;
            row.DisplayName = !string.IsNullOrEmpty(displayName) ? displayName : slotId;
            // ADR-0046: index REAL metadata from the snapshot's dedicated fields.
            row.SynapticSeaSeed = snapshot.WorldSeed;
            row.PlayerClass = V.Str(snapshot.PlayerProgressionSummary.Get("class_id", ""));
            row.CurrentLocation = snapshot.CurrentLocation;
            row.ObjectiveSequence = snapshot.CurrentObjectiveSequence;
            row.PlayTimeSeconds = snapshot.PlayTimeSeconds;
            row.SavedAt = snapshot.SavedAt;
            row.SavedAtEpoch = NowEpoch();
            row.SchemaVersion = CURRENT_SLICE_VERSION;
            row.PayloadSizeBytes = SizeOfFile(payloadPath);
            row.RunId = snapshot.RunId;
            idx.AddOrReplace(row);
            SaveIndex(idx);
        }

        void IndexWorldSlot(WorldSnapshot worldSnapshot)
        {
            SaveIndexState idx = LoadIndex();
            var row = new SaveSlotState();
            row.SlotId = "world";
            row.SlotKind = SaveSlotState.SlotKindWorld;
            row.DisplayName = "World";
            row.CurrentLocation = worldSnapshot.CurrentLocation;
            row.ObjectiveSequence = 0;
            row.SavedAt = Clock.DateTimeString(true);
            row.SavedAtEpoch = NowEpoch();
            row.SchemaVersion = WorldSnapshot.WorldSliceVersion;
            row.PayloadSizeBytes = SizeOfFile(WORLD_SLOT_FILE);
            row.RunId = worldSnapshot.RunId;
            idx.AddOrReplace(row);
            SaveIndex(idx);
        }

        void WriteCloudManifest(string slotId, string slotPath, string schemaVersion)
        {
            if (!EnsureSaveDir())
                return;
            // Ensure the .cloud subdir exists.
            if (!Storage.DirExists(CLOUD_DIR))
            {
                try
                {
                    Storage.MakeDirRecursive(CLOUD_DIR);
                }
                catch (Exception)
                {
                    return; // silent: a failed manifest does not break the save
                }
            }
            CloudManifestState manifest = CloudManifestState.BuildForSlot(slotId, slotPath, schemaVersion, Storage, Clock);
            string manifestPath = CLOUD_DIR + "/" + slotId + ".manifest.json";
            if (!TryWrite(manifestPath, GdJson.Stringify(manifest.ToDict(), "\t"), out _))
                CoreServices.Log.Warning("SaveLoadService: cannot write cloud manifest for slot_id=" + slotId);
        }

        void BackupCorruptFile(string path, string slotId, long epoch)
        {
            if (!EnsureSaveDir())
                return;
            if (!Storage.DirExists(CORRUPT_DIR))
            {
                try
                {
                    Storage.MakeDirRecursive(CORRUPT_DIR);
                }
                catch (Exception)
                {
                    return;
                }
            }
            string baseName = ResPath.GetFile(path);
            string backupPath = CORRUPT_DIR + "/" + slotId + "." + GdString.FormatInt(epoch) + "." + baseName + ".bak";
            if (Storage.FileExists(path))
            {
                if (!Storage.Rename(path, backupPath))
                    CoreServices.Log.Warning("SaveLoadService: failed to backup corrupt file " + path + " -> " + backupPath + " error=rename_failed");
            }
            // Mark the slot row corrupt in the index.
            SaveIndexState idx = LoadIndex();
            SaveSlotState row = idx.Find(slotId);
            if (row != null)
            {
                row.Corrupt = true;
                SaveIndex(idx);
            }
        }

        long SizeOfFile(string path)
        {
            if (!Storage.FileExists(path))
                return 0;
            string content = Storage.ReadText(path);
            if (content == null)
                return 0;
            // FileAccess.get_length(): byte length of the UTF-8 file.
            return Encoding.UTF8.GetByteCount(content);
        }

        /// <summary><c>int(Time.get_unix_time_from_system())</c>.</summary>
        long NowEpoch() => GdMath.Trunc(Clock.UnixTime());

        /// <summary>
        /// <c>FileAccess.open(path, WRITE)</c> + <c>store_string</c>: false (with the error text) when the storage
        /// write throws, which is where Godot's open returned null.
        /// </summary>
        bool TryWrite(string path, string text, out string error)
        {
            try
            {
                Storage.WriteText(path, text);
                error = "";
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
