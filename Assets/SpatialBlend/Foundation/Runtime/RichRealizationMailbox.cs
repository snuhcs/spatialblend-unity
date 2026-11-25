// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend
{
    public enum RichMailboxReason
    {
        NativeSnapshot,
        NativeRelease,
        LocalRetirement,
        ClockBridgeRejected,
        NativeAgeExpired,
        QueueCapacityRejected,
        MalformedSnapshot,
        UnknownLifetime
    }

    /// <summary>Immutable result drained by the main-thread owner.</summary>
    public sealed class RichRealizationMailboxEnvelope
    {
        internal RichRealizationMailboxEnvelope(long space, long actor, ulong lifetime, RichRealizationSnapshot snapshot,
                                                RichMailboxReason reason,
                                                string error,
                                                double adapterIngressSeconds,
                                                SpatialBlendLocalTimeInterval interval,
                                                bool hasInterval,
                                                double deliverySeconds)
        {
            SpaceId = space; ActorId = actor; ProducerLifetime = lifetime; Snapshot = snapshot; Reason = reason; Error = error;
            AdapterIngressSeconds = adapterIngressSeconds;
            LocalReceiptInterval = interval; HasLocalReceiptInterval = hasInterval;
            DeliverySeconds = deliverySeconds;
        }
        public long SpaceId { get; } public long ActorId { get; } public ulong ProducerLifetime { get; }
        public RichRealizationSnapshot Snapshot { get; }
        public RichMailboxReason Reason { get; }
        public string Error { get; }
        public double AdapterIngressSeconds { get; }
        public SpatialBlendLocalTimeInterval LocalReceiptInterval { get; }
        public bool HasLocalReceiptInterval { get; }
        public double DeliverySeconds { get; }
    }

    /// <summary>
    /// Thread-safe, bounded handoff from native callback/poll threads to a
    /// caller-owned drain. It has no Unity API calls and never admits a token
    /// implicitly from received data.
    /// </summary>
    public sealed class RichRealizationMailbox
    {
        private sealed class Slot
        {
            internal long Space; internal long Actor; internal ulong Lifetime;
            internal ulong ClockId; internal ulong ClockEpoch; internal double MaxAge; internal SpatialBlendNativeClockMapping Mapping;
            internal ulong Revision; internal bool HasRevision; internal bool Retired;
            internal LinkedListNode<Pending> Node;
        }
        private sealed class Pending
        {
            internal Slot Slot; internal RichRealizationSnapshot Snapshot;
            internal RichMailboxReason Reason; internal string Error;
            internal double AdapterIngress; internal SpatialBlendLocalTimeInterval Interval;
            internal bool HasInterval;
        }

        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly Dictionary<ulong, Slot> _slots = new Dictionary<ulong, Slot>();
        private readonly LinkedList<Pending> _pending = new LinkedList<Pending>();

        public RichRealizationMailbox(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public bool TryAdmit(long space, long actor, ulong lifetime, ulong clockId,
                             ulong clockEpoch, double maximumNativeAgeSeconds, SpatialBlendNativeClockMapping mapping,
                             out string error)
        {
            error = null;
            if (lifetime == 0 || clockId == 0 || clockEpoch == 0 || mapping == null || mapping.ClockId != clockId || mapping.ClockEpoch != clockEpoch || !Finite(maximumNativeAgeSeconds) || maximumNativeAgeSeconds <= 0)
                return Fail("Admission requires a nonzero lifetime/clock identity and finite positive age bound.", out error);
            lock (_gate)
            {
                if (_slots.ContainsKey(lifetime)) return Fail("Lifetime is already admitted or awaiting retirement drain.", out error);
                foreach (Slot existing in _slots.Values) if (existing.Space == space && existing.Actor == actor) return Fail("Actor has an active or undrained retirement slot.", out error);
                if (_slots.Count >= _capacity) return Fail("Mailbox slot capacity is exhausted.", out error);
                _slots.Add(lifetime, new Slot { Space = space, Actor = actor, Lifetime = lifetime,
                    ClockId = clockId, ClockEpoch = clockEpoch, MaxAge = maximumNativeAgeSeconds, Mapping = mapping });
                return true;
            }
        }

        public bool TryEnqueue(RichRealizationSnapshot snapshot, double adapterIngressSeconds, out string error)
        {
            error = null;
            if (snapshot == null || !Finite(adapterIngressSeconds) || adapterIngressSeconds < 0)
                return Fail("Snapshot and finite adapter ingress are required.", out error);
            RichRealizationHeader h = snapshot.Header;
            lock (_gate)
            {
                Slot slot;
                if (!_slots.TryGetValue(h.ProducerLifetime, out slot) || slot.Retired)
                    return Fail("Unknown or retired producer lifetime; replacement state is untouched.", out error);
                if (slot.Space != h.ReceiverSpaceId || slot.Actor != h.ReceiverActorId)
                    return QueueRelease(slot, RichMailboxReason.MalformedSnapshot, "Receiver identity does not match admitted slot.", adapterIngressSeconds, out error);
                if (slot.HasRevision && h.Revision <= slot.Revision) return true; // exact/older dedup; no freshness refresh.
                slot.HasRevision = true; slot.Revision = h.Revision;
                if (h.SnapshotStatus != RichSnapshotStatus.Complete)
                    return Queue(slot, snapshot, RichMailboxReason.NativeRelease, null, adapterIngressSeconds, default(SpatialBlendLocalTimeInterval), false);
                SpatialBlendLocalTimeInterval interval;
                string mapError = null;
                if (!h.NativeClockId.Known || !h.NativeClockEpoch.Known || !h.NativeReceiptNanoseconds.Known ||
                    h.NativeClockId.Value != slot.ClockId || h.NativeClockEpoch.Value != slot.ClockEpoch ||
                    !slot.Mapping.TryMap(h.NativeClockId.Value, h.NativeClockEpoch.Value,
                    h.NativeReceiptNanoseconds.Value, adapterIngressSeconds, out interval, out mapError))
                    return QueueRelease(slot, RichMailboxReason.ClockBridgeRejected, mapError ?? "No matched native clock bridge.", adapterIngressSeconds, out error);
                if (!interval.IsFreshAt(adapterIngressSeconds, slot.MaxAge))
                    return QueueRelease(slot, RichMailboxReason.NativeAgeExpired, "Native receipt exceeds admitted age.", adapterIngressSeconds, out error);
                return Queue(slot, snapshot, RichMailboxReason.NativeSnapshot, null, adapterIngressSeconds, interval, true);
            }
        }

        public bool Retire(ulong lifetime, double adapterIngressSeconds, out string error)
        {
            error = null;
            if (!Finite(adapterIngressSeconds) || adapterIngressSeconds < 0) return Fail("Finite retirement receipt required.", out error);
            lock (_gate)
            {
                Slot slot;
                if (!_slots.TryGetValue(lifetime, out slot) || slot.Retired) return Fail("Unknown lifetime cannot retire a replacement.", out error);
                slot.Retired = true;
                return Queue(slot, null, RichMailboxReason.LocalRetirement, null, adapterIngressSeconds, default(SpatialBlendLocalTimeInterval), false);
            }
        }

        public bool Reject(ulong lifetime, RichMailboxReason reason, string message, double adapterIngressSeconds, out string error)
        {
            error = null;
            if (!Finite(adapterIngressSeconds) || adapterIngressSeconds < 0 ||
                (reason != RichMailboxReason.ClockBridgeRejected && reason != RichMailboxReason.NativeAgeExpired &&
                 reason != RichMailboxReason.QueueCapacityRejected && reason != RichMailboxReason.MalformedSnapshot && reason != RichMailboxReason.UnknownLifetime))
                return Fail("A finite local rejection reason is required.", out error);
            lock (_gate) { Slot slot; if (!_slots.TryGetValue(lifetime, out slot) || slot.Retired) return Fail("Unknown lifetime cannot clear a replacement.", out error); return QueueRelease(slot, reason, message ?? "Local rejection.", adapterIngressSeconds, out error); }
        }

        public IReadOnlyList<RichRealizationMailboxEnvelope> Drain(double deliverySeconds)
        {
            if (!Finite(deliverySeconds) || deliverySeconds < 0) throw new ArgumentOutOfRangeException(nameof(deliverySeconds));
            var result = new List<RichRealizationMailboxEnvelope>();
            lock (_gate)
            {
                for (var check = _pending.First; check != null; check = check.Next)
                    if (deliverySeconds < check.Value.AdapterIngress)
                        throw new ArgumentOutOfRangeException(nameof(deliverySeconds));
                while (_pending.First != null)
                {
                    Pending p = _pending.First.Value; _pending.RemoveFirst(); p.Slot.Node = null;
                    if (p.Reason == RichMailboxReason.NativeSnapshot && !p.Interval.IsFreshAt(deliverySeconds, p.Slot.MaxAge))
                        result.Add(new RichRealizationMailboxEnvelope(p.Slot.Space, p.Slot.Actor, p.Slot.Lifetime, null, RichMailboxReason.NativeAgeExpired, "Native receipt expired before drain.", p.AdapterIngress, p.Interval, true, deliverySeconds));
                    else result.Add(new RichRealizationMailboxEnvelope(p.Slot.Space, p.Slot.Actor, p.Slot.Lifetime, p.Snapshot, p.Reason, p.Error, p.AdapterIngress, p.Interval, p.HasInterval, deliverySeconds));
                    if (p.Slot.Retired) _slots.Remove(p.Slot.Lifetime);
                }
            }
            return result;
        }

        // The production owner samples its clock while holding the queue lock.
        // A callback cannot insert a newer ingress between the sample and batch
        // extraction. Keep Drain(double) strict for explicitly timestamped callers.
        internal IReadOnlyList<RichRealizationMailboxEnvelope> DrainAtCurrentTime(Func<double> clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            lock (_gate) return Drain(clock());
        }

        // Expiring a previously delivered output must not overwrite a newer
        // callback queued while the owner was notifying its observers.
        internal bool TryExpireIfIdle(ulong lifetime, double ingressSeconds)
        {
            if (!Finite(ingressSeconds) || ingressSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(ingressSeconds));
            lock (_gate)
            {
                if (!_slots.TryGetValue(lifetime, out Slot slot) || slot.Retired || slot.Node != null)
                    return false;
                return Queue(slot, null, RichMailboxReason.NativeAgeExpired,
                    "No newer rich completion arrived before its native-age deadline.",
                    ingressSeconds, default(SpatialBlendLocalTimeInterval), false);
            }
        }

        private bool QueueRelease(Slot slot, RichMailboxReason reason, string message, double ingress, out string error)
        { error = message; Queue(slot, null, reason, message, ingress, default(SpatialBlendLocalTimeInterval), false); return false; }
        private bool Queue(Slot slot, RichRealizationSnapshot snapshot, RichMailboxReason reason, string error, double ingress, SpatialBlendLocalTimeInterval interval, bool hasInterval)
        {
            var pending = new Pending { Slot = slot, Snapshot = snapshot, Reason = reason, Error = error, AdapterIngress = ingress, Interval = interval, HasInterval = hasInterval };
            if (slot.Node != null) { slot.Node.Value = pending; return true; }
            slot.Node = _pending.AddLast(pending); return true;
        }
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
