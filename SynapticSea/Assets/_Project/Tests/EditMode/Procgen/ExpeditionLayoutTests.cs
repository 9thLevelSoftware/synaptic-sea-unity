using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    public class ExpeditionLayoutTests
    {
        [SetUp] public void Setup() { CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot); CoreServices.Log = NullLog.Instance; CatalogRegistry.Clear(); }
        [TearDown] public void Cleanup() { CatalogRegistry.Clear(); EncounterInjector.ClearTableCache(); }
        [TestCase(17, 1)] [TestCase(42, 1)] [TestCase(777, 1)] [TestCase(999, 1)]
        [TestCase(17, 2)] [TestCase(42, 2)] [TestCase(777, 2)] [TestCase(999, 2)]
        public void ExpeditionHasRealLoopsPurposefulRoomsAndNoOverlappingCells(int seed, int size)
        {
            var blueprint = new ShipBlueprint(size, 0, seed) { GenerationProfile = ExpeditionLayoutEngine.Profile };
            var generator = new ShipGenerator(); generator.ConfigureRunContext("dead_fleet", "standard");
            var docs = generator.Generate(blueprint); Assert.NotNull(docs);
            var layout = docs.Layout; Assert.AreEqual(ExpeditionLayoutEngine.Profile, layout.GetString("generation_profile"));
            var rooms = layout.GetArrayOrEmpty("rooms"); Assert.GreaterOrEqual(rooms.Count, size == 1 ? 12 : 18);
            var occupied = new HashSet<string>(); var roles = new HashSet<string>();
            foreach (GdDict room in rooms)
            {
                roles.Add(room.GetString("room_role"));
                foreach (object cell in room.GetArrayOrEmpty("cells")) Assert.IsTrue(occupied.Add(GdJson.Stringify(cell)), "overlapping room footprint");
            }
            CollectionAssert.IsSubsetOf(new[] { "dock", "corridor", "crew_quarters", "bridge", "engineering" }, roles);
            var graph = new Dictionary<string, HashSet<string>>(); foreach (GdDict room in rooms) graph[room.GetString("id")] = new HashSet<string>();
            var portals = layout.GetArrayOrEmpty("portals");
            foreach (GdDict portal in portals) { string a = portal.GetString("from_room"), b = portal.GetString("to_room"); graph[a].Add(b); graph[b].Add(a); }
            var reached = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue("dock_01");
            while (queue.Count > 0) { string id = queue.Dequeue(); if (!reached.Add(id)) continue; foreach (string next in graph[id]) queue.Enqueue(next); }
            Assert.AreEqual(rooms.Count, reached.Count, "every sector must connect to the dock");
            Assert.Greater(portals.Count, rooms.Count, "physical graph contains alternate route cycles");
            Assert.IsTrue(layout.GetBool("structural_plan_validated"));
            var restored = ShipBlueprint.FromDict(GdJson.ParseDict(GdJson.Stringify(blueprint.ToDict())));
            Assert.AreEqual(docs.LayoutJson, generator.Generate(restored).LayoutJson, "saved version/seed exactly restores geometry");
        }
        [Test] public void LegacyBlueprintSerializationRemainsUnchangedAndSmallBoatsStayLegacy()
        {
            Assert.IsFalse(new ShipBlueprint().ToDict().Has("generation_profile"));
            var generator = new ShipGenerator { RichExpeditions = true }; generator.ConfigureRunContext("breach_field", "standard");
            Assert.IsFalse(generator.GenerateFromSeed(42, 0, 0).Layout.Has("generation_profile"));
        }
    }
}
