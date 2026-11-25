// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>The slice of SDKS.lock the Editor reads. Field names are the JSON keys.</summary>
    [Serializable]
    public sealed class SdkLockFile
    {
        public int schema;
        public SdkLockEditor unity_editor;
        public List<SdkLockEntry> entries = new List<SdkLockEntry>();
    }

    [Serializable]
    public sealed class SdkLockEditor { public string version; }

    [Serializable]
    public sealed class SdkLockSource { public string revision; }

    [Serializable]
    public sealed class SdkLockEntry
    {
        public string name;
        public string kind;
        public string version;
        public bool required;
        public SdkLockSource source;
    }

    /// <summary>What is installed in this project, gathered by <see cref="SdkLockCheck"/> and handed to <see cref="SdkLockComparer"/>.</summary>
    public sealed class SdkInstalled
    {
        public string EditorVersion;
        /// <summary>UPM package name to resolved version.</summary>
        public Dictionary<string, string> Packages = new Dictionary<string, string>();
        /// <summary>UPM git package name to resolved commit.</summary>
        public Dictionary<string, string> GitRevisions = new Dictionary<string, string>();
        /// <summary>The build line of Assets/Photon/Fusion/build_info.txt, or null when Fusion is not installed.</summary>
        public string FusionBuild;
    }

    /// <summary>Pure comparison of SDKS.lock with what is installed; no Editor state, so a test can drive it.</summary>
    public static class SdkLockComparer
    {
        public const string SetupCommand = "python3 tools/setup_sdks.py";

        /// <summary>One line per difference. Empty when everything matches. It never changes anything.</summary>
        public static List<string> Compare(SdkLockFile lockFile, SdkInstalled installed)
        {
            var differences = new List<string>();
            if (!string.IsNullOrEmpty(lockFile.unity_editor?.version) && installed.EditorVersion != lockFile.unity_editor.version)
                differences.Add($"Unity Editor is {installed.EditorVersion}, SDKS.lock pins {lockFile.unity_editor.version}");
            foreach (var entry in lockFile.entries)
            {
                if (entry.kind == "upm")
                {
                    if (!installed.Packages.TryGetValue(entry.name, out var version))
                    {
                        // Built-in modules are always present; a missing registry package is resolved by the Package Manager.
                        continue;
                    }
                    if (version != entry.version) differences.Add($"{entry.name} is {version}, SDKS.lock pins {entry.version}");
                }
                else if (entry.kind == "upm-git")
                {
                    var wanted = entry.source?.revision;
                    if (installed.GitRevisions.TryGetValue(entry.name, out var revision) && !string.IsNullOrEmpty(wanted)
                        && !revision.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) && !wanted.StartsWith(revision, StringComparison.OrdinalIgnoreCase))
                        differences.Add($"{entry.name} is at git {Short(revision)}, SDKS.lock pins {Short(wanted)}");
                }
                else if (entry.kind == "photon" && entry.name == "photon-fusion")
                {
                    if (installed.FusionBuild == null) differences.Add("Photon Fusion is not installed");
                    else if (installed.FusionBuild != entry.version) differences.Add($"Photon Fusion is {installed.FusionBuild}, SDKS.lock pins {entry.version}");
                }
            }
            return differences;
        }

        public static string Warning(IReadOnlyCollection<string> differences) =>
            $"[SDKS.lock] {differences.Count} installed SDK version(s) differ from SDKS.lock: {string.Join("; ", differences)}. " +
            $"Nothing was changed. To check or fix, run: {SetupCommand} --check";

        private static string Short(string value) => value.Length > 10 ? value.Substring(0, 10) : value;
    }

    /// <summary>
    /// On project load, warns once if an installed SDK version differs from SDKS.lock and names the setup command.
    /// It only reads; it never changes the project.
    /// </summary>
    [InitializeOnLoad]
    public static class SdkLockCheck
    {
        private const string SessionKey = "SpatialBlend.SdkLockCheck.Done";

        static SdkLockCheck()
        {
            if (Application.isBatchMode && Environment.GetEnvironmentVariable("SPATIALBLEND_SKIP_SDK_LOCK_CHECK") == "1") return;
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);
            var warning = CheckProject(Directory.GetParent(Application.dataPath).FullName);
            if (warning != null) Debug.LogWarning(warning);
        }

        [MenuItem("SpatialBlend/Check SDKS.lock")]
        public static void CheckFromMenu()
        {
            var warning = CheckProject(Directory.GetParent(Application.dataPath).FullName);
            Debug.Log(warning ?? "[SDKS.lock] every installed SDK matches SDKS.lock.");
        }

        /// <summary>The warning text, or null when everything matches or there is no lock file.</summary>
        public static string CheckProject(string projectRoot)
        {
            var path = Path.Combine(projectRoot, "SDKS.lock");
            if (!File.Exists(path)) return null;
            SdkLockFile lockFile;
            try { lockFile = JsonUtility.FromJson<SdkLockFile>(File.ReadAllText(path)); }
            catch (Exception) { return $"[SDKS.lock] SDKS.lock could not be read. Run: {SdkLockComparer.SetupCommand} --check"; }
            if (lockFile == null || lockFile.entries == null) return null;
            var differences = SdkLockComparer.Compare(lockFile, Gather(projectRoot));
            return differences.Count == 0 ? null : SdkLockComparer.Warning(differences);
        }

        private static SdkInstalled Gather(string projectRoot)
        {
            var installed = new SdkInstalled { EditorVersion = Application.unityVersion };
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                installed.Packages[package.name] = package.version;
                if (package.git != null && !string.IsNullOrEmpty(package.git.hash)) installed.GitRevisions[package.name] = package.git.hash;
            }
            var buildInfo = Path.Combine(projectRoot, "Assets/Photon/Fusion/build_info.txt");
            if (File.Exists(buildInfo))
            {
                var match = Regex.Match(File.ReadAllText(buildInfo), @"^build:\s*(.+?)\s*$", RegexOptions.Multiline);
                if (match.Success) installed.FusionBuild = match.Groups[1].Value;
            }
            return installed;
        }
    }
}
