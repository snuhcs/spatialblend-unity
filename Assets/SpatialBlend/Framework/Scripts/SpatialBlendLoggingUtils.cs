// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System.Text;
using System.Collections.Generic;

namespace SpatialBlend
{
    /// <summary>
    /// Utility class for managing SpatialBlend logging configuration and providing debugging tools
    /// 
    /// LOGGING SYSTEM GUIDE:
    /// - Use SpatialBlendLog.* for development debugging (preserves Unity Console source linking)
    /// - Use SpatialBlendDebug.* for production/filtered logging (categorized, but loses source linking)
    /// - Use direct Debug.* calls for temporary debugging (remove before committing)
    /// </summary>
    public static class SpatialBlendLoggingUtils
    {
        /// <summary>
        /// Get a formatted summary of current logging configuration
        /// </summary>
        public static string GetLoggingStatusReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SpatialBlend Logging Status ===");
            sb.AppendLine($"Logging Enabled: {SpatialBlendDebug.IsLoggingEnabled}");
            sb.AppendLine($"Enabled Categories: {SpatialBlendDebug.GetEnabledCategories()}");

            // Check each category individually
            var categories = System.Enum.GetValues(typeof(SpatialBlendDebug.LogCategory));
            sb.AppendLine("\nCategory Status:");
            foreach (SpatialBlendDebug.LogCategory category in categories)
            {
                if (category != SpatialBlendDebug.LogCategory.None && category != SpatialBlendDebug.LogCategory.All)
                {
                    bool enabled = SpatialBlendDebug.IsCategoryEnabled(category);
                    sb.AppendLine($"  {category}: {(enabled ? "✓" : "✗")}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Configure logging for development (all categories enabled)
        /// </summary>
        public static void ConfigureForDevelopment()
        {
            SpatialBlendDebug.SetLoggingEnabled(true);
            SpatialBlendDebug.SetEnabledCategories(SpatialBlendDebug.LogCategory.All);
            SpatialBlendDebug.Log(SpatialBlendDebug.LogCategory.All, "Logging configured for development mode");
        }

        /// <summary>
        /// Configure logging for production (errors and warnings only)
        /// </summary>
        public static void ConfigureForProduction()
        {
            SpatialBlendDebug.SetLoggingEnabled(true);
            // Only enable critical categories for production
            SpatialBlendDebug.SetEnabledCategories(
                SpatialBlendDebug.LogCategory.Connection |
                SpatialBlendDebug.LogCategory.State
            );
            SpatialBlendDebug.Log(SpatialBlendDebug.LogCategory.Connection, "Logging configured for production mode");
        }

        /// <summary>
        /// Configure minimal logging (connection and errors only)
        /// </summary>
        public static void ConfigureMinimal()
        {
            SpatialBlendDebug.SetLoggingEnabled(true);
            SpatialBlendDebug.SetEnabledCategories(SpatialBlendDebug.LogCategory.Connection);
            SpatialBlendDebug.Log(SpatialBlendDebug.LogCategory.Connection, "Logging configured for minimal mode");
        }

        /// <summary>
        /// Disable all logging
        /// </summary>
        public static void DisableAllLogging()
        {
            SpatialBlendDebug.SetLoggingEnabled(false);
        }

        /// <summary>
        /// Apply a SpatialBlendSettings configuration to the logging system
        /// </summary>
        public static void ApplySpatialBlendSettings(SpatialBlendSettings settings)
        {
            if (settings == null)
            {
                Debug.LogWarning("[SpatialBlend] No SpatialBlendSettings provided, using default configuration");
                return;
            }

            settings.ApplyToDebugSystem();

            // Apply special configurations based on settings
            if (settings.errorsAndWarningsOnly)
            {
                // This would need implementation in SpatialBlendDebug to filter log levels
                Debug.Log("[SpatialBlend] Errors and warnings only mode requested (requires additional implementation)");
            }
        }

        /// <summary>
        /// Log the current configuration for debugging purposes
        /// </summary>
        public static void LogCurrentConfiguration()
        {
            string report = GetLoggingStatusReport();
            Debug.Log(report);
        }

        /// <summary>
        /// Quick setup for common scenarios
        /// </summary>
        public static void QuickSetup(LoggingProfile profile)
        {
            switch (profile)
            {
                case LoggingProfile.Development:
                    ConfigureForDevelopment();
                    break;
                case LoggingProfile.Production:
                    ConfigureForProduction();
                    break;
                case LoggingProfile.Minimal:
                    ConfigureMinimal();
                    break;
                case LoggingProfile.Disabled:
                    DisableAllLogging();
                    break;
            }
        }
    }

    /// <summary>
    /// Predefined logging profiles for quick configuration
    /// </summary>
    public enum LoggingProfile
    {
        Development,    // All categories enabled
        Production,     // Critical categories only
        Minimal,        // Connection only
        Disabled        // No logging
    }
}