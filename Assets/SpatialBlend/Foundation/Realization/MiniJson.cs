// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// A JSON object that keeps its members in file order and keeps `null` as a value. The embodiment spec needs both
    /// (an anchor's `bone` is `null` for the actor root; the spec is passed to the core as written), and Unity's
    /// JsonUtility offers neither. This project has no JSON package (Packages/manifest.json: only jsonserialize), and
    /// this adds none: it is the small subset the embodiment files use.
    /// Values are: <see cref="MiniJsonObject"/>, <c>List&lt;object&gt;</c>, <see cref="string"/>, <see cref="double"/>,
    /// <see cref="bool"/> and <c>null</c>.
    /// </summary>
    internal sealed class MiniJsonObject
    {
        internal readonly List<KeyValuePair<string, object>> Members = new List<KeyValuePair<string, object>>();

        internal bool TryGet(string key, out object value)
        {
            for (int i = 0; i < Members.Count; ++i)
            {
                if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
                {
                    value = Members[i].Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        /// <summary>The member's value, or null when it is absent (or null).</summary>
        internal object Get(string key) => TryGet(key, out object value) ? value : null;

        /// <summary>Replaces the member when it exists (keeping its place), else appends it.</summary>
        internal void Set(string key, object value)
        {
            for (int i = 0; i < Members.Count; ++i)
            {
                if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
                {
                    Members[i] = new KeyValuePair<string, object>(key, value);
                    return;
                }
            }
            Members.Add(new KeyValuePair<string, object>(key, value));
        }
    }

    internal static class MiniJson
    {
        /// <summary>Parses a complete JSON document. False, with the position of the fault, for anything else.</summary>
        internal static bool TryParse(string text, out object value, out string error)
        {
            value = null;
            error = null;
            if (text == null)
            {
                error = "no text";
                return false;
            }
            var parser = new Parser(text);
            try
            {
                parser.SkipWhitespace();
                value = parser.ReadValue(0);
                parser.SkipWhitespace();
                if (!parser.AtEnd) throw parser.Fault("trailing characters after the JSON value");
                return true;
            }
            catch (FormatException exception)
            {
                value = null;
                error = exception.Message;
                return false;
            }
        }

        /// <summary>Compact JSON. Numbers are written so that they read back to the same double.</summary>
        internal static string Write(object value)
        {
            var sb = new StringBuilder();
            Append(sb, value);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    AppendString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("JSON has no NaN or infinity");
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case MiniJsonObject o:
                    sb.Append('{');
                    for (int k = 0; k < o.Members.Count; ++k)
                    {
                        if (k > 0) sb.Append(',');
                        AppendString(sb, o.Members[k].Key);
                        sb.Append(':');
                        Append(sb, o.Members[k].Value);
                    }
                    sb.Append('}');
                    break;
                case List<object> a:
                    sb.Append('[');
                    for (int k = 0; k < a.Count; ++k)
                    {
                        if (k > 0) sb.Append(',');
                        Append(sb, a[k]);
                    }
                    sb.Append(']');
                    break;
                default:
                    throw new ArgumentException("unsupported JSON value type " + value.GetType().Name);
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            // Documents are small (the embodiment files); the bound only keeps a hostile one from exhausting the stack.
            private const int MaxDepth = 64;
            private readonly string _text;
            private int _pos;

            internal Parser(string text) { _text = text; }

            internal bool AtEnd => _pos >= _text.Length;

            internal FormatException Fault(string what) => new FormatException(what + " at offset " + _pos);

            internal void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') ++_pos;
                    else break;
                }
            }

            internal object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Fault("nesting too deep");
                if (AtEnd) throw Fault("unexpected end");
                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw Fault("expected '" + word + "'");
                _pos += word.Length;
            }

            private MiniJsonObject ReadObject(int depth)
            {
                var result = new MiniJsonObject();
                ++_pos; // {
                SkipWhitespace();
                if (!AtEnd && _text[_pos] == '}') { ++_pos; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != '"') throw Fault("expected a member name");
                    string key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != ':') throw Fault("expected ':'");
                    ++_pos;
                    SkipWhitespace();
                    object value = ReadValue(depth + 1);
                    if (result.TryGet(key, out _)) throw Fault("duplicate member '" + key + "'");
                    result.Members.Add(new KeyValuePair<string, object>(key, value));
                    SkipWhitespace();
                    if (AtEnd) throw Fault("unexpected end in an object");
                    if (_text[_pos] == ',') { ++_pos; continue; }
                    if (_text[_pos] == '}') { ++_pos; return result; }
                    throw Fault("expected ',' or '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var result = new List<object>();
                ++_pos; // [
                SkipWhitespace();
                if (!AtEnd && _text[_pos] == ']') { ++_pos; return result; }
                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw Fault("unexpected end in an array");
                    if (_text[_pos] == ',') { ++_pos; continue; }
                    if (_text[_pos] == ']') { ++_pos; return result; }
                    throw Fault("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                ++_pos; // opening quote
                while (true)
                {
                    if (AtEnd) throw Fault("unterminated string");
                    char c = _text[_pos++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw Fault("control character in a string");
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Fault("unterminated escape");
                    char e = _text[_pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) throw Fault("short \\u escape");
                            if (!int.TryParse(_text.Substring(_pos, 4), NumberStyles.AllowHexSpecifier,
                                    CultureInfo.InvariantCulture, out int code))
                                throw Fault("bad \\u escape");
                            sb.Append((char)code);
                            _pos += 4;
                            break;
                        default: throw Fault("bad escape '\\" + e + "'");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _pos;
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') ++_pos;
                    else break;
                }
                if (_pos == start) throw Fault("unexpected character '" + _text[_pos] + "'");
                if (!double.TryParse(_text.Substring(start, _pos - start),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw Fault("bad number");
                }
                return number;
            }
        }
    }
}
