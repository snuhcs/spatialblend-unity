// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend
{
    /// <summary>
    /// Optional per-device scan-server config read at runtime from
    /// "&lt;persistentDataPath&gt;/spatialblend-local.json". It overrides the address and TLS flag
    /// shipped in SpatialBlendSettings. A flat JSON object with optional keys sessionServerAddress,
    /// sessionFallbackServerAddress (gRPC "host:port", no scheme), tls (boolean), scanSegmentTimeoutSeconds
    /// and scanReconstructTimeoutSeconds.
    /// A valid value overrides the SpatialBlendSettings value. A bad or absent field changes nothing, except
    /// that a primary address with no tls key means plaintext: that is what those device files meant
    /// before the settings default became TLS. With no primary address, the settings TLS default stays.
    ///
    /// Privacy rule: warnings name the field and the reason only. They never include an address
    /// or any part of the file. The class has no UnityEngine dependency, so it is tested as pure code.
    /// </summary>
    public sealed class LocalSessionConfig
    {
        public const string FileName = "spatialblend-local.json";
        public const string ServerAddressKey = "sessionServerAddress";
        public const string FallbackServerAddressKey = "sessionFallbackServerAddress";
        public const string TlsKey = "tls";
        public const string SegmentTimeoutKey = "scanSegmentTimeoutSeconds";
        public const string ReconstructTimeoutKey = "scanReconstructTimeoutSeconds";
        public const int MaxFileBytes = 64 * 1024;

        /// <summary>Null when the file does not set a valid primary address.</summary>
        public string ServerAddress { get; private set; }
        public string FallbackServerAddress { get; private set; }
        /// <summary>Null when the file does not set a valid boolean. True selects the TLS connect.</summary>
        public bool? UseTls { get; private set; }
        public double? SegmentTimeoutSeconds { get; private set; }
        public double? ReconstructTimeoutSeconds { get; private set; }

        /// <summary>The config of an absent file: overrides nothing.</summary>
        public static LocalSessionConfig None => new LocalSessionConfig();

        public bool HasOverrides => OverriddenFields.Count > 0;

        /// <summary>Key names the file overrides. Names only, never values.</summary>
        public List<string> OverriddenFields
        {
            get
            {
                var names = new List<string>();
                if (ServerAddress != null) names.Add(ServerAddressKey);
                if (FallbackServerAddress != null) names.Add(FallbackServerAddressKey);
                if (UseTls.HasValue) names.Add(TlsKey);
                if (SegmentTimeoutSeconds.HasValue) names.Add(SegmentTimeoutKey);
                if (ReconstructTimeoutSeconds.HasValue) names.Add(ReconstructTimeoutKey);
                return names;
            }
        }

        /// <summary>
        /// Reads spatialblend-local.json from a directory. A missing file is silent. Any read or parse
        /// problem warns without values and returns a config that overrides nothing.
        /// </summary>
        public static LocalSessionConfig Load(string directory, Action<string> warn)
        {
            if (string.IsNullOrEmpty(directory)) return None;
            string path = Path.Combine(directory, FileName);
            string text;
            try
            {
                if (!File.Exists(path)) return None;
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    warn?.Invoke($"{FileName} is too large (over {MaxFileBytes} bytes). Ignoring it.");
                    return None;
                }
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                // Only the exception type: a message could echo the path or content.
                warn?.Invoke($"{FileName} could not be read ({e.GetType().Name}). Ignoring it.");
                return None;
            }
            return Parse(text, warn);
        }

        /// <summary>Parses file text. Never throws and never reports a value.</summary>
        public static LocalSessionConfig Parse(string json, Action<string> warn)
        {
            var result = new LocalSessionConfig();
            if (json != null && json.Length > MaxFileBytes)
            {
                warn?.Invoke($"{FileName} is too large (over {MaxFileBytes} characters). Ignoring it.");
                return result;
            }
            if (!MiniJson.TryParseFlatObject(json, out Dictionary<string, object> fields))
            {
                warn?.Invoke($"{FileName} is not a valid flat JSON object. Ignoring the whole file.");
                return result;
            }

            result.ServerAddress = ReadAddress(fields, ServerAddressKey, warn);
            result.FallbackServerAddress = ReadAddress(fields, FallbackServerAddressKey, warn);
            result.UseTls = ReadBool(fields, TlsKey, warn);
            result.SegmentTimeoutSeconds = ReadTimeout(fields, SegmentTimeoutKey, warn);
            result.ReconstructTimeoutSeconds = ReadTimeout(fields, ReconstructTimeoutKey, warn);
            return result;
        }

        /// <summary>
        /// Applies the valid overrides in place. Absent fields keep the existing value. A primary address
        /// with no tls key forces plaintext, which is what an older device file meant.
        /// </summary>
        public void ApplyTo(ScanSessionConfig config)
        {
            if (config == null) return;
            if (ServerAddress != null) config.ServerAddress = ServerAddress;
            if (FallbackServerAddress != null) config.FallbackServerAddress = FallbackServerAddress;
            if (UseTls.HasValue) config.UseTls = UseTls.Value;
            else if (ServerAddress != null) config.UseTls = false;
            if (SegmentTimeoutSeconds.HasValue) config.SegmentTimeoutSeconds = SegmentTimeoutSeconds.Value;
            if (ReconstructTimeoutSeconds.HasValue) config.ReconstructTimeoutSeconds = ReconstructTimeoutSeconds.Value;
        }

        private static string ReadAddress(Dictionary<string, object> fields, string key, Action<string> warn)
        {
            if (!fields.TryGetValue(key, out object raw) || raw == null) return null;
            if (!(raw is string text))
            {
                warn?.Invoke($"{FileName}: '{key}' must be a string. Ignoring this field.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(text)) return null; // empty keeps the settings value
            if (!ScanSessionConfig.TryParseAddress(text, out string reason))
            {
                // The reason strings are fixed text that never contain the address.
                warn?.Invoke($"{FileName}: '{key}' is not a valid address ({reason}). Ignoring this field.");
                return null;
            }
            return text.Trim();
        }

        private static bool? ReadBool(Dictionary<string, object> fields, string key, Action<string> warn)
        {
            if (!fields.TryGetValue(key, out object raw) || raw == null) return null;
            if (raw is bool flag) return flag;
            warn?.Invoke($"{FileName}: '{key}' must be true or false. Ignoring this field.");
            return null;
        }

        private static double? ReadTimeout(Dictionary<string, object> fields, string key, Action<string> warn)
        {
            if (!fields.TryGetValue(key, out object raw) || raw == null) return null;
            if (raw is NumberError)
            {
                warn?.Invoke($"{FileName}: '{key}' is not a usable number. Ignoring this field.");
                return null;
            }
            if (!(raw is double seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
            {
                warn?.Invoke($"{FileName}: '{key}' must be a positive number. Ignoring this field.");
                return null;
            }
            return seconds;
        }

        internal sealed class NumberError
        {
            public static readonly NumberError Instance = new NumberError();
            private NumberError() { }
        }

        /// <summary>
        /// Minimal parser for a flat JSON object whose values are strings, numbers, true, false or
        /// null. Nested objects and arrays are rejected. Kept local so the type needs no Unity or
        /// third-party JSON library. Returns false on any syntax error.
        /// </summary>
        internal static class MiniJson
        {
            public static bool TryParseFlatObject(string text, out Dictionary<string, object> result)
            {
                result = null;
                if (text == null) return false;
                int i = 0;
                var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipSpace(text, ref i);
                if (i < text.Length && text[i] == '\uFEFF') { i++; SkipSpace(text, ref i); }
                if (i >= text.Length || text[i] != '{') return false;
                i++;
                SkipSpace(text, ref i);
                if (i < text.Length && text[i] == '}')
                {
                    i++;
                }
                else
                {
                    while (true)
                    {
                        SkipSpace(text, ref i);
                        if (!ReadString(text, ref i, out string key)) return false;
                        SkipSpace(text, ref i);
                        if (i >= text.Length || text[i] != ':') return false;
                        i++;
                        SkipSpace(text, ref i);
                        if (!ReadValue(text, ref i, out object value)) return false;
                        fields[key] = value;
                        SkipSpace(text, ref i);
                        if (i >= text.Length) return false;
                        if (text[i] == ',') { i++; continue; }
                        if (text[i] == '}') { i++; break; }
                        return false;
                    }
                }
                SkipSpace(text, ref i);
                if (i != text.Length) return false;
                result = fields;
                return true;
            }

            private static void SkipSpace(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            }

            private static bool ReadValue(string s, ref int i, out object value)
            {
                value = null;
                if (i >= s.Length) return false;
                char c = s[i];
                if (c == '"')
                {
                    if (!ReadString(s, ref i, out string str)) return false;
                    value = str;
                    return true;
                }
                if (Literal(s, ref i, "true")) { value = true; return true; }
                if (Literal(s, ref i, "false")) { value = false; return true; }
                if (Literal(s, ref i, "null")) { value = null; return true; }
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int start = i;
                    while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                    string token = s.Substring(start, i - start);
                    if (!IsJsonNumber(token)) return false; // syntax error: malformed file
                    // Valid syntax but not representable (1e999): a field-level problem, not a file one.
                    value = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                            && !double.IsInfinity(number) && !double.IsNaN(number)
                        ? (object)number
                        : NumberError.Instance;
                    return true;
                }
                return false; // nested object or array, or garbage
            }

            // RFC 8259: -? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?
            private static bool IsJsonNumber(string t)
            {
                int i = 0;
                if (i < t.Length && t[i] == '-') i++;
                if (i >= t.Length) return false;
                if (t[i] == '0') i++;
                else if (t[i] >= '1' && t[i] <= '9') { while (i < t.Length && IsDigit(t[i])) i++; }
                else return false;
                if (i < t.Length && t[i] == '.')
                {
                    i++;
                    int d = i;
                    while (i < t.Length && IsDigit(t[i])) i++;
                    if (i == d) return false;
                }
                if (i < t.Length && (t[i] == 'e' || t[i] == 'E'))
                {
                    i++;
                    if (i < t.Length && (t[i] == '+' || t[i] == '-')) i++;
                    int d = i;
                    while (i < t.Length && IsDigit(t[i])) i++;
                    if (i == d) return false;
                }
                return i == t.Length;
            }

            private static bool IsDigit(char c) => c >= '0' && c <= '9';

            private static bool IsHex(char c) =>
                IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

            private static bool Literal(string s, ref int i, string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
                i += word.Length;
                return true;
            }

            private static bool ReadString(string s, ref int i, out string value)
            {
                value = null;
                if (i >= s.Length || s[i] != '"') return false;
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') { value = sb.ToString(); return true; }
                    if (c < 0x20) return false;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) return false;
                    char e = s[i++];
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
                            if (i + 4 > s.Length || !IsHex(s[i]) || !IsHex(s[i + 1]) || !IsHex(s[i + 2]) ||
                                !IsHex(s[i + 3])) return false;
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: return false;
                    }
                }
                return false;
            }
        }
    }
}
