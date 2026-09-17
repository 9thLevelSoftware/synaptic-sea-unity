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
    /// Builds Assets/Resources/Catalogs/IconCatalog.asset:
    /// <list type="bullet">
    /// <item><c>Assets/Content/UI/Icons/{status,achievements}/*.png</c> under <c>res://assets/ui/{status,achievements}/&lt;file&gt;</c>;</item>
    /// <item>one generated placeholder per item category (<c>Assets/Content/UI/Icons/items/placeholder_&lt;category&gt;.png</c>),
    /// because Godot's item icon paths (<c>res://assets/icons/**</c>, <c>res://assets/placeholder/*</c>) have no files.</item>
    /// </list>
    /// Idempotent: placeholders are rewritten only when their pixels change.
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.IconCatalogBuilder.Build -quit
    /// </summary>
    public static class IconCatalogBuilder
    {
        public const string CatalogPath = "Assets/Resources/Catalogs/IconCatalog.asset";
        const string IconRoot = "Assets/Content/UI/Icons";
        const string PlaceholderDir = IconRoot + "/items";
        const int Size = 64;

        static readonly string[] MappedFolders = { "status", "achievements" };

        /// <summary>Item categories from data/items, data/materials (plus unique loot and the generic fallback), with a colour and glyph.</summary>
        static readonly (string category, Color color, Glyph glyph)[] Categories =
        {
            ("chart", new Color(0.31f, 0.55f, 0.80f), Glyph.Grid),
            ("consumable", new Color(0.85f, 0.62f, 0.25f), Glyph.Circle),
            ("equipment", new Color(0.50f, 0.56f, 0.64f), Glyph.Shield),
            ("fluid", new Color(0.25f, 0.70f, 0.85f), Glyph.Drop),
            ("food", new Color(0.55f, 0.75f, 0.30f), Glyph.Circle),
            ("junk", new Color(0.45f, 0.40f, 0.36f), Glyph.Cross),
            ("medicine", new Color(0.88f, 0.30f, 0.32f), Glyph.Plus),
            ("part", new Color(0.62f, 0.64f, 0.70f), Glyph.Square),
            ("raw", new Color(0.60f, 0.48f, 0.36f), Glyph.Diamond),
            ("stimulant", new Color(0.75f, 0.35f, 0.80f), Glyph.Triangle),
            ("supply", new Color(0.40f, 0.62f, 0.52f), Glyph.Bars),
            ("tool", new Color(0.90f, 0.78f, 0.30f), Glyph.Bars),
            ("trade", new Color(0.80f, 0.68f, 0.40f), Glyph.Diamond),
            ("utility", new Color(0.35f, 0.75f, 0.65f), Glyph.Triangle),
            ("unique", new Color(0.95f, 0.55f, 0.15f), Glyph.Star),
            (IconCatalog.GenericCategory, new Color(0.60f, 0.64f, 0.69f), Glyph.Square),
        };

        enum Glyph { Circle, Square, Diamond, Plus, Cross, Triangle, Bars, Grid, Drop, Shield, Star }

        [MenuItem("Synaptic Sea/Content/Build Icon Catalog")]
        public static void Build()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<IconCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<IconCatalog>();
                Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            var entries = new List<IconCatalog.Entry>();
            foreach (string folder in MappedFolders)
            {
                string dir = $"{IconRoot}/{folder}";
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.png").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string assetPath = file.Replace('\\', '/');
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (tex == null) continue;
                    entries.Add(new IconCatalog.Entry { resPath = $"res://assets/ui/{folder}/{Path.GetFileName(assetPath)}", texture = tex });
                }
            }

            Directory.CreateDirectory(PlaceholderDir);
            var written = new List<string>();
            foreach (var (category, color, glyph) in Categories)
            {
                string path = $"{PlaceholderDir}/placeholder_{category}.png";
                byte[] png = RenderPlaceholder(color, glyph).EncodeToPNG();
                if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(png))
                {
                    File.WriteAllBytes(path, png);
                    written.Add(path);
                }
            }
            foreach (string path in written) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            foreach (var (category, _, _) in Categories) ConfigurePlaceholderImporter($"{PlaceholderDir}/placeholder_{category}.png");

            var categories = new List<IconCatalog.CategoryEntry>();
            foreach (var (category, _, _) in Categories)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{PlaceholderDir}/placeholder_{category}.png");
                if (tex != null) categories.Add(new IconCatalog.CategoryEntry { category = category, texture = tex });
            }

            catalog.entries = entries;
            catalog.categories = categories;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            bool ok = entries.Count == 16 && categories.Count == Categories.Length;
            Debug.Log($"[IconCatalogBuilder] ICON CATALOG {(ok ? "PASS" : "FAIL")} icons={entries.Count} placeholders={categories.Count} rewritten={written.Count}");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        static void ConfigurePlaceholderImporter(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;
            bool changed = importer.textureType != TextureImporterType.Default || !importer.alphaIsTransparency || importer.mipmapEnabled;
            if (!changed) return;
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        static Texture2D RenderPlaceholder(Color color, Glyph glyph)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var pixels = new Color32[Size * Size];
            Color border = Color.Lerp(color, Color.white, 0.35f);
            Color fill = new Color(color.r * 0.45f, color.g * 0.45f, color.b * 0.45f, 0.92f);
            const float half = Size / 2f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    // Rounded tile: 2 px margin, 10 px corner radius, 3 px border.
                    float d = RoundedRectDistance(px - half, py - half, half - 2f, 10f);
                    Color c = new Color(0, 0, 0, 0);
                    if (d <= 0f) c = d > -3f ? border : fill;
                    if (d <= -3f && InGlyph(glyph, (px - half) / (half - 12f), (py - half) / (half - 12f))) c = color;
                    pixels[y * Size + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        static float RoundedRectDistance(float x, float y, float halfExtent, float radius)
        {
            float qx = Mathf.Abs(x) - (halfExtent - radius);
            float qy = Mathf.Abs(y) - (halfExtent - radius);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        /// <summary>Glyph coverage in normalised tile space (-1..1, y up).</summary>
        static bool InGlyph(Glyph glyph, float x, float y)
        {
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
            switch (glyph)
            {
                case Glyph.Circle: return x * x + y * y <= 0.55f;
                case Glyph.Square: return ax <= 0.6f && ay <= 0.6f;
                case Glyph.Diamond: return ax + ay <= 0.8f;
                case Glyph.Plus: return (ax <= 0.22f && ay <= 0.75f) || (ay <= 0.22f && ax <= 0.75f);
                case Glyph.Cross: return Mathf.Abs(ax - ay) <= 0.2f && ax <= 0.7f;
                case Glyph.Triangle: return y >= -0.6f && y <= 0.7f && ax <= (0.7f - y) * 0.6f;
                case Glyph.Bars: return ay <= 0.7f && (ax <= 0.14f || (ax >= 0.4f && ax <= 0.68f));
                case Glyph.Grid: return ax <= 0.7f && ay <= 0.7f && (Mathf.Repeat(x + 0.7f, 0.47f) <= 0.12f || Mathf.Repeat(y + 0.7f, 0.47f) <= 0.12f);
                case Glyph.Drop: return (x * x + (y + 0.2f) * (y + 0.2f) <= 0.3f) || (y >= -0.2f && y <= 0.75f && ax <= (0.75f - y) * 0.58f);
                case Glyph.Shield: return y <= 0.65f && y >= -0.75f && ax <= (y >= 0f ? 0.6f : 0.6f * (1f + y / 0.75f));
                case Glyph.Star:
                {
                    float angle = Mathf.Atan2(y, x);
                    float r = Mathf.Sqrt(x * x + y * y);
                    float spike = 0.45f + 0.3f * Mathf.Cos(5f * (angle - Mathf.PI / 2f));
                    return r <= spike;
                }
                default: return false;
            }
        }
    }
}
