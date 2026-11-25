// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;

namespace SpatialBlend.Tests
{
    /// <summary>The task state machine: every task waits for its real events, advances on them, and can be skipped; the X confirmation.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialFlowTests
    {
        private static TutorialFlow NewFlow() => new TutorialFlow(TutorialScript.Frames);

        private static void Complete(TutorialFlow flow)
        {
            foreach (var step in flow.Current.Steps.ToArray()) flow.Report(step.Signal);
        }

        private static void GoTo(TutorialFlow flow, string id)
        {
            while (flow.Current.Id != id) Assert.That(flow.Skip(), Is.True);
        }

        [Test]
        public void Script_IsTheTaskDrivenTour_SevenTasksSeventeenVerifiedSteps_NoInfoOnlyFrame()
        {
            var frames = TutorialScript.Frames;
            Assert.That(frames.Select(f => f.Id), Is.EqualTo(new[] { "1.1", "1.2", "2.1", "2.2", "3.1", "4.1", "4.2" }));
            Assert.That(frames.GroupBy(f => f.Chapter).Select(g => g.Count()), Is.EqualTo(new[] { 2, 2, 1, 2 }));
            Assert.That(TutorialScript.ChapterNames, Is.EqualTo(new[] { "Interact", "Virtual and real objects", "Scan a real object", "Join a room" }));
            Assert.That(frames.Select(f => f.Steps.Length), Is.EqualTo(new[] { 1, 2, 2, 2, 8, 1, 1 }));
            Assert.That(TutorialScript.AllSteps().Count(), Is.EqualTo(17));
            Assert.That(frames.Any(f => f.InfoOnly), Is.False, "every task asks for something");
            foreach (var frame in frames)
            {
                Assert.That(frame.TaskLine, Is.Not.Empty, frame.Id);
                Assert.That(frame.SuccessText, Is.Not.Empty, frame.Id + ": every task has success feedback");
                foreach (var step in frame.Steps)
                {
                    Assert.That(step.Label, Is.Not.Empty, frame.Id);
                    Assert.That(step.Voice, Is.Not.Empty, frame.Id);
                    Assert.That(step.ClipName, Does.StartWith("t"), frame.Id);
                }
            }
            Assert.That(TutorialScript.AllSteps().Select(s => s.ClipName).Distinct().Count(), Is.EqualTo(17), "one clip per step");
        }

        [Test]
        public void TheScanIsOneTask_TapThenCopyThenASharedObject()
        {
            var scan = TutorialScript.Frames.Single(f => f.Id == "3.1");
            Assert.That(scan.Steps.Select(s => s.Signal), Is.EqualTo(new[]
            {
                TutorialSignal.BoxCompleted, TutorialSignal.ReconstructChosen, TutorialSignal.PhotoCaptured,
                TutorialSignal.MaskReady, TutorialSignal.ScanSubmitted, TutorialSignal.ScanCopyReady,
                TutorialSignal.LineHeard, TutorialSignal.CopyPlaced,
            }));
            // Capture on the box menu is the menu step. The photo step does not tell the user to press Confirm.
            Assert.That(scan.Steps[1].Label, Is.EqualTo("Open the object menu and press Capture"));
            Assert.That(scan.Steps[1].Voice, Is.EqualTo("Good. Point at your box and press A, or pinch Menu. Press Capture."));
            Assert.That(scan.Steps[2].Label, Is.EqualTo("Capture the object in your box"));
            Assert.That(scan.Steps[2].Voice, Is.EqualTo("Look at the object in your box, then press Capture."));
            // Any small real object on the floor near the user, never a vase; the photo step is points and Confirm,
            // and nothing says the model builds by itself.
            string scanText = string.Join("\n", scan.Steps.Select(s => s.Label + "\n" + s.Voice + "\n" + s.Note));
            Assert.That(scanText, Does.Not.Contain("vase"));
            Assert.That(scan.Steps[0].Voice, Does.Contain("small object on the floor near you"));
            Assert.That(scan.Steps[3].Voice, Does.Contain("add points").And.Contain("Confirm"), "points, then Confirm");
            Assert.That(scan.Steps[3].Signal, Is.EqualTo(TutorialSignal.MaskReady));
            Assert.That(scan.Steps[4].Voice, Does.Contain("Press 3D copy"));
            Assert.That(SpatialBlend.Samples.Scanning.ScanCardText.SelectTitle + SpatialBlend.Samples.Scanning.ScanCardText.SelectDetail, Does.Not.Contain("vase"));
            Assert.That(TutorialScript.PendingNarration, Is.Empty, "every scan line has its clip");
            // The box menu and the photo need no Confirm; only the mask step does.
            Assert.That(string.Join("\n", scan.Steps.Take(3).Select(s => s.Label + "\n" + s.Voice)), Does.Not.Contain("Confirm"));
            Assert.That(scan.Steps.Single(s => s.Signal == TutorialSignal.LineHeard).CompletesWhenHeard, Is.True);
            Assert.That(TutorialScript.Frames.Count(f => f.Chapter == 3), Is.EqualTo(1), "no separate frames between box and placement");
        }

        [Test]
        public void EveryTask_DoesNotAdvanceWithoutItsEvents_AdvancesOnThem_AndCanBeSkipped()
        {
            foreach (var frame in TutorialScript.Frames)
            {
                // Without the event: Next stays off, nothing auto-advances, other tasks' signals do nothing.
                var flow = NewFlow();
                GoTo(flow, frame.Id);
                Assert.That(flow.CanNext, Is.False, frame.Id + ": Next is off until the task is done");
                Assert.That(flow.ActionDone, Is.False, frame.Id);
                foreach (var signal in System.Enum.GetValues(typeof(TutorialSignal)).Cast<TutorialSignal>().Where(s => s != frame.Steps[0].Signal))
                    flow.Report(signal);
                Assert.That(flow.ActionDone, Is.False, frame.Id + ": only the step's own signal counts");
                Assert.That(flow.AdvanceIfReady(true), Is.False, frame.Id + ": no timer or sentence end advances an undone task");
                Assert.That(flow.Current.Id, Is.EqualTo(frame.Id));

                // With the events, in order.
                var completed = new List<int>();
                flow.StepCompleted += (f, i) => completed.Add(i);
                for (int i = 0; i < frame.Steps.Length; i++)
                {
                    Assert.That(flow.ActionDone, Is.False, frame.Id + " step " + i);
                    Assert.That(flow.StepIndex, Is.EqualTo(i));
                    Assert.That(flow.Report(frame.Steps[i].Signal), Is.True, frame.Id + " step " + i);
                }
                Assert.That(completed, Is.EqualTo(Enumerable.Range(0, frame.Steps.Length)), frame.Id);
                Assert.That(flow.ActionDone, Is.True, frame.Id);
                Assert.That(flow.CanNext, Is.True, frame.Id);
                Assert.That(flow.AutoAdvancePending, Is.EqualTo(frame.AutoAdvance), frame.Id);
                Assert.That(flow.Report(frame.Steps[0].Signal), Is.False, frame.Id + ": done once");

                // Skip works on a task that was not done.
                var skipper = NewFlow();
                GoTo(skipper, frame.Id);
                Assert.That(skipper.CanSkip, Is.True, frame.Id);
                TutorialEnd? ended = null;
                skipper.Ended += e => ended = e;
                Assert.That(skipper.Skip(), Is.True, frame.Id);
                if (frame.Id == "4.2") Assert.That(ended, Is.EqualTo(TutorialEnd.Finished), "skipping the last task ends the tour");
                else Assert.That(skipper.Current.Id, Is.Not.EqualTo(frame.Id));
            }
        }

        [Test]
        public void ASignalOutOfOrder_IsIgnored_ItDoesNotCountEarly()
        {
            var flow = NewFlow();
            GoTo(flow, "3.1");
            Assert.That(flow.Report(TutorialSignal.PhotoCaptured), Is.False, "a capture before the box");
            Assert.That(flow.Report(TutorialSignal.CopyPlaced), Is.False);
            Assert.That(flow.StepIndex, Is.EqualTo(0));
            Assert.That(flow.Report(TutorialSignal.BoxCompleted), Is.True);
            Assert.That(flow.Report(TutorialSignal.PhotoCaptured), Is.False, "the menu step comes first");
            Assert.That(flow.StepIndex, Is.EqualTo(1));
        }

        [Test]
        public void AdvanceIfReady_NeedsTheTaskDoneAndTheSentenceEnd()
        {
            var flow = NewFlow();
            Assert.That(flow.AdvanceIfReady(true), Is.False, "task not done");
            Complete(flow);
            Assert.That(flow.AdvanceIfReady(false), Is.False, "the sentence is still being said");
            Assert.That(flow.Current.Id, Is.EqualTo("1.1"));
            Assert.That(flow.AdvanceIfReady(true), Is.True);
            Assert.That(flow.Current.Id, Is.EqualTo("1.2"));
            Assert.That(flow.AutoAdvancePending, Is.False);
        }

        [Test]
        public void TheLastTaskDoesNotAdvanceByItself_FinishIsThePlayersToPress()
        {
            var flow = NewFlow();
            GoTo(flow, "4.2");
            Complete(flow);
            Assert.That(flow.AutoAdvancePending, Is.False);
            Assert.That(flow.AdvanceIfReady(true), Is.False);
            Assert.That(flow.CanNext, Is.True);
            Assert.That(flow.Next(), Is.True);
            Assert.That(flow.End, Is.EqualTo(TutorialEnd.Finished));
        }

        [Test]
        public void Skip_GoesOnWithoutTheTask_BackReturnsAndRestartsIt()
        {
            var flow = NewFlow();
            flow.Skip();                          // 1.2
            Assert.That(flow.Report(TutorialSignal.WhiteboardPlaced), Is.False, "the whiteboard's step comes after the chessboard's");
            flow.Report(TutorialSignal.ChessPlaced);
            Assert.That(flow.StepIndex, Is.EqualTo(1));
            Assert.That(flow.Skip(), Is.True);
            Assert.That(flow.Current.Id, Is.EqualTo("2.1"));
            Assert.That(flow.IsFrameDone(1), Is.False, "a skipped task is not done");
            Assert.That(flow.Back(), Is.True);
            Assert.That(flow.Current.Id, Is.EqualTo("1.2"));
            Assert.That(flow.ProgressInFrame, Is.EqualTo(0), "coming back restarts the task");
            Assert.That(flow.CanNext, Is.False);
        }

        [Test]
        public void RewindTo_TakesATaskBackToAnEarlierStep_ForTryAgain()
        {
            var flow = NewFlow();
            GoTo(flow, "3.1");
            Assert.That(flow.RewindTo(1), Is.False, "nothing to rewind to yet");
            flow.Report(TutorialSignal.BoxCompleted);
            flow.Report(TutorialSignal.ReconstructChosen);
            flow.Report(TutorialSignal.PhotoCaptured);
            flow.Report(TutorialSignal.MaskReady);
            flow.Report(TutorialSignal.ScanSubmitted);
            Assert.That(flow.StepIndex, Is.EqualTo(5));
            Assert.That(flow.RewindTo(1), Is.True);
            Assert.That(flow.StepIndex, Is.EqualTo(1));
            Assert.That(flow.Report(TutorialSignal.PhotoCaptured), Is.False);
            Assert.That(flow.Report(TutorialSignal.ReconstructChosen), Is.True);
            Assert.That(flow.RewindTo(9), Is.False);
        }

        [Test]
        public void Back_IsOfferedOnEveryFrameButTheFirst()
        {
            var flow = NewFlow();
            Assert.That(flow.CanBack, Is.False);
            Assert.That(flow.Back(), Is.False);
            Assert.That(flow.Current.Id, Is.EqualTo("1.1"));
            flow.Skip();
            Assert.That(flow.CanBack, Is.True);
        }

        [Test]
        public void NextOnTheLastFrame_Finishes_AndSkipOnItDoesToo()
        {
            foreach (bool useSkip in new[] { false, true })
            {
                var flow = NewFlow();
                TutorialEnd? ended = null;
                flow.Ended += e => ended = e;
                while (!flow.IsLast) flow.Skip();
                Assert.That(flow.Current.Id, Is.EqualTo("4.2"));
                if (!useSkip) Complete(flow);
                Assert.That(useSkip ? flow.Skip() : flow.Next(), Is.True);
                Assert.That(ended, Is.EqualTo(TutorialEnd.Finished));
                Assert.That(flow.Running, Is.False);
                Assert.That(flow.Next(), Is.False, "nothing moves after the end");
                Assert.That(flow.Back(), Is.False);
            }
        }

        [Test]
        public void Exit_AsksForConfirmation_Cancel_Keeps_Confirm_Ends()
        {
            var flow = NewFlow();
            var pending = new List<bool>();
            flow.ExitPendingChanged += pending.Add;
            TutorialEnd? ended = null;
            flow.Ended += e => ended = e;

            flow.RequestExit();
            Assert.That(flow.ExitPending, Is.True);
            Assert.That(flow.Running, Is.True, "X alone never ends it");
            Assert.That(flow.CanNext || flow.CanBack || flow.CanSkip, Is.False, "the card is frozen behind the confirmation");
            Assert.That(flow.Report(TutorialSignal.ButtonPressed), Is.False);
            flow.CancelExit();
            Assert.That(flow.ExitPending, Is.False);
            Assert.That(flow.Running, Is.True);
            Assert.That(ended, Is.Null);

            flow.RequestExit();
            flow.ConfirmExit();
            Assert.That(ended, Is.EqualTo(TutorialEnd.Exited));
            Assert.That(flow.End, Is.EqualTo(TutorialEnd.Exited));
            Assert.That(pending, Is.EqualTo(new[] { true, false, true, false }));
        }

        [Test]
        public void ConfirmWithoutARequest_DoesNothing()
        {
            var flow = NewFlow();
            flow.ConfirmExit();
            Assert.That(flow.Running, Is.True);
        }

        [Test]
        public void NoSignalAndNoMove_EverExits_OnlyConfirmedXDoes()
        {
            // "B must never exit the tutorial": B is not an input of the flow at all. The only ways to Exited are RequestExit + ConfirmExit.
            var flow = NewFlow();
            foreach (var signal in System.Enum.GetValues(typeof(TutorialSignal)).Cast<TutorialSignal>())
                for (int i = 0; i < 3; i++) flow.Report(signal);
            while (flow.Running && !flow.IsLast) { flow.Back(); flow.Skip(); flow.Skip(); }
            Assert.That(flow.End, Is.Not.EqualTo(TutorialEnd.Exited));
        }

        [Test]
        public void GoToChapter_JumpsToItsFirstFrame()
        {
            var flow = NewFlow();
            GoTo(flow, "3.1");
            Assert.That(flow.GoToChapter(2), Is.True);
            Assert.That(flow.Current.Id, Is.EqualTo("2.1"));
            Assert.That(flow.GoToChapter(2), Is.False, "already there");
            Assert.That(flow.GoToChapter(9), Is.False);
        }

        [Test]
        public void FrameChanged_ReportsOldAndNewIndex()
        {
            var flow = NewFlow();
            var moves = new List<(int, int)>();
            flow.FrameChanged += (a, b) => moves.Add((a, b));
            flow.Skip(); flow.Skip(); flow.Back();
            Assert.That(moves, Is.EqualTo(new[] { (0, 1), (1, 2), (2, 1) }));
        }
    }
}
#endif
