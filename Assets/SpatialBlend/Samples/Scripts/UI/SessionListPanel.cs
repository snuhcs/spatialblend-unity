// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Room list overlay built at runtime over the control panel's canvas, from the same
    /// <see cref="ControlPanelKit"/> builders and tokens as the panel, so it needs no prefab
    /// or scene wiring. The template button only locates the canvas to cover.
    /// </summary>
    public class SessionListPanel : MonoBehaviour
    {
        /// <summary>Three rooms and the "more" line fit inside the panel (the input surface is the panel), with New room and Back below them.</summary>
        public const int MaxRows = 3;

        private FusionSessionBrowser _browser;
        private TMP_Text _status;
        private RectTransform _rows;
        private Button _newRoom;
        private readonly List<GameObject> _rowObjects = new();
        private System.Action<string> _joinOverride;
        private System.Action _newRoomOverride;

        /// <summary>
        /// True while the list is read-only: the rooms are listed as usual, but a tap on a room or on New room goes to the handlers
        /// given to <see cref="SetReadOnly"/> instead of joining or creating anything (the lobby tutorial shows the real list this way).
        /// </summary>
        public bool ReadOnly => _joinOverride != null || _newRoomOverride != null;

        /// <summary>Routes room taps and New room to the handlers (both null restores the normal behaviour).</summary>
        public void SetReadOnly(System.Action<string> onRoomTapped, System.Action onNewRoomTapped)
        {
            _joinOverride = onRoomTapped;
            _newRoomOverride = onNewRoomTapped;
            if (gameObject.activeSelf) Refresh();
        }

        private void OnRoomTapped(string name)
        {
            if (_joinOverride != null) _joinOverride(name);
            else _browser.Join(name);
        }

        private void OnNewRoomTapped()
        {
            if (_newRoomOverride != null) _newRoomOverride();
            else _browser.CreateRoom();
        }

        public IReadOnlyList<GameObject> RowObjects => _rowObjects;
        public string StatusText => _status == null ? null : _status.text;
        public bool NewRoomInteractable => _newRoom != null && _newRoom.interactable;

        public static SessionListPanel Create(Button template, FusionSessionBrowser browser)
        {
            var canvas = template.GetComponentInParent<Canvas>(true);
            var parent = canvas != null ? canvas.rootCanvas.transform : template.transform.parent;

            var root = new GameObject("SessionListPanel", typeof(RectTransform), typeof(VerticalLayoutGroup),
                typeof(LayoutElement));
            // The control panel canvas lays its children out in a column; the list
            // overlays that column instead of joining it.
            root.GetComponent<LayoutElement>().ignoreLayout = true;
            var rect = (RectTransform)root.transform;
            rect.SetParent(parent, false);
            root.layer = parent.gameObject.layer;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 1f);
            var palette = UiTheme.Palette;
            var surface = ControlPanelKit.AddSurface(rect, UiTheme.RadiusPanel, palette.PanelBottom, 2, true, null, true);
            // The list covers the panel: opaque, so nothing of the panel below shows through.
            var gradient = surface.GetComponent<UiVerticalGradient>();
            gradient.Set(new Color(palette.PanelTop.r, palette.PanelTop.g, palette.PanelTop.b, 1f),
                new Color(palette.PanelBottom.r, palette.PanelBottom.g, palette.PanelBottom.b, 1f));
            var layout = root.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 20, 20);
            layout.spacing = UiTheme.Space3;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var panel = root.AddComponent<SessionListPanel>();
            panel._browser = browser;
            panel._status = panel.CreateText(rect, "Rooms");
            var rows = new GameObject("Rows", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            rows.layer = root.layer;
            panel._rows = (RectTransform)rows.transform;
            panel._rows.SetParent(rect, false);
            var rowsLayout = rows.GetComponent<VerticalLayoutGroup>();
            rowsLayout.spacing = UiTheme.Space3;
            rowsLayout.childControlWidth = true;
            rowsLayout.childControlHeight = true;   // rows take their 84 unit height from the kit
            rowsLayout.childForceExpandWidth = true;
            rowsLayout.childForceExpandHeight = false;
            rows.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            panel._newRoom = panel.CreateButton(rect, "New room", panel.OnNewRoomTapped, true);
            panel.CreateButton(rect, "Back", panel.Hide, false);

            browser.Changed += panel.Refresh;
            root.SetActive(false);
            return panel;
        }

        public void Show()
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            _browser.OpenLobby();
            Refresh();
        }

        public void Hide()
        {
            gameObject.SetActive(false);
            if (_browser != null && _browser.State != FusionSessionBrowserState.Starting) _browser.CloseLobby();
        }

        private void OnDestroy()
        {
            if (_browser != null) _browser.Changed -= Refresh;
        }

        public void Refresh()
        {
            if (this == null || _rows == null) return;
            foreach (var row in _rowObjects)
            {
                if (Application.isPlaying) Destroy(row);
                else DestroyImmediate(row);
            }
            _rowObjects.Clear();

            switch (_browser.State)
            {
                case FusionSessionBrowserState.Starting:
                    gameObject.SetActive(false);
                    return;
                case FusionSessionBrowserState.JoiningLobby:
                case FusionSessionBrowserState.InLobby when !_browser.HasSessionList:
                    _status.text = "Looking for rooms…";
                    break;
                case FusionSessionBrowserState.Failed:
                    // One plain sentence for the user; the technical error goes to the log.
                    if (!string.IsNullOrEmpty(_browser.LastError)) Debug.LogWarning("[SessionListPanel] " + _browser.LastError);
                    _status.text = "Couldn't load rooms. Check your Wi-Fi and try again.";
                    break;
                default:
                    _status.text = _browser.Sessions.Count == 0 ? "No rooms yet — create one." : "Rooms";
                    break;
            }

            // A new room's name could collide with one we cannot see yet, so only
            // offer it once the lobby has delivered a list. An observer never hosts.
            var listed = _browser.State == FusionSessionBrowserState.InLobby && _browser.HasSessionList;
            bool mayHost = listed && _browser.HostingAllowed;
            _newRoom.interactable = mayHost;
            if (listed && !_browser.HostingAllowed)
                _status.text = FreeformMatchmaking.ObserverHostError;
            if (listed) AddRows();
            FitToContent();
        }

        private void AddRows()
        {
            var shown = 0;
            foreach (var session in _browser.Sessions)
            {
                if (shown == MaxRows) break;
                var name = session.Name;
                var label = $"{name}   {session.Occupancy}" + (session.CanJoin ? "" : (session.IsFull ? "  full" : "  closed"));
                var button = CreateButton(_rows, label, () => OnRoomTapped(name), false);
                button.interactable = session.CanJoin;
                _rowObjects.Add(button.gameObject);
                shown++;
            }
            if (_browser.Sessions.Count > shown)
            {
                var more = CreateText(_rows, $"+{_browser.Sessions.Count - shown} more");
                more.fontSize = UiTheme.FontCaption;
                more.color = UiTheme.Palette.TextMuted;
                _rowObjects.Add(more.gameObject);
            }
        }

        // Cover the canvas, growing downward when the list is taller than it.
        private void FitToContent()
        {
            var rect = (RectTransform)transform;
            if (rect.parent is not RectTransform parent) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            var extra = LayoutUtility.GetPreferredHeight(rect) - parent.rect.height;
            rect.offsetMin = new Vector2(0, Mathf.Min(0, -extra));
        }

        private Button CreateButton(RectTransform parent, string label, UnityEngine.Events.UnityAction onClick, bool primary)
        {
            var button = ControlPanelKit.AddButtonRow(parent, label, null, primary);
            button.gameObject.name = "SessionListButton";
            button.onClick.AddListener(onClick);
            SetLayerRecursive(button.gameObject, parent.gameObject.layer);
            return button;
        }

        private TMP_Text CreateText(RectTransform parent, string value)
        {
            var palette = UiTheme.Palette;
            var text = ControlPanelKit.Label(parent, value, UiTheme.FontTitle, TextAlignmentOptions.Left, palette.Text, true, true);
            text.gameObject.name = "Text";
            text.rectTransform.sizeDelta = new Vector2(text.rectTransform.sizeDelta.x, 48);
            text.gameObject.layer = parent.gameObject.layer;
            return text;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
