// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Contract = SpatialBlend.SbRetargetTransform.RealizationContract;
using Lease = SpatialBlend.SbRetargetTransform.RealizationLease;
using Result = SpatialBlend.SbRetargetTransform.RealizationResult;
using Sample = SpatialBlend.SbRetargetTransform.RealizedSample;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class CheckedRetargetRealizationTests
    {
        private GameObject _root;
        private SbRetargetTransform _retarget;
        private Lease _lease;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("checked-retarget-realization");
            _retarget = _root.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = Part("head");
            _retarget.leftHandTransform = Part("left");
            _retarget.rightHandTransform = Part("right");
            _retarget.SetHeadTransform(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            _lease?.Dispose();
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void LegacyRootAndPartialHeadBehaviorIsUnchangedWithoutAdmission()
        {
            _retarget.ApplyRootRealizationTransform(Vector3.one, Quaternion.Euler(15f, 30f, 4f));
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion body), Is.True);
            AssertRotation(body, Quaternion.Euler(15f, 30f, 4f));
            _retarget.SetAnchorTargetOverridesEnabled(true);
            Assert.That(_retarget.SetAnchorTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Pose(Vector3.one * 3f, Quaternion.Euler(0f, 90f, 0f)), .5f), Is.True);
            Assert.That(_retarget.headTransform.position, Is.EqualTo(Vector3.one * 2f));
            Assert.That(_retarget.headTransform.eulerAngles.y, Is.EqualTo(60f).Within(.001f));
        }

        [Test]
        public void ReadonlyUninitializedAndExistingBridgePathsDoNotAdvertiseAdmission()
        {
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.ReadOnlyOrAsynchronous, out _), Is.False);
            Assert.That(_retarget.TryAcquireCheckedRealization(null, Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            SetField("_hasRootPlacement", false);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            SetField("_hasRootPlacement", true);
            _retarget.SetAnchorTargetOverridesEnabled(true);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
        }

        public sealed class CheckedRealizationTrackingInput : SbTransformInputManager
        {
            public void InitializeProviders() => base.OnTrackingInitialized();
        }

        [Test]
        public void RootOnlyAdmissionRefusalPreservesSdkInputUntilGenuineHeadBaselineIsAvailable()
        {
            var child = new GameObject("root-only-retarget");
            child.transform.SetParent(_root.transform);
            var retarget = child.AddComponent<SbRetargetTransform>();
            retarget.headTransform = Part("root-only-head");
            retarget.ApplyRootRealizationTransform(Vector3.up, Quaternion.identity);
            var input = child.AddComponent<CheckedRealizationTrackingInput>();
            input.SbTransform = retarget;
            input.InitializeProviders();
            var provider = input.InputTrackingProvider;
            Assert.That(provider, Is.TypeOf<Oculus.Avatar2.OvrAvatarInputTrackingDelegatedProvider>());
            Assert.That(retarget.HasHeadObservation, Is.True);
            Assert.That(provider.GetInputTrackingState(out var before), Is.True);
            Assert.That(before.headsetActive, Is.True);
            Assert.That((Vector3)before.headset.position, Is.EqualTo(Vector3.up));

            Assert.That(retarget.TryAcquireCheckedRealization(new object(),
                Contract.SynchronousExclusiveRootAndHead, out Lease refused), Is.False);
            Assert.That(refused, Is.Null);
            Assert.That(retarget.HasHeadObservation, Is.True);
            Assert.That(provider.GetInputTrackingState(out var afterRefusal), Is.True);
            Assert.That(afterRefusal.headsetActive, Is.True);
            Assert.That((Vector3)afterRefusal.headset.position, Is.EqualTo((Vector3)before.headset.position));
            AssertRotation(afterRefusal.headset.orientation, before.headset.orientation);
            retarget.ApplyRootRealizationTransform(Vector3.forward, Quaternion.Euler(0f, 30f, 0f));
            Assert.That(provider.GetInputTrackingState(out var legacyMove), Is.True);
            Assert.That(legacyMove.headsetActive, Is.True);
            Assert.That((Vector3)legacyMove.headset.position, Is.EqualTo(Vector3.forward));

            retarget.SetHeadTransform(Vector3.up, Quaternion.identity);
            Assert.That(retarget.TryAcquireCheckedRealization(new object(),
                Contract.SynchronousExclusiveRootAndHead, out _lease), Is.True);
            Assert.That(retarget.HasHeadObservation, Is.True);
            Assert.That(_lease.TryMoveRoot(Vector3.forward * 2f, Quaternion.Euler(0f, 30f, 0f), Radians(30d)),
                Is.EqualTo(Result.Committed));
            Assert.That(provider.GetInputTrackingState(out var rootMove), Is.True);
            Assert.That(rootMove.headsetActive, Is.True);
            Assert.That((Vector3)rootMove.headset.position, Is.EqualTo(Vector3.forward * 2f));
            AssertRotation(rootMove.headset.orientation, retarget.headTransform.rotation);

            Assert.That(_lease.TryMoveHead(new Vector3(3f, 2f, 4f), Radians(40d)), Is.EqualTo(Result.Committed));
            Assert.That(provider.GetInputTrackingState(out var headMove), Is.True);
            Assert.That(headMove.headsetActive, Is.True);
            Assert.That((Vector3)headMove.headset.position, Is.EqualTo(new Vector3(3f, 2f, 4f)));
            AssertRotation(headMove.headset.orientation, retarget.headTransform.rotation);
        }

        [Test]
        public void RootMovesAndPlacementsCommitBothReadoutsWithoutRebasing()
        {
            _retarget.SetHeadTransform(Vector3.up, Quaternion.Euler(0f, 170f, 0f));
            Acquire();
            Sample initial = Read();
            Assert.That(initial.Revision, Is.Zero);
            foreach (float yaw in new[] { 190f, 350f, 510f })
            {
                double step = yaw == 190f ? 20d : 160d;
                Assert.That(_lease.TryMoveRoot(Vector3.up, Quaternion.Euler(0f, yaw, 0f), Radians(step)), Is.EqualTo(Result.Committed));
                Sample sample = Read();
                Assert.That(Degrees(sample.RootYawRadians), Is.EqualTo(yaw).Within(.002d));
                Assert.That(Degrees(sample.HeadYawRadians), Is.EqualTo(yaw).Within(.002d));
                AssertRotation(sample.RootPlacement.rotation, _retarget.headTransform.rotation);
            }
            Assert.That(_lease.TryPlaceRoot(Vector3.up * 2f, Quaternion.Euler(0f, 170f, 0f)), Is.EqualTo(Result.Committed));
            Sample placed = Read();
            Assert.That(placed.Revision, Is.EqualTo(4ul));
            Assert.That(placed.Incarnation, Is.EqualTo(initial.Incarnation));
            Assert.That(Degrees(placed.RootYawRadians), Is.EqualTo(530d).Within(.002d));
            Assert.That(placed.Head.position, Is.EqualTo(Vector3.up * 2f));
        }

        [Test]
        public void DirectHeadOwnReadbackCrossesMultipleTurnsAndPreservesPitchRoll()
        {
            Quaternion baseline = Quaternion.Euler(20f, 0f, 7f);
            _retarget.SetHeadTransform(Vector3.up, baseline);
            Acquire();
            for (int i = 1; i <= 5; ++i)
            {
                Assert.That(_lease.TryMoveHead(Vector3.up, Radians(90d)), Is.EqualTo(Result.Committed));
                Sample sample = Read();
                Assert.That(Degrees(sample.HeadYawRadians), Is.EqualTo(90d * i).Within(.003d));
                Assert.That(Degrees(sample.RootYawRadians), Is.EqualTo(0d).Within(.003d));
                AssertRotation(sample.Head.rotation, Quaternion.Euler(20f, 90f * i, 7f));
            }
        }

        [Test]
        public void RootCarryAndWorldLockedHeadUseDifferentMeasuredYawIncrements()
        {
            Acquire();
            Assert.That(_lease.TryMoveRoot(Vector3.forward, Quaternion.Euler(0f, 30f, 0f), Radians(30d)), Is.EqualTo(Result.Committed));
            Assert.That(Degrees(Read().HeadYawRadians), Is.EqualTo(30d).Within(.002d));
            Assert.That(_lease.TryMoveHead(new Vector3(3f, 2f, 4f), Radians(40d)), Is.EqualTo(Result.Committed));
            Assert.That(_lease.TryMoveRoot(Vector3.forward * 2f, Quaternion.Euler(0f, 50f, 0f), Radians(20d)), Is.EqualTo(Result.Committed));
            Sample locked = Read();
            Assert.That(Degrees(locked.RootYawRadians), Is.EqualTo(50d).Within(.002d));
            Assert.That(Degrees(locked.HeadYawRadians), Is.EqualTo(70d).Within(.002d));
            Assert.That(locked.Head.position, Is.EqualTo(new Vector3(3f, 2f, 4f)));
            Assert.That(_lease.TryReleaseHead(Radians(-20d)), Is.EqualTo(Result.Committed));
            Assert.That(_lease.TryMoveRoot(Vector3.forward * 3f, Quaternion.Euler(0f, 60f, 0f), Radians(10d)), Is.EqualTo(Result.Committed));
            Assert.That(Degrees(Read().HeadYawRadians), Is.EqualTo(60d).Within(.002d));
            Assert.That(Read().Head.position, Is.EqualTo(Vector3.forward * 3f));
        }

        [Test]
        public void AbsolutePlacementHalfTurnTiesChoosePositivePiInPersistentDomain()
        {
            Acquire();
            Assert.That(_lease.TryPlaceRoot(Vector3.up, Quaternion.Euler(0f, 180f, 0f)), Is.EqualTo(Result.Committed));
            Assert.That(Read().RootYawRadians, Is.EqualTo(Math.PI).Within(.00001d));
            Assert.That(_lease.TryPlaceRoot(Vector3.up, Quaternion.identity), Is.EqualTo(Result.Committed));
            Assert.That(Read().RootYawRadians, Is.EqualTo(2d * Math.PI).Within(.00001d));
            Assert.That(Read().HeadYawRadians, Is.EqualTo(2d * Math.PI).Within(.00001d));
        }

        [Test]
        public void AbsoluteDirectHeadPlacementsKeepWindingAndPositiveHalfTurnTie()
        {
            Acquire();
            Assert.That(_lease.TryPlaceHead(Vector3.one, -Math.PI), Is.EqualTo(Result.Committed));
            Assert.That(Read().HeadYawRadians, Is.EqualTo(Math.PI).Within(.00001d));
            Assert.That(_lease.TryPlaceHead(Vector3.one * 2f, 0d), Is.EqualTo(Result.Committed));
            Sample placed = Read();
            Assert.That(placed.HeadYawRadians, Is.EqualTo(2d * Math.PI).Within(.00001d));
            Assert.That(placed.RootYawRadians, Is.EqualTo(0d));
            Assert.That(placed.Head.position, Is.EqualTo(Vector3.one * 2f));
            Assert.That(_lease.TryMoveHead(Vector3.one * 2f, Radians(90d)), Is.EqualTo(Result.Committed));
            Assert.That(Degrees(Read().HeadYawRadians), Is.EqualTo(450d).Within(.003d));
        }

        [Test]
        public void RefusedAbsoluteHeadPlacementPreservesSampleAndBaselinePitchRoll()
        {
            _retarget.SetHeadTransform(Vector3.up, Quaternion.Euler(20f, 0f, 7f));
            Acquire();
            Sample before = Read();
            Assert.That(_lease.TryPlaceHead(new Vector3(float.NaN, 0f, 0f), Math.PI), Is.EqualTo(Result.InvalidInput));
            Assert.That(_lease.TryPlaceHead(Vector3.one, double.NaN), Is.EqualTo(Result.InvalidInput));
            AssertUnchanged(before);
            Assert.That(_lease.TryPlaceHead(Vector3.one, Radians(90d)), Is.EqualTo(Result.Committed));
            AssertRotation(Read().Head.rotation, Quaternion.Euler(20f, 90f, 7f));
        }

        [Test]
        public void RootPlacementObservesLockedHeadInsteadOfCopyingRootTurn()
        {
            Acquire();
            Assert.That(_lease.TryMoveHead(Vector3.up, Radians(45d)), Is.EqualTo(Result.Committed));
            Assert.That(_lease.TryPlaceRoot(Vector3.right, Quaternion.Euler(0f, 180f, 0f)), Is.EqualTo(Result.Committed));
            Assert.That(Degrees(Read().RootYawRadians), Is.EqualTo(180d).Within(.002d));
            Assert.That(Degrees(Read().HeadYawRadians), Is.EqualTo(45d).Within(.002d));
        }

        [TestCase(180d)]
        [TestCase(-180d)]
        [TestCase(360d)]
        public void AmbiguousOrdinaryRootAndHeadStepsAreRejectedBeforeMutation(double degrees)
        {
            Acquire();
            Sample before = Read();
            Assert.That(_lease.TryMoveRoot(Vector3.one, Quaternion.Euler(0f, (float)degrees, 0f), Radians(degrees)),
                Is.EqualTo(Result.AmbiguousMotion));
            Assert.That(_lease.TryMoveHead(Vector3.one, Radians(degrees)), Is.EqualTo(Result.AmbiguousMotion));
            AssertUnchanged(before);
        }

        [Test]
        public void InvalidAndIncongruentInputsDoNotAdvanceCommittedState()
        {
            Acquire();
            Sample before = Read();
            Assert.That(_lease.TryMoveRoot(Vector3.one, Quaternion.Euler(0f, 10f, 0f), 0d), Is.EqualTo(Result.InvalidInput));
            Assert.That(_lease.TryMoveRoot(Vector3.one, default, 0d), Is.EqualTo(Result.InvalidInput));
            Assert.That(_lease.TryMoveRoot(new Vector3(float.NaN, 0f, 0f), Quaternion.identity, 0d), Is.EqualTo(Result.InvalidInput));
            Assert.That(_lease.TryMoveHead(Vector3.one, double.PositiveInfinity), Is.EqualTo(Result.InvalidInput));
            Assert.That(_lease.TryPlaceRoot(Vector3.one, Quaternion.Euler(90f, 0f, 0f)), Is.EqualTo(Result.InvalidInput));
            AssertUnchanged(before);
        }

        [Test]
        public void ReentryReturnsBusyWithoutAcknowledgingOrWritingAnotherCommit()
        {
            Acquire();
            Sample before = Read();
            SetField("_realizationCommitInProgress", true);
            try
            {
                Assert.That(_lease.TryMoveRoot(Vector3.one, Quaternion.identity, 0d), Is.EqualTo(Result.Busy));
                Assert.That(_lease.TryMoveHead(Vector3.one, 0d), Is.EqualTo(Result.Busy));
                Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Busy));
            }
            finally { SetField("_realizationCommitInProgress", false); }
            AssertUnchanged(before);
        }

        [Test]
        public void AlreadyDisabledOverridesPreserveDirectLegacyHeadAndHandWrites()
        {
            _retarget.ApplyRootRealizationTransform(new Vector3(2f, 0f, 3f), Quaternion.identity);
            Assert.That(_retarget.SetHeadObservation(new Vector3(2.2f, 1.6f, 3.1f), Quaternion.identity), Is.True);
            _retarget.SetAnchorTargetOverridesEnabled(true);
            Assert.That(_retarget.SetLeftHandObservation(Vector3.left, Quaternion.identity), Is.True);
            Assert.That(_retarget.SetRightHandObservation(Vector3.right, Quaternion.identity), Is.True);
            _retarget.SetAnchorTargetOverridesEnabled(false);
            var head = new Pose(new Vector3(9f, 2f, 7f), Quaternion.Euler(13f, 70f, 6f));
            var left = new Pose(Vector3.one * 4f, Quaternion.Euler(2f, 30f, 5f));
            var right = new Pose(Vector3.one * 6f, Quaternion.Euler(3f, 40f, 7f));
            _retarget.headTransform.SetPositionAndRotation(head.position, head.rotation);
            _retarget.leftHandTransform.SetPositionAndRotation(left.position, left.rotation);
            _retarget.rightHandTransform.SetPositionAndRotation(right.position, right.rotation);
            _retarget.SetAnchorTargetOverridesEnabled(false);
            _retarget.SetAnchorTargetOverridesEnabled(false);
            AssertPose(_retarget.headTransform, head);
            AssertPose(_retarget.leftHandTransform, left);
            AssertPose(_retarget.rightHandTransform, right);
            Vector3 observed = head.position + head.rotation * Vector3.left;
            Assert.That(_retarget.SetLeftHandObservation(observed, head.rotation), Is.True);
            AssertPose(_retarget.leftHandTransform, new Pose(observed, head.rotation));
            AssertPose(_retarget.headTransform, head);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(),
                Contract.SynchronousExclusiveRootAndHead, out _), Is.False,
                "a repeated disable must not snap a foreign direct pose back to cached observation state");
        }

        [Test]
        public void LegacyDisableCannotRewriteForeignPoseOrHandsWhileCheckedOwnerIsHeld()
        {
            _retarget.ApplyRootRealizationTransform(new Vector3(2f, 0f, 3f), Quaternion.identity);
            Assert.That(_retarget.SetHeadObservation(new Vector3(2.2f, 1.6f, 3.1f), Quaternion.identity), Is.True);
            Acquire();
            Assert.That(_lease.TryMoveHead(Vector3.one * 5f, Radians(60d)), Is.EqualTo(Result.Committed));
            Assert.That(_retarget.SetLeftHandObservation(Vector3.left, Quaternion.identity), Is.True);
            Assert.That(_retarget.SetRightHandObservation(Vector3.right, Quaternion.identity), Is.True);
            var left = new Pose(_retarget.leftHandTransform.position, _retarget.leftHandTransform.rotation);
            var right = new Pose(_retarget.rightHandTransform.position, _retarget.rightHandTransform.rotation);
            var external = new Pose(Vector3.one * 9f, Quaternion.Euler(13f, 20f, 7f));
            _retarget.headTransform.SetPositionAndRotation(external.position, external.rotation);
            _retarget.SetAnchorTargetOverridesEnabled(false);
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
            AssertPose(_retarget.headTransform, external);
            AssertPose(_retarget.leftHandTransform, left);
            AssertPose(_retarget.rightHandTransform, right);
            _lease.Dispose();
            _lease.Dispose();
            AssertPose(_retarget.headTransform, external);
            AssertPose(_retarget.leftHandTransform, left);
            AssertPose(_retarget.rightHandTransform, right);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(),
                Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
        }

        private static void AssertPose(Transform actual, Pose expected)
        {
            Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThan(.00001f));
            AssertRotation(actual.rotation, expected.rotation);
        }

        [Test]
        public void OwnerReplacementMakesOldLeaseStaleAndStartsNewIncarnation()
        {
            Acquire();
            Lease old = _lease;
            Sample before = Read();
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            old.Dispose();
            Acquire();
            Assert.That(Read().Incarnation, Is.GreaterThan(before.Incarnation));
            Assert.That(old.TryRead(out _), Is.EqualTo(Result.StaleLease));
            Assert.That(old.TryPlaceRoot(Vector3.one, Quaternion.identity), Is.EqualTo(Result.StaleLease));
            Assert.That(Read().Revision, Is.Zero);
        }

        [Test]
        public void CleanDisposalWithdrawsIndependentHeadThroughEstablishedBaselineBeforeNewLease()
        {
            _retarget.ApplyRootRealizationTransform(new Vector3(2f, 0f, 3f), Quaternion.identity);
            Assert.That(_retarget.SetHeadObservation(new Vector3(2.2f, 1.6f, 3.1f), Quaternion.identity), Is.True);
            Acquire();
            Sample baseline = Read();
            Lease old = _lease;
            Assert.That(old.TryMoveHead(Vector3.one * 5f, Radians(60d)), Is.EqualTo(Result.Committed));
            Assert.That(Read().Head.position, Is.Not.EqualTo(baseline.Head.position));
            old.Dispose();
            Assert.That(_retarget.headTransform.position, Is.EqualTo(baseline.Head.position));
            AssertRotation(_retarget.headTransform.rotation, baseline.Head.rotation);
            Acquire();
            Assert.That(Read().Incarnation, Is.GreaterThan(baseline.Incarnation));
            Assert.That(Read().Head.position, Is.EqualTo(baseline.Head.position));
            Assert.That(old.TryRead(out _), Is.EqualTo(Result.StaleLease));
        }

        [Test]
        public void CleanDisposalUsesLatestRetainedRootOnceWithOffsetPitchRollAndIndependentFacing()
        {
            var root = new Pose(new Vector3(2f, 0f, 3f), Quaternion.Euler(17f, 25f, 9f));
            _retarget.ApplyRootRealizationTransform(root.position, root.rotation);
            Assert.That(_retarget.SetHeadObservation(
                root.position + root.rotation * new Vector3(.2f, 1.6f, .1f), root.rotation), Is.True);
            Acquire();
            Sample initial = Read();
            Vector3 localOffset = Quaternion.Inverse(initial.RootPlacement.rotation) *
                (initial.Head.position - initial.RootPlacement.position);
            Lease old = _lease;
            Assert.That(old.TryMoveHead(Vector3.one * 5f, Radians(60d)), Is.EqualTo(Result.Committed));
            var movedRoot = new Pose(new Vector3(4f, 0f, -2f), Quaternion.Euler(17f, 55f, 9f));
            Assert.That(old.TryMoveRoot(movedRoot.position, movedRoot.rotation, Radians(30d)),
                Is.EqualTo(Result.Committed));
            Sample targeted = Read();
            Assert.That(Mathf.DeltaAngle(targeted.Head.rotation.eulerAngles.y, 85f),
                Is.EqualTo(0f).Within(.001f), "Facing stays independent of the later root turn");
            AssertRotation(targeted.Head.rotation, Quaternion.Euler(17f, 85f, 9f));
            Vector3 expectedHead = targeted.RootPlacement.position +
                targeted.RootPlacement.rotation * localOffset;

            old.Dispose();
            Assert.That(Vector3.Distance(_retarget.headTransform.position, expectedHead), Is.LessThan(.00001f));
            AssertRotation(_retarget.headTransform.rotation, targeted.RootPlacement.rotation);
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion retainedRoot), Is.True);
            AssertRotation(retainedRoot, targeted.RootPlacement.rotation);
            Acquire();
            Sample fresh = Read();
            Assert.That(fresh.Incarnation, Is.GreaterThan(initial.Incarnation));
            Assert.That(fresh.Revision, Is.Zero);
            Assert.That(fresh.RootPlacement.position, Is.EqualTo(targeted.RootPlacement.position));
            AssertRotation(fresh.RootPlacement.rotation, targeted.RootPlacement.rotation);
            Assert.That(Vector3.Distance(fresh.Head.position, expectedHead), Is.LessThan(.00001f));
            AssertRotation(fresh.Head.rotation, targeted.RootPlacement.rotation);
            Assert.That(fresh.HeadYawRadians, Is.EqualTo(fresh.RootYawRadians).Within(.00001d));
            Assert.That(old.TryRead(out _), Is.EqualTo(Result.StaleLease));
            old.Dispose();
            AssertUnchanged(fresh);

            var nextRoot = new Pose(new Vector3(-1f, 0f, 2f), Quaternion.Euler(17f, 75f, 9f));
            Assert.That(_lease.TryMoveRoot(nextRoot.position, nextRoot.rotation, Radians(20d)),
                Is.EqualTo(Result.Committed));
            Sample next = Read();
            Vector3 nextHead = next.RootPlacement.position + next.RootPlacement.rotation * localOffset;
            Assert.That(Vector3.Distance(next.Head.position, nextHead), Is.LessThan(.00001f),
                "fresh ownership reuses the original observation offset, not a rebased or doubled offset");
            AssertRotation(next.Head.rotation, next.RootPlacement.rotation);
            _lease.Dispose(); // No head target: clean release must not compose the offset a second time.
            Assert.That(Vector3.Distance(_retarget.headTransform.position, nextHead), Is.LessThan(.00001f));
            AssertRotation(_retarget.headTransform.rotation, next.RootPlacement.rotation);
            Acquire();
            Assert.That(Vector3.Distance(Read().Head.position, nextHead), Is.LessThan(.00001f));
        }

        [Test]
        public void DisposalDetectsUnreportedForeignMutationBeforeAnyBaselineWrite()
        {
            Acquire();
            Assert.That(_lease.TryMoveHead(Vector3.one, Radians(60d)), Is.EqualTo(Result.Committed));
            var external = new Pose(Vector3.one * 9f, Quaternion.Euler(13f, 20f, 7f));
            _retarget.headTransform.SetPositionAndRotation(external.position, external.rotation);
            _lease.Dispose(); // No preceding TryRead: disposal must perform its own ownership check.
            Assert.That(_retarget.headTransform.position, Is.EqualTo(external.position));
            AssertRotation(_retarget.headTransform.rotation, external.rotation);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(),
                Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
        }

        [Test]
        public void InvalidatedDisposalDoesNotRewriteForeignHeadToForceBaselineAdmission()
        {
            Acquire();
            Assert.That(_lease.TryMoveHead(Vector3.one, Radians(60d)), Is.EqualTo(Result.Committed));
            Pose external = new Pose(Vector3.one * 9f, Quaternion.Euler(0f, 20f, 0f));
            _retarget.headTransform.SetPositionAndRotation(external.position, external.rotation);
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
            _lease.Dispose();
            Assert.That(_retarget.headTransform.position, Is.EqualTo(external.position));
            AssertRotation(_retarget.headTransform.rotation, external.rotation);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
        }

        [TestCase("placement")]
        [TestCase("root")]
        [TestCase("observation")]
        [TestCase("clear")]
        [TestCase("target")]
        public void LegacyOrAsynchronousHeadMutationInvalidatesLeaseWithoutWriting(string operation)
        {
            Acquire();
            Pose before = new Pose(_retarget.headTransform.position, _retarget.headTransform.rotation);
            switch (operation)
            {
                case "placement": _retarget.SetHeadTransform(Vector3.one, Quaternion.Euler(0f, 90f, 0f)); break;
                case "root": _retarget.ApplyRootRealizationTransform(Vector3.one, Quaternion.Euler(0f, 90f, 0f)); break;
                case "observation": Assert.That(_retarget.SetHeadObservation(Vector3.one, Quaternion.identity), Is.False); break;
                case "clear": _retarget.ClearHeadObservation(); break;
                case "target": Assert.That(_retarget.SetAnchorTarget("head", new Pose(Vector3.one, Quaternion.identity), 1f), Is.False); break;
            }
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
            Assert.That(_retarget.headTransform.position, Is.EqualTo(before.position));
            AssertRotation(_retarget.headTransform.rotation, before.rotation);
        }

        [Test]
        public void ExternalTransformMutationIsNotGuessedIntoContinuousState()
        {
            Acquire();
            _retarget.headTransform.rotation = Quaternion.Euler(0f, 1f, 0f);
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
            Assert.That(_lease.TryMoveHead(Vector3.one, 0d), Is.EqualTo(Result.Invalidated));
        }

        [TestCase("parent")]
        [TestCase("head")]
        public void TransformIdentityChangesInvalidateEvenWhenWorldPoseIsUnchanged(string operation)
        {
            Acquire();
            Pose before = Read().Head;
            if (operation == "parent")
            {
                _retarget.headTransform.SetParent(Part("new-parent"), true);
            }
            else
            {
                Transform replacement = Part("replacement-head");
                replacement.SetPositionAndRotation(before.position, before.rotation);
                _retarget.headTransform = replacement;
            }
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
            Assert.That(_lease.TryPlaceRoot(Vector3.one, Quaternion.identity), Is.EqualTo(Result.Invalidated));
            Assert.That(_retarget.headTransform.position, Is.EqualTo(before.position));
        }

        [Test]
        public void UnrelatedHandObservationDoesNotAdvanceRootOrHeadRevision()
        {
            Acquire();
            Sample before = Read();
            Assert.That(_retarget.SetLeftHandObservation(Vector3.left, Quaternion.identity), Is.True);
            Assert.That(_retarget.leftHandTransform.position, Is.EqualTo(Vector3.left));
            AssertUnchanged(before);
        }

        [Test]
        public void RevisionExhaustionInvalidatesBeforeMutation()
        {
            Acquire();
            _lease.Revision = ulong.MaxValue;
            Pose before = Read().Head;
            Assert.That(_lease.TryMoveHead(Vector3.one, 0d), Is.EqualTo(Result.Invalidated));
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
            Assert.That(_retarget.headTransform.position, Is.EqualTo(before.position));
            AssertRotation(_retarget.headTransform.rotation, before.rotation);
        }

        [Test]
        public void HandAliasChangesAreRejectedBeforeHandWriterCanMoveHead()
        {
            Acquire();
            Pose before = Read().Head;
            _retarget.leftHandTransform = _retarget.headTransform;
            Assert.That(_retarget.SetLeftHandObservation(Vector3.one, Quaternion.Euler(0f, 90f, 0f)), Is.False);
            Assert.That(_retarget.headTransform.position, Is.EqualTo(before.position));
            AssertRotation(_retarget.headTransform.rotation, before.rotation);
            Assert.That(_lease.TryRead(out _), Is.EqualTo(Result.Invalidated));
        }

        [Test]
        public void NoRetargetAndGenericSubclassDoNotAdvertiseCheckedCapability()
        {
            var entity = _root.AddComponent<SbNetworkAvatarEntity>();
            Assert.That(entity.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
            var child = new GameObject("generic-retarget");
            child.transform.SetParent(_root.transform);
            var generic = child.AddComponent<UnadmittedRetarget>();
            generic.headTransform = Part("generic-head");
            generic.SetHeadTransform(Vector3.up, Quaternion.identity);
            Assert.That(generic.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _), Is.False);
        }

        private void Acquire()
        {
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(), Contract.SynchronousExclusiveRootAndHead, out _lease), Is.True);
        }

        private Sample Read()
        {
            Assert.That(_lease.TryRead(out Sample sample), Is.EqualTo(Result.Committed));
            return sample;
        }

        private void AssertUnchanged(Sample before)
        {
            Sample after = Read();
            Assert.That(after.Revision, Is.EqualTo(before.Revision));
            Assert.That(after.RootYawRadians, Is.EqualTo(before.RootYawRadians));
            Assert.That(after.HeadYawRadians, Is.EqualTo(before.HeadYawRadians));
            Assert.That(after.Head.position, Is.EqualTo(before.Head.position));
            AssertRotation(after.Head.rotation, before.Head.rotation);
        }

        private Transform Part(string name)
        {
            var part = new GameObject(name);
            part.transform.SetParent(_root.transform);
            return part.transform;
        }

        private void SetField(string name, object value) =>
            typeof(SbRetargetTransform).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_retarget, value);

        private static double Radians(double degrees) => degrees * Math.PI / 180d;
        private static double Degrees(double radians) => radians * 180d / Math.PI;
        private static void AssertRotation(Quaternion actual, Quaternion expected) =>
            Assert.That(Quaternion.Angle(actual, expected), Is.LessThan(.05f));
    }

    public sealed class UnadmittedRetarget : SbRetargetTransform { }
}
#endif
