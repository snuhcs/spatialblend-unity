// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Meta.XR.MRUtilityKit;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Unified utility for environment raycasting with automatic fallback.
    /// Priority: EnvironmentRaycastManager (depth) → MRUK Room anchors.
    /// </summary>
    public static class EnvironmentRaycastUtility
    {
        private static EnvironmentRaycastManager s_RaycastManager;
        private static EnvironmentDepthManager s_DepthManager;

        /// <summary>
        /// Result of an environment raycast.
        /// </summary>
        public struct RaycastResult
        {
            public bool Hit;
            public Vector3 Point;
            public Vector3 Normal;
            public float Distance;
            public RaycastSource Source;
        }

        /// <summary>
        /// Source of the raycast hit.
        /// </summary>
        public enum RaycastSource
        {
            None,
            EnvironmentDepth,
            MRUKFloor,
            MRUKWall,
            MRUKAny
        }

        /// <summary>
        /// Get or find the EnvironmentRaycastManager in the scene.
        /// Automatically retries if the cached reference is null or destroyed.
        /// </summary>
        public static EnvironmentRaycastManager RaycastManager
        {
            get
            {
                if (s_RaycastManager == null)
                {
                    s_RaycastManager = Object.FindAnyObjectByType<EnvironmentRaycastManager>();
                }
                return s_RaycastManager;
            }
        }

        /// <summary>
        /// Get or find the EnvironmentDepthManager in the scene.
        /// Automatically retries if the cached reference is null or destroyed.
        /// </summary>
        private static EnvironmentDepthManager DepthManager
        {
            get
            {
                if (s_DepthManager == null)
                {
                    s_DepthManager = Object.FindAnyObjectByType<EnvironmentDepthManager>();
                }
                return s_DepthManager;
            }
        }

        /// <summary>
        /// Check if depth-based raycasting is available (EnvironmentDepthManager enabled).
        /// </summary>
        private static bool IsDepthRaycastReady
        {
            get
            {
                var depthMgr = DepthManager;
                if (depthMgr == null || !depthMgr.enabled || !depthMgr.gameObject.activeInHierarchy)
                {
                    return false;
                }
                var raycastMgr = RaycastManager;
                return raycastMgr != null && raycastMgr.enabled && raycastMgr.gameObject.activeInHierarchy;
            }
        }

        /// <summary>
        /// Check if environment raycasting is available (either depth or MRUK).
        /// </summary>
        public static bool IsReady
        {
            get
            {
                // Check if depth-based raycast is ready
                if (IsDepthRaycastReady)
                {
                    return true;
                }
                // MRUK is our fallback - check for current room or any rooms (editor)
                var mruk = MRUK.Instance;
                if (mruk == null) return false;
                return mruk.GetCurrentRoom() != null || mruk.Rooms.Count > 0;
            }
        }

        /// <summary>
        /// Clear cached references. Call when scene changes.
        /// Note: The getters now automatically retry when references are null,
        /// so this is only needed for explicit cache invalidation.
        /// </summary>
        public static void ClearCache()
        {
            s_RaycastManager = null;
            s_DepthManager = null;
        }

        /// <summary>
        /// Raycast against environment surfaces (any surface type).
        /// Falls back to MRUK if EnvironmentRaycastManager is not available.
        /// Does NOT fall back to Physics.Raycast - only hits environment mesh.
        /// </summary>
        public static bool Raycast(Ray ray, out RaycastResult result, float maxDistance = 10f)
        {
            result = new RaycastResult();

            // Priority 1: EnvironmentRaycastManager (depth-based)
            if (TryEnvironmentRaycast(ray, out result, maxDistance))
            {
                return true;
            }

            // Priority 2: MRUK room raycast (any anchor)
            if (TryMRUKRaycast(ray, out result, maxDistance, LabelFilter.Included(MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.WALL_FACE)))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Raycast against floor surfaces only.
        /// Falls back to MRUK floor anchor if EnvironmentRaycastManager is not available.
        /// Does NOT fall back to Physics.Raycast - only hits environment floor.
        /// </summary>
        public static bool RaycastFloor(Ray ray, out RaycastResult result, float maxDistance = 10f, float groundNormalThreshold = 0.6f)
        {
            result = new RaycastResult();

            // Priority 1: EnvironmentRaycastManager with ground normal check
            if (TryEnvironmentRaycast(ray, out result, maxDistance))
            {
                if (Vector3.Dot(result.Normal, Vector3.up) >= groundNormalThreshold)
                {
                    return true;
                }
                // Hit something but not ground-like, continue to MRUK fallback
            }

            // Priority 2: MRUK floor anchor only
            if (TryMRUKRaycast(ray, out result, maxDistance, LabelFilter.Included(MRUKAnchor.SceneLabels.FLOOR), RaycastSource.MRUKFloor))
            {
                if (Vector3.Dot(result.Normal, Vector3.up) >= groundNormalThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Raycast against wall surfaces only.
        /// </summary>
        public static bool RaycastWall(Ray ray, out RaycastResult result, float maxDistance = 10f, float wallNormalThreshold = 0.7f)
        {
            result = new RaycastResult();

            // Priority 1: EnvironmentRaycastManager with wall normal check
            if (TryEnvironmentRaycast(ray, out result, maxDistance))
            {
                float verticalDot = Mathf.Abs(Vector3.Dot(result.Normal, Vector3.up));
                if (verticalDot < (1f - wallNormalThreshold))
                {
                    return true;
                }
            }

            // Priority 2: MRUK wall anchors
            if (TryMRUKRaycast(ray, out result, maxDistance, LabelFilter.Included(MRUKAnchor.SceneLabels.WALL_FACE), RaycastSource.MRUKWall))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Raycast using EnvironmentRaycastManager (depth-based).
        /// Only calls raycast if EnvironmentDepthManager is enabled to avoid error spam.
        /// </summary>
        private static bool TryEnvironmentRaycast(Ray ray, out RaycastResult result, float maxDistance)
        {
            result = new RaycastResult();

            // Check if depth raycast is ready before calling to avoid "Please enable EnvironmentDepthManager" errors
            if (!IsDepthRaycastReady)
            {
                return false;
            }

            var manager = RaycastManager;
            if (manager.Raycast(ray, out var hit, maxDistance))
            {
                result.Hit = true;
                result.Point = hit.point;
                result.Normal = hit.normal;
                result.Distance = Vector3.Distance(ray.origin, hit.point);
                result.Source = RaycastSource.EnvironmentDepth;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Raycast using MRUK room anchors.
        /// Falls back to checking all rooms if GetCurrentRoom() returns null (common in editor).
        /// </summary>
        private static bool TryMRUKRaycast(Ray ray, out RaycastResult result, float maxDistance, LabelFilter labelFilter, RaycastSource source = RaycastSource.MRUKAny)
        {
            result = new RaycastResult();

            var mruk = MRUK.Instance;
            if (mruk == null) return false;

            // Try current room first
            var room = mruk.GetCurrentRoom();
            if (room != null)
            {
                if (room.Raycast(ray, maxDistance, labelFilter, out var hit, out var anchor))
                {
                    result.Hit = true;
                    result.Point = hit.point;
                    result.Normal = hit.normal;
                    result.Distance = hit.distance;
                    result.Source = source;
                    return true;
                }
            }

            // Fallback: try all rooms (for editor where GetCurrentRoom may return null)
            foreach (var r in mruk.Rooms)
            {
                if (r == room) continue; // Skip if already tried

                if (r.Raycast(ray, maxDistance, labelFilter, out var hit, out var anchor))
                {
                    result.Hit = true;
                    result.Point = hit.point;
                    result.Normal = hit.normal;
                    result.Distance = hit.distance;
                    result.Source = source;
                    return true;
                }
            }

            return false;
        }
    }
}
