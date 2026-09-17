using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class RunDirectoryJanitorTests
    {
        MemoryStorage _storage;
        SaveLoadService _saves;

        [SetUp]
        public void SetUp()
        {
            _storage = new MemoryStorage();
            _saves = new SaveLoadService(_storage);
        }

        void WriteRun(string name)
        {
            foreach (string file in new[] { "layout.json", "gameplay_slice.json", "blueprint.json" })
                _storage.WriteText(RunDirectoryJanitor.RunsDir + "/" + name + "/" + file, "{}");
        }

        void WriteSlot(string file, GdDict payload) => _storage.WriteText(SaveLoadService.SAVES_DIR + "/" + file, GdJson.Stringify(payload, "\t"));

        static string Layout(string run) => RunDirectoryJanitor.RunsDir + "/" + run + "/layout.json";

        [Test]
        public void Sweep_DeletesDirectoriesNoSaveReferences()
        {
            WriteRun("run_a");
            WriteRun("run_b");
            WriteRun("run_c");
            WriteSlot("slot_01.json", new GdDict { { "slice_version", SaveMigrationService.TargetVersion }, { "layout_path", Layout("run_a") } });
            WriteSlot("world.json", new GdDict { { "slice_version", "world-4" }, { "home_ship", new GdDict { { "layout_path", Layout("run_b") } } } });

            GdArray deleted = new RunDirectoryJanitor(_storage, _saves).Sweep();

            Assert.AreEqual(1, deleted.Count);
            Assert.AreEqual("run_c", deleted[0]);
            CollectionAssert.AreEqual(new[] { "run_a", "run_b" }, _storage.ListDirectories(RunDirectoryJanitor.RunsDir).ToArray());
            Assert.IsFalse(_storage.FileExists(Layout("run_c")));
            Assert.IsTrue(_storage.FileExists(Layout("run_a")));
        }

        [Test]
        public void Sweep_KeepsTheActiveRunDirectory()
        {
            WriteRun("live");
            WriteRun("old");
            GdArray deleted = new RunDirectoryJanitor(_storage, _saves).Sweep(Layout("live"));
            Assert.AreEqual(1, deleted.Count);
            Assert.AreEqual("old", deleted[0]);
            Assert.IsTrue(_storage.DirExists(RunDirectoryJanitor.RunsDir + "/live"));
        }

        [Test]
        public void Sweep_IgnoresNonRunPathsAndLeavesSavesAlone()
        {
            WriteRun("orphan");
            WriteSlot("slot_02.json", new GdDict { { "layout_path", "res://data/procgen/golden/coherent_ship_001/layout.json" } });
            GdArray deleted = new RunDirectoryJanitor(_storage, _saves).Sweep();
            Assert.AreEqual(1, deleted.Count);
            Assert.IsTrue(_storage.FileExists(SaveLoadService.SAVES_DIR + "/slot_02.json"), "saves are never touched");
            Assert.IsTrue(_storage.DirExists(SaveLoadService.SAVES_DIR));
            Assert.AreEqual("", RunDirectoryJanitor.RunDirectoryName("res://data/procgen/golden/coherent_ship_001/layout.json"));
            Assert.AreEqual("x", RunDirectoryJanitor.RunDirectoryName("user://runs/x/layout.json"));
        }

        [Test]
        public void Sweep_WithoutARunsDirectoryDoesNothing()
        {
            Assert.AreEqual(0, new RunDirectoryJanitor(_storage, _saves).Sweep().Count);
        }

        [Test]
        public void MemoryStorage_ListsAndDeletesDirectories()
        {
            _storage.WriteText("user://a/b/c.json", "1");
            _storage.WriteText("user://a/d.json", "2");
            _storage.MakeDirRecursive("user://a/e");
            CollectionAssert.AreEqual(new[] { "b", "e" }, _storage.ListDirectories("user://a").ToArray());
            CollectionAssert.AreEqual(new[] { "a" }, _storage.ListDirectories("user://").ToArray());
            Assert.IsTrue(_storage.DeleteDirectory("user://a/b"));
            Assert.IsFalse(_storage.FileExists("user://a/b/c.json"));
            Assert.IsTrue(_storage.FileExists("user://a/d.json"));
            Assert.IsFalse(_storage.DirExists("user://a/b"));
            Assert.IsFalse(_storage.DeleteDirectory("user://a/b"));
        }

        [Test]
        public void FileSystemStorage_ListsAndDeletesDirectories()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ss_janitor_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var fs = new FileSystemStorage(root);
                fs.WriteText("user://runs/r1/layout.json", "{}");
                fs.WriteText("user://runs/r2/layout.json", "{}");
                CollectionAssert.AreEqual(new[] { "r1", "r2" }, fs.ListDirectories("user://runs").ToArray());
                Assert.IsTrue(fs.DeleteDirectory("user://runs/r1"));
                CollectionAssert.AreEqual(new[] { "r2" }, fs.ListDirectories("user://runs").ToArray());
                Assert.IsFalse(fs.DeleteDirectory("user://"), "the storage root is never deleted");
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }
    }
}
