// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders of every capture surface: the scan panel (camera view, captured photo with taps and mask, camera
    /// messages, the permission card, the failure toast). Run with
    /// <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.CaptureUiScreenshot.Capture -shotDir DIR -shotPrefix before</c>;
    /// needs a graphics device (no -nographics), like <see cref="ControlPanelScreenshot"/>, whose helpers it shares.
    /// </summary>
    public static class CaptureUiScreenshot
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir") ?? System.Environment.GetEnvironmentVariable("CAPTURE_UI_SHOT_DIR");
            string prefix = ControlPanelScreenshot.Arg("-shotPrefix") ?? "shot";
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "capture-ui-shots");
            Directory.CreateDirectory(dir);
            try
            {
                if (UiFonts.Regular == null || UiFonts.SemiBold == null) UiFontAssetBuilder.Generate();
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                Run(dir, prefix);
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CaptureUiScreenshot] " + e);
                EditorApplication.Exit(1);
            }
        }

        internal static GameObject NewPanelPublic(Camera cam, out PassthroughPanelManager panel) => NewPanel(cam, out panel);
        internal static Texture2D PhotoPublic() => Photo();

        private static GameObject NewPanel(Camera cam, out PassthroughPanelManager panel)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Scanning/Prefabs/PassthroughPanel.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true)) canvas.worldCamera = cam;
            panel = instance.GetComponent<PassthroughPanelManager>();
            // Awake does not run for an instance made outside play mode; it wires the buttons and the image.
            Call(panel, "Awake");
            return instance;
        }

        private static object Call(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method, Any);
            return m == null ? null : m.Invoke(target, args);
        }

        private static void Layout(GameObject root)
        {
            foreach (var group in root.GetComponentsInChildren<LayoutGroup>(true))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
            Canvas.ForceUpdateCanvases();
        }

        private static Bounds Drawn(GameObject root)
        {
            Bounds? bounds = null;
            var corners = new Vector3[4];
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(false))
            {
                if (!graphic.enabled || graphic.canvasRenderer == null || graphic.color.a <= 0.01f) continue;
                graphic.rectTransform.GetWorldCorners(corners);
                if (corners[2] == corners[0]) continue;
                if (bounds == null) bounds = new Bounds(corners[0], Vector3.zero);
                var b = bounds.Value;
                foreach (var c in corners) b.Encapsulate(c);
                bounds = b;
            }
            return bounds ?? new Bounds(root.transform.position, Vector3.one * 0.1f);
        }

        private static void Shot(Camera cam, GameObject root, string dir, string name)
        {
            Layout(root);
            var title = root.transform.Find("Canvas/TitleGroup");
            if (title != null && name.EndsWith("camera-view"))
                foreach (var r in title.GetComponentsInChildren<RectTransform>(true))
                    Debug.Log($"[CaptureUiScreenshot] TITLE '{r.name}' active={r.gameObject.activeInHierarchy} {r.rect.width * r.lossyScale.x * 100f:0.0}x{r.rect.height * r.lossyScale.y * 100f:0.0}cm comps={string.Join(",", System.Array.ConvertAll(r.GetComponents<Component>(), c => c == null ? "null" : c.GetType().Name))}");
            foreach (var t in root.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                var tr = t.rectTransform;
                Debug.Log($"[CaptureUiScreenshot] {name} text '{t.text}' font={t.fontSize:0.#} size={tr.rect.width:0}x{tr.rect.height:0} color={t.color} alpha={t.canvasRenderer.GetAlpha()}");
            }
            foreach (var b in root.GetComponentsInChildren<Button>(false))
            {
                var r = (RectTransform)b.transform;
                Debug.Log($"[CaptureUiScreenshot] {name} button '{b.name}' {r.rect.width * r.lossyScale.x * 100f:0.0} x {r.rect.height * r.lossyScale.y * 100f:0.0} cm");
            }
            ControlPanelScreenshot.Shoot(cam, dir, name, Drawn(root));
        }

        private static Texture2D Photo()
        {
            var photo = SpatialBlend.Samples.FreeformCollaboration.Tutorial.TutorialAssets.LoadSamplePhoto();
            if (photo != null) return photo;
            var fallback = new Texture2D(640, 480, TextureFormat.RGB24, false);
            for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++) fallback.SetPixel(x, y, Color.Lerp(new Color(0.35f, 0.3f, 0.25f), new Color(0.6f, 0.58f, 0.5f), y / 480f));
            fallback.Apply();
            return fallback;
        }

        private static void Run(string dir, string prefix)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = ControlPanelScreenshot.NewCamera();
            var photo = Photo();

            // 1. The camera view (a still stands in for the passthrough image), then the captured photo with taps and the mask.
            var root = NewPanel(cam, out var panel);
            panel.StillOverride = photo;
            panel.OpenPanel();
            Shot(cam, root, dir, prefix + "-1-camera-view");
            panel.CaptureImage();
            var pick = panel.GetType().GetMethod("AddPickedPoint", Any);
            pick.Invoke(panel, new object[] { new Vector2(photo.width * 0.45f, photo.height * 0.5f), new Vector2(0.45f, 0.5f) });
            pick.Invoke(panel, new object[] { new Vector2(photo.width * 0.55f, photo.height * 0.42f), new Vector2(0.55f, 0.42f) });
            var mask = new byte[160 * 120];
            for (int y = 0; y < 120; y++) for (int x = 0; x < 160; x++) { float dx = (x - 78) / 38f, dy = (y - 62) / 30f; if (dx * dx + dy * dy < 1f) mask[y * 160 + x] = 255; }
            Call(panel, "UpdateMaskOverlay", mask, 160, 120);
            Shot(cam, root, dir, prefix + "-2-captured-taps-mask");
            Object.DestroyImmediate(root);

            // 2. No camera: nothing to show (the panel's own message).
            root = NewPanel(cam, out panel);
            panel.OpenPanel();
            Call(panel, "ShowCameraUnavailableError");
            Shot(cam, root, dir, prefix + "-3-camera-unavailable");
            Object.DestroyImmediate(root);

            // 3. Messages the after-version adds (absent before: nothing is rendered).
            CaptureUiScreenshotAfter.Run(cam, dir, prefix);

            Object.DestroyImmediate(photo);
        }

        internal static void ShotPublic(Camera cam, GameObject root, string dir, string name) => Shot(cam, root, dir, name);
    }
}
#endif
