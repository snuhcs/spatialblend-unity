// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Owner-thread observer of one existing writer; never a second input sink.
    /// No replay. Dispose is silent; writer/registration/legacy retirement is terminal.
    /// Reset retains the lease but revokes evidence. No actuation is authorized here.
    /// </summary>
    public sealed class CommittedSourceInteractionObservationLease : IDisposable
    {
        private readonly SpatialBlendActor _owner;
        internal readonly LocalActorRegistration Registration;
        internal Action<CommittedSourceInteractionObservation> Handler;
        internal CommittedSourceInteractionObservation LastDelivered;
        internal bool Retired;
        internal CommittedSourceInteractionObservationLease(SpatialBlendActor owner, LocalActorRegistration registration,
            CommittedMotionSource source, Action<CommittedSourceInteractionObservation> handler)
        { _owner = owner; Registration = registration; Source = source; Handler = handler; }
        public CommittedMotionSource Source { get; }
        public bool IsActive => _owner.IsOwnerThread && !Retired && Registration.IsLive && ReferenceEquals(Registration, _owner.Registration);
        /// <summary>
        /// Validate the exact latest receipt delivered by this lease against current
        /// writer, frame, scene and ORIGINAL native admission age. This is an as-of-call
        /// source cue check, not receiver/contact authority or a sensor freshness claim.
        /// </summary>
        public bool TryRevalidate(CommittedSourceInteractionObservation observation, long maximumAgeNanoseconds,
            out SourceInteractionEvidenceStatus status)
        {
            status = _owner.ValidateSourceInteractionLease(this, observation, maximumAgeNanoseconds);
            return status == SourceInteractionEvidenceStatus.Available;
        }
        public void Dispose() { if (_owner.IsOwnerThread) _owner.DisposeSourceInteractionLease(this); }
    }

    public sealed partial class SpatialBlendActor
    {
        private readonly List<CommittedSourceInteractionObservationLease> _sourceInteractionObservers =
            new List<CommittedSourceInteractionObservationLease>();
        private LocalActorRegistration _sourceEvidenceRegistration, _sourceDisableRegistration;
        private ulong _sourceEvidenceEpoch, _sourceDisableEpoch;

        public bool TrySubscribeCommittedSourceInteraction(Action<CommittedSourceInteractionObservation> observer,
            out CommittedSourceInteractionObservationLease lease)
        {
            lease = null;
            if (!Main() || _applying || !_isSource || !IsRegistered || _motionEpoch == 0 || observer == null) return false;
            FlushSourceEvidenceDisable();
            if (_sourceDisableRegistration != null) return false; // No new lease over a pending old disable.
            if (_sourceInteractionObservers.Count == 0 &&
                EnableSourceEvidence(_registration, _motionEpoch) != SourceInteractionEvidenceStatus.Available) return false;
            var source = new CommittedMotionSource(_motionRegistrationId, _registration.ActorId,
                _registration.OwnerObject.SpaceId, _registration.OwnerObject.ObjectId, _motionSinkLease);
            lease = new CommittedSourceInteractionObservationLease(this, _registration, source, observer);
            _sourceInteractionObservers.Add(lease);
            return true;
        }

        private SourceInteractionEvidenceStatus EnableSourceEvidence(LocalActorRegistration registration, ulong epoch)
        {
            if (ReferenceEquals(_sourceEvidenceRegistration, registration) && _sourceEvidenceEpoch == epoch)
                return SourceInteractionEvidenceStatus.Available;
            var result = registration.World.TryEnableSourceEvidence(registration, epoch, true);
            if (result == SourceInteractionEvidenceStatus.Available)
            { _sourceEvidenceRegistration = registration; _sourceEvidenceEpoch = epoch; }
            return result;
        }
        private void PrepareSourceInteractionFrame(LocalActorRegistration registration, ulong epoch)
        {
            FlushSourceEvidenceDisable();
            if (_sourceInteractionObservers.Count != 0 && _sourceDisableRegistration == null)
                EnableSourceEvidence(registration, epoch);
        }
        // BeginEpoch already revokes native capture. Retain subscriptions after a
        // replay reset, enabling only future frames in the new epoch.
        private void ResetSourceInteractionEpoch()
        {
            _sourceEvidenceRegistration = null; _sourceEvidenceEpoch = 0;
            _sourceDisableRegistration = null; _sourceDisableEpoch = 0;
        }
        private void StopSourceEvidence()
        {
            if (_sourceEvidenceRegistration != null)
            {
                _sourceDisableRegistration = _sourceEvidenceRegistration; _sourceDisableEpoch = _sourceEvidenceEpoch;
                _sourceEvidenceRegistration = null; _sourceEvidenceEpoch = 0;
            }
            FlushSourceEvidenceDisable();
        }
        private void FlushSourceEvidenceDisable()
        {
            if (_sourceDisableRegistration == null || !Main()) return;
            var result = _sourceDisableRegistration.World.TryEnableSourceEvidence(_sourceDisableRegistration, _sourceDisableEpoch, false);
            if (result == SourceInteractionEvidenceStatus.Busy) return;
            // Unavailable includes an already retired/replaced exact native epoch.
            _sourceDisableRegistration = null; _sourceDisableEpoch = 0;
        }
        private void Update()
        {
            // Only a pending nonblocking cleanup; no polling for evidence or new input.
            if (!_applying && _sourceDisableRegistration != null) FlushSourceEvidenceDisable();
        }
        internal void DisposeSourceInteractionLease(CommittedSourceInteractionObservationLease lease)
        {
            if (!Main() || lease.Retired) return;
            lease.Retired = true; lease.Handler = null; lease.LastDelivered = null;
            _sourceInteractionObservers.Remove(lease);
            if (_sourceInteractionObservers.Count == 0) StopSourceEvidence();
        }
        internal SourceInteractionEvidenceStatus ValidateSourceInteractionLease(CommittedSourceInteractionObservationLease lease,
            CommittedSourceInteractionObservation observation, long age)
        {
            if (!Main() || _applying || lease.Retired || !Current(lease.Registration) ||
                lease.Source.WriterGeneration != _motionSinkLease || !ReferenceEquals(lease.LastDelivered, observation) ||
                observation == null || observation.Revision != _motionPublicationRevision ||
                observation.Input.Epoch != _motionEpoch || observation.Input.Sequence != _lastSequence ||
                observation.Status != SourceInteractionEvidenceStatus.Available || observation.Evidence == null)
                return SourceInteractionEvidenceStatus.Unavailable;
            return lease.Registration.World.TryValidateSourceEvidence(lease.Registration, observation.Evidence, age);
        }

        private void DeliverSourceInteraction(CommittedSourceInteractionObservationLease[] observers,
            CommittedMotionObservation input, SourceInteractionEvidence evidence, SourceInteractionEvidenceStatus status,
            LocalActorRegistration registration, long writer, ulong epoch, long revision)
        {
            foreach (var lease in observers)
            {
                if (!Current(registration) || writer != _motionSinkLease || epoch != _motionEpoch || revision != _motionPublicationRevision) break;
                if (lease.Retired) continue;
                var currentStatus = status;
                if (evidence != null)
                    currentStatus = registration.World.TryValidateSourceEvidence(registration, evidence, SpatialBlendAPI.SourceEvidenceMaximumAgeNs);
                var value = new CommittedSourceInteractionObservation(input,
                    currentStatus == SourceInteractionEvidenceStatus.Available ? evidence : null, currentStatus, revision);
                lease.LastDelivered = value;
                try { lease.Handler?.Invoke(value); } catch { /* Observers cannot undo native admission. */ }
            }
        }
        private void DeliverSourceInteractionControl(CommittedSourceInteractionObservationLease[] observers,
            CommittedMotionObservationKind kind, LocalActorRegistration registration, long writer, ulong epoch, long revision, string cleared = null)
        {
            if (observers.Length == 0) return;
            DeliverSourceInteraction(observers, ControlObservation(observers[0].Source, kind, epoch, cleared), null,
                SourceInteractionEvidenceStatus.Unavailable, registration, writer, epoch, revision);
        }
        private Action DetachSourceInteraction(CommittedMotionRetirementReason reason)
        {
            var observers = _sourceInteractionObservers.ToArray(); _sourceInteractionObservers.Clear();
            StopSourceEvidence();
            if (observers.Length == 0) return () => { };
            var value = new CommittedSourceInteractionObservation(ControlObservation(observers[0].Source,
                CommittedMotionObservationKind.Retired, _motionEpoch, reason: reason), null,
                SourceInteractionEvidenceStatus.Unavailable, _motionPublicationRevision);
            var handlers = new List<Action<CommittedSourceInteractionObservation>>();
            foreach (var lease in observers)
            {
                if (!lease.Retired && lease.Handler != null) handlers.Add(lease.Handler);
                lease.Retired = true; lease.Handler = null; lease.LastDelivered = null;
            }
            return () => { foreach (var handler in handlers) try { handler(value); } catch { } };
        }
    }
}
