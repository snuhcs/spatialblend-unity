// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests.RoomMatrix
{
    /// <summary>
    /// The room matrix's input: every MRUK sample room (the prefab rooms and the JSON scenes of the MRUK package), loaded through
    /// MRUK's OWN loaders (LoadSceneFromPrefab, LoadSceneFromJsonString) and exported anchor by anchor (label, world transform,
    /// plane rect and boundary, volume bounds) exactly as MRUK holds them. The matrix (<see cref="RoomMatrixTests"/>) rebuilds
    /// these as MRUKRoom/MRUKAnchor objects and drives the app's real registration from them, so it needs no MRUK native store
    /// (whose process-wide state cannot hold two rooms of two users at once).
    /// Run once per MRUK package version; the export is written to <see cref="ExportDirectory"/>.
    /// </summary>
    [TestFixture]
    [Category("RoomMatrixExport")]
    [Explicit("The room matrix runs only when asked for (its category or name): it is minutes long and needs the exported rooms.")]
    public sealed class MrukRoomExport
    {
        public static string ExportDirectory =>
            Environment.GetEnvironmentVariable("SPATIALBLEND_ROOM_MATRIX_DIR") ?? Path.Combine(Application.dataPath, "../Temp/RoomMatrix");

        [DllImport("libSystem.dylib")] private static extern IntPtr dlopen(string path, int flags);

        [Serializable]
        public sealed class AnchorData
        {
            public string name;
            public long label;
            public Matrix4x4 localToWorld;
            public Vector3 position;
            public Quaternion rotation;
            public bool hasPlane;
            public Rect planeRect;
            public List<Vector2> boundary = new List<Vector2>();
            public bool hasVolume;
            public Vector3 volumeCenter;
            public Vector3 volumeSize;
        }

        [Serializable]
        public sealed class RoomData
        {
            public string name;
            public string source;
            public List<AnchorData> anchors = new List<AnchorData>();
        }

        public static IEnumerable<string> PrefabPaths()
        {
            string root = PackageRoot();
            return Directory.GetFiles(Path.Combine(root, "Core/Rooms/Prefabs"), "*.prefab", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        public static IEnumerable<string> JsonPaths() =>
            Directory.GetFiles(Path.Combine(PackageRoot(), "Core/Rooms/Json"), "*.json").OrderBy(p => p, StringComparer.Ordinal);

        private static string PackageRoot() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MRUK).Assembly).resolvedPath;

        [UnityTest]
        public IEnumerator ExportEveryMrukSampleRoom()
        {
            Directory.CreateDirectory(ExportDirectory);
            OpenSharedLibrary();
            var go = new GameObject("mruk-export");
            go.SetActive(false);
            MRUK mruk = go.AddComponent<MRUK>();
            mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false };
            go.SetActive(true);
            LogAssert.ignoreFailingMessages = true;
            typeof(MRUK).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mruk, null);
            MethodInfo update = typeof(MRUK).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            var failures = new List<string>();
            int exported = 0;
            var sources = PrefabPaths().Select(p => (p, true)).Concat(JsonPaths().Select(p => (p, false))).ToList();
            try
            {
                foreach ((string path, bool prefab) in sources)
                {
                    string rel = "Packages/com.meta.xr.mrutilitykit" + path.Substring(PackageRoot().Length);
                    System.Threading.Tasks.Task<MRUK.LoadDeviceResult> load;
                    if (prefab)
                        load = mruk.LoadSceneFromPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(rel), true);
                    else
                    {
                        mruk.ClearScene();
                        load = mruk.LoadSceneFromJsonString(File.ReadAllText(path));
                    }
                    double deadline = EditorApplication.timeSinceStartup + 30.0;
                    while (!load.IsCompleted && EditorApplication.timeSinceStartup < deadline)
                    {
                        update.Invoke(mruk, null);
                        yield return null;
                    }
                    if (!load.IsCompleted || load.Result != MRUK.LoadDeviceResult.Success)
                    {
                        failures.Add($"{rel}: {(load.IsCompleted ? load.Result.ToString() : "timed out")}");
                        continue;
                    }
                    string baseName = Path.GetFileNameWithoutExtension(path);
                    int index = 0;
                    foreach (MRUKRoom room in mruk.Rooms)
                    {
                        RoomData data = Export(room, mruk.Rooms.Count > 1 ? $"{baseName}#{index}" : baseName, rel);
                        File.WriteAllText(Path.Combine(ExportDirectory, data.name + ".json"), JsonUtility.ToJson(data, true));
                        ++index;
                        ++exported;
                    }
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                mruk.ClearScene();
                UnityEngine.Object.DestroyImmediate(go);
            }
            Debug.Log($"[RoomMatrixExport] exported {exported} rooms from {sources.Count} sources to {ExportDirectory}");
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
            Assert.That(exported, Is.GreaterThanOrEqualTo(sources.Count));
        }

        private static RoomData Export(MRUKRoom room, string name, string source)
        {
            var data = new RoomData { name = name, source = source };
            foreach (MRUKAnchor a in room.Anchors)
            {
                if (a == null) continue;
                var d = new AnchorData
                {
                    name = a.name, label = (long)a.Label, localToWorld = a.transform.localToWorldMatrix,
                    position = a.transform.position, rotation = a.transform.rotation,
                    hasPlane = a.PlaneRect.HasValue, planeRect = a.PlaneRect ?? default,
                    hasVolume = a.VolumeBounds.HasValue,
                    volumeCenter = a.VolumeBounds?.center ?? default, volumeSize = a.VolumeBounds?.size ?? default,
                };
                if (a.PlaneBoundary2D != null) d.boundary.AddRange(a.PlaneBoundary2D);
                data.anchors.Add(d);
            }
            return data;
        }

        private static bool s_open;

        private static void OpenSharedLibrary()
        {
            if (s_open) return;
            Assembly mruk = typeof(MRUK).Assembly;
            string root = UnityEditor.PackageManager.PackageInfo.FindForAssembly(mruk).resolvedPath;
            string folder = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "MacArm" : "Mac";
            if (Application.platform != RuntimePlatform.OSXEditor)
                Assert.Ignore("MRUK's shared library is opened from its macOS build here; the export runs in the macOS Editor only.");
            IntPtr handle = dlopen(root + "/Plugins/" + folder + "/libmrutilitykitshared.dylib", 2);
            Assert.That(handle, Is.Not.EqualTo(IntPtr.Zero), "MRUK shared library did not open");
            mruk.GetType("Meta.XR.MRUtilityKit.MRUKNative", true)
                .GetField("_nativeLibraryPtr", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, handle);
            mruk.GetType("Meta.XR.MRUtilityKit.MRUKNativeFuncs", true)
                .GetMethod("LoadNativeFunctions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Invoke(null, null);
            s_open = true;
        }
    }
}
#endif
