// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Puts the TURN credentials Worker's client secret into the build without putting it in git. The source is the file
    /// <c>~/.config/spatialblend/turn-client-secret</c> (mode 600); the destination is the TextAsset
    /// <c>Assets/Resources/SpatialBlendTurnClientSecret.txt</c>, which .gitignore excludes. It runs before every build and as the
    /// menu SpatialBlend > TURN > Write Client Secret. With no source file nothing is written and the build has no TURN: the
    /// carrier then behaves as it did before TURN existed. The secret is never logged.
    /// </summary>
    public sealed class TurnClientSecretSource : IPreprocessBuildWithReport
    {
        public const string SourceRelativeToHome = ".config/spatialblend/turn-client-secret";
        public static readonly string AssetPath = "Assets/Resources/" + TurnClientSecret.ResourceName + ".txt";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => Write(out _);

        [MenuItem("SpatialBlend/TURN/Write Client Secret")]
        public static void WriteFromMenu()
        {
            Debug.Log(Write(out var message) ? "[TURN] " + message : "[TURN] " + message);
        }

        public static string SourcePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SourceRelativeToHome);

        /// <summary>Copies the source file's trimmed text to <see cref="AssetPath"/>. The message names paths only.</summary>
        public static bool Write(out string message)
        {
            var source = SourcePath;
            if (!File.Exists(source)) { message = "no client secret file at " + source + "; this build has no TURN."; return false; }
            var secret = File.ReadAllText(source).Trim();
            if (secret.Length == 0) { message = "the client secret file is empty; this build has no TURN."; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            if (!File.Exists(AssetPath) || File.ReadAllText(AssetPath) != secret)
            {
                File.WriteAllText(AssetPath, secret);
                AssetDatabase.ImportAsset(AssetPath);
            }
            message = "wrote the client secret to " + AssetPath + " (git-ignored).";
            return true;
        }
    }
}
