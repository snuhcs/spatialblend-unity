// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Interactions.UI
{
    /// <summary>
    /// The colours and typeface of the mode-switch widgets and the small room widgets that share their look (the edit tag, the box undo
    /// hint, the place callout, the scan save button): the app's <see cref="UiTheme"/> palette and the <see cref="UiFonts"/> SemiBold.
    /// <see cref="Resolve"/> lets a host substitute its own; the default is the theme itself (<see cref="FromTheme"/>), so there is one
    /// palette and no second copy of it here.
    /// </summary>
    public struct ModeSwitchStyle
    {
        public Color Surface, Outline, Accent, OnAccent, Text, TextMuted, ButtonHover, ButtonPressed, AccentHover, AccentPressed, Warning;

        /// <summary>The typeface for the widgets' text (SemiBold is used for every label); null leaves the TextMesh Pro default font.</summary>
        public TMP_FontAsset SemiBold;

        /// <summary>The theme's tokens, read now (so a typeface registered late is still picked up).</summary>
        public static ModeSwitchStyle FromTheme()
        {
            UiPalette p = UiTheme.Palette;
            return new ModeSwitchStyle
            {
                Surface = p.PanelTop,
                Outline = new Color(p.Outline.r, p.Outline.g, p.Outline.b, p.OutlineAlpha),
                Accent = p.Accent, AccentHover = p.AccentHover, AccentPressed = p.AccentPressed, OnAccent = p.OnAccent,
                Text = p.Text, TextMuted = p.TextMuted,
                ButtonHover = p.SurfaceHover, ButtonPressed = p.SurfacePressed,
                Warning = p.Warning,
                SemiBold = UiFonts.SemiBold,
            };
        }

        /// <summary>The default style: the theme's.</summary>
        public static ModeSwitchStyle Graphite => FromTheme();

        /// <summary>Where a host supplies another style, read each time the widgets are built. Null means <see cref="FromTheme"/>.</summary>
        public static Func<ModeSwitchStyle> Resolve;

        /// <summary>The style the widgets are built with.</summary>
        public static ModeSwitchStyle Current => Resolve != null ? Resolve() : FromTheme();
    }

    /// <summary>
    /// A button on a world-space canvas that the scene's ray presses through the Interaction SDK (a <see cref="RayInteractable"/> on a
    /// collider the size of the button), so it works with controllers and with hands alike. A click is a press and release with the ray still on it.
    /// </summary>
    public sealed class ModeSwitchClickTarget : MonoBehaviour
    {
        private RayInteractable _interactable;
        private Image _image;
        private Color _normal, _hover, _pressed;
        private bool _subscribed;

        public event Action Clicked;
        /// <summary>The ray pressed the button (Hover to Select): the press, before <see cref="Clicked"/> on the release.</summary>
        public event Action Pressed;

        public RayInteractable Interactable => _interactable;

        /// <summary>Whether the ray is on the button (hovering or pressing it).</summary>
        public bool IsHovered { get; private set; }

        internal static ModeSwitchClickTarget Create(GameObject host, Vector2 size, Image image, Color normal, Color hover, Color pressed)
        {
            var target = host.AddComponent<ModeSwitchClickTarget>();
            var box = host.AddComponent<BoxCollider>();
            box.size = new Vector3(size.x, size.y, 6f);
            var surface = host.AddComponent<ColliderSurface>();
            surface.InjectCollider(box);
            target._interactable = host.AddComponent<RayInteractable>();
            target._interactable.InjectSurface(surface);
            target._image = image;
            target.SetColors(normal, hover, pressed);
            // AddComponent ran OnEnable before the interactable existed, so subscribe here too.
            target.Subscribe();
            return target;
        }

        internal void SetColors(Color normal, Color hover, Color pressed)
        {
            _normal = normal;
            _hover = hover;
            _pressed = pressed;
            if (_image != null) _image.color = IsHovered ? _hover : _normal;
        }

        private void Subscribe()
        {
            if (_subscribed || _interactable == null) return;
            _interactable.WhenStateChanged += OnStateChanged;
            _subscribed = true;
        }

        private void OnEnable() => Subscribe();

        private void OnDisable()
        {
            if (_subscribed) _interactable.WhenStateChanged -= OnStateChanged;
            _subscribed = false;
            IsHovered = false;
            if (_image != null) _image.color = _normal;
        }

        private void OnStateChanged(InteractableStateChangeArgs args)
        {
            IsHovered = args.NewState == InteractableState.Hover || args.NewState == InteractableState.Select;
            if (_image != null)
                _image.color = args.NewState == InteractableState.Select ? _pressed : IsHovered ? _hover : _normal;
            if (args.NewState == InteractableState.Select && args.PreviousState != InteractableState.Select)
                Pressed?.Invoke();
            if (args.PreviousState == InteractableState.Select && args.NewState == InteractableState.Hover)
                Clicked?.Invoke();
        }
    }

    /// <summary>
    /// The three widgets of <see cref="ModeSwitchAssist"/>, built from code on world-space canvases in the design system's units (one
    /// canvas unit is one dmm, <see cref="UiMetrics"/>): the "Edit this object?" prompt that floats over an object (the
    /// <see cref="UiMetrics.Room"/> surface: 1 mm per unit at 1 m, growing with distance so its text stays 24 dmm out to 4.5 m), the
    /// "Back to Interaction in N..." toast and the mode chip (the head-locked <see cref="UiMetrics.Hud"/> surface, 0.95 m). The toast and
    /// the chip sit on one head-following anchor; the prompt follows the object it is about. Panels and buttons are the theme's one panel
    /// and one button style (<see cref="ControlPanelKit.AddPanel"/>, <see cref="ControlPanelKit.AddButtonFill"/>).
    /// </summary>
    public sealed class ModeSwitchUi : MonoBehaviour
    {
        public const float HudDistanceMetres = 0.85f;
        public const float HudDropMetres = 0.30f;
        public const float ChipOffsetMetres = 0.36f;
        /// <summary>Height of the chip above the HUD anchor (which is <see cref="HudDropMetres"/> below the eye): 0.25 m above the eye.</summary>
        public const float ChipRiseMetres = 0.55f;
        /// <summary>Height of the toast above the HUD anchor: 0.28 m above the eye (11 to 23 deg up at 0.85 m), clear of the wrist panel and of the app's own toast overlay (0.2 m below the eye). The wrist panel is tall (about 0.5 m, its bottom 7 cm above the controller), so with the hand held out at the reference pose its top reaches 11 deg above the eye line; the toast's lower edge stays above that.</summary>
        public const float ToastRiseMetres = 0.58f;

        private static readonly Vector2 PromptSize = new Vector2(404f, 112f);
        private static readonly Vector2 EditButtonSize = new Vector2(120f, UiTheme.ButtonHeight);
        private static readonly Vector2 ToastSize = new Vector2(344f, 168f);
        private static readonly Vector2 KeepButtonSize = new Vector2(296f, UiTheme.ButtonHeight);
        private static readonly Vector2 ChipSize = new Vector2(230f, UiTheme.ButtonHeight);
        private static readonly Vector2 MenuPromptSize = new Vector2(330f, 96f);
        private static readonly Vector2 MenuButtonSize = new Vector2(120f, UiTheme.ButtonHeight);
        private static readonly Vector2 UndoSize = new Vector2(230f, UiTheme.ButtonHeight);
        /// <summary>Height of the hands' Undo button above the HUD anchor: just over the chip, clear of the wrist panel's top (11 degrees) and of the caption beside the chip.</summary>
        public const float UndoRiseMetres = 0.64f;
        /// <summary>The mode-change caption: beside the chip on its row, two lines of room-reading text.</summary>
        public static readonly Vector2 CaptionSize = new Vector2(560f, 112f);
        /// <summary>Gap between the chip's right edge and the caption's left edge.</summary>
        public const float CaptionGapMetres = 0.012f;
        /// <summary>Degrees above the eye line that the left-controller wrist panel reaches with the hand held out (see <see cref="ToastRiseMetres"/>); the caption's lower edge stays above it.</summary>
        public const float WristPanelTopDegrees = 11f;

        private ModeSwitchStyle _style;
        private Transform _eye;

        private GameObject _prompt;
        private ModeSwitchClickTarget _editButton;
        private Transform _promptRoot;
        private Vector3 _promptAnchorLocal;

        // The hands' tools: the Menu button over a hovered object (A's stand-in) and the Undo button by the chip (B's).
        private GameObject _menuPrompt;
        private ModeSwitchClickTarget _menuButton;
        private Transform _menuRoot;
        private Vector3 _menuAnchorLocal;
        private ModeSwitchClickTarget _undo;

        private Transform _hud;
        private GameObject _toast;
        private TextMeshProUGUI _toastLabel;
        private RectTransform _toastBar;
        private ModeSwitchClickTarget _keepButton;
        private ModeSwitchClickTarget _chip;
        private Image _chipBackground;
        private Image _chipDot;
        private TextMeshProUGUI _chipLabel;
        private GameObject _caption;
        private TextMeshProUGUI _captionLabel;
        private int _toastSeconds = -1;
        private float _hudYaw;
        private Vector3 _hudVelocity;

        public event Action EditClicked;
        public event Action KeepEditingClicked;
        public event Action ChipClicked;
        public event Action MenuClicked;
        public event Action UndoClicked;

        public bool PromptVisible => _prompt != null && _prompt.activeSelf;
        public bool ToastVisible => _toast != null && _toast.activeSelf;
        public bool MenuPromptVisible => _menuPrompt != null && _menuPrompt.activeSelf;
        public bool UndoVisible => _undo != null && _undo.gameObject.activeSelf;
        public Transform MenuPromptRoot => MenuPromptVisible ? _menuRoot : null;
        public GameObject MenuPromptObject => _menuPrompt;
        public GameObject UndoObject => _undo != null ? _undo.gameObject : null;
        public bool ChipVisible => _chip != null && _chip.gameObject.activeSelf;
        public Transform PromptRoot => PromptVisible ? _promptRoot : null;
        public string ToastText => _toastLabel != null ? _toastLabel.text : string.Empty;
        public string ChipText => _chipLabel != null ? _chipLabel.text : string.Empty;
        public bool CaptionVisible => _caption != null && _caption.activeSelf;
        public string CaptionText => _captionLabel != null ? _captionLabel.text : string.Empty;
        public GameObject CaptionObject => _caption;
        public GameObject PromptObject => _prompt;
        public GameObject ToastObject => _toast;
        public GameObject ChipObject => _chip != null ? _chip.gameObject : null;
        public Transform Hud => _hud;

        /// <summary>Whether <paramref name="interactable"/> is one of the widgets' buttons (the ray is on the prompt, the toast or the chip).</summary>
        public static bool IsWidget(RayInteractable interactable) =>
            interactable != null && interactable.GetComponent<ModeSwitchClickTarget>() != null;

        /// <summary>Whether <paramref name="interactable"/> is the prompt's own [Edit] button (not the chip or the toast).</summary>
        public bool IsPromptButton(RayInteractable interactable) =>
            interactable != null && _editButton != null && _editButton.Interactable == interactable;

        /// <summary>Whether <paramref name="interactable"/> is the Menu button over an object (hands).</summary>
        public bool IsMenuButton(RayInteractable interactable) =>
            interactable != null && _menuButton != null && _menuButton.Interactable == interactable;

        public void Build(Transform eye)
        {
            _eye = eye;
            _style = ModeSwitchStyle.Current;
            BuildHud();
            BuildPrompt();
            BuildMenuPrompt();
            HidePrompt();
            HideMenuPrompt();
            HideToast();
            HideCaption();
            SetChipVisible(false);
            SetUndoVisible(false);
        }

        // ---- public surface used by the assist ----

        public void ShowPrompt(Transform root, Bounds bounds, float gapMetres)
        {
            if (root == null) { HidePrompt(); return; }
            if (_promptRoot != root || !_prompt.activeSelf)
            {
                _promptRoot = root;
                var top = new Vector3(bounds.center.x, bounds.max.y + gapMetres, bounds.center.z);
                _promptAnchorLocal = root.InverseTransformPoint(top);
            }
            if (!_prompt.activeSelf) _prompt.SetActive(true);
            PlacePrompt();
        }

        public void HidePrompt()
        {
            _promptRoot = null;
            if (_prompt != null && _prompt.activeSelf) _prompt.SetActive(false);
        }

        /// <summary>Floats the Menu button above <paramref name="root"/> (the object whose menu it opens), as the Edit prompt floats above its object.</summary>
        public void ShowMenuPrompt(Transform root, Bounds bounds, float gapMetres)
        {
            if (root == null) { HideMenuPrompt(); return; }
            if (_menuRoot != root || !_menuPrompt.activeSelf)
            {
                _menuRoot = root;
                var top = new Vector3(bounds.center.x, bounds.max.y + gapMetres, bounds.center.z);
                _menuAnchorLocal = root.InverseTransformPoint(top);
            }
            if (!_menuPrompt.activeSelf) _menuPrompt.SetActive(true);
            PlaceAt(_menuPrompt, _menuRoot, _menuAnchorLocal);
        }

        public void HideMenuPrompt()
        {
            _menuRoot = null;
            if (_menuPrompt != null && _menuPrompt.activeSelf) _menuPrompt.SetActive(false);
        }

        public void SetUndoVisible(bool visible)
        {
            if (_undo != null && _undo.gameObject.activeSelf != visible) _undo.gameObject.SetActive(visible);
        }

        public void ShowToast(int seconds, float fractionRemaining)
        {
            if (!_toast.activeSelf) _toast.SetActive(true);
            if (seconds != _toastSeconds)
            {
                _toastSeconds = seconds;
                _toastLabel.text = "Back to Interaction in " + seconds + "…";
            }
            var anchor = _toastBar.anchorMax;
            anchor.x = Mathf.Clamp01(fractionRemaining);
            _toastBar.anchorMax = anchor;
        }

        public void HideToast()
        {
            _toastSeconds = -1;
            if (_toast != null && _toast.activeSelf) _toast.SetActive(false);
        }

        /// <summary>Shows the mode-change caption under the chip with <paramref name="text"/>. The assist hides it after its time.</summary>
        public void ShowCaption(string text)
        {
            if (_caption == null) return;
            _captionLabel.text = text ?? string.Empty;
            if (!_caption.activeSelf) _caption.SetActive(true);
        }

        public void HideCaption()
        {
            if (_caption != null && _caption.activeSelf) _caption.SetActive(false);
        }

        public void SetChipVisible(bool visible)
        {
            if (_chip != null && _chip.gameObject.activeSelf != visible) _chip.gameObject.SetActive(visible);
        }

        public void SetChipMode(InteractionMode mode)
        {
            bool editing = mode == InteractionMode.SceneEdit;
            _chipLabel.text = editing ? "Editing" : "Interaction";
            var c = UiTheme.Button(editing);   // Editing is the primary look, Interaction the secondary
            _chipLabel.color = c.Label;
            _chipDot.color = editing ? c.Label : _style.Accent;
            _chip.SetColors(c.Normal, c.Hover, c.Pressed);
        }

        public bool HasEye => _eye != null;

        public void SetEye(Transform eye) => _eye = eye;

        /// <summary>Puts the head-following anchor in front of the eye at once (the first frame, so it does not fly in).</summary>
        public void SnapHud()
        {
            if (_eye == null) return;
            _hudYaw = _eye.eulerAngles.y;
            _hud.position = HudTarget(_hudYaw);
            _hud.rotation = Quaternion.Euler(0f, _hudYaw, 0f);
        }

        // ---- per frame ----

        private void LateUpdate()
        {
            if (_eye == null) return;
            if (_prompt != null && _prompt.activeSelf) PlacePrompt();
            if (_menuPrompt != null && _menuPrompt.activeSelf) PlaceAt(_menuPrompt, _menuRoot, _menuAnchorLocal);
            FollowHead();
        }

        private void PlacePrompt()
        {
            if (_promptRoot == null) { HidePrompt(); return; }
            PlaceAt(_prompt, _promptRoot, _promptAnchorLocal);
        }

        // A prompt hangs over its object, faces the eye and grows with distance (the Room surface) so its text keeps its angular size.
        private void PlaceAt(GameObject panel, Transform root, Vector3 anchorLocal)
        {
            if (root == null) { panel.SetActive(false); return; }
            Vector3 position = root.TransformPoint(anchorLocal);
            panel.transform.position = position;
            UiMetrics.Face(panel.transform, _eye, UiMetrics.Room, (RectTransform)panel.transform);
        }

        // Lazy follow: the anchor stays where it is until the head has turned past 20 degrees from it, then swings after the head.
        private void FollowHead()
        {
            float headYaw = _eye.eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(_hudYaw, headYaw)) > 20f)
                _hudYaw = Mathf.MoveTowardsAngle(_hudYaw, headYaw, 180f * Time.unscaledDeltaTime);
            _hud.position = Vector3.SmoothDamp(_hud.position, HudTarget(_hudYaw), ref _hudVelocity, 0.2f, Mathf.Infinity, Time.unscaledDeltaTime);
            _hud.rotation = Quaternion.Euler(0f, _hudYaw, 0f);
        }

        private Vector3 HudTarget(float yaw)
        {
            Vector3 flat = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            return _eye.position + flat * HudDistanceMetres + Vector3.down * HudDropMetres;
        }

        // ---- construction ----

        private void BuildHud()
        {
            var hud = new GameObject("ModeSwitchHud");
            hud.transform.SetParent(transform, false);
            _hud = hud.transform;
            float m = UiMetrics.Hud.MetresPerUnit;

            // The chip sits up and to the left of the view: low is where the left-controller wrist panel is held, and the middle is in the
            // path of every ray the right controller points at something ahead and below. Nothing else lives in the upper left.
            var chipRect = NewPanel("ModeChip", _hud, UiMetrics.Hud, ChipSize, new Vector3(-ChipOffsetMetres, ChipRiseMetres, 0f));
            _chipBackground = ControlPanelKit.AddButtonFill(chipRect, ChipSize.y, false);
            var secondary = UiTheme.Button(false);
            _chip = ModeSwitchClickTarget.Create(chipRect.gameObject, ChipSize, _chipBackground, secondary.Normal, secondary.Hover, secondary.Pressed);
            _chip.Clicked += () => ChipClicked?.Invoke();
            var dot = NewChild("Dot", chipRect, new Vector2(18f, 18f), new Vector2(-ChipSize.x * 0.5f + 34f, 0f));
            _chipDot = AddRounded(dot.gameObject, _style.Accent, 9f);
            _chipLabel = NewLabel("Label", chipRect, new Vector2(ChipSize.x - 70f, ChipSize.y), new Vector2(18f, 0f), "Interaction",
                _style.Text, TextAlignmentOptions.MidlineLeft);

            // The caption beside the chip says what the mode just entered is for. It sits on the chip's row, high in the view: above the
            // wrist panel (which reaches 11 degrees over the eye line on the left) and above the ray's path ahead and below.
            float chipRight = -ChipOffsetMetres + ChipSize.x * 0.5f * m;
            var captionRect = NewPanel("ModeCaption", _hud, UiMetrics.Hud, CaptionSize,
                new Vector3(chipRight + CaptionGapMetres + CaptionSize.x * 0.5f * m, ChipRiseMetres, 0f));
            _caption = captionRect.gameObject;
            ControlPanelKit.AddPanel(captionRect, UiTheme.RadiusCard, opaque: true);   // head-locked widgets can stack: opaque
            _captionLabel = NewLabel("Label", captionRect, CaptionSize - new Vector2(UiTheme.PanelPadding * 2f, 24f), Vector2.zero, "",
                _style.Text, TextAlignmentOptions.MidlineLeft);
            _captionLabel.textWrappingMode = TextWrappingModes.Normal;

            // The hands' Undo button: B's stand-in, shown only to hands in Editing when there is something to undo.
            var undoRect = NewPanel("UndoButton", _hud, UiMetrics.Hud, UndoSize, new Vector3(-ChipOffsetMetres, UndoRiseMetres, 0f));
            var undoBackground = ControlPanelKit.AddButtonFill(undoRect, UndoSize.y, false);
            _undo = ModeSwitchClickTarget.Create(undoRect.gameObject, UndoSize, undoBackground, secondary.Normal, secondary.Hover, secondary.Pressed);
            _undo.Clicked += () => UndoClicked?.Invoke();
            NewLabel("Label", undoRect, UndoSize, Vector2.zero, "Undo", secondary.Label, TextAlignmentOptions.Midline);

            var toastRect = NewPanel("ModeToast", _hud, UiMetrics.Hud, ToastSize, new Vector3(0f, ToastRiseMetres, 0f));
            _toast = toastRect.gameObject;
            ControlPanelKit.AddPanel(toastRect, UiTheme.RadiusCard, opaque: true);
            _toastLabel = NewLabel("Label", toastRect, new Vector2(ToastSize.x - 40f, 44f), new Vector2(0f, ToastSize.y * 0.5f - 38f), "",
                _style.Text, TextAlignmentOptions.Midline);
            var keep = NewChild("KeepEditing", toastRect, KeepButtonSize, new Vector2(0f, -10f));
            _keepButton = NewButton(keep, KeepButtonSize, true, "Keep editing");
            _keepButton.Clicked += () => KeepEditingClicked?.Invoke();
            var track = NewChild("Bar", toastRect, new Vector2(ToastSize.x - 48f, 6f), new Vector2(0f, -ToastSize.y * 0.5f + 16f));
            AddRounded(track.gameObject, UiTheme.Palette.Track, 3f);
            var fill = NewChild("BarFill", track, track.sizeDelta, Vector2.zero);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = Vector2.one;
            fill.offsetMin = fill.offsetMax = Vector2.zero;
            AddRounded(fill.gameObject, _style.Warning, 3f);
            _toastBar = fill;
        }

        private void BuildPrompt()
        {
            var rect = NewPanel("EditPrompt", transform, UiMetrics.Room, PromptSize, Vector3.zero);
            _prompt = rect.gameObject;
            ControlPanelKit.AddPanel(rect, UiTheme.RadiusCard);
            NewLabel("Label", rect, new Vector2(230f, PromptSize.y), new Vector2(-PromptSize.x * 0.5f + UiTheme.PanelPadding + 115f, 0f), "Edit this object?",
                _style.Text, TextAlignmentOptions.MidlineLeft);
            var edit = NewChild("Edit", rect, EditButtonSize, new Vector2(PromptSize.x * 0.5f - UiTheme.PanelPadding - EditButtonSize.x * 0.5f, 0f));
            _editButton = NewButton(edit, EditButtonSize, true, "Edit");
            _editButton.Clicked += () => EditClicked?.Invoke();
        }

        private void BuildMenuPrompt()
        {
            var rect = NewPanel("MenuPrompt", transform, UiMetrics.Room, MenuPromptSize, Vector3.zero);
            _menuPrompt = rect.gameObject;
            ControlPanelKit.AddPanel(rect, UiTheme.RadiusCard);
            NewLabel("Label", rect, new Vector2(170f, MenuPromptSize.y), new Vector2(-MenuPromptSize.x * 0.5f + UiTheme.PanelPadding + 85f, 0f), "Object menu",
                _style.Text, TextAlignmentOptions.MidlineLeft);
            var menu = NewChild("Menu", rect, MenuButtonSize, new Vector2(MenuPromptSize.x * 0.5f - UiTheme.Space4 - MenuButtonSize.x * 0.5f, 0f));
            _menuButton = NewButton(menu, MenuButtonSize, true, "Menu");
            _menuButton.Clicked += () => MenuClicked?.Invoke();
        }

        /// <summary>A button in the theme's one style on <paramref name="rect"/>: a pill fill, its label, and the ray target over it.</summary>
        internal static ModeSwitchClickTarget NewButton(RectTransform rect, Vector2 size, bool primary, string text)
        {
            var c = UiTheme.Button(primary);
            var fill = ControlPanelKit.AddButtonFill(rect, size.y, primary);
            var target = ModeSwitchClickTarget.Create(rect.gameObject, size, fill, c.Normal, c.Hover, c.Pressed);
            NewLabel("Text", rect, size, Vector2.zero, text, c.Label, TextAlignmentOptions.Midline);
            return target;
        }

        /// <summary>A world-space canvas of <paramref name="size"/> units on <paramref name="surface"/>, at a local position in metres.</summary>
        private static RectTransform NewPanel(string name, Transform parent, UiSurface surface, Vector2 size, Vector3 localPositionMetres)
        {
            var rect = UiMetrics.NewCanvas(name, parent, surface, size);
            rect.localPosition = localPositionMetres;
            return rect;
        }

        private static RectTransform NewChild(string name, RectTransform parent, Vector2 size, Vector2 anchoredPosition)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;
            return rect;
        }

        /// <summary>A label in the theme's typeface (SemiBold) at the label token, one line.</summary>
        internal static TextMeshProUGUI NewLabel(string name, RectTransform parent, Vector2 size, Vector2 anchoredPosition, string text,
            Color color, TextAlignmentOptions align, int fontSize = UiTheme.FontLabel)
        {
            var rect = NewChild(name, parent, size, anchoredPosition);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            UiFonts.Apply(label, true);
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = FontStyles.Normal;
            label.color = color;
            label.alignment = align;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>A small rounded mark (a dot, a bar) from the theme's sprites; never a ray target.</summary>
        internal static Image AddRounded(GameObject go, Color color, float radius)
        {
            var image = go.AddComponent<Image>();
            UiSprites.Apply(image, UiSprites.RoundedRect(radius));
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
    }
}
