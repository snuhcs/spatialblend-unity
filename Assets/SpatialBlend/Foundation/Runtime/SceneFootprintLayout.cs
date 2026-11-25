// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// How observed room geometry is laid out in the element tree so that no two SIBLING footprints overlap:
    /// the core refuses two sibling elements whose footprints overlap with positive area beyond
    /// the data precision, and real rooms overlap (a lamp on a table, a couch against a coffee table).
    /// <list type="bullet">
    /// <item>CONTAINED: a footprint that lies entirely inside a larger one (at data precision) is registered as a CHILD
    /// of the larger. Nothing is changed in either footprint.</item>
    /// <item>PARTIAL overlap: the two stay siblings and the smaller footprint is clipped by the overlap (the larger one is
    /// kept whole), so the union of the footprints, and with it the free space, is unchanged.</item>
    /// </list>
    /// There is no percentage threshold: only exact containment nests. The planner is pure and deterministic: footprints
    /// are taken in the order (area descending, key ascending) and each one is placed against the ones already placed.
    /// Footprints are world ground polygons (x, z of Unity space), convex and counter-clockwise (an observed box).
    /// </summary>
    public static class SceneFootprintLayout
    {
        /// <summary>
        /// The data precision the core applies to sibling overlap, relative to the smaller footprint's area
        /// (relation/support.h kUprightCosTolerance): the apps hand the core single-precision matrices.
        /// </summary>
        public const double DataPrecision = 1e-6;

        /// <summary>
        /// How far a clipped piece is kept off the edge it was cut at, for a layout whose coordinates are at most
        /// <paramref name="largestCoordinate"/> (metres), so that "touching" stays touching in the core.
        ///
        /// Where the rounding is. The core decides sibling overlap in containment_layout.cc CheckPair: the intersection
        /// of the two WorldFootprints, each the element's local ring placed by its world transform, is refused when
        /// it exceeds kUprightCosTolerance (1e-6, relation/support.h) of the smaller footprint's area. That arithmetic is in
        /// double (relative error 2^-53 = 1.1e-16, which is nothing here). What it is handed is single precision, because
        /// the apps hand the core float32 matrices and rings (support.h says so). So the world position of a piece vertex
        /// differs from the planned one only by the roundings on this side, each at most half a unit in the last place of
        /// a number of magnitude at most L = the largest coordinate (metres, at least 1): rounding to float32 changes a value x
        /// by at most 2^-24 * |x| (a half ulp), so each rounding below costs at most 2^-24 * L:
        ///   1 half ulp  for the vertex stored as float32 in the element's local frame;
        ///   4 half ulps for the element's float32 transform (three rotation entries times a coordinate and the translation,
        ///                each entry rounded once; the rotation entries are at most 1 in magnitude);
        ///   4 half ulps for the float32 conversion of the planned world vertex into that local frame (the inverse of the
        ///                transform and its product with the vertex: the same three products and a translation, each carried at most
        ///                one rounding further).
        /// A vertex therefore lands within 1 + 4 + 4 = 9 half ulps = <see cref="RoundingHalfUlpsPerVertex"/> of where it was
        /// planned. Two pieces touch along a cut: each side's vertex moves by that much, in opposite directions at worst, so
        /// the gap that cannot turn into an overlap is twice that: 2 * 9 * L * 2^-24 = 9 * L * 2^-23. The test
        /// ClearanceCoversTheRoundingOfTheSinglePrecisionPathToTheCore measures the actual deviation on the eight sample rooms
        /// and asserts it stays under half this value. The union of the footprints differs by this width times the
        /// cut's length (about a micrometre times a metre), far below the data precision the core itself applies.
        /// </summary>
        public static double Clearance(float largestCoordinate) =>
            2.0 * RoundingHalfUlpsPerVertex * Math.Max(1.0, largestCoordinate) * Math.Pow(2.0, -24);

        /// <summary>The sum of the roundings of one vertex on its way to the core, in half units in the last place (see <see cref="Clearance"/>).</summary>
        public const int RoundingHalfUlpsPerVertex = 1 + 4 + 4;

        public readonly struct Input
        {
            public Input(int key, Vector2[] ring) { Key = key; Ring = ring; }
            public int Key { get; }
            /// <summary>The world ground footprint, convex, counter-clockwise; null when the geometry has no footprint.</summary>
            public Vector2[] Ring { get; }
        }

        public sealed class Placement
        {
            /// <summary>The key of the larger footprint that contains this one, or 0 for a child of the room.</summary>
            public int ParentKey;
            /// <summary>
            /// The footprint after clipping, as convex counter-clockwise pieces whose union is the footprint minus the
            /// larger siblings'. Null when nothing was clipped (the footprint is registered whole).
            /// </summary>
            public List<Vector2[]> Pieces;
        }

        public static Dictionary<int, Placement> Compute(IReadOnlyList<Input> inputs)
        {
            var result = new Dictionary<int, Placement>();
            var order = new List<Input>();
            foreach (Input input in inputs)
            {
                if (input.Ring == null || input.Ring.Length < 3 || Area(input.Ring) <= 0.0)
                {
                    result[input.Key] = new Placement();
                    continue;
                }
                order.Add(input);
            }
            order.Sort((a, b) =>
            {
                int byArea = Area(b.Ring).CompareTo(Area(a.Ring));
                return byArea != 0 ? byArea : a.Key.CompareTo(b.Key);
            });

            float largest = 1f;
            foreach (Input input in order)
                foreach (Vector2 v in input.Ring) largest = Mathf.Max(largest, Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y)));
            double clearance = Clearance(largest);

            var children = new Dictionary<int, List<int>> { [0] = new List<int>() };
            var ringOf = new Dictionary<int, Vector2[]>();
            var piecesOf = new Dictionary<int, List<Vector2[]>>();
            foreach (Input input in order)
            {
                double area = Area(input.Ring);
                int parent = 0;
                for (bool descended = true; descended;)
                {
                    descended = false;
                    foreach (int child in children[parent])
                    {
                        if (Contains(ringOf[child], input.Ring, area))
                        {
                            parent = child;
                            descended = true;
                            break;
                        }
                    }
                }

                // The remaining siblings overlap partially (or not at all): clip this footprint by theirs.
                List<Vector2[]> pieces = new List<Vector2[]> { input.Ring };
                bool clipped = false;
                foreach (int sibling in children[parent])
                {
                    foreach (Vector2[] cutter in piecesOf[sibling])
                    {
                        var next = new List<Vector2[]>();
                        foreach (Vector2[] piece in pieces)
                        {
                            double shared = IntersectionArea(piece, cutter);
                            if (shared <= DataPrecision * Math.Min(Area(piece), Area(cutter)))
                            {
                                next.Add(piece);
                                continue;
                            }
                            clipped = true;
                            Subtract(piece, cutter, next, DataPrecision * area, clearance);
                        }
                        pieces = next;
                    }
                }

                ringOf[input.Key] = input.Ring;
                piecesOf[input.Key] = pieces;
                children[input.Key] = new List<int>();
                children[parent].Add(input.Key);
                result[input.Key] = new Placement { ParentKey = parent, Pieces = clipped ? pieces : null };
            }
            return result;
        }

        /// <summary>True when <paramref name="inner"/> lies inside <paramref name="outer"/> at data precision.</summary>
        public static bool Contains(Vector2[] outer, Vector2[] inner, double innerArea)
        {
            return IntersectionArea(outer, inner) >= (1.0 - DataPrecision) * innerArea;
        }

        public static double Area(IReadOnlyList<Vector2> ring)
        {
            double sum = 0.0;
            for (int i = 0, n = ring.Count; i < n; ++i)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % n];
                sum += (double)a.x * b.y - (double)b.x * a.y;
            }
            return Math.Abs(sum) * 0.5;
        }

        public static double IntersectionArea(Vector2[] a, Vector2[] b)
        {
            List<Vector2> clipped = new List<Vector2>(a);
            for (int i = 0; i < b.Length && clipped.Count >= 3; ++i)
                clipped = Clip(clipped, b[i], b[(i + 1) % b.Length], keepInside: true);
            return clipped.Count < 3 ? 0.0 : Area(clipped);
        }

        /// <summary>
        /// <paramref name="piece"/> minus <paramref name="cutter"/>, both convex and counter-clockwise: disjoint convex parts, each
        /// kept <paramref name="clearance"/> off the cut, those smaller than <paramref name="minimumArea"/> dropped.
        /// </summary>
        internal static void SubtractConvex(Vector2[] piece, Vector2[] cutter, List<Vector2[]> into, double minimumArea, double clearance) =>
            Subtract(piece, cutter, into, minimumArea, clearance);

        // piece minus cutter, both convex counter-clockwise: the parts of the piece outside each edge of the cutter in
        // turn, each inside the edges before it. The parts are disjoint and convex.
        private static void Subtract(Vector2[] piece, Vector2[] cutter, List<Vector2[]> into, double minimumArea, double clearance)
        {
            List<Vector2> remaining = new List<Vector2>(piece);
            for (int i = 0; i < cutter.Length && remaining.Count >= 3; ++i)
            {
                Vector2 p = cutter[i], q = cutter[(i + 1) % cutter.Length];
                // A zero-length edge (a duplicate vertex) has no direction and no outside; it cuts nothing.
                if ((q - p).sqrMagnitude < DuplicateVertexSquared) continue;
                // The part outside this edge, kept `clearance` off it (the outward side of a counter-clockwise edge is its right).
                Vector2 edge = (q - p).normalized;
                Vector2 shift = new Vector2(edge.y, -edge.x) * (float)clearance;
                List<Vector2> outside = Clip(remaining, p + shift, q + shift, keepInside: false);
                if (outside.Count >= 3 && Area(outside) > minimumArea) into.Add(outside.ToArray());
                remaining = Clip(remaining, p, q, keepInside: true);
            }
        }

        // Sutherland-Hodgman against one directed edge p->q of a counter-clockwise polygon: inside is the left side.
        private static List<Vector2> Clip(List<Vector2> polygon, Vector2 p, Vector2 q, bool keepInside)
        {
            var output = new List<Vector2>();
            int n = polygon.Count;
            for (int i = 0; i < n; ++i)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % n];
                double da = Side(p, q, a) * (keepInside ? 1.0 : -1.0);
                double db = Side(p, q, b) * (keepInside ? 1.0 : -1.0);
                bool aIn = da >= 0.0, bIn = db >= 0.0;
                if (aIn) output.Add(a);
                if (aIn != bIn)
                {
                    double t = da / (da - db);
                    output.Add(new Vector2((float)(a.x + (b.x - a.x) * t), (float)(a.y + (b.y - a.y) * t)));
                }
            }
            // A vertex exactly on the edge is emitted twice (as itself and as the crossing); consecutive duplicates are dropped
            // so a clipped piece never carries a zero-length edge into the next cut.
            for (int i = output.Count - 1; i >= 0 && output.Count > 1; --i)
            {
                if ((output[i] - output[(i + 1) % output.Count]).sqrMagnitude < DuplicateVertexSquared) output.RemoveAt(i);
            }
            return output;
        }

        // Two vertices closer than 1 micrometre are one vertex (the data is millimetre-scale floats).
        private const float DuplicateVertexSquared = 1e-12f;

        private static double Side(Vector2 p, Vector2 q, Vector2 r) =>
            ((double)q.x - p.x) * ((double)r.y - p.y) - ((double)q.y - p.y) * ((double)r.x - p.x);
    }
}
