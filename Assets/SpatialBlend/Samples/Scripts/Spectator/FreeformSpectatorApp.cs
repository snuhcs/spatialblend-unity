// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The app of a desktop spectator. It receives what the room shares and shows it, and does nothing else: it registers no
    /// avatar, no actor, no embodiment, no scene geometry of its own, no native entity, and it declares no anchor or relation,
    /// so it cannot alter the participants' relation solve. Compare <see cref="FreeformCollaborationApp"/>, whose OnAppStart
    /// registers all of those.
    /// </summary>
    public class FreeformSpectatorApp : SbApp
    {
        private readonly List<GameObject> _received = new List<GameObject>();
        private bool _started;
        private int _addedEver;

        /// <summary>Raised for every object the room delivers and this client accepted.</summary>
        public event Action<GameObject> ObjectArrived;
        public event Action<GameObject> ObjectRemoved;

        public override bool IsAppStarted => _started;
        public IReadOnlyList<GameObject> ReceivedObjects => _received;
        public int ReceivedCount => _received.Count;
        public int AddedEver => _addedEver;
        public bool ReceiveAvatarsAsActors { get; set; }

        /// <summary>Test seam: a spectator app needs no manager when nothing is started.</summary>
        internal void SetSpaceManager(SpatialBlendManager manager) => _spaceManager = manager;

        public override void OnAppStart()
        {
            // Nothing to register. The room's content arrives through OnShare/OnObjectAdded.
            _started = true;
        }

        public override void OnAppStop()
        {
            _started = false;
            _received.Clear();
            base.OnAppStop();
        }

        public override OnShareResponse OnShare(GameObject gameObject)
        {
            bool avatar = gameObject != null && (gameObject.CompareTag("Avatar") ||
                gameObject.GetComponent<MetaAvatarEntity>() != null || gameObject.GetComponent<SbAvatarEntity>() != null);
            return SpectatorJoinPolicy.ShareResponse(avatar, ReceiveAvatarsAsActors);
        }

        public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType onObjectAddedType)
        {
            if (gameObject == null || onObjectAddedType != OnObjectAddedType.Added) return;
            if (_received.Contains(gameObject)) return;
            _received.Add(gameObject);
            _addedEver++;
            // A shared real object (a scene anchor such as a linked TABLE) has no geometry of its own here, and its pose in the
            // spectator's own space is meaningless (no room): the room view draws it at its true pose and size in the viewed room,
            // in the shared-real look. It used to get a gray 15 cm marker at that meaningless pose.
            if (!IsSharedSceneAnchor(gameObject)) SpectatorMarker.EnsureVisible(gameObject);
            ObjectArrived?.Invoke(gameObject);
        }

        public override void OnRemove(GameObject gameObject)
        {
            if (gameObject == null) return;
            if (_received.Remove(gameObject)) ObjectRemoved?.Invoke(gameObject);
        }

        /// <summary>A received shared scene anchor (an MRUK anchor's entity, named by <see cref="SharedAnchorName"/>).</summary>
        public static bool IsSharedSceneAnchor(GameObject go) =>
            go != null && (go.GetComponent<SbMRUKAnchorEntity>() != null || SharedAnchorName.TryParse(go.name, out _, out _, out _, out _));

        /// <summary>World bounds of everything shown so far, for framing the camera. False when nothing has a renderer yet.</summary>
        public bool TryGetContentBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var go in _received)
            {
                if (go == null || IsSharedSceneAnchor(go)) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    // Hidden by the room view: the viewed room does not place it, so its spectator-space pose is not framed.
                    if (!r.enabled) continue;
                    if (!any) { bounds = r.bounds; any = true; }
                    else bounds.Encapsulate(r.bounds);
                }
            }

            return any;
        }
    }

    /// <summary>A scene anchor or other content with no geometry of its own is drawn as a small labelled marker so the spectator sees it.</summary>
    internal static class SpectatorMarker
    {
        public static void EnsureVisible(GameObject go)
        {
            if (go.GetComponentInChildren<Renderer>() != null) return;
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "SpectatorMarker";
            var collider = marker.GetComponent<Collider>();
            if (collider != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(collider); else UnityEngine.Object.DestroyImmediate(collider); }
            marker.transform.SetParent(go.transform, false);
            marker.transform.localScale = Vector3.one * 0.15f;
        }
    }
}
