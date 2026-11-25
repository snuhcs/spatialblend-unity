// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// The mode-switch assist must not flip the mode while the tutorial sets it for its own steps. By default the tutorial suspends the
    /// real <see cref="ModeSwitchAssist"/> (static, owner-counted); a registered <see cref="ITutorialModeAssist"/> replaces it (tests).
    /// </summary>
    public interface ITutorialModeAssist
    {
        /// <summary>The tutorial holds the assist off (no prompt, no chip, no auto-return).</summary>
        void Suspend();
        /// <summary>The tutorial lets the assist work for a task that is about modes. In the lobby the assist is not active yet (the app starts it when a session is ready), so this starts it.</summary>
        void Resume();
        /// <summary>The run is over: the assist is exactly as the app had it (not suspended by the tutorial, and not started if the app had not started it).</summary>
        void Release();
    }

    public static class TutorialModeAssistBridge
    {
        private sealed class RealAssist : ITutorialModeAssist
        {
            // True while the assist runs only because the tutorial started it (it was not active: the lobby, before a session).
            private bool _startedIt;

            public void Suspend()
            {
                ModeSwitchAssist.Suspend(this);
                StopIfStarted();
            }

            public void Resume()
            {
                var assist = ModeSwitchAssist.Instance;
                if (assist != null && !assist.IsActive && !_startedIt)
                {
                    assist.SetActive(true);
                    _startedIt = true;
                }
                ModeSwitchAssist.Resume(this);
            }

            public void Release()
            {
                ModeSwitchAssist.Resume(this);
                StopIfStarted();
            }

            private void StopIfStarted()
            {
                if (!_startedIt) return;
                _startedIt = false;
                var assist = ModeSwitchAssist.Instance;
                if (assist != null) assist.SetActive(false);
            }
        }

        private static ITutorialModeAssist _override;
        private static readonly RealAssist Real = new RealAssist();
        private static ITutorialModeAssist Current => _override ?? Real;

        /// <summary>Always true: with no override the real assist is used.</summary>
        public static bool HasAssist => true;

        /// <summary>Replaces the real assist (null restores it).</summary>
        public static void Register(ITutorialModeAssist assist) => _override = assist;

        public static void Unregister(ITutorialModeAssist assist)
        {
            if (ReferenceEquals(_override, assist)) _override = null;
        }

        /// <summary>Suspends the assist. Returns true (the caller resumes only what it suspended).</summary>
        public static bool Suspend()
        {
            Current.Suspend();
            return true;
        }

        public static void Resume() => Current.Resume();

        /// <summary>Gives the assist back to the app as it was before the tutorial.</summary>
        public static void Release() => Current.Release();
    }
}
