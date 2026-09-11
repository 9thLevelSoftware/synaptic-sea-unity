using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class AtmosphereApplierTests
    {
        GameObject _root;

        [SetUp] public void SetUp() => _root = new GameObject("ShipRoot");
        [TearDown] public void TearDown() => Object.DestroyImmediate(_root);

        static GdDict Atmosphere(string biome) =>
            GdJson.ParseDict(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "data", "procgen", "biomes", biome + ".json"))).GetDict("atmosphere");

        [Test]
        public void BreachFieldMatchesTheGodotApplier()
        {
            var summary = AtmosphereApplier.Apply(_root.transform, Atmosphere("breach_field"), isAway: false);
            Assert.IsTrue(summary.GetBool("applied"));
            Assert.AreEqual(AmbientMode.Flat, RenderSettings.ambientMode);
            Color expectedAmbient = AtmosphereApplier.ColorValue("#1a2430", Color.black) * 0.35f;
            Assert.That(Vector4.Distance(expectedAmbient, RenderSettings.ambientLight), Is.LessThan(1e-4f));
            Assert.IsTrue(RenderSettings.fog);
            Assert.AreEqual(FogMode.Exponential, RenderSettings.fogMode);
            Assert.AreEqual(0.02f, RenderSettings.fogDensity, 1e-6f);

            var key = _root.transform.Find(AtmosphereApplier.KeyLightName).GetComponent<Light>();
            Assert.AreEqual(LightType.Directional, key.type);
            Assert.AreEqual(0.55f * AtmosphereApplier.DirectionalEnergyScale, key.intensity, 1e-5f);
            // Godot key light at rotation (-55, -35, 0) shines along its -Z: down and toward the mirrored quadrant.
            Vector3 godotDir = Quaternion.Euler(-55f, -35f, 0f) * Vector3.back;
            Vector3 expectedUnityDir = new Vector3(-godotDir.x, godotDir.y, godotDir.z);
            Assert.That(Vector3.Angle(expectedUnityDir, key.transform.forward), Is.LessThan(0.01f));

            var accent = _root.transform.Find(AtmosphereApplier.AccentLightName).GetComponent<Light>();
            Assert.AreEqual(LightType.Point, accent.type);
            Assert.AreEqual(12f, accent.range);
        }

        [Test]
        public void AwayMultipliesFogDensity()
        {
            var summary = AtmosphereApplier.Apply(_root.transform, Atmosphere("breach_field"), isAway: true);
            Assert.AreEqual(0.02 * 1.6, summary.GetFloat("fog_density"), 1e-6);
            Assert.AreEqual(0.032f, RenderSettings.fogDensity, 1e-6f);
        }
    }
}
