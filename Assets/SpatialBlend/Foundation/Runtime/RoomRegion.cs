// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The room as the core sees it (the "simple room"). The room's Navigable polygon is the scanned room
    /// ENLARGED, never shrunk: the union of the floor outline grown by <see cref="DefaultMarginMeters"/>, every anchor's
    /// footprint grown by its stand-off (<see cref="StandOffMeters"/>, the reach of a scene anchor's region) and the walls
    /// (each wall's bottom edge grown by the margin). Every anchor region and every object in the room is inside it by
    /// construction. Where an avatar can stand is decided by OBSTACLES, not by the polygon: the furniture boxes (the MRUK
    /// scene-geometry adapter's Obstacle elements) and the walls (<see cref="Walls"/>, thin footprints behind each wall,
    /// deep enough to cover the margin band). The NavMesh is not part of it: it only serves the avatar's walking.
    /// Pure: every input and output is a world ground polygon (Unity x, z), so the app, the manager and the tests build the
    /// same room from the same scan.
    /// </summary>
    public sealed class RoomRegion
    {
        /// <summary>Metres the floor outline is grown by (default 0.3 m).</summary>
        public const float DefaultMarginMeters = 0.3f;

        /// <summary>
        /// Metres each anchor's footprint is grown by in the ring: a scene anchor's region reaches 0.5 m past its box
        /// (SceneAnchorSpatialAuthoring.StandOffMeters), plus a few centimetres so that full stand-off passes the anchor
        /// region fit's own clearance.
        /// </summary>
        public const float StandOffMeters = 0.5f + 0.05f;

        /// <summary>
        /// Metres a wall obstacle reaches behind its wall: the margin band, and the stand-off of furniture that stands
        /// against the wall, so no part of the ring outside the walls is free space. Shortened per wall when it would reach
        /// back into the floor (a thin partition, a narrow notch).
        /// </summary>
        public const float WallDepthMeters = DefaultMarginMeters + StandOffMeters + 0.2f;

        public RoomRegion(Vector2[] ring, Vector2[] floor, Vector2[][] walls, Vector2[][] furniture, float margin,
                          (Vector2 a, Vector2 b, Vector2 inward)[] wallEdges = null)
        {
            WallEdges = wallEdges ?? new (Vector2, Vector2, Vector2)[0];
            Ring = ring;
            Floor = floor;
            Walls = walls ?? new Vector2[0][];
            Furniture = furniture ?? new Vector2[0][];
            MarginMeters = margin;
        }

        /// <summary>The room's Navigable polygon: counter-clockwise, simple, no holes.</summary>
        public Vector2[] Ring { get; }

        /// <summary>The scanned floor outline, counter-clockwise.</summary>
        public Vector2[] Floor { get; }

        /// <summary>One thin Obstacle footprint per wall, counter-clockwise, convex. Walls may overlap each other at corners.</summary>
        public Vector2[][] Walls { get; }

        /// <summary>Every anchor's ground footprint (the furniture the adapter stages as Obstacle boxes).</summary>
        public Vector2[][] Furniture { get; }

        public float MarginMeters { get; }

        /// <summary>Each wall's bottom edge on the ground and its inward unit normal (into the room): where a panel snaps.</summary>
        public (Vector2 a, Vector2 b, Vector2 inward)[] WallEdges { get; }

        public bool IsValid => Ring != null && Ring.Length >= 3;

        /// <summary>The ring as a walkable region with no holes (the obstacles are separate elements).</summary>
        public WalkableRegion Walkable => new WalkableRegion(Ring, null);

        /// <summary>
        /// What a region must stay clear of, besides its sibling regions: the furniture (the core's siblings). The walls bound the
        /// object's footprint instead (<see cref="RegionPull"/>).
        /// </summary>
        public List<Vector2[]> RegionObstacles() => new List<Vector2[]>(Furniture);

        /// <summary>Everything the avatar's free space excludes: the walls and the furniture.</summary>
        public List<Vector2[]> Obstacles()
        {
            var all = new List<Vector2[]>(Walls.Length + Furniture.Length);
            all.AddRange(Walls);
            all.AddRange(Furniture);
            return all;
        }

        /// <summary>
        /// Builds the room. <paramref name="floor"/> is the floor outline (either winding); <paramref name="walls"/> are the
        /// walls' bottom edges (null or empty: the floor outline's edges are the walls); <paramref name="furniture"/> are the
        /// anchors' footprints. Null when there is no usable floor.
        /// </summary>
        public static RoomRegion Build(IReadOnlyList<Vector2> floor, IReadOnlyList<(Vector2 a, Vector2 b)> walls,
                                       IReadOnlyList<Vector2[]> furniture, float margin = DefaultMarginMeters)
        {
            Vector2[] outline = CleanRing(floor);
            if (outline == null) return null;
            if (margin < 0f || float.IsNaN(margin)) margin = DefaultMarginMeters;
            var edges = new List<(Vector2 a, Vector2 b)>();
            if (walls != null)
            {
                foreach ((Vector2 a, Vector2 b) w in walls)
                    if ((w.b - w.a).sqrMagnitude > 1e-6f) edges.Add(w);
            }
            if (edges.Count == 0)
            {
                for (int i = 0; i < outline.Length; ++i) edges.Add((outline[i], outline[(i + 1) % outline.Length]));
            }

            var parts = new List<Vector2[]> { outline };
            foreach ((Vector2 a, Vector2 b) e in edges)
            {
                Vector2[] band = SegmentBox(e.a, e.b, margin, margin, margin, margin);
                if (band != null) parts.Add(band);
            }
            var footprints = new List<Vector2[]>();
            if (furniture != null)
            {
                foreach (Vector2[] f in furniture)
                {
                    if (f == null || f.Length < 3) continue;
                    footprints.Add(f);
                    Vector2[] grown = PolygonBoolean.InflateConvex(f, StandOffMeters);
                    if (grown != null && grown.Length >= 3) parts.Add(grown);
                }
            }
            Vector2[] ring = PolygonBoolean.OuterRing(parts, null, null);
            ring = CleanRing(ring);
            if (ring == null) return null;

            var wallObstacles = new List<Vector2[]>(edges.Count);
            var wallEdges = new List<(Vector2, Vector2, Vector2)>(edges.Count);
            foreach ((Vector2 a, Vector2 b) e in edges)
            {
                Vector2[] wall = WallObstacle(e.a, e.b, outline);
                if (wall != null) wallObstacles.Add(wall);
                else
                    Debug.LogError($"[RoomRegion] The wall from {e.a} to {e.b} produced no obstacle (a degenerate edge): the room has a gap there " +
                                   "(an avatar's course may leave the room).");
                wallEdges.Add((e.a, e.b, -AwayFromFloor(e.a, e.b, outline)));
            }
            return new RoomRegion(ring, outline, wallObstacles.ToArray(), footprints.ToArray(), margin, wallEdges.ToArray());
        }

        /// <summary>
        /// The wall's obstacle: a rectangle behind its bottom edge (the side away from the floor, <see cref="AwayFromFloor"/>),
        /// <see cref="WallDepthMeters"/> deep, carried past each end that is a convex corner of the room (the corner square of
        /// the margin band would otherwise be free space outside the walls). Only what would cover the floor is cut back: the
        /// depth when the floor reaches behind the wall, and each corner extension on its own. Null when no rectangle behind
        /// the wall stays off the floor.
        /// </summary>
        /// <summary>Half the thickness of an inner wall (floor on both sides of it): its obstacle straddles its line.</summary>
        public const float InnerWallHalfThicknessMeters = 0.05f;

        internal static Vector2[] WallObstacle(Vector2 a, Vector2 b, Vector2[] floor)
        {
            Vector2 d = b - a;
            float length = d.magnitude;
            if (length < 1e-3f) return null;
            d /= length;
            Vector2 n = AwayFromFloor(a, b, floor);
            const float probe = 0.02f;
            float depth = WallDepthMeters;
            while (depth >= 0.02f && CoversFloor(a, b, 0f, 0f, depth, n, floor)) depth *= 0.5f;
            // The floor goes on behind it (an inner wall of a multi-room floor): a thin wall on its line, as thick as a wall.
            if (depth < 0.02f) return SegmentBox(a, b, 0f, 0f, InnerWallHalfThicknessMeters, InnerWallHalfThicknessMeters, n);
            // An end is convex when just past it, on the floor side, there is no floor; its extension is cut back by itself.
            float ExtensionAt(bool atA)
            {
                Vector2 end = atA ? a : b, along = atA ? -d : d;
                if (WalkablePlacementSnap.Contains(floor, end + along * probe - n * probe)) return 0f;   // the floor goes on past the end: not convex
                for (float e = depth; e >= 0.02f; e *= 0.5f)
                    if (!CoversFloor(a, b, atA ? e : 0f, atA ? 0f : e, depth, n, floor)) return e;
                return 0f;
            }
            float back = ExtensionAt(true), ahead = ExtensionAt(false);
            return SegmentBox(a, b, back, ahead, 0f, depth, n);
        }

        /// <summary>
        /// The unit normal of wall a->b that points away from the floor: the side whose band (half a metre deep) covers less floor.
        /// Robust to a wall drawn off the floor's edge (a probe at the edge would see no floor on either side).
        /// </summary>
        internal static Vector2 AwayFromFloor(Vector2 a, Vector2 b, Vector2[] floor)
        {
            Vector2 d = (b - a).normalized, n = new Vector2(d.y, -d.x);
            double right = SceneFootprintLayout.IntersectionArea(floor, SegmentBox(a, b, 0f, 0f, 0f, 0.5f, n));
            double left = SceneFootprintLayout.IntersectionArea(floor, SegmentBox(a, b, 0f, 0f, 0f, 0.5f, -n));
            return right > left ? -n : n;
        }

        // Whether the rectangle behind the wall (its strip from 1 cm off the wall line to `depth`, so touching the floor along the
        // wall does not count) covers any floor.
        private static bool CoversFloor(Vector2 a, Vector2 b, float back, float ahead, float depth, Vector2 n, Vector2[] floor)
        {
            Vector2[] inner = SegmentBox(a, b, back, ahead, -0.01f, depth, n);
            return inner != null && SceneFootprintLayout.IntersectionArea(floor, inner) > 1e-6;
        }

        // The rectangle around segment a->b: `back` metres before a, `ahead` past b, `left` and `right` metres to each
        // side of it (right is the side `n` points to; by default the clockwise normal). Counter-clockwise.
        private static Vector2[] SegmentBox(Vector2 a, Vector2 b, float back, float ahead, float left, float right,
                                            Vector2? normal = null)
        {
            Vector2 d = b - a;
            float length = d.magnitude;
            if (length < 1e-4f) return null;
            d /= length;
            Vector2 n = normal ?? new Vector2(d.y, -d.x);
            Vector2 p0 = a - d * back, p1 = b + d * ahead;
            var box = new[] { p0 - n * left, p1 - n * left, p1 + n * right, p0 + n * right };
            if (PolygonBoolean.SignedArea(box) < 0f) System.Array.Reverse(box);
            return PolygonBoolean.SignedArea(box) > 1e-8f ? box : null;
        }

        /// <summary>The ring counter-clockwise, without repeated or collinear vertices; null when fewer than three remain.</summary>
        public static Vector2[] CleanRing(IReadOnlyList<Vector2> ring)
        {
            if (ring == null || ring.Count < 3) return null;
            var points = new List<Vector2>(ring.Count);
            foreach (Vector2 p in ring)
            {
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return null;
                if (points.Count == 0 || (points[points.Count - 1] - p).sqrMagnitude > 1e-8f) points.Add(p);
            }
            if (points.Count > 1 && (points[0] - points[points.Count - 1]).sqrMagnitude <= 1e-8f) points.RemoveAt(points.Count - 1);
            for (bool changed = true; changed && points.Count >= 3;)
            {
                changed = false;
                for (int i = 0; i < points.Count; ++i)
                {
                    Vector2 prev = points[(i + points.Count - 1) % points.Count], next = points[(i + 1) % points.Count];
                    Vector2 e = next - prev;
                    float len = e.magnitude;
                    float off = len > 0f ? Mathf.Abs(e.x * (points[i].y - prev.y) - e.y * (points[i].x - prev.x)) / len : 0f;
                    if (len <= 1e-4f || off <= 1e-4f)
                    {
                        points.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            if (points.Count < 3) return null;
            Vector2[] result = points.ToArray();
            if (PolygonBoolean.SignedArea(result) < 0f) System.Array.Reverse(result);
            return result;
        }
    }
}
