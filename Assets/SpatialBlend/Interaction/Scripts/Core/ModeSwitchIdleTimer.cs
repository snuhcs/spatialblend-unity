// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Where the Scene Edit to Interaction auto-return is.
    /// </summary>
    public enum ModeSwitchPhase
    {
        /// <summary>Not in Scene Edit, or the assist is suspended: nothing is counting.</summary>
        Inactive,
        /// <summary>In Scene Edit and the user is editing, or has not been idle long enough to be told.</summary>
        Editing,
        /// <summary>Idle long enough: the return to Interaction is announced and counting down.</summary>
        Countdown,
    }

    /// <summary>What one <see cref="ModeSwitchIdleTimer.Tick"/> decided.</summary>
    public readonly struct ModeSwitchTick
    {
        public ModeSwitchTick(ModeSwitchPhase phase, float countdownRemaining, bool switchNow)
        {
            Phase = phase;
            CountdownRemaining = countdownRemaining;
            SwitchNow = switchNow;
        }

        public ModeSwitchPhase Phase { get; }

        /// <summary>Seconds until the switch while <see cref="Phase"/> is <see cref="ModeSwitchPhase.Countdown"/>, else 0.</summary>
        public float CountdownRemaining { get; }

        /// <summary>The caller must switch to Interaction now. Reported once; the timer is then <see cref="ModeSwitchPhase.Inactive"/>.</summary>
        public bool SwitchNow { get; }

        /// <summary>The whole number shown in the toast ("Back to Interaction in 5..."): the seconds left, rounded up, never below 1.</summary>
        public int CountdownDisplaySeconds => CountdownRemaining <= 0f ? 0 : Math.Max(1, (int)Math.Ceiling(CountdownRemaining - 1e-4f));
    }

    /// <summary>
    /// The state machine behind the idle auto-return from Scene Edit to Interaction. It has no Unity dependency and no clock of its own:
    /// every call is given <c>now</c> in seconds, so a test steps time by passing numbers and never waits.
    /// <para>
    /// After <c>idleSeconds</c> with no editing the countdown starts; after a further <c>countdownSeconds</c> the switch is due
    /// (so 10 s and 15 s with the default values). Any activity, or <see cref="KeepEditing"/>, cancels the countdown and restarts the
    /// idle wait. While the caller reports <c>busy</c> (a grab, a box being drawn, the object menu or the scan panel open) the timer is held at
    /// zero, so a switch can never happen under such work, not even on the frame it would be due.
    /// </para>
    /// <para>
    /// The switch is always announced first: if one tick finds the deadline already passed without the countdown having been seen (the headset
    /// was off, a long frame), the countdown starts at that tick with its full length instead of switching unannounced.
    /// </para>
    /// </summary>
    public sealed class ModeSwitchIdleTimer
    {
        private readonly float _idleSeconds;
        private readonly float _countdownSeconds;
        private float _lastActivity;
        private ModeSwitchPhase _phase = ModeSwitchPhase.Inactive;

        public ModeSwitchIdleTimer(float idleSeconds, float countdownSeconds)
        {
            if (idleSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(idleSeconds));
            if (countdownSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(countdownSeconds));
            _idleSeconds = idleSeconds;
            _countdownSeconds = countdownSeconds;
        }

        public float IdleSeconds => _idleSeconds;
        public float CountdownSeconds => _countdownSeconds;
        public ModeSwitchPhase Phase => _phase;

        /// <summary>
        /// Editing activity at <paramref name="now"/>: restarts the idle wait and cancels a running countdown. Ignored while inactive
        /// (the first <see cref="Tick"/> in Scene Edit starts the wait itself).
        /// </summary>
        public void NotifyActivity(float now)
        {
            if (_phase == ModeSwitchPhase.Inactive) return;
            _lastActivity = now;
            _phase = ModeSwitchPhase.Editing;
        }

        /// <summary>The [Keep editing] button: the same as activity.</summary>
        public void KeepEditing(float now) => NotifyActivity(now);

        /// <summary>
        /// Advances to <paramref name="now"/>. <paramref name="editMode"/> is whether the scene is in Scene Edit, <paramref name="busy"/> whether
        /// editing work is in progress, <paramref name="suspended"/> whether something (the tutorial) has paused the assist.
        /// </summary>
        public ModeSwitchTick Tick(float now, bool editMode, bool busy, bool suspended)
        {
            if (!editMode || suspended)
            {
                _phase = ModeSwitchPhase.Inactive;
                return new ModeSwitchTick(ModeSwitchPhase.Inactive, 0f, false);
            }

            if (_phase == ModeSwitchPhase.Inactive)
            {
                // Entering Scene Edit (or resuming) is the first moment of the idle wait.
                _lastActivity = now;
                _phase = ModeSwitchPhase.Editing;
            }

            if (busy)
            {
                _lastActivity = now;
                _phase = ModeSwitchPhase.Editing;
                return new ModeSwitchTick(ModeSwitchPhase.Editing, 0f, false);
            }

            float idle = now - _lastActivity;
            if (idle < _idleSeconds)
            {
                _phase = ModeSwitchPhase.Editing;
                return new ModeSwitchTick(ModeSwitchPhase.Editing, 0f, false);
            }

            float remaining = _idleSeconds + _countdownSeconds - idle;
            if (remaining <= 0f && _phase != ModeSwitchPhase.Countdown)
            {
                // Never seen counting down: announce it now, with all of its seconds.
                _lastActivity = now - _idleSeconds;
                remaining = _countdownSeconds;
            }
            if (remaining > 0f)
            {
                _phase = ModeSwitchPhase.Countdown;
                return new ModeSwitchTick(ModeSwitchPhase.Countdown, remaining, false);
            }

            // Due. The caller switches; the mode change makes the next tick inactive, and the timer restarts on re-entry.
            _phase = ModeSwitchPhase.Inactive;
            return new ModeSwitchTick(ModeSwitchPhase.Inactive, 0f, true);
        }
    }
}
