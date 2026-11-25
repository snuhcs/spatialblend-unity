// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Meta.XR.MRUtilityKit;
using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.Events;

namespace SpatialBlend
{
    /// <summary>
    /// Converts MRUK's local anchor primitives to local-only v2 geometry.
    /// It intentionally does not use anchor labels as an identity or behaviour cue.
    /// </summary>
    public static class MRUKSceneGeometryAdapter
    {
        public static int RegisterCurrentRoom(MRUKRoom room, ILocalSceneGeometrySink sink)
        {
            if (room == null || sink == null) return 0;

            int registered = 0;
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (TryCreateDescriptor(anchor, out SceneGeometryDescriptor descriptor) &&
                    sink.RegisterLocalSceneGeometry(descriptor))
                {
                    ++registered;
                }
            }
            return registered;
        }

        /// <summary>
        /// The anchor's key in the scene layout, the same in every run: a hash of the platform's anchor UUID, or,
        /// for an anchor that has none (a prefab or JSON room in the Editor), of its label and its measured pose and box (millimetres).
        /// Never 0 or -1 (the layout's room and holding keys). Unity's instance id changed from run to run, and with it the layout's order.
        /// </summary>
        public static int StableKey(MRUKAnchor anchor)
        {
            if (anchor == null) return 2;
            // Computed once per anchor: an anchor that moves later (an update) keeps its key.
            if (s_keys.TryGetValue(anchor, out object known)) return (int)known;
            // A UUID-less anchor not in a room yet has no room-relative pose: its key is not kept until it is.
            if (anchor.Anchor.Uuid == System.Guid.Empty && anchor.Room == null) return Hash(KeyText(anchor));
            PruneOwners();
            string text = KeyText(anchor);
            int key = Hash(text);
            // A collision with another live anchor (two anchors with the same pose and box) is logged and resolved the same way in
            // every run: the text is salted with a counter until the key is free.
            for (int salt = 1; s_owners.TryGetValue(key, out System.WeakReference<MRUKAnchor> owner) &&
                               owner.TryGetTarget(out MRUKAnchor other) && other != null && other != anchor; ++salt)
            {
                Debug.LogWarning($"[MRUKSceneGeometry] Anchor '{anchor.name}' has the key of '{other.name}' ({key}); salted with {salt}.");
                key = Hash(text + "#" + salt);
            }
            s_keys.Add(anchor, key);
            s_owners[key] = new System.WeakReference<MRUKAnchor>(anchor);
            return key;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MRUKAnchor, object> s_keys =
            new System.Runtime.CompilerServices.ConditionalWeakTable<MRUKAnchor, object>();
        private static readonly Dictionary<int, System.WeakReference<MRUKAnchor>> s_owners = new Dictionary<int, System.WeakReference<MRUKAnchor>>();

        // Keys of destroyed anchors (an unloaded room) are released, so a later room's anchors are not salted against them.
        private static void PruneOwners()
        {
            if (s_owners.Count < 256) return;
            var dead = new List<int>();
            foreach (KeyValuePair<int, System.WeakReference<MRUKAnchor>> pair in s_owners)
                if (!pair.Value.TryGetTarget(out MRUKAnchor a) || a == null) dead.Add(pair.Key);
            foreach (int key in dead) s_owners.Remove(key);
        }

        // The platform UUID, or the anchor's label, pose RELATIVE TO ITS ROOM (MRUK positioning the room does not change it) and box.
        private static string KeyText(MRUKAnchor anchor)
        {
            System.Guid uuid = anchor.Anchor.Uuid;
            if (uuid != System.Guid.Empty) return uuid.ToString();
            Transform room = anchor.Room != null ? anchor.Room.transform : null;
            UnityEngine.Vector3 p = room != null ? room.InverseTransformPoint(anchor.transform.position) : anchor.transform.position;
            UnityEngine.Vector3 e = (room != null ? UnityEngine.Quaternion.Inverse(room.rotation) * anchor.transform.rotation : anchor.transform.rotation).eulerAngles;
            UnityEngine.Bounds v = anchor.VolumeBounds ?? default;
            UnityEngine.Rect r = anchor.PlaneRect ?? default;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1:0.000},{2:0.000},{3:0.000}|{4:0.0},{5:0.0},{6:0.0}|{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11:0.000},{12:0.000}|{13:0.000},{14:0.000},{15:0.000},{16:0.000}",
                (long)anchor.Label, p.x, p.y, p.z, e.x, e.y, e.z, v.center.x, v.center.y, v.center.z, v.size.x, v.size.y, v.size.z,
                r.x, r.y, r.width, r.height);
        }

        private static int Hash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in text) h = (h ^ c) * 16777619;
                int key = (int)(h & 0x7fffffff);
                return key <= 1 ? key + 2 : key;
            }
        }

        /// <summary>
        /// Whether the anchor is a (near-)horizontal plane with no volume whose ground image covers most of the room: at least
        /// <see cref="RoomCoverFraction"/> of the room's floor outline (<see cref="MrukRoomRegion.FloorOutline"/>), whatever the
        /// room's size; without an outline, of the largest such plane of the room. That is the floor or the ceiling, by geometry,
        /// not by label: the room itself (RoomRegion registers it as the room's ring), not furniture. Staged, its surface covers the
        /// whole room in the ground image and the core reads it as a region that extends its anchor, refusing every later footprint
        /// under it (an own scan, a joiner's box: found by the room matrix).
        /// </summary>
        internal static bool CoversTheRoom(MRUKAnchor anchor)
        {
            if (anchor == null || anchor.VolumeBounds.HasValue || !IsHorizontalPlane(anchor)) return false;
            Vector2[] image = GroundImage(anchor);
            if (image == null) return false;
            double own = System.Math.Abs(SceneFootprintLayout.Area(image));
            if (own <= 0.0) return false;
            MRUKRoom room = anchor.Room;
            Vector2[] floor = room != null ? MrukRoomRegion.FloorOutline(room) : null;
            if (floor != null && floor.Length >= 3)
            {
                double floorArea = System.Math.Abs(SceneFootprintLayout.Area(floor));
                // Floors are often not convex (an L-shaped room), which the convex clipper does not measure: the plane covers the
                // room when its own area is that share of the floor's and it lies over the floor (its bounds over the floor's bounds).
                return floorArea > 0.0 && own >= RoomCoverFraction * floorArea && BoundsOverlap(image, floor) >= RoomCoverFraction;
            }
            double largest = own;
            if (room != null && room.Anchors != null)
                foreach (MRUKAnchor other in room.Anchors)
                {
                    if (other == null || other.VolumeBounds.HasValue || !IsHorizontalPlane(other)) continue;
                    Vector2[] o = GroundImage(other);
                    if (o != null) largest = System.Math.Max(largest, System.Math.Abs(SceneFootprintLayout.Area(o)));
                }
            return own >= RoomCoverFraction * largest;
        }

        /// <summary>The share of the room's floor outline a plane's ground image must cover to be the floor or the ceiling.</summary>
        public const float RoomCoverFraction = 0.6f;

        // Near-horizontal: a ceiling or floor scanned a few degrees off (cos 18 degrees).
        private static bool IsHorizontalPlane(MRUKAnchor anchor) =>
            anchor.PlaneBoundary2D != null && anchor.PlaneBoundary2D.Count >= 3 &&
            Mathf.Abs(Vector3.Dot(anchor.transform.forward, Vector3.up)) > 0.95f;

        // The plane's outline on the ground (world x, z).
        private static Vector2[] GroundImage(MRUKAnchor anchor)
        {
            List<Vector2> b = anchor.PlaneBoundary2D;
            if (b == null || b.Count < 3) return null;
            var ring = new Vector2[b.Count];
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector3 w = anchor.transform.TransformPoint(new Vector3(b[i].x, b[i].y, 0f));
                ring[i] = new Vector2(w.x, w.z);
            }
            return ring;
        }

        // The share of b's bounding box that a's bounding box covers.
        private static double BoundsOverlap(Vector2[] a, Vector2[] b)
        {
            Vector2 aMin = a[0], aMax = a[0], bMin = b[0], bMax = b[0];
            foreach (Vector2 v in a) { aMin = Vector2.Min(aMin, v); aMax = Vector2.Max(aMax, v); }
            foreach (Vector2 v in b) { bMin = Vector2.Min(bMin, v); bMax = Vector2.Max(bMax, v); }
            double w = Mathf.Min(aMax.x, bMax.x) - Mathf.Max(aMin.x, bMin.x), h = Mathf.Min(aMax.y, bMax.y) - Mathf.Max(aMin.y, bMin.y);
            double area = (double)(bMax.x - bMin.x) * (bMax.y - bMin.y);
            return w > 0 && h > 0 && area > 0 ? w * h / area : 0.0;
        }

        public static bool TryCreateDescriptor(MRUKAnchor anchor,
                                               out SceneGeometryDescriptor descriptor)
        {
            descriptor = default;
            if (anchor == null || anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH) || CoversTheRoom(anchor))
            {
                return false;
            }
            // A UUID-less anchor is keyed by its pose in its room: it is not staged before MRUK has put it in one.
            if (anchor.Anchor.Uuid == Guid.Empty && anchor.Room == null) return false;

            // PlaneBoundary2D is the exact measured outline. PlaneRect is only a
            // documented fallback when that outline is unavailable.
            return SceneGeometryDescriptor.TryCreate(StableKey(anchor),
                anchor.transform.localToWorldMatrix, anchor.VolumeBounds,
                anchor.PlaneBoundary2D, anchor.PlaneRect, out descriptor);
        }
    }

    /// <summary>Owns exact MRUK room listeners and forwards only concrete geometry.</summary>
    public sealed class MRUKSceneGeometryWatcher
    {
        private sealed class Binding
        {
            public readonly MRUKRoom Room;
            public readonly IMutableLocalSceneGeometrySink Sink;
            public readonly Func<bool> IsLive;
            public readonly bool UsesDefaultLiveness;
            public readonly HashSet<int> OwnedKeys = new HashSet<int>();
            public readonly HashSet<int> PendingRemovals = new HashSet<int>();
            public UnityAction<MRUKAnchor> Created;
            public UnityAction<MRUKAnchor> Updated;
            public UnityAction<MRUKAnchor> Removed;

            public Binding(MRUKRoom room, IMutableLocalSceneGeometrySink sink,
                           Func<bool> isLive, bool usesDefaultLiveness)
            {
                Room = room;
                Sink = sink;
                IsLive = isLive;
                UsesDefaultLiveness = usesDefaultLiveness;
            }
        }

        private Binding _active;
        private Binding _retiring;
        private int _callbackDepth;

        public bool Bind(MRUKRoom room, IMutableLocalSceneGeometrySink sink, Func<bool> isLive = null)
        {
            // A room event, liveness predicate, or sink is allowed to call back into
            // this object. It must not replace the binding currently on that stack.
            if (_callbackDepth != 0) return false;
            if (Matches(_active, room, sink, isLive)) return true;
            if (!Dispose()) return false;
            if (room == null || sink == null) return true;

            var binding = new Binding(room, sink, isLive ?? AlwaysLive, isLive == null);
            binding.Created = anchor => OnAnchorCreated(binding, anchor);
            binding.Updated = anchor => OnAnchorUpdated(binding, anchor);
            binding.Removed = anchor => OnAnchorRemoved(binding, anchor);
            _active = binding;
            room.AnchorCreatedEvent.AddListener(binding.Created);
            room.AnchorUpdatedEvent.AddListener(binding.Updated);
            room.AnchorRemovedEvent.AddListener(binding.Removed);
            if (IsLive(binding))
            {
                foreach (MRUKAnchor anchor in room.Anchors) Upsert(binding, anchor);
            }
            return true;
        }

        public bool Dispose()
        {
            if (_callbackDepth != 0) return false;
            if (_active != null)
            {
                Binding retired = _active;
                _active = null;
                Detach(retired);
                _retiring = retired;
            }
            return RetryPendingRemovals();
        }

        /// <summary>
        /// Retries removals which a mutable geometry sink previously rejected.
        /// For an active room this drains only invalidated/removed keys; on a
        /// retired binding it drains every key before a replacement may bind.
        /// </summary>
        public bool RetryPendingRemovals()
        {
            if (_callbackDepth != 0) return false;
            if (_retiring != null) return DrainRetired(_retiring);
            return _active == null || DrainPending(_active);
        }

        private static bool AlwaysLive() => true;

        private static bool Matches(Binding binding, MRUKRoom room,
                                    IMutableLocalSceneGeometrySink sink, Func<bool> isLive)
        {
            if (binding == null || binding.Room != room || binding.Sink != sink) return false;
            return isLive == null ? binding.UsesDefaultLiveness : binding.IsLive == isLive;
        }

        private bool IsCurrent(Binding binding) => ReferenceEquals(_active, binding);
        private bool IsOwned(Binding binding) => IsCurrent(binding) || ReferenceEquals(_retiring, binding);

        private bool IsLive(Binding binding)
        {
            if (!IsOwned(binding)) return false;
            ++_callbackDepth;
            try
            {
                return binding.IsLive != null && binding.IsLive() && IsOwned(binding);
            }
            finally { --_callbackDepth; }
        }

        private static void Detach(Binding binding)
        {
            if (binding == null || binding.Room == null) return;
            binding.Room.AnchorCreatedEvent.RemoveListener(binding.Created);
            binding.Room.AnchorUpdatedEvent.RemoveListener(binding.Updated);
            binding.Room.AnchorRemovedEvent.RemoveListener(binding.Removed);
        }

        private bool DrainRetired(Binding binding)
        {
            if (!ReferenceEquals(_retiring, binding)) return false;
            // A retired world generation must never delete geometry belonging to
            // its replacement. This preserves the prior stale-liveness contract.
            if (!IsLive(binding))
            {
                binding.OwnedKeys.Clear();
                binding.PendingRemovals.Clear();
                _retiring = null;
                return true;
            }
            bool complete = true;
            foreach (int key in new List<int>(binding.OwnedKeys))
            {
                if (!Remove(binding, key)) complete = false;
            }
            if (complete && ReferenceEquals(_retiring, binding)) _retiring = null;
            return complete && _retiring == null;
        }

        private bool DrainPending(Binding binding)
        {
            if (!IsCurrent(binding) || !IsLive(binding)) return false;
            bool complete = true;
            foreach (int key in new List<int>(binding.PendingRemovals))
            {
                if (!Remove(binding, key)) complete = false;
            }
            return complete && IsCurrent(binding);
        }

        private bool Remove(Binding binding, int key)
        {
            if (!IsOwned(binding)) return false;
            ++_callbackDepth;
            bool removed;
            try { removed = binding.Sink.RemoveLocalSceneGeometry(key); }
            finally { --_callbackDepth; }
            if (!IsOwned(binding)) return false;
            if (removed)
            {
                binding.OwnedKeys.Remove(key);
                binding.PendingRemovals.Remove(key);
                return true;
            }
            binding.PendingRemovals.Add(key);
            return false;
        }

        private void OnAnchorCreated(Binding binding, MRUKAnchor anchor) => Upsert(binding, anchor);
        private void OnAnchorUpdated(Binding binding, MRUKAnchor anchor) => Upsert(binding, anchor);
        private void OnAnchorRemoved(Binding binding, MRUKAnchor anchor)
        {
            if (!IsCurrent(binding) || !IsLive(binding) || anchor == null) return;
            int key = MRUKSceneGeometryAdapter.StableKey(anchor);
            if (binding.OwnedKeys.Contains(key)) Remove(binding, key);
        }

        private void Upsert(Binding binding, MRUKAnchor anchor)
        {
            if (!IsCurrent(binding) || !IsLive(binding)) return;
            if (MRUKSceneGeometryAdapter.TryCreateDescriptor(anchor,
                out SceneGeometryDescriptor descriptor))
            {
                ++_callbackDepth;
                bool upserted;
                try { upserted = binding.Sink.UpsertLocalSceneGeometry(descriptor); }
                finally { --_callbackDepth; }
                if (upserted && IsCurrent(binding))
                {
                    binding.OwnedKeys.Add(descriptor.SourceKey);
                    // A same-key fresh anchor supersedes an earlier failed remove.
                    binding.PendingRemovals.Remove(descriptor.SourceKey);
                }
            }
            else if (IsCurrent(binding) && IsLive(binding) && anchor != null)
            {
                int key = MRUKSceneGeometryAdapter.StableKey(anchor);
                if (binding.OwnedKeys.Contains(key)) Remove(binding, key);
            }
        }
    }
}
