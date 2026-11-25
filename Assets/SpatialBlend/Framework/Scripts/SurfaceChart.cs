// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// The plane chart the core derives for a surface (<c>Polygon3D::ComputeDerivedData</c>), and the one frame in which a
    /// surface sub-region's patch polygon is authored. <c>sb_add_sub_region</c> places a patch relative to the owning
    /// definition's own frame (spatialblend.h), and for a surface that frame is this chart:
    /// - the origin is the centroid of the vertices;
    /// - z is the normal, <c>(v1 - v0) x (v2 - v0)</c> normalized;
    /// - x is the first edge <c>v0 -> v1</c> projected onto the plane;
    /// - y is <c>z x x</c>.
    /// All of it is derived in the core's Z-up frame (the Unity-to-Z-up conversion swaps y and z, which is a reflection, so the
    /// normal and y are derived after converting, never in Unity space). A patch polygon given in the object's XZ, or in
    /// world xy, is therefore wrong; <see cref="TryProject"/> converts a point on the surface into the chart.
    /// Unity passes the authored polygon through with an identity transform, so it must already be chart-relative.
    /// </summary>
    public static class SurfaceChart
    {
        /// <summary>The chart coordinates of an object-local Unity point projected onto the surface plane.</summary>
        public static bool TryProject(Vector3[] unitySurface, Vector3 unityPoint, out Vector2 chart)
        {
            chart = default;
            if (unitySurface == null || unitySurface.Length < 3) return false;
            var v = new double[unitySurface.Length][];
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < unitySurface.Length; ++i)
            {
                CoordinateConverter.UnityToSpatialBlend3D(unitySurface[i], out double x, out double y, out double z);
                v[i] = new[] { x, y, z };
                cx += x; cy += y; cz += z;
            }
            double n = unitySurface.Length;
            cx /= n; cy /= n; cz /= n;

            double[] e1 = Sub(v[1], v[0]);
            double[] e2 = Sub(v[2], v[0]);
            double[] normal = Normalize(Cross(e1, e2));
            if (normal == null) return false;
            double[] xAxis = Normalize(Sub(e1, Scale(normal, Dot(e1, normal))));
            if (xAxis == null) return false;
            double[] yAxis = Normalize(Cross(normal, xAxis));

            CoordinateConverter.UnityToSpatialBlend3D(unityPoint, out double px, out double py, out double pz);
            double[] fromCentroid = { px - cx, py - cy, pz - cz };
            chart = new Vector2((float)Dot(fromCentroid, xAxis), (float)Dot(fromCentroid, yAxis));
            return true;
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double[] Normalize(double[] a)
        {
            double length = Math.Sqrt(Dot(a, a));
            return length < 1e-12 ? null : Scale(a, 1.0 / length);
        }
    }
}
