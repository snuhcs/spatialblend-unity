// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using SpatialBlend.Interactions.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders of the control panel in HAND mode over a left hand held palm up, before (5.7 cm over the joint plane, centred over the
    /// knuckles) and after (the controller's clearance from the hand's surface, shifted to the thumb side), with the hand's collision volume drawn as
    /// a translucent box: a side view and the user's view from the eye. Run with
    /// <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.HandPanelShot.Capture -shotDir DIR</c>; needs a graphics device.
    /// </summary>
    public static class HandPanelShot
    {
        private static readonly Vector3 Wrist = new Vector3(-0.18f, 1.18f, 0.38f);
        private static readonly Vector3 Index1 = Wrist + new Vector3(-0.02f, 0f, 0.09f);
        private static readonly Vector3 Middle1 = Wrist + new Vector3(0f, 0f, 0.095f);
        private static readonly Vector3 Pinky1 = Wrist + new Vector3(0.03f, 0f, 0.075f);
        private static readonly Vector3 MiddleTip = Wrist + new Vector3(0f, 0f, 0.18f);
        private static readonly Vector3 Head = new Vector3(0f, 1.62f, 0f);
        private static readonly Vector3 RightHand = new Vector3(0.22f, 1.22f, 0.22f);

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir") ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "hand-panel-shots");
            Directory.CreateDirectory(dir);
            try
            {
                if (UiFonts.Regular == null || UiFonts.SemiBold == null) UiFontAssetBuilder.Generate();
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                FreeformModeSwitchTheme.Install();
                Run(dir);
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[HandPanelShot] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static Material Mat(Color color, bool translucent = false)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { color = color };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (translucent && mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = 3000;
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
            }
            return mat;
        }

        private static GameObject Prim(PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale, Material mat, Transform parent)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static GameObject Hand(Transform parent)
        {
            var root = new GameObject("left-hand");
            root.transform.SetParent(parent, false);
            var skin = Mat(new Color(0.86f, 0.66f, 0.55f));
            Vector3 centre = (Wrist + Index1 + Middle1 + Pinky1) * 0.25f;
            // the palm: a flat box from the wrist to the knuckles, 3 cm thick
            Prim(PrimitiveType.Cube, Vector3.Lerp(Wrist, Middle1, 0.5f), Quaternion.identity, new Vector3(0.085f, 0.03f, 0.115f), skin, root.transform);
            foreach (var (knuckle, length) in new[] { (Index1, 0.08f), (Middle1, 0.09f), (Pinky1 + new Vector3(0.005f, 0f, 0f), 0.065f), (Pinky1 + new Vector3(-0.03f, 0f, 0.0f), 0.075f) })
            {
                Vector3 mid = knuckle + new Vector3(0f, 0f, length * 0.5f);
                Prim(PrimitiveType.Capsule, mid, Quaternion.Euler(90f, 0f, 0f), new Vector3(0.018f, length * 0.5f, 0.018f), skin, root.transform);
            }
            // the thumb, to the thumb side (-x) and forward
            Prim(PrimitiveType.Capsule, Wrist + new Vector3(-0.065f, 0f, 0.07f), Quaternion.Euler(90f, 0f, -30f), new Vector3(0.02f, 0.04f, 0.02f), skin, root.transform);
            return root;
        }

        private static void Volume(Transform parent, ControlPanelHandPlacement.HandVolume volume)
        {
            var box = Prim(PrimitiveType.Cube, volume.Centre, volume.Rotation, volume.HalfExtents * 2f, Mat(new Color(1f, 0.25f, 0.2f, 0.28f), true), parent);
            box.name = "hand-collision-volume";
        }

        private static ControlPanel Panel(Transform anchor, Pose pose)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
            var panel = instance.GetComponent<ControlPanel>();
            panel.BuildGeneratedLayout();
            var follower = instance.GetComponent<ControlPanelAnchor>();
            follower.LeftControllerInUse = () => false;
            follower.LeftControllerTracked = () => false;
            follower.LeftHandInUse = () => true;
            follower.HandPose = () => pose;
            follower.Step(0.016f);
            for (int i = 0; i < 20; i++) follower.Step(0.02f);
            var canvas = (RectTransform)panel.View.Root.parent;
            panel.View.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true }));
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(canvas);
            Canvas.ForceUpdateCanvases();
            return panel;
        }

        private static void Run(string dir)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = ControlPanelScreenshot.NewCamera();
            cam.backgroundColor = new Color(0.86f, 0.88f, 0.91f, 1f);
            ControlPanelHandPlacement.TryCompute(Wrist, Index1, Middle1, Pinky1, true, MiddleTip, out Pose after, out var volume);

            // Before: the bottom-centre 5.7 cm over the joint plane, centred over the knuckles.
            Vector3 centre = (Wrist + Index1 + Middle1 + Pinky1) * 0.25f;
            Vector3 forward = (Middle1 - Wrist).normalized;
            Vector3 normal = after.rotation * Vector3.up;
            Vector3 old = centre + forward * Vector3.Dot(Middle1 - centre, forward) + normal * 0.057f;
            var before = new Pose(old, after.rotation);

            foreach (var (tag, pose) in new[] { ("before", before), ("after", after) })
            {
                var anchor = new GameObject("LeftControllerAnchor").transform;
                Hand(anchor);
                Volume(anchor, volume);
                var panel = Panel(anchor, pose);
                var canvas = (RectTransform)panel.View.Root.parent;
                var corners = new Vector3[4];
                canvas.GetWorldCorners(corners);

                // side view: from the thumb side, level with the hand, looking along the across axis
                cam.orthographic = false;
                cam.fieldOfView = 38f;
                cam.aspect = 1.0f;
                Vector3 target = Middle1 + new Vector3(0f, 0.16f, 0.03f);
                cam.transform.position = target + new Vector3(-1.5f, -0.02f, 0f);
                cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
                Render(cam, Path.Combine(dir, tag + "-side.png"), 1000, 1000);

                // the user's view: the eye at the head, the hand held out in front of the left shoulder
                cam.fieldOfView = 70f;
                cam.aspect = 1.25f;
                cam.transform.position = Head;
                Vector3 look = Vector3.Lerp(Middle1, (corners[0] + corners[2]) * 0.5f, 0.5f);
                cam.transform.rotation = Quaternion.LookRotation(look - Head, Vector3.up);
                Render(cam, Path.Combine(dir, tag + "-view.png"), 1250, 1000);

                // from above, with the right hand's ray origin marked
                var mark = Prim(PrimitiveType.Sphere, RightHand, Quaternion.identity, Vector3.one * 0.04f, Mat(new Color(0.1f, 0.5f, 1f)), anchor);
                cam.fieldOfView = 40f;
                cam.aspect = 1.0f;
                Vector3 top = (Middle1 + RightHand) * 0.5f + new Vector3(0.05f, 0.1f, 0.05f);
                cam.transform.position = top + new Vector3(0f, 1.1f, -0.1f);
                cam.transform.rotation = Quaternion.LookRotation(top - cam.transform.position, Vector3.forward);
                Render(cam, Path.Combine(dir, tag + "-top.png"), 1000, 1000);
                Object.DestroyImmediate(mark);
                Object.DestroyImmediate(anchor.gameObject);
            }
        }

        private static void Render(Camera cam, string path, int w, int h)
        {
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
            Debug.Log("[HandPanelShot] " + path);
            cam.targetTexture = null;
            RenderTexture.active = original;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
