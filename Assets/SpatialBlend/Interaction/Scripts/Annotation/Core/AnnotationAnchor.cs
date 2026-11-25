// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Represents a point anchor for annotations.
    /// Stores world position, surface normal, and optional reference to hit object
    /// for stable updates if the object moves.
    /// </summary>
    [System.Serializable]
    public struct AnnotationAnchor
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Normal;
        public float Timestamp;

        // Optional: reference to hit object for tracking
        [System.NonSerialized]
        public Transform HitObject;

        // Local position relative to hit object (for re-projection)
        public Vector3 LocalPosition;
        public bool HasHitObject;

        public AnnotationAnchor(Vector3 position, Vector3 normal, Transform hitObject = null)
        {
            Position = position;
            Normal = normal;
            Rotation = normal != Vector3.zero
                ? Quaternion.LookRotation(Vector3.forward, normal)
                : Quaternion.identity;
            Timestamp = Time.time;
            HitObject = hitObject;
            HasHitObject = hitObject != null;

            if (hitObject != null)
            {
                LocalPosition = hitObject.InverseTransformPoint(position);
            }
            else
            {
                LocalPosition = Vector3.zero;
            }
        }

        /// <summary>
        /// Get the current world position, accounting for object movement.
        /// </summary>
        public Vector3 GetWorldPosition()
        {
            if (HasHitObject && HitObject != null)
            {
                return HitObject.TransformPoint(LocalPosition);
            }
            return Position;
        }

        /// <summary>
        /// Update the anchor position (for editing).
        /// </summary>
        public void UpdatePosition(Vector3 newPosition)
        {
            Position = newPosition;
            if (HasHitObject && HitObject != null)
            {
                LocalPosition = HitObject.InverseTransformPoint(newPosition);
            }
        }

        /// <summary>
        /// Create an anchor from a raycast hit.
        /// </summary>
        public static AnnotationAnchor FromRaycastHit(RaycastHit hit)
        {
            return new AnnotationAnchor(hit.point, hit.normal, hit.transform);
        }

        /// <summary>
        /// Create an anchor from environment raycast hit (no transform reference).
        /// </summary>
        public static AnnotationAnchor FromEnvironmentHit(Vector3 point, Vector3 normal)
        {
            return new AnnotationAnchor(point, normal, null);
        }
    }
}
