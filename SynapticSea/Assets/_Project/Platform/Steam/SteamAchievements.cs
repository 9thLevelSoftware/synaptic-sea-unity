using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Platform.Steam
{
    /// <summary>
    /// Mirrors <see cref="AchievementState.Unlocked"/> onto Steam. Steam achievements use the
    /// <c>data/release/achievement_catalog.json</c> ids verbatim (<see cref="PlatformAchievementIds"/>), one Steam
    /// achievement per catalog entry; configure the same API names in Steamworks.
    /// </summary>
    public static class SteamAchievements
    {
        static AchievementState _bound;

        /// <summary>Follows one session's achievement model (a new session replaces the previous binding).</summary>
        public static void Bind(RunSession session)
        {
            AchievementState state = session?.AchievementState;
            if (ReferenceEquals(state, _bound)) return;
            Unbind();
            if (state == null) return;
            _bound = state;
            _bound.Unlocked += OnUnlocked;
            // Unlocks restored before the binding are replayed; setting an achievement twice is harmless on Steam.
            foreach (object id in state.GetUnlocked()) OnUnlocked(V.Str(id));
        }

        public static void Unbind()
        {
            if (_bound != null) _bound.Unlocked -= OnUnlocked;
            _bound = null;
        }

        static void OnUnlocked(string catalogId)
        {
            string apiName = PlatformAchievementIds.SteamApiNameFor(catalogId);
            if (apiName.Length == 0)
            {
                Debug.LogWarning("[SteamAchievements] '" + catalogId + "' is not a valid Steam API name; not sent");
                return;
            }
            if (SteamPlatform.Instance == null || !SteamPlatform.Instance.Initialized) return;
            new Steamworks.Data.Achievement(apiName).Trigger();
        }
    }
}
