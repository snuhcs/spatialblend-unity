// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Reflection;
using SpatialBlend.Interactions.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders of the left-controller control panel over the Quest 3 controller model, before and after the placement
    /// change, in the lobby and in a room: a side profile (shows the clearance above the thumbstick) and the user's view from the eye at the
    /// reference pose with the mode chip, the mode-switch toast and the toast overlay in place (shows the overlap rule). Run with
    /// <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.ControlPanelPlacementShot.Capture -shotDir DIR</c>; needs a graphics device.
    /// "Before" re-applies the old numbers (centre pivot, offset 0, 0.0724, 0.1499, 0.00052 m per unit) over the same prefab.
    /// </summary>
    public static class ControlPanelPlacementShot
    {
        private static readonly Vector3 OldOffset = new Vector3(0f, 0.07243666f, 0.14988577f);
        private const float OldMetresPerUnit = 0.00052f;
        private const string ControllerModel = "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx";

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir") ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "placement-shots");
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
                Debug.LogError("[ControlPanelPlacementShot] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static GameObject Controller(Transform anchor)
        {
            var model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ControllerModel), anchor, false);
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mat = new Material(shader);
                // Body mid-grey, so the panel's dark card and the model both read against the light backdrop.
                mat.color = r.name.Contains("battery") ? new Color(0.2f, 0.9f, 0.5f) : new Color(0.30f, 0.32f, 0.36f);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", mat.color);
                r.sharedMaterial = mat;
            }
            return model;
        }

        private static ControlPanel Panel(Transform anchor, bool after, SessionStatusInput state)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
            var panel = instance.GetComponent<ControlPanel>();
            panel.BuildGeneratedLayout();
            var canvas = (RectTransform)panel.View.Root.parent;
            if (!after)
            {
                float inner = canvas.localScale.x;
                canvas.pivot = new Vector2(0.5f, 0.5f);
                instance.transform.localPosition = OldOffset;
                instance.transform.localScale = Vector3.one * (OldMetresPerUnit / inner);
            }
            panel.View.ApplySession(SessionStatusPresenter.Build(state));
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
            var lobby = new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true };
            var room = new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connected, Role = CoreParticipantRole.Host, LibraryCompatible = true, RoomName = "Room 3", PeopleCount = 2 };

            foreach (bool after in new[] { false, true })
            foreach (var (name, state) in new[] { ("lobby", lobby), ("in-room", room) })
            {
                string tag = (after ? "after-" : "before-") + name;

                // 1. Three-quarter view with the anchor at the origin (shows the clearance above the thumbstick).
                var anchor = new GameObject("LeftControllerAnchor").transform;
                Controller(anchor);
                Panel(anchor, after, state);
                // Three-quarter view from behind and to the left of the hand (the panel's face looks back and up), 1.1 m away.
                cam.orthographic = false;
                cam.fieldOfView = 42f;
                cam.aspect = 1.0f;
                var target = new Vector3(0f, 0.27f, 0.12f);
                cam.transform.position = target + new Vector3(-0.95f, 0.12f, -0.75f);
                cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
                Render(cam, Path.Combine(dir, tag + "-3q.png"), 1000, 1000);
                Object.DestroyImmediate(anchor.gameObject);

                // 2. The user's view: the eye is the origin; the anchor is at the reference pose; HUD pieces at their eye-frame places.
                anchor = new GameObject("LeftControllerAnchor").transform;
                anchor.position = UiTheme.HandReferencePose;
                Controller(anchor);
                var panel = Panel(anchor, after, state);
                var hudHost = new GameObject("hud");
                var ui = hudHost.AddComponent<ModeSwitchUi>();
                ui.Build(null);
                var hud = ui.ChipObject.transform.parent;
                hud.position = new Vector3(0f, -ModeSwitchUi.HudDropMetres, ModeSwitchUi.HudDistanceMetres);
                hud.rotation = Quaternion.identity;
                ui.ChipObject.SetActive(name == "in-room");
                ui.ToastObject.SetActive(name == "in-room");
                var overlay = UiToastOverlay.Create(0);
                overlay.transform.position = UiToastOverlay.PositionFor(Vector3.zero, Vector3.forward);
                overlay.transform.rotation = Quaternion.LookRotation(overlay.transform.position, Vector3.up);
                if (name == "in-room") overlay.Host.Show(StatusTone.Good, "In Room 3 · 2 people", string.Empty);
                Canvas.ForceUpdateCanvases();
                cam.orthographic = false;
                cam.fieldOfView = 95f;
                cam.aspect = 1.25f;
                cam.transform.position = Vector3.zero;
                var corners = new Vector3[4];
                ((RectTransform)panel.View.Root.parent).GetWorldCorners(corners);
                var centre = corners.Aggregate(Vector3.zero, (a, c) => a + c) * 0.25f;
                // Look at the middle of the HUD and the panel together, so both are in frame.
                cam.transform.rotation = Quaternion.LookRotation(Vector3.Lerp(centre, hud.position, 0.5f).normalized, Vector3.up);
                Render(cam, Path.Combine(dir, tag + "-view.png"), 1250, 1000);
                Object.DestroyImmediate(anchor.gameObject);
                Object.DestroyImmediate(hudHost);
                Object.DestroyImmediate(overlay.gameObject);
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
            Debug.Log("[ControlPanelPlacementShot] " + path);
            cam.targetTexture = null;
            RenderTexture.active = original;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
