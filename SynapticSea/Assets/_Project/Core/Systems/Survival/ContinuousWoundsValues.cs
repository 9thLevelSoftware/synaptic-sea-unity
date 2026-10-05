using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Closed immutable scratch data, not a publication or restore capability.
    internal sealed class ContinuousWoundsValues
    {
        internal const int MaximumRows = 256;
        static readonly string[] TextKeys = { "wound_id", "kind", "body_part", "source_id" };
        static readonly string[] DoubleKeys = { "severity", "bleed_rate", "infection_chance", "age_seconds" };
        static readonly string[] BoolKeys = { "treated", "bandaged" };
        readonly GdArray _rows;
        internal long NextId { get; }
        internal int Count => _rows.Count;
        ContinuousWoundsValues(long nextId, GdArray rows) { NextId = nextId; _rows = rows; }
        internal static ContinuousWoundsValues CaptureExact(WoundState actual)
        {
            if (actual == null || actual.Wounds == null || actual.Wounds.Count > MaximumRows)
                throw new ArgumentException("wounds_capture_capacity");
            var rows = new GdArray(actual.Wounds.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actual.Wounds.Count; i++)
            {
                if (!(actual.Wounds[i] is GdDict row) || row.Count != 10)
                    throw new ArgumentException("wounds_row_shape");
                var copy = new GdDict();
                foreach (var key in TextKeys)
                {
                    if (!(row.Get(key) is string text) || text.Length > 256)
                        throw new ArgumentException("wounds_text_shape");
                    copy[key] = text;
                }
                foreach (var key in DoubleKeys)
                {
                    if (!(row.Get(key) is double number) || double.IsNaN(number) || double.IsInfinity(number))
                        throw new ArgumentException("wounds_numeric_shape");
                    copy[key] = number;
                }
                foreach (var key in BoolKeys)
                {
                    if (!(row.Get(key) is bool flag)) throw new ArgumentException("wounds_bool_shape");
                    copy[key] = flag;
                }
                if (!ids.Add((string)copy.Get("wound_id"))) throw new ArgumentException("duplicate_wound_id");
                rows.Add(copy);
            }
            return new ContinuousWoundsValues(actual.ReadContinuousNextId(), rows);
        }
        internal GdDict CopyRow(int index) => ((GdDict)_rows[index]).DeepCopy();
        internal WoundState ExactScratch() => WoundState.CreateContinuousExactScratch(NextId, _rows);
        internal bool MatchesRaw(WoundState actual)
        {
            if (actual == null || actual.Wounds == null || actual.ReadContinuousNextId() != NextId || actual.Wounds.Count != Count)
                return false;
            for (int i = 0; i < Count; i++)
            {
                if (!(actual.Wounds[i] is GdDict current) || current.Count != 10) return false;
                var row = (GdDict)_rows[i];
                foreach (var key in TextKeys) if (!(current.Get(key) is string text) || text != (string)row.Get(key)) return false;
                foreach (var key in DoubleKeys)
                    if (!(current.Get(key) is double number) || BitConverter.DoubleToInt64Bits(number) != BitConverter.DoubleToInt64Bits((double)row.Get(key))) return false;
                foreach (var key in BoolKeys) if (!(current.Get(key) is bool flag) || flag != (bool)row.Get(key)) return false;
            }
            return true;
        }
    }
}
