// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class FusionSessionBrowserTests
    {
        private readonly List<GameObject> _objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects.Where(go => go != null)) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        [Test]
        public void ListHidesInvisibleAndDuplicateSessionsAndSortsByName()
        {
            var list = FusionSessionList.Build(new[]
            {
                new FusionSessionEntry("Room 2", 1, 9),
                new FusionSessionEntry("Hidden", 1, 9, isVisible: false),
                new FusionSessionEntry("Room 1", 3, 9),
                new FusionSessionEntry("Room 2", 5, 9),
                new FusionSessionEntry(" ", 1, 9),
            });
            NUnitAssert.That(list.Select(s => s.Name), Is.EqualTo(new[] { "Room 1", "Room 2" }));
            NUnitAssert.That(list[1].PlayerCount, Is.EqualTo(1));
        }

        [Test]
        public void FullOrClosedSessionsAreListedButNotJoinable()
        {
            var full = new FusionSessionEntry("A", 9, 9);
            var closed = new FusionSessionEntry("B", 1, 9, isOpen: false);
            var open = new FusionSessionEntry("C", 8, 9);
            NUnitAssert.That(full.IsFull && !full.CanJoin, Is.True);
            NUnitAssert.That(closed.CanJoin, Is.False);
            NUnitAssert.That(open.CanJoin, Is.True);
            NUnitAssert.That(open.Occupancy, Is.EqualTo("8/9"));
        }

        [Test]
        public void NewRoomNameTakesSmallestUnlistedNumber()
        {
            NUnitAssert.That(FusionSessionList.NewRoomName(null), Is.EqualTo("Room 1"));
            NUnitAssert.That(FusionSessionList.NewRoomName(new[]
            {
                new FusionSessionEntry("Room 1", 1, 9),
                new FusionSessionEntry("Room 3", 1, 9),
            }), Is.EqualTo("Room 2"));
        }

        [Test]
        public void ProjectAdmitsNineParticipantsPerSession()
        {
            // The peer-mode sizing audit bounds the local core at nine; Fusion must
            // not admit a tenth client that the core would then refuse.
            var config = Resources.Load<TextAsset>("NetworkProjectConfig");
            var text = config != null ? config.text :
                System.IO.File.ReadAllText("Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion");
            StringAssert.Contains("\"PlayerCount\": 9,", text);
        }

        [Test]
        public void JoiningListedSessionLeavesLobbyThenStartsThatSession()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby));
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 2, 9) });

            NUnitAssert.That(Done(browser.JoinAsync("Room 1")), Is.True);
            NUnitAssert.That(browser.Calls, Is.EqualTo(new[] { "join-lobby", "close-lobby", "start:Room 1" }));
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Starting));
            NUnitAssert.That(Done(browser.JoinAsync("Room 1")), Is.False, "a second click must not start twice");
        }

        [Test]
        public void FullSessionIsRefusedWithoutLeavingTheLobby()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 9, 9) });
            NUnitAssert.That(Done(browser.JoinAsync("Room 1")), Is.False);
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby));
            NUnitAssert.That(browser.Calls, Is.EqualTo(new[] { "join-lobby" }));
        }

        [Test]
        public void CreateRoomStartsAnUnlistedName()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 1, 9) });
            browser.CreateRoom();
            NUnitAssert.That(browser.Calls.Last(), Is.EqualTo("start:Room 2"));
        }

        [Test]
        public void AnObserverCannotCreateARoomFromTheList()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            browser.SetHostingAllowed(false);
            var panel = SessionListPanel.Create(CreateTemplateButton(), browser);
            _objects.Add(panel.gameObject);
            panel.Show();
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 1, 9) });
            NUnitAssert.That(panel.NewRoomInteractable, Is.False);
            NUnitAssert.That(panel.StatusText, Is.EqualTo(FreeformMatchmaking.ObserverHostError));
            NUnitAssert.That(panel.RowObjects.Count, Is.EqualTo(1), "the existing room stays joinable");
            browser.CreateRoom();
            NUnitAssert.That(browser.Calls.Any(call => call.StartsWith("start:")), Is.False);
        }

        [Test]
        public void RunningFusionRefusesToStartAgain()
        {
            var browser = CreateBrowser();
            browser.CanStart = false;
            Done(browser.OpenLobbyAsync());
            NUnitAssert.That(Done(browser.JoinAsync("Room 1")), Is.False);
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Failed));
            NUnitAssert.That(browser.Calls.Any(c => c.StartsWith("start:")), Is.False);
        }

        [Test]
        public void LobbyFailureIsReportedAndCanBeRetried()
        {
            var browser = CreateBrowser();
            browser.LobbyError = "offline";
            Done(browser.OpenLobbyAsync());
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.Failed));
            NUnitAssert.That(browser.LastError, Is.EqualTo("offline"));

            browser.LobbyError = null;
            Done(browser.OpenLobbyAsync());
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby));
            NUnitAssert.That(browser.LastError, Is.Null);
        }

        [Test]
        public void StaleLobbyAttemptCannotCloseOrAdvanceANewerOne()
        {
            // Run continuations inline so each completion is observed deterministically.
            var context = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                RunStaleLobbyAttempt();
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            }
        }

        private void RunStaleLobbyAttempt()
        {
            var browser = CreateBrowser();
            var first = new TaskCompletionSource<string>();
            var second = new TaskCompletionSource<string>();
            browser.PendingLobby = first;
            var firstOpen = browser.OpenLobbyAsync();
            browser.CloseLobby();
            browser.PendingLobby = second;
            var secondOpen = browser.OpenLobbyAsync();
            var callsBefore = browser.Calls.Count;

            first.SetResult("stale failure");
            Done(firstOpen);
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.JoiningLobby));
            NUnitAssert.That(browser.Calls.Count, Is.EqualTo(callsBefore), "the stale attempt must not close the new lobby");
            NUnitAssert.That(browser.LastError, Is.Null);

            second.SetResult(null);
            Done(secondOpen);
            NUnitAssert.That(browser.State, Is.EqualTo(FusionSessionBrowserState.InLobby));
        }

        [Test]
        public void NewRoomWaitsForTheFirstSessionList()
        {
            var browser = CreateBrowser();
            Done(browser.OpenLobbyAsync());
            NUnitAssert.That(browser.HasSessionList, Is.False);
            browser.CreateRoom();
            NUnitAssert.That(browser.Calls.Any(c => c.StartsWith("start:")), Is.False);

            var panel = SessionListPanel.Create(CreateTemplateButton(), browser);
            _objects.Add(panel.gameObject);
            panel.Show();
            NUnitAssert.That(panel.StatusText, Is.EqualTo("Looking for rooms…"));
            NUnitAssert.That(panel.NewRoomInteractable, Is.False);

            browser.ApplySessions(new FusionSessionEntry[0]);
            NUnitAssert.That(panel.NewRoomInteractable, Is.True);
            browser.CreateRoom();
            NUnitAssert.That(browser.Calls.Last(), Is.EqualTo("start:Room 1"));
        }

        [Test]
        public void PanelOverlaysALaidOutCanvasInsteadOfJoiningItsColumn()
        {
            // The shipped control panel canvas is itself a VerticalLayoutGroup.
            var template = CreateTemplateButton();
            var canvas = (RectTransform)template.transform.parent;
            canvas.sizeDelta = new Vector2(400, 300);
            var column = canvas.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childControlWidth = false;
            column.childControlHeight = true;
            var browser = CreateBrowser();
            var panel = SessionListPanel.Create(template, browser);
            _objects.Add(panel.gameObject);
            panel.Show();
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 1, 9) });
            LayoutRebuilder.ForceRebuildLayoutImmediate(canvas);

            var rect = (RectTransform)panel.transform;
            NUnitAssert.That(panel.GetComponent<LayoutElement>().ignoreLayout, Is.True);
            NUnitAssert.That(rect.rect.width, Is.EqualTo(400).Within(0.01f));
            NUnitAssert.That(rect.rect.height, Is.GreaterThanOrEqualTo(300 - 0.01f));
            NUnitAssert.That(((RectTransform)panel.RowObjects[0].transform).rect.width, Is.GreaterThan(0));
        }

        [Test]
        public void PanelRendersOneRowPerSessionAndDisablesFullOnes()
        {
            var browser = CreateBrowser();
            var panel = SessionListPanel.Create(CreateTemplateButton(), browser);
            _objects.Add(panel.gameObject);
            panel.Show();
            browser.ApplySessions(new[]
            {
                new FusionSessionEntry("Room 1", 2, 9),
                new FusionSessionEntry("Room 2", 9, 9),
            });

            NUnitAssert.That(panel.RowObjects.Count, Is.EqualTo(2));
            var rows = panel.RowObjects.Select(r => r.GetComponent<Button>()).ToArray();
            NUnitAssert.That(rows[0].interactable, Is.True);
            NUnitAssert.That(rows[1].interactable, Is.False);
            StringAssert.Contains("2/9", rows[0].GetComponentInChildren<TMP_Text>().text);

            rows[0].onClick.Invoke();
            NUnitAssert.That(browser.Calls.Last(), Is.EqualTo("start:Room 1"));
            NUnitAssert.That(panel.gameObject.activeSelf, Is.False, "the list closes once a session starts");
        }

        [Test]
        public void PanelCapsRowsAndCountsTheRest()
        {
            var browser = CreateBrowser();
            var panel = SessionListPanel.Create(CreateTemplateButton(), browser);
            _objects.Add(panel.gameObject);
            panel.Show();
            browser.ApplySessions(Enumerable.Range(1, SessionListPanel.MaxRows + 2)
                .Select(i => new FusionSessionEntry($"Room {i:00}", 1, 9)));
            NUnitAssert.That(panel.RowObjects.Count(r => r.GetComponent<Button>() != null), Is.EqualTo(SessionListPanel.MaxRows));
            NUnitAssert.That(panel.RowObjects.Last().GetComponent<TMP_Text>().text, Is.EqualTo("+2 more"));
        }

        // The test browser's lobby completes synchronously; a pending task here is a bug.
        private static void Done(Task task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True);
            task.GetAwaiter().GetResult();
        }

        private static T Done<T>(Task<T> task)
        {
            NUnitAssert.That(task.IsCompleted, Is.True);
            return task.GetAwaiter().GetResult();
        }

        private TestSessionBrowser CreateBrowser()
        {
            var go = new GameObject("session-browser-test");
            _objects.Add(go);
            return go.AddComponent<TestSessionBrowser>();
        }

        private Button CreateTemplateButton()
        {
            var canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _objects.Add(canvas);
            var buttonObject = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvas.transform, false);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(buttonObject.transform, false);
            label.GetComponent<TMP_Text>().text = "Connect";
            return buttonObject.GetComponent<Button>();
        }

        private sealed class TestSessionBrowser : FusionSessionBrowser
        {
            public readonly List<string> Calls = new();
            public bool CanStart = true;
            public string LobbyError;
            public TaskCompletionSource<string> PendingLobby;

            protected override bool CanStartSession => CanStart;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);

            protected override Task<string> JoinLobbyAsync()
            {
                Calls.Add("join-lobby");
                return PendingLobby != null ? PendingLobby.Task : Task.FromResult(LobbyError);
            }

            protected override Task CloseLobbyAsync()
            {
                if (Calls.LastOrDefault() != "close-lobby") Calls.Add("close-lobby");
                return Task.CompletedTask;
            }
        }
    }
}
#endif
