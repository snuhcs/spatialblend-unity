// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using SpatialBlend;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders for the UI unification: every room-anchored widget built by its own code, seen from the eye
    /// with a perspective camera, so their ANGULAR sizes can be compared on one picture: a line-up of all of them at 1.5 m and again
    /// at 3 m (distance growth), and the head-locked layer (the mode chip, its caption, the countdown toast, the toast overlay).
    /// It only calls what every version of the widgets has, so the same file renders the before and the after.
    /// Run with <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.UiUnifyShots.Capture -shotDir DIR</c>; needs a
    /// graphics device (no -nographics). The object menu prefab is drawn by <see cref="ControlPanelScreenshot"/> (prefab-*.png).
    /// </summary>
    public static class UiUnifyShots
    {
        private const int W = 1800, H = 1200;
        private const float VerticalFov = 70f;
        private static readonly Vector3 EyePos = new Vector3(0f, 1.6f, 0f);
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ui-unify-shots");
            Directory.CreateDirectory(dir);
            int code = 0;
            try
            {
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                FreeformModeSwitchTheme.Install();
                LineUp(dir, 1.5f, "unify-lineup-1.5m.png");
                LineUp(dir, 3.0f, "unify-lineup-3m.png");
                HeadLocked(dir);
                CoverageProbe(dir);

            }
            catch (Exception e)
            {
                Debug.LogError("[UiUnifyShots] " + e);
                code = 1;
            }
            EditorApplication.Exit(code);
        }

        private static Camera NewEye(out Transform eye)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Eye", typeof(Camera));
            go.tag = "MainCamera";
            var cam = go.GetComponent<Camera>();
            cam.enabled = true;   // Camera.main (which the widgets face) skips a disabled camera; nothing renders it but Shoot
            cam.fieldOfView = VerticalFov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.36f, 0.37f, 0.38f);   // a neutral stand-in for passthrough
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            go.transform.position = EyePos;
            go.transform.rotation = Quaternion.identity;
            eye = go.transform;
            return cam;
        }

        private static GameObject Target(string name, Vector3 position, float size)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            cube.transform.localScale = Vector3.one * size;
            return cube;
        }

        /// <summary>
        /// Every room widget with its top-centre near a grid point at <paramref name="distance"/> straight ahead: three rows, four columns,
        /// spread in proportion to the distance so the picture keeps its layout and only the widgets' own growth differs.
        /// </summary>
        private static void LineUp(string dir, float distance, string file)
        {
            var cam = NewEye(out var eye);
            float k = distance / 1.5f;
            Vector3 Grid(int col, int row) => EyePos + new Vector3((-0.84f + 0.56f * col) * k, (0.34f - 0.40f * row) * k, distance);
            const float cube = 0.08f;

            // Row 0: the mode-switch prompts over objects (Edit, Menu), the box undo hint, the edit tag.
            var modeGo = new GameObject("ModeSwitchUi");
            var mode = modeGo.AddComponent<ModeSwitchUi>();
            mode.Build(eye);
            var a = Target("EditTarget", Grid(0, 0) - Vector3.up * 0.1f, cube);
            mode.ShowPrompt(a.transform, a.GetComponent<Renderer>().bounds, 0.04f);
            var b = Target("MenuTarget", Grid(1, 0) - Vector3.up * 0.1f, cube);
            mode.ShowMenuPrompt(b.transform, b.GetComponent<Renderer>().bounds, 0.04f);
            var hint = BoxUndoHint.Create(null);
            hint.Show(Grid(2, 0) - Vector3.up * BoxUndoHint.RiseMetres, 1, eye);
            var follow = Target("TagFollow", Grid(3, 0) - Vector3.up * 0.06f, cube);
            var tag = EditPresenceTag.Create("Tag", follow.transform, Vector3.up * 0.06f);
            tag.Show("Moving: Player 2");

            // Row 1: the place callout, the furniture share menu, the world label (the scan save button is gone: Save was removed).
            Try("PlaceCallout", () =>
            {
                var calloutHost = Target("CalloutHost", Grid(1, 1) - Vector3.up * 0.2f, cube);
                var view = calloutHost.AddComponent<PlaceCalloutView>();
                typeof(PlaceCalloutView).GetField("_eye", Any).SetValue(view, eye);
                typeof(PlaceCalloutView).GetMethod("EnsurePanel", Any).Invoke(view, null);
                typeof(PlaceCalloutView).GetMethod("Place", Any).Invoke(view, new object[] { Grid(1, 1) });
            });
            Try("FurnitureShareMenu", () =>
            {
                var anchor = new GameObject("Couch");
                anchor.transform.position = Grid(2, 1) - Vector3.up * 0.13f;
                anchor.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);   // an MRUK volume: local +z is up
                var record = new SceneAnchorRecord { Label = "COUCH", Anchor = anchor, LocalCenter = Vector3.zero, LocalSize = new Vector3(0.2f, 0.2f, 0.1f) };
                var menu = new GameObject("FurnitureShareMenu").AddComponent<FurnitureShareMenu>();
                menu.Show(record);
            });
            Try("UiWorldLabel", () =>
            {
                var labelTarget = new GameObject("LabelTarget");
                labelTarget.transform.position = Grid(3, 1) - new Vector3(0f, 0.15f, 0f);
                var label = UiWorldLabel.Create(labelTarget.transform, "Desk surface", StatusTone.Good);
                label.Viewer = cam;
                typeof(UiWorldLabel).GetMethod("LateUpdate", Any).Invoke(label, null);
            });

            Shoot(cam, Path.Combine(dir, file));
        }

        /// <summary>The head-locked layer: the mode chip in Editing with its caption, the hands' Undo button, the countdown toast, the toast overlay.</summary>
        private static void HeadLocked(string dir)
        {
            var cam = NewEye(out var eye);
            // Look a little up so the chip row (above the eye line) and the overlay (below it) are both in the frame.
            eye.rotation = Quaternion.Euler(-8f, 0f, 0f);
            var modeGo = new GameObject("ModeSwitchUi");
            var mode = modeGo.AddComponent<ModeSwitchUi>();
            mode.Build(eye);
            // The HUD follows the yaw only: snap it with a level eye, then tilt the camera.
            var tilt = eye.rotation;
            eye.rotation = Quaternion.identity;
            mode.SnapHud();
            eye.rotation = tilt;
            mode.SetChipVisible(true);
            mode.SetChipMode(SpatialBlend.Interactions.InteractionMode.SceneEdit);
            mode.ShowCaption("Editing: move things and capture real objects.");
            mode.SetUndoVisible(true);
            mode.ShowToast(5, 0.6f);
            var overlay = UiToastOverlay.Create(0);
            overlay.Viewer = cam;
            typeof(UiToastOverlay).GetMethod("LateUpdate", Any).Invoke(overlay, null);
            overlay.Host.Show(StatusTone.Good, "Joined Room 3", "2 people are here.");
            Shoot(cam, Path.Combine(dir, "unify-headlocked.png"));
        }

        /// <summary>
        /// What a translucent panel writes into the eye buffer's alpha, which the Quest compositor uses to show it over the passthrough
        /// underlay: a 0.5 alpha UI/Default image over a cleared (0, 0, 0, 0) target must leave alpha 0.5 (a + dst *
        /// (1 - a)); a shader that blends alpha by itself would leave 0.25. Also the panel token (PanelTop, 97%). Written to coverage.txt.
        /// </summary>
        private static void CoverageProbe(string dir)
        {
            var cam = NewEye(out var eye);
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            var lines = new System.Text.StringBuilder();
            foreach (var (name, colour) in new[] { ("half-white", new Color(1f, 1f, 1f, 0.5f)), ("PanelTop", UiTheme.Palette.PanelTop) })
            {
                var canvas = new GameObject("Probe", typeof(RectTransform), typeof(Canvas));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                var rect = (RectTransform)canvas.transform;
                rect.sizeDelta = new Vector2(4000f, 4000f);
                rect.localScale = Vector3.one * 0.001f;
                rect.position = EyePos + Vector3.forward;
                var image = new GameObject("Fill", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                image.rectTransform.SetParent(rect, false);
                image.rectTransform.sizeDelta = rect.sizeDelta;
                image.color = colour;
                Canvas.ForceUpdateCanvases();
                var target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = target;
                cam.Render();
                var original = RenderTexture.active;
                RenderTexture.active = target;
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                tex.Apply();
                float alpha = tex.GetPixel(32, 32).a;
                lines.AppendLine($"{name}: source alpha {colour.a:0.000} -> eye-buffer alpha {alpha:0.000} (coverage {(Mathf.Abs(alpha - colour.a) < 0.02f ? "correct" : "WRONG")})");
                RenderTexture.active = original;
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(tex);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(canvas);
            }
            File.WriteAllText(Path.Combine(dir, "coverage.txt"), lines.ToString());
            Debug.Log("[UiUnifyShots] coverage\n" + lines);
        }

        private static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Debug.LogWarning("[UiUnifyShots] " + what + " not drawn: " + (e.InnerException ?? e).Message); }
        }

        private static void Shoot(Camera cam, string path)
        {
            Canvas.ForceUpdateCanvases();
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (canvas.transform is RectTransform rect) LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            Canvas.ForceUpdateCanvases();
            var original = RenderTexture.active;
            var target = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = target;
            cam.aspect = W / (float)H;
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[UiUnifyShots] -> " + path);
            cam.targetTexture = null;
            RenderTexture.active = original;
            UnityEngine.Object.DestroyImmediate(tex);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
#endif
