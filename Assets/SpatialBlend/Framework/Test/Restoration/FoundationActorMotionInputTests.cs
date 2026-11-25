// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationNative")]
    public sealed class FoundationActorMotionInputTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;

        // ABI 4: the 0.3 s tracking-gap dwell is deleted. An anchor missing from a
        // committed frame withdraws its relations at once, exactly like explicit deactivation (ClearWorldAnchor, which
        // calls sb_clear_actor_observation_anchor); there is no hold on the omission frame and no release time. This is
        // the clock step between consecutive frames of the fixtures below, not a threshold.
        private const double NextFrameSeconds = 0.5d;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("motion-world");
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
        public void RecordedMotionFramesDriveMappedTargetsAndAuthoritativeAbsence()
        {
            string jsonl = Path.GetTempFileName();
            string manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(jsonl,
                    MotionFrame(0, 10d, true, new Vector3(-.25f, 0f, 0f)) + "\n" +
                    Frame(1, 11d, "body", new Vector3(.25f, 0f, 0f)) + "\n" +
                    EmptyFrame(2, 12d) + "\n" +
                    EmptyFrame(3, 12d + NextFrameSeconds));
                WriteManifest(jsonl, manifest);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(jsonl, manifest,
                    new[] { "root", "body" }, out string error), Is.True, error);

                SpatialBlendObject sourceOwner = ObjectIn("source", "motion-source-owner");
                SpatialBlendObject receiverOwner = ObjectIn("target", "motion-receiver-owner");
                SpatialBlendObject sourceSeat = SupportUnder(sourceOwner, "chair-seat", Vector3.zero);
                SpatialBlendObject targetSeat = SupportUnder(receiverOwner, "sofa-seat", new Vector3(20f, 0f, 0f));
                Assert.That(sourceSeat.TryRegister(out error), Is.True, error);
                Assert.That(targetSeat.TryRegister(out error), Is.True, error);
                Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSeat,
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
                    SpatialBlendActor source = Create<SpatialBlendActor>("motion-source");
                    SpatialBlendActor receiver = Create<SpatialBlendActor>("motion-receiver");
                    AnchorTargetController targets = Create<AnchorTargetController>("motion-targets");
                    source.Configure(sourceOwner, "motion-person", true, sourceAnchors);
                    receiver.Configure(receiverOwner, "motion-person", false, receiverAnchors, targets);
                    Assert.That(source.TryRegister(out error), Is.True, error);
                    Assert.That(receiver.TryRegister(out error), Is.True, error);
                    Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                    Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink motionSink), Is.True);
                    RecordedAnchorPoseSource input = Create<RecordedAnchorPoseSource>("recorded-motion");
                    input.SetSink(motionSink);
                    AnchorPoseReplayPlayer player = Create<AnchorPoseReplayPlayer>("motion-player");
                    Assert.That(player.Configure(input, recording, out error), Is.True, error);
                    Assert.That(player.Begin(out error), Is.True, error);
                    Vector3 receivedRoot = default;
                    int roots = 0;
                    receiver.RootTargetAvailable += target => { receivedRoot = target.Position; ++roots; };

                    Assert.That(player.TryAdvanceTo(0d, out error), Is.True, error);
                    _world.DrainRealizations(1d);
                    Assert.That(roots, Is.GreaterThan(0));
                    Assert.That(receivedRoot.x, Is.GreaterThan(10f));
                    Assert.That(targets.TryGetTarget("body", out AnchorTargetController.Target first), Is.True);
                    Assert.That(first.Position.x, Is.GreaterThan(10f));

                    Assert.That(player.TryAdvanceTo(1d, out error), Is.True, error);
                    _world.DrainRealizations(2d);
                    Assert.That(targets.TryGetTarget("body", out AnchorTargetController.Target rootOmitted), Is.True);
                    Assert.That(Vector3.Distance(first.Position, rootOmitted.Position), Is.GreaterThan(.01f));

                    Assert.That(player.TryAdvanceTo(2d, out error), Is.True, error);
                    _world.DrainRealizations(3d);
                    Assert.That(targets.TryGetTarget("body", out _), Is.False,
                        "An omitted body withdraws its relation at once: " +
                        "there is no tracking-gap dwell.");

                    Assert.That(player.TryAdvanceTo(2d + NextFrameSeconds, out error), Is.True, error);
                    _world.DrainRealizations(3.5d);
                    Assert.That(targets.TryGetTarget("body", out _), Is.False,
                        "A body that stays omitted stays withdrawn.");

                    Assert.That(player.Begin(out error), Is.True, error);
                    Assert.That(player.TryAdvanceTo(0d, out error), Is.True, error);
                    _world.DrainRealizations(4d);
                    Assert.That(targets.TryGetTarget("body", out _), Is.True);
                    motionSink.ClearWorldAnchor("body");
                    _world.DrainRealizations(4.5d);
                    Assert.That(targets.TryGetTarget("body", out _), Is.False,
                        "Explicit deactivation releases immediately.");
                    player.Stop();
                    _world.DrainRealizations(5d);
                    Assert.That(targets.TryGetTarget("body", out _), Is.False);

                    source.Unregister();
                    Assert.That(motionSink.TryApplyWorldFrame(new AnchorPoseFrame
                    {
                        sequence = 3, timeSeconds = 13d, anchors = Array.Empty<AnchorPoseSample>()
                    }), Is.False);
                    Assert.That(source.TryRegister(out error), Is.True, error);
                    ((IAnchorPoseFrameReplaySink)motionSink).ResetWorldAnchorReplay();
                    Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink freshSink), Is.True);
                    Assert.That(freshSink.TryApplyWorldFrame(new AnchorPoseFrame
                    {
                        sequence = 0, timeSeconds = 0d,
                        anchors = new[] { new AnchorPoseSample { anchorId = "body",
                            worldPosition = Vector3.zero, worldRotation = Quaternion.identity } }
                    }), Is.True);
                }
                finally { lease.Dispose(); }
            }
            finally
            {
                File.Delete(jsonl);
                File.Delete(manifest);
            }
        }

        [Test]
        public void CanonicalRootOutputRoutesByRoleWhenBodyIsLiterallyRoot()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "named-root-source-owner");
            SpatialBlendObject owner = ObjectIn("target", "named-root-owner");
            SpatialBlendObject sourceSeat = SupportUnder(sourceOwner, "named-source-seat", Vector3.zero);
            SpatialBlendObject targetSeat = SupportUnder(owner, "named-target-seat", new Vector3(20f, 2f, 0f));
            Assert.That(sourceSeat.TryRegister(out string error), Is.True, error);
            Assert.That(targetSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSeat,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(owner.TryRegister(out error), Is.True, error);
                AnchorTargetController targets = Create<AnchorTargetController>("named-root-targets");
                SpatialBlendActor source = Create<SpatialBlendActor>("named-root-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("named-root-receiver");
                var anchors = new[]
                {
                    new SpatialBlendActorAnchor("navigation", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                var receiverAnchors = new[]
                {
                    new SpatialBlendActorAnchor("navigation", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                source.Configure(sourceOwner, "named-root-person", true, anchors);
                receiver.Configure(owner, "named-root-person", false, receiverAnchors, targets);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                int roots = 0;
                receiver.RootTargetAvailable += _ => ++roots;
                Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink sink), Is.True);
                Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 0, timeSeconds = 0d, anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = new Vector3(.25f, 0f, 0f), worldRotation = Quaternion.identity }
                    }
                }), Is.True);
                _world.DrainRealizations(.5d);
                Assert.That(targets.TryGetTarget("root", out AnchorTargetController.Target firstBody), Is.True);
                // Without an observed root the receiver
                // keeps its current root, and the engaged contact is published on
                // the mapped support (here the target seat at x=20 plus the 0.25 m
                // body offset). Reach does not move that contact; it is flagged.
                // Clamping versus emitting a target beyond reach is the open audit item A6.
                Assert.That(firstBody.HasHeight, Is.True);
                Assert.That(firstBody.Position.y, Is.EqualTo(2f).Within(.001f));
                Assert.That(firstBody.Position.x, Is.EqualTo(20.25f).Within(.01f));
                Assert.That(firstBody.Position.z, Is.EqualTo(0f).Within(.01f));
                Assert.That(ReadBeyondReach(receiver, "root"), Is.EqualTo(1));
                Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = 1, timeSeconds = 1d, anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "navigation", worldPosition = Vector3.zero, worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "root", worldPosition = new Vector3(.25f, 0f, 0f), worldRotation = Quaternion.identity }
                    }
                }), Is.True);
                _world.DrainRealizations(1d);
                Assert.That(roots, Is.GreaterThan(0));
                Assert.That(targets.TryGetTarget("root", out AnchorTargetController.Target body), Is.True);
                Assert.That(body.Position.x, Is.GreaterThan(10f));
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void WorkerThreadMotionClearAndResetCannotChangeLiveNativeState()
        {
            SpatialBlendObject sourceOwner = ObjectIn("source", "thread-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("target", "thread-receiver-owner");
            SpatialBlendObject sourceSeat = SupportUnder(sourceOwner, "thread-source-seat", Vector3.zero);
            SpatialBlendObject targetSeat = SupportUnder(receiverOwner, "thread-target-seat", new Vector3(20f, 0f, 0f));
            Assert.That(sourceSeat.TryRegister(out string error), Is.True, error);
            Assert.That(targetSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSeat,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                SpatialBlendActor source = Create<SpatialBlendActor>("thread-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("thread-receiver");
                AnchorTargetController targets = Create<AnchorTargetController>("thread-targets");
                source.Configure(sourceOwner, "thread-person", true, MotionAnchors());
                receiver.Configure(receiverOwner, "thread-person", false, MotionAnchors(), targets);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink sink), Is.True);
                AnchorPoseFrame frame = MotionFrameDto(0, 0d, new Vector3(4f, 0f, 2f), 37f,
                    new Vector3(.25f, 0f, 0f));
                Assert.That(sink.TryApplyWorldFrame(frame), Is.True);
                _world.DrainRealizations(1d);
                Assert.That(targets.TryGetTarget("body", out _), Is.True);
                Assert.That(SpatialBlendAPI.TryReadActorState(source.Registration.NativeSpace,
                    source.Registration.NativeActor, out Vector3 before, out float beforeYaw), Is.True);

                Exception workerException = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        sink.ClearWorldAnchor("root");
                        sink.ClearWorldAnchor("body");
                        ((IAnchorPoseFrameReplaySink)sink).ResetWorldAnchorReplay();
                    }
                    catch (Exception exception) { workerException = exception; }
                });
                worker.IsBackground = true;
                worker.Start();
                Assert.That(worker.Join(TimeSpan.FromSeconds(5d)), Is.True,
                    "Worker-thread sink calls did not complete within the bounded check.");

                Assert.That(workerException, Is.Null);
                Assert.That(sink.TryApplyWorldFrame(frame), Is.False);
                Assert.That(SpatialBlendAPI.TryReadActorState(source.Registration.NativeSpace,
                    source.Registration.NativeActor, out Vector3 after, out float afterYaw), Is.True);
                Assert.That(after, Is.EqualTo(before));
                Assert.That(afterYaw, Is.EqualTo(beforeYaw).Within(.001f));
                _world.DrainRealizations(2d);
                Assert.That(targets.TryGetTarget("body", out _), Is.True);
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void ReplacementMotionSinkReadsFreshNativeReferenceInsteadOfRetiredRoot()
        {
            SpatialBlendObject owner = ObjectIn("source", "replacement-motion-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor source = Create<SpatialBlendActor>("replacement-motion-source");
            source.Configure(owner, "replacement-motion-person", true, MotionAnchors());
            Assert.That(source.TryRegister(out error), Is.True, error);
            Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink oldSink), Is.True);
            Assert.That(oldSink.TryApplyWorldFrame(MotionFrameDto(0, 0d, new Vector3(1f, 0f, 1f),
                11f, Vector3.zero)), Is.True);
            source.Unregister();
            Assert.That(source.TryRegister(out error), Is.True, error);
            Vector3 freshPosition = new Vector3(4f, 0f, 2f);
            Assert.That(source.TryUpdateRoot(freshPosition, 37f, out error), Is.True, error);
            Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink freshSink), Is.True);
            Assert.That(freshSink.TryApplyWorldFrame(new AnchorPoseFrame
            {
                sequence = 0, timeSeconds = 0d, anchors = Array.Empty<AnchorPoseSample>()
            }), Is.True);
            Assert.That(SpatialBlendAPI.TryReadActorState(source.Registration.NativeSpace,
                source.Registration.NativeActor, out Vector3 position, out float yaw), Is.True);
            Assert.That(position, Is.EqualTo(freshPosition));
            Assert.That(yaw, Is.EqualTo(37f).Within(.001f));
            Assert.That(oldSink.TryApplyWorldFrame(MotionFrameDto(1, 1d, Vector3.zero, 0f,
                Vector3.zero)), Is.False);
            ((IAnchorPoseFrameReplaySink)oldSink).ResetWorldAnchorReplay();
            Assert.That(freshSink.TryApplyWorldFrame(new AnchorPoseFrame
            {
                sequence = 0, timeSeconds = 0d, anchors = Array.Empty<AnchorPoseSample>()
            }), Is.False);
            Assert.That(SpatialBlendAPI.TryReadActorState(source.Registration.NativeSpace,
                source.Registration.NativeActor, out position, out yaw), Is.True);
            Assert.That(position, Is.EqualTo(freshPosition));
            Assert.That(yaw, Is.EqualTo(37f).Within(.001f));
        }

        [Test]
        public void RecordedMotionFrames_PreserveZeroIrregularAndEqualTimestampClockWithoutSyntheticClearTime()
        {
            SpatialBlendObject owner = ObjectIn("source", "clock-motion-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>("clock-motion-actor");
            actor.Configure(owner, "clock-motion-person", true, MotionAnchors());
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink sink), Is.True);
            RecordedAnchorPoseSource recorded = Create<RecordedAnchorPoseSource>("clock-recorded");
            Assert.That(recorded.TrySetKnownAnchorIds(new[] { "root", "body" }, out error), Is.True, error);
            recorded.SetSink(sink);

            Assert.That(recorded.TrySubmit(MotionFrameDto(0, 0d, new Vector3(1f, 3f, 2f),
                10f, new Vector3(.1f, 4f, .2f)), out error), Is.True, error);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var first), Is.True);
            Assert.That(first.Epoch, Is.GreaterThan(0));
            Assert.That(first.HasSample, Is.True); Assert.That(first.Sequence, Is.EqualTo(0));
            Assert.That(first.TimeSeconds, Is.EqualTo(0d));

            Assert.That(recorded.TrySubmit(MotionFrameDto(1, .2d, new Vector3(2f, 9f, 2f),
                20f, new Vector3(.2f, 5f, .3f)), out error), Is.True, error);
            Assert.That(recorded.TrySubmit(MotionFrameDto(2, .2d, new Vector3(3f, 11f, 2f),
                30f, new Vector3(.3f, 6f, .4f)), out error), Is.True, error);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var corrected), Is.True);
            Assert.That(corrected.Sequence, Is.EqualTo(2));
            Assert.That(corrected.TimeSeconds, Is.EqualTo(.2d));

            Assert.That(recorded.TrySubmit(new AnchorPoseFrame { sequence = 3, timeSeconds = .7d,
                anchors = Array.Empty<AnchorPoseSample>() }, out error), Is.True, error);
            sink.ClearWorldAnchor("body");
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var cleared), Is.True);
            Assert.That(cleared.Sequence, Is.EqualTo(3));
            Assert.That(cleared.TimeSeconds, Is.EqualTo(.7d),
                "timeless clear must not fabricate or advance sample time");
        }

        [Test]
        public void ReplacedMotionSink_CannotApplyClearOrResetAndFreshEpochRestartsAtZero()
        {
            SpatialBlendObject owner = ObjectIn("source", "lease-motion-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>("lease-motion-actor");
            actor.Configure(owner, "lease-motion-person", true, MotionAnchors());
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink oldSink), Is.True);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink freshSink), Is.True);
            Assert.That(oldSink.TryApplyWorldFrame(MotionFrameDto(0, 0d, Vector3.zero, 0f,
                Vector3.one)), Is.False);
            oldSink.ClearWorldAnchor("body");
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)oldSink).TryResetWorldAnchorReplay(), Is.False);

            Assert.That(freshSink.TryApplyWorldFrame(MotionFrameDto(0, 0d, Vector3.zero, 0f,
                new Vector3(.2f, 1f, .3f))), Is.True);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var accepted), Is.True);
            Assert.That(accepted.HasSample, Is.True);
            ulong oldEpoch = accepted.Epoch;
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)freshSink).TryResetWorldAnchorReplay(), Is.True);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var reset), Is.True);
            Assert.That(reset.Epoch, Is.GreaterThan(oldEpoch));
            Assert.That(reset.HasSample, Is.False); Assert.That(reset.Sequence, Is.EqualTo(-1));
            Assert.That(reset.TimeSeconds, Is.EqualTo(0d));
            Assert.That(freshSink.TryApplyWorldFrame(MotionFrameDto(0, 0d, Vector3.zero, 0f,
                new Vector3(.4f, 2f, .5f))), Is.True);
        }

        [Test]
        public void RecordedSource_CanDetachProvenRetiredMotionSinkWithoutGivingItAuthority()
        {
            SpatialBlendObject owner = ObjectIn("source", "recorded-replace-owner");
            Assert.That(owner.TryRegister(out string error), Is.True, error);
            SpatialBlendActor actor = Create<SpatialBlendActor>("recorded-replace-actor");
            actor.Configure(owner, "recorded-replace-person", true, MotionAnchors());
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink oldSink), Is.True);
            RecordedAnchorPoseSource input = Create<RecordedAnchorPoseSource>("recorded-replace-input");
            Assert.That(input.TrySetKnownAnchorIds(new[] { "root", "body" }, out error), Is.True, error);
            input.SetSink(oldSink);
            Assert.That(input.TrySubmit(MotionFrameDto(0, 0d, Vector3.zero, 0f, Vector3.one), out error), Is.True, error);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink freshSink), Is.True);
            Assert.That(input.TrySetSink(freshSink, out error), Is.True, error);
            Assert.That(input.TrySubmit(MotionFrameDto(0, 0d, Vector3.zero, 0f,
                new Vector3(2f, 3f, 4f)), out error), Is.True, error);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var before), Is.True);
            Assert.That(oldSink.TryApplyWorldFrame(MotionFrameDto(1, 1d, Vector3.zero, 0f,
                new Vector3(9f, 9f, 9f))), Is.False);
            oldSink.ClearWorldAnchor("body");
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)oldSink).TryResetWorldAnchorReplay(), Is.False);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var after), Is.True);
            Assert.That(after.Epoch, Is.EqualTo(before.Epoch));
            Assert.That(after.Sequence, Is.EqualTo(before.Sequence));
            Assert.That(after.TimeSeconds, Is.EqualTo(before.TimeSeconds));

            // Registration retirement is another proven lease retirement.  The
            // source may detach from freshSink only after the actor has acquired
            // a replacement native registration/epoch; neither prior lease gains
            // authority over that replacement.
            actor.Unregister();
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryCreateMotionInput(out IAnchorPoseFrameSink reRegisteredSink), Is.True);
            Assert.That(input.TrySetSink(reRegisteredSink, out error), Is.True, error);
            Assert.That(input.TrySubmit(MotionFrameDto(0, 0d, Vector3.zero, 0f,
                new Vector3(5f, 6f, 7f)), out error), Is.True, error);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var reRegistered), Is.True);
            Assert.That(reRegistered.Sequence, Is.EqualTo(0));
            Assert.That(reRegistered.TimeSeconds, Is.EqualTo(0d));
            Assert.That(freshSink.TryApplyWorldFrame(MotionFrameDto(1, 1d, Vector3.zero, 0f,
                new Vector3(8f, 8f, 8f))), Is.False);
            freshSink.ClearWorldAnchor("body");
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)freshSink).TryResetWorldAnchorReplay(), Is.False);
            Assert.That(actor.Registration.World.TryGetActorObservationClock(actor.Registration,
                out var final), Is.True);
            Assert.That(final.Epoch, Is.EqualTo(reRegistered.Epoch));
            Assert.That(final.Sequence, Is.EqualTo(reRegistered.Sequence));
            Assert.That(final.TimeSeconds, Is.EqualTo(reRegistered.TimeSeconds));
        }

        [Test]
        public void UnicodeObservationNames_ReachLinkedReceiverWithExactNativeLayout()
        {
            // The C ABI is pointer + four aligned doubles on the required native
            // 64-bit target.  Offsets catch a marshal packing drift before a name
            // silently reaches native with the following pose fields corrupted.
            Assert.That(IntPtr.Size, Is.EqualTo(8), "Foundation native verification requires 64-bit ABI.");
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SpatialBlendAPI.SbActorObservation>(),
                Is.EqualTo(40));
            Assert.That(System.Runtime.InteropServices.Marshal.OffsetOf<SpatialBlendAPI.SbActorObservation>("anchor_name").ToInt64(), Is.EqualTo(0));
            Assert.That(System.Runtime.InteropServices.Marshal.OffsetOf<SpatialBlendAPI.SbActorObservation>("position_x").ToInt64(), Is.EqualTo(8));
            Assert.That(System.Runtime.InteropServices.Marshal.OffsetOf<SpatialBlendAPI.SbActorObservation>("position_y").ToInt64(), Is.EqualTo(16));
            Assert.That(System.Runtime.InteropServices.Marshal.OffsetOf<SpatialBlendAPI.SbActorObservation>("position_z").ToInt64(), Is.EqualTo(24));
            Assert.That(System.Runtime.InteropServices.Marshal.OffsetOf<SpatialBlendAPI.SbActorObservation>("yaw").ToInt64(), Is.EqualTo(32));

            const string root = "根";
            const string body = "손";
            const float targetSupportHeight = 3.25f;
            SpatialBlendObject sourceOwner = ObjectIn("unicode-source", "unicode-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("unicode-target", "unicode-receiver-owner");
            SpatialBlendObject sourceSeat = SupportUnder(sourceOwner, "unicode-source-seat",
                new Vector3(0f, 1.5f, 0f));
            SpatialBlendObject targetSeat = SupportUnder(receiverOwner, "unicode-target-seat",
                new Vector3(20f, targetSupportHeight, 0f));
            Assert.That(sourceSeat.TryRegister(out string error), Is.True, error);
            Assert.That(targetSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSeat,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                var sourceAnchors = new[]
                {
                    new SpatialBlendActorAnchor(root, SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor(body, SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                var receiverAnchors = new[]
                {
                    new SpatialBlendActorAnchor(root, SpatialBlendActorAnchorRole.Root, Vector3.zero),
                    new SpatialBlendActorAnchor(body, SpatialBlendActorAnchorRole.Body, Vector3.zero)
                };
                AnchorTargetController controller = Create<AnchorTargetController>("unicode-target-controller");
                SpatialBlendActor source = Create<SpatialBlendActor>("unicode-motion-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("unicode-motion-receiver");
                source.Configure(sourceOwner, "unicode-motion-person", true, sourceAnchors);
                receiver.Configure(receiverOwner, "unicode-motion-person", false, receiverAnchors, controller);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver

                // Preserve the world's production receiver while observing the exact
                // native callback it consumes.  The observer is restored below.
                FieldInfo callbacksField = typeof(SpatialBlendAPI).GetField("targetCallbacks",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(callbacksField, Is.Not.Null);
                var callbacks = (Dictionary<long, OnTargetsDelegate>)callbacksField.GetValue(null);
                Assert.That(callbacks.TryGetValue(receiver.Registration.NativeSpace,
                    out OnTargetsDelegate productionCallback), Is.True);
                var received = new List<RealizationTarget>();
                object receivedLock = new object();
                SpatialBlendAPI.RegisterOnTargets(receiver.Registration.NativeSpace,
                    (space, actor, nativeTargets) =>
                    {
                        if (actor == receiver.Registration.NativeActor)
                            lock (receivedLock) received.AddRange(nativeTargets);
                        productionCallback(space, actor, nativeTargets);
                    });
                try
                {
                    string appliedName = null;
                    bool appliedHasHeight = false;
                    controller.TargetApplied += (name, pose, hasHeight, weight) =>
                    {
                        appliedName = name;
                        appliedHasHeight = hasHeight;
                    };
                    SpatialBlendRootTarget rootTarget = default;
                    int rootCallbacks = 0;
                    receiver.RootTargetAvailable += target => { rootTarget = target; ++rootCallbacks; };

                    Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink sink), Is.True);
                    // A fully specified but off-support body observation must not
                    // manufacture a body relation.  The native support-enter gate
                    // correctly rejects this 5.5 m vertical separation.
                    Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame
                    {
                        sequence = 0,
                        timeSeconds = 0d,
                        anchors = new[]
                        {
                            // Root vertical input is deliberately projected at the atomic ingress.
                            new AnchorPoseSample { anchorId = root, worldPosition = new Vector3(.25f, 9f, .5f), worldRotation = Quaternion.identity },
                            new AnchorPoseSample { anchorId = body, worldPosition = new Vector3(.5f, 7f, .25f), worldRotation = Quaternion.identity }
                        }
                    }), Is.True);
                    _world.DrainRealizations(.1d);
                    lock (receivedLock)
                        Assert.That(received.Exists(target => target.AnchorName == body), Is.False,
                            "The source body is outside its declared support contact gate.");
                    Assert.That(controller.TryGetTarget(body, out _), Is.False);

                    // This is a separate positive boundary case, rather than an
                    // assertion about the support gate's EMA/hysteresis history.
                    // A new acknowledged epoch clears its source/receiver gate
                    // state, and limits the captured callback to contact output.
                    Assert.That(((IAnchorPoseFrameCheckedReplaySink)sink).TryResetWorldAnchorReplay(), Is.True);
                    lock (receivedLock) received.Clear();

                    // Contact with the independent source support has a nonzero
                    // body height which maps to target support height; root stays
                    // horizontal despite its finite ingress height.
                    Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame
                    {
                        sequence = 0,
                        timeSeconds = 0d,
                        anchors = new[]
                        {
                            new AnchorPoseSample { anchorId = root, worldPosition = new Vector3(.25f, 9f, .5f), worldRotation = Quaternion.identity },
                            new AnchorPoseSample { anchorId = body, worldPosition = new Vector3(.5f, 1.5f, .25f), worldRotation = Quaternion.identity }
                        }
                    }), Is.True);
                    _world.DrainRealizations(1d);

                    RealizationTarget receivedBody;
                    RealizationTarget receivedRoot;
                    lock (receivedLock)
                    {
                        receivedBody = received.Find(target => target.AnchorName == body);
                        // Native Spacing output is named by the actor's own root anchor
                        // (core 4.11), here the localized declared name.
                        receivedRoot = received.Find(target => target.KernelType == SbKernelType.Spacing);
                    }
                    Assert.That(receivedBody.AnchorName, Is.EqualTo(body), "UTF-8 callback decoding changed the body name.");
                    Assert.That(receivedBody.KernelType, Is.EqualTo(SbKernelType.AffordancePose));
                    Assert.That(receivedBody.HasHeight, Is.True);
                    Assert.That(receivedRoot.AnchorName, Is.EqualTo(root));
                    Assert.That(receivedRoot.KernelType, Is.EqualTo(SbKernelType.Spacing));
                    Assert.That(receivedRoot.HasHeight, Is.False);
                    Assert.That(receivedRoot.Position.y, Is.EqualTo(0f).Within(.001f));
                    Assert.That(rootCallbacks, Is.GreaterThan(0));
                    Assert.That(rootTarget.Position.y, Is.EqualTo(0f).Within(.001f));
                    Assert.That(appliedName, Is.EqualTo(body));
                    Assert.That(appliedHasHeight, Is.True);
                    Assert.That(controller.TryGetTarget(body, out AnchorTargetController.Target target), Is.True);
                    Assert.That(target.HasHeight, Is.True);
                    Assert.That(target.Position.y, Is.EqualTo(targetSupportHeight).Within(.001f));

                    sink.ClearWorldAnchor(body);
                    _world.DrainRealizations(2d);
                    Assert.That(controller.TryGetTarget(body, out _), Is.False,
                        "Clearing a UTF-8 body anchor must authoritatively release its target.");
                }
                finally
                {
                    SpatialBlendAPI.RegisterOnTargets(receiver.Registration.NativeSpace, productionCallback);
                }
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void ExplicitConvexSeatAndSofaPolygons_MapSeatContactByChartAndReleaseIndependently()
        {
            // These are caller-declared supports, not labels inferred from a
            // scene scan.  Their different frames/shapes make a translated
            // source-world point an invalid target oracle.
            // Both charts start with a horizontal +X edge. This makes their
            // authored chart heading explicit after their different rotations.
            Vector2[] sourceSquare =
            {
                new Vector2(-1f, -1f), new Vector2(1f, -1f),
                new Vector2(1f, 1f), new Vector2(-1f, 1f)
            };
            // This bounded, asymmetric trapezoid keeps all target samples
            // inside default receiver reach without relaxing that policy.
            Vector2[] targetSofaPolygon =
            {
                new Vector2(-.48f, -.32f), new Vector2(.48f, -.32f),
                new Vector2(.26f, .32f), new Vector2(-.16f, .32f)
            };
            const float sourceYaw = 28f;
            const float targetYaw = -47f;
            SpatialBlendObject sourceOwner = ObjectIn("seat-source", "seat-contact-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("seat-target", "seat-contact-receiver-owner");
            SpatialBlendObject sourceSeat = PolygonSupportUnder(sourceOwner, "explicit-chair-seat",
                new Vector3(14f, .35f, 5f), sourceYaw, sourceSquare);
            SpatialBlendObject targetSofa = PolygonSupportUnder(receiverOwner, "explicit-sofa-seat",
                // A nonzero target chart placement makes centre mapping
                // observable independently of the receiver's initial origin.
                new Vector3(.35f, .15f, .25f), targetYaw, targetSofaPolygon);
            Assert.That(sourceSeat.TryRegister(out string error), Is.True, error);
            Assert.That(targetSofa.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSofa,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                SpatialBlendActor source = Create<SpatialBlendActor>("seat-contact-source");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("seat-contact-receiver");
                AnchorTargetController targets = Create<AnchorTargetController>("seat-contact-targets");
                var sourceAnchor = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                        Vector3.zero),
                    new SpatialBlendActorAnchor("seat_contact", SpatialBlendActorAnchorRole.Body,
                        Vector3.zero,
                        positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
                };
                var receiverAnchor = new[]
                {
                    new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root,
                        Vector3.zero),
                    new SpatialBlendActorAnchor("seat_contact", SpatialBlendActorAnchorRole.Body,
                        Vector3.zero,
                        positionDimension: SpatialBlendActorAnchorPositionDimension.Spatial3D)
                };
                source.Configure(sourceOwner, "explicit-seat-person", true, sourceAnchor);
                receiver.Configure(receiverOwner, "explicit-seat-person", false, receiverAnchor, targets);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink input), Is.True,
                    "The atomic motion pipeline is required for source-frame commit.");

                // The values deliberately cover chart center, an off-center interior point, and a point at the source
                // boundary. ABI 4 engagement is the exact geometric fact "the anchor touches the surface", with no
                // tolerance (ABI 4 section 4): a point meant to lie ON the boundary
                // is decided by float32 rounding of the world transform (1e-7 m outside is not touching in the
                // core, exactly on the edge is). The boundary sample is therefore taken 1e-4 m inside the corner,
                // which no float32 rounding of this fixture's transforms can push out.
                Vector2[] samples = { Vector2.zero, new Vector2(.45f, .22f), new Vector2(.9999f, .4999f) };
                long sequence = 0;
                double sourceTime = 10d;
                Vector3 lastMappedSeat = default;
                for (int sampleIndex = 0; sampleIndex < samples.Length; ++sampleIndex)
                {
                    Vector2 sourceLocal = samples[sampleIndex];
                    TestContext.WriteLine($"Seat mapping sample={sampleIndex}, sourceLocal={sourceLocal}");
                    Vector3 sourceWorld = sourceSeat.transform.TransformPoint(
                        new Vector3(sourceLocal.x, 0f, sourceLocal.y));
                    Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                    {
                        sequence = sequence++, timeSeconds = sourceTime++, anchors = new[]
                        {
                            new AnchorPoseSample
                            {
                                anchorId = "root", worldPosition = sourceSeat.transform.position,
                                worldRotation = Quaternion.Euler(0f, sourceYaw, 0f)
                            },
                            new AnchorPoseSample
                            {
                                anchorId = "seat_contact", worldPosition = sourceWorld,
                                worldRotation = Quaternion.Euler(0f, sourceYaw + 31f, 0f)
                            }
                        }
                    }), Is.True);
                    // This direct C-ABI probe distinguishes source grounding
                    // from receiver callback delivery; it is not used to form
                    // the chart-mapping oracle below.
                    NativeRelationState sourceRelation = ReadNativeRelation(
                        source.Registration.NativeSpace, source.Registration.NativeActor,
                        "seat_contact", sourceSeat.Registration.NativeElement,
                        out int relationCount, out string nativeRelations);
                    Assert.That(relationCount, Is.GreaterThan(0),
                        "The committed source observation produced no native relation. " + nativeRelations);
                    Assert.That(sourceRelation.active, Is.EqualTo(1), nativeRelations);
                    Assert.That(sourceRelation.has_contact, Is.EqualTo(1), nativeRelations);
                    _world.DrainRealizations(20d + sampleIndex);
                    Assert.That(targets.TryGetTarget("seat_contact",
                        out AnchorTargetController.Target mapped), Is.True);

                    Vector2 targetLocal = MapByIndependentPolygonChart(sourceLocal,
                        sourceSquare, targetSofaPolygon);
                    Vector3 expected = targetSofa.transform.TransformPoint(
                        new Vector3(targetLocal.x, 0f, targetLocal.y));
                    Assert.That(Vector3.Distance(mapped.Position, expected), Is.LessThan(.015f),
                        "Mapped target must preserve explicit polygon-chart coordinates.");
                    if (sampleIndex > 0)
                    {
                        Vector3 translatedSourceWorld = sourceWorld - sourceSeat.transform.position +
                                                        targetSofa.transform.position;
                        Assert.That(Vector3.Distance(expected, translatedSourceWorld), Is.GreaterThan(.1f),
                            "This asymmetric target must not be satisfied by a source-world translation.");
                    }
                    Assert.That(Quaternion.Angle(mapped.Rotation,
                        Quaternion.Euler(0f, targetYaw + 31f, 0f)), Is.LessThan(.5f));
                    Assert.That(mapped.HasHeight, Is.True);
                    lastMappedSeat = mapped.Position;
                }

                // An empty committed frame omits the anchor. ABI 4 deletes the tracking-gap dwell:
                // the omitted anchor's relation is withdrawn on that frame, as explicit deactivation is.
                double omissionTime = sourceTime;
                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = sequence++, timeSeconds = omissionTime, anchors = Array.Empty<AnchorPoseSample>()
                }), Is.True);
                _world.DrainRealizations(31d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False,
                    "The omission frame withdraws the seat target at once (the tracking-gap dwell is deleted).");

                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = sequence++, timeSeconds = omissionTime + NextFrameSeconds,
                    anchors = Array.Empty<AnchorPoseSample>()
                }), Is.True);
                _world.DrainRealizations(31.5d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False,
                    "A seat contact that stays omitted stays withdrawn.");

                sourceTime = omissionTime + NextFrameSeconds + 1d;
                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = sequence++, timeSeconds = sourceTime, anchors = new[]
                    {
                        new AnchorPoseSample
                        {
                            anchorId = "root", worldPosition = sourceSeat.transform.position,
                            worldRotation = Quaternion.Euler(0f, sourceYaw, 0f)
                        },
                        new AnchorPoseSample
                        {
                            anchorId = "seat_contact",
                            worldPosition = sourceSeat.transform.TransformPoint(Vector3.zero),
                            worldRotation = Quaternion.Euler(0f, sourceYaw + 31f, 0f)
                        }
                    }
                }), Is.True);
                _world.DrainRealizations(32d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.True);
                input.ClearWorldAnchor("seat_contact");
                _world.DrainRealizations(32.5d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False,
                    "Explicit deactivation releases the seat immediately.");
                sourceTime += 1d;
                Assert.That(input.TryApplyWorldFrame(new AnchorPoseFrame
                {
                    sequence = sequence++, timeSeconds = sourceTime, anchors = new[]
                    {
                        new AnchorPoseSample
                        {
                            anchorId = "root", worldPosition = sourceSeat.transform.position,
                            worldRotation = Quaternion.Euler(0f, sourceYaw, 0f)
                        },
                        new AnchorPoseSample
                        {
                            anchorId = "seat_contact",
                            worldPosition = sourceSeat.transform.TransformPoint(Vector3.zero),
                            worldRotation = Quaternion.Euler(0f, sourceYaw + 31f, 0f)
                        }
                    }
                }), Is.True);
                _world.DrainRealizations(32.75d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.True);
                Assert.That(((IAnchorPoseFrameCheckedReplaySink)input).TryResetWorldAnchorReplay(), Is.True);
                _world.DrainRealizations(33d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False);
            }
            finally { lease.Dispose(); }
        }

        [Test]
        public void ExplicitLSeatSurface_UsesVisibilityKernelRadialChartAndRejectsNonStarU()
        {
            // This exact simple L has ordinary area centroid (1.1, 1.1), which
            // lies in its missing notch.  Its strict visibility kernel is [0,1]^2
            // with radial origin (.5, .5).  The target expectations below are
            // hand-computed from that kernel -- never from the production encoder.
            const float chartScale = .2f;
            Vector2[] rawSquare =
            {
                new Vector2(0f, 0f), new Vector2(3f, 0f),
                new Vector2(3f, 3f), new Vector2(0f, 3f)
            };
            Vector2[] rawLShape =
            {
                new Vector2(0f, 0f), new Vector2(3f, 0f), new Vector2(3f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 3f), new Vector2(0f, 3f)
            };
            Assert.That(Vector2.Distance(PolygonCentroid(rawLShape), new Vector2(1.1f, 1.1f)),
                Is.LessThan(1e-5f));
            Assert.That(PointInPolygon(new Vector2(1.1f, 1.1f), rawLShape), Is.False,
                "The ordinary polygon centroid is outside the L and must not become a fallback radial origin.");
            // The values below remain the dimensionless authored chart ratios.
            // Scale both supports and every injected/oracle coordinate uniformly so
            // the 2.5-unit L arm fits the existing physical body-reach policy.
            Vector2[] square = ScaleChart(rawSquare, chartScale);
            Vector2[] lShape = ScaleChart(rawLShape, chartScale);

            AssertLSeatSurfaceMapping("square-to-l", square, lShape, new[]
            {
                new SurfaceMappingSample("center", Chart(.2f, 1.5f, 1.5f), Chart(.2f, .5f, .5f)),
                new SurfaceMappingSample("+x half", Chart(.2f, 2.25f, 1.5f), Chart(.2f, 1.75f, .5f)),
                new SurfaceMappingSample("+x boundary", Chart(.2f, 3f, 1.5f), Chart(.2f, 3f, .5f)),
                new SurfaceMappingSample("+y half", Chart(.2f, 1.5f, 2.25f), Chart(.2f, .5f, 1.75f)),
                new SurfaceMappingSample("+y boundary", Chart(.2f, 1.5f, 3f), Chart(.2f, .5f, 3f)),
                new SurfaceMappingSample("reflex ray", Chart(.2f, 3f, 3f), Chart(.2f, 1f, 1f))
            }, new SurfaceChannelMapping(Chart(.2f, 1.5f, 1.5f), Chart(.2f, .5f, .5f),
                Chart(.2f, 2.25f, 1.5f), Chart(.2f, 1.75f, .5f),
                Chart(.2f, 1.5f, 2.25f), Chart(.2f, .5f, 1.75f)));
            AssertLSeatSurfaceMapping("l-to-square", lShape, square, new[]
            {
                new SurfaceMappingSample("center", Chart(.2f, .5f, .5f), Chart(.2f, 1.5f, 1.5f)),
                new SurfaceMappingSample("+x half", Chart(.2f, 1.75f, .5f), Chart(.2f, 2.25f, 1.5f)),
                new SurfaceMappingSample("+x boundary", Chart(.2f, 3f, .5f), Chart(.2f, 3f, 1.5f)),
                new SurfaceMappingSample("+y half", Chart(.2f, .5f, 1.75f), Chart(.2f, 1.5f, 2.25f)),
                new SurfaceMappingSample("+y boundary", Chart(.2f, .5f, 3f), Chart(.2f, 1.5f, 3f)),
                new SurfaceMappingSample("reflex ray", Chart(.2f, 1f, 1f), Chart(.2f, 3f, 3f))
            }, new SurfaceChannelMapping(Chart(.2f, .5f, .5f), Chart(.2f, 1.5f, 1.5f),
                Chart(.2f, 1.75f, .5f), Chart(.2f, 2.25f, 1.5f),
                Chart(.2f, .5f, 1.75f), Chart(.2f, 1.5f, 2.25f)));
            AssertNonStarUSurfaceRejectsSeatContact(square);
        }

        private void AssertLSeatSurfaceMapping(string name, IReadOnlyList<Vector2> sourcePolygon,
                                               IReadOnlyList<Vector2> targetPolygon,
                                               IReadOnlyList<SurfaceMappingSample> samples,
                                               SurfaceChannelMapping channels)
        {
            const float sourceYaw = 28f;
            const float targetYaw = -47f;
            SpatialBlendObject sourceOwner = ObjectIn("l-source-" + name, "l-source-owner-" + name);
            SpatialBlendObject receiverOwner = ObjectIn("l-target-" + name, "l-target-owner-" + name);
            SpatialBlendObject sourceSeat = PolygonSupportUnder(sourceOwner, "l-source-seat-" + name,
                new Vector3(14f, .35f, 5f), sourceYaw, sourcePolygon);
            SpatialBlendObject targetSeat = PolygonSupportUnder(receiverOwner, "l-target-seat-" + name,
                new Vector3(.35f, .15f, .25f), targetYaw, targetPolygon);
            Assert.That(sourceSeat.TryRegister(out string error), Is.True, error);
            Assert.That(targetSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSeat,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                SpatialBlendActor source = Create<SpatialBlendActor>("l-source-actor-" + name);
                SpatialBlendActor receiver = Create<SpatialBlendActor>("l-receiver-actor-" + name);
                AnchorTargetController targets = Create<AnchorTargetController>("l-targets-" + name);
                SpatialBlendActorAnchor[] sourceAnchors = SurfaceAnchors();
                SpatialBlendActorAnchor[] receiverAnchors = SurfaceAnchors();
                source.Configure(sourceOwner, "l-person-" + name, true, sourceAnchors);
                receiver.Configure(receiverOwner, "l-person-" + name, false, receiverAnchors, targets);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink input), Is.True);
                int rootCallbacks = 0;
                int rootClears = 0;
                receiver.RootTargetAvailable += _ => ++rootCallbacks;
                receiver.RootTargetsCleared += () => ++rootClears;

                Vector2 sourceCenter = samples[0].Source;
                long sequence = 0;
                Vector3 lastMappedSeat = default;
                foreach (SurfaceMappingSample sample in samples)
                {
                    Vector3 sourceCenterWorld = SurfaceWorld(sourceSeat, sourceCenter);
                    Vector3 sourceSeatWorld = SurfaceWorld(sourceSeat, sample.Source);
                    Vector3 sourceHeadWorld = SurfaceWorld(sourceSeat, channels.HeadSource);
                    Vector3 sourceLeftWorld = SurfaceWorld(sourceSeat, channels.LeftSource);
                    Vector3 sourceRightWorld = SurfaceWorld(sourceSeat, channels.RightSource);
                    long frameSequence = sequence++;
                    Assert.That(input.TryApplyWorldFrame(SurfaceFrame(frameSequence, frameSequence, sourceCenterWorld,
                        sourceHeadWorld, sourceLeftWorld, sourceRightWorld, sourceSeatWorld, sourceYaw)), Is.True, sample.Name);
                    NativeRelationState relation = ReadNativeRelation(source.Registration.NativeSpace,
                        source.Registration.NativeActor, "seat_contact",
                        sourceSeat.Registration.NativeElement, out int count, out string diagnostics);
                    Assert.That(count, Is.GreaterThan(0), diagnostics);
                    Assert.That(relation.active, Is.EqualTo(1), diagnostics);
                    Assert.That(relation.has_contact, Is.EqualTo(1), diagnostics);
                    _world.DrainRealizations(40d + sequence);
                    Assert.That(targets.TryGetTarget("seat_contact",
                        out AnchorTargetController.Target mapped), Is.True, sample.Name);
                    Vector3 expected = SurfaceWorld(targetSeat, sample.Target);
                    Assert.That(Vector3.Distance(mapped.Position, expected), Is.LessThan(.02f),
                        name + " " + sample.Name + " must use the declared L kernel/ray chart.");
                    Assert.That(Quaternion.Angle(mapped.Rotation,
                        Quaternion.Euler(0f, targetYaw + 31f, 0f)), Is.LessThan(.5f));
                    Assert.That(mapped.HasHeight, Is.True);
                    Assert.That(mapped.Position.y, Is.EqualTo(targetSeat.transform.position.y).Within(.001f));
                    lastMappedSeat = mapped.Position;
                    Assert.That(rootCallbacks, Is.GreaterThan(0),
                        "The measured root remains a real mapped channel beside seat contact.");
                    AssertObservedSurfaceTarget(targets, "head", SurfaceWorld(targetSeat, channels.HeadTarget),
                        targetYaw + 7f, targetSeat.transform.position.y);
                    AssertObservedSurfaceTarget(targets, "hand_l", SurfaceWorld(targetSeat, channels.LeftTarget),
                        targetYaw + 13f, targetSeat.transform.position.y);
                    AssertObservedSurfaceTarget(targets, "hand_r", SurfaceWorld(targetSeat, channels.RightTarget),
                        targetYaw - 17f, targetSeat.transform.position.y);
                }

                Vector3 centerWorld = SurfaceWorld(sourceSeat, sourceCenter);
                Vector3 headWorld = SurfaceWorld(sourceSeat, channels.HeadSource);
                Vector3 leftWorld = SurfaceWorld(sourceSeat, channels.LeftSource);
                Vector3 rightWorld = SurfaceWorld(sourceSeat, channels.RightSource);
                long omittedSequence = sequence++;
                Assert.That(input.TryApplyWorldFrame(SurfaceFrame(omittedSequence, omittedSequence, centerWorld,
                    headWorld, leftWorld, rightWorld, null, sourceYaw)), Is.True);
                _world.DrainRealizations(60d + sequence);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False,
                    "Omitting seat contact withdraws it at once (there is no tracking-gap dwell), " +
                    "without clearing head/hands.");
                AssertObservedSurfaceTarget(targets, "head", SurfaceWorld(targetSeat, channels.HeadTarget),
                    targetYaw + 7f, targetSeat.transform.position.y);
                AssertObservedSurfaceTarget(targets, "hand_l", SurfaceWorld(targetSeat, channels.LeftTarget),
                    targetYaw + 13f, targetSeat.transform.position.y);
                AssertObservedSurfaceTarget(targets, "hand_r", SurfaceWorld(targetSeat, channels.RightTarget),
                    targetYaw - 17f, targetSeat.transform.position.y);

                long dwellSequence = sequence++;
                Assert.That(input.TryApplyWorldFrame(SurfaceFrame(dwellSequence,
                    omittedSequence + NextFrameSeconds, centerWorld,
                    headWorld, leftWorld, rightWorld, null, sourceYaw)), Is.True);
                _world.DrainRealizations(62d + sequence);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False,
                    "A seat contact that stays omitted stays withdrawn, without clearing head/hands.");
                AssertObservedSurfaceTarget(targets, "head", SurfaceWorld(targetSeat, channels.HeadTarget),
                    targetYaw + 7f, targetSeat.transform.position.y);
                AssertObservedSurfaceTarget(targets, "hand_l", SurfaceWorld(targetSeat, channels.LeftTarget),
                    targetYaw + 13f, targetSeat.transform.position.y);
                AssertObservedSurfaceTarget(targets, "hand_r", SurfaceWorld(targetSeat, channels.RightTarget),
                    targetYaw - 17f, targetSeat.transform.position.y);

                long reintroducedSequence = sequence++;
                Assert.That(input.TryApplyWorldFrame(SurfaceFrame(reintroducedSequence, reintroducedSequence,
                    centerWorld, headWorld, leftWorld, rightWorld, SurfaceWorld(sourceSeat, samples[0].Source),
                    sourceYaw)), Is.True);
                _world.DrainRealizations(65d + sequence);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.True,
                    "Reset must retire a freshly reintroduced, verified seat target.");
                Assert.That(((IAnchorPoseFrameCheckedReplaySink)input).TryResetWorldAnchorReplay(), Is.True);
                _world.DrainRealizations(70d + sequence);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False);
                Assert.That(targets.TryGetTarget("head", out _), Is.False,
                    "Checked reset authoritatively retires every measured channel.");
                Assert.That(targets.TryGetTarget("hand_l", out _), Is.False);
                Assert.That(targets.TryGetTarget("hand_r", out _), Is.False);
                Assert.That(rootClears, Is.GreaterThan(0),
                    "Checked reset must also deliver an authoritative root clear.");
            }
            finally { lease.Dispose(); }
        }

        private void AssertNonStarUSurfaceRejectsSeatContact(IReadOnlyList<Vector2> sourcePolygon)
        {
            Vector2[] uShape =
            {
                new Vector2(0f, 0f), new Vector2(3f, 0f), new Vector2(3f, 3f),
                new Vector2(2f, 3f), new Vector2(2f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 3f), new Vector2(0f, 3f)
            };
            uShape = ScaleChart(uShape, .2f);
            SpatialBlendObject sourceOwner = ObjectIn("u-source", "u-source-owner");
            SpatialBlendObject receiverOwner = ObjectIn("u-target", "u-target-owner");
            SpatialBlendObject sourceSeat = PolygonSupportUnder(sourceOwner, "u-source-seat",
                new Vector3(6f, .4f, -4f), 19f, sourcePolygon);
            SpatialBlendObject targetSeat = PolygonSupportUnder(receiverOwner, "u-target-seat",
                new Vector3(-.4f, .2f, .6f), -31f, uShape);
            Assert.That(sourceSeat.TryRegister(out string error), Is.True, error);
            Assert.That(targetSeat.TryRegister(out error), Is.True, error);
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSeat, targetSeat,
                out IDisposable lease, out error), Is.True, error);
            try
            {
                Assert.That(sourceOwner.TryRegister(out error), Is.True, error);
                Assert.That(receiverOwner.TryRegister(out error), Is.True, error);
                SpatialBlendActor source = Create<SpatialBlendActor>("u-source-actor");
                SpatialBlendActor receiver = Create<SpatialBlendActor>("u-receiver-actor");
                AnchorTargetController targets = Create<AnchorTargetController>("u-targets");
                source.Configure(sourceOwner, "u-person", true, SurfaceAnchors());
                receiver.Configure(receiverOwner, "u-person", false, SurfaceAnchors(), targets);
                Assert.That(source.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryRegister(out error), Is.True, error);
                Assert.That(receiver.TryPublishEmbodiedPose(Vector3.zero, 0f, out error), Is.True, error);  // the test plays the driver
                Assert.That(source.TryCreateMotionInput(out IAnchorPoseFrameSink input), Is.True);
                Vector3 center = SurfaceWorld(sourceSeat, new Vector2(.3f, .3f));
                Assert.That(input.TryApplyWorldFrame(SurfaceFrame(0, 0d, center, center, center, center,
                    center, 19f)), Is.True);
                NativeRelationState relation = ReadNativeRelation(source.Registration.NativeSpace,
                    source.Registration.NativeActor, "seat_contact", sourceSeat.Registration.NativeElement,
                    out int count, out string diagnostics);
                Assert.That(count, Is.GreaterThan(0), diagnostics);
                Assert.That(relation.active, Is.EqualTo(1), diagnostics);
                Assert.That(relation.has_contact, Is.EqualTo(1), diagnostics);
                _world.DrainRealizations(80d);
                Assert.That(targets.TryGetTarget("seat_contact", out _), Is.False,
                    "A simple non-star U support must reject contact, not fall back to a centroid/MVC map.");
            }
            finally { lease.Dispose(); }
        }

        private static SpatialBlendActorAnchor[] SurfaceAnchors() => new[]
        {
            new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
            new SpatialBlendActorAnchor("head", SpatialBlendActorAnchorRole.Body, Vector3.zero),
            new SpatialBlendActorAnchor("hand_l", SpatialBlendActorAnchorRole.Body, Vector3.zero),
            new SpatialBlendActorAnchor("hand_r", SpatialBlendActorAnchorRole.Body, Vector3.zero),
            new SpatialBlendActorAnchor("seat_contact", SpatialBlendActorAnchorRole.Body, Vector3.zero)
        };

        private static AnchorPoseFrame SurfaceFrame(long sequence, double time, Vector3 root,
                                                    Vector3 head, Vector3 left, Vector3 right,
                                                    Vector3? seat, float sourceYaw)
        {
            var anchors = new List<AnchorPoseSample>
            {
                new AnchorPoseSample { anchorId = "root", worldPosition = root,
                    worldRotation = Quaternion.Euler(0f, sourceYaw, 0f) },
                new AnchorPoseSample { anchorId = "head", worldPosition = head,
                    worldRotation = Quaternion.Euler(0f, sourceYaw + 7f, 0f) },
                new AnchorPoseSample { anchorId = "hand_l", worldPosition = left,
                    worldRotation = Quaternion.Euler(0f, sourceYaw + 13f, 0f) },
                new AnchorPoseSample { anchorId = "hand_r", worldPosition = right,
                    worldRotation = Quaternion.Euler(0f, sourceYaw - 17f, 0f) }
            };
            if (seat.HasValue)
                anchors.Add(new AnchorPoseSample { anchorId = "seat_contact", worldPosition = seat.Value,
                    worldRotation = Quaternion.Euler(0f, sourceYaw + 31f, 0f) });
            return new AnchorPoseFrame { sequence = sequence, timeSeconds = time, anchors = anchors.ToArray() };
        }

        private static Vector3 SurfaceWorld(SpatialBlendObject support, Vector2 local) =>
            support.transform.TransformPoint(new Vector3(local.x, 0f, local.y));

        private static Vector2[] ScaleChart(IReadOnlyList<Vector2> raw, float scale)
        {
            var scaled = new Vector2[raw.Count];
            for (int index = 0; index < scaled.Length; ++index) scaled[index] = raw[index] * scale;
            return scaled;
        }

        private static Vector2 Chart(float scale, float x, float y) => new Vector2(x * scale, y * scale);

        private static void AssertObservedSurfaceTarget(AnchorTargetController targets, string name,
                                                        Vector3 expected, float yaw, float expectedHeight)
        {
            Assert.That(targets.TryGetTarget(name, out AnchorTargetController.Target mapped), Is.True,
                name + " must remain an independent observed channel.");
            Assert.That(Vector3.Distance(mapped.Position, expected), Is.LessThan(.02f),
                name + " contact must map through the same authored chart.");
            Assert.That(Quaternion.Angle(mapped.Rotation, Quaternion.Euler(0f, yaw, 0f)), Is.LessThan(.5f),
                name + " yaw must preserve its independent observed orientation.");
            Assert.That(mapped.HasHeight, Is.True);
            Assert.That(mapped.Position.y, Is.EqualTo(expectedHeight).Within(.001f));
        }

        private static bool PointInPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i]; Vector2 b = polygon[j];
                if ((a.y > point.y) == (b.y > point.y)) continue;
                float x = (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
                if (point.x < x) inside = !inside;
            }
            return inside;
        }

        private readonly struct SurfaceMappingSample
        {
            internal SurfaceMappingSample(string name, Vector2 source, Vector2 target)
            { Name = name; Source = source; Target = target; }
            internal string Name { get; } internal Vector2 Source { get; } internal Vector2 Target { get; }
        }

        private readonly struct SurfaceChannelMapping
        {
            internal SurfaceChannelMapping(Vector2 headSource, Vector2 headTarget,
                                           Vector2 leftSource, Vector2 leftTarget,
                                           Vector2 rightSource, Vector2 rightTarget)
            {
                HeadSource = headSource; HeadTarget = headTarget;
                LeftSource = leftSource; LeftTarget = leftTarget;
                RightSource = rightSource; RightTarget = rightTarget;
            }
            internal Vector2 HeadSource { get; } internal Vector2 HeadTarget { get; }
            internal Vector2 LeftSource { get; } internal Vector2 LeftTarget { get; }
            internal Vector2 RightSource { get; } internal Vector2 RightTarget { get; }
        }

        private static SpatialBlendActorAnchor[] MotionAnchors()
        {
            return new[]
            {
                new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            };
        }

        private static AnchorPoseFrame MotionFrameDto(long sequence, double time, Vector3 root,
                                                       float yaw, Vector3 body)
        {
            return new AnchorPoseFrame
            {
                sequence = sequence,
                timeSeconds = time,
                anchors = new[]
                {
                    new AnchorPoseSample { anchorId = "root", worldPosition = root,
                        worldRotation = Quaternion.Euler(0f, yaw, 0f) },
                    new AnchorPoseSample { anchorId = "body", worldPosition = body,
                        worldRotation = Quaternion.identity }
                }
            };
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

        // ABI 4: scope belongs to the relation family, so a
        // support is reached as a descendant of the actor's own element, not through an app-declared scope.
        private SpatialBlendObject SupportUnder(SpatialBlendObject owner, string id, Vector3 center)
        {
            SpatialBlendObject support = Create<SpatialBlendObject>(id);
            support.Configure(_world, owner.SpaceId, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(support.TrySetSupportPolygon(new[]
            {
                center + new Vector3(-1f, 0f, -1f), center + new Vector3(1f, 0f, -1f),
                center + new Vector3(1f, 0f, 1f), center + new Vector3(-1f, 0f, 1f)
            }, out string error), Is.True, error);
            return support;
        }

        private SpatialBlendObject PolygonSupportUnder(SpatialBlendObject owner, string id, Vector3 position, float yaw,
                                                    IReadOnlyList<Vector2> polygon)
        {
            // Keep the support's world chart distinct from its native Object parent
            // chart: source and receiver therefore exercise different parent
            // translations and rotations as well as different support polygons.
            owner.transform.SetPositionAndRotation(position + new Vector3(.3f, -.1f, .25f),
                Quaternion.Euler(0f, yaw - 11f, 0f));
            SpatialBlendObject support = Create<SpatialBlendObject>(id);
            support.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            support.Configure(_world, owner.SpaceId, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(support.TrySetColliderGeometryEnabled(false, out string error), Is.True, error);
            var vertices = new Vector3[polygon.Count];
            for (int index = 0; index < vertices.Length; ++index)
                vertices[index] = new Vector3(polygon[index].x, 0f, polygon[index].y);
            Assert.That(support.TrySetSupportPolygon(vertices, out error), Is.True, error);
            return support;
        }

        private static Vector2 MapByIndependentPolygonChart(Vector2 sourcePoint,
                                                              IReadOnlyList<Vector2> source,
                                                              IReadOnlyList<Vector2> target)
        {
            Vector2 sourceCenter = PolygonCentroid(source);
            Vector2 delta = sourcePoint - sourceCenter;
            if (delta.sqrMagnitude < 1e-10f)
                return PolygonCentroid(target);
            Vector2 direction = delta.normalized;
            float sourceBoundary = RayBoundaryDistance(sourceCenter, direction, source);
            float targetBoundary = RayBoundaryDistance(PolygonCentroid(target), direction, target);
            Assert.That(sourceBoundary, Is.GreaterThan(0f));
            Assert.That(targetBoundary, Is.GreaterThan(0f));
            return PolygonCentroid(target) + direction * (delta.magnitude / sourceBoundary * targetBoundary);
        }

        private static Vector2 PolygonCentroid(IReadOnlyList<Vector2> polygon)
        {
            float twiceArea = 0f;
            Vector2 sum = Vector2.zero;
            for (int index = 0; index < polygon.Count; ++index)
            {
                Vector2 a = polygon[index];
                Vector2 b = polygon[(index + 1) % polygon.Count];
                float cross = a.x * b.y - b.x * a.y;
                twiceArea += cross;
                sum += (a + b) * cross;
            }
            Assert.That(Mathf.Abs(twiceArea), Is.GreaterThan(1e-5f));
            return sum / (3f * twiceArea);
        }

        private static float RayBoundaryDistance(Vector2 origin, Vector2 direction,
                                                 IReadOnlyList<Vector2> polygon)
        {
            float best = float.PositiveInfinity;
            for (int index = 0; index < polygon.Count; ++index)
            {
                Vector2 edgeStart = polygon[index];
                Vector2 edge = polygon[(index + 1) % polygon.Count] - edgeStart;
                float denominator = Cross(direction, edge);
                if (Mathf.Abs(denominator) < 1e-6f) continue;
                Vector2 offset = edgeStart - origin;
                float rayDistance = Cross(offset, edge) / denominator;
                float edgeRatio = Cross(offset, direction) / denominator;
                if (rayDistance >= 0f && edgeRatio >= -1e-5f && edgeRatio <= 1f + 1e-5f)
                    best = Mathf.Min(best, rayDistance);
            }
            return best;
        }

        private static float Cross(Vector2 left, Vector2 right) =>
            left.x * right.y - left.y * right.x;

        private static NativeRelationState ReadNativeRelation(long space, long actor, string anchor,
                                                               long expectedReferent, out int count,
                                                               out string diagnostics)
        {
            var states = new NativeRelationState[16];
            count = sb_get_relation_states(space, actor, states, states.Length);
            diagnostics = DescribeNativeRelations(states, count, expectedReferent);
            Assert.That(count, Is.InRange(0, states.Length), diagnostics);
            for (int index = 0; index < count; ++index)
                if (Marshal.PtrToStringAnsi(states[index].anchor_name) == anchor &&
                    states[index].referent_element_id == expectedReferent)
                    return states[index];
            Assert.Fail("Native extraction has no relation for declared anchor '" + anchor +
                        "' and expected support element " + expectedReferent + ". " + diagnostics);
            return default;
        }

        private static string DescribeNativeRelations(NativeRelationState[] states, int count,
                                                      long expectedReferent)
        {
            if (count < 0 || count > states.Length)
                return "Native relation inventory count=" + count + "; expectedReferent=" +
                       expectedReferent + ".";
            var entries = new StringBuilder();
            for (int index = 0; index < count; ++index)
            {
                NativeRelationState state = states[index];
                if (index > 0) entries.Append(" | ");
                entries.Append("anchor=").Append(Marshal.PtrToStringAnsi(state.anchor_name) ?? "<null>")
                    .Append(",ref=").Append(state.referent_element_id)
                    .Append(",shared=").Append(state.referent_shared_id)
                    .Append(",kernel=").Append(state.kernel_type)
                    .Append(",proximity=").Append(state.proximity)
                    .Append(",contact=").Append(state.has_contact)
                    .Append(",ratio=").Append(state.normalized_radial_ratio)
                    .Append(",xyz=").Append(state.contact_point_world_x).Append(',')
                    .Append(state.contact_point_world_y).Append(',')
                    .Append(state.contact_point_world_z)
                    .Append(",active=").Append(state.active);
            }
            return "expectedReferent=" + expectedReferent + "; nativeRelations=[" + entries + "].";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRelationState
        {
            public IntPtr anchor_name;
            public double proximity, contact_point_world_x, contact_point_world_y,
                          contact_point_world_z, normalized_angle, normalized_radial_ratio,
                          normalized_height_ratio;
            public long referent_element_id, referent_shared_id;
            public int kernel_type, normalized_strategy, has_contact, active;
        }

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_get_relation_states(long space, long actor,
            [Out] NativeRelationState[] states, int capacity);

        // Mirrors sb_realization_target_v2_t. sb_sizeof rejects
        // any other size, so a layout drift fails before a misread flag.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRealizationTargetV2
        {
            public uint struct_size;
            public uint version;
            public IntPtr anchor_name;
            public double arrival_time;
            public double position_x;
            public double position_y;
            public double position_z;
            public double yaw;
            public double weight;
            public double requested_x;
            public double requested_y;
            public double reach_excess_m;
            public long referent_id;
            public int kernel_type;
            public int has_z;
            public int beyond_reach;
            public int reserved;
        }

        private const int RealizationTargetV2StructId = 15;

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong sb_sizeof(int structId);

        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_get_realization_targets_v2(long space, long actor,
            [Out] NativeRealizationTargetV2[] targets, int capacity, uint structSize);

        private static int ReadBeyondReach(SpatialBlendActor receiver, string anchor)
        {
            uint size = (uint)Marshal.SizeOf(typeof(NativeRealizationTargetV2));
            Assert.That(sb_sizeof(RealizationTargetV2StructId), Is.EqualTo((ulong)size),
                "sb_realization_target_v2_t must match this marshal layout.");
            var targets = new NativeRealizationTargetV2[8];
            int count = sb_get_realization_targets_v2(receiver.Registration.NativeSpace,
                receiver.Registration.NativeActor, targets, targets.Length, size);
            Assert.That(count, Is.InRange(1, targets.Length));
            var described = new StringBuilder();
            for (int index = 0; index < count; ++index)
            {
                NativeRealizationTargetV2 candidate = targets[index];
                string name = Marshal.PtrToStringAnsi(candidate.anchor_name);
                if (described.Length > 0) described.Append(" | ");
                described.Append(name).Append("#").Append(candidate.kernel_type)
                    .Append(" beyond=").Append(candidate.beyond_reach)
                    .Append(" excess=").Append(candidate.reach_excess_m)
                    .Append(" xy=").Append(candidate.position_x).Append(',')
                    .Append(candidate.position_y);
                // The locomotion root is also reported as "root". The body anchor
                // in this test is an AffordancePose (kernel 2) that happens to
                // be named root.
                if (name == anchor && candidate.kernel_type == 2)
                    return candidate.beyond_reach;
            }
            Assert.Fail("Native realization has no affordance target named '" + anchor +
                        "'. " + described);
            return 0;
        }

        private SpatialBlendObject ObjectIn(string space, string id)
        {
            SpatialBlendObject value = Create<SpatialBlendObject>(id);
            value.Configure(_world, space, id, null, SpatialBlendObjectKind.Object);
            return value;
        }

        private static string Frame(long sequence, double time, string anchor, Vector3 position)
        {
            return $"{{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":{sequence},\"timeSeconds\":{time.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"anchors\":[{{\"anchorId\":\"{anchor}\",\"worldPosition\":{{\"x\":{position.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"y\":{position.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"z\":{position.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},\"worldRotation\":{{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}}}]}}";
        }

        private static string MotionFrame(long sequence, double time, bool includeRoot, Vector3 body)
        {
            string root = includeRoot
                ? "{\"anchorId\":\"root\",\"worldPosition\":{\"x\":0,\"y\":0,\"z\":0},\"worldRotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}},"
                : string.Empty;
            return $"{{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":{sequence},\"timeSeconds\":{time.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"anchors\":[{root}{{\"anchorId\":\"body\",\"worldPosition\":{{\"x\":{body.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"y\":{body.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"z\":{body.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},\"worldRotation\":{{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}}}]}}";
        }

        private static string EmptyFrame(long sequence, double time) =>
            $"{{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":{sequence},\"timeSeconds\":{time.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"anchors\":[]}}";

        private static void WriteManifest(string jsonl, string manifest)
        {
            byte[] bytes = File.ReadAllBytes(jsonl);
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(manifest,
                "{\"provenance\":{\"orientation_mode\":\"s6-canonical-spatial-anchor-" +
                "yaw-only: Unity yaw = pi/2 - S6 yaw; raw tracker quaternions retained as " +
                "evidence, not relabeled as Unity anchor rotation\"},\"output\":{\"publication_" +
                "state\":\"committed\",\"jsonl_path\":\"" + Path.GetFullPath(jsonl).Replace("\\", "\\\\") +
                "\",\"jsonl_byte_count\":" + bytes.Length + ",\"jsonl_sha256\":\"" + hash +
                "\",\"anchor_pose_schema\":1,\"frame\":\"unity-world\"}}");
        }
    }
}
#endif
