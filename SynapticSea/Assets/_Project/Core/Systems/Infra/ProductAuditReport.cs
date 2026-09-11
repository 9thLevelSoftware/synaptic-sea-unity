// Ported from scripts/systems/product_audit_report.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-INT-006 / REQ-INT-007 product audit report model. Validates that every finding has evidence,
    /// open contradictions are tied to fix cards, and the verdict evaluates the integration matrix.
    /// </summary>
    public class ProductAuditReport : IStatusLineProvider
    {
        public static readonly GdArray TrackedStatuses = GdArray.Of("tracked", "resolved", "accepted", "deferred_with_card");

        GdDict _report = new GdDict();
        GdDict _issueManifest = new GdDict();
        GdArray _findings = new GdArray();
        GdArray _fixCards = new GdArray();

        public bool Configure(GdDict reportData, GdDict issueManifest)
        {
            _report = reportData != null ? reportData.DeepCopy() : new GdDict();
            _issueManifest = issueManifest != null ? issueManifest.DeepCopy() : new GdDict();
            _findings = InfraCompat.AsArray(_report.Get("findings", new GdArray()));
            _fixCards = InfraCompat.AsArray(_issueManifest.Get("fix_cards", _issueManifest.Get("issues", new GdArray())));
            if (_report.IsEmpty || _findings.IsEmpty) return false;
            if (!HasProductVerdict()) return false;
            foreach (var rawFinding in _findings)
            {
                GdDict finding = InfraCompat.AsDict(rawFinding);
                if (V.Str(finding.Get("finding_id", "")).Length == 0) return false;
                if (InfraCompat.AsArray(finding.Get("evidence", new GdArray())).IsEmpty) return false;
                string severity = V.Str(finding.Get("severity", "")).ToLowerInvariant();
                if ((severity == "blocking" || severity == "follow_up" || severity == "contradiction") && V.Str(finding.Get("fix_key", "")).Length == 0)
                    return false;
            }
            return true;
        }

        /// <summary><c>validate_against_matrix(matrix)</c>: any object exposing the matrix view.</summary>
        public GdDict ValidateAgainstMatrix(object matrix)
        {
            var view = matrix as IIntegrationMatrixView;
            if (view == null) return new GdDict { { "pass", false }, { "reason", "matrix_missing" } };
            long minSystems = V.I64(_report.Get("min_matrix_entries", 14L));
            if (view.GetEntryCount() < minSystems)
            {
                return new GdDict
                {
                    { "pass", false }, { "reason", "matrix_entry_count" }, { "entry_count", view.GetEntryCount() }, { "min", minSystems },
                };
            }
            GdArray requiredStages = InfraCompat.AsArray(_report.Get("required_loop_stages", new GdArray()));
            if (!view.CoversLoopStages(requiredStages)) return new GdDict { { "pass", false }, { "reason", "loop_stage_coverage" } };
            var missingLinks = new GdArray();
            foreach (var rawFinding in _findings)
            {
                GdDict finding = InfraCompat.AsDict(rawFinding);
                string fixKey = V.Str(finding.Get("fix_key", ""));
                if (fixKey.Length == 0) continue;
                if (!ManifestHasFixKey(fixKey)) missingLinks.Add(fixKey);
            }
            if (!missingLinks.IsEmpty)
                return new GdDict { { "pass", false }, { "reason", "fix_keys_missing" }, { "missing", missingLinks } };
            return new GdDict
            {
                { "pass", true }, { "entry_count", view.GetEntryCount() }, { "findings", GetFindingCount() }, { "fix_cards", GetFixCardCount() },
            };
        }

        public int GetFindingCount() => _findings.Count;

        public long GetFixCardCount()
        {
            long count = 0;
            foreach (var raw in _fixCards)
            {
                GdDict card = InfraCompat.AsDict(raw);
                string cardId = V.Str(card.Get("kanban_card_id", card.Get("linked_task_id", "")));
                if (cardId.Length != 0) count += 1;
            }
            return count;
        }

        public long GetBlockingCount()
        {
            long count = 0;
            foreach (var rawFinding in _findings)
            {
                GdDict finding = InfraCompat.AsDict(rawFinding);
                string severity = V.Str(finding.Get("severity", "")).ToLowerInvariant();
                string status = V.Str(finding.Get("status", "")).ToLowerInvariant();
                if (severity == "blocking" && !TrackedStatuses.Contains(status)) count += 1;
            }
            return count;
        }

        public bool HasProductVerdict() => GetVerdict().Length != 0;

        public string GetVerdict() => V.Str(_report.Get("verdict", ""));

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "verdict", GetVerdict() },
                { "finding_count", GetFindingCount() },
                { "fix_card_count", GetFixCardCount() },
                { "blocking_count", GetBlockingCount() },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("Product Audit: verdict={0} findings={1} fix_cards={2} blocking={3}",
                GetVerdict(), GetFindingCount(), GetFixCardCount(), GetBlockingCount()));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        bool ManifestHasFixKey(string fixKey)
        {
            foreach (var raw in _fixCards)
            {
                GdDict card = InfraCompat.AsDict(raw);
                if (V.Str(card.Get("fix_key", "")) == fixKey)
                {
                    string cardId = V.Str(card.Get("kanban_card_id", card.Get("linked_task_id", "")));
                    return cardId.Length != 0;
                }
            }
            return false;
        }
    }
}
