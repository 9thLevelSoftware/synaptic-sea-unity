// Ported from scripts/systems/release_readiness_ledger.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-RL-008 release readiness ledger. In-memory evidence tracker over the release checklist;
    /// <c>source=external</c> rows must carry a non-empty <c>evidence_path</c>.
    /// </summary>
    public class ReleaseReadinessLedger : IStatusLineProvider
    {
        public static readonly GdArray ValidSources = GdArray.Of("local", "external");
        public static readonly GdArray ValidStatuses = GdArray.Of("pass", "fail", "pending");
        public static readonly GdArray ValidCategories = GdArray.Of("pre_launch", "launch_day", "post_launch");

        readonly GdDict _checks = new GdDict();     // check_id -> {description, category}
        readonly GdArray _checkIds = new GdArray();
        readonly GdArray _rows = new GdArray();     // [{check_id, status, source, evidence_path, captured_at, note}]
        IClock _clock;

        public ReleaseReadinessLedger(IClock clock = null)
        {
            _clock = clock;
        }

        public IClock Clock
        {
            get => _clock ?? CoreServices.Clock;
            set => _clock = value;
        }

        public void Configure(GdDict checklist)
        {
            _checks.Clear();
            _checkIds.Clear();
            _rows.Clear();
            if (checklist == null) checklist = new GdDict();
            object listVariant = checklist.Get("checks", new GdArray());
            if (!(listVariant is GdArray list)) return;
            foreach (var entry in list)
            {
                if (!(entry is GdDict dict)) continue;
                string checkId = V.Str(dict.Get("check_id", ""));
                if (checkId.Length == 0) continue;
                string category = V.Str(dict.Get("category", ""));
                _checks[checkId] = new GdDict
                {
                    { "description", V.Str(dict.Get("description", "")) },
                    { "category", category },
                };
                _checkIds.Add(checkId);
            }
        }

        public bool IsKnownCheck(string checkId) => _checks.Has(checkId);

        public GdArray GetCheckIds() => _checkIds.ShallowCopy();

        public int GetCheckCount() => _checkIds.Count;

        public string GetCheckCategory(string checkId)
        {
            if (!_checks.Has(checkId)) return "";
            return V.Str(((GdDict)_checks[checkId]).Get("category", ""));
        }

        public bool RecordLocalEvidence(string checkId, string status, string evidencePath = "", string note = "")
        {
            return Record(checkId, status, "local", evidencePath, note);
        }

        public bool RecordExternalEvidence(string checkId, string status, string evidencePath, string capturedAt = "", string note = "")
        {
            if (string.IsNullOrEmpty(evidencePath))
            {
                CoreServices.Log.Warning("ReleaseReadinessLedger: external evidence rejected, evidence_path is required");
                return false;
            }
            return Record(checkId, status, "external", evidencePath, note, capturedAt);
        }

        bool Record(string checkId, string status, string source, string evidencePath, string note, string capturedAt = "")
        {
            if (string.IsNullOrEmpty(checkId) || !_checks.Has(checkId))
            {
                CoreServices.Log.Warning("ReleaseReadinessLedger: unknown check_id=" + checkId);
                return false;
            }
            if (!ValidStatuses.Contains(status))
            {
                CoreServices.Log.Warning("ReleaseReadinessLedger: invalid status=" + status);
                return false;
            }
            if (!ValidSources.Contains(source))
            {
                CoreServices.Log.Warning("ReleaseReadinessLedger: invalid source=" + source);
                return false;
            }
            if (source == "external" && string.IsNullOrEmpty(evidencePath))
            {
                // Already warned by RecordExternalEvidence; defensive duplicate.
                return false;
            }
            string captured = !string.IsNullOrEmpty(capturedAt) ? capturedAt : Clock.DateTimeString(true);
            _rows.Add(new GdDict
            {
                { "check_id", checkId },
                { "status", status },
                { "source", source },
                { "evidence_path", evidencePath },
                { "captured_at", captured },
                { "note", note },
            });
            return true;
        }

        public GdArray GetRows() => _rows.DeepCopy();

        public int GetRowCount() => _rows.Count;

        public long GetLocalCount()
        {
            long count = 0;
            foreach (var row in _rows)
            {
                if (V.Str(((GdDict)row).Get("source", "")) == "local") count += 1;
            }
            return count;
        }

        public long GetExternalCount()
        {
            long count = 0;
            foreach (var row in _rows)
            {
                if (V.Str(((GdDict)row).Get("source", "")) == "external") count += 1;
            }
            return count;
        }

        public GdDict GetCategoryCounts()
        {
            var counts = new GdDict { { "pre_launch", 0L }, { "launch_day", 0L }, { "post_launch", 0L } };
            foreach (var row in _rows)
            {
                string checkId = V.Str(((GdDict)row).Get("check_id", ""));
                if (!_checks.Has(checkId)) continue;
                string category = V.Str(((GdDict)_checks[checkId]).Get("category", ""));
                if (counts.Has(category)) counts[category] = V.I64(counts[category]) + 1;
            }
            return counts;
        }

        public GdDict GetStatusCounts()
        {
            var counts = new GdDict { { "pass", 0L }, { "fail", 0L }, { "pending", 0L } };
            foreach (var row in _rows)
            {
                string status = V.Str(((GdDict)row).Get("status", ""));
                if (counts.Has(status)) counts[status] = V.I64(counts[status]) + 1;
            }
            return counts;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "check_count", _checkIds.Count },
                { "row_count", _rows.Count },
                { "local_count", GetLocalCount() },
                { "external_count", GetExternalCount() },
                { "category_counts", GetCategoryCounts() },
                { "status_counts", GetStatusCounts() },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("Release Readiness: {0} checks, {1} rows", _checkIds.Count, _rows.Count));
            GdDict statusCounts = GetStatusCounts();
            lines.Add(InfraCompat.Fmt("  pass={0} fail={1} pending={2}",
                V.I64(statusCounts.Get("pass", 0L)),
                V.I64(statusCounts.Get("fail", 0L)),
                V.I64(statusCounts.Get("pending", 0L))));
            GdDict catCounts = GetCategoryCounts();
            foreach (var category in ValidCategories)
                lines.Add(InfraCompat.Fmt("  {0}={1}", V.Str(category), V.I64(catCounts.Get(category, 0L))));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
