// Ported from scripts/systems/tutorial_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure tutorial trigger state (REQ-UI-005 / ADR-0033). <see cref="Trigger"/> fires once per
    /// (event, target) per run; <see cref="Dismiss"/> unlocks the matching codex entry.
    /// </summary>
    public class TutorialState : IStatusLineProvider
    {
        /// <summary><c>signal triggered(tutorial_id, title, body)</c>.</summary>
        public event Action<string, string, string> Triggered;
        /// <summary><c>signal dismissed(tutorial_id)</c>.</summary>
        public event Action<string> Dismissed;
        /// <summary><c>signal codex_unlocked(codex_entry_id)</c>.</summary>
        public event Action<string> CodexUnlocked;

        public const string SchemaVersion = "tutorial-state-1";
        public const string SaveKey = "tutorial_state";

        GdDict _catalog = new GdDict();
        readonly GdArray _tutorialIds = new GdArray();
        readonly GdDict _triggerToId = new GdDict();    // event|target -> tutorial_id
        readonly GdDict _idToEntry = new GdDict();      // tutorial_id -> entry_dict
        readonly GdDict _fired = new GdDict();          // event|target -> tutorial_id (fired this run)
        readonly GdDict _dismissed = new GdDict();      // tutorial_id -> true
        readonly GdDict _codexUnlocks = new GdDict();   // codex_entry_id -> tutorial_id that unlocked it
        string _latestTutorialId = "";                  // id of the most recently triggered tutorial

        public bool Configure(GdDict catalog)
        {
            if (!TutorialStateSchema.Validate(catalog)) return false;
            _catalog = catalog.DeepCopy();
            _tutorialIds.Clear();
            _triggerToId.Clear();
            _idToEntry.Clear();
            foreach (var tutorial in (GdArray)_catalog.Get("tutorials", new GdArray()))
            {
                var tDict = (GdDict)tutorial;
                string idStr = V.Str(tDict.Get("id", ""));
                _tutorialIds.Add(idStr);
                _idToEntry[idStr] = tDict;
                string eventStr = V.Str(tDict.Get("trigger_event", ""));
                string targetStr = V.Str(tDict.Get("trigger_target", ""));
                string key = eventStr + "|" + targetStr;
                _triggerToId[key] = idStr;
            }
            _fired.Clear();
            _dismissed.Clear();
            _codexUnlocks.Clear();
            _latestTutorialId = "";
            return true;
        }

        public bool IsKnown(string tutorialId) => _tutorialIds.Contains(tutorialId);

        public GdArray GetTutorialIds() => _tutorialIds.ShallowCopy();

        public int GetCatalogSize() => _tutorialIds.Count;

        public GdDict GetEntry(string tutorialId)
        {
            if (!IsKnown(tutorialId)) return new GdDict();
            return ((GdDict)_idToEntry[tutorialId]).DeepCopy();
        }

        public string GetTitle(string tutorialId)
        {
            if (!IsKnown(tutorialId)) return "";
            return V.Str(((GdDict)_idToEntry[tutorialId]).Get("title", ""));
        }

        public string GetBody(string tutorialId)
        {
            if (!IsKnown(tutorialId)) return "";
            return V.Str(((GdDict)_idToEntry[tutorialId]).Get("body", ""));
        }

        /// <summary>Tutorial id on first call; "" on re-fire or unknown trigger.</summary>
        public string Trigger(string evt, string target)
        {
            if (string.IsNullOrEmpty(evt) || string.IsNullOrEmpty(target)) return "";
            string key = evt + "|" + target;
            if (!_triggerToId.Has(key)) return "";
            string tutorialId = V.Str(_triggerToId[key]);
            if (_fired.Has(key)) return "";
            _fired[key] = tutorialId;
            _latestTutorialId = tutorialId;
            Triggered?.Invoke(tutorialId, GetTitle(tutorialId), GetBody(tutorialId));
            return tutorialId;
        }

        public bool Dismiss(string tutorialId)
        {
            if (!IsKnown(tutorialId))
            {
                CoreServices.Log.Warning("TutorialState: dismiss unknown tutorial '" + tutorialId + "'");
                return false;
            }
            if (_dismissed.Has(tutorialId)) return true;
            _dismissed[tutorialId] = true;
            Dismissed?.Invoke(tutorialId);
            var entry = (GdDict)_idToEntry[tutorialId];
            string codexEntryId = V.Str(entry.Get("codex_entry_id", ""));
            if (codexEntryId.Length != 0 && !_codexUnlocks.Has(codexEntryId))
            {
                _codexUnlocks[codexEntryId] = tutorialId;
                CodexUnlocked?.Invoke(codexEntryId);
            }
            return true;
        }

        public bool UnlockCodex(string codexEntryId)
        {
            if (string.IsNullOrEmpty(codexEntryId)) return false;
            if (_codexUnlocks.Has(codexEntryId)) return true;
            _codexUnlocks[codexEntryId] = "";
            CodexUnlocked?.Invoke(codexEntryId);
            return true;
        }

        public string GetLatestTutorialId() => _latestTutorialId;

        public bool HasPendingBanner()
        {
            if (_latestTutorialId.Length == 0) return false;
            return !_dismissed.Has(_latestTutorialId);
        }

        public bool IsDismissed(string tutorialId) => _dismissed.Has(tutorialId);

        public GdArray GetUnlockedCodexIds()
        {
            var unlocked = new GdArray();
            foreach (var key in _codexUnlocks.Keys) unlocked.Add(V.Str(key));
            GdSort.Sort(unlocked);
            return unlocked;
        }

        public bool IsCodexUnlocked(string codexEntryId) => _codexUnlocks.Has(codexEntryId);

        public int GetFiredCount() => _fired.Count;

        public int GetDismissedCount() => _dismissed.Count;

        public int GetCodexUnlockCount() => _codexUnlocks.Count;

        /// <summary>Reset fired / dismissed / unlocks for a new run; the catalog stays loaded.</summary>
        public void Reset()
        {
            _fired.Clear();
            _dismissed.Clear();
            _codexUnlocks.Clear();
            _latestTutorialId = "";
        }

        public GdDict GetSummary()
        {
            var firedKeys = new GdArray();
            foreach (var key in _fired.Keys) firedKeys.Add(V.Str(key));
            GdSort.Sort(firedKeys);
            var dismissedIds = new GdArray();
            foreach (var idStr in _dismissed.Keys) dismissedIds.Add(V.Str(idStr));
            GdSort.Sort(dismissedIds);
            GdArray codexIds = GetUnlockedCodexIds();
            return new GdDict
            {
                { "schema", SchemaVersion },
                { "catalog_size", _tutorialIds.Count },
                { "fired_count", _fired.Count },
                { "dismissed_count", _dismissed.Count },
                { "codex_unlock_count", _codexUnlocks.Count },
                { "fired_keys", firedKeys },
                { "dismissed_ids", dismissedIds },
                { "codex_unlocked_ids", codexIds },
                { "latest_tutorial_id", _latestTutorialId },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null) return false;
            if (V.Str(summary.Get("schema", "")) != SchemaVersion) return false;
            _fired.Clear();
            _dismissed.Clear();
            _codexUnlocks.Clear();
            if (summary.Get("fired_keys", new GdArray()) is GdArray fired)
            {
                foreach (var key in fired)
                {
                    string keyStr = V.Str(key);
                    if (_triggerToId.Has(keyStr)) _fired[keyStr] = V.Str(_triggerToId[keyStr]);
                }
            }
            if (summary.Get("dismissed_ids", new GdArray()) is GdArray dismissed)
            {
                foreach (var idStr in dismissed)
                {
                    string idStrS = V.Str(idStr);
                    if (IsKnown(idStrS)) _dismissed[idStrS] = true;
                }
            }
            if (summary.Get("codex_unlocked_ids", new GdArray()) is GdArray codex)
            {
                foreach (var entryId in codex)
                {
                    string entryIdS = V.Str(entryId);
                    if (entryIdS.Length != 0) _codexUnlocks[entryIdS] = "";
                }
            }
            _latestTutorialId = V.Str(summary.Get("latest_tutorial_id", ""));
            if (_latestTutorialId.Length != 0 && !IsKnown(_latestTutorialId)) _latestTutorialId = "";
            return true;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add(InfraCompat.Fmt("TutorialState: catalog={0} fired={1} dismissed={2} codex_unlocks={3} latest={4}",
                _tutorialIds.Count,
                _fired.Count,
                _dismissed.Count,
                _codexUnlocks.Count,
                _latestTutorialId.Length != 0 ? _latestTutorialId : "<none>"));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
