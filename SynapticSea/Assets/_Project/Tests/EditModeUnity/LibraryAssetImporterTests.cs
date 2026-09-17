using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.EditorTools.Content;

namespace SynapticSea.Tests
{
    public class LibraryAssetImporterTests
    {
        string _tmp, _library, _project;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "ss-library-" + Guid.NewGuid().ToString("N"));
            _library = Path.Combine(_tmp, "art-library");
            _project = Path.Combine(_tmp, "SynapticSea");
            Directory.CreateDirectory(Path.Combine(_library, "Sci Fi Pack", "props", "crate_textures"));
            Directory.CreateDirectory(_project);
            File.WriteAllText(Path.Combine(_library, "Sci Fi Pack", "props", "crate.glb"), "glb");
            File.WriteAllText(Path.Combine(_library, "Sci Fi Pack", "props", "crate.png"), "png");
            File.WriteAllText(Path.Combine(_library, "Sci Fi Pack", "props", "crate.glb.meta"), "meta");
            File.WriteAllText(Path.Combine(_library, "Sci Fi Pack", "props", "crate_old.glb"), "other");
            File.WriteAllText(Path.Combine(_library, "Sci Fi Pack", "props", "crate_textures", "albedo.png"), "albedo");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
        }

        [Test]
        public void PlanTakesSameStemSiblingsAndTextureFolderIntoTheCategory()
        {
            var plan = LibraryAssetImporter.Plan(_library, Path.Combine(_library, "Sci Fi Pack", "props", "crate.glb"));
            Assert.AreEqual("sci_fi_pack", plan.Category);
            CollectionAssert.AreEquivalent(new[]
            {
                "Assets/Content/Library/sci_fi_pack/crate.glb",
                "Assets/Content/Library/sci_fi_pack/crate.png",
                "Assets/Content/Library/sci_fi_pack/crate_textures/albedo.png",
            }, plan.Files.Select(f => f.destination));
            Assert.AreEqual("props", LibraryAssetImporter.Plan(_library, Path.Combine(_library, "Sci Fi Pack", "props", "crate.glb"), "Props").Category);
            Assert.AreEqual("misc", LibraryAssetImporter.SanitizeCategory("  // "));
        }

        [Test]
        public void ExecuteCopiesOnceSkipsIdenticalAndRefusesConflicts()
        {
            var plan = LibraryAssetImporter.Plan(_library, Path.Combine(_library, "Sci Fi Pack", "props", "crate.glb"));
            Assert.AreEqual(3, LibraryAssetImporter.Execute(plan, _project).Count);
            Assert.AreEqual(0, LibraryAssetImporter.Execute(plan, _project).Count);
            File.WriteAllText(Path.Combine(_project, "Assets", "Content", "Library", "sci_fi_pack", "crate.png"), "changed");
            File.Delete(Path.Combine(_project, "Assets", "Content", "Library", "sci_fi_pack", "crate.glb"));
            Assert.Throws<IOException>(() => LibraryAssetImporter.Execute(plan, _project));
            Assert.IsFalse(File.Exists(Path.Combine(_project, "Assets", "Content", "Library", "sci_fi_pack", "crate.glb")), "nothing is copied when a conflict exists");
        }

        [Test]
        public void SourcesOutsideTheLibraryOrAMissingLibraryAreRejected()
        {
            string outside = Path.Combine(_tmp, "loose.glb");
            File.WriteAllText(outside, "x");
            Assert.Throws<ArgumentException>(() => LibraryAssetImporter.Plan(_library, outside));
            Assert.Throws<DirectoryNotFoundException>(() => LibraryAssetImporter.Plan(Path.Combine(_tmp, "nope"), outside));
        }
    }
}
