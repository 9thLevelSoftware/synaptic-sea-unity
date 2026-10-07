using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Unused detached job kernel. Live participating state/publication belongs to a later sole owner.</summary>
    public sealed class WorkTransactionState
    {
        readonly IWorkCommitPort _port;
        readonly WorkEligibility _eligibility = new WorkEligibility();
        bool _publishing;
        GdDict _summary = new GdDict
        {
            { "schema_version", 1L }, { "revision", 0L }, { "jobs", new GdDict() },
        };
        GdDict Jobs => _summary.GetDict("jobs");
        long Revision => _summary.GetInt("revision");

        public WorkTransactionState(IWorkCommitPort commitPort) { _port = commitPort; }

        public GdDict Begin(GdDict ticket, GdDict context)
        {
            if (_publishing) return Result(null, "", false, "publication_in_progress");
            if (!WorkKernelData.ValidTicket(ticket)) return Result(null, "", false, "invalid_ticket");
            string jobId = ticket.GetString("job_id");
            GdDict row = Jobs.GetDict(jobId);
            if (row != null && (!WorkKernelData.SameTicket(row, ticket) ||
                (ticket.GetFloat("progress_seconds") != 0 && ticket.GetFloat("progress_seconds") != row.GetFloat("progress_seconds"))))
                return Result(row, jobId, false, "job_conflict");
            if (row == null)
            {
                if (ticket.GetFloat("progress_seconds") != 0) return Result(null, jobId, false, "invalid_ticket");
                foreach (var existing in Jobs)
                {
                    GdDict pending = (GdDict)existing.Value;
                    if (pending.GetString("actor_id") == ticket.GetString("actor_id") &&
                        pending.GetString("prepared_transaction_id").Length > 0 && pending.GetString("status") != "committed")
                        return Result(null, jobId, false, "commit_pending");
                }
            }

            GdDict evidence = _eligibility.Evaluate(ticket, context);
            if (!evidence.GetBool("ok")) return Result(row, jobId, false, evidence.GetString("reason"));
            if (row != null)
            {
                if (row.GetString("status") == "committed") return Result(row, jobId, true, "already_committed");
                if (row.GetBool("resume_required") && !context.GetBool("resume_requested")) return Result(row, jobId, false, "resume_required");
                string desired = row.GetString("prepared_transaction_id").Length > 0 ? "pending_commit" :
                    row.GetFloat("progress_seconds") == row.GetFloat("duration_seconds") ? "ready" : "active";
                if (row.GetString("status") != desired || row.GetBool("resume_required"))
                {
                    if (!Capacity(1)) return Result(row, jobId, false, "revision_overflow");
                    row["status"] = desired; row["pause_reason"] = ""; row["resume_required"] = false; Bump();
                }
                return Result(row, jobId, true, "ok");
            }

            if (!Capacity(1)) return Result(null, jobId, false, "revision_overflow");
            row = WorkKernelData.TicketCopy(ticket);
            row["status"] = "active"; row["pause_reason"] = ""; row["resume_required"] = false;
            row["prepared_transaction_id"] = ""; row["committed_result"] = new GdDict();
            Jobs[jobId] = row; Bump();
            return Result(row, jobId, true, "ok");
        }

        public GdDict Advance(string jobId, double delta, GdDict context)
        {
            if (_publishing) return Result(null, jobId, false, "publication_in_progress");
            GdDict row = Find(jobId);
            if (!WorkKernelData.Finite(delta) || delta < 0) return Result(row, jobId, false, "invalid_delta");
            if (row == null) return Result(null, jobId, false, "job_not_found");
            if (delta == 0) return Result(row, jobId, true, "no_advance");
            if (row.GetBool("resume_required")) return Result(row, jobId, false, "resume_required");
            if (row.GetString("status") == "committed") return Result(row, jobId, true, "already_committed");
            if (row.GetString("prepared_transaction_id").Length > 0) return Result(row, jobId, false, "commit_pending");
            GdDict evidence = _eligibility.Evaluate(row, context);
            if (!evidence.GetBool("ok")) return Blocked(row, jobId, evidence.GetString("reason"));

            double progress = row.GetFloat("progress_seconds"), duration = row.GetFloat("duration_seconds"), remaining = duration - progress;
            if (remaining == 0)
            {
                if (row.GetString("status") != "ready")
                {
                    if (!Capacity(1)) return Result(row, jobId, false, "revision_overflow");
                    row["status"] = "ready"; row["pause_reason"] = ""; Bump();
                }
                return Result(row, jobId, true, "ready");
            }
            GdDict policy = row.GetDict("effort_policy");
            double speed = policy.GetFloat("base_speed");
            if (policy.GetString("speed_rule") == "ordinary_work")
            {
                double ratio = context.GetFloat("stamina") / context.GetFloat("max_stamina");
                speed *= Math.Max(0.35, Math.Min(1, 0.35 + 0.65 * ratio));
                speed *= context.GetFloat("wound_speed_mult");
            }
            double rawProgress = delta * speed, rawSum = progress + rawProgress;
            if (!WorkKernelData.Finite(speed) || speed <= 0 || !WorkKernelData.Finite(rawProgress) || !WorkKernelData.Finite(rawSum))
                return Result(row, jobId, false, "arithmetic_overflow");
            double increment = Math.Min(remaining, rawProgress);
            double used = rawProgress < remaining ? delta : remaining / speed;
            double eligible = policy.GetBool("clip_final_tick") ? used : delta;
            double debit = policy.GetString("stamina_rule") == "none" ? 0 : eligible * policy.GetFloat("stamina_per_eligible_second");
            double next = increment == remaining ? duration : progress + increment;
            if (!WorkKernelData.Finite(used) || !WorkKernelData.Finite(eligible) || !WorkKernelData.Finite(debit) ||
                !WorkKernelData.Finite(next) || used < 0 || eligible < 0 || debit < 0)
                return Result(row, jobId, false, "arithmetic_overflow");
            string status = next == duration ? "ready" : "active";
            if (next != progress || row.GetString("status") != status)
            {
                if (!Capacity(1)) return Result(row, jobId, false, "revision_overflow");
                row["progress_seconds"] = next; row["status"] = status; row["pause_reason"] = ""; Bump();
            }
            GdDict result = Result(row, jobId, true, "ok");
            result["eligible_delta"] = eligible; result["progress_delta"] = increment; result["stamina_debit"] = debit;
            return result;
        }

        public GdDict Commit(string jobId, GdDict context)
        {
            if (_publishing) return Result(null, jobId, false, "publication_in_progress");
            GdDict row = Find(jobId);
            if (row == null) return Result(null, jobId, false, "job_not_found");
            if (row.GetString("status") == "committed") return row.GetDict("committed_result").DeepCopy();
            if (row.GetBool("resume_required")) return Result(row, jobId, false, "resume_required");
            if (row.GetFloat("progress_seconds") != row.GetFloat("duration_seconds")) return Result(row, jobId, false, "not_ready");
            if (_port == null) return Result(row, jobId, false, "commit_port_unbound");
            GdDict evidence = _eligibility.Evaluate(row, context);
            if (!evidence.GetBool("ok")) return Blocked(row, jobId, evidence.GetString("reason"));
            bool prepared = row.GetString("prepared_transaction_id").Length > 0;
            // Reserve the binding and outcome revisions before any owner can publish an effect.
            if (!Capacity(prepared ? 1 : 2)) return Result(row, jobId, false, "revision_overflow");
            string commandId = row.GetDict("completion_command").GetString("command_id");
            _publishing = true;
            try
            {
                if (!prepared)
                {
                    GdDict preparation;
                    try { preparation = _port.Prepare(row.GetDict("completion_command").DeepCopy()); }
                    catch (Exception) { return Result(row, jobId, false, "port_prepare_failed"); }
                    if (preparation == null || !WorkKernelData.SafeSnapshot(preparation) || preparation.Get("ok") is not bool ok)
                        return Result(row, jobId, false, "invalid_port_preparation");
                    if (!ok) return Result(row, jobId, false, "port_prepare_rejected");
                    if (preparation.Get("command_id") is not string command || command != commandId ||
                        !WorkKernelData.Text(preparation, "transaction_id")) return Result(row, jobId, false, "invalid_port_preparation");
                    row["prepared_transaction_id"] = preparation.GetString("transaction_id");
                    row["status"] = "pending_commit"; row["pause_reason"] = ""; Bump();
                }
                string transactionId = row.GetString("prepared_transaction_id");
                // Prepare may alter the still-supplied evidence through an owner callback.
                // Keep its bound ID on refusal and revalidate immediately before publication.
                evidence = _eligibility.Evaluate(row, context);
                if (!evidence.GetBool("ok")) return Blocked(row, jobId, evidence.GetString("reason"));
                GdDict receipt;
                try { receipt = _port.Commit(transactionId); }
                catch (Exception) { return PendingOutcome(row, jobId, "commit_unknown"); }
                if (receipt == null || !WorkKernelData.SafeSnapshot(receipt) || receipt.Get("transaction_id") is not string transaction ||
                    transaction != transactionId || receipt.Get("committed") is not bool committed)
                    return PendingOutcome(row, jobId, "commit_unknown");
                if (!committed)
                {
                    if (receipt.Has("command_id") && (receipt.Get("command_id") is not string rejectedCommand || rejectedCommand != commandId))
                        return PendingOutcome(row, jobId, "commit_unknown");
                    return PendingOutcome(row, jobId, "port_commit_rejected");
                }
                if (!WorkKernelData.ConfirmedReceipt(receipt, transactionId, commandId)) return PendingOutcome(row, jobId, "commit_unknown");
                GdDict result = receipt.DeepCopy();
                // The port owns its receipt payload; kernel identity/status cannot be rewritten by response extensions.
                result.Merge(Result(row, jobId, true, "ok"), true);
                result["status"] = "committed"; result["committed"] = true; result["transaction_id"] = transactionId;
                result["commit_id"] = receipt.GetString("commit_id"); result["result"] = receipt.GetDict("result").DeepCopy();
                row["status"] = "committed"; row["pause_reason"] = ""; row["resume_required"] = false;
                row["committed_result"] = result.DeepCopy(); Bump();
                return result;
            }
            finally { _publishing = false; }
        }

        public GdDict Interrupt(string jobId, string reason)
        {
            if (_publishing) return Result(null, jobId, false, "publication_in_progress");
            GdDict row = Find(jobId);
            if (row == null) return Result(null, jobId, false, "job_not_found");
            if (!WorkKernelData.Nonblank(reason)) return Result(row, jobId, false, "invalid_reason");
            if (row.GetString("status") == "committed") return Result(row, jobId, true, "already_committed");
            if (row.GetString("prepared_transaction_id").Length > 0) return Result(row, jobId, false, "commit_pending");
            if (row.GetString("status") != "interrupted" || row.GetString("pause_reason") != reason || !row.GetBool("resume_required"))
            {
                if (!Capacity(1)) return Result(row, jobId, false, "revision_overflow");
                row["status"] = "interrupted"; row["pause_reason"] = reason; row["resume_required"] = true; Bump();
            }
            return Result(row, jobId, true, reason);
        }

        public GdDict GetSummary() => _summary.DeepCopy();

        public bool ApplySummary(GdDict summary)
        {
            if (_publishing || summary == null || !WorkKernelData.SafeSnapshot(summary) ||
                summary.Get("schema_version") is not long schema || schema != 1 ||
                summary.Get("revision") is not long revision || revision < Revision || summary.Get("jobs") is not GdDict jobs) return false;
            foreach (var entry in jobs)
                if (entry.Key is not string id || !WorkKernelData.Nonblank(id) || entry.Value is not GdDict row ||
                    !WorkKernelData.ValidRow(row) || row.GetString("job_id") != id) return false;
            foreach (var entry in Jobs)
            {
                GdDict prior = (GdDict)entry.Value;
                if (prior.GetString("status") == "committed")
                {
                    if (!jobs.TryGetValue(entry.Key, out object replacement) || !WorkKernelData.ExactEquals(prior, replacement)) return false;
                }
                else if (prior.GetString("prepared_transaction_id").Length > 0)
                {
                    GdDict replacement = jobs.GetDict(entry.Key);
                    if (replacement == null || !WorkKernelData.SameTicket(prior, replacement) ||
                        !WorkKernelData.ExactEquals(prior.Get("progress_seconds"), replacement.Get("progress_seconds")) ||
                        prior.GetString("prepared_transaction_id") != replacement.GetString("prepared_transaction_id")) return false;
                }
            }
            // Whole-input validation precedes every external copy and the single replacement.
            GdDict candidate = summary.DeepCopy();
            foreach (var entry in candidate.GetDict("jobs"))
            {
                GdDict row = (GdDict)entry.Value;
                if (row.GetString("status") == "committed") continue;
                row["status"] = "paused_restore"; row["pause_reason"] = "resume_required"; row["resume_required"] = true;
            }
            _summary = candidate;
            return true;
        }

        GdDict Find(string jobId) => WorkKernelData.Nonblank(jobId) ? Jobs.GetDict(jobId) : null;
        bool Capacity(int increments) => Revision <= long.MaxValue - increments;
        void Bump() => _summary["revision"] = Revision + 1;

        GdDict Blocked(GdDict row, string jobId, string reason)
        {
            // A released pending hold simply waits for input, retaining the unknown publication identity.
            if (reason == "hold_released" && row.GetString("prepared_transaction_id").Length > 0) return Result(row, jobId, false, reason);
            string status = reason == "hold_released" ? "paused_hold" :
                reason == "power" && row.GetDict("effort_policy").GetString("power_loss_behavior") == "pause" ? "paused_power" : "interrupted";
            bool resume = status != "paused_hold";
            if (row.GetString("status") != status || row.GetString("pause_reason") != reason || row.GetBool("resume_required") != resume)
            {
                if (!Capacity(1)) return Result(row, jobId, false, "revision_overflow");
                row["status"] = status; row["pause_reason"] = reason; row["resume_required"] = resume; Bump();
            }
            return Result(row, jobId, false, reason);
        }

        GdDict PendingOutcome(GdDict row, string jobId, string reason)
        {
            row["status"] = "pending_commit"; row["pause_reason"] = reason; row["resume_required"] = false; Bump();
            return Result(row, jobId, false, reason);
        }

        static GdDict Result(GdDict row, string jobId, bool ok, string reason) => new GdDict
        {
            { "ok", ok }, { "reason", reason }, { "job_id", jobId ?? "" }, { "status", row?.GetString("status") ?? "" },
            { "owner_revision", row?.GetInt("owner_revision") ?? 0L }, { "target_revision", row?.GetInt("target_revision") ?? 0L },
            { "missing_requirements", ok ? new GdArray() : GdArray.Of(reason) },
            { "progress_seconds", row?.GetFloat("progress_seconds") ?? 0.0 },
            { "eligible_delta", 0.0 }, { "progress_delta", 0.0 }, { "stamina_debit", 0.0 },
            { "committed", false }, { "transaction_id", row?.GetString("prepared_transaction_id") ?? "" },
            { "commit_id", "" }, { "result", new GdDict() },
        };
    }
}
