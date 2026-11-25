// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// Centralized configuration for SpatialBlend library paths and platform definitions
    /// </summary>
    public static class SpatialBlendLibraryConfig
    {
        // Directory paths
        public static class Paths
        {
            public const string PLUGINS_DIR = "Assets/Plugins";
            public const string FETCH_SCRIPT = "tools/fetch_plugins.py";
        }

        // Library names for DllImport. The player loads libspatialblend (sb_* C API); the shipped file is libspatialblend.<ext> on every
        // platform. Android, macOS and Linux add the "lib" prefix to a DllImport name; Windows does not (it looks for <name>.dll), so
        // the Windows names carry the prefix themselves.
        public static class LibraryNames
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            public const string RELEASE_LIBRARY = "libspatialblend";
            public const string DEBUG_LIBRARY = "libspatialblend_d";
#else
            public const string RELEASE_LIBRARY = "spatialblend";
            public const string DEBUG_LIBRARY = "spatialblend_d";
#endif
            public const string UNSUPPORTED_PLATFORM = "Unsupported Platform";
        }

        // Platform definitions
        public static class Platforms
        {
            public static readonly Dictionary<RuntimePlatform, PlatformInfo> PlatformMap = new Dictionary<RuntimePlatform, PlatformInfo>
            {
                { RuntimePlatform.WindowsEditor, new PlatformInfo("Windows", "dll", "Windows") },
                { RuntimePlatform.WindowsPlayer, new PlatformInfo("Windows", "dll", "Windows") },
                { RuntimePlatform.OSXEditor, new PlatformInfo("OSX", "dylib", "OSX") },
                { RuntimePlatform.OSXPlayer, new PlatformInfo("OSX", "dylib", "OSX") },
                { RuntimePlatform.LinuxEditor, new PlatformInfo("Linux", "so", "Linux") },
                { RuntimePlatform.LinuxPlayer, new PlatformInfo("Linux", "so", "Linux") },
                { RuntimePlatform.Android, new PlatformInfo("Android", "so", "Android") },
                { RuntimePlatform.VisionOS, new PlatformInfo("visionOS", "dylib", "visionOS") }
            };

            public static readonly string[] SupportedPlatforms = { "Windows", "OSX", "Linux", "Android", "visionOS" };
            public static readonly string[] SourcePlatforms = { "windows", "macos", "linux", "android", "visionos" };
        }

        // Expected library files for each platform
        public static class ExpectedLibraries
        {
            /// <summary>
            /// Core and session plugins the updater installs: this Editor's desktop plugin, plus Android.
            /// Installing both OSX and Windows makes Unity load two Editor plugins with the same name.
            /// Paths are relative to Assets/Plugins.
            /// </summary>
            public static string[] GetExpectedLibraries()
            {
                var paths = new List<string>();
                foreach (var platform in GetFetchPlatforms())
                {
                    if (platform == "android")
                        paths.Add(RelativeToPlugins(CorePlugins.ANDROID_DEST));
                    else if (platform == "osx")
                        paths.Add(RelativeToPlugins(CorePlugins.OSX_DEST));
                    else if (platform == "windows")
                        paths.Add(RelativeToPlugins(CorePlugins.WINDOWS_DEST));
                }
                foreach (var platform in GetFetchPlatforms())
                {
                    if (platform == "android")
                        paths.Add(RelativeToPlugins(SessionPlugins.ANDROID_DEST));
                    else if (platform == "osx")
                        paths.Add(RelativeToPlugins(SessionPlugins.OSX_DEST));
                    else if (platform == "windows")
                        paths.Add(RelativeToPlugins(SessionPlugins.WINDOWS_DEST));
                }
                return paths.ToArray();
            }

            /// <summary>
            /// Values for fetch_plugins.py --platform (applied to both libraries). Other desktop plugins stay opt-in.
            /// </summary>
            public static string[] GetFetchPlatforms()
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                return new[] { "windows", "android" };
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                return new[] { "osx", "android" };
#else
                return new[] { "android" };
#endif
            }

            private static string RelativeToPlugins(string projectRelative)
            {
                const string prefix = "Assets/Plugins/";
                if (projectRelative.StartsWith(prefix))
                    return projectRelative.Substring(prefix.Length);
                return projectRelative;
            }
        }

        /// <summary>
        /// Core native plugins (libspatialblend), published as files of a public GitHub release with a SHA256SUMS file.
        /// tools/fetch_plugins.py installs them into the gitignored plugin directories and refuses a file whose
        /// SHA-256 does not match. Keep RELEASE_TAG equal to
        /// CORE_RELEASE_TAG in tools/fetch_plugins.py (tools/test_fetch_plugins.py checks it).
        /// </summary>
        public static class CorePlugins
        {
            public const string RELEASE_REPOSITORY = "snuhcs/spatialblend-core";
            public const string RELEASE_TAG = "v1.0.0";
            public const string CHECKSUM_FILE = "SHA256SUMS";
            // EditorPrefs (SpatialBlend.CorePluginReleaseUrl), else SPATIALBLEND_PLUGIN_RELEASE_URL, overrides this (LibraryAutoUpdater.ResolveReleaseUrl).
            public const string RELEASE_URL = "https://github.com/" + RELEASE_REPOSITORY + "/releases/download/" + RELEASE_TAG + "/";

            public const string ANDROID_LIBRARY = "libspatialblend.so";
            public const string OSX_LIBRARY = "libspatialblend.dylib";
            public const string WINDOWS_LIBRARY = "libspatialblend.dll";

            public const string ANDROID_DEST = "Assets/Plugins/Android/arm64-v8a/libspatialblend.so";
            public const string OSX_DEST = "Assets/Plugins/OSX/libspatialblend.dylib";
            public const string WINDOWS_DEST = "Assets/Plugins/Windows/libspatialblend.dll";
        }

        /// <summary>
        /// libspatialblend_session (the scan session client, spatialblend_* C API), published as files of the public
        /// spatialblend-reconstruction release. It is not the core library and shares no symbols with it.
        /// tools/fetch_plugins.py fetches it with the same host-plus-Android default and SHA-256 check as
        /// <see cref="CorePlugins"/>.
        /// </summary>
        public static class SessionPlugins
        {
            public const string RELEASE_REPOSITORY = "snuhcs/spatialblend-reconstruction";
            public const string RELEASE_TAG = "v1.0.0";
            // EditorPrefs (SpatialBlend.SessionPluginReleaseUrl), else SPATIALBLEND_SESSION_PLUGIN_RELEASE_URL, overrides this (LibraryAutoUpdater.ResolveReleaseUrl).
            public const string RELEASE_URL = "https://github.com/" + RELEASE_REPOSITORY + "/releases/download/" + RELEASE_TAG + "/";

            public const string ANDROID_LIBRARY = "libspatialblend_session.so";
            public const string OSX_LIBRARY = "libspatialblend_session.dylib";
            public const string WINDOWS_LIBRARY = "libspatialblend_session.dll";

            public const string ANDROID_DEST = "Assets/Plugins/Android/arm64-v8a/libspatialblend_session.so";
            public const string OSX_DEST = "Assets/Plugins/OSX/libspatialblend_session.dylib";
            public const string WINDOWS_DEST = "Assets/Plugins/Windows/libspatialblend_session.dll";
        }

        // Editor preferences keys
        public static class EditorPrefs
        {
            public const string AUTO_CHECK_LIBRARIES = "SpatialBlend.AutoCheckLibraries";
            public const string PLATFORM_SPECIFIC_WARNINGS = "SpatialBlend.PlatformSpecificWarnings";
            public const string CORE_PLUGIN_RELEASE_URL = "SpatialBlend.CorePluginReleaseUrl";
            public const string CORE_PLUGIN_SHA256_ANDROID = "SpatialBlend.CorePluginSha256Android";
            public const string CORE_PLUGIN_SHA256_OSX = "SpatialBlend.CorePluginSha256Osx";
            public const string CORE_PLUGIN_SHA256_WINDOWS = "SpatialBlend.CorePluginSha256Windows";
            public const string SESSION_PLUGIN_RELEASE_URL = "SpatialBlend.SessionPluginReleaseUrl";
            public const string SESSION_PLUGIN_SHA256_ANDROID = "SpatialBlend.SessionPluginSha256Android";
            public const string SESSION_PLUGIN_SHA256_OSX = "SpatialBlend.SessionPluginSha256Osx";
            public const string SESSION_PLUGIN_SHA256_WINDOWS = "SpatialBlend.SessionPluginSha256Windows";
        }

        // Menu item paths
        public static class MenuItems
        {
            public const string BASE_PATH = "SpatialBlend/";
            public const string CHECK_LIBRARY_STATUS = BASE_PATH + "Check Library Status";
            public const string FORCE_UPDATE_LIBRARIES = BASE_PATH + "Force Update Libraries";
            public const string CHECK_API_COMPATIBILITY = BASE_PATH + "Check API Compatibility";
            public const string PREFERENCES = BASE_PATH + "Preferences";
            public const string TOGGLE_PLATFORM_WARNINGS = BASE_PATH + "Toggle Platform Warnings";
            public const string OPEN_PLUGINS_DIRECTORY = BASE_PATH + "Open Plugins Directory";
        }

        // Log messages
        public static class LogMessages
        {
            public const string PREFIX = "[SpatialBlend] ";
            public const string UPDATE_PREFIX = "[SpatialBlend Update] ";

            public static class Success
            {
                public const string ALL_LIBRARIES_PRESENT = PREFIX + "All libraries are present and up to date.";
                public const string LIBRARY_UPDATE_COMPLETED = PREFIX + "Library update completed successfully.";
                public const string API_COMPATIBILITY_PASSED = PREFIX + "API compatibility check passed.";
            }

            public static class Warning
            {
                public const string MISSING_LIBRARIES = PREFIX + "Missing libraries detected: {0}";
                public const string PLUGINS_DIR_NOT_FOUND = PREFIX + "Plugins directory not found.";
            }

            public static class Error
            {
                public const string UPDATE_SCRIPT_NOT_FOUND = PREFIX + "Update script not found at: {0}";
                public const string BASH_NOT_FOUND = PREFIX + "Bash not found on Windows. Please install Git Bash, WSL, or another bash implementation.";
                public const string COULD_NOT_CHECK_BASH = PREFIX + "Could not check for bash availability on Windows.";
                public const string LIBRARY_UPDATE_FAILED = PREFIX + "Library update failed with exit code: {0}";
                public const string FAILED_TO_RUN_SCRIPT = PREFIX + "Failed to run update script: {0}";
            }
        }

        // Dialog messages
        public static class DialogMessages
        {
            public static class Titles
            {
                public const string MISSING_LIBRARIES = "Missing SpatialBlend Libraries";
                public const string FORCE_UPDATE = "Force Update Libraries";
                public const string API_COMPATIBILITY = "API Compatibility";
                public const string API_COMPATIBILITY_ISSUES = "API Compatibility Issues";
                public const string PREFERENCES = "SpatialBlend Preferences";
                public const string SUCCESS = "Success";
                public const string ERROR = "Error";
            }

            public static class Content
            {
                public const string MISSING_LIBRARIES_TEMPLATE = "Missing libraries detected:\n\n{0}\n\nWould you like to download them now?";
                public const string FORCE_UPDATE_CONFIRMATION = "This will download the libspatialblend and libspatialblend_session plugins from their GitHub releases, verify each SHA-256, and install them into Assets/Plugins. Continue?";
                public const string API_COMPATIBILITY_SUCCESS = "All core API elements are present.";
                public const string API_COMPATIBILITY_ISSUES_TEMPLATE = "API compatibility issues detected:\n\n{0}";
                public const string PREFERENCES_TEMPLATE = "Auto-check for missing libraries on startup: {0}\n\nWould you like to toggle this setting?";
                public const string LIBRARY_UPDATE_SUCCESS = "SpatialBlend libraries have been updated successfully.";
                public const string UPDATE_SCRIPT_NOT_FOUND_TEMPLATE = "Update script not found at:\n{0}\n\nPlease ensure tools/fetch_plugins.py exists in the project root.";
                public const string BASH_NOT_FOUND = "Bash not found on Windows.\n\nPlease install Git Bash, WSL, or another bash implementation to run the update script.";
                public const string COULD_NOT_CHECK_BASH = "Could not check for bash availability on Windows.\n\nPlease ensure bash is available in your PATH.";
                public const string LIBRARY_UPDATE_FAILED = "Library update failed. This may be due to:\n\n" +
                                                          "• Unity having files locked (try closing Unity)\n" +
                                                          "• File permission issues\n" +
                                                          "• Missing bash on Windows\n\n" +
                                                          "Check the console for detailed error messages.";
                public const string FAILED_TO_RUN_SCRIPT_TEMPLATE = "Failed to run update script:\n{0}";
            }
        }

        // Platform information structure
        public class PlatformInfo
        {
            public string UnityName { get; }
            public string Extension { get; }
            public string DirectoryName { get; }

            public PlatformInfo(string unityName, string extension, string directoryName)
            {
                UnityName = unityName;
                Extension = extension;
                DirectoryName = directoryName;
            }
        }

        // Utility methods
        public static class Utils
        {
            public static bool IsPlatformSupported(RuntimePlatform platform)
            {
                return Platforms.PlatformMap.ContainsKey(platform);
            }

            public static PlatformInfo GetPlatformInfo(RuntimePlatform platform)
            {
                return Platforms.PlatformMap.TryGetValue(platform, out var info) ? info : null;
            }

            public static string GetUnsupportedPlatformLibraryName()
            {
                return LibraryNames.UNSUPPORTED_PLATFORM;
            }
        }
    }
}
