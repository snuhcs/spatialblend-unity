// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Utility class for getting ray origin that works for both hand tracking and controllers.
    /// Follows the pattern from PointerHandlerMotif for unified input handling.
    /// </summary>
    public static class RaycastUtils
    {
        public enum RaySource
        {
            Hand,
            Controller,
            Fallback
        }

        // Cached OVRCameraRig reference for transforming tracking-space to world-space
        private static OVRCameraRig s_CameraRig;

        /// <summary>
        /// Gets a ray from the right hand or controller, with automatic fallback.
        /// Priority: Hand tracking > Controller > Center eye anchor
        /// All rays are returned in world space.
        /// </summary>
        /// <param name="hand">OVRHand reference for hand tracking</param>
        /// <param name="source">Output: which source was used</param>
        /// <param name="controller">Controller type (default: RTouch)</param>
        /// <param name="fallbackAnchor">Fallback transform if neither hand nor controller available</param>
        /// <returns>Ray from the appropriate source in world space</returns>
        public static Ray GetRay(
            OVRHand hand,
            out RaySource source,
            OVRInput.Controller controller = OVRInput.Controller.RTouch,
            Transform fallbackAnchor = null)
        {
            // Priority 1: Hand tracking if available (PointerPose is already in world space)
            if (hand != null && hand.IsTracked && hand.PointerPose != null)
            {
                source = RaySource.Hand;
                return new Ray(hand.PointerPose.position, hand.PointerPose.forward);
            }

            // Priority 2: Controller
            // Note: OVRInput.GetLocalControllerPosition/Rotation return tracking-space coordinates.
            // We need to transform them to world space using OVRCameraRig's tracking space.
            if (OVRInput.IsControllerConnected(controller))
            {
                source = RaySource.Controller;
                var localPos = OVRInput.GetLocalControllerPosition(controller);
                var localRot = OVRInput.GetLocalControllerRotation(controller);

                // Transform from tracking-space to world-space
                var trackingSpace = GetTrackingSpaceTransform();
                if (trackingSpace != null)
                {
                    var worldPos = trackingSpace.TransformPoint(localPos);
                    var worldDir = trackingSpace.TransformDirection(localRot * Vector3.forward);
                    return new Ray(worldPos, worldDir);
                }

                // Fallback if no tracking space found (shouldn't happen in normal use)
                return new Ray(localPos, localRot * Vector3.forward);
            }

            // Priority 3: Fallback anchor (e.g., center eye) - already in world space
            if (fallbackAnchor != null)
            {
                source = RaySource.Fallback;
                return new Ray(fallbackAnchor.position, fallbackAnchor.forward);
            }

            // Last resort
            source = RaySource.Fallback;
            return new Ray(Vector3.zero, Vector3.forward);
        }

        /// <summary>
        /// Gets the tracking space transform from OVRCameraRig.
        /// Cached for performance.
        /// </summary>
        private static Transform GetTrackingSpaceTransform()
        {
            if (s_CameraRig == null)
            {
                s_CameraRig = Object.FindAnyObjectByType<OVRCameraRig>();
            }
            return s_CameraRig?.trackingSpace;
        }

        /// <summary>
        /// Clear cached references. Call when scene changes.
        /// </summary>
        public static void ClearCache()
        {
            s_CameraRig = null;
        }

        /// <summary>
        /// Simplified overload that doesn't return the source.
        /// </summary>
        public static Ray GetRay(
            OVRHand hand,
            OVRInput.Controller controller = OVRInput.Controller.RTouch,
            Transform fallbackAnchor = null)
        {
            return GetRay(hand, out _, controller, fallbackAnchor);
        }

        /// <summary>
        /// Gets a ray using only a transform anchor (for simple cases).
        /// </summary>
        public static Ray GetRay(Transform anchor)
        {
            if (anchor != null)
            {
                return new Ray(anchor.position, anchor.forward);
            }
            return new Ray(Vector3.zero, Vector3.forward);
        }

        /// <summary>
        /// Gets rays for both hands/controllers.
        /// </summary>
        public static (Ray left, Ray right) GetRays(
            OVRHand leftHand,
            OVRHand rightHand,
            Transform fallbackAnchor = null)
        {
            var leftRay = GetRay(leftHand, OVRInput.Controller.LTouch, fallbackAnchor);
            var rightRay = GetRay(rightHand, OVRInput.Controller.RTouch, fallbackAnchor);
            return (leftRay, rightRay);
        }
    }
}
