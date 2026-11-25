// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Walkable area as the v2 core paths it: one outer ring, plus one obstacle
    /// footprint per hole. Points are Unity ground coordinates (x, z).
    ///
    /// The core does not consume inner rings. <c>Polygon2D</c> is a single
    /// ring (<c>spatialblend/geometry/polygon2d.h:35</c>) and
    /// <c>sb_set_element_polygon</c> stores that one ring
    /// (<c>spatialblend/c/spatialblend_c.cc:774</c>). Free space is the
    /// navigable polygon minus obstacle footprints
    /// (<c>spatialblend/geometry/obstacle_free_space.h:28</c>).
    /// <c>Space::SyncNavigableObstaclesLocked</c> copies every
    /// <c>GeometryRole::kObstacle</c> footprint into the navigable definition
    /// (<c>spatialblend/space/space.cc:4484</c>, role test at <c>:4507</c>).
    /// The navigable ring is used as given
    /// (<c>spatialblend/c/spatialblend.h:662</c>); a hole is
    /// <c>SB_GEOMETRY_OBSTACLE</c> (<c>spatialblend/c/spatialblend_type.h:35</c>).
    /// </summary>
    public sealed class WalkableRegion
    {
        public WalkableRegion(IReadOnlyList<Vector2> boundary, IReadOnlyList<Vector2[]> holes)
        {
            Boundary = CopyRing(boundary);
            if (holes == null || holes.Count == 0)
            {
                Holes = Array.Empty<Vector2[]>();
                return;
            }

            var copied = new Vector2[holes.Count][];
            for (int i = 0; i < holes.Count; ++i)
                copied[i] = CopyRing(holes[i]);
            Holes = copied;
        }

        public Vector2[] Boundary { get; }

        public Vector2[][] Holes { get; }

        public bool HasBoundary => Boundary.Length >= 3;

        private static Vector2[] CopyRing(IReadOnlyList<Vector2> ring)
        {
            if (ring == null || ring.Count == 0)
                return Array.Empty<Vector2>();
            var copy = new Vector2[ring.Count];
            for (int i = 0; i < ring.Count; ++i)
                copy[i] = ring[i];
            return copy;
        }
    }
}
