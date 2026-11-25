// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR.Samples;
using UnityEngine;
using Oculus.Interaction;
using SpatialBlend.Interactions;

namespace SpatialBlend.Samples.Objects.Whiteboard
{
    public class PointerHandlerMotif : MonoBehaviour
    {
        [Header("ISDK Interaction")]
        [SerializeField, Tooltip("Assign from prefab or auto-found at runtime.")]
        private RayInteractable _rayInteractable;

        [Header("Raycast Settings")]
        [SerializeField, Tooltip("Assign the specific colliders you want the pointer to hit (e.g. the Whiteboard surface mesh).")]
        private Collider[] targetColliders;

        [SerializeField] private LayerMask targetLayer = 1 << 7; // 'VirtualObject' layer

        [Header("Hit Indicator Settings")]
        [Tooltip("Assign a prefab that represents a small black circle.")]
        [SerializeField] private GameObject hitIndicatorPrefab;

        /// <summary>
        /// A stroke is a use of the board at arm's reach. The board's own placement hover is 1.4 m; past this the ray
        /// still meets the surface from across the room, and that stroke is sent to every peer.
        /// </summary>
        public const float MaxDrawDistanceMetres = 1.5f;

        public bool IsHovered { get; private set; }
        public bool IsSelected { get; private set; }
        public bool HasValidHit { get; private set; }
        public Vector3 HitPoint { get; private set; }

        private GameObject m_hitIndicator;
        private bool _subscribedToInteractor = false;
        private RayInteractor _activeInteractor;
        private RayInteractor _subscribedInteractor;
        // After the pen was held by Scene Edit with the trigger down, the trigger must be let go once before it draws again, so a trigger
        // still held from an edit (a drag, then the chip or the idle return) never starts a stroke on its own.
        private bool _awaitTriggerRelease;
        private bool _subscribedToModes;

        private void Awake()
        {
            if (hitIndicatorPrefab != null)
            {
                m_hitIndicator = Instantiate(hitIndicatorPrefab, transform);
                m_hitIndicator.SetActive(false);
            }
        }

        private void Start()
        {
            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponentInChildren<RayInteractable>();
            }

            if (_rayInteractable == null)
            {
                Debug.LogError($"[PointerHandlerMotif:{name}] No RayInteractable found!");
            }

            if (targetColliders == null || targetColliders.Length == 0)
            {
                Debug.LogWarning($"[PointerHandlerMotif:{name}] targetColliders array is empty. The pointer raycast will not hit anything.");
            }

            SubscribeToInteractor();
        }

        private void Update()
        {
            if (_subscribedToInteractor && _subscribedInteractor != InteractionModeManager.Interactor)
            {
                IsHovered = false;
                _activeInteractor = null;
                ClearObservation();
                UnsubscribeFromInteractor();
            }

            // Retry subscription if not yet subscribed
            if (!_subscribedToInteractor)
            {
                SubscribeToInteractor();
            }

            // Board pen input is local to its owned collider. It must not depend on
            // avatar display/transfer state: v1 observed a hovered board ray directly.
            if (IsHovered && _activeInteractor != null)
            {
                var controller = _activeInteractor.GetComponentInParent<Oculus.Interaction.Input.IController>();
                bool triggerDown = controller != null &&
                    controller.IsButtonUsageAnyActive(Oculus.Interaction.Input.ControllerButtonUsage.TriggerButton);
                Observe(new Ray(_activeInteractor.Origin, _activeInteractor.Forward), triggerDown, out _);
            }
            else
            {
                ClearObservation();
            }
        }

        /// <summary>
        /// Whether the pen is held off because the scene is in Scene Edit ("Editing"). In Editing the same trigger-drag moves the board
        /// (<c>PanelPlacementMotif</c>), and one input must not also draw a stroke that every peer receives. Drawing is a use of
        /// the board, so it belongs to Interaction mode. With no mode manager in the scene (the Foundation local build) the pen is never held.
        /// </summary>
        public static bool PenHeldByEditMode
        {
            get
            {
                var modes = InteractionModeManager.Instance;
                return modes != null && modes.CurrentMode == SpatialBlend.Interactions.InteractionMode.SceneEdit;
            }
        }

        /// <summary>
        /// One frame of the pointer: where the ray hits the board and whether the trigger is drawing. In Scene Edit nothing is observed
        /// (no hit dot, no selection), so a stroke in progress ends the frame the mode becomes Scene Edit. Shared production/test seam.
        /// </summary>
        public void Observe(Ray ray, bool triggerDown)
        {
            Observe(ray, triggerDown, out _);
        }

