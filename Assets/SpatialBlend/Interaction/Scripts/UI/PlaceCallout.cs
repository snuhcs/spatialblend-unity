// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;

namespace SpatialBlend.Interactions.UI
{
    /// <summary>
    /// Whether the "this just arrived, hover it to place it" card is up. The card is for a scan a share dropped on the
    /// floor. It stays visible from across the room and steps aside once the cursor — the ray — comes close, so the
    /// object itself and the hover Edit prompt are what the user sees from there.
    /// </summary>
    public static class PlaceCallout
    {
        /// <summary>The ray this close to the object, in metres, counts as the cursor having arrived. The card hides.</summary>
        public const float HideWithinMetres = 0.75f;

        public const string Title = "New object";
        public const string Hint = "Hover to place";

        /// <summary>The card is up while the object still needs a place and the cursor has not come this close.</summary>
        public static bool Shows(bool needsPlace, float cursorDistanceMetres) =>
            needsPlace && cursorDistanceMetres > HideWithinMetres;

        /// <summary>
        /// Distance from the cursor to <paramref name="point"/>. The cursor is the ray: a point the ray passes is at
        /// the perpendicular distance, and a point beside or behind the ray's origin is measured from that origin.
        /// </summary>
        public static float CursorDistance(Ray ray, Vector3 point)
        {
            Vector3 direction = ray.direction;
            if (direction.sqrMagnitude < 1e-8f) return Vector3.Distance(ray.origin, point);
            direction.Normalize();
            float along = Vector3.Dot(point - ray.origin, direction);
            Vector3 closest = along > 0f ? ray.origin + direction * along : ray.origin;
            return Vector3.Distance(closest, point);
        }

        /// <summary>A ray that meets the bounds is on the object. Otherwise the distance is to its centre.</summary>
        public static float CursorDistance(Ray ray, Bounds bounds) =>
            bounds.IntersectRay(ray) ? 0f : CursorDistance(ray, bounds.center);
    }

    /// <summary>
    /// The card over one <see cref="UnplacedSharedScan"/>. It faces the eye, grows with distance so the words keep
    /// their size, and hides when <see cref="PlaceCallout.Shows"/> says the cursor is close or the object is placed.
    /// It has no collider, so the ray reaches the object underneath.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class PlaceCalloutView : MonoBehaviour
    {
        private static readonly Vector2 Size = new Vector2(360f, 120f);

        private UnplacedSharedScan _scan;
        private GameObject _panel;
        private Transform _eye;
        private bool _retired;

        private void Awake() => _scan = GetComponent<UnplacedSharedScan>();

        private void LateUpdate() => Apply(EditInput.ActiveRay, Eye());

        private void OnDestroy() => DestroyPanel();

        /// <summary>Whether the card is drawn this moment.</summary>
        internal bool CalloutVisible => _panel != null && _panel.activeSelf;

        /// <summary>One frame. <paramref name="cursor"/> null means there is no ray yet, so the card stays up.</summary>
        internal void Apply(Ray? cursor, Transform eye)
        {
            if (_retired) return;
            if (_scan == null) _scan = GetComponent<UnplacedSharedScan>();
            if (_scan == null || _scan.PositionSpecified)
            {
                Retire();
                return;
            }

            EnsurePanel();
            _eye = eye != null ? eye : Eye();
            Bounds bounds = ObjectBounds();
            float distance = cursor.HasValue ? PlaceCallout.CursorDistance(cursor.Value, bounds) : float.PositiveInfinity;
            bool show = PlaceCallout.Shows(true, distance);
            _panel.SetActive(show);
            if (show) Place(Top(bounds));
        }

        private void Retire()
        {
            _retired = true;
            DestroyPanel();
            enabled = false;
        }

        private void EnsurePanel()
        {
            if (_panel != null) return;
            ModeSwitchStyle style = ModeSwitchStyle.Current;
            var rect = UiMetrics.NewCanvas("PlaceCallout", null, UiMetrics.Room, Size);
            ControlPanelKit.AddPanel(rect, UiTheme.RadiusCard);
            float inner = Size.x - UiTheme.PanelPadding * 2f;
            ModeSwitchUi.NewLabel("Title", rect, new Vector2(inner, 48f), new Vector2(0f, 26f), PlaceCallout.Title, style.Text, TextAlignmentOptions.Midline);
            ModeSwitchUi.NewLabel("Hint", rect, new Vector2(inner, 44f), new Vector2(0f, -28f), PlaceCallout.Hint, style.Warning, TextAlignmentOptions.Midline);
            _panel = rect.gameObject;
        }

        private void Place(Vector3 position)
        {
            _panel.transform.position = position;
            UiMetrics.Face(_panel.transform, _eye, UiMetrics.Room, (RectTransform)_panel.transform);
        }

        private Bounds ObjectBounds()
        {
            bool any = false;
            Bounds bounds = new Bounds(transform.position, Vector3.one * 0.2f);
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>())
            {
                if (renderer is TrailRenderer || renderer is LineRenderer || renderer is ParticleSystemRenderer) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        private static Vector3 Top(Bounds bounds) => new Vector3(bounds.center.x, bounds.max.y + 0.16f, bounds.center.z);

        private static Transform Eye()
        {
            var rig = Object.FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null) return rig.centerEyeAnchor;
            return Camera.main != null ? Camera.main.transform : null;
        }

        private void DestroyPanel()
        {
            if (_panel == null) return;
            if (Application.isPlaying) Destroy(_panel);
            else DestroyImmediate(_panel);
            _panel = null;
        }
    }
}
