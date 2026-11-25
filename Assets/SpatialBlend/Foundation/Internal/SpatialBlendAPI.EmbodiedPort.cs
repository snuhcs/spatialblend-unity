// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// Mirrors <c>sb_anchor_pose_t</c> in <c>spatialblend_type.h</c> (ABI 4). 72 bytes. The pose of one anchor of a
    /// receiver, published by its driver, which is the only writer of it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbAnchorPose
    {
        public uint struct_size;
        public uint flags;
        public double x;
        public double y;
        public double z;
        public SbOrientation orientation;
        public ulong epoch;
    }

    /// <summary>
    /// Mirrors <c>sb_anchor_proposal_t</c> (ABI 4). 144 bytes: the proposal of ONE relation-bearing anchor of a receiver.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbAnchorProposal
    {
        public uint struct_size;
        public uint anchor_id;
        public int status;
        public uint flags;
        public uint cause;
        public uint reserved;
        public double x;
        public double y;
        public double z;
        public SbOrientation orientation;
        public double err_spacing_target;
        public double err_spacing_sample;
        public double err_facing_target;
        public double err_facing_sample;
        public double err_pose_target;
        public double err_pose_sample;
        public double err_scale_m;
        public long referent_shared_id;
    }

    /// <summary>
    /// Mirrors <c>sb_anchor_solve_t</c> (ABI 4.11). 80 bytes: the inner level of ONE anchor's proposal (<c>sb_read_anchor_solve</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbAnchorSolve
    {
        public uint struct_size;
        public uint anchor_id;
        public uint flags;
        public int iterations;
        public long object_shared_id;
        public double sd_scale_m;
        public double objective_j;
        public double facing_yaw;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] driving_pose_tag;
    }

    /// <summary>
    /// Mirrors <c>sb_anchor_turn_t</c> (ABI 4.12). 32 bytes: the signed, unwrapped yaw turn of ONE anchor's latest proposal
    /// (<c>sb_read_anchor_turn</c>). <c>yaw</c> repeats the proposal's orientation.yaw (the driver checks it against the proposal it
    /// pairs the turn with); <c>yaw_turn</c> (radians) is the turn from the sampled yaw: sampled yaw + yaw_turn = yaw, modulo 2 pi.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbAnchorTurn
    {
        public uint struct_size;
        public uint anchor_id;
        public uint flags;
        public uint reserved;
        public double yaw;
        public double yaw_turn;
    }

    /// <summary>
    /// Mirrors <c>sb_anchor_chart_t</c> (ABI 4.14). 88 bytes: the replay through the chart of ONE anchor of the latest proposal set
    /// (<c>sb_read_anchor_chart</c>): the source anchor's own sample in the frame of the containment-transfer leaf that holds it,
    /// mapped through this space's counterpart of the leaf (Z-up, metres, radians). <c>orientation.yaw</c> is mapped through the
    /// leaf; pitch and roll are the source's pass-through. <c>revision</c> is the proposal set it belongs to.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbAnchorChart
    {
        public uint struct_size;
        public uint anchor_id;
        public uint flags;
        public uint reserved;
        public long leaf_shared_id;
        public double x;
        public double y;
        public double z;
        public SbOrientation orientation;
        public ulong revision;
    }

    /// <summary>Mirrors <c>sb_proposal_set_t</c> (ABI 4). 64 bytes: the header of one proposal set.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbProposalSet
    {
        public uint struct_size;
        public uint anchor_count;
        public uint total_anchor_count;
        public int sample_valid;
        public ulong revision;
        public long source_sequence;
        public ulong sample_sequence;
        public ulong sample_epoch;
        public double sample_timestamp_s;
        public double sample_age_s;
    }

    /// <summary>
    /// Mirrors <c>sb_actor_event_t</c> (ABI 4). 360 bytes: a source-app exception or core's request to teleport.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbActorEvent
    {
        public uint struct_size;
        public int kind;
        public ulong revision;
        public long source_sequence;
        public long source_shared_id;
        public uint exception_id;
        public uint payload_size;
        public int interaction_kind;
        public uint anchor_id;
        public double x;
        public double y;
        public double z;
        public SbOrientation orientation;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = (int)SpatialBlendAPI.ExceptionPayloadMax)]
        public byte[] payload;
    }

    /// <summary>Mirrors <c>sb_target_request_payload_t</c> (ABI 4.13). Native-layout/endian bytes inside the unchanged event.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbTargetRequestPayload
    {
        public uint struct_size;
        public uint channels;
        public ulong sample_epoch;
        public ulong sample_sequence;
        public double sample_yaw;
        public double yaw_turn;
        public uint proposal_flags;
        public uint proposal_cause;
        public double facing_yaw;
        public int proposal_status;
        public uint request_flags;
    }

    /// <summary>Mirrors <c>sb_orientation_t</c> (ABI 3.1). 32 bytes. Yaw, pitch and roll in radians, Z-up.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbOrientation
    {
        public uint struct_size;
        public int type;
        public double yaw;
        public double pitch;
        public double roll;
    }

    /// <summary>Mirrors <c>sb_orientation_type_t</c>: which of yaw, pitch and roll a source feed carries.</summary>
    internal enum SbOrientationType : int
    {
        Yaw = 0,
        YawPitch = 1,
        YawPitchRoll = 2
    }

    internal static partial class SpatialBlendAPI
    {
        /// <summary><c>SB_EMBODIED_TELEPORTED</c>: the driver placed the body, it did not walk.</summary>
        internal const uint EmbodiedTeleported = 0x1u;

        /// <summary><c>SB_EXCEPTION_PAYLOAD_MAX</c>: the capacity of an exception's opaque payload, bytes.</summary>
        internal const uint ExceptionPayloadMax = 256u;

        /// <summary><c>SB_EXCEPTION_JOIN</c>: the exception id the first update of every source actor carries.</summary>
        internal const uint ExceptionJoin = 1u;

        /// <summary>Additional admission for typed target events; the legacy snapshot port still requires only ABI 4.12.</summary>
        internal const uint TargetEventAbiMinor = 13u;
        internal const uint TargetRequestPayloadSize = 64u;

        /// <summary>The proposals a driver reads with one call. Larger sets are read again with a larger buffer.</summary>
        internal const int InitialProposalCapacity = 8;

        private static bool _warnedMissingEmbodiedPort;

        private static class OrientationRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern long sb_update_actor_pose(long space, long actor, double x, double y, ref SbOrientation orientation);
        }

        private static class OrientationDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern long sb_update_actor_pose(long space, long actor, double x, double y, ref SbOrientation orientation);
        }

        private static bool _actorPoseUnavailable;

        /// <summary>
        /// A source actor's state with its head's yaw and pitch (ABI 3.1, <c>sb_update_actor_pose</c>). The pitch rides to every
        /// receiver's proposal as a pass-through: the core never solves it. Falls back to the yaw-only update on a core
        /// without it. Unity space in, Z-up radians out.
        /// </summary>
        internal static long UpdateActorPose(long space, long actor, Vector3 unityPosition,
                                                    float unityYawDegrees, float headPitchDegrees)
        {
            if (!_actorPoseUnavailable)
            {
                CoordinateConverter.UnityToSpatialBlend2D(unityPosition, out double x, out double y);
                var orientation = new SbOrientation
                {
                    struct_size = (uint)Marshal.SizeOf<SbOrientation>(),
                    type = (int)SbOrientationType.YawPitch,
                    yaw = CoordinateConverter.UnityToSpatialBlendAngle(unityYawDegrees) * Math.PI / 180.0,
                    pitch = HeadOrientation.PitchRadiansFromDegrees(headPitchDegrees),
                    roll = 0.0,
                };
                try
                {
                    return useDebugLibrary ? OrientationDebug.sb_update_actor_pose(space, actor, x, y, ref orientation)
                                           : OrientationRelease.sb_update_actor_pose(space, actor, x, y, ref orientation);
                }
                catch (EntryPointNotFoundException)
                {
                    _actorPoseUnavailable = true;
                }
            }
            return UpdateActorState(space, actor, unityPosition, unityYawDegrees);
        }

        private static class EmbodiedPortRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_anchor_id(long space, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, out uint id);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_anchor_name(long space, long actor, uint anchorId,
                [Out] byte[] buffer, int capacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_publish_anchor_pose(long space, long actor, uint anchorId, ref SbAnchorPose pose,
                double timestampSeconds, ulong sequence);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_proposals(long space, long actor, ref SbProposalSet set,
                [In, Out] SbAnchorProposal[] proposals, int capacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_anchor_solve(long space, long actor, uint anchorId, ref SbAnchorSolve solve);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_anchor_turn(long space, long actor, uint anchorId, ref SbAnchorTurn turn);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_anchor_chart(long space, long actor, uint anchorId, ref SbAnchorChart chart);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_raise_source_exception(long space, long sourceActor, uint exceptionId,
                byte[] payload, uint payloadSize);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_poll_actor_event(long space, long actor, ref SbActorEvent actorEvent);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_target_event_delivery(long space, long actor, int enabled);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_query_course(long space, long actor, double fromX, double fromY,
                double toX, double toY, [Out] double[] xyOut, int capacity);
        }

        private static class EmbodiedPortDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_anchor_id(long space, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, out uint id);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_anchor_name(long space, long actor, uint anchorId,
                [Out] byte[] buffer, int capacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_publish_anchor_pose(long space, long actor, uint anchorId, ref SbAnchorPose pose,
                double timestampSeconds, ulong sequence);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_proposals(long space, long actor, ref SbProposalSet set,
                [In, Out] SbAnchorProposal[] proposals, int capacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_anchor_solve(long space, long actor, uint anchorId, ref SbAnchorSolve solve);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_anchor_turn(long space, long actor, uint anchorId, ref SbAnchorTurn turn);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_read_anchor_chart(long space, long actor, uint anchorId, ref SbAnchorChart chart);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_raise_source_exception(long space, long sourceActor, uint exceptionId,
                byte[] payload, uint payloadSize);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_poll_actor_event(long space, long actor, ref SbActorEvent actorEvent);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_target_event_delivery(long space, long actor, int enabled);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_query_course(long space, long actor, double fromX, double fromY,
                double toX, double toY, [Out] double[] xyOut, int capacity);
        }

        // Field names in declaration order, exactly the order sb_get_struct_field_offset indexes them.
        internal static readonly string[] AnchorPoseFields =
        {
            "struct_size", "flags", "x", "y", "z", "orientation", "epoch"
        };

        internal static readonly string[] AnchorProposalFields =
        {
            "struct_size", "anchor_id", "status", "flags", "cause", "reserved", "x", "y", "z", "orientation",
            "err_spacing_target", "err_spacing_sample", "err_facing_target", "err_facing_sample",
            "err_pose_target", "err_pose_sample", "err_scale_m", "referent_shared_id"
        };

        internal static readonly string[] ProposalSetFields =
        {
            "struct_size", "anchor_count", "total_anchor_count", "sample_valid", "revision", "source_sequence",
            "sample_sequence", "sample_epoch", "sample_timestamp_s", "sample_age_s"
        };

        internal static readonly string[] AnchorSolveFields =
        {
            "struct_size", "anchor_id", "flags", "iterations", "object_shared_id", "sd_scale_m", "objective_j", "facing_yaw",
            "driving_pose_tag"
        };

        internal static readonly string[] AnchorTurnFields =
        {
            "struct_size", "anchor_id", "flags", "reserved", "yaw", "yaw_turn"
        };

        internal static readonly string[] ActorEventFields =
        {
            "struct_size", "kind", "revision", "source_sequence", "source_shared_id", "exception_id", "payload_size",
            "interaction_kind", "anchor_id", "x", "y", "z", "orientation", "payload"
        };

        internal static readonly string[] TargetRequestPayloadFields =
        {
            "struct_size", "channels", "sample_epoch", "sample_sequence", "sample_yaw", "yaw_turn", "proposal_flags",
            "proposal_cause", "facing_yaw", "proposal_status", "request_flags"
        };

        /// <summary>
        /// Additional, non-enabling admission for the explicit target-event capability. Check the descriptor, unchanged
        /// event/orientation envelope, and every payload field before calling the subscription entry point. No cache may
        /// carry admission across a release/debug library selection. Legacy callers need not request this capability.
        /// </summary>
        internal static bool TryCheckTargetEventPort(out string error)
        {
            error = null;
            try
            {
                var mismatches = GetTargetEventAbiMismatches(GetNativeAbiLayout());
                CheckStruct(SbStructId.ActorEvent, typeof(SbActorEvent), ActorEventFields, mismatches);
                CheckStruct(SbStructId.Orientation, typeof(SbOrientation),
                    new[] { "struct_size", "type", "yaw", "pitch", "roll" }, mismatches);
                CheckStruct(SbStructId.TargetRequestPayload, typeof(SbTargetRequestPayload), TargetRequestPayloadFields,
                    mismatches);
                if (mismatches.Count == 0) return true;
                error = "The target-event ABI differs from this binding: " + string.Join("; ", mismatches) + ".";
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException ||
                                               exception is DllNotFoundException || exception is BadImageFormatException ||
                                               exception is InvalidOperationException)
            {
                error = "The target-event ABI is unavailable: " + exception.Message;
            }
            return false;
        }

        internal static List<string> GetTargetEventAbiMismatches(SbV2AbiLayout native)
        {
            List<string> mismatches = GetAbiLayoutMismatches(native);
            if (native.abi_minor < TargetEventAbiMinor)
                mismatches.Add($"target-event abi_minor: native={native.abi_minor} required>={TargetEventAbiMinor}");
            return mismatches;
        }

        /// <summary>
        /// Subscriber-lifetime operation, disabled by default. Exactly one consumer owns each actor's stream and accepted
        /// sample history. Refusal leaves native subscription/queue unchanged; disable does not discard historical events.
        /// This does not establish that a producer measures continuous realized yaw, nor is it correspondence recovery.
        /// </summary>
        internal static bool TrySetTargetEventDelivery(long space, long actor, bool enabled, out string error)
        {
            if (!TryCheckTargetEventPort(out error)) return false;
            try
            {
                int result = useDebugLibrary
                    ? EmbodiedPortDebug.sb_set_target_event_delivery(space, actor, enabled ? 1 : 0)
                    : EmbodiedPortRelease.sb_set_target_event_delivery(space, actor, enabled ? 1 : 0);
                if (result == 0) return true;
                error = $"sb_set_target_event_delivery returned {result}: invalid actor/space or refused legacy backlog.";
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException ||
                                               exception is DllNotFoundException || exception is BadImageFormatException)
            {
                error = "Target-event delivery is unavailable: " + exception.Message;
            }
            return false;
        }

        /// <summary>
        /// True when the loaded library exposes the per-anchor port (ABI 4) with the layouts this binding marshals:
        /// <c>sb_sizeof</c> and every field offset of the four ABI 4 structs must equal the managed structs, or a
        /// publish, a read or a poll would corrupt memory. A library that fails this has nothing to fall back to:
        /// the core proposes only through this port. The failure is logged as an error once and returned in
        /// <paramref name="error"/> so the caller can surface it.
        /// </summary>
        internal static bool TryCheckEmbodiedPort(out string error)
        {
            error = null;
            try
            {
                var mismatches = new List<string>();
                CheckStruct(SbStructId.AnchorPose, typeof(SbAnchorPose), AnchorPoseFields, mismatches);
                CheckStruct(SbStructId.AnchorProposal, typeof(SbAnchorProposal), AnchorProposalFields, mismatches);
                CheckStruct(SbStructId.ProposalSet, typeof(SbProposalSet), ProposalSetFields, mismatches);
                CheckStruct(SbStructId.ActorEvent, typeof(SbActorEvent), ActorEventFields, mismatches);
                CheckStruct(SbStructId.AnchorSolve, typeof(SbAnchorSolve), AnchorSolveFields, mismatches);
                CheckStruct(SbStructId.AnchorTurn, typeof(SbAnchorTurn), AnchorTurnFields, mismatches);
                if (mismatches.Count > 0)
                {
                    error = "The anchor-port layouts differ from the managed binding: " + string.Join("; ", mismatches) + ".";
                    return ReportEmbodiedPortFailure(error);
                }
                // The entry points must exist too: an unknown space is refused with -1, which is all this probes.
                var probe = new SbProposalSet { struct_size = (uint)Marshal.SizeOf<SbProposalSet>() };
                var probeProposals = new SbAnchorProposal[1];
                if (useDebugLibrary) EmbodiedPortDebug.sb_read_proposals(0, 0, ref probe, probeProposals, 0);
                else EmbodiedPortRelease.sb_read_proposals(0, 0, ref probe, probeProposals, 0);
                return true;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = "The loaded native library has no per-anchor port (sb_publish_anchor_pose / " +
                        "sb_read_proposals, ABI 4): " + exception.Message;
                return ReportEmbodiedPortFailure(error);
            }
        }

        private static bool ReportEmbodiedPortFailure(string error)
        {
            if (!_warnedMissingEmbodiedPort)
            {
                _warnedMissingEmbodiedPort = true;
                Debug.LogError("[SpatialBlend] " + error + " Received avatars cannot be driven and will not move.");
            }
            return false;
        }

        /// <summary>
        /// The id of an anchor of <paramref name="actor"/>, interning it on first use. Id 0 is the actor's root anchor.
        /// False for an invalid actor or a library without the entry point.
        /// </summary>
        internal static bool TryGetAnchorId(long space, long actor, string anchorName, out uint id)
        {
            id = 0;
            try
            {
                return (useDebugLibrary
                    ? EmbodiedPortDebug.sb_get_anchor_id(space, actor, anchorName, out id)
                    : EmbodiedPortRelease.sb_get_anchor_id(space, actor, anchorName, out id)) == 0;
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                return false;
            }
        }

        /// <summary>The name of an anchor id of <paramref name="actor"/>. False for an invalid actor or id.</summary>
        internal static bool TryGetAnchorName(long space, long actor, uint anchorId, out string name)
        {
            name = null;
            var buffer = new byte[256];
            int length;
            try
            {
                length = useDebugLibrary
                    ? EmbodiedPortDebug.sb_get_anchor_name(space, actor, anchorId, buffer, buffer.Length)
                    : EmbodiedPortRelease.sb_get_anchor_name(space, actor, anchorId, buffer, buffer.Length);
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                return false;
            }
            if (length < 0) return false;
            name = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length - 1));
            return true;
        }

        /// <summary>
        /// Publishes the driver's pose of one anchor (Unity space). Wait-free in the core. False when the core refused
        /// it: an unknown actor or anchor, a non-finite pose, or a stale sequence (not newer within the epoch).
        /// </summary>
        internal static bool TryPublishAnchorPose(long space, long actor, uint anchorId, in EmbodiedPose pose,
                                                  double timestampSeconds, ulong sequence, ulong epoch,
                                                  bool teleported)
        {
            SbAnchorPose native = ToNativeAnchorPose(pose, epoch, teleported);
            return TryPublishNativeAnchorPose(space, actor, anchorId, ref native, timestampSeconds, sequence);
        }

        /// <summary>
        /// Explicit continuous-yaw path: the double Unity yaw overrides the pose's legacy float yaw. The output is copied
        /// from the exact native submission, not recomputed by the caller; retain it with committed yaw only on true.
        /// Non-finite yaw is refused before any native call. No accepted-sample storage or producer admission occurs here.
        /// </summary>
        internal static bool TryPublishAnchorPose(long space, long actor, uint anchorId, in EmbodiedPose pose,
                                                  double continuousUnityYawDegrees, double timestampSeconds,
                                                  ulong sequence, ulong epoch, bool teleported,
                                                  out double submittedNativeYawRadians)
        {
            submittedNativeYawRadians = double.NaN;
            if (!IsFinite(continuousUnityYawDegrees)) return false;
            SbAnchorPose native = ToNativeAnchorPose(pose, continuousUnityYawDegrees, epoch, teleported);
            submittedNativeYawRadians = native.orientation.yaw;
            if (!IsFinite(submittedNativeYawRadians)) return false;
            return TryPublishNativeAnchorPose(space, actor, anchorId, ref native, timestampSeconds, sequence);
        }

        private static bool TryPublishNativeAnchorPose(long space, long actor, uint anchorId, ref SbAnchorPose native,
                                                       double timestampSeconds, ulong sequence)
        {
            try
            {
                return (useDebugLibrary
                    ? EmbodiedPortDebug.sb_publish_anchor_pose(space, actor, anchorId, ref native, timestampSeconds, sequence)
                    : EmbodiedPortRelease.sb_publish_anchor_pose(space, actor, anchorId, ref native, timestampSeconds, sequence)) == 0;
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                return false;
            }
        }

        /// <summary>Unity pose in, <c>sb_anchor_pose_t</c> out (Z-up radians; the height is the anchor's own).</summary>
        internal static SbAnchorPose ToNativeAnchorPose(in EmbodiedPose pose, ulong epoch, bool teleported)
        {
            CoordinateConverter.UnityToSpatialBlend3D(pose.Position, out double x, out double y, out double z);
            return new SbAnchorPose
            {
                struct_size = (uint)Marshal.SizeOf<SbAnchorPose>(),
                flags = teleported ? EmbodiedTeleported : 0u,
                x = x,
                y = y,
                z = z,
                orientation = new SbOrientation
                {
                    struct_size = (uint)Marshal.SizeOf<SbOrientation>(),
                    type = (int)(pose.HasPitch ? SbOrientationType.YawPitch : SbOrientationType.Yaw),
                    yaw = CoordinateConverter.UnityToSpatialBlendAngle(pose.YawDegrees) * Math.PI / 180.0,
                    pitch = pose.HasPitch ? HeadOrientation.PitchRadiansFromDegrees(pose.PitchDegrees) : 0.0,
                    roll = 0.0,
                },
                epoch = epoch,
            };
        }

        /// <summary>Same position/pitch/epoch encoding, with an authoritative double continuous Unity yaw override.</summary>
        internal static SbAnchorPose ToNativeAnchorPose(in EmbodiedPose pose, double continuousUnityYawDegrees,
                                                        ulong epoch, bool teleported)
        {
            SbAnchorPose native = ToNativeAnchorPose(pose, epoch, teleported);
            native.orientation.yaw = CoordinateConverter.UnityToSpatialBlendAngle(continuousUnityYawDegrees) * (Math.PI / 180.0);
            return native;
        }

        /// <summary>
        /// Reads the core's latest proposal set for <paramref name="actor"/>: its header and one proposal per
        /// relation-bearing anchor, the root first, converted to the driver's types. A set larger than the scratch
        /// buffer is read again with one that holds it (the core's set is replaced whole, so each read is consistent).
        /// </summary>
        internal static bool TryReadProposals(long space, long actor, ref SbAnchorProposal[] scratch, ProposalSet into)
        {
            into.Clear();
            if (scratch == null || scratch.Length == 0) scratch = new SbAnchorProposal[InitialProposalCapacity];
            for (int attempt = 0; attempt < 2; ++attempt)
            {
                for (int i = 0; i < scratch.Length; ++i)
                {
                    scratch[i] = new SbAnchorProposal { struct_size = (uint)Marshal.SizeOf<SbAnchorProposal>() };
                }
                var header = new SbProposalSet { struct_size = (uint)Marshal.SizeOf<SbProposalSet>() };
                int result;
                try
                {
                    result = useDebugLibrary
                        ? EmbodiedPortDebug.sb_read_proposals(space, actor, ref header, scratch, scratch.Length)
                        : EmbodiedPortRelease.sb_read_proposals(space, actor, ref header, scratch, scratch.Length);
                }
                catch (EntryPointNotFoundException exception)
                {
                    ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                    return false;
                }
                if (result != 0) return false;
                if (header.total_anchor_count > scratch.Length && attempt == 0)
                {
                    scratch = new SbAnchorProposal[header.total_anchor_count];
                    continue;
                }
                bool converted = TryConvertProposalSet(header, scratch, into);
                if (converted) AttachYawTurns(space, actor, scratch, (int)Math.Min(header.anchor_count, (uint)scratch.Length), into);
                if (converted) AttachAnchorCharts(space, actor, into);
                return converted;
            }
            return false;
        }

        /// <summary>
        /// ABI 4.12: pairs each proposal that carries a target and a specified yaw with the signed yaw turn the core read for it
        /// (<c>sb_read_anchor_turn</c>). The turn is a second call, so it is accepted only when it answers THE proposal just read (the
        /// same flags and the same absolute yaw, see <see cref="TryConvertAnchorTurn"/>); a failed call, an absent anchor or a proposal
        /// that was replaced in between leaves the turn absent, and the driver then takes the shortest arc to the absolute yaw.
        /// </summary>
        private static void AttachYawTurns(long space, long actor, SbAnchorProposal[] natives, int nativeCount, ProposalSet set)
        {
            for (int i = 0; i < set.Anchors.Count; ++i)
            {
                AnchorProposal proposal = set.Anchors[i];
                if (!proposal.CarriesTarget || proposal.YawUnspecified) continue;
                int index = Array.FindIndex(natives, 0, nativeCount, native => native.anchor_id == proposal.AnchorId);
                if (index < 0) continue;
                var turn = new SbAnchorTurn { struct_size = (uint)Marshal.SizeOf<SbAnchorTurn>() };
                int result;
                try
                {
                    result = useDebugLibrary
                        ? EmbodiedPortDebug.sb_read_anchor_turn(space, actor, proposal.AnchorId, ref turn)
                        : EmbodiedPortRelease.sb_read_anchor_turn(space, actor, proposal.AnchorId, ref turn);
                }
                catch (EntryPointNotFoundException exception)
                {
                    ReportEmbodiedPortFailure("The anchor turn is missing: " + exception.Message);
                    return;
                }
                if (result != 1 || !TryConvertAnchorTurn(turn, natives[index], out float degrees)) continue;
                proposal.YawTurnDegrees = degrees;
                proposal.HasYawTurn = true;
                set.Anchors[i] = proposal;
            }
        }

        // A library before ABI 4.14 has no sb_read_anchor_chart: the proposals are read without charts (as before), said once.
        private static bool _anchorChartUnavailable;

        /// <summary>
        /// ABI 4.14: pairs each anchor of the set just read with its replay through the chart
        /// (<c>sb_read_anchor_chart</c>), the separate output beside the Spacing position and the Facing yaw that the DRIVER composes:
        /// present while the source anchor is inside a containment-transfer leaf this space maps. It is a second call, so it is
        /// accepted only for the set just read (the same revision, <see cref="TryConvertAnchorChart"/>).
        /// </summary>
        private static void AttachAnchorCharts(long space, long actor, ProposalSet set)
        {
            if (_anchorChartUnavailable) return;
            if (AttachAnchorCharts(set, space, actor, NativeAnchorChartRead)) return;
            _anchorChartUnavailable = true;
            Debug.LogWarning("[SpatialBlend] The native library has no sb_read_anchor_chart (ABI 4.14): anchors near a shared object are " +
                             "not replayed through its chart (a received avatar's raw body follows its Sb head).");
        }

        /// <summary>One <c>sb_read_anchor_chart</c> call: 1 written, 0 no chart, -1 refused.</summary>
        internal delegate int AnchorChartRead(long space, long actor, uint anchorId, ref SbAnchorChart chart);

        // The native read, one cached non-capturing delegate (no allocation per frame).
        private static readonly AnchorChartRead NativeAnchorChartRead = ReadNativeAnchorChart;
        private static int ReadNativeAnchorChart(long space, long actor, uint anchorId, ref SbAnchorChart chart) => useDebugLibrary
            ? EmbodiedPortDebug.sb_read_anchor_chart(space, actor, anchorId, ref chart)
            : EmbodiedPortRelease.sb_read_anchor_chart(space, actor, anchorId, ref chart);

        /// <summary>
        /// Pairs every anchor of <paramref name="set"/> with its chart through <paramref name="read"/>: a chart of the set's revision is
        /// converted onto the proposal; one that answered another revision (the set was replaced between the two calls) marks the
        /// proposal <see cref="AnchorProposal.ChartStale"/> (unknown, not absent). False when the entry point is missing (nothing read).
        /// </summary>
        internal static bool AttachAnchorCharts(ProposalSet set, long space, long actor, AnchorChartRead read)
        {
            for (int i = 0; i < set.Anchors.Count; ++i)
            {
                AnchorProposal proposal = set.Anchors[i];
                var chart = new SbAnchorChart { struct_size = (uint)Marshal.SizeOf<SbAnchorChart>() };
                int result;
                try { result = read(space, actor, proposal.AnchorId, ref chart); }
                catch (EntryPointNotFoundException) { return false; }
                if (result != 1) continue;
                if (chart.anchor_id == proposal.AnchorId && chart.revision != set.Revision) proposal.ChartStale = true;
                else if (!TryConvertAnchorChart(chart, proposal.AnchorId, set.Revision, ref proposal)) continue;
                set.Anchors[i] = proposal;
            }
            return true;
        }

        /// <summary>
        /// Converts one anchor's chart (Z-up, radians) into the proposal (Unity space, degrees) when it answers the proposal of
        /// <paramref name="anchorId"/> in the set of <paramref name="revision"/> and is finite. The yaw follows the same Z-up to Unity
        /// map as every other yaw (<see cref="CoordinateConverter"/>), so a leaf turned by a quarter on the receiver turns the chart
        /// yaw by the same quarter in Unity's sense.
        /// </summary>
        internal static bool TryConvertAnchorChart(in SbAnchorChart chart, uint anchorId, ulong revision, ref AnchorProposal proposal)
        {
            if (chart.anchor_id != anchorId || chart.revision != revision || !IsFinite(chart.x) || !IsFinite(chart.y) ||
                !IsFinite(chart.z) || !IsFinite(chart.orientation.yaw))
            {
                return false;
            }
            proposal.HasChart = true;
            proposal.ChartLeafSharedId = chart.leaf_shared_id;
            proposal.ChartVerified = (chart.flags & AnchorChartFlagVerified) != 0;
            proposal.ChartPosition = CoordinateConverter.SpatialBlendToUnity3D(chart.x, chart.y, chart.z);
            proposal.ChartYawDegrees = RadiansToUnityYawDegrees(chart.orientation.yaw);
            return true;
        }

        /// <summary><c>SB_ANCHOR_CHART_FLAG_VERIFIED</c>: the source's footprint of the leaf rode the update and is congruent with this space's.</summary>
        internal const uint AnchorChartFlagVerified = 1u << 0;

        /// <summary>
        /// The signed turn in Unity degrees (the sign flips with the Z-up to Unity yaw map), when <paramref name="turn"/> answers
        /// <paramref name="proposal"/>: the same anchor, the same flags, the same absolute yaw (the struct repeats it), and a finite turn.
        /// </summary>
        internal static bool TryConvertAnchorTurn(in SbAnchorTurn turn, in SbAnchorProposal proposal, out float unityTurnDegrees)
        {
            unityTurnDegrees = 0f;
            if (turn.anchor_id != proposal.anchor_id || turn.flags != proposal.flags ||
                !IsFinite(turn.yaw) || !IsFinite(turn.yaw_turn) || turn.yaw != proposal.orientation.yaw)
            {
                return false;
            }
            unityTurnDegrees = (float)(-turn.yaw_turn * 180.0 / Math.PI);
            return true;
        }

        /// <summary>
        /// Converts a native set to the driver's types (Unity space, degrees). A proposal with a status the binding does
        /// not know, or a non-finite target on a status that carries one, is reported and left out: that relation fails
        /// loudly, and the others in the set are used.
        /// </summary>
        internal static bool TryConvertProposalSet(in SbProposalSet header, SbAnchorProposal[] proposals, ProposalSet into)
        {
            into.Clear();
            into.Revision = header.revision;
            into.SourceSequence = header.source_sequence;
            into.SampleValid = header.sample_valid != 0;
            into.TotalAnchorCount = header.total_anchor_count;
            uint count = Math.Min(header.anchor_count, (uint)(proposals?.Length ?? 0));
            for (int i = 0; i < count; ++i)
            {
                if (!TryConvertAnchorProposal(proposals[i], out AnchorProposal converted))
                {
                    // This relation's proposal is unusable: say so, and go on. Dropping the whole set (as this did) held back
                    // every other anchor and, because the events are polled only after a readable set, every event with it.
                    ReportUnreadableProposal(proposals[i]);
                    continue;
                }
                into.Anchors.Add(converted);
            }
            return true;
        }

        private static readonly System.Collections.Generic.HashSet<(uint Anchor, int Status)> _reportedUnreadable =
            new System.Collections.Generic.HashSet<(uint, int)>();

        // Loud, once per (anchor, status): an unknown status means the binding and the library disagree.
        private static void ReportUnreadableProposal(in SbAnchorProposal native)
        {
            if (!_reportedUnreadable.Add((native.anchor_id, (int)native.status))) return;
            Debug.LogError($"[SpatialBlend] The proposal for anchor {native.anchor_id} has status {native.status}, which this binding does " +
                           "not know (or a non-finite target): that relation is not followed. The other anchors and the events go on.");
        }

        /// <summary>
        /// Converts one anchor proposal to the driver's types (Unity space, degrees). False for a status the binding does
        /// not know and for a non-finite target on a status that carries one.
        /// </summary>
        internal static bool TryConvertAnchorProposal(in SbAnchorProposal native, out AnchorProposal proposal)
        {
            proposal = default;
            if (!Enum.IsDefined(typeof(EmbodiedStatus), native.status))
            {
                return false;
            }
            var status = (EmbodiedStatus)native.status;
            proposal.AnchorId = native.anchor_id;
            proposal.Status = status;
            proposal.Flags = (AnchorProposalFlags)native.flags;
            proposal.Cause = (EmbodiedCause)native.cause;
            proposal.ReferentSharedId = native.referent_shared_id;
            proposal.NoPositionRelation = native.err_spacing_target < 0 && native.err_pose_target < 0;
            bool carriesTarget = status == EmbodiedStatus.Exact || status == EmbodiedStatus.Inexact;
            if (!carriesTarget)
            {
                return true;
            }
            if (!IsFinite(native.x) || !IsFinite(native.y) || !IsFinite(native.z) || !IsFinite(native.orientation.yaw))
            {
                return false;
            }

            proposal.Position = CoordinateConverter.SpatialBlendToUnity3D(native.x, native.y, native.z);
            proposal.YawDegrees = RadiansToUnityYawDegrees(native.orientation.yaw);
            if (native.orientation.type >= (int)SbOrientationType.YawPitch && IsFinite(native.orientation.pitch))
            {
                proposal.HasPitch = true;
                proposal.PitchDegrees = HeadOrientation.PitchDegreesFromRadians(native.orientation.pitch);
            }
            return true;
        }

        /// <summary>
        /// ABI 4.11: the inner level of one anchor's latest proposal: the object whose affordance poses the anchor is solved among, the
        /// SD scale, J, the iterations, the tag of the driving pose and, with <see cref="AnchorProposalFlags.FacingYaw"/>, the direction
        /// (Unity yaw, degrees) the head and upper body follow. False when the latest set has no such anchor or the actor is unknown.
        /// The driver does not need it to realize a proposal; it is for what follows a seated or INNER anchor (the upper body's facing).
        /// </summary>
        internal static bool TryReadAnchorSolve(long space, long actor, uint anchorId, out AnchorSolve solve)
        {
            solve = default;
            var native = new SbAnchorSolve
            {
                struct_size = (uint)Marshal.SizeOf<SbAnchorSolve>(),
                driving_pose_tag = new byte[32],
            };
            int result;
            try
            {
                result = useDebugLibrary
                    ? EmbodiedPortDebug.sb_read_anchor_solve(space, actor, anchorId, ref native)
                    : EmbodiedPortRelease.sb_read_anchor_solve(space, actor, anchorId, ref native);
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor solve is missing: " + exception.Message);
                return false;
            }
            if (result != 1) return false;
            int length = Array.IndexOf(native.driving_pose_tag, (byte)0);
            solve = new AnchorSolve
            {
                AnchorId = native.anchor_id,
                Flags = (AnchorProposalFlags)native.flags,
                Iterations = native.iterations,
                ObjectSharedId = native.object_shared_id,
                SdScaleMeters = native.sd_scale_m,
                ObjectiveJ = native.objective_j,
                FacingYawDegrees = IsFinite(native.facing_yaw) ? RadiansToUnityYawDegrees(native.facing_yaw) : 0f,
                DrivingPoseTag = System.Text.Encoding.UTF8.GetString(native.driving_pose_tag, 0,
                    length < 0 ? native.driving_pose_tag.Length : length),
            };
            return true;
        }

        /// <summary>
        /// Raises an app-specific exception of the SOURCE actor with an opaque payload (at most
        /// <see cref="ExceptionPayloadMax"/> bytes). Core never reads the payload; it rides the actor's next update
        /// to every space. False for an unknown actor or an oversize payload.
        /// </summary>
        internal static bool TryRaiseSourceException(long space, long actor, uint exceptionId, byte[] payload)
        {
            uint size = payload == null ? 0u : (uint)payload.Length;
            if (size > ExceptionPayloadMax) return false;
            try
            {
                return (useDebugLibrary
                    ? EmbodiedPortDebug.sb_raise_source_exception(space, actor, exceptionId, payload, size)
                    : EmbodiedPortRelease.sb_raise_source_exception(space, actor, exceptionId, payload, size)) == 0;
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// Solve-fenced native poll for exactly one consumer per actor. Drain without a budget, independently of snapshot
        /// reads. Only NativeEmpty can close a successful drain and authorize previous-sample retirement. Native/decode
        /// errors leave the drain incomplete; retain the returned failure and accepted samples even if a later poll is empty.
        /// Never call from a synchronous solve callback: the native C entry point waits for the solve lock.
        /// </summary>
        internal static ActorEventPollResult PollActorEvent(long space, long actor, out ActorEvent actorEvent,
                                                           out ActorEventPollFailure failure)
        {
            actorEvent = default;
            var native = new SbActorEvent
            {
                struct_size = (uint)Marshal.SizeOf<SbActorEvent>(),
                payload = new byte[ExceptionPayloadMax],
            };
            int result;
            try
            {
                result = useDebugLibrary
                    ? EmbodiedPortDebug.sb_poll_actor_event(space, actor, ref native)
                    : EmbodiedPortRelease.sb_poll_actor_event(space, actor, ref native);
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException ||
                                               exception is DllNotFoundException || exception is BadImageFormatException)
            {
                failure = new ActorEventPollFailure(null, "Actor-event polling is unavailable: " + exception.Message);
                return ActorEventPollResult.NativeError;
            }
            return ConvertActorEventPollResult(result, native, out actorEvent, out failure);
        }

        internal static ActorEventPollResult ConvertActorEventPollResult(int result, in SbActorEvent native,
                                                                         out ActorEvent actorEvent,
                                                                         out ActorEventPollFailure failure)
        {
            actorEvent = default;
            failure = null;
            if (result == 0) return ActorEventPollResult.NativeEmpty;
            if (result != 1)
            {
                failure = new ActorEventPollFailure(result, $"sb_poll_actor_event returned {result}, not event (1) or empty (0).");
                return ActorEventPollResult.NativeError;
            }
            if (TryConvertCheckedActorEvent(native, out actorEvent, out string error)) return ActorEventPollResult.Event;
            failure = new ActorEventPollFailure(result, error, CopyRejectedActorEvent(native), native.payload?.Length ?? 0);
            return ActorEventPollResult.ConversionError;
        }

        private static byte[] CopyRejectedActorEvent(in SbActorEvent native)
        {
            // Keep a detached, fixed-layout diagnostic copy, including malformed declared sizes. A synthetic shortened
            // managed buffer is zero-padded only in this copy; its original length is retained separately in the failure.
            SbActorEvent copy = native;
            copy.payload = new byte[ExceptionPayloadMax];
            if (native.payload != null)
                Array.Copy(native.payload, copy.payload, Math.Min(native.payload.Length, copy.payload.Length));
            var bytes = new byte[Marshal.SizeOf<SbActorEvent>()];
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(copy, pinned.AddrOfPinnedObject(), false);
            }
            finally
            {
                pinned.Free();
            }
            return bytes;
        }

        /// <summary>
        /// Strict decoder for an admitted typed consumer, including its lifecycle events. Typed yaw remains double native
        /// radians, never the legacy float fields. Resolving the matching accepted sample and its continuous world domain
        /// belongs to the consumer; a locally newer epoch alone must not reject a retained older accepted correspondence.
        /// </summary>
        internal static bool TryConvertCheckedActorEvent(in SbActorEvent native, out ActorEvent actorEvent, out string error)
        {
            actorEvent = default;
            error = null;
            if (native.struct_size != Marshal.SizeOf<SbActorEvent>())
            {
                error = $"Actor-event struct_size {native.struct_size} is not {Marshal.SizeOf<SbActorEvent>()}.";
                return false;
            }
            if (native.kind == (int)ActorEventKind.SourceException)
            {
                if (native.payload_size > ExceptionPayloadMax || native.payload_size > (native.payload?.Length ?? 0))
                {
                    error = "Source-exception payload_size exceeds its native buffer.";
                    return false;
                }
                return TryConvertActorEvent(native, out actorEvent);
            }
            if (native.kind == (int)ActorEventKind.TeleportRequest)
            {
                if (native.payload_size != 0 || !HasReadableEventPose(native) ||
                    (native.orientation.type >= (int)SbOrientationType.YawPitch &&
                     !IsFinite(HeadOrientation.PitchDegreesFromRadians(native.orientation.pitch))))
                {
                    error = "Teleport request has an invalid pose/orientation or unexpected payload.";
                    return false;
                }
                return TryConvertActorEvent(native, out actorEvent);
            }
            if (native.kind != (int)ActorEventKind.PositionRequest && native.kind != (int)ActorEventKind.YawRequest &&
                native.kind != (int)ActorEventKind.TargetRelease)
            {
                error = $"Unknown actor-event kind {native.kind}.";
                return false;
            }
            if (native.payload_size != TargetRequestPayloadSize || native.payload == null ||
                native.payload.Length < TargetRequestPayloadSize)
            {
                error = "Target request payload_size/buffer does not contain the 64-byte native payload.";
                return false;
            }
            SbTargetRequestPayload payload;
            IntPtr storage = Marshal.AllocHGlobal((int)TargetRequestPayloadSize);
            try
            {
                // The enclosing byte array need not be aligned for this native struct.
                Marshal.Copy(native.payload, 0, storage, (int)TargetRequestPayloadSize);
                payload = Marshal.PtrToStructure<SbTargetRequestPayload>(storage);
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
            if (payload.struct_size != TargetRequestPayloadSize ||
                (payload.request_flags & ~(uint)TargetRequestFlags.Placement) != 0)
            {
                error = "Target request struct_size or request_flags is unknown.";
                return false;
            }
            uint allChannels = (uint)(TargetChannels.Position | TargetChannels.Yaw);
            bool release = native.kind == (int)ActorEventKind.TargetRelease;
            bool yaw = native.kind == (int)ActorEventKind.YawRequest;
            uint expectedChannel = (uint)(yaw ? TargetChannels.Yaw : TargetChannels.Position);
            if (payload.channels == 0 || (payload.channels & ~allChannels) != 0 ||
                (!release && payload.channels != expectedChannel) || (release && payload.request_flags != 0))
            {
                error = "Target request channels do not match its event kind, or release claims placement.";
                return false;
            }
            if (!Enum.IsDefined(typeof(EmbodiedStatus), payload.proposal_status) ||
                (!release && payload.proposal_status != (int)EmbodiedStatus.Exact &&
                 payload.proposal_status != (int)EmbodiedStatus.Inexact))
            {
                error = "Target request proposal_status is unknown or does not carry a target.";
                return false;
            }
            bool placement = (payload.request_flags & (uint)TargetRequestFlags.Placement) != 0;
            if (!release && (!HasReadableEventPose(native) ||
                ((payload.proposal_flags & (uint)AnchorProposalFlags.FacingYaw) != 0 && !IsFinite(payload.facing_yaw))))
            {
                error = "Target request has an invalid pose/orientation or Facing yaw.";
                return false;
            }
            // Epoch zero is a valid native-accepted epoch; Sampled and a nonzero sequence establish availability.
            if (yaw && ((payload.proposal_flags & (uint)AnchorProposalFlags.YawUnspecified) != 0 ||
                (!placement && (payload.sample_sequence == 0 ||
                 (payload.proposal_flags & (uint)AnchorProposalFlags.Sampled) == 0 ||
                 !IsFinite(payload.sample_yaw) || !IsFinite(payload.yaw_turn) ||
                 !IsFinite(payload.sample_yaw + payload.yaw_turn)))))
            {
                error = "Ordinary yaw lacks its own accepted sample/finite signed turn, or its yaw is unspecified.";
                return false;
            }
            actorEvent = new ActorEvent
            {
                Kind = (ActorEventKind)native.kind,
                Revision = native.revision,
                SourceSequence = native.source_sequence,
                SourceSharedId = native.source_shared_id,
                AnchorId = native.anchor_id,
                TargetRequest = new TargetRequest((TargetChannels)payload.channels, payload.sample_epoch,
                    payload.sample_sequence, payload.sample_yaw, payload.yaw_turn,
                    (AnchorProposalFlags)payload.proposal_flags, (EmbodiedCause)payload.proposal_cause, payload.facing_yaw,
                    (EmbodiedStatus)payload.proposal_status, (TargetRequestFlags)payload.request_flags,
                    release ? default : CoordinateConverter.SpatialBlendToUnity3D(native.x, native.y, native.z),
                    native.orientation.type, native.orientation.yaw, native.orientation.pitch, native.orientation.roll),
            };
            return true;
        }

        private static bool HasReadableEventPose(in SbActorEvent native) =>
            IsFinite(native.x) && Math.Abs(native.x) <= float.MaxValue &&
            IsFinite(native.y) && Math.Abs(native.y) <= float.MaxValue &&
            IsFinite(native.z) && Math.Abs(native.z) <= float.MaxValue &&
            native.orientation.struct_size == Marshal.SizeOf<SbOrientation>() &&
            Enum.IsDefined(typeof(SbOrientationType), native.orientation.type) && IsFinite(native.orientation.yaw) &&
            (native.orientation.type < (int)SbOrientationType.YawPitch || IsFinite(native.orientation.pitch)) &&
            (native.orientation.type < (int)SbOrientationType.YawPitchRoll || IsFinite(native.orientation.roll));

        /// <summary>
        /// Legacy lifecycle-only poll, unchanged for existing drivers. False also covers invalid handles/decode failures:
        /// it is NOT NativeEmpty and must never authorize accepted-sample retirement or typed publication-group completion.
        /// </summary>
        internal static bool TryPollActorEvent(long space, long actor, out ActorEvent actorEvent)
        {
            actorEvent = default;
            var native = new SbActorEvent
            {
                struct_size = (uint)Marshal.SizeOf<SbActorEvent>(),
                payload = new byte[ExceptionPayloadMax],
            };
            int result;
            try
            {
                result = useDebugLibrary
                    ? EmbodiedPortDebug.sb_poll_actor_event(space, actor, ref native)
                    : EmbodiedPortRelease.sb_poll_actor_event(space, actor, ref native);
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                return false;
            }
            return result == 1 && TryConvertActorEvent(native, out actorEvent);
        }

        /// <summary>
        /// Converts a native event (Unity space, degrees). False for a kind the binding does not know. A teleport
        /// request with a non-finite pose is refused: there is nothing to place at.
        /// </summary>
        internal static bool TryConvertActorEvent(in SbActorEvent native, out ActorEvent actorEvent)
        {
            actorEvent = default;
            // The legacy driver has no typed consumer or accepted-sample history. New kinds are decoded only by the
            // explicit checked path; adding enum values must not reinterpret them as legacy teleport requests.
            if (native.kind != (int)ActorEventKind.SourceException && native.kind != (int)ActorEventKind.TeleportRequest)
            {
                return false;
            }
            actorEvent.Kind = (ActorEventKind)native.kind;
            actorEvent.Revision = native.revision;
            actorEvent.SourceSequence = native.source_sequence;
            actorEvent.SourceSharedId = native.source_shared_id;
            if (actorEvent.Kind == ActorEventKind.SourceException)
            {
                actorEvent.ExceptionId = native.exception_id;
                int size = (int)Math.Min(native.payload_size, ExceptionPayloadMax);
                actorEvent.Payload = new byte[size];
                if (native.payload != null) Array.Copy(native.payload, actorEvent.Payload, Math.Min(size, native.payload.Length));
                return true;
            }
            if (!IsFinite(native.x) || !IsFinite(native.y) || !IsFinite(native.z) || !IsFinite(native.orientation.yaw))
            {
                return false;
            }
            actorEvent.InteractionKind = native.interaction_kind;
            actorEvent.AnchorId = native.anchor_id;
            actorEvent.Position = CoordinateConverter.SpatialBlendToUnity3D(native.x, native.y, native.z);
            actorEvent.YawDegrees = RadiansToUnityYawDegrees(native.orientation.yaw);
            if (native.orientation.type >= (int)SbOrientationType.YawPitch && IsFinite(native.orientation.pitch))
            {
                actorEvent.HasPitch = true;
                actorEvent.PitchDegrees = HeadOrientation.PitchDegreesFromRadians(native.orientation.pitch);
            }
            return true;
        }

        /// <summary>
        /// The traversable course between two ground points (Unity space), in <paramref name="actor"/>'s
        /// obstacle-aware free space. Appends the waypoints after the start, ending at <paramref name="to"/>.
        /// False when no course exists. Stateless in the core.
        /// </summary>
        internal static bool TryQueryEmbodiedCourse(long space, long actor, Vector2 from, Vector2 to,
                                                    double[] scratch, List<Vector2> waypoints)
        {
            CoordinateConverter.UnityToSpatialBlend2D(new Vector3(from.x, 0f, from.y), out double fx, out double fy);
            CoordinateConverter.UnityToSpatialBlend2D(new Vector3(to.x, 0f, to.y), out double tx, out double ty);
            int count;
            try
            {
                count = useDebugLibrary
                    ? EmbodiedPortDebug.sb_query_course(space, actor, fx, fy, tx, ty, scratch, scratch.Length / 2)
                    : EmbodiedPortRelease.sb_query_course(space, actor, fx, fy, tx, ty, scratch, scratch.Length / 2);
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportEmbodiedPortFailure("The anchor port is missing: " + exception.Message);
                return false;
            }
            return TryConvertCourse(scratch, count, waypoints);
        }

        /// <summary>
        /// Converts a native course (<paramref name="count"/> waypoints as sb (x, y) pairs, the first being the
        /// start) to Unity ground points, without the start. False when there is no course or it has nothing
        /// after the start.
        /// </summary>
        internal static bool TryConvertCourse(double[] xy, int count, List<Vector2> waypoints)
        {
            int available = xy == null ? 0 : Math.Min(count, xy.Length / 2);
            if (count < 2 || available < 2) return false;
            for (int i = 1; i < available; ++i)
            {
                if (double.IsNaN(xy[2 * i]) || double.IsInfinity(xy[2 * i]) ||
                    double.IsNaN(xy[2 * i + 1]) || double.IsInfinity(xy[2 * i + 1]))
                {
                    waypoints.Clear();
                    return false;
                }
                Vector3 unity = CoordinateConverter.SpatialBlendToUnity2D(xy[2 * i], xy[2 * i + 1], 0f);
                waypoints.Add(new Vector2(unity.x, unity.z));
            }
            return waypoints.Count > 0;
        }

        // Reduce in double precision before narrowing, as the target path does.
        internal static float RadiansToUnityYawDegrees(double radians) =>
            CoordinateConverter.SpatialBlendToUnityAngle((float)((radians % (2.0 * Math.PI)) * 180.0 / Math.PI));

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>The embodied driver's native adapter: the ABI 4 per-anchor ports, the events and the stateless course query.</summary>
    internal sealed class NativeEmbodiedCore : IEmbodiedTargetEventCore
    {
        private readonly long _space;
        private readonly double[] _courseScratch = new double[2 * 64];
        private SbAnchorProposal[] _proposalScratch = new SbAnchorProposal[SpatialBlendAPI.InitialProposalCapacity];

        internal NativeEmbodiedCore(long space)
        {
            _space = space;
        }

        public bool TryGetAnchorId(long actor, string anchorName, out uint anchorId) =>
            SpatialBlendAPI.TryGetAnchorId(_space, actor, anchorName, out anchorId);

        public bool TryGetAnchorName(long actor, uint anchorId, out string anchorName) =>
            SpatialBlendAPI.TryGetAnchorName(_space, actor, anchorId, out anchorName);

        public void PublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double timeSeconds,
                                      ulong sequence, ulong epoch, bool teleported)
        {
            TryPublishAnchorPose(actor, anchorId, pose, timeSeconds, sequence, epoch, teleported);
        }

        public bool TryPublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double timeSeconds,
                                         ulong sequence, ulong epoch, bool teleported) =>
            SpatialBlendAPI.TryPublishAnchorPose(_space, actor, anchorId, pose, timeSeconds, sequence, epoch, teleported);

        public bool TryPublishAnchorPose(long actor, uint anchorId, in EmbodiedPose pose, double continuousUnityYawDegrees,
                                         double timeSeconds, ulong sequence, ulong epoch, bool teleported,
                                         out double submittedNativeYawRadians) =>
            SpatialBlendAPI.TryPublishAnchorPose(_space, actor, anchorId, pose, continuousUnityYawDegrees, timeSeconds,
                sequence, epoch, teleported, out submittedNativeYawRadians);

        public bool TrySetTargetEventDelivery(long actor, bool enabled, out string error) =>
            SpatialBlendAPI.TrySetTargetEventDelivery(_space, actor, enabled, out error);

        public ActorEventPollResult PollEvent(long actor, out ActorEvent actorEvent, out ActorEventPollFailure failure) =>
            SpatialBlendAPI.PollActorEvent(_space, actor, out actorEvent, out failure);

        public bool TryReadProposals(long actor, ProposalSet proposals) =>
            SpatialBlendAPI.TryReadProposals(_space, actor, ref _proposalScratch, proposals);

        public bool TryPollEvent(long actor, out ActorEvent actorEvent) =>
            SpatialBlendAPI.TryPollActorEvent(_space, actor, out actorEvent);

        public bool TryQueryCourse(long actor, Vector2 from, Vector2 to, List<Vector2> waypoints) =>
            SpatialBlendAPI.TryQueryEmbodiedCourse(_space, actor, from, to, _courseScratch, waypoints);

        public EmbodimentRegistrationResult RegisterEmbodiment(long actor, string specJson, out string error) =>
            SpatialBlendAPI.RegisterEmbodimentJson(_space, actor, specJson, out error);

        public bool TryGetBoneId(long actor, string boneName, out int boneId) =>
            SpatialBlendAPI.TryGetBoneId(_space, actor, boneName, out boneId);

        public void PublishBonePose(long actor, int boneId, in EmbodiedBonePose pose, double timeSeconds, ulong sequence,
                                    ulong epoch, bool teleported)
        {
            TryPublishBonePose(actor, boneId, pose, timeSeconds, sequence, epoch, teleported);
        }

        public bool TryPublishBonePose(long actor, int boneId, in EmbodiedBonePose pose, double timeSeconds, ulong sequence,
                                       ulong epoch, bool teleported) =>
            SpatialBlendAPI.TryPublishBonePose(_space, actor, boneId, pose, timeSeconds, sequence, epoch, teleported);

        public bool TryAnchorPoseFromBone(long actor, string anchorName, in EmbodiedBonePose bone, out EmbodiedBonePose anchor) =>
            SpatialBlendAPI.TryAnchorPoseFromBone(_space, actor, anchorName, bone, out anchor);

        public bool TryBonePoseFromAnchor(long actor, string anchorName, in EmbodiedBonePose anchor, out EmbodiedBonePose bone) =>
            SpatialBlendAPI.TryBonePoseFromAnchor(_space, actor, anchorName, anchor, out bone);
    }
}
