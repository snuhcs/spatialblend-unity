// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oculus.Interaction;

namespace SpatialBlend.Interactions
{
    public class PanelPlacementMotif : PlacementMotif
    {
        [Header("Visual")]
        [SerializeField] private GameObject _panelHighlight;

        [Header("ISDK Interaction")]
        [SerializeField, Tooltip("Assign from prefab or auto-found at runtime.")]
        private RayInteractable _rayInteractable;

        [Header("Input")]
        [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.RTouch;

        [Header("Panel Settings")]
        [SerializeField] private float _hoverDistance = 1f;
        [SerializeField, Tooltip("Distance from wall surface when snapping")]
        private float _wallOffset = 0.05f;
        // Whether the grab's last target pose was the wall snap's.
        private bool _onWall;
        private const float MinDistance = 0.3f;
        private const float MaxDistance = 10f;
        private const float MoveSpeed = 2.5f;
        private const float SizeTolerance = 0.2f;
        private const float RaycastDistance = 5f;
        private const float SmoothSpeed = 15f;

        [Header("SFX")]
        [SerializeField] private AudioClip _objectPlacedClip;
        [SerializeField, Range(0f, 1f)] private float _sfxVolume = 0.5f;

        // Auto-found references
        private Transform _raycastAnchor;
        private OVRHand _hand;

        private float _distanceFromController;
        private EditPointer _selectingPointer;
        private bool _subscribed;
        private readonly RollingAverage _rollingAverageFilter = new();

        protected override void Awake()
        {
            base.Awake();
            if (_objectPlacedClip == null) _objectPlacedClip = Resources.Load<AudioClip>("SFX/ObjectPlaced");

            // Auto-find raycast anchor from camera rig
            if (_raycastAnchor == null)
                _raycastAnchor = _centerEyeAnchor;

            // Find OVRHand for the configured controller
            var hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            foreach (var h in hands)
            {
                if ((_controller == OVRInput.Controller.RTouch && h.GetHand() == OVRPlugin.Hand.HandRight) ||
                    (_controller == OVRInput.Controller.LTouch && h.GetHand() == OVRPlugin.Hand.HandLeft))
                {
                    _hand = h;
                    break;
                }
            }

            // Log error if no hand or controller found
            if (_hand == null && !OVRInput.IsControllerConnected(_controller))
            {
                Debug.LogError($"[PanelPlacementMotif:{gameObject.name}] No OVRHand or controller found for {_controller}. Raycast will fall back to center eye.");
            }
        }

        protected override void Start()
        {
            base.Start();

            // Find RayInteractable - either wired by SbEntity or on this object
            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponentInChildren<RayInteractable>();
            }

            if (_rayInteractable == null)
            {
                Debug.LogError($"[PanelPlacementMotif:{name}] No RayInteractable found!");
            }

            // Listen to the active editing input: the right controller's ray or the right hand's
            EditInput.Hovered += OnInteractorHover;
            EditInput.Unhovered += OnInteractorUnhover;
            EditInput.Selected += OnInteractorSelect;
            EditInput.Unselected += OnInteractorUnselect;
            _subscribed = true;
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

            if (!_isGrabbed && _panelHighlight != null)
            {
                _panelHighlight.SetActive(true);
            }
        }

        internal void OnInteractorUnhover(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;

            if (!_isGrabbed && _panelHighlight != null)
            {
                _panelHighlight.SetActive(false);
            }
        }

        internal void OnInteractorSelect(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            if (!AcceptsSelection(IsFocused) || _isGrabbed) return;


            _selectingPointer = pointer;
            BeginGrabbing();
        }

        internal void OnInteractorUnselect(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            if (!_isGrabbed) return;
            _selectingPointer = null;
            EndGrabbing();
        }

        protected override void Update()
        {
            if (!IsFocused) return;
            base.Update();

            if (ReleaseIfModeBlocked()) return;

            // Update target pose while grabbed
            if (_isGrabbed)
            {
                UpdateTargetPose();
                TickPreview(Time.unscaledTime);
            }
        }

        private void LateUpdate()
        {
            if (_isGrabbed)
            {
                AnimatePanelPose();
            }
        }

