// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using Oculus.Interaction;
using UnityEngine;
using SpatialBlend.Interactions.Annotation;

namespace SpatialBlend.Interactions
{
    public class ObjectPlacementMotif : PlacementMotif
    {
        [Header("ISDK Interaction")]
        [SerializeField, Tooltip("Assign from prefab or auto-found at runtime.")]
        private RayInteractable _rayInteractable;

        [Header("Placement Settings")]
        // As in v1: off by default. An object without it keeps
        // the height it was released at and is only clamped to the walkable region. The chessboard turns it on.
        [SerializeField] private bool _snapOn = false;
        [SerializeField] private float _hoverDistance = 0.1f;

        [Header("SFX")]
        [SerializeField] private AudioClip _objectPlacedClip;
        [SerializeField, Range(0f, 1f)] private float _sfxVolume = 0.5f;
        [SerializeField, Tooltip("Clearance (metres) kept between the authored region and the registered walkable boundary and holes.")]
        private float _walkableSnapMargin = 0.2f;

        protected override float WalkableMargin => _walkableSnapMargin;

        /// <summary>A snapping object (the chessboard) spawns at v1's absolute hover height, as its drop does (<see cref="SnapTarget"/>).</summary>
        public override float SpawnHeight => _snapOn ? _hoverDistance : base.SpawnHeight;

        [Header("Grab controls")]
        [SerializeField, Tooltip("Right thumbstick left/right turns a grabbed object about vertical, up to this many degrees per second.")]
        private float _turnDegreesPerSecond = 90f;

        /// <summary>Thumbstick deflection under this is ignored, so resting a thumb on the stick does nothing.</summary>
        public const float StickDeadZone = 0.25f;
        /// <summary>Push/pull of a grabbed object with the right thumbstick up/down: the panel's values (<c>PanelPlacementMotif</c>), so both grabs feel alike.</summary>
        public const float PushPullMetresPerSecond = 2.5f;
        public const float MinGrabDistance = 0.3f;
        public const float MaxGrabDistance = 10f;

        // Whether the user has turned the object: then every release keeps its yaw instead of facing the mover, until a recenter.
        private bool _userOriented;

        /// <summary>
        /// The grab's thumbstick-shaped input, each frame of a grab. Null reads <see cref="EditInput.GrabAdjust"/> for the pointer that holds the object:
        /// the right thumbstick, or for a hand its wrist twist and reach. Replaced by a test.
        /// </summary>
        internal Func<Vector2> StickSource { get; set; }

        /// <summary>Whether the user has turned the object, so a release keeps its yaw (until <see cref="PlaceInFrontOfUser"/>).</summary>
        public bool KeepsUserYaw => _userOriented;

        /// <summary>The current grab distance along the ray, for tests.</summary>
        internal float GrabDistance => _grabDistance;

        private Vector3 _hitPoint;
        private Vector3 _laggedHitPoint;

        private Coroutine _placementCoroutine;
        private Collider _collider;

        // Grab state - track which pointer (controller ray or hand ray) is grabbing and at what distance
        private EditPointer _selectingPointer;
        private float _grabDistance;
        private bool _subscribed;

        protected override void Awake()
        {
            base.Awake();
            // Collider is on parent entity, not on this prefab
            _collider = GetComponentInParent<Collider>();
            if (_objectPlacedClip == null) _objectPlacedClip = Resources.Load<AudioClip>("SFX/ObjectPlaced");
        }

        protected override void Start()
        {
            base.Start();

            // Find RayInteractable - either wired by SbEntity or on this object
            // As in v1: the lookup goes down the hierarchy, not up.
            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponentInChildren<RayInteractable>();
            }

            if (_rayInteractable == null)
            {
                Debug.LogError($"[ObjectPlacementMotif:{name}] No RayInteractable found!");
            }

            // Listen to the active editing input: the right controller's ray or the right hand's
            Subscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            EditInput.Hovered += OnInteractorHover;
            EditInput.Unhovered += OnInteractorUnhover;
            EditInput.Selected += OnInteractorSelect;
            EditInput.Unselected += OnInteractorUnselect;
        }

        private void OnDestroy()
        {
            if (!_subscribed) return;
            _subscribed = false;
            EditInput.Hovered -= OnInteractorHover;
            EditInput.Unhovered -= OnInteractorUnhover;
            EditInput.Selected -= OnInteractorSelect;
            EditInput.Unselected -= OnInteractorUnselect;
        }

