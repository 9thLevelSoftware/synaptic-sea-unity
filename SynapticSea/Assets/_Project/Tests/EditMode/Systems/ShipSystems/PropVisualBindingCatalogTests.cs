using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class PropVisualBindingCatalogTests
    {
        IStorage _previousStorage;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            _previousStorage = CoreServices.UserStorage;
            CoreServices.UserStorage = new MemoryStorage();
        }

        [TearDown]
        public void TearDown()
        {
            CoreServices.UserStorage = _previousStorage;
            CatalogRegistry.Clear();
        }

        GdDict ValidDocument(PropVisualBindingCatalog catalog) => new GdDict
        {
            { "schema_version", "1.0.0" },
            { "document_kind", "prop_visual_binding_index" },
            { "components", new GdDict { { "reactor_console", catalog.GetComponentBinding("reactor_console") } } },
            { "objectives", new GdDict { { "reactor_control_panel", catalog.GetObjectiveBinding("reactor_control_panel") } } },
            { "dressing", new GdDict { { "cable_tray", catalog.GetDressingBinding("cable_tray") } } },
        };

        static PropVisualBindingCatalog LoadRaw(string text)
        {
            CoreServices.UserStorage.WriteText("user://probe.json", text);
            var probe = new PropVisualBindingCatalog();
            probe.LoadFromPath("user://probe.json");
            return probe;
        }

        [Test]
        public void LoadsGeneratedIndex_AndResolvesBindings()
        {
            var catalog = new PropVisualBindingCatalog();
            Assert.IsTrue(catalog.LoadFromPath(), string.Join("\n", catalog.GetErrors()));
            Assert.IsFalse(catalog.GetComponentBinding("reactor_console").IsEmpty);
            Assert.IsFalse(catalog.GetObjectiveBinding("reactor_control_panel").IsEmpty);
            GdDict dressing = catalog.GetDressingBinding("cable_tray");
            Assert.AreEqual("visual_prop_id", dressing.GetDict("binding").GetString("namespace"));
            Assert.IsTrue(catalog.GetComponentBinding("missing_component").IsEmpty);
            Assert.IsTrue(catalog.GetObjectiveBinding("bridge_power_distribution").IsEmpty);
        }

        [Test]
        public void ValidDocumentAccepted_MalformedVariantsRejected()
        {
            var catalog = new PropVisualBindingCatalog();
            Assert.IsTrue(catalog.LoadFromPath());
            GdDict valid = ValidDocument(catalog);
            Assert.IsTrue(LoadRaw(GdJson.Stringify(valid)).GetErrors().Count == 0);

            GdDict badSchema = valid.DeepCopy();
            badSchema["schema_version"] = "1.0";
            var probe = LoadRaw(GdJson.Stringify(badSchema));
            Assert.IsNotEmpty(probe.GetErrors());

            GdDict badPath = valid.DeepCopy();
            ((GdDict)((GdDict)badPath["components"])["reactor_console"])["visual_scene_path"] =
                "res://assets/imported/props/components/../reactor_console.glb";
            Assert.Contains("catalog binding components/reactor_console scene path must be canonical imported prop GLB",
                LoadRaw(GdJson.Stringify(badPath)).GetErrors());

            GdDict badSurface = valid.DeepCopy();
            ((GdDict)((GdDict)((GdDict)badSurface["dressing"])["cable_tray"])["placement"])["surface"] = "deck";
            Assert.Contains("catalog binding dressing/cable_tray surface must be floor, wall, or ceiling",
                LoadRaw(GdJson.Stringify(badSurface)).GetErrors());
        }

        [Test]
        public void DuplicateJsonKeyRejected()
        {
            var catalog = new PropVisualBindingCatalog();
            Assert.IsTrue(catalog.LoadFromPath());
            string serialized = GdJson.Stringify(ValidDocument(catalog));
            const string token = "\"schema_version\":\"1.0.0\",";
            int offset = serialized.IndexOf(token, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(offset, 0);
            string duplicated = serialized.Substring(0, offset) + token + serialized.Substring(offset);
            var probe = new PropVisualBindingCatalog();
            CoreServices.UserStorage.WriteText("user://dup.json", duplicated);
            Assert.IsFalse(probe.LoadFromPath("user://dup.json"));
            Assert.Contains("catalog JSON contains duplicate JSON object key", probe.GetErrors());
        }
    }
}
