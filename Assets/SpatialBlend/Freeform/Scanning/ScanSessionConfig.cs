// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>
    /// Everything the scan service needs, read from SpatialBlendSettings by the app.
    /// An empty address leaves scanning disabled. UseTls selects the TLS connect for every address.
    /// </summary>
    public sealed class ScanSessionConfig
    {
        public const double DefaultSegmentTimeoutSeconds = 30.0;
        public const double DefaultReconstructTimeoutSeconds = 300.0;

        /// <summary>gRPC "host:port" of the primary server.</summary>
        public string ServerAddress = string.Empty;
        /// <summary>Optional second server tried when the primary does not connect.</summary>
        public string FallbackServerAddress = string.Empty;
        /// <summary>
        /// True connects both addresses over TLS (spatialblend_inference_session_connect_tls). False is
        /// plaintext. There is no automatic fallback between the two.
        /// </summary>
        public bool UseTls = false;
        public string ClientId = "unity-client";
        /// <summary>Where segmentation masks are written before they are read back.</summary>
        public string CacheDirectory = string.Empty;
        /// <summary>Where reconstruction outputs are written by the server side of the library.</summary>
        public string OutputDirectory = string.Empty;
        public double SegmentTimeoutSeconds = DefaultSegmentTimeoutSeconds;
        public double ReconstructTimeoutSeconds = DefaultReconstructTimeoutSeconds;

        public bool IsEnabled => !string.IsNullOrWhiteSpace(ServerAddress);

        /// <summary>
        /// The addresses to try, in order: the primary server, then the second one. Segmentation and reconstruction are
        /// unavailable without a primary. (The one-shot pipeline, which preferred the second server, was retired.)
        /// </summary>
        public List<string> AddressesFor(ScanKind kind)
        {
            string primary = ServerAddress?.Trim();
            string fallback = FallbackServerAddress?.Trim();
            var addresses = new List<string>();
            if (string.IsNullOrEmpty(primary)) return addresses;
            addresses.Add(primary);
            if (!string.IsNullOrEmpty(fallback) && !addresses.Contains(fallback)) addresses.Add(fallback);
            return addresses;
        }

        public double TimeoutFor(ScanKind kind) =>
            kind == ScanKind.Segment ? SegmentTimeoutSeconds : ReconstructTimeoutSeconds;

        public bool Validate(out ScanError error)
        {
            if (!IsEnabled)
            {
                error = new ScanError(ScanErrorCode.InvalidConfig, "No session server address is configured.");
                return false;
            }
            string reason;
            if (!string.IsNullOrWhiteSpace(ServerAddress) && !TryParseAddress(ServerAddress, out reason))
            {
                error = new ScanError(ScanErrorCode.InvalidConfig, $"Primary server address: {reason}");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(FallbackServerAddress) &&
                !TryParseAddress(FallbackServerAddress, out reason))
            {
                error = new ScanError(ScanErrorCode.InvalidConfig,
                    $"Fallback server address: {reason}");
                return false;
            }
            if (string.IsNullOrWhiteSpace(ClientId))
            {
                error = new ScanError(ScanErrorCode.InvalidConfig, "The client id is empty.");
                return false;
            }
            if (SegmentTimeoutSeconds <= 0.0 || ReconstructTimeoutSeconds <= 0.0)
            {
                error = new ScanError(ScanErrorCode.InvalidConfig, "Timeouts must be positive.");
                return false;
            }
            error = ScanError.None;
            return true;
        }

        /// <summary>Accepts "host:port" with a port in 1..65535. A URL scheme is refused.</summary>
        public static bool TryParseAddress(string address, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(address))
            {
                reason = "empty";
                return false;
            }
            string trimmed = address.Trim();
            if (trimmed.Contains("://"))
            {
                reason = "expected host:port without a URL scheme";
                return false;
            }
            int colon = trimmed.LastIndexOf(':');
            if (colon <= 0 || colon == trimmed.Length - 1)
            {
                reason = "expected host:port";
                return false;
            }
            string host = trimmed.Substring(0, colon);
            if (host.IndexOfAny(new[] { '/', '\\' }) >= 0 || HasWhiteSpaceOrControl(host))
            {
                reason = "the host contains an invalid character";
                return false;
            }
            if (!int.TryParse(trimmed.Substring(colon + 1), out int port) || port < 1 || port > 65535)
            {
                reason = "the port must be 1..65535";
                return false;
            }
            return true;
        }

        private static bool HasWhiteSpaceOrControl(string s)
        {
            foreach (char c in s)
                if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
            return false;
        }

        /// <summary>
        /// The v1 rule: a per-device suffix keeps two headsets from sharing one client id.
        /// The hasher is Hash128.Compute(...).ToString() in the player and a fake in tests.
        /// </summary>
        public static string ResolveClientId(string baseId, bool useDeviceSpecific, string deviceId,
                                             Func<string, string> hasher)
        {
            string resolvedBase = string.IsNullOrWhiteSpace(baseId) ? "unity-client" : baseId;
            if (!useDeviceSpecific) return resolvedBase;

            bool valid = !string.IsNullOrWhiteSpace(deviceId) &&
                         !string.Equals(deviceId, "unknown", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(deviceId, "n/a", StringComparison.OrdinalIgnoreCase);
            if (!valid || hasher == null) return resolvedBase;

            string hash = hasher(deviceId) ?? string.Empty;
            if (hash.Length == 0) return resolvedBase;
            string token = hash.Length > 8 ? hash.Substring(0, 8) : hash;
            return $"{resolvedBase}-{token}";
        }
    }
}
