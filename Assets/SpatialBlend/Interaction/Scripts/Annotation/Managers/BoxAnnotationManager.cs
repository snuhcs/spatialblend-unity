// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using SpatialBlend.Interactions.Annotation;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Scene singleton that manages BoxAnnotation triggering.
    /// Uses EnvironmentSurfaceManager for floor detection via ISDK events.
    /// ISDK RayInteractor provides visual cursor feedback.
    /// </summary>
    public class BoxAnnotationManager : MonoBehaviour
    {
        /// <summary>
        /// Raised once a box creation ended: true when the user finished all the points and the box exists, false when it was
        /// cancelled. The lobby tutorial waits for it, and any other listener may.
        /// </summary>
        public event Action<bool> BoxCompleted;

        private static readonly System.Collections.Generic.HashSet<object> s_CreationHolds = new System.Collections.Generic.HashSet<object>();

        /// <summary>
        /// Stops new boxes from starting while <paramref name="owner"/> holds it (the capture card: a trigger press then captures,
        /// and must not also start a box on the floor). Release with <see cref="ReleaseCreation"/>.
        /// </summary>
        public static void HoldCreation(object owner) { if (owner != null) s_CreationHolds.Add(owner); }

        public static void ReleaseCreation(object owner) { if (owner != null) s_CreationHolds.Remove(owner); }

        private static bool s_BoxActive;
        private static int s_BusyUntilFrame = -1;

        /// <summary>
        /// A box is being drawn (its corner presses are the box's), or one finished a moment ago (the same press may still be in flight).
        /// Other press handlers, such as the furniture menu, stand aside.
        /// </summary>
        public static bool BoxBusy => s_BoxActive || Time.frameCount <= s_BusyUntilFrame;

        /// <summary>Whether something holds box creation.</summary>
        public static bool CreationHeld => s_CreationHolds.Count > 0;

        // EnvironmentSurfaceManager for floor ISDK events
        private EnvironmentSurfaceManager _surfaceManager;

        [Header("Settings")]
        [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.RTouch;

        // Auto-found references
        private Transform _raycastAnchor;
        private Transform _centerEyeAnchor;
        private OVRHand _hand;

        private BoxAnnotationMotif _activeMotif;
        private int _cooldownFrames = 0; // Prevent immediate re-triggering after completion

        private void Awake()
        {
            // Auto-find OVRCameraRig anchors
            var cameraRig = FindAnyObjectByType<OVRCameraRig>();
            if (cameraRig != null)
            {
                _centerEyeAnchor = cameraRig.centerEyeAnchor;
                _raycastAnchor = _controller == OVRInput.Controller.LTouch
                    ? cameraRig.leftControllerAnchor
                    : cameraRig.rightControllerAnchor;
            }
            else
            {
                if (Camera.main != null)
                    _centerEyeAnchor = Camera.main.transform;
                _raycastAnchor = _centerEyeAnchor;
            }

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

            // Find EnvironmentSurfaceManager for floor ISDK events
            _surfaceManager = FindAnyObjectByType<EnvironmentSurfaceManager>();
            if (_surfaceManager != null)
            {
                _surfaceManager.OnFloorSelected += OnFloorSelected;
            }
            else
            {
                Debug.LogWarning("[BoxAnnotationManager] EnvironmentSurfaceManager not found. Floor selection won't work.");
            }
        }

        // === EnvironmentSurfaceManager Event Handlers ===

        private void OnFloorSelected(MRUKAnchor anchor, Vector3 hitPoint)
        {
            // Defensive cleanup for stale motif references that are already idle.
            if (_activeMotif != null && _activeMotif.IsIdleState)
            {
                CleanupActiveMotif();
            }

            // Don't start new annotation if already creating
            if (_activeMotif != null)
            {
                return;
            }

            // Cooldown prevents immediate re-triggering after annotation completes
            if (_cooldownFrames > 0)
            {
                return;
            }

            // A press whose ray first meets a furniture-menu probe is the furniture menu's press, in any mode, never an annotation: one
            // press is never both.
            if (RayFirstHitsFurnitureProbe(hitPoint)) return;

            // Only active in SceneEdit mode
            if (!InteractionModeManager.IsMode(InteractionMode.SceneEdit))
            {
                return;
            }

            // The capture card is open: its trigger press captures.
            if (CreationHeld)
            {
                return;
            }

            // Note: If EnvironmentSurfaceManager has an active handler (e.g., AffordanceAnnotationMotif),
            // this event won't fire at all - the active handler receives it instead.

            StartBoxCreation(hitPoint, Vector3.up);
        }

        /// <summary>The furniture-menu probes' object name and layer (see FurnitureShareProbe); the Samples assembly owns them, so they are matched by name.</summary>
        public const string FurnitureProbeObjectName = "FurnitureShareProbe";
        private const string FurnitureProbeLayerName = "Ignore Raycast";

        /// <summary>True when the active ray meets a furniture probe before it reaches <paramref name="hitPoint"/>.</summary>
        public static bool RayFirstHitsFurnitureProbe(Vector3 hitPoint)
        {
            Ray? ray = EditInput.ActiveRay;
            int layer = LayerMask.NameToLayer(FurnitureProbeLayerName);
            if (ray == null || layer < 0) return false;
            float limit = Vector3.Distance(ray.Value.origin, hitPoint);
            foreach (RaycastHit hit in Physics.RaycastAll(ray.Value, limit, 1 << layer, QueryTriggerInteraction.Collide))
                if (hit.collider.gameObject.name == FurnitureProbeObjectName) return true;
            return false;
        }

        private void Update()
        {
            if (_cooldownFrames > 0)
                _cooldownFrames--;
        }

        private void StartBoxCreation(Vector3 hitPoint, Vector3 normal)
        {
            var go = new GameObject("BoxAnnotationMotif_Active");
            _activeMotif = go.AddComponent<BoxAnnotationMotif>();
            s_BoxActive = true;

            // Configure with raycast references
            _activeMotif.Configure(
                _raycastAnchor,
                _centerEyeAnchor,
                null, // No longer using EnvironmentRaycastManager
                _hand,
                _controller,
                rayOriginOverride: null
            );

            _activeMotif.OnAnnotationComplete += HandleAnnotationComplete;
            _activeMotif.BeginCreationFromExternal(hitPoint, normal);
        }

        /// <summary>The motif's completion callback. Public so the hook can be driven without a running motif.</summary>
        public void HandleAnnotationComplete(AnnotationMotifBase motif, bool success)
        {
            CleanupActiveMotif();
            s_BusyUntilFrame = Time.frameCount + 2;

            // Set cooldown to prevent the same click from starting a new annotation
            _cooldownFrames = 2;
            BoxCompleted?.Invoke(success);
        }

        private void CleanupActiveMotif()
        {
            s_BoxActive = false;
            if (_activeMotif != null)
            {
                _activeMotif.OnAnnotationComplete -= HandleAnnotationComplete;
                Destroy(_activeMotif.gameObject);
                _activeMotif = null;
            }
        }

        private void OnDestroy()
        {
            // Unsubscribe from EnvironmentSurfaceManager events
            if (_surfaceManager != null)
            {
                _surfaceManager.OnFloorSelected -= OnFloorSelected;
            }

            CleanupActiveMotif();
        }
    }
}
