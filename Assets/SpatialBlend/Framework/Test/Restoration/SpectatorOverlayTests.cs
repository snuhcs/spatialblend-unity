// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The desktop spectator's window (<see cref="SpectatorOverlay"/>) in a screen-space overlay canvas, driven by a session with fakes: the row
    /// that shows for each phase, the room field's refill, the Join and Leave wiring, a status that wraps, and the camera keys that must
    /// not fly the camera while a field has the keyboard.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SpectatorOverlayTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private FreeformSpectatorSessionTests.TestSession _session;
        private SpectatorOverlay _overlay;
        private EventSystem[] _eventSystemsBefore;

        [SetUp]
        public void SetUp()
        {
            _eventSystemsBefore = Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None);
            var go = new GameObject("spectator-session");
            _objects.Add(go);
            var connection = go.AddComponent<FreeformSpectatorSessionTests.TestConnection>();
            _session = go.AddComponent<FreeformSpectatorSessionTests.TestSession>();
            _session.Configure(null, connection, null, null);
            _overlay = SpectatorOverlay.Create(_session, UiFonts.Set);
            _objects.Add(_overlay.gameObject);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var system in Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
                if (!_eventSystemsBefore.Contains(system)) Object.DestroyImmediate(system.gameObject);
        }

        private void SetPhase(SpectatorPhase phase)
        {
            typeof(FreeformSpectatorSession).GetProperty("Phase", Any).GetSetMethod(true).Invoke(_session, new object[] { phase });
            _overlay.Refresh();
        }

        private void SetRoom(string room) => typeof(FreeformSpectatorSession).GetField("_room", Any).SetValue(_session, room);

        private void Layout()
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_overlay.Card);
        }

        private RectTransform StatusRow => (RectTransform)_overlay.StatusText.transform.parent;

        [Test]
        public void TheOverlayIsAScreenSpaceCanvasOfTheSpectatorWindow()
        {
            Assert.That(_overlay.Canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(_overlay.GetComponent<CanvasScaler>().referenceResolution, Is.EqualTo(SpectatorOverlay.ReferenceResolution));
            Assert.That(_overlay.JoinButton.GetComponentInChildren<TMP_Text>(true).text, Is.EqualTo(SpectatorOverlay.JoinLabel));
            Assert.That(_overlay.LeaveButton.GetComponentInChildren<TMP_Text>(true).text, Is.EqualTo(SpectatorOverlay.LeaveLabel));
            Assert.That(_overlay.RoomField.placeholder.GetComponent<TMP_Text>().text, Is.EqualTo(SpectatorOverlay.RoomPlaceholder));
        }

        [TestCase(SpectatorPhase.Idle, true)]
        [TestCase(SpectatorPhase.Failed, true)]
        [TestCase(SpectatorPhase.Searching, false)]
        [TestCase(SpectatorPhase.Joining, false)]
        [TestCase(SpectatorPhase.Spectating, false)]
        public void TheJoinRowShowsWhileIdleOrFailedAndTheStatusRowOtherwise(SpectatorPhase phase, bool joinRow)
        {
            SetPhase(phase);
            Assert.That(_overlay.JoinRowVisible, Is.EqualTo(joinRow), phase.ToString());
            Assert.That(_overlay.StatusRowVisible, Is.EqualTo(!joinRow), phase.ToString());
        }

        [Test]
        public void TheStatusRowShowsTheSessionsStatusLine()
        {
            SetRoom("R");
            SetPhase(SpectatorPhase.Spectating);
            Assert.That(_overlay.StatusText.text, Is.EqualTo(_session.StatusLine()));
            Assert.That(_overlay.StatusText.text, Does.Contain("room 'R'").And.Contain("Spectating"));
        }

        [Test]
        public void TheRoomFieldIsRefilledWithTheLastRoomWhenTheWindowReturnsToJoining()
        {
            typeof(FreeformSpectatorSession).GetField("_typedRoom", Any).SetValue(_session, "Room 3");
            SetPhase(SpectatorPhase.Spectating);
            Assert.That(_overlay.RoomField.text, Is.Empty);

            SetPhase(SpectatorPhase.Idle);

            Assert.That(_overlay.RoomField.text, Is.EqualTo("Room 3"));
        }

        [Test]
        public void ARoomTheUserIsTypingIsNotOverwrittenByTheRefill()
        {
            typeof(FreeformSpectatorSession).GetField("_typedRoom", Any).SetValue(_session, "Room 3");
            SetPhase(SpectatorPhase.Failed);
            _overlay.RoomField.text = "Room 9";
            SetPhase(SpectatorPhase.Idle);
            Assert.That(_overlay.RoomField.text, Is.EqualTo("Room 9"));
        }

        [Test]
        public void JoinPassesTheTypedRoomToTheSessionFromTheButtonAndFromEnter()
        {
            _overlay.RoomField.text = "R";
            _overlay.JoinButton.onClick.Invoke();
            Assert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Searching));
            Assert.That(_session.Room, Is.EqualTo("R"));

            _session.Leave();
            Assert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle));
            _overlay.RoomField.text = "S";
            _overlay.RoomField.onSubmit.Invoke("S");
            Assert.That(_session.Room, Is.EqualTo("S"), "Enter in the field joins as well");
        }

        [Test]
        public void JoinWithAnEmptyFieldFailsWithTheEmptyNameError()
        {
            _overlay.RoomField.text = string.Empty;
            _overlay.JoinButton.onClick.Invoke();
            Assert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Failed));
            _overlay.Refresh();
            Assert.That(_overlay.ErrorText.text, Is.EqualTo(SpectatorJoinPolicy.EmptyNameError));
            Assert.That(_overlay.ErrorText.gameObject.activeSelf, Is.True);
            Assert.That(_overlay.JoinRowVisible, Is.True, "a failed join returns to the join row");
        }

        [Test]
        public void LeaveActsThroughTheSession()
        {
            _overlay.RoomField.text = "R";
            _overlay.JoinButton.onClick.Invoke();
            Assert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Searching));

            _overlay.LeaveButton.onClick.Invoke();

            Assert.That(_session.Phase, Is.EqualTo(SpectatorPhase.Idle));
            Assert.That(_session.Calls, Is.Empty, "a join given up while searching has no runner to shut down");
            _overlay.Refresh();
            Assert.That(_overlay.JoinRowVisible, Is.True);
        }

        [Test]
        public void NoticeErrorAndCarrierShowOnlyWhenTheyHaveText()
        {
            SetPhase(SpectatorPhase.Searching);
            Assert.That(_overlay.NoticeText.gameObject.activeSelf, Is.False);
            Assert.That(_overlay.ErrorText.gameObject.activeSelf, Is.False);
            Assert.That(_overlay.CarrierText.gameObject.activeSelf, Is.False);

            typeof(FreeformSpectatorSession).GetProperty("Notice", Any).GetSetMethod(true).Invoke(_session, new object[] { "Waiting for the room's participants to start." });
            _overlay.Refresh();
            Assert.That(_overlay.NoticeText.gameObject.activeSelf, Is.True);
            Assert.That(_overlay.NoticeText.text, Is.EqualTo("Waiting for the room's participants to start."));

            typeof(FreeformSpectatorSession).GetProperty("Notice", Any).GetSetMethod(true).Invoke(_session, new object[] { null });
            _overlay.Refresh();
            Assert.That(_overlay.NoticeText.gameObject.activeSelf, Is.False, "gone once the wait is over");
        }

        // ---- a long status ----

        [Test]
        public void AShortStatusKeepsTheRowOneControlHigh()
        {
            SetRoom("R");
            SetPhase(SpectatorPhase.Spectating);
            Layout();
            Assert.That(StatusRow.rect.height, Is.EqualTo(ControlPanelKit.RowHeight).Within(0.5f));
        }

        [Test]
        public void ALongStatusGrowsTheRowSoTheTextStaysInsideItAndNothingRunsOverTheRowsBelow()
        {
            SetRoom(string.Concat(Enumerable.Repeat("a long room name for a design review ", 6)));
            SetPhase(SpectatorPhase.Spectating);
            Layout();

            float textHeight = _overlay.StatusText.preferredHeight;
            Assert.That(textHeight, Is.GreaterThan(ControlPanelKit.RowHeight), "the status really wraps to more than one control's height");
            Assert.That(StatusRow.rect.height, Is.GreaterThanOrEqualTo(textHeight - 0.5f), "the row takes the height of its text");
            Assert.That(_overlay.StatusText.rectTransform.rect.height, Is.GreaterThanOrEqualTo(textHeight - 0.5f));

            // The keys help is the last line of the card; it sits below the status row, not over it.
            var lines = _overlay.Card.GetComponentsInChildren<TMP_Text>(true).Where(t => t.gameObject.activeInHierarchy).ToArray();
            var help = lines.First(t => t.text == SpectatorOverlay.KeysHelp);
            Assert.That(Top(help.rectTransform), Is.LessThanOrEqualTo(Bottom(StatusRow) + 0.5f));
        }

        private static float Top(RectTransform rect) { var c = new Vector3[4]; rect.GetWorldCorners(c); return c[1].y; }
        private static float Bottom(RectTransform rect) { var c = new Vector3[4]; rect.GetWorldCorners(c); return c[0].y; }

        // ---- the camera keys while typing ----

        [Test]
        public void WasdQEAreTextNotFlightWhileTheRoomFieldHasTheKeyboard()
        {
            var events = new GameObject("events", typeof(EventSystem)).GetComponent<EventSystem>();
            _objects.Add(events.gameObject);
            var view = new GameObject("view").transform;
            _objects.Add(view.gameObject);
            System.Func<KeyCode, bool> wasd = k => k == KeyCode.W || k == KeyCode.A || k == KeyCode.Q;

            Assert.That(SpectatorCamera.KeyMove(wasd, view), Is.Not.EqualTo(Vector3.zero), "held keys fly the camera");
            Assert.That(SpectatorCamera.TypingInTextField(events), Is.False, "nothing selected");

            events.SetSelectedGameObject(_overlay.RoomField.gameObject);
            Assert.That(SpectatorCamera.TypingInTextField(events), Is.False, "selected but not focused: the field does not have the keyboard");

            typeof(TMP_InputField).GetField("m_AllowInput", Any).SetValue(_overlay.RoomField, true);   // what ActivateInputField sets
            Assert.That(_overlay.RoomField.isFocused, Is.True);
            Assert.That(SpectatorCamera.TypingInTextField(events), Is.True);

            events.SetSelectedGameObject(_overlay.JoinButton.gameObject);
            Assert.That(SpectatorCamera.TypingInTextField(events), Is.False, "a button does not take the keyboard");
        }

        [Test]
        public void TheCameraSeesNoKeyWhileTypingAndEveryKeyOtherwise()
        {
            var view = new GameObject("view").transform;
            _objects.Add(view.gameObject);
            System.Func<KeyCode, bool> everything = _ => true;

            Assert.That(SpectatorCamera.KeyMove(SpectatorCamera.HeldKeys(true, everything), view), Is.EqualTo(Vector3.zero), "typing: WASD/Q/E are text");
            Assert.That(SpectatorCamera.HeldKeys(true, everything)(KeyCode.LeftShift), Is.False);
            Assert.That(SpectatorCamera.KeyPressed(true, everything, KeyCode.F), Is.False, "typing an F does not frame the content");
            Assert.That(SpectatorCamera.KeyMove(SpectatorCamera.HeldKeys(false, k => k == KeyCode.W), view), Is.EqualTo(view.forward));
            Assert.That(SpectatorCamera.KeyPressed(false, everything, KeyCode.F), Is.True);
        }

        [Test]
        public void KeyMoveFollowsTheViewAndIsZeroWithNoKeys()
        {
            var view = new GameObject("view").transform;
            _objects.Add(view.gameObject);
            Assert.That(SpectatorCamera.KeyMove(_ => false, view), Is.EqualTo(Vector3.zero));
            Assert.That(SpectatorCamera.KeyMove(k => k == KeyCode.W, view), Is.EqualTo(view.forward));
            Assert.That(SpectatorCamera.KeyMove(k => k == KeyCode.D, view), Is.EqualTo(view.right));
            Assert.That(SpectatorCamera.KeyMove(k => k == KeyCode.E, view), Is.EqualTo(Vector3.up));
            Assert.That(SpectatorCamera.KeyMove(k => k == KeyCode.Q, view), Is.EqualTo(Vector3.down));
            Assert.That(SpectatorCamera.KeyMove(k => k == KeyCode.W || k == KeyCode.S, view), Is.EqualTo(Vector3.zero), "opposite keys cancel");
        }
    }
}
#endif
