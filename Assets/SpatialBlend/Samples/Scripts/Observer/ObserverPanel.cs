// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Left-hand screen panel for a desktop observer. Built with the control panel's Graphite pieces
    /// (<see cref="ControlPanelKit"/>, <see cref="UiTheme"/>, Pretendard), the same way as the spectator window.
    /// </summary>
    public sealed class ObserverPanel : MonoBehaviour
    {
        public const float CardWidth = 480f;
        public const string KeysHelp = "Right mouse: look    WASD: fly    Q/E or Space/Ctrl: down/up    Shift: faster";
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        private ObserverSessionState _state;
        private TMP_Text _status;
        private RectTransform _dynamic;
        private int _listRevision = -1;
        private ObserverCameraMode _mode;
        private string _follow;
        private bool _built;

        public static ObserverPanel Create(ObserverSessionState state, Transform parent = null)
        {
            var go = new GameObject("ObserverPanel", typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            var panel = go.AddComponent<ObserverPanel>();
            panel._state = state;
            panel.BuildChrome();
            panel.Refresh();
            return panel;
        }

        public TMP_Text Status => _status;

        public void Refresh()
        {
            if (_state == null || _status == null) return;
            ParticipantRoomSnapshot room = _state.Rooms.Selected;
            string status = room == null
                ? "Waiting for a participant room"
                : "Viewing " + room.DisplayName + "  ·  space " + room.SpaceId.ToString(CultureInfo.InvariantCulture);
            if (_status.text != status) _status.text = status;
            if (_built && _listRevision == _state.Rooms.ListRevision && _mode == _state.Mode && _follow == _state.FollowParticipantId)
                return;
            _listRevision = _state.Rooms.ListRevision;
            _mode = _state.Mode;
            _follow = _state.FollowParticipantId;
            _built = true;
            RebuildDynamic();
        }

        private void BuildChrome()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            RectTransform card = ControlPanelKit.Stack("Card", transform, UiTheme.Space3);
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0f, 1f);
            card.anchoredPosition = new Vector2(UiTheme.Space6, -UiTheme.Space6);
            card.sizeDelta = new Vector2(CardWidth, 0f);
            VerticalLayoutGroup stack = card.GetComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset((int)UiTheme.Space4, (int)UiTheme.Space4, (int)UiTheme.Space4, (int)UiTheme.Space4);
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            ControlPanelKit.AddSurface(card, UiTheme.RadiusPanel, UiTheme.Palette.PanelBottom, 2, true, null, true);

            ControlPanelKit.Label(card, "Observer", UiTheme.FontTitle, TextAlignmentOptions.Left, UiTheme.Palette.Text, true);
            _status = ControlPanelKit.Label(card, "Waiting for a participant room", ControlPanelKit.SmallFontSize,
                TextAlignmentOptions.Left, UiTheme.Palette.TextMuted, false, true);
            _dynamic = ControlPanelKit.Stack("Dynamic", card, UiTheme.Space3);
            ControlPanelKit.Label(card, KeysHelp, ControlPanelKit.SmallFontSize, TextAlignmentOptions.Left,
                UiTheme.Palette.TextMuted, false, true);
        }

        private void RebuildDynamic()
        {
            for (int i = _dynamic.childCount - 1; i >= 0; --i)
            {
                GameObject child = _dynamic.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }

            RectTransform modes = ControlPanelKit.NewRect("Modes", _dynamic);
            var row = modes.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = UiTheme.Space3;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = false;
            modes.gameObject.AddComponent<LayoutElement>().minHeight = UiTheme.RowHeight;
            ControlPanelKit.AddButtonRow(modes, "Free fly", () => _state.SetMode(ObserverCameraMode.FreeFly),
                _state.Mode == ObserverCameraMode.FreeFly);
            ControlPanelKit.AddButtonRow(modes, "Follow head", () => _state.SetMode(ObserverCameraMode.FollowHead),
                _state.Mode == ObserverCameraMode.FollowHead);

            ControlPanelKit.Label(_dynamic, "Rooms", ControlPanelKit.SmallFontSize, TextAlignmentOptions.Left,
                UiTheme.Palette.TextMuted, true);
            IReadOnlyList<ParticipantRoomSnapshot> rooms = _state.Rooms.Rooms;
            if (rooms.Count == 0)
            {
                ControlPanelKit.Label(_dynamic, "No participant rooms yet", ControlPanelKit.BodyFontSize,
                    TextAlignmentOptions.Left, UiTheme.Palette.Text, false, true);
            }
            for (int i = 0; i < rooms.Count; ++i)
            {
                ParticipantRoomSnapshot room = rooms[i];
                string id = room.ParticipantId;
                bool selected = id == _state.Rooms.SelectedParticipantId;
                string label = room.DisplayName + "  ·  space " + room.SpaceId.ToString(CultureInfo.InvariantCulture);
                ControlPanelKit.AddButtonRow(_dynamic, label, () => _state.SelectRoom(id), selected);
            }

            if (_state.Mode != ObserverCameraMode.FollowHead) return;
            ParticipantRoomSnapshot selectedRoom = _state.Rooms.Selected;
            if (selectedRoom == null) return;
            ControlPanelKit.Label(_dynamic, "Viewpoint", ControlPanelKit.SmallFontSize, TextAlignmentOptions.Left,
                UiTheme.Palette.TextMuted, true);
            AddViewpoint(selectedRoom.ParticipantId, selectedRoom.DisplayName);
            for (int i = 0; i < selectedRoom.Avatars.Count; ++i)
            {
                ObserverAvatarPose avatar = selectedRoom.Avatars[i];
                if (avatar.ParticipantId == selectedRoom.ParticipantId) continue;
                string name = string.IsNullOrEmpty(avatar.DisplayName) ? "Participant " + avatar.ParticipantId : avatar.DisplayName;
                AddViewpoint(avatar.ParticipantId, name);
            }
        }

        private void AddViewpoint(string participantId, string label)
        {
            string id = participantId;
            ControlPanelKit.AddButtonRow(_dynamic, label, () => _state.SetFollow(id), id == _state.FollowParticipantId);
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindFirstObjectByType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}
