// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Batchmode entry used by tools/setup_sdks.py: imports TextMesh Pro Essential Resources from the project's own pinned
    /// package (com.unity.ugui 2.x ships them; com.unity.textmeshpro is the fallback for older pins).
    ///   Unity -batchmode -nographics -projectPath P -executeMethod SpatialBlend.Editor.TmpEssentialsImport.Run
    /// AssetDatabase.ImportPackage is asynchronous, so do NOT pass -quit (it exits before the import finishes): the method exits
    /// itself from the completion callbacks. Exit 0 only when
    /// TMP_Settings loads with a default font asset afterwards.
    /// </summary>
    public static class TmpEssentialsImport
    {
        private static readonly string[] Candidates =
        {
            "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage",
            "Packages/com.unity.textmeshpro/Package Resources/TMP Essential Resources.unitypackage",
        };

        public static void Run()
        {
            if (TmpEssentialsBuildGuard.Verify() == null)
            {
                Debug.Log("[TmpEssentialsImport] already present");
                EditorApplication.Exit(0);
                return;
            }

            string package = null;
            foreach (var candidate in Candidates)
            {
                if (File.Exists(Path.GetFullPath(candidate)))
                {
                    package = candidate;
                    break;
                }
            }

            if (package == null)
            {
                Debug.LogError("[TmpEssentialsImport] TMP Essential Resources.unitypackage not found in com.unity.ugui or com.unity.textmeshpro");
                EditorApplication.Exit(2);
                return;
            }

            AssetDatabase.importPackageCompleted += _ =>
            {
                AssetDatabase.Refresh();
                var problem = TmpEssentialsBuildGuard.Verify();
                Debug.Log("[TmpEssentialsImport] imported " + package + (problem == null ? " ok" : " but " + problem));
                EditorApplication.Exit(problem == null ? 0 : 1);
            };
            AssetDatabase.importPackageFailed += (name, error) =>
            {
                Debug.LogError("[TmpEssentialsImport] import failed: " + error);
                EditorApplication.Exit(1);
            };
            AssetDatabase.ImportPackage(package, false);
        }
    }
}