        private Ray GetRaycastRay()
        {
            // The pointer that holds the panel (controller ray or hand ray); before any, the unified hand/controller fallback.
            if (_selectingPointer != null && _selectingPointer.Source == EditInputSource.Hand) return _selectingPointer.Ray;
            // A controller's grab keeps the ray it always had (hand rays change nothing for controllers).
            return RaycastUtils.GetRay(_hand, _controller, _raycastAnchor);
        }

        /// <summary>The push/pull input each frame of a grab. Null reads <see cref="EditInput.GrabAdjust"/>: the right thumbstick, or a hand's reach. Replaced by a test.</summary>
        internal System.Func<Vector2> StickSource { get; set; }

        private void BeginGrabbing()
        {
            ClaimExclusive();
            var ray = GetRaycastRay();
            if (Physics.Raycast(ray, out var hit, RaycastDistance, _raycastLayer))
            {
                _distanceFromController = Vector3.Distance(ray.origin, hit.point);
            }
            else
            {
                _distanceFromController = 2f;
            }
        }

        /// <summary>A panel spawns at its fixed hover height, the height its wall snap holds it at.</summary>
        public override float SpawnHeight => _hoverDistance;

        /// <summary>The wall snap's pose (as a drop on a wall makes it): <c>_wallOffset</c> off the wall at the hover height, facing its normal.</summary>
        public override Pose? WallPose(Vector3 wallPoint, Vector3 inward)
        {
            // As the wall snap writes it (v1): the facing is the wall's normal, turned for a reversed panel, and the offset is along
            // that facing (the Whiteboard's is -0.1 m with a reversed facing: 0.1 m into the room).
            Vector3 n = Vector3.ProjectOnPlane(inward, Vector3.up).normalized;
            Vector3 facing = IsFacingReverse ? -n : n;
            Vector3 p = wallPoint + facing * _wallOffset;
            return new Pose(new Vector3(p.x, _hoverDistance, p.z), Quaternion.LookRotation(facing, Vector3.up));
        }

        /// <summary>
        /// What letting go now does. The region is pulled once to the nearest valid spot (inside the room, clear of the walls, the
        /// furniture and the other regions); the wall snap's height and facing stay. Dropped on a wall, it stays on a wall: an invalid
        /// spot slides along the walls to the nearest valid one, never into mid-room (RoomFit.TryPlaceOnWall).
        /// </summary>
        internal override PlacementDrop? PlanDrop(bool preview)
        {
            if (_entityTransform == null) return null;
            var start = new Pose(_entityTransform.position, _entityTransform.rotation);
            RoomRegion room = SbApp != null ? SbApp.PlacementRoom : null;
            if (!_clampToWalkable) return new PlacementDrop(this, start, start, true, true, RoomFit.IsOnWall(_entityTransform.gameObject, this, room));
            GameObject root = _entityTransform.gameObject;
            bool wallTried = _onWall && SbApp != null && !(preview && PreviewInputs == null);
            if (wallTried && (preview ? RoomFit.PreviewOnWall(PreviewInputs, this) : SbApp.TryPlaceOnWall(root, this, WalkableMargin)))
            {
                var landed = new Pose(_entityTransform.position, _entityTransform.rotation);
                bool onWall = RoomFit.IsOnWall(root, this, room);
                _entityTransform.SetPositionAndRotation(start.position, start.rotation);
                return new PlacementDrop(this, start, landed, true, true, onWall);
            }
            // Aimed at a wall and the preview's slide found no spot within its reach: the drop slides farther along the walls (or, only
            // where no wall fits at all, stands on the floor). Not knowing which, the preview shows amber with no ghost.
            if (wallTried && preview) return new PlacementDrop(this, start, start, true, false, false, true);
            Vector3 pulled = ClampPositionToWalkable(start.position, WalkableMargin, preview, out bool placed, out bool approximate);
            _entityTransform.position = pulled;
            bool onWallPulled = placed && RoomFit.IsOnWall(root, this, room);
            _entityTransform.position = start.position;
            return new PlacementDrop(this, start, new Pose(pulled, start.rotation), true, placed, onWallPulled, approximate);
        }

