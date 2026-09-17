using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.Unity
{
    /// <summary>D6: Godot icon paths resolve to imported textures, and item icons to per-category placeholders.</summary>
    public class IconCatalogTests
    {
        IconCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            // A fresh instance per test so the log-once set starts empty.
            var asset = AssetDatabase.LoadAssetAtPath<IconCatalog>("Assets/Resources/Catalogs/IconCatalog.asset");
            Assert.IsNotNull(asset, "run IconCatalogBuilder");
            _catalog = Object.Instantiate(asset);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_catalog);

        [Test]
        public void StatusAndAchievementIconsResolveExactly()
        {
            foreach (string folder in new[] { "status", "achievements" })
            {
                string[] files = Directory.GetFiles("Assets/Content/UI/Icons/" + folder, "*.png");
                Assert.That(files.Length, Is.GreaterThan(0), folder);
                foreach (string file in files)
                {
                    string res = $"res://assets/ui/{folder}/{Path.GetFileName(file)}";
                    Assert.IsTrue(_catalog.HasExact(res), res);
                    Assert.AreSame(AssetDatabase.LoadAssetAtPath<Texture2D>(file.Replace('\\', '/')), _catalog.Lookup(res));
                }
            }
        }

        [Test]
        public void ItemIconsFallBackToCategoryPlaceholdersAndLogOnce()
        {
            const string path = "res://assets/placeholder/bandage_kit.png";
            LogAssert.Expect(LogType.Log, new Regex(Regex.Escape(path)));
            Texture2D medicine = _catalog.Lookup(path, "medicine");
            Assert.IsNotNull(medicine);
            Assert.AreSame(_catalog.GetCategoryPlaceholder("medicine"), medicine);
            Assert.AreSame(medicine, _catalog.Lookup(path, "medicine"));
            Assert.AreEqual(1, _catalog.UnresolvedPaths.Count);

            LogAssert.ignoreFailingMessages = true;
            Assert.AreSame(_catalog.GetCategoryPlaceholder("raw"), _catalog.Lookup("res://assets/icons/materials/scrap_metal.png"));
            Assert.AreSame(_catalog.GetCategoryPlaceholder("unique"), _catalog.Lookup("res://assets/icons/loot/captains_black_box.png", "no_such_category"));
            Assert.AreSame(_catalog.GetCategoryPlaceholder(IconCatalog.GenericCategory), _catalog.Lookup("res://elsewhere/x.png"));
            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void EveryDataItemIconResolvesToATexture()
        {
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            try
            {
                int checkedIcons = 0;
                foreach (string file in new[]
                         {
                             "res://data/items/item_definitions.json", "res://data/items/medicine_definitions.json",
                             "res://data/items/stimulant_definitions.json", "res://data/items/trade_item_definitions.json",
                             "res://data/items/unique_items.json", "res://data/items/utility_item_definitions.json",
                             "res://data/materials/material_definitions.json",
                         })
                {
                    GdDict root = CatalogRegistry.LoadDict(file);
                    Assert.IsNotNull(root, file);
                    checkedIcons += CheckIcons(root);
                }
                Assert.That(checkedIcons, Is.GreaterThanOrEqualTo(40));
            }
            finally
            {
                CatalogRegistry.Clear();
            }
        }

        int CheckIcons(object node)
        {
            int count = 0;
            if (node is GdDict dict)
            {
                if (dict.Get("icon", null) is string icon && icon.Length > 0)
                {
                    string category = V.Str(dict.Get("category", ""));
                    Assert.IsNotNull(_catalog.Lookup(icon, category), icon);
                    if (category.Length > 0 && _catalog.HasCategory(category))
                        Assert.AreSame(_catalog.GetCategoryPlaceholder(category), _catalog.Lookup(icon, category), icon);
                    count++;
                }
                foreach (object value in dict.Values) count += CheckIcons(value);
            }
            else if (node is GdArray array)
            {
                foreach (object value in array) count += CheckIcons(value);
            }
            return count;
        }

        [Test]
        public void EveryDataCategoryHasAPlaceholder()
        {
            foreach (string category in new[] { "chart", "consumable", "equipment", "fluid", "food", "junk", "medicine", "part", "raw", "stimulant", "supply", "tool", "trade", "utility", "unique", IconCatalog.GenericCategory })
                Assert.IsTrue(_catalog.HasCategory(category), category);
        }
    }
}
