// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using Oculus.Interaction;

namespace SpatialBlend.Interactions
{
    public enum InteractionMode
    {
        SceneEdit,
        Interaction
    }

    /// <summary>
    /// Central mode switch for scene editing vs real-time interaction.
    /// Defaults to SceneEdit to preserve existing placement/annotation flows.
    /// </summary>
    public class InteractionModeManager : MonoBehaviour
    {
        public static InteractionModeManager Instance { get; private set; }
        public static event Action<InteractionMode> ModeChanged;

        [SerializeField] private InteractionMode _startingMode = InteractionMode.SceneEdit;

        [Header("ISDK References")]
        [SerializeField, Tooltip("RayInteractor for the right controller. Assign in inspector.")]
        private RayInteractor _interactor;
        [SerializeField, Tooltip("RayInteractor for the right hand (pinch to select). Optional: when empty, the rig's right HandRayInteractor is found at run time.")]
        private RayInteractor _handInteractor;

        // Auto-created in Start if not found
        private CustomButtonSelector _customButtonSelector;

        [Header("Default Prefabs")]
        [SerializeField, Tooltip("Default panel prefab for ManipulationPanelMotif. Assign in inspector.")]
        private GameObject _defaultManipulationPanelPrefab;
        [SerializeField, Tooltip("Default panel prefab for GltfObjectControlPanelMotif. Assign in inspector.")]
        private GameObject _defaultGltfObjectControlPanelPrefab;
        [SerializeField, Tooltip("Prefab for passthrough camera panel (spawned by Reconstruct 3D).")]
        private GameObject _defaultPassthroughPanelPrefab;

        [Header("Debug")]
        [SerializeField, Tooltip("Enable debug logging for interaction system.")]
        private bool _debugLog = false;

        [Tooltip("Show the declared-region outlines. Off hides them in every mode (as v1, shipped on in the scene).")]
        public bool _visualizeAffordanceVolumes = true;

        public InteractionMode CurrentMode { get; private set; } = InteractionMode.SceneEdit;

        /// <summary>
        /// Whether declared-region outlines may be drawn at all. True without a mode manager, as in v1, where every
        /// reader guarded with <c>Instance != null &amp;&amp; !_visualizeAffordanceVolumes</c>.
        /// </summary>
        public static bool VisualizeAffordanceVolumes => Instance == null || Instance._visualizeAffordanceVolumes;

        /// <summary>
        /// Whether debug logging is enabled for the interaction system.
        /// </summary>
        public static bool DebugLog => Instance != null && Instance._debugLog;

        /// <summary>
        /// Default panel prefab for ManipulationPanelMotif.
        /// </summary>
        public static GameObject DefaultManipulationPanelPrefab => Instance?._defaultManipulationPanelPrefab;

        /// <summary>
        /// Default panel prefab for GltfObjectControlPanelMotif.
        /// </summary>
        public static GameObject DefaultGltfObjectControlPanelPrefab => Instance?._defaultGltfObjectControlPanelPrefab;

        /// <summary>
        /// Default prefab for PassthroughPanel (Reconstruct 3D workflow).
        /// </summary>
        public static GameObject DefaultPassthroughPanelPrefab => Instance?._defaultPassthroughPanelPrefab;

        /// <summary>
        /// The scene's Interactor for placement/interaction.
        /// </summary>
        public static RayInteractor Interactor => Instance?._interactor;

        /// <summary>
        /// The right hand's RayInteractor when the scene assigns one; otherwise null and <see cref="EditInputRouter"/> finds the rig's. Motifs do not
        /// read either interactor: they listen to <see cref="EditInput"/>, which carries whichever input is active.
        /// </summary>
        public static RayInteractor HandInteractor => Instance?._handInteractor;

        /// <summary>
        /// The scene's CustomButtonSelector for A button events.
        /// </summary>
        public static CustomButtonSelector CustomButtonSelector => Instance?._customButtonSelector;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            SetMode(_startingMode);
            if (GetComponent<EditInputRouter>() == null) gameObject.AddComponent<EditInputRouter>();
        }

        private void Start()
        {
            // Auto-create CustomButtonSelector if not assigned
            if (_customButtonSelector == null)
            {
                SetupCustomButtonSelector();
            }
        }

        private void SetupCustomButtonSelector()
        {
            // Try to add as sibling to RayInteractor, or on this gameObject
            GameObject targetGO = _interactor != null ? _interactor.gameObject : gameObject;

            _customButtonSelector = targetGO.GetComponent<CustomButtonSelector>();
            if (_customButtonSelector == null)
            {
                _customButtonSelector = targetGO.AddComponent<CustomButtonSelector>();
            }
        }

        public void SetMode(InteractionMode mode)
        {
            if (CurrentMode == mode) return;
            CurrentMode = mode;
            ModeChanged?.Invoke(mode);
        }

        public static bool IsMode(InteractionMode mode)
        {
            return Instance == null || Instance.CurrentMode == mode;
        }
    }
}
