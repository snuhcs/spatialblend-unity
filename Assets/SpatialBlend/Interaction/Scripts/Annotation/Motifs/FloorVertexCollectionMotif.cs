// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;
using SpatialBlend.Interactions;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Abstract base class for annotation motifs that collect vertices on floor surfaces.
    /// Subscribes to EnvironmentSurfaceManager for floor selection events.
    /// Subclasses define: start/end conditions, point semantics, and final output creation.
    ///
    /// Used by:
    /// - BoxAnnotationMotif: 4-point box creation (P0=origin, P1=width, P2=depth, P3=height)
    /// - AffordanceAnnotationMotif: N-point polygon creation with ring closure
    /// </summary>
    public abstract class FloorVertexCollectionMotif : AnnotationMotifBase
    {
        /// <summary>
        /// Current mode of the annotation workflow.
        /// </summary>
        protected enum Mode
        {
            Idle,           // Not active - ignore floor clicks
            WaitingForStart, // Active but waiting for first floor click to start
            Create          // Collecting points to create new annotation
        }

        private Mode _currentMode = Mode.Idle;

        protected Mode CurrentMode
        {
            get => _currentMode;
            set
            {
                if (value == Mode.Idle && _currentMode != Mode.Idle) IdledFrame = Time.frameCount;
                _currentMode = value;
            }
        }

        /// <summary>The frame this motif last went idle (-1 never). B that cancelled the last point idles it that frame, before it is destroyed.</summary>
        public int IdledFrame { get; private set; } = -1;

        /// <summary>
        /// Whether B belongs to this motif this frame: it is collecting points, or B just cancelled it. A motif left idle in the scene
        /// (released by a mode change before its first point) owns nothing after that frame.
        /// </summary>
        public bool OwnsUndoButtonThisFrame => IsActive || IdledFrame == Time.frameCount;

        /// <summary>
        /// Activate this motif to start waiting for floor clicks.
        /// Called by ManipulationContext when AffordanceVolume mode is executed.
        /// </summary>
        public void Activate()
        {
            if (CurrentMode == Mode.Idle)
            {
                // Find surface manager and claim floor clicks
                _surfaceManager = FindAnyObjectByType<EnvironmentSurfaceManager>();
                if (_surfaceManager != null)
                {
                    _surfaceManager.SetActiveFloorHandler(OnFloorSelected);
                }

                CurrentMode = Mode.WaitingForStart;
                ClaimExclusive();
            }
        }

        /// <summary>
        /// Points collected during creation (world space).
        /// </summary>
        protected List<Vector3> Points { get; } = new List<Vector3>();

        /// <summary>
        /// Ground plane frame established at creation start.
        /// Provides origin, normal, and tangent axes for consistent projection.
        /// </summary>
        protected PlaneFrame _groundFrame;

        /// <summary>
        /// Unity Plane for backward compatibility (e.g., OnFinalized callback).
        /// Computed from _groundFrame.
        /// </summary>
        protected Plane GroundPlane => new Plane(_groundFrame.normal, _groundFrame.origin);

        /// <summary>
        /// Current preview point (where cursor is pointing, projected onto ground plane).
        /// Updated every frame during Create mode.
        /// </summary>
        protected Vector3 PreviewPoint { get; private set; }

        /// <summary>
        /// Whether the preview point is near the first point (for ring closure detection).
        /// </summary>
        protected bool IsNearFirstPoint { get; private set; }

        // EnvironmentSurfaceManager reference for floor selection events
        private EnvironmentSurfaceManager _surfaceManager;

        // === Abstract Configuration ===

        /// <summary>
        /// Minimum number of points required to finalize.
        /// </summary>
        protected abstract int MinPoints { get; }

        /// <summary>
        /// Maximum number of points allowed (-1 for unlimited).
        /// </summary>
        protected abstract int MaxPoints { get; }

        /// <summary>
        /// Whether to auto-finalize when MaxPoints is reached.
        /// </summary>
        protected abstract bool AutoFinalizeOnMaxPoints { get; }

        // === Abstract Callbacks ===

        /// <summary>
        /// Called when a new point is added during creation.
        /// </summary>
        protected abstract void OnPointAdded(int index, Vector3 point);

        /// <summary>
        /// Called when a point is removed (undo) during creation.
        /// </summary>
        protected abstract void OnPointRemoved(int index);

        /// <summary>
        /// Called every frame during creation with the current preview point.
        /// Use this to update visual preview of the annotation being created.
        /// </summary>
        protected abstract void OnPointUpdated(int index, Vector3 point);

        /// <summary>
        /// Called when creation is finalized with all collected points.
        /// </summary>
        protected abstract void OnFinalized(IReadOnlyList<Vector3> points, Plane groundPlane);

        /// <summary>
        /// Called when creation is cancelled.
        /// </summary>
        protected abstract void OnCancelled();

        // === Optional Overrides ===

        /// <summary>
        /// Override to define custom finalization conditions.
        /// Called after each point is added to check if annotation should auto-finalize.
        /// Default implementation returns false (only auto-finalize on MaxPoints if configured).
        ///
        /// Example: AffordanceVolume overrides to check ring closure (click near first point).
        /// </summary>
        protected virtual bool ShouldFinalize() => false;

        /// <summary>
        /// Called every frame during Create mode for custom preview drawing.
        /// Default implementation does nothing.
        /// </summary>
        protected virtual void DrawPreview() { }

        /// <summary>
        /// Called before starting creation to allow subclass to handle any existing annotations.
        /// Return true to proceed with creation, false to cancel.
        /// </summary>
        protected virtual bool OnBeforeCreate(Vector3 hitPoint, Vector3 normal) => true;

        // === Unity Lifecycle ===

        protected virtual void Start()
        {
            // Nothing to do - wait for Activate() to be called
        }

        protected virtual void OnDestroy()
        {
            // Clear active handler if we're still claiming it
            if (_surfaceManager != null)
            {
                _surfaceManager.SetActiveFloorHandler(null);
            }
        }

        protected virtual void Update()
        {
            if (!IsFocused) return;

            if (!IsModeAllowed)
            {
                ForceRelease();
                return;
            }

            // Only handle Create mode - WaitingForStart and Idle just wait for OnFloorSelected
            if (CurrentMode == Mode.Create)
            {
                HandleCreateModeUpdate();
                DrawPreview();
            }
        }

        // === Floor Selection Handling ===

        /// <summary>
        /// Called when floor is selected via EnvironmentSurfaceManager.
        /// Starts creation or adds point based on current mode.
        /// </summary>
        private void OnFloorSelected(MRUKAnchor anchor, Vector3 hitPoint)
        {
            if (!IsModeAllowed)
            {
                return;
            }

            // Extract ground plane normal from MRUKAnchor
            Vector3 normal = anchor.transform.up;

            if (CurrentMode == Mode.WaitingForStart)
            {
                // Start creation with first point
                if (!OnBeforeCreate(hitPoint, normal))
                {
                    return;
                }

                _groundFrame = PlaneFrame.Build(hitPoint, normal, _centerEyeAnchor);
                Points.Clear();
                Points.Add(hitPoint);
                CurrentMode = Mode.Create;
                OnPointAdded(0, hitPoint);

                // Auto-finalize if max is 1
                if (MaxPoints == 1 && AutoFinalizeOnMaxPoints)
                {
                    FinalizeAnnotation();
                }
            }
            else if (CurrentMode == Mode.Create)
            {
                // Add point during creation
                AddPointAtPosition(hitPoint);
            }
        }

        // === Input Handling ===

        /// <summary>
        /// Handles preview update and undo during Create mode.
        /// Point addition is handled by OnFloorSelected event.
        /// </summary>
        private void HandleCreateModeUpdate()
        {
            Vector3 previewPoint = Vector3.zero;
            bool hasPreviewPoint = false;

            // Priority 1: Use ISDK interactor's collision point when hovering over floor
            // This ensures preview matches the visible cursor exactly
            if (_surfaceManager != null && _surfaceManager.IsFloorHovered)
            {
                var hit = EditInput.ActivePointer?.HitPoint;
                if (hit.HasValue)
                {
                    previewPoint = hit.Value;
                    hasPreviewPoint = true;
                }
            }

            // Fallback: Project ray onto the ground frame's plane
            if (!hasPreviewPoint)
            {
                var ray = GetControllerRay();
                var plane = new Plane(_groundFrame.normal, _groundFrame.origin);
                if (plane.Raycast(ray, out float enter) && enter <= _maxRaycastDistance)
                {
                    previewPoint = ray.GetPoint(enter);
                    hasPreviewPoint = true;
                }
            }

            if (hasPreviewPoint)
            {
                PreviewPoint = previewPoint;

                // Check ring closure: is cursor near first point?
                if (Points.Count >= MinPoints)
                {
                    float distToFirst = Vector3.Distance(previewPoint, Points[0]);
                    IsNearFirstPoint = distToFirst <= _closeLoopThreshold;
                }
                else
                {
                    IsNearFirstPoint = false;
                }

                OnPointUpdated(Points.Count, previewPoint);
            }

            // Undo - pop last point (B button)
            if (EditInput.UndoDown)
            {
                if (Points.Count > 0)
                {
                    int removed = Points.Count - 1;
                    Points.RemoveAt(removed);
                    OnPointRemoved(removed);

                    // If no points left, cancel creation
                    if (Points.Count == 0)
                    {
                        Cancel();
                    }
                }
            }
        }

        /// <summary>
        /// Add a point at the given position during creation.
        /// Called from OnFloorSelected when in Create mode.
        /// </summary>
        private void AddPointAtPosition(Vector3 point)
        {
            // Check custom finalization condition (e.g., ring closure)
            if (ShouldFinalize())
            {
                FinalizeAnnotation();
                return;
            }

            // Check for close-loop: clicking near first point with enough points
            if (Points.Count >= MinPoints &&
                Vector3.Distance(point, Points[0]) < _closeLoopThreshold)
            {
                FinalizeAnnotation();
                return;
            }

            Points.Add(point);
            OnPointAdded(Points.Count - 1, point);

            // Auto-finalize if max reached
            if (MaxPoints > 0 && Points.Count >= MaxPoints && AutoFinalizeOnMaxPoints)
            {
                FinalizeAnnotation();
            }
        }

        // === State Transitions ===

        /// <summary>
        /// Finalize the annotation creation.
        /// Calls OnFinalized and resets to idle state.
        /// </summary>
        protected void FinalizeAnnotation()
        {
            if (Points.Count >= MinPoints)
            {
                OnFinalized(Points, GroundPlane);
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] Not enough points to finalize: {Points.Count} < {MinPoints}");
            }

            // Release floor click handling
            if (_surfaceManager != null)
            {
                _surfaceManager.SetActiveFloorHandler(null);
            }

            Points.Clear();
            IsNearFirstPoint = false;
            CurrentMode = Mode.Idle;
            FireAnnotationComplete(true);
            ReleaseExclusive();
        }

        /// <summary>
        /// Cancel the annotation creation.
        /// Calls OnCancelled and resets to idle state.
        /// </summary>
        protected void Cancel()
        {
            OnCancelled();

            // Release floor click handling
            if (_surfaceManager != null)
            {
                _surfaceManager.SetActiveFloorHandler(null);
            }

            Points.Clear();
            IsNearFirstPoint = false;
            CurrentMode = Mode.Idle;
            FireAnnotationComplete(false);
            ReleaseExclusive();
        }

        /// <summary>
        /// Public override for external cancellation.
        /// Called by ManipulationContext or other external systems.
        /// </summary>
        public override void CancelAnnotation()
        {
            if (CurrentMode == Mode.Create)
            {
                Cancel();
            }
            else if (CurrentMode == Mode.WaitingForStart)
            {
                // Cancel before any points were collected
                CurrentMode = Mode.Idle;
                FireAnnotationComplete(false);
                ReleaseExclusive();
            }
            else
            {
                base.CancelAnnotation();
            }
        }

        /// <summary>
        /// Force release any active interaction state.
        /// Called when mode changes or interaction is interrupted.
        /// </summary>
        protected void ForceRelease()
        {
            if (CurrentMode == Mode.Create)
            {
                Cancel();
            }
            else if (CurrentMode == Mode.WaitingForStart)
            {
                CurrentMode = Mode.Idle;
                ReleaseExclusive();
            }

            // Safety cleanup for idle mode
            if (CurrentMode == Mode.Idle && IsActiveByMe)
            {
                ReleaseExclusive();
            }
        }

        // === Public Methods ===

        /// <summary>
        /// Start creation externally at a given ground hit point.
        /// Used by managers that detect ground hits and delegate to this motif.
        /// </summary>
        public void BeginCreationAt(Vector3 hitPoint, Vector3 normal)
        {
            if (CurrentMode != Mode.Idle) return;

            if (!OnBeforeCreate(hitPoint, normal))
            {
                return;
            }

            ClaimExclusive();
            _groundFrame = PlaneFrame.Build(hitPoint, normal, _centerEyeAnchor);
            Points.Clear();
            Points.Add(hitPoint);
            CurrentMode = Mode.Create;
            OnPointAdded(0, hitPoint);

            if (MaxPoints == 1 && AutoFinalizeOnMaxPoints)
            {
                FinalizeAnnotation();
            }
        }

        /// <summary>
        /// Whether currently in any active state (not idle).
        /// </summary>
        public bool IsActive => CurrentMode != Mode.Idle;

        /// <summary>
        /// Whether currently creating a new annotation.
        /// </summary>
        public bool IsCreating => CurrentMode == Mode.Create;

        // === PlaneFrame Struct ===

        /// <summary>
        /// Defines a coordinate frame on a plane with origin, normal, and tangent axes.
        /// Used for consistent projection of points onto the floor plane.
        /// </summary>
        protected struct PlaneFrame
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
