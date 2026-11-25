// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using SpatialBlend.Avatar;

namespace SpatialBlend
{
    /// <summary>Which app a credential list is for. The Quest and Windows player needs voice; the macOS desktop spectator has no microphone.</summary>
    public enum CredentialProfile
    {
        Player,
        Spectator,
    }

    /// <summary>Whether one credential is there, and how long it is. Never the value itself: nothing built from this can leak a secret.</summary>
    public readonly struct CredentialState
    {
        public readonly bool Present;
        public readonly int Length;
        /// <summary>Why a non-empty value was refused (for example "not a GUID"); null otherwise.</summary>
        public readonly string Problem;

        public CredentialState(bool present, int length, string problem = null)
        {
            Present = present;
            Length = length;
            Problem = problem;
        }

        public static CredentialState Absent => default;

        public static CredentialState OfText(string text)
        {
            int length = string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Length;
            return new CredentialState(length > 0, length);
        }

        public string Describe() => Present ? (Length == 0 ? "set (self-hosted server)" : $"set ({Length} characters)") : "MISSING" + (Problem != null ? " (" + Problem + ")" : string.Empty);
    }

    /// <summary>What a build or a running app has of each required credential. Plain data, so the rules are testable without Unity or a scene.</summary>
    public sealed class CredentialFacts
    {
        public CredentialState FusionAppId;
        public CredentialState VoiceAppId;
        public CredentialState TurnClientSecret;
        /// <summary>
        /// False only when TURN is deliberately off (WebRTC disabled, or an empty credentials Worker URL): then the client secret
        /// has nothing to open and is not required. True is the default; "unknown" counts as required.
        /// </summary>
        public bool TurnRequired = true;

        /// <summary>The Photon part of the facts, from PhotonAppSettings.AppSettings (the Fusion App ID, the Voice App ID, a self-hosted Voice server).</summary>
        public static CredentialFacts FromPhoton(string appIdFusion, string appIdVoice, string server)
        {
            var facts = new CredentialFacts();
            facts.FusionAppId = RequiredCredentials.FusionState(appIdFusion);
            facts.VoiceAppId = VoiceConfiguration.IsVoiceConfigured(appIdVoice, server)
                ? new CredentialState(true, string.IsNullOrWhiteSpace(appIdVoice) ? 0 : appIdVoice.Trim().Length)
                : CredentialState.Absent;
            return facts;
        }
    }

    /// <summary>One required credential: what it is, what breaks silently without it, how to supply it, and the variable that waives it for a compile-only build.</summary>
    public sealed class CredentialSpec
    {
        public string Key;
        public string Name;
        /// <summary>What does not work without it, in a sentence (developer text: build errors and the log).</summary>
        public string Impact;
        public string HowToSupply;
        /// <summary>Set to 1 to build without it anyway (a compile-only check). The APK of such a build is rejected by tools/check_apk_contents.py.</summary>
        public string AllowVariable;
        /// <summary>The user-facing headline for the in-app notice: plain words, what is off and why.</summary>
        public string NoticeTitle;
        public string NoticeMessage;
        /// <summary>The key the build stamp carries (true: the credential is in the build).</summary>
        public string StampKey;
    }

    /// <summary>
    /// The IDs and credentials an SpatialBlend Freeform app cannot work without.
    /// This is the one list for all of them, used by the build guard (Editor), the build stamp and the APK check, the
    /// runtime startup log and the control panel notice, and mirrored by tools/setup_sdks.py. The scan server (spatialblend-local.json) is
    /// optional by design and is not in it. docs/required-credentials.md has the full inventory.
    /// </summary>
    public static class RequiredCredentials
    {
        public const string FusionKey = "photon-app-id";
        public const string VoiceKey = "photon-voice-app-id";
        public const string TurnKey = "turn-client-secret";
        public const string DocsPath = "docs/required-credentials.md";

        public static readonly CredentialSpec Fusion = new CredentialSpec
        {
            Key = FusionKey,
            Name = "Photon Fusion App ID",
            Impact = "the app cannot connect to Photon, so no room can be created or joined",
            HowToSupply = "python3 tools/setup_sdks.py --photon-app-id <id> (or SPATIALBLEND_PHOTON_APP_ID); the id of the Fusion application in the Photon dashboard",
            AllowVariable = "SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID",
            NoticeTitle = "Rooms unavailable: no Photon App ID in this build",
            NoticeMessage = "This app was built without it. Install a complete build.",
            StampKey = "photonAppIdConfigured",
        };

        public static readonly CredentialSpec Voice = new CredentialSpec
        {
            Key = VoiceKey,
            Name = "Photon Voice App ID",
            Impact = "the app opens the microphone and lip-syncs but never connects to Photon Voice: nobody hears anybody",
            HowToSupply = "python3 tools/setup_sdks.py --photon-voice-app-id <id> (or SPATIALBLEND_PHOTON_VOICE_APP_ID); a separate 'Voice' application in the Photon dashboard",
            AllowVariable = "SPATIALBLEND_ALLOW_NO_VOICE_APP_ID",
            NoticeTitle = "Voice disabled: no Photon Voice App ID in this build",
            NoticeMessage = "You will not hear others, and they will not hear you. Install a complete build.",
            StampKey = "voiceAppIdConfigured",
        };

        public static readonly CredentialSpec Turn = new CredentialSpec
        {
            Key = TurnKey,
            Name = "TURN client secret",
            Impact = "the WebRTC carrier has no relay: on a network that blocks a direct connection the link silently falls back to the slow Photon relay",
            HowToSupply = "python3 tools/setup_sdks.py --turn-secret-file <file> (setup_sdks.py also reads SPATIALBLEND_TURN_CLIENT_SECRET_FILE); it is copied to ~/.config/spatialblend/turn-client-secret and embedded at build",
            AllowVariable = "SPATIALBLEND_ALLOW_NO_TURN",
            NoticeTitle = "Relay unavailable: no TURN secret in this build",
            NoticeMessage = "Connections may fail or be slow on some networks. Install a complete build.",
            StampKey = "turnClientSecretConfigured",
        };

