// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// The driver's whole interface to the relation core (design A*, sections 5.2 and 5.5, ABI 4). Everything
    /// the app knows about the native ABI sits behind this one adapter, so the binding is the only thing that
    /// changes with the ABI.
    ///
    /// The driver is the sole writer of the receiver's embodied poses. It publishes the pose of every anchor it can
    /// sample, every frame, and the core samples them in its own source-triggered solve. The core holds no pose of
    /// its own and no pacing: it publishes one proposal per relation-bearing anchor, which the driver reads, and it
    /// queues events (source exceptions, teleport requests), which the driver polls. The course query is stateless
    /// geometry.
    /// </summary>
    internal interface IEmbodiedCore
    {
        /// <summary>The core's id of an anchor of <paramref name="actor"/>, interned on first use. 0 is the root.</summary>
        bool TryGetAnchorId(long actor, string anchorName, out uint anchorId);

        /// <summary>The name of an anchor id of <paramref name="actor"/>.</summary>
        bool TryGetAnchorName(long actor, uint anchorId, out string anchorName);

        /// <summary>
        /// Publishes the pose of one anchor of <paramref name="actor"/>. <paramref name="sequence"/> increases by one
        /// per publish within an epoch of that anchor and restarts at 1 in a new one. <paramref name="epoch"/> changes
        /// when the driver placed or reset the anchor, and <paramref name="teleported"/> says the driver placed it in
        /// the frame this pose reports.
        /// </summary>
        void PublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double timeSeconds, ulong sequence,
                               ulong epoch, bool teleported);

        /// <summary>Reads the core's latest proposal set for <paramref name="actor"/> into <paramref name="proposals"/>. False when there is none to read.</summary>
        bool TryReadProposals(long actor, ProposalSet proposals);

        /// <summary>Pops the oldest pending event of <paramref name="actor"/>. False when there is none.</summary>
        bool TryPollEvent(long actor, out ActorEvent actorEvent);

        /// <summary>The course between two ground points for <paramref name="actor"/>; see <see cref="IEmbodiedCourse"/>.</summary>
        bool TryQueryCourse(long actor, Vector2 from, Vector2 to, List<Vector2> waypoints);

        // ---- Embodiment registration (core C ABI 4.7). Additive: a core without it
        //      answers Unsupported and the driver goes on publishing the anchors it always did.

        /// <summary>
        /// Registers the embodiment (skeleton, anchors, offsets with provenance, optional chain) of <paramref name="actor"/> from its
        /// JSON spec. From then on the bone-bound anchors are derived by the core from the bones the driver publishes
        /// (<see cref="PublishBonePose"/>), and publishing such an anchor's own pose is refused. Once per actor.
        /// <paramref name="error"/> carries the core's own text when the result is not Registered.
        /// </summary>
        EmbodimentRegistrationResult RegisterEmbodiment(long actor, string specJson, out string error);

        /// <summary>The id of a bone of the registered skeleton.</summary>
        bool TryGetBoneId(long actor, string boneName, out int boneId);

        /// <summary>
        /// Publishes the pose of one bone (core world, the registered bone frame). The clock arguments are those of
        /// <see cref="PublishAnchorPose"/>, per bone.
        /// </summary>
        void PublishBonePose(long actor, int boneId, in EmbodiedBonePose pose, double timeSeconds, ulong sequence, ulong epoch,
                             bool teleported);

        /// <summary>The registration's arithmetic, anchor = bone * offset. False when <paramref name="anchorName"/> is not a registered bone-bound anchor.</summary>
        bool TryAnchorPoseFromBone(long actor, string anchorName, in EmbodiedBonePose bone, out EmbodiedBonePose anchor);

        /// <summary>The registration's inverse, bone = anchor * offset^-1: the bone pose that places the anchor at <paramref name="anchor"/>.</summary>
        bool TryBonePoseFromAnchor(long actor, string anchorName, in EmbodiedBonePose anchor, out EmbodiedBonePose bone);
    }

    /// <summary>
    /// Explicit ABI 4.13 capability. Legacy drivers do not subscribe. Exactly one consumer per actor owns the stream and
    /// previous/current native-accepted sample correspondences, admitting at most one newer publication per anchor between
    /// completed drains. Only <see cref="ActorEventPollResult.NativeEmpty"/> can fence a successful complete drain before
    /// previous-sample retirement. Errors preserve incomplete-drain state and both samples; do not accept a third sample.
    /// A boolean legacy poll is not that fence. Capability/layout admission alone does not prove continuous-yaw measurement.
    /// </summary>
    internal interface IEmbodiedTargetEventCore : IEmbodiedCore
    {
        bool TrySetTargetEventDelivery(long actor, bool enabled, out string error);
        ActorEventPollResult PollEvent(long actor, out ActorEvent actorEvent, out ActorEventPollFailure failure);

        /// <summary>True only when native accepted this exact publication; refusal must not advance accepted-sample state.</summary>
        bool TryPublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double timeSeconds, ulong sequence,
                                  ulong epoch, bool teleported);

        /// <summary>
        /// Publishes committed continuous Unity world yaw in degrees without float narrowing or turn wrapping. This yaw
        /// overrides pose.YawDegrees; position and optional pitch still come from pose. On true, retain the returned exact
        /// native Z-up yaw (radians) alongside the committed Unity yaw and stamp. On false, do not advance accepted state;
        /// the output describes only an attempted submission, not an accepted sample.
        /// </summary>
        bool TryPublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double continuousUnityYawDegrees,
                                  double timeSeconds, ulong sequence, ulong epoch, bool teleported,
                                  out double submittedNativeYawRadians);

        bool TryPublishBonePose(long actor, int boneId, in EmbodiedBonePose pose, double timeSeconds, ulong sequence,
                                ulong epoch, bool teleported);
    }

    internal enum ActorEventPollResult
    {
        // Default is not a successful drain.
        NativeError = 0,
        Event,
        NativeEmpty,
        ConversionError
    }

    /// <summary>
    /// Detached evidence of a failed poll. Conversion failure preserves the popped native envelope (including its opaque
    /// bytes); a later empty poll cannot repair or erase it. The consumer must retain this failure and its correspondences.
    /// </summary>
    internal sealed class ActorEventPollFailure
    {
        internal readonly int? NativeResult;
        internal readonly string Error;
        internal readonly IReadOnlyList<byte> RejectedEventBytes;
        internal readonly int PayloadBufferLength;

        internal ActorEventPollFailure(int? nativeResult, string error, byte[] rejectedEventBytes = null,
                                       int payloadBufferLength = 0)
        {
            NativeResult = nativeResult;
            Error = error;
            RejectedEventBytes = rejectedEventBytes == null ? null : Array.AsReadOnly((byte[])rejectedEventBytes.Clone());
            PayloadBufferLength = payloadBufferLength;
        }
    }

    [Flags]
    internal enum TargetChannels : uint
    {
        None = 0,
        Position = 1,
        Yaw = 2
    }

    [Flags]
    internal enum TargetRequestFlags : uint
    {
        None = 0,
        Placement = 1
    }

    /// <summary>
    /// Immutable ABI 4.13 request, detached from later snapshots. Native Z-up radians remain double precision here:
    /// sample identity and signed turn must not pass through the legacy float pose/proposal fields. The consumer resolves
    /// ordinary yaw against its accepted correspondence before converting to its persistent realization coordinates;
    /// placement instead uses TargetYawRadians as an absolute orientation. A release needs only Channels.
    /// </summary>
    internal readonly struct TargetRequest
    {
        internal readonly TargetChannels Channels;
        internal readonly ulong SampleEpoch;
        internal readonly ulong SampleSequence;
        internal readonly double SampleYawRadians;
        internal readonly double YawTurnRadians;
        internal readonly AnchorProposalFlags ProposalFlags;
        internal readonly EmbodiedCause ProposalCause;
        internal readonly double FacingYawRadians;
        internal readonly EmbodiedStatus ProposalStatus;
        internal readonly TargetRequestFlags RequestFlags;
        internal readonly Vector3 Position;
        internal readonly int OrientationType;
        internal readonly double TargetYawRadians;
        internal readonly double TargetPitchRadians;
        internal readonly double TargetRollRadians;

        internal bool Placement => (RequestFlags & TargetRequestFlags.Placement) != 0;

        internal TargetRequest(TargetChannels channels, ulong sampleEpoch, ulong sampleSequence, double sampleYawRadians,
                               double yawTurnRadians, AnchorProposalFlags proposalFlags, EmbodiedCause proposalCause,
                               double facingYawRadians, EmbodiedStatus proposalStatus, TargetRequestFlags requestFlags,
                               Vector3 position, int orientationType, double targetYawRadians, double targetPitchRadians,
                               double targetRollRadians)
        {
            Channels = channels;
            SampleEpoch = sampleEpoch;
            SampleSequence = sampleSequence;
            SampleYawRadians = sampleYawRadians;
            YawTurnRadians = yawTurnRadians;
            ProposalFlags = proposalFlags;
            ProposalCause = proposalCause;
            FacingYawRadians = facingYawRadians;
            ProposalStatus = proposalStatus;
            RequestFlags = requestFlags;
            Position = position;
            OrientationType = orientationType;
            TargetYawRadians = targetYawRadians;
            TargetPitchRadians = targetPitchRadians;
            TargetRollRadians = targetRollRadians;
        }
    }

    /// <summary>
    /// The head pitch the way v1 carried it: Unity's <c>eulerAngles.x</c> of the head, sent as the source's pitch and
    /// applied to the receiver's head as the same <c>eulerAngles.x</c>, at once and uninterpolated
    /// (<c>SbEntity.UpdateStates</c> and <c>SbNetworkAvatarEntity.AvatarOnUpdateStateBatch</c> in v1).
    /// </summary>
    internal static class HeadOrientation
    {
        /// <summary>The pitch to publish for a head with this rotation, in degrees.</summary>
        internal static float PitchDegrees(Quaternion rotation) => rotation.eulerAngles.x;

        // The wire carries radians, and a pitch past 180 degrees is a negative pitch (looking up): the head's
        // eulerAngles.x of 350 is -10. The receiver writes the value back as an euler angle, so the round trip is the
        // same rotation.
        internal static double PitchRadiansFromDegrees(float degrees) => Mathf.DeltaAngle(0f, degrees) * Math.PI / 180.0;

        internal static float PitchDegreesFromRadians(double radians) => (float)(radians * 180.0 / Math.PI);

        /// <summary>The yaw to publish for a head with this rotation, in degrees.</summary>
        internal static float YawDegrees(Quaternion rotation) => rotation.eulerAngles.y;
    }
}