        private void EndGrabbing()
        {
            // The drop's plan, resolved with what the preview showed: it lands where the hologram was (PlacementMotif.ResolveRelease).
            PlacementDrop? drop = ResolveRelease(PlanDrop(false));
            if (drop.HasValue) _entityTransform.SetPositionAndRotation(drop.Value.Landed.position, drop.Value.Landed.rotation);
            _targetPose = null;
            UpdateIndicatorVisibility(false);
            if (_panelHighlight != null && _panelHighlight.activeSelf) _panelHighlight.SetActive(false);
            ReleaseExclusive();
            if (_objectPlacedClip != null)
                AudioSource.PlayClipAtPoint(_objectPlacedClip, _entityTransform.position, _sfxVolume);
            if (drop.HasValue) ReportDrop(drop.Value);
        }

        /// <summary>True while the mode blocks placement, after releasing a grab in progress (as in v1).</summary>
        internal bool ReleaseIfModeBlocked()
        {
            if (IsModeAllowed) return false;
            ForceRelease();
            return true;
        }

        /// <summary>Whether a select event starts a grab: the app has focus and the mode allows placement (v1 :143).</summary>
        internal bool AcceptsSelection(bool focused) => focused && IsModeAllowed && !IsOwnScan;

        private void ForceRelease()
        {
            _selectingPointer = null;
            if (_isGrabbed)
            {
                EndGrabbing();
            }
            UpdateIndicatorVisibility(false);
            if (_panelHighlight != null && _panelHighlight.activeSelf) _panelHighlight.SetActive(false);
        }

        /// <summary>One frame of push/pull: the thumbstick's Y (or a hand's reach) moves the panel along the ray, within the panel's limits.</summary>
        internal void ApplyPushPull(float input, float deltaTime)
        {
            _distanceFromController += input * MoveSpeed * deltaTime;
            _distanceFromController = Mathf.Clamp(_distanceFromController, MinDistance, MaxDistance);
        }

        internal float DistanceFromController => _distanceFromController;

        private void UpdateTargetPose()
        {
            ApplyPushPull((StickSource != null ? StickSource() : EditInput.GrabAdjust(_selectingPointer)).y, Time.deltaTime);

            var newEnvPose = TryGetEnvironmentPose();

            if (newEnvPose.HasValue)
            {
                _targetPose = newEnvPose.Value;
            }
            else
            {
                // Use controller ray for manual positioning
                var ray = GetRaycastRay();
                var manualPos = ray.origin + ray.direction * _distanceFromController;
                var manualFwd = Vector3.ProjectOnPlane(_centerEyeAnchor.position - manualPos, Vector3.up).normalized;
                if (IsFacingReverse) manualFwd = -manualFwd;
                _targetPose = new Pose(manualPos, Quaternion.LookRotation(manualFwd, Vector3.up));
            }
        }

