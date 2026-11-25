// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Fusion;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The spectator's minimap: a top-down overview of ONE participant's room in the bottom-right corner of the spectator window, drawn
    /// from that participant's room snapshot (the desktop observer's OBS1 stream, <see cref="FreeformSpectatorSession.Rooms"/>), not
    /// from a second camera. It shows the room's walls and floor, the furniture as faint footprints (shared real objects in the
    /// shared-real violet, <see cref="UiTheme"/>.SharedReal), the placed virtual objects (Chess, Whiteboard) as small labelled squares, the
    /// room's owner (their headset, physically in that room) as a FILLED circle and every other participant's avatar as realized in
    /// that room as a RING, each with a heading tick, a per-participant colour and a "P{id}" label. The spectator camera is a wedge when
    /// the spectator's own scene and the room share a placed object (the map is aligned on it); otherwise no wedge.
    /// World +X is right and world +Z is up on the map, fixed (it never turns with the camera). Updated at
    /// <see cref="RefreshHz"/>; M hides or shows it, Tab switches between the rooms the spectator receives.
    /// </summary>
    public sealed class SpectatorMinimap : MonoBehaviour
    {
        public const float RefreshHz = 12f;
        /// <summary>The panel's side as a fraction of the canvas height.</summary>
        public const float HeightFraction = 0.24f;
        public const KeyCode ToggleKey = KeyCode.M;
        public const KeyCode NextRoomKey = KeyCode.Tab;
        public const string HelpText = "M: hide    Tab: room";
        public const int MaxLabels = 32;
        public const string NoRoomText = "Minimap: no room yet";

        /// <summary>
        /// What the empty map says (instead of a dark square): the spectator is in the room and waits for a participant's room snapshot,
        /// with the Photon region and the players it sees, so a wrong region or an empty room is visible at a glance.
        /// </summary>
        public static string WaitingText(string region, int players) =>
            $"Waiting for the participants' rooms\nregion {region}, players {players}\n(participants send their room once they see this spectator)";

        private FreeformSpectatorSession _session;
        private RectTransform _panel;
        private SpectatorMinimapGraphic _graphic;
        private TMP_Text _title;
        private readonly List<TMP_Text> _labels = new List<TMP_Text>();
        private readonly SpectatorMinimapLayout _layout = new SpectatorMinimapLayout();
        private float _next;
        private float _nextCache;
        private string _lastTitle, _titleRoom, _lastWaiting;
        private TMP_Text _waiting;

        /// <summary>The empty map's status text, or null when a room is drawn.</summary>
        public string WaitingStatus => _waiting != null && _waiting.gameObject.activeSelf ? _waiting.text : null;

        private void SetWaiting(string text)
        {
            if (_waiting == null) return;
            bool show = text != null;
            if (_waiting.gameObject.activeSelf != show) _waiting.gameObject.SetActive(show);
            if (show && text != _lastWaiting) { _lastWaiting = text; _waiting.text = text; }
        }
        private int _titleIndex, _titleCount;
        // Caches of the spectator's own scene, refreshed every 2 s: Fusion object names by raw id, and the keys of shared real objects.
        private readonly Dictionary<uint, Transform> _netObjects = new Dictionary<uint, Transform>();
        private readonly Dictionary<string, Transform> _nativeObjects = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly List<(string label, string key, Vector3 size)> _shared = new List<(string, string, Vector3)>();
        private readonly Dictionary<string, string> _objectLabels = new Dictionary<string, string>();
        private System.Func<ObserverFurniture, bool> _isShared;
        private System.Func<string, string> _objectLabel;

        /// <summary>The user's M toggle. The panel is shown only when this is true AND the session is spectating.</summary>
        public bool Visible { get; private set; } = true;
        /// <summary>Whether the panel is on screen now.</summary>
        public bool Shown => _panel != null && _panel.gameObject.activeSelf;
        public SpectatorMinimapLayout Layout => _layout;
        public RectTransform Panel => _panel;
        public SpectatorMinimapGraphic Graphic => _graphic;
        public string Title => _lastTitle;

        public static SpectatorMinimap Create(FreeformSpectatorSession session, Transform canvas)
        {
            var go = new GameObject("SpectatorMinimap", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            var map = go.AddComponent<SpectatorMinimap>();
            map._session = session;
            map.Build(canvas as RectTransform);
            map.ApplyShown();
            return map;
        }

        private void Build(RectTransform canvas)
        {
            var p = UiTheme.Palette;
            var root = (RectTransform)transform;
            ControlPanelKit.Stretch(root, 0f);
            float side = SpectatorOverlay.ReferenceResolution.y * HeightFraction;
            float header = UiTheme.FontCaption + UiTheme.Space3;
            _panel = ControlPanelKit.NewRect("MinimapPanel", root);
            _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(1f, 0f);
            _panel.anchoredPosition = new Vector2(-UiTheme.Space5, UiTheme.Space5);
            _panel.sizeDelta = new Vector2(side, side + header);
            ControlPanelKit.AddSurface(_panel, UiTheme.RadiusCard * 0.5f, p.PanelBottom, 2, true);

            _title = ControlPanelKit.Label(_panel, string.Empty, UiTheme.FontCaption, TextAlignmentOptions.Left, p.TextMuted);
            var tr = _title.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f); tr.pivot = new Vector2(0.5f, 1f);
            tr.offsetMin = new Vector2(UiTheme.Space3, -header); tr.offsetMax = new Vector2(-UiTheme.Space3, -UiTheme.Space1);

            var mapRect = ControlPanelKit.NewRect("Map", _panel);
            mapRect.anchorMin = Vector2.zero; mapRect.anchorMax = new Vector2(1f, 0f); mapRect.pivot = new Vector2(0.5f, 0f);
            mapRect.offsetMin = new Vector2(0f, 0f); mapRect.offsetMax = new Vector2(0f, side);
            mapRect.gameObject.AddComponent<RectMask2D>();
            _graphic = mapRect.gameObject.AddComponent<SpectatorMinimapGraphic>();
            _graphic.raycastTarget = false;
            _graphic.Bind(_layout);

            for (int i = 0; i < MaxLabels; ++i)
            {
                var label = ControlPanelKit.Label(mapRect, string.Empty, UiTheme.Spectator.MapLabelFont, TextAlignmentOptions.Center, p.Text, true);
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = Vector2.zero;
                label.rectTransform.sizeDelta = new Vector2(60f, 20f);
                label.gameObject.SetActive(false);
                _labels.Add(label);
            }
            _waiting = ControlPanelKit.Label(mapRect, string.Empty, UiTheme.Spectator.MapStatusFont, TextAlignmentOptions.Center, p.TextMuted, false, true);
            ControlPanelKit.Stretch(_waiting.rectTransform, UiTheme.Space3);
            _waiting.gameObject.SetActive(false);
            _isShared = f =>
            {
                for (int i = 0; i < _shared.Count; ++i)
                    if (SpectatorMinimapLayout.MatchesShared(f, _shared[i].label, _shared[i].key, _shared[i].size)) return true;
                return false;
            };
            _objectLabel = key => _objectLabels.TryGetValue(key, out string label) ? label : null;
        }

        /// <summary>Applies the keys: <paramref name="toggle"/> hides or shows the map; <paramref name="nextRoom"/> shows the next room.</summary>
        public void HandleKeys(bool toggle, bool nextRoom)
        {
            if (toggle) SetVisible(!Visible);
            if (nextRoom && Visible) NextRoom();
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            ApplyShown();
        }

        /// <summary>Shows the panel only while spectating and toggled on: the lobby and a failed join have no room to map.</summary>
        public void ApplyShown()
        {
            bool shown = Visible && _session != null && _session.Phase == SpectatorPhase.Spectating;
            if (_panel != null && _panel.gameObject.activeSelf != shown) _panel.gameObject.SetActive(shown);
        }

        // The received rooms in numeric player-id order (the catalog sorts by display name, where "Participant 10" precedes "Participant 2").
        private readonly List<ParticipantRoomSnapshot> _ordered = new List<ParticipantRoomSnapshot>();
        private static readonly Comparison<ParticipantRoomSnapshot> s_byId = SpectatorMinimapLayout.CompareRooms;

        private List<ParticipantRoomSnapshot> OrderedRooms(ObserverRoomCatalog catalog)
        {
            _ordered.Clear();
            for (int i = 0; i < catalog.Rooms.Count; ++i) _ordered.Add(catalog.Rooms[i]);
            _ordered.Sort(s_byId);
            return _ordered;
        }

        /// <summary>Selects the next room the spectator receives, in player-id order. False with fewer than two rooms.</summary>
        public bool NextRoom()
        {
            var catalog = _session != null ? _session.Rooms.Rooms : null;
            if (catalog == null || catalog.Rooms.Count < 2) return false;
            var rooms = OrderedRooms(catalog);
            int index = 0;
            for (int i = 0; i < rooms.Count; ++i)
                if (rooms[i].ParticipantId == catalog.SelectedParticipantId) { index = i; break; }
            return catalog.Select(rooms[(index + 1) % rooms.Count].ParticipantId);
        }

        private void Update()
        {
            bool typing = SpectatorCamera.TypingInTextField();
            HandleKeys(SpectatorCamera.KeyPressed(typing, Input.GetKeyDown, ToggleKey), SpectatorCamera.KeyPressed(typing, Input.GetKeyDown, NextRoomKey));
            ApplyShown();
            if (!Shown || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f / RefreshHz;
            Refresh();
        }

        /// <summary>Lays the selected room out and redraws. Called at <see cref="RefreshHz"/>; public for tests.</summary>
        public void Refresh()
        {
            if (_session == null || _graphic == null) return;
            if (Time.unscaledTime >= _nextCache) { _nextCache = Time.unscaledTime + 2f; RefreshSceneCaches(); }
            var catalog = _session.Rooms.Rooms;
            ParticipantRoomSnapshot room = catalog.Selected;
            float side = _graphic.rectTransform.rect.width;
            _layout.Build(room, SpectatorMinimapLayout.ReferenceSide, _isShared, _objectLabel);
            _graphic.SetWedge(TryWedge(room, out Vector2 apex, out Vector2 direction, out float halfAngle), apex, direction, halfAngle);
            _graphic.SetVerticesDirty();

            SetWaiting(room == null ? WaitingText(_session.RegionLabel, _session.PlayerCountForMap) : null);
            int index = room == null ? -1 : IndexOf(catalog, room);
            if (_lastTitle == null || room?.ParticipantId != _titleRoom || index != _titleIndex || catalog.Rooms.Count != _titleCount)
            {
                _titleRoom = room?.ParticipantId; _titleIndex = index; _titleCount = catalog.Rooms.Count;
                _lastTitle = room == null
                    ? NoRoomText
                    : $"Room of {SpectatorMinimapLayout.ParticipantLabel(room.ParticipantId)}  {index + 1}/{catalog.Rooms.Count}";
                _title.text = _lastTitle;
            }
            PlaceLabels(side);
        }

        private int IndexOf(ObserverRoomCatalog catalog, ParticipantRoomSnapshot room)
        {
            var rooms = OrderedRooms(catalog);
            for (int i = 0; i < rooms.Count; ++i) if (rooms[i] == room) return i;
            return 0;
        }

        private void PlaceLabels(float side)
        {
            float k = side / SpectatorMinimapLayout.ReferenceSide;
            int n = 0;
            var markers = _layout.Markers;
            for (int i = 0; i < markers.Count && n < _labels.Count; ++i, ++n)
                SetLabel(_labels[n], markers[i].Label, (markers[i].Position + new Vector2(0f, 16f)) * k, markers[i].Color);
            var footprints = _layout.Footprints;
            for (int i = 0; i < footprints.Count && n < _labels.Count; ++i)
            {
                if (footprints[i].Kind != MinimapFootprintKind.VirtualObject || footprints[i].Label == null) continue;
                Vector2 c = (footprints[i].A + footprints[i].C) * 0.5f;
                SetLabel(_labels[n++], footprints[i].Label, (c + new Vector2(0f, -14f)) * k, UiTheme.Palette.Accent);
            }
            for (; n < _labels.Count; ++n) if (_labels[n].gameObject.activeSelf) _labels[n].gameObject.SetActive(false);
        }

        private static void SetLabel(TMP_Text label, string text, Vector2 position, Color color)
        {
            if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
            if (label.text != text) label.text = text;
            label.color = color;
            label.rectTransform.anchoredPosition = position;
        }

        /// <summary>
        /// The map from the spectator's world to <paramref name="room"/>: aligned through the first placed object that both the room
        /// snapshot and the spectator's scene have (a Fusion scene object by its network id: Chess, Whiteboard). False when none is common.
        /// Shared with the 3-D room view (<see cref="SpectatorRoomView"/>), so the map and the view agree.
        /// </summary>
        public bool TryAlignment(ParticipantRoomSnapshot room, out GroundAlignment align)
        {
            align = default;
            if (room == null) return false;
            // The 3-D room view puts the spectator's objects at their poses in the viewed room, so the spectator's world IS that
            // room's frame: no alignment to find.
            if (_session != null && _session.RoomView != null) { align = new GroundAlignment(Vector2.zero, Vector2.zero, 0f); return true; }
            if (Time.unscaledTime >= _nextCache) { _nextCache = Time.unscaledTime + 2f; RefreshSceneCaches(); }
            for (int i = 0; i < room.Objects.Count; ++i)
            {
                ObserverPlacedObject o = room.Objects[i];
                if (!ObserverSceneMap.TryParseNetworkKey(o.Key, out uint raw) || !_netObjects.TryGetValue(raw, out Transform local) || local == null) continue;
                if (SpectatorMinimapLayout.TryAlign(local.position, local.rotation, o.Position, o.Rotation, out align)) return true;
            }
            return false;
        }

        /// <summary>
        /// The spectator's own object for a room snapshot's placed-object key: a Fusion scene object by network id (Chess, the
        /// Whiteboard and its child) or a received native object by name. Refreshed every 2 s.
        /// </summary>
        public bool TryFindLocalObject(string key, out Transform local)
        {
            local = null;
            if (Time.unscaledTime >= _nextCache) { _nextCache = Time.unscaledTime + 2f; RefreshSceneCaches(); }
            if (ObserverSceneMap.TryParseNetworkKey(key, out uint raw)) return _netObjects.TryGetValue(raw, out local) && local != null;
            return _nativeObjects.TryGetValue(key, out local) && local != null;
        }

        /// <summary>Whether a piece of a room's furniture is a shared real object (the spectator received it), for the map and the 3-D view.</summary>
        public System.Func<ObserverFurniture, bool> IsSharedFurniture => _isShared;

        /// <summary>The spectator camera on the map, through <see cref="TryAlignment"/>. False when no object is common.</summary>
        private bool TryWedge(ParticipantRoomSnapshot room, out Vector2 apex, out Vector2 direction, out float halfAngle)
        {
            apex = direction = Vector2.zero;
            halfAngle = 0f;
            var cam = Camera.main;
            if (cam == null || !TryAlignment(room, out GroundAlignment align)) return false;
            Vector2 camGround = align.Point(SpectatorMinimapLayout.Ground(cam.transform.position));
            Vector2 fwd = SpectatorMinimapLayout.Heading(cam.transform.rotation);
            if (fwd == Vector2.zero) fwd = SpectatorMinimapLayout.Heading(Quaternion.LookRotation(cam.transform.up));
            apex = _layout.ToPanel(camGround);
            direction = align.Rotate(fwd);
            halfAngle = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * 0.5f;
            return true;
        }

        private void RefreshSceneCaches()
        {
            _netObjects.Clear();
            _nativeObjects.Clear();
            _objectLabels.Clear();
            foreach (var no in FindObjectsByType<NetworkObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (no == null || !no.IsValid) continue;
                _netObjects[no.Id.Raw] = no.transform;
                _objectLabels[ObserverSceneMap.NetworkKey(no.Id.Raw)] = ShortName(no.gameObject.name);
            }
            _shared.Clear();
            var app = _session.App;
            if (app == null) return;
            foreach (var go in app.ReceivedObjects)
            {
                if (go == null) continue;
                if (SharedAnchorName.TryParse(go.name, out string head, out _, out Vector3 size, out _))
                {
                    string[] parts = head.Split(SharedAnchorName.KeySeparator);
                    if (parts.Length >= 2) _shared.Add((parts[0], parts[1], size));
                }
                else if (go.GetComponent<SbNativeEntity>() != null)
                {
                    string key = ObserverSceneMap.NativeKey(go.name);
                    _objectLabels[key] = ShortName(go.name);
                    _nativeObjects[key] = go.transform;
                }
            }
        }

        /// <summary>The map's label for a placed object: its name up to the first space, bracket or '#', at most 10 characters.</summary>
        private static readonly char[] s_nameEnds = { ' ', '(', '#' };

        public static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            int end = name.IndexOfAny(s_nameEnds);
            string s = end > 0 ? name.Substring(0, end) : name;
            return s.Length > 10 ? s.Substring(0, 10) : s;
        }
    }

    /// <summary>
    /// Draws a <see cref="SpectatorMinimapLayout"/> as one uGUI mesh: footprints as filled quads with outlines, people as filled circles
    /// (the person in the room) or rings (an avatar) with a heading tick, and the camera wedge. Layout units are scaled to the rect.
    /// </summary>
    public sealed class SpectatorMinimapGraphic : MaskableGraphic
    {
        private const int CircleSegments = 18;
        private const float OutlineWidth = 1.5f;
        private static readonly Color MarkerOutline = UiTheme.Spectator.MarkerOutline;
        private SpectatorMinimapLayout _layout;
        private bool _hasWedge;
        private Vector2 _apex, _direction;
        private float _halfAngle;

        public bool HasWedge => _hasWedge;

        public void Bind(SpectatorMinimapLayout layout) => _layout = layout;

        public void SetWedge(bool has, Vector2 apex, Vector2 direction, float halfAngleDegrees)
        {
            _hasWedge = has;
            _apex = apex;
            _direction = direction;
            _halfAngle = halfAngleDegrees;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_layout == null || !_layout.HasRoom) return;
            Rect r = rectTransform.rect;
            float k = r.width / SpectatorMinimapLayout.ReferenceSide;
            Vector2 o = r.min;
            var p = UiTheme.Palette;
            var footprints = _layout.Footprints;
            for (int i = 0; i < footprints.Count; ++i)
            {
                MinimapFootprint f = footprints[i];
                Vector2 a = o + f.A * k, b = o + f.B * k, c = o + f.C * k, d = o + f.D * k;
                Color fill, line; float width;
                switch (f.Kind)
                {
                    case MinimapFootprintKind.Floor: fill = UiTheme.WithAlpha(p.Knob, 0.05f); line = UiTheme.WithAlpha(p.Knob, 0.15f); width = 1f; break;
                    case MinimapFootprintKind.Wall: fill = Color.clear; line = UiTheme.WithAlpha(p.Text, 0.85f); width = 2.5f; break;
                    case MinimapFootprintKind.SharedFurniture: fill = UiTheme.WithAlpha(p.SharedReal, 0.30f); line = p.SharedReal; width = 2f; break;
                    case MinimapFootprintKind.VirtualObject: fill = UiTheme.WithAlpha(p.Accent, 0.55f); line = p.Accent; width = 1.5f; break;
                    default: fill = UiTheme.WithAlpha(p.Knob, 0.10f); line = UiTheme.WithAlpha(p.Knob, 0.30f); width = 1f; break;
                }
                if (fill.a > 0f) Quad(vh, a, b, c, d, fill);
                Line(vh, a, b, width, line); Line(vh, b, c, width, line); Line(vh, c, d, width, line); Line(vh, d, a, width, line);
            }
            if (_hasWedge)
            {
                Vector2 apex = o + _apex * k;
                float len = 0.22f * r.width;
                Vector2 left = Rotate(_direction, _halfAngle) * len, right = Rotate(_direction, -_halfAngle) * len;
                var wedge = UiTheme.WithAlpha(p.Accent, 0.22f);
                Triangle(vh, apex, apex + left, apex + right, wedge);
                Line(vh, apex, apex + left, 1.5f, p.Accent);
                Line(vh, apex, apex + right, 1.5f, p.Accent);
            }
            var markers = _layout.Markers;
            float radius = Mathf.Max(5f, 0.035f * r.width);
            for (int i = 0; i < markers.Count; ++i)
            {
                MinimapMarker m = markers[i];
                Vector2 c = o + m.Position * k;
                // A thin dark outline under every marker keeps each colour (white included) apart from the map behind it.
                if (m.Heading != Vector2.zero) Line(vh, c + m.Heading * radius * 0.6f, c + m.Heading * radius * 1.9f, 2.5f + 2f * OutlineWidth, MarkerOutline);
                if (m.Kind == MinimapMarkerKind.Person)
                {
                    Disc(vh, c, radius + OutlineWidth, 0f, MarkerOutline);
                    Disc(vh, c, radius, 0f, m.Color);
                }
                else
                {
                    Disc(vh, c, radius + OutlineWidth, radius * 0.62f - OutlineWidth, MarkerOutline);
                    Disc(vh, c, radius, radius * 0.62f, m.Color);
                }
                if (m.Heading != Vector2.zero) Line(vh, c + m.Heading * radius * 0.6f, c + m.Heading * radius * 1.9f, 2.5f, m.Color);
            }
        }

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float rad = degrees * Mathf.Deg2Rad, cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            return new Vector2(cs * v.x - sn * v.y, sn * v.x + cs * v.y);
        }

        private static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero); vh.AddVert(d, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
        }

        private static void Triangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero); vh.AddVert(c, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
        }

        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (width * 0.5f);
            Quad(vh, a - n, b - n, b + n, a + n, color);
        }

        private static void Disc(VertexHelper vh, Vector2 c, float outer, float inner, Color color)
        {
            for (int s = 0; s < CircleSegments; ++s)
            {
                float a0 = s * Mathf.PI * 2f / CircleSegments, a1 = (s + 1) * Mathf.PI * 2f / CircleSegments;
                var u0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)); var u1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                if (inner <= 0f) Triangle(vh, c, c + u0 * outer, c + u1 * outer, color);
                else Quad(vh, c + u0 * inner, c + u0 * outer, c + u1 * outer, c + u1 * inner, color);
            }
        }
    }
}
