using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// The pure half of the GeneratedShipLoader port (<see cref="GeneratedShipLayout"/>) against the Godot loader
    /// captures in fixtures/godot/loader: specs, markers, zones, portal specs, placed-prop plan, dressing plan (RNG),
    /// probes. Positions compare within 1e-4 (Godot frame). The Unity ShipSceneBuilder tests cover the scene nodes.
    /// </summary>
    public class GeneratedShipLayoutParityTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        static IEnumerable<string> Cases => LoaderParity.Cases;

        [TestCaseSource(nameof(Cases))]
        public void PureSectionsMatchGodotLoader(string caseName)
        {
            Fixtures.Require($"{LoaderParity.Dir}/{caseName}.json");
            GdDict fixture = LoaderParity.Fixture(caseName);
            GdDict inputs = fixture.GetDict("inputs");
            GdDict layout = LoaderParity.LoadInput(inputs.GetString("layout"));
            GdDict gameplay = LoaderParity.LoadInput(inputs.GetString("gameplay_slice"));
            GdDict propCatalog = CatalogRegistry.LoadDict("res://data/kits/gameplay_prop_v0.json").GetDict("props");
            var visual = new PropVisualBindingCatalog();
            Assert.IsTrue(visual.LoadFromPath(), string.Join("; ", visual.GetErrors()));

            GeneratedShipLayout model = LoaderParity.BuildModel(layout, gameplay, propCatalog, visual,
                out var placedProps, out _, out var dressing, out int verticalLinks);
            Assert.IsNotNull(model, "documents failed the pure load steps");
            GdDict actual = LoaderParity.CoreRecord(model, placedProps, dressing, verticalLinks);

            var expected = LoaderParity.Pick(fixture, actual.Keys.Select(V.Str).Where(k => k != "summary_partial" && k != "nodes"));
            expected["summary_partial"] = LoaderParity.Pick(fixture.GetDict("summary"), ((GdDict)actual["summary_partial"]).Keys.Select(V.Str));
            expected["nodes"] = LoaderParity.Pick(fixture.GetDict("nodes"), ((GdDict)actual["nodes"]).Keys.Select(V.Str));

            var diffs = TreeDiff.Compare(expected, actual, LoaderParity.Options());
            Assert.IsEmpty(diffs, TreeDiff.Format(diffs));
        }

        [Test]
        public void AugmentedCaseExercisesDressingAndZones()
        {
            const string caseName = "seed_000017_augmented_home";
            Fixtures.Require($"{LoaderParity.Dir}/{caseName}.json");
            GdDict fixture = LoaderParity.Fixture(caseName);
            GdDict nodes = fixture.GetDict("nodes");
            Assert.That(nodes.GetArray("dressing").Count, Is.GreaterThan(10), "fixture should carry dressing lights, fog and props");
            Assert.That(fixture.GetArray("radiation_zone_markers").Count, Is.EqualTo(2));
            Assert.That(fixture.GetArray("placed_prop_errors").Count, Is.EqualTo(1));
            Assert.That(fixture.GetArray("authored_atmosphere_specs").Count, Is.GreaterThan(0));
        }

        [Test]
        public void SeedFromLayoutDocMatchesGodotRules()
        {
            Assert.AreEqual(42, GeneratedShipLayout.SeedFromLayoutDoc(new GdDict { { "seed_value", 42.0 } }));
            Assert.AreEqual(1234, GeneratedShipLayout.SeedFromLayoutDoc(new GdDict { { "program_id", "worldgen-seed-1234x" } }));
            Assert.AreEqual(0, GeneratedShipLayout.SeedFromLayoutDoc(new GdDict { { "program_id", "coherent-proof-ship-001" } }));
            Assert.AreEqual(0, GeneratedShipLayout.SeedFromLayoutDoc(new GdDict { { "program_id", "seed-" } }));
        }

        [Test]
        public void ParseSlotCellHandlesGodotForms()
        {
            Assert.IsTrue(V.VariantEquals(GdArray.Of(3L, -2L), GeneratedShipLayout.ParseSlotCell(GdArray.Of(3.0, -2.0, 1.0))));
            Assert.IsTrue(V.VariantEquals(GdArray.Of(4L, 5L), GeneratedShipLayout.ParseSlotCell(" (3.6, 4.5) ")));
            Assert.IsTrue(V.VariantEquals(GdArray.Of(7L, 8L), GeneratedShipLayout.ParseSlotCell("floor_cell_d1_x7_z8")));
            Assert.IsTrue(V.VariantEquals(GdArray.Of(1L, 2L), GeneratedShipLayout.ParseSlotCell(new GdDict { { "cell", GdArray.Of(1.0, 2.0) } })));
            Assert.AreEqual(0, GeneratedShipLayout.ParseSlotCell("nope").Count);
        }
    }
}
