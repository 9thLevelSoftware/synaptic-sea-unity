using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Builds Assets/Resources/Catalogs/AudioCatalog.asset: every clip under Assets/Content/Audio/{SFX,Music,UI,Voice}
    /// registered under its Godot path (res://data/audio/{sfx,music,ui,voice}/&lt;file&gt;), plus Godot's unreferenced
    /// slice clips in Assets/Content/Audio/Clips (res://assets/audio/&lt;file&gt;). Applies import settings: SFX
    /// decompress-on-load mono PCM; music stems decompress-on-load (sample-aligned PlayScheduled loops); ambient beds
    /// (<c>ambient_*</c>, <c>reactor_hum</c>) streaming Vorbis mono. Reports catalog ids with no clip.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.AudioCatalogBuilder.Build -quit
    /// </summary>
    public static class AudioCatalogBuilder
    {
        /// <summary>Unity folder under Assets/Content/Audio → Godot resource directory.</summary>
        static readonly Dictionary<string, string> Folders = new Dictionary<string, string>
        {
            { "SFX", "res://data/audio/sfx" }, { "Music", "res://data/audio/music" }, { "UI", "res://data/audio/ui" },
            { "Voice", "res://data/audio/voice" }, { "Clips", "res://assets/audio" },
        };

        enum Preset { Sfx, MusicStem, AmbientBed }

        [MenuItem("Synaptic Sea/Content/Build Audio Catalog")]
        public static void Build()
        {
            var entries = new List<AudioCatalog.Entry>();
            foreach (var kv in Folders)
            {
                string dir = "Assets/Content/Audio/" + kv.Key;
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir).Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)))
                {
                    string assetPath = file.Replace('\\', '/');
                    ConfigureImporter(assetPath, PresetFor(kv.Key, Path.GetFileNameWithoutExtension(assetPath)));
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                    if (clip == null) continue;
                    entries.Add(new AudioCatalog.Entry { resPath = $"{kv.Value}/{Path.GetFileName(assetPath)}", clip = clip });
                }
            }

            const string path = "Assets/Resources/Catalogs/AudioCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<AudioCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<AudioCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }
            catalog.entries = entries.OrderBy(e => e.resPath, StringComparer.Ordinal).ToList();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            var missing = AudioManager.StreamCatalog.Where(kv => !catalog.TryGetClip(kv.Value, out _)).Select(kv => kv.Key).ToList();
            foreach (var m in missing) Debug.LogWarning("[AudioCatalogBuilder] no clip for catalog id " + m);
            Debug.Log($"[AudioCatalogBuilder] AUDIO CATALOG {(missing.Count == 0 ? "PASS" : "FAIL")} clips={entries.Count} missing={missing.Count}");
            if (Application.isBatchMode) EditorApplication.Exit(missing.Count == 0 ? 0 : 1);
        }

        static Preset PresetFor(string folder, string fileName)
        {
            if (folder == "Music") return Preset.MusicStem;
            if (fileName.StartsWith("ambient_", StringComparison.Ordinal) || fileName == "reactor_hum") return Preset.AmbientBed;
            return Preset.Sfx;
        }

        static void ConfigureImporter(string assetPath, Preset preset)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is AudioImporter importer)) return;
            var settings = importer.defaultSampleSettings;
            bool mono;
            bool background;
            switch (preset)
            {
                case Preset.MusicStem:
                    // Decompressed so PlayScheduled starts every stem on the same DSP sample.
                    settings.loadType = AudioClipLoadType.DecompressOnLoad;
                    settings.compressionFormat = AudioCompressionFormat.PCM;
                    mono = false;
                    background = false;
                    break;
                case Preset.AmbientBed:
                    settings.loadType = AudioClipLoadType.Streaming;
                    settings.compressionFormat = AudioCompressionFormat.Vorbis;
                    settings.quality = 0.7f;
                    mono = true;
                    background = true;
                    break;
                default:
                    settings.loadType = AudioClipLoadType.DecompressOnLoad;
                    settings.compressionFormat = AudioCompressionFormat.PCM;
                    mono = true;
                    background = false;
                    break;
            }
            if (!importer.defaultSampleSettings.Equals(settings) || importer.forceToMono != mono || importer.loadInBackground != background)
            {
                importer.defaultSampleSettings = settings;
                importer.forceToMono = mono;
                importer.loadInBackground = background;
                importer.SaveAndReimport();
            }
        }
    }
}