        /// <summary>Every credential the app can require, in the order they are reported.</summary>
        public static readonly IReadOnlyList<CredentialSpec> All = new[] { Fusion, Voice, Turn };

        /// <summary>The Photon Fusion App ID is a GUID; anything else would be refused by Photon at run time, silently for the user.</summary>
        public static CredentialState FusionState(string appIdFusion)
        {
            if (string.IsNullOrWhiteSpace(appIdFusion)) return CredentialState.Absent;
            var text = appIdFusion.Trim();
            return Guid.TryParse(text, out _) ? new CredentialState(true, text.Length) : new CredentialState(false, text.Length, "not a GUID");
        }

        public static CredentialState StateOf(CredentialSpec spec, CredentialFacts facts)
        {
            if (ReferenceEquals(spec, Fusion)) return facts.FusionAppId;
            if (ReferenceEquals(spec, Voice)) return facts.VoiceAppId;
            return facts.TurnClientSecret;
        }

        /// <summary>The player needs all of them; the desktop spectator has no voice. The TURN secret is not required when TURN is deliberately off.</summary>
        public static bool IsRequired(CredentialSpec spec, CredentialProfile profile, CredentialFacts facts)
        {
            if (ReferenceEquals(spec, Voice)) return profile == CredentialProfile.Player;
            if (ReferenceEquals(spec, Turn)) return facts.TurnRequired;
            return true;
        }

        /// <summary>The required credentials this app does not have, in report order. Empty when it is complete.</summary>
        public static List<CredentialSpec> Missing(CredentialProfile profile, CredentialFacts facts)
        {
            var missing = new List<CredentialSpec>();
            foreach (var spec in All)
            {
                if (IsRequired(spec, profile, facts) && !StateOf(spec, facts).Present) missing.Add(spec);
            }
            return missing;
        }

        /// <summary>True when the environment variable named by the spec is exactly "1".</summary>
        public static bool IsWaived(CredentialSpec spec, Func<string, string> getEnvironmentVariable) =>
            string.Equals(getEnvironmentVariable?.Invoke(spec.AllowVariable), "1", StringComparison.Ordinal);

        /// <summary>
        /// The one build failure: every missing required credential with what breaks and how to supply it, plus the presence (never the
        /// value) of the others. Null when nothing required is missing. A waived credential (its variable is 1, or <paramref name="alsoWaived"/>
        /// returns true) does not fail the build.
        /// </summary>
        public static string BuildFailure(string targetName, CredentialProfile profile, CredentialFacts facts,
                                          Func<string, string> getEnvironmentVariable, Func<CredentialSpec, bool> alsoWaived = null)
        {
            var failing = new List<CredentialSpec>();
            foreach (var spec in Missing(profile, facts))
            {
                if (IsWaived(spec, getEnvironmentVariable) || (alsoWaived != null && alsoWaived(spec))) continue;
                failing.Add(spec);
            }
            if (failing.Count == 0) return null;

            var text = new StringBuilder();
            text.Append($"This {targetName} build is missing {failing.Count} required credential{(failing.Count == 1 ? string.Empty : "s")}; the app would run but not work.");
            foreach (var spec in failing)
            {
                var state = StateOf(spec, facts);
                text.Append($"\n  - {spec.Key} ({spec.Name}): {state.Describe()}. Without it, {spec.Impact}.");
                text.Append($"\n      Supply it: {spec.HowToSupply}.");
                text.Append($"\n      Compile-only check, no working app: set {spec.AllowVariable}=1 (check_apk_contents.py rejects that APK).");
            }
            var others = new List<string>();
            foreach (var spec in All)
            {
                if (!failing.Contains(spec) && IsRequired(spec, profile, facts)) others.Add($"{spec.Key} {StateOf(spec, facts).Describe()}");
            }
            if (others.Count > 0) text.Append("\n  Not failing: " + string.Join("; ", others) + ".");
            text.Append($"\n  Values are never printed. Inventory: {DocsPath}.");
            return text.ToString();
        }

        /// <summary>The build stamp's JSON members (no braces, no leading comma). A true member means the credential is in the build.</summary>
        public static string StampMembers(CredentialFacts facts)
        {
            return "\"" + Voice.StampKey + "\":" + Bool(facts.VoiceAppId.Present) + "," +
                   "\"" + Fusion.StampKey + "\":" + Bool(facts.FusionAppId.Present) + "," +
                   "\"" + Turn.StampKey + "\":" + Bool(facts.TurnClientSecret.Present) + "," +
                   "\"" + TurnRequiredStampKey + "\":" + Bool(facts.TurnRequired);
        }

        public const string TurnRequiredStampKey = "turnRequired";

        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary>The one structured log line for a missing required credential. Names the id, never a value.</summary>
        public static string ErrorLine(CredentialSpec spec) =>
            $"[credentials] missing id={spec.Key} required=true impact=\"{spec.Impact}\" fix=\"{spec.HowToSupply}\"";

        /// <summary>One info line with the presence (and length) of each required credential.</summary>
        public static string SummaryLine(CredentialProfile profile, CredentialFacts facts)
        {
            var parts = new List<string>();
            foreach (var spec in All)
            {
                parts.Add(spec.Key + "=" + (IsRequired(spec, profile, facts) ? StateOf(spec, facts).Describe() : "not required"));
            }
            return "[credentials] " + string.Join(" ", parts);
        }
    }
}
