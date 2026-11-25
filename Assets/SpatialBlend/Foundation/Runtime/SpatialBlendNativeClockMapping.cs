// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend
{
    /// <summary>
    /// A conservative interval in one receiver's local monotonic clock. It is
    /// not a source sample time or an application delivery timestamp.
    /// </summary>
    public readonly struct SpatialBlendLocalTimeInterval
    {
        private readonly bool _isValid;

        internal SpatialBlendLocalTimeInterval(double earliest, double latest)
        {
            _isValid = true;
            EarliestSeconds = earliest;
            LatestSeconds = latest;
        }

        public double EarliestSeconds { get; }
        public double LatestSeconds { get; }
        public double WidthSeconds => LatestSeconds - EarliestSeconds;

        /// <summary>Expiry is exclusive and uses the oldest possible receipt.</summary>
        public bool IsFreshAt(double localNowSeconds, double maximumAgeSeconds)
        {
            return _isValid && IsFinite(localNowSeconds) && IsFinite(maximumAgeSeconds) &&
                   maximumAgeSeconds > 0 && localNowSeconds >= LatestSeconds &&
                   localNowSeconds - EarliestSeconds < maximumAgeSeconds;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Explicit native-to-local monotonic clock calibration. The native clock
    /// read must occur between the supplied local before/after readings. Native
    /// timestamps are nanoseconds; local timestamps are seconds. Native clock
    /// identifiers and epochs are process-local and must never survive a process
    /// rebind. A clock ID is meaningful only together with its nonzero epoch.
    /// </summary>
    /// <remarks>
    /// The caller declares a maximum clock-rate error and calibration lifetime.
    /// This type checks those declared bounds; it cannot establish the hardware
    /// clock-rate bound itself. Unsupported clock evidence must not be relabelled
    /// as a calibrated mapping. No Unity API or mutable clock is read here.
    /// </remarks>
    public sealed class SpatialBlendNativeClockMapping
    {
        private const double NanosecondsPerSecond = 1000000000.0;
        private const double RelativeRoundingBound = 1.7763568394002505e-15;

        private readonly ulong _nativeReferenceNanoseconds;
        private readonly double _localBeforeSeconds;
        private readonly double _localAfterSeconds;
        private readonly double _rateError;
        private readonly double _maximumIntervalWidthSeconds;

        private SpatialBlendNativeClockMapping(ulong clockId, ulong clockEpoch,
                                             ulong nativeReferenceNanoseconds,
                                             double localBeforeSeconds, double localAfterSeconds,
                                             double maximumIntervalWidthSeconds,
                                             double rateError, double expiresAtSeconds)
        {
            ClockId = clockId;
            ClockEpoch = clockEpoch;
            _nativeReferenceNanoseconds = nativeReferenceNanoseconds;
            _localBeforeSeconds = localBeforeSeconds;
            _localAfterSeconds = localAfterSeconds;
            _maximumIntervalWidthSeconds = maximumIntervalWidthSeconds;
            _rateError = rateError;
            ExpiresAtSeconds = expiresAtSeconds;
        }

        public ulong ClockId { get; }
        /// <summary>
        /// Per-process lifetime of <see cref="ClockId"/>. A clock ID can be
        /// reused after native-process restart, so it is not sufficient alone.
        /// </summary>
        public ulong ClockEpoch { get; }
        public double ExpiresAtSeconds { get; }

        public static bool TryCreate(ulong clockId, ulong clockEpoch,
                                      ulong nativeReadNanoseconds,
                                      double localBeforeSeconds, double localAfterSeconds,
                                      double maximumIntervalWidthSeconds,
                                      double maximumRateErrorPartsPerMillion,
                                      double validForSeconds,
                                      out SpatialBlendNativeClockMapping mapping, out string error)
        {
            mapping = null;
            error = null;
            if (clockId == 0 || clockEpoch == 0 || !IsFinite(localBeforeSeconds) ||
                !IsFinite(localAfterSeconds) || localBeforeSeconds < 0 ||
                localAfterSeconds < localBeforeSeconds)
                return Fail("A known nonzero clock identity/epoch and ordered finite local calibration readings are required.", out error);
            if (!IsFinite(maximumIntervalWidthSeconds) || maximumIntervalWidthSeconds <= 0 ||
                !IsFinite(maximumRateErrorPartsPerMillion) || maximumRateErrorPartsPerMillion < 0 ||
                maximumRateErrorPartsPerMillion >= 1000000 ||
                !IsFinite(validForSeconds) || validForSeconds <= 0)
                return Fail("Clock uncertainty, rate-error and lifetime bounds must be explicit and finite.", out error);

            double expiration = localAfterSeconds + validForSeconds;
            if (!IsFinite(expiration) || expiration <= localAfterSeconds)
                return Fail("Clock calibration lifetime cannot be represented safely.", out error);
            double rounding = RoundingAllowance(localBeforeSeconds, localAfterSeconds, 0);
            if (localAfterSeconds - localBeforeSeconds + 2 * rounding > maximumIntervalWidthSeconds)
                return Fail("Clock calibration uncertainty exceeds its declared interval bound.", out error);

            mapping = new SpatialBlendNativeClockMapping(clockId, clockEpoch,
                nativeReadNanoseconds,
                localBeforeSeconds, localAfterSeconds, maximumIntervalWidthSeconds,
                maximumRateErrorPartsPerMillion / 1000000.0, expiration);
            return true;
        }

        /// <summary>
        /// Maps an already captured native event. Repeated polls never replace
        /// its native timestamp with the poll/queue/delivery time. Future events,
        /// changed clock identities/epochs, expired calibration and excessive
        /// uncertainty reject.
        /// </summary>
        public bool TryMap(ulong clockId, ulong clockEpoch,
                           ulong nativeEventNanoseconds,
                           double localNowSeconds, out SpatialBlendLocalTimeInterval interval,
                           out string error)
        {
            interval = default;
            error = null;
            if (clockId == 0 || clockEpoch == 0 || clockId != ClockId ||
                clockEpoch != ClockEpoch || !IsFinite(localNowSeconds) ||
                localNowSeconds < _localAfterSeconds || localNowSeconds >= ExpiresAtSeconds)
                return Fail("Native clock identity/epoch or local calibration lifetime is no longer valid.", out error);

            // Subtract unsigned integer timestamps first. Converting both large
            // absolute nanosecond counters to double would lose small deltas.
            double delta = nativeEventNanoseconds >= _nativeReferenceNanoseconds
                ? (nativeEventNanoseconds - _nativeReferenceNanoseconds) / NanosecondsPerSecond
                : -((_nativeReferenceNanoseconds - nativeEventNanoseconds) / NanosecondsPerSecond);
            double drift = Math.Abs(delta) * _rateError;
            double before = _localBeforeSeconds + delta;
            double after = _localAfterSeconds + delta;
            double rounding = RoundingAllowance(before, after, delta);
            double earliest = before - drift - rounding;
            double latest = after + drift + rounding;
            if (!IsFinite(earliest) || !IsFinite(latest) ||
                latest - earliest > _maximumIntervalWidthSeconds || earliest > localNowSeconds)
                return Fail("Native event time is future, non-finite or exceeds the clock uncertainty bound.", out error);

            // Receipt of this event proves it occurred no later than this local
            // call. Intersect the upper bound, without making its lower bound
            // newer; conservative age/expiry is unchanged by a subsequent poll.
            interval = new SpatialBlendLocalTimeInterval(earliest, Math.Min(latest, localNowSeconds));
            return true;
        }

        private static double RoundingAllowance(double before, double after, double delta)
        {
            double magnitude = Math.Max(1.0,
                Math.Max(Math.Abs(before), Math.Max(Math.Abs(after), Math.Abs(delta))));
            return magnitude * RelativeRoundingBound + 2e-9;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
