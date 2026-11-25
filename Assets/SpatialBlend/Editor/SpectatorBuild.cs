// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using Fusion;
using Fusion.Editor;
using SpatialBlend.Samples;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// The desktop spectator's scene and its macOS (Apple silicon) build. Run in a disposable copy of the project: BuildMac
    /// switches the active build target.
    ///
    /// Unity -batchmode -nographics -quit -projectPath COPY -buildTarget StandaloneOSX
    ///   -executeMethod SpatialBlend.Editor.SpectatorBuild.BuildMac -spectatorOut /abs/SpatialBlendSpectator.app -logFile LOG
    /// </summary>
    public static class SpectatorBuild
    {
        public const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformSpectator.unity";

        /// <summary>The Freeform scene's scene NetworkObjects, by Fusion's order (sorted by SortKey; NetworkSceneManagerDefault.OnSceneLoaded).</summary>
        public const uint WhiteboardChildSortKey = 92298323, WhiteboardSortKey = 1622921506, ConnectionManagerSortKey = 1731314165, ChessSortKey = 4038426580;
        private const string WhiteboardPrefabGuid = "ca3e42cd095e1be4782f9bec92afd137";
        private const string ChessPrefabGuid = "6eb43724236bbee4bb53046b1a809aff";

        /// <summary>
        /// Writes the spectator scene. Fusion names a scene object by the scene's index and the object's position in the scene's
        /// NetworkObjects sorted by SortKey, so a client binds to the host's objects only when its scene has the same set in the
        /// same order. The Freeform scene has four: the Whiteboard prefab's child and root, the room's FusionConnectionManager
        /// (third) and the Chess prefab. This scene instantiates the same two prefabs and carries the same four SortKeys. The
        /// scene is not re-baked afterwards (SpectatorSceneBakeGuard), because the baker would replace the keys by hashes of this
        /// scene's own object ids.
        /// </summary>
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("Spectator");
            var manager = root.AddComponent<SpatialBlendManager>();
            var so = new SerializedObject(manager);
            so.FindProperty("_connectOnAwake").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            var app = root.AddComponent<FreeformSpectatorApp>();
            var appSo = new SerializedObject(app);
            appSo.FindProperty("_spaceManager").objectReferenceValue = manager;
            appSo.ApplyModifiedPropertiesWithoutUndo();

            var cameraObject = new GameObject("SpectatorCamera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.10f, 0.11f, 0.13f);
            camera.nearClipPlane = 0.05f;
            var rig = cameraObject.AddComponent<SpectatorCamera>();
            cameraObject.AddComponent<AudioListener>();

            var lightObject = new GameObject("Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "ReferenceFloor";
            floor.transform.localScale = new Vector3(2f, 1f, 2f);

            var connectionObject = new GameObject("FusionConnectionManager");
            connectionObject.AddComponent<NetworkObject>();
            var connection = connectionObject.AddComponent<FusionConnectionManager>();
            var connSo = new SerializedObject(connection);
            connSo.FindProperty("_spaceManager").objectReferenceValue = manager;
            connSo.ApplyModifiedPropertiesWithoutUndo();

            var whiteboard = InstantiatePrefab(WhiteboardPrefabGuid, manager);
            var chess = InstantiatePrefab(ChessPrefabGuid, manager);

            var session = root.AddComponent<FreeformSpectatorSession>();
            session.Configure(manager, connection, app, rig);
            // The window's typeface: a font set outside Resources is in a player only when a scene references it.
            var sessionSo = new SerializedObject(session);
            sessionSo.FindProperty("_fonts").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<SpatialBlend.Samples.FreeformCollaboration.UI.UiFontSet>(SpatialBlend.Samples.FreeformCollaboration.UI.UiFonts.SetPath);
            sessionSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(session);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            // First save: the baker fills the flags, the behaviour lists and the nesting of every NetworkObject.
            SpectatorSceneBakeGuard.Bake = true;
            try { if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Could not save " + ScenePath); }
            finally { SpectatorSceneBakeGuard.Bake = false; }

            // Then the SortKeys of the Freeform scene. The scene is not baked again.
            var objects = UnityEngine.Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var whiteboardObjects = objects.Where(o => o.transform.IsChildOf(whiteboard.transform)).ToArray();
            var chessObjects = objects.Where(o => o.transform.IsChildOf(chess.transform)).ToArray();
            if (whiteboardObjects.Length != 2 || chessObjects.Length != 1 || objects.Length != 4)
                throw new InvalidOperationException($"Expected the Freeform scene's 4 NetworkObjects (2 whiteboard, 1 chess, 1 connection manager), found {objects.Length} ({whiteboardObjects.Length} whiteboard, {chessObjects.Length} chess).");
            // The Freeform scene's overrides name the prefab's own NetworkObject components by their file ids.
            SetSortKey(whiteboardObjects.Single(o => SourceFileId(o) == WhiteboardChildFileId), WhiteboardChildSortKey);
            SetSortKey(whiteboardObjects.Single(o => SourceFileId(o) == WhiteboardFileId), WhiteboardSortKey);
            SetSortKey(connectionObject.GetComponent<NetworkObject>(), ConnectionManagerSortKey);
            SetSortKey(chessObjects[0], ChessSortKey);
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Could not save " + ScenePath);
            AssetDatabase.ImportAsset(ScenePath);
            VerifyNetworkObjectOrder();
            Debug.Log("[SpectatorBuild] wrote " + ScenePath);
        }

        private const ulong WhiteboardChildFileId = 6291845339452228689UL, WhiteboardFileId = 7004840941358847307UL;

        private static ulong SourceFileId(NetworkObject o)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(o);
            return source == null ? 0UL : GlobalObjectId.GetGlobalObjectIdSlow(source).targetObjectId;
        }

        private static GameObject InstantiatePrefab(string guid, SpatialBlendManager manager)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) throw new InvalidOperationException("Prefab not found: " + guid);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            // The Freeform scene wires its manager into these prefabs' _spaceManager fields.
            foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var so = new SerializedObject(behaviour);
                var property = so.FindProperty("_spaceManager");
                if (property == null || property.propertyType != SerializedPropertyType.ObjectReference) continue;
                property.objectReferenceValue = manager;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            return instance;
        }

        private static void SetSortKey(NetworkObject target, uint value)
        {
            var so = new SerializedObject(target);
            so.FindProperty("SortKey").longValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
            EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
        }

        /// <summary>Fails unless the saved scene has the Freeform scene's four SortKeys and the connection manager is third.</summary>
        public static void VerifyNetworkObjectOrder()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sorted = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NetworkObject>(true)).OrderBy(o => o.SortKey).ToArray();
            var keys = string.Join(",", sorted.Select(o => o.SortKey));
            var expected = $"{WhiteboardChildSortKey},{WhiteboardSortKey},{ConnectionManagerSortKey},{ChessSortKey}";
            if (keys != expected || sorted[2].GetComponent<FusionConnectionManager>() == null)
                throw new InvalidOperationException($"Spectator scene NetworkObject order is {keys}, expected {expected} with the FusionConnectionManager at index 2.");
            Debug.Log("[SpectatorBuild] scene NetworkObjects by index: " + string.Join(" | ", sorted.Select((o, i) => $"{i}:{o.name}:{o.SortKey}")));
        }

        public static void BuildMac()
        {
            var output = ArgumentAfter("-spectatorOut");
            if (string.IsNullOrEmpty(output))
            {
                Debug.LogError("[SpectatorBuild] Missing -spectatorOut /abs/path/SpatialBlendSpectator.app");
                EditorApplication.Exit(2);
                return;
            }
            try
            {
                if (!File.Exists(ScenePath)) CreateScene();
                // The spectator scene is the only scene, so it is build index 0 as Fusion's scene object identity needs.
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
#if UNITY_EDITOR_OSX
                UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.ARM64;
#else
                throw new InvalidOperationException("BuildMac needs the macOS editor (Apple silicon).");
#endif
                PlayerSettings.productName = "SpatialBlendSpectator";
                // The shared SpatialBlend code references the microphone and camera classes (voice, scanning); the spectator never opens them.
                PlayerSettings.macOS.microphoneUsageDescription = "The spectator does not use the microphone.";
                PlayerSettings.macOS.cameraUsageDescription = "The spectator does not use the camera.";
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.defaultScreenWidth = 1280;
                PlayerSettings.defaultScreenHeight = 800;
                PlayerSettings.runInBackground = true;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = output,
                    target = BuildTarget.StandaloneOSX,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                });
                var summary = report.summary;
                Debug.Log($"[SpectatorBuild] result={summary.result} errors={summary.totalErrors} warnings={summary.totalWarnings} output={output}");
                EditorApplication.Exit(summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError("[SpectatorBuild] " + e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// <c>-spectatorNoTurnSecret</c>: removes the TURN client secret that <see cref="TurnClientSecretSource"/> copied into
        /// Resources before this build, so the app has no TURN and no secret inside it. Runs after that step.
        /// </summary>
        private sealed class NoTurnSecretInBuild : IPreprocessBuildWithReport
        {
            public int callbackOrder => 100;

            public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
            {
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-spectatorNoTurnSecret") < 0) return;
                if (AssetDatabase.DeleteAsset(TurnClientSecretSource.AssetPath)) Debug.Log("[SpectatorBuild] the TURN client secret is not part of this build");
            }
        }

        private static string ArgumentAfter(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
