// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders of the control panel in the two input states: on the left controller and over the left palm. Each is a side
    /// profile and a three-quarter view, at the same camera, so the clearance above what holds the panel can be compared. The hand is a stand-in
    /// drawn from the same joint positions the placement is computed from (wrist, three knuckles, a palm slab, finger bars), not a hand model.
    /// Run with <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.ControlPanelHandShot.Capture -shotDir DIR</c>; needs a graphics device.
    /// </summary>
    public static class ControlPanelHandShot
    {
        private const string ControllerModel = "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx";

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir") ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "hand-anchor-shots");
            Directory.CreateDirectory(dir);
            try
            {
                if (UiFonts.Regular == null || UiFonts.SemiBold == null) UiFontAssetBuilder.Generate();
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                Run(dir);
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[ControlPanelHandShot] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static Material Grey(float v)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { color = new Color(v, v + 0.02f, v + 0.06f) };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", mat.color);
            return mat;
        }

        private static void Bar(Transform parent, Vector3 a, Vector3 b, float thickness, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.position = (a + b) * 0.5f;
            go.transform.up = (b - a).normalized;
            go.transform.localScale = new Vector3(thickness, (b - a).magnitude * 0.5f, thickness);
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        // The stand-in hand: a flattened slab through the wrist and the three knuckles, bars for the fingers, a ball at each joint.
        private static GameObject Hand(Vector3 wrist, Vector3 index1, Vector3 middle1, Vector3 pinky1)
        {
            var root = new GameObject("StandInHand");
            var skin = Grey(0.55f);
            Vector3 forward = (middle1 - wrist).normalized;
            Vector3 across = pinky1 - index1;
            Vector3 up = Vector3.Cross(forward, across).normalized;
            Vector3 centre = (wrist + index1 + middle1 + pinky1) * 0.25f;
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(slab.GetComponent<Collider>());
            slab.transform.SetParent(root.transform, false);
            slab.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(forward, up));
            slab.transform.localScale = new Vector3(across.magnitude + 0.03f, 0.022f, (middle1 - wrist).magnitude + 0.02f);
            slab.GetComponent<Renderer>().sharedMaterial = skin;
            foreach (var knuckle in new[] { index1, middle1, pinky1 })
                Bar(root.transform, knuckle, knuckle + forward * 0.07f, 0.016f, skin);
            return root;
        }

        private static ControlPanel Panel(Transform parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            var panel = instance.GetComponent<ControlPanel>();
            panel.BuildGeneratedLayout();
            var state = new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true };
            panel.View.ApplySession(SessionStatusPresenter.Build(state));
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)panel.View.Root.parent);
            Canvas.ForceUpdateCanvases();
            return panel;
        }

        private static void Run(string dir)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = ControlPanelScreenshot.NewCamera();
            cam.backgroundColor = new Color(0.86f, 0.88f, 0.91f, 1f);
            cam.orthographic = false;
            cam.fieldOfView = 42f;
            cam.aspect = 1f;

            // The controller state, as the controller placement draws it.
            var anchor = new GameObject("LeftControllerAnchor").transform;
            var model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ControllerModel), anchor, false);
            var grey = Grey(0.30f);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true)) r.sharedMaterial = grey;
            Panel(anchor);
            Shoot(cam, new Vector3(0f, 0.27f, 0.12f), dir, "controller");
            Object.DestroyImmediate(anchor.gameObject);

            // The hand state: a left hand palm up, fingers along +z, the panel placed from its joints. The joint layout is the one the unit tests use.
            Vector3 wrist = new Vector3(0f, 0f, -0.02f), index1 = new Vector3(-0.02f, 0f, 0.07f), middle1 = new Vector3(0f, 0f, 0.075f), pinky1 = new Vector3(0.03f, 0f, 0.055f);
            Hand(wrist, index1, middle1, pinky1);
            var holder = new GameObject("LeftControllerAnchor").transform; // the panel's parent when it was a controller's; the follower overrides it
            var panel = Panel(holder);
            ControlPanelHandPlacement.TryCompute(wrist, index1, middle1, pinky1, true, out Pose pose);
            panel.transform.SetPositionAndRotation(pose.position, pose.rotation);
            Shoot(cam, new Vector3(0f, 0.27f, 0.12f), dir, "hand");
        }

        private static void Shoot(Camera cam, Vector3 target, string dir, string tag)
        {
            // Three-quarter view from behind and to the left of the hand, then the side profile (the clearance above the palm / the thumbstick).
            cam.transform.position = target + new Vector3(-0.95f, 0.12f, -0.75f);
            cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
            Render(cam, Path.Combine(dir, tag + "-3q.png"));
            cam.transform.position = target + new Vector3(-1.1f, 0f, 0.0f);
            cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
            Render(cam, Path.Combine(dir, tag + "-side.png"));
        }

        private static void Render(Camera cam, string path)
        {
            const int w = 1000, h = 1000;
            var original = RenderTexture.active;
            var target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = target;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 20f;
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[ControlPanelHandShot] " + path);
            cam.targetTexture = null;
            RenderTexture.active = original;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
