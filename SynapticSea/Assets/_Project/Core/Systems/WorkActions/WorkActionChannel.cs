// Ported from scripts/systems/work_action_channel.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.5: shared WorkAction progress/interrupt channel for scene wrappers (RepairPoint, BreachSealPoint,
    /// FireSuppressionPoint). Domain gates and completion side-effects stay on the wrappers; this owns the pure
    /// <see cref="WorkActionState"/> tick path so all three share one progress/interrupt model.
    /// </summary>
    public class WorkActionChannel
    {
        static WorkActionCatalog _sharedCatalog = null;

        /// <summary>WorkActionState while active.</summary>
        public WorkActionState Work = null;
        public string ActionId = "";

        public static WorkActionCatalog SharedCatalog()
        {
            if (_sharedCatalog == null)
            {
                _sharedCatalog = new WorkActionCatalog();
                _sharedCatalog.LoadDefault();
            }
            return _sharedCatalog;
        }

        /// <summary>
        /// Port-only: drops the process-wide catalog cache (GDScript <c>static var</c> lived for the process),
        /// so tests and data hot-reload can rebuild it after CatalogRegistry changes.
        /// </summary>
        public static void ResetSharedCatalog()
        {
            _sharedCatalog = null;
        }

        /// <summary>
        /// Begin a catalog action. <paramref name="durationSeconds"/> overrides the catalog default (skill-scaled
        /// repair, etc.). <paramref name="context"/> is passed to WorkActionState.Start for tool/skill/material gates.
        /// </summary>
        public bool Begin(string pActionId, string targetId, double durationSeconds, GdDict context = null)
        {
            context = context ?? new GdDict();
            WorkActionCatalog cat = SharedCatalog();
            if (cat == null || !cat.HasAction(pActionId))
                return false;
            GdDict def = cat.GetAction(pActionId);
            if (def.IsEmpty)
                return false;
            var state = new WorkActionState();
            state.ConfigureAction(pActionId, def);
            state.Duration = Math.Max(0.01, durationSeconds);
            if (!state.Start(targetId, context))
                return false;
            Work = state;
            ActionId = pActionId;
            return true;
        }

        public string Tick(double delta, GdDict context = null)
        {
            context = context ?? new GdDict();
            if (Work == null)
                return WorkActionState.STATUS_IDLE;
            return Work.Tick(delta, context);
        }

        public double ProgressRatio()
        {
            if (Work == null)
                return 0.0;
            return Work.ProgressRatio();
        }

        public bool IsActive()
        {
            if (Work == null)
                return false;
            return Work.Status == WorkActionState.STATUS_ACTIVE;
        }

        public bool IsCompleted()
        {
            if (Work == null)
                return false;
            return Work.Status == WorkActionState.STATUS_COMPLETED;
        }

        public bool IsInterrupted()
        {
            if (Work == null)
                return false;
            return Work.Status == WorkActionState.STATUS_INTERRUPTED;
        }

        public void Cancel()
        {
            if (Work != null)
                Work.Reset();
            Work = null;
            ActionId = "";
        }

        public GdDict GetSummary()
        {
            if (Work == null)
                return new GdDict { { "action_id", ActionId }, { "status", WorkActionState.STATUS_IDLE }, { "progress", 0.0 } };
            return Work.GetSummary();
        }
    }
}
