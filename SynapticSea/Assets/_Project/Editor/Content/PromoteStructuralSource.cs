// Ported from tools/promote_structural_sources.py and tools/structural_source_contract.py (Godot repository) @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SynapticSea.Core.Variant;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Promotes staged structural GLBs (exported from the recovered Blender sources by
    /// <c>tools/python/export_structural_glb.py</c>) into <c>Assets/Content/Structural/ship_structural_v0/&lt;module&gt;/</c>.
    /// Port of the Godot <c>promote_structural_sources.py</c>; per module it:
    /// <list type="number">
    /// <item>checks the module against the 15-module allowlist;</item>
    /// <item>strictly validates the placement contract and the runtime intact GLB (<see cref="LoadSourceSpec"/>, the
    /// <c>structural_source_contract.load_source_spec</c> rules);</item>
    /// <item>requires at least one staged variant (<c>&lt;id&gt;.glb</c>, <c>_damaged</c>, <c>_breached</c>) and a valid GLB
    /// header (magic, version 2, length = file size) on each;</item>
    /// <item>skips variants whose SHA-256 equals the runtime GLB unless <c>-force</c>;</item>
    /// <item>import smoke (replaces the temporary Godot project overlay): imports the changed GLBs into a disposable
    /// <c>Assets/_PromotionSmoke</c> folder and requires glTFast to produce a GameObject with at least one renderer and
    /// no import error;</item>
    /// <item>copies atomically (temporary sibling, then replace) and re-imports, then rebuilds the structural prefabs
    /// with <see cref="StructuralPrefabBuilder"/> and fails on any builder error.</item>
    /// </list>
    /// <c>-dryRun</c> runs every check including the import smoke and changes nothing. A failing module does not stop
    /// later modules; the run fails at the end. The Blender export step and the external source backup stay in Python.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.PromoteStructuralSource.Run
    ///     -stagingRoot &lt;dir&gt; (-module &lt;id&gt; [-module &lt;id&gt;...] | -all) [-dryRun] [-force] [-skipImportSmoke]
    /// </summary>
    public static class PromoteStructuralSource
    {
        public const string KitId = "ship_structural_v0";
        public const string RuntimeAssetRoot = "Assets/Content/Structural/" + KitId;
        public const string SmokeAssetRoot = "Assets/_PromotionSmoke";

        /// <summary><c>STRUCTURAL_SOURCE_MODULE_IDS</c>, in the Godot order.</summary>
        public static readonly string[] ModuleIds =
        {
            "floor_1x1", "floor_2x1", "corridor_floor_1x1", "corridor_floor_1x2", "wall_straight_1x1",
            "doorway_frame_open_1x1", "pillar_support_1x1", "ramp_up_1x2", "bulkhead_portal_2x1", "ceiling_cap_1x1",
            "doorway_frame_blocked_1x1", "wall_end_cap", "wall_inner_corner", "wall_outer_corner", "wall_t_junction",
        };

        /// <summary><c>_VARIANT_SUFFIXES</c>.</summary>
        public static readonly (string variant, string suffix)[] Variants = { ("intact", ""), ("damaged", "_damaged"), ("breached", "_breached") };

        public sealed class PromotionException : Exception
        {
            public PromotionException(string message) : base(message) { }
        }

        public sealed class SocketSpec
        {
            public string SocketId;
            public string Kind;
            public string[] CompatibleKinds;
            public double[] PositionYUp;
        }

        /// <summary><c>StructuralSourceSpec</c>.</summary>
        public sealed class SourceSpec
        {
            public string ModuleId;
            public string KitId;
            public string ModuleFamily;
            public double GridStepM;
            public int[] FootprintCells;
            public string PlacementOrigin;
            public double[] BoundsMinYUp;
            public double[] BoundsMaxYUp;
            public string CollisionProxyShape;
            public bool NavBlocker;
            public SocketSpec[] Sockets;
            public string ContractSha256;
            public string SourceGlbSha256;
        }

        public sealed class ModuleResult
        {
            public string ModuleId;
            public bool Ok;
            public string Error = "";
            public readonly List<string> Changed = new List<string>();
            public readonly List<string> Promoted = new List<string>();
            public bool SkippedHashMatch;
        }

        // ------------------------------------------------------------------ pure checks (unit-tested)

        public static bool IsAllowlisted(string moduleId) => moduleId != null && Array.IndexOf(ModuleIds, moduleId) >= 0;

        static void ValidateModuleId(string moduleId)
        {
            if (!IsAllowlisted(moduleId)) throw new PromotionException($"unsupported structural source module: '{moduleId}'");
        }

        public static string VariantFileName(string moduleId, string variant)
        {
            foreach (var (v, suffix) in Variants)
                if (v == variant) return moduleId + suffix + ".glb";
            throw new PromotionException($"unsupported structural source variant: '{variant}'");
        }

        /// <summary><c>_validate_glb_file</c>: 12-byte header "glTF", version 2, declared length equals the file size.</summary>
        public static bool IsValidGlbHeader(byte[] header, long fileSize)
        {
            if (header == null || header.Length < 12) return false;
            if (header[0] != (byte)'g' || header[1] != (byte)'l' || header[2] != (byte)'T' || header[3] != (byte)'F') return false;
            uint version = BitConverter.ToUInt32(header, 4);
            uint length = BitConverter.ToUInt32(header, 8);
            return version == 2 && length == fileSize;
        }

        public static void ValidateGlbFile(string path, string moduleId)
        {
            if (!string.Equals(Path.GetExtension(path), ".glb", StringComparison.Ordinal))
                throw new PromotionException($"invalid source GLB extension: {moduleId}");
            byte[] header;
            long size;
            try
            {
                size = new FileInfo(path).Length;
                using (var stream = File.OpenRead(path))
                {
                    header = new byte[12];
                    int read = stream.Read(header, 0, 12);
                    if (read != 12) header = Array.Empty<byte>();
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new PromotionException($"cannot read source GLB: {moduleId}");
            }
            if (!IsValidGlbHeader(header, size)) throw new PromotionException($"invalid source GLB: {moduleId}");
        }

        public static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return Hex(sha.ComputeHash(stream));
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        static string Hex(byte[] hash) => string.Concat(hash.Select(b => b.ToString("x2")));

        /// <summary><c>should_skip_promotion</c>: identical staged/runtime GLBs are left untouched unless forced.</summary>
        public static bool ShouldSkipPromotion(string staged, string runtime, bool force)
        {
            if (force || !File.Exists(staged) || !File.Exists(runtime)) return false;
            return Sha256Hex(staged) == Sha256Hex(runtime);
        }

        /// <summary>
        /// <c>_load_spec_from_paths</c>: strict contract validation plus the runtime GLB check. Throws
        /// <see cref="PromotionException"/> with the Godot tool's messages.
        /// </summary>
        public static SourceSpec LoadSourceSpec(string moduleId, string contractPath, string sourceGlbPath)
        {
            ValidateModuleId(moduleId);
            byte[] contractBytes;
            try { contractBytes = File.ReadAllBytes(contractPath); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new PromotionException($"cannot read structural contract: {moduleId}");
            }
            return ParseSourceSpec(moduleId, contractBytes, sourceGlbPath);
        }

        public static SourceSpec ParseSourceSpec(string moduleId, byte[] contractBytes, string sourceGlbPath)
        {
            JToken token;
            try
            {
                string text = new UTF8Encoding(false, true).GetString(contractBytes);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                {
                    token = JToken.ReadFrom(reader);
                    if (reader.Read()) throw new JsonReaderException("trailing content");
                }
            }
            catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
            {
                throw new PromotionException($"invalid structural contract JSON: {moduleId}");
            }
            if (!(token is JObject doc)) throw new PromotionException($"invalid structural contract document: {moduleId}");

            if (!StringEquals(doc["document_kind"], "modular_asset_spec")) throw new PromotionException($"invalid structural contract kind: {moduleId}");
            if (!StringEquals(doc["module_id"], moduleId)) throw new PromotionException($"contract module_id mismatch: {moduleId}");
            if (!StringEquals(doc["schema_version"], "1.0.0")) throw new PromotionException($"invalid structural contract schema_version: {moduleId}");
            if (!StringEquals(doc["asset_id"], moduleId)) throw new PromotionException($"contract asset_id mismatch: {moduleId}");

            var spec = new SourceSpec { ModuleId = moduleId };
            spec.KitId = NonEmptyString(doc["kit_id"], "kit_id");
            spec.ModuleFamily = NonEmptyString(doc["module_family"], "module_family");
            spec.GridStepM = FiniteNumber(doc["grid_step_m"], "grid_step_m");
            if (spec.GridStepM <= 0.0) throw new PromotionException("invalid structural contract grid_step_m");

            if (!(doc["bounds"] is JObject bounds)) throw new PromotionException($"invalid structural contract bounds: {moduleId}");
            spec.BoundsMinYUp = Coordinates(bounds["local_min_m"], "bounds.local_min_m");
            spec.BoundsMaxYUp = Coordinates(bounds["local_max_m"], "bounds.local_max_m");
            for (int i = 0; i < 3; i++)
                if (spec.BoundsMinYUp[i] > spec.BoundsMaxYUp[i]) throw new PromotionException($"invalid structural contract bounds: {moduleId}");
            spec.PlacementOrigin = NonEmptyString(bounds["placement_origin"], "bounds.placement_origin");

            if (!(doc["footprint_cells"] is JArray footprint) || footprint.Count != 2 || footprint.Any(t => t.Type != JTokenType.Integer))
                throw new PromotionException("invalid structural contract footprint_cells");
            spec.FootprintCells = new[] { (int)footprint[0], (int)footprint[1] };

            if (!(doc["sockets"] is JArray rawSockets)) throw new PromotionException($"invalid structural contract sockets: {moduleId}");
            spec.Sockets = rawSockets.Select((socket, index) => ParseSocket(socket, index)).ToArray();
            if (spec.Sockets.Select(s => s.SocketId).Distinct(StringComparer.Ordinal).Count() != spec.Sockets.Length)
                throw new PromotionException($"duplicate structural contract socket id: {moduleId}");

            JToken collisionToken = doc["collision"];
            JObject collision = collisionToken == null || collisionToken.Type == JTokenType.Null ? new JObject() : collisionToken as JObject;
            if (collision == null) throw new PromotionException($"invalid structural contract collision: {moduleId}");
            spec.CollisionProxyShape = collision.ContainsKey("proxy_shape") ? NonEmptyString(collision["proxy_shape"], "collision.proxy_shape") : "box";
            JToken nav = collision["nav_blocker"];
            if (nav == null) spec.NavBlocker = false;
            else if (nav.Type == JTokenType.Boolean) spec.NavBlocker = (bool)nav;
            else throw new PromotionException($"invalid structural contract collision.nav_blocker: {moduleId}");

            if (string.IsNullOrEmpty(sourceGlbPath) || !File.Exists(sourceGlbPath)) throw new PromotionException($"missing source GLB: {moduleId}");
            ValidateGlbFile(sourceGlbPath, moduleId);
            spec.ContractSha256 = Sha256Hex(contractBytes);
            spec.SourceGlbSha256 = Sha256Hex(sourceGlbPath);
            return spec;
        }

        static SocketSpec ParseSocket(JToken token, int index)
        {
            string prefix = $"sockets[{index}]";
            if (!(token is JObject socket)) throw new PromotionException($"invalid structural contract {prefix}");
            var spec = new SocketSpec
            {
                SocketId = NonEmptyString(socket["id"], prefix + ".id"),
                Kind = NonEmptyString(socket["kind"], prefix + ".kind"),
            };
            if (!(socket["compatible_kinds"] is JArray compatible) || compatible.Count == 0)
                throw new PromotionException($"invalid structural contract {prefix}.compatible_kinds");
            spec.CompatibleKinds = compatible.Select(k => NonEmptyString(k, prefix + ".compatible_kinds")).ToArray();
            spec.PositionYUp = Coordinates(socket["position_m"], prefix + ".position_m");
            return spec;
        }

        static bool StringEquals(JToken token, string expected) => token != null && token.Type == JTokenType.String && (string)token == expected;

        static string NonEmptyString(JToken token, string field)
        {
            if (token == null || token.Type != JTokenType.String || ((string)token).Trim().Length == 0)
                throw new PromotionException($"invalid structural contract {field}");
            return ((string)token).Trim();
        }

        static double FiniteNumber(JToken token, string field)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new PromotionException($"invalid structural contract {field}");
            double value = (double)token;
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new PromotionException($"invalid structural contract {field}");
            return value;
        }

        static double[] Coordinates(JToken token, string field)
        {
            if (!(token is JArray array) || array.Count != 3) throw new PromotionException($"invalid structural contract {field}");
            return new[] { FiniteNumber(array[0], field), FiniteNumber(array[1], field), FiniteNumber(array[2], field) };
        }

        /// <summary><c>_copy_atomic</c>: temporary sibling then replace, so an interrupted copy never leaves a partial GLB.</summary>
        public static void CopyAtomic(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = Path.Combine(Path.GetDirectoryName(destination), "." + Path.GetFileName(destination) + ".promote.tmp");
            if (File.Exists(temporary)) File.Delete(temporary);
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        // ------------------------------------------------------------------ editor pipeline

        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static string ContractPath(string moduleId) =>
            Path.Combine(Application.streamingAssetsPath, "data", "placement", "contracts", "structural", KitId, moduleId + "_contract.json");

        public static string RuntimeGlbAssetPath(string moduleId, string variant) => $"{RuntimeAssetRoot}/{moduleId}/{VariantFileName(moduleId, variant)}";

        static string FullPath(string assetPath) => Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        [MenuItem("Synaptic Sea/Content/Promote Structural Source…")]
        public static void RunMenu()
        {
            string staging = EditorUtility.OpenFolderPanel("Staging root (contains <module_id>/<module_id>*.glb)", ProjectRoot, "");
            if (string.IsNullOrEmpty(staging)) return;
            var modules = ModuleIds.Where(id => Variants.Any(v => File.Exists(Path.Combine(staging, id, VariantFileName(id, v.variant))))).ToList();
            if (modules.Count == 0)
            {
                EditorUtility.DisplayDialog("Promote Structural Source", "No staged structural GLBs under " + staging, "OK");
                return;
            }
            int choice = EditorUtility.DisplayDialogComplex("Promote Structural Source",
                $"Staged modules: {string.Join(", ", modules)}\n\nDry run checks everything and changes nothing.", "Promote", "Cancel", "Dry run");
            if (choice == 1) return;
            Promote(modules, staging, dryRun: choice == 2, force: false, importSmoke: true);
        }

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string staging = Arg(args, "-stagingRoot");
            var modules = new List<string>();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-module") modules.Add(args[i + 1]);
            if (args.Contains("-all")) modules = ModuleIds.ToList();
            int code;
            if (string.IsNullOrEmpty(staging) || modules.Count == 0)
            {
                Debug.LogError("[PromoteStructuralSource] usage: -stagingRoot <dir> (-module <id>... | -all) [-dryRun] [-force] [-skipImportSmoke]");
                code = 2;
            }
            else
            {
                var results = Promote(modules, staging, args.Contains("-dryRun"), args.Contains("-force"), !args.Contains("-skipImportSmoke"));
                code = results.All(r => r.Ok) ? 0 : 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static List<ModuleResult> Promote(IReadOnlyList<string> moduleIds, string stagingRoot, bool dryRun, bool force, bool importSmoke)
        {
            var results = new List<ModuleResult>();
            string stagingFull = Path.GetFullPath(stagingRoot);
            bool anyPromoted = false;
            foreach (string moduleId in moduleIds)
            {
                var result = new ModuleResult { ModuleId = moduleId };
                results.Add(result);
                try
                {
                    ValidateModuleId(moduleId);
                    string intact = FullPath(RuntimeGlbAssetPath(moduleId, "intact"));
                    SourceSpec spec = LoadSourceSpec(moduleId, ContractPath(moduleId), intact);

                    string stagedDir = Path.Combine(stagingFull, moduleId);
                    var staged = new List<(string variant, string path)>();
                    foreach (var (variant, _) in Variants)
                    {
                        string path = Path.Combine(stagedDir, VariantFileName(moduleId, variant));
                        if (!File.Exists(path)) continue;
                        if (new FileInfo(path).Length <= 0) throw new PromotionException("staged GLB is empty: " + path);
                        ValidateGlbFile(path, moduleId);
                        staged.Add((variant, path));
                    }
                    if (staged.Count == 0) throw new PromotionException($"no staged GLBs for {moduleId} under {stagedDir}");

                    var changed = staged.Where(s => !ShouldSkipPromotion(s.path, FullPath(RuntimeGlbAssetPath(moduleId, s.variant)), force)).ToList();
                    result.Changed.AddRange(changed.Select(c => c.variant));
                    Debug.Log($"[PromoteStructuralSource] STRUCTURAL_PROMOTION_PLAN module={moduleId} source_spec=loaded contract_sha256={spec.ContractSha256} " +
                              $"staging={stagedDir} staged={string.Join(",", staged.Select(s => Path.GetFileName(s.path)))} changed={changed.Count} dry_run={dryRun.ToString().ToLowerInvariant()}");
                    if (changed.Count == 0)
                    {
                        result.SkippedHashMatch = true;
                        result.Ok = true;
                        Debug.Log($"[PromoteStructuralSource] STRUCTURAL_PROMOTED_SKIP module={moduleId} reason=hash_match");
                        continue;
                    }

                    if (importSmoke) RunImportSmoke(moduleId, changed);
                    foreach (var (variant, path) in changed)
                    {
                        string assetPath = RuntimeGlbAssetPath(moduleId, variant);
                        if (!dryRun)
                        {
                            CopyAtomic(path, FullPath(assetPath));
                            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                            anyPromoted = true;
                        }
                        result.Promoted.Add(assetPath);
                    }
                    result.Ok = true;
                    Debug.Log($"[PromoteStructuralSource] STRUCTURAL_PROMOTED module={moduleId} glbs={result.Promoted.Count} dry_run={dryRun.ToString().ToLowerInvariant()}");
                }
                catch (Exception e) when (e is PromotionException || e is IOException || e is UnauthorizedAccessException)
                {
                    result.Ok = false;
                    result.Error = e.Message;
                    Debug.LogError($"[PromoteStructuralSource] ERROR: {moduleId}: {e.Message}");
                }
            }

            if (anyPromoted)
            {
                int errors = RebuildPrefabs();
                if (errors > 0)
                {
                    foreach (var r in results.Where(r => r.Ok && r.Promoted.Count > 0))
                    {
                        r.Ok = false;
                        r.Error = $"structural prefab rebuild reported {errors} error(s)";
                    }
                    Debug.LogError($"[PromoteStructuralSource] ERROR: structural prefab rebuild reported {errors} error(s); see builds/logs/structural-prefab-report-{KitId}.json");
                }
            }
            int glbs = results.Sum(r => r.Promoted.Count);
            string status = results.All(r => r.Ok) ? "PASS" : "FAIL";
            Debug.Log($"[PromoteStructuralSource] STRUCTURAL_PROMOTION_SUMMARY {status} modules={results.Count} glbs={glbs} " +
                      $"failed={results.Count(r => !r.Ok)} dry_run={dryRun.ToString().ToLowerInvariant()}");
            return results;
        }

        /// <summary>
        /// The Godot overlay import smoke, done with the AssetDatabase: import the changed GLBs into a disposable folder,
        /// require a GameObject with at least one renderer and no error logged during import, then delete the folder.
        /// </summary>
        static void RunImportSmoke(string moduleId, List<(string variant, string path)> changed)
        {
            string folder = $"{SmokeAssetRoot}/{moduleId}";
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
            }
            Application.logMessageReceived += OnLog;
            try
            {
                Directory.CreateDirectory(FullPath(folder));
                foreach (var (variant, path) in changed)
                {
                    string assetPath = $"{folder}/{VariantFileName(moduleId, variant)}";
                    File.Copy(path, FullPath(assetPath), true);
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (model == null) throw new PromotionException($"import smoke failed for {moduleId}: {assetPath} produced no GameObject");
                    int renderers = model.GetComponentsInChildren<Renderer>(true).Length;
                    if (renderers == 0) throw new PromotionException($"import smoke failed for {moduleId}: {assetPath} has no renderers");
                    if (errors.Count > 0) throw new PromotionException($"import smoke failed for {moduleId}: {errors[0]}");
                    Debug.Log($"[PromoteStructuralSource] STRUCTURAL_IMPORT_SMOKE module={moduleId} variant={variant} renderers={renderers}");
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                AssetDatabase.DeleteAsset(SmokeAssetRoot);
                if (Directory.Exists(FullPath(SmokeAssetRoot))) Directory.Delete(FullPath(SmokeAssetRoot), true);
                string meta = FullPath(SmokeAssetRoot) + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
            }
        }

        /// <summary>Rebuilds the kit's prefabs and returns the builder report's error count.</summary>
        static int RebuildPrefabs()
        {
            StructuralPrefabBuilder.Build(KitId, strict: false, exitWhenDone: false);
            string report = Path.Combine(ProjectRoot, "..", "builds", "logs", $"structural-prefab-report-{KitId}.json");
            var doc = File.Exists(report) ? GdJson.ParseDict(File.ReadAllText(report)) : null;
            return doc == null ? 1 : doc.GetArrayOrEmpty("errors").Count;
        }
    }
}
