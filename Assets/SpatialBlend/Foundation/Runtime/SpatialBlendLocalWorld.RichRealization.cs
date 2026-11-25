// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Explicit receiver-side admission limits for diagnostic rich realization
    /// delivery. MailboxCapacity is a pinned per-world active-or-undrained
    /// lifetime limit, not a per-actor queue hint.
    /// </summary>
    public sealed class RichRealizationSubscriptionOptions
    {
        public int MailboxCapacity { get; set; } = 16;
        public double MaximumNativeAgeSeconds { get; set; } = .5d;
        public double MaximumCalibrationIntervalSeconds { get; set; } = .02d;
        public double MaximumClockRateErrorPartsPerMillion { get; set; } = 100d;
        public double CalibrationLifetimeSeconds { get; set; } = 5d;
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        private sealed class RichSubscription
        {
            internal LocalActorRegistration Registration;
            internal ulong Lifetime;
            internal RichRealizationMailbox Mailbox;
            internal SpatialBlendNativeClockMapping Mapping;
            internal Action<RichRealizationMailboxEnvelope> Deliver;
            internal double MaximumNativeAgeSeconds;
            internal bool Retiring;
            internal bool NativeReleased;
            internal bool HasExpiryDeadline;
            internal double ExpiryDeadlineSeconds;
            internal bool ExpiryQueued;
            // Only owner-delivered native snapshots can prepare body work.
            // Header equality on a caller-created DTO is not provenance.
            internal RichRealizationMailboxEnvelope LastDelivered;
        }

        private readonly object _richLock = new object();
        private readonly Dictionary<ulong, RichSubscription> _richByLifetime =
            new Dictionary<ulong, RichSubscription>();
        private readonly HashSet<long> _richCallbackSpaces = new HashSet<long>();
        private int _richAggregateCapacity;
        // Deterministic production-adapter tests may replace only the local
        // Stopwatch source. Native time remains read from the negotiated API.
        private Func<double> _testOnlyRichStopwatchClock;
        internal Func<double> TestOnlyRichStopwatchClock
        {
            get { lock (_richLock) return _testOnlyRichStopwatchClock; }
            set { lock (_richLock) _testOnlyRichStopwatchClock = value; }
        }

        internal bool TryEnableRichRealization(LocalActorRegistration registration,
                                               RichRealizationSubscriptionOptions options,
                                               Action<RichRealizationMailboxEnvelope> deliver,
                                               out string error)
            => TryEnableRichRealization(registration, options, deliver, out _, out error);

        internal bool TryEnableRichRealization(LocalActorRegistration registration,
                                               RichRealizationSubscriptionOptions options,
                                               Action<RichRealizationMailboxEnvelope> deliver,
                                               out ulong admittedLifetime, out string error)
        {
            admittedLifetime = 0; error = null;
            if (registration == null || !registration.IsLive || registration.World != this ||
                options == null || deliver == null || _shuttingDown || !Valid(options))
            {
                error = "Rich subscription requires a live receiver, a callback, and finite positive limits.";
                return false;
            }

            lock (_richLock)
            {
                foreach (RichSubscription existing in _richByLifetime.Values)
                    if (ReferenceEquals(existing.Registration, registration) && !existing.Retiring)
                    {
                        error = "This actor already has an active rich subscription.";
                        return false;
                    }
                if (_richByLifetime.Count > 0 && options.MailboxCapacity != _richAggregateCapacity)
                {
                    error = "Rich mailbox capacity is pinned while this world has active or undrained lifetimes.";
                    return false;
                }
                if (_richByLifetime.Count >= options.MailboxCapacity)
                {
                    error = "Rich mailbox capacity is exhausted by active or undrained lifetimes.";
                    return false;
                }
            }
            if (!SpatialBlendAPI.TryActivateRichRealization(registration.NativeSpace,
                                                            registration.NativeActor,
                                                            out ulong lifetime,
                                                            out error))
                return false;
            if (!TryCalibrateRich(options, out SpatialBlendNativeClockMapping mapping, out error))
            {
                CleanupUnadmittedNativeLifetime(lifetime);
                return false;
            }
            // World capacity bounds all token slots. Each admitted lifetime has
            // one coalescing pending node, so its private mailbox needs one slot.
            var mailbox = new RichRealizationMailbox(1);
            if (!mailbox.TryAdmit(registration.NativeSpace, registration.NativeActor, lifetime,
                                  mapping.ClockId, mapping.ClockEpoch,
                                  options.MaximumNativeAgeSeconds, mapping, out error))
            {
                CleanupUnadmittedNativeLifetime(lifetime);
                return false;
            }

            var subscription = new RichSubscription
            {
                Registration = registration,
                Lifetime = lifetime,
                Mailbox = mailbox,
                Mapping = mapping,
                Deliver = deliver,
                MaximumNativeAgeSeconds = options.MaximumNativeAgeSeconds
            };
            bool registerCallback;
            bool duplicateLifetime;
            lock (_richLock)
            {
                duplicateLifetime = _richByLifetime.ContainsKey(lifetime);
                if (_richByLifetime.Count >= (_richAggregateCapacity == 0
                    ? options.MailboxCapacity : _richAggregateCapacity))
                {
                    duplicateLifetime = true;
                    error = "Rich mailbox capacity was exhausted during activation.";
                }
                registerCallback = !_richCallbackSpaces.Contains(registration.NativeSpace);
                // Install routing before native registration: a synchronous callback
                // is copied into this exact slot, never a future replacement.
                if (!duplicateLifetime)
                {
                    _richByLifetime.Add(lifetime, subscription);
                    if (_richAggregateCapacity == 0) _richAggregateCapacity = options.MailboxCapacity;
                }
            }
            if (duplicateLifetime)
            {
                CleanupUnadmittedNativeLifetime(lifetime);
                if (error == null) error = "Native rich activation returned an already-routed lifetime token.";
                return false;
            }
            if (registerCallback && !SpatialBlendAPI.TryRegisterOnRichRealization(
                registration.NativeSpace, OnRichSnapshot, out error))
            {
                lock (_richLock) _richByLifetime.Remove(lifetime);
                lock (_richLock) if (_richByLifetime.Count == 0) _richAggregateCapacity = 0;
                CleanupUnadmittedNativeLifetime(lifetime);
                return false;
            }
            if (registerCallback)
                lock (_richLock) _richCallbackSpaces.Add(registration.NativeSpace);
            admittedLifetime = lifetime;
            return true;
        }

        internal bool TryPollRichRealization(LocalActorRegistration registration, out string error)
        {
            error = null;
            if (registration == null || registration.World != this || !registration.IsLive)
            {
                error = "A live receiver registration is required to poll rich realization.";
                return false;
            }
            RichSubscription subscription = FindRichSubscription(registration);
            if (subscription == null || subscription.Retiring)
            {
                error = "This actor has no active rich subscription.";
                return false;
            }
            if (!SpatialBlendAPI.TryPollRichRealization(subscription.Lifetime, out RichRealizationSnapshot snapshot,
                                                       out int nativeResult, out error))
                return false;
            if (nativeResult == 0) return true;
            if (nativeResult != 1 || snapshot == null)
            {
                error = "Rich poll returned an unsupported result.";
                return false;
            }
            // Poll intentionally reuses activation calibration: it cannot refresh
            // the native age bound or clock mapping lifetime.
            return subscription.Mailbox.TryEnqueue(snapshot, RichStopwatchSeconds(), out error);
        }

        internal void DisableRichRealization(LocalActorRegistration registration)
            => DisableRichRealization(registration, 0);

        // An exact optional consumer must not retire a replacement subscription.
        // Zero retains the registration-wide legacy lifecycle operation.
        internal void DisableRichRealization(LocalActorRegistration registration, ulong expectedLifetime)
        {
            if (registration == null) return;
            RichSubscription[] matches;
            lock (_richLock)
            {
                var list = new List<RichSubscription>();
                foreach (RichSubscription value in _richByLifetime.Values)
                    if (ReferenceEquals(value.Registration, registration) && !value.Retiring &&
                        (expectedLifetime == 0 || value.Lifetime == expectedLifetime))
                    {
                        value.Retiring = true;
                        list.Add(value);
                    }
                matches = list.ToArray();
            }
            foreach (RichSubscription value in matches) RetireRich(value);
        }

        internal bool IsRichRealizationDeliveryCurrent(LocalActorRegistration registration,
                                                       ulong lifetime)
        {
            if (registration == null || !registration.IsLive) return false;
            lock (_richLock)
            {
                return _richByLifetime.TryGetValue(lifetime, out RichSubscription subscription) &&
                       ReferenceEquals(subscription.Registration, registration) && !subscription.Retiring;
            }
        }

        internal bool TryGetDeliveredRichRealization(LocalActorRegistration registration,
            RichRealizationSnapshot snapshot, out RichRealizationMailboxEnvelope envelope,
            out string error)
        {
            envelope = null; error = null;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread ||
                registration == null || !registration.IsLive || snapshot == null)
                return ReceiverInputValidation.Fail("Rich preparation requires the live owner thread and snapshot.", out error);
            lock (_richLock)
            {
                if (!_richByLifetime.TryGetValue(snapshot.Header.ProducerLifetime, out RichSubscription subscription) ||
                    subscription.Retiring || !ReferenceEquals(subscription.Registration, registration) ||
                    subscription.LastDelivered == null ||
                    !ReferenceEquals(subscription.LastDelivered.Snapshot, snapshot) ||
                    subscription.LastDelivered.Reason != RichMailboxReason.NativeSnapshot ||
                    !subscription.LastDelivered.HasLocalReceiptInterval)
                    return ReceiverInputValidation.Fail("Only this registration's exact currently delivered native snapshot may prepare body work.", out error);
                var candidate = subscription.LastDelivered;
                double localNow = RichStopwatchSeconds();
                if (localNow < candidate.DeliverySeconds || subscription.Mapping == null ||
                    localNow >= subscription.Mapping.ExpiresAtSeconds ||
                    !candidate.LocalReceiptInterval.IsFreshAt(localNow, subscription.MaximumNativeAgeSeconds))
                    return ReceiverInputValidation.Fail("Delivered rich snapshot has exceeded its pinned clock calibration or original subscription age bound.", out error);
                envelope = candidate;
                return true;
            }
        }

        private bool TryCalibrateRich(RichRealizationSubscriptionOptions options,
                                      out SpatialBlendNativeClockMapping mapping, out string error)
        {
            mapping = null;
            error = null;
            double before = RichStopwatchSeconds();
            bool read = SpatialBlendAPI.TryReadRichMonotonicNanoseconds(out ulong ns, out ulong id,
                                                                        out ulong epoch, out error);
            double after = RichStopwatchSeconds();
            return read && SpatialBlendNativeClockMapping.TryCreate(id, epoch, ns, before, after,
                options.MaximumCalibrationIntervalSeconds,
                options.MaximumClockRateErrorPartsPerMillion,
                options.CalibrationLifetimeSeconds, out mapping, out error);
        }

        // May run on an arbitrary native callback thread. No Unity API or observer
        // executes here, and the managed routing lock is released before enqueue.
        private void OnRichSnapshot(RichRealizationSnapshot snapshot)
        {
            if (snapshot == null) return;
            RichSubscription subscription;
            lock (_richLock)
                _richByLifetime.TryGetValue(snapshot.Header.ProducerLifetime, out subscription);
            if (subscription == null || subscription.Retiring ||
                subscription.Registration.NativeSpace != snapshot.Header.ReceiverSpaceId ||
                subscription.Registration.NativeActor != snapshot.Header.ReceiverActorId)
                return;
            subscription.Mailbox.TryEnqueue(snapshot, RichStopwatchSeconds(), out _);
        }

        private void RetireRich(RichSubscription subscription)
        {
            // Keep this exact token routable until its terminal envelope drains. A
            // late callback can therefore never select a replacement subscription.
            ReleaseNativeLifetime(subscription);
            subscription.Mailbox.Retire(subscription.Lifetime, RichStopwatchSeconds(), out _);
        }

        private void DrainRichRealizations()
        {
            RichSubscription[] values;
            lock (_richLock)
                values = new List<RichSubscription>(_richByLifetime.Values).ToArray();
            foreach (RichSubscription value in values)
            {
                DeliverRichPending(value);
                double stopwatchSeconds = RichStopwatchSeconds();
                if (value.HasExpiryDeadline && !value.ExpiryQueued &&
                    stopwatchSeconds >= value.ExpiryDeadlineSeconds &&
                    value.Mailbox.TryExpireIfIdle(value.Lifetime, stopwatchSeconds))
                {
                    value.ExpiryQueued = true;
                    DeliverRichPending(value);
                }
            }
        }

        private void DeliverRichPending(RichSubscription subscription)
        {
            IReadOnlyList<RichRealizationMailboxEnvelope> envelopes =
                subscription.Mailbox.DrainAtCurrentTime(RichStopwatchSeconds);
            foreach (RichRealizationMailboxEnvelope envelope in envelopes)
            {
                bool terminal = IsTerminal(envelope.Reason);
                if (terminal) FinalizeTerminalBeforeDelivery(subscription);
                if (envelope.Reason == RichMailboxReason.NativeSnapshot && envelope.HasLocalReceiptInterval)
                {
                    // Earliest possible receipt is the conservative deadline; the
                    // mailbox's revision dedup means duplicate polls cannot move it.
                    subscription.HasExpiryDeadline = true;
                    subscription.ExpiryDeadlineSeconds =
                        envelope.LocalReceiptInterval.EarliestSeconds + subscription.MaximumNativeAgeSeconds;
                    subscription.ExpiryQueued = false;
                }
                else if (envelope.Reason != RichMailboxReason.NativeSnapshot)
                {
                    if (envelope.Reason == RichMailboxReason.NativeAgeExpired)
                    {
                        // This withdraws the delivered output once, but is not a
                        // producer retirement. A genuinely newer revision may use
                        // this still-valid calibration and establish a new deadline.
                        subscription.HasExpiryDeadline = true;
                        subscription.ExpiryQueued = true;
                    }
                    else
                    {
                        subscription.HasExpiryDeadline = false;
                        subscription.ExpiryQueued = IsTerminal(envelope.Reason);
                    }
                }
                // Publish before user notification so preparation from within a
                // callback sees this exact immutable native-owned copy. A later
                // observer must still recheck after another observer's reentry.
                lock (_richLock) subscription.LastDelivered =
                    envelope.Reason == RichMailboxReason.NativeSnapshot ? envelope : null;
                try { subscription.Deliver(envelope); }
                catch { /* A subscriber fault cannot strand another lifetime. */ }
            }
        }

        private RichSubscription FindRichSubscription(LocalActorRegistration registration)
        {
            lock (_richLock)
                foreach (RichSubscription value in _richByLifetime.Values)
                    if (ReferenceEquals(value.Registration, registration) && !value.Retiring) return value;
            return null;
        }

        private void RemoveRichSubscription(RichSubscription subscription)
        {
            long space = subscription.Registration.NativeSpace;
            bool unregister;
            lock (_richLock)
            {
                if (!_richByLifetime.TryGetValue(subscription.Lifetime, out RichSubscription current) ||
                    !ReferenceEquals(current, subscription)) return;
                _richByLifetime.Remove(subscription.Lifetime);
                unregister = true;
                foreach (RichSubscription remaining in _richByLifetime.Values)
                    if (remaining.Registration.NativeSpace == space)
                    {
                        unregister = false;
                        break;
                    }
                if (unregister) _richCallbackSpaces.Remove(space);
                if (_richByLifetime.Count == 0) _richAggregateCapacity = 0;
            }
            if (unregister) SpatialBlendAPI.TryUnregisterOnRichRealization(space, out _);
        }

        private void ReleaseNativeLifetime(RichSubscription subscription)
        {
            bool release;
            lock (_richLock)
            {
                release = !subscription.NativeReleased;
                if (release) subscription.NativeReleased = true;
            }
            if (!release) return;
            SpatialBlendAPI.TryRetireRichRealization(subscription.Lifetime, out _);
            SpatialBlendAPI.TryReleaseRichRealization(subscription.Lifetime, out _, out _);
        }

        private void FinalizeTerminalBeforeDelivery(RichSubscription subscription)
        {
            lock (_richLock) subscription.Retiring = true;
            // Retirement/release and callback removal happen before user code so a
            // release observer can synchronously acquire a distinct replacement.
            ReleaseNativeLifetime(subscription);
            RemoveRichSubscription(subscription);
        }

        private static void CleanupUnadmittedNativeLifetime(ulong lifetime)
        {
            if (lifetime == 0) return;
            SpatialBlendAPI.TryRetireRichRealization(lifetime, out _);
            SpatialBlendAPI.TryReleaseRichRealization(lifetime, out _, out _);
        }

        private static bool IsTerminal(RichMailboxReason reason)
        {
            return reason == RichMailboxReason.LocalRetirement ||
                   reason == RichMailboxReason.ClockBridgeRejected ||
                   reason == RichMailboxReason.QueueCapacityRejected ||
                   reason == RichMailboxReason.MalformedSnapshot ||
                   reason == RichMailboxReason.UnknownLifetime;
        }

        private static bool Valid(RichRealizationSubscriptionOptions options)
        {
            return options.MailboxCapacity > 0 && Finite(options.MaximumNativeAgeSeconds) &&
                   options.MaximumNativeAgeSeconds > 0 && Finite(options.MaximumCalibrationIntervalSeconds) &&
                   options.MaximumCalibrationIntervalSeconds >= 0 &&
                   Finite(options.MaximumClockRateErrorPartsPerMillion) &&
                   options.MaximumClockRateErrorPartsPerMillion >= 0 &&
                   Finite(options.CalibrationLifetimeSeconds) && options.CalibrationLifetimeSeconds > 0;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private double RichStopwatchSeconds()
        {
            Func<double> clock;
            lock (_richLock) clock = _testOnlyRichStopwatchClock;
            return clock != null ? clock() : (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }
}
