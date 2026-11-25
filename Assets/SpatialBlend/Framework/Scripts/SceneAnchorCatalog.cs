// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// One room anchor the app can share or link. MRUK is not required: tests
    /// supply records directly.
    /// </summary>
    public sealed class SceneAnchorRecord
    {
        public string Label;
        public GameObject Anchor;
        /// <summary>
        /// The key the scene-geometry adapter registered this anchor under (the MRUK anchor's
        /// instance id). The shared TABLE binds the elements registered under it.
        /// </summary>
        public int SourceKey;
        /// <summary>
        /// The platform's stable id for this anchor (the MRUK anchor UUID), or empty when it has none. Saved
        /// objects use it to find the same anchor again in a later session.
        /// </summary>
        public string AnchorId = string.Empty;
        public float Size;
        public Vector3 LocalCenter;
        public Vector3 LocalSize;
        /// <summary>
        /// The measured outline of the anchor's plane in the anchor's local XY (<c>MRUKAnchor.PlaneBoundary2D</c>), or the plane
        /// rectangle when MRUK has no outline (<see cref="PlaneIsRectApproximation"/>). Null when the anchor has no plane.
        /// A seat is registered only from a plane: its height is the plane's, never a guess.
        /// </summary>
        public Vector2[] PlaneBoundary;
        public bool PlaneIsRectApproximation;
    }

    public interface ISceneAnchorCatalog
    {
        IReadOnlyList<SceneAnchorRecord> Anchors { get; }
    }

    /// <summary>
    /// A catalog that knows which room its anchors belong to. State kept per room (seat links, a shared table) is
    /// scoped to this key and dropped when it changes. Empty when no room is confirmed.
    /// </summary>
    public interface IRoomScopedCatalog
    {
        string RoomKey { get; }
    }

    public interface ISceneAnchorPublisher
    {
        void Publish(GameObject anchor, bool shareWithPeers);
    }

    public static class SceneAnchorSelection
    {
        public static bool TrySelectBiggest(IReadOnlyList<SceneAnchorRecord> anchors, string label,
                                            out SceneAnchorRecord selected)
        {
            selected = null;
            if (anchors == null || string.IsNullOrEmpty(label))
            {
                return false;
            }
            float best = float.NegativeInfinity;
            for (int i = 0; i < anchors.Count; ++i)
            {
                SceneAnchorRecord candidate = anchors[i];
                if (candidate == null || candidate.Anchor == null ||
                    !string.Equals(candidate.Label, label, System.StringComparison.Ordinal) ||
                    candidate.Size <= best)
                {
                    continue;
                }
                best = candidate.Size;
                selected = candidate;
            }
            return selected != null;
        }
    }

    public sealed class ListSceneAnchorCatalog : ISceneAnchorCatalog
    {
        private readonly List<SceneAnchorRecord> _anchors = new List<SceneAnchorRecord>();
        public IReadOnlyList<SceneAnchorRecord> Anchors => _anchors;
        public void Add(SceneAnchorRecord record) => _anchors.Add(record);
    }
}
