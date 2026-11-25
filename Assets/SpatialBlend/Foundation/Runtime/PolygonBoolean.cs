// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The outer ring of a union of simple rings, part of them clipped by one more, for the room's registered walkable ring
    /// (the NavMesh's ring merged with the furniture it carved, the furniture kept inside the floor). No general clipping
    /// library: every edge is cut where another one meets it, and a piece is kept when the region lies on exactly one of its
    /// sides (probed <see cref="Probe"/> metres to each side), so collinear, touching and repeated edges need no case of their
    /// own. The kept pieces are chained into loops and the largest counter-clockwise one is the answer; pieces of the region
    /// that touch it only at a point, or that are separate from it, are not part of it.
    /// </summary>
    public static class PolygonBoolean
    {
        /// <summary>Metres to each side of an edge at which the region is sampled.</summary>
        public const float Probe = 1e-4f;
        /// <summary>
        /// Metres within which two points are one vertex. It is the shortest piece kept (ten probes), so a piece shorter than
        /// that collapses into its vertex instead of being dropped and leaving the chain open (two edges crossing a third a
        /// fraction of a millimetre apart). The ring is exact to it, and everything is fitted to the ring as registered.
        /// </summary>
        public const float Weld = 10f * Probe;

        /// <summary>
        /// The outer ring (counter-clockwise, ground x/z as x/y) of the points in some ring of <paramref name="kept"/>, or in
        /// some ring of <paramref name="clipped"/> and inside <paramref name="clip"/> (when one is given). Null when empty.
        /// </summary>
        public static Vector2[] OuterRing(IReadOnlyList<Vector2[]> kept, IReadOnlyList<Vector2[]> clipped,
                                          IReadOnlyList<Vector2> clip)
        {
            List<Vector2[]> keep = Usable(kept), grown = Usable(clipped);
            if (keep.Count == 0 && grown.Count == 0) return null;
            Vector2[] clipRing = clip != null && clip.Count >= 3 ? ToArray(clip) : null;

            bool Inside(Vector2 p)
            {
                foreach (Vector2[] r in keep) if (WalkablePlacementSnap.Contains(r, p)) return true;
                if (clipRing != null && !WalkablePlacementSnap.Contains(clipRing, p)) return false;
                foreach (Vector2[] r in grown) if (WalkablePlacementSnap.Contains(r, p)) return true;
                return false;
            }

            var edges = new List<(Vector2 a, Vector2 b)>();
            foreach (Vector2[] r in keep) AddEdges(r, edges);
            foreach (Vector2[] r in grown) AddEdges(r, edges);
            if (clipRing != null) AddEdges(clipRing, edges);

            // Each edge, cut at every point where another edge meets it. The point two edges meet at is computed once and is one
            // vertex of both (two separate float computations of it differ at shallow angles, and the chain would not close);
            // points within Weld of each other are one vertex as well (coincident corners, a crossing next to a corner).
            var vertices = new List<Vector2>();
            int Vertex(Vector2 p)
            {
                for (int v = 0; v < vertices.Count; ++v) if ((vertices[v] - p).sqrMagnitude <= Weld * Weld) return v;
                vertices.Add(p);
                return vertices.Count - 1;
            }
            var cuts = new List<(float t, int vertex)>[edges.Count];
            for (int i = 0; i < edges.Count; ++i)
                cuts[i] = new List<(float, int)> { (0f, Vertex(edges[i].a)), (1f, Vertex(edges[i].b)) };
            for (int i = 0; i < edges.Count; ++i)
            {
                for (int j = i + 1; j < edges.Count; ++j) Meet(i, j, edges, cuts, Vertex);
            }
            var pieces = new HashSet<(int from, int to)>();
            for (int i = 0; i < edges.Count; ++i)
            {
                List<(float t, int vertex)> along = cuts[i];
                along.Sort((x, y) => x.t.CompareTo(y.t));
                for (int k = 0; k + 1 < along.Count; ++k)
                {
                    int from = along[k].vertex, to = along[k + 1].vertex;
                    if (from == to) continue;
                    // Sampled where the piece really is, on its own edge: a welded vertex may sit up to Weld off it.
                    Vector2 p = Vector2.LerpUnclamped(edges[i].a, edges[i].b, along[k].t);
                    Vector2 q = Vector2.LerpUnclamped(edges[i].a, edges[i].b, along[k + 1].t);
                    Vector2 d = q - p;
                    float length = d.magnitude;
                    if (length <= 0f) continue;
                    Vector2 mid = (p + q) * 0.5f, left = new Vector2(-d.y, d.x) / length * Probe;
                    bool inLeft = Inside(mid + left), inRight = Inside(mid - left);
                    if (inLeft == inRight) continue;
                    // The region on the left: counter-clockwise loops.
                    pieces.Add(inLeft ? (from, to) : (to, from));
                }
            }

            // A piece no loop can pass through (nothing leaves its end, or nothing arrives at its start) is a dangling sliver: a
            // region thinner than the probe, between two edges a fraction of a millimetre apart. Dropped until none is left.
            for (bool pruned = true; pruned;)
            {
                pruned = false;
                var starts = new HashSet<int>();
                var ends = new HashSet<int>();
                foreach ((int from, int to) piece in pieces) { starts.Add(piece.from); ends.Add(piece.to); }
                if (pieces.RemoveWhere(piece => !starts.Contains(piece.to) || !ends.Contains(piece.from)) > 0) pruned = true;
            }

            // Chain the pieces: where several leave a vertex, take the rightmost turn (a loop keeps to its own boundary).
            var outgoing = new Dictionary<int, List<int>>();
            foreach ((int from, int to) piece in pieces)
            {
                if (!outgoing.TryGetValue(piece.from, out var list)) outgoing[piece.from] = list = new List<int>();
                list.Add(piece.to);
            }
            var used = new HashSet<(int, int)>();
            Vector2[] best = null;
            float bestArea = 0f;
            foreach ((int from, int to) start in pieces)
            {
                if (used.Contains(start)) continue;
                var loop = new List<Vector2>();
                (int from, int to) current = start;
                bool closed = false;
                for (int guard = 0; guard <= pieces.Count; ++guard)
                {
                    used.Add(current);
                    loop.Add(vertices[current.from]);
                    if (current.to == start.from) { closed = true; break; }
                    if (!outgoing.TryGetValue(current.to, out var next)) break;
                    Vector2 incoming = vertices[current.to] - vertices[current.from];
                    int chosen = -1;
                    float chosenTurn = float.MaxValue;
                    foreach (int to in next)
                    {
                        if (used.Contains((current.to, to))) continue;
                        float turn = Vector2.SignedAngle(incoming, vertices[to] - vertices[current.to]);  // + turns left
                        if (turn < chosenTurn) { chosenTurn = turn; chosen = to; }
                    }
                    if (chosen < 0) break;
                    current = (current.to, chosen);
                }
                if (!closed || loop.Count < 3) continue;
                Vector2[] ring = Simplify(loop);
                float area = ring == null ? 0f : SignedArea(ring);
                if (area > bestArea) { bestArea = area; best = ring; }
            }
            return best;
        }

        /// <summary>The shoelace area: positive for a counter-clockwise ring.</summary>
        public static float SignedArea(IReadOnlyList<Vector2> ring)
        {
            double twice = 0.0;
            for (int i = 0; i < ring.Count; ++i)
            {
                Vector2 u = ring[i], v = ring[(i + 1) % ring.Count];
                twice += (double)u.x * v.y - (double)v.x * u.y;
            }
            return (float)(twice * 0.5);
        }

        /// <summary>
        /// A convex ring grown by <paramref name="distance"/> metres: every edge moved outward and the corners mitred, so the
        /// result contains every point within that distance of the ring (the NavMesh's carve around a box, which rounds its
        /// corners, lies inside it). Null when the ring is not usable.
        /// </summary>
        public static Vector2[] InflateConvex(IReadOnlyList<Vector2> ring, float distance)
        {
            if (ring == null || ring.Count < 3) return null;
            // Short and collinear edges have no reliable direction to grow along: the convex hull's corners only.
            List<Vector2> points = WalkablePlacementSnap.ConvexHull(ring);
            Vector2[] corners = Simplify(points);
            if (corners == null) return null;
            points = new List<Vector2>(corners);
            int n = points.Count;
            if (distance <= 0f) return points.ToArray();
            var grown = new Vector2[n];
            for (int i = 0; i < n; ++i)
            {
                Vector2 a0 = points[(i + n - 1) % n], a1 = points[i], b1 = points[(i + 1) % n];
                Vector2 da = (a1 - a0).normalized, db = (b1 - a1).normalized;
                Vector2 na = new Vector2(da.y, -da.x), nb = new Vector2(db.y, -db.x);  // outward for counter-clockwise
                Vector2 pa = a1 + na * distance, pb = a1 + nb * distance;
                float denominator = Cross(da, db);
                grown[i] = Mathf.Abs(denominator) <= 1e-9f ? pa : pa + da * (Cross(pb - pa, db) / denominator);
            }
            return grown;
        }

        private static List<Vector2[]> Usable(IReadOnlyList<Vector2[]> rings)
        {
            var usable = new List<Vector2[]>();
            if (rings != null)
            {
                foreach (Vector2[] r in rings) if (r != null && r.Length >= 3) usable.Add(r);
            }
            return usable;
        }

        private static void AddEdges(Vector2[] ring, List<(Vector2, Vector2)> edges)
        {
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
                if ((b - a).sqrMagnitude > 0f) edges.Add((a, b));
            }
        }

        // Where edges i and j meet: one point, cut into both (a crossing, a touch, or, collinear, each one's ends on the other).
        // A meeting within reach of an end is that end itself.
        private static void Meet(int i, int j, List<(Vector2 a, Vector2 b)> edges, List<(float t, int vertex)>[] cuts,
                                 Func<Vector2, int> vertex)
        {
            (Vector2 a, Vector2 b) = edges[i];
            (Vector2 c, Vector2 d) = edges[j];
            Vector2 r = b - a, s = d - c;
            float rr = r.sqrMagnitude, ss = s.sqrMagnitude;
            float denominator = Cross(r, s);
            if (Mathf.Abs(denominator) <= 1e-12f * Mathf.Max(1f, Mathf.Sqrt(rr * ss)))
            {
                // Parallel: only collinear edges meet, at each one's ends lying on the other.
                if (Mathf.Abs(Cross(c - a, r)) > Probe * Mathf.Sqrt(rr)) return;
                Cut(cuts[i], Vector2.Dot(c - a, r) / rr, c, vertex);
                Cut(cuts[i], Vector2.Dot(d - a, r) / rr, d, vertex);
                Cut(cuts[j], Vector2.Dot(a - c, s) / ss, a, vertex);
                Cut(cuts[j], Vector2.Dot(b - c, s) / ss, b, vertex);
                return;
            }
            float t = Cross(c - a, s) / denominator, u = Cross(c - a, r) / denominator;
            float slackT = Probe / Mathf.Sqrt(rr), slackU = Probe / Mathf.Sqrt(ss);
            if (t < -slackT || t > 1f + slackT || u < -slackU || u > 1f + slackU) return;
            Vector2 p = Mathf.Abs(u) <= slackU ? c : Mathf.Abs(u - 1f) <= slackU ? d
                      : Mathf.Abs(t) <= slackT ? a : Mathf.Abs(t - 1f) <= slackT ? b
                      : a + r * t;
            Cut(cuts[i], t, p, vertex);
            Cut(cuts[j], u, p, vertex);
        }

        private static void Cut(List<(float t, int vertex)> along, float t, Vector2 p, Func<Vector2, int> vertex)
        {
            if (t > 0f && t < 1f) along.Add((t, vertex(p)));
        }

        // Drops repeated and collinear vertices; null when fewer than three are left.
        private static Vector2[] Simplify(List<Vector2> loop)
        {
            var ring = new List<Vector2>(loop);
            for (bool changed = true; changed && ring.Count >= 3;)
            {
                changed = false;
                for (int i = 0; i < ring.Count; ++i)
                {
                    Vector2 prev = ring[(i + ring.Count - 1) % ring.Count], next = ring[(i + 1) % ring.Count];
                    Vector2 e = next - prev;
                    float len = e.magnitude;
                    bool repeated = (ring[i] - prev).sqrMagnitude <= Probe * Probe;
                    bool collinear = len > 0f && Mathf.Abs(Cross(e, ring[i] - prev)) / len <= Probe &&
                                     Vector2.Dot(ring[i] - prev, e) > 0f && Vector2.Dot(next - ring[i], e) > 0f;
                    if (repeated || collinear)
                    {
                        ring.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            return ring.Count >= 3 ? ring.ToArray() : null;
        }

        private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        private static Vector2[] ToArray(IReadOnlyList<Vector2> ring)
        {
            var copy = new Vector2[ring.Count];
            for (int i = 0; i < copy.Length; ++i) copy[i] = ring[i];
            return copy;
        }
    }
}
