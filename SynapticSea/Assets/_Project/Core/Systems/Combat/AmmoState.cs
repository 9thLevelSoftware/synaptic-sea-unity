// Ported from scripts/systems/ammo_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Per-weapon magazine + timed reload. Combat fires from the magazine; inventory holds the reserve stock.
    /// Reload moves reserve -> magazine over <see cref="RELOAD_SECONDS"/>.
    /// </summary>
    public sealed class AmmoState : ISimModel, IStatusLineProvider
    {
        public const double RELOAD_SECONDS = 1.5;

        /// <summary>weapon_id (String) -> loaded rounds (int).</summary>
        public GdDict Magazines = new GdDict();
        public bool ReloadActive = false;
        public double ReloadRemaining = 0.0;
        public string ReloadWeaponId = "";
        /// <summary>Rounds committed to load on completion.</summary>
        public long ReloadTarget = 0;
        public long TotalFired = 0;

        public void Configure(GdDict config = null)
        {
            config = config ?? new GdDict();
            Magazines.Clear();
            ReloadActive = false;
            ReloadRemaining = 0.0;
            ReloadWeaponId = "";
            ReloadTarget = 0;
            TotalFired = 0;
            object raw = config.Get("magazines", new GdDict());
            if (raw is GdDict mags)
            {
                foreach (var kv in mags)
                    Magazines[V.Str(kv.Key)] = Math.Max(0L, V.I64(kv.Value));
            }
        }

        public long Loaded(string weaponId) => V.I64(Magazines.Get(weaponId, 0L));

        public bool Spend(string weaponId)
        {
            long cur = Loaded(weaponId);
            if (cur <= 0) return false;
            Magazines[weaponId] = cur - 1;
            TotalFired += 1;
            return true;
        }

        public bool IsReloading() => ReloadActive;

        /// <summary>
        /// Begins a reload if not already reloading and there is room + reserve. The coordinator removes
        /// <see cref="ReloadTarget"/> rounds from inventory once this returns true.
        /// </summary>
        public bool BeginReload(string weaponId, long magazineSize, long reserveAvailable)
        {
            if (ReloadActive) return false;
            if (weaponId.Length == 0 || magazineSize <= 0) return false;
            long need = magazineSize - Loaded(weaponId);
            long canLoad = Math.Min(need, Math.Max(0L, reserveAvailable));
            if (canLoad <= 0) return false;
            ReloadActive = true;
            ReloadRemaining = RELOAD_SECONDS;
            ReloadWeaponId = weaponId;
            ReloadTarget = canLoad;
            return true;
        }

        /// <summary>
        /// Advances the reload timer. On completion credits the magazine and returns { weapon_id, loaded };
        /// returns {} while idle or mid-reload.
        /// </summary>
        public GdDict Tick(double delta)
        {
            if (!ReloadActive) return new GdDict();
            ReloadRemaining -= delta;
            if (ReloadRemaining > 0.0) return new GdDict();
            string wid = ReloadWeaponId;
            long loadedCount = ReloadTarget;
            Magazines[wid] = Loaded(wid) + loadedCount;
            ReloadActive = false;
            ReloadRemaining = 0.0;
            ReloadWeaponId = "";
            ReloadTarget = 0;
            return new GdDict { { "weapon_id", wid }, { "loaded", loadedCount } };
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "magazines", Magazines.DeepCopy() },
                { "reload_active", ReloadActive },
                { "reload_remaining", ReloadRemaining },
                { "reload_weapon_id", ReloadWeaponId },
                { "reload_target", ReloadTarget },
                { "total_fired", TotalFired },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            Configure(new GdDict { { "magazines", summary.Get("magazines", new GdDict()) } });
            ReloadActive = V.Bool(summary.Get("reload_active", false));
            ReloadRemaining = V.F64(summary.Get("reload_remaining", 0.0));
            ReloadWeaponId = V.Str(summary.Get("reload_weapon_id", ""));
            ReloadTarget = V.I64(summary.Get("reload_target", 0L));
            TotalFired = V.I64(summary.Get("total_fired", 0L));
            return true;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            var wids = new List<object>(Magazines.Keys);
            GdSort.Sort(wids);
            foreach (object wid in wids)
                lines.Add("Mag " + V.Str(wid) + "=" + SurvivalCompat.FormatD(V.I64(Magazines[wid])));
            if (ReloadActive)
                lines.Add("Reloading " + ReloadWeaponId + " (" + SurvivalCompat.FormatF(ReloadRemaining, 1) + "s)");
            return lines;
        }
    }
}
