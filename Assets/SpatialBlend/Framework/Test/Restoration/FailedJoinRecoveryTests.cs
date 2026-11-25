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
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The failure this guards: after a failed join (room full, closed or gone, timeout, network error) Find a room did nothing. The runner start ended
    /// without the player in the room, but nothing told the room list: the browser stayed in Starting (which refuses to open the lobby again
    /// and hides the list), the matchmaker kept the player InSession and the connection manager kept its client role.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FailedJoinRecoveryTests
    {
        private readonly List<GameObject> _objects = new();

        [SetUp]
        public void SetUp() => JoinFailure.Take();

        [TearDown]
        public void TearDown()
        {
            JoinFailure.Take();
            foreach (var go in _objects.Where(go => go != null)) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private static readonly ShutdownReason[] FailureKinds =
        {
            ShutdownReason.GameIsFull,            // room full
            ShutdownReason.GameClosed,            // room closed
            ShutdownReason.GameNotFound,          // room gone
            ShutdownReason.PhotonCloudTimeout,    // timeout
            ShutdownReason.ConnectionTimeout,     // timeout
            ShutdownReason.OperationTimeout,      // timeout
            ShutdownReason.Error,                 // network error
            ShutdownReason.ConnectionRefused,     // network error
        };

        [TestCaseSource(nameof(FailureKinds))]
        public void AFailedJoin_ClearsEveryFlag_AndFindARoomSearchesAgain(ShutdownReason reason)
        {
            var browser = NewBrowser(out var connection);
            var panel = SessionListPanel.Create(NewTemplateButton(), browser);
            _objects.Add(panel.gameObject);

            // Find a room, pick Room 1, the start begins.
            panel.Show();
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 2, 9) });
            NUnitAssert.That(panel.RowObjects.Count, Is.EqualTo(1));
            panel.RowObjects[0].GetComponent<Button>().onClick.Invoke();
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Starting));
            NUnitAssert.That(browser.InSession, Is.True);
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Client));
            NUnitAssert.That(panel.gameObject.activeSelf, Is.False, "the list closes once a session starts");

            // The runner could not join.
            browser.ReportStartFailed(reason);

            NUnitAssert.That(browser.State, Is.Not.EqualTo(FusionSessionBrowserState.Starting), "the busy state is cleared");
            NUnitAssert.That(browser.InSession, Is.False, "the matchmaker no longer has the player in a session");
            NUnitAssert.That(browser.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            NUnitAssert.That(browser.MatchmakingPhase, Is.EqualTo(MatchmakingPhase.Browsing));
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None), "the core client role is dropped");
            NUnitAssert.That(connection.HostLeft, Is.False, "the player's own failed join is not a lost host");
            NUnitAssert.That(browser.LastError, Is.EqualTo(JoinFailure.ToUserMessage(reason)), "the error is kept for the user");
            NUnitAssert.That(browser.LastError, Is.Not.Empty);
            NUnitAssert.That(browser.Resets, Is.EqualTo(1), "the runner and the network stack are torn down once");
            NUnitAssert.That(JoinFailure.Take(), Is.EqualTo(JoinFailure.ToUserMessage(reason)), "and the sentence survives the reset");

            // Find a room again: a new search runs and its result shows.
            panel.Show();
            NUnitAssert.That(browser.Calls.Count(c => c == "join-lobby"), Is.EqualTo(2), "a second search started");
            NUnitAssert.That(panel.StatusText, Is.EqualTo("Looking for rooms…"));
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 2", 1, 9) });
            NUnitAssert.That(panel.gameObject.activeSelf, Is.True);
            NUnitAssert.That(panel.RowObjects.Count, Is.EqualTo(1));
            StringAssert.Contains("Room 2", panel.RowObjects[0].GetComponentInChildren<TMP_Text>().text);
            NUnitAssert.That(panel.NewRoomInteractable, Is.True);

            // And the same player can join again.
            NUnitAssert.That(Await(browser.JoinAsync("Room 2")), Is.True);
            NUnitAssert.That(browser.Calls.Last(), Is.EqualTo("start:Room 2"));
        }

        [Test]
        public void AFailedHost_IsRecoveredTheSameWay()
        {
            var browser = NewBrowser(out var connection);
            Await(browser.OpenLobbyAsync());
            browser.ApplySessions(new FusionSessionEntry[0]);
            browser.CreateRoom();
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Starting));
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.Host));

            browser.ReportStartFailed(ShutdownReason.PhotonCloudTimeout);

            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Idle));
            NUnitAssert.That(browser.InSession, Is.False);
            NUnitAssert.That(connection.ParticipantRole, Is.EqualTo(CoreParticipantRole.None));
            Await(browser.OpenLobbyAsync());
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby));
        }

        [Test]
        public void ASecondReportOfTheSameFailure_ResetsOnce()
        {
            var browser = NewBrowser(out _);
            Await(browser.OpenLobbyAsync());
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 2, 9) });
            Await(browser.JoinAsync("Room 1"));
            browser.ReportStartFailed(ShutdownReason.GameIsFull);
            browser.ReportStartFailed(ShutdownReason.GameIsFull);
            NUnitAssert.That(browser.Resets, Is.EqualTo(1));
        }

        [Test]
        public void EveryFailureKindHasAPlainSentence_AndNoTechnicalName()
        {
            foreach (ShutdownReason reason in System.Enum.GetValues(typeof(ShutdownReason)))
            {
                var text = JoinFailure.ToUserMessage(reason);
                NUnitAssert.That(text, Is.Not.Null.And.Not.Empty, reason.ToString());
                NUnitAssert.That(text, Does.Not.Contain(reason.ToString()).Or.EqualTo(JoinFailure.Fallback), reason.ToString());
            }
            NUnitAssert.That(JoinFailure.ToUserMessage(ShutdownReason.GameIsFull), Does.Contain("full"));
            NUnitAssert.That(JoinFailure.ToUserMessage(ShutdownReason.GameClosed), Does.Contain("closed"));
            NUnitAssert.That(JoinFailure.ToUserMessage(ShutdownReason.GameNotFound), Does.Contain("gone"));
            NUnitAssert.That(JoinFailure.ToUserMessage(ShutdownReason.PhotonCloudTimeout), Does.Contain("too long"));
            NUnitAssert.That(JoinFailure.ToUserMessage(ShutdownReason.Error), Does.Contain("Wi-Fi"));
        }

        [Test]
        public void TheBootstrap_ReportsAStartThatDidNotJoin_AndAThrownOne()
        {
            var go = new GameObject("bootstrap");
            _objects.Add(go);
            var bootstrap = go.AddComponent<FailingBootstrap>();
            var seen = new List<ShutdownReason>();
            bootstrap.StartFailed += seen.Add;

            bootstrap.Outcome = (true, ShutdownReason.Ok);
            Await(bootstrap.Drive());
            NUnitAssert.That(seen, Is.Empty, "a join that worked reports nothing");

            bootstrap.Outcome = (false, ShutdownReason.GameIsFull);
            Await(bootstrap.Drive());
            bootstrap.Throws = true;
            Await(bootstrap.Drive());
            NUnitAssert.That(seen, Is.EqualTo(new[] { ShutdownReason.GameIsFull, ShutdownReason.Error }));
        }

        [Test]
        public void TheNewControlPanel_ShowsTheCarriedSentenceOnce()
        {
            JoinFailure.Carry(JoinFailure.ToUserMessage(ShutdownReason.GameIsFull));
            var canvas = new GameObject("panel-canvas", typeof(RectTransform), typeof(Canvas));
            _objects.Add(canvas);
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(406f, 300f);
            var template = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            template.transform.SetParent(canvas.transform, false);
            new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).transform.SetParent(template.transform, false);
            var panelObject = new GameObject("control-panel");
            _objects.Add(panelObject);
            panelObject.SetActive(false);
            var panel = panelObject.AddComponent<ControlPanel>();
            typeof(ControlPanel).GetField("_connect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(panel, template.GetComponent<Button>());
            NUnitAssert.That(panel.BuildGeneratedLayout(), Is.True);

            panel.ShowPendingJoinNotice();

            var overlay = (UiToastOverlay)typeof(ControlPanel).GetField("_toastOverlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(panel);
            _objects.Add(overlay.gameObject);
            var texts = overlay.Host.GetComponentsInChildren<TMP_Text>().Select(t => t.text).ToArray();
            NUnitAssert.That(texts, Does.Contain(JoinFailure.Title));
            NUnitAssert.That(texts, Does.Contain(JoinFailure.ToUserMessage(ShutdownReason.GameIsFull)));
            NUnitAssert.That(JoinFailure.HasNotice, Is.False, "taken once");
            panel.ShowPendingJoinNotice();
            NUnitAssert.That(overlay.Host.GetComponentsInChildren<TMP_Text>().Count(t => t.text == JoinFailure.Title), Is.EqualTo(1), "and not shown again");
        }

        // ---- fakes -------------------------------------------------------------------------------------------------------

        private TestBrowser NewBrowser(out TestConnection connection)
        {
            var go = new GameObject("failed-join-browser");
            _objects.Add(go);
            connection = go.AddComponent<TestConnection>();
            var browser = go.AddComponent<TestBrowser>();
            browser.Configure(null, connection);
            return browser;
        }

        private Button NewTemplateButton()
        {
            var canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _objects.Add(canvas);
            var buttonObject = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvas.transform, false);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(buttonObject.transform, false);
            return buttonObject.GetComponent<Button>();
        }

        private static void Await(Task task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True);
            task.GetAwaiter().GetResult();
        }

        private static T Await<T>(Task<T> task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>A connection manager with the Fusion-facing calls replaced (as in the other contract tests), so it runs without a runner.</summary>
        public sealed class TestConnection : FusionConnectionManager
        {
            protected override bool IsAuthority => true;
            protected override SpatialBlendConnectionState ConnectionState => SpatialBlendConnectionState.Disconnected;
            protected override void RequestHostTunneled() { }
            protected override void RequestDisconnect() { }
            protected override void BroadcastJoinRound(long epoch) { }
            protected override void BroadcastRetire(long epoch) { }
        }

        /// <summary>The browser with the lobby and the runner start replaced by recorders: the start "succeeds" until told otherwise.</summary>
        private sealed class TestBrowser : FusionSessionBrowser
        {
            public readonly List<string> Calls = new();
            public int Resets;

            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);
            protected override void ResetAfterFailedStart() => Resets++;

            protected override Task<string> JoinLobbyAsync()
            {
                Calls.Add("join-lobby");
                return Task.FromResult<string>(null);
            }

            protected override Task CloseLobbyAsync() => Task.CompletedTask;
        }

        private sealed class FailingBootstrap : SbFusionBootstrap
        {
            public (bool Ok, ShutdownReason Reason) Outcome = (true, ShutdownReason.Ok);
            public bool Throws;

            protected override Task<(bool Ok, ShutdownReason Reason)> StartRunnerAsync(NetworkRunner runner, StartGameArgs args)
            {
                if (Throws) throw new System.InvalidOperationException("boom");
                return Task.FromResult(Outcome);
            }

            public Task Drive() => StartAndReportAsync(null, default);
        }
    }
}
#endif
