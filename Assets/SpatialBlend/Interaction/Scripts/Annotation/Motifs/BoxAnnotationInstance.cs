// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using SpatialBlend.Interactions;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// Standalone annotation representation with its own render/collider state.
    /// Subscribes to scene RayInteractor for hover/select events.
    /// Optionally attaches ManipulationPanelMotif for panel-based interactions.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [DisallowMultipleComponent]
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public class BoxAnnotationInstance : MonoBehaviour
    {
        [Header("Visuals")]
        [Tooltip("Root transform for the box visuals (mesh + wire). If null, this GameObject is used.")]
        [SerializeField] private Transform _visualRoot;

        [Header("UI")]
        [Tooltip("Root transform for the popup UI that should float above the box.")]
        [SerializeField] private Transform _uiRoot;
        [Tooltip("Additional gap (meters) between the box top and the UI after automatic sizing.")]
        [SerializeField] private float _uiExtraVerticalMargin = 0.02f;

        [Header("Manipulation")]
        [Tooltip("Whether to add ManipulationPanelMotif for A button panel toggle.")]
        [SerializeField] private bool _addManipulationPanelMotif = true;
        [Tooltip("Whether to add BoxAnnotationPlacementMotif for grab-and-move functionality.")]
        [SerializeField] private bool _addBoxAnnotationPlacementMotif = true;

        private BoxCollider _collider;
        private ColliderSurface _colliderSurface;
        private RayInteractable _rayInteractable;
        private MeshRenderer[] _visualRenderers;
        private ManipulationPanelMotif _manipulationMotif;
        private BoxAnnotationPlacementMotif _BoxAnnotationPlacementMotif;
        private bool _subscribedToInteractor = false;

        // Visibility state: controlled by UI (Visible toggle) and hover events.
        // Collider always stays enabled; mesh visuals shown when _isVisible.
        private bool _isVisible = true;

        // The box is drawn by an AnnotationVisual (graphite lines, corner brackets, faint faces), not by the prefab's mesh.
        private AnnotationVisual _visual;
        private AnnotationEmphasis _emphasis = AnnotationEmphasis.Normal;
        private int _focusCorner = -1;
        private Vector3 _size;

        /// <summary>The graphics of this box (null until it has a size).</summary>
        public AnnotationVisual Visual => _visual;

        /// <summary>
        /// Hovered or selected look of the box; <paramref name="focusCorner"/> (0..7, -1 for none) brightens the corner bracket
        /// under the ray.
        /// </summary>
        public void SetEmphasis(AnnotationEmphasis emphasis, int focusCorner = -1)
        {
            if (_emphasis == emphasis && _focusCorner == focusCorner) return;
            _emphasis = emphasis;
            _focusCorner = focusCorner;
            RefreshVisual();
        }

        private void RefreshVisual()
        {
            if (_size.sqrMagnitude < 1e-8f) return;
            if (_visual == null)
            {
                _visual = AnnotationVisual.Create(transform, "AnnotationVisual_Box");
                _visual.transform.localPosition = Vector3.zero;
                _visual.transform.localRotation = Quaternion.identity;
            }
            _visual.Clear();
            // The root sits at the centre of the box's base, so the box is at the local origin.
            _visual.SetBox(Vector3.zero, Quaternion.identity, _size, _emphasis, _focusCorner, _focusCorner >= 0 ? CornerFocus.Hover : CornerFocus.None);
            _visual.gameObject.SetActive(_isVisible);
        }

        public void Initialize()
        {
            EnsureComponents();
            ApplyVisibility();
        }

        public void SetData(Pose pose, Vector3 size, Vector3 up)
        {
            EnsureComponents();

            // Position and orient the root transform, but keep scale at (1,1,1)
            // to avoid scaling children (like AffordanceVolume).
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            transform.localScale = Vector3.one;

            // Size the collider directly (BoxCollider.size is in local space).
            // Position is at BOTTOM, so offset collider center up by half-height
            _collider.center = new Vector3(0, size.y / 2f, 0);
            _collider.size = size;

            // The box is spatial definition, not a volume component (T5.2): its footprint extent is the
            // region, its base is the footprint and its top is a tagged surface. See BoxAnnotationAuthoring.
            BoxAnnotationAuthoring.Declare(gameObject, _collider);

            _size = size;
            RefreshVisual();

            // Scale only the visual mesh child, not the root.
            // Position is at BOTTOM, so offset visual up so its bottom aligns with position.
            if (_visualRoot != null && _visualRoot != transform)
            {
                _visualRoot.localScale = size;
                // Unit cube center is at origin; when scaled, bottom is at -size.y/2
                // Offset up by half-height so bottom aligns with transform position (which is at BOTTOM)
                _visualRoot.localPosition = new Vector3(0, size.y / 2f, 0);
            }

            // Position the popup UI so it always hovers just above the box,
            // automatically accounting for the UI's own height.
            if (_uiRoot != null)
            {
                // Compute the UI's world-space bounds based on its renderers.
                var renderers = _uiRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
                if (renderers != null && renderers.Length > 0)
                {
                    var uiBounds = new Bounds(renderers[0].bounds.center, renderers[0].bounds.size);
                    for (int i = 1; i < renderers.Length; i++)
                    {
                        if (renderers[i] != null)
                        {
                            uiBounds.Encapsulate(renderers[i].bounds);
                        }
                    }

                    // Height of the UI in world space.
                    float uiHalfHeight = uiBounds.extents.y;

                    // Full box height from collider size (position is at BOTTOM, so use full height).
                    float boxHeightWorld = _collider.size.y;
                    float totalOffset = boxHeightWorld + uiHalfHeight + _uiExtraVerticalMargin;

                    // Place UI above the box along its local up axis.
                    Vector3 worldPos = transform.position + transform.up * totalOffset;
                    _uiRoot.position = worldPos;
                    _uiRoot.rotation = transform.rotation;
                }
                else
                {
                    // Fallback: if no renderers, just place at a small fixed offset above the box.
                    // Position is at BOTTOM, so use full height.
                    float boxHeightWorld = _collider.size.y;
                    float totalOffset = boxHeightWorld + _uiExtraVerticalMargin;
                    Vector3 worldPos = transform.position + transform.up * totalOffset;
                    _uiRoot.position = worldPos;
                    _uiRoot.rotation = transform.rotation;
                }
            }

            ApplyVisibility();
        }

        private void Awake()
        {
            EnsureComponents();
            ApplyVisibility();
        }

        private void Start()
        {
            // Subscribe to scene RayInteractor for hover/select events
            SubscribeToSceneRayInteractor();

            // Optionally instantiate ManipulationPanelMotif for panel interactions
            SetupManipulationPanelMotif();

            // Optionally add BoxAnnotationPlacementMotif for grab-and-move
            SetupBoxAnnotationPlacementMotif();
        }

        private void SubscribeToSceneRayInteractor()
        {
            if (_subscribedToInteractor) return;

            // RayInteractor events are not currently used for BoxAnnotationInstance
            // Hover/select behavior is handled by ManipulationPanelMotif if attached
            _subscribedToInteractor = true;
        }

        private void UnsubscribeFromSceneRayInteractor()
        {
            _subscribedToInteractor = false;
        }

        private void SetupManipulationPanelMotif()
        {
            if (!_addManipulationPanelMotif) return;

            // Add ManipulationPanelMotif component for A button panel toggle
            _manipulationMotif = ManipulationPanelMotif.Setup(gameObject);
        }

        private void SetupBoxAnnotationPlacementMotif()
        {
            if (!_addBoxAnnotationPlacementMotif) return;

            // Add BoxAnnotationPlacementMotif for grab-and-move functionality
            _BoxAnnotationPlacementMotif = GetComponent<BoxAnnotationPlacementMotif>();
            if (_BoxAnnotationPlacementMotif == null)
            {
                _BoxAnnotationPlacementMotif = gameObject.AddComponent<BoxAnnotationPlacementMotif>();
            }
        }

        private void OnDestroy()
        {
            UnsubscribeFromSceneRayInteractor();
            // A deleted box scopes nothing.
            BoxAnnotationAuthoring.Withdraw(gameObject);
            // ManipulationPanelMotif is a component on same GameObject, destroyed automatically
        }

        private void EnsureComponents()
        {
            if (_collider == null)
            {
                _collider = GetComponent<BoxCollider>();
                _collider.isTrigger = true;
            }

            // Cache all visual mesh renderers for visibility control.
            // Try to find a visual child (named "Visual" or "Box") if _visualRoot is not set from prefab.
            if (_visualRoot == null)
            {
                // Look for common visual child names
                var visualChild = transform.Find("Visual") ?? transform.Find("Box");
                _visualRoot = visualChild != null ? visualChild : transform;
            }

            if (_visualRenderers == null || _visualRenderers.Length == 0)
            {
                _visualRenderers = _visualRoot.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            }

            // Ensure Interaction SDK components for ray interaction over this instance's mesh/collider
            if (_colliderSurface == null)
            {
                _colliderSurface = GetComponent<ColliderSurface>();
                if (_colliderSurface == null)
                {
                    _colliderSurface = gameObject.AddComponent<ColliderSurface>();
                }
                _colliderSurface.InjectCollider(_collider);
            }

            if (_rayInteractable == null)
            {
                _rayInteractable = GetComponent<RayInteractable>();
                if (_rayInteractable == null)
                {
                    try
                    {
                        _rayInteractable = gameObject.AddComponent<RayInteractable>();
                        // Use this instance's collider surface (wrapping the box/mesh) as the ray surface
                        _rayInteractable.InjectSurface(_colliderSurface);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[AABB] Could not add RayInteractable to instance (ray interaction may not work): {e.Message}");
                        // RayInteractable remains null, instance will still work but won't be ray-interactive
                    }
                }
                else
                {
                    // Component exists, ensure it's configured properly
                    try
                    {
                        _rayInteractable.InjectSurface(_colliderSurface);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[AABB] Could not configure RayInteractable on instance: {e.Message}");
                    }
                }
            }

            HidePrefabMesh();
        }

        /// <summary>
        /// The prefab's cube mesh (and a runtime fallback cube) is not the look of the box any more: the AnnotationVisual draws it.
        /// The collider stays; only the renderers are switched off.
        /// </summary>
        private void HidePrefabMesh()
        {
            if (_visualRenderers == null) return;
            for (int i = 0; i < _visualRenderers.Length; i++)
                if (_visualRenderers[i] != null) _visualRenderers[i].enabled = false;
        }

        /// <summary>Applies the Visible toggle to the box graphics. The collider always stays enabled.</summary>
        private void ApplyVisibility()
        {
            HidePrefabMesh();
            if (_visual != null) _visual.gameObject.SetActive(_isVisible);

            // Ensure collider always remains active (regardless of visual state).
            if (_collider != null)
            {
                _collider.enabled = true;
            }
        }

        /// <summary>
        /// Called by the prefab UI Confirm button.
        /// Currently a no-op placeholder for higher-level workflows.
        /// </summary>
        public void OnConfirmPressed()
        {
            // Intentionally left blank; higher-level systems can hook into this later.
            // Debug.Log("[AABB] Confirm pressed on BoxAnnotationInstance");
        }

        /// <summary>
        /// Called by the prefab UI Delete button.
        /// Asks "Delete this object?" in the object menu. The box is deleted only when that is answered yes
        /// (every Delete asks).
        /// </summary>
        public void OnDeletePressed()
        {
            var context = ManipulationContext.FindContextForTarget(gameObject) ?? ManipulationContext.Create(gameObject);
            if (context == null || !context.RequestDelete())
            {
                Debug.LogWarning("[BoxAnnotationInstance] No object menu to ask in; the box is kept.");
            }
        }

        /// <summary>
        /// For button-like controls that expose OnValueChanged(bool) instead of OnClick.
        /// Treats value=true as a press edge and reuses OnConfirmPressed().
        /// </summary>
        public void OnConfirmValueChanged(bool isOn)
        {
            if (!isOn) return;
            OnConfirmPressed();
        }

        /// <summary>
        /// For button-like controls that expose OnValueChanged(bool) instead of OnClick.
        /// Treats value=true as a press edge and reuses OnDeletePressed().
        /// </summary>
        public void OnDeleteValueChanged(bool isOn)
        {
            if (!isOn) return;
            // The button is a press, not a state: the question is answered in the object menu, so it reads off again.
            foreach (var toggle in GetComponentsInChildren<UnityEngine.UI.Toggle>(true))
                if (toggle.name == "DeleteButton") toggle.SetIsOnWithoutNotify(false);
            OnDeletePressed();
        }

        /// <summary>
        /// Called by the prefab Visible toggle.
        /// Controls whether the box visuals are persistently visible.
        /// Collider remains active even when visuals are hidden.
        /// </summary>
        /// <param name="value">True to keep visuals visible, false to hide unless hovered.</param>
        public void OnVisibleToggleChanged(bool value)
        {
            _isVisible = value;
            ApplyVisibility();
        }

    }
}
