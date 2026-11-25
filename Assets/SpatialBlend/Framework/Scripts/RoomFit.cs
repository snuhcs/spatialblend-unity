// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// The app side of the one validity rule (<see cref="RegionPull"/>): what a virtual object's region is
    /// checked against when the user drops it and when it first spawns. Validity is computed once, never polled and never
    /// during a grab, and nothing is refused: the region is pulled to the nearest valid spot.
    /// <list type="bullet">
    /// <item>In a session (the room is registered and the object is attached) the obstacles are the core tree's: the room's
    /// walls, every piece of furniture and every sibling region (<see cref="SpatialBlendManager.RegionObstacles"/>), the same the
    /// manager pulls against when it shares the object.</item>
    /// <item>Before (the lobby, a first spawn) they are the room's walls and furniture (<see cref="RoomRegion"/>) and the
    /// regions of the other virtual objects in the scene.</item>
    /// </list>
    /// </summary>
    public static class RoomFit
    {
        /// <summary>The authored region of <paramref name="root"/> on the ground (world x, z) at its current pose; false without one.</summary>
        public static bool TryRegion(GameObject root, out Vector2[] ring) =>
            TryShape(root, SpatialHierarchyNodeKind.Region, out ring);

        /// <summary>The object's own footprint on the ground (world x, z) at its current pose; false without one.</summary>
        public static bool TryFootprint(GameObject root, out Vector2[] ring) =>
            TryShape(root, SpatialHierarchyNodeKind.Object, out ring);

        private static bool TryShape(GameObject root, SpatialHierarchyNodeKind kind, out Vector2[] ring)
        {
            ring = null;
            if (root == null) return false;
            SbSpatialAuthoring authoring = root.GetComponent<SbSpatialAuthoring>();
            if (authoring == null) authoring = root.GetComponentInChildren<SbSpatialAuthoring>();
            if (authoring == null || !authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out _) ||
                !SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out _))
                return false;
            Transform frame = authoring.transform;
            ring = SpatialBlendManager.WorldGroundRing(plan, frame.localToWorldMatrix * plan.FrameToObject, kind);
            return ring != null && ring.Length >= 3;
        }

        // The ground rectangle of the object's colliders' world bounds, counter-clockwise.
        private static bool TryColliderRing(GameObject root, out Vector2[] ring)
        {
            ring = null;
            Collider[] colliders = root != null ? root.GetComponentsInChildren<Collider>() : null;
            if (colliders == null || colliders.Length == 0) return false;
            Bounds b = colliders[0].bounds;
            for (int i = 1; i < colliders.Length; ++i) if (!colliders[i].isTrigger) b.Encapsulate(colliders[i].bounds);
            if (b.size.x <= 0f || b.size.z <= 0f) return false;
            ring = new[] { new Vector2(b.min.x, b.min.z), new Vector2(b.max.x, b.min.z), new Vector2(b.max.x, b.max.z), new Vector2(b.min.x, b.max.z) };
            return true;
        }

        /// <summary>
        /// What <paramref name="self"/>'s region must stay clear of: in a session (a manager with a room) the core tree's (every
        /// piece of furniture and every sibling region, <see cref="SpatialBlendManager.RegionObstacles"/>), also for an object not
        /// attached yet, so a placement and the share-time check see the same obstacles; otherwise
        /// <paramref name="room"/>'s furniture. Plus the regions of <paramref name="others"/> (never <paramref name="self"/>, its
        /// parents or its children) and <paramref name="extra"/> (something arriving that is not registered yet). Furniture is an
        /// obstacle for a floor object everywhere (Chess is never on a table).
        /// </summary>
        public static List<Vector2[]> Obstacles(RoomRegion room, SpatialBlendManager manager, GameObject self,
                                                IEnumerable<GameObject> others, IReadOnlyList<Vector2[]> extra = null)
        {
            SbEntity entity = self != null ? self.GetComponentInParent<SbEntity>() : null;
            List<Vector2[]> obstacles = InSession(manager)
                ? manager.RegionObstacles(entity != null && entity.IsAttached() ? entity.ID : 0)
                : room != null ? room.RegionObstacles() : new List<Vector2[]>();
            if (others != null)
                foreach (GameObject other in others)
                {
                    if (other == null || self != null &&
                        (other == self || other.transform.IsChildOf(self.transform) || self.transform.IsChildOf(other.transform)))
                        continue;
                    if (TryRegion(other, out Vector2[] ring)) obstacles.Add(ring);
                }
            if (extra != null)
                foreach (Vector2[] e in extra) if (e != null && e.Length >= 3) obstacles.Add(e);
            return obstacles;
        }

        private static bool InSession(SpatialBlendManager manager) =>
            manager != null && manager.PlacementWalkable != null && manager.PlacementWalkable.HasBoundary;

        /// <summary>The walls the object's footprint stays clear of: the registered room's in a session, else the room's.</summary>
        public static IReadOnlyList<Vector2[]> Walls(RoomRegion room, SpatialBlendManager manager)
        {
            if (InSession(manager)) return manager.RoomWalls;
            return room != null ? room.Walls : new Vector2[0][];
        }

        /// <summary>
        /// The nearest translation that makes <paramref name="root"/>'s placement valid, its shapes moved by <paramref name="offset"/>
        /// first (a candidate pose). False (zero) when it has no region, no room is known, or no spot fits.
        /// </summary>
        public static bool TryPull(GameObject root, Vector2 offset, RoomRegion room, SpatialBlendManager manager,
                                   IEnumerable<GameObject> others, float margin, out Vector2 shift)
        {
            shift = Vector2.zero;
            Vector2[] ring = Ring(room, manager);
            if (ring == null) return false;
            return PullFrom(root, offset, ring, () => Obstacles(room, manager, root, others), Walls(room, manager),
                Mathf.Max(margin, SpatialBlendManager.RegionFitMarginMeters), true, out shift);
        }

        private static bool PullFrom(GameObject root, Vector2 offset, Vector2[] ring, Func<List<Vector2[]>> obstacles,
                                     IReadOnlyList<Vector2[]> walls, float m, bool fineFallback, out Vector2 shift)
        {
            shift = Vector2.zero;
            // An object with no authored region yet (a received box whose declaration is written later) is placed by its collider.
            if (!TryRegion(root, out Vector2[] region) && !TryColliderRing(root, out region)) return false;
            if (!TryFootprint(root, out Vector2[] footprint)) footprint = (Vector2[])region.Clone();
            for (int i = 0; i < region.Length; ++i) region[i] += offset;
            if (footprint != null) for (int i = 0; i < footprint.Length; ++i) footprint[i] += offset;
            var shape = new RegionPull.Shape
            {
                Region = region, RegionObstacles = obstacles(),
                Footprint = footprint, FootprintObstacles = walls,
            };
            if (offset == Vector2.zero && RegionPull.IsValid(shape, Vector2.zero, ring, m))
                return true;
            // The way back to where it stands bounds the search (a snug spot the grid would miss is kept).
            return RegionPull.TryPull(shape, ring, m, out shift, offset != Vector2.zero ? -offset : (Vector2?)null, fineFallback);
        }

        /// <summary>True when <paramref name="root"/>'s placement is valid where it stands (the rule <see cref="TryPull"/> pulls to).</summary>
        public static bool IsValid(GameObject root, RoomRegion room, SpatialBlendManager manager, IEnumerable<GameObject> others, float margin)
        {
            if (!TryRegion(root, out Vector2[] region)) return false;
            Vector2[] ring = Ring(room, manager);
            if (ring == null) return false;
            TryFootprint(root, out Vector2[] footprint);
            var shape = new RegionPull.Shape
            {
                Region = region, RegionObstacles = Obstacles(room, manager, root, others),
                Footprint = footprint, FootprintObstacles = Walls(room, manager),
            };
            return RegionPull.IsValid(shape, Vector2.zero, ring, margin);
        }

        /// <summary>
        /// One object's placement inputs, computed once: the ring, the obstacles (the furniture and the other objects' regions), the
        /// walls, the object's compiled authoring (so a shape at a pose is a matrix product, not a compile) and the spaces the shape is
        /// checked in. A drop preview (<see cref="Interactions.PlacementMotif"/>) prepares them when a grab begins (and again each second)
        /// and reuses them at each of its ticks.
        /// </summary>
        public sealed class Prepared
        {
            internal Placement Placement;
            internal RoomRegion WallRoom;
            internal float Margin;
            internal RegionPull.Spaces Spaces;
            // Nothing can fit (a global answer): not searched again while these inputs last.
            internal bool NoSpot;
            public GameObject Root => Placement?.Root;
        }

        /// <summary>The inputs of <paramref name="root"/>'s placements now; null when it has no authored region or no room is known.</summary>
        public static Prepared Prepare(GameObject root, RoomRegion room, SpatialBlendManager manager, IEnumerable<GameObject> others, float margin)
        {
            if (root == null) return null;
            SbSpatialAuthoring authoring = root.GetComponent<SbSpatialAuthoring>();
            if (authoring == null) authoring = root.GetComponentInChildren<SbSpatialAuthoring>();
            if (authoring == null || !authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out _) ||
                !SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out _))
                return null;
            Placement placement = Placement.Of(root, room, manager, others, margin, null, null, plan, authoring.transform);
            if (placement == null) return null;
            Transform t = root.transform;
            RegionPull.Shape here = placement.ShapeAt(new Pose(t.position, t.rotation));
            return new Prepared
            {
                // Never under the manager's fit margin (what a session's share-time check and the release re-check use).
                Placement = placement, WallRoom = room ?? manager?.RegisteredRoom, Margin = Mathf.Max(margin, SpatialBlendManager.RegionFitMarginMeters),
                Spaces = RegionPull.BuildSpaces(here, placement.Ring),
            };
        }

        /// <summary>As <see cref="IsValid(GameObject, RoomRegion, SpatialBlendManager, IEnumerable{GameObject}, float)"/>, from prepared inputs (a drop preview).</summary>
        public static bool IsValid(Prepared prepared)
        {
            if (prepared?.Root == null) return false;
            Transform t = prepared.Root.transform;
            return RegionPull.IsValid(prepared.Placement.ShapeAt(new Pose(t.position, t.rotation)), Vector2.zero, prepared.Spaces, prepared.Margin);
        }

        /// <summary>How far a drop preview's search looks (metres): a drop that must move farther is shown as approximate.</summary>
        public const float PreviewReachMeters = 0.5f;

        /// <summary>What a drop preview's bounded search says of its answer.</summary>
        public enum PreviewFit
        {
            /// <summary>The drop's own answer: valid where it is, or the wall slide's spot (the same nearest-first search, within the reach).</summary>
            Exact,
            /// <summary>Pulled to the coarse spot (within <see cref="RegionPull.CoarseStep"/> of the drop's, which refines it).</summary>
            Rough,
            /// <summary>No spot within <see cref="PreviewReachMeters"/>; the drop searches farther (and turns).</summary>
            Unknown,
            /// <summary>Nothing can fit: the region is larger than the room in every orientation the drop tries.</summary>
            NoSpot,
        }

        /// <summary>
        /// A drop preview of <see cref="Place(GameObject, RoomRegion, SpatialBlendManager, IEnumerable{GameObject}, float, IReadOnlyList{Vector2[]}, Vector2[], out string, SpatialAuthoringPlan, Transform, Vector2?)"/>
        /// within a budget: valid where it stands; the wall slide within <see cref="PreviewReachMeters"/>; the pull
        /// unturned on the coarse grid within the same reach. No turns, no 5 cm grid, no refinement. The root is left at the spot found
        /// (the caller puts it back).
        /// </summary>
        public static PreviewFit PreviewPlace(Prepared prepared, Vector2? knownValid)
        {
            if (prepared?.Root == null) return PreviewFit.NoSpot;
            GameObject root = prepared.Root;
            Placement placement = prepared.Placement;
            var start = new Pose(root.transform.position, root.transform.rotation);
            RegionPull.Shape shape = placement.ShapeAt(start);
            if (RegionPull.IsValid(shape, Vector2.zero, prepared.Spaces, placement.Margin)) return PreviewFit.Exact;
            Interactions.PlacementMotif motif = root.GetComponentInChildren<Interactions.PlacementMotif>(true);
            if (TryWall(root, motif, new Vector2(start.position.x, start.position.z), prepared.WallRoom, placement, prepared.Spaces, PreviewReachMeters))
                return PreviewFit.Exact;
            root.transform.SetPositionAndRotation(start.position, start.rotation);
            return PreviewPullFrom(prepared, shape, start, knownValid);
        }

        /// <summary>A preview of <see cref="TryPull(GameObject, Vector2, RoomRegion, SpatialBlendManager, IEnumerable{GameObject}, float, out Vector2)"/> within the same budget. The root is left at the spot found.</summary>
        public static PreviewFit PreviewPull(Prepared prepared, Vector2 offset)
        {
            if (prepared?.Root == null) return PreviewFit.NoSpot;
            Transform t = prepared.Root.transform;
            var start = new Pose(t.position + new Vector3(offset.x, 0f, offset.y), t.rotation);
            RegionPull.Shape shape = prepared.Placement.ShapeAt(start);
            // As the drop's pull: an object with no footprint is held off the walls by its region.
            if (shape.Footprint == null && shape.Region != null) shape.Footprint = (IReadOnlyList<Vector2>)shape.Region;
            if (RegionPull.IsValid(shape, Vector2.zero, prepared.Spaces, prepared.Placement.Margin)) return PreviewFit.Exact;
            return PreviewPullFrom(prepared, shape, start, offset != Vector2.zero ? -offset : (Vector2?)null);
        }

        private static PreviewFit PreviewPullFrom(Prepared prepared, RegionPull.Shape shape, Pose start, Vector2? knownValid)
        {
            Transform t = prepared.Root.transform;
            if (prepared.NoSpot) { t.SetPositionAndRotation(start.position, start.rotation); return PreviewFit.NoSpot; }
            if (RegionPull.TryPullCoarse(shape, prepared.Spaces, prepared.Placement.Margin, knownValid, PreviewReachMeters, out Vector2 shift))
            {
                t.SetPositionAndRotation(start.position + new Vector3(shift.x, 0f, shift.y), start.rotation);
                return shift == Vector2.zero ? PreviewFit.Exact : PreviewFit.Rough;
            }
            t.SetPositionAndRotation(start.position, start.rotation);
            if (shape.Region != null && FitsNowhere(shape.Region, prepared.Placement.Ring, prepared.Placement.Margin))
            {
                prepared.NoSpot = true;
                return PreviewFit.NoSpot;
            }
            return PreviewFit.Unknown;
        }

        /// <summary>A preview of the wall drop (<see cref="SbApp.TryPlaceOnWall"/>): valid where it stands, else the wall slide within the reach.</summary>
        public static bool PreviewOnWall(Prepared prepared, Interactions.PlacementMotif motif)
        {
            if (prepared?.Root == null) return false;
            if (IsValid(prepared)) return true;
            Vector3 at = prepared.Root.transform.position;
            return TryWall(prepared.Root, motif, new Vector2(at.x, at.z), prepared.WallRoom, prepared.Placement, prepared.Spaces, PreviewReachMeters);
        }

        // True when the region's bounding box is wider than the ring's (less the margin both sides) in every orientation a drop tries
        // (the quarter and the eighth turns): then no translation or turn fits, so the preview may say so.
        private static bool FitsNowhere(IReadOnlyList<Vector2> region, IReadOnlyList<Vector2> ring, float margin)
        {
            Vector2 rMin = ring[0], rMax = ring[0];
            foreach (Vector2 v in ring) { rMin = Vector2.Min(rMin, v); rMax = Vector2.Max(rMax, v); }
            Vector2 room = rMax - rMin - new Vector2(2f * margin, 2f * margin);
            Vector2 c = Vector2.zero;
            foreach (Vector2 v in region) c += v;
            c /= region.Count;
            foreach (float yaw in new[] { 0f, 45f, 90f, 135f })
            {
                float cs = Mathf.Cos(yaw * Mathf.Deg2Rad), sn = Mathf.Sin(yaw * Mathf.Deg2Rad);
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = -min;
                foreach (Vector2 v in region)
                {
                    Vector2 d = v - c;
                    var q = new Vector2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);
                    min = Vector2.Min(min, q); max = Vector2.Max(max, q);
                }
                Vector2 size = max - min;
                if (size.x <= room.x && size.y <= room.y) return false;
            }
            return true;
        }

        /// <summary>The distance within which a wall-mounted object's footprint counts as on a wall (<see cref="IsOnWall"/>).</summary>
        public const float OnWallMeters = 0.12f;

        /// <summary>
        /// Whether wall-mounted <paramref name="root"/> is on a wall where it stands: its footprint within <see cref="OnWallMeters"/> of a
        /// wall of <paramref name="room"/>, facing as the wall pose faces (into the room). A valid pose mid-room is not on a wall.
        /// </summary>
        public static bool IsOnWall(GameObject root, Interactions.PlacementMotif motif, RoomRegion room)
        {
            if (root == null || motif == null || room == null || room.WallEdges.Length == 0) return false;
            Vector3 fwd = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-6f) return false;
            Vector2[] foot = TryFootprint(root, out Vector2[] ring) ? ring : new[] { new Vector2(root.transform.position.x, root.transform.position.z) };
            foreach ((Vector2 a, Vector2 b, Vector2 inward) e in room.WallEdges)
            {
                Pose? wall = motif.WallPose(new Vector3(e.a.x, 0f, e.a.y), new Vector3(e.inward.x, 0f, e.inward.y));
                if (!wall.HasValue) return false;
                if (Vector3.Angle(fwd, Vector3.ProjectOnPlane(wall.Value.rotation * Vector3.forward, Vector3.up)) > 15f) continue;
                foreach (Vector2 v in foot)
                    if (DistanceToSegment(v, e.a, e.b) <= OnWallMeters) return true;
            }
            return false;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>
        /// Whether a drop on a wall could land on one: the drop's own wall slide (<see cref="TryPlaceOnWall"/>) from where the object
        /// stands finds a spot on any wall. The pose is left as it was.
        /// </summary>
        public static bool DropWallFits(GameObject root, Interactions.PlacementMotif motif, RoomRegion room, SpatialBlendManager manager,
                                        IEnumerable<GameObject> others, float margin)
        {
            if (root == null) return false;
            var start = new Pose(root.transform.position, root.transform.rotation);
            bool fits = TryPlaceOnWall(root, motif, new Vector2(start.position.x, start.position.z), room, manager, others, margin);
            root.transform.SetPositionAndRotation(start.position, start.rotation);
            return fits;
        }

        /// <summary>The ring a region is pulled into: the registered one in a session, else the room's.</summary>
        public static Vector2[] Ring(RoomRegion room, SpatialBlendManager manager)
        {
            if (InSession(manager)) return manager.PlacementWalkable.Boundary;
            return room != null && room.IsValid ? room.Ring : null;
        }

        /// <summary>
        /// The first spawn of a virtual object: <paramref name="root"/> is put at <paramref name="pose"/> (in front of
        /// the user, PlacementMotif.SpawnPose) and pulled to the nearest valid spot.
        /// </summary>
        public static bool PlaceAt(GameObject root, Pose pose, RoomRegion room, SpatialBlendManager manager,
                                   IEnumerable<GameObject> others, float margin, out Vector2 shift)
        {
            shift = Vector2.zero;
            if (root == null) return false;
            root.transform.SetPositionAndRotation(pose.position, pose.rotation);
            return PullInPlace(root, room, manager, others, margin, out shift);
        }

        /// <summary>
        /// First spawn of several objects: the wall-mounted ones first (a wall has few spots), then the largest
        /// region first (ties by name, then by authored object id:
        /// a stable order whatever order the scene lists them in), each clear of the ones placed before it
        /// (<see cref="PlaceForSpawn"/>). A physical object (an own scan) is never moved. Returns the objects that found no spot
        /// anywhere (they stay at their spawn pose).
        /// </summary>
        public static List<GameObject> SpawnAll(IEnumerable<(GameObject root, Interactions.PlacementMotif motif)> objects,
                                                Vector3 eye, Vector3 forward, RoomRegion room, SpatialBlendManager manager,
                                                IEnumerable<GameObject> others, float margin) =>
            SpawnAll(objects, eye, forward, room, manager, others, margin, null);

        /// <summary>
        /// As <see cref="SpawnAll(IEnumerable{ValueTuple{GameObject, Interactions.PlacementMotif}}, Vector3, Vector3, RoomRegion, SpatialBlendManager, IEnumerable{GameObject}, float)"/>,
        /// with the chosen pose's reason reported to <paramref name="reasons"/> (the initial position must be good,
        /// so nothing ever needs moving later).
        /// </summary>
        public static List<GameObject> SpawnAll(IEnumerable<(GameObject root, Interactions.PlacementMotif motif)> objects,
                                                Vector3 eye, Vector3 forward, RoomRegion room, SpatialBlendManager manager,
                                                IEnumerable<GameObject> others, float margin, Dictionary<GameObject, string> reasons)
        {
            var list = new List<(GameObject root, Interactions.PlacementMotif motif, float area, string id)>();
            foreach ((GameObject root, Interactions.PlacementMotif motif) o in objects)
            {
                if (o.root == null || o.motif == null || SpatialBlendManager.IsPhysicalObject(o.root)) continue;
                Pose spawn = o.motif.SpawnPose(eye, forward);
                o.root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                SbSpatialAuthoring authoring = o.root.GetComponentInChildren<SbSpatialAuthoring>();
                string id = authoring != null && authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out _)
                    ? declaration.objectId ?? "" : "";
                list.Add((o.root, o.motif, TryRegion(o.root, out Vector2[] r) ? Mathf.Abs(PolygonBoolean.SignedArea(r)) : 0f, id));
            }
            // Wall-mounted objects first (their spots are few: a stretch of wall), then the largest region first.
            list = list.OrderByDescending(o => o.motif.WallPose(Vector3.zero, Vector3.forward).HasValue).ThenByDescending(o => o.area)
                       .ThenBy(o => o.root.name, System.StringComparer.Ordinal)
                       .ThenBy(o => o.id, System.StringComparer.Ordinal).ToList();
            // A joint search, bounded: this order first; when something found no spot, each other object taken first in turn; the
            // order that places the most wins (the first such, so the result is stable).
            var orders = new List<List<(GameObject root, Interactions.PlacementMotif motif, float area, string id)>> { list };
            for (int i = 1; i < list.Count; ++i)
            {
                var order = new List<(GameObject root, Interactions.PlacementMotif motif, float area, string id)> { list[i] };
                order.AddRange(list.Where((o, k) => k != i));
                orders.Add(order);
            }
            List<GameObject> bestFailed = null;
            List<Pose> bestPoses = null;
            foreach (var order in orders)
            {
                var placed = new List<GameObject>(others ?? new GameObject[0]);
                var failed = new List<GameObject>();
                foreach (var o in order)
                {
                    if (!InitialPlace(o.root, o.motif, eye, forward, room, manager, placed, margin, out string why))
                        failed.Add(o.root);
                    else if (reasons != null) reasons[o.root] = why;
                    placed.Add(o.root);
                }
                if (bestFailed == null || failed.Count < bestFailed.Count)
                {
                    bestFailed = failed;
                    bestPoses = list.Select(o => new Pose(o.root.transform.position, o.root.transform.rotation)).ToList();
                }
                if (bestFailed.Count == 0) break;
            }
            if (bestPoses != null)
                for (int i = 0; i < list.Count; ++i) list[i].root.transform.SetPositionAndRotation(bestPoses[i].position, bestPoses[i].rotation);
            return bestFailed ?? new List<GameObject>();
        }

        /// <summary>
        /// One placement's fixed inputs, computed once: the ring, the obstacles and the walls,
        /// and the object's region and footprint relative to its root, so each candidate pose is a rigid transform of them
        /// instead of a new compile and a new obstacle sweep.
        /// </summary>
        internal sealed class Placement
        {
            public Vector2[] Ring;
            public List<Vector2[]> Obstacles;
            public IReadOnlyList<Vector2[]> Walls;
            public float Margin;

            public static Placement Of(GameObject root, RoomRegion room, SpatialBlendManager manager, IEnumerable<GameObject> others, float margin,
                                       IReadOnlyList<Vector2[]> extra = null, Vector2[] ringOverride = null,
                                       SpatialAuthoringPlan plan = null, Transform frame = null)
            {
                if (plan == null ? !TryRegion(root, out _)
                                 : SpatialBlendManager.WorldGroundRing(plan, (frame ?? root.transform).localToWorldMatrix * plan.FrameToObject,
                                       SpatialHierarchyNodeKind.Region) == null) return null;
                Vector2[] ring = ringOverride ?? Ring(room, manager);
                if (ring == null) return null;
                return new Placement
                {
                    Ring = ring, Obstacles = Obstacles(room, manager, root, others, extra), Walls = Walls(room, manager),
                    Root = root, Plan = plan, Frame = frame ?? root.transform,
                    Margin = Mathf.Max(margin, SpatialBlendManager.RegionFitMarginMeters),
                };
            }

            // The shapes at a candidate pose, compiled from the object itself there (0.05 ms: the measured cost of a compile), so
            // they are exactly the ones the manager will register; a rigid 2D transform of cached ones missed a root that is not
            // only turned about the vertical (share-time moves).
            public GameObject Root;
            // The compiled plan the manager holds for it, when there is one (its authoring frame `Frame` moves with Root).
            public SpatialAuthoringPlan Plan;
            public Transform Frame;

            public RegionPull.Shape ShapeAt(Pose pose)
            {
                Root.transform.SetPositionAndRotation(pose.position, pose.rotation);
                Vector2[] region, footprint;
                if (Plan != null)
                {
                    Matrix4x4 world = Frame.localToWorldMatrix * Plan.FrameToObject;
                    region = SpatialBlendManager.WorldGroundRing(Plan, world, SpatialHierarchyNodeKind.Region);
                    footprint = SpatialBlendManager.WorldGroundRing(Plan, world, SpatialHierarchyNodeKind.Object);
                }
                else
                {
                    TryRegion(Root, out region);
                    TryFootprint(Root, out footprint);
                }
                return new RegionPull.Shape { Region = region, RegionObstacles = Obstacles, Footprint = footprint, FootprintObstacles = Walls };
            }
        }

        /// <summary>
        /// One object's initial pose, by the one rule: a wall-mounted object (the Whiteboard) on the wall stretch
        /// where it is valid for its whole footprint and standing region, the stretch nearest the user's view direction first, then
        /// the longest, centred on it at its hover height; when no wall fits, standing on the open floor in front of the user (the v1
        /// panel fallback). Any other object (Chess: always on the floor) in front of the user at its hover height, pulled to the
        /// nearest valid open spot. Deterministic for a room and a view.
        /// </summary>
        public static bool InitialPlace(GameObject root, Interactions.PlacementMotif motif, Vector3 eye, Vector3 forward,
                                        RoomRegion room, SpatialBlendManager manager, IEnumerable<GameObject> others, float margin,
                                        out string reason)
        {
            reason = null;
            if (root == null || motif == null) return false;
            Pose spawn = motif.SpawnPose(eye, forward);
            root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            Placement placement = Placement.Of(root, room, manager, others, margin);
            if (placement == null) return false;
            if (TryBestWall(root, motif, eye, forward, room ?? manager?.RegisteredRoom, placement, out reason)) return true;
            bool wallMounted = motif.WallPose(Vector3.zero, Vector3.forward).HasValue;
            root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            if (KnownUnfit(root, placement, spawn.rotation)) return false;
            if (!PullOrTurn(root, placement, spawn, true, out string how))
            {
                root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                RememberUnfit(root, placement, spawn.rotation);
                return false;
            }
            reason = (wallMounted ? "standing on the open floor (no wall fits), " : "") + "in front of the user" + how;
            return true;
        }

        // The wall stretch for a wall-mounted object: every valid run of spots along every wall; the run nearest the user's view
        // direction first (the smallest angle between the head's forward and the run's middle), then the longest, then the lower wall
        // index. The object is centred on the run when that spot is valid, else at the run's valid spot nearest its middle.
        private static bool TryBestWall(GameObject root, Interactions.PlacementMotif motif, Vector3 eye, Vector3 forward, RoomRegion room,
                                        Placement placement, out string reason)
        {
            reason = null;
            if (room == null || room.WallEdges.Length == 0 || motif == null || !motif.WallPose(Vector3.zero, Vector3.forward).HasValue)
                return false;
            var start = new Pose(root.transform.position, root.transform.rotation);
            var eye2 = new Vector2(eye.x, eye.z);
            Vector2 look = new Vector2(forward.x, forward.z).sqrMagnitude > 1e-6f ? new Vector2(forward.x, forward.z).normalized : Vector2.up;
            (float angle, float length, int wall, Pose pose)? best = null;
            Pose At((Vector2 a, Vector2 b, Vector2 inward) e, Vector2 q) =>
                motif.WallPose(new Vector3(q.x, 0f, q.y), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
            for (int w = 0; w < room.WallEdges.Length; ++w)
            {
                (Vector2 a, Vector2 b, Vector2 inward) e = room.WallEdges[w];
                float length = Vector2.Distance(e.a, e.b);
                if (length < 1e-4f) continue;
                Vector2 d = (e.b - e.a) / length;
                var run = new List<float>();
                for (float t = 0f; t <= length + WallSearchStepMeters * 0.5f; t += WallSearchStepMeters)
                {
                    bool ok = t <= length &&
                              RegionPull.IsValid(placement.ShapeAt(At(e, e.a + d * t)), Vector2.zero, placement.Ring, placement.Margin + RegionPull.PullSlack);
                    if (ok) { run.Add(t); continue; }
                    if (run.Count == 0) continue;
                    float mid = (run[0] + run[run.Count - 1]) * 0.5f;
                    // The centre when valid there, else the run's valid sample nearest it.
                    float spot = RegionPull.IsValid(placement.ShapeAt(At(e, e.a + d * mid)), Vector2.zero, placement.Ring, placement.Margin + RegionPull.PullSlack)
                        ? mid : run.OrderBy(x => Mathf.Abs(x - mid)).First();
                    Vector2 q = e.a + d * spot;
                    Vector2 toward = q - eye2;
                    float angle = toward.sqrMagnitude > 1e-6f ? Vector2.Angle(look, toward) : 0f;
                    float lengthRun = run[run.Count - 1] - run[0] + WallSearchStepMeters;
                    if (best == null || angle < best.Value.angle - 1f ||
                        Mathf.Abs(angle - best.Value.angle) <= 1f && lengthRun > best.Value.length + 1e-4f)
                        best = (angle, lengthRun, w, At(e, q));
                    run.Clear();
                }
            }
            if (best == null)
            {
                root.transform.SetPositionAndRotation(start.position, start.rotation);
                return false;
            }
            root.transform.SetPositionAndRotation(best.Value.pose.position, best.Value.pose.rotation);
            reason = $"wall {best.Value.wall}, free stretch {best.Value.length:0.#} m, {best.Value.angle:0} degrees from the view";
            return true;
        }

        /// <summary>
        /// THE placement from where an object stands (every path calls this; spawn, drop, share-time fit,
        /// received-copy arrival, furniture arriving, a room re-registered): valid where it is, it stays; a wall-mounted one slides
        /// along the walls to the nearest valid spot, then (no wall fits) stands on the open floor; any other one is pulled to the
        /// nearest valid spot, turned when no translation fits. <paramref name="extra"/>: obstacles not registered yet (the
        /// arriving furniture or object); <paramref name="ringOverride"/>: the room ring about to be registered. False when nothing
        /// fits: the object is back where it was.
        /// </summary>
        public static bool Place(GameObject root, RoomRegion room, SpatialBlendManager manager, IEnumerable<GameObject> others, float margin,
                                 IReadOnlyList<Vector2[]> extra, Vector2[] ringOverride, out string reason,
                                 SpatialAuthoringPlan plan = null, Transform frame = null, Vector2? knownValid = null)
        {
            reason = null;
            if (root == null) return false;
            Placement placement = Placement.Of(root, room, manager, others, margin, extra, ringOverride, plan, frame);
            if (placement == null) return false;
            return PlaceWith(root, placement, room ?? manager?.RegisteredRoom, out reason, knownValid);
        }

        private static bool PlaceWith(GameObject root, Placement placement, RoomRegion wallRoom, out string reason, Vector2? knownValid)
        {
            reason = null;
            var start = new Pose(root.transform.position, root.transform.rotation);
            if (RegionPull.IsValid(placement.ShapeAt(start), Vector2.zero, placement.Ring, placement.Margin))
            {
                reason = "valid where it stands";
                return true;
            }
            Interactions.PlacementMotif motif = root.GetComponentInChildren<Interactions.PlacementMotif>(true);
            if (TryWall(root, motif, new Vector2(start.position.x, start.position.z), wallRoom, placement))
            {
                reason = $"slid along the walls {Vector3.Distance(start.position, root.transform.position):0.##} m";
                return true;
            }
            root.transform.SetPositionAndRotation(start.position, start.rotation);
            // The same search with the same inputs already found nothing: not again (it is costly).
            if (KnownUnfit(root, placement, start.rotation)) return false;
            if (!PullOrTurn(root, placement, start, true, out string how, knownValid))
            {
                root.transform.SetPositionAndRotation(start.position, start.rotation);
                RememberUnfit(root, placement, start.rotation);
                return false;
            }
            reason = (motif != null && motif.WallPose(Vector3.zero, Vector3.forward).HasValue ? "standing on the open floor (no wall fits)" : "on the open floor") + how;
            return true;
        }

        // From `from`: the nearest valid spot unturned; else the quarter turns, then the eighth turns (the shortest pull within the
        // first group that fits). `how` says what moved it.
        private static bool PullOrTurn(GameObject root, Placement placement, Pose from, bool fineFirst, out string how, Vector2? knownValid = null)
        {
            how = null;
            foreach (float[] group in new[] { new[] { 0f }, new[] { 90f, 270f, 180f }, new[] { 45f, 315f, 135f, 225f } })
            {
                Pose? best = null;
                float bestShift = float.PositiveInfinity, bestYaw = 0f;
                foreach (float yaw in group)
                {
                    var pose = new Pose(from.position, Quaternion.Euler(0f, yaw, 0f) * from.rotation);
                    // The finer grid for the unturned attempt only (the cost: seven turns times a 5 cm grid).
                    if (!RegionPull.TryPull(placement.ShapeAt(pose), placement.Ring, placement.Margin, out Vector2 shift, yaw == 0f ? knownValid : null, fineFirst && yaw == 0f) ||
                        shift.magnitude >= bestShift) continue;
                    bestShift = shift.magnitude;
                    bestYaw = yaw;
                    best = new Pose(pose.position + new Vector3(shift.x, 0f, shift.y), pose.rotation);
                }
                if (best == null) continue;
                root.transform.SetPositionAndRotation(best.Value.position, best.Value.rotation);
                how = bestShift < 1e-3f && bestYaw == 0f ? "" : $", pulled {bestShift:0.##} m{(bestYaw != 0f ? $" and turned {bestYaw:0} degrees" : "")}";
                return true;
            }
            return false;
        }

        /// <summary>
        /// One object's first spawn: a wall-mounted one (<see cref="Interactions.PlacementMotif.WallPose"/>) takes the valid spot
        /// on any wall nearest its spawn pose (<see cref="TryPlaceOnWall"/>); any other one, or a wall-mounted one no wall fits, is
        /// pulled from in front of the user and, when unturned it fits nowhere, turned (the quarter turns, then the eighth turns: seven tries at most). False when
        /// nothing fits (the room is too small or too cluttered for it).
        /// </summary>
        public static bool PlaceForSpawn(GameObject root, Interactions.PlacementMotif motif, Vector3 eye, Vector3 forward,
                                         RoomRegion room, SpatialBlendManager manager, IEnumerable<GameObject> others, float margin)
        {
            if (root == null || motif == null) return false;
            Pose spawn = motif.SpawnPose(eye, forward);
            root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            Placement placement = Placement.Of(root, room, manager, others, margin);
            if (placement == null) return false;
            if (TryWall(root, motif, new Vector2(spawn.position.x, spawn.position.z), room, placement)) return true;
            root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            if (PullOrTurn(root, placement, spawn, true, out _)) return true;
            root.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            return false;
        }

        /// <summary>
        /// A wall-mounted object (<see cref="Interactions.PlacementMotif.WallPose"/>, the Whiteboard) put on the valid wall spot
        /// nearest <paramref name="near"/>: it slides along every wall segment, facing out of the wall, never into mid-room. Used at a
        /// first spawn and when it is dropped on a wall. False (unmoved) when it is not wall-mounted or no wall spot fits.
        /// </summary>
        /// <summary>Whether any wall has a valid spot for wall-mounted <paramref name="root"/> (its pose is left as it was).</summary>
        public static bool AnyWallFits(GameObject root, Interactions.PlacementMotif motif, RoomRegion room, SpatialBlendManager manager,
                                       IEnumerable<GameObject> others, float margin)
        {
            if (root == null) return false;
            var start = new Pose(root.transform.position, root.transform.rotation);
            Placement placement = Placement.Of(root, room, manager, others, margin);
            // The same wall search the initial placement makes (TryBestWall), so "a wall fits" means what InitialPlace finds.
            bool fits = placement != null &&
                        TryBestWall(root, motif, start.position, Vector3.forward, room ?? manager?.RegisteredRoom, placement, out _);
            root.transform.SetPositionAndRotation(start.position, start.rotation);
            return fits;
        }

        public static bool TryPlaceOnWall(GameObject root, Interactions.PlacementMotif motif, Vector2 near, RoomRegion room,
                                          SpatialBlendManager manager, IEnumerable<GameObject> others, float margin)
        {
            Placement placement = root != null ? Placement.Of(root, room, manager, others, margin) : null;
            return placement != null && TryWall(root, motif, near, room ?? manager?.RegisteredRoom, placement);
        }

        // spaces: prebuilt (a preview), else built per check. maxReach: no farther from `near` (a preview); the nearest spot within it is
        // the full slide's (every nearer sample is tried first).
        private static bool TryWall(GameObject root, Interactions.PlacementMotif motif, Vector2 near, RoomRegion room, Placement placement,
                                    RegionPull.Spaces spaces = null, float maxReach = float.PositiveInfinity)
        {
            if (room == null || room.WallEdges.Length == 0 || motif == null || !motif.WallPose(Vector3.zero, Vector3.forward).HasValue)
                return false;
            var start = new Pose(root.transform.position, root.transform.rotation);
            float best = maxReach;
            Pose bestPose = default;
            bool found = false;
            foreach ((Vector2 a, Vector2 b, Vector2 inward) e in room.WallEdges)
            {
                float length = Vector2.Distance(e.a, e.b);
                if (length < 1e-4f) continue;
                Vector2 d = (e.b - e.a) / length;
                // The nearest point of this wall first, then outward along it both ways: the first valid spot is this wall's best.
                float t0 = Mathf.Clamp(Vector2.Dot(near - e.a, d), 0f, length);
                for (int k = 0; k * WallSearchStepMeters <= length + WallSearchStepMeters; ++k)
                {
                    bool any = false;
                    foreach (float t in new[] { t0 + k * WallSearchStepMeters, t0 - k * WallSearchStepMeters })
                    {
                        if (t < 0f || t > length || k == 0 && t != t0 + 0f) continue;
                        any = true;
                        Vector2 q = e.a + d * t;
                        float dist = (q - near).magnitude;
                        if (dist >= best) continue;
                        Pose pose = motif.WallPose(new Vector3(q.x, 0f, q.y), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
                        RegionPull.Shape shape = placement.ShapeAt(pose);
                        if (!(spaces != null
                                ? RegionPull.IsValid(shape, Vector2.zero, spaces, placement.Margin + RegionPull.PullSlack)
                                : RegionPull.IsValid(shape, Vector2.zero, placement.Ring, placement.Margin + RegionPull.PullSlack))) continue;
                        best = dist;
                        bestPose = pose;
                        found = true;
                    }
                    if (!any || k * WallSearchStepMeters >= best) break;
                }
            }
            if (!found)
            {
                root.transform.SetPositionAndRotation(start.position, start.rotation);
                return false;
            }
            root.transform.SetPositionAndRotation(bestPose.position, bestPose.rotation);
            return true;
        }

        // Objects no spot was found for, keyed by what the search saw: the object, the ring, the obstacles and the
        // walls, each rounded to the centimetre. The same inputs give the same "fits nowhere", so the failing search is not repeated
        // (seconds on a headset); a changed room, a piece of furniture arriving or another object moving is a new key. Never cleared
        // blindly; bounded.
        private static readonly HashSet<(int, long)> s_unfit = new HashSet<(int, long)>();
        private const int UnfitCapacity = 512;

        private static long RingHash(IReadOnlyList<Vector2> ring)
        {
            if (ring == null) return 0;
            unchecked
            {
                long h = 1469598103934665603L ^ ring.Count;
                foreach (Vector2 v in ring)
                {
                    h = (h ^ Mathf.RoundToInt(v.x * 100f)) * 1099511628211L;
                    h = (h ^ Mathf.RoundToInt(v.y * 100f)) * 1099511628211L;
                }
                return h;
            }
        }

        // Order-independent over the set of rings (the obstacle lists are not built in a fixed order).
        private static long SetHash(IEnumerable<IReadOnlyList<Vector2>> rings)
        {
            if (rings == null) return 0;
            unchecked
            {
                long sum = 0, x = 0;
                int n = 0;
                foreach (IReadOnlyList<Vector2> r in rings) { long h = RingHash(r); sum += h; x ^= h * 31; ++n; }
                return sum * 1099511628211L ^ x ^ n;
            }
        }

        // The start yaw (the turns are tried relative to it, to the degree) and the margin (a drop's and the share-time check's differ)
        // are part of what the search saw.
        private static (int, long) UnfitKey(GameObject root, Placement placement, Quaternion startRotation)
        {
            unchecked
            {
                long h = RingHash(placement.Ring) * 1099511628211L ^ SetHash(placement.Obstacles);
                h = h * 1099511628211L ^ SetHash(placement.Walls);
                h = h * 1099511628211L ^ Mathf.RoundToInt(Mathf.Repeat(startRotation.eulerAngles.y, 360f));
                h = h * 1099511628211L ^ Mathf.RoundToInt(placement.Margin * 1000f);
                return (root.GetInstanceID(), h);
            }
        }

        private static bool KnownUnfit(GameObject root, Placement placement, Quaternion startRotation) =>
            root != null && placement != null && s_unfit.Contains(UnfitKey(root, placement, startRotation));

        private static void RememberUnfit(GameObject root, Placement placement, Quaternion startRotation)
        {
            if (root == null || placement == null) return;
            if (s_unfit.Count >= UnfitCapacity) s_unfit.Clear();
            s_unfit.Add(UnfitKey(root, placement, startRotation));
        }

        /// <summary>The step a wall-mounted object slides along a wall segment in a wall search.</summary>
        public const float WallSearchStepMeters = 0.1f;

        /// <summary>
        /// The longest stretch of each wall where a wall-mounted object's spot is valid, ignoring other virtual objects (why a room
        /// is too small for it: a measurement for the exclusion report). Metres, one per wall edge.
        /// </summary>
        public static float[] FreeWallLengths(GameObject root, Interactions.PlacementMotif motif, RoomRegion room, SpatialBlendManager manager, float margin)
        {
            Placement placement = Placement.Of(root, room, manager, null, margin);
            if (placement == null || room == null) return new float[0];
            var result = new float[room.WallEdges.Length];
            for (int w = 0; w < room.WallEdges.Length; ++w)
            {
                (Vector2 a, Vector2 b, Vector2 inward) e = room.WallEdges[w];
                float length = Vector2.Distance(e.a, e.b), run = 0f, longest = 0f;
                if (length < 1e-4f) continue;
                Vector2 d = (e.b - e.a) / length;
                for (float t = 0f; t <= length; t += WallSearchStepMeters)
                {
                    Vector2 q = e.a + d * t;
                    Pose pose = motif.WallPose(new Vector3(q.x, 0f, q.y), new Vector3(e.inward.x, 0f, e.inward.y)).Value;
                    run = RegionPull.IsValid(placement.ShapeAt(pose), Vector2.zero, placement.Ring, placement.Margin) ? run + WallSearchStepMeters : 0f;
                    longest = Mathf.Max(longest, run);
                }
                result[w] = longest;
            }
            return result;
        }

        /// <summary>Pulls <paramref name="root"/> where it stands (a drop). False when it has no region, no room is known or nothing fits.</summary>
        public static bool PullInPlace(GameObject root, RoomRegion room, SpatialBlendManager manager,
                                       IEnumerable<GameObject> others, float margin, out Vector2 shift)
        {
            if (!TryPull(root, Vector2.zero, room, manager, others, margin, out shift)) return false;
            root.transform.position += new Vector3(shift.x, 0f, shift.y);
            return true;
        }
    }
}
