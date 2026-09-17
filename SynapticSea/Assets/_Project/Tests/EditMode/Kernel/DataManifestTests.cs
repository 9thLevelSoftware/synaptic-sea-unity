using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Services;

namespace SynapticSea.Tests.Kernel
{
    /// <summary>
    /// The synced Godot data (StreamingAssets/data) must parse with the Godot-compatible reader, and every
    /// <c>res://data/...</c> reference inside it must resolve. The checks live in <see cref="DataManifestValidator"/>, which
    /// the player Builder runs before every build. Asset references (<c>res://assets</c>, <c>res://scenes</c>) are resolved
    /// by content catalogs; their count is reported, not asserted.
    /// </summary>
    public class DataManifestTests
    {
        static DataManifestReport Validate()
        {
            // StreamingAssets/data is checked in; only a checkout without StreamingAssets at all is skipped.
            if (!Directory.Exists(Fixtures.StreamingDataRoot)) Assert.Ignore("SynapticSea/Assets/StreamingAssets is absent (stripped checkout)");
            DataManifestReport report = DataManifestValidator.Validate(Fixtures.StreamingDataRoot);
            Assert.IsEmpty(report.RootError, "StreamingAssets/data is missing; run tools/sync-godot-data.ps1");
            return report;
        }

        [Test]
        public void EveryDataFileParses()
        {
            DataManifestReport report = Validate();
            Assert.That(report.FileCount, Is.GreaterThan(100), "expected the full Godot data set");
            Assert.IsEmpty(report.ParseErrors, string.Join("\n", report.ParseErrors));
        }

        [Test]
        public void EveryResDataReferenceResolves()
        {
            DataManifestReport report = Validate();
            TestContext.WriteLine($"non-data res:// references (resolved by content catalogs later): {report.AssetReferenceCount}");
            Assert.IsEmpty(report.MissingDataReferences, "unresolved res://data JSON references:\n" + string.Join("\n", report.MissingDataReferences));
        }

        [Test]
        public void MissingDataRootIsReportedNotThrown()
        {
            DataManifestReport report = DataManifestValidator.Validate(Path.Combine(Path.GetTempPath(), "synaptic-no-such-streaming-assets"));
            Assert.IsFalse(report.IsValid);
            StringAssert.Contains("StreamingAssets/data missing", report.Describe());
        }
    }
}
