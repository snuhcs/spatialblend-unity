// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Events;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The one owner of the room NavMesh bakes (the avatar's walking surface, MRUK <see cref="SceneNavigation"/>).
    /// <para>
    /// Left to itself, <see cref="SceneNavigation"/> bakes on every MRUK scene load AND on every room created, updated or
    /// removed, each bake a full <c>NavMeshBuilder.BuildNavMeshData</c> plus destroying and re-creating every carving
    /// obstacle: a device that keeps several scanned rooms bakes once per room and again for the scene load, and every
    /// "significant" room update bakes again, whatever changed. This component turns those triggers off on the
    /// <see cref="SceneNavigation"/> (its <c>BuildOnSceneLoaded</c> and its room-event tracking) and takes them over:
    /// the requests of one frame are coalesced into one bake at <see cref="LateUpdate"/>, and a bake runs only when the
    /// rooms' geometry (their anchors' labels, poses and measured boxes) differs from the last bake's. The rooms baked
    /// are the ones the scene configured (<c>BuildOnSceneLoaded</c>: all rooms, or the current room).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneNavMeshBaker : MonoBehaviour
    {
        private SceneNavigation _navigation;
        private MRUK.RoomFilter _filter = MRUK.RoomFilter.AllRooms;
        private MRUK _hooked;
        private bool _pending;
        private bool _forced;
        private string _reason;
        private bool _baked;
        private long _lastFingerprint;

        /// <summary>Test seam: the geometry fingerprint (default: the MRUK rooms the filter selects).</summary>
        internal Func<long> FingerprintSource { get; set; }
        /// <summary>Test seam: the bake itself (default: <see cref="SceneNavigation"/>'s, for the filter's rooms).</summary>
        internal Action BakeAction { get; set; }

        /// <summary>The rooms the scene asked to be baked (<see cref="SceneNavigation.BuildOnSceneLoaded"/> before the take-over).</summary>
        public MRUK.RoomFilter Filter => _filter;
        public bool HasPendingRequest => _pending;

        /// <summary>The baker of <paramref name="navigation"/>, added to its GameObject on first use.</summary>
        public static SceneNavMeshBaker Ensure(SceneNavigation navigation)
        {
            if (navigation == null) return null;
            var baker = navigation.GetComponent<SceneNavMeshBaker>();
            if (baker == null) baker = navigation.gameObject.AddComponent<SceneNavMeshBaker>();
            baker.TakeOver();
            return baker;
        }

        private void Awake() => TakeOver();

        private void OnEnable() => HookMruk();

        private void OnDisable() => UnhookMruk();

        private void Update() => HookMruk();

        private void LateUpdate() => Flush();

        /// <summary>
        /// Turns off <see cref="SceneNavigation"/>'s own triggers. Before its Start (the app's Awake), clearing
        /// <c>BuildOnSceneLoaded</c> and <c>TrackUpdates</c> keeps it from subscribing; after it, its room listeners are removed.
        /// Idempotent.
        /// </summary>
        internal void TakeOver()
        {
            if (_navigation != null) return;
            _navigation = GetComponent<SceneNavigation>();
            if (_navigation == null) return;
            _filter = _navigation.BuildOnSceneLoaded;
            _navigation.BuildOnSceneLoaded = MRUK.RoomFilter.None;
            // TrackUpdates is internal to MRUK (and not serialized): reflection is the only switch.
            FieldInfo track = typeof(SceneNavigation).GetField("TrackUpdates", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (track != null && track.FieldType == typeof(bool)) track.SetValue(_navigation, false);
            MRUK mruk = MRUK.Instance;
            if (mruk != null)
            {
                RemoveListener(mruk.RoomCreatedEvent, "ReceiveCreatedRoom");
                RemoveListener(mruk.RoomUpdatedEvent, "ReceiveUpdatedRoom");
                RemoveListener(mruk.RoomRemovedEvent, "ReceiveRemovedRoom");
            }
        }

        private void RemoveListener(UnityEvent<MRUKRoom> evt, string method)
        {
            if (evt == null) return;
            MethodInfo info = typeof(SceneNavigation).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(MRUKRoom) }, null);
            if (info == null) return;
            var action = (UnityAction<MRUKRoom>)Delegate.CreateDelegate(typeof(UnityAction<MRUKRoom>), _navigation, info, false);
            if (action != null) evt.RemoveListener(action);
        }

        private void HookMruk()
        {
            MRUK mruk = MRUK.Instance;
            if (mruk == null || mruk == _hooked) return;
            UnhookMruk();
            _hooked = mruk;
            mruk.RoomCreatedEvent.AddListener(OnRoomCreated);
            mruk.RoomUpdatedEvent.AddListener(OnRoomUpdated);
            mruk.RoomRemovedEvent.AddListener(OnRoomRemoved);
            // Invoked at once when the scene is already loaded: the catch-up bake.
            mruk.RegisterSceneLoadedCallback(OnSceneLoaded);
        }

        private void UnhookMruk()
        {
            if (_hooked == null) return;
            _hooked.RoomCreatedEvent.RemoveListener(OnRoomCreated);
            _hooked.RoomUpdatedEvent.RemoveListener(OnRoomUpdated);
            _hooked.RoomRemovedEvent.RemoveListener(OnRoomRemoved);
            _hooked.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
            _hooked = null;
        }

        private void OnSceneLoaded() => Request("scene loaded");
        private void OnRoomCreated(MRUKRoom room) => Request("room created");
        private void OnRoomUpdated(MRUKRoom room) => Request("room updated");
        private void OnRoomRemoved(MRUKRoom room) => Request("room removed");

        /// <summary>Asks for a bake. It runs once at the end of the frame, and only if the rooms changed (or <paramref name="force"/>).</summary>
        public void Request(string reason, bool force = false)
        {
            ++RuntimeWorkCounters.NavMeshBakeRequests;
            _pending = true;
            _forced |= force;
            _reason = _reason == null ? reason : _reason + ", " + reason;
        }

        /// <summary>Bakes now (the control panel's Rebuild button): always, whatever changed.</summary>
        public bool BakeNow(string reason)
        {
            Request(reason, true);
            return Flush();
        }

        /// <summary>Runs the pending request: one bake when the geometry changed since the last one. True when it baked.</summary>
        internal bool Flush()
        {
            if (!_pending) return false;
            bool forced = _forced;
            string reason = _reason;
            _pending = false;
            _forced = false;
            _reason = null;
            if (!forced && _filter == MRUK.RoomFilter.None && BakeAction == null) return false;
            long fingerprint = FingerprintSource != null ? FingerprintSource() : RoomFingerprint();
            if (!forced && _baked && fingerprint == _lastFingerprint)
            {
                ++RuntimeWorkCounters.NavMeshBakesSkipped;
                RuntimeWorkCounters.Log($"NavMesh bake skipped, rooms unchanged ({reason})");
                return false;
            }
            long started = RuntimeWorkCounters.Start();
            if (!Bake()) return false;
            _baked = true;
            _lastFingerprint = fingerprint;
            ++RuntimeWorkCounters.NavMeshBakes;
            RuntimeWorkCounters.Log($"NavMesh bake ({reason})", started);
            return true;
        }

        private bool Bake()
        {
            if (BakeAction != null)
            {
                BakeAction();
                return true;
            }
            if (_navigation == null || MRUK.Instance == null) return false;
            List<MRUKRoom> rooms = MRUK.Instance.Rooms;
            if (rooms == null || rooms.Count == 0)
            {
                // The last room went: nothing to walk on (what SceneNavigation did on a room removed).
                _navigation.RemoveNavMeshData();
                return true;
            }
            if (_filter == MRUK.RoomFilter.CurrentRoomOnly && MRUK.Instance.GetCurrentRoom() != null)
                _navigation.BuildSceneNavMeshForRoom(MRUK.Instance.GetCurrentRoom());
            else
                _navigation.BuildSceneNavMesh();
            return true;
        }

        private long RoomFingerprint()
        {
            MRUK mruk = MRUK.Instance;
            if (mruk == null || mruk.Rooms == null) return 0L;
            long hash = 17L;
            if (_filter == MRUK.RoomFilter.CurrentRoomOnly) return Fingerprint(mruk.GetCurrentRoom(), hash);
            foreach (MRUKRoom room in mruk.Rooms) hash = Fingerprint(room, hash);
            return hash;
        }

        /// <summary>A hash of what a bake reads from a room: its anchors' labels, poses (1 mm) and measured boxes.</summary>
        internal static long Fingerprint(MRUKRoom room, long hash)
        {
            if (room == null) return Mix(hash, 0);
            hash = Mix(hash, room.GetInstanceID());
            if (room.Anchors == null) return hash;
            hash = Mix(hash, room.Anchors.Count);
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (anchor == null) { hash = Mix(hash, 0); continue; }
                hash = Mix(hash, (long)anchor.Label);
                Matrix4x4 m = anchor.transform.localToWorldMatrix;
                for (int i = 0; i < 16; ++i) hash = Mix(hash, Quant(m[i]));   // column-major: 12..14 is the position
                if (anchor.VolumeBounds.HasValue)
                {
                    Bounds b = anchor.VolumeBounds.Value;
                    hash = Mix(Mix(Mix(hash, Quant(b.min.x)), Quant(b.min.y)), Quant(b.min.z));
                    hash = Mix(Mix(Mix(hash, Quant(b.max.x)), Quant(b.max.y)), Quant(b.max.z));
                }
                if (anchor.PlaneRect.HasValue)
                {
                    Rect r = anchor.PlaneRect.Value;
                    hash = Mix(Mix(Mix(Mix(hash, Quant(r.xMin)), Quant(r.yMin)), Quant(r.xMax)), Quant(r.yMax));
                }
                // The floor's walkable surface is built from the plane outline, not only its bounding rectangle.
                if (anchor.PlaneBoundary2D != null)
                {
                    hash = Mix(hash, anchor.PlaneBoundary2D.Count);
                    foreach (Vector2 p in anchor.PlaneBoundary2D) hash = Mix(Mix(hash, Quant(p.x)), Quant(p.y));
                }
            }
            return hash;
        }

        private static long Quant(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0L : (long)Math.Round(v * 1000.0);
        private static long Mix(long hash, long value) => unchecked(hash * 1099511628211L ^ value);
    }
}
