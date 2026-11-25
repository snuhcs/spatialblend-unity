// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using TMPro;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// TextMesh Pro Essential Resources (Assets/TextMesh Pro, git-ignored) are not part of the repository; tools/setup_sdks.py
    /// imports them from the pinned com.unity.ugui package. Without them TMP_Settings.instance is null in the player, every
    /// ControlPanelKit.Label throws, and the app sits on the splash. This guard fails the build instead of producing that app,
    /// and writes a stamp into the APK (StreamingAssets) that tools/check_apk_contents.py asserts from outside. The stamp also records which required
    /// credentials the build has (RequiredCredentials.StampMembers), so an APK built with one missing is rejected from outside too.
    /// </summary>
    public sealed class TmpEssentialsBuildGuard : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public const string StampFileName = "SpatialBlendBuildStamp.json";
        public const string SetupHint = "TextMesh Pro Essential Resources are missing. Run: python3 tools/setup_sdks.py";

        private static string StampPath => Path.Combine(Application.dataPath, "StreamingAssets", StampFileName);

        public int callbackOrder => -1000;

        /// <summary>Returns null when TMP is usable, else the reason.</summary>
        public static string Verify()
        {
            var settings = TMP_Settings.instance;
            if (settings == null)
            {
                return "TMP_Settings.instance is null (no Assets/TextMesh Pro/Resources/TMP Settings.asset). " + SetupHint;
            }

            if (TMP_Settings.defaultFontAsset == null)
            {
                return "TMP_Settings has no default font asset. " + SetupHint;
            }

            return null;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            var problem = Verify();
            if (problem != null)
            {
                throw new BuildFailedException("[TmpEssentialsBuildGuard] " + problem);
            }

            var font = TMP_Settings.defaultFontAsset;
            var settingsPath = AssetDatabase.GetAssetPath(TMP_Settings.instance);
            var stamp = "{\"tmpSettings\":\"" + AssetDatabase.AssetPathToGUID(settingsPath) + "\"," +
                        "\"tmpDefaultFontAsset\":\"" + font.name + "\"," +
                        "\"tmpDefaultFontAssetGuid\":\"" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(font)) + "\"," +
                        RequiredCredentials.StampMembers(RequiredCredentialsBuildGuard.ReadFacts()) + "}";
            Directory.CreateDirectory(Path.GetDirectoryName(StampPath));
            File.WriteAllText(StampPath, stamp);
            AssetDatabase.Refresh();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            // The stamp is a build artifact, not source: remove it (and the empty folder this guard created) again.
            if (File.Exists(StampPath))
            {
                AssetDatabase.DeleteAsset("Assets/StreamingAssets/" + StampFileName);
            }
        }
    }
}
