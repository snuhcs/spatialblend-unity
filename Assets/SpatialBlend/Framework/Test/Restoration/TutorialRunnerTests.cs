// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;

namespace SpatialBlend.Tests
{
    /// <summary>One run: the scene snapshot and its restore, the success feedback and the narration gate on auto-advance, the anchors task, the notices, the done mark and the room demo.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialRunnerTests
    {
        private FakeTutorialHost _host;
        private FakeTutorialClock _clock;
        private FakeKeyValueStore _store;
        private TutorialDoneMark _done;
        private TutorialRunner _runner;

        [SetUp]
        public void SetUp()
        {
            _host = new FakeTutorialHost { SceneEdit = false, AnchorsVisible = false, Brightness = 0.25f };
            _clock = new FakeTutorialClock();
            _store = new FakeKeyValueStore();
            _done = new TutorialDoneMark(_store);
            _runner = new TutorialRunner(_host, _clock, _done);
        }

        [TearDown]
        public void TearDown()
        {
            // A run left open would keep the real, static mode-switch assist suspended for the rest of the Editor session.
            _runner.Abort();
            TutorialModeAssistBridge.Register(null);
        }

        private void GoTo(string id)
        {
            while (_runner.Flow.Current.Id != id) Assert.That(_runner.Skip(), Is.True);
        }

        private void Finish(string id)
        {
            foreach (var step in _runner.Flow.Frames[TutorialScript.IndexOf(id)].Steps)
            {
                if (step.RequiresEditing) _host.SceneEdit = true;   // the user has switched to Editing
                _runner.Report(step.Signal);
            }
        }

        /// <summary>Runs the clock until the current narration and the step gap are over.</summary>
        private void LetTimePass(double seconds = 30.0)
        {
            for (double t = 0; t < seconds; t += 0.25) { _clock.Advance(0.25); _runner.Tick(); }
        }

        [Test]
        public void Begin_HidesTheLobby_PlaysTheFirstClipAndShowsItsSubtitle()
        {
            _runner.Begin();
            Assert.That(_host.LobbyHidden, Is.True);
            Assert.That(_host.Played, Is.EqualTo(new[] { "t1_1" }));
            Assert.That(_runner.Narrator.Playing, Is.True);
            Assert.That(_runner.Narrator.Subtitle, Is.EqualTo("Welcome to SpatialBlend."));
            Assert.That(_runner.Flow.CanNext, Is.False, "the first task asks for a press: Next is off");
        }

        [Test]
        public void ExitRestoresTheModeAnchorsAndBrightnessAndTheLobby_AndDoesNotMarkItDone()
        {
            _host.SceneEdit = false; _host.AnchorsVisible = false; _host.Brightness = 0.25f;
            _runner.Begin();
            GoTo("3.1");                       // the scan task needs Editing, but the tutorial no longer switches there silently
            Assert.That(_host.SceneEdit, Is.False);
            _host.SceneEdit = true;            // the user switched with the chip
            _host.AnchorsVisible = true;       // 2.2 turned them on
            _host.Brightness = 0.9f;
            _runner.RequestExit();
            _runner.ConfirmExit();
            Assert.That(_host.SceneEdit, Is.False, "mode back");
            Assert.That(_host.AnchorsVisible, Is.False, "anchors back");
            Assert.That(_host.Brightness, Is.EqualTo(0.25f), "brightness back");
            Assert.That(_host.LobbyHidden, Is.False, "lobby panel back");
            Assert.That(_host.EndedWith, Is.EqualTo(TutorialEnd.Exited));
            Assert.That(_done.IsDone, Is.False, "leaving early is not completing it");
            Assert.That(_host.Log, Does.Contain("leave:3.1"));
        }

        [Test]
        public void ExitRestoresASceneEditSnapshotToo()
        {
            _host.SceneEdit = true; _host.AnchorsVisible = true; _host.Brightness = -0.5f;
            _runner.Begin();
            GoTo("2.1");
            Assert.That(_host.SceneEdit, Is.False, "tasks that need no editing run in Interaction");
            _runner.Abort();
            Assert.That(_host.SceneEdit, Is.True);
            Assert.That(_host.AnchorsVisible, Is.True);
            Assert.That(_host.Brightness, Is.EqualTo(-0.5f));
        }

        [Test]
        public void TheAnchorsTask_ForcesThemOffAtItsStart_WhateverTheUserHad_AndRestoresTheirSettingOnExit()
        {
            _host.AnchorsVisible = true;        // the user had them on
            _runner.Begin();
            Assert.That(_host.AnchorsVisible, Is.True, "the first tasks leave the setting alone");
            GoTo("2.1");
            Assert.That(_host.AnchorsVisible, Is.False, "forced off when the task starts, so turning them on is a change");
            Assert.That(_runner.Flow.ActionDone, Is.False);
            _host.AnchorsVisible = true;        // the user turns them on
            Assert.That(_runner.Report(TutorialSignal.AnchorsOn), Is.True);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(1), "then the shared real-object box");
            Assert.That(_runner.Report(TutorialSignal.SharedBoxPointed), Is.True);
            Assert.That(_runner.Flow.ActionDone, Is.True);
            _runner.Abort();
            Assert.That(_host.AnchorsVisible, Is.True, "the user's own setting is back");
        }

