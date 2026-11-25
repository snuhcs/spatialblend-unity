// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System.Diagnostics;

namespace SpatialBlend
{
    /// <summary>
    /// Alternative logging system that preserves Unity Console source code linking
    /// while still providing filtering capabilities when needed.
    /// 
    /// Use SpatialBlendLog for development debugging (preserves source linking)
    /// Use SpatialBlendDebug for production/filtered logging (categorized)
    /// </summary>
    public static class SpatialBlendLog
    {
        private static bool isLoggingEnabled = true;

        /// <summary>
        /// Enable or disable all SpatialBlendLog logging
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            isLoggingEnabled = enabled;
        }

        /// <summary>
        /// Check if logging is enabled
        /// </summary>
        public static bool IsEnabled => isLoggingEnabled;

        // Direct Unity Debug calls that preserve source code linking
        // These are only active in Editor and Development builds

        /// <summary>
        /// Direct log that preserves Unity Console source linking
        /// Only active in Editor and Development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Log(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend] {message}");
            }
        }

        /// <summary>
        /// Direct log with context object that preserves Unity Console source linking
        /// Only active in Editor and Development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Log(string message, Object context)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend] {message}", context);
            }
        }

        /// <summary>
        /// Direct warning that preserves Unity Console source linking
        /// Only active in Editor and Development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void LogWarning(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogWarning($"[SpatialBlend] {message}");
            }
        }

        /// <summary>
        /// Direct warning with context object that preserves Unity Console source linking
        /// Only active in Editor and Development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void LogWarning(string message, Object context)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogWarning($"[SpatialBlend] {message}", context);
            }
        }

        /// <summary>
        /// Direct error that preserves Unity Console source linking
        /// Always active (errors should always be visible)
        /// </summary>
        public static void LogError(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogError($"[SpatialBlend] {message}");
            }
        }

        /// <summary>
        /// Direct error with context object that preserves Unity Console source linking
        /// Always active (errors should always be visible)
        /// </summary>
        public static void LogError(string message, Object context)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogError($"[SpatialBlend] {message}", context);
            }
        }

        /// <summary>
        /// Log with category prefix but still preserve source linking
        /// Only active in Editor and Development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void LogWithCategory(SpatialBlendDebug.LogCategory category, string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend][{category}] {message}");
            }
        }

        /// <summary>
        /// Warning with category prefix but still preserve source linking
        /// Only active in Editor and Development builds
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void LogWarningWithCategory(SpatialBlendDebug.LogCategory category, string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogWarning($"[SpatialBlend][{category}] {message}");
            }
        }

        /// <summary>
        /// Error with category prefix but still preserve source linking
        /// Always active (errors should always be visible)
        /// </summary>
        public static void LogErrorWithCategory(SpatialBlendDebug.LogCategory category, string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.LogError($"[SpatialBlend][{category}] {message}");
            }
        }

        // Conditional compilation methods for different scenarios

        /// <summary>
        /// Only logs in Unity Editor (useful for editor-only debugging)
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        public static void LogEditor(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend][Editor] {message}");
            }
        }

        /// <summary>
        /// Only logs in development builds (useful for testing builds)
        /// </summary>
        [Conditional("DEVELOPMENT_BUILD")]
        public static void LogDevelopment(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend][Dev] {message}");
            }
        }

        /// <summary>
        /// Always logs regardless of build configuration (use sparingly)
        /// </summary>
        public static void LogAlways(string message)
        {
            if (isLoggingEnabled)
            {
                UnityEngine.Debug.Log($"[SpatialBlend][Always] {message}");
            }
        }
    }
}