using System;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        public bool ManualStudyEnabled => Deps.EnableManualStudy && PaidCraftingEnabled;
        bool _studyConsent;
        Vec3 _studyPosition;
        double _studyHealth;
        public GdDict GetManualStudyState() => ManualStudyEnabled && _componentDomain?.SchemaVersion == 4
            ? ManualStudyState.State(_componentDomain.GetSummary()).DeepCopy() : new GdDict();
        public bool ManualStudyRunning => GetManualStudyState().GetDictOrEmpty("job").GetString("status") == "running";
        bool OtherManualWork(GdDict domain) => WorkActionDriver?.IsWorking() == true ||
            RepairPoints.Any(p => p.Channeling) || BreachSealPoints.Any(p => p.Channeling) || FireSuppressionPoints.Any(p => p.Channeling) || DockBarriers.Any(p => p.Channeling) ||
            domain.GetDictOrEmpty("component_work").GetString("status") == "active" ||
            PaidState(domain).GetDictOrEmpty("jobs").Values.OfType<GdDict>().Any(j => j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
        string StudyGate(GdDict domain, string book)
        {
            if (!ManualStudyEnabled || domain.GetInt("schema_version") != 4) return "manual_study_inactive";
            if (DomainPublicationInProgress) return "reentrant_mutation";
            if (ComponentTerminalPending || SliceComplete || !PlayableStarted || VitalsState?.IsIncapacitated() == true || !HasPlayer) return "actor_unavailable";
            if (!PlayerProgression.GetBooksCatalog().Has(book) || InventoryState.GetDefinition(book).GetString("category") != "book") return "manual_missing";
            if (InventoryState.GetQuantity(book) < 1) return "manual_not_carried";
            if (PlayerProgression.HasReadBook(book)) return "already_studied";
            if (PlayerMoving) return "moving";
            if (OtherManualWork(domain)) return "work_busy";
            return "ready";
        }
        GdDict StudyCommand(GdDict domain, string action, string book, double delta = 0, string reason = "")
            => new GdDict { { "command_id", "study:" + RunId + ":" + checked(domain.GetInt("command_sequence") + 1) }, { "action", action },
                { "run_id", RunId }, { "actor_id", PLAYER_LOCAL_ID }, { "book_id", book }, { "delta_seconds", delta }, { "reason", reason } };
        GdDict ExecuteStudy(GdDict command, Func<GdDict, GdDict> stage, bool requireEligibility)
        {
            if (DomainPublicationInProgress || ComponentTerminalPending || SliceComplete) return PaidFailure("study_unavailable");
            GdDict before = _componentDomain.GetSummary(); Vec3 position = PlayerPos; double health = VitalsState?.Health ?? 0;
            _paidPublicationContext = PaidContextFingerprint(); _componentMutating = true;
            _studyPublicationGate = () => {
                if (PlayerPos != position || VitalsState?.Health != health || command.GetString("run_id") != RunId ||
                    requireEligibility && StudyGateForPublication(before, command.GetString("book_id")) != "ready") throw new InvalidOperationException("stale_study_context");
            };
            try
            {
                GdDict prepared = _componentDomain.PrepareStudy(command, candidate => {
                    GdDict effect = stage(candidate);
                    candidate["command_sequence"] = checked(candidate.GetInt("command_sequence") + 1); SetPaidProjections(candidate);
                    return effect;
                });
                if (!prepared.GetBool("ok") || prepared.GetBool("committed")) return prepared;
                return _componentDomain.Commit(prepared.GetString("transaction_id"));
            }
            finally { _componentMutating = false; _paidPublicationContext = null; _studyPublicationGate = null; }
        }
        Action _studyPublicationGate;
        string StudyGateForPublication(GdDict before, string book)
        {
            // Publication itself owns the mutation guard; all independent actor/copy/work gates remain live.
            if (ComponentTerminalPending || SliceComplete || !PlayableStarted || VitalsState?.IsIncapacitated() == true || !HasPlayer) return "actor_unavailable";
            if (InventoryState.GetQuantity(book) < 1 || PlayerProgression.HasReadBook(book) || PlayerMoving || OtherManualWork(before)) return "stale_context";
            return "ready";
        }
        public GdDict RequestManualStudy(string book)
        {
            if (ComponentGenerationRestoreInProgress) return PaidFailure("restore_in_progress");
            if (!ManualStudyEnabled || !EnsurePaidOwner()) return PaidFailure("manual_study_inactive");
            GdDict domain = CapturePaidCraftingDomain(); string gate = StudyGate(domain, book);
            if (gate != "ready") return PaidFailure(gate);
            GdDict old = ManualStudyState.State(domain).GetDictOrEmpty("job");
            if (!old.IsEmpty && old.GetString("status") != "completed" && old.GetString("book_id") != book) return PaidFailure("other_manual_pending");
            if (old.GetString("status") == "running") return PaidFailure("study_busy");
            GdDict command = StudyCommand(domain, "start", book);
            GdDict result = ExecuteStudy(command, candidate => {
                GdDict state = ManualStudyState.State(candidate), prior = state.GetDictOrEmpty("job").DeepCopy();
                GdDict job = new GdDict { { "book_id", book }, { "progress_seconds", prior.GetString("book_id") == book && prior.GetString("status") != "completed" ? prior.GetFloat("progress_seconds") : 0.0 },
                    { "status", "running" }, { "resume_required", false }, { "reason", "" } };
                state["job"] = job;
                return new GdDict { { "operation", "study_start" }, { "reason", "started" }, { "book_id", book }, { "job_before", prior }, { "job_after", job.DeepCopy() } };
            }, true);
            if (result.GetBool("committed")) { _studyConsent = true; _studyPosition = PlayerPos; _studyHealth = VitalsState.Health; RefreshStudyHud(); }
            return result;
        }
        public GdDict PauseManualStudy(string reason = "paused")
        {
            _studyConsent = false;
            if (ComponentGenerationRestoreInProgress || !ManualStudyEnabled || _componentDomain?.SchemaVersion != 4) return PaidFailure("manual_study_inactive");
            GdDict domain = CapturePaidCraftingDomain(), job = ManualStudyState.State(domain).GetDictOrEmpty("job");
            if (job.IsEmpty || job.GetString("status") != "running") return PaidFailure("study_not_running");
            string book = job.GetString("book_id");
            GdDict result = ExecuteStudy(StudyCommand(domain, "pause", book, reason: reason), candidate => {
                GdDict prior = ManualStudyState.State(candidate).GetDictOrEmpty("job").DeepCopy(), next = prior.DeepCopy();
                next["status"] = "paused"; next["resume_required"] = true; next["reason"] = reason;
                ManualStudyState.State(candidate)["job"] = next;
                return new GdDict { { "operation", "study_pause" }, { "reason", reason }, { "book_id", book }, { "job_before", prior }, { "job_after", next.DeepCopy() } };
            }, false);
            RefreshStudyHud(); return result;
        }
        void TickManualStudy(double delta)
        {
            if (!ManualStudyEnabled || !ManualStudyRunning || delta <= 0 || double.IsNaN(delta) || double.IsInfinity(delta)) return;
            GdDict domain = CapturePaidCraftingDomain(), job = ManualStudyState.State(domain).GetDictOrEmpty("job"); string book = job.GetString("book_id");
            string gate = StudyGate(domain, book);
            if (!_studyConsent) gate = "explicit_resume_required";
            if (PlayerMoving || (PlayerPos - _studyPosition).LengthSquared() > .0001) gate = "moving";
            if (VitalsState.Health < _studyHealth) gate = "damage";
            if (gate != "ready") { PauseManualStudy(gate); return; }
            delta = Math.Min(delta, ManualStudyState.RequiredSeconds - job.GetFloat("progress_seconds"));
            if (delta > 0)
            {
                GdDict command = StudyCommand(domain, "progress", book, delta);
                GdDict result = ExecuteStudy(command, candidate => {
                    GdDict prior = ManualStudyState.State(candidate).GetDictOrEmpty("job").DeepCopy(), next = prior.DeepCopy();
                    next["progress_seconds"] = Math.Min(ManualStudyState.RequiredSeconds, prior.GetFloat("progress_seconds") + command.GetFloat("delta_seconds"));
                    ManualStudyState.State(candidate)["job"] = next;
                    return new GdDict { { "operation", "study_progress" }, { "reason", "advanced" }, { "book_id", book }, { "job_before", prior }, { "job_after", next.DeepCopy() },
                        { "origin_receipt_id", ManualStudyState.OriginReceipt(candidate, book) }, { "eligible_steps", ManualStudyState.LatestProgress(candidate, book).GetDictOrEmpty("result").GetInt("eligible_steps") + 1 } };
                }, true);
                if (!result.GetBool("committed")) { PauseManualStudy(result.GetString("reason")); return; }
            }
            domain = CapturePaidCraftingDomain(); job = ManualStudyState.State(domain).GetDictOrEmpty("job");
            if (job.GetFloat("progress_seconds") == ManualStudyState.RequiredSeconds)
            {
                GdDict result = ExecuteStudy(StudyCommand(domain, "complete", book), candidate => {
                    GdDict state = ManualStudyState.State(candidate); string commit = ManualStudyState.CompletionId(state, book);
                    GdDict effect = ManualStudyState.Reward(candidate, book, commit);
                    state.GetDictOrEmpty("job")["status"] = "completed"; state.GetDictOrEmpty("job")["resume_required"] = false;
                    state.GetDictOrEmpty("completed")[book] = commit;
                    return effect;
                }, true);
                if (result.GetBool("committed")) _studyConsent = false;
            }
            RefreshStudyHud();
        }
        public string ViewManual(string book)
        {
            GdDict definition = PlayerProgression?.GetBooksCatalog().GetDictOrEmpty(book) ?? new GdDict();
            if (!ManualStudyEnabled || definition.IsEmpty) return "Manual unavailable";
            GdDict job = GetManualStudyState().GetDictOrEmpty("job");
            return book + "\n" + definition.GetString("target_skill") + " · " + definition.GetInt("book_xp") + " base XP\n" +
                (PlayerProgression.HasReadBook(book) ? "Already studied" : job.GetString("book_id") == book ? "Study " + job.GetFloat("progress_seconds").ToString("0.0") + " / 30 s · " + job.GetString("status") + " " + job.GetString("reason") : "30 stationary seconds · manual retained");
        }
        void RefreshStudyHud()
        {
            GdDict job = GetManualStudyState().GetDictOrEmpty("job"); if (job.IsEmpty) return;
            Events.RaiseWorkActionHudState(new GdDict { { "action_id", "study_manual" }, { "target_id", job.GetString("book_id") }, { "verb", "Study" },
                { "progress", job.GetFloat("progress_seconds") / ManualStudyState.RequiredSeconds }, { "status", job.GetString("status") == "running" ? "active" : job.GetString("status") },
                { "block_reason", job.GetString("reason") }, { "noise", 0.0 } });
        }
    }
}
