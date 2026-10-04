using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    // Provisioned runtime-contract probes, not evidence of an earned unlock-tool source.
    public class FrozenPortalAccessTests
    {
        static GdDict EntrySpec(int condition)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "data/worldgen-fixtures/reviewed-export-parity",
                "seed-42-size-2-condition-" + condition, "layout.json");
            var layout = (GdDict)GdJson.ParseString(File.ReadAllText(path));
            string start = layout.GetDictOrEmpty("prototype").GetString("start_room");
            return layout.GetArrayOrEmpty("portals").Cast<GdDict>().Single(p =>
                p.GetBool("required") && p.GetString("from_room") == start);
        }

        static AuthoredPortalRuntime MakePortal(GdDict spec)
        {
            var node = new GameObject("FrozenPortalContract");
            var portal = node.AddComponent<AuthoredPortalRuntime>();
            portal.Configure(spec, Vec3.Zero);
            return portal;
        }

        [TestCase(0)]
        [TestCase(2)]
        public void RequiredLockedEntryRefusesAbsentOrEmptyChargeAndCrowbarFlag(int condition)
        {
            AuthoredPortalRuntime portal = MakePortal(EntrySpec(condition));
            try
            {
                Assert.AreEqual("LOCKED", portal.portalKind);
                Assert.AreEqual("lockpick", portal.RequiredFlag());
                foreach (var flags in new[] { new GdDict(), new GdDict { { "crowbar", true } },
                    new GdDict { { "lockpick", new GdDict { { "count", 0L } } } } })
                {
                    GdDict result = portal.TryInteractAt(flags, portal.transform.position);
                    Assert.IsFalse(result.GetBool("ok"));
                    Assert.AreEqual("locked", result.GetString("reason"));
                    Assert.AreEqual("lockpick", result.GetString("needs"));
                    Assert.IsFalse(portal.isUnlocked);
                    Assert.IsFalse(portal.isOpen);
                    Assert.IsTrue(portal.GetBlockerCollider().enabled);
                }
            }
            finally { Object.DestroyImmediate(portal.gameObject); }
        }

        [TestCase(0)]
        [TestCase(2)]
        public void ProvisionedChargeUnlocksOnceAndRetainedStateAllowsReturnWithoutAnotherCharge(int condition)
        {
            GdDict spec = EntrySpec(condition);
            AuthoredPortalRuntime portal = MakePortal(spec), restored = null, untouched = null;
            try
            {
                GdDict charge = new GdDict { { "lockpick", new GdDict { { "count", 1L } } } };
                Assert.AreEqual("out_of_range", portal.TryInteractAt(charge, portal.transform.position + Vector3.right * 10).GetString("reason"));
                Assert.IsFalse(portal.isUnlocked);
                GdDict unlocked = portal.TryInteractAt(charge, portal.transform.position);
                Assert.IsTrue(unlocked.GetBool("ok")); Assert.IsTrue(unlocked.GetBool("unlocked_now"));
                Assert.IsTrue(portal.isOpen); Assert.IsFalse(portal.GetBlockerCollider().enabled);
                // Session owns charge consumption when unlocked_now is true; runtime must not request it again.
                GdDict closed = portal.TryInteractAt(new GdDict(), portal.transform.position);
                Assert.IsTrue(closed.GetBool("ok")); Assert.IsFalse(closed.GetBool("unlocked_now"));
                Assert.IsFalse(portal.isOpen); Assert.IsTrue(portal.GetBlockerCollider().enabled);
                restored = MakePortal(spec); restored.RestorePersistentState(portal.isUnlocked, portal.isOpen);
                GdDict returned = restored.TryInteractAt(new GdDict(), restored.transform.position);
                Assert.IsTrue(returned.GetBool("ok")); Assert.IsTrue(restored.isOpen);
                Assert.IsFalse(returned.GetBool("unlocked_now"));
                untouched = MakePortal(spec);
                Assert.IsFalse(untouched.TryInteractAt(new GdDict(), untouched.transform.position).GetBool("ok"),
                    "Unlock state is instance-local; other secured portals remain locked.");
            }
            finally
            {
                if (untouched != null) Object.DestroyImmediate(untouched.gameObject);
                if (restored != null) Object.DestroyImmediate(restored.gameObject);
                Object.DestroyImmediate(portal.gameObject);
            }
        }
    }
}
