using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Design rule: no ceilings while inside a ship; exterior views turn them back on.</summary>
    public class IsoCameraRigTests
    {
        GameObject _go;

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        [Test]
        public void CeilingsAreCulledByDefaultAndCanBeShown()
        {
            _go = new GameObject("IsoCameraRig");
            var rig = _go.AddComponent<IsoCameraRig>();
            Camera cam = rig.EnsureCamera();
            int ceiling = 1 << PhysicsLayers.Ceiling;
            int structure = 1 << PhysicsLayers.Structure;

            Assert.AreEqual(0, cam.cullingMask & ceiling, "interior view hides ceilings");
            Assert.AreNotEqual(0, cam.cullingMask & structure, "walls and floors still render");

            rig.SetShowCeilings(true);
            Assert.AreNotEqual(0, cam.cullingMask & ceiling, "exterior view shows ceilings");

            rig.SetShowCeilings(false);
            rig.SyncToTarget();
            Assert.AreEqual(0, cam.cullingMask & ceiling);
        }
    }
}
