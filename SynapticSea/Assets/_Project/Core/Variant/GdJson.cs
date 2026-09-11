using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Godot 4.7 <c>JSON.parse_string</c> / <c>JSON.stringify</c> equivalents (port of <c>core/io/json.cpp</c>).
    /// Parsing: every number becomes a <c>double</c> (Godot never yields ints from JSON) using Godot's strtod;
    /// objects keep key insertion order; a repeated key updates the value in place.
    /// Writing: optional indent string, recursive key sort by Godot's string-like ordering (default on),
    /// Godot float formatting, minimal escaping.
    /// </summary>
    public static class GdJson
    {
        // ================================================================== stringify

        /// <summary><c>JSON.stringify(value, indent, sort_keys = true)</c>.</summary>
        public static string Stringify(object value, string indent = "", bool sortKeys = true)
        {
            var sb = new StringBuilder();
            Write(sb, V.Normalize(value), indent ?? string.Empty, 0, sortKeys);
            return sb.ToString();
        }

        static void AddIndent(StringBuilder sb, string indent, int count)
        {
            for (int i = 0; i < count; i++) sb.Append(indent);
        }

        static void Write(StringBuilder sb, object v, string indent, int cur, bool sortKeys)
        {
            string colon = indent.Length == 0 ? ":" : ": ";
            string endStatement = indent.Length == 0 ? "" : "\n";

            switch (v)
            {
                case null:
                    sb.Append("null");
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(GdFloatFormat.JsonNumber(d));
                    return;
                case GdArray a:
                    {
                        if (a.IsEmpty)
                        {
                            sb.Append("[]");
                            return;
                        }
                        sb.Append('[').Append(endStatement);
                        bool first = true;
                        foreach (var item in a)
                        {
                            if (first) first = false;
                            else sb.Append(',').Append(endStatement);
                            AddIndent(sb, indent, cur + 1);
                            Write(sb, item, indent, cur + 1, sortKeys);
                        }
                        sb.Append(endStatement);
                        AddIndent(sb, indent, cur);
                        sb.Append(']');
                        return;
                    }
                case GdDict d:
                    {
                        if (d.IsEmpty)
                        {
                            sb.Append("{}");
                            return;
                        }
                        sb.Append('{').Append(endStatement);
                        var keys = new List<object>(d.Keys);
                        if (sortKeys) GdSort.SortCustom(keys, StringLikeLess);
                        bool firstKey = true;
                        foreach (var key in keys)
                        {
                            if (firstKey) firstKey = false;
                            else sb.Append(',').Append(endStatement);
                            AddIndent(sb, indent, cur + 1);
                            WriteString(sb, KeyToString(key));
                            sb.Append(colon);
                            Write(sb, d[key], indent, cur + 1, sortKeys);
                        }
                        sb.Append(endStatement);
                        AddIndent(sb, indent, cur);
                        sb.Append('}');
                        return;
                    }
                case string s:
                    WriteString(sb, s);
                    return;
                default:
                    WriteString(sb, v.ToString());
                    return;
            }
        }

        /// <summary><c>String(key)</c> for a dictionary key.</summary>
        static string KeyToString(object key) => key is string s ? s : V.Str(key);

        /// <summary>Godot <c>StringLikeVariantOrder</c>: string keys by code point, otherwise Variant &lt;.</summary>
        public static bool StringLikeLess(object a, object b)
        {
            if (a is string sa && b is string sb) return V.CompareCodePoints(sa, sb) < 0;
            return V.VariantLess(a, b);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\v': sb.Append("\\v"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        // ================================================================== parse

        public sealed class ParseError : Exception
        {
            public readonly int Line;
            public ParseError(string message, int line) : base($"JSON parse error at line {line}: {message}") => Line = line;
        }

        /// <summary><c>JSON.parse_string(text)</c>: returns null on failure (like Godot).</summary>
        public static object ParseString(string text)
        {
            try
            {
                return Parse(text);
            }
            catch (ParseError)
            {
                return null;
            }
        }

        /// <summary>Parses JSON text; throws <see cref="ParseError"/> on malformed input.</summary>
        public static object Parse(string text) => Parse(text, integersAsLong: false);

        /// <summary>
        /// Parses JSON text. <paramref name="integersAsLong"/> = true keeps integer literals (no '.', no exponent)
        /// as exact <c>long</c> values. That is NOT Godot behaviour; use it only for reading test fixtures that
        /// carry 64-bit integers (RNG states, hashes). Game data must use the default Godot-faithful parse.
        /// </summary>
        public static object Parse(string text, bool integersAsLong)
        {
            var p = new Parser(text ?? string.Empty) { IntegersAsLong = integersAsLong };
            return p.ParseDocument();
        }

        /// <summary>Parses and requires a top-level object; returns null otherwise.</summary>
        public static GdDict ParseDict(string text) => ParseString(text) as GdDict;

        sealed class Parser
        {
            readonly string _s;
            int _i;
            int _line = 1;
            public bool IntegersAsLong;

            public Parser(string s)
            {
                _s = s;
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;
            }

            char Cur => _i < _s.Length ? _s[_i] : '\0';

            void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '\n') _line++;
                    if (c <= ' ') _i++;
                    else break;
                }
            }

            public object ParseDocument()
            {
                SkipWhitespace();
                object v = ParseValue(0);
                SkipWhitespace();
                if (_i < _s.Length) throw new ParseError("Expected 'EOF'", _line);
                return v;
            }

            object ParseValue(int depth)
            {
                if (depth > 1024) throw new ParseError("JSON structure is too deep", _line);
                SkipWhitespace();
                char c = Cur;
                switch (c)
                {
                    case '{': _i++; return ParseObject(depth + 1);
                    case '[': _i++; return ParseArray(depth + 1);
                    case '"': _i++; return ParseStringBody();
                    case '\0': throw new ParseError("Expected value, got 'EOF'", _line);
                }
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    double number = GodotStrtod.Parse(_s, _i, out int end);
                    if (end == _i) throw new ParseError("Invalid number", _line);
                    string lexeme = _s.Substring(_i, end - _i);
                    _i = end;
                    if (IntegersAsLong && lexeme.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 &&
                        long.TryParse(lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long exact))
                        return exact;
                    return number;
                }
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    int start = _i;
                    while (_i < _s.Length && ((_s[_i] >= 'a' && _s[_i] <= 'z') || (_s[_i] >= 'A' && _s[_i] <= 'Z'))) _i++;
                    string id = _s.Substring(start, _i - start);
                    switch (id)
                    {
                        case "true": return true;
                        case "false": return false;
                        case "null": return null;
                        default: throw new ParseError($"Expected 'true', 'false', or 'null', got '{id}'", _line);
                    }
                }
                throw new ParseError($"Unexpected character '{c}'", _line);
            }

            GdArray ParseArray(int depth)
            {
                var arr = new GdArray();
                bool needComma = false;
                while (true)
                {
                    SkipWhitespace();
                    char c = Cur;
                    if (c == '\0') throw new ParseError("Expected ']'", _line);
                    if (c == ']')
                    {
                        _i++;
                        return arr;
                    }
                    if (needComma)
                    {
                        if (c != ',') throw new ParseError("Expected ','", _line);
                        _i++;
                        needComma = false;
                        continue;
                    }
                    arr.Add(ParseValue(depth));
                    needComma = true;
                }
            }

            GdDict ParseObject(int depth)
            {
                var dict = new GdDict();
                bool needComma = false;
                while (true)
                {
                    SkipWhitespace();
                    char c = Cur;
                    if (c == '\0') throw new ParseError("Expected '}'", _line);
                    if (c == '}')
                    {
                        _i++;
                        return dict;
                    }
                    if (needComma)
                    {
                        if (c != ',') throw new ParseError("Expected '}' or ','", _line);
                        _i++;
                        needComma = false;
                        continue;
                    }
                    if (c != '"') throw new ParseError("Expected key", _line);
                    _i++;
                    string key = ParseStringBody();
                    SkipWhitespace();
                    if (Cur != ':') throw new ParseError("Expected ':'", _line);
                    _i++;
                    object value = ParseValue(depth);
                    dict.Set(key, value);
                    needComma = true;
                }
            }

            string ParseStringBody()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new ParseError("Unterminated string", _line);
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new ParseError("Unterminated string", _line);
                        char e = _s[_i++];
                        switch (e)
                        {
                            case 'b': sb.Append('\b'); break;
                            case 't': sb.Append('\t'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'r': sb.Append('\r'); break;
                            case 'u':
                                {
                                    int code = ReadHex4();
                                    if (code >= 0xD800 && code <= 0xDBFF && _i + 1 < _s.Length && _s[_i] == '\\' && _s[_i + 1] == 'u')
                                    {
                                        _i += 2;
                                        int low = ReadHex4();
                                        sb.Append((char)code).Append((char)low);
                                    }
                                    else
                                    {
                                        sb.Append((char)code);
                                    }
                                    break;
                                }
                            case '"':
                            case '\\':
                            case '/':
                                sb.Append(e);
                                break;
                            default:
                                throw new ParseError($"Invalid escape sequence '\\{e}'", _line);
                        }
                    }
                    else
                    {
                        if (c == '\n') _line++;
                        sb.Append(c);
                    }
                }
            }

            int ReadHex4()
            {
                if (_i + 4 > _s.Length) throw new ParseError("Unterminated \\u escape", _line);
                int v = 0;
                for (int k = 0; k < 4; k++)
                {
                    char h = _s[_i++];
                    int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                    if (d < 0) throw new ParseError("Malformed hex constant in string", _line);
                    v = v * 16 + d;
                }
                return v;
            }
        }
    }
}
