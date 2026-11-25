// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbActorObservation
        {
            [MarshalAs(UnmanagedType.LPUTF8Str)] internal string anchor_name;
            internal double position_x, position_y, position_z, yaw;
        }

        internal readonly struct ActorObservationClock
        {
            internal ActorObservationClock(ulong epoch, long sequence, double time, bool hasSample)
            { Epoch = epoch; Sequence = sequence; TimeSeconds = time; HasSample = hasSample; }
            internal ulong Epoch { get; } internal long Sequence { get; }
            internal double TimeSeconds { get; } internal bool HasSample { get; }
        }

        private static class ActorsRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_destroy_actor(long space, long actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_anchor_active(long space, long actor,
                                                            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int active);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_add_anchor_with_position_dimension(
                long space, long actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double x, double y, double z,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string parent, int positionDimension);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_enable_actor_observation_frames(long session);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong sb_begin_actor_observation_epoch(long space, long actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string rootAnchorName);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_apply_actor_observation_frame(long space, long actor, ulong epoch, long sequence, double time, [In] SbActorObservation[] observations, int count);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_clear_actor_observation_anchor(long space, long actor, ulong epoch, [MarshalAs(UnmanagedType.LPUTF8Str)] string anchor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_get_actor_observation_clock(long space, long actor, out ulong epoch, out long sequence, out double time, out int hasSample);
        }

        private static class ActorsDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_destroy_actor(long space, long actor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_anchor_active(long space, long actor,
                                                            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int active);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY,
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_add_anchor_with_position_dimension(
                long space, long actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double x, double y, double z,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string parent, int positionDimension);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_enable_actor_observation_frames(long session);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong sb_begin_actor_observation_epoch(long space, long actor, [MarshalAs(UnmanagedType.LPUTF8Str)] string rootAnchorName);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_apply_actor_observation_frame(long space, long actor, ulong epoch, long sequence, double time, [In] SbActorObservation[] observations, int count);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_clear_actor_observation_anchor(long space, long actor, ulong epoch, [MarshalAs(UnmanagedType.LPUTF8Str)] string anchor);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)] internal static extern int sb_get_actor_observation_clock(long space, long actor, out ulong epoch, out long sequence, out double time, out int hasSample);
        }

        public static bool DestroyActor(long space, long actor)
        {
            return (useDebugLibrary
                ? ActorsDebug.sb_destroy_actor(space, actor)
                : ActorsRelease.sb_destroy_actor(space, actor)) != 0;
        }

        public static bool SetAnchorActive(long space, long actor, string name, bool active)
        {
            return (useDebugLibrary
                ? ActorsDebug.sb_set_anchor_active(space, actor, name, active ? 1 : 0)
                : ActorsRelease.sb_set_anchor_active(space, actor, name, active ? 1 : 0)) != 0;
        }

        public static bool AddAnchorWithPositionDimension(long space, long actor, string name,
                                                          UnityEngine.Vector3 localOffset,
                                                          string parent, int positionDimension)
        {
            CoordinateConverter.UnityToSpatialBlend3D(localOffset, out double x, out double y,
                                                    out double z);
            return (useDebugLibrary
                ? ActorsDebug.sb_add_anchor_with_position_dimension(space, actor, name, x, y,
                    z, parent, positionDimension)
                : ActorsRelease.sb_add_anchor_with_position_dimension(space, actor, name, x,
                    y, z, parent, positionDimension)) != 0;
        }

        public static bool EnableActorObservationFrames(long session) => (useDebugLibrary
            ? ActorsDebug.sb_enable_actor_observation_frames(session)
            : ActorsRelease.sb_enable_actor_observation_frames(session)) != 0;
        public static ulong BeginActorObservationEpoch(long space, long actor, string rootAnchorName) =>
            useDebugLibrary ? ActorsDebug.sb_begin_actor_observation_epoch(space, actor, rootAnchorName)
                            : ActorsRelease.sb_begin_actor_observation_epoch(space, actor, rootAnchorName);
        public static bool ApplyActorObservationFrame(long space, long actor, ulong epoch, long sequence,
                                                       double time, SbActorObservation[] observations) => (useDebugLibrary
            ? ActorsDebug.sb_apply_actor_observation_frame(space, actor, epoch, sequence, time, observations, observations == null ? 0 : observations.Length)
            : ActorsRelease.sb_apply_actor_observation_frame(space, actor, epoch, sequence, time, observations, observations == null ? 0 : observations.Length)) != 0;
        public static bool ClearActorObservationAnchor(long space, long actor, ulong epoch, string anchor) => (useDebugLibrary
            ? ActorsDebug.sb_clear_actor_observation_anchor(space, actor, epoch, anchor)
            : ActorsRelease.sb_clear_actor_observation_anchor(space, actor, epoch, anchor)) != 0;
        internal static bool TryGetActorObservationClock(long space, long actor, out ActorObservationClock clock)
        {
            clock = default;
            ulong epoch; long sequence; double time; int hasSample;
            bool accepted = (useDebugLibrary
                ? ActorsDebug.sb_get_actor_observation_clock(space, actor, out epoch, out sequence, out time, out hasSample)
                : ActorsRelease.sb_get_actor_observation_clock(space, actor, out epoch, out sequence, out time, out hasSample)) != 0;
            if (!accepted) return false;
            clock = new ActorObservationClock(epoch, sequence, time, hasSample != 0);
            return true;
        }

        public static bool TryReadActorState(long space, long actor, out UnityEngine.Vector3 position,
                                             out float yawDegrees)
        {
            position = default;
            yawDegrees = 0f;
            try
            {
                SbActorState state = useDebugLibrary
                    ? DebugApi.sb_get_actor_state(space, actor)
                    : Release.sb_get_actor_state(space, actor);
                if (double.IsNaN(state.position_x) || double.IsInfinity(state.position_x) ||
                    double.IsNaN(state.position_y) || double.IsInfinity(state.position_y) ||
                    double.IsNaN(state.yaw) || double.IsInfinity(state.yaw)) return false;
                position = CoordinateConverter.SpatialBlendToUnity2D(state.position_x, state.position_y);
                yawDegrees = CoordinateConverter.SpatialBlendToUnityAngle(
                    (float)(state.yaw * 180.0 / Math.PI));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolves every actor-lifetime entrypoint before an actor is allocated.
        /// Invalid zero handles make this a non-mutating ABI probe.
        /// </summary>
        public static bool TryEnsureActorLifetimeAbi(out string error)
        {
            try
            {
                DestroyActor(0, 0);
                SetAnchorActive(0, 0, string.Empty, false);
                Bind(0, 0, string.Empty, SbKernelType.Spacing);
                BindAffordance(0, 0, string.Empty);
                AddAnchorWithPositionDimension(0, 0, string.Empty, UnityEngine.Vector3.zero,
                                               null, 2);
                if (useDebugLibrary) DebugApi.sb_get_actor_state(0, 0);
                else Release.sb_get_actor_state(0, 0);
                error = null;
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                error = "The selected SpatialBlend native library is missing current actor-lifetime ABI symbols. Update the matching native library.";
                return false;
            }
            catch (DllNotFoundException)
            {
                error = "The selected SpatialBlend native library could not be loaded. Install the matching current native library.";
                return false;
            }
        }

        /// <summary>Independent additive probe; legacy actor creation never calls it.</summary>
        public static bool TryEnsureActorObservationFrameAbi(out string error)
        {
            try
            {
                EnableActorObservationFrames(0);
                BeginActorObservationEpoch(0, 0, string.Empty);
                ApplyActorObservationFrame(0, 0, 0, 0, 0d, Array.Empty<SbActorObservation>());
                ClearActorObservationAnchor(0, 0, 0, string.Empty);
                TryGetActorObservationClock(0, 0, out _);
                error = null; return true;
            }
            catch (EntryPointNotFoundException) { error = "The selected native library is missing actor observation-frame ABI symbols."; return false; }
            catch (DllNotFoundException) { error = "The selected native library could not be loaded."; return false; }
        }
    }
}
