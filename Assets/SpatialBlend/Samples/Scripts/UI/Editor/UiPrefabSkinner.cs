// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Applies the UI tokens to the authored panel prefabs, in place and idempotently. It restyles what is there
    /// (the object tree, scripts and serialised references, including the nested Interaction SDK button prefabs, stay
    /// untouched, so scene overrides and guids keep resolving): backplates get a rounded gradient surface, controls get
    /// a token colour block (the SDK's Animator transitions and rounded-box materials are switched off), text gets the UI
    /// typeface and a size that meets the angular-size rule at the room reading distance.
    /// Menu: SpatialBlend/UI/Apply Theme To Prefabs. Batch: -executeMethod ...UiPrefabSkinner.Apply.
    /// </summary>
    public static class UiPrefabSkinner
    {
        public static readonly string[] Prefabs =
        {
            "Assets/SpatialBlend/Samples/Prefabs/UI/InteractionModePanel.prefab",
            "Assets/SpatialBlend/Samples/Scanning/Prefabs/PassthroughPanel.prefab",
            "Assets/SpatialBlend/Interaction/Prefabs/UI/ManipulationPopUpUI.prefab",
            "Assets/SpatialBlend/Interaction/Prefabs/UI/GlTFObjectControlPanel.prefab",
            "Assets/SpatialBlend/Samples/Prefabs/BoxAnnotationInstance.prefab",
            "Assets/SpatialBlend/Samples/Prefabs/UI/MeshPopupUI.prefab",
            "Assets/SpatialBlend/Samples/Prefabs/UI/VOPanel.prefab",
        };
        /// <summary>
        /// The reading distance a prefab is designed for. The box annotation panel and the mesh and VO panels are small
        /// canvases (about 0.5 mm per unit, 33 to 36 cm wide) read within arm's reach; the others float in the room.
        /// An assumption about use, recorded here and pinned by a test.
        /// </summary>
        public static UiReading ReadingFor(string path) =>
            path.EndsWith("BoxAnnotationInstance.prefab") || path.EndsWith("MeshPopupUI.prefab") || path.EndsWith("VOPanel.prefab")
                ? UiReading.Hand : UiReading.Room;

        /// <summary>The surface a reading class maps to for the authored prefabs (UiMetrics.PrefabSurface by path agrees).</summary>
        public static UiSurface SurfaceFor(UiReading reading) => reading == UiReading.Hand ? UiMetrics.HandPrefab : UiMetrics.RoomPrefab;

        public const string ControlPanelPrefab = "Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab";

        [MenuItem("SpatialBlend/UI/Apply Theme To Prefabs")]
        public static void Apply()
        {
            if (UiFonts.Set == null) UiFontAssetBuilder.Generate();
            foreach (string path in Prefabs) SkinPrefab(path);
            SkinControlPanelHost();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static void SkinControlPanelHost()
        {
            var root = PrefabUtility.LoadPrefabContents(ControlPanelPrefab);
            try
            {
                var panel = root.GetComponent<ControlPanel>();
                var so = new SerializedObject(panel);
                so.FindProperty("_fontSet").objectReferenceValue = UiFonts.Set;
                so.ApplyModifiedPropertiesWithoutUndo();
                // The generated panel draws its own surface; the host canvas no longer paints the old theme behind it.
                foreach (var image in root.GetComponentsInChildren<Image>(true))
                {
                    if (image.name != "ControlPanelCanvas") continue;
                    image.sprite = null;
                    image.color = Color.clear;
                }
                PrefabUtility.SaveAsPrefabAsset(root, ControlPanelPrefab);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void SkinPrefab(string path)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Skin(root, ReadingFor(path));
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void Skin(GameObject root, UiReading reading)
        {
            var p = UiTheme.Palette;
            var skinned = new HashSet<Graphic>();
            // A component of ours saved while its script could not be resolved leaves an empty shell behind; drop it so
            // the pass below adds a real one (the pass is meant to be re-run).
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

            foreach (var selectable in root.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable is Slider slider) SkinSlider(slider, skinned);
                else if (selectable is Scrollbar bar) SkinScrollbar(bar, skinned);
                else if (selectable is Toggle || selectable is Button) SkinControl(selectable, skinned);
            }

            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (skinned.Contains(image)) continue;
                string n = image.name;
                if (n.StartsWith("UIBackplate") || (image.transform.parent != null && image.transform.parent.GetComponent<Canvas>() != null && n == "Canvas"))
                    Backplate(image);
                else if (n == "GradientEffect") Retire(image);
                else if (n == "Section") Card(image);
                else if (n == "Scroll View") { image.sprite = null; image.color = Color.clear; image.material = null; }
                else if (n == "Viewport")
                {
                    image.sprite = null;
                    image.color = Color.white;
                    image.material = null;
                    var mask = image.GetComponent<Mask>();
                    if (mask != null) mask.showMaskGraphic = false;
                }
                else if (IsLegacyRounded(image)) Backplate(image);
            }

            // The generated sprites carry their 9-slice border in texels at 400 pixels per unit: the corner is r canvas units only
            // when the image's multiplier makes the pixels per unit 4 (reference 100 over 100). Legacy overrides (2) distort it.
            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null || !AssetDatabase.GetAssetPath(image.sprite).StartsWith(UiSpriteAssets.Dir)) continue;
                var canvas = image.GetComponentInParent<Canvas>(true);
                image.pixelsPerUnitMultiplier = canvas != null ? canvas.referencePixelsPerUnit / UiSprites.ReferencePpu : 1f;
            }

            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true)) SkinText(text, reading);
        }

        // ---- surfaces ---------------------------------------------------------------------------------------------

        private static void Backplate(Image image)
        {
            Disable(image);
            image.material = null;
            image.sprite = UiSpriteAssets.RoundedRect(UiTheme.RadiusPanel);
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            var gradient = image.GetComponent<UiVerticalGradient>() ?? image.gameObject.AddComponent<UiVerticalGradient>();
            gradient.Top = UiTheme.Palette.PanelTop;
            gradient.Bottom = UiTheme.Palette.PanelBottom;
            image.SetAllDirty();
        }

        private static void Card(Image image)
        {
            Disable(image);
            image.material = null;
            image.sprite = UiSpriteAssets.RoundedRect(UiTheme.RadiusCard);
            image.type = Image.Type.Sliced;
            image.color = UiTheme.Palette.Surface;
        }

        private static void Retire(Image image)
        {
            Disable(image);
            image.material = null;
            image.enabled = false;
        }

        private static bool IsLegacyRounded(Image image) =>
            image.sprite != null && (AssetDatabase.GetAssetPath(image.sprite).EndsWith("RoundCorners.png") ||
                                     AssetDatabase.GetAssetPath(image.sprite) == UiSpriteAssets.PathFor(UiTheme.RadiusPanel));

        /// <summary>Switches off the Interaction SDK sample's rounded-box helpers on a graphic (they only serve its own shader).</summary>
        private static void Disable(Graphic graphic)
        {
            foreach (var behaviour in graphic.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                string type = behaviour.GetType().Name;
                if (type == "RoundedBoxUIProperties") behaviour.enabled = false;
                else if (behaviour is Mask mask) mask.enabled = false;
            }
        }

        // ---- controls ---------------------------------------------------------------------------------------------

        private static Image FindBackground(Selectable selectable)
        {
            var named = selectable.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "Background");
            if (named != null) return named;
            return selectable.targetGraphic as Image ?? selectable.GetComponent<Image>();
        }

        private static bool IsIconSprite(Image image) =>
            image != null && image.sprite != null && AssetDatabase.GetAssetPath(image.sprite).Contains("OCUIIconSet");

        private static void SkinControl(Selectable selectable, HashSet<Graphic> skinned)
        {
            var p = UiTheme.Palette;
            foreach (var behaviour in selectable.GetComponents<MonoBehaviour>())
                if (behaviour != null && behaviour.GetType().Name == "AnimatorOverrideLayerWeigth") behaviour.enabled = false;
            var animator = selectable.GetComponent<Animator>();
            if (animator != null) animator.enabled = false;

            var background = FindBackground(selectable);
            if (background == null) return;
            skinned.Add(background);
            selectable.transition = Selectable.Transition.ColorTint;
            selectable.targetGraphic = background;

            if (IsIconSprite(background))
            {
                // A bare icon button (zoom, undo): the icon is the graphic.
                selectable.colors = ControlPanelKit.Block(p.Text, p.Accent, p.AccentPressed, p.TextDisabled);
                return;
            }

            float originalAlpha = background.color.a;
            bool isSwitch = selectable is Toggle && selectable.transform.GetComponentsInChildren<Transform>(true).Any(t => t.name == "ToggleCircle");
            bool ghost = !isSwitch && (originalAlpha < 0.05f || selectable.colors.normalColor.a < 0.05f);   // second term: already skinned
            Disable(background);
            background.material = null;
            background.type = Image.Type.Sliced;
            background.color = Color.white;

            if (isSwitch)
            {
                background.sprite = UiSpriteAssets.RoundedRect(12f);
                var knob = selectable.GetComponentsInChildren<Image>(true).First(i => i.name == "ToggleCircle");
                Disable(knob);
                knob.material = null;
                knob.sprite = UiSpriteAssets.RoundedRect(10f);
                knob.type = Image.Type.Sliced;
                knob.color = p.Knob;
                skinned.Add(knob);
                var visual = selectable.GetComponent<UiToggleVisual>() ?? selectable.gameObject.AddComponent<UiToggleVisual>();
                visual.Toggle = (Toggle)selectable;
                visual.Background = background;
                visual.Knob = (RectTransform)knob.transform;
                visual.Content = new Graphic[0];
                visual.Apply(((Toggle)selectable).isOn);
                return;
            }

            background.sprite = UiSpriteAssets.RoundedRect(ghost ? 28f : UiTheme.RadiusCard);
            if (ghost)
            {
                selectable.colors = ControlPanelKit.Block(new Color(0f, 0f, 0f, 0f), p.SurfaceHover, p.SurfacePressed, new Color(0f, 0f, 0f, 0f));
                foreach (var graphic in selectable.GetComponentsInChildren<Graphic>(true))
                    if (graphic != background && (graphic is TMP_Text || graphic is Image)) graphic.color = p.Text;
                return;
            }

            bool primary = selectable.name == "Capture" || selectable.name == "Send Server" || selectable.name == "Confirm" ||
                           selectable.name.StartsWith("ConfirmButton");
            if (selectable is Toggle toggle)
            {
                var content = selectable.GetComponentsInChildren<Graphic>(true)
                    .Where(g => g != background && (g is TMP_Text || g is Image)).ToArray();
                var visual = selectable.GetComponent<UiToggleVisual>() ?? selectable.gameObject.AddComponent<UiToggleVisual>();
                visual.Toggle = toggle;
                visual.Background = background;
                visual.Knob = null;
                visual.Content = content;
                visual.Apply(toggle.isOn);
            }
            else
            {
                selectable.colors = primary
                    ? ControlPanelKit.Block(p.Accent, p.AccentHover, p.AccentPressed, p.SurfaceDisabled)
                    : ControlPanelKit.Block(p.Surface, p.SurfaceHover, p.SurfacePressed, p.SurfaceDisabled);
                var onColor = primary ? p.OnAccent : p.Text;
                foreach (var graphic in selectable.GetComponentsInChildren<Graphic>(true))
                    if (graphic != background && (graphic is TMP_Text || graphic is Image)) graphic.color = onColor;
            }
        }

        private static void SkinSlider(Slider slider, HashSet<Graphic> skinned)
        {
            var p = UiTheme.Palette;
            var animator = slider.GetComponent<Animator>();
            if (animator != null) animator.enabled = false;
            slider.transition = Selectable.Transition.ColorTint;
            foreach (var image in slider.GetComponentsInChildren<Image>(true))
            {
                skinned.Add(image);
                Disable(image);
                image.material = null;
                image.type = Image.Type.Sliced;
                if (image.name == "Background")
                {
                    image.sprite = UiSpriteAssets.RoundedRect(6f);
                    image.color = p.Track;
                }
                else if (image.name == "Fill")
                {
                    image.sprite = UiSpriteAssets.RoundedRect(6f);
                    image.color = p.Accent;
                }
                else if (image.name == "Handle")
                {
                    image.sprite = UiSpriteAssets.RoundedRect(14f);
                    image.color = Color.white;
                    ((RectTransform)image.transform).sizeDelta = new Vector2(28f, 28f);
                    slider.targetGraphic = image;
                }
            }
            slider.colors = ControlPanelKit.Block(p.Knob, p.Knob, p.Accent, p.SurfaceDisabled);
        }

        private static void SkinScrollbar(Scrollbar bar, HashSet<Graphic> skinned)
        {
            var p = UiTheme.Palette;
            foreach (var image in bar.GetComponentsInChildren<Image>(true))
            {
                skinned.Add(image);
                image.sprite = UiSpriteAssets.RoundedRect(10f);
                image.type = Image.Type.Sliced;
                image.color = image.name == "Handle" ? p.Track : p.SurfaceDisabled;
            }
            bar.transition = Selectable.Transition.ColorTint;
            bar.colors = ControlPanelKit.Block(Color.white, Color.white, Color.white, Color.white);
        }

        // ---- text -------------------------------------------------------------------------------------------------

        private static readonly string[] MutedNames = { "Subtitle", "SubTitle (4)", "TextLeft", "TextRight" };

        private static void SkinText(TMP_Text text, UiReading reading)
        {
            var p = UiTheme.Palette;
            bool title = text.name == "Title" || text.name == "Header";
            bool muted = MutedNames.Any(n => text.name.StartsWith(n));
            UiFonts.Apply(text, title || !muted);
            text.fontStyle = FontStyles.Normal;
            text.color = muted ? p.TextMuted : p.Text;
            float scale = text.transform.lossyScale.x;
            if (scale <= 0f) return;
            // The type scale in dmm, converted on the prefab's surface (UiMetrics): the em in millimetres at its reading distance.
            UiSurface surface = SurfaceFor(reading);
            float mm = surface.Millimetres(title ? UiTheme.FontTitle : muted ? UiTheme.FontCaption : UiTheme.FontBody);
            text.enableAutoSizing = false;
            text.fontSize = Mathf.Ceil(UiTheme.UnitsForMm(mm, scale));
        }
    }
}
#endif
