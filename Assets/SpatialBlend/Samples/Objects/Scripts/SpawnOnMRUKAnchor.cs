// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using SpatialBlend.Interactions;
using Meta.XR.MRUtilityKit;

namespace SpatialBlend.Samples.Objects
{
    public class SpawnOnMRUKAnchor : MonoBehaviour
    {
        [Header("Target Settings")]
        [Tooltip("Target MRUK Anchor's label (e.g., Table, Wall, Screen, etc.).")]
        [SerializeField] private MRUKAnchor.SceneLabels _targetLabel;
        
        private PlacementMotif _placementMotif;
        // Update from _motif
        private bool _debugLog = false;
        private Vector3 _positionOffset = Vector3.zero;
        private Vector3 _rotationOffset = Vector3.zero;
        private bool _isFacingReverse = false;

        MRUKAnchor _foundAnchor = null;
        float _maxVolume = -1f;

        private void Start()
        {
            _placementMotif = GetComponent<PlacementMotif>();
            if (_placementMotif == null)
            {
                Debug.LogWarning($"[{name}] PlacementMotif not found. Offsets will default to zero.");
            }
            else {
                _debugLog = _placementMotif.DebugLog;
                _positionOffset = _placementMotif.PositionOffset;
                _rotationOffset = _placementMotif.RotationOffset;
                _isFacingReverse = _placementMotif.IsFacingReverse;
            }

            if (MRUK.Instance == null)
            {
                if (_debugLog) Debug.LogWarning($"[{name}] MRUK.Instance is null. Fallback to (0,0,0).");
                ApplyFallback();
                return;
            }

            MRUK.Instance.RegisterSceneLoadedCallback(SetPositionFromAnchor);
        }

        private void SetPositionFromAnchor()
        {
            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null)
            {
                if (_debugLog) Debug.LogWarning($"[{name}] MRUK Current Room is null. Fallback to (0,0,0).");
                ApplyFallback();
                return;
            }

            foreach (var anchor in room.Anchors)
            {
                if (anchor == null || anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                    continue;

                if (anchor.HasAnyLabel(_targetLabel))
                {
                    Vector3 curSize = anchor.GetAnchorSize();
                    float curVolume = curSize.x * curSize.y * curSize.z;
                    if (curVolume > _maxVolume)
                    {
                        _maxVolume = curVolume;
                        _foundAnchor = anchor;
                    }
                }
            }

            if (_foundAnchor != null)
            {
                ApplyAnchorTransform(_foundAnchor);
            }
            else
            {
                ApplyFallback();
            }
        }

        private bool IsVerticalSurface(MRUKAnchor.SceneLabels targetLabel)
        {
            return targetLabel == MRUKAnchor.SceneLabels.WALL_FACE || 
                targetLabel == MRUKAnchor.SceneLabels.WINDOW_FRAME || 
                targetLabel == MRUKAnchor.SceneLabels.DOOR_FRAME;
        }

        private void ApplyAnchorTransform(MRUKAnchor anchor)
        {
            Transform anchorTr = anchor.transform;

            // Only follow anchorTransform when it is vertical surface
            Quaternion baseRotation;
            if (IsVerticalSurface(_targetLabel))
            {
                var forward = _isFacingReverse ? -anchorTr.forward : anchorTr.forward;
                baseRotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            else
            {
                baseRotation = Quaternion.identity;
            }

            // As in v1, the anchor's own origin plus the prefab offset. A TABLE's
            // transform is already at its top surface, so there is no half-height lift, and the offset is a world
            // offset, not one rotated by the anchor.
            transform.position = anchorTr.position + _positionOffset;
            transform.rotation = baseRotation * Quaternion.Euler(_rotationOffset);
        }

        private void ApplyFallback()

        {
            transform.position = _positionOffset;
            transform.rotation = Quaternion.Euler(_rotationOffset);
        }

        private void OnDestroy()
        {
            if (MRUK.Instance != null)
            {
                MRUK.Instance.SceneLoadedEvent.RemoveListener(SetPositionFromAnchor);
            }
        }
    }
}
