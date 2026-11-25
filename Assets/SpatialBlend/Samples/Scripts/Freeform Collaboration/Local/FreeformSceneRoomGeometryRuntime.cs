// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Meta.XR.MRUtilityKit;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// One FoundationLocal lifetime of generic observed room geometry. It consumes
    /// exact MRUK descriptors only; labels never become content identity,
    /// affordances, support correspondences, or navigation declarations here.
    /// </summary>
    public sealed class FreeformSceneRoomGeometryRuntime
    {
        private readonly FreeformSceneRoomGeometryDefinition _definition;
        private readonly SpatialBlendLocalWorld _world;
        private readonly Scene _scene;
        private readonly MRUKSceneGeometryWatcher _watcher = new MRUKSceneGeometryWatcher();
        private readonly Func<bool> _isLive;
        private LocalSceneGeometryScope _scope;
        private MRUK _mruk;
        private MRUKRoom _room;
        private MRUKRoom _removedRoom;
        private bool _running;
        private bool _stopping;
        private bool _teardownPending;
        private bool _listenersDetached;

        private FreeformSceneRoomGeometryRuntime(FreeformSceneRoomGeometryDefinition definition,
                                                 SpatialBlendLocalWorld world)
        {
            _definition = definition;
            _world = world;
            _scene = definition.gameObject.scene;
            _isLive = IsWatcherLive;
        }

        public bool IsRunning => _running;
        public bool IsWaitingForRoom => _running && _room == null;
        /// <summary>True only while this exact scope is awaiting a failed-close retry.</summary>
        public bool IsTeardownPending => _teardownPending;
        public LocalSceneGeometryScope Scope => _scope;
        public MRUKRoom CurrentRoom => _room;
        public string LastError { get; private set; }

        public static bool TryStart(FreeformSceneRoomGeometryDefinition definition,
                                    SpatialBlendLocalWorld world, string receiverSpaceId,
                                    out FreeformSceneRoomGeometryRuntime runtime, out string error)
        {
            runtime = null;
            error = null;
            if (definition == null || world == null || !world.IsInitialized ||
                string.IsNullOrWhiteSpace(receiverSpaceId) ||
                world.gameObject.scene != definition.gameObject.scene ||
                !definition.ValidateForLoadedScene(definition.gameObject.scene, out error))
            {
                error ??= "A live local world, receiver space, and original room-geometry definition are required.";
                return false;
            }

            var candidate = new FreeformSceneRoomGeometryRuntime(definition, world);
            if (!world.TryCreateSceneGeometryScope(receiverSpaceId, definition.ScopeId,
                                                   out LocalSceneGeometryScope scope, out error))
                return false;
            candidate._scope = scope;
            candidate._mruk = definition.Mruk;
            candidate._running = true;
            candidate.Subscribe();
            if (!candidate.Tick(out error))
            {
                string startError = string.IsNullOrWhiteSpace(error)
                    ? "Room geometry startup did not complete."
                    : error;
                if (!candidate.TryStop(out string stopError))
                {
                    // A caller that owns the world must retain this exact runtime
                    // until it can retry or retire that world. Dropping it would
                    // lose authority over a still-live scope.
                    runtime = candidate;
                    error = startError + " Cleanup is pending: " + stopError;
                }
                return false;
            }
            runtime = candidate;
            return true;
        }

        /// <summary>
        /// A missing/not-yet-loaded room on the declared scene MRUK component is a
        /// truthful waiting state rather than a content-start failure or fallback.
        /// </summary>
        public bool Tick(out string error)
        {
            error = null;
            if (_teardownPending)
                return TryStop(out error);
            if (!IsLive())
            {
                error = "The local room-geometry lifetime is no longer current.";
                LastError = error;
                return false;
            }

            // Retain exact failed removals and retry them before room adoption.
            // This cannot remove observations from a replacement lifetime because
            // the watcher liveness predicate is this exact scope/world generation.
            if (!_watcher.RetryPendingRemovals())
            {
                error = LastError = _scope.LastError ?? "Room geometry removal is pending.";
                return false;
            }
            if (_removedRoom != null && ReferenceEquals(_removedRoom, _room))
            {
                // A failed RoomRemoved disposal just drained its retired binding.
                // Keep the removed identity as a guard against MRUK's cached room,
                // but report that this lifetime is now waiting for a new room.
                _room = null;
            }

            return AdoptCurrentRoom(out error);
        }

        public void Stop()
        {
            TryStop(out _);
        }

        /// <summary>
        /// Detaches declared MRUK listeners and closes only this exact scope. A
        /// rejected removal leaves a retryable, listener-detached lifetime rather
        /// than pretending disposal completed.
        /// </summary>
        public bool TryStop(out string error)
        {
            error = null;
            if (_stopping)
            {
                error = "Room geometry teardown is already in progress.";
                return false;
            }
            if (!_running && !_teardownPending)
                return true;

            _stopping = true;
            _teardownPending = true;
            try
            {
                if (!_listenersDetached)
                {
                    Unsubscribe();
                    _listenersDetached = true;
                }
                // Dispose first while this exact scope/world is live, so ordinary
                // room removal is observed before the backend retires its world.
                if (!_watcher.Dispose())
                {
                    error = LastError = _scope != null ? _scope.LastError : "Room geometry removal is pending.";
                    return false;
                }
                if (_scope != null && _scope.IsLive && !_scope.TryClose(out error))
                {
                    LastError = error;
                    return false;
                }
                _room = null;
                _removedRoom = null;
                _running = false;
                _teardownPending = false;
                LastError = null;
                return true;
            }
            finally
            {
                _stopping = false;
            }
        }

        private void OnSceneLoaded()
        {
            if (IsLive())
                Tick(out _);
        }

        private void OnRoomCreated(MRUKRoom room)
        {
            if (IsLive())
                Tick(out _);
        }

        private void OnRoomUpdated(MRUKRoom room)
        {
            if (IsLive())
                Tick(out _);
        }

        private void OnRoomRemoved(MRUKRoom room)
        {
            if (!IsLive() || room == null || !ReferenceEquals(room, _room))
                return;

            // MRUK invokes this before its room list is necessarily updated. Do
            // not immediately ask GetCurrentRoom() and accidentally re-adopt the
            // just-retired instance; the next Tick adopts only the current room.
            _removedRoom = room;
            if (_watcher.Dispose())
            {
                _room = null;
                LastError = null;
            }
            else
            {
                LastError = _scope.LastError ?? "Room geometry removal is pending.";
            }
        }

        private bool AdoptCurrentRoom(out string error)
        {
            error = null;
            MRUKRoom candidate = _mruk != null ? _mruk.GetCurrentRoom() : null;
            if (candidate == null)
            {
                // A room switch to no current room must retire only this scope's
                // old observations; no ambient manager or global fallback is used.
                if (_room != null && !_watcher.Dispose())
                {
                    error = LastError = _scope.LastError;
                    return false;
                }
                _room = null;
                LastError = null;
                return true;
            }
            if (ReferenceEquals(candidate, _removedRoom))
            {
                // The SDK can invoke removal before GetCurrentRoom's cached value
                // changes. Do not re-adopt that exact retired room.
                return true;
            }
            _removedRoom = null;
            if (_room == candidate)
            {
                LastError = null;
                return true;
            }
            if (!_watcher.Bind(candidate, _scope, _isLive))
            {
                error = LastError = _scope.LastError;
                return false;
            }
            _room = candidate;
            LastError = null;
            return true;
        }

        private bool IsLive() => _running && !_stopping && !_teardownPending && _scope != null && _scope.IsLive &&
            _world != null && _world.IsInitialized && _mruk != null && _definition != null &&
            _scene.IsValid() && _scene.isLoaded && _definition.gameObject.scene == _scene &&
            _mruk.gameObject.scene == _scene;

        // Stop unsubscribes the declared MRUK source before draining the watcher.
        // The drain itself must remain live while the exact scope/world still are,
        // otherwise Dispose would clear bookkeeping and skip owned removals.
        private bool IsWatcherLive() => (_running || _teardownPending) && _scope != null && _scope.IsLive &&
            _world != null && _world.IsInitialized && _mruk != null && _definition != null &&
            _scene.IsValid() && _scene.isLoaded && _definition.gameObject.scene == _scene &&
            _mruk.gameObject.scene == _scene;

        private void Subscribe()
        {
            if (_mruk == null)
                return;
            _mruk.RegisterSceneLoadedCallback(OnSceneLoaded);
            _mruk.RoomCreatedEvent.AddListener(OnRoomCreated);
            _mruk.RoomUpdatedEvent.AddListener(OnRoomUpdated);
            _mruk.RoomRemovedEvent.AddListener(OnRoomRemoved);
        }

        private void Unsubscribe()
        {
            if (_mruk == null)
                return;
            _mruk.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
            _mruk.RoomCreatedEvent.RemoveListener(OnRoomCreated);
            _mruk.RoomUpdatedEvent.RemoveListener(OnRoomUpdated);
            _mruk.RoomRemovedEvent.RemoveListener(OnRoomRemoved);
        }
    }
}
