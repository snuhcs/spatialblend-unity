// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformMatchmakingContractTests
    {
        private readonly List<GameObject> _objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects.Where(go => go != null)) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        [Test]
        public void EmptyListHasNoRoomsAndStillAllowsHosting()
        {
            var match = new FreeformMatchmaking();
            var listed = match.Refresh(null);
            NUnitAssert.That(listed.Rooms, Is.Empty);
            NUnitAssert.That(match.IsEmpty, Is.True);
            NUnitAssert.That(match.Phase, Is.EqualTo(MatchmakingPhase.Browsing));

            var hosted = match.TryHost("Demo");
            NUnitAssert.That(hosted.Accepted, Is.True);
            NUnitAssert.That(hosted.Role, Is.EqualTo(CoreParticipantRole.Host));
            NUnitAssert.That(hosted.SessionName, Is.EqualTo("Demo"));
        }

        [Test]
        public void RefreshReplacesTheRoomList()
        {
            var match = new FreeformMatchmaking();
            match.Refresh(new[]
            {
                new MatchmakingRoom("Room 2", 1, 9),
                new MatchmakingRoom("Hidden", 1, 9, isVisible: false),
                new MatchmakingRoom("Room 2", 4, 9),
            });
            var refreshed = match.Refresh(new[]
            {
                new MatchmakingRoom("Room 9", 2, 9),
            });

            NUnitAssert.That(refreshed.Rooms.Select(room => room.Name), Is.EqualTo(new[] { "Room 9" }));
            NUnitAssert.That(match.TryJoin("Room 2").Phase, Is.EqualTo(MatchmakingPhase.Error));
        }

        [Test]
        public void HostingAssignsTheCoreHostRole()
        {
            var match = Listed("Other");
            var decision = match.TryHost("Demo");
            NUnitAssert.That(decision.Accepted, Is.True);
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.Host));
            NUnitAssert.That(decision.Phase, Is.EqualTo(MatchmakingPhase.InSession));
            NUnitAssert.That(decision.SessionName, Is.EqualTo("Demo"));
        }

        [Test]
        public void JoiningAssignsTheClientRoleByTheRoomNameAlone()
        {
            // No address is advertised or needed: the room's name is all a joiner has.
            var match = new FreeformMatchmaking();
            match.Refresh(new[]
            {
                new MatchmakingRoom("Demo", 1, 9),
            });
            var decision = match.TryJoin("Demo");
            NUnitAssert.That(decision.Accepted, Is.True);
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.Client));
            NUnitAssert.That(decision.SessionName, Is.EqualTo("Demo"));
        }

        [Test]
        public void LeaveTearsDownTheSessionRole()
        {
            var match = Listed();
            match.TryHost("Demo");
            var left = match.Leave();
            NUnitAssert.That(left.Accepted, Is.True);
            NUnitAssert.That(left.Role, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(left.Phase, Is.EqualTo(MatchmakingPhase.Browsing));
            NUnitAssert.That(left.SessionName, Is.Null);
        }

        [Test]
        public void JoiningAVanishedRoomEntersTheErrorState()
        {
            var match = Listed("Demo");
            match.Refresh(System.Array.Empty<MatchmakingRoom>());
            var decision = match.TryJoin("Demo");
            NUnitAssert.That(decision.Accepted, Is.False);
            NUnitAssert.That(decision.Phase, Is.EqualTo(MatchmakingPhase.Error));
            NUnitAssert.That(decision.Error, Is.EqualTo(FreeformMatchmaking.VanishedRoomError));
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.None));
        }

        [Test]
        public void HostingAListedNameIsACollision()
        {
            var match = Listed("Demo");
            var decision = match.TryHost("Demo");
            NUnitAssert.That(decision.Accepted, Is.False);
            NUnitAssert.That(decision.Phase, Is.EqualTo(MatchmakingPhase.Error));
            NUnitAssert.That(decision.Error, Is.EqualTo(FreeformMatchmaking.NameCollisionError));
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(match.Phase, Is.EqualTo(MatchmakingPhase.Error));
        }

        [Test]
        public void BrowserHostsAFreeNameAndJoinsAListedRoomByName()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            browser.ApplySessions(new[]
            {
                new FusionSessionEntry("Demo", 1, 9),
            });
            NUnitAssert.That(browser.RoomListIsEmpty, Is.False);

            browser.HostRoom("Other");
            NUnitAssert.That(browser.ParticipantRole, Is.EqualTo(CoreParticipantRole.Host));
            NUnitAssert.That(browser.Calls.Last(), Is.EqualTo("start:Other"));

            var joining = CreateBrowser();
            Done(joining.OpenLobbyAsync());
            joining.ApplySessions(new[]
            {
                new FusionSessionEntry("Demo", 1, 9),
            });
            NUnitAssert.That(Done(joining.JoinAsync("Demo")), Is.True);
            NUnitAssert.That(joining.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
            NUnitAssert.That(joining.Calls.Last(), Is.EqualTo("start:Demo"));
        }

        [Test]
        public void BrowserReportsAVanishedRoomAndANameCollision()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            browser.ApplySessions(System.Array.Empty<FusionSessionEntry>());
            NUnitAssert.That(browser.RoomListIsEmpty, Is.True);
            NUnitAssert.That(Done(browser.JoinAsync("Demo")), Is.False);
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Failed));
            NUnitAssert.That(browser.LastError, Is.EqualTo(FreeformMatchmaking.VanishedRoomError));
            NUnitAssert.That(browser.Calls.Any(call => call.StartsWith("start:")), Is.False);

            var collision = CreateBrowser();
            Done(collision.OpenLobbyAsync());
            collision.ApplySessions(new[] { new FusionSessionEntry("Demo", 1, 9) });
            collision.HostRoom("Demo");
            NUnitAssert.That(collision.State, Is.EqualTo(FusionSessionBrowserState.Failed));
            NUnitAssert.That(collision.LastError, Is.EqualTo(FreeformMatchmaking.NameCollisionError));
            NUnitAssert.That(collision.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(collision.Calls.Any(call => call.StartsWith("start:")), Is.False);
        }

        [Test]
        public void BrowserLeaveShutsDownTheCoreLink()
        {
            var root = new GameObject("matchmaking-leave");
            _objects.Add(root);
            var connection = root.AddComponent<RoleConnection>();
            var browser = root.AddComponent<TestBrowser>();
            Done(browser.OpenLobbyAsync());
            browser.ApplySessions(System.Array.Empty<FusionSessionEntry>());
            browser.HostRoom("Demo");
            NUnitAssert.That(browser.ParticipantRole, Is.EqualTo(CoreParticipantRole.Host));

            browser.LeaveSession();
            NUnitAssert.That(browser.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(connection.Shutdowns, Is.EqualTo(1));
        }

        [Test]
        public void FailedStartClearsTheBusySession()
        {
            var browser = CreateBrowser();
            browser.CanStart = false;
            Done(browser.OpenLobbyAsync());
            browser.ApplySessions(System.Array.Empty<FusionSessionEntry>());
            browser.HostRoom("Demo");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Failed));
            NUnitAssert.That(browser.MatchmakingPhase, Is.EqualTo(MatchmakingPhase.Browsing));
            NUnitAssert.That(browser.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(browser.Calls.Any(call => call.StartsWith("start:")), Is.False);
        }

        [Test]
        public void ClientRoleDoesNotHostTheCoreLink()
        {
            var root = new GameObject("matchmaking-role");
            _objects.Add(root);
            var connection = root.AddComponent<RoleConnection>();
            connection.AdoptMatchmakingDecision(new MatchmakingDecision(true, MatchmakingPhase.InSession,
                CoreParticipantRole.Client, "Demo", null, null));
            connection.OnPlayerJoined(null, PlayerRef.None);
            NUnitAssert.That(connection.HostCalls, Is.EqualTo(0));
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        private static FreeformMatchmaking Listed(string name = "Room 1")
        {
            var match = new FreeformMatchmaking();
            match.Refresh(new[] { new MatchmakingRoom(name, 1, 9) });
            return match;
        }

        private static void Done(Task task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True, task.IsFaulted ? task.Exception?.GetBaseException().Message : "pending");
            task.GetAwaiter().GetResult();
        }

        private static T Done<T>(Task<T> task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True);
            return task.GetAwaiter().GetResult();
        }

        private TestBrowser CreateBrowser()
        {
            var go = new GameObject("matchmaking-browser");
            _objects.Add(go);
            return go.AddComponent<TestBrowser>();
        }

        private sealed class TestBrowser : FusionSessionBrowser
        {
            public readonly List<string> Calls = new();
            public bool CanStart = true;
            protected override bool CanStartSession => CanStart;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);
            protected override Task<string> JoinLobbyAsync()
            {
                Calls.Add("join-lobby");
                return Task.FromResult<string>(null);
            }

            protected override Task CloseLobbyAsync()
            {
                Calls.Add("close-lobby");
                return Task.CompletedTask;
            }
        }

        private sealed class RoleConnection : FusionConnectionManager
        {
            public int HostCalls;
            public int Shutdowns;

            public override void ShutdownConnection()
            {
                Shutdowns++;
                base.ShutdownConnection();
            }

            protected override bool IsAuthority => true;
            protected override SpatialBlendConnectionState ConnectionState => SpatialBlendConnectionState.Disconnected;
            protected override void RequestHostTunneled() => HostCalls++;
            protected override void RequestDisconnect() { }
            protected override void BroadcastJoinRound(long epoch) { }
            protected override void BroadcastRetire(long epoch) { }
        }
    }
}
#endif
