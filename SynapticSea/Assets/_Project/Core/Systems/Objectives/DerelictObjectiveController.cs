// Ported from scripts/systems/derelict_objective_controller.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure-logic objective loop for a generated derelict. Composes <see cref="ObjectiveProgressState"/>
    /// (single-step objectives) and adds reach_goal / cleared semantics. Never touches the scene tree.
    /// Owned by a ShipInstance; its summary rides the per-ship slice.
    /// </summary>
    public class DerelictObjectiveController
    {
        public const string REACH_GOAL_ID = "obj_reach_goal";
        public const string STEP_ID = "done"; // single synthetic step per single-objective

        // Inline-initialized so the controller is always in a valid state with a non-null progress.
        public ObjectiveProgressState Progress = new ObjectiveProgressState();
        public long ReachGoalSequence = 0;
        public bool Cleared = false;

        public static DerelictObjectiveController Create() => new DerelictObjectiveController();

        /// <summary>True once the objective set has been registered (or restored).</summary>
        public bool IsConfigured() => ReachGoalSequence != 0 || !Progress.GetSummary().IsEmpty;

        /// <summary>
        /// Registers or reconciles the generated objective set. Reconciliation preserves restored completion IDs
        /// while migrating legacy placeholder step counts to the current authored contract.
        /// </summary>
        public void Configure(GdArray objectiveSpecs)
        {
            foreach (object specVariant in objectiveSpecs)
            {
                if (!(specVariant is GdDict spec))
                    continue;
                long sequence = V.I64(spec.Get("sequence", 0L));
                if (sequence <= 0)
                    continue;
                long requiredSteps = 1;
                object stepsVariant = spec.Get("steps", new GdArray());
                if (V.Str(spec.Get("kind", "single")) == "repair_junction" && stepsVariant is GdArray steps)
                    requiredSteps = Math.Max(1L, (long)steps.Count);
                Progress.ReconcileObjective(sequence, V.Str(spec.Get("type", "objective")), requiredSteps);
                if (V.Str(spec.Get("id", "")) == REACH_GOAL_ID)
                    ReachGoalSequence = sequence;
            }
        }

        /// <summary>
        /// Completes a single-step objective by sequence. Returns true if newly completed.
        /// Sets <see cref="Cleared"/> when the reach_goal sequence becomes complete.
        /// </summary>
        public bool Complete(long sequence, string stepId = STEP_ID)
        {
            if (Progress == null)
                return false;
            string resolvedStepId = !string.IsNullOrEmpty(stepId) ? stepId : STEP_ID;
            bool changed = Progress.CompleteStep(sequence, resolvedStepId);
            if (ReachGoalSequence != 0 && Progress.IsSequenceComplete(ReachGoalSequence))
                Cleared = true;
            return changed;
        }

        public GdDict GetStepProgress(long sequence) => Progress != null ? Progress.GetStepProgress(sequence) : new GdDict();

        public bool IsStepComplete(long sequence, string stepId)
        {
            if (Progress == null)
                return false;
            string resolvedStepId = !string.IsNullOrEmpty(stepId) ? stepId : STEP_ID;
            return Progress.GetStepProgress(sequence).GetArrayOrEmpty("completed_step_ids").Contains(resolvedStepId);
        }

        public bool IsObjectiveComplete(long sequence) => Progress != null && Progress.IsSequenceComplete(sequence);

        public bool IsCleared() => Cleared;

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "progress", Progress != null ? Progress.GetSummary() : new GdDict() },
                { "reach_goal_sequence", ReachGoalSequence },
                { "cleared", Cleared },
            };
        }

        public bool ApplySummary(object summary)
        {
            if (!(summary is GdDict dict) || dict.IsEmpty)
                return false;
            if (Progress == null)
                Progress = new ObjectiveProgressState();
            object prog = dict.Get("progress", new GdDict());
            if (prog is GdDict progDict && !progDict.IsEmpty)
                Progress.ApplySummary(progDict);
            ReachGoalSequence = V.I64(dict.Get("reach_goal_sequence", 0L));
            Cleared = V.Bool(dict.Get("cleared", false));
            return true;
        }
    }
}
