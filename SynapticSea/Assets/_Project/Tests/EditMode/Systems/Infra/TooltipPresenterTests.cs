using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    public class TooltipPresenterTests
    {
        static GdDict Catalog() => new GdDict
        {
            { "version", "tooltip-catalog-1" },
            {
                "entries", GdArray.Of(
                    new GdDict
                    {
                        { "id", "item_circuit_board" }, { "subject_kind", "item" }, { "subject_id", "circuit_board" },
                        { "title", "Circuit Board" }, { "body", "Repair part." }, { "footer", "[E] Pick up" },
                    },
                    new GdDict
                    {
                        { "id", "interactable_door" }, { "subject_kind", "interactable" }, { "subject_id", "door" },
                        { "title", "Door" },
                    })
            },
        };

        [Test]
        public void RoundTrip_SummaryMatches()
        {
            // TooltipPresenter has no apply_summary; two presenters driven identically must summarize identically.
            var a = new TooltipPresenter();
            var b = new TooltipPresenter();
            Assert.IsTrue(a.Configure(Catalog()));
            Assert.IsTrue(b.Configure(Catalog()));
            var q = new GdDict { { "subject_kind", "item" }, { "subject_id", "circuit_board" } };
            a.Resolve(q);
            b.Resolve(q);
            Assert.IsTrue(V.VariantEquals(a.GetSummary(), b.GetSummary()));
            Assert.AreEqual(2, a.GetSummary().GetInt("catalog_size"));
            Assert.IsTrue(a.GetSummary().GetBool("has_payload"));
        }

        [Test]
        public void Resolve_MatchesSmokeAndEmits()
        {
            var presenter = new TooltipPresenter();
            Assert.IsTrue(presenter.Configure(Catalog()));
            int emitted = 0;
            TooltipPayload last = null;
            presenter.PayloadChanged += p => { emitted++; last = p; };
            TooltipPayload payload = presenter.Resolve(new GdDict { { "subject_kind", "item" }, { "subject_id", "circuit_board" } });
            Assert.IsNotNull(payload);
            Assert.AreEqual("Circuit Board", payload.Title);
            Assert.AreEqual("[E] Pick up", payload.Footer);
            Assert.AreSame(payload, last);
            Assert.IsNull(presenter.Resolve(new GdDict { { "subject_kind", "item" }, { "subject_id", "nope" } }));
            Assert.IsNull(presenter.GetCurrentPayload());
            Assert.AreEqual(2, emitted);
            Assert.AreEqual(GdArray.Of("interactable/door", "item/circuit_board").ToString(), presenter.GetCatalogPairs().ToString());
        }

        [Test]
        public void Configure_RejectsInvalidCatalog()
        {
            var presenter = new TooltipPresenter();
            Assert.IsFalse(presenter.Configure(new GdDict { { "version", "wrong" }, { "entries", new GdArray() } }));
            GdDict dup = Catalog();
            ((GdDict)dup.GetArray("entries")[1])["id"] = "item_circuit_board";
            Assert.IsFalse(TooltipSchema.Validate(dup));
        }
    }
}
