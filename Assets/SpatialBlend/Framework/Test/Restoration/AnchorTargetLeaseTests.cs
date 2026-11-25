// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FreeformRestoration")]
    public sealed class AnchorTargetLeaseTests
    {
        private GameObject _go;
        private AnchorTargetController _controller;
        [SetUp] public void SetUp() { _go = new GameObject("leases"); _controller = _go.AddComponent<AnchorTargetController>(); }
        [TearDown] public void TearDown() { Object.DestroyImmediate(_go); }

        [Test]
        public void ExclusiveLeaseDisposalAndLegacyIsolationAreExact()
        {
            Assert.That(_controller.TryAcquireLease("body", out AnchorTargetController.TargetLease first), Is.True);
            Assert.That(_controller.TryAcquireLease("body", out _), Is.False);
            _controller.ApplyTarget("body", Vector3.one, 0f, false, 1d, 1d);
            Assert.That(first.TryApply(Vector3.zero, 0f, false, 1d, 0d), Is.True);
            Assert.That(_controller.TryGetTarget("body", out _), Is.True);
            first.Dispose();
            Assert.That(_controller.TryAcquireLease("body", out AnchorTargetController.TargetLease replacement), Is.True);
            first.Cancel();
            Assert.That(replacement.TryApply(Vector3.right, 0f, false, 1d, 0d), Is.True);
        }

        [Test]
        public void LeasedRootAndInvalidCommandsHaveExpectedOwnership()
        {
            Assert.That(_controller.TryAcquireLease("root", out AnchorTargetController.TargetLease lease), Is.True);
            Assert.That(lease.TryApply(Vector3.zero, 0f, false, 1d, 1d), Is.True);
            Assert.That(lease.TryApply(new Vector3(float.NaN, 0f, 0f), 0f, false, 1d, 2d), Is.False);
            lease.Dispose();
            _controller.ApplyTarget("root", Vector3.zero, 0f, false, 1d, 1d);
            Assert.That(_controller.TryGetTarget("root", out _), Is.False);
        }

        [Test]
        public void ReentrantLeaseApplyAndCancellationCannotNotifyStaleSubscribers()
        {
            Assert.That(_controller.TryAcquireLease("body", out AnchorTargetController.TargetLease lease), Is.True);
            int second = 0;
            bool newerAccepted = false;
            bool duplicateRejected = false;
            bool reentered = false;
            _controller.TargetApplied += (_, _, _, _) =>
            {
                if (reentered) return;
                reentered = true;
                newerAccepted = lease.TryApply(Vector3.right, 0f, false, 1d, 2d);
                duplicateRejected = !lease.TryApply(Vector3.right, 0f, false, 1d, 1d);
                lease.Cancel();
            };
            _controller.TargetApplied += (_, _, _, _) => ++second;
            Assert.That(lease.TryApply(Vector3.zero, 0f, false, 1d, 1d), Is.True);
            Assert.That(newerAccepted, Is.True);
            Assert.That(duplicateRejected, Is.True);
            Assert.That(second, Is.EqualTo(1));
        }

        [Test]
        public void LeasedGoalIsStabilizedAfterGoalRootMoves()
        {
            var root = new GameObject("goal-root");
            try
            {
                _controller.SetGoalRoot(root.transform);
                Assert.That(_controller.TryAcquireLease("body", out AnchorTargetController.TargetLease lease), Is.True);
                Vector3 expected = new Vector3(2f, 0f, 0f);
                Assert.That(lease.TryApply(expected, 0f, false, 1d, 1d), Is.True);
                root.transform.position = new Vector3(10f, 0f, 0f);
                _controller.Tick(2d);
                Assert.That(_controller.GetGoal("body").position, Is.EqualTo(expected));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void NewerSameLeaseCommandSuppressesOnlyOldObserverDelivery()
        {
            Assert.That(_controller.TryAcquireLease("body", out AnchorTargetController.TargetLease lease), Is.True);
            var seen = new System.Collections.Generic.List<Vector3>();
            bool sentNewer = false;
            bool newerAccepted = false;
            _controller.TargetApplied += (_, pose, _, _) =>
            {
                if (!sentNewer)
                {
                    sentNewer = true;
                    newerAccepted = lease.TryApply(Vector3.right, 0f, false, 1d, 2d);
                }
            };
            _controller.TargetApplied += (_, pose, _, _) => seen.Add(pose.position);
            Assert.That(lease.TryApply(Vector3.zero, 0f, false, 1d, 1d), Is.True);
            Assert.That(newerAccepted, Is.True);
            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0], Is.EqualTo(Vector3.right));
        }

        [Test]
        public void ThrownObserverDoesNotBlockLaterObserverAndALeasedTargetNeverExpires()
        {
            Assert.That(_controller.TryAcquireLease("body", out AnchorTargetController.TargetLease lease), Is.True);
            int later = 0;
            _controller.TargetApplied += (_, _, _, _) => throw new System.InvalidOperationException();
            _controller.TargetApplied += (_, _, _, _) => ++later;
            Assert.That(lease.TryApply(Vector3.zero, 0f, false, 1d, 1d), Is.True);
            Assert.That(later, Is.EqualTo(1));
            _controller.Tick(1e9d);
            Assert.That(_controller.TryGetTarget("body", out _), Is.True, "no time-based expiry");
            _go.SetActive(false);
            Assert.That(_controller.TryAcquireLease("other", out _), Is.False);
        }

        // There is no expiry (v1 had no anchor-target controller, and no paper gives one). A target is
        // released only by an explicit cancel, so a long gap between authoritative updates never withdraws a command.
        [Test]
        public void ATargetIsNeverReleasedByTheClock()
        {
            int releases = 0;
            _controller.TargetReleased += _ => ++releases;
            _controller.ApplyTarget("legacy", Vector3.one, 0f, false, 1d, 0d);
            _controller.Tick(0.5d);
            _controller.Tick(2d);
            _controller.Tick(1e9d);
            Assert.That(_controller.TryGetTarget("legacy", out _), Is.True);
            Assert.That(releases, Is.Zero);
            _controller.CancelTarget("legacy");
            Assert.That(releases, Is.EqualTo(1), "explicit cancel still releases");
        }
    }
}
#endif