        // Update uses the value already evaluated by Observe for diagnostics;
        // direct production/test callers retain the public two-argument seam.
        private void Observe(Ray ray, bool triggerDown, out bool heldByEditMode)
        {
            heldByEditMode = PenHeldByEditMode;
            if (heldByEditMode)
            {
                if (triggerDown) _awaitTriggerRelease = true;
                ClearObservation();
                return;
            }
            if (!triggerDown) _awaitTriggerRelease = false;
            RefreshHit(ray);
            IsSelected = HasValidHit && triggerDown && !_awaitTriggerRelease;
        }

        // The frame the mode becomes Scene Edit the selection ends at once, whatever order this and the drawing motif update in, so not one
        // more segment goes out.
        private void OnModeChanged(SpatialBlend.Interactions.InteractionMode mode)
        {
            if (mode != SpatialBlend.Interactions.InteractionMode.SceneEdit) return;
            if (IsSelected) _awaitTriggerRelease = true;
            ClearObservation();
        }

        private void OnEnable()
        {
            if (_subscribedToModes) return;
            InteractionModeManager.ModeChanged += OnModeChanged;
            _subscribedToModes = true;
        }

        // Shared production/test seam: evaluates only the serialized owned
        // colliders, never a scene-wide Physics.Raycast.
        public void RefreshHit(Ray ray)
        {
            if (!IsFinite(ray.origin) || !IsFinite(ray.direction) || ray.direction.sqrMagnitude < 1e-12f)
            {
                ClearObservation();
                return;
            }

            int targetLayerValue = targetLayer.value;

            bool hitFound = false;
            RaycastHit closestHit = new RaycastHit();
            float minDistance = float.MaxValue;

            if (targetColliders != null)
            {
                foreach (var col in targetColliders)
                {
                    if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;

                    int layerMask = 1 << col.gameObject.layer;
                    if ((layerMask & targetLayerValue) == 0)
                    {
                        continue; // Skip colliders not on the target layer
                    }

                    if (col.Raycast(ray, out var hit, MaxDrawDistanceMetres))
                    {
                        if (hit.distance < minDistance)
                        {
                            minDistance = hit.distance;
                            closestHit = hit;
                            hitFound = true;
                        }
                    }
                }
            }

            if (hitFound)
            {
                HitPoint = closestHit.point;
                HasValidHit = true;
                if (m_hitIndicator)
                {
                    m_hitIndicator.transform.position = closestHit.point;
                    m_hitIndicator.transform.rotation = Quaternion.LookRotation(closestHit.normal);
                    m_hitIndicator.SetActive(true);
                }
            }
            else
            {
                ClearObservation();
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        private void SubscribeToInteractor()
        {
            if (_subscribedToInteractor) return;
            if (_rayInteractable == null) return;

            var interactor = InteractionModeManager.Interactor;
            if (interactor == null) return;

            interactor.WhenInteractableSet.Action += OnInteractorHover;
            interactor.WhenInteractableUnset.Action += OnInteractorUnhover;
            _subscribedInteractor = interactor;
            _subscribedToInteractor = true;
        }

        private void UnsubscribeFromInteractor()
        {
            if (!_subscribedToInteractor) return;

            if (_subscribedInteractor != null)
            {
                _subscribedInteractor.WhenInteractableSet.Action -= OnInteractorHover;
                _subscribedInteractor.WhenInteractableUnset.Action -= OnInteractorUnhover;
            }
            _subscribedToInteractor = false;
            _subscribedInteractor = null;
        }

        private void OnDestroy()
        {
            UnsubscribeFromInteractor();
        }

        private void OnDisable()
        {
            if (_subscribedToModes) InteractionModeManager.ModeChanged -= OnModeChanged;
            _subscribedToModes = false;
            IsHovered = false;
            _activeInteractor = null;
            ClearObservation();
            UnsubscribeFromInteractor();
        }

        private void ClearObservation()
        {
            HasValidHit = false;
            IsSelected = false;
            if (m_hitIndicator) m_hitIndicator.SetActive(false);
        }

        private void OnInteractorHover(RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            IsHovered = true;
            _activeInteractor = _subscribedInteractor;
        }

        private void OnInteractorUnhover(RayInteractable interactable)
        {
            if (interactable != _rayInteractable) return;
            IsHovered = false;
            _activeInteractor = null;
            ClearObservation();
        }
    }
}
