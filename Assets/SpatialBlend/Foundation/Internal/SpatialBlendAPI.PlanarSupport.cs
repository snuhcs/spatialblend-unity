// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbPlanarSupportFeature
        {
            public uint descriptor_size, feature_version, header_size, max_points;
            public uint coordinate_convention, plane_convention;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbPlanarSupportHeader
        {
            public uint struct_size, feature_version, definition_kind, geometry_role;
            public long space, element;
            public ulong tree_revision, support_revision;
            public uint point_count, plane_convention;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] plane_to_world_row_major;
        }

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_planar_support_feature")]
        private static extern uint PlanarFeatureRelease(out SbPlanarSupportFeature feature, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_planar_support_feature")]
        private static extern uint PlanarFeatureDebug(out SbPlanarSupportFeature feature, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_planar_support_field_count")]
        private static extern uint PlanarCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_planar_support_field_count")]
        private static extern uint PlanarCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_planar_support_field_offset")]
        private static extern uint PlanarOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_planar_support_field_offset")]
        private static extern uint PlanarOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_copy_planar_support")]
        private static extern int PlanarCopyRelease(long session, long space, long element, uint version,
            ulong tree, ulong support, [Out] double[] xy, uint capacity, out SbPlanarSupportHeader header, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_copy_planar_support")]
        private static extern int PlanarCopyDebug(long session, long space, long element, uint version,
            ulong tree, ulong support, [Out] double[] xy, uint capacity, out SbPlanarSupportHeader header, uint size);

        private static readonly Type[] PlanarTypes = { typeof(SbPlanarSupportFeature), typeof(SbPlanarSupportHeader) };
        private static readonly string[][] PlanarFields =
        {
            new[] { "descriptor_size", "feature_version", "header_size", "max_points", "coordinate_convention", "plane_convention" },
            new[] { "struct_size", "feature_version", "definition_kind", "geometry_role", "space", "element",
                "tree_revision", "support_revision", "point_count", "plane_convention", "plane_to_world_row_major" }
        };

        internal static bool TryGetPlanarSupportFeature(out SbPlanarSupportFeature feature, out string error)
        {
            feature = default;
            try
            {
                uint size = (uint)Marshal.SizeOf(PlanarTypes[0]);
                uint got = useDebugLibrary ? PlanarFeatureDebug(out feature, size) : PlanarFeatureRelease(out feature, size);
                if (got != size) return ReceiverInputValidation.Fail("Planar support descriptor size mismatch.", out error);
                return ValidatePlanarSupportFeature(feature,
                    kind => useDebugLibrary ? PlanarCountDebug(kind) : PlanarCountRelease(kind),
                    (kind, index) => useDebugLibrary ? PlanarOffsetDebug(kind, index) : PlanarOffsetRelease(kind, index), out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Planar support feature unavailable: " + e.Message, out error); }
        }

        internal static bool ValidatePlanarSupportFeature(SbPlanarSupportFeature feature,
            Func<uint, uint> count, Func<uint, uint, uint> offset, out string error)
        {
            error = null;
            if (feature.descriptor_size != Marshal.SizeOf(PlanarTypes[0]) || feature.feature_version != 1 ||
                feature.header_size != Marshal.SizeOf(PlanarTypes[1]) || feature.max_points != 65536 ||
                feature.coordinate_convention != 1 || feature.plane_convention != 1 || count == null || offset == null)
                return ReceiverInputValidation.Fail("Planar support descriptor is incompatible.", out error);
            for (int kind = 0; kind < PlanarTypes.Length; ++kind)
            {
                if (count((uint)kind + 1) != PlanarFields[kind].Length)
                    return ReceiverInputValidation.Fail("Planar support field count mismatch.", out error);
                for (int index = 0; index < PlanarFields[kind].Length; ++index)
                    if (offset((uint)kind + 1, (uint)index) != (uint)Marshal.OffsetOf(PlanarTypes[kind], PlanarFields[kind][index]).ToInt64())
                        return ReceiverInputValidation.Fail("Planar support field offset mismatch.", out error);
            }
            return true;
        }

        private static int CopyPlanar(long session, long space, long element, ulong tree, ulong support,
            double[] xy, out SbPlanarSupportHeader header)
        {
            uint size = (uint)Marshal.SizeOf(PlanarTypes[1]);
            uint capacity = xy == null ? 0u : (uint)(xy.Length / 2);
            return useDebugLibrary
                ? PlanarCopyDebug(session, space, element, 1, tree, support, xy, capacity, out header, size)
                : PlanarCopyRelease(session, space, element, 1, tree, support, xy, capacity, out header, size);
        }

        internal static bool TryCopyPlanarSupport(long session, long space, long element, ulong tree, ulong support,
            out PlanarSupportGeometry geometry, out string error)
        {
            geometry = null;
            if (session <= 0 || space <= 0 || element <= 0 || tree == 0 || support == 0)
                return ReceiverInputValidation.Fail("Planar support needs exact native ownership and known nonzero scene revisions.", out error);
            if (!TryGetPlanarSupportFeature(out _, out error)) return false;
            try
            {
                int result = CopyPlanar(session, space, element, tree, support, null, out var probe);
                if (result != -3)
                    return ReceiverInputValidation.Fail("Native support size probe rejected (result " + result + ").", out error);
                if (!ValidatePlanarSupportHeader(probe, space, element, tree, support, out error)) return false;
                var xy = new double[checked((int)probe.point_count * 2)];
                // Keep both expected revisions pinned. Never retry with a new stamp.
                result = CopyPlanar(session, space, element, tree, support, xy, out var copied);
                if (result != 1)
                    return ReceiverInputValidation.Fail("Native support copy rejected (result " + result + ").", out error);
                if (!ValidatePlanarSupportHeader(copied, space, element, tree, support, out error)) return false;
                if (copied.point_count != probe.point_count || copied.definition_kind != probe.definition_kind ||
                    copied.geometry_role != probe.geometry_role || !SamePlanarMatrix(probe.plane_to_world_row_major, copied.plane_to_world_row_major))
                    return ReceiverInputValidation.Fail("Native support changed within the pinned geometry read.", out error);
                foreach (double value in xy)
                    if (!RichKnownDouble.Finite(value))
                        return ReceiverInputValidation.Fail("Native support boundary is not finite.", out error);
                geometry = new PlanarSupportGeometry(copied, xy);
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Native support read unavailable: " + e.Message, out error); }
        }

        internal static bool ValidatePlanarSupportHeader(SbPlanarSupportHeader header, long space, long element,
            ulong tree, ulong support, out string error)
        {
            error = null;
            double[] m = header.plane_to_world_row_major;
            if (header.struct_size != Marshal.SizeOf(PlanarTypes[1]) || header.feature_version != 1 ||
                header.space != space || header.element != element || header.tree_revision != tree ||
                header.support_revision != support || header.point_count < 3 || header.point_count > 65536 ||
                header.plane_convention != 1 || (header.definition_kind != 1 && header.definition_kind != 2) ||
                header.geometry_role > 5 || m == null || m.Length != 16)
                return ReceiverInputValidation.Fail("Native support header is incompatible with the requested geometry.", out error);
            foreach (double value in m)
                if (!RichKnownDouble.Finite(value)) return ReceiverInputValidation.Fail("Native support plane is not finite.", out error);
            double determinant = m[0] * (m[5] * m[10] - m[6] * m[9]) -
                m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
            if (Math.Abs(m[12]) > 1e-12 || Math.Abs(m[13]) > 1e-12 || Math.Abs(m[14]) > 1e-12 ||
                Math.Abs(m[15] - 1) > 1e-12 || !RichKnownDouble.Finite(determinant) || Math.Abs(determinant) <= 1e-12)
                return ReceiverInputValidation.Fail("Native support plane must be finite invertible affine XY-to-world.", out error);
            return true;
        }

        internal static bool SamePlanarMatrix(System.Collections.Generic.IReadOnlyList<double> a,
            System.Collections.Generic.IReadOnlyList<double> b)
        {
            if (a == null || b == null || a.Count != 16 || b.Count != 16) return false;
            for (int i = 0; i < 16; ++i)
                if (BitConverter.DoubleToInt64Bits(a[i]) != BitConverter.DoubleToInt64Bits(b[i])) return false;
            return true;
        }
    }
}
