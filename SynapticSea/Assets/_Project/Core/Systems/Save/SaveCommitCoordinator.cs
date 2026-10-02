using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Explicit read-only lifecycle authority; absent or uncertain answers must fail closed.</summary>
    public interface ISaveGenerationTerminalAuthority
    {
        GdDict Query(string runId, string slotId);
    }

    /// <summary>
    /// Unused supplied-payload F05 prerequisite. Exact supplied document bytes are immutable; a verified
    /// shared slot pointer selects one complete generation. Live capture, migration and reclaim are external.
    /// </summary>
    public sealed class SaveCommitCoordinator
    {
        public const string PayloadVersion = "save-payload-bundle-1";
        public const string CommitVersion = "save-commit-1";
        public const string PointerVersion = "save-pointer-1";
        public const string IndexVersion = "save-generation-index-1";
        public const string TerminalVersion = "save-run-terminal-1";

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly string[] Slots = { "world", "autosave_active", "autosave_a", "autosave_b", "autosave_c", "quicksave", "slot_01", "slot_02", "slot_03", "slot_04", "slot_05", "slot_06" };
        readonly IStorage _storage;
        readonly string _root;
        readonly bool _validRoot;
        readonly ISaveGenerationTerminalAuthority _authority;
        readonly GdDict _compatibility;
        readonly Action<string> _fault;
        readonly object _gate = new object();
        bool _mutating;

        public SaveCommitCoordinator(IStorage storage, string root,
            ISaveGenerationTerminalAuthority terminalAuthority, GdDict compatibility,
            Action<string> fault = null)
        {
            _storage = storage;
            _root = root;
            _validRoot = ValidRoot(root) && storage != null;
            _authority = terminalAuthority;
            _compatibility = SafeGraph(compatibility) && ValidCompatibility(compatibility) ? compatibility.DeepCopy() : null;
            _fault = fault;
        }

        public GdDict Commit(GdDict payloads, string runId, string slotId)
        {
            lock (_gate)
            {
                if (_mutating) return Result(false, "reentrant", runId, slotId);
                _mutating = true;
                Candidate candidate = null;
                bool publicationAttempted = false;
                try
                {
                    Guard(runId, slotId);
                    candidate = ValidateRequest(payloads, runId, slotId);
                    CheckCandidatePaths(candidate);
                    GdDict terminalWitness = RequireLive(runId, slotId);
                    Candidate current = CurrentForCommit(runId, slotId, out string oldPointer);
                    if (current != null && current.Id == candidate.Id)
                    {
                        if (!Equivalent(candidate.Request, current.Request)) throw new Refusal("generation_conflict");
                        GdDict repeated = Success(current, "already_committed");
                        // Exact replay performs no publication, but its verified disk commit still
                        // needs current playability after rereading the immutable payload closure.
                        try { RequireLive(current.Run, current.Slot); }
                        catch (Exception lifecycleError)
                        {
                            GdDict blocked = Failure(lifecycleError, current.Run, current.Slot);
                            repeated["ok"] = false; repeated["reason"] = blocked.GetString("reason"); repeated["payloads"] = null;
                            repeated["detail"] = Description(lifecycleError);
                        }
                        return repeated;
                    }
                    CheckParent(candidate, current, oldPointer);
                    candidate.ParentPointer = oldPointer ?? "";
                    candidate.Manifest = MakeManifest(candidate, terminalWitness);
                    candidate.ManifestText = Encode(candidate.Manifest);
                    string directory = Generation(runId, slotId, candidate.Id);
                    string manifestPath = directory + "/commit.json";
                    if (_storage.FileExists(manifestPath))
                    {
                        Candidate existing = LoadGeneration(runId, slotId, candidate.Id);
                        if (!Equivalent(candidate.Request, existing.Request) || existing.ParentPointer != candidate.ParentPointer) throw new Refusal("generation_conflict");
                        candidate = existing;
                    }
                    else
                    {
                        _storage.MakeDirRecursive(directory);
                        foreach (Entry entry in candidate.Entries)
                        {
                            Hook("before_payload:" + entry.Key);
                            WriteImmutable(directory + "/" + entry.File, entry.Text);
                            Hook("after_payload:" + entry.Key);
                        }
                        Hook("before_validation");
                        foreach (Entry entry in candidate.Entries) VerifyText(directory + "/" + entry.File, entry.Text);
                        Hook("after_validation");
                        Hook("before_manifest");
                        WriteImmutable(manifestPath, candidate.ManifestText);
                        Hook("after_manifest");
                        candidate = LoadGeneration(runId, slotId, candidate.Id, Hash(candidate.ManifestText));
                    }
                    RequireLive(runId, slotId);
                    CheckSelection(candidate, oldPointer);
                    if (oldPointer != null)
                    {
                        Hook("before_previous_pointer");
                        CheckPhysicalPath(Previous(slotId));
                        _storage.WriteText(Previous(slotId), oldPointer);
                        VerifyText(Previous(slotId), oldPointer);
                        Hook("after_previous_pointer");
                    }
                    Hook("before_pointer");
                    RequireLive(runId, slotId);
                    CheckSelection(candidate, oldPointer);
                    CheckPhysicalPath(Active(slotId));
                    publicationAttempted = true;
                    string pointer = PointerText(candidate);
                    _storage.WriteText(Active(slotId), pointer);
                    VerifySelected(candidate, pointer);
                    Hook("after_pointer");
                    return Finish(candidate, "committed", false);
                }
                catch (Exception error)
                {
                    if (publicationAttempted && candidate != null)
                        return PublicationOutcome(candidate, error);
                    return Failure(error, runId, slotId);
                }
                finally { _mutating = false; }
            }
        }

        public GdDict Recover(string runId, string slotId)
        {
            lock (_gate)
            {
                if (_mutating) return Result(false, "reentrant", runId, slotId);
                _mutating = true;
                Candidate promoted = null;
                bool publicationAttempted = false;
                try
                {
                    Guard(runId, slotId);
                    CheckOperationPaths(runId, slotId);
                    RequireLive(runId, slotId);
                    if (_storage.FileExists(Active(slotId)))
                    {
                        string active = _storage.ReadText(Active(slotId));
                        GdDict header = TryPointer(active, slotId);
                        if (header != null && header.GetString("run_id") != runId) throw new Refusal("slot_owner_conflict");
                        Candidate selected = TrySelected(active, runId, slotId);
                        if (selected != null) return Finish(selected, "pointer", false);
                        string previous = _storage.FileExists(Previous(slotId)) ? _storage.ReadText(Previous(slotId)) : null;
                        selected = TrySelected(previous, runId, slotId);
                        if (selected == null) throw new Refusal("corrupt_generation");
                        RequireLive(runId, slotId);
                        return Finish(selected, "previous_pointer", true, selected);
                    }
                    CheckPreviousOwner(runId, slotId);
                    var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
                    var diagnostics = new GdArray();
                    bool uncertain = Discover(runId, slotId, candidates, diagnostics);
                    if (candidates.Count == 0 && !uncertain) return Result(false, "not_found", runId, slotId);
                    var parents = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Candidate candidate in candidates.Values)
                    {
                        if (!Resolved(candidate, candidates, new HashSet<string>(StringComparer.Ordinal))) uncertain = true;
                        if (candidate.Parent.Length > 0) parents.Add(candidate.Parent);
                    }
                    var tips = candidates.Values.Where(c => !parents.Contains(c.Id)).ToList();
                    if (uncertain || tips.Count != 1)
                    {
                        GdDict ambiguous = Result(false, "ambiguous", runId, slotId);
                        ambiguous["candidates"] = diagnostics;
                        return ambiguous;
                    }
                    promoted = tips[0];
                    Hook("before_pointer");
                    RequireLive(runId, slotId);
                    if (_storage.FileExists(Active(slotId)))
                    {
                        GdDict other = TryPointer(_storage.ReadText(Active(slotId)), slotId);
                        throw new Refusal(other != null && other.GetString("run_id") != runId ? "slot_owner_conflict" : "stale_parent");
                    }
                    CheckPreviousOwner(runId, slotId);
                    CheckPhysicalPath(Active(slotId));
                    publicationAttempted = true;
                    string pointer = PointerText(promoted);
                    _storage.WriteText(Active(slotId), pointer);
                    VerifySelected(promoted, pointer);
                    Hook("after_pointer");
                    return Finish(promoted, "lineage_tip", true);
                }
                catch (Exception error)
                {
                    if (error is Refusal refusal && TerminalRefusal(refusal.Reason))
                    {
                        GdDict blocked = Failure(error, runId, slotId);
                        if (publicationAttempted) { blocked["committed"] = true; blocked["outcome"] = "committed"; }
                        return blocked;
                    }
                    if (publicationAttempted && promoted != null) return PublicationOutcome(promoted, error, true);
                    return Failure(error, runId, slotId);
                }
                finally { _mutating = false; }
            }
        }

        public GdDict RecordTerminal(GdDict terminal, string runId)
        {
            lock (_gate)
            {
                if (_mutating) return Result(false, "reentrant", runId, "");
                _mutating = true;
                GdDict wire = null;
                bool attempted = false;
                try
                {
                    Guard(runId, null);
                    if (!SafeGraph(terminal)) throw new Refusal("invalid_request");
                    GdDict copy = terminal.DeepCopy();
                    if (!(copy.Get("terminal_revision") is long revision) || revision < 0) throw new Refusal("invalid_request");
                    copy["terminal_revision"] = Decimal((long)copy.Get("terminal_revision"));
                    if (!ValidTerminal(copy, runId)) throw new Refusal("invalid_request");
                    wire = copy;
                    string path = Tombstone(runId);
                    CheckPhysicalPath(path);
                    if (_storage.FileExists(path))
                    {
                        GdDict prior = ParseObject(_storage.ReadText(path));
                        if (prior == null || !ValidTerminal(prior, runId)) throw new Refusal("terminal_ambiguous");
                        if (!V.VariantEquals(prior, wire)) throw new Refusal("generation_conflict");
                        return TerminalSuccess(runId, "already_terminal");
                    }
                    Hook("before_terminal");
                    CheckPhysicalPath(path);
                    attempted = true;
                    _storage.WriteText(path, Encode(wire));
                    VerifyText(path, Encode(wire));
                    Hook("after_terminal");
                    return TerminalSuccess(runId, "terminal");
                }
                catch (Exception error)
                {
                    if (attempted && wire != null)
                    {
                        try
                        {
                            GdDict actual = ParseObject(_storage.ReadText(Tombstone(runId)));
                            if (actual != null && ValidTerminal(actual, runId) && V.VariantEquals(actual, wire)) return TerminalSuccess(runId, "terminal");
                        }
                        catch (Exception) { }
                        return Unknown(runId, "", "", error);
                    }
                    return Failure(error, runId, "");
                }
                finally { _mutating = false; }
            }
        }

        public GdDict QueryTerminal(string runId)
        {
            lock (_gate)
            {
                try
                {
                    Guard(runId, null);
                    CheckPhysicalPath(Tombstone(runId));
                    GdDict terminal = ReadTerminal(runId);
                    GdDict result = Result(true, terminal == null ? "live" : "terminal", runId, "");
                    result["status"] = terminal == null ? "live" : terminal.GetString("state");
                    result["terminal"] = terminal == null ? null : DecodeTerminal(terminal);
                    return result;
                }
                catch (Exception error)
                {
                    GdDict result = Failure(error, runId, "");
                    result["status"] = "unknown"; result["terminal"] = null;
                    return result;
                }
            }
        }

        GdDict Finish(Candidate candidate, string reason, bool recovered, Candidate previousSelection = null)
        {
            // Playability refusal is separate from a derived index failure, including after a successful
            // index rebuild. Never expose a previously validated candidate after terminal authority changed.
            RequireLive(candidate.Run, candidate.Slot);
            GdDict result = Success(candidate, reason, recovered);
            try
            {
                Hook("before_index");
                ReconcileIndex(previousSelection);
                Hook("after_index");
            }
            catch (Exception error)
            {
                result["reason"] = "index_reconciliation_needed";
                result["index_reconciliation_needed"] = true;
                result["detail"] = Description(error);
                if (recovered) result["recovery_reason"] = reason;
            }
            RequireLive(candidate.Run, candidate.Slot);
            return result;
        }

        GdDict PublicationOutcome(Candidate candidate, Exception error, bool recovered = false)
        {
            try
            {
                Candidate actual = TrySelected(_storage.ReadText(Active(candidate.Slot)), candidate.Run, candidate.Slot);
                if (actual != null && actual.Id == candidate.Id && Hash(actual.ManifestText) == Hash(candidate.ManifestText))
                {
                    GdDict committed = Success(actual, "index_reconciliation_needed", recovered);
                    committed["index_reconciliation_needed"] = true;
                    committed["detail"] = Description(error);
                    if (error is Refusal refusal && TerminalRefusal(refusal.Reason))
                    {
                        committed["ok"] = false; committed["reason"] = refusal.Reason; committed["payloads"] = null;
                    }
                    // Reopening proves publication, not current playability. An ordinary postwrite
                    // exception may follow a terminal transition, so retain the disk outcome while
                    // refusing payloads for every final lifecycle/authority failure.
                    try { RequireLive(actual.Run, actual.Slot); }
                    catch (Exception lifecycleError)
                    {
                        GdDict blocked = Failure(lifecycleError, actual.Run, actual.Slot);
                        committed["ok"] = false; committed["reason"] = blocked.GetString("reason"); committed["payloads"] = null;
                        committed["detail"] = Description(lifecycleError);
                    }
                    return committed;
                }
                if (actual != null) return Failure(error, candidate.Run, candidate.Slot);
            }
            catch (Exception) { }
            return Unknown(candidate.Run, candidate.Slot, candidate.Id, error);
        }

        void ReconcileIndex(Candidate previousSelection)
        {
            var rows = new GdArray();
            foreach (string slot in Slots.OrderBy(s => s, StringComparer.Ordinal))
            {
                if (!_storage.FileExists(Active(slot))) continue;
                string pointer = _storage.ReadText(Active(slot));
                GdDict header = TryPointer(pointer, slot);
                Candidate selected = null;
                string selection = "pointer";
                if (header != null) selected = TrySelected(pointer, header.GetString("run_id"), slot);
                if (selected == null && previousSelection != null && previousSelection.Slot == slot)
                { selected = previousSelection; selection = "previous_pointer"; }
                if (selected == null) throw new Refusal("corrupt_generation");
                bool frozen;
                try { RequireLive(selected.Run, selected.Slot); frozen = false; }
                catch (Refusal refusal)
                {
                    if (refusal.Reason != "run_terminal" && refusal.Reason != "legacy_death") throw;
                    frozen = true;
                }
                rows.Add(new GdDict
                {
                    { "run_id", selected.Run }, { "slot_id", selected.Slot }, { "generation_id", selected.Id },
                    { "manifest_sha256", Hash(selected.ManifestText) }, { "domain_revision", Decimal(selected.Revision) },
                    { "frozen", frozen }, { "selection", selection }
                });
            }
            string text = Encode(new GdDict { { "schema_version", IndexVersion }, { "slots", rows } });
            string path = _root + "/index.json";
            CheckPhysicalPath(path);
            if (!_storage.FileExists(path) || _storage.ReadText(path) != text)
            { _storage.WriteText(path, text); VerifyText(path, text); }
        }

        Candidate CurrentForCommit(string runId, string slotId, out string pointer)
        {
            pointer = null;
            if (!_storage.FileExists(Active(slotId))) { CheckPreviousOwner(runId, slotId); return null; }
            pointer = _storage.ReadText(Active(slotId));
            GdDict header = TryPointer(pointer, slotId);
            if (header != null && header.GetString("run_id") != runId) throw new Refusal("slot_owner_conflict");
            Candidate current = TrySelected(pointer, runId, slotId);
            if (current == null) throw new Refusal("corrupt_generation");
            return current;
        }

        void CheckParent(Candidate candidate, Candidate current, string pointer)
        {
            if (current == null)
            {
                if (candidate.Parent.Length != 0 || candidate.Request.GetString("expected_pointer_sha256").Length != 0) throw new Refusal("stale_parent");
                var known = new Dictionary<string, Candidate>(StringComparer.Ordinal);
                var diagnostics = new GdArray();
                bool uncertain = Discover(candidate.Run, candidate.Slot, known, diagnostics);
                if (uncertain || known.Values.Any(c => c.Id != candidate.Id)) throw new Refusal("ambiguous");
                return;
            }
            if (candidate.Parent != current.Id || candidate.Request.GetString("expected_pointer_sha256") != Hash(pointer)) throw new Refusal("stale_parent");
            if (candidate.Revision <= current.Revision || !OwnerRevisionsAdvance(candidate.Request, current.Request)) throw new Refusal("stale_revision");
        }

        void CheckSelection(Candidate candidate, string originalPointer)
        {
            bool present = _storage.FileExists(Active(candidate.Slot));
            if (originalPointer == null && !present) { CheckPreviousOwner(candidate.Run, candidate.Slot); return; }
            string current = present ? _storage.ReadText(Active(candidate.Slot)) : null;
            GdDict header = TryPointer(current, candidate.Slot);
            if (header != null && header.GetString("run_id") != candidate.Run) throw new Refusal("slot_owner_conflict");
            if (current != originalPointer) throw new Refusal("stale_parent");
        }

        void CheckPreviousOwner(string run, string slot)
        {
            if (!_storage.FileExists(Previous(slot))) return;
            string text = _storage.ReadText(Previous(slot));
            GdDict pointer = TryPointer(text, slot);
            if (pointer == null) throw new Refusal("corrupt_generation");
            if (pointer.GetString("run_id") != run) throw new Refusal("slot_owner_conflict");
        }

        bool Discover(string run, string slot, Dictionary<string, Candidate> candidates, GdArray diagnostics)
        {
            bool uncertain = false;
            string directory = Generations(run, slot);
            foreach (string token in _storage.ListDirectories(directory))
            {
                if (!Hex(token)) { uncertain = true; diagnostics.Add(new GdDict { { "token", token }, { "reason", "invalid_identity" } }); continue; }
                string path = directory + "/" + token + "/commit.json";
                if (!_storage.FileExists(path)) continue;
                try
                {
                    GdDict manifest = ParseObject(_storage.ReadText(path));
                    if (manifest == null || !StringFields(manifest, "schema_version", "status", "run_id", "slot_id", "generation_id", "slot_kind") ||
                        manifest.GetString("schema_version") != CommitVersion || manifest.GetString("status") != "complete" ||
                        !Identity(manifest.GetString("run_id")) || !KnownSlot(manifest.GetString("slot_id")) || !Identity(manifest.GetString("generation_id")) ||
                        manifest.GetString("slot_kind") != Kind(manifest.GetString("slot_id")) ||
                        TupleToken(manifest.GetString("run_id"), manifest.GetString("slot_id"), manifest.GetString("generation_id")) != token) throw new Refusal("corrupt_generation");
                    // Only a current strictly typed header bound to its full tuple token establishes
                    // foreign ownership. Unknown or malformed complete records stay ambiguity blockers.
                    if (manifest.GetString("run_id") != run || manifest.GetString("slot_id") != slot) continue;
                    Candidate candidate = LoadGeneration(run, slot, manifest.GetString("generation_id"));
                    if (candidates.ContainsKey(candidate.Id)) throw new Refusal("generation_conflict");
                    candidates.Add(candidate.Id, candidate);
                    diagnostics.Add(new GdDict { { "generation_id", candidate.Id }, { "reason", "complete" } });
                }
                catch (Exception error)
                {
                    uncertain = true;
                    diagnostics.Add(new GdDict { { "token", token }, { "reason", error is Refusal refusal ? refusal.Reason : "storage_failure" } });
                }
            }
            return uncertain;
        }

        bool Resolved(Candidate candidate, Dictionary<string, Candidate> candidates, HashSet<string> ancestors)
        {
            if (!ancestors.Add(candidate.Id)) return false;
            if (candidate.Parent.Length == 0) return candidate.ParentPointer.Length == 0;
            if (!candidates.TryGetValue(candidate.Parent, out Candidate parent) || candidate.Revision <= parent.Revision || !OwnerRevisionsAdvance(candidate.Request, parent.Request)) return false;
            GdDict pointer = TryPointer(candidate.ParentPointer, candidate.Slot);
            return pointer != null && pointer.GetString("run_id") == candidate.Run && pointer.GetString("generation_id") == parent.Id &&
                pointer.GetString("manifest_sha256") == Hash(parent.ManifestText) && Resolved(parent, candidates, ancestors);
        }

        Candidate TrySelected(string text, string run, string slot)
        {
            GdDict pointer = TryPointer(text, slot);
            if (pointer == null || pointer.GetString("run_id") != run) return null;
            try { return LoadGeneration(run, slot, pointer.GetString("generation_id"), pointer.GetString("manifest_sha256")); }
            catch (Refusal) { return null; }
        }

        GdDict TryPointer(string text, string slot)
        {
            GdDict pointer = ParseObject(text);
            if (pointer == null || pointer.Count != 5 || pointer.GetString("schema_version") != PointerVersion ||
                !StringFields(pointer, "schema_version", "run_id", "slot_id", "generation_id", "manifest_sha256") ||
                !Identity(pointer.GetString("run_id")) || pointer.GetString("slot_id") != slot ||
                !Identity(pointer.GetString("generation_id")) || !Hex(pointer.GetString("manifest_sha256"))) return null;
            return pointer;
        }

        void VerifySelected(Candidate candidate, string pointer)
        {
            VerifyText(Active(candidate.Slot), pointer);
            Candidate selected = TrySelected(pointer, candidate.Run, candidate.Slot);
            if (selected == null || selected.Id != candidate.Id || Hash(selected.ManifestText) != Hash(candidate.ManifestText)) throw new Refusal("corrupt_generation");
        }

        string PointerText(Candidate candidate) => Encode(new GdDict
        {
            { "schema_version", PointerVersion }, { "run_id", candidate.Run }, { "slot_id", candidate.Slot },
            { "generation_id", candidate.Id }, { "manifest_sha256", Hash(candidate.ManifestText) }
        });

        Candidate LoadGeneration(string run, string slot, string id, string expectedManifestHash = null)
        {
            if (!Identity(id)) throw new Refusal("corrupt_generation");
            string directory = Generation(run, slot, id);
            CheckPhysicalPath(directory + "/commit.json");
            string text = _storage.ReadText(directory + "/commit.json");
            if (text == null || expectedManifestHash != null && Hash(text) != expectedManifestHash) throw new Refusal("corrupt_generation");
            GdDict manifest = ParseObject(text);
            if (manifest == null || !StringFields(manifest, "schema_version", "status", "run_id", "slot_id", "generation_id", "slot_kind", "parent_generation_id", "expected_pointer_sha256", "parent_pointer_text") ||
                manifest.GetString("schema_version") != CommitVersion || manifest.GetString("status") != "complete" ||
                manifest.GetString("run_id") != run || manifest.GetString("slot_id") != slot || manifest.GetString("generation_id") != id ||
                !(manifest.Get("request_metadata") is GdDict metadata) || !(manifest.Get("entries") is GdArray entries) ||
                !(manifest.Get("artifact_order") is GdArray order) || !(manifest.Get("parent_pointer_text") is string parentPointer)) throw new Refusal("corrupt_generation");
            GdDict request = DecodeMetadata(manifest.GetDictOrEmpty("request_metadata"));
            var artifacts = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            var validatedEntries = new List<GdDict>();
            var logicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var artifactFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (object value in manifest.GetArrayOrEmpty("entries"))
            {
                if (!(value is GdDict entry) || entry.Count != 7 || !StringFields(entry, "role", "logical_path", "document_kind", "schema_version", "file", "sha256", "byte_length")) throw new Refusal("corrupt_generation");
                GdDict e = (GdDict)value;
                string role = e.GetString("role"), logical = e.GetString("logical_path");
                string key = role == "artifact" ? logical : role;
                if (!roles.Add(key) || role != "run" && role != "world" && role != "artifact" || role == "artifact" && !LogicalPath(logical) || role != "artifact" && logical.Length != 0) throw new Refusal("corrupt_generation");
                if (!Hex(e.GetString("sha256")) || !WireLong(e.Get("byte_length"), out _)) throw new Refusal("corrupt_generation");
                if (role == "artifact")
                {
                    if (!logicalIds.Add(logical)) throw new Refusal("corrupt_generation");
                    artifactFiles.Add(logical, "");
                }
                validatedEntries.Add(e);
            }
            var sortedLogical = artifactFiles.Keys.OrderBy(logical => logical, StringComparer.Ordinal).ToList();
            for (int ordinal = 0; ordinal < sortedLogical.Count; ordinal++) artifactFiles[sortedLogical[ordinal]] = ArtifactFile(ordinal);
            if (!roles.Contains("run") || !roles.Contains("world") || order.Count != artifactFiles.Count) throw new Refusal("corrupt_generation");
            // Resolve and validate the entire map before reading any payload. Claimed filenames are
            // metadata assertions; only the recomputed ordinal/role names become storage paths.
            foreach (GdDict e in validatedEntries)
            {
                string role = e.GetString("role"), logical = e.GetString("logical_path");
                string file = role == "artifact" ? artifactFiles[logical] : role + ".json";
                if (e.GetString("file") != file) throw new Refusal("corrupt_generation");
                CheckPhysicalPath(directory + "/" + file);
            }
            foreach (GdDict e in validatedEntries)
            {
                string role = e.GetString("role"), logical = e.GetString("logical_path");
                string file = role == "artifact" ? artifactFiles[logical] : role + ".json";
                if (e.GetString("file") != file || !Hex(e.GetString("sha256")) || !WireLong(e.Get("byte_length"), out long bytes)) throw new Refusal("corrupt_generation");
                string payload = _storage.ReadText(directory + "/" + file);
                if (payload == null || Utf8.GetByteCount(payload) != bytes || Hash(payload) != e.GetString("sha256")) throw new Refusal("corrupt_generation");
                if (role == "run" || role == "world") request[role + "_text"] = payload;
                else artifacts.Add(logical, new GdDict { { "logical_path", logical }, { "document_kind", e.GetString("document_kind") }, { "schema_version", e.GetString("schema_version") }, { "text", payload } });
            }
            if (!roles.Contains("run") || !roles.Contains("world") || order.Count != artifacts.Count) throw new Refusal("corrupt_generation");
            var ordered = new GdArray();
            var orderedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in manifest.GetArrayOrEmpty("artifact_order"))
            {
                if (!(item is string logical) || !orderedIds.Add(logical) || !artifacts.TryGetValue(logical, out GdDict artifact)) throw new Refusal("corrupt_generation");
                ordered.Add(artifacts[(string)item]);
            }
            request["artifacts"] = ordered;
            Candidate candidate = ValidateRequest(request, run, slot);
            if (candidate.Id != id || manifest.GetString("slot_kind") != request.GetString("slot_kind") || manifest.GetString("parent_generation_id") != candidate.Parent ||
                manifest.GetString("expected_pointer_sha256") != request.GetString("expected_pointer_sha256") ||
                !WireLong(manifest.Get("domain_revision"), out long revision) || revision != candidate.Revision ||
                !V.VariantEquals(manifest.Get("binding"), WireBinding(request.GetDictOrEmpty("binding"))) ||
                !V.VariantEquals(manifest.Get("compatibility"), request.Get("compatibility"))) throw new Refusal("corrupt_generation");
            foreach (Entry entry in candidate.Entries)
            {
                GdDict expected = EntryMetadata(entry);
                bool matched = manifest.GetArrayOrEmpty("entries").Any(v => v is GdDict actual && V.VariantEquals(expected, actual));
                if (!matched) throw new Refusal("corrupt_generation");
            }
            candidate.ParentPointer = (string)manifest.Get("parent_pointer_text");
            if (candidate.Parent.Length == 0)
            { if (candidate.ParentPointer.Length > 0 || request.GetString("expected_pointer_sha256").Length > 0) throw new Refusal("corrupt_generation"); }
            else
            {
                GdDict parent = TryPointer(candidate.ParentPointer, slot);
                if (parent == null || parent.GetString("run_id") != run || parent.GetString("generation_id") != candidate.Parent ||
                    Hash(candidate.ParentPointer) != request.GetString("expected_pointer_sha256")) throw new Refusal("corrupt_generation");
            }
            if (!(manifest.Get("terminal_witness") is GdDict witness) || !StringFields(witness, "status", "run_id", "slot_id") ||
                !(witness.Get("ok") is bool permitted) || !permitted || !(witness.Get("legacy_witnesses") is GdArray) ||
                witness.GetString("status") != "live" || witness.GetString("run_id") != run || witness.GetString("slot_id") != slot) throw new Refusal("corrupt_generation");
            foreach (object value in witness.GetArrayOrEmpty("legacy_witnesses"))
            {
                if (!(value is GdDict prior) || !StringFields(prior, "run_id", "slot_id", "path") || prior.GetString("run_id") != run || !KnownSlot(prior.GetString("slot_id"))) throw new Refusal("corrupt_generation");
                string witnessSlot = prior.GetString("slot_id"), witnessPath = prior.GetString("path");
                if (witnessPath != Death(witnessSlot) && witnessPath != LegacyPayload(witnessSlot) && witnessPath != LegacyPayload(witnessSlot) + ".tmp" && witnessPath != "user://saves/index.json") throw new Refusal("corrupt_generation");
            }
            candidate.Manifest = manifest; candidate.ManifestText = text;
            return candidate;
        }

        GdDict MakeManifest(Candidate candidate, GdDict witness)
        {
            GdDict metadata = candidate.Request.DeepCopy();
            metadata.Erase("run_text"); metadata.Erase("world_text"); metadata.Erase("artifacts");
            metadata["domain_revision"] = Decimal(candidate.Revision);
            metadata["binding"] = WireBinding(candidate.Request.GetDictOrEmpty("binding"));
            var entries = new GdArray();
            foreach (Entry entry in candidate.Entries.OrderBy(e => e.Role, StringComparer.Ordinal).ThenBy(e => e.Logical, StringComparer.Ordinal)) entries.Add(EntryMetadata(entry));
            var order = new GdArray();
            foreach (GdDict artifact in candidate.Request.GetArrayOrEmpty("artifacts")) order.Add(artifact.GetString("logical_path"));
            return new GdDict
            {
                { "schema_version", CommitVersion }, { "status", "complete" }, { "run_id", candidate.Run }, { "slot_id", candidate.Slot },
                { "slot_kind", candidate.Request.GetString("slot_kind") }, { "generation_id", candidate.Id }, { "parent_generation_id", candidate.Parent },
                { "expected_pointer_sha256", candidate.Request.GetString("expected_pointer_sha256") }, { "parent_pointer_text", candidate.ParentPointer },
                { "domain_revision", Decimal(candidate.Revision) }, { "binding", metadata.GetDictOrEmpty("binding").DeepCopy() },
                { "compatibility", candidate.Request.GetDictOrEmpty("compatibility").DeepCopy() }, { "request_metadata", metadata },
                { "entries", entries }, { "artifact_order", order }, { "terminal_witness", witness.DeepCopy() }
            };
        }

        static GdDict EntryMetadata(Entry entry) => new GdDict
        {
            { "role", entry.Role }, { "logical_path", entry.Logical }, { "document_kind", entry.Kind },
            { "schema_version", entry.Version }, { "file", entry.File }, { "sha256", Hash(entry.Text) },
            { "byte_length", Decimal(Utf8.GetByteCount(entry.Text)) }
        };

        static GdDict WireBinding(GdDict binding)
        {
            GdDict copy = binding.DeepCopy(), revisions = copy.GetDictOrEmpty("owner_revisions");
            foreach (object key in revisions.Keys.ToList()) revisions[key] = Decimal((long)revisions[key]);
            return copy;
        }

        static GdDict DecodeMetadata(GdDict metadata)
        {
            GdDict copy = metadata.DeepCopy();
            if (!WireLong(copy.Get("domain_revision"), out long revision) || !(copy.Get("binding") is GdDict binding) || !(binding.Get("owner_revisions") is GdDict)) throw new Refusal("corrupt_generation");
            copy["domain_revision"] = revision;
            GdDict revisions = copy.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions");
            foreach (object key in revisions.Keys.ToList())
            {
                if (!WireLong(revisions[key], out long value)) throw new Refusal("corrupt_generation");
                revisions[key] = value;
            }
            return copy;
        }

        Candidate ValidateRequest(GdDict supplied, string run, string slot)
        {
            if (supplied == null || !SafeGraph(supplied) || supplied.Count != 13) throw new Refusal("invalid_request");
            GdDict request = supplied.DeepCopy();
            if (!StringFields(request, "schema_version", "generation_id", "parent_generation_id", "expected_pointer_sha256", "run_id", "slot_id", "slot_kind") ||
                request.GetString("schema_version") != PayloadVersion || request.GetString("run_id") != run || request.GetString("slot_id") != slot ||
                request.GetString("slot_kind") != Kind(slot) || !Identity(request.GetString("generation_id")) ||
                !(request.Get("parent_generation_id") is string parent) || parent.Length > 0 && !Identity(parent) ||
                !(request.Get("expected_pointer_sha256") is string expectedPointer) || expectedPointer.Length > 0 && !Hex(expectedPointer) ||
                !(request.Get("domain_revision") is long revision) || revision < 0 || !(request.Get("binding") is GdDict) ||
                !(request.Get("compatibility") is GdDict) || !(request.Get("run_text") is string) || !(request.Get("world_text") is string) || !(request.Get("artifacts") is GdArray)) throw new Refusal("invalid_request");
            if (_compatibility == null || !V.VariantEquals(request.Get("compatibility"), _compatibility)) throw new Refusal("incompatible_content");
            var candidate = new Candidate(request);
            var artifacts = new Dictionary<string, Artifact>(StringComparer.Ordinal);
            var logicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in request.GetArrayOrEmpty("artifacts"))
            {
                if (!(value is GdDict artifact) || artifact.Count != 4 || !(artifact.Get("logical_path") is string logical) || !LogicalPath(logical) ||
                    !logicalIds.Add(logical) || !(artifact.Get("text") is string)) throw new Refusal("invalid_reference");
                GdDict a = (GdDict)value;
                string path = a.GetString("logical_path"), text = a.GetString("text"), kind = a.GetString("document_kind"), version = a.GetString("schema_version");
                GdDict document = ParseObject(text);
                if (!StringFields(a, "logical_path", "document_kind", "schema_version", "text") || document == null ||
                    !StringFields(document, "document_kind", "schema_version") || document.GetString("document_kind") != kind || document.GetString("schema_version") != version) throw new Refusal("invalid_reference");
                if (kind == "ship_layout")
                { if (version != "1.2.0") throw new Refusal("unsupported_schema"); if (!ValidLayout(document)) throw new Refusal("invalid_reference"); }
                else if (kind == "ship_gameplay_slice")
                { if (version != "1.1.0") throw new Refusal("unsupported_schema"); if (!Nonempty(document.Get("start_room")) || !Nonempty(document.Get("goal_room"))) throw new Refusal("invalid_reference"); }
                else if (kind == "ship_structural_catalog")
                { if (version != "1.0.0") throw new Refusal("unsupported_schema"); if (!ValidKit(document)) throw new Refusal("invalid_reference"); }
                else throw new Refusal("unsupported_schema");
                artifacts.Add(path, new Artifact(kind, document));
                candidate.Entries.Add(new Entry("artifact", path, kind, version, text));
            }
            GdDict active = ParseObject(request.GetString("run_text")), world = ParseObject(request.GetString("world_text"));
            if (active == null || world == null) throw new Refusal("invalid_payload");
            if (active.GetString("slice_version") != "gate2-current-run-6" || world.GetString("slice_version") != "world-4") throw new Refusal("unsupported_schema");
            if (!ValidRun(active) || !Shape(world, new WorldSnapshot().ToDict()) || world.GetString("godot_version") != _compatibility.GetString("engine_version") ||
                active.GetString("run_id") != run || world.GetString("run_id") != run || active.GetString("slot_id") != slot || active.GetString("slot_kind") != Kind(slot) ||
                active.GetBool("is_autosave") != (Kind(slot) == "auto") || active.GetBool("is_quicksave") != (Kind(slot) == "quick") || active.GetString("parent_world_slot").Length > 0 ||
                !Position(world.Get("player_position_in_ship")) || !Number(world.Get("world_time"), out double time) || time < 0) throw new Refusal("invalid_payload");
            GdDict home = world.GetDictOrEmpty("home_ship"), summary = world.GetDictOrEmpty("world_summary");
            if (!ValidRun(home) || home.GetString("run_id").Length > 0 && home.GetString("run_id") != run ||
                !JsonInteger(active.Get("world_seed"), out long activeSeed, true) || !JsonInteger(home.Get("world_seed"), out long homeSeed, true) ||
                !JsonInteger(summary.Get("world_seed"), out long worldSeed, true) || activeSeed != homeSeed || activeSeed != worldSeed ||
                !Position(summary.Get("player_position")) || !(summary.Get("generated_marker_ids") is GdArray)) throw new Refusal("binding_mismatch");
            GdDict visited = world.GetDictOrEmpty("visited_ships");
            if (!V.VariantEquals(active.Get("visited_ships"), visited)) throw new Refusal("binding_mismatch");
            GdDict binding = request.GetDictOrEmpty("binding");
            if (binding.Count != 7 || !StringFields(binding, "home_ship_id", "lifeboat_ship_id", "current_owner_id", "current_location") ||
                binding.GetString("home_ship_id") != "ship_start" || binding.GetString("lifeboat_ship_id") != "lifeboat" ||
                !(binding.Get("current_location") is string) || !Position(binding.Get("player_local_pose")) ||
                !(binding.Get("owner_revisions") is GdDict) || !(binding.Get("ship_references") is GdDict)) throw new Refusal("binding_mismatch");
            string location = world.GetString("current_location");
            if (location != binding.GetString("current_location") || location != active.GetString("current_location") ||
                !V.VariantEquals(active.Get("player_position"), binding.Get("player_local_pose")) || !V.VariantEquals(world.Get("player_position_in_ship"), binding.Get("player_local_pose"))) throw new Refusal("binding_mismatch");
            var owners = new HashSet<string>(StringComparer.Ordinal) { "ship_start", "lifeboat" };
            var used = new HashSet<string>(StringComparer.Ordinal);
            GdDict refs = binding.GetDictOrEmpty("ship_references");
            ValidateReference(refs.Get("ship_start") as GdDict, artifacts, used, home, null);
            foreach (var item in visited)
            {
                if (!(item.Key is string marker) || !Identity(marker) || !(item.Value is GdDict ship) || !StringFields(ship, "ship_id", "marker_id") || !Identity(ship.GetString("ship_id")) ||
                    ship.GetString("marker_id") != marker || !owners.Add(ship.GetString("ship_id")) || !(ship.Get("systems") is GdDict) || !(ship.Get("blueprint") is GdDict)) throw new Refusal("binding_mismatch");
                GdDict s = (GdDict)item.Value;
                ValidateReference(refs.Get(s.GetString("ship_id")) as GdDict, artifacts, used, null, s.GetDictOrEmpty("blueprint"));
                if (s.Has("mobility") && !ValidMobility(s.Get("mobility") as GdDict, s.GetString("ship_id"))) throw new Refusal("invalid_payload");
            }
            string owner = location.Length == 0 ? "ship_start" : visited.GetDictOrEmpty(location).GetString("ship_id");
            if (!Identity(owner) || owner != binding.GetString("current_owner_id")) throw new Refusal("binding_mismatch");
            ValidateReference(refs.Get(owner) as GdDict, artifacts, used, active, location.Length == 0 ? null : visited.GetDictOrEmpty(location).GetDictOrEmpty("blueprint"));
            if (location.Length == 0 && !V.VariantEquals(home.Get("player_position"), active.Get("player_position"))) throw new Refusal("binding_mismatch");
            GdDict lifeRef = refs.Get("lifeboat") as GdDict;
            if (world.Has("mobile_home_state"))
            {
                if (!(world.Get("mobile_home_state") is GdDict mobile) || !JsonInteger(mobile.Get("version"), out long mobileVersion) || mobileVersion != 1 ||
                    !(mobile.Get("lifeboat_commissioned") is bool) || !(mobile.Get("lifeboat") is GdDict)) throw new Refusal("invalid_payload");
                GdDict mobileState = world.GetDictOrEmpty("mobile_home_state"), lifeboat = mobileState.GetDictOrEmpty("lifeboat");
                if (!StringFields(lifeboat, "ship_id", "marker_id") || !(lifeboat.Get("blueprint") is GdDict) || !(lifeboat.Get("systems") is GdDict)) throw new Refusal("binding_mismatch");
                if (lifeboat.GetString("ship_id") != "lifeboat" || !ValidMobility(mobileState.Get("home_mobility") as GdDict, "ship_start") ||
                    !ValidMobility(lifeboat.Get("mobility") as GdDict, "lifeboat")) throw new Refusal("invalid_payload");
                if (mobileState.Has("active_scene_position") && !Position(mobileState.Get("active_scene_position"))) throw new Refusal("invalid_payload");
                if (mobileState.Has("home_location"))
                {
                    GdDict homeLocation = mobileState.Get("home_location") as GdDict;
                    if (homeLocation == null || !JsonInteger(homeLocation.Get("version"), out long version) || version != 1 || !Identity(homeLocation.GetString("marker_id")) || !Position(homeLocation.Get("sea_position"))) throw new Refusal("invalid_payload");
                }
                ValidateReference(lifeRef, artifacts, used, null, lifeboat.Get("blueprint") as GdDict);
            }
            else if (lifeRef == null || lifeRef.Count != 1 || !(lifeRef.Get("present") is bool present) || present) throw new Refusal("binding_mismatch");
            GdDict revisions = binding.GetDictOrEmpty("owner_revisions");
            if (revisions.Count != owners.Count || refs.Count != owners.Count || owners.Any(id => !(revisions.Get(id) is long r) || r < 0 || !refs.Has(id)) ||
                used.Count != artifacts.Count) throw new Refusal("binding_mismatch");
            var snapshot = new WorldSnapshot { VisitedShips = visited, DockEdges = world.GetArrayOrEmpty("dock_edges"), PilotedShipId = world.GetString("piloted_ship_id"), AboardShipId = world.GetString("aboard_ship_id") };
            if (snapshot.PilotedShipId.Length > 0 && !owners.Contains(snapshot.PilotedShipId) || snapshot.AboardShipId.Length > 0 && !owners.Contains(snapshot.AboardShipId) ||
                !WorldSnapshotAssembler.ValidateConnectionSnapshot(snapshot, "ship_start", "lifeboat", out _, out _)) throw new Refusal("invalid_payload");
            candidate.Entries.Insert(0, new Entry("world", "", "world_snapshot", "world-4", request.GetString("world_text")));
            candidate.Entries.Insert(0, new Entry("run", "", "run_snapshot", "gate2-current-run-6", request.GetString("run_text")));
            int ordinal = 0;
            foreach (Entry entry in candidate.Entries.Where(e => e.Role == "artifact").OrderBy(e => e.Logical, StringComparer.Ordinal)) entry.File = ArtifactFile(ordinal++);
            return candidate;
        }

        void ValidateReference(GdDict reference, Dictionary<string, Artifact> artifacts, HashSet<string> used, GdDict run, GdDict blueprint)
        {
            if (reference == null || reference.Count != 5 || !StringFields(reference, "layout_path", "gameplay_slice_path", "kit_path", "profile_id") ||
                !(reference.Get("present") is bool present) || !present) throw new Refusal("binding_mismatch");
            string layoutPath = reference.GetString("layout_path"), slicePath = reference.GetString("gameplay_slice_path"), kitPath = reference.GetString("kit_path"), profile = reference.GetString("profile_id");
            if (!artifacts.TryGetValue(layoutPath, out Artifact layout) || !artifacts.TryGetValue(slicePath, out Artifact slice) || !artifacts.TryGetValue(kitPath, out Artifact kit)) throw new Refusal("reference_missing");
            if (layout.Kind != "ship_layout" || slice.Kind != "ship_gameplay_slice" || kit.Kind != "ship_structural_catalog" ||
                layout.Document.GetString("kit_id") != kit.Document.GetString("kit_id")) throw new Refusal("invalid_reference");
            if (!KitJoin(layout.Document, kit.Document)) throw new Refusal("invalid_reference");
            if (run != null && (run.GetString("layout_path") != layoutPath || run.GetString("gameplay_slice_path") != slicePath || run.GetString("kit_path") != kitPath)) throw new Refusal("binding_mismatch");
            var rooms = new HashSet<string>(layout.Document.GetArrayOrEmpty("rooms").OfType<GdDict>().Select(r => r.GetString("id")), StringComparer.Ordinal);
            if (!rooms.Contains(slice.Document.GetString("start_room")) || !rooms.Contains(slice.Document.GetString("goal_room"))) throw new Refusal("invalid_reference");
            if (layout.Document.GetString("generation_profile") != profile || profile.Length > 0 &&
                (!ConstrainedExpedition.Supported(profile) || _compatibility.GetDictOrEmpty("profiles").GetString(profile) != profile)) throw new Refusal("incompatible_content");
            if (blueprint != null)
            {
                if (blueprint.Has("generation_profile") && !(blueprint.Get("generation_profile") is string) ||
                    !JsonInteger(blueprint.Get("size"), out long size) || size < 0 || size > 2 || !JsonInteger(blueprint.Get("condition"), out long condition) || condition < 0 || condition > 2 ||
                    !JsonInteger(blueprint.Get("seed_value"), out long seed, true) || !(blueprint.Get("room_count_range") is GdDict range) ||
                    !JsonInteger(range.Get("min"), out long min) || !JsonInteger(range.Get("max"), out long max) || min <= 0 || max < min ||
                    blueprint.GetString("generation_profile") != profile || !JsonInteger(layout.Document.Get("generation_seed"), out long layoutSeed, true) || layoutSeed != seed) throw new Refusal("binding_mismatch");
            }
            used.Add(layoutPath); used.Add(slicePath); used.Add(kitPath);
        }

        bool ValidRun(GdDict run)
            => run != null && Shape(run, new RunSnapshot().ToDict()) && run.GetString("slice_version") == "gate2-current-run-6" &&
                run.GetString("godot_version") == _compatibility.GetString("engine_version") && Position(run.Get("player_position")) &&
                Number(run.GetDictOrEmpty("vitals_summary").Get("health"), out double health) && health > 0 &&
                Number(run.Get("play_time_seconds"), out double time) && time >= 0;

        static bool ValidMobility(GdDict mobility, string owner)
            => mobility != null && JsonInteger(mobility.Get("version"), out long version) && version == 1 &&
                mobility.Get("engine_id") is string && Number(mobility.Get("area_m2"), out _) && Number(mobility.Get("dry_mass_kg"), out _) &&
                Number(mobility.Get("rated_supported_kg"), out _) && AssemblyMobility.ValidSpecification(mobility) && WorldSnapshotAssembler.OwnedInstallation(owner, mobility);

        static bool ValidLayout(GdDict layout)
        {
            if (!Number(layout.Get("cell_size"), out double cell) || cell <= 0 || !(layout.Get("rooms") is GdArray rooms) || rooms.Count == 0 || !(layout.Get("portals") is GdArray)) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in rooms) if (!(value is GdDict room) || !Nonempty(room.Get("id")) || !ids.Add(room.GetString("id"))) return false;
            return true;
        }

        static bool ValidKit(GdDict kit)
        {
            if (!StringFields(kit, "kit_id", "default_role_module") || !Identity(kit.GetString("kit_id")) || !Number(kit.Get("grid_step_m"), out double grid) || grid <= 0 ||
                !(kit.Get("modules") is GdArray modules) || modules.Count == 0 || !JsonInteger(kit.Get("module_count"), out long count) || count != modules.Count ||
                !(kit.Get("role_modules") is GdDict roles) || roles.IsEmpty || !(kit.Get("biome_preference") is GdDict preferences)) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in modules)
            {
                string id = value is string name ? name : value is GdDict module && module.Get("module_id") is string moduleId ? moduleId : "";
                if (!Identity(id) || !ids.Add(id)) return false;
            }
            if (!ids.Contains(kit.GetString("default_role_module"))) return false;
            foreach (var role in roles)
            {
                if (!(role.Key is string key) || !Identity(key) || !(role.Value is GdArray values) || values.Count == 0) return false;
                foreach (object value in (GdArray)role.Value) if (!(value is string id) || !ids.Contains(id)) return false;
            }
            foreach (object value in preferences.Values) if (!Number(value, out _)) return false;
            return true;
        }

        static bool KitJoin(GdDict layout, GdDict kit)
        {
            if (!StringFields(layout, "kit_id") || !Number(layout.Get("cell_size"), out double cell) || !Number(kit.Get("grid_step_m"), out double grid) || cell != grid) return false;
            var modules = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in kit.GetArrayOrEmpty("modules")) modules.Add(value is string kitModuleId ? kitModuleId : ((GdDict)value).GetString("module_id"));
            foreach (GdDict room in layout.GetArrayOrEmpty("rooms"))
            {
                if (!room.Has("structural_placements")) continue;
                if (!(room.Get("structural_placements") is GdArray placements)) return false;
                foreach (object value in placements)
                {
                    if (!(value is GdDict placement)) return false;
                    if (!(placement.Get("module") is string) && !(placement.Get("module_id") is string)) return false;
                    string placementId = placement.Get("module") is string module ? module : placement.GetString("module_id");
                    if (!modules.Contains(placementId) || placement.Has("module") && !(placement.Get("module") is string) ||
                        placement.Has("module_id") && (!(placement.Get("module_id") is string) || placement.GetString("module_id") != placementId)) return false;
                }
            }
            return true;
        }

        GdDict RequireLive(string run, string slot)
        {
            if (_authority == null) throw new Refusal("terminal_unbound");
            GdDict status;
            try { status = _authority.Query(run, slot); }
            catch (Exception) { throw new Refusal("terminal_authority_error"); }
            if (status == null || !SafeGraph(status) || !(status.Get("ok") is bool)) throw new Refusal("terminal_ambiguous");
            status = status.DeepCopy();
            if (!status.GetBool("ok")) throw new Refusal("terminal_authority_error");
            if (!StringFields(status, "run_id", "slot_id", "status") || status.GetString("run_id") != run || status.GetString("slot_id") != slot || !(status.Get("legacy_witnesses") is GdArray)) throw new Refusal("terminal_ambiguous");
            string state = status.GetString("status");
            if (state == "terminal" || state == "frozen") throw new Refusal("run_terminal");
            if (state != "live") throw new Refusal("terminal_ambiguous");
            if (ReadTerminal(run) != null) throw new Refusal("run_terminal");
            if (_storage.FileExists(Death(slot))) throw new Refusal("legacy_death");
            foreach (object value in status.GetArrayOrEmpty("legacy_witnesses"))
            {
                if (!(value is GdDict witness) || !StringFields(witness, "run_id", "slot_id", "path") || witness.GetString("run_id") != run || !KnownSlot(witness.GetString("slot_id"))) throw new Refusal("terminal_ambiguous");
                GdDict w = (GdDict)value;
                string ownerSlot = w.GetString("slot_id"), path = w.GetString("path");
                if (path != Death(ownerSlot) && path != LegacyPayload(ownerSlot) && path != LegacyPayload(ownerSlot) + ".tmp" && path != "user://saves/index.json") throw new Refusal("terminal_ambiguous");
                if (!_storage.FileExists(path)) throw new Refusal("legacy_ownership_ambiguous");
                if (path == Death(ownerSlot)) throw new Refusal("legacy_death");
                if (w.Get("frozen") is bool frozen && frozen) throw new Refusal("run_terminal");
                if (path != "user://saves/index.json")
                {
                    GdDict payload = ParseObject(_storage.ReadText(path));
                    if (payload == null || payload.GetString("run_id") != run) throw new Refusal("legacy_ownership_ambiguous");
                }
            }
            var indexedOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_storage.FileExists("user://saves/index.json"))
            {
                GdDict index = ParseObject(_storage.ReadText("user://saves/index.json"));
                if (index == null || index.GetString("version") != "save-index-1" || !(index.Get("slots") is GdArray)) throw new Refusal("legacy_ownership_ambiguous");
                var indexed = new HashSet<string>(StringComparer.Ordinal);
                foreach (object value in index.GetArrayOrEmpty("slots"))
                {
                    if (!(value is GdDict row) || !KnownSlot(row.GetString("slot_id")) || !indexed.Add(row.GetString("slot_id")) ||
                        !(row.Get("run_id") is string) || !(row.Get("frozen") is bool)) throw new Refusal("legacy_ownership_ambiguous");
                    GdDict r = (GdDict)value;
                    if (Identity(r.GetString("run_id"))) indexedOwners[r.GetString("slot_id")] = r.GetString("run_id");
                    else if (r.GetBool("frozen")) throw new Refusal("legacy_ownership_ambiguous");
                    if (r.GetString("run_id") != run) continue;
                    if (r.GetBool("frozen")) throw new Refusal("run_terminal");
                    if (_storage.FileExists(Death(r.GetString("slot_id")))) throw new Refusal("legacy_death");
                }
            }
            foreach (string legacySlot in Slots)
            {
                bool death = _storage.FileExists(Death(legacySlot));
                var owners = new HashSet<string>(StringComparer.Ordinal);
                if (indexedOwners.TryGetValue(legacySlot, out string indexedOwner)) owners.Add(indexedOwner);
                foreach (string path in new[] { LegacyPayload(legacySlot), LegacyPayload(legacySlot) + ".tmp" })
                {
                    if (!_storage.FileExists(path)) continue;
                    GdDict payload = ParseObject(_storage.ReadText(path));
                    if (payload == null || !StringFields(payload, "run_id") || !Identity(payload.GetString("run_id")))
                    { if (death) throw new Refusal("legacy_ownership_ambiguous"); continue; }
                    owners.Add(payload.GetString("run_id"));
                    if (death && payload.GetString("run_id") == run) throw new Refusal("legacy_death");
                }
                if (death && _storage.FileExists(Active(legacySlot)))
                {
                    string active = _storage.ReadText(Active(legacySlot));
                    GdDict pointer = TryPointer(active, legacySlot);
                    if (pointer == null || TrySelected(active, pointer.GetString("run_id"), legacySlot) == null) throw new Refusal("legacy_ownership_ambiguous");
                    owners.Add(pointer.GetString("run_id"));
                }
                if (death && owners.Count != 1) throw new Refusal("legacy_ownership_ambiguous");
                if (death && owners.Contains(run)) throw new Refusal("legacy_death");
            }
            return status.DeepCopy();
        }

        GdDict ReadTerminal(string run)
        {
            string path = Tombstone(run);
            CheckPhysicalPath(path);
            try
            {
                if (!_storage.FileExists(path)) return null;
                GdDict terminal = ParseObject(_storage.ReadText(path));
                if (terminal == null || !ValidTerminal(terminal, run)) throw new Refusal("terminal_ambiguous");
                return terminal;
            }
            catch (Refusal) { throw; }
            catch (Exception) { throw new Refusal("terminal_authority_error"); }
        }

        static bool ValidTerminal(GdDict terminal, string run)
        {
            if (terminal.Count != 8 || !StringFields(terminal, "schema_version", "run_id", "state", "reason", "cause", "epitaph") ||
                terminal.GetString("schema_version") != TerminalVersion || terminal.GetString("run_id") != run ||
                terminal.GetString("state") != "terminal" && terminal.GetString("state") != "frozen" || !Nonempty(terminal.Get("reason")) ||
                !(terminal.Get("cause") is string) || !(terminal.Get("epitaph") is string) || !WireLong(terminal.Get("terminal_revision"), out _) ||
                !(terminal.Get("legacy_witnesses") is GdArray)) return false;
            foreach (object value in terminal.GetArrayOrEmpty("legacy_witnesses"))
            {
                if (!(value is GdDict witness) || !StringFields(witness, "run_id", "slot_id", "path") || witness.GetString("run_id") != run || !KnownSlot(witness.GetString("slot_id"))) return false;
                string slot = witness.GetString("slot_id"), path = witness.GetString("path");
                if (path != Death(slot) && path != LegacyPayload(slot) && path != LegacyPayload(slot) + ".tmp" && path != "user://saves/index.json") return false;
            }
            return true;
        }

        static GdDict DecodeTerminal(GdDict wire)
        {
            GdDict result = wire.DeepCopy();
            WireLong(result.Get("terminal_revision"), out long revision);
            result["terminal_revision"] = revision;
            return result;
        }

        void Guard(string run, string slot)
        {
            if (!_validRoot) throw new Refusal("invalid_root");
            if (!Identity(run) || slot != null && !KnownSlot(slot)) throw new Refusal("invalid_identity");
        }

        void WriteImmutable(string path, string text)
        {
            CheckPhysicalPath(path);
            if (_storage.FileExists(path)) { if (_storage.ReadText(path) != text) throw new Refusal("generation_conflict"); return; }
            _storage.WriteText(path, text);
        }

        void CheckCandidatePaths(Candidate candidate)
        {
            CheckOperationPaths(candidate.Run, candidate.Slot);
            string directory = Generation(candidate.Run, candidate.Slot, candidate.Id);
            CheckPhysicalPath(directory + "/commit.json");
            foreach (Entry entry in candidate.Entries) CheckPhysicalPath(directory + "/" + entry.File);
        }

        void CheckOperationPaths(string run, string slot)
        {
            CheckPhysicalPath(Active(slot));
            CheckPhysicalPath(Previous(slot));
            CheckPhysicalPath(Tombstone(run));
            CheckPhysicalPath(_root + "/index.json");
        }

        void CheckPhysicalPath(string logical)
        {
            string full = _storage.Globalize(logical);
            if (string.IsNullOrEmpty(full)) throw new Refusal("path_budget_exceeded", "Globalize did not provide an owned physical path.");
            bool drive = full.Length >= 3 && (full[0] >= 'A' && full[0] <= 'Z' || full[0] >= 'a' && full[0] <= 'z') &&
                full[1] == ':' && (full[2] == '\\' || full[2] == '/');
            bool unc = full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal);
            // Virtual storage diagnostics (such as memory://) are not Win32 filesystem paths.
            if (!drive && !unc) return;
            string windows = full.Replace('/', '\\');
            int separator = windows.LastIndexOf('\\');
            if (windows.Length + 4 > 259 || windows.Length + 12 > 259 || separator > 247 ||
                windows.Split('\\').Any(component => component.Length > 255))
                throw new Refusal("path_budget_exceeded", "Owned Windows path exceeds final247/tmp251/backup259/directory247 publication budget: " + logical + " (physical characters=" + Decimal(windows.Length) + ").");
        }

        void VerifyText(string path, string text)
        {
            if (!_storage.FileExists(path) || _storage.ReadText(path) != text) throw new Refusal("corrupt_generation");
        }

        void Hook(string stage) => _fault?.Invoke(stage);
        string Generations(string run, string slot) => _root + "/g";
        string Generation(string run, string slot, string id) => Generations(run, slot) + "/" + TupleToken(run, slot, id);
        string Active(string slot) => _root + "/s/" + Hash(slot) + "/active.json";
        string Previous(string slot) => _root + "/s/" + Hash(slot) + "/previous.json";
        string Tombstone(string run) => _root + "/r/" + Hash(run) + "/terminal.json";
        static string Frame(string value) => Decimal(Utf8.GetByteCount(value)) + ":" + value;
        static string TupleToken(string run, string slot, string id) => Hash("save-generation-path-1:" + Frame(run) + Frame(slot) + Frame(id));
        static string ArtifactFile(int ordinal) => ordinal.ToString("x8", CultureInfo.InvariantCulture) + ".json";
        static string Death(string slot) => "user://saves/" + slot + ".death.json";
        static string LegacyPayload(string slot) => slot == "autosave_active" ? "user://saves/current_run.json" : "user://saves/" + slot + ".json";
        static bool KnownSlot(string slot) => Slots.Contains(slot, StringComparer.Ordinal);
        static string Kind(string slot) => slot == "world" ? "world" : slot == "quicksave" ? "quick" : slot.StartsWith("autosave_", StringComparison.Ordinal) ? "auto" : "manual";
        static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Encode(GdDict record) => GdJson.Stringify(record, "  ") + "\n";
        static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Utf8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        static bool ValidRoot(string root)
        {
            const string prefix = "user://saves/.generations";
            return root != null && (root == prefix || root.StartsWith(prefix + "/", StringComparison.Ordinal)) && LogicalPath(root);
        }
        static bool LogicalPath(string path)
        {
            if (path == null) return false;
            string relative = path.StartsWith("user://", StringComparison.Ordinal) ? path.Substring(7) : path.StartsWith("res://", StringComparison.Ordinal) ? path.Substring(6) : path;
            if (relative.Length == 0 || relative.IndexOfAny(new[] { '\\', ':', '<', '>', '"', '|', '?', '*' }) >= 0) return false;
            foreach (string segment in relative.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) || !Text(segment)) return false;
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9') return false;
            }
            return true;
        }
        static bool Identity(string value) => value != null && value.Length > 0 && value.Length <= 256 && Text(value);
        static bool Nonempty(object value) => value is string text && text.Length > 0;
        static bool StringFields(GdDict record, params string[] fields) => record != null && fields.All(field => record.Get(field) is string);
        static bool Text(string value)
        {
            if (value == null || value.Any(c => c < 32 || c == 127)) return false;
            try { Utf8.GetByteCount(value); return true; } catch (EncoderFallbackException) { return false; }
        }
        static bool Hex(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        static bool WireLong(object value, out long number)
        {
            number = 0;
            return value is string text && text.Length > 0 && text.Length <= 19 && (text == "0" || text[0] >= '1' && text[0] <= '9') &&
                text.All(c => c >= '0' && c <= '9') && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }
        static bool Number(object value, out double number)
        {
            number = value is long integer ? integer : value is double floating ? floating : double.NaN;
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        static bool JsonInteger(object value, out long number, bool seedString = false)
        {
            number = 0;
            if (seedString && value is string text)
                return text.Length > 0 && text.Length <= 20 && text != "-0" && (text[0] != '-' ? text == "0" || text[0] >= '1' && text[0] <= '9' : text.Length > 1 && text[1] >= '1' && text[1] <= '9') &&
                    text.Skip(text[0] == '-' ? 1 : 0).All(c => c >= '0' && c <= '9') && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
            if (!Number(value, out double n) || Math.Abs(n) > 9007199254740991d || Math.Truncate(n) != n) return false;
            number = (long)n; return true;
        }
        static bool Position(object value) => value is GdArray array && array.Count == 3 && array.All(v => Number(v, out _));
        static bool Shape(GdDict actual, GdDict template)
        {
            foreach (var field in template)
            {
                if (!actual.Has(field.Key)) return false;
                object expected = field.Value, value = actual[field.Key];
                if (expected is GdDict && !(value is GdDict) || expected is GdArray && !(value is GdArray) || expected is string && !(value is string) ||
                    expected is bool && !(value is bool) || expected is double && !Number(value, out _) || expected is long && !JsonInteger(value, out _, field.Key.Equals("world_seed"))) return false;
            }
            return true;
        }
        static bool ValidCompatibility(GdDict compatibility)
        {
            if (compatibility == null || compatibility.Count != 6 || !StringFields(compatibility, "engine_version", "catalog_id", "catalog_version", "library_id", "library_version") ||
                !Identity(compatibility.GetString("engine_version")) || !Identity(compatibility.GetString("catalog_id")) ||
                !Identity(compatibility.GetString("catalog_version")) || !(compatibility.Get("library_id") is string) || !(compatibility.Get("library_version") is string) || !(compatibility.Get("profiles") is GdDict)) return false;
            foreach (var profile in compatibility.GetDictOrEmpty("profiles")) if (!(profile.Key is string key) || !ConstrainedExpedition.Supported(key) || !(profile.Value is string version) || version != key) return false;
            return true;
        }
        static bool OwnerRevisionsAdvance(GdDict child, GdDict parent)
        {
            GdDict newer = child.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions"), older = parent.GetDictOrEmpty("binding").GetDictOrEmpty("owner_revisions");
            foreach (var revision in older) if (newer.Get(revision.Key) is long value && value < (long)revision.Value) return false;
            return true;
        }
        static bool Equivalent(GdDict left, GdDict right) => V.VariantEquals(left, right);

        static bool SafeGraph(object value) => SafeGraph(value, new HashSet<object>(), 0);
        static bool SafeGraph(object value, HashSet<object> ancestors, int depth)
        {
            if (depth > 128) return false;
            if (value is GdDict dictionary)
            {
                if (!ancestors.Add(dictionary)) return false;
                foreach (var item in dictionary) if (!(item.Key is string key) || !Text(key) || !SafeGraph(item.Value, ancestors, depth + 1)) { ancestors.Remove(dictionary); return false; }
                ancestors.Remove(dictionary); return true;
            }
            if (value is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                foreach (object item in array) if (!SafeGraph(item, ancestors, depth + 1)) { ancestors.Remove(array); return false; }
                ancestors.Remove(array); return true;
            }
            if (value is string text) { try { Utf8.GetByteCount(text); return true; } catch (EncoderFallbackException) { return false; } }
            return value == null || value is bool || value is long || value is double n && !double.IsNaN(n) && !double.IsInfinity(n);
        }

        static GdDict ParseObject(string text)
        {
            if (text == null) return null;
            try
            {
                Utf8.GetByteCount(text);
                if (!new JsonGuard(text).Valid()) return null;
                GdDict dictionary = GdJson.ParseString(text) as GdDict;
                return SafeGraph(dictionary) ? dictionary : null;
            }
            catch (Exception) { return null; }
        }

        // Local strict JSON syntax/duplicate-key/depth guard. The existing permissive gameplay adapters are
        // never invoked, and the supplied text itself is retained rather than normalized by this scanner.
        sealed class JsonGuard
        {
            readonly string _text; int _position;
            public JsonGuard(string text) { _text = text; }
            public bool Valid() { try { Value(0); White(); return _position == _text.Length; } catch (FormatException) { return false; } }
            void White() { while (_position < _text.Length && (_text[_position] == ' ' || _text[_position] == '\t' || _text[_position] == '\r' || _text[_position] == '\n')) _position++; }
            char Next { get { White(); return _position < _text.Length ? _text[_position] : '\0'; } }
            void Take(char token) { if (Next != token) throw new FormatException(); _position++; }
            void Value(int depth)
            {
                if (depth > 128) throw new FormatException();
                char next = Next;
                if (next == '{')
                {
                    _position++; var keys = new HashSet<string>(StringComparer.Ordinal);
                    if (Next == '}') { _position++; return; }
                    while (true) { string key = String(); if (!keys.Add(key)) throw new FormatException(); Take(':'); Value(depth + 1); if (Next == '}') { _position++; return; } Take(','); }
                }
                if (next == '[')
                {
                    _position++; if (Next == ']') { _position++; return; }
                    while (true) { Value(depth + 1); if (Next == ']') { _position++; return; } Take(','); }
                }
                if (next == '"') { String(); return; }
                if (next == 't') { Literal("true"); return; }
                if (next == 'f') { Literal("false"); return; }
                if (next == 'n') { Literal("null"); return; }
                int start = _position;
                if (next == '-') _position++;
                if (_position >= _text.Length) throw new FormatException();
                if (_text[_position] == '0') _position++;
                else { if (_text[_position] < '1' || _text[_position] > '9') throw new FormatException(); Digits(); }
                if (_position < _text.Length && _text[_position] == '.') { _position++; int fraction = _position; Digits(); if (_position == fraction) throw new FormatException(); }
                if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
                { _position++; if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-')) _position++; int exponent = _position; Digits(); if (_position == exponent) throw new FormatException(); }
                if (start == _position) throw new FormatException();
            }
            void Digits() { while (_position < _text.Length && _text[_position] >= '0' && _text[_position] <= '9') _position++; }
            void Literal(string word) { if (_position + word.Length > _text.Length || _text.Substring(_position, word.Length) != word) throw new FormatException(); _position += word.Length; }
            string String()
            {
                Take('"'); int begin = _position - 1;
                while (_position < _text.Length)
                {
                    char c = _text[_position++];
                    if (c == '"')
                    {
                        string parsed = GdJson.ParseString(_text.Substring(begin, _position - begin)) as string;
                        if (parsed == null) throw new FormatException();
                        Utf8.GetByteCount(parsed); return parsed;
                    }
                    if (c < 32) throw new FormatException();
                    if (c != '\\') continue;
                    if (_position >= _text.Length) throw new FormatException();
                    char escape = _text[_position++];
                    if (escape == 'u')
                    {
                        for (int i = 0; i < 4; i++) { if (_position >= _text.Length || !Uri.IsHexDigit(_text[_position++])) throw new FormatException(); }
                    }
                    else if ("\"\\/bfnrt".IndexOf(escape) < 0) throw new FormatException();
                }
                throw new FormatException();
            }
        }

        sealed class Artifact
        {
            public readonly string Kind; public readonly GdDict Document;
            public Artifact(string kind, GdDict document) { Kind = kind; Document = document; }
        }
        sealed class Entry
        {
            public readonly string Role, Logical, Kind, Version, Text;
            public string File;
            public string Key => Role == "artifact" ? Logical : Role;
            public Entry(string role, string logical, string kind, string version, string text)
            { Role = role; Logical = logical; Kind = kind; Version = version; Text = text; File = role == "artifact" ? "" : role + ".json"; }
        }
        sealed class Candidate
        {
            public readonly GdDict Request; public readonly List<Entry> Entries = new List<Entry>();
            public readonly string Run, Slot, Id, Parent; public readonly long Revision;
            public GdDict Manifest; public string ManifestText, ParentPointer;
            public Candidate(GdDict request) { Request = request; Run = request.GetString("run_id"); Slot = request.GetString("slot_id"); Id = request.GetString("generation_id"); Parent = request.GetString("parent_generation_id"); Revision = (long)request.Get("domain_revision"); }
        }
        sealed class Refusal : Exception { public readonly string Reason, Diagnostic; public Refusal(string reason, string diagnostic = null) { Reason = reason; Diagnostic = diagnostic; } }
        static bool TerminalRefusal(string reason) => reason == "run_terminal" || reason == "terminal_unbound" || reason == "terminal_authority_error" ||
            reason == "terminal_ambiguous" || reason == "legacy_death" || reason == "legacy_ownership_ambiguous";
        static string Description(Exception error) => error is Refusal refusal && refusal.Diagnostic != null ? refusal.Diagnostic : error.GetType().FullName;
        static GdDict Failure(Exception error, string run, string slot)
        { GdDict result = Result(false, error is Refusal refusal ? refusal.Reason : "storage_failure", run, slot); result["detail"] = Description(error); return result; }
        static GdDict Unknown(string run, string slot, string id, Exception error)
        { GdDict result = Result(false, "publication_unknown", run, slot); result["outcome"] = "unknown"; result["generation_id"] = id; result["detail"] = Description(error); return result; }
        static GdDict Success(Candidate candidate, string reason, bool recovered = false)
        {
            GdDict result = Result(true, reason, candidate.Run, candidate.Slot);
            result["committed"] = true; result["outcome"] = "committed"; result["generation_id"] = candidate.Id;
            result["payloads"] = candidate.Request.DeepCopy(); result["recovered"] = recovered;
            return result;
        }
        static GdDict TerminalSuccess(string run, string reason)
        { GdDict result = Result(true, reason, run, ""); result["committed"] = true; result["outcome"] = "committed"; return result; }
        static GdDict Result(bool ok, string reason, string runId, string slotId) => new GdDict
        {
            { "ok", ok }, { "committed", false }, { "outcome", "not_committed" },
            { "reason", reason }, { "run_id", runId ?? "" },
            { "slot_id", slotId ?? "" }, { "generation_id", "" },
            { "index_reconciliation_needed", false }, { "recovered", false },
            { "payloads", null }, { "candidates", new GdArray() }
        };
    }
}
