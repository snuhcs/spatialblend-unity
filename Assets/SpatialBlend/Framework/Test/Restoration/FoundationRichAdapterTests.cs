// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationNative")]
    public sealed class FoundationRichAdapterTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("rich-adapter-world");
        }

        [TearDown]
        public void TearDown()
        {
            _world.Shutdown();
            for (int index = _created.Count - 1; index >= 0; --index)
                UnityEngine.Object.DestroyImmediate(_created[index]);
            _created.Clear();
        }

        [Test]
        public void ExplicitReceiverSubscriptionDeliversNativeCompleteAndDetachmentRelease()
        {
            CreateMappedActors(out _, out SpatialBlendActor receiver,
                out IAnchorPoseFrameSink input, out string error);
            var observed = new List<RichRealizationMailboxEnvelope>();
            receiver.RichRealizationAvailable += observed.Add;
            var options = new RichRealizationSubscriptionOptions
            {
                MailboxCapacity = 4,
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                MaximumClockRateErrorPartsPerMillion = 100d,
                CalibrationLifetimeSeconds = 2d
            };
            Assert.That(receiver.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
            {
                sequence = 0,
                timeSeconds = 12.5d,
                anchors = new[]
                {
                    new AnchorPoseSample
                    {
                        anchorId = "root",
                        worldPosition = new Vector3(.25f, 0f, -.2f),
                        worldRotation = Quaternion.Euler(0f, 31f, 0f)
                    }
                }
            }), Is.True);

            _world.DrainRealizations(0d);
            RichRealizationMailboxEnvelope complete = observed.Find(value =>
                value.Reason == RichMailboxReason.NativeSnapshot && value.Snapshot != null &&
                value.Snapshot.Header.SnapshotStatus == RichSnapshotStatus.Complete);
            Assert.That(complete, Is.Not.Null,
                "The explicitly opted-in receiver must observe the native rich completion.");
            Assert.That(complete.ProducerLifetime, Is.GreaterThan(0UL));
            Assert.That(complete.Snapshot.Header.ReceiverActorId,
                Is.EqualTo(receiver.Registration.NativeActor));
            Assert.That(receiver.TryPollRichRealization(out error), Is.True, error);

            ulong retiredLifetime = complete.ProducerLifetime;
            receiver.Unregister();
            _world.DrainRealizations(1d);
            RichRealizationMailboxEnvelope release = observed.Find(value =>
                value.ProducerLifetime == retiredLifetime &&
                value.Reason == RichMailboxReason.LocalRetirement);
            Assert.That(release, Is.Not.Null,
                "Actor detachment must deliver one terminal release for its old rich lifetime.");
            Assert.That(release.Snapshot, Is.Null);

            Assert.That(receiver.TryRegister(out error), Is.True, error);
            PlayTheDriver(receiver);  // a new native actor has no published sample
            Assert.That(receiver.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
            {
                sequence = 1,
                timeSeconds = 12.6d,
                anchors = new[]
                {
                    new AnchorPoseSample
                    {
                        anchorId = "root",
                        worldPosition = new Vector3(.3f, 0f, -.1f),
                        worldRotation = Quaternion.Euler(0f, 33f, 0f)
                    }
                }
            }), Is.True);
            _world.DrainRealizations(2d);
            RichRealizationMailboxEnvelope replacement = observed.Find(value =>
                value.Reason == RichMailboxReason.NativeSnapshot && value.Snapshot != null &&
                value.ProducerLifetime != retiredLifetime);
            Assert.That(replacement, Is.Not.Null,
                "A re-bound receiver must use a distinct lifetime, not revive old callback state.");
        }

        [Test]
        public void ShutdownDrainsRichRetirementBeforeDestroyingTheSession()
        {
            CreateMappedActors(out _, out SpatialBlendActor receiver,
                out _, out string error);
            var observed = new List<RichRealizationMailboxEnvelope>();
            receiver.RichRealizationAvailable += observed.Add;
            Assert.That(receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
            {
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = 2d
            }, out error), Is.True, error);

            _world.Shutdown();
            Assert.That(observed.Exists(value => value.Reason == RichMailboxReason.LocalRetirement), Is.True,
                "Shutdown has no future Update, so it must drain rich retirement synchronously.");
        }

        [Test]
        public void NativeAgeExpiryIsOneWithdrawalAndDuplicatePollsDoNotRefreshIt()
        {
            CreateMappedActors(out _, out SpatialBlendActor receiver,
                out IAnchorPoseFrameSink input, out string error);
            var observed = new List<RichRealizationMailboxEnvelope>();
            receiver.RichRealizationAvailable += observed.Add;
            const double maximumAge = .1d;
            Assert.That(receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
            {
                MaximumNativeAgeSeconds = maximumAge,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = 5d
            }, out error), Is.True, error);
            Assert.That(ApplyRoot(input, 0, 20d), Is.True);
            _world.DrainRealizations(0d);
            RichRealizationMailboxEnvelope complete = observed.Find(value =>
                value.Reason == RichMailboxReason.NativeSnapshot && value.HasLocalReceiptInterval);
            Assert.That(complete, Is.Not.Null);

            // These are duplicate native revisions. They cannot replace the
            // captured interval/deadline even though poll is requested twice.
            Assert.That(receiver.TryPollRichRealization(out error), Is.True, error);
            Assert.That(receiver.TryPollRichRealization(out error), Is.True, error);
            double expiry = complete.LocalReceiptInterval.EarliestSeconds + maximumAge + .001d;
            _world.TestOnlyRichStopwatchClock = () => expiry;
            _world.DrainRealizations(1d);
            Assert.That(observed.FindAll(value => value.Reason == RichMailboxReason.NativeAgeExpired).Count,
                Is.EqualTo(1));

            Assert.That(receiver.TryPollRichRealization(out error), Is.True, error);
            _world.DrainRealizations(2d);
            Assert.That(observed.FindAll(value => value.Reason == RichMailboxReason.NativeAgeExpired).Count,
                Is.EqualTo(1), "A duplicate poll must not relabel an expired receipt as fresh.");
        }

        [Test]
        public void ActualNewNativeFrameRecoversSameLifetimeAfterAgeWithdrawal()
        {
            CreateMappedActors(out _, out SpatialBlendActor receiver,
                out IAnchorPoseFrameSink input, out string error);
            var observed = new List<RichRealizationMailboxEnvelope>();
            receiver.RichRealizationAvailable += observed.Add;
            Assert.That(receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
            {
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                MaximumClockRateErrorPartsPerMillion = 100d,
                CalibrationLifetimeSeconds = 5d
            }, out error), Is.True, error);
            Assert.That(ApplyRoot(input, 0, 25d), Is.True);
            _world.DrainRealizations(0d);
            RichRealizationMailboxEnvelope first = observed.Find(value =>
                value.Reason == RichMailboxReason.NativeSnapshot && value.Snapshot != null);
            Assert.That(first, Is.Not.Null);

            // This is intentionally one bounded real native-clock interval: it
            // proves recovery against the actual pinned bridge, not a fabricated
            // header timestamp or a recalibration side effect.
            Thread.Sleep(1200);
            _world.DrainRealizations(1d);
            Assert.That(observed.Exists(value => value.Reason == RichMailboxReason.NativeAgeExpired), Is.True);

            Assert.That(ApplyRoot(input, 1, 25.1d), Is.True);
            _world.DrainRealizations(2d);
            RichRealizationMailboxEnvelope recovered = observed.FindLast(value =>
                value.Reason == RichMailboxReason.NativeSnapshot && value.Snapshot != null);
            Assert.That(recovered, Is.Not.Null);
            Assert.That(recovered.ProducerLifetime, Is.EqualTo(first.ProducerLifetime),
                "An age withdrawal must preserve its native lifetime for a genuinely newer frame.");
            Assert.That(recovered.Snapshot.Header.Revision, Is.GreaterThan(first.Snapshot.Header.Revision));
        }

        [Test]
        public void CalibrationExpiryIsTerminalAndReentrantDisableStopsLaterCompleteObservers()
        {
            CreateMappedActors(out _, out SpatialBlendActor receiver,
                out IAnchorPoseFrameSink input, out string error);
            var observed = new List<RichRealizationMailboxEnvelope>();
            int laterCompleteHandlers = 0;
            bool reenabledFromRelease = false;
            string releaseReenableError = null;
            RichRealizationSubscriptionOptions options = null;
            receiver.RichRealizationAvailable += _ =>
            {
                throw new InvalidOperationException("Test observer failure must be contained.");
            };
            receiver.RichRealizationAvailable += envelope =>
            {
                observed.Add(envelope);
                if (envelope.Reason == RichMailboxReason.NativeSnapshot)
                    receiver.DisableRichRealization();
                if (envelope.Reason == RichMailboxReason.LocalRetirement && !reenabledFromRelease)
                {
                    reenabledFromRelease = receiver.TryEnableRichRealization(options, out releaseReenableError);
                }
            };
            receiver.RichRealizationAvailable += envelope =>
            {
                if (envelope.Reason == RichMailboxReason.NativeSnapshot) ++laterCompleteHandlers;
            };
            options = new RichRealizationSubscriptionOptions
            {
                MaximumNativeAgeSeconds = 10d,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = .001d
            };
            Assert.That(receiver.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(ApplyRoot(input, 0, 30d), Is.True);
            _world.DrainRealizations(0d);
            Assert.That(laterCompleteHandlers, Is.EqualTo(0),
                "A reentrant disable must fence later multicast Complete observers.");
            _world.DrainRealizations(1d);
            Assert.That(observed.Exists(value => value.Reason == RichMailboxReason.LocalRetirement), Is.True);
            Assert.That(reenabledFromRelease, Is.True,
                "Terminal cleanup must finish before a release observer creates a replacement: " + releaseReenableError);

            // The release observer already re-enabled. Expire that replacement's
            // pinned mapping without allowing an implicit refresh.
            RichRealizationMailboxEnvelope retired = observed.Find(value =>
                value.Reason == RichMailboxReason.LocalRetirement);
            double afterCalibration = retired.DeliverySeconds + 1d;
            _world.TestOnlyRichStopwatchClock = () => afterCalibration;
            Assert.That(ApplyRoot(input, 1, 30.1d), Is.True);
            _world.DrainRealizations(2d);
            Assert.That(observed.Exists(value => value.Reason == RichMailboxReason.ClockBridgeRejected), Is.True,
                "A calibration expiry cannot silently refresh the native clock mapping.");
            Assert.That(receiver.TryPollRichRealization(out error), Is.False);

            _world.TestOnlyRichStopwatchClock = () => afterCalibration;
            Assert.That(receiver.TryEnableRichRealization(options, out error), Is.True, error);
            receiver.DisableRichRealization();
            _world.DrainRealizations(3d);
        }

        [Test]
        public void FailedPostActivationCalibrationRetiresThenAllowsRetry()
        {
            CreateMappedActors(out _, out SpatialBlendActor receiver,
                out _, out string error);
            receiver.RichRealizationAvailable += _ => { };
            Assert.That(receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
            {
                MailboxCapacity = 1,
                MaximumNativeAgeSeconds = 1d,
                // Valid option shape, but rejected by TryCreate only after the
                // native lifetime has been activated.
                MaximumCalibrationIntervalSeconds = 0d,
                CalibrationLifetimeSeconds = 1d
            }, out error), Is.False);
            Assert.That(error, Does.Contain("Clock uncertainty"));

            // Cleanup must retire before release; an immediate retry proves this
            // actor has no retained native lifetime or managed admission slot.
            Assert.That(receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
            {
                MailboxCapacity = 1,
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = 1d
            }, out error), Is.True, error);
            receiver.DisableRichRealization();
            _world.DrainRealizations(0d);
        }

        [Test]
        public void AggregateCapacityCountsUndrainedLifetimesAndFreesAfterTerminalDelivery()
        {
            CreateMappedActors(out _, out SpatialBlendActor first,
                out _, out string error);
            CreateSecondPairInSameNativeSpaces(out _, out SpatialBlendActor second,
                out _, out error);
            first.RichRealizationAvailable += _ => { };
            second.RichRealizationAvailable += _ => { };
            var single = new RichRealizationSubscriptionOptions
            {
                MailboxCapacity = 1,
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = 1d
            };
            Assert.That(first.TryEnableRichRealization(single, out error), Is.True, error);
            Assert.That(second.TryEnableRichRealization(single, out error), Is.False);
            Assert.That(error, Does.Contain("capacity"));

            first.DisableRichRealization();
            Assert.That(second.TryEnableRichRealization(single, out error), Is.False,
                "An undrained terminal lifetime still occupies the pinned world capacity.");
            _world.DrainRealizations(0d);
            Assert.That(second.TryEnableRichRealization(single, out error), Is.True, error);
            second.DisableRichRealization();
            _world.DrainRealizations(1d);
        }

        [Test]
        public void PerSpaceCallbacksMultiplexReceiversAndOneDisableDoesNotDropTheOther()
        {
            CreateMappedActors(out _, out SpatialBlendActor first,
                out IAnchorPoseFrameSink input, out string error);
            CreateSecondPairInSameNativeSpaces(out _, out SpatialBlendActor second,
                out IAnchorPoseFrameSink secondInput, out error);
            int firstSnapshots = 0;
            int secondSnapshots = 0;
            first.RichRealizationAvailable += envelope =>
            {
                if (envelope.Reason == RichMailboxReason.NativeSnapshot) ++firstSnapshots;
            };
            second.RichRealizationAvailable += envelope =>
            {
                if (envelope.Reason == RichMailboxReason.NativeSnapshot) ++secondSnapshots;
            };
            var options = new RichRealizationSubscriptionOptions
            {
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = 2d
            };
            Assert.That(first.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(second.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(ApplyRoot(input, 0, 40d), Is.True);
            Assert.That(ApplyRoot(secondInput, 0, 40d), Is.True);
            _world.DrainRealizations(0d);
            Assert.That(firstSnapshots, Is.GreaterThan(0));
            Assert.That(secondSnapshots, Is.GreaterThan(0));

            first.DisableRichRealization();
            _world.DrainRealizations(1d);
            int firstBefore = firstSnapshots;
            int secondBefore = secondSnapshots;
            Assert.That(ApplyRoot(secondInput, 1, 40.1d), Is.True);
            _world.DrainRealizations(2d);
            Assert.That(firstSnapshots, Is.EqualTo(firstBefore));
            Assert.That(secondSnapshots, Is.GreaterThan(secondBefore),
                "Removing one actor must not unregister the shared native-space callback.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReentrantCrossActorRetirementUsesANewDrainClock(bool shutdown)
        {
            CreateMappedActors(out _, out SpatialBlendActor first,
                out IAnchorPoseFrameSink firstInput, out string error);
            CreateSecondPairInSameNativeSpaces(out _, out SpatialBlendActor second,
                out IAnchorPoseFrameSink secondInput, out error);
            var observed = new List<RichRealizationMailboxEnvelope>();
            bool triggered = false;
            long retiredActor = 0;
            Action<SpatialBlendActor, RichRealizationMailboxEnvelope> observe = (other, envelope) =>
            {
                observed.Add(envelope);
                if (triggered || envelope.Reason != RichMailboxReason.NativeSnapshot) return;
                triggered = true;
                retiredActor = other.Registration.NativeActor;
                if (shutdown) _world.Shutdown();
                else other.DisableRichRealization();
            };
            first.RichRealizationAvailable += envelope => observe(second, envelope);
            second.RichRealizationAvailable += envelope => observe(first, envelope);
            var options = new RichRealizationSubscriptionOptions
            {
                MaximumNativeAgeSeconds = 1d,
                MaximumCalibrationIntervalSeconds = .1d,
                CalibrationLifetimeSeconds = 5d
            };
            Assert.That(first.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(second.TryEnableRichRealization(options, out error), Is.True, error);
            Assert.That(ApplyRoot(firstInput, 0, 45d), Is.True);
            Assert.That(ApplyRoot(secondInput, 0, 45d), Is.True);
            double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() /
                System.Diagnostics.Stopwatch.Frequency;
            _world.TestOnlyRichStopwatchClock = () => now += .001d;

            Assert.DoesNotThrow(() => _world.DrainRealizations(0d));
            Assert.That(triggered, Is.True, "A real native Complete must exercise observer reentrancy.");
            Assert.DoesNotThrow(() => _world.DrainRealizations(1d));
            Assert.That(observed.FindAll(envelope => envelope.ActorId == retiredActor &&
                envelope.Reason == RichMailboxReason.LocalRetirement).Count, Is.EqualTo(1));
            foreach (RichRealizationMailboxEnvelope envelope in observed)
                Assert.That(envelope.DeliverySeconds, Is.GreaterThanOrEqualTo(envelope.AdapterIngressSeconds));
        }

        private static bool ApplyRoot(IAnchorPoseFrameSink input, long sequence, double time)
        {
            return input.TryApplyWorldFrame(new AnchorPoseFrame
            {
                sequence = sequence,
                timeSeconds = time,
                anchors = new[]
                {
                    new AnchorPoseSample
                    {
                        anchorId = "root",
                        worldPosition = new Vector3(.25f + sequence * .01f, 0f, -.2f),
                        worldRotation = Quaternion.Euler(0f, 31f + sequence, 0f)
                    }
                }
            });
        }

        private void CreateMappedActors(out SpatialBlendActor source, out SpatialBlendActor receiver,
                                        out IAnchorPoseFrameSink input, out string error)
        {
            // ABI 4 scope belongs to the relation family: the
            // supports are descendants of the actor's own element, not an app-declared scope.
            SpatialBlendObject sourceOwner = ObjectIn("adapter-source", "adapter-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("adapter-receiver", "adapter-receiver-owner");
            SpatialBlendObject sourceSupport = SupportUnder(sourceOwner, "adapter-source-seat", Vector3.zero);
            SpatialBlendObject receiverSupport = SupportUnder(receiverOwner, "adapter-receiver-seat",
                new Vector3(8f, 0f, 0f));
            Assert.That(sourceSupport.TryRegister(out error), Is.True, error);
            Assert.That(receiverSupport.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, receiverSupport,
                out IDisposable correspondence, out error), Is.True, error);

            // The test world owns its support correspondence through shutdown.
            Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
            Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
            source = Create<SpatialBlendActor>("adapter-source-actor");
            receiver = Create<SpatialBlendActor>("adapter-receiver-actor");
            source.Configure(sourceOwner, "adapter-person", true, Anchors());
            receiver.Configure(receiverOwner, "adapter-person", false, Anchors());
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            PlayTheDriver(receiver);
            Assert.That(source.TryCreateMotionInput(out input), Is.True);
        }

        private void CreateSecondPairInSameNativeSpaces(out SpatialBlendActor source,
                                                         out SpatialBlendActor receiver,
                                                         out IAnchorPoseFrameSink input,
                                                         out string error)
        {
            SpatialBlendObject sourceOwner = ObjectIn("adapter-source", "adapter-source-two-owner");
            SpatialBlendObject receiverOwner = ObjectIn("adapter-receiver", "adapter-receiver-two-owner");
            SpatialBlendObject sourceSupport = SupportUnder(sourceOwner, "adapter-source-two-seat",
                new Vector3(-3f, 0f, 0f));
            SpatialBlendObject receiverSupport = SupportUnder(receiverOwner, "adapter-receiver-two-seat",
                new Vector3(11f, 0f, 0f));
            Assert.That(sourceSupport.TryRegister(out error), Is.True, error);
            Assert.That(receiverSupport.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, receiverSupport,
                out IDisposable correspondence, out error), Is.True, error);
            Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
            Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
            source = Create<SpatialBlendActor>("adapter-source-two-actor");
            receiver = Create<SpatialBlendActor>("adapter-receiver-two-actor");
            source.Configure(sourceOwner, "adapter-person-two", true, Anchors());
            receiver.Configure(receiverOwner, "adapter-person-two", false, Anchors());
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(receiver.TryRegister(out error), Is.True, error);
            PlayTheDriver(receiver);
            Assert.That(source.TryCreateMotionInput(out input), Is.True);
        }

        private static SpatialBlendActorAnchor[] Anchors()
        {
            return new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero)
            };
        }

        // The core solves a receiver only from a published pose (ABI 3 and 4: the driver is the only writer of the
        // sampling port), so a receiver fixture plays the driver at its registration.
        private static void PlayTheDriver(SpatialBlendActor receiver)
        {
            Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out string error), Is.True, error);
        }

        private SpatialBlendObject SupportUnder(SpatialBlendObject owner, string id, Vector3 center)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(_world, owner.SpaceId, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(value.TrySetSupportPolygon(new[]
            {
                center + new Vector3(-1f, 0f, -1f), center + new Vector3(1f, 0f, -1f),
                center + new Vector3(1f, 0f, 1f), center + new Vector3(-1f, 0f, 1f)
            }, out string error), Is.True, error);
            return value;
        }

        private T Create<T>(string name) where T : Component
        {
            var value = new GameObject(name);
            _created.Add(value);
            T component = value.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(component, null);
            return component;
        }

        private SpatialBlendObject ObjectIn(string space, string id)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(_world, space, id, null, SpatialBlendObjectKind.Object);
            return value;
        }
    }
}
#endif
