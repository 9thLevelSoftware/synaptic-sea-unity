// Ported from scripts/systems/manifestation_pool.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-C3.3: data-driven sanity manifestation pool + narrative force hooks.
    /// Pure — HallucinationDirector consumes kinds/entries; scene never loads this JSON.
    /// </summary>
    public sealed class ManifestationPool
    {
        public const string DEFAULT_PATH = "res://data/sanity/manifestation_pool.json";

        public string Schema = "";
        public string Version = "";
        public GdDict Kinds = new GdDict();            // kind_id -> config
        public GdDict Entries = new GdDict();          // entry_id -> entry
        public GdDict RoomTriggers = new GdDict();     // room_id -> Array[entry_id]
        public GdDict AudioLogTriggers = new GdDict(); // log_id -> Array[entry_id]
        string _loadedPath = "";

        /// <summary>Path of the last successful <see cref="LoadFile"/> (GDScript <c>_loaded_path</c>).</summary>
        public string LoadedPath => _loadedPath;

        public bool LoadDefault() => LoadFile(DEFAULT_PATH);

        public bool LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !CatalogRegistry.Exists(path)) return false;
            if (!(CatalogRegistry.Load(path) is GdDict root)) return false;
            Schema = V.Str(root.Get("schema", ""));
            Version = V.Str(root.Get("version", ""));
            object kindsV = root.Get("kinds", new GdDict());
            object entriesV = root.Get("entries", new GdDict());
            if (!(kindsV is GdDict kindsDict) || !(entriesV is GdDict entriesDict)) return false;
            Kinds = kindsDict.DeepCopy();
            Entries = entriesDict.DeepCopy();
            RoomTriggers.Clear();
            AudioLogTriggers.Clear();
            if (root.Get("narrative_hooks", new GdDict()) is GdDict hooks)
            {
                object rt = hooks.Get("room_triggers", new GdDict());
                object at = hooks.Get("audio_log_triggers", new GdDict());
                if (rt is GdDict rtDict)
                    foreach (object k in new List<object>(rtDict.Keys))
                        RoomTriggers[V.Str(k)] = AsStringArray(rtDict[k]);
                if (at is GdDict atDict)
                    foreach (object k2 in new List<object>(atDict.Keys))
                        AudioLogTriggers[V.Str(k2)] = AsStringArray(atDict[k2]);
            }
            _loadedPath = path;
            return !Kinds.IsEmpty;
        }

        static GdArray AsStringArray(object v)
        {
            var outArr = new GdArray();
            if (!(v is GdArray arr)) return outArr;
            foreach (object item in arr)
            {
                string s = V.Str(item);
                if (s.Length != 0) outArr.Add(s);
            }
            return outArr;
        }

        public bool HasKind(string kindId) => Kinds.Has(kindId);

        public GdDict GetKind(string kindId)
        {
            if (!Kinds.Has(kindId)) return new GdDict();
            return (Kinds[kindId] as GdDict)?.DeepCopy() ?? new GdDict();
        }

        public List<string> KindIds()
        {
            var outList = new List<string>();
            foreach (object k in Kinds.Keys) outList.Add(V.Str(k));
            GdSort.SortCustom(outList, (a, b) => V.CompareCodePoints(a, b) < 0);
            return outList;
        }

        public bool HasEntry(string entryId) => Entries.Has(entryId);

        public GdDict GetEntry(string entryId)
        {
            if (!Entries.Has(entryId)) return new GdDict();
            return (Entries[entryId] as GdDict)?.DeepCopy() ?? new GdDict();
        }

        public long EntryCount() => Entries.Count;

        public long KindCount() => Kinds.Count;

        /// <summary>Weighted entry pick for a kind at tier (excludes force_only entries).</summary>
        public string PickEntryId(string kindId, long tier, long seedHash)
        {
            var candidateIds = new List<string>();
            var candidateWeights = new List<long>();
            long totalW = 0;
            foreach (var kv in Entries)
            {
                var e = kv.Value as GdDict ?? new GdDict();
                if (V.Str(e.Get("kind", "")) != kindId) continue;
                if (V.Bool(e.Get("force_only", false))) continue;
                if (V.I64(e.Get("min_tier", 0L)) > tier) continue;
                long w = System.Math.Max(0L, V.I64(e.Get("weight", 1L)));
                if (w <= 0) continue;
                candidateIds.Add(V.Str(kv.Key));
                candidateWeights.Add(w);
                totalW += w;
            }
            if (candidateIds.Count == 0 || totalW <= 0) return "";
            long roll = ItemsCompat.AbsI(seedHash) % totalW;
            long cum = 0;
            for (int i = 0; i < candidateIds.Count; i++)
            {
                cum += candidateWeights[i];
                if (roll < cum) return candidateIds[i];
            }
            return candidateIds[candidateIds.Count - 1];
        }

        /// <summary>Narrative: room enter force list (entry ids that exist).</summary>
        public GdArray ForceEntriesForRoom(string roomId)
        {
            if (string.IsNullOrEmpty(roomId) || !RoomTriggers.Has(roomId)) return new GdArray();
            return FilterExisting(RoomTriggers[roomId] as GdArray);
        }

        /// <summary>Narrative: audio log force list.</summary>
        public GdArray ForceEntriesForAudioLog(string logId)
        {
            if (string.IsNullOrEmpty(logId) || !AudioLogTriggers.Has(logId)) return new GdArray();
            return FilterExisting(AudioLogTriggers[logId] as GdArray);
        }

        GdArray FilterExisting(GdArray ids)
        {
            var outArr = new GdArray();
            if (ids == null) return outArr;
            foreach (object id in ids)
            {
                string s = V.Str(id);
                if (Entries.Has(s)) outArr.Add(s);
            }
            return outArr;
        }

        /// <summary>Kind config usable by HallucinationDirector (mirrors legacy KIND_CONFIG shape).</summary>
        public GdDict KindScheduleConfig()
        {
            var outDict = new GdDict();
            foreach (var kv in Kinds)
            {
                var k = kv.Value as GdDict ?? new GdDict();
                outDict[V.Str(kv.Key)] = new GdDict
                {
                    { "min_tier", V.I64(k.Get("min_tier", 1L)) },
                    { "interval", V.F64(k.Get("interval", 6.0)) },
                    { "interval_t3", V.F64(k.Get("interval_t3", k.Get("interval", 4.0))) },
                    { "max", V.I64(k.Get("max", 1L)) },
                    { "max_t3", V.I64(k.Get("max_t3", k.Get("max", 1L))) },
                    { "ttl", V.F64(k.Get("ttl", 3.0)) },
                };
            }
            return outDict;
        }
    }
}
