// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// One guard for every ID and credential the app cannot work without (<see cref="RequiredCredentials"/>; docs/required-credentials.md).
    /// A Quest, Windows or macOS spectator build that lacks one is refused with ONE message that lists every missing ID and how to supply
    /// it. Before this, a build without the Photon Voice App ID ran, opened the microphone and lip-synced but never connected to voice,
    /// and the same silence was possible for the Photon App ID and the TURN secret.
    ///
    /// Each credential has an explicit opt-out variable for a build that only checks that the code compiles: SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID,
    /// SPATIALBLEND_ALLOW_NO_VOICE_APP_ID, SPATIALBLEND_ALLOW_NO_TURN (set to 1; the spectator script's --no-turn-secret is the TURN opt-out of
    /// that build). The stamp (TmpEssentialsBuildGuard) records what the build really has, so tools/check_apk_contents.py rejects the APK
    /// of an opted-out build. No value is ever printed: only presence and length.
    /// </summary>
    public sealed class RequiredCredentialsBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>The spectator build's own flag for "no TURN secret in the app" (tools/build-spectator-mac.sh --no-turn-secret).</summary>
        public const string SpectatorNoTurnSecretArgument = "-spectatorNoTurnSecret";

        public int callbackOrder => -1001;

        /// <summary>Null for a target that needs none of this (the Editor, tests, other platforms).</summary>
        public static CredentialProfile? ProfileFor(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android:
                case BuildTarget.StandaloneWindows64:
                    return CredentialProfile.Player;
                case BuildTarget.StandaloneOSX:
                    return CredentialProfile.Spectator;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Null when the build may go ahead, else the one message. <paramref name="turnWaivedByArgument"/> is the spectator's
        /// "-spectatorNoTurnSecret": the app is deliberately made without the secret.
        /// </summary>
        public static string Evaluate(BuildTarget target, CredentialFacts facts, Func<string, string> getEnvironmentVariable,
                                      bool turnWaivedByArgument = false)
        {
            var profile = ProfileFor(target);
            if (profile == null) return null;
            return RequiredCredentials.BuildFailure(target.ToString(), profile.Value, facts, getEnvironmentVariable,
                spec => turnWaivedByArgument && ReferenceEquals(spec, RequiredCredentials.Turn));
        }

        /// <summary>
        /// What this project would build with: the Photon settings asset, and the TURN secret as the build will have it (the user's
        /// file, which <see cref="TurnClientSecretSource"/> copies in, or a copy already in Resources).
        /// </summary>
        public static CredentialFacts ReadFacts()
        {
            string fusion = null, voice = null, server = null;
            if (Fusion.Photon.Realtime.PhotonAppSettings.TryGetGlobal(out var photon) && photon != null && photon.AppSettings != null)
            {
                fusion = photon.AppSettings.AppIdFusion;
                voice = photon.AppSettings.AppIdVoice;
                server = photon.AppSettings.Server;
            }
            var facts = CredentialFacts.FromPhoton(fusion, voice, server);
            facts.TurnClientSecret = TurnSecretAsBuilt();
            facts.TurnRequired = RequiredCredentialsRuntime.TurnIsWanted(Resources.Load<SpatialBlendSettings>("SpatialBlendSettings"));
            return facts;
        }

        public static CredentialState TurnSecretAsBuilt()
        {
            var fromSource = ReadTrimmed(TurnClientSecretSource.SourcePath);
            return CredentialState.OfText(fromSource ?? ReadTrimmed(TurnClientSecretSource.AssetPath));
        }

        private static string ReadTrimmed(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception)
            {
                return null; // unreadable: the same as absent
            }
        }

        public static bool SpectatorNoTurnSecret => Array.IndexOf(Environment.GetCommandLineArgs(), SpectatorNoTurnSecretArgument) >= 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var profile = ProfileFor(report.summary.platform);
            if (profile == null) return;
            var facts = ReadFacts();
            // Presence and length only, so the build log shows what the build has without a value.
            Debug.Log("[RequiredCredentialsBuildGuard] " + RequiredCredentials.SummaryLine(profile.Value, facts));
            var problem = Evaluate(report.summary.platform, facts, Environment.GetEnvironmentVariable, SpectatorNoTurnSecret);
            if (problem != null)
            {
                throw new BuildFailedException("[RequiredCredentialsBuildGuard] " + problem);
            }
        }
    }
}
