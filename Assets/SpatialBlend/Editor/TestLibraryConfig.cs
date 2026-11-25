// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEditor;
using System.Linq;
using SpatialBlend.Internal;

namespace SpatialBlend.Editor
{
    public class TestLibraryConfig
    {
        [MenuItem("SpatialBlend/Test Library Configuration")]
        public static void TestLibraryConfiguration()
        {
            var expectedLibraries = SpatialBlendLibraryConfig.ExpectedLibraries.GetExpectedLibraries();

            Debug.Log($"[SpatialBlend Test] Expected libraries for this platform:");
            foreach (var lib in expectedLibraries)
            {
                Debug.Log($"  - {lib}");
            }

            Debug.Log($"[SpatialBlend Test] Total expected libraries: {expectedLibraries.Length}");

            // Check which libraries are actually present
            var missingLibraries = new System.Collections.Generic.List<string>();
            foreach (var libPath in expectedLibraries)
            {
                var fullPath = System.IO.Path.Combine(SpatialBlendLibraryConfig.Paths.PLUGINS_DIR, libPath);
                if (!System.IO.File.Exists(fullPath))
                {
                    missingLibraries.Add(libPath);
                }
            }

            if (missingLibraries.Count == 0)
            {
                Debug.Log("[SpatialBlend Test] ✅ All expected libraries are present!");
            }
            else
            {
                Debug.LogWarning($"[SpatialBlend Test] ⚠️ Missing libraries: {string.Join(", ", missingLibraries)}");
            }
        }
    }
}