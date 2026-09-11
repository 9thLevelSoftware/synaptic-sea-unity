using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class MarkerGeneratorTests
    {
        [Test]
        public void RoundTrip_MarkersAreDeterministic()
        {
            var gen = new MarkerGenerator();
            List<ShipMarker> a = gen.MarkersForCell(42, new Vec2i(3, -1));
            List<ShipMarker> b = gen.MarkersForCell(42, new Vec2i(3, -1));
            Assert.AreEqual(MarkerGenerator.MARKERS_PER_CELL, a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.IsTrue(V.VariantEquals(a[i].ToDict(), b[i].ToDict()));
                Assert.IsTrue(V.VariantEquals(a[i].ToDict(), ShipMarker.FromDict(a[i].ToDict()).ToDict()));
            }
            Assert.AreEqual("3:-1:0", a[0].MarkerId);
        }

        // Captured from Godot 4.7.1 running markers_for_cell's RNG sequence: marker_id, x, z, seed, size, condition, type.
        static readonly object[][] GodotCapture =
        {
            new object[] { 42L, 3, -1, "3:-1:0", 373.037414551, -78.304069519, 1415114682L, 2L, 2L, "freighter" },
            new object[] { 42L, 3, -1, "3:-1:1", 367.327697754, -38.668251038, 1958815299L, 0L, 2L, "derelict_hauler" },
            new object[] { 42L, 3, -1, "3:-1:2", 327.849029541, -25.191413879, 1885086694L, 0L, 1L, "science_vessel" },
            new object[] { -7L, -12, 5, "-12:5:0", -1104.922119141, 588.443481445, 1764466733L, 1L, 1L, "science_vessel" },
            new object[] { -7L, -12, 5, "-12:5:1", -1129.109863281, 561.208129883, 2803537758L, 1L, 1L, "science_vessel" },
            new object[] { -7L, -12, 5, "-12:5:2", -1127.519409180, 597.349487305, 1976761671L, 0L, 2L, "derelict_hauler" },
        };

        [Test]
        public void Markers_MatchGodotCapture()
        {
            var gen = new MarkerGenerator();
            for (int row = 0; row < GodotCapture.Length; row++)
            {
                object[] e = GodotCapture[row];
                ShipMarker m = gen.MarkersForCell((long)e[0], new Vec2i((int)e[1], (int)e[2]))[row % 3];
                Assert.AreEqual((string)e[3], m.MarkerId);
                Assert.AreEqual((float)(double)e[4], m.Position.X);
                Assert.AreEqual((float)(double)e[5], m.Position.Z);
                Assert.AreEqual((long)e[6], m.SeedValue);
                Assert.AreEqual((long)e[7], m.SizeClass);
                Assert.AreEqual((long)e[8], m.Condition);
                Assert.AreEqual((string)e[9], m.ShipType);
            }
        }

        [Test]
        public void Markers_FallInsideCellWithDistinctSeeds()
        {
            var gen = new MarkerGenerator();
            List<ShipMarker> a = gen.MarkersForCell(42, new Vec2i(3, -1));
            List<ShipMarker> c = gen.MarkersForCell(42, new Vec2i(4, -1));
            Assert.IsFalse(c[0].MarkerId == a[0].MarkerId && c[0].SeedValue == a[0].SeedValue);
            var seeds = new HashSet<long>();
            foreach (ShipMarker m in a)
            {
                Assert.That(m.Position.X, Is.InRange(300.0f, 400.0f));
                Assert.That(m.Position.Z, Is.InRange(-100.0f, 0.0f));
                Assert.AreEqual(0.0f, m.Position.Y);
                Assert.That(m.SizeClass, Is.InRange(0L, 2L));
                Assert.That(m.Condition, Is.InRange(0L, 2L));
                Assert.IsTrue(MarkerGenerator.SHIP_TYPES.Contains(m.ShipType));
                seeds.Add(m.SeedValue);
            }
            Assert.AreEqual(a.Count, seeds.Count);
            Assert.AreEqual(42L ^ (3L * 73856093L) ^ (-1L * 19349663L), MarkerGenerator.CellSeed(42, new Vec2i(3, -1)));
        }
    }
}
