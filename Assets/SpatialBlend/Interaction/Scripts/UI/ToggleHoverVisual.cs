// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpatialBlend.Interactions.UI
{
    /// <summary>
    /// Provides hover visual feedback for Unity UI Toggles.
    /// Scales and/or changes color when pointer enters/exits.
    /// </summary>
    [RequireComponent(typeof(Toggle))]
    public class ToggleHoverVisual : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Scale Feedback")]
        [SerializeField] private bool _useScaleFeedback = true;
        [SerializeField] private float _hoverScale = 1.1f;
        [SerializeField] private float _scaleTransitionSpeed = 10f;

        [Header("Color Feedback")]
        [SerializeField] private bool _useColorFeedback = true;
        [SerializeField] private Color _hoverColor = Color.white;
        [SerializeField] private float _colorTransitionSpeed = 10f;

        [Header("Target")]
        [SerializeField, Tooltip("Optional: specific graphic to modify. If null, uses Toggle's targetGraphic.")]
        private Graphic _targetGraphic;

        private Toggle _toggle;
        private Vector3 _originalScale;
        private Color _originalColor;
        private bool _isHovered;
        private float _currentScaleT;
        private float _currentColorT;

        private void Awake()
        {
            _toggle = GetComponent<Toggle>();
            _originalScale = transform.localScale;

            if (_targetGraphic == null && _toggle != null)
            {
                _targetGraphic = _toggle.targetGraphic;
            }

            if (_targetGraphic != null)
            {
                _originalColor = _targetGraphic.color;
            }
        }

        private void OnEnable()
        {
            // Reset state
            _isHovered = false;
            _currentScaleT = 0f;
            _currentColorT = 0f;
            transform.localScale = _originalScale;
            if (_targetGraphic != null)
            {
                _targetGraphic.color = _originalColor;
            }
        }

        private void Update()
        {
            float targetT = _isHovered ? 1f : 0f;

            // Animate scale
            if (_useScaleFeedback)
            {
                _currentScaleT = Mathf.MoveTowards(_currentScaleT, targetT, _scaleTransitionSpeed * Time.deltaTime);
                float scale = Mathf.Lerp(1f, _hoverScale, _currentScaleT);
                transform.localScale = _originalScale * scale;
            }

            // Animate color
            if (_useColorFeedback && _targetGraphic != null)
            {
                _currentColorT = Mathf.MoveTowards(_currentColorT, targetT, _colorTransitionSpeed * Time.deltaTime);
                _targetGraphic.color = Color.Lerp(_originalColor, _hoverColor, _currentColorT);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _isHovered = true;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _isHovered = false;
        }

        /// <summary>
        /// Manually set hover state (for programmatic control).
        /// </summary>
        public void SetHovered(bool hovered)
        {
            _isHovered = hovered;
        }

        /// <summary>
        /// Reset to original appearance immediately.
        /// </summary>
        public void ResetVisuals()
        {
            _isHovered = false;
            _currentScaleT = 0f;
            _currentColorT = 0f;
            transform.localScale = _originalScale;
            if (_targetGraphic != null)
            {
                _targetGraphic.color = _originalColor;
            }
        }
    }
}
