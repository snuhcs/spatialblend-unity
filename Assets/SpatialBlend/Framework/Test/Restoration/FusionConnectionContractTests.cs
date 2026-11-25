// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Fusion;
using Fusion.Sockets;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;
using UnityEngine.TestTools;
using FakeTunnelManager = SpatialBlend.Tests.TunnelFixtures.FakeTunnelManager;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FusionConnectionContractTests
    {
        private GameObject _object;
        private GameObject _managerObject;
        private TestConnectionManager _connection;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("fusion-readiness-test");
            _connection = _object.AddComponent<TestConnectionManager>();
            _connection.Roster.Add(_connection.Local);
            _connection.Roster.Add(_connection.Peer);
            _managerObject = new GameObject("fusion-readiness-core");
            _managerObject.SetActive(false);
            _connection.Core = _managerObject.AddComponent<FakeTunnelManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_connection.Core, false);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_object);
            _managerObject.GetComponent<SpatialBlendManager>()?.OnDestroy();
            UnityEngine.Object.DestroyImmediate(_managerObject);
        }

        // The 15 s deadline and the 2-player start gate are declared in
        // SpatialBlendSettings, with the previous constants as the defaults. So is the
        // player cap, which is handed to Fusion as StartGameArgs.PlayerCount.
        [Test]
        public void DeclaredConfigDefaultsAreThePreviousConstants()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                NUnitAssert.That(settings.connectDeadlineSeconds, Is.EqualTo(15f));
                NUnitAssert.That(settings.playersToStartApps, Is.EqualTo(2));
                NUnitAssert.That(settings.maxPlayers, Is.EqualTo(9));
                NUnitAssert.That(settings.scanSegmentTimeoutSeconds, Is.EqualTo(30f));
                NUnitAssert.That(settings.scanReconstructTimeoutSeconds, Is.EqualTo(300f));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void TheDeadlineFollowsTheDeclaredConfig()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                settings.connectDeadlineSeconds = 30f;
                _connection.Config = settings;
                _connection.Authority = false;
                _connection.BeginRemote(4);
                _connection.NowValue = 20; // past the default 15, inside the declared 30
                _connection.Tick();
                NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
                _connection.NowValue = 30;
                _connection.Tick();
                NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        // Out-of-range declared config is not replaced by its default silently.
        [Test]
        public void AnOutOfRangeConnectDeadlineIsAVisibleErrorAndTheDefaultIsUsed()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                settings.connectDeadlineSeconds = 0.2f;
                _connection.Config = settings;
                _connection.Authority = false;
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("connectDeadlineSeconds = 0.2 is out of range; using the default 15"));
                _connection.BeginRemote(4);
                _connection.NowValue = 14.9; _connection.Tick();
                NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0), "the default 15 s applies");
                _connection.NowValue = 15; _connection.Tick();
                NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void AnOutOfRangeStartGateAndIceTimeoutAreVisibleErrorsReportedOnce()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            var probeObject = new GameObject("config-error-probe");
            try
            {
                settings.playersToStartApps = 0; settings.webRtcIceTimeoutSeconds = 0f;
                _connection.Config = settings;
                // The connection reads the ICE timeout when it makes the host's link, then the start gate when it counts players.
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("webRtcIceTimeoutSeconds = 0 is out of range; using the default 5"));
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("playersToStartApps = 0 is out of range; using the default 2"));
                _connection.OnPlayerJoined(null, _connection.Local);
                _connection.Ack(_connection.Epoch, _connection.Peer);
                NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1), "the default 2 players start it");

                var probe = probeObject.AddComponent<WebRtcConfigProbe>();
                probe.Config = settings;
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("webRtcIceTimeoutSeconds = 0 is out of range; using the default 5"));
                NUnitAssert.That(probe.IceTimeout, Is.EqualTo(5f));
                NUnitAssert.That(probe.IceTimeout, Is.EqualTo(5f), "reported once, not every read");
            }
            finally { UnityEngine.Object.DestroyImmediate(probeObject); UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void AnOutOfRangePlayerCapIsAVisibleErrorAndNoSettingsIsNot()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                settings.maxPlayers = 1;
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("maxPlayers = 1 is out of range"));
                NUnitAssert.That(SbFusionBootstrap.ResolvePlayerCount(settings), Is.EqualTo(SpatialBlendSettings.DefaultMaxPlayers));
                NUnitAssert.That(SbFusionBootstrap.ResolvePlayerCount(null), Is.EqualTo(SpatialBlendSettings.DefaultMaxPlayers), "no settings is not an error");
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void TheStartGateFollowsTheDeclaredConfig()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                settings.playersToStartApps = 3;
                _connection.Config = settings;
                _connection.OnPlayerJoined(null, _connection.Local);
                _connection.Ack(_connection.Epoch, _connection.Peer);
                NUnitAssert.That(_connection.StartCalls, Is.EqualTo(0), "two ready players are short of the declared three");
                var third = PlayerRef.FromEncoded(3);
                _connection.Roster.Add(third);
                _connection.Ack(_connection.Epoch, third);
                NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        // A host that leaves is surfaced in the UI as "host left"; nothing
        // re-hosts. There is no Fusion runner without a Photon server, so the runner's OnShutdown event (the scene
        // wires it to ShutdownConnection) is driven here at the app level.
        private static MatchmakingDecision Decision(CoreParticipantRole role) =>
            new MatchmakingDecision(true, MatchmakingPhase.InSession, role, "Demo", null, null);

        [Test]
        public void AShutdownUnderAJoinedClientMeansTheHostLeft()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            NUnitAssert.That(_connection.HostLeft, Is.False);

            _connection.ShutdownConnection(); // the runner's OnShutdown

            NUnitAssert.That(_connection.HostLeft, Is.True);
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0), "nothing re-hosts");
        }

        [Test]
        public void TheHostPlayerLeavingTheRoomMeansTheHostLeft()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.BeginRemote(4);
            _connection.OnPlayerLeft(null, PlayerRef.FromEncoded(77)); // somebody else
            NUnitAssert.That(_connection.HostLeft, Is.False);

            _connection.OnPlayerLeft(null, _connection.Peer); // the host's player

            NUnitAssert.That(_connection.HostLeft, Is.True);
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(_connection.DisconnectCalls, Is.GreaterThan(0), "the core link comes down");
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0), "nothing re-hosts");
        }

        // Host loss is decided by WHO left (the host's player), never by who is master afterwards. Fusion
        // promotes a remaining player to master the moment the host leaves, so that player is the authority exactly when
        // the host loss has to be noticed.
        [Test]
        public void AJoinerPromotedToMasterByTheHostLeavingStillSeesTheHostLeft()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.BeginRemote(4);
            _connection.Authority = true; // the departure made this joiner the Shared-mode master

            _connection.OnPlayerLeft(null, _connection.Peer); // the host's player

            NUnitAssert.That(_connection.HostLeft, Is.True);
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(_connection.DisconnectCalls, Is.GreaterThan(0), "the core link comes down");
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0), "nothing re-hosts");
        }

        [Test]
        public void AJoinerThatIsMasterIgnoresAnotherJoinersDeparture()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.BeginRemote(4);
            _connection.Authority = true;

            _connection.OnPlayerLeft(null, PlayerRef.FromEncoded(77)); // somebody who was never linked to this joiner

            NUnitAssert.That(_connection.HostLeft, Is.False);
            NUnitAssert.That(_connection.IsRetired(4), Is.False, "a joiner never retires the host's round");
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1), "the link to the host is untouched");
        }

        [Test]
        public void ACoreLinkThatEndsUnderAConnectedClientMeansTheHostLeft()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.BeginRemote(4);
            _connection.Core.JoinCompleted = true;
            _connection.Core.PollTunneledJoin();
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.Tick();
            NUnitAssert.That(_connection.HostLeft, Is.False);

            _connection.Core.Pipes[_connection.Core.LastJoinLink].Ended = true; // core closed it
            _connection.Tick();

            NUnitAssert.That(_connection.HostLeft, Is.True);
        }

        [Test]
        public void TheHostLeftNoteAppearsInTheSessionPanel()
        {
            var panelObject = new GameObject("control-panel");
            panelObject.SetActive(false);
            try
            {
                var panel = panelObject.AddComponent<ControlPanel>();
                typeof(ControlPanel).GetField("_fusionConnectionManager", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(panel, _connection);
                _connection.Authority = false;
                _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
                // This panel has no space manager, so the native library counts as not compatible: the blocking message shows
                // (the user is told "This app needs an update", never a role or link wording).
                NUnitAssert.That(panel.RefreshSessionStatus().Headline, Is.EqualTo(SessionStatusPresenter.NeedsUpdateHeadline));

                _connection.ShutdownConnection();

                SessionStatusView view = panel.RefreshSessionStatus();
                NUnitAssert.That(view.Headline, Is.EqualTo(SessionStatusPresenter.HostLeftHeadline));
                NUnitAssert.That(view.Tone, Is.EqualTo(StatusTone.Bad));
                NUnitAssert.That(view.ConnectLabel, Is.EqualTo("Back to the lobby"), "the runner may still be up: the primary action is a leave");
                NUnitAssert.That(view.ConnectEnabled, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(panelObject); }
        }

        [Test]
        public void AParticipantsOwnLeaveIsNotAHostLoss()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.None)); // Leave
            NUnitAssert.That(_connection.HostLeft, Is.False);
            _connection.ShutdownConnection(); // the runner's shutdown that follows the leave
            NUnitAssert.That(_connection.HostLeft, Is.False);
        }

        [Test]
        public void AHostShuttingDownIsNotAHostLoss()
        {
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Host));
            _connection.ShutdownConnection();
            NUnitAssert.That(_connection.HostLeft, Is.False);
        }

        [Test]
        public void JoiningAnotherRoomClearsTheHostLeftNote()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.ShutdownConnection();
            NUnitAssert.That(_connection.HostLeft, Is.True);

            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));

            NUnitAssert.That(_connection.HostLeft, Is.False);
        }

        [Test]
        public void HostStartsDisconnectedAndStartsAppsOnlyAfterEveryActiveAck()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(_connection.Epoch, Is.GreaterThan(0));
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(0));
            _connection.Ack(_connection.Epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
        }

        [Test]
        public void ClientJoinsBeforeAcknowledgingAuthorityRound()
        {
            _connection.Authority = false;
            _connection.BeginRemote(4);
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(1));
            NUnitAssert.That(_connection.ReadyCalls, Is.EqualTo(0));
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.Tick();
            NUnitAssert.That(_connection.ReadyCalls, Is.EqualTo(1));
        }

        [Test]
        public void FailedHostDoesNotCountReadyOrStartApps()
        {
            _connection.HostResult = SpatialBlendConnectionState.Failed;
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.IsRetired(_connection.Epoch), Is.True);
        }

        [Test]
        public void DeadlineDisconnectsPendingClientRound()
        {
            _connection.Authority = false;
            _connection.BeginRemote(4);
            _connection.NowValue = 20;
            _connection.Tick();
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(1));
        }

        [Test]
        public void ExactDeadlineWinsOverLateConnectedStateWithoutAcknowledgement()
        {
            _connection.Authority = false;
            _connection.BeginRemote(4);
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.NowValue = 15;
            _connection.Tick();
            NUnitAssert.That(_connection.ReadyCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(1));
        }

        [Test]
        public void ExactDeadlinePreventsDeferredReconnectRequest()
        {
            _connection.Authority = false;
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.BeginRemote(4);
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(1));
            _connection.State = SpatialBlendConnectionState.Disconnected;
            _connection.NowValue = 15;
            _connection.Tick();
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.ReadyCalls, Is.EqualTo(0));
        }

        [Test]
        public void NewRemoteEpochDisconnectsOldTransportBeforeRejoin()
        {
            _connection.Authority = false;
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.BeginRemote(4);
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(1));
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(0));
            _connection.State = SpatialBlendConnectionState.Disconnected;
            _connection.Tick();
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateJoinAndStartAreOneShotPerEpoch()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            _connection.BeginRemote(epoch);
            _connection.DeliverStart(epoch);
            _connection.DeliverStart(epoch);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
        }

        [Test]
        public void LateJoinedReadyPlayerRebroadcastsStartWithoutRestartingApps()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            var late = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(late);
            _connection.OnPlayerJoined(null, late);
            _connection.Ack(epoch, late);
            _connection.Ack(epoch, late);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(2));
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
        }

        // The start announcement is deduplicated on WHO was announced to. It was on how many, so a leave followed by a join
        // (the roster size back where it was) never announced the start to the newcomer, whose core linked but whose apps
        // never started.
        [Test]
        public void StartAnnouncementIsNotRepeatedForAnUnchangedRoster()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            _connection.Ack(epoch, _connection.Peer);
            _connection.OnPlayerJoined(null, _connection.Peer); // a duplicate delivery of the same join
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1));
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
        }

        [Test]
        public void ALeaveFollowedByAJoinOfTheSameRosterSizeAnnouncesTheStartAgain()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1));

            _connection.Roster.Remove(_connection.Peer);
            _connection.OnPlayerLeft(null, _connection.Peer);
            _connection.OnPlayerLeft(null, _connection.Peer); // NetworkEvents and IPlayerLeft may both deliver it
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False, "a round whose apps started keeps serving the others");
            var replacement = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(replacement);
            _connection.OnPlayerJoined(null, replacement);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1), "not before the newcomer is ready");
            _connection.Ack(epoch, replacement);
            _connection.Ack(epoch, replacement);

            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(2), "the newcomer is announced to exactly once");
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1), "the host's own apps are not restarted");
        }

        [Test]
        public void APlayerWhoLeavesAndRejoinsUnderTheSameRefMustAckAgainAndIsAnnouncedTo()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);

            _connection.Roster.Remove(_connection.Peer);
            _connection.OnPlayerLeft(null, _connection.Peer);
            _connection.Roster.Add(_connection.Peer);
            _connection.OnPlayerJoined(null, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1), "the earlier ack is the leaver's, not the rejoiner's");
            _connection.Ack(epoch, _connection.Peer);

            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(2));
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
        }

        [Test]
        public void RetiredEpochRejectsLateJoinStartAndAck()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.ShutdownConnection();
            _connection.BeginRemote(epoch);
            _connection.DeliverStart(epoch);
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(0));
        }

        [Test]
        public void DelayedOlderRetirementDoesNotRetireActiveRound()
        {
            _connection.SetEpochs(epoch: 5, retiredEpoch: 2);
            _connection.Retire(3);
            NUnitAssert.That(_connection.Epoch, Is.EqualTo(5));
            NUnitAssert.That(_connection.IsRetired(5), Is.False);
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
        }

        [Test]
        public void DepartedUnreadyPeerRetiresAuthorityRound()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.OnPlayerLeft(null, _connection.Peer);
            NUnitAssert.That(_connection.IsRetired(epoch), Is.True);
        }

        [Test]
        public void AnObserverDoesNotCountTowardStartingAppsOrRetireTheRoundOnLeave()
        {
            var watcher = PlayerRef.FromEncoded(9);
            _connection.Roster.Add(watcher);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.NoteObserver(watcher);
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1), "the observer is not a player the room waits for");

            _connection.Roster.Remove(watcher);
            _connection.OnPlayerLeft(null, watcher);
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False, "an observer leaving does not retire the participants' round");
        }

        [Test]
        public void AHostLeavesAnAnnouncedSpectatorOutOfItsStartAndItsLeaveKeepsTheRound()
        {
            // The desktop spectator announces itself by the observer presence RPC (FreeformSpectatorSession.TickSpectating).
            var spectator = PlayerRef.FromEncoded(11);
            _connection.Roster.Add(spectator);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.ReceiveObserverPresence(spectator);
            NUnitAssert.That(_connection.IsKnownObserver(spectator), Is.True);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(0), "no app-start before the participants' own start");
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1), "the participants start without the spectator's acknowledgement");

            _connection.Roster.Remove(spectator);
            _connection.OnPlayerLeft(null, spectator);
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False, "the spectator's leave does not retire the participants' round");
        }

        [Test]
        public void ALateSpectatorIsSentTheStartedAppsOnceByTheHost()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1));
            // The spectator joins the started room (it waits for the participants) and announces itself every second.
            var spectator = PlayerRef.FromEncoded(12);
            _connection.Roster.Add(spectator);
            _connection.ReceiveObserverPresence(spectator);
            _connection.ReceiveObserverPresence(spectator);
            _connection.ReceiveObserverPresence(spectator);
            NUnitAssert.That(_connection.ObserverStarts, Is.EqualTo(new[] { spectator }), "the host sends the started round to it once");
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False);
        }

        [Test]
        public void AnObserverCannotCreateARoom()
        {
            var match = new FreeformMatchmaking();
            match.Refresh(new[] { new MatchmakingRoom("Room 1", 1, 9) });
            match.SetHostingAllowed(false);
            var hosted = match.TryHost("Room 2");
            NUnitAssert.That(hosted.Accepted, Is.False);
            NUnitAssert.That(hosted.Role, Is.Not.EqualTo(CoreParticipantRole.Host));
            NUnitAssert.That(hosted.Error, Is.EqualTo(FreeformMatchmaking.ObserverHostError));
            NUnitAssert.That(match.Phase, Is.EqualTo(MatchmakingPhase.Browsing));
            NUnitAssert.That(match.TryJoin("Room 1").Accepted, Is.True, "an observer can still join a room that exists");
        }

        [Test]
        public void AnObserverWhoLeavesDoesNotMakeAParticipantHost()
        {
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.Authority = true;
            var watcher = PlayerRef.FromEncoded(9);
            _connection.Roster.Add(watcher);
            _connection.ReceiveObserverPresence(watcher);
            _connection.Roster.Remove(watcher);
            _connection.OnPlayerLeft(null, watcher);
            _connection.OnPlayerLeft(null, watcher);
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void AClientWhoBecomesMasterWithNoObserverDoesNotHost()
        {
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.Authority = true;
            _connection.Tick();
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void AnObserverRecordsAnotherObserverWithoutStartingOrRouting()
        {
            _connection.DeclareLocalObserver(true);
            int announced = 0;
            _connection.ObserverAnnounced += _ => announced++;
            var other = PlayerRef.FromEncoded(9);
            _connection.ReceiveObserverPresence(other);
            _connection.ReceiveObserverPresence(other);
            NUnitAssert.That(_connection.IsKnownObserver(other), Is.True);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(0));
            NUnitAssert.That(announced, Is.EqualTo(0));
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
        }

        [Test]
        public void AnObserverMasterStepsAsideWithoutHosting()
        {
            _connection.DeclareLocalObserver(true);
            _connection.Tick();
            NUnitAssert.That(_connection.RunnerShutdownCalls, Is.EqualTo(1));
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
        }

        [Test]
        public void PresenceNamesThePhotonSenderAndSticksUntilLeave()
        {
            var watcher = PlayerRef.FromEncoded(9);
            _connection.ReceiveObserverPresence(_connection.Local);
            NUnitAssert.That(_connection.IsKnownObserver(_connection.Local), Is.False, "the local player is not an announced observer");
            _connection.ReceiveObserverPresence(watcher);
            NUnitAssert.That(_connection.IsKnownObserver(watcher), Is.True);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1));
            // A second callback, after the record was consumed, is still their leave.
            _connection.Roster.Remove(watcher);
            _connection.OnPlayerLeft(null, watcher);
            _connection.OnPlayerLeft(null, watcher);
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False, "an observer who never acked does not retire the round");
            NUnitAssert.That(_connection.IsKnownObserver(watcher), Is.False);
        }

        [Test]
        public void ALateObserverIsToldToStartAppsThatAlreadyStarted()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
            var watcher = PlayerRef.FromEncoded(9);
            _connection.Roster.Add(watcher);
            _connection.ReceiveObserverPresence(watcher);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(1));
            NUnitAssert.That(_connection.ObserverStarts[0], Is.EqualTo(watcher));
            _connection.ReceiveObserverPresence(watcher);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(1), "presence repeats; the app-start does not");
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1), "the host's apps are not started again");
            NUnitAssert.That(_connection.StartBroadcastCalls, Is.EqualTo(1), "the participants are not told again");
            _connection.MarkAppsStarted(epoch + 1);
            _connection.ReceiveObserverPresence(watcher);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(2), "a new round sends the start once more");
        }

        [Test]
        public void ANonHostDoesNotSendTheObserverAppStart()
        {
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.Authority = true;
            _connection.MarkAppsStarted(4);
            var watcher = PlayerRef.FromEncoded(9);
            _connection.ReceiveObserverPresence(watcher);
            _connection.ReceiveObserverPresence(watcher);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(0), "a client, even the Fusion master, does not send the host RPC");

            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Host));
            _connection.ReceiveObserverPresence(watcher);
            NUnitAssert.That(_connection.ObserverStarts.Count, Is.EqualTo(0), "a peer who is not the Fusion authority does not send it either");
        }

        [Test]
        public void AnObserverAppliesTheStartOnceItsCoreLinkIsUp()
        {
            _connection.DeclareLocalObserver(true);
            _connection.Authority = false;
            _connection.State = SpatialBlendConnectionState.Disconnected;
            _connection.BeginRemote(4);
            _connection.DeliverStart(4);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(0), "the start arrived before the core link was up");
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.Tick();
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
            _connection.DeliverStart(4);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1), "the same epoch does not start the apps twice");
        }

        [Test]
        public void ClientRetryRequestsAuthorityInsteadOfAcknowledgingRetiredRound()
        {
            _connection.Authority = false;
            _connection.BeginRemote(4);
            _connection.ShutdownConnection();
            _connection.RetryConnection();
            NUnitAssert.That(_connection.RelinkRequests, Is.EqualTo(1));
            NUnitAssert.That(_connection.ReadyCalls, Is.EqualTo(0));
        }

        [Test]
        public void BootstrapClientCanRequestAuthorityRetryAtEpochZero()
        {
            _connection.Authority = false;
            _connection.RetryConnection();
            NUnitAssert.That(_connection.RelinkRequests, Is.EqualTo(1));
        }

        [Test]
        public void StaleOrSourceFreeRetryCannotInterruptCurrentAuthorityRound()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.RetryRpc(epoch - 1, _connection.Peer);
            _connection.RetryRpc(epoch, PlayerRef.None);
            NUnitAssert.That(_connection.Epoch, Is.EqualTo(epoch));
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(1));
        }

        [Test]
        public void SourceFreeOrUnknownReadyDoesNotStartApps()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            _connection.Ack(_connection.Epoch, PlayerRef.None);
            _connection.Ack(_connection.Epoch, PlayerRef.FromEncoded(99));
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(0));
        }

        [Test]
        public void FailedJoinKeepsFailureVisibleWithoutDisconnect()
        {
            _connection.Authority = false;
            _connection.BeginRemote(4);
            _connection.State = SpatialBlendConnectionState.Failed;
            _connection.Tick();
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(1), "a join is one try: a retry asks the host for a new link");
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.State, Is.EqualTo(SpatialBlendConnectionState.Failed));
            NUnitAssert.That(_connection.IsRetired(4), Is.False, "the round is the host's; this joiner's failure does not end it");
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(0), "the failed join's link is gone");
        }

        [Test]
        public void FailedHostLoopbackRetirementDoesNotEraseFailureState()
        {
            _connection.HostResult = SpatialBlendConnectionState.Failed;
            _connection.LoopbackRetires = true;
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(_connection.State, Is.EqualTo(SpatialBlendConnectionState.Failed));
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
        }

        [Test]
        public void ClientRetryDoesNotStartAHostRound()
        {
            _connection.Authority = true;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.RetryConnection();
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.RelinkRequests, Is.EqualTo(1));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        // ---- The core link: one per player, carried as Fusion reliable data ---------------------------------------

        [Test]
        public void TheHostHostsWithNoAddressAndAttachesALinkPerOtherPlayer()
        {
            _connection.Roster.Add(PlayerRef.FromEncoded(3));
            _connection.OnPlayerJoined(null, _connection.Local);

            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(1));
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(2), "every player but the host itself");
            NUnitAssert.That(_connection.Core.Pipes.Count, Is.EqualTo(2));
            NUnitAssert.That(_connection.JoinRoundBroadcasts, Is.EqualTo(1));
        }

        [Test]
        public void ANewcomerGetsItsLinkBeforeTheRoundIsAnnounced()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            _connection.State = SpatialBlendConnectionState.Connected;
            var late = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(late);
            _connection.Log.Clear();

            _connection.OnPlayerJoined(null, late);

            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(2));
            NUnitAssert.That(_connection.Log.First(), Is.EqualTo("accept"), "the link exists before the announcement");
            NUnitAssert.That(_connection.Log, Does.Contain("announce"));
            NUnitAssert.That(_connection.Log.IndexOf("accept"), Is.LessThan(_connection.Log.IndexOf("announce")));
        }

        [Test]
        public void ChunksFromAPlayerGoToThatPlayersLinkOnly()
        {
            var other = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(other);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = (int)_connection.Epoch;

            _connection.ReceiveLinkChunk(_connection.Peer, epoch, 0, new byte[] { 1, 2, 3 });
            _connection.ReceiveLinkChunk(other, epoch, 0, new byte[] { 9 });
            _connection.ReceiveLinkChunk(PlayerRef.FromEncoded(55), epoch, 0, new byte[] { 7 }); // not in the room

            var written = _connection.Core.Pipes.Values.Select(p => p.FromPeer.ToArray()).OrderBy(b => b.Length).ToList();
            NUnitAssert.That(written[0], Is.EqualTo(new byte[] { 9 }));
            NUnitAssert.That(written[1], Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void TheHostSendsNothingToAPlayerUntilThatPlayerHasSpoken()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var pipe = _connection.Core.Pipes.Values.Single();
            pipe.ToPeer.AddRange(new byte[] { 5, 5, 5 }); // core's server speaks first

            _connection.Tick();
            NUnitAssert.That(_connection.Sent, Is.Empty, "the player may not have joined this round yet");

            _connection.ReceiveLinkChunk(_connection.Peer, (int)_connection.Epoch, 0, new byte[] { 1 });
            _connection.Tick();
            NUnitAssert.That(_connection.Sent.Count, Is.EqualTo(1));
            NUnitAssert.That(_connection.Sent[0].Bytes, Is.EqualTo(new byte[] { 5, 5, 5 }));
            NUnitAssert.That(_connection.Sent[0].Player, Is.EqualTo(_connection.Peer));
            NUnitAssert.That(_connection.Sent[0].Magic, Is.EqualTo(FusionConnectionManager.CoreLinkKeyMagic));
            NUnitAssert.That(_connection.Sent[0].Epoch, Is.EqualTo((int)_connection.Epoch));
            NUnitAssert.That(_connection.Sent[0].Sequence, Is.EqualTo(0));
        }

        [Test]
        public void ALeavingPlayersLinkIsClosedAndAStartedRoundKeepsServingTheOthers()
        {
            var other = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(other);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer);
            _connection.Ack(epoch, other);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1), "the round's apps started");

            _connection.OnPlayerLeft(null, _connection.Peer);

            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
            NUnitAssert.That(_connection.Core.ClosedLinks.Count, Is.EqualTo(1), "that player's link, and only that one");
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False, "the others keep their round");
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
        }

        [Test]
        public void ARetiredRoundClosesEveryLink()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
            _connection.ShutdownConnection();
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(0));
            NUnitAssert.That(_connection.Core.ClosedLinks.Count, Is.EqualTo(1));
        }

        [Test]
        public void AJoinerPumpsItsLinkToTheHostPlayerFromTheRoundAnnouncement()
        {
            _connection.Authority = false;
            _connection.BeginRemote(4);
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
            _connection.Core.Pipes[_connection.Core.LastJoinLink].ToPeer.AddRange(new byte[] { 8, 8 });

            _connection.Tick();

            NUnitAssert.That(_connection.Sent.Count, Is.EqualTo(1), "a joiner speaks first: nothing is held back");
            NUnitAssert.That(_connection.Sent[0].Player, Is.EqualTo(_connection.Peer));
            NUnitAssert.That(_connection.Sent[0].Epoch, Is.EqualTo(4));
        }

        [Test]
        public void ChunksOfAnotherRoundAreIgnored()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = (int)_connection.Epoch;
            _connection.ReceiveLinkChunk(_connection.Peer, epoch - 1, 0, new byte[] { 1 });
            _connection.ReceiveLinkChunk(_connection.Peer, epoch + 1, 0, new byte[] { 2 });
            NUnitAssert.That(_connection.Core.Pipes.Values.Single().FromPeer, Is.Empty);
        }

        // ---- The direct (WebRTC) carrier on top of the link --------------------------------------------------------

        private static PeerCarrierTests.FakeTransport Fake(PlayerRef _) => new PeerCarrierTests.FakeTransport();

        [Test]
        public void TheHostOffersToEachJoinerAndSendsNothingWhileItNegotiates()
        {
            var made = new List<PeerCarrierTests.FakeTransport>();
            _connection.TransportFactory = p => { var t = Fake(p); made.Add(t); return t; };
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(made.Count, Is.EqualTo(1));
            NUnitAssert.That(made[0].Offerer, Is.True, "the host offers");
            NUnitAssert.That(_connection.Signals.Single().Json, Does.Contain("offer"));
            NUnitAssert.That(_connection.Signals.Single().Peer, Is.EqualTo(_connection.Peer));
            NUnitAssert.That(_connection.Signals.Single().Epoch, Is.EqualTo((int)_connection.Epoch));
            _connection.Core.Pipes.Values.Single().ToPeer.AddRange(new byte[] { 1, 2, 3 });
            _connection.ReceiveLinkChunk(_connection.Peer, (int)_connection.Epoch, 0, new byte[] { 1 });
            _connection.Tick();
            NUnitAssert.That(_connection.Sent, Is.Empty, "no byte starts on the slow path while the direct path negotiates");
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: negotiating a direct path"));
        }

        [Test]
        public void TheJoinerAnswersTheOfferAndASignalWithNoCarrierOrForAnotherLinkIsDroppedNotKept()
        {
            PeerCarrierTests.FakeTransport made = null;
            _connection.TransportFactory = p => made = Fake(p);
            _connection.Authority = false;
            // A signal before this side has a carrier for that peer has nowhere to go. A signal passes the token check only
            // after this side issued the peer a token, which is when its carrier is made, so nothing is kept for later.
            _connection.ReceiveSignal(_connection.Peer, 4, "{\"t\":\"offer\"}");
            _connection.BeginRemote(4);
            NUnitAssert.That(made.Offerer, Is.False, "the joiner answers");
            NUnitAssert.That(made.SignalsReceived, Is.Empty, "the early signal was not kept");
            _connection.ReceiveSignal(_connection.Peer, 4, "{\"t\":\"offer\"}");
            NUnitAssert.That(made.SignalsReceived.Single(), Does.Contain("offer"));
            NUnitAssert.That(_connection.Signals.Single().Json, Does.Contain("answer"));
            // A signal of another round is not this link's.
            _connection.ReceiveSignal(_connection.Peer, 3, "{\"t\":\"ice\"}");
            NUnitAssert.That(made.SignalsReceived.Count, Is.EqualTo(1));
        }

        [Test]
        public void ASignalForAPhotonOnlyLinkIsDroppedAndNothingAccumulates()
        {
            _connection.TransportFactory = null; // no direct transport: the Photon carrier alone
            _connection.OnPlayerJoined(null, _connection.Local);
            for (int i = 0; i < 50; i++) _connection.ReceiveSignal(_connection.Peer, (int)_connection.Epoch, "{\"t\":\"ice\"}");
            NUnitAssert.That(_connection.Signals, Is.Empty);
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
        }

        // A chunk waits for the peer's token offer in core, not in a queue of ours that could grow for ever.
        [Test]
        public void NothingIsReadFromCoreUntilThePeerHasOfferedItsTokenSoNoSendWaitsInAQueue()
        {
            _connection.PeerAnswersOffers = false;
            _connection.Authority = false;
            _connection.BeginRemote(4);
            var pipe = _connection.Core.Pipes[_connection.Core.LastJoinLink];
            pipe.ToPeer.AddRange(new byte[] { 1, 2, 3 });

            _connection.Tick();
            NUnitAssert.That(_connection.Sent, Is.Empty, "no offer yet");
            NUnitAssert.That(pipe.ToPeer.Count, Is.EqualTo(3), "the bytes stay in core");

            _connection.GiveOffer(_connection.Peer, 4, TestConnectionManager.PeerToken(4));
            _connection.Tick();
            NUnitAssert.That(_connection.Sent.Single().Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
            NUnitAssert.That(pipe.ToPeer, Is.Empty);
        }

        [Test]
        public void AnOpenDirectChannelIsReportedAsTheCarrierAndAFailedOneAsAFallback()
        {
            PeerCarrierTests.FakeTransport made = null;
            _connection.TransportFactory = p => made = Fake(p);
            _connection.OnPlayerJoined(null, _connection.Local);
            made.State = PeerTransportState.Open;
            _connection.Tick();
            NUnitAssert.That(_connection.ActiveCarrier, Is.EqualTo(CoreLinkCarrier.WebRtc));
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: direct (WebRTC)"));

            PeerCarrierTests.FakeTransport second = null;
            var other = new GameObject("other-connection");
            try
            {
                var c2 = other.AddComponent<TestConnectionManager>();
                c2.Roster.Add(c2.Local); c2.Roster.Add(c2.Peer);
                c2.Core = _connection.Core;
                c2.TransportFactory = p => second = Fake(p);
                c2.OnPlayerJoined(null, c2.Local);
                second.State = PeerTransportState.Failed;
                c2.Tick();
                NUnitAssert.That(c2.ActiveCarrier, Is.EqualTo(CoreLinkCarrier.Photon));
                NUnitAssert.That(c2.CarrierText, Does.StartWith("Carrier: Photon relay (fallback:"));
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }

        [Test]
        public void TheTurnCredentialsAreRequestedOnceAndALinkMadeWhileTheyArePendingKeepsItsFullIceBudget()
        {
            var http = new TurnIceServersTests.FakeHttp();
            _connection.NowValue = 10;
            _connection.Turn = new TurnIceProvider("https://worker.example/ice", "fake-secret", http, () => _connection.NowValue, 4f, 15f, 15f);
            _connection.TransportFactory = Fake;
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(http.Posts.Count, Is.EqualTo(1), "the link's setup asks for the credentials, once");
            // The default 5 s direct budget starts after the fetch's own 4 s bound: 10 + 4 + 5.
            _connection.NowValue = 18.9; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: negotiating a direct path"));
            NUnitAssert.That(http.Posts.Count, Is.EqualTo(1), "a fetch that failed is not asked again inside the round");
            _connection.NowValue = 19; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Does.Contain("did not open in time"));
        }

        private TurnIceProvider WarmProvider(TurnIceServersTests.FakeHttp http, string ttl = "14400")
        {
            var provider = new TurnIceProvider("https://worker.example/ice", "fake-secret", http, () => _connection.NowValue, 4f, 15f, 15f);
            provider.Ensure();
            http.Answer(200, TurnIceServersTests.Good.Replace("14400", ttl));
            provider.Tick();
            NUnitAssert.That(provider.State, Is.EqualTo(TurnIceProvider.FetchState.Ready));
            return provider;
        }

        [Test]
        public void JoiningWithAWarmTurnCacheMakesNoFetchAndWaitsForNothing()
        {
            var http = new TurnIceServersTests.FakeHttp();
            _connection.NowValue = 0;
            _connection.Turn = WarmProvider(http); // the app-start prefetch
            NUnitAssert.That(http.Posts.Count, Is.EqualTo(1));
            _connection.TransportFactory = Fake;
            _connection.NowValue = 600; // ten minutes later: the room flow
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(http.Posts.Count, Is.EqualTo(1), "the join made no fetch");
            NUnitAssert.That(_connection.Turn.Pending, Is.False);
            // The link's budget is the declared 5 s, with no extension for a fetch.
            _connection.NowValue = 604.9; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: negotiating a direct path"));
            _connection.NowValue = 605; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Does.Contain("did not open in time"));
            NUnitAssert.That(http.Posts.Count, Is.EqualTo(1));
        }

        [Test]
        public void AnExpiredTurnCacheIsRefreshedAtTheNextLinkAndTheLinkWaitsOnlyForThatBoundedFetch()
        {
            var http = new TurnIceServersTests.FakeHttp();
            _connection.NowValue = 0;
            _connection.Turn = WarmProvider(http, ttl: "100"); // valid until 100 less one 15 s round: 85
            _connection.TransportFactory = Fake;
            _connection.NowValue = 84.9;
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(http.Posts.Count, Is.EqualTo(1), "still warm: no refresh");
            var other = new GameObject("other-connection");
            try
            {
                var c2 = other.AddComponent<TestConnectionManager>();
                c2.Roster.Add(c2.Local); c2.Roster.Add(c2.Peer);
                c2.Core = _connection.Core;
                c2.TransportFactory = Fake;
                c2.Turn = _connection.Turn;
                _connection.NowValue = c2.NowValue = 85; // the cache has expired (the provider reads the first manager's clock)
                c2.OnPlayerJoined(null, c2.Local);
                NUnitAssert.That(http.Posts.Count, Is.EqualTo(2), "expiry triggers the refresh");
                NUnitAssert.That(c2.Turn.Servers, Is.Empty, "the old answer is not served meanwhile");
                _connection.NowValue = c2.NowValue = 85 + 8.9; c2.Tick(); // 4 s fetch bound + 5 s direct budget
                NUnitAssert.That(c2.CarrierText, Is.EqualTo("Carrier: negotiating a direct path"));
                _connection.NowValue = c2.NowValue = 85 + 9; c2.Tick();
                NUnitAssert.That(c2.CarrierText, Does.Contain("did not open in time"));
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }

        [Test]
        public void TheProcessWideTurnServiceKeepsOneProviderPerUrlAndSecret()
        {
            TurnIceService.Reset();
            try
            {
                var a = TurnIceService.GetOrCreate("https://w.example/ice", "s1", 4f, 15f, 15f);
                NUnitAssert.That(TurnIceService.GetOrCreate("https://w.example/ice", "s1", 4f, 15f, 15f), Is.SameAs(a), "the prefetch's provider is the room's");
                NUnitAssert.That(TurnIceService.Current, Is.SameAs(a));
                NUnitAssert.That(TurnIceService.GetOrCreate("https://w.example/ice", "s2", 4f, 15f, 15f), Is.Not.SameAs(a));
                TurnIceService.Reset();
                NUnitAssert.That(TurnIceService.Current, Is.Null);
            }
            finally { TurnIceService.Reset(); }
        }

        [Test]
        public void WithNoTurnConfiguredNothingIsFetchedAndTheBudgetIsTheDeclaredOne()
        {
            _connection.TransportFactory = Fake;
            _connection.NowValue = 10;
            _connection.OnPlayerJoined(null, _connection.Local);
            _connection.NowValue = 14.9; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: negotiating a direct path"));
            _connection.NowValue = 15; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Does.Contain("did not open in time"));
            NUnitAssert.That(_connection.ActivePath, Is.EqualTo(CoreLinkPath.Photon));
        }

        [Test]
        public void ARelayedDirectChannelIsShownAsTheTurnRelayWithItsTransport()
        {
            var t = new TurnPathFake();
            _connection.TransportFactory = _ => t;
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(_connection.ActivePath, Is.EqualTo(CoreLinkPath.Unknown), "negotiating");
            t.State = PeerTransportState.Open;
            _connection.Tick();
            NUnitAssert.That(_connection.ActiveCarrier, Is.EqualTo(CoreLinkCarrier.WebRtc));
            NUnitAssert.That(_connection.ActivePath, Is.EqualTo(CoreLinkPath.Unknown), "open, the selected pair not read yet");
            t.Path = new CoreLinkPath(CoreLinkPathKind.TurnRelay, RelayTransport.Tls);
            LogAssert.Expect(UnityEngine.LogType.Log, new System.Text.RegularExpressions.Regex("path=TurnRelay\\(tls\\)"));
            _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: WebRTC via TURN relay (tls)"));
            _connection.Tick(); // logged once per link, not each frame
            t.Path = CoreLinkPath.Unknown;
        }

        private sealed class TurnPathFake : PeerCarrierTests.FakeTransport, IPathReporting
        {
            public CoreLinkPath Path { get; set; } = CoreLinkPath.Unknown;
        }

        [Test]
        public void ADirectPathThatDoesNotOpenWithinTheDeclaredTimeoutFallsBack()
        {
            _connection.TransportFactory = Fake;
            _connection.NowValue = 10;
            _connection.OnPlayerJoined(null, _connection.Local);
            _connection.NowValue = 14.9; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Is.EqualTo("Carrier: negotiating a direct path"));
            _connection.NowValue = 15; _connection.Tick();
            NUnitAssert.That(_connection.CarrierText, Does.Contain("did not open in time"));
            // The held traffic now flows over Photon.
            _connection.Core.Pipes.Values.Single().ToPeer.AddRange(new byte[] { 5 });
            _connection.ReceiveLinkChunk(_connection.Peer, (int)_connection.Epoch, 0, new byte[] { 1 });
            _connection.Tick();
            NUnitAssert.That(_connection.Sent.Count, Is.EqualTo(1));
        }

        // ---- A link lost after it opened, and a retry: one peer's link is replaced inside the round -------------------

        private PeerCarrierTests.FakeTransport JoinerWithAnOpenDirectChannel(List<PeerCarrierTests.FakeTransport> made = null)
        {
            PeerCarrierTests.FakeTransport t = null;
            _connection.TransportFactory = p => { t = Fake(p); made?.Add(t); return t; };
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.BeginRemote(4);
            _connection.Core.JoinCompleted = true;
            _connection.Core.PollTunneledJoin();
            _connection.State = SpatialBlendConnectionState.Connected;
            t.State = PeerTransportState.Open;
            _connection.Tick();
            NUnitAssert.That(_connection.ActiveCarrier, Is.EqualTo(CoreLinkCarrier.WebRtc));
            return t;
        }

        // Chunks already handed to a channel that then fails are gone, and nothing retransmits them. The link
        // has ended; it must be neither a silent stall nor a host loss.
        [Test]
        public void ADirectChannelLostAfterItOpenedEndsTheJoinersLinkAndAsksTheHostForAnotherOverPhoton()
        {
            var channel = JoinerWithAnOpenDirectChannel();
            long joinLink = _connection.Core.LastJoinLink;

            channel.State = PeerTransportState.Closed; // the channel dies with chunks in flight
            _connection.Tick();

            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(0), "the link has ended");
            NUnitAssert.That(_connection.Core.ClosedLinks, Does.Contain(joinLink));
            NUnitAssert.That(_connection.HostLeft, Is.False, "the host is still there");
            NUnitAssert.That(_connection.RelinkRequests, Is.EqualTo(1));
            NUnitAssert.That(_connection.LastRelinkRequest, Is.EqualTo((4L, 4, false)), "this link of this round, over the Photon carrier");
        }

        [Test]
        public void TheHostReplacesTheLinkOfAPeerWhoseDirectChannelWasLostOverPhotonAndOnlyThatLink()
        {
            var made = new List<PeerCarrierTests.FakeTransport>();
            var other = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(other);
            _connection.TransportFactory = p => { var t = Fake(p); made.Add(t); return t; };
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = (int)_connection.Epoch;
            made[0].State = PeerTransportState.Open; made[1].State = PeerTransportState.Open;
            _connection.Tick();
            _connection.Ack(epoch, _connection.Peer); _connection.Ack(epoch, other);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
            int closedBefore = _connection.Core.ClosedLinks.Count;
            _connection.Log.Clear();

            made[0].State = PeerTransportState.Closed; // the first peer's channel dies
            _connection.Tick();

            NUnitAssert.That(_connection.Relinks.Count, Is.EqualTo(1));
            var relink = _connection.Relinks.Single();
            NUnitAssert.That(relink.Player, Is.EqualTo(_connection.Peer));
            NUnitAssert.That(relink.Epoch, Is.EqualTo((long)epoch), "the round is not reset");
            NUnitAssert.That(relink.Link, Is.GreaterThan(epoch), "the new link has a number of its own");
            NUnitAssert.That(relink.Direct, Is.False, "the direct channel just failed");
            NUnitAssert.That(made.Count, Is.EqualTo(2), "no new direct transport for the relinked peer");
            NUnitAssert.That(_connection.Core.ClosedLinks.Count, Is.EqualTo(closedBefore + 1), "one link closed");
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(2));
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False);
            NUnitAssert.That(_connection.Epoch, Is.EqualTo((long)epoch));
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.Log.IndexOf("accept"), Is.LessThan(_connection.Log.IndexOf("relink")), "the link exists before the player is told");
        }

        [Test]
        public void AJoinerRejoinsTheSameRoundOverTheLinkTheHostAssignedAndRestartsItsApps()
        {
            JoinerWithAnOpenDirectChannel();
            _connection.DeliverStart(4);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
            int joinsBefore = _connection.JoinCalls;

            _connection.RelinkRpc(4, 9, false, _connection.Peer);

            NUnitAssert.That(_connection.DisconnectCalls, Is.GreaterThan(0), "the native session is replaced");
            NUnitAssert.That(_connection.HostLinkId, Is.EqualTo(9));
            _connection.Core.Disconnect(); // what the real RequestDisconnect does
            _connection.State = SpatialBlendConnectionState.Disconnected;
            _connection.Tick();
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(joinsBefore + 1), "it rejoined");
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
            _connection.Core.Pipes[_connection.Core.LastJoinLink].ToPeer.AddRange(new byte[] { 8 });
            _connection.Tick();
            NUnitAssert.That(_connection.Sent.Single().Epoch, Is.EqualTo(9), "chunks are numbered by the new link, not by the round");
            _connection.State = SpatialBlendConnectionState.Connected;
            _connection.Tick();
            _connection.DeliverStart(4);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(2), "the host's start announcement starts the apps again");
        }

        [Test]
        public void ARelinkThatIsNotFromTheHostOrNotForThisRoundOrNotNewerIsIgnored()
        {
            JoinerWithAnOpenDirectChannel();
            int disconnects = _connection.DisconnectCalls;
            _connection.RelinkRpc(4, 9, true, PlayerRef.FromEncoded(77)); // not the host
            _connection.RelinkRpc(3, 9, true, _connection.Peer);          // another round
            _connection.RelinkRpc(4, 4, true, _connection.Peer);          // not newer than the current link
            _connection.RelinkRpc(5, 9, true, _connection.Peer);          // a round this joiner has not been announced
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(disconnects));
            NUnitAssert.That(_connection.HostLinkId, Is.EqualTo(4));
        }

        // A retry is that peer's alone. The host closes and re-admits its link and never resets the round.
        [Test]
        public void OneJoinersRetryReplacesOnlyThatJoinersLinkAndNeverResetsTheRound()
        {
            var other = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(other);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.Ack(epoch, _connection.Peer); _connection.Ack(epoch, other);
            NUnitAssert.That(_connection.StartCalls, Is.EqualTo(1));
            int hosts = _connection.HostCalls, closed = _connection.Core.ClosedLinks.Count;
            _connection.Log.Clear();

            _connection.RetryRpc(epoch, _connection.Peer);

            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(hosts), "the host's session is not recreated");
            NUnitAssert.That(_connection.Epoch, Is.EqualTo(epoch), "the round is not reset");
            NUnitAssert.That(_connection.IsRetired(epoch), Is.False);
            NUnitAssert.That(_connection.DisconnectCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.Core.ClosedLinks.Count, Is.EqualTo(closed + 1), "that peer's link only");
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(2));
            NUnitAssert.That(_connection.Relinks.Single().Player, Is.EqualTo(_connection.Peer));
            NUnitAssert.That(_connection.Relinks.Single().Direct, Is.True, "a retry is a fresh attempt at the direct path");
            NUnitAssert.That(_connection.Log, Does.Not.Contain("announce"), "no round announcement reaches the other players");
        }

        [Test]
        public void AFailedJoinersRetryAsksTheHostForItsOwnLinkAtTheCurrentRound()
        {
            _connection.Authority = false;
            _connection.AdoptMatchmakingDecision(Decision(CoreParticipantRole.Client));
            _connection.BeginRemote(4);
            _connection.State = SpatialBlendConnectionState.Failed;
            _connection.Tick();

            _connection.RetryConnection();

            NUnitAssert.That(_connection.RelinkRequests, Is.EqualTo(1));
            NUnitAssert.That(_connection.LastRelinkRequest, Is.EqualTo((4L, 4, true)));
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
            // The host's relink then reaches a joiner that never retired the round.
            _connection.Core.Disconnect(); // a failed join has released its native session
            _connection.RelinkRpc(4, 9, true, _connection.Peer);
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(2));
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
        }

        [Test]
        public void ARelinkRequestAboutAReplacedLinkOrAStrangerIsIgnored()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            _connection.RelinkRequestRpc(epoch, (int)epoch + 5, true, _connection.Peer); // a link that is not the current one
            _connection.RelinkRequestRpc(epoch, (int)epoch, true, PlayerRef.FromEncoded(55));                                    // not in the room
            _connection.RelinkRequestRpc(epoch - 1, (int)epoch, true, _connection.Peer);                                         // another round
            NUnitAssert.That(_connection.Relinks, Is.Empty);
        }

        [Test]
        public void ARelinkRequestForTheCurrentLinkReplacesThatLinkOnly()
        {
            var other = PlayerRef.FromEncoded(3);
            _connection.Roster.Add(other);
            _connection.OnPlayerJoined(null, _connection.Local);
            var epoch = _connection.Epoch;
            int closed = _connection.Core.ClosedLinks.Count;

            _connection.RelinkRequestRpc(epoch, (int)epoch, false, _connection.Peer);

            NUnitAssert.That(_connection.Relinks.Single().Player, Is.EqualTo(_connection.Peer));
            NUnitAssert.That(_connection.Relinks.Single().Direct, Is.False);
            NUnitAssert.That(_connection.Core.ClosedLinks.Count, Is.EqualTo(closed + 1));
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(2));
            NUnitAssert.That(_connection.Epoch, Is.EqualTo(epoch));
        }

        // The token offer is one RPC. A dropped one hangs the join, so every announcement of the round offers
        // again to a peer not yet heard from, and stops once it has been.
        [Test]
        public void TheTokenOfferIsRepeatedOnEachAnnouncementUntilThePeerHasSpoken()
        {
            _connection.PeerAnswersOffers = false;
            var late = PlayerRef.FromEncoded(3);
            _connection.OnPlayerJoined(null, _connection.Local);
            _connection.State = SpatialBlendConnectionState.Connected;
            int OffersTo(PlayerRef p) => _connection.Offers.Count(o => o.Peer == p);
            int baseline = OffersTo(_connection.Peer); // its admission, and the round's own announcement
            NUnitAssert.That(baseline, Is.GreaterThanOrEqualTo(1));
            var first = _connection.Offers[0].Token;

            _connection.Roster.Add(late);
            _connection.OnPlayerJoined(null, late); // an announcement event
            NUnitAssert.That(OffersTo(_connection.Peer), Is.EqualTo(baseline + 1), "the silent peer is offered again");
            NUnitAssert.That(_connection.Offers.Where(o => o.Peer == _connection.Peer).Last().Token, Is.EqualTo(first), "the same token");
            int lateBaseline = OffersTo(late);
            NUnitAssert.That(lateBaseline, Is.GreaterThanOrEqualTo(1), "its first offer, at its own admission");

            _connection.ReceiveLinkChunk(_connection.Peer, (int)_connection.Epoch, 0, new byte[] { 1 }); // the peer speaks
            var third = PlayerRef.FromEncoded(4);
            _connection.Roster.Add(third);
            _connection.OnPlayerJoined(null, third);
            NUnitAssert.That(OffersTo(_connection.Peer), Is.EqualTo(baseline + 1), "heard from: nothing repeated");
            NUnitAssert.That(OffersTo(late), Is.EqualTo(lateBaseline + 1), "the other silent peer is still offered");
        }

        [Test]
        public void AJoinerRepeatsItsOfferOnADuplicateAnnouncementUntilTheHostHasSpoken()
        {
            _connection.PeerAnswersOffers = false;
            _connection.Authority = false;
            _connection.BeginRemote(4);
            NUnitAssert.That(_connection.Offers.Count, Is.EqualTo(1));
            _connection.BeginRemote(4); // the host announces the same round again
            NUnitAssert.That(_connection.Offers.Count, Is.EqualTo(2));
            _connection.ReceiveLinkChunk(_connection.Peer, 4, 0, new byte[] { 1 });
            _connection.BeginRemote(4);
            NUnitAssert.That(_connection.Offers.Count, Is.EqualTo(2), "the host has spoken");
        }

        // The link state belongs to one runner. Without the reset a rejoin (or a host that starts again at 1)
        // would meet the last runner's retired epoch and every round of it would be dropped as old.
        [Test]
        public void ALinkStateIsResetPerRunnerSoARestartedHostIsNotDroppedAsOld()
        {
            _connection.Authority = false;
            _connection.SetEpochs(epoch: 9, retiredEpoch: 9);
            _connection.BeginRemote(1);
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(0), "within one runner a lower epoch is an old round");

            _connection.ResetLinkState(); // a new runner

            _connection.BeginRemote(1);
            NUnitAssert.That(_connection.JoinCalls, Is.EqualTo(1));
            NUnitAssert.That(_connection.Epoch, Is.EqualTo(1L));
        }

        [Test]
        public void ResettingTheLinkStateDropsTheOldRunnersTokensAndLinks()
        {
            _connection.OnPlayerJoined(null, _connection.Local);
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(1));
            _connection.ResetLinkState();
            NUnitAssert.That(_connection.LinkedPeerCount, Is.EqualTo(0));
            NUnitAssert.That(_connection.Core.ClosedLinks.Count, Is.EqualTo(1));
            // A chunk of the old runner's round finds no link and no token.
            _connection.ReceiveLinkChunk(_connection.Peer, 1, 0, new byte[] { 1 });
            NUnitAssert.That(_connection.Core.Pipes.Values.Single().FromPeer, Is.Empty);
        }

        [Test]
        public void TheDirectCarrierIsOffWhenTheSettingSaysSo()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            var go = new GameObject("webrtc-config");
            try
            {
                var probe = go.AddComponent<WebRtcConfigProbe>();
                probe.Config = settings;
                NUnitAssert.That(probe.Stun, Is.EqualTo(new[] { "stun:stun.l.google.com:19302" }));
                NUnitAssert.That(probe.IceTimeout, Is.EqualTo(5f));
                settings.useWebRtc = false;
                NUnitAssert.That(probe.Transport(), Is.Null);
                settings.stunServers = new[] { "stun:a:1", " ", "stun:b:2" };
                settings.webRtcIceTimeoutSeconds = 9;
                NUnitAssert.That(probe.Stun, Is.EqualTo(new[] { "stun:a:1", "stun:b:2" }));
                NUnitAssert.That(probe.IceTimeout, Is.EqualTo(9f));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); UnityEngine.Object.DestroyImmediate(go); }
        }

        private sealed class WebRtcConfigProbe : FusionConnectionManager
        {
            public SpatialBlendSettings Config;
            protected override SpatialBlendSettings DeclaredConfig => Config;
            public string[] Stun => StunServers.ToArray();
            public float IceTimeout => IceTimeoutSeconds;
            public IPeerDataTransport Transport() => CreatePeerTransport(PlayerRef.FromEncoded(2));
        }

        private sealed class TestConnectionManager : FusionConnectionManager
        {
            public Func<PlayerRef, IPeerDataTransport> TransportFactory;
            /// <summary>The TURN credentials of this manager; null (the default) is "no TURN configured", which also keeps a test off the network.</summary>
            public TurnIceProvider Turn;
            protected override TurnIceProvider CreateTurnProvider() => Turn;
            public readonly List<(PlayerRef Peer, int Epoch, string Json)> Signals = new();
            protected override IPeerDataTransport CreatePeerTransport(PlayerRef peer) => TransportFactory?.Invoke(peer);
            protected override void SendSignal(PlayerRef peer, int epoch, byte[] token, string json)
            {
                SignalTokens.Add(token);
                Signals.Add((peer, epoch, json));
            }
            public readonly List<byte[]> SignalTokens = new();
            /// <summary>The offers this side made: (peer, epoch, token).</summary>
            public readonly List<(PlayerRef Peer, int Epoch, byte[] Token)> Offers = new();
            /// <summary>When true (default) the peer answers every offer at once with <see cref="PeerToken"/>, as a live peer does.</summary>
            public bool PeerAnswersOffers = true;
            public static byte[] PeerToken(int epoch) { var t = new byte[LinkTokens.Size]; for (int i = 0; i < t.Length; i++) t[i] = (byte)(0xA0 + epoch + i); return t; }
            protected override void SendTokenOffer(PlayerRef peer, int epoch, long high, long low)
            {
                var token = new byte[LinkTokens.Size];
                BitConverter.GetBytes(high).CopyTo(token, 0); BitConverter.GetBytes(low).CopyTo(token, 8);
                Offers.Add((peer, epoch, token));
                if (PeerAnswersOffers) GiveOffer(peer, epoch, PeerToken(epoch));
            }
            public void GiveOffer(PlayerRef source, int epoch, byte[] token) =>
                Invoke("AcceptTokenOffer", source, epoch, BitConverter.ToInt64(token, 0), BitConverter.ToInt64(token, 8));

            public sealed class SentChunk
            {
                public PlayerRef Player;
                public int Magic, Epoch, Sequence;
                public byte[] Bytes, Token;
            }

            public readonly PlayerRef Local = PlayerRef.FromEncoded(1);
            public readonly PlayerRef Peer = PlayerRef.FromEncoded(2);
            public readonly List<PlayerRef> Roster = new();
            public readonly List<SentChunk> Sent = new();
            public readonly List<string> Log = new();
            public FakeTunnelManager Core;
            public SpatialBlendConnectionState State = SpatialBlendConnectionState.Disconnected;
            public SpatialBlendConnectionState HostResult = SpatialBlendConnectionState.Connected;
            public bool Authority = true;
            public bool LoopbackRetires;
            public SpatialBlendSettings Config;
            public double NowValue;
            public int StartCalls;
            public int JoinCalls;
            public int DisconnectCalls;
            public int ReadyCalls;
            public int HostCalls;
            public int StartBroadcastCalls;
            public int JoinRoundBroadcasts;
            public long Epoch => (long)Field("_epoch");

            protected override SpatialBlendConnectionState ConnectionState => State;
            protected override bool IsAuthority => Authority;
            protected override PlayerRef LocalPlayer => Local;
            protected override List<PlayerRef> ActiveRoster => Roster;
            protected override double Now => NowValue;
            protected override SpatialBlendSettings DeclaredConfig => Config;

            protected override void RequestHostTunneled()
            {
                HostCalls++;
                State = HostResult;
                if (HostResult == SpatialBlendConnectionState.Connected)
                {
                    Core.HostTunneled();
                }
            }

            protected override CoreLink RequestAcceptLink()
            {
                Log.Add("accept");
                return Core.AcceptCoreLink();
            }

            protected override CoreLink RequestJoinTunneled()
            {
                JoinCalls++;
                State = SpatialBlendConnectionState.Connecting;
                return Core.JoinTunneled();
            }

            protected override void RequestDisconnect() => DisconnectCalls++;
            protected override void StartManagedApps() => StartCalls++;
            protected override void BroadcastJoinRound(long epoch)
            {
                Log.Add("announce");
                JoinRoundBroadcasts++;
            }
            protected override void SendReadyToAuthority(long epoch) => ReadyCalls++;
            protected override void RequestRelinkFromAuthority(long epoch, int link, bool direct)
            {
                RelinkRequests++;
                LastRelinkRequest = (epoch, link, direct);
            }
            public int RelinkRequests;
            public (long Epoch, int Link, bool Direct) LastRelinkRequest;
            public readonly List<(PlayerRef Player, long Epoch, int Link, bool Direct)> Relinks = new();
            protected override void SendRelink(PlayerRef player, long epoch, int link, bool direct)
            {
                Log.Add("relink");
                Relinks.Add((player, epoch, link, direct));
            }
            protected override void BroadcastStartApps(long epoch)
            {
                StartBroadcastCalls++;
                DeliverStart(epoch);
            }
            public readonly List<PlayerRef> ObserverStarts = new();
            protected override void SendObserverStart(PlayerRef player, long epoch) => ObserverStarts.Add(player);
            public int RunnerShutdownCalls;
            protected override void RequestRunnerShutdown() => RunnerShutdownCalls++;
            protected override void BroadcastRetire(long epoch)
            {
                if (LoopbackRetires) Retire(epoch);
            }
            protected override void SendReliable(PlayerRef player, ReliableKey key, byte[] bytes)
            {
                key.GetInts(out int magic, out int epoch, out int sequence, out _);
                var token = bytes.Take(LinkTokens.Size).ToArray();
                Sent.Add(new SentChunk { Player = player, Magic = magic, Epoch = epoch, Sequence = sequence, Token = token, Bytes = bytes.Skip(LinkTokens.Size).ToArray() });
            }

            public void Ack(long epoch, PlayerRef source) => Invoke("AcceptReadyAck", epoch, source);
            public void BeginRemote(long epoch) => Invoke("BeginRemoteConnection", epoch, Peer);
            public void DeliverStart(long epoch) => Invoke("StartAppsForEpoch", epoch);
            // Injected inbound receipt seams: these deliberately avoid invoking
            // Fusion-weaved RPC transport wrappers on an unspawned object.
            public void Retire(long epoch) => Invoke("HandleRetirement", epoch);
            // A joiner's retry, as the host receives it: a request about the link it was given in this round.
            public void RetryRpc(long epoch, PlayerRef source) => Invoke("HandleRelinkRequest", epoch, (int)epoch, true, source);
            public void RelinkRequestRpc(long epoch, int link, bool direct, PlayerRef source) =>
                Invoke("HandleRelinkRequest", epoch, link, direct, source);
            public void RelinkRpc(long epoch, int link, bool direct, PlayerRef source) =>
                Invoke("BeginRelink", epoch, link, direct, source);
            public int HostLinkId => (int)Field("_hostLinkId");
            public void Tick() => Invoke("Update");
            public void ResetLinkState() => Invoke("ResetLinkState");
            public bool IsRetired(long epoch) => (long)Field("_retiredEpoch") >= epoch;
            public void SetEpochs(long epoch, long retiredEpoch)
            {
                SetField("_epoch", epoch);
                SetField("_retiredEpoch", retiredEpoch);
            }

            public void MarkAppsStarted(long epoch)
            {
                SetField("_epoch", epoch);
                SetField("_retiredEpoch", epoch - 1);
                SetField("_appsStartedEpoch", epoch);
            }

            private object Field(string name) => typeof(FusionConnectionManager)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this);
            private void SetField(string name, object value) => typeof(FusionConnectionManager)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, value);

            private void Invoke(string name, params object[] args)
            {
                foreach (var method in typeof(FusionConnectionManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (method.Name == name && method.GetParameters().Length == args.Length)
                    {
                        method.Invoke(this, args);
                        return;
                    }
                }
                throw new MissingMethodException(name);
            }
        }
    }
}
#endif
