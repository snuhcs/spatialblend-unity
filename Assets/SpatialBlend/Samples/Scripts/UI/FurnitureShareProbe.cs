// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// A trigger the size of a furniture anchor's volume, on <see cref="FurnitureShareMenu.ProbeLayerName"/>, so the furniture
    /// menu's ray hits the furniture when the effect-mesh colliders are off. It is a child of the anchor; the lobby creates it
    /// and the connect destroys it.
    /// </summary>
    public sealed class FurnitureShareProbe : MonoBehaviour
    {
        public const string ObjectName = "FurnitureShareProbe";

        public BoxCollider Box { get; private set; }

        public static FurnitureShareProbe Ensure(Transform anchor, Vector3 center, Vector3 size)
        {
            if (anchor == null) return null;
            Transform existing = anchor.Find(ObjectName);
            FurnitureShareProbe probe = existing != null ? existing.GetComponent<FurnitureShareProbe>() : null;
            if (probe == null)
            {
                var go = new GameObject(ObjectName);
                go.transform.SetParent(anchor, false);
                probe = go.AddComponent<FurnitureShareProbe>();
                probe.Box = go.AddComponent<BoxCollider>();
                probe.Box.isTrigger = true;
            }
            int layer = FurnitureShareMenu.ProbeLayer;
            if (layer >= 0) probe.gameObject.layer = layer;
            probe.Box.center = center;
            probe.Box.size = size;
            return probe;
        }

        public static void Remove(Transform anchor)
        {
            Transform existing = anchor != null ? anchor.Find(ObjectName) : null;
            if (existing == null) return;
            if (Application.isPlaying) Destroy(existing.gameObject);
            else DestroyImmediate(existing.gameObject);
        }
    }
}
