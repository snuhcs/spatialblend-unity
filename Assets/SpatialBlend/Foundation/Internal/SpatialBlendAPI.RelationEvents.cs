// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace SpatialBlend.Internal
{
    using SpaceID = System.Int64;

    internal enum SbStructId : int
    {
        ActorState = 0,
        RealizationTarget = 1,
        RelationEvent = 2,
        RelationState = 3,
        RelationStateV2 = 4,
        // ABI 3.1: the orientation. (ABI 3's 19 and 20, the embodied pose and the proposal, are removed and not reused.)
        Orientation = 21,
        // ABI 4: the per-anchor port, the anchor proposal, the proposal set and the actor event.
        AnchorPose = 22,
        AnchorProposal = 23,
        ProposalSet = 24,
        ActorEvent = 25,
        // ABI 4.7: embodiment registration (the bone pose, the named bone pose and the chain joint read-back).
        BonePose = 26,
        NamedBonePose = 27,
        ChainJoint = 28,
        // ABI 4.10: the part chart coordinate (no managed mirror). ABI 4.11: the inner level of one anchor's proposal.
        AnchorSolve = 30,
        // ABI 4.12: the signed, unwrapped yaw turn of one anchor's proposal.
        AnchorTurn = 31,
        // ABI 4.13: typed payload in the unchanged actor-event envelope.
        TargetRequestPayload = 32
    }

    internal enum SbRelationPhase : int
    {
        Dormant = 0,
        Active = 1,
        Driving = 2,
        Contact = 3
    }

    // Mirrors sb_relation_event_t. Field offsets are checked against
    // sb_get_struct_field_offset(SB_STRUCT_RELATION_EVENT) before use.
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct SbRelationEvent
    {
        [FieldOffset(0)] public long actor_shared_id;
        [FieldOffset(8)] public IntPtr anchor_name;
        [FieldOffset(16)] public long referent_shared_id;
        [FieldOffset(24)] public int kernel_type;
        [FieldOffset(28)] public int from_phase;
        [FieldOffset(32)] public int to_phase;
        [FieldOffset(40)] public double timestamp;
    }

    // Mirrors sb_relation_state_v2_t, including the A4 tail. Callers set
    // struct_size to this size; sb_sizeof(SB_STRUCT_RELATION_STATE_V2) must agree.
    [StructLayout(LayoutKind.Explicit, Size = 264)]
    internal struct SbRelationStateV2
    {
        [FieldOffset(0)] public uint struct_size;
        [FieldOffset(4)] public uint version;
        [FieldOffset(8)] public IntPtr anchor_name;
        [FieldOffset(16)] public IntPtr sub_region_name;
        [FieldOffset(24)] public double proximity;
        [FieldOffset(32)] public double support_distance_m;
        [FieldOffset(40)] public double source_scope_diameter_m;
        [FieldOffset(48)] public double contact_point_world_x;
        [FieldOffset(56)] public double contact_point_world_y;
        [FieldOffset(64)] public double contact_point_world_z;
        [FieldOffset(72)] public double normalized_angle;
        [FieldOffset(80)] public double normalized_radial_ratio;
        [FieldOffset(88)] public double normalized_height_ratio;
        [FieldOffset(96)] public double interior_u;
        [FieldOffset(104)] public double interior_v;
        [FieldOffset(112)] public double interior_w;
        [FieldOffset(120)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public double[] encoded_state;
        [FieldOffset(184)] public long referent_element_id;
        [FieldOffset(192)] public long referent_shared_id;
        [FieldOffset(200)] public long binding_index;
        [FieldOffset(208)] public int kernel_type;
        [FieldOffset(212)] public int normalized_strategy;
        [FieldOffset(216)] public int phase;
        [FieldOffset(220)] public int has_contact;
        [FieldOffset(224)] public int active;
        [FieldOffset(228)] public int has_interior;
        [FieldOffset(232)] public int encoded_state_size;
        [FieldOffset(236)] public uint chart_version;
        [FieldOffset(240)] public double ap_offset_u_m;
        [FieldOffset(248)] public double ap_offset_v_m;
        [FieldOffset(256)] public double ap_height_m;
    }

    internal readonly struct RelationEvent
    {
        public readonly long ActorSharedId;
        public readonly string AnchorName;
        public readonly long ReferentSharedId;
        public readonly SbKernelType Kernel;
        public readonly SbRelationPhase FromPhase;
        public readonly SbRelationPhase ToPhase;
        public readonly double Timestamp;

        public RelationEvent(long actorSharedId, string anchorName, long referentSharedId,
                             SbKernelType kernel, SbRelationPhase fromPhase, SbRelationPhase toPhase,
                             double timestamp)
        {
            ActorSharedId = actorSharedId;
            AnchorName = anchorName ?? string.Empty;
            ReferentSharedId = referentSharedId;
            Kernel = kernel;
            FromPhase = fromPhase;
            ToPhase = toPhase;
            Timestamp = timestamp;
        }
    }

    internal readonly struct RelationStateV2Row
    {
        public readonly string AnchorName;
        public readonly double Proximity;
        public readonly long ReferentElementId;
        public readonly long ReferentSharedId;
        public readonly SbKernelType Kernel;
        public readonly SbRelationPhase Phase;
        public readonly int Active;

        public RelationStateV2Row(string anchorName, double proximity, long referentElementId,
                                  long referentSharedId, SbKernelType kernel, SbRelationPhase phase,
                                  int active)
        {
            AnchorName = anchorName ?? string.Empty;
            Proximity = proximity;
            ReferentElementId = referentElementId;
            ReferentSharedId = referentSharedId;
            Kernel = kernel;
            Phase = phase;
            Active = active;
        }
    }

    internal delegate void OnRelationEventDelegate(SpaceID space, RelationEvent relationEvent);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnRelationEventNativeDelegate(IntPtr relationEvent, IntPtr userData);

    internal static partial class SpatialBlendAPI
    {
        private const uint ObservabilityVersion = 1;
        private static readonly Dictionary<SpaceID, OnRelationEventDelegate> relationEventCallbacks =
            new Dictionary<SpaceID, OnRelationEventDelegate>();
        private static readonly Dictionary<SpaceID, long> relationEventCallbackTokens =
            new Dictionary<SpaceID, long>();
        private static readonly Dictionary<long, SpaceID> relationCallbackSpaceByToken =
            new Dictionary<long, SpaceID>();
        private static readonly OnRelationEventNativeDelegate onRelationEventWrapper = OnRelationEventWrapper;

        private static class RelationRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_relation_event(long space, OnRelationEventNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ulong sb_sizeof(SbStructId structId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_struct_field_offset(SbStructId structId, uint field);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_struct_field_count(SbStructId structId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_relation_states_v2(long space, long actor, IntPtr states, int cap, uint structSize);
        }

        private static class RelationDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_relation_event(long space, OnRelationEventNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ulong sb_sizeof(SbStructId structId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_struct_field_offset(SbStructId structId, uint field);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_struct_field_count(SbStructId structId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_relation_states_v2(long space, long actor, IntPtr states, int cap, uint structSize);
        }

        public static void RegisterOnRelationEvent(SpaceID space, OnRelationEventDelegate callback)
        {
            if (callback == null)
            {
                UnregisterOnRelationEvent(space);
                return;
            }

            long previous = 0;
            lock (callbackLock)
            {
                relationEventCallbackTokens.TryGetValue(space, out previous);
            }
            long token = RegisterCallback(relationEventCallbacks, relationEventCallbackTokens, space, callback);
            lock (callbackLock)
            {
                if (previous != 0) relationCallbackSpaceByToken.Remove(previous);
                relationCallbackSpaceByToken[token] = space;
            }
            try
            {
                if (useDebugLibrary) RelationDebug.sb_register_on_relation_event(space, onRelationEventWrapper, new IntPtr(token));
                else RelationRelease.sb_register_on_relation_event(space, onRelationEventWrapper, new IntPtr(token));
            }
            catch
            {
                InvalidateCallback(relationEventCallbacks, relationEventCallbackTokens, space, token);
                lock (callbackLock)
                {
                    relationCallbackSpaceByToken.Remove(token);
                }
                throw;
            }
        }

        public static void UnregisterOnRelationEvent(SpaceID space)
        {
            if (!DropRelationCallback(space)) return;
            if (useDebugLibrary) RelationDebug.sb_register_on_relation_event(space, null, IntPtr.Zero);
            else RelationRelease.sb_register_on_relation_event(space, null, IntPtr.Zero);
        }

        /// <summary>
        /// Copies sb_get_relation_states_v2 rows. Anchor names are copied before return;
        /// the native pointers are only valid until the next call on this thread.
        /// </summary>
        public static int GetRelationStatesV2(long space, long actor, List<RelationStateV2Row> rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            rows.Clear();
            int structSize = Marshal.SizeOf<SbRelationStateV2>();
            int capacity = 8;
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                SbRelationStateV2[] native = ReadRelationStateBuffer(space, actor, capacity, structSize, out int count);
                if (count < 0) return count;
                if (count > capacity)
                {
                    capacity = count;
                    continue;
                }
                int copy = native == null ? 0 : native.Length;
                for (int i = 0; i < copy; ++i)
                {
                    SbRelationStateV2 row = native[i];
                    string anchor = row.anchor_name == IntPtr.Zero
                        ? string.Empty
                        : Marshal.PtrToStringUTF8(row.anchor_name);
                    rows.Add(new RelationStateV2Row(
                        anchor, row.proximity, row.referent_element_id, row.referent_shared_id,
                        (SbKernelType)row.kernel_type, (SbRelationPhase)row.phase, row.active));
                }
                return count;
            }
            return -1;
        }

        internal static bool TryValidateRelationAbi(out string error)
        {
            var mismatches = new List<string>();
            try
            {
                CheckStruct(SbStructId.RelationEvent, typeof(SbRelationEvent), new[]
                {
                    "actor_shared_id", "anchor_name", "referent_shared_id",
                    "kernel_type", "from_phase", "to_phase", "timestamp"
                }, mismatches);
                CheckStruct(SbStructId.RelationStateV2, typeof(SbRelationStateV2), new[]
                {
                    "struct_size", "version", "anchor_name", "sub_region_name", "proximity",
                    "support_distance_m", "source_scope_diameter_m",
                    "contact_point_world_x", "contact_point_world_y", "contact_point_world_z",
                    "normalized_angle", "normalized_radial_ratio", "normalized_height_ratio",
                    "interior_u", "interior_v", "interior_w", "encoded_state",
                    "referent_element_id", "referent_shared_id", "binding_index",
                    "kernel_type", "normalized_strategy", "phase",
                    "has_contact", "active", "has_interior", "encoded_state_size", "chart_version",
                    "ap_offset_u_m", "ap_offset_v_m", "ap_height_m"
                }, mismatches);
            }
            catch (EntryPointNotFoundException exception)
            {
                error = "sb_sizeof / sb_get_struct_field_offset is missing from the loaded library: " + exception.Message;
                return false;
            }
            error = mismatches.Count == 0 ? string.Empty : string.Join("; ", mismatches);
            return mismatches.Count == 0;
        }

        private static void CheckStruct(SbStructId id, Type managed, string[] fields, List<string> mismatches)
        {
            ulong nativeSize = useDebugLibrary ? RelationDebug.sb_sizeof(id) : RelationRelease.sb_sizeof(id);
            uint managedSize = (uint)Marshal.SizeOf(managed);
            if (nativeSize == 0)
            {
                mismatches.Add(id + " is not exposed by sb_sizeof");
                return;
            }
            if (nativeSize != managedSize)
            {
                mismatches.Add($"{id} size native={nativeSize} managed={managedSize}");
            }
            uint nativeCount = useDebugLibrary
                ? RelationDebug.sb_get_struct_field_count(id)
                : RelationRelease.sb_get_struct_field_count(id);
            if (nativeCount != fields.Length)
            {
                mismatches.Add($"{id} field count native={nativeCount} managed={fields.Length}");
            }
            uint compare = Math.Min(nativeCount, (uint)fields.Length);
            for (uint field = 0; field < compare; ++field)
            {
                uint nativeOffset = useDebugLibrary
                    ? RelationDebug.sb_get_struct_field_offset(id, field)
                    : RelationRelease.sb_get_struct_field_offset(id, field);
                uint managedOffset = (uint)Marshal.OffsetOf(managed, fields[field]).ToInt32();
                if (nativeOffset != managedOffset)
                {
                    mismatches.Add($"{id}.{fields[field]} native={nativeOffset} managed={managedOffset}");
                }
            }
        }

        private static SbRelationStateV2[] ReadRelationStateBuffer(long space, long actor, int capacity, int structSize, out int count)
        {
            IntPtr buffer = Marshal.AllocHGlobal(structSize * capacity);
            try
            {
                for (int i = 0; i < capacity; ++i)
                {
                    var row = new SbRelationStateV2
                    {
                        struct_size = (uint)structSize,
                        version = ObservabilityVersion,
                        encoded_state = new double[8]
                    };
                    Marshal.StructureToPtr(row, IntPtr.Add(buffer, i * structSize), false);
                }
                count = useDebugLibrary
                    ? RelationDebug.sb_get_relation_states_v2(space, actor, buffer, capacity, (uint)structSize)
                    : RelationRelease.sb_get_relation_states_v2(space, actor, buffer, capacity, (uint)structSize);
                if (count < 0) return null;
                int copy = Math.Min(count, capacity);
                var rows = new SbRelationStateV2[copy];
                for (int i = 0; i < copy; ++i)
                {
                    rows[i] = Marshal.PtrToStructure<SbRelationStateV2>(IntPtr.Add(buffer, i * structSize));
                }
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int RelationCallbackCount
        {
            get
            {
                lock (callbackLock)
                {
                    return relationEventCallbacks.Count;
                }
            }
        }

        private static void ForgetRelationCallbacks()
        {
            lock (callbackLock)
            {
                relationEventCallbacks.Clear();
                relationEventCallbackTokens.Clear();
                relationCallbackSpaceByToken.Clear();
            }
        }

        private static bool DropRelationCallback(SpaceID space)
        {
            lock (callbackLock)
            {
                bool had = relationEventCallbacks.Remove(space);
                if (relationEventCallbackTokens.TryGetValue(space, out long token))
                {
                    relationCallbackSpaceByToken.Remove(token);
                }
                relationEventCallbackTokens.Remove(space);
                return had;
            }
        }

        private static void ReleaseRelationCallbackNative(SpaceID space)
        {
            if (useDebugLibrary) RelationDebug.sb_register_on_relation_event(space, null, IntPtr.Zero);
            else RelationRelease.sb_register_on_relation_event(space, null, IntPtr.Zero);
        }

        [MonoPInvokeCallback(typeof(OnRelationEventNativeDelegate))]
        private static void OnRelationEventWrapper(IntPtr eventPtr, IntPtr userData)
        {
            if (eventPtr == IntPtr.Zero ||
                !TryGetRelationCallback(userData, out SpaceID space, out OnRelationEventDelegate callback))
            {
                return;
            }
            SbRelationEvent native = Marshal.PtrToStructure<SbRelationEvent>(eventPtr);
            string anchor = native.anchor_name == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(native.anchor_name);
            callback(space, new RelationEvent(
                native.actor_shared_id, anchor, native.referent_shared_id,
                (SbKernelType)native.kernel_type,
                (SbRelationPhase)native.from_phase, (SbRelationPhase)native.to_phase,
                native.timestamp));
        }

        private static bool TryGetRelationCallback(IntPtr userData, out SpaceID space, out OnRelationEventDelegate callback)
        {
            long token = userData.ToInt64();
            lock (callbackLock)
            {
                if (token != 0 &&
                    relationCallbackSpaceByToken.TryGetValue(token, out space) &&
                    relationEventCallbackTokens.TryGetValue(space, out long current) &&
                    current == token &&
                    relationEventCallbacks.TryGetValue(space, out callback) &&
                    callback != null)
                {
                    return true;
                }
            }
            space = 0;
            callback = null;
            return false;
        }
    }
}
