// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Motion;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Exercises the production tracked-input seam, not a goal-only controller.
    /// These tests intentionally use no skeleton/mesh: calibrated articulated rig
    /// realization remains outside this head/hand input adapter.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SbRetargetAnchorTargetBridgeTests
    {
        private GameObject _root;
        private AnchorTargetController _controller;
        private SbRetargetTransform _retarget;
        private SbRetargetAnchorTargetBridge _bridge;
        private SbTransformInputManager _input;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("retarget-anchor-bridge");
            _controller = _root.AddComponent<AnchorTargetController>();
            _retarget = _root.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = NewPart("head");
            _retarget.leftHandTransform = NewPart("left");
            _retarget.rightHandTransform = NewPart("right");
            _bridge = _root.AddComponent<SbRetargetAnchorTargetBridge>();
            _bridge.Configure(_controller, _retarget);
            _input = _root.AddComponent<SbTransformInputManager>();
            _input.SbTransform = _retarget;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void OneHandTarget_PreservesFreshHeadAndOtherHandBaselinesInDelegateOutput()
        {
            SetBaseline(new Vector3(2f, 1f, 3f), Quaternion.Euler(0f, 90f, 0f),
                new Vector3(-.3f, 0f, .2f), new Vector3(.4f, 0f, .1f));
            Vector3 expectedRight = _retarget.rightHandTransform.position;

            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                new Vector3(8f, 2f, -1f), 45f, true, 1d, 1d);
            _retarget.SetHeadTransform(new Vector3(3f, 1.5f, 4f), Quaternion.identity);
            _retarget.SetHandTransform(new Vector3(-.6f, 0f, .1f),
                new Vector3(.1f, .2f, .3f), Quaternion.identity, Quaternion.identity, 1f);

            AssertPose(_retarget.leftHandTransform, new Vector3(8f, 2f, -1f));
            AssertPose(_retarget.headTransform, new Vector3(3f, 1.5f, 4f));
            Assert.That(Vector3.Distance(_retarget.rightHandTransform.position, expectedRight),
                Is.GreaterThan(.01f), "right input must continue to accept fresh baselines");

            Assert.That(HeadsetActive(), Is.True);
            Assert.That(LeftControllerActive(), Is.True);
            Assert.That(RightControllerActive(), Is.True);
            AssertPose(LeftControllerPosition(), new Vector3(8f, 2f, -1f));
            AssertPose(HeadsetPosition(), new Vector3(3f, 1.5f, 4f));
        }

        [Test]
        public void IndependentHeadAndHandTargets_UseSeparateWorldChannels()
        {
            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Vector3(5f, 2f, 1f), 90f, true, 1d, 1d);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                new Vector3(-2f, 3f, 4f), 20f, true, 1d, 2d);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.RightHandAnchor,
                new Vector3(2f, 4f, -3f), -30f, true, 1d, 3d);

            AssertPose(_retarget.headTransform, new Vector3(5f, 2f, 1f));
            AssertPose(_retarget.leftHandTransform, new Vector3(-2f, 3f, 4f));
            AssertPose(_retarget.rightHandTransform, new Vector3(2f, 4f, -3f));
            Assert.That(Quaternion.Angle(_retarget.headTransform.rotation,
                Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(.01f));
        }

        [Test]
        public void PartialWeight_ReappliesAgainstBaselineInsteadOfFeedingBackOutput()
        {
            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Vector3(10f, 0f, 0f), 0f, true, .5d, 1d);
            AssertPose(_retarget.headTransform, new Vector3(5f, 0f, 0f));

            // A fresh C command with the same partial target must remain halfway,
            // rather than drifting to 7.5 through output-as-baseline feedback.
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Vector3(10f, 0f, 0f), 0f, true, .5d, 2d);
            AssertPose(_retarget.headTransform, new Vector3(5f, 0f, 0f));
        }

        [Test]
        public void CancelReturnsToLatestBaselineNotCachedOverrideAndNothingExpiresByTime()
        {
            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                new Vector3(9f, 0f, 0f), 0f, true, 1d, 1d);
            _retarget.SetHandTransform(new Vector3(-3f, 0f, 0f), Vector3.right,
                Quaternion.identity, Quaternion.identity, 1f);
            AssertPose(_retarget.leftHandTransform, new Vector3(9f, 0f, 0f));
            _controller.CancelTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor);
            AssertPose(_retarget.leftHandTransform, new Vector3(-3f, 0f, 0f));

            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                new Vector3(7f, 0f, 0f), 0f, true, 1d, 2d);
            _retarget.SetHandTransform(new Vector3(-4f, 0f, 0f), Vector3.right,
                Quaternion.identity, Quaternion.identity, 1f);
            _controller.Tick(1e9d);
            AssertPose(_retarget.leftHandTransform, new Vector3(7f, 0f, 0f));
            _controller.CancelTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor);
            AssertPose(_retarget.leftHandTransform, new Vector3(-4f, 0f, 0f));
        }

        [Test]
        public void Rebind_IgnoresStaleControllerAndReplaysCurrentReplacementTarget()
        {
            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            var replacementObject = new GameObject("replacement-controller");
            var replacement = replacementObject.AddComponent<AnchorTargetController>();
            try
            {
                _controller.ApplyTarget(SbRetargetAnchorTargetBridge.RightHandAnchor,
                    new Vector3(3f, 0f, 0f), 0f, true, 1d, 1d);
                _bridge.Configure(replacement, _retarget);
                _controller.ApplyTarget(SbRetargetAnchorTargetBridge.RightHandAnchor,
                    new Vector3(8f, 0f, 0f), 0f, true, 1d, 2d);
                AssertPose(_retarget.rightHandTransform, Vector3.right);

                replacement.ApplyTarget(SbRetargetAnchorTargetBridge.RightHandAnchor,
                    new Vector3(4f, 0f, 0f), 0f, true, 1d, 1d);
                AssertPose(_retarget.rightHandTransform, new Vector3(4f, 0f, 0f));
            }
            finally
            {
                Object.DestroyImmediate(replacementObject);
            }
        }

        [Test]
        public void MissingObservations_AreInactiveUntilTrackedBaselineArrives()
        {
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Vector3(4f, 2f, 0f), 0f, true, 1d, 1d);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                new Vector3(-4f, 2f, 0f), 0f, true, 1d, 2d);

            Assert.That(HeadsetActive(), Is.False);
            Assert.That(LeftControllerActive(), Is.False);
            Assert.That(RightControllerActive(), Is.False);

            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            Assert.That(HeadsetActive(), Is.True);
            Assert.That(LeftControllerActive(), Is.True);
            Assert.That(RightControllerActive(), Is.True);
            AssertPose(HeadsetPosition(), new Vector3(4f, 2f, 0f));
            AssertPose(LeftControllerPosition(), new Vector3(-4f, 2f, 0f));

            _retarget.ClearLeftHandObservation();
            _controller.CancelTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor);
            Assert.That(HeadsetActive(), Is.True);
            Assert.That(LeftControllerActive(), Is.False,
                "a released target must not turn a missing observation back on");
            Assert.That(RightControllerActive(), Is.True);

            _retarget.SetHeadTransform(new Vector3(float.NaN, 0f, 0f), Quaternion.identity);
            Assert.That(HeadsetActive(), Is.False,
                "invalid input is loss of observation, not an identity headset sample");
        }

        [Test]
        public void HeightlessTarget_IsRejectedWithoutInventingAnOriginHeight()
        {
            SetBaseline(new Vector3(1f, 1.7f, 2f), Quaternion.identity,
                Vector3.left, Vector3.right);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Vector3(8f, 0f, 0f), 0f, false, 1d, 1d);

            AssertPose(_retarget.headTransform, new Vector3(1f, 1.7f, 2f));
            AssertPose(HeadsetPosition(), new Vector3(1f, 1.7f, 2f));
        }

        [Test]
        public void RootRealization_ComposesLatestPlacementThroughActiveAndReleasedChannels()
        {
            SetBaseline(Vector3.zero, Quaternion.identity,
                Vector3.left, Vector3.right);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                new Vector3(6f, 2f, 1f), 0f, true, 1d, 1d);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                new Vector3(4f, 2f, 1f), 0f, true, 1d, 2d);
            _retarget.ApplyRootRealizationTransform(new Vector3(10f, 0f, 0f),
                Quaternion.identity);
            _retarget.SetHandTransform(new Vector3(-2f, 0f, 0f),
                new Vector3(2f, 0f, 0f), Quaternion.identity, Quaternion.identity, 1f);
            AssertPose(_retarget.headTransform, new Vector3(6f, 2f, 1f));
            AssertPose(_retarget.leftHandTransform, new Vector3(4f, 2f, 1f));
            // The Fusion hand samples are headset-relative, so the untargeted right hand is composed on the actual
            // visible head (the head target at (6, 2, 1)), not on the body root (CurrentHandPlacement).
            AssertPose(_retarget.rightHandTransform, new Vector3(8f, 2f, 1f));

            _controller.CancelTarget(SbRetargetAnchorTargetBridge.HeadAnchor);
            _controller.CancelTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor);
            AssertPose(_retarget.headTransform, new Vector3(10f, 0f, 0f));
            AssertPose(_retarget.leftHandTransform, new Vector3(8f, 0f, 0f));
            AssertPose(_retarget.rightHandTransform, new Vector3(12f, 0f, 0f));
        }

        [Test]
        public void StreamedHandsFollowActualHeadFrameAcrossFacingChangesWithoutPackets()
        {
            var sourceHead = new Pose(new Vector3(2f, 1.6f, -3f), Quaternion.Euler(17f, 25f, 9f));
            Vector3 leftRelative = new Vector3(-.35f, .15f, .45f);
            Vector3 rightRelative = new Vector3(.4f, -.1f, .3f);
            Quaternion leftRelativeRotation = Quaternion.Euler(12f, -18f, 7f);
            Quaternion rightRelativeRotation = Quaternion.Euler(-8f, 22f, -5f);

            _retarget.SetHeadTransform(sourceHead.position, sourceHead.rotation);
            _retarget.SetHandTransform(leftRelative, rightRelative, leftRelativeRotation,
                rightRelativeRotation, 1f);
            AssertStreamedHand(_retarget.leftHandTransform, sourceHead, leftRelative,
                leftRelativeRotation);
            AssertStreamedHand(_retarget.rightHandTransform, sourceHead, rightRelative,
                rightRelativeRotation);

            _retarget.SetAnchorTargetOverridesEnabled(true);
            var firstFacingTarget = new Pose(new Vector3(8f, 3f, 4f), Quaternion.Euler(0f, 80f, 0f));
            Assert.That(_retarget.SetAnchorTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                firstFacingTarget, 1f), Is.True);
            var firstRealizedHead = new Pose(firstFacingTarget.position, Quaternion.Euler(17f, 80f, 9f));
            AssertPose(_retarget.headTransform, firstRealizedHead.position);
            AssertRotation(_retarget.headTransform.rotation, firstRealizedHead.rotation);
            AssertStreamedHand(_retarget.rightHandTransform, firstRealizedHead, rightRelative,
                rightRelativeRotation);
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion bodyRotation), Is.True);
            AssertRotation(bodyRotation, sourceHead.rotation);

            var leftTarget = new Pose(new Vector3(-6f, 5f, 2f), Quaternion.Euler(0f, -35f, 0f));
            Assert.That(_retarget.SetAnchorTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                leftTarget, .4f), Is.True);
            AssertBlendedHand(_retarget.leftHandTransform, firstRealizedHead, leftRelative,
                leftRelativeRotation, leftTarget, .4f);

            var secondFacingTarget = new Pose(new Vector3(-4f, 2.5f, 7f), Quaternion.Euler(0f, -40f, 0f));
            Assert.That(_retarget.SetAnchorTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                secondFacingTarget, 1f), Is.True);
            var secondRealizedHead = new Pose(secondFacingTarget.position, Quaternion.Euler(17f, -40f, 9f));
            AssertPose(_retarget.headTransform, secondRealizedHead.position);
            AssertRotation(_retarget.headTransform.rotation, secondRealizedHead.rotation);
            AssertBlendedHand(_retarget.leftHandTransform, secondRealizedHead, leftRelative,
                leftRelativeRotation, leftTarget, .4f);
            AssertStreamedHand(_retarget.rightHandTransform, secondRealizedHead, rightRelative,
                rightRelativeRotation);

            Assert.That(_retarget.ClearAnchorTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor), Is.True);
            AssertStreamedHand(_retarget.leftHandTransform, secondRealizedHead, leftRelative,
                leftRelativeRotation);
            Assert.That(_retarget.ClearAnchorTarget(SbRetargetAnchorTargetBridge.HeadAnchor), Is.True);
            AssertPose(_retarget.headTransform, sourceHead.position);
            AssertRotation(_retarget.headTransform.rotation, sourceHead.rotation);
            AssertStreamedHand(_retarget.leftHandTransform, sourceHead, leftRelative,
                leftRelativeRotation);
            AssertStreamedHand(_retarget.rightHandTransform, sourceHead, rightRelative,
                rightRelativeRotation);
        }

        [Test]
        public void CheckedLeaseDisposalRecomposesStreamedHandsAfterFacingWithoutNewPacket()
        {
            _bridge.enabled = false;
            InvokeEditModeLifecycle(_bridge, "OnDisable");
            var root = new Pose(new Vector3(2f, 0f, 3f), Quaternion.Euler(17f, 25f, 9f));
            var head = new Pose(root.position + root.rotation * new Vector3(.2f, 1.6f, .1f),
                root.rotation);
            Vector3 leftRelative = new Vector3(-.35f, .15f, .45f);
            Vector3 rightRelative = new Vector3(.4f, -.1f, .3f);
            Quaternion leftRelativeRotation = Quaternion.Euler(12f, -18f, 7f);
            Quaternion rightRelativeRotation = Quaternion.Euler(-8f, 22f, -5f);

            _retarget.ApplyRootRealizationTransform(root.position, root.rotation);
            Assert.That(_retarget.SetHeadObservation(head.position, head.rotation), Is.True);
            Assert.That(_retarget.TryAcquireCheckedRealization(new object(),
                SbRetargetTransform.RealizationContract.SynchronousExclusiveRootAndHead,
                out SbRetargetTransform.RealizationLease lease), Is.True);
            _retarget.SetHandTransform(leftRelative, rightRelative, leftRelativeRotation,
                rightRelativeRotation, 1f);
            AssertStreamedHand(_retarget.leftHandTransform, head, leftRelative, leftRelativeRotation);
            AssertStreamedHand(_retarget.rightHandTransform, head, rightRelative, rightRelativeRotation);

            var facing = new Pose(new Vector3(7f, 2f, -4f), Quaternion.Euler(17f, 75f, 9f));
            Assert.That(lease.TryMoveHead(facing.position, 50f * Mathf.Deg2Rad),
                Is.EqualTo(SbRetargetTransform.RealizationResult.Committed));
            AssertStreamedHand(_retarget.leftHandTransform, facing, leftRelative, leftRelativeRotation);
            AssertStreamedHand(_retarget.rightHandTransform, facing, rightRelative, rightRelativeRotation);

            lease.Dispose();

            AssertPose(_retarget.headTransform, head.position);
            AssertRotation(_retarget.headTransform.rotation, head.rotation);
            AssertStreamedHand(_retarget.leftHandTransform, head, leftRelative, leftRelativeRotation);
            AssertStreamedHand(_retarget.rightHandTransform, head, rightRelative, rightRelativeRotation);
            Assert.That(_retarget.TryGetBodyPlacement(out Quaternion retainedRoot), Is.True);
            AssertRotation(retainedRoot, root.rotation);
        }

        [Test]
        public void DisabledBridge_PreservesLegacyDirectRootAsLatestHandBasis()
        {
            _bridge.enabled = false;
            // This EditMode fixture does not automatically invoke MonoBehaviour
            // callbacks when enabled changes. Invoke the production callback to
            // assert its bridge-off contract; PlayMode lifecycle remains separate.
            InvokeEditModeLifecycle(_bridge, "OnDisable");
            _retarget.SetHeadTransform(new Vector3(1f, 0f, 0f), Quaternion.identity);
            AssertPose(_retarget.headTransform, new Vector3(1f, 0f, 0f),
                "the legacy public setter must write its supplied head pose");
            _retarget.SetHandTransform(Vector3.left, Vector3.right,
                Quaternion.identity, Quaternion.identity, 1f);
            Quaternion directRotation = Quaternion.Euler(0f, 90f, 0f);
            _retarget.headTransform.SetPositionAndRotation(new Vector3(10f, 0f, 0f),
                directRotation);
            _retarget.SetHandTransform(new Vector3(-2f, 0f, 0f),
                new Vector3(2f, 0f, 0f), Quaternion.identity, Quaternion.identity, 1f);

            AssertPose(_retarget.headTransform, new Vector3(10f, 0f, 0f));
            AssertPose(_retarget.leftHandTransform,
                new Vector3(10f, 0f, 0f) + directRotation * new Vector3(-2f, 0f, 0f));
            AssertPose(_retarget.rightHandTransform,
                new Vector3(10f, 0f, 0f) + directRotation * new Vector3(2f, 0f, 0f));
        }

        [Test]
        public void BridgeRootPlacement_RendersWithoutFabricatingHeadObservation()
        {
            // SetUp configures an enabled bridge but deliberately supplies no
            // tracked baseline. A root realization still has to be visible while
            // the SDK tracking state remains explicitly inactive.
            _retarget.ApplyRootRealizationTransform(new Vector3(4f, 1f, -2f),
                Quaternion.Euler(0f, 45f, 0f));

            AssertPose(_retarget.headTransform, new Vector3(4f, 1f, -2f));
            Assert.That(HeadsetActive(), Is.False);
        }

        [Test]
        public void HeightlessReplacement_ClearsPriorTargetInsteadOfRetainingIt()
        {
            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.RightHandAnchor,
                new Vector3(7f, 1f, 0f), 0f, true, 1d, 1d);
            AssertPose(_retarget.rightHandTransform, new Vector3(7f, 1f, 0f));

            _controller.ApplyTarget(SbRetargetAnchorTargetBridge.RightHandAnchor,
                new Vector3(99f, 0f, 0f), 0f, false, 1d, 2d);
            AssertPose(_retarget.rightHandTransform, Vector3.right);
        }

        [Test]
        public void ReentrantOldControllerCallback_CannotMutateReplacementBinding()
        {
            SetBaseline(Vector3.zero, Quaternion.identity, Vector3.left, Vector3.right);
            var replacementObject = new GameObject("replacement-controller");
            var replacement = replacementObject.AddComponent<AnchorTargetController>();
            try
            {
                // This subscriber is registered before the bridge. The old bridge
                // callback is still in the controller's invocation snapshot after
                // Configure runs, so only its source/generation fence can stop it.
                _bridge.enabled = false;
                _controller.TargetApplied += (_, _, _, _) => _bridge.Configure(replacement, _retarget);
                _bridge.enabled = true;
                _controller.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                    new Vector3(9f, 0f, 0f), 0f, true, 1d, 1d);
                AssertPose(_retarget.leftHandTransform, Vector3.left);

                replacement.ApplyTarget(SbRetargetAnchorTargetBridge.LeftHandAnchor,
                    new Vector3(3f, 0f, 0f), 0f, true, 1d, 1d);
                AssertPose(_retarget.leftHandTransform, new Vector3(3f, 0f, 0f));
            }
            finally
            {
                Object.DestroyImmediate(replacementObject);
            }
        }

        [Test]
        public void FreeformStopAndReadd_RebindsOwnedBridgeWithoutTouchingControllerIdentity()
        {
            var appObject = new GameObject("freeform-anchor-bridge-app");
            var avatar = new GameObject("remote-avatar") { tag = "Avatar" };
            avatar.AddComponent<SbLocalEntity>();
            var retarget = avatar.AddComponent<SbRetargetTransform>();
            retarget.headTransform = NewPartOn(avatar.transform, "head");
            retarget.leftHandTransform = NewPartOn(avatar.transform, "left");
            retarget.rightHandTransform = NewPartOn(avatar.transform, "right");
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                var controller = avatar.GetComponent<AnchorTargetController>();
                var bridge = avatar.GetComponent<SbRetargetAnchorTargetBridge>();
                Assert.That(controller, Is.Not.Null);
                Assert.That(bridge, Is.Not.Null);
                retarget.SetHeadTransform(Vector3.zero, Quaternion.identity);
                controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                    new Vector3(2f, 0f, 0f), 0f, true, 1d, 1d);
                AssertPose(retarget.headTransform, new Vector3(2f, 0f, 0f));

                app.OnAppStop();
                Assert.That(bridge.enabled, Is.False);
                AssertPose(retarget.headTransform, Vector3.zero);

                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                Assert.That(ReferenceEquals(controller,
                    avatar.GetComponent<AnchorTargetController>()), Is.True);
                Assert.That(bridge.enabled, Is.True);
                controller.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                    new Vector3(3f, 0f, 0f), 0f, true, 1d, 2d);
                AssertPose(retarget.headTransform, new Vector3(3f, 0f, 0f));
            }
            finally
            {
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void FreeformLifecycle_LeavesExternalBridgeControllerAndRetargetBindingUntouched()
        {
            var appObject = new GameObject("freeform-external-bridge-app");
            var avatar = new GameObject("external-bridge-avatar") { tag = "Avatar" };
            avatar.AddComponent<SbLocalEntity>();
            // The app discovers these local components. The external bridge is
            // intentionally configured to different components, so an old
            // unconditional Configure(discoveredController) call is observable.
            var discoveredController = avatar.AddComponent<AnchorTargetController>();
            var discoveredRetarget = avatar.AddComponent<SbRetargetTransform>();
            discoveredRetarget.headTransform = NewPartOn(avatar.transform, "discovered-head");
            discoveredRetarget.leftHandTransform = NewPartOn(avatar.transform, "discovered-left");
            discoveredRetarget.rightHandTransform = NewPartOn(avatar.transform, "discovered-right");
            discoveredRetarget.SetHeadTransform(Vector3.zero, Quaternion.identity);

            var sourceObject = new GameObject("external-anchor-source");
            var externalController = sourceObject.AddComponent<AnchorTargetController>();
            var retargetObject = new GameObject("external-retarget-consumer");
            var externalRetarget = retargetObject.AddComponent<SbRetargetTransform>();
            externalRetarget.headTransform = NewPartOn(retargetObject.transform, "external-head");
            externalRetarget.leftHandTransform = NewPartOn(retargetObject.transform, "external-left");
            externalRetarget.rightHandTransform = NewPartOn(retargetObject.transform, "external-right");
            var externalBridge = avatar.AddComponent<SbRetargetAnchorTargetBridge>();
            externalBridge.Configure(externalController, externalRetarget);
            externalRetarget.SetHeadTransform(Vector3.zero, Quaternion.identity);
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                externalController.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                    new Vector3(1f, 0f, 0f), 0f, true, 1d, 1d);
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                app.OnRemove(avatar);
                app.OnAppStop();
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);

                Assert.That(avatar.GetComponent<AnchorTargetController>(),
                    Is.SameAs(discoveredController));
                Assert.That(avatar.GetComponent<SbRetargetAnchorTargetBridge>(),
                    Is.SameAs(externalBridge));
                Assert.That(externalBridge.enabled, Is.True);
                Assert.That(externalController.TryGetTarget(
                    SbRetargetAnchorTargetBridge.HeadAnchor, out _), Is.True);
                AssertPose(externalRetarget.headTransform, new Vector3(1f, 0f, 0f));
                AssertPose(discoveredRetarget.headTransform, Vector3.zero);

                externalController.ApplyTarget(SbRetargetAnchorTargetBridge.HeadAnchor,
                    new Vector3(2f, 0f, 0f), 0f, true, 1d, 2d);
                AssertPose(externalRetarget.headTransform, new Vector3(2f, 0f, 0f));
                Assert.That(Vector3.Distance(discoveredRetarget.headTransform.position, Vector3.zero),
                    Is.LessThan(1e-4f),
                    "the avatar-local retarget must never replace the explicit external consumer");
            }
            finally
            {
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(sourceObject);
                Object.DestroyImmediate(retargetObject);
            }
        }

        // The production received avatar samples the head as well as the root, and
        // the pelvis stays deferred.
        [Test]
        public void NetworkAvatarSamplesTheHeadAnchorAsWellAsTheRootAndNotThePelvis()
        {
            var networkObject = new GameObject("network-avatar-samples");
            networkObject.SetActive(false);
            var entity = networkObject.AddComponent<SbNetworkAvatarEntity>();
            var meta = networkObject.AddComponent<MetaAvatarEntity>();
            var sbObject = new GameObject("sb-avatar-samples");
            sbObject.SetActive(false);
            var sbAvatar = sbObject.AddComponent<SbAvatarEntity>();
            var retarget = sbObject.AddComponent<SbRetargetTransform>();
            try
            {
                // No retarget head yet: only the root is sampled.
                sbAvatar._retargetTransform = retarget;
                meta._sbAvatar = sbAvatar;
                Assert.That(entity.RealizedAnchorNames, Is.Empty);
                Assert.That(entity.TryGetRealizedAnchorPose("head", out _, out _), Is.False);

                retarget.headTransform = NewPartOn(sbObject.transform, "head");
                retarget.headTransform.SetPositionAndRotation(new Vector3(1f, 1.6f, 2f), Quaternion.Euler(10f, 30f, 0f));
                Assert.That(entity.RealizedAnchorNames, Is.EqualTo(new[] { "head" }));
                Assert.That(entity.RealizedAnchorNames, Does.Not.Contain("pelvis"));
                Assert.That(entity.TryGetRealizedAnchorPose("head", out Vector3 position, out Quaternion rotation), Is.True);
                Assert.That(position, Is.EqualTo(new Vector3(1f, 1.6f, 2f)));
                Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(10f, 30f, 0f)), Is.LessThan(1e-3f));
                Assert.That(entity.TryGetRealizedAnchorPose("pelvis", out _, out _), Is.False, "The pelvis stays deferred.");
                Assert.That(entity.TryGetRealizedAnchorPose("root", out _, out _), Is.False,
                    "The root is GetRealizedPose, not a named anchor sample.");
            }
            finally
            {
                Object.DestroyImmediate(networkObject);
                Object.DestroyImmediate(sbObject);
            }
        }

        [Test]
        public void NetworkAvatarRootConsumer_RoutesMetaRetargetToSdkTrackingOutput()
        {
            // Keep all production Avatar components inactive: this test verifies the
            // real public reference route without invoking Meta/Fusion Start, mesh,
            // device, or network initialization.
            var networkObject = new GameObject("network-avatar-route");
            networkObject.SetActive(false);
            var entity = networkObject.AddComponent<SbNetworkAvatarEntity>();
            var meta = networkObject.AddComponent<MetaAvatarEntity>();
            var sbObject = new GameObject("sb-avatar-route");
            sbObject.SetActive(false);
            var sbAvatar = sbObject.AddComponent<SbAvatarEntity>();
            var retarget = sbObject.AddComponent<SbRetargetTransform>();
            retarget.headTransform = NewPartOn(sbObject.transform, "head");
            retarget.leftHandTransform = NewPartOn(sbObject.transform, "left");
            retarget.rightHandTransform = NewPartOn(sbObject.transform, "right");
            sbAvatar._retargetTransform = retarget;
            meta._sbAvatar = sbAvatar;
            var inputObject = new GameObject("sdk-input-route");
            inputObject.SetActive(false);
            var input = inputObject.AddComponent<SbTransformInputManager>();
            input.SbTransform = retarget;
            try
            {
                entity.ApplyRealizationTarget(new Vector3(5f, 1.5f, -2f),
                    Quaternion.Euler(0f, 30f, 0f));
                var trackingDelegate = new SbTransformTrackingDelegate(input);
                trackingDelegate.GetRawInputTrackingState(out var state);

                AssertPose(retarget.headTransform, new Vector3(5f, 1.5f, -2f));
                Assert.That(state.headsetActive, Is.True);
                AssertPose(state.headset.position, new Vector3(5f, 1.5f, -2f));
            }
            finally
            {
                Object.DestroyImmediate(networkObject);
                Object.DestroyImmediate(sbObject);
                Object.DestroyImmediate(inputObject);
            }
        }

        private Transform NewPart(string name)
        {
            return NewPartOn(_root.transform, name);
        }

        private static Transform NewPartOn(Transform parent, string name)
        {
            var part = new GameObject(name).transform;
            part.SetParent(parent, false);
            return part;
        }

        private void SetBaseline(Vector3 headPosition, Quaternion headRotation,
                                 Vector3 leftRelative, Vector3 rightRelative)
        {
            _retarget.SetHeadTransform(headPosition, headRotation);
            _retarget.SetHandTransform(leftRelative, rightRelative, Quaternion.identity,
                Quaternion.identity, 1f);
        }

        private bool HeadsetActive()
        {
            var trackingDelegate = new SbTransformTrackingDelegate(_input);
            trackingDelegate.GetRawInputTrackingState(out var state);
            return state.headsetActive;
        }

        private bool LeftControllerActive()
        {
            var trackingDelegate = new SbTransformTrackingDelegate(_input);
            trackingDelegate.GetRawInputTrackingState(out var state);
            return state.leftControllerActive;
        }

        private bool RightControllerActive()
        {
            var trackingDelegate = new SbTransformTrackingDelegate(_input);
            trackingDelegate.GetRawInputTrackingState(out var state);
            return state.rightControllerActive;
        }

        private Vector3 HeadsetPosition()
        {
            var trackingDelegate = new SbTransformTrackingDelegate(_input);
            trackingDelegate.GetRawInputTrackingState(out var state);
            return state.headset.position;
        }

        private Vector3 LeftControllerPosition()
        {
            var trackingDelegate = new SbTransformTrackingDelegate(_input);
            trackingDelegate.GetRawInputTrackingState(out var state);
            return state.leftController.position;
        }

        private static void AssertPose(Transform transform, Vector3 expected)
        {
            AssertPose(transform.position, expected);
        }

        private static void AssertPose(Transform transform, Vector3 expected, string message)
        {
            Assert.That(Vector3.Distance(transform.position, expected), Is.LessThan(1e-4f),
                message);
        }

        private static void AssertPose(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-4f));
        }

        private static void AssertStreamedHand(Transform hand, Pose head, Vector3 relativePosition,
                                               Quaternion relativeRotation)
        {
            AssertPose(hand.position, head.position + head.rotation * relativePosition);
            AssertRotation(hand.rotation, head.rotation * relativeRotation);
        }

        private static void AssertBlendedHand(Transform hand, Pose head, Vector3 relativePosition,
                                              Quaternion relativeRotation, Pose target, float weight)
        {
            var baseline = new Pose(head.position + head.rotation * relativePosition,
                head.rotation * relativeRotation);
            AssertPose(hand.position, Vector3.Lerp(baseline.position, target.position, weight));
            AssertRotation(hand.rotation, Quaternion.Slerp(baseline.rotation, target.rotation, weight));
        }

        private static void AssertRotation(Quaternion actual, Quaternion expected)
        {
            Assert.That(Quaternion.Angle(actual, expected), Is.LessThan(.01f));
        }

        private static void InvokeEditModeLifecycle(MonoBehaviour component, string method)
        {
            MethodInfo callback = component.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(callback, Is.Not.Null, method + " must remain a production lifecycle callback.");
            callback.Invoke(component, null);
        }
    }
}
#endif
