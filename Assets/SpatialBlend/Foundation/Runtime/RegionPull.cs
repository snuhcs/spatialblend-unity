// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The one validity rule of a virtual object's placement. Computed once, on a drop and at a spawn; nothing
    /// is ever refused: a placement that is not valid is pulled to the NEAREST valid spot, however far.
    /// Valid means:
    /// <list type="bullet">
    /// <item>the object's REGION lies inside the room's ring and clear of every region obstacle: the furniture boxes and the
    /// sibling regions, which the core refuses to overlap;</item>
    /// <item>the object's FOOTPRINT (the object itself) lies clear of the walls, so the object stands in the room. Its region may
    /// reach over the margin band behind a wall: a wall is not the region's sibling, only the avatar's obstacle.</item>
    /// </list>
    /// Each test is <see cref="WalkablePlacementSnap.Satisfies"/> with the obstacles as holes: every vertex <c>margin</c> inside
    /// the ring and outside each obstacle, no ring or obstacle corner inside the polygon, no edge crossing. Pure.
    /// </summary>
    public static class RegionPull
    {
        /// <summary>Metres between the candidate translations of the global search.</summary>
        public const float CoarseStep = 0.1f;
        /// <summary>Metres between the candidates of the refinement around the nearest coarse spot.</summary>
        public const float FineStep = 0.01f;
        /// <summary>Metres a pulled placement keeps beyond the margin (see <see cref="TryPull(Shape, IReadOnlyList{Vector2}, float, out Vector2)"/>).</summary>
        public const float PullSlack = 0.01f;

        /// <summary>The placement to test: the region and its obstacles, and optionally the footprint and its (the walls).</summary>
        public sealed class Shape
        {
            public IReadOnlyList<Vector2> Region;
            public IReadOnlyList<Vector2[]> RegionObstacles;
            public IReadOnlyList<Vector2> Footprint;
            public IReadOnlyList<Vector2[]> FootprintObstacles;
        }

        /// <summary>True when the placement moved by <paramref name="shift"/> is valid in <paramref name="ring"/>.</summary>
        public static bool IsValid(Shape shape, Vector2 shift, IReadOnlyList<Vector2> ring, float margin)
        {
            if (shape?.Region == null || shape.Region.Count < 3 || ring == null || ring.Count < 3) return false;
            Space(shape, ring, out WalkableRegion regionSpace, out WalkableRegion footprintSpace);
            return Valid(shape, shift, regionSpace, footprintSpace, margin);
        }

        /// <summary>The region-only form of <see cref="IsValid(Shape, Vector2, IReadOnlyList{Vector2}, float)"/>.</summary>
        public static bool IsValid(IReadOnlyList<Vector2> region, Vector2 shift, IReadOnlyList<Vector2> ring,
                                   IReadOnlyList<Vector2[]> obstacles, float margin) =>
            IsValid(new Shape { Region = region, RegionObstacles = obstacles }, shift, ring, margin);

        /// <summary>
        /// The shortest translation (within <see cref="FineStep"/>) that makes the placement valid. False when no translation does
        /// anywhere in the ring (the room is too small or too cluttered for it): <paramref name="shift"/> is then zero.
        /// </summary>
        public static bool TryPull(Shape shape, IReadOnlyList<Vector2> ring, float margin, out Vector2 shift) =>
            TryPull(shape, ring, margin, out shift, null);

        /// <summary>
        /// As <see cref="TryPull(Shape, IReadOnlyList{Vector2}, float, out Vector2)"/>, with <paramref name="knownValid"/>: a translation
        /// that may be valid (a drop's way back to where the object was valid): it bounds the search, so a pocket the coarse grid
        /// misses (a snug spot) is never lost. When the coarse grid finds nothing, a 5 cm grid is searched.
        /// </summary>
        public static bool TryPull(Shape shape, IReadOnlyList<Vector2> ring, float margin, out Vector2 shift, Vector2? knownValid,
                                   bool fineFallback = true)
        {
            shift = Vector2.zero;
            if (shape?.Region == null || shape.Region.Count < 3 || ring == null || ring.Count < 3) return false;
            Space(shape, ring, out WalkableRegion regionSpace, out WalkableRegion footprintSpace);
            if (Valid(shape, Vector2.zero, regionSpace, footprintSpace, margin)) return true;
            // The spot searched for keeps PullSlack more than the margin: a pull ends where the rule holds with room to spare, so
            // the same rule checked again later (at share time, from a re-composed frame) never finds it a hair inside the margin.
            margin += PullSlack;

            // (No local snap first: WalkablePlacementSnap.Snap cost tens of milliseconds a drop in a large room, and the ring search
            // below finds the nearest coarse spot exactly, which the refinement then brings to the centimetre.)
            bool found = Search(shape, ring, regionSpace, footprintSpace, margin, knownValid, fineFallback, float.PositiveInfinity, out Vector2 best);
            if (!found) return false;

            // Refine: the nearest valid translation within one coarse step of the one found, coarse to fine (a 2 cm pass over the
            // coarse cell, then a 1 cm pass around its best), each candidate tested only when it would be nearer.
            Vector2 refined = best;
            foreach ((float reachM, float stepM) pass in new[] { (CoarseStep, 2f * FineStep), (2f * FineStep, FineStep) })
            {
                Vector2 centreT = refined;
                for (float x = -pass.reachM; x <= pass.reachM + 1e-5f; x += pass.stepM)
                for (float y = -pass.reachM; y <= pass.reachM + 1e-5f; y += pass.stepM)
                {
                    Vector2 t = centreT + new Vector2(x, y);
                    if (t.sqrMagnitude >= refined.sqrMagnitude) continue;
                    if (Valid(shape, t, regionSpace, footprintSpace, margin)) refined = t;
                }
            }
            shift = refined;
            return true;
        }

        /// <summary>The spaces a shape is checked in (the ring less its obstacles), built once for a set of obstacles: a drop preview reuses them.</summary>
        public sealed class Spaces
        {
            internal IReadOnlyList<Vector2> Ring;
            internal WalkableRegion Region;
            internal WalkableRegion Footprint;
        }

        /// <summary>The spaces of <paramref name="shape"/>'s obstacles in <paramref name="ring"/> (its region and footprint are not read).</summary>
        public static Spaces BuildSpaces(Shape shape, IReadOnlyList<Vector2> ring) => new Spaces
        {
            Ring = ring,
            Region = new WalkableRegion(ring, Usable(shape.RegionObstacles)),
            Footprint = new WalkableRegion(ring, Usable(shape.FootprintObstacles)),
        };

        /// <summary>As <see cref="IsValid(Shape, Vector2, IReadOnlyList{Vector2}, float)"/>, in spaces built once.</summary>
        public static bool IsValid(Shape shape, Vector2 shift, Spaces spaces, float margin) =>
            shape?.Region != null && shape.Region.Count >= 3 && spaces != null &&
            Valid(shape, shift, spaces.Region, shape.Footprint != null && shape.Footprint.Count >= 3 ? spaces.Footprint : null, margin);

        /// <summary>
        /// A bounded <see cref="TryPull(Shape, IReadOnlyList{Vector2}, float, out Vector2, Vector2?, bool)"/> (a drop preview): the coarse grid only,
        /// no farther than <paramref name="maxReach"/>, and no refinement, so <paramref name="shift"/> is the coarse spot (within
        /// <see cref="CoarseStep"/> of the full search's). False when nothing within the reach is valid.
        /// </summary>
        public static bool TryPullCoarse(Shape shape, Spaces spaces, float margin, Vector2? knownValid, float maxReach, out Vector2 shift)
        {
            shift = Vector2.zero;
            if (shape?.Region == null || shape.Region.Count < 3 || spaces == null) return false;
            WalkableRegion foot = shape.Footprint != null && shape.Footprint.Count >= 3 ? spaces.Footprint : null;
            if (Valid(shape, Vector2.zero, spaces.Region, foot, margin)) return true;
            if (!Search(shape, spaces.Ring, spaces.Region, foot, margin + PullSlack, knownValid, false, maxReach, out Vector2 best)) return false;
            shift = best;
            return true;
        }

        /// <summary>The region-only form of <see cref="TryPull(Shape, IReadOnlyList{Vector2}, float, out Vector2)"/>.</summary>
        public static bool TryPull(IReadOnlyList<Vector2> region, IReadOnlyList<Vector2> ring, IReadOnlyList<Vector2[]> obstacles,
                                   float margin, out Vector2 shift) =>
            TryPull(new Shape { Region = region, RegionObstacles = obstacles }, ring, margin, out shift);

        // The coarse search (then, when it finds nothing and fineFallback, the 5 cm grid), nearest first, never beyond maxReach: every
        // spot nearer than the one found has been tried, so within the reach the answer is the full search's.
        private static bool Search(Shape shape, IReadOnlyList<Vector2> ring, WalkableRegion regionSpace, WalkableRegion footprintSpace, float margin,
                                   Vector2? knownValid, bool fineFallback, float maxReach, out Vector2 best)
        {
            bool found = false;
            best = Vector2.zero;
            float bound = float.MaxValue;
            if (knownValid.HasValue && Valid(shape, knownValid.Value, regionSpace, footprintSpace, margin))
            {
                best = knownValid.Value;
                bound = best.magnitude;
                found = true;
            }

            // Every translation that keeps the region's centre inside the ring's bounding box, searched outward in square rings
            // of the coarse grid (ring k holds the points k steps out), nearest first within a ring, until no ring can hold a
            // nearer valid spot than the one found: ring k's nearest point is k steps away.
            Bounds(ring, out Vector2 rMin, out Vector2 rMax);
            Bounds(shape.Region, out Vector2 gMin, out Vector2 gMax);
            Vector2 centre = (gMin + gMax) * 0.5f;
            Vector2 lo = rMin - centre, hi = rMax - centre;
            var ringPoints = new List<Vector2>();
            foreach (float step in new[] { CoarseStep, CoarseStep * 0.5f })
            {
                if (step < CoarseStep && (found || !fineFallback)) break;
                int reach = Mathf.CeilToInt(Mathf.Max(Mathf.Max(-lo.x, hi.x), Mathf.Max(-lo.y, hi.y)) / step) + 1;
                for (int k = 1; k <= reach && k * step < bound && k * step <= maxReach + 1e-5f; ++k)
                {
                    ringPoints.Clear();
                    for (int i = -k; i <= k; ++i)
                    for (int j = -k; j <= k; ++j)
                    {
                        if (Mathf.Max(Mathf.Abs(i), Mathf.Abs(j)) != k) continue;
                        var t = new Vector2(i * step, j * step);
                        if (t.x < lo.x - step || t.x > hi.x + step || t.y < lo.y - step || t.y > hi.y + step) continue;
                        if (t.magnitude < bound) ringPoints.Add(t);
                    }
                    ringPoints.Sort((p, q) => p.sqrMagnitude.CompareTo(q.sqrMagnitude));
                    foreach (Vector2 t in ringPoints)
                    {
                        if (t.magnitude >= bound) break;
                        if (!Valid(shape, t, regionSpace, footprintSpace, margin)) continue;
                        best = t;
                        bound = t.magnitude;
                        found = true;
                        break;
                    }
                }
            }
            return found;
        }

        private static void Space(Shape shape, IReadOnlyList<Vector2> ring, out WalkableRegion regionSpace,
                                  out WalkableRegion footprintSpace)
        {
            regionSpace = new WalkableRegion(ring, Usable(shape.RegionObstacles));
            footprintSpace = shape.Footprint != null && shape.Footprint.Count >= 3
                ? new WalkableRegion(ring, Usable(shape.FootprintObstacles)) : null;
        }

        private static bool Valid(Shape shape, Vector2 t, WalkableRegion regionSpace, WalkableRegion footprintSpace, float margin)
        {
            if (QuickReject(shape.Region, t, regionSpace) || footprintSpace != null && QuickReject(shape.Footprint, t, footprintSpace)) return false;
            WalkableRegion nearRegion = Near(regionSpace, shape.Region, t, margin);
            if (!WalkablePlacementSnap.Satisfies(shape.Region, t, nearRegion, margin) || !Symmetric(shape.Region, t, nearRegion, margin)) return false;
            if (footprintSpace == null) return true;
            WalkableRegion nearFoot = Near(footprintSpace, shape.Footprint, t, margin);
            return WalkablePlacementSnap.Satisfies(shape.Footprint, t, nearFoot, margin) && Symmetric(shape.Footprint, t, nearFoot, margin);
        }

        // The same margin the other way round: no obstacle corner inside the moved polygon or within the margin of its edges. Without
        // it two regions could be valid one way and not the other (a sliver where a corner of one grazes the other's edge), and an
        // object placed validly would be moved when it is checked again against the first (at share time).
        private static bool Symmetric(IReadOnlyList<Vector2> polygon, Vector2 t, WalkableRegion space, float margin)
        {
            int n = polygon.Count;
            for (int h = 0; h < space.Holes.Length; ++h)
            {
                Vector2[] hole = space.Holes[h];
                if (hole == null || hole.Length < 3) continue;
                foreach (Vector2 p in hole)
                {
                    Vector2 q = p - t;
                    bool inside = false;
                    float best = float.MaxValue;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        Vector2 a = polygon[i], b = polygon[j];
                        if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x - a.x) * (q.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                        Vector2 ab = b - a;
                        float u = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                        best = Mathf.Min(best, Vector2.Distance(q, a + ab * u));
                    }
                    if (inside || best < margin - 1e-5f) return false;
                }
            }
            return true;
        }

        // A necessary condition, cheap: every moved vertex inside the ring and outside every obstacle (Satisfies asks that and
        // more, a margin in). Rejects most of a global search's candidates before the full test.
        private static bool QuickReject(IReadOnlyList<Vector2> polygon, Vector2 t, WalkableRegion space)
        {
            for (int i = 0; i < polygon.Count; ++i)
            {
                Vector2 p = polygon[i] + t;
                if (!PointIn(p, space.Boundary)) return true;
                for (int h = 0; h < space.Holes.Length; ++h)
                    if (space.Holes[h] != null && space.Holes[h].Length >= 3 && PointIn(p, space.Holes[h])) return true;
            }
            return false;
        }

        private static bool PointIn(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        // The space with only the obstacles whose bounds come within the margin of the moved shape's (KeepNear): an obstacle
        // farther away can neither hold a vertex, cross an edge nor lie inside the shape, so the test is exact.
        private static WalkableRegion Near(WalkableRegion space, IReadOnlyList<Vector2> polygon, Vector2 t, float margin)
        {
            if (space.Holes.Length == 0) return space;
            Bounds(polygon, out Vector2 min, out Vector2 max);
            min += t - new Vector2(margin, margin) * 1.01f;
            max += t + new Vector2(margin, margin) * 1.01f;
            List<Vector2[]> near = null;
            for (int h = 0; h < space.Holes.Length; ++h)
            {
                Vector2[] hole = space.Holes[h];
                Bounds(hole, out Vector2 hMin, out Vector2 hMax);
                bool apart = hMax.x < min.x || hMin.x > max.x || hMax.y < min.y || hMin.y > max.y;
                if (apart && near == null)
                {
                    near = new List<Vector2[]>(space.Holes.Length);
                    for (int k = 0; k < h; ++k) near.Add(space.Holes[k]);
                }
                else if (!apart && near != null) near.Add(hole);
            }
            return near == null ? space : new WalkableRegion(space.Boundary, near);
        }

        private static List<Vector2[]> Usable(IReadOnlyList<Vector2[]> obstacles)
        {
            var list = new List<Vector2[]>();
            if (obstacles == null) return list;
            foreach (Vector2[] o in obstacles)
                if (o != null && o.Length >= 3) list.Add(o);
            return list;
        }

        private static void Bounds(IReadOnlyList<Vector2> ring, out Vector2 min, out Vector2 max)
        {
            min = ring[0];
            max = ring[0];
            for (int i = 1; i < ring.Count; ++i)
            {
                min = Vector2.Min(min, ring[i]);
                max = Vector2.Max(max, ring[i]);
            }
        }
    }
}
