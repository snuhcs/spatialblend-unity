// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Batchmode IL2CPP checks for the Quest target (arm64). ExportAndroid runs
    /// il2cpp (C# to C++) and writes a Gradle project; BuildAndroidApk also
    /// compiles that C++ into libil2cpp.so through Unity's bundled Gradle, SDK,
    /// NDK and JDK. No device is needed. Run them in a disposable copy of the
    /// project: they switch the active build target.
    ///
    /// Unity -batchmode -quit -projectPath COPY -buildTarget Android
    ///   -executeMethod SpatialBlend.Editor.Il2CppBuildCheck.BuildAndroidApk
    ///   -il2cppCheckOut /abs/output/dir-or.apk -logFile LOG
    ///
    /// RequiredCredentialsBuildGuard fails an Android build that lacks a required ID or credential (Photon Fusion App ID, Photon Voice App ID,
    /// TURN client secret). A copy that only checks that the code compiles may set SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID=1,
    /// SPATIALBLEND_ALLOW_NO_VOICE_APP_ID=1 and SPATIALBLEND_ALLOW_NO_TURN=1 as needed (its APK then stamps the credential as missing and
    /// check_apk_contents.py rejects it).
    /// </summary>
    public static class Il2CppBuildCheck
    {
        public static void ExportAndroid() => Build(export: true);

        public static void BuildAndroidApk() => Build(export: false);

        private static void Build(bool export)
        {
            var output = ArgumentAfter("-il2cppCheckOut");
            if (string.IsNullOrEmpty(output))
            {
                Debug.LogError("[Il2CppBuildCheck] Missing -il2cppCheckOut <dir>");
                EditorApplication.Exit(2);
                return;
            }

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = export;
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                // The APK path stores the game data as one LZ4HC data.unity3d. The default (BuildOptions.None) deflates it into
                // 1 MB sharedassets0.assets.splitN files and the first scene load then takes minutes on a Quest.
                options = export ? BuildOptions.None : BuildOptions.CompressWithLz4HC,
            });

            var summary = report.summary;
            Debug.Log($"[Il2CppBuildCheck] result={summary.result} errors={summary.totalErrors} " +
                      $"warnings={summary.totalWarnings} scenes={string.Join(",", scenes)} output={output}");
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static string ArgumentAfter(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
