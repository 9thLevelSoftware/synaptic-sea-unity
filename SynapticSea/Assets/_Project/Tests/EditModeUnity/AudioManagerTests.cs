using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class AudioManagerTests
    {
        GameObject _go;
        AudioManager _audio;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("Audio");
            _audio = _go.AddComponent<AudioManager>();
            _audio.Catalog = AssetDatabase.LoadAssetAtPath<AudioCatalog>("Assets/Resources/Catalogs/AudioCatalog.asset");
            _audio.Initialize();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        [Test]
        public void CatalogCoversEveryStreamId()
        {
            Assert.IsNotNull(_audio.Catalog, "run AudioCatalogBuilder");
            foreach (var kv in AudioManager.StreamCatalog)
                Assert.IsTrue(_audio.Catalog.TryGetClip(kv.Value, out _), kv.Key);
        }

        [Test]
        public void BuildsOnePlayerPerChildBus()
        {
            foreach (string bus in new[] { "sfx", "music", "voice", "ui", "ambient", "meta" })
                Assert.IsNotNull(_audio.GetBusPlayer(bus), bus);
            Assert.IsNull(_audio.GetBusPlayer("master"));
        }

        [Test]
        public void PlaySfxRoutesAndAssignsTheCatalogClip()
        {
            Assert.IsTrue(_audio.PlaySfx("sfx.tool.pickup"));
            var sfx = _audio.GetBusPlayer("sfx");
            Assert.IsNotNull(sfx.clip);
            Assert.AreEqual(1L, _audio.SfxRouter.GetRoutedCount("sfx.tool.pickup"));
        }

        [Test]
        public void BusVolumeAndMuteDriveSourceVolume()
        {
            _audio.PlaySfx("sfx.tool.pickup");
            var sfx = _audio.GetBusPlayer("sfx");
            float before = sfx.volume;
            Assert.IsTrue(_audio.SetBusVolume("sfx", _audio.GetBusVolume("sfx") - 20.0));
            Assert.Less(sfx.volume, before);
            Assert.IsTrue(_audio.SetBusMuted("master", true));
            Assert.AreEqual(0f, sfx.volume);
        }

        [Test]
        public void SpatialSfxUsesAPooledThreeDSource()
        {
            Assert.IsTrue(_audio.PlaySfx("sfx.door.open", new Vector3(3f, 0f, 2f)));
            Assert.AreEqual(1, _audio.GetSpatialPlayerCount());
            _audio.PlaySfx("sfx.door.open", new Vector3(5f, 0f, 2f));
            Assert.AreEqual(1, _audio.GetSpatialPlayerCount(), "the event's pool entry is reused");
        }

        [Test]
        public void SummaryKeepsGodotKeysAndRoundTrips()
        {
            var summary = _audio.GetSummary();
            foreach (string key in new[] { "bus_config", "ambient", "sfx_router", "music", "spatial", "meta_event", "current_voice_log_id", "listener_attached" })
                Assert.IsTrue(summary.Has(key), key);
            var other = new GameObject("Audio2").AddComponent<AudioManager>();
            try
            {
                other.Initialize();
                other.ApplySummary(summary);
                var diffs = TreeDiff.Compare(summary.GetDict("bus_config"), other.GetSummary().GetDict("bus_config"));
                Assert.IsEmpty(diffs, TreeDiff.Format(diffs));
            }
            finally
            {
                Object.DestroyImmediate(other.gameObject);
            }
        }
    }
}
