// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PolygonRing
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public double[] points;
        public ulong num_points;
        [MarshalAs(UnmanagedType.I1)] public bool is_inner;

        public double[] Points => points;
        public ulong NumPoints => num_points;
        public bool IsInner => is_inner;

        internal PolygonRing(SbPolygonRing native)
        {
            points = native.points;
            num_points = native.num_points;
            is_inner = native.is_inner;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Polygon
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public PolygonRing[] rings;
        public ulong num_rings;
        public double min_x, min_y;
        public double max_x, max_y;

        public PolygonRing[] Rings => rings;
        public ulong NumRings => num_rings;
        public double MinX => min_x;
        public double MinY => min_y;
        public double MaxX => max_x;
        public double MaxY => max_y;

        internal Polygon(SbPolygon native)
        {
            rings = new PolygonRing[native.num_rings];
            for (ulong i = 0; i < native.num_rings; i++)
            {
                rings[i] = new PolygonRing(native.rings[i]);
            }
            num_rings = native.num_rings;
            min_x = native.min_x;
            min_y = native.min_y;
            max_x = native.max_x;
            max_y = native.max_y;
        }
    }
}
