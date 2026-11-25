// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Against the real Photon Cloud: a session hosted by one runner is
    /// listed by FusionSessionBrowser's lobby with its occupancy and the
    /// project's player limit, and joining it through the browser adds a
    /// player to the host. Needs network and PhotonAppSettings.AppIdFusion
    /// (see SpatialBlend.Editor.PhotonAppIdSetup); ignored without an App ID.
    /// </summary>
    [TestFixture]
    [Category("PhotonCloud")]
    public class FusionSessionBrowserPhotonCloudTests
    {
        private const float StepTimeoutSeconds = 30f;
        private readonly List<NetworkRunner> _runners = new();
        private readonly List<GameObject> _objects = new();
        private NetworkProjectConfig.PeerModes _previousPeerMode;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!PhotonAppSettings.TryGetGlobal(out var settings) ||
                !Guid.TryParse(settings.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            // Host and joiner are two game runners in one process.
            _previousPeerMode = NetworkProjectConfig.Global.PeerMode;
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Multiple;
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var runner in _runners.Where(r => r != null))
            {
                var shutdown = runner.Shutdown();
                yield return WaitFor(shutdown, "runner shutdown");
            }
            _runners.Clear();
            foreach (var go in _objects.Where(o => o != null)) UnityEngine.Object.Destroy(go);
            _objects.Clear();
            NetworkProjectConfig.Global.PeerMode = _previousPeerMode;
        }

        [UnityTest]
        public IEnumerator BrowserReachesTheSharedLobby()
        {
            var browserObject = new GameObject("photon-cloud-lobby");
            _objects.Add(browserObject);
            var browser = browserObject.AddComponent<FusionSessionBrowser>();
            var lobby = browser.OpenLobbyAsync();
            yield return WaitFor(lobby, "lobby join");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby), browser.LastError);
            yield return WaitUntil(() => browser.HasSessionList, "first session list");
            browser.CloseLobby();
        }

        [UnityTest]
        public IEnumerator HostedSessionIsListedWithOccupancyAndJoinable()
        {
            var sessionName = "spatialblend-it-" + Guid.NewGuid().ToString("N").Substring(0, 12);

            var host = CreateRunner("host");
            var hosted = host.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = SpatialBlendSettings.DefaultMaxPlayers,
                SceneManager = host.gameObject.AddComponent<NetworkSceneManagerDefault>(),
            });
            yield return WaitFor(hosted, "host StartGame");
            NUnitAssert.That(hosted.Result.Ok, Is.True, $"host failed: {hosted.Result.ShutdownReason}");

            var browserObject = new GameObject("photon-cloud-browser");
            _objects.Add(browserObject);
            var browser = browserObject.AddComponent<CloudBrowser>();
            browser.Test = this;

            var lobby = browser.OpenLobbyAsync();
            yield return WaitFor(lobby, "lobby join");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby), browser.LastError);

            yield return WaitUntil(() => browser.Sessions.Any(s => s.Name == sessionName), "session listed in lobby");
            var entry = browser.Sessions.First(s => s.Name == sessionName);
            NUnitAssert.That(entry.PlayerCount, Is.EqualTo(1));
            NUnitAssert.That(entry.MaxPlayers, Is.EqualTo(SpatialBlendSettings.DefaultMaxPlayers), "SpatialBlendSettings.maxPlayers, passed as StartGameArgs.PlayerCount, is the room limit");
            NUnitAssert.That(entry.CanJoin, Is.True);

            var join = browser.JoinAsync(sessionName);
            yield return WaitFor(join, "browser join");
            NUnitAssert.That(join.Result, Is.True);
            yield return WaitFor(browser.Started, "joining runner StartGame");
            NUnitAssert.That(browser.Started.Result.Ok, Is.True, $"join failed: {browser.Started.Result.ShutdownReason}");
            yield return WaitUntil(() => host.ActivePlayers.Count() == 2, "host sees the joined player");
        }

        // Against the real Photon Cloud: the room list is stale (it still shows a place) when the join reaches a room that has since
        // filled up or closed. Photon refuses the join (GameIsFull / GameClosed), the runner shuts itself down, and the browser must be
        // usable again: not Starting, not InSession, and a new search lists the room anew.
        [UnityTest]
        public IEnumerator JoiningAFullRoomFails_AndFindARoomWorksAgain()
        {
            var sessionName = "spatialblend-it-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var host = CreateRunner("host");
            var hosted = host.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = 1,   // the host fills it
                SceneManager = host.gameObject.AddComponent<NetworkSceneManagerDefault>(),
            });
            yield return WaitFor(hosted, "host StartGame");
            NUnitAssert.That(hosted.Result.Ok, Is.True, $"host failed: {hosted.Result.ShutdownReason}");
            yield return JoinStaleRoomAndRecover(sessionName, ShutdownReason.GameIsFull);
        }

        [UnityTest]
        public IEnumerator JoiningAClosedRoomFails_AndFindARoomWorksAgain()
        {
            var sessionName = "spatialblend-it-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var host = CreateRunner("host");
            var hosted = host.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = SpatialBlendSettings.DefaultMaxPlayers,
                SceneManager = host.gameObject.AddComponent<NetworkSceneManagerDefault>(),
            });
            yield return WaitFor(hosted, "host StartGame");
            NUnitAssert.That(hosted.Result.Ok, Is.True, $"host failed: {hosted.Result.ShutdownReason}");
            host.SessionInfo.IsOpen = false;
            yield return JoinStaleRoomAndRecover(sessionName, ShutdownReason.GameClosed);
        }

        private IEnumerator JoinStaleRoomAndRecover(string sessionName, ShutdownReason expected)
        {
            var browserObject = new GameObject("photon-cloud-browser");
            _objects.Add(browserObject);
            var browser = browserObject.AddComponent<CloudBrowser>();
            browser.Test = this;
            var bootstrapObject = new GameObject("photon-cloud-bootstrap");
            _objects.Add(bootstrapObject);
            browser.Bootstrap = bootstrapObject.AddComponent<CloudBootstrap>();
            browser.Bootstrap.StartFailed += browser.Bootstrap.FailureReasons.Add;
            var template = new GameObject("photon-cloud-runner-template");
            _objects.Add(template);
            browser.Bootstrap.RunnerPrefab = template.AddComponent<NetworkRunner>();
            yield return WaitFor(browser.OpenLobbyAsync(), "lobby join");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby), browser.LastError);
            yield return WaitUntil(() => browser.Sessions.Any(s => s.Name == sessionName), "session listed in lobby");
            // The list in the player's hand is older than the room: it still shows a place and an open door.
            browser.ApplySessions(new[] { new FusionSessionEntry(sessionName, 1, SpatialBlendSettings.DefaultMaxPlayers) });
            var join = browser.JoinAsync(sessionName);
            yield return WaitFor(join, "browser join");
            NUnitAssert.That(join.Result, Is.True, "the join was accepted from the stale list");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Starting));

            // Photon refuses the join; the real SbFusionBootstrap reports it (StartFailed) and the browser recovers by itself.
            yield return WaitUntil(() => browser.State != FusionSessionBrowserState.Starting, "the failed start reaching the browser");
            NUnitAssert.That(browser.Bootstrap.FailureReasons, Is.EqualTo(new[] { expected }), "Photon's own reason");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Idle));
            NUnitAssert.That(browser.InSession, Is.False);
            NUnitAssert.That(browser.LastError, Is.EqualTo(JoinFailure.ToUserMessage(expected)));
            NUnitAssert.That(browser.Resets, Is.EqualTo(1));
            JoinFailure.Take();

            // Find a room again: a new lobby search lists the room.
            yield return WaitFor(browser.OpenLobbyAsync(), "second lobby join");
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby), browser.LastError);
            yield return WaitUntil(() => browser.HasSessionList && browser.Sessions.Any(s => s.Name == sessionName), "room listed by the second search");
            var fresh = browser.Sessions.First(s => s.Name == sessionName);
            NUnitAssert.That(fresh.CanJoin, Is.False, "and now the list tells the truth: " + fresh.Occupancy);
            browser.CloseLobby();
        }

        private NetworkRunner CreateRunner(string name)
        {
            var go = new GameObject("photon-cloud-" + name);
            _objects.Add(go);
            var runner = go.AddComponent<NetworkRunner>();
            _runners.Add(runner);
            return runner;
        }

        private static IEnumerator WaitFor(Task task, string what)
        {
            yield return WaitUntil(() => task.IsCompleted, what);
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static IEnumerator WaitUntil(Func<bool> done, string what)
        {
            var deadline = Time.realtimeSinceStartup + StepTimeoutSeconds;
            while (!done())
            {
                if (Time.realtimeSinceStartup > deadline)
                    NUnitAssert.Fail($"Timed out after {StepTimeoutSeconds}s waiting for {what}.");
                yield return null;
            }
        }

        // The real lobby path; only the final bootstrap start is replaced by a
        // second runner, since one process cannot drive two FusionBootstraps.
        private sealed class CloudBrowser : FusionSessionBrowser
        {
            public FusionSessionBrowserPhotonCloudTests Test;
            public Task<StartGameResult> Started;
            public int Resets;
            public CloudBootstrap Bootstrap;

            protected override bool CanStartSession => true;
            protected override void ResetAfterFailedStart() => Resets++;

            protected override void StartSession(string sessionName)
            {
                if (Bootstrap != null)
                {
                    // The failed-join tests start through the real SbFusionBootstrap (its AddClient is the stock path StartSharedClient
                    // ends in), so the failure is reported by the product's own event.
                    Bootstrap.DefaultRoomName = sessionName;
                    ObserveStart(Bootstrap);
                    _ = Bootstrap.AddClient(GameMode.Shared, default);
                    return;
                }
                var runner = Test.CreateRunner("joiner");
                Started = runner.StartGame(new StartGameArgs
                {
                    GameMode = GameMode.Shared,
                    SessionName = sessionName,
                    PlayerCount = SpatialBlendSettings.DefaultMaxPlayers,
                    SceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>(),
                });
            }
        }

        private sealed class CloudBootstrap : SbFusionBootstrap
        {
            public readonly List<ShutdownReason> FailureReasons = new();
        }
    }
}
