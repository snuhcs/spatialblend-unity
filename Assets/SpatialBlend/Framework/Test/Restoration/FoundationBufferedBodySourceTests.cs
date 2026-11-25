// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>Authored numeric fixtures only; no FC scene, corpus or device.</summary>
    [Category("FoundationReceiverInput")]
    public sealed class FoundationBufferedBodySourceTests
    {
        private const long Origin = 1000000000;

        private static ReceiverBodyProfile Profile(long uncertainty = 0)
        {
            var input = ReceiverBodyFixture.Input();
            Assert.That(ReceiverBodyClockMapping.TryCreate("authored-recorded-seconds", 1, 1,
                ReceiverBodyClockMappingKind.ReplayTimeline, "authored-test-timeline",
                input.ClockId, input.ClockEpoch, 0, Origin, 1000000000, uncertainty, 1, Origin + 30000000000,
                out var clock, out string error), Is.True, error);
            Assert.That(ReceiverBodyProfile.TryCreate(input, "authored-test-reader", 1, clock,
                ReceiverBodyEvidenceKind.RecordedBody, "synthetic-buffer-test-not-corpus",
                ReceiverBodyFixture.Skeleton(), Matrix4x4.identity, ReceiverBodyFixture.Anchors(),
                out var profile, out error), Is.True, error);
            return profile;
        }

        private static BufferedReceiverBodySource Buffer(ReceiverBodyProfile profile, params ReceiverBodyFrame[] frames)
        {
            Assert.That(BufferedReceiverBodySource.TryCreate(profile, frames, out var source, out string error), Is.True, error);
            return source;
        }

        private static ReceiverBodyFrame Acquire(BufferedReceiverBodySource source, long floor, ReceiverBodySampleStatus expected)
        {
            Assert.That(source.TryAcquire(source.Profile, floor, out var status, out var frame, out string error), Is.True, error);
            Assert.That(status, Is.EqualTo(expected));
            if (status != ReceiverBodySampleStatus.Sample) Assert.That(frame, Is.Null);
            return frame;
        }

        [Test]
        public void BufferedBodyFreezesCompleteFramesWithoutRestampOrRootInference()
        {
            var profile = Profile();
            var first = ReceiverBodyFixture.Frame(profile, root: false);
            var frames = new[] { first };
            var source = Buffer(profile, frames);
            frames[0] = null;
            var acquired = Acquire(source, Origin + 100, ReceiverBodySampleStatus.Sample);
            Assert.That(acquired, Is.SameAs(first)); Assert.That(acquired.Profile, Is.SameAs(profile));
            Assert.That(acquired.GeometricInput.ObservedAtNanoseconds, Is.EqualTo(Origin));
            Assert.That(acquired.SourceTimeSeconds, Is.Zero); Assert.That(acquired.SourceSequence, Is.Zero);
            Assert.That(acquired.Joints.Count, Is.EqualTo(15)); Assert.That(acquired.HasAllJointPoses, Is.True);
            Assert.That(acquired.Joints[1].ParentFromJoint, Is.EqualTo(first.Joints[1].ParentFromJoint));
            Assert.That(acquired.GeometricInput.Anchors.Single(a => a.Name == "locomotion").Available, Is.False);
            Assert.That(source.FrameCount, Is.EqualTo(1)); Assert.That(source.DeliveredFrameCount, Is.EqualTo(1));
        }

        [Test]
        public void BufferedBodyWaitsForWholeMappedAcquisitionInterval()
        {
            var profile = Profile(13); var first = ReceiverBodyFixture.Frame(profile);
            var source = Buffer(profile, first);
            Acquire(source, Origin - 13, ReceiverBodySampleStatus.Unavailable);
            Acquire(source, Origin + 12, ReceiverBodySampleStatus.Unavailable);
            Assert.That(source.DeliveredFrameCount, Is.Zero);
            Assert.That(Acquire(source, Origin + 13, ReceiverBodySampleStatus.Sample), Is.SameAs(first));
            Assert.That(first.GeometricInput.ObservedAtNanoseconds, Is.EqualTo(Origin - 13));
        }

        [Test]
        public void BufferedBodyEmptyGapsAndCompletionNeverHoldPastExpiry()
        {
            var profile = Profile();
            Acquire(Buffer(profile), Origin, ReceiverBodySampleStatus.Unavailable);
            var first = ReceiverBodyFixture.Frame(profile, expiry: Origin + 100000000);
            var last = ReceiverBodyFixture.Frame(profile, 2, 1, .5, expiry: Origin + 600000000);
            var source = Buffer(profile, first, last);
            Acquire(source, Origin - 1, ReceiverBodySampleStatus.Unavailable);
            Acquire(source, Origin, ReceiverBodySampleStatus.Sample);
            Acquire(source, first.GeometricInput.ValidUntilNanoseconds - 1, ReceiverBodySampleStatus.NoChange);
            Acquire(source, first.GeometricInput.ValidUntilNanoseconds, ReceiverBodySampleStatus.Unavailable);
            Acquire(source, last.AcquisitionUpperNanoseconds - 1, ReceiverBodySampleStatus.Unavailable);
            Acquire(source, last.AcquisitionUpperNanoseconds, ReceiverBodySampleStatus.Sample);
            Acquire(source, last.GeometricInput.ValidUntilNanoseconds - 1, ReceiverBodySampleStatus.NoChange);
            Acquire(source, last.GeometricInput.ValidUntilNanoseconds, ReceiverBodySampleStatus.Unavailable);
            Acquire(source, last.GeometricInput.ValidUntilNanoseconds + 1, ReceiverBodySampleStatus.Unavailable);
            Assert.That(source.IsFaulted, Is.False);
        }

        [Test]
        public void BufferedBodyDrainsLiveBacklogInOrderWithoutCoalescing()
        {
            var profile = Profile();
            var frames = new[] { ReceiverBodyFixture.Frame(profile), ReceiverBodyFixture.Frame(profile, 2, 1, .125),
                ReceiverBodyFixture.Frame(profile, 3, 2, .25) };
            var source = Buffer(profile, frames);
            foreach (var frame in frames)
                Assert.That(Acquire(source, Origin + 300000000, ReceiverBodySampleStatus.Sample), Is.SameAs(frame));
            Acquire(source, Origin + 300000000, ReceiverBodySampleStatus.NoChange);
            Assert.That(source.DeliveredFrameCount, Is.EqualTo(3));
        }

        [Test]
        public void BufferedBodyExpiredUndeliveredFrameFaultsWithoutSkipping()
        {
            var profile = Profile();
            var source = Buffer(profile, ReceiverBodyFixture.Frame(profile, expiry: Origin + 100),
                ReceiverBodyFixture.Frame(profile, 2, 1));
            foreach (long floor in new[] { Origin + 100, Origin + 200, Origin + 300 })
            {
                Assert.That(source.TryAcquire(profile, floor, out var status, out var frame, out string error), Is.False);
                Assert.That(error, Does.Contain("expired")); Assert.That(frame, Is.Null);
                Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Unavailable));
                Assert.That(source.DeliveredFrameCount, Is.Zero); Assert.That(source.IsFaulted, Is.True);
            }
        }

        [Test]
        public void BufferedBodyRejectsInvalidFinalRowBeforeReturningAnySource()
        {
            var profile = Profile(); var first = ReceiverBodyFixture.Frame(profile, 2, 1, .25);
            var sameContentProfile = Profile();
            Assert.That(profile.Fingerprint, Is.EqualTo(sameContentProfile.Fingerprint));
            foreach (var last in new[] { null, ReceiverBodyFixture.Frame(sameContentProfile, 3, 2, .5),
                ReceiverBodyFixture.Frame(profile, 3, 1, .5), ReceiverBodyFixture.Frame(profile, 3, 0, .5),
                ReceiverBodyFixture.Frame(profile, 2, 2, .5), ReceiverBodyFixture.Frame(profile, 1, 2, .5),
                ReceiverBodyFixture.Frame(profile, 3, 2, .125) })
            {
                Assert.That(BufferedReceiverBodySource.TryCreate(profile, new[] { first, last }, out var source, out _), Is.False);
                Assert.That(source, Is.Null);
            }
        }

        private sealed class CallerFrames : IReadOnlyList<ReceiverBodyFrame>
        {
            public Func<int> ReadCount;
            public Func<int, ReceiverBodyFrame> ReadFrame;
            public int Count => ReadCount();
            public ReceiverBodyFrame this[int index] => ReadFrame(index);
            public IEnumerator<ReceiverBodyFrame> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Test]
        public void BufferedBodyRejectsUnboundedChangingAndThrowingCollections()
        {
            var profile = Profile(); int reads = 0;
            foreach (int count in new[] { -1, BufferedReceiverBodySource.MaximumFrames + 1,
                BufferedReceiverBodySource.MaximumJointSamples / profile.Skeleton.Joints.Count + 1 })
            {
                var rows = new CallerFrames { ReadCount = () => count, ReadFrame = _ => { ++reads; return null; } };
                Assert.That(BufferedReceiverBodySource.TryCreate(profile, rows, out var source, out _), Is.False);
                Assert.That(source, Is.Null);
            }
            Assert.That(reads, Is.Zero, "Reject bounds before touching rows or allocating the buffer.");
            int counts = 0;
            var changing = new CallerFrames { ReadCount = () => ++counts, ReadFrame = _ => ReceiverBodyFixture.Frame(profile) };
            Assert.That(BufferedReceiverBodySource.TryCreate(profile, changing, out _, out _), Is.False);
            var throwing = new CallerFrames { ReadCount = () => 1, ReadFrame = _ => throw new InvalidOperationException() };
            Assert.That(BufferedReceiverBodySource.TryCreate(profile, throwing, out _, out _), Is.False);
            Assert.That(BufferedReceiverBodySource.TryCreate(profile, null, out _, out _), Is.False);
            Assert.That(BufferedReceiverBodySource.TryCreate(null, Array.Empty<ReceiverBodyFrame>(), out _, out _), Is.False);
        }

        [Test]
        public void BufferedBodyWrongProfileAndRegressedClockCannotConsumeOrFaultOwner()
        {
            var profile = Profile(); var first = ReceiverBodyFixture.Frame(profile);
            var source = Buffer(profile, first, ReceiverBodyFixture.Frame(profile, 2, 1, .25));
            Assert.That(source.TryAcquire(Profile(), long.MaxValue, out _, out _, out _), Is.False);
            Assert.That(source.TryAcquire(profile, 0, out _, out _, out _), Is.False);
            Assert.That(source.IsFaulted, Is.False); Assert.That(source.DeliveredFrameCount, Is.Zero);
            Assert.That(Acquire(source, Origin, ReceiverBodySampleStatus.Sample), Is.SameAs(first));
            Assert.That(source.TryAcquire(profile, Origin - 1, out _, out _, out _), Is.False);
            Acquire(source, Origin + 250000000, ReceiverBodySampleStatus.Sample);
            Assert.That(source.DeliveredFrameCount, Is.EqualTo(2));
        }

        [Test]
        public void BufferedBodyWorkerCannotConsumeOrPoisonOwner()
        {
            var profile = Profile(); var source = Buffer(profile, ReceiverBodyFixture.Frame(profile));
            bool accepted = true; ReceiverBodyFrame frame = null; string error = null;
            var worker = new Thread(() => accepted = source.TryAcquire(profile, long.MaxValue, out _, out frame, out error));
            worker.Start(); worker.Join();
            Assert.That(accepted, Is.False); Assert.That(frame, Is.Null); Assert.That(error, Does.Contain("thread"));
            Assert.That(source.IsFaulted, Is.False); Assert.That(source.DeliveredFrameCount, Is.Zero);
            Acquire(source, Origin, ReceiverBodySampleStatus.Sample);
        }

        [Test]
        public void BufferedBodyAcquisitionIsAtMostOnceNotNativeAcknowledgement()
        {
            var profile = Profile(); var source = Buffer(profile, ReceiverBodyFixture.Frame(profile));
            Acquire(source, Origin, ReceiverBodySampleStatus.Sample);
            // The caller deliberately performs no native submit. This protocol
            // cannot know success/failure and must never redeliver a duplicate.
            Acquire(source, Origin, ReceiverBodySampleStatus.NoChange);
            Assert.That(source.DeliveredFrameCount, Is.EqualTo(1));
        }
    }

    public sealed partial class FoundationReceiverBodyNativeTests
    {
        [Test]
        public void BufferedBodyNativeExactJoinAndIndependentRootLoss()
        {
            var first = Frame(); var lost = Frame(2, 1, 0, root: false);
            Assert.That(BufferedReceiverBodySource.TryCreate(_binding.Profile, new[] { first, lost }, out var source, out string error), Is.True, error);
            Assert.That(_binding.TryReadAcquisitionClock(out long floor, out error), Is.True, error);
            Assert.That(source.TryAcquire(_binding.Profile, floor, out var status, out var acquired, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Sample)); Assert.That(acquired, Is.SameAs(first));
            Assert.That(_binding.TrySubmit(acquired, out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(Solve(), out var joined, out error), Is.True, error);
            Assert.That(joined, Is.SameAs(first));
            Assert.That(source.TryAcquire(_binding.Profile, floor, out status, out acquired, out error), Is.True, error);
            Assert.That(acquired, Is.SameAs(lost)); Assert.That(acquired.HasAllJointPoses, Is.True);
            Assert.That(_binding.TrySubmit(acquired, out error), Is.True, error);
            var released = Solve();
            Assert.That(released.Channels.Any(channel => channel.HasGoal), Is.False);
            Assert.That(_delivered.Last().Reason, Is.EqualTo(RichMailboxReason.NativeRelease));
        }
    }
}
#endif
