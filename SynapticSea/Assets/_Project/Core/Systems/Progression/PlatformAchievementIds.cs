using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Unity port (plan Phase 12, Steam stretch): maps <c>data/release/achievement_catalog.json</c> ids onto store
    /// achievement API names. Steam uses the catalog ids verbatim (1:1), so a catalog id must already be a valid Steam
    /// API name. Engine-free so the rule is tested without the Steam SDK; <c>Platform/Steam</c> consumes it.
    /// </summary>
    public static class PlatformAchievementIds
    {
        /// <summary>Steamworks limits achievement API names to 128 characters; the port also restricts the alphabet.</summary>
        public const int SteamApiNameMaxLength = 128;

        /// <summary>True for a lowercase ASCII letter, digit or underscore name of 1..128 characters.</summary>
        public static bool IsValidSteamApiName(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > SteamApiNameMaxLength) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>The Steam achievement API name for a catalog id (identity), or "" when the id is not a valid name.</summary>
        public static string SteamApiNameFor(string catalogId) => IsValidSteamApiName(catalogId) ? catalogId : "";

        /// <summary>Catalog ids (in catalog order) that cannot be used as Steam API names.</summary>
        public static List<string> InvalidSteamIds(GdArray catalogIds)
        {
            var invalid = new List<string>();
            if (catalogIds == null) return invalid;
            foreach (object id in catalogIds)
            {
                string s = V.Str(id);
                if (!IsValidSteamApiName(s)) invalid.Add(s);
            }
            return invalid;
        }
    }
}
