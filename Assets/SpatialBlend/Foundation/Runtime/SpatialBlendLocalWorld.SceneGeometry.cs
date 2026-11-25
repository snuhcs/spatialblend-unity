// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    // Internal test seam for deterministic ownership/rollback contracts. Production
    // always reaches the selected SpatialBlendAPI library below.
    internal interface ISceneGeometryNativeOperations
    {
        long AddElement(long space, long parent);
        void SetElementTag(long space, long element, string tag);
        void SetElementTransform(long space, long element, Matrix4x4 transform);
        void SetElementBox(long space, long element, SbGeometryRole role, Vector3 min, Vector3 max);
        void SetElementWorldSurface(long space, long element, SbGeometryRole role,
                                    IReadOnlyList<Vector3> worldVertices);
        void SetElementPolygon(long space, long element, SbGeometryRole role, IReadOnlyList<Vector2> points);
        bool RemoveLocalElement(long space, long element);
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        private readonly Dictionary<ValueTuple<string, string>, LocalSceneGeometryScope>
            _sceneGeometryScopes = new Dictionary<ValueTuple<string, string>, LocalSceneGeometryScope>();
        private readonly Dictionary<ValueTuple<long, int>, LocalSceneGeometryScope>
            _sceneGeometryKeys = new Dictionary<ValueTuple<long, int>, LocalSceneGeometryScope>();
        // A room-root can exist before the scope becomes live because tag/setup
        // writes may throw. Keep that exact child until setup or removal succeeds;
        // otherwise a retry could allocate a second untracked room root.
        private readonly Dictionary<ValueTuple<string, string>, PendingSceneGeometryScope>
            _pendingSceneGeometryScopes =
                new Dictionary<ValueTuple<string, string>, PendingSceneGeometryScope>();
        internal static ISceneGeometryNativeOperations TestOnlySceneGeometryNativeOperations;

        private sealed class PendingSceneGeometryScope
        {
            internal long NativeSpace;
            internal int Generation;
            internal long RoomRoot;
        }

        /// <summary>Creates one exact-lifetime owner for generic observed geometry.</summary>
        public bool TryCreateSceneGeometryScope(string spaceId, string scopeId,
                                                out LocalSceneGeometryScope scope, out string error)
        {
            scope = null;
            error = null;
            if (!IsSceneGeometryMainThread() || !IsInitialized || !IsValidId(spaceId) || !IsValidId(scopeId))
                return Fail("Initialized local world, main thread, space ID, and scope ID are required.", out error);
            var key = (spaceId, scopeId);
            if (_sceneGeometryScopes.TryGetValue(key, out LocalSceneGeometryScope existing))
            {
                if (existing.IsLive)
                    return Fail("A live observed-geometry scope already owns this space and scope ID.", out error);
                _sceneGeometryScopes.Remove(key);
            }
            if (!TryGetOrCreateSpace(spaceId, out long nativeSpace, out error)) return false;
            if (!TryGetNativeSpaceRoot(spaceId, out long nativeSpaceRoot, out error)) return false;
            long root = 0;
            try
            {
                if (_pendingSceneGeometryScopes.TryGetValue(key, out PendingSceneGeometryScope pending))
                {
                    if (pending.Generation != _generation || pending.NativeSpace != nativeSpace ||
                        pending.RoomRoot <= 0)
                        return Fail("Pending observed-geometry scope ownership is stale.", out error);
                    root = pending.RoomRoot;
                }
                else
                {
                    root = AddSceneGeometryNativeElement(nativeSpace, nativeSpaceRoot);
                    if (root <= 0)
                        return Fail("Native observed-geometry room root creation failed.", out error);
                    // Claim before the first fallible setup write. A failed cleanup
                    // remains owned here and the next call operates on this root.
                    _pendingSceneGeometryScopes.Add(key, new PendingSceneGeometryScope
                    {
                        NativeSpace = nativeSpace,
                        Generation = _generation,
                        RoomRoot = root
                    });
                }
                SetSceneGeometryNativeTag(nativeSpace, root, scopeId);
                var created = new LocalSceneGeometryScope(this, spaceId, scopeId, nativeSpace, _generation, root);
                _sceneGeometryScopes.Add(key, created);
                _pendingSceneGeometryScopes.Remove(key);
                scope = created;
                return true;
            }
            catch (Exception exception)
            {
                scope = null;
                if (root > 0 && TryRemoveSceneGeometryElement(nativeSpace, root, true, out _))
                    _pendingSceneGeometryScopes.Remove(key);
                return Fail(exception.Message, out error);
            }
        }

        internal bool IsSceneGeometryScopeCurrent(LocalSceneGeometryScope scope, bool allowShutdown)
        {
            return scope != null && IsSceneGeometryMainThread() && (allowShutdown || !_shuttingDown) &&
                _session > 0 && scope.Generation == _generation &&
                _sceneGeometryScopes.TryGetValue((scope.SpaceId, scope.ScopeId), out LocalSceneGeometryScope current) &&
                ReferenceEquals(current, scope) && scope.NativeSpace > 0;
        }

        internal bool CanOwnSceneGeometryKey(LocalSceneGeometryScope scope, int sourceKey, out string error)
        {
            error = null;
            if (!IsSceneGeometryScopeCurrent(scope, false) || sourceKey == 0)
            {
                error = "Observed-geometry scope is stale or source key is invalid.";
                return false;
            }
            if (_sceneGeometryKeys.TryGetValue((scope.NativeSpace, sourceKey), out LocalSceneGeometryScope owner) &&
                !ReferenceEquals(owner, scope))
            {
                error = "Another live observed-geometry scope owns this source key in the local space.";
                return false;
            }
            return true;
        }

        internal void ClaimSceneGeometryKey(LocalSceneGeometryScope scope, int sourceKey)
        {
            if (IsSceneGeometryScopeCurrent(scope, false))
                _sceneGeometryKeys[(scope.NativeSpace, sourceKey)] = scope;
        }

        internal void ReleaseSceneGeometryKey(LocalSceneGeometryScope scope, int sourceKey)
        {
            if (_sceneGeometryKeys.TryGetValue((scope.NativeSpace, sourceKey), out LocalSceneGeometryScope owner) &&
                ReferenceEquals(owner, scope)) _sceneGeometryKeys.Remove((scope.NativeSpace, sourceKey));
        }

        internal long AddSceneGeometryElement(LocalSceneGeometryScope scope, long parent)
        {
            return IsSceneGeometryScopeCurrent(scope, false) ?
                AddSceneGeometryNativeElement(scope.NativeSpace, parent) : 0;
        }
        internal void SetSceneGeometryTransform(LocalSceneGeometryScope scope, long element, Matrix4x4 transform)
        {
            if (!IsSceneGeometryScopeCurrent(scope, false)) throw new InvalidOperationException("Observed-geometry scope is stale.");
            SetSceneGeometryNativeTransform(scope.NativeSpace, element, transform);
        }
        internal void SetSceneGeometryBox(LocalSceneGeometryScope scope, long element, Vector3 min, Vector3 max)
        {
            if (!IsSceneGeometryScopeCurrent(scope, false)) throw new InvalidOperationException("Observed-geometry scope is stale.");
            SetSceneGeometryNativeBox(scope.NativeSpace, element, SbGeometryRole.Obstacle, min, max);
        }
        internal void SetSceneGeometryPolygon(LocalSceneGeometryScope scope, long element, IReadOnlyList<Vector2> points,
                                              SbGeometryRole role = SbGeometryRole.Surface)
        {
            if (!IsSceneGeometryScopeCurrent(scope, false)) throw new InvalidOperationException("Observed-geometry scope is stale.");
            if (TestOnlySceneGeometryNativeOperations != null)
                TestOnlySceneGeometryNativeOperations.SetElementPolygon(scope.NativeSpace, element, role, points);
            else SpatialBlendAPI.SetElementPolygon(scope.NativeSpace, element, role, points);
        }
        internal void SetSceneGeometryWorldSurface(LocalSceneGeometryScope scope, long element,
                                                   IReadOnlyList<Vector3> worldVertices)
        {
            if (!IsSceneGeometryScopeCurrent(scope, false)) throw new InvalidOperationException("Observed-geometry scope is stale.");
            SetSceneGeometryNativeWorldSurface(scope.NativeSpace, element, SbGeometryRole.Surface, worldVertices);
        }
        internal bool TryRemoveSceneGeometryTree(LocalSceneGeometryScope scope,
                                                  LocalSceneGeometryScope.GeometryTree tree,
                                                  bool bestEffort, out string error)
        {
            error = null;
            if (tree.Parent <= 0) return true;
            if (!IsSceneGeometryScopeCurrent(scope, bestEffort))
            {
                error = "Observed-geometry scope is stale.";
                return false;
            }
            return TryRemoveSceneGeometryElement(scope.NativeSpace, tree.Parent, bestEffort, out error);
        }

        internal bool TryCloseSceneGeometryScope(LocalSceneGeometryScope scope, bool bestEffort,
                                                 out string error)
        {
            error = null;
            if (scope == null || !IsSceneGeometryScopeCurrent(scope, bestEffort))
            {
                error = "Observed-geometry scope is stale.";
                return false;
            }
            foreach (int key in new List<int>(scope.Partial.Keys))
            {
                if (!TryRemoveSceneGeometryTree(scope, scope.Partial[key], bestEffort, out error)) return false;
                scope.RemovePartial(key);
                scope.ReleaseKeyIfUnowned(key);
            }
            var activeKeys = new List<int>(scope.Active.Keys);
            // Nested trees go before the trees they hang under: removing a parent takes its children with it.
            activeKeys.Sort((a, b) => scope.NestingDepth(b).CompareTo(scope.NestingDepth(a)));
            foreach (int key in activeKeys)
            {
                if (!TryRemoveSceneGeometryTree(scope, scope.Active[key], bestEffort, out error)) return false;
                scope.RemoveActive(key);
                scope.ReleaseKeyIfUnowned(key);
            }
            if (scope.RoomRoot > 0 && !TryRemoveSceneGeometryElement(scope.NativeSpace, scope.RoomRoot,
                                                                       bestEffort, out error)) return false;
            _sceneGeometryScopes.Remove((scope.SpaceId, scope.ScopeId));
            scope.MarkClosed();
            return true;
        }

        private bool TryRemoveSceneGeometryElement(long space, long element, bool bestEffort,
                                                   out string error)
        {
            error = null;
            try
            {
                RetireInteractionHierarchiesInSpace(space);
                if (HasPendingHierarchyInSpace(space))
                { error = "Hierarchy retirement must complete before observed-geometry removal."; return false; }
                if (RemoveSceneGeometryNativeElement(space, element)) return true;
                error = "Native observed-geometry removal was rejected.";
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void RetireSceneGeometryScopesForShutdown()
        {
            foreach (LocalSceneGeometryScope scope in new List<LocalSceneGeometryScope>(_sceneGeometryScopes.Values))
            {
                TryCloseSceneGeometryScope(scope, true, out _);
                scope.MarkClosed();
            }
            _sceneGeometryScopes.Clear();
            _sceneGeometryKeys.Clear();
            // Pending roots never became usable scopes, but are still local
            // children. Try their exact teardown before session destruction;
            // failures are session-owned and must not be retried after teardown.
            foreach (PendingSceneGeometryScope pending in _pendingSceneGeometryScopes.Values)
                TryRemoveSceneGeometryElement(pending.NativeSpace, pending.RoomRoot, true, out _);
            _pendingSceneGeometryScopes.Clear();
        }

        private bool IsSceneGeometryMainThread() =>
            System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThread;

        private static long AddSceneGeometryNativeElement(long space, long parent) =>
            TestOnlySceneGeometryNativeOperations != null
                ? TestOnlySceneGeometryNativeOperations.AddElement(space, parent)
                : SpatialBlendAPI.AddElement(space, parent);

        private static void SetSceneGeometryNativeTag(long space, long element, string tag)
        {
            if (TestOnlySceneGeometryNativeOperations != null)
                TestOnlySceneGeometryNativeOperations.SetElementTag(space, element, tag);
            else SpatialBlendAPI.SetElementTag(space, element, tag);
        }

        private static void SetSceneGeometryNativeTransform(long space, long element, Matrix4x4 transform)
        {
            if (TestOnlySceneGeometryNativeOperations != null)
                TestOnlySceneGeometryNativeOperations.SetElementTransform(space, element, transform);
            else SpatialBlendAPI.SetElementTransform(space, element, transform);
        }

        private static void SetSceneGeometryNativeBox(long space, long element, SbGeometryRole role,
                                                       Vector3 min, Vector3 max)
        {
            if (TestOnlySceneGeometryNativeOperations != null)
                TestOnlySceneGeometryNativeOperations.SetElementBox(space, element, role, min, max);
            else SpatialBlendAPI.SetElementBox(space, element, role, min, max);
        }

        private static void SetSceneGeometryNativeWorldSurface(long space, long element, SbGeometryRole role,
                                                                IReadOnlyList<Vector3> worldVertices)
        {
            if (TestOnlySceneGeometryNativeOperations != null)
                TestOnlySceneGeometryNativeOperations.SetElementWorldSurface(space, element, role, worldVertices);
            else SpatialBlendAPI.SetElementWorldSurface(space, element, worldVertices, role);
        }

        private static bool RemoveSceneGeometryNativeElement(long space, long element) =>
            TestOnlySceneGeometryNativeOperations != null
                ? TestOnlySceneGeometryNativeOperations.RemoveLocalElement(space, element)
                : SpatialBlendAPI.RemoveLocalElement(space, element);
    }
}
