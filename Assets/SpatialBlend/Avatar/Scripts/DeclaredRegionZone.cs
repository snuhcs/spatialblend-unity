// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// One declared Containment Region as the ZoneBased test reads it. The real
    /// source is <c>SbSpatialAuthoring</c>. Tests supply a fake.
    /// </summary>
    public interface IDeclaredRegion
    {
        /// <summary>
        /// The region polygon in the object's local XZ plane (x, z) and the world
        /// to object-local matrix. False when the region cannot be resolved.
        /// </summary>
        bool TryGetRegion(out Vector2[] localPolygon, out Matrix4x4 worldToLocal);
    }

    /// <summary>
    /// The ZoneBased display flag. ZoneBased is the paper's fixed-zone baseline, so the flag has no hysteresis and no
    /// dwell, and it never reads the v1 scene volume component or a core relation event. It follows the core's
    /// interaction value: for a received avatar the TRANSFER flag of its root proposal AND that anchor's ARRIVED, for
    /// the local avatar the source-side transfer fact (<c>sb_get_source_transfer</c>).
    /// The core is the single judgment of where an avatar is relative to a declared region; the app holds no point test
    /// of its own.
    /// </summary>
    public static class DeclaredRegionZone
    {
        public sealed class Switch
        {
            public bool Inside { get; private set; }

            /// <summary>Sets the flag from the core's value. Returns true when this sample changed it.</summary>
            public bool Set(bool inside)
            {
                if (inside == Inside) return false;
                Inside = inside;
                return true;
            }

            public void Reset() => Inside = false;
        }
    }
}
