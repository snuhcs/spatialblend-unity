// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI.Editor;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The authored panel prefabs carry the UI tokens (<see cref="UiTheme"/>, Graphite direction), applied by
    /// <see cref="UiPrefabSkinner"/>. Backplates are the token gradient on a generated rounded sprite; controls use a token
    /// colour block (no Animator transition, no Interaction SDK rounded-box material, no blue, teal or flat gray of the earlier
    /// themes); text uses the UI typeface at a size that meets the angular-size rule at the room reading distance.
    /// (v1 unified the panels to a dark gray; this look replaces it.)
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class DarkThemePanelPrefabTests
    {
        private static readonly Color[] OldThemeColors =
        {
            new Color(0.21960786f, 0.21960786f, 0.21960786f, 1f),     // flat panel gray #383838
            new Color(0.29411766f, 0.29411766f, 0.29411766f, 1f),     // control gray #4B4B4B
            new Color(0f, 0.39215687f, 0.8784314f, 0.7294118f),       // blue pressed
            new Color(0.15686275f, 0.23529413f, 0.2784314f, 1f),      // teal
            new Color(0.10980392f, 0.16862746f, 0.2f, 1f),            // navy
        };

        public static string[] AllPrefabs => UiPrefabSkinner.Prefabs;

        private static GameObject Load(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            return prefab;
        }

        private static bool Near(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f && Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f;

        [Test]
        public void EveryPrefabResolvesItsScripts([ValueSource(nameof(AllPrefabs))] string path)
        {
            var prefab = Load(path);
            foreach (Transform child in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject), Is.Zero, path + ": " + child.name);
        }

        [Test]
        public void NoGraphicUsesTheSdkSampleMaterialsOrTheOldThemeColours([ValueSource(nameof(AllPrefabs))] string path)
        {
            var prefab = Load(path);
            foreach (var graphic in prefab.GetComponentsInChildren<Graphic>(true))
            {
                string material = graphic.material != null ? graphic.material.name : string.Empty;
                Assert.That(material, Does.Not.Contain("RoundedBoxUI"), path + ": " + graphic.name);
                Assert.That(material, Does.Not.Contain("LinearGradient"), path + ": " + graphic.name);
                if (graphic is TMP_Text) continue;
                foreach (var old in OldThemeColors)
                    Assert.That(Near(graphic.color, old), Is.False, path + ": " + graphic.name + " still has an old theme colour");
            }
        }

        [Test]
        public void BackplatesAreTheTokenGradientOnTheGeneratedSprite([ValueSource(nameof(AllPrefabs))] string path)
        {
            var prefab = Load(path);
            var backplates = prefab.GetComponentsInChildren<Image>(true)
                .Where(i => i.name.StartsWith("UIBackplate") || i.name == "Canvas").ToArray();
            Assert.That(backplates, Is.Not.Empty, path);
            var palette = UiTheme.Palette;
            foreach (var plate in backplates)
            {
                Assert.That(plate.sprite, Is.Not.Null, path + ": " + plate.name);
                Assert.That(AssetDatabase.GetAssetPath(plate.sprite), Is.EqualTo(UiSpriteAssets.PathFor(UiTheme.RadiusPanel)));
                var gradient = plate.GetComponent<UiVerticalGradient>();
                Assert.That(gradient, Is.Not.Null, path + ": " + plate.name);
                Assert.That(Near(gradient.Top, palette.PanelTop), Is.True);
                Assert.That(Near(gradient.Bottom, palette.PanelBottom), Is.True);
            }
        }

        [Test]
        public void ControlsUseTokenColourBlocksAndNoSdkAnimation([ValueSource(nameof(AllPrefabs))] string path)
        {
            var prefab = Load(path);
            var palette = UiTheme.Palette;
            var allowedPressed = new[] { palette.AccentPressed, palette.SurfacePressed, palette.Accent, palette.Track };
            int checkedControls = 0;
            foreach (var selectable in prefab.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable is Scrollbar) continue;
                checkedControls++;
                Assert.That(selectable.transition, Is.EqualTo(Selectable.Transition.ColorTint), path + ": " + selectable.name);
                var animator = selectable.GetComponent<Animator>();
                Assert.That(animator == null || !animator.enabled, Is.True, path + ": " + selectable.name + " keeps an Animator transition");
                bool isSwitch = selectable.GetComponent<UiToggleVisual>() != null && selectable.GetComponent<UiToggleVisual>().Knob != null;
                bool ghost = Near(selectable.colors.normalColor, new Color(0f, 0f, 0f, 0f));
                bool icon = Near(selectable.colors.normalColor, palette.Text);   // a bare icon button
                bool pressedOk = allowedPressed.Any(c => Near(selectable.colors.pressedColor, c));
                Assert.That(pressedOk || isSwitch, Is.True, path + ": " + selectable.name + " pressed colour is not a token " + selectable.colors.pressedColor);
                Assert.That(ghost || icon || isSwitch || selectable.targetGraphic is Image, Is.True, path + ": " + selectable.name + " has an Image target graphic");
            }
            Assert.That(checkedControls, Is.GreaterThan(0), path);
        }

        [Test]
        public void TextUsesTheUiTypefaceAtLegibleSizesForItsReadingDistance([ValueSource(nameof(AllPrefabs))] string path)
        {
            var prefab = Load(path);
            Assert.That(UiFonts.Set, Is.Not.Null, "the generated font set");
            float minMm = UiTheme.MinEmMillimetres(UiPrefabSkinner.ReadingFor(path));
            foreach (var text in prefab.GetComponentsInChildren<TMP_Text>(true))
            {
                Assert.That(text.font, Is.EqualTo(UiFonts.Regular).Or.EqualTo(UiFonts.SemiBold), path + ": " + text.name);
                float mm = text.fontSize * text.transform.lossyScale.x * 1000f;
                Assert.That(mm, Is.GreaterThanOrEqualTo(minMm - 0.05f), path + ": " + text.name + " em " + mm + " mm");
                Assert.That(text.enableAutoSizing, Is.False);
            }
        }

        [Test]
        public void GeneratedSpritesKeepTheirCornerRadiusOnEveryImage([ValueSource(nameof(AllPrefabs))] string path)
        {
            var prefab = Load(path);
            foreach (var image in prefab.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null || !AssetDatabase.GetAssetPath(image.sprite).StartsWith(UiSpriteAssets.Dir)) continue;
                var canvas = image.GetComponentInParent<Canvas>(true);
                float expected = canvas != null ? canvas.referencePixelsPerUnit / UiSprites.ReferencePpu : 1f;
                Assert.That(image.pixelsPerUnitMultiplier, Is.EqualTo(expected).Within(1e-4f), path + ": " + image.name);
            }
        }

        [Test]
        public void ControlPanelPrefabCarriesTheFontSetAndNoOldCanvasColour()
        {
            var prefab = Load(UiPrefabSkinner.ControlPanelPrefab);
            var so = new SerializedObject(prefab.GetComponent<ControlPanel>());
            Assert.That(so.FindProperty("_fontSet").objectReferenceValue, Is.EqualTo(UiFonts.Set));
            var canvasImage = prefab.GetComponentsInChildren<Image>(true).First(i => i.name == "ControlPanelCanvas");
            Assert.That(canvasImage.color.a, Is.Zero, "the host canvas no longer paints the old theme behind the generated panel");
        }

        [Test]
        public void MeshPopupUIDoesNotCarryAMissingScript()
        {
            var prefab = Load("Assets/SpatialBlend/Samples/Prefabs/UI/MeshPopupUI.prefab");
            Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
        }
    }
}
#endif
