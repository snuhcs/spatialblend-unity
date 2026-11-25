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
    using ActorID = System.Int64;
    using ElementID = System.Int64;

    // Mirrors sb_containment_transfer_feature_t (feature version 3, ABI 4.3). Field offsets are checked against
    // sb_get_containment_transfer_field_offset(1, i) before the feature is used. ABI 4.3 has no contained event and no
    // per-actor containment state to read: the fact that an actor is inside a transfer leaf is the per-update
    // SB_ANCHOR_PROPOSAL_FLAG_TRANSFER of its root proposal (AnchorProposalFlags.Transfer) and the CONTAIN_ENTER /
    // CONTAIN_EXIT cause bits of the jump update.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SbContainmentTransferFeature
    {
        public uint descriptor_size, feature_version, coordinate_convention, reserved;
    }

    /// <summary>
    /// Containment transfer: element-local region polygon and the per-element
    /// declaration of a transfer leaf. Every entry point lives
    /// in the additive header <c>spatialblend_containment_transfer.h</c>; a library built before it
    /// reports the feature unavailable instead of failing, and the app falls back to the
    /// scene-level relations.
    /// </summary>
    internal static partial class SpatialBlendAPI
    {
        internal const uint ContainmentFeatureVersion = 4;
        private const uint ContainmentFeatureKind = 1;

        // Field names in declaration order, exactly the order sb_get_containment_transfer_field_offset indexes.
        internal static readonly string[] ContainmentFeatureFields =
        {
            "descriptor_size", "feature_version", "coordinate_convention", "reserved"
        };

        private static class ContainmentRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_containment_transfer_feature(out SbContainmentTransferFeature feature, uint size);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_containment_transfer_field_count(uint kind);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_containment_transfer_field_offset(uint kind, uint index);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_local_polygon(long space, long elem, SbGeometryRole role, double[] pointsXy, long numPoints);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_containment_transfer(long space, long elem, int enabled);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_source_transfer(long space, long actor, out long leafSharedId);
        }

        private static class ContainmentDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_containment_transfer_feature(out SbContainmentTransferFeature feature, uint size);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_containment_transfer_field_count(uint kind);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint sb_get_containment_transfer_field_offset(uint kind, uint index);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_local_polygon(long space, long elem, SbGeometryRole role, double[] pointsXy, long numPoints);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_set_element_containment_transfer(long space, long elem, int enabled);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_source_transfer(long space, long actor, out long leafSharedId);
        }

        /// <summary>
        /// Negotiates the additive feature: descriptor size and version, the Z-up convention, and
        /// every field offset of the feature struct against the managed mirrors. False (with the reason)
        /// for a library without the header, which is not an error: the app then keeps the
        /// scene-level relations and the pure region fallback.
        /// </summary>
        internal static bool TryValidateContainmentAbi(out string error)
        {
            error = string.Empty;
            try
            {
                uint size = (uint)Marshal.SizeOf<SbContainmentTransferFeature>();
                SbContainmentTransferFeature feature;
                uint got = useDebugLibrary
                    ? ContainmentDebug.sb_get_containment_transfer_feature(out feature, size)
                    : ContainmentRelease.sb_get_containment_transfer_feature(out feature, size);
                return ValidateContainmentFeature(feature, got,
                    kind => useDebugLibrary
                        ? ContainmentDebug.sb_get_containment_transfer_field_count(kind)
                        : ContainmentRelease.sb_get_containment_transfer_field_count(kind),
                    (kind, index) => useDebugLibrary
                        ? ContainmentDebug.sb_get_containment_transfer_field_offset(kind, index)
                        : ContainmentRelease.sb_get_containment_transfer_field_offset(kind, index),
                    out error);
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                error = "Containment transfer is unavailable in the loaded library: " + e.Message;
                return false;
            }
        }

        /// <summary>Pure half of the negotiation, so a test can drive it without a native library.</summary>
        internal static bool ValidateContainmentFeature(SbContainmentTransferFeature feature, uint returnedSize,
            Func<uint, uint> fieldCount, Func<uint, uint, uint> fieldOffset, out string error)
        {
            error = string.Empty;
            uint featureSize = (uint)Marshal.SizeOf<SbContainmentTransferFeature>();
            if (returnedSize != featureSize || feature.descriptor_size != featureSize)
            {
                error = "Containment descriptor size mismatch.";
                return false;
            }
            if (feature.feature_version != ContainmentFeatureVersion)
            {
                error = "Containment feature version " + feature.feature_version + " is not " +
                        ContainmentFeatureVersion + ".";
                return false;
            }
            if (feature.coordinate_convention != 1u)
            {
                error = "Containment coordinate convention " + feature.coordinate_convention + " is not Z-up metres.";
                return false;
            }
            if (fieldCount == null || fieldOffset == null)
            {
                error = "Containment field probes are missing.";
                return false;
            }
            return ValidateContainmentFields(ContainmentFeatureKind, typeof(SbContainmentTransferFeature),
                       ContainmentFeatureFields, fieldCount, fieldOffset, out error);
        }

        private static bool ValidateContainmentFields(uint kind, Type managed, string[] fields,
            Func<uint, uint> fieldCount, Func<uint, uint, uint> fieldOffset, out string error)
        {
            error = string.Empty;
            if (fieldCount(kind) != (uint)fields.Length)
            {
                error = "Containment " + managed.Name + " field count native=" + fieldCount(kind) +
                        " managed=" + fields.Length + ".";
                return false;
            }
            for (uint index = 0; index < fields.Length; ++index)
            {
                uint nativeOffset = fieldOffset(kind, index);
                uint managedOffset = (uint)Marshal.OffsetOf(managed, fields[index]).ToInt32();
                if (nativeOffset != managedOffset)
                {
                    error = "Containment " + managed.Name + "." + fields[index] + " native=" + nativeOffset +
                            " managed=" + managedOffset + ".";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Writes an element-LOCAL polygon (sb_set_element_local_polygon): the points are in the
        /// element's own frame, so the region follows later transforms of the element or its
        /// ancestors. Unity (x, z) maps to core (x, y) unchanged. False when the library lacks the
        /// entry point or the core refuses the input.
        /// </summary>
        internal static bool TrySetElementLocalPolygon(SpaceID space, ElementID elem, SbGeometryRole role,
                                                       IReadOnlyList<Vector2> unityPoints)
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
            try
            {
                return (useDebugLibrary
                    ? ContainmentDebug.sb_set_element_local_polygon(space, elem, role, points, unityPoints.Count)
                    : ContainmentRelease.sb_set_element_local_polygon(space, elem, role, points, unityPoints.Count)) == 1;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                return false;
            }
        }

        /// <summary>
        /// Declares (or clears) a Containment element as a transfer leaf. Declare it on both sides. False when the element has
        /// no Containment definition yet, the id is unknown, or the library lacks the entry point. The declaration is never
        /// refused for its shape: congruence of the two leaves is judged by the core on every update, as part of the
        /// receiver's TRANSFER flag.
        /// </summary>
        internal static bool TrySetElementContainmentTransfer(SpaceID space, ElementID elem, bool enabled)
        {
            try
            {
                return (useDebugLibrary
                    ? ContainmentDebug.sb_set_element_containment_transfer(space, elem, enabled ? 1 : 0)
                    : ContainmentRelease.sb_set_element_containment_transfer(space, elem, enabled ? 1 : 0)) == 1;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                return false;
            }
        }

        /// <summary>
        /// The source-side transfer fact (ABI 4.4): which transfer leaf the local actor's root was inside at its latest
        /// update, as the core computed it. True when the core answered; <paramref name="leafSharedId"/> is then the leaf's
        /// shared id, or 0 when the root is inside none. False for an unknown actor or a library without the entry point.
        /// </summary>
        internal static bool TryGetSourceTransfer(SpaceID space, ActorID actor, out long leafSharedId)
        {
            leafSharedId = 0;
            try
            {
                long leaf;
                int result = useDebugLibrary
                    ? ContainmentDebug.sb_get_source_transfer(space, actor, out leaf)
                    : ContainmentRelease.sb_get_source_transfer(space, actor, out leaf);
                if (result < 0) return false;
                leafSharedId = result == 1 ? leaf : 0;
                return true;
            }
            catch (Exception e) when (IsRichLoadFailure(e))
            {
                return false;
            }
        }
    }
}
