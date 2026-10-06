using System;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        /// <summary>Manual study is part of the base game. The paid-domain study proofs (schema 4/5) stay behind this flag until paid crafting is deleted.</summary>
        public bool ManualStudyEnabled => true;
        bool PaidManualStudyEnabled => Deps.EnableManualStudy && PaidCraftingEnabled;
        readonly ManualStudyState _study = new ManualStudyState();
        Vec3 _studyPosition;
        double _studyHealth;
        public GdDict GetManualStudyState() => _study.ToDict();
        public bool ManualStudyRunning => _study.IsRunning;
        bool OtherManualWork()
        {
            if (WorkActionDriver?.IsWorking() == true || RepairPoints.Any(p => p.Channeling) || BreachSealPoints.Any(p => p.Channeling) ||
                FireSuppressionPoints.Any(p => p.Channeling) || DockBarriers.Any(p => p.Channeling)) return true;
            GdDict domain = _componentDomain?.GetSummary() ?? new GdDict();
            return domain.GetDictOrEmpty("component_work").GetString("status") == "active" ||
                PaidState(domain).GetDictOrEmpty("jobs").Values.OfType<GdDict>().Any(j => j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
        }
        string StudyGate(string book)
        {
            if (ComponentTerminalPending || SliceComplete || !PlayableStarted || VitalsState?.IsIncapacitated() == true || !HasPlayer) return "actor_unavailable";
            if (!PlayerProgression.GetBooksCatalog().Has(book) || InventoryState.GetDefinition(book).GetString("category") != "book") return "manual_missing";
            if (InventoryState.GetQuantity(book) < 1) return "manual_not_carried";
            if (PlayerProgression.HasReadBook(book)) return "already_studied";
            if (PlayerMoving) return "moving";
            if (OtherManualWork()) return "work_busy";
            return "ready";
        }
        static GdDict StudyResult(bool ok, string reason) => new GdDict { { "committed", ok }, { "reason", reason } };
        public GdDict RequestManualStudy(string book)
        {
            if (ComponentGenerationRestoreInProgress) return StudyResult(false, "restore_in_progress");
            string gate = StudyGate(book);
            if (gate != "ready") return StudyResult(false, gate);
            if (!_study.IsEmpty && _study.Status != ManualStudyState.Completed && _study.BookId != book) return StudyResult(false, "other_manual_pending");
            if (_study.IsRunning) return StudyResult(false, "study_busy");
            _study.Start(book);
            _studyPosition = PlayerPos; _studyHealth = VitalsState.Health;
            RefreshStudyHud();
            return StudyResult(true, "started");
        }
        public GdDict PauseManualStudy(string reason = "paused")
        {
            if (!_study.IsRunning) return StudyResult(false, "study_not_running");
            _study.Pause(reason);
            RefreshStudyHud();
            return StudyResult(true, reason);
        }
        void TickManualStudy(double delta)
        {
            if (!_study.IsRunning || delta <= 0 || double.IsNaN(delta) || double.IsInfinity(delta)) return;
            string book = _study.BookId, gate = StudyGate(book);
            if (PlayerMoving || (PlayerPos - _studyPosition).LengthSquared() > .0001) gate = "moving";
            if (VitalsState.Health < _studyHealth) gate = "damage";
            if (gate != "ready") { PauseManualStudy(gate); return; }
            if (_study.Advance(delta)) CompleteManualStudy(book);
            RefreshStudyHud();
        }
        void CompleteManualStudy(string book)
        {
            GdDict definition = PlayerProgression.GetBooksCatalog().GetDictOrEmpty(book);
            long xp = definition.GetInt("book_xp");
            string skill = definition.GetString("target_skill");
            if (!PlayerProgression.GrantXpFromBook(book)) { _study.Clear(); return; }
            if (xp > 0 && TrainingEventBus != null)
            {
                var record = new GdDict { { "event_id", "study_manual" }, { "target_id", book }, { "skill_id", skill }, { "base_xp", xp },
                    { "category", PlayerProgressionState.LoadSkillsCatalog().GetDictOrEmpty(skill).GetString("category") },
                    { "is_cross_training", false }, { "sequence", TrainingEventBus.GetEventCount() }, { "gated", false } };
                TrainingEventBus.RecordApplied(record, "manual_study:complete:" + RunId + ":" + book);
            }
            RecipeKnowledge?.LearnFromBook(book, RecipeCatalog());
            _study.Complete();
        }
        public string ViewManual(string book)
        {
            GdDict definition = PlayerProgression?.GetBooksCatalog().GetDictOrEmpty(book) ?? new GdDict();
            if (definition.IsEmpty) return "Manual unavailable";
            string seconds = ManualStudyState.RequiredSeconds.ToString("0");
            return book + "\n" + definition.GetString("target_skill") + " · " + definition.GetInt("book_xp") + " base XP\n" +
                (PlayerProgression.HasReadBook(book) ? "Already studied" : _study.BookId == book && !_study.IsEmpty
                    ? "Study " + _study.ProgressSeconds.ToString("0.0") + " / " + seconds + " s · " + _study.Status + " " + _study.Reason
                    : seconds + " stationary seconds · manual retained");
        }
        void RefreshStudyHud()
        {
            if (_study.IsEmpty) return;
            Events.RaiseWorkActionHudState(new GdDict { { "action_id", "study_manual" }, { "target_id", _study.BookId }, { "verb", "Study" },
                { "progress", _study.ProgressSeconds / ManualStudyState.RequiredSeconds }, { "status", _study.IsRunning ? "active" : _study.Status },
                { "block_reason", _study.Reason }, { "noise", 0.0 } });
        }
    }
}
