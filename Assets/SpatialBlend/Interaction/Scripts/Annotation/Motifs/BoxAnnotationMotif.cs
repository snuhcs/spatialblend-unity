// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using SpatialBlend.Interactions;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Annotation motif for creating and editing 3D bounding boxes on floor surfaces.
    /// Uses 4-point sequential creation: P0 (origin), P1 (width), P2 (depth), P3 (height).
    /// Triggered by BoxAnnotationManager when ray hits ground in SceneEdit mode.
    ///
    /// Extends FloorVertexCollectionMotif for shared infrastructure, but uses its own
    /// specialized 4-point creation logic with custom semantics for each point.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public class BoxAnnotationMotif : FloorVertexCollectionMotif
    {
        [Header("Interaction")]
        [SerializeField] private bool _useDragToCreate = false;

        [Header("Box Settings")]
        [SerializeField] private Transform _annotationRoot;
        [SerializeField] private GameObject _boxAnnotationPrefab;

        [Header("SFX")]
        [SerializeField] private AudioClip _annotationCreatedClip;
        [SerializeField] private AudioClip _pointPlacedClip;
        [SerializeField, Range(0f, 1f)] private float _sfxVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] private float _pointPlacedVolume = 0.25f;

        // Visual settings (inline defaults)
        private float _defaultHeight => 0.25f;
        private float _maxHeight => 2.5f;
        private float _minExtent => 0.05f;

        // === Abstract Implementation (FloorVertexCollectionMotif) ===
        // BoxAnnotationMotif uses specialized 4-point creation logic, so we implement
        // these abstract methods but the actual logic is in the box-specific methods below.

        protected override int MinPoints => 4;
        protected override int MaxPoints => 4;
        protected override bool AutoFinalizeOnMaxPoints => true;

        // These callbacks are not used - BoxAnnotationMotif has its own point management
        protected override void OnPointAdded(int index, Vector3 point) { }
        protected override void OnPointRemoved(int index) { }
        protected override void OnPointUpdated(int index, Vector3 point) { }
        protected override void OnFinalized(IReadOnlyList<Vector3> points, Plane groundPlane) { }
        protected override void OnCancelled() { }

        // Box-specific state (uses its own enum instead of base Mode)
        private enum BoxMode { Idle, Create, Edit, DragPoint }
        private BoxMode _boxMode = BoxMode.Idle;
        private int _currentPointIndex = 0;

        public bool IsIdleState => _boxMode == BoxMode.Idle;

        private PlaneFrame _plane;
        private Vector2 _minXZ;
        private Vector2 _maxXZ;
        private float _height;

        private Vector3 _p0, _p1, _p2, _p3;
        private Vector3 _lastProjectedPoint;

        private BoxCollider _boxCollider;

        private int _activePointIndex = -1;
        private List<BoxAnnotationInstance> _instances = new();
        private BoxAnnotationInstance _selectedInstance;
        private static int s_AnnotationCounter = 0;
        private Pose _originalPose;
        private Vector3 _originalSize;
        private bool _skipInputThisFrame = false; // Skip input on frame when started externally

        // === Computed Properties ===
        private Pose BoxPose => _boxMode == BoxMode.Create ? ComputeBoxPoseFromPoints() : ComputeBoxPoseFromExtents();
        private Vector3 BoxSize => _boxMode == BoxMode.Create ? ComputeBoxSizeFromPoints() : ComputeBoxSizeFromExtents();
        private bool HasBounds => _boxMode == BoxMode.Create ? ComputeHasBoundsFromPoints() : ComputeHasBoundsFromExtents();

        // === Unity Lifecycle ===

        protected override void Awake()
        {
            base.Awake();

            _boxCollider = GetComponent<BoxCollider>();
            _boxCollider.isTrigger = true;

            if (_annotationCreatedClip == null) _annotationCreatedClip = Resources.Load<AudioClip>("SFX/AnnotationCreated");
            if (_pointPlacedClip == null) _pointPlacedClip = Resources.Load<AudioClip>("SFX/AnnotationPointPlaced");
        }

        /// <summary>
        /// Override Start to skip EnvironmentSurfaceManager subscription.
        /// BoxAnnotationMotif uses BoxAnnotationManager's specialized flow instead of base class flow.
        /// Floor clicks are handled by BoxAnnotationManager which calls BeginCreationFromExternal().
        /// </summary>
        protected override void Start()
        {
            // Do NOT call base.Start() - BoxAnnotationMotif has its own specialized
            // 4-point creation flow via BoxAnnotationManager. It doesn't use the
            // base class's OnFloorSelected event handling.
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_visual != null) _visual.Clear();
            if (_undoHint != null) _undoHint.Hide();
            // A box this motif left hovered must not stay lit once the motif is gone (the manager destroys it when a box completes).
            for (int i = 0; i < _instances.Count; i++)
                if (_instances[i] != null) _instances[i].SetEmphasis(AnnotationEmphasis.Normal, -1);
        }

        // === Box-specific Update logic ===
        // Overrides base Update() to use specialized 4-point creation logic

        protected override void Update()
        {
            if (!IsFocused) return;

            if (ReleaseIfModeBlocked()) return;

            // Skip input processing for one frame after external start (to avoid double-triggering)
            if (_skipInputThisFrame)
            {
                _skipInputThisFrame = false;
                RenderCurrentState();
                return;
            }

            // Handle undo in any mode
            if (UndoDown)
            {
                HandleUndo();
                return;
            }

            switch (_boxMode)
            {
                case BoxMode.Idle:
                    HandleBoxIdleInput();
                    break;
                case BoxMode.Create:
                    HandleBoxCreateInput();
                    break;
                case BoxMode.Edit:
                    HandleBoxEditInput();
                    break;
                case BoxMode.DragPoint:
                    HandleBoxDragPointInput();
                    break;
            }

            RenderCurrentState();
        }

        // === Box-Specific Input Handling ===

        private void HandleUndo()
        {
            if (_boxMode == BoxMode.Create)
            {
                UndoLastPoint();
            }
            else if (_boxMode == BoxMode.DragPoint || _boxMode == BoxMode.Edit)
            {
                // Cancel edit and restore original state
                if (_selectedInstance != null)
                {
                    _selectedInstance.SetData(_originalPose, _originalSize, _plane.normal);
                    _selectedInstance.gameObject.SetActive(true);
                }
                ResetToIdle();
            }
            else if (_boxMode == BoxMode.Idle && _instances.Count > 0)
            {
                if (FindClosestInstance(out var targetInstance, out _))
                {
                    // Undo on a box asks first, like every Delete; the menu deletes it on yes.
                    targetInstance.OnDeletePressed();
                }
            }
        }

        private void HandleBoxIdleInput()
        {
            // Check instance hit for editing (creation is handled by BoxAnnotationManager)
            if (_instances.Count > 0 && FindClosestInstance(out var instance, out var hitPoint))
            {
                bool isCorner = IsCloseToCorner(instance, hitPoint, out var pointIndex);

                if (isCorner && AcceptDown)
                {
                    ClaimExclusive();
                    if (SelectInstanceForEdit(instance, hitPoint))
                    {
                        _activePointIndex = pointIndex;
                        _boxMode = BoxMode.DragPoint;
                    }
                    else
                    {
                        ReleaseExclusive();
                    }
                }
            }
        }

        private void HandleBoxCreateInput()
        {
            UpdateCreate();

            if (_useDragToCreate)
            {
                if (AcceptUp)
                {
                    LockCurrentPoint();
                }
            }
            else
            {
                if (AcceptDown)
                {
                    LockCurrentPoint();
                }
            }
        }

        private void HandleBoxEditInput()
        {
            if (AcceptDown && FindClosestPoint(out var pointIndex))
            {
                ClaimExclusive();
                _activePointIndex = pointIndex;
                _boxMode = BoxMode.DragPoint;
            }
        }

        private void HandleBoxDragPointInput()
        {
            if (_selectedInstance == null || _selectedInstance.gameObject == null)
            {
                ResetToIdle();
                return;
            }

            if (AcceptUp)
            {
                EndPointDrag();
                return;
            }

            if (TryProjectCurrentRay(out var projected))
            {
                _lastProjectedPoint = projected;
                ApplyPointDrag(_activePointIndex, projected);
            }
        }

        // === Box Creation Logic ===

        /// <summary>
        /// Starts box creation from an external system (e.g. BoxAnnotationManager) at a given hit point.
        /// Claims exclusive interaction priority and begins the 4-point creation flow.
        /// </summary>
        public void BeginCreationFromExternal(Vector3 hitPoint, Vector3 normal)
        {
            ClaimExclusive();
            BeginCreation(hitPoint, normal);

            if (!_useDragToCreate)
            {
                LockCurrentPoint();
            }

            // Skip input this frame to avoid double-triggering from the same button press
            _skipInputThisFrame = true;
        }

        private void BeginCreation(Vector3 hitPoint, Vector3 normal)
        {
            _plane = PlaneFrame.Build(hitPoint, normal, _centerEyeAnchor);
            _height = Mathf.Max(_defaultHeight, _minExtent);

            _p0 = hitPoint;
            _p1 = hitPoint;
            _p2 = hitPoint;
            _p3 = hitPoint;
            _lastProjectedPoint = hitPoint;
            _currentPointIndex = 0;

            _boxMode = BoxMode.Create;
        }

        private void UpdateCreate()
        {
            // P3 (height) computes height from the ray directly: skip the floor projection. The ray aims up
            // to set a height, and a ray that aims up never meets the floor plane (as v1).
            Vector3 projected = _lastProjectedPoint;
            if (_currentPointIndex != 3)
            {
                if (!TryProjectCurrentRay(out projected))
                    return;

                _lastProjectedPoint = projected;
            }

            switch (_currentPointIndex)
            {
                case 0:
                    _p0 = projected;
                    break;
                case 1:
                    {
                        var delta = projected - _p0;
                        var axis = Vector3.ProjectOnPlane(delta, _plane.normal);
                        if (axis.sqrMagnitude < 0.0001f) axis = Vector3.ProjectOnPlane(_plane.right, _plane.normal);
                        if (axis.sqrMagnitude < 0.0001f) axis = Vector3.right;
                        axis = axis.normalized;
                        var extentX = Mathf.Max(_minExtent, Vector3.Dot(delta, axis));
                        _p1 = _p0 + axis * extentX;
                    }
                    break;
                case 2:
                    {
                        var axisX = Vector3.ProjectOnPlane(_p1 - _p0, _plane.normal);
                        if (axisX.sqrMagnitude < 0.0001f) break;
                        axisX = axisX.normalized;

                        var delta = projected - _p0;
                        var zProj = Vector3.ProjectOnPlane(delta, _plane.normal);
                        zProj -= axisX * Vector3.Dot(zProj, axisX);
                        if (zProj.sqrMagnitude < 0.0001f) break;
                        var axisZ = zProj.normalized;

                        var extentZ = Vector3.Dot(delta, axisZ);
                        extentZ = Mathf.Sign(extentZ) * Mathf.Max(_minExtent, Mathf.Abs(extentZ));
                        _p2 = _p0 + axisZ * extentZ;
                    }
                    break;
                case 3:
                    {
                        var p3_corner = _p0 + (_p1 - _p0) + (_p2 - _p0);
                        var baseCenter = (_p0 + _p1 + _p2 + p3_corner) * 0.25f;
                        var ray = GetControllerRay();

                        float height = _defaultHeight;

                        var O = ray.origin;
                        var D = ray.direction.normalized;
                        var C = baseCenter;
                        var N = _plane.normal.normalized;

                        var CO = (C - O);
                        float dDotN = Vector3.Dot(D, N);
                        float denom = 1f - dDotN * dDotN;

                        if (denom > 1e-5f)
                        {
                            float t = (Vector3.Dot(CO, D) - Vector3.Dot(CO, N) * dDotN) / denom;
                            if (t < 0f) t = 0f;
                            var closestOnRay = O + D * t;
                            height = Vector3.Dot(closestOnRay - _p0, N);
                        }

                        height = Mathf.Clamp(Mathf.Abs(height), _minExtent, _maxHeight);
                        _p3 = _p0 + _plane.normal * height;
                        _lastProjectedPoint = _p3;
                    }
                    break;
            }
        }

        private void LockCurrentPoint()
        {
            switch (_currentPointIndex)
            {
                case 0:
                    _p0 = _lastProjectedPoint;
                    _currentPointIndex = 1;
                    PlaySfxAt(_pointPlacedClip, _lastProjectedPoint, _pointPlacedVolume);
                    break;
                case 1:
                    _p1 = _lastProjectedPoint;
                    _currentPointIndex = 2;
                    PlaySfxAt(_pointPlacedClip, _lastProjectedPoint, _pointPlacedVolume);
                    break;
                case 2:
                    _p2 = _lastProjectedPoint;
                    _currentPointIndex = 3;
                    PlaySfxAt(_pointPlacedClip, _lastProjectedPoint, _pointPlacedVolume);
                    break;
                case 3:
                    _p3 = _lastProjectedPoint;
                    FinalizeCreation();
                    break;
            }
        }

        private void UndoLastPoint()
        {
            if (_currentPointIndex == 0)
            {
                CancelAnnotation();
            }
            else
            {
                _currentPointIndex--;
                switch (_currentPointIndex)
                {
                    case 0: _p1 = _p0; break;
                    case 1: _p2 = _p0; break;
                    case 2: _p3 = _p0; break;
                }
            }
        }

        private void FinalizeCreation()
        {
            ComputeBoundsFrom4Points();
            var boxPose = ComputeBoxPoseFromPoints();
            var boxSize = ComputeBoxSizeFromPoints();

            bool success = false;
            if (boxSize.x >= _minExtent && boxSize.y >= _minExtent && boxSize.z >= _minExtent)
            {
                CreateNewInstance(boxPose, boxSize);
                success = true;
                PlaySfxAt(_annotationCreatedClip, boxPose.position);
            }

            ResetToIdle();
            FireAnnotationComplete(success);
        }

        private void ResetToIdle()
        {
            _boxMode = BoxMode.Idle;
            _currentPointIndex = 0;
            _selectedInstance = null;
            _activePointIndex = -1;
            if (_undoHint != null) _undoHint.Hide();
            ReleaseExclusive();
        }

        /// <summary>
        /// Public override for external cancellation.
        /// Called by ManipulationContext or other external systems.
        /// </summary>
        public override void CancelAnnotation()
        {
            if (_boxMode != BoxMode.Idle)
            {
                ResetToIdle();
                FireAnnotationComplete(false);
            }
            else
            {
                base.CancelAnnotation();
            }
        }

        // === Instance Management ===

        private void CreateNewInstance(Pose boxPose, Vector3 boxSize)
        {
            GameObject go;

            if (_boxAnnotationPrefab != null)
            {
                // Use the authored prefab when available (sample scenes)
                // Instantiate at world root first, then set world position
                go = Instantiate(_boxAnnotationPrefab);
                // Reset prefab's transform to identity to avoid any baked-in offsets
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = Vector3.one;
            }
            else
            {
                // Fallback path for programmatically created motifs with no prefab assigned.
                // Create a minimal runtime instance with a collider, wireframe and optional cube visual.
                Debug.LogWarning("[BoxAnnotation] _boxAnnotationPrefab not assigned; creating runtime BoxAnnotationInstance without prefab visuals.");

                go = new GameObject($"BoxAnnotation_{s_AnnotationCounter}");

                // Add a simple cube mesh as visual so the box is still visible.
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "Visual";
                visual.transform.SetParent(go.transform, false);

                // Remove the extra collider from the visual; BoxAnnotationInstance will manage its own BoxCollider.
                var visualCollider = visual.GetComponent<Collider>();
                if (visualCollider != null)
                {
                    Destroy(visualCollider);
                }
            }

            go.name = $"BoxAnnotation_{s_AnnotationCounter++}";
            go.layer = gameObject.layer;

            var instance = go.GetComponent<BoxAnnotationInstance>();
            if (instance == null)
            {
                instance = go.AddComponent<BoxAnnotationInstance>();
            }
            instance.Initialize();

            // Set world position/rotation (SetData applies directly to transform)
            instance.SetData(boxPose, boxSize, _plane.normal);

            // Don't parent - keep at world root to avoid any transform hierarchy issues
            // The instance position is already in world space

            _instances.Add(instance);
        }

        // === Edit Mode ===

        private bool SelectInstanceForEdit(BoxAnnotationInstance instance, Vector3 hitPoint)
        {
            if (instance == null || instance.gameObject == null) return false;

            _selectedInstance = instance;

            var collider = instance.GetComponent<BoxCollider>();
            if (collider == null) return false;

            // Use bounds.size to get the world-space size (accounts for transform scale)
            // since BoxAnnotationInstance uses unit collider (1,1,1) with transform.localScale for sizing
            var size = collider.bounds.size;
            var half = size * 0.5f;

            _originalPose = new Pose(instance.transform.position, instance.transform.rotation);
            _originalSize = size;
            _height = size.y;

            // Position is already at bottom, so origin = position
            _plane = new PlaneFrame
            {
                origin = instance.transform.position,
                normal = instance.transform.up,
                right = instance.transform.right,
                forward = instance.transform.forward
            };

            _minXZ = new Vector2(-half.x, -half.z);
            _maxXZ = new Vector2(half.x, half.z);

            _boxMode = BoxMode.Edit;
            return true;
        }

        private void ApplyPointDrag(int pointIndex, Vector3 projected)
        {
            if (!HasBounds || _selectedInstance == null) return;

            var local = _plane.ToLocal(projected);

            int bottomIndex = pointIndex % 4;
            bool isTop = pointIndex >= 4;

            if (!isTop)
            {
                bool moveXMin = bottomIndex == 0 || bottomIndex == 3;
                bool moveXMax = bottomIndex == 1 || bottomIndex == 2;
                bool moveZMin = bottomIndex == 0 || bottomIndex == 1;
                bool moveZMax = bottomIndex == 2 || bottomIndex == 3;

                if (moveXMin) _minXZ.x = Mathf.Min(local.x, _maxXZ.x - _minExtent);
                if (moveXMax) _maxXZ.x = Mathf.Max(local.x, _minXZ.x + _minExtent);
                if (moveZMin) _minXZ.y = Mathf.Min(local.z, _maxXZ.y - _minExtent);
                if (moveZMax) _maxXZ.y = Mathf.Max(local.z, _minXZ.y + _minExtent);
            }

            if (isTop)
            {
                var ray = GetControllerRay();
                var baseCenterLocal = new Vector3((_minXZ.x + _maxXZ.x) * 0.5f, 0f, (_minXZ.y + _maxXZ.y) * 0.5f);
                var baseCenterWorld = _plane.ToWorld(baseCenterLocal);

                var toCamera = ray.origin - baseCenterWorld;
                toCamera = Vector3.ProjectOnPlane(toCamera, _plane.normal);
                if (toCamera.sqrMagnitude < 0.0001f) toCamera = _plane.forward;
                toCamera.Normalize();

                var verticalPlane = new Plane(toCamera, baseCenterWorld);
                if (verticalPlane.Raycast(ray, out var enter))
                {
                    var hit = ray.GetPoint(enter);
                    float newHeight = Vector3.Dot(hit - _plane.origin, _plane.normal);
                    _height = Mathf.Max(_minExtent, newHeight);
                }
            }

            ApplyBoundsFromExtents();
            if (_selectedInstance != null && _selectedInstance.gameObject != null)
            {
                var boxSize = BoxSize;
                if (boxSize.x > 0 && boxSize.y > 0 && boxSize.z > 0)
                {
                    _selectedInstance.SetData(BoxPose, boxSize, _plane.normal);
                }
            }
        }

        private void EndPointDrag()
        {
            if (_selectedInstance != null && HasBounds && _selectedInstance.gameObject != null)
            {
                var boxSize = BoxSize;
                if (boxSize.x > 0 && boxSize.y > 0 && boxSize.z > 0)
                {
                    _selectedInstance.SetData(BoxPose, boxSize, _plane.normal);
                }
                _selectedInstance.gameObject.SetActive(true);
            }

            ResetToIdle();
        }

        // === Rendering ===

        private readonly Vector3[] _cornerScratch = new Vector3[8];
        private AnnotationVisual _visual;   // world-space graphics: the aim marker, the box in progress, the corner being dragged

        private void EnsureVisual()
        {
            if (_visual != null) return;
            _visual = AnnotationVisual.Create(transform, "AnnotationVisual_Preview");
        }

        private void RenderCurrentState()
        {
            EnsureVisual();
            // ComputeBoundsFrom4Points moves this motif's transform to the box pose; the content is in world space.
            _visual.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _visual.Clear();
            switch (_boxMode)
            {
                case BoxMode.Idle:
                    RenderIdleState();
                    break;
                case BoxMode.Create:
                    RenderCreateState();
                    break;
                case BoxMode.Edit:
                    break;
                case BoxMode.DragPoint:
                    RenderDragPointState();
                    break;
            }
            UpdateUndoHint();
        }

        private BoxUndoHint _undoHint;

        /// <summary>The "B: undo" tag of a box being drawn; null until the first box is started.</summary>
        public BoxUndoHint UndoHint => _undoHint;

        // A box in progress says how to take it back: a missed trigger on the floor starts one, and B is the only way out.
        private void UpdateUndoHint()
        {
            if (_boxMode != BoxMode.Create)
            {
                if (_undoHint != null) _undoHint.Hide();
                return;
            }
            if (_undoHint == null) _undoHint = BoxUndoHint.Create(transform);
            _undoHint.Show(_p0, _currentPointIndex, _centerEyeAnchor, EditInput.ActiveSource);   // B for a controller, the Undo button for hands
        }

        /// <summary>The floor-plane axes for a flat marker, turned to the viewer's heading.</summary>
        private void SurfaceAxes(out Vector3 right, out Vector3 forward)
        {
            var reference = _centerEyeAnchor != null ? _centerEyeAnchor.right : Vector3.right;
            right = Vector3.ProjectOnPlane(reference, Vector3.up);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            right.Normalize();
            forward = Vector3.Cross(Vector3.up, right);
        }

        private void RenderIdleState()
        {
            BoxAnnotationInstance hovered = null;
            Vector3 hitPoint = default;
            bool hit = _instances.Count > 0 && FindClosestInstance(out hovered, out hitPoint);
            int hoverCorner = -1;
            if (hit && IsCloseToCorner(hovered, hitPoint, out var pointIndex)) hoverCorner = pointIndex;

            // The hovered box (and the corner under the ray) get the emphasised look, drawn by the box itself.
            foreach (var inst in _instances)
            {
                if (inst == null) continue;
                bool isHovered = hit && inst == hovered;
                inst.SetEmphasis(isHovered ? AnnotationEmphasis.Hovered : AnnotationEmphasis.Normal, isHovered ? hoverCorner : -1);
            }

            if (!hit)
            {
                var ray = GetControllerRay();
                if (TryRaycastGround(ray, out var groundPoint, out _))
                {
                    SurfaceAxes(out var right, out var forward);
                    _visual.SetAim(ray.origin, groundPoint, right, forward);
                }
            }
        }

        private void RenderCreateState()
        {
            switch (_currentPointIndex)
            {
                case 0:
                {
                    SurfaceAxes(out var right, out var forward);
                    _visual.SetAim(GetControllerRay().origin, _p0, right, forward);
                    break;
                }
                case 1:
                    _visual.AddPointMarker(_p0, _plane.right, _plane.forward, CornerFocus.None);
                    _visual.AddSegment(_p0, _p1);
                    _visual.AddPointMarker(_p1, _plane.right, _plane.forward, CornerFocus.Active);
                    break;
                case 2:
                    // the corner under the cursor is the one opposite the first point (corner 2 of the rectangle)
                    _visual.SetFloorRect(_p0, _p1, _p2, _plane.normal, focusCorner: 2, focus: CornerFocus.Active);
                    break;
                case 3:
                    ComputeBoundsFrom4Points();
                    if (HasBounds)
                    {
                        var pose = BoxPose;
                        var size = BoxSize;
                        // the corner being placed is the top one above the first point
                        AnnotationGeometry.BoxCorners(pose.position, pose.rotation, size, _cornerScratch);
                        int top = AnnotationGeometry.NearestCorner(_cornerScratch, _p3);
                        _visual.SetBox(pose.position, pose.rotation, size, AnnotationEmphasis.Normal, top, CornerFocus.Active);
                    }
                    break;
            }
        }

        private void RenderDragPointState()
        {
            if (_selectedInstance == null) return;

            _selectedInstance.gameObject.SetActive(false);
            if (!HasBounds) return;
            var pose = BoxPose;
            _visual.SetBox(pose.position, pose.rotation, BoxSize, AnnotationEmphasis.Selected, _activePointIndex, CornerFocus.Active);
        }

        // === Helper Methods ===

        private bool FindClosestInstance(out BoxAnnotationInstance instance, out Vector3 hitPoint)
        {
            instance = null;
            hitPoint = Vector3.zero;

            var ray = GetControllerRay();
            float minDist = float.MaxValue;

            _instances.RemoveAll(inst => inst == null || inst.gameObject == null);

            foreach (var inst in _instances)
            {
                var collider = inst.GetComponent<BoxCollider>();
                if (collider == null) continue;

                var bounds = collider.bounds;
                if (bounds.IntersectRay(ray, out float distance) && distance < 10f && distance < minDist)
                {
                    minDist = distance;
                    instance = inst;
                    hitPoint = ray.GetPoint(distance);
                }
            }

            return instance != null;
        }

        private bool IsCloseToCorner(BoxAnnotationInstance instance, Vector3 hitPoint, out int pointIndex)
        {
            pointIndex = -1;
            if (instance == null) return false;

            var collider = instance.GetComponent<BoxCollider>();
            if (collider == null) return false;

            // Use world-space bounds directly - works regardless of position convention
            var b = collider.bounds;
            Vector3[] corners = new Vector3[8];
            corners[0] = new Vector3(b.min.x, b.min.y, b.min.z);
            corners[1] = new Vector3(b.max.x, b.min.y, b.min.z);
            corners[2] = new Vector3(b.max.x, b.min.y, b.max.z);
            corners[3] = new Vector3(b.min.x, b.min.y, b.max.z);
            corners[4] = new Vector3(b.min.x, b.max.y, b.min.z);
            corners[5] = new Vector3(b.max.x, b.max.y, b.min.z);
            corners[6] = new Vector3(b.max.x, b.max.y, b.max.z);
            corners[7] = new Vector3(b.min.x, b.max.y, b.max.z);

            float minDist = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                float dist = Vector3.Distance(hitPoint, corners[i]);
                if (dist < minDist)
                {
                    minDist = dist;
                    pointIndex = i;
                }
            }

            return pointIndex >= 0 && minDist < 0.2f;
        }

        private bool FindClosestPoint(out int pointIndex)
        {
            pointIndex = -1;
            if (_selectedInstance == null) return false;

            var ray = GetControllerRay();
            var collider = _selectedInstance.GetComponent<BoxCollider>();
            if (collider == null) return false;

            if (collider.Raycast(ray, out var hit, 10f))
            {
                return IsCloseToCorner(_selectedInstance, hit.point, out pointIndex);
            }

            return false;
        }

        private bool TryProjectCurrentRay(out Vector3 projected)
        {
            var ray = GetControllerRay();
            var plane = new Plane(_plane.normal, _plane.origin);
            if (plane.Raycast(ray, out var enter))
            {
                projected = ray.GetPoint(enter);
                return true;
            }

            projected = _plane.origin;
            return false;
        }

        // === Bounds Computation ===

        private Pose ComputeBoxPoseFromExtents()
        {
            // Position at BOTTOM of box (Y=0 in local plane space)
            var bottomLocal = new Vector3((_minXZ.x + _maxXZ.x) * 0.5f, 0f, (_minXZ.y + _maxXZ.y) * 0.5f);
            return new Pose(_plane.ToWorld(bottomLocal), Quaternion.LookRotation(_plane.forward, _plane.normal));
        }

        private Vector3 ComputeBoxSizeFromExtents()
        {
            return new Vector3(_maxXZ.x - _minXZ.x, _height, _maxXZ.y - _minXZ.y);
        }

        private bool ComputeHasBoundsFromExtents()
        {
            var width = _maxXZ.x - _minXZ.x;
            var depth = _maxXZ.y - _minXZ.y;
            return width >= _minExtent && depth >= _minExtent && _height >= _minExtent;
        }

        private Pose ComputeBoxPoseFromPoints()
        {
            var axisX = Vector3.ProjectOnPlane(_p1 - _p0, _plane.normal);
            if (axisX.sqrMagnitude < 0.0001f) return new Pose();
            axisX = axisX.normalized;

            var axisZ = Vector3.ProjectOnPlane(_p2 - _p0, _plane.normal);
            axisZ -= axisX * Vector3.Dot(axisZ, axisX);
            if (axisZ.sqrMagnitude < 0.0001f) return new Pose();
            axisZ = axisZ.normalized;

            var extentX = Vector3.Dot(_p1 - _p0, axisX);
            var extentZ = Vector3.Dot(_p2 - _p0, axisZ);
            var height = Vector3.Dot(_p3 - _p0, _plane.normal);

            // Use absolute extents for center calculation to match size calculation
            // This ensures the box is centered on the annotated region regardless of draw direction
            var absExtentX = Mathf.Abs(extentX);
            var absExtentZ = Mathf.Abs(extentZ);
            var absHeight = Mathf.Abs(height);

            // Determine actual base corner based on sign of extents
            var baseCorner = _p0;
            if (extentX < 0) baseCorner += axisX * extentX;
            if (extentZ < 0) baseCorner += axisZ * extentZ;
            if (height < 0) baseCorner += _plane.normal * height;

            // Position at BOTTOM of box (no height offset)
            var bottomWorld = baseCorner + axisX * (0.5f * absExtentX) + axisZ * (0.5f * absExtentZ);
            return new Pose(bottomWorld, Quaternion.LookRotation(axisZ, _plane.normal));
        }

        private Vector3 ComputeBoxSizeFromPoints()
        {
            var axisX = Vector3.ProjectOnPlane(_p1 - _p0, _plane.normal);
            if (axisX.sqrMagnitude < 0.0001f) return Vector3.zero;
            axisX = axisX.normalized;

            var axisZ = Vector3.ProjectOnPlane(_p2 - _p0, _plane.normal);
            axisZ -= axisX * Vector3.Dot(axisZ, axisX);
            if (axisZ.sqrMagnitude < 0.0001f) return Vector3.zero;
            axisZ = axisZ.normalized;

            var extentX = Vector3.Dot(_p1 - _p0, axisX);
            var extentZ = Vector3.Dot(_p2 - _p0, axisZ);
            var height = Vector3.Dot(_p3 - _p0, _plane.normal);

            return new Vector3(Mathf.Abs(extentX), Mathf.Abs(height), Mathf.Abs(extentZ));
        }

        private bool ComputeHasBoundsFromPoints()
        {
            var size = ComputeBoxSizeFromPoints();
            return size.x >= _minExtent && size.y >= _minExtent && size.z >= _minExtent;
        }

        private void ComputeBoundsFrom4Points()
        {
            var boxSize = ComputeBoxSizeFromPoints();
            if (boxSize.x < _minExtent || boxSize.y < _minExtent || boxSize.z < _minExtent) return;

            var boxPose = ComputeBoxPoseFromPoints();
            transform.SetPositionAndRotation(boxPose.position, boxPose.rotation);
            _boxCollider.center = Vector3.zero;
            _boxCollider.size = boxSize;
        }

        private void ApplyBoundsFromExtents()
        {
            if (!HasBounds) return;

            var boxPose = BoxPose;
            var boxSize = BoxSize;

            transform.SetPositionAndRotation(boxPose.position, boxPose.rotation);
            _boxCollider.center = Vector3.zero;
            _boxCollider.size = boxSize;
        }

        /// <summary>
        /// A blocked mode cancels a box in progress and gives up the claim (as v1).
        /// Returns true while the mode is blocked. An idle, unclaimed motif is left alone, so the blocked
        /// mode does not repeat the release every frame.
        /// </summary>
        internal bool ReleaseIfModeBlocked()
        {
            if (IsModeAllowed) return false;
            if (_boxMode != BoxMode.Idle || IsActiveByMe)
            {
                BoxForceRelease();
            }
            return true;
        }

        private void BoxForceRelease()
        {
            // Mode was blocked while this motif was active: cancel and notify manager so it can cleanup.
            if (_boxMode != BoxMode.Idle)
            {
                CancelAnnotation();
            }
            else if (IsActiveByMe)
            {
                ReleaseExclusive();
            }

            if (_visual != null) _visual.Clear();
        }

        private void PlaySfxAt(AudioClip clip, Vector3 position, float volume = -1f)
        {
            if (clip != null)
                AudioSource.PlayClipAtPoint(clip, position, volume < 0 ? _sfxVolume : volume);
        }

        // === PlaneFrame Struct ===

        private struct PlaneFrame
        {
            public Vector3 origin;
            public Vector3 normal;
            public Vector3 right;
            public Vector3 forward;

            public static PlaneFrame Build(Vector3 origin, Vector3 normal, Transform reference)
            {
                var frame = new PlaneFrame
                {
                    origin = origin,
                    normal = normal.normalized
                };

                var refRight = reference ? reference.right : Vector3.right;
                var right = Vector3.ProjectOnPlane(refRight, frame.normal);
                if (right.sqrMagnitude < 0.0001f)
                    right = Vector3.ProjectOnPlane(Vector3.right, frame.normal);
                frame.right = right.normalized;
                frame.forward = Vector3.Cross(frame.normal, frame.right).normalized;
                return frame;
            }

            public Vector3 ToLocal(Vector3 world)
            {
                var delta = world - origin;
                return new Vector3(
                    Vector3.Dot(delta, right),
                    Vector3.Dot(delta, normal),
                    Vector3.Dot(delta, forward));
            }

            public Vector3 ToWorld(Vector3 local)
            {
                return origin + right * local.x + normal * local.y + forward * local.z;
            }
        }
    }
}
