// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The scan camera: its permission state machine, the camera provider the scene may lack, and what the panel shows while there is
    /// no image. And the capture surfaces' look: tokens only, hit targets, the mask colour.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class CaptureCameraAndStyleTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly string[] Pair = { "android.permission.CAMERA", "horizonos.permission.HEADSET_CAMERA" };

        private sealed class FakeBackend : ICameraPermissionBackend
        {
            public readonly HashSet<string> Granted = new HashSet<string>();
            public readonly List<string[]> Requests = new List<string[]>();
            public Action Finish;
            public bool OtherPending { get; set; }
            public bool OtherRequestPending => OtherPending;
            public bool IsGranted(string permission) => Granted.Contains(permission);
            public void Request(string[] permissions, Action finished) { Requests.Add(permissions); Finish = finished; }
        }

        // ---- permission state machine -----------------------------------------------------------------------------

        [Test]
        public void Gate_AlreadyGrantedAsksForNothing()
        {
            var backend = new FakeBackend();
            backend.Granted.UnionWith(Pair);
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Granted));
            Assert.That(backend.Requests, Is.Empty);
        }

        [Test]
        public void Gate_AsksOnlyForWhatIsMissing_OnceWhileThePromptIsUp_ThenGrants()
        {
            var backend = new FakeBackend();
            backend.Granted.Add(Pair[0]);
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick(); gate.Tick(); gate.Tick();
            Assert.That(backend.Requests.Count, Is.EqualTo(1), "one prompt, however often it is polled");
            Assert.That(backend.Requests[0], Is.EqualTo(new[] { Pair[1] }));
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Requesting));
            backend.Granted.Add(Pair[1]);
            backend.Finish();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Granted));
        }

        [Test]
        public void Gate_ReadsTheGrantNotTheCallback_SoAnotherRequestersPromptCounts()
        {
            // v1 bug shape: SbPermissionManager's prompt was the one the user answered; the camera's own callback never ran.
            var backend = new FakeBackend { OtherPending = true };
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick();
            Assert.That(backend.Requests, Is.Empty, "a second request would replace the first one's callbacks");
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Requesting));
            backend.Granted.UnionWith(Pair);
            backend.OtherPending = false;
            gate.Tick();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Granted));
            Assert.That(backend.Requests, Is.Empty);
        }

        [Test]
        public void Gate_AnotherRequesterEndedWithoutTheGrant_IsADenial_AndRetryAsksAgain()
        {
            var backend = new FakeBackend { OtherPending = true };
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick();
            backend.OtherPending = false;
            gate.Tick();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Denied));
            gate.Tick();
            Assert.That(backend.Requests, Is.Empty, "no nagging after a no");
            gate.Retry();
            Assert.That(backend.Requests.Count, Is.EqualTo(1));
            Assert.That(backend.Requests[0], Is.EqualTo(Pair));
            backend.Granted.UnionWith(Pair);
            backend.Finish();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Granted));
        }

        [Test]
        public void Gate_ADenialIsNotFinal_AGrantInTheSystemSettingsIsSeen()
        {
            var backend = new FakeBackend();
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick();
            backend.Finish();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Denied));
            backend.Granted.UnionWith(Pair);
            gate.Tick();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Granted));
        }

        [Test]
        public void Gate_ARevokedPermissionIsAskedForAgain()
        {
            var backend = new FakeBackend();
            backend.Granted.UnionWith(Pair);
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick();
            backend.Granted.Remove(Pair[1]);
            gate.Tick();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Requesting));
            Assert.That(backend.Requests.Single(), Is.EqualTo(new[] { Pair[1] }));
        }

        [Test]
        public void Gate_APromptWhoseCallbackNeverComesTimesOut_AndALateCallbackIsIgnored()
        {
            var backend = new FakeBackend();
            var gate = new CameraPermissionGate(backend, Pair);
            gate.Tick(0.0);
            var stale = backend.Finish;
            gate.Tick(CameraPermissionGate.RequestTimeoutSeconds + 1.0);
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Denied));
            gate.Retry(100.0);
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Requesting));
            stale();
            Assert.That(gate.State, Is.EqualTo(CameraPermissionState.Requesting), "the old prompt's callback must not end the new request");
        }

        [Test]
        public void CameraPermissions_AreTheTwoNamesTheHorizonCameraNeeds_AndTheManifestDeclaresBoth()
        {
            Assert.That(PassthroughCameraPermissions.CameraPermissions, Is.EqualTo(Pair));
            string manifest = File.ReadAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets/Plugins/Android/AndroidManifest.xml"));
            foreach (string name in Pair) Assert.That(manifest, Does.Contain("<uses-permission android:name=\"" + name + "\" />"), name);
            Assert.That(manifest, Does.Contain("com.oculus.feature.PASSTHROUGH"));
        }

        // ---- the camera provider ----------------------------------------------------------------------------------

        [Test]
        public void EnsureInstance_MakesTheCameraProviderTheSceneLacks_AndReusesIt()
        {
            Assert.That(UnityEngine.Object.FindAnyObjectByType<WebCamTextureManager>(FindObjectsInactive.Include), Is.Null, "the test scene has none, like the shipped scene");
            WebCamTextureManager made = null;
            try
            {
                made = WebCamTextureManager.EnsureInstance();
                Assert.That(made, Is.Not.Null);
                Assert.That(made.GetComponent<PassthroughCameraPermissions>(), Is.Not.Null);
                Assert.That(WebCamTextureManager.EnsureInstance(), Is.SameAs(made));
            }
            finally
            {
                if (made != null) UnityEngine.Object.DestroyImmediate(made.gameObject);
            }
        }

        // ---- messages ---------------------------------------------------------------------------------------------

        [Test]
        public void EveryCameraStatusHasWords_AndOnlyLiveIsSilent()
        {
            foreach (ScanCameraStatus status in Enum.GetValues(typeof(ScanCameraStatus)))
            {
                var message = ScanPanelStyle.Describe(status);
                if (status == ScanCameraStatus.Live) { Assert.That(message.Show, Is.False); continue; }
                Assert.That(message.Show, Is.True, status.ToString());
                Assert.That(message.Title, Is.Not.Empty, status.ToString());
                Assert.That(message.OffersAllow, Is.EqualTo(status == ScanCameraStatus.PermissionDenied), status.ToString());
            }
        }

        [Test]
        public void Hints_FollowThePhase()
        {
            Assert.That(ScanPanelStyle.HintFor(false, 0), Is.EqualTo(ScanPanelStyle.HintLive));
            Assert.That(ScanPanelStyle.HintFor(true, 0), Is.EqualTo(ScanPanelStyle.HintTap));
            Assert.That(ScanPanelStyle.HintFor(true, 2), Is.EqualTo(ScanPanelStyle.HintTapMore));
            Assert.That(ScanPanelStyle.PointsText(1), Is.EqualTo("1 point"));
            Assert.That(ScanPanelStyle.PointsText(3), Is.EqualTo("3 points"));
        }

        // ---- look -------------------------------------------------------------------------------------------------

        private static float HueDistance(Color a, Color b)
        {
            Color.RGBToHSV(a, out float ha, out _, out _);
            Color.RGBToHSV(b, out float hb, out _, out _);
            float d = Mathf.Abs(ha - hb);
            return Mathf.Min(d, 1f - d);
        }

        [Test]
        public void MaskAndDots_AreTheThemeAccent_NotTheRealAnchorOrange()
        {
            var p = UiTheme.Palette;
            var mask = ScanPanelStyle.MaskColor;
            Assert.That(new Color(mask.r, mask.g, mask.b), Is.EqualTo(new Color(p.Accent.r, p.Accent.g, p.Accent.b)));
            Assert.That(mask.a, Is.InRange(0.3f, 0.6f), "see the object through it");
            Assert.That(HueDistance(mask, p.RealAnchor), Is.GreaterThan(0.2f), "distinct from RealAnchor orange");
            Assert.That(ScanPanelStyle.DotFill, Is.EqualTo(p.Accent));
            Assert.That(UiThemeTests.Contrast(ScanPanelStyle.DotFill, ScanPanelStyle.DotRing), Is.GreaterThan(4.5), "the dot's ring reads against its fill");
        }

        [Test]
        public void HitTarget_IsTheThemeRowInPhysicalSize_WhateverTheCanvasScale()
        {
            Assert.That(ScanPanelStyle.MinHitUnits(UiTheme.MetresPerUnit), Is.EqualTo(UiTheme.RowHeight).Within(0.01f));
            float scale = 0.0012f;
            Assert.That(ScanPanelStyle.MinHitUnits(scale) * scale, Is.EqualTo(UiTheme.RowHeight * UiTheme.MetresPerUnit).Within(1e-6f));
        }

        [Test]
        public void RoomText_MeetsTheAngularFloorAtTheRoomDistance()
        {
            float scale = 0.0012f;
            foreach (float mm in new[] { UiMetrics.Room.Millimetres(UiTheme.FontCaption), UiMetrics.Room.Millimetres(UiTheme.FontBody), UiMetrics.Room.Millimetres(UiTheme.FontTitle) })
            {
                float units = ScanPanelStyle.FontUnits(mm, scale);
                Assert.That(UiTheme.AngularSizeDegrees(units * scale / UiTheme.MetresPerUnit, UiTheme.RoomWorstMetres), Is.GreaterThanOrEqualTo(UiTheme.MinEmDegrees - 0.01f));
            }
        }

        // ---- the panel --------------------------------------------------------------------------------------------

        private static GameObject NewPanel(out PassthroughPanelManager panel)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Scanning/Prefabs/PassthroughPanel.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            panel = instance.GetComponent<PassthroughPanelManager>();
            typeof(PassthroughPanelManager).GetMethod("Awake", Any).Invoke(panel, null); // Awake does not run outside play mode
            return instance;
        }

        private static void Layout(GameObject root)
        {
            foreach (var group in root.GetComponentsInChildren<LayoutGroup>(true))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
            Canvas.ForceUpdateCanvases();
        }

        private static string LabelOf(GameObject root, string path) => root.transform.Find("Canvas/" + path).GetComponentInChildren<TMPro.TMP_Text>(true).text;

        [Test]
        public void Panel_WithoutACamera_SaysSoInsteadOfShowingNothing_AndCaptureExplainsItself()
        {
            var root = NewPanel(out var panel);
            try
            {
                panel.OpenPanel();
                Assert.That(panel.CameraMessage.Show, Is.True);
                Assert.That(panel.CameraMessage.Title, Is.Not.Empty);
                var overlays = (ScanPanelOverlays)typeof(PassthroughPanelManager).GetField("_overlays", Any).GetValue(panel);
                Assert.That(overlays.MessageVisible, Is.True);
                panel.CaptureImage();
                Assert.That(LabelOf(root, "Buttons/Capture"), Is.EqualTo(ScanPanelStyle.CaptureLabel), "an unready Capture must not flip to 'Start New Capture'");
                Assert.That(overlays.ToastVisible, Is.True, "'Capture not ready'");
                Assert.That(overlays.HintText, Is.Empty, "the camera message already says what to do");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Panel_WithAStill_ShowsNoCameraMessage_AndCaptureFlowKeepsTheLabelsAndHints()
        {
            var root = NewPanel(out var panel);
            var photo = new Texture2D(64, 48, TextureFormat.RGB24, false);
            try
            {
                panel.StillOverride = photo;
                panel.OpenPanel();
                Assert.That(panel.CameraMessage.Show, Is.False);
                panel.CaptureImage();
                Assert.That(LabelOf(root, "Buttons/Capture"), Is.EqualTo(ScanPanelStyle.NewCaptureLabel));
                Assert.That(LabelOf(root, "Buttons/Send Server"), Is.EqualTo(ScanPanelStyle.ReconstructLabel), "one name for the action, whatever the server");
                var overlays = (ScanPanelOverlays)typeof(PassthroughPanelManager).GetField("_overlays", Any).GetValue(panel);
                Assert.That(overlays.HintText, Is.EqualTo(ScanPanelStyle.HintTap));
                typeof(PassthroughPanelManager).GetMethod("AddPickedPoint", Any).Invoke(panel, new object[] { new Vector2(10, 10), new Vector2(0.2f, 0.2f) });
                Assert.That(overlays.HintText, Is.EqualTo(ScanPanelStyle.HintTapMore));
                Assert.That(root.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(t => t.text == "1 point"), Is.True);
                panel.CaptureImage();
                Assert.That(LabelOf(root, "Buttons/Capture"), Is.EqualTo(ScanPanelStyle.CaptureLabel));
                Assert.That(overlays.HintText, Is.EqualTo(ScanPanelStyle.HintLive));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(photo); }
        }

        [Test]
        public void Panel_MaskOverlayUsesTheAccent_AndEveryButtonIsAtLeastTheThemeRow()
        {
            var root = NewPanel(out var panel);
            try
            {
                panel.OpenPanel();
                var mask = new byte[16];
                for (int i = 0; i < mask.Length; i++) mask[i] = 255;
                typeof(PassthroughPanelManager).GetMethod("UpdateMaskOverlay", Any).Invoke(panel, new object[] { mask, 4, 4 });
                var overlay = (RawImage)typeof(PassthroughPanelManager).GetField("m_maskOverlayImage", Any).GetValue(panel);
                var tex = (Texture2D)overlay.texture;
                Assert.That((Color32)tex.GetPixel(1, 1), Is.EqualTo(ScanPanelStyle.MaskColor32));

                Layout(root);
                float min = UiTheme.RowHeight * UiTheme.MetresPerUnit - 0.0005f;
                foreach (var button in root.GetComponentsInChildren<Selectable>(false))
                {
                    var rect = (RectTransform)button.transform;
                    if (rect.rect.height <= 0f) continue; // a hidden layout group member
                    float metres = rect.rect.height * rect.lossyScale.y;
                    Assert.That(metres, Is.GreaterThanOrEqualTo(min), button.name + " is " + metres * 100f + " cm tall");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Panel_ButtonLabelsFitInsideTheirButtons_InEveryState()
        {
            var root = NewPanel(out var panel);
            try
            {
                panel.OpenPanel();
                Layout(root);
                var labelsOf = new System.Collections.Generic.Dictionary<string, string[]>
                {
                    ["Capture"] = new[] { ScanPanelStyle.CaptureLabel, ScanPanelStyle.NewCaptureLabel },
                    ["Send Server"] = new[] { ScanPanelStyle.ReconstructLabel },
                };
                foreach (var button in root.GetComponentsInChildren<Button>(false))
                {
                    var text = button.GetComponentInChildren<TMPro.TMP_Text>(false);
                    if (text == null || string.IsNullOrEmpty(text.text)) continue;
                    var labels = labelsOf.TryGetValue(button.name, out var known) ? known : new[] { text.text };
                    float room = ((RectTransform)button.transform).rect.width;
                    foreach (string label in labels)
                    {
                        float need = text.GetPreferredValues(label).x;
                        Assert.That(need, Is.LessThanOrEqualTo(room), $"'{label}' needs {need:0} units, button '{button.name}' is {room:0}");
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Panel_TapMarkersAreTheThemeAccentWithAnOnAccentRing_NotRed()
        {
            var root = NewPanel(out var panel);
            var photo = new Texture2D(64, 48, TextureFormat.RGB24, false);
            try
            {
                panel.StillOverride = photo;
                panel.OpenPanel();
                panel.CaptureImage();
                typeof(PassthroughPanelManager).GetMethod("AddPickedPoint", Any).Invoke(panel, new object[] { new Vector2(10, 10), new Vector2(0.2f, 0.2f) });
                var dot = root.GetComponentsInChildren<Image>(true).First(i => i.name == "PickedDot");
                Assert.That(dot.color, Is.EqualTo(ScanPanelStyle.DotRing));
                var core = dot.transform.Find("Core").GetComponent<Image>();
                Assert.That(core.color, Is.EqualTo(ScanPanelStyle.DotFill));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(photo); }
        }

        [Test]
        public void Panel_DestroyedWithoutClose_StopsTheCamera()
        {
            var root = NewPanel(out var panel);
            WebCamTextureManager manager = null;
            try
            {
                manager = WebCamTextureManager.EnsureInstance();
                typeof(PassthroughPanelManager).GetField("m_webCamTextureManager", Any).SetValue(panel, manager);
                panel.OpenPanel();
                Assert.That(manager.CaptureWanted, Is.True, "opening the panel asks for the camera");
                // The box was deleted: the context destroys the panel and ClosePanel never runs. OnDestroy does not run for an
                // edit-mode object, so it is invoked the way the test already invokes Awake.
                typeof(PassthroughPanelManager).GetMethod("OnDestroy", Any).Invoke(panel, null);
                Assert.That(manager.CaptureWanted, Is.False, "the cameras must not stay on with no panel");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (manager != null) UnityEngine.Object.DestroyImmediate(manager.gameObject);
            }
        }

        [Test]
        public void Panel_AccentButtonsCarryOnAccentText()
        {
            var root = NewPanel(out _);
            try
            {
                var p = UiTheme.Palette;
                foreach (string name in new[] { "Buttons/Capture", "Buttons/Send Server" })
                {
                    var text = root.transform.Find("Canvas/" + name).GetComponentInChildren<TMPro.TMP_Text>(true);
                    Assert.That(text.color, Is.EqualTo(p.OnAccent), name);
                    Assert.That(UiThemeTests.Contrast(text.color, p.Accent), Is.GreaterThanOrEqualTo(4.5), name);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Panel_RuntimeUi_WritesNoColourOutsideTheTheme()
        {
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets/SpatialBlend/Samples/Scanning/Scripts");
            foreach (string file in new[] { "PassthroughPanelManager.cs", "ScanPanelOverlays.cs", "ScanPanelStyle.cs" })
            {
                string text = File.ReadAllText(Path.Combine(dir, file));
                Assert.That(text, Does.Not.Contain("Color.red"), file);
                Assert.That(text, Does.Not.Contain("Color.green"), file);
                Assert.That(text, Does.Not.Contain("new Color32(0, 200"), file + ": the old green mask");
            }
        }
    }
}
#endif
