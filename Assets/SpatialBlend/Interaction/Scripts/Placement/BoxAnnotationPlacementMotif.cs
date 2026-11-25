// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using Oculus.Interaction;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Lightweight placement motif for annotation-like objects.
    /// Keeps ISDK hover/select lifecycle but removes distance-based move/snap behavior.
    /// </summary>
    public class BoxAnnotationPlacementMotif : PlacementMotif
    {
        [Header("ISDK Interaction")]
        [SerializeField, Tooltip("Assign from prefab or auto-found at runtime.")]
        private RayInteractable _rayInteractable;

        [Header("Spawn Guard")]
        [SerializeField, Tooltip("Ignore select events briefly after spawn to avoid carry-over trigger input.")]
        private float _ignoreSelectAfterSpawnSeconds = 1.5f;

        private EditPointer _selectingPointer;
        private bool _subscribed;
        private float _grabDistance;
        private float _allowSelectAtTime;
        private bool _pendingReleaseAfterSpawnSelectIgnore;

        protected override void Awake()
        {
            base.Awake();
            ResetSpawnSelectGuard();
        }

        protected override void Start()
        {
            base.Start();

            // Find RayInteractable - either wired by SbEntity or on this object
            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponentInParent<RayInteractable>();
            }

            if (_rayInteractable == null)
            {
                Debug.LogError($"[BoxAnnotationPlacementMotif:{name}] No RayInteractable found!");
            }

            // Listen to the active editing input: the right controller's ray or the right hand's
            EditInput.Hovered += OnInteractorHover;
            EditInput.Unhovered += OnInteractorUnhover;
            EditInput.Selected += OnInteractorSelect;
            EditInput.Unselected += OnInteractorUnselect;
            _subscribed = true;
            ResetSpawnSelectGuard();
        }

        private void ResetSpawnSelectGuard()
        {
            _allowSelectAtTime = Time.unscaledTime + Mathf.Max(0f, _ignoreSelectAfterSpawnSeconds);
            _pendingReleaseAfterSpawnSelectIgnore = false;
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
            if (!IsFocused || !IsModeAllowed) return;
            if (Time.unscaledTime < _allowSelectAtTime)
            {
                _pendingReleaseAfterSpawnSelectIgnore = true;
                return;
            }
            if (_pendingReleaseAfterSpawnSelectIgnore)
            {
                return;
            }

            ClaimExclusive();
            _selectingPointer = pointer;

            // Keep object at the same distance from ray origin while dragging.
            Ray ray = pointer.Ray;
            _grabDistance = Vector3.Distance(ray.origin, _entityTransform.position);
        }

        internal void OnInteractorUnselect(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;

            if (_pendingReleaseAfterSpawnSelectIgnore)
            {
                _pendingReleaseAfterSpawnSelectIgnore = false;
            }

            if (!_isGrabbed) return;

            _selectingPointer = null;
            _targetPose = new Pose(_entityTransform.position, _entityTransform.rotation);

            ReleaseExclusive();
        }

        protected override void Update()
        {
            if (!IsFocused) return;
            base.Update();

            if (!IsModeAllowed)
            {
                ForceReleaseForModeBlock();
                return;
            }

            if (!_isGrabbed || _selectingPointer == null)
            {
                return;
            }

            // This can always change position(as not SbEntity)
            Ray ray = _selectingPointer.Ray;
            Vector3 targetPos = ray.origin + ray.direction * _grabDistance;
            _entityTransform.position = Vector3.Lerp(_entityTransform.position, targetPos, Time.deltaTime * 15f);
        }

        private void ForceReleaseForModeBlock()
        {
            _selectingPointer = null;
            _pendingReleaseAfterSpawnSelectIgnore = false;
            _targetPose = null;
            UpdateIndicatorVisibility(false);

            if (_isGrabbed)
            {
                ReleaseExclusive();
            }
        }
    }
}