        private Pose? TryGetEnvironmentPose()
        {
            var ray = GetRaycastRay();

            // Try EnvironmentRaycastManager first (for advanced depth-based placement)
            bool depthManagerAvailable = _raycastManager != null && _raycastManager.enabled && _raycastManager.gameObject.activeInHierarchy;
            
            if (depthManagerAvailable)
            {
                try
                {
                    bool raycastHit = _raycastManager.Raycast(ray, out var hit);
                    
                    if (raycastHit)
                    {
                        // Check confidence threshold
                        if (hit.normalConfidence < 0.5f)
                        {
                            return null;
                        }

                        bool isCeiling = Vector3.Dot(hit.normal, Vector3.down) > 0.7f;
                        if (isCeiling)
                        {
                            return null;
                        }

                        var panelSize = new Vector3(_entityTransform.localScale.x, _entityTransform.localScale.y, 0f) * (1f - SizeTolerance);
                        float verticalDot = Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up));
                        bool isVerticalSurface = verticalDot < 0.3f;

                        if (isVerticalSurface)
                        {
                            Vector3 wallPoint;
                            Vector3 wallNormal;
                            
                            bool placeBoxResult = _raycastManager.PlaceBox(ray, panelSize, Vector3.up, out var placeResult);
                            
                            if (placeBoxResult)
                            {
                                wallPoint = placeResult.point;
                                wallNormal = placeResult.normal;
                            }
                            else
                            {
                                wallPoint = hit.point;
                                wallNormal = hit.normal;
                            }
                            
                            if (IsFacingReverse) wallNormal = -wallNormal;
                            var smoothedNormal = _rollingAverageFilter.UpdateRollingAverage(wallNormal);
                            var offsetPosition = wallPoint + smoothedNormal * _wallOffset;
                            var fixedPosition = new Vector3(offsetPosition.x, _hoverDistance, offsetPosition.z);
                            _onWall = true;
                            return new Pose(fixedPosition, Quaternion.LookRotation(smoothedNormal, Vector3.up));
                        }

                        // Floor placement path
                        _onWall = false;
                        var pos = hit.point + Vector3.up * Mathf.Max(_entityTransform.localScale.y * 0.5f, _hoverDistance);
                        var halfExtents = panelSize * 0.5f;
                        var forward = Vector3.ProjectOnPlane(_centerEyeAnchor.position - pos, Vector3.up).normalized;
                        if (IsFacingReverse) forward = -forward;
                        var orient = Quaternion.LookRotation(forward, Vector3.up);

                        bool checkBoxCollision = _raycastManager.CheckBox(pos + Vector3.up * 0.1f, halfExtents, orient);
                        if (!checkBoxCollision)
                        {
                            return new Pose(pos, orient);
                        }

                        return null;
                    }
                }
                catch
                {
                    // EnvironmentDepthManager not enabled - fall through to utility fallback
                }
            }

            // Fallback to utility - try wall first, then any surface (uses MRUK when depth not available)
            if (EnvironmentRaycastUtility.RaycastWall(ray, out var wallResult, MaxDistance))
            {
                var normal = wallResult.Normal;
                if (IsFacingReverse) normal = -normal;
                var smoothedNormal = _rollingAverageFilter.UpdateRollingAverage(normal);
                var offsetPosition = wallResult.Point + smoothedNormal * _wallOffset;
                var fixedPosition = new Vector3(offsetPosition.x, _hoverDistance, offsetPosition.z);
                // On a wall as in the depth path: the drop then slides along the walls (without depth, MRUK walls are this path).
                _onWall = true;
                return new Pose(fixedPosition, Quaternion.LookRotation(smoothedNormal, Vector3.up));
            }

            // Fallback to floor/any surface for horizontal placement
            if (EnvironmentRaycastUtility.Raycast(ray, out var result2, MaxDistance))
            {
                bool isCeiling = Vector3.Dot(result2.Normal, Vector3.down) > 0.7f;
                if (isCeiling)
                {
                    return null;
                }

                _onWall = false;
                var pos = result2.Point + Vector3.up * Mathf.Max(_entityTransform.localScale.y * 0.5f, _hoverDistance);
                var forward = Vector3.ProjectOnPlane(_centerEyeAnchor.position - pos, Vector3.up).normalized;
                if (IsFacingReverse) forward = -forward;
                var orient = Quaternion.LookRotation(forward, Vector3.up);
                return new Pose(pos, orient);
            }

            return null;
        }

        private void AnimatePanelPose()
        {
            if (_targetPose.HasValue)
            {
                UpdateIndicatorVisibility(true);
                DrawLine(GetRaycastRay().origin, _targetPose.Value.position);
                
                _entityTransform.position = Vector3.Lerp(_entityTransform.position, _targetPose.Value.position, Time.deltaTime * SmoothSpeed);
                _entityTransform.rotation = Quaternion.Slerp(_entityTransform.rotation, _targetPose.Value.rotation, Time.deltaTime * SmoothSpeed);
            }
        }

        private class RollingAverage
        {
            private List<Vector3> _normals;
            private int _index;
            public Vector3 UpdateRollingAverage(Vector3 current)
            {
                if (_normals == null) { _normals = Enumerable.Repeat(current, 10).ToList(); }
                _index = (_index + 1) % _normals.Count;
                _normals[_index] = current;
                var sum = Vector3.zero;
                foreach (var n in _normals) sum += n;
                return sum.normalized;
            }
        }
    }
}
