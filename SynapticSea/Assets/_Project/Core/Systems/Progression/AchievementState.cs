// Ported from scripts/systems/achievement_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-RL-003 / REQ-RL-004 achievement state. Per-run unlock set over the catalog; <see cref="Unlock"/> is
    /// idempotent and rejects unknown ids. Persists to <c>user://achievements.json</c> through an <see cref="IStorage"/>.
    /// </summary>
    public class AchievementState : ISimModel, IDiskPersisted, IStatusLineProvider
    {
        public const string SchemaVersion = "release-achievements-1";
        public const string SavePath = "user://achievements.json";

        GdDict _catalog = new GdDict();
        readonly GdArray _catalogIds = new GdArray();
        readonly GdDict _triggerToId = new GdDict();
        readonly GdDict _unlocked = new GdDict(); // id -> {unlocked: true, unlocked_at: "iso"}
        string _runId = "";

        IStorage _storage;
        IClock _clock;

        /// <summary>
        /// Unity port: raised with the id when <see cref="Unlock"/> newly unlocks an achievement (not on
        /// <see cref="ApplySummary"/> restores). Platform layers (Steam) mirror unlocks from it.
        /// </summary>
        public event Action<string> Unlocked;

        public AchievementState(IStorage storage = null, IClock clock = null)
        {
            _storage = storage;
            _clock = clock;
        }

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

        public void Configure(GdDict catalog)
        {
            _catalog = catalog ?? new GdDict();
            _catalogIds.Clear();
            _triggerToId.Clear();
            object listVariant = _catalog.Get("achievements", new GdArray());
            if (listVariant is GdArray list)
            {
                foreach (var entry in list)
                {
                    if (!(entry is GdDict dict)) continue;
                    string idStr = V.Str(dict.Get("id", ""));
                    if (idStr.Length == 0) continue;
                    _catalogIds.Add(idStr);
                    string triggerEvent = V.Str(dict.Get("trigger_event", ""));
                    string triggerTarget = V.Str(dict.Get("trigger_target", ""));
                    if (triggerEvent.Length != 0)
                    {
                        string key = triggerEvent + "|" + triggerTarget;
                        _triggerToId[key] = idStr;
                        // Also register a wildcard target alias for "any".
                        if (triggerTarget == "any")
                        {
                            string anyKey = triggerEvent + "|*";
                            _triggerToId[anyKey] = idStr;
                        }
                    }
                }
            }
        }

        public bool IsKnown(string id) => _catalogIds.Contains(id);

        public GdArray GetCatalogIds() => _catalogIds.ShallowCopy();

        public int GetCatalogSize() => _catalogIds.Count;

        public bool Unlock(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!_catalogIds.Contains(id)) return false;
            if (_unlocked.Has(id)) return false;
            _unlocked[id] = new GdDict
            {
                { "unlocked", true },
                { "unlocked_at", Clock.DateTimeString(true) },
            };
            Unlocked?.Invoke(id);
            return true;
        }

        /// <summary>
        /// Resolves (event, target) to an achievement id (exact key first, then the "event|*" wildcard alias) and
        /// unlocks it. Returns the unlocked id, or "" when nothing matches or it was already unlocked.
        /// </summary>
        public string UnlockForTrigger(string triggerEvent, string triggerTarget)
        {
            if (string.IsNullOrEmpty(triggerEvent)) return "";
            string exactKey = triggerEvent + "|" + triggerTarget;
            if (_triggerToId.Has(exactKey))
            {
                string idForExact = V.Str(_triggerToId[exactKey]);
                if (Unlock(idForExact)) return idForExact;
                return "";
            }
            string wildcardKey = triggerEvent + "|*";
            if (_triggerToId.Has(wildcardKey))
            {
                string idForWild = V.Str(_triggerToId[wildcardKey]);
                if (Unlock(idForWild)) return idForWild;
            }
            return "";
        }

        public bool IsUnlocked(string id) => _unlocked.Has(id) && V.Bool(((GdDict)_unlocked[id]).Get("unlocked", false));

        public GdArray GetUnlocked() => InfraCompat.SortedKeys(_unlocked);

        public int GetUnlockCount() => _unlocked.Count;

        public void StartNewRun(string runId = "")
        {
            _runId = runId;
            _unlocked.Clear();
        }

        public string GetRunId() => _runId;

        /// <summary>Round-trip for the per-run save seam (current run only).</summary>
        public GdDict ToDict()
        {
            var unlockedDict = new GdDict();
            foreach (var idStr in _unlocked.Keys) unlockedDict[V.Str(idStr)] = _unlocked[idStr];
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "run_id", _runId },
                { "unlocked", unlockedDict },
                { "saved_at", Clock.DateTimeString(true) },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null) return false;
            string schema = V.Str(summary.Get("schema", ""));
            if (schema != SchemaVersion) return false;
            _runId = V.Str(summary.Get("run_id", ""));
            object unlockedVariant = summary.Get("unlocked", new GdDict());
            if (!(unlockedVariant is GdDict unlocked)) return false;
            _unlocked.Clear();
            foreach (var key in unlocked.Keys)
            {
                string idStr = V.Str(key);
                if (idStr.Length == 0) continue;
                if (!_catalogIds.Contains(idStr)) continue;
                var entry = new GdDict();
                object rawEntry = unlocked[key];
                if (rawEntry is GdDict rawDict) entry = rawDict;
                _unlocked[idStr] = new GdDict
                {
                    { "unlocked", V.Bool(entry.Get("unlocked", true)) },
                    { "unlocked_at", V.Str(entry.Get("unlocked_at", "")) },
                };
            }
            return true;
        }

        public GdDict GetSummary()
        {
            GdArray sortedIds = InfraCompat.SortedKeys(_unlocked);
            var unlockedDict = new GdDict();
            foreach (var idStr in sortedIds)
            {
                var entry = (GdDict)_unlocked[idStr];
                unlockedDict[V.Str(idStr)] = new GdDict
                {
                    { "unlocked", V.Bool(entry.Get("unlocked", false)) },
                    { "unlocked_at", V.Str(entry.Get("unlocked_at", "")) },
                };
            }
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "run_id", _runId },
                { "catalog_size", _catalogIds.Count },
                { "unlock_count", _unlocked.Count },
                { "unlocked", unlockedDict },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("Achievements: {0} / {1}", _unlocked.Count, _catalogIds.Count));
            foreach (var idStr in GetUnlocked()) lines.Add("  - " + V.Str(idStr));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public bool SaveToDisk()
        {
            try
            {
                Storage.WriteText(SavePath, GdJson.Stringify(ToDict(), "\t"));
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        public bool LoadFromDisk()
        {
            if (!Storage.FileExists(SavePath)) return false;
            string jsonText = Storage.ReadText(SavePath);
            if (jsonText == null) return false;
            object parsed = GdJson.ParseString(jsonText);
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
    }
}
