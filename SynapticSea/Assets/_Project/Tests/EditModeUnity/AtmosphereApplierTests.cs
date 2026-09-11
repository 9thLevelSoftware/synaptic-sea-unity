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
            // Godot: linear(ambient_color) * ambient_energy. Unity's ambientLight is sRGB, so it holds the encoded product,
            // and the SH probe URP actually shades with carries the linear value.
            Color expectedLinear = AtmosphereApplier.ColorValue("#1a2430", Color.black).linear * (0.35f * AtmosphereApplier.AmbientEnergyScale);
            Assert.That(Vector4.Distance(expectedLinear.gamma, RenderSettings.ambientLight), Is.LessThan(1e-4f));
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(expectedLinear);
            Assert.AreEqual(probe[0, 0], RenderSettings.ambientProbe[0, 0], 1e-5f);
            Assert.AreEqual(probe[2, 0], RenderSettings.ambientProbe[2, 0], 1e-5f);
            Assert.IsTrue(RenderSettings.fog);
            Assert.AreEqual(FogMode.Exponential, RenderSettings.fogMode);
            Assert.AreEqual(0.02f, RenderSettings.fogDensity, 1e-6f);
            Assert.AreEqual(RenderSettings.fogColor, AtmosphereApplier.BackgroundColor, "Godot fog covers the background");

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
        public void CalibratedConstantsAreTheDefaults()
        {
            // Measured against fixtures/godot/screens (docs/port-status.md, calibration).
            Assert.AreEqual(1.3f, AtmosphereApplier.CalibratedDirectionalEnergyScale);
            Assert.AreEqual(2.5f, AtmosphereApplier.CalibratedOmniEnergyScale);
            Assert.AreEqual(1.0f, AtmosphereApplier.CalibratedAmbientEnergyScale);
            Assert.AreEqual(AtmosphereApplier.CalibratedDirectionalEnergyScale, AtmosphereApplier.DirectionalEnergyScale);
            Assert.AreEqual(AtmosphereApplier.CalibratedOmniEnergyScale, AtmosphereApplier.OmniEnergyScale);
            Assert.AreEqual(AtmosphereApplier.CalibratedAmbientEnergyScale, AtmosphereApplier.AmbientEnergyScale);
        }

        [Test]
        public void GodotDefaultEnvironmentUsesTheClearColour()
        {
            AtmosphereApplier.ApplyGodotDefaultEnvironment();
            Assert.IsFalse(RenderSettings.fog);
            Assert.AreEqual(AtmosphereApplier.GodotClearColor, AtmosphereApplier.BackgroundColor);
            Color linear = AtmosphereApplier.GodotClearColor.linear * AtmosphereApplier.AmbientEnergyScale;
            Assert.That(Vector4.Distance(linear.gamma, RenderSettings.ambientLight), Is.LessThan(1e-4f));
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
