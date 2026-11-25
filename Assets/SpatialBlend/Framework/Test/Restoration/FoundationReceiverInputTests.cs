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
    [Category("FoundationReceiverInput")]
    public sealed class FoundationReceiverInputFrameTests
    {
        private static ReceiverInputEpoch Input() => new ReceiverInputEpoch(1, 2, "root",
            new[] { "root", "손" }, 1, 17, 19, 1, 2, 1000000000L);
        private static ReceiverAnchorPose Row(string name, bool available = true) =>
            new ReceiverAnchorPose(name, available, Matrix4x4.identity);

        [Test]
        public void FrameDeepOwnsCompleteRowsWithExplicitLossAndOriginalTime()
        {
            var rows = new[] { Row("root"), Row("손", false) };
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 7, 100, 1000, rows,
                out ReceiverInputFrame frame, out string error), Is.True, error);
            rows[0] = Row("changed", false);
            Assert.That(frame.Anchors[0].Name, Is.EqualTo("root"));
            Assert.That(frame.Anchors[1].Available, Is.False);
            Assert.That(frame.Anchors, Is.Not.InstanceOf<ReceiverAnchorPose[]>());
            Assert.That(frame.ObservedAtNanoseconds, Is.EqualTo(100));
            Assert.That(frame.ValidUntilNanoseconds, Is.EqualTo(1000));
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 8, 100, 1000,
                new[] { Row("root", false), Row("손") }, out _, out error), Is.True, error);
        }

        [Test]
        public void OmittedDuplicateUnknownAndInvalidUtf8RowsReject()
        {
            foreach (ReceiverAnchorPose[] rows in new[]
            {
                new[] { Row("root") }, new[] { Row("root"), Row("root") },
                new[] { Row("root"), Row("unknown") }, new[] { Row("root"), Row("\ud800") },
                new[] { Row("root"), Row("a\0b") }, new[] { Row("root"), Row(new string('한', 43)) }
            }) Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 100, 1000, rows, out _, out _), Is.False);
            Assert.That(ReceiverInputValidation.ValidName(new string('한', 42)), Is.True);
        }

        [Test]
        public void NonRigidAndUndefinedHeadingRejectEvenUnavailablePoseMustBeRigid()
        {
            Matrix4x4 shear = Matrix4x4.identity; shear.m01 = .1f;
            Matrix4x4 nonfinite = Matrix4x4.identity; nonfinite.m03 = float.NaN;
            Matrix4x4 projective = Matrix4x4.identity; projective.m30 = .1f;
            foreach (Matrix4x4 invalid in new[] { shear, nonfinite, projective, Matrix4x4.zero,
                Matrix4x4.Scale(new Vector3(-1, 1, 1)), Matrix4x4.Scale(new Vector3(2, 1, 1)) })
                Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 100, 1000,
                    new[] { Row("root"), new ReceiverAnchorPose("손", false, invalid) }, out _, out _), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 100, 1000,
                new[] { new ReceiverAnchorPose("root", true, Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0))), Row("손") },
                out _, out _), Is.False);
        }

        [Test]
        public void AnchorBasisAndRowMajorPreserveAllOrientationAxesAndHeight()
        {
            Matrix4x4 unity = Matrix4x4.TRS(new Vector3(2, 3, 5), Quaternion.Euler(17, 39, -23), Vector3.one);
            Assert.That(ReceiverInputValidation.TryNativePose(unity, out double[] pose), Is.True);
            // Independent columns: native forward = Unity forward, native left =
            // minus Unity right, native up = Unity up, with world Y/Z exchange.
            int[] columns = { 2, 0, 1 }; int[] rows = { 0, 2, 1 };
            for (int r = 0; r < 3; ++r)
                for (int c = 0; c < 3; ++c)
                    Assert.That(pose[r * 4 + c], Is.EqualTo(unity[rows[r], columns[c]] * (c == 1 ? -1 : 1)).Within(1e-7));
            Assert.That(pose[3], Is.EqualTo(2)); Assert.That(pose[7], Is.EqualTo(5)); Assert.That(pose[11], Is.EqualTo(3));
            Assert.That(pose[12], Is.Zero); Assert.That(pose[15], Is.EqualTo(1));
        }

        [Test]
        public void InvalidTimesAndUnboundedCountsRejectBeforeReadingRows()
        {
            var rows = new[] { Row("root"), Row("손") };
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 0, 100, 1000, rows, out _, out _), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 0, 1000, rows, out _, out _), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 100, 100, rows, out _, out _), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 100, 1000000101, rows, out _, out _), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(Input(), 1, 100, 1000, new CountOnly(), out _, out _), Is.False);
        }
        private sealed class CountOnly : IReadOnlyList<ReceiverAnchorPose>
        {
            public int Count => int.MaxValue;
            public ReceiverAnchorPose this[int index] => throw new InvalidOperationException("Must not read oversized rows.");
            public IEnumerator<ReceiverAnchorPose> GetEnumerator() => throw new InvalidOperationException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    [Category("FoundationReceiverInput")]
    public sealed class FoundationReceiverInputNativeTests
    {
        private long _session, _source, _target, _sourceActor, _receiver;
        private ulong _lifetime;
        private ReceiverInputEpoch _input;
        private int _solveStep;
        private readonly List<RichRealizationSnapshot> _snapshots = new List<RichRealizationSnapshot>();

        [SetUp]
        public void SetUp()
        {
            _solveStep = 0;
            _session = SpatialBlendAPI.CreateSession();
            _source = SpatialBlendAPI.AddSpace(_session); _target = SpatialBlendAPI.AddSpace(_session);
            _sourceActor = Actor(_source); _receiver = Actor(_target);
            Assert.That(SpatialBlendAPI.TryBeginReceiverInput(_target, _receiver, "root", new[] { "root", "손" },
                17, 19, out _input, out string error), Is.True, error);
            Assert.That(SpatialBlendAPI.TryRegisterOnRichRealization(_target, _snapshots.Add, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TryActivateRichRealization(_target, _receiver, out _lifetime, out error), Is.True, error);
        }
        [TearDown]
        public void TearDown()
        {
            if (_target != 0) SpatialBlendAPI.TryUnregisterOnRichRealization(_target, out _);
            if (_lifetime != 0)
            {
                SpatialBlendAPI.TryRetireRichRealization(_lifetime, out _);
                SpatialBlendAPI.TryReleaseRichRealization(_lifetime, out _, out _);
            }
            if (_session != 0) SpatialBlendAPI.DestroySession(_session);
            _snapshots.Clear();
        }

        [Test]
        public void FeatureNegotiatesAllLayoutsAndRejectsDescriptorCountOffsetMismatch()
        {
            Assert.That(SpatialBlendAPI.TryGetReceiverInputFeature(out var feature, out string error), Is.True, error);
            Assert.That(feature.max_anchors, Is.EqualTo(64)); Assert.That(feature.max_name_bytes, Is.EqualTo(128));
            var broken = feature; broken.coordinate_convention = 2;
            Assert.That(SpatialBlendAPI.ValidateReceiverInputFeature(broken, _ => 0, (_, __) => 0, out error), Is.False);
            StringAssert.Contains("descriptor", error);
            Assert.That(SpatialBlendAPI.ValidateReceiverInputFeature(feature, _ => 0, (_, __) => 0, out error), Is.False);
            StringAssert.Contains("count", error);
            uint[] counts = { 13, 4, 11, 9, 6 };
            Assert.That(SpatialBlendAPI.ValidateReceiverInputFeature(feature, kind => counts[kind - 1], (_, __) => uint.MaxValue, out error), Is.False);
            StringAssert.Contains("offset", error);
        }

        [Test]
        public void SubmissionDoesNotSolveOrMoveActorAndValidityDoesNotPollOrReinterpretV1()
        {
            ReceiverInputFrame frame = Frame(1);
            int before = _snapshots.Count;
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(frame, out string error), Is.True, error);
            Assert.That(_snapshots.Count, Is.EqualTo(before));
            Assert.That(SpatialBlendAPI.TryReadActorState(_target, _receiver, out Vector3 initial, out _), Is.True);
            Solve();
            RichRealizationSnapshot frozen = _snapshots[_snapshots.Count - 1];
            ReceiverInputValidity value = Query(frozen, 1);
            Assert.That(value.Valid, Is.True); Assert.That(value.Changed, Is.False);
            Assert.That(value.Captured.ObservedAtNanoseconds, Is.EqualTo(frame.ObservedAtNanoseconds));
            Assert.That(value.Captured.RigRevision, Is.EqualTo(17)); Assert.That(value.Captured.CalibrationRevision, Is.EqualTo(19));
            Assert.That(SpatialBlendAPI.TryReadActorState(_target, _receiver, out Vector3 after, out _), Is.True);
            Assert.That(after, Is.EqualTo(initial));
            Assert.That(SpatialBlendAPI.TryGetRichRealizationValidity(_lifetime, frozen.Header.Revision,
                RichRealizationValidityDomain.ReceiverInput, out var old, out int oldResult, out error), Is.True, error);
            Assert.That(oldResult, Is.EqualTo(-5)); Assert.That(old.Valid, Is.False);
            Assert.That(old.UncheckedDomains, Is.EqualTo(RichRealizationValidityDomain.ReceiverInput));
            Assert.That(SpatialBlendAPI.TryPollRichRealization(_lifetime, out var polled, out int result, out error), Is.True, error);
            Assert.That(result, Is.EqualTo(1)); Assert.That(polled.Header.Revision, Is.EqualTo(frozen.Header.Revision));
            Assert.That(polled.Header.NativeReceiptNanoseconds.Value, Is.EqualTo(frozen.Header.NativeReceiptNanoseconds.Value));
        }

        [Test]
        public void NewerFrameClearAndExplicitRootLossInvalidateWithoutNewRichRevision()
        {
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(Frame(1), out string error), Is.True, error);
            Solve(); var frozen = _snapshots[_snapshots.Count - 1]; int count = _snapshots.Count;
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(Frame(2), out error), Is.True, error);
            ReceiverInputValidity newer = Query(frozen, 1);
            Assert.That(newer.Changed, Is.True); Assert.That(newer.Valid, Is.False);
            Assert.That(SpatialBlendAPI.TryClearReceiverInput(_input, out error), Is.True, error);
            Assert.That(Query(frozen, -5).Current.Known, Is.False);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(Frame(2), out error), Is.False, "Clear keeps the sequence fence.");
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(Frame(3, false), out error), Is.True, error);
            Assert.That(Query(frozen, -5).Current.Known, Is.False);
            Assert.That(_snapshots.Count, Is.EqualTo(count));
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(Frame(4), out error), Is.True, error);
            Solve(); Assert.That(_snapshots.Count, Is.GreaterThan(count));
            Assert.That(Query(_snapshots[_snapshots.Count - 1], 1).Valid, Is.True);
        }

        [Test]
        public void OldEpochFutureAcquisitionExpiredAndWrongClockFramesCannotReplaceInput()
        {
            ReceiverInputFrame original = Frame(1);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(original, out string error), Is.True, error);
            Solve(); var frozen = _snapshots[_snapshots.Count - 1];
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(original, out error), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(_input, 2, long.MaxValue - 100, long.MaxValue,
                original.Anchors, out var future, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(future, out error), Is.False);
            Assert.That(ReceiverInputFrame.TryCreate(_input, 2, 1, 2, original.Anchors,
                out var expired, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(expired, out error), Is.False);
            var wrong = new ReceiverInputEpoch(_target, _receiver, "root", new[] { "root", "손" },
                _input.Epoch, 17, 19, _input.ClockId, _input.ClockEpoch + 1, _input.MaximumAgeNanoseconds);
            Assert.That(ReceiverInputFrame.TryCreate(wrong, 2, original.ObservedAtNanoseconds, original.ValidUntilNanoseconds,
                original.Anchors, out var wrongClock, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(wrongClock, out error), Is.False);
            Assert.That(Query(frozen, 1).Valid, Is.True);
            Assert.That(SpatialBlendAPI.TryBeginReceiverInput(_target, _receiver, "root", new[] { "root", "손" },
                20, 21, out _input, out error), Is.True, error);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(original, out error), Is.False);
            Assert.That(Query(frozen, -5).Valid, Is.False);
        }

        [Test]
        public void NativeExpiryDoesNotManufactureRevisionAndRetiredReleasedTokensStayDistinct()
        {
            var frame = Frame(1, duration: 500000000L);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(frame, out string error), Is.True, error);
            Solve(); var frozen = _snapshots[_snapshots.Count - 1];
            ReceiverInputValidity before = Query(frozen, 1);
            Thread.Sleep(550);
            ReceiverInputValidity expired = Query(frozen, -5);
            Assert.That(expired.Current.Revision, Is.EqualTo(before.Current.Revision));
            Assert.That(expired.Changed, Is.False); Assert.That(expired.Valid, Is.False);
            Assert.That(SpatialBlendAPI.TryGetReceiverInputValidity(_lifetime, frozen.Header.Revision + 1,
                out _, out int result, out error), Is.True, error);
            Assert.That(result, Is.EqualTo(-6));
            Assert.That(SpatialBlendAPI.TryRetireRichRealization(_lifetime, out error), Is.True, error);
            Assert.That(Query(frozen, -5), Is.Null);
            Assert.That(SpatialBlendAPI.TryReleaseRichRealization(_lifetime, out _, out error), Is.True, error);
            Assert.That(Query(frozen, -2), Is.Null); _lifetime = 0;
        }

        [Test]
        public void NativeSolveRetainsPitchedRootHeadingAndIndependentFacingAnchorXYZ()
        {
            ReceiverInputFrame prototype = Frame(1);
            var rows = new[] { prototype.Anchors[0], new ReceiverAnchorPose("손", true,
                Matrix4x4.TRS(new Vector3(1.25f, 1.7f, 2.75f), Quaternion.Euler(21, -37, 11), Vector3.one)) };
            Assert.That(ReceiverInputFrame.TryCreate(_input, 1, prototype.ObservedAtNanoseconds,
                prototype.ValidUntilNanoseconds, rows, out var input, out string error), Is.True, error);
            long obstacle = SpatialBlendAPI.FindElementBySharedId(_source, 7002);
            SpatialBlendAPI.Bind(_source, _sourceActor, "손", SbKernelType.Facing);
            // A static source issues a request only at contact (R = 1, update count 1): a Facing relation whose look-at is
            // off its referent has an infinite count and no weight (as v1
            // Angular1D). The source hand therefore looks at the obstacle's centre (5, 5) from (3, 4).
            SpatialBlendAPI.SetAnchorTransform(_source, _sourceActor, "손", new Vector3(3, 1.6f, 4),
                Mathf.Atan2(5f - 3f, 5f - 4f) * Mathf.Rad2Deg);
            Assert.That(SpatialBlendAPI.TrySubmitReceiverInput(input, out error), Is.True, error);
            Solve(); var frozen = _snapshots[_snapshots.Count - 1];
            var facing = new List<RichRealizationChannel>(frozen.Channels).Find(c => c.Anchor == "손" && c.Family == RichRelationFamily.Facing);
            var root = new List<RichRealizationChannel>(frozen.Channels).Find(c => c.Anchor == "root" && c.Family == RichRelationFamily.Spacing);
            Assert.That(facing, Is.Not.Null); Assert.That(facing.HasGoal, Is.True);
            Assert.That(facing.AdmittedPosition.X, Is.EqualTo(1.25).Within(1e-6));
            Assert.That(facing.AdmittedPosition.Y, Is.EqualTo(2.75).Within(1e-6));
            Assert.That(facing.AdmittedPosition.Z, Is.EqualTo(1.7).Within(1e-6));
            Assert.That(root, Is.Not.Null); Assert.That(root.HasGoal, Is.True);
            Vector4 forward = rows[0].WorldPose.GetColumn(2);
            Assert.That(root.YawRadians, Is.EqualTo(Math.Atan2(forward.z, forward.x)).Within(1e-6));
        }

        private ReceiverInputValidity Query(RichRealizationSnapshot snapshot, int expected)
        {
            Assert.That(SpatialBlendAPI.TryGetReceiverInputValidity(_lifetime, snapshot.Header.Revision,
                out var value, out int result, out string error), Is.True, error);
            Assert.That(result, Is.EqualTo(expected)); return value;
        }
        private ReceiverInputFrame Frame(ulong sequence, bool root = true, long duration = 1000000000L)
        {
            Assert.That(SpatialBlendAPI.TryReadReceiverInputTime(_input, out long now, out string error), Is.True, error);
            Assert.That(ReceiverInputFrame.TryCreate(_input, sequence, now, now + duration,
                new[] { new ReceiverAnchorPose("root", root, Matrix4x4.TRS(new Vector3(2, 1, 3), Quaternion.Euler(17, 27, -13), Vector3.one)),
                    new ReceiverAnchorPose("손", false, Matrix4x4.identity) }, out var frame, out error), Is.True, error);
            return frame;
        }
        // Legacy source calls are movement/heartbeat gated. Each requested solve
        // deliberately supplies a new source pose; receiver submission alone
        // must not cause a solve or refresh an old native receipt.
        private void Solve() => SpatialBlendAPI.UpdateActorState(_source, _sourceActor,
            new Vector3(3 + .25f * ++_solveStep, 0, 4), 0);
        private static long Actor(long space)
        {
            long room = SpatialBlendAPI.AddElement(space); SpatialBlendAPI.SetElementSharedId(space, room, 7001);
            SpatialBlendAPI.SetElementPolygon(space, room, SbGeometryRole.Navigable,
                new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) });
            long obstacle = SpatialBlendAPI.AddElement(space, room); SpatialBlendAPI.SetElementSharedId(space, obstacle, 7002);
            SpatialBlendAPI.SetElementBox(space, obstacle, SbGeometryRole.Obstacle, new Vector3(4, 0, 4), new Vector3(6, 1, 6));
            long actor = SpatialBlendAPI.CreateActor(space); SpatialBlendAPI.SetActorElement(space, actor, room);
            SpatialBlendAPI.AddAnchor(space, actor, "root", Vector3.zero); SpatialBlendAPI.AddAnchor(space, actor, "손", Vector3.zero);
            SpatialBlendAPI.Bind(space, actor, "root", SbKernelType.Spacing); return actor;
        }
    }

    [Category("FoundationReceiverInput")]
    public sealed class FoundationReceiverInputRuntimeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;
        [SetUp] public void SetUp() { _world = Create<SpatialBlendLocalWorld>("receiver-world"); }
        [TearDown] public void TearDown()
        {
            _world.Shutdown();
            for (int i = _created.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        [Test]
        public void ExactWriterIdentitySurvivesNumericEpochReuseAndClearDoesNotReviveLegacyOutput()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var first, out string error), Is.True, error);
            ReceiverInputFrame old = Frame(first, 1);
            Assert.That(first.TrySubmit(old, out error), Is.True, error);
            Assert.That(first.TryClear(out error), Is.True, error);
            Assert.That(first.TrySubmit(old, out error), Is.False);
            Assert.That(first.TrySubmit(Frame(first, 2), out error), Is.True, error);
            actor.Unregister();
            Assert.That(actor.TryRegister(out error), Is.True, error);
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var next, out error), Is.True, error);
            Assert.That(next.Input.Epoch, Is.EqualTo(first.Input.Epoch), "Opaque identity must protect even native epoch-number reuse.");
            Assert.That(first.TrySubmit(old, out error), Is.False);
            Assert.That(next.TrySubmit(old, out error), Is.False);
            Assert.That(next.TrySubmit(Frame(next, 1), out error), Is.True, error);
            Assert.That(first.TryClose(out error), Is.True, error);
            Assert.That(next.TryClear(out error), Is.True, error);
            Assert.That(next.TryClose(out error), Is.True, error);
            Assert.That(actor.Registration.ReceiverInputTargetOnly, Is.True);
            Assert.That(next.TrySubmit(FrameFor(next.Input, 2), out error), Is.False);
        }

        [Test]
        public void NewBeginRetiresOldWriterAndShutdownPreventsLateAccess()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var first, out string error), Is.True, error);
            Assert.That(actor.TryBeginReceiverInput(3, 4, out var next, out error), Is.True, error);
            Assert.That(next.Input.Epoch, Is.GreaterThan(first.Input.Epoch));
            Assert.That(first.TryReadAcquisitionClock(out _, out error), Is.False);
            Assert.That(next.TrySubmit(Frame(next, 1), out error), Is.True, error);
            _world.Shutdown();
            Assert.That(next.TryClear(out error), Is.False);
            Assert.That(next.TryReadAcquisitionClock(out _, out error), Is.False);
            Assert.That(next.TryClose(out error), Is.True, error);
        }

        [Test]
        public void WrongThreadAndSourceRoleFailWithoutRetiringCurrentWriter()
        {
            SpatialBlendActor source = Actor("source", true);
            Assert.That(source.TryBeginReceiverInput(1, 2, out _, out _), Is.False);
            SpatialBlendActor receiver = Actor();
            Assert.That(receiver.TryBeginReceiverInput(1, 2, out var input, out string error), Is.True, error);
            ReceiverInputFrame frame = Frame(input, 1); bool submit = true, close = true;
            var thread = new Thread(() => { submit = input.TrySubmit(frame, out _); close = input.TryClose(out _); });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(submit, Is.False); Assert.That(close, Is.False);
            Assert.That(input.TrySubmit(frame, out error), Is.True, error);
        }

        [Test]
        public void BeginWithdrawsReducedOutputsAndReentrantUnregisterCannotReturnLiveWriter()
        {
            SpatialBlendActor actor = Actor(); int releases = 0, roots = 0;
            actor.RootTargetAvailable += _ => ++roots;
            actor.RootTargetsCleared += () => ++releases;
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var input, out string error), Is.True, error);
            Assert.That(releases, Is.EqualTo(1));
            actor.DeliverRealization(actor.Registration, 0, new[] { new RealizationTarget { AnchorName = "root",
                KernelType = SbKernelType.Spacing, Position = new Vector3(3, 0, 4), Rotation = Quaternion.identity } }, 1);
            Assert.That(roots, Is.Zero);
            Assert.That(input.TryClose(out error), Is.True, error);
            actor.DeliverRealization(actor.Registration, 0, new[] { new RealizationTarget { AnchorName = "root",
                KernelType = SbKernelType.Spacing, Position = new Vector3(3, 0, 4), Rotation = Quaternion.identity } }, 2);
            Assert.That(roots, Is.Zero);
            actor.RootTargetsCleared += actor.Unregister;
            Assert.That(actor.TryBeginReceiverInput(3, 4, out var rejected, out error), Is.False);
            Assert.That(rejected, Is.Null); Assert.That(actor.IsRegistered, Is.False);
        }

        [Test]
        public void NestedBeginIsRejectedBeforeAnotherNativeEpochIsAdmitted()
        {
            SpatialBlendActor actor = Actor(); int callbacks = 0;
            bool admitted = true; ReceiverInputWriter nested = null; string nestedError = null;
            actor.RootTargetsCleared += () =>
            {
                ++callbacks;
                admitted = actor.TryBeginReceiverInput(3, 4, out nested, out nestedError);
            };
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var writer, out string error), Is.True, error);
            Assert.That(admitted, Is.False); Assert.That(nested, Is.Null);
            StringAssert.Contains("in progress", nestedError);
            Assert.That(callbacks, Is.EqualTo(1)); Assert.That(writer.Input.Epoch, Is.EqualTo(1));
            Assert.That(writer.TrySubmit(Frame(writer, 1), out error), Is.True, error);
        }

        [Test]
        public void OffMainShutdownRejectsBeforeAnyRetirementOrNativeDestruction()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var writer, out string error), Is.True, error);
            Exception failure = null;
            var thread = new Thread(() => { try { _world.Shutdown(); } catch (Exception e) { failure = e; } });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
            Assert.That(actor.IsRegistered, Is.True); Assert.That(_world.IsInitialized, Is.True);
            Assert.That(writer.TrySubmit(Frame(writer, 1), out error), Is.True, error);
        }

        [Test]
        public void ExplicitEditModeInitializationPinsThreadWithoutRequiringAwake()
        {
            var go = new GameObject("explicit-world"); _created.Add(go);
            var world = go.AddComponent<SpatialBlendLocalWorld>();
            Assert.DoesNotThrow(world.Shutdown);
            Assert.That(world.TryInitialize(out string error), Is.True, error);
            Exception failure = null; bool initialized = true;
            var thread = new Thread(() =>
            {
                initialized = world.TryInitialize(out _);
                try { world.Shutdown(); } catch (Exception e) { failure = e; }
            });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(initialized, Is.False); Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
            Assert.That(world.IsInitialized, Is.True); Assert.DoesNotThrow(world.Shutdown);
        }

        [Test]
        public void WorkerFirstCannotClaimNeverAwokenWorldOrAllocateItsSession()
        {
            var go = new GameObject("worker-first-world"); _created.Add(go);
            var world = go.AddComponent<SpatialBlendLocalWorld>();
            FieldInfo ownerThread = typeof(SpatialBlendLocalWorld).GetField("_mainThread", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(ownerThread.GetValue(world), Is.EqualTo(0), "This EditMode fixture must exercise initialization before Awake.");
            bool initialized = true; string workerError = null;
            var thread = new Thread(() => initialized = world.TryInitialize(out workerError));
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(initialized, Is.False); StringAssert.Contains("Unity main thread", workerError);
            Assert.That(ownerThread.GetValue(world), Is.EqualTo(0)); Assert.That(world.IsInitialized, Is.False);
            Assert.That(world.TryInitialize(out string error), Is.True, error);
            Assert.DoesNotThrow(world.Shutdown);
        }

        [Test]
        public void FailedCloseStopsWritesButKeepsExactNativeClearRetryOwnership()
        {
            SpatialBlendActor actor = Actor();
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var writer, out string error), Is.True, error);
            ReceiverInputFrame frame = Frame(writer, 1);
            Assert.That(writer.TrySubmit(frame, out error), Is.True, error);
            int attempts = 0; _world.TestOnlyRejectReceiverClear = () => ++attempts == 1;
            Assert.That(writer.TryClose(out error), Is.False);
            Assert.That(writer.TrySubmit(frame, out error), Is.False);
            Assert.That(writer.TryReadAcquisitionClock(out _, out error), Is.False);
            Assert.That(writer.TryClose(out error), Is.True, error); Assert.That(attempts, Is.EqualTo(2));
            Assert.That(writer.TryClose(out error), Is.True, error); Assert.That(attempts, Is.EqualTo(2));
            _world.TestOnlyRejectReceiverClear = null;
            Assert.That(actor.TryBeginReceiverInput(3, 4, out var next, out error), Is.True, error);
            Assert.That(next.TrySubmit(Frame(next, 1), out error), Is.True, error);
        }

        [Test]
        public void BeginCancelsOldBodyGoalsAndRelinquishesTheirLeasesWithoutReacquisition()
        {
            AnchorTargetController targets = Create<AnchorTargetController>("body-goals");
            SpatialBlendActor actor = Actor(targets: targets);
            var legacy = new[] { new RealizationTarget { AnchorName = "body", KernelType = SbKernelType.AffordancePose,
                Position = new Vector3(1, 2, 3), Rotation = Quaternion.identity, Weight = 1, HasHeight = true } };
            actor.DeliverRealization(actor.Registration, 0, legacy, 1);
            Assert.That(targets.TryGetTarget("body", out _), Is.True);
            Assert.That(actor.TryBeginReceiverInput(1, 2, out var writer, out string error), Is.True, error);
            Assert.That(targets.TryGetTarget("body", out _), Is.False);
            Assert.That(targets.TryAcquireLease("body", out var independent), Is.True);
            try
            {
                Assert.That(independent.TryApply(new Vector3(4, 5, 6), 0, true, 1, 2), Is.True);
                actor.DeliverRealization(actor.Registration, 0, legacy, 2);
                Assert.That(writer.TryClose(out error), Is.True, error);
                actor.DeliverRealization(actor.Registration, 0, legacy, 3);
                actor.Unregister();
                Assert.That(targets.TryGetTarget("body", out var preserved), Is.True);
                Assert.That(preserved.Position, Is.EqualTo(new Vector3(4, 5, 6)));
            }
            finally { independent.Dispose(); }
        }

        [Test]
        public void RuntimeRichQueryJoinsExactRegistrationAndWithdrawsAfterClearOrDetach()
        {
            SpatialBlendObject sourceSupport = Support("source", "chair");
            SpatialBlendObject receiverSupport = Support("receiver", "sofa");
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, receiverSupport, out var correspondence,
                out string error), Is.True, error);
            try
            {
                SpatialBlendActor source = Actor("source", true, "person", sourceSupport);
                SpatialBlendActor receiver = Actor("receiver", false, "person", receiverSupport);
                var delivered = new List<RichRealizationMailboxEnvelope>();
                receiver.RichRealizationAvailable += delivered.Add;
                Assert.That(receiver.TryBeginReceiverInput(1, 2, out var writer, out error), Is.True, error);
                Assert.That(receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
                { MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1 }, out error), Is.True, error);
                Assert.That(source.TryCreateMotionInput(out var sourceInput), Is.True);
                Assert.That(writer.TrySubmit(Frame(writer, 1), out error), Is.True, error);
                Assert.That(sourceInput.TryApplyWorldFrame(new AnchorPoseFrame { sequence = 0, timeSeconds = 20,
                    anchors = new[] { new AnchorPoseSample { anchorId = "root", worldPosition = new Vector3(.2f, 0, .1f),
                        worldRotation = Quaternion.identity } } }), Is.True);
                _world.DrainRealizations(0);
                var envelope = delivered.FindLast(value => value.Snapshot != null && value.Reason == RichMailboxReason.NativeSnapshot);
                Assert.That(envelope, Is.Not.Null);
                Assert.That(writer.TryGetValidity(envelope.Snapshot, out var validity, out int result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(1)); Assert.That(validity.Valid, Is.True);
                Assert.That(writer.TryClear(out error), Is.True, error);
                Assert.That(writer.TryGetValidity(envelope.Snapshot, out validity, out result, out error), Is.True, error);
                Assert.That(result, Is.EqualTo(-5)); Assert.That(validity.Valid, Is.False);
                receiver.Unregister();
                Assert.That(writer.TryGetValidity(envelope.Snapshot, out _, out _, out error), Is.False);
            }
            finally { correspondence.Dispose(); }
        }

        private ReceiverInputFrame Frame(ReceiverInputWriter writer, ulong sequence)
        {
            Assert.That(writer.TryReadAcquisitionClock(out long now, out string error), Is.True, error);
            Assert.That(ReceiverInputFrame.TryCreate(writer.Input, sequence, now, now + 1000000000L,
                new[] { new ReceiverAnchorPose("root", true, Matrix4x4.identity) }, out var frame, out error), Is.True, error);
            return frame;
        }
        private static ReceiverInputFrame FrameFor(ReceiverInputEpoch input, ulong sequence)
        {
            Assert.That(ReceiverInputFrame.TryCreate(input, sequence, 1, 2,
                new[] { new ReceiverAnchorPose("root", true, Matrix4x4.identity) }, out var frame, out _), Is.True); return frame;
        }
        private SpatialBlendActor Actor(string id = "receiver", bool source = false, string actorId = null,
            SpatialBlendObject support = null, AnchorTargetController targets = null)
        {
            // ABI 4: scope belongs to the relation family. A support is
            // a descendant of the actor's own element (its owner object), not an app-declared scope.
            SpatialBlendObject owner;
            if (support != null) owner = _supportOwner[support];
            else
            {
                owner = Create<SpatialBlendObject>(id + "-owner"); owner.Configure(_world, id, id + "-owner");
                Assert.That(owner.TryRegister(out string ownerError), Is.True, ownerError);
            }
            SpatialBlendActor actor = Create<SpatialBlendActor>(id);
            var anchors = new List<SpatialBlendActorAnchor>
                { new SpatialBlendActorAnchor("root", SpatialBlendActorAnchorRole.Root, Vector3.zero) };
            if (targets != null) anchors.Add(new SpatialBlendActorAnchor("body", SpatialBlendActorAnchorRole.Body, Vector3.zero));
            actor.Configure(owner, actorId ?? id, source, anchors.ToArray(), targets);
            Assert.That(actor.TryRegister(out string error), Is.True, error); return actor;
        }
        private readonly Dictionary<SpatialBlendObject, SpatialBlendObject> _supportOwner =
            new Dictionary<SpatialBlendObject, SpatialBlendObject>();
        private SpatialBlendObject Support(string space, string id)
        {
            var owner = Create<SpatialBlendObject>(id + "-support-owner"); owner.Configure(_world, space, id + "-support-owner");
            var support = Create<SpatialBlendObject>(id); support.Configure(_world, space, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(support.TrySetSupportPolygon(new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
                new Vector3(1, 0, 1), new Vector3(-1, 0, 1) }, out string error), Is.True, error);
            Assert.That(support.TryRegister(out error), Is.True, error);
            _supportOwner[support] = owner; return support;
        }
        private T Create<T>(string name) where T : Component
        {
            var go = new GameObject(name); _created.Add(go); var value = go.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, null); return value;
        }
    }
}
#endif
