// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>What a session status change shows as a toast. Pure, so the rule is testable without a scene.</summary>
    public struct ToastContent
    {
        public bool Show;
        public StatusTone Tone;
        public string Title;
        public string Message;
    }

    public static class SessionToastPresenter
    {
        /// <summary>
        /// A toast follows a change of the session status the panel already shows: joining and hosting a room (Good),
        /// connecting (Busy), a failure or the host leaving (Bad), and leaving a room (back to Neutral after Good).
        /// The first status after the panel is built shows nothing; neither does returning to the idle state.
        /// </summary>
        public static ToastContent Describe(bool hasPrevious, SessionStatusView previous, SessionStatusView current)
        {
            var none = new ToastContent();
            if (!hasPrevious) return none;
            // A change of what the user sees as the state (its tone), or a new failure, is news; a changing head count or a
            // late room name in the same state is not.
            if (previous.Tone == current.Tone && (current.Tone != StatusTone.Bad || previous.Headline == current.Headline)) return none;
            // Going back to neutral says nothing: a real leave reloads the scene, and a dropped link is shown by the panel itself.
            if (current.Tone == StatusTone.Neutral) return none;
            return new ToastContent { Show = true, Tone = current.Tone, Title = current.Headline, Message = current.Detail ?? string.Empty };
        }
    }

    /// <summary>
    /// A stack of toasts above a panel. It draws and expires cards; it owns no events. Callers (the ControlPanel) push a
    /// card when something they already observe changes. Toasts never take ray input.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class UiToastHost : MonoBehaviour
    {
        public const int MaxVisible = 3;
        public const float DefaultSeconds = 4f;
        /// <summary>A toast is a short card, not as wide as the panel.</summary>
        public const float ToastWidth = 420f;
        private const float FadeSeconds = 0.4f;

        private sealed class Item { public RectTransform Rect; public CanvasGroup Group; public float Expires; }

        private readonly List<Item> _items = new();
        public int Count => _items.Count;

        public static UiToastHost Create(RectTransform panel)
        {
            var rect = ControlPanelKit.NewRect("Toasts", panel);
            rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, UiTheme.Space4);
            rect.sizeDelta = new Vector2(ToastWidth, 0f);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = UiTheme.Space3;
            layout.childAlignment = TextAnchor.LowerCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            rect.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rect.gameObject.layer = panel.gameObject.layer;
            return rect.gameObject.AddComponent<UiToastHost>();
        }

        public RectTransform Show(StatusTone tone, string title, string message, float seconds = DefaultSeconds)
        {
            while (_items.Count >= MaxVisible) Remove(0);
            var card = ControlPanelKit.AddToast(transform, tone, title, message);
            var group = card.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            foreach (var t in card.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;
            _items.Add(new Item { Rect = card, Group = group, Expires = Time.unscaledTime + seconds });
            return card;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                float left = item.Expires - now;
                if (left <= 0f) { Remove(i); continue; }
                item.Group.alpha = Mathf.Clamp01(left / FadeSeconds);
            }
        }

        private void Remove(int index)
        {
            var item = _items[index];
            _items.RemoveAt(index);
            if (item.Rect != null)
            {
                if (Application.isPlaying) Destroy(item.Rect.gameObject);
                else DestroyImmediate(item.Rect.gameObject);
            }
        }
    }

    /// <summary>
    /// Toasts as an overlay in front of the user instead of on the wrist panel: a world canvas that lazily follows the viewer's
    /// head (a little below the line of sight and right of it, facing them), so a message is seen without looking at the left hand. It draws
    /// and expires cards only and never takes ray input. It is the <see cref="UiMetrics.Toast"/> surface: one unit is one dmm at its
    /// 0.92 m, so a toast's 24 unit text is 24 dmm, the size of the same text on the panel (it was 0.6 mm per unit, 15.6 dmm, before the
    /// UI unification).
    /// </summary>
    [AddComponentMenu("")]
    public sealed class UiToastOverlay : MonoBehaviour
    {
        public const float DistanceMetres = 0.9f, DropMetres = 0.2f, FollowSpeed = 4f;
        /// <summary>
        /// How far right of the gaze the overlay's centre sits (0.06 m at 0.9 m, 3.8 degrees). At the unified 24 dmm text the overlay is
        /// 26 degrees wide; centred it would reach under the left-hand control panel (whose right edge is 11 degrees left of the gaze in the
        /// reference pose), so it moves just clear of it (ControlPanelPlacementTests).
        /// </summary>
        public const float RightMetres = 0.06f;
        /// <summary>World size of one canvas unit (<see cref="UiMetrics.Toast"/>).</summary>
        public static float UnitScale => UiMetrics.Toast.MetresPerUnit;
        public UiToastHost Host { get; private set; }
        /// <summary>The viewer; the main camera when not set. With no camera the overlay stays where it is placed.</summary>
        public Camera Viewer;

        public static UiToastOverlay Create(int layer)
        {
            var go = new GameObject("ToastOverlay", typeof(RectTransform), typeof(Canvas));
            go.layer = layer;
            UiMetrics.MakeWorldSpace(go.GetComponent<Canvas>(), UiMetrics.Toast);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(UiToastHost.ToastWidth + 40f, 260f);
            var overlay = go.AddComponent<UiToastOverlay>();
            overlay.Host = UiToastHost.Create(rect);
            // Cards stack upward from the bottom edge of the overlay.
            var hostRect = (RectTransform)overlay.Host.transform;
            hostRect.anchorMin = hostRect.anchorMax = new Vector2(0.5f, 0f);
            hostRect.pivot = new Vector2(0.5f, 0f);
            hostRect.anchoredPosition = Vector2.zero;
            return overlay;
        }

        /// <summary>Where the overlay belongs for a viewer: ahead along the horizontal gaze, a little below it.</summary>
        public static Vector3 PositionFor(Vector3 eye, Vector3 forward)
        {
            var flat = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            var right = new Vector3(flat.z, 0f, -flat.x);
            return eye + flat * DistanceMetres + right * RightMetres + Vector3.down * DropMetres;
        }

        private bool _placed;

        private void LateUpdate()
        {
            var camera = Viewer != null ? Viewer : Camera.main;
            if (camera == null) return;
            var target = PositionFor(camera.transform.position, camera.transform.forward);
            // The first frame with a viewer snaps to its place; after that the overlay follows lazily.
            transform.position = _placed ? Vector3.Lerp(transform.position, target, Mathf.Clamp01(FollowSpeed * Time.unscaledDeltaTime)) : target;
            _placed = true;
            var away = transform.position - camera.transform.position;
            if (away.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }

    /// <summary>
    /// A pill label that floats over a target and faces the viewer. It shows text; it reacts to nothing. The <see cref="UiMetrics.Room"/>
    /// surface: its 24 unit text is 24 dmm, growing with distance so it stays that size across the room.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class UiWorldLabel : MonoBehaviour
    {
        public Transform Target;
        public Vector3 Offset = new Vector3(0f, 0.15f, 0f);
        /// <summary>The viewer; the main camera when not set.</summary>
        public Camera Viewer;
        private Camera _camera;

        public static UiWorldLabel Create(Transform target, string text, StatusTone tone = StatusTone.Neutral)
        {
            var go = new GameObject("WorldLabel", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)go.transform;
            rect.SetParent(target, false);
            UiMetrics.MakeWorldSpace(go.GetComponent<Canvas>(), UiMetrics.Room);
            rect.sizeDelta = new Vector2(1f, 1f);
            var pill = ControlPanelKit.AddWorldLabel(rect, text, tone);
            pill.anchorMin = pill.anchorMax = pill.pivot = new Vector2(0.5f, 0.5f);
            go.layer = target.gameObject.layer;
            var label = go.AddComponent<UiWorldLabel>();
            label.Target = target;
            label.transform.position = target.position + label.Offset;
            return label;
        }

        private void LateUpdate()
        {
            if (Target == null) return;
            _camera = Viewer != null ? Viewer : (_camera != null ? _camera : Camera.main);
            transform.position = Target.position + Offset;
            if (_camera == null) return;
            Vector3 away = transform.position - _camera.transform.position;
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
            // The scale is in world terms whatever the target's own scale: the label is a child of it.
            float parent = transform.parent != null ? Mathf.Max(1e-6f, transform.parent.lossyScale.x) : 1f;
            transform.localScale = Vector3.one * (UiMetrics.Room.WorldScale(away.magnitude) / parent);
        }
    }
}
