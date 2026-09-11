using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class DockPortsTests
    {
        static GdDict Room(string id, string role, params double[][] cells)
        {
            var placements = new GdArray();
            foreach (var c in cells)
                placements.Add(new GdDict { { "module_id", "floor_1x1" }, { "world_position", GdArray.Of(c[0], c[1], c[2]) } });
            placements.Add(new GdDict { { "module_id", "wall_straight_1x1" }, { "world_position", GdArray.Of(99.0, 0.0, 0.0) } });
            return new GdDict { { "id", id }, { "room_role", role }, { "structural_placements", placements } };
        }

        [Test]
        public void LifeboatAndDerelictPorts()
        {
            var lb = new GdDict { { "rooms", GdArray.Of(Room("airlock_0", "airlock", new[] { -2.0, 0.0, 2.0 }, new[] { 2.0, 0.0, -2.0 })) } };
            GdDict lbPort = DockPorts.ForLifeboat(lb);
            Assert.AreEqual(new Vec3(-2f, 0f, 0f), lbPort["position"]);
            Assert.AreEqual(new Vec3(-1f, 0f, 0f), lbPort["facing"]);
            Assert.AreEqual(1L, lbPort["size_class"]);

            var der = new GdDict { { "rooms", GdArray.Of(Room("dock_01", "dock", new[] { 12.0, 0.0, 0.0 })) } };
            GdDict derPort = DockPorts.ForDerelict(der);
            Assert.AreEqual(new Vec3(12f, 0f, 0f), derPort["position"]);
            Assert.AreEqual(new Vec3(1f, 0f, 0f), derPort["facing"]);
            Assert.AreEqual("intact", derPort["condition"]);
            Assert.IsTrue(DockPorts.ForDerelict(new GdDict { { "rooms", new GdArray() } }).IsEmpty);
            Assert.IsTrue(DockPorts.PortsCompatible(lbPort, derPort));
        }

        [Test]
        public void HangarDescriptor_AndCompatibility()
        {
            var layout = new GdDict { { "rooms", GdArray.Of(Room("cargo_0", "cargo", new[] { 0.0, 0.0, 0.0 }, new[] { 4.0, 0.0, 0.0 }, new[] { 8.0, 0.0, 0.0 }, new[] { 12.0, 0.0, 0.0 }, new[] { 16.0, 0.0, 0.0 })) } };
            GdDict bay = DockPorts.ForHangar(layout);
            Assert.AreEqual(2L, bay["slot_count"]);
            Assert.AreEqual(2L, bay["slot_size_class"]);
            Assert.IsTrue(V.VariantEquals(GdArray.Of(new Vec3(0f, 0f, 0f), new Vec3(8f, 0f, 0f)), bay["slot_anchors"]));
            var ship = new GdDict { { "type", "airlock" }, { "size_class", 2L } };
            Assert.IsTrue(DockPorts.PortsCompatible(bay, ship));
            Assert.IsFalse(DockPorts.PortsCompatible(bay, bay));
            Assert.IsFalse(DockPorts.PortsCompatible(new GdDict { { "type", "airlock" } }, ship));
        }

        [Test]
        public void ConditionFromSeed_IsDeterministicPerTier()
        {
            Assert.AreEqual("intact", DockPorts.ConditionFromSeed(123, 0));
            Assert.AreEqual("broken", DockPorts.ConditionFromSeed(123, 3));
            for (long s = 0; s < 20; s++)
                Assert.AreEqual(DockPorts.ConditionFromSeed(s, 2), DockPorts.ConditionFromSeed(s, 2));
        }
    }

    sealed class FakeRoot : IShipSceneRoot
    {
        public bool IsValid { get; set; } = true;
        public bool IsInsideTree { get; set; } = true;
        public Xform3 Transform { get; set; } = Xform3.Identity;
        public Xform3 GlobalTransform => Transform;
    }

    sealed class FakeShip : IDockableShip
    {
        public FakeShip(IShipSceneRoot root) => SceneRoot = root;
        public IShipSceneRoot SceneRoot { get; }
        public IDockableShip ParentShip { get; set; }
        public IList<IDockableShip> DockedShips { get; } = new List<IDockableShip>();
        public GdArray DockingPorts { get; set; } = new GdArray();
    }

    public class DockingManagerTests
    {
        static readonly GdDict HostPort = new GdDict { { "position", new Vec3(22f, 0f, 0f) }, { "facing", new Vec3(1f, 0f, 0f) } };
        static readonly GdDict MobilePort = new GdDict { { "position", new Vec3(2f, 0f, 0f) }, { "facing", new Vec3(1f, 0f, 0f) } };

        [Test]
        public void DockAlignsPorts_AndUndockClears()
        {
            var host = new FakeShip(new FakeRoot());
            var mobileRoot = new FakeRoot();
            var mobile = new FakeShip(mobileRoot);
            GdDict res = DockingManager.Dock(host, mobile, HostPort, MobilePort);
            Assert.IsTrue(res.GetBool("success"), res.ToString());

            Vec3 portWorld = mobileRoot.Transform * (Vec3)MobilePort["position"];
            Vec3 facingWorld = (mobileRoot.Transform.Basis * (Vec3)MobilePort["facing"]).Normalized();
            Assert.Less(portWorld.DistanceTo((Vec3)HostPort["position"]), 0.001f);
            Assert.Less(facingWorld.DistanceTo(-(Vec3)HostPort["facing"]), 0.001f);
            Assert.AreSame(host, mobile.ParentShip);
            Assert.IsTrue(host.DockedShips.Contains(mobile));

            DockingManager.Undock(mobile);
            Assert.IsNull(mobile.ParentShip);
            Assert.IsFalse(host.DockedShips.Contains(mobile));
            Assert.IsTrue(mobile.DockingPorts.IsEmpty);
            Assert.AreEqual("not_docked", DockingManager.Undock(mobile).GetString("reason"));
        }

        [Test]
        public void RejectsMalformedPortsSelfDock_AndRedockSeversOldHost()
        {
            var a = new FakeShip(new FakeRoot());
            var b = new FakeShip(new FakeRoot());
            var m = new FakeShip(new FakeRoot());
            Assert.AreEqual("dock_failed", DockingManager.Dock(a, m, new GdDict(), MobilePort).GetString("reason"));
            Assert.AreEqual("dock_failed", DockingManager.Dock(a, a, HostPort, MobilePort).GetString("reason"));
            DockingManager.Dock(a, m, HostPort, MobilePort);
            DockingManager.Dock(b, m, HostPort, MobilePort);
            Assert.AreSame(b, m.ParentShip);
            Assert.IsFalse(a.DockedShips.Contains(m));
            Assert.IsTrue(b.DockedShips.Contains(m));
        }

        [Test]
        public void HostPortToWorld_AppliesGlobalTransform()
        {
            var basis = Basis3.FromAxisAngle(Vec3.Up, (float)(System.Math.PI / 2.0));
            var root = new FakeRoot { Transform = new Xform3(basis, new Vec3(10f, 0f, 0f)) };
            var host = new FakeShip(root);
            GdDict world = DockingManager.HostPortToWorld(host, new GdDict { { "position", new Vec3(2f, 0f, 0f) }, { "facing", new Vec3(1f, 0f, 0f) } });
            Assert.IsTrue(((Vec3)world["position"]).IsEqualApprox(new Vec3(10f, 0f, -2f)));
            Assert.IsTrue(((Vec3)world["facing"]).IsEqualApprox(new Vec3(0f, 0f, -1f)));
            Assert.AreEqual(1L, world["size_class"]);
            root.IsInsideTree = false;
            Assert.IsTrue(DockingManager.HostPortToWorld(host, new GdDict { { "position", Vec3.Zero } }).IsEmpty);
        }
    }

    public class HangarBayTests
    {
        [Test]
        public void SlotsFillLaunch_AndRoundTrip()
        {
            var bay = HangarBay.Create(2, 1);
            Assert.AreEqual(-1, bay.FreeSlotFor(2));
            Assert.AreEqual(0, bay.Dock("ship_a", 1));
            Assert.AreEqual(-1, bay.Dock("ship_a", 1));
            Assert.AreEqual(1, bay.Dock("ship_b", 1));
            Assert.IsTrue(bay.IsFull());
            Assert.AreEqual(-1, bay.Dock("ship_c", 1));
            Assert.AreEqual("ship_a", bay.Launch(0));
            Assert.AreEqual("", bay.Launch(5));
            bay.Dock("ship_d", 1);

            GdDict summary = bay.GetSummary();
            var b2 = HangarBay.Create(0, 0);
            Assert.IsTrue(b2.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, b2.GetSummary()));
            Assert.IsFalse(b2.ApplySummary("nope"));
        }
    }

    public class ShipAccessStateTests
    {
        [Test]
        public void ClaimGrantRevoke_AndRoundTrip()
        {
            var access = ShipAccessState.Create();
            Assert.IsTrue(access.Claim("p1"));
            Assert.IsFalse(access.Claim("p2"));
            access.Grant("p2");
            Assert.IsTrue(access.HasAccess("p2"));
            access.Revoke("p1");
            Assert.IsTrue(access.HasAccess("p1"));

            GdDict summary = access.GetSummary();
            var fresh = new ShipAccessState();
            Assert.IsTrue(fresh.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, fresh.GetSummary()));
            access.Revoke("p2");
            Assert.IsFalse(access.HasAccess("p2"));
        }
    }

    public class ShipMarkerTests
    {
        [Test]
        public void DictRoundTrip()
        {
            var m = new ShipMarker { MarkerId = "3:4:1", Position = new Vec3(10.5f, 0f, -20.25f), SizeClass = 2, Condition = 0, ShipType = "freighter", SeedValue = 987654321012L };
            GdDict d = m.ToDict();
            Assert.IsTrue(V.VariantEquals(d, ShipMarker.FromDict(d).ToDict()));
            Assert.AreEqual(1L, ShipMarker.FromDict(new GdDict()).Condition);
        }
    }

    public class ShipOccupancyTests
    {
        [Test]
        public void ResolvesContainment_TiebreakAndMalformed()
        {
            object host = new object(), mobile = new object();
            var hostAabb = new Aabb3(new Vec3(0f, -1f, -5f), new Vec3(10f, 2f, 10f));
            var mobileAabb = new Aabb3(new Vec3(9f, -1f, -5f), new Vec3(10f, 2f, 10f));
            var entries = new List<object> { new OccupancyEntry(host, hostAabb), new OccupancyEntry(mobile, mobileAabb) };
            Assert.AreSame(host, ShipOccupancy.Resolve(new Vec3(2f, 0f, 0f), entries));
            Assert.AreSame(mobile, ShipOccupancy.Resolve(new Vec3(15f, 0f, 0f), entries));
            Assert.AreSame(host, ShipOccupancy.Resolve(new Vec3(9.5f, 0f, 0f), entries));
            Assert.IsNull(ShipOccupancy.Resolve(new Vec3(100f, 0f, 0f), entries));
            Assert.AreSame(host, ShipOccupancy.Resolve(new Vec3(10f, 0f, 0f), new List<object> { 42L, new OccupancyEntry(host, hostAabb) }));
            Assert.AreSame(mobile, ShipOccupancy.Resolve(new Vec3(15f, 0f, 0f), new List<object> { new OccupancyEntry(host, "notanaabb"), new OccupancyEntry(mobile, mobileAabb) }));
        }
    }

    sealed class FakeWorld : IMarkerWorld
    {
        public readonly List<ShipMarker> Markers = new List<ShipMarker>();
        public readonly HashSet<string> Generated = new HashSet<string>();
        public Vec3 PlayerPosition { get; private set; } = Vec3.Zero;

        public IReadOnlyList<ShipMarker> MarkersInRange(double radius)
        {
            var output = new List<ShipMarker>();
            foreach (var m in Markers)
                if (m.Position.DistanceTo(PlayerPosition) <= radius)
                    output.Add(m);
            return output;
        }

        public void SetPlayerPosition(Vec3 pos) => PlayerPosition = pos;
        public void MarkGenerated(string markerId) => Generated.Add(markerId);

        public static FakeWorld WithMarkers()
        {
            var w = new FakeWorld();
            w.Markers.Add(new ShipMarker { MarkerId = "0:0:0", Position = new Vec3(30f, 0f, 40f), SizeClass = 1, Condition = 1, ShipType = "freighter", SeedValue = 7 });
            w.Markers.Add(new ShipMarker { MarkerId = "0:1:0", Position = new Vec3(100f, 0f, 0f), SizeClass = 5, Condition = 2, ShipType = "corvette", SeedValue = 8 });
            return w;
        }
    }

    public class ScannerStateTests
    {
        [Test]
        public void DetailGatedBySystemsAndSkill()
        {
            var world = FakeWorld.WithMarkers();
            var scanner = new ScannerState();
            GdDict r0 = scanner.Scan(world, new GdDict { { "navigation", false }, { "scanners", true } }, 10);
            Assert.AreEqual(0L, r0["detail_level"]);
            Assert.IsTrue(((GdArray)r0["markers"]).IsEmpty);

            GdDict r1 = scanner.Scan(world, new GdDict { { "navigation", true }, { "scanners", false } }, 10);
            Assert.AreEqual(1L, r1["detail_level"]);
            var v1 = (GdDict)((GdArray)r1["markers"])[0];
            Assert.IsFalse(v1.Has("ship_type"));
            Assert.AreEqual(50.0, v1.GetFloat("distance"), 1e-5);

            GdDict r6 = scanner.Scan(world, new GdDict { { "navigation", true }, { "scanners", true } }, 10);
            Assert.AreEqual(6L, r6["detail_level"]);
            var v6 = (GdDict)((GdArray)r6["markers"])[1];
            Assert.AreEqual("rich cache, salvageable", v6["loot_hint"]);
            Assert.AreEqual("systems critical", v6["predicted_status"]);
        }

        [Test]
        public void RoundTrip()
        {
            var scanner = new ScannerState { RangeRadius = 333.0, HardwareDetail = 2 };
            GdDict summary = scanner.GetSummary();
            var s2 = new ScannerState();
            Assert.IsTrue(s2.ApplySummary(summary));
            Assert.IsTrue(V.VariantEquals(summary, s2.GetSummary()));
            Assert.IsFalse(s2.ApplySummary(new GdDict()));
        }
    }

    public class TravelControllerTests
    {
        sealed class FakeGenerator : IShipGenerator
        {
            public long LastSeed = -1;
            public object GenerateFromSeed(long seedValue, long size = 0, long condition = 1)
            {
                LastSeed = seedValue;
                return "ship-handle";
            }
        }

        [Test]
        public void GatesThenTravels()
        {
            var world = FakeWorld.WithMarkers();
            var travel = new TravelController();
            var gen = new FakeGenerator();
            ShipMarker target = world.MarkersInRange(250.0)[0];

            var rProp = travel.AttemptTravel(target, new GdDict { { "propulsion", false } }, world, gen, 250.0);
            Assert.AreEqual("propulsion_offline", rProp.Reason);
            Assert.IsEmpty(world.Generated);

            var bogus = new ShipMarker { MarkerId = "9999:9999:0", Position = new Vec3(1000000f, 0f, 0f), SeedValue = 7 };
            Assert.AreEqual("out_of_range", travel.AttemptTravel(bogus, new GdDict { { "propulsion", true } }, world, gen, 250.0).Reason);
            Assert.AreEqual("null_marker", travel.AttemptTravel(null, new GdDict(), world, gen, 250.0).Reason);

            var ok = travel.AttemptTravel(target, new GdDict { { "propulsion", true } }, world, gen, 250.0);
            Assert.IsTrue(ok.Success);
            Assert.AreEqual("ship-handle", ok.Ship);
            Assert.AreEqual(7L, gen.LastSeed);
            Assert.IsTrue(world.Generated.Contains(target.MarkerId));
            Assert.AreEqual(target.Position, world.PlayerPosition);
        }
    }

    public class WebChartStateTests
    {
        [Test]
        public void RecordsUpgradesNeverDowngrades_SkipsMalformed()
        {
            var chart = new WebChartState();
            var views2 = GdArray.Of(
                new GdDict { { "marker_id", "m1" }, { "position", GdArray.Of(10.0, 0.0, 20.0) }, { "size_class", 1L }, { "ship_type", "freighter" } },
                new GdDict { { "marker_id", "m2" }, { "position", GdArray.Of(30.0, 0.0, 40.0) }, { "size_class", 0L }, { "ship_type", "corvette" } });
            Assert.AreEqual(2, chart.RecordViews(views2, 2));
            GdDict e1 = chart.GetEntry("m1");
            Assert.AreEqual(2L, e1["detail"]);
            Assert.IsFalse(e1.Has("condition"));

            var views5 = GdArray.Of(new GdDict
            {
                { "marker_id", "m1" }, { "position", GdArray.Of(10.0, 0.0, 20.0) }, { "size_class", 1L }, { "ship_type", "freighter" },
                { "condition", 1L }, { "predicted_status", "systems degraded" }, { "predicted_offline", GdArray.Of("scanners") },
            });
            Assert.AreEqual(1, chart.RecordViews(views5, 5));
            Assert.AreEqual("systems degraded", chart.GetEntry("m1")["predicted_status"]);
            Assert.AreEqual(0, chart.RecordViews(views2, 2));
            Assert.AreEqual(5L, chart.GetEntry("m1")["detail"]);

            var malformed = GdArray.Of(
                new GdDict { { "position", GdArray.Of(1.0, 1.0, 1.0) }, { "size_class", 0L } },
                new GdDict { { "marker_id", "m3" } },
                "not_a_dict");
            Assert.AreEqual(0, chart.RecordViews(malformed, 3));
            Assert.AreEqual(2, chart.GetKnownCount());
            Assert.IsTrue(V.VariantEquals(GdArray.Of("m1", "m2"), chart.GetKnownMarkerIds()));
            Assert.AreEqual("WebChartState: known=2", chart.GetStatusLines()[0]);
        }
    }
}
