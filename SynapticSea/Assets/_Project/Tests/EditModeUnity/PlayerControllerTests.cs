using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class PlayerControllerTests
    {
        GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("Player", typeof(CharacterController), typeof(PlayerController));
            _go.GetComponent<PlayerController>().EnsureSupportComponents(); // Awake does not run in edit mode
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        [Test]
        public void EffectiveSpeedAppliesVitalsGateAndCrouch()
        {
            var p = _go.GetComponent<PlayerController>();
            Assert.AreEqual(6f, p.GetEffectiveMoveSpeed());
            p.SetMovementSpeedMultiplier(0.5f);
            Assert.AreEqual(3f, p.GetEffectiveMoveSpeed());
            p.SetCrouching(true);
            Assert.AreEqual(1.5f, p.GetEffectiveMoveSpeed());
            p.SetMovementSpeedMultiplier(2f);
            Assert.AreEqual(3f, p.GetEffectiveMoveSpeed(), "multiplier clamps to 1");
        }

        [Test]
        public void VelocityFollowsTheGodotUpdateRule()
        {
            var v = PlayerController.ComputeVelocity(new Vec3(0f, -3f, 0f), new Vec3(1f, 0f, 0f), 6f, 9.8f, onFloor: true, delta: 1f / 60f);
            Assert.AreEqual(6f, v.X);
            Assert.AreEqual(0f, v.Y, "landing zeroes downward velocity");
            v = PlayerController.ComputeVelocity(Vec3.Zero, Vec3.Zero, 6f, 9.8f, onFloor: false, delta: 0.5f);
            Assert.AreEqual(-4.9f, v.Y, 1e-5f);
        }

        [Test]
        public void GodotRightMovesAlongMirroredUnityX()
        {
            // move_right is Godot +X, which Frame maps to Unity -X so the on-screen direction matches Godot.
            Vector3 unity = Frame.ToUnity(new Vec3(6f, 0f, 0f));
            Assert.AreEqual(-6f, unity.x);
        }

        [Test]
        public void SupportComponentsMatchGodotCapsule()
        {
            var cc = _go.GetComponent<CharacterController>();
            Assert.AreEqual(0.35f, cc.radius);
            Assert.AreEqual(1.6f, cc.height);
            Assert.AreEqual(60f, cc.slopeLimit);
            Assert.AreEqual(PlayerController.PlayerLayer, _go.layer);
            Assert.IsNotNull(_go.transform.Find("PlayerMarker"));
            Assert.IsNull(_go.transform.Find("PlayerMarker").GetComponent<Collider>());
        }

        [Test]
        public void InteractRaisesEvent()
        {
            var p = _go.GetComponent<PlayerController>();
            PlayerController seen = null;
            p.InteractRequested += x => seen = x;
            p.RequestInteract();
            Assert.AreSame(p, seen);
        }
    }
}
