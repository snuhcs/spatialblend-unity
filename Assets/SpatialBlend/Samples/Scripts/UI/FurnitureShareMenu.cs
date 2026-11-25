// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// The menu on a piece of furniture. Pressing it (trigger or pinch on the ray) offers "Share when connected". There is no list of anchors.
    /// Every eligible piece of furniture gets a trigger probe the size of its volume when the lobby starts
    /// (<see cref="SetAnchors"/>), on the Ignore Raycast layer: the default raycast mask leaves it out, so only this menu's
    /// ray (which masks to that layer alone) sees them, and the menu is reachable when the effect-mesh colliders are off. The
    /// probes are destroyed on connect (<see cref="Close"/>), so nothing in a session can hit them either. The menu is the control panel's
    /// toggle row on the <see cref="UiMetrics.Room"/> surface (24 dmm text, growing with distance).
    /// </summary>
    public sealed class FurnitureShareMenu : MonoBehaviour
    {
        public const string ToggleLabel = "Share when connected";

        /// <summary>The layer of the probes: excluded from <see cref="Physics.DefaultRaycastLayers"/>. The menu ray masks to it alone.</summary>
        public const string ProbeLayerName = "Ignore Raycast";

        public static int ProbeLayer => LayerMask.NameToLayer(ProbeLayerName);

        private readonly List<SceneAnchorRecord> _anchors = new List<SceneAnchorRecord>();
        private RectTransform _root;
        private Toggle _toggle;
        private SceneAnchorRecord _current;
        private bool _suppress;

        public SceneAnchorRecord Current => _current;
        /// <summary>True once a session has started: the menu belongs to the lobby and closes then. Set by the app.</summary>
        public Func<bool> InSession;
        public event Action Changed;

        public void SetAnchors(IReadOnlyList<SceneAnchorRecord> anchors)
        {
            _anchors.Clear();
            if (anchors != null)
            {
                for (int i = 0; i < anchors.Count; ++i)
                    if (anchors[i] != null && anchors[i].Anchor != null) _anchors.Add(anchors[i]);
            }
            // A probe per eligible anchor from the start: the ray finds the furniture before any menu was shown.
            for (int i = 0; i < _anchors.Count; ++i) EnsureProbe(_anchors[i]);
        }

        /// <summary>The session started (or the lobby closed): the menu hides and every probe is destroyed.</summary>
        /// <summary>How many anchors the menu offers now (0 once closed).</summary>
        internal int AnchorCount => _anchors.Count;

        public void Close()
        {
            Hide();
            for (int i = 0; i < _anchors.Count; ++i)
            {
                if (_anchors[i] == null || _anchors[i].Anchor == null) continue;
                FurnitureShareProbe.Remove(_anchors[i].Anchor.transform);
            }
            _anchors.Clear();
        }

        public void Show(SceneAnchorRecord record)
        {
            if (record == null || record.Anchor == null) { Hide(); return; }
            Ensure();
            _current = record;
            _root.SetParent(record.Anchor.transform, true);
            PlaceAbove(record);
            _root.gameObject.SetActive(true);
            _suppress = true;
            _toggle.SetIsOnWithoutNotify(FurnitureSharePolicy.WillShare(record));
            _suppress = false;
        }

        public void Hide()
        {
            _current = null;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>How long the menu stays open without a press on it or on the furniture.</summary>
        public const float OpenSeconds = 10f;
        /// <summary>The menu closes when the user is farther than this from the furniture it is on.</summary>
        public const float MoveAwayMeters = 3f;
        /// <summary>An interactable this much nearer than the furniture along the ray is something else under the ray.</summary>
        public const float InFrontMeters = 0.05f;

        private float _lastActive;
        private RayInteractable _interactable;
        /// <summary>The user's head position (the main camera by default); tests set it.</summary>
        public Func<Vector3?> UserPosition;

        private void Update()
        {
            if (InSession != null && InSession()) { Close(); return; }
            Vector3? user = UserPosition != null ? UserPosition() : Camera.main != null ? Camera.main.transform.position : (Vector3?)null;
            Step(SpatialBlend.Interactions.EditInput.AcceptDown, SpatialBlend.Interactions.EditInput.ActivePointer,
                SpatialBlend.Interactions.EditInput.Clock(), user);
        }

        /// <summary>
        /// One frame. The menu is hidden until the user PRESSES the furniture with the ray (the trigger, or the pinch: <c>EditInput.AcceptDown</c>
        /// on the active pointer, so controller and hand both work); hovering or pointing shows nothing. It closes on a press anywhere else,
        /// after <see cref="OpenSeconds"/> without one, and when the user walks <see cref="MoveAwayMeters"/> away. A press on the menu itself
        /// is the menu's (the toggle). A press with another interactable in front of the furniture (a virtual object, a widget) is that
        /// one's, not this menu's.
        /// </summary>
        internal void Step(bool pressed, SpatialBlend.Interactions.EditPointer pointer, float now, Vector3? user)
        {
            if (_root != null && _root.gameObject.activeSelf && _current != null)
            {
                PlaceAbove(_current);
                bool timedOut = now - _lastActive > OpenSeconds;
                bool away = user.HasValue && _current.Anchor != null &&
                            Vector3.Distance(user.Value, _current.Anchor.transform.TransformPoint(_current.LocalCenter)) > MoveAwayMeters;
                if (timedOut || away) Hide();
            }
            if (!pressed || pointer == null) return;
            // The box tool owns its presses: the corners while a box is drawn, the trigger while the capture card holds it.
            if (SpatialBlend.Interactions.BoxAnnotationManager.BoxBusy || SpatialBlend.Interactions.BoxAnnotationManager.CreationHeld) return;
            RayInteractable on = pointer.Selected != null ? pointer.Selected : pointer.Hovered;
            if (on != null && on == _interactable) { _lastActive = now; return; }   // the toggle's own press
            float furnitureDistance = 0f, exitDistance = 0f;
            SceneAnchorRecord found = _anchors.Count > 0 && ProbeLayer >= 0 ? FindUnderRay(pointer.Ray, out furnitureDistance, out exitDistance) : null;
            // Something else under the ray: in front of the furniture, or inside its volume past the surface it enters by (a prop resting
            // on it). Its own collider at the entering surface is the furniture itself.
            bool blocked = false;
            if (found != null && on != null && pointer.HitPoint.HasValue)
            {
                float hitDistance = Vector3.Distance(pointer.Origin, pointer.HitPoint.Value);
                bool atSurface = Mathf.Abs(hitDistance - furnitureDistance) <= InFrontMeters;
                blocked = !atSurface && hitDistance < exitDistance;
            }
            if (found != null && !blocked)
            {
                _lastActive = now;
                if (_current == null || _current.Anchor != found.Anchor || _root == null || !_root.gameObject.activeSelf) Show(found);
            }
            else if (_current != null) Hide();   // a press elsewhere
        }

        // The nearest probe on the ray; anything else on the layer is not furniture and is passed over.
        private SceneAnchorRecord FindUnderRay(Ray ray, out float distance, out float exitDistance)
        {
            exitDistance = 0f;
            Collider nearestCollider = null;
            RaycastHit[] hits = Physics.RaycastAll(ray, 20f, 1 << ProbeLayer, QueryTriggerInteraction.Collide);
            SceneAnchorRecord found = null;
            float nearest = float.MaxValue;
            for (int h = 0; h < hits.Length; ++h)
            {
                if (hits[h].distance >= nearest || hits[h].transform.GetComponent<FurnitureShareProbe>() == null) continue;
                for (int i = 0; i < _anchors.Count; ++i)
                {
                    if (!hits[h].transform.IsChildOf(_anchors[i].Anchor.transform)) continue;
                    found = _anchors[i];
                    nearest = hits[h].distance;
                    nearestCollider = hits[h].collider;
                    break;
                }
            }
            distance = nearest;
            if (nearestCollider != null)
            {
                // The far side of the volume: cast back from well beyond it.
                const float Reach = 20f;
                Ray back = new Ray(ray.origin + ray.direction * Reach, -ray.direction);
                exitDistance = nearestCollider.Raycast(back, out RaycastHit exit, Reach) ? Reach - exit.distance : nearest;
            }
            return found;
        }

        private void Ensure()
        {
            if (_root != null) return;
            _root = ControlPanelKit.Stack("FurnitureShare", transform, UiTheme.Space2);
            UiMetrics.MakeWorldSpace(_root.gameObject.AddComponent<Canvas>(), UiMetrics.Room);
            _root.gameObject.AddComponent<GraphicRaycaster>();
            _root.sizeDelta = new Vector2(460f, 120f);
            ToggleRow row = ControlPanelKit.AddToggleRow(_root, ToggleLabel, false, OnToggle);
            _toggle = row.Toggle;
            // The ray presses this the same way it presses a mode-switch button: a collider the size of the row.
            var box = row.Toggle.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(460f, 96f, 40f);
            var surface = row.Toggle.gameObject.AddComponent<ColliderSurface>();
            surface.InjectCollider(box);
            var interactable = row.Toggle.gameObject.AddComponent<RayInteractable>();
            interactable.InjectSurface(surface);
            interactable.WhenStateChanged += OnButton;
            _interactable = interactable;
            _root.gameObject.SetActive(false);
        }

        private void OnButton(InteractableStateChangeArgs args)
        {
            if (args.PreviousState == InteractableState.Select && args.NewState == InteractableState.Hover)
                _toggle.isOn = !_toggle.isOn;
        }

        private static void EnsureProbe(SceneAnchorRecord record) =>
            FurnitureShareProbe.Ensure(record.Anchor.transform, record.LocalCenter, record.LocalSize);

        /// <summary>Above the top face in world up. An MRUK volume's local +Z is up, which is not the parent's local Y.</summary>
        private void PlaceAbove(SceneAnchorRecord record)
        {
            if (record == null || record.Anchor == null || _root == null) return;
            Vector3 top = record.Anchor.transform.TransformPoint(record.LocalCenter + new Vector3(0f, 0f, record.LocalSize.z * 0.5f));
            _root.position = top + Vector3.up * 0.08f;
            Camera cam = Camera.main;
            if (cam == null) { _root.rotation = Quaternion.identity; UiMetrics.Declare(_root, UiMetrics.Room); return; }
            UiMetrics.Face(_root, cam.transform, UiMetrics.Room, _root);
        }

        private void OnToggle(bool share)
        {
            if (_suppress || _current == null) return;
            _lastActive = SpatialBlend.Interactions.EditInput.Clock();
            FurnitureSharePolicy.SetShare(_current, share);
            Changed?.Invoke();
        }
    }
}
