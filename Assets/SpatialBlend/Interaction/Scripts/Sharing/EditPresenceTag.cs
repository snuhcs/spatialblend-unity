// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// The small tag that tells others what someone is doing: "Moving: Player 2" over a shared object someone else holds, and
    /// "Editing" over the avatar of a participant in Editing. A pill in the theme's one panel style with an accent dot, never the
    /// real-anchor orange, on the <see cref="UiMetrics.Room"/> surface (24 dmm text, growing with distance so it stays that size across
    /// the room). It floats a fixed height over the point it follows and turns to face the viewer. It shows nothing a user can press.
    /// </summary>
    public sealed class EditPresenceTag : MonoBehaviour
    {
        /// <summary>Pill size in canvas units (dmm, <see cref="UiMetrics.Room"/>).</summary>
        public static readonly Vector2 Size = new Vector2(300f, 64f);

        private Transform _follow;
        private Vector3 _offset;
        private TextMeshProUGUI _label;
        private GameObject _body;
        private RectTransform _canvas;

        public bool Visible => _body != null && _body.activeSelf;
        public string Text => _label != null ? _label.text : string.Empty;
        public Transform Follow => _follow;

        /// <summary>A hidden tag that follows <paramref name="follow"/> at <paramref name="offset"/> (world metres, up is +y).</summary>
        public static EditPresenceTag Create(string name, Transform follow, Vector3 offset)
        {
            var go = new GameObject(name);
            var tag = go.AddComponent<EditPresenceTag>();
            tag._follow = follow;
            tag._offset = offset;
            tag.Build();
            tag.Hide();
            return tag;
        }

        public void Show(string text)
        {
            if (_label.text != text) _label.text = text;
            if (!_body.activeSelf) _body.SetActive(true);
            Place();
        }

        public void Hide()
        {
            if (_body != null && _body.activeSelf) _body.SetActive(false);
        }

        private void LateUpdate()
        {
            if (_follow == null) { Hide(); return; }
            if (Visible) Place();
        }

        private void Place()
        {
            if (_follow == null) return;
            transform.position = _follow.position + _offset;
            Camera eye = Camera.main;
            if (eye == null) return;
            UiMetrics.Face(transform, eye.transform, UiMetrics.Room, _canvas);
        }

        private void Build()
        {
            ModeSwitchStyle style = ModeSwitchStyle.Current;
            _canvas = UiMetrics.NewCanvas("Pill", transform, UiMetrics.Room, Size);
            _body = _canvas.gameObject;
            ControlPanelKit.AddPanel(_canvas, UiTheme.Pill(Size.y));

            var dot = new GameObject("Dot", typeof(RectTransform));
            dot.transform.SetParent(_canvas, false);
            var dotRect = (RectTransform)dot.transform;
            dotRect.sizeDelta = new Vector2(16f, 16f);
            dotRect.anchoredPosition = new Vector2(-Size.x * 0.5f + 30f, 0f);
            ModeSwitchUi.AddRounded(dot, style.Accent, 8f);

            _label = ModeSwitchUi.NewLabel("Label", _canvas, new Vector2(Size.x - 64f, Size.y), new Vector2(16f, 0f), string.Empty,
                style.Text, TextAlignmentOptions.MidlineLeft);
            _label.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    /// <summary>
    /// The outline of a part someone else is moving (a chess piece): the 12 edges of its renderer bounds in the accent of the B Graphite
    /// tokens, so it reads as "someone is doing something with this" and not as a real object (the real-anchor orange).
    /// </summary>
    public sealed class EditPresenceOutline : MonoBehaviour
    {
        private LineRenderer _line;
        private Transform _target;

        public Transform Target => _target;
        public bool Visible => _line != null && _line.enabled;

        public static EditPresenceOutline Create(string name)
        {
            var go = new GameObject(name);
            var outline = go.AddComponent<EditPresenceOutline>();
            outline._line = go.AddComponent<LineRenderer>();
            outline._line.useWorldSpace = true;
            outline._line.loop = false;
            outline._line.widthMultiplier = 0.004f;
            outline._line.material = new Material(Shader.Find("Sprites/Default"));
            Color accent = ModeSwitchStyle.Current.Accent;
            outline._line.startColor = outline._line.endColor = accent;
            outline._line.enabled = false;
            return outline;
        }

        public void Show(Transform target)
        {
            _target = target;
            _line.enabled = target != null;
            Draw();
        }

        public void Hide()
        {
            _target = null;
            if (_line != null) _line.enabled = false;
        }

        private void LateUpdate()
        {
            if (_line.enabled) Draw();
        }

        private void OnDestroy()
        {
            if (_line != null && _line.sharedMaterial != null)
            {
                if (Application.isPlaying) Destroy(_line.sharedMaterial);
                else DestroyImmediate(_line.sharedMaterial);
            }
        }

        private void Draw()
        {
            if (_target == null) { Hide(); return; }
            Bounds b = BoundsOf(_target);
            Vector3 n = b.min, x = b.max;
            // One polyline through all 12 edges (some retraced).
            var p = new[]
            {
                new Vector3(n.x, n.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, n.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(n.x, n.y, n.z),
                new Vector3(n.x, x.y, n.z), new Vector3(x.x, x.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, x.y, n.z),
                new Vector3(x.x, x.y, x.z), new Vector3(x.x, n.y, x.z), new Vector3(x.x, x.y, x.z),
                new Vector3(n.x, x.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(n.x, x.y, x.z), new Vector3(n.x, x.y, n.z),
            };
            _line.positionCount = p.Length;
            _line.SetPositions(p);
        }

        internal static Bounds BoundsOf(Transform target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>();
            bool any = false;
            var bounds = new Bounds(target.position, Vector3.one * 0.02f);
            foreach (var r in renderers)
            {
                if (r is LineRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            bounds.Expand(0.01f);
            return bounds;
        }
    }
}
