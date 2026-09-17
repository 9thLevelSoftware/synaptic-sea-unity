using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEngine;

namespace SynapticSea.UI
{
    /// <summary>
    /// Icon lookup for presenters: Godot icon paths (<c>res://assets/ui/{status,achievements}/*.png</c>, item icons) through
    /// <see cref="IconCatalog"/>. Item art exists in neither engine, so items resolve to their category placeholder.
    /// Status effects map ids through <c>data/ui/status_effect_icons.json</c>; an effect without an entry has no icon.
    /// </summary>
    public static class UiIcons
    {
        public const string StatusEffectIconsPath = "res://data/ui/status_effect_icons.json";

        /// <summary>Resolution seam (tests replace it); defaults to <see cref="IconCatalog.Resolve"/>.</summary>
        public static Func<string, string, Texture2D> Resolver = (path, hint) => IconCatalog.Resolve(path, hint);

        public static Texture2D Resolve(string resPath, string categoryHint = null)
        {
            if (string.IsNullOrEmpty(resPath) && string.IsNullOrEmpty(categoryHint)) return null;
            try
            {
                return Resolver?.Invoke(resPath, categoryHint);
            }
            catch (Exception e)
            {
                Debug.LogWarning("UiIcons: icon lookup failed for '" + resPath + "': " + e.Message);
                return null;
            }
        }

        /// <summary>An item row icon: the item's own icon path when the catalog has it, otherwise its category placeholder.</summary>
        public static Texture2D ForItem(string iconPath, string category) => Resolve(iconPath ?? "", string.IsNullOrEmpty(category) ? null : category);

        /// <summary>The status-effect icon for <paramref name="effectId"/> (null when the effect has no icon entry).</summary>
        public static Texture2D ForStatusEffect(string effectId)
        {
            if (string.IsNullOrEmpty(effectId)) return null;
            GdDict table = CatalogRegistry.Exists(StatusEffectIconsPath) ? CatalogRegistry.LoadDict(StatusEffectIconsPath, copy: false) : null;
            string path = table != null ? V.Str(table.Get(effectId, "")) : "";
            return path.Length == 0 ? null : Resolve(path);
        }
    }
}
