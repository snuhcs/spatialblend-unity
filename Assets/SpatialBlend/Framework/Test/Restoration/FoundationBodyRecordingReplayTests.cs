// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    internal static class BodyRecordingReplayFixture
    {
        internal const long Origin = 1000000000L, Ttl = 900000000L;
        internal static string Hash(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        internal static Matrix4x4 Local(int frame, int joint, bool largeLast = false)
        {
            Matrix4x4 value = ReceiverBodyFixture.Definition()[joint].ParentFromReferenceJoint;
            if (joint == 0)
            {
                // Exact signed rotation, authored independently of reader/FK.
                value.m00 = 0; value.m01 = -1; value.m10 = 1; value.m11 = 0;
                value.m03 = largeLast && frame == 2 ? 3e38f : .25f * frame;
                value.m13 = .75f; value.m23 = -1;
            }
            if (joint == 1) { value.m11 = 0; value.m12 = -1; value.m21 = 1; value.m22 = 0; }
            if (joint == 4) { value.m00 = 0; value.m01 = -1; value.m10 = 1; value.m11 = 0; }
            return value;
        }
        private static void Pose(BinaryWriter writer, Matrix4x4 matrix)
        {
            for (int i = 0; i < 3; ++i) writer.Write((double)matrix[i, 3]);
            for (int r = 0; r < 3; ++r) for (int c = 0; c < 3; ++c) writer.Write((double)matrix[r, c]);
        }
        internal static ReceiverBodyRecording Recording(long denominator = 10, bool largeLast = false,
            bool lastRootLoss = true, string receiptText = "authored-fifteen-joint-recording", string lineage = "synthetic")
        {
            byte[] payload;
            var references = ReceiverBodyFixture.Definition();
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("OSBODY1\0"));
                writer.Write(references.Length); writer.Write(3); writer.Write(1); writer.Write(1L); writer.Write(denominator);
                foreach (var joint in references)
                {
                    byte[] name = Encoding.UTF8.GetBytes(joint.Name);
                    writer.Write(joint.ParentIndex); writer.Write((ushort)name.Length); writer.Write(name);
                    Pose(writer, joint.ParentFromReferenceJoint);
                }
                writer.Write(2); writer.Write(0d); writer.Write(.125d); writer.Write(0d);
                for (int i = 0; i < 3; ++i)
                {
                    writer.Write((long)i); writer.Write(i / (double)denominator);
                    writer.Write((byte)(lastRootLoss && i == 2 ? 0 : 1)); Pose(writer, Matrix4x4.Translate(new Vector3(2, 0, 1)));
                    for (int j = 0; j < references.Length; ++j)
                    {
                        writer.Write((byte)(i == 1 && j == 5 ? 0 : 1)); Pose(writer, Local(i, j, largeLast));
                    }
                }
                payload = bytes.ToArray();
            }
            byte[] receipt = Encoding.UTF8.GetBytes(receiptText);
            string json = "{\"schema_version\":1,\"contract\":\"spatialblend-receiver-body-recording-v1\"," +
                "\"producer_source_sha256\":\"" + new string('c', 64) + "\",\"source_receipt_sha256\":\"" + Hash(receipt) + "\"," +
                "\"payload_sha256\":\"" + Hash(payload) + "\",\"payload_byte_count\":" + payload.Length + "," +
                "\"source_evidence_kind\":\"" + lineage + "\",\"calibration_status\":\"unreviewed_authored_mapping\"," +
                "\"runtime_admission_supported\":false,\"target_basis_label\":\"authored_unity_basis\",\"target_length_unit_label\":\"authored_metres\"," +
                "\"source_clock\":\"fixture_recorded_seconds\",\"source_clock_epoch\":1,\"frame_interval_numerator\":1," +
                "\"frame_interval_denominator\":" + denominator + ",\"frame_count\":3,\"joint_count\":15,\"end_site_count\":1}";
            byte[] metadata = Encoding.UTF8.GetBytes(json);
            Assert.That(ReceiverBodyRecording.TryRead(metadata, receipt, payload, Hash(metadata), out var recording, out string error), Is.True, error);
            return recording;
        }
        internal static ReceiverBodyProfileSpec Spec(ReceiverInputEpoch input = null, long origin = Origin,
            ReceiverBodySkeleton skeleton = null, Matrix4x4? world = null, long uncertainty = 20,
            long until = 0, double sourceOrigin = 0, string sourceClock = "fixture_recorded_seconds",
            ulong sourceEpoch = 1, ulong providerEpoch = 11, ReceiverBodyEvidenceKind evidence = ReceiverBodyEvidenceKind.RecordedBody)
        {
            input = input ?? ReceiverBodyFixture.Input();
            Assert.That(ReceiverBodyClockMapping.TryCreate(sourceClock, sourceEpoch, 1,
                evidence == ReceiverBodyEvidenceKind.RecordedBody ? ReceiverBodyClockMappingKind.ReplayTimeline : ReceiverBodyClockMappingKind.SyntheticFixture,
                "independently-authored-replay-clock", input.ClockId, input.ClockEpoch, sourceOrigin, origin, 1e9,
                uncertainty, Math.Max(1, origin-1000000000), until == 0 ? checked(origin+5000000000) : until,
                out var clock, out string error), Is.True, error);
            Assert.That(ReceiverBodyProfileSpec.TryCreate(input.RootAnchor, input.AnchorNames, 17, 19, "authored-recording-provider",
                providerEpoch, clock, evidence, "independent-authored-recording-calibration", skeleton ?? ReceiverBodyFixture.Skeleton(),
                world ?? Matrix4x4.Translate(new Vector3(4, 0, -2)), ReceiverBodyFixture.Anchors(), out var spec, out error), Is.True, error);
            return spec;
        }
        internal static ReceiverBodyRecordingReplayDefinition Definition(ReceiverBodyRecording recording = null,
            ReceiverBodyProfileSpec spec = null, ulong first = 17, long ttl = Ttl, long additionalUncertainty = 10)
        {
            recording = recording ?? Recording(); spec = spec ?? Spec();
            Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(recording, recording.ManifestSha256,
                "authored_unity_basis", "authored_metres", spec, first, ttl, additionalUncertainty, out var definition, out string error), Is.True, error);
            return definition;
        }
        internal static ReceiverBodyProfile Profile(ReceiverBodyRecordingReplayDefinition definition, ReceiverInputEpoch input = null)
        {
            Assert.That(definition.Body.TryMaterialize(input ?? ReceiverBodyFixture.Input(), out var profile, out string error), Is.True, error);
            return profile;
        }
        internal sealed class PinnedFixtureApproval : IReceiverBodyRecordingReplayApproval
        {
            private readonly string _definition;
            private readonly ReceiverBodyProfile _profile;
            internal PinnedFixtureApproval(ReceiverBodyRecordingReplayDefinition definition, ReceiverBodyProfile profile)
            { _definition = definition.Fingerprint; _profile = profile; }
            internal int Calls;
            internal bool Deny, Throw, InvalidReference;
            internal Action OnApprove;
            internal ReceiverBodyRecordingReplayApprovalRequest Last;
            public bool TryApprove(ReceiverBodyRecordingReplayApprovalRequest request, out string reviewReference, out string error)
            {
                ++Calls; Last = request; OnApprove?.Invoke();
                if (Throw) throw new InvalidOperationException("fixture rejection");
                error = null; reviewReference = InvalidReference ? "" : "explicit-authored-fixture-review";
                return !Deny && request.Definition.Fingerprint == _definition && ReferenceEquals(request.Profile, _profile);
            }
        }
        internal static void ExactRows(ReceiverBodyRecordedSample sample, ReceiverBodyFrame frame)
        {
            Assert.That(frame.SourceSequence, Is.EqualTo(sample.SourceSequence));
            Assert.That(BitConverter.DoubleToInt64Bits(frame.SourceTimeSeconds), Is.EqualTo(BitConverter.DoubleToInt64Bits(sample.SourceTimeSeconds)));
            Assert.That(frame.Joints.Count, Is.EqualTo(sample.Joints.Count));
            for (int j = 0; j < sample.Joints.Count; ++j)
            {
                Assert.That(frame.Joints[j].Name, Is.EqualTo(sample.Joints[j].Name));
                Assert.That(frame.Joints[j].Index, Is.EqualTo(j)); Assert.That(frame.Joints[j].Available, Is.EqualTo(sample.Joints[j].Available));
                for (int r = 0; r < 4; ++r) for (int c = 0; c < 4; ++c)
                    Assert.That(BitConverter.ToInt32(BitConverter.GetBytes(frame.Joints[j].ParentFromJoint[r, c]), 0),
                        Is.EqualTo(BitConverter.ToInt32(BitConverter.GetBytes(sample.Joints[j].ParentFromJoint[r, c]), 0)));
            }
            Assert.That(frame.GeometricInput.Anchors.Single(a => a.Name == frame.Profile.Input.RootAnchor).Available,
                Is.EqualTo(sample.LocomotionRootAvailable));
        }
    }

    [Category("FoundationReceiverInput")]
    public sealed class FoundationBodyRecordingReplayTests
    {
        [Test]
        public void RecordingReplayApprovedExactBindingPreservesAllRowsAndOriginalClocks()
        {
            var definition = BodyRecordingReplayFixture.Definition(); var profile = BodyRecordingReplayFixture.Profile(definition);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            var approval = new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile);
            Assert.That(source.TryBind(profile, approval, out string error), Is.True, error);
            Assert.That(source.Profile, Is.SameAs(profile)); Assert.That(approval.Last.Definition, Is.SameAs(definition));
            Assert.That(approval.Last.Profile, Is.SameAs(profile)); Assert.That(source.ApprovalRequestFingerprint, Is.EqualTo(approval.Last.Fingerprint));
            Assert.That(profile.EvidenceKind, Is.EqualTo(ReceiverBodyEvidenceKind.RecordedBody));
            Assert.That(definition.Recording.DeclaredSourceEvidenceKind, Is.EqualTo("synthetic"));
            Assert.That(definition.Recording.RuntimeAdmissionSupported, Is.False);
            for (int i = 0; i < definition.Recording.FrameCount; ++i)
            {
                definition.Recording.TryGetSample(i, out var sample);
                Assert.That(profile.ClockMapping.TryMap(sample.SourceTimeSeconds, out long lower, out long upper, out error), Is.True, error);
                Assert.That(source.TryAcquire(profile, upper, out var status, out var frame, out error), Is.True, error);
                Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Sample)); BodyRecordingReplayFixture.ExactRows(sample, frame);
                Assert.That(frame.GeometricInput.Sequence, Is.EqualTo(17UL+(ulong)i));
                Assert.That(frame.GeometricInput.ObservedAtNanoseconds, Is.EqualTo(lower));
                Assert.That(frame.GeometricInput.ValidUntilNanoseconds, Is.EqualTo(upper+BodyRecordingReplayFixture.Ttl));
            }
            Assert.That(source.DeliveredFrameCount, Is.EqualTo(3));
        }

        [Test]
        public void RecordingReplayMissingDeniedThrowingApprovalNeverExposesFrames()
        {
            var definition = BodyRecordingReplayFixture.Definition(); var profile = BodyRecordingReplayFixture.Profile(definition);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(source.TryAcquire(profile, BodyRecordingReplayFixture.Origin, out _, out var frame, out _), Is.False); Assert.That(frame, Is.Null);
            Assert.That(source.TryBind(profile, null, out _), Is.False);
            foreach (var approval in new[] {
                new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile) { Deny = true },
                new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile) { Throw = true },
                new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile) { InvalidReference = true } })
            {
                Assert.That(source.TryBind(profile, approval, out _), Is.False);
                Assert.That(source.IsBound, Is.False); Assert.That(source.Profile, Is.Null);
                Assert.That(source.DeliveredFrameCount, Is.Zero); Assert.That(source.ApprovalRequestFingerprint, Is.Null);
            }
            Assert.That(source.TryBind(profile, new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile), out string error), Is.True, error);
        }

        [Test]
        public void RecordingReplayRejectsPinsLabelsAndExactReferenceMismatch()
        {
            var record = BodyRecordingReplayFixture.Recording(); var spec = BodyRecordingReplayFixture.Spec();
            foreach (var values in new[] { new[] { new string('b', 64), "authored_unity_basis", "authored_metres" },
                new[] { record.ManifestSha256, "other_basis", "authored_metres" }, new[] { record.ManifestSha256, "authored_unity_basis", "cm" } })
                Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, values[0], values[1], values[2], spec, 1, 100, 0, out _, out _), Is.False);
            foreach (int change in Enumerable.Range(0, 3))
            {
                var rows = ReceiverBodyFixture.Definition(); var joint = rows[14]; Matrix4x4 pose = joint.ParentFromReferenceJoint;
                if (change == 0) pose.m03 = .125f;
                if (change == 1) pose.m03 = BitConverter.ToSingle(BitConverter.GetBytes(int.MinValue), 0); // exact -0 reference bit
                rows[14] = new ReceiverBodyJoint(change == 2 ? "renamed_ankle" : joint.Name, joint.ParentIndex, pose);
                Assert.That(ReceiverBodySkeleton.TryCreate("independent", 1, rows, out var skeleton, out string error), Is.True, error);
                Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, record.ManifestSha256, "authored_unity_basis", "authored_metres",
                    BodyRecordingReplayFixture.Spec(skeleton: skeleton), 1, 100, 0, out _, out _), Is.False);
            }
        }

        [Test]
        public void RecordingReplayRejectsClockSequenceAndCompleteExpiryWindowChanges()
        {
            var record = BodyRecordingReplayFixture.Recording();
            var unknown = BodyRecordingReplayFixture.Recording(lineage: "unknown");
            Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(unknown, unknown.ManifestSha256, "authored_unity_basis", "authored_metres",
                BodyRecordingReplayFixture.Spec(), 1, 100, 0, out var unknownDefinition, out _), Is.False);
            Assert.That(unknownDefinition, Is.Null);
            foreach (var spec in new[] { BodyRecordingReplayFixture.Spec(uncertainty: 0), BodyRecordingReplayFixture.Spec(sourceOrigin: .1),
                BodyRecordingReplayFixture.Spec(sourceClock: "other"), BodyRecordingReplayFixture.Spec(sourceEpoch: 2),
                BodyRecordingReplayFixture.Spec(evidence: ReceiverBodyEvidenceKind.SyntheticFixture),
                BodyRecordingReplayFixture.Spec(until: BodyRecordingReplayFixture.Origin+250000000) })
                Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, record.ManifestSha256, "authored_unity_basis", "authored_metres",
                    spec, 1, BodyRecordingReplayFixture.Ttl, 0, out _, out _), Is.False);
            foreach (ulong first in new[] { 0UL, ulong.MaxValue, ulong.MaxValue-1 })
                Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, record.ManifestSha256, "authored_unity_basis", "authored_metres",
                    BodyRecordingReplayFixture.Spec(), first, 100, 0, out _, out _), Is.False);
            foreach (long ttl in new[] { -1L, 0, 1000000000L })
                Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, record.ManifestSha256, "authored_unity_basis", "authored_metres",
                    BodyRecordingReplayFixture.Spec(), 1, ttl, 0, out _, out _), Is.False);
            Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, record.ManifestSha256, "authored_unity_basis", "authored_metres",
                BodyRecordingReplayFixture.Spec(uncertainty: 20), 1, 100, 20, out _, out _), Is.False, "Rounding is additional uncertainty.");
            var late = BodyRecordingReplayFixture.Spec(origin: long.MaxValue-300000000, until: long.MaxValue);
            Assert.That(ReceiverBodyRecordingReplayDefinition.TryCreate(record, record.ManifestSha256, "authored_unity_basis", "authored_metres",
                late, 1, BodyRecordingReplayFixture.Ttl, 0, out _, out _), Is.False, "Checked upper+TTL overflow.");
        }

        [Test]
        public void RecordingReplayProfileAndEpochCannotReplaceApprovedIdentity()
        {
            var definition = BodyRecordingReplayFixture.Definition(); var profile = BodyRecordingReplayFixture.Profile(definition);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            var approval = new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile);
            var altered = BodyRecordingReplayFixture.Definition(spec: BodyRecordingReplayFixture.Spec(providerEpoch: 12));
            Assert.That(source.TryBind(BodyRecordingReplayFixture.Profile(altered), approval, out _), Is.False); Assert.That(approval.Calls, Is.Zero);
            Assert.That(source.TryBind(profile, approval, out string error), Is.True, error);
            var equalValues = BodyRecordingReplayFixture.Profile(definition);
            Assert.That(equalValues.Fingerprint, Is.EqualTo(profile.Fingerprint));
            Assert.That(source.TryAcquire(equalValues, BodyRecordingReplayFixture.Origin+100, out _, out _, out _), Is.False);
            Assert.That(source.TryBind(equalValues, approval, out _), Is.False); Assert.That(source.DeliveredFrameCount, Is.Zero);
            var second = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(second.TryBind(equalValues, approval, out _), Is.False, "Approval pins the actual profile object, not borrowed matching values.");
        }

        [Test]
        public void RecordingReplayInvalidFinalFkNeverPublishesApprovedPrefix()
        {
            var definition = BodyRecordingReplayFixture.Definition(BodyRecordingReplayFixture.Recording(largeLast: true),
                BodyRecordingReplayFixture.Spec(world: Matrix4x4.Translate(new Vector3(3e38f, 0, 0))));
            var profile = BodyRecordingReplayFixture.Profile(definition); var source = new ReceiverBodyRecordingReplaySource(definition);
            var approval = new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile);
            Assert.That(source.TryBind(profile, approval, out _), Is.False); Assert.That(approval.Calls, Is.EqualTo(1));
            Assert.That(source.IsBound, Is.False); Assert.That(source.Profile, Is.Null); Assert.That(source.ReviewReference, Is.Null);
            Assert.That(source.TryAcquire(profile, BodyRecordingReplayFixture.Origin+900000000, out _, out var frame, out _), Is.False);
            Assert.That(frame, Is.Null); Assert.That(source.DeliveredFrameCount, Is.Zero);
        }

        [Test]
        public void RecordingReplayDelegatesWaitBacklogExpiryWithoutRestamping()
        {
            var definition = BodyRecordingReplayFixture.Definition(); var profile = BodyRecordingReplayFixture.Profile(definition);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(source.TryBind(profile, new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile), out string error), Is.True, error);
            Assert.That(source.TryAcquire(profile, BodyRecordingReplayFixture.Origin-1, out var status, out var frame, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Unavailable)); Assert.That(frame, Is.Null);
            for (int i = 0; i < 3; ++i)
            {
                Assert.That(source.TryAcquire(profile, BodyRecordingReplayFixture.Origin+300000000, out status, out frame, out error), Is.True, error);
                Assert.That(frame.SourceSequence, Is.EqualTo(i));
            }
            long expiry = frame.GeometricInput.ValidUntilNanoseconds;
            Assert.That(source.TryAcquire(profile, expiry-1, out status, out frame, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.NoChange));
            Assert.That(source.TryAcquire(profile, expiry, out status, out frame, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Unavailable));
            var expired = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(expired.TryBind(profile, new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile), out error), Is.True, error);
            Assert.That(expired.TryAcquire(profile, expiry, out _, out frame, out _), Is.False);
            Assert.That(expired.IsFaulted, Is.True); Assert.That(expired.DeliveredFrameCount, Is.Zero); Assert.That(frame, Is.Null);
        }

        [Test]
        public void RecordingReplayThreadAndApprovalReentryCannotPublishOrConsume()
        {
            var definition = BodyRecordingReplayFixture.Definition(); var profile = BodyRecordingReplayFixture.Profile(definition);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            var approval = new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile);
            bool bound = true;
            var thread = new Thread(() => bound = source.TryBind(profile, approval, out _)); thread.Start(); thread.Join();
            Assert.That(bound, Is.False); Assert.That(approval.Calls, Is.Zero);
            approval.OnApprove = () =>
            {
                Assert.That(source.TryBind(profile, approval, out _), Is.False);
                Assert.That(source.TryAcquire(profile, BodyRecordingReplayFixture.Origin+100, out _, out _, out _), Is.False);
            };
            Assert.That(source.TryBind(profile, approval, out string error), Is.True, error);
            bool acquired = true;
            thread = new Thread(() => acquired = source.TryAcquire(profile, BodyRecordingReplayFixture.Origin+100, out _, out _, out _)); thread.Start(); thread.Join();
            Assert.That(acquired, Is.False); Assert.That(source.DeliveredFrameCount, Is.Zero);
        }

        [Test]
        public void RecordingReplayAdmittedEpochAgeIsCheckedBeforeApproval()
        {
            var definition = BodyRecordingReplayFixture.Definition();
            var input = new ReceiverInputEpoch(1, 2, "locomotion", new[] { "head_facing", "seat_contact", "locomotion" }, 1, 17, 19, 23, 29, 100);
            var profile = BodyRecordingReplayFixture.Profile(definition, input);
            var approval = new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(source.TryBind(profile, approval, out _), Is.False); Assert.That(approval.Calls, Is.Zero); Assert.That(source.IsBound, Is.False);
        }

        [Test]
        public void RecordingReplayFingerprintBindsSourceDefinitionAndWindowPolicy()
        {
            var first = BodyRecordingReplayFixture.Definition();
            var variants = new[] { BodyRecordingReplayFixture.Definition(first: 18), BodyRecordingReplayFixture.Definition(ttl: 800000000),
                BodyRecordingReplayFixture.Definition(additionalUncertainty: 11),
                BodyRecordingReplayFixture.Definition(BodyRecordingReplayFixture.Recording(receiptText: "another-exact-source-receipt")),
                BodyRecordingReplayFixture.Definition(BodyRecordingReplayFixture.Recording(lastRootLoss: false)),
                BodyRecordingReplayFixture.Definition(spec: BodyRecordingReplayFixture.Spec(providerEpoch: 12)),
                BodyRecordingReplayFixture.Definition(spec: BodyRecordingReplayFixture.Spec(world: Matrix4x4.identity)) };
            Assert.That(BodyRecordingReplayFixture.Definition().Fingerprint, Is.EqualTo(first.Fingerprint));
            Assert.That(variants.All(value => value.Fingerprint != first.Fingerprint), Is.True);
            Assert.That(variants.Select(value => value.Fingerprint).Distinct().Count(), Is.EqualTo(variants.Length));
        }

        [Test]
        public void RecordingReplayApprovalBindsNativeOwnerAndAgeBeyondProfileFingerprint()
        {
            var definition = BodyRecordingReplayFixture.Definition(); var original = BodyRecordingReplayFixture.Profile(definition);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(source.TryBind(original, new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, original), out string error), Is.True, error);
            foreach (var input in new[] {
                new ReceiverInputEpoch(99, 100, "locomotion", new[] { "head_facing", "seat_contact", "locomotion" }, 1, 17, 19, 23, 29, 1000000000),
                new ReceiverInputEpoch(1, 2, "locomotion", new[] { "head_facing", "seat_contact", "locomotion" }, 1, 17, 19, 23, 29, 950000000) })
            {
                var other = BodyRecordingReplayFixture.Profile(definition, input);
                Assert.That(other.Fingerprint, Is.EqualTo(original.Fingerprint), "Existing profile content identity is not native-owner authority.");
                var next = new ReceiverBodyRecordingReplaySource(definition);
                Assert.That(next.TryBind(other, new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, other), out error), Is.True, error);
                Assert.That(next.ApprovalRequestFingerprint, Is.Not.EqualTo(source.ApprovalRequestFingerprint));
            }
        }
    }

    public sealed partial class FoundationReceiverBodyNativeTests
    {
        private ReceiverBodyRecordingReplaySource BeginRecordingReplay(out ReceiverBodyRecordingReplayDefinition definition,
            out BodyRecordingReplayFixture.PinnedFixtureApproval approval)
        {
            Assert.That(_binding.TryClose(out string error), Is.True, error); _binding = null;
            Assert.That(_receiver.TryBeginReceiverInput(17, 19, out _writer, out error), Is.True, error);
            Assert.That(_writer.TryReadAcquisitionClock(out long now, out error), Is.True, error);
            definition = BodyRecordingReplayFixture.Definition(BodyRecordingReplayFixture.Recording(denominator: 1000000),
                BodyRecordingReplayFixture.Spec(_writer.Input, now-1000000));
            var profile = BodyRecordingReplayFixture.Profile(definition, _writer.Input);
            var source = new ReceiverBodyRecordingReplaySource(definition);
            approval = new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, profile);
            Assert.That(source.TryBind(profile, approval, out error), Is.True, error);
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, profile, out _binding, out error), Is.True, error);
            return source;
        }

        [Test]
        public void RecordingReplayNativeFullRowsNondefaultSequenceAndIndependentRootLoss()
        {
            var source = BeginRecordingReplay(out var definition, out _);
            string manifest = definition.Recording.ManifestSha256, receipt = definition.Recording.SourceReceiptSha256;
            for (int i = 0; i < 3; ++i)
            {
                Assert.That(_binding.TryReadAcquisitionClock(out long floor, out string error), Is.True, error);
                Assert.That(source.TryAcquire(_binding.Profile, floor, out var status, out var frame, out error), Is.True, error);
                Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Sample));
                definition.Recording.TryGetSample(i, out var sample); BodyRecordingReplayFixture.ExactRows(sample, frame);
                Assert.That(frame.GeometricInput.Sequence, Is.EqualTo(17UL+(ulong)i));
                Assert.That(_binding.TrySubmit(frame, out error), Is.True, error);
                var snapshot = Solve();
                if (sample.LocomotionRootAvailable)
                {
                    Assert.That(_binding.TryGetBodyForRealization(snapshot, out var joined, out error), Is.True, error);
                    Assert.That(joined, Is.SameAs(frame));
                }
                else
                {
                    Assert.That(frame.HasAllJointPoses, Is.True);
                    Assert.That(snapshot.Channels.Any(channel => channel.HasGoal), Is.False);
                    Assert.That(_delivered.Last().Reason, Is.EqualTo(RichMailboxReason.NativeRelease));
                }
            }
            Assert.That(source.DeliveredFrameCount, Is.EqualTo(3));
            Assert.That(definition.Recording.ManifestSha256, Is.EqualTo(manifest)); Assert.That(definition.Recording.SourceReceiptSha256, Is.EqualTo(receipt));
        }

        [Test]
        public void RecordingReplayNativeReplacementRequiresNewExactApprovalAndSource()
        {
            var source = BeginRecordingReplay(out var definition, out var oldApproval);
            Assert.That(_binding.TryReadAcquisitionClock(out long floor, out string error), Is.True, error);
            Assert.That(source.TryAcquire(_binding.Profile, floor, out _, out var oldFrame, out error), Is.True, error);
            Assert.That(_binding.TrySubmit(oldFrame, out error), Is.True, error);
            Assert.That(_binding.TryClose(out error), Is.True, error); _binding = null;
            Assert.That(_receiver.TryBeginReceiverInput(17, 19, out _writer, out error), Is.True, error);
            var next = BodyRecordingReplayFixture.Profile(definition, _writer.Input);
            Assert.That(source.TryBind(next, oldApproval, out _), Is.False);
            Assert.That(source.TryAcquire(next, floor, out _, out _, out _), Is.False);
            var replacement = new ReceiverBodyRecordingReplaySource(definition);
            Assert.That(replacement.TryBind(next, oldApproval, out _), Is.False); Assert.That(replacement.IsBound, Is.False);
            Assert.That(replacement.TryBind(next, new BodyRecordingReplayFixture.PinnedFixtureApproval(definition, next), out error), Is.True, error);
            Assert.That(replacement.ApprovalRequestFingerprint, Is.Not.EqualTo(source.ApprovalRequestFingerprint));
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, next, out _binding, out error), Is.True, error);
            Assert.That(_binding.TrySubmit(oldFrame, out _), Is.False);
            Assert.That(_binding.TryReadAcquisitionClock(out floor, out error), Is.True, error);
            Assert.That(replacement.TryAcquire(next, floor, out _, out var newFrame, out error), Is.True, error);
            Assert.That(_binding.TrySubmit(newFrame, out error), Is.True, error);
            Assert.That(_binding.TryGetBodyForRealization(Solve(), out var joined, out error), Is.True, error);
            Assert.That(joined, Is.SameAs(newFrame));
        }
    }
}
#endif
