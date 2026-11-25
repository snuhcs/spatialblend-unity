// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    public enum CommittedMotionObservationKind { Frame, Cleared, Reset, Retired }
    public enum CommittedMotionRetirementReason { None, WriterReplaced, RegistrationRetired, LegacyMutation }

    /// <summary>
    /// Local registration/writer identity, not authenticated person identity or
    /// physical provenance. Re-registering the same named actor changes RegistrationId.
    /// </summary>
    public sealed class CommittedMotionSource
    {
        internal CommittedMotionSource(Guid registrationId, string actorId, string spaceId,
            string objectId, long writerGeneration)
        {
            RegistrationId = registrationId; ActorId = actorId; SpaceId = spaceId;
            ObjectId = objectId; WriterGeneration = writerGeneration;
        }
        public Guid RegistrationId { get; }
        public string ActorId { get; }
        public string SpaceId { get; }
        public string ObjectId { get; }
        public long WriterGeneration { get; }
    }

    /// <summary>
    /// The Unity-coordinate position and yaw submitted to atomic native admission.
    /// Locomotion Root has Y=0; Body retains Y. Pitch/roll are not admitted by this API.
    /// A Body role or anchor name does not certify a physical pelvis or seat contact.
    /// </summary>
    public readonly struct CommittedMotionAnchor
    {
        internal CommittedMotionAnchor(string id, SpatialBlendActorAnchorRole role,
            Vector3 position, float yaw)
        { AnchorId = id; Role = role; WorldPosition = position; WorldYawDegrees = yaw; }
        public string AnchorId { get; }
        public SpatialBlendActorAnchorRole Role { get; }
        public Vector3 WorldPosition { get; }
        public float WorldYawDegrees { get; }
    }

    /// <summary>
    /// Immutable successful-admission receipt, never a realized-avatar readback.
    /// Frame availability is complete: absent registered anchors are UnavailableAnchorIds.
    /// Controls carry no sample sequence/time/receipt and cannot extend sample freshness.
    /// ReceiptTimeSeconds uses Stopwatch's monotonic clock at admission-attempt start,
    /// before row construction/native invocation. It includes that preparation and
    /// marshaling/native delay, but not unknown upstream sensor age.
    /// This stream alone establishes no calibration, expiry policy, proxy, or actuation.
    /// </summary>
    public sealed class CommittedMotionObservation
    {
        internal CommittedMotionObservation(CommittedMotionSource source,
            CommittedMotionObservationKind kind, ulong epoch, long? sequence,
            double? sourceTime, double? receiptTime, CommittedMotionAnchor[] anchors,
            string[] unavailable, CommittedMotionRetirementReason retirementReason)
        {
            Source = source; Kind = kind; Epoch = epoch; Sequence = sequence;
            SourceTimeSeconds = sourceTime; ReceiptTimeSeconds = receiptTime;
            Anchors = Array.AsReadOnly((CommittedMotionAnchor[])anchors.Clone());
            UnavailableAnchorIds = Array.AsReadOnly((string[])unavailable.Clone());
            RetirementReason = retirementReason;
        }
        public CommittedMotionSource Source { get; }
        public CommittedMotionObservationKind Kind { get; }
        public ulong Epoch { get; }
        public long? Sequence { get; }
        public double? SourceTimeSeconds { get; }
        public double? ReceiptTimeSeconds { get; }
        public IReadOnlyList<CommittedMotionAnchor> Anchors { get; }
        public IReadOnlyList<string> UnavailableAnchorIds { get; }
        public CommittedMotionRetirementReason RetirementReason { get; }
    }
}
