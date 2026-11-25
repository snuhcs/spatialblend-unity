// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.IO;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Batchmode renders of the lobby tutorial: the card on its panel canvas for every frame (with the state the user would be
    /// in), the lobby's Tutorial button with its hint dot, the practice props in the room, and the scan panel showing the sample
    /// photo. Run with <c>-executeMethod SpatialBlend.Samples.FreeformCollaboration.UI.Editor.TutorialScreenshot.Capture -shotDir DIR</c>;
    /// this needs a graphics device (no -nographics), like <see cref="ControlPanelScreenshot"/>, whose helpers it shares.
    /// </summary>
    public static class TutorialScreenshot
    {
        private sealed class Clock : ITutorialClock { public double Now { get; set; } }

        private sealed class ShotHost : ITutorialHost
        {
            public bool SceneEdit { get; set; }
            public bool AnchorsVisible { get; set; }
            public float Brightness { get; set; }
            public bool HandTrackingActive { get; set; }
            public bool RoomAnchorsAvailable { get; set; } = true;
            public void SetLobbyHidden(bool hidden) { }
            public void FrameEntered(TutorialFrame frame) { }
            public void FrameLeft(TutorialFrame frame) { }
            public void StepStarted(TutorialFrame frame, int stepIndex) { }
            public void StepCompleted(TutorialFrame frame, int stepIndex) { }
            public double PlayVoice(string clipName) => 9.0;
            public void StopVoice() { }
            public void PauseVoice(bool paused) { }
            public void Ended(TutorialEnd end) { }
        }

        private sealed class MemoryStore : ITutorialKeyValueStore
        {
            private readonly Dictionary<string, int> _values = new Dictionary<string, int>();
            public int GetInt(string key, int fallback) => _values.TryGetValue(key, out int v) ? v : fallback;
            public void SetInt(string key, int value) => _values[key] = value;
            public void Save() { }
        }

        public static void Capture()
        {
            string dir = ControlPanelScreenshot.Arg("-shotDir") ?? System.Environment.GetEnvironmentVariable("TUTORIAL_SHOT_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "tutorial-shots");
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
                Debug.LogError("[TutorialScreenshot] " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void Settle(RectTransform rect)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            Canvas.ForceUpdateCanvases();
        }

        private static Bounds Union(RectTransform a, RectTransform b)
        {
            var corners = new Vector3[4];
            a.GetWorldCorners(corners);
            var bounds = new Bounds(corners[0], Vector3.zero);
            foreach (var c in corners) bounds.Encapsulate(c);
            b.GetWorldCorners(corners);
            foreach (var c in corners) bounds.Encapsulate(c);
            return bounds;
        }

        private static void Run(string dir)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = ControlPanelScreenshot.NewCamera();
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();

            // The lobby with the Tutorial button and its hint dot.
            var rect = ControlPanelScreenshot.NewHostCanvas("ControlPanelCanvas");
            rect.sizeDelta = new Vector2(UiTheme.PanelWidth, 0f);
            var avatars = new AvatarSettingsPresenter(() => settings, () => { });
            var scene = new SceneControlsPresenter(null, null, null, null);
            var view = ControlPanelView.Build(rect, UiTheme.PanelWidth, new ControlPanelActions { Tutorial = () => { } }, avatars, scene);
            view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LinkState = SpatialBlendConnectionState.Disconnected, LibraryCompatible = true }));
            view.SetTutorialHint(true);
            Settle(rect);
            ControlPanelScreenshot.Shoot(cam, dir, "lobby-tutorial-button-hint-dot", ControlPanelScreenshot.RectBounds(rect));
            view.SetTutorialHint(false);
            ControlPanelScreenshot.Shoot(cam, dir, "lobby-tutorial-button-done", ControlPanelScreenshot.RectBounds(rect));

            // The card, task by task, over that canvas: the task as it starts (its target is up), each step done, and the success.
            var card = TutorialView.Create(rect);
            var host = new ShotHost();
            var clock = new Clock();
            var runner = new TutorialRunner(host, clock, new TutorialDoneMark(new MemoryStore()));
            card.Bind(runner, new TutorialCardActions());
            runner.Begin();
            foreach (var frame in TutorialScript.Frames)
            {
                while (runner.Flow.Current.Id != frame.Id) runner.Skip();
                string id = frame.Id;
                if (id == "2.1") host.AnchorsVisible = false;
                clock.Now += 2.0;    // part-way through the line: the subtitle shows
                runner.Tick();
                card.Refresh();
                Shoot(cam, dir, "task-" + id + "-a-start", rect, card);

                for (int step = 0; step < frame.Steps.Length; step++)
                {
                    if (frame.Steps[step].RequiresEditing) host.SceneEdit = true;   // the user switched to Editing with the chip
                    if (id == "2.2") host.SceneEdit = frame.Steps[step].Signal == TutorialSignal.ModeEditing;   // Edit pressed, then back with the chip
                    runner.Report(frame.Steps[step].Signal);
                    if (id == "2.1") { card.AnchorsRow.Set(true); host.AnchorsVisible = true; }
                    if (id == "3.1" && step == 3) runner.NoteScanInFlight(true);   // the request went out: "Building your 3D copy..."
                    if (id == "3.1" && step == 4) runner.NoteScanInFlight(false);
                    if (id == "4.1" && step == 0) runner.NoteRoomList(true, true);
                    if (id == "4.2") { runner.ChooseRoom("Room 2", false); }
                    clock.Now += 0.4;
                    runner.Tick();
                    card.Refresh();
                    bool last = step == frame.Steps.Length - 1;
                    Shoot(cam, dir, "task-" + id + "-" + (char)('b' + step) + (last ? "-done" : "-step" + (step + 1) + "-ticked"), rect, card);
                }
                if (id == "2.2")
                {
                    // Half way: in Editing, the explanation under the task and the "Tap the mode chip" step.
                    runner.Back(); runner.Skip();
                    host.SceneEdit = true;
                    runner.Report(TutorialSignal.ModeEditing);
                    clock.Now += 0.4; runner.Tick();
                    card.Refresh();
                    Shoot(cam, dir, "task-2.2-in-editing-explanation", rect, card);
                    host.SceneEdit = false;
                }
                if (id == "3.1")
                {
                    // The task as it starts when the user is still in Interaction: "Switch to Editing first".
                    runner.Back(); runner.Skip();
                    host.SceneEdit = false;
                    clock.Now += 0.4; runner.Tick();
                    card.Refresh();
                    Shoot(cam, dir, "task-3.1-switch-to-editing-first", rect, card);
                    host.SceneEdit = true;
                    foreach (var step in frame.Steps) runner.Report(step.Signal);
                    // Back to the capture step to show the checklist half way, with the camera notice for hand tracking.
                    host.HandTrackingActive = true;
                    card.Refresh();
                    Shoot(cam, dir, "task-3.1-hands-notice", rect, card);
                    host.HandTrackingActive = false;
                    runner.Back(); runner.Skip();
                    foreach (var step in frame.Steps.Take(4)) runner.Report(step.Signal);
                    runner.NoteScanInFlight(true);
                    runner.ReportScanFailure(ScanFailureText.ToUserMessage(new Freeform.Scanning.ScanError(Freeform.Scanning.ScanErrorCode.ConnectFailed, "x")));
                    card.Refresh();
                    Shoot(cam, dir, "task-3.1-scan-failed-try-again", rect, card);
                    runner.ClearScanProblem();
                }
                if (id == "2.1")
                {
                    runner.Back(); runner.Skip();     // back in, anchors forced off again
                    host.AnchorsVisible = false;
                    host.RoomAnchorsAvailable = false;
                    card.Refresh();
                    Shoot(cam, dir, "task-2.1-no-room-setup", rect, card);
                    host.RoomAnchorsAvailable = true;
                }
                clock.Now += 6.0; runner.Tick();   // the dwell is over: the task would move on by itself
            }
            runner.Abort();
            // The exit confirmation.
            var runner2 = new TutorialRunner(host, clock, new TutorialDoneMark(new MemoryStore()));
            card.Bind(runner2, new TutorialCardActions());
            runner2.Begin();
            runner2.RequestExit();
            card.Refresh();
            Shoot(cam, dir, "frame-exit-confirm", rect, card);
            runner2.Abort();
            Object.DestroyImmediate(rect.gameObject);

            ShootRoom(dir, cam);
            ShootScanPanel(dir, cam);
            Object.DestroyImmediate(settings);
        }

        private static void Shoot(Camera cam, string dir, string name, RectTransform canvas, TutorialView card)
        {
            Settle(canvas);
            ControlPanelScreenshot.Shoot(cam, dir, name, Union(canvas, card.Root));
        }

        private static Camera NewRoomCamera()
        {
            var camGo = new GameObject("RoomCam", typeof(Camera));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.transform.position = new Vector3(0f, 1.55f, 0f);
            cam.transform.rotation = Quaternion.identity;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.50f, 0.52f, 0.55f, 1f);
            cam.nearClipPlane = 0.05f;
            cam.enabled = false;
            return cam;
        }

        // The outlines pulse in LateUpdate, which does not run here: run one pulse by hand so they are drawn as in the headset.
        private static void Pulse()
        {
            foreach (var outline in Object.FindObjectsByType<TutorialOutline>(FindObjectsSortMode.None))
                typeof(TutorialOutline).GetMethod("LateUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(outline, null);
        }

        private static void Shot(Camera cam, string dir, string name)
        {
            Pulse();
            RenderPerspective(cam, Path.Combine(dir, name + ".png"));
        }

        private static GameObject Floor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(0f, 0f, 0f);
            floor.transform.localScale = new Vector3(1.2f, 1f, 1.2f);
            TutorialPracticeProps.Tint(floor, new Color(0.30f, 0.32f, 0.36f));
            return floor;
        }

        /// <summary>
        /// The task props in the room, seen from the user's head: for each task, the target as it appears (object, pad and outline) and
        /// the success state (the object on the pad, the targets hidden). Plain bodies stand in for the sample model.
        /// </summary>
        private static void ShootRoom(string dir, Camera orthographic)
        {
            var cam = NewRoomCamera();
            var floor = Floor();
            var props = new TutorialPracticeProps();
            var eye = cam.transform;

            // 2.2 The cube to point at: the mode task's prop is the practice cube with its outline (the assist's Edit prompt floats above it in the headset).
            var modeCube = props.EnsureCube(eye);
            props.Highlight(modeCube.gameObject);
            Shot(cam, dir, "room-2.2-a-start-cube-to-point-at");
            props.DestroyAll();

            // 3.1 The scanned copy appears and stays (left); the virtual object, a different object that is not in the user's room, arrives
            // beside it in the add-as-new box, to be placed anywhere in the room. Plain bodies stand in for both models here.
            var copy = props.EnsureCopy(TutorialPracticeProps.SpotAhead(eye, 1.0f, 0.35f, -0.5f));
            TutorialPracticeProps.AddPlaceholderBody(copy.gameObject, new Vector3(0.3f, 0.3f, 0.3f));
            Shot(cam, dir, "room-3.1-a-scanned-copy-in-the-room");
            var virt = props.EnsureShared(TutorialPracticeProps.SpotAhead(eye, 1.0f, 0.35f, 0.05f));
            TutorialPracticeProps.AddPlaceholderBody(virt.gameObject, TutorialPracticeProps.SharedSize);
            props.AddRealAnchorBox(virt.gameObject, TutorialPracticeProps.SharedSize);
            Shot(cam, dir, "room-3.1-b-virtual-object-arrives-in-add-as-new-box");
            virt.transform.position += new Vector3(0.5f, 0f, 0.3f);
            props.ClearTargets();
            Shot(cam, dir, "room-3.1-c-placed-box-gone-scanned-copy-stays");
            props.DestroyAll();

            Object.DestroyImmediate(floor);
            ShootAnchors(dir, cam);
            Object.DestroyImmediate(cam.gameObject);
        }

        /// <summary>The real-object anchor outline before (the package's cyan) and after (the RealAnchor token), next to a virtual object and a box annotation.</summary>
        private static void ShootAnchors(string dir, Camera cam)
        {
            var floor = Floor();
            var material = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.meta.xr.mrutilitykit/Core/Materials/RoomBoxEffects.mat");
            if (material == null) { Debug.LogWarning("[TutorialScreenshot] the MRUK effect-mesh material is missing"); Object.DestroyImmediate(floor); return; }
            var themed = SceneAnchorStyle.Themed(material);
            GameObject Furniture(string name, Vector3 position, Vector3 size, Material m)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.position = position;
                go.transform.localScale = size;
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.GetComponent<Renderer>().sharedMaterial = m;
                return go;
            }
            var props = new TutorialPracticeProps();
            foreach (bool after in new[] { false, true })
            {
                var m = after ? themed : material;
                var table = Furniture("Table", new Vector3(-0.5f, 0.35f, 1.8f), new Vector3(1.0f, 0.7f, 0.6f), m);
                var shelf = Furniture("Shelf", new Vector3(1.0f, 0.8f, 2.3f), new Vector3(0.5f, 1.6f, 0.35f), m);
                // A virtual object (the accent) and a box annotation (the annotation green) for comparison.
                var cube = props.EnsureCube(cam.transform);
                cube.transform.position = new Vector3(0.1f, 1.0f, 1.2f);
                props.Highlight(cube.gameObject);
                var box = new GameObject("BoxAnnotationStandIn");
                AddWireBox(box.transform, new Vector3(0.9f, 0.5f, 1.4f), new Vector3(0.35f, 0.25f, 0.3f), new Color(0f, 1f, 0.3f, 1f));
                Shot(cam, dir, "anchors-" + (after ? "after-real-anchor-token" : "before-package-cyan"));
                Object.DestroyImmediate(table); Object.DestroyImmediate(shelf); Object.DestroyImmediate(box);
                props.DestroyAll();
            }
            Object.DestroyImmediate(themed);
            Object.DestroyImmediate(floor);
        }

        private static void AddWireBox(Transform parent, Vector3 centre, Vector3 size, Color color)
        {
            var go = new GameObject("Wire");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.widthMultiplier = 0.01f;
            line.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
            var h = size * 0.5f;
            Vector3 P(int x, int y, int z) => centre + new Vector3(x * h.x, y * h.y, z * h.z);
            var path = new[] { P(-1,-1,-1), P(1,-1,-1), P(1,-1,1), P(-1,-1,1), P(-1,-1,-1), P(-1,1,-1), P(1,1,-1), P(1,-1,-1), P(1,1,-1), P(1,1,1), P(1,-1,1), P(1,1,1), P(-1,1,1), P(-1,-1,1), P(-1,1,1), P(-1,1,-1) };
            line.positionCount = path.Length;
            line.SetPositions(path);
        }

        /// <summary>The scan panel prefab with the sample photo as its view, before and after Capture.</summary>
        private static void ShootScanPanel(string dir, Camera cam)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Scanning/Prefabs/PassthroughPanel.prefab");
            if (prefab == null) { Debug.LogWarning("[TutorialScreenshot] no scan panel prefab"); return; }
            var photo = TutorialAssets.LoadSamplePhoto();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true)) canvas.worldCamera = cam;
            var panel = instance.GetComponent<PassthroughPanelManager>();
            // Awake does not run for an instance made outside play mode; it wires the buttons and the image.
            typeof(PassthroughPanelManager).GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(panel, null);
            panel.StillOverride = photo;
            panel.OpenPanel();
            LayoutAll(instance);
            ControlPanelScreenshot.Shoot(cam, dir, "scan-panel-sample-photo-live", DrawnBounds(instance));
            panel.CaptureImage();
            LayoutAll(instance);
            ControlPanelScreenshot.Shoot(cam, dir, "scan-panel-sample-photo-captured", DrawnBounds(instance));
            Object.DestroyImmediate(instance);
            if (photo != null) Object.DestroyImmediate(photo);
            ShootScanMask(dir, cam);
        }

        /// <summary>
        /// The canned scan's mask: a tap on the vase in the sample photo, answered by the canned "server" and drawn by the panel's own overlay
        /// (the same path as a real Segment answer); then a tap off the vase, which selects nothing.
        /// </summary>
        private static void ShootScanMask(string dir, Camera cam)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Scanning/Prefabs/PassthroughPanel.prefab");
            if (prefab == null) return;
            var photo = TutorialAssets.LoadSamplePhoto();
            var manager = SessionManager.EnsureInstance();
            // Awake publishes SessionManager.Instance, which the panel reads; it does not run for a component added outside play mode.
            typeof(SessionManager).GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(manager, null);
            var clock = new Clock();
            var backend = new TutorialScanBackend(clock, () => new byte[] { 1 }, Path.GetTempPath(), CannedSegmenter.LoadSample);
            backend.Prepare(manager);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true)) canvas.worldCamera = cam;
            var panel = instance.GetComponent<PassthroughPanelManager>();
            var panelType = typeof(PassthroughPanelManager);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            panelType.GetMethod("Awake", flags).Invoke(panel, null);
            panel.StillOverride = photo;
            panel.OpenPanel();
            panel.CaptureImage();
            void Tap(double x, double y)
            {
                // The panel's own point handling, at a point given as the server sees it (y down).
                var pixel = new Vector2((float)x, photo.height - 1f - (float)y);
                panelType.GetMethod("AddPickedPoint", flags).Invoke(panel, new object[] { pixel, new Vector2(pixel.x / photo.width, pixel.y / photo.height) });
                clock.Now += 1.0;
                backend.Tick();
                manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                LayoutAll(instance);
            }
            Tap(100, 420);        // the floor: nothing selected
            ControlPanelScreenshot.Shoot(cam, dir, "scan-panel-tap-off-the-vase-no-mask", DrawnBounds(instance));
            Tap(300, 200);        // the vase: the mask appears
            ControlPanelScreenshot.Shoot(cam, dir, "scan-panel-tap-on-the-vase-mask", DrawnBounds(instance));
            Object.DestroyImmediate(instance);
            backend.Release();
            if (manager != null) Object.DestroyImmediate(manager.gameObject);
            if (photo != null) Object.DestroyImmediate(photo);
        }

        private static void LayoutAll(GameObject root)
        {
            foreach (var group in root.GetComponentsInChildren<LayoutGroup>(true))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
            Canvas.ForceUpdateCanvases();
        }

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

        private static void RenderPerspective(Camera cam, string path)
        {
            var previous = RenderTexture.active;
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = target;
            cam.Render();
            RenderTexture.active = target;
            var tex = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[TutorialScreenshot] " + path);
            cam.targetTexture = null;
            RenderTexture.active = previous;
            Object.DestroyImmediate(tex);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }
}
#endif
