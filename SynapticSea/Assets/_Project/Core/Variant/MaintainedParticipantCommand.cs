using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Variant
{
    internal sealed partial class ParticipantProjectionCohort
    {
        // Preparation-only bridge from validated actual model commands. This does not publish or
        // install participant state; reservation and paired publication remain separate dependencies.
        internal MaintainedParticipantCommand PrepareMaintainedCommand(
            InventoryState.PreparedReplacement inventory,
            PlayerProgressionState.PreparedReplacement progression,
            TrainingEventBus.PreparedReplacement training)
        {
            var origins = new List<PreparedVariantBacking.ProjectionReplacementOrigin>(3);
            lock (CommonParticipantGate.SyncRoot)
            {
                if (!CurrentUnderGate()) throw new InvalidOperationException("maintenance_cohort_not_ready");
                if (inventory != null)
                {
                    if (!inventory.IsForModel(_inventory) || !inventory.MatchesUnderGate())
                        throw new ArgumentException("foreign_or_stale_inventory_command");
                    origins.Add(inventory.CaptureProjectionOrigin());
                }
                if (progression != null)
                {
                    if (!progression.IsForModel(_progression) || !progression.MatchesUnderGate())
                        throw new ArgumentException("foreign_or_stale_progression_command");
                    origins.Add(progression.CaptureProjectionOrigin());
                }
                if (training != null)
                {
                    if (!training.IsForModel(_training) || !training.MatchesUnderGate())
                        throw new ArgumentException("foreign_or_stale_training_command");
                    origins.Add(training.CaptureProjectionOrigin());
                }
                if (origins.Count == 0) throw new ArgumentException("empty_maintenance_command");
            }
            var candidate = PrepareMaintainedCandidate(origins.ToArray());
            try
            {
                var admission = PrepareCandidateAdmission(candidate);
                return MaintainedParticipantCommand.Create(candidate, admission);
            }
            catch { candidate.Dispose(); throw; }
        }
    }

    internal sealed class MaintainedParticipantCommand : IDisposable
    {
        ParticipantProjectionCohort.MaintainedCandidate _candidate;
        MaintainedGraphAdmissionCursor _admission;
        private MaintainedParticipantCommand(ParticipantProjectionCohort.MaintainedCandidate candidate,
            MaintainedGraphAdmissionCursor admission) { _candidate = candidate; _admission = admission; }
        internal static MaintainedParticipantCommand Create(ParticipantProjectionCohort.MaintainedCandidate candidate,
            MaintainedGraphAdmissionCursor admission) => new MaintainedParticipantCommand(candidate, admission);
        internal ProjectionCursorStatus Status => _admission == null ? ProjectionCursorStatus.Cancelled : _admission.Status;
        internal void Advance(int budget)
        {
            if (_admission == null) throw new ObjectDisposedException(nameof(MaintainedParticipantCommand));
            _admission.Advance(budget);
        }
        public void Dispose()
        {
            _admission?.Dispose(); _admission = null;
            _candidate?.Dispose(); _candidate = null;
        }
    }
}
