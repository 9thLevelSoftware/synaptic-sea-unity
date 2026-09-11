using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;

namespace SynapticSea.Tests.Unity
{
    /// <summary>The four Godot scenes/vfx/*.tscn ported to prefabs by VfxPrefabBuilder.</summary>
    public class VfxCatalogTests
    {
        static readonly string[] Ids = { VfxCatalog.TimedFire, VfxCatalog.BeaconBlue, VfxCatalog.ReactorGreen, VfxCatalog.BiomatterBlockage };

        [Test]
        public void CatalogListsTheFourGodotScenes()
        {
            var catalog = VfxCatalog.Load();
            Assert.IsNotNull(catalog, "Resources/Catalogs/VfxCatalog missing (run VfxPrefabBuilder.BuildAll)");
            CollectionAssert.AreEquivalent(Ids, catalog.Ids.ToArray());
            foreach (var id in Ids)
            {
                Assert.IsTrue(catalog.TryGet(id, out var entry), id);
                var effect = entry.prefab.GetComponent<VfxEffect>();
                Assert.IsNotNull(effect, id);
                Assert.AreEqual(id, effect.vfxId);
                Assert.IsNotNull(entry.prefab.GetComponentInChildren<Light>(true), $"{id}: Godot OmniLight3D");
                Assert.IsNotEmpty(effect.tracks, $"{id}: autoplay animation");
            }
        }

        [Test]
        public void TimedFireMapsTheGodotParticleEmitters()
        {
            Assert.IsTrue(VfxCatalog.Load().TryGet(VfxCatalog.TimedFire, out var entry));
            // Only timed_fire has GPUParticles3D; the glow scenes are mesh + light + pulse.
            var systems = entry.prefab.GetComponentsInChildren<ParticleSystem>(true).ToDictionary(p => p.name);
            CollectionAssert.AreEquivalent(new[] { "FireParticles", "SmokeParticles", "EmberParticles" }, systems.Keys);

            var fire = systems["FireParticles"];
            Assert.AreEqual(28, fire.main.maxParticles);
            Assert.AreEqual(1.35f, fire.main.startLifetime.constant, 1e-5f);
            Assert.AreEqual(28f / 1.35f, fire.emission.rateOverTime.constant, 1e-3f);
            Assert.AreEqual(0.7f, fire.main.startSpeed.constantMin, 1e-5f);
            Assert.AreEqual(1.55f, fire.main.startSpeed.constantMax, 1e-5f);
            Assert.AreEqual(28f, fire.shape.angle, 1e-4f);
            Assert.AreEqual(-0.65f, fire.forceOverLifetime.y.constant, 1e-5f, "Godot gravity (0, -0.65, 0)");
            Assert.AreEqual(0.26f * 0.45f, fire.main.startSize.constantMin, 1e-5f, "SphereMesh diameter x scale_min");
            Assert.IsTrue(fire.main.prewarm);
            Assert.IsTrue(fire.colorOverLifetime.enabled, "color_ramp");
            Assert.AreEqual(0.18f, systems["SmokeParticles"].forceOverLifetime.y.constant, 1e-5f);
            Assert.AreEqual(0.22f, systems["SmokeParticles"].transform.localPosition.y, 1e-5f);
            Assert.AreEqual(22, systems["EmberParticles"].main.maxParticles);

            var light = entry.prefab.GetComponentInChildren<Light>();
            Assert.AreEqual(LightType.Point, light.type);
            Assert.AreEqual(3f, light.range);
            var effect = entry.prefab.GetComponent<VfxEffect>();
            Assert.AreEqual(2.2f, effect.lights.Single().godotEnergy, 1e-5f);
            Assert.AreEqual("flicker", effect.animationName);
            Assert.AreEqual(1.4f, effect.animationLength, 1e-5f);
            Assert.AreEqual(2.45f, VfxEffect.Sample(effect.tracks[0], 0.29f), 1e-4f);
        }

        [Test]
        public void GlowSourcesKeepTheirEmission()
        {
            foreach (var id in new[] { VfxCatalog.BeaconBlue, VfxCatalog.ReactorGreen, VfxCatalog.BiomatterBlockage })
            {
                Assert.IsTrue(VfxCatalog.Load().TryGet(id, out var entry));
                foreach (var r in entry.prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Assert.IsTrue(r.sharedMaterial.IsKeywordEnabled("_EMISSION"), $"{id}/{r.name}: emission stripped");
                    Assert.Greater(r.sharedMaterial.GetColor("_EmissionColor").maxColorComponent, 0f, id);
                }
            }
        }

        [Test]
        public void SpawnPlacesTheEffectAtAGodotPosition()
        {
            var parent = new GameObject("ShipRoot");
            try
            {
                var fx = VfxCatalog.Spawn(VfxCatalog.BeaconBlue, parent.transform, new Vec3(2f, 1f, -3f));
                Assert.IsNotNull(fx);
                Assert.AreSame(parent.transform, fx.transform.parent);
                Assert.AreEqual(Frame.ToUnity(new Vec3(2f, 1f, -3f)), fx.transform.localPosition);
                Assert.IsNull(VfxCatalog.Spawn("no_such_vfx", parent.transform, Vec3.Zero));
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }
    }
}
