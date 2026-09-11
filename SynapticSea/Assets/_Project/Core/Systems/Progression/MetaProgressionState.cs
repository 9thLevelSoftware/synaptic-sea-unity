// Ported from scripts/systems/meta_progression_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-PM-006 / REQ-PM-008 / ADR-0033 meta-progression model. Cross-run state persisted to
    /// <c>user://meta_progression.json</c> (through an injected <see cref="IStorage"/>) independent of RunSnapshot.
    /// Owns meta-currency, hub upgrade / class / codex unlock sets, and run counters.
    /// </summary>
    public class MetaProgressionState : ISimModel, IDiskPersisted, IHubUpgradeWallet, IStatusLineProvider
    {
        public const string SchemaVersion = "meta-progression-1";
        public const string SavePath = "user://meta_progression.json";

        public long MetaCurrency = 0;
        public GdDict UnlockedClassIds = new GdDict();          // class_id -> true
        public GdDict UnlockedHubUpgradeIds = new GdDict();     // upgrade_id -> true
        public GdDict UnlockedCodexEntryIds = new GdDict();     // unlock_id -> true
        public long TotalRunsCompleted = 0;
        public long TotalRunsDeaths = 0;
        public long HighestSkillLevelSeen = 0;
        public long LastPayoutCurrency = 0;
        public string LastPayoutReason = "";
        public string SelectedClassId = "";                     // Domain 6: player's chosen class for the next run
        readonly GdDict _catalogKnownIds = new GdDict();        // optional whitelist (set by Configure)
        bool _schemaWarned = false;

        IStorage _storage;
        IClock _clock;

        public MetaProgressionState(IStorage storage = null, IClock clock = null)
        {
            _storage = storage;
            _clock = clock;
        }

        /// <summary><c>user://</c> backing store; defaults to <see cref="CoreServices.UserStorage"/>.</summary>
        public IStorage Storage
        {
            get => _storage ?? CoreServices.UserStorage;
            set => _storage = value;
        }

        public IClock Clock
        {
            get => _clock ?? CoreServices.Clock;
            set => _clock = value;
        }

        public void Configure(GdDict catalog = null)
        {
            _catalogKnownIds.Clear();
            if (catalog == null) catalog = new GdDict();
            object variant = catalog.Get("unlocks", new GdArray());
            if (variant is GdArray unlocks)
            {
                foreach (var entry in unlocks)
                {
                    if (!(entry is GdDict entryDict)) continue;
                    string uid = V.Str(entryDict.Get("unlock_id", ""));
                    if (uid.Length == 0) continue;
                    _catalogKnownIds[uid] = true;
                }
            }
        }

        public bool IsKnown(string unlockId)
        {
            if (_catalogKnownIds.IsEmpty) return true;
            return _catalogKnownIds.Has(unlockId);
        }

        public long GetMetaCurrency() => MetaCurrency;

        public string GetSelectedClass() => SelectedClassId;

        public void SetSelectedClass(string classId) => SelectedClassId = classId ?? "";

        public bool AddMetaCurrency(long amount)
        {
            if (amount <= 0) return false;
            MetaCurrency += amount;
            return true;
        }

        public bool SpendMetaCurrency(long amount)
        {
            if (amount <= 0) return false;
            if (MetaCurrency < amount) return false;
            MetaCurrency -= amount;
            return true;
        }

        public bool UnlockClass(string classId)
        {
            if (string.IsNullOrEmpty(classId)) return false;
            if (UnlockedClassIds.Has(classId)) return false;
            UnlockedClassIds[classId] = true;
            return true;
        }

        public bool UnlockHubUpgrade(string upgradeId)
        {
            if (string.IsNullOrEmpty(upgradeId)) return false;
            if (UnlockedHubUpgradeIds.Has(upgradeId)) return false;
            UnlockedHubUpgradeIds[upgradeId] = true;
            return true;
        }

        public bool UnlockCodexEntry(string entryId)
        {
            if (string.IsNullOrEmpty(entryId)) return false;
            if (!IsKnown(entryId)) return false;
            if (UnlockedCodexEntryIds.Has(entryId)) return false;
            UnlockedCodexEntryIds[entryId] = true;
            return true;
        }

        public bool IsClassUnlocked(string classId) => UnlockedClassIds.Has(classId) && V.Bool(UnlockedClassIds[classId]);

        public bool IsHubUpgradeUnlocked(string upgradeId) => UnlockedHubUpgradeIds.Has(upgradeId) && V.Bool(UnlockedHubUpgradeIds[upgradeId]);

        public bool IsCodexEntryUnlocked(string entryId) => UnlockedCodexEntryIds.Has(entryId) && V.Bool(UnlockedCodexEntryIds[entryId]);

        public GdArray GetUnlockedClassIds() => InfraCompat.SortedKeys(UnlockedClassIds);

        public GdArray GetUnlockedHubUpgradeIds() => InfraCompat.SortedKeys(UnlockedHubUpgradeIds);

        public GdArray GetUnlockedCodexEntryIds() => InfraCompat.SortedKeys(UnlockedCodexEntryIds);

        public int GetUnlockCount() => UnlockedClassIds.Count + UnlockedHubUpgradeIds.Count + UnlockedCodexEntryIds.Count;

        /// <summary>
        /// Applies the meta payout for a finished run: +10 per completed objective, +5 per skill &gt;= 5,
        /// +15 per skill &gt;= 8, +2 per discovery. Returns the total amount added.
        /// </summary>
        public long ApplyMetaPayout(GdDict runSummary)
        {
            long payout = 0;
            if (runSummary == null) runSummary = new GdDict();
            long objectives = V.I64(runSummary.Get("completed_objectives", 0L));
            payout += objectives * 10;
            object skillLevelsVariant = runSummary.Get("skill_levels", new GdDict());
            if (skillLevelsVariant is GdDict skillLevels)
            {
                foreach (var sid in skillLevels.Keys)
                {
                    long lvl = V.I64(skillLevels[sid]);
                    if (lvl >= 8)
                        payout += 15;
                    else if (lvl >= 5)
                        payout += 5;
                    if (lvl > HighestSkillLevelSeen) HighestSkillLevelSeen = lvl;
                }
            }
            long discoveries = V.I64(runSummary.Get("discoveries", 0L));
            payout += discoveries * 2;
            string reason = V.Str(runSummary.Get("reason", "completion"));
            LastPayoutCurrency = payout;
            LastPayoutReason = reason;
            if (reason == "death")
                TotalRunsDeaths += 1;
            else
                TotalRunsCompleted += 1;
            MetaCurrency += payout;
            return payout;
        }

        /// <summary>Resets the run-scoped payout fields; does NOT wipe currency, unlocks, or run totals.</summary>
        public void StartNewRun()
        {
            LastPayoutCurrency = 0;
            LastPayoutReason = "";
        }

        public void ResetAll()
        {
            MetaCurrency = 0;
            UnlockedClassIds.Clear();
            UnlockedHubUpgradeIds.Clear();
            UnlockedCodexEntryIds.Clear();
            TotalRunsCompleted = 0;
            TotalRunsDeaths = 0;
            HighestSkillLevelSeen = 0;
            LastPayoutCurrency = 0;
            LastPayoutReason = "";
            SelectedClassId = "";
        }

        /// <summary>Serializes the meta state (save/load agnostic).</summary>
        public GdDict ToDict()
        {
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "meta_currency", MetaCurrency },
                { "unlocked_class_ids", UnlockedClassIds.ShallowCopy() },
                { "unlocked_hub_upgrade_ids", UnlockedHubUpgradeIds.ShallowCopy() },
                { "unlocked_codex_entry_ids", UnlockedCodexEntryIds.ShallowCopy() },
                { "total_runs_completed", TotalRunsCompleted },
                { "total_runs_deaths", TotalRunsDeaths },
                { "highest_skill_level_seen", HighestSkillLevelSeen },
                { "last_payout_currency", LastPayoutCurrency },
                { "last_payout_reason", LastPayoutReason },
                { "selected_class_id", SelectedClassId },
                { "saved_at", Clock.DateTimeString(true) },
            };
        }

        /// <summary>
        /// Restores from a <see cref="ToDict"/> dict. Null / non-dict / empty input is rejected. A mismatched schema
        /// that still carries known meta fields is best-effort applied with one warning; fields absent from a
        /// partial dict are left untouched.
        /// </summary>
        public bool ApplySummary(object summary)
        {
            if (summary == null || !(summary is GdDict dict)) return false;
            if (dict.IsEmpty) return false;
            string schema = V.Str(dict.Get("schema", ""));
            if (schema != SchemaVersion)
            {
                if (!HasKnownMetaField(dict))
                {
                    WarnSchemaOnce(schema, "rejected (no known meta fields)");
                    return false;
                }
                WarnSchemaOnce(schema, "best-effort apply of known fields");
            }
            if (dict.Has("meta_currency")) MetaCurrency = Math.Max(0L, V.I64(dict.Get("meta_currency", 0L)));
            if (dict.Has("unlocked_class_ids") && dict.Get("unlocked_class_ids") is GdDict clsV)
            {
                UnlockedClassIds.Clear();
                foreach (var k in clsV.Keys) UnlockedClassIds[V.Str(k)] = V.Bool(clsV[k]);
            }
            if (dict.Has("unlocked_hub_upgrade_ids") && dict.Get("unlocked_hub_upgrade_ids") is GdDict hubV)
            {
                UnlockedHubUpgradeIds.Clear();
                foreach (var k in hubV.Keys) UnlockedHubUpgradeIds[V.Str(k)] = V.Bool(hubV[k]);
            }
            if (dict.Has("unlocked_codex_entry_ids") && dict.Get("unlocked_codex_entry_ids") is GdDict codexV)
            {
                UnlockedCodexEntryIds.Clear();
                foreach (var k in codexV.Keys) UnlockedCodexEntryIds[V.Str(k)] = V.Bool(codexV[k]);
            }
            if (dict.Has("total_runs_completed")) TotalRunsCompleted = Math.Max(0L, V.I64(dict.Get("total_runs_completed", 0L)));
            if (dict.Has("total_runs_deaths")) TotalRunsDeaths = Math.Max(0L, V.I64(dict.Get("total_runs_deaths", 0L)));
            if (dict.Has("highest_skill_level_seen")) HighestSkillLevelSeen = Math.Max(0L, V.I64(dict.Get("highest_skill_level_seen", 0L)));
            if (dict.Has("last_payout_currency")) LastPayoutCurrency = Math.Max(0L, V.I64(dict.Get("last_payout_currency", 0L)));
            if (dict.Has("last_payout_reason")) LastPayoutReason = V.Str(dict.Get("last_payout_reason", ""));
            if (dict.Has("selected_class_id")) SelectedClassId = V.Str(dict.Get("selected_class_id", ""));
            return true;
        }

        bool ISimModel.ApplySummary(GdDict summary) => ApplySummary(summary);

        static readonly string[] KnownMetaFields =
        {
            "meta_currency",
            "unlocked_class_ids",
            "unlocked_hub_upgrade_ids",
            "unlocked_codex_entry_ids",
            "total_runs_completed",
            "total_runs_deaths",
            "highest_skill_level_seen",
            "last_payout_currency",
            "last_payout_reason",
            "selected_class_id",
        };

        /// <summary>True when the dict carries at least one field this model owns.</summary>
        bool HasKnownMetaField(GdDict dict)
        {
            foreach (string key in KnownMetaFields)
            {
                if (dict.Has(key)) return true;
            }
            return false;
        }

        void WarnSchemaOnce(string foundSchema, string action)
        {
            if (_schemaWarned) return;
            _schemaWarned = true;
            CoreServices.Log.Warning("MetaProgressionState: schema mismatch ('" + foundSchema + "' != '" + SchemaVersion + "'); " + action);
        }

        /// <summary>Persists to <c>user://meta_progression.json</c>. Returns false on IO failure.</summary>
        public bool SaveToDisk(string savePath = SavePath)
        {
            try
            {
                Storage.WriteText(savePath, GdJson.Stringify(ToDict(), "\t"));
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        /// <summary>Loads from <c>user://meta_progression.json</c>; false when missing, unparseable, or rejected.</summary>
        public bool LoadFromDisk(string sourcePath = SavePath)
        {
            if (!Storage.FileExists(sourcePath)) return false;
            string text = Storage.ReadText(sourcePath);
            if (text == null) return false;
            object parsed = GdJson.ParseString(text);
            if (parsed == null || !(parsed is GdDict dict)) return false;
            return ApplySummary(dict);
        }

        bool IDiskPersisted.LoadFromDisk(IStorage storage)
        {
            IStorage previous = _storage;
            _storage = storage;
            try { return LoadFromDisk(); }
            finally { _storage = previous; }
        }

        bool IDiskPersisted.SaveToDisk(IStorage storage)
        {
            IStorage previous = _storage;
            _storage = storage;
            try { return SaveToDisk(); }
            finally { _storage = previous; }
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "meta_currency", MetaCurrency },
                { "unlock_count", GetUnlockCount() },
                { "unlocked_class_count", UnlockedClassIds.Count },
                { "unlocked_hub_upgrade_count", UnlockedHubUpgradeIds.Count },
                { "unlocked_codex_count", UnlockedCodexEntryIds.Count },
                { "total_runs_completed", TotalRunsCompleted },
                { "total_runs_deaths", TotalRunsDeaths },
                { "highest_skill_level_seen", HighestSkillLevelSeen },
                { "last_payout_currency", LastPayoutCurrency },
                { "last_payout_reason", LastPayoutReason },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("Meta Currency: {0}", MetaCurrency));
            lines.Add(InfraCompat.Fmt("Runs Completed: {0}   Deaths: {1}", TotalRunsCompleted, TotalRunsDeaths));
            lines.Add(InfraCompat.Fmt("Hub Upgrades: {0}   Classes: {1}   Codex: {2}",
                UnlockedHubUpgradeIds.Count, UnlockedClassIds.Count, UnlockedCodexEntryIds.Count));
            if (LastPayoutCurrency > 0) lines.Add(InfraCompat.Fmt("Last Payout: +{0} ({1})", LastPayoutCurrency, LastPayoutReason));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
