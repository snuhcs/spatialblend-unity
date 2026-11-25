// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct GeometryBounds
    {
        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width { get; }
        public double Height { get; }
        public double CenterX { get; }
        public double CenterY { get; }

        internal GeometryBounds(Internal.SbGeometryBounds native)
        {
            MinX = native.min_x;
            MinY = native.min_y;
            MaxX = native.max_x;
            MaxY = native.max_y;
            Width = native.width;
            Height = native.height;
            CenterX = native.center_x;
            CenterY = native.center_y;
        }
    }
}