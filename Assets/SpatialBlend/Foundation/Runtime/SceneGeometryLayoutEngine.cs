// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>The ground footprint of an observed volume and the frame changes the layout needs (see <see cref="SceneFootprintLayout"/>).</summary>
    internal static class SceneGeometryFootprint
    {
        /// <summary>The registered element's world transform: the app's own, composed with the plane-chart basis change.</summary>
        internal static Matrix4x4 Element(SceneGeometryDescriptor d) => SceneGeometryChart.ElementTransform(d.LocalToWorld);

        /// <summary>
        /// The world ground (x, z) footprint of the volume's box, counter-clockwise; false when the geometry has no volume.
        /// The box is registered in the element frame, so its footprint is the rectangle of its local x and z.
        /// </summary>
        internal static bool TryRing(SceneGeometryDescriptor d, out Vector2[] ring)
        {
            ring = null;
            if (!d.HasVolume) return false;
            Bounds b = SceneGeometryChart.VolumeBounds(d.VolumeBounds);
            Matrix4x4 e = Element(d);
            float y = b.center.y;
            Vector3[] corners =
            {
                e.MultiplyPoint3x4(new Vector3(b.min.x, y, b.min.z)), e.MultiplyPoint3x4(new Vector3(b.max.x, y, b.min.z)),
                e.MultiplyPoint3x4(new Vector3(b.max.x, y, b.max.z)), e.MultiplyPoint3x4(new Vector3(b.min.x, y, b.max.z)),
            };
            var points = new Vector2[4];
            for (int i = 0; i < 4; ++i) points[i] = new Vector2(corners[i].x, corners[i].z);
            if (SignedArea(points) < 0f) Array.Reverse(points);
            ring = points;
            return SceneFootprintLayout.Area(points) > 0.0;
        }

        /// <summary>
        /// True when the element's local up axis is the world up axis, so that a world ground point maps back to the
        /// element's (x, z) plane without ambiguity. Only then can a footprint be clipped and re-expressed.
        /// </summary>
        internal static bool IsUpright(SceneGeometryDescriptor d)
        {
            Matrix4x4 e = Element(d);
            Vector3 up = e.GetColumn(1).normalized;
            return up.y > 1f - 1e-4f;
        }

        /// <summary>A world ground point in the element's local (x, z) frame.</summary>
        internal static Vector2 ToElementLocal(SceneGeometryDescriptor d, Vector2 world)
        {
            Matrix4x4 e = Element(d);
            Vector3 local = e.inverse.MultiplyPoint3x4(new Vector3(world.x, e.m13, world.y));
            return new Vector2(local.x, local.z);
        }

        private static float SignedArea(Vector2[] ring)
        {
            double sum = 0.0;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
                sum += (double)a.x * b.y - (double)b.x * a.y;
            }
            return (float)(sum * 0.5);
        }
    }

    /// <summary>
    /// Owns the layout of one space's observed geometry: it keeps every requested descriptor, plans the layout of all of
    /// them (<see cref="SceneFootprintLayout"/>) and has the sink stage the trees accordingly, re-staging the ones whose
    /// parent or clipped footprint changed, and the descendants a re-staged parent takes down with it. The sink only
    /// provides raw operations; one engine serves the Fusion manager and the Foundation-local scope alike.
    /// </summary>
    internal sealed class SceneGeometryLayoutEngine
    {
        /// <summary>Stages (or replaces) one key's tree under the tree of <c>parentKey</c> (0: the room). The pieces are in the key's element frame; null means the whole box.</summary>
        internal delegate bool StageFunction(SceneGeometryDescriptor descriptor, int parentKey, Matrix4x4 local,
                                             IReadOnlyList<Vector2[]> piecesInElementFrame);
        /// <summary>Removes one key's tree natively (and the sink's record of it). A tree's descendants are removed first.</summary>
        internal delegate bool RemoveFunction(int key);

        private sealed class Applied
        {
            public SceneGeometryDescriptor Descriptor;
            public int Parent;
            public List<Vector2[]> Pieces;
            public bool Staged;
        }

        /// <summary>
        /// Moves a content element (with its subtree, identity, shared id and links) under the tree of <c>anchorKey</c>
        /// (<see cref="Room"/>: the room, <see cref="Holding"/>: the holding element, which has no footprint) with the given
        /// parent-local transform. False when the move was refused.
        /// </summary>
        internal delegate bool ReparentFunction(long element, int anchorKey, Matrix4x4 local);

        internal const int Room = 0;
        internal const int Holding = -1;

        /// <summary>
        /// An object that stands on observed geometry (a Chess on the MRUK table, a collider on furniture) and so hangs under
        /// the anchor that contains it. Anchors are staged and retired by this engine and a retired tree takes everything
        /// below it down natively, so the content is parked out of the way first and set down again, under the anchor that now
        /// contains it, once the layout is staged: the same element, so its shared id and link state survive, at its
        /// unchanged world pose.
        /// </summary>
        internal sealed class Content
        {
            /// <summary>The element that stands on the anchor; its subtree moves with it.</summary>
            internal long Element;
            /// <summary>The anchor it hangs under now (<see cref="Room"/> when none).</summary>
            internal int AnchorKey;
            /// <summary>Its element's world transform now.</summary>
            internal Func<Matrix4x4> World;
            /// <summary>The ground footprint (counter-clockwise) that decides which anchor contains it.</summary>
            internal Func<Vector2[]> Footprint;
            /// <summary>The ground area its element claims as a sibling (a region's extent); it is moved out of the way of anchors that overlap it.</summary>
            internal Func<Vector2[]> Extent;
            /// <summary>Called after every move with the new anchor key and that anchor element's world transform (identity for the room).</summary>
            internal Action<int, Matrix4x4> Moved;
            internal bool Parked;
        }

        private readonly StageFunction _stage;
        private readonly RemoveFunction _remove;
        private readonly ReparentFunction _reparent;
        private readonly List<Content> _content = new List<Content>();
        private readonly Dictionary<int, SceneGeometryDescriptor> _requested = new Dictionary<int, SceneGeometryDescriptor>();
        private readonly Dictionary<int, Applied> _applied = new Dictionary<int, Applied>();

        internal SceneGeometryLayoutEngine(StageFunction stage, RemoveFunction remove, ReparentFunction reparent = null)
        {
            _stage = stage;
            _remove = remove;
            _reparent = reparent;
        }

        /// <summary>Registers content standing on (or beside) the observed geometry; without a reparent function content is not tracked.</summary>
        internal void AddContent(Content content)
        {
            if (_reparent != null && content != null && content.Element > 0) _content.Add(content);
        }

        internal void RemoveContent(long element) => _content.RemoveAll(c => c.Element == element);

        /// <summary>
        /// Content that was MOVED (its <see cref="Content.World"/> and <see cref="Content.Footprint"/> already read the new pose) may
        /// now stand on another anchor, or on none: sets it down under the smallest staged anchor that contains its footprint, or
        /// under the room, at its unchanged new world pose (policy C: a contained footprint nests, so the sibling-overlap rule is met by
        /// the hierarchy and never by an exemption). False when the content is not tracked, has no footprint, stays under the same
        /// anchor (the caller writes the transform as before), or the move was refused (the caller then writes the transform and the
        /// core's own answer decides).
        /// </summary>
        internal bool TryRehome(long element)
        {
            if (_reparent == null) return false;
            Content content = _content.Find(c => c.Element == element);
            if (content == null || content.Parked || content.Footprint == null) return false;
            Vector2[] footprint = content.Footprint();
            if (footprint == null) return false;
            int key = 0;
            Matrix4x4 parentWorld = Matrix4x4.identity;
            TryFindContainer(footprint, out key, out parentWorld);
            if (key == content.AnchorKey) return false;
            Matrix4x4 world = content.World();
            if (!_reparent(content.Element, key, key != 0 ? parentWorld.inverse * world : world)) return false;
            content.AnchorKey = key;
            content.Moved?.Invoke(key, key != 0 ? parentWorld : Matrix4x4.identity);
            return true;
        }

        internal int ContentCount => _content.Count;

        /// <summary>The world ground footprint of every staged piece of furniture (every anchor with a volume), at any depth.</summary>
        internal void CollectFurnitureFootprints(List<Vector2[]> into, int excludeKey = 0)
        {
            foreach (KeyValuePair<int, Applied> pair in _applied)
            {
                if (pair.Key != excludeKey && pair.Value.Staged &&
                    SceneGeometryFootprint.TryRing(pair.Value.Descriptor, out Vector2[] ring)) into.Add(ring);
            }
        }

        /// <summary>
        /// Adds the world ground footprint of everything this layout hangs directly under the room: each staged top-level
        /// anchor's box and each content element's extent (or footprint). Those are the siblings an authored region under the
        /// room must not overlap. <paramref name="excludeElement"/> (the region being fitted) and the anchor
        /// <paramref name="excludeKey"/> (the one whose region is fitted) are left out.
        /// </summary>
        internal void CollectRoomFootprints(List<Vector2[]> into, long excludeElement, int excludeKey = 0)
        {
            foreach (KeyValuePair<int, Applied> pair in _applied)
            {
                if (pair.Key != excludeKey && pair.Value.Staged && pair.Value.Parent == Room &&
                    SceneGeometryFootprint.TryRing(pair.Value.Descriptor, out Vector2[] ring)) into.Add(ring);
            }
            foreach (Content content in _content)
            {
                if (content.Parked || content.AnchorKey != Room || content.Element == excludeElement) continue;
                Vector2[] ring = content.Extent?.Invoke() ?? content.Footprint?.Invoke();
                if (ring != null && ring.Length >= 3) into.Add(ring);
            }
        }

        /// <summary>The descriptor a key was last requested with (to stage it again from scratch).</summary>
        internal bool TryGetRequested(int key, out SceneGeometryDescriptor descriptor) => _requested.TryGetValue(key, out descriptor);

        internal bool Has(int key) => _applied.TryGetValue(key, out Applied a) && a.Staged;

        /// <summary>The registered parent key of a staged key (0: the room), for diagnostics and tests.</summary>
        internal bool TryGetParent(int key, out int parent)
        {
            parent = 0;
            if (!_applied.TryGetValue(key, out Applied a) || !a.Staged) return false;
            parent = a.Parent;
            return true;
        }

        /// <summary>The smallest staged anchor whose footprint contains <paramref name="ring"/> at data precision, and its element's world transform.</summary>
        internal bool TryFindContainer(Vector2[] ring, out int key, out Matrix4x4 world)
        {
            key = 0;
            world = Matrix4x4.identity;
            double area = SceneFootprintLayout.Area(ring);
            double best = double.MaxValue;
            foreach (KeyValuePair<int, Applied> pair in _applied)
            {
                if (!pair.Value.Staged || !SceneGeometryFootprint.IsUpright(pair.Value.Descriptor) ||
                    !SceneGeometryFootprint.TryRing(pair.Value.Descriptor, out Vector2[] outer)) continue;
                double outerArea = SceneFootprintLayout.Area(outer);
                // The smallest container; a tie goes to the lower stable key, whatever order the dictionary holds them in.
                if ((outerArea < best || outerArea == best && pair.Key < key) && SceneFootprintLayout.Contains(outer, ring, area))
                {
                    best = outerArea;
                    key = pair.Key;
                    world = SceneGeometryFootprint.Element(pair.Value.Descriptor);
                }
            }
            return key != 0;
        }

        internal void Reset()
        {
            _requested.Clear();
            _applied.Clear();
            _content.Clear();
        }

        internal bool Upsert(SceneGeometryDescriptor descriptor)
        {
            int key = descriptor.SourceKey;
            SceneGeometryDescriptor? previous = _requested.TryGetValue(key, out SceneGeometryDescriptor p) ? p : (SceneGeometryDescriptor?)null;
            _requested[key] = descriptor;
            if (!Reflow(key))
            {
                // Never keep a request that could not be staged: it would be re-tried as if registered.
                if (previous.HasValue) _requested[key] = previous.Value; else _requested.Remove(key);
                // The siblings were laid out against the request that failed; lay them out against what is registered.
                Reflow(-1);
                return false;
            }
            return true;
        }

        internal bool Remove(int key)
        {
            if (!_requested.ContainsKey(key)) return true;
            var gone = new List<int>(Descendants(key));
            gone.Add(key);
            if (!Park(gone, null)) return false;
            // Deepest first (by tree depth, not by discovery order): a parent's native removal takes everything below it with it.
            gone.Sort((a, b) => { int d = Depth(b).CompareTo(Depth(a)); return d != 0 ? d : a.CompareTo(b); });
            for (int i = 0; i < gone.Count; ++i)
            {
                if (!_applied.TryGetValue(gone[i], out Applied a) || !a.Staged) continue;
                if (!_remove(gone[i])) { RehomeParked(); return false; }
                a.Staged = false;
            }
            _requested.Remove(key);
            _applied.Remove(key);
            Reflow(-1);
            return true;
        }

        // Plans the layout of everything requested and brings the sink in line. Returns false when
        // `mustStage` (the key just requested) could not be staged.
        private bool Reflow(int mustStage)
        {
            bool ok = ReflowCore(mustStage);
            // Content parked for this change is set down again whatever happened to the layout.
            RehomeParked();
            return ok;
        }

        private bool ReflowCore(int mustStage)
        {
            var inputs = new List<SceneFootprintLayout.Input>(_requested.Count);
            foreach (KeyValuePair<int, SceneGeometryDescriptor> pair in _requested)
            {
                Vector2[] ring = null;
                if (SceneGeometryFootprint.IsUpright(pair.Value)) SceneGeometryFootprint.TryRing(pair.Value, out ring);
                inputs.Add(new SceneFootprintLayout.Input(pair.Key, ring));
            }
            Dictionary<int, SceneFootprintLayout.Placement> plan = SceneFootprintLayout.Compute(inputs);

            var redo = new HashSet<int>();
            foreach (KeyValuePair<int, SceneGeometryDescriptor> pair in _requested)
            {
                int key = pair.Key;
                SceneFootprintLayout.Placement want = plan[key];
                if (key == mustStage || !_applied.TryGetValue(key, out Applied have) || !have.Staged ||
                    have.Parent != want.ParentKey || !SamePieces(have.Pieces, want.Pieces))
                    redo.Add(key);
            }
            // A re-staged tree takes the trees below it down with it.
            foreach (int key in new List<int>(redo))
                foreach (int below in Descendants(key)) redo.Add(below);

            // A replaced tree is retired BEFORE its replacement is staged, never after: the replacement's footprint is the
            // old one's, and two siblings may only touch. The old tree is not kept as a fallback.
            var newRings = new Dictionary<int, Vector2[]>();
            foreach (SceneFootprintLayout.Input input in inputs)
                if (redo.Contains(input.Key) && input.Ring != null) newRings[input.Key] = input.Ring;
            if (!Park(redo, newRings))
                return false;

            var removeOrder = new List<int>(redo);
            removeOrder.Sort((a, b) => { int d = Depth(b).CompareTo(Depth(a)); return d != 0 ? d : a.CompareTo(b); });
            foreach (int key in removeOrder)
            {
                if (_applied.TryGetValue(key, out Applied a) && a.Staged)
                {
                    if (!_remove(key)) return false;
                    a.Staged = false;
                }
            }

            var stageOrder = new List<int>(redo);
            // By depth, then by the stable key (MRUKSceneGeometryAdapter.StableKey): the same order in every run.
            stageOrder.Sort((a, b) => { int d = Depth(plan, a).CompareTo(Depth(plan, b)); return d != 0 ? d : a.CompareTo(b); });
            bool mustOk = mustStage < 0;
            foreach (int key in stageOrder)
            {
                SceneGeometryDescriptor d = _requested[key];
                SceneFootprintLayout.Placement want = plan[key];
                Matrix4x4 local = SceneGeometryFootprint.Element(d);
                bool ok = true;
                if (want.ParentKey != 0)
                {
                    if (!_applied.TryGetValue(want.ParentKey, out Applied parent) || !parent.Staged) ok = false;
                    else local = SceneGeometryFootprint.Element(_requested[want.ParentKey]).inverse * local;
                }
                List<Vector2[]> pieces = null;
                if (ok && want.Pieces != null)
                {
                    pieces = new List<Vector2[]>(want.Pieces.Count);
                    foreach (Vector2[] world in want.Pieces)
                    {
                        var ring = new Vector2[world.Length];
                        for (int i = 0; i < ring.Length; ++i) ring[i] = SceneGeometryFootprint.ToElementLocal(d, world[i]);
                        pieces.Add(ring);
                    }
                }
                if (ok) ok = _stage(d, want.ParentKey, local, pieces);
                _applied[key] = new Applied { Descriptor = d, Parent = want.ParentKey, Pieces = want.Pieces, Staged = ok };
                if (key == mustStage) mustOk = ok;
            }
            return mustOk;
        }

        // Parks the content that the trees of `keys` take with them, or whose extent the (re)staged footprints would overlap,
        // under the holding element (no footprint, so it overlaps nothing). False when a move was refused: nothing is retired then.
        private bool Park(IEnumerable<int> keys, Dictionary<int, Vector2[]> newRings)
        {
            if (_reparent == null || _content.Count == 0) return true;
            var affected = new List<Vector2[]>();
            var keySet = new HashSet<int>(keys);
            foreach (int key in keySet)
            {
                if (newRings != null && newRings.TryGetValue(key, out Vector2[] fresh)) affected.Add(fresh);
                if (_applied.TryGetValue(key, out Applied a) && a.Staged && SceneGeometryFootprint.TryRing(a.Descriptor, out Vector2[] old))
                    affected.Add(old);
            }
            foreach (Content content in _content)
            {
                if (content.Parked) continue;
                bool takenDown = content.AnchorKey > 0 && keySet.Contains(content.AnchorKey);
                bool overlapped = false;
                if (!takenDown && content.Extent != null && affected.Count > 0)
                {
                    Vector2[] extent = content.Extent();
                    foreach (Vector2[] ring in affected)
                    {
                        if (SceneFootprintLayout.IntersectionArea(extent, ring) >
                            SceneFootprintLayout.DataPrecision * System.Math.Min(SceneFootprintLayout.Area(extent), SceneFootprintLayout.Area(ring)))
                        { overlapped = true; break; }
                    }
                }
                if (!takenDown && !overlapped) continue;
                // The holding element is a child of the room, whose frame is the world, so the local transform is the world one.
                if (!_reparent(content.Element, Holding, content.World())) return false;
                content.Parked = true;
                content.AnchorKey = Room;
            }
            return true;
        }

        // Sets every parked content element down under the deepest staged anchor that contains its footprint, or under the room.
        private void RehomeParked()
        {
            if (_reparent == null) return;
            foreach (Content content in _content)
            {
                if (!content.Parked) continue;
                int key = 0;
                Matrix4x4 parentWorld = Matrix4x4.identity;
                Vector2[] footprint = content.Footprint != null ? content.Footprint() : null;
                if (footprint != null) TryFindContainer(footprint, out key, out parentWorld);
                Matrix4x4 world = content.World();
                if (_reparent(content.Element, key, key != 0 ? parentWorld.inverse * world : world))
                {
                    content.Parked = false;
                    content.AnchorKey = key;
                    content.Moved?.Invoke(key, key != 0 ? parentWorld : Matrix4x4.identity);
                }
            }
        }

        private IEnumerable<int> Descendants(int key)
        {
            var found = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(key);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (KeyValuePair<int, Applied> pair in _applied)
                {
                    if (pair.Value.Staged && pair.Value.Parent == current && pair.Key != key && !found.Contains(pair.Key))
                    {
                        found.Add(pair.Key);
                        queue.Enqueue(pair.Key);
                    }
                }
            }
            return found;
        }

        private int Depth(int key)
        {
            int depth = 0;
            while (_applied.TryGetValue(key, out Applied a) && a.Staged && a.Parent != 0)
            {
                key = a.Parent;
                ++depth;
            }
            return depth;
        }

        private static int Depth(Dictionary<int, SceneFootprintLayout.Placement> plan, int key)
        {
            int depth = 0;
            while (plan.TryGetValue(key, out SceneFootprintLayout.Placement p) && p.ParentKey != 0)
            {
                key = p.ParentKey;
                ++depth;
            }
            return depth;
        }

        private static bool SamePieces(List<Vector2[]> a, List<Vector2[]> b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; ++i)
            {
                if (a[i].Length != b[i].Length) return false;
                for (int j = 0; j < a[i].Length; ++j)
                    if ((a[i][j] - b[i][j]).sqrMagnitude > 1e-12f) return false;
            }
            return true;
        }
    }
}
