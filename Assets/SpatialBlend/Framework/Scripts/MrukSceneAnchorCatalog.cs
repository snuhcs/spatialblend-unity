// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Reads the current MRUK room (<see cref="MrukRoomTracker"/>). GLOBAL_MESH anchors are skipped. Size is the
    /// volume when one exists, otherwise the plane area.
    /// </summary>
    public sealed class MrukSceneAnchorCatalog : ISceneAnchorCatalog, IRoomScopedCatalog
    {
        private readonly Func<MRUKRoom> _room;
        // Enum.GetValues allocates a new array per call; the catalog is read per anchor, by the observer every half second.
        private static readonly MRUKAnchor.SceneLabels[] Labels = (MRUKAnchor.SceneLabels[])Enum.GetValues(typeof(MRUKAnchor.SceneLabels));

        /// <summary>Reads MRUK's confirmed room.</summary>
        public MrukSceneAnchorCatalog() { }

        /// <summary>Reads the given room (a room a test or a tool built), with the same measurement.</summary>
        public MrukSceneAnchorCatalog(Func<MRUKRoom> room) { _room = room; }

        /// <summary>The UUID of the room these anchors belong to, or empty while MRUK has not confirmed a room.</summary>
        public string RoomKey => _room != null ? RoomUuid(_room()) : MrukRoomTracker.CurrentKey(MRUK.Instance);

        public IReadOnlyList<SceneAnchorRecord> Anchors
        {
            get
            {
                var records = new List<SceneAnchorRecord>();
                // The confirmed room, not GetCurrentRoom(): that returns the last or the first room when the headset is in none.
                MRUKRoom room = _room != null ? _room() : MrukRoomTracker.Current(MRUK.Instance);
                if (room == null)
                {
                    return records;
                }
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (anchor == null || anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                    {
                        continue;
                    }
                    if (!TryMeasure(anchor, out Vector3 center, out Vector3 size, out float metric))
                    {
                        continue;
                    }
                    // Not catalogued before MRUK has put a UUID-less anchor in its room (its key is its pose in the room).
                    if (anchor.Anchor.Uuid == System.Guid.Empty && anchor.Room == null) continue;
                    Vector2[] plane = MeasurePlane(anchor, out bool rectApproximation);
                    foreach (MRUKAnchor.SceneLabels label in Labels)
                    {
                        int bits = (int)label;
                        if (bits == 0 || (bits & (bits - 1)) != 0 ||
                            label == MRUKAnchor.SceneLabels.GLOBAL_MESH || !anchor.HasAnyLabel(label))
                        {
                            continue;
                        }
                        records.Add(new SceneAnchorRecord
                        {
                            Label = label.ToString(),
                            Anchor = anchor.gameObject,
                            SourceKey = MRUKSceneGeometryAdapter.StableKey(anchor),
                            AnchorId = AnchorUuid(anchor),
                            Size = metric,
                            LocalCenter = center,
                            LocalSize = size,
                            PlaneBoundary = plane,
                            PlaneIsRectApproximation = rectApproximation
                        });
                    }
                }
                return records;
            }
        }

        /// <summary>The MRUK room's UUID, the room's identity. Empty when there is none.</summary>
        public static string RoomUuid(MRUKRoom room)
        {
            if (room == null) return string.Empty;
            System.Guid uuid = room.Anchor.Uuid;
            return uuid == System.Guid.Empty ? string.Empty : uuid.ToString();
        }

        private static string AnchorUuid(MRUKAnchor anchor)
        {
            System.Guid uuid = anchor.Anchor.Uuid;
            return uuid == System.Guid.Empty ? string.Empty : uuid.ToString();
        }

        /// <summary>
        /// The anchor's measured plane outline (anchor-local XY). <c>PlaneBoundary2D</c> is the exact outline; the plane rectangle is
        /// the documented fallback only when MRUK has no outline, flagged by <paramref name="rectApproximation"/>
        /// (the same rule as <see cref="SceneGeometryDescriptor"/>). Null when the anchor has no plane.
        /// </summary>
        internal static Vector2[] MeasurePlane(MRUKAnchor anchor, out bool rectApproximation)
        {
            rectApproximation = false;
            if (anchor.PlaneBoundary2D != null && anchor.PlaneBoundary2D.Count >= 3)
            {
                return anchor.PlaneBoundary2D.ToArray();
            }
            if (anchor.PlaneRect.HasValue)
            {
                Rect rect = anchor.PlaneRect.Value;
                rectApproximation = true;
                return new[]
                {
                    new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
                    new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax),
                };
            }
            return null;
        }

        private static bool TryMeasure(MRUKAnchor anchor, out Vector3 center, out Vector3 size, out float metric)
        {
            center = Vector3.zero;
            size = Vector3.zero;
            metric = 0f;
            if (anchor.VolumeBounds.HasValue)
            {
                Bounds volume = anchor.VolumeBounds.Value;
                center = volume.center;
                size = volume.size;
                metric = size.x * size.y * size.z;
                return true;
            }
            if (anchor.PlaneRect.HasValue)
            {
                Rect rect = anchor.PlaneRect.Value;
                center = new Vector3(rect.center.x, rect.center.y, 0f);
                size = new Vector3(rect.width, rect.height, 0.05f);
                metric = rect.width * rect.height;
                return true;
            }
            return false;
        }
    }
}
