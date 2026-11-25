// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.AI;
using System.IO;
using System.Text;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    public static class NavMeshExporter
    {
        [Tooltip("Editor path for OBJ (absolute or under Assets)")]
        public static string editorObjSavePath = "Assets/SpatialBlend/Samples/NavMeshBakes/GeneratedNavMesh.obj";

        [Tooltip("Device subfolder under persistentDataPath for OBJ")]
        public static string deviceSaveFolder = "NavMeshBakes";

        public static string LastSavedPath { get; private set; } = string.Empty;

        public static bool TrySaveCurrentNavMeshObj(out string savedPath)
        {
            savedPath = string.Empty;
            var tri = NavMesh.CalculateTriangulation();
            if (tri.indices == null || tri.indices.Length == 0 || tri.vertices == null || tri.vertices.Length == 0)
            {
                Debug.Log("[NavMeshExporter] No triangulation to save.");
                return false;
            }

            string fullPath;
#if UNITY_EDITOR
            fullPath = ResolveEditorObjPath(editorObjSavePath);
            EnsureDirectoryForFile(fullPath);
#else
            string baseDir = Path.Combine(Application.persistentDataPath, deviceSaveFolder);
            try
            {
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[NavMeshExporter] Failed to create folder '{baseDir}': {e.Message}");
                return false;
            }
            string fileName = $"navmesh_{System.DateTime.Now:yyyyMMdd_HHmmss}.obj";
            fullPath = Path.Combine(baseDir, fileName);
#endif

            try
            {
                SaveTriangulationAsObj(tri, fullPath);
                LastSavedPath = fullPath;
                savedPath = fullPath;
                Debug.Log($"[NavMeshExporter] Saved OBJ -> {fullPath}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[NavMeshExporter] Failed to save OBJ: {e.Message}");
                return false;
            }
        }

        private static void SaveTriangulationAsObj(NavMeshTriangulation tri, string path)
        {
            StringBuilder sb = new StringBuilder(1024 * 128);
            sb.AppendLine("# NavMeshExporter OBJ export");
            sb.AppendLine("o NavMesh");
            for (int i = 0; i < tri.vertices.Length; i++)
            {
                Vector3 v = tri.vertices[i];
                sb.Append("v "); sb.Append(v.x.ToString("G9")); sb.Append(' ');
                sb.Append(v.y.ToString("G9")); sb.Append(' ');
                sb.Append(v.z.ToString("G9")); sb.Append('\n');
            }
            for (int i = 0; i < tri.indices.Length; i += 3)
            {
                int i0 = tri.indices[i] + 1;
                int i1 = tri.indices[i + 1] + 1;
                int i2 = tri.indices[i + 2] + 1;
                sb.Append("f "); sb.Append(i0); sb.Append(' '); sb.Append(i1); sb.Append(' '); sb.Append(i2); sb.Append('\n');
            }

            File.WriteAllText(path, sb.ToString());
        }

#if UNITY_EDITOR
        private static string ResolveEditorObjPath(string configuredPath)
        {
            if (string.IsNullOrEmpty(configuredPath))
            {
                return Path.Combine(Application.dataPath, "SpatialBlend/Samples/NavMeshBakes/GeneratedNavMesh.obj");
            }
            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }
            string norm = configuredPath.Replace('\\', '/');
            if (norm.StartsWith("Assets/"))
            {
                string sub = norm.Substring("Assets/".Length);
                return Path.Combine(Application.dataPath, sub);
            }
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, configuredPath);
        }

        private static void EnsureDirectoryForFile(string fullFilePath)
        {
            string dir = Path.GetDirectoryName(fullFilePath);
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
#endif
    }
}


