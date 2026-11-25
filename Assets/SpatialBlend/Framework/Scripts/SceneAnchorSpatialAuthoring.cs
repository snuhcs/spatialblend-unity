// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Spatial authoring for the shared MRUK TABLE anchor. The anchor has no prefab: its box comes
    /// from the room scan, so its footprint, its extent and its tabletop surface are derived from that
    /// box when the anchor is registered, then written to the same <see cref="SbSpatialAuthoring"/>
    /// declaration every other shared object uses.
    /// <para>
    /// An MRUK anchor has local +Z up and its volume extends backward along local Z from the top face
    /// (<c>MRUKAnchor.VolumeBounds</c>). The declaration therefore uses an authoring frame that stands
    /// that anchor up: <see cref="MrukFrameEulerAngles"/>. In that frame +Y is up, so the footprint is in
    /// XZ like any other object. A frame point (x, y, z) is the anchor-local point (x, -z, y).
    /// </para>
    /// <para>
    /// Two tables are two different rooms, so they are not identical in the object frame. The correspondence
    /// between them is the owned one (T3.2), not the identity.
    /// </para>
    /// </summary>
    public static class SceneAnchorSpatialAuthoring
    {
        public const string TabletopTag = "Tabletop";
        public const string TableObjectId = "Table";
        public const string TableRegionId = "TableArea";
        public const string TabletopAffordanceId = "TableTop";
        public const string TabletopSupportId = "table.top";

        /// <summary>
        /// Stand-off added around the tabletop to make its extent: seated and standing users reach the table
        /// from every side. The MRUK scan gives no authored extent, so this is the authoring default, and the
        /// same 0.5 m the v1 SURROUND volume shipped with.
        /// </summary>
        public const float StandOffMeters = SpatialAuthoringCompiler.DefaultRegionMarginMeters;

        /// <summary>The MRUK label a seat has. MRUK has no CHAIR label: a COUCH anchor with a measured plane is the seat.</summary>
        public const string CouchLabel = "COUCH";
        public const string SittableTag = "Sittable";
        public const string SeatObjectId = "Seat";
        public const string SeatRegionId = "SeatArea";
        public const string SeatAffordanceId = "SeatSurface";
        public const string SeatSupportId = "seat.plane";
        /// <summary>The seat part: the patch of the seat surface the pelvis relation is measured on.</summary>
        public const string SeatPartName = "seat";
        public const string SeatPartTag = "seat";

        /// <summary>The MRUK anchor frame: local +Z is up, so the authoring frame turns it 90 degrees about X.</summary>
        public static readonly Vector3 MrukFrameEulerAngles = new Vector3(90f, 0f, 0f);

        /// <summary>
        /// Writes the TABLE declaration for an anchor whose volume box is <paramref name="localCenter"/> and
        /// <paramref name="localSize"/> in the anchor's local frame (<c>VolumeBounds</c>, or the plane rect
        /// with a thin depth). Idempotent: calling it again replaces the geometry.
        /// </summary>
        public static SbSpatialAuthoring Apply(GameObject anchor, Vector3 localCenter, Vector3 localSize)
        {
            if (anchor == null) return null;
            if (!TryBuildGeometry(localCenter, localSize, out Vector2[] footprint, out Vector2[] extent,
                    out Vector3[] tabletop))
            {
                return null;
            }

            SbSpatialAuthoring authoring = anchor.GetComponent<SbSpatialAuthoring>();
            if (authoring == null) authoring = anchor.AddComponent<SbSpatialAuthoring>();
            authoring.objectId = TableObjectId;
            authoring.regionId = TableRegionId;
            authoring.footprint = null;
            authoring.footprintPolygon = footprint;
            authoring.declaredExtent = extent;
            authoring.fallbackMarginMeters = StandOffMeters;
            authoring.frameEulerAngles = MrukFrameEulerAngles;
            authoring.includeChildAffordances = false;
            authoring.correspondenceKey = TabletopSupportId;
            authoring.affordances = new[]
            {
                new AuthoredAffordanceSurface
                {
                    id = TabletopAffordanceId,
                    tag = TabletopTag,
                    supportId = TabletopSupportId,
                    surfacePolygon = tabletop,
                },
            };
            return authoring;
        }

        /// <summary>
        /// Writes the seat declaration for a COUCH anchor that has a measured plane. The seat goes through the table's path
        /// (this class, <see cref="SbSpatialAuthoring"/>, <see cref="SpatialAuthoringCompiler"/>): an object whose footprint is
        /// the anchor's volume footprint, a region (the same authoring stand-off as the table), one <see cref="SittableTag"/>
        /// affordance surface that is the measured plane outline at the plane's height (an MRUK COUCH anchor's origin is on the
        /// seat, so the plane is local z = 0), and one <see cref="SeatPartName"/> part covering the whole surface.
        /// <para>
        /// <paramref name="frontDegrees"/> is the confirmed front of the seat in the anchor's local plane (degrees from local +x,
        /// counter-clockwise seen from +z), or null while no front is confirmed. The part's +x is the reference the core measures a
        /// pelvis' relative yaw from, so a confirmed front turns the part to it (<see cref="SeatPartRotationDegrees"/>); with no
        /// confirmed front the part is the plane's own frame and the driver ignores the seat's yaw (Facing decides it).
        /// </para>
        /// <para>
        /// <paramref name="seatKey"/> is the user's link: the two seats a user links share a key, and it replaces the table's fixed
        /// correspondence key. Returns null when the anchor has no usable plane: a seat is never registered at a guessed height.
        /// </para>
        /// </summary>
        public static SbSpatialAuthoring ApplySeat(GameObject anchor, Vector3 localCenter, Vector3 localSize,
                                                     Vector2[] planeOutline, string seatKey, float? frontDegrees)
        {
            if (anchor == null || string.IsNullOrWhiteSpace(seatKey)) return null;
            if (!TryBuildSeatGeometry(localCenter, localSize, planeOutline, frontDegrees, out Vector2[] footprint,
                    out Vector2[] extent, out Vector3[] surface, out AuthoredSubRegion seat))
            {
                return null;
            }
            SbSpatialAuthoring authoring = anchor.GetComponent<SbSpatialAuthoring>();
            if (authoring == null) authoring = anchor.AddComponent<SbSpatialAuthoring>();
            authoring.objectId = SeatObjectId;
            authoring.regionId = SeatRegionId;
            authoring.footprint = null;
            authoring.footprintPolygon = footprint;
            authoring.declaredExtent = extent;
            authoring.fallbackMarginMeters = StandOffMeters;
            authoring.frameEulerAngles = MrukFrameEulerAngles;
            authoring.includeChildAffordances = false;
            authoring.correspondenceKey = seatKey.Trim();
            authoring.affordances = new[]
            {
                new AuthoredAffordanceSurface
                {
                    id = SeatAffordanceId,
                    tag = SittableTag,
                    supportId = SeatSupportId,
                    surfacePolygon = surface,
                    subRegions = new[] { seat },
                },
            };
            return authoring;
        }

        /// <summary>
        /// The seat's footprint, extent, plane surface and seat part, from the anchor's volume box, its plane outline and its
        /// confirmed front. False when the outline is missing, degenerate or not finite.
        /// </summary>
        public static bool TryBuildSeatGeometry(Vector3 localCenter, Vector3 localSize, Vector2[] planeOutline,
                                                float? frontDegrees, out Vector2[] footprint, out Vector2[] extent,
                                                out Vector3[] surface, out AuthoredSubRegion seat)
        {
            footprint = null;
            extent = null;
            surface = null;
            seat = null;
            if (planeOutline == null || planeOutline.Length < 3 || !IsFinite(localCenter) || !IsFinite(localSize)) return false;
            if (frontDegrees.HasValue && (float.IsNaN(frontDegrees.Value) || float.IsInfinity(frontDegrees.Value))) return false;
            // The plane outline is an MRUK anchor-local XY polygon. The anchor frame point (x, y, 0) is the authoring frame
            // point (x, 0, -y); that mirrors the polygon, so the order is reversed to keep the orientation of every other
            // upward surface. Exactly the basis change SceneGeometryChart.PlanePolygon registers the observed plane with.
            Vector2[] chart = SceneGeometryChart.PlanePolygon(outline: planeOutline);
            double twiceArea = 0d;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            surface = new Vector3[chart.Length];
            for (int i = 0; i < chart.Length; ++i)
            {
                Vector2 p = chart[i];
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y)) return false;
                Vector2 q = chart[(i + 1) % chart.Length];
                twiceArea += (double)p.x * q.y - (double)q.x * p.y;
                surface[i] = new Vector3(p.x, 0f, p.y);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
            }
            if (Math.Abs(twiceArea) < 1e-6) return false;
            // The footprint is the anchor's volume footprint; a plane that sticks out of it (or a plane-only anchor) widens it,
            // because the surface must stand inside the object it belongs to.
            if (TryBuildGeometry(localCenter, localSize, out Vector2[] volumeFootprint, out _, out _))
            {
                foreach (Vector2 p in volumeFootprint)
                {
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
                }
            }
            footprint = Rectangle(minX, minZ, maxX, maxZ);
            extent = Rectangle(minX - StandOffMeters, minZ - StandOffMeters, maxX + StandOffMeters, maxZ + StandOffMeters);

            float rotation = frontDegrees.HasValue ? SeatPartRotationDegrees(frontDegrees.Value) : 0f;
            seat = new AuthoredSubRegion
            {
                name = SeatPartName,
                tag = SeatPartTag,
                polygon = RotatePolygon(chart, -rotation),
                rotationDegrees = rotation,
            };
            return true;
        }

        /// <summary>
        /// The rotation of the seat part that makes its +x the seat's front. The front is a direction in the anchor's local plane
        /// at <paramref name="frontDegrees"/> from local +x; the part lives in the registered plane polygon's coordinates, which
        /// are (x, -y) of that plane (<see cref="SceneGeometryChart.PlanePolygon"/>), so the same direction is at the negated
        /// angle there. The part's +x in the world is checked independently in SeatRegistrationTests (the core's element frame times the transform16 rotation). The pelvis yaw the core proposes against the turned part is not checked in this phase: it needs a sitting sender, which the seated layer's native fixture (phase U2) provides.
        /// </summary>
        public static float SeatPartRotationDegrees(float frontDegrees) => -frontDegrees;

        /// <summary>
        /// The front of the seat as an anchor-local plane angle, from a world point the user aimed at on the seat. The front is
        /// the direction from the seat's area centroid toward that point in the anchor's plane. False when the point is
        /// (numerically) over the centre.
        /// </summary>
        public static bool TryFrontFromWorldPoint(Matrix4x4 anchorLocalToWorld, Vector2[] planeOutline, Vector3 worldPoint,
                                                  out float frontDegrees)
        {
            frontDegrees = 0f;
            if (planeOutline == null || planeOutline.Length < 3) return false;
            Vector2 centre = PolygonCentroid(planeOutline);
            Vector3 local = anchorLocalToWorld.inverse.MultiplyPoint3x4(worldPoint);
            Vector2 aim = new Vector2(local.x, local.y) - centre;
            if (aim.sqrMagnitude < 1e-8f) return false;
            frontDegrees = Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>The area centroid of a simple polygon: the origin the core's radial chart takes for a convex seat.</summary>
        public static Vector2 PolygonCentroid(Vector2[] polygon)
        {
            double twiceArea = 0d, cx = 0d, cy = 0d;
            for (int i = 0; i < polygon.Length; ++i)
            {
                Vector2 p = polygon[i], q = polygon[(i + 1) % polygon.Length];
                double cross = (double)p.x * q.y - (double)q.x * p.y;
                twiceArea += cross;
                cx += (p.x + q.x) * cross;
                cy += (p.y + q.y) * cross;
            }
            if (Math.Abs(twiceArea) < 1e-12) return polygon[0];
            return new Vector2((float)(cx / (3d * twiceArea)), (float)(cy / (3d * twiceArea)));
        }

        private static Vector2[] RotatePolygon(Vector2[] polygon, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            var rotated = new Vector2[polygon.Length];
            for (int i = 0; i < polygon.Length; ++i)
            {
                rotated[i] = new Vector2(c * polygon[i].x - s * polygon[i].y, s * polygon[i].x + c * polygon[i].y);
            }
            return rotated;
        }

        /// <summary>
        /// The footprint and the tabletop in the authoring frame (+Y up), and the extent: the footprint
        /// plus <see cref="StandOffMeters"/> on every side.
        /// </summary>
        public static bool TryBuildGeometry(Vector3 localCenter, Vector3 localSize,
                                            out Vector2[] footprint, out Vector2[] extent, out Vector3[] tabletop)
        {
            footprint = null;
            extent = null;
            tabletop = null;
            if (!IsFinite(localCenter) || !IsFinite(localSize)) return false;
            float halfX = Mathf.Abs(localSize.x) * 0.5f;
            float halfY = Mathf.Abs(localSize.y) * 0.5f;
            if (halfX < 1e-3f || halfY < 1e-3f) return false;

            // Anchor-local (x, y, z) is the frame point (x, z, -y).
            float x0 = localCenter.x - halfX;
            float x1 = localCenter.x + halfX;
            float z0 = -localCenter.y - halfY;
            float z1 = -localCenter.y + halfY;
            float top = localCenter.z + Mathf.Abs(localSize.z) * 0.5f;

            footprint = Rectangle(x0, z0, x1, z1);
            extent = Rectangle(x0 - StandOffMeters, z0 - StandOffMeters, x1 + StandOffMeters, z1 + StandOffMeters);
            // Same vertex order as every other upward surface: the normal points up.
            tabletop = new[]
            {
                new Vector3(x0, top, z0), new Vector3(x1, top, z0),
                new Vector3(x1, top, z1), new Vector3(x0, top, z1),
            };
            return true;
        }

        private static Vector2[] Rectangle(float x0, float z0, float x1, float z1) => new[]
        {
            new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1),
        };

        private static bool IsFinite(Vector3 value) =>
            !(float.IsNaN(value.x) || float.IsInfinity(value.x) || float.IsNaN(value.y) ||
              float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z));
    }
}
