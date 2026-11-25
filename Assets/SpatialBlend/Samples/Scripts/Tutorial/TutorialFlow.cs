// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    public enum TutorialEnd
    {
        /// <summary>Still running.</summary>
        None,
        /// <summary>The user went through the last frame (Next or Skip on it). This is what marks the tutorial done.</summary>
        Finished,
        /// <summary>The user pressed X and confirmed.</summary>
        Exited,
    }

    /// <summary>
    /// The frame state machine, with no Unity in it: which frame is showing, what the user has done in it, and the moves
    /// Back, Next, Skip and Exit. B never reaches it (the tutorial has no B handler), so B cannot exit.
    /// </summary>
    public sealed class TutorialFlow
    {
        private readonly IReadOnlyList<TutorialFrame> _frames;
        private readonly int[] _progress;
        private readonly bool[] _done;
        private bool _autoPending;

        public TutorialFlow(IReadOnlyList<TutorialFrame> frames)
        {
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
            if (frames.Count == 0) throw new ArgumentException("A tutorial needs at least one frame.", nameof(frames));
            _progress = new int[frames.Count];
            _done = new bool[frames.Count];
            for (int i = 0; i < frames.Count; i++) _done[i] = frames[i].InfoOnly;
        }

        public IReadOnlyList<TutorialFrame> Frames => _frames;
        public int Index { get; private set; }
        public TutorialFrame Current => _frames[Index];
        public TutorialEnd End { get; private set; }
        public bool Running => End == TutorialEnd.None;
        /// <summary>True between pressing X and answering the confirmation.</summary>
        public bool ExitPending { get; private set; }

        /// <summary>The user has done what the current frame asks (always true for an information frame).</summary>
        public bool ActionDone => _done[Index];
        public bool IsFirst => Index == 0;
        public bool IsLast => Index == _frames.Count - 1;
        /// <summary>Back is offered on every frame but the first.</summary>
        public bool CanBack => Running && !ExitPending && Index > 0;
        /// <summary>Next waits for the action. An information frame enables it at once.</summary>
        public bool CanNext => Running && !ExitPending && _done[Index];
        public bool CanSkip => Running && !ExitPending;
        /// <summary>The task is done and the frame only waits for the narration's sentence to end (and the feedback dwell), then moves on by itself.</summary>
        public bool AutoAdvancePending => _autoPending;
        /// <summary>The step the user is on inside the task (equal to the step count when the task is done).</summary>
        public int StepIndex => _progress[Index];

        /// <summary>How many of the current frame's signals have been seen.</summary>
        public int ProgressInFrame => _progress[Index];

        public bool IsFrameDone(int index) => _done[index];

        /// <summary>How many steps of frame <paramref name="index"/> were done (kept when the frame is left, reset when it is entered again).</summary>
        public int StepsDoneIn(int index) => _progress[index];

        public event Action<int, int> FrameChanged;
        public event Action<TutorialEnd> Ended;
        /// <summary>The current frame's action just became done (the signal that completed it).</summary>
        public event Action<TutorialFrame> ActionCompleted;
        /// <summary>One step of the current task just became done: the frame and the index of the step.</summary>
        public event Action<TutorialFrame, int> StepCompleted;
        public event Action<bool> ExitPendingChanged;

        /// <summary>Feeds a signal. Only the next signal the current frame asks for counts; others are ignored.</summary>
        public bool Report(TutorialSignal signal)
        {
            if (!Running || ExitPending || _done[Index]) return false;
            TutorialFrame frame = _frames[Index];
            if (frame.Steps[_progress[Index]].Signal != signal) return false;
            int step = _progress[Index]++;
            if (_progress[Index] < frame.Steps.Length)
            {
                StepCompleted?.Invoke(frame, step);
                return true;
            }
            _done[Index] = true;
            _autoPending = frame.AutoAdvance;
            StepCompleted?.Invoke(frame, step);
            ActionCompleted?.Invoke(frame);
            return true;
        }

        /// <summary>
        /// Called when the narration reached the end of its current sentence (or has nothing left to say). Moves on if the
        /// frame's action is done and the frame moves on by itself.
        /// </summary>
        public bool AdvanceIfReady(bool narrationAtSentenceEnd)
        {
            if (!_autoPending || !narrationAtSentenceEnd || !Running || ExitPending) return false;
            return Next();
        }

        public bool Next()
        {
            if (!CanNext) return false;
            return MoveForward();
        }

        /// <summary>Goes on without doing the step. The step stays not done, so Back and forward again still waits for it.</summary>
        public bool Skip()
        {
            if (!CanSkip) return false;
            return MoveForward();
        }

        private bool MoveForward()
        {
            _autoPending = false;
            if (IsLast)
            {
                Finish(TutorialEnd.Finished);
                return true;
            }
            Go(Index + 1);
            return true;
        }

        public bool Back()
        {
            if (!CanBack) return false;
            _autoPending = false;
            Go(Index - 1);
            return true;
        }

        /// <summary>Goes back to an earlier step of the current task (a failed scan: press Capture again). Steps from there on are not done.</summary>
        public bool RewindTo(int stepIndex)
        {
            if (!Running || ExitPending || stepIndex < 0 || stepIndex >= _frames[Index].Steps.Length) return false;
            if (!_done[Index] && stepIndex >= _progress[Index]) return false;
            _progress[Index] = stepIndex;
            _done[Index] = false;
            _autoPending = false;
            return true;
        }

        /// <summary>Back to a chapter's first frame (the chapter dots).</summary>
        public bool GoToChapter(int chapter)
        {
            if (!Running || ExitPending) return false;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (_frames[i].Chapter != chapter) continue;
                if (i == Index) return false;
                _autoPending = false;
                Go(i);
                return true;
            }
            return false;
        }

        /// <summary>X: asks for the confirmation. Nothing ends yet.</summary>
        public void RequestExit()
        {
            if (!Running || ExitPending) return;
            ExitPending = true;
            ExitPendingChanged?.Invoke(true);
        }

        public void CancelExit()
        {
            if (!ExitPending) return;
            ExitPending = false;
            ExitPendingChanged?.Invoke(false);
        }

        public void ConfirmExit()
        {
            if (!ExitPending) return;
            ExitPending = false;
            ExitPendingChanged?.Invoke(false);
            Finish(TutorialEnd.Exited);
        }

        private void Go(int index)
        {
            int old = Index;
            Index = index;
            // A frame the user comes back to starts its action again, so the card and the scene are in a known state.
            if (!_frames[index].InfoOnly)
            {
                _progress[index] = 0;
                _done[index] = false;
            }
            _autoPending = false;
            FrameChanged?.Invoke(old, index);
        }

        private void Finish(TutorialEnd end)
        {
            End = end;
            Ended?.Invoke(end);
        }
    }
}
