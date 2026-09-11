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

        /// <summary>Skips (Inconclusive) when a fixture has not been captured yet.</summary>
        public static void Require(string relative)
        {
            if (!Exists(relative)) Assert.Ignore($"fixture not captured yet: fixtures/{relative}");
        }
    }
}
