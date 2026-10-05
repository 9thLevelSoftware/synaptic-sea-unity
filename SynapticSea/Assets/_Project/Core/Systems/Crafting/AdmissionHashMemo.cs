using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    // Lifetime is exactly one fresh DomainBundle admission. No live input reference is retained.
    internal sealed class AdmissionHashMemo : IDisposable
    {
        [ThreadStatic] static AdmissionHashMemo _current;
        readonly AdmissionHashMemo _previous;
        readonly Dictionary<string, List<Entry>> _entries = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        sealed class Entry { internal Snapshot Input; internal string Digest; }
        sealed class Snapshot
        {
            internal Type Type;
            internal object Scalar;
            internal Snapshot[] Keys, Values;
        }
        AdmissionHashMemo() { _previous = _current; _current = this; }
        internal static IDisposable Begin() => new AdmissionHashMemo();
        public void Dispose() { _current = _previous; _entries.Clear(); }
        static string Bucket(object value) => value == null ? "null" : value.GetType().FullName + ":" + (value is GdDict d ? d.Count : value is GdArray a ? a.Count : 0);
        internal static bool TryGet(object input, out string digest)
        {
            digest = null;
            if (_current == null || !_current._entries.TryGetValue(Bucket(input), out var entries)) return false;
            foreach (var entry in entries) if (Matches(entry.Input, input)) { digest = entry.Digest; return true; }
            return false;
        }
        internal static void Record(object input, string digest)
        {
            if (_current == null) return;
            Snapshot snapshot;
            snapshot = Capture(input);
            if (snapshot == null) return; // Unsupported values retain the original canonical Hash path.
            string key = Bucket(input);
            if (!_current._entries.TryGetValue(key, out var entries)) _current._entries[key] = entries = new List<Entry>();
            entries.Add(new Entry { Input = snapshot, Digest = digest });
        }
        static Snapshot Capture(object value)
        {
            var result = new Snapshot { Type = value?.GetType() };
            if (value is GdDict dictionary)
            {
                result.Keys = new Snapshot[dictionary.Count]; result.Values = new Snapshot[dictionary.Count]; int i = 0;
                foreach (var entry in dictionary)
                { result.Keys[i] = Capture(entry.Key); result.Values[i] = Capture(entry.Value); if (result.Keys[i] == null || result.Values[i] == null) return null; i++; }
            }
            else if (value is GdArray array)
            {
                result.Values = new Snapshot[array.Count];
                for (int i = 0; i < array.Count; i++) { result.Values[i] = Capture(array[i]); if (result.Values[i] == null) return null; }
            }
            else if (value == null || value is string || value is bool || value is long || value is int || value is double || value is float || value is Vec3) result.Scalar = value;
            else return null;
            return result;
        }
        static bool Matches(Snapshot expected, object actual)
        {
            if (expected.Type != actual?.GetType()) return false;
            if (actual == null) return true;
            if (actual is GdDict dictionary)
            {
                if (dictionary.Count != expected.Values.Length) return false; int i = 0;
                // Conservative insertion sequence preserves stable-sort ties between differently typed keys.
                foreach (var entry in dictionary) if (!Matches(expected.Keys[i], entry.Key) || !Matches(expected.Values[i++], entry.Value)) return false;
                return true;
            }
            if (actual is GdArray array)
            {
                if (array.Count != expected.Values.Length) return false;
                for (int i = 0; i < array.Count; i++) if (!Matches(expected.Values[i], array[i])) return false;
                return true;
            }
            if (actual is double d) return BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits((double)expected.Scalar);
            if (actual is float f) return BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits((float)expected.Scalar);
            if (actual is Vec3 v) { var e = (Vec3)expected.Scalar; return BitConverter.SingleToInt32Bits(v.X) == BitConverter.SingleToInt32Bits(e.X) && BitConverter.SingleToInt32Bits(v.Y) == BitConverter.SingleToInt32Bits(e.Y) && BitConverter.SingleToInt32Bits(v.Z) == BitConverter.SingleToInt32Bits(e.Z); }
            return actual.Equals(expected.Scalar);
        }
    }
}
