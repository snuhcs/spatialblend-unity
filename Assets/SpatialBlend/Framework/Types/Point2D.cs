// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct Point2D
    {
        public double X { get; }
        public double Y { get; }

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        internal Point2D(Internal.SbPoint2D native)
        {
            X = native.x;
            Y = native.y;
        }
    }
}