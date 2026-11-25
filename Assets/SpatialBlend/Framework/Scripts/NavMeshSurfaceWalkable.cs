// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace SpatialBlend
{
    /// <summary>
    /// The one reading of a baked <see cref="NavMeshSurface"/> as the room's walkable footprint. The manager's
    /// registration and the local backend's navigable root both come from this, so there is a single source of truth
    /// for the room.
    /// </summary>
    public static class NavMeshSurfaceWalkable
    {
        /// <summary>
        /// Reads the surface's triangulation (limited to its own world bounds) into the walkable ring and holes.
        /// <paramref name="height"/> is the mean height of the triangles' vertices. False when the surface is
        /// missing, disabled, or has no triangulation.
        /// </summary>
        public static bool TryRead(NavMeshSurface surface, out WalkableRegion region, out float height)
        {
            region = null;
            height = 0f;
            if (surface == null || !surface.enabled || surface.navMeshData == null) return false;
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.indices == null) return false;

            Bounds bounds = WorldBounds(surface.navMeshData);
            var filtered = new List<int>();
            float heightSum = 0f;
            int heightCount = 0;
            for (int i = 0; i < triangulation.indices.Length / 3; i++)
            {
                Vector3 v0 = triangulation.vertices[triangulation.indices[i * 3]];
                Vector3 v1 = triangulation.vertices[triangulation.indices[i * 3 + 1]];
                Vector3 v2 = triangulation.vertices[triangulation.indices[i * 3 + 2]];
                if (!bounds.Contains(v0) || !bounds.Contains(v1) || !bounds.Contains(v2)) continue;
                filtered.Add(triangulation.indices[i * 3]);
                filtered.Add(triangulation.indices[i * 3 + 1]);
                filtered.Add(triangulation.indices[i * 3 + 2]);
                heightSum += v0.y + v1.y + v2.y;
                heightCount += 3;
            }
            height = heightCount == 0 ? bounds.min.y : heightSum / heightCount;
            if (heightCount > 0 && NavMeshWalkableExtractor.TryExtract(triangulation.vertices, filtered, out region))
                return true;
            // The triangulation had no usable boundary: the surface's own bounds are the footprint.
            region = new WalkableRegion(new[]
            {
                new Vector2(bounds.min.x, bounds.min.z), new Vector2(bounds.max.x, bounds.min.z),
                new Vector2(bounds.max.x, bounds.max.z), new Vector2(bounds.min.x, bounds.max.z),
            }, null);
            return true;
        }

        /// <summary>World-space bounds of a NavMeshData: its sourceBounds transformed by its position and rotation.</summary>
        internal static Bounds WorldBounds(NavMeshData data)
        {
            Bounds local = data.sourceBounds;
            Vector3 min = local.min, max = local.max;
            Bounds world = default;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y,
                                         (i & 4) == 0 ? min.z : max.z);
                Vector3 point = data.rotation * corner + data.position;
                if (i == 0) world = new Bounds(point, Vector3.zero); else world.Encapsulate(point);
            }
            return world;
        }
    }
}
