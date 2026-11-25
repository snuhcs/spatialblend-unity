// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    // C3a/C3c are deliberately data-only.  This binding must not grow a route
    // planner or infer a navigation root from a Unity Transform/body pose.
    internal static partial class SpatialBlendAPI
    {
        internal const uint ReceiverApproachFeatureVersion = 1;
        internal const uint ReceiverApproachCoordinateConvention = 1;
        internal const uint ReceiverApproachAffineConvention = 1;
        internal const uint ReceiverApproachRoutePointStride = sizeof(double) * 2;
        internal const uint ReceiverApproachMaximumPolygons = 64;
        internal const uint ReceiverApproachMaximumVerticesPerPolygon = 256;
        internal const uint ReceiverApproachMaximumTotalVertices = 32768;
        internal const uint ReceiverApproachMaximumRoutePoints = 16384;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverApproachContextFeature
        {
            public uint descriptor_size, feature_version, registration_size, polygon_size, destination_size;
            public uint max_polygons, max_vertices_per_polygon, max_total_vertices, max_root_name_bytes;
            public uint coordinate_convention, affine_convention, required_receiver_input_version, native_clock_domain;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverApproachPolygon
        {
            public uint struct_size, vertex_count;
            public ulong nav_element_id, region_id;
            public uint coordinate_convention, affine_convention;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public double[] local_to_space_row_major;
            public IntPtr vertices_xy;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverApproachDestination
        {
            public uint struct_size;
            public long space;
            public ulong region_id;
            public long @object;
            public ulong affordance_id, support_revision;
            public SbReceiverApproachPolygon local_support;
            public uint coordinate_convention, reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] safe_approach_pose_row_major;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverApproachContextRegistration
        {
            public uint struct_size, feature_version;
            public ulong expected_registration_generation, expected_receiver_input_epoch, expected_receiver_input_sequence, geometry_revision;
            public long requested_valid_until_ns;
            public IntPtr navigation_root_utf8;
            public uint navigation_root_bytes, coordinate_convention;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] navigation_root_pose_row_major;
            public IntPtr navigable_regions;
            public uint navigable_region_count;
            public IntPtr obstacle_regions;
            public uint obstacle_region_count;
            public double footprint_radius_m, required_clearance_m;
            public SbReceiverApproachDestination destination;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverApproachFeasibilityFeature
        {
            public uint descriptor_size, feature_version, header_size, max_route_points;
            public uint coordinate_convention, route_point_stride, native_clock_domain, required_context_version;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbReceiverApproachFeasibilityHeader
        {
            public uint struct_size, feature_version;
            public int feasibility_status;
            public uint coordinate_convention;
            public long session, space, receiver_actor;
            public ulong registration_session_id, registration_generation;
            public ulong receiver_input_epoch, receiver_input_sequence, receiver_input_revision;
            public ulong geometry_revision, support_revision, context_revision;
            public ulong calculation_revision, owner_lifetime, native_clock_domain;
            public long issued_at_ns, valid_until_ns;
            public uint route_point_count, route_point_stride;
            public double start_x, start_y, safe_approach_x, safe_approach_y, route_length_m;
        }

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_context_feature")]
        private static extern uint ReceiverApproachContextFeatureRelease(out SbReceiverApproachContextFeature value, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_context_feature")]
        private static extern uint ReceiverApproachContextFeatureDebug(out SbReceiverApproachContextFeature value, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_context_field_count")]
        private static extern uint ReceiverApproachContextCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_context_field_count")]
        private static extern uint ReceiverApproachContextCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_context_field_offset")]
        private static extern uint ReceiverApproachContextOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_context_field_offset")]
        private static extern uint ReceiverApproachContextOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_register_receiver_approach_context")]
        private static extern int ReceiverApproachContextRegisterRelease(long session, long space, long actor, uint version,
            ref SbReceiverApproachContextRegistration registration, uint registrationSize, out ulong contextRevision);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_register_receiver_approach_context")]
        private static extern int ReceiverApproachContextRegisterDebug(long session, long space, long actor, uint version,
            ref SbReceiverApproachContextRegistration registration, uint registrationSize, out ulong contextRevision);

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_feasibility_feature")]
        private static extern uint ReceiverApproachFeasibilityFeatureRelease(out SbReceiverApproachFeasibilityFeature value, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_feasibility_feature")]
        private static extern uint ReceiverApproachFeasibilityFeatureDebug(out SbReceiverApproachFeasibilityFeature value, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_feasibility_field_count")]
        private static extern uint ReceiverApproachFeasibilityCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_feasibility_field_count")]
        private static extern uint ReceiverApproachFeasibilityCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_feasibility_field_offset")]
        private static extern uint ReceiverApproachFeasibilityOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_receiver_approach_feasibility_field_offset")]
        private static extern uint ReceiverApproachFeasibilityOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_copy_receiver_approach_feasibility")]
        private static extern int ReceiverApproachFeasibilityCopyRelease(long session, long space, long actor, uint version,
            ulong contextRevision, ulong calculationRevision, [Out] double[] route, uint capacity,
            out SbReceiverApproachFeasibilityHeader header, uint headerSize);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_copy_receiver_approach_feasibility")]
        private static extern int ReceiverApproachFeasibilityCopyDebug(long session, long space, long actor, uint version,
            ulong contextRevision, ulong calculationRevision, [Out] double[] route, uint capacity,
            out SbReceiverApproachFeasibilityHeader header, uint headerSize);

        private static readonly Type[] ReceiverApproachContextTypes = { typeof(SbReceiverApproachContextFeature),
            typeof(SbReceiverApproachContextRegistration), typeof(SbReceiverApproachPolygon), typeof(SbReceiverApproachDestination) };
        private static readonly string[][] ReceiverApproachContextFields =
        {
            new[] { "descriptor_size", "feature_version", "registration_size", "polygon_size", "destination_size", "max_polygons", "max_vertices_per_polygon", "max_total_vertices", "max_root_name_bytes", "coordinate_convention", "affine_convention", "required_receiver_input_version", "native_clock_domain" },
            new[] { "struct_size", "feature_version", "expected_registration_generation", "expected_receiver_input_epoch", "expected_receiver_input_sequence", "geometry_revision", "requested_valid_until_ns", "navigation_root_utf8", "navigation_root_bytes", "coordinate_convention", "navigation_root_pose_row_major", "navigable_regions", "navigable_region_count", "obstacle_regions", "obstacle_region_count", "footprint_radius_m", "required_clearance_m", "destination" },
            new[] { "struct_size", "vertex_count", "nav_element_id", "region_id", "coordinate_convention", "affine_convention", "local_to_space_row_major", "vertices_xy" },
            new[] { "struct_size", "space", "region_id", "object", "affordance_id", "support_revision", "local_support", "coordinate_convention", "reserved", "safe_approach_pose_row_major" }
        };
        private static readonly Type[] ReceiverApproachFeasibilityTypes = { typeof(SbReceiverApproachFeasibilityFeature), typeof(SbReceiverApproachFeasibilityHeader) };
        private static readonly string[][] ReceiverApproachFeasibilityFields =
        {
            new[] { "descriptor_size", "feature_version", "header_size", "max_route_points", "coordinate_convention", "route_point_stride", "native_clock_domain", "required_context_version" },
            new[] { "struct_size", "feature_version", "feasibility_status", "coordinate_convention", "session", "space", "receiver_actor", "registration_session_id", "registration_generation", "receiver_input_epoch", "receiver_input_sequence", "receiver_input_revision", "geometry_revision", "support_revision", "context_revision", "calculation_revision", "owner_lifetime", "native_clock_domain", "issued_at_ns", "valid_until_ns", "route_point_count", "route_point_stride", "start_x", "start_y", "safe_approach_x", "safe_approach_y", "route_length_m" }
        };

        internal static bool TryGetReceiverApproachFeatures(out SbReceiverApproachContextFeature context,
            out SbReceiverApproachFeasibilityFeature feasibility, out string error)
        {
            context = default; feasibility = default; error = null;
            try
            {
                uint contextSize = (uint)Marshal.SizeOf(ReceiverApproachContextTypes[0]);
                uint feasibilitySize = (uint)Marshal.SizeOf(ReceiverApproachFeasibilityTypes[0]);
                uint gotContext = useDebugLibrary ? ReceiverApproachContextFeatureDebug(out context, contextSize) : ReceiverApproachContextFeatureRelease(out context, contextSize);
                uint gotFeasibility = useDebugLibrary ? ReceiverApproachFeasibilityFeatureDebug(out feasibility, feasibilitySize) : ReceiverApproachFeasibilityFeatureRelease(out feasibility, feasibilitySize);
                if (gotContext != contextSize || gotFeasibility != feasibilitySize)
                    return ReceiverInputValidation.Fail("Receiver approach descriptor size mismatch.", out error);
                if (!ValidateReceiverApproachContextFeature(context,
                        kind => useDebugLibrary ? ReceiverApproachContextCountDebug(kind) : ReceiverApproachContextCountRelease(kind),
                        (kind, index) => useDebugLibrary ? ReceiverApproachContextOffsetDebug(kind, index) : ReceiverApproachContextOffsetRelease(kind, index), out error)) return false;
                return ValidateReceiverApproachFeasibilityFeature(feasibility,
                    kind => useDebugLibrary ? ReceiverApproachFeasibilityCountDebug(kind) : ReceiverApproachFeasibilityCountRelease(kind),
                    (kind, index) => useDebugLibrary ? ReceiverApproachFeasibilityOffsetDebug(kind, index) : ReceiverApproachFeasibilityOffsetRelease(kind, index), out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver approach feature unavailable: " + e.Message, out error); }
        }

        internal static bool ValidateReceiverApproachContextFeature(SbReceiverApproachContextFeature value,
            Func<uint, uint> count, Func<uint, uint, uint> offset, out string error)
        {
            error = null;
            if (value.descriptor_size != Marshal.SizeOf(ReceiverApproachContextTypes[0]) || value.feature_version != ReceiverApproachFeatureVersion ||
                value.registration_size != Marshal.SizeOf(ReceiverApproachContextTypes[1]) || value.polygon_size != Marshal.SizeOf(ReceiverApproachContextTypes[2]) ||
                value.destination_size != Marshal.SizeOf(ReceiverApproachContextTypes[3]) || value.max_polygons != ReceiverApproachMaximumPolygons ||
                value.max_vertices_per_polygon != ReceiverApproachMaximumVerticesPerPolygon || value.max_total_vertices != ReceiverApproachMaximumTotalVertices ||
                value.max_root_name_bytes != ReceiverInputValidation.MaximumNameBytes || value.coordinate_convention != ReceiverApproachCoordinateConvention ||
                value.affine_convention != ReceiverApproachAffineConvention || value.required_receiver_input_version != 1 || value.native_clock_domain != 1 || count == null || offset == null)
                return ReceiverInputValidation.Fail("Receiver approach context descriptor is incompatible.", out error);
            return ValidateApproachLayout(ReceiverApproachContextTypes, ReceiverApproachContextFields, count, offset, out error);
        }

        internal static bool ValidateReceiverApproachFeasibilityFeature(SbReceiverApproachFeasibilityFeature value,
            Func<uint, uint> count, Func<uint, uint, uint> offset, out string error)
        {
            error = null;
            if (value.descriptor_size != Marshal.SizeOf(ReceiverApproachFeasibilityTypes[0]) || value.feature_version != ReceiverApproachFeatureVersion ||
                value.header_size != Marshal.SizeOf(ReceiverApproachFeasibilityTypes[1]) || value.max_route_points != ReceiverApproachMaximumRoutePoints ||
                value.coordinate_convention != ReceiverApproachCoordinateConvention || value.route_point_stride != ReceiverApproachRoutePointStride ||
                value.native_clock_domain != 1 || value.required_context_version != ReceiverApproachFeatureVersion || count == null || offset == null)
                return ReceiverInputValidation.Fail("Receiver approach feasibility descriptor is incompatible.", out error);
            return ValidateApproachLayout(ReceiverApproachFeasibilityTypes, ReceiverApproachFeasibilityFields, count, offset, out error);
        }

        private static bool ValidateApproachLayout(Type[] types, string[][] fields, Func<uint, uint> count,
            Func<uint, uint, uint> offset, out string error)
        {
            for (int kind = 0; kind < types.Length; ++kind)
            {
                if (count((uint)kind + 1) != fields[kind].Length)
                    return ReceiverInputValidation.Fail("Receiver approach field count mismatch.", out error);
                for (int index = 0; index < fields[kind].Length; ++index)
                    if (offset((uint)kind + 1, (uint)index) != (uint)Marshal.OffsetOf(types[kind], fields[kind][index]).ToInt64())
                        return ReceiverInputValidation.Fail("Receiver approach field offset mismatch.", out error);
            }
            error = null; return true;
        }

        // Input owns only caller-supplied native-space numbers.  All arrays are
        // copied into Core during this call and never retained by this binding.
        internal static bool TryRegisterReceiverApproachContext(long session, long space, long actor,
            ReceiverApproachContextInput input, out ulong contextRevision, out int nativeResult, out string error)
        {
            contextRevision = 0; nativeResult = -1; error = null;
            if (session <= 0 || space <= 0 || actor <= 0 || input == null || !input.TryValidate(space, out error)) return false;
            if (!TryGetReceiverApproachFeatures(out _, out _, out error)) return false;
            var pins = new ReceiverApproachPinnedInput(input);
            try
            {
                SbReceiverApproachContextRegistration native = pins.Native;
                nativeResult = useDebugLibrary
                    ? ReceiverApproachContextRegisterDebug(session, space, actor, ReceiverApproachFeatureVersion, ref native, (uint)Marshal.SizeOf(typeof(SbReceiverApproachContextRegistration)), out contextRevision)
                    : ReceiverApproachContextRegisterRelease(session, space, actor, ReceiverApproachFeatureVersion, ref native, (uint)Marshal.SizeOf(typeof(SbReceiverApproachContextRegistration)), out contextRevision);
                if (nativeResult != 1 || contextRevision == 0)
                    return ReceiverInputValidation.Fail("Native receiver approach context rejected (result " + nativeResult + ").", out error);
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver approach registration unavailable: " + e.Message, out error); }
            finally { pins.Dispose(); }
        }

        private static int CopyReceiverApproach(long session, long space, long actor, ulong context, ulong calculation,
            double[] route, out SbReceiverApproachFeasibilityHeader header)
        {
            uint capacity = route == null ? 0u : (uint)(route.Length / 2);
            uint size = (uint)Marshal.SizeOf(typeof(SbReceiverApproachFeasibilityHeader));
            return useDebugLibrary
                ? ReceiverApproachFeasibilityCopyDebug(session, space, actor, ReceiverApproachFeatureVersion, context, calculation, route, capacity, out header, size)
                : ReceiverApproachFeasibilityCopyRelease(session, space, actor, ReceiverApproachFeatureVersion, context, calculation, route, capacity, out header, size);
        }

        // Exactly two calls: probe freezes calculation_revision and successful
        // copy cites it.  Any changed/retry/partial result returns no DTO.
        internal static bool TryCopyReceiverApproachFeasibility(long session, long space, long actor,
            ulong contextRevision, ReceiverApproachExpectedIdentity expected, ulong fixedCalculationRevision,
            out ReceiverApproachNativeCopy copy, out int nativeResult, out string error)
        {
            copy = null; nativeResult = -1; error = null;
            if (session <= 0 || space <= 0 || actor <= 0 || contextRevision == 0 || expected == null ||
                !expected.MatchesCall(session, space, actor, contextRevision))
                return ReceiverInputValidation.Fail("Receiver approach copy requires exact receiver-local identities.", out error);
            if (!TryGetReceiverApproachFeatures(out _, out _, out error)) return false;
            try
            {
                nativeResult = CopyReceiverApproach(session, space, actor, contextRevision, fixedCalculationRevision, null, out var probe);
                if (nativeResult != -8 || !ValidateReceiverApproachHeader(probe, expected, fixedCalculationRevision, out error)) return false;
                if (probe.route_point_count > ReceiverApproachMaximumRoutePoints || probe.route_point_count > int.MaxValue / 2)
                    return ReceiverInputValidation.Fail("Native receiver approach route count is invalid.", out error);
                var route = new double[checked((int)probe.route_point_count * 2)];
                nativeResult = CopyReceiverApproach(session, space, actor, contextRevision, probe.calculation_revision, route, out var copied);
                if (nativeResult != 1 || !ValidateReceiverApproachHeader(copied, expected, probe.calculation_revision, out error) ||
                    copied.route_point_count != probe.route_point_count || copied.route_point_stride != probe.route_point_stride ||
                    !SameApproachHeader(probe, copied))
                    return ReceiverInputValidation.Fail("Native receiver approach copy changed or was incomplete.", out error);
                for (int i = 0; i < route.Length; ++i)
                    if (!RichKnownDouble.Finite(route[i])) return ReceiverInputValidation.Fail("Native receiver approach route is not finite.", out error);
                copy = new ReceiverApproachNativeCopy(copied, route);
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Receiver approach copy unavailable: " + e.Message, out error); }
        }

        internal static bool ValidateReceiverApproachHeader(SbReceiverApproachFeasibilityHeader value,
            ReceiverApproachExpectedIdentity expected, ulong calculation, out string error)
        {
            error = null;
            if (value.struct_size != Marshal.SizeOf(typeof(SbReceiverApproachFeasibilityHeader)) || value.feature_version != ReceiverApproachFeatureVersion ||
                value.coordinate_convention != ReceiverApproachCoordinateConvention || value.session != expected.Session || value.space != expected.Space ||
                value.receiver_actor != expected.Actor || value.registration_session_id != (ulong)expected.Session || value.registration_generation == 0 ||
                value.receiver_input_epoch != expected.InputEpoch || value.receiver_input_sequence != expected.InputSequence || value.receiver_input_revision == 0 ||
                value.geometry_revision != expected.GeometryRevision || value.support_revision != expected.SupportRevision || value.context_revision != expected.ContextRevision ||
                value.calculation_revision == 0 || (calculation != 0 && value.calculation_revision != calculation) || value.owner_lifetime == 0 ||
                value.native_clock_domain != expected.NativeClockDomain || value.issued_at_ns <= 0 || value.valid_until_ns <= value.issued_at_ns ||
                value.valid_until_ns > expected.MaximumValidUntilNanoseconds || value.route_point_count > ReceiverApproachMaximumRoutePoints ||
                value.route_point_stride != ReceiverApproachRoutePointStride || value.feasibility_status < 0 || value.feasibility_status > 12 ||
                !RichKnownDouble.Finite(value.start_x) || !RichKnownDouble.Finite(value.start_y) || !RichKnownDouble.Finite(value.safe_approach_x) ||
                !RichKnownDouble.Finite(value.safe_approach_y) || !RichKnownDouble.Finite(value.route_length_m) || value.route_length_m < 0)
                return ReceiverInputValidation.Fail("Native receiver approach header is incompatible with its exact owner/input context.", out error);
            if (value.feasibility_status == 0 && value.route_point_count < 2)
                return ReceiverInputValidation.Fail("Feasible receiver approach lacks a complete route.", out error);
            return true;
        }

        private static bool SameApproachHeader(SbReceiverApproachFeasibilityHeader a, SbReceiverApproachFeasibilityHeader b)
        {
            return a.feasibility_status == b.feasibility_status && a.registration_session_id == b.registration_session_id &&
                a.registration_generation == b.registration_generation && a.receiver_input_epoch == b.receiver_input_epoch &&
                a.receiver_input_sequence == b.receiver_input_sequence && a.receiver_input_revision == b.receiver_input_revision &&
                a.geometry_revision == b.geometry_revision && a.support_revision == b.support_revision && a.context_revision == b.context_revision &&
                a.calculation_revision == b.calculation_revision && a.owner_lifetime == b.owner_lifetime && a.native_clock_domain == b.native_clock_domain &&
                a.issued_at_ns == b.issued_at_ns && a.valid_until_ns == b.valid_until_ns && a.route_point_count == b.route_point_count &&
                a.route_point_stride == b.route_point_stride && BitConverter.DoubleToInt64Bits(a.start_x) == BitConverter.DoubleToInt64Bits(b.start_x) &&
                BitConverter.DoubleToInt64Bits(a.start_y) == BitConverter.DoubleToInt64Bits(b.start_y) &&
                BitConverter.DoubleToInt64Bits(a.safe_approach_x) == BitConverter.DoubleToInt64Bits(b.safe_approach_x) &&
                BitConverter.DoubleToInt64Bits(a.safe_approach_y) == BitConverter.DoubleToInt64Bits(b.safe_approach_y) &&
                BitConverter.DoubleToInt64Bits(a.route_length_m) == BitConverter.DoubleToInt64Bits(b.route_length_m);
        }
    }
}
