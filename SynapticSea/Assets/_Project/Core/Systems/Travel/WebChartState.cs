// Ported from scripts/systems/web_chart_state.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Domain 10 (ADR-0045): session-only record of ship-marker knowledge the player has recorded onto their web
    /// chart. Two callers merge views in: found <c>web_chart</c> items (detail 2, "paper map" import) and scanner
    /// scans performed while a chart is possessed (detail 1-6). No get_summary/apply_summary: deliberately ephemeral.
    /// Pure-model-first: no scene-tree access.
    /// </summary>
    public class WebChartState : IStatusLineProvider
    {
        public static readonly IReadOnlyList<string> REQUIRED_FIELDS = new[] { "marker_id", "position", "size_class" };
        public static readonly IReadOnlyList<string> DETAIL_GATED_FIELDS = new[] { "ship_type", "condition", "predicted_status", "predicted_offline", "loot_hint" };

        /// <summary>marker_id -> {position, size_class, detail, ...detail-gated fields}</summary>
        readonly GdDict _entries = new GdDict();

        /// <summary>
        /// Merges <paramref name="views"/> (scan()/chart view dictionaries) into the chart at
        /// <paramref name="detailLevel"/>. Per marker: detail is max(existing.detail, detail_level) (never
        /// downgrades); fields present at the new detail are unioned in. Malformed views are skipped.
        /// Returns the count of markers that were newly added or had their detail upgraded.
        /// </summary>
        public long RecordViews(GdArray views, long detailLevel)
        {
            if (detailLevel < 0)
                throw new ArgumentOutOfRangeException(nameof(detailLevel), "WebChartState.record_views: detail_level must be non-negative");
            long changed = 0;
            if (views == null)
                return changed;
            foreach (object viewVariant in views)
            {
                if (!(viewVariant is GdDict view))
                    continue;
                bool ok = true;
                foreach (string field in REQUIRED_FIELDS)
                {
                    if (!view.Has(field))
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok)
                    continue;
                if (!(view.Get("position") is GdArray))
                    continue; // malformed non-Array position: skip like any other malformed view
                string markerId = V.Str(view.Get("marker_id", ""));
                if (markerId.Length == 0)
                    continue;
                GdDict existing = _entries.Get(markerId, null) as GdDict ?? new GdDict();
                long existingDetail = V.I64(existing.Get("detail", 0L));
                if (detailLevel <= existingDetail && _entries.Has(markerId))
                    continue; // no upgrade, no-op (idempotent re-record)
                GdDict merged = existing.DeepCopy();
                merged["position"] = ((GdArray)view.Get("position", new GdArray())).ShallowCopy();
                merged["size_class"] = V.I64(view.Get("size_class", 0L));
                merged["detail"] = Math.Max(existingDetail, detailLevel);
                foreach (string field in DETAIL_GATED_FIELDS)
                {
                    if (view.Has(field))
                        merged[field] = view[field];
                }
                _entries[markerId] = merged;
                changed += 1;
            }
            return changed;
        }

        public GdArray GetKnownMarkerIds()
        {
            var ids = new GdArray(_entries.Keys);
            GdSort.Sort(ids);
            return ids;
        }

        public GdDict GetEntry(string markerId)
        {
            object entry = _entries.Get(markerId, new GdDict());
            return entry is GdDict d ? d.DeepCopy() : new GdDict();
        }

        public long GetKnownCount() => _entries.Count;

        public IReadOnlyList<string> GetStatusLines() =>
            new List<string> { "WebChartState: known=" + _entries.Count };
    }
}
