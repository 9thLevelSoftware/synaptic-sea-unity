using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Standalone component registry (instance form used by component integration; the static IsSafeSnapshot is also used by ordinary play via CanonicalHash). Imports replace the complete validated state; all read boundaries
    /// return defensive snapshots. Catalog acquisition, holder capabilities and live-store bindings are external.
    /// </summary>
    public sealed class ItemInstanceState
    {
        GdDict _summary = new GdDict { { "schema_version", 1L }, { "instances", new GdDict() } };

        public ItemInstanceState() { }

        public GdDict Get(string instanceId)
        {
            if (instanceId == null || !(_summary.GetDictOrEmpty("instances").Get(instanceId) is GdDict row))
                return new GdDict();
            return row.DeepCopy();
        }

        public GdDict GetSummary() => _summary.DeepCopy();

        /// <summary>Validate every supplied record before publishing an independent complete replacement.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || !IsSafeSnapshot(summary) || !IsSchemaOne(summary) ||
                !(summary.Get("instances") is GdDict instances))
                return false;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in instances)
            {
                if (!(entry.Key is string key) || string.IsNullOrWhiteSpace(key) ||
                    !(entry.Value is GdDict row) || !ValidRow(key, row) || !identities.Add(key))
                    return false;
            }
            GdDict replacement = summary.DeepCopy();
            _summary = replacement;
            return true;
        }

        /// <summary>
        /// GdDict.DeepCopy preserves keys. Reject mutable keys and cyclic value graphs before copying any
        /// supplied snapshot; shared acyclic values remain valid and are copied independently.
        /// </summary>
        internal static bool IsSafeSnapshot(object snapshot) => IsSafeSnapshot(snapshot, new HashSet<object>());

        static bool IsSafeSnapshot(object snapshot, HashSet<object> ancestors)
        {
            if (snapshot is GdDict dictionary)
            {
                if (!ancestors.Add(dictionary)) return false;
                foreach (var entry in dictionary)
                {
                    if (entry.Key is GdDict || entry.Key is GdArray || !IsSafeSnapshot(entry.Value, ancestors))
                    {
                        ancestors.Remove(dictionary);
                        return false;
                    }
                }
                ancestors.Remove(dictionary);
            }
            else if (snapshot is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                foreach (object value in array)
                {
                    if (!IsSafeSnapshot(value, ancestors))
                    {
                        ancestors.Remove(array);
                        return false;
                    }
                }
                ancestors.Remove(array);
            }
            return true;
        }

        static bool IsSchemaOne(GdDict value) => value.Get("schema_version") is long version && version == 1L;

        static bool NonblankString(object value) => value is string text && !string.IsNullOrWhiteSpace(text);

        static bool ValidMetadata(object value) => NonblankString(value) || value is GdDict dictionary && !dictionary.IsEmpty;

        static bool FiniteNumber(object value, out double number)
        {
            if (value is long integer) number = integer;
            else if (value is double floating) number = floating;
            else { number = 0; return false; }
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        static bool ValidRow(string key, GdDict row)
        {
            if (!IsSchemaOne(row) || !(row.Get("instance_id") is string identity) ||
                string.IsNullOrWhiteSpace(identity) || !string.Equals(identity, key, StringComparison.Ordinal) ||
                !NonblankString(row.Get("definition_id")) || !NonblankString(row.Get("item_form")) ||
                !NonblankString(row.Get("holder")) || !(row.Get("revision") is long revision) || revision < 0 ||
                !FiniteNumber(row.Get("mass"), out double mass) || mass <= 0 ||
                !ValidMetadata(row.Get("origin")) || !ValidMetadata(row.Get("provenance")) || !row.Has("condition"))
                return false;
            if (row.Get("condition_state") is string state)
            {
                if (state == "unknown") return row.Get("condition") == null;
                if (state == "known")
                    return FiniteNumber(row.Get("condition"), out double condition) && condition >= 0 && condition <= 1;
            }
            return false;
        }
    }
}
