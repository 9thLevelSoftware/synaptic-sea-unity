using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Unused in-memory sole publisher for the bounded component domain.</summary>
    public sealed class DomainTransactionCoordinator
    {
        DomainBundle _current;
        readonly Action<string> _stageHook;
        readonly Action<GdDict> _notification;
        readonly Action<GdDict> _publishViews;
        readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        readonly HashSet<string> _pendingCommands = new HashSet<string>(StringComparer.Ordinal);
        // Presentation details are transient; canonical domain outcomes live only in the bundle's receipts.
        readonly Dictionary<string, GdDict> _outcomes = new Dictionary<string, GdDict>(StringComparer.Ordinal);
        bool _busy;

        sealed class Pending
        {
            public readonly GdDict Command, Candidate, Result;
            public readonly long ExpectedRevision;
            public Pending(GdDict command, GdDict candidate, GdDict result, long revision)
            {
                Command = command.DeepCopy(); Candidate = candidate.DeepCopy(); Result = result.DeepCopy(); ExpectedRevision = revision;
            }
        }

        public DomainTransactionCoordinator(GdDict initialSummary, Action<string> stageHook = null, Action<GdDict> notification = null, Action<GdDict> publishViews = null)
        {
            if (!DomainBundle.TryCreate(initialSummary, out _current, out string reason)) throw new ArgumentException(reason, nameof(initialSummary));
            _stageHook = stageHook; _notification = notification; _publishViews = publishViews;
        }

        public GdDict GetSummary() => _current.GetSummary();
        public GdDict GetProjections() => _current.GetProjections();
        internal GdDict GetParticipantProjection(string key) => _current.GetParticipantProjection(key);
        internal long SchemaVersion => _current.SchemaVersion;
        internal bool HasExecutablePaidChannel(string channel) => _current.HasExecutablePaidChannel(channel);

        internal GdDict PrepareLive(GdDict command, Func<GdDict, GdDict> stageEffects)
        {
            GdDict prepared = Prepare(command);
            if (!prepared.GetBool("ok")) return prepared;
            string id = prepared.GetString("transaction_id");
            Pending pending = _pending[id];
            try
            {
                GdDict candidate = stageEffects(pending.Candidate.DeepCopy());
                if (candidate.GetInt("schema_version") < 2 || !DomainBundle.TryCreatePreparation(candidate, command, out _, out string reason) ||
                    !ConservedTransition(_current.GetSummary(), candidate, command))
                    throw new InvalidOperationException("Invalid staged live component effect.");
                _pending[id] = new Pending(command, candidate, pending.Result, pending.ExpectedRevision);
                prepared["candidate"] = candidate.DeepCopy();
                return prepared;
            }
            catch
            {
                _pending.Remove(id); _pendingCommands.Remove(command.GetString("command_id"));
                throw;
            }
        }

        public GdDict Prepare(GdDict command)
        {
            if (_busy) return Failure("reentrant_mutation");
            if (command == null || !ItemInstanceState.IsSafeSnapshot(command) || !(command.Get("command_id") is string commandId) || string.IsNullOrWhiteSpace(commandId)) return Failure("invalid_command");
            GdDict before = _current.GetSummary();
            string transactionId = "component_transfer:" + commandId;
            if (_pendingCommands.Contains(commandId) || before.GetDictOrEmpty("receipts").Has(transactionId)) return Failure("duplicate_command", transactionId, commandId);
            _busy = true;
            try
            {
                GdDict ownedCommand = command.DeepCopy();
                GdDict prepared = new ComponentTransferService().Prepare(ownedCommand, before.DeepCopy());
                if (!prepared.GetBool("ok")) return prepared.DeepCopy();
                if (prepared.GetString("transaction_id") != transactionId || prepared.GetInt("expected_revision", -1) != _current.Revision ||
                    !(prepared.Get("candidate") is GdDict candidate) || !(prepared.Get("result") is GdDict result) ||
                    !DomainBundle.TryCreatePreparation(candidate, ownedCommand, out _, out _) || !ConservedTransition(before, candidate, ownedCommand) || !MatchesEffect(result, ownedCommand))
                    return Failure("invalid_preparation", transactionId, commandId);
                _pending[transactionId] = new Pending(ownedCommand, candidate, result, _current.Revision);
                _pendingCommands.Add(commandId);
                return prepared.DeepCopy();
            }
            finally { _busy = false; }
        }

        /// <summary>Concrete paid-operation preparation; the session supplies current gates and staged effects privately.</summary>
        internal GdDict PrepareCraft(GdDict command, Func<GdDict, GdDict> stage)
        {
            if (_busy) return Failure("reentrant_mutation");
            if (command == null || !ItemInstanceState.IsSafeSnapshot(command) || string.IsNullOrWhiteSpace(command.GetString("command_id"))) return Failure("invalid_command");
            GdDict before = _current.GetSummary();
            if (!PaidCraftingState.IsDomainVersion(before.GetInt("schema_version"))) return Failure("paid_crafting_inactive");
            foreach (object entry in before.GetDictOrEmpty("receipts").Values)
            {
                GdDict prior = (GdDict)entry;
                if (prior.GetString("command_id") != command.GetString("command_id")) continue;
                return prior.GetString("command_hash") == PaidCraftingState.Hash(command) ? ExpandedResult(CommittedResult(prior)) : Failure("command_collision");
            }
            if (before.GetInt("revision") == long.MaxValue) return Failure("revision_overflow");
            _busy = true;
            try
            {
                GdDict candidate; candidate = before.DeepCopy(); candidate["revision"] = before.GetInt("revision") + 1;
                GdDict effect = stage(candidate);
                if (!effect.GetBool("ok", true)) return effect;
                if (command.GetBool("internal"))
                {
                    effect["internal"] = true;
                    effect["progress_seconds"] = PaidCraftingState.State(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(effect.GetString("job_id")).GetFloat("progress_seconds");
                }
                PaidCraftingState.PruneInternalReceipts(candidate, command, effect);
                string id = effect.GetString("operation") == "craft_complete"
                    ? PaidCraftingState.State(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(effect.GetString("job_id")).GetString("completion_commit_id")
                    : "craft:" + command.GetString("command_id");
                if (!PaidCraftingState.Conserved(before, candidate, effect)) return Failure("invalid_craft_transition");
                candidate.GetDictOrEmpty("receipts")[id] = new GdDict { { "schema_version", 1L }, { "transaction_id", id }, { "commit_id", id },
                    { "command_id", command.Get("command_id") }, { "command", command.DeepCopy() }, { "command_hash", PaidCraftingState.Hash(command) },
                    { "revision", candidate.Get("revision") }, { "result", effect.DeepCopy() } };
                if (!DomainBundle.TryCreate(candidate, out _, out string reason)) return Failure("invalid_candidate:" + reason);
                _pending[id] = new Pending(command, candidate, effect, _current.Revision); _pendingCommands.Add(command.GetString("command_id"));
                return new GdDict { { "ok", true }, { "committed", false }, { "transaction_id", id }, { "candidate", candidate.DeepCopy() } };
            }
            catch (Exception e) { var failure = Failure("staging_failed"); failure["detail"] = Describe(e); return failure; }
            finally { _busy = false; }
        }

        internal GdDict PrepareStudy(GdDict command, Func<GdDict, GdDict> stage)
        {
            if (_busy) return Failure("reentrant_mutation");
            if (command == null || !ItemInstanceState.IsSafeSnapshot(command) || string.IsNullOrWhiteSpace(command.GetString("command_id"))) return Failure("invalid_command");
            GdDict before = _current.GetSummary();
            if (before.GetInt("schema_version") != 4 && before.GetInt("schema_version") != 5) return Failure("manual_study_inactive");
            foreach (object entry in before.GetDictOrEmpty("receipts").Values)
            {
                GdDict prior = (GdDict)entry;
                if (prior.GetString("command_id") != command.GetString("command_id")) continue;
                return prior.GetString("command_hash") == PaidCraftingState.Hash(command) ? ExpandedResult(CommittedResult(prior)) : Failure("command_collision");
            }
            if (before.GetInt("revision") == long.MaxValue) return Failure("revision_overflow");
            _busy = true;
            try
            {
                GdDict candidate; candidate = before.DeepCopy(); candidate["revision"] = before.GetInt("revision") + 1;
                GdDict effect = stage(candidate);
                if (!effect.GetBool("ok", true)) return effect;
                ManualStudyState.PruneProgress(candidate, command);
                string id = effect.GetString("operation") == "study_complete"
                    ? ManualStudyState.CompletionId(ManualStudyState.State(candidate), command.GetString("book_id")) : "manual_study:" + command.GetString("command_id");
                if (!ManualStudyState.Conserved(before, candidate, effect)) return Failure("invalid_study_transition");
                candidate.GetDictOrEmpty("receipts")[id] = new GdDict { { "schema_version", 1L }, { "transaction_id", id }, { "commit_id", id },
                    { "command_id", command.Get("command_id") }, { "command", command.DeepCopy() }, { "command_hash", PaidCraftingState.Hash(command) },
                    { "revision", candidate.Get("revision") }, { "result", effect.DeepCopy() } };
                if (!DomainBundle.TryCreate(candidate, out _, out string reason)) return Failure("invalid_candidate:" + reason);
                _pending[id] = new Pending(command, candidate, effect, _current.Revision); _pendingCommands.Add(command.GetString("command_id"));
                return new GdDict { { "ok", true }, { "committed", false }, { "transaction_id", id }, { "candidate", candidate.DeepCopy() } };
            }
            catch (Exception e) { var failure = Failure("staging_failed"); failure["detail"] = Describe(e); return failure; }
            finally { _busy = false; }
        }

        public GdDict Commit(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId)) return Failure("missing_transaction");
            // A receipt read has no mutation or notification, including a same-ID read from a notification callback.
            GdDict current = _current.GetSummary();
            if (current.GetDictOrEmpty("receipts").Get(transactionId) is GdDict committedReceipt)
                return ExpandedResult(_outcomes.TryGetValue(transactionId, out GdDict prior) ? prior : CommittedResult(committedReceipt));
            if (_busy) return Failure("reentrant_mutation", transactionId);
            if (!_pending.TryGetValue(transactionId, out Pending pending)) return Failure("missing_transaction", transactionId);
            string commandId = pending.Command.GetString("command_id");
            GdDict publishedResult = null;
            bool published = false;
            _busy = true;
            try
            {
                if (_current.Revision != pending.ExpectedRevision) return Failure("stale_domain", transactionId, commandId);
                bool study = ManualStudyState.IsOperation(pending.Result.GetString("operation"));
                bool craft = PaidCraftingState.IsOperation(pending.Result.GetString("operation")) || study;
                if (craft ? !DomainBundle.TryCreate(pending.Candidate, out _, out _) || !(study ? ManualStudyState.Conserved(current, pending.Candidate, pending.Result) : PaidCraftingState.Conserved(current, pending.Candidate, pending.Result))
                    : !DomainBundle.TryCreatePreparation(pending.Candidate, pending.Command, out _, out _) || !ConservedTransition(current, pending.Candidate, pending.Command) || !MatchesEffect(pending.Result, pending.Command))
                    return Failure("invalid_preparation", transactionId, commandId);

                // Staging remains private. Readers at every hook see _current, never any per-field candidate.
                GdDict staged = new GdDict { { "schema_version", pending.Candidate.Get("schema_version") }, { "revision", pending.Candidate.Get("revision") },
                    { "registry", pending.Candidate.GetDictOrEmpty("registry").DeepCopy() } };
                _stageHook?.Invoke("registry");
                staged["holders"] = pending.Candidate.GetDictOrEmpty("holders").DeepCopy(); _stageHook?.Invoke("holders");
                staged["machinery"] = pending.Candidate.GetDictOrEmpty("machinery").DeepCopy(); _stageHook?.Invoke("machinery");
                if (pending.Candidate.GetInt("schema_version") >= 2)
                {
                    staged["physical_slots"] = pending.Candidate.GetDictOrEmpty("physical_slots").DeepCopy(); _stageHook?.Invoke("placement");
                    staged["participating_state"] = pending.Candidate.GetDictOrEmpty("participating_state").DeepCopy(); _stageHook?.Invoke("inventory");
                    staged["component_work"] = pending.Candidate.GetDictOrEmpty("component_work").DeepCopy(); _stageHook?.Invoke("job");
                    staged["command_sequence"] = pending.Candidate.Get("command_sequence");
                    staged["registered_owners"] = pending.Candidate.GetArrayOrEmpty("registered_owners").DeepCopy(); _stageHook?.Invoke("progression");
                }
                if (PaidCraftingState.IsDomainVersion(pending.Candidate.GetInt("schema_version"))) staged["domain_mode"] = pending.Candidate.Get("domain_mode");
                GdDict receipts; receipts = pending.Candidate.GetDictOrEmpty("receipts").DeepCopy();
                GdDict receipt = craft ? receipts.GetDictOrEmpty(transactionId) : new GdDict { { "schema_version", 1L }, { "transaction_id", transactionId }, { "command_id", commandId },
                    { "revision", pending.Candidate.Get("revision") }, { "result", pending.Result.DeepCopy() } };
                if (pending.Candidate.GetInt("schema_version") >= 2) receipt["commit_id"] = transactionId;
                receipts[transactionId] = receipt; staged["receipts"] = receipts; _stageHook?.Invoke("receipt");
                _stageHook?.Invoke("validation");
                if (!DomainBundle.TryCreate(staged, out DomainBundle complete, out string reason)) return Failure("invalid_candidate:" + reason, transactionId, commandId);
                publishedResult = CommittedResult(receipt);
                _stageHook?.Invoke("publication");
                _publishViews?.Invoke(complete.GetSummary());
                _current = complete; // The sole publication assignment: all participating state and its receipt.
                published = true;
                if (craft)
                    foreach (object oldId in current.GetDictOrEmpty("receipts").Keys)
                        if (!receipts.Has(oldId)) _outcomes.Remove(V.Str(oldId));

                try { _notification?.Invoke(ExpandedResult(publishedResult)); }
                catch (Exception e)
                {
                    publishedResult["presentation_failed"] = true; publishedResult["presentation_error"] = Describe(e);
                }
                _outcomes[transactionId] = publishedResult.DeepCopy();
                return ExpandedResult(publishedResult);
            }
            catch (Exception e)
            {
                // Once the receipt owns the effect, no subsequent diagnostic failure can report an uncommitted result.
                if (published)
                {
                    publishedResult["presentation_failed"] = true; publishedResult["presentation_error"] = Describe(e);
                    _outcomes[transactionId] = publishedResult.DeepCopy();
                    return ExpandedResult(publishedResult);
                }
                GdDict failure = Failure("staging_failed", transactionId, commandId); failure["detail"] = Describe(e);
                return failure;
            }
            finally
            {
                _pending.Remove(transactionId); _pendingCommands.Remove(commandId); _busy = false;
            }
        }

        public bool ApplySummary(GdDict summary)
        {
            if (_busy) return false;
            _busy = true;
            try
            {
                if (!DomainBundle.TryCreate(summary, out DomainBundle candidate, out _) || candidate.Revision < _current.Revision) return false;
                GdDict before = _current.GetSummary(), after = candidate.GetSummary();
                if (candidate.Revision == _current.Revision) return V.VariantEquals(before, after);
                // Import cannot erase or reinterpret an already published command, even at a newer revision.
                GdDict previousReceipts = before.GetDictOrEmpty("receipts"), importedReceipts = after.GetDictOrEmpty("receipts");
                foreach (object key in previousReceipts.Keys)
                    if (!importedReceipts.Has(key) || !V.VariantEquals(previousReceipts[key], importedReceipts[key])) return false;
                _current = candidate;
                return true;
            }
            finally { _busy = false; }
        }

        static GdDict Failure(string reason, string transactionId = "", string commandId = "")
            => new GdDict { { "ok", false }, { "committed", false }, { "reason", reason }, { "transaction_id", transactionId }, { "command_id", commandId } };

        GdDict ExpandedResult(GdDict compact)
        {
            GdDict result; result = compact.DeepCopy();
            if (result.GetDictOrEmpty("result").GetString("operation") == "craft_complete")
                result["result"] = PaidCraftRewardProof.Expand(PaidCraftingState.State(_current.GetSummary()), result.GetDictOrEmpty("result"));
            return result;
        }

        static GdDict CommittedResult(GdDict receipt)
        {
            var result = new GdDict { { "ok", true }, { "committed", true }, { "reason", receipt.GetDictOrEmpty("result").GetString("reason", "committed") },
                { "transaction_id", receipt.Get("transaction_id") }, { "command_id", receipt.Get("command_id") },
                { "revision", receipt.Get("revision") }, { "commit_id", receipt.Get("commit_id", receipt.Get("transaction_id")) }, { "result", receipt.GetDictOrEmpty("result").DeepCopy() },
                { "presentation_failed", false }, { "presentation_error", "" } };
            foreach (string key in new[] { "job_id", "reconciliation_id", "recipe_id" })
                if (receipt.GetDictOrEmpty("result").Has(key)) result[key] = receipt.GetDictOrEmpty("result").Get(key);
            return result;
        }

        static string Describe(Exception exception)
        {
            string type = exception.GetType().FullName;
            try { string message = exception.Message; return string.IsNullOrWhiteSpace(message) ? type : type + ": " + message; }
            catch { return type; }
        }

        static bool MatchesEffect(GdDict result, GdDict command)
        {
            var expected = new GdDict { { "operation", command.Get("operation") }, { "instance_id", command.Get("instance_id") },
                { "source_holder_id", command.Get("source_holder_id") }, { "destination_holder_id", command.Get("destination_holder_id") } };
            if (command.GetString("operation") == "swap") expected["swap_instance_id"] = command.Get("swap_instance_id");
            return V.VariantEquals(expected, result);
        }

        static bool ConservedTransition(GdDict before, GdDict candidate, GdDict command)
        {
            if (before.GetInt("schema_version") >= 2)
            {
                GdDict oldParticipants = before.GetDictOrEmpty("participating_state"), newParticipants = candidate.GetDictOrEmpty("participating_state");
                foreach (string key in new[] { "items", "tool_ids", "active_effects", "drain_multiplier", "max_weight" })
                    if (!V.VariantEquals(oldParticipants.GetDictOrEmpty("inventory").Get(key), newParticipants.GetDictOrEmpty("inventory").Get(key))) return false;
                foreach (string key in new[] { "stacks", "crafting", "field_crafting", "spoilage", "manual_study" })
                    if (!V.VariantEquals(oldParticipants.Get(key), newParticipants.Get(key))) return false;
                if (PaidCraftingState.IsDomainVersion(before.GetInt("schema_version")))
                {
                    GdDict oldPaid = oldParticipants.GetDictOrEmpty("paid_crafting").DeepCopy(), nextPaid = newParticipants.GetDictOrEmpty("paid_crafting").DeepCopy();
                    oldPaid.Erase("reward_history"); nextPaid.Erase("reward_history");
                    if (!V.VariantEquals(oldPaid, nextPaid) || !PaidCraftRewardProof.Conserved(oldParticipants, newParticipants)) return false;
                }
                if (before.GetInt("schema_version") != candidate.GetInt("schema_version") || !V.VariantEquals(before.Get("domain_mode"), candidate.Get("domain_mode"))) return false;
                if (!ComponentRewardConserved(before, candidate, command)) return false;
                double oldWeight = oldParticipants.GetDictOrEmpty("inventory").GetFloat("total_weight");
                double weight = newParticipants.GetDictOrEmpty("inventory").GetFloat("total_weight");
                double oldMass = 0, newMass = 0;
                foreach (object value in candidate.GetDictOrEmpty("registry").GetDictOrEmpty("instances").Values)
                {
                    GdDict row = (GdDict)value;
                    GdDict oldRow = before.GetDictOrEmpty("registry").GetDictOrEmpty("instances").GetDictOrEmpty(row.Get("instance_id"));
                    if (row.GetString("holder") == "player:player_local") newMass += row.GetFloat("mass");
                    if (oldRow.GetString("holder") == "player:player_local") oldMass += oldRow.GetFloat("mass");
                }
                // PrepareLive also admits its unchanged private transfer candidate before the session applies projected mass.
                if (weight != oldWeight && weight != oldWeight - oldMass + newMass) return false;
            }
            long revision = before.GetInt("revision");
            if (revision == long.MaxValue || candidate.GetInt("revision") != revision + 1 ||
                !V.VariantEquals(before.Get("machinery"), candidate.Get("machinery")) ||
                !V.VariantEquals(before.Get("receipts"), candidate.Get("receipts"))) return false;
            string movedId = command.GetString("instance_id"), swapId = command.GetString("swap_instance_id");
            string source = command.GetString("source_holder_id"), destination = command.GetString("destination_holder_id");
            bool swap = command.GetString("operation") == "swap";
            GdDict oldInstances = before.GetDictOrEmpty("registry").GetDictOrEmpty("instances"), newInstances = candidate.GetDictOrEmpty("registry").GetDictOrEmpty("instances");
            if (oldInstances.Count != newInstances.Count) return false;
            foreach (object key in oldInstances.Keys)
            {
                if (!(oldInstances[key] is GdDict oldRow) || !(newInstances.Get(key) is GdDict newRow)) return false;
                bool moved = (string)key == movedId || swap && (string)key == swapId;
                if (!moved) { if (!V.VariantEquals(oldRow, newRow)) return false; continue; }
                string expectedOld = (string)key == movedId ? source : destination;
                string expectedNew = (string)key == movedId ? destination : source;
                if (oldRow.GetString("holder") != expectedOld || newRow.GetString("holder") != expectedNew || !Incremented(oldRow, newRow)) return false;
                GdDict oldFixed = oldRow.DeepCopy(), newFixed = newRow.DeepCopy();
                oldFixed.Erase("holder"); oldFixed.Erase("revision"); newFixed.Erase("holder"); newFixed.Erase("revision");
                if (!V.VariantEquals(oldFixed, newFixed)) return false;
            }
            if (!oldInstances.Has(movedId) || swap && !oldInstances.Has(swapId)) return false;
            GdDict oldHolders = before.GetDictOrEmpty("holders"), newHolders = candidate.GetDictOrEmpty("holders");
            if (oldHolders.Count != newHolders.Count || !oldHolders.Has(source) || !oldHolders.Has(destination)) return false;
            foreach (object key in oldHolders.Keys)
            {
                if (!(oldHolders[key] is GdDict oldHolder) || !(newHolders.Get(key) is GdDict newHolder)) return false;
                if ((string)key != source && (string)key != destination) { if (!V.VariantEquals(oldHolder, newHolder)) return false; continue; }
                if (!Incremented(oldHolder, newHolder)) return false;
                GdDict oldFixed = oldHolder.DeepCopy(), newFixed = newHolder.DeepCopy(); oldFixed.Erase("revision"); newFixed.Erase("revision");
                if (!V.VariantEquals(oldFixed, newFixed)) return false;
            }
            return true;
        }

        static bool Incremented(GdDict before, GdDict after)
        {
            long revision = before.GetInt("revision");
            return revision != long.MaxValue && after.GetInt("revision") == revision + 1;
        }

        static bool ComponentRewardConserved(GdDict before, GdDict after, GdDict command)
        {
            GdDict a = before.GetDictOrEmpty("participating_state"), b = after.GetDictOrEmpty("participating_state");
            if (V.VariantEquals(a.Get("progression"), b.Get("progression")) && V.VariantEquals(a.Get("training"), b.Get("training"))) return true;
            GdDict work = after.GetDictOrEmpty("component_work");
            if (work.GetString("status") != "committed" || work.GetString("command_id") != command.GetString("command_id") || work.GetString("instance_id") != command.GetString("instance_id")) return false;
            var catalog = new WorkActionCatalog(); catalog.LoadDefault();
            string eventId = catalog.GetAction(work.GetString("action_id")).GetString("xp_event");
            if (eventId.Length == 0) return false;
            string commit = "component_transfer:" + command.GetString("command_id");
            GdDict record = null;
            foreach (object value in b.GetDictOrEmpty("training").GetArrayOrEmpty("log"))
                if (value is GdDict row && row.GetString("commit_id") == commit) record = row;
            var progression = new PlayerProgressionState(); var classes = ClassDefinition.LoadAll();
            if (!classes.TryGetValue(a.GetDictOrEmpty("progression").GetString("class_id"), out ClassDefinition definition)) return false;
            progression.Configure(definition, PlayerProgressionState.LoadSkillsCatalog());
            if (PaidCraftingState.IsDomainVersion(after.GetInt("schema_version")))
            {
                if (!PaidCraftRewardProof.CopyProgressionExact(progression, a.GetDictOrEmpty("progression"))) return false;
            }
            else progression.ApplySummary(a.GetDictOrEmpty("progression"));
            if (command.Get("xp_multipliers") is GdDict multipliers)
            { progression.XpMultipliers.Clear(); foreach (var entry in multipliers) progression.XpMultipliers[entry.Key] = entry.Value; }
            var emitter = new TrainingEventBus(); emitter.Configure(); if (!emitter.ApplySummary(a.GetDictOrEmpty("training"))) return false;
            emitter.EventFilter = (id, target) => record == null; emitter.SkillGate = skill => record == null || !record.GetBool("gated");
            GdDict generated = emitter.Emit(eventId, work.GetString("instance_id"), progression);
            var recorded = new TrainingEventBus(); recorded.Configure(); recorded.ApplySummary(a.GetDictOrEmpty("training"));
            if (generated != null) recorded.RecordApplied(generated, commit);
            GdDict expectedTraining = recorded.ToDict(); expectedTraining["xp_total"] = emitter.GetTotalXpDelivered(); expectedTraining["dropped"] = emitter.GetDroppedCount();
            return V.VariantEquals(progression.GetSummary(), b.Get("progression")) && V.VariantEquals(expectedTraining, b.Get("training"));
        }
    }
}
