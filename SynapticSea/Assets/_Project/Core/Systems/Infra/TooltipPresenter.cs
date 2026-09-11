// Ported from scripts/systems/tooltip_presenter.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure tooltip query -> payload resolver (REQ-UI-004 / ADR-0033). Owns a catalog of
    /// {subject_kind, subject_id} -> {title, body, footer} mappings. <see cref="Resolve"/> returns a
    /// <see cref="TooltipPayload"/> or null for unknown ids; unknown ids are never mapped to a default.
    /// Headless-queryable: scenes subscribe to <see cref="PayloadChanged"/>, but the resolver works without it.
    /// </summary>
    public class TooltipPresenter
    {
        /// <summary>GDScript <c>signal payload_changed(payload)</c>; payload may be null.</summary>
        public event Action<TooltipPayload> PayloadChanged;

        public const string SCHEMA_VERSION = "tooltip-presenter-1";
        public const string SAVE_KEY = "tooltip_presenter";

        readonly GdDict _entriesByKind = new GdDict(); // subject_kind -> {subject_id -> entry_dict}
        readonly GdDict _entriesById = new GdDict();   // entry_id -> entry_dict
        TooltipPayload _currentPayload = null;        // last-resolved TooltipPayload (or null)
        GdDict _lastQuery = new GdDict();              // last query dict for the summary

        public bool Configure(GdDict catalog)
        {
            if (!TooltipSchema.Validate(catalog))
                return false;
            _entriesByKind.Clear();
            _entriesById.Clear();
            foreach (object entry in catalog.GetArrayOrEmpty("entries"))
            {
                GdDict entryDict = (GdDict)entry;
                string kind = V.Str(entryDict.Get("subject_kind", ""));
                string idStr = V.Str(entryDict.Get("subject_id", ""));
                if (!_entriesByKind.Has(kind))
                    _entriesByKind[kind] = new GdDict();
                ((GdDict)_entriesByKind[kind])[idStr] = entryDict;
                _entriesById[V.Str(entryDict.Get("id", ""))] = entryDict;
            }
            _currentPayload = null;
            _lastQuery = new GdDict();
            return true;
        }

        /// <summary>
        /// Resolve a tooltip query ({subject_kind, subject_id}). Returns null for unknown ids or malformed
        /// queries. Emits <see cref="PayloadChanged"/> on every resolve (null included).
        /// </summary>
        public TooltipPayload Resolve(GdDict query)
        {
            if (query == null)
            {
                SetPayload(null, new GdDict());
                return null;
            }
            string kind = V.Str(query.Get("subject_kind", ""));
            string idStr = V.Str(query.Get("subject_id", ""));
            if (kind.Length == 0 || idStr.Length == 0)
            {
                SetPayload(null, query);
                return null;
            }
            if (!_entriesByKind.Has(kind))
            {
                SetPayload(null, query);
                return null;
            }
            GdDict kindMap = (GdDict)_entriesByKind[kind];
            if (!kindMap.Has(idStr))
            {
                SetPayload(null, query);
                return null;
            }
            GdDict entry = (GdDict)kindMap[idStr];
            var payload = new TooltipPayload(
                V.Str(entry.Get("title", "")),
                V.Str(entry.Get("body", "")),
                V.Str(entry.Get("footer", "")),
                kind,
                idStr);
            SetPayload(payload, query);
            return payload;
        }

        /// <summary>Returns the current payload (or null).</summary>
        public TooltipPayload GetCurrentPayload() => _currentPayload;

        /// <summary>Number of registered catalog entries.</summary>
        public long GetCatalogSize() => _entriesById.Count;

        /// <summary>The registered "kind/id" pairs sorted lexicographically.</summary>
        public GdArray GetCatalogPairs()
        {
            var pairs = new GdArray();
            foreach (object kind in _entriesByKind.Keys)
            {
                foreach (object idStr in ((GdDict)_entriesByKind[kind]).Keys)
                    pairs.Append(V.Str(kind) + "/" + V.Str(idStr));
            }
            GdSort.Sort(pairs);
            return pairs;
        }

        /// <summary>Headless round-trip seam for the save/load smoke.</summary>
        public GdDict GetSummary()
        {
            object payloadDict = null;
            if (_currentPayload != null)
                payloadDict = _currentPayload.ToDict();
            return new GdDict
            {
                { "schema", SCHEMA_VERSION },
                { "catalog_size", (long)_entriesById.Count },
                { "has_payload", _currentPayload != null },
                { "payload", payloadDict },
            };
        }

        public List<string> GetStatusLines()
        {
            return new List<string>
            {
                "TooltipPresenter: catalog=" + GdString.FormatInt(_entriesById.Count) +
                " current=" + (_currentPayload != null ? "present" : "none"),
            };
        }

        void SetPayload(TooltipPayload payload, GdDict query)
        {
            _currentPayload = payload;
            _lastQuery = query.DeepCopy();
            PayloadChanged?.Invoke(payload);
        }
    }
}
