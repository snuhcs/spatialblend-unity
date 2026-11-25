// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    public class AnchorTargetControllerTests
    {
        private GameObject _gameObject;
        private GameObject _externalGoalRoot;
        private AnchorTargetController _controller;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("anchor-target-test");
            _externalGoalRoot = new GameObject("external-goal-root");
            _controller = _gameObject.AddComponent<AnchorTargetController>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
            {
                Object.DestroyImmediate(_gameObject);
            }
            if (_externalGoalRoot != null)
            {
                Object.DestroyImmediate(_externalGoalRoot);
            }
        }

        [Test]
        public void IndependentAnchors_KeepSeparateGoals_AndRootIsNotClaimed()
        {
            _controller.ApplyTarget("hand_l", new Vector3(1f, 2f, 3f), 45f, true, 0.8, 10d);
            _controller.ApplyTarget("pelvis", new Vector3(4f, 1f, 2f), 90f, true, 0.5, 10d);
            _controller.ApplyTarget("root", new Vector3(9f, 9f, 9f), 0f, false, 1d, 10d);

            Assert.That(_controller.TryGetTarget("hand_l", out var hand), Is.True);
            Assert.That(_controller.TryGetTarget("pelvis", out var pelvis), Is.True);
            Assert.That(hand.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(pelvis.Position, Is.EqualTo(new Vector3(4f, 1f, 2f)));
            Assert.AreNotSame(_controller.GetGoal("hand_l"), _controller.GetGoal("pelvis"));
            Assert.That(_controller.TryGetTarget("root", out _), Is.False);
            Assert.That(_gameObject.transform.position, Is.EqualTo(Vector3.zero),
                "root commands must not move the avatar through this controller");
        }

        [Test]
        public void ExplicitCancel_ReleasesOnlyNamedAnchor()
        {
            int releases = 0;
            _controller.TargetReleased += _ => releases++;
            _controller.ApplyTarget("hand_r", Vector3.one, 0f, true, 1d, 10d);
            _controller.ApplyTarget("pelvis", Vector3.zero, 0f, true, 1d, 10d);
            _controller.CancelTarget("hand_r");
            _controller.CancelTarget("hand_r");

            Assert.That(_controller.TryGetTarget("hand_r", out _), Is.False);
            Assert.That(_controller.TryGetTarget("pelvis", out _), Is.True);
            Assert.That(releases, Is.EqualTo(1));
        }

        [Test]
        public void Tick_NeverExpiresATargetByTimeAndCancelReleasesOnlyThatAnchor()
        {
            _controller.ApplyTarget("hand_l", Vector3.one, 0f, true, 1d, 10d);
            _controller.ApplyTarget("pelvis", Vector3.zero, 0f, true, 1d, 10.5d);
            _controller.Tick(10.75d);
            _controller.Tick(11.1d);
            _controller.Tick(1e9d);

            Assert.That(_controller.TryGetTarget("hand_l", out _), Is.True, "no time-based expiry");
            Assert.That(_controller.TryGetTarget("pelvis", out _), Is.True);

            _controller.CancelTarget("hand_l");
            Assert.That(_controller.TryGetTarget("hand_l", out _), Is.False);
            Assert.That(_controller.TryGetTarget("pelvis", out _), Is.True);
        }

        [Test]
        public void InvalidDuplicateOrOlderCommand_DoesNotOverwriteLatestTarget()
        {
            int applied = 0;
            _controller.TargetApplied += (_, _, _, _) => applied++;
            _controller.ApplyTarget("hand_l", new Vector3(1f, 2f, 3f), 10f, true, 0d, 10d);
            _controller.ApplyTarget("hand_l", new Vector3(2f, 2f, 3f), 10f, true, 1d, 11d);
            _controller.ApplyTarget("hand_l", new Vector3(9f, 9f, 9f), 20f, true, 0.8, 9d);
            _controller.ApplyTarget("hand_l", new Vector3(8f, 8f, 8f), 20f, true, 0.8, 11d);
            _controller.ApplyTarget("hand_l", new Vector3(float.NaN, 0f, 0f), 20f, true, 0.8, 11d);
            _controller.ApplyTarget("hand_l", Vector3.one, float.PositiveInfinity, true, 0.8, 11d);
            _controller.ApplyTarget("hand_l", Vector3.one, 20f, true, double.NaN, 11d);
            _controller.ApplyTarget("hand_l", Vector3.one, 20f, true, 0.8, double.NaN);
            _controller.ApplyTarget("hand_l", Vector3.one, 20f, true, -0.01d, 12d);
            _controller.ApplyTarget("hand_l", Vector3.one, 20f, true, 1.01d, 12d);

            Assert.That(_controller.TryGetTarget("hand_l", out var target), Is.True);
            Assert.That(target.Position, Is.EqualTo(new Vector3(2f, 2f, 3f)));
            Assert.That(target.Weight, Is.EqualTo(1d));
            Assert.That(target.UpdatedAt, Is.EqualTo(11d));
            Assert.That(applied, Is.EqualTo(2));
        }

        [Test]
        public void DisabledOrLateAfterCancelCommand_DoesNotReacquireTarget()
        {
            _controller.ApplyTarget("hand_l", Vector3.one, 0f, true, 0.5d, 10d);
            _controller.CancelTarget("hand_l");
            _controller.ApplyTarget("hand_l", Vector3.zero, 0f, true, 0.5d, 10d);
            _controller.ApplyTarget("hand_l", Vector3.zero, 0f, true, 0.5d, 9d);
            Assert.That(_controller.TryGetTarget("hand_l", out _), Is.False);

            _controller.enabled = false;
            _controller.ApplyTarget("hand_l", Vector3.zero, 0f, true, 0.5d, 11d);
            Assert.That(_controller.TryGetTarget("hand_l", out _), Is.False);
        }

        [Test]
        public void Tick_KeepsWorldGoalStableWhenExternalParentMoves()
        {
            _controller.SetGoalRoot(_externalGoalRoot.transform);
            Vector3 targetPosition = new Vector3(2f, 3f, 4f);
            _controller.ApplyTarget("pelvis", targetPosition, 30f, true, 1d, 10d);
            Transform goal = _controller.GetGoal("pelvis");

            _externalGoalRoot.transform.position = new Vector3(20f, 0f, 0f);
            _controller.Tick(10.1d);
            Assert.That(goal.position, Is.EqualTo(targetPosition));
        }
    }
}
#endif
