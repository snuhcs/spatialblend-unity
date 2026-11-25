// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationReceiverInput")]
    public sealed class FoundationBodyRecordingTests
    {
        private sealed class Fixture
        {
            internal readonly int Count;
            internal readonly long Numerator, Denominator;
            internal byte[] Bytes;
            internal readonly byte[] Receipt = Encoding.UTF8.GetBytes("{\"fixture\":\"authored-no-dataset\"}");
            internal readonly long FrameStart, ReferenceStart;
            internal const int FrameSize = 307;

            internal Fixture(int frames = 4, long numerator = 1, long denominator = 10)
            {
                Count = frames; Numerator = numerator; Denominator = denominator;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("OSBODY1\0"));
                    writer.Write(2); writer.Write(frames); writer.Write(1); writer.Write(numerator); writer.Write(denominator);
                    writer.Write(-1); writer.Write((ushort)6); writer.Write(Encoding.ASCII.GetBytes("pelvis"));
                    ReferenceStart = stream.Position; WritePose(writer, Matrix4x4.Translate(new Vector3(0, 1, 0)));
                    writer.Write(0); writer.Write((ushort)4); writer.Write(Encoding.ASCII.GetBytes("hand"));
                    WritePose(writer, Matrix4x4.Translate(new Vector3(1, .25f, 0)));
                    writer.Write(1); writer.Write(0d); writer.Write(.5d); writer.Write(0d);
                    FrameStart = stream.Position;
                    for (int i = 0; i < frames; ++i)
                    {
                        writer.Write((long)i); writer.Write((double)(i * numerator) / denominator);
                        writer.Write((byte)0); WritePose(writer, Matrix4x4.identity);
                        writer.Write((byte)1); WritePose(writer, Local(i));
                        writer.Write((byte)(i == 1 ? 0 : 1)); WritePose(writer, Matrix4x4.Translate(new Vector3(1, .25f, 0)));
                    }
                    Bytes = stream.ToArray();
                }
            }
            internal static Matrix4x4 Local(int frame)
            {
                var value = Matrix4x4.identity;
                value.m00 = 0; value.m01 = -1; value.m10 = 1; value.m11 = 0;
                value.m03 = .25f * frame; value.m13 = .75f; value.m23 = -1;
                return value;
            }
            internal static void WritePose(BinaryWriter writer, Matrix4x4 value)
            {
                for (int i = 0; i < 3; ++i) writer.Write((double)value[i, 3]);
                for (int row = 0; row < 3; ++row)
                    for (int column = 0; column < 3; ++column) writer.Write((double)value[row, column]);
            }
            internal byte[] Metadata(byte[] payload = null)
            {
                payload = payload ?? Bytes;
                string json = "{\"schema_version\":1,\"contract\":\"spatialblend-receiver-body-recording-v1\"," +
                    "\"producer_source_sha256\":\"" + new string('a', 64) + "\",\"source_receipt_sha256\":\"" + Hash(Receipt) + "\"," +
                    "\"payload_sha256\":\"" + Hash(payload) + "\",\"payload_byte_count\":" + payload.Length + "," +
                    "\"source_evidence_kind\":\"synthetic\",\"calibration_status\":\"unreviewed_authored_mapping\"," +
                    "\"runtime_admission_supported\":false,\"target_basis_label\":\"authored_basis\",\"target_length_unit_label\":\"evaluation_unit\"," +
                    "\"source_clock\":\"fixture_uniform_seconds\",\"source_clock_epoch\":1,\"frame_interval_numerator\":" + Numerator +
                    ",\"frame_interval_denominator\":" + Denominator + ",\"frame_count\":" + Count + ",\"joint_count\":2,\"end_site_count\":1}";
                return Encoding.UTF8.GetBytes(json);
            }
            internal byte[] Mutate(long offset, Action<BinaryWriter> change)
            {
                byte[] copy = (byte[])Bytes.Clone();
                using (var stream = new MemoryStream(copy))
                using (var writer = new BinaryWriter(stream)) { stream.Position = offset; change(writer); }
                return copy;
            }
            internal ReceiverBodyRecording Read()
            {
                byte[] meta = Metadata();
                Assert.That(ReceiverBodyRecording.TryRead(meta, Receipt, Bytes, Hash(meta), out var recording, out string error), Is.True, error);
                return recording;
            }
        }

        private static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }
        private static void Reject(Fixture fixture, byte[] payload, byte[] metadata = null)
        {
            metadata = metadata ?? fixture.Metadata(payload);
            Assert.That(ReceiverBodyRecording.TryRead(metadata, fixture.Receipt, payload, Hash(metadata), out var recording, out _), Is.False);
            Assert.That(recording, Is.Null, "No successful prefix from a malformed recording.");
        }
        private static ReceiverBodyClockMapping Clock(ReceiverBodyRecording recording, long uncertainty = 1,
            long until = 5000000000, string clock = null, ulong epoch = 1, double origin = 0,
            ReceiverBodyClockMappingKind kind = ReceiverBodyClockMappingKind.ReplayTimeline)
        {
            Assert.That(ReceiverBodyClockMapping.TryCreate(clock ?? recording.SourceClock, epoch, 1, kind, "authored-clock-check",
                1, 1, origin, 1000000000, 1000000000, uncertainty, 1, until, out var mapping, out string error), Is.True, error);
            return mapping;
        }

        [Test]
        public void BodyRecordingPreservesWholeSourceRowsReferencesAndExplicitLoss()
        {
            var recording = new Fixture().Read();
            Assert.That(recording.FrameCount, Is.EqualTo(4)); Assert.That(recording.ReferenceJoints.Count, Is.EqualTo(2));
            Assert.That(recording.ReferenceJoints[0].ParentFromReferenceJoint.m13, Is.EqualTo(1));
            Assert.That(recording.ReferenceJoints[1].Name, Is.EqualTo("hand")); Assert.That(recording.ReferenceJoints[1].ParentIndex, Is.Zero);
            Assert.That(recording.EndSites.Count, Is.EqualTo(1)); Assert.That(recording.EndSites[0].ParentIndex, Is.EqualTo(1));
            Assert.That(recording.EndSites[0].Offset, Is.EqualTo(new Vector3(0, .5f, 0)));
            for (int i = 0; i < recording.FrameCount; ++i)
            {
                Assert.That(recording.TryGetSample(i, out var sample), Is.True);
                Assert.That(sample.SourceSequence, Is.EqualTo(i)); Assert.That(sample.SourceTimeSeconds, Is.EqualTo(i / 10d));
                Assert.That(sample.LocomotionRootAvailable, Is.False); Assert.That(sample.BodyFromLocomotionRoot, Is.EqualTo(Matrix4x4.identity));
                Assert.That(sample.Joints.Count, Is.EqualTo(2)); Assert.That(sample.Joints[0].ParentFromJoint, Is.EqualTo(Fixture.Local(i)));
                Assert.That(sample.Joints[1].Available, Is.EqualTo(i != 1));
                Assert.That(sample.Joints[1].ParentFromJoint.m03, Is.EqualTo(1), "Unavailable local data is retained, not reference-filled.");
            }
            Assert.That(recording.TryGetSample(-1, out _), Is.False); Assert.That(recording.TryGetSample(4, out _), Is.False);
            Assert.That(recording.DeclaredSourceEvidenceKind, Is.EqualTo("synthetic"));
            Assert.That(recording.TargetLengthUnitLabel, Is.EqualTo("evaluation_unit"));
            Assert.That(recording.RuntimeAdmissionSupported, Is.False);
        }

        [Test]
        public void BodyRecordingExternalPinsAndOwnedBytesCannotBeReplaced()
        {
            var fixture = new Fixture(); byte[] meta = fixture.Metadata();
            Assert.That(ReceiverBodyRecording.TryRead(meta, fixture.Receipt, fixture.Bytes, Hash(meta), out var recording, out string error), Is.True, error);
            meta[0] = 0; fixture.Bytes[0] = 0; fixture.Receipt[0] = 0;
            Assert.That(recording.TryGetSample(3, out var sample), Is.True);
            Assert.That(sample.Joints[0].ParentFromJoint, Is.EqualTo(Fixture.Local(3)));
            fixture = new Fixture(); meta = fixture.Metadata();
            Assert.That(ReceiverBodyRecording.TryRead(meta, fixture.Receipt, fixture.Bytes, new string('b', 64), out _, out _), Is.False);
            Assert.That(ReceiverBodyRecording.TryRead(meta, new byte[] { 0 }, fixture.Bytes, Hash(meta), out _, out _), Is.False);
            byte[] changed = (byte[])fixture.Bytes.Clone(); changed[changed.Length - 1] ^= 1;
            Assert.That(ReceiverBodyRecording.TryRead(meta, fixture.Receipt, changed, Hash(meta), out _, out _), Is.False);
        }

        [Test]
        public void BodyRecordingStrictManifestCannotSelfApproveCalibrationOrRuntime()
        {
            var fixture = new Fixture(); string text = Encoding.UTF8.GetString(fixture.Metadata());
            foreach (string bad in new[] { text.Replace("unreviewed_authored_mapping", "reviewed"),
                text.Replace("\"runtime_admission_supported\":false", "\"runtime_admission_supported\":true"),
                text.Replace("\"schema_version\":1", "\"schema_version\":true"),
                text.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1"),
                text.Replace("\"schema_version\":1", "\"unexpected\":1,\"schema_version\":1"),
                text.Replace("\"frame_count\":4", "\"frame_count\":100001"),
                text.Replace("\"joint_count\":2", "\"joint_count\":129"),
                text.Replace("\"frame_interval_denominator\":10", "\"frame_interval_denominator\":0") })
                Reject(fixture, fixture.Bytes, Encoding.UTF8.GetBytes(bad));
        }

        [Test]
        public void BodyRecordingMalformedFinalFrameAndSkeletonNeverExposePrefix()
        {
            var f = new Fixture(); long last = f.FrameStart + 3 * Fixture.FrameSize;
            foreach (byte[] bad in new[] { f.Bytes.Take(f.Bytes.Length - 1).ToArray(), f.Bytes.Concat(new byte[] { 0 }).ToArray(),
                f.Mutate(0, w => w.Write((byte)0)), f.Mutate(36, w => w.Write(0)),
                f.Mutate(last, w => w.Write(2L)), f.Mutate(last + 8, w => w.Write(.4d)),
                f.Mutate(last + 16, w => w.Write((byte)2)), f.Mutate(last + 113, w => w.Write((byte)2)),
                f.Mutate(last + 114, w => w.Write(double.NaN)), f.Mutate(8, w => w.Write(3)) })
                Reject(f, bad);
        }

        [Test]
        public void BodyRecordingCastRejectsOverflowUnderflowScaleAndReflection()
        {
            var f = new Fixture();
            foreach (double bad in new[] { double.PositiveInfinity, 1e40, 1e-50 })
                Reject(f, f.Mutate(f.ReferenceStart, w => w.Write(bad)));
            foreach (double bad in new[] { 2d, -1d })
                Reject(f, f.Mutate(f.ReferenceStart + 24, w => w.Write(bad)));
            Reject(f, f.Mutate(f.ReferenceStart + 32, w => w.Write(1e-50)));
            Reject(f, f.Mutate(f.ReferenceStart + 24, w => w.Write(1d + Math.Pow(2, -30))));
            long unavailableHand = f.FrameStart + Fixture.FrameSize + 211;
            Reject(f, f.Mutate(unavailableHand, w => w.Write(double.NaN)));
        }

        [Test]
        public void BodyRecordingExactFractionUsesNearestEvenNotFpsOrAccumulatedTime()
        {
            var f = new Fixture(4, 16667, 1000000); var recording = f.Read();
            Assert.That(recording.FrameIntervalNumerator, Is.EqualTo(16667));
            Assert.That(recording.FrameIntervalDenominator, Is.EqualTo(1000000));
            Reject(f, f.Mutate(f.FrameStart + Fixture.FrameSize + 8, w => w.Write(1d / 60)));
            var midpoint = new Fixture(2, 9007199254740993, 9007199254740992);
            var exact = midpoint.Read(); Assert.That(exact.TryGetSample(1, out var sample), Is.True);
            Assert.That(sample.SourceTimeSeconds, Is.EqualTo(1));
            Reject(midpoint, midpoint.Mutate(midpoint.FrameStart + Fixture.FrameSize + 8,
                w => w.Write(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1d) + 1))));
        }

        [Test]
        public void BodyRecordingQuantizationIsAdditionalToIndependentClockUncertainty()
        {
            var recording = new Fixture().Read();
            Assert.That(recording.TryGetTimeQuantizationNanoseconds(1e9, out long allowance, out string error), Is.True, error);
            Assert.That(allowance, Is.EqualTo(1));
            Assert.That(recording.TryValidateReplayClock(Clock(recording, 0), 0, out _), Is.False);
            Assert.That(recording.TryValidateReplayClock(Clock(recording, 1), 0, out error), Is.True, error);
            Assert.That(recording.TryValidateReplayClock(Clock(recording, 1), 1, out _), Is.False);
            Assert.That(recording.TryValidateReplayClock(Clock(recording, 2), 1, out error), Is.True, error);
            Assert.That(recording.TryGetTimeQuantizationNanoseconds(1e308, out _, out _), Is.False);
            var exact = new Fixture(3, 1, 4).Read();
            Assert.That(exact.TryGetTimeQuantizationNanoseconds(1e9, out allowance, out error), Is.True, error);
            Assert.That(allowance, Is.Zero);
        }

        [Test]
        public void BodyRecordingClockIdentityBoundsAndSourceOriginCannotBeRewritten()
        {
            var recording = new Fixture().Read();
            foreach (var clock in new[] { Clock(recording, clock: "other"), Clock(recording, epoch: 2),
                Clock(recording, origin: 1), Clock(recording, kind: ReceiverBodyClockMappingKind.CaptureClock),
                Clock(recording, until: 1100000000) })
                Assert.That(recording.TryValidateReplayClock(clock, 0, out _), Is.False);
            Assert.That(recording.TryValidateReplayClock(null, 0, out _), Is.False);
            Assert.That(recording.TryValidateReplayClock(Clock(recording), -1, out _), Is.False);
        }
    }
}
#endif
