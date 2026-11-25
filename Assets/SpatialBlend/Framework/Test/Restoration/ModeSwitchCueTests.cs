// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.UI;
using UnityEngine;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Every mode change while the assist is live gets a right-controller pulse, a chime and a caption that says what the mode is for.
    /// Entering Editing without the right controller says that its tools need one. Driven through <see cref="ModeSwitchAssist.Step"/>
    /// with a test clock; the pulse, the chime and the controller probe are replaced, so nothing here needs a headset or an audio device.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ModeSwitchCueTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private ModeSwitchAssist _assist;
        private readonly List<int> _pulses = new List<int>();
        private readonly List<bool> _chimes = new List<bool>();
        private bool _usesHands;
        private readonly List<string> _only = new List<string>();
        private readonly object _owner = new object();

        [SetUp]
        public void SetUp()
        {
            // NUnit runs every test on one fixture instance: per-test state starts here, not in field initializers.
            _usesHands = false;
            _only.Clear();
            _pulses.Clear();
            _chimes.Clear();
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            Invoke(_mode, "Awake");
            _mode.SetMode(InteractionMode.Interaction);
            _assist = _modeHost.AddComponent<ModeSwitchAssist>();
            Invoke(_assist, "Awake");
            Invoke(_assist, "Start");
            _assist.Haptic = pulses => _pulses.Add(pulses);
            _assist.Chime = rising => _chimes.Add(rising);
            _assist.UsesHands = () => _usesHands;
            _assist.ControllerOnlyInPlay = () => new List<string>(_only);
            _assist.Step(0f); // the baseline: the mode the assist started in is not a change
        }

        [TearDown]
        public void TearDown()
        {
            ModeSwitchAssist.Resume(_owner);
            if (_assist != null) Invoke(_assist, "OnDestroy");
            if (_modeHost != null) Object.DestroyImmediate(_modeHost);
            _pulses.Clear();
            _chimes.Clear();
        }

        [Test]
        public void TheModeTheAssistStartsInIsNotCued()
        {
            _assist.Step(0.5f);
            Assert.That(_assist.CueCount, Is.Zero);
            Assert.That(_assist.Ui.CaptionVisible, Is.False);
            Assert.That(_pulses, Is.Empty);
            Assert.That(_chimes, Is.Empty);
        }

        [Test]
        public void TheChipIntoEditingPulsesOnceChimesUpAndCaptionsWhatEditingIsFor()
        {
            Invoke(_assist, "OnChipClicked");
            _assist.Step(1f);

            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            Assert.That(_pulses, Is.EqualTo(new[] { 1 }));
            Assert.That(_chimes, Is.EqualTo(new[] { true }), "rising into Editing");
            Assert.That(_assist.Ui.CaptionVisible, Is.True);
            Assert.That(_assist.Ui.CaptionText, Is.EqualTo(ModeSwitchAssist.EditingCaption));
            StringAssert.Contains("capture real objects", _assist.Ui.CaptionText);
            StringAssert.Contains("Tap the chip when done", _assist.Ui.CaptionText);
        }

        [Test]
        public void TheEditButtonIsCuedLikeTheChip()
        {
            Invoke(_assist, "OnEditClicked");
            _assist.Step(1f);
            Assert.That(_assist.CueCount, Is.EqualTo(1));
            Assert.That(_assist.LastCueCaption, Is.EqualTo(ModeSwitchAssist.EditingCaption));
        }

        [Test]
        public void AChangeFromOutsideTheAssistIsCuedToo()
        {
            // The control panel's toggle, a tutorial task that keeps the assist live: whoever sets the mode, the user hears and reads it.
            _mode.SetMode(InteractionMode.SceneEdit);
            _assist.Step(1f);
            _mode.SetMode(InteractionMode.Interaction);
            _assist.Step(2f);

            Assert.That(_assist.CueCount, Is.EqualTo(2));
            Assert.That(_chimes, Is.EqualTo(new[] { true, false }), "rising in, falling out");
            Assert.That(_assist.Ui.CaptionText, Is.EqualTo(ModeSwitchAssist.InteractionCaption));
        }

        [Test]
        public void TheIdleReturnPulsesTwiceAndSaysWhy()
        {
            _mode.SetMode(InteractionMode.SceneEdit);
            _assist.Step(1f);
            _pulses.Clear();
            _chimes.Clear();

            // 10 s idle, then the 5 s countdown: the switch lands at 16 s; the next step cues it.
            for (float t = 1.5f; t <= 16.5f; t += 0.5f) _assist.Step(t);

            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.Interaction));
            Assert.That(_pulses, Is.EqualTo(new[] { 2 }), "a double pulse: the user did not make this change");
            Assert.That(_chimes, Is.EqualTo(new[] { false }));
            Assert.That(_assist.Ui.CaptionText, Is.EqualTo(ModeSwitchAssist.IdleReturnCaption));
        }

        [Test]
        public void TheCaptionHidesAfterThreeSeconds()
        {
            Invoke(_assist, "OnChipClicked");
            _assist.Step(1f);
            _assist.Step(3.9f);
            Assert.That(_assist.Ui.CaptionVisible, Is.True);
            _assist.Step(4f);
            Assert.That(_assist.Ui.CaptionVisible, Is.False);
        }

        [Test]
        public void EditingOnHandsSaysHowAHandMovesAndTurnsThingsForFiveSeconds()
        {
            _usesHands = true;
            Invoke(_assist, "OnChipClicked");
            _assist.Step(1f);

            Assert.That(_mode.CurrentMode, Is.EqualTo(InteractionMode.SceneEdit));
            Assert.That(_assist.Ui.CaptionText, Is.EqualTo(ModeSwitchAssist.HandsEditingCaption));
            StringAssert.Contains("pinch", _assist.Ui.CaptionText);
            StringAssert.DoesNotContain("needs the right controller", _assist.Ui.CaptionText, "no such notice: every Editing tool has a hand equivalent");
            _assist.Step(5.9f);
            Assert.That(_assist.Ui.CaptionVisible, Is.True);
            _assist.Step(6f);
            Assert.That(_assist.Ui.CaptionVisible, Is.False);
        }

        [Test]
        public void TheControllerNoticeAppearsOnlyForAFeatureThatStillNeedsOne()
        {
            // Nothing needs a controller today: no notice on hands, and no notice on controllers either.
            Assert.That(EditInput.ControllerOnlyFeatures.InPlay(), Is.Empty);
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.SceneEdit, false, true, true, new List<string>()), Is.EqualTo(ModeSwitchAssist.HandsEditingCaption));

            // A feature that does: hands are told which, and only hands, and only for Editing.
            var one = new List<string> { "a made-up feature" };
            string notice = ModeSwitchAssist.CaptionFor(InteractionMode.SceneEdit, false, true, true, one);
            StringAssert.Contains("a made-up feature", notice);
            StringAssert.Contains("needs a controller", notice);
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.SceneEdit, false, false, true, one), Is.EqualTo(ModeSwitchAssist.EditingCaption), "controller users are not told to pick one up");
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.Interaction, false, true, true, one), Is.EqualTo(ModeSwitchAssist.InteractionCaption));

            // Through the assist, with the registry's own answer.
            _usesHands = true;
            _only.Add("a made-up feature");
            Invoke(_assist, "OnChipClicked");
            _assist.Step(1f);
            StringAssert.Contains("a made-up feature", _assist.Ui.CaptionText);
        }

        [Test]
        public void TheRegistryReportsOnlyFeaturesInPlay()
        {
            bool inPlay = false;
            EditInput.ControllerOnlyFeatures.Register("a made-up feature", () => inPlay);
            try
            {
                Assert.That(EditInput.ControllerOnlyFeatures.InPlay(), Is.Empty);
                inPlay = true;
                Assert.That(EditInput.ControllerOnlyFeatures.InPlay(), Is.EqualTo(new[] { "a made-up feature" }));
            }
            finally { EditInput.ControllerOnlyFeatures.Clear(); }
        }

        [Test]
        public void TheCaptionIsOnlyForEditing()
        {
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.SceneEdit, false, false), Is.EqualTo(ModeSwitchAssist.EditingCaption));
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.SceneEdit, false, true), Is.EqualTo(ModeSwitchAssist.HandsEditingCaption));
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.Interaction, false, true), Is.EqualTo(ModeSwitchAssist.InteractionCaption));
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.Interaction, true, true), Is.EqualTo(ModeSwitchAssist.IdleReturnCaption));
        }

        [Test]
        public void ASuspendedAssistCuesNothingAndDoesNotReplayTheChangeOnResume()
        {
            ModeSwitchAssist.Suspend(_owner);
            _mode.SetMode(InteractionMode.SceneEdit);
            _assist.Step(1f);
            Assert.That(_assist.CueCount, Is.Zero, "the tutorial narrates its own changes");

            ModeSwitchAssist.Resume(_owner);
            _assist.Step(2f);
            Assert.That(_assist.CueCount, Is.Zero, "a change made while suspended is not announced late");
            Assert.That(_pulses, Is.Empty);
        }

        [Test]
        public void AModeSetWhileTheAssistWasOffIsNotCuedWhenItTurnsOn()
        {
            // The session start: ControlPanel sets Interaction, then activates the assist.
            _assist.SetActive(false);
            _mode.SetMode(InteractionMode.SceneEdit);
            _assist.Step(1f);
            _mode.SetMode(InteractionMode.Interaction);
            _assist.SetActive(true);
            _assist.Step(2f);
            _assist.Step(2.5f);
            Assert.That(_assist.CueCount, Is.Zero);
        }

        [Test]
        public void ACaptionUpWhenASuspensionBeginsRunsOutItsTime()
        {
            // The tutorial suspends the assist the frame the user reaches Editing; that is when the caption matters.
            Invoke(_assist, "OnChipClicked");
            _assist.Step(1f);
            ModeSwitchAssist.Suspend(_owner);
            _assist.Step(1.1f);
            Assert.That(_assist.Ui.CaptionVisible, Is.True);
            _assist.Step(4f);
            Assert.That(_assist.Ui.CaptionVisible, Is.False);
        }

        [Test]
        public void AChangeTheUserMadeIsCuedEvenWhenTheTutorialSuspendsTheAssistLaterInThatFrame()
        {
            // Task 3.1 in Interaction: the assist is live for the chip; the tutorial suspends it as soon as it sees Editing, possibly before
            // the assist's next step. The change happened while the assist was live, so it is the user's and is cued.
            Invoke(_assist, "Step", 0.5f); // subscribes to the mode manager
            Invoke(_assist, "OnChipClicked");
            ModeSwitchAssist.Suspend(_owner);
            _assist.Step(1f);

            Assert.That(_assist.CueCount, Is.EqualTo(1));
            Assert.That(_pulses, Is.EqualTo(new[] { 1 }));
            Assert.That(_assist.LastCueCaption, Is.EqualTo(ModeSwitchAssist.EditingCaptionNoChip), "the suspended assist hides the chip");
        }

        [Test]
        public void TheModeTheSuspenderRestoresAsItEndsIsNotAnnounced()
        {
            // TutorialRunner.OnEnded puts the user's mode back, then releases the assist, in one call.
            ModeSwitchAssist.Suspend(_owner);
            _assist.Step(1f);
            _mode.SetMode(InteractionMode.SceneEdit);
            ModeSwitchAssist.Resume(_owner);
            _assist.Step(2f);
            Assert.That(_assist.CueCount, Is.Zero);
            // The next real change is cued.
            Invoke(_assist, "OnChipClicked");
            _assist.Step(3f);
            Assert.That(_assist.CueCount, Is.EqualTo(1));
            Assert.That(_assist.LastCueCaption, Is.EqualTo(ModeSwitchAssist.InteractionCaption));
        }

        [Test]
        public void TheCountdownToastTakesTheCaptionsPlace()
        {
            _mode.SetMode(InteractionMode.SceneEdit);
            _assist.Step(1f);
            Assert.That(_assist.Ui.CaptionVisible, Is.True);
            // A short idle setting can bring the countdown while the caption is still up; they share the upper band.
            typeof(ModeSwitchAssist).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_assist, new ModeSwitchIdleTimer(1f, 5f));
            _assist.Step(1.5f);
            _assist.Step(2.6f);
            Assert.That(_assist.Ui.ToastVisible, Is.True);
            Assert.That(_assist.Ui.CaptionVisible, Is.False);
        }

        [Test]
        public void AnAppWithoutTheChipGetsACaptionThatDoesNotMentionIt()
        {
            Assert.That(ModeSwitchAssist.CaptionFor(InteractionMode.SceneEdit, false, false, hasChip: false),
                Is.EqualTo(ModeSwitchAssist.EditingCaptionNoChip));
            StringAssert.DoesNotContain("chip", ModeSwitchAssist.EditingCaptionNoChip);
        }

        [Test]
        public void TheCueCanBeTurnedOff()
        {
            typeof(ModeSwitchAssist).GetField("_showModeCue", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_assist, false);
            Invoke(_assist, "OnChipClicked");
            _assist.Step(1f);
            Assert.That(_assist.CueCount, Is.Zero);
            Assert.That(_pulses, Is.Empty);
        }

        [Test]
        public void TheCaptionSitsBesideTheChipAboveTheWristPanel()
        {
            Transform chip = _assist.Ui.ChipObject.transform;
            Transform caption = _assist.Ui.CaptionObject.transform;
            Assert.That(caption.parent, Is.SameAs(chip.parent), "both ride the head-following HUD anchor");
            var chipRect = (RectTransform)chip;
            var captionRect = (RectTransform)caption;
            float chipRight = chipRect.localPosition.x + chipRect.sizeDelta.x * 0.5f * chipRect.localScale.x;
            float captionLeft = captionRect.localPosition.x - captionRect.sizeDelta.x * 0.5f * captionRect.localScale.x;
            float captionBottom = captionRect.localPosition.y - captionRect.sizeDelta.y * 0.5f * captionRect.localScale.y;
            Assert.That(captionLeft, Is.GreaterThan(chipRight), "beside the chip, not over it");
            Assert.That(captionRect.localPosition.y, Is.EqualTo(chipRect.localPosition.y).Within(1e-4f), "on the chip's row");
            // The HUD anchor is HudDropMetres below the eye at HudDistanceMetres: the caption's lower edge is above the wrist panel's top.
            float lowerEdgeDegrees = Mathf.Atan2(captionBottom - ModeSwitchUi.HudDropMetres, ModeSwitchUi.HudDistanceMetres) * Mathf.Rad2Deg;
            Assert.That(lowerEdgeDegrees, Is.GreaterThan(ModeSwitchUi.WristPanelTopDegrees));
        }

        [Test]
        public void TheChimeRisesIntoEditingAndFallsIntoInteraction()
        {
            float[] rising = ModeSwitchChime.Samples(true, ModeSwitchChime.SampleRate);
            float[] falling = ModeSwitchChime.Samples(false, ModeSwitchChime.SampleRate);
            int half = rising.Length / 2;
            Assert.That(rising.Length, Is.EqualTo(falling.Length));
            Assert.That(rising.Length / (float)ModeSwitchChime.SampleRate, Is.EqualTo(0.09f).Within(0.001f));
            Assert.That(Frequency(rising, 0, half), Is.EqualTo(ModeSwitchChime.LowHz).Within(40f));
            Assert.That(Frequency(rising, half, half), Is.EqualTo(ModeSwitchChime.HighHz).Within(40f));
            Assert.That(Frequency(falling, 0, half), Is.EqualTo(ModeSwitchChime.HighHz).Within(40f));
            Assert.That(Frequency(falling, half, half), Is.EqualTo(ModeSwitchChime.LowHz).Within(40f));
            foreach (float sample in rising) Assert.That(Mathf.Abs(sample), Is.LessThanOrEqualTo(ModeSwitchChime.Amplitude + 1e-5f));
            Assert.That(Mathf.Abs(rising[0]), Is.LessThan(0.01f), "faded in: no click");
            Assert.That(Mathf.Abs(rising[rising.Length - 1]), Is.LessThan(0.01f), "faded out: no click");
        }

        // Upward zero crossings per second over one tone.
        private static float Frequency(float[] samples, int start, int count)
        {
            int crossings = 0;
            for (int i = start + 1; i < start + count; i++)
                if (samples[i - 1] < 0f && samples[i] >= 0f) crossings++;
            return crossings / (count / (float)ModeSwitchChime.SampleRate);
        }

        private static void Invoke(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(target, args.Length == 0 ? null : args);
    }
}
#endif
