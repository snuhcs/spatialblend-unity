// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Strict, dependency-free decoder for the recorded AnchorPoseFrame
    /// wire DTO.</summary>
    public static class AnchorPoseJson
    {
        internal sealed class JsonValue
        {
            internal Dictionary<string, JsonValue> Object;
            internal List<JsonValue> Array;
            internal string String;
            internal string RawNumber;
            internal double? Number;
            internal bool? Boolean;
        }

        public static bool TryDecode(string json, out AnchorPoseFrame frame, out string error)
        {
            frame = null;
            if (!TryParse(json, out JsonValue root, out error) || root.Object == null)
                return Fail("Frame must be a JSON object.", out frame, out error);
            if (!Required(root, "schema", out JsonValue schema) ||
                !Int32(schema, out int version) ||
                !Required(root, "frame", out JsonValue coordinate) || coordinate.String == null ||
                !Required(root, "sequence", out JsonValue sequence) ||
                !Int64(sequence, out long seq) || seq < 0 ||
                !Required(root, "timeSeconds", out JsonValue time) ||
                !Number(time, out double seconds) || !Finite(seconds) || seconds < 0 ||
                !Required(root, "anchors", out JsonValue anchors) || anchors.Array == null)
                return Fail("Frame requires typed schema, frame, nonnegative Int64 " +
                                "sequence, finite timeSeconds, and anchors.",
                            out frame, out error);
            if (version != AnchorPoseFrame.CurrentSchema ||
                coordinate.String != AnchorPoseFrame.UnityWorldFrame)
                return Fail("Frame schema/frame does not match the Unity anchor-pose contract.",
                            out frame, out error);
            var samples = new AnchorPoseSample[anchors.Array.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < samples.Length; ++i)
            {
                JsonValue item = anchors.Array[i];
                if (item.Object == null || !Required(item, "anchorId", out JsonValue id) ||
                    id.String == null || !AnchorPoseFrameValidation.IsValidAnchorId(id.String) ||
                    !ids.Add(id.String) || !Vector(item, "worldPosition", out Vector3 position) ||
                    !Quaternion(item, "worldRotation", out Quaternion rotation))
                    return Fail("Each anchor requires unique anchorId and finite " +
                                    "worldPosition xyz/worldRotation xyzw.",
                                out frame, out error);
                float magnitude = rotation.x * rotation.x + rotation.y * rotation.y +
                                  rotation.z * rotation.z + rotation.w * rotation.w;
                if (!Finite(magnitude) || Math.Abs(magnitude - 1f) > .001f)
                    return Fail("Anchor rotations must be unit quaternions.", out frame, out error);
                samples[i] = new AnchorPoseSample { anchorId = id.String, worldPosition = position,
                                                    worldRotation = rotation };
            }
            frame =
                new AnchorPoseFrame { schema = version, frame = coordinate.String, sequence = seq,
                                      timeSeconds = seconds, anchors = samples };
            error = null;
            return true;
        }

        internal static bool TryParse(string json, out JsonValue value, out string error)
        {
            value = null;
            error = null;
            if (string.IsNullOrEmpty(json))
            {
                error = "JSON is empty.";
                return false;
            }
            var parser = new Parser(json);
            if (!parser.Value(out value, out error))
                return false;
            parser.White();
            if (!parser.End)
            {
                error = "Trailing JSON content.";
                return false;
            }
            return true;
        }
        internal static bool Required(JsonValue value, string name, out JsonValue child)
        {
            child = null;
            return value != null && value.Object != null &&
                   value.Object.TryGetValue(name, out child);
        }
        internal static bool Number(JsonValue value, out double number)
        {
            number = 0;
            return value != null && value.Number.HasValue && Finite(value.Number.Value) &&
                   (number = value.Number.Value) == value.Number.Value;
        }
        internal static bool Int64(JsonValue value, out long integer)
        {
            integer = 0;
            return value != null && value.RawNumber != null &&
                   long.TryParse(value.RawNumber, NumberStyles.AllowLeadingSign,
                                 CultureInfo.InvariantCulture, out integer);
        }
        internal static bool Int32(JsonValue value, out int integer)
        {
            integer = 0;
            return Int64(value, out long n) && n >= int.MinValue && n <= int.MaxValue &&
                   (integer = (int)n) == n;
        }
        internal static bool Finite(double value) => !double.IsNaN(value) &&
                                                     !double.IsInfinity(value);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Fail(string message, out AnchorPoseFrame frame, out string error)
        {
            frame = null;
            error = message;
            return false;
        }
        private static bool Vector(JsonValue parent, string name, out Vector3 vector)
        {
            vector = default;
            if (!Required(parent, name, out JsonValue v) || v.Object == null ||
                !Component(v, "x", out float x) || !Component(v, "y", out float y) ||
                !Component(v, "z", out float z))
                return false;
            vector = new Vector3(x, y, z);
            return true;
        }
        private static bool Quaternion(JsonValue parent, string name, out Quaternion quaternion)
        {
            quaternion = default;
            if (!Required(parent, name, out JsonValue v) || v.Object == null ||
                !Component(v, "x", out float x) || !Component(v, "y", out float y) ||
                !Component(v, "z", out float z) || !Component(v, "w", out float w))
                return false;
            quaternion = new Quaternion(x, y, z, w);
            return true;
        }
        private static bool Component(JsonValue value, string name, out float component)
        {
            component = 0;
            return Required(value, name, out JsonValue n) && Number(n, out double d) &&
                   d >= float.MinValue && d <= float.MaxValue && (component = (float)d) == (float)d;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _at;
            private int _depth;
            internal Parser(string text)
            {
                _text = text;
            }
            internal bool End => _at == _text.Length;
            internal void White()
            {
                while (_at < _text.Length && (_text[_at] == ' ' || _text[_at] == '\t' ||
                                              _text[_at] == '\r' || _text[_at] == '\n'))
                    ++_at;
            }
            internal bool Value(out JsonValue value, out string error)
            {
                value = null;
                error = null;
                White();
                if (_at >= _text.Length)
                {
                    error = "Unexpected end.";
                    return false;
                }
                char c = _text[_at];
                if (c == '{')
                    return Container(true, out value, out error);
                if (c == '[')
                    return Container(false, out value, out error);
                if (c == '\"')
                {
                    if (!String(out string s, out error))
                        return false;
                    value = new JsonValue { String = s };
                    return true;
                }
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int start = _at;
                    if (!Numeric(out double n, out error))
                        return false;
                    value = new JsonValue { Number = n,
                                            RawNumber = _text.Substring(start, _at - start) };
                    return true;
                }
                if (Match("true"))
                {
                    value = new JsonValue { Boolean = true };
                    return true;
                }
                if (Match("false"))
                {
                    value = new JsonValue { Boolean = false };
                    return true;
                }
                if (Match("null"))
                {
                    value = new JsonValue();
                    return true;
                }
                error = "Invalid JSON token.";
                return false;
            }
            private bool Container(bool isObject, out JsonValue value, out string error)
            {
                value = null;
                error = null;
                if (++_depth > 64)
                {
                    --_depth;
                    error = "JSON nesting exceeds 64 levels.";
                    return false;
                }
                try
                {
                    return isObject ? Object(out value, out error) : Array(out value, out error);
                }
                finally
                {
                    --_depth;
                }
            }
            private bool Object(out JsonValue value, out string error)
            {
                value = null;
                error = null;
                ++_at;
                var o = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                White();
                if (Take('}'))
                {
                    value = new JsonValue { Object = o };
                    return true;
                }
                while (true)
                {
                    White();
                    if (!String(out string key, out error))
                        return false;
                    White();
                    if (!Take(':'))
                    {
                        error = "Object property lacks colon.";
                        return false;
                    }
                    if (!Value(out JsonValue child, out error))
                        return false;
                    if (!o.TryAdd(key, child))
                    {
                        error = "Duplicate JSON property: " + key;
                        return false;
                    }
                    White();
                    if (Take('}'))
                    {
                        value = new JsonValue { Object = o };
                        return true;
                    }
                    if (!Take(','))
                    {
                        error = "Object property separator expected.";
                        return false;
                    }
                }
            }
            private bool Array(out JsonValue value, out string error)
            {
                value = null;
                error = null;
                ++_at;
                var a = new List<JsonValue>();
                White();
                if (Take(']'))
                {
                    value = new JsonValue { Array = a };
                    return true;
                }
                while (true)
                {
                    if (!Value(out JsonValue child, out error))
                        return false;
                    a.Add(child);
                    White();
                    if (Take(']'))
                    {
                        value = new JsonValue { Array = a };
                        return true;
                    }
                    if (!Take(','))
                    {
                        error = "Array separator expected.";
                        return false;
                    }
                }
            }
            private bool String(out string value, out string error)
            {
                value = null;
                error = null;
                if (!Take('\"'))
                {
                    error = "String expected.";
                    return false;
                }
                var b = new StringBuilder();
                while (_at < _text.Length)
                {
                    char c = _text[_at++];
                    if (c == '\"')
                    {
                        value = b.ToString();
                        return true;
                    }
                    if (c < ' ')
                    {
                        error = "Control character in string.";
                        return false;
                    }
                    if (c != '\\')
                    {
                        b.Append(c);
                        continue;
                    }
                    if (_at >= _text.Length)
                    {
                        error = "Truncated escape.";
                        return false;
                    }
                    char e = _text[_at++];
                    switch (e)
                    {
                    case '\"':
                        b.Append('\"');
                        break;
                    case '\\':
                        b.Append('\\');
                        break;
                    case '/':
                        b.Append('/');
                        break;
                    case 'b':
                        b.Append('\b');
                        break;
                    case 'f':
                        b.Append('\f');
                        break;
                    case 'n':
                        b.Append('\n');
                        break;
                    case 'r':
                        b.Append('\r');
                        break;
                    case 't':
                        b.Append('\t');
                        break;
                    case 'u':
                        if (!Hex(out char h))
                        {
                            error = "Invalid unicode escape.";
                            return false;
                        }
                        b.Append(h);
                        break;
                    default:
                        error = "Invalid escape.";
                        return false;
                    }
                }
                error = "Unterminated string.";
                return false;
            }
            private bool Hex(out char value)
            {
                value = '\0';
                if (_at + 4 > _text.Length)
                    return false;
                int n = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = _text[_at + i];
                    int d = c >= '0' && c <= '9'   ? c - '0'
                            : c >= 'a' && c <= 'f' ? c - 'a' + 10
                            : c >= 'A' && c <= 'F' ? c - 'A' + 10
                                                   : -1;
                    if (d < 0)
                        return false;
                    n = n * 16 + d;
                }
                _at += 4;
                value = (char)n;
                return true;
            }
            private bool Numeric(out double value, out string error)
            {
                value = 0;
                error = null;
                int start = _at;
                if (Take('-') && _at >= _text.Length)
                {
                    error = "Bad number.";
                    return false;
                }
                if (Take('0'))
                {
                }
                else
                {
                    if (_at >= _text.Length || !char.IsDigit(_text[_at]))
                    {
                        error = "Bad number.";
                        return false;
                    }
                    while (_at < _text.Length && char.IsDigit(_text[_at]))
                        ++_at;
                }
                if (Take('.'))
                {
                    if (_at >= _text.Length || !char.IsDigit(_text[_at]))
                    {
                        error = "Bad fraction.";
                        return false;
                    }
                    while (_at < _text.Length && char.IsDigit(_text[_at]))
                        ++_at;
                }
                if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
                {
                    ++_at;
                    if (_at < _text.Length && (_text[_at] == '+' || _text[_at] == '-'))
                        ++_at;
                    if (_at >= _text.Length || !char.IsDigit(_text[_at]))
                    {
                        error = "Bad exponent.";
                        return false;
                    }
                    while (_at < _text.Length && char.IsDigit(_text[_at]))
                        ++_at;
                }
                return double.TryParse(_text.Substring(start, _at - start), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out value) &&
                               Finite(value)
                           ? true
                           : Bad(out error);
            }
            private bool Bad(out string error)
            {
                error = "Invalid/non-finite number.";
                return false;
            }
            private bool Take(char c)
            {
                White();
                if (_at < _text.Length && _text[_at] == c)
                {
                    ++_at;
                    return true;
                }
                return false;
            }
            private bool Match(string s)
            {
                if (_at + s.Length > _text.Length ||
                    string.CompareOrdinal(_text, _at, s, 0, s.Length) != 0)
                    return false;
                _at += s.Length;
                return true;
            }
        }
    }
}
