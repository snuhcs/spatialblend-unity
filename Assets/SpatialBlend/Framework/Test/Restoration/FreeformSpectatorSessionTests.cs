// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using FakeTunnelManager = SpatialBlend.Tests.TunnelFixtures.FakeTunnelManager;
using RunnerStart = SpatialBlend.Samples.FreeformSpectatorSession.RunnerStart;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The spectator session's state machine, with fakes for the runner, the room list, the clock and the core link: every way
    /// out tears down the same way, a join can be given up while it starts, and the next join starts clean.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformSpectatorSessionTests
    {
        private readonly List<GameObject> _objects = new();
        private TestSession _session;
        private TestConnection _connection;

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("spectator-session");
            _objects.Add(go);
            _connection = go.AddComponent<TestConnection>();
            _session = go.AddComponent<TestSession>();
            _session.Configure(null, _connection, null, null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            LogAssert.ignoreFailingMessages = false;
        }

        /// <summary>The scene reload that follows a shut-down runner gives the next join a new session; this is that new one.</summary>
        private void AfterSceneReload()
        {
            UnityEngine.Object.DestroyImmediate(_session);
            _session = _connection.gameObject.AddComponent<TestSession>();
            _session.Configure(null, _connection, null, null);
        }

        private static FusionSessionEntry Room(string name, int players = 1) => new FusionSessionEntry(name, players, 9);

        /// <summary>Joins "R" and lets the room list answer with it; the runner start is whatever <see cref="TestSession.NextStart"/> is.</summary>
        private void JoinListed(string room = "R")
        {
            _session.Join(room);
            _session.GetComponent<TestBrowser>().ApplySessions(new[] { Room(room) });
        }

        [Test]
        public void ASearchThatNeverSeesTheRoomTimesOutAndStartsNothing()
        {
            _session.Join("R");
            _session.GetComponent<TestBrowser>().ApplySessions(new[] { Room("other") });
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Searching));
            _session.NowValue += FreeformSpectatorSession.DefaultRoomWaitSeconds + 1;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Does.Contain(SpectatorJoinPolicy.NotFoundError));
            NUnitAssert.That(_session.Calls, Is.Empty, "no runner was started for a room that is not listed");
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
        }

        [Test]
        public void AListedRoomIsJoinedAsAClientAndTheSpectatorSpectates()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating));
            NUnitAssert.That(_session.Calls, Is.EqualTo(new[] { "start:R" }));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void LeavingTearsTheCoreConnectionAndTheRunnerDownAndTheSpectatorCanJoinAgain()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            _session.Leave();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle));
            NUnitAssert.That(_session.Calls, Is.EqualTo(new[] { "start:R", "shutdown", "reload" }), "the runner is shut down and the scene reloaded");
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None), "the core connection was told the participant left");
            NUnitAssert.That(_connection.HostLeft, Is.False, "a leave is not a host loss");
            _session.Join("R");
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle), "no join while the scene reloads");

            AfterSceneReload();
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating), "a second join after a leave works");
            NUnitAssert.That(_session.Calls, Is.EqualTo(new[] { "start:R" }));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void ARoomTheRunnerCreatedIsLeftAgainAndNothingStaysHosted()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Created());
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Is.EqualTo(SpectatorJoinPolicy.RoomCreatedByUsError));
            NUnitAssert.That(_session.Calls, Is.EqualTo(new[] { "start:R", "shutdown", "reload" }));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
        }

        [Test]
        public void AFailedStartShutsDownAndTheNextJoinIsNotBusy()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Fail("no capacity"));
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Is.EqualTo("no capacity"));
            NUnitAssert.That(_session.Calls, Does.Contain("shutdown"));
            AfterSceneReload();
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating), "a failure left no matchmaker or browser state behind");
        }

        [UnityTest]
        public IEnumerator LeavingWhileTheRunnerStartsCancelsTheJoin()
        {
            var pending = new TaskCompletionSource<RunnerStart>();
            _session.NextStart = pending.Task;
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Joining));
            _session.Leave();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle));
            pending.SetResult(RunnerStart.Success());
            for (int i = 0; i < 3; i++) yield return null;
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle), "the start that finished after the leave did not make it a spectator");
            NUnitAssert.That(_session.Calls.Count(c => c == "shutdown"), Is.EqualTo(1), "the runner that was starting was shut down by the leave");
            NUnitAssert.That(_session.Calls.Count(c => c == "reload"), Is.EqualTo(1));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
        }

        [Test]
        public void ALeaveBetweenTheListAndTheRunnerNeverStartsOne()
        {
            // The room list is answered, then the join is given up before the browser asks for the runner.
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            _session.Join("R");
            var browser = _session.GetComponent<TestBrowser>();
            _session.Leave();
            browser.ApplySessions(new[] { Room("R") });
            NUnitAssert.That(_session.Calls, Is.Empty);
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
        }

        [Test]
        public void AJoinThatDoesNotFinishHasADeadline()
        {
            _session.NextStart = new TaskCompletionSource<RunnerStart>().Task;
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Joining));
            _session.NowValue += FreeformSpectatorSession.DefaultJoinSeconds + 1;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Is.EqualTo(FreeformSpectatorSession.JoinTimedOutError));
            NUnitAssert.That(_session.Calls, Does.Contain("shutdown"));
        }

        [Test]
        public void ACoreLinkThatDoesNotComeUpInTimeMakesTheSpectatorLeaveTheRoom()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            _session.CoreStateValue = SpatialBlendConnectionState.Connecting;
            JoinListed();
            _session.NowValue += 10;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating), "still inside the connect deadline");
            _session.NowValue += 6;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Is.EqualTo(FreeformSpectatorSession.CoreLinkDeadlineError));
            NUnitAssert.That(_session.Calls, Does.Contain("shutdown"));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
        }

        [Test]
        public void ALinkedSpectatorStaysThroughARelinkAndLeavesOnAFailedCore()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            _session.CoreStateValue = SpatialBlendConnectionState.Connected;
            JoinListed();
            _session.Tick();
            _session.CoreStateValue = SpatialBlendConnectionState.Disconnected; // a relink: the round is replaced
            _session.NowValue += 60;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating));
            _session.CoreStateValue = SpatialBlendConnectionState.Failed;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Calls, Does.Contain("shutdown"));
        }

        [Test]
        public void ASpectatorWhoseHostLeftLeavesTheRoom()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            _session.HostLeftValue = true;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Is.EqualTo(FreeformSpectatorSession.HostLeftError));
        }

        [Test]
        public void ASpectatorThatFusionMakesMasterStillNeverHostsTheCore()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            // The connection is the master here (TestConnection.IsAuthority): Fusion promoted the spectator.
            _connection.OnPlayerJoined(null, default);
            NUnitAssert.That(_connection.HostCalls, Is.EqualTo(0));
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void ARoomWithTooFewParticipantsKeepsTheSpectatorWaitingUntilItHasEnough()
        {
            _session.RequiredValue = 2;
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            _session.Join("R");
            var browser = _session.GetComponent<TestBrowser>();
            browser.ApplySessions(new[] { Room("R", 1) });
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Searching), "one participant is not enough to start the room's apps");
            NUnitAssert.That(_session.Notice, Does.Contain("Waiting for the room's participants"));
            NUnitAssert.That(_session.Calls, Is.Empty, "no runner while the room waits for its participants");
            NUnitAssert.That(_connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            browser.ApplySessions(new[] { Room("R", 2) });
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating), "joined once the room has the players it needs");
            NUnitAssert.That(_session.Notice, Is.Null);
            NUnitAssert.That(_session.Calls, Is.EqualTo(new[] { "start:R" }));
        }

        [Test]
        public void ARoomThatNeverGetsEnoughParticipantsIsRefusedAtTheDeadlineWithAClearMessage()
        {
            _session.RequiredValue = 2;
            _session.Join("R");
            _session.GetComponent<TestBrowser>().ApplySessions(new[] { Room("R", 1) });
            _session.NowValue += FreeformSpectatorSession.DefaultRoomWaitSeconds + 1;
            _session.Tick();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            NUnitAssert.That(_session.Error, Does.StartWith(SpectatorJoinPolicy.WaitingForParticipantsError));
            NUnitAssert.That(_session.Error, Does.Contain("1 of the 2 players"));
            NUnitAssert.That(_session.Calls, Is.Empty);
        }

        [Test]
        public void AWaitForParticipantsIsNotAnUnlistedRoom()
        {
            _session.RequiredValue = 2;
            _session.Join("R");
            _session.GetComponent<TestBrowser>().ApplySessions(new[] { Room("other", 5) });
            NUnitAssert.That(_session.Notice, Is.Null, "an unlisted room has no participants to wait for");
            _session.NowValue += FreeformSpectatorSession.DefaultRoomWaitSeconds + 1;
            _session.Tick();
            NUnitAssert.That(_session.Error, Does.Contain(SpectatorJoinPolicy.NotFoundError));
        }

        [Test]
        public void AQuittingApplicationDoesNotReloadTheScene()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            typeof(FreeformSpectatorSession).GetMethod("OnApplicationQuit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_session, null);
            _session.Leave();
            NUnitAssert.That(_session.Calls, Is.EqualTo(new[] { "start:R", "shutdown" }), "the runner is shut down, the scene is not reloaded while quitting");
        }

        internal sealed class TestBrowser : SpectatorSessionBrowser
        {
            protected override Task<string> JoinLobbyAsync() => Task.FromResult<string>(null);
            protected override Task CloseLobbyAsync() => Task.CompletedTask;
        }

        // ---- the spectator as a declared desktop observer (the minimap's room snapshots) ----

        private static bool DeclaredObserver(FusionConnectionManager connection) =>
            (bool)typeof(FusionConnectionManager).GetField("_localObserver", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(connection);

        [Test]
        public void StartDeclaresTheSpectatorAnObserverAndBuildsTheMinimap()
        {
            var before = UnityEngine.Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None);
            NUnitAssert.That(DeclaredObserver(_connection), Is.False);
            typeof(FreeformSpectatorSession).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_session, null);
            if (_session.Overlay != null) _objects.Add(_session.Overlay.gameObject);
            if (_session.RoomView != null) _objects.Add(_session.RoomView.gameObject);
            foreach (var system in UnityEngine.Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None))
                if (!before.Contains(system)) _objects.Add(system.gameObject);
            NUnitAssert.That(DeclaredObserver(_connection), Is.True, "the room's participants leave it out of their start and send it their rooms");
            NUnitAssert.That(_session.Minimap, Is.Not.Null);
            NUnitAssert.That(_session.Minimap.Shown, Is.False, "no map before the spectator is in a room");
            NUnitAssert.That(_session.RoomView, Is.Not.Null, "the 3-D room view");
            NUnitAssert.That(_session.RegionSource, Is.Not.Empty, "the Photon region was resolved (empty Region: the best region)");
            NUnitAssert.That(_session.RegionLabel, Is.Not.Empty);
        }

        [Test]
        public void WhileSpectatingTheSpectatorAnnouncesItselfOnceASecond()
        {
            _session.NextStart = Task.FromResult(RunnerStart.Success());
            JoinListed();
            NUnitAssert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Spectating));
            int start = _session.Announces;
            _session.Tick();
            NUnitAssert.That(_session.Announces, Is.EqualTo(start + 1), "the first tick announces");
            _session.NowValue += 0.5;
            _session.Tick();
            NUnitAssert.That(_session.Announces, Is.EqualTo(start + 1), "not again within the second");
            _session.NowValue += 0.6;
            _session.Tick();
            NUnitAssert.That(_session.Announces, Is.EqualTo(start + 2), "once a second");
            for (int i = 0; i < 3; i++) { _session.NowValue += 1.0; _session.Tick(); }
            NUnitAssert.That(_session.Announces, Is.EqualTo(start + 5));
        }

        [Test]
        public void NoAnnouncementOutsideARoom()
        {
            _session.Tick();
            _session.Join("R");
            _session.Tick();
            NUnitAssert.That(_session.Announces, Is.EqualTo(0), "idle and searching: no presence");
        }

        [Test]
        public void AParticipantWhoLeavesTakesTheirRoomOffTheMap()
        {
            var player = Fusion.PlayerRef.FromEncoded(4);
            string id = ObserverSceneMap.ParticipantId(player.PlayerId);
            var room = ParticipantRoomSnapshot.Create(id, null, 3, true, Vector3.zero, Quaternion.identity,
                Array.Empty<ObserverFurniture>(), Array.Empty<ObserverPlacedObject>(), Array.Empty<ObserverAvatarPose>());
            NUnitAssert.That(_session.ReceiveRoomSnapshot(player, new ArraySegment<byte>(ObserverRoomCodec.Write(room))), Is.True);
            NUnitAssert.That(_session.Rooms.Rooms.Rooms.Count, Is.EqualTo(1));
            ((Fusion.INetworkRunnerCallbacks)_session).OnPlayerLeft(null, Fusion.PlayerRef.FromEncoded(7));
            NUnitAssert.That(_session.Rooms.Rooms.Rooms.Count, Is.EqualTo(1), "somebody else's leave");
            ((Fusion.INetworkRunnerCallbacks)_session).OnPlayerLeft(null, player);
            NUnitAssert.That(_session.Rooms.Rooms.Rooms.Count, Is.EqualTo(0));
        }

        internal sealed class TestConnection : FusionConnectionManager
        {
            public int HostCalls;
            protected override bool IsAuthority => true;
            protected override SpatialBlendConnectionState ConnectionState => SpatialBlendConnectionState.Disconnected;
            protected override void RequestHostTunneled() => HostCalls++;
            protected override void RequestDisconnect() { }
            protected override void BroadcastJoinRound(long epoch) { }
            protected override void BroadcastRetire(long epoch) { }
        }

        internal sealed class TestSession : FreeformSpectatorSession
        {
            public readonly List<string> Calls = new();
            public double NowValue = 100;
            public SpatialBlendConnectionState CoreStateValue = SpatialBlendConnectionState.Connecting;
            public bool HostLeftValue;
            public int RequiredValue = 1;
            public Task<RunnerStart> NextStart = Task.FromResult(RunnerStart.Success());
            protected override double Now => NowValue;
            protected override SpatialBlendConnectionState CoreState => CoreStateValue;
            protected override bool HostHasLeft => HostLeftValue;
            protected override float ConnectDeadlineSeconds => 15f;
            protected override int ParticipantsRequired => RequiredValue;
            protected override SpectatorSessionBrowser CreateBrowser() => gameObject.AddComponent<TestBrowser>();
            protected override Task<RunnerStart> StartRunnerCore(string sessionName)
            {
                Calls.Add("start:" + sessionName);
                return NextStart;
            }
            protected override Task ShutdownRunnerCore()
            {
                Calls.Add("shutdown");
                return Task.CompletedTask;
            }
            protected override void ReloadScene() => Calls.Add("reload");
            public int Announces;
            protected override void AnnouncePresence() => Announces++;
        }
    }
}
#endif
