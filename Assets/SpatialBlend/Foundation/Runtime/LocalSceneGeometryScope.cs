// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Exact-lifetime owner for generic observed room geometry in one Foundation
    /// local space. It owns only anonymous native element trees: no affordance,
    /// navigation, correspondence, MRUK, or transport meaning is introduced.
    /// </summary>
    public sealed class LocalSceneGeometryScope : IMutableLocalSceneGeometrySink, IDisposable
    {
        internal struct GeometryTree
        {
            internal long Parent;
            internal long Plane;
            /// <summary>The key of the tree this one is nested under (0: the room root).</summary>
            internal int ParentKey;
        }

        private readonly SpatialBlendLocalWorld _world;
        private readonly string _spaceId;
        private readonly string _scopeId;
        private readonly long _nativeSpace;
        private readonly int _generation;
        private long _roomRoot;
        private bool _live;
        private readonly Dictionary<int, GeometryTree> _active = new Dictionary<int, GeometryTree>();
        private readonly Dictionary<int, GeometryTree> _partial = new Dictionary<int, GeometryTree>();
        // Policy C layout (SceneFootprintLayout): contained footprints nest, partial overlaps are clipped.
        private readonly SceneGeometryLayoutEngine _layout;

        internal LocalSceneGeometryScope(SpatialBlendLocalWorld world, string spaceId, string scopeId,
                                         long nativeSpace, int generation, long roomRoot)
        {
            _world = world;
            _spaceId = spaceId;
            _scopeId = scopeId;
            _nativeSpace = nativeSpace;
            _generation = generation;
            _roomRoot = roomRoot;
            _live = true;
            _layout = new SceneGeometryLayoutEngine(StageRaw, RemoveRaw);
        }

        public bool IsLive => _live && _world != null &&
                              _world.IsSceneGeometryScopeCurrent(this, false);
        public string LastError { get; private set; }

        public bool RegisterLocalSceneGeometry(SceneGeometryDescriptor descriptor)
        {
            if (!IsLive) return Fail("Scene geometry scope is no longer live.");
            if (_active.ContainsKey(descriptor.SourceKey)) return true;
            return UpsertLocalSceneGeometry(descriptor);
        }

        public bool UpsertLocalSceneGeometry(SceneGeometryDescriptor descriptor)
        {
            if (!IsLive) return Fail("Scene geometry scope is no longer live.");
            if (descriptor.SourceKey == 0 || (!descriptor.HasVolume && !descriptor.HasPlane))
                return Fail("Observed scene geometry must contain a valid volume or measured plane.");
            if (!_world.CanOwnSceneGeometryKey(this, descriptor.SourceKey, out string ownershipError))
                return Fail(ownershipError);
            return _layout.Upsert(descriptor);
        }

        private bool StageRaw(SceneGeometryDescriptor descriptor, int parentKey, Matrix4x4 local,
                              IReadOnlyList<Vector2[]> pieces)
        {
            if (!IsLive) return Fail("Scene geometry scope is no longer live.");
            long parentElement = _roomRoot;
            if (parentKey != 0)
            {
                if (!_active.TryGetValue(parentKey, out GeometryTree parentTree) || parentTree.Parent <= 0)
                    return Fail("The tree this geometry nests under is not registered.");
                // Things nested under an anchor stand on it: under its measured top surface when it has one (a sibling of
                // that plane would overlap it), else under the anchor's own element.
                parentElement = parentTree.Plane > 0 ? parentTree.Plane : parentTree.Parent;
            }
            // A prior failed stage is never adopted as a replacement. Retire it
            // first, preserving it only when the native removal itself refuses so
            // the next call retries the exact same owned tree.
            if (_partial.TryGetValue(descriptor.SourceKey, out GeometryTree partial))
            {
                if (!_world.TryRemoveSceneGeometryTree(this, partial, false, out string partialError))
                    return Fail(partialError);
                _partial.Remove(descriptor.SourceKey);
                ReleaseKeyIfUnowned(descriptor.SourceKey);
            }

            if (!TryStage(descriptor, parentKey, parentElement, local, pieces, out GeometryTree staged, out string error))
            {
                ClaimKeyIfPartial(descriptor.SourceKey);
                return Fail(error);
            }

            if (_active.TryGetValue(descriptor.SourceKey, out GeometryTree previous) &&
                !_world.TryRemoveSceneGeometryTree(this, previous, false, out error))
            {
                // Existing geometry remains authoritative. A failed best-effort
                // staged cleanup is tracked for an exact retry and shutdown.
                if (!_world.TryRemoveSceneGeometryTree(this, staged, true, out _))
                    RetainPartial(descriptor.SourceKey, staged);
                return Fail(error);
            }
            _active[descriptor.SourceKey] = staged;
            _world.ClaimSceneGeometryKey(this, descriptor.SourceKey);
            LastError = null;
            return true;
        }

        public bool RemoveLocalSceneGeometry(int sourceKey)
        {
            if (!IsLive) return Fail("Scene geometry scope is no longer live.");
            // The layout takes the trees nested below this one down first and stages them again afterwards.
            if (!_layout.Remove(sourceKey)) return Fail("Native observed-geometry removal was rejected.");
            return RemoveRaw(sourceKey);
        }

        private bool RemoveRaw(int sourceKey)
        {
            if (!IsLive) return Fail("Scene geometry scope is no longer live.");
            if (_partial.TryGetValue(sourceKey, out GeometryTree partial))
            {
                if (!_world.TryRemoveSceneGeometryTree(this, partial, false, out string error))
                    return Fail(error);
                _partial.Remove(sourceKey);
                ReleaseKeyIfUnowned(sourceKey);
            }
            if (_active.TryGetValue(sourceKey, out GeometryTree active))
            {
                if (!_world.TryRemoveSceneGeometryTree(this, active, false, out string error))
                    return Fail(error);
                _active.Remove(sourceKey);
                ReleaseKeyIfUnowned(sourceKey);
            }
            LastError = null;
            return true;
        }

        public bool TryClose(out string error)
        {
            error = null;
            if (!_live) return true;
            if (!_world.TryCloseSceneGeometryScope(this, false, out error)) return Fail(error);
            LastError = null;
            return true;
        }

        public void Dispose()
        {
            TryClose(out _);
        }

        /// <summary>Diagnostic snapshot for native acceptance fixtures; handles are never mutable.</summary>
        internal bool TryGetNativeGeometry(int sourceKey, out long parent, out long plane)
        {
            parent = 0;
            plane = 0;
            if (!IsLive || !_active.TryGetValue(sourceKey, out GeometryTree tree)) return false;
            parent = tree.Parent;
            plane = tree.Plane;
            return true;
        }

        private bool TryStage(SceneGeometryDescriptor descriptor, int parentKey, long parentElement, Matrix4x4 local,
                              IReadOnlyList<Vector2[]> pieces, out GeometryTree tree, out string error)
        {
            tree = default;
            error = null;
            try
            {
                tree.ParentKey = parentKey;
                tree.Parent = _world.AddSceneGeometryElement(this, parentElement);
                if (tree.Parent <= 0)
                {
                    error = "Native observed-geometry element creation was rejected.";
                    return false;
                }
                if (descriptor.HasPlane)
                {
                    tree.Plane = _world.AddSceneGeometryElement(this, tree.Parent);
                    if (tree.Plane <= 0)
                    {
                        RetainPartial(descriptor.SourceKey, tree);
                        error = "Native observed-plane creation was rejected.";
                        return false;
                    }
                }
                // One registration contract for both adapters (SceneGeometryChart): the element at the app's own world
                // transform (relative to its parent when nested), the outline as its local polygon. The core owns the
                // ground image (GroundFrame); nothing is flattened or re-framed to a floor. The full 3D surface is
                // descriptor.BakedPlaneSurface, kept apart.
                _world.SetSceneGeometryTransform(this, tree.Parent, local);
                if (descriptor.HasVolume)
                {
                    if (pieces == null)
                    {
                        Bounds volume = SceneGeometryChart.VolumeBounds(descriptor.VolumeBounds);
                        _world.SetSceneGeometryBox(this, tree.Parent, volume.min, volume.max);
                    }
                    else if (pieces.Count > 0)
                    {
                        // Clipped by a larger sibling: convex pieces under one group (not a sibling of the Surface plane).
                        long group = _world.AddSceneGeometryElement(this, tree.Parent);
                        if (group <= 0)
                        {
                            RetainPartial(descriptor.SourceKey, tree);
                            error = "Native observed-footprint group creation was rejected.";
                            return false;
                        }
                        foreach (Vector2[] ring in pieces)
                        {
                            long piece = _world.AddSceneGeometryElement(this, group);
                            if (piece <= 0)
                            {
                                RetainPartial(descriptor.SourceKey, tree);
                                error = "Native observed-footprint piece creation was rejected.";
                                return false;
                            }
                            _world.SetSceneGeometryPolygon(this, piece, ring, SbGeometryRole.Obstacle);
                        }
                    }
                }
                if (descriptor.HasPlane)
                    _world.SetSceneGeometryPolygon(this, tree.Plane, SceneGeometryChart.PlanePolygon(descriptor.PlaneBoundary),
                        SbGeometryRole.Surface);
                return true;
            }
            catch (Exception exception)
            {
                if (tree.Parent > 0) RetainPartial(descriptor.SourceKey, tree);
                error = exception.Message;
                return false;
            }
        }

        private bool Fail(string error)
        {
            LastError = string.IsNullOrWhiteSpace(error) ? "Scene geometry scope failed." : error;
            return false;
        }

        // A partial tree has already acquired native identity. Keep the source
        // key exclusive until that exact tree and any active replacement are gone.
        private void RetainPartial(int sourceKey, GeometryTree tree)
        {
            _partial[sourceKey] = tree;
            _world.ClaimSceneGeometryKey(this, sourceKey);
        }

        private void ClaimKeyIfPartial(int sourceKey)
        {
            if (_partial.ContainsKey(sourceKey))
                _world.ClaimSceneGeometryKey(this, sourceKey);
        }

        internal void ReleaseKeyIfUnowned(int sourceKey)
        {
            if (!_active.ContainsKey(sourceKey) && !_partial.ContainsKey(sourceKey))
                _world.ReleaseSceneGeometryKey(this, sourceKey);
        }

        internal string SpaceId => _spaceId;
        internal string ScopeId => _scopeId;
        internal long NativeSpace => _nativeSpace;
        internal int Generation => _generation;
        internal long RoomRoot => _roomRoot;
        internal IReadOnlyDictionary<int, GeometryTree> Active => _active;
        internal IReadOnlyDictionary<int, GeometryTree> Partial => _partial;
        internal void RemoveActive(int key) => _active.Remove(key);
        internal int NestingDepth(int key)
        {
            int depth = 0;
            while (_active.TryGetValue(key, out GeometryTree tree) && tree.ParentKey != 0)
            {
                key = tree.ParentKey;
                ++depth;
            }
            return depth;
        }
        internal void RemovePartial(int key) => _partial.Remove(key);
        internal void MarkClosed()
        {
            _active.Clear(); _partial.Clear(); _layout.Reset(); _roomRoot = 0; _live = false;
        }
    }
}
