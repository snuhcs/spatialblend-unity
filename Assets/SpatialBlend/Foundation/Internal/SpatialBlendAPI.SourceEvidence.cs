// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        internal const long SourceEvidenceMaximumAgeNs = 1000000000;
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbSourceFeature
        {
            public uint descriptor_size, feature_version, header_size, anchor_size, relation_size, node_size, copy_size;
            public uint max_anchors, max_relations, max_path_depth, max_nodes, max_points, max_name_bytes, max_utf8_bytes, coordinate_convention;
            public ulong max_age_ns, monotonic_clock_id, monotonic_clock_epoch;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbSourceHeader
        {
            public uint struct_size, feature_version;
            public long space, actor, actor_element, actor_shared;
            public ulong epoch;
            public long sequence;
            public double source_time_seconds;
            public long receipt_monotonic_ns;
            public ulong monotonic_clock_id, monotonic_clock_epoch, tree_revision, support_revision;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbSourceAnchor
        {
            public uint name_offset, name_length, observed, locomotion_root, position_dimension;
            public double position_x, position_y, position_z, yaw;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbSourceRelation
        {
            public uint binding_ordinal, anchor_ordinal;
            public int kernel;
            public long explicit_scope;
            public uint filter_offset, filter_length;
            public double proximity, normalized_weight;
            public int phase;
            public uint phase_identity_unambiguous;
            public long phase_referent_id;
            public uint path_offset, path_count, has_projection, bounded_planar_support;
            public double projected_x, projected_y, projected_z, anchor_support_distance;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] support_to_world_row_major;
            public uint point_offset, point_count, sub_region_offset, sub_region_length;
            public double support_angle, support_radial_ratio;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbSourceNode
        {
            public long element, parent, shared;
            public uint tag_offset, tag_length, geometry_role;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] world_transform_row_major;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SbSourceCopy
        {
            public uint struct_size, anchor_capacity, relation_capacity, node_capacity, point_capacity, utf8_capacity;
            public IntPtr anchors, relations, nodes, boundary_xy, utf8;
            public SbSourceHeader header;
            public uint anchor_count, relation_count, node_count, point_count, utf8_bytes;
        }

        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_source_evidence_feature")]
        private static extern uint SourceFeatureRelease(out SbSourceFeature value, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_source_evidence_feature")]
        private static extern uint SourceFeatureDebug(out SbSourceFeature value, uint size);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_source_evidence_field_count")]
        private static extern uint SourceCountRelease(uint kind);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_source_evidence_field_count")]
        private static extern uint SourceCountDebug(uint kind);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_source_evidence_field_offset")]
        private static extern uint SourceOffsetRelease(uint kind, uint index);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_get_source_evidence_field_offset")]
        private static extern uint SourceOffsetDebug(uint kind, uint index);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_enable_source_evidence")]
        private static extern int SourceEnableRelease(long session, long space, long actor, uint version, ulong epoch, uint enabled);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_try_enable_source_evidence")]
        private static extern int SourceEnableDebug(long session, long space, long actor, uint version, ulong epoch, uint enabled);
        [DllImport(ReleaseRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_copy_source_evidence")]
        private static extern int SourceCopyRelease(long session, long space, long actor, uint version, ulong epoch,
            long sequence, long age, ref SbSourceCopy copy, uint size);
        [DllImport(DebugRich, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sb_copy_source_evidence")]
        private static extern int SourceCopyDebug(long session, long space, long actor, uint version, ulong epoch,
            long sequence, long age, ref SbSourceCopy copy, uint size);

        internal static readonly Type[] SourceEvidenceTypes = { typeof(SbSourceFeature), typeof(SbSourceHeader),
            typeof(SbSourceAnchor), typeof(SbSourceRelation), typeof(SbSourceNode), typeof(SbSourceCopy) };
        internal static readonly string[][] SourceEvidenceFields =
        {
            new[] { "descriptor_size", "feature_version", "header_size", "anchor_size", "relation_size", "node_size", "copy_size",
                "max_anchors", "max_relations", "max_path_depth", "max_nodes", "max_points", "max_name_bytes", "max_utf8_bytes",
                "coordinate_convention", "max_age_ns", "monotonic_clock_id", "monotonic_clock_epoch" },
            new[] { "struct_size", "feature_version", "space", "actor", "actor_element", "actor_shared", "epoch", "sequence",
                "source_time_seconds", "receipt_monotonic_ns", "monotonic_clock_id", "monotonic_clock_epoch", "tree_revision", "support_revision" },
            new[] { "name_offset", "name_length", "observed", "locomotion_root", "position_dimension", "position_x", "position_y", "position_z", "yaw" },
            new[] { "binding_ordinal", "anchor_ordinal", "kernel", "explicit_scope", "filter_offset", "filter_length", "proximity",
                "normalized_weight", "phase", "phase_identity_unambiguous", "phase_referent_id", "path_offset", "path_count",
                "has_projection", "bounded_planar_support", "projected_x", "projected_y", "projected_z", "anchor_support_distance",
                "support_to_world_row_major", "point_offset", "point_count", "sub_region_offset", "sub_region_length", "support_angle", "support_radial_ratio" },
            new[] { "element", "parent", "shared", "tag_offset", "tag_length", "geometry_role", "world_transform_row_major" },
            new[] { "struct_size", "anchor_capacity", "relation_capacity", "node_capacity", "point_capacity", "utf8_capacity",
                "anchors", "relations", "nodes", "boundary_xy", "utf8", "header", "anchor_count", "relation_count", "node_count", "point_count", "utf8_bytes" }
        };
        private static bool _sourceFeatureKnown, _sourceFeatureDebug;
        private static SbSourceFeature _sourceFeature;
        private static readonly UTF8Encoding SourceUtf8 = new UTF8Encoding(false, true);

        internal static bool TryGetSourceEvidenceFeature(out SbSourceFeature feature, out string error)
        {
            feature = default;
            try
            {
                uint size = (uint)Marshal.SizeOf<SbSourceFeature>();
                uint got = useDebugLibrary ? SourceFeatureDebug(out feature, size) : SourceFeatureRelease(out feature, size);
                if (got != size) return ReceiverInputValidation.Fail("Source evidence descriptor size mismatch.", out error);
                return ValidateSourceEvidenceFeature(feature,
                    k => useDebugLibrary ? SourceCountDebug(k) : SourceCountRelease(k),
                    (k, i) => useDebugLibrary ? SourceOffsetDebug(k, i) : SourceOffsetRelease(k, i), out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            { return ReceiverInputValidation.Fail("Optional source evidence feature unavailable: " + e.Message, out error); }
        }

        internal static bool ValidateSourceEvidenceFeature(SbSourceFeature f, Func<uint, uint> count,
            Func<uint, uint, uint> offset, out string error)
        {
            error = null;
            uint[] sizes = { f.descriptor_size, f.header_size, f.anchor_size, f.relation_size, f.node_size, f.copy_size };
            if (f.feature_version != 1 || f.max_anchors != 128 || f.max_relations != 256 || f.max_path_depth != 64 ||
                f.max_nodes != 8192 || f.max_points != 65536 || f.max_name_bytes != 128 || f.max_utf8_bytes != 2097152 ||
                f.coordinate_convention != 1 || f.max_age_ns != SourceEvidenceMaximumAgeNs ||
                f.monotonic_clock_id != 1 || f.monotonic_clock_epoch == 0 || count == null || offset == null)
                return ReceiverInputValidation.Fail("Source evidence descriptor is incompatible.", out error);
            for (int k = 0; k < sizes.Length; ++k)
            {
                if (sizes[k] != Marshal.SizeOf(SourceEvidenceTypes[k]) || count((uint)k + 1) != SourceEvidenceFields[k].Length)
                    return ReceiverInputValidation.Fail("Source evidence layout size/count mismatch.", out error);
                for (int i = 0; i < SourceEvidenceFields[k].Length; ++i)
                    if (offset((uint)k + 1, (uint)i) != (uint)Marshal.OffsetOf(SourceEvidenceTypes[k], SourceEvidenceFields[k][i]).ToInt64())
                        return ReceiverInputValidation.Fail("Source evidence layout offset mismatch.", out error);
            }
            return true;
        }

        private static bool EnsureSourceEvidenceFeature(out SbSourceFeature feature)
        {
            if (!_sourceFeatureKnown || _sourceFeatureDebug != useDebugLibrary)
            {
                _sourceFeatureKnown = false;
                if (!TryGetSourceEvidenceFeature(out feature, out _)) return false;
                _sourceFeature = feature; _sourceFeatureDebug = useDebugLibrary; _sourceFeatureKnown = true;
            }
            feature = _sourceFeature;
            return true;
        }
        private static SourceInteractionEvidenceStatus SourceStatus(int result) => result == 1
            ? SourceInteractionEvidenceStatus.Available : result == 0 ? SourceInteractionEvidenceStatus.Unavailable
            : result == -2 ? SourceInteractionEvidenceStatus.Stale : result == -4
            ? SourceInteractionEvidenceStatus.Busy : SourceInteractionEvidenceStatus.Incompatible;

        internal static SourceInteractionEvidenceStatus TryEnableSourceEvidence(long session, long space, long actor, ulong epoch, bool enabled)
        {
            if (!EnsureSourceEvidenceFeature(out _)) return SourceInteractionEvidenceStatus.Incompatible;
            try { return SourceStatus(useDebugLibrary ? SourceEnableDebug(session, space, actor, 1, epoch, enabled ? 1u : 0u)
                : SourceEnableRelease(session, space, actor, 1, epoch, enabled ? 1u : 0u)); }
            catch (Exception e) when (IsRichLoadFailure(e)) { return SourceInteractionEvidenceStatus.Incompatible; }
        }
        private static int CopySource(long session, long space, long actor, ulong epoch, long sequence, long age, ref SbSourceCopy copy)
            => useDebugLibrary ? SourceCopyDebug(session, space, actor, 1, epoch, sequence, age, ref copy, (uint)Marshal.SizeOf<SbSourceCopy>())
                : SourceCopyRelease(session, space, actor, 1, epoch, sequence, age, ref copy, (uint)Marshal.SizeOf<SbSourceCopy>());

        private static bool ValidSourceProbe(SbSourceCopy c, SbSourceFeature f, long space, long actor, ulong epoch, long sequence)
        {
            var h = c.header;
            return h.struct_size == Marshal.SizeOf<SbSourceHeader>() && h.feature_version == 1 && h.space == space &&
                h.actor == actor && h.actor_element > 0 && h.actor_shared >= -1 && h.epoch == epoch && h.sequence == sequence &&
                RichKnownDouble.Finite(h.source_time_seconds) && h.source_time_seconds >= 0 && h.receipt_monotonic_ns > 0 &&
                h.monotonic_clock_id == f.monotonic_clock_id && h.monotonic_clock_epoch == f.monotonic_clock_epoch &&
                h.tree_revision != 0 && h.support_revision != 0 && c.anchor_count > 0 && c.anchor_count <= f.max_anchors &&
                c.relation_count <= f.max_relations && c.node_count <= f.max_nodes && c.point_count <= f.max_points &&
                c.utf8_bytes > 0 && c.utf8_bytes <= f.max_utf8_bytes;
        }

        internal static SourceInteractionEvidenceStatus TryValidateSourceEvidence(long session, SourceInteractionEvidence evidence, long age)
        {
            if (evidence == null || age <= 0 || age > SourceEvidenceMaximumAgeNs || !EnsureSourceEvidenceFeature(out var f))
                return SourceInteractionEvidenceStatus.Incompatible;
            var copy = new SbSourceCopy { struct_size = (uint)Marshal.SizeOf<SbSourceCopy>() };
            try
            {
                int result = CopySource(session, evidence.NativeSpace, evidence.NativeActor, evidence.Epoch, evidence.Sequence, age, ref copy);
                if (result != -3) return SourceStatus(result == 1 ? -1 : result);
                return ValidSourceProbe(copy, f, evidence.NativeSpace, evidence.NativeActor, evidence.Epoch, evidence.Sequence) &&
                    copy.header.Equals(evidence.NativeHeader) ? SourceInteractionEvidenceStatus.Available : SourceInteractionEvidenceStatus.Stale;
            }
            catch (Exception e) when (IsRichLoadFailure(e)) { return SourceInteractionEvidenceStatus.Incompatible; }
        }

        internal static SourceInteractionEvidenceStatus TryCopySourceEvidence(long session, long space, long actor, ulong epoch,
            long sequence, long age, out SourceInteractionEvidence evidence)
        {
            evidence = null;
            if (age <= 0 || age > SourceEvidenceMaximumAgeNs || !EnsureSourceEvidenceFeature(out var feature))
                return SourceInteractionEvidenceStatus.Incompatible;
            var copy = new SbSourceCopy { struct_size = (uint)Marshal.SizeOf<SbSourceCopy>() };
            try
            {
                int result = CopySource(session, space, actor, epoch, sequence, age, ref copy);
                if (result != -3) return SourceStatus(result == 1 ? -1 : result);
                if (!ValidSourceProbe(copy, feature, space, actor, epoch, sequence)) return SourceInteractionEvidenceStatus.Incompatible;
                var probe = copy;
                copy.anchor_capacity = copy.anchor_count; copy.relation_capacity = copy.relation_count;
                copy.node_capacity = copy.node_count; copy.point_capacity = copy.point_count; copy.utf8_capacity = copy.utf8_bytes;
                copy.anchors = AllocateSource<SbSourceAnchor>(copy.anchor_capacity);
                copy.relations = AllocateSource<SbSourceRelation>(copy.relation_capacity);
                copy.nodes = AllocateSource<SbSourceNode>(copy.node_capacity);
                copy.boundary_xy = AllocateSource<double>(checked(copy.point_capacity * 2));
                copy.utf8 = AllocateSource<byte>(copy.utf8_capacity);
                result = CopySource(session, space, actor, epoch, sequence, age, ref copy);
                if (result != 1) return SourceStatus(result);
                if (!ValidSourceProbe(copy, feature, space, actor, epoch, sequence) || !copy.header.Equals(probe.header) ||
                    copy.anchor_count != probe.anchor_count || copy.relation_count != probe.relation_count ||
                    copy.node_count != probe.node_count || copy.point_count != probe.point_count || copy.utf8_bytes != probe.utf8_bytes)
                    return SourceInteractionEvidenceStatus.Stale;
                var xy = new double[checked((int)copy.point_count * 2)];
                var bytes = new byte[copy.utf8_bytes];
                if (xy.Length != 0) Marshal.Copy(copy.boundary_xy, xy, 0, xy.Length);
                Marshal.Copy(copy.utf8, bytes, 0, bytes.Length);
                evidence = DecodeSourceEvidence(copy.header, ReadSourceRows<SbSourceAnchor>(copy.anchors, copy.anchor_count),
                    ReadSourceRows<SbSourceRelation>(copy.relations, copy.relation_count),
                    ReadSourceRows<SbSourceNode>(copy.nodes, copy.node_count), xy, bytes);
                return SourceInteractionEvidenceStatus.Available;
            }
            catch (Exception e) when (IsRichLoadFailure(e) || e is InvalidDataException || e is ArgumentException || e is OverflowException)
            { evidence = null; return SourceInteractionEvidenceStatus.Incompatible; }
            finally
            {
                Marshal.FreeHGlobal(copy.anchors); Marshal.FreeHGlobal(copy.relations); Marshal.FreeHGlobal(copy.nodes);
                Marshal.FreeHGlobal(copy.boundary_xy); Marshal.FreeHGlobal(copy.utf8);
            }
        }
        private static IntPtr AllocateSource<T>(uint count) => count == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(checked((int)count * Marshal.SizeOf<T>()));
        private static T[] ReadSourceRows<T>(IntPtr pointer, uint count)
        {
            var rows = new T[count]; int size = Marshal.SizeOf<T>();
            for (int i = 0; i < rows.Length; ++i) rows[i] = Marshal.PtrToStructure<T>(IntPtr.Add(pointer, checked(i * size)));
            return rows;
        }
        private static void SourceRequire(bool condition)
        { if (!condition) throw new InvalidDataException("Malformed source interaction evidence."); }
        private static string SourceName(byte[] bytes, uint offset, uint length, bool nonempty = false)
        {
            SourceRequire(length < 128 && (!nonempty || length != 0) && offset <= bytes.Length && length <= bytes.Length - offset);
            string name = SourceUtf8.GetString(bytes, (int)offset, (int)length);
            SourceRequire(name.IndexOf('\0') < 0); return name;
        }
        private static bool SourceMatrix(double[] m)
        {
            if (m == null || m.Length != 16) return false;
            foreach (double x in m) if (!RichKnownDouble.Finite(x)) return false;
            double d = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
            return Math.Abs(m[12]) <= 1e-12 && Math.Abs(m[13]) <= 1e-12 && Math.Abs(m[14]) <= 1e-12 &&
                Math.Abs(m[15] - 1) <= 1e-12 && RichKnownDouble.Finite(d) && Math.Abs(d) > 1e-12;
        }
        internal static SourceInteractionEvidence DecodeSourceEvidence(SbSourceHeader header, SbSourceAnchor[] anchors,
            SbSourceRelation[] relations, SbSourceNode[] nodes, double[] xy, byte[] bytes)
        {
            SourceRequire(anchors != null && anchors.Length > 0 && anchors.Length <= 128 && relations != null && relations.Length <= 256 &&
                nodes != null && nodes.Length <= 8192 && xy != null && xy.Length <= 131072 && xy.Length % 2 == 0 &&
                bytes != null && bytes.Length > 0 && bytes.Length <= 2097152);
            var decodedAnchors = new SourceInteractionAnchor[anchors.Length];
            var names = new HashSet<string>(StringComparer.Ordinal); int roots = 0;
            for (int i = 0; i < anchors.Length; ++i)
            {
                var a = anchors[i]; string name = SourceName(bytes, a.name_offset, a.name_length, true);
                SourceRequire(names.Add(name) && a.observed <= 1 && a.locomotion_root <= 1 &&
                    (a.position_dimension == 2 || a.position_dimension == 3) && RichKnownDouble.Finite(a.position_x) &&
                    RichKnownDouble.Finite(a.position_y) && RichKnownDouble.Finite(a.position_z) && RichKnownDouble.Finite(a.yaw));
                if (a.locomotion_root != 0) { ++roots; SourceRequire(a.position_dimension == 2 && a.position_z == 0); }
                decodedAnchors[i] = new SourceInteractionAnchor(name, a);
            }
            SourceRequire(roots == 1);
            foreach (double v in xy) SourceRequire(RichKnownDouble.Finite(v));
            var decodedRelations = new SourceInteractionRelation[relations.Length];
            uint pathEnd = 0, pointEnd = 0;
            for (int i = 0; i < relations.Length; ++i)
            {
                var r = relations[i];
                SourceRequire(r.anchor_ordinal < anchors.Length && anchors[r.anchor_ordinal].observed == 1 && r.kernel >= 0 && r.kernel <= 2 &&
                    (r.explicit_scope == -1 || r.explicit_scope > 0) && r.phase >= 0 && r.phase <= 3 && r.phase_identity_unambiguous <= 1 &&
                    r.phase_referent_id >= -1 && r.path_count > 0 && r.path_count <= 64 && r.path_offset == pathEnd &&
                    r.path_offset <= nodes.Length && r.path_count <= nodes.Length - r.path_offset &&
                    r.point_offset == pointEnd && r.point_offset <= xy.Length / 2 && r.point_count <= xy.Length / 2 - r.point_offset &&
                    r.has_projection <= 1 && r.bounded_planar_support <= 1 &&
                    (r.bounded_planar_support == 0 || (r.has_projection == 1 && r.point_count >= 3)) &&
                    RichKnownDouble.Finite(r.proximity) && r.proximity >= 0 && r.proximity <= 1 &&
                    RichKnownDouble.Finite(r.normalized_weight) && r.normalized_weight >= 0 && r.normalized_weight <= 1 &&
                    RichKnownDouble.Finite(r.projected_x) && RichKnownDouble.Finite(r.projected_y) && RichKnownDouble.Finite(r.projected_z) &&
                    RichKnownDouble.Finite(r.anchor_support_distance) && r.anchor_support_distance >= 0 &&
                    RichKnownDouble.Finite(r.support_angle) && RichKnownDouble.Finite(r.support_radial_ratio) && SourceMatrix(r.support_to_world_row_major));
                var path = new SourceInteractionHierarchyNode[r.path_count]; var visited = new HashSet<long>(); long parent = -1;
                for (int j = 0; j < path.Length; ++j)
                {
                    var n = nodes[r.path_offset + j];
                    SourceRequire(n.element > 0 && n.parent == parent && n.shared >= -1 && visited.Add(n.element) &&
                        n.geometry_role <= 5 && SourceMatrix(n.world_transform_row_major));
                    path[j] = new SourceInteractionHierarchyNode(SourceName(bytes, n.tag_offset, n.tag_length), n); parent = n.element;
                }
                var boundary = new double[r.point_count * 2]; Array.Copy(xy, r.point_offset * 2, boundary, 0, boundary.Length);
                decodedRelations[i] = new SourceInteractionRelation(r, SourceName(bytes, r.filter_offset, r.filter_length),
                    SourceName(bytes, r.sub_region_offset, r.sub_region_length), path, boundary);
                pathEnd += r.path_count; pointEnd += r.point_count;
            }
            SourceRequire(pathEnd == nodes.Length && pointEnd == xy.Length / 2);
            return new SourceInteractionEvidence(header, decodedAnchors, decodedRelations);
        }
    }
}
