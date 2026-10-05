using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.EditMode.Procgen
{
    public sealed class FirstAwaySalvagePrototypeTests
    {
        CollectingLog _log;
        [SetUp] public void Setup() { CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot); CoreServices.Log = _log = new CollectingLog(); CatalogRegistry.Clear(); }
        [TearDown] public void Cleanup() { CoreServices.Log = NullLog.Instance; CatalogRegistry.Clear(); }
        static FirstAwayGenerationInputs Inputs(long size = 0, long condition = 2, long world = 17, string marker = "0:0:0", string owner = "ship-0")
            => new FirstAwayGenerationInputs(42, world, size, condition, marker, owner, "breach_field", "standard");
        [Test]
        public void EveryDeclaredFamilyFootprintOrientationAndBoundaryChoiceOwnsConnectedRealPortals()
        {
            int cases = 0;
            for (int size = 0; size < 3; size++) foreach (string family in FirstAwaySalvageGeometry.Families(size))
                foreach (var fp in size == 0 ? new[] { new Vec2i(3, 3) } : new[] { new Vec2i(3, 3), new Vec2i(3, 4), new Vec2i(4, 3) })
                    for (int orientation = 0; orientation < 8; orientation++) for (int offset = 0; offset < 2; offset++)
                    {
                        var input = Inputs(size); var choice = new FirstAwaySalvageGeometry.Choices(family, fp.X, fp.Y, orientation, offset);
                        var grid = FirstAwaySalvageGeometry.Compose(input, choice, out var plan);
                        Assert.AreEqual("", FirstAwaySalvageGeometry.Validate(grid));
                        Assert.AreEqual("dock", plan.First().GetString("role")); Assert.AreEqual("bridge", plan.Last().GetString("role"));
                        int expectedRooms = size == 0 ? 4 : size == 1 ? family == "service_loop" ? 6 : family == "split_service" ? 7 : 8
                            : family == "service_loop" ? 8 : family == "divided_service_hull" ? 10 : family == "divided_service_hull_p1" ? 11 : 12;
                        Assert.AreEqual(expectedRooms, plan.Count);
                        long cycles = grid.GetArrayOrEmpty("adjacencies").Count - plan.Count + 1;
                        Assert.GreaterOrEqual(cycles, size == 0 ? 0 : size == 1 ? 1 : 2);
                        var geometry = new WallDoorResolver().Resolve(grid, plan); Assert.IsFalse(geometry.IsEmpty, "compiler rejects " + family + "/" + orientation + ": " + string.Join(";", _log.Errors));
                        var layout = new LayoutSerializer().Serialize(grid, geometry, plan, "prototype", input.CandidateSeed, "prototype");
                        Assert.IsTrue(FirstRunAwayGate.HasStandingStartToGoal(layout), "serialized floor route");
                        var structural = layout.GetDictOrEmpty("structural_plan");
                        var slotIds = new HashSet<string>();
                        foreach (string layer in new[] { "floor", "ceiling", "edge" })
                        {
                            string array = layer == "edge" ? "placements" : layer + "_placements";
                            var slots = structural.GetArrayOrEmpty(array); Assert.Greater(slots.Count, 0, "actual compiled " + layer + " inventory");
                            foreach (GdDict slot in slots)
                            {
                                string keyPart = layer == "edge" ? slot.GetString("edge_key", slot.GetString("key")) : slot.GetString("cell_key");
                                Assert.IsNotEmpty(keyPart);
                                Assert.IsTrue(slotIds.Add(layer + "/" + keyPart), "unique actual LayoutMutator module identity");
                                var owned = slot.GetArrayOrEmpty("room_ids").Cast<object>().Select(V.Str).Where(owner => owner.Length > 0).ToList();
                                if (slot.GetString("room_id").Length > 0) owned.Add(slot.GetString("room_id"));
                                Assert.Greater(owned.Count, 0, "compiled slot has an explicit real owner");
                                foreach (string owner in owned) Assert.IsTrue(grid.GetDictOrEmpty("rooms").Has(owner), "compiled owner belongs to topology");
                            }
                        }
                        Assert.Greater(slotIds.Count, 0);
                        var recovery = layout.GetArrayOrEmpty("rooms").Cast<GdDict>().Single(room => room.GetString("room_role") == "cargo");
                        var entries = EncounterInjector.FloorCellEntries(recovery, 4);
                        Assert.AreEqual(Vec2i.FromArray(((GdDict)entries[entries.Count / 2])["cell"]), grid.GetDictOrEmpty("reservations")["threat_anchor"], "actual emitter midpoint reservation");
                        foreach (GdDict edge in grid.GetArrayOrEmpty("adjacencies"))
                        {
                            Assert.AreEqual(input.OwnerId, edge.GetString("owner_id"));
                            var ca = (Vec2i)edge["from_cell"]; var cb = (Vec2i)edge["to_cell"];
                            Assert.AreEqual(1, ca.ManhattanTo(cb)); Assert.AreEqual(cb - ca, edge["normal"]);
                        }
                        cases++;
                    }
            Assert.AreEqual(352, cases, "finite space coverage, no random samples");
        }
        [Test]
        public void RotationReflectionKeepsSemanticOwnersAndTransformsEveryPortalNormal()
        {
            var input = Inputs(2); var baseline = FirstAwaySalvageGeometry.Compose(input, new FirstAwaySalvageGeometry.Choices("service_loop", 3, 3, 0, 1), out var basePlan);
            for (int orientation = 0; orientation < 8; orientation++)
            {
                var grid = FirstAwaySalvageGeometry.Compose(input, new FirstAwaySalvageGeometry.Choices("service_loop", 3, 3, orientation, 1), out var plan);
                CollectionAssert.AreEqual(basePlan.Select(row => row.GetString("id")), plan.Select(row => row.GetString("id")));
                var before = baseline.GetArrayOrEmpty("adjacencies").Cast<GdDict>().ToArray(); var after = grid.GetArrayOrEmpty("adjacencies").Cast<GdDict>().ToArray();
                for (int index = 0; index < before.Length; index++) foreach (string field in new[] { "from_cell", "to_cell", "normal" })
                    Assert.AreEqual(FirstAwaySalvageGeometry.Transform((Vec2i)before[index][field], orientation), after[index][field]);
            }
        }
        [Test]
        public void UnsupportedChoicesAndCorruptOwnedPortalsAreRefused()
        {
            Assert.Throws<ArgumentException>(() => FirstAwaySalvageGeometry.Compose(Inputs(), new FirstAwaySalvageGeometry.Choices("service_loop", 3, 3, 0, 0), out _));
            Assert.Throws<ArgumentException>(() => FirstAwaySalvageGeometry.Compose(Inputs(), new FirstAwaySalvageGeometry.Choices("compact_dogleg", 4, 3, 0, 0), out _));
            Assert.Throws<ArgumentException>(() => FirstAwaySalvageGeometry.Transform(Vec2i.Zero, 8));
            var grid = FirstAwaySalvageGeometry.Compose(Inputs(), out _); ((GdDict)grid.GetArrayOrEmpty("adjacencies")[0])["to_cell"] = new Vec2i(900, 900);
            Assert.AreEqual("portal_ownership", FirstAwaySalvageGeometry.Validate(grid));
            grid = FirstAwaySalvageGeometry.Compose(Inputs(), out _);
            var dock = grid.GetDictOrEmpty("rooms").Values.Cast<GdDict>().Single(room => room.GetString("role") == "dock");
            dock.GetArrayOrEmpty("cells")[0] = new Vec2i(200, 200);
            Assert.AreEqual("nonrectangular_floor", FirstAwaySalvageGeometry.Validate(grid), "area count alone cannot admit a detached floor");
            Assert.AreEqual("missing_grid", FirstAwaySalvageGeometry.Validate(new GdDict()));
            grid = FirstAwaySalvageGeometry.Compose(Inputs(), out _); grid.GetDictOrEmpty("reservations")["work_approach"] = grid.GetDictOrEmpty("reservations")["branch_landing"];
            Assert.AreEqual("reservation_ownership", FirstAwaySalvageGeometry.Validate(grid));
        }
        [Test]
        public void FullInputProjectionIsRepeatableAndVariesAcrossActualWorldAndMarkerIdentities()
        {
            var signatures = new HashSet<string>();
            foreach (long world in new[] { 17L, 29L, 73L }) foreach (string marker in new[] { "0:0:0", "-2:0:1", "3:-4:1" })
                for (int size = 0; size < 3; size++) for (int condition = 0; condition < 3; condition++) foreach (long seed in new[] { 42L, 777L })
                {
                    var input = new FirstAwayGenerationInputs(seed, world, size, condition, marker, "owner", "breach_field", "standard");
                    var first = FirstAwaySalvageGeometry.Compose(input, out _); var repeat = FirstAwaySalvageGeometry.Compose(input, out _);
                    Assert.IsTrue(V.VariantEquals(first, repeat)); signatures.Add(GdJson.Stringify(first.GetDictOrEmpty("prototype_choices")));
                }
            Assert.Greater(signatures.Count, 20);
        }
        static Dictionary<string,FirstAwayCatalogIdentity> Catalogs(bool reversed = false)
        {
            var rows = new[] { new KeyValuePair<string,FirstAwayCatalogIdentity>("contract", new FirstAwayCatalogIdentity("contract-1", new string('a',64))),
                new KeyValuePair<string,FirstAwayCatalogIdentity>("kit", new FirstAwayCatalogIdentity("kit-1", new string('b',64))) };
            return (reversed ? rows.Reverse() : rows).ToDictionary(row => row.Key, row => row.Value);
        }
        [Test]
        public void DescriptorBindsCompleteTypedContextCatalogProviderAndExactRawBytesWithoutMutation()
        {
            var input = Inputs(); var catalogs = Catalogs(); byte[] layout = Encoding.UTF8.GetBytes("{\"x\":-0.0}\n"), gameplay = Encoding.UTF8.GetBytes("{\"finite\":true}");
            var descriptor = new FirstAwayGenerationDescriptor(input, "managed-prototype-1", catalogs, layout, gameplay);
            string binding = descriptor.BindingSha256;
            Assert.IsTrue(descriptor.Matches(input,"managed-prototype-1",Catalogs(true),layout,gameplay), "catalog enumeration order is not identity");
            var detached = descriptor.Snapshot(); detached.GetDictOrEmpty("inputs")["world_seed"] = 99L;
            layout[0] = 0; gameplay[0] = 0; catalogs.Clear(); descriptor.LayoutBytes[0] = 0;
            Assert.AreEqual(binding, descriptor.BindingSha256); Assert.IsTrue(descriptor.MatchesSnapshot(descriptor.Snapshot()));
            Assert.IsFalse(descriptor.MatchesSnapshot(detached));
            var rawLayout = descriptor.LayoutBytes; var rawGameplay = descriptor.GameplayBytes;
            Assert.IsFalse(descriptor.Matches(Inputs(world:29),"managed-prototype-1",Catalogs(),rawLayout,rawGameplay));
            Assert.IsFalse(descriptor.Matches(Inputs(marker:"1:0:0"),"managed-prototype-1",Catalogs(),rawLayout,rawGameplay));
            Assert.IsFalse(descriptor.Matches(Inputs(owner:"other-owner"),"managed-prototype-1",Catalogs(),rawLayout,rawGameplay));
            Assert.IsFalse(descriptor.Matches(Inputs(condition:1),"managed-prototype-1",Catalogs(),rawLayout,rawGameplay));
            Assert.IsFalse(descriptor.Matches(input,"rust-fixture",Catalogs(),rawLayout,rawGameplay));
            var changed = Catalogs(); changed["kit"] = new FirstAwayCatalogIdentity("kit-2", new string('b',64));
            Assert.IsFalse(descriptor.Matches(input,"managed-prototype-1",changed,rawLayout,rawGameplay));
            Assert.IsFalse(descriptor.Matches(input,"managed-prototype-1",Catalogs(),Encoding.UTF8.GetBytes("{\"x\":0.0}\n"),rawGameplay));
            var numericType = descriptor.Snapshot(); numericType.GetDictOrEmpty("inputs")["world_seed"] = 17.0;
            Assert.IsFalse(descriptor.MatchesSnapshot(numericType), "integer identity cannot silently coerce from floating point");
            var unknown = descriptor.Snapshot(); unknown["ignored"] = true; Assert.IsFalse(descriptor.MatchesSnapshot(unknown));
            var typedRoundTrip = SynapticSea.Core.Systems.PaidSnapshotCodec.Parse(SynapticSea.Core.Systems.PaidSnapshotCodec.Stringify(descriptor.Snapshot()));
            Assert.IsTrue(descriptor.MatchesSnapshot(typedRoundTrip));
        }
        [Test]
        public void InvalidContextOrUnauthenticatedSectorFormsFailClosed()
        {
            foreach (string marker in new[] { "", "1:2", "01:0:0", "+1:0:0", "-0:0:0", "0.0:0:0", "0:0:-1", "0:0:3", "2147483648:0:0", " 1:0:0", "9223372036854775808:0:0" })
                Assert.Throws<ArgumentException>(() => Inputs(marker:marker));
            Assert.Throws<ArgumentException>(() => Inputs(size:3)); Assert.Throws<ArgumentException>(() => Inputs(condition:-1));
            Assert.Throws<ArgumentException>(() => new FirstAwayGenerationInputs(42,17,0,2,"0:0:0","ship","dead_fleet","standard"));
            Assert.Throws<ArgumentException>(() => new FirstAwayCatalogIdentity("v1","guess"));
        }
    }
}
