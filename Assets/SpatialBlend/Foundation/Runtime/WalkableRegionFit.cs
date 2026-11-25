// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Pure check of an authored region against the room's walkable ring, in ground (x, z) coordinates. The core
    /// refuses a Containment region that sticks out of its Navigable parent (ContainmentEscapesParent), so a virtual
    /// object is moved before its region is written (<see cref="WalkablePlacementSnap"/>). This test is looser than the
    /// core's (touching within <see cref="Tolerance"/>): it rejects what the snap's vertex rules miss, a reflex corner
    /// reaching in or an edge crossing, and never stands in for the snap's margin.
    /// </summary>
    public static class WalkableRegionFit
    {
        /// <summary>The length below which a point counts as on a ring: touching the parent's edge is allowed.</summary>
        public const float Tolerance = 1e-3f;

        /// <summary>
        /// True when <paramref name="polygon"/> lies inside <paramref name="ring"/>, touching allowed. Vertices alone do not
        /// decide it for a ring that is not convex: a reflex corner of the ring (the notch the NavMesh cuts around furniture
        /// at a wall) can reach into the polygon, and a thin spike of the outside can cross it, with every polygon vertex inside.
        /// </summary>
        public static bool Contains(IReadOnlyList<Vector2> ring, IReadOnlyList<Vector2> polygon, float tolerance = Tolerance)
        {
            if (ring == null || ring.Count < 3 || polygon == null || polygon.Count < 3) return false;
            for (int i = 0; i < polygon.Count; ++i)
            {
                if (!WalkablePlacementSnap.Contains(ring, polygon[i]) && DistanceToRing(polygon[i], ring) > tolerance)
                    return false;
            }
            for (int i = 0; i < ring.Count; ++i)
            {
                if (WalkablePlacementSnap.Contains(polygon, ring[i]) && DistanceToRing(ring[i], polygon) > tolerance)
                    return false;
            }
            return !EdgesCross(polygon, ring, tolerance);
        }

        /// <summary>True when an edge of <paramref name="a"/> properly crosses an edge of <paramref name="b"/>.</summary>
        public static bool EdgesCross(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b, float tolerance = Tolerance)
        {
            for (int i = 0; i < a.Count; ++i)
            {
                Vector2 p = a[i], q = a[(i + 1) % a.Count];
                for (int j = 0; j < b.Count; ++j)
                {
                    if (CrossProperly(p, q, b[j], b[(j + 1) % b.Count], tolerance)) return true;
                }
            }
            return false;
        }

        private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        // Each segment's ends lie strictly on opposite sides of the other's line, beyond the tolerance as a length.
        private static bool CrossProperly(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float tolerance)
        {
            float ab = (b - a).magnitude, cd = (d - c).magnitude;
            if (ab <= tolerance || cd <= tolerance) return false;
            float c1 = Cross(b - a, c - a) / ab, d1 = Cross(b - a, d - a) / ab;
            float a1 = Cross(d - c, a - c) / cd, b1 = Cross(d - c, b - c) / cd;
            return ((c1 > tolerance && d1 < -tolerance) || (c1 < -tolerance && d1 > tolerance)) &&
                   ((a1 > tolerance && b1 < -tolerance) || (a1 < -tolerance && b1 > tolerance));
        }

        private static float DistanceToRing(Vector2 point, IReadOnlyList<Vector2> ring)
        {
            float best = float.MaxValue;
            for (int i = 0; i < ring.Count; ++i)
            {
                Vector2 a = ring[i], edge = ring[(i + 1) % ring.Count] - a;
                float length = edge.sqrMagnitude;
                float t = length > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / length) : 0f;
                best = Mathf.Min(best, (point - (a + edge * t)).magnitude);
            }
            return best;
        }
    }
}
