// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BigInteger = System.Numerics.BigInteger;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Structural endpoint metadata, never a joint or contact annotation.</summary>
    public readonly struct ReceiverBodyRecordedEndSite
    {
        internal ReceiverBodyRecordedEndSite(int parent, Vector3 offset) { ParentIndex = parent; Offset = offset; }
        public int ParentIndex { get; }
        public Vector3 Offset { get; }
    }

    /// <summary>
    /// Immutable source-domain sample, NOT a ReceiverBodyFrame or input lease.
    /// Pose coordinates retain the recording's unreviewed authored labels.
    /// </summary>
    public sealed class ReceiverBodyRecordedSample
    {
        internal ReceiverBodyRecordedSample(long sequence, double time, bool rootAvailable,
            Matrix4x4 root, ReceiverBodyJointPose[] joints)
        {
            SourceSequence = sequence; SourceTimeSeconds = time;
            LocomotionRootAvailable = rootAvailable; BodyFromLocomotionRoot = root;
            Joints = Array.AsReadOnly(joints);
        }
        public long SourceSequence { get; }
        public double SourceTimeSeconds { get; }
        public bool LocomotionRootAvailable { get; }
        public Matrix4x4 BodyFromLocomotionRoot { get; }
        public IReadOnlyList<ReceiverBodyJointPose> Joints { get; }
    }

    /// <summary>
    /// Framework-owned full-body recording staging boundary. Verifies exact
    /// out-of-band manifest/receipt/payload pins and EVERY source-domain row
    /// before exposing anything. It does not verify source access/calibration,
    /// create a runtime skeleton/profile/frame, acquire an epoch, or drive a rig.
    /// </summary>
    public sealed class ReceiverBodyRecording
    {
        public const string Contract = "spatialblend-receiver-body-recording-v1";
        public const int MaximumPayloadBytes = 128 * 1024 * 1024;
        private const int MaximumMetadataBytes = 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OSBODY1\0");
        private readonly ReceiverBodyRecordedSample[] _samples;
        private readonly BigInteger _maxTimeErrorNumerator, _maxTimeErrorDenominator;
        private readonly Metadata _metadata;

        private ReceiverBodyRecording(string identity, Metadata metadata, ReceiverBodyJoint[] joints,
            ReceiverBodyRecordedEndSite[] ends, ReceiverBodyRecordedSample[] samples, BigInteger errorN, BigInteger errorD)
        {
            ManifestSha256 = identity; _metadata = metadata;
            ReferenceJoints = Array.AsReadOnly(joints); EndSites = Array.AsReadOnly(ends);
            _samples = samples; _maxTimeErrorNumerator = errorN; _maxTimeErrorDenominator = errorD;
        }
        public string ManifestSha256 { get; }
        public string SourceReceiptSha256 => _metadata.ReceiptHash;
        public string ProducerSourceSha256 => _metadata.ProducerHash;
        public string DeclaredSourceEvidenceKind => _metadata.Evidence;
        public string TargetBasisLabel => _metadata.Basis;
        public string TargetLengthUnitLabel => _metadata.Unit;
        public string CalibrationStatus => "unreviewed_authored_mapping";
        public bool RuntimeAdmissionSupported => false;
        public string SourceClock => _metadata.Clock;
        public ulong SourceClockEpoch => (ulong)_metadata.ClockEpoch;
        public long FrameIntervalNumerator => _metadata.Numerator;
        public long FrameIntervalDenominator => _metadata.Denominator;
        public int FrameCount => _samples.Length;
        public IReadOnlyList<ReceiverBodyJoint> ReferenceJoints { get; }
        public IReadOnlyList<ReceiverBodyRecordedEndSite> EndSites { get; }

        public bool TryGetSample(int index, out ReceiverBodyRecordedSample sample)
        {
            sample = index >= 0 && index < _samples.Length ? _samples[index] : null;
            return sample != null;
        }

        public static bool TryRead(byte[] manifestBytes, byte[] sourceReceiptBytes, byte[] payloadBytes,
            string expectedManifestSha256, out ReceiverBodyRecording recording, out string error)
        {
            recording = null; error = null;
            if (!HashText(expectedManifestSha256) || manifestBytes == null || manifestBytes.Length == 0 ||
                manifestBytes.Length > MaximumMetadataBytes || sourceReceiptBytes == null || sourceReceiptBytes.Length == 0 ||
                sourceReceiptBytes.Length > MaximumMetadataBytes || payloadBytes == null || payloadBytes.Length == 0 ||
                payloadBytes.Length > MaximumPayloadBytes)
                return ReceiverInputValidation.Fail("A bounded complete recording and external manifest SHA-256 are required.", out error);
            // Own the exact bytes used for every subsequent hash/parse decision.
            byte[] manifest = (byte[])manifestBytes.Clone(), receipt = (byte[])sourceReceiptBytes.Clone(), payload = (byte[])payloadBytes.Clone();
            if (Hash(manifest) != expectedManifestSha256)
                return ReceiverInputValidation.Fail("Recording manifest differs from its external pin.", out error);
            try
            {
                if (!ReadMetadata(manifest, out Metadata meta, out error)) return false;
                if (meta.PayloadBytes != payload.LongLength || Hash(payload) != meta.PayloadHash || Hash(receipt) != meta.ReceiptHash)
                    return ReceiverInputValidation.Fail("Recording payload or source receipt differs from its pinned manifest.", out error);
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, ReceiverInputValidation.Utf8, false))
                {
                    if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                        return ReceiverInputValidation.Fail("Body recording magic/version mismatch.", out error);
                    int jointCount = reader.ReadInt32(), frameCount = reader.ReadInt32(), endCount = reader.ReadInt32();
                    long numerator = reader.ReadInt64(), denominator = reader.ReadInt64();
                    if (jointCount != meta.Joints || frameCount != meta.Frames || endCount != meta.Ends ||
                        numerator != meta.Numerator || denominator != meta.Denominator)
                        return ReceiverInputValidation.Fail("Body recording header disagrees with its manifest.", out error);
                    var joints = new ReceiverBodyJoint[jointCount];
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < jointCount; ++i)
                    {
                        int parent = reader.ReadInt32(), length = reader.ReadUInt16();
                        if ((i == 0 ? parent != -1 : parent < 0 || parent >= i) || length == 0 || length > 128)
                            return ReceiverInputValidation.Fail("Recorded skeleton requires one root and ordered bounded named joints.", out error);
                        byte[] nameBytes = reader.ReadBytes(length);
                        if (nameBytes.Length != length) throw new EndOfStreamException();
                        string name = ReceiverInputValidation.Utf8.GetString(nameBytes);
                        if (!ReceiverInputValidation.ValidName(name) || !names.Add(name))
                            return ReceiverInputValidation.Fail("Recorded joint names must be valid and unique.", out error);
                        joints[i] = new ReceiverBodyJoint(name, parent, ReadPose(reader));
                    }
                    var ends = new ReceiverBodyRecordedEndSite[endCount];
                    for (int i = 0; i < endCount; ++i)
                    {
                        int parent = reader.ReadInt32();
                        if (parent < 0 || parent >= jointCount) throw new InvalidDataException("Invalid End Site parent.");
                        ends[i] = new ReceiverBodyRecordedEndSite(parent, new Vector3(CastFloat(reader.ReadDouble()), CastFloat(reader.ReadDouble()), CastFloat(reader.ReadDouble())));
                    }
                    long remaining = checked((long)frameCount * (113L + 97L * jointCount));
                    if (stream.Length - stream.Position != remaining)
                        return ReceiverInputValidation.Fail("Body recording has truncated, excess or missing frame rows.", out error);
                    var samples = new ReceiverBodyRecordedSample[frameCount];
                    BigInteger maximumN = BigInteger.Zero, maximumD = BigInteger.One;
                    double lastTime = -1;
                    for (int i = 0; i < frameCount; ++i)
                    {
                        long sequence = reader.ReadInt64(); double time = reader.ReadDouble();
                        long exactNumerator = checked(i * numerator);
                        if (sequence != i || !NearestBinary64(time, exactNumerator, denominator) || time < lastTime)
                            return ReceiverInputValidation.Fail("Recorded ordinals/time must preserve the exact uniform interval rounded once to binary64.", out error);
                        lastTime = time;
                        Difference(time, exactNumerator, denominator, out BigInteger errorN, out BigInteger errorD);
                        if (errorN * maximumD > maximumN * errorD) { maximumN = errorN; maximumD = errorD; }
                        bool rootAvailable = ReadAvailability(reader); Matrix4x4 root = ReadPose(reader);
                        var rows = new ReceiverBodyJointPose[jointCount];
                        for (int j = 0; j < jointCount; ++j)
                            rows[j] = new ReceiverBodyJointPose(joints[j].Name, j, ReadAvailability(reader), ReadPose(reader));
                        samples[i] = new ReceiverBodyRecordedSample(sequence, time, rootAvailable, root, rows);
                    }
                    recording = new ReceiverBodyRecording(expectedManifestSha256, meta, joints, ends, samples, maximumN, maximumD);
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException || exception is OverflowException)
            { return ReceiverInputValidation.Fail("Malformed body recording: " + exception.Message, out error); }
        }

        /// <summary>Exact ceiling of source-time representation error × dyadic rate; NOT physical clock uncertainty.</summary>
        public bool TryGetTimeQuantizationNanoseconds(double nanosecondsPerSourceSecond, out long allowance, out string error)
        {
            allowance = 0; error = null;
            if (!RichKnownDouble.Finite(nanosecondsPerSourceSecond) || nanosecondsPerSourceSecond <= 0)
                return ReceiverInputValidation.Fail("A finite positive source-to-native rate is required.", out error);
            Fraction(nanosecondsPerSourceSecond, out BigInteger rateN, out BigInteger rateD);
            BigInteger numerator = _maxTimeErrorNumerator * rateN, denominator = _maxTimeErrorDenominator * rateD;
            BigInteger ceiling = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
            if (!remainder.IsZero) ++ceiling;
            if (ceiling > long.MaxValue)
                return ReceiverInputValidation.Fail("Source-time quantization allowance overflows native nanoseconds.", out error);
            allowance = (long)ceiling; return true;
        }

        /// <summary>Pure numerical replay compatibility only; grants no native epoch, calibration or runtime admission.</summary>
        public bool TryValidateReplayClock(ReceiverBodyClockMapping mapping, long additionalSourceUncertaintyNanoseconds, out string error)
        {
            error = null;
            if (mapping == null || mapping.Kind != ReceiverBodyClockMappingKind.ReplayTimeline ||
                mapping.SourceClock != SourceClock || mapping.SourceEpoch != SourceClockEpoch || mapping.SourceOriginSeconds != 0 ||
                additionalSourceUncertaintyNanoseconds < 0)
                return ReceiverInputValidation.Fail("Use the exact source clock/epoch and explicit zero-origin replay mapping plus independent uncertainty.", out error);
            if (!TryGetTimeQuantizationNanoseconds(mapping.NanosecondsPerSourceSecond, out long required, out error)) return false;
            if (additionalSourceUncertaintyNanoseconds > mapping.UncertaintyNanoseconds ||
                required > mapping.UncertaintyNanoseconds - additionalSourceUncertaintyNanoseconds)
                return ReceiverInputValidation.Fail("Replay uncertainty must include source-time rounding in addition to independently supplied uncertainty.", out error);
            foreach (var sample in _samples)
                if (!mapping.TryMap(sample.SourceTimeSeconds, out _, out _, out error)) return false;
            return true;
        }

        private static bool ReadAvailability(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new InvalidDataException("Availability must be an explicit byte 0 or 1.");
            return value == 1;
        }
        private static float CastFloat(double value)
        {
            float result = (float)value;
            if (!RichKnownDouble.Finite(value) || float.IsNaN(result) || float.IsInfinity(result) ||
                (value != 0 && result == 0) ||
                Math.Abs(value - (double)result) > Math.Abs(value) * 5.960464477539063e-8 + 0.5 * (double)float.Epsilon)
                throw new InvalidDataException("Pose is non-finite, underflows binary32, or exceeds its bounded cast error.");
            return result;
        }
        private static Matrix4x4 ReadPose(BinaryReader reader)
        {
            var original = new double[12];
            for (int i = 0; i < original.Length; ++i) original[i] = reader.ReadDouble();
            if (original.Any(value => !RichKnownDouble.Finite(value)))
                throw new InvalidDataException("Original binary64 pose is non-finite.");
            double gramError = 0;
            for (int a = 0; a < 3; ++a)
                for (int b = 0; b < 3; ++b)
                {
                    double dot = 0;
                    for (int k = 0; k < 3; ++k) dot += original[3 + 3*k + a] * original[3 + 3*k + b];
                    double difference = dot - (a == b ? 1 : 0); gramError += difference * difference;
                }
            double determinant = original[3]*(original[7]*original[11]-original[8]*original[10]) -
                original[4]*(original[6]*original[11]-original[8]*original[9]) +
                original[5]*(original[6]*original[10]-original[7]*original[9]);
            if (!RichKnownDouble.Finite(gramError) || !RichKnownDouble.Finite(determinant) ||
                gramError > 1e-20 || Math.Abs(determinant - 1) > 1e-10)
                throw new InvalidDataException("Original binary64 pose must be proper rigid before casting.");
            var pose = Matrix4x4.identity;
            for (int row = 0; row < 3; ++row) pose[row, 3] = CastFloat(original[row]);
            for (int row = 0; row < 3; ++row)
                for (int column = 0; column < 3; ++column) pose[row, column] = CastFloat(original[3 + 3*row + column]);
            if (!ReceiverInputValidation.TryNativePose(pose, out _))
                throw new InvalidDataException("Cast pose must remain proper rigid; no normalization or scale stripping.");
            return pose;
        }

        private static void Fraction(double value, out BigInteger numerator, out BigInteger denominator)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
            int biased = (int)((bits >> 52) & 0x7ff);
            ulong mantissa = bits & 0x000fffffffffffffUL;
            int exponent = biased == 0 ? -1074 : biased - 1023 - 52;
            if (biased != 0) mantissa |= 1UL << 52;
            numerator = new BigInteger(mantissa); denominator = BigInteger.One;
            if (exponent >= 0) numerator <<= exponent;
            else denominator <<= -exponent;
        }
        private static int CompareMidpoint(long exactN, long exactD, double left, double right)
        {
            Fraction(left, out BigInteger a, out BigInteger b); Fraction(right, out BigInteger c, out BigInteger d);
            return (new BigInteger(exactN) * 2 * b * d).CompareTo((a * d + c * b) * exactD);
        }
        private static bool NearestBinary64(double value, long exactN, long exactD)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            if (!RichKnownDouble.Finite(value) || bits < 0 || (exactN != 0 && value == 0)) return false;
            if (exactN == 0) return bits == 0;
            double previous = BitConverter.Int64BitsToDouble(bits - 1), next = BitConverter.Int64BitsToDouble(bits + 1);
            if (!RichKnownDouble.Finite(next)) return false;
            int lower = CompareMidpoint(exactN, exactD, previous, value), upper = CompareMidpoint(exactN, exactD, value, next);
            bool even = (bits & 1) == 0;
            return (lower > 0 || (lower == 0 && even)) && (upper < 0 || (upper == 0 && even));
        }
        private static void Difference(double value, long exactN, long exactD, out BigInteger numerator, out BigInteger denominator)
        {
            Fraction(value, out BigInteger n, out BigInteger d);
            numerator = BigInteger.Abs(n * exactD - new BigInteger(exactN) * d); denominator = d * exactD;
        }

        private sealed class Metadata
        {
            internal string ReceiptHash, PayloadHash, ProducerHash, Evidence, Basis, Unit, Clock;
            internal long PayloadBytes, Numerator, Denominator, ClockEpoch;
            internal int Joints, Frames, Ends;
        }
        private static bool ReadMetadata(byte[] bytes, out Metadata metadata, out string error)
        {
            metadata = null; error = null;
            if (!AnchorPoseJson.TryParse(ReceiverInputValidation.Utf8.GetString(bytes), out var root, out error)) return false;
            var fields = new[] { "schema_version", "contract", "producer_source_sha256", "source_receipt_sha256", "payload_sha256",
                "payload_byte_count", "source_evidence_kind", "calibration_status", "runtime_admission_supported", "target_basis_label",
                "target_length_unit_label", "source_clock", "source_clock_epoch", "frame_interval_numerator", "frame_interval_denominator",
                "frame_count", "joint_count", "end_site_count" };
            if (root.Object == null || root.Object.Count != fields.Length || fields.Any(name => !root.Object.ContainsKey(name)))
                return ReceiverInputValidation.Fail("Recording manifest fields must match the exact schema.", out error);
            var values = root.Object;
            var m = new Metadata();
            if (!AnchorPoseJson.Int32(values["schema_version"], out int version) || version != 1 || values["contract"].String != Contract ||
                values["calibration_status"].String != "unreviewed_authored_mapping" || values["runtime_admission_supported"].Boolean != false ||
                !AnchorPoseJson.Int64(values["payload_byte_count"], out m.PayloadBytes) || m.PayloadBytes <= 0 || m.PayloadBytes > MaximumPayloadBytes ||
                !AnchorPoseJson.Int64(values["frame_interval_numerator"], out m.Numerator) || m.Numerator <= 0 ||
                !AnchorPoseJson.Int64(values["frame_interval_denominator"], out m.Denominator) || m.Denominator <= 0 ||
                BigInteger.GreatestCommonDivisor(m.Numerator, m.Denominator) != BigInteger.One ||
                !AnchorPoseJson.Int64(values["source_clock_epoch"], out m.ClockEpoch) || m.ClockEpoch <= 0 ||
                !AnchorPoseJson.Int32(values["joint_count"], out m.Joints) || m.Joints < 1 || m.Joints > ReceiverBodySkeleton.MaximumJoints ||
                !AnchorPoseJson.Int32(values["frame_count"], out m.Frames) || m.Frames < 1 || m.Frames > BufferedReceiverBodySource.MaximumFrames ||
                (long)m.Joints * m.Frames > BufferedReceiverBodySource.MaximumJointSamples ||
                (m.Frames > 1 && m.Numerator > long.MaxValue / (m.Frames - 1)) ||
                !AnchorPoseJson.Int32(values["end_site_count"], out m.Ends) || m.Ends < 0 || m.Ends > ReceiverBodySkeleton.MaximumJoints)
                return ReceiverInputValidation.Fail("Recording manifest counts, time, or unreviewed admission flags are invalid.", out error);
            m.ReceiptHash = values["source_receipt_sha256"].String; m.PayloadHash = values["payload_sha256"].String;
            m.ProducerHash = values["producer_source_sha256"].String; m.Evidence = values["source_evidence_kind"].String;
            m.Basis = values["target_basis_label"].String; m.Unit = values["target_length_unit_label"].String; m.Clock = values["source_clock"].String;
            if (!HashText(m.ReceiptHash) || !HashText(m.PayloadHash) || !HashText(m.ProducerHash) ||
                (m.Evidence != "derived" && m.Evidence != "synthetic" && m.Evidence != "measured" && m.Evidence != "unknown") ||
                !ReceiverInputValidation.ValidName(m.Basis) || !ReceiverInputValidation.ValidName(m.Unit) || !ReceiverInputValidation.ValidName(m.Clock))
                return ReceiverInputValidation.Fail("Recording source pins, lineage declarations, and coordinate/clock labels are required.", out error);
            metadata = m; return true;
        }
        private static bool HashText(string value) => value != null && value.Length == 64 &&
            value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
