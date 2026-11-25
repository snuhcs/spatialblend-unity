// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Owner-created preparation for a future controller, NOT an admitted motion
    /// plan or a lease on animated DOFs. Retains the complete native-delivered
    /// rich channels and exact independently observed body, without projection.
    /// Revalidate on the Unity owner thread immediately before each use; expiry,
    /// native/source revision, body and scene changes can invalidate it at any time.
    /// </summary>
    public sealed class ReceiverBodyRealizationCheckpoint
    {
        public const RichRealizationValidityDomain RequiredSceneDomains =
            RichRealizationValidityDomain.Policy | RichRealizationValidityDomain.Tree |
            RichRealizationValidityDomain.Counterpart | RichRealizationValidityDomain.Support;
        private readonly ReceiverBodyInputBinding _owner;
        private readonly int _ownerThread;
        private int _withdrawn;
        internal ReceiverBodyRealizationCheckpoint(ReceiverBodyInputBinding owner,
            RichRealizationMailboxEnvelope delivery, ReceiverBodyFrame body,
            RichRealizationValidity validity, long preparedAt, long validUntil,
            long maximumAge)
        {
            _owner = owner; _ownerThread = Thread.CurrentThread.ManagedThreadId;
            Delivery = delivery; Body = body; PreparedValidity = validity;
            PreparedAtNativeNanoseconds = preparedAt;
            ValidUntilNativeNanoseconds = validUntil; MaximumNativeAgeNanoseconds = maximumAge;
        }
        public RichRealizationMailboxEnvelope Delivery { get; }
        public RichRealizationSnapshot Snapshot => Delivery.Snapshot;
        public ReceiverBodyFrame Body { get; }
        public ReceiverBodyProfile Profile => Body.Profile;
        public RichRealizationValidity PreparedValidity { get; }
        public long PreparedAtNativeNanoseconds { get; }
        public long ValidFromNativeNanoseconds => (long)Snapshot.Header.NativeSolveNanoseconds.Value;
        public long ValidUntilNativeNanoseconds { get; }
        public long MaximumNativeAgeNanoseconds { get; }
        // This is local terminal state, never a positive currentness certificate.
        public bool IsWithdrawn => Volatile.Read(ref _withdrawn) != 0;
        internal bool IsOwnedBy(ReceiverBodyInputBinding owner) => ReferenceEquals(_owner, owner);

        public bool TryRevalidate(out RichRealizationValidity currentValidity, out string error)
        {
            currentValidity = null;
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                return ReceiverInputValidation.Fail("Preparation revalidation requires its owner thread.", out error);
            if (IsWithdrawn || _owner == null || Body == null || Delivery == null)
                return ReceiverInputValidation.Fail("This preparation was withdrawn and cannot be revived.", out error);
            if (_owner.TryRevalidatePreparation(this, out currentValidity, out error)) return true;
            Volatile.Write(ref _withdrawn, 1);
            return false;
        }

        /// <summary>Withdraws ONLY this preparation; never clears observed body input or another checkpoint.</summary>
        public bool TryWithdraw(out string error)
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                return ReceiverInputValidation.Fail("Preparation withdrawal requires its owner thread.", out error);
            Volatile.Write(ref _withdrawn, 1); error = null; return true;
        }
    }

    public sealed partial class ReceiverBodyInputBinding
    {
        /// <summary>
        /// Joins the exact owner-delivered native rich snapshot to this current
        /// complete body, the separate input sidecar and all four scene validity domains. The age bound is in
        /// native integer nanoseconds, measured from ORIGINAL native receipt.
        /// Source observation and receiver-body sequences/times remain distinct.
        /// No provider certificate, source-body clock join or actuator admission
        /// is manufactured by this numeric/causal preparation operation.
        /// </summary>
        public bool TryPrepareRealization(RichRealizationSnapshot snapshot, long maximumNativeAgeNanoseconds,
            out ReceiverBodyRealizationCheckpoint checkpoint, out string error)
        {
            checkpoint = null;
            if (!Check(out error)) return false;
            if (maximumNativeAgeNanoseconds <= 0 || maximumNativeAgeNanoseconds > Profile.Input.MaximumAgeNanoseconds)
                return ReceiverInputValidation.Fail("Preparation needs an explicit positive native age bound within the receiver-input maximum.", out error);
            if (!TryCheckPreparation(snapshot, null, maximumNativeAgeNanoseconds, out var delivery,
                out var body, out var validity, out long now, out long until, out error)) return false;
            checkpoint = new ReceiverBodyRealizationCheckpoint(this, delivery, body, validity,
                now, until, maximumNativeAgeNanoseconds);
            return true;
        }

        internal bool TryRevalidatePreparation(ReceiverBodyRealizationCheckpoint checkpoint,
            out RichRealizationValidity validity, out string error)
        {
            validity = null;
            if (!Check(out error)) return false;
            if (checkpoint == null || checkpoint.Body == null || checkpoint.Delivery == null || checkpoint.IsWithdrawn ||
                !checkpoint.IsOwnedBy(this) || !ReferenceEquals(checkpoint.Profile, Profile))
                return ReceiverInputValidation.Fail("Preparation belongs to another exact body profile.", out error);
            if (!TryCheckPreparation(checkpoint.Snapshot, checkpoint.Body, checkpoint.MaximumNativeAgeNanoseconds,
                out var delivery, out _, out var checkedValidity, out long now, out long until, out error)) return false;
            if (!ReferenceEquals(delivery, checkpoint.Delivery) || now < checkpoint.PreparedAtNativeNanoseconds ||
                until != checkpoint.ValidUntilNativeNanoseconds)
                return ReceiverInputValidation.Fail("Preparation delivery/clock or its pinned expiry changed.", out error);
            validity = checkedValidity;
            return true;
        }

        private bool TryCheckPreparation(RichRealizationSnapshot snapshot, ReceiverBodyFrame expectedBody,
            long maximumAge, out RichRealizationMailboxEnvelope delivery, out ReceiverBodyFrame body,
            out RichRealizationValidity validity, out long now, out long until, out string error)
        {
            delivery = null; body = null; validity = null; now = until = 0;
            if (!_writer.Registration.World.TryGetDeliveredRichRealization(_writer.Registration,
                snapshot, out var delivered, out error)) return false;
            RichRealizationHeader h = snapshot.Header;
            if (h.SnapshotStatus != RichSnapshotStatus.Complete ||
                h.SourceProvenanceMode != RichSourceProvenanceMode.TimestampedObservation ||
                !h.SourceEpoch.Known || h.SourceEpoch.Value == 0 ||
                !h.SourceSequence.Known || h.SourceSequence.Value <= 0 ||
                !h.SourceObservationSequence.Known || h.SourceObservationSequence.Value < 0 ||
                !h.SourceTimeSeconds.Known || !h.SourceActorSpace.Known || h.SourceActorSpace.Value < 0 ||
                !h.SourceActorElement.Known || h.SourceActorElement.Value < 0 ||
                !h.RelaySourceSpace.Known || h.RelaySourceSpace.Value < 0 ||
                !h.NativeClockId.Known || !h.NativeClockEpoch.Known ||
                h.NativeClockId.Value != Profile.Input.ClockId || h.NativeClockEpoch.Value != Profile.Input.ClockEpoch ||
                !h.NativeReceiptNanoseconds.Known || h.NativeReceiptNanoseconds.Value == 0 ||
                !h.NativeSolveNanoseconds.Known || h.NativeSolveNanoseconds.Value > long.MaxValue)
                return ReceiverInputValidation.Fail("Preparation requires complete causal native output with original source sample and matching native clock identity.", out error);
            if (!TryGetBodyForRealization(snapshot, out var candidate, out error)) return false;
            if (expectedBody != null && !ReferenceEquals(candidate, expectedBody))
                return ReceiverInputValidation.Fail("Preparation cannot substitute a newer or equivalent body sample.", out error);
            if (!candidate.HasAllJointPoses)
                return ReceiverInputValidation.Fail("Preparation requires the entire declared body layout, without filling unavailable joints.", out error);
            foreach (ReceiverAnchorPose anchor in candidate.GeometricInput.Anchors)
                if (!anchor.Available)
                    return ReceiverInputValidation.Fail("Preparation requires all declared receiver anchors including independent locomotion root.", out error);
            if (candidate.AcquisitionUpperNanoseconds > (long)h.NativeSolveNanoseconds.Value)
                return ReceiverInputValidation.Fail("Body acquisition cannot follow the native solve it claims to supply.", out error);
            long receipt = (long)h.NativeReceiptNanoseconds.Value;
            long receiptExpiry = receipt > long.MaxValue - maximumAge ? long.MaxValue : receipt + maximumAge;
            until = Math.Min(candidate.GeometricInput.ValidUntilNanoseconds, receiptExpiry);
            if (!_writer.TryReadAcquisitionClock(out now, out error)) return false;
            if (now < (long)h.NativeSolveNanoseconds.Value || now >= until)
                return ReceiverInputValidation.Fail("Preparation is future-dated or expired in the original native clock.", out error);
            // Validity-v1 deliberately leaves ReceiverInput unchecked. Its
            // separately negotiated sidecar is checked before AND after this
            // scene query against the exact same immutable body/native revision.
            // These are bounded as-of-query reads, not a lock across later use.
            if (!SpatialBlendAPI.TryGetRichRealizationValidity(h.ProducerLifetime, h.Revision,
                ReceiverBodyRealizationCheckpoint.RequiredSceneDomains, out var comparison, out int result, out error)) return false;
            if (result != 1 || comparison == null || !comparison.Valid ||
                comparison.RequestedDomains != ReceiverBodyRealizationCheckpoint.RequiredSceneDomains ||
                comparison.CapturedPolicyRevision != h.PolicyRevision.Value ||
                comparison.CapturedTreeRevision != h.ReceiverTreeRevision.Value ||
                comparison.CapturedCounterpartRevision != h.CounterpartRevision.Value ||
                comparison.CapturedSupportRevision != h.SupportRevision.Value || h.KnownRevisionMask != 15)
                return ReceiverInputValidation.Fail("Preparation requires unchanged, known policy/tree/counterpart/support and receiver-input domains.", out error);
            if (!TryGetBodyForRealization(snapshot, out var bodyAfterScene, out error)) return false;
            if (!ReferenceEquals(candidate, bodyAfterScene))
                return ReceiverInputValidation.Fail("Observed body changed during scene comparison.", out error);
            // No user callbacks occur in these reads. Still fence owner teardown
            // and a reentrant replacement before returning any preparation data.
            if (!Check(out error)) return false;
            if (!ReferenceEquals(_current, candidate))
                return ReceiverInputValidation.Fail("Observed body changed while preparing work.", out error);
            if (!_writer.Registration.World.TryGetDeliveredRichRealization(_writer.Registration,
                    snapshot, out var currentDelivery, out error)) return false;
            if (!ReferenceEquals(currentDelivery, delivered))
                return ReceiverInputValidation.Fail("Native owner delivery changed while preparing body work.", out error);
            long beforeComparison = now;
            if (!_writer.TryReadAcquisitionClock(out now, out error)) return false;
            if (now < beforeComparison || now >= until)
                return ReceiverInputValidation.Fail("Native clock moved backwards or preparation expired during comparison.", out error);
            delivery = delivered; body = candidate; validity = comparison;
            return true;
        }
    }
}
