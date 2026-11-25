// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using Meta.XR;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Abstract base class providing shared infrastructure for all annotation motifs.
    /// Provides raycast utilities, visual factories, config access, and completion events.
    /// Subclasses: FloorVertexCollectionMotif → BoxAnnotationMotif, AffordanceAnnotationMotif
    /// </summary>
    public abstract class AnnotationMotifBase : InteractionMotif
    {
        /// <summary>
        /// Event fired when annotation is completed (finalized or cancelled).
        /// Parameter: true if finalized successfully, false if cancelled.
        /// </summary>
        public event Action<AnnotationMotifBase, bool> OnAnnotationComplete;

        [Header("Settings")]
        [SerializeField] protected OVRInput.Controller _controller = OVRInput.Controller.RTouch;

        // Auto-found references
        protected Transform _raycastAnchor;
        protected Transform _centerEyeAnchor;
        protected OVRHand _hand;
        protected EnvironmentRaycastManager _raycastManager;
        protected Transform _rayOriginOverride;

        // Input settings
        // Trigger or pinch, and B or the Undo button (see EditInput).
        protected static bool AcceptDown => EditInput.AcceptDown;
        protected static bool AcceptUp => EditInput.AcceptUp;
        protected static bool UndoDown => EditInput.UndoDown;

        // Raycast settings
        protected float _maxRaycastDistance => 10f;
        protected float _closeLoopThreshold => 0.15f;
        protected LayerMask _raycastLayerMask => -1;
        protected float _groundNormalThreshold => 0.6f;

        /// <summary>
        /// Configure the motif with external references.
        /// Called by managers when dynamically instantiating motifs.
        /// </summary>
        public virtual void Configure(Transform raycastAnchor, Transform centerEyeAnchor, EnvironmentRaycastManager raycastManager, OVRHand hand = null, OVRInput.Controller controller = OVRInput.Controller.RTouch, Transform rayOriginOverride = null)
        {
            if (_raycastAnchor == null && raycastAnchor != null)
            {
                _raycastAnchor = raycastAnchor;
            }
            if (_centerEyeAnchor == null && centerEyeAnchor != null)
            {
                _centerEyeAnchor = centerEyeAnchor;
            }
            if (_raycastManager == null && raycastManager != null)
            {
                _raycastManager = raycastManager;
            }
            if (_hand == null && hand != null)
            {
                _hand = hand;
            }
            _controller = controller;

            // Ray origin override takes priority over all other ray sources
            if (rayOriginOverride != null)
            {
                _rayOriginOverride = rayOriginOverride;
            }
        }

        // === Unity Lifecycle ===

        protected virtual void Awake()
        {
            // Auto-find OVRCameraRig anchors
            if (_centerEyeAnchor == null || _raycastAnchor == null)
            {
                var cameraRig = FindAnyObjectByType<OVRCameraRig>();
                if (cameraRig != null)
                {
                    if (_centerEyeAnchor == null)
                        _centerEyeAnchor = cameraRig.centerEyeAnchor;

                    if (_raycastAnchor == null)
                    {
                        _raycastAnchor = _controller == OVRInput.Controller.LTouch
                            ? cameraRig.leftControllerAnchor
                            : cameraRig.rightControllerAnchor;
                    }
                }
                else
                {
                    // Fallback to Camera.main
                    if (_centerEyeAnchor == null && Camera.main != null)
                        _centerEyeAnchor = Camera.main.transform;
                    if (_raycastAnchor == null)
                        _raycastAnchor = _centerEyeAnchor;

                    if (_debugLog)
                        Debug.LogWarning($"[{GetType().Name}] OVRCameraRig not found. Using Camera.main fallback.");
                }
            }

            // Auto-find EnvironmentRaycastManager
            _raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>();

            // Auto-find OVRHand for hand tracking
            var hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            foreach (var h in hands)
            {
                if ((_controller == OVRInput.Controller.RTouch && h.GetHand() == OVRPlugin.Hand.HandRight) ||
                    (_controller == OVRInput.Controller.LTouch && h.GetHand() == OVRPlugin.Hand.HandLeft))
                {
                    _hand = h;
                    break;
                }
            }
        }

        // === Completion Event ===

        /// <summary>
        /// Fire the completion event. Call this when annotation is finalized or cancelled.
        /// </summary>
        protected void FireAnnotationComplete(bool success)
        {
            OnAnnotationComplete?.Invoke(this, success);
        }

        /// <summary>
        /// Cancel any active annotation creation.
        /// Called by external systems (e.g., ManipulationContext) to cancel annotation.
        /// Subclasses should override to implement proper cancellation behavior.
        /// </summary>
        public virtual void CancelAnnotation()
        {
            // Default implementation just fires the complete event with failure.
            // Subclasses override to do proper state cleanup.
            FireAnnotationComplete(false);
        }

        // === Raycast Helpers ===

        protected Ray GetControllerRay()
        {
            // Priority 1: the active editing input's ISDK ray, the controller's or the hand's (matches the visual cursor)
            var pointer = EditInput.ActivePointer;
            if (pointer != null)
            {
                return pointer.Ray;
            }

            // Priority 2: Use explicit ray origin override
            if (_rayOriginOverride != null)
            {
                return new Ray(_rayOriginOverride.position, _rayOriginOverride.forward);
            }

            // Priority 3: Use hand/controller tracking via RaycastUtils
            if (_hand != null || OVRInput.IsControllerConnected(_controller))
            {
                return RaycastUtils.GetRay(_hand, _controller, _raycastAnchor ?? _centerEyeAnchor);
            }

            // Fallback to anchor transforms
            if (_raycastAnchor != null)
            {
                return new Ray(_raycastAnchor.position, _raycastAnchor.forward);
            }
            if (_centerEyeAnchor != null)
            {
                return new Ray(_centerEyeAnchor.position, _centerEyeAnchor.forward);
            }
            return new Ray(Vector3.zero, Vector3.forward);
        }

        protected bool TryRaycastGround(Ray ray, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;

            // Use EnvironmentRaycastUtility which handles depth manager checks and MRUK fallback
            if (EnvironmentRaycastUtility.RaycastFloor(ray, out var result, _maxRaycastDistance, _groundNormalThreshold))
            {
                point = result.Point;
                normal = result.Normal;
                return true;
            }

            // Fallback to physics raycast
            if (Physics.Raycast(ray, out var physicsHit, _maxRaycastDistance, _raycastLayerMask))
            {
                // Only accept if it's ground-like
                if (Vector3.Dot(physicsHit.normal, Vector3.up) >= _groundNormalThreshold)
                {
                    point = physicsHit.point;
                    normal = physicsHit.normal;
                    return true;
                }
            }

            // No invented floor: outside every room, and with no physics ground, there is no ground point
            // (v1 fell back to a plane at world y = 0).
            return false;
        }

        protected bool TryRaycastOnPlane(Ray ray, Plane plane, out Vector3 point)
        {
            point = Vector3.zero;

            if (plane.Raycast(ray, out float enter) && enter <= _maxRaycastDistance)
            {
                point = ray.GetPoint(enter);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a surface normal is horizontal enough to be considered ground.
        /// </summary>
        protected bool IsGroundSurface(Vector3 normal)
        {
            return Vector3.Dot(normal, Vector3.up) >= _groundNormalThreshold;
        }
    }
}
