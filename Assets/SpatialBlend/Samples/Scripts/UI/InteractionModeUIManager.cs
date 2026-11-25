// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.UI;
using SpatialBlend.Interactions;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Binds two toggles to InteractionModeManager (Interaction / SceneEdit).
    /// Keeps UI and runtime mode in sync in both directions.
    /// </summary>
    [DisallowMultipleComponent]
    public class InteractionModeUIManager : MonoBehaviour
    {
        [Header("Mode Toggles")]
        [SerializeField] private Toggle _sceneEditModeToggle;
        [SerializeField] private Toggle _interactionModeToggle;

        [Header("Debug")]
        [SerializeField] private bool _debugLog = false;

        private bool _isSyncingUI;
        private bool _isSubscribed;
        private ToggleGroup _modeToggleGroup;

        private void OnEnable()
        {
            if (_isSubscribed)
            {
                SyncToggleStateFromCurrentMode();
                return;
            }
            if (_interactionModeToggle == null || _sceneEditModeToggle == null)
            {
                Debug.LogWarning(
                    $"[InteractionModeUIManager:{name}] Toggle reference missing. " +
                    $"interactionToggle={_interactionModeToggle != null}, sceneEditToggle={_sceneEditModeToggle != null}");
            }

            ConfigureToggleGroup();

            if (_interactionModeToggle != null)
            {
                _interactionModeToggle.onValueChanged.AddListener(OnInteractionModeToggleChanged);
            }

            if (_sceneEditModeToggle != null)
            {
                _sceneEditModeToggle.onValueChanged.AddListener(OnSceneEditModeToggleChanged);
            }

            InteractionModeManager.ModeChanged += OnModeChanged;
            _isSubscribed = true;
            SyncToggleStateFromCurrentMode();
        }

        private void OnDisable()
        {
            if (!_isSubscribed)
            {
                return;
            }
            if (_interactionModeToggle != null)
            {
                _interactionModeToggle.onValueChanged.RemoveListener(OnInteractionModeToggleChanged);
            }

            if (_sceneEditModeToggle != null)
            {
                _sceneEditModeToggle.onValueChanged.RemoveListener(OnSceneEditModeToggleChanged);
            }

            InteractionModeManager.ModeChanged -= OnModeChanged;
            _isSubscribed = false;
        }

        /// <summary>
        /// Matches ControlPanel's semantics:
        /// isOn=true => SceneEdit, isOn=false => Interaction.
        /// Useful when binding an external toggle.
        /// </summary>
        public void OnSceneManipulationToggled(bool isOn)
        {
            SetMode(isOn ? InteractionMode.SceneEdit : InteractionMode.Interaction);
        }

        public void SetInteractionMode()
        {
            SetMode(InteractionMode.Interaction);
        }

        public void SetSceneEditMode()
        {
            SetMode(InteractionMode.SceneEdit);
        }

        private void OnInteractionModeToggleChanged(bool isOn)
        {
            if (_isSyncingUI || !isOn)
            {
                return;
            }

            SetInteractionMode();
        }

        private void OnSceneEditModeToggleChanged(bool isOn)
        {
            if (_isSyncingUI || !isOn)
            {
                return;
            }

            SetSceneEditMode();
        }

        private void OnModeChanged(InteractionMode mode)
        {
            SyncToggleState(mode);
        }

        private void SetMode(InteractionMode mode)
        {
            var manager = InteractionModeManager.Instance;
            if (manager == null)
            {
                if (_debugLog) { Debug.LogWarning("[InteractionModeUIManager] InteractionModeManager.Instance is null"); }
                return;
            }

            manager.SetMode(mode);
            SyncToggleState(mode);
        }

        private void SyncToggleStateFromCurrentMode()
        {
            var manager = InteractionModeManager.Instance;
            var mode = manager != null ? manager.CurrentMode : InteractionMode.SceneEdit;
            SyncToggleState(mode);
        }

        private void SyncToggleState(InteractionMode mode)
        {
            _isSyncingUI = true;
            try
            {
                bool interactionShouldBeOn = mode == InteractionMode.Interaction;

                // Use the property setter (not SetIsOnWithoutNotify) so third-party toggle visuals
                // that listen to onValueChanged (e.g., animator layer controllers) also update.
                Toggle activeToggle = interactionShouldBeOn ? _interactionModeToggle : _sceneEditModeToggle;
                Toggle inactiveToggle = interactionShouldBeOn ? _sceneEditModeToggle : _interactionModeToggle;
                if (activeToggle != null && !activeToggle.isOn)
                {
                    activeToggle.isOn = true;
                }
                if (inactiveToggle != null && inactiveToggle.isOn)
                {
                    inactiveToggle.isOn = false;
                }
            }
            finally
            {
                _isSyncingUI = false;
            }
        }

        private void ConfigureToggleGroup()
        {
            if (_interactionModeToggle == null || _sceneEditModeToggle == null)
            {
                return;
            }

            Transform commonParent = FindCommonParent(_interactionModeToggle.transform, _sceneEditModeToggle.transform);
            var host = commonParent != null ? commonParent.gameObject : gameObject;

            _modeToggleGroup = host.GetComponent<ToggleGroup>();
            if (_modeToggleGroup == null)
            {
                _modeToggleGroup = host.AddComponent<ToggleGroup>();
            }

            _modeToggleGroup.allowSwitchOff = false;
            _interactionModeToggle.group = _modeToggleGroup;
            _sceneEditModeToggle.group = _modeToggleGroup;
        }

        private static Transform FindCommonParent(Transform a, Transform b)
        {
            if (a == null || b == null)
            {
                return null;
            }

            for (Transform current = a; current != null; current = current.parent)
            {
                for (Transform other = b; other != null; other = other.parent)
                {
                    if (current == other)
                    {
                        return current;
                    }
                }
            }

            return null;
        }
    }
}
