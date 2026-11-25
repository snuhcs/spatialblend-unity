// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Where the control panel sits when the input is the left HAND. The panel was authored on the left controller anchor
    /// (<see cref="UiTheme.HandPanelOffset"/>, <see cref="UiTheme.MetresPerUnit"/>, <see cref="UiTheme.HandPanelTiltDegrees"/>). With hand tracking that
    /// anchor is untracked, so the panel hung wherever the rig last left it, often at the origin. This puts it over the left palm instead, with the
    /// same clearance and the same scale and tilt, so its angular size and its distance from what holds it are what the controller placement fixed.
    /// <para>The clearance is the controller's, measured from the hand's real surface. The controller panel's bottom-centre is the controller model's top
    /// plus <see cref="UiTheme.HandPanelClearanceMetres"/> plus 7 mm above the anchor, and 7 cm along the aim. Here the "above" is the palm's normal
    /// from the hand's collision volume (<see cref="ControlPanelHandPlacement.HandHalfThicknessMetres"/> + margin over the joint plane, then the same 5 cm + 7 mm),
    /// the "along the aim" is toward the knuckles, and the panel is shifted to the thumb side by the hand's half width, so the lowest row clears the
    /// hand and the other hand reaches it from the open side (measured from the joint plane, inside the hand, the clearance is too small and the lowest
    /// buttons cannot be pressed). Assumed, not yet seen on a device: that a user glances at the palm held up as they glance at a controller's top.</para>
    /// </summary>
    public static class ControlPanelHandPlacement
    {
        /// <summary>The 7 mm that the controller placement adds to the clearance (<see cref="UiTheme.HandPanelOffset"/>.y = model top 0.0133 + 0.05 + 7 mm, rounded to 0.07).</summary>
        public const float ExtraAboveMetres = 0.007f;

        /// <summary>
        /// Half the thickness of a hand held palm up, from the joint plane (the plane through the wrist and the knuckles) to the palm's surface
        /// (and to the knuckles' tops): 2 cm. A palm is about 3 cm thick and the knuckle joints sit inside it; with a fingertip's 1 cm radius the
        /// hand never reaches further than 2 cm from the plane. A measured-size assumption, not yet checked on a device.
        /// </summary>
        public const float HandHalfThicknessMetres = 0.02f;

        /// <summary>Knuckle (proximal joint) to fingertip, adult middle finger: 9 cm. Used when the tip joint is not given.</summary>
        public const float FingerLengthMetres = 0.09f;

        /// <summary>
        /// The margin added around the joints for the hand's collision volume: 2 cm. Meta's hands UI guide sets the smallest poke target at about
        /// 22 mm of physical size, so a finger of the other hand needs about that much room beside any surface it presses; the tracking jitter of
        /// a few millimetres rides on top.
        /// </summary>
        public const float HandMarginMetres = 0.02f;

        /// <summary>
        /// How high the panel's bottom edge stands over the joint plane: the top of the hand's collision volume (half thickness + margin = 4 cm), then
        /// the same clearance the controller placement leaves between the controller's highest point and the panel (<see cref="UiTheme.HandPanelClearanceMetres"/> + 7 mm), 9.7 cm
        /// in all. Measured from the joint plane, which is inside the hand, the real gap would be 4 cm short of the controller's
        /// (too close: the bottom buttons cannot be pressed).
        /// </summary>
        public const float BottomEdgeAboveJointPlaneMetres = HandHalfThicknessMetres + HandMarginMetres + UiTheme.HandPanelClearanceMetres + ExtraAboveMetres;

        /// <summary>
        /// The hand's collision volume: a box in the palm frame (x across, y out of the palm, z toward the fingers) around the wrist, the palm, and the
        /// fingers to their tips, grown by <see cref="HandMarginMetres"/>. The panel's bottom row must not intersect it, and a ray to a button must not
        /// pass through it.
        /// </summary>
        public readonly struct HandVolume
        {
            public readonly Vector3 Centre;
            public readonly Quaternion Rotation;
            public readonly Vector3 HalfExtents;

            public HandVolume(Vector3 centre, Quaternion rotation, Vector3 halfExtents)
            {
                Centre = centre;
                Rotation = rotation;
                HalfExtents = halfExtents;
            }

            public Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(Rotation) * (world - Centre);

            public bool Contains(Vector3 world)
            {
                Vector3 p = ToLocal(world);
                return Mathf.Abs(p.x) <= HalfExtents.x && Mathf.Abs(p.y) <= HalfExtents.y && Mathf.Abs(p.z) <= HalfExtents.z;
            }

            /// <summary>Whether the planar quad (four world corners in order) shares any point with the box, by sampling it on a fine grid.</summary>
            public bool IntersectsQuad(IReadOnlyList<Vector3> corners, int samples = 40)
            {
                Vector3 a = corners[0], u = corners[1] - corners[0], v = corners[3] - corners[0];
                for (int i = 0; i <= samples; i++)
                    for (int j = 0; j <= samples; j++)
                        if (Contains(a + u * (i / (float)samples) + v * (j / (float)samples))) return true;
                return false;
            }

            /// <summary>Whether the segment from <paramref name="from"/> to <paramref name="to"/> passes through the box (a ray blocked by the hand).</summary>
            public bool IntersectsSegment(Vector3 from, Vector3 to)
            {
                Vector3 a = ToLocal(from), b = ToLocal(to), d = b - a;
                float t0 = 0f, t1 = 1f;
                for (int axis = 0; axis < 3; axis++)
                {
                    float min = -HalfExtents[axis], max = HalfExtents[axis], o = a[axis], dir = d[axis];
                    if (Mathf.Abs(dir) < 1e-9f)
                    {
                        if (o < min || o > max) return false;
                        continue;
                    }
                    float ta = (min - o) / dir, tb = (max - o) / dir;
                    if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
                    t0 = Mathf.Max(t0, ta);
                    t1 = Mathf.Min(t1, tb);
                    if (t0 > t1) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// The root pose of the panel over a hand, from the world positions of its wrist and its index, middle and pinky proximal joints (the knuckles).
        /// The root's x is across the palm, y the palm's normal, z toward the fingers, so the canvas tilt applied below the root is the controller's.
        /// Returns false when the joints do not describe a hand (coincident points).
        /// </summary>
        public static bool TryCompute(Vector3 wrist, Vector3 index1, Vector3 middle1, Vector3 pinky1, bool leftHand, out Pose root) =>
            TryCompute(wrist, index1, middle1, pinky1, leftHand, null, out root, out _);

        /// <summary>
        /// As above, with the hand's collision volume. <paramref name="middleTip"/> is the middle fingertip when it is tracked; without it the finger is
        /// <see cref="FingerLengthMetres"/> long. The panel's bottom-centre sits <see cref="BottomEdgeAboveJointPlaneMetres"/> over the knuckle line, shifted to the
        /// thumb side by the hand's half width plus its margin: above the hand and beside it, so the other hand reaches the lowest row from the open side
        /// and the lowest row clears the hand (the controller case keeps its own placement).
        /// </summary>
        public static bool TryCompute(Vector3 wrist, Vector3 index1, Vector3 middle1, Vector3 pinky1, bool leftHand, Vector3? middleTip,
                                      out Pose root, out HandVolume volume)
        {
            root = default;
            volume = default;
            Vector3 along = middle1 - wrist;
            if (along.sqrMagnitude < 1e-8f) return false;
            Vector3 forward = along.normalized;
            // Thumb side to little-finger side. Left hand, palm down, fingers away: the thumb is to the right, so this points left, and
            // forward x left points down: out of the palm. The right hand mirrors it.
            Vector3 across = pinky1 - index1;
            Vector3 normal = Vector3.Cross(forward, across);
            if (normal.sqrMagnitude < 1e-10f) return false;
            normal.Normalize();
            if (!leftHand) normal = -normal;
            Vector3 acrossUnit = Vector3.Cross(normal, forward);   // palm frame x, orthogonal to the other two
            Quaternion frame = Quaternion.LookRotation(forward, normal);

            Vector3 centre = (wrist + index1 + middle1 + pinky1) * 0.25f;
            float toKnuckles = Vector3.Dot(middle1 - centre, forward);
            float halfWidth = across.magnitude * 0.5f + HandMarginMetres;
            // acrossUnit points to the thumb side for a left hand (x of the frame is right of forward x up); the thumb side is what "beside" means.
            Vector3 thumbSide = Vector3.Dot(acrossUnit, pinky1 - index1) > 0f ? -acrossUnit : acrossUnit;
            Vector3 position = centre + forward * toKnuckles + normal * BottomEdgeAboveJointPlaneMetres + thumbSide * halfWidth;
            root = new Pose(position, frame);

            float backZ = Vector3.Dot(wrist - centre, forward) - HandMarginMetres;
            float finger = middleTip.HasValue ? Mathf.Max(FingerLengthMetres * 0.5f, Vector3.Dot(middleTip.Value - middle1, forward)) : FingerLengthMetres;
            float frontZ = toKnuckles + finger + HandMarginMetres;
            Vector3 volumeCentre = centre + forward * ((backZ + frontZ) * 0.5f);
            volume = new HandVolume(volumeCentre, frame, new Vector3(halfWidth, HandHalfThicknessMetres + HandMarginMetres, (frontZ - backZ) * 0.5f));
            return true;
        }
    }

    /// <summary>
    /// Keeps the left hand's own Interaction SDK interactors (ray, poke, grab) off the control panel: the left hand holds the panel, it is
    /// not the pointer. Each is given a filter that refuses any interactable under the panel, so the left hand's fingers can neither press a control
    /// nor take a hover or selection away from the right hand's ray.
    /// </summary>
    public sealed class ControlPanelInteractableFilter : MonoBehaviour, IGameObjectFilter
    {
        /// <summary>The panel root; interactables under it are refused.</summary>
        public Transform Panel;

        public bool Filter(GameObject gameObject) => Panel == null || gameObject == null || !gameObject.transform.IsChildOf(Panel);

        /// <summary>
        /// Adds the filter to every interactor of <paramref name="interactors"/> that <paramref name="isLeftHand"/> accepts and that does not carry it
        /// yet (an interactor's own filters are kept). Returns how many were changed.
        /// </summary>
        public static int Apply(ControlPanelInteractableFilter filter, IEnumerable<Component> interactors, Func<Component, bool> isLeftHand)
        {
            int changed = 0;
            foreach (Component interactor in interactors)
            {
                if (interactor == null || !isLeftHand(interactor)) continue;
                MethodInfo inject = interactor.GetType().GetMethod("InjectOptionalInteractableFilters", BindingFlags.Public | BindingFlags.Instance);
                if (inject == null) continue;
                var filters = new List<IGameObjectFilter>();
                for (Type t = interactor.GetType(); t != null; t = t.BaseType)
                {
                    FieldInfo field = t.GetField("_interactableFilters", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (field == null) continue;
                    if (field.GetValue(interactor) is IEnumerable existing)
                        foreach (object item in existing) if (item is IGameObjectFilter g) filters.Add(g);
                    break;
                }
                if (filters.Contains(filter)) continue;
                filters.Add(filter);
                inject.Invoke(interactor, new object[] { filters });
                changed++;
            }
            return changed;
        }
    }

    /// <summary>
    /// Keeps the control panel on the input in use: on the left controller anchor when a left controller is in use, over the left palm when
    /// the left hand is. The panel is a child of the controller anchor (that is how the controller placement puts it), so a controller needs nothing from this
    /// component; it only takes over when that anchor cannot be trusted.
    /// <list type="bullet">
    /// <item>Switching input blends from where the panel is to where the new input puts it over <see cref="BlendSeconds"/>, so it never pops.</item>
    /// <item>When tracking is lost the panel holds its last valid pose; it never jumps to the anchor's stale or origin pose.</item>
    /// <item>Before any valid pose (a hand not yet seen) the panel is hidden, not shown at the origin.</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class ControlPanelAnchor : MonoBehaviour
    {
        public const float BlendSeconds = 0.2f;

        public enum Source { Controller, Hand }

        /// <summary>Whether a left controller is in use. Replaced by a test; the default asks OVRInput (the Editor with no headset counts as one).</summary>
        public Func<bool> LeftControllerInUse { get; set; } = () => !OVRManager.isHmdPresent || (OVRInput.GetActiveController() & OVRInput.Controller.LTouch) != 0;
        /// <summary>Whether the left controller's pose is tracked right now. Replaced by a test.</summary>
        public Func<bool> LeftControllerTracked { get; set; } = () =>
            !OVRManager.isHmdPresent || (OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch) && OVRInput.GetControllerOrientationTracked(OVRInput.Controller.LTouch));
        /// <summary>Whether the left hand is the input. Replaced by a test; the default asks OVRInput.</summary>
        public Func<bool> LeftHandInUse { get; set; } = () => OVRManager.isHmdPresent && (OVRInput.GetActiveController() & OVRInput.Controller.LHand) != 0;
        /// <summary>The panel's root pose over the left palm, or null when the hand is not tracked. Replaced by a test.</summary>
        public Func<Pose?> HandPose { get; set; }
        /// <summary>The left hand's collision volume, or null when the hand is not tracked. Replaced by a test.</summary>
        public Func<ControlPanelHandPlacement.HandVolume?> HandVolumeSource { get; set; }
        /// <summary>The left hand's collision volume now (what the panel stays clear of), or null.</summary>
        public ControlPanelHandPlacement.HandVolume? HandVolume => HandVolumeSource != null ? HandVolumeSource() : _lastVolume;

        private Transform _controllerAnchor;
        private Source? _source;
        private Pose? _lastValid;
        private Pose _blendFrom;
        private float _blendLeft;
        private bool _overriding, _hadValid, _wasTracking;
        private readonly System.Collections.Generic.List<GameObject> _rootCanvases = new System.Collections.Generic.List<GameObject>();
        private bool _hidden;
        private IHand _hand;
        private float _nextHandSearch;
        private ControlPanelHandPlacement.HandVolume? _lastVolume;
        private ControlPanelInteractableFilter _filter;
        private float _nextGuard;

        public Source? CurrentSource => _source;
        public bool IsHidden => _hidden;
        public bool IsBlending => _blendLeft > 0f;
        public Pose? LastValidPose => _lastValid;

        private void Awake()
        {
            _controllerAnchor = transform.parent;
            if (HandPose == null) HandPose = ReadLeftHand;
        }

        private void LateUpdate() => Step(Time.unscaledDeltaTime);

        /// <summary>One frame; public so a test can drive it with its own time step.</summary>
        public void Step(float deltaTime)
        {
            if (_controllerAnchor == null) _controllerAnchor = transform.parent;
            if (_controllerAnchor == null) return;

            Source source = LeftControllerInUse() || !LeftHandInUse() ? Source.Controller : Source.Hand;
            Pose? target;
            if (source == Source.Controller)
                target = LeftControllerTracked()
                    ? new Pose(_controllerAnchor.TransformPoint(UiTheme.HandPanelOffset), _controllerAnchor.rotation)
                    : (Pose?)null;
            else
            {
                target = HandPose != null ? HandPose() : null;
                GuardLeftHandInteractors();
            }

            // Tracking that comes back after a loss starts from the held pose, like a change of input does.
            bool reacquired = target.HasValue && _hadValid && !_wasTracking && !_hidden;
            _wasTracking = target.HasValue;
            if (target.HasValue) { _lastValid = target; _hadValid = true; }
            if (reacquired && _blendLeft <= 0f && _source == source)
            {
                _blendFrom = new Pose(transform.position, transform.rotation);
                _blendLeft = BlendSeconds;
            }
            if (_source != source)
            {
                if (_source.HasValue && !_hidden)
                {
                    _blendFrom = new Pose(transform.position, transform.rotation);
                    _blendLeft = BlendSeconds;
                }
                _source = source;
            }

            if (!_lastValid.HasValue)
            {
                SetHidden(true);
                return;
            }
            SetHidden(false);

            Pose desired = _lastValid.Value;
            if (_blendLeft > 0f)
            {
                _blendLeft = Mathf.Max(0f, _blendLeft - deltaTime);
                float t = Mathf.SmoothStep(0f, 1f, 1f - _blendLeft / BlendSeconds);
                desired = new Pose(Vector3.Lerp(_blendFrom.position, desired.position, t), Quaternion.Slerp(_blendFrom.rotation, desired.rotation, t));
                Apply(desired);
                return;
            }
            if (source == Source.Controller && target.HasValue)
            {
                // The controller anchor is trusted: the panel goes back to being its child at the controller offset.
                if (_overriding)
                {
                    transform.localPosition = UiTheme.HandPanelOffset;
                    transform.localRotation = Quaternion.identity;
                    _overriding = false;
                }
                return;
            }
            Apply(desired);
        }

        private void Apply(Pose pose)
        {
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            _overriding = true;
        }

        private void SetHidden(bool hidden)
        {
            if (_hidden == hidden) return;
            _hidden = hidden;
            // The panel's root canvases are switched off (they carry the Interaction SDK's canvas interactables too, so no ray hits a hidden panel);
            // the transform is not scaled, because a zero scale has no inverse for the SDK's surfaces.
            if (hidden) _rootCanvases.Clear();
            if (hidden) foreach (var canvas in GetComponentsInChildren<Canvas>(false)) if (canvas.rootCanvas == canvas) _rootCanvases.Add(canvas.gameObject);
            foreach (var go in _rootCanvases) if (go != null) go.SetActive(!hidden);
        }

        /// <summary>Keeps the left hand's interactors off the panel (see <see cref="ControlPanelInteractableFilter"/>); new ones are picked up every 2 s.</summary>
        private void GuardLeftHandInteractors()
        {
            if (!Application.isPlaying || Time.unscaledTime < _nextGuard) return;
            _nextGuard = Time.unscaledTime + 2f;
            if (_filter == null)
            {
                _filter = GetComponent<ControlPanelInteractableFilter>() ?? gameObject.AddComponent<ControlPanelInteractableFilter>();
                _filter.Panel = transform;
            }
            var interactors = new List<Component>();
            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (behaviour is IInteractor) interactors.Add(behaviour);
            ControlPanelInteractableFilter.Apply(_filter, interactors, IsLeftHandInteractor);
        }

        private static bool IsLeftHandInteractor(Component interactor)
        {
            var hand = interactor.GetComponentInParent<IHand>();
            return hand != null && hand.Handedness == Handedness.Left;
        }

        private Pose? ReadLeftHand()
        {
            if (_hand == null && Time.unscaledTime >= _nextHandSearch)
            {
                _nextHandSearch = Time.unscaledTime + 1f;
                foreach (var candidate in FindObjectsByType<HandRef>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (candidate.Handedness == Handedness.Left) { _hand = candidate; break; }
            }
            if (_hand == null || !_hand.IsConnected) return null;
            if (_hand.GetRootPose(out Pose wrist) &&
                _hand.GetJointPose(HandJointId.HandIndex1, out Pose index) &&
                _hand.GetJointPose(HandJointId.HandMiddle1, out Pose middle) &&
                _hand.GetJointPose(HandJointId.HandPinky1, out Pose pinky))
            {
                Vector3? tip = _hand.GetJointPose(HandJointId.HandMiddleTip, out Pose tipPose) ? tipPose.position : (Vector3?)null;
                if (ControlPanelHandPlacement.TryCompute(wrist.position, index.position, middle.position, pinky.position, true, tip, out Pose root, out var volume))
                {
                    _lastVolume = volume;
                    return root;
                }
            }
            _lastVolume = null;
            return null;
        }
    }
}