        internal void OnInteractorHover(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
        }

        internal void OnInteractorUnhover(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
        }

        internal void OnInteractorSelect(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            if (!AcceptsSelection(IsFocused)) return;

            ClaimExclusive();

            // Store the selecting interactor and calculate grab distance
            _selectingPointer = pointer;
            var ray = pointer.Ray;
            _grabDistance = Vector3.Distance(ray.origin, _entityTransform.position);

            if (_placementCoroutine != null) { StopCoroutine(_placementCoroutine); _placementCoroutine = null; }
        }

        internal void OnInteractorUnselect(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            if (!_isGrabbed) return;

            // Clear selecting pointer
            _selectingPointer = null;

            if (!PerformRaycastAndSnap())
            {
                _targetPose = new Pose(_entityTransform.position, _entityTransform.rotation);
            }

            UpdateIndicatorVisibility(false);
            ReleaseExclusive();
            // Letting go is this room's placement of a scan the share had dropped on the floor.
            GetComponentInParent<UnplacedSharedScan>()?.SpecifyPosition();
            if (_objectPlacedClip != null)
                AudioSource.PlayClipAtPoint(_objectPlacedClip, _entityTransform.position, _sfxVolume);
            ReportLastDrop();
        }

        protected override void Update()
        {
            if (!IsFocused) return;
            base.Update();

            if (ReleaseIfModeBlocked()) return;

            if (!_isGrabbed)
            {
                // B in Editing undoes the last move; only the motif of that move listens, so one press undoes once.
                if (ObjectMoveHistory.Last == this && EditInput.UndoDown && ObjectMoveHistory.CanUndoNow())
                    ObjectMoveHistory.TryUndo();
                return;
            }

            StepGrab(Time.deltaTime);
            TickPreview(Time.unscaledTime);

            UpdatePlacementIndicator();
            UpdateLineRenderer();
        }

        /// <summary>
        /// One frame of a grab: the thumbstick-shaped input (the stick, or a hand's wrist twist and reach) turns or pushes the object, then the
        /// object follows the ray of the pointer that holds it. The same for a controller ray and a hand ray.
        /// </summary>
        internal void StepGrab(float deltaTime)
        {
            ApplyGrabStick(StickSource != null ? StickSource() : EditInput.GrabAdjust(_selectingPointer), deltaTime);
            if (_selectingPointer == null || _entityTransform == null) return;
            var ray = _selectingPointer.Ray;
            var targetPos = ray.origin + ray.direction * _grabDistance;
            _entityTransform.position = Vector3.Lerp(_entityTransform.position, targetPos, deltaTime * 15f);
        }

        /// <summary>
        /// The mode no longer allows placement while the user may be holding the object (as in v1):
        /// stop the settle animation, drop the selecting interactor and the target, hide the indicator. The base
        /// <see cref="PlacementMotif.Update"/> has already given up the exclusive claim.
        /// </summary>
        /// <summary>True while the mode blocks placement, after dropping any pending grab state (v1 :163-165).</summary>
        internal bool ReleaseIfModeBlocked()
        {
            if (IsModeAllowed) return false;
            ForceReleaseForModeBlock();
            return true;
        }

        /// <summary>Whether a select event starts a grab: the app has focus and the mode allows placement (v1 :123).</summary>
        internal bool AcceptsSelection(bool focused) => focused && IsModeAllowed && !IsOwnScan;

        private void ForceReleaseForModeBlock()
        {
            if (_placementCoroutine != null)
            {
                StopCoroutine(_placementCoroutine);
                _placementCoroutine = null;
            }

            _selectingPointer = null;
            _targetPose = null;
            UpdateIndicatorVisibility(false);
        }

        /// <summary>
        /// One frame of the right thumbstick during a grab. The stronger axis wins, so pushing the object away does not also turn it:
        /// left/right turns the object about world up (right turns it clockwise seen from above), up/down pushes it away or pulls it in along
        /// the ray, clamped like a panel. Outside a grab it does nothing.
        /// </summary>
        internal void ApplyGrabStick(Vector2 stick, float deltaTime)
        {
            if (!_isGrabbed || _entityTransform == null) return;
            bool turn = Mathf.Abs(stick.x) >= Mathf.Abs(stick.y);
            if (turn && Mathf.Abs(stick.x) > StickDeadZone)
            {
                _entityTransform.Rotate(Vector3.up, stick.x * _turnDegreesPerSecond * deltaTime, Space.World);
                _userOriented = true;
            }
            else if (!turn && Mathf.Abs(stick.y) > StickDeadZone)
            {
                _grabDistance = Mathf.Clamp(_grabDistance + stick.y * PushPullMetresPerSecond * deltaTime, MinGrabDistance, MaxGrabDistance);
            }
        }

        /// <summary>Whether the object is being held by a grab.</summary>
        public bool IsHeld => _isGrabbed;

        /// <summary>Held, or settling in its release animation (SmoothMoveToTarget).</summary>
        public override bool InMotion => _isGrabbed || _placementCoroutine != null;

        // Disabled mid-settle: Unity stops the coroutine but would leave its handle, so InMotion would stay true
        // and the pose would never be pushed. The settle is finished at its target at once and the handle dropped.
        protected override void OnDisable()
        {
            base.OnDisable();
            if (_placementCoroutine == null) return;
            StopCoroutine(_placementCoroutine);
            _placementCoroutine = null;
            if (_targetPose.HasValue && _entityTransform != null)
                _entityTransform.SetPositionAndRotation(_targetPose.Value.position, _targetPose.Value.rotation);
            _targetPose = null;
        }

        // A pause or a loss of focus (the Quest system menu) mid-grab is a drop: snapped to the floor and placed
        // by the one rule, as letting go is, never left floating at hand height.
        internal override void ReleaseGrab()
        {
            if (!_isGrabbed) return;
            _selectingPointer = null;
            _targetPose = null;
            if (_centerEyeAnchor == null || _entityTransform == null || !PerformRaycastAndSnap())
                _targetPose = _entityTransform != null ? new Pose(_entityTransform.position, _entityTransform.rotation) : (Pose?)null;
            UpdateIndicatorVisibility(false);
            ReleaseExclusive();
            ReportLastDrop();
        }

        // Where the object was when the current grab began (committed to the undo history only if the grab moved or turned it).
        private Pose? _poseBeforeGrab;

        protected override void OnExclusiveClaimed()
        {
            base.OnExclusiveClaimed();
            if (_entityTransform == null) return;
            // A grab that interrupts a settle undoes to where the object was settling, not to the frame it was caught in.
            _poseBeforeGrab = PoseBeforeGrab(_placementCoroutine != null, _targetPose,
                new Pose(_entityTransform.position, _entityTransform.rotation));
            SetGrabStart(_poseBeforeGrab.Value);
        }

        protected override void OnExclusiveReleased()
        {
            base.OnExclusiveReleased();
            // A grab that left the object where it was (a press, no drag) does not replace the move B would undo.
            // A release the rule placed nowhere goes back where it was: nothing moved, nothing to undo.
            bool reverted = _lastDrop.HasValue && _lastDrop.Value.Reverted;
            if (_poseBeforeGrab.HasValue && _entityTransform != null && !reverted &&
                ObjectMoveHistory.Moved(_poseBeforeGrab.Value, new Pose(_entityTransform.position, _entityTransform.rotation)))
                ObjectMoveHistory.Record(this, _poseBeforeGrab.Value);
            _poseBeforeGrab = null;
        }

        /// <summary>Where an undo of this grab returns: the settle target when the grab caught the object settling, else where it is.</summary>
        internal static Pose PoseBeforeGrab(bool settling, Pose? settleTarget, Pose current) =>
            settling && settleTarget.HasValue ? settleTarget.Value : current;

        /// <summary>Puts the object at <paramref name="pose"/> at once, ending any settle in progress (the undo of a move).</summary>
        internal void RestorePose(Pose pose)
        {
            if (_entityTransform == null) return;
            if (_placementCoroutine != null)
            {
                StopCoroutine(_placementCoroutine);
                _placementCoroutine = null;
            }
            _targetPose = null;
            _entityTransform.SetPositionAndRotation(pose.position, pose.rotation);
            UpdateIndicatorVisibility(false);
            if (_objectPlacedClip != null && Application.isPlaying)
                AudioSource.PlayClipAtPoint(_objectPlacedClip, _entityTransform.position, _sfxVolume);
        }

        /// <summary>
        /// The rotation an object settles to on release: the yaw the user turned it to, or (never turned, v1) facing the viewer.
        /// </summary>
        internal static Quaternion ReleaseRotation(bool userOriented, Quaternion current, Quaternion facingViewer) =>
            userOriented ? current : facingViewer;

        /// <summary>A recenter places the object facing the user again, so later releases face the viewer as before (v1).</summary>
        public override void PlaceInFrontOfUser()
        {
            base.PlaceInFrontOfUser();
            _userOriented = false;
            ObjectMoveHistory.Forget(this);
        }

        /// <summary>What releasing the object does (as v1 PerformRaycastAndSnap).</summary>
        internal enum ReleasePlan
        {
            /// <summary>Nothing moves.</summary>
            Stay,
            /// <summary>No floor raycast: clamp the position it was released at to the walkable region and face the viewer.</summary>
            ClampInPlace,
            /// <summary>Raycast the floor and settle onto it, when it is near enough.</summary>
            SnapToFloor,
        }

        internal static ReleasePlan PlanRelease(bool snapOn, bool clampToWalkable)
        {
            if (snapOn) return ReleasePlan.SnapToFloor;
            return clampToWalkable ? ReleasePlan.ClampInPlace : ReleasePlan.Stay;
        }

        /// <summary>Where a snapping object settles: over the floor hit, at the absolute height <paramref name="hoverDistance"/> (v1 :224).</summary>
        internal static Vector3 SnapTarget(Vector3 floorHit, float hoverDistance) =>
            new Vector3(floorHit.x, hoverDistance, floorHit.z);

        /// <summary>The floor under a ray (the drop's raycast). Null reads <see cref="EnvironmentRaycastUtility.RaycastFloor"/>; replaced by a test.</summary>
        internal Func<Ray, float, Vector3?> FloorSource { get; set; }

        private bool FloorHit(Ray ray, float maxDistance, out Vector3 point)
        {
            if (FloorSource != null)
            {
                Vector3? hit = FloorSource(ray, maxDistance);
                point = hit ?? Vector3.zero;
                return hit.HasValue;
            }
            bool ok = EnvironmentRaycastUtility.RaycastFloor(ray, out var floorResult, maxDistance);
            point = floorResult.Point;
            return ok;
        }

        // The drop of the release in progress, reported once the object is let go.
        private PlacementDrop? _lastDrop;

        private void ReportLastDrop()
        {
            PlacementDrop? drop = _lastDrop;
            _lastDrop = null;
            if (drop.HasValue) ReportDrop(drop.Value);
        }

        /// <summary>
        /// The release: the drop's plan (<see cref="PlanDrop"/>), resolved with what the preview showed (<see cref="PlacementMotif.ResolveRelease"/>),
        /// settled into with the release animation when it settles.
        /// </summary>
        private bool PerformRaycastAndSnap()
        {
            PlacementDrop? drop = ResolveRelease(PlanDrop(false));
            _lastDrop = drop;
            if (!drop.HasValue || !drop.Value.Settles) return false;
            _targetPose = drop.Value.Landed;
            _placementCoroutine = StartCoroutine(SmoothMoveToTarget());
            return true;
        }

        /// <summary>How far below a snapping object its drop looks for the floor (let go at any height, it is set on the floor).</summary>
        public const float FloorSearchMeters = 5f;

