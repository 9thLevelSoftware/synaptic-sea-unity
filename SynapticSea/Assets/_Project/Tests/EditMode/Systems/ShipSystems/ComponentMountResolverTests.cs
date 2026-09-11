using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ComponentMountResolverTests
    {
        /// <summary>One-slot stand-in for ComponentPlacementState's dismount/remount rules.</summary>
        sealed class FakePlacement : ComponentMountResolver.IComponentPlacement
        {
            public bool Mounted = true;
            public string LastMountArgs = "";

            public GdDict Dismount(string instanceId)
            {
                if (instanceId != "engine_wall_0") return new GdDict { { "ok", false }, { "reason", "not_found" } };
                if (!Mounted) return new GdDict { { "ok", false }, { "reason", "already_dismounted" } };
                Mounted = false;
                return new GdDict
                {
                    { "ok", true }, { "reason", "" }, { "item_form", "power_coupling" }, { "mass", 12.0 }, { "qty", 1L },
                    { "component_id", "coupling" }, { "instance_id", instanceId }, { "linked_system", "power" }, { "linked_subcomponent", "bus" },
                };
            }

            public GdDict Mount(string itemForm, string roomId, string slotKind, long slotIndex, GdDict inventory, ComponentCatalog catalog = null)
            {
                LastMountArgs = roomId + "|" + slotKind + "|" + slotIndex + "|" + itemForm;
                if (V.I64(inventory.Get(itemForm, 0L)) < 1) return new GdDict { { "ok", false }, { "reason", "missing_item" } };
                if (Mounted) return new GdDict { { "ok", false }, { "reason", "slot_occupied" } };
                Mounted = true;
                inventory[itemForm] = V.I64(inventory[itemForm]) - 1;
                if (V.I64(inventory[itemForm]) <= 0) inventory.Erase(itemForm);
                return new GdDict { { "ok", true }, { "instance_id", "engine_wall_0" } };
            }
        }

        static WorkActionState Work(string targetId, GdDict def)
        {
            var w = new WorkActionState();
            w.ConfigureAction("unbolt", def);
            w.Status = WorkActionState.STATUS_COMPLETED;
            w.TargetId = targetId;
            return w;
        }

        [Test]
        public void DismountThenRemount()
        {
            var placement = new FakePlacement();
            var inventory = new GdDict();
            var idle = new WorkActionState { TargetId = "engine_wall_0" };
            Assert.AreEqual("not_completed", ComponentMountResolver.ResolveDismount(idle, placement, inventory)["reason"]);
            Assert.AreEqual("no_target", ComponentMountResolver.ResolveDismount(Work("", new GdDict()), placement, inventory)["reason"]);

            GdDict down = ComponentMountResolver.ResolveDismount(Work("engine_wall_0", new GdDict { { "noise", 0.4 }, { "xp_event", "salvage" } }), placement, inventory);
            Assert.AreEqual(true, down["ok"]);
            Assert.AreEqual(1L, inventory["power_coupling"]);
            Assert.AreEqual(12.0, down["mass"]);
            Assert.AreEqual(0.4, down["noise"]);
            Assert.AreEqual("salvage", down["xp_event"]);
            Assert.AreEqual("power", down["linked_system"]);

            Assert.AreEqual("bad_target", ComponentMountResolver.ResolveMount(Work("nope", new GdDict()), placement, inventory)["reason"]);
            GdDict up = ComponentMountResolver.ResolveMount(Work("engine|wall|0|power_coupling", new GdDict { { "xp_event", "repair" } }), placement, inventory);
            Assert.AreEqual(true, up["ok"]);
            Assert.AreEqual("engine|wall|0|power_coupling", placement.LastMountArgs);
            Assert.AreEqual("engine_wall_0", up["instance_id"]);
            Assert.IsFalse(inventory.Has("power_coupling"), "mount consumed the item");
        }
    }
}
