// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System.Diagnostics;

namespace SpatialBlend
{
    public static class SpatialBlendDebug
    {
        [System.Flags]
        public enum LogCategory
        {
            None = 0,
            Connection = 1 << 0,    // Space creation, connection, disconnection
            Scene = 1 << 1,         // Share, remove operations
            State = 1 << 2,         // State updates
            Data = 1 << 3,          // Data updates
            Geometry = 1 << 4,      // Geometry registration
            Interaction = 1 << 5,   // Interaction operations
            All = ~0
        }

        private static LogCategory enabledCategories = LogCategory.All;
        private static bool isLoggingEnabled = true;
        private static SpatialBlendSettings spatialBlendSettings;

        /// <summary>
        /// Initialize the debug system with SpatialBlend settings
        /// </summary>
        public static void Initialize(SpatialBlendSettings settings = null)
        {
            spatialBlendSettings = settings;
            if (spatialBlendSettings != null)
            {
                isLoggingEnabled = spatialBlendSettings.enableDebugLogging;
                RuntimeWorkCounters.LogEnabled = spatialBlendSettings.logRuntimeWork;
            }
        }

        /// <summary>
        /// Check if logging is globally enabled
        /// </summary>
        public static bool IsLoggingEnabled => isLoggingEnabled;

        /// <summary>
        /// Enable or disable all logging
        /// </summary>
        public static void SetLoggingEnabled(bool enabled)
        {
            isLoggingEnabled = enabled;
        }

        public static void EnableCategory(LogCategory category)
        {
            enabledCategories |= category;
        }

        public static void DisableCategory(LogCategory category)
        {
            enabledCategories &= ~category;
        }

        public static void SetEnabledCategories(LogCategory categories)
        {
            enabledCategories = categories;
        }

        public static LogCategory GetEnabledCategories()
        {
            return enabledCategories;
        }

        public static bool IsCategoryEnabled(LogCategory category)
        {
            return isLoggingEnabled && (enabledCategories & category) != 0;
        }

        /// <summary>
        /// Log with category filtering - preserves source code linking in Unity Console
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Log(LogCategory category, string message)
        {
            if (IsCategoryEnabled(category))
            {
                UnityEngine.Debug.Log($"[SpatialBlend][{category}] {message}");
            }
        }

        /// <summary>
        /// Log warning with category filtering - preserves source code linking in Unity Console
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void LogWarning(LogCategory category, string message)
        {
            if (IsCategoryEnabled(category))
            {
                UnityEngine.Debug.LogWarning($"[SpatialBlend][{category}] {message}");
            }
        }

        /// <summary>
        /// Log error with category filtering - preserves source code linking in Unity Console
        /// Note: Errors are always logged regardless of build configuration
        /// </summary>
        public static void LogError(LogCategory category, string message)
        {
            if (IsCategoryEnabled(category))
            {
                UnityEngine.Debug.LogError($"[SpatialBlend][{category}] {message}");
            }
        }

        /// <summary>
        /// Force log a message regardless of category settings (useful for critical info)
        /// Only active in editor and development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void ForceLog(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend][CRITICAL] {message}");
            }
        }

        /// <summary>
        /// Force log an error regardless of category settings
        /// Always active (errors should always be visible)
        /// </summary>
        public static void ForceLogError(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogError($"[SpatialBlend][CRITICAL] {message}");
            }
        }

        // Convenience methods that directly call Unity's Debug methods when enabled
        // These preserve source code linking while still respecting our logging settings

        /// <summary>
        /// Direct log - only use for temporary debugging, preserves source linking
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void DirectLog(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend] {message}");
            }
        }

        /// <summary>
        /// Direct warning - preserves source linking
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void DirectLogWarning(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogWarning($"[SpatialBlend] {message}");
            }
        }

        /// <summary>
        /// Direct error - preserves source linking, always active
        /// </summary>
        public static void DirectLogError(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogError($"[SpatialBlend] {message}");
            }
        }
    }
}