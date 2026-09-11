// Ported from scripts/systems/unique_item_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public sealed class UniqueItemState : ISimModel, IStatusLineProvider
    {
        public GdDict ClaimedUniqueIds = new GdDict();
        public GdDict ClaimedSeedKeys = new GdDict();
        public GdDict UnlockedCodexEntryIds = new GdDict();

        public void Configure(GdDict data = null)
        {
            ClaimedUniqueIds.Clear();
            ClaimedSeedKeys.Clear();
            UnlockedCodexEntryIds.Clear();
            if (data == null || data.IsEmpty) return;
            ApplySummary(data);
        }

        public bool IsClaimed(string uniqueId) =>
            !string.IsNullOrEmpty(uniqueId) && ClaimedUniqueIds.Has(uniqueId);

        public bool IsSeedClaimed(string seedKey) =>
            !string.IsNullOrEmpty(seedKey) && ClaimedSeedKeys.Has(seedKey);

        public bool CanClaim(string uniqueId, string seedKey = "")
        {
            if (string.IsNullOrEmpty(uniqueId)) return false;
            if (IsClaimed(uniqueId)) return false;
            if (!string.IsNullOrEmpty(seedKey) && IsSeedClaimed(seedKey)) return false;
            return true;
        }

        public bool Claim(string uniqueId, string seedKey = "", string codexEntryId = "")
        {
            if (!CanClaim(uniqueId, seedKey)) return false;
            ClaimedUniqueIds[uniqueId] = true;
            if (!string.IsNullOrEmpty(seedKey)) ClaimedSeedKeys[seedKey] = true;
            if (!string.IsNullOrEmpty(codexEntryId)) UnlockedCodexEntryIds[codexEntryId] = true;
            return true;
        }

        public bool RecordCodexUnlock(string entryId)
        {
            if (string.IsNullOrEmpty(entryId) || UnlockedCodexEntryIds.Has(entryId)) return false;
            UnlockedCodexEntryIds[entryId] = true;
            return true;
        }

        public void Reset()
        {
            ClaimedUniqueIds.Clear();
            ClaimedSeedKeys.Clear();
            UnlockedCodexEntryIds.Clear();
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "claimed_unique_ids", ClaimedUniqueIds.ShallowCopy() },
                { "claimed_seed_keys", ClaimedSeedKeys.ShallowCopy() },
                { "unlocked_codex_entry_ids", UnlockedCodexEntryIds.ShallowCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            ClaimedUniqueIds.Clear();
            ClaimedSeedKeys.Clear();
            UnlockedCodexEntryIds.Clear();
            CopyTrueKeys(summary.Get("claimed_unique_ids", new GdDict()), ClaimedUniqueIds);
            CopyTrueKeys(summary.Get("claimed_seed_keys", new GdDict()), ClaimedSeedKeys);
            CopyTrueKeys(summary.Get("unlocked_codex_entry_ids", new GdDict()), UnlockedCodexEntryIds);
            return true;
        }

        static void CopyTrueKeys(object source, GdDict target)
        {
            if (!(source is GdDict dict)) return;
            foreach (var kv in dict)
                if (V.Bool(kv.Value)) target[V.Str(kv.Key)] = true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            return new List<string>
            {
                "unique_claimed=" + ItemsCompat.D(ClaimedUniqueIds.Count),
                "unique_seed_keys=" + ItemsCompat.D(ClaimedSeedKeys.Count),
                "unique_codex=" + ItemsCompat.D(UnlockedCodexEntryIds.Count),
            };
        }
    }
}
