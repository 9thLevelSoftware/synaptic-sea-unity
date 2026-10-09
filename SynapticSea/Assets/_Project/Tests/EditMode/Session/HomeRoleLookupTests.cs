using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    /// <summary>
    /// Phase 1.10: the role lookup that replaces the golden hub's room-id literals. On the golden hub it must resolve to the authored rooms the literals
    /// named (so golden behaviour is unchanged); on any other layout it resolves by role and never returns a passage.
    /// </summary>
    public class HomeRoleLookupTests
    {
        static GdDict Golden() =>
            GdJson.ParseDict(File.ReadAllText(Path.Combine(Fixtures.StreamingDataRoot, "data", "procgen", "golden", "coherent_ship_001", "layout.json")));

        static GdDict Layout(params (string id, string role)[] rooms)
        {
            var array = new GdArray();
            foreach ((string id, string role) in rooms) array.Append(new GdDict { { "id", id }, { "room_role", role } });
            return new GdDict { { "rooms", array } };
        }

        [TestCase("objective:recover_supplies", "cargo_01")]
        [TestCase("objective:restore_systems", "maintenance_01")]
        [TestCase("objective:download_logs", "medbay_01")]
        [TestCase("objective:stabilize_reactor", "reactor_01")]
        [TestCase("tool_pickup", "maintenance_01")]
        [TestCase("home_mooring", "cargo_01")]
        public void TheGoldenHubResolvesToItsAuthoredRooms(string kind, string expected)
        {
            Assert.AreEqual(expected, StationPlacer.RoomIdsByPreference(Golden(), kind).First(), kind);
        }

        [Test]
        public void PassagesAreNeverReturned_AndEveryUsableRoomIsListedOnce()
        {
            GdDict golden = Golden();
            foreach (string kind in new[] { "objective:restore_systems", "tool_pickup", "calibrator_pickup", "home_mooring", "unknown_kind" })
            {
                var ids = StationPlacer.RoomIdsByPreference(golden, kind);
                CollectionAssert.AllItemsAreUnique(ids);
                CollectionAssert.IsSubsetOf(ids, new[] { "cargo_01", "medbay_01", "maintenance_01", "reactor_01" });
                Assert.AreEqual(4, ids.Count, kind);
            }
        }

        [Test]
        public void ARolesPriorityOrdersTheRooms_ThenOtherRoomsFollowInLayoutOrder()
        {
            GdDict layout = Layout(("airlock_01", "airlock"), ("crew_quarters_01", "crew_quarters"), ("bridge_01", "bridge"), ("medical_01", "medical"), ("engineering_01", "engineering"));
            Assert.AreEqual(new[] { "medical_01", "bridge_01", "crew_quarters_01", "engineering_01" },
                StationPlacer.RoomIdsByPreference(layout, "objective:download_logs").ToArray());
            Assert.AreEqual(new[] { "engineering_01", "crew_quarters_01", "bridge_01", "medical_01" },
                StationPlacer.RoomIdsByPreference(layout, "tool_pickup").ToArray());
        }

        [Test]
        public void ALayoutWithoutRoomsOrAKindOfNoPreferenceStillAnswers()
        {
            Assert.IsEmpty(StationPlacer.RoomIdsByPreference(null, "tool_pickup"));
            Assert.IsEmpty(StationPlacer.RoomIdsByPreference(new GdDict(), "tool_pickup"));
            Assert.AreEqual(new[] { "a_01" }, StationPlacer.RoomIdsByPreference(Layout(("corridor_01", "corridor"), ("a_01", "armory")), "no_such_kind").ToArray());
        }
    }
}
