// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The window of the desktop spectator (the room field, "Join as spectator", the status line, "Leave", what it is waiting for, what
    /// went wrong, and the camera keys). It replaces the Unity IMGUI default skin with the app's own look: the Graphite palette
    /// (<see cref="UiTheme"/>), the Pretendard typeface (<see cref="UiFonts"/>) and the control panel's builders
    /// (<see cref="ControlPanelKit"/>), on a screen-space canvas. It reads the session and acts through its public Join and Leave, so the
    /// session's contract is unchanged.
    /// </summary>
    public sealed class SpectatorOverlay : MonoBehaviour
    {
        public const string RoomPlaceholder = "Room name";
        public const string JoinLabel = "Join as spectator";
        public const string LeaveLabel = "Leave";
        public const string KeysHelp = "Right mouse: orbit    WASD: fly    Q/E: down/up    Scroll: zoom    F: frame room    M: minimap    Tab: next room";
        /// <summary>The card's width in canvas units. The canvas is 1920 x 1080 units, so the card is 46% of a screen's width.</summary>
        public const float CardWidth = 880f;
        /// <summary>The overlay canvas's reference size: a unit is a pixel on a 1080p display, so a body em is 24 px and a row 84 px.</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        private FreeformSpectatorSession _session;
        private Canvas _canvas;
        private RectTransform _card;
        private GameObject _joinRow, _statusRow;
        private TMP_InputField _room;
        private Button _join, _leave;
        private TMP_Text _status, _notice, _error, _carrier;
        private string _lastNotice, _lastError, _lastCarrier, _lastStatus;
        private SpectatorPhase? _lastPhase;

        public Canvas Canvas => _canvas;
        public RectTransform Card => _card;
        public TMP_InputField RoomField => _room;
        public Button JoinButton => _join;
        public Button LeaveButton => _leave;
        public TMP_Text StatusText => _status;
        public TMP_Text NoticeText => _notice;
        public TMP_Text ErrorText => _error;
        public TMP_Text CarrierText => _carrier;
        public bool JoinRowVisible => _joinRow != null && _joinRow.activeSelf;
        public bool StatusRowVisible => _statusRow != null && _statusRow.activeSelf;

        /// <summary>Builds the overlay for a session. <paramref name="fonts"/> is the UI typeface set, carried by the scene so a player build includes it.</summary>
        public static SpectatorOverlay Create(FreeformSpectatorSession session, UiFontSet fonts, Transform parent = null)
        {
            UiFonts.Register(fonts);
            var go = new GameObject("SpectatorOverlay", typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            var overlay = go.AddComponent<SpectatorOverlay>();
            overlay._session = session;
            overlay.Build();
            overlay.Refresh();
            return overlay;
        }

        private void Build()
        {
            var p = UiTheme.Palette;
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            _card = ControlPanelKit.Stack("Card", transform, UiTheme.Space3);
            _card.anchorMin = _card.anchorMax = _card.pivot = new Vector2(0f, 1f);
            _card.anchoredPosition = new Vector2(UiTheme.Space6, -UiTheme.Space6);
            _card.sizeDelta = new Vector2(CardWidth, 0f);
            var stack = _card.GetComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(28, 28, 24, 24);
            _card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ControlPanelKit.AddSurface(_card, UiTheme.RadiusPanel, p.PanelTop, 3, true, null, true);

            // Idle or failed: the room field and the Join button.
            var joinRow = Row("JoinRow");
            _joinRow = joinRow.gameObject;
            var roomLabel = ControlPanelKit.Label(joinRow, "Room", UiTheme.FontBody, TextAlignmentOptions.Left, p.TextMuted, true);
            roomLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = 96f;
            _room = BuildRoomField(joinRow);
            _join = ControlPanelKit.AddButtonRow(joinRow, JoinLabel, OnJoin, true);
            Width(_join.gameObject, 300f);

            // In a room: the status line and Leave.
            var statusRow = Row("StatusRow", grows: true);
            _statusRow = statusRow.gameObject;
            _status = ControlPanelKit.Label(statusRow, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.Text, false, true);
            _status.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            _leave = ControlPanelKit.AddButtonRow(statusRow, LeaveLabel, OnLeave, false);
            Width(_leave.gameObject, 200f);

            _notice = ControlPanelKit.Label(_card, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.Warning, false, true);
            _error = ControlPanelKit.Label(_card, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.Error, false, true);
            _carrier = ControlPanelKit.Label(_card, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted, false, true);
            ControlPanelKit.Label(_card, KeysHelp, UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted, false, true);
        }

        /// <summary>
        /// A card row. <paramref name="grows"/>: the row is at least one control high and takes the height of its tallest child, so a status that
        /// wraps (room, phase, core, carrier, players and entities can take three lines) pushes the rows below it down instead of running over them.
        /// </summary>
        private RectTransform Row(string name, bool grows = false)
        {
            var rect = ControlPanelKit.NewRect(name, _card);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = UiTheme.Space3;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var size = rect.gameObject.AddComponent<LayoutElement>();
            size.minHeight = ControlPanelKit.RowHeight;
            size.preferredHeight = grows ? -1f : ControlPanelKit.RowHeight;
            return rect;
        }

        private static void Width(GameObject go, float width)
        {
            var layout = go.GetComponent<LayoutElement>();
            layout.preferredWidth = layout.minWidth = width;
        }

        private TMP_InputField BuildRoomField(RectTransform parent)
        {
            var p = UiTheme.Palette;
            var rect = ControlPanelKit.NewRect("RoomField", parent);
            var size = rect.gameObject.AddComponent<LayoutElement>();
            size.flexibleWidth = 1f;
            size.minHeight = size.preferredHeight = ControlPanelKit.RowHeight;
            float radius = ControlPanelKit.RowHeight * 0.5f;
            var fill = ControlPanelKit.AddSurface(rect, radius, Color.white, 1, true);   // white: the colour block tints it
            var viewport = ControlPanelKit.NewRect("Viewport", rect);
            ControlPanelKit.Stretch(viewport, 0f);
            viewport.offsetMin = new Vector2(UiTheme.Space5, 0f);
            viewport.offsetMax = new Vector2(-UiTheme.Space5, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var text = ControlPanelKit.Label(viewport, string.Empty, UiTheme.FontBody, TextAlignmentOptions.Left, p.Text, false, false);
            ControlPanelKit.Stretch(text.rectTransform, 0f);
            var placeholder = ControlPanelKit.Label(viewport, RoomPlaceholder, UiTheme.FontBody, TextAlignmentOptions.Left, p.TextDisabled, false, false);
            ControlPanelKit.Stretch(placeholder.rectTransform, 0f);
            var field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = fill;
            field.textViewport = viewport;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.caretColor = p.Accent;
            field.customCaretColor = true;
            field.caretWidth = 3;
            field.selectionColor = UiTheme.WithAlpha(p.Accent, 0.35f);
            field.colors = ControlPanelKit.Block(p.Surface, p.SurfaceHover, p.SurfacePressed, p.SurfaceDisabled);
            field.onSubmit.AddListener(_ => OnJoin());
            ControlPanelKit.AddFocusRing(rect, radius);
            return field;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindFirstObjectByType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private void OnJoin()
        {
            if (_session != null && _room != null) _session.Join(_room.text);
        }

        private void OnLeave()
        {
            if (_session != null) _session.Leave();
        }

        private void Update() => Refresh();

        /// <summary>Brings the card in line with the session: the join row while idle or failed, the status row otherwise.</summary>
        public void Refresh()
        {
            if (_session == null) return;
            var phase = _session.Phase;
            bool joining = phase == SpectatorPhase.Idle || phase == SpectatorPhase.Failed;
            if (_lastPhase != phase)
            {
                _lastPhase = phase;
                _joinRow.SetActive(joining);
                _statusRow.SetActive(!joining);
                if (joining && _room != null && string.IsNullOrEmpty(_room.text)) _room.text = _session.TypedRoom;
            }
            if (!joining) Set(_status, ref _lastStatus, _session.StatusLine());
            Set(_notice, ref _lastNotice, _session.Notice);
            Set(_error, ref _lastError, _session.Error);
            Set(_carrier, ref _lastCarrier, _session.CarrierText);
        }

        private static void Set(TMP_Text label, ref string last, string value)
        {
            value = value ?? string.Empty;
            if (value == last) return;
            last = value;
            label.text = value;
            label.gameObject.SetActive(value.Length > 0);
        }
    }
}
