// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Samples;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A join and a shared object are each one narration line. A burst of objects is one line, and a second burst
    /// while that line is still up says nothing.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ArrivalNarrationTests
    {
        [Test]
        public void EachLineNamesItsClip()
        {
            Assert.That(ArrivalNarration.ClipFor(ArrivalNarration.PersonLine), Is.EqualTo(ArrivalNarration.PersonClip));
            Assert.That(ArrivalNarration.ClipFor(ArrivalNarration.ObjectLine), Is.EqualTo(ArrivalNarration.ObjectClip));
            Assert.That(ArrivalNarration.ClipFor(ArrivalNarration.ObjectsLine), Is.EqualTo(ArrivalNarration.ObjectsClip));
            Assert.That(ArrivalNarration.ClipFor(ArrivalNarration.PersonAndObjectLine), Is.EqualTo(ArrivalNarration.PersonAndObjectClip));
            Assert.That(ArrivalNarration.ClipFor(ArrivalNarration.PersonAndObjectsLine), Is.EqualTo(ArrivalNarration.PersonAndObjectsClip));
            Assert.That(ArrivalNarration.ClipFor("nope"), Is.Null);
        }

        [Test]
        public void OneArrivalIsOneLine()
        {
            var narration = new ArrivalNarration();
            narration.NotePerson();
            Assert.That(narration.Flush(0f), Is.EqualTo(ArrivalNarration.PersonLine));

            narration.NoteObject();
            Assert.That(narration.Flush(10f), Is.EqualTo(ArrivalNarration.ObjectLine));
        }

        [Test]
        public void SeveralObjectsInOneMomentAreOneLine()
        {
            var narration = new ArrivalNarration();
            narration.NoteObject();
            narration.NoteObject();
            narration.NoteObject();
            narration.NotePerson();

            Assert.That(narration.Flush(0f), Is.EqualTo(ArrivalNarration.PersonAndObjectsLine));
            Assert.That(narration.Flush(0.5f), Is.Null, "the line is still up");
        }

        [Test]
        public void MoreObjectsWhileTheLineIsUpAreNotSaidAgain()
        {
            var narration = new ArrivalNarration();
            narration.NoteObject();
            Assert.That(narration.Flush(0f), Is.EqualTo(ArrivalNarration.ObjectLine));

            narration.NoteObject();
            narration.NoteObject();
            Assert.That(narration.Flush(1f), Is.Null);
            Assert.That(narration.Flush(ArrivalNarration.HoldSeconds), Is.Null, "those objects were the same news");
        }

        [Test]
        public void APersonWhoArrivesDuringAnObjectLineIsSaidWhenItEnds()
        {
            var narration = new ArrivalNarration();
            narration.NoteObject();
            Assert.That(narration.Flush(0f), Is.EqualTo(ArrivalNarration.ObjectLine));

            narration.NotePerson();
            Assert.That(narration.Flush(1f), Is.Null);
            Assert.That(narration.Flush(ArrivalNarration.HoldSeconds), Is.EqualTo(ArrivalNarration.PersonLine));
        }

        [Test]
        public void ALaterArrivalIsSaidOnceTheHoldIsOver()
        {
            var narration = new ArrivalNarration();
            narration.NoteObject();
            narration.Flush(0f);

            narration.NoteObject();
            narration.NoteObject();
            Assert.That(narration.Flush(ArrivalNarration.HoldSeconds), Is.EqualTo(ArrivalNarration.ObjectsLine));
        }
    }
}
#endif
