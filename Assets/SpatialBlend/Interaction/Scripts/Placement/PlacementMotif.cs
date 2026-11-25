// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using Meta.XR;
using UnityEngine.Scripting.APIUpdating;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Base class for placement motifs that position objects in front of the user.
    /// Extends InteractionMotif for gizmo mutex functionality.
    /// </summary>
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public abstract class PlacementMotif : InteractionMotif
    {
        [Header("Settings")]
        [SerializeField] protected bool _allowRecenter = true;
        [SerializeField] protected float _forwardOffset = 0.6f;
        [SerializeField] protected Vector3 _positionOffset = Vector3.zero;
        [SerializeField] protected Vector3 _rotationOffset = Vector3.zero;
        [SerializeField] protected bool _isFacingReverse = false;
        [SerializeField] protected bool _clampToWalkable = true;

        [Header("Line Indicator (optional)")]
        [SerializeField] protected LineRenderer _lineIndicator;

        // Auto-found references
        protected Transform _centerEyeAnchor;
        protected EnvironmentRaycastManager _raycastManager;
        protected Transform _entityTransform; // Parent entity's transform (for movement)
        protected SpatialBlend.SbSpatialAuthoring _authoring;
        private SpatialBlend.SbApp _sbApp;

        protected virtual float WalkableMargin => 0f;

        protected Pose? _targetPose;

        protected new bool IsFocused => FocusOverride != null ? FocusOverride() : Application.isFocused;
        protected bool IsEditor => Application.isEditor;

        protected int _raycastLayer;

        protected static int s_RecenterToken;
        protected static int s_LastConsumedToken;

        public bool AllowRecenter { get => _allowRecenter; set => _allowRecenter = value; }
        public Vector3 PositionOffset => _positionOffset;
        public Vector3 RotationOffset => _rotationOffset;
        public bool IsFacingReverse => _isFacingReverse;

        public virtual void Configure(Transform centerEyeAnchor, EnvironmentRaycastManager raycastManager, LineRenderer lineIndicator = null)
        {
            if (centerEyeAnchor != null)
                _centerEyeAnchor = centerEyeAnchor;
            if (raycastManager != null)
                _raycastManager = raycastManager;
            if (lineIndicator != null)
                _lineIndicator = lineIndicator;
        }

        protected virtual void Awake()
        {
            // Auto-find references
            if (_centerEyeAnchor == null)
            {
                OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
                if (rig != null)
                {
                    _centerEyeAnchor = rig.centerEyeAnchor.transform;
                }
                else if (Camera.main != null)
                {
                    _centerEyeAnchor = Camera.main.transform;
                }
            }

            if (_raycastManager == null)
                _raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>();

            if (_lineIndicator != null)
                _lineIndicator.enabled = false;

            // Cache parent entity reference (prefab is child of entity)
            var parentEntity = GetComponentInParent<SpatialBlend.SbEntity>();
            _entityTransform = parentEntity != null ? parentEntity.transform : transform;
            FindAuthoring();
            _sbApp = GetComponentInParent<SpatialBlend.SbApp>();
            _raycastLayer = 1 << _entityTransform.gameObject.layer;
        }

        protected virtual void Start()
        {
            LatestObjectSelection.Set(this);
        }

        protected virtual void Update()
        {
            if (!IsFocused && !IsEditor) return;
            if (!IsModeAllowed)
            {
                if (_isGrabbed)
                {
                    ReleaseExclusive();
                }
                return;
            }
            CheckGlobalRecenter();
        }

        protected override void OnExclusiveClaimed()
        {
            LatestObjectSelection.Set(this);
            // The grab uses the right thumbstick (panels push and pull, objects also turn): the rig's stick locomotion waits.
            LocomotionHold.Hold(this);
            NoteGrabStart();
        }

        protected override void OnExclusiveReleased()
        {
            LocomotionHold.Release(this);
            HidePreview();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_preview != null)
            {
                if (Application.isPlaying) Destroy(_preview.gameObject);
                else DestroyImmediate(_preview.gameObject);
                _preview = null;
            }
        }

        // ---------------------------------------------------------------- the drop and its preview

        /// <summary>
        /// A drop was made (a release, a pause mid-grab, a mode change mid-grab): what the room rule did with it. Raised once per drop,
        /// after the object was let go. The tutorial's placement task is verified by it.
        /// </summary>
        public static event System.Action<PlacementDrop> Dropped;

        protected static void ReportDrop(in PlacementDrop drop) => Dropped?.Invoke(drop);

        /// <summary>
        /// What letting go now does, by the drop's own code: the motif's drop applies it, and the preview shows it. A preview
        /// (<paramref name="preview"/>) leaves the object where it is and asks nothing of the app (it reads the inputs prepared at the
        /// grab's start). Null when the motif has no drop of its own.
        /// </summary>
        internal virtual PlacementDrop? PlanDrop(bool preview) => null;

        /// <summary>The preview is computed at most this often while an object is held (and only when it moved), never every frame.</summary>
        public const float PreviewIntervalSeconds = 0.1f;

        /// <summary>
        /// The prepared inputs (the obstacles: the other objects' regions, the furniture) are taken again this often during a grab, so an
        /// object a remote user moved in a session is seen within this long (visual only: the drop always reads them fresh).
        /// </summary>
        public const float PreviewInputsSeconds = 1f;

        private PlacementPreview _preview;
        private RoomFit.Prepared _previewInputs;
        private bool _previewReady;
        private float _nextPreviewAt;
        private float _preparedAt;

        /// <summary>How many times the preview was computed (the throttle's test).</summary>
        internal int PreviewComputations { get; private set; }
        private Pose _previewFrom;
        private bool _previewFromSet;

        /// <summary>The preview's lines (null until the first grab).</summary>
        internal PlacementPreview Preview => _preview;

        /// <summary>A preview runs with these instead of the room's inputs now: null before the grab's first preview, or when no room or region is known.</summary>
        internal RoomFit.Prepared PreviewInputs => _previewInputs;

        /// <summary>One frame of a grab: the preview is recomputed when it is due (<see cref="PreviewIntervalSeconds"/>) and the object moved.</summary>
        protected internal void TickPreview(float now)
        {
            if (_entityTransform == null || now < _nextPreviewAt) return;
            _nextPreviewAt = now + PreviewIntervalSeconds;
            if (_previewReady && now - _preparedAt >= PreviewInputsSeconds)
            {
                _previewReady = false;
                _previewFromSet = false;   // new inputs: computed again even if the object did not move
            }
            var at = new Pose(_entityTransform.position, _entityTransform.rotation);
            if (_previewFromSet && (at.position - _previewFrom.position).sqrMagnitude < 1e-4f &&
                Quaternion.Angle(at.rotation, _previewFrom.rotation) < 1f) return;
            _previewFrom = at;
            _previewFromSet = true;
            if (!_previewReady) _preparedAt = now;
            RefreshPreview();
        }

        /// <summary>Computes and shows the preview now (the inputs are prepared once per grab). Returns the drop it shows.</summary>
        internal PlacementDrop? RefreshPreview()
        {
            if (_entityTransform == null) return null;
            if (!_previewReady)
            {
                _previewReady = true;
                _previewInputs = SbApp != null ? SbApp.PreparePlacement(_entityTransform.gameObject, WalkableMargin) : null;
            }
            PreviewComputations++;
            PlacementDrop? drop = PlanDrop(true);
            if (_preview == null) _preview = PlacementPreview.Create(name);
            // No inputs: no room known, a physical object, or one with no authored region (its drop falls back to its collider): nothing to show.
            _shownGhost = null;
            if (_previewInputs == null || !drop.HasValue || !drop.Value.Settles)
            {
                _preview.Conceal();
                return drop;
            }
            // The region where it lands: the object stands there for the read and is put back at once.
            var at = new Pose(_entityTransform.position, _entityTransform.rotation);
            _entityTransform.SetPositionAndRotation(drop.Value.Landed.position, drop.Value.Landed.rotation);
            if (!SpatialBlend.RoomFit.TryRegion(_entityTransform.gameObject, out Vector2[] region)) region = null;
            _entityTransform.SetPositionAndRotation(at.position, at.rotation);
            _preview.Show(drop.Value, region, _entityTransform, SbApp != null ? SbApp.PlacementFloorY : 0f);
            // What the user now sees: a hologram where it will land. A release from here lands there (ResolveRelease).
            if (_preview.Visible)
            {
                _shownGhost = drop.Value;
            }
            return drop;
        }

        // ---------------------------------------------------------------- the release lands where the preview showed

        // The hologram shown now (null when none: no inputs, nothing fits, farther than the preview looks) and the pose it was computed from.
        private PlacementDrop? _shownGhost;
        // Where the object was when the grab began (a release the rule cannot place returns it there).
        private Pose _grabStart;
        private bool _grabStartSet;

        /// <summary>The drop the user sees now: the hologram's, or null.</summary>
        internal PlacementDrop? ShownGhost => _shownGhost;

        /// <summary>A grab begins: where the object is now is where a release with no valid spot returns it; the preview starts over.</summary>
        protected internal void NoteGrabStart()
        {
            ResetPreview();
            if (_entityTransform == null) return;
            _grabStart = new Pose(_entityTransform.position, _entityTransform.rotation);
            _grabStartSet = true;
        }

        /// <summary>Overrides where a release with no valid spot returns the object (the settle target when a grab caught it settling).</summary>
        protected void SetGrabStart(Pose pose)
        {
            _grabStart = pose;
            _grabStartSet = true;
        }

        /// <summary>
        /// What a release does (when the snap fails, it goes to the preview position): what the user saw is where
        /// it lands, when it is still valid. Let go where the hologram was computed, it lands exactly at the hologram if the one rule,
        /// checked again now, accepts it there. Otherwise the drop's own plan when the rule placed it; else the hologram still shown, if
        /// valid now. With no valid hologram and no valid spot, the object
        /// goes back where it was before the grab (the one rule found nothing; a pose it rejects would be refused by the core in a
        /// session and reverted there anyway, so the lobby does the same). An object the rule cannot judge (no room known, no authored
        /// region, a physical object) keeps the drop's own plan.
        /// </summary>
        protected PlacementDrop? ResolveRelease(PlacementDrop? planned)
        {
            if (_entityTransform == null) return planned;
            var now = new Pose(_entityTransform.position, _entityTransform.rotation);
            bool plannedPlaced = planned.HasValue && planned.Value.Settles && planned.Value.Placed;
            // Valid where it is let go (cyan, "lands where you let go"): there, not at a hologram up to 0.1 s old.
            if (plannedPlaced && !planned.Value.Pulled) return planned;
            // It would be moved: the hologram shown (so it never goes somewhere other than where the preview shows), checked again
            // now with fresh inputs and the predicate the preview accepted it by (or a looser one), so it fails only if the room really
            // changed. Used whether or not the object moved in the last preview interval: a fresh search from the pose it was let go at
            // could pull it to another spot than the one on screen.
            bool ghostValid = _shownGhost.HasValue && ValidNowAt(_shownGhost.Value.Landed);
            if (ghostValid) return Shown(_shownGhost.Value, now);
            if (plannedPlaced) return planned;
            if (!Judged() || !_grabStartSet) return planned;
            bool offFloor = planned.HasValue && !planned.Value.Settles;
            return new PlacementDrop(this, now, _grabStart, true, false, false, false, true, offFloor);
        }

        // The one rule at `pose`, with fresh inputs (the room, the other objects as they are now); the object is put back at once.
        private bool ValidNowAt(Pose pose)
        {
            if (SbApp == null) return false;
            Transform t = _entityTransform;
            var was = new Pose(t.position, t.rotation);
            t.SetPositionAndRotation(pose.position, pose.rotation);
            bool valid = SbApp.IsPlacementValid(t.gameObject, WalkableMargin);
            t.SetPositionAndRotation(was.position, was.rotation);
            return valid;
        }

        // The shown hologram as the release's drop: placed there (a coarse spot is a valid one), no longer approximate.
        private PlacementDrop Shown(in PlacementDrop ghost, Pose from) =>
            new PlacementDrop(this, from, ghost.Landed, true, true, ghost.OnWall);

        // Whether the one rule can judge this object here: a room is known, it authors a region, and it is not a physical object.
        private bool Judged()
        {
            GameObject root = _entityTransform.gameObject;
            return SbApp != null && SbApp.PlacementRoom != null && !SpatialBlend.SpatialBlendManager.IsPhysicalObject(root) &&
                   SpatialBlend.RoomFit.TryRegion(root, out _);
        }

        private void ResetPreview()
        {
            _previewReady = false;
            _previewInputs = null;
            _shownGhost = null;
            _previewFromSet = false;
            _nextPreviewAt = 0f;
        }

        protected void HidePreview()
        {
            if (_preview != null) _preview.Hide();
            ResetPreview();
        }

        public static void RequestGlobalRecenter()
        {
            s_RecenterToken++;
        }

        protected void CheckGlobalRecenter()
        {
            bool isLatestObj = LatestObjectSelection.IsCurrent(this);
            if (_allowRecenter && isLatestObj && s_LastConsumedToken < s_RecenterToken)
            {
                PlaceInFrontOfUser();
                s_LastConsumedToken = s_RecenterToken;
            }
        }

        /// <summary>The height (world y) this object is first placed at: its own height unless the motif snaps it (v1's absolute hover).</summary>
        public virtual float SpawnHeight => _entityTransform != null ? _entityTransform.position.y : transform.position.y;

        /// <summary>
        /// The first-spawn pose: in front of the user at <see cref="SpawnHeight"/>, at least a metre away, turned as
        /// <see cref="PlaceInFrontOfUser"/> turns it. The app places it there and pulls it to the nearest valid spot (<see cref="SpatialBlend.RoomFit"/>).
        /// </summary>
        public Pose SpawnPose(Vector3 eye, Vector3 forward)
        {
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 pos = eye + flat * Mathf.Max(_forwardOffset, 1f);
            pos.y = SpawnHeight;
            Vector3 fwd = -flat;
            if (_isFacingReverse) fwd = -fwd;
            return new Pose(pos, Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(_rotationOffset));
        }

        /// <summary>
        /// The pose this object takes on a wall at <paramref name="wallPoint"/> (on the wall's bottom edge), facing into the room
        /// along <paramref name="inward"/>; null when it is not wall-mounted (the default). A first spawn searches every wall with it.
        /// </summary>
        public virtual Pose? WallPose(Vector3 wallPoint, Vector3 inward) => null;

        public virtual void PlaceInFrontOfUser()
        {
            if (_centerEyeAnchor == null) return;
            // Recenter never moves a physical object: an own scan or a box stands where the real thing is.
            if (SpatialBlend.SpatialBlendManager.IsPhysicalObject(_entityTransform != null ? _entityTransform.gameObject : gameObject)) return;
            var pos = _centerEyeAnchor.position + _centerEyeAnchor.forward * _forwardOffset;
            var fwd = Vector3.ProjectOnPlane(_centerEyeAnchor.position - pos, Vector3.up).normalized;
            if (_isFacingReverse) fwd = -fwd;
            var baseRotation = Quaternion.LookRotation(fwd, Vector3.up);
            var targetRotation = baseRotation * Quaternion.Euler(_rotationOffset);
            if (_clampToWalkable)
                pos = ClampPositionToWalkable(pos, WalkableMargin);
            _entityTransform.SetPositionAndRotation(pos, targetRotation);
        }

        private void FindAuthoring()
        {
            if (_authoring != null || _entityTransform == null) return;
            _authoring = _entityTransform.GetComponent<SpatialBlend.SbSpatialAuthoring>();
            if (_authoring == null)
                _authoring = _entityTransform.GetComponentInChildren<SpatialBlend.SbSpatialAuthoring>();
        }

        /// <summary>
        /// The drop and spawn rule: the object's authored region at <paramref name="position"/> is pulled to the
        /// nearest spot where it lies inside the room, clear of the walls, the furniture and the other regions
        /// (<see cref="SpatialBlend.RoomFit"/>, no distance limit). Computed once, when the object is dropped or placed, never during
        /// a grab. Returns <paramref name="position"/> unchanged when the object declares no region, no room is known yet, or no
        /// spot in the room fits it.
        /// </summary>
        /// <summary>
        /// Whether this object is the user's own scan (<see cref="SpatialBlend.PhysicalCounterpart"/>): it stands for a real object and is
        /// never grabbed or moved. A joiner's box is not: the user carries it onto the real table.
        /// </summary>
        protected bool IsOwnScan => (_entityTransform != null ? _entityTransform : transform).GetComponentInParent<SpatialBlend.PhysicalCounterpart>() != null;

        /// <summary>
        /// Whether the object is moving under this motif: held by a grab, or in its release animation. Its authored region is not pushed
        /// to the core meanwhile (a carried object never sticks at an edge); the pose is pushed once at rest.
        /// </summary>
        public virtual bool InMotion => _isGrabbed;

        // A grab never outlives the app's pause or its loss of focus: the object is let go where it is, so it is
        // at rest and the manager pushes its pose once (SpatialBlendManager.LateUpdate skips an object in motion).
        protected virtual void OnApplicationPause(bool paused)
        {
            if (paused) ReleaseGrab();
        }

        protected virtual void OnApplicationFocus(bool focused)
        {
            if (!focused && !IsEditor) ReleaseGrab();
        }

        /// <summary>Lets go of a grab in progress where the object is (no snap, no settle).</summary>
        internal virtual void ReleaseGrab()
        {
            if (_isGrabbed) ReleaseExclusive();
        }

        /// <summary>The app this motif's object belongs to (found once).</summary>
        protected SpatialBlend.SbApp SbApp
        {
            get
            {
                if (_sbApp == null) _sbApp = FindAnyObjectByType<SpatialBlend.SbApp>();
                return _sbApp;
            }
        }

        protected Vector3 ClampPositionToWalkable(Vector3 position, float margin = 0f) =>
            ClampPositionToWalkable(position, margin, false, out _);

        /// <param name="preview">The drop preview: computed from the inputs prepared at the grab's start, nothing asked of the app.</param>
        /// <param name="placed">The rule found a valid spot (or the object is physical and stays).</param>
        protected Vector3 ClampPositionToWalkable(Vector3 position, float margin, bool preview, out bool placed) =>
            ClampPositionToWalkable(position, margin, preview, out placed, out _);

        /// <param name="approximate">A preview whose bounded search found nothing where the drop's full search may still.</param>
        protected Vector3 ClampPositionToWalkable(Vector3 position, float margin, bool preview, out bool placed, out bool approximate)
        {
            placed = false;
            approximate = false;
            if (_entityTransform == null) return position;
            if (SbApp == null) return position;
            // The shapes are at the entity's current pose; the candidate position moves them.
            Vector3 entityWorldPos = _entityTransform.position;
            var offset = new Vector2(position.x - entityWorldPos.x, position.z - entityWorldPos.z);
            if (preview)
            {
                if (_previewInputs == null)
                {
                    placed = SpatialBlend.SpatialBlendManager.IsPhysicalObject(_entityTransform.gameObject);
                    return position;
                }
                Transform held = _entityTransform;
                var at = new Pose(held.position, held.rotation);
                RoomFit.PreviewFit pullFit = RoomFit.PreviewPull(_previewInputs, offset);
                Vector3 spot = held.position;
                held.SetPositionAndRotation(at.position, at.rotation);
                placed = pullFit == RoomFit.PreviewFit.Exact || pullFit == RoomFit.PreviewFit.Rough;
                approximate = pullFit == RoomFit.PreviewFit.Rough || pullFit == RoomFit.PreviewFit.Unknown;
                return placed ? new Vector3(spot.x, position.y, spot.z) : position;
            }
            if (!_sbApp.TryPullPlacement(_entityTransform.gameObject, offset, margin, out Vector2 shift))
            {
                if (InteractionModeManager.DebugLog && !preview)
                    Debug.LogWarning($"[PlacementMotif:{name}] no region, no room known yet, or no spot in the room fits");
                return position;
            }
            placed = true;
            return new Vector3(position.x + shift.x, position.y, position.z + shift.y);
        }

        /// <summary>
        /// A drop's pose by THE placement (SbApp.TryPlaceDrop, RoomFit.Place: pulled, and turned when no translation fits); the
        /// candidate itself when there is no app, no region, no room known yet, or no spot fits.
        /// </summary>
        protected Pose PlaceDropInRoom(Pose candidate, float margin) => PlaceDropInRoom(candidate, margin, false, out _);

        /// <param name="preview">The drop preview: computed from the inputs prepared at the grab's start, nothing asked of the app.</param>
        /// <param name="placed">The rule placed it (or the object is physical and stays).</param>
        protected Pose PlaceDropInRoom(Pose candidate, float margin, bool preview, out bool placed) =>
            PlaceDropInRoom(candidate, margin, preview, out placed, out _);

        /// <param name="approximate">A preview whose bounded search found nothing where the drop's full search may still.</param>
        protected Pose PlaceDropInRoom(Pose candidate, float margin, bool preview, out bool placed, out bool approximate)
        {
            placed = false;
            approximate = false;
            if (_entityTransform == null || SbApp == null) return candidate;
            if (preview)
            {
                if (_previewInputs == null)
                {
                    placed = SpatialBlend.SpatialBlendManager.IsPhysicalObject(_entityTransform.gameObject);
                    return candidate;
                }
                // As the drop (SbApp.TryPlaceDrop): from the candidate, the way back to where it is held bounding the search.
                Transform t = _entityTransform;
                var was = new Pose(t.position, t.rotation);
                var back = new Vector2(was.position.x - candidate.position.x, was.position.z - candidate.position.z);
                t.SetPositionAndRotation(candidate.position, candidate.rotation);
                RoomFit.PreviewFit fit = RoomFit.PreviewPlace(_previewInputs, back.sqrMagnitude > 1e-8f ? back : (Vector2?)null);
                var found = new Pose(t.position, t.rotation);
                t.SetPositionAndRotation(was.position, was.rotation);
                placed = fit == RoomFit.PreviewFit.Exact || fit == RoomFit.PreviewFit.Rough;
                approximate = fit == RoomFit.PreviewFit.Rough || fit == RoomFit.PreviewFit.Unknown;
                return placed ? found : candidate;
            }
            if (_sbApp.TryPlaceDrop(_entityTransform.gameObject, candidate, margin, out Pose landed))
            {
                placed = true;
                return landed;
            }
            if (InteractionModeManager.DebugLog && !preview)
                Debug.LogWarning($"[PlacementMotif:{name}] no region, no room known yet, or no spot in the room fits");
            return candidate;
        }

        protected void UpdateIndicatorVisibility(bool show)
        {
            if (_lineIndicator != null)
            {
                _lineIndicator.enabled = show;
            }
        }

        protected void DrawLine(Vector3 start, Vector3 end)
        {
            if (_lineIndicator != null && _lineIndicator.enabled)
            {
                _lineIndicator.SetPosition(0, start);
                _lineIndicator.SetPosition(1, end);
            }
        }
    }
}
