// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Tests.RoomMatrix
{
    /// <summary>
    /// The room matrix's own geometry: containment and overlap written here from first principles (ray-cast
    /// point in polygon, segment crossings and a sampled overlap), sharing no code with RoomFit, RegionPull or WalkablePlacementSnap,
    /// so a placement the product calls valid is checked by a second, separate rule.
    /// </summary>
    internal static class IndependentGeometry
    {
        /// <summary>Even-odd ray cast: whether <paramref name="p"/> is inside <paramref name="poly"/> (either winding).</summary>
        public static bool Inside(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>Distance from <paramref name="p"/> to the polygon's boundary.</summary>
        public static float BoundaryDistance(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 0; i < poly.Count; ++i)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count], ab = b - a;
                float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        // Whether segments ab and cd cross properly (each one's ends strictly on both sides of the other).
        private static bool Cross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float Side(Vector2 o, Vector2 p, Vector2 q) => (p.x - o.x) * (q.y - o.y) - (p.y - o.y) * (q.x - o.x);
            float d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            const float e = 1e-7f;
            return (d1 > e && d2 < -e || d1 < -e && d2 > e) && (d3 > e && d4 < -e || d3 < -e && d4 > e);
        }

        /// <summary>
        /// Whether <paramref name="inner"/> lies inside <paramref name="outer"/> up to <paramref name="tolerance"/>: every vertex inside
        /// or within the tolerance of the boundary, and no edge of one properly crossing an edge of the other farther in than that.
        /// </summary>
        public static bool Contained(IReadOnlyList<Vector2> inner, IReadOnlyList<Vector2> outer, float tolerance, out string why)
        {
            why = null;
            foreach (Vector2 v in inner)
                if (!Inside(v, outer) && BoundaryDistance(v, outer) > tolerance) { why = $"vertex {v} is {BoundaryDistance(v, outer):0.###} m outside"; return false; }
            // An outer vertex strictly inside the inner polygon (deeper than the tolerance) means the outer boundary cuts into it.
            foreach (Vector2 v in outer)
                if (Inside(v, inner) && BoundaryDistance(v, inner) > tolerance) { why = $"the boundary's corner {v} reaches {BoundaryDistance(v, inner):0.###} m into it"; return false; }
            return true;
        }

        /// <summary>
        /// The overlap of two polygons, sampled on a 1 cm grid over their common bounds, counting only points deeper than
        /// <paramref name="tolerance"/> inside both (so touching never counts). Square metres.
        /// </summary>
        public static float Overlap(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b, float tolerance)
        {
            Bounds(a, out Vector2 aMin, out Vector2 aMax);
            Bounds(b, out Vector2 bMin, out Vector2 bMax);
            Vector2 min = Vector2.Max(aMin, bMin), max = Vector2.Min(aMax, bMax);
            if (min.x >= max.x || min.y >= max.y) return 0f;
            const float step = 0.01f;
            int count = 0;
            for (float x = min.x + step * 0.5f; x < max.x; x += step)
            for (float y = min.y + step * 0.5f; y < max.y; y += step)
            {
                var p = new Vector2(x, y);
                if (Inside(p, a) && Inside(p, b) && BoundaryDistance(p, a) > tolerance && BoundaryDistance(p, b) > tolerance) ++count;
            }
            return count * step * step;
        }

        private static void Bounds(IReadOnlyList<Vector2> ring, out Vector2 min, out Vector2 max)
        {
            min = max = ring[0];
            foreach (Vector2 p in ring) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        }
    }
}
#endif
