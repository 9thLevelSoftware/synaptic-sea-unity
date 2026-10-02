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

        public DomainTransactionCoordinator(GdDict initialSummary, Action<string> stageHook = null, Action<GdDict> notification = null)
        {
            if (!DomainBundle.TryCreate(initialSummary, out _current, out string reason)) throw new ArgumentException(reason, nameof(initialSummary));
            _stageHook = stageHook; _notification = notification;
        }

        public GdDict GetSummary() => _current.GetSummary();

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
                    !DomainBundle.TryCreate(candidate, out _, out _) || !ConservedTransition(before, candidate, ownedCommand) || !MatchesEffect(result, ownedCommand))
                    return Failure("invalid_preparation", transactionId, commandId);
                _pending[transactionId] = new Pending(ownedCommand, candidate, result, _current.Revision);
                _pendingCommands.Add(commandId);
                return prepared.DeepCopy();
            }
            finally { _busy = false; }
        }

        public GdDict Commit(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId)) return Failure("missing_transaction");
            // A receipt read has no mutation or notification, including a same-ID read from a notification callback.
            GdDict current = _current.GetSummary();
            if (current.GetDictOrEmpty("receipts").Get(transactionId) is GdDict committedReceipt)
                return _outcomes.TryGetValue(transactionId, out GdDict prior) ? prior.DeepCopy() : CommittedResult(committedReceipt);
            if (_busy) return Failure("reentrant_mutation", transactionId);
            if (!_pending.TryGetValue(transactionId, out Pending pending)) return Failure("missing_transaction", transactionId);
            string commandId = pending.Command.GetString("command_id");
            GdDict publishedResult = null;
            bool published = false;
            _busy = true;
            try
            {
                if (_current.Revision != pending.ExpectedRevision) return Failure("stale_domain", transactionId, commandId);
                if (!DomainBundle.TryCreate(pending.Candidate, out _, out _) || !ConservedTransition(current, pending.Candidate, pending.Command) || !MatchesEffect(pending.Result, pending.Command))
                    return Failure("invalid_preparation", transactionId, commandId);

                // Staging remains private. Readers at every hook see _current, never any per-field candidate.
                GdDict staged = new GdDict { { "schema_version", 1L }, { "revision", pending.Candidate.Get("revision") },
                    { "registry", pending.Candidate.GetDictOrEmpty("registry").DeepCopy() } };
                _stageHook?.Invoke("registry");
                staged["holders"] = pending.Candidate.GetDictOrEmpty("holders").DeepCopy(); _stageHook?.Invoke("holders");
                staged["machinery"] = pending.Candidate.GetDictOrEmpty("machinery").DeepCopy(); _stageHook?.Invoke("machinery");
                GdDict receipts = pending.Candidate.GetDictOrEmpty("receipts").DeepCopy();
                GdDict receipt = new GdDict { { "schema_version", 1L }, { "transaction_id", transactionId }, { "command_id", commandId },
                    { "revision", pending.Candidate.Get("revision") }, { "result", pending.Result.DeepCopy() } };
                receipts[transactionId] = receipt; staged["receipts"] = receipts; _stageHook?.Invoke("receipt");
                _stageHook?.Invoke("validation");
                if (!DomainBundle.TryCreate(staged, out DomainBundle complete, out string reason)) return Failure("invalid_candidate:" + reason, transactionId, commandId);
                publishedResult = CommittedResult(receipt);
                _stageHook?.Invoke("publication");
                _current = complete; // The sole publication assignment: all participating state and its receipt.
                published = true;

                try { _notification?.Invoke(publishedResult.DeepCopy()); }
                catch (Exception e)
                {
                    publishedResult["presentation_failed"] = true; publishedResult["presentation_error"] = Describe(e);
                }
                _outcomes[transactionId] = publishedResult.DeepCopy();
                return publishedResult.DeepCopy();
            }
            catch (Exception e)
            {
                // Once the receipt owns the effect, no subsequent diagnostic failure can report an uncommitted result.
                if (published)
                {
                    publishedResult["presentation_failed"] = true; publishedResult["presentation_error"] = Describe(e);
                    _outcomes[transactionId] = publishedResult.DeepCopy();
                    return publishedResult.DeepCopy();
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

        static GdDict CommittedResult(GdDict receipt)
            => new GdDict { { "ok", true }, { "committed", true }, { "reason", "committed" },
                { "transaction_id", receipt.Get("transaction_id") }, { "command_id", receipt.Get("command_id") },
                { "revision", receipt.Get("revision") }, { "result", receipt.GetDictOrEmpty("result").DeepCopy() },
                { "presentation_failed", false }, { "presentation_error", "" } };

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
    }
}
