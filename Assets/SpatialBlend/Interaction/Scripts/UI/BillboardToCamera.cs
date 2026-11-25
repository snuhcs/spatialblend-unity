// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Interactions.UI
{
    /// <summary>
    /// Billboard that faces the camera. Positions at target bounds center (XZ) and top + gap (Y). The panel is an authored room panel
    /// (<see cref="UiMetrics.RoomPrefab"/>: the object menu, the glTF object panel), so it grows with its distance from the camera past
    /// 1.2 m, keeping its text at 20 dmm (the room widgets' size), up to the surface's cap (1.4x: the menu stays under 1 m wide, so it does not take rays meant for
    /// the objects around it); it grows upward from its lowest edge, so the gap above the object stays. A box's panel held at the fixed
    /// height beside the box (<see cref="SetVerticalOffsetClamp"/>) never grows: its offsets are tuned for its own size and a larger panel
    /// would cover the box again.
    /// </summary>
    public class BillboardToCamera : MonoBehaviour
    {
        [Header("Camera")]
        [SerializeField] private Camera _targetCamera;

        [Header("Target")]
        [SerializeField, Tooltip("Target transform to position above. If null, must be set via SetTarget().")]
        private Transform _target;

        [Header("Positioning")]
        [SerializeField, Tooltip("Gap between target top and billboard pivot")]
        private float _gap = 0.3f;

        [SerializeField, Tooltip("Offset toward camera along horizontal look direction. Keeps panel in front of target.")]
        private float _forwardOffset = 0f;

        [SerializeField, Tooltip("If > 0, panel Y is fixed to this value when (targetBounds.max.y + gap) exceeds it.")]
        private float _clampOffsetY = 0f;

        [Header("Interpolation")]
        [SerializeField, Tooltip("Smoothing speed for rotation")]
        private float _smoothSpeed = 10f;

        private bool _initialized;
        private bool _useVerticalOffsetClamp;
        private Vector3 _baseScale;
        private float _belowPivot;   // how far the panel reaches below its pivot at the base scale (metres)

        private void Awake()
        {
            if (_targetCamera == null)
            {
                _targetCamera = Camera.main;
            }
        }

        private void Start()
        {
            if (_target != null)
            {
                Initialize();
            }
        }

        /// <summary>
        /// Set the target transform to position above.
        /// </summary>
        public void SetTarget(Transform target)
        {
            _target = target;
            Initialize();
        }

        /// <summary>
        /// Set horizontal offset toward camera. Positive values push the panel closer to the user.
        /// </summary>
        public void SetForwardOffset(float offset)
        {
            _forwardOffset = offset;
        }

        /// <summary>
        /// Fixes panel Y using original formula (targetBounds.max.y + _gap) with clamp.
        /// Rule: if (targetBounds.max.y + _gap) > fixedOffsetY, set panel Y to fixedOffsetY.
        /// When this fixed-offset rule is applied, panel is shifted to targetBounds.max.x + _gap * 3
        /// so it does not occlude the box.
        /// </summary>
        public void SetVerticalOffsetClamp(float fixedOffsetY)
        {
            _clampOffsetY = Mathf.Max(0f, fixedOffsetY);
            if (_clampOffsetY > 0f)
            {
                _useVerticalOffsetClamp = true;
            }
        }

        private void Initialize()
        {
            if (_target == null) return;
            if (!_initialized) MeasureBase();
            UpdatePosition();
            UpdateRotationImmediate();
            _initialized = true;
        }

        private void LateUpdate()
        {
            if (_targetCamera == null || !_initialized) return;
            UpdatePosition();
            UpdateRotation();
        }

        private void UpdatePosition()
        {
            if (_target == null)
            {
                return;
            }

            // Get target bounds from colliders
            Bounds targetBounds = new Bounds(_target.position, Vector3.zero);
            var colliders = _target.GetComponentsInChildren<Collider>();
            if (colliders.Length > 0)
            {
                targetBounds = colliders[0].bounds;
                for (int i = 1; i < colliders.Length; i++)
                {
                    targetBounds.Encapsulate(colliders[i].bounds);
                }
            }

            float rawPanelY = targetBounds.max.y + _gap;
            bool isClamped = _useVerticalOffsetClamp && rawPanelY > _clampOffsetY;
            float panelY = isClamped ? _clampOffsetY : rawPanelY;

            // Base position: XZ at bounds center, Y at bounds top + gap
            Vector3 pos = new Vector3(
                targetBounds.center.x,
                panelY,
                targetBounds.center.z
            );

            if (isClamped)
            {
                pos.x = targetBounds.max.x + _gap * 3;
            }

            // Offset toward camera so panel is between user and object
            if (_forwardOffset > 0f && _targetCamera != null)
            {
                Vector3 toCamera = _targetCamera.transform.position - targetBounds.center;
                toCamera.y = 0f;
                if (toCamera.sqrMagnitude > 0.0001f)
                {
                    Vector3 dir = toCamera.normalized;
                    // At minimum, push panel to the camera-side face of the bounding box
                    float halfExtent = Vector3.Dot(targetBounds.extents, new Vector3(
                        Mathf.Abs(dir.x), 0f, Mathf.Abs(dir.z)));
                    float offset = Mathf.Max(_forwardOffset, halfExtent + 0.1f);
                    pos += dir * offset;
                }
            }

            float growth = isClamped ? 1f : Growth(pos);
            if (_baseScale != Vector3.zero) transform.localScale = _baseScale * growth;
            pos.y += _belowPivot * (growth - 1f);
            transform.position = pos;
        }

        /// <summary>The scale and the reach below the pivot at the prefab's own scale, before any growth.</summary>
        private void MeasureBase()
        {
            _baseScale = transform.localScale;
            _belowPivot = 0f;
            var corners = new Vector3[4];
            foreach (var rect in GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.GetComponent<Canvas>() == null) continue;
                rect.GetWorldCorners(corners);
                foreach (var c in corners) _belowPivot = Mathf.Max(_belowPivot, transform.position.y - c.y);
            }
        }

        private float Growth(Vector3 position) =>
            _targetCamera == null ? 1f : UiMetrics.RoomPrefab.Growth(Vector3.Distance(_targetCamera.transform.position, position));

        private void UpdateRotation()
        {
            Vector3 cameraPos = _targetCamera.transform.position;

            // Face camera (billboard)
            Vector3 lookDir = cameraPos - transform.position;
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(-lookDir, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * _smoothSpeed);
            }
        }

        private void UpdateRotationImmediate()
        {
            if (_targetCamera == null) return;

            Vector3 cameraPos = _targetCamera.transform.position;
            Vector3 lookDir = cameraPos - transform.position;
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.LookRotation(-lookDir, Vector3.up);
            }
        }
    }
}
