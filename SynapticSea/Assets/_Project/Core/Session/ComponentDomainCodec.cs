using System;
using System.Globalization;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Versioned owned codec. Structurally tagged nodes preserve Int64 and metadata without Godot JSON coercion.</summary>
    public static class ComponentDomainCodec
    {
        public const string LegacySchema = "component_domain_codec_v1";
        public const string BitExactSchema = "component_domain_codec_v2";
        const int MaxDepth = 128;
        const int MaxNodes = 100000;

        public static GdDict Encode(GdDict summary) => Encode(summary, LegacySchema);

        public static GdDict Encode(GdDict summary, string schema)
        {
            if (schema != LegacySchema && schema != BitExactSchema) throw new ArgumentException("Unsupported component codec version.");
            if (summary == null || !ItemInstanceState.IsSafeSnapshot(summary)) throw new ArgumentException("Unsafe component snapshot.");
            int nodes = 0;
            return new GdDict { { "schema", schema }, { "value", EncodeNode(summary, 0, ref nodes, schema == BitExactSchema) } };
        }

        public static bool TryDecode(GdDict envelope, out GdDict summary, out string reason)
        {
            summary = null; reason = "invalid_component_codec";
            if (envelope == null || envelope.Count != 2 || (envelope.GetString("schema") != LegacySchema && envelope.GetString("schema") != BitExactSchema) || !ItemInstanceState.IsSafeSnapshot(envelope)) return false;
            try
            {
                int nodes = 0;
                object decoded = DecodeNode(envelope.Get("value"), 0, ref nodes, envelope.GetString("schema") == BitExactSchema);
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

        static GdArray EncodeNode(object value, int depth, ref int nodes, bool bitExact)
        {
            Bound(depth, ref nodes);
            if (value == null) return GdArray.Of("null");
            if (value is string text)
            {
                if (bitExact) new System.Text.UTF8Encoding(false, true).GetByteCount(text);
                return GdArray.Of("text", text);
            }
            if (value is bool boolean) return GdArray.Of("bool", boolean);
            if (value is long integer) return GdArray.Of("integer", integer.ToString(CultureInfo.InvariantCulture));
            if (value is int small) return GdArray.Of("integer", small.ToString(CultureInfo.InvariantCulture));
            if (value is double real) return GdArray.Of("real", RealText(real, bitExact));
            if (value is float single) return GdArray.Of("real", RealText(single, bitExact));
            if (value is Vec3 vector) return GdArray.Of("vector3", RealText(vector.X, bitExact), RealText(vector.Y, bitExact), RealText(vector.Z, bitExact));
            if (value is GdArray array)
            {
                var children = new GdArray();
                foreach (object child in array) children.Add(EncodeNode(child, depth + 1, ref nodes, bitExact));
                return GdArray.Of("array", children);
            }
            if (value is GdDict dictionary)
            {
                var entries = new GdArray();
                foreach (var entry in dictionary)
                    entries.Add(GdArray.Of(EncodeNode(entry.Key, depth + 1, ref nodes, bitExact), EncodeNode(entry.Value, depth + 1, ref nodes, bitExact)));
                return GdArray.Of("dictionary", entries);
            }
            throw new ArgumentException("Unsupported component codec value.");
        }

        static string RealText(double value, bool bitExact = false)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Nonfinite component value.");
            return bitExact ? unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
        }

        static double Real(object value, bool bitExact = false)
        {
            if (bitExact)
            {
                if (!(value is string bits) || bits.Length != 16) throw new ArgumentException("Invalid component real bits.");
                foreach (char digit in bits) if (!(digit >= '0' && digit <= '9' || digit >= 'a' && digit <= 'f')) throw new ArgumentException("Invalid component real bits.");
                if (!ulong.TryParse(bits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong encoded)) throw new ArgumentException("Invalid component real bits.");
                double exact = BitConverter.Int64BitsToDouble(unchecked((long)encoded));
                if (double.IsNaN(exact) || double.IsInfinity(exact)) throw new ArgumentException("Nonfinite component real bits.");
                return exact;
            }
            if (!(value is string text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                double.IsNaN(number) || double.IsInfinity(number) || RealText(number) != text) throw new ArgumentException("Invalid component real.");
            return number;
        }

        static double VectorReal(object value, bool bitExact)
        {
            double number = Real(value, bitExact);
            if (bitExact && BitConverter.DoubleToInt64Bits(number) != BitConverter.DoubleToInt64Bits((double)(float)number))
                throw new ArgumentException("Noncanonical component vector precision.");
            return number;
        }

        static object DecodeNode(object value, int depth, ref int nodes, bool bitExact)
        {
            Bound(depth, ref nodes);
            if (!(value is GdArray node) || node.Count < 1 || !(node[0] is string kind)) throw new ArgumentException("Invalid node.");
            if (kind == "null" && node.Count == 1) return null;
            if (kind == "text" && node.Count == 2 && node[1] is string text)
            { if (bitExact) new System.Text.UTF8Encoding(false, true).GetByteCount(text); return text; }
            if (kind == "bool" && node.Count == 2 && node[1] is bool) return node[1];
            if (kind == "integer" && node.Count == 2 && node[1] is string digits &&
                long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer) &&
                integer.ToString(CultureInfo.InvariantCulture) == digits) return integer;
            if (kind == "real" && node.Count == 2) return Real(node[1], bitExact);
            if (kind == "vector3" && node.Count == 4)
            {
                var vector = new Vec3(VectorReal(node[1], bitExact), VectorReal(node[2], bitExact), VectorReal(node[3], bitExact));
                if (float.IsInfinity(vector.X) || float.IsInfinity(vector.Y) || float.IsInfinity(vector.Z)) throw new ArgumentException("Vector overflow.");
                return vector;
            }
            if (node.Count == 2 && node[1] is GdArray children)
            {
                if (kind == "array")
                {
                    var array = new GdArray();
                    foreach (object child in children) array.Add(DecodeNode(child, depth + 1, ref nodes, bitExact));
                    return array;
                }
                if (kind == "dictionary")
                {
                    var dictionary = new GdDict();
                    foreach (object child in children)
                    {
                        if (!(child is GdArray pair) || pair.Count != 2) throw new ArgumentException("Invalid dictionary entry.");
                        object key = DecodeNode(pair[0], depth + 1, ref nodes, bitExact);
                        if (key == null || key is GdArray || key is GdDict || dictionary.Has(key)) throw new ArgumentException("Invalid or duplicate dictionary key.");
                        dictionary[key] = DecodeNode(pair[1], depth + 1, ref nodes, bitExact);
                    }
                    return dictionary;
                }
            }
            throw new ArgumentException("Invalid component node kind or shape.");
        }
    }
}
