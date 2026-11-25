// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using SpatialBlend.Interactions.UI;
using Oculus.Interaction;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Popup panel motif dedicated to GLTF objects.
    /// Uses the same hover + custom button activation pattern as ManipulationPanelMotif,
    /// but only supports Confirm/Cancel semantics from GltfObjectControlPanel.
    /// </summary>
    public class GltfObjectControlPanelMotif : MonoBehaviour
    {
        [Header("Visual Feedback")]
        [SerializeField, Tooltip("Highlight component for hover feedback")]
        private HoverHighlight _hoverHighlight;

        [Header("Panel UI")]
        [SerializeField, Tooltip("Prefab for GLTF object control panel")]
        private GameObject _panelPrefab;

        [Header("Settings")]
        [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.RTouch;
        [SerializeField] private bool _respectModeGate = true;
        [SerializeField] private InteractionMode _requiredMode = InteractionMode.SceneEdit;

        // ISDK components
        private RayInteractable _rayInteractable;
        private bool _subscribedToInteractor = false;
        private bool _subscribedToCustomButton = false;

        // State
        private bool _isHovered;
        private bool _isPanelShowing;
        private bool _panelEnabled = true;

        // Runtime panel instance
        private GameObject _panelInstance;

        public bool IsPanelShowing => _isPanelShowing;
        public bool IsPanelEnabled => _panelEnabled;

        /// <summary>
        /// Static helper to add/setup motif on a target.
        /// </summary>
        public static GltfObjectControlPanelMotif Setup(GameObject target, GameObject panelPrefab = null)
        {
            if (target == null)
            {
                return null;
            }

            var motif = target.GetComponent<GltfObjectControlPanelMotif>();
            if (motif == null)
            {
                motif = target.AddComponent<GltfObjectControlPanelMotif>();
            }

            if (panelPrefab != null)
            {
                motif.SetPanelPrefab(panelPrefab);
            }
            return motif;
        }

        public void SetPanelPrefab(GameObject panelPrefab)
        {
            _panelPrefab = panelPrefab;
        }

        private void Awake()
        {
            SetupISDKComponents();
        }

        private void Start()
        {
            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponentInChildren<RayInteractable>();
            }

            SubscribeToSceneRayInteractor();
        }

        private void Update()
        {
            if (!_subscribedToInteractor)
            {
                SubscribeToSceneRayInteractor();
            }

            if (!_subscribedToCustomButton)
            {
                SubscribeToCustomButtonSelector();
            }

            if (_respectModeGate && !InteractionModeManager.IsMode(_requiredMode))
            {
                if (_isPanelShowing)
                {
                    HidePanel();
                }
            }
        }

        private void OnDisable()
        {
            if (_isPanelShowing)
            {
                HidePanel();
            }

            if (_isHovered && _hoverHighlight != null)
            {
                _hoverHighlight.SetHighlighted(false);
            }
        }

        private void OnDestroy()
        {
            UnsubscribeFromSceneRayInteractor();
            UnsubscribeFromCustomButtonSelector();

            if (_panelInstance != null)
            {
                Destroy(_panelInstance);
                _panelInstance = null;
            }
        }

        private void SetupISDKComponents()
        {
            _rayInteractable = GetComponentInChildren<RayInteractable>();
        }

        private void SubscribeToSceneRayInteractor()
        {
            if (_subscribedToInteractor) return;

            // The active editing input's ray: the right controller's or the right hand's.
            EditInput.Hovered += OnInteractorHover;
            EditInput.Unhovered += OnInteractorUnhover;
            _subscribedToInteractor = true;
        }

        private void UnsubscribeFromSceneRayInteractor()
        {
            if (!_subscribedToInteractor) return;

            EditInput.Hovered -= OnInteractorHover;
            EditInput.Unhovered -= OnInteractorUnhover;
            _subscribedToInteractor = false;
        }

        private void SubscribeToCustomButtonSelector()
        {
            if (_subscribedToCustomButton) return;

            var selector = InteractionModeManager.CustomButtonSelector;
            if (selector == null)
            {
                return;
            }

            selector.WhenSelected += OnCustomButtonPressed;
            _subscribedToCustomButton = true;
        }

        private void UnsubscribeFromCustomButtonSelector()
        {
            if (!_subscribedToCustomButton) return;

            var selector = InteractionModeManager.CustomButtonSelector;
            if (selector != null)
            {
                selector.WhenSelected -= OnCustomButtonPressed;
            }

            _subscribedToCustomButton = false;
        }

        internal void OnInteractorHover(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            OnHoverEnter();
        }

        internal void OnInteractorUnhover(EditPointer pointer, RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            OnHoverExit();
        }

        /// <summary>Opens or closes the control panel as A does, whether or not the ray is on the object now (the hands' Menu button).</summary>
        public void ToggleMenu()
        {
            if (!_panelEnabled) return;
            if (_respectModeGate && !InteractionModeManager.IsMode(_requiredMode)) return;
            TogglePanel();
        }

        private void OnHoverEnter()
        {
            _isHovered = true;
            if (_hoverHighlight != null)
            {
                _hoverHighlight.SetHighlighted(true);
            }
        }

        private void OnHoverExit()
        {
            _isHovered = false;
            if (_hoverHighlight != null)
            {
                _hoverHighlight.SetHighlighted(false);
            }
        }

        private void OnCustomButtonPressed()
        {
            if (!_panelEnabled) return;
            if (!_isHovered) return;

            if (_respectModeGate && !InteractionModeManager.IsMode(_requiredMode))
            {
                return;
            }

            TogglePanel();
        }

        private void TogglePanel()
        {
            if (_isPanelShowing)
            {
                HidePanel();
            }
            else
            {
                ShowPanel();
            }
        }

        private void ShowPanel()
        {
            var panelPrefab = _panelPrefab != null
                ? _panelPrefab
                : InteractionModeManager.DefaultGltfObjectControlPanelPrefab;
            if (panelPrefab == null)
            {
                Debug.LogWarning($"[GltfObjectControlPanelMotif:{name}] Panel prefab is not assigned (and no default is set on InteractionModeManager).");
                return;
            }

            if (_panelInstance == null)
            {
                _panelInstance = Instantiate(panelPrefab);
                SetupPanelInstance();
            }

            var popup = _panelInstance.GetComponentInChildren<GltfObjectControlPopUp>(true);
            if (popup != null)
            {
                popup.ResetActionState();
            }

            _panelInstance.SetActive(true);
            _isPanelShowing = true;
        }

        private void HidePanel()
        {
            if (_panelInstance != null)
            {
                _panelInstance.SetActive(false);
            }

            _isPanelShowing = false;
        }

        private void SetupPanelInstance()
        {
            if (_panelInstance == null) return;

            var billboard = _panelInstance.GetComponent<BillboardToCamera>();
            if (billboard == null)
            {
                billboard = _panelInstance.AddComponent<BillboardToCamera>();
            }
            billboard.SetTarget(transform);

            // Disable legacy popup controllers so only GLTF-specific behavior is active.
            var legacyPopups = _panelInstance.GetComponentsInChildren<ManipulationPopUp>(true);
            for (int i = 0; i < legacyPopups.Length; i++)
            {
                if (legacyPopups[i] != null)
                {
                    legacyPopups[i].enabled = false;
                }
            }

            var popup = _panelInstance.GetComponentInChildren<GltfObjectControlPopUp>(true);
            if (popup == null)
            {
                popup = _panelInstance.AddComponent<GltfObjectControlPopUp>();
            }

            WirePanelEvents(popup);
        }

        private void WirePanelEvents(GltfObjectControlPopUp popup)
        {
            popup.OnConfirm.RemoveAllListeners();
            popup.OnCancel.RemoveAllListeners();

            popup.OnConfirm.AddListener(OnConfirmPressed);
            popup.OnCancel.AddListener(OnCancelPressed);
        }

        private void OnConfirmPressed()
        {
            FinalizeGizmoLikeManipulationContext();
            HidePanel();

            if (_panelInstance != null)
            {
                Destroy(_panelInstance);
                _panelInstance = null;
            }

            _panelEnabled = false;
            Destroy(this);
        }

        private void OnCancelPressed()
        {
            HidePanel();
        }

        /// <summary>
        /// Confirm ends the placement session for a GLB object. v1 also marked the
        /// SbEntity ready to share here. v2 has no such gate: the source shares
        /// once its content is ready and only a source writes state, so confirming
        /// only retires this panel. Returns true when the target is a native entity.
        /// </summary>
        private bool FinalizeGizmoLikeManipulationContext()
        {
            var targetNativeEntity = GetComponent<SbNativeEntity>();
            if (targetNativeEntity == null)
            {
                return false;
            }
            return true;
        }
    }
}
