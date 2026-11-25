// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Pure share-once rules ported from v1 (register entities
    /// created before connect, no duplicate registration).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class NativeEntityReadyToShareTrackerTests
    {
        private static NativeEntityShareState State(bool remote = false, bool active = true,
            bool attached = false, bool content = true) =>
            new NativeEntityShareState(remote, active, attached, content);

        [Test]
        public void EntityWithContentIsSharedExactlyOnce()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            Assert.That(tracker.Evaluate("a", State()), Is.EqualTo(NativeShareDecision.Share));
            Assert.That(tracker.Evaluate("a", State()), Is.EqualTo(NativeShareDecision.Skip),
                "A second scan must not register the same entity again.");
            Assert.That(tracker.OnContentReady("a", State(), true), Is.EqualTo(NativeShareDecision.Skip));
            Assert.That(tracker.WasShared("a"), Is.True);
        }

        [Test]
        public void EmptyContentIsDeferredThenSharedWhenReady()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            Assert.That(tracker.Evaluate("a", State(content: false)), Is.EqualTo(NativeShareDecision.Defer));
            Assert.That(tracker.IsPending("a"), Is.True);
            Assert.That(tracker.WasShared("a"), Is.False, "No name-only placeholder is shared.");
            Assert.That(tracker.OnContentReady("a", State(), true), Is.EqualTo(NativeShareDecision.Share));
            Assert.That(tracker.IsPending("a"), Is.False);
            Assert.That(tracker.OnContentReady("a", State(), true), Is.EqualTo(NativeShareDecision.Skip));
        }

        [Test]
        public void ReadyBeforeTheAppCanShareLeavesTheEntityAvailableToALaterScan()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            tracker.Evaluate("a", State(content: false));
            Assert.That(tracker.OnContentReady("a", State(), false), Is.EqualTo(NativeShareDecision.Skip));
            Assert.That(tracker.IsPending("a"), Is.False);
            Assert.That(tracker.WasShared("a"), Is.False);
            Assert.That(tracker.Evaluate("a", State()), Is.EqualTo(NativeShareDecision.Share));
        }

        [Test]
        public void AttachedRemoteInactiveEntitiesAreNeverShared()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            Assert.That(tracker.Evaluate("attached", State(attached: true)), Is.EqualTo(NativeShareDecision.Skip));
            Assert.That(tracker.Evaluate("remote", State(remote: true)), Is.EqualTo(NativeShareDecision.Skip));
            Assert.That(tracker.Evaluate("inactive", State(active: false)), Is.EqualTo(NativeShareDecision.Skip));
            Assert.That(tracker.WasShared("attached") || tracker.WasShared("remote") ||
                        tracker.WasShared("inactive"), Is.False);
        }

        [Test]
        public void EntityAttachedWhilePendingLeavesThePendingSet()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            tracker.Evaluate("a", State(content: false));
            Assert.That(tracker.Evaluate("a", State(attached: true, content: false)),
                Is.EqualTo(NativeShareDecision.Skip));
            Assert.That(tracker.IsPending("a"), Is.False);
        }

        [Test]
        public void ForgetAllowsARetryAfterAFailedRegistration()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            Assert.That(tracker.Evaluate("a", State()), Is.EqualTo(NativeShareDecision.Share));
            tracker.Forget("a");
            Assert.That(tracker.Evaluate("a", State()), Is.EqualTo(NativeShareDecision.Share));
        }

        [Test]
        public void ClearPendingKeepsShareRecordsAndResetClearsBoth()
        {
            var tracker = new NativeEntityReadyToShareTracker<string>();
            tracker.Evaluate("shared", State());
            tracker.Evaluate("waiting", State(content: false));
            tracker.ClearPending();
            Assert.That(tracker.PendingCount, Is.EqualTo(0));
            Assert.That(tracker.WasShared("shared"), Is.True);
            tracker.Reset();
            Assert.That(tracker.WasShared("shared"), Is.False);
            Assert.That(tracker.Pending.Any(), Is.False);
        }

        [Test]
        public void ManyEntitiesEachShareOnceAcrossRepeatedScans()
        {
            var tracker = new NativeEntityReadyToShareTracker<int>();
            int shares = 0;
            for (int scan = 0; scan < 3; ++scan)
                for (int id = 0; id < 10; ++id)
                    if (tracker.Evaluate(id, State(content: id % 2 == 0)) == NativeShareDecision.Share)
                        ++shares;
            Assert.That(shares, Is.EqualTo(5), "Only the five entities with content are shared, once each.");
            Assert.That(tracker.PendingCount, Is.EqualTo(5));
        }
    }
}
#endif
