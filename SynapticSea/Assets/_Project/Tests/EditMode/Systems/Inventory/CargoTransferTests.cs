using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class CargoTransferTests
    {
        const string Part = "scrap_metal";
        const string Supply = "ration_pack";
        const string Tool = "portable_oxygen_pump";

        static readonly Dictionary<string, (string, double)> Defs = new Dictionary<string, (string, double)>
        {
            { Part, ("part", 5.0) },
            { Supply, ("supply", 0.5) },
            { Tool, ("tool", 2.0) },
        };

        [Test]
        public void MoveItemHonorsDestinationCapAndConserves()
        {
            var src = new FakeCargo(Defs);
            src.AddItem(Part, 5);
            var tiny = new FakeCargo(Defs, 12.0);
            long moved = CargoTransfer.MoveItem(src, tiny, Part, 5);
            Assert.AreEqual(2L, moved, "a 12-weight hold accepts floor(12/5)=2");
            Assert.AreEqual(3L, src.GetQuantity(Part));
            Assert.AreEqual(5L, src.GetQuantity(Part) + tiny.GetQuantity(Part));
            Assert.AreEqual(0L, CargoTransfer.MoveItem(src, tiny, Part, 0));
        }

        [Test]
        public void MoveItemsAndDepositAll()
        {
            var player = new FakeCargo(Defs);
            player.AddItem(Tool, 1);
            player.AddItem(Supply, 3);
            var hold = new FakeCargo(Defs, 1000.0);
            Assert.AreEqual(4L, CargoTransfer.MoveItems(player, hold, new GdDict { { Tool, 1L }, { Supply, 3L } }));
            Assert.AreEqual(1L, hold.GetQuantity(Tool));

            var p2 = new FakeCargo(Defs);
            p2.AddItem(Tool, 1);
            p2.AddItem(Part, 2);
            p2.AddItem(Supply, 4);
            GdDict result = CargoTransfer.DepositAll(p2, hold);
            Assert.AreEqual(6L, result["total_moved"]);
            Assert.AreEqual(1L, p2.GetQuantity(Tool), "tools are never auto-deposited");
            Assert.AreEqual(new List<object> { Supply, Part }, new List<object>(((GdDict)result["moved"]).Keys), "sorted id order");

            GdDict back = CargoTransfer.WithdrawCategory(hold, p2, "part");
            Assert.AreEqual(2L, back["total_moved"]);
        }
    }
}
