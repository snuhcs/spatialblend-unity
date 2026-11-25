// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>One line of the panel's notices block (and, for a Bad one, a toast): what is off in this build, in plain words.</summary>
    public struct CredentialNotice
    {
        public string Key;
        public StatusTone Tone;
        public string Title;
        public string Message;
    }

    /// <summary>Why the panel says scanning is off. None means the line is hidden.</summary>
    public enum ScanOffReason
    {
        None,
        /// <summary>The settings field and spatialblend-local.json are both empty, and no scan service is running.</summary>
        NoServer,
        /// <summary>An address is set, but the headset cameras are not allowed.</summary>
        PermissionDenied,
        /// <summary>An address is set, and the scan service tried to connect and the server did not answer.</summary>
        Unreachable,
        /// <summary>An address is set, and startup failed for a reason other than a refused connection. The message is that error.</summary>
        Fault,
    }

    /// <summary>
    /// What the control panel says about the build's IDs and credentials (<see cref="RequiredCredentials"/>). Pure, so the rule is testable
    /// without a scene. A required credential this build does not have is a Bad notice ("Voice disabled: no Photon Voice App ID in this
    /// build"). Scanning off is a Neutral line whose words say which of the three it is: no server configured, camera permission denied,
    /// or the server could not be reached. A complete build whose scan server is reachable shows nothing.
    /// </summary>
    public static class CredentialNoticePresenter
    {
        public const string ScanOffTitle = "Scanning is off";
        public const string NoServerMessage = "No scan server is set.";
        public const string PermissionMessage = "Camera access is off. Allow the headset cameras, then try the scan again.";
        public const string UnreachableMessage = "The scan server is set, but this headset could not reach it. Check Wi-Fi.";
        /// <summary>The no-server line. Kept for callers that still name the old constant.</summary>
        public const string ScanOffMessage = NoServerMessage;

        public static List<CredentialNotice> Describe(CredentialProfile profile, CredentialFacts facts, bool scanServerConfigured) =>
            Describe(profile, facts, scanServerConfigured ? ScanOffReason.None : ScanOffReason.NoServer);

        public static List<CredentialNotice> Describe(CredentialProfile profile, CredentialFacts facts, ScanOffReason scan, string detail = null)
        {
            var notices = new List<CredentialNotice>();
            foreach (var spec in RequiredCredentials.Missing(profile, facts))
            {
                notices.Add(new CredentialNotice { Key = spec.Key, Tone = StatusTone.Bad, Title = spec.NoticeTitle, Message = spec.NoticeMessage });
            }
            if (scan != ScanOffReason.None)
            {
                notices.Add(new CredentialNotice
                {
                    Key = scan == ScanOffReason.PermissionDenied ? "scan-permission"
                        : scan == ScanOffReason.Unreachable ? "scan-unreachable"
                        : scan == ScanOffReason.Fault ? "scan-fault" : "scan-server",
                    Tone = StatusTone.Neutral,
                    Title = ScanOffTitle,
                    Message = MessageFor(scan, detail),
                });
            }
            return notices;
        }

        public static string MessageFor(ScanOffReason scan, string detail = null)
        {
            switch (scan)
            {
                case ScanOffReason.PermissionDenied: return PermissionMessage;
                case ScanOffReason.Unreachable: return UnreachableMessage;
                case ScanOffReason.Fault: return string.IsNullOrEmpty(detail) ? "Scanning could not start." : detail;
                default: return NoServerMessage;
            }
        }

        /// <summary>
        /// No server, unless a connect was actually attempted and failed. Camera denial matters only once an address exists;
        /// a running service is reachable. A granted camera with no address is still <see cref="ScanOffReason.NoServer"/>.
        /// </summary>
        public static ScanOffReason Classify(bool addressConfigured, bool serviceRunning, bool connectFailed, bool cameraDenied) =>
            Classify(addressConfigured, serviceRunning, connectFailed ? "ConnectFailed:" : null, cameraDenied);

        /// <summary>
        /// Same order as the bool overload. <paramref name="lastError"/> is a <see cref="SpatialBlend.Freeform.Scanning.ScanError"/>
        /// (<c>Code: message</c>) or the InitializeWith sentence. Only <c>ConnectFailed</c> is the Wi-Fi line. Any other
        /// startup failure is <see cref="ScanOffReason.Fault"/> and the panel shows that text.
        /// </summary>
        public static ScanOffReason Classify(bool addressConfigured, bool serviceRunning, string lastError, bool cameraDenied)
        {
            if (serviceRunning) return cameraDenied ? ScanOffReason.PermissionDenied : ScanOffReason.None;
            if (!addressConfigured) return ScanOffReason.NoServer;
            if (IsConnectFailure(lastError)) return ScanOffReason.Unreachable;
            if (OtherStartFailure(lastError) != null) return ScanOffReason.Fault;
            return cameraDenied ? ScanOffReason.PermissionDenied : ScanOffReason.None;
        }

        /// <summary>True only for a refused connection (<c>ConnectFailed</c>). A transport or setup fault is not this.</summary>
        public static bool IsConnectFailure(string lastError)
        {
            return !string.IsNullOrEmpty(lastError) && lastError.StartsWith("ConnectFailed", StringComparison.Ordinal);
        }

        /// <summary>The startup error to show as itself, or null when it is a connect failure or not a startup error.</summary>
        public static string OtherStartFailure(string lastError)
        {
            if (string.IsNullOrEmpty(lastError) || IsConnectFailure(lastError)) return null;
            if (lastError.IndexOf("No session server address", StringComparison.Ordinal) >= 0) return null;
            if (lastError.IndexOf("Only a fallback", StringComparison.Ordinal) >= 0) return null;
            if (lastError.IndexOf("not ready", StringComparison.Ordinal) >= 0) return null;
            return lastError;
        }

        /// <summary>
        /// The scan server counts as configured when a scan service is already running, when the device's local config names
        /// an address, or when the settings do (the shipped default, or a replacement). Camera permission is not this check.
        /// </summary>
        public static bool ScanServerConfigured(SpatialBlendSettings settings, LocalSessionConfig local, bool scanServiceRunning = false)
        {
            if (scanServiceRunning) return true;
            if (local != null && !string.IsNullOrWhiteSpace(local.ServerAddress)) return true;
            if (settings == null) return false;
            return SessionManager.BuildConfig(settings, string.Empty, true, string.Empty, string.Empty, string.Empty, local).IsEnabled;
        }
    }
}
