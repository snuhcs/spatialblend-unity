// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Owned local MRUK geometry only; no element is shared.</summary>
    public partial class SpatialBlendManager : IMutableLocalSceneGeometrySink
    {
        private struct OwnedSceneGeometry
        {
            public long Parent;
            public long Plane;
            public long Space;
            public long Generation;
        }
        private readonly Dictionary<int, OwnedSceneGeometry> _ownedLocalSceneGeometry =
            new Dictionary<int, OwnedSceneGeometry>();
        private readonly Dictionary<int, OwnedSceneGeometry> _partialLocalSceneGeometry =
            new Dictionary<int, OwnedSceneGeometry>();
        private long _localSceneGeometryGeneration = long.MinValue;

        // The layout of everything observed in this space: contained footprints nest,
        // partial overlaps are clipped. See SceneFootprintLayout. It stages through StageSceneGeometryRaw.
        private SceneGeometryLayoutEngine _sceneLayout;
        private SceneGeometryLayoutEngine SceneLayout =>
            _sceneLayout ?? (_sceneLayout = new SceneGeometryLayoutEngine(StageSceneGeometryRaw, RemoveSceneGeometryRaw, ReparentContentRaw));

        public bool RegisterLocalSceneGeometry(SceneGeometryDescriptor descriptor)
        {
            long generation = ConnectionGeneration;
            ResetSceneGeometryGeneration(generation);
            if (!IsLocalGeometryCurrent(generation) || descriptor.SourceKey == 0) return false;
            if (_ownedLocalSceneGeometry.ContainsKey(descriptor.SourceKey)) return true;
            return UpsertLocalSceneGeometry(descriptor);
        }

        public bool UpsertLocalSceneGeometry(SceneGeometryDescriptor descriptor)
        {
            long generation = ConnectionGeneration;
            if (!IsLocalGeometryCurrent(generation) || descriptor.SourceKey == 0 ||
                (!descriptor.HasVolume && !descriptor.HasPlane)) return false;
            ResetSceneGeometryGeneration(generation);
            bool known = SceneLayout.TryGetRequested(descriptor.SourceKey, out SceneGeometryDescriptor current);
            // An MRUK anchor update that changed nothing this layout reads (the same pose, box and plane) is not a change:
            // re-staging it would remove and add its elements and move the regions around it again for nothing. Only when
            // it is staged now: a request another key's reflow could not stage again is retried, as before.
            if (known && SceneLayout.Has(descriptor.SourceKey) && SameDescriptor(current, descriptor))
            {
                ++RuntimeWorkCounters.SceneGeometryUnchanged;
                return true;
            }
            // A shared anchor that holds a region cannot be re-staged: its box is shared, so the core does not let the old one
            // go, and a new box beside it under the region would overlap it. It keeps the pose it was shared with.
            if (known && _anchorRegionByKey.ContainsKey(descriptor.SourceKey) && !SameGeometry(current, descriptor))
            {
                // Said once per anchor: an anchor that keeps updating would otherwise log every update.
                if (_movedSharedAnchorsWarned.Add(descriptor.SourceKey))
                    SpatialBlendDebug.LogWarning(SpatialBlendDebug.LogCategory.Scene,
                        $"Scene anchor {descriptor.SourceKey} moved, but it is shared and holds a region: it keeps the pose it was " +
                        "shared with. Unshare it and share it again to follow the new pose.");
                return false;
            }
            // Furniture that arrives where a region already is would be refused against it: the region's object moves first.
            MakeRoomForSceneGeometry(descriptor);
            return SceneLayout.Upsert(descriptor);
        }

        private bool StageSceneGeometryRaw(SceneGeometryDescriptor descriptor, int parentKey, Matrix4x4 local,
                                           IReadOnlyList<Vector2[]> pieces)
        {
            long space = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsLocalGeometryCurrent(space, generation)) return false;
            long parentElement = 0;
            // A shared scene anchor's tree hangs under its own region (TryApplySpatialAuthoring), not under the room.
            if (parentKey == 0 && _anchorRegionByKey.TryGetValue(descriptor.SourceKey, out AnchorRegionParent region) &&
                region.Element > 0 && region.Space == space && region.Generation == generation)
            {
                parentElement = region.Element;
                local = region.WorldInverse * local;
            }
            if (parentKey != 0)
            {
                if (!_ownedLocalSceneGeometry.TryGetValue(parentKey, out OwnedSceneGeometry parentOwned) ||
                    parentOwned.Parent <= 0 || parentOwned.Space != space || parentOwned.Generation != generation) return false;
                // Things nested under an anchor stand on it: under its measured top surface when it has one (a sibling of
                // that plane would overlap it), else under the anchor's own element.
                parentElement = parentOwned.Plane > 0 ? parentOwned.Plane : parentOwned.Parent;
            }
            _partialLocalSceneGeometry.TryGetValue(descriptor.SourceKey, out OwnedSceneGeometry staged);
            if (!TryStageSceneGeometry(descriptor, space, generation, ref staged, parentElement, local, pieces)) return false;

            bool replacing = _ownedLocalSceneGeometry.TryGetValue(descriptor.SourceKey,
                out OwnedSceneGeometry previous);
            if (replacing && !RemoveOwnedSceneGeometry(previous, false))
            {
                // The old tree remains authoritative; best-effort retire the staged
                // subtree but never pretend the replacement succeeded.
                if (RemoveOwnedSceneGeometry(staged, true))
                    _partialLocalSceneGeometry.Remove(descriptor.SourceKey);
                else
                    _partialLocalSceneGeometry[descriptor.SourceKey] = staged;
                return false;
            }
            _ownedLocalSceneGeometry[descriptor.SourceKey] = staged;
            _partialLocalSceneGeometry.Remove(descriptor.SourceKey);
            ++RuntimeWorkCounters.SceneGeometryStages;
            RuntimeWorkCounters.Log($"furniture {descriptor.SourceKey} staged");
            return true;
        }

        /// <summary>
        /// The elements the scene-geometry adapter already owns for one source key: the
        /// parent that carries the Obstacle box and the Surface plane child. A shared scene
        /// anchor binds these instead of creating a second element for the same anchor.
        /// </summary>
        public bool TryGetLocalSceneGeometryElements(int sourceKey, out long parent, out long plane)
        {
            parent = 0;
            plane = 0;
            if (sourceKey == 0 || _localSceneGeometryGeneration != ConnectionGeneration ||
                !IsLocalGeometryCurrent(ConnectionGeneration) ||
                !_ownedLocalSceneGeometry.TryGetValue(sourceKey, out OwnedSceneGeometry owned) ||
                owned.Parent <= 0 || owned.Space != _spaceID)
            {
                return false;
            }
            parent = owned.Parent;
            plane = owned.Plane;
            return true;
        }

        /// <summary>
        /// The element a thing whose world ground footprint is <paramref name="worldRing"/> nests under, when that footprint lies
        /// entirely inside an observed anchor's: the anchor's top surface when it has one, else the
        /// anchor. <paramref name="parentWorld"/> is that element's world transform; the thing's transform is relative to it.
        /// </summary>
        internal bool TryFindSceneGeometryContainer(Vector2[] worldRing, out long element, out Matrix4x4 parentWorld)
            => TryFindSceneGeometryContainer(worldRing, out element, out parentWorld, out _);

        internal bool TryFindSceneGeometryContainer(Vector2[] worldRing, out long element, out Matrix4x4 parentWorld, out int anchorKey)
        {
            anchorKey = 0;
            element = 0;
            parentWorld = Matrix4x4.identity;
            if (_sceneLayout == null || worldRing == null || !_sceneLayout.TryFindContainer(worldRing, out int key, out parentWorld)) return false;
            anchorKey = key;
            if (!_ownedLocalSceneGeometry.TryGetValue(key, out OwnedSceneGeometry owned) || owned.Parent <= 0 ||
                owned.Space != _spaceID || owned.Generation != ConnectionGeneration) return false;
            element = owned.Plane > 0 ? owned.Plane : owned.Parent;
            return true;
        }

        // Content that stands on observed geometry (see SceneGeometryLayoutEngine.Content) moves through here: the core moves the
        // element with its subtree, shared id and links, and the new parent-local transform is checked with the move.
        private long _holdingElement;

        private bool ReparentContentRaw(long element, int anchorKey, Matrix4x4 local)
        {
            long space = _spaceID;
            long generation = ConnectionGeneration;
            if (!IsLocalGeometryCurrent(space, generation)) return false;
            long parent;
            if (anchorKey == SceneGeometryLayoutEngine.Holding)
            {
                parent = EnsureHoldingElement(space);
                if (parent <= 0) return false;
            }
            else if (anchorKey == SceneGeometryLayoutEngine.Room)
            {
                parent = EnsureLocalSceneGeometryRoom(space);
            }
            else
            {
                if (!_ownedLocalSceneGeometry.TryGetValue(anchorKey, out OwnedSceneGeometry owned) || owned.Parent <= 0 ||
                    owned.Space != space || owned.Generation != generation) return false;
                parent = owned.Plane > 0 ? owned.Plane : owned.Parent;
            }
            return parent > 0 && SpatialBlendAPI.TryReparentElement(space, element, parent, local);
        }

        // No footprint, so nothing parked under it can overlap anything; a child of the room, whose frame is the world.
        private long EnsureHoldingElement(long space)
        {
            if (_holdingElement <= 0)
            {
                long room = EnsureLocalSceneGeometryRoom(space);
                if (room <= 0) return 0;
                _holdingElement = AddLocalSceneGeometryElement(space, room);
            }
            return _holdingElement;
        }

        // Moves an element under the holding element at its world pose.
        private bool ParkSceneGeometry(long space, long element, Matrix4x4 world)
        {
            long holding = EnsureHoldingElement(space);
            return holding > 0 && ReparentSpatialHierarchyElement(space, element, holding, world);
        }

        private void RegisterSceneContent(long element, int anchorKey, Func<Matrix4x4> world, Func<Vector2[]> footprint,
                                          Func<Vector2[]> extent, Action<int, Matrix4x4> moved)
        {
            SceneLayout.AddContent(new SceneGeometryLayoutEngine.Content
            {
                Element = element, AnchorKey = anchorKey, World = world, Footprint = footprint, Extent = extent, Moved = moved,
            });
        }

        // Elements of objects that stand on an observed anchor, with the inverse of that anchor element's world transform.
        private readonly Dictionary<long, Matrix4x4> _contentParentWorldInverse = new Dictionary<long, Matrix4x4>();

        private Matrix4x4 ContentLocalTransform(long element, Matrix4x4 world) =>
            _contentParentWorldInverse.TryGetValue(element, out Matrix4x4 inverse) ? inverse * world : world;

        // The collider's footprint on the ground in world (x, z), counter-clockwise. A box collider's own rectangle; any other
        // collider's bounds (which contain its footprint, so a containment found against it holds for the footprint too).
        private static Vector2[] ColliderGroundRing(Collider collider)
        {
            Vector3[] corners;
            if (collider is BoxCollider box)
            {
                Vector3 c = box.center, h = box.size * 0.5f;
                corners = new[]
                {
                    box.transform.TransformPoint(c + new Vector3(-h.x, 0f, -h.z)), box.transform.TransformPoint(c + new Vector3(h.x, 0f, -h.z)),
                    box.transform.TransformPoint(c + new Vector3(h.x, 0f, h.z)), box.transform.TransformPoint(c + new Vector3(-h.x, 0f, h.z)),
                };
            }
            else
            {
                Bounds b = collider.bounds;
                corners = new[]
                {
                    new Vector3(b.min.x, 0f, b.min.z), new Vector3(b.max.x, 0f, b.min.z),
                    new Vector3(b.max.x, 0f, b.max.z), new Vector3(b.min.x, 0f, b.max.z),
                };
            }
            var ring = new Vector2[4];
            for (int i = 0; i < 4; ++i) ring[i] = new Vector2(corners[i].x, corners[i].z);
            return CounterClockwise(ring);
        }

        private readonly HashSet<int> _movedSharedAnchorsWarned = new HashSet<int>();

        // Everything the layout and the staging read from a descriptor: its pose and box, and its plane outline.
        private static bool SameDescriptor(SceneGeometryDescriptor a, SceneGeometryDescriptor b)
        {
            if (!SameGeometry(a, b) || a.HasPlane != b.HasPlane ||
                a.PlaneBoundaryIsRectApproximation != b.PlaneBoundaryIsRectApproximation) return false;
            if (!a.HasPlane) return true;
            IReadOnlyList<Vector2> pa = a.PlaneBoundary, pb = b.PlaneBoundary;
            if (pa == null || pb == null) return pa == pb;
            if (pa.Count != pb.Count) return false;
            for (int i = 0; i < pa.Count; ++i)
            {
                if ((pa[i] - pb[i]).sqrMagnitude > 1e-8f) return false;
            }
            return true;
        }

        private static bool SameGeometry(SceneGeometryDescriptor a, SceneGeometryDescriptor b)
        {
            for (int i = 0; i < 16; ++i)
            {
                if (Mathf.Abs(a.LocalToWorld[i] - b.LocalToWorld[i]) > 1e-4f) return false;
            }
            return a.HasVolume == b.HasVolume && (!a.HasVolume || a.VolumeBounds == b.VolumeBounds);
        }

        public bool RemoveLocalSceneGeometry(int sourceKey)
        {
            long generation = ConnectionGeneration;
            ResetSceneGeometryGeneration(generation);
            // The layout takes the trees nested below this one down first and stages them again afterwards.
            if (!SceneLayout.Remove(sourceKey)) return false;
            return RemoveSceneGeometryRaw(sourceKey);
        }

        private bool RemoveSceneGeometryRaw(int sourceKey)
        {
            if (!_ownedLocalSceneGeometry.TryGetValue(sourceKey, out OwnedSceneGeometry owned))
            {
                if (_partialLocalSceneGeometry.TryGetValue(sourceKey, out OwnedSceneGeometry partial))
                {
                    if (!RemoveOwnedSceneGeometry(partial, false)) return false;
                    _partialLocalSceneGeometry.Remove(sourceKey);
                }
                return true;
            }
            if (!RemoveOwnedSceneGeometry(owned, false)) return false;
            _ownedLocalSceneGeometry.Remove(sourceKey);
            return true;
        }

        private bool TryStageSceneGeometry(SceneGeometryDescriptor descriptor, long space,
                                           long generation, ref OwnedSceneGeometry staged,
                                           long parentElement, Matrix4x4 local, IReadOnlyList<Vector2[]> pieces)
        {
            if (!IsLocalGeometryCurrent(space, generation)) return false;
            if (staged.Parent <= 0)
            {
                long room = EnsureLocalSceneGeometryRoom(space);
                if (room <= 0 || !IsLocalGeometryCurrent(space, generation)) return false;
                staged = new OwnedSceneGeometry { Parent = AddLocalSceneGeometryElement(space, parentElement > 0 ? parentElement : room), Space = space, Generation = generation };
                if (staged.Parent <= 0 || !IsLocalGeometryCurrent(space, generation)) return false;
                _partialLocalSceneGeometry[descriptor.SourceKey] = staged;
            }
            if (descriptor.HasPlane && staged.Plane <= 0)
            {
                staged.Plane = AddLocalSceneGeometryElement(space, staged.Parent);
                if (staged.Plane <= 0 || !IsLocalGeometryCurrent(space, generation)) return false;
                _partialLocalSceneGeometry[descriptor.SourceKey] = staged;
            }
            if (!IsLocalGeometryCurrent(space, generation)) return false;
            // One registration contract for both adapters (SceneGeometryChart): the element at the app's own world
            // transform (relative to its parent when it is nested), the outline as its local polygon. The core owns the
            // ground image (GroundFrame); nothing is flattened or re-framed to a floor.
            SetLocalSceneGeometryTransform(space, staged.Parent, local);
            if (!IsLocalGeometryCurrent(space, generation)) return false;
            if (descriptor.HasVolume)
            {
                if (pieces == null)
                {
                    Bounds volume = SceneGeometryChart.VolumeBounds(descriptor.VolumeBounds);
                    SetLocalSceneGeometryBox(space, staged.Parent, (int)SbGeometryRole.Obstacle,
                        volume.min, volume.max);
                }
                else if (pieces.Count > 0)
                {
                    // Clipped by a larger sibling: the remaining footprint is a few convex pieces under one group, which
                    // is not a sibling of the Surface plane (both would overlap it).
                    long group = AddLocalSceneGeometryElement(space, staged.Parent);
                    if (group <= 0 || !IsLocalGeometryCurrent(space, generation)) return false;
                    foreach (Vector2[] ring in pieces)
                    {
                        long piece = AddLocalSceneGeometryElement(space, group);
                        if (piece <= 0 || !IsLocalGeometryCurrent(space, generation)) return false;
                        SetLocalSceneGeometryPolygon(space, piece, (int)SbGeometryRole.Obstacle, ring);
                    }
                }
                if (!IsLocalGeometryCurrent(space, generation)) return false;
            }
            if (descriptor.HasPlane)
            {
                SetLocalSceneGeometryPolygon(space, staged.Plane, (int)SbGeometryRole.Surface,
                    SceneGeometryChart.PlanePolygon(descriptor.PlaneBoundary));
                if (!IsLocalGeometryCurrent(space, generation)) return false;
            }
            return true;
        }

        private bool RemoveOwnedSceneGeometry(OwnedSceneGeometry owned, bool bestEffort)
        {
            if (owned.Parent <= 0 || (!bestEffort &&
                (owned.Space != _spaceID || owned.Generation != ConnectionGeneration))) return false;
            try
            {
                if (!RemoveLocalSceneGeometryElement(owned.Space, owned.Parent))
                {
                    SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                        $"Failed to remove owned local scene geometry {owned.Parent} in space {owned.Space}.");
                    return false;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                SpatialBlendDebug.LogError(SpatialBlendDebug.LogCategory.Geometry,
                    $"Failed to remove owned local scene geometry {owned.Parent}: {ex.Message}");
                return false;
            }
        }

        private bool IsLocalGeometryCurrent(long generation) => IsLocalGeometryCurrent(_spaceID, generation);
        private bool IsLocalGeometryCurrent(long space, long generation) => !_isTearingDown &&
            Initialized && ConnectionState == SpatialBlendConnectionState.Connected && _spaceID == space &&
            ConnectionGeneration == generation;
        private void ResetSceneGeometryGeneration(long generation)
        {
            if (_localSceneGeometryGeneration == generation) return;
            _ownedLocalSceneGeometry.Clear();
            _partialLocalSceneGeometry.Clear();
            _sceneLayout?.Reset();
            _contentParentWorldInverse.Clear();
            _anchorRegionByKey.Clear();
            _movedSharedAnchorsWarned.Clear();
            _holdingElement = 0;
            _localSceneGeometryGeneration = generation;
        }

        /// <summary>
        /// The last scene change the core refused for this space (a containment overlap, a child outside its parent, an
        /// invalid argument), with the elements it names; null when none was. A refused change is not registered, and the
        /// same message is logged as an error when it happens.
        /// </summary>
        public string SceneRegistrationError => SpatialBlendAPI.TryGetSceneError(_spaceID, out string error) ? error : null;

        // Legacy hooks remain so existing deterministic manager fixtures retain
        // their injectable seam; captured-space overloads are used in production.
        protected virtual long EnsureLocalSceneGeometryRoom() => EnsureRoomElement();
        protected virtual long AddLocalSceneGeometryElement(long parent) => SpatialBlendAPI.AddElement(_spaceID, parent);
        protected virtual void SetLocalSceneGeometryTransform(long element, Matrix4x4 transform) => SpatialBlendAPI.SetElementTransform(_spaceID, element, transform);
        protected virtual void SetLocalSceneGeometryBox(long element, int role, Vector3 min, Vector3 max) => SpatialBlendAPI.SetElementBox(_spaceID, element, (SbGeometryRole)role, min, max);
        protected virtual void SetLocalSceneGeometryPolygon(long element, int role, IReadOnlyList<Vector2> points) => SpatialBlendAPI.SetElementPolygon(_spaceID, element, (SbGeometryRole)role, points);
        protected virtual long EnsureLocalSceneGeometryRoom(long space) => space == _spaceID ? EnsureLocalSceneGeometryRoom() : -1;
        protected virtual long AddLocalSceneGeometryElement(long space, long parent) => space == _spaceID ? AddLocalSceneGeometryElement(parent) : -1;
        protected virtual void SetLocalSceneGeometryTransform(long space, long element, Matrix4x4 transform) { if (space == _spaceID) SetLocalSceneGeometryTransform(element, transform); }
        protected virtual void SetLocalSceneGeometryBox(long space, long element, int role, Vector3 min, Vector3 max) { if (space == _spaceID) SetLocalSceneGeometryBox(element, role, min, max); }
        protected virtual void SetLocalSceneGeometryPolygon(long space, long element, int role, IReadOnlyList<Vector2> points) { if (space == _spaceID) SetLocalSceneGeometryPolygon(element, role, points); }
        protected virtual bool RemoveLocalSceneGeometryElement(long space, long element) => SpatialBlendAPI.RemoveLocalElement(space, element);
    }
}
