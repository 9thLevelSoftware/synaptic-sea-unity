using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class InventorySelectionModelTests
    {
        [SetUp] public void SetUp() => ItemsTestData.Use();
        [TearDown] public void TearDown() => ItemsTestData.Reset();

        static GdArray Sel(InventorySelectionModel m) => m.GetSelectedIds();

        [Test]
        public void SelectionMath()
        {
            var m = new InventorySelectionModel();
            m.SetIds(GdArray.Of("a", "b", "c", "d", "e"));
            m.SelectSingle(1);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("b"), Sel(m)));
            m.SelectRangeTo(3);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("b", "c", "d"), Sel(m)));
            m.Toggle(2);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("b", "d"), Sel(m)));
            m.SelectSingle(3);
            m.SelectRangeTo(1);
            Assert.IsTrue(V.VariantEquals(GdArray.Of("b", "c", "d"), Sel(m)), "reverse range");
            m.SelectSingle(4);
            m.SetIds(GdArray.Of("x", "y"));
            Assert.IsTrue(Sel(m).IsEmpty, "shrunk id list drops stale selection");
        }

        [Test]
        public void ContextActions()
        {
            GdDict defs = ItemDefs.LoadDefinitions();
            Assert.AreEqual(new List<string> { "transfer", "transfer_all", "split" },
                InventorySelectionModel.ContextActions("scrap_metal", defs, true, true, false));
            Assert.AreEqual(new List<string> { "equip" },
                InventorySelectionModel.ContextActions("hardsuit", defs, false, false, false));
            CollectionAssert.Contains(InventorySelectionModel.ContextActions("hardsuit", defs, true, true, false), "equip");
            Assert.AreEqual(new List<string> { "unequip" },
                InventorySelectionModel.ContextActions("hardsuit", defs, false, false, true));
        }
    }
}
