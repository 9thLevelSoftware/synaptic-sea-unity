using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Tests
{
    /// <summary>
    /// The player's build kind comes from <c>StreamingAssets/build_stamp.json</c>, which Builder.PerformBuild writes for
    /// the build and deletes afterwards. A stamp left in the project would make the editor (and every test run) report
    /// the last built kind, so a demo build would silently put the editor behind the demo scope gate.
    /// </summary>
    public class BuildStampTests
    {
        [Test]
        public void NoBuildStampIsLeftInTheProject()
        {
            string stamp = Path.Combine(Application.streamingAssetsPath, "build_stamp.json");
            Assert.IsFalse(File.Exists(stamp),
                "StreamingAssets/build_stamp.json exists outside a build (a leftover from an older Builder or an interrupted build); delete it");
        }

        [Test]
        public void EditorReportsTheManifestBuildKind()
        {
            GdDict manifest = GdJson.ParseDict(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "data", "release", "build_metadata.json")));
            var meta = new BuildMetadataState();
            meta.Configure(manifest);
            Assert.AreEqual("dev", meta.GetBuildKind(), "the synced build_metadata.json is the dev manifest; builds override the kind through the stamp");
        }
    }
}
