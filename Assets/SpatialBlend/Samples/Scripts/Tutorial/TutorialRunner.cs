// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>Everything the tutorial needs from the scene. The Unity implementation is <c>ControlPanelTutorialHost</c>; tests use a fake.</summary>
    public interface ITutorialHost
    {
        /// <summary>The interaction mode: true is Scene Edit.</summary>
        bool SceneEdit { get; set; }
        /// <summary>Whether the real room's anchors (walls, floor, furniture) are drawn.</summary>
        bool AnchorsVisible { get; set; }
        /// <summary>The passthrough brightness setting.</summary>
        float Brightness { get; set; }
        bool HandTrackingActive { get; }
        /// <summary>The headset has a room setup (walls, floor, furniture) to show. Without it "Show anchors" shows nothing.</summary>
        bool RoomAnchorsAvailable { get; }

        /// <summary>Hides the lobby panel for the tutorial's run, and brings it back.</summary>
        void SetLobbyHidden(bool hidden);
        void FrameEntered(TutorialFrame frame);
        void FrameLeft(TutorialFrame frame);
        /// <summary>The step's line is starting. The host shows what that step is about.</summary>
        void StepStarted(TutorialFrame frame, int stepIndex);
        /// <summary>A step of the task was verified: the host plays the success sound and clears that step's targets.</summary>
        void StepCompleted(TutorialFrame frame, int stepIndex);
        /// <summary>Starts a narration clip and returns its length in seconds; 0 when there is no such clip.</summary>
        double PlayVoice(string clipName);
        void StopVoice();
        void PauseVoice(bool paused);
        /// <summary>The tutorial is over (finished or exited): remove the practice props and anything else it added.</summary>
        void Ended(TutorialEnd end);
    }

    /// <summary>
    /// One run of the tutorial, with no Unity in it. It owns the frame flow, the narration timing and the chapter 4 demo,
    /// snapshots the scene state before the first frame and puts it back when the run ends however it ends. The Unity side
    /// pumps <see cref="Tick"/> and feeds real events in through <see cref="Report"/>.
    /// </summary>
    public sealed class TutorialRunner
    {
        public const string ControllerNotice = "This step needs controllers. Pick them up to try it, or skip it.";
        public const string RoomSetupText = "No room setup found. Set up your room in the Quest settings, or skip this step.";

        private readonly ITutorialHost _host;
        private readonly ITutorialClock _clock;
        private readonly TutorialDoneMark _done;
        private bool _snapshotTaken;
        private bool _snapshotSceneEdit;
        private bool _snapshotAnchors;
        private float _snapshotBrightness;
        // True while the mode-switch assist is held suspended by this run. It is released for the tasks about modes (see SyncAssist) and again at the end.
        private bool _assistSuspended;
        // True from Begin until the end: the run holds the assist (suspended or, in the mode tasks, live) and owes it back to the app.
        private bool _assistHeld;
        private bool _lastSceneEdit;
        private bool _leadInPending;
        // A pause (a chapter change, or the arrival of someone else's object) runs from the moment the narration is over.
        private double _gapStart = -1;
        // A sub-step was ticked and its line waits for the narration to end.
        private bool _stepLinePending;
        // The current step ends when its line ends, and that line has been started.
        private bool _heardArmed;
        private string _hint;
        private bool _begun;
        private bool _ended;
        private string _scanProblem;

        public TutorialRunner(ITutorialHost host, ITutorialClock clock, TutorialDoneMark done,
                              IReadOnlyList<TutorialFrame> frames = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _done = done ?? throw new ArgumentNullException(nameof(done));
            Flow = new TutorialFlow(frames ?? TutorialScript.Frames);
            Narrator = new TutorialNarrator(clock);
            Room = new TutorialSimulatedRoom(clock);
            Flow.FrameChanged += OnFrameChanged;
            Flow.StepCompleted += OnStepCompleted;
            Flow.ActionCompleted += OnActionCompleted;
            Flow.Ended += OnEnded;
            Flow.ExitPendingChanged += pending =>
            {
                StateChanged?.Invoke();
            };
        }

        public TutorialFlow Flow { get; }
        public TutorialNarrator Narrator { get; }
        public TutorialSimulatedRoom Room { get; }

        /// <summary>A scan request has been sent and its result has not come yet (chapter 3's "Building your 3D copy").</summary>
        public bool ScanInFlight { get; private set; }

        public void NoteScanInFlight(bool inFlight)
        {
            if (ScanInFlight == inFlight) return;
            ScanInFlight = inFlight;
            StateChanged?.Invoke();
        }

        /// <summary>The scan failure shown on the card, or null.</summary>
        public string ScanProblem => _scanProblem;

        public event Action StateChanged;

        // The "Switch to Editing first" line is to be said again (the user left Editing in a step that needs it, or tried the step in
        // Interaction); Tick says it once nothing else is being said.
        private bool _modeLineDue;

        // A one-shot signal that arrived in Interaction, reported again once the user is back in Editing.
        private TutorialSignal? _heldSignal;

        private static bool IsOneShot(TutorialSignal signal) =>
            signal == TutorialSignal.PhotoCaptured || signal == TutorialSignal.MaskReady || signal == TutorialSignal.ScanSubmitted;

        /// <summary>A short note for the current frame (the hand tracking note), or null.</summary>
        public string Notice => Flow.Running && Flow.Current.NeedsController && _host.HandTrackingActive ? ControllerNotice : null;

        /// <summary>Shown on the "Place the boards" and "Show anchors" frames when the headset has no room setup (no room to place in, no anchors to show).</summary>
        public string RoomSetupNotice => Flow.Running && (Flow.Current.Id == "1.2" || Flow.Current.Id == "2.1") && !_host.RoomAnchorsAvailable ? RoomSetupText : null;

        /// <summary>A short line under the task (which way to turn to see a board, a drop that found no spot), or null.</summary>
        public string Hint
        {
            get
            {
                if (!Flow.Running || Flow.ActionDone) return null;
                if (EditingMissing) return TutorialScript.SwitchToEditingText;
                TutorialStep step = Flow.Current.Steps[Flow.StepIndex];
                return _hint ?? (PracticeObjectShown && step.PracticeNote != null ? step.PracticeNote : step.Note);
            }
        }

        /// <summary>The task needs Editing for the step the user is on, and the scene is in Interaction (the tutorial never switches it for them).</summary>
        public bool EditingMissing =>
            Flow.Running && !Flow.ActionDone && Flow.Current.NeedsEditing && Flow.Current.Steps[Flow.StepIndex].RequiresEditing && !_host.SceneEdit;

        /// <summary>The scan runs on the practice server: a practice object stands in the user's box (the host sets it).</summary>
        public bool PracticeObjectShown { get; set; }

        public void SetHint(string hint)
        {
            if (_hint == hint) return;
            _hint = hint;
            StateChanged?.Invoke();
        }

        /// <summary>Chapter 4: the room list was opened (the real list, or the sample rooms when the real one cannot be read).</summary>
        public bool RoomListShown { get; private set; }
        public bool RoomListIsSample { get; private set; }

        public const string DemoChoiceText = "Demo only: no room was joined or created.";
        private string _chosenRoom;
        private bool _chosenHosting;

        /// <summary>
        /// The user tapped a room (or New room). During "Pick a room" this completes the task and starts the simulated connecting;
        /// at any other time it is only remembered. Nothing is joined or created either way.
        /// </summary>
        public void ChooseRoom(string roomName, bool hosting)
        {
            _chosenRoom = roomName;
            _chosenHosting = hosting;
            if (Flow.Running && Flow.Current.Id == "4.2" && !Flow.ActionDone)
            {
                if (hosting) Room.Host();
                else Room.Join(roomName);
                Report(TutorialSignal.RoomPicked);
            }
            StateChanged?.Invoke();
        }

        /// <summary>Shown once a room was picked.</summary>
        public string DemoChoiceNotice => Flow.Running && Flow.Current.Id == "4.2" && (_chosenHosting || _chosenRoom != null) ? DemoChoiceText : null;

        public void NoteRoomList(bool shown, bool sample)
        {
            RoomListShown = shown;
            RoomListIsSample = shown && sample;
            if (shown) Flow.Report(TutorialSignal.FindRoomPressed);
            StateChanged?.Invoke();
        }

        public void Begin()
        {
            if (_begun) return;
            _begun = true;
            _snapshotSceneEdit = _host.SceneEdit;
            _snapshotAnchors = _host.AnchorsVisible;
            _snapshotBrightness = _host.Brightness;
            _snapshotTaken = true;
            _assistSuspended = TutorialModeAssistBridge.Suspend();
            _assistHeld = true;
            _lastSceneEdit = _host.SceneEdit;
            _host.SetLobbyHidden(true);
            EnterFrame(Flow.Current);
        }

        /// <summary>Call every frame.</summary>
        public void Tick()
        {
            if (!_begun || _ended) return;
            Narrator.Tick();
            Room.Tick();
            // The mode is the user's to change in the mode tasks: a change refreshes the card (the "Switch to Editing first" line), and the assist follows it.
            bool edit = _host.SceneEdit;
            if (edit != _lastSceneEdit)
            {
                _lastSceneEdit = edit;
                // Left Editing in the middle of a step that needs it: the "Switch to Editing first" line is said again.
                if (EditingMissing) _modeLineDue = true;
                StateChanged?.Invoke();
            }
            if (!EditingMissing) _modeLineDue = false;
            if (_heldSignal.HasValue && Flow.Running && !Flow.ActionDone && Flow.Current.Steps[Flow.StepIndex].Signal != _heldSignal.Value)
                _heldSignal = null;   // the step moved on (Skip, Back): the held result no longer applies
            if (_heldSignal.HasValue && _host.SceneEdit)
            {
                TutorialSignal held = _heldSignal.Value;
                _heldSignal = null;
                Report(held);
            }
            SyncAssist();
            // The mode line waits for the line being said (narration is never cut off), and is said only while it still applies.
            if (_modeLineDue && !_leadInPending && !Narrator.Playing && !Narrator.Paused && !string.IsNullOrEmpty(Flow.Current.ModeClipName))
            {
                _modeLineDue = false;
                double lead = _host.PlayVoice(Flow.Current.ModeClipName);
                Narrator.Play(Flow.Current.ModeVoice, lead);
                StateChanged?.Invoke();
            }
            if (_leadInPending && !Narrator.Playing)
            {
                _leadInPending = false;
                if (Flow.Running && !Flow.ExitPending && !Flow.ActionDone && Flow.StepIndex == 0) PlayStep(Flow.Current.Steps[0]);
            }
            TickGap();
        }

        // The next line, or the next task, starts once nothing is being said. A chapter change waits <see cref="TutorialScript.ChapterGapSeconds"/>.
        // The object someone else shared waits <see cref="TutorialScript.IncomingShareSeconds"/> after the share line.
        private void TickGap()
        {
            if (!Flow.Running || Flow.ExitPending || Narrator.Playing || Narrator.Paused) { _gapStart = -1; return; }
            if (!_stepLinePending && !Flow.AutoAdvancePending && _heardArmed && !Flow.ActionDone
                && Flow.Current.Steps[Flow.StepIndex].CompletesWhenHeard)
            {
                _heardArmed = false;
                Flow.Report(Flow.Current.Steps[Flow.StepIndex].Signal);
                return;
            }
            if (!_stepLinePending && !Flow.AutoAdvancePending) { _gapStart = -1; return; }
            double wait = PendingWait();
            if (wait > 0)
            {
                if (_gapStart < 0) _gapStart = _clock.Now;
                if (_clock.Now - _gapStart < wait) return;
            }
            _gapStart = -1;
            if (Flow.AutoAdvancePending)
            {
                _stepLinePending = false;
                Flow.AdvanceIfReady(true);
            }
            else
            {
                _stepLinePending = false;
                if (!Flow.ActionDone) PlayStep(Flow.Current.Steps[Flow.StepIndex]);
            }
        }

        private double PendingWait()
        {
            if (_stepLinePending)
            {
                int next = Flow.StepIndex;
                TutorialStep[] steps = Flow.Current.Steps;
                if (next < steps.Length && steps[next].Signal == TutorialSignal.CopyPlaced)
                    return Math.Max(0.0, TutorialScript.IncomingShareSeconds);
                return 0.0;
            }
            if (Flow.AutoAdvancePending && !Flow.IsLast && Flow.Frames[Flow.Index + 1].Chapter != Flow.Current.Chapter)
                return TutorialScript.ChapterGapSeconds;
            return 0.0;
        }

        /// <summary>Feeds a real event in. Only a signal the current frame is waiting for counts.</summary>
        public bool Report(TutorialSignal signal)
        {
            // A step that needs Editing does not count in Interaction, whatever event was seen.
            if (Flow.Running && !Flow.ExitPending && !Flow.ActionDone && Flow.Current.Steps[Flow.StepIndex].RequiresEditing && !_host.SceneEdit)
            {
                // Tried in Interaction: the card already says "Switch to Editing first"; it is said again too.
                if (Flow.Current.NeedsEditing) _modeLineDue = true;
                // A one-shot result of work already under way (the photo, the mask, the submit) is not lost: it counts once the user
                // is back in Editing.
                if (IsOneShot(signal) && Flow.Current.Steps[Flow.StepIndex].Signal == signal) _heldSignal = signal;
                StateChanged?.Invoke();
                return false;
            }
            bool counted = Flow.Report(signal);
            if (counted) StateChanged?.Invoke();
            return counted;
        }

        /// <summary>A scan could not be made: the card says so in plain words and offers Skip.</summary>
        public void ReportScanFailure(string userMessage)
        {
            _scanProblem = string.IsNullOrWhiteSpace(userMessage) ? "The scan did not work. You can skip this step." : userMessage;
            ScanInFlight = false;
            StateChanged?.Invoke();
        }

        public void ClearScanProblem()
        {
            if (_scanProblem == null) return;
            _scanProblem = null;
            StateChanged?.Invoke();
        }

        /// <summary>The headset lost (false) or regained (true) focus: the narration pauses with it.</summary>
        public void SetFocus(bool focused)
        {
            if (!_begun || _ended) return;
            if (focused) { Narrator.Resume(); _host.PauseVoice(false); }
            else { Narrator.Pause(); _host.PauseVoice(true); }
        }

        /// <summary>Takes the current task back to an earlier step.</summary>
        public bool RewindTo(int stepIndex)
        {
            bool moved = Flow.RewindTo(stepIndex);
            if (moved)
            {
                _hint = null;
                _stepLinePending = false;
                PlayStep(Flow.Current.Steps[stepIndex]);   // the line of the step the user is back on
            }
            return moved;
        }

        public bool Next() => Flow.Next();
        public bool Back() => Flow.Back();
        public bool Skip() => Flow.Skip();
        public void RequestExit() => Flow.RequestExit();
        public void CancelExit() => Flow.CancelExit();
        public void ConfirmExit() => Flow.ConfirmExit();

        /// <summary>The user left some other way (the panel was destroyed): the state is restored all the same.</summary>
        public void Abort()
        {
            if (!_begun || _ended) return;
            if (Flow.Running)
            {
                Flow.RequestExit();
                Flow.ConfirmExit();
            }
        }

        private void OnFrameChanged(int oldIndex, int newIndex)
        {
            _host.FrameLeft(Flow.Frames[oldIndex]);
            Narrator.Stop();
            _host.StopVoice();
            _scanProblem = null;
            _hint = null;
            _leadInPending = false;
            _stepLinePending = false;
            _heardArmed = false;
            _gapStart = -1;
            // "Pick a room" follows "Find a room" with the list still open; any other move starts the room demo over.
            bool keepList = oldIndex < newIndex && Flow.Frames[oldIndex].Id == "4.1" && Flow.Frames[newIndex].Id == "4.2";
            if (!keepList)
            {
                RoomListShown = false;
                RoomListIsSample = false;
            }
            // A tap during the previous task's dwell is not this task's pick.
            _chosenRoom = null;
            _chosenHosting = false;
            // Leaving the anchors task without having turned them on (Skip, Back) puts the user's own setting back at once.
            if (Flow.Frames[oldIndex].AnchorsStartOff && !AnchorsTurnedOn(oldIndex)) _host.AnchorsVisible = _snapshotAnchors;
            Room.Reset();
            EnterFrame(Flow.Frames[newIndex]);
            StateChanged?.Invoke();
        }

        // The anchors step of that frame was done (the user turned them on): they stay on even if a later step of the task is skipped.
        private bool AnchorsTurnedOn(int frameIndex)
        {
            if (Flow.IsFrameDone(frameIndex)) return true;
            TutorialStep[] steps = Flow.Frames[frameIndex].Steps;
            int done = Flow.StepsDoneIn(frameIndex);
            for (int i = 0; i < done && i < steps.Length; ++i) if (steps[i].Signal == TutorialSignal.AnchorsOn) return true;
            return false;
        }

        private void EnterFrame(TutorialFrame frame)
        {
            // The tutorial sets the mode itself where a task only needs it as a stage (the cube, the vase); the user is never asked to there.
            // A task that needs Editing from the user (the scan) or is about the switch itself (the mode task) leaves the mode alone, except that
            // the mode task starts in Interaction so that its first step is a real change.
            if (!frame.NeedsEditing) _host.SceneEdit = frame.SceneEdit;
            _lastSceneEdit = _host.SceneEdit;
            // The scene anchors start off, so that turning them on is a change the app can see (the snapshot restores them on exit).
            if (frame.AnchorsStartOff) _host.AnchorsVisible = false;
            _host.FrameEntered(frame);
            SyncAssist();
            if (EditingMissing && !string.IsNullOrEmpty(frame.ModeClipName))
            {
                // Starts in Interaction in a task that needs Editing: say so first, then the task's own first line.
                double lead = _host.PlayVoice(frame.ModeClipName);
                Narrator.Play(frame.ModeVoice, lead);
                _leadInPending = true;   // the lead-in is said whole (it has two sentences), then Tick plays the first step's line
                StateChanged?.Invoke();
                return;
            }
            _host.StepStarted(frame, 0);
            double length = _host.PlayVoice(frame.ClipName);
            Narrator.Play(frame.Voice, length);
            _heardArmed = frame.Steps.Length > 0 && frame.Steps[0].CompletesWhenHeard;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// The mode-switch assist is live only where the tour needs the user to use it: the whole mode task, and a task that needs Editing
        /// while the user is still in Interaction (so the chip and the [Edit] prompt are there to switch with). It is suspended again as soon
        /// as the user is where the task needs, so its idle auto-return cannot pull them out of the task.
        /// </summary>
        private void SyncAssist()
        {
            if (!_begun || _ended) return;
            bool want = Flow.Running && !Flow.ExitPending && (Flow.Current.ModeAssistLive || EditingMissing);
            if (want && _assistSuspended)
            {
                TutorialModeAssistBridge.Resume();
                _assistSuspended = false;
            }
            else if (!want && !_assistSuspended)
            {
                _assistSuspended = TutorialModeAssistBridge.Suspend();
            }
        }

        private void OnStepCompleted(TutorialFrame frame, int stepIndex)
        {
            _hint = null;
            _host.StepCompleted(frame, stepIndex);
            bool last = stepIndex == frame.Steps.Length - 1;
            _heardArmed = false;
            if (!last)
            {
                // The checklist moves on at once; the next step's line is said once the line now playing is over.
                _stepLinePending = true;
                _gapStart = -1;
            }
            StateChanged?.Invoke();
        }

        private void PlayStep(TutorialStep step)
        {
            _heardArmed = step.CompletesWhenHeard;
            _host.StepStarted(Flow.Current, Flow.StepIndex);
            double length = _host.PlayVoice(step.ClipName);
            Narrator.Play(step.Voice, length);
            StateChanged?.Invoke();
        }

        private void OnActionCompleted(TutorialFrame frame)
        {
            // A task advances on its last step's event, once the narration line is over and the gap has passed (the check mark stays up).
            _stepLinePending = false;
            _gapStart = -1;
            StateChanged?.Invoke();
        }

        private void OnEnded(TutorialEnd end)
        {
            if (_ended) return;
            _ended = true;
            Narrator.Stop();
            _host.StopVoice();
            _host.FrameLeft(Flow.Current);
            if (_snapshotTaken)
            {
                _host.SceneEdit = _snapshotSceneEdit;
                _host.AnchorsVisible = _snapshotAnchors;
                _host.Brightness = _snapshotBrightness;
            }
            // The mark is set before the lobby comes back, so the hint dot is drawn already hidden.
            if (end == TutorialEnd.Finished) _done.MarkDone();
            _host.SetLobbyHidden(false);
            if (_assistHeld) TutorialModeAssistBridge.Release();
            _assistHeld = false;
            _assistSuspended = false;
            _host.Ended(end);
            StateChanged?.Invoke();
        }
    }
}
