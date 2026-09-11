// Ported from scripts/systems/objective_progress_state.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Runtime model for multi-step objective progress on the main playable slice. A sequence is registered
    /// once with an objective type and required step count; steps complete by id (idempotent).
    /// </summary>
    public class ObjectiveProgressState
    {
        readonly GdDict _objectives = new GdDict(); // sequence (int) -> record

        public void RegisterObjective(long sequence, string objectiveType, long requiredSteps)
        {
            if (sequence <= 0) return;
            if (requiredSteps < 1) requiredSteps = 1;
            _objectives[sequence] = new GdDict
            {
                { "objective_type", objectiveType },
                { "required_steps", requiredSteps },
                { "completed_steps", 0L },
                { "completed_step_ids", new GdArray() },
                { "complete", false },
                // REQ-014 junction_calibrator: per-sequence flag so save/load restores the reduced step count.
                { "calibrator_applied", false },
            };
        }

        /// <summary>
        /// Reconciles restored progress with the current authored contract, preserving completed ids and
        /// calibrator use while recomputing the requirement and completion state.
        /// </summary>
        public void ReconcileObjective(long sequence, string objectiveType, long authoredSteps)
        {
            if (sequence <= 0) return;
            long requiredSteps = Math.Max(1L, authoredSteps);
            if (!_objectives.Has(sequence))
            {
                RegisterObjective(sequence, objectiveType, requiredSteps);
                return;
            }
            var objective = (GdDict)_objectives[sequence];
            bool calibratorApplied = V.Bool(objective.Get("calibrator_applied", false));
            if (calibratorApplied) requiredSteps = Math.Max(1L, requiredSteps - 1);
            GdArray completedIds = ((GdArray)objective.Get("completed_step_ids", new GdArray())).ShallowCopy();
            objective["objective_type"] = objectiveType;
            objective["required_steps"] = requiredSteps;
            objective["completed_step_ids"] = completedIds;
            objective["completed_steps"] = completedIds.Count;
            objective["complete"] = completedIds.Count >= requiredSteps;
            objective["calibrator_applied"] = calibratorApplied;
            _objectives[sequence] = objective;
        }

        /// <summary>
        /// REQ-014: reduces required_steps by one (clamped at 1) and marks calibrator_applied. Returns false and
        /// leaves state untouched when unregistered, complete, one-step, or already applied.
        /// </summary>
        public bool ApplyJunctionCalibrator(long sequence)
        {
            if (sequence <= 0) return false;
            if (!_objectives.Has(sequence)) return false;
            var objective = (GdDict)_objectives[sequence];
            if (V.Bool(objective.Get("complete", false))) return false;
            if (V.Bool(objective.Get("calibrator_applied", false))) return false;
            long requiredSteps = V.I64(objective.Get("required_steps", 1L));
            if (requiredSteps <= 1) return false;
            objective["required_steps"] = requiredSteps - 1;
            objective["calibrator_applied"] = true;
            if (V.I64(objective.Get("completed_steps", 0L)) >= V.I64(objective["required_steps"]))
                objective["complete"] = true;
            _objectives[sequence] = objective;
            return true;
        }

        public bool HasCalibratorApplied(long sequence)
        {
            if (!_objectives.Has(sequence)) return false;
            return V.Bool(((GdDict)_objectives[sequence]).Get("calibrator_applied", false));
        }

        public bool CompleteStep(long sequence, string stepId)
        {
            if (sequence <= 0) return false;
            if (!_objectives.Has(sequence)) return false;
            var objective = (GdDict)_objectives[sequence];
            if (V.Bool(objective.Get("complete", false))) return false;
            GdArray completedIds = (GdArray)objective.Get("completed_step_ids", new GdArray());
            if (completedIds.Contains(stepId)) return false;
            completedIds.Add(stepId);
            objective["completed_step_ids"] = completedIds;
            objective["completed_steps"] = completedIds.Count;
            if (V.I64(objective["completed_steps"]) >= V.I64(objective.Get("required_steps", 1L)))
                objective["complete"] = true;
            _objectives[sequence] = objective;
            return true;
        }

        public bool IsSequenceComplete(long sequence)
        {
            if (!_objectives.Has(sequence)) return false;
            return V.Bool(((GdDict)_objectives[sequence]).Get("complete", false));
        }

        public GdDict GetStepProgress(long sequence)
        {
            if (!_objectives.Has(sequence))
            {
                return new GdDict
                {
                    { "required_steps", 0L }, { "completed_steps", 0L }, { "complete", false }, { "completed_step_ids", new GdArray() },
                };
            }
            var objective = (GdDict)_objectives[sequence];
            return new GdDict
            {
                { "required_steps", V.I64(objective.Get("required_steps", 1L)) },
                { "completed_steps", V.I64(objective.Get("completed_steps", 0L)) },
                { "complete", V.Bool(objective.Get("complete", false)) },
                { "completed_step_ids", ((GdArray)objective.Get("completed_step_ids", new GdArray())).ShallowCopy() },
                // REQ-014: surface the calibrator-applied flag for the HUD and save/load.
                { "calibrator_applied", V.Bool(objective.Get("calibrator_applied", false)) },
            };
        }

        public string GetSequenceObjectiveType(long sequence)
        {
            if (!_objectives.Has(sequence)) return "";
            return V.Str(((GdDict)_objectives[sequence]).Get("objective_type", ""));
        }

        /// <summary>Keyed by sequence number (int keys; JSON round-trips turn them into strings).</summary>
        public GdDict GetSummary()
        {
            var summary = new GdDict();
            foreach (var sequence in _objectives.Keys)
            {
                var objective = (GdDict)_objectives[sequence];
                summary[sequence] = new GdDict
                {
                    { "objective_type", V.Str(objective.Get("objective_type", "")) },
                    { "required_steps", V.I64(objective.Get("required_steps", 1L)) },
                    { "completed_steps", V.I64(objective.Get("completed_steps", 0L)) },
                    { "completed_step_ids", ((GdArray)objective.Get("completed_step_ids", new GdArray())).ShallowCopy() },
                    { "complete", V.Bool(objective.Get("complete", false)) },
                    { "calibrator_applied", V.Bool(objective.Get("calibrator_applied", false)) },
                };
            }
            return summary;
        }

        public void Reset() => _objectives.Clear();

        /// <summary>
        /// REQ-012 / REQ-014: restore from a <see cref="GetSummary"/>-shaped dict. Accepts int, float, and
        /// digit-only string sequence keys; anything else is skipped. Returns true if any record changed.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            foreach (var sequenceVariant in summary.Keys)
            {
                long sequence = 0;
                switch (sequenceVariant)
                {
                    case long _:
                    case double _:
                        sequence = V.I64(sequenceVariant);
                        break;
                    case string s:
                    {
                        string raw = InfraCompat.StripEdges(s);
                        if (InfraCompat.IsValidInt(raw) && V.StringToInt(raw) > 0) sequence = V.StringToInt(raw);
                        break;
                    }
                    default:
                        continue;
                }
                if (sequence <= 0) continue;
                object objectiveVariant = summary[sequenceVariant];
                if (!(objectiveVariant is GdDict objective)) continue;
                long requiredSteps = Math.Max(1L, V.I64(objective.Get("required_steps", 1L)));
                var completedStepIds = new GdArray();
                object completedIdsVariant = objective.Get("completed_step_ids", new GdArray());
                if (completedIdsVariant is GdArray ids)
                {
                    foreach (var stepId in ids) completedStepIds.Add(V.Str(stepId));
                }
                string objectiveType = V.Str(objective.Get("objective_type", ""));
                bool calibratorApplied = V.Bool(objective.Get("calibrator_applied", false));
                var newRecord = new GdDict
                {
                    { "objective_type", objectiveType },
                    { "required_steps", requiredSteps },
                    { "completed_steps", completedStepIds.Count },
                    { "completed_step_ids", completedStepIds },
                    { "complete", completedStepIds.Count >= requiredSteps },
                    { "calibrator_applied", calibratorApplied },
                };
                if (!_objectives.Has(sequence) || !V.VariantEquals(_objectives[sequence], newRecord))
                {
                    _objectives[sequence] = newRecord;
                    changed = true;
                }
            }
            return changed;
        }
    }
}
