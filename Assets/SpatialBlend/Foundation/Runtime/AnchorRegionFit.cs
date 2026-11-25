// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The Containment region of a scene anchor (a TABLE, a COUCH): a rectangle in the anchor's authoring frame, the box
    /// widened by a stand-off on each side (v1's SURROUND volume, 0.5 m). The furniture cannot move, so each side is set to
    /// the stand-off the core admits under its strict rule, a region inside its parent's ring. The room's registered ring
    /// already holds the furniture (merged in Unity before it is passed: SpatialBlendManager.MergeFurniture), so
    /// the box is inside it and only the strips matter:
    /// <list type="bullet">
    /// <item>the part of the region outside the box (the strip beyond each side that keeps a stand-off) must lie inside the
    /// room's ring; the holes are inside that ring. A side may go inside the box (a negative stand-off), which narrows the
    /// strips beside it to the part of the face that is on the floor;</item>
    /// <item>it is a sibling of everything else under the room and may only touch it: every sibling footprint is
    /// <c>margin</c> clear of it (a box that already overlaps a neighbour, whose staged pieces the layout clipped, makes that
    /// side go inside the box);</item>
    /// <item>a side without a stand-off lies at least <see cref="CoverInset"/> inside the box, so that edge is inside the ring
    /// whatever the rounding of the two polygons.</item>
    /// </list>
    /// Of the rectangles that pass, the one with the largest part outside the box (floor an avatar can stand on) wins. The
    /// search is a bounded grid of stand-offs per side. Pure: every input is in the frame's ground coordinates.
    /// </summary>
    public static class AnchorRegionFit
    {
        public const float CoverInset = 0.002f;
        /// <summary>The smallest half size of the region along an axis, so a side never goes past the box's middle.</summary>
        public const float MinHalfSize = 0.05f;
        private static readonly float[] Outward = { 1f, 0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 0.1f };
        private const float InwardStep = 0.1f;

        /// <param name="boxMin">The box footprint's lower corner in the frame.</param>
        /// <param name="boxMax">The box footprint's upper corner in the frame.</param>
        /// <param name="offsets">Per side, +x, -x, +y, -y: the stand-off kept (negative: inside the box).</param>
        /// <returns>False when no rectangle reaches past the box onto the floor and satisfies the rules.</returns>
        public static bool TryFit(Vector2 boxMin, Vector2 boxMax, float standOff, IReadOnlyList<Vector2> boundary,
                                  IReadOnlyList<Vector2[]> siblings, float margin, out Vector2[] region, out float[] offsets)
        {
            region = null;
            offsets = null;
            if (boundary == null || boundary.Count < 3 || standOff <= 0f || boxMax.x <= boxMin.x || boxMax.y <= boxMin.y)
                return false;
            Vector2 half = (boxMax - boxMin) * 0.5f;
            float[][] levels = { Levels(standOff, half.x), Levels(standOff, half.x), Levels(standOff, half.y), Levels(standOff, half.y) };
            var hulls = new List<List<Vector2>>();
            if (siblings != null)
            {
                foreach (Vector2[] s in siblings)
                {
                    if (s != null && s.Length >= 3) hulls.Add(WalkablePlacementSnap.ConvexHull(s));
                }
            }
            var walkable = new WalkableRegion(boundary, null);
            var strips = new Dictionary<(int, float, float, float), bool>();
            bool StripOk(float[] o, int side)
            {
                if (o[side] <= 0f) return true;
                int p = side < 2 ? 2 : 0;  // the perpendicular sides
                var key = (side, o[side], o[p], o[p + 1]);
                if (!strips.TryGetValue(key, out bool ok))
                {
                    ok = WalkablePlacementSnap.Satisfies(Strip(boxMin, boxMax, o, side), Vector2.zero, walkable, margin);
                    strips[key] = ok;
                }
                return ok;
            }

            // Largest apron first: the first rectangle that passes is the answer, so most of the grid is never checked.
            var candidates = new List<(float apron, float[] o)>();
            foreach (float px in levels[0])
            foreach (float nx in levels[1])
            foreach (float py in levels[2])
            foreach (float ny in levels[3])
            {
                var o = new[] { px, nx, py, ny };
                float apron = Area(boxMin, boxMax, o) - Overlap(boxMin, boxMax, o);
                if (apron > 1e-6f) candidates.Add((apron, o));
            }
            candidates.Sort((a, b) => b.apron.CompareTo(a.apron));
            foreach ((float _, float[] o) in candidates)
            {
                if (!StripOk(o, 0) || !StripOk(o, 1) || !StripOk(o, 2) || !StripOk(o, 3)) continue;
                Vector2[] rect = Rect(boxMin, boxMax, o);
                bool clear = true;
                foreach (List<Vector2> hull in hulls)
                {
                    if (Overlap(rect, hull, margin)) { clear = false; break; }
                }
                if (!clear) continue;
                offsets = o;
                region = rect;
                return true;
            }
            return false;
        }

        // Outward stand-offs down to a tenth of the stand-off, then the cover inset and steps into the box down to its middle.
        private static float[] Levels(float standOff, float half)
        {
            var levels = new List<float>();
            foreach (float f in Outward) levels.Add(standOff * f);
            for (float inward = -CoverInset; inward >= -(half - MinHalfSize); inward -= InwardStep) levels.Add(inward);
            return levels.ToArray();
        }

        private static float Area(Vector2 boxMin, Vector2 boxMax, float[] o) =>
            (boxMax.x - boxMin.x + o[0] + o[1]) * (boxMax.y - boxMin.y + o[2] + o[3]);

        // The rectangle's area inside the box.
        private static float Overlap(Vector2 boxMin, Vector2 boxMax, float[] o) =>
            (boxMax.x - boxMin.x + Mathf.Min(o[0], 0f) + Mathf.Min(o[1], 0f)) *
            (boxMax.y - boxMin.y + Mathf.Min(o[2], 0f) + Mathf.Min(o[3], 0f));

        // Counter-clockwise: (+x,+y) last.
        private static Vector2[] Rect(Vector2 boxMin, Vector2 boxMax, float[] o) => new[]
        {
            new Vector2(boxMin.x - o[1], boxMin.y - o[3]), new Vector2(boxMax.x + o[0], boxMin.y - o[3]),
            new Vector2(boxMax.x + o[0], boxMax.y + o[2]), new Vector2(boxMin.x - o[1], boxMax.y + o[2]),
        };

        // The strip of one side beyond the box face, across the whole rectangle (its corners included).
        private static Vector2[] Strip(Vector2 boxMin, Vector2 boxMax, float[] o, int side)
        {
            Vector2[] r = Rect(boxMin, boxMax, o);
            Vector2 min = r[0], max = r[2];
            switch (side)
            {
                case 0: return Box(new Vector2(boxMax.x, min.y), max);
                case 1: return Box(min, new Vector2(boxMin.x, max.y));
                case 2: return Box(new Vector2(min.x, boxMax.y), max);
                default: return Box(min, new Vector2(max.x, boxMin.y));
            }
        }

        private static Vector2[] Box(Vector2 min, Vector2 max) => new[]
            { min, new Vector2(max.x, min.y), max, new Vector2(min.x, max.y) };

        // Separating axes of two convex rings; overlapping unless some axis leaves a gap of at least `gap`.
        private static bool Overlap(Vector2[] rect, List<Vector2> hull, float gap)
        {
            var axes = new List<Vector2> { Vector2.right, Vector2.up };
            for (int i = 0; i < hull.Count; ++i)
            {
                Vector2 e = hull[(i + 1) % hull.Count] - hull[i];
                if (e.sqrMagnitude > 1e-12f) axes.Add(new Vector2(-e.y, e.x).normalized);
            }
            foreach (Vector2 axis in axes)
            {
                Project(rect, axis, out float aMin, out float aMax);
                Project(hull, axis, out float bMin, out float bMax);
                if (bMin - aMax >= gap || aMin - bMax >= gap) return false;
            }
            return true;
        }

        private static void Project(IReadOnlyList<Vector2> ring, Vector2 axis, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            foreach (Vector2 p in ring)
            {
                float d = Vector2.Dot(p, axis);
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }

    }
}
