using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Copies one asset on demand from the local third-party art library (<c>&lt;repo&gt;/art-library/</c>, git-ignored,
    /// never imported wholesale; plan Phase 7) into <c>Assets/Content/Library/&lt;category&gt;/</c>. The copy takes the chosen
    /// file, its same-stem siblings (<c>crate.glb</c> brings <c>crate.png</c>, <c>crate.sidecar.json</c>, …) and a same-stem
    /// texture folder (<c>crate/</c>, <c>crate_textures/</c> or <c>crate.fbm/</c>). The category is the first folder below
    /// <c>art-library/</c> unless one is given. Identical files are skipped; a different file already at the destination
    /// stops the import before anything is copied.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.LibraryAssetImporter.Run
    ///     -file art-library/&lt;pack&gt;/.../&lt;asset&gt;.glb [-category &lt;name&gt;]
    /// </summary>
    public static class LibraryAssetImporter
    {
        public const string LibraryFolderName = "art-library";
        public const string DestinationAssetRoot = "Assets/Content/Library";
        static readonly string[] TextureFolderSuffixes = { "", "_textures", ".fbm" };
        static readonly Regex UnsafeCategory = new Regex("[^a-z0-9_-]+");

        public sealed class ImportPlan
        {
            public string Category;
            /// <summary>(source file, destination path relative to the Unity project, e.g. Assets/Content/Library/…).</summary>
            public readonly List<(string source, string destination)> Files = new List<(string, string)>();
        }

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>Lower-case, filesystem-safe category name; "misc" when nothing usable is left.</summary>
        public static string SanitizeCategory(string name)
        {
            string safe = UnsafeCategory.Replace((name ?? "").Trim().ToLowerInvariant().Replace(' ', '_'), "_").Trim('_');
            return safe.Length == 0 ? "misc" : safe;
        }

        /// <summary>Plans the copy of <paramref name="sourceFile"/> (which must be a file below <paramref name="libraryRoot"/>).</summary>
        public static ImportPlan Plan(string libraryRoot, string sourceFile, string category = null)
        {
            string root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"{LibraryFolderName}/ not found at {root}");
            string source = Path.GetFullPath(sourceFile);
            if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"the asset must be inside {root}: {source}");
            if (!File.Exists(source)) throw new FileNotFoundException("library asset not found", source);

            string relative = source.Substring(root.Length + 1);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var plan = new ImportPlan { Category = SanitizeCategory(category ?? (parts.Length > 1 ? parts[0] : "misc")) };
            string destinationDir = $"{DestinationAssetRoot}/{plan.Category}";

            string dir = Path.GetDirectoryName(source);
            string stem = Path.GetFileNameWithoutExtension(source);
            foreach (string sibling in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(sibling);
                if (sibling.Equals(source, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    plan.Files.Add((sibling, $"{destinationDir}/{name}"));
                }
            }
            foreach (string suffix in TextureFolderSuffixes)
            {
                string folder = Path.Combine(dir, stem + suffix);
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string inner = file.Substring(folder.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                    plan.Files.Add((file, $"{destinationDir}/{stem + suffix}/{inner}"));
                }
            }
            return plan;
        }

        /// <summary>
        /// Copies the plan into <paramref name="projectRoot"/>: returns the destinations written. Throws before copying
        /// anything when a destination already holds different bytes.
        /// </summary>
        public static List<string> Execute(ImportPlan plan, string projectRoot)
        {
            var conflicts = plan.Files.Where(f => File.Exists(Dest(projectRoot, f.destination)) && !SameBytes(f.source, Dest(projectRoot, f.destination)))
                .Select(f => f.destination).ToList();
            if (conflicts.Count > 0) throw new IOException("destination already holds a different file: " + string.Join(", ", conflicts));
            var written = new List<string>();
            foreach (var (source, destination) in plan.Files)
            {
                string full = Dest(projectRoot, destination);
                if (File.Exists(full)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.Copy(source, full, false);
                written.Add(destination);
            }
            return written;
        }

        static string Dest(string projectRoot, string destination) => Path.Combine(projectRoot, destination.Replace('/', Path.DirectorySeparatorChar));

        static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        [MenuItem("Synaptic Sea/Content/Import Library Asset…")]
        public static void ImportMenu()
        {
            string libraryRoot = Path.Combine(RepoRoot, LibraryFolderName);
            if (!Directory.Exists(libraryRoot))
            {
                EditorUtility.DisplayDialog("Import Library Asset",
                    $"No art library at {libraryRoot}.\n\nThe third-party pack lives outside git: unpack it into {LibraryFolderName}/ next to SynapticSea/.", "OK");
                return;
            }
            string file = EditorUtility.OpenFilePanelWithFilters("Import library asset", libraryRoot,
                new[] { "Models and textures", "glb,gltf,fbx,obj,png,jpg,tga", "All files", "*" });
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var written = Import(libraryRoot, file, null);
                EditorUtility.DisplayDialog("Import Library Asset", written.Count == 0 ? "Already imported (identical files)." : "Imported:\n" + string.Join("\n", written), "OK");
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                EditorUtility.DisplayDialog("Import Library Asset", "Import failed: " + e.Message, "OK");
            }
        }

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string file = Arg("-file");
            int code = 0;
            try
            {
                if (file == null) throw new ArgumentException("usage: -file art-library/<pack>/.../<asset> [-category <name>]");
                string full = Path.IsPathRooted(file) ? file : Path.Combine(RepoRoot, file);
                Import(Path.Combine(RepoRoot, LibraryFolderName), full, Arg("-category"));
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                Debug.LogError("[LibraryAssetImporter] IMPORT FAIL " + e.Message);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static List<string> Import(string libraryRoot, string file, string category)
        {
            var plan = Plan(libraryRoot, file, category);
            var written = Execute(plan, ProjectRoot);
            foreach (string destination in written) AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[LibraryAssetImporter] IMPORT PASS category={plan.Category} files={plan.Files.Count} written={written.Count} " +
                      $"out={DestinationAssetRoot}/{plan.Category}");
            return written;
        }
    }
}
