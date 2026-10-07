using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    public class LootContainerPositionOffsetTests
    {
        ILog _previousLog;
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _previousLog = CoreServices.Log;
            CoreServices.Log = _log = new CollectingLog();
        }

        [TearDown]
        public void TearDown() => CoreServices.Log = _previousLog;

        static GdDict Row(string id = "synthetic_empty") => new GdDict
        {
            { "id", id }, { "room_id", "maintenance_01" },
            { "approach_cell", GdArray.Of(5L, 1L, 1L) },
            { "contents", new GdArray() },
        };

        static GeneratedShipLayout Layout(GdArray rows, GdArray floorPosition = null) => new GeneratedShipLayout
        {
            LayoutDoc = new GdDict
            {
                { "rooms", GdArray.Of(new GdDict { { "id", "maintenance_01" }, { "deck", 1L } }) },
                { "structural_plan", new GdDict
                    {
                        { "floor_placements", GdArray.Of(new GdDict
                            {
                                { "room_id", "maintenance_01" }, { "cell_key", "1|5|1" },
                                { "position", floorPosition ?? GdArray.Of(20.0, 4.0, 4.0) },
                            }) },
                    } },
            },
            GameplayDoc = new GdDict { { "loot_containers", rows } },
        };

        [Test]
        public void AbsentOffsetKeepsCompleteLegacyDescriptorAndCopies()
        {
            var row = Row();
            row["kind"] = "locker";
            row["loot_table"] = "empty_table";
            row["slot_kind"] = "wall";
            row["slot_index"] = 2L;
            var rows = GdArray.Of(row);
            string inputBefore = GdJson.Stringify(rows);
            var expected = GdArray.Of(new GdDict
            {
                { "id", "synthetic_empty" }, { "kind", "locker" }, { "room_id", "maintenance_01" },
                { "loot_table", "empty_table" }, { "position", new Vec3(20.0, 4.12, 4.0) },
                { "approach_cell", GdArray.Of(5L, 1L, 1L) }, { "slot_kind", "wall" },
                { "slot_index", 2L }, { "contents", new GdArray() },
            });
            GdArray actual = Layout(rows).BuildLootContainerSpecs();
            Assert.That(GdJson.Stringify(actual), Is.EqualTo(GdJson.Stringify(expected)), "legacy serialized output");
            Assert.That(V.VariantEquals(expected, actual), Is.True, "legacy logical output");
            Assert.That(GdJson.Stringify(rows), Is.EqualTo(inputBefore));
            var spec = (GdDict)actual[0];
            Assert.That(spec.Get("approach_cell"), Is.Not.SameAs(row.Get("approach_cell")));
            Assert.That(spec.Get("contents"), Is.Not.SameAs(row.Get("contents")));
            Assert.That(spec.Has("position_offset"), Is.False);
            Assert.That(_log.Errors, Is.Empty);
        }

        [Test]
        public void ExplicitZeroOffsetKeepsLegacyOutput()
        {
            var row = Row();
            string expected = GdJson.Stringify(Layout(GdArray.Of(row)).BuildLootContainerSpecs());
            row["position_offset"] = GdArray.Of(0L, 0.0, 0L);
            Assert.That(GdJson.Stringify(Layout(GdArray.Of(row)).BuildLootContainerSpecs()), Is.EqualTo(expected));
            Assert.That(_log.Errors, Is.Empty);
        }

        [TestCase(-0.75, 0.25, 1.25, 19.25, 4.37, 5.25)]
        [TestCase(1.0, -0.125, -2.0, 21.0, 3.995, 2.0)]
        public void OffsetMovesShipLocalRootAfterFloorProjection(double dx, double dy, double dz, double x, double y, double z)
        {
            var row = Row();
            row["position_offset"] = GdArray.Of(dx, dy, dz);
            string inputBefore = GdJson.Stringify(row);
            var spec = (GdDict)Layout(GdArray.Of(row)).BuildLootContainerSpecs()[0];
            Assert.That(spec.Get("position"), Is.EqualTo(new Vec3(x, y, z)));
            Assert.That(V.VariantEquals(spec.Get("approach_cell"), GdArray.Of(5L, 1L, 1L)), Is.True);
            Assert.That(spec.Has("position_offset"), Is.False, "resolved descriptor keeps the existing shape");
            Assert.That(GdJson.Stringify(row), Is.EqualTo(inputBefore));
            Assert.That(_log.Errors, Is.Empty);
        }

        [Test]
        public void PresentOffsetPreservesExplicitContentsAndSlotMetadata()
        {
            var row = Row();
            row["position_offset"] = GdArray.Of(-1L, 0.25, 2L);
            row["slot_kind"] = "wall";
            row["slot_index"] = 7L;
            row["contents"] = GdArray.Of(new GdDict { { "item_id", "fixture_only" }, { "qty", 2L } });
            var spec = (GdDict)Layout(GdArray.Of(row)).BuildLootContainerSpecs()[0];
            Assert.That(spec.Get("position"), Is.EqualTo(new Vec3(19.0, 4.37, 6.0)));
            Assert.That(spec.GetString("slot_kind"), Is.EqualTo("wall"));
            Assert.That(spec.Get("slot_index"), Is.EqualTo(7L));
            Assert.That(V.VariantEquals(spec.Get("contents"), row.Get("contents")), Is.True);
            ((GdDict)row.GetArray("contents")[0])["qty"] = 99L;
            Assert.That(((GdDict)spec.GetArray("contents")[0]).Get("qty"), Is.EqualTo(2L));
        }

        static IEnumerable<TestCaseData> MalformedOffsets()
        {
            yield return new TestCaseData(new object[] { null }).SetName("PresentNullOffsetRejectsRow");
            yield return new TestCaseData(new object[] { new GdDict() }).SetName("DictionaryOffsetRejectsRow");
            yield return new TestCaseData(new object[] { new Vec3(1, 2, 3) }).SetName("VectorLeafOffsetRejectsRow");
            yield return new TestCaseData(new object[] { "[1,2,3]" }).SetName("StringOffsetRejectsRow");
            yield return new TestCaseData(new object[] { new GdArray() }).SetName("EmptyOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(1L, 2L) }).SetName("ShortOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(1L, 2L, 3L, 4L) }).SetName("LongOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of("1", 2L, 3L) }).SetName("NumericStringOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(true, 2L, 3L) }).SetName("BoolOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(1L, null, 3L) }).SetName("NullCoordinateRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(1L, new GdDict(), 3L) }).SetName("DictionaryCoordinateRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(1L, 2L, double.NaN) }).SetName("NaNOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(double.PositiveInfinity, 2L, 3L) }).SetName("PositiveInfinityOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(1L, double.NegativeInfinity, 3L) }).SetName("NegativeInfinityOffsetRejectsRow");
            yield return new TestCaseData(new object[] { GdArray.Of(double.MaxValue, 0L, 0L) }).SetName("Float32OffsetOverflowRejectsRow");
        }

        [TestCaseSource(nameof(MalformedOffsets))]
        public void MalformedPresentOffsetRejectsOnlyItsRow(object offset)
        {
            var bad = Row("bad_offset");
            bad["position_offset"] = offset;
            var rows = GdArray.Of(bad, Row("valid_neighbor"));
            string inputBefore = GdJson.Stringify(rows);
            GdArray specs = Layout(rows).BuildLootContainerSpecs();
            Assert.That(specs.Count, Is.EqualTo(1));
            Assert.That(((GdDict)specs[0]).GetString("id"), Is.EqualTo("valid_neighbor"));
            Assert.That(_log.Errors.Count, Is.EqualTo(1));
            StringAssert.Contains("bad_offset", _log.Errors[0]);
            StringAssert.Contains("position_offset", _log.Errors[0]);
            Assert.That(GdJson.Stringify(rows), Is.EqualTo(inputBefore));
        }

        [Test]
        public void FiniteOffsetWhoseResolvedFloat32RootOverflowsRejectsOnlyItsRow()
        {
            var bad = Row("overflow_sum");
            bad["position_offset"] = GdArray.Of((double)float.MaxValue, 0L, 0L);
            GdArray specs = Layout(GdArray.Of(bad, Row("valid_neighbor")), GdArray.Of((double)float.MaxValue, 4.0, 4.0)).BuildLootContainerSpecs();
            Assert.That(specs.Count, Is.EqualTo(1));
            Assert.That(((GdDict)specs[0]).GetString("id"), Is.EqualTo("valid_neighbor"));
            Assert.That(_log.Errors.Count, Is.EqualTo(1));
            StringAssert.Contains("overflow_sum", _log.Errors[0]);
            StringAssert.Contains("position_offset", _log.Errors[0]);
        }

        [Test]
        public void OffsetCannotRescueAnUnresolvedApproachCell()
        {
            var row = Row();
            row["approach_cell"] = GdArray.Of(999L, 1L, 1L);
            row["position_offset"] = GdArray.Of(0L, 0L, 0L);
            Assert.That(Layout(GdArray.Of(row)).BuildLootContainerSpecs(), Is.Empty);
        }
    }
}
