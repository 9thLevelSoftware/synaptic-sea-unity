using System;
using System.Globalization;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Versioned owned codec. Structurally tagged nodes preserve Int64 and metadata without Godot JSON coercion.</summary>
    public static class ComponentDomainCodec
    {
        const string Schema = "component_domain_codec_v1";
        const int MaxDepth = 128;
        const int MaxNodes = 100000;

        public static GdDict Encode(GdDict summary)
        {
            if (summary == null || !ItemInstanceState.IsSafeSnapshot(summary)) throw new ArgumentException("Unsafe component snapshot.");
            int nodes = 0;
            return new GdDict { { "schema", Schema }, { "value", EncodeNode(summary, 0, ref nodes) } };
        }

        public static bool TryDecode(GdDict envelope, out GdDict summary, out string reason)
        {
            summary = null; reason = "invalid_component_codec";
            if (envelope == null || envelope.Count != 2 || envelope.GetString("schema") != Schema || !ItemInstanceState.IsSafeSnapshot(envelope)) return false;
            try
            {
                int nodes = 0;
                object decoded = DecodeNode(envelope.Get("value"), 0, ref nodes);
                if (!(decoded is GdDict dictionary)) return false;
                summary = dictionary; reason = "ok"; return true;
            }
            catch (ArgumentException) { return false; }
            catch (OverflowException) { return false; }
        }

        static void Bound(int depth, ref int nodes)
        {
            if (depth > MaxDepth || ++nodes > MaxNodes) throw new ArgumentException("Component codec bound exceeded.");
        }

        static GdArray EncodeNode(object value, int depth, ref int nodes)
        {
            Bound(depth, ref nodes);
            if (value == null) return GdArray.Of("null");
            if (value is string text) return GdArray.Of("text", text);
            if (value is bool boolean) return GdArray.Of("bool", boolean);
            if (value is long integer) return GdArray.Of("integer", integer.ToString(CultureInfo.InvariantCulture));
            if (value is int small) return GdArray.Of("integer", small.ToString(CultureInfo.InvariantCulture));
            if (value is double real) return GdArray.Of("real", RealText(real));
            if (value is float single) return GdArray.Of("real", RealText(single));
            if (value is Vec3 vector) return GdArray.Of("vector3", RealText(vector.X), RealText(vector.Y), RealText(vector.Z));
            if (value is GdArray array)
            {
                var children = new GdArray();
                foreach (object child in array) children.Add(EncodeNode(child, depth + 1, ref nodes));
                return GdArray.Of("array", children);
            }
            if (value is GdDict dictionary)
            {
                var entries = new GdArray();
                foreach (var entry in dictionary)
                    entries.Add(GdArray.Of(EncodeNode(entry.Key, depth + 1, ref nodes), EncodeNode(entry.Value, depth + 1, ref nodes)));
                return GdArray.Of("dictionary", entries);
            }
            throw new ArgumentException("Unsupported component codec value.");
        }

        static string RealText(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Nonfinite component value.");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static double Real(object value)
        {
            if (!(value is string text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                double.IsNaN(number) || double.IsInfinity(number) || RealText(number) != text) throw new ArgumentException("Invalid component real.");
            return number;
        }

        static object DecodeNode(object value, int depth, ref int nodes)
        {
            Bound(depth, ref nodes);
            if (!(value is GdArray node) || node.Count < 1 || !(node[0] is string kind)) throw new ArgumentException("Invalid node.");
            if (kind == "null" && node.Count == 1) return null;
            if (kind == "text" && node.Count == 2 && node[1] is string) return node[1];
            if (kind == "bool" && node.Count == 2 && node[1] is bool) return node[1];
            if (kind == "integer" && node.Count == 2 && node[1] is string digits &&
                long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer) &&
                integer.ToString(CultureInfo.InvariantCulture) == digits) return integer;
            if (kind == "real" && node.Count == 2) return Real(node[1]);
            if (kind == "vector3" && node.Count == 4)
            {
                var vector = new Vec3(Real(node[1]), Real(node[2]), Real(node[3]));
                if (float.IsInfinity(vector.X) || float.IsInfinity(vector.Y) || float.IsInfinity(vector.Z)) throw new ArgumentException("Vector overflow.");
                return vector;
            }
            if (node.Count == 2 && node[1] is GdArray children)
            {
                if (kind == "array")
                {
                    var array = new GdArray();
                    foreach (object child in children) array.Add(DecodeNode(child, depth + 1, ref nodes));
                    return array;
                }
                if (kind == "dictionary")
                {
                    var dictionary = new GdDict();
                    foreach (object child in children)
                    {
                        if (!(child is GdArray pair) || pair.Count != 2) throw new ArgumentException("Invalid dictionary entry.");
                        object key = DecodeNode(pair[0], depth + 1, ref nodes);
                        if (key == null || key is GdArray || key is GdDict || dictionary.Has(key)) throw new ArgumentException("Invalid or duplicate dictionary key.");
                        dictionary[key] = DecodeNode(pair[1], depth + 1, ref nodes);
                    }
                    return dictionary;
                }
            }
            throw new ArgumentException("Invalid component node kind or shape.");
        }
    }
}
