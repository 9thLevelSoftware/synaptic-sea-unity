// Ported from scripts/systems/module_integrity_map.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.1a: ship-level sparse map of <see cref="ModuleIntegrityState"/> (ADR-0051).
    /// Only non-pristine (or explicitly registered) modules are stored for persistence.
    /// </summary>
    public class ModuleIntegrityMap : IModuleIntegrityMap
    {
        // module_id -> ModuleIntegrityState, iterated in insertion order like the GDScript Dictionary.
        readonly Dictionary<string, ModuleIntegrityState> _modules = new Dictionary<string, ModuleIntegrityState>();
        readonly List<string> _order = new List<string>();

        static readonly string[] WallPrefixes = { "wall_", "bulkhead_", "panel_", "door_" };

        public void Clear()
        {
            _modules.Clear();
            _order.Clear();
        }

        public long Size() => _modules.Count;

        public bool HasModule(string moduleId) => _modules.ContainsKey(moduleId);

        public ModuleIntegrityState GetModule(string moduleId)
        {
            return _modules.TryGetValue(moduleId, out ModuleIntegrityState m) ? m : null;
        }

        public ModuleIntegrityState EnsureModule(string moduleId, string kind = "", GdDict composition = null, string roomId = "")
        {
            if (_modules.TryGetValue(moduleId, out ModuleIntegrityState existing))
            {
                if (existing != null && !string.IsNullOrEmpty(roomId))
                    existing.RoomId = roomId;
                return existing;
            }
            var m = new ModuleIntegrityState();
            m.Configure(new GdDict
            {
                { "module_id", moduleId },
                { "kind", kind },
                { "material_composition", composition ?? new GdDict() },
                { "room_id", roomId },
            });
            _modules[moduleId] = m;
            _order.Add(moduleId);
            return m;
        }

        public string ApplyDamage(string moduleId, double amount, string kind = "")
        {
            ModuleIntegrityState m = EnsureModule(moduleId, kind);
            return m.ApplyDamage(amount);
        }

        public string GetState(string moduleId)
        {
            ModuleIntegrityState m = GetModule(moduleId);
            if (m == null)
                return ModuleIntegrityState.STATE_INTACT;
            return m.State;
        }

        /// <summary>Sparse deltas: only modules that are not pristine.</summary>
        public GdArray ToSparseDeltas()
        {
            var output = new GdArray();
            foreach (string mid in _order)
            {
                ModuleIntegrityState m = _modules[mid];
                if (m == null)
                    continue;
                if (m.IsPristine())
                    continue;
                output.Append(m.GetSummary());
            }
            return output;
        }

        public void ApplySparseDeltas(GdArray deltas)
        {
            foreach (object entry in deltas)
            {
                if (!(entry is GdDict row))
                    continue;
                string mid = V.Str(row.Get("module_id", ""));
                if (mid.Length == 0)
                    continue;
                ModuleIntegrityState m = EnsureModule(mid, V.Str(row.Get("kind", "")));
                m.ApplySummary(row);
            }
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", "module_integrity_map_v1" },
                { "deltas", ToSparseDeltas() },
                { "registered", (long)_modules.Count },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary.IsEmpty)
                return false;
            object deltas = summary.Get("deltas", new GdArray());
            if (!(deltas is GdArray deltasArr))
                return false;
            Clear();
            ApplySparseDeltas(deltasArr);
            return true;
        }

        /// <summary>Determinism helper: sorted module ids + states.</summary>
        public string Fingerprint()
        {
            var ids = new GdArray();
            foreach (string mid in _order)
                ids.Append(mid);
            GdSort.Sort(ids);
            var parts = new List<string>();
            foreach (object mid in ids)
            {
                ModuleIntegrityState m = _modules[V.Str(mid)];
                parts.Add(V.Str(mid) + ":" + m.State + ":" + GdString.FormatFixed(m.Integrity, 4));
            }
            return string.Join("|", parts);
        }

        /// <summary>PKG-B2.1b: count wall modules that are breached or destroyed.</summary>
        public long CountWallBreaches()
        {
            long count = 0;
            foreach (string mid in _order)
            {
                ModuleIntegrityState m = _modules[mid];
                if (m == null)
                    continue;
                string kind = m.Kind;
                string st = m.State;
                bool isWall = false;
                string k = kind.ToLowerInvariant();
                foreach (string prefix in WallPrefixes)
                {
                    if (GdString.BeginsWith(k, prefix) || GdString.Find(k, prefix) >= 0)
                    {
                        isWall = true;
                        break;
                    }
                }
                if (!isWall)
                    continue;
                if (st == ModuleIntegrityState.STATE_BREACHED || st == ModuleIntegrityState.STATE_DESTROYED)
                    count += 1;
            }
            return count;
        }

        public List<string> ModuleIds()
        {
            var output = new List<string>();
            foreach (string mid in _order)
                output.Add(mid);
            GdString.SortStrings(output);
            return output;
        }

        IEnumerable<string> IModuleIntegrityMap.ModuleIds() => ModuleIds();

        /// <summary>Room ids that have at least one wall module with nav_gap consequence.</summary>
        public List<string> RoomsWithNavGaps()
        {
            var rooms = new GdDict();
            foreach (string mid in _order)
            {
                ModuleIntegrityState m = _modules[mid];
                if (m == null)
                    continue;
                string st = m.State;
                if (st != ModuleIntegrityState.STATE_BREACHED && st != ModuleIntegrityState.STATE_DESTROYED)
                    continue;
                GdDict summary = m.GetSummary();
                string roomId = V.Str(summary.Get("room_id", ""));
                if (roomId.Length == 0)
                    roomId = m.RoomId;
                if (roomId.Length == 0)
                {
                    // mid format room/name
                    List<string> parts = GdString.Split(mid, "/");
                    if (parts.Count >= 1)
                        roomId = parts[0];
                }
                if (roomId.Length != 0)
                    rooms[roomId] = true;
                if (m.OwnerRooms != null)
                {
                    foreach (string rid in m.OwnerRooms)
                    {
                        if (!string.IsNullOrEmpty(rid))
                            rooms[rid] = true;
                    }
                }
            }
            var output = new List<string>();
            foreach (object rid in rooms.Keys)
                output.Add(V.Str(rid));
            GdString.SortStrings(output);
            return output;
        }
    }
}
