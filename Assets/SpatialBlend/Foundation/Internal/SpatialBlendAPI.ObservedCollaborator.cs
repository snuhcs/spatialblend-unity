// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        private const uint ObservedCollaboratorVersion = 1;
        private const uint MaxFrames = 64, MaxObservations = 128, MaxPublications = 64;
        private const uint ObservedSourceSetupVersion = 1;
        private const long MaxObservedSourceAgeNanoseconds = 1000000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ObservedCollaboratorFeature
        {
            internal uint descriptor_size, feature_version, frame_size, observation_size,
                publication_size, max_frames, max_observations_per_frame,
                max_publications, max_anchor_name_bytes;
            internal ulong max_age_ns;
            internal uint coordinate_convention, reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedCollaboratorObservation
        {
            internal IntPtr anchor_name;
            internal double position_x, position_y, position_z, yaw;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedCollaboratorFrame
        {
            internal uint struct_size, feature_version;
            internal long actor;
            internal ulong epoch;
            internal long sequence;
            internal double time_seconds;
            internal IntPtr observations;
            internal uint observation_count, observation_size;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedCollaboratorPublication
        {
            internal uint struct_size, feature_version;
            internal long element;
            internal ulong lease;
            internal long observed_at_ns, valid_until_ns;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObservedSourceSetupFeature
        {
            internal uint descriptor_size, feature_version, participant_size, point_size,
                actor_result_size, point_result_size, max_participants, max_points,
                max_anchor_name_bytes, reserved;
            internal long max_age_ns;
            internal uint coordinate_convention, reserved2;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedSourceSetupParticipant
        {
            internal uint struct_size, feature_version;
            internal long space, actor;
            internal IntPtr root_anchor_name;
            internal ulong expected_prior_epoch;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedSourceSetupPoint
        {
            internal uint struct_size, feature_version;
            internal long space, element, producer;
            internal IntPtr anchor_name;
            internal long maximum_age_ns;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedSourceSetupActorResult
        {
            internal long actor;
            internal ulong epoch;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObservedSourceSetupPointResult
        {
            internal long element;
            internal ulong lease;
        }

        internal readonly struct ObservedCollaboratorFrameInput
        {
            internal ObservedCollaboratorFrameInput(long actor, ulong epoch, long sequence,
                double timeSeconds, SbActorObservation[] observations)
            { Actor = actor; Epoch = epoch; Sequence = sequence; TimeSeconds = timeSeconds; Observations = observations; }
            internal long Actor { get; } internal ulong Epoch { get; } internal long Sequence { get; }
            internal double TimeSeconds { get; } internal SbActorObservation[] Observations { get; }
        }
        internal readonly struct ObservedCollaboratorPublicationInput
        {
            internal ObservedCollaboratorPublicationInput(long element, ulong lease,
                long observedAtNanoseconds, long validUntilNanoseconds)
            { Element = element; Lease = lease; ObservedAtNanoseconds = observedAtNanoseconds; ValidUntilNanoseconds = validUntilNanoseconds; }
            internal long Element { get; } internal ulong Lease { get; }
            internal long ObservedAtNanoseconds { get; } internal long ValidUntilNanoseconds { get; }
        }
        internal readonly struct ObservedSourceSetupParticipantInput
        {
            internal ObservedSourceSetupParticipantInput(long actor, string rootAnchor,
                ulong expectedPriorEpoch = 0)
            { Actor = actor; RootAnchor = rootAnchor; ExpectedPriorEpoch = expectedPriorEpoch; }
            internal long Actor { get; } internal string RootAnchor { get; }
            internal ulong ExpectedPriorEpoch { get; }
        }
        internal readonly struct ObservedSourceSetupPointInput
        {
            internal ObservedSourceSetupPointInput(long element, long producer, string anchor,
                long maximumAgeNanoseconds)
            { Element = element; Producer = producer; Anchor = anchor; MaximumAgeNanoseconds = maximumAgeNanoseconds; }
            internal long Element { get; } internal long Producer { get; }
            internal string Anchor { get; } internal long MaximumAgeNanoseconds { get; }
        }
        internal readonly struct ObservedSourceSetupActorResult
        {
            internal ObservedSourceSetupActorResult(long actor, ulong epoch)
            { Actor = actor; Epoch = epoch; }
            internal long Actor { get; } internal ulong Epoch { get; }
        }
        internal readonly struct ObservedSourceSetupPointResult
        {
            internal ObservedSourceSetupPointResult(long element, ulong lease)
            { Element = element; Lease = lease; }
            internal long Element { get; } internal ulong Lease { get; }
        }
        internal sealed class ObservedSourceSetupReceipt
        {
            internal ObservedSourceSetupReceipt(long setup,
                ObservedSourceSetupActorResult[] actors,
                ObservedSourceSetupPointResult[] points)
            {
                Setup = setup;
                Actors = new System.Collections.ObjectModel.ReadOnlyCollection<ObservedSourceSetupActorResult>(
                    new List<ObservedSourceSetupActorResult>(actors));
                Points = new System.Collections.ObjectModel.ReadOnlyCollection<ObservedSourceSetupPointResult>(
                    new List<ObservedSourceSetupPointResult>(points));
            }
            internal long Setup { get; }
            internal IReadOnlyList<ObservedSourceSetupActorResult> Actors { get; }
            internal IReadOnlyList<ObservedSourceSetupPointResult> Points { get; }
        }

        private static class ObservedCollaboratorRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_collaborator_feature(ref ObservedCollaboratorFeature feature, uint size);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_collaborator_field_count(uint layout);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_collaborator_field_offset(uint layout, uint field);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_begin_observed_source_anchor_point(long space, long element, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchor, long maximumAgeNanoseconds, out ulong lease);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_retire_observed_source_anchor_point(long space, long element, ulong lease);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_apply_observed_collaborator_batch(long space, IntPtr frames,
                uint frameCount, IntPtr publications, uint publicationCount);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_source_setup_feature(ref ObservedSourceSetupFeature feature, uint size);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_source_setup_field_count(uint layout);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_source_setup_field_offset(uint layout, uint field);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_begin_observed_source_setup(long space,
                IntPtr participants, uint participantCount, IntPtr points, uint pointCount,
                out long setup, IntPtr actorResults, uint actorResultCapacity,
                IntPtr pointResults, uint pointResultCapacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_retire_observed_source_setup(long space, long setup);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_release_observed_source_setup(long setup);
        }
        private static class ObservedCollaboratorDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_collaborator_feature(ref ObservedCollaboratorFeature feature, uint size);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_collaborator_field_count(uint layout);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_collaborator_field_offset(uint layout, uint field);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_begin_observed_source_anchor_point(long space, long element, long actor,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string anchor, long maximumAgeNanoseconds, out ulong lease);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_retire_observed_source_anchor_point(long space, long element, ulong lease);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_apply_observed_collaborator_batch(long space, IntPtr frames,
                uint frameCount, IntPtr publications, uint publicationCount);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_source_setup_feature(ref ObservedSourceSetupFeature feature, uint size);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_source_setup_field_count(uint layout);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_observed_source_setup_field_offset(uint layout, uint field);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_begin_observed_source_setup(long space,
                IntPtr participants, uint participantCount, IntPtr points, uint pointCount,
                out long setup, IntPtr actorResults, uint actorResultCapacity,
                IntPtr pointResults, uint pointResultCapacity);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_retire_observed_source_setup(long space, long setup);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_release_observed_source_setup(long setup);
        }

        internal static bool TryEnsureObservedCollaboratorBatchAbi(out string error)
        {
            error = null;
            try
            {
                var feature = default(ObservedCollaboratorFeature);
                uint size = (uint)Marshal.SizeOf(typeof(ObservedCollaboratorFeature));
                uint written = useDebugLibrary
                    ? ObservedCollaboratorDebug.sb_get_observed_collaborator_feature(ref feature, size)
                    : ObservedCollaboratorRelease.sb_get_observed_collaborator_feature(ref feature, size);
                if (written != size || feature.descriptor_size != size || feature.feature_version != ObservedCollaboratorVersion ||
                    feature.frame_size != Marshal.SizeOf(typeof(NativeObservedCollaboratorFrame)) ||
                    feature.observation_size != Marshal.SizeOf(typeof(NativeObservedCollaboratorObservation)) ||
                    feature.publication_size != Marshal.SizeOf(typeof(NativeObservedCollaboratorPublication)) ||
                    feature.max_frames != MaxFrames || feature.max_observations_per_frame != MaxObservations ||
                    feature.max_publications != MaxPublications || feature.max_anchor_name_bytes == 0 ||
                    feature.max_age_ns == 0 || feature.coordinate_convention != 1 || feature.reserved != 0 ||
                    !OffsetsMatch(1, typeof(ObservedCollaboratorFeature), new[] { "descriptor_size", "feature_version", "frame_size", "observation_size", "publication_size", "max_frames", "max_observations_per_frame", "max_publications", "max_anchor_name_bytes", "max_age_ns", "coordinate_convention", "reserved" }) ||
                    !OffsetsMatch(2, typeof(NativeObservedCollaboratorFrame), new[] { "struct_size", "feature_version", "actor", "epoch", "sequence", "time_seconds", "observations", "observation_count", "observation_size" }) ||
                    !OffsetsMatch(3, typeof(NativeObservedCollaboratorPublication), new[] { "struct_size", "feature_version", "element", "lease", "observed_at_ns", "valid_until_ns" }))
                { error = "Native observed-collaborator C1 ABI feature is incompatible."; return false; }
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            { error = "Native observed-collaborator C1 feature unavailable: " + e.Message; return false; }
        }

        internal static bool TryEnsureObservedSourceSetupAbi(out string error)
        {
            error = null;
            try
            {
                var feature = default(ObservedSourceSetupFeature);
                uint size = (uint)Marshal.SizeOf(typeof(ObservedSourceSetupFeature));
                uint written = useDebugLibrary
                    ? ObservedCollaboratorDebug.sb_get_observed_source_setup_feature(ref feature, size)
                    : ObservedCollaboratorRelease.sb_get_observed_source_setup_feature(ref feature, size);
                if (written != size || feature.descriptor_size != size ||
                    feature.feature_version != ObservedSourceSetupVersion ||
                    feature.participant_size != Marshal.SizeOf(typeof(NativeObservedSourceSetupParticipant)) ||
                    feature.point_size != Marshal.SizeOf(typeof(NativeObservedSourceSetupPoint)) ||
                    feature.actor_result_size != Marshal.SizeOf(typeof(NativeObservedSourceSetupActorResult)) ||
                    feature.point_result_size != Marshal.SizeOf(typeof(NativeObservedSourceSetupPointResult)) ||
                    feature.max_participants != MaxFrames || feature.max_points != MaxPublications ||
                    feature.max_anchor_name_bytes == 0 ||
                    feature.max_age_ns != MaxObservedSourceAgeNanoseconds ||
                    feature.coordinate_convention != 1 || feature.reserved != 0 || feature.reserved2 != 0 ||
                    !SourceSetupOffsetsMatch(1, typeof(ObservedSourceSetupFeature), new[] { "descriptor_size", "feature_version", "participant_size", "point_size", "actor_result_size", "point_result_size", "max_participants", "max_points", "max_anchor_name_bytes", "reserved", "max_age_ns", "coordinate_convention", "reserved2" }) ||
                    !SourceSetupOffsetsMatch(2, typeof(NativeObservedSourceSetupParticipant), new[] { "struct_size", "feature_version", "space", "actor", "root_anchor_name", "expected_prior_epoch" }) ||
                    !SourceSetupOffsetsMatch(3, typeof(NativeObservedSourceSetupPoint), new[] { "struct_size", "feature_version", "space", "element", "producer", "anchor_name", "maximum_age_ns" }) ||
                    !SourceSetupOffsetsMatch(4, typeof(NativeObservedSourceSetupActorResult), new[] { "actor", "epoch" }) ||
                    !SourceSetupOffsetsMatch(5, typeof(NativeObservedSourceSetupPointResult), new[] { "element", "lease" }))
                { error = "Native observed-source-setup C1 ABI feature is incompatible."; return false; }
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            { error = "Native observed-source-setup C1 feature unavailable: " + e.Message; return false; }
        }

        internal static bool TryBeginObservedSourceSetup(long space,
            IReadOnlyList<ObservedSourceSetupParticipantInput> participants,
            IReadOnlyList<ObservedSourceSetupPointInput> points,
            out ObservedSourceSetupReceipt receipt, out string error)
        {
            receipt = null; error = null;
            if (!TryEnsureObservedSourceSetupAbi(out error)) return false;
            if (space <= 0 || participants == null || points == null ||
                participants.Count == 0 || participants.Count > MaxFrames ||
                points.Count == 0 || points.Count > MaxPublications)
                return FailObserved("Observed source setup bounds or native space are invalid.", out error);
            try
            {
                using (var memory = new NativeObservedSourceSetupMemory(space, participants, points))
                {
                    long setup;
                    bool accepted = (useDebugLibrary
                        ? ObservedCollaboratorDebug.sb_begin_observed_source_setup(space, memory.Participants,
                            (uint)participants.Count, memory.Points, (uint)points.Count, out setup,
                            memory.ActorResults, (uint)participants.Count, memory.PointResults, (uint)points.Count)
                        : ObservedCollaboratorRelease.sb_begin_observed_source_setup(space, memory.Participants,
                            (uint)participants.Count, memory.Points, (uint)points.Count, out setup,
                            memory.ActorResults, (uint)participants.Count, memory.PointResults, (uint)points.Count)) != 0;
                    if (!accepted || setup <= 0)
                        return FailObserved("Native observed source setup was rejected without a partial fallback.", out error);
                    receipt = new ObservedSourceSetupReceipt(setup,
                        memory.ReadActorResults(participants.Count), memory.ReadPointResults(points.Count));
                    return true;
                }
            }
            catch (Exception e) { receipt = null; error = e.Message; return false; }
        }

        internal static bool TryRetireObservedSourceSetup(long space, long setup, out string error)
        {
            error = null;
            if (!TryEnsureObservedSourceSetupAbi(out error)) return false;
            if (space <= 0 || setup <= 0)
                return FailObserved("A live source space and setup token are required.", out error);
            try
            {
                return (useDebugLibrary
                    ? ObservedCollaboratorDebug.sb_retire_observed_source_setup(space, setup)
                    : ObservedCollaboratorRelease.sb_retire_observed_source_setup(space, setup)) != 0 ||
                    FailObserved("Native observed source setup retirement was rejected.", out error);
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        internal static bool TryReleaseObservedSourceSetup(long setup, out string error)
        {
            error = null;
            if (!TryEnsureObservedSourceSetupAbi(out error)) return false;
            if (setup <= 0) return FailObserved("A setup token is required.", out error);
            try
            {
                return (useDebugLibrary
                    ? ObservedCollaboratorDebug.sb_release_observed_source_setup(setup)
                    : ObservedCollaboratorRelease.sb_release_observed_source_setup(setup)) != 0 ||
                    FailObserved("Native observed source setup release was rejected.", out error);
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        internal static bool TryBeginObservedSourceAnchorPoint(long space, long element, long actor,
            string anchor, long maximumAgeNanoseconds, out ulong lease, out string error)
        {
            lease = 0; error = null;
            if (!TryEnsureObservedCollaboratorBatchAbi(out error)) return false;
            if (space <= 0 || element <= 0 || actor <= 0 || !ValidAnchor(anchor) || maximumAgeNanoseconds <= 0)
            { error = "A live source space, point element, actor, anchor, and bounded age are required."; return false; }
            try
            {
                bool accepted = (useDebugLibrary
                    ? ObservedCollaboratorDebug.sb_begin_observed_source_anchor_point(space, element, actor, anchor, maximumAgeNanoseconds, out lease)
                    : ObservedCollaboratorRelease.sb_begin_observed_source_anchor_point(space, element, actor, anchor, maximumAgeNanoseconds, out lease)) != 0;
                if (!accepted || lease == 0) { lease = 0; error = "Native observed source-point lease was rejected."; return false; }
                return true;
            }
            catch (Exception e) { error = e.Message; lease = 0; return false; }
        }

        internal static bool TryRetireObservedSourceAnchorPoint(long space, long element, ulong lease, out string error)
        {
            error = null;
            if (!TryEnsureObservedCollaboratorBatchAbi(out error)) return false;
            try { return (useDebugLibrary ? ObservedCollaboratorDebug.sb_retire_observed_source_anchor_point(space, element, lease) : ObservedCollaboratorRelease.sb_retire_observed_source_anchor_point(space, element, lease)) != 0 || FailObserved("Native observed source-point retirement was rejected.", out error); }
            catch (Exception e) { error = e.Message; return false; }
        }

        internal static bool TryApplyObservedCollaboratorBatch(long space,
            IReadOnlyList<ObservedCollaboratorFrameInput> frames,
            IReadOnlyList<ObservedCollaboratorPublicationInput> publications, out string error)
        {
            error = null;
            if (!TryEnsureObservedCollaboratorBatchAbi(out error)) return false;
            if (space <= 0 || frames == null || frames.Count == 0 || frames.Count > MaxFrames ||
                publications == null || publications.Count > MaxPublications)
                return FailObserved("Observed C1 batch bounds or native space are invalid.", out error);
            try
            {
                using (var memory = new NativeObservedCollaboratorBatchMemory(frames, publications))
                {
                    bool accepted = (useDebugLibrary
                        ? ObservedCollaboratorDebug.sb_apply_observed_collaborator_batch(space, memory.Frames, (uint)frames.Count, memory.Publications, (uint)publications.Count)
                        : ObservedCollaboratorRelease.sb_apply_observed_collaborator_batch(space, memory.Frames, (uint)frames.Count, memory.Publications, (uint)publications.Count)) != 0;
                    return accepted || FailObserved("Native observed C1 batch was rejected without sequential fallback.", out error);
                }
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        private static bool OffsetsMatch(uint layout, Type type, string[] fields)
        {
            uint count = useDebugLibrary ? ObservedCollaboratorDebug.sb_get_observed_collaborator_field_count(layout) : ObservedCollaboratorRelease.sb_get_observed_collaborator_field_count(layout);
            if (count != fields.Length) return false;
            for (uint index = 0; index < count; ++index)
            {
                uint native = useDebugLibrary ? ObservedCollaboratorDebug.sb_get_observed_collaborator_field_offset(layout, index) : ObservedCollaboratorRelease.sb_get_observed_collaborator_field_offset(layout, index);
                if (native != (uint)Marshal.OffsetOf(type, fields[index]).ToInt64()) return false;
            }
            return (useDebugLibrary ? ObservedCollaboratorDebug.sb_get_observed_collaborator_field_offset(layout, count) : ObservedCollaboratorRelease.sb_get_observed_collaborator_field_offset(layout, count)) == uint.MaxValue;
        }
        private static bool SourceSetupOffsetsMatch(uint layout, Type type, string[] fields)
        {
            uint count = useDebugLibrary
                ? ObservedCollaboratorDebug.sb_get_observed_source_setup_field_count(layout)
                : ObservedCollaboratorRelease.sb_get_observed_source_setup_field_count(layout);
            if (count != fields.Length) return false;
            for (uint index = 0; index < count; ++index)
            {
                uint native = useDebugLibrary
                    ? ObservedCollaboratorDebug.sb_get_observed_source_setup_field_offset(layout, index)
                    : ObservedCollaboratorRelease.sb_get_observed_source_setup_field_offset(layout, index);
                if (native != (uint)Marshal.OffsetOf(type, fields[index]).ToInt64()) return false;
            }
            return (useDebugLibrary
                ? ObservedCollaboratorDebug.sb_get_observed_source_setup_field_offset(layout, count)
                : ObservedCollaboratorRelease.sb_get_observed_source_setup_field_offset(layout, count)) == uint.MaxValue;
        }
        private static bool FailObserved(string message, out string error) { error = message; return false; }

        private sealed class NativeObservedSourceSetupMemory : IDisposable
        {
            private readonly List<IntPtr> _allocations = new List<IntPtr>();
            internal IntPtr Participants { get; }
            internal IntPtr Points { get; }
            internal IntPtr ActorResults { get; }
            internal IntPtr PointResults { get; }

            internal NativeObservedSourceSetupMemory(long space,
                IReadOnlyList<ObservedSourceSetupParticipantInput> participants,
                IReadOnlyList<ObservedSourceSetupPointInput> points)
            {
                Participants = AllocateArray<NativeObservedSourceSetupParticipant>(participants.Count);
                for (int index = 0; index < participants.Count; ++index)
                {
                    ObservedSourceSetupParticipantInput input = participants[index];
                    if (input.Actor <= 0 || !ValidAnchor(input.RootAnchor))
                        throw new ArgumentException("Observed source setup participant is invalid.");
                    Write(Participants, index, new NativeObservedSourceSetupParticipant {
                        struct_size = (uint)Marshal.SizeOf(typeof(NativeObservedSourceSetupParticipant)),
                        feature_version = ObservedSourceSetupVersion, space = space, actor = input.Actor,
                        root_anchor_name = AllocateUtf8(input.RootAnchor),
                        expected_prior_epoch = input.ExpectedPriorEpoch });
                }
                Points = AllocateArray<NativeObservedSourceSetupPoint>(points.Count);
                for (int index = 0; index < points.Count; ++index)
                {
                    ObservedSourceSetupPointInput input = points[index];
                    if (input.Element <= 0 || input.Producer <= 0 || !ValidAnchor(input.Anchor) ||
                        input.MaximumAgeNanoseconds <= 0 ||
                        input.MaximumAgeNanoseconds > MaxObservedSourceAgeNanoseconds)
                        throw new ArgumentException("Observed source setup point is invalid.");
                    Write(Points, index, new NativeObservedSourceSetupPoint {
                        struct_size = (uint)Marshal.SizeOf(typeof(NativeObservedSourceSetupPoint)),
                        feature_version = ObservedSourceSetupVersion, space = space, element = input.Element,
                        producer = input.Producer, anchor_name = AllocateUtf8(input.Anchor),
                        maximum_age_ns = input.MaximumAgeNanoseconds });
                }
                ActorResults = AllocateArray<NativeObservedSourceSetupActorResult>(participants.Count);
                PointResults = AllocateArray<NativeObservedSourceSetupPointResult>(points.Count);
            }

            internal ObservedSourceSetupActorResult[] ReadActorResults(int count)
            {
                var results = new ObservedSourceSetupActorResult[count];
                for (int index = 0; index < count; ++index)
                {
                    var native = Marshal.PtrToStructure<NativeObservedSourceSetupActorResult>(
                        IntPtr.Add(ActorResults, Marshal.SizeOf(typeof(NativeObservedSourceSetupActorResult)) * index));
                    if (native.actor <= 0 || native.epoch == 0)
                        throw new InvalidOperationException("Native observed source setup returned an invalid actor result.");
                    results[index] = new ObservedSourceSetupActorResult(native.actor, native.epoch);
                }
                return results;
            }

            internal ObservedSourceSetupPointResult[] ReadPointResults(int count)
            {
                var results = new ObservedSourceSetupPointResult[count];
                for (int index = 0; index < count; ++index)
                {
                    var native = Marshal.PtrToStructure<NativeObservedSourceSetupPointResult>(
                        IntPtr.Add(PointResults, Marshal.SizeOf(typeof(NativeObservedSourceSetupPointResult)) * index));
                    if (native.element <= 0 || native.lease == 0)
                        throw new InvalidOperationException("Native observed source setup returned an invalid point result.");
                    results[index] = new ObservedSourceSetupPointResult(native.element, native.lease);
                }
                return results;
            }

            public void Dispose()
            {
                foreach (IntPtr pointer in _allocations) Marshal.FreeHGlobal(pointer);
                _allocations.Clear();
            }

            private IntPtr AllocateArray<T>(int count) where T : struct
            {
                IntPtr pointer = Marshal.AllocHGlobal(checked(Marshal.SizeOf(typeof(T)) * count));
                _allocations.Add(pointer); return pointer;
            }
            private IntPtr AllocateUtf8(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, pointer, bytes.Length); Marshal.WriteByte(pointer, bytes.Length, 0);
                _allocations.Add(pointer); return pointer;
            }
            private static void Write<T>(IntPtr basePointer, int index, T value) where T : struct =>
                Marshal.StructureToPtr(value, IntPtr.Add(basePointer,
                    Marshal.SizeOf(typeof(T)) * index), false);
        }

        private sealed class NativeObservedCollaboratorBatchMemory : IDisposable
        {
            private readonly List<IntPtr> _allocations = new List<IntPtr>();
            internal IntPtr Frames { get; }
            internal IntPtr Publications { get; }
            internal NativeObservedCollaboratorBatchMemory(IReadOnlyList<ObservedCollaboratorFrameInput> frames,
                IReadOnlyList<ObservedCollaboratorPublicationInput> publications)
            {
                Frames = AllocateArray<NativeObservedCollaboratorFrame>(frames.Count);
                for (int index = 0; index < frames.Count; ++index)
                {
                    ObservedCollaboratorFrameInput input = frames[index];
                    if (input.Actor <= 0 || input.Epoch == 0 || input.Sequence < 0 || !Finite(input.TimeSeconds) ||
                        input.Observations == null || input.Observations.Length > MaxObservations)
                        throw new ArgumentException("Observed C1 frame input is invalid.");
                    IntPtr observations = AllocateArray<NativeObservedCollaboratorObservation>(input.Observations.Length);
                    for (int row = 0; row < input.Observations.Length; ++row)
                    {
                        SbActorObservation source = input.Observations[row];
                        if (!ValidAnchor(source.anchor_name) || !Finite(source.position_x) || !Finite(source.position_y) || !Finite(source.position_z) || !Finite(source.yaw))
                            throw new ArgumentException("Observed C1 anchor input is invalid.");
                        Write(observations, row, new NativeObservedCollaboratorObservation { anchor_name = AllocateUtf8(source.anchor_name), position_x = source.position_x, position_y = source.position_y, position_z = source.position_z, yaw = source.yaw });
                    }
                    Write(Frames, index, new NativeObservedCollaboratorFrame { struct_size = (uint)Marshal.SizeOf(typeof(NativeObservedCollaboratorFrame)), feature_version = ObservedCollaboratorVersion, actor = input.Actor, epoch = input.Epoch, sequence = input.Sequence, time_seconds = input.TimeSeconds, observations = observations, observation_count = (uint)input.Observations.Length, observation_size = (uint)Marshal.SizeOf(typeof(NativeObservedCollaboratorObservation)) });
                }
                Publications = AllocateArray<NativeObservedCollaboratorPublication>(publications.Count);
                for (int index = 0; index < publications.Count; ++index)
                {
                    ObservedCollaboratorPublicationInput input = publications[index];
                    if (input.Element <= 0 || input.Lease == 0 || input.ObservedAtNanoseconds <= 0 || input.ValidUntilNanoseconds <= input.ObservedAtNanoseconds)
                        throw new ArgumentException("Observed C1 point publication is invalid.");
                    Write(Publications, index, new NativeObservedCollaboratorPublication { struct_size = (uint)Marshal.SizeOf(typeof(NativeObservedCollaboratorPublication)), feature_version = ObservedCollaboratorVersion, element = input.Element, lease = input.Lease, observed_at_ns = input.ObservedAtNanoseconds, valid_until_ns = input.ValidUntilNanoseconds });
                }
            }
            public void Dispose() { foreach (IntPtr pointer in _allocations) Marshal.FreeHGlobal(pointer); _allocations.Clear(); }
            private IntPtr AllocateArray<T>(int count) where T : struct
            {
                if (count == 0) return IntPtr.Zero;
                IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T)) * count); _allocations.Add(pointer); return pointer;
            }
            private IntPtr AllocateUtf8(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value); IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, pointer, bytes.Length); Marshal.WriteByte(pointer, bytes.Length, 0); _allocations.Add(pointer); return pointer;
            }
            private static void Write<T>(IntPtr basePointer, int index, T value) where T : struct => Marshal.StructureToPtr(value, IntPtr.Add(basePointer, Marshal.SizeOf(typeof(T)) * index), false);
            private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool ValidAnchor(string value) => !string.IsNullOrWhiteSpace(value) &&
            value.IndexOf('\0') < 0 && Encoding.UTF8.GetByteCount(value) <= 128;
    }
}
