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
    /// registered under its Godot path (res://data/audio/{sfx,music,ui,voice}/&lt;file&gt;), and applies import
    /// settings (short SFX decompress-on-load mono; music streaming Vorbis). Reports catalog ids with no clip.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.AudioCatalogBuilder.Build -quit
    /// </summary>
    public static class AudioCatalogBuilder
    {
        static readonly Dictionary<string, string> Folders = new Dictionary<string, string>
        {
            { "SFX", "sfx" }, { "Music", "music" }, { "UI", "ui" }, { "Voice", "voice" },
        };

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
                    ConfigureImporter(assetPath, kv.Key == "Music");
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                    if (clip == null) continue;
                    entries.Add(new AudioCatalog.Entry { resPath = $"res://data/audio/{kv.Value}/{Path.GetFileName(assetPath)}", clip = clip });
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

        static void ConfigureImporter(string assetPath, bool music)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is AudioImporter importer)) return;
            var settings = importer.defaultSampleSettings;
            if (music)
            {
                settings.loadType = AudioClipLoadType.Streaming;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.7f;
                importer.forceToMono = false;
                importer.loadInBackground = true;
            }
            else
            {
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.PCM;
            }
            if (!importer.defaultSampleSettings.Equals(settings))
            {
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
            }
        }
    }
}
