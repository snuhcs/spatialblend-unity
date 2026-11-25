// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using SpatialBlend.Motion;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FoundationNative")]
    public sealed class FoundationActorNativeLifecycleTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("actor-world");
        }

        [TearDown]
        public void TearDown()
        {
            SetActorSetupRejection(null);
            _world.Shutdown();
            for (int index = _created.Count - 1; index >= 0; --index)
                UnityEngine.Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void SameKeyActorsUseNativeSharedRoutingAndMappedSupportGrounding()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "source-actor-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "receiver-actor-owner");
            SpatialBlendObject chairSeat = SupportUnder(sourceOwner, "chair-seat",
                new Vector3(0f, 0f, 0f));
            SpatialBlendObject sofaSeat = SupportUnder(receiverOwner, "sofa-support",
                new Vector3(20f, 0f, 0f));
            Assert.That(chairSeat.TryRegister(out string error), Is.True, error);
            Assert.That(sofaSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(
                            chairSeat, sofaSeat, out IDisposable supportLease, out error),
                        Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                SpatialBlendActor source = Create<SpatialBlendActor>("source-actor");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("receiver-actor");
                var anchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                                             Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body,
                                             Vector3.zero)
                };
                var receiverAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                                             Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body,
                                             Vector3.zero)
                };
                AnchorTargetController bodyTargets = Create<AnchorTargetController>("receiver-body-targets");
                source.Configure(sourceOwner, "tracked-person", true, anchors);
                receiver.Configure(receiverOwner, "tracked-person", false, receiverAnchors, bodyTargets);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink sourceInput), Is.True);
                Assert.That(sourceInput.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 0,
                    timeSeconds = 0d,
                    anchors = new[] { new AnchorPoseSample { anchorId = "body", worldPosition = Vector3.zero,
                        worldRotation = Quaternion.identity } }
                }), Is.True);

                SpatialBlendRootTarget received = default;
                int targetCount = 0;
                receiver.RootTargetAvailable += value =>
                {
                    received = value;
                    ++targetCount;
                };
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(17.25d);

                Assert.That(targetCount, Is.GreaterThan(0),
                    "Native same-session routing must select the receiver actor.");
                Assert.That(received.DeliveredAt, Is.EqualTo(17.25d));
                Assert.That(double.IsNaN(received.ArrivalTime), Is.False,
                    "The native arrival time is retained independently of local delivery time.");
                Assert.That(received.Position.x, Is.GreaterThan(10f),
                    "The receiver target must resolve at the displaced sofa support, not the chair.");

                LocalActorRegistration stale = receiver.Registration;
                receiver.Unregister();
                Assert.That(stale.IsLive, Is.False);
                Assert.That(source.TryUpdateRoot(new Vector3(1f, 0f, 0f), 0f, out error),
                            Is.True, error);
                _world.DrainRealizations(18d);
                Assert.That(targetCount, Is.EqualTo(1),
                    "A retired receiver cannot be revived by a queued/native callback.");
            }
            finally
            {
                supportLease.Dispose();
            }
        }

        [Test]
        public void SameSourceDeliversMappedTargetsToTwoIndependentReceivers()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "source-owner");
            SpatialBlendObject leftOwner = ObjectIn("left", "left-owner");
            SpatialBlendObject rightOwner = ObjectIn("right", "right-owner");
            SpatialBlendObject sourceSupport = SupportUnder(sourceOwner, "chair-seat", Vector3.zero);
            SpatialBlendObject leftSupport = SupportUnder(leftOwner, "left-sofa-seat", new Vector3(-20f, 0f, 0f));
            SpatialBlendObject rightSupport = SupportUnder(rightOwner, "right-sofa-seat", new Vector3(20f, 0f, 0f));
            Assert.That(sourceSupport.TryRegister(out string error), Is.True, error);
            Assert.That(leftSupport.TryRegister(out error), Is.True, error);
            Assert.That(rightSupport.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, leftSupport,
                out IDisposable leftLease, out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, rightSupport,
                out IDisposable rightLease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(leftOwner.TryRegister(out error), Is.True, error);
                Assert.That(rightOwner.TryRegister(out error), Is.True, error);
                var sourceAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                var leftAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                var rightAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                SpatialBlendActor source = Create<SpatialBlendActor>("source");
                SpatialBlendActor left = Create<SpatialBlendActor>("left");
                SpatialBlendActor right = Create<SpatialBlendActor>("right");
                AnchorTargetController leftBodies = Create<AnchorTargetController>("left-bodies");
                AnchorTargetController rightBodies = Create<AnchorTargetController>("right-bodies");
                source.Configure(sourceOwner, "same-person", true, sourceAnchors);
                left.Configure(leftOwner, "same-person", false, leftAnchors, leftBodies);
                right.Configure(rightOwner, "same-person", false, rightAnchors, rightBodies);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(left.TryRegister(out error), Is.True, error);
                Assert.That(left.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(right.TryRegister(out error), Is.True, error);
                Assert.That(right.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink input), Is.True);
                Assert.That(input.TryApplyWorldFrame(Frame(0, 0d, "body")), Is.True);
                Vector3 leftTarget = default;
                Vector3 rightTarget = default;
                int leftCount = 0;
                int rightCount = 0;
                left.RootTargetAvailable += target => { leftTarget = target.Position; ++leftCount; };
                right.RootTargetAvailable += target => { rightTarget = target.Position; ++rightCount; };
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(10d);
                Assert.That(leftCount, Is.GreaterThan(0));
                Assert.That(rightCount, Is.GreaterThan(0));
                Assert.That(leftTarget.x, Is.LessThan(-10f));
                Assert.That(rightTarget.x, Is.GreaterThan(10f));
                Assert.That(Mathf.Abs(leftTarget.x - rightTarget.x), Is.GreaterThan(10f));
                Assert.That(leftBodies.TryGetTarget("body", out AnchorTargetController.Target leftBody), Is.True);
                Assert.That(leftBody.Position.x, Is.LessThan(-10f));
                Assert.That(rightBodies.TryGetTarget("body", out AnchorTargetController.Target rightBody), Is.True);
                Assert.That(rightBody.Position.x, Is.GreaterThan(10f));
            }
            finally
            {
                leftLease.Dispose();
                rightLease.Dispose();
            }
        }

        [Test]
        public void StationaryRootBodyObservationChangesAndEmptySnapshotReleasesOnlyBody()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "stationary-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "stationary-receiver-owner");
            SpatialBlendObject sourceSupport = SupportUnder(sourceOwner, "source-seat", Vector3.zero);
            SpatialBlendObject targetSupport = SupportUnder(receiverOwner, "target-seat", new Vector3(20f, 0f, 0f));
            Assert.That(sourceSupport.TryRegister(out string error), Is.True, error);
            Assert.That(targetSupport.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, targetSupport,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                var sourceAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                var receiverAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                SpatialBlendActor source = Create<SpatialBlendActor>("stationary-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("stationary-receiver");
                AnchorTargetController controller = Create<AnchorTargetController>("stationary-targets");
                controller.ApplyTarget("unrelated", Vector3.one, 0f, false, 1d, 1d);
                source.Configure(sourceOwner, "stationary-person", true, sourceAnchors);
                receiver.Configure(receiverOwner, "stationary-person", false, receiverAnchors, controller);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink input), Is.True);
                int roots = 0;
                receiver.RootTargetAvailable += _ => ++roots;
                Assert.That(input.TryApplyWorldFrame(FrameAt(0, 0d, new Vector3(-0.5f, 0f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(1d);
                Assert.That(controller.TryGetTarget("body", out AnchorTargetController.Target first), Is.True);
                Assert.That(input.TryApplyWorldFrame(FrameAt(1, 1d, new Vector3(0.5f, 0f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(2d);
                Assert.That(controller.TryGetTarget("body", out AnchorTargetController.Target second), Is.True);
                Assert.That(Vector3.Distance(first.Position, second.Position), Is.GreaterThan(0.01f));
                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame { sequence = 2, timeSeconds = 2d,
                    anchors = Array.Empty<AnchorPoseSample>() }), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(3d);
                Assert.That(controller.TryGetTarget("body", out _), Is.False);
                Assert.That(roots, Is.GreaterThanOrEqualTo(3));
                Assert.That(controller.TryGetTarget("unrelated", out _), Is.True);
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void CustomSpatialBodyAnchorMapsDifferingSupportHeightAndClearsOnEmptyFrame()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "raised-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "raised-receiver-owner");
            SpatialBlendObject sourceSupport = SupportUnder(sourceOwner, "raised-chair-seat",
                new Vector3(0f, 1f, 0f));
            SpatialBlendObject targetSupport = SupportUnder(receiverOwner, "raised-sofa-seat",
                new Vector3(20f, 5f, 0f));
            Assert.That(sourceSupport.TryRegister(out string error), Is.True, error);
            Assert.That(targetSupport.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, targetSupport,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                var sourceAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                        Vector3.zero),
                    new SpatialBlendActorAnchor("custom-hand", SpatialBlendActorAnchorRole.Body,
                        Vector3.zero,
                        positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
                };
                var receiverAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                        Vector3.zero),
                    new SpatialBlendActorAnchor("custom-hand", SpatialBlendActorAnchorRole.Body,
                        Vector3.zero,
                        positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
                };
                SpatialBlendActor source = Create<SpatialBlendActor>("raised-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("raised-receiver");
                AnchorTargetController controller = Create<AnchorTargetController>("raised-targets");
                controller.ApplyTarget("unrelated", Vector3.one, 0f, false, 1d, 1d);
                source.Configure(sourceOwner, "raised-person", true, sourceAnchors);
                receiver.Configure(receiverOwner, "raised-person", false, receiverAnchors, controller);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink input), Is.True);
                int roots = 0;
                receiver.RootTargetAvailable += _ => ++roots;

                // A non-seat anchor emits a support target only while it is inside
                // its enter distance (0.05 m above the support plane, core
                // AnchorEngagementConfig). The source seat is authored
                // at y = 1, so these samples sit on that plane at two chart
                // positions. The target seat is at y = 5.
                Assert.That(input.TryApplyWorldFrame(HandFrame(0, 0d,
                    new Vector3(0f, 1f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(1d);
                Assert.That(controller.TryGetTarget("custom-hand",
                    out AnchorTargetController.Target first), Is.True);
                Assert.That(first.HasHeight, Is.True);
                Assert.That(first.Position.x, Is.GreaterThan(10f));
                Assert.That(first.Position.y, Is.GreaterThan(4f));

                Assert.That(input.TryApplyWorldFrame(HandFrame(1, 1d,
                    new Vector3(0.03f, 1f, 0.02f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(2d);
                Assert.That(controller.TryGetTarget("custom-hand",
                    out AnchorTargetController.Target second), Is.True);
                Assert.That(second.HasHeight, Is.True);
                Assert.That(Vector3.Distance(first.Position, second.Position), Is.GreaterThan(0.01f));

                // Legacy pose input treats a missing anchor as explicit
                // deactivation (SetAnchorActive false), which releases at once.
                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 2,
                    timeSeconds = 2d,
                    anchors = Array.Empty<AnchorPoseSample>()
                }), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(3d);
                Assert.That(controller.TryGetTarget("custom-hand", out _), Is.False);

                // 0.12 m above the y = 1 plane is outside the 0.05 m enter
                // distance. The previous frame deactivated the anchor, so this
                // is a new engagement attempt and publishes no target.
                Assert.That(input.TryApplyWorldFrame(HandFrame(3, 3d,
                    new Vector3(0f, 1.12f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(4d);
                Assert.That(controller.TryGetTarget("custom-hand", out _), Is.False,
                    "A custom body anchor outside the 0.05 m enter distance awaits engagement and publishes no target.");
                Assert.That(roots, Is.GreaterThanOrEqualTo(3));
                Assert.That(controller.TryGetTarget("unrelated", out _), Is.True);
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void SourceReregistrationResumesOutputWithoutRecreatingReceiver()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "reregister-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "reregister-receiver-owner");
            SpatialBlendObject sourceSupport = SupportUnder(sourceOwner, "chair-seat", Vector3.zero);
            SpatialBlendObject targetSupport = SupportUnder(receiverOwner, "sofa-seat", new Vector3(20f, 0f, 0f));
            Assert.That(sourceSupport.TryRegister(out string error), Is.True, error);
            Assert.That(targetSupport.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, targetSupport,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                var sourceAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                var receiverAnchors = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                SpatialBlendActor source = Create<SpatialBlendActor>("reregister-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("reregister-receiver");
                AnchorTargetController controller = Create<AnchorTargetController>("reregister-targets");
                source.Configure(sourceOwner, "reregister-person", true, sourceAnchors);
                receiver.Configure(receiverOwner, "reregister-person", false, receiverAnchors, controller);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                LocalActorRegistration receiverRegistration = receiver.Registration;
                int roots = 0;
                receiver.RootTargetAvailable += _ => ++roots;
                Assert.That(source.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink oldSink), Is.True);
                Assert.That(oldSink.TryApplyWorldFrame(FrameAt(0, 0d, new Vector3(-0.25f, 0f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(1d);
                Assert.That(oldSink.TryApplyWorldFrame(FrameAt(1, 1d, new Vector3(0.25f, 0f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(2d);
                Assert.That(roots, Is.GreaterThanOrEqualTo(2));
                Assert.That(controller.TryGetTarget("body", out _), Is.True);
                source.Unregister();
                Assert.That(receiver.IsRegistered, Is.True);
                Assert.That(receiver.Registration, Is.SameAs(receiverRegistration));
                Assert.That(controller.TryGetTarget("body", out _), Is.False);
                Assert.That(oldSink.TryApplyWorldFrame(FrameAt(2, 2d, Vector3.zero)), Is.False);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(source.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink freshSink), Is.True);
                Assert.That(freshSink.TryApplyWorldFrame(FrameAt(0, 0d, new Vector3(0.1f, 0f, 0f))), Is.True);
                Assert.That(source.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
                _world.DrainRealizations(3d);
                Assert.That(receiver.IsRegistered, Is.True);
                Assert.That(receiver.Registration, Is.SameAs(receiverRegistration));
                Assert.That(roots, Is.GreaterThanOrEqualTo(3));
                Assert.That(controller.TryGetTarget("body", out AnchorTargetController.Target resumed), Is.True);
                Assert.That(resumed.Position.x, Is.GreaterThan(10f));
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void FailedActorSetupDoesNotRewriteLiveReceiverRoute()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "failed-route-source");
            SpatialBlendObject receiverOwner = ObjectIn("target", "failed-route-receiver");
            Assert.That(sourceOwner.TryRegister(out string error), Is.True, error);
            Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
            var root = new[] { new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero) };
            SpatialBlendActor receiver = Create<SpatialBlendActor>("failed-route-live-receiver");
            SpatialBlendActor source = Create<SpatialBlendActor>("failed-route-source");
            receiver.Configure(receiverOwner, "failed-route-person", false, root);
            source.Configure(sourceOwner, "failed-route-person", true, root);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            long before = RoutingIdFor("failed-route-person");
            SetActorSetupRejection(name => name == "root");
            try
            {
                Assert.That(source.TryRegister(out _), Is.False);
            }
            finally { SetActorSetupRejection(null); }
            Assert.That(receiver.IsRegistered, Is.True);
            Assert.That(RoutingIdFor("failed-route-person"), Is.EqualTo(before));
            Assert.That(source.TryRegister(out error), Is.True, error);
        }

        [Test]
        public void SharedControllerBodyNameCollisionRejectsWithoutStealingLease()
        {
            SpatialBlendObject firstOwner = ObjectIn("target-a", "lease-first-owner");
            SpatialBlendObject secondOwner = ObjectIn("target-b", "lease-second-owner");
            Assert.That(firstOwner.TryRegister(out string error), Is.True, error);
            Assert.That(secondOwner.TryRegister(out error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("shared-body-controller");
            var body = new[] { new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero) };
            SpatialBlendActor first = Create<SpatialBlendActor>("lease-first");
            SpatialBlendActor second = Create<SpatialBlendActor>("lease-second");
            first.Configure(firstOwner, "lease-first-person", false, body, controller);
            second.Configure(secondOwner, "lease-second-person", false, body, controller);
            Assert.That(first.TryRegister(out error), Is.True, error);
            Assert.That(first.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            Assert.That(second.TryRegister(out _), Is.False);
            Assert.That(first.IsRegistered, Is.True);
            Assert.That(controller.TryAcquireLease("body", out _), Is.False);
            first.Unregister();
            Assert.That(controller.TryAcquireLease("body", out AnchorTargetController.TargetLease replacement), Is.True);
            replacement.Dispose();
        }

        [Test]
        public void DetachedBodyReleaseCannotDisposeReconfiguredBodyLease()
        {
            SpatialBlendObject owner = ObjectIn("target", "release-reconfigure-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("release-reconfigure-targets");
            SpatialBlendActor receiver = Create<SpatialBlendActor>("release-reconfigure-receiver");
            var oldAnchors = new[]
            {
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            };
            var replacementAnchors = new[]
            {
                new SpatialBlendActorAnchor("other-body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            };
            receiver.Configure(owner, "release-reconfigure-person", false, oldAnchors, controller);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            LocalActorRegistration oldRegistration = receiver.Registration;
            receiver.DeliverRealization(oldRegistration, 0, new[]
            {
                new SpatialBlend.Internal.RealizationTarget
                {
                    AnchorName = "body", KernelType = SpatialBlend.Internal.SbKernelType.AffordancePose,
                    Position = Vector3.zero, Rotation = Quaternion.identity, Weight = 1d
                }
            }, 1d);
            bool replaced = false;
            controller.TargetReleased += name =>
            {
                if (name != "body" || replaced) return;
                replaced = true;
                receiver.Configure(owner, "release-reconfigure-person", false, replacementAnchors, controller);
                Assert.That(receiver.TryRegister(out string replacementError), Is.True, replacementError);
            };
            receiver.Unregister();
            Assert.That(replaced, Is.True);
            LocalActorRegistration replacement = receiver.Registration;
            Assert.That(replacement, Is.Not.Null);
            receiver.DeliverRealization(replacement, 0, new[]
            {
                new SpatialBlend.Internal.RealizationTarget
                {
                    AnchorName = "other-body", KernelType = SpatialBlend.Internal.SbKernelType.AffordancePose,
                    Position = Vector3.right, Rotation = Quaternion.identity, Weight = 1d
                }
            }, 2d);
            Assert.That(controller.TryGetTarget("other-body", out AnchorTargetController.Target target), Is.True);
            Assert.That(target.Position, Is.EqualTo(Vector3.right));
        }

        [Test]
        public void RetainedReleaseStopsBeforeCancellingSameLeaseAfterReentrantOutput()
        {
            SpatialBlendObject owner = ObjectIn("target", "retained-reentry-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("retained-reentry-targets");
            SpatialBlendActor receiver = Create<SpatialBlendActor>("retained-reentry-receiver");
            var anchors = new[]
            {
                new SpatialBlendActorAnchor("body-a", SpatialBlendActorAnchorRole.Body, Vector3.zero),
                new SpatialBlendActorAnchor("body-b", SpatialBlendActorAnchorRole.Body, Vector3.zero),
                new SpatialBlendActorAnchor("body-c", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            };
            receiver.Configure(owner, "retained-reentry-person", false, anchors, controller);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            LocalActorRegistration registration = receiver.Registration;
            receiver.DeliverRealization(registration, 0, new[]
            {
                BodyTarget("body-a", Vector3.zero), BodyTarget("body-b", Vector3.one),
                BodyTarget("body-c", Vector3.up)
            }, 1d);
            bool reentered = false;
            controller.TargetReleased += name =>
            {
                if (name != "body-a" || reentered) return;
                reentered = true;
                receiver.DeliverRealization(registration, 0,
                    BodyTargets("body-b", Vector3.right), 2d);
            };
            receiver.ReleaseOwnedOutputs(registration, false);
            Assert.That(reentered, Is.True);
            Assert.That(controller.TryGetTarget("body-b", out AnchorTargetController.Target target), Is.True);
            Assert.That(target.Position, Is.EqualTo(Vector3.right));
            Assert.That(controller.TryGetTarget("body-c", out _), Is.False);
        }

        [Test]
        public void ForcedClearStopsLaterObserversAfterReentrantReceiverOutput()
        {
            SpatialBlendObject owner = ObjectIn("target", "clear-reentry-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("clear-reentry-targets");
            SpatialBlendActor receiver = Create<SpatialBlendActor>("clear-reentry-receiver");
            receiver.Configure(owner, "clear-reentry-person", false, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            }, controller);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            LocalActorRegistration registration = receiver.Registration;
            int later = 0;
            int newerRoots = 0;
            bool reentered = false;
            receiver.RootTargetAvailable += _ => ++newerRoots;
            receiver.RootTargetsCleared += () =>
            {
                if (reentered) return;
                reentered = true;
                receiver.DeliverRealization(registration, 0, new[]
                {
                    new SpatialBlend.Internal.RealizationTarget
                    {
                        AnchorName = "root", KernelType = SpatialBlend.Internal.SbKernelType.Spacing,
                        Position = Vector3.zero, Rotation = Quaternion.identity, Weight = 1d
                    },
                    BodyTarget("body", Vector3.right)
                }, 2d);
            };
            receiver.RootTargetsCleared += () => ++later;
            receiver.ReleaseOwnedOutputs(registration, false);
            Assert.That(reentered, Is.True);
            Assert.That(later, Is.EqualTo(0));
            Assert.That(newerRoots, Is.EqualTo(1));
            Assert.That(controller.TryGetTarget("body", out AnchorTargetController.Target target), Is.True);
            Assert.That(target.Position, Is.EqualTo(Vector3.right));
        }

        [Test]
        public void ClearedCallbackReregistrationDoesNotNotifyLaterStaleSubscriber()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "clear-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "clear-receiver-owner");
            Assert.That(sourceOwner.TryRegister(out string error), Is.True, error);
            Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("clear-targets");
            var anchors = new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            };
            SpatialBlendActor source = Create<SpatialBlendActor>("clear-source");
            SpatialBlendActor receiver = Create<SpatialBlendActor>("clear-receiver");
            source.Configure(sourceOwner, "clear-person", true, anchors);
            receiver.Configure(receiverOwner, "clear-person", false, anchors, controller);
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            bool replaced = false;
            int staleSubscriber = 0;
            receiver.RootTargetsCleared += () =>
            {
                if (replaced) return;
                replaced = true;
                receiver.Unregister();
                Assert.That(receiver.TryRegister(out string replacementError), Is.True, replacementError);
            };
            receiver.RootTargetsCleared += () => ++staleSubscriber;
            source.Unregister();
            Assert.That(replaced, Is.True);
            Assert.That(receiver.IsRegistered, Is.True);
            Assert.That(staleSubscriber, Is.EqualTo(0));
        }

        [Test]
        public void ActorKeyAndOwnerAmbiguitiesFailBeforeNativeActorAllocation()
        {
            SpatialBlendObject first = ObjectIn("source", "seat-a");
            SpatialBlendObject second = ObjectIn("source", "seat-b");
            Assert.That(first.TryRegister(out string error), Is.True, error);
            Assert.That(second.TryRegister(out error), Is.True, error);
            var anchor = new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero)
            };
            SpatialBlendActor source = Create<SpatialBlendActor>("source");
            SpatialBlendActor duplicateSource = Create<SpatialBlendActor>("duplicate-source");
            SpatialBlendActor duplicateOwner = Create<SpatialBlendActor>("duplicate-owner");
            source.Configure(first, "person", true, anchor);
            duplicateSource.Configure(second, "person", true, anchor);
            duplicateOwner.Configure(first, "other-person", false, anchor);
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(duplicateSource.TryRegister(out _), Is.False);
            Assert.That(duplicateOwner.TryRegister(out _), Is.False);
            Assert.That(source.IsRegistered, Is.True);
        }

        [Test]
        public void SameKeyReceiversInDifferentSpacesDoNotAliasPerSpaceNativeActorIds()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "source-owner");
            SpatialBlendObject firstOwner = ObjectIn("target-a", "first-owner");
            SpatialBlendObject secondOwner = ObjectIn("target-b", "second-owner");
            Assert.That(sourceOwner.TryRegister(out string error), Is.True, error);
            Assert.That(firstOwner.TryRegister(out error), Is.True, error);
            Assert.That(secondOwner.TryRegister(out error), Is.True, error);
            var anchors = new[] { new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero) };
            SpatialBlendActor source = Create<SpatialBlendActor>("source");
            SpatialBlendActor first = Create<SpatialBlendActor>("first");
            SpatialBlendActor second = Create<SpatialBlendActor>("second");
            source.Configure(sourceOwner, "person", true, anchors);
            first.Configure(firstOwner, "person", false, anchors);
            second.Configure(secondOwner, "person", false, anchors);
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(first.TryRegister(out error), Is.True, error);
            Assert.That(first.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            Assert.That(second.TryRegister(out error), Is.True, error);
            Assert.That(second.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            Assert.That(first.Registration.NativeActor, Is.EqualTo(second.Registration.NativeActor),
                "The native API allocates actor IDs per space; world lookup must include space.");
            Assert.That(first.Registration.NativeSpace, Is.Not.EqualTo(second.Registration.NativeSpace));
        }

        [Test]
        public void CapturedSourceSinkCannotWriteAfterUnregisterAndReregister()
        {
            SpatialBlendObject owner = ObjectIn("source", "source-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>("source");
            actor.Configure(owner, "person", true, new[]
            {
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            });
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink stale), Is.True);
            actor.Unregister();
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(stale.TryApplyWorldFrame(Frame(0, 0d, "body")), Is.False);
            Assert.That(actor.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink replacement), Is.True);
            Assert.That(replacement.TryApplyWorldFrame(Frame(0, 0d, "body")), Is.True);
        }

        [Test]
        public void MotionInputRequiresZeroUnparentedHorizontalRootButLegacyInputsRemainAvailable()
        {
            SpatialBlendObject owner = ObjectIn("source", "motion-root-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);

            SpatialBlendActor canonical = Create<SpatialBlendActor>("canonical-motion-root");
            canonical.Configure(owner, "canonical-motion", true, new[]
            {
                new SpatialBlendActorAnchor("navigation", SpatialBlendActorAnchorRole.Root, Vector3.zero)
            });
            Assert.That(canonical.TryRegister(out error), Is.True, error);
            Assert.That(canonical.TryCreateMotionInput(out _), Is.True);
            canonical.Unregister();

            SpatialBlendActor offset = Create<SpatialBlendActor>("offset-motion-root");
            offset.Configure(owner, "offset-motion", true, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                                         new Vector3(0.000001f, 0f, 0f))
            });
            Assert.That(offset.TryRegister(out error), Is.True, error);
            Assert.That(offset.TryCreateMotionInput(out _), Is.False,
                "Motion ingress requires an exactly zero local root offset.");
            Assert.That(offset.TryCreateAnchorPoseInput(out _), Is.True);
            Assert.That(offset.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
            offset.Unregister();

            SpatialBlendActor parented = Create<SpatialBlendActor>("parented-motion-root");
            parented.Configure(owner, "parented-motion", true, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero,
                                         parent: "body"),
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            });
            Assert.That(parented.TryRegister(out error), Is.True, error);
            Assert.That(parented.TryCreateMotionInput(out _), Is.False,
                "Motion ingress must not compose an authored parent transform into native root state.");
            Assert.That(parented.TryCreateAnchorPoseInput(out _), Is.True);
            Assert.That(parented.TryUpdateRoot(Vector3.zero, 0f, out error), Is.True, error);
            parented.Unregister();

            SpatialBlendActor spatialRoot = Create<SpatialBlendActor>("spatial-root");
            spatialRoot.Configure(owner, "spatial-root", true, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero,
                    positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
            });
            Assert.That(spatialRoot.TryRegister(out _), Is.False,
                "A Root cannot declare Spatial3D.");

            SpatialBlendActor invalidDimension = Create<SpatialBlendActor>("invalid-root-dimension");
            invalidDimension.Configure(owner, "invalid-root-dimension", true, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero,
                    positionDimension: (SpatialBlendActorAnchorPositionDimension)99)
            });
            Assert.That(invalidDimension.TryRegister(out _), Is.False,
                "Unknown anchor position dimensions must fail before native registration.");
        }

        [Test]
        public void CapturedReplayResetIsGenerationBound()
        {
            SpatialBlendObject owner = ObjectIn("source", "replay-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>("replay-source");
            actor.Configure(owner, "replay-person", true, new[]
            {
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            });
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink oldSink), Is.True);
            Assert.That(oldSink, Is.InstanceOf<IAnchorPoseFrameReplaySink>());
            Assert.That(oldSink.TryApplyWorldFrame(Frame(0, 0d, "body")), Is.True);
            actor.Unregister();
            Assert.That(actor.TryRegister(out error), Is.True, error);
            ((IAnchorPoseFrameReplaySink)oldSink).ResetWorldAnchorReplay();
            Assert.That(actor.TryCreateAnchorPoseInput(out IAnchorPoseFrameSink replacement), Is.True);
            Assert.That(replacement.TryApplyWorldFrame(Frame(0, 0d, "body")), Is.True,
                "A retained replay sink must not reset the replacement generation.");
        }

        [Test]
        public void CompleteBodySnapshotCancelsOnlyThisActorsTargetsAndCallbacksAreContained()
        {
            SpatialBlendObject owner = ObjectIn("target", "receiver-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("targets");
            controller.ApplyTarget("unrelated", Vector3.one, 0f, false, 1d, 1d);
            SpatialBlendActor actor = Create<SpatialBlendActor>("receiver");
            actor.Configure(owner, "person", false, new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            }, controller);
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            actor.RootTargetAvailable += _ => throw new InvalidOperationException("observer");
            LocalActorRegistration registration = actor.Registration;
            actor.DeliverRealization(registration, 0, new[]
            {
                new SpatialBlend.Internal.RealizationTarget { AnchorName = "root", KernelType = SpatialBlend.Internal.SbKernelType.Spacing,
                    Position = Vector3.zero, Rotation = Quaternion.identity, Weight = 1d },
                new SpatialBlend.Internal.RealizationTarget { AnchorName = "body", KernelType = SpatialBlend.Internal.SbKernelType.AffordancePose,
                    Position = Vector3.zero, Rotation = Quaternion.identity, Weight = 1d }
            }, 2d);
            Assert.That(controller.TryGetTarget("body", out _), Is.True);
            actor.DeliverRealization(registration, 0, Array.Empty<SpatialBlend.Internal.RealizationTarget>(), 3d);
            Assert.That(controller.TryGetTarget("body", out _), Is.False);
            Assert.That(controller.TryGetTarget("unrelated", out _), Is.True);
        }

        [Test]
        public void SourceRetirementInsideRootCallbackCannotReapplyBodyTargets()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "retiring-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "live-receiver-owner");
            Assert.That(sourceOwner.TryRegister(out string error), Is.True, error);
            Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
            AnchorTargetController controller = Create<AnchorTargetController>("receiver-targets");
            controller.ApplyTarget("unrelated", Vector3.one, 0f, false, 1d, 1d);
            var anchors = new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            };
            SpatialBlendActor source = Create<SpatialBlendActor>("retiring-source");
            SpatialBlendActor receiver = Create<SpatialBlendActor>("live-receiver");
            source.Configure(sourceOwner, "same-person", true, anchors);
            receiver.Configure(receiverOwner, "same-person", false, anchors, controller);
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
            int secondSubscriberCalls = 0;
            receiver.RootTargetAvailable += _ => source.Unregister();
            receiver.RootTargetAvailable += _ => ++secondSubscriberCalls;
            LocalActorRegistration receiverRegistration = receiver.Registration;
            receiver.DeliverRealization(receiverRegistration, 0, new[]
            {
                new SpatialBlend.Internal.RealizationTarget
                {
                    AnchorName = "root", KernelType = SpatialBlend.Internal.SbKernelType.Spacing,
                    Position = Vector3.zero, Rotation = Quaternion.identity, Weight = 1d
                },
                new SpatialBlend.Internal.RealizationTarget
                {
                    AnchorName = "body", KernelType = SpatialBlend.Internal.SbKernelType.AffordancePose,
                    Position = new Vector3(3f, 0f, 0f), Rotation = Quaternion.identity, Weight = 1d
                }
            }, 5d);
            Assert.That(source.IsRegistered, Is.False);
            Assert.That(receiver.IsRegistered, Is.True);
            Assert.That(secondSubscriberCalls, Is.EqualTo(0));
            Assert.That(controller.TryGetTarget("body", out _), Is.False);
            Assert.That(controller.TryGetTarget("unrelated", out _), Is.True);
        }

        private T Create<T>(string name)
            where T : Component
        {
            var value = new GameObject(name);
            _created.Add(value);
            T component = value.AddComponent<T>();
            // These are Edit Mode component contracts. Unity does not run
            // non-ExecuteAlways runtime lifecycle hooks when adding components
            // here; invoke Awake explicitly instead of weakening thread guards.
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(component, null);
            return component;
        }

        // ABI 4: scope belongs to the relation family, so a
        // support is reached as a descendant of the actor's own element, not through an app-declared scope.
        private SpatialBlendObject SupportUnder(SpatialBlendObject owner, string id, Vector3 center)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(_world, owner.SpaceId, id, owner, SpatialBlendObjectKind.Affordance);
            Vector3[] polygon =
            {
                center + new Vector3(-1f, 0f, -1f), center + new Vector3(1f, 0f, -1f),
                center + new Vector3(1f, 0f, 1f), center + new Vector3(-1f, 0f, 1f)
            };
            Assert.That(value.TrySetSupportPolygon(polygon, out string error), Is.True, error);
            return value;
        }

        private SpatialBlendObject ObjectIn(string space, string id)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(_world, space, id, null, SpatialBlendObjectKind.Object);
            return value;
        }

        private static AnchorPoseFrame Frame(long sequence, double time, string anchor) => new AnchorPoseFrame
        {
            sequence = sequence,
            timeSeconds = time,
            anchors = new[] { new AnchorPoseSample { anchorId = anchor, worldPosition = Vector3.zero,
                worldRotation = Quaternion.identity } }
        };

        private static AnchorPoseFrame HandFrame(long sequence, double time, Vector3 position) => new AnchorPoseFrame
        {
            sequence = sequence,
            timeSeconds = time,
            anchors = new[]
            {
                new AnchorPoseSample
                {
                    anchorId = "custom-hand",
                    worldPosition = position,
                    worldRotation = Quaternion.identity
                }
            }
        };

        private static AnchorPoseFrame FrameAt(long sequence, double time, Vector3 position) => new AnchorPoseFrame
        {
            sequence = sequence,
            timeSeconds = time,
            anchors = new[] { new AnchorPoseSample { anchorId = "body", worldPosition = position,
                worldRotation = Quaternion.identity } }
        };

        private static SpatialBlend.Internal.RealizationTarget[] BodyTargets(string firstName,
                                                                            Vector3 firstPosition,
                                                                            string secondName = null,
                                                                            Vector3 secondPosition = default)
        {
            var result = new List<SpatialBlend.Internal.RealizationTarget>
            {
                BodyTarget(firstName, firstPosition)
            };
            if (secondName != null) result.Add(BodyTarget(secondName, secondPosition));
            return result.ToArray();
        }

        private static SpatialBlend.Internal.RealizationTarget BodyTarget(string name, Vector3 position) =>
            new SpatialBlend.Internal.RealizationTarget
            {
                AnchorName = name, KernelType = SpatialBlend.Internal.SbKernelType.AffordancePose,
                Position = position, Rotation = Quaternion.identity, Weight = 1d
            };

        private long RoutingIdFor(string actorId)
        {
            FieldInfo field = typeof(SpatialBlendLocalWorld).GetField("_actorRoutingIds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var routes = (System.Collections.IDictionary)field.GetValue(_world);
            return (long)routes[actorId];
        }

        private static void SetActorSetupRejection(Func<string, bool> rejection)
        {
            FieldInfo field = typeof(SpatialBlendLocalWorld).GetField("TestOnlyRejectActorAnchorBinding",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            field.SetValue(null, rejection);
        }
    }
}
#endif
