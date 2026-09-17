using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>F1 clip mapping, D5 music stems and the ambient beds.</summary>
    public class AudioContentTests
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

        /// <summary>Every id the game can emit: routed SFX/UI/meta events, ambient role tracks, music layers.</summary>
        static IEnumerable<string> EmittableIds()
        {
            foreach (object key in SfxEventRouter.EVENT_CATALOG.Keys) yield return V.Str(key);
            foreach (object track in AmbientZoneState.ROLE_TRACK_IDS.Values) yield return V.Str(track);
            foreach (string layer in AudioManager.MusicStemLayers) yield return layer;
        }

        [Test]
        public void EveryMappedClipLoads()
        {
            Assert.IsNotNull(_audio.Catalog, "run AudioCatalogBuilder");
            foreach (var kv in AudioManager.StreamCatalog)
            {
                Assert.IsTrue(_audio.Catalog.TryGetClip(kv.Value, out AudioClip clip), kv.Key + " → " + kv.Value);
                Assert.That(clip.length, Is.GreaterThan(0f), kv.Key);
                Assert.IsTrue(clip.LoadAudioData() || clip.loadState == AudioDataLoadState.Loaded || clip.loadType == AudioClipLoadType.Streaming, kv.Key);
            }
        }

        [Test]
        public void GodotSliceClipsAreCataloguedWithImportPresets()
        {
            string[] files = System.IO.Directory.GetFiles("Assets/Content/Audio/Clips", "*.wav");
            Assert.AreEqual(21, files.Length);
            foreach (string file in files)
            {
                string name = System.IO.Path.GetFileName(file);
                Assert.IsTrue(_audio.Catalog.TryGetClip("res://assets/audio/" + name, out _), name);
                var importer = (AudioImporter)AssetImporter.GetAtPath(file.Replace('\\', '/'));
                bool bed = name.StartsWith("ambient_") || name == "reactor_hum.wav";
                Assert.AreEqual(bed ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad, importer.defaultSampleSettings.loadType, name);
                Assert.IsTrue(importer.forceToMono, name);
            }
        }

        [Test]
        public void ReportsEventsStillWithoutClips()
        {
            List<string> silent = EmittableIds().Distinct().Where(id => !AudioManager.StreamCatalog.ContainsKey(id)).OrderBy(id => id).ToList();
            string report = $"[AudioContent] {silent.Count} emittable audio ids still have no clip: {string.Join(", ", silent)}";
            Debug.Log(report);
            TestContext.WriteLine(report);
            // Informational: the list is expected to shrink as audio is authored, never to gain mapped ids back.
            Assert.IsFalse(silent.Contains(AudioEventSeam.SFX_WORK_WELD));
        }

        [Test]
        public void MusicStemsStartTogetherAndFollowLayerGains()
        {
            var music = new DynamicMusicState();
            music.Configure(new GdDict());
            _audio.BindSessionModels(music, null);
            _audio.SetMusicVolumeDb(0.0);

            Assert.IsTrue(_audio.MusicStemsStarted);
            foreach (string layer in new[] { AudioEventSeam.MUSIC_LAYER_BASE, AudioEventSeam.MUSIC_LAYER_TENSION_DRONE, AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD })
            {
                AudioSource stem = _audio.GetMusicStem(layer);
                Assert.IsNotNull(stem.clip, layer);
                Assert.IsTrue(stem.loop, layer);
            }
            Assert.IsNull(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_COMBAT_PERCUSSION).clip, "no percussion stream");
            Assert.AreEqual(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_BASE).clip.samples, _audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD).clip.samples,
                "stems are sample-aligned loops");

            // Exploration: base full, the others silent.
            Assert.AreEqual(0.0, _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_BASE)), 1e-9);
            Assert.AreEqual(AudioManager.SilentDb, _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_TENSION_DRONE)), 1e-9);

            // Tension crossfades per stem: halfway through, the drone is rising and the base is falling.
            music.SetFlags(false, true, false);
            music.Tick(music.CrossfadeSeconds * 0.5);
            _audio.SetMusicVolumeDb(0.0);
            double drone = _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_TENSION_DRONE));
            double baseDb = _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_BASE));
            Assert.That(drone, Is.GreaterThan(AudioManager.SilentDb).And.LessThan(AudioManager.StemDbForGain(0.7)));
            Assert.That(baseDb, Is.LessThan(0.0).And.GreaterThan(AudioManager.StemDbForGain(0.6)));

            music.Tick(music.CrossfadeSeconds);
            _audio.SetMusicVolumeDb(0.0);
            Assert.AreEqual(AudioManager.StemDbForGain(0.7), _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_TENSION_DRONE)), 1e-6);
            Assert.AreEqual(AudioManager.StemDbForGain(0.6), _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_BASE)), 1e-6);
            Assert.AreEqual(AudioManager.SilentDb, _audio.GetSourceDb(_audio.GetMusicStem(AudioEventSeam.MUSIC_LAYER_CRITICAL_PAD)), 1e-9);
        }

        [Test]
        public void AmbientBedsFollowTheRoleCrossfade()
        {
            var ambient = new AmbientZoneState();
            ambient.Configure(new GdDict());
            _audio.BindSessionModels(new DynamicMusicState(), ambient);
            _audio.SetMusicVolumeDb(0.0);
            _audio.Catalog.TryGetClip(AudioManager.StreamCatalog[AudioEventSeam.AMB_DOCKING], out AudioClip docking);
            _audio.Catalog.TryGetClip(AudioManager.StreamCatalog[AudioEventSeam.AMB_ENGINE], out AudioClip engine);
            Assert.AreSame(docking, _audio.GetAmbientBed(0).clip);

            ambient.SetRoomRole(AudioEventSeam.ROOM_ROLE_ENGINE);
            ambient.Tick(ambient.CrossfadeSeconds * 0.5);
            _audio.SetMusicVolumeDb(0.0);
            Assert.AreSame(engine, _audio.GetAmbientBed(0).clip);
            Assert.AreSame(docking, _audio.GetAmbientBed(1).clip, "the docking bed fades out on the other source");

            ambient.Tick(ambient.CrossfadeSeconds);
            _audio.SetMusicVolumeDb(0.0);
            Assert.AreSame(engine, _audio.GetAmbientBed(0).clip);
            Assert.IsNull(_audio.GetAmbientBed(1).clip);
        }
    }
}
