// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// What the running app has of each required credential (see <see cref="RequiredCredentials"/>), and the startup report. A player
    /// logs one structured error per missing required ID at startup; the control panel shows the same list as a notice. The Editor does
    /// not log at startup (a developer's test run would fail on an unexpected error), but the panel still shows the notice.
    /// Only presence and length are read out, never a value.
    /// </summary>
    public static class RequiredCredentialsRuntime
    {
        /// <summary>The desktop spectator build (macOS player) has no voice; every other target is the full player.</summary>
        public static CredentialProfile ProfileFor(RuntimePlatform platform) =>
            platform == RuntimePlatform.OSXPlayer ? CredentialProfile.Spectator : CredentialProfile.Player;

        public static CredentialFacts Read()
        {
            string fusion = null, voice = null, server = null;
            if (PhotonAppSettings.TryGetGlobal(out var photon) && photon != null && photon.AppSettings != null)
            {
                fusion = photon.AppSettings.AppIdFusion;
                voice = photon.AppSettings.AppIdVoice;
                server = photon.AppSettings.Server;
            }
            var facts = CredentialFacts.FromPhoton(fusion, voice, server);
            facts.TurnClientSecret = CredentialState.OfText(TurnClientSecret.Load());
            facts.TurnRequired = TurnIsWanted(Resources.Load<SpatialBlendSettings>("SpatialBlendSettings"));
            return facts;
        }

        /// <summary>TURN is wanted unless WebRTC is off or the credentials Worker URL is empty (documented as "TURN off"). No settings asset: wanted.</summary>
        public static bool TurnIsWanted(SpatialBlendSettings settings) =>
            settings == null || (settings.useWebRtc && !string.IsNullOrWhiteSpace(TurnWorkerUrl.Resolve(settings.turnCredentialsUrl)));

        /// <summary>
        /// Writes the report through the given sinks: one summary line, then one error per missing required credential. Returns the
        /// missing ones. Pure apart from the sinks, so a test passes its own.
        /// </summary>
        public static System.Collections.Generic.List<CredentialSpec> Report(CredentialProfile profile, CredentialFacts facts,
                                                                              Action<string> info, Action<string> error)
        {
            info?.Invoke(RequiredCredentials.SummaryLine(profile, facts));
            var missing = RequiredCredentials.Missing(profile, facts);
            foreach (var spec in missing) error?.Invoke(RequiredCredentials.ErrorLine(spec));
            return missing;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Application.isEditor) return;
            Report(ProfileFor(Application.platform), Read(), message => Debug.Log(message), message => Debug.LogError(message));
        }
    }
}
