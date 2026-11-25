// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Turns a NavMesh triangulation into the outer walkable ring and its holes.
    /// Vertices within 1 mm are welded first, so a NavMesh seam that duplicated
    /// a vertex still cancels. Shared triangle edges then cancel. Each remaining
    /// loop is simplified, then classified by signed area and containment: the
    /// largest ring is the boundary, and an opposite-winding ring inside it is
    /// a hole.
    /// </summary>
    public static class NavMeshWalkableExtractor
    {
        public const float CollinearEpsilon = 0.001f;

        public static bool TryExtract(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices,
                                       out WalkableRegion region)
        {
            region = null;
            if (vertices == null || indices == null || indices.Count < 3 || (indices.Count % 3) != 0)
                return false;

            if (!TryWeld(vertices, out int[] canonical, out Vector2[] welded))
                return false;

            var forward = new Dictionary<ulong, int>();
            int triangleCount = indices.Count / 3;
            for (int triangle = 0; triangle < triangleCount; ++triangle)
            {
                int ia = indices[triangle * 3];
                int ib = indices[triangle * 3 + 1];
                int ic = indices[triangle * 3 + 2];
                if ((uint)ia >= (uint)vertices.Count || (uint)ib >= (uint)vertices.Count ||
                    (uint)ic >= (uint)vertices.Count)
                    return false;

                int a = canonical[ia];
                int b = canonical[ib];
                int c = canonical[ic];
                if (a == b || b == c || c == a)
                    continue;

                if (Mathf.Abs(Cross(welded[a], welded[b], welded[c])) <= CollinearEpsilon * CollinearEpsilon)
                    continue;

                AddDirected(forward, a, b);
                AddDirected(forward, b, c);
                AddDirected(forward, c, a);
            }

            var next = new Dictionary<int, List<int>>();
            var seen = new HashSet<ulong>();
            foreach (KeyValuePair<ulong, int> pair in forward)
            {
                if (!seen.Add(pair.Key))
                    continue;
                Unpack(pair.Key, out int from, out int to);
                ulong reverseKey = Pack(to, from);
                seen.Add(reverseKey);
                int reverse = 0;
                if (forward.TryGetValue(reverseKey, out int reverseCount))
                    reverse = reverseCount;
                int net = pair.Value - reverse;
                if (net > 0)
                    AddCopies(next, from, to, net);
                else if (net < 0)
                    AddCopies(next, to, from, -net);
            }

            Vector2[] ground = welded;

            var loops = new List<List<Vector2>>();
            int guardBudget = vertices.Count * 4 + indices.Count;
            while (TryTakeStart(next, out int start))
            {
                var loop = new List<int> { start };
                int current = start;
                bool closed = false;
                for (int step = 0; step < guardBudget; ++step)
                {
                    if (!TryPop(next, current, out int successor))
                        break;
                    if (successor == start && loop.Count >= 3)
                    {
                        closed = true;
                        break;
                    }
                    loop.Add(successor);
                    current = successor;
                }
                if (!closed)
                    continue;

                var ring = new List<Vector2>(loop.Count);
                for (int i = 0; i < loop.Count; ++i)
                    ring.Add(ground[loop[i]]);
                SimplifyCollinear(ring);
                if (ring.Count >= 3 && Mathf.Abs(SignedArea(ring)) > CollinearEpsilon)
                    loops.Add(ring);
            }

            if (loops.Count == 0)
                return false;

            int outerIndex = 0;
            float outerArea = Mathf.Abs(SignedArea(loops[0]));
            for (int i = 1; i < loops.Count; ++i)
            {
                float area = Mathf.Abs(SignedArea(loops[i]));
                if (area > outerArea)
                {
                    outerArea = area;
                    outerIndex = i;
                }
            }

            List<Vector2> outer = loops[outerIndex];
            float outerSign = Mathf.Sign(SignedArea(outer));
            var holes = new List<Vector2[]>();
            for (int i = 0; i < loops.Count; ++i)
            {
                if (i == outerIndex)
                    continue;
                List<Vector2> loop = loops[i];
                if (Mathf.Sign(SignedArea(loop)) == outerSign)
                    continue;
                if (Mathf.Abs(SignedArea(loop)) >= outerArea)
                    continue;
                if (!ContainedBy(outer, loop))
                    continue;
                if (SignedArea(loop) > 0f)
                    loop.Reverse();
                RotateToMinimum(loop);
                holes.Add(loop.ToArray());
            }

            if (SignedArea(outer) < 0f)
                outer.Reverse();
            RotateToMinimum(outer);
            holes.Sort(CompareRings);
            region = new WalkableRegion(outer, holes);
            return region.HasBoundary;
        }

        private static int CompareRings(Vector2[] a, Vector2[] b)
        {
            int x = a[0].x.CompareTo(b[0].x);
            if (x != 0)
                return x;
            return a[0].y.CompareTo(b[0].y);
        }

        private static bool ContainedBy(List<Vector2> outer, List<Vector2> inner)
        {
            Vector2 average = Vector2.zero;
            for (int i = 0; i < inner.Count; ++i)
                average += inner[i];
            average /= inner.Count;
            if (Contains(outer, average))
                return true;
            for (int i = 0; i < inner.Count; ++i)
            {
                if (Contains(outer, inner[i]))
                    return true;
            }
            return false;
        }

        private static bool Contains(List<Vector2> ring, Vector2 point)
        {
            bool inside = false;
            int previous = ring.Count - 1;
            for (int i = 0; i < ring.Count; previous = i++)
            {
                Vector2 a = ring[previous];
                Vector2 b = ring[i];
                if ((a.y > point.y) != (b.y > point.y))
                {
                    float x = a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y);
                    if (point.x < x)
                        inside = !inside;
                }
            }
            return inside;
        }

        private static void SimplifyCollinear(List<Vector2> ring)
        {
            int passes = ring.Count;
            for (int pass = 0; pass < passes && ring.Count >= 3; ++pass)
            {
                bool changed = false;
                var kept = new List<Vector2>(ring.Count);
                int count = ring.Count;
                for (int i = 0; i < count; ++i)
                {
                    Vector2 previous = ring[(i - 1 + count) % count];
                    Vector2 current = ring[i];
                    Vector2 next = ring[(i + 1) % count];
                    if (Vector2.Distance(previous, current) <= CollinearEpsilon)
                    {
                        changed = true;
                        continue;
                    }
                    float span = Mathf.Max(Vector2.Distance(previous, next), CollinearEpsilon);
                    if (Mathf.Abs(Cross(previous, current, next)) <= CollinearEpsilon * span)
                    {
                        changed = true;
                        continue;
                    }
                    kept.Add(current);
                }
                if (!changed)
                    return;
                ring.Clear();
                ring.AddRange(kept);
            }
        }

        private static void RotateToMinimum(List<Vector2> ring)
        {
            int minimum = 0;
            for (int i = 1; i < ring.Count; ++i)
            {
                if (ring[i].x < ring[minimum].x ||
                    (ring[i].x == ring[minimum].x && ring[i].y < ring[minimum].y))
                    minimum = i;
            }
            if (minimum == 0)
                return;
            var rotated = new List<Vector2>(ring.Count);
            for (int i = 0; i < ring.Count; ++i)
                rotated.Add(ring[(minimum + i) % ring.Count]);
            ring.Clear();
            ring.AddRange(rotated);
        }

        private static float SignedArea(List<Vector2> ring)
        {
            float area = 0f;
            for (int i = 0; i < ring.Count; ++i)
            {
                Vector2 current = ring[i];
                Vector2 next = ring[(i + 1) % ring.Count];
                area += current.x * next.y - next.x * current.y;
            }
            return area * 0.5f;
        }

        private static bool TryTakeStart(Dictionary<int, List<int>> next, out int start)
        {
            foreach (KeyValuePair<int, List<int>> pair in next)
            {
                if (pair.Value.Count > 0)
                {
                    start = pair.Key;
                    return true;
                }
            }
            start = 0;
            return false;
        }

        private static bool TryPop(Dictionary<int, List<int>> next, int from, out int to)
        {
            to = 0;
            if (!next.TryGetValue(from, out List<int> successors) || successors.Count == 0)
                return false;
            int last = successors.Count - 1;
            to = successors[last];
            successors.RemoveAt(last);
            return true;
        }

        private static void AddCopies(Dictionary<int, List<int>> next, int from, int to, int copies)
        {
            if (!next.TryGetValue(from, out List<int> successors))
            {
                successors = new List<int>();
                next.Add(from, successors);
            }
            for (int i = 0; i < copies; ++i)
                successors.Add(to);
        }

        private static void AddDirected(Dictionary<ulong, int> forward, int from, int to)
        {
            ulong key = Pack(from, to);
            forward.TryGetValue(key, out int count);
            forward[key] = count + 1;
        }

        private static ulong Pack(int from, int to) => ((ulong)(uint)from << 32) | (uint)to;

        private static void Unpack(ulong key, out int from, out int to)
        {
            from = (int)(key >> 32);
            to = (int)(key & 0xffffffffu);
        }

        private static bool TryWeld(IReadOnlyList<Vector3> vertices, out int[] canonical, out Vector2[] welded)
        {
            canonical = new int[vertices.Count];
            var positions = new List<Vector2>(vertices.Count);
            var keys = new Dictionary<long, int>();
            for (int i = 0; i < vertices.Count; ++i)
            {
                Vector2 ground = Ground(vertices[i]);
                long key = PackMillimetre(ground);
                if (!keys.TryGetValue(key, out int id))
                {
                    id = positions.Count;
                    keys.Add(key, id);
                    positions.Add(ground);
                }
                canonical[i] = id;
            }
            welded = positions.ToArray();
            return true;
        }

        // 1 mm bins. The high half is the rounded X and the low half the rounded Z,
        // so a seam duplicate and its neighbour cancel as one vertex.
        private static long PackMillimetre(Vector2 ground)
        {
            int x = Mathf.RoundToInt(ground.x * 1000f);
            int z = Mathf.RoundToInt(ground.y * 1000f);
            return ((long)x << 32) | (uint)z;
        }

        private static Vector2 Ground(Vector3 vertex) => new Vector2(vertex.x, vertex.z);

        private static float Cross(Vector2 origin, Vector2 a, Vector2 b)
        {
            return (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x);
        }
    }
}
