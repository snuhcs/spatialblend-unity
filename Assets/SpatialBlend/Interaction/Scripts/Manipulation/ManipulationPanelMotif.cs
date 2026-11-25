// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using SpatialBlend.Interactions.UI;
using Oculus.Interaction;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Per-entity component for manipulation interactions.
    /// Attach to any entity that should be manipulable (hover highlight, annotation panel, etc.).
    /// Expects RayInteractable to already exist (added by PlacementMotif or other motifs).
    /// Press A button while hovering to toggle the manipulation panel.
    /// </summary>
    public class ManipulationPanelMotif : MonoBehaviour
    {
        [Header("Visual Feedback")]
        [SerializeField, Tooltip("Highlight component for hover feedback")]
        private HoverHighlight _hoverHighlight;

        [Header("Panel UI")]
        [SerializeField, Tooltip("Prefab for annotation selection panel")]
        private GameObject _annotationPanelPrefab;

        [Header("Settings")]
        [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.RTouch;
        [SerializeField] private bool _respectModeGate = true;
        [SerializeField] private InteractionMode _requiredMode = InteractionMode.SceneEdit;

        // ISDK components (found, not created - other motifs add them)
        private RayInteractable _rayInteractable;
        private bool _subscribedToInteractor = false;
        private bool _subscribedToCustomButton = false;

        // State
        private bool _isHovered;

        // Context manages panel and mode state
        private ManipulationContext _context;

        /// <summary>
        /// The ManipulationContext for this entity.
        /// </summary>
        public ManipulationContext Context => _context;

        /// <summary>
        /// Whether this motif only acts in <paramref name="mode"/> (mode gate on, required mode equal).
        /// </summary>
        public bool IsGatedTo(InteractionMode mode) => _respectModeGate && _requiredMode == mode;

        /// <summary>
        /// Whether the panel is currently showing (via Context).
        /// </summary>
        public bool IsPanelShowing => _context?.IsPanelShowing ?? false;

        /// <summary>
        /// Static helper to add ManipulationPanelMotif to a GameObject.
        /// Returns existing component if already present.
        /// </summary>
        public static ManipulationPanelMotif Setup(GameObject target)
        {
            if (target == null) return null;

            var motif = target.GetComponent<ManipulationPanelMotif>();
            if (motif == null)
            {
                motif = target.AddComponent<ManipulationPanelMotif>();
            }
            return motif;
        }

        private void Awake()
        {
            // Setup ISDK components
            SetupISDKComponents();
        }

        private void Start()
        {
            // Retry finding RayInteractable if not found in Awake
            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponentInChildren<RayInteractable>();
            }

            if (_rayInteractable == null)
            {
                Debug.LogWarning($"[ManipulationPanelMotif:{name}] No RayInteractable found. A button panel won't work.");
            }

            // Subscribe to scene RayInteractor for hover/select events
            SubscribeToSceneRayInteractor();
        }

        /// <summary>
        /// Looks for the entity's RayInteractable again when none was found at Awake and Start. A received copy is made before its model
        /// loads, so its collider and interactable may only exist once the content is ready.
        /// </summary>
        public void RebindInteractable()
        {
            if (_rayInteractable == null) _rayInteractable = GetComponentInChildren<RayInteractable>();
        }

        private void SetupISDKComponents()
        {
            // Find existing RayInteractable (added by PlacementMotif, SbEntity, or BoxAnnotationInstance)
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

        /// <summary>
        /// Opens or closes the object menu, as A does, but for this motif's object whether or not the ray is on it now. The Menu button that hands see
        /// floats over the object, so by the time it is pressed the ray has left the object.
        /// </summary>
        public void ToggleMenu()
        {
            if (_respectModeGate && !InteractionModeManager.IsMode(_requiredMode)) return;
            EnsureContext();
            _context.TogglePanel(_annotationPanelPrefab);
        }

        /// <summary>Whether the menu is open for this motif's object.</summary>
        public bool IsMenuOpen => IsPanelShowing;

        private void OnCustomButtonPressed()
        {
            if (_respectModeGate && !InteractionModeManager.IsMode(_requiredMode))
            {
                return;
            }

            if (_isHovered)
            {
                // Create or get context for this target
                EnsureContext();

                // Toggle panel via context
                _context.TogglePanel(_annotationPanelPrefab);
            }
        }

        private void EnsureContext()
        {
            if (_context == null)
            {
                _context = ManipulationContext.FindContextForTarget(gameObject);
                if (_context == null)
                {
                    _context = ManipulationContext.Create(gameObject);
                }
                _context.SetPanelMotif(this);
            }
        }

        private void UnsubscribeFromSceneRayInteractor()
        {
            if (!_subscribedToInteractor) return;

            EditInput.Hovered -= OnInteractorHover;
            EditInput.Unhovered -= OnInteractorUnhover;
            _subscribedToInteractor = false;
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

        private void Update()
        {
            if (!Application.isFocused) return;

            // Retry subscribing if not yet subscribed
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
                if (_context != null && _context.IsPanelShowing)
                {
                    _context.HidePanel();
                }
                return;
            }

            // Button handling is done via CustomButtonSelector events (OnCustomButtonPressed)
            // Panel positioning is handled by BillboardToCamera component via Context
        }

        /// <summary>
        /// Called when hover begins on this entity.
        /// </summary>
        private void OnHoverEnter()
        {
            _isHovered = true;

            if (_hoverHighlight != null)
            {
                _hoverHighlight.SetHighlighted(true);
            }
        }

        /// <summary>
        /// Called when hover ends on this entity.
        /// </summary>
        private void OnHoverExit()
        {
            _isHovered = false;

            if (_hoverHighlight != null)
            {
                _hoverHighlight.SetHighlighted(false);
            }

            // Panel stays visible until user presses button again (toggle behavior)
        }

        private void OnDisable()
        {
            if (_context != null && _context.IsPanelShowing)
            {
                _context.HidePanel();
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
        }
    }
}
