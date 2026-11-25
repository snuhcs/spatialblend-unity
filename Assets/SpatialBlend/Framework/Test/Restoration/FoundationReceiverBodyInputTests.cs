// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    internal static class ReceiverBodyFixture
    {
        internal static Matrix4x4 Pose(Vector3 position, Vector3 euler = default) =>
            Matrix4x4.TRS(position, Quaternion.Euler(euler), Vector3.one);
        internal static ReceiverBodyJoint[] Definition() => new[]
        {
            new ReceiverBodyJoint("pelvis", -1, Pose(new Vector3(0, 1, 0))),
            new ReceiverBodyJoint("chest", 0, Pose(new Vector3(0, .4f, 0))),
            new ReceiverBodyJoint("head", 1, Pose(new Vector3(0, .25f, 0))),
            new ReceiverBodyJoint("left_shoulder", 1, Pose(new Vector3(-.2f, .1f, 0))),
            new ReceiverBodyJoint("left_elbow", 3, Pose(new Vector3(-.25f, 0, 0))),
            new ReceiverBodyJoint("left_wrist", 4, Pose(new Vector3(-.25f, 0, 0))),
            new ReceiverBodyJoint("right_shoulder", 1, Pose(new Vector3(.2f, .1f, 0))),
            new ReceiverBodyJoint("right_elbow", 6, Pose(new Vector3(.25f, 0, 0))),
            new ReceiverBodyJoint("right_wrist", 7, Pose(new Vector3(.25f, 0, 0))),
            new ReceiverBodyJoint("left_hip", 0, Pose(new Vector3(-.1f, 0, 0))),
            new ReceiverBodyJoint("left_knee", 9, Pose(new Vector3(0, -.4f, 0))),
            new ReceiverBodyJoint("left_ankle", 10, Pose(new Vector3(0, -.4f, 0))),
            new ReceiverBodyJoint("right_hip", 0, Pose(new Vector3(.1f, 0, 0))),
            new ReceiverBodyJoint("right_knee", 12, Pose(new Vector3(0, -.4f, 0))),
            new ReceiverBodyJoint("right_ankle", 13, Pose(new Vector3(0, -.4f, 0)))
        };
        internal static ReceiverBodySkeleton Skeleton()
        {
            Assert.That(ReceiverBodySkeleton.TryCreate("synthetic-15-joint-biped", 7, Definition(), out var value,
                out string error), Is.True, error); return value;
        }
        internal static ReceiverInputEpoch Input() => new ReceiverInputEpoch(1, 2, "locomotion",
            new[] { "head_facing", "seat_contact", "locomotion" }, 1, 17, 19, 23, 29, 1000000000);
        internal static ReceiverBodyAnchorBinding[] Anchors() => new[]
        {
            new ReceiverBodyAnchorBinding("seat_contact", "pelvis", 0, Pose(new Vector3(.1f, -.15f, .2f), new Vector3(13, 0, 7))),
            new ReceiverBodyAnchorBinding("head_facing", "head", 2, Pose(new Vector3(0, .03f, .08f), new Vector3(0, 17, 0)))
        };
        internal static ReceiverBodyClockMapping Clock(ReceiverInputEpoch input, long nativeOrigin = 100,
            double sourceOrigin = 0, ReceiverBodyClockMappingKind kind = ReceiverBodyClockMappingKind.SyntheticFixture)
        {
            Assert.That(ReceiverBodyClockMapping.TryCreate("fixture-source-seconds", 3, 5, kind, "synthetic-clock-calibration",
                input.ClockId, input.ClockEpoch, sourceOrigin, nativeOrigin, 1000000000, 0,
                Math.Max(1, nativeOrigin - 30000000000), nativeOrigin + 30000000000, out var mapping, out string error), Is.True, error);
            return mapping;
        }
        internal static ReceiverBodyProfile Profile(ReceiverInputEpoch input = null, long nativeOrigin = 100, double sourceOrigin = 0)
        {
            input = input ?? Input();
            Assert.That(ReceiverBodyProfile.TryCreate(input, "fixture-reader", 11, Clock(input, nativeOrigin, sourceOrigin),
                ReceiverBodyEvidenceKind.SyntheticFixture, "synthetic-15-joint-pose-v1", Skeleton(),
                Pose(new Vector3(4, 0, -2), new Vector3(0, 90, 0)), Anchors(), out var value, out string error), Is.True, error);
            return value;
        }
        internal static ReceiverBodyJointPose[] Joints()
        {
            var definition = Definition();
            var values = definition.Select((joint, index) => new ReceiverBodyJointPose(joint.Name, index, true,
                joint.ParentFromReferenceJoint)).ToArray();
            // Synthetic input is NOT its reference pose: retain pelvis,
            // chest and head rotation independently, with an articulated arm.
            values[0] = new ReceiverBodyJointPose("pelvis", 0, true, Pose(new Vector3(0, .7f, 0), new Vector3(0, 90, 0)));
            values[1] = new ReceiverBodyJointPose("chest", 1, true, Pose(new Vector3(0, .4f, 0), new Vector3(17, 0, -11)));
            values[2] = new ReceiverBodyJointPose("head", 2, true, Pose(new Vector3(0, .25f, 0), new Vector3(-13, 29, 7)));
            values[4] = new ReceiverBodyJointPose("left_elbow", 4, true, Pose(new Vector3(-.25f, 0, 0), new Vector3(0, 0, 35)));
            return values;
        }
        internal static ReceiverBodyFrame Frame(ReceiverBodyProfile profile, ulong sequence = 1, long sourceSequence = 0,
            double sourceTime = 0, long? observed = null, long? expiry = null,
            bool root = true, ReceiverBodyJointPose[] joints = null)
        {
            Assert.That(profile.ClockMapping.TryMap(sourceTime, out long lower, out _, out string clockError), Is.True, clockError);
            Assert.That(ReceiverBodyFrame.TryCreate(profile, sequence, sourceSequence, sourceTime, observed ?? lower,
                expiry ?? lower + 1000000000, root,
                Pose(new Vector3(2, 0, 1), new Vector3(0, -37, 0)), joints ?? Joints(), out var frame, out string error),
                Is.True, error); return frame;
        }
    }

    [Category("FoundationReceiverInput")]
    public sealed class FoundationReceiverBodyFrameTests
    {
        [Test]
        public void ExactSkeletonFreezesTopologyReferencePoseAndFingerprintsThem()
        {
            var definition = ReceiverBodyFixture.Definition();
            Assert.That(ReceiverBodySkeleton.TryCreate("fixture", 7, definition, out var first, out string error), Is.True, error);
            Assert.That(ReceiverBodySkeleton.TryCreate("fixture", 7, definition, out var same, out error), Is.True, error);
            Assert.That(first.Fingerprint, Is.EqualTo(same.Fingerprint)); Assert.That(first.Fingerprint.Length, Is.EqualTo(64));
            definition[2] = new ReceiverBodyJoint("head", 1, ReceiverBodyFixture.Pose(new Vector3(0, .3f, 0)));
            Assert.That(ReceiverBodySkeleton.TryCreate("fixture", 7, definition, out var changed, out error), Is.True, error);
            Assert.That(first.Joints[2].ParentFromReferenceJoint.m13, Is.EqualTo(.25f));
            Assert.That(changed.Fingerprint, Is.Not.EqualTo(first.Fingerprint));
            definition[2] = new ReceiverBodyJoint("head", 2, Matrix4x4.identity);
            Assert.That(ReceiverBodySkeleton.TryCreate("fixture", 7, definition, out _, out error), Is.False);
            definition[2] = new ReceiverBodyJoint("head", -1, Matrix4x4.identity);
            Assert.That(ReceiverBodySkeleton.TryCreate("fixture", 7, definition, out _, out error), Is.False);
            definition[2] = new ReceiverBodyJoint("pelvis", 1, Matrix4x4.identity);
            Assert.That(ReceiverBodySkeleton.TryCreate("fixture", 7, definition, out _, out error), Is.False);
        }

        [Test]
        public void ProfileRequiresActualJointNameIndexAndExplicitIndependentProvenance()
        {
            var input = ReceiverBodyFixture.Input(); var skeleton = ReceiverBodyFixture.Skeleton();
            var anchors = ReceiverBodyFixture.Anchors();
            bool Create(ReceiverBodyEvidenceKind evidence) => ReceiverBodyProfile.TryCreate(input, "reader", 1,
                ReceiverBodyFixture.Clock(input, kind: evidence == ReceiverBodyEvidenceKind.RecordedBody
                    ? ReceiverBodyClockMappingKind.ReplayTimeline : evidence == ReceiverBodyEvidenceKind.LiveBodyTracking
                    ? ReceiverBodyClockMappingKind.CaptureClock : ReceiverBodyClockMappingKind.SyntheticFixture),
                evidence, "receipt", skeleton, Matrix4x4.identity, anchors, out _, out _);
            Assert.That(Create(ReceiverBodyEvidenceKind.RecordedBody), Is.True);
            Assert.That(Create(ReceiverBodyEvidenceKind.LiveBodyTracking), Is.True);
            Assert.That(Create(ReceiverBodyEvidenceKind.GeneratedOutput), Is.False);
            Assert.That(Create((ReceiverBodyEvidenceKind)99), Is.False);
            anchors[1] = new ReceiverBodyAnchorBinding("head_facing", "head", 1, Matrix4x4.identity);
            Assert.That(Create(ReceiverBodyEvidenceKind.SyntheticFixture), Is.False, "Cue order is not joint order.");
            anchors[1] = new ReceiverBodyAnchorBinding("locomotion", "head", 2, Matrix4x4.identity);
            Assert.That(Create(ReceiverBodyEvidenceKind.SyntheticFixture), Is.False, "Locomotion is never a joint alias.");
        }

        [Test]
        public void FullHierarchyAndRotatingContactOffsetReachReceiverWorldWithoutYawOnlyLoss()
        {
            var profile = ReceiverBodyFixture.Profile(); var rows = ReceiverBodyFixture.Joints();
            var frame = ReceiverBodyFixture.Frame(profile, joints: rows.Reverse().ToArray());
            Assert.That(frame.Joints.Count, Is.EqualTo(15)); Assert.That(frame.HasAllJointPoses, Is.True);
            Assert.That(frame.SourceSequence, Is.Zero); Assert.That(frame.SourceTimeSeconds, Is.Zero);
            var contact = frame.GeometricInput.Anchors.Single(row => row.Name == "seat_contact");
            // Independent geometric oracle: two 90-degree yaws make 180; rotate
            // the hip-local (0.1,-0.15,0.2) offset rather than add it in world.
            Assert.That(Vector3.Distance(contact.WorldPose.GetColumn(3), new Vector3(3.9f, .55f, -2.2f)), Is.LessThan(1e-5));
            var expectedRotation = Quaternion.Euler(0, 180, 0) * Quaternion.Euler(13, 0, 7);
            Assert.That(Vector3.Distance(contact.WorldPose.GetColumn(2), expectedRotation * Vector3.forward), Is.LessThan(1e-5));
            var head = frame.GeometricInput.Anchors.Single(row => row.Name == "head_facing");
            Assert.That(Mathf.Abs(head.WorldPose.m12), Is.GreaterThan(.01f), "Retain independent pitch/roll, not yaw only.");
            var root = frame.GeometricInput.Anchors.Single(row => row.Name == "locomotion");
            Assert.That(Vector3.Distance(root.WorldPose.GetColumn(3), new Vector3(5, 0, -4)), Is.LessThan(1e-5));
            rows[0] = new ReceiverBodyJointPose("pelvis", 0, false, Matrix4x4.identity);
            Assert.That(frame.Joints[0].Available, Is.True); Assert.That(frame.Joints[0].ParentFromJoint.m13, Is.EqualTo(.7f));
            Assert.That(frame.WorldJoints[0].WorldPose.m13, Is.EqualTo(.7f));
            Assert.That(frame.Profile.EvidenceKind, Is.EqualTo(ReceiverBodyEvidenceKind.SyntheticFixture));
        }

        [Test]
        public void ParentLossPropagatesWithoutReferencePoseFillingAndIndependentRootRemainsExplicit()
        {
            var rows = ReceiverBodyFixture.Joints();
            rows[1] = new ReceiverBodyJointPose("chest", 1, false, Matrix4x4.identity);
            var frame = ReceiverBodyFixture.Frame(ReceiverBodyFixture.Profile(), joints: rows);
            Assert.That(frame.HasAllJointPoses, Is.False);
            Assert.That(frame.Joints[2].Available, Is.True, "The child local sample is retained, but is not a world observation.");
            Assert.That(frame.WorldJoints[2].Available, Is.False);
            Assert.That(frame.WorldJoints[2].WorldPose, Is.EqualTo(Matrix4x4.identity));
            Assert.That(frame.GeometricInput.Anchors.Single(row => row.Name == "head_facing").Available, Is.False);
            Assert.That(frame.GeometricInput.Anchors.Single(row => row.Name == "seat_contact").Available, Is.True);
            Assert.That(frame.GeometricInput.Anchors.Single(row => row.Name == "locomotion").Available, Is.True);
            frame = ReceiverBodyFixture.Frame(frame.Profile, root: false);
            Assert.That(frame.HasAllJointPoses, Is.True);
            Assert.That(frame.GeometricInput.Anchors.Single(row => row.Name == "locomotion").Available, Is.False);
        }

        [Test]
        public void SharedJointReadMappingsAndProfileFingerprintsDoNotClaimMultipleActuatorWriters()
        {
            var input = new ReceiverInputEpoch(1, 2, "locomotion", new[] { "locomotion", "pelvis", "seat_contact" },
                1, 17, 19, 23, 29, 1000000000);
            var skeleton = ReceiverBodyFixture.Skeleton(); var clock = ReceiverBodyFixture.Clock(input);
            var anchors = new[] { new ReceiverBodyAnchorBinding("pelvis", "pelvis", 0, Matrix4x4.identity),
                ReceiverBodyFixture.Anchors()[0] };
            bool Create(out ReceiverBodyProfile profile) => ReceiverBodyProfile.TryCreate(input, "reader", 1, clock,
                ReceiverBodyEvidenceKind.SyntheticFixture, "receipt", skeleton, Matrix4x4.identity, anchors, out profile, out _);
            Assert.That(Create(out var first), Is.True); Assert.That(Create(out var same), Is.True);
            Assert.That(first.Fingerprint, Is.EqualTo(same.Fingerprint));
            anchors[1] = new ReceiverBodyAnchorBinding("seat_contact", "pelvis", 0,
                ReceiverBodyFixture.Pose(new Vector3(0, -.2f, 0)));
            Assert.That(Create(out var changed), Is.True); Assert.That(changed.Fingerprint, Is.Not.EqualTo(first.Fingerprint));
            var frame = ReceiverBodyFixture.Frame(first);
            Vector3 pelvis = frame.GeometricInput.Anchors.Single(row => row.Name == "pelvis").WorldPose.GetColumn(3);
            Vector3 contact = frame.GeometricInput.Anchors.Single(row => row.Name == "seat_contact").WorldPose.GetColumn(3);
            Assert.That(Vector3.Distance(pelvis, contact), Is.EqualTo(new Vector3(.1f, -.15f, .2f).magnitude).Within(1e-5));
            Assert.That(first.Anchors[1].JointFromAnchor.m13, Is.EqualTo(-.15f));
        }

        [Test]
        public void DeepDeclaredSkeletonRetainsFiniteRigidFkInsteadOfAccumulatingMatrixScaleDrift()
        {
            var definition = new ReceiverBodyJoint[128]; var rows = new ReceiverBodyJointPose[128];
            for (int i = 0; i < definition.Length; ++i)
            {
                var pose = ReceiverBodyFixture.Pose(new Vector3(.01f, .01f, .01f), new Vector3(1, 2, 3));
                definition[i] = new ReceiverBodyJoint("joint_" + i, i - 1, pose);
                rows[i] = new ReceiverBodyJointPose("joint_" + i, i, true, pose);
            }
            Assert.That(ReceiverBodySkeleton.TryCreate("deep-fixture", 1, definition, out var skeleton, out string error), Is.True, error);
            var input = ReceiverBodyFixture.Input();
            Assert.That(ReceiverBodyProfile.TryCreate(input, "reader", 1, ReceiverBodyFixture.Clock(input), ReceiverBodyEvidenceKind.SyntheticFixture,
                "deep-fixture", skeleton, Matrix4x4.identity, new[] {
                    new ReceiverBodyAnchorBinding("head_facing", "joint_127", 127, Matrix4x4.identity),
                    new ReceiverBodyAnchorBinding("seat_contact", "joint_0", 0, Matrix4x4.identity) }, out var profile, out error), Is.True, error);
            Assert.That(ReceiverBodyFrame.TryCreate(profile, 1, 0, 0, 100, 1000000100, true,
                Matrix4x4.identity, rows, out var frame, out error), Is.True, error);
            Assert.That(frame.HasAllJointPoses, Is.True); Assert.That(frame.WorldJoints.Count, Is.EqualTo(128));
        }

        [TestCase("missing")]
        [TestCase("duplicate")]
        [TestCase("wrong-name")]
        [TestCase("nan")]
        [TestCase("scale")]
        [TestCase("reflection")]
        [TestCase("unavailable-shear")]
        public void MalformedFullBodyRejectsAtomically(string kind)
        {
            var rows = ReceiverBodyFixture.Joints(); var pose = Matrix4x4.identity; bool available = true;
            switch (kind)
            {
                case "missing": rows = rows.Take(rows.Length - 1).ToArray(); break;
                case "duplicate": rows[1] = rows[2]; break;
                case "wrong-name": rows[1] = new ReceiverBodyJointPose("head", 1, true, pose); break;
                case "nan": pose.m03 = float.NaN; break;
                case "scale": pose.m00 = 2; break;
                case "reflection": pose.m00 = -1; break;
                case "unavailable-shear": pose.m01 = .2f; available = false; break;
            }
            if (kind == "nan" || kind == "scale" || kind == "reflection" || kind == "unavailable-shear")
                rows[1] = new ReceiverBodyJointPose("chest", 1, available, pose);
            Assert.That(ReceiverBodyFrame.TryCreate(ReceiverBodyFixture.Profile(), 1, 0, 0, 100, 10000,
                true, Matrix4x4.identity, rows, out var frame, out _), Is.False);
            Assert.That(frame, Is.Null);
        }

        [Test]
        public void CapsRejectBeforeReadingRowsAndUnmappedTimesAreNotInvented()
        {
            Assert.That(ReceiverBodySkeleton.TryCreate("too-large", 1, new CountOnly<ReceiverBodyJoint>(), out _, out _), Is.False);
            var profile = ReceiverBodyFixture.Profile();
            Assert.That(ReceiverBodyFrame.TryCreate(profile, 1, 0, 0, 1, 2, true, Matrix4x4.identity,
                new CountOnly<ReceiverBodyJointPose>(), out _, out _), Is.False);
            foreach (double time in new[] { -1, double.NaN, double.PositiveInfinity })
                Assert.That(ReceiverBodyFrame.TryCreate(profile, 1, 0, time, 1, 2, true, Matrix4x4.identity,
                    ReceiverBodyFixture.Joints(), out _, out _), Is.False);
            Assert.That(ReceiverBodyFrame.TryCreate(profile, 1, 0, 0, 0, 2, true, Matrix4x4.identity,
                ReceiverBodyFixture.Joints(), out _, out _), Is.False);
        }
        private sealed class CountOnly<T> : IReadOnlyList<T>
        {
            public int Count => int.MaxValue;
            public T this[int index] => throw new InvalidOperationException("must not index oversized input");
            public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("must not enumerate oversized input");
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    [Category("FoundationReceiverInput")]
    public sealed class FoundationReceiverBodyClockTests
    {
        [Test]
        public void IntegerRoundedDoubleProductCannotNarrowAcquisitionBounds()
        {
            Assert.That(ReceiverBodyClockMapping.TryCreate("source", 1, 1, ReceiverBodyClockMappingKind.CaptureClock,
                "clock-receipt", 2, 3, 0, 100, 1e9, 0, 1, 10000000000,
                out var mapping, out string error), Is.True, error);
            // Exact stored-double product = 7215934051.999999709892108513...;
            // ordinary double multiplication rounds UP to 7215934052 before Floor.
            Assert.That(mapping.TryMap(7.215934052, out long lower, out long upper, out error), Is.True, error);
            Assert.That(lower, Is.EqualTo(100L + 7215934051));
            Assert.That(upper, Is.EqualTo(100L + 7215934052));
            Assert.That(mapping.TryMap(BitConverter.Int64BitsToDouble(1), out lower, out upper, out error), Is.True, error);
            Assert.That(lower, Is.EqualTo(100)); Assert.That(upper, Is.EqualTo(101));
            Assert.That(ReceiverBodyClockMapping.TryCreate("source", 1, 1, ReceiverBodyClockMappingKind.CaptureClock,
                "clock-receipt", 2, 3, BitConverter.Int64BitsToDouble(1), 100, 1e9, 0, 1, 1000,
                out mapping, out error), Is.True, error);
            Assert.That(mapping.TryMap(0, out lower, out upper, out error), Is.True, error);
            Assert.That(lower, Is.EqualTo(99)); Assert.That(upper, Is.EqualTo(100));
        }

        [Test]
        public void MappingRetainsLargeIntegerEpochAndWidensFractionalAcquisitionConservatively()
        {
            long origin = (1L << 54) + 37;
            Assert.That(ReceiverBodyClockMapping.TryCreate("sensor", 2, 3, ReceiverBodyClockMappingKind.CaptureClock,
                "clock-calibration", 5, 7, 10, origin, 1000000000, 13, origin - 100, origin + 2000000000,
                out var mapping, out string error), Is.True, error);
            Assert.That(mapping.TryMap(11.25, out long lower, out long upper, out error), Is.True, error);
            Assert.That(lower, Is.EqualTo(origin + 1250000000 - 13));
            Assert.That(upper, Is.EqualTo(origin + 1250000000 + 13));
            Assert.That(mapping.TryMap(12, out _, out _, out _), Is.False, "Exact calibration expiry is expired.");
            Assert.That(mapping.TryMap(double.MaxValue, out _, out _, out _), Is.False);
            Assert.That(mapping.TryMap(double.NaN, out _, out _, out _), Is.False);
            Assert.That(mapping.Fingerprint.Length, Is.EqualTo(64));
        }

        [Test]
        public void ReplayMappingHasExplicitRateAndNeverRestampsOrChangesClockIdentity()
        {
            var input = ReceiverBodyFixture.Input();
            Assert.That(ReceiverBodyClockMapping.TryCreate("recorded-seconds", 2, 3, ReceiverBodyClockMappingKind.ReplayTimeline,
                "explicit-double-speed-replay", input.ClockId, input.ClockEpoch, 0, 100, 500000000, 0, 1, 2000000000,
                out var mapping, out string error), Is.True, error);
            Assert.That(mapping.TryMap(1, out long lower, out long upper, out error), Is.True, error);
            Assert.That(lower, Is.EqualTo(500000100)); Assert.That(upper, Is.EqualTo(lower));
            Assert.That(ReceiverBodyProfile.TryCreate(input, "recorded-reader", 1, mapping, ReceiverBodyEvidenceKind.RecordedBody,
                "recording-receipt", ReceiverBodyFixture.Skeleton(), Matrix4x4.identity, ReceiverBodyFixture.Anchors(),
                out var profile, out error), Is.True, error);
            Assert.That(ReceiverBodyFrame.TryCreate(profile, 1, 1, 1, lower + 1, lower + 1000, true, Matrix4x4.identity,
                ReceiverBodyFixture.Joints(), out _, out _), Is.False, "No arbitrary native-now restamp.");
            Assert.That(ReceiverBodyFrame.TryCreate(profile, 1, 1, 1, lower, lower + 1000, true, Matrix4x4.identity,
                ReceiverBodyFixture.Joints(), out var body, out error), Is.True, error);
            Assert.That(body.SourceTimeSeconds, Is.EqualTo(1)); Assert.That(body.AcquisitionUpperNanoseconds, Is.EqualTo(upper));
            var wrong = new ReceiverInputEpoch(1, 2, "locomotion", input.AnchorNames.ToArray(), 1, 17, 19, 23, 30, 1000000000);
            Assert.That(ReceiverBodyProfile.TryCreate(wrong, "reader", 1, mapping, ReceiverBodyEvidenceKind.RecordedBody,
                "recording", profile.Skeleton, Matrix4x4.identity, profile.Anchors, out _, out _), Is.False);
            Assert.That(ReceiverBodyProfile.TryCreate(input, "reader", 1, mapping, ReceiverBodyEvidenceKind.LiveBodyTracking,
                "recording", profile.Skeleton, Matrix4x4.identity, profile.Anchors, out _, out _), Is.False);
        }

        [Test]
        public void InvalidRatesEpochsBoundsAndIntegerOverflowReject()
        {
            foreach (double rate in new[] { 0, -1, double.NaN, double.PositiveInfinity })
                Assert.That(ReceiverBodyClockMapping.TryCreate("source", 1, 1, ReceiverBodyClockMappingKind.CaptureClock,
                    "calibration", 1, 1, 0, 100, rate, 0, 1, 200, out _, out _), Is.False);
            Assert.That(ReceiverBodyClockMapping.TryCreate("source", 0, 1, ReceiverBodyClockMappingKind.CaptureClock,
                "calibration", 1, 1, 0, 100, 1e9, 0, 1, 200, out _, out _), Is.False);
            Assert.That(ReceiverBodyClockMapping.TryCreate("source", 1, 1, ReceiverBodyClockMappingKind.CaptureClock,
                "calibration", 1, 1, 0, 100, 1e9, 1000000000, 1, 200, out _, out _), Is.False);
            Assert.That(ReceiverBodyClockMapping.TryCreate("source", 1, 1, ReceiverBodyClockMappingKind.CaptureClock,
                "calibration", 1, 1, 0, long.MaxValue - 10, 1e9, 0, 1, long.MaxValue, out var mapping, out string error), Is.True, error);
            Assert.That(mapping.TryMap(1, out long lower, out long upper, out _), Is.False);
            Assert.That(lower, Is.Zero); Assert.That(upper, Is.Zero);
        }
    }

    [Category("FoundationReceiverInput")]
    public sealed partial class FoundationReceiverBodyNativeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<RichRealizationMailboxEnvelope> _delivered = new List<RichRealizationMailboxEnvelope>();
        private SpatialBlendLocalWorld _world;
        private SpatialBlendActor _source, _receiver;
        private ReceiverInputWriter _writer;
        private ReceiverBodyInputBinding _binding;
        private IAnchorPoseFrameSink _sourceInput;
        private IDisposable _correspondence;
        private long _sourceSequence;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("body-input-world"); _sourceSequence = 0;
            var sourceSupport = Support("source", "chair"); var receiverSupport = Support("receiver", "sofa");
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, receiverSupport, out var link, out string error), Is.True, error);
            _correspondence = link;
            _source = Actor("source", true, sourceSupport); _receiver = Actor("receiver", false, receiverSupport);
            Assert.That(_source.TryCreateMotionInput(out _sourceInput), Is.True);
            // Independent facing family on the source; runtime input remains the
            // declared full atomic source frame, not the generated receiver pose.
            // Facing is global: no scope is named.
            SpatialBlendAPI.Bind(_source.Registration.NativeSpace, _source.Registration.NativeActor,
                "head_facing", SbKernelType.Facing);
            Begin();
            _receiver.RichRealizationAvailable += _delivered.Add;
            Assert.That(_receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions
                { MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1 }, out error), Is.True, error);
        }
        [TearDown]
        public void TearDown()
        {
            _world.TestOnlyRejectReceiverClear = null;
            _binding?.TryClose(out _); _correspondence?.Dispose(); _world.Shutdown();
            for (int i = _created.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear(); _delivered.Clear();
        }
        private void Begin(double sourceOrigin = 0)
        {
            Assert.That(_receiver.TryBeginReceiverInput(17, 19, out _writer, out string error), Is.True, error);
            Assert.That(_writer.TryReadAcquisitionClock(out long now, out error), Is.True, error);
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, ReceiverBodyFixture.Profile(_writer.Input, now, sourceOrigin), out _binding, out error), Is.True, error);
        }
        private ReceiverBodyFrame Frame(ulong sequence = 1, long sourceSequence = 0, double sourceTime = double.NaN,
            bool root = true, ReceiverBodyJointPose[] joints = null)
        {
            if (double.IsNaN(sourceTime)) sourceTime = _binding.Profile.ClockMapping.SourceOriginSeconds;
            return ReceiverBodyFixture.Frame(_binding.Profile, sequence, sourceSequence, sourceTime, root: root, joints: joints);
        }
        private RichRealizationSnapshot Solve()
        {
            int count = _delivered.Count;
            Assert.That(_sourceInput.TryApplyWorldFrame(new AnchorPoseFrame { sequence = _sourceSequence++, timeSeconds = 20 + _sourceSequence,
                anchors = new[] {
                    new AnchorPoseSample { anchorId = "locomotion", worldPosition = new Vector3(.2f + .1f * _sourceSequence, 0, .1f) },
                    // ABI 4: engagement is the exact geometric fact that the anchor touches the support (in its footprint, on or
                    // below its plane; ABI 4 section 4). There is no 0.20 m enter radius, so a
                    // sample 5 cm above the plane is not engaged and no seat contributor exists. This one is on the plane.
                    new AnchorPoseSample { anchorId = "seat_contact", worldPosition = new Vector3(.1f, 0f, .2f) },
                    new AnchorPoseSample { anchorId = "head_facing", worldPosition = new Vector3(.3f, 1.6f, .1f), worldRotation = Quaternion.Euler(0, 17, 0) }
                } }), Is.True);
            _world.DrainRealizations(0);
            Assert.That(_delivered.Count, Is.GreaterThan(count));
            // A failed/empty native solve is an authoritative NativeRelease.
            // Selecting only NativeSnapshot across all history would reuse an
            // earlier successful result and conceal the new withdrawal.
            var envelope = _delivered.Skip(count).Last(value => value.Snapshot != null &&
                (value.Reason == RichMailboxReason.NativeSnapshot || value.Reason == RichMailboxReason.NativeRelease));
            Assert.That(envelope.Snapshot.Header.SourceObservationSequence.Known, Is.True);
            Assert.That(envelope.Snapshot.Header.SourceObservationSequence.Value, Is.EqualTo(_sourceSequence - 1));
            return envelope.Snapshot;
        }

        [Test]
        public void NativeSolveConsumesFullBodyFkAndReturnsExactBodyJoinWithoutDrivingActor()
        {
            ReceiverBodyFrame frame = Frame(); Vector3 actorBefore = _receiver.transform.position;
            int count = _delivered.Count;
            Assert.That(_binding.TrySubmit(frame, out string error), Is.True, error);
            Assert.That(_delivered.Count, Is.EqualTo(count), "Body input itself is not a source solve.");
            RichRealizationSnapshot snapshot = Solve();
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(frame)); Assert.That(body.Joints.Count, Is.EqualTo(15));
            var facing = snapshot.Channels.Single(channel => channel.Anchor == "head_facing" && channel.Family == RichRelationFamily.Facing);
            var worldHead = frame.GeometricInput.Anchors.Single(row => row.Name == "head_facing").WorldPose.GetColumn(3);
            Assert.That(facing.HasGoal, Is.True);
            Assert.That(facing.AdmittedPosition.X, Is.EqualTo(worldHead.x).Within(1e-5));
            Assert.That(facing.AdmittedPosition.Y, Is.EqualTo(worldHead.z).Within(1e-5));
            Assert.That(facing.AdmittedPosition.Z, Is.EqualTo(worldHead.y).Within(1e-5));
            Assert.That(_receiver.transform.position, Is.EqualTo(actorBefore));
            ulong revision = snapshot.Header.Revision;
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out body, out error), Is.True, error);
            Assert.That(snapshot.Header.Revision, Is.EqualTo(revision)); Assert.That(body.SourceTimeSeconds, Is.Zero);
        }

        [Test]
        public void OneProfileOwnsFreshEpochAndCannotRelabelPreviouslyAcceptedUnprofiledInput()
        {
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, _binding.Profile, out _, out _), Is.False);
            var frame = Frame();
            Assert.That(_writer.TrySubmit(frame.GeometricInput, out _), Is.False);
            Assert.That(_writer.TryClear(out _), Is.False);
            Assert.That(_binding.TrySubmit(frame, out string error), Is.True, error);
            var oldBinding = _binding;
            Assert.That(_receiver.TryBeginReceiverInput(17, 19, out _writer, out error), Is.True, error);
            var next = ReceiverBodyFixture.Profile(_writer.Input, frame.GeometricInput.ObservedAtNanoseconds);
            Assert.That(_writer.TrySubmit(ReceiverBodyFixture.Frame(next, observed: frame.GeometricInput.ObservedAtNanoseconds,
                expiry: frame.GeometricInput.ValidUntilNanoseconds).GeometricInput, out error), Is.True, error);
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, next, out _, out _), Is.False);
            Assert.That(_writer.TryClear(out error), Is.True, error);
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, next, out _, out _), Is.False);
            Assert.That(oldBinding.TrySubmit(frame, out _), Is.False);
            Assert.That(oldBinding.TryClose(out error), Is.True, error);
            Begin();
        }

        [Test]
        public void SourceOrderAndNativeOrderBothFenceWithoutReinterpretingTimeOrConsumingFailedSequence()
        {
            Begin(10);
            var frame = Frame(sourceTime: 10);
            Assert.That(_binding.TrySubmit(frame, out string error), Is.True, error);
            var first = Solve();
            Assert.That(_binding.TrySubmit(Frame(2, 0, 11), out _), Is.False);
            Assert.That(_binding.TrySubmit(Frame(2, 1, 9), out _), Is.False);
            Assert.That(_binding.TrySubmit(Frame(1, 1, 10), out _), Is.False, "Native input order independently rejects.");
            Assert.That(_binding.TryGetBodyForRealization(first, out var retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(frame));
            var newer = Frame(2, 1, 10);
            Assert.That(_binding.TrySubmit(newer, out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(first, out _, out _), Is.False, "Never nearest-time join an old solve to a new body.");
            Assert.That(_binding.TryGetBodyForRealization(Solve(), out retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(newer)); Assert.That(retained.SourceTimeSeconds, Is.EqualTo(10));
        }

        [Test]
        public void ExpiredNativeSubmissionDoesNotReplaceBodyAndClearRequiresStrictlyNewerRecovery()
        {
            var frame = Frame(); Assert.That(_binding.TrySubmit(frame, out string error), Is.True, error);
            var snapshot = Solve();
            var expired = ReceiverBodyFixture.Frame(_binding.Profile, 2, 1, 0,
                frame.GeometricInput.ObservedAtNanoseconds, frame.GeometricInput.ObservedAtNanoseconds + 1);
            Assert.That(_binding.TrySubmit(expired, out _), Is.False);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(frame));
            Assert.That(_binding.TryClear(out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out _), Is.False);
            Assert.That(_binding.TrySubmit(Frame(2, 0), out _), Is.False);
            var recovered = Frame(2, 1, 0);
            Assert.That(_binding.TrySubmit(recovered, out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(Solve(), out retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(recovered));
        }

        [Test]
        public void ExplicitLossRemainsLossNotCompleteBodyAndRootLossWithdrawsAllNativeGoals()
        {
            var rows = ReceiverBodyFixture.Joints(); rows[1] = new ReceiverBodyJointPose("chest", 1, false, Matrix4x4.identity);
            Assert.That(_binding.TrySubmit(Frame(joints: rows), out string error), Is.True, error);
            var snapshot = Solve();
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var partial, out error), Is.True, error);
            Assert.That(partial.HasAllJointPoses, Is.False);
            Assert.That(snapshot.Channels.Any(channel => channel.Anchor == "head_facing" && channel.HasGoal), Is.False);
            ulong previousRevision = snapshot.Header.Revision;
            Assert.That(_binding.TrySubmit(Frame(2, 1, 0, root: false), out error), Is.True, error);
            snapshot = Solve();
            Assert.That(snapshot.Header.Revision, Is.GreaterThan(previousRevision));
            Assert.That(snapshot.Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Failed));
            Assert.That(_delivered.Last().Reason, Is.EqualTo(RichMailboxReason.NativeRelease));
            Assert.That(snapshot.Channels.Any(channel => channel.HasGoal), Is.False);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out _), Is.False);
        }

        [Test]
        public void ForeignProfileAndThreadCannotMutateBodyAndFailedCloseRetainsOnlyCleanup()
        {
            var frame = Frame(); Assert.That(_binding.TrySubmit(frame, out string error), Is.True, error);
            var snapshot = Solve(); var sameNumbersDifferentProfile = ReceiverBodyFixture.Profile(_writer.Input,
                frame.GeometricInput.ObservedAtNanoseconds);
            Assert.That(_binding.TrySubmit(ReceiverBodyFixture.Frame(sameNumbersDifferentProfile, 2, 1, 0,
                frame.GeometricInput.ObservedAtNanoseconds, frame.GeometricInput.ValidUntilNanoseconds), out _), Is.False);
            bool submit = true, clear = true, close = true;
            var newer = Frame(2, 1, 0);
            var thread = new Thread(() => { submit = _binding.TrySubmit(newer, out _); clear = _binding.TryClear(out _); close = _binding.TryClose(out _); });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(submit || clear || close, Is.False);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out error), Is.True, error);
            int attempts = 0; _world.TestOnlyRejectReceiverClear = () => ++attempts == 1;
            Assert.That(_binding.TryClose(out _), Is.False);
            Assert.That(_binding.TrySubmit(newer, out _), Is.False);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out _), Is.False);
            Assert.That(_binding.TryClose(out error), Is.True, error); Assert.That(attempts, Is.EqualTo(2));
            _world.TestOnlyRejectReceiverClear = null;
        }

        [Test]
        public void FutureMappedAcquisitionIsRejectedWithoutClaimingOrConsumingBody()
        {
            var first = Frame(); Assert.That(_binding.TrySubmit(first, out string error), Is.True, error);
            var snapshot = Solve();
            Assert.That(_binding.TrySubmit(Frame(2, 1, 10), out _), Is.False);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(first));
            Assert.That(_binding.TrySubmit(Frame(2, 1, 0), out error), Is.True, error);
        }

        private ReceiverBodyRealizationCheckpoint Prepare(out RichRealizationSnapshot snapshot)
        {
            Assert.That(_binding.TrySubmit(Frame(), out string error), Is.True, error);
            snapshot = Solve();
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var checkpoint, out error), Is.True, error);
            return checkpoint;
        }

        [Test]
        public void PreparationPreservesFullNativeSnapshotBodyAndIndependentSequenceDomains()
        {
            var checkpoint = Prepare(out var snapshot);
            Assert.That(checkpoint.Snapshot, Is.SameAs(snapshot));
            Assert.That(checkpoint.Delivery, Is.SameAs(_delivered.Last()));
            Assert.That(checkpoint.Body.Joints.Count, Is.EqualTo(15));
            Assert.That(checkpoint.Profile, Is.SameAs(_binding.Profile));
            Assert.That(checkpoint.PreparedValidity.RequestedDomains, Is.EqualTo(ReceiverBodyRealizationCheckpoint.RequiredSceneDomains));
            Assert.That(checkpoint.PreparedValidity.Valid, Is.True);
            Assert.That(checkpoint.PreparedValidity.UncheckedDomains & RichRealizationValidityDomain.ReceiverInput,
                Is.EqualTo(RichRealizationValidityDomain.ReceiverInput), "The separate input sidecar is not validity-v1 All.");
            Assert.That(checkpoint.Snapshot.Channels.Any(c => c.Family == RichRelationFamily.Facing), Is.True);
            Assert.That(checkpoint.Snapshot.Channels.Any(c => c.Family == RichRelationFamily.AffordancePose), Is.True);
            Assert.That(checkpoint.Body.SourceSequence, Is.Zero);
            Assert.That(snapshot.Header.SourceObservationSequence.Value, Is.Zero);
            Assert.That(snapshot.Header.SourceSequence.Value, Is.GreaterThan(0));
            Assert.That(checkpoint.Body.SourceTimeSeconds, Is.Zero);
            Assert.That(snapshot.Header.SourceTimeSeconds.Value, Is.EqualTo(21));
            Assert.That(checkpoint.TryRevalidate(out var comparison, out string error), Is.True, error);
            Assert.That(comparison.Revision, Is.EqualTo(snapshot.Header.Revision));
            // Preparing does not consume the native poll cursor or restamp output.
            Assert.That(SpatialBlendAPI.TryPollRichRealization(snapshot.Header.ProducerLifetime,
                out var polled, out int result, out error), Is.True, error);
            Assert.That(result, Is.EqualTo(1));
            Assert.That(polled.ToDeterministicJson(), Is.EqualTo(snapshot.ToDeterministicJson()));
        }

        [Test]
        public void PreparationRejectsCopiedOrAlteredPayloadWithSameNativeHeader()
        {
            var checkpoint = Prepare(out var snapshot);
            var sameValues = new RichRealizationSnapshot(snapshot.Header, snapshot.Channels);
            var altered = new RichRealizationSnapshot(snapshot.Header, Array.Empty<RichRealizationChannel>());
            Assert.That(sameValues.ToDeterministicJson(), Is.EqualTo(snapshot.ToDeterministicJson()));
            foreach (var forged in new[] { sameValues, altered })
            {
                Assert.That(_binding.TryPrepareRealization(forged, 1000000000, out var rejected, out _), Is.False);
                Assert.That(rejected, Is.Null);
            }
            Assert.That(checkpoint.TryRevalidate(out _, out string error), Is.True, error);
        }

        [Test]
        public void PreparationChecksSceneTransformBeforeAnotherSourceSample()
        {
            var checkpoint = Prepare(out var snapshot);
            var support = _created.Single(go => go.name == "sofa").GetComponent<SpatialBlendObject>();
            support.transform.position = new Vector3(.2f, .1f, .3f);
            Assert.That(support.TryRefresh(out string error), Is.True, error);
            // The old diagnostic body-only join remains true: this test needs
            // the newly composed scene-domain check, not another input test.
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out error), Is.True, error);
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
            Assert.That(checkpoint.IsWithdrawn, Is.True);
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out _, out _), Is.False);
        }

        [Test]
        public void PreparationChecksPolicyBeforeAnotherSourceSample()
        {
            var checkpoint = Prepare(out var snapshot);
            Assert.That(SpatialBlendAPI.TrySetRichValidityPolicyForTest(_receiver.Registration.NativeSpace,
                1, out string error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out error), Is.True, error);
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
            Assert.That(checkpoint.IsWithdrawn, Is.True);
        }

        [Test]
        public void PreparationChecksCorrespondenceRemovalBeforeAnotherSourceSample()
        {
            var checkpoint = Prepare(out var snapshot);
            _correspondence.Dispose(); _correspondence = null;
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out string error), Is.True, error);
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
        }

        [Test]
        public void PreparationRejectsNewNativeRevisionBeforeMailboxDrain()
        {
            var checkpoint = Prepare(out _); int delivered = _delivered.Count;
            Assert.That(_sourceInput.TryApplyWorldFrame(new AnchorPoseFrame {
                sequence = _sourceSequence++, timeSeconds = 30,
                anchors = new[] { new AnchorPoseSample { anchorId = "locomotion", worldPosition = Vector3.zero } }
            }), Is.True);
            Assert.That(_delivered.Count, Is.EqualTo(delivered));
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
            Assert.That(checkpoint.IsWithdrawn, Is.True);
        }

        [Test]
        public void PreparationRejectsIncompleteBodyDespiteReducedNativeGoals()
        {
            var rows = ReceiverBodyFixture.Joints();
            rows[1] = new ReceiverBodyJointPose("chest", 1, false, Matrix4x4.identity);
            Assert.That(_binding.TrySubmit(Frame(joints: rows), out string error), Is.True, error);
            var snapshot = Solve();
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var partial, out error), Is.True, error);
            Assert.That(partial.HasAllJointPoses, Is.False);
            Assert.That(snapshot.Channels.Any(c => c.HasGoal), Is.True);
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out _, out _), Is.False);
        }

        [Test]
        public void PreparationExpiresFromOriginalNativeReceiptWithoutUnityTick()
        {
            Prepare(out var snapshot);
            Assert.That(_binding.TryPrepareRealization(snapshot, 100000000, out var checkpoint, out string error), Is.True, error);
            Assert.That(checkpoint.ValidUntilNativeNanoseconds,
                Is.EqualTo((long)snapshot.Header.NativeReceiptNanoseconds.Value + 100000000));
            // An actual native clock is read; no fake Update advances time.
            Thread.Sleep(130);
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
            Assert.That(checkpoint.IsWithdrawn, Is.True);
            Assert.That(_binding.TryPrepareRealization(snapshot, 100000000, out _, out _), Is.False,
                "A later first preparation cannot restamp receipt.");
        }

        [Test]
        public void PreparationRetirementCannotWithdrawReplacement()
        {
            var old = Prepare(out var first);
            _receiver.DisableRichRealization(); _world.DrainRealizations(0);
            Assert.That(_receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions {
                MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1
            }, out string error), Is.True, error);
            var snapshot = Solve();
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var replacement, out error), Is.True, error);
            Assert.That(snapshot.Header.ProducerLifetime, Is.Not.EqualTo(first.Header.ProducerLifetime));
            Assert.That(old.TryRevalidate(out _, out _), Is.False);
            Assert.That(old.TryWithdraw(out error), Is.True, error);
            Assert.That(replacement.TryRevalidate(out _, out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out _, out error), Is.True, error);
        }

        [Test]
        public void PreparationRejectsExpiredPinnedRichClockCalibrationBeforeReceiptAge()
        {
            _receiver.DisableRichRealization(); _world.DrainRealizations(0);
            double localNow = 100;
            _world.TestOnlyRichStopwatchClock = () => localNow;
            Assert.That(_receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions {
                MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1,
                CalibrationLifetimeSeconds = .1
            }, out string error), Is.True, error);
            Assert.That(_binding.TrySubmit(Frame(), out error), Is.True, error);
            localNow = 100.05;
            var snapshot = Solve();
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var checkpoint, out error), Is.True, error);
            Assert.That(checkpoint.Delivery.LocalReceiptInterval.IsFreshAt(100.1, 1), Is.True);
            localNow = 100.1;
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
            Assert.That(checkpoint.IsWithdrawn, Is.True);
            localNow = 100.06;
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False, "Withdrawn preparation never revives.");
        }

        [Test]
        public void PreparationWorkerThreadCannotReadOrPoisonOwnerState()
        {
            var checkpoint = Prepare(out var snapshot);
            bool prepare = true, revalidate = true, withdraw = true;
            var thread = new Thread(() => {
                prepare = _binding.TryPrepareRealization(snapshot, 1000000000, out _, out _);
                revalidate = checkpoint.TryRevalidate(out _, out _);
                withdraw = checkpoint.TryWithdraw(out _);
            });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(prepare || revalidate || withdraw, Is.False);
            Assert.That(checkpoint.IsWithdrawn, Is.False);
            Assert.That(checkpoint.TryRevalidate(out _, out string error), Is.True, error);
        }

        [Test]
        public void PreparationClearAndNewBodyCannotReviveOldCheckpoint()
        {
            var old = Prepare(out var first);
            Assert.That(_binding.TryClear(out string error), Is.True, error);
            Assert.That(old.TryRevalidate(out _, out _), Is.False);
            Assert.That(_binding.TrySubmit(Frame(2, 1, 0), out error), Is.True, error);
            var next = Solve();
            Assert.That(_binding.TryPrepareRealization(next, 1000000000, out var replacement, out error), Is.True, error);
            Assert.That(old.TryRevalidate(out _, out _), Is.False);
            Assert.That(old.Body.SourceSequence, Is.Zero);
            Assert.That(replacement.Body.SourceSequence, Is.EqualTo(1));
            Assert.That(replacement.TryRevalidate(out _, out error), Is.True, error);
        }

        [Test]
        public void PreparationPinsAgeBoundAndNeverExtendsBodyExpiry()
        {
            var first = Prepare(out var snapshot);
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var again, out string error), Is.True, error);
            Assert.That(again.ValidUntilNativeNanoseconds, Is.EqualTo(first.ValidUntilNativeNanoseconds));
            Assert.That(again.ValidUntilNativeNanoseconds, Is.EqualTo(first.Body.GeometricInput.ValidUntilNanoseconds));
            foreach (long invalid in new[] { 0L, -1L, 1000000001L, long.MaxValue })
                Assert.That(_binding.TryPrepareRealization(snapshot, invalid, out _, out _), Is.False);
        }

        [Test]
        public void PreparationRejectsSourceClearWithoutOriginalSampleAndReset()
        {
            var checkpoint = Prepare(out _);
            _sourceInput.ClearWorldAnchor("head_facing"); _world.DrainRealizations(0);
            var cleared = _delivered.Last(value => value.Snapshot != null).Snapshot;
            Assert.That(cleared.Header.SourceObservationSequence.Known, Is.False);
            Assert.That(_binding.TryPrepareRealization(cleared, 1000000000, out _, out _), Is.False);
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.False);
            Assert.That(_source.TryCreateMotionInput(out _sourceInput), Is.True);
            _world.DrainRealizations(0);
            var reset = _delivered.Last(value => value.Snapshot != null).Snapshot;
            Assert.That(reset.Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Empty));
            Assert.That(_binding.TryPrepareRealization(reset, 1000000000, out _, out _), Is.False);
        }

        [Test]
        public void PreparationExplicitWithdrawalLeavesBodyAndOtherPreparationIntact()
        {
            var first = Prepare(out var snapshot);
            Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var second, out string error), Is.True, error);
            Assert.That(first.TryWithdraw(out error), Is.True, error);
            Assert.That(first.TryWithdraw(out error), Is.True, error);
            Assert.That(first.TryRevalidate(out _, out _), Is.False);
            Assert.That(second.TryRevalidate(out _, out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(second.Body));
        }

        [Test]
        public void PreparationInsideCallbackRejectsReentrantSceneChangeForLaterObserver()
        {
            ReceiverBodyRealizationCheckpoint beforeChange = null;
            bool firstAccepted = false, secondAccepted = true;
            _receiver.RichRealizationAvailable += envelope => {
                if (envelope.Reason != RichMailboxReason.NativeSnapshot) return;
                firstAccepted = _binding.TryPrepareRealization(envelope.Snapshot, 1000000000, out beforeChange, out _);
                SpatialBlendAPI.TrySetRichValidityPolicyForTest(_receiver.Registration.NativeSpace, 1, out _);
            };
            _receiver.RichRealizationAvailable += envelope => {
                if (envelope.Reason != RichMailboxReason.NativeSnapshot) return;
                secondAccepted = _binding.TryPrepareRealization(envelope.Snapshot, 1000000000, out _, out _);
            };
            Assert.That(_binding.TrySubmit(Frame(), out string error), Is.True, error);
            Solve();
            Assert.That(firstAccepted, Is.True); Assert.That(beforeChange, Is.Not.Null);
            Assert.That(secondAccepted, Is.False);
            Assert.That(beforeChange.TryRevalidate(out _, out _), Is.False);
        }

        // ABI 4: scope belongs to the relation family, so the
        // support is a descendant of the actor's own element (its owner object), not an app-declared scope.
        private readonly Dictionary<SpatialBlendObject, SpatialBlendObject> _supportOwner =
            new Dictionary<SpatialBlendObject, SpatialBlendObject>();

        private SpatialBlendActor Actor(string space, bool source, SpatialBlendObject support)
        {
            SpatialBlendObject owner = _supportOwner[support];
            var actor = Create<SpatialBlendActor>(space + "-actor");
            actor.Configure(owner, "participant", source, new[] {
                new SpatialBlendActorAnchor("locomotion", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("head_facing", SpatialBlendActorAnchorRole.Body, Vector3.zero),
                new SpatialBlendActorAnchor("seat_contact", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            });
            Assert.That(actor.TryRegister(out string error), Is.True, error); return actor;
        }
        private SpatialBlendObject Support(string space, string id)
        {
            var owner = Create<SpatialBlendObject>(space + "-owner"); owner.Configure(_world, space, space + "-actor-owner");
            var support = Create<SpatialBlendObject>(id); support.Configure(_world, space, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(support.TrySetSupportPolygon(new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
                new Vector3(1, 0, 1), new Vector3(-1, 0, 1) }, out string error), Is.True, error);
            Assert.That(support.TryRegister(out error), Is.True, error);
            _supportOwner[support] = owner; return support;
        }
        private T Create<T>(string name) where T : Component
        {
            var go = new GameObject(name); _created.Add(go); T component = go.AddComponent<T>();
            // Unity EditMode does not dispatch ordinary MonoBehaviour Awake.
            // Match the existing native Foundation fixture's explicit lifecycle.
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            return component;
        }
    }
}
#endif
