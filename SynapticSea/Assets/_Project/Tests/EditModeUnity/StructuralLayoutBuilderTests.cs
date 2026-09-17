using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>
    /// Builds the Godot golden layouts in Unity and checks every placed socket against Godot's own placement math
    /// (the ported ModularSocketCatalog.WorldSocketPosition, float32 Basis) converted through Frame.
    /// </summary>
    public class StructuralLayoutBuilderTests
    {
        static readonly string[] Layouts =
        {
            "procgen/golden/coherent_ship_001/layout.json",
            "procgen/golden/coherent_ship_002/layout.json",
            "procgen/golden/coherent_ship_003/layout.json",
            "procgen/smoke/seed_000017/layout.json",
        };

        GameObject _parent;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            _parent = new GameObject("TestShip");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_parent);
            CatalogRegistry.Clear();
        }

        static KitPrefabCatalog Kit() => AssetDatabase.LoadAssetAtPath<KitPrefabCatalog>("Assets/Resources/Catalogs/KitCatalog_ship_structural_v0.asset");

        static GdDict LoadLayout(string rel) =>
            GdJson.ParseDict(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "data", rel)));

        [TestCaseSource(nameof(Layouts))]
        public void BuildsEveryPlacementWithUniqueModuleKeys(string layoutPath)
        {
            var layout = LoadLayout(layoutPath);
            var plan = layout.GetDict("structural_plan");
            var result = new StructuralLayoutBuilder().Build(layout, Kit(), _parent.transform);
            Assert.IsNotNull(result, "build failed");
            // Edges a relocated vertex wrapper's wing now walls build nothing of their own (VertexWrapperPlacement).
            int covered = VertexWrapperPlacement.Resolve(plan).Covered.Count;
            Assert.AreEqual(plan.GetArray("placements").Count - covered, result.EdgeCount);
            Assert.AreEqual(plan.GetArray("floor_placements").Count, result.FloorCount);
            Assert.AreEqual(plan.GetArrayOrEmpty("ceiling_placements").Count, result.CeilingCount);
            Assert.AreEqual(result.Modules.Count, result.ByModuleKey.Count, "module keys must be unique (Godot module_key meta)");
            Assert.IsTrue(result.Root.activeSelf);
        }

        [TestCaseSource(nameof(Layouts))]
        public void PlacedSocketsMatchGodotPlacementMath(string layoutPath)
        {
            var layout = LoadLayout(layoutPath);
            var result = new StructuralLayoutBuilder().Build(layout, Kit(), _parent.transform);
            Assert.IsNotNull(result);

            var catalog = new ModularSocketCatalog();
            Assert.IsTrue(catalog.LoadKit("ship_structural_v0"), "socket catalog failed to load contracts");

            var mismatches = new List<string>();
            int compared = 0;
            foreach (var module in result.Modules)
            {
                var godotPlacement = new Vec3(module.godotPosition.x, module.godotPosition.y, module.godotPosition.z);
                foreach (var socket in module.GetComponentsInChildren<SocketMarker>(true))
                {
                    var local = new Vec3(socket.godotLocalPosition.x, socket.godotLocalPosition.y, socket.godotLocalPosition.z);
                    Vec3 godotWorld = catalog.WorldSocketPosition(godotPlacement, module.godotYawDegrees, local);
                    Vector3 expected = _parent.transform.TransformPoint(Frame.ToUnity(godotWorld));
                    Vector3 actual = socket.transform.position;
                    compared++;
                    if (Vector3.Distance(expected, actual) > 1e-3f)
                        mismatches.Add($"{module.moduleKey} {socket.socketId}: Godot→Unity {expected:F4} placed {actual:F4}");
                }
            }
            Assert.That(compared, Is.GreaterThan(100));
            Assert.IsEmpty(mismatches, $"{mismatches.Count}/{compared} sockets off:\n" + string.Join("\n", mismatches.GetRange(0, System.Math.Min(20, mismatches.Count))));
        }

        [Test]
        public void ModuleDamageRowsDriveVisualVariants()
        {
            var layout = LoadLayout(Layouts[0]);
            var firstEdge = (GdDict)layout.GetDict("structural_plan").GetArray("placements")[0];
            string key = "edge/" + firstEdge.GetString("edge_key");
            layout["module_damage"] = GdArray.Of(new GdDict { { "module_key", key }, { "state", "breached" } });

            var result = new StructuralLayoutBuilder().Build(layout, Kit(), _parent.transform);
            var module = result.ByModuleKey[key];
            Assert.AreEqual("breached", module.integrityState);
            Assert.IsTrue(module.breachedVisual.activeSelf);
            if (module.intactVisual != module.breachedVisual) Assert.IsFalse(module.intactVisual.activeSelf);
        }

        [Test]
        public void UnknownModuleFailsAtomically()
        {
            var layout = LoadLayout(Layouts[0]);
            ((GdDict)layout.GetDict("structural_plan").GetArray("floor_placements")[3])["module_id"] = "no_such_module";
            var builder = new StructuralLayoutBuilder();
            Assert.IsNull(builder.Build(layout, Kit(), _parent.transform));
            StringAssert.Contains("no_such_module", builder.LastError);
            Assert.AreEqual(0, _parent.transform.childCount, "nothing is published when a record fails");
        }
    }
}
