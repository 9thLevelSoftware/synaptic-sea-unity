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

        [Test]
        public void InteriorZoomIsCloserBoundedAndDoesNotChangeTheFollowGeometry()
        {
            _go = new GameObject("IsoCameraRig");
            var rig = _go.AddComponent<IsoCameraRig>();
            var cam = rig.EnsureCamera();
            Assert.AreEqual(7f, cam.orthographicSize);
            Assert.IsTrue(cam.orthographic);
            Vector3 offset = rig.godotOffset;
            rig.ZoomByWheel(120);
            Assert.AreEqual(6f, rig.TargetViewSize);
            Assert.AreEqual(7f, cam.orthographicSize, "zoom is eased, not an instantaneous pop");
            for (int i = 0; i < 60; i++) rig.AdvanceZoom(1f / 60f);
            Assert.AreEqual(6f, cam.orthographicSize, 0.001f);
            rig.ZoomByWheel(-120);
            Assert.AreEqual(7f, rig.TargetViewSize);
            rig.SetViewSize(-100);
            Assert.AreEqual(4f, cam.orthographicSize);
            rig.SetViewSize(100);
            Assert.AreEqual(11f, cam.orthographicSize);
            rig.SetViewSize(float.NaN);
            rig.ZoomByWheel(float.PositiveInfinity);
            Assert.AreEqual(11f, cam.orthographicSize);
            Assert.AreEqual(offset, rig.godotOffset);
        }

        [Test]
        public void DiagonalFloorProjectionIsApproximatelyTwoToOne()
        {
            _go = new GameObject("IsoCameraRig");
            var target = new GameObject("CameraTarget");
            try
            {
                var rig = _go.AddComponent<IsoCameraRig>();
                rig.SetFollowTarget(target.transform);
                var cam = rig.Camera;
                var origin = cam.WorldToScreenPoint(Vector3.zero);
                var alongX = cam.WorldToScreenPoint(Vector3.right) - origin;
                var alongZ = cam.WorldToScreenPoint(Vector3.forward) - origin;
                Assert.AreEqual(0.5f, Mathf.Abs(alongX.y / alongX.x), 0.002f);
                Assert.AreEqual(0.5f, Mathf.Abs(alongZ.y / alongZ.x), 0.002f);
            }
            finally { Object.DestroyImmediate(target); }
        }
    }
}