        /// <summary>
        /// What letting go now does (as v1 PerformRaycastAndSnap): ClampInPlace pulls the pose it is let go at; SnapToFloor raycasts
        /// the floor (up to <see cref="FloorSearchMeters"/> below), sets it at the absolute hover height facing the viewer (or its own
        /// yaw), and places that by the one rule: let go at any height, it is set on the floor. No floor below it:
        /// the plan does not settle, and the release (<see cref="PlacementMotif.ResolveRelease"/>) takes it to the hologram shown or back.
        /// </summary>
        internal override PlacementDrop? PlanDrop(bool preview)
        {
            if (_entityTransform == null) return null;
            var start = new Pose(_entityTransform.position, _entityTransform.rotation);
            var stays = new PlacementDrop(this, start, start, false, false, false);
            ReleasePlan plan = PlanRelease(_snapOn, _clampToWalkable);
            if (plan == ReleasePlan.Stay || _centerEyeAnchor == null) return stays;
            if (plan == ReleasePlan.ClampInPlace)
            {
                Pose candidate = ClampCandidate();
                Pose landed = PlaceDropInRoom(candidate, WalkableMargin, preview, out bool placedInPlace, out bool approxInPlace);
                return new PlacementDrop(this, candidate, landed, true, placedInPlace, false, approxInPlace);
            }

            var rayOrigin = start.position + Vector3.up * 0.3f;
            if (!FloorHit(new Ray(rayOrigin, Vector3.down), FloorSearchMeters, out Vector3 hitPoint))
            {
                if (InteractionModeManager.DebugLog && !preview) Debug.LogWarning($"[ObjectPlacementMotif:{name}] RaycastFloor failed - rayOrigin={rayOrigin}");
                return stays;
            }

            // The snap height is v1's absolute _hoverDistance, not the hit height plus the hover plus the collider's offset.
            var targetPosition = SnapTarget(hitPoint, _hoverDistance);
            // Billboard Rotation
            var forward = Vector3.ProjectOnPlane(_centerEyeAnchor.position - targetPosition, Vector3.up).normalized;
            var baseYaw = Quaternion.LookRotation(forward);
            var snapped = new Pose(targetPosition, ReleaseRotation(_userOriented, start.rotation, baseYaw * Quaternion.Euler(_rotationOffset)));

            bool placed = true, approximate = false;
            Pose target = snapped;
            // The snapped pose, placed by the one rule (pulled, turned when no translation fits).
            if (_clampToWalkable) target = PlaceDropInRoom(snapped, WalkableMargin, preview, out placed, out approximate);
            return new PlacementDrop(this, snapped, target, true, placed, false, approximate);
        }

        // The ClampInPlace release's pose before the rule: where it is, facing the viewer from there unless the user turned it.
        private Pose ClampCandidate()
        {
            Vector3 at = _entityTransform.position;
            var fwd = Vector3.ProjectOnPlane(_centerEyeAnchor.position - at, Vector3.up).normalized;
            var facing = Quaternion.LookRotation(fwd) * Quaternion.Euler(_rotationOffset);
            return new Pose(at, ReleaseRotation(_userOriented, _entityTransform.rotation, facing));
        }

        /// <summary>
        /// The ClampInPlace release: no raycast; the current position clamped to the walkable region, facing the viewer from there, unless the
        /// user turned the object during the grab, when it keeps its yaw.
        /// </summary>
        internal Pose ClampInPlacePose() => PlaceDropInRoom(ClampCandidate(), WalkableMargin);

        private IEnumerator SmoothMoveToTarget()
        {
            float elapsed = 0f;
            float smoothTime = 1.5f;
            Vector3 startPos = _entityTransform.position;
            Quaternion startRot = _entityTransform.rotation;
            Vector3 _targetPosition = _targetPose.Value.position;
            Quaternion _targetRotation = _targetPose.Value.rotation;

            while (elapsed < smoothTime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / smoothTime;
                float easedT = 1f - Mathf.Pow(2, -10 * t); // EaseOutExpo

                _entityTransform.position = Vector3.Lerp(startPos, _targetPosition, easedT);
                _entityTransform.rotation = Quaternion.Slerp(startRot, _targetRotation, easedT);
                yield return null;
            }
            _entityTransform.position = _targetPosition;
            _entityTransform.rotation = _targetRotation;
            _placementCoroutine = null; // settled: a later grab's undo reads the object, not this target
        }

        private void UpdatePlacementIndicator()
        {
            // Use EnvironmentRaycastUtility for floor detection with MRUK fallback
            if (!EnvironmentRaycastUtility.RaycastFloor(new Ray(_entityTransform.position, Vector3.down), out var floorResult, FloorSearchMeters))
            {
                UpdateIndicatorVisibility(false);
                return;
            }

            _hitPoint = floorResult.Point;
            float d = Vector3.Distance(_entityTransform.position, _hitPoint);
            // The line to the floor shows wherever a drop snaps (any height above a floor), not below the hover height.
            UpdateIndicatorVisibility(d >= _hoverDistance);
        }

        private void UpdateLineRenderer()
        {
            _laggedHitPoint = Vector3.Lerp(_laggedHitPoint, _hitPoint, Time.deltaTime / 0.15f);
            var bounds = _collider.bounds;
            var end = bounds.center - new Vector3(0, bounds.extents.y, 0);
            DrawLine(_laggedHitPoint, end);
        }
    }
}
