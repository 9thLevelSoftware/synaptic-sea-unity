// Ported from scripts/systems/integration_matrix.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The duck-typed matrix surface <see cref="DependencyValidator"/> (<c>get_entries</c>) and
    /// <see cref="ProductAuditReport"/> (<c>get_entry_count</c>, <c>covers_loop_stages</c>) call.
    /// </summary>
    public interface IIntegrationMatrixView
    {
        GdArray GetEntries();
        int GetEntryCount();
        bool CoversLoopStages(GdArray requiredStages);
    }

    /// <summary>
    /// REQ-INT-001 / REQ-INT-002 cross-system package dependency matrix. Normalizes the data-driven
    /// integration manifest into package rows audited by <see cref="DependencyValidator"/>.
    /// </summary>
    public class IntegrationMatrix : IIntegrationMatrixView, IStatusLineProvider
    {
        GdDict _metadata = new GdDict();
        readonly GdArray _entries = new GdArray();
        readonly GdDict _entriesById = new GdDict();

        public bool Configure(GdDict data)
        {
            _metadata.Clear();
            _entries.Clear();
            _entriesById.Clear();
            if (data == null || data.IsEmpty) return false;
            _metadata = AsDict(data.Get("metadata", new GdDict()));
            GdArray rawEntries = AsArray(data.Get("systems", data.Get("entries", new GdArray())));
            if (rawEntries.IsEmpty) return false;
            foreach (var raw in rawEntries)
            {
                GdDict row = AsDict(raw);
                if (row.IsEmpty) continue;
                string packageId = V.Str(row.Get("package_id", row.Get("id", "")));
                if (packageId.Length == 0) continue;
                GdDict normalized = row.DeepCopy();
                normalized["package_id"] = packageId;
                normalized["requirements"] = ToStringArray(row.Get("requirements", new GdArray()));
                normalized["code_files"] = ToStringArray(row.Get("code_files", new GdArray()));
                normalized["docs_files"] = ToStringArray(row.Get("docs_files", new GdArray()));
                normalized["data_files"] = ToStringArray(row.Get("data_files", new GdArray()));
                normalized["smoke_files"] = ToStringArray(row.Get("smoke_files", new GdArray()));
                normalized["smoke_markers"] = ToStringArray(row.Get("smoke_markers", new GdArray()));
                normalized["loop_stages"] = ToStringArray(row.Get("loop_stages", new GdArray()));
                normalized["dependencies"] = ToStringArray(row.Get("dependencies", new GdArray()));
                normalized["requires_smoke"] = V.Bool(row.Get("requires_smoke", true));
                _entries.Add(normalized);
                _entriesById[packageId] = normalized;
            }
            return !_entries.IsEmpty;
        }

        public int GetEntryCount() => _entries.Count;

        public GdArray GetEntries() => _entries.DeepCopy();

        public GdDict GetEntry(string packageId) => AsDict(_entriesById.Get(packageId, new GdDict())).DeepCopy();

        public bool HasEntry(string packageId) => _entriesById.Has(packageId);

        public GdArray GetPackageIds() => InfraCompat.SortedKeys(_entriesById);

        public List<string> GetRequirementIds()
        {
            var seen = new GdDict();
            foreach (var entry in _entries)
            {
                foreach (var rid in AsArray(((GdDict)entry).Get("requirements", new GdArray())))
                    seen[V.Str(rid)] = true;
            }
            return SortedStrings(seen);
        }

        public List<string> GetSmokeMarkers()
        {
            var seen = new GdDict();
            foreach (var entry in _entries)
            {
                foreach (var marker in AsArray(((GdDict)entry).Get("smoke_markers", new GdArray())))
                {
                    if (V.Str(marker).Length != 0) seen[V.Str(marker)] = true;
                }
            }
            return SortedStrings(seen);
        }

        public List<string> GetDocumentPaths()
        {
            var seen = new GdDict();
            foreach (var entry in _entries)
            {
                foreach (var path in AsArray(((GdDict)entry).Get("docs_files", new GdArray())))
                {
                    if (V.Str(path).Length != 0) seen[V.Str(path)] = true;
                }
            }
            return SortedStrings(seen);
        }

        public bool CoversLoopStages(GdArray requiredStages)
        {
            GdDict coverage = GetLoopStageCoverage();
            foreach (var stage in requiredStages)
            {
                if (!coverage.Has(V.Str(stage))) return false;
            }
            return true;
        }

        public GdDict GetLoopStageCoverage()
        {
            var coverage = new GdDict();
            foreach (var entry in _entries)
            {
                foreach (var stage in AsArray(((GdDict)entry).Get("loop_stages", new GdArray())))
                {
                    string key = V.Str(stage);
                    if (key.Length == 0) continue;
                    coverage[key] = V.I64(coverage.Get(key, 0L)) + 1;
                }
            }
            return coverage;
        }

        public GdArray GetMissingRequiredFields()
        {
            var missing = new GdArray();
            foreach (var entry in _entries)
            {
                var row = (GdDict)entry;
                string packageId = V.Str(row.Get("package_id", ""));
                foreach (string fieldName in new[] { "title", "task_id", "status", "loop_stages" })
                {
                    if (!row.Has(fieldName) || IsEmptyValue(row.Get(fieldName)))
                        missing.Add(new GdDict { { "package_id", packageId }, { "field", fieldName } });
                }
                if (V.Bool(row.Get("requires_smoke", true)) && AsArray(row.Get("smoke_markers", new GdArray())).IsEmpty)
                    missing.Add(new GdDict { { "package_id", packageId }, { "field", "smoke_markers" } });
            }
            return missing;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "entry_count", GetEntryCount() },
                { "requirement_count", GetRequirementIds().Count },
                { "smoke_marker_count", GetSmokeMarkers().Count },
                { "document_count", GetDocumentPaths().Count },
                { "loop_stage_coverage", GetLoopStageCoverage() },
                { "missing_required_fields", GetMissingRequiredFields() },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            GdDict summary = GetSummary();
            lines.Add(InfraCompat.Fmt("Integration Matrix: {0} package rows", V.I64(summary.Get("entry_count", 0L))));
            lines.Add(InfraCompat.Fmt("  requirements={0} smoke_markers={1} docs={2}",
                V.I64(summary.Get("requirement_count", 0L)),
                V.I64(summary.Get("smoke_marker_count", 0L)),
                V.I64(summary.Get("document_count", 0L))));
            GdDict coverage = summary.Get("loop_stage_coverage", new GdDict()) as GdDict;
            GdArray stages = InfraCompat.SortedKeys(coverage);
            foreach (var stage in stages)
                lines.Add(InfraCompat.Fmt("  {0}={1}", V.Str(stage), V.I64(coverage[stage])));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        static List<string> SortedStrings(GdDict seen)
        {
            var output = new List<string>();
            foreach (var key in InfraCompat.SortedKeys(seen)) output.Add(V.Str(key));
            return output;
        }

        static GdArray ToStringArray(object value) => InfraCompat.ToStringArray(value);
        static GdArray AsArray(object value) => InfraCompat.AsArray(value);
        static GdDict AsDict(object value) => InfraCompat.AsDict(value);

        static bool IsEmptyValue(object value)
        {
            if (value == null) return true;
            if (value is string s) return s.Length == 0;
            if (value is GdArray a) return a.IsEmpty;
            if (value is GdDict d) return d.IsEmpty;
            return false;
        }
    }
}
