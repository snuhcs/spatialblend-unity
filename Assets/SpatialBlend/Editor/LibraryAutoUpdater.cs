// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEditor;
using System.IO;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;
using SpatialBlend.Internal;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Automatically checks for missing SpatialBlend libraries and runs tools/fetch_plugins.py when needed. The fetcher downloads
    /// the public release files (SpatialBlendLibraryConfig.CorePlugins/SessionPlugins.RELEASE_URL, overridable per user in
    /// EditorPrefs) and verifies each SHA-256 before installing.
    /// </summary>
    [InitializeOnLoad]
    public class LibraryAutoUpdater
    {
        private const string UPDATE_SCRIPT_PATH = SpatialBlendLibraryConfig.Paths.FETCH_SCRIPT;
        private const string PLUGINS_DIR = SpatialBlendLibraryConfig.Paths.PLUGINS_DIR;
        private const string AUTO_CHECK_PREF = SpatialBlendLibraryConfig.EditorPrefs.AUTO_CHECK_LIBRARIES;

        // Expected library files for each platform
        private static string[] EXPECTED_LIBRARIES => SpatialBlendLibraryConfig.ExpectedLibraries.GetExpectedLibraries();

        static LibraryAutoUpdater()
        {
            // CI and device-free tests must not download or replace plugins.
            // Library provisioning is an explicit step for batch invocations.
            if (Application.isBatchMode)
                return;

            // Check for missing libraries when Unity loads
            EditorApplication.delayCall += CheckAndUpdateLibraries;
        }

        [MenuItem(SpatialBlendLibraryConfig.MenuItems.CHECK_LIBRARY_STATUS)]
        public static void CheckLibraryStatus()
        {
            var missingLibraries = GetMissingLibraries();
            if (missingLibraries.Length == 0)
            {
                Debug.Log(SpatialBlendLibraryConfig.LogMessages.Success.ALL_LIBRARIES_PRESENT);
            }
            else
            {
                var message = string.Format(SpatialBlendLibraryConfig.DialogMessages.Content.MISSING_LIBRARIES_TEMPLATE, string.Join("\n", missingLibraries));
                if (EditorUtility.DisplayDialog(SpatialBlendLibraryConfig.DialogMessages.Titles.MISSING_LIBRARIES, message, "Download", "Cancel"))
                {
                    RunUpdateScript();
                }
            }
        }

        [MenuItem(SpatialBlendLibraryConfig.MenuItems.FORCE_UPDATE_LIBRARIES)]
        public static void ForceUpdateLibraries()
        {
            if (EditorUtility.DisplayDialog(SpatialBlendLibraryConfig.DialogMessages.Titles.FORCE_UPDATE,
                SpatialBlendLibraryConfig.DialogMessages.Content.FORCE_UPDATE_CONFIRMATION, "Yes", "Cancel"))
            {
                RunUpdateScript();
            }
        }

        [MenuItem(SpatialBlendLibraryConfig.MenuItems.PREFERENCES)]
        public static void OpenPreferences()
        {
            var autoCheck = EditorPrefs.GetBool(AUTO_CHECK_PREF, true);
            var platformSpecificWarnings = EditorPrefs.GetBool(SpatialBlendLibraryConfig.EditorPrefs.PLATFORM_SPECIFIC_WARNINGS, true);
            
            var message = $"SpatialBlend Preferences:\n\n" +
                         $"• Auto-check for missing libraries on startup: {(autoCheck ? "Enabled" : "Disabled")}\n" +
                         $"• Platform-specific warnings: {(platformSpecificWarnings ? "Enabled" : "Disabled")}\n\n" +
                         $"Which setting would you like to toggle?";
            
            var choice = EditorUtility.DisplayDialogComplex("SpatialBlend Preferences", message, 
                "Toggle Auto-Check", "Toggle Platform Warnings", "Cancel");

            if (choice == 0) // Toggle Auto-Check
            {
                EditorPrefs.SetBool(AUTO_CHECK_PREF, !autoCheck);
                Debug.Log($"[SpatialBlend] Auto-check libraries setting changed to: {!autoCheck}");
            }
            else if (choice == 1) // Toggle Platform Warnings
            {
                EditorPrefs.SetBool(SpatialBlendLibraryConfig.EditorPrefs.PLATFORM_SPECIFIC_WARNINGS, !platformSpecificWarnings);
                Debug.Log($"[SpatialBlend] Platform-specific warnings setting changed to: {!platformSpecificWarnings}");
            }
        }

        [MenuItem(SpatialBlendLibraryConfig.MenuItems.TOGGLE_PLATFORM_WARNINGS)]
        public static void TogglePlatformWarnings()
        {
            var currentSetting = EditorPrefs.GetBool(SpatialBlendLibraryConfig.EditorPrefs.PLATFORM_SPECIFIC_WARNINGS, true);
            var newSetting = !currentSetting;
            
            EditorPrefs.SetBool(SpatialBlendLibraryConfig.EditorPrefs.PLATFORM_SPECIFIC_WARNINGS, newSetting);
            
            var status = newSetting ? "enabled" : "disabled";
            Debug.Log($"[SpatialBlend] Platform-specific warnings {status}");
            
            EditorUtility.DisplayDialog("SpatialBlend Settings", 
                $"Platform-specific warnings have been {status}.\n\n" +
                $"When enabled, warnings only show for relevant build targets.\n" +
                $"When disabled, warnings show for all platforms.", "OK");
        }

        /// <summary>
        /// Checks if warnings should be shown based on current build settings
        /// </summary>
        private static bool ShouldShowWarning()
        {
            // Check if platform-specific warnings are enabled
            if (!EditorPrefs.GetBool(SpatialBlendLibraryConfig.EditorPrefs.PLATFORM_SPECIFIC_WARNINGS, true))
            {
                // If disabled, always show warnings (original behavior)
                return true;
            }

            // Always show warnings for Android builds
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
            {
                return true;
            }

            // Show warnings if the current build target requires SpatialBlend libraries
            var currentTarget = EditorUserBuildSettings.activeBuildTarget;
            var supportedTargets = new[]
            {
                BuildTarget.Android,
                BuildTarget.StandaloneWindows,
                BuildTarget.StandaloneWindows64,
                BuildTarget.StandaloneOSX,
                BuildTarget.StandaloneLinux64,
                BuildTarget.VisionOS
            };

            return System.Array.Exists(supportedTargets, target => target == currentTarget);
        }

        private static void CheckAndUpdateLibraries()
        {
            // Only check if we're not in play mode and the project is fully loaded
            if (EditorApplication.isPlaying || EditorApplication.isCompiling)
                return;

            // Check if auto-check is enabled
            if (!EditorPrefs.GetBool(AUTO_CHECK_PREF, true))
                return;

            // Only show warnings if it matches current build settings or is Android
            if (!ShouldShowWarning())
                return;

            var missingLibraries = GetMissingLibraries();
            if (missingLibraries.Length > 0)
            {
                Debug.LogWarning($"[SpatialBlend] Missing libraries detected: {string.Join(", ", missingLibraries)}");

                // Show a dialog asking if the user wants to download them
                var message = $"SpatialBlend libraries are missing:\n\n{string.Join("\n", missingLibraries)}\n\nWould you like to download them automatically?";
                if (EditorUtility.DisplayDialog("Missing SpatialBlend Libraries", message, "Download", "Skip"))
                {
                    RunUpdateScript();
                }
            }
        }

        private static string[] GetMissingLibraries()
        {
            var missing = new System.Collections.Generic.List<string>();

            foreach (var libPath in EXPECTED_LIBRARIES)
            {
                var fullPath = Path.Combine(PLUGINS_DIR, libPath);
                if (!File.Exists(fullPath))
                {
                    missing.Add(libPath);
                }
            }

            return missing.ToArray();
        }

        private static void RunUpdateScript()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var scriptPath = Path.GetFullPath(Path.Combine(projectRoot, UPDATE_SCRIPT_PATH));

            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[SpatialBlend] Update script not found at: {scriptPath}");
                EditorUtility.DisplayDialog("Error", $"Update script not found at:\n{scriptPath}\n\nPlease ensure tools/fetch_plugins.py exists.", "OK");
                return;
            }

            var releaseUrl = ResolveReleaseUrl(
                SpatialBlendLibraryConfig.EditorPrefs.CORE_PLUGIN_RELEASE_URL,
                "SPATIALBLEND_PLUGIN_RELEASE_URL",
                SpatialBlendLibraryConfig.CorePlugins.RELEASE_URL);
            var sessionReleaseUrl = ResolveReleaseUrl(
                SpatialBlendLibraryConfig.EditorPrefs.SESSION_PLUGIN_RELEASE_URL,
                "SPATIALBLEND_SESSION_PLUGIN_RELEASE_URL",
                SpatialBlendLibraryConfig.SessionPlugins.RELEASE_URL);
            if (string.IsNullOrEmpty(releaseUrl) || string.IsNullOrEmpty(sessionReleaseUrl))
            {
                EditorUtility.DisplayDialog(
                    "Error",
                    "SpatialBlend.CorePluginReleaseUrl or SpatialBlend.SessionPluginReleaseUrl is set to an empty value. Delete the key to use the public release.",
                    "OK");
                return;
            }

            Debug.Log("[SpatialBlend] Fetching libspatialblend and libspatialblend_session from their releases.");

            try
            {
                var python = Application.platform == RuntimePlatform.WindowsEditor ? "python" : "python3";
                var arguments = BuildFetchArguments(scriptPath, projectRoot, releaseUrl, sessionReleaseUrl);
                Debug.Log($"[SpatialBlend] {python} {arguments}");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = python,
                        Arguments = arguments,
                        WorkingDirectory = projectRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.Log($"[SpatialBlend Update] {e.Data}");
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.LogError($"[SpatialBlend Update] {e.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Debug.Log("[SpatialBlend] Library update completed successfully.");
                    EditorUtility.DisplayDialog("Success", "SpatialBlend libraries have been updated successfully.", "OK");

                    // Refresh the asset database to pick up new files
                    AssetDatabase.Refresh();
                }
                else
                {
                    Debug.LogError($"[SpatialBlend] Library update failed with exit code: {process.ExitCode}");
                    var errorMessage = "Library update failed. This may be due to:\n\n" +
                                      "• A SHA-256 mismatch\n" +
                                      "• No network access to the release URL\n" +
                                      "• Unity having the plugin file locked\n\n" +
                                      "Check the console for detailed error messages.";
                    EditorUtility.DisplayDialog("Error", errorMessage, "OK");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[SpatialBlend] Failed to run update script: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to run update script:\n{ex.Message}", "OK");
            }
        }

        private static string BuildFetchArguments(string scriptPath, string projectRoot, string releaseUrl, string sessionReleaseUrl)
        {
            var args = new StringBuilder();
            args.Append(Quote(scriptPath));
            args.Append(" --project-root ").Append(Quote(projectRoot));
            foreach (var platform in SpatialBlendLibraryConfig.ExpectedLibraries.GetFetchPlatforms())
                args.Append(" --platform ").Append(platform);
            args.Append(" --release-url ").Append(Quote(releaseUrl));
            args.Append(" --session-release-url ").Append(Quote(sessionReleaseUrl));
            AppendPin(args, "android", SpatialBlendLibraryConfig.EditorPrefs.CORE_PLUGIN_SHA256_ANDROID);
            AppendPin(args, "osx", SpatialBlendLibraryConfig.EditorPrefs.CORE_PLUGIN_SHA256_OSX);
            AppendPin(args, "windows", SpatialBlendLibraryConfig.EditorPrefs.CORE_PLUGIN_SHA256_WINDOWS);
            AppendPin(args, "session:android", SpatialBlendLibraryConfig.EditorPrefs.SESSION_PLUGIN_SHA256_ANDROID);
            AppendPin(args, "session:osx", SpatialBlendLibraryConfig.EditorPrefs.SESSION_PLUGIN_SHA256_OSX);
            AppendPin(args, "session:windows", SpatialBlendLibraryConfig.EditorPrefs.SESSION_PLUGIN_SHA256_WINDOWS);
            return args.ToString();
        }

        // The release download base: the EditorPrefs key when set (an empty value is reported by the caller), else the
        // environment variable the fetcher also reads, else the public release.
        internal static string ResolveReleaseUrl(string prefKey, string environmentVariable, string fallback)
        {
            if (EditorPrefs.HasKey(prefKey))
                return EditorPrefs.GetString(prefKey, fallback);
            var fromEnvironment = System.Environment.GetEnvironmentVariable(environmentVariable);
            return string.IsNullOrEmpty(fromEnvironment) ? fallback : fromEnvironment;
        }

        private static void AppendPin(StringBuilder args, string platform, string prefKey)
        {
            var pin = EditorPrefs.GetString(prefKey, "");
            if (!string.IsNullOrEmpty(pin))
                args.Append(" --pin ").Append(platform).Append('=').Append(pin);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        [MenuItem(SpatialBlendLibraryConfig.MenuItems.OPEN_PLUGINS_DIRECTORY)]
        public static void OpenPluginsDirectory()
        {
            var pluginsPath = Path.Combine(Application.dataPath, "Plugins");
            if (Directory.Exists(pluginsPath))
            {
                EditorUtility.RevealInFinder(pluginsPath);
            }
            else
            {
                Debug.LogWarning("[SpatialBlend] Plugins directory not found.");
            }
        }
    }
}
