// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SpatialBlend
{
    public enum ReceiverBodyClockMappingKind { CaptureClock = 1, ReplayTimeline = 2, SyntheticFixture = 3 }

    /// <summary>
    /// Explicit bounded affine source-clock → native acquisition mapping.
    /// Calibration/replay scheduling must supply the parameters and uncertainty;
    /// equality of clock names is never synchronization. This value enforces a
    /// declared mapping, but does not attest that the calibration was measured.
    /// Changing any parameter requires a new body profile AND receiver epoch.
    /// </summary>
    public sealed class ReceiverBodyClockMapping
    {
        private ReceiverBodyClockMapping(string sourceClock, ulong sourceEpoch, ulong revision,
            ReceiverBodyClockMappingKind kind, string evidenceReference, ulong nativeClock, ulong nativeEpoch,
            double sourceOrigin, long nativeOrigin, double rate, long uncertainty, long from, long until)
        {
            SourceClock = sourceClock; SourceEpoch = sourceEpoch; Revision = revision; Kind = kind;
            EvidenceReference = evidenceReference; NativeClockId = nativeClock; NativeClockEpoch = nativeEpoch;
            SourceOriginSeconds = sourceOrigin; NativeOriginNanoseconds = nativeOrigin;
            NanosecondsPerSourceSecond = rate; UncertaintyNanoseconds = uncertainty;
            ValidFromNativeNanoseconds = from; ValidUntilNativeNanoseconds = until;
            Fingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-receiver-body-clock-v1"); writer.Write(sourceClock); writer.Write(sourceEpoch);
                writer.Write(revision); writer.Write((int)kind); writer.Write(evidenceReference);
                writer.Write(nativeClock); writer.Write(nativeEpoch); writer.Write(sourceOrigin); writer.Write(nativeOrigin);
                writer.Write(rate); writer.Write(uncertainty); writer.Write(from); writer.Write(until);
            });
        }
        public string SourceClock { get; }
        public ulong SourceEpoch { get; }
        public ulong Revision { get; }
        public ReceiverBodyClockMappingKind Kind { get; }
        public string EvidenceReference { get; }
        public ulong NativeClockId { get; }
        public ulong NativeClockEpoch { get; }
        public double SourceOriginSeconds { get; }
        public long NativeOriginNanoseconds { get; }
        public double NanosecondsPerSourceSecond { get; }
        public long UncertaintyNanoseconds { get; }
        public long ValidFromNativeNanoseconds { get; }
        public long ValidUntilNativeNanoseconds { get; }
        public string Fingerprint { get; }

        public static bool TryCreate(string sourceClock, ulong sourceEpoch, ulong revision,
            ReceiverBodyClockMappingKind kind, string evidenceReference, ulong nativeClock, ulong nativeEpoch,
            double sourceOriginSeconds, long nativeOriginNanoseconds, double nanosecondsPerSourceSecond,
            long uncertaintyNanoseconds, long validFromNativeNanoseconds, long validUntilNativeNanoseconds,
            out ReceiverBodyClockMapping mapping, out string error)
        {
            mapping = null; error = null;
            if (!ReceiverInputValidation.ValidName(sourceClock) || !ReceiverInputValidation.ValidName(evidenceReference) ||
                sourceEpoch == 0 || revision == 0 || nativeClock == 0 || nativeEpoch == 0 ||
                (kind != ReceiverBodyClockMappingKind.CaptureClock && kind != ReceiverBodyClockMappingKind.ReplayTimeline &&
                 kind != ReceiverBodyClockMappingKind.SyntheticFixture) ||
                !RichKnownDouble.Finite(sourceOriginSeconds) || sourceOriginSeconds < 0 ||
                !RichKnownDouble.Finite(nanosecondsPerSourceSecond) || nanosecondsPerSourceSecond <= 0 ||
                validFromNativeNanoseconds <= 0 || validUntilNativeNanoseconds <= validFromNativeNanoseconds ||
                uncertaintyNanoseconds < 0 || uncertaintyNanoseconds >= ReceiverInputValidation.MaximumAgeNanoseconds ||
                nativeOriginNanoseconds < validFromNativeNanoseconds || nativeOriginNanoseconds >= validUntilNativeNanoseconds)
                return ReceiverInputValidation.Fail("Clock mapping requires explicit source/native epochs, a positive rate and a bounded native calibration interval/uncertainty.", out error);
            mapping = new ReceiverBodyClockMapping(sourceClock, sourceEpoch, revision, kind, evidenceReference,
                nativeClock, nativeEpoch, sourceOriginSeconds, nativeOriginNanoseconds, nanosecondsPerSourceSecond,
                uncertaintyNanoseconds, validFromNativeNanoseconds, validUntilNativeNanoseconds);
            return true;
        }

        /// <summary>
        /// Inclusive acquisition bounds. Exact dyadic arithmetic on the supplied
        /// IEEE-754 values avoids rounding an affine result inward. Floor/ceil
        /// widen fractional nanoseconds without converting the native epoch to double;
        /// all arithmetic overflow, unknown time and out-of-calibration samples fail.
        /// Native expiry must be after Upper and cannot exceed calibration expiry.
        /// </summary>
        public bool TryMap(double sourceTimeSeconds, out long lower, out long upper, out string error)
        {
            lower = upper = 0; error = null;
            if (!RichKnownDouble.Finite(sourceTimeSeconds) || sourceTimeSeconds < 0)
                return ReceiverInputValidation.Fail("Mapping requires finite original source seconds.", out error);
            if (!TryOffsetBounds(sourceTimeSeconds, SourceOriginSeconds, NanosecondsPerSourceSecond,
                out long floorOffset, out long ceilOffset))
                return ReceiverInputValidation.Fail("Source-to-native acquisition offset overflows.", out error);
            try
            {
                long low = checked(NativeOriginNanoseconds + floorOffset - UncertaintyNanoseconds);
                long high = checked(NativeOriginNanoseconds + ceilOffset + UncertaintyNanoseconds);
                if (low < ValidFromNativeNanoseconds || high >= ValidUntilNativeNanoseconds)
                    return ReceiverInputValidation.Fail("Acquisition is outside the declared clock calibration/replay interval.", out error);
                lower = low; upper = high;
                return true;
            }
            catch (OverflowException)
            { return ReceiverInputValidation.Fail("Mapped acquisition interval overflows.", out error); }
        }

        private static bool TryOffsetBounds(double sample, double origin, double rate, out long lower, out long upper)
        {
            lower = upper = 0;
            if (sample == origin) return true;
            Decompose(sample, out BigInteger sampleInteger, out int sampleExponent);
            Decompose(origin, out BigInteger originInteger, out int originExponent);
            Decompose(rate, out BigInteger rateInteger, out int rateExponent);
            int exponent = Math.Min(sampleExponent, originExponent);
            BigInteger numerator = ((sampleInteger << (sampleExponent - exponent)) -
                (originInteger << (originExponent - exponent))) * rateInteger;
            exponent += rateExponent;
            BigInteger floor, ceiling;
            if (exponent >= 0) floor = ceiling = numerator << exponent;
            else
            {
                BigInteger quotient = BigInteger.DivRem(numerator, BigInteger.One << -exponent, out BigInteger remainder);
                floor = remainder.Sign < 0 ? quotient - BigInteger.One : quotient;
                ceiling = remainder.Sign > 0 ? quotient + BigInteger.One : quotient;
            }
            if (floor < long.MinValue || ceiling > long.MaxValue) return false;
            lower = (long)floor; upper = (long)ceiling;
            return true;
        }

        private static void Decompose(double nonnegativeFinite, out BigInteger significand, out int exponent)
        {
            // Public validation already rejects NaN, infinity and negative values.
            // Binary64 gives a constant bound on integer sizes (~4 Ki bits here),
            // including subnormals; there is no input-dependent allocation cap gap.
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(nonnegativeFinite);
            int biasedExponent = (int)((bits >> 52) & 0x7ff);
            ulong mantissa = bits & 0x000fffffffffffffUL;
            if (biasedExponent == 0) exponent = -1074;
            else { mantissa |= 1UL << 52; exponent = biasedExponent - 1023 - 52; }
            significand = new BigInteger(mantissa);
        }
    }

    internal static class ReceiverBodyHash
    {
        internal static string Compute(Action<BinaryWriter> write)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true)) write(writer);
                using (SHA256 hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static void WriteMatrix(BinaryWriter writer, UnityEngine.Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column) writer.Write(matrix[row, column]);
        }
    }
}
