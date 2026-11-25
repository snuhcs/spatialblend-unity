// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Scanning;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The guard of the unified design system: sizes are angular (dmm), every world-space canvas declares its
    /// metres per unit through <see cref="UiMetrics"/>, every runtime text is the theme's Pretendard at a token size, the tokens meet Meta's
    /// floors, and the panels and buttons are the one style. A new surface that sets its own scale or font size fails here.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class UiDesignSystemTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private GameObject _eye;

        [SetUp]
        public void SetUp()
        {
            _eye = new GameObject("eye", typeof(Camera)) { tag = "MainCamera" };
            _eye.transform.position = new Vector3(0f, 1.6f, 0f);
            _spawned.Add(_eye);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        // ---- the tokens --------------------------------------------------------------------------------------------

        [Test]
        public void EveryCodeBuiltSurfaceIsOneDmmPerUnit_TheScanCardIsTheOneZoom()
        {
            foreach (var surface in UiMetrics.All)
            {
                if (surface.Name == UiMetrics.RoomPrefab.Name || surface.Name == UiMetrics.HandPrefab.Name) continue;   // authored prefabs
                Assert.That(surface.UnitsPerDmm, Is.EqualTo(1f).Within(1e-4f), surface.Name);
                Assert.That(surface.MetresPerUnit, Is.EqualTo(0.001f * surface.ReadingMetres * surface.Zoom).Within(1e-8f), surface.Name);
                // The one type scale, scaled per surface only where chosen: the scan card 4/3, the room widgets 5/6.
                if (surface.Name == UiMetrics.ScanCard.Name) Assert.That(surface.Zoom, Is.EqualTo(4f / 3f).Within(1e-6f));
                else if (surface.Name == UiMetrics.Room.Name) Assert.That(surface.Zoom, Is.EqualTo(UiMetrics.RoomZoom));
                else Assert.That(surface.Zoom, Is.EqualTo(1f), surface.Name + ": no scale factor of its own");
            }
            Assert.That(UiMetrics.RoomPrefab.Zoom, Is.EqualTo(UiMetrics.RoomZoom), "the authored room panels share the room rule");
            Assert.That(UiMetrics.Room.MetresPerUnit, Is.EqualTo(0.001f).Within(1e-8f), "1 mm per unit near, the pre-unification size");
            Assert.That(UiMetrics.ScanCard.MetresPerUnit, Is.EqualTo(0.002f).Within(1e-7f), "the 2x card is kept exactly");
            Assert.That(UiMetrics.Hand.MetresPerUnit, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-9f), "the hand panel size is kept exactly");
            Assert.That(UiMetrics.RoomPrefab.UnitsPerDmm, Is.EqualTo(1f).Within(1e-4f), "the authored room panels: one unit per dmm token");
        }

        [Test]
        public void RoomWidgetsReadTwentyDmm_TheOwnersChoice_FromTheirReadingDistanceOut()
        {
            foreach (var room in new[] { UiMetrics.Room, UiMetrics.RoomPrefab })
            {
                Assert.That(room.Dmm(room.Units(UiTheme.FontBody), 1.5f), Is.EqualTo(UiMetrics.MinBodyDmm).Within(0.01f), room.Name + " at 1.5 m");
                Assert.That(room.Dmm(room.Units(UiTheme.FontBody), room.MaxMetres), Is.EqualTo(UiMetrics.MinBodyDmm).Within(0.01f), room.Name + " at its farthest");
                Assert.That(room.Units(UiTheme.FontBody), Is.EqualTo(UiTheme.FontBody).Within(1e-4f), room.Name + ": still the 24 unit token");
            }
            // 1.25 times the size before the unification at 1.5 m (1 mm per unit then).
            Assert.That(UiMetrics.Room.WorldScale(1.5f) / 0.001f, Is.EqualTo(1.25f).Within(1e-3f));
            Assert.That(UiMetrics.Hand.Zoom, Is.EqualTo(1f), "the hand panel and the tutorial card keep the type scale as it is");
        }

        [Test]
        public void TheTypeScaleAndTheHitTargetsMeetMetasFloorsOnEverySurface()
        {
            foreach (int token in UiTheme.FontTokens)
                if (token != UiTheme.FontFine)
                    Assert.That(token, Is.GreaterThanOrEqualTo(UiMetrics.MinBodyDmm), "reading text is at least 20 dmm: " + token);
            foreach (float hit in new[] { UiTheme.ButtonHeight, UiTheme.ToolHeight, UiTheme.RowHeight, UiTheme.StatusRowHeight })
                Assert.That(hit, Is.GreaterThanOrEqualTo(UiMetrics.MinHitDmm), "hit target " + hit);
            foreach (var surface in UiMetrics.All)
            {
                // At the reading distance a token is its dmm (times the zoom); a growing surface keeps that out to its farthest distance.
                Assert.That(surface.Dmm(surface.Units(UiTheme.FontBody), surface.ReadingMetres), Is.EqualTo(UiTheme.FontBody * surface.Zoom).Within(0.01f), surface.Name);
                Assert.That(surface.Dmm(surface.Units(UiTheme.FontBody), surface.MaxMetres), Is.EqualTo(UiTheme.FontBody * surface.Zoom).Within(0.01f), surface.Name);
            }
        }

        // ---- every world-space canvas declares its surface ---------------------------------------------------------

        private static IEnumerable<(string rel, string code)> RuntimeSources()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "SpatialBlend"));
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                if (rel.Contains("/Editor/") || rel.StartsWith("Editor/") || rel.Contains("/Test/")) continue;
                // The desktop spectator and observer draw screen-space UI on a monitor, not in the headset (a separate branch).
                if (rel.Contains("/Spectator/") || rel.Contains("/Observer/") || rel.Contains("/Local Simulator/")) continue;
                yield return (rel, Regex.Replace(Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//.*", string.Empty));
            }
        }

        /// <summary>Runtime files allowed to make a canvas world-space without <see cref="UiMetrics.MakeWorldSpace"/>, and why.</summary>
        private static readonly Dictionary<string, string> CanvasAllowList = new Dictionary<string, string>
        {
            { "Samples/Scripts/UI/Theme/UiMetrics.cs", "The helper itself: MakeWorldSpace sets the render mode, the scale and the tag of the canvas together." },
        };

        private static readonly Regex WorldSpace = new Regex(@"RenderMode\s*\.\s*WorldSpace");
        private static readonly Regex LiteralScale = new Regex(@"localScale\s*=\s*Vector3\s*\.\s*one\s*\*\s*\(?\s*[0-9.]+f?\b(?!\s*/)");
        private static readonly Regex LiteralFontSize = new Regex(@"fontSize\s*=\s*[0-9]");
        /// <summary>A file that creates or owns UI components: text, images, canvases.</summary>
        private static readonly Regex UiFile = new Regex(@"\b(TextMeshProUGUI|TMP_Text|RawImage|CanvasScaler|GraphicRaycaster)\b|AddComponent\s*<\s*(Image|Canvas)\s*>|typeof\s*\(\s*(Image|Canvas)\s*\)|\bCanvas\s+\w+\s*[;=]");

        [Test]
        public void EveryWorldSpaceCanvasIsMadeByUiMetricsOnTheCanvasItself()
        {
            var findings = new List<string>();
            foreach (var (rel, code) in RuntimeSources())
            {
                // The render mode is set in one place, MakeWorldSpace(canvas, surface), which also sets that canvas's scale and tag.
                if (WorldSpace.IsMatch(code) && !CanvasAllowList.ContainsKey(rel))
                    findings.Add(rel + ": sets RenderMode.WorldSpace itself; make the canvas with UiMetrics.NewCanvas or UiMetrics.MakeWorldSpace(canvas, surface)");
                if (!UiFile.IsMatch(code)) continue;
                if (LiteralScale.IsMatch(code)) findings.Add(rel + ": sets a UI scale from a literal (" + LiteralScale.Match(code).Value + "); use UiMetrics");
                if (code.Contains("MmToM")) findings.Add(rel + ": a private millimetre scale (MmToM); use UiMetrics");
                if (LiteralFontSize.IsMatch(code)) findings.Add(rel + ": a literal font size (" + LiteralFontSize.Match(code).Value + "); use a UiTheme.Font* token");
            }
            Assert.That(findings, Is.Empty, string.Join("\n", findings));
        }

        [Test]
        public void TheSourceGuardRecognisesUiFilesAndLiteralSizes()
        {
            Assert.That(UiFile.IsMatch("var t = go.AddComponent<TextMeshProUGUI>();"), Is.True);
            Assert.That(UiFile.IsMatch("go.AddComponent<Image>();"), Is.True);
            Assert.That(UiFile.IsMatch("new GameObject(\"x\", typeof(RectTransform), typeof(Canvas));"), Is.True);
            Assert.That(UiFile.IsMatch("cube.transform.localScale = Vector3.one * 0.1f;"), Is.False, "a 3D prop is not UI");
            Assert.That(LiteralScale.IsMatch("rect.localScale = Vector3.one * 0.001f;"), Is.True);
            Assert.That(LiteralScale.IsMatch("rect.localScale = Vector3.one * (UiMetrics.Hand.MetresPerUnit / inner);"), Is.False);
            Assert.That(LiteralFontSize.IsMatch("label.fontSize = 18;"), Is.True);
            Assert.That(LiteralFontSize.IsMatch("label.fontSize = UiTheme.FontFine;"), Is.False);
        }

        [Test]
        public void EveryCanvasAllowListEntryStillNeedsItsAllowance()
        {
            var sources = RuntimeSources().ToDictionary(s => s.rel, s => s.code);
            foreach (var pair in CanvasAllowList)
            {
                Assert.That(sources.ContainsKey(pair.Key), Is.True, pair.Key + " is gone: drop its allowance");
                Assert.That(WorldSpace.IsMatch(sources[pair.Key]), Is.True, pair.Key + " no longer makes a canvas: drop its allowance");
            }
        }

        // ---- the widgets as built ----------------------------------------------------------------------------------

        private GameObject Track(GameObject go) { _spawned.Add(go); return go; }

        private GameObject Cube(Vector3 position)
        {
            var cube = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.transform.position = position;
            cube.transform.localScale = Vector3.one * 0.1f;
            return cube;
        }

        /// <summary>Builds every code-built room and head widget the way its owner does, shown.</summary>
        private List<GameObject> BuildAll()
        {
            var roots = new List<GameObject>();
            var mode = Track(new GameObject("mode")).AddComponent<ModeSwitchUi>();
            mode.Build(_eye.transform);
            mode.SnapHud();
            var target = Cube(new Vector3(0f, 1f, 2.5f));
            mode.ShowPrompt(target.transform, target.GetComponent<Renderer>().bounds, 0.05f);
            mode.ShowMenuPrompt(target.transform, target.GetComponent<Renderer>().bounds, 0.05f);
            mode.SetChipVisible(true);
            mode.ShowCaption("Editing: move things.");
            mode.SetUndoVisible(true);
            mode.ShowToast(5, 0.5f);
            roots.Add(mode.gameObject);

            var hint = BoxUndoHint.Create(null);
            Track(hint.gameObject);
            hint.Show(new Vector3(0f, 0f, 3f), 1, _eye.transform);
            roots.Add(hint.gameObject);

            var tag = EditPresenceTag.Create("tag", Cube(new Vector3(1f, 1f, 3f)).transform, Vector3.up * 0.3f);
            Track(tag.gameObject);
            tag.Show("Moving: Player 2");
            roots.Add(tag.gameObject);

            var overlay = UiToastOverlay.Create(0);
            Track(overlay.gameObject);
            overlay.Host.Show(StatusTone.Good, "Joined Room 3", "2 people are here.");
            roots.Add(overlay.gameObject);

            var labelTarget = Track(new GameObject("label-target"));
            labelTarget.transform.position = new Vector3(0f, 1f, 2f);
            var label = UiWorldLabel.Create(labelTarget.transform, "Desk surface", StatusTone.Good);
            label.Viewer = _eye.GetComponent<Camera>();
            typeof(UiWorldLabel).GetMethod("LateUpdate", Any).Invoke(label, null);
            roots.Add(label.gameObject);

            var callout = Cube(new Vector3(-1f, 0.2f, 3f)).AddComponent<PlaceCalloutView>();
            typeof(PlaceCalloutView).GetField("_eye", Any).SetValue(callout, _eye.transform);
            typeof(PlaceCalloutView).GetMethod("EnsurePanel", Any).Invoke(callout, null);
            typeof(PlaceCalloutView).GetMethod("Place", Any).Invoke(callout, new object[] { new Vector3(-1f, 0.6f, 3f) });
            roots.Add((GameObject)typeof(PlaceCalloutView).GetField("_panel", Any).GetValue(callout));

            var anchor = Track(new GameObject("couch"));
            anchor.transform.SetPositionAndRotation(new Vector3(1f, 0.4f, 2f), Quaternion.Euler(-90f, 0f, 0f));
            var menu = Track(new GameObject("share")).AddComponent<FurnitureShareMenu>();
            menu.Show(new SceneAnchorRecord { Label = "COUCH", Anchor = anchor, LocalSize = new Vector3(1f, 0.8f, 0.4f) });
            roots.Add(anchor);
            foreach (var root in roots) Track(root);
            return roots;
        }

        private static UiSurface SurfaceOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var tag = t.GetComponent<UiSurfaceTag>();
                if (tag != null) return UiMetrics.All.First(s => s.Name == tag.Surface);
            }
            throw new AssertionException("no declared surface above " + t);
        }

        [Test]
        public void EveryCodeBuiltCanvasIsDeclaredAndScaledByItsSurface()
        {
            foreach (var root in BuildAll())
            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas.renderMode != RenderMode.WorldSpace || (canvas.transform.parent != null && canvas.transform.parent.GetComponentInParent<Canvas>(true) != null)) continue;
                var tag = canvas.GetComponent<UiSurfaceTag>();
                Assert.That(tag, Is.Not.Null, canvas.name + " declares its surface");
                var surface = UiMetrics.All.First(s => s.Name == tag.Surface);
                float scale = canvas.transform.lossyScale.x;
                // Widgets that face Camera.main grow for its distance; another fixture's leftover camera may be it, so judge by both viewers.
                Transform main = Camera.main != null ? Camera.main.transform : _eye.transform;
                float near = Vector3.Distance(canvas.transform.position, _eye.transform.position);
                float far = Vector3.Distance(canvas.transform.position, main.position);
                bool ok = new[] { surface.MetresPerUnit, surface.WorldScale(near), surface.WorldScale(far) }
                    .Any(expected => Mathf.Abs(scale - expected) <= expected * 1e-2f);
                Assert.That(ok, Is.True, canvas.name + " on " + surface.Name + ": scale " + scale + " is not the surface's at any viewer distance");
                Assert.That(canvas.transform.lossyScale.y, Is.EqualTo(scale).Within(scale * 1e-3f), canvas.name + " is uniformly scaled");
            }
        }

        [Test]
        public void EveryRuntimeTextIsThePretendardFaceAtATokenSize()
        {
            Assert.That(UiFonts.Set, Is.Not.Null, "the generated font set");
            int checkedTexts = 0;
            foreach (var root in BuildAll())
            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                Assert.That(text.font == UiFonts.Regular || text.font == UiFonts.SemiBold, Is.True, text.name + " uses " + (text.font != null ? text.font.name : "no font"));
                var surface = SurfaceOf(text.transform);
                var sizes = UiTheme.FontTokens.Select(t => surface.Units(t)).ToArray();
                Assert.That(sizes.Any(u => Mathf.Abs(u - text.fontSize) < 0.01f), Is.True, text.name + " on " + surface.Name + " is " + text.fontSize + ", not a type-scale token");
                Assert.That(text.enableAutoSizing, Is.False, text.name);
                Assert.That(text.fontStyle & FontStyles.Bold, Is.EqualTo((FontStyles)0), text.name + ": weight comes from the SemiBold face, not a faux bold");
                checkedTexts++;
            }
            Assert.That(checkedTexts, Is.GreaterThan(15));
        }

        [Test]
        public void EveryAuthoredPanelPrefabSitsOnItsSurfaceWithTokenText()
        {
            // Prefabs whose canvas is scaled by their owner at runtime, not by the prefab.
            var runtimeScaled = new Dictionary<string, string>
            {
                { "ControlPanel.prefab", "ControlPanel.ApplyHandPlacement scales its root to UiMetrics.Hand (pinned by ControlPanelPlacementTests)." },
                { "PassthroughPanel.prefab", "The legacy scan panel; its manager scales it (no path opens it)." },
                { "VOPanel.prefab", "Legacy, only in the Test scene; scaled by its parent there." },
            };
            int checkedPrefabs = 0;
            foreach (string path in UiConformanceGuardTests.AllPrefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var canvas = prefab.GetComponentsInChildren<Canvas>(true).FirstOrDefault(c => c.renderMode == RenderMode.WorldSpace);
                if (canvas == null || runtimeScaled.ContainsKey(Path.GetFileName(path))) continue;
                UiSurface? declared = UiMetrics.PrefabSurface(path);
                Assert.That(declared.HasValue, Is.True, path + " has a world-space canvas but no surface in UiMetrics.PrefabSurface");
                var surface = declared.Value;
                Vector3 s = canvas.transform.lossyScale;
                Assert.That(s.x, Is.EqualTo(surface.MetresPerUnit).Within(1e-7f), path);
                Assert.That(s.y, Is.EqualTo(s.x).Within(1e-7f), path + " is uniformly scaled (text is not stretched)");
                // And so is everything under it: a child scale that compensates a stretch moves the stretch, it does not remove it.
                foreach (var rect in canvas.GetComponentsInChildren<RectTransform>(true))
                {
                    Vector3 r = rect.lossyScale;
                    Assert.That(Mathf.Abs(r.y), Is.EqualTo(Mathf.Abs(r.x)).Within(Mathf.Abs(r.x) * 1e-4f + 1e-9f),
                        path + ": " + rect.name + " is stretched (accumulated scale " + r.x + " x " + r.y + ")");
                }
                if (surface.Name == UiMetrics.RoomPrefab.Name)
                    foreach (var text in prefab.GetComponentsInChildren<TMP_Text>(true))
                        Assert.That(UiTheme.FontTokens.Select(t => surface.Units(t)).Any(u => Mathf.Abs(u - text.fontSize) < 0.01f), Is.True,
                            path + ": " + text.name + " " + text.fontSize + " is not a type-scale token");
                checkedPrefabs++;
            }
            Assert.That(checkedPrefabs, Is.GreaterThanOrEqualTo(3));
        }

        // ---- the object menu over its object (BillboardToCamera) ----------------------------------------------------

        private const string ObjectMenuPrefab = "Assets/SpatialBlend/Interaction/Prefabs/UI/ManipulationPopUpUI.prefab";

        private (GameObject panel, BillboardToCamera billboard, Bounds box) MenuOverBox(Vector3 boxCentre, Vector3 boxSize, Vector3 eye, float clampY)
        {
            var box = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.transform.position = boxCentre;
            box.transform.localScale = boxSize;
            Physics.SyncTransforms();   // the collider bounds the billboard reads, as the physics step would leave them
            _eye.transform.position = eye;
            var panel = Track((GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ObjectMenuPrefab)));
            var billboard = panel.GetComponent<BillboardToCamera>() ?? panel.AddComponent<BillboardToCamera>();
            typeof(BillboardToCamera).GetField("_targetCamera", Any).SetValue(billboard, _eye.GetComponent<Camera>());
            if (clampY > 0f) billboard.SetVerticalOffsetClamp(clampY);
            billboard.SetTarget(box.transform);
            return (panel, billboard, box.GetComponent<Collider>().bounds);
        }

        private static float WorldWidth(GameObject panel)
        {
            var canvas = (RectTransform)panel.GetComponentInChildren<Canvas>(true).transform;
            return canvas.rect.width * canvas.lossyScale.x;
        }

        private static Bounds CanvasBounds(GameObject panel)
        {
            var canvas = (RectTransform)panel.GetComponentInChildren<Canvas>(true).transform;
            var corners = new Vector3[4];
            canvas.GetWorldCorners(corners);
            var bounds = new Bounds(corners[0], Vector3.zero);
            foreach (var c in corners) bounds.Encapsulate(c);
            return bounds;
        }

        [Test]
        public void TheBoxPanelAtItsFixedHeightNeverGrowsSoItNeverCoversTheBox_At3m()
        {
            // A tall drawn box (its top + gap above the 1.0 m clamp, ManipulationContext's _boxPanelFixedY): the panel is held at 1.0 m and
            // moved beside the box by offsets tuned for the panel's own size. From 3 m it must be exactly where and as big as from 0.9 m, so
            // growth can never push it up or out over the box.
            Vector3 centre = new Vector3(0f, 0.6f, 0f), size = new Vector3(0.5f, 1.2f, 0.5f);
            var (near, _, _) = MenuOverBox(centre, size, new Vector3(0f, 1.6f, -0.9f), 1.0f);
            float nearWidth = WorldWidth(near);
            Vector3 nearPosition = near.transform.position;
            Vector3 nearScale = near.transform.localScale;
            UnityEngine.Object.DestroyImmediate(near);
            var (far, _, box) = MenuOverBox(centre, size, new Vector3(0f, 1.6f, -3f), 1.0f);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ObjectMenuPrefab);
            Assert.That(far.transform.localScale, Is.EqualTo(prefab.transform.localScale), "no growth in the clamped (box) case");
            Assert.That(nearScale, Is.EqualTo(prefab.transform.localScale));
            Assert.That(far.transform.position.y, Is.EqualTo(1.0f).Within(1e-4f), "held at the clamp height");
            Assert.That(WorldWidth(far), Is.EqualTo(nearWidth).Within(1e-4f), "as wide from 3 m as from 0.9 m");
            Assert.That(Vector3.Distance(far.transform.position, nearPosition), Is.LessThan(1e-4f), "and not lifted or moved sideways");
            Bounds atFar = CanvasBounds(far);
            Assert.That(atFar.center.x, Is.GreaterThan(box.max.x), "its centre is beside the box");
        }

        [Test]
        public void TheObjectMenuGrowsWithDistanceButStaysUnderAMetreWide()
        {
            var (near, _, _) = MenuOverBox(new Vector3(0f, 0.2f, 0f), new Vector3(0.3f, 0.4f, 0.3f), new Vector3(0f, 1.0f, -0.6f), 0f);   // within 1 m: no growth
            float baseWidth = WorldWidth(near);
            UnityEngine.Object.DestroyImmediate(near);
            var (far, _, box) = MenuOverBox(new Vector3(0f, 0.2f, 0f), new Vector3(0.3f, 0.4f, 0.3f), new Vector3(0f, 1.6f, -3f), 0f);
            Bounds drawn = CanvasBounds(far);
            Assert.That(WorldWidth(far), Is.EqualTo(baseWidth * UiMetrics.RoomPrefab.MaxMetres / UiMetrics.RoomPrefab.ReadingMetres).Within(0.01f), "capped growth (1.4x) at 3 m");
            Assert.That(WorldWidth(far), Is.LessThanOrEqualTo(1.0f), "under a metre wide: it does not swallow rays aimed at nearby objects");
            Assert.That(drawn.min.y, Is.GreaterThan(box.max.y), "it grows upward: the gap above the object stays");
        }

        // ---- one panel style, one button style ---------------------------------------------------------------------

        [Test]
        public void ButtonsAreTheOneStyle_PrimaryAccentSecondarySurface()
        {
            var mode = Track(new GameObject("mode")).AddComponent<ModeSwitchUi>();
            mode.Build(_eye.transform);
            var primary = UiTheme.Button(true);
            var secondary = UiTheme.Button(false);
            var edit = mode.PromptObject.transform.Find("Edit").GetComponentInChildren<UnityEngine.UI.Image>(true);
            Assert.That(edit.color, Is.EqualTo(primary.Normal));
            mode.SetChipMode(SpatialBlend.Interactions.InteractionMode.Interaction);
            var chipFill = mode.ChipObject.transform.Find("Fill").GetComponent<UnityEngine.UI.Image>();
            Assert.That(chipFill.color, Is.EqualTo(secondary.Normal));
            mode.SetChipMode(SpatialBlend.Interactions.InteractionMode.SceneEdit);
            Assert.That(chipFill.color, Is.EqualTo(primary.Normal));
            Assert.That(mode.ChipText, Is.EqualTo("Editing"));
        }

        [Test]
        public void PanelsAreTheOneStyle_GradientHairlineAndTokenRadius()
        {
            foreach (var root in BuildAll())
            foreach (var gradient in root.GetComponentsInChildren<UiVerticalGradient>(true))
            {
                Assert.That((Vector3)(Vector4)gradient.Top, Is.EqualTo((Vector3)(Vector4)UiTheme.Palette.PanelTop), gradient.name);
                Assert.That((Vector3)(Vector4)gradient.Bottom, Is.EqualTo((Vector3)(Vector4)UiTheme.Palette.PanelBottom), gradient.name);
                Assert.That(gradient.Top.a, Is.EqualTo(UiTheme.Palette.PanelTop.a).Or.EqualTo(1f), gradient.name + ": the panel alpha, or opaque");
                Assert.That(gradient.transform.parent.Find("Ring"), Is.Not.Null, gradient.transform.parent.name + " has the hairline");
            }
            var prompt = Track(new GameObject("mode")).AddComponent<ModeSwitchUi>();
            prompt.Build(_eye.transform);
            Assert.That(prompt.PromptObject.transform.Find("Fill").GetComponent<UiVerticalGradient>(), Is.Not.Null, "the prompt is the panel style");
            Assert.That(prompt.ToastObject.transform.Find("Fill").GetComponent<UiVerticalGradient>(), Is.Not.Null, "the countdown is the panel style");
            // The head-locked caption and countdown can be up together and overlap: each covers what is behind it (coverage >= 0.99).
            foreach (var hud in new[] { prompt.ToastObject, prompt.CaptionObject })
            {
                var g = hud.transform.Find("Fill").GetComponent<UiVerticalGradient>();
                Assert.That(Mathf.Min(g.Top.a, g.Bottom.a), Is.GreaterThanOrEqualTo(0.99f), hud.name + " is opaque");
            }
        }

        [Test]
        public void TranslucentPanelsComposeOverPassthroughWithCoverageAlpha()
        {
            // The panel fills are 97% opaque. The UI and TMP shaders blend premultiplied (Blend One OneMinusSrcAlpha), so the eye buffer's
            // alpha is a + dst * (1 - a): the coverage the Quest compositor shows over the passthrough underlay.
            foreach (string path in new[] { "Assets/TextMesh Pro/Shaders/TMP_SDF.shader", "Assets/TextMesh Pro/Shaders/TMP_SDF-Mobile.shader" })
            {
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.StartsWith("Blend ") && !l.StartsWith("Blend Off")))
                    Assert.That(line, Is.EqualTo("Blend One OneMinusSrcAlpha"), path);
            }
            Assert.That(UiTheme.Palette.PanelTop.a, Is.GreaterThanOrEqualTo(0.9f), "a panel reads as a surface over the room");
        }
    }
}
#endif
