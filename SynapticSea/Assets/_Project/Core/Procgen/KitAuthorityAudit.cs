// Kit + structural placement contracts are the only socket/footprint authority.
// Companion {module_id}.asset.json is the art-package gate for new modules (mesh alone is not shippable).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>One ithappy / structural module's kit-vs-contract gate result.</summary>
    public sealed class KitAuthorityModuleReport
    {
        public string ModuleId = "";
        public bool Ungated;
        public bool HasV0Twin;
        public bool HasCompanionAssetJson;
        public readonly List<string> Issues = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<long> KitFootprint = new List<long>();
        public readonly List<long> ContractFootprint = new List<long>();
        public readonly List<string> KitSocketNames = new List<string>();
        public readonly List<string> ContractSocketIds = new List<string>();
        public readonly List<string> ContractKinds = new List<string>();
    }

    /// <summary>Audit of every module row in a structural kit.</summary>
    public sealed class KitAuthorityReport
    {
        public string KitId = "";
        public readonly List<KitAuthorityModuleReport> Modules = new List<KitAuthorityModuleReport>();

        public List<KitAuthorityModuleReport> UngatedModules()
        {
            var list = new List<KitAuthorityModuleReport>();
            foreach (var module in Modules)
                if (module.Ungated) list.Add(module);
            return list;
        }

        public string DescribeUngated()
        {
            var ungated = UngatedModules();
            if (ungated.Count == 0) return "ungated=0";
            var sb = new StringBuilder();
            sb.Append("ungated=").Append(ungated.Count);
            foreach (var module in ungated)
            {
                sb.Append("\n  ").Append(module.ModuleId);
                foreach (string issue in module.Issues) sb.Append("\n    ").Append(issue);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Engine-free gate: ingest kit JSON rows plus
    /// <c>data/placement/contracts/structural/</c> only. No invented SOCK names.
    /// New modules (no v0 twin) also need a companion <c>{module_id}.asset.json</c> beside the GLB.
    /// </summary>
    public static class KitAuthorityAudit
    {
        public const string IthappyKitId = "ithappy_scifi_v0";
        public const string DefaultKitId = "ship_structural_v0";
        public const string CompanionDocumentKind = "structural_art_package";

        /// <summary>Godot kit-authority kinds locked for new art. Do not invent synonyms.</summary>
        public static readonly IReadOnlyList<string> ArtLockKinds = new[]
        {
            "floor_edge",
            "wall_face",
            "wall_end",
            "outer_corner_vertex",
            "inner_corner_vertex",
            "portal_edge",
            "portal_center",
            "prop_anchor",
        };

        /// <summary>
        /// Extra kinds that already live on v0/ithappy contract rows (compiler <c>ChooseModule</c>).
        /// Inherited, not invented; they may appear on contracts without a kit <c>socket_names</c> entry.
        /// </summary>
        public static readonly IReadOnlyList<string> InheritedGodotKinds = new[]
        {
            "floor_top",
            "wall_base",
            "wall_edge",
            "corridor_edge",
            "ceiling_edge",
            "ceiling_bottom",
        };

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string CompanionFileName(string moduleId) => (moduleId ?? "") + ".asset.json";

        public static string CompanionAssetJsonPath(string structuralContentRoot, string moduleId) =>
            Path.Combine(structuralContentRoot ?? "", moduleId ?? "", CompanionFileName(moduleId));

        public static string SocketIdFromKitName(string socketName)
        {
            string name = socketName ?? "";
            return name.StartsWith("SOCK_", StringComparison.Ordinal) ? name.Substring(5) : name;
        }

        public static string KitSocketNameFromId(string socketId)
        {
            string id = socketId ?? "";
            return id.StartsWith("SOCK_", StringComparison.Ordinal) ? id : "SOCK_" + id;
        }

        /// <summary>Builds the Forge companion document from an already-gated kit row. Does not invent sockets.</summary>
        public static GdDict CompanionFromKitRow(GdDict kitRow, string collisionNote)
        {
            var sockets = new GdArray();
            foreach (var name in FootprintIntsOrNames(kitRow, "socket_names", asNames: true))
                sockets.Add(name);
            var footprint = new GdArray();
            foreach (long cell in ReadFootprint(kitRow)) footprint.Add(cell);
            return new GdDict
            {
                { "schema_version", "1.0.0" },
                { "document_kind", CompanionDocumentKind },
                { "module_id", kitRow.GetString("module_id") },
                { "module_family", kitRow.GetString("module_family") },
                { "footprint_cells", footprint },
                { "socket_names", sockets },
                { "pivot_policy", kitRow.GetString("pivot_policy") },
                { "nav_blocker", kitRow.GetBool("nav_blocker") },
                { "collision_note", collisionNote ?? "" },
            };
        }

        public static KitAuthorityReport AuditIthappy(string repoRoot)
        {
            string streaming = Path.Combine(repoRoot ?? "", "SynapticSea", "Assets", "StreamingAssets");
            string kitPath = Path.Combine(streaming, "data", "kits", IthappyKitId + ".json");
            string contractDir = Path.Combine(streaming, "data", "placement", "contracts", "structural", IthappyKitId);
            string v0Dir = Path.Combine(streaming, "data", "placement", "contracts", "structural", DefaultKitId);
            string contentRoot = Path.Combine(repoRoot ?? "", "SynapticSea", "Assets", "Content", "Structural", "ithappy");
            return AuditKitFile(kitPath, contractDir, v0Dir, contentRoot);
        }

        public static KitAuthorityReport AuditKitFile(string kitPath, string contractDir, string v0ContractDir, string structuralContentRoot)
        {
            var report = new KitAuthorityReport();
            if (!File.Exists(kitPath))
            {
                report.KitId = IthappyKitId;
                var missing = new KitAuthorityModuleReport { ModuleId = "(kit)", Ungated = true };
                missing.Issues.Add("kit JSON missing: " + kitPath);
                report.Modules.Add(missing);
                return report;
            }
            GdDict kit = GdJson.ParseDict(File.ReadAllText(kitPath, Utf8NoBom));
            report.KitId = kit != null ? kit.GetString("kit_id", IthappyKitId) : IthappyKitId;
            if (kit == null)
            {
                var invalid = new KitAuthorityModuleReport { ModuleId = "(kit)", Ungated = true };
                invalid.Issues.Add("kit JSON invalid: " + kitPath);
                report.Modules.Add(invalid);
                return report;
            }

            foreach (object entry in kit.GetArrayOrEmpty("modules"))
            {
                if (!(entry is GdDict row)) continue;
                string moduleId = row.GetString("module_id");
                if (moduleId.Length == 0) continue;
                string contractPath = Path.Combine(contractDir ?? "", moduleId + "_contract.json");
                GdDict contract = File.Exists(contractPath)
                    ? GdJson.ParseDict(File.ReadAllText(contractPath, Utf8NoBom))
                    : null;
                string companionPath = CompanionAssetJsonPath(structuralContentRoot, moduleId);
                GdDict companion = File.Exists(companionPath)
                    ? GdJson.ParseDict(File.ReadAllText(companionPath, Utf8NoBom))
                    : null;
                bool hasV0Twin = File.Exists(Path.Combine(v0ContractDir ?? "", moduleId + "_contract.json"));
                report.Modules.Add(EvaluateModule(row, contract, companion, hasV0Twin));
            }
            return report;
        }

        public static KitAuthorityModuleReport EvaluateModule(GdDict kitRow, GdDict contract, GdDict companion, bool hasV0Twin)
        {
            var report = new KitAuthorityModuleReport
            {
                ModuleId = kitRow != null ? kitRow.GetString("module_id") : "",
                HasV0Twin = hasV0Twin,
                HasCompanionAssetJson = companion != null,
            };
            if (kitRow == null)
            {
                report.Ungated = true;
                report.Issues.Add("kit row missing");
                return report;
            }

            List<long> kitFp = ReadFootprint(kitRow);
            foreach (long cell in kitFp) report.KitFootprint.Add(cell);
            foreach (string name in FootprintIntsOrNames(kitRow, "socket_names", asNames: true))
                report.KitSocketNames.Add(name);

            if (kitFp.Count != 2) report.Issues.Add("kit missing footprint_cells");
            if (report.KitSocketNames.Count == 0) report.Issues.Add("kit missing socket_names");

            if (contract == null)
            {
                report.Issues.Add("contract missing");
            }
            else
            {
                List<long> contractFp = ReadFootprint(contract);
                foreach (long cell in contractFp) report.ContractFootprint.Add(cell);
                if (contractFp.Count != 2) report.Issues.Add("contract missing footprint_cells");
                else if (kitFp.Count == 2 && (kitFp[0] != contractFp[0] || kitFp[1] != contractFp[1]))
                    report.Issues.Add("footprint mismatch kit=[" + kitFp[0] + "," + kitFp[1] + "] contract=[" + contractFp[0] + "," + contractFp[1] + "]");

                var contractIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (object socketVariant in contract.GetArrayOrEmpty("sockets"))
                {
                    if (!(socketVariant is GdDict socket)) continue;
                    string id = socket.GetString("id");
                    if (id.Length == 0) continue;
                    report.ContractSocketIds.Add(id);
                    contractIds.Add(id);
                    string kind = socket.GetString("kind");
                    if (kind.Length != 0 && !report.ContractKinds.Contains(kind)) report.ContractKinds.Add(kind);
                }
                if (report.ContractSocketIds.Count == 0) report.Issues.Add("contract missing sockets");

                foreach (string name in report.KitSocketNames)
                {
                    string id = SocketIdFromKitName(name);
                    if (!contractIds.Contains(id))
                        report.Issues.Add("kit socket not in contract: " + name);
                }

                var kitIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in report.KitSocketNames) kitIds.Add(SocketIdFromKitName(name));
                foreach (string id in report.ContractSocketIds)
                {
                    if (!kitIds.Contains(id))
                        report.Notes.Add("contract socket not listed on kit socket_names: SOCK_" + id);
                }
            }

            if (!hasV0Twin)
            {
                if (companion == null)
                {
                    report.Issues.Add("ungated new module missing companion " + CompanionFileName(report.ModuleId) + " (mesh alone is not shippable)");
                }
                else
                {
                    ValidateCompanion(report, kitRow, companion);
                }
            }
            else if (companion != null)
            {
                ValidateCompanion(report, kitRow, companion);
            }

            report.Ungated = report.Issues.Count > 0;
            return report;
        }

        /// <summary>
        /// Bake-time errors for one module. Inherited v0 twins may omit the companion file;
        /// new modules and any present companion are gated.
        /// </summary>
        public static List<string> BakeErrors(GdDict kitRow, GdDict contract, GdDict companion, bool hasV0Twin)
        {
            KitAuthorityModuleReport report = EvaluateModule(kitRow, contract, companion, hasV0Twin);
            return report.Ungated ? new List<string>(report.Issues) : new List<string>();
        }

        static void ValidateCompanion(KitAuthorityModuleReport report, GdDict kitRow, GdDict companion)
        {
            string moduleId = report.ModuleId;
            if (companion.GetString("module_id") != moduleId)
                report.Issues.Add("companion module_id mismatch");
            if (companion.GetString("module_family") != kitRow.GetString("module_family"))
                report.Issues.Add("companion module_family mismatch");
            if (companion.GetString("pivot_policy") != kitRow.GetString("pivot_policy"))
                report.Issues.Add("companion pivot_policy mismatch");
            if (companion.GetBool("nav_blocker") != kitRow.GetBool("nav_blocker"))
                report.Issues.Add("companion nav_blocker mismatch");

            List<long> companionFp = ReadFootprint(companion);
            if (companionFp.Count != 2)
                report.Issues.Add("companion missing footprint_cells");
            else if (report.KitFootprint.Count == 2 && (companionFp[0] != report.KitFootprint[0] || companionFp[1] != report.KitFootprint[1]))
                report.Issues.Add("companion footprint_cells mismatch");

            var companionNames = new List<string>(FootprintIntsOrNames(companion, "socket_names", asNames: true));
            if (companionNames.Count == 0)
                report.Issues.Add("companion missing socket_names");
            else if (!SameStringList(companionNames, report.KitSocketNames))
                report.Issues.Add("companion socket_names mismatch kit row");

            string note = companion.GetString("collision_note");
            if (note.Length == 0 && companion.Get("collision") is GdDict collision)
                note = collision.GetString("notes", collision.GetString("note"));
            if (note.Length == 0)
                report.Issues.Add("companion missing collision_note");
        }

        static List<long> ReadFootprint(GdDict doc)
        {
            var cells = new List<long>();
            if (doc == null) return cells;
            foreach (object item in doc.GetArrayOrEmpty("footprint_cells"))
            {
                if (V.IsNumber(item)) cells.Add(V.I64(item));
            }
            return cells;
        }

        static List<string> FootprintIntsOrNames(GdDict doc, string key, bool asNames)
        {
            var names = new List<string>();
            if (doc == null || !asNames) return names;
            foreach (object item in doc.GetArrayOrEmpty(key))
            {
                string name = V.Str(item);
                if (name.Length != 0) names.Add(name);
            }
            return names;
        }

        static bool SameStringList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
