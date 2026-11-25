// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>Mirrors three doubles (<c>double[3]</c> of the C structs). 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbVec3d
    {
        public double x;
        public double y;
        public double z;
    }

    /// <summary>Mirrors four doubles (<c>double[4]</c>, a quaternion x, y, z, w). 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbQuatd
    {
        public double x;
        public double y;
        public double z;
        public double w;
    }

    /// <summary>
    /// Mirrors <c>sb_bone_pose_t</c> (ABI 4.7, <c>SB_STRUCT_BONE_POSE</c> = 26). 72 bytes: the pose of one bone,
    /// bone-local to world, Z-up right-handed, rotation a unit quaternion (x, y, z, w).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbBonePose
    {
        public uint struct_size;
        public uint flags;
        public SbVec3d position;
        public SbQuatd rotation;
        public ulong epoch;
    }

    /// <summary>Mirrors <c>sb_named_bone_pose_t</c> (ABI 4.7, <c>SB_STRUCT_NAMED_BONE_POSE</c> = 27). 80 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbNamedBonePose
    {
        public IntPtr bone_name;
        public SbBonePose pose;
    }

    /// <summary>Mirrors <c>sb_chain_joint_t</c> (ABI 4.7, <c>SB_STRUCT_CHAIN_JOINT</c> = 28). 128 bytes: one joint of the registered chain, as read back.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbChainJoint
    {
        public uint struct_size;
        public int parent;
        public SbVec3d rest_position;
        public SbQuatd rest_rotation;
        public double segment_length_m;
        public uint limit_mask;
        public SbVec3d limit_min;
        public SbVec3d limit_max;
    }

    /// <summary>How the core answered a registration.</summary>
    internal enum EmbodimentRegistrationResult
    {
        /// <summary>The embodiment is registered: its bone-bound anchors are now derived from the bones the driver publishes.</summary>
        Registered = 0,
        /// <summary>The loaded core (or the adapter) has no embodiment registration. Nothing changed: the anchors stay as they were.</summary>
        Unsupported = 1,
        /// <summary>The core refused the spec (invalid, or already registered): the error text says why.</summary>
        Refused = 2
    }

    internal static partial class SpatialBlendAPI
    {
        /// <summary>
        /// The ABI 4 minor version that has embodiment registration (4.7). Since <see cref="RequiredAbiMinor"/> became 4.11 every
        /// accepted core has it; the driver still treats an Unsupported answer as "keep the legacy anchors".
        /// </summary>
        internal const uint EmbodimentAbiMinor = 7;

        private static class EmbodimentRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_abi_minor_version();
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_register_embodiment_json(long space, long actor, byte[] json, int jsonLen,
                [Out] byte[] error, int errorCap);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_embodiment_json(long space, long actor, [Out] byte[] buffer, int capacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_bone_id(long space, long actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_publish_bone_pose(long space, long actor, int boneId, ref SbBonePose pose,
                double timestampSeconds, ulong sequence);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_apply_actor_bone_frame(long space, long actor, ulong epoch, long frameSequence,
                double timeSeconds, IntPtr bones, int boneCount, IntPtr observations, int observationCount);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_embodiment_anchor_pose_from_bone(long space, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, ref SbBonePose bone, ref SbBonePose anchorOut);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_embodiment_bone_pose_from_anchor(long space, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, ref SbBonePose anchor, ref SbBonePose boneOut);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_chain_joint_count(long space, long actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_chain_joint(long space, long actor, int index, ref SbChainJoint joint,
                [Out] byte[] name, int nameCap);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_convert_pose_basis([In] double[] basis, int toCanonical, ref SbBonePose input,
                ref SbBonePose output);
        }

        private static class EmbodimentDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_abi_minor_version();
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_register_embodiment_json(long space, long actor, byte[] json, int jsonLen,
                [Out] byte[] error, int errorCap);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_embodiment_json(long space, long actor, [Out] byte[] buffer, int capacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_bone_id(long space, long actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_publish_bone_pose(long space, long actor, int boneId, ref SbBonePose pose,
                double timestampSeconds, ulong sequence);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_apply_actor_bone_frame(long space, long actor, ulong epoch, long frameSequence,
                double timeSeconds, IntPtr bones, int boneCount, IntPtr observations, int observationCount);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_embodiment_anchor_pose_from_bone(long space, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, ref SbBonePose bone, ref SbBonePose anchorOut);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_embodiment_bone_pose_from_anchor(long space, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, ref SbBonePose anchor, ref SbBonePose boneOut);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_chain_joint_count(long space, long actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_chain_joint(long space, long actor, int index, ref SbChainJoint joint,
                [Out] byte[] name, int nameCap);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_convert_pose_basis([In] double[] basis, int toCanonical, ref SbBonePose input,
                ref SbBonePose output);
        }

        private static bool? _embodimentUsable;
        private static string _embodimentUnusableReason;

        /// <summary>
        /// True when the loaded library has embodiment registration (ABI 4.7) with the layouts this binding marshals:
        /// <c>sb_abi_minor_version</c> at least <see cref="EmbodimentAbiMinor"/>, and for the three new structs
        /// <c>sb_sizeof</c>, the field count and every field offset (<c>sb_get_struct_field_offset</c>, ids 26 to 28) equal to the managed ones.
        /// The answer is computed once. Quiet: a core without the feature is a supported configuration, the caller says so.
        /// </summary>
        internal static bool TryCheckEmbodimentRegistration(out string reason)
        {
            if (_embodimentUsable.HasValue)
            {
                reason = _embodimentUnusableReason;
                return _embodimentUsable.Value;
            }
            _embodimentUsable = EvaluateEmbodimentRegistration(out _embodimentUnusableReason);
            reason = _embodimentUnusableReason;
            return _embodimentUsable.Value;
        }

        private static bool EvaluateEmbodimentRegistration(out string reason)
        {
            reason = null;
            try
            {
                uint minor = useDebugLibrary ? EmbodimentDebug.sb_abi_minor_version() : EmbodimentRelease.sb_abi_minor_version();
                if (minor < EmbodimentAbiMinor)
                {
                    reason = $"the core is ABI 4.{minor}; embodiment registration needs 4.{EmbodimentAbiMinor}";
                    return false;
                }
                var mismatches = new System.Collections.Generic.List<string>();
                CheckStruct(SbStructId.BonePose, typeof(SbBonePose), BonePoseFields, mismatches);
                CheckStruct(SbStructId.NamedBonePose, typeof(SbNamedBonePose), NamedBonePoseFields, mismatches);
                CheckStruct(SbStructId.ChainJoint, typeof(SbChainJoint), ChainJointFields, mismatches);
                string mismatch = mismatches.Count == 0 ? null : string.Join("; ", mismatches);
                if (mismatch != null)
                {
                    reason = "the embodiment layouts differ from the managed binding: " + mismatch;
                    return false;
                }
                return true;
            }
            catch (EntryPointNotFoundException exception)
            {
                reason = "the loaded native library has no embodiment registration: " + exception.Message;
                return false;
            }
            catch (DllNotFoundException exception)
            {
                reason = "the native library is not loaded: " + exception.Message;
                return false;
            }
        }

        // Field names in declaration order, exactly the order sb_get_struct_field_offset indexes them.
        internal static readonly string[] BonePoseFields = { "struct_size", "flags", "position", "rotation", "epoch" };
        internal static readonly string[] NamedBonePoseFields = { "bone_name", "pose" };
        internal static readonly string[] ChainJointFields =
        {
            "struct_size", "parent", "rest_position", "rest_rotation", "segment_length_m", "limit_mask", "limit_min", "limit_max"
        };

        private static SbBonePose ToNativeBonePose(in EmbodiedBonePose pose, ulong epoch, bool teleported) => new SbBonePose
        {
            struct_size = (uint)Marshal.SizeOf<SbBonePose>(),
            flags = teleported ? EmbodiedTeleported : 0u,
            position = new SbVec3d { x = pose.X, y = pose.Y, z = pose.Z },
            rotation = new SbQuatd { x = pose.Qx, y = pose.Qy, z = pose.Qz, w = pose.Qw },
            epoch = epoch,
        };

        private static EmbodiedBonePose FromNativeBonePose(in SbBonePose pose) => new EmbodiedBonePose(
            pose.position.x, pose.position.y, pose.position.z,
            pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w);

        /// <summary>
        /// Registers the actor's embodiment. <see cref="EmbodimentRegistrationResult.Unsupported"/> (and nothing sent) when
        /// the library lacks ABI 4.7; <see cref="EmbodimentRegistrationResult.Refused"/> with the core's own error text otherwise
        /// when it says no.
        /// </summary>
        internal static EmbodimentRegistrationResult RegisterEmbodimentJson(long space, long actor, string json, out string error)
        {
            error = null;
            if (!TryCheckEmbodimentRegistration(out string reason))
            {
                error = reason;
                return EmbodimentRegistrationResult.Unsupported;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
            var buffer = new byte[512];
            int result = useDebugLibrary
                ? EmbodimentDebug.sb_register_embodiment_json(space, actor, bytes, bytes.Length, buffer, buffer.Length)
                : EmbodimentRelease.sb_register_embodiment_json(space, actor, bytes, bytes.Length, buffer, buffer.Length);
            if (result == 0) return EmbodimentRegistrationResult.Registered;
            int length = Array.IndexOf(buffer, (byte)0);
            error = $"sb_register_embodiment_json returned {result}: " +
                    Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
            return EmbodimentRegistrationResult.Refused;
        }

        /// <summary>The id of bone <paramref name="boneName"/> of the actor's registered skeleton. False when there is none.</summary>
        internal static bool TryGetBoneId(long space, long actor, string boneName, out int boneId)
        {
            boneId = -1;
            if (!TryCheckEmbodimentRegistration(out _)) return false;
            boneId = useDebugLibrary
                ? EmbodimentDebug.sb_get_bone_id(space, actor, boneName)
                : EmbodimentRelease.sb_get_bone_id(space, actor, boneName);
            return boneId >= 0;
        }

        /// <summary>
        /// Publishes the pose of one bone (core world, the registered bone frame). Wait-free in the core. False when it was
        /// refused: an unknown bone or actor, a non-finite pose, or a stale sequence.
        /// </summary>
        internal static bool TryPublishBonePose(long space, long actor, int boneId, in EmbodiedBonePose pose,
                                                double timestampSeconds, ulong sequence, ulong epoch, bool teleported)
        {
            if (!TryCheckEmbodimentRegistration(out _)) return false;
            SbBonePose native = ToNativeBonePose(pose, epoch, teleported);
            return (useDebugLibrary
                ? EmbodimentDebug.sb_publish_bone_pose(space, actor, boneId, ref native, timestampSeconds, sequence)
                : EmbodimentRelease.sb_publish_bone_pose(space, actor, boneId, ref native, timestampSeconds, sequence)) == 0;
        }

        /// <summary>The registration's own arithmetic, anchor = bone * offset (the core's, so no driver re-implements it).</summary>
        internal static bool TryAnchorPoseFromBone(long space, long actor, string anchorName, in EmbodiedBonePose bone,
                                                   out EmbodiedBonePose anchor)
        {
            anchor = default;
            if (!TryCheckEmbodimentRegistration(out _)) return false;
            SbBonePose input = ToNativeBonePose(bone, 0, false);
            var output = new SbBonePose { struct_size = (uint)Marshal.SizeOf<SbBonePose>() };
            int result = useDebugLibrary
                ? EmbodimentDebug.sb_embodiment_anchor_pose_from_bone(space, actor, anchorName, ref input, ref output)
                : EmbodimentRelease.sb_embodiment_anchor_pose_from_bone(space, actor, anchorName, ref input, ref output);
            if (result != 0) return false;
            anchor = FromNativeBonePose(output);
            return true;
        }

        /// <summary>The registration's own inverse, bone = anchor * offset^-1: the bone pose that places the anchor at a pose.</summary>
        internal static bool TryBonePoseFromAnchor(long space, long actor, string anchorName, in EmbodiedBonePose anchor,
                                                   out EmbodiedBonePose bone)
        {
            bone = default;
            if (!TryCheckEmbodimentRegistration(out _)) return false;
            SbBonePose input = ToNativeBonePose(anchor, 0, false);
            var output = new SbBonePose { struct_size = (uint)Marshal.SizeOf<SbBonePose>() };
            int result = useDebugLibrary
                ? EmbodimentDebug.sb_embodiment_bone_pose_from_anchor(space, actor, anchorName, ref input, ref output)
                : EmbodimentRelease.sb_embodiment_bone_pose_from_anchor(space, actor, anchorName, ref input, ref output);
            if (result != 0) return false;
            bone = FromNativeBonePose(output);
            return true;
        }

        /// <summary>
        /// The core's own basis conversion (<c>sb_convert_pose_basis</c>): a pose between a skeleton's native axes and the
        /// canonical ones, c = B n, rotations B R B^T. Used by tests to compare the managed conversion with the core's.
        /// </summary>
        internal static bool TryConvertPoseBasis(double[] basisRows, bool toCanonical, in EmbodiedBonePose input,
                                                 out EmbodiedBonePose output)
        {
            output = default;
            if (!TryCheckEmbodimentRegistration(out _)) return false;
            SbBonePose native = ToNativeBonePose(input, 0, false);
            var converted = new SbBonePose { struct_size = (uint)Marshal.SizeOf<SbBonePose>() };
            int result = useDebugLibrary
                ? EmbodimentDebug.sb_convert_pose_basis(basisRows, toCanonical ? 1 : 0, ref native, ref converted)
                : EmbodimentRelease.sb_convert_pose_basis(basisRows, toCanonical ? 1 : 0, ref native, ref converted);
            if (result != 0) return false;
            output = FromNativeBonePose(converted);
            return true;
        }

        /// <summary>The registered embodiment as the core holds it (canonical JSON), or null when there is none.</summary>
        internal static string GetEmbodimentJson(long space, long actor)
        {
            if (!TryCheckEmbodimentRegistration(out _)) return null;
            int needed = useDebugLibrary ? EmbodimentDebug.sb_get_embodiment_json(space, actor, null, 0)
                                         : EmbodimentRelease.sb_get_embodiment_json(space, actor, null, 0);
            if (needed <= 0) return null;
            var buffer = new byte[needed + 1];
            if (useDebugLibrary) EmbodimentDebug.sb_get_embodiment_json(space, actor, buffer, buffer.Length);
            else EmbodimentRelease.sb_get_embodiment_json(space, actor, buffer, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, needed);
        }
    }
}
