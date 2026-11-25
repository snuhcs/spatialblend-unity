// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Manager that adds ISDK interactability to MRUK environment surfaces.
    /// Hooks into MRUK room lifecycle and adds ColliderSurface + RayInteractable to floor anchors.
    /// Provides hover events for consumers like BoxAnnotationManager.
    /// </summary>
    public class EnvironmentSurfaceManager : MonoBehaviour
    {
        [Header("Surface Configuration")]
        [SerializeField, Tooltip("Add ISDK interactability to floor surfaces")]
        private bool _setupFloor = true;

        [SerializeField, Tooltip("Add ISDK interactability to wall surfaces (future)")]
        private bool _setupWalls = false;

        // Track setup anchors and their interactables
        private readonly Dictionary<MRUKAnchor, RayInteractable> _floorInteractables = new();
        private readonly Dictionary<MRUKAnchor, RayInteractable> _wallInteractables = new();
        private bool _subscribedToInteractor = false;

        // Track GameObjects we created for fallback colliders
        private readonly List<GameObject> _createdColliderObjects = new();

        // Track whether we've subscribed to MRUK events
        private bool _subscribedToMruk = false;

        /// <summary>
        /// Fired when ray begins hovering over a floor surface.
        /// </summary>
        public event Action<MRUKAnchor> OnFloorHoverBegin;

        /// <summary>
        /// Fired when ray stops hovering over a floor surface.
        /// </summary>
        public event Action<MRUKAnchor> OnFloorHoverEnd;

        /// <summary>
        /// Fired when floor surface is selected (trigger pressed).
        /// Only fires if no ActiveFloorHandler is set.
        /// </summary>
        public event Action<MRUKAnchor, Vector3> OnFloorSelected;

        /// <summary>
        /// Active floor click handler. When set, floor clicks go to this handler instead of OnFloorSelected event.
        /// Used by AffordanceAnnotationMotif to claim floor clicks while creating.
        /// </summary>
        private Action<MRUKAnchor, Vector3> _activeFloorHandler;

        /// <summary>
        /// Set the active floor handler. When set, floor clicks go to this handler exclusively.
        /// Pass null to clear and resume normal OnFloorSelected event behavior.
        /// </summary>
        public void SetActiveFloorHandler(Action<MRUKAnchor, Vector3> handler)
        {
            _activeFloorHandler = handler;
        }

        /// <summary>
        /// Whether there's an active floor handler claiming floor clicks.
        /// </summary>
        public bool HasActiveFloorHandler => _activeFloorHandler != null;

        /// <summary>
        /// Returns true if any floor interactable is currently hovered.
        /// </summary>
        public bool IsFloorHovered { get; private set; }

        /// <summary>
        /// The currently hovered floor anchor, or null if none.
        /// </summary>
        public MRUKAnchor HoveredFloorAnchor { get; private set; }

        private void Start()
        {
            // Try to subscribe to MRUK events
            TrySubscribeToMruk();

            // Subscribe to RayInteractor for hover events
            SubscribeToRayInteractor();

            // Disable floor interaction when entering Interaction mode (as v1)
            InteractionModeManager.ModeChanged += OnInteractionModeChanged;
        }

        private void OnInteractionModeChanged(InteractionMode mode)
        {
            SetFloorInteractionEnabled(mode == InteractionMode.SceneEdit);
        }

        /// <summary>
        /// Enable or disable all floor RayInteractables (as v1).
        /// Called automatically on mode change; can also be called directly.
        /// </summary>
        public void SetFloorInteractionEnabled(bool enabled)
        {
            foreach (var interactable in _floorInteractables.Values)
                if (interactable != null)
                    interactable.enabled = enabled;
        }

        private void Update()
        {
            // Retry MRUK subscription if not yet subscribed
            // MRUK often initializes asynchronously, so we need to keep trying
            if (!_subscribedToMruk)
            {
                TrySubscribeToMruk();
            }

            // Retry RayInteractor subscription if not yet subscribed
            // InteractionModeManager might initialize late
            if (!_subscribedToInteractor)
            {
                SubscribeToRayInteractor();
            }
        }

        /// <summary>
        /// Attempt to subscribe to MRUK events. Returns true if successful.
        /// Called from Start() and Update() until successful.
        /// </summary>
        private void TrySubscribeToMruk()
        {
            if (_subscribedToMruk) return;
            if (MRUK.Instance == null) return;

            MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneLoaded);
            MRUK.Instance.RoomCreatedEvent.AddListener(OnRoomCreated);
            MRUK.Instance.RoomRemovedEvent.AddListener(OnRoomRemoved);

            // If scene is already loaded, set up existing rooms
            if (MRUK.Instance.IsInitialized)
            {
                OnSceneLoaded();
            }

            _subscribedToMruk = true;
        }

        private void OnDestroy()
        {
            InteractionModeManager.ModeChanged -= OnInteractionModeChanged;

            // Unsubscribe from MRUK events
            if (MRUK.Instance != null)
            {
                MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
                MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomCreated);
                MRUK.Instance.RoomRemovedEvent.RemoveListener(OnRoomRemoved);
            }

            // Unsubscribe from RayInteractor
            UnsubscribeFromRayInteractor();

            // Clean up added components
            CleanupAllAnchors();
        }

        private void OnSceneLoaded()
        {
            // Set up all existing rooms
            foreach (var room in MRUK.Instance.Rooms)
            {
                SetupRoom(room);
            }
        }

        private void OnRoomCreated(MRUKRoom room)
        {
            SetupRoom(room);
        }

        private void OnRoomRemoved(MRUKRoom room)
        {
            CleanupRoom(room);
        }

        private void SetupRoom(MRUKRoom room)
        {
            if (room == null) return;

            // Setup floor anchor
            if (_setupFloor && room.FloorAnchor != null)
            {
                SetupAnchorInteraction(room.FloorAnchor, _floorInteractables);
            }

            // Setup wall anchors (future)
            if (_setupWalls)
            {
                foreach (var wallAnchor in room.WallAnchors)
                {
                    SetupAnchorInteraction(wallAnchor, _wallInteractables);
                }
            }
        }

        private void CleanupRoom(MRUKRoom room)
        {
            if (room == null) return;

            // Cleanup floor anchor
            if (room.FloorAnchor != null && _floorInteractables.ContainsKey(room.FloorAnchor))
            {
                CleanupAnchor(room.FloorAnchor, _floorInteractables);
            }

            // Cleanup wall anchors
            foreach (var wallAnchor in room.WallAnchors)
            {
                if (_wallInteractables.ContainsKey(wallAnchor))
                {
                    CleanupAnchor(wallAnchor, _wallInteractables);
                }
            }
        }

        private void SetupAnchorInteraction(MRUKAnchor anchor, Dictionary<MRUKAnchor, RayInteractable> registry)
        {
            if (anchor == null) return;
            if (registry.ContainsKey(anchor)) return; // Already setup

            // Find collider on anchor or its children (MRUK creates colliders on child GameObjects via EffectMesh)
            var collider = anchor.GetComponentInChildren<Collider>();

            // If no collider exists, create a fallback collider based on anchor's PlaneRect
            if (collider == null)
            {
                collider = CreateFallbackColliderForAnchor(anchor);
                if (collider == null)
                {
                    return;
                }
            }

            // ISDK components must be on the same GameObject as the Collider
            var colliderGO = collider.gameObject;

            // Add ColliderSurface (same pattern as SbEntity)
            var surface = colliderGO.GetComponent<ColliderSurface>();
            if (surface == null)
            {
                surface = colliderGO.AddComponent<ColliderSurface>();
            }
            surface.InjectCollider(collider);

            // Add RayInteractable
            var interactable = colliderGO.GetComponent<RayInteractable>();
            if (interactable == null)
            {
                interactable = colliderGO.AddComponent<RayInteractable>();
            }
            interactable.InjectSurface(surface);

            // Register (keyed by anchor, but interactable is on collider's GO)
            registry[anchor] = interactable;

            // A floor that appears after the mode was set follows the current mode (v1 only toggled on a change).
            if (ReferenceEquals(registry, _floorInteractables))
                interactable.enabled = InteractionModeManager.IsMode(InteractionMode.SceneEdit);
        }

        /// <summary>
        /// Create a fallback BoxCollider for an anchor that doesn't have one.
        /// Uses the anchor's PlaneRect to determine size.
        /// </summary>
        private Collider CreateFallbackColliderForAnchor(MRUKAnchor anchor)
        {
            if (!anchor.PlaneRect.HasValue)
            {
                return null;
            }

            var rect = anchor.PlaneRect.Value;

            // Create a child GameObject with BoxCollider
            var go = new GameObject($"{anchor.name}_FallbackCollider");
            go.transform.SetParent(anchor.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var boxCollider = go.AddComponent<BoxCollider>();
            // PlaneRect is centered at anchor origin, size is width x height, thin in Z
            boxCollider.center = Vector3.zero;
            boxCollider.size = new Vector3(rect.width, rect.height, 0.01f);
            boxCollider.isTrigger = true;

            _createdColliderObjects.Add(go);

            return boxCollider;
        }

        private void CleanupAnchor(MRUKAnchor anchor, Dictionary<MRUKAnchor, RayInteractable> registry)
        {
            if (anchor == null || !registry.ContainsKey(anchor)) return;

            // Remove components we added (they're on the collider's GO, not the anchor)
            var interactable = registry[anchor];
            if (interactable != null)
            {
                // Get the surface from the same GameObject before destroying interactable
                var surface = interactable.gameObject.GetComponent<ColliderSurface>();
                if (surface != null)
                {
                    Destroy(surface);
                }
                Destroy(interactable);
            }

            registry.Remove(anchor);
        }

        private void CleanupAllAnchors()
        {
            // Copy keys to avoid modification during enumeration
            var floorAnchors = new List<MRUKAnchor>(_floorInteractables.Keys);
            foreach (var anchor in floorAnchors)
            {
                if (anchor != null)
                {
                    CleanupAnchor(anchor, _floorInteractables);
                }
            }
            _floorInteractables.Clear();

            var wallAnchors = new List<MRUKAnchor>(_wallInteractables.Keys);
            foreach (var anchor in wallAnchors)
            {
                if (anchor != null)
                {
                    CleanupAnchor(anchor, _wallInteractables);
                }
            }
            _wallInteractables.Clear();

            // Cleanup created fallback collider objects
            foreach (var go in _createdColliderObjects)
            {
                if (go != null)
                {
                    Destroy(go);
                }
            }
            _createdColliderObjects.Clear();
        }

        // === RayInteractor Event Handling ===

        private void SubscribeToRayInteractor()
        {
            if (_subscribedToInteractor) return;

            // The active editing input's ray: the right controller's or the right hand's.
            EditInput.Hovered += OnInteractorHover;
            EditInput.Unhovered += OnInteractorUnhover;
            EditInput.Selected += OnInteractorSelect;
            _subscribedToInteractor = true;
        }

        private void UnsubscribeFromRayInteractor()
        {
            if (!_subscribedToInteractor) return;

            EditInput.Hovered -= OnInteractorHover;
            EditInput.Unhovered -= OnInteractorUnhover;
            EditInput.Selected -= OnInteractorSelect;
            _subscribedToInteractor = false;
        }

        internal void OnInteractorHover(EditPointer pointer, RayInteractable interactable)
        {
            // Check if this is a floor interactable
            foreach (var kvp in _floorInteractables)
            {
                if (kvp.Value == interactable)
                {
                    IsFloorHovered = true;
                    HoveredFloorAnchor = kvp.Key;
                    OnFloorHoverBegin?.Invoke(kvp.Key);
                    return;
                }
            }
        }

        internal void OnInteractorUnhover(EditPointer pointer, RayInteractable interactable)
        {
            // Check if this is a floor interactable
            foreach (var kvp in _floorInteractables)
            {
                if (kvp.Value == interactable)
                {
                    IsFloorHovered = false;
                    HoveredFloorAnchor = null;
                    OnFloorHoverEnd?.Invoke(kvp.Key);
                    return;
                }
            }
        }

        internal void OnInteractorSelect(EditPointer pointer, RayInteractable interactable)
        {
            // Check if this is a floor interactable
            foreach (var kvp in _floorInteractables)
            {
                if (kvp.Value == interactable)
                {
                    // Get hit point from interactor
                    var hitPoint = pointer != null ? pointer.HitPoint ?? Vector3.zero : Vector3.zero;

                    // If there's an active handler, send to it exclusively
                    if (_activeFloorHandler != null)
                    {
                        _activeFloorHandler.Invoke(kvp.Key, hitPoint);
                    }
                    else
                    {
                        // Otherwise, fire the normal event
                        OnFloorSelected?.Invoke(kvp.Key, hitPoint);
                    }
                    return;
                }
            }
        }
    }
}
