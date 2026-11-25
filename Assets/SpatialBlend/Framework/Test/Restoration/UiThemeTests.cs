// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI.Editor;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>The token source itself: contrast, angular size, hit targets, the font and its licence record, the generated sprites.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class UiThemeTests
    {
        private static double Linear(float c) => c <= 0.03928f ? c / 12.92 : System.Math.Pow((c + 0.055) / 1.055, 2.4);
        private static double Luminance(Color c) => 0.2126 * Linear(c.r) + 0.7152 * Linear(c.g) + 0.0722 * Linear(c.b);

        /// <summary>WCAG 2.x contrast ratio of two opaque colours.</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            if (la < lb) (la, lb) = (lb, la);
            return (la + 0.05) / (lb + 0.05);
        }

        [Test]
        public void TextMeetsWcagAaOnEverySurfaceItIsDrawnOn()
        {
            var p = UiTheme.Palette;
            foreach (var surface in new[] { p.Surface, p.SurfaceHover, p.SurfacePressed, p.PanelBottom, p.PanelTop })
            {
                var opaque = new Color(surface.r, surface.g, surface.b, 1f);
                Assert.That(Contrast(p.Text, opaque), Is.GreaterThanOrEqualTo(4.5), "text on " + surface);
                if (surface != p.SurfacePressed)   // muted text is not drawn on a pressed control
                    Assert.That(Contrast(p.TextMuted, opaque), Is.GreaterThanOrEqualTo(4.5), "muted text on " + surface);
            }
            foreach (var accent in new[] { p.Accent, p.AccentHover, p.AccentPressed })
                Assert.That(Contrast(p.OnAccent, accent), Is.GreaterThanOrEqualTo(4.5), "text on accent " + accent);
            foreach (var tone in new[] { p.Success, p.Warning, p.Error, p.Accent })
            {
                Assert.That(Contrast(tone, p.Surface), Is.GreaterThanOrEqualTo(4.5), "tone on a card " + tone);
                Assert.That(Contrast(tone, new Color(p.PanelBottom.r, p.PanelBottom.g, p.PanelBottom.b, 1f)), Is.GreaterThanOrEqualTo(4.5));
            }
        }

        [Test]
        public void RealAnchorIsItsOwnHue_NotTheAccentTheWarningOrTheBoxGreen_AndShowsOverPassthrough()
        {
            var p = UiTheme.Palette;
            float Hue(Color c) { Color.RGBToHSV(c, out float h, out _, out _); return h * 360f; }
            float Gap(float a, float b) { float d = Mathf.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }
            // Every colour the annotation graphics can draw (edges, corner brackets, faces, in every hover / selected / active
            // state) is checked against RealAnchor: the old edit-mode orange (1, 0.5, 0), yellow and the primaries are gone, and
            // what remains is the graphite accent family. Near-white colours have no hue and are told apart by saturation.
            var look = SpatialBlend.Interactions.Annotation.AnnotationLook.Graphite();
            var annotationColours = new System.Collections.Generic.List<Color> { look.Edge, look.EdgeHover, look.FaceFill, look.Core };
            foreach (SpatialBlend.Interactions.Annotation.AnnotationEmphasis e in System.Enum.GetValues(typeof(SpatialBlend.Interactions.Annotation.AnnotationEmphasis)))
            {
                annotationColours.Add(look.EdgeColor(e));
                foreach (SpatialBlend.Interactions.Annotation.CornerFocus f in System.Enum.GetValues(typeof(SpatialBlend.Interactions.Annotation.CornerFocus)))
                    annotationColours.Add(look.BracketColor(f, e));
            }
            foreach (var annotation in annotationColours)
            {
                Color.RGBToHSV(annotation, out _, out float annotationSaturation, out _);
                if (annotationSaturation < 0.2f) continue;
                Assert.That(Gap(Hue(p.RealAnchor), Hue(annotation)), Is.GreaterThanOrEqualTo(90f), "annotation colour " + annotation + " against the real-anchor outline");
            }
            Assert.That(Gap(Hue(p.RealAnchor), Hue(p.Accent)), Is.GreaterThanOrEqualTo(90f), "not the accent (virtual things, practice targets)");
            Assert.That(Gap(Hue(p.RealAnchor), Hue(p.Success)), Is.GreaterThanOrEqualTo(90f));
            Assert.That(Gap(Hue(p.RealAnchor), Hue(p.Warning)), Is.GreaterThanOrEqualTo(12f), "orange, not the warning yellow");
            Color.RGBToHSV(p.RealAnchor, out _, out float s, out float v);
            Assert.That(s, Is.GreaterThanOrEqualTo(0.7f), "saturated, so it reads over a camera feed");
            Assert.That(v, Is.GreaterThanOrEqualTo(0.9f));
            Assert.That(SceneAnchorStyle.OutlineColor, Is.EqualTo(p.RealAnchor));
        }

        [Test]
        public void ControlsMeetTheThreeToOneNonTextBar()
        {
            var p = UiTheme.Palette;
            Assert.That(Contrast(p.Track, p.Surface), Is.GreaterThanOrEqualTo(3.0), "switch track and slider rail against a card");
            Assert.That(Contrast(p.Knob, p.Track), Is.GreaterThanOrEqualTo(3.0), "knob against the track");
            Assert.That(Contrast(p.Accent, p.Surface), Is.GreaterThanOrEqualTo(3.0), "accent fill against a card");
        }

        [Test]
        public void TextTokensMeetTheAngularSizeRuleAtTheirWorstReadingDistance()
        {
            foreach (int size in new[] { UiTheme.FontCaption, UiTheme.FontBody, UiTheme.FontLabel, UiTheme.FontTitle, UiTheme.FontHeading })
                Assert.That(UiTheme.AngularSizeDegrees(size, UiTheme.HandWorstMetres), Is.GreaterThanOrEqualTo(UiTheme.MinEmDegrees), "hand panel size " + size);
            // A room surface grows with distance from 1 m, so a token keeps its angular size out to its farthest distance.
            foreach (int size in new[] { UiTheme.FontCaption, UiTheme.FontBody, UiTheme.FontTitle })
                Assert.That(UiTheme.AngularSizeDegreesMm(UiMetrics.Room.Dmm(size, UiTheme.RoomWorstMetres) * UiTheme.RoomWorstMetres, UiTheme.RoomWorstMetres),
                    Is.GreaterThanOrEqualTo(UiTheme.MinEmDegrees), "room size " + size);
            // The nominal hand distance is the comfortable case, at least 1.1 deg for body text.
            Assert.That(UiTheme.AngularSizeDegrees(UiTheme.FontBody, UiTheme.HandNominalMetres), Is.GreaterThanOrEqualTo(1.1f));
        }

        [Test]
        public void HitTargetsAreAtLeastFourCentimetres()
        {
            Assert.That(UiTheme.RowHeight * UiTheme.MetresPerUnit, Is.GreaterThanOrEqualTo(0.04f));
        }

        [Test]
        public void FontSourcesMatchTheRecordedChecksumsAndLicenceIsPresent()
        {
            const string dir = "Assets/SpatialBlend/Samples/Fonts/Pretendard/";
            string record = File.ReadAllText(dir + "SOURCE.txt");
            StringAssert.Contains("SIL Open Font License 1.1", record);
            Assert.That(File.Exists(dir + "OFL-LICENSE.txt"), Is.True);
            foreach (var file in new[] { "Pretendard-Regular.otf", "Pretendard-SemiBold.otf", "OFL-LICENSE.txt" })
            {
                string hash;
                using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(File.ReadAllBytes(dir + file)).Select(b => b.ToString("x2")));
                string recorded = file == "OFL-LICENSE.txt" ? "LICENSE.txt" : file;
                StringAssert.Contains(recorded, record);
                StringAssert.Contains(hash, record, file + " differs from the recorded sha256");
            }
        }

        [Test]
        public void FontSetIsCommittedOutsideResourcesAndItsSourceCoversLatinAndHangul()
        {
            Assert.That(UiFonts.SetPath, Does.Not.Contain("/Resources/"));
            var set = AssetDatabase.LoadAssetAtPath<UiFontSet>(UiFonts.SetPath);
            Assert.That(set, Is.Not.Null);
            foreach (var font in new[] { set.Regular, set.SemiBold })
            {
                Assert.That(font, Is.Not.Null);
                // The glyph table of a dynamic asset grows as text is drawn in the Editor, so it is not pinned; the source font is.
                Assert.That(font.sourceFontFile, Is.Not.Null);
                Assert.That(font.sourceFontFile.name, Does.StartWith("Pretendard"));
                foreach (char c in "Freeform0123방참가새만들기")
                    Assert.That(font.sourceFontFile.HasCharacter(c), Is.True, font.name + " source lacks '" + c + "'");
                Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Dynamic), "other Hangul is added on first use");
            }
        }

        [Test]
        public void GeneratedSpritesAreNineSlicedWithTheRequestedRadius()
        {
            var sprite = UiSprites.RoundedRect(28f);
            Assert.That(sprite.border.x, Is.EqualTo(28f * UiSprites.Supersample).Within(0.01f));
            Assert.That(sprite.border.y, Is.EqualTo(sprite.border.x));
            var pixels = UiSprites.Bake(28f, 0f, UiSprites.Mode.Fill, 0f);
            int n = UiSprites.Size(28f, 0f);
            Assert.That(pixels[0].a, Is.Zero, "the corner texel is outside the rounded shape");
            Assert.That(pixels[(n / 2) * n + n / 2].a, Is.EqualTo(255), "the centre is solid");
            Assert.That(pixels[(n / 2) * n].a, Is.EqualTo(255), "the straight edge is solid");
            var asset = UiSpriteAssets.RoundedRect(28f);
            Assert.That(asset.border, Is.EqualTo(sprite.border), "prefab and runtime sprites share their corners");
        }

        // ---- the control panel as built --------------------------------------------------------------------------

        private static GameObject NewCanvas(out RectTransform rect, bool prefabLike)
        {
            var go = new GameObject("test-canvas", typeof(RectTransform), typeof(Canvas));
            rect = (RectTransform)go.transform;
            if (!prefabLike) return go;
            go.AddComponent<Image>();
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(24, 24, 24, 24);
            vlg.childControlWidth = false;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rect.sizeDelta = new Vector2(UiTheme.PanelWidth, 0f);
            return go;
        }

        private static ControlPanelView BuildView(RectTransform rect, out SpatialBlendSettings settings)
        {
            settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            var local = settings;
            return ControlPanelView.Build(rect, UiTheme.PanelWidth, new ControlPanelActions(),
                new AvatarSettingsPresenter(() => local, () => { }), new SceneControlsPresenter(null, null, null, null));
        }

        [Test]
        public void TheCanvasIsExactlyThePanelSoTheInputSurfaceFollowsTheRootRect()
        {
            var go = NewCanvas(out var rect, true);
            try
            {
                var view = BuildView(rect, out var settings);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Assert.That(rect.rect.width, Is.EqualTo(view.Root.rect.width).Within(0.01f));
                Assert.That(rect.rect.height, Is.EqualTo(view.Root.rect.height).Within(0.01f), "no padding around the root: the surface is the panel");
                Assert.That(rect.rect.height, Is.EqualTo(ControlPanelView.MinHeight).Within(0.5f), "the release panel (no diagnostics) fits its minimum height");
                Assert.That(view.Root.rect.width, Is.EqualTo(UiTheme.PanelWidth).Within(0.01f));
                view.Title.ForceMeshUpdate();
                Assert.That(view.Title.textInfo.lineCount, Is.EqualTo(1), "the product title fits on one line at the title size");
                foreach (var label in new[] { view.UsePreset.Label, view.Anchors.Label, view.Preset.Label, view.Passthrough.Label, view.Headline.Text })
                {
                    label.ForceMeshUpdate();
                    Assert.That(label.textInfo.lineCount, Is.EqualTo(1), "'" + label.text + "' fits on one line");
                }
                Object.DestroyImmediate(settings);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TwoColumnsFitInsideThePanelAndEveryRowKeepsItsHitHeight()
        {
            var go = NewCanvas(out var rect, true);
            try
            {
                var view = BuildView(rect, out var settings);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                var root = view.Root.rect;
                var corners = new Vector3[4];
                view.Root.GetWorldCorners(corners);
                var rootBounds = new Bounds(corners[0], Vector3.zero);
                foreach (var c in corners) rootBounds.Encapsulate(c);
                var room = (RectTransform)view.Root.Find("Columns/Room");
                var side = (RectTransform)view.Root.Find("Columns/Settings");
                Assert.That(room.rect.width, Is.GreaterThan(280f));
                Assert.That(side.rect.width, Is.GreaterThan(280f));
                Assert.That(room.rect.width + side.rect.width, Is.LessThan(root.width), "padding and the gap are left over");
                foreach (var r in new[] { room, side })
                {
                    r.GetWorldCorners(corners);
                    foreach (var c in corners) Assert.That(rootBounds.Contains(new Vector3(c.x, c.y, rootBounds.center.z)), Is.True, r.name + " is inside the panel");
                }
                Assert.That(((RectTransform)view.Connect.transform).rect.height, Is.GreaterThanOrEqualTo(UiTheme.RowHeight - 0.01f));
                view.ApplySession(SessionStatusPresenter.Build(new SessionStatusInput { LinkState = SpatialBlendConnectionState.Connected, LibraryCompatible = true }));
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                foreach (var button in new[] { view.LeaveRoom })
                    Assert.That(((RectTransform)button.transform).rect.height, Is.GreaterThanOrEqualTo(UiTheme.RowHeight - 0.01f));
                foreach (var toggle in new[] { view.UsePreset, view.Anchors })
                    Assert.That(((RectTransform)toggle.Toggle.transform).rect.height, Is.GreaterThanOrEqualTo(UiTheme.RowHeight - 0.01f));
                Assert.That(view.Root.rect.width, Is.EqualTo(UiTheme.PanelWidth).Within(0.01f));
                view.Title.ForceMeshUpdate();
                Assert.That(view.Title.textInfo.lineCount, Is.EqualTo(1), "the product title fits on one line at the title size");
                foreach (var label in new[] { view.UsePreset.Label, view.Anchors.Label, view.Preset.Label, view.Passthrough.Label, view.Headline.Text })
                {
                    label.ForceMeshUpdate();
                    Assert.That(label.textInfo.lineCount, Is.EqualTo(1), "'" + label.text + "' fits on one line");
                }
                Object.DestroyImmediate(settings);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void PanelUsesTokensOnly()
        {
            var go = NewCanvas(out var rect, false);
            try
            {
                var view = BuildView(rect, out var settings);
                var p = UiTheme.Palette;
                Assert.That(view.Connect.colors.normalColor, Is.EqualTo(p.Accent));
                Assert.That(view.LeaveRoom.colors.normalColor, Is.EqualTo(p.Surface), "the secondary action is the outline style");
                foreach (var text in view.Root.GetComponentsInChildren<TMP_Text>(true))
                    Assert.That(text.fontSize, Is.GreaterThanOrEqualTo(UiTheme.FontCaption), text.name);
                Object.DestroyImmediate(settings);
            }
            finally { Object.DestroyImmediate(go); }
        }

        private sealed class ListBrowser : FusionSessionBrowser
        {
            protected override bool CanStartSession => true;
            protected override void StartSession(string sessionName) { }
            protected override System.Threading.Tasks.Task<string> JoinLobbyAsync() => System.Threading.Tasks.Task.FromResult<string>(null);
            protected override System.Threading.Tasks.Task CloseLobbyAsync() => System.Threading.Tasks.Task.CompletedTask;
        }

        [Test]
        public void TheRoomListWithAllItsRowsStaysInsideTheInputSurface()
        {
            var go = NewCanvas(out var rect, true);
            var browserObject = new GameObject("browser", typeof(ListBrowser));
            try
            {
                var view = BuildView(rect, out var settings);
                var browser = browserObject.GetComponent<ListBrowser>();
                var list = SessionListPanel.Create(view.Connect, browser);
                list.Show();
                browser.ApplySessions(Enumerable.Range(1, SessionListPanel.MaxRows + 4)
                    .Select(i => new FusionSessionEntry("Room " + i, 1, 9)));
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                var surface = rect.rect;          // the Interaction SDK surfaces are fitted to the canvas rectangle
                var corners = new Vector3[4];
                int checkedButtons = 0;
                foreach (var button in list.GetComponentsInChildren<Button>(true))
                {
                    ((RectTransform)button.transform).GetWorldCorners(corners);
                    foreach (var c in corners)
                    {
                        var local = rect.InverseTransformPoint(c);
                        Assert.That(local.y, Is.GreaterThanOrEqualTo(surface.yMin - 0.01f), button.name + " " + button.GetComponentInChildren<TMP_Text>().text + " is below the input surface");
                        Assert.That(local.y, Is.LessThanOrEqualTo(surface.yMax + 0.01f));
                    }
                    Assert.That(((RectTransform)button.transform).rect.height, Is.GreaterThanOrEqualTo(UiTheme.RowHeight - 0.01f));
                    checkedButtons++;
                }
                Assert.That(checkedButtons, Is.EqualTo(SessionListPanel.MaxRows + 2), "the rooms, New room and Back");
                Object.DestroyImmediate(settings);
            }
            finally
            {
                Object.DestroyImmediate(browserObject);
                Object.DestroyImmediate(go);
            }
        }

        // ---- toasts and labels ------------------------------------------------------------------------------------

        private static SessionStatusView Status(string headline, string link, StatusTone tone) =>
            new SessionStatusView { Headline = headline, Detail = link, Tone = tone };

        [Test]
        public void SessionToastsFollowOnlyChangesOfTheStatusThePanelAlreadyShows()
        {
            var idle = Status("Not in a room", "", StatusTone.Neutral);
            var connecting = Status("Connecting…", "", StatusTone.Busy);
            var hosting = Status("In Room 3 · 2 people", "", StatusTone.Good);
            var failed = Status("Couldn't connect", "Check your Wi-Fi and try again.", StatusTone.Bad);

            Assert.That(SessionToastPresenter.Describe(false, default, hosting).Show, Is.False, "the first status shows nothing");
            Assert.That(SessionToastPresenter.Describe(true, hosting, hosting).Show, Is.False, "an unchanged status shows nothing");
            Assert.That(SessionToastPresenter.Describe(true, hosting, Status("In Room 3 · 3 people", "", StatusTone.Good)).Show, Is.False, "a head count change is not news");
            var busy = SessionToastPresenter.Describe(true, idle, connecting);
            Assert.That(busy.Show, Is.True);
            Assert.That(busy.Tone, Is.EqualTo(StatusTone.Busy));
            Assert.That(busy.Title, Is.EqualTo("Connecting…"));
            Assert.That(SessionToastPresenter.Describe(true, connecting, hosting).Tone, Is.EqualTo(StatusTone.Good));
            var bad = SessionToastPresenter.Describe(true, connecting, failed);
            Assert.That(bad.Tone, Is.EqualTo(StatusTone.Bad));
            Assert.That(bad.Message, Is.EqualTo("Check your Wi-Fi and try again."));
            var left = SessionToastPresenter.Describe(true, hosting, idle);
            Assert.That(left.Show, Is.False, "going back to idle after a good state says nothing"); 
            Assert.That(SessionToastPresenter.Describe(true, failed, idle).Show, Is.False, "going idle after a failure is not a leave");
        }

        [Test]
        public void ToastHostStacksTheNewestCapsAtThreeAndNeverTakesRayInput()
        {
            var go = NewCanvas(out var rect, false);
            try
            {
                var view = BuildView(rect, out var settings);
                var host = UiToastHost.Create(view.Root);
                for (int i = 0; i < 5; i++) host.Show(StatusTone.Good, "Toast " + i, "message");
                Assert.That(host.Count, Is.EqualTo(UiToastHost.MaxVisible));
                var titles = host.GetComponentsInChildren<TMP_Text>().Select(t => t.text).ToArray();
                Assert.That(titles, Does.Contain("Toast 4"));
                Assert.That(titles, Does.Not.Contain("Toast 0"));
                foreach (var group in host.GetComponentsInChildren<CanvasGroup>()) Assert.That(group.blocksRaycasts, Is.False);
                Assert.That(host.GetComponent<LayoutElement>().ignoreLayout, Is.True, "the panel's layout does not include the toasts");
                Object.DestroyImmediate(settings);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void WorldLabelFacesTheViewerAndStaysAboveItsTarget()
        {
            var target = new GameObject("target");
            var cameraObject = new GameObject("viewer", typeof(Camera));
            cameraObject.tag = "MainCamera";
            try
            {
                cameraObject.transform.position = new Vector3(0f, 1.6f, -2f);
                var label = UiWorldLabel.Create(target.transform, "Desk surface");
                label.Viewer = cameraObject.GetComponent<Camera>();
                typeof(UiWorldLabel).GetMethod("LateUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(label, null);
                Assert.That(label.transform.position.y, Is.EqualTo(0.15f).Within(1e-4f));
                var toCamera = (cameraObject.transform.position - label.transform.position).normalized;
                Assert.That(Vector3.Dot(-label.transform.forward, toCamera), Is.GreaterThan(0.99f), "the pill faces the camera");
                Assert.That(label.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("Desk surface"));
                float distance = Vector3.Distance(label.transform.position, cameraObject.transform.position);
                float emMm = label.GetComponentInChildren<TMP_Text>().fontSize * label.transform.lossyScale.x * 1000f;
                Assert.That(emMm / distance, Is.EqualTo(UiTheme.FontBody * UiMetrics.Room.Zoom).Within(0.05f), "20 dmm, grown for its distance (UiMetrics.Room)");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
            }
        }
    }
}
#endif
