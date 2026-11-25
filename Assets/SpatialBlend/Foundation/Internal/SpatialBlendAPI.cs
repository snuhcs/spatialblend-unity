// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using AOT;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpatialBlend.Internal
{
    using ActorID = System.Int64;
    using ElementID = System.Int64;
    using EventHandle = System.Int64;
    using SessionID = System.Int64;
    using SpaceID = System.Int64;

    internal enum SbGeometryRole : int
    {
        Unspecified = 0,
        Navigable = 1,
        Containment = 2,
        Obstacle = 3,
        Surface = 4,
        InteractionRegion = 5
    }

    internal enum SbKernelType : int
    {
        Spacing = 0,
        Facing = 1,
        AffordancePose = 2
    }

    // Primitive collider shapes handed to the core to auto-derive an element's
    // Containment envelope (its enclosing footprint) when no explicit boundary is set.
    internal enum SbColliderType : int
    {
        Box = 0,
        Sphere = 1,
        Capsule = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SbActorState
    {
        public double position_x;
        public double position_y;
        public double yaw;
    }

    // MUST mirror sb_realization_target_t in spatialblend_type.h EXACTLY. The
    // runtime layout check below validates every field offset before this type is
    // used to marshal an array.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbRealizationTarget
    {
        public double arrival_time;
        public IntPtr anchor_name;
        public double position_x;
        public double position_y;
        public double position_z;
        public double yaw;
        public double weight;
        public int has_z;
        public SbKernelType kernel_type;
        public long referent_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SbV2AbiLayout
    {
        public uint descriptor_size;
        public uint abi_version;
        public uint realization_target_size;
        public uint pointer_size;
        public uint kernel_type_size;
        public uint arrival_time_offset;
        public uint anchor_name_offset;
        public uint position_x_offset;
        public uint position_y_offset;
        public uint position_z_offset;
        public uint yaw_offset;
        public uint weight_offset;
        public uint has_z_offset;
        public uint kernel_type_offset;
        public uint referent_id_offset;
        /// <summary>ABI 3.1 (<c>sb_abi_minor_version()</c>): the minor version; this binding needs at least <see cref="SpatialBlendAPI.RequiredAbiMinor"/>.</summary>
        public uint abi_minor;
    }

    internal struct RealizationPipelineKey : IEquatable<RealizationPipelineKey>, IComparable<RealizationPipelineKey>
    {
        public readonly string AnchorName;
        public readonly SbKernelType KernelType;
        public readonly long ReferentId;

        public RealizationPipelineKey(string anchorName, SbKernelType kernelType, long referentId)
        {
            AnchorName = anchorName ?? string.Empty;
            KernelType = kernelType;
            ReferentId = referentId;
        }

        public bool Equals(RealizationPipelineKey other)
        {
            return string.Equals(AnchorName, other.AnchorName, StringComparison.Ordinal)
                && KernelType == other.KernelType
                && ReferentId == other.ReferentId;
        }

        public override bool Equals(object obj)
        {
            return obj is RealizationPipelineKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(AnchorName ?? string.Empty);
                hash = (hash * 397) ^ (int)KernelType;
                hash = (hash * 397) ^ ReferentId.GetHashCode();
                return hash;
            }
        }

        public int CompareTo(RealizationPipelineKey other)
        {
            int anchorComparison = string.Compare(
                AnchorName, other.AnchorName, StringComparison.Ordinal);
            if (anchorComparison != 0)
            {
                return anchorComparison;
            }

            int kernelComparison = KernelType.CompareTo(other.KernelType);
            return kernelComparison != 0
                ? kernelComparison
                : ReferentId.CompareTo(other.ReferentId);
        }
    }

    internal struct RealizationTarget
    {
        public double ArrivalTime;
        public string AnchorName;
        public Vector3 Position;
        public Quaternion Rotation;
        // True for body-anchor (hand/pelvis) targets whose Position.y carries a
        // real height; false for root locomotion targets (ground-projected).
        public bool HasHeight;
        public SbKernelType KernelType;
        public long ReferentId;
        public double Weight;

        public RealizationPipelineKey PipelineKey =>
            new RealizationPipelineKey(AnchorName, KernelType, ReferentId);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnTargetsNativeDelegate(
        SpaceID space,
        ActorID actor,
        IntPtr targets,
        long numTargets,
        IntPtr userData);

    internal delegate void OnTargetsDelegate(
        SpaceID space,
        ActorID actor,
        IReadOnlyList<RealizationTarget> targets);

    // Native callback fired when a peer-shared element is received (sb_share_element).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnElementAddedNativeDelegate(
        SpaceID space,
        ElementID localElement,
        long sharedId,
        IntPtr tag,
        IntPtr userData,
        long userDataSize,
        IntPtr cbUserData);

    internal delegate void OnElementAddedDelegate(
        SpaceID space,
        ElementID localElement,
        long sharedId,
        string tag,
        IntPtr userData,
        long userDataSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnElementRemovedNativeDelegate(
        SpaceID space,
        ElementID localElement,
        IntPtr cbUserData);

    internal delegate void OnElementRemovedDelegate(SpaceID space, ElementID localElement);

    internal static partial class SpatialBlendAPI
    {
        [Flags]
        private enum CallbackKinds
        {
            None = 0,
            Targets = 1,
            ElementAdded = 2,
            ElementRemoved = 4,
            RelationEvent = 8,
            LinkRemoved = 16
        }
        private static volatile bool useDebugLibrary;
        // Library selection is process-wide P/Invoke state. Sessions created
        // here pin that selection until their native destruction completes.
        // Never hold this lock while calling native code: teardown may drain
        // callbacks that re-enter managed code.
        private static readonly object sessionLock = new object();
        private static readonly Dictionary<SessionID, bool> managedSessions =
            new Dictionary<SessionID, bool>();
        // Space ownership is needed to discard only the destroyed session's
        // managed callback capabilities after native teardown. A different
        // session's callbacks must survive that cleanup.
        private static readonly Dictionary<SpaceID, SessionID> managedSpaceSessions =
            new Dictionary<SpaceID, SessionID>();
        // Delegate maps retain their historical shape for ABI/reflection consumers.
        // Separate monotonically-issued tokens are passed to native as opaque
        // cbUserData, so a stale callback from a destroyed/reused SpaceID cannot
        // select a replacement delegate.
        private static readonly object callbackLock = new object();
        private static long nextCallbackToken;
        private static readonly Dictionary<SpaceID, OnTargetsDelegate> targetCallbacks = new Dictionary<SpaceID, OnTargetsDelegate>();
        private static readonly Dictionary<SpaceID, long> targetCallbackTokens = new Dictionary<SpaceID, long>();
        private static readonly OnTargetsNativeDelegate onTargetsWrapper = OnTargetsWrapper;
        private static readonly Dictionary<SpaceID, OnElementAddedDelegate> elementAddedCallbacks = new Dictionary<SpaceID, OnElementAddedDelegate>();
        private static readonly Dictionary<SpaceID, long> elementAddedCallbackTokens = new Dictionary<SpaceID, long>();
        private static readonly OnElementAddedNativeDelegate onElementAddedWrapper = OnElementAddedWrapper;
        private static readonly Dictionary<SpaceID, OnElementRemovedDelegate> elementRemovedCallbacks = new Dictionary<SpaceID, OnElementRemovedDelegate>();
        private static readonly Dictionary<SpaceID, long> elementRemovedCallbackTokens = new Dictionary<SpaceID, long>();
        private static readonly OnElementRemovedNativeDelegate onElementRemovedWrapper = OnElementRemovedWrapper;

        private static class Release
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_v2_abi_layout(ref SbV2AbiLayout layout, uint layoutSize);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern SessionID sb_create_session();
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_destroy_session(SessionID session);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern SpaceID sb_add_space(SessionID session);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_host_space(SpaceID space, int port);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_connect_space(SpaceID space, string host, int port);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_try_wait(EventHandle handle, int timeoutMs);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr sb_get_error(EventHandle handle);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ElementID sb_add_element(SpaceID space, ElementID parent);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_transform(SpaceID space, ElementID elem, double[] matrix4x4);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_element_shared_id(SpaceID space, ElementID elem, long sharedId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_element_tag(SpaceID space, ElementID elem, string tag);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_polygon(SpaceID space, ElementID elem, SbGeometryRole role, double[] pointsXy, long numPoints);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_box(SpaceID space, ElementID elem, SbGeometryRole role, double minX, double minY, double minZ, double maxX, double maxY, double maxZ);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_collider(SpaceID space, ElementID elem, SbColliderType type, double[] paramsArr, long numParams);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ElementID sb_find_element_by_shared_id(SpaceID space, long sharedId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_add_element_anchor(SpaceID space, ElementID elem, string name, double[] matrix4x4);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_share_element(SpaceID space, ElementID elem, IntPtr userData, long userDataSize);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_unshare_element(SpaceID space, ElementID elem);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ActorID sb_create_actor(SpaceID space);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_add_anchor(SpaceID space, ActorID actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double x, double y, double z, [MarshalAs(UnmanagedType.LPUTF8Str)] string parent);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_actor_element(SpaceID space, ActorID actor, ElementID elem);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ActorID sb_find_actor_by_shared_id(SpaceID space, long sharedId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_update_actor_state(SpaceID space, ActorID actor, double x, double y, double yaw);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern SbActorState sb_get_actor_state(SpaceID space, ActorID actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_bind(SpaceID space, ActorID actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, SbKernelType kernelType);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_bind_affordance(SpaceID space, ActorID actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_targets(SpaceID space, OnTargetsNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_element_added(SpaceID space, OnElementAddedNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_element_removed(SpaceID space, OnElementRemovedNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_poll_realization_targets(SpaceID space, ActorID actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_update_interval(SpaceID space, double seconds);

            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_resolve_shared_element(SpaceID space, ElementID received, ElementID resolved);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_cleanup_all();
        }

        private static class DebugApi
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_v2_abi_layout(ref SbV2AbiLayout layout, uint layoutSize);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern SessionID sb_create_session();
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_destroy_session(SessionID session);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern SpaceID sb_add_space(SessionID session);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_host_space(SpaceID space, int port);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_connect_space(SpaceID space, string host, int port);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_try_wait(EventHandle handle, int timeoutMs);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr sb_get_error(EventHandle handle);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ElementID sb_add_element(SpaceID space, ElementID parent);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_transform(SpaceID space, ElementID elem, double[] matrix4x4);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_element_shared_id(SpaceID space, ElementID elem, long sharedId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_element_tag(SpaceID space, ElementID elem, string tag);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_polygon(SpaceID space, ElementID elem, SbGeometryRole role, double[] pointsXy, long numPoints);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_box(SpaceID space, ElementID elem, SbGeometryRole role, double minX, double minY, double minZ, double maxX, double maxY, double maxZ);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_collider(SpaceID space, ElementID elem, SbColliderType type, double[] paramsArr, long numParams);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ElementID sb_find_element_by_shared_id(SpaceID space, long sharedId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_add_element_anchor(SpaceID space, ElementID elem, string name, double[] matrix4x4);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_share_element(SpaceID space, ElementID elem, IntPtr userData, long userDataSize);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_unshare_element(SpaceID space, ElementID elem);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ActorID sb_create_actor(SpaceID space);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_add_anchor(SpaceID space, ActorID actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double x, double y, double z, [MarshalAs(UnmanagedType.LPUTF8Str)] string parent);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_actor_element(SpaceID space, ActorID actor, ElementID elem);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ActorID sb_find_actor_by_shared_id(SpaceID space, long sharedId);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_update_actor_state(SpaceID space, ActorID actor, double x, double y, double yaw);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern SbActorState sb_get_actor_state(SpaceID space, ActorID actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_bind(SpaceID space, ActorID actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, SbKernelType kernelType);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_bind_affordance(SpaceID space, ActorID actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string anchorName, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_targets(SpaceID space, OnTargetsNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_element_added(SpaceID space, OnElementAddedNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_element_removed(SpaceID space, OnElementRemovedNativeDelegate callback, IntPtr userData);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_poll_realization_targets(SpaceID space, ActorID actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_set_update_interval(SpaceID space, double seconds);

            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_resolve_shared_element(SpaceID space, ElementID received, ElementID resolved);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_cleanup_all();
        }

        public static void Initialize(bool useDebugLibraries = false)
        {
            bool previousSelection;
            lock (sessionLock)
            {
                if (managedSessions.Count != 0 && useDebugLibrary != useDebugLibraries)
                {
                    throw new InvalidOperationException(
                        "Cannot switch the SpatialBlend release/debug libraries while a managed session is live.");
                }
                previousSelection = useDebugLibrary;
                useDebugLibrary = useDebugLibraries;
            }

            try
            {
                ValidateNativeAbi();
            }
            catch
            {
                lock (sessionLock)
                {
                    useDebugLibrary = previousSelection;
                }
                throw;
            }
        }

        internal static SbV2AbiLayout GetNativeAbiLayout()
        {
            SbV2AbiLayout layout = default;
            uint managedDescriptorSize = (uint)Marshal.SizeOf<SbV2AbiLayout>();
            uint requiredSize = useDebugLibrary
                ? DebugApi.sb_get_v2_abi_layout(ref layout, managedDescriptorSize)
                : Release.sb_get_v2_abi_layout(ref layout, managedDescriptorSize);
            if (requiredSize != managedDescriptorSize)
            {
                throw new InvalidOperationException(
                    $"SpatialBlend core ABI descriptor size mismatch: native requires {requiredSize} bytes, " +
                    $"managed provides {managedDescriptorSize} bytes.");
            }
            return layout;
        }

        /// <summary>The ABI major version this binding mirrors: 4 (one proposal per anchor, per-anchor ports, actor events).</summary>
        internal const uint ExpectedAbiMajor = 4;

        /// <summary>The ABI 4 minor version this binding needs: 4.11 (4.1 added the POSITION_UNSPECIFIED proposal flag, 4.2 removed the scope and tag arguments of the bind API, 4.3 added the TRANSFER proposal flag and removed the contained event, 4.4 added the source-side transfer fact and moved shape congruence into the flag, 4.5 added the tunneled core link). 4.11 changed what a proposal MEANS and the embodied driver depends on it: every proposal is the anchor's pursued target (root, pelvis, hands alike), a hold is the current position, POSITION_UNSPECIFIED is no longer emitted, and the first proposal is placed. A core older than 4.11 would send absolute targets and POSITION_UNSPECIFIED holds that this driver no longer reads, so it is refused here rather than driven wrongly. 4.11 also added sb_set_element_affordance_pose, sb_read_anchor_solve, the INNER / FACING_YAW proposal flags and the FOLD cause. 4.12 added sb_read_anchor_turn (the signed, unwrapped yaw turn of a proposal, which may exceed pi): the driver turns by it instead of by the shortest arc to the absolute yaw, so a core older than 4.12 is refused too.</summary>
        internal const uint RequiredAbiMinor = 12;

        internal static List<string> GetAbiLayoutMismatches(SbV2AbiLayout native)
        {
            const uint ExpectedAbiVersion = ExpectedAbiMajor;
            var mismatches = new List<string>();

            AddAbiMismatch(mismatches, "descriptor_size", native.descriptor_size,
                (uint)Marshal.SizeOf<SbV2AbiLayout>());
            AddAbiMismatch(mismatches, "abi_version", native.abi_version,
                ExpectedAbiVersion);
            if (native.abi_minor < RequiredAbiMinor)
            {
                mismatches.Add($"abi_minor: native={native.abi_minor} required>={RequiredAbiMinor}");
            }
            AddAbiMismatch(mismatches, "realization_target_size",
                native.realization_target_size,
                (uint)Marshal.SizeOf<SbRealizationTarget>());
            AddAbiMismatch(mismatches, "pointer_size", native.pointer_size,
                (uint)IntPtr.Size);
            AddAbiMismatch(mismatches, "kernel_type_size",
                native.kernel_type_size, sizeof(int));
            AddAbiOffsetMismatch(mismatches, "arrival_time",
                native.arrival_time_offset);
            AddAbiOffsetMismatch(mismatches, "anchor_name",
                native.anchor_name_offset);
            AddAbiOffsetMismatch(mismatches, "position_x",
                native.position_x_offset);
            AddAbiOffsetMismatch(mismatches, "position_y",
                native.position_y_offset);
            AddAbiOffsetMismatch(mismatches, "position_z",
                native.position_z_offset);
            AddAbiOffsetMismatch(mismatches, "yaw", native.yaw_offset);
            AddAbiOffsetMismatch(mismatches, "weight",
                native.weight_offset);
            AddAbiOffsetMismatch(mismatches, "has_z", native.has_z_offset);
            AddAbiOffsetMismatch(mismatches, "kernel_type",
                native.kernel_type_offset);
            AddAbiOffsetMismatch(mismatches, "referent_id",
                native.referent_id_offset);
            return mismatches;
        }

        private static void ValidateNativeAbi()
        {
            SbV2AbiLayout native;
            try
            {
                native = GetNativeAbiLayout();
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new InvalidOperationException(
                    "Loaded libspatialblend does not expose sb_get_v2_abi_layout. " +
                    "Install a native library built from the matching core headers.",
                    exception);
            }

            List<string> mismatches = GetAbiLayoutMismatches(native);
            if (mismatches.Count > 0)
            {
                throw new InvalidOperationException(
                    "SpatialBlend core native/managed ABI mismatch:\n" +
                    string.Join("\n", mismatches));
            }
        }

        private static void AddAbiOffsetMismatch(
            List<string> mismatches, string fieldName, uint nativeOffset)
        {
            uint managedOffset = (uint)Marshal.OffsetOf<SbRealizationTarget>(
                fieldName).ToInt64();
            AddAbiMismatch(mismatches, fieldName + "_offset", nativeOffset,
                managedOffset);
        }

        private static void AddAbiMismatch(
            List<string> mismatches, string name, uint nativeValue,
            uint managedValue)
        {
            if (nativeValue != managedValue)
            {
                mismatches.Add(
                    $"{name}: native={nativeValue}, managed={managedValue}");
            }
        }

        public static SessionID CreateSession()
        {
            // Creation cannot dispatch application callbacks, so it can be
            // serialized with library selection to pin the returned session.
            lock (sessionLock)
            {
                bool debug = useDebugLibrary;
                SessionID session = debug ? DebugApi.sb_create_session() : Release.sb_create_session();
                if (session != 0)
                {
                    managedSessions[session] = debug;
                }
                return session;
            }
        }

        public static void DestroySession(SessionID session)
        {
            bool debug;
            bool tracked;
            lock (sessionLock)
            {
                tracked = managedSessions.TryGetValue(session, out debug);
                if (!tracked) debug = useDebugLibrary;
            }

            // Do not hold sessionLock during native destruction: it may drain
            // native callbacks into managed code.
            if (debug) DebugApi.sb_destroy_session(session);
            else Release.sb_destroy_session(session);

            var removedSpaces = new List<SpaceID>();
            lock (sessionLock)
            {
                if (tracked) managedSessions.Remove(session);
                foreach (KeyValuePair<SpaceID, SessionID> item in managedSpaceSessions)
                {
                    if (item.Value == session) removedSpaces.Add(item.Key);
                }
                foreach (SpaceID space in removedSpaces) managedSpaceSessions.Remove(space);
            }
            foreach (SpaceID space in removedSpaces)
            {
                InvalidateCallbacksForSpace(space);
                ForgetRichCallbackForSpace(space);
            }
        }
        public static SpaceID AddSpace(SessionID session)
        {
            // Like session creation, allocating a space cannot invoke an app
            // callback. Keep its library choice and ownership record together.
            lock (sessionLock)
            {
                bool debug;
                if (!managedSessions.TryGetValue(session, out debug)) debug = useDebugLibrary;
                SpaceID space = debug ? DebugApi.sb_add_space(session) : Release.sb_add_space(session);
                if (space != 0) managedSpaceSessions[space] = session;
                return space;
            }
        }
        public static EventHandle HostSpace(SpaceID space, int port) => useDebugLibrary ? DebugApi.sb_host_space(space, port) : Release.sb_host_space(space, port);
        public static EventHandle ConnectSpace(SpaceID space, string host, int port) => useDebugLibrary ? DebugApi.sb_connect_space(space, host, port) : Release.sb_connect_space(space, host, port);
        public static bool TryWait(EventHandle handle, int timeoutMs) => (useDebugLibrary ? DebugApi.sb_try_wait(handle, timeoutMs) : Release.sb_try_wait(handle, timeoutMs)) != 0;
        public static string GetError(EventHandle handle)
        {
            IntPtr ptr = useDebugLibrary ? DebugApi.sb_get_error(handle) : Release.sb_get_error(handle);
            return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr);
        }

        public static ElementID AddElement(SpaceID space, ElementID parent = 0) => useDebugLibrary ? DebugApi.sb_add_element(space, parent) : Release.sb_add_element(space, parent);
        /// <returns>False when the core refused the transform (logged with its message, see NoteSceneStatus).</returns>
        public static bool SetElementTransform(SpaceID space, ElementID elem, Matrix4x4 unityTransform) => CallSetElementTransform(space, elem, unityTransform);
        public static void SetElementSharedId(SpaceID space, ElementID elem, long sharedId) { if (useDebugLibrary) DebugApi.sb_set_element_shared_id(space, elem, sharedId); else Release.sb_set_element_shared_id(space, elem, sharedId); }
        public static void SetElementTag(SpaceID space, ElementID elem, string tag) { if (useDebugLibrary) DebugApi.sb_set_element_tag(space, elem, tag ?? string.Empty); else Release.sb_set_element_tag(space, elem, tag ?? string.Empty); }
        public static ElementID FindElementBySharedId(SpaceID space, long sharedId) => useDebugLibrary ? DebugApi.sb_find_element_by_shared_id(space, sharedId) : Release.sb_find_element_by_shared_id(space, sharedId);
        /// <summary>
        /// FRAME (spatialblend.h, ABI 3.1): the points are in the ELEMENT's local frame (a root element's local frame is the
        /// world), Unity x and z as the Z-up x and y. The one exception is <see cref="SbGeometryRole.Navigable"/>, which the core
        /// reads as WORLD coordinates: a walkable ring under the root room is both, a nested walkable zone must be authored
        /// in world coordinates and re-issued when an ancestor moves.
        /// </summary>
        /// <returns>False when the core refused the polygon (logged with its message, see NoteSceneStatus), or when there
        /// was no polygon to write.</returns>
        public static bool SetElementPolygon(SpaceID space, ElementID elem, SbGeometryRole role, IReadOnlyList<Vector2> unityPoints)
        {
            if (unityPoints == null || unityPoints.Count < 3) return false;
            if (unityPoints.Count > MaxSceneVertices)
                throw new ArgumentOutOfRangeException(nameof(unityPoints),
                    "A polygon cannot exceed the native limit of 65536 vertices.");
            double[] points = new double[unityPoints.Count * 2];
            for (int i = 0; i < unityPoints.Count; ++i)
            {
                points[i * 2] = unityPoints[i].x;
                points[i * 2 + 1] = unityPoints[i].y;
            }
            return NoteSceneStatus(space, useDebugLibrary ? DebugApi.sb_set_element_polygon(space, elem, role, points, unityPoints.Count)
                : Release.sb_set_element_polygon(space, elem, role, points, unityPoints.Count), "sb_set_element_polygon", elem)
                == SbSceneStatus.Ok;
        }
        /// <summary>FRAME: element-local, axis-aligned in the element's frame (ABI 3.1).</summary>
        public static void SetElementBox(SpaceID space, ElementID elem, SbGeometryRole role, Vector3 unityMin, Vector3 unityMax)
        {
            CoordinateConverter.UnityToSpatialBlend3D(unityMin, out double x0, out double y0, out double z0);
            CoordinateConverter.UnityToSpatialBlend3D(unityMax, out double x1, out double y1, out double z1);
            double minX = Math.Min(x0, x1);
            double minY = Math.Min(y0, y1);
            double minZ = Math.Min(z0, z1);
            double maxX = Math.Max(x0, x1);
            double maxY = Math.Max(y0, y1);
            double maxZ = Math.Max(z0, z1);
            NoteSceneStatus(space, useDebugLibrary ? DebugApi.sb_set_element_box(space, elem, role, minX, minY, minZ, maxX, maxY, maxZ)
                : Release.sb_set_element_box(space, elem, role, minX, minY, minZ, maxX, maxY, maxZ), "sb_set_element_box", elem);
        }
        // Hand the core a primitive collider so it can auto-derive the element's Containment envelope (footprint)
        // when no explicit boundary is set. FRAME: the parameters are ELEMENT-LOCAL (spatialblend.h), in Z-up. They
        // are the element's own local values only when the collider sits on the element's transform. A collider on a
        // child (a GetComponentInChildren pick) has its own offset and scale, so pass the element's transform as
        // <paramref name="elementFrame"/> and the parameters are composed into it. The envelope is axis-aligned in
        // the element frame: a rotation between the collider and the element is not represented.
        public static void SetElementCollider(SpaceID space, ElementID elem, Collider collider, Transform elementFrame = null)
        {
            if (!TryBuildColliderParams(collider, elementFrame, out SbColliderType type, out double[] p)) return;
            NoteSceneStatus(space, useDebugLibrary ? DebugApi.sb_set_element_collider(space, elem, type, p, p.Length)
                : Release.sb_set_element_collider(space, elem, type, p, p.Length), "sb_set_element_collider", elem);
        }

        // The collider primitive in the element's local frame. With no frame, or the collider on the element's own
        // transform, these are the collider's own local values.
        internal static bool TryBuildColliderParams(Collider collider, Transform elementFrame, out SbColliderType type,
                                                    out double[] p)
        {
            type = SbColliderType.Box;
            p = null;
            if (collider == null) return false;
            Transform ct = collider.transform;
            bool composed = elementFrame != null && elementFrame != ct;
            // Collider-local point and size to element-local: through the element's inverse transform.
            Vector3 ToFramePoint(Vector3 local) => composed ? elementFrame.InverseTransformPoint(ct.TransformPoint(local)) : local;
            Vector3 relativeScale = composed ? DivideScale(ct.lossyScale, elementFrame.lossyScale) : Vector3.one;
            if (collider is BoxCollider box)
            {
                CoordinateConverter.UnityToSpatialBlend3D(ToFramePoint(box.center), out double cx, out double cy, out double cz);
                CoordinateConverter.UnityToSpatialBlend3D(Vector3.Scale(box.size, relativeScale), out double sx, out double sy, out double sz);
                type = SbColliderType.Box;
                p = new double[] { cx, cy, cz, Math.Abs(sx), Math.Abs(sy), Math.Abs(sz) };
            }
            else if (collider is SphereCollider sphere)
            {
                CoordinateConverter.UnityToSpatialBlend3D(ToFramePoint(sphere.center), out double cx, out double cy, out double cz);
                type = SbColliderType.Sphere;
                float scale = Mathf.Max(Mathf.Abs(relativeScale.x), Mathf.Abs(relativeScale.y), Mathf.Abs(relativeScale.z));
                p = new double[] { cx, cy, cz, Math.Abs(sphere.radius) * scale };
            }
            else if (collider is CapsuleCollider cap)
            {
                CoordinateConverter.UnityToSpatialBlend3D(ToFramePoint(cap.center), out double cx, out double cy, out double cz);
                // Unity direction: 0=X, 1=Y(up), 2=Z(fwd) -> SpatialBlend axis: X=0, Z(up)=2, Y(fwd)=1.
                int axis = cap.direction == 1 ? 2 : (cap.direction == 2 ? 1 : 0);
                float along = Mathf.Abs(cap.direction == 0 ? relativeScale.x : cap.direction == 1 ? relativeScale.y : relativeScale.z);
                float across = Mathf.Max(
                    Mathf.Abs(cap.direction == 0 ? relativeScale.y : relativeScale.x),
                    Mathf.Abs(cap.direction == 2 ? relativeScale.y : relativeScale.z));
                type = SbColliderType.Capsule;
                p = new double[] { cx, cy, cz, Math.Abs(cap.radius) * across, Math.Abs(cap.height) * along, axis };
            }
            else
            {
                // MeshCollider / other: the collider's world AABB, expressed in the element's frame (the collider's own
                // frame when no element frame is given, as before).
                Transform frame = elementFrame != null ? elementFrame : ct;
                Bounds w = collider.bounds;
                Vector3 lc = frame.InverseTransformPoint(w.center);
                Vector3 ls = frame.InverseTransformVector(w.size);
                CoordinateConverter.UnityToSpatialBlend3D(lc, out double cx, out double cy, out double cz);
                CoordinateConverter.UnityToSpatialBlend3D(ls, out double sx, out double sy, out double sz);
                type = SbColliderType.Box;
                p = new double[] { cx, cy, cz, Math.Abs(sx), Math.Abs(sy), Math.Abs(sz) };
            }
            return true;
        }

        private static Vector3 DivideScale(Vector3 a, Vector3 b) => new Vector3(
            Mathf.Abs(b.x) > 1e-6f ? a.x / b.x : a.x,
            Mathf.Abs(b.y) > 1e-6f ? a.y / b.y : a.y,
            Mathf.Abs(b.z) > 1e-6f ? a.z / b.z : a.z);

        public static void AddElementAnchor(SpaceID space, ElementID elem, string name, Matrix4x4 localTransform)
        {
            double[] matrix = ToColumnMajorSbMatrix(localTransform);
            if (useDebugLibrary) DebugApi.sb_add_element_anchor(space, elem, name, matrix);
            else Release.sb_add_element_anchor(space, elem, name, matrix);
        }
        public static void ShareElement(SpaceID space, ElementID elem) => ShareElement(space, elem, IntPtr.Zero, 0);
        public static void ShareElement(SpaceID space, ElementID elem, IntPtr userData, long userDataSize)
        {
            if (useDebugLibrary) DebugApi.sb_share_element(space, elem, userData, userDataSize);
            else Release.sb_share_element(space, elem, userData, userDataSize);
        }
        public static void UnshareElement(SpaceID space, ElementID elem) { if (useDebugLibrary) DebugApi.sb_unshare_element(space, elem); else Release.sb_unshare_element(space, elem); }
        public static ActorID CreateActor(SpaceID space) => useDebugLibrary ? DebugApi.sb_create_actor(space) : Release.sb_create_actor(space);
        public static void AddAnchor(SpaceID space, ActorID actor, string name, Vector3 localOffset, string parent = null)
        {
            CoordinateConverter.UnityToSpatialBlend3D(localOffset, out double x, out double y, out double z);
            if (useDebugLibrary) DebugApi.sb_add_anchor(space, actor, name, x, y, z, parent);
            else Release.sb_add_anchor(space, actor, name, x, y, z, parent);
        }
        public static void SetActorElement(SpaceID space, ActorID actor, ElementID elem) { if (useDebugLibrary) DebugApi.sb_set_actor_element(space, actor, elem); else Release.sb_set_actor_element(space, actor, elem); }
        public static ActorID FindActorBySharedId(SpaceID space, long sharedId) => useDebugLibrary ? DebugApi.sb_find_actor_by_shared_id(space, sharedId) : Release.sb_find_actor_by_shared_id(space, sharedId);
        public static EventHandle UpdateActorState(SpaceID space, ActorID actor, Vector3 unityPosition, float unityYawDegrees)
        {
            CoordinateConverter.UnityToSpatialBlend2D(unityPosition, out double x, out double y);
            double yaw = CoordinateConverter.UnityToSpatialBlendAngle(unityYawDegrees) * Math.PI / 180.0;
            return useDebugLibrary ? DebugApi.sb_update_actor_state(space, actor, x, y, yaw) : Release.sb_update_actor_state(space, actor, x, y, yaw);
        }

        /// <summary>ABI 4.2: binds a Spacing or Facing relation. Scope belongs to the family, so there is no scope or tag argument.</summary>
        public static bool Bind(SpaceID space, ActorID actor, string anchorName, SbKernelType kernelType)
            => (useDebugLibrary ? DebugApi.sb_bind(space, actor, anchorName, kernelType) : Release.sb_bind(space, actor, anchorName, kernelType)) != 0;
        /// <summary>The AffordancePose binding; the tag (e.g. "Sittable") survives only here. A null tag binds every affordance of the parent object's region.</summary>
        public static bool BindAffordance(SpaceID space, ActorID actor, string anchorName, string tag = null)
            => (useDebugLibrary ? DebugApi.sb_bind_affordance(space, actor, anchorName, tag ?? string.Empty)
                : Release.sb_bind_affordance(space, actor, anchorName, tag ?? string.Empty)) != 0;
        public static void RegisterOnTargets(SpaceID space, OnTargetsDelegate callback)
        {
            long token = RegisterCallback(targetCallbacks, targetCallbackTokens, space, callback);
            try
            {
                if (useDebugLibrary) DebugApi.sb_register_on_targets(space, onTargetsWrapper, new IntPtr(token));
                else Release.sb_register_on_targets(space, onTargetsWrapper, new IntPtr(token));
            }
            catch
            {
                InvalidateCallback(targetCallbacks, targetCallbackTokens, space, token);
                throw;
            }
        }
        public static bool PollRealizationTargets(SpaceID space, ActorID actor) => (useDebugLibrary ? DebugApi.sb_poll_realization_targets(space, actor) : Release.sb_poll_realization_targets(space, actor)) != 0;
        public static void RegisterOnElementAdded(SpaceID space, OnElementAddedDelegate callback)
        {
            long token = RegisterCallback(elementAddedCallbacks, elementAddedCallbackTokens, space, callback);
            try
            {
                if (useDebugLibrary) DebugApi.sb_register_on_element_added(space, onElementAddedWrapper, new IntPtr(token));
                else Release.sb_register_on_element_added(space, onElementAddedWrapper, new IntPtr(token));
            }
            catch
            {
                InvalidateCallback(elementAddedCallbacks, elementAddedCallbackTokens, space, token);
                throw;
            }
        }
        public static bool TryRegisterOnElementRemoved(SpaceID space, OnElementRemovedDelegate callback)
        {
            if (callback == null)
            {
                return false;
            }

            long token = RegisterCallback(elementRemovedCallbacks, elementRemovedCallbackTokens, space, callback);
            try
            {
                if (useDebugLibrary) DebugApi.sb_register_on_element_removed(space, onElementRemovedWrapper, new IntPtr(token));
                else Release.sb_register_on_element_removed(space, onElementRemovedWrapper, new IntPtr(token));
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                InvalidateCallback(elementRemovedCallbacks, elementRemovedCallbackTokens, space, token);
                Debug.LogWarning("[SpatialBlend] sb_register_on_element_removed is unsupported by the loaded native library; element-removal callbacks are disabled for this space.");
                return false;
            }
            catch
            {
                InvalidateCallback(elementRemovedCallbacks, elementRemovedCallbackTokens, space, token);
                throw;
            }
        }

        /// <summary>Unregisters managed/native callbacks for one space only.</summary>
        public static void UnregisterCallbacks(SpaceID space)
        {
            CallbackKinds removed = InvalidateCallbacksForSpace(space);
            // An absent rich registration is a managed-only no-op, including
            // with an older ABI2 library that has no additive rich symbols.
            if (!TryUnregisterOnRichRealization(space, out string richError))
                Debug.LogWarning("[SpatialBlend] " + richError);

            // Managed invalidation precedes native null-unregister. Any late native
            // callback sees no matching token and cannot reach a replacement session.
            if ((removed & CallbackKinds.Targets) != 0)
            {
                if (useDebugLibrary) DebugApi.sb_register_on_targets(space, null, IntPtr.Zero);
                else Release.sb_register_on_targets(space, null, IntPtr.Zero);
            }
            if ((removed & CallbackKinds.ElementAdded) != 0)
            {
                if (useDebugLibrary) DebugApi.sb_register_on_element_added(space, null, IntPtr.Zero);
                else Release.sb_register_on_element_added(space, null, IntPtr.Zero);
            }
            if ((removed & CallbackKinds.ElementRemoved) != 0)
            {
                try
                {
                    if (useDebugLibrary) DebugApi.sb_register_on_element_removed(space, null, IntPtr.Zero);
                    else Release.sb_register_on_element_removed(space, null, IntPtr.Zero);
                }
                catch (EntryPointNotFoundException)
                {
                    Debug.LogWarning("[SpatialBlend] sb_register_on_element_removed is unsupported by the loaded native library; no native removal callback was registered.");
                }
            }
            if ((removed & CallbackKinds.RelationEvent) != 0)
            {
                ReleaseRelationCallbackNative(space);
            }
            if ((removed & CallbackKinds.LinkRemoved) != 0)
            {
                ReleaseLinkRemovedNative(space);
            }
        }

        private static CallbackKinds InvalidateCallbacksForSpace(SpaceID space)
        {
            lock (callbackLock)
            {
                CallbackKinds removed = CallbackKinds.None;
                if (targetCallbacks.Remove(space)) removed |= CallbackKinds.Targets;
                targetCallbackTokens.Remove(space);
                if (elementAddedCallbacks.Remove(space)) removed |= CallbackKinds.ElementAdded;
                elementAddedCallbackTokens.Remove(space);
                if (elementRemovedCallbacks.Remove(space)) removed |= CallbackKinds.ElementRemoved;
                elementRemovedCallbackTokens.Remove(space);
                if (DropRelationCallback(space)) removed |= CallbackKinds.RelationEvent;
                if (DropLinkRemovedCallback(space)) removed |= CallbackKinds.LinkRemoved;
                return removed;
            }
        }
        public static void SetUpdateInterval(SpaceID space, double seconds) { if (useDebugLibrary) DebugApi.sb_set_update_interval(space, seconds); else Release.sb_set_update_interval(space, seconds); }

        // Communicate the app's OnShare resolution back to the core (option B):
        // received = the temp element the core created for the incoming shared element;
        // resolved = the existing local element it links to (LinkToExisting) or `received`
        // itself (AddAsNew). On link the core removes the temp (no duplicate shared id)
        // and records the cross-space link in the ElementLinker.
        public static void ResolveSharedElement(SpaceID space, ElementID received, ElementID resolved) { if (useDebugLibrary) DebugApi.sb_resolve_shared_element(space, received, resolved); else Release.sb_resolve_shared_element(space, received, resolved); }
        // True while this domain holds native sessions or callback registrations,
        // i.e. while CleanupAll has something to release. Never loads the library.
        internal static bool HasLiveNativeState
        {
            get
            {
                lock (sessionLock)
                {
                    if (managedSessions.Count != 0) return true;
                }
                lock (callbackLock)
                {
                    return targetCallbacks.Count + elementAddedCallbacks.Count +
                           elementRemovedCallbacks.Count + RelationCallbackCount + linkRemovedCallbacks.Count != 0;
                }
            }
        }

        public static void CleanupAll()
        {
            lock (callbackLock)
            {
                targetCallbacks.Clear(); targetCallbackTokens.Clear();
                elementAddedCallbacks.Clear(); elementAddedCallbackTokens.Clear();
                elementRemovedCallbacks.Clear(); elementRemovedCallbackTokens.Clear();
                ForgetLinkRemovedCallbacks();
                ForgetRelationCallbacks();
            }
            bool debug = useDebugLibrary;
            if (debug) DebugApi.sb_cleanup_all(); else Release.sb_cleanup_all();
            // Native retirement may deliver a final empty snapshot first.
            // Forgetting the tokens also fences late callbacks which have not
            // entered managed code; it does not cancel already running code.
            ForgetAllRichCallbacks();
            lock (sessionLock)
            {
                managedSessions.Clear();
                managedSpaceSessions.Clear();
            }
        }

        private static long RegisterCallback<T>(Dictionary<SpaceID, T> callbacks,
                                                Dictionary<SpaceID, long> tokens,
                                                SpaceID space, T callback)
        {
            lock (callbackLock)
            {
                long token = ++nextCallbackToken;
                if (token == 0)
                {
                    token = ++nextCallbackToken;
                }
                callbacks[space] = callback;
                tokens[space] = token;
                return token;
            }
        }

        private static void InvalidateCallback<T>(Dictionary<SpaceID, T> callbacks,
                                                  Dictionary<SpaceID, long> tokens,
                                                  SpaceID space, long token)
        {
            lock (callbackLock)
            {
                if (tokens.TryGetValue(space, out long currentToken) && currentToken == token)
                {
                    callbacks.Remove(space);
                    tokens.Remove(space);
                }
            }
        }

        private static bool TryGetCallback<T>(Dictionary<SpaceID, T> callbacks,
                                              Dictionary<SpaceID, long> tokens,
                                              SpaceID space, IntPtr cbUserData, out T callback)
            where T : class
        {
            long token = cbUserData.ToInt64();
            lock (callbackLock)
            {
                if (token != 0 && tokens.TryGetValue(space, out long currentToken) &&
                    currentToken == token && callbacks.TryGetValue(space, out callback) &&
                    callback != null)
                {
                    return true;
                }
            }
            callback = null;
            return false;
        }

        private static bool CallSetElementTransform(SpaceID space, ElementID elem, Matrix4x4 unityTransform)
        {
            double[] matrix = ToRowMajorSbMatrix(unityTransform);
            return NoteSceneStatus(space, useDebugLibrary ? DebugApi.sb_set_element_transform(space, elem, matrix)
                : Release.sb_set_element_transform(space, elem, matrix), "sb_set_element_transform", elem) == SbSceneStatus.Ok;
        }

        private static double[] ToRowMajorSbMatrix(Matrix4x4 unityTransform)
        {
            Matrix4x4 sb = CoordinateConverter.UnityToSpatialBlendMatrix(unityTransform);
            double[] matrix = new double[16];
            int index = 0;
            for (int row = 0; row < 4; ++row)
            {
                for (int col = 0; col < 4; ++col)
                {
                    matrix[index++] = sb[row, col];
                }
            }
            return matrix;
        }

        private static double[] ToColumnMajorSbMatrix(Matrix4x4 unityTransform)
        {
            Matrix4x4 sb = CoordinateConverter.UnityToSpatialBlendAnchorMatrix(unityTransform);
            double[] matrix = new double[16];
            int index = 0;
            for (int col = 0; col < 4; ++col)
            {
                for (int row = 0; row < 4; ++row)
                {
                    matrix[index++] = sb[row, col];
                }
            }
            return matrix;
        }

        // Latches after the first invalid target so the error is logged once, not
        // every frame. Reset is unnecessary — a correct dylib never trips it.
        private static bool _warnedInvalidTarget = false;

        private const double MinTargetWeight = 0.0;
        private const double MaxTargetWeight = 1.0;

        private static bool IsFiniteTarget(SbRealizationTarget t)
        {
            return !double.IsNaN(t.yaw) && !double.IsInfinity(t.yaw)
                && IsRepresentablePosition(t.position_x)
                && IsRepresentablePosition(t.position_y)
                && IsRepresentablePosition(t.position_z)
                && !double.IsNaN(t.weight) && !double.IsInfinity(t.weight)
                && t.weight >= MinTargetWeight && t.weight <= MaxTargetWeight;
        }

        private static bool IsRepresentablePosition(double value)
        {
            // A finite native double can still overflow a Unity float.
            return !double.IsNaN(value) && value >= -float.MaxValue
                && value <= float.MaxValue;
        }

        internal static bool TryConvertRealizationTarget(
            SbRealizationTarget native,
            out RealizationTarget managed)
        {
            managed = default;
            if (!IsFiniteTarget(native))
            {
                return false;
            }

            string anchorName = native.anchor_name == IntPtr.Zero
                ? string.Empty : Marshal.PtrToStringUTF8(native.anchor_name);
            Vector3 unityPosition = CoordinateConverter.SpatialBlendToUnity3D(
                native.position_x, native.position_y, native.position_z);
            // Reduce while still in double precision so large finite angles
            // cannot overflow when converted to Unity's float representation.
            float sbYawDegrees = (float)((native.yaw % (2.0 * Math.PI)) * 180.0 / Math.PI);
            float unityYawDegrees =
                CoordinateConverter.SpatialBlendToUnityAngle(sbYawDegrees);

            managed = new RealizationTarget
            {
                ArrivalTime = native.arrival_time,
                AnchorName = anchorName,
                Position = unityPosition,
                Rotation = Quaternion.Euler(0f, unityYawDegrees, 0f),
                HasHeight = native.has_z != 0,
                KernelType = native.kernel_type,
                ReferentId = native.referent_id,
                Weight = native.weight
            };
            return true;
        }

        [MonoPInvokeCallback(typeof(OnTargetsNativeDelegate))]
        private static void OnTargetsWrapper(SpaceID space, ActorID actor, IntPtr targetsPtr, long numTargets, IntPtr userData)
        {
            if (!TryGetCallback(targetCallbacks, targetCallbackTokens, space, userData,
                                out OnTargetsDelegate callback) || numTargets < 0)
            {
                return;
            }

            // A zero-length native array is an authoritative cancellation
            // snapshot, not "no callback data."
            if (numTargets == 0)
            {
                callback(space, actor, Array.Empty<RealizationTarget>());
                return;
            }

            if (targetsPtr == IntPtr.Zero)
            {
                return;
            }

            int structSize = Marshal.SizeOf(typeof(SbRealizationTarget));
            List<RealizationTarget> targets = new List<RealizationTarget>((int)numTargets);
            for (long i = 0; i < numTargets; ++i)
            {
                IntPtr currentPtr = IntPtr.Add(targetsPtr, (int)(i * structSize));
                SbRealizationTarget native = Marshal.PtrToStructure<SbRealizationTarget>(currentPtr);

                // Defensive: a non-finite yaw/position means a NaN crept through the
                // solver OR the native struct layout doesn't match this marshal struct
                // (stale dylib). Either way, feeding NaN to Quaternion.Euler trips a
                // Unity normalization assertion every frame. Skip the bad target and
                // warn ONCE rather than spam — keeps the session usable and makes the
                // mismatch obvious instead of drowning the console. Abort the whole
                // authoritative snapshot: delivering only its valid subset could
                // incorrectly cancel a pipeline omitted by marshalling failure.
                if (!TryConvertRealizationTarget(native, out var managedTarget))
                {
                    if (!_warnedInvalidTarget)
                    {
                        _warnedInvalidTarget = true;
                        Debug.LogError("[SpatialBlend] Realization target has invalid fields " +
                            $"(yaw={native.yaw}, x={native.position_x}, y={native.position_y}, z={native.position_z}, weight={native.weight}). " +
                            "Dropping the snapshot. This usually means the loaded libspatialblend dylib does NOT " +
                            "match SbRealizationTarget — rebuild + reimport the plugin and restart Unity.");
                    }
                    return;
                }

                targets.Add(managedTarget);
            }

            callback(space, actor, targets);
        }

        [MonoPInvokeCallback(typeof(OnElementAddedNativeDelegate))]
        private static void OnElementAddedWrapper(SpaceID space, ElementID localElement, long sharedId, IntPtr tagPtr, IntPtr userData, long userDataSize, IntPtr cbUserData)
        {
            if (!TryGetCallback(elementAddedCallbacks, elementAddedCallbackTokens, space,
                                cbUserData, out OnElementAddedDelegate callback))
            {
                return;
            }
            string tag = tagPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(tagPtr);
            // userData points into native memory valid only for this call; the
            // managed handler must copy/consume it synchronously.
            callback(space, localElement, sharedId, tag, userData, userDataSize);
        }

        [MonoPInvokeCallback(typeof(OnElementRemovedNativeDelegate))]
        private static void OnElementRemovedWrapper(SpaceID space, ElementID localElement,
                                                    IntPtr cbUserData)
        {
            if (!TryGetCallback(elementRemovedCallbacks, elementRemovedCallbackTokens, space,
                                cbUserData, out OnElementRemovedDelegate callback))
            {
                return;
            }
            callback(space, localElement);
        }
    }
}
