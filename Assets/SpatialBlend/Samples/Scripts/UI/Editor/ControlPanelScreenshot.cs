// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SpatialBlend.Samples;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode capture of every themed headset surface. The control panel is built on a canvas that mirrors the
    /// ControlPanel prefab's host canvas and rendered once per tab (plus with toasts, plus the room list); the toast and
    /// label overlay is built from the same kit; each themed prefab is instantiated and framed on its drawn bounds. Each
    /// goes to an orthographic camera and a RenderTexture. Run with
    /// <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.ControlPanelScreenshot.Capture</c> and
    /// <c>-shotDir DIR</c> (or CONTROLPANEL_SHOT_DIR); this needs a graphics device (no -nographics).
    /// </summary>
    public static class ControlPanelScreenshot
    {
        private const float HostCanvasWidth = 406.396f;   // the prefab's legacy canvas; the panel resizes it
        private const float TargetPixelsWide = 1100f;

        internal static string Arg(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        public static void Capture()
        {
            string dir = Arg("-shotDir") ?? System.Environment.GetEnvironmentVariable("CONTROLPANEL_SHOT_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "controlpanel-shots");
            Directory.CreateDirectory(dir);
            try
            {
                if (UiFonts.Regular == null || UiFonts.SemiBold == null) UiFontAssetBuilder.Generate();
                UiFonts.Register(AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath));
                Debug.Log("[ControlPanelScreenshot] fonts: " + (UiFonts.Regular != null ? UiFonts.Regular.name : "NONE (labels use the TMP default)"));
                Run(dir);
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[ControlPanelScreenshot] " + e);
                EditorApplication.Exit(1);
            }
        }

        internal static Camera NewCamera()
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.50f, 0.52f, 0.55f, 1f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            return cam;
        }

        internal static RectTransform NewHostCanvas(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(HostCanvasWidth, 0f);
            rect.localScale = Vector3.one * UiTheme.MetresPerUnit * 1000f;   // 1 unit = 0.523 mm, drawn in millimetres
            // The prefab canvas carries its own Image, layout and size fitter; the generated panel takes them over.
            go.AddComponent<Image>().color = new Color(0.15686275f, 0.23529413f, 0.2784314f, 1f);
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(24, 24, 24, 24);
            vlg.spacing = 12f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = false;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.childScaleHeight = true;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rect;
        }

        private static void Run(string dir)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = NewCamera();

            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            settings.fadeAvatarDuration = 0.5f;
            var states = new (string name, SessionStatusInput input, bool tutorial, bool diagnostics)[]
            {
                ("default", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true }, true, false),
                ("desktop-lobby", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true }, true, false),
                ("connecting", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connecting, Role = CoreParticipantRole.Client, LibraryCompatible = true }, true, false),
                ("in-room", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connected, Role = CoreParticipantRole.Host, LibraryCompatible = true, RoomName = "Room 3", PeopleCount = 2 }, true, false),
                ("error", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Failed, LibraryCompatible = true, CanRetry = true }, true, false),
                ("needs-update", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = false }, false, false),
                ("diagnostics", new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connected, Role = CoreParticipantRole.Host, LibraryCompatible = true, CarrierText = "Photon fallback", RoomName = "Room 3", PeopleCount = 2 }, false, true),
            };
            RectTransform rect = null;
            ControlPanelView view = null;
            foreach (var state in states)
            {
                if (rect != null) Object.DestroyImmediate(rect.gameObject);
                rect = NewHostCanvas("ControlPanelCanvas");
                rect.sizeDelta = new Vector2(UiTheme.PanelWidth, 0f);
                var avatars = new AvatarSettingsPresenter(() => settings, () => { });
                var scene = new SceneControlsPresenter(null, null, null, null);
                scene.SetBrightness(0.25f);
                view = ControlPanelView.Build(rect, UiTheme.PanelWidth,
                    new ControlPanelActions { Tutorial = state.tutorial ? () => { } : null, Leave = () => { }, JoinAsObserver = state.name == "desktop-lobby" ? () => { } : null },
                    avatars, scene, state.diagnostics, state.name == "desktop-lobby" ? RuntimePlatform.WindowsPlayer : RuntimePlatform.Android);
                view.SetNavMeshNote("NavMesh source not assigned.");
                view.ApplySession(SessionStatusPresenter.Build(state.input));
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Canvas.ForceUpdateCanvases();
                Shoot(cam, dir, "controlpanel-" + state.name, RectBounds(rect));
            }
            Object.DestroyImmediate(rect.gameObject);
            rect = NewHostCanvas("ControlPanelCanvas");
            rect.sizeDelta = new Vector2(UiTheme.PanelWidth, 0f);
            view = ControlPanelView.Build(rect, UiTheme.PanelWidth, new ControlPanelActions(),
                new AvatarSettingsPresenter(() => settings, () => { }), new SceneControlsPresenter(null, null, null, null));
            view.ApplySession(SessionStatusPresenter.Build(states[0].input));
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

            // In a room: the wrist panel plus the toast overlay, which is a separate canvas in front of the user. Here it is drawn
            // beside the panel at its own (larger) scale, as it would appear over the view.
            view.ApplySession(SessionStatusPresenter.Build(System.Array.Find(states, st => st.name == "in-room").input));
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            var toastOverlay = UiToastOverlay.Create(0);
            toastOverlay.Host.Show(StatusTone.Good, "In Room 3 · 2 people", string.Empty);
            toastOverlay.Host.Show(StatusTone.Bad, RegionRefusalText.Title, RegionRefusalText.ToUserMessage("Region not created: The declared extent must be a simple polygon."));
            Canvas.ForceUpdateCanvases();
            var panelBounds = RectBounds(rect);
            // Place the overlay (0.6 mm units) to the right of the panel (0.52 mm units), centred vertically.
            toastOverlay.transform.localScale = Vector3.one * (UiTheme.MetresPerUnit * 1000f) * (UiToastOverlay.UnitScale / UiTheme.MetresPerUnit);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)toastOverlay.Host.transform);
            Canvas.ForceUpdateCanvases();
            var hostBounds = RectBounds((RectTransform)toastOverlay.Host.transform);
            toastOverlay.transform.position += new Vector3(panelBounds.max.x + 24f - hostBounds.min.x, panelBounds.center.y - hostBounds.center.y, 0f);
            Canvas.ForceUpdateCanvases();
            var both = panelBounds;
            both.Encapsulate(RectBounds((RectTransform)toastOverlay.Host.transform));
            Shoot(cam, dir, "controlpanel-in-room-toast-overlay", both);
            Object.DestroyImmediate(toastOverlay.gameObject);

            // The room list over the same canvas.
            var browser = new GameObject("browser", typeof(ShotSessionBrowser)).GetComponent<ShotSessionBrowser>();
            var list = SessionListPanel.Create(view.Connect, browser);
            list.Show();
            browser.ApplySessions(new[]
            {
                new FusionSessionEntry("design-review", 2, 9),
                new FusionSessionEntry("studio-a", 9, 9),
                new FusionSessionEntry("workshop", 1, 9),
            });
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            Shoot(cam, dir, "roomlist", RectBounds((RectTransform)list.transform));
            Object.DestroyImmediate(list.gameObject);
            Object.DestroyImmediate(rect.gameObject);

            // Toast cards and in-world labels, the kit pieces on their own.
            var overlay = NewHostCanvas("OverlayCanvas");
            overlay.GetComponent<Image>().color = Color.clear;
            var overlayLayout = overlay.GetComponent<VerticalLayoutGroup>();
            overlayLayout.spacing = 20f;
            overlayLayout.childControlWidth = false;
            Toast(overlay, StatusTone.Good, "Joined room", "You are in \"design-review\" with 3 others.");
            Toast(overlay, StatusTone.Busy, "Reconnecting", "The host connection dropped. Trying again.");
            Toast(overlay, StatusTone.Bad, "Region refused", "The outline crosses itself. Draw it again.");
            ControlPanelKit.AddWorldLabel(overlay, "Jiwon", StatusTone.Good);
            ControlPanelKit.AddWorldLabel(overlay, "Desk surface");
            Canvas.ForceUpdateCanvases();
            Shoot(cam, dir, "overlay-toast-label", RectBounds(overlay));
            Object.DestroyImmediate(overlay.gameObject);

            // Every themed prefab on its drawn bounds.
            foreach (string path in UiPrefabSkinner.Prefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true)) canvas.worldCamera = cam;
                foreach (var group in instance.GetComponentsInChildren<LayoutGroup>(true))
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
                // The object menu of the user's own scan: Delete and Close.
                var objectMenu = instance.GetComponentInChildren<SpatialBlend.Interactions.UI.ManipulationPopUp>(true);
                if (objectMenu != null)
                {
                    objectMenu.PreviewScanMenu();
                    RebuildLayouts(instance);
                    Shoot(cam, dir, "prefab-" + prefab.name + "-scan", DrawnBounds(instance));
                    // A copy received from a peer: the owner line, Hide for me, Close.
                    foreach (var (state, owner) in new[] { ("received", (string)null), ("received-owner", "Jiwon") })
                    {
                        objectMenu.PreviewReceivedMenu(owner);
                        RebuildLayouts(instance);
                        Shoot(cam, dir, "prefab-" + prefab.name + "-" + state, DrawnBounds(instance));
                    }
                    // Every Delete asks: the question on a scan and a box.
                    foreach (var (state, scan) in new[] { ("delete-confirm", true), ("delete-confirm-box", false) })
                    {
                        objectMenu.PreviewDeleteConfirm(scan);
                        RebuildLayouts(instance);
                        Shoot(cam, dir, "prefab-" + prefab.name + "-" + state, DrawnBounds(instance));
                    }
                    objectMenu.CancelDeleteConfirm();
                    objectMenu.RefreshMenu(); // no target: the box-scanning menu, as shipped
                    RebuildLayouts(instance);
                }
                Canvas.ForceUpdateCanvases();
                Shoot(cam, dir, "prefab-" + prefab.name, DrawnBounds(instance));
                Object.DestroyImmediate(instance);
            }
        }

        private static void RebuildLayouts(GameObject instance)
        {
            foreach (var group in instance.GetComponentsInChildren<LayoutGroup>(true))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
            Canvas.ForceUpdateCanvases();
        }

        private static void Toast(RectTransform parent, StatusTone tone, string title, string message)
        {
            var toast = ControlPanelKit.AddToast(parent, tone, title, message);
            toast.sizeDelta = new Vector2(HostCanvasWidth - 48f, 0f);
        }

        private sealed class ShotSessionBrowser : FusionSessionBrowser
        {
            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) { }
            protected override Task<string> JoinLobbyAsync() => Task.FromResult<string>(null);
            protected override Task CloseLobbyAsync() => Task.CompletedTask;
        }

        /// <summary>World-space bounds of a rect (and the overflow of its children).</summary>
        internal static Bounds RectBounds(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var b = new Bounds(corners[0], Vector3.zero);
            foreach (var c in corners) b.Encapsulate(c);
            return b;
        }

        /// <summary>World bounds of everything a prefab draws (each enabled graphic's rect), so overflow past the canvas is framed.</summary>
        private static Bounds DrawnBounds(GameObject root)
        {
            Bounds? bounds = null;
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(false))
            {
                if (!graphic.enabled || graphic.canvasRenderer == null || graphic.color.a <= 0.01f) continue;
                var corners = new Vector3[4];
                graphic.rectTransform.GetWorldCorners(corners);
                if (corners[2] == corners[0]) continue;
                if (bounds == null) bounds = new Bounds(corners[0], Vector3.zero);
                var b = bounds.Value;
                foreach (var c in corners) b.Encapsulate(c);
                bounds = b;
            }
            return bounds ?? new Bounds(root.transform.position, Vector3.one * 0.1f);
        }

        internal static void Shoot(Camera cam, string dir, string fileName, Bounds world)
        {
            RenderTexture original = RenderTexture.active;
            var camTransform = cam.transform;
            float margin = Mathf.Max(world.size.x, world.size.y) * 0.08f + 0.004f;
            float worldW = world.size.x + margin * 2f, worldH = world.size.y + margin * 2f;
            cam.orthographicSize = worldH * 0.5f;
            cam.aspect = worldW / worldH;
            camTransform.position = new Vector3(world.center.x, world.center.y, world.center.z - 5f);
            camTransform.rotation = Quaternion.identity;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            int w = Mathf.CeilToInt(TargetPixelsWide), h = Mathf.Max(64, Mathf.CeilToInt(TargetPixelsWide * worldH / worldW));
            var target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = target;
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            string path = Path.Combine(dir, fileName + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[ControlPanelScreenshot] {fileName} world={world.size.x:0.####}x{world.size.y:0.####} -> {path}");
            cam.targetTexture = null;
            RenderTexture.active = original;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
