// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Reads a <see cref="RoomRegion"/> from an MRUK room: the FLOOR anchor's measured outline, each WALL_FACE / INVISIBLE_WALL_FACE
    /// anchor's bottom edge, and the ground footprint of every anchor with a volume, exactly as the scene-geometry adapter stages it
    /// (<see cref="MRUKSceneGeometryAdapter.TryCreateDescriptor"/>, so the ring holds the boxes the core receives).
    /// </summary>
    public static class MrukRoomRegion
    {
        public static RoomRegion Build(MRUKRoom room, float margin = RoomRegion.DefaultMarginMeters)
        {
            if (room == null) return null;
            Vector2[] floor = FloorOutline(room);
            if (floor == null) return null;
            var walls = new List<(Vector2, Vector2)>();
            var furniture = new List<Vector2[]>();
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (anchor == null) continue;
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE))
                {
                    if (TryWallBottom(anchor, out Vector2 a, out Vector2 b)) walls.Add((a, b));
                    continue;
                }
                if (MRUKSceneGeometryAdapter.TryCreateDescriptor(anchor, out SceneGeometryDescriptor d) &&
                    SceneGeometryFootprint.TryRing(d, out Vector2[] ring))
                    furniture.Add(ring);
            }
            return RoomRegion.Build(floor, walls, furniture, margin);
        }

        /// <summary>The ground footprint of every anchor of <paramref name="room"/> with a volume, as the adapter stages it, read now (anchors that arrived later included).</summary>
        public static List<Vector2[]> Furniture(MRUKRoom room)
        {
            var furniture = new List<Vector2[]>();
            if (room == null) return furniture;
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (anchor == null || anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE)) continue;
                if (MRUKSceneGeometryAdapter.TryCreateDescriptor(anchor, out SceneGeometryDescriptor d) &&
                    SceneGeometryFootprint.TryRing(d, out Vector2[] ring))
                    furniture.Add(ring);
            }
            return furniture;
        }

        /// <summary>The FLOOR anchor's measured outline in world (x, z), or null.</summary>
        public static Vector2[] FloorOutline(MRUKRoom room)
        {
            MRUKAnchor floor = room != null ? room.FloorAnchor : null;
            if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3) return null;
            var outline = new Vector2[floor.PlaneBoundary2D.Count];
            for (int i = 0; i < outline.Length; ++i)
            {
                Vector3 world = floor.transform.TransformPoint(new Vector3(floor.PlaneBoundary2D[i].x, floor.PlaneBoundary2D[i].y, 0f));
                outline[i] = new Vector2(world.x, world.z);
            }
            return outline;
        }

        // The wall plane's bottom edge on the ground: its rect's lower corners.
        private static bool TryWallBottom(MRUKAnchor wall, out Vector2 a, out Vector2 b)
        {
            a = b = Vector2.zero;
            if (!wall.PlaneRect.HasValue) return false;
            Rect r = wall.PlaneRect.Value;
            Vector3 p = wall.transform.TransformPoint(new Vector3(r.xMin, r.yMin, 0f));
            Vector3 q = wall.transform.TransformPoint(new Vector3(r.xMax, r.yMin, 0f));
            a = new Vector2(p.x, p.z);
            b = new Vector2(q.x, q.z);
            return (b - a).sqrMagnitude > 1e-6f;
        }
    }
}
