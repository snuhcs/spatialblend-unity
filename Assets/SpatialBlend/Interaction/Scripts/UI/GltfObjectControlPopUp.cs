// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SpatialBlend.Interactions.UI
{
    /// <summary>
    /// Minimal popup controller for GLTF object control.
    /// Handles only Confirm/Cancel actions.
    /// </summary>
    public class GltfObjectControlPopUp : MonoBehaviour
    {
        [Header("Action Toggles")]
        [SerializeField] private Toggle _confirmToggle;
        [SerializeField] private Toggle _cancelToggle;

        [Header("Events")]
        public UnityEvent OnConfirm;
        public UnityEvent OnCancel;

        private void OnEnable()
        {
            if (_confirmToggle != null)
            {
                _confirmToggle.onValueChanged.AddListener(OnConfirmToggleChanged);
                EnsureHoverVisual(_confirmToggle);
            }

            if (_cancelToggle != null)
            {
                _cancelToggle.onValueChanged.AddListener(OnCancelToggleChanged);
                EnsureHoverVisual(_cancelToggle);
            }

            ResetActionState();
        }

        private void OnDisable()
        {
            if (_confirmToggle != null)
            {
                _confirmToggle.onValueChanged.RemoveListener(OnConfirmToggleChanged);
            }

            if (_cancelToggle != null)
            {
                _cancelToggle.onValueChanged.RemoveListener(OnCancelToggleChanged);
            }
        }

        public void SetButtons(Toggle confirmToggle, Toggle cancelToggle)
        {
            _confirmToggle = confirmToggle;
            _cancelToggle = cancelToggle;
        }


        public void ResetActionState()
        {
            if (_confirmToggle != null)
            {
                _confirmToggle.SetIsOnWithoutNotify(false);
            }

            if (_cancelToggle != null)
            {
                _cancelToggle.SetIsOnWithoutNotify(false);
            }
        }

        private void OnConfirmToggleChanged(bool isOn)
        {
            if (!isOn) return;

            OnConfirm?.Invoke();
            if (_confirmToggle != null) _confirmToggle.SetIsOnWithoutNotify(false);
        }

        private void OnCancelToggleChanged(bool isOn)
        {
            if (!isOn) return;

            OnCancel?.Invoke();
            if (_cancelToggle != null) _cancelToggle.SetIsOnWithoutNotify(false);
        }

        private static void EnsureHoverVisual(Toggle toggle)
        {
            if (toggle == null) return;
            if (toggle.GetComponent<ToggleHoverVisual>() == null)
            {
                toggle.gameObject.AddComponent<ToggleHoverVisual>();
            }
        }
    }
}
