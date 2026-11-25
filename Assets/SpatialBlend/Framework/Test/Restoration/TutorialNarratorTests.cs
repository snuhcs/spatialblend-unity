// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>Subtitle and sentence timing with an injected clock, and the done mark's storage.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialNarratorTests
    {
        private FakeTutorialClock _clock;
        private TutorialNarrator _narrator;
        private List<string> _subtitles;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeTutorialClock();
            _narrator = new TutorialNarrator(_clock);
            _subtitles = new List<string>();
            _narrator.SubtitleChanged += (i, text) => _subtitles.Add(i + ":" + (text ?? "<none>"));
        }

        [Test]
        public void SplitSentences_CutsAtTerminalPunctuationFollowedBySpaceOrEnd()
        {
            Assert.That(TutorialNarrator.SplitSentences("One. Two! Three? Four"), Is.EqualTo(new[] { "One.", "Two!", "Three?", "Four" }));
            Assert.That(TutorialNarrator.SplitSentences("A 3.5 mm jack. Done."), Is.EqualTo(new[] { "A 3.5 mm jack.", "Done." }), "a dot inside a number does not cut");
            Assert.That(TutorialNarrator.SplitSentences("   "), Is.Empty);
            Assert.That(TutorialNarrator.SplitSentences(null), Is.Empty);
        }

        [Test]
        public void EveryTutorialLine_SplitsIntoItsSentences_AndTheyRejoinToTheLine()
        {
            foreach (var step in TutorialScript.AllSteps())
            {
                var sentences = TutorialNarrator.SplitSentences(step.Voice);
                Assert.That(sentences.Length, Is.InRange(1, 4), step.ClipName);
                Assert.That(string.Join(" ", sentences), Is.EqualTo(step.Voice), step.ClipName);
                Assert.That(step.Voice.Length, Is.LessThan(150), step.ClipName + ": narration lines are short");
            }
        }

        [Test]
        public void SentenceEnds_FollowTheCharacterShare_AndTheLastOneIsTheClipLength()
        {
            var sentences = new[] { "aaaa.", "bbbbbbbbb.", "cc." };   // 5, 10, 3 characters of 18
            var ends = TutorialNarrator.SentenceEnds(sentences, 18.0);
            Assert.That(ends[0], Is.EqualTo(5.0).Within(1e-9));
            Assert.That(ends[1], Is.EqualTo(15.0).Within(1e-9));
            Assert.That(ends[2], Is.EqualTo(18.0).Within(1e-9));
        }

        [Test]
        public void Subtitles_ShowSentenceBySentence_AtTheirShareOfTheClip()
        {
            _narrator.Play("Aaaa. Bbbbbbbbb. Cc.", 18.0);
            Assert.That(_narrator.Subtitle, Is.EqualTo("Aaaa."));
            _clock.Advance(4.9); _narrator.Tick();
            Assert.That(_narrator.Subtitle, Is.EqualTo("Aaaa."));
            _clock.Advance(0.2); _narrator.Tick();
            Assert.That(_narrator.Subtitle, Is.EqualTo("Bbbbbbbbb."));
            _clock.Advance(10.0); _narrator.Tick();
            Assert.That(_narrator.Subtitle, Is.EqualTo("Cc."));
            Assert.That(_subtitles, Is.EqualTo(new[] { "0:Aaaa.", "1:Bbbbbbbbb.", "2:Cc." }));
            _clock.Advance(3.0); _narrator.Tick();
            Assert.That(_narrator.Playing, Is.False);
            Assert.That(_narrator.Subtitle, Is.Empty);
            Assert.That(_subtitles.Last(), Is.EqualTo("-1:<none>"));
        }

        [Test]
        public void ABigJumpInTime_LandsOnTheRightSentence()
        {
            _narrator.Play("One. Two. Three. Four.", 8.0);
            _clock.Advance(5.0); _narrator.Tick();
            Assert.That(_narrator.SentenceIndex, Is.EqualTo(2));
        }

        [Test]
        public void Pause_StopsTheClockForTheNarration_ResumeContinues()
        {
            _narrator.Play("One. Two.", 10.0);
            _clock.Advance(2.0); _narrator.Tick();
            _narrator.Pause();
            Assert.That(_narrator.Paused, Is.True);
            _clock.Advance(100.0); _narrator.Tick();
            Assert.That(_narrator.Elapsed, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(_narrator.SentenceIndex, Is.EqualTo(0));
            _narrator.Resume();
            _clock.Advance(4.0); _narrator.Tick();
            Assert.That(_narrator.Elapsed, Is.EqualTo(6.0).Within(1e-9));
            Assert.That(_narrator.SentenceIndex, Is.EqualTo(1));
        }

        [Test]
        public void RunAtSentenceEnd_WaitsForTheEndOfTheCurrentSentence_NotTheClip()
        {
            _narrator.Play("Aaaa. Bbbb. Cccc.", 12.0);   // 4 s each
            _clock.Advance(1.0); _narrator.Tick();
            int ran = 0;
            _narrator.RunAtSentenceEnd(() => ran++);
            Assert.That(ran, Is.EqualTo(0));
            Assert.That(_narrator.SecondsToSentenceEnd, Is.EqualTo(3.0).Within(1e-9));
            _clock.Advance(2.9); _narrator.Tick();
            Assert.That(ran, Is.EqualTo(0));
            _clock.Advance(0.2); _narrator.Tick();
            Assert.That(ran, Is.EqualTo(1), "ran when the second sentence began");
            Assert.That(_narrator.Playing, Is.True, "the clip goes on");
            _clock.Advance(20.0); _narrator.Tick();
            Assert.That(ran, Is.EqualTo(1), "only once");
        }

        [Test]
        public void RunAtSentenceEnd_RunsAtOnceWhenNothingPlays_AndOnStop()
        {
            int ran = 0;
            _narrator.RunAtSentenceEnd(() => ran++);
            Assert.That(ran, Is.EqualTo(1));
            _narrator.Play("One. Two.", 6.0);
            _narrator.RunAtSentenceEnd(() => ran++);
            Assert.That(ran, Is.EqualTo(1));
            _narrator.Stop();
            Assert.That(ran, Is.EqualTo(2), "nothing is being said any more");
            Assert.That(_narrator.Playing, Is.False);
        }

        [Test]
        public void AClipOfZeroLengthOrNoText_PlaysNothing()
        {
            _narrator.Play("Hello there.", 0.0);
            Assert.That(_narrator.Playing, Is.False);
            Assert.That(_narrator.AtSentenceEnd, Is.True);
            _narrator.Play("", 5.0);
            Assert.That(_narrator.Playing, Is.False);
        }

        [Test]
        public void Finished_IsRaisedWhenTheClipEnds_NotWhenItIsStopped()
        {
            int finished = 0;
            _narrator.Finished += () => finished++;
            _narrator.Play("One. Two.", 4.0);
            _clock.Advance(4.0); _narrator.Tick();
            Assert.That(finished, Is.EqualTo(1));
            _narrator.Play("One. Two.", 4.0);
            _narrator.Stop();
            Assert.That(finished, Is.EqualTo(1));
        }

        [Test]
        public void PlayingANewLine_ReplacesTheOldOne()
        {
            _narrator.Play("Old one. Old two.", 4.0);
            _clock.Advance(1.0); _narrator.Tick();
            _narrator.Play("New.", 2.0);
            Assert.That(_narrator.Subtitle, Is.EqualTo("New."));
            Assert.That(_narrator.Elapsed, Is.EqualTo(0.0));
        }
    }

    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialDoneMarkTests
    {
        [Test]
        public void MarkDone_IsStoredAndRead()
        {
            var store = new FakeKeyValueStore();
            var mark = new TutorialDoneMark(store);
            Assert.That(mark.IsDone, Is.False);
            Assert.That(mark.MarkDone(), Is.True);
            Assert.That(store.Saves, Is.EqualTo(1));
            Assert.That(new TutorialDoneMark(store).IsDone, Is.True, "a new reader sees it");
        }

        [Test]
        public void AFailingRead_IsCaughtAndReadsAsNotDone()
        {
            var store = new FakeKeyValueStore { ThrowOnRead = true };
            var mark = new TutorialDoneMark(store);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Could not read the done mark"));
            Assert.That(mark.IsDone, Is.False);
        }

        [Test]
        public void AFailingWrite_IsCaught_TheMarkStillCountsForTheSession()
        {
            var store = new FakeKeyValueStore { ThrowOnWrite = true };
            var mark = new TutorialDoneMark(store);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Could not save the done mark"));
            Assert.That(mark.MarkDone(), Is.False);
            Assert.That(mark.IsDone, Is.True, "no endless hint dot in this session");
        }

        [Test]
        public void AFailingSave_IsCaughtToo()
        {
            var store = new FakeKeyValueStore { ThrowOnSave = true };
            var mark = new TutorialDoneMark(store);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Could not save the done mark"));
            Assert.That(mark.MarkDone(), Is.False);
        }
    }
}
#endif
