using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Layout and path contract for the Art Director iso stills. The Editor runner
    /// (<c>IthappyKitContactSheet.Run</c>) consumes this spec; it needs a GPU.
    /// </summary>
    public class IthappyKitContactSheetSpecTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreServices.Log = new CollectingLog();
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void Spec_ListsAllSixteenKitModules_IncludingHighlights()
        {
            List<string> ids = IthappyKitContactSheetSpec.ModuleIdsFromKit(Fixtures.StreamingDataRoot);
            Assert.AreEqual(16, ids.Count);
            CollectionAssert.Contains(ids, "wall_x_junction");
            CollectionAssert.IsSubsetOf(IthappyKitContactSheetSpec.HighlightModuleIds, ids);
            CollectionAssert.AreEqual(new[]
            {
                "floor_1x1", "wall_straight_1x1", "wall_t_junction", "wall_x_junction",
            }, IthappyKitContactSheetSpec.HighlightModuleIds.ToArray());
        }

        [Test]
        public void Spec_WritesUnderArtifactsScreenshotsKitFolder()
        {
            string root = Fixtures.RepoRoot;
            Assert.AreEqual(
                Path.Combine(root, "artifacts", "screenshots", KitCatalog.ITHAPPY_KIT_ID),
                IthappyKitContactSheetSpec.OutputDirectory(root));
            Assert.AreEqual(
                Path.Combine(root, "artifacts", "screenshots", KitCatalog.ITHAPPY_KIT_ID, "contact_sheet.png"),
                IthappyKitContactSheetSpec.ContactSheetPath(root));
            Assert.AreEqual(
                Path.Combine(root, "artifacts", "screenshots", KitCatalog.ITHAPPY_KIT_ID, "wall_x_junction.png"),
                IthappyKitContactSheetSpec.PerModulePath(root, "wall_x_junction"));
            Assert.AreEqual(
                "Assets/Content/Prefabs/Structural/ithappy_scifi_v0/wall_x_junction.prefab",
                IthappyKitContactSheetSpec.PrefabAssetPath("wall_x_junction"));
        }

        [Test]
        public void Spec_LocksTrueIsoFraming_PitchYawAndOrthoBand()
        {
            Assert.AreEqual(35.26438968f, IthappyKitContactSheetSpec.PitchDownDegrees, 1e-5f);
            Assert.AreEqual(45f, IthappyKitContactSheetSpec.YawBiasDegrees);
            Assert.AreEqual(18f, IthappyKitContactSheetSpec.OrthographicSizeMin);
            Assert.AreEqual(22f, IthappyKitContactSheetSpec.OrthographicSizeMax);
            Assert.AreEqual(22f, IthappyKitContactSheetSpec.ContactSheetOrthographicSize);
            Assert.AreEqual(18f, IthappyKitContactSheetSpec.PerModuleOrthographicSize);
            Assert.That(IthappyKitContactSheetSpec.ContactSheetOrthographicSize,
                Is.InRange(IthappyKitContactSheetSpec.OrthographicSizeMin, IthappyKitContactSheetSpec.OrthographicSizeMax));
            Assert.That(IthappyKitContactSheetSpec.PerModuleOrthographicSize,
                Is.InRange(IthappyKitContactSheetSpec.OrthographicSizeMin, IthappyKitContactSheetSpec.OrthographicSizeMax));

            IthappyKitContactSheetSpec.LockedIsoOffset(1f, out float x, out float y, out float z);
            // Unity octant matches play iso after Frame (north up-left): -X, +Y, +Z.
            Assert.Less(x, 0f);
            Assert.Greater(y, 0f);
            Assert.Greater(z, 0f);
            Assert.AreEqual(Math.Abs(x), z, 1e-5f);
            Assert.AreEqual(Math.Abs(x), y, 1e-5f);
            float horizontal = (float)Math.Sqrt(x * x + z * z);
            float pitch = (float)(Math.Atan(y / horizontal) * (180.0 / Math.PI));
            Assert.AreEqual(IthappyKitContactSheetSpec.PitchDownDegrees, pitch, 1e-3f);
            float yaw = (float)(Math.Atan2(Math.Abs(x), z) * (180.0 / Math.PI));
            Assert.AreEqual(IthappyKitContactSheetSpec.YawBiasDegrees, yaw, 1e-3f);
            IthappyKitContactSheetSpec.GodotEqualAxisOffset(IthappyKitContactSheetSpec.CameraDistanceM,
                out float gx, out float gy, out float gz);
            Assert.AreEqual(IthappyKitContactSheetSpec.CameraDistanceM, gx, 1e-5f);
            Assert.AreEqual(IthappyKitContactSheetSpec.CameraDistanceM, gy, 1e-5f);
            Assert.AreEqual(IthappyKitContactSheetSpec.CameraDistanceM, gz, 1e-5f);
        }

        [Test]
        public void Spec_PlacesModulesOnAFourColumnGrid()
        {
            IthappyKitContactSheetSpec.GridOffset(0, out float x0, out float z0);
            IthappyKitContactSheetSpec.GridOffset(1, out float x1, out float z1);
            IthappyKitContactSheetSpec.GridOffset(4, out float x4, out float z4);
            Assert.AreEqual(0f, x0);
            Assert.AreEqual(0f, z0);
            Assert.AreEqual(IthappyKitContactSheetSpec.CellSpacingM, x1, 1e-5f);
            Assert.AreEqual(0f, z1);
            Assert.AreEqual(0f, x4);
            Assert.AreEqual(IthappyKitContactSheetSpec.CellSpacingM, z4, 1e-5f);
        }

        [Test]
        public void BakedIthappyPrefabs_ExistOnDiskForEveryKitModule()
        {
            string folder = Path.Combine(Fixtures.RepoRoot, "SynapticSea",
                IthappyKitContactSheetSpec.PrefabFolder.Replace('/', Path.DirectorySeparatorChar));
            var missing = new List<string>();
            foreach (string id in IthappyKitContactSheetSpec.ModuleIdsFromKit(Fixtures.StreamingDataRoot))
            {
                if (!File.Exists(Path.Combine(folder, id + ".prefab"))) missing.Add(id);
            }
            Assert.IsEmpty(missing, "ithappy bake missing prefabs:\n" + string.Join("\n", missing));
        }
    }
}
