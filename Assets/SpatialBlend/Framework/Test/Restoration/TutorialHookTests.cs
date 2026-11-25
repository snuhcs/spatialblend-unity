// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Scanning;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>The small public hooks the tutorial reads: box completion, the scan panel's capture and tap counts, the room list in read-only mode, the mode panel.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialHookTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private T Make<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go.AddComponent<T>();
        }

        // ---- box completed

        [Test]
        public void BoxAnnotationManager_RaisesBoxCompleted_ForADrawnBoxAndForACancelledOne()
        {
            var manager = Make<BoxAnnotationManager>("box-manager");
            var results = new List<bool>();
            manager.BoxCompleted += results.Add;
            manager.HandleAnnotationComplete(null, true);
            manager.HandleAnnotationComplete(null, false);
            Assert.That(results, Is.EqualTo(new[] { true, false }), "success tells a box exists; false is a cancel (B undoing the first point)");
        }

        [Test]
        public void BoxAnnotationManager_WithNoListener_CompletesQuietly()
        {
            var manager = Make<BoxAnnotationManager>("box-manager");
            Assert.DoesNotThrow(() => manager.HandleAnnotationComplete(null, true));
        }

        // ---- capture and tap count

        [Test]
        public void ScanPanelProgress_CountsCapturesAndTaps()
        {
            var progress = new ScanPanelProgress();
            var captures = new List<int>();
            var taps = new List<int>();
            int changes = 0;
            progress.Captured += captures.Add;
            progress.Tapped += taps.Add;
            progress.Changed += () => changes++;

            progress.NoteTap(1);                       // a tap before any capture still counts as a tap
            Assert.That(progress.TapCount, Is.EqualTo(1));
            progress.NoteCapture();
            Assert.That(progress.CaptureCount, Is.EqualTo(1));
            Assert.That(progress.TapCount, Is.EqualTo(0), "a new photo starts with no points");
            progress.NoteTap(1);
            progress.NoteTap(2);
            Assert.That(progress.TapCount, Is.EqualTo(2));
            Assert.That(progress.TotalTaps, Is.EqualTo(3));
            progress.SetTapCount(1);                   // undo
            Assert.That(progress.TapCount, Is.EqualTo(1));
            progress.SetTapCount(1);                   // no change, no event
            Assert.That(captures, Is.EqualTo(new[] { 1 }));
            Assert.That(taps, Is.EqualTo(new[] { 1, 1, 2 }));
            Assert.That(changes, Is.EqualTo(5), "tap, capture, tap, tap, undo");
            progress.Reset();
            Assert.That(progress.CaptureCount + progress.TapCount + progress.TotalTaps, Is.EqualTo(0));
        }

        [Test]
        public void ScanPanel_WithAStill_OpensWithoutACamera_AndCaptureCountsAndSubmitFailureIsReported()
        {
            var panel = Make<PassthroughPanelManager>("scan-panel");
            var still = new Texture2D(8, 6, TextureFormat.RGB24, false);
            _objects.Add(still);
            int opening = 0;
            Action<PassthroughPanelManager> handler = p => { opening++; p.StillOverride = still; };
            PassthroughPanelManager.PanelOpening += handler;
            ScanError? failed = null;
            panel.RequestFailed += e => failed = e;
            try
            {
                panel.OpenPanel();
            }
            finally
            {
                PassthroughPanelManager.PanelOpening -= handler;
            }
            Assert.That(opening, Is.EqualTo(1), "the static hook fires at the start of OpenPanel, so a listener can configure the panel");
            Assert.That(panel.StillOverride, Is.SameAs(still));
            Assert.That(panel.Progress.CaptureCount, Is.EqualTo(0));

            panel.CaptureImage();
            Assert.That(panel.Progress.CaptureCount, Is.EqualTo(1), "the still is captured as a camera frame is");
            Assert.That(panel.Progress.TapCount, Is.EqualTo(0));

            LogAssert.Expect(LogType.Error, new Regex("scan service is not connected"));
            panel.SendToServer();
            Assert.That(failed.HasValue, Is.True, "a request that cannot be sent is reported, not only logged");
            Assert.That(failed.Value.Code, Is.EqualTo(ScanErrorCode.NotInitialized));
        }

        [Test]
        public void ScanPanel_OpeningResetsTheProgress()
        {
            var panel = Make<PassthroughPanelManager>("scan-panel");
            panel.Progress.NoteCapture();
            panel.Progress.NoteTap(3);
            panel.OpenPanel();
            Assert.That(panel.Progress.CaptureCount, Is.EqualTo(0));
            Assert.That(panel.Progress.TapCount, Is.EqualTo(0));
        }

        // ---- the mode panel before connecting

        [Test]
        public void ControlPanel_CanShowTheModePanelBeforeConnecting()
        {
            var panelObject = new GameObject("control");
            panelObject.SetActive(false);
            _objects.Add(panelObject);
            var panel = panelObject.AddComponent<ControlPanel>();
            var modeCanvas = new GameObject("mode-canvas");
            _objects.Add(modeCanvas);
            modeCanvas.SetActive(false);
            typeof(ControlPanel).GetField("_InteractionModeCanvas", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, modeCanvas);
            Assert.That(panel.ModePanelVisible, Is.False);
            panel.SetModePanelVisible(true);
            Assert.That(modeCanvas.activeSelf, Is.True);
            Assert.That(panel.ModePanelVisible, Is.True);
            panel.SetModePanelVisible(false);
            Assert.That(modeCanvas.activeSelf, Is.False);
        }

        [Test]
        public void ControlPanel_ModePanelHookIsSafeWithoutAModeCanvas()
        {
            var panel = Make<ControlPanel>("control");
            Assert.DoesNotThrow(() => panel.SetModePanelVisible(true));
            Assert.That(panel.ModePanelVisible, Is.False);
        }

        // ---- the room list read-only

        private sealed class TestBrowser : FusionSessionBrowser
        {
            public readonly List<string> Calls = new List<string>();
            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) => Calls.Add("start:" + sessionName);
            protected override Task<string> JoinLobbyAsync() { Calls.Add("join-lobby"); return Task.FromResult<string>(null); }
            protected override Task CloseLobbyAsync() { Calls.Add("close-lobby"); return Task.CompletedTask; }
        }

        private (SessionListPanel panel, TestBrowser browser) NewList()
        {
            var canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            _objects.Add(canvas);
            var buttonObject = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvas.transform, false);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(buttonObject.transform, false);
            var browserObject = new GameObject("browser");
            _objects.Add(browserObject);
            var browser = browserObject.AddComponent<TestBrowser>();
            var panel = SessionListPanel.Create(buttonObject.GetComponent<Button>(), browser);
            _objects.Add(panel.gameObject);
            return (panel, browser);
        }

        [Test]
        public void ReadOnlyRoomList_ListsRoomsButJoinsAndCreatesNothing()
        {
            var (panel, browser) = NewList();
            var tapped = new List<string>();
            int newRooms = 0;
            panel.SetReadOnly(tapped.Add, () => newRooms++);
            Assert.That(panel.ReadOnly, Is.True);
            panel.Show();
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 2, 9), new FusionSessionEntry("Room 2", 1, 9) });
            Assert.That(panel.RowObjects.Count, Is.EqualTo(2), "the real list is read");

            panel.RowObjects[0].GetComponent<Button>().onClick.Invoke();
            Assert.That(tapped, Is.EqualTo(new[] { "Room 1" }));
            var newRoom = panel.GetComponentsInChildren<Button>(true).First(b => b.GetComponentInChildren<TMP_Text>().text == "New room");
            newRoom.onClick.Invoke();
            Assert.That(newRooms, Is.EqualTo(1));
            Assert.That(browser.Calls.Any(c => c.StartsWith("start:")), Is.False, "no room was started, joined or hosted");
            Assert.That(panel.gameObject.activeSelf, Is.True, "the handler decides when the list closes");
        }

        [Test]
        public void RoomList_WithNoHandlers_StillJoinsAndCreatesAsBefore()
        {
            var (panel, browser) = NewList();
            panel.Show();
            browser.ApplySessions(new[] { new FusionSessionEntry("Room 1", 2, 9) });
            Assert.That(panel.ReadOnly, Is.False);
            panel.RowObjects[0].GetComponent<Button>().onClick.Invoke();
            Assert.That(browser.Calls.Last(), Is.EqualTo("start:Room 1"));
        }

        [Test]
        public void RoomList_ReadOnlyIsLiftedByClearingTheHandlers()
        {
            var (panel, browser) = NewList();
            panel.SetReadOnly(_ => { }, () => { });
            panel.SetReadOnly(null, null);
            Assert.That(panel.ReadOnly, Is.False);
            panel.Show();
            browser.ApplySessions(new FusionSessionEntry[0]);
            var newRoom = panel.GetComponentsInChildren<Button>(true).First(b => b.GetComponentInChildren<TMP_Text>().text == "New room");
            newRoom.onClick.Invoke();
            Assert.That(browser.Calls.Last(), Is.EqualTo("start:Room 1"));
        }
    }
}
#endif
