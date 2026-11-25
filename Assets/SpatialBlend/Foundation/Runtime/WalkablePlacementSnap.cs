// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Pure placement snap against the walkable region the app registered with the core: one outer
    /// boundary ring and one ring per hole, in ground (x, z) coordinates. It returns the smallest
    /// translation that puts every vertex of an object's authored region inside the boundary and outside
    /// every hole, with <c>margin</c> metres of clearance from both, and keeps the boundary's and the holes'
    /// corners out of the region. It reads no Unity scene state, so the placement motif, the registration fit
    /// and the tests give it the same inputs.
    /// </summary>
    public static class WalkablePlacementSnap
    {
        public const int MaxIterations = 64;
        // Metres a relaxation may carry the region from its start before it is given up. It holds for every caller, the
        // interactive clamp (PlacementMotif.ClampPositionToWalkable) too: the starts already reach past every nearby hole and
        // corner, so a fit found only beyond this from all of them is not one a drag or a spawn should jump to.
        private const float MaxWander = 3f;
        private const float Epsilon = 1e-4f;

        /// <summary>
        /// The translation to add to the region. It is zero when the region already satisfies the
        /// walkable region, or when the region or the walkable boundary is unusable.
        /// </summary>
        public static Vector2 Snap(IReadOnlyList<Vector2> region, WalkableRegion walkable, float margin)
        {
            return Snap(region, walkable, margin, out _);
        }

        /// <summary>The most start translations one snap relaxes from (the direct one first), so a furnished room stays cheap.</summary>
        public const int MaxCandidates = 96;
        private const int PolarStartDirections = 8;
        private static readonly float[] PolarStartRadii = { 0.25f, 0.5f, 1f, 1.5f, 2f };

        /// <param name="satisfied">True when the translated region meets every constraint.</param>
        /// <remarks>
        /// Every candidate is relaxed (the direct start, then starts past each hole's bounding box, past each boundary corner
        /// inside the region, the minimum translation that separates the region from each hole it overlaps, and a coarse
        /// ring of starts around it; at most <see cref="MaxCandidates"/>, nearest first), and the
        /// shortest translation that passes <see cref="Satisfies"/> wins. The relaxation alone answers vertex by vertex; the
        /// verdict also refuses an edge that crosses the boundary or a hole (a thin wall or box through the region).
        /// </remarks>
        public static Vector2 Snap(IReadOnlyList<Vector2> region, WalkableRegion walkable, float margin,
                                   out bool satisfied)
        {
            satisfied = true;
            if (region == null || region.Count < 3 || walkable == null || !walkable.HasBoundary)
                return Vector2.zero;
            if (float.IsNaN(margin) || float.IsInfinity(margin) || margin < 0f)
                margin = 0f;
            if (Satisfies(region, Vector2.zero, walkable, margin))
                return Vector2.zero;

            var starts = new List<Vector2> { Vector2.zero };
            for (int h = 0; h < walkable.Holes.Length; ++h)
            {
                Vector2[] hole = walkable.Holes[h];
                if (hole == null || hole.Length < 3 || !BoundsOverlap(region, hole, margin))
                    continue;
                if (TrySeparation(region, hole, margin, out Vector2 separation))
                    starts.Add(separation);
                for (int side = 0; side < 4; ++side)
                    starts.Add(ClearHoleBounds(region, hole, margin, side));
            }
            // A reflex corner of the boundary inside the region fights the walls the same way (a region over the notch
            // the NavMesh cuts around furniture at a wall): start from past that corner.
            for (int i = 0; i < walkable.Boundary.Length; ++i)
            {
                Vector2 corner = walkable.Boundary[i];
                if (!Contains(region, corner))
                    continue;
                for (int side = 0; side < 4; ++side)
                    starts.Add(ClearHoleBounds(region, new[] { corner }, margin, side));
            }
            // Relaxing from one obstacle at a time can end in a far basin when several are close (a furnished room): a
            // coarse ring of starts around the region covers the others.
            foreach (float radius in PolarStartRadii)
            {
                for (int k = 0; k < PolarStartDirections; ++k)
                {
                    float angle = 2f * Mathf.PI * k / PolarStartDirections;
                    starts.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
                }
            }
            // The cheapest starts first, so the cap drops the far ones.
            starts.Sort((x, y) => x.sqrMagnitude.CompareTo(y.sqrMagnitude));

            bool found = false;
            Vector2 best = Vector2.zero;
            Vector2 fallback = Vector2.zero;
            for (int i = 0; i < starts.Count && i < MaxCandidates; ++i)
            {
                // Starts are nearest first; one well past the best answer rarely relaxes back below it.
                if (found && starts[i].sqrMagnitude > 2.25f * best.sqrMagnitude)
                    break;
                Vector2 candidate = Relax(region, walkable, margin, starts[i], out bool ok);
                if (i == 0) fallback = candidate;
                if (!ok || (found && candidate.sqrMagnitude >= best.sqrMagnitude) ||
                    !Satisfies(region, candidate, walkable, margin))
                    continue;
                best = candidate;
                found = true;
            }
            satisfied = found;
            return found ? best : fallback;
        }

        /// <summary>
        /// True when the region moved by <paramref name="translation"/> keeps <paramref name="margin"/> metres inside the
        /// boundary and outside every hole at each vertex, has no boundary or hole corner inside it, and no edge of it crosses
        /// the boundary or a hole.
        /// </summary>
        public static bool Satisfies(IReadOnlyList<Vector2> region, Vector2 translation, WalkableRegion walkable, float margin)
        {
            if (LargestCorrection(region, translation, walkable, margin).sqrMagnitude > Epsilon * Epsilon)
                return false;
            var moved = new Vector2[region.Count];
            for (int i = 0; i < region.Count; ++i)
                moved[i] = region[i] + translation;
            if (WalkableRegionFit.EdgesCross(moved, walkable.Boundary))
                return false;
            for (int h = 0; h < walkable.Holes.Length; ++h)
            {
                Vector2[] hole = walkable.Holes[h];
                if (hole != null && hole.Length >= 3 && WalkableRegionFit.EdgesCross(moved, hole))
                    return false;
            }
            return true;
        }

        private static bool BoundsOverlap(IReadOnlyList<Vector2> a, Vector2[] b, float margin)
        {
            Bounds2(a, out Vector2 aMin, out Vector2 aMax);
            Bounds2(b, out Vector2 bMin, out Vector2 bMax);
            return aMin.x < bMax.x + margin && bMin.x < aMax.x + margin && aMin.y < bMax.y + margin && bMin.y < aMax.y + margin;
        }

        private static void Bounds2(IReadOnlyList<Vector2> ring, out Vector2 min, out Vector2 max)
        {
            min = ring[0];
            max = ring[0];
            for (int i = 1; i < ring.Count; ++i)
            {
                min = Vector2.Min(min, ring[i]);
                max = Vector2.Max(max, ring[i]);
            }
        }

        // The minimum translation (separating axis of the two convex hulls) that takes the region margin metres off the hole.
        private static bool TrySeparation(IReadOnlyList<Vector2> region, Vector2[] hole, float margin, out Vector2 translation)
        {
            translation = Vector2.zero;
            List<Vector2> a = Hull(region), b = Hull(hole);
            if (a.Count < 3 || b.Count < 3)
                return false;
            float best = float.MaxValue;
            foreach (List<Vector2> ring in new[] { a, b })
            {
                for (int i = 0; i < ring.Count; ++i)
                {
                    Vector2 edge = ring[(i + 1) % ring.Count] - ring[i];
                    if (edge.sqrMagnitude <= Epsilon * Epsilon)
                        continue;
                    Vector2 axis = new Vector2(-edge.y, edge.x).normalized;
                    Project(a, axis, out float aMin, out float aMax);
                    Project(b, axis, out float bMin, out float bMax);
                    // Move the region along +axis past the hole, or along -axis.
                    float forward = bMax - aMin + margin, backward = aMax - bMin + margin;
                    if (forward <= 0f || backward <= 0f)
                        return false; // already separated on this axis
                    if (forward < best) { best = forward; translation = axis * forward; }
                    if (backward < best) { best = backward; translation = -axis * backward; }
                }
            }
            return best < float.MaxValue;
        }

        private static void Project(List<Vector2> ring, Vector2 axis, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            foreach (Vector2 p in ring)
            {
                float d = Vector2.Dot(p, axis);
                min = Mathf.Min(min, d);
                max = Mathf.Max(max, d);
            }
        }

        /// <summary>The convex hull of the points, counter-clockwise.</summary>
        public static List<Vector2> ConvexHull(IReadOnlyList<Vector2> points) => Hull(points);

        // Andrew's monotone chain, counter-clockwise.
        private static List<Vector2> Hull(IReadOnlyList<Vector2> points)
        {
            var sorted = new List<Vector2>(points);
            sorted.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
            var hull = new List<Vector2>(2 * sorted.Count);
            for (int pass = 0; pass < 2; ++pass)
            {
                int start = hull.Count;
                foreach (Vector2 p in sorted)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
                sorted.Reverse();
            }
            return hull;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        // Repeatedly applies the largest single correction, which is the shortest move that fixes the
        // worst vertex, until nothing is left to fix or the iteration cap is reached.
        private static Vector2 Relax(IReadOnlyList<Vector2> region, WalkableRegion walkable, float margin,
                                     Vector2 start, out bool satisfied)
        {
            Vector2 translation = start;
            Vector2 previous = Vector2.zero;
            // The check after the last correction counts too: a relaxation that settles on its last step is satisfied.
            for (int iteration = 0; ; ++iteration)
            {
                Vector2 correction = LargestCorrection(region, translation, walkable, margin);
                if (correction.sqrMagnitude <= Epsilon * Epsilon)
                {
                    satisfied = true;
                    return translation;
                }
                // Two constraints pushing back and forth (a region wedged between two obstacles) never settle, and a start
                // that has wandered this far from where it began is not the short answer: given up, another start decides.
                if (iteration == MaxIterations || (correction + previous).sqrMagnitude <= Epsilon * Epsilon ||
                    (translation - start).sqrMagnitude > MaxWander * MaxWander)
                    break;
                previous = correction;
                translation += correction;
            }
            satisfied = false;
            return translation;
        }

        // The translation that puts the region's bounding box margin metres past one side of the hole's
        // bounding box: 0 = -x, 1 = +x, 2 = -z, 3 = +z.
        private static Vector2 ClearHoleBounds(IReadOnlyList<Vector2> region, Vector2[] hole, float margin, int side)
        {
            Vector2 rMin = region[0], rMax = region[0], hMin = hole[0], hMax = hole[0];
            for (int i = 1; i < region.Count; ++i)
            {
                rMin = Vector2.Min(rMin, region[i]);
                rMax = Vector2.Max(rMax, region[i]);
            }
            for (int i = 1; i < hole.Length; ++i)
            {
                hMin = Vector2.Min(hMin, hole[i]);
                hMax = Vector2.Max(hMax, hole[i]);
            }
            switch (side)
            {
                case 0: return new Vector2(hMin.x - margin - rMax.x, 0f);
                case 1: return new Vector2(hMax.x + margin - rMin.x, 0f);
                case 2: return new Vector2(0f, hMin.y - margin - rMax.y);
                default: return new Vector2(0f, hMax.y + margin - rMin.y);
            }
        }

        [System.ThreadStatic] private static Vector2[] _moved;

        private static Vector2 LargestCorrection(IReadOnlyList<Vector2> region, Vector2 translation,
                                                 WalkableRegion walkable, float margin)
        {
            Vector2 best = Vector2.zero;
            float bestSqr = 0f;

            if (_moved == null || _moved.Length != region.Count) _moved = new Vector2[region.Count];
            Vector2[] moved = _moved;
            int count = region.Count;
            for (int i = 0; i < count; ++i)
                moved[i] = region[i] + translation;

            for (int i = 0; i < count; ++i)
            {
                Consider(KeepInside(moved[i], walkable.Boundary, margin), ref best, ref bestSqr);
                for (int h = 0; h < walkable.Holes.Length; ++h)
                {
                    Vector2[] hole = walkable.Holes[h];
                    if (hole != null && hole.Length >= 3)
                        Consider(KeepOutside(moved[i], hole, margin), ref best, ref bestSqr);
                }
            }

            // A reflex corner of the boundary (the notch the NavMesh cuts around furniture at a wall) can reach into the
            // region with every region vertex still inside the boundary.
            for (int i = 0; i < walkable.Boundary.Length; ++i)
                Consider(PushRegionOffPoint(walkable.Boundary[i], moved, margin), ref best, ref bestSqr);

            // A hole can sit entirely inside the region with no region vertex in it.
            for (int h = 0; h < walkable.Holes.Length; ++h)
            {
                Vector2[] hole = walkable.Holes[h];
                if (hole == null || hole.Length < 3)
                    continue;
                for (int i = 0; i < hole.Length; ++i)
                    Consider(PushRegionOffPoint(hole[i], moved, margin), ref best, ref bestSqr);
            }
            return best;
        }

        private static void Consider(Vector2 candidate, ref Vector2 best, ref float bestSqr)
        {
            float sqr = candidate.sqrMagnitude;
            if (sqr > bestSqr)
            {
                best = candidate;
                bestSqr = sqr;
            }
        }

        // Moves a vertex to at least margin metres inside the ring.
        private static Vector2 KeepInside(Vector2 point, Vector2[] ring, float margin)
        {
            Vector2 nearest = NearestOnRing(point, ring, out float distance);
            if (!Contains(ring, point))
                return Toward(point, nearest, distance, ring) * (distance + margin);
            return distance < margin - Epsilon ? Away(point, nearest, distance, ring) * (margin - distance) : Vector2.zero;
        }

        // Moves a vertex to at least margin metres outside the ring.
        private static Vector2 KeepOutside(Vector2 point, Vector2[] ring, float margin)
        {
            Vector2 nearest = NearestOnRing(point, ring, out float distance);
            if (Contains(ring, point))
                return Toward(point, nearest, distance, ring) * (distance + margin);
            return distance < margin - Epsilon ? Away(point, nearest, distance, ring) * (margin - distance) : Vector2.zero;
        }

        // A fixed point inside the region is pushed out by moving the region, not the point: the shift
        // that takes the point to the region's nearest edge, plus the margin.
        private static Vector2 PushRegionOffPoint(Vector2 point, Vector2[] region, float margin)
        {
            if (!Contains(region, point))
                return Vector2.zero;
            Vector2 nearest = NearestOnRing(point, region, out float distance);
            Vector2 direction = Away(point, nearest, distance, region);
            return direction * (distance + margin);
        }

        private static Vector2 Toward(Vector2 point, Vector2 nearest, float distance, Vector2[] ring)
        {
            if (distance > Epsilon)
                return (nearest - point) / distance;
            return Direction(point, ring);
        }

        private static Vector2 Away(Vector2 point, Vector2 nearest, float distance, Vector2[] ring)
        {
            if (distance > Epsilon)
                return (point - nearest) / distance;
            return -Direction(point, ring);
        }

        // Unit vector from the point toward the ring's centroid, for a point that lies on the ring.
        private static Vector2 Direction(Vector2 point, Vector2[] ring)
        {
            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < ring.Length; ++i)
                centroid += ring[i];
            centroid /= ring.Length;
            Vector2 toward = centroid - point;
            return toward.sqrMagnitude > Epsilon * Epsilon ? toward.normalized : Vector2.right;
        }

        private static Vector2 NearestOnRing(Vector2 point, Vector2[] ring, out float distance)
        {
            Vector2 best = ring[0];
            float bestSqr = float.MaxValue;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % ring.Length];
                Vector2 edge = b - a;
                float length = edge.sqrMagnitude;
                float t = length > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / length) : 0f;
                Vector2 candidate = a + edge * t;
                float sqr = (point - candidate).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = candidate;
                }
            }
            distance = Mathf.Sqrt(bestSqr);
            return best;
        }

        public static bool Contains(IReadOnlyList<Vector2> ring, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[j];
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }
    }
}
