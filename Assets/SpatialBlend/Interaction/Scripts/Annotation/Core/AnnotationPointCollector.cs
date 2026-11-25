// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Collects annotation points (anchors) and provides events for point management.
    /// Handles the domain logic for building annotation shapes.
    /// </summary>
    public class AnnotationPointCollector : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int _minPoints = 3;
        [SerializeField] private int _maxPoints = -1; // -1 = unlimited
        [SerializeField] private bool _autoFinalizeOnMaxPoints = false;

        /// <summary>
        /// Fired when an anchor is added. Parameters: (index, anchor)
        /// </summary>
        public event Action<int, AnnotationAnchor> OnAnchorAdded;

        /// <summary>
        /// Fired when an anchor is removed. Parameter: (index)
        /// </summary>
        public event Action<int> OnAnchorRemoved;

        /// <summary>
        /// Fired when an anchor is updated. Parameters: (index, anchor)
        /// </summary>
        public event Action<int, AnnotationAnchor> OnAnchorUpdated;

        /// <summary>
        /// Fired when the collection is finalized. Parameter: (anchors)
        /// </summary>
        public event Action<IReadOnlyList<AnnotationAnchor>> OnFinalized;

        /// <summary>
        /// Fired when the collection is cancelled/cleared.
        /// </summary>
        public event Action OnCancelled;

        private List<AnnotationAnchor> _anchors = new();
        private Plane _groundPlane;
        private bool _hasGroundPlane;

        /// <summary>
        /// Read-only access to collected anchors.
        /// </summary>
        public IReadOnlyList<AnnotationAnchor> Anchors => _anchors;

        /// <summary>
        /// Number of collected anchors.
        /// </summary>
        public int Count => _anchors.Count;

        /// <summary>
        /// Minimum points required to finalize.
        /// </summary>
        public int MinPoints => _minPoints;

        /// <summary>
        /// Maximum points allowed (-1 = unlimited).
        /// </summary>
        public int MaxPoints => _maxPoints;

        /// <summary>
        /// Whether enough points have been collected to finalize.
        /// </summary>
        public bool CanFinalize => _anchors.Count >= _minPoints;

        /// <summary>
        /// Whether more points can be added.
        /// </summary>
        public bool CanAddMore => _maxPoints < 0 || _anchors.Count < _maxPoints;

        /// <summary>
        /// The ground plane established by the first point.
        /// </summary>
        public Plane GroundPlane => _groundPlane;

        /// <summary>
        /// Whether a ground plane has been established.
        /// </summary>
        public bool HasGroundPlane => _hasGroundPlane;

        /// <summary>
        /// Add an anchor to the collection.
        /// </summary>
        public bool AddAnchor(AnnotationAnchor anchor)
        {
            if (!CanAddMore) return false;

            // Establish ground plane from first point
            if (_anchors.Count == 0)
            {
                _groundPlane = new Plane(anchor.Normal, anchor.Position);
                _hasGroundPlane = true;
            }

            _anchors.Add(anchor);
            int index = _anchors.Count - 1;
            OnAnchorAdded?.Invoke(index, anchor);

            // Auto-finalize if at max points
            if (_autoFinalizeOnMaxPoints && _maxPoints > 0 && _anchors.Count >= _maxPoints)
            {
                Finalize();
            }

            return true;
        }

        /// <summary>
        /// Add an anchor from a world position and normal.
        /// </summary>
        public bool AddAnchor(Vector3 position, Vector3 normal, Transform hitObject = null)
        {
            return AddAnchor(new AnnotationAnchor(position, normal, hitObject));
        }

        /// <summary>
        /// Remove the last anchor (undo).
        /// </summary>
        public bool RemoveLast()
        {
            if (_anchors.Count == 0) return false;

            int index = _anchors.Count - 1;
            _anchors.RemoveAt(index);
            OnAnchorRemoved?.Invoke(index);

            // Reset ground plane if all points removed
            if (_anchors.Count == 0)
            {
                _hasGroundPlane = false;
            }

            return true;
        }

        /// <summary>
        /// Update an anchor at a specific index.
        /// </summary>
        public bool UpdateAnchor(int index, AnnotationAnchor anchor)
        {
            if (index < 0 || index >= _anchors.Count) return false;

            _anchors[index] = anchor;
            OnAnchorUpdated?.Invoke(index, anchor);
            return true;
        }

        /// <summary>
        /// Update an anchor's position.
        /// </summary>
        public bool UpdateAnchorPosition(int index, Vector3 newPosition)
        {
            if (index < 0 || index >= _anchors.Count) return false;

            var anchor = _anchors[index];
            anchor.UpdatePosition(newPosition);
            _anchors[index] = anchor;
            OnAnchorUpdated?.Invoke(index, anchor);
            return true;
        }

        /// <summary>
        /// Get world positions of all anchors.
        /// </summary>
        public List<Vector3> GetWorldPositions()
        {
            var positions = new List<Vector3>(_anchors.Count);
            foreach (var anchor in _anchors)
            {
                positions.Add(anchor.GetWorldPosition());
            }
            return positions;
        }

        /// <summary>
        /// Finalize the collection and fire the OnFinalized event.
        /// </summary>
        public bool Finalize()
        {
            if (!CanFinalize) return false;

            OnFinalized?.Invoke(_anchors);
            return true;
        }

        /// <summary>
        /// Cancel and clear all collected anchors.
        /// </summary>
        public void Cancel()
        {
            _anchors.Clear();
            _hasGroundPlane = false;
            OnCancelled?.Invoke();
        }

        /// <summary>
        /// Clear all anchors without firing cancel event.
        /// </summary>
        public void Clear()
        {
            _anchors.Clear();
            _hasGroundPlane = false;
        }

        /// <summary>
        /// Configure the collector settings.
        /// </summary>
        public void Configure(int minPoints, int maxPoints = -1, bool autoFinalize = false)
        {
            _minPoints = minPoints;
            _maxPoints = maxPoints;
            _autoFinalizeOnMaxPoints = autoFinalize;
        }
    }
}
