// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using FakeTunnelManager = SpatialBlend.Tests.TunnelFixtures.FakeTunnelManager;

namespace SpatialBlend.Tests
{
    /// <summary>The desktop spectator's contract: a core client that registers nothing and never hosts.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FreeformSpectatorTests
    {
        private readonly List<GameObject> _objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            LogAssert.ignoreFailingMessages = false;
        }

        private static FusionSessionEntry Room(string name, int players = 1, int max = 9, bool open = true) =>
            new FusionSessionEntry(name, players, max, open);

        // ---- the join decision ---------------------------------------------------------------------------------------

        [Test]
        public void ARoomOnTheListIsJoinedAsAClientNeverAHost()
        {
            var d = SpectatorJoinPolicy.Evaluate("Room 1", true, new[] { Room("Room 1") });
            NUnitAssert.That(d.Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Join));
            NUnitAssert.That(d.Role, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void ARoomThatIsNotListedIsWaitedForNeverCreated()
        {
            var d = SpectatorJoinPolicy.Evaluate("Room 9", true, new[] { Room("Room 1") });
            NUnitAssert.That(d.Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Wait));
            NUnitAssert.That(d.Role, Is.EqualTo(CoreParticipantRole.Client));
            NUnitAssert.That(SpectatorJoinPolicy.Evaluate("Room 9", true, new FusionSessionEntry[0]).Verdict,
                Is.EqualTo(SpectatorJoinPolicy.Verdict.Wait));
            NUnitAssert.That(SpectatorJoinPolicy.Evaluate("Room 1", false, null).Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Wait));
        }

        [Test]
        public void AFullClosedOrNamelessRoomIsRefusedWithAMessage()
        {
            var full = SpectatorJoinPolicy.Evaluate("A", true, new[] { Room("A", 9, 9) });
            var closed = SpectatorJoinPolicy.Evaluate("A", true, new[] { Room("A", 1, 9, open: false) });
            var nameless = SpectatorJoinPolicy.Evaluate("  ", true, new[] { Room("A") });
            NUnitAssert.That(full.Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Refuse));
            NUnitAssert.That(full.Error, Is.EqualTo(SpectatorJoinPolicy.FullError));
            NUnitAssert.That(closed.Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Refuse));
            NUnitAssert.That(nameless.Error, Is.EqualTo(SpectatorJoinPolicy.EmptyNameError));
        }

        [Test]
        public void ARoomWithFewerParticipantsThanItNeedsToStartItsAppsIsWaitedFor()
        {
            var few = SpectatorJoinPolicy.Evaluate("R", true, new[] { Room("R", 1) }, participantsRequired: 2);
            NUnitAssert.That(few.Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Wait));
            NUnitAssert.That(few.Reason, Is.EqualTo(SpectatorJoinPolicy.WaitReason.TooFewParticipants));
            NUnitAssert.That(few.Notice, Does.Contain("1 of the 2 players"));
            NUnitAssert.That(SpectatorJoinPolicy.Evaluate("R", true, new[] { Room("R", 2) }, participantsRequired: 2).Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Join));
            NUnitAssert.That(SpectatorJoinPolicy.Evaluate("R", true, new[] { Room("R", 1) }).Verdict, Is.EqualTo(SpectatorJoinPolicy.Verdict.Join), "no requirement given: as before");
        }

        [Test]
        public void ARunnerAloneInTheRoomItMasteredHasCreatedItAndLeaves()
        {
            NUnitAssert.That(SpectatorJoinPolicy.CreatedTheRoom(true, 1), Is.True);
            NUnitAssert.That(SpectatorJoinPolicy.CreatedTheRoom(true, 2), Is.False);
            NUnitAssert.That(SpectatorJoinPolicy.CreatedTheRoom(false, 2), Is.False);
        }

        [Test]
        public void TheRoomComesFromTheFlagThenTheEnvironment()
        {
            Func<string, string> env = n => n == SpectatorJoinPolicy.RoomEnvironmentVariable ? " FromEnv " : null;
            NUnitAssert.That(SpectatorJoinPolicy.ResolveRoom(new[] { "app", "-spectate-room", "Room 4" }, env), Is.EqualTo("Room 4"));
            NUnitAssert.That(SpectatorJoinPolicy.ResolveRoom(new[] { "app" }, env), Is.EqualTo("FromEnv"));
            NUnitAssert.That(SpectatorJoinPolicy.ResolveRoom(new[] { "app", "-spectate-room" }, n => null), Is.Null);
            NUnitAssert.That(SpectatorJoinPolicy.ResolveRoom(new[] { "-spectate-room", " " }, n => null), Is.Null);
        }

        // ---- the role through the real matchmaker and connection manager ------------------------------------------------

        [Test]
        public void TheSpectatorBrowserRefusesAnUnlistedRoomAndNeverHosts()
        {
            var go = new GameObject("spectator-browser");
            _objects.Add(go);
            var browser = go.AddComponent<TestSpectatorBrowser>();
            browser.Attach(go.AddComponent<FreeformSpectatorSession>(), null);
            browser.ApplySessions(new[] { Room("Room 1") });
            Run(browser.JoinAsync("Room 7"));
            NUnitAssert.That(browser.Calls, Does.Not.Contain("start:Room 7"), "an unlisted room is never started (Shared mode would create it)");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Failed));
            NUnitAssert.That(browser.ParticipantRole, Is.Not.EqualTo(CoreParticipantRole.Host));
        }

        [Test]
        public void AListedRoomIsStartedWithTheClientRole()
        {
            var go = new GameObject("spectator-browser");
            _objects.Add(go);
            var browser = go.AddComponent<TestSpectatorBrowser>();
            browser.Attach(go.AddComponent<FreeformSpectatorSession>(), null);
            browser.ApplySessions(new[] { Room("Room 1") });
            NUnitAssert.That(Run(browser.JoinAsync("Room 1")), Is.True);
            NUnitAssert.That(browser.Calls, Does.Contain("start:Room 1"));
            NUnitAssert.That(browser.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
        }

        [Test]
        public void AClientThatFusionMakesMasterStillNeverHostsTheCore()
        {
            var go = new GameObject("spectator-connection");
            _objects.Add(go);
            var connection = go.AddComponent<MasterConnection>();
            var mm = new FreeformMatchmaking();
            mm.Refresh(new[] { new MatchmakingRoom("Room 1", 1, 9) });
            var decision = mm.TryJoin("Room 1");
            NUnitAssert.That(decision.Role, Is.EqualTo(CoreParticipantRole.Client));
            connection.AdoptMatchmakingDecision(decision);
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
            connection.OnPlayerJoined(null, default);
            NUnitAssert.That(connection.HostCalls, Is.EqualTo(0), "even as the Shared-mode master, a spectator never hosts the core");
        }

        // ---- the app registers nothing --------------------------------------------------------------------------------

        private FakeTunnelManager StartedManagerWithSpectatorApp(out FreeformSpectatorApp app)
        {
            var managerObject = new GameObject("spectator-core");
            _objects.Add(managerObject);
            managerObject.SetActive(false);
            var manager = managerObject.AddComponent<FakeTunnelManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            app = managerObject.AddComponent<FreeformSpectatorApp>();
            app.SetSpaceManager(manager);
            // A connected core whose apps have been started by the manager, as the connection round does.
            manager.HostTunneled();
            NUnitAssert.That(manager.Initialized, Is.True);
            typeof(SpatialBlendManager).GetMethod("RegisterApp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, new object[] { app });
            typeof(SpatialBlendManager).GetMethod("StartApps", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
            return manager;
        }

        [Test]
        public void StartingTheSpectatorAppRegistersNoActorAvatarOrEntity()
        {
            var manager = StartedManagerWithSpectatorApp(out var app);
            NUnitAssert.That(app.IsAppStarted, Is.True, "the manager started the app");
            NUnitAssert.That(manager.Entities.Count, Is.EqualTo(0), "no entity registered");
            var actors = (System.Collections.IDictionary)typeof(SpatialBlendManager)
                .GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            NUnitAssert.That(actors.Count, Is.EqualTo(0), "no actor registered");
            NUnitAssert.That(manager.Relations.Count, Is.EqualTo(0), "no relation declared");
            NUnitAssert.That(UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsSortMode.None).Length, Is.EqualTo(0), "no embodiment");
        }

        /// <summary>One inbound share through the manager's CompleteShare, to the spectator app's OnShare. Returns whether it was attached.</summary>
        private bool ShareThroughTheManager(FakeTunnelManager manager, GameObject received, SbEntity entity)
        {
            var data = new SerializationData(System.Runtime.InteropServices.Marshal.AllocHGlobal(16), 16, false);
            for (int i = 0; i < 16; i++) System.Runtime.InteropServices.Marshal.WriteByte(data.DataPtr, i, 0);
            try
            {
                typeof(SpatialBlendManager).GetMethod("CompleteShare", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager,
                    new object[] { manager.ID, 7001L, received, entity, data, 1000L, manager.ConnectionGeneration, 0L, 0L });
            }
            catch (TargetInvocationException)
            {
                // What follows the app's OnObjectAdded for an accepted share is the native core (not available to a fake manager);
                // the decision and the app notification, which this test is about, come before it.
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(data.DataPtr); }
            return entity.IsAttached();
        }

        [Test]
        public void AnAvatarShareGoesThroughTheManagerToTheAppIsRejectedAndMakesNoActor()
        {
            var manager = StartedManagerWithSpectatorApp(out var app);
            var avatar = new GameObject("remote-avatar");
            _objects.Add(avatar);
            avatar.tag = "Avatar";
            var entity = avatar.AddComponent<SbLocalEntity>();
            ShareThroughTheManager(manager, avatar, entity);
            NUnitAssert.That(app.AddedEver, Is.EqualTo(0), "the app was never told an object was added");
            NUnitAssert.That(entity.IsAttached(), Is.False, "the received avatar was not taken into the core");
            var actors = (System.Collections.IDictionary)typeof(SpatialBlendManager)
                .GetField("_v2ActorByEntity", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            NUnitAssert.That(actors.Count, Is.EqualTo(0), "no receiver actor was created for it");
            NUnitAssert.That(manager.Entities.Count, Is.EqualTo(0));
        }

        [Test]
        public void ThePositiveControlAnOrdinaryShareThroughTheSameCallIsAccepted()
        {
            var manager = StartedManagerWithSpectatorApp(out var app);
            // An accepted share continues into the native core, which this fake manager does not have: it logs an error there.
            LogAssert.ignoreFailingMessages = true;
            var plain = new GameObject("remote-object");
            _objects.Add(plain);
            var entity = plain.AddComponent<SbLocalEntity>();
            bool attached = ShareThroughTheManager(manager, plain, entity);
            NUnitAssert.That(attached, Is.True, "the ordinary share was attached (so the avatar test above can fail)");
            NUnitAssert.That(app.AddedEver, Is.EqualTo(1), "the app was told it was added");
        }

        [Test]
        public void AReceivedAvatarIsNotTakenIntoTheCoreButOtherContentIs()
        {
            NUnitAssert.That(SpectatorJoinPolicy.ShareResponse(true).result, Is.EqualTo(OnShareResponse.Result.Reject));
            NUnitAssert.That(SpectatorJoinPolicy.ShareResponse(false).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            var go = new GameObject("avatar");
            _objects.Add(go);
            go.tag = "Avatar";
            var app = go.AddComponent<FreeformSpectatorApp>();
            NUnitAssert.That(app.OnShare(go).result, Is.EqualTo(OnShareResponse.Result.Reject));
            var plain = new GameObject("object");
            _objects.Add(plain);
            NUnitAssert.That(app.OnShare(plain).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
        }

        [Test]
        public void AnArrivedObjectIsTrackedShownAndForgottenOnRemoval()
        {
            var go = new GameObject("app");
            _objects.Add(go);
            var app = go.AddComponent<FreeformSpectatorApp>();
            var received = new GameObject("received");
            _objects.Add(received);
            GameObject seen = null;
            app.ObjectArrived += o => seen = o;
            app.OnObjectAdded(received, SbApp.OnObjectAddedType.Added);
            NUnitAssert.That(seen, Is.SameAs(received));
            NUnitAssert.That(app.ReceivedCount, Is.EqualTo(1));
            NUnitAssert.That(received.GetComponentInChildren<Renderer>(), Is.Not.Null, "geometry-free content gets a marker");
            app.OnObjectAdded(received, SbApp.OnObjectAddedType.Added);
            NUnitAssert.That(app.ReceivedCount, Is.EqualTo(1));
            NUnitAssert.That(app.TryGetContentBounds(out _), Is.True);
            app.OnRemove(received);
            NUnitAssert.That(app.ReceivedCount, Is.EqualTo(0));
        }

        private static T Run<T>(Task<T> task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True);
            return task.GetAwaiter().GetResult();
        }

        private sealed class TestSpectatorBrowser : SpectatorSessionBrowser
        {
            public readonly List<string> Calls = new();
            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);
            protected override Task<string> JoinLobbyAsync() => Task.FromResult<string>(null);
            protected override Task CloseLobbyAsync() => Task.CompletedTask;
        }

        private sealed class MasterConnection : FusionConnectionManager
        {
            public int HostCalls;
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
