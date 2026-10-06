using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed partial class SaveCommitCoordinator
    {
        string Deleted(string slot) => _root + "/s/" + Hash(slot) + "/deleted.json";
        string TerminalIntent(string run) => _root + "/r/" + Hash(run) + "/terminal-intent.json";
        internal GdDict ReadCommitParent(string run, string slot)
        {
            lock (_gate)
            {
                try
                {
                    Guard(run, slot); if (!CompleteGenerationEnabled) throw new Refusal("invalid_identity"); RequireLive(run, slot);
                    if (!_storage.FileExists(Active(slot))) return Result(false, "not_found", run, slot);
                    string text = _storage.ReadText(Active(slot)); GdDict header = TryPointer(text, slot);
                    if (header == null) throw new Refusal("corrupt_generation");
                    if (header.GetString("run_id") != run) throw new Refusal("slot_owner_conflict");
                    Candidate selected = TrySelected(text, run, slot); if (selected == null) throw new Refusal("corrupt_generation"); RequireLive(run, slot);
                    return new GdDict { { "ok", true }, { "parent_generation_id", selected.Id }, { "expected_pointer_sha256", Hash(text) }, { "domain_revision", selected.Revision } };
                }
                catch (Exception e) { return Failure(e, run, slot); }
            }
        }
        GdDict ReconcileDeletedVisibility(Candidate selected)
        {
            if (!CompleteGenerationEnabled || !_storage.FileExists(Deleted(selected.Slot))) return null;
            try { CheckPhysicalPath(Deleted(selected.Slot)); _storage.Delete(Deleted(selected.Slot)); if (!_storage.FileExists(Deleted(selected.Slot))) return null; }
            catch (Exception) { }
            GdDict result = Success(selected, "slot_deletion_reconciliation_needed"); result["ok"] = false; result["payloads"] = null; result["visibility_reconciliation_needed"] = true; return result;
        }
        internal GdDict RecordTerminalIntent(GdDict terminal, string run)
        {
            lock (_gate)
            {
                try
                {
                    Guard(run, null); if (!CompleteGenerationEnabled || !SafeGraph(terminal)) throw new Refusal("invalid_request");
                    string path = TerminalIntent(run); CheckPhysicalPath(path); GdDict wire;
                    if (_storage.FileExists(path)) wire = ParseMetadata(_storage.ReadText(path));
                    else
                    {
                        wire = terminal.DeepCopy(); if (!(wire.Get("terminal_revision") is long revision) || revision < 0) throw new Refusal("invalid_request");
                        wire["terminal_revision"] = Decimal(revision); if (!ValidTerminal(wire, run)) throw new Refusal("invalid_request");
                        string text = Encode(wire); _storage.WriteText(path, text); VerifyText(path, text);
                    }
                    if (wire == null || !ValidTerminal(wire, run)) throw new Refusal("terminal_ambiguous");
                    return new GdDict { { "ok", true }, { "reason", "terminal_intent" }, { "terminal", DecodeTerminal(wire) } };
                }
                catch (Exception e) { return Failure(e, run, ""); }
            }
        }
        public GdDict ValidateSuppliedPayload(GdDict payloads, string runId, string slotId)
        {
            lock (_gate)
            {
                try { Guard(runId, slotId); Candidate c = ValidateRequest(payloads, runId, slotId); CheckCandidatePaths(c); RequireLive(runId, slotId); return new GdDict { { "ok", true }, { "reason", "validated" } }; }
                catch (Exception e) { return Failure(e, runId, slotId); }
            }
        }

        public GdDict ReadSelected(string slotId)
        {
            lock (_gate)
            {
                string run = "";
                try
                {
                    if (!_validRoot || !KnownSlot(slotId)) throw new Refusal("invalid_identity");
                    CheckPhysicalPath(Active(slotId));
                    if (CompleteGenerationEnabled && _storage.FileExists(Deleted(slotId))) throw new Refusal("slot_deleted");
                    if (!_storage.FileExists(Active(slotId))) return Result(false, "not_found", "", slotId);
                    string pointer = _storage.ReadText(Active(slotId)); GdDict header = TryPointer(pointer, slotId);
                    if (header == null) throw new Refusal("corrupt_generation");
                    run = header.GetString("run_id"); RequireLive(run, slotId);
                    Candidate selected = TrySelected(pointer, run, slotId);
                    if (selected == null) throw new Refusal("corrupt_generation");
                    RequireLive(run, slotId); return Selection(selected, Hash(pointer));
                }
                catch (Exception e) { return Failure(e, run, slotId); }
            }
        }

        public GdDict ReadGeneration(string runId, string slotId, string generationId, string manifestSha256)
        {
            lock (_gate)
            {
                try
                {
                    Guard(runId, slotId);
                    if (CompleteGenerationEnabled && _storage.FileExists(Deleted(slotId))) throw new Refusal("slot_deleted");
                    if (!Identity(generationId) || !Hex(manifestSha256)) throw new Refusal("invalid_identity");
                    RequireLive(runId, slotId); Candidate selected = LoadGeneration(runId, slotId, generationId, manifestSha256);
                    RequireLive(runId, slotId); return Selection(selected, "");
                }
                catch (Exception e) { return Failure(e, runId, slotId); }
            }
        }

        GdDict Selection(Candidate candidate, string pointerHash)
        {
            GdDict result = Success(candidate, "selected");
            result["manifest_sha256"] = Hash(candidate.ManifestText); result["selected_pointer_sha256"] = pointerHash;
            result["payloads_sha256"] = SaveGenerationArtifacts.Hash(_allowPaidCrafting ? PaidSnapshotCodec.Stringify(candidate.Request) : GdJson.Stringify(candidate.Request));
            if (_allowPaidCrafting) result["save_mode"] = candidate.Request.GetDictOrEmpty("binding").GetString("save_mode");
            return result;
        }

        /// <summary>Deliberate diagnostic new-run reuse; old immutable generations and terminal authority survive.</summary>
        public GdDict CommitNewRun(GdDict payloads, string runId, string slotId)
        {
            lock (_gate)
            {
                if (!CompleteGenerationEnabled || _mutating || _explicitReclaim) return Result(false, "reclaim_not_admitted", runId, slotId);
                _explicitReclaim = true;
                try { return Commit(payloads, runId, slotId); }
                finally { _explicitReclaim = false; }
            }
        }

        public GdDict DeleteDiagnosticSlot(string slotId)
        {
            lock (_gate)
            {
                try
                {
                    if (!CompleteGenerationEnabled || !KnownSlot(slotId) || _mutating) throw new Refusal("invalid_identity");
                    string path = Deleted(slotId); CheckPhysicalPath(path);
                    string retained = _storage.FileExists(Active(slotId)) ? _storage.ReadText(Active(slotId)) : "";
                    GdDict pointer = retained.Length > 0 ? TryPointer(retained, slotId) : null;
                    if (retained.Length > 0 && pointer == null) throw new Refusal("corrupt_generation");
                    string text = Encode(new GdDict { { "schema_version", "component-slot-deletion-1" }, { "slot_id", slotId }, { "pointer_text", retained } });
                    _storage.WriteText(path, text); VerifyText(path, text);
                    // Keep the selected pointer as an immutable ownership witness; marker controls visibility/recovery.
                    return new GdDict { { "ok", true }, { "reason", "slot_deleted" } };
                }
                catch (Exception e) { return Failure(e, "", slotId); }
            }
        }

        public GdDict ReadSlotMetadata(string slotId)
        {
            lock (_gate)
            {
                try
                {
                    if (!CompleteGenerationEnabled || !KnownSlot(slotId) || _storage.FileExists(Deleted(slotId)) || !_storage.FileExists(Active(slotId))) return Result(false, "not_found", "", slotId);
                    string pointer = _storage.ReadText(Active(slotId)); GdDict header = TryPointer(pointer, slotId);
                    if (header == null) throw new Refusal("corrupt_generation");
                    Candidate candidate = TrySelected(pointer, header.GetString("run_id"), slotId);
                    if (candidate == null) throw new Refusal("corrupt_generation");
                    bool frozen = false;
                    try { RequireLive(candidate.Run, candidate.Slot); }
                    catch (Refusal r) { if (r.Reason != "run_terminal" && r.Reason != "legacy_death") throw; frozen = true; }
                    GdDict terminal = ReadTerminal(candidate.Run);
                    if (terminal == null && _storage.FileExists(TerminalIntent(candidate.Run))) terminal = ParseMetadata(_storage.ReadText(TerminalIntent(candidate.Run)));
                    return new GdDict { { "ok", true }, { "run_id", candidate.Run }, { "slot_id", candidate.Slot }, { "frozen", frozen },
                        { "epitaph", terminal?.GetString("epitaph") ?? "" }, { "run_snapshot", (_allowPaidCrafting ? PaidSnapshotCodec.Parse(candidate.Request.GetString("run_text"), PaidSnapshotCodec.SnapshotPolicy(_allowComponentIntegration, false)) : ParseObject(candidate.Request.GetString("run_text"))) } };
                }
                catch (Exception e) { return Failure(e, "", slotId); }
            }
        }

        Candidate ValidateIntegrationRequest(GdDict supplied, string run, string slot, bool paid = false)
        {
            if (supplied == null || !SafeGraph(supplied) || supplied.Count != 13) throw new Refusal("invalid_request");
            GdDict request = supplied.DeepCopy();
            GdDict binding = request.GetDictOrEmpty("binding");
            string mode = _allowComponentIntegration ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode;
            if (paid && binding.GetString("save_mode") != mode) throw new Refusal("binding_mismatch");
            GdDict active = paid ? PaidSnapshotCodec.Parse(request.GetString("run_text"), PaidSnapshotCodec.SnapshotPolicy(_allowComponentIntegration, false)) : ParseObject(request.GetString("run_text"));
            GdDict world = paid ? PaidSnapshotCodec.Parse(request.GetString("world_text"), PaidSnapshotCodec.SnapshotPolicy(_allowComponentIntegration, true)) : ParseObject(request.GetString("world_text"));
            string runVersion = _allowComponentIntegration ? RunSnapshot.ComponentIntegrationVersion : SaveLoadService.CURRENT_SLICE_VERSION;
            string worldVersion = _allowComponentIntegration ? WorldSnapshot.ComponentIntegrationVersion : WorldSnapshot.WorldSliceVersion;
            if (active == null || world == null || active.GetString("slice_version") != runVersion ||
                world.GetString("slice_version") != worldVersion) throw new Refusal("unsupported_schema");
            GdDict home = world.GetDictOrEmpty("home_ship");
            if (home.GetString("slice_version") != runVersion || binding.Count != (paid ? 11 : 10) ||
                binding.GetString("binding_version") != "component-generation-binding-1" ||
                !(request.Get("domain_revision") is long capture) || capture < 0) throw new Refusal("binding_mismatch");
            GdDict domain;
            if (paid)
            {
                GdDict envelope = active.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft");
                if (envelope.Count != 3 || !(envelope.Get("schema_version") is long version) || version != 1L ||
                    envelope.GetString("save_mode") != mode || !PaidSnapshotCodec.Same(envelope, home.GetDictOrEmpty("crafting_summary").Get("paid_craft")) ||
                    !ComponentDomainCodec.TryDecode(envelope.GetDictOrEmpty("domain"), out domain, out _) ||
                    !DomainBundle.TryCreate(domain, out _, out _) || (domain.GetInt("schema_version") != 3 && domain.GetInt("schema_version") != 4 && domain.GetInt("schema_version") != 5) ||
                    domain.GetString("domain_mode") != (_allowComponentIntegration ? "components_and_craft" : "craft_only") ||
                    !RunSession.ValidatePaidMirrors(domain.GetDictOrEmpty("participating_state"))) throw new Refusal("binding_mismatch");
                GdDict paidState = domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
                if (paidState.GetString("run_id") != run || paidState.GetString("actor_id") != RunSession.PLAYER_LOCAL_ID)
                    throw new Refusal("binding_mismatch");
                if (_allowComponentIntegration && (!PaidSnapshotCodec.Same(envelope.Get("domain"), active.Get("component_domain")) ||
                    !PaidSnapshotCodec.Same(envelope.Get("domain"), home.Get("component_domain")) ||
                    !PaidSnapshotCodec.Same(envelope.Get("domain"), world.Get("component_domain")))) throw new Refusal("binding_mismatch");
                if (!_allowComponentIntegration && (active.Has("component_domain") || home.Has("component_domain") || world.Has("component_domain") ||
                    active.Has("generation_id") || home.Has("generation_id") || world.Has("generation_id") ||
                    active.Has("capture_revision") || home.Has("capture_revision") || world.Has("capture_revision"))) throw new Refusal("binding_mismatch");
            }
            else
            {
                if (active.GetDictOrEmpty("crafting_summary").Has("paid_craft") || home.GetDictOrEmpty("crafting_summary").Has("paid_craft"))
                    throw new Refusal("binding_mismatch");
                if (!V.VariantEquals(active.Get("component_domain"), world.Get("component_domain")) || !V.VariantEquals(home.Get("component_domain"), world.Get("component_domain")) ||
                    !ComponentDomainCodec.TryDecode(world.GetDictOrEmpty("component_domain"), out domain, out _) ||
                    !DomainBundle.TryCreate(domain, out _, out _) || domain.GetInt("schema_version") != 2) throw new Refusal("binding_mismatch");
                if (SavePayloadAssembler.HasUnverifiedCraft(active.GetDictOrEmpty("crafting_summary")) || SavePayloadAssembler.HasUnverifiedCraft(domain)) throw new Refusal("craft_payment_unverified");
            }
            if (!(domain.Get("revision") is long commandRevision) || binding.GetString("component_revision") != Decimal(commandRevision)) throw new Refusal("binding_mismatch");
            if (_allowComponentIntegration && (active.GetString("capture_revision") != Decimal(capture) || world.GetString("capture_revision") != Decimal(capture) || home.GetString("capture_revision") != Decimal(capture) ||
                active.GetString("generation_id") != request.GetString("generation_id") || world.GetString("generation_id") != request.GetString("generation_id") || home.GetString("generation_id") != request.GetString("generation_id"))) throw new Refusal("binding_mismatch");
            foreach (object value in domain.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values)
                if (!(value is GdDict row) || row.GetString("condition_state") != "known") throw new Refusal("legacy_condition_unresolved");
            var references = binding.GetDictOrEmpty("ship_references");
            var registered = new HashSet<string>(domain.GetArrayOrEmpty("registered_owners").OfType<string>(), StringComparer.Ordinal);
            if (_allowComponentIntegration && !registered.SetEquals(references.Keys.OfType<string>()) || !references.Has(binding.GetString("player_pose_owner_id")) || world.GetString("aboard_ship_id") != binding.GetString("player_pose_owner_id")) throw new Refusal("binding_mismatch");
            foreach (GdDict holder in domain.GetDictOrEmpty("holders").Values.OfType<GdDict>())
                if (holder.GetString("kind") != "player" && !registered.Contains(holder.GetString("owner_id"))) throw new Refusal("binding_mismatch");
            foreach (GdDict machine in domain.GetDictOrEmpty("machinery").Values.OfType<GdDict>())
                if (!registered.Contains(machine.GetString("owner_id"))) throw new Refusal("binding_mismatch");
            ValidateComponentMirrors(domain, active, world, home, paid);
            var artifacts = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in request.GetArrayOrEmpty("artifacts"))
            {
                if (!(value is GdDict a) || a.Count != 4 || !StringFields(a, "logical_path", "document_kind", "schema_version", "text") || !LogicalPath(a.GetString("logical_path")) ||
                    !identities.Add(a.GetString("logical_path")) || ParseObject(a.GetString("text")) == null) throw new Refusal("invalid_reference");
                artifacts.Add(a.GetString("logical_path"), a);
            }
            var used = new HashSet<string>(StringComparer.Ordinal);
            GdDict legacy = request.DeepCopy(), oldActive = LegacyRun(active), oldWorld = world.DeepCopy();
            oldWorld["slice_version"] = WorldSnapshot.WorldSliceVersion; oldWorld["home_ship"] = LegacyRun(home);
            oldWorld.Erase("component_domain"); oldWorld.Erase("generation_id"); oldWorld.Erase("capture_revision");
            var oldRefs = new GdDict();
            foreach (var pair in references)
            {
                if (!(pair.Key is string owner) || !(pair.Value is GdDict reference) || reference.Count != 7 || !reference.GetBool("present")) throw new Refusal("invalid_reference");
                string kind = reference.GetString("reference_kind"), layoutPath = reference.GetString("layout_path"), slicePath = reference.GetString("gameplay_slice_path"), kitPath = reference.GetString("kit_path"), blueprintPath = reference.GetString("blueprint_path");
                if (!artifacts.TryGetValue(layoutPath, out GdDict la) || !artifacts.TryGetValue(kitPath, out GdDict ka)) throw new Refusal("reference_missing");
                GdDict layout = ParseObject(la.GetString("text")), kit = ParseObject(ka.GetString("text"));
                if (la.GetString("document_kind") != "ship_layout" || la.GetString("schema_version") != "1.2.0" ||
                    ka.GetString("document_kind") != "ship_structural_catalog" || ka.GetString("schema_version") != "1.0.0" ||
                    layout.GetString("document_kind") != la.GetString("document_kind") || layout.GetString("schema_version") != la.GetString("schema_version") ||
                    kit.GetString("document_kind") != ka.GetString("document_kind") || kit.GetString("schema_version") != ka.GetString("schema_version") ||
                    !ValidLayout(layout) || !ValidKit(kit) || !KitJoin(layout, kit)) throw new Refusal("invalid_reference");
                used.Add(layoutPath); used.Add(kitPath);
                if (_allowComponentIntegration && !RunSession.ValidateComponentPhysicalLayout(domain, owner, layout, out string physicalReason)) throw new Refusal(physicalReason);
                if (kind == "fixed_lifeboat")
                {
                    GdDict life = world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat");
                    var roomIds = new HashSet<string>(layout.GetArrayOrEmpty("rooms").OfType<GdDict>().Select(r => r.GetString("id")));
                    if (owner != "lifeboat" || slicePath.Length != 0 || blueprintPath.Length != 0 || reference.GetString("profile_id").Length != 0 ||
                        life.GetString("ship_id") != "lifeboat" || !life.GetDictOrEmpty("blueprint").IsEmpty ||
                        !roomIds.SetEquals(new[] { "airlock_01", "cockpit_01", "engine_bay_01" }) || !ValidMobility(life.GetDictOrEmpty("mobility"), "lifeboat")) throw new Refusal("invalid_fixed_lifeboat");
                    oldRefs[owner] = new GdDict { { "present", false } }; continue;
                }
                if (kind != "generated_ship" || !artifacts.TryGetValue(slicePath, out GdDict sa) || !artifacts.TryGetValue(blueprintPath, out GdDict ba) ||
                    ba.GetString("document_kind") != "ship_blueprint" || ba.GetString("schema_version") != "component-blueprint-1") throw new Refusal("reference_missing");
                GdDict bp = ParseObject(ba.GetString("text"));
                if (!JsonInteger(bp.Get("size"), out long size) || size < 0 || size > 2 || !JsonInteger(bp.Get("condition"), out long condition) || condition < 0 || condition > 2 ||
                    !JsonInteger(bp.Get("seed_value"), out _, true)) throw new Refusal("invalid_reference");
                if (bp.GetString("generation_profile") != reference.GetString("profile_id") ||
                    !(bp.Get("room_count_range") is GdDict range) || !JsonInteger(range.Get("min"), out long min) || !JsonInteger(range.Get("max"), out long max) || min <= 0 || max < min ||
                    layout.Has("generation_seed") && (!JsonInteger(layout.Get("generation_seed"), out long layoutSeed, true) || !JsonInteger(bp.Get("seed_value"), out long blueprintSeed, true) || layoutSeed != blueprintSeed)) throw new Refusal("binding_mismatch");
                if (owner != "ship_start")
                {
                    GdDict retained = world.GetDictOrEmpty("visited_ships").Values.OfType<GdDict>().SingleOrDefault(s => s.GetString("ship_id") == owner);
                    GdDict original = bp.DeepCopy(); original.Erase("document_kind"); original.Erase("schema_version");
                    if (retained == null || !V.VariantEquals(original, retained.Get("blueprint"))) throw new Refusal("binding_mismatch");
                }
                if (sa.GetString("document_kind") == "runtime_generated_gameplay_slice")
                {
                    if (sa.GetString("schema_version") != "component-runtime-gameplay-1") throw new Refusal("unsupported_schema");
                    GdDict gameplay = ParseObject(sa.GetString("text"));
                    if (!ValidRuntimeGameplay(layout, gameplay, bp)) throw new Refusal("invalid_runtime_gameplay");
                    // Deep validation adapter only: published artifacts retain their original bytes and distinct envelope.
                    GdDict adapterSlice = legacy.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a => a.GetString("logical_path") == slicePath);
                    GdDict normalized = gameplay.DeepCopy(); normalized["document_kind"] = "ship_gameplay_slice"; normalized["schema_version"] = "1.1.0";
                    adapterSlice["document_kind"] = "ship_gameplay_slice"; adapterSlice["schema_version"] = "1.1.0"; adapterSlice["text"] = GdJson.Stringify(normalized);
                    GdDict adapterLayout = legacy.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a => a.GetString("logical_path") == layoutPath);
                    normalized = layout.DeepCopy(); normalized["generation_seed"] = bp.Get("seed_value"); adapterLayout["text"] = GdJson.Stringify(normalized);
                }
                else if (sa.GetString("document_kind") != "ship_gameplay_slice" || sa.GetString("schema_version") != "1.1.0") throw new Refusal("unsupported_schema");
                used.Add(slicePath); used.Add(blueprintPath);
                oldRefs[owner] = new GdDict { { "present", true }, { "layout_path", layoutPath }, { "gameplay_slice_path", slicePath }, { "kit_path", kitPath }, { "profile_id", reference.Get("profile_id") } };
            }
            if (used.Count != artifacts.Count || !oldRefs.Has("lifeboat")) throw new Refusal("binding_mismatch");
            GdDict mobile = world.GetDictOrEmpty("mobile_home_state");
            if (mobile.GetInt("version") != 1 || !ValidMobility(mobile.GetDictOrEmpty("home_mobility"), "ship_start")) throw new Refusal("invalid_payload");
            if (mobile.Has("starting_home_anchor")) throw new Refusal("invalid_payload");
            oldWorld.Erase("mobile_home_state");
            legacy["run_text"] = paid ? PaidSnapshotCodec.Stringify(oldActive) : GdJson.Stringify(oldActive); legacy["world_text"] = paid ? PaidSnapshotCodec.Stringify(oldWorld) : GdJson.Stringify(oldWorld);
            GdDict oldBinding = legacy.GetDictOrEmpty("binding"); oldBinding.Erase("binding_version"); oldBinding.Erase("component_revision"); oldBinding.Erase("player_pose_owner_id"); oldBinding.Erase("save_mode"); oldBinding["ship_references"] = oldRefs;
            var needed = new HashSet<string>(); foreach (GdDict r in oldRefs.Values.OfType<GdDict>()) if (r.GetBool("present")) { needed.Add(r.GetString("layout_path")); needed.Add(r.GetString("gameplay_slice_path")); needed.Add(r.GetString("kit_path")); }
            legacy["artifacts"] = new GdArray(legacy.GetArrayOrEmpty("artifacts").OfType<GdDict>().Where(a => needed.Contains(a.GetString("logical_path"))));
            ValidateRequest(legacy, run, slot, true, paid); // Structural adapter retains exact paid numeric types and values.
            var candidate = new Candidate(request);
            candidate.Entries.Add(new Entry("run", "", "run_snapshot", runVersion, request.GetString("run_text")));
            candidate.Entries.Add(new Entry("world", "", "world_snapshot", worldVersion, request.GetString("world_text")));
            int ordinal = 0;
            foreach (GdDict a in request.GetArrayOrEmpty("artifacts").OfType<GdDict>().OrderBy(a => a.GetString("logical_path"), StringComparer.Ordinal))
            { var entry = new Entry("artifact", a.GetString("logical_path"), a.GetString("document_kind"), a.GetString("schema_version"), a.GetString("text")); entry.File = ArtifactFile(ordinal++); candidate.Entries.Add(entry); }
            return candidate;
        }
        static GdDict LegacyRun(GdDict run)
        { GdDict old = run.DeepCopy(); old["slice_version"] = SaveLoadService.CURRENT_SLICE_VERSION; old.Erase("component_domain"); old.Erase("generation_id"); old.Erase("capture_revision"); old.GetDictOrEmpty("crafting_summary").Erase("paid_craft"); return old; }

        static bool ValidRuntimeGameplay(GdDict layout, GdDict gameplay, GdDict blueprint)
        {
            if (gameplay == null || gameplay.Count != 7 || !StringFields(gameplay, "start_room", "goal_room") ||
                !JsonInteger(blueprint.Get("seed_value"), out long seed, true)) return false;
            string program = layout.GetString("program_id"), suffix = "-seed-" + Decimal(seed);
            if (!program.StartsWith("procgen-", StringComparison.Ordinal) || !program.EndsWith(suffix, StringComparison.Ordinal) || program.Length <= 7 + suffix.Length) return false;
            string archetype = program.Substring(7, program.Length - 7 - suffix.Length);
            if (!archetype.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-')) return false;
            foreach (string key in new[] { "generation_seed", "seed_value" })
                if (layout.Has(key) && (!JsonInteger(layout.Get(key), out long declared, true) || declared != seed)) return false;
            if (layout.GetString("generation_profile") != blueprint.GetString("generation_profile")) return false;
            var rooms = layout.GetArrayOrEmpty("rooms").OfType<GdDict>().ToDictionary(r => r.GetString("id"), StringComparer.Ordinal);
            if (!rooms.ContainsKey(gameplay.GetString("start_room")) || !rooms.ContainsKey(gameplay.GetString("goal_room"))) return false;
            foreach (string key in new[] { "objectives", "loot_containers", "fire_zones", "arc_zones", "breach_zones" }) if (!(gameplay.Get(key) is GdArray)) return false;
            if (!gameplay.GetArrayOrEmpty("fire_zones").IsEmpty || !gameplay.GetArrayOrEmpty("breach_zones").IsEmpty) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal); long sequence = 0;
            foreach (object value in gameplay.GetArrayOrEmpty("objectives"))
            {
                if (!(value is GdDict row) || !RuntimeFields(row, new[] { "id", "sequence", "type", "kind", "room_id", "approach_cell" }, new[] { "loot_table", "slot_kind", "slot_index" }) ||
                    !StringFields(row, "id", "type", "kind", "room_id") || !Nonempty(row.Get("id")) || !ids.Add(row.GetString("id")) ||
                    !JsonInteger(row.Get("sequence"), out long next) || next != ++sequence || row.GetString("kind") != "single" ||
                    row.GetString("type") != "salvage" && row.GetString("type") != "interact" ||
                    row.Has("loot_table") && !Nonempty(row.Get("loot_table")) || !RuntimeApproach(row, rooms)) return false;
            }
            if (sequence == 0) return false;
            ids.Clear();
            foreach (object value in gameplay.GetArrayOrEmpty("loot_containers"))
            {
                if (!(value is GdDict row) || !RuntimeFields(row, new[] { "id", "kind", "room_id", "approach_cell", "loot_table" }, new[] { "slot_kind", "slot_index", "contents" }) ||
                    !StringFields(row, "id", "kind", "room_id", "loot_table") || !Nonempty(row.Get("id")) || !ids.Add(row.GetString("id")) ||
                    row.GetString("kind") != "generic_locker" && row.GetString("kind") != "generic_crate" || !RuntimeApproach(row, rooms)) return false;
                if (row.Has("contents"))
                {
                    if (!(row.Get("contents") is GdArray contents)) return false;
                    foreach (object item in contents) if (!(item is GdDict entry) || entry.Count != 2 || !Nonempty(entry.Get("item_id")) || !JsonInteger(entry.Get("qty"), out long quantity) || quantity <= 0) return false;
                }
            }
            ids.Clear();
            foreach (object value in gameplay.GetArrayOrEmpty("arc_zones"))
            {
                if (!(value is GdDict row) || !RuntimeFields(row, new[] { "id", "from_room", "to_room", "kind", "rationale" }, new[] { "from_cell", "to_cell" }) ||
                    !StringFields(row, "id", "from_room", "to_room", "kind", "rationale") || !Nonempty(row.Get("id")) || !ids.Add(row.GetString("id")) || row.GetString("kind") != "electrical_arc" ||
                    !rooms.TryGetValue(row.GetString("from_room"), out GdDict from) || !rooms.TryGetValue(row.GetString("to_room"), out GdDict to) || from == to ||
                    row.Has("from_cell") && !RuntimeCell(row.Get("from_cell"), from) || row.Has("to_cell") && !RuntimeCell(row.Get("to_cell"), to)) return false;
                if (!layout.GetArrayOrEmpty("room_links").OfType<GdDict>().Any(link => link.GetString("from_room") == row.GetString("from_room") && link.GetString("to_room") == row.GetString("to_room"))) return false;
            }
            return true;
        }
        static bool RuntimeFields(GdDict row, string[] required, string[] optional)
            => required.All(row.Has) && row.Keys.OfType<string>().All(k => required.Contains(k) || optional.Contains(k));
        static List<GdArray> RuntimeFloors(GdDict room)
        {
            var cells = new List<GdArray>(); var seen = new HashSet<string>();
            foreach (GdDict placement in room.GetArrayOrEmpty("structural_placements").OfType<GdDict>())
            {
                string name = placement.GetString("name"); if (!name.StartsWith("floor_cell", StringComparison.Ordinal) || !Position(placement.Get("world_position"))) continue;
                GdArray cell = LayoutSerializer.ParseSlotCell(name);
                if (cell.Count == 2 && JsonInteger(cell[0], out long x) && JsonInteger(cell[1], out long z) && seen.Add(Decimal(x) + ":" + Decimal(z))) cells.Add(cell);
            }
            return cells;
        }
        static bool RuntimeCell(object value, GdDict room)
        {
            if (!(value is GdArray cell) || cell.Count != 3 || !JsonInteger(cell[0], out long x) || !JsonInteger(cell[1], out long z) ||
                !JsonInteger(cell[2], out long deck) || !JsonInteger(room.Get("deck"), out long roomDeck) || deck != roomDeck) return false;
            return RuntimeFloors(room).Any(c => V.I64(c[0]) == x && V.I64(c[1]) == z);
        }
        static bool RuntimeApproach(GdDict row, Dictionary<string, GdDict> rooms)
        {
            if (!rooms.TryGetValue(row.GetString("room_id"), out GdDict room) || !RuntimeCell(row.Get("approach_cell"), room)) return false;
            if (!row.Has("slot_kind") && !row.Has("slot_index")) return true;
            if (!StringFields(row, "slot_kind") || !JsonInteger(row.Get("slot_index"), out long index) || index < 0 || index > int.MaxValue) return false;
            string kind = row.GetString("slot_kind"); GdArray cell = (GdArray)row.Get("approach_cell");
            GdArray source = kind == "floor" ? new GdArray(RuntimeFloors(room)) : room.GetDictOrEmpty("interior_zones").GetArrayOrEmpty(kind == "center" ? "center_slots" : kind == "wall" ? "wall_slots" : kind == "reserved" ? "reserved_cells" : "unsupported");
            if (index >= source.Count) return false;
            GdArray expected = LayoutSerializer.ParseSlotCell(source[(int)index]);
            return expected.Count == 2 && JsonInteger(expected[0], out long x) && JsonInteger(expected[1], out long z) && JsonInteger(cell[0], out long actualX) && JsonInteger(cell[1], out long actualZ) && x == actualX && z == actualZ;
        }

        // This adapter compares owned typed summaries with ordinary JSON mirrors. Unsafe integer mirrors are refused,
        // rather than rounding the canonical owner or altering the global JSON parser.
        internal static bool SameComponentMirror(object owned, object wire)
        {
            if (owned is long integer) return JsonInteger(wire, out long parsedInteger) && integer == parsedInteger;
            if (owned is double number) return Number(wire, out double parsedNumber) && GdJson.Stringify(number) == GdJson.Stringify(parsedNumber);
            if (owned is GdDict dictionary)
            {
                if (!(wire is GdDict other) || dictionary.Count != other.Count) return false;
                foreach (var pair in dictionary) if (!other.Has(pair.Key) || !SameComponentMirror(pair.Value, other.Get(pair.Key))) return false;
                return true;
            }
            if (owned is GdArray array)
            {
                if (!(wire is GdArray other) || array.Count != other.Count) return false;
                for (int i = 0; i < array.Count; i++) if (!SameComponentMirror(array[i], other[i])) return false;
                return true;
            }
            return V.VariantEquals(owned, wire);
        }
        static void ValidateComponentMirrors(GdDict domain, GdDict active, GdDict world, GdDict home, bool paid = false)
        {
            bool Equal(object owned, object wire) => paid ? PaidSnapshotCodec.Same(owned, wire) : SameComponentMirror(owned, wire);
            string mismatch = "", context = "active";
            string Difference(object owned, object wire, string path)
            {
                if (owned is GdDict left && wire is GdDict right)
                {
                    foreach (var item in left)
                    {
                        string child = path + "[" + GdJson.Stringify(V.Str(item.Key)) + "]";
                        if (!right.Has(item.Key)) return child + ":missing_wire_key";
                        if (!Equal(item.Value, right.Get(item.Key))) return Difference(item.Value, right.Get(item.Key), child);
                    }
                    return path + ":dictionary_count";
                }
                if (owned is GdArray a && wire is GdArray b)
                {
                    for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                        if (!Equal(a[i], b[i])) return Difference(a[i], b[i], path + "[" + i + "]");
                    return path + ":array_count";
                }
                return path + ":owned=" + (owned?.GetType().FullName ?? "null") + ":wire=" +
                    (wire?.GetType().FullName ?? "null") + ":value_or_tag";
            }
            bool Same(object owned, object wire, string field, object childKey = null)
            {
                if (Equal(owned, wire)) return true;
                string path = context + "." + field;
                if (childKey != null) path += "[" + GdJson.Stringify(V.Str(childKey)) + "]";
                mismatch = Difference(owned, wire, path); return false;
            }
            Refusal Failure(string label) => new Refusal("component_mirror_mismatch", context + ":" + label + (mismatch.Length > 0 ? ":" + mismatch : ""));

            if (!ComponentRawShipSystems.Validate(active.Get("ship_systems_summary"))) throw Failure("active.ship_systems_summary");
            GdDict participants = domain.GetDictOrEmpty("participating_state"), stacks = participants.GetDictOrEmpty("stacks");
            foreach (GdDict snapshot in new[] { active, home })
            {
                context = ReferenceEquals(snapshot, active) ? "active" : "home";
                GdDict inventory = snapshot.GetDictOrEmpty("inventory_summary").DeepCopy(), crafting = snapshot.GetDictOrEmpty("crafting_summary").DeepCopy();
                inventory.Erase("combat_hotbar_text"); inventory.Erase("threat_summary"); crafting.Erase("field_crafting"); crafting.Erase("paid_craft");
                if (paid && !Same(participants.Get("spoilage"), snapshot.Get("spoilage_summary"), "spoilage_summary")) throw Failure("participant.spoilage");
                if (!Same(participants.Get("inventory"), inventory, "inventory_summary") ||
                    !Same(participants.Get("progression"), snapshot.Get("player_progression_summary"), "player_progression_summary") ||
                    !Same(participants.Get("crafting"), crafting, "crafting_summary") ||
                    !Same(participants.GetDictOrEmpty("field_crafting").Get("field_crafting"), snapshot.GetDictOrEmpty("crafting_summary").Get("field_crafting"), "crafting_summary.field_crafting")) throw Failure("participant");
            }
            var ships = new Dictionary<string, GdDict>(StringComparer.Ordinal)
            {
                ["ship_start"] = new GdDict { { "systems", home.Get("ship_systems_summary") }, { "inventory", world.Get("home_ship_inventory") }, { "carts", world.Get("home_ship_carts") } },
                ["lifeboat"] = world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat")
            };
            foreach (GdDict ship in world.GetDictOrEmpty("visited_ships").Values.OfType<GdDict>())
            { if (ships.ContainsKey(ship.GetString("ship_id"))) throw Failure("ships.duplicate_owner"); ships.Add(ship.GetString("ship_id"), ship); }
            foreach (var pair in ships)
            {
                string owner = pair.Key; GdDict ship = pair.Value;
                context = owner == "ship_start" ? "home" : owner == "lifeboat" ? "lifeboat" : "visited_ship";
                GdDict expectedCargo = stacks.GetDictOrEmpty("ship_cargo:" + owner), cargo = ship.GetDictOrEmpty("inventory");
                if (cargo.IsEmpty)
                { if (!expectedCargo.GetDictOrEmpty("items").IsEmpty) throw Failure("ship.inventory.missing_items"); }
                else if (!Same(expectedCargo, cargo, "ship.inventory")) throw Failure("ship.inventory");
                var cartHolders = new HashSet<string>(StringComparer.Ordinal);
                foreach (object value in ship.GetArrayOrEmpty("carts"))
                {
                    if (!(value is GdDict cart)) throw Failure("ship.carts.row_type");
                    string holder = "cart:" + owner + ":" + cart.GetString("cart_id");
                    if (!cartHolders.Add(holder) || !stacks.Has(holder) || !Same(stacks.Get(holder), cart.Get("hold"), "ship.carts.hold")) throw Failure("ship.carts.identity_or_hold");
                }
                if (!cartHolders.SetEquals(stacks.Keys.OfType<string>().Where(k => k.StartsWith("cart:" + owner + ":", StringComparison.Ordinal)))) throw Failure("ship.carts.holder_set");
                if (!ComponentRawShipSystems.Validate(ship.Get("systems"))) throw Failure("ship.systems.raw");
                GdDict systems = ship.GetDictOrEmpty("systems").GetDictOrEmpty("systems");
                foreach (GdDict machine in domain.GetDictOrEmpty("machinery").Values.OfType<GdDict>().Where(m => m.GetString("owner_id") == owner))
                {
                    GdDict sub = systems.GetDictOrEmpty(machine.GetString("system_id")).GetArrayOrEmpty("subcomponents").OfType<GdDict>().SingleOrDefault(s => s.GetString("subcomponent_id") == machine.GetString("subcomponent_id"));
                    if (sub == null || !Same(machine.Get("health"), sub.Get("health"), "ship.systems.subcomponents.health")) throw Failure("ship.systems.machine");
                }
            }
            context = "active";
            string activeOwner = world.GetString("current_location").Length == 0 ? "ship_start" : world.GetDictOrEmpty("visited_ships").GetDictOrEmpty(world.GetString("current_location")).GetString("ship_id");
            if (!ships.TryGetValue(activeOwner, out GdDict current)) throw Failure("active.owner_missing");
            foreach (var pair in current.GetDictOrEmpty("systems"))
                if (!Same(pair.Value, active.GetDictOrEmpty("ship_systems_summary").Get(pair.Key), "ship_systems_summary", pair.Key)) throw Failure("active.ship_systems_summary");
        }
    }
}
