using System.Collections;
using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Session;
using UnityEngine;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.PlayMode
{
    public class AuxiliaryUtilityViewTests
    {
        [UnityTest]
        public IEnumerator LightsFollowCommittedHardwareAndLivePowerWithoutPhysics()
        {
            var owner = new GameObject("AuxiliaryTestOwner");
            try
            {
                foreach (string id in new[] { "medbay_task_light_01", "airlock_dock_beacon_01" })
                {
                    var fixture = AuxiliaryUtilityView.CreateFixture(id, owner.transform);
                    var view = fixture.GetComponent<AuxiliaryUtilityView>();
                    var effects = new GdDict { { "hardware_ready", false }, { "powered", true }, { "status", "Needs repair" } };
                    view.BindEffects(() => effects);
                    Assert.IsFalse(view.Lamp.enabled, "power alone cannot repair hardware");
                    yield return null;
                    Assert.AreEqual(0, fixture.GetComponentsInChildren<Collider>(true).Length);
                    effects["hardware_ready"] = true; effects["status"] = "Online"; view.Refresh();
                    Assert.IsTrue(view.Lamp.enabled);
                    StringAssert.Contains("Online", view.StatusText.text);
                    effects["powered"] = false; effects["status"] = "Repaired — no power"; view.Refresh();
                    Assert.IsFalse(view.Lamp.enabled);
                    Assert.IsTrue(view.HardwareReady);
                    StringAssert.Contains("no power", view.StatusText.text);
                    effects["powered"] = true; effects["status"] = "Dock: 1 connected; airlock open"; view.Refresh();
                    Assert.IsTrue(view.Lamp.enabled);
                    StringAssert.Contains("airlock open", view.StatusText.text);
                }
            }
            finally { Object.Destroy(owner); }
            yield return null;
        }
    }
}
