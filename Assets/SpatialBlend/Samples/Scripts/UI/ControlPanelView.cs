// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Re-applies the interactable look (text and fill colours) of a control. Unity's ColorBlock only tints the
    /// target graphic, so labels, slider fills and knobs register here and follow <see cref="Selectable.IsInteractable"/>.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class UiInteractableTint : MonoBehaviour
    {
        private sealed class Entry { public Graphic Graphic; public Color On; public Color Off; }

        private readonly List<Entry> _entries = new();
        private Selectable _selectable;
        private int _applied = -1;

        public static UiInteractableTint For(Selectable selectable)
        {
            var tint = selectable.GetComponent<UiInteractableTint>();
            if (tint == null) tint = selectable.gameObject.AddComponent<UiInteractableTint>();
            tint._selectable = selectable;
            return tint;
        }

        public void Track(Graphic graphic, Color on, Color off)
        {
            _entries.Add(new Entry { Graphic = graphic, On = on, Off = off });
            _applied = -1;
            Refresh();
        }

        public void Refresh()
        {
            if (_selectable == null) return;
            int state = _selectable.IsInteractable() ? 1 : 0;
            if (state == _applied) return;
            _applied = state;
            foreach (var entry in _entries)
                if (entry.Graphic != null) entry.Graphic.color = state == 1 ? entry.On : entry.Off;
        }

        private void LateUpdate() => Refresh();
    }

    /// <summary>Shows an outline while the control has selection focus (keyboard, d-pad or ray focus).</summary>
    [AddComponentMenu("")]
    public sealed class UiFocusRing : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        public Graphic Ring;
        public void OnSelect(BaseEventData eventData) { if (Ring != null) Ring.enabled = true; }
        public void OnDeselect(BaseEventData eventData) { if (Ring != null) Ring.enabled = false; }
    }

    /// <summary>
    /// Reusable builders for the world-space panel. Everything is built from code so no hand-placed prefab
    /// hierarchy has to stay in sync with it. All values come from <see cref="UiTheme"/>. Sizes are in canvas
    /// units; the panel canvas is 0.583 mm per unit (UiTheme.MetresPerUnit), so an 84 unit row is a 4.9 cm hit target.
    /// </summary>
    public static class ControlPanelKit
    {
        public const float RowHeight = UiTheme.RowHeight;
        public const float SliderTrackHeight = UiTheme.SliderTrackHeight;
        public const float StatusRowHeight = UiTheme.StatusRowHeight;
        public const float Spacing = UiTheme.Space3;
        /// <summary>One text size for every label, status and button; captions and tabs are one step smaller.</summary>
        public const int BodyFontSize = UiTheme.FontBody;
        public const int SmallFontSize = UiTheme.FontCaption;
        public const float KnobInset = 6f;

        public static Color ToneColor(StatusTone tone) => UiTheme.ToneColor(tone);

        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>A vertical stack that reports its height to the parent layout.</summary>
        public static RectTransform Stack(string name, Transform parent, float spacing = Spacing)
        {
            var rect = NewRect(name, parent);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        public static TMP_Text Label(Transform parent, string text, int fontSize = BodyFontSize,
                                     TextAlignmentOptions align = TextAlignmentOptions.Left, Color? color = null,
                                     bool semiBold = false, bool wrap = false)
        {
            var rect = NewRect("Label", parent);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(label, semiBold);
            label.fontSize = fontSize;
            label.alignment = align;
            label.color = color ?? UiTheme.Palette.Text;
            label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            label.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            label.text = text;
            return label;
        }

        /// <summary>
        /// Gives a host rect its look as separate children (so a shadow can sit behind the fill): soft shadow, rounded
        /// fill and hairline ring, all ignored by the host's layout group. The fill is the returned Image.
        /// </summary>
        public static Image AddSurface(RectTransform host, float radius, Color fill, int elevation, bool ring,
                                       Color? ringColor = null, bool gradient = false)
        {
            var p = UiTheme.Palette;
            float shadowAlpha = elevation >= 2 ? 0.6f : 0f;
            if (elevation > 0 && shadowAlpha > 0.001f)
            {
                var (blur, dy) = UiTheme.Elevation[Mathf.Clamp(elevation, 0, UiTheme.Elevation.Length - 1)];
                var shadow = NewRect("Shadow", host);
                Ignore(shadow);
                shadow.anchorMin = Vector2.zero;
                shadow.anchorMax = Vector2.one;
                shadow.offsetMin = new Vector2(-blur, -blur - dy);
                shadow.offsetMax = new Vector2(blur, blur - dy);
                var image = shadow.gameObject.AddComponent<Image>();
                image.color = new Color(p.Shadow.r, p.Shadow.g, p.Shadow.b, shadowAlpha);
                image.raycastTarget = false;
                UiSprites.Apply(image, UiSprites.Shadow(radius, blur));
            }

            var fillRect = NewRect("Fill", host);
            Ignore(fillRect);
            Stretch(fillRect, 0f);
            var fillImage = fillRect.gameObject.AddComponent<Image>();
            fillImage.color = gradient ? Color.white : fill;
            UiSprites.Apply(fillImage, UiSprites.RoundedRect(radius));
            if (gradient)
            {
                var g = fillRect.gameObject.AddComponent<UiVerticalGradient>();
                g.Top = p.PanelTop;
                g.Bottom = p.PanelBottom;
            }

            if (ring && p.OutlineAlpha > 0f)
            {
                var ringRect = NewRect("Ring", host);
                Ignore(ringRect);
                Stretch(ringRect, 0f);
                var ringImage = ringRect.gameObject.AddComponent<Image>();
                var c = ringColor ?? p.Outline;
                ringImage.color = new Color(c.r, c.g, c.b, c.a * p.OutlineAlpha);
                ringImage.raycastTarget = false;
                UiSprites.Apply(ringImage, UiSprites.Ring(radius, UiTheme.HairlineWidth));
            }
            return fillImage;
        }

        /// <summary>
        /// The one panel style (UiTheme): the PanelTop to PanelBottom gradient, the hairline, rounded corners. Every floating panel,
        /// card, prompt, tag and toast is drawn with it; the fill is returned. <paramref name="opaque"/> draws the same gradient at full
        /// alpha, for a panel that can sit over another one (the head-locked caption and countdown): 97% would let the one behind show through.
        /// </summary>
        public static Image AddPanel(RectTransform host, float radius, int elevation = 0, bool opaque = false)
        {
            var fill = AddSurface(host, radius, UiTheme.Palette.PanelBottom, elevation, true, null, true);
            if (opaque)
            {
                var p = UiTheme.Palette;
                fill.GetComponent<UiVerticalGradient>().Set(new Color(p.PanelTop.r, p.PanelTop.g, p.PanelTop.b, 1f),
                    new Color(p.PanelBottom.r, p.PanelBottom.g, p.PanelBottom.b, 1f));
            }
            return fill;
        }

        /// <summary>
        /// The one button style's body on <paramref name="host"/> (a pill of its height): the primary or secondary normal colour, with the
        /// hairline on a secondary button. Returns the fill, which the caller's button tints through <see cref="UiTheme.Button"/>'s states.
        /// </summary>
        public static Image AddButtonFill(RectTransform host, float height, bool primary)
        {
            var fill = AddSurface(host, UiTheme.Pill(height), UiTheme.Button(primary).Normal, 0, !primary);
            fill.raycastTarget = false;
            return fill;
        }

        private static void Ignore(RectTransform rect) => rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        /// <summary>Focus outline for a Selectable, hidden until it is selected.</summary>
        public static void AddFocusRing(RectTransform host, float radius)
        {
            var rect = NewRect("Focus", host);
            Ignore(rect);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-4f, -4f);
            rect.offsetMax = new Vector2(4f, 4f);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = UiTheme.Palette.Accent;
            image.raycastTarget = false;
            UiSprites.Apply(image, UiSprites.Ring(radius + 4f, 3f));
            image.enabled = false;
            host.gameObject.AddComponent<UiFocusRing>().Ring = image;
        }

        public static ColorBlock Block(Color normal, Color hover, Color pressed, Color disabled)
        {
            return new ColorBlock
            {
                normalColor = normal,
                highlightedColor = hover,
                pressedColor = pressed,
                selectedColor = normal,
                disabledColor = disabled,
                colorMultiplier = 1f,
                fadeDuration = 0.08f,
            };
        }

        public static Button AddButtonRow(Transform parent, string label, Action onClick, bool primary = true)
        {
            var rect = NewRect(label, parent);
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = RowHeight;
            layout.preferredHeight = RowHeight;
            var fill = AddSurface(rect, RowHeight * 0.5f, Color.white, 1, !primary);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = fill;
            var c = UiTheme.Button(primary);
            button.colors = Block(c.Normal, c.Hover, c.Pressed, c.Disabled);
            AddFocusRing(rect, RowHeight * 0.5f);
            var text = Label(rect, label, UiTheme.FontLabel, TextAlignmentOptions.Center, c.Label, true);
            Stretch(text.rectTransform, 12f);
            UiInteractableTint.For(button).Track(text, c.Label, c.LabelDisabled);
            if (onClick != null) button.onClick.AddListener(() => onClick());
            return button;
        }

        public static void SetButtonLabel(Button button, string value)
        {
            var text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null) text.text = value;
        }

        public static ToggleRow AddToggleRow(Transform parent, string label, bool initial, Action<bool> onChanged)
        {
            var p = UiTheme.Palette;
            var rect = NewRect(label, parent);
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = RowHeight;
            layout.preferredHeight = RowHeight;
            var background = AddSurface(rect, UiTheme.RadiusCard, Color.white, 1, true);
            var toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = background;
            toggle.transition = Selectable.Transition.ColorTint;
            toggle.colors = Block(p.Surface, p.SurfaceHover, p.SurfacePressed, p.SurfaceDisabled);
            AddFocusRing(rect, UiTheme.RadiusCard);

            var text = Label(rect, label, BodyFontSize, TextAlignmentOptions.Left, p.Text, true, true);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(UiTheme.Space5, 0f);
            text.rectTransform.offsetMax = new Vector2(-124f, 0f);

            var track = NewRect("Switch", rect);
            track.anchorMin = track.anchorMax = new Vector2(1f, 0.5f);
            track.pivot = new Vector2(1f, 0.5f);
            track.anchoredPosition = new Vector2(-UiTheme.Space5, 0f);
            track.sizeDelta = new Vector2(88f, 48f);
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = p.Track;
            trackImage.raycastTarget = false;
            UiSprites.Apply(trackImage, UiSprites.RoundedRect(24f));
            // The accent fill is the toggle graphic, so it shows only while the switch is on.
            var mark = NewRect("Checkmark", track);
            Stretch(mark, 0f);
            var markImage = mark.gameObject.AddComponent<Image>();
            markImage.color = p.Accent;
            markImage.raycastTarget = false;
            UiSprites.Apply(markImage, UiSprites.RoundedRect(24f));
            toggle.graphic = markImage;
            var knob = NewRect("Knob", track);
            knob.sizeDelta = new Vector2(36f, 36f);
            var knobImage = knob.gameObject.AddComponent<Image>();
            knobImage.color = p.Knob;
            knobImage.raycastTarget = false;
            UiSprites.Apply(knobImage, UiSprites.RoundedRect(18f));
            toggle.SetIsOnWithoutNotify(initial);
            var tint = UiInteractableTint.For(toggle);
            tint.Track(text, p.Text, p.TextDisabled);
            tint.Track(markImage, p.Accent, p.TextDisabled);
            var row = new ToggleRow { Toggle = toggle, Label = text, Knob = knob, Tint = tint };
            row.SyncGraphic();
            row.PlaceKnob();
            toggle.onValueChanged.AddListener(value =>
            {
                row.PlaceKnob();
                onChanged?.Invoke(value);
            });
            return row;
        }

        public static SliderRow AddSliderRow(Transform parent, string label, float min, float max, bool whole,
                                          float initial, Func<float, string> format, Action<float> onChanged)
        {
            var p = UiTheme.Palette;
            var rect = Stack(label, parent, 0f);
            AddSurface(rect, UiTheme.RadiusCard, p.Surface, 1, true);
            var padding = rect.GetComponent<VerticalLayoutGroup>();
            padding.padding = new RectOffset(24, 24, 10, 14);

            var head = NewRect("Head", rect);
            var headLayout = head.gameObject.AddComponent<LayoutElement>();
            headLayout.minHeight = StatusRowHeight;
            headLayout.preferredHeight = StatusRowHeight;
            var title = Label(head, label, BodyFontSize, TextAlignmentOptions.Left, p.Text, true);
            title.rectTransform.anchorMin = Vector2.zero;
            title.rectTransform.anchorMax = new Vector2(0.62f, 1f);
            title.rectTransform.offsetMin = title.rectTransform.offsetMax = Vector2.zero;
            var value = Label(head, string.Empty, SmallFontSize, TextAlignmentOptions.Right, p.TextMuted);
            value.rectTransform.anchorMin = new Vector2(0.62f, 0f);
            value.rectTransform.anchorMax = Vector2.one;
            value.rectTransform.offsetMin = value.rectTransform.offsetMax = Vector2.zero;

            var track = NewRect("Slider", rect);
            var trackLayout = track.gameObject.AddComponent<LayoutElement>();
            trackLayout.minHeight = SliderTrackHeight;
            trackLayout.preferredHeight = SliderTrackHeight;
            var slider = BuildSlider(track, out var fillImage);
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = whole;
            slider.SetValueWithoutNotify(initial);
            var tint = UiInteractableTint.For(slider);
            tint.Track(title, p.Text, p.TextDisabled);
            tint.Track(value, p.TextMuted, p.TextDisabled);
            tint.Track(fillImage, p.Accent, p.TextDisabled);
            var row = new SliderRow { Slider = slider, Label = title, ValueText = value, Format = format, Tint = tint };
            row.RefreshValueText();
            slider.onValueChanged.AddListener(v =>
            {
                row.RefreshValueText();
                onChanged?.Invoke(v);
            });
            return row;
        }

        private static Slider BuildSlider(RectTransform track, out Image fillImage)
        {
            var p = UiTheme.Palette;
            const float barHeight = 12f;
            var bar = NewRect("Background", track);
            bar.anchorMin = new Vector2(0f, 0.5f);
            bar.anchorMax = new Vector2(1f, 0.5f);
            bar.offsetMin = new Vector2(0f, -barHeight * 0.5f);
            bar.offsetMax = new Vector2(0f, barHeight * 0.5f);
            var barImage = bar.gameObject.AddComponent<Image>();
            barImage.color = p.Track;
            barImage.raycastTarget = false;
            UiSprites.Apply(barImage, UiSprites.RoundedRect(barHeight * 0.5f));

            var fillArea = NewRect("Fill Area", track);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.offsetMin = new Vector2(22f, -barHeight * 0.5f);
            fillArea.offsetMax = new Vector2(-22f, barHeight * 0.5f);
            var fill = NewRect("Fill", fillArea);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.sizeDelta = new Vector2(22f, 0f);
            fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.color = p.Accent;
            fillImage.raycastTarget = false;
            UiSprites.Apply(fillImage, UiSprites.RoundedRect(barHeight * 0.5f));

            var handleArea = NewRect("Handle Slide Area", track);
            // Slider rewrites the handle's anchors along both axes, so the area carries the handle's height.
            handleArea.anchorMin = new Vector2(0f, 0.5f);
            handleArea.anchorMax = new Vector2(1f, 0.5f);
            handleArea.offsetMin = new Vector2(22f, -22f);
            handleArea.offsetMax = new Vector2(-22f, 22f);
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(44f, 0f);
            var handleImage = AddSurface(handle, 22f, p.Knob, 1, true);
            handleImage.raycastTarget = true;

            var slider = track.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.transition = Selectable.Transition.ColorTint;
            slider.colors = Block(p.Knob, p.Knob, p.Accent, p.SurfaceDisabled);
            slider.direction = Slider.Direction.LeftToRight;
            return slider;
        }

        public static StatusRow AddStatusRow(Transform parent, string initial, int fontSize = SmallFontSize,
                                             bool strong = false)
        {
            var p = UiTheme.Palette;
            var rect = NewRect("Status", parent);
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = StatusRowHeight;
            var group = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = UiTheme.Space3;
            group.childAlignment = TextAnchor.MiddleLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            var dotHolder = NewRect("DotHolder", rect);
            var dotLayout = dotHolder.gameObject.AddComponent<LayoutElement>();
            dotLayout.minWidth = dotLayout.preferredWidth = 16f;
            dotLayout.minHeight = dotLayout.preferredHeight = 16f;
            var dot = NewRect("Dot", dotHolder);
            Stretch(dot, 0f);
            var dotImage = dot.gameObject.AddComponent<Image>();
            dotImage.raycastTarget = false;
            UiSprites.Apply(dotImage, UiSprites.RoundedRect(8f));
            var text = Label(rect, initial, fontSize, TextAlignmentOptions.Left,
                strong ? p.Text : p.TextMuted, strong, true);
            var textLayout = text.gameObject.AddComponent<LayoutElement>();
            textLayout.flexibleWidth = 1f;
            textLayout.minHeight = StatusRowHeight;
            var row = new StatusRow { Dot = dotImage, Text = text };
            row.Set(initial, StatusTone.Neutral);
            return row;
        }

        /// <summary>A notification card: a tone bar, a title and a message. Used for errors and confirmations.</summary>
        public static RectTransform AddToast(Transform parent, StatusTone tone, string title, string message)
        {
            var p = UiTheme.Palette;
            var rect = Stack("Toast", parent, 2f);
            var pad = rect.GetComponent<VerticalLayoutGroup>();
            pad.padding = new RectOffset(34, 24, 16, 18);
            AddSurface(rect, UiTheme.RadiusCard, p.Surface, 3, true);
            var bar = NewRect("ToneBar", rect);
            Ignore(bar);
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(0f, 1f);
            bar.offsetMin = new Vector2(14f, 16f);
            bar.offsetMax = new Vector2(20f, -16f);
            var barImage = bar.gameObject.AddComponent<Image>();
            barImage.color = UiTheme.ToneColor(tone);
            barImage.raycastTarget = false;
            UiSprites.Apply(barImage, UiSprites.RoundedRect(3f));
            Label(rect, title, BodyFontSize, TextAlignmentOptions.Left, p.Text, true, true);
            if (!string.IsNullOrEmpty(message)) Label(rect, message, SmallFontSize, TextAlignmentOptions.Left, p.TextMuted, false, true);
            return rect;
        }

        /// <summary>A small pill label for anchoring a name or value to something in the room.</summary>
        public static RectTransform AddWorldLabel(Transform parent, string text, StatusTone tone = StatusTone.Neutral)
        {
            var p = UiTheme.Palette;
            var rect = NewRect("WorldLabel", parent);
            var group = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(22, 26, 10, 10);
            group.spacing = UiTheme.Space3;
            group.childAlignment = TextAnchor.MiddleCenter;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            var fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            AddSurface(rect, 26f, p.Surface, 2, true);
            if (tone != StatusTone.Neutral)
            {
                var dotHolder = NewRect("DotHolder", rect);
                var le = dotHolder.gameObject.AddComponent<LayoutElement>();
                le.minWidth = le.preferredWidth = 16f;
                le.minHeight = le.preferredHeight = 16f;
                var dot = NewRect("Dot", dotHolder);
                Stretch(dot, 0f);
                var image = dot.gameObject.AddComponent<Image>();
                image.color = UiTheme.ToneColor(tone);
                image.raycastTarget = false;
                UiSprites.Apply(image, UiSprites.RoundedRect(8f));
            }
            var label = Label(rect, text, BodyFontSize, TextAlignmentOptions.Center, p.Text, true);
            label.overflowMode = TextOverflowModes.Overflow;
            return rect;
        }

        public static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }
    }

    public sealed class ToggleRow
    {
        public Toggle Toggle;
        public TMP_Text Label;
        public RectTransform Knob;
        public UiInteractableTint Tint;

        public void Set(bool value)
        {
            Toggle.SetIsOnWithoutNotify(value);
            SyncGraphic();
            PlaceKnob();
        }

        /// <summary>Outside play mode uGUI does not update the on graphic of an unchanged toggle (a screenshot or a test); do it by hand.</summary>
        public void SyncGraphic()
        {
            if (!Application.isPlaying && Toggle.graphic != null) Toggle.graphic.canvasRenderer.SetAlpha(Toggle.isOn ? 1f : 0f);
        }

        /// <summary>The knob sits at the right end when on and the left end when off.</summary>
        public void PlaceKnob()
        {
            if (Knob == null) return;
            bool on = Toggle.isOn;
            Knob.anchorMin = Knob.anchorMax = new Vector2(on ? 1f : 0f, 0.5f);
            Knob.pivot = new Vector2(on ? 1f : 0f, 0.5f);
            Knob.anchoredPosition = new Vector2(on ? -ControlPanelKit.KnobInset : ControlPanelKit.KnobInset, 0f);
        }
    }

    public sealed class SliderRow
    {
        public Slider Slider;
        public TMP_Text Label;
        public TMP_Text ValueText;
        public Func<float, string> Format;
        public UiInteractableTint Tint;

        public void Set(float value)
        {
            Slider.SetValueWithoutNotify(value);
            RefreshValueText();
        }

        public void RefreshValueText()
        {
            ValueText.text = Format != null ? Format(Slider.value) : Slider.value.ToString("0.0");
        }
    }

    public sealed class StatusRow
    {
        public Image Dot;
        public TMP_Text Text;

        public void Set(string value, StatusTone tone)
        {
            Text.text = value;
            Dot.color = ControlPanelKit.ToneColor(tone);
        }
    }

    /// <summary>Callbacks the generated panel needs from its owner.</summary>
    public sealed class ControlPanelActions
    {
        public Action Connect;
        /// <summary>The tutorial. The button has its place under the primary action and shows when this is set.</summary>
        public Action Tutorial;
        /// <summary>Leaves the room. The "Leave room" button shows while in a room.</summary>
        public Action Leave;
        /// <summary>Joins as an observer (desktop spectator). Wired by the spectator branch; the button shows only on desktop and only when this is set.</summary>
        public Action JoinAsObserver;
        public Action<bool> SetAnchorsVisible;
        public Action<bool> SetManipulation;
        public Action<float> SetPassthroughBrightness;
        public Func<bool> RebuildNavMesh;
    }

    /// <summary>
    /// The generated control panel, one page in two columns: the room (status, the primary action, the tutorial slot)
    /// on the left and the settings on the right. No developer information in the default
    /// panel (the Diagnostics block exists only when built with <c>diagnostics</c>), and one wide page instead of tabs.
    /// It holds no logic of its own; controls call presenters or <see cref="ControlPanelActions"/> and presenter changes
    /// flow back through the Sync methods.
    /// </summary>
    public sealed class ControlPanelView
    {
        public const string RootName = "GeneratedControlPanel";
        public const float MinHeight = 660f;

        private bool _hasTutorial;
        private bool _hasObserver;

        /// <summary>Desktop players and the Editor can join as an observer; the Quest (Android) build cannot.</summary>
        public static bool ObserverPlatform(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxEditor:
                    return true;
                default:
                    return false;
            }
        }

        public RectTransform Root { get; private set; }

        // Room
        public StatusRow Headline { get; private set; }
        public TMP_Text Detail { get; private set; }
        public Button Connect { get; private set; }
        /// <summary>"Leave room", an outline button that takes the primary action's place while in a room.</summary>
        public Button LeaveRoom { get; private set; }
        /// <summary>"Join as observer", lobby only, desktop players and the Editor only.</summary>
        public Button JoinAsObserver { get; private set; }
        public TMP_Text Title { get; private set; }
        /// <summary>The tutorial button. Hidden until <see cref="ControlPanelActions.Tutorial"/> is set.</summary>
        public Button Tutorial { get; private set; }
        public Image BusyBar { get; private set; }

        /// <summary>
        /// What is off in this build (a missing required ID, <see cref="CredentialNoticePresenter"/>) and the scan server's status, one line each under
        /// the title. Hidden while there is nothing to say.
        /// </summary>
        public RectTransform NoticesBlock { get; private set; }

        // Settings
        public ToggleRow UsePreset { get; private set; }
        public SliderRow Preset { get; private set; }
        /// <summary>The preset picker (toggle, slider and the mirror): shown in the lobby, gone once a room is joined.</summary>
        public RectTransform PresetSection { get; private set; }
        public AvatarMirror Mirror { get; private set; }
        /// <summary>The lobby preview avatar behind the mirror; it exists only while the picker is open.</summary>
        public AvatarPreview Preview { get; private set; }
        private int _presetIndex;
        /// <summary>The mirror's card, beside the panel.</summary>
        public RectTransform MirrorCard => Mirror != null ? (RectTransform)Mirror.transform : null;
        /// <summary>True while the preset picker is on screen.</summary>
        public bool PresetPickerOpen => PresetSection != null && PresetSection.gameObject.activeSelf;
        public ToggleRow Anchors { get; private set; }
        public SliderRow Passthrough { get; private set; }

        // Diagnostics (null unless built with diagnostics)
        public RectTransform DiagnosticsBlock { get; private set; }
        public StatusRow LinkState { get; private set; }
        public StatusRow Carrier { get; private set; }
        public StatusRow Library { get; private set; }
        public Button RebuildNavMesh { get; private set; }
        public TMP_Text NavMeshNote { get; private set; }

        public static ControlPanelView Build(RectTransform parent, float width, ControlPanelActions actions,
                                             AvatarSettingsPresenter avatars, SceneControlsPresenter scene,
                                             bool diagnostics = false, RuntimePlatform? platform = null)
        {
            var p = UiTheme.Palette;
            var view = new ControlPanelView();
            // The host canvas used to paint its own (old theme) background behind the panel; the panel now draws its
            // own surface, so the host only stays as the raycast area.
            if (parent.TryGetComponent<Image>(out var host)) host.color = Color.clear;
            view.Root = ControlPanelKit.Stack(RootName, parent, UiTheme.Space3);
            var root = view.Root;
            root.gameObject.layer = parent.gameObject.layer;
            root.sizeDelta = new Vector2(width, 0f);
            var layout = root.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 20, 20);
            // Tall enough for the room list overlay (three rooms, New room, Back), which must stay inside the input surface.
            root.gameObject.AddComponent<LayoutElement>().minHeight = MinHeight;
            // The host canvas is exactly the panel, so the input surface sized to the canvas is the root rect.
            if (parent.TryGetComponent<VerticalLayoutGroup>(out var hostLayout)) hostLayout.padding = new RectOffset(0, 0, 0, 0);
            // The prefab's host canvas fits to its MINIMUM size, which the root's minimum height pins at MinHeight; the panel must be able
            // to grow past it (the Diagnostics block of a development build), so the canvas fits to the PREFERRED size.
            if (parent.TryGetComponent<ContentSizeFitter>(out var hostFitter)) hostFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ControlPanelKit.AddSurface(root, UiTheme.RadiusPanel, p.PanelBottom, 2, true, null, true);

            view.Title = ControlPanelKit.Label(root, UiTheme.ProductTitle, UiTheme.FontTitle, TextAlignmentOptions.Left, p.Text, true);
            view.Title.gameObject.AddComponent<LayoutElement>().minHeight = 48f;
            view.NoticesBlock = ControlPanelKit.Stack("Notices", root, UiTheme.Space2);
            view.NoticesBlock.gameObject.SetActive(false);
            var columns = ControlPanelKit.NewRect("Columns", root);
            var group = columns.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = UiTheme.Space4;
            group.childAlignment = TextAnchor.UpperCenter;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            view.BuildRoomColumn(columns, actions);
            view.BuildSettingsColumn(columns, avatars, scene);
            if (diagnostics) view.BuildDiagnostics(root, scene);
            view.SyncAvatars(avatars);
            view.SyncScene(scene);
            view._hasTutorial = actions.Tutorial != null;
            view._hasObserver = actions.JoinAsObserver != null && ObserverPlatform(platform ?? Application.platform);
            view.ApplySession(new SessionStatusView { Headline = "Not in a room", ConnectLabel = "Find a room", ConnectEnabled = true, TutorialVisible = true });
            SetLayerRecursive(root.gameObject, parent.gameObject.layer);
            return view;
        }

        /// <summary>Replaces the notices block's lines. A Bad line is strong; the block is hidden when the list is empty.</summary>
        public void SetNotices(IReadOnlyList<CredentialNotice> notices)
        {
            if (NoticesBlock == null) return;
            for (int i = NoticesBlock.childCount - 1; i >= 0; i--)
            {
                var child = NoticesBlock.GetChild(i).gameObject;
                if (Application.isPlaying) UnityEngine.Object.Destroy(child); else UnityEngine.Object.DestroyImmediate(child);
            }
            foreach (var notice in notices)
            {
                var row = ControlPanelKit.AddStatusRow(NoticesBlock, notice.Title, ControlPanelKit.SmallFontSize, notice.Tone == StatusTone.Bad);
                row.Set(notice.Title, notice.Tone);
                row.Text.textWrappingMode = TextWrappingModes.Normal;
                row.Text.overflowMode = TextOverflowModes.Overflow;
                SetLayerRecursive(row.Text.transform.parent.gameObject, Root.gameObject.layer);
            }
            NoticesBlock.gameObject.SetActive(notices.Count > 0);
        }

        public void ApplySession(SessionStatusView status)
        {
            Headline.Set(status.Headline, status.Tone);
            Detail.text = status.Detail ?? string.Empty;
            Detail.gameObject.SetActive(!string.IsNullOrEmpty(status.Detail));
            ControlPanelKit.SetButtonLabel(Connect, status.ConnectLabel);
            Connect.interactable = status.ConnectEnabled;
            Connect.gameObject.SetActive(status.ConnectVisible || status.ConnectEnabled);
            LeaveRoom.gameObject.SetActive(status.LeaveVisible);
            // The tutorial runs from the lobby before connecting: only there, and only once it has an action.
            // The avatar is chosen in the lobby; once a room is joined the picker (and its mirror) is gone.
            if (PresetSection != null)
            {
                PresetSection.gameObject.SetActive(!status.LeaveVisible);
                if (Mirror != null)
                {
                    Mirror.gameObject.SetActive(PresetPickerOpen);
                    Preview.SetOpen(PresetPickerOpen, _presetIndex);   // made when the picker opens, destroyed when it closes
                    Mirror.SetPickerOpen(PresetPickerOpen);
                }
            }
            Tutorial.gameObject.SetActive(_hasTutorial && status.TutorialVisible);
            JoinAsObserver.gameObject.SetActive(_hasObserver && status.TutorialVisible);
            UiInteractableTint.For(Connect).Refresh();
            BusyBar.gameObject.SetActive(status.Busy);
            if (LinkState != null)
            {
                LinkState.Set(status.LinkStateText, status.Tone);
                Carrier.Set(status.CarrierText, status.CarrierText != null && status.CarrierText.Contains("fallback") ? StatusTone.Busy : StatusTone.Neutral);
                Carrier.Text.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(status.CarrierText));
                Library.Set(status.LibraryText, status.LibraryLoaded ? StatusTone.Good : StatusTone.Bad);
            }
        }

        /// <summary>Sweeps the busy bar; call from Update while a session step is in progress.</summary>
        public void TickBusy(float time)
        {
            if (BusyBar != null && BusyBar.gameObject.activeSelf)
                BusyBar.fillAmount = Mathf.PingPong(time * 0.8f, 1f);
        }

        public void SyncAvatars(AvatarSettingsPresenter avatars)
        {
            UsePreset.Set(avatars.UsePreset);
            Preset.Slider.interactable = avatars.UsePreset;
            Preset.Set(avatars.PresetIndex);
            Preset.Tint?.Refresh();
            _presetIndex = avatars.PresetIndex;
            if (Preview != null) Preview.SetPreset(_presetIndex);
        }

        public void SyncScene(SceneControlsPresenter scene)
        {
            Anchors.Set(scene.AnchorsVisible);
            Passthrough.Set(scene.Brightness);
        }

        /// <summary>Diagnostics only: the reason the NavMesh could not be rebuilt.</summary>
        public void SetNavMeshNote(string value)
        {
            if (NavMeshNote != null) NavMeshNote.text = value;
        }

        private RectTransform NewColumn(RectTransform columns, string name, string header)
        {
            var column = ControlPanelKit.Stack(name, columns, UiTheme.Space3);
            var share = column.gameObject.AddComponent<LayoutElement>();
            share.minWidth = share.preferredWidth = 300f;   // equal columns: they share what is left equally
            share.flexibleWidth = 1f;
            ControlPanelKit.Label(column, header, UiTheme.FontCaption, TextAlignmentOptions.Left, UiTheme.Palette.TextMuted, true)
                .gameObject.AddComponent<LayoutElement>().minHeight = 30f;
            return column;
        }

        private void BuildRoomColumn(RectTransform columns, ControlPanelActions actions)
        {
            var p = UiTheme.Palette;
            var column = NewColumn(columns, "Room", "Room");
            var card = ControlPanelKit.Stack("StatusCard", column, 2f);
            card.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 16, 18);
            ControlPanelKit.AddSurface(card, UiTheme.RadiusCard, p.Surface, 1, true);
            Headline = ControlPanelKit.AddStatusRow(card, "Not in a room", ControlPanelKit.BodyFontSize, true);
            Detail = ControlPanelKit.Label(card, string.Empty, ControlPanelKit.SmallFontSize, TextAlignmentOptions.Left,
                p.TextMuted, false, true);
            var barRect = ControlPanelKit.NewRect("BusyBar", card);
            var barLayout = barRect.gameObject.AddComponent<LayoutElement>();
            barLayout.minHeight = 6f;
            barLayout.preferredHeight = 6f;
            BusyBar = barRect.gameObject.AddComponent<Image>();
            BusyBar.color = p.Accent;
            BusyBar.type = Image.Type.Filled;
            BusyBar.fillMethod = Image.FillMethod.Horizontal;
            BusyBar.raycastTarget = false;
            BusyBar.gameObject.SetActive(false);
            Connect = ControlPanelKit.AddButtonRow(column, "Find a room", actions.Connect);
            LeaveRoom = ControlPanelKit.AddButtonRow(column, "Leave room", actions.Leave, false);
            LeaveRoom.gameObject.SetActive(false);
            // The tutorial has its place right under the primary action; it shows once the tutorial is wired.
            Tutorial = ControlPanelKit.AddButtonRow(column, "Tutorial", actions.Tutorial, false);
            BuildTutorialHint();
            JoinAsObserver = ControlPanelKit.AddButtonRow(column, "Join as observer", actions.JoinAsObserver, false);
            JoinAsObserver.gameObject.SetActive(false);
        }

        /// <summary>A small accent dot at the Tutorial button's corner: "start here". Hidden once the tutorial was done (see <see cref="SetTutorialHint"/>).</summary>
        public Image TutorialHint { get; private set; }

        private void BuildTutorialHint()
        {
            var dot = ControlPanelKit.NewRect("HintDot", Tutorial.transform);
            dot.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            dot.anchorMin = dot.anchorMax = new Vector2(1f, 1f);
            dot.pivot = new Vector2(0.5f, 0.5f);
            dot.anchoredPosition = new Vector2(-30f, -4f);
            dot.sizeDelta = new Vector2(20f, 20f);
            TutorialHint = dot.gameObject.AddComponent<Image>();
            TutorialHint.color = UiTheme.Palette.Accent;
            TutorialHint.raycastTarget = false;
            UiSprites.Apply(TutorialHint, UiSprites.RoundedRect(10f));
        }

        /// <summary>Shows or hides the hint dot on the Tutorial button.</summary>
        public void SetTutorialHint(bool visible)
        {
            if (TutorialHint != null) TutorialHint.gameObject.SetActive(visible);
        }

        private void BuildSettingsColumn(RectTransform columns, AvatarSettingsPresenter avatars, SceneControlsPresenter scene)
        {
            var column = NewColumn(columns, "Settings", "Settings");
            // The preset picker is one section so it can leave as a whole once a room is joined (the avatar is chosen at spawn).
            PresetSection = ControlPanelKit.Stack("AvatarPresets", column, UiTheme.Space3);
            UsePreset = ControlPanelKit.AddToggleRow(PresetSection, "Preset avatar", avatars.UsePreset, avatars.SetUsePreset);
            Preset = ControlPanelKit.AddSliderRow(PresetSection, "Preset", 0f, AvatarSettingsPresenter.PresetCount - 1,
                true, avatars.PresetIndex, value => $"{Mathf.RoundToInt(value) + 1} of {AvatarSettingsPresenter.PresetCount}",
                avatars.SetPresetIndex);
            BuildMirror(Root);
            // Zone-based display and its fade duration are not offered (the demo runs the Default
            // display mode). The framework feature and the presenter setters stay.
            Anchors = ControlPanelKit.AddToggleRow(column, "Show anchors", scene.AnchorsVisible, scene.SetAnchorsVisible);
            // No mode switch row: the interaction mode chip replaces it.
            Passthrough = ControlPanelKit.AddSliderRow(column, "Passthrough",
                SceneControlsPresenter.MinBrightness, SceneControlsPresenter.MaxBrightness, false, scene.Brightness,
                value => $"{Mathf.RoundToInt((value - SceneControlsPresenter.MinBrightness) / (SceneControlsPresenter.MaxBrightness - SceneControlsPresenter.MinBrightness) * 100f)}%", scene.SetBrightness);
        }

        private void BuildMirror(RectTransform parent)
        {
            var p = UiTheme.Palette;
            // Beside the panel, not in it: the panel keeps its height. It leaves the panel's layout and hangs off its top right corner.
            var card = ControlPanelKit.Stack("AvatarMirror", parent, 0f);
            card.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            card.anchorMin = card.anchorMax = new Vector2(1f, 1f);
            card.pivot = new Vector2(0f, 1f);
            card.anchoredPosition = new Vector2(UiTheme.Space4, 0f);
            card.sizeDelta = new Vector2(MirrorHeight + 24f, MirrorHeight + 24f);
            card.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(12, 12, 12, 12);
            ControlPanelKit.AddSurface(card, UiTheme.RadiusCard, p.Surface, 1, true);
            var frame = ControlPanelKit.NewRect("Mirror", card);
            var size = frame.gameObject.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = MirrorHeight;
            var image = frame.gameObject.AddComponent<RawImage>();
            image.color = Color.white;
            image.raycastTarget = false;
            image.enabled = false;
            var hint = ControlPanelKit.Label(frame, "Loading your avatar preview", ControlPanelKit.SmallFontSize,
                TextAlignmentOptions.Center, p.TextMuted, false, true);
            ControlPanelKit.Stretch(hint.rectTransform, 12f);
            Mirror = card.gameObject.AddComponent<AvatarMirror>();
            Mirror.Image = image;
            Mirror.Placeholder = hint.gameObject;
            Preview = card.gameObject.AddComponent<AvatarPreview>();
            Preview.Changed += Mirror.Refresh;
            Mirror.TargetResolver = () => Preview.Root;
        }

        public const float MirrorHeight = 300f;

        private void BuildDiagnostics(RectTransform root, SceneControlsPresenter scene)
        {
            var p = UiTheme.Palette;
            var block = ControlPanelKit.Stack("Diagnostics", root, 2f);
            DiagnosticsBlock = block;
            block.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 14, 16);
            ControlPanelKit.AddSurface(block, UiTheme.RadiusCard, p.SurfaceDisabled, 0, true);
            ControlPanelKit.Label(block, "Diagnostics", UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted, true)
                .gameObject.AddComponent<LayoutElement>().minHeight = 30f;
            LinkState = ControlPanelKit.AddStatusRow(block, "Core link: disconnected");
            Carrier = ControlPanelKit.AddStatusRow(block, string.Empty);
            Library = ControlPanelKit.AddStatusRow(block, "Core library: missing or incompatible");
            RebuildNavMesh = ControlPanelKit.AddButtonRow(block, "Rebuild NavMesh", scene.RebuildNavMesh, false);
            NavMeshNote = ControlPanelKit.Label(block, string.Empty, ControlPanelKit.SmallFontSize, TextAlignmentOptions.Left,
                p.TextMuted, false, true);
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
