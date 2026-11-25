// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Editor;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEditor;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// One list of the IDs and credentials the app cannot work without (RequiredCredentials; docs/required-credentials.md), and what each
    /// layer does when one is missing: the build guard names every missing one in one message, each has its own opt-out, the stamp records
    /// what the build really has, the runtime logs one structured error per missing one and the control panel shows a notice. The scan
    /// server is the public lab endpoint in SpatialBlendSettings; a device file can replace it. The panel says why scanning is off, and
    /// that line never fails a build.
    /// Obviously fake values only: a test must never hold a real ID or secret.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class RequiredCredentialsTests
    {
        private const string FakeFusion = "00000000-0000-4000-8000-0000000000f1";
        private const string FakeVoice = "00000000-0000-4000-8000-0000000000a2";
        private const string FakeSecret = "fake-turn-secret-do-not-print-0123456789";

        private static CredentialFacts Full()
        {
            var facts = CredentialFacts.FromPhoton(FakeFusion, FakeVoice, "");
            facts.TurnClientSecret = CredentialState.OfText(FakeSecret);
            return facts;
        }

        private static CredentialFacts Empty() => CredentialFacts.FromPhoton("", "", "");

        private static Func<string, string> Env(params string[] set) => name => set.Contains(name) ? "1" : null;

        private static readonly Func<string, string> NoEnvironment = _ => null;

        // ---- the build guard ------------------------------------------------------------------------------------------------------

        [Test]
        public void TheGuardListsEveryMissingIdInOneMessageWithHowToSupplyEach()
        {
            foreach (var target in new[] { BuildTarget.Android, BuildTarget.StandaloneWindows64 })
            {
                var message = RequiredCredentialsBuildGuard.Evaluate(target, Empty(), NoEnvironment);
                Assert.That(message, Is.Not.Null, target.ToString());
                Assert.That(message, Does.Contain("missing 3 required credentials"));
                foreach (var spec in RequiredCredentials.All)
                {
                    Assert.That(message, Does.Contain(spec.Key), "names " + spec.Key);
                    Assert.That(message, Does.Contain(spec.HowToSupply), "says how to supply " + spec.Key);
                    Assert.That(message, Does.Contain(spec.AllowVariable + "=1"), "names the opt-out of " + spec.Key);
                }
                Assert.That(message, Does.Contain("setup_sdks.py --photon-app-id"));
                Assert.That(message, Does.Contain("--photon-voice-app-id"));
                Assert.That(message, Does.Contain("--turn-secret-file"));
            }
        }

        [Test]
        public void TheGuardNamesOnlyTheMissingOnesAndPassesACompleteBuild()
        {
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Full(), NoEnvironment), Is.Null);
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneWindows64, Full(), NoEnvironment), Is.Null);

            var noVoice = Full();
            noVoice.VoiceAppId = CredentialState.Absent;
            var message = RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, noVoice, NoEnvironment);
            Assert.That(message, Does.Contain("missing 1 required credential;"));
            Assert.That(message, Does.Contain("- photon-voice-app-id"));
            Assert.That(message, Does.Not.Contain("- photon-app-id"));
            Assert.That(message, Does.Not.Contain("- turn-client-secret"));
            Assert.That(message, Does.Contain("Not failing: photon-app-id set").And.Contain("turn-client-secret set"));

            var noTurn = Full();
            noTurn.TurnClientSecret = CredentialState.Absent;
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, noTurn, NoEnvironment), Does.Contain("- turn-client-secret"));
            var noFusion = Full();
            noFusion.FusionAppId = CredentialState.Absent;
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, noFusion, NoEnvironment), Does.Contain("- photon-app-id"));
        }

        [Test]
        public void TheGuardNeverPrintsAValue_OnlyPresenceAndLength()
        {
            var facts = CredentialFacts.FromPhoton("not-a-guid-SECRETISH", FakeVoice, "");
            facts.TurnClientSecret = CredentialState.OfText(FakeSecret);
            Assert.That(facts.FusionAppId.Present, Is.False, "a Fusion App ID that is not a GUID is not usable");
            Assert.That(facts.FusionAppId.Problem, Is.EqualTo("not a GUID"));
            var message = RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, facts, NoEnvironment);
            Assert.That(message, Does.Contain("photon-app-id"));
            Assert.That(message, Does.Not.Contain("SECRETISH"));
            Assert.That(message, Does.Not.Contain(FakeVoice));
            Assert.That(message, Does.Not.Contain(FakeSecret));
            Assert.That(message, Does.Contain("set (" + FakeVoice.Length + " characters)"));
            Assert.That(message, Does.Contain("set (" + FakeSecret.Length + " characters)"));
            var summary = RequiredCredentials.SummaryLine(CredentialProfile.Player, facts);
            Assert.That(summary, Does.Not.Contain(FakeSecret).And.Not.Contain(FakeVoice).And.Not.Contain("SECRETISH"));
        }

        [Test]
        public void EachCredentialHasItsOwnOptOutThatWaivesOnlyThatOne()
        {
            Assert.That(RequiredCredentials.Fusion.AllowVariable, Is.EqualTo("SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID"));
            Assert.That(RequiredCredentials.Voice.AllowVariable, Is.EqualTo("SPATIALBLEND_ALLOW_NO_VOICE_APP_ID"), "the existing name is kept");
            Assert.That(RequiredCredentials.Turn.AllowVariable, Is.EqualTo("SPATIALBLEND_ALLOW_NO_TURN"),
                "no SECRET/KEY/TOKEN in the name: tools/lib/unity-env.sh would strip the variable before Unity sees it");
            foreach (var spec in RequiredCredentials.All)
            {
                foreach (var banned in new[] { "SECRET", "KEY", "TOKEN", "PASSWORD" }) StringAssert.DoesNotContain(banned, spec.AllowVariable);
            }

            foreach (var spec in RequiredCredentials.All)
            {
                var message = RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Empty(), Env(spec.AllowVariable));
                Assert.That(message, Does.Contain("missing 2 required credentials"), spec.Key + " waived");
                Assert.That(message, Does.Not.Contain("- " + spec.Key), spec.Key + " waived");
                foreach (var other in RequiredCredentials.All.Where(o => o != spec)) Assert.That(message, Does.Contain("- " + other.Key));
            }
            var all = Env(RequiredCredentials.All.Select(s => s.AllowVariable).ToArray());
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Empty(), all), Is.Null);
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneWindows64, Empty(), all), Is.Null);
        }

        [Test]
        public void OnlyTheValueOneWaives()
        {
            foreach (var value in new[] { "0", "", "true", "yes", " 1", "1 " })
            {
                Func<string, string> env = name => value;
                Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, Empty(), env), Is.Not.Null, "'" + value + "'");
            }
        }

        [Test]
        public void TheSpectatorNeedsNoVoiceButNeedsRoomsAndNeverFailsForTheOptionalScanServer()
        {
            var spectator = Full();
            spectator.VoiceAppId = CredentialState.Absent;
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, spectator, NoEnvironment), Is.Null, "no microphone on the spectator");

            var noFusion = Full();
            noFusion.FusionAppId = CredentialState.Absent;
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, noFusion, NoEnvironment), Does.Contain("- photon-app-id"));

            var noTurn = Full();
            noTurn.TurnClientSecret = CredentialState.Absent;
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, noTurn, NoEnvironment), Does.Contain("- turn-client-secret"));
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, noTurn, NoEnvironment, turnWaivedByArgument: true), Is.Null,
                "build-spectator-mac.sh --no-turn-secret leaves the secret out on purpose");
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, noFusion, NoEnvironment, turnWaivedByArgument: true), Does.Contain("- photon-app-id"),
                "the TURN flag waives only TURN");
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.StandaloneOSX, noTurn, Env(RequiredCredentials.Turn.AllowVariable)), Is.Null);
        }

        [Test]
        public void OtherTargetsAreNotChecked_AndTurnThatIsDeliberatelyOffIsNotRequired()
        {
            foreach (var target in new[] { BuildTarget.iOS, BuildTarget.StandaloneLinux64, BuildTarget.WebGL })
            {
                Assert.That(RequiredCredentialsBuildGuard.ProfileFor(target), Is.Null, target.ToString());
                Assert.That(RequiredCredentialsBuildGuard.Evaluate(target, Empty(), NoEnvironment), Is.Null, target.ToString());
            }
            Assert.That(RequiredCredentialsBuildGuard.ProfileFor(BuildTarget.Android), Is.EqualTo(CredentialProfile.Player));
            Assert.That(RequiredCredentialsBuildGuard.ProfileFor(BuildTarget.StandaloneWindows64), Is.EqualTo(CredentialProfile.Player));
            Assert.That(RequiredCredentialsBuildGuard.ProfileFor(BuildTarget.StandaloneOSX), Is.EqualTo(CredentialProfile.Spectator));

            var off = Full();
            off.TurnClientSecret = CredentialState.Absent;
            off.TurnRequired = false;
            Assert.That(RequiredCredentialsBuildGuard.Evaluate(BuildTarget.Android, off, NoEnvironment), Is.Null, "an empty Worker URL or WebRTC off is TURN off on purpose");

            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(RequiredCredentialsRuntime.TurnIsWanted(null), Is.True, "no settings asset: required");
                Assert.That(RequiredCredentialsRuntime.TurnIsWanted(settings), Is.False, "no Worker URL by default: TURN off, so not required");
                settings.turnCredentialsUrl = "https://turn.example.invalid/ice";
                Assert.That(RequiredCredentialsRuntime.TurnIsWanted(settings), Is.True, "your own Worker URL and WebRTC on");
                settings.useWebRtc = false;
                Assert.That(RequiredCredentialsRuntime.TurnIsWanted(settings), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // ---- the build stamp ------------------------------------------------------------------------------------------------------

        [Test]
        public void TheStampRecordsWhatTheBuildReallyHas_AndKeepsTheVoiceKeyOfIssue167()
        {
            var full = RequiredCredentials.StampMembers(Full());
            Assert.That(full, Is.EqualTo("\"voiceAppIdConfigured\":true,\"photonAppIdConfigured\":true,\"turnClientSecretConfigured\":true,\"turnRequired\":true"));

            var none = RequiredCredentials.StampMembers(Empty());
            Assert.That(none, Is.EqualTo("\"voiceAppIdConfigured\":false,\"photonAppIdConfigured\":false,\"turnClientSecretConfigured\":false,\"turnRequired\":true"),
                "an opted-out build stamps the truth, so check_apk_contents.py rejects its APK");

            var off = Empty();
            off.TurnRequired = false;
            Assert.That(RequiredCredentials.StampMembers(off), Does.EndWith("\"turnRequired\":false"));

            // The stamp is valid JSON as TmpEssentialsBuildGuard assembles it.
            var json = "{\"tmpSettings\":\"x\"," + full + "}";
            Assert.That(JsonUtility.FromJson<StampProbe>(json).photonAppIdConfigured, Is.True);
            Assert.That(JsonUtility.FromJson<StampProbe>("{" + none + "}").voiceAppIdConfigured, Is.False);
        }

        [Serializable]
        private sealed class StampProbe
        {
            public bool voiceAppIdConfigured, photonAppIdConfigured, turnClientSecretConfigured, turnRequired;
        }

        // ---- the runtime log -------------------------------------------------------------------------------------------------------

        [Test]
        public void StartupLogsOneStructuredErrorPerMissingRequiredIdAndNothingWhenComplete()
        {
            var errors = new List<string>();
            var infos = new List<string>();
            var missing = RequiredCredentialsRuntime.Report(CredentialProfile.Player, Empty(), infos.Add, errors.Add);
            Assert.That(missing.Select(s => s.Key), Is.EqualTo(new[] { "photon-app-id", "photon-voice-app-id", "turn-client-secret" }));
            Assert.That(errors, Has.Count.EqualTo(3));
            Assert.That(errors[0], Does.StartWith("[credentials] missing id=photon-app-id required=true"));
            Assert.That(errors[1], Does.StartWith("[credentials] missing id=photon-voice-app-id required=true"));
            Assert.That(errors[2], Does.StartWith("[credentials] missing id=turn-client-secret required=true"));
            foreach (var error in errors) Assert.That(error, Does.Contain("impact=").And.Contain("fix="));
            Assert.That(infos, Has.Count.EqualTo(1));
            Assert.That(infos[0], Does.Contain("photon-voice-app-id=MISSING"));

            errors.Clear();
            infos.Clear();
            Assert.That(RequiredCredentialsRuntime.Report(CredentialProfile.Player, Full(), infos.Add, errors.Add), Is.Empty);
            Assert.That(errors, Is.Empty, "a complete build logs no error");
            Assert.That(infos.Single(), Does.Contain("photon-voice-app-id=set (36 characters)"));
            Assert.That(infos.Single(), Does.Not.Contain(FakeVoice));

            errors.Clear();
            var spectatorMissing = RequiredCredentialsRuntime.Report(CredentialProfile.Spectator, Empty(), null, errors.Add);
            Assert.That(spectatorMissing.Select(s => s.Key), Is.EqualTo(new[] { "photon-app-id", "turn-client-secret" }), "the spectator has no voice");
        }

        [Test]
        public void ThePlatformPicksTheProfile()
        {
            Assert.That(RequiredCredentialsRuntime.ProfileFor(RuntimePlatform.Android), Is.EqualTo(CredentialProfile.Player));
            Assert.That(RequiredCredentialsRuntime.ProfileFor(RuntimePlatform.WindowsPlayer), Is.EqualTo(CredentialProfile.Player));
            Assert.That(RequiredCredentialsRuntime.ProfileFor(RuntimePlatform.OSXPlayer), Is.EqualTo(CredentialProfile.Spectator));
        }

        // ---- the in-app notice -----------------------------------------------------------------------------------------------------

        [Test]
        public void ThePanelNoticeAppearsForAMissingIdAndNotWhenAllArePresent()
        {
            var noVoice = Full();
            noVoice.VoiceAppId = CredentialState.Absent;
            var notices = CredentialNoticePresenter.Describe(CredentialProfile.Player, noVoice, true);
            Assert.That(notices, Has.Count.EqualTo(1));
            Assert.That(notices[0].Tone, Is.EqualTo(StatusTone.Bad));
            Assert.That(notices[0].Title, Is.EqualTo("Voice disabled: no Photon Voice App ID in this build"));
            Assert.That(notices[0].Message, Is.Not.Empty);

            var all = CredentialNoticePresenter.Describe(CredentialProfile.Player, Empty(), true);
            Assert.That(all.Select(n => n.Key), Is.EqualTo(new[] { "photon-app-id", "photon-voice-app-id", "turn-client-secret" }));
            Assert.That(all.All(n => n.Tone == StatusTone.Bad));

            Assert.That(CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), true), Is.Empty, "no notice for a complete build");
        }

        [Test]
        public void TheOptionalScanServerShowsAStatusNeverAnError()
        {
            var off = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), false);
            Assert.That(off, Has.Count.EqualTo(1));
            Assert.That(off[0].Key, Is.EqualTo("scan-server"));
            Assert.That(off[0].Tone, Is.EqualTo(StatusTone.Neutral));
            Assert.That(off[0].Title, Is.EqualTo(CredentialNoticePresenter.ScanOffTitle));

            // With everything required missing and no scan server, the scan line is still neutral and never a Bad one.
            var mixed = CredentialNoticePresenter.Describe(CredentialProfile.Player, Empty(), false);
            Assert.That(mixed.Single(n => n.Key == "scan-server").Tone, Is.EqualTo(StatusTone.Neutral));
            Assert.That(RequiredCredentials.Missing(CredentialProfile.Player, Full()), Is.Empty, "the scan server is not a required credential");
            Assert.That(RequiredCredentials.All.Select(s => s.Key), Has.None.Contain("scan"));

            // There is no shipped default: no address and no device file is the no-server case. A settings or local address turns it on.
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(CredentialNoticePresenter.ScanServerConfigured(settings, LocalSessionConfig.None), Is.False);
                settings.sessionServerAddress = "scan-host.example:443";
                Assert.That(CredentialNoticePresenter.ScanServerConfigured(settings, LocalSessionConfig.None), Is.True);
                settings.sessionServerAddress = "";
                settings.sessionFallbackServerAddress = "";
                Assert.That(CredentialNoticePresenter.ScanServerConfigured(settings, LocalSessionConfig.None), Is.False);
                Assert.That(CredentialNoticePresenter.ScanServerConfigured(null, LocalSessionConfig.None), Is.False);
                var local = LocalSessionConfig.Parse("{\"sessionServerAddress\":\"scan-host.example:50000\"}", null);
                Assert.That(CredentialNoticePresenter.ScanServerConfigured(settings, local), Is.True);
                settings.sessionServerAddress = "scan-host.example:50000";
                Assert.That(CredentialNoticePresenter.ScanServerConfigured(settings, LocalSessionConfig.None), Is.True);
                Assert.That(CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), true), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ALocalAddressOrARunningScanServiceCountsWhenTheSettingsAssetIsMissing()
        {
            // Issue 244. ScanServerConfigured used to return false as soon as the settings asset was missing, so the panel
            // said "Scanning is off" even when spatialblend-local.json named a server or a scan service had already started.
            // Camera permission is a different check and does not change this line.
            var local = LocalSessionConfig.Parse("{\"sessionServerAddress\":\"scan-host.example:50000\"}", null);
            Assert.That(CredentialNoticePresenter.ScanServerConfigured(null, local), Is.True,
                "a local address counts even when the settings asset is not ready");
            Assert.That(CredentialNoticePresenter.ScanServerConfigured(null, LocalSessionConfig.None, scanServiceRunning: true), Is.True,
                "a scan service that already started counts as configured");

            var notices = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(),
                CredentialNoticePresenter.ScanServerConfigured(null, local));
            Assert.That(notices.Select(n => n.Title), Has.None.EqualTo(CredentialNoticePresenter.ScanOffTitle));

            var fallbackOnly = LocalSessionConfig.Parse("{\"sessionFallbackServerAddress\":\"scan-two.example:50001\"}", null);
            Assert.That(CredentialNoticePresenter.ScanServerConfigured(null, fallbackOnly), Is.False,
                "a fallback address alone does not turn scanning on");
            Assert.That(CredentialNoticePresenter.ScanServerConfigured(null, LocalSessionConfig.None, scanServiceRunning: false), Is.False);
        }

        [Test]
        public void TheScanLineSaysWhyScanningIsOff()
        {
            Assert.That(CredentialNoticePresenter.Classify(false, false, false, false), Is.EqualTo(ScanOffReason.NoServer));
            Assert.That(CredentialNoticePresenter.Classify(false, false, false, true), Is.EqualTo(ScanOffReason.NoServer),
                "no address is the reason even when the camera is also denied");
            Assert.That(CredentialNoticePresenter.Classify(true, false, true, false), Is.EqualTo(ScanOffReason.Unreachable));
            Assert.That(CredentialNoticePresenter.Classify(true, true, false, true), Is.EqualTo(ScanOffReason.PermissionDenied));
            Assert.That(CredentialNoticePresenter.Classify(true, true, false, false), Is.EqualTo(ScanOffReason.None));
            Assert.That(CredentialNoticePresenter.IsConnectFailure("ConnectFailed: No session could be opened."), Is.True);
            Assert.That(CredentialNoticePresenter.IsConnectFailure(
                "No session server address in SpatialBlendSettings or the local config. Scanning is off."), Is.False);
            const string tlsFault = "TransportFault: TLS needs a newer session library";
            const string initFault = "InitializeWith needs a transport, a file system and a config.";
            Assert.That(CredentialNoticePresenter.IsConnectFailure(tlsFault), Is.False);
            Assert.That(CredentialNoticePresenter.IsConnectFailure(initFault), Is.False);
            Assert.That(CredentialNoticePresenter.Classify(true, false, tlsFault, false), Is.EqualTo(ScanOffReason.Fault));
            Assert.That(CredentialNoticePresenter.Classify(true, false, initFault, false), Is.EqualTo(ScanOffReason.Fault));
            var tlsLine = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), ScanOffReason.Fault, tlsFault);
            Assert.That(tlsLine.Single().Message, Is.EqualTo(tlsFault));
            Assert.That(tlsLine.Single().Message, Is.Not.EqualTo(CredentialNoticePresenter.UnreachableMessage));
            var initLine = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), ScanOffReason.Fault, initFault);
            Assert.That(initLine.Single().Message, Is.EqualTo(initFault));
            Assert.That(initLine.Single().Message, Does.Not.Contain("Wi-Fi"));

            var noServer = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), ScanOffReason.NoServer);
            Assert.That(noServer.Single(n => n.Key == "scan-server").Message, Is.EqualTo(CredentialNoticePresenter.NoServerMessage));
            Assert.That(noServer.Single(n => n.Key == "scan-server").Tone, Is.EqualTo(StatusTone.Neutral));

            var denied = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), ScanOffReason.PermissionDenied);
            Assert.That(denied.Single().Title, Is.EqualTo(CredentialNoticePresenter.ScanOffTitle));
            Assert.That(denied.Single().Message, Is.EqualTo(CredentialNoticePresenter.PermissionMessage));

            var down = CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), ScanOffReason.Unreachable);
            Assert.That(down.Single().Key, Is.EqualTo("scan-unreachable"));
            Assert.That(down.Single().Message, Is.EqualTo(CredentialNoticePresenter.UnreachableMessage));
            Assert.That(down.Single().Message, Is.Not.EqualTo(CredentialNoticePresenter.NoServerMessage));
        }

        [Test]
        public void TheGeneratedPanelShowsTheNoticesAndHidesTheBlockWhenThereAreNone()
        {
            var root = new GameObject("view-canvas", typeof(RectTransform), typeof(Canvas));
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                var view = ControlPanelView.Build((RectTransform)root.transform, UiTheme.PanelWidth, new ControlPanelActions(),
                    new AvatarSettingsPresenter(() => settings, () => { }), new SceneControlsPresenter(null, null, null, null));
                Assert.That(view.NoticesBlock.gameObject.activeSelf, Is.False, "nothing to say by default");

                var noVoice = Full();
                noVoice.VoiceAppId = CredentialState.Absent;
                view.SetNotices(CredentialNoticePresenter.Describe(CredentialProfile.Player, noVoice, false));
                Assert.That(view.NoticesBlock.gameObject.activeSelf, Is.True);
                var texts = view.NoticesBlock.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t => t.text).ToList();
                Assert.That(texts, Does.Contain("Voice disabled: no Photon Voice App ID in this build"));
                Assert.That(texts, Does.Contain(CredentialNoticePresenter.ScanOffTitle));

                view.SetNotices(CredentialNoticePresenter.Describe(CredentialProfile.Player, Full(), true));
                Assert.That(view.NoticesBlock.gameObject.activeSelf, Is.False);
                Assert.That(view.NoticesBlock.GetComponentsInChildren<TMPro.TMP_Text>(true), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
#endif
