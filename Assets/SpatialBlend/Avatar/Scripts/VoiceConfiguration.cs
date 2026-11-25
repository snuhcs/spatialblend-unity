// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Fusion.Photon.Realtime;
using UnityEngine;

namespace SpatialBlend.Avatar
{
    /// <summary>
    /// Whether this build can reach Photon Voice at all. <c>FusionVoiceClient</c> (on the NetworkRunner prefab) connects with the
    /// Voice App ID in <c>PhotonAppSettings.AppIdVoice</c>; the Fusion App ID does not work for voice, and with no Voice App ID
    /// <c>ConnectUsingSettings</c> only logs "Provide an AppId or a Server address" and never connects. Nothing is transmitted or
    /// received then, while the microphone still opens and the avatar still lip-syncs, so the app looks like it has voice.
    /// A build whose PhotonAppSettings has a Fusion App ID and an empty Voice App ID fails exactly this way.
    /// </summary>
    public static class VoiceConfiguration
    {
        public const string MissingAppIdMessage =
            "[voice] PhotonAppSettings.AppIdVoice is empty: Photon Voice cannot connect, so no voice is sent or received. " +
            "Create a Photon 'Voice' application in the Photon dashboard and rebuild with its App ID " +
            "(python3 tools/setup_sdks.py --photon-voice-app-id <id>, or SPATIALBLEND_PHOTON_VOICE_APP_ID).";

        /// <summary>The same test Photon applies before it connects: an App ID or a self-hosted server address.</summary>
        public static bool IsVoiceConfigured(string appIdVoice, string server) =>
            !string.IsNullOrWhiteSpace(appIdVoice) || !string.IsNullOrWhiteSpace(server);

        /// <summary>Reads the global PhotonAppSettings (what <c>FusionVoiceClient.UseFusionAppSettings</c> uses). False when there is none.</summary>
        public static bool IsVoiceConfigured()
        {
            if (!PhotonAppSettings.TryGetGlobal(out var settings) || settings == null || settings.AppSettings == null) return false;
            return IsVoiceConfigured(settings.AppSettings.AppIdVoice, settings.AppSettings.Server);
        }

        /// <summary>Logs <see cref="MissingAppIdMessage"/> once when voice cannot connect. Returns whether voice is configured.</summary>
        public static bool WarnIfMissing()
        {
            if (IsVoiceConfigured()) return true;
            Debug.LogError(MissingAppIdMessage);
            return false;
        }
    }
}
