using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class ComponentCatalogTests
    {
        [SetUp]
        public void SetUp() => CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [Test]
        public void LoadsDefaultCatalog_AndReverseLooksUpItemForm()
        {
            var cat = new ComponentCatalog();
            Assert.IsTrue(cat.LoadDefault());
            Assert.Greater(cat.ComponentCount(), 0);
            Assert.IsFalse(cat.HasComponent("definitely_missing"));
            Assert.IsTrue(cat.GetComponent("definitely_missing").IsEmpty);
            Assert.AreEqual("", cat.ComponentIdForItemForm(""));

            var comps = CatalogRegistry.LoadDict(ComponentCatalog.DEFAULT_PATH).GetDict("components");
            foreach (object cid in comps.Keys)
            {
                string id = (string)cid;
                Assert.IsTrue(cat.HasComponent(id));
                string form = V.Str(((GdDict)comps[cid]).Get("item_form", id));
                string resolved = cat.ComponentIdForItemForm(form);
                Assert.IsNotEmpty(resolved);
                string resolvedForm = V.Str(cat.GetComponent(resolved).Get("item_form", resolved));
                Assert.IsTrue(resolvedForm == form || resolved == form, $"{form} -> {resolved}");
            }
        }

        [Test]
        public void RoleSet_FallsBackToDefaultRole()
        {
            var cat = new ComponentCatalog();
            Assert.IsTrue(cat.LoadDefault());
            var roles = CatalogRegistry.LoadDict(ComponentCatalog.DEFAULT_PATH).GetDict("role_sets");
            if (roles == null || !(roles.Get("default") is GdDict def) || def.IsEmpty)
            {
                Assert.Ignore("catalog has no default role set");
                return;
            }
            string slotKind = (string)def.Keys[0];
            Assert.IsTrue(V.VariantEquals(def[slotKind], cat.RoleSet("no_such_role", slotKind)));
        }
    }
}
