// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// The scan panel's own surfaces, built in code from the B Graphite kit so they match the control panel and the pop-ups: the
    /// frame around the camera view, the camera message card (with "Allow camera"), the hint pill and the toast. Sizes are theme
    /// units scaled to this canvas (<see cref="ScanPanelStyle.MinHitUnits"/>); text uses the room reading sizes.
    /// </summary>
    public sealed class ScanPanelOverlays
    {
        private readonly float _scale;   // metres per canvas unit of the panel
        private readonly float _k;       // panel units per theme unit
        private readonly RectTransform _imageRect;
        private RectTransform _card, _hintPill, _toast;
        private TMP_Text _title, _detail, _hint;
        private Image _bar;
        private float _toastUntil;

        public Button AllowButton { get; private set; }
        public bool MessageVisible => _card != null && _card.gameObject.activeSelf;
        public string MessageTitle => _title != null ? _title.text : string.Empty;
        public string HintText => _hint != null ? _hint.text : string.Empty;
        public bool ToastVisible => _toast != null;
        public RectTransform Card => _card;

        private ScanPanelOverlays(RawImage image)
        {
            _imageRect = image.rectTransform;
            _scale = Mathf.Max(1e-5f, image.transform.lossyScale.x);
            _k = UiTheme.MetresPerUnit / _scale;
        }

        private float U(float themeUnits) => themeUnits * _k;
        private float Body => ScanPanelStyle.FontUnits(UiMetrics.Room.Millimetres(UiTheme.FontBody), _scale);
        private float Caption => ScanPanelStyle.FontUnits(UiMetrics.Room.Millimetres(UiTheme.FontCaption), _scale);

        public static ScanPanelOverlays Build(RawImage image, Action onAllow)
        {
            var o = new ScanPanelOverlays(image);
            o.BuildFrame();
            o.BuildCard(onAllow);
            o.BuildHint();
            return o;
        }

        private static void Ignore(RectTransform rect) => rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        /// <summary>
        /// A hairline ring just outside the camera view, as a child of the view so it follows whatever the panel's layout does with it.
        /// (A ring only: a child draws over its parent.)
        /// </summary>
        private void BuildFrame()
        {
            var frame = ControlPanelKit.NewRect("CameraFrame", _imageRect);
            Ignore(frame);
            frame.gameObject.layer = _imageRect.gameObject.layer;
            ControlPanelKit.Stretch(frame, -U(ScanPanelStyle.FramePad) * 0.5f);
            var image = frame.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            var c = UiTheme.Palette.Outline;
            image.color = new Color(c.r, c.g, c.b, 0.9f);
            UiSprites.Apply(image, UiSprites.Ring(U(ScanPanelStyle.FrameRadius), U(3f)));
        }

        private void BuildCard(Action onAllow)
        {
            var p = UiTheme.Palette;
            _card = ControlPanelKit.Stack("CameraMessage", _imageRect, U(UiTheme.Space4));
            _card.gameObject.layer = _imageRect.gameObject.layer;
            _card.anchorMin = new Vector2(0.14f, 0.5f);
            _card.anchorMax = new Vector2(0.86f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            _card.anchoredPosition = Vector2.zero;
            _card.sizeDelta = Vector2.zero;
            var layout = _card.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)U(40), (int)U(32), (int)U(28), (int)U(28));
            layout.childAlignment = TextAnchor.UpperCenter;
            _card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ControlPanelKit.AddSurface(_card, U(UiTheme.RadiusCard), p.Surface, 3, true);
            _bar = NewBar(_card);
            _title = ControlPanelKit.Label(_card, string.Empty, Mathf.RoundToInt(ScanPanelStyle.FontUnits(UiMetrics.Room.Millimetres(UiTheme.FontTitle), _scale)), TextAlignmentOptions.Left, p.Text, true, true);
            _detail = ControlPanelKit.Label(_card, string.Empty, Mathf.RoundToInt(Body), TextAlignmentOptions.Left, p.TextMuted, false, true);
            AllowButton = ControlPanelKit.AddButtonRow(_card, ScanPanelStyle.AllowCameraLabel, onAllow, true);
            Fit(AllowButton);
            AllowButton.gameObject.SetActive(false);
            foreach (var t in _card.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _card.gameObject.layer;
            _card.gameObject.SetActive(false);
        }

        private Image NewBar(RectTransform host)
        {
            var bar = ControlPanelKit.NewRect("ToneBar", host);
            Ignore(bar);
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(0f, 1f);
            bar.offsetMin = new Vector2(U(14f), U(20f));
            bar.offsetMax = new Vector2(U(20f), -U(20f));
            var image = bar.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            UiSprites.Apply(image, UiSprites.RoundedRect(U(3f)));
            return image;
        }

        /// <summary>Gives a button of the kit the hit target and text size of this canvas (at least the theme row, 4.9 cm).</summary>
        private void Fit(Button button)
        {
            var layout = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
            float h = ScanPanelStyle.MinHitUnits(_scale);
            layout.minHeight = h;
            layout.preferredHeight = h;
            var text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null) text.fontSize = Body;
        }

        private void BuildHint()
        {
            var p = UiTheme.Palette;
            _hintPill = ControlPanelKit.NewRect("Hint", _imageRect);
            Ignore(_hintPill);
            _hintPill.gameObject.layer = _imageRect.gameObject.layer;
            _hintPill.anchorMin = _hintPill.anchorMax = new Vector2(0.5f, 0f);
            _hintPill.pivot = new Vector2(0.5f, 0f);
            _hintPill.anchoredPosition = new Vector2(0f, U(UiTheme.Space4));
            _hintPill.sizeDelta = new Vector2(Mathf.Max(U(360f), _imageRect.rect.width * 0.84f), U(76f));
            var fill = ControlPanelKit.AddSurface(_hintPill, U(38f), new Color(p.Surface.r, p.Surface.g, p.Surface.b, 0.92f), 1, true);
            fill.raycastTarget = false;
            _hint = ControlPanelKit.Label(_hintPill, string.Empty, Mathf.RoundToInt(Caption), TextAlignmentOptions.Center, p.Text, false, false);
            ControlPanelKit.Stretch(_hint.rectTransform, U(6f));
            _hint.overflowMode = TextOverflowModes.Overflow; // Ellipsis drops a line that is taller than its rect, and the line is the whole text
            foreach (var t in _hintPill.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _hintPill.gameObject.layer;
        }

        public void SetHint(string text)
        {
            if (_hint == null) return;
            _hint.text = text;
            _hintPill.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        public void ShowMessage(ScanCameraMessage message)
        {
            if (_card == null) return;
            if (!message.Show) { _card.gameObject.SetActive(false); return; }
            _bar.color = UiTheme.ToneColor(message.Tone);
            _title.text = message.Title;
            _detail.text = message.Detail ?? string.Empty;
            _detail.gameObject.SetActive(!string.IsNullOrEmpty(message.Detail));
            AllowButton.gameObject.SetActive(message.OffersAllow);
            _card.gameObject.SetActive(true);
        }

        /// <summary>A short card above the camera view (failures and "Capture not ready").</summary>
        public void ShowToast(StatusTone tone, string title, string detail, float now)
        {
            if (_toast != null) UnityEngine.Object.DestroyImmediate(_toast.gameObject);
            _toast = ControlPanelKit.Stack("Toast", _imageRect, U(2f));
            Ignore(_toast);
            _toast.gameObject.layer = _imageRect.gameObject.layer;
            _toast.anchorMin = new Vector2(0.1f, 1f);
            _toast.anchorMax = new Vector2(0.9f, 1f);
            _toast.pivot = new Vector2(0.5f, 1f);
            _toast.sizeDelta = Vector2.zero;
            _toast.anchoredPosition = new Vector2(0f, -U(UiTheme.Space5));
            var layout = _toast.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)U(44), (int)U(28), (int)U(20), (int)U(22));
            _toast.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ControlPanelKit.AddSurface(_toast, U(UiTheme.RadiusCard), UiTheme.Palette.Surface, 3, true);
            var bar = NewBar(_toast);
            bar.color = UiTheme.ToneColor(tone);
            ControlPanelKit.Label(_toast, title, Mathf.RoundToInt(Body), TextAlignmentOptions.Left, UiTheme.Palette.Text, true, true);
            if (!string.IsNullOrEmpty(detail))
                ControlPanelKit.Label(_toast, detail, Mathf.RoundToInt(Caption), TextAlignmentOptions.Left, UiTheme.Palette.TextMuted, false, true);
            foreach (var t in _toast.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _toast.gameObject.layer;
            foreach (var g in _toast.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            _toastUntil = now + ScanPanelStyle.ToastSeconds;
        }

        public void Tick(float now)
        {
            if (_toast != null && now >= _toastUntil)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_toast.gameObject);
                else UnityEngine.Object.DestroyImmediate(_toast.gameObject);
                _toast = null;
            }
        }
    }
}