        [Test]
        public void TheAnchorsTask_RestoresAnOffSettingToo_AndSkippingItChangesNothing()
        {
            _host.AnchorsVisible = false;
            _runner.Begin();
            GoTo("2.1");
            _host.AnchorsVisible = true;
            Assert.That(_runner.Skip(), Is.True, "Skip this step");
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("2.2"));
            _runner.Abort();
            Assert.That(_host.AnchorsVisible, Is.False);
            // Coming back to the task forces them off again.
            _host.AnchorsVisible = true;
            var second = new TutorialRunner(_host, _clock, _done);
            second.Begin();
            while (second.Flow.Current.Id != "2.1") second.Skip();
            Assert.That(_host.AnchorsVisible, Is.False);
            second.Skip(); second.Back();
            _host.AnchorsVisible = true;
            second.Skip(); second.Back();
            Assert.That(_host.AnchorsVisible, Is.False, "forced off on every entry");
            second.Abort();
            Assert.That(_host.AnchorsVisible, Is.True);
        }

        [Test]
        public void Finishing_RestoresTheSameState_AndMarksTheTutorialDone()
        {
            _host.AnchorsVisible = false;
            _runner.Begin();
            GoTo("4.2");
            _host.AnchorsVisible = true;
            _runner.ChooseRoom("Room 1", false);
            Assert.That(_runner.Next(), Is.True);
            Assert.That(_host.EndedWith, Is.EqualTo(TutorialEnd.Finished));
            Assert.That(_host.AnchorsVisible, Is.False);
            Assert.That(_done.IsDone, Is.True);
            Assert.That(_store.Values[TutorialDoneMark.Key], Is.EqualTo(1));
        }

        [Test]
        public void TheTutorialSetsSceneEditItself_OnlyWhereATaskNeedsIt()
        {
            _runner.Begin();
            var modes = new System.Collections.Generic.List<(string, bool)>();
            while (_runner.Flow.Running && !_runner.Flow.IsLast)
            {
                modes.Add((_runner.Flow.Current.Id, _host.SceneEdit));
                _runner.Skip();
            }
            Assert.That(modes.Where(m => m.Item2).Select(m => m.Item1), Is.EqualTo(new[] { "1.2" }), "only the boards task: the mode task and the scan task leave the mode to the user");
        }

        [Test]
        public void ATaskInTheSameChapter_AdvancesAsSoonAsItsLineEnds()
        {
            _host.VoiceSeconds = 8.0;
            _runner.Begin();
            _runner.Report(TutorialSignal.ButtonPressed);
            Assert.That(_runner.Flow.ActionDone, Is.True);
            Assert.That(_host.Completed, Is.EqualTo(new[] { "1.1/0" }), "the host plays the success sound for the step");
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.1"), "the sentence is still being said");
            _clock.Advance(0.2); _runner.Tick();
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.1"));
            LetTimePass(8.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.2"), "the next task in the chapter does not wait");
            Assert.That(_host.Played.Last(), Is.EqualTo("t1_2_chess"));
        }

        [Test]
        public void AChapterChange_WaitsTwoSecondsAfterTheLineEnds()
        {
            _host.VoiceSeconds = 6.0;
            _runner.Begin();
            GoTo("1.2");
            Finish("1.2");
            LetTimePass(6.0 + TutorialScript.ChapterGapSeconds - 0.5);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.2"), "the next chapter waits two seconds after the line");
            LetTimePass(1.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("2.1"));
        }

        [Test]
        public void TheNextSubStep_StartsWhenTheLineEnds_AndTheLineIsNeverCutOff()
        {
            _host.VoiceSeconds = 6.0;
            _runner.Begin();
            GoTo("1.2");
            _host.Played.Clear();
            Assert.That(_runner.Report(TutorialSignal.ChessPlaced), Is.True);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(1), "the tick shows at once");
            LetTimePass(5.7);
            Assert.That(_host.Played, Is.Empty, "the line being said is not cut off");
            LetTimePass(0.6);
            Assert.That(_host.Played, Is.EqualTo(new[] { "t1_2_board" }));
        }

        [Test]
        public void NothingAdvancesWithoutTheEvent_NoMatterHowLongItWaits()
        {
            _host.VoiceSeconds = 4.0;
            _runner.Begin();
            LetTimePass(120.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.1"), "no timer advances a task");
            Assert.That(_runner.Flow.CanNext, Is.False);
        }

        [Test]
        public void ADoneTaskWithNothingPlaying_StillShowsItsFeedbackForTheDwell()
        {
            _host.VoiceSeconds = 0.0;
            _runner.Begin();
            _runner.Report(TutorialSignal.ButtonPressed);
            _runner.Tick();
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.2"), "the same chapter moves on as soon as nothing is being said");
        }

        [Test]
        public void EachStepOfATask_PlaysItsOwnLine_AfterTheSentenceBeingSaid()
        {
            _host.VoiceSeconds = 6.0;
            _runner.Begin();
            GoTo("3.1");
            _host.SceneEdit = true;
            _host.Played.Clear();
            Assert.That(_runner.Report(TutorialSignal.BoxCompleted), Is.True);
            Assert.That(_host.Played, Is.Empty, "the line being said is not cut off");
            LetTimePass(8.0);
            Assert.That(_host.Played, Is.EqualTo(new[] { "t3_2" }));
            _runner.Report(TutorialSignal.ReconstructChosen);
            LetTimePass(8.0);
            Assert.That(_host.Played, Is.EqualTo(new[] { "t3_2", "t3_3" }));
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("3.1"), "two steps done of eight");
        }

        [Test]
        public void TheHint_ShowsUntilTheStepIsDone()
        {
            _runner.Begin();
            GoTo("1.2");
            _runner.SetHint("Try looking to your right");
            Assert.That(_runner.Hint, Is.EqualTo("Try looking to your right"));
            _runner.Report(TutorialSignal.ChessPlaced);
            Assert.That(_runner.Hint, Is.EqualTo(_runner.Flow.Current.Steps[1].Note), "placed: the hint goes, the next step's note shows");
            _runner.SetHint("Point at a wall, then let go to hang it.");
            _runner.Report(TutorialSignal.WhiteboardPlaced);
            Assert.That(_runner.Hint, Is.Null);
            GoTo("3.1");
            Assert.That(_runner.Hint, Is.EqualTo(TutorialScript.SwitchToEditingText), "the scan starts with the switch (the user is in Interaction)");
            _host.SceneEdit = true;
            Assert.That(_runner.Hint, Is.Null, "and nothing else once they are in Editing");
        }

        [Test]
        public void AfterTheCopy_TheScanIsShared_ThenSomeoneElsesObjectArrivesToBePlaced()
        {
            _host.VoiceSeconds = 2.0;
            _runner.Begin();
            GoTo("3.1");
            _host.SceneEdit = true;                        // the scan task leaves the mode alone, so Editing is set after it is entered
            _host.Played.Clear();
            var steps = _runner.Flow.Current.Steps;
            for (int i = 0; i < 6; i++) _runner.Report(steps[i].Signal);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(6));
            Assert.That(steps[6].Voice, Is.EqualTo("The object you scanned is now shared with another space."));
            LetTimePass(2.3);                                 // the line already playing ends, then the share line starts
            Assert.That(_host.Played.Last(), Is.EqualTo("t3_7"));
            LetTimePass(2.3);                                 // the share line ends and the step completes by itself
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(7));
            LetTimePass(0.5);
            Assert.That(_host.Played.Last(), Is.EqualTo("t3_7"), "the other object waits");
            LetTimePass(TutorialScript.IncomingShareSeconds);
            Assert.That(_host.Played.Last(), Is.EqualTo("t3_8_partner"));
            Assert.That(steps[7].Voice, Does.Contain("Your partner scanned this object"), "it says why it is placed");
            Assert.That(_runner.Hint, Does.Contain("connects it to the interaction"));
            Assert.That(_runner.Report(TutorialSignal.CopyPlaced), Is.True);
            Assert.That(_runner.Flow.Current.SuccessText, Is.EqualTo("Placed. It's now part of the shared space"));
        }

        [Test]
        public void ScanRetry_TakesTheTaskBackToTheMenuStep_AndClearsTheMessage()
        {
            _runner.Begin();
            GoTo("3.1");
            _host.SceneEdit = true;
            foreach (var s in new[] { TutorialSignal.BoxCompleted, TutorialSignal.ReconstructChosen, TutorialSignal.PhotoCaptured, TutorialSignal.ScanSubmitted })
                _runner.Report(s);
            _runner.ReportScanFailure("It did not work.");
            Assert.That(_runner.ScanProblem, Is.Not.Null);
            Assert.That(_runner.RewindTo(1), Is.True);
            _runner.ClearScanProblem();
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(1));
            Assert.That(_runner.ScanProblem, Is.Null);
            Assert.That(_host.Played.Last(), Is.EqualTo("t3_2"), "the line of the step the user is back on is said again");
        }

        [Test]
        public void LeavingTheAnchorsTaskUndone_PutsTheUsersSettingBackAtOnce_DoneKeepsThemOn()
        {
            _host.AnchorsVisible = true;
            _runner.Begin();
            GoTo("2.1");
            Assert.That(_host.AnchorsVisible, Is.False);
            Assert.That(_runner.Skip(), Is.True);
            Assert.That(_host.AnchorsVisible, Is.True, "skipped: the user's own setting is back at once, not only at the end");
            _runner.Back();
            Assert.That(_host.AnchorsVisible, Is.False, "forced off again on re-entry");
            _runner.Back();                                  // 2.1
            Assert.That(_host.AnchorsVisible, Is.True, "left by Back undone: back as well");
            GoTo("2.1");
            _host.AnchorsVisible = true;
            _runner.Report(TutorialSignal.AnchorsOn);
            _runner.Skip();                                  // the shared-box step skipped: the anchors step was done
            Assert.That(_host.AnchorsVisible, Is.True, "turned on: they stay on for the scan");
            _runner.Abort();
        }

        [Test]
        public void ATapOnARoomDuringFindARoomsDwell_IsNotThePick()
        {
            _host.VoiceSeconds = 0.0;
            _runner.Begin();
            GoTo("4.1");
            _runner.NoteRoomList(true, true);
            _runner.ChooseRoom("Room 2", false);            // tapped before the task moved on
            LetTimePass(5.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("4.2"));
            Assert.That(_runner.DemoChoiceNotice, Is.Null);
            Assert.That(_runner.Flow.ActionDone, Is.False);
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Browsing));
        }

        [Test]
        public void ThePressedButtonsAreChecked_NextIsEnabledWhenDone_AndSkipStaysAvailable()
        {
            _runner.Begin();
            Assert.That(_runner.Flow.CanSkip, Is.True);
            Assert.That(_runner.Flow.CanNext, Is.False);
            _runner.Report(TutorialSignal.ButtonPressed);
            Assert.That(_runner.Flow.CanNext, Is.True);
            Assert.That(_runner.Flow.CanSkip, Is.True);
        }

        [Test]
        public void PressingXWhileAnAdvanceWaits_DoesNotLoseTheAdvance()
        {
            _host.VoiceSeconds = 8.0;
            _runner.Begin();
            _runner.Report(TutorialSignal.ButtonPressed);
            _runner.RequestExit();
            LetTimePass(30.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.1"), "the question has the card");
            _runner.CancelExit();
            _runner.Tick();
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("1.2"), "and the advance is still there when it closes");
        }

        [Test]
        public void FocusLossPausesTheNarration_AndTheSubtitlesWaitForIt()
        {
            _host.VoiceSeconds = 6.0;
            _runner.Begin();
            _clock.Advance(1.0); _runner.Tick();
            var before = _runner.Narrator.Elapsed;
            _runner.SetFocus(false);
            _clock.Advance(20.0); _runner.Tick();
            Assert.That(_runner.Narrator.Elapsed, Is.EqualTo(before).Within(1e-6));
            Assert.That(_host.Log, Does.Contain("pause-voice"));
            _runner.SetFocus(true);
            _clock.Advance(1.0); _runner.Tick();
            Assert.That(_runner.Narrator.Elapsed, Is.EqualTo(before + 1.0).Within(1e-6));
            Assert.That(_host.Log, Does.Contain("resume-voice"));
        }

        [Test]
        public void HandTracking_NoTaskNeedsAController_SoNoNoticeShows()
        {
            // The box, the object menu and every grab have a hand equivalent, so the scan task does not say "controllers only".
            _host.HandTrackingActive = true;
            _runner.Begin();
            foreach (var frame in TutorialScript.Frames)
            {
                GoTo(frame.Id);
                Assert.That(_runner.Notice, Is.Null, frame.Id);
            }
            Assert.That(TutorialScript.Frames.Where(f => f.NeedsController).Select(f => f.Id), Is.Empty);
        }

        [Test]
        public void HandTracking_TheNoticeStillShowsForATaskThatIsMarkedControllerOnly()
        {
            var frame = TutorialScript.Frames[0];
            frame.NeedsController = true;
            try
            {
                _host.HandTrackingActive = true;
                _runner.Begin();
                Assert.That(_runner.Notice, Is.EqualTo(TutorialRunner.ControllerNotice));
                _host.HandTrackingActive = false;
                Assert.That(_runner.Notice, Is.Null);
            }
            finally { frame.NeedsController = false; }
        }

        [Test]
        public void BoardsAndAnchorsTasks_SaySoWhenThereIsNoRoomSetup()
        {
            _host.RoomAnchorsAvailable = false;
            _runner.Begin();
            GoTo("1.2");
            Assert.That(_runner.RoomSetupNotice, Is.EqualTo(TutorialRunner.RoomSetupText), "no room to place the boards in");
            GoTo("2.1");
            Assert.That(_runner.RoomSetupNotice, Is.EqualTo(TutorialRunner.RoomSetupText));
            _host.RoomAnchorsAvailable = true;
            Assert.That(_runner.RoomSetupNotice, Is.Null);
            GoTo("3.1");
            _host.RoomAnchorsAvailable = false;
            Assert.That(_runner.RoomSetupNotice, Is.Null, "only on those tasks");
        }

        [Test]
        public void ScanFailure_ShowsAMessage_AndClearsWhenTheFrameChanges()
        {
            _runner.Begin();
            GoTo("3.1");
            _runner.ReportScanFailure("The server is not reachable.");
            Assert.That(_runner.ScanProblem, Is.EqualTo("The server is not reachable."));
            _runner.ReportScanFailure("  ");
            Assert.That(_runner.ScanProblem, Is.Not.Empty);
            _runner.Skip();
            Assert.That(_runner.ScanProblem, Is.Null);
        }

        [Test]
        public void ModeSwitchAssist_IsSuspendedForTheRun_AndResumedOnlyIfItWasSuspended()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _runner.Begin();
            Assert.That(assist.Suspends, Is.EqualTo(1));
            Assert.That(assist.Resumes + assist.Releases, Is.EqualTo(0));
            _runner.Abort();
            Assert.That(assist.Releases, Is.EqualTo(1), "given back to the app once");
            Assert.That(assist.IsSuspended, Is.False);
            _runner.Abort();
            Assert.That(assist.Releases, Is.EqualTo(1), "once");
        }

        [Test]
        public void WithNoOverride_TheRealModeSwitchAssistIsSuspendedForTheRunAndResumedAfter()
        {
            TutorialModeAssistBridge.Register(null);
            // The assist's suspenders are static; clear what other tests may have left so this one starts from "not suspended".
            var owners = typeof(SpatialBlend.Interactions.ModeSwitchAssist).GetField("s_Suspenders", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
            owners.GetType().GetMethod("Clear").Invoke(owners, null);
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
            _runner.Begin();
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.True);
            _runner.Abort();
            Assert.That(SpatialBlend.Interactions.ModeSwitchAssist.IsSuspended, Is.False);
        }

        [Test]
        public void Rooms_FindARoomThenPickOne_NothingIsCreatedOrJoined()
        {
            _host.VoiceSeconds = 0.0;
            _runner.Begin();
            GoTo("4.1");
            Assert.That(_runner.Flow.ActionDone, Is.False);
            _runner.NoteRoomList(true, true);
            Assert.That(_runner.Flow.ActionDone, Is.True, "pressing Find a room is task 4.1");
            Assert.That(_runner.DemoChoiceNotice, Is.Null);
            LetTimePass(5.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("4.2"));
            Assert.That(_runner.RoomListShown, Is.True, "the list stays open for the pick");
            Assert.That(_runner.Flow.ActionDone, Is.False, "task 4.2 waits for the pick");
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Browsing));
            _runner.ChooseRoom("Room 2", false);
            Assert.That(_runner.Flow.ActionDone, Is.True, "picking a room is task 4.2");
            Assert.That(_runner.DemoChoiceNotice, Is.EqualTo(TutorialRunner.DemoChoiceText));
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Connecting));
            Assert.That(_runner.Room.RoomName, Is.EqualTo("Room 2"));
            Assert.That(_runner.Room.Hosting, Is.False);
            Assert.That(_runner.Room.Headline, Does.StartWith("Joining Room 2"));
            Assert.That(_runner.Room.Detail, Does.Contain("Demo only"));
            _clock.Advance(TutorialSimulatedRoom.ConnectSeconds / 2); _runner.Tick();
            Assert.That(_runner.Room.Progress, Is.EqualTo(0.5).Within(1e-6));
            _clock.Advance(TutorialSimulatedRoom.ConnectSeconds); _runner.Tick();
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Connected));
            Assert.That(_runner.Room.Headline, Is.EqualTo("In Room 2 (demo)"));
            Assert.That(_runner.Room.SimulatedActions, Is.EqualTo(1));
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("4.2"), "the last task waits for Finish");
        }

        [Test]
        public void Rooms_ATapBeforeThePickTaskIsOnlyRemembered()
        {
            _host.VoiceSeconds = 0.0;
            _runner.Begin();
            GoTo("4.1");
            _runner.ChooseRoom("Room 1", false);
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Browsing));
            Assert.That(_runner.Flow.ActionDone, Is.False);
        }

        [Test]
        public void Rooms_NewRoomIsAHostingDemo()
        {
            _host.VoiceSeconds = 0.0;
            _runner.Begin();
            GoTo("4.1");
            _runner.NoteRoomList(true, false);
            LetTimePass(5.0);
            _runner.ChooseRoom(null, true);
            Assert.That(_runner.Room.Hosting, Is.True);
            Assert.That(_runner.Room.Headline, Does.StartWith("Hosting Room "));
        }

        [Test]
        public void Rooms_BackResetsTheDemoAndTheList()
        {
            _host.VoiceSeconds = 0.0;
            _runner.Begin();
            GoTo("4.2");
            _runner.NoteRoomList(true, true);
            _runner.ChooseRoom("Room 1", false);
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Connecting));
            _runner.Back();
            Assert.That(_runner.Room.State, Is.EqualTo(SimulatedRoomState.Browsing));
            Assert.That(_runner.RoomListShown, Is.False);
            Assert.That(_runner.DemoChoiceNotice, Is.Null);
        }

        [Test]
        public void TheRoomDemoHoldsNoNetworkType()
        {
            // The model is a plain class over a clock: nothing in its fields can reach Fusion, Photon or a socket.
            foreach (var field in typeof(TutorialSimulatedRoom).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                Assert.That(field.FieldType.Namespace ?? "", Does.Not.Contain("Fusion").And.Not.Contain("Photon").And.Not.Contain("Sockets"), field.Name);
        }

        [Test]
        public void TheCardIsToldWhenTheExitQuestionOpensAndCloses()
        {
            _runner.Begin();
            int changes = 0;
            _runner.StateChanged += () => changes++;
            _runner.RequestExit();
            Assert.That(changes, Is.GreaterThan(0), "the card has to redraw to show the question");
            int afterRequest = changes;
            _runner.CancelExit();
            Assert.That(changes, Is.GreaterThan(afterRequest));
            Assert.That(_runner.Flow.Running, Is.True);
        }

        [Test]
        public void ARunnerOnlyBeginsOnce_AndAbortBeforeBeginDoesNothing()
        {
            _runner.Abort();
            Assert.That(_host.EndedWith, Is.EqualTo(TutorialEnd.None));
            _runner.Begin();
            _runner.Begin();
            Assert.That(_host.Played.Count, Is.EqualTo(1));
        }

        // ---- the mode task and the scan task's mode requirement

        [Test]
        public void TheModeTask_StartsInInteraction_WhateverTheUserHad_AndLeavesTheModeToTheUser()
        {
            _host.SceneEdit = true;
            _runner.Begin();
            GoTo("2.2");
            Assert.That(_host.SceneEdit, Is.False, "starts in Interaction, so that pressing Edit is a real change");
            _clock.Advance(1.0); _runner.Tick();
            Assert.That(_host.SceneEdit, Is.False, "and nothing in the run sets it again");
            Assert.That(_runner.Flow.Current.Steps.Select(s => s.Signal), Is.EqualTo(new[] { TutorialSignal.ModeEditing, TutorialSignal.ModeInteraction }));
            Assert.That(_runner.Hint, Is.EqualTo(_runner.Flow.Current.Steps[0].Note), "the first step explains how to get the Edit button");
            Assert.That(_host.Played.Last(), Is.EqualTo("t2_2"));
        }

        [Test]
        public void TheModeTask_SaysWhatEditingIsWhenTheUserIsInIt_AndOnlyTheEventsInOrderCount()
        {
            _host.VoiceSeconds = 3.0;
            _runner.Begin();
            GoTo("2.2");
            Assert.That(_runner.Report(TutorialSignal.ModeInteraction), Is.False, "going back before going in is nothing");
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(0));
            _host.SceneEdit = true;                     // the assist switched
            Assert.That(_runner.Report(TutorialSignal.ModeEditing), Is.True);
            Assert.That(_host.Completed.Last(), Is.EqualTo("2.2/0"), "the host plays the chime");
            LetTimePass(5.0);
            Assert.That(_host.Played.Last(), Is.EqualTo("t2_2b"), "the explanation is said once the user is in Editing");
            Assert.That(TutorialScript.Frames.Single(f => f.Id == "2.2").Steps[1].Voice, Does.Contain("move things and capture real objects"));
            Assert.That(_runner.Hint, Does.Contain("move and capture").Or.Contain("move things"));
            _host.SceneEdit = false;
            Assert.That(_runner.Report(TutorialSignal.ModeInteraction), Is.True);
            Assert.That(_runner.Flow.ActionDone, Is.True);
            LetTimePass(10.0);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("3.1"), "moved on by itself");
        }

        [Test]
        public void TheAssist_IsLiveOnlyDuringTheModeTask_AndSuspendedAgainAfterIt()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _runner.Begin();
            Assert.That(assist.IsSuspended, Is.True);
            GoTo("2.1");
            Assert.That(assist.IsSuspended, Is.True, "the tutorial sets modes itself in the tasks before");
            _runner.Skip();                             // 2.2
            Assert.That(assist.IsSuspended, Is.False, "un-suspended for this task only: the real [Edit] and chip paths work");
            _host.SceneEdit = true; _runner.Report(TutorialSignal.ModeEditing);
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(assist.IsSuspended, Is.False, "still live for the way back (chip or auto-return)");
            _host.SceneEdit = false; _runner.Report(TutorialSignal.ModeInteraction);
            _runner.Skip();                             // 3.1: the user is in Interaction there
            _host.SceneEdit = true; _clock.Advance(0.1); _runner.Tick();
            Assert.That(assist.IsSuspended, Is.True, "re-suspended once the user is in Editing for the scan: its idle auto-return must not pull them out");
            _runner.Abort();
            Assert.That(assist.IsSuspended, Is.False, "released at the end, as the user had it");
        }

        [Test]
        public void ExitingMidStepOfTheModeTask_RestoresTheModeAndTheAssist()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _host.SceneEdit = false;                    // the user was in Interaction when they opened the tutorial
            _runner.Begin();
            GoTo("2.2");
            Assert.That(assist.IsSuspended, Is.False);
            _host.SceneEdit = true; _runner.Report(TutorialSignal.ModeEditing);     // mid-task: pressed Edit
            _runner.RequestExit();
            _runner.ConfirmExit();
            Assert.That(_host.SceneEdit, Is.False, "the mode the user had is back");
            Assert.That(assist.IsSuspended, Is.False, "and so is the assist");
            Assert.That(assist.Releases, Is.EqualTo(1), "and handed back to the app");
            Assert.That(_host.EndedWith, Is.EqualTo(TutorialEnd.Exited));
        }

        [Test]
        public void ExitingInTheModeTask_FromInteraction_PutsInteractionBack_AndTheExitQuestionHoldsTheAssist()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _runner.Begin();
            GoTo("2.2");
            _host.SceneEdit = true;
            _runner.RequestExit();
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(assist.IsSuspended, Is.True, "while the question is open nothing may switch the mode under it");
            _runner.CancelExit();
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(assist.IsSuspended, Is.False);
            _runner.Abort();
            Assert.That(_host.SceneEdit, Is.False);
            Assert.That(assist.IsSuspended, Is.False);
        }

        [Test]
        public void TheScanBoxStep_RequiresEditing_NotCountedInInteraction_AndTheCardSaysToSwitchFirst()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _runner.Begin();
            GoTo("3.1");
            Assert.That(_host.SceneEdit, Is.False, "not forced");
            Assert.That(_runner.EditingMissing, Is.True);
            Assert.That(_runner.Hint, Is.EqualTo(TutorialScript.SwitchToEditingText));
            Assert.That(TutorialScript.SwitchToEditingText, Does.StartWith("Switch to Editing first"));
            Assert.That(assist.IsSuspended, Is.False, "the chip and the Edit prompt are there to switch with");
            Assert.That(_host.Played, Does.Contain("t3_0"), "the lead-in line is said");
            Assert.That(_runner.Report(TutorialSignal.BoxCompleted), Is.False, "a box needs Editing");
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(0));

            _host.SceneEdit = true;                      // the user switched
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(_runner.EditingMissing, Is.False);
            Assert.That(_runner.Hint, Is.Null);
            Assert.That(assist.IsSuspended, Is.True, "suspended again: no idle auto-return in the middle of the scan");
            Assert.That(_runner.Report(TutorialSignal.BoxCompleted), Is.True);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(1));
        }

        [Test]
        public void TheScanTask_StartedInEditing_ShowsNoSwitchHint_SaysItsOwnFirstLine_AndLeavesTheAssistSuspended()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _host.VoiceSeconds = 5.0;
            _runner.Begin();
            GoTo("2.2");
            _host.SceneEdit = true; _runner.Report(TutorialSignal.ModeEditing);   // pressed Edit, then moved on with Skip while in Editing
            _host.Played.Clear();
            _runner.Skip();
            Assert.That(_host.SceneEdit, Is.True, "the scan task does not take the user out of Editing");
            Assert.That(_runner.Hint, Is.Null);
            Assert.That(assist.IsSuspended, Is.True);
            Assert.That(_host.Played, Is.EqualTo(new[] { "t3_1" }), "no lead-in: they are where the task needs them");
        }

        // The scan never goes on outside Editing. Leaving Editing in the middle of it, or trying a step in
        // Interaction, shows "Switch to Editing first" on the card and says it again (after the line being said); the step waits.
        [Test]
        public void TheScan_LeftOrTriedInInteraction_SaysAndShowsSwitchToEditing_AndDoesNotProceed()
        {
            _host.VoiceSeconds = 2.0;
            _runner.Begin();
            GoTo("3.1");
            _host.SceneEdit = true;
            _clock.Advance(0.1); _runner.Tick();
            LetTimePass(6.0);
            Assert.That(_runner.Report(TutorialSignal.BoxCompleted), Is.True);
            LetTimePass(6.0);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(1), "on Capture");
            _host.Played.Clear();

            _host.SceneEdit = false;                     // left Editing in the middle of the scan
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(_runner.EditingMissing, Is.True);
            Assert.That(_runner.Hint, Is.EqualTo(TutorialScript.SwitchToEditingText), "the card says it");
            LetTimePass(1.0);
            Assert.That(_host.Played, Does.Contain("t3_0"), "and it is said");
            Assert.That(_runner.Report(TutorialSignal.ReconstructChosen), Is.False, "the step waits for Editing");
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(1));

            LetTimePass(6.0);
            _host.Played.Clear();                        // tried again later, still in Interaction
            Assert.That(_runner.Report(TutorialSignal.ReconstructChosen), Is.False);
            LetTimePass(1.0);
            Assert.That(_host.Played.Count(p => p == "t3_0"), Is.EqualTo(1), "said again on the attempt");

            _host.SceneEdit = true;
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(_runner.Hint, Is.Not.EqualTo(TutorialScript.SwitchToEditingText));
            Assert.That(_runner.Report(TutorialSignal.ReconstructChosen), Is.True);
        }

        // The photo, the mask and the submit are one-shot results of work already under way; one that arrives in
        // Interaction is held and counts once the user is back in Editing, instead of being lost.
        [Test]
        public void AOneShotScanResultInInteraction_CountsOnceEditingResumes()
        {
            _host.VoiceSeconds = 1.0;
            _runner.Begin();
            GoTo("3.1");
            _host.SceneEdit = true;
            _clock.Advance(0.1); _runner.Tick();
            LetTimePass(4.0);
            Assert.That(_runner.Report(TutorialSignal.BoxCompleted), Is.True);
            LetTimePass(4.0);
            Assert.That(_runner.Report(TutorialSignal.ReconstructChosen), Is.True);
            LetTimePass(4.0);
            Assert.That(_runner.Flow.Current.Steps[_runner.Flow.StepIndex].Signal, Is.EqualTo(TutorialSignal.PhotoCaptured));
            int step = _runner.Flow.StepIndex;

            _host.SceneEdit = false;
            _clock.Advance(0.1); _runner.Tick();
            Assert.That(_runner.Report(TutorialSignal.PhotoCaptured), Is.False, "not in Interaction");
            LetTimePass(2.0);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(step), "held, not counted");

            _host.SceneEdit = true;
            LetTimePass(4.0);
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(step + 1), "counted once Editing resumed");
        }

        [Test]
        public void TheLeadIn_ComesFirst_ThenTheBoxLine_AndIsNotRepeatedByTheSwitch()
        {
            _host.VoiceSeconds = 4.0;
            _runner.Begin();
            GoTo("3.1");
            Assert.That(_host.Played.Skip(_host.Played.Count - 1), Is.EqualTo(new[] { "t3_0" }));
            Assert.That(_runner.Narrator.Subtitle, Is.EqualTo("Switch to Editing first."));
            _host.SceneEdit = true;                      // switched during the lead-in: it is not cut off
            _clock.Advance(0.5); _runner.Tick();
            Assert.That(_host.Played.Last(), Is.EqualTo("t3_0"));
            LetTimePass(6.0);
            Assert.That(_host.Played.Last(), Is.EqualTo("t3_1"), "then the task's own first line");
            Assert.That(_host.Played.Count(p => p == "t3_0"), Is.EqualTo(1));
        }

        [Test]
        public void BackFromTheScanIntoTheModeTask_StartsItAgainInInteraction_WithTheAssistLive()
        {
            var assist = new FakeModeAssist();
            TutorialModeAssistBridge.Register(assist);
            _runner.Begin();
            GoTo("3.1");
            _host.SceneEdit = true; _clock.Advance(0.1); _runner.Tick();
            Assert.That(assist.IsSuspended, Is.True, "in Editing for the scan");
            Assert.That(_runner.Back(), Is.True);
            Assert.That(_runner.Flow.Current.Id, Is.EqualTo("2.2"));
            Assert.That(_host.SceneEdit, Is.False, "the mode task starts in Interaction again, so pressing Edit is a real change");
            Assert.That(assist.IsSuspended, Is.False, "and the assist is live for it");
            Assert.That(_runner.Flow.StepIndex, Is.EqualTo(0));
            Assert.That(_host.Played.Last(), Is.EqualTo("t2_2"));
            _runner.Abort();
            Assert.That(assist.IsSuspended, Is.False);
        }
    }
}
#endif
