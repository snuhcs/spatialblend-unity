// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EmbodiedTargetConsumerTests
    {
        private const long Actor = 7;
        internal static double Radians(double degrees) => degrees * Math.PI / 180d;
        internal static double Degrees(double radians) => radians * 180d / Math.PI;

        internal static ActorEvent Request(ActorEventKind kind, uint anchor, ulong revision,
            TargetChannels channels, Vector3 position = default, double unityYaw = 0d,
            EmbodiedTargetConsumer.AcceptedSample sample = null, bool placement = false,
            double pitch = 0d, int orientationType = 0)
        {
            return new ActorEvent
            {
                Kind = kind, AnchorId = anchor, Revision = revision,
                TargetRequest = new TargetRequest(channels, sample?.Stamp.Epoch ?? 0,
                    sample?.Stamp.Sequence ?? 0, sample?.NativeYawRadians ?? 0d,
                    sample == null ? 0d : sample.UnityYawRadians - Radians(unityYaw),
                    AnchorProposalFlags.Sampled, default, 0d, default,
                    placement ? TargetRequestFlags.Placement : TargetRequestFlags.None,
                    position, orientationType, Radians(90d - unityYaw), Radians(pitch), 0d)
            };
        }

        private static EmbodiedTargetConsumer.AcceptedSample Accept(EmbodiedTargetConsumer consumer,
            double yaw, uint anchor = 0)
        {
            Assert.That(consumer.TryProposePublication(anchor, out var stamp), Is.True);
            consumer.AcceptPublication(anchor, stamp, Radians(yaw), Radians(90d - yaw));
            return consumer.Current(anchor);
        }

        [Test]
        public void RejectedPublicationConsumesNeitherStampNorPendingTeleport()
        {
            var consumer = new EmbodiedTargetConsumer(Actor, 0);
            consumer.Placed(0);
            Assert.That(consumer.TryProposePublication(0, out var first), Is.True);
            Assert.That(consumer.TryProposePublication(0, out var retry), Is.True);
            Assert.That(retry.Epoch, Is.EqualTo(first.Epoch));
            Assert.That(retry.Sequence, Is.EqualTo(1ul));
            Assert.That(retry.Teleported, Is.True);
            Assert.That(consumer.Current(0), Is.Null);
            consumer.AcceptPublication(0, retry, 0d, Math.PI / 2d);
            Assert.That(consumer.TryProposePublication(0, out _), Is.False);
            consumer.NativeEmpty();
            Assert.That(consumer.TryProposePublication(0, out var next), Is.True);
            Assert.That(next.Sequence, Is.EqualTo(2ul));
            Assert.That(next.Teleported, Is.False);
        }

        [Test]
        public void EpochZeroIsAnExactAcceptedCorrespondenceNotAnInvalidSentinel()
        {
            var consumer = new EmbodiedTargetConsumer(Actor, 0);
            var accepted = Accept(consumer, 170d);
            consumer.Receive(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw,
                unityYaw: 440d, sample: accepted), true);
            consumer.NativeEmpty();
            Assert.That(consumer.DrainFailed, Is.False);
            Assert.That(Degrees(consumer.Pending()[0].YawRadians), Is.EqualTo(440d).Within(1e-10d));
            Assert.That(consumer.Pending()[0].Correspondence, Is.SameAs(accepted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedDrainAndLaterEmptyRetainBothSamplesAndBlockThirdAcceptance(bool conversion)
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            var old = Accept(consumer, 170d);
            consumer.NativeEmpty();
            var current = Accept(consumer, 190d);
            var failure = new ActorEventPollFailure(conversion ? 1 : -1, "injected", new byte[] { 1, 2, 3 });
            consumer.Fail(conversion ? "conversion" : "native", failure);
            consumer.NativeEmpty();
            Assert.That(consumer.Previous(0), Is.SameAs(old));
            Assert.That(consumer.Current(0), Is.SameAs(current));
            Assert.That(consumer.TryProposePublication(0, out _), Is.False);
            Assert.That(consumer.Failures[0], Is.SameAs(failure));
            Assert.That(consumer.Failures[0].RejectedEventBytes[2], Is.EqualTo(3));
        }

        [Test]
        public void PlacementAndLatePriorSampleResolveBeforeRetirementWithoutRebasing()
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            var first = Accept(consumer, 170d);
            consumer.NativeEmpty();
            var second = Accept(consumer, 190d);
            consumer.Receive(Request(ActorEventKind.YawRequest, 0, 10, TargetChannels.Yaw,
                unityYaw: -170d, placement: true), true);
            consumer.Receive(Request(ActorEventKind.YawRequest, 0, 10, TargetChannels.Yaw,
                unityYaw: 440d, sample: first), true);
            consumer.Receive(Request(ActorEventKind.YawRequest, 0, 11, TargetChannels.Yaw,
                unityYaw: 620d, sample: first), true);
            Assert.That(consumer.PendingCount, Is.EqualTo(2));
            Assert.That(consumer.Previous(0), Is.SameAs(first));
            consumer.NativeEmpty();
            var operations = consumer.Pending();
            Assert.That(operations.Length, Is.EqualTo(3));
            Assert.That(Degrees(EmbodiedTargetConsumer.Nearest(Radians(170d), operations[0].YawRadians)),
                Is.EqualTo(190d).Within(1e-10d));
            Assert.That(Degrees(operations[1].YawRadians), Is.EqualTo(440d).Within(1e-10d));
            Assert.That(Degrees(operations[2].YawRadians), Is.EqualTo(620d).Within(1e-10d));
            Assert.That(operations[2].Correspondence, Is.SameAs(first));
            Assert.That(consumer.Previous(0), Is.Null);
            Assert.That(consumer.Current(0), Is.SameAs(second));
        }

        [Test]
        public void OrdinaryYawRejectsNearButNotExactNativeSampleIdentity()
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            var accepted = Accept(consumer, 10d);
            var different = new EmbodiedTargetConsumer.AcceptedSample(Actor, 0, accepted.Stamp,
                accepted.UnityYawRadians, accepted.NativeYawRadians + 1e-12d);
            consumer.Receive(Request(ActorEventKind.YawRequest, 0, 1, TargetChannels.Yaw,
                unityYaw: 50d, sample: different), true);
            consumer.NativeEmpty();
            Assert.That(consumer.DrainFailed, Is.True);
            Assert.That(consumer.PendingCount, Is.Zero);
            Assert.That(consumer.RetainedOpenCount, Is.EqualTo(1));
        }

        [Test]
        public void LifecycleAndTypedPlacementAreDetachedAndAppliedOnlyThroughTypedChannels()
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            var payload = new byte[] { 4 };
            consumer.Receive(new ActorEvent { Kind = ActorEventKind.SourceException, Revision = 4, Payload = payload }, true);
            consumer.Receive(new ActorEvent { Kind = ActorEventKind.TeleportRequest, AnchorId = 0, Revision = 4,
                Position = Vector3.one * 100f }, true);
            consumer.Receive(Request(ActorEventKind.PositionRequest, 0, 4, TargetChannels.Position,
                position: Vector3.right, placement: true), true);
            consumer.Receive(Request(ActorEventKind.YawRequest, 0, 4, TargetChannels.Yaw,
                unityYaw: 90d, placement: true), true);
            payload[0] = 99;
            Assert.That(consumer.PendingCount, Is.Zero);
            consumer.NativeEmpty();
            var operations = consumer.Pending();
            Assert.That(operations.Length, Is.EqualTo(3));
            Assert.That(operations[0].Event.Payload[0], Is.EqualTo(4));
            Assert.That(operations[1].Channels, Is.EqualTo(TargetChannels.Position));
            Assert.That(operations[2].Channels, Is.EqualTo(TargetChannels.Yaw));
            Assert.Throws<InvalidOperationException>(() => consumer.Acknowledge(operations[1]));
            foreach (var operation in operations) consumer.Acknowledge(operation);
            Assert.That(consumer.PendingCount, Is.Zero);
        }

        [Test]
        public void RegressedRevisionCannotBeReinterpretedAsSuccessfulEmpty()
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            consumer.Receive(Request(ActorEventKind.PositionRequest, 0, 4, TargetChannels.Position, Vector3.one), true);
            consumer.Receive(Request(ActorEventKind.PositionRequest, 0, 3, TargetChannels.Position, Vector3.right), true);
            consumer.NativeEmpty();
            Assert.That(consumer.DrainFailed, Is.True);
            Assert.That(consumer.PendingCount, Is.Zero);
            Assert.That(consumer.RetainedOpenCount, Is.EqualTo(2));
        }

        [Test]
        public void UnsupportedBoneDoesNotDemandInventedWindingOrBlockSupportedPosition()
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            consumer.Receive(Request(ActorEventKind.YawRequest, 5, 1, TargetChannels.Yaw), false);
            consumer.Receive(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, Vector3.right), true);
            consumer.NativeEmpty();
            Assert.That(consumer.DrainFailed, Is.False);
            Assert.That(consumer.Pending()[0].Kind, Is.EqualTo(EmbodiedTargetConsumer.OperationKind.Unsupported));
            Assert.That(consumer.Pending()[1].Kind, Is.EqualTo(EmbodiedTargetConsumer.OperationKind.Target));
        }

        private static EmbodiedTargetConsumer.Operation Goal(TargetChannels channels, Vector3 position, double yaw)
        {
            return new EmbodiedTargetConsumer.Operation(EmbodiedTargetConsumer.OperationKind.Target,
                Request(ActorEventKind.PositionRequest, 0, 1, channels, position), channels, position, Radians(yaw));
        }

        [Test]
        public void StoppedSourceCompletesBothIndependentGoalsFromActualReadback()
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            goal.Set(Goal(TargetChannels.Position | TargetChannels.Yaw, new Vector3(2f, 9f, 0f), 620d));
            Vector3 position = Vector3.up;
            double yaw = Radians(190d);
            for (int i = 0; i < 400; ++i)
            {
                position = goal.StepPosition(position, .01f, true, null, out _);
                yaw += goal.StepYaw(yaw, .01f);
            }
            Assert.That(position.x, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(position.y, Is.EqualTo(1f));
            Assert.That(Degrees(yaw), Is.EqualTo(620d).Within(1e-8d));
            Assert.That(goal.HasPosition && goal.HasYaw, Is.True);
        }

        [Test]
        public void YawTrafficCannotResetPositionLatchAndReleaseIsChannelLocal()
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            goal.Set(Goal(TargetChannels.Position, new Vector3(.033f, 0f, 0f), 0d));
            Vector3 first = goal.StepPosition(Vector3.zero, .01f, true, null, out _);
            goal.Set(Goal(TargetChannels.Yaw, Vector3.one * 100f, 90d));
            Vector3 second = goal.StepPosition(first, .01f, true, null, out _);
            // The yaw request neither re-latches the position pace nor stops the body: the ramp from rest goes on, a*dt a step
            // (EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared), so the second step is twice the first.
            float a = EmbodiedDriverConfig.MaxLinearAccelerationMetersPerSecondSquared;
            Assert.That(first.x, Is.EqualTo(a * .01f * .01f).Within(1e-7f));
            Assert.That(second.x - first.x, Is.EqualTo(2f * first.x).Within(1e-7f));
            goal.Release(TargetChannels.Yaw);
            Assert.That(goal.HasYaw, Is.False);
            Assert.That(goal.HasPosition, Is.True);
            goal.Release(TargetChannels.Position);
            Assert.That(goal.HasPosition, Is.False);
            Assert.That(goal.StepPosition(second, 1f, true, null, out _), Is.EqualTo(second));
        }

        [TestCase(0.004f, 0.003f)]
        [TestCase(-0.006f, 0.0f)]
        [TestCase(0.0f, -0.0099f)]
        public void AResidualInsideTheSnapDistanceGivesNoMotionHeadingSoTheYawIsKept(float dx, float dz)
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            var at = new Vector3(1f, 0f, 1f);
            goal.Set(Goal(TargetChannels.Position, at + new Vector3(dx, 0f, dz), 0d));
            goal.StepPosition(at, .01f, true, null, out double? heading);
            Assert.That(heading, Is.Null, "a sub-centimetre residual has no direction to face");
            Assert.That(goal.StepYaw(Radians(37d), .01f, heading), Is.EqualTo(0d), "the yaw holds");
        }

        [Test]
        public void AWalkBeyondTheSnapDistanceStillFacesItsMotion()
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            goal.Set(Goal(TargetChannels.Position, new Vector3(0f, 0f, -0.5f), 0d));
            goal.StepPosition(Vector3.zero, .01f, true, null, out double? heading);
            Assert.That(heading.HasValue, Is.True);
            Assert.That(Math.Abs(Degrees(heading.Value)), Is.EqualTo(180d).Within(1e-6d));
            var near = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            near.Set(Goal(TargetChannels.Position, new Vector3(0.011f, 0f, 0f), 0d));
            near.StepPosition(Vector3.zero, .01f, true, null, out double? justOutside);
            Assert.That(Degrees(justOutside.Value), Is.EqualTo(90d).Within(1e-6d));
        }

        [Test]
        public void RefusedPlannedStepDoesNotConsumeRemainingYaw()
        {
            var goal = new EmbodiedTargetGoal(EmbodiedDriverConfig.Default);
            goal.Set(Goal(TargetChannels.Yaw, default, 440d));
            double actual = Radians(190d);
            double refused = goal.StepYaw(actual, .01f);
            Assert.That(goal.StepYaw(actual, .01f), Is.EqualTo(refused));
            Assert.That(Degrees(goal.YawRadians), Is.EqualTo(440d).Within(1e-9d));
            Assert.That(EmbodiedTargetConsumer.Nearest(0d, -Math.PI), Is.EqualTo(Math.PI));
        }

        [Test]
        public void ClosedConsumerCannotPublishOrKeepOperationsForReplacement()
        {
            var consumer = new EmbodiedTargetConsumer(Actor);
            Accept(consumer, 0d);
            consumer.Receive(Request(ActorEventKind.PositionRequest, 0, 1, TargetChannels.Position, Vector3.one), true);
            consumer.NativeEmpty();
            consumer.Close();
            Assert.That(consumer.PendingCount, Is.Zero);
            Assert.That(consumer.TryProposePublication(0, out _), Is.False);
            consumer.Receive(Request(ActorEventKind.PositionRequest, 0, 2, TargetChannels.Position, Vector3.one), true);
            Assert.That(consumer.PendingCount, Is.Zero);
        }
    }
}
#endif
