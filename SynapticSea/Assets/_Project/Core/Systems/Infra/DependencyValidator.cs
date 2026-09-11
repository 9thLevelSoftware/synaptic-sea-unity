// Ported from scripts/systems/dependency_validator.gd @ 96ecb2b0
using System;
using System.IO;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-INT-002 cross-system dependency verifier. Verifies that every matrix row has concrete file,
    /// requirement, marker, and documentation evidence. Pure host-file validator for headless checks.
    /// </summary>
    public class DependencyValidator
    {
        GdArray _entries = new GdArray();

        /// <summary>
        /// <c>FileAccess.file_exists</c> for host paths. Defaults to <c>res://</c> through
        /// <see cref="CoreServices.Resources"/> and everything else through <see cref="File.Exists"/>.
        /// </summary>
        public Func<string, bool> FileExistsProvider;

        /// <summary><c>configure(matrix)</c>: any object exposing <c>get_entries()</c>.</summary>
        public void Configure(object matrix)
        {
            _entries.Clear();
            if (matrix is IIntegrationMatrixView view) _entries = view.GetEntries();
        }

        public GdDict VerifyFileEvidence(string rootPath)
        {
            var missing = new GdArray();
            long checkedCount = 0;
            foreach (var entry in _entries)
            {
                var row = entry as GdDict;
                foreach (string fieldName in new[] { "code_files", "smoke_files", "data_files" })
                {
                    foreach (var path in InfraCompat.AsArray(row.Get(fieldName, new GdArray())))
                    {
                        string filePath = V.Str(path);
                        if (filePath.Length == 0) continue;
                        checkedCount += 1;
                        if (!FileExistsAt(rootPath, filePath))
                        {
                            missing.Add(new GdDict
                            {
                                { "package_id", V.Str(row.Get("package_id", "")) }, { "field", fieldName }, { "path", filePath },
                            });
                        }
                    }
                }
            }
            return new GdDict { { "ok", missing.IsEmpty }, { "checked", checkedCount }, { "missing", missing } };
        }

        public GdDict VerifyDocumentationEvidence(string rootPath)
        {
            var missing = new GdArray();
            long checkedCount = 0;
            foreach (var entry in _entries)
            {
                var row = entry as GdDict;
                foreach (var path in InfraCompat.AsArray(row.Get("docs_files", new GdArray())))
                {
                    string filePath = V.Str(path);
                    if (filePath.Length == 0) continue;
                    checkedCount += 1;
                    if (!FileExistsAt(rootPath, filePath))
                        missing.Add(new GdDict { { "package_id", V.Str(row.Get("package_id", "")) }, { "path", filePath } });
                }
            }
            return new GdDict { { "ok", missing.IsEmpty }, { "checked", checkedCount }, { "missing", missing } };
        }

        public GdDict VerifyRequirementRows(string requirementsText)
        {
            var missing = new GdArray();
            long checkedCount = 0;
            foreach (var entry in _entries)
            {
                var row = entry as GdDict;
                foreach (var reqId in InfraCompat.AsArray(row.Get("requirements", new GdArray())))
                {
                    string rid = V.Str(reqId);
                    if (rid.Length == 0) continue;
                    checkedCount += 1;
                    if (!requirementsText.Contains("## " + rid + ":"))
                        missing.Add(new GdDict { { "package_id", V.Str(row.Get("package_id", "")) }, { "requirement", rid } });
                }
            }
            return new GdDict { { "ok", missing.IsEmpty }, { "checked", checkedCount }, { "missing", missing } };
        }

        public GdDict VerifyValidationMarkers(string validationText)
        {
            var missing = new GdArray();
            long checkedCount = 0;
            foreach (var entry in _entries)
            {
                var row = entry as GdDict;
                if (!V.Bool(row.Get("requires_smoke", true))) continue;
                foreach (var marker in InfraCompat.AsArray(row.Get("smoke_markers", new GdArray())))
                {
                    string markerText = V.Str(marker);
                    if (markerText.Length == 0) continue;
                    checkedCount += 1;
                    if (!validationText.Contains(markerText))
                        missing.Add(new GdDict { { "package_id", V.Str(row.Get("package_id", "")) }, { "marker", markerText } });
                }
            }
            return new GdDict { { "ok", missing.IsEmpty }, { "checked", checkedCount }, { "missing", missing } };
        }

        public GdDict GetSummary() => new GdDict { { "entry_count", _entries.Count } };

        bool FileExistsAt(string rootPath, string path)
        {
            if (path.StartsWith("res://", StringComparison.Ordinal)) return Exists(path);
            if (path.StartsWith("/", StringComparison.Ordinal)) return Exists(path);
            return Exists(PathJoin(rootPath, path));
        }

        bool Exists(string path)
        {
            if (FileExistsProvider != null) return FileExistsProvider(path);
            if (path.StartsWith(ResPath.ResScheme, StringComparison.Ordinal)) return CoreServices.Resources?.Exists(path) ?? false;
            return File.Exists(path);
        }

        /// <summary><c>String.path_join</c>: inserts one '/' unless the base already ends with one.</summary>
        static string PathJoin(string basePath, string file)
        {
            if (string.IsNullOrEmpty(basePath)) return file;
            if (basePath.EndsWith("/", StringComparison.Ordinal) || (file.Length > 0 && file[0] == '/')) return basePath + file;
            return basePath + "/" + file;
        }
    }
}
