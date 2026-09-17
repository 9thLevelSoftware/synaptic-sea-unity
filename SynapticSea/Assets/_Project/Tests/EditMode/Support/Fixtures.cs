using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests
{
    /// <summary>
    /// Locates the repo-level <c>fixtures/</c> folder (Godot parity captures) and <c>StreamingAssets/data</c>
    /// from either the Unity project root (EditMode) or a dotnet test bin folder.
    /// </summary>
    public static class Fixtures
    {
        static string _repoRoot;

        public static string RepoRoot
        {
            get
            {
                if (_repoRoot != null) return _repoRoot;
                foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    var dir = new DirectoryInfo(start);
                    while (dir != null)
                    {
                        if (Directory.Exists(Path.Combine(dir.FullName, "SynapticSea", "Assets")) &&
                            Directory.Exists(Path.Combine(dir.FullName, "docs")))
                        {
                            _repoRoot = dir.FullName;
                            return _repoRoot;
                        }
                        dir = dir.Parent;
                    }
                }
                throw new DirectoryNotFoundException("Could not locate the repo root containing fixtures/ and SynapticSea/.");
            }
        }

        public static string FixturesDir => Path.Combine(RepoRoot, "fixtures");

        public static string StreamingDataRoot => Path.Combine(RepoRoot, "SynapticSea", "Assets", "StreamingAssets");

        public static string PathOf(string relative) => Path.Combine(FixturesDir, relative.Replace('/', Path.DirectorySeparatorChar));

        public static bool Exists(string relative) => File.Exists(PathOf(relative));

        public static string ReadText(string relative) => File.ReadAllText(PathOf(relative), new UTF8Encoding(false));

        /// <summary>
        /// Reads a fixture with exact numbers: integer literals stay long (64-bit states, hashes) and floats are
        /// correctly rounded (full-precision captures). Never read game data this way.
        /// </summary>
        public static GdDict ReadDict(string relative)
        {
            var d = GdJson.Parse(ReadText(relative), exactNumbers: true) as GdDict;
            Assert.IsNotNull(d, $"fixture {relative} is not a JSON object");
            return d;
        }

        /// <summary>Message used for the one allowed skip: the whole <c>fixtures/</c> tree is absent.</summary>
        public const string NoFixturesTreeMessage =
            "repo fixtures/ directory is absent (stripped checkout); Godot parity suites are skipped";

        /// <summary>
        /// Guards a fixture-driven test. When the repo's <c>fixtures/</c> tree exists a missing fixture is a real
        /// failure (a parity suite must never pass silently); only a checkout without any <c>fixtures/</c> directory
        /// is ignored, with <see cref="NoFixturesTreeMessage"/>.
        /// </summary>
        public static void Require(string relative)
        {
            RequireTree();
            if (!Exists(relative)) Assert.Fail($"fixture missing: fixtures/{relative} (fixtures/ exists, so every captured fixture must be present)");
        }

        /// <summary>Ignores the test only when the whole <c>fixtures/</c> tree is absent.</summary>
        public static void RequireTree()
        {
            if (!Directory.Exists(FixturesDir)) Assert.Ignore(NoFixturesTreeMessage);
        }
    }
}
