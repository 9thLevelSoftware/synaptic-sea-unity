using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Exact conventional JSON for explicitly admitted paid snapshots and their generation metadata.
    /// Resource documents and legacy/schema2 snapshots retain their original JSON policy.</summary>
    public static class PaidSnapshotCodec
    {
        public enum Policy { Raw, TypedOwner, OrdinaryRun, OrdinaryWorld, DiagnosticRun, DiagnosticWorld }

        public const string OrdinaryMode = "paid_crafting_ordinary";
        public const string DiagnosticMode = "component-live-diagnostic";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal const int MaxNodes = 100000;
        internal const int MaxRawDepth = 128;
        // Codec overhead: W = 2 + 3N - Z + 2V + P, with V <= N-1 and P <= (N-1)/2.
        internal const int MaxOwnerWireNodes = 5 * MaxNodes + (MaxNodes - 1) / 2;
        internal const int MaxOwnerWireDepth = 3 * MaxRawDepth + 2;

        public static GdDict Parse(string text) => Parse(text, Policy.Raw);

        public static GdDict Parse(string text, Policy policy) => SaveCommitCoordinator.ParsePaidObject(text, policy);
        public static string Stringify(GdDict value) => Stringify(value, Policy.Raw);

        public static string Stringify(GdDict value, Policy policy)
        {
            if (value == null) throw new ValidationException("$:null:root_null");
            if (!IsValidGraph(value, policy))
            {
                string diagnostic = "$:policy:invalid_snapshot";
                if (!KnownPolicy(policy)) diagnostic = "$:policy:unknown_policy";
                else if (policy == Policy.Raw) Diagnose(value, out diagnostic);
                throw new ValidationException(diagnostic);
            }
            var output = new StringBuilder(); Write(output, value); return output.ToString();
        }

        public sealed class ValidationException : ArgumentException
        {
            public string Diagnostic { get; }
            internal ValidationException(string diagnostic) : base("invalid_paid_snapshot:" + diagnostic) { Diagnostic = diagnostic; }
        }

        internal static bool IsValidGraph(GdDict value) => IsValidGraph(value, Policy.Raw);

        internal static bool IsValidGraph(GdDict value, Policy policy)
        {
            if (value == null || !KnownPolicy(policy)) return false;
            int nodes = 0;
            try
            {
                if (policy == Policy.Raw) return Valid(value, new HashSet<object>(), 0, ref nodes);
                return ValidSnapshot(value, policy, RootPath(policy), new HashSet<object>(), 0, ref nodes) &&
                    (policy == Policy.TypedOwner || SnapshotShape(value, policy));
            }
            catch (EncoderFallbackException) { return false; }
        }

        internal static bool KnownPolicy(Policy policy) => (int)policy >= 0 && (int)policy <= (int)Policy.DiagnosticWorld;
        internal static Policy SnapshotPolicy(bool components, bool world) => components
            ? (world ? Policy.DiagnosticWorld : Policy.DiagnosticRun)
            : (world ? Policy.OrdinaryWorld : Policy.OrdinaryRun);
        static bool World(Policy policy) => policy == Policy.OrdinaryWorld || policy == Policy.DiagnosticWorld;
        static bool Diagnostic(Policy policy) => policy == Policy.DiagnosticRun || policy == Policy.DiagnosticWorld;

        // Fixed structural states are shared with the pre-parser. Array children always use Other.
        internal enum PathState { Other, Root, Home, Crafting, Paid, Owner }
        internal static PathState RootPath(Policy policy) => policy == Policy.Raw ? PathState.Other
            : policy == Policy.TypedOwner ? PathState.Owner : PathState.Root;
        internal static PathState ChildPath(Policy policy, PathState parent, string key)
        {
            switch (parent)
            {
                case PathState.Root:
                    if (World(policy) && key == "home_ship") return PathState.Home;
                    if (!World(policy) && key == "crafting_summary") return PathState.Crafting;
                    if (Diagnostic(policy) && key == "component_domain") return PathState.Owner;
                    break;
                case PathState.Home:
                    if (key == "crafting_summary") return PathState.Crafting;
                    if (Diagnostic(policy) && key == "component_domain") return PathState.Owner;
                    break;
                case PathState.Crafting:
                    if (key == "paid_craft") return PathState.Paid;
                    break;
                case PathState.Paid:
                    if (key == "domain") return PathState.Owner;
                    break;
            }
            return PathState.Other;
        }

        static bool ValidSnapshot(object value, Policy policy, PathState path, HashSet<object> ancestors, int depth, ref int nodes)
        {
            // Each eligible envelope root is one raw value. Its physical descendants have a separate bound.
            if (depth > MaxRawDepth || ++nodes > MaxNodes) return false;
            if (path == PathState.Owner)
            {
                int wireNodes = 0;
                if (!ValidOwnerWire(value, ancestors, 0, ref wireNodes) || !(value is GdDict envelope) ||
                    !ComponentDomainCodec.TryDecode(envelope, out GdDict owner, out _)) return false;
                if (policy == Policy.TypedOwner) return true;
                return owner.Get("schema_version") is long version && version == 3L &&
                    owner.GetString("domain_mode") == (Diagnostic(policy) ? "components_and_craft" : "craft_only") &&
                    DomainBundle.TryCreate(owner, out _, out _);
            }
            if (value is GdDict dict)
            {
                if (!ancestors.Add(dict)) return false;
                foreach (var pair in dict)
                {
                    if (!(pair.Key is string key)) return false;
                    Utf8.GetByteCount(key);
                    if (!ValidSnapshot(pair.Value, policy, ChildPath(policy, path, key), ancestors, depth + 1, ref nodes)) return false;
                }
                ancestors.Remove(dict); return true;
            }
            if (value is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                foreach (object item in array)
                    if (!ValidSnapshot(item, policy, PathState.Other, ancestors, depth + 1, ref nodes)) return false;
                ancestors.Remove(array); return true;
            }
            if (value is string text) { Utf8.GetByteCount(text); return true; }
            return value == null || value is bool || value is long || value is double real && !double.IsNaN(real) && !double.IsInfinity(real);
        }

        static bool ValidOwnerWire(object value, HashSet<object> ancestors, int depth, ref int nodes)
        {
            // Bound every physical node before the Core codec's recursive safety/semantic validation.
            if (depth > MaxOwnerWireDepth || ++nodes > MaxOwnerWireNodes) return false;
            if (value is GdDict dict)
            {
                if (!ancestors.Add(dict)) return false;
                foreach (var pair in dict)
                {
                    if (!(pair.Key is string key)) return false;
                    Utf8.GetByteCount(key);
                    if (!ValidOwnerWire(pair.Value, ancestors, depth + 1, ref nodes)) return false;
                }
                ancestors.Remove(dict); return true;
            }
            if (value is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                foreach (object item in array) if (!ValidOwnerWire(item, ancestors, depth + 1, ref nodes)) return false;
                ancestors.Remove(array); return true;
            }
            if (value is string text) { Utf8.GetByteCount(text); return true; }
            return value == null || value is bool || value is long || value is double real && !double.IsNaN(real) && !double.IsInfinity(real);
        }

        static bool SnapshotShape(GdDict snapshot, Policy policy)
        {
            bool diagnostic = Diagnostic(policy), world = World(policy);
            string runVersion = diagnostic ? RunSnapshot.ComponentIntegrationVersion : SaveLoadService.CURRENT_SLICE_VERSION;
            string version = world ? (diagnostic ? WorldSnapshot.ComponentIntegrationVersion : WorldSnapshot.WorldSliceVersion) : runVersion;
            if (snapshot.GetString("slice_version") != version) return false;
            GdDict home = world ? snapshot.GetDictOrEmpty("home_ship") : snapshot;
            if (home.GetString("slice_version") != runVersion) return false;
            GdDict envelope = home.GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft");
            if (envelope.Count != 3 || !(envelope.Get("schema_version") is long schema) || schema != 1L ||
                envelope.GetString("save_mode") != (diagnostic ? DiagnosticMode : OrdinaryMode) ||
                !(envelope.Get("domain") is GdDict owner)) return false;
            if (diagnostic)
                return Same(owner, snapshot.Get("component_domain")) && (!world || Same(owner, home.Get("component_domain")));
            return !snapshot.Has("component_domain") && (!world || !home.Has("component_domain"));
        }

        static bool Valid(object value, HashSet<object> ancestors, int depth, ref int nodes)
        {
            if (depth > 128 || ++nodes > MaxNodes) return false;
            if (value is GdDict dict)
            {
                if (!ancestors.Add(dict)) return false;
                foreach (var pair in dict)
                {
                    if (!(pair.Key is string key)) return false;
                    Utf8.GetByteCount(key);
                    if (!Valid(pair.Value, ancestors, depth + 1, ref nodes)) return false;
                }
                ancestors.Remove(dict); return true;
            }
            if (value is GdArray array)
            {
                if (!ancestors.Add(array)) return false;
                foreach (object item in array) if (!Valid(item, ancestors, depth + 1, ref nodes)) return false;
                ancestors.Remove(array); return true;
            }
            if (value is string text) { Utf8.GetByteCount(text); return true; }
            return value == null || value is bool || value is long || value is double real && !double.IsNaN(real) && !double.IsInfinity(real);
        }

        static bool Diagnose(object value, out string diagnostic)
        {
            int nodes = 0;
            return Valid(value, new HashSet<object>(), 0, ref nodes, "$", out diagnostic);
        }

        static bool Reject(object value, string path, string reason, out string diagnostic)
        {
            diagnostic = path + ":" + (value?.GetType().FullName ?? "null") + ":" + reason;
            return false;
        }

        static bool Valid(object value, HashSet<object> ancestors, int depth, ref int nodes, string path, out string diagnostic)
        {
            diagnostic = "";
            if (depth > 128) return Reject(value, path, "depth", out diagnostic);
            if (++nodes > MaxNodes) return Reject(value, path, "node_limit", out diagnostic);
            if (value is GdDict dict)
            {
                if (!ancestors.Add(dict)) return Reject(value, path, "cycle", out diagnostic);
                foreach (var pair in dict)
                {
                    if (!(pair.Key is string key)) return Reject(pair.Key, path + "[<key>]", "key_type", out diagnostic);
                    try { Utf8.GetByteCount(key); }
                    catch (EncoderFallbackException) { return Reject(key, path + "[<key>]", "unicode", out diagnostic); }
                    if (!Valid(pair.Value, ancestors, depth + 1, ref nodes, path + "[" + GdJson.Stringify(key) + "]", out diagnostic)) return false;
                }
                ancestors.Remove(dict); return true;
            }
            if (value is GdArray array)
            {
                if (!ancestors.Add(array)) return Reject(value, path, "cycle", out diagnostic);
                int index = 0;
                foreach (object item in array)
                    if (!Valid(item, ancestors, depth + 1, ref nodes, path + "[" + (index++).ToString(CultureInfo.InvariantCulture) + "]", out diagnostic)) return false;
                ancestors.Remove(array); return true;
            }
            if (value is string text)
            {
                try { Utf8.GetByteCount(text); return true; }
                catch (EncoderFallbackException) { return Reject(value, path, "unicode", out diagnostic); }
            }
            if (value is double real)
                return !double.IsNaN(real) && !double.IsInfinity(real) || Reject(value, path, "nonfinite", out diagnostic);
            return value == null || value is bool || value is long || Reject(value, path, "type", out diagnostic);
        }

        static void Write(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string text) { WriteString(output, text); return; }
            if (value is bool boolean) { output.Append(boolean ? "true" : "false"); return; }
            if (value is long integer) { output.Append(integer.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double real)
            {
                string token = real == 0.0 ? "0.0" : real.ToString("R", CultureInfo.InvariantCulture);
                if (token.IndexOf('.') < 0 && token.IndexOf('e') < 0 && token.IndexOf('E') < 0) token += ".0";
                output.Append(token); return;
            }
            bool first = true;
            if (value is GdDict dict)
            {
                output.Append('{');
                foreach (var pair in dict)
                {
                    if (!first) output.Append(','); first = false;
                    WriteString(output, (string)pair.Key); output.Append(':'); Write(output, pair.Value);
                }
                output.Append('}'); return;
            }
            output.Append('[');
            foreach (object item in (GdArray)value)
            { if (!first) output.Append(','); first = false; Write(output, item); }
            output.Append(']');
        }

        static void WriteString(StringBuilder output, string text)
        {
            const string hex = "0123456789abcdef";
            output.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\t': output.Append("\\t"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\r': output.Append("\\r"); break;
                    default:
                        if (c < 32) output.Append("\\u00").Append(hex[c >> 4]).Append(hex[c & 15]);
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }

        public static GdDict Envelope(GdDict domain, bool components) => new GdDict
        {
            { "schema_version", 1L }, { "save_mode", components ? DiagnosticMode : OrdinaryMode },
            { "domain", ComponentDomainCodec.Encode(domain) }
        };

        internal static bool Same(object owned, object wire)
        {
            if (owned is long integer) return wire is long otherInteger && integer == otherInteger;
            if (owned is double real) return wire is double otherReal && !double.IsNaN(real) && !double.IsInfinity(real) && real == otherReal;
            if (owned is GdDict dict)
            {
                if (!(wire is GdDict other) || dict.Count != other.Count) return false;
                foreach (var pair in dict) if (!other.Has(pair.Key) || !Same(pair.Value, other.Get(pair.Key))) return false;
                return true;
            }
            if (owned is GdArray array)
            {
                if (!(wire is GdArray other) || array.Count != other.Count) return false;
                for (int i = 0; i < array.Count; i++) if (!Same(array[i], other[i])) return false;
                return true;
            }
            return owned == null ? wire == null : owned.GetType() == wire?.GetType() && owned.Equals(wire);
        }
    }
}
