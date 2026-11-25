// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.Scanning;
using UnityEngine;
using UnityEngine.TestTools;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The idle auto-return state machine (after 10 s with no editing, announce the return, then switch after 5 s).
    /// It takes its time as a number, so nothing here waits.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ModeSwitchIdleTimerTests
    {
        private const float Idle = 10f, Countdown = 5f;

        private static ModeSwitchIdleTimer NewTimer() => new ModeSwitchIdleTimer(Idle, Countdown);

        private static ModeSwitchTick Edit(ModeSwitchIdleTimer timer, float now, bool busy = false, bool suspended = false) =>
            timer.Tick(now, true, busy, suspended);

        [Test]
        public void TheOwnersValuesAreTheDefaults()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(settings.modeSwitchIdleSeconds, Is.EqualTo(10f));
                Assert.That(settings.modeSwitchCountdownSeconds, Is.EqualTo(5f));
                Assert.That(SpatialBlendSettings.DefaultModeSwitchIdleSeconds, Is.EqualTo(10f));
                Assert.That(SpatialBlendSettings.DefaultModeSwitchCountdownSeconds, Is.EqualTo(5f));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void EditingIsQuietBeforeTenSecondsAndTheCountdownStartsAtTen()
        {
            var timer = NewTimer();
            Assert.That(Edit(timer, 0f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 9.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));

            ModeSwitchTick tick = Edit(timer, 10f);

            Assert.That(tick.Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(tick.CountdownRemaining, Is.EqualTo(5f).Within(1e-4f));
            Assert.That(tick.CountdownDisplaySeconds, Is.EqualTo(5));
            Assert.That(tick.SwitchNow, Is.False);
        }

        [Test]
        public void TheCountdownCountsDownAndTheSwitchHappensAtFifteenSeconds()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Assert.That(Edit(timer, 11f).CountdownDisplaySeconds, Is.EqualTo(4));
            Assert.That(Edit(timer, 12.5f).CountdownDisplaySeconds, Is.EqualTo(3));
            Assert.That(Edit(timer, 14f).CountdownDisplaySeconds, Is.EqualTo(1));
            ModeSwitchTick last = Edit(timer, 14.99f);
            Assert.That(last.Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(last.SwitchNow, Is.False);

            ModeSwitchTick due = Edit(timer, 15f);

            Assert.That(due.SwitchNow, Is.True);
            Assert.That(due.Phase, Is.EqualTo(ModeSwitchPhase.Inactive));
        }

        [Test]
        public void TheSwitchIsReportedOnce()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Edit(timer, 10f);
            Assert.That(Edit(timer, 15f).SwitchNow, Is.True);
            // The caller switched to Interaction; had it not, the timer starts a fresh wait rather than switching again.
            Assert.That(Edit(timer, 15.1f).SwitchNow, Is.False);
            Assert.That(Edit(timer, 20f).SwitchNow, Is.False);
        }

        [Test]
        public void ADeadlineThatPassedUnseenIsAnnouncedNotSwitchedUnannounced()
        {
            var timer = NewTimer();
            Edit(timer, 0f);

            // The headset was off for ninety seconds: the very next tick is far past 15 s.
            ModeSwitchTick tick = Edit(timer, 100f);

            Assert.That(tick.SwitchNow, Is.False, "the user never saw the countdown");
            Assert.That(tick.Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(tick.CountdownRemaining, Is.EqualTo(5f).Within(1e-4f), "it starts with all of its seconds");
            Assert.That(Edit(timer, 104.9f).SwitchNow, Is.False);
            Assert.That(Edit(timer, 105f).SwitchNow, Is.True);
        }

        [Test]
        public void TheDisplayedSecondsAreNeverZeroWhileCounting()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            ModeSwitchTick tick = Edit(timer, 14.9999f);
            Assert.That(tick.Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(tick.CountdownDisplaySeconds, Is.EqualTo(1));
        }

        [Test]
        public void ActivityRestartsTheIdleWait()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Edit(timer, 8f);
            timer.NotifyActivity(8f);

            Assert.That(Edit(timer, 17.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing), "ten seconds after the activity, not after the start");
            Assert.That(Edit(timer, 18f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(Edit(timer, 23f).SwitchNow, Is.True);
        }

        [Test]
        public void ActivityCancelsARunningCountdown()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Assert.That(Edit(timer, 12f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));

            timer.NotifyActivity(12f);

            Assert.That(timer.Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 15f).SwitchNow, Is.False, "the original deadline passes without a switch");
            Assert.That(Edit(timer, 21.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 22f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(Edit(timer, 27f).SwitchNow, Is.True);
        }

        [Test]
        public void KeepEditingCancelsTheCountdownAndRestartsTheWait()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Edit(timer, 13f);

            timer.KeepEditing(13f);

            Assert.That(Edit(timer, 14f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 22.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 23f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
        }

        [Test]
        public void BusyWorkHoldsTheTimerAndNeverSwitches()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            // A grab, a box, the object menu or the scan panel is reported as busy for 40 s: no countdown, no switch.
            for (float t = 1f; t <= 40f; t += 1f)
            {
                ModeSwitchTick tick = Edit(timer, t, busy: true);
                Assert.That(tick.Phase, Is.EqualTo(ModeSwitchPhase.Editing), "t=" + t);
                Assert.That(tick.SwitchNow, Is.False, "t=" + t);
            }

            // The work ended at 40 s: the wait starts from the end of the work, not from the last edit before it.
            Assert.That(Edit(timer, 45f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 49.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 50f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
        }

        [Test]
        public void BusyOnTheFrameTheSwitchIsDueCancelsTheSwitch()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Assert.That(Edit(timer, 14.99f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));

            ModeSwitchTick tick = Edit(timer, 15f, busy: true);

            Assert.That(tick.SwitchNow, Is.False);
            Assert.That(tick.Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 15.5f).Phase, Is.EqualTo(ModeSwitchPhase.Editing), "and the wait restarts");
        }

        [Test]
        public void BusyDuringTheCountdownCancelsIt()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Assert.That(Edit(timer, 12f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(Edit(timer, 12.5f, busy: true).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 13f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
        }

        [Test]
        public void InteractionModeNeverCounts()
        {
            var timer = NewTimer();
            for (float t = 0f; t <= 100f; t += 5f)
            {
                ModeSwitchTick tick = timer.Tick(t, false, false, false);
                Assert.That(tick.Phase, Is.EqualTo(ModeSwitchPhase.Inactive));
                Assert.That(tick.SwitchNow, Is.False);
            }
        }

        [Test]
        public void EnteringSceneEditStartsAFreshWait()
        {
            var timer = NewTimer();
            timer.Tick(0f, false, false, false);
            timer.Tick(100f, false, false, false);

            Assert.That(Edit(timer, 100f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 109.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing), "the time spent in Interaction does not count");
            Assert.That(Edit(timer, 110f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
        }

        [Test]
        public void LeavingSceneEditDuringTheCountdownEndsItAndReEntryRestarts()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Assert.That(Edit(timer, 12f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
            Assert.That(timer.Tick(13f, false, false, false).Phase, Is.EqualTo(ModeSwitchPhase.Inactive));

            Assert.That(Edit(timer, 14f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 23.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
        }

        [Test]
        public void ASuspendedAssistNeverCountsAndResumesWithAFreshWait()
        {
            var timer = NewTimer();
            Edit(timer, 0f);
            Assert.That(Edit(timer, 12f, suspended: true).Phase, Is.EqualTo(ModeSwitchPhase.Inactive));
            Assert.That(Edit(timer, 500f, suspended: true).SwitchNow, Is.False);

            Assert.That(Edit(timer, 501f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 510.99f).Phase, Is.EqualTo(ModeSwitchPhase.Editing));
            Assert.That(Edit(timer, 511f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
        }

        [Test]
        public void ActivityBeforeTheFirstTickIsIgnoredAndTheFirstTickStartsTheWait()
        {
            var timer = NewTimer();
            timer.NotifyActivity(3f);
            Assert.That(Edit(timer, 50f).Phase, Is.EqualTo(ModeSwitchPhase.Editing), "the wait starts at the first tick in Scene Edit");
            Assert.That(Edit(timer, 60f).Phase, Is.EqualTo(ModeSwitchPhase.Countdown));
        }

        [Test]
        public void DurationsMustBePositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModeSwitchIdleTimer(0f, 5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModeSwitchIdleTimer(10f, 0f));
        }

        [Test]
        public void TheTimerHasNoUnityDependencyAndNoClockOfItsOwn()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "SpatialBlend/Interaction/Scripts/Core/ModeSwitchIdleTimer.cs"));
            Assert.That(source, Does.Not.Contain("using UnityEngine"));
            Assert.That(source, Does.Not.Contain("Time."));
        }
    }

    /// <summary>Which object carries the "Edit this object?" prompt, and when.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ModeSwitchHoverModelTests
    {
        private const int A = 11, B = 22;

        [Test]
        public void ThePromptAppearsOnHoverInInteractionMode()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            Assert.That(model.Tick(0f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
            Assert.That(model.Tick(0.1f, InteractionMode.Interaction, A, false, false), Is.EqualTo(A), "no dwell: it shows on hover");
        }

        [Test]
        public void WithTheOwnersDwellThePromptAppearsAtOneSecondNotBefore()
        {
            var model = new ModeSwitchHoverModel(0.4f, 1f);
            Assert.That(model.Tick(0f, InteractionMode.Interaction, A, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
            Assert.That(model.Tick(0.99f, InteractionMode.Interaction, A, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
            Assert.That(model.Tick(1f, InteractionMode.Interaction, A, false, false), Is.EqualTo(A));
        }

        [Test]
        public void ADwellInSceneEditNeverShows()
        {
            var model = new ModeSwitchHoverModel(0.4f, 1f);
            model.Tick(0f, InteractionMode.SceneEdit, A, false, false);
            Assert.That(model.Tick(2f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void ChangingObjectOrLeavingRestartsTheDwell()
        {
            var model = new ModeSwitchHoverModel(0.4f, 1f);
            model.Tick(0f, InteractionMode.Interaction, A, false, false);
            Assert.That(model.Tick(0.9f, InteractionMode.Interaction, B, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
            Assert.That(model.Tick(1.8f, InteractionMode.Interaction, B, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget), "0.9 s on B");
            Assert.That(model.Tick(1.9f, InteractionMode.Interaction, B, false, false), Is.EqualTo(B));

            var leaver = new ModeSwitchHoverModel(0.4f, 1f);
            leaver.Tick(0f, InteractionMode.Interaction, A, false, false);
            leaver.Tick(0.9f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false);
            leaver.Tick(1.0f, InteractionMode.Interaction, A, false, false);
            Assert.That(leaver.Tick(1.9f, InteractionMode.Interaction, A, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget), "back on A for 0.9 s");
            Assert.That(leaver.Tick(2.0f, InteractionMode.Interaction, A, false, false), Is.EqualTo(A));
        }

        [Test]
        public void AShownPromptStillLingersAfterTheRayLeavesWithTheDwellOn()
        {
            var model = new ModeSwitchHoverModel(0.4f, 1f);
            model.Tick(0f, InteractionMode.Interaction, A, false, false);
            model.Tick(1f, InteractionMode.Interaction, A, false, false);
            Assert.That(model.Tick(1.2f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(1.7f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void ThereIsNoPromptInSceneEditEvenOverAnEditableObject()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            Assert.That(model.Tick(0f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void LeavingInteractionModeDropsAShownPrompt()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            model.Tick(0f, InteractionMode.Interaction, A, false, false);
            Assert.That(model.Tick(0.1f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
            Assert.That(model.Tick(0.2f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget), "and it stays gone");
        }

        [Test]
        public void MovingBetweenObjectsRetargetsOnePromptWithoutAGap()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            model.Tick(0f, InteractionMode.Interaction, A, false, false);
            // The ray leaves A for a frame, then lands on B: the prompt never hides and ends on B.
            Assert.That(model.Tick(0.016f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(0.032f, InteractionMode.Interaction, B, false, false), Is.EqualTo(B));
            Assert.That(model.Tick(0.2f, InteractionMode.Interaction, B, false, false), Is.EqualTo(B));
        }

        [Test]
        public void ThePromptLingersWhileTheRayCrossesToTheButtonThenHides()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            model.Tick(0f, InteractionMode.Interaction, A, false, false);
            Assert.That(model.Tick(1f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(1.39f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(1.41f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void TheRayOnThePromptKeepsItAndOnlyLeavingItStartsTheLinger()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            model.Tick(0f, InteractionMode.Interaction, A, false, false);
            model.Tick(0.2f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false);
            for (float t = 0.5f; t < 30f; t += 0.5f)
                Assert.That(model.Tick(t, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, true, false), Is.EqualTo(A), "t=" + t);

            Assert.That(model.Tick(30f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(30.41f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void TheRayOnThePromptShowsNothingWhenThereIsNoTarget()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            Assert.That(model.Tick(0f, InteractionMode.Interaction, ModeSwitchHoverModel.NoTarget, true, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void SuspendedShowsNothingAndClearClearsAtOnce()
        {
            var model = new ModeSwitchHoverModel(0.4f);
            Assert.That(model.Tick(0f, InteractionMode.Interaction, A, false, true), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
            model.Tick(1f, InteractionMode.Interaction, A, false, false);
            model.Clear();
            Assert.That(model.Target, Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        // The hands' Menu button uses the same model with a longer linger (ModeSwitchAssist.DefaultHandMenuLingerSeconds).
        // The clock is the `now` argument: no Unity time.

        [Test]
        public void TheHandMenuStaysPastTheControllerLingerThenHides()
        {
            float linger = ModeSwitchAssist.DefaultHandMenuLingerSeconds;
            var model = ShownHandMenu();
            float shown = ModeSwitchAssist.HandMenuDwellSeconds;

            Assert.That(linger, Is.EqualTo(3.0f));

            // The ray has left the object and has not reached the button.
            Assert.That(model.Tick(shown, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(shown + 2.5f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A),
                "still there 2.5 s after the hand left; the controller Edit prompt hides at 0.4 s");
            // Just past 3 s: (shown + 3.0f) - shown rounds below 3.0f in single precision.
            Assert.That(model.Tick(shown + 3.0f + 1e-3f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void RehoveringTheObjectOrTheButtonCancelsTheHandMenuHide()
        {
            float linger = ModeSwitchAssist.DefaultHandMenuLingerSeconds;
            var model = ShownHandMenu();
            float shown = ModeSwitchAssist.HandMenuDwellSeconds;

            model.Tick(shown, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false);
            // Back on the object 2.5 s later, inside the 3 s window: the pending hide is dropped.
            Assert.That(model.Tick(shown + 2.5f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(shown + 2.5f + linger + 1f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(A),
                "still hovering, so the earlier leave does not hide it at 3 s");

            // Leave again. The linger runs from this leave, not the cancelled one.
            float left = shown + 2.5f + linger + 1f;
            model.Tick(left, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false);
            Assert.That(model.Tick(left + 2.5f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));

            // Landing on the button cancels that second hide, and staying on it never starts another.
            Assert.That(model.Tick(left + 2.5f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, true, false), Is.EqualTo(A));
            Assert.That(model.Tick(left + 3.0f + 30f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, true, false), Is.EqualTo(A));

            // Leaving the button is what starts the hide.
            float leftButton = left + 3.0f + 30f;
            model.Tick(leftButton, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false);
            Assert.That(model.Tick(leftButton + 2.5f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(leftButton + 3.0f, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(ModeSwitchHoverModel.NoTarget));
        }

        [Test]
        public void AOneFrameHoverDropDoesNotHideTheHandMenu()
        {
            float linger = ModeSwitchAssist.DefaultHandMenuLingerSeconds;
            var model = ShownHandMenu();
            float shown = ModeSwitchAssist.HandMenuDwellSeconds;
            const float frame = 1f / 72f;

            // A hand ray jitters off the object for a frame, then returns.
            Assert.That(model.Tick(shown + frame, InteractionMode.SceneEdit, ModeSwitchHoverModel.NoTarget, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(shown + frame * 2f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(A));
            Assert.That(model.Tick(shown + frame * 2f + linger + 1f, InteractionMode.SceneEdit, A, false, false), Is.EqualTo(A),
                "the return cancelled the hide, so the drop does not expire later");
        }

        private static ModeSwitchHoverModel ShownHandMenu()
        {
            var model = new ModeSwitchHoverModel(
                ModeSwitchAssist.DefaultHandMenuLingerSeconds,
                ModeSwitchAssist.HandMenuDwellSeconds,
                InteractionMode.SceneEdit);
            model.Tick(0f, InteractionMode.SceneEdit, A, false, false);
            Assert.That(
                model.Tick(ModeSwitchAssist.HandMenuDwellSeconds, InteractionMode.SceneEdit, A, false, false),
                Is.EqualTo(A),
                "the hand dwell has elapsed");
            return model;
        }
    }

    /// <summary>
    /// The pieces together: which objects count as editable, the prompt's mode rule, the widgets, the chip, the tutorial hook and the
    /// "never switch under work" rule, driven through <see cref="ModeSwitchAssist.Step"/> with a test clock.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ModeSwitchAssistContractTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private ModeSwitchAssist _assist;
        private readonly System.Collections.Generic.List<GameObject> _objects = new System.Collections.Generic.List<GameObject>();
        private readonly object _owner = new object();

        [SetUp]
        public void SetUp()
        {
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            // EditMode does not run Awake, and Awake is what publishes InteractionModeManager.Instance.
            Invoke(_mode, "Awake");
            _mode.SetMode(InteractionMode.SceneEdit);
            _assist = _modeHost.AddComponent<ModeSwitchAssist>();
            Invoke(_assist, "Awake");
            Invoke(_assist, "Start");
            // Most tests are about what the prompt does once shown; the dwell has its own tests.
            _assist.ConfigureHoverDwell(0f);
        }

        [TearDown]
        public void TearDown()
        {
            ModeSwitchAssist.Resume(_owner);
            foreach (var go in _objects)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            if (_modeHost != null) UnityEngine.Object.DestroyImmediate(_modeHost);
        }

        // ---- editable objects ----

        private RayInteractable NewInteractable(Transform parent, string name = "collider")
        {
            var go = new GameObject(name);
            _objects.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            var surface = go.AddComponent<ColliderSurface>();
            surface.InjectCollider(box);
            var interactable = go.AddComponent<RayInteractable>();
            interactable.InjectSurface(surface);
            return interactable;
        }

        private GameObject NewEntity()
        {
            var entity = new GameObject("entity");
            _objects.Add(entity);
            entity.AddComponent<SbLocalEntity>();
            return entity;
        }

        [Test]
        public void AnEntityWithAPlacementMotifGatedToSceneEditIsEditable()
        {
            var entity = NewEntity();
            entity.AddComponent<ObjectPlacementMotif>();
            var ray = NewInteractable(entity.transform);

            Assert.That(ModeSwitchAssist.TryResolveEditable(ray, out Transform root), Is.True);
            Assert.That(root, Is.SameAs(entity.transform));
        }

        [Test]
        public void APanelPlacementMotifCountsToo()
        {
            var entity = NewEntity();
            entity.AddComponent<PanelPlacementMotif>();
            Assert.That(ModeSwitchAssist.TryResolveEditable(NewInteractable(entity.transform), out _), Is.True);
        }

        [Test]
        public void AManipulationPanelMotifGatedToSceneEditIsEditableAndItsOwnerIsTheRoot()
        {
            var box = new GameObject("annotation");
            _objects.Add(box);
            box.AddComponent<ManipulationPanelMotif>();
            var ray = NewInteractable(box.transform);

            Assert.That(ModeSwitchAssist.TryResolveEditable(ray, out Transform root), Is.True);
            Assert.That(root, Is.SameAs(box.transform));
        }

        [Test]
        public void AnObjectWithoutMotifsIsNotEditable()
        {
            var plain = NewEntity();
            Assert.That(ModeSwitchAssist.TryResolveEditable(NewInteractable(plain.transform), out Transform root), Is.False);
            Assert.That(root, Is.Null);
            Assert.That(ModeSwitchAssist.TryResolveEditable(NewInteractable(null, "floor"), out _), Is.False, "a surface the ray can hit but nobody edits");
            Assert.That(ModeSwitchAssist.TryResolveEditable(null, out _), Is.False);
        }

        [Test]
        public void AMotifThatIsNotGatedToSceneEditDoesNotMakeAnObjectEditable()
        {
            var ungated = NewEntity();
            var motif = ungated.AddComponent<ObjectPlacementMotif>();
            SetField(motif, "_respectModeGate", false);
            Assert.That(ModeSwitchAssist.TryResolveEditable(NewInteractable(ungated.transform), out _), Is.False, "always-on placement is not something Scene Edit unlocks");

            var other = NewEntity();
            var interactionOnly = other.AddComponent<ObjectPlacementMotif>();
            SetField(interactionOnly, "_requiredMode", InteractionMode.Interaction);
            Assert.That(ModeSwitchAssist.TryResolveEditable(NewInteractable(other.transform), out _), Is.False);
        }

        [Test]
        public void TheHoverEditPromptIsOnlyForAnUnplacedSharedScan()
        {
            var board = NewEntity();
            board.AddComponent<PanelPlacementMotif>();
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.Interaction, NewInteractable(board.transform)), Is.False,
                "a placed scene object does not offer Edit");

            var own = NewEntity();
            own.AddComponent<ManipulationPanelMotif>();
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.Interaction, NewInteractable(own.transform)), Is.False,
                "the user's own scan already has a pose");

            var scan = NewEntity();
            var pending = scan.AddComponent<UnplacedSharedScan>();
            var scanRay = NewInteractable(scan.transform);
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.Interaction, scanRay), Is.True);
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.SceneEdit, scanRay), Is.False, "never in Scene Edit");
            pending.SpecifyPosition();
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.Interaction, scanRay), Is.False,
                "once the user places it the prompt retires");
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.Interaction, NewInteractable(NewEntity().transform)), Is.False);
            Assert.That(ModeSwitchAssist.ShouldPrompt(InteractionMode.Interaction, null), Is.False);
        }

        [Test]
        public void TheWidgetsOwnButtonsAreNeverEditableAndAreRecognisedAsWidgets()
        {
            var ui = _assist.Ui;
            foreach (var host in new[] { ui.PromptObject, ui.ToastObject, ui.ChipObject })
            {
                foreach (var target in host.GetComponentsInChildren<ModeSwitchClickTarget>(true))
                {
                    Assert.That(ModeSwitchUi.IsWidget(target.Interactable), Is.True, target.name);
                    Assert.That(ModeSwitchAssist.TryResolveEditable(target.Interactable, out _), Is.False, target.name);
                }
            }
            Assert.That(ModeSwitchUi.IsWidget(NewInteractable(null)), Is.False);
        }

        // ---- the idle return through the assist ----

        private float _now;

        private void StepTo(float now)
        {
            _now = now;
            _assist.Step(now);
        }

        [Test]
        public void TheToastShowsTheCountdownAtTenSecondsAndTheModeSwitchesAtFifteen()
        {
            StepTo(0f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            StepTo(9.9f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);

            StepTo(10f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
            Assert.That(_assist.Ui.ToastText, Is.EqualTo("Back to Interaction in 5…"));
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));

            StepTo(12.2f);
            Assert.That(_assist.Ui.ToastText, Is.EqualTo("Back to Interaction in 3…"));

            StepTo(15f);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
            Assert.That(_assist.Ui.ToastVisible, Is.False);
        }

        [Test]
        public void KeepEditingHidesTheToastAndRestartsTheWait()
        {
            StepTo(0f);
            StepTo(11f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
            _assist.Clock = () => 11f;
            Invoke(_assist, "OnKeepEditingClicked");
            StepTo(11.1f);

            Assert.That(_assist.Ui.ToastVisible, Is.False);
            StepTo(16f);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            StepTo(21f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
            StepTo(26f);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
        }

        [Test]
        public void NothingSwitchesWhileAnObjectMenuIsOpen()
        {
            var target = new GameObject("target");
            _objects.Add(target);
            var context = target.AddComponent<ManipulationContext>();
            SetField(context, "_isPanelShowing", true);
            AssertNeverSwitches(60f);
            SetField(context, "_isPanelShowing", false);
        }

        [Test]
        public void NothingSwitchesWhileTheScanPanelIsOpen()
        {
            var go = new GameObject("scan");
            _objects.Add(go);
            var panel = go.AddComponent<PassthroughPanelManager>();
            SetField(panel, "_isPanelOpen", true);
            AssertNeverSwitches(60f);
        }

        [Test]
        public void NothingSwitchesWhileABoxIsBeingDrawn()
        {
            var go = new GameObject("box");
            _objects.Add(go);
            var box = go.AddComponent<BoxAnnotationMotif>();
            SetBoxMode(box, "Create");
            AssertNeverSwitches(60f);
            SetBoxMode(box, "DragPoint");
            AssertNeverSwitches(120f, 60f);
        }

        [Test]
        public void NothingSwitchesUnderARegisteredBusyProbe()
        {
            Func<bool> probe = () => true;
            ModeSwitchAssist.AddBusyProbe(probe);
            try { AssertNeverSwitches(60f); }
            finally { ModeSwitchAssist.RemoveBusyProbe(probe); }
        }

        [Test]
        public void TheBoxBecomingIdleStartsAFreshWait()
        {
            var go = new GameObject("box");
            _objects.Add(go);
            var box = go.AddComponent<BoxAnnotationMotif>();
            SetBoxMode(box, "Create");
            AssertNeverSwitches(30f);
            SetBoxMode(box, "Idle");

            StepTo(31f);
            Assert.That(_assist.Ui.ToastVisible, Is.False, "the wait restarts when the work ends");
            StepTo(40.2f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            StepTo(41f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
        }

        private void AssertNeverSwitches(float untilSeconds, float fromSeconds = 0f)
        {
            for (float t = fromSeconds; t <= untilSeconds; t += 1f)
            {
                // The scan for open work runs four times a second; step past it.
                StepTo(t + 0.3f);
                Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit), "t=" + t);
                Assert.That(_assist.Ui.ToastVisible, Is.False, "t=" + t);
            }
        }

        [Test]
        public void ASuspendedAssistShowsNothingAndNeverSwitchesAndResumeStartsAFreshWait()
        {
            StepTo(0f);
            ModeSwitchAssist.Suspend(_owner);
            Assert.That(ModeSwitchAssist.IsSuspended, Is.True);
            StepTo(12f);
            StepTo(100f);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            Assert.That(_assist.Ui.ChipVisible, Is.False, "the tutorial owns the screen");
            Assert.That(_assist.Ui.PromptVisible, Is.False);

            ModeSwitchAssist.Resume(_owner);
            Assert.That(ModeSwitchAssist.IsSuspended, Is.False);
            StepTo(101f);
            Assert.That(_assist.Ui.ChipVisible, Is.True);
            StepTo(110f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            StepTo(111f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
        }

        [Test]
        public void SuspendedByTwoOwnersResumesWhenTheLastOneResumes()
        {
            var other = new object();
            ModeSwitchAssist.Suspend(_owner);
            ModeSwitchAssist.Suspend(other);
            ModeSwitchAssist.Resume(_owner);
            Assert.That(ModeSwitchAssist.IsSuspended, Is.True);
            ModeSwitchAssist.Resume(other);
            Assert.That(ModeSwitchAssist.IsSuspended, Is.False);
            Assert.Throws<ArgumentNullException>(() => ModeSwitchAssist.Suspend(null));
        }

        [Test]
        public void AnInactiveAssistShowsNothing()
        {
            _assist.SetActive(false);
            StepTo(0f);
            StepTo(100f);
            Assert.That(_assist.Ui.ChipVisible, Is.False);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            _assist.SetActive(true);
            StepTo(101f);
            Assert.That(_assist.Ui.ChipVisible, Is.True);
        }


        // ---- the hover prompt, driven through Step with a fake ray ----

        private RayInteractable NewEditable(out GameObject entity)
        {
            entity = NewEntity();
            entity.AddComponent<ObjectPlacementMotif>();
            entity.AddComponent<UnplacedSharedScan>();
            return NewInteractable(entity.transform);
        }

        private RayInteractable EditButton() => _assist.Ui.PromptObject.GetComponentInChildren<ModeSwitchClickTarget>(true).Interactable;

        [Test]
        public void HoveringAnEditableObjectInInteractionModeShowsOnePromptAboveIt()
        {
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable ray = NewEditable(out GameObject entity);
            _assist.HoveredSource = () => ray;

            StepTo(0f);

            Assert.That(_assist.Ui.PromptVisible, Is.True, "no dwell: it shows on hover");
            Assert.That(_assist.Ui.PromptRoot, Is.SameAs(entity.transform));
        }

        [Test]
        public void TheOwnersDwellIsTheDefaultSetting()
        {
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(settings.modeSwitchHoverDwellSeconds, Is.EqualTo(1f));
                Assert.That(SpatialBlendSettings.DefaultModeSwitchHoverDwellSeconds, Is.EqualTo(1f));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void ThePromptShowsOnlyAfterTheRayStaysOneSecondOnTheSameObject()
        {
            _assist.ConfigureHoverDwell(1f);
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable ray = NewEditable(out GameObject entity);
            _assist.HoveredSource = () => ray;

            StepTo(0f);
            StepTo(0.99f);
            Assert.That(_assist.Ui.PromptVisible, Is.False);
            StepTo(1f);
            Assert.That(_assist.Ui.PromptVisible, Is.True);
            Assert.That(_assist.Ui.PromptRoot, Is.SameAs(entity.transform));
        }

        [Test]
        public void SwitchingObjectsRestartsTheDwellAndLeavingEarlyShowsNothing()
        {
            _assist.ConfigureHoverDwell(1f);
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable a = NewEditable(out _);
            RayInteractable b = NewEditable(out GameObject entityB);
            _assist.HoveredSource = () => a;
            StepTo(0f);
            StepTo(0.9f);
            _assist.HoveredSource = () => b;
            StepTo(0.95f);
            StepTo(1.5f);
            Assert.That(_assist.Ui.PromptVisible, Is.False, "B has been under the ray 0.55 s, not 1 s");
            StepTo(1.95f);
            Assert.That(_assist.Ui.PromptVisible, Is.True);
            Assert.That(_assist.Ui.PromptRoot, Is.SameAs(entityB.transform));

            _assist.HoveredSource = () => null;
            StepTo(2f);
            _assist.HoveredSource = () => a;
            StepTo(2.1f);
            _assist.HoveredSource = () => null;
            StepTo(3.0f);
            _assist.HoveredSource = () => a;
            StepTo(3.1f);
            StepTo(3.5f);
            Assert.That(_assist.Ui.PromptVisible, Is.False, "leaving and returning restarts the dwell");
        }

        [Test]
        public void TheDwellNeverShowsAPromptInSceneEdit()
        {
            _assist.ConfigureHoverDwell(1f);
            RayInteractable ray = NewEditable(out _);
            _assist.HoveredSource = () => ray;
            StepTo(0f);
            StepTo(1f);
            StepTo(5f);
            Assert.That(_assist.Ui.PromptVisible, Is.False);
        }

        [Test]
        public void HoveringNeverPromptsInSceneEditOrOnAnObjectThatCannotBeEdited()
        {
            RayInteractable editable = NewEditable(out _);
            RayInteractable plain = NewInteractable(NewEntity().transform);

            _assist.HoveredSource = () => editable;
            StepTo(0f);
            Assert.That(_assist.Ui.PromptVisible, Is.False, "Scene Edit");

            _mode.SetMode(InteractionMode.Interaction);
            _assist.HoveredSource = () => plain;
            StepTo(1f);
            Assert.That(_assist.Ui.PromptVisible, Is.False, "not editable");
        }

        [Test]
        public void MovingTheRayToAnotherEditableObjectReAnchorsTheSamePrompt()
        {
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable a = NewEditable(out GameObject entityA);
            RayInteractable b = NewEditable(out GameObject entityB);
            _assist.HoveredSource = () => a;
            StepTo(0f);
            GameObject prompt = _assist.Ui.PromptObject;
            Assert.That(_assist.Ui.PromptRoot, Is.SameAs(entityA.transform));

            _assist.HoveredSource = () => b;
            StepTo(0.016f);

            Assert.That(_assist.Ui.PromptVisible, Is.True);
            Assert.That(_assist.Ui.PromptRoot, Is.SameAs(entityB.transform));
            Assert.That(_assist.Ui.PromptObject, Is.SameAs(prompt), "one prompt instance");
        }

        [Test]
        public void ANewObjectDoesNotGetThePromptBeforeItsOwnDwellAndNothingFlickers()
        {
            _assist.ConfigureHoverDwell(1f);
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable a = NewEditable(out GameObject entityA);
            RayInteractable b = NewEditable(out GameObject entityB);
            _assist.HoveredSource = () => a;
            StepTo(0f);
            StepTo(1f);
            Assert.That(_assist.Ui.PromptRoot, Is.SameAs(entityA.transform));

            _assist.HoveredSource = () => b;
            var seen = new System.Collections.Generic.List<string>();
            for (float t = 1.1f; t <= 3f; t += 0.1f)
            {
                StepTo(t);
                Transform root = _assist.Ui.PromptRoot;
                string where = root == null ? "none" : root == entityA.transform ? "A" : "B";
                if (seen.Count == 0 || seen[seen.Count - 1] != where) seen.Add(where);
                if (where == "B") Assert.That(t - 1.1f, Is.GreaterThanOrEqualTo(0.9f), "B shows only after about 1 s on B, t=" + t);
            }
            Assert.That(string.Join(",", seen), Is.EqualTo("A,none,B"), "the old prompt lingers out, then the new one dwells in: no A,B,none,B flicker");
        }

        [Test]
        public void AssistStartedInactiveBuildsAndThemesItsWidgetsOnlyWhenActivated()
        {
            UnityEngine.Object.DestroyImmediate(_assist);
            var host = new GameObject("late");
            _objects.Add(host);
            var late = host.AddComponent<ModeSwitchAssist>();
            SetField(late, "_startActive", false);
            Invoke(late, "Awake");
            Invoke(late, "Start");
            Assert.That(late.IsActive, Is.False);
            Assert.That(late.Ui, Is.Null, "nothing is built before the session is ready");
            late.Step(0f);
            Assert.That(late.Ui, Is.Null);

            late.SetActive(true);
            late.Step(1f);
            Assert.That(late.Ui, Is.Not.Null);
            Assert.That(late.Ui.ChipVisible, Is.True);
            Assert.That(late.ReplacesModePanel, Is.True);
        }

        [Test]
        public void ThePromptHidesWhenTheHoverEndsAfterItsLinger()
        {
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable ray = NewEditable(out _);
            _assist.HoveredSource = () => ray;
            StepTo(0f);

            _assist.HoveredSource = () => null;
            StepTo(0.1f);
            Assert.That(_assist.Ui.PromptVisible, Is.True, "still there while the ray crosses to the button");
            StepTo(0.6f);
            Assert.That(_assist.Ui.PromptVisible, Is.False);
        }

        [Test]
        public void TheRayOnThePromptsOwnButtonKeepsItButTheChipDoesNot()
        {
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable ray = NewEditable(out _);
            _assist.HoveredSource = () => ray;
            StepTo(0f);

            _assist.HoveredSource = () => EditButton();
            for (float t = 0.5f; t < 5f; t += 0.5f)
            {
                StepTo(t);
                Assert.That(_assist.Ui.PromptVisible, Is.True, "t=" + t);
            }

            RayInteractable chip = _assist.Ui.ChipObject.GetComponent<ModeSwitchClickTarget>().Interactable;
            _assist.HoveredSource = () => chip;
            StepTo(5f);
            StepTo(5.6f);
            Assert.That(_assist.Ui.PromptVisible, Is.False, "resting on the chip is not resting on the prompt");
        }

        [Test]
        public void WorkingAnObjectWithTheTriggerInInteractionModeHidesThePromptButPressingItsButtonDoesNot()
        {
            _mode.SetMode(InteractionMode.Interaction);
            RayInteractable ray = NewEditable(out _);
            _assist.HoveredSource = () => ray;
            StepTo(0f);
            Assert.That(_assist.Ui.PromptVisible, Is.True);

            _assist.SelectedSource = () => ray;
            StepTo(0.1f);
            Assert.That(_assist.Ui.PromptVisible, Is.False, "the user is playing with the object");

            _assist.SelectedSource = () => null;
            _assist.HoveredSource = () => ray;
            StepTo(0.2f);
            Assert.That(_assist.Ui.PromptVisible, Is.True);

            _assist.HoveredSource = () => EditButton();
            _assist.SelectedSource = () => EditButton();
            StepTo(0.3f);
            Assert.That(_assist.Ui.PromptVisible, Is.True, "the press that is about to click [Edit] must not remove it");
        }

        // ---- editing activity and busy work, driven through Step with a fake ray ----

        [Test]
        public void AGrabHoldsTheTimerForAsLongAsItLastsAndTheWaitRestartsAtTheRelease()
        {
            RayInteractable ray = NewEditable(out _);
            StepTo(0f);
            _assist.SelectedSource = () => ray;
            for (float t = 1f; t <= 60f; t += 1f)
            {
                StepTo(t);
                Assert.That(_assist.Ui.ToastVisible, Is.False, "t=" + t);
                Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit), "t=" + t);
            }

            _assist.SelectedSource = () => null;
            StepTo(61f);
            StepTo(70.9f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            StepTo(71f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
        }

        [Test]
        public void ASelectOrUnselectOnAnEditableObjectRestartsTheWait()
        {
            RayInteractable ray = NewEditable(out _);
            StepTo(0f);
            _assist.SelectedSource = () => ray;
            StepTo(8f);
            _assist.SelectedSource = () => null;
            StepTo(9f);

            StepTo(18.5f);
            Assert.That(_assist.Ui.ToastVisible, Is.False, "ten seconds after the unselect, not after the select");
            StepTo(19f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
        }

        [Test]
        public void SelectingSomethingThatCannotBeEditedOnlyHoldsTheTimerWhileItIsSelected()
        {
            RayInteractable plain = NewInteractable(NewEntity().transform);
            StepTo(0f);
            _assist.SelectedSource = () => plain;
            StepTo(2f);
            _assist.SelectedSource = () => null;
            StepTo(3f);

            StepTo(11.9f);
            Assert.That(_assist.Ui.ToastVisible, Is.False, "held until the select ended at 2 s");
            StepTo(12f);
            Assert.That(_assist.Ui.ToastVisible, Is.True, "and no restart at the unselect itself");
        }

        [Test]
        public void TheAButtonIsEditingActivity()
        {
            StepTo(0f);
            StepTo(8f);
            _assist.Clock = () => 8f;
            Invoke(_assist, "OnObjectMenuButton");

            StepTo(10f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            StepTo(18f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
        }

        [Test]
        public void AnObjectMenuOpenedInTheLastInstantsOfTheCountdownStopsTheSwitch()
        {
            var target = new GameObject("target");
            _objects.Add(target);
            var context = target.AddComponent<ManipulationContext>();
            StepTo(0f);
            StepTo(10f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
            StepTo(14.85f);

            SetField(context, "_isPanelShowing", true);
            StepTo(14.99f);
            StepTo(15f);
            StepTo(15.2f);

            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit), "the menu opened 0.14 s before the deadline");
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            SetField(context, "_isPanelShowing", false);
        }

        [Test]
        public void ADeadlinePassedWhileAwayIsAnnouncedBeforeItSwitches()
        {
            StepTo(0f);
            StepTo(100f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            StepTo(105f);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
        }

        [Test]
        public void TheTimerIsHeldWhileTheUserIsOutOfTheApp()
        {
            StepTo(0f);
            _assist.HasInputFocus = () => false;
            StepTo(30f);
            StepTo(60f);
            Assert.That(_assist.Ui.ToastVisible, Is.False);
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));

            _assist.HasInputFocus = () => true;
            StepTo(61f);
            StepTo(69.9f);
            Assert.That(_assist.Ui.ToastVisible, Is.False, "the wait restarts from the last moment the user was away");
            StepTo(70f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
        }

        [Test]
        public void AnOwnerThatWasDestroyedWithoutResumingNoLongerSuspends()
        {
            var tutorial = new GameObject("tutorial");
            ModeSwitchAssist.Suspend(tutorial);
            Assert.That(ModeSwitchAssist.IsSuspended, Is.True);
            UnityEngine.Object.DestroyImmediate(tutorial);
            Assert.That(ModeSwitchAssist.IsSuspended, Is.False);
        }

        [Test]
        public void TheControlPanelStartsAndStopsTheAssistWithTheSession()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "SpatialBlend/Samples/Scripts/UI/ControlPanel.cs"));
            int ready = source.IndexOf("void EnterReadySessionUi()", StringComparison.Ordinal);
            int retry = source.IndexOf("void ShowRetryUi()", StringComparison.Ordinal);
            Assert.That(ready, Is.GreaterThan(0));
            Assert.That(retry, Is.GreaterThan(0));
            Assert.That(source.Substring(ready, 1800), Does.Contain("modeSwitch.SetActive(true)").And.Contain("ModeSwitchAssist"));
            Assert.That(source.Substring(retry, 600), Does.Contain("modeSwitchOff.SetActive(false)").And.Contain("ModeSwitchAssist"));
        }

        // ---- the chip ----

        [Test]
        public void TheChipNamesTheModeAndTappingItSwitches()
        {
            StepTo(0f);
            Assert.That(_assist.Ui.ChipVisible, Is.True);
            Assert.That(_assist.Ui.ChipText, Is.EqualTo("Editing"));

            Tap(_assist.Ui.ChipObject.GetComponent<ModeSwitchClickTarget>());
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
            StepTo(1f);
            Assert.That(_assist.Ui.ChipText, Is.EqualTo("Interaction"));

            Tap(_assist.Ui.ChipObject.GetComponent<ModeSwitchClickTarget>());
            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            StepTo(2f);
            Assert.That(_assist.Ui.ChipText, Is.EqualTo("Editing"));
        }

        [Test]
        public void ThePromptEditButtonSwitchesToSceneEditAndHidesThePrompt()
        {
            _mode.SetMode(InteractionMode.Interaction);
            var entity = NewEntity();
            entity.AddComponent<ObjectPlacementMotif>();
            entity.AddComponent<BoxCollider>();
            _assist.Ui.ShowPrompt(entity.transform, new Bounds(entity.transform.position, Vector3.one), 0.15f);
            Assert.That(_assist.Ui.PromptVisible, Is.True);

            Tap(_assist.Ui.PromptObject.GetComponentInChildren<ModeSwitchClickTarget>());

            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            Assert.That(_assist.Ui.PromptVisible, Is.False);
        }

        [Test]
        public void APressThatLeavesTheButtonIsNotAClick()
        {
            int clicks = 0;
            var chip = _assist.Ui.ChipObject.GetComponent<ModeSwitchClickTarget>();
            chip.Clicked += () => clicks++;
            Invoke(chip, "OnStateChanged", new InteractableStateChangeArgs(InteractableState.Hover, InteractableState.Select));
            Invoke(chip, "OnStateChanged", new InteractableStateChangeArgs(InteractableState.Select, InteractableState.Normal));
            Assert.That(clicks, Is.EqualTo(0));
            Tap(chip);
            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void TheWidgetsTextMeetsTheRoomReadingSize()
        {
            // The prompt is on the Room surface: its 24 unit label is 20 dmm (Meta's floor) from 1.2 m out to 4.5 m,
            // larger inside 1.2 m.
            var room = SpatialBlend.Samples.FreeformCollaboration.UI.UiMetrics.Room;
            foreach (float d in new[] { 1f, 2f, 4.5f })
                Assert.That(room.Dmm(SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.FontLabel, d), Is.GreaterThanOrEqualTo(SpatialBlend.Samples.FreeformCollaboration.UI.UiMetrics.MinBodyDmm - 0.01f), "at " + d + " m");
            foreach (var host in new[] { _assist.Ui.PromptObject, _assist.Ui.ToastObject, _assist.Ui.ChipObject })
                foreach (var label in host.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
                    Assert.That(label.fontSize, Is.EqualTo(SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.FontLabel), label.name);
        }

        [Test]
        public void TheFreeformAppThemesTheWidgetsWithUiThemeAndPretendard()
        {
            SpatialBlend.Samples.FreeformCollaboration.UI.FreeformModeSwitchTheme.Install();
            try
            {
                var palette = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette;
                ModeSwitchStyle style = ModeSwitchStyle.Current;
                Assert.That(style.Accent, Is.EqualTo(palette.Accent));
                Assert.That(style.OnAccent, Is.EqualTo(palette.OnAccent));
                Assert.That(style.Text, Is.EqualTo(palette.Text));
                Assert.That(style.Surface, Is.EqualTo(palette.PanelTop));
                Assert.That(style.SemiBold, Is.Not.Null, "the Pretendard SemiBold TMP font asset");
                Assert.That(style.SemiBold, Is.SameAs(SpatialBlend.Samples.FreeformCollaboration.UI.UiFonts.SemiBold));
            }
            finally { ModeSwitchStyle.Resolve = null; }
            Assert.That(ModeSwitchStyle.Current.Accent, Is.EqualTo(ModeSwitchStyle.Graphite.Accent), "no theme: the default Graphite tokens");
        }

        [Test]
        public void ThemedWidgetsUseThePretendardFont()
        {
            SpatialBlend.Samples.FreeformCollaboration.UI.FreeformModeSwitchTheme.Install();
            GameObject host = null;
            try
            {
                host = new GameObject("themed");
                var ui = host.AddComponent<ModeSwitchUi>();
                ui.Build(null);
                foreach (var label in ui.ChipObject.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
                    Assert.That(label.font, Is.SameAs(SpatialBlend.Samples.FreeformCollaboration.UI.UiFonts.SemiBold), label.name);
            }
            finally
            {
                ModeSwitchStyle.Resolve = null;
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void TheChipAndToastStayOutOfTheWristPanelAndTheLowRayPath()
        {
            // The HUD anchor is HudDropMetres below the eye; the wrist panel is held low (around 0.3 m below the eye) on the left.
            float chipHeightFromEye = ModeSwitchUi.ChipRiseMetres - ModeSwitchUi.HudDropMetres;
            float toastHeightFromEye = ModeSwitchUi.ToastRiseMetres - ModeSwitchUi.HudDropMetres;
            Assert.That(chipHeightFromEye, Is.GreaterThan(0f), "the chip is above the eye line");
            Assert.That(toastHeightFromEye, Is.GreaterThan(-0.15f), "the toast is above where the wrist panel is held");
            Assert.That(ModeSwitchUi.ChipOffsetMetres, Is.GreaterThan(0f), "and to the left");
        }

        [Test]
        public void TheChipReplacesTheModePanelsRowWhenPresent()
        {
            Assert.That(_assist.ReplacesModePanel, Is.True);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "SpatialBlend/Samples/Scripts/UI/ControlPanel.cs"));
            int ready = source.IndexOf("void EnterReadySessionUi()", StringComparison.Ordinal);
            Assert.That(source.Substring(ready, 1600), Does.Contain("ReplacesModePanel").And.Contain("SetActive(!chipReplacesPanel)"));
        }

        [Test]
        public void TheFreeformSceneEnablesTheAssist()
        {
            string scene = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "SpatialBlend/Samples/Scenes/FreeformCollaboration.unity"));
            string meta = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "SpatialBlend/Interaction/Scripts/Core/ModeSwitchAssist.cs.meta"));
            string guid = System.Text.RegularExpressions.Regex.Match(meta, "guid: ([0-9a-f]{32})").Groups[1].Value;
            Assert.That(guid, Has.Length.EqualTo(32));
            Assert.That(scene, Does.Contain("guid: " + guid), "the scene carries a ModeSwitchAssist");
            var block = System.Text.RegularExpressions.Regex.Match(scene, "guid: " + guid + @"[^\n]*\n(?:[^\n]*\n){0,12}?\s*_startActive: (\d)");
            Assert.That(block.Success, Is.True, "its _startActive is serialized");
            Assert.That(block.Groups[1].Value, Is.EqualTo("0"), "off until the session is ready (ControlPanel starts it)");
            Assert.That(scene, Does.Contain("m_GameObject: {fileID: 2066952689}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: 11500000, guid: " + guid), "on the InteractionSystem object");
        }

        // ---- helpers ----

        private static void Tap(ModeSwitchClickTarget target)
        {
            Invoke(target, "OnStateChanged", new InteractableStateChangeArgs(InteractableState.Hover, InteractableState.Select));
            Invoke(target, "OnStateChanged", new InteractableStateChangeArgs(InteractableState.Select, InteractableState.Hover));
        }

        private static void SetBoxMode(BoxAnnotationMotif box, string name)
        {
            FieldInfo field = typeof(BoxAnnotationMotif).GetField("_boxMode", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(box, Enum.Parse(field.FieldType, name));
        }

        private static void SetField(object target, string name, object value)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) { field.SetValue(target, value); return; }
            }
            Assert.Fail("no field " + name);
        }

        private static void Invoke(object target, string name, params object[] args)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (method != null) { method.Invoke(target, args); return; }
            }
            Assert.Fail("no method " + name);
        }
    }
}
#endif
