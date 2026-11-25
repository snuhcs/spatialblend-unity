// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Independent observer, not a second input sink. Subscribe/dispose on the owning
    /// thread. Disposal is idempotent and silent; writer/registration retirement emits
    /// one terminal receipt. No replay is delivered on subscription.
    /// </summary>
    public sealed class CommittedMotionObservationLease : IDisposable
    {
        private readonly SpatialBlendActor _owner;
        internal readonly LocalActorRegistration Registration;
        internal Action<CommittedMotionObservation> Handler;
        internal bool Retired;
        internal CommittedMotionObservationLease(SpatialBlendActor owner,
            LocalActorRegistration registration, CommittedMotionSource source,
            Action<CommittedMotionObservation> handler)
        { _owner = owner; Registration = registration; Source = source; Handler = handler; }
        public CommittedMotionSource Source { get; }
        public bool IsActive => _owner.IsOwnerThread && !Retired &&
            ReferenceEquals(Registration, _owner.Registration) && Registration.IsLive;
        public void Dispose() { if (_owner.IsOwnerThread) _owner.DisposeCommittedMotionLease(this); }
    }

    public sealed partial class SpatialBlendActor
    {
        private Guid _motionRegistrationId;
        private long _motionPublicationRevision;
        private readonly List<CommittedMotionObservationLease> _motionObservers =
            new List<CommittedMotionObservationLease>();
#if UNITY_EDITOR
        internal Func<double> TestOnlyMotionReceiptClock;
        internal Action TestOnlyBeforeMotionNativeAdmission;
#endif

        /// <summary>
        /// Observes future successful motion-input commits for the current exact writer.
        /// Reset keeps the subscription; writer replacement, legacy mutation, and actor
        /// retirement terminate it. Consumers must explicitly bind any receiver/proxy.
        /// </summary>
        public bool TrySubscribeCommittedMotion(Action<CommittedMotionObservation> observer,
            out CommittedMotionObservationLease lease)
        {
            lease = null;
            if (!Main() || _applying || !_isSource || !IsRegistered || _motionEpoch == 0 ||
                observer == null) return false;
            var source = new CommittedMotionSource(_motionRegistrationId, _registration.ActorId,
                _registration.OwnerObject.SpaceId, _registration.OwnerObject.ObjectId, _motionSinkLease);
            lease = new CommittedMotionObservationLease(this, _registration, source, observer);
            _motionObservers.Add(lease);
            return true;
        }

        internal void DisposeCommittedMotionLease(CommittedMotionObservationLease lease)
        {
            if (!Main() || lease.Retired) return;
            lease.Retired = true;
            lease.Handler = null;
            _motionObservers.Remove(lease);
        }

        private double MotionReceiptSeconds()
        {
#if UNITY_EDITOR
            if (TestOnlyMotionReceiptClock != null) return TestOnlyMotionReceiptClock();
#endif
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }

        private CommittedMotionObservation FrameObservation(CommittedMotionSource source,
            ulong epoch, AnchorPoseFrame frame, string rootName, double receipt)
        {
            var rows = new CommittedMotionAnchor[frame.anchors.Length];
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Length; ++i)
            {
                AnchorPoseSample sample = frame.anchors[i];
                Vector3 position = sample.anchorId == rootName
                    ? new Vector3(sample.worldPosition.x, 0f, sample.worldPosition.z)
                    : sample.worldPosition;
                rows[i] = new CommittedMotionAnchor(sample.anchorId, _roles[sample.anchorId],
                    position, sample.worldRotation.eulerAngles.y);
                present.Add(sample.anchorId);
            }
            var unavailable = new List<string>();
            foreach (SpatialBlendActorAnchor anchor in _anchors)
                if (!present.Contains(anchor.Name)) unavailable.Add(anchor.Name);
            return new CommittedMotionObservation(source, CommittedMotionObservationKind.Frame,
                epoch, frame.sequence, frame.timeSeconds, receipt, rows, unavailable.ToArray(),
                CommittedMotionRetirementReason.None);
        }

        private CommittedMotionObservation ControlObservation(CommittedMotionSource source,
            CommittedMotionObservationKind kind, ulong epoch, string cleared = null,
            CommittedMotionRetirementReason reason = CommittedMotionRetirementReason.None)
        {
            var unavailable = new List<string>();
            foreach (SpatialBlendActorAnchor anchor in _anchors)
                if (cleared == null || anchor.Name == cleared) unavailable.Add(anchor.Name);
            return new CommittedMotionObservation(source, kind, epoch, null, null, null,
                Array.Empty<CommittedMotionAnchor>(), unavailable.ToArray(), reason);
        }

        private void DeliverCommittedMotion(CommittedMotionObservationLease[] observers,
            CommittedMotionObservation observation, LocalActorRegistration registration,
            long writer, ulong epoch, long revision)
        {
            foreach (CommittedMotionObservationLease lease in observers)
            {
                // A newer frame/control from a callback supersedes this delivery,
                // even when it used the same writer and epoch.
                if (!Current(registration) || writer != _motionSinkLease || epoch != _motionEpoch ||
                    revision != _motionPublicationRevision) break;
                if (lease.Retired) continue;
                try { lease.Handler?.Invoke(observation); }
                catch { /* Observers cannot undo successful native admission. */ }
            }
        }

        // Detach before arbitrary callbacks, then deliver at a caller-selected safe
        // boundary. Terminal events keep their old immutable identity even if the
        // first callback registers a replacement or subscribes a new observer.
        private Action DetachCommittedMotion(CommittedMotionRetirementReason reason)
        {
            ++_motionPublicationRevision;
            Action sourceRetired = DetachSourceInteraction(reason);
            var observers = _motionObservers.ToArray();
            _motionObservers.Clear();
            if (observers.Length == 0) return sourceRetired;
            var terminal = ControlObservation(observers[0].Source,
                CommittedMotionObservationKind.Retired, _motionEpoch, reason: reason);
            var handlers = new List<Action<CommittedMotionObservation>>();
            foreach (CommittedMotionObservationLease lease in observers)
            {
                if (!lease.Retired && lease.Handler != null) handlers.Add(lease.Handler);
                lease.Retired = true; lease.Handler = null;
            }
            return () =>
            {
                sourceRetired();
                foreach (Action<CommittedMotionObservation> handler in handlers)
                    try { handler(terminal); } catch { }
            };
        }
    }
}
