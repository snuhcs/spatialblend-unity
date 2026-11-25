// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The lobby is Editing, so a scan can be saved and a virtual object moved before a room. Coming back from a room puts Editing
    /// again, once per stay, so a lobby tutorial that sets the mode is not undone by every refresh. A room still starts in Interaction.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RoomModeResetTests
    {
        private GameObject _host;
        private InteractionModeManager _mode;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("mode");
            _mode = _host.AddComponent<InteractionModeManager>();
            // EditMode does not run Awake, and Awake is what publishes InteractionModeManager.Instance.
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_mode, null);
            _mode.SetMode(InteractionMode.SceneEdit);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        [Test]
        public void TheLobbyStaysInEditing()
        {
            _mode.SetMode(InteractionMode.Interaction);
            var reset = new RoomModeReset();
            Assert.That(reset.Apply(_mode), Is.True);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
        }

        [Test]
        public void LeavingARoomComesBackToEditing()
        {
            var reset = new RoomModeReset();
            reset.Apply(_mode);                                   // the lobby, before the room
            reset.EnteredRoom();                                  // the session is ready
            _mode.SetMode(InteractionMode.Interaction);           // a room starts here

            Assert.That(reset.Apply(_mode), Is.True, "the lobby UI came back (leave, a lost host, a failed join)");
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
        }

        [Test]
        public void ARefreshOfTheLobbyDoesNotUndoAModeSetInIt()
        {
            var reset = new RoomModeReset();
            reset.Apply(_mode);
            _mode.SetMode(InteractionMode.Interaction);           // the lobby tutorial's own switch

            Assert.That(reset.Apply(_mode), Is.False, "the lobby UI is refreshed again and again while connecting");
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
        }

        [Test]
        public void AnAppWithoutModesStillConsumesTheReset()
        {
            var reset = new RoomModeReset();
            Assert.That(reset.Apply(null), Is.True);
            Assert.That(reset.Pending, Is.False);
            Assert.That(reset.Apply(_mode), Is.False, "a manager that appears later is not changed by an old reset");
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
        }

        [Test]
        public void TheControlPanelWiresTheResetToTheRetryUiAndTheReadyRoom()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "SpatialBlend/Samples/Scripts/UI/ControlPanel.cs"));
            int ready = source.IndexOf("void EnterReadySessionUi()", StringComparison.Ordinal);
            int retry = source.IndexOf("void ShowRetryUi()", StringComparison.Ordinal);
            Assert.That(ready, Is.GreaterThan(0));
            Assert.That(retry, Is.GreaterThan(0));
            Assert.That(source.Substring(ready, 1800), Does.Contain("_roomMode.EnteredRoom()"));
            string retryBody = source.Substring(retry, 900);
            Assert.That(retryBody, Does.Contain("_roomMode.Apply(InteractionModeManager.Instance)"));
            Assert.That(retryBody.IndexOf("modeSwitchOff.SetActive(false)", StringComparison.Ordinal),
                Is.LessThan(retryBody.IndexOf("_roomMode.Apply", StringComparison.Ordinal)), "the assist is off first, so the reset makes no cue");
            Assert.That(retryBody, Does.Contain("!_tutorialActive"));
        }
    }
}
#endif
