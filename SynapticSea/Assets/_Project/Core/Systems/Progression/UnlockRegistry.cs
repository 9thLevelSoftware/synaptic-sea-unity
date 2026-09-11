// Ported from scripts/systems/unlock_registry.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-PM-009 / ADR-0033 cross-run unlock registry. Wraps a codex / hub-scene / class-unlock catalog and
    /// persists to <c>user://unlock_registry.json</c> (through an injected <see cref="IStorage"/>).
    /// </summary>
    public class UnlockRegistry : IDiskPersisted, IStatusLineProvider
    {
        public const string SchemaVersion = "unlock-registry-1";
        public const string SavePath = "user://unlock_registry.json";

        readonly GdDict _catalogById = new GdDict(); // unlock_id -> {category, display_name, ...}
        readonly GdDict _unlocked = new GdDict();    // unlock_id -> true (idempotent)

        IStorage _storage;
        IClock _clock;

        public UnlockRegistry(IStorage storage = null, IClock clock = null)
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

        /// <summary>Configures from a parsed catalog; unlock calls for unknown ids are rejected.</summary>
        public bool Configure(GdDict catalog = null)
        {
            _catalogById.Clear();
            _unlocked.Clear();
            if (catalog == null) catalog = new GdDict();
            object variant = catalog.Get("unlocks", new GdArray());
            if (!(variant is GdArray unlocks)) return false;
            foreach (var entry in unlocks)
            {
                if (!(entry is GdDict entryDict)) continue;
                string uid = V.Str(entryDict.Get("unlock_id", ""));
                if (uid.Length == 0) continue;
                _catalogById[uid] = entryDict.DeepCopy();
            }
            return true;
        }

        /// <summary>Reloads the catalog at runtime, preserving the existing unlock set.</summary>
        public void SetCatalog(GdDict catalog)
        {
            _catalogById.Clear();
            if (catalog == null) return;
            object variant = catalog.Get("unlocks", new GdArray());
            if (!(variant is GdArray unlocks)) return;
            foreach (var entry in unlocks)
            {
                if (!(entry is GdDict entryDict)) continue;
                string uid = V.Str(entryDict.Get("unlock_id", ""));
                if (uid.Length == 0) continue;
                _catalogById[uid] = entryDict.DeepCopy();
            }
        }

        public int GetCatalogSize() => _catalogById.Count;

        public bool IsKnown(string unlockId) => _catalogById.Has(unlockId);

        public string GetCategory(string unlockId) => IsKnown(unlockId) ? V.Str(Entry(unlockId).Get("category", "")) : "";

        public string GetDisplayName(string unlockId) => IsKnown(unlockId) ? V.Str(Entry(unlockId).Get("display_name", unlockId)) : "";

        public string GetTriggerEvent(string unlockId) => IsKnown(unlockId) ? V.Str(Entry(unlockId).Get("trigger_event", "")) : "";

        public string GetTriggerTarget(string unlockId) => IsKnown(unlockId) ? V.Str(Entry(unlockId).Get("trigger_target", "")) : "";

        public string GetClassId(string unlockId) => IsKnown(unlockId) ? V.Str(Entry(unlockId).Get("class_id", "")) : "";

        /// <summary>Row-side "*" and "any" are wildcards matching any fired target (PR #55 Codex P1).</summary>
        bool TriggerMatches(string rowEvent, string rowTarget, string firedEvent, string firedTarget)
        {
            if (rowEvent != firedEvent) return false;
            if (rowTarget == "*" || rowTarget == "any") return true;
            return rowTarget == firedTarget;
        }

        /// <summary>
        /// class_ids of ALL class-category rows whose trigger matches, unlocking each (idempotent).
        /// </summary>
        public GdArray ClassIdsForTrigger(string triggerEvent, string triggerTarget)
        {
            var output = new GdArray();
            if (string.IsNullOrEmpty(triggerEvent)) return output;
            foreach (var uidKey in _catalogById.Keys)
            {
                string uid = V.Str(uidKey);
                GdDict entry = Entry(uid);
                if (V.Str(entry.Get("category", "")) != "class") continue;
                if (TriggerMatches(V.Str(entry.Get("trigger_event", "")), V.Str(entry.Get("trigger_target", "")), triggerEvent, triggerTarget))
                {
                    Unlock(uid);
                    string cls = V.Str(entry.Get("class_id", ""));
                    if (cls.Length != 0) output.Add(cls);
                }
            }
            return output;
        }

        /// <summary>True on first-time unlock; false when unknown, already unlocked, or empty.</summary>
        public bool Unlock(string unlockId)
        {
            if (string.IsNullOrEmpty(unlockId)) return false;
            if (!IsKnown(unlockId)) return false;
            if (_unlocked.Has(unlockId)) return false;
            _unlocked[unlockId] = true;
            return true;
        }

        /// <summary>Unlocks the first catalog row matching (event, target); returns its id or "".</summary>
        public string UnlockForTrigger(string triggerEvent, string triggerTarget)
        {
            if (string.IsNullOrEmpty(triggerEvent)) return "";
            string resolved = "";
            foreach (var uidKey in _catalogById.Keys)
            {
                string uid = V.Str(uidKey);
                GdDict entry = Entry(uid);
                string evt = V.Str(entry.Get("trigger_event", ""));
                string tgt = V.Str(entry.Get("trigger_target", ""));
                if (TriggerMatches(evt, tgt, triggerEvent, triggerTarget))
                {
                    if (Unlock(uid))
                    {
                        resolved = uid;
                        break;
                    }
                }
            }
            return resolved;
        }

        public bool IsUnlocked(string unlockId) => _unlocked.Has(unlockId) && V.Bool(_unlocked[unlockId]);

        public GdArray GetUnlockedIds() => InfraCompat.SortedKeys(_unlocked);

        public int GetUnlockCount() => _unlocked.Count;

        public GdArray GetEntriesForCategory(string category)
        {
            var output = new GdArray();
            foreach (var uidKey in _catalogById.Keys)
            {
                string uid = V.Str(uidKey);
                GdDict entry = Entry(uid);
                if (V.Str(entry.Get("category", "")) == category)
                {
                    output.Add(new GdDict
                    {
                        { "unlock_id", uid },
                        { "display_name", V.Str(entry.Get("display_name", uid)) },
                        { "description", V.Str(entry.Get("description", "")) },
                        { "unlocked", IsUnlocked(uid) },
                    });
                }
            }
            output.SortCustom((a, b) => V.CompareCodePoints(V.Str(((GdDict)a).Get("unlock_id", "")), V.Str(((GdDict)b).Get("unlock_id", ""))) < 0);
            return output;
        }

        public List<string> GetStatusLines()
        {
            return new List<string> { InfraCompat.Fmt("Unlock Registry: {0} / {1}", _unlocked.Count, _catalogById.Count) };
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "unlocked", _unlocked.ShallowCopy() },
                { "saved_at", Clock.DateTimeString(true) },
            };
        }

        public bool ApplySummary(object summary)
        {
            if (summary == null || !(summary is GdDict dict)) return false;
            string schema = V.Str(dict.Get("schema", ""));
            if (schema != SchemaVersion) return false;
            _unlocked.Clear();
            object variant = dict.Get("unlocked", new GdDict());
            if (!(variant is GdDict unlocked)) return false;
            foreach (var k in unlocked.Keys)
            {
                if (V.Bool(unlocked[k]))
                {
                    string uid = V.Str(k);
                    if (IsKnown(uid)) _unlocked[uid] = true;
                }
            }
            return true;
        }

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

        GdDict Entry(string unlockId) => (GdDict)_catalogById[unlockId];
    }
}
