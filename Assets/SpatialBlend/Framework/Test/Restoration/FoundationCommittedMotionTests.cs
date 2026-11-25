// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationNative")]
    public sealed class FoundationCommittedMotionTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;
        [SetUp] public void SetUp() { _world = Create<SpatialBlendLocalWorld>("observation-world"); }
        [TearDown] public void TearDown()
        {
            _world.Shutdown();
            for (int i = _created.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        [Test]
        public void CommittedMotionCopiesAdmittedCoordinatesAndCompleteAvailability()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TrySubscribeCommittedMotion(_ => { }, out _), Is.False);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink sink), Is.True);
            var events = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(events.Add, out var lease), Is.True);
            Assert.That(events, Is.Empty, "Subscription must not replay cached input.");
            AnchorPoseFrame frame = Frame(4, 12.7d);
            Vector3 pelvis = frame.anchors[1].worldPosition;
            float yaw = frame.anchors[1].worldRotation.eulerAngles.y;
            Assert.That(sink.TryApplyWorldFrame(frame), Is.True);
            frame.anchors[0].worldPosition = Vector3.one * 99f;
            frame.anchors[1].anchorId = "changed";
            CommittedMotionObservation admitted = events[0];
            Assert.That(admitted.Kind, Is.EqualTo(CommittedMotionObservationKind.Frame));
            Assert.That(admitted.Source.ActorId, Is.EqualTo("person"));
            Assert.That(admitted.Source.SpaceId, Is.EqualTo("source"));
            Assert.That(admitted.Source.ObjectId, Is.EqualTo("person-owner"));
            Assert.That(admitted.Source.RegistrationId, Is.EqualTo(lease.Source.RegistrationId));
            Assert.That(admitted.Sequence, Is.EqualTo(4));
            Assert.That(admitted.SourceTimeSeconds, Is.EqualTo(12.7d));
            Assert.That(admitted.Anchors[0].Role, Is.EqualTo(SpatialBlendActorAnchorRole.Root));
            Assert.That(admitted.Anchors[0].WorldPosition, Is.EqualTo(new Vector3(2f, 0f, 3f)));
            Assert.That(admitted.Anchors[1].AnchorId, Is.EqualTo("pelvis"));
            Assert.That(admitted.Anchors[1].Role, Is.EqualTo(SpatialBlendActorAnchorRole.Body));
            Assert.That(admitted.Anchors[1].WorldPosition, Is.EqualTo(pelvis));
            Assert.That(admitted.Anchors[1].WorldYawDegrees, Is.EqualTo(yaw));
            Assert.That(admitted.UnavailableAnchorIds, Is.EqualTo(new[] { "head" }));
            Assert.Throws<NotSupportedException>(() =>
                ((IList<CommittedMotionAnchor>)admitted.Anchors)[0] = default);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<string>)admitted.UnavailableAnchorIds)[0] = "pelvis");
            Assert.That(SpatialBlendAPI.TryReadActorState(actor.Registration.NativeSpace,
                actor.Registration.NativeActor, out Vector3 nativeRoot, out float nativeYaw), Is.True);
            Assert.That(nativeRoot, Is.EqualTo(admitted.Anchors[0].WorldPosition));
            Assert.That(nativeYaw, Is.EqualTo(admitted.Anchors[0].WorldYawDegrees).Within(.001f));

            AnchorPoseFrame bodyOnly = Frame(5, 12.7d);
            bodyOnly.anchors = new[] { bodyOnly.anchors[1] };
            Assert.That(sink.TryApplyWorldFrame(bodyOnly), Is.True);
            Assert.That(events[1].Anchors.Count, Is.EqualTo(1));
            Assert.That(events[1].UnavailableAnchorIds, Is.EqualTo(new[] { "navigation", "head" }));
            Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame { sequence = 6,
                timeSeconds = 13d, anchors = Array.Empty<AnchorPoseSample>() }), Is.True);
            Assert.That(events[2].Anchors, Is.Empty);
            Assert.That(events[2].UnavailableAnchorIds, Is.EqualTo(new[] { "navigation", "pelvis", "head" }));
        }

        [Test]
        public void CommittedMotionReceiptPrecedesNativeAdmissionAndNeverUsesSourceTime()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            double clock = 12.5d;
            int reads = 0;
            actor.TestOnlyMotionReceiptClock = () => { ++reads; return clock; };
            bool beforeHadSample = true, nestedApply = true, nestedReset = true, nestedWriter = true;
            actor.TestOnlyBeforeMotionNativeAdmission = () =>
            {
                actor.Registration.World.TryGetActorObservationClock(actor.Registration, out var before);
                beforeHadSample = before.HasSample;
                nestedApply = sink.TryApplyWorldFrame(Frame(0, 0d));
                nestedReset = ((IAnchorPoseFrameCheckedReplaySink)sink).TryResetWorldAnchorReplay();
                nestedWriter = actor.TryCreateMotionInput(out _);
                sink.ClearWorldAnchor("pelvis");
                clock += .75d; // Marshaling/native/queue delay is not erased at callback time.
            };
            CommittedMotionObservation received = null;
            bool nativeCommittedAtDelivery = false;
            Assert.That(actor.TrySubscribeCommittedMotion(value =>
            {
                received = value;
                actor.Registration.World.TryGetActorObservationClock(actor.Registration, out var native);
                nativeCommittedAtDelivery = native.HasSample && native.Sequence == 9 && native.TimeSeconds == 987d;
            }, out _), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(9, 987d)), Is.True);
            Assert.That(beforeHadSample || nestedApply || nestedReset || nestedWriter, Is.False);
            Assert.That(nativeCommittedAtDelivery, Is.True);
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(received.ReceiptTimeSeconds, Is.EqualTo(12.5d));
            Assert.That(clock - received.ReceiptTimeSeconds.Value, Is.EqualTo(.75d));
            Assert.That(received.SourceTimeSeconds, Is.EqualTo(987d));
        }

        [Test]
        public void CommittedMotionRejectedFramesPublishNothingOrConsumeSequence()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            var events = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(events.Add, out _), Is.True);
            AnchorPoseFrame invalid = Frame(0, 0d);
            invalid.anchors[1].anchorId = "unknown";
            Assert.That(sink.TryApplyWorldFrame(invalid), Is.False);
            // Advance the actual native epoch outside this managed writer, forcing
            // native rejection of a managed-valid frame (not a mocked bool).
            Assert.That(_world.TryBeginActorObservationEpoch(actor.Registration, "navigation", out _, out _), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.False);
            Assert.That(events, Is.Empty);
            Assert.That(typeof(SpatialBlendActor).GetField("_lastSequence",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(actor), Is.EqualTo(-1L));
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)sink).TryResetWorldAnchorReplay(), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 1d)), Is.False);
            Assert.That(events.Count, Is.EqualTo(2), "Exactly one reset and one accepted frame.");
        }

        [Test]
        public void CommittedMotionClearAndResetAreTimelessControls()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            var events = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(events.Add, out var lease), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(6, 4d)), Is.True);
            ulong epoch = events[0].Epoch;
            sink.ClearWorldAnchor("unknown");
            sink.ClearWorldAnchor("pelvis");
            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That(events[1].Kind, Is.EqualTo(CommittedMotionObservationKind.Cleared));
            Assert.That(events[1].UnavailableAnchorIds, Is.EqualTo(new[] { "pelvis" }));
            AssertControl(events[1]);
            Assert.That(_world.TryGetActorObservationClock(actor.Registration, out var cleared), Is.True);
            Assert.That(cleared.Sequence, Is.EqualTo(6));
            Assert.That(cleared.TimeSeconds, Is.EqualTo(4d));
            sink.ClearWorldAnchor("navigation");
            Assert.That(events[2].Kind, Is.EqualTo(CommittedMotionObservationKind.Cleared));
            Assert.That(events[2].UnavailableAnchorIds, Is.EqualTo(new[] { "navigation" }));
            AssertControl(events[2]);
            Assert.That(_world.TryGetActorObservationClock(actor.Registration, out var rootCleared), Is.True);
            Assert.That(rootCleared.Sequence, Is.EqualTo(6));
            Assert.That(rootCleared.TimeSeconds, Is.EqualTo(4d));
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)sink).TryResetWorldAnchorReplay(), Is.True);
            Assert.That(events[3].Kind, Is.EqualTo(CommittedMotionObservationKind.Reset));
            Assert.That(events[3].Epoch, Is.GreaterThan(epoch));
            Assert.That(events[3].UnavailableAnchorIds.Count, Is.EqualTo(3));
            AssertControl(events[3]);
            Assert.That(lease.IsActive, Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(events[4].Epoch, Is.EqualTo(events[3].Epoch));
        }

        [Test]
        public void CommittedMotionWriterReplacementAndRetirementFenceExactLeases()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var oldSink), Is.True);
            var oldEvents = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(oldEvents.Add, out var oldLease), Is.True);
            Assert.That(oldSink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(actor.TryCreateMotionInput(out var freshSink), Is.True);
            Assert.That(oldLease.IsActive, Is.False);
            Assert.That(oldEvents.Count, Is.EqualTo(2));
            Assert.That(oldEvents[1].RetirementReason, Is.EqualTo(CommittedMotionRetirementReason.WriterReplaced));
            AssertControl(oldEvents[1]);
            var freshEvents = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(freshEvents.Add, out var freshLease), Is.True);
            Assert.That(freshLease.Source.WriterGeneration, Is.GreaterThan(oldLease.Source.WriterGeneration));
            Assert.That(freshLease.Source.RegistrationId, Is.EqualTo(oldLease.Source.RegistrationId));
            oldLease.Dispose(); oldLease.Dispose();
            oldSink.ClearWorldAnchor("pelvis");
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)oldSink).TryResetWorldAnchorReplay(), Is.False);
            Assert.That(oldSink.TryApplyWorldFrame(Frame(1, 1d)), Is.False);
            Assert.That(freshEvents, Is.Empty);
            Assert.That(freshSink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            actor.Unregister(); actor.Unregister();
            Assert.That(freshEvents.Count, Is.EqualTo(2));
            Assert.That(freshEvents[1].RetirementReason, Is.EqualTo(CommittedMotionRetirementReason.RegistrationRetired));
            Assert.That(freshLease.IsActive, Is.False);
            Assert.That(actor.TryRegister(out string error), Is.True, error);
            Assert.That(actor.TrySubscribeCommittedMotion(_ => { }, out _), Is.False,
                "Old epoch cannot survive actor registration replacement.");
            Assert.That(actor.TryCreateMotionInput(out var replacementSink), Is.True);
            Assert.That(actor.TrySubscribeCommittedMotion(_ => { }, out var replacementLease), Is.True);
            Assert.That(replacementLease.Source.RegistrationId, Is.Not.EqualTo(freshLease.Source.RegistrationId));
            freshLease.Dispose();
            Assert.That(replacementLease.IsActive, Is.True);
            Assert.That(replacementSink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
        }

        [Test]
        public void CommittedMotionObserverMutationAndExceptionsCannotChangeAdmission()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            var second = new List<CommittedMotionObservation>();
            var added = new List<CommittedMotionObservation>();
            CommittedMotionObservationLease firstLease = null;
            AnchorPoseFrame input = Frame(0, 0d);
            bool subscribed = false;
            Assert.That(actor.TrySubscribeCommittedMotion(value =>
            {
                firstLease.Dispose();
                input.anchors[1].worldPosition = Vector3.one * 100f;
                subscribed = actor.TrySubscribeCommittedMotion(added.Add, out _);
                throw new InvalidOperationException("Untrusted observer failure");
            }, out firstLease), Is.True);
            Assert.That(actor.TrySubscribeCommittedMotion(second.Add, out _), Is.True);
            Assert.That(sink.TryApplyWorldFrame(input), Is.True);
            Assert.That(subscribed, Is.True);
            Assert.That(second.Count, Is.EqualTo(1));
            Assert.That(second[0].Anchors[1].WorldPosition.y, Is.EqualTo(1.25f));
            Assert.That(added, Is.Empty);
            Assert.That(sink.TryApplyWorldFrame(Frame(1, 0d)), Is.True);
            Assert.That(added.Count, Is.EqualTo(1));
            Assert.That(added[0].Sequence, Is.EqualTo(1));
        }

        [Test]
        public void CommittedMotionReentrantNewFrameSuppressesOlderDelivery()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            bool nestedAccepted = false;
            Assert.That(actor.TrySubscribeCommittedMotion(value =>
            {
                if (value.Kind == CommittedMotionObservationKind.Frame && value.Sequence == 0)
                    nestedAccepted = sink.TryApplyWorldFrame(Frame(1, 1d));
            }, out _), Is.True);
            var second = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(second.Add, out _), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(nestedAccepted, Is.True);
            Assert.That(second.Count, Is.EqualTo(1));
            Assert.That(second[0].Sequence, Is.EqualTo(1));
            Assert.That(_world.TryGetActorObservationClock(actor.Registration, out var native), Is.True);
            Assert.That(native.Sequence, Is.EqualTo(1));
            Assert.That(sink.TryApplyWorldFrame(Frame(1, 2d)), Is.False);
        }

        [Test]
        public void CommittedMotionReentrantResetSuppressesOlderDelivery()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            bool reset = false;
            Assert.That(actor.TrySubscribeCommittedMotion(value =>
            {
                if (value.Kind == CommittedMotionObservationKind.Frame)
                    reset = ((IAnchorPoseFrameCheckedReplaySink)sink).TryResetWorldAnchorReplay();
            }, out _), Is.True);
            var second = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(second.Add, out _), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(reset, Is.True);
            Assert.That(second.Count, Is.EqualTo(1));
            Assert.That(second[0].Kind, Is.EqualTo(CommittedMotionObservationKind.Reset));
            Assert.That(_world.TryGetActorObservationClock(actor.Registration, out var native), Is.True);
            Assert.That(native.HasSample, Is.False);
        }

        [Test]
        public void CommittedMotionReentrantWriterReplacementSuppressesOlderDelivery()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            IAnchorPoseFrameSink replacement = null;
            bool replaced = false, subscribed = false;
            var fresh = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(value =>
            {
                if (value.Kind != CommittedMotionObservationKind.Frame) return;
                replaced = actor.TryCreateMotionInput(out replacement);
                subscribed = actor.TrySubscribeCommittedMotion(fresh.Add, out _);
            }, out _), Is.True);
            var second = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(second.Add, out _), Is.True);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(replaced && subscribed, Is.True);
            Assert.That(second.Count, Is.EqualTo(1));
            Assert.That(second[0].Kind, Is.EqualTo(CommittedMotionObservationKind.Retired));
            Assert.That(fresh, Is.Empty);
            Assert.That(replacement.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(fresh.Count, Is.EqualTo(1));
        }

        [Test]
        public void CommittedMotionLegacyTransitionsRetireBeforeMutation()
        {
            for (int mode = 0; mode < 4; ++mode)
            {
                SpatialBlendActor actor = Actor("person-" + mode);
                Assert.That(actor.TryCreateMotionInput(out var motion), Is.True);
                Assert.That(motion.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
                Assert.That(actor.TryCreateAnchorPoseInput(out var legacy), Is.True);
                var events = new List<CommittedMotionObservation>();
                Vector3 rootAtRetirement = Vector3.zero;
                bool nestedSubscription = true;
                Assert.That(actor.TrySubscribeCommittedMotion(value =>
                {
                    events.Add(value);
                    if (value.Kind != CommittedMotionObservationKind.Retired) return;
                    SpatialBlendAPI.TryReadActorState(actor.Registration.NativeSpace,
                        actor.Registration.NativeActor, out rootAtRetirement, out _);
                    nestedSubscription = actor.TrySubscribeCommittedMotion(_ => { }, out _);
                }, out var oldLease), Is.True);
                // The existing body-only legacy path cannot clear an observed
                // locomotion root. Apply can write body rows before returning false;
                // neither failed mixed call is an atomic no-op or a live-stream proof.
                if (mode == 0) Assert.That(legacy.TryApplyWorldFrame(Frame(1, 1d)), Is.False);
                if (mode == 1) legacy.ClearWorldAnchor("pelvis");
                if (mode == 2) Assert.That(((IAnchorPoseFrameCheckedReplaySink)legacy).TryResetWorldAnchorReplay(), Is.False);
                if (mode == 3) Assert.That(actor.TryUpdateRoot(new Vector3(20f, 0f, 30f), 91f, out _), Is.True);
                Assert.That(events.Count, Is.EqualTo(1));
                Assert.That(events[0].RetirementReason, Is.EqualTo(CommittedMotionRetirementReason.LegacyMutation));
                AssertControl(events[0]);
                Assert.That(rootAtRetirement, Is.EqualTo(new Vector3(2f, 0f, 3f)));
                Assert.That(nestedSubscription, Is.False);
                Assert.That(oldLease.IsActive, Is.False);
                var fresh = new List<CommittedMotionObservation>();
                Assert.That(actor.TrySubscribeCommittedMotion(fresh.Add, out var freshLease), Is.True);
                oldLease.Dispose();
                Assert.That(motion.TryApplyWorldFrame(Frame(2, 2d)), Is.True);
                Assert.That(fresh.Count, Is.EqualTo(1));
                Assert.That(events.Count, Is.EqualTo(1));
                Assert.That(freshLease.IsActive, Is.True);
                actor.Unregister();
            }
        }

        [Test]
        public void CommittedMotionWorkerAndRenderedTransformsCannotPublishOrRetire()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            var events = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(events.Add, out var lease), Is.True);
            bool applied = true, reset = true, subscribed = true;
            Exception workerError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    applied = sink.TryApplyWorldFrame(Frame(0, 0d));
                    reset = ((IAnchorPoseFrameCheckedReplaySink)sink).TryResetWorldAnchorReplay();
                    subscribed = actor.TrySubscribeCommittedMotion(_ => { }, out _);
                    sink.ClearWorldAnchor("pelvis");
                    lease.Dispose();
                }
                catch (Exception error) { workerError = error; }
            });
            thread.Start(); Assert.That(thread.Join(5000), Is.True);
            Assert.That(workerError, Is.Null);
            Assert.That(applied || reset || subscribed, Is.False);
            Assert.That(lease.IsActive, Is.True);
            actor.transform.SetPositionAndRotation(new Vector3(100f, 50f, 200f), Quaternion.Euler(30f, 42f, 80f));
            _world.DrainRealizations(1d);
            Assert.That(events, Is.Empty);
            Assert.That(sink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(events[0].Anchors[0].WorldPosition, Is.EqualTo(new Vector3(2f, 0f, 3f)));
        }

        [Test]
        public void CommittedMotionRetirementCallbackReplacementCannotBeClearedByOldToken()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryCreateMotionInput(out var oldSink), Is.True);
            bool registered = false, created = false, subscribed = false;
            IAnchorPoseFrameSink freshSink = null;
            CommittedMotionObservationLease freshLease = null;
            var fresh = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(value =>
            {
                if (value.RetirementReason != CommittedMotionRetirementReason.RegistrationRetired) return;
                registered = actor.TryRegister(out _);
                created = actor.TryCreateMotionInput(out freshSink);
                subscribed = actor.TrySubscribeCommittedMotion(fresh.Add, out freshLease);
            }, out var oldLease), Is.True);
            var second = new List<CommittedMotionObservation>();
            Assert.That(actor.TrySubscribeCommittedMotion(second.Add, out _), Is.True);
            actor.Unregister();
            // Prevent fixture teardown from intentionally starting yet another actor.
            oldLease.Dispose();
            Assert.That(registered && created && subscribed, Is.True);
            Assert.That(second.Count, Is.EqualTo(1));
            Assert.That(second[0].Source.RegistrationId, Is.EqualTo(oldLease.Source.RegistrationId));
            Assert.That(freshLease.Source.RegistrationId, Is.Not.EqualTo(oldLease.Source.RegistrationId));
            Assert.That(fresh, Is.Empty);
            Assert.That(oldSink.TryApplyWorldFrame(Frame(0, 0d)), Is.False);
            oldSink.ClearWorldAnchor("pelvis");
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)oldSink).TryResetWorldAnchorReplay(), Is.False);
            Assert.That(freshSink.TryApplyWorldFrame(Frame(0, 0d)), Is.True);
            Assert.That(fresh.Count, Is.EqualTo(1));
            Assert.That(freshLease.IsActive, Is.True);
        }

        private static void AssertControl(CommittedMotionObservation value)
        {
            Assert.That(value.Anchors, Is.Empty);
            Assert.That(value.Sequence, Is.Null);
            Assert.That(value.SourceTimeSeconds, Is.Null);
            Assert.That(value.ReceiptTimeSeconds, Is.Null);
        }
        private SpatialBlendActor Actor(string id = "person")
        {
            SpatialBlendObject owner = Create<SpatialBlendObject>(id + "-owner");
            owner.Configure(_world, "source", id + "-owner", null, SpatialBlendObjectKind.Object);
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>(id);
            actor.Configure(owner, id, true, new[]
            {
                new SpatialBlendActorAnchor("navigation", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("pelvis", SpatialBlendActorAnchorRole.Body, Vector3.zero),
                new SpatialBlendActorAnchor("head", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            });
            Assert.That(actor.TryRegister(out error), Is.True, error);
            return actor;
        }
        private T Create<T>(string name) where T : Component
        {
            var gameObject = new GameObject(name); _created.Add(gameObject);
            T component = gameObject.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            return component;
        }
        private static AnchorPoseFrame Frame(long sequence, double time)
        {
            return new AnchorPoseFrame { sequence = sequence, timeSeconds = time, anchors = new[]
            {
                new AnchorPoseSample { anchorId = "navigation", worldPosition = new Vector3(2f, 9f, 3f),
                    worldRotation = Quaternion.Euler(0f, 37f, 0f) },
                new AnchorPoseSample { anchorId = "pelvis", worldPosition = new Vector3(2.1f, 1.25f, 3.2f),
                    worldRotation = Quaternion.Euler(17f, 43f, 29f) }
            } };
        }
    }
}
#endif
