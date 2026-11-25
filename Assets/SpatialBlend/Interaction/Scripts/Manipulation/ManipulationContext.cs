// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.Scanning;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// The type of manipulation target.
    /// </summary>
    public enum ManipulationTargetType
    {
        None,
        SbEntity,
        BoxAnnotationInstance
    }

    /// <summary>
    /// Helper component attached to the target object to track the lifecycle of its ManipulationContext.
    /// When the target is destroyed, this automatically disposes the associated context.
    /// </summary>
    internal sealed class ContextOwnerTracker : MonoBehaviour
    {
        public ManipulationContext Context;

        private void OnDestroy()
        {
            if (Context != null)
            {
                Context.Dispose();
            }
        }
    }

    /// <summary>
    /// Context container for manipulation workflow.
    /// Holds reference to target object and manages all child motifs (panel, annotations, gizmo, etc.).
    /// Created when manipulation begins on an object, destroyed when manipulation ends.
    /// </summary>
    public class ManipulationContext : MonoBehaviour
    {
        [Header("Box Panel Placement")]
        [SerializeField, Tooltip("Fixed panel Y. If (targetBounds.max.y + gap) exceeds this, Y is clamped to this value.")]
        private float _boxPanelFixedY = 1.0f;

        // Target reference
        private GameObject _targetObject;
        private ManipulationTargetType _targetType = ManipulationTargetType.None;
        private SbEntity _targetSbEntity;
        private BoxAnnotationInstance _targetBoxAnnotation;
        private Transform _centerEyeAnchor;

        // Child components
        private ManipulationPanelMotif _panelMotif;
        private List<MonoBehaviour> _activeMotifs = new List<MonoBehaviour>();

        // Panel management
        private GameObject _panelInstance;
        private bool _isPanelShowing;

        // Mode execution
        private int _selectedModeIndex = -1;
        private Action<bool> _onModeComplete;
        private GameObject _passthroughPanelInstance;
        private ScanCard _scanCard;

        #region Properties

        /// <summary>
        /// The target GameObject this context is for.
        /// </summary>
        public GameObject TargetObject => _targetObject;

        /// <summary>
        /// The type of target (SbEntity or BoxAnnotationInstance).
        /// </summary>
        public ManipulationTargetType TargetType => _targetType;

        /// <summary>
        /// The target SbEntity (null if target is not SbEntity).
        /// </summary>
        public SbEntity TargetSbEntity => _targetSbEntity;

        /// <summary>
        /// The target BoxAnnotationInstance (null if target is not BoxAnnotationInstance).
        /// </summary>
        public BoxAnnotationInstance TargetBoxAnnotation => _targetBoxAnnotation;

        /// <summary>
        /// The panel motif managing the UI toggle.
        /// </summary>
        public ManipulationPanelMotif PanelMotif => _panelMotif;

        /// <summary>
        /// Whether this context has an active manipulation (any child motif running).
        /// </summary>
        public bool HasActiveMotif => _activeMotifs.Count > 0;

        /// <summary>
        /// Whether the panel is currently showing.
        /// </summary>
        public bool IsPanelShowing => _isPanelShowing;

        /// <summary>Whether the scan (passthrough camera) panel spawned for this target is open.</summary>
        public bool IsPassthroughPanelOpen => _passthroughPanelInstance != null || (_scanCard != null && _scanCard.IsCapturing);

        /// <summary>
        /// The panel instance GameObject.
        /// </summary>
        public GameObject PanelInstance => _panelInstance;

        /// <summary>
        /// Currently selected mode index (-1 = none).
        /// </summary>
        public int SelectedModeIndex => _selectedModeIndex;

        #endregion

        #region Events

        /// <summary>
        /// Called when a child motif is added.
        /// </summary>
        public event Action<ManipulationContext, MonoBehaviour> OnMotifAdded;

        /// <summary>
        /// Called when a child motif is removed.
        /// </summary>
        public event Action<ManipulationContext, MonoBehaviour> OnMotifRemoved;

        /// <summary>
        /// Called when this context is about to be destroyed.
        /// </summary>
        public event Action<ManipulationContext> OnContextDestroyed;

        /// <summary>
        /// Called when panel is shown.
        /// </summary>
        public event Action<ManipulationContext> OnPanelShown;

        /// <summary>
        /// Called when panel is hidden.
        /// </summary>
        public event Action<ManipulationContext> OnPanelHidden;

        /// <summary>
        /// Called when a mode is selected.
        /// </summary>
        public event Action<ManipulationContext, int> OnModeSelected;

        /// <summary>
        /// Called when mode execution completes.
        /// </summary>
        public event Action<ManipulationContext, int, bool> OnModeExecuted;

        #endregion

        #region Static Factory

        /// <summary>
        /// Create a ManipulationContext for the given target.
        /// Creates a new GameObject to host the context.
        /// The context is automatically disposed when the target is destroyed.
        /// </summary>
        public static ManipulationContext Create(GameObject target)
        {
            if (target == null)
            {
                Debug.LogError("[ManipulationContext] Cannot create context for null target");
                return null;
            }

            // Check if context already exists for this target
            var existing = FindContextForTarget(target);
            if (existing != null)
            {
                return existing;
            }

            // Create context GameObject as sibling of target (not child, to avoid transform inheritance)
            var contextGO = new GameObject($"ManipulationContext_{target.name}");
            var context = contextGO.AddComponent<ManipulationContext>();
            context.Initialize(target);

            // Attach lifecycle tracker to target so context is disposed when target is destroyed
            var tracker = target.GetComponent<ContextOwnerTracker>();
            if (tracker == null)
            {
                tracker = target.AddComponent<ContextOwnerTracker>();
            }
            tracker.Context = context;

            return context;
        }

        /// <summary>
        /// Find existing context for a target, if any.
        /// </summary>
        public static ManipulationContext FindContextForTarget(GameObject target)
        {
            if (target == null) return null;

            var allContexts = FindObjectsByType<ManipulationContext>(FindObjectsSortMode.None);
            foreach (var ctx in allContexts)
            {
                if (ctx._targetObject == target)
                    return ctx;
            }
            return null;
        }

        #endregion

        #region Initialization

        private void Initialize(GameObject target)
        {
            _targetObject = target;
            _targetType = ManipulationTargetType.None;
            _targetSbEntity = null;
            _targetBoxAnnotation = null;

            if (target == null) return;

            // Determine target type
            _targetSbEntity = target.GetComponent<SbEntity>();
            if (_targetSbEntity != null)
            {
                _targetType = ManipulationTargetType.SbEntity;
                return;
            }

            _targetBoxAnnotation = target.GetComponent<BoxAnnotationInstance>();
            if (_targetBoxAnnotation != null)
            {
                _targetType = ManipulationTargetType.BoxAnnotationInstance;
                return;
            }
        }

        #endregion

        #region Panel Motif

        /// <summary>
        /// Set the panel motif for this context.
        /// Called by ManipulationPanelMotif when it associates with this context.
        /// </summary>
        public void SetPanelMotif(ManipulationPanelMotif motif)
        {
            _panelMotif = motif;
        }

        #endregion

        #region Panel Management

        /// <summary>
        /// Show the manipulation panel.
        /// </summary>
        /// <param name="panelPrefab">Panel prefab to instantiate (or use default if null).</param>
        public void ShowPanel(GameObject panelPrefab = null)
        {
            // Use provided prefab or fall back to InteractionModeManager default
            var prefab = panelPrefab ?? InteractionModeManager.DefaultManipulationPanelPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"[ManipulationContext:{_targetObject?.name}] No panel prefab available");
                return;
            }

            if (_panelInstance == null)
            {
                _panelInstance = UnityEngine.Object.Instantiate(prefab);
                SetupPanel();
            }

            // Reset mode selection when showing panel
            _selectedModeIndex = -1;

            // Reset UI selection state in ManipulationPopUp
            var manipPopup = _panelInstance.GetComponentInChildren<ManipulationPopUp>(true);
            if (manipPopup != null)
            {
                manipPopup.ResetSelection();
            }

            _panelInstance.SetActive(true);
            _isPanelShowing = true;

            OnPanelShown?.Invoke(this);
        }

        /// <summary>
        /// A Delete asked for from outside the menu (a button on the box itself, an undo gesture): opens the object menu
        /// on its "Delete this object?" question. Nothing is deleted until that question is answered yes. False when no
        /// menu can be shown, in which case nothing is deleted either.
        /// </summary>
        public bool RequestDelete(GameObject panelPrefab = null)
        {
            ShowPanel(panelPrefab);
            if (_panelInstance == null) return false;
            var popup = _panelInstance.GetComponentInChildren<ManipulationPopUp>(true);
            if (popup == null) return false;
            popup.BeginDeleteConfirm();
            return popup.IsConfirmingDelete;
        }

        /// <summary>
        /// Hide the manipulation panel.
        /// </summary>
        public void HidePanel()
        {
            if (_panelInstance != null)
            {
                _panelInstance.SetActive(false);
            }
            _isPanelShowing = false;

            OnPanelHidden?.Invoke(this);
        }

        /// <summary>
        /// Toggle panel visibility.
        /// </summary>
        public void TogglePanel(GameObject panelPrefab = null)
        {
            if (_isPanelShowing)
                HidePanel();
            else
                ShowPanel(panelPrefab);
        }

        private void SetupPanel()
        {
            if (_panelInstance == null) return;

            // Setup billboard to face camera and position above target
            var billboard = _panelInstance.GetComponent<BillboardToCamera>();
            if (billboard == null)
            {
                billboard = _panelInstance.AddComponent<BillboardToCamera>();
            }
            billboard.SetTarget(_targetObject.transform);
            if (_targetType == ManipulationTargetType.BoxAnnotationInstance)
            {
                billboard.SetVerticalOffsetClamp(_boxPanelFixedY);
            }

            // Setup ManipulationPopUp with this context
            var manipPopup = _panelInstance.GetComponentInChildren<ManipulationPopUp>(true);
            if (manipPopup != null)
            {
                manipPopup.SetContext(this);
                WireUpPanelEvents(manipPopup);
            }
        }

        private void WireUpPanelEvents(ManipulationPopUp popup)
        {
            // Clear existing listeners
            popup.OnAffordanceVolumeSelected.RemoveAllListeners();
            popup.OnReconstructSelected.RemoveAllListeners();
            popup.OnGizmoSelected.RemoveAllListeners();
            popup.OnDeleteSelected.RemoveAllListeners();
            popup.OnConfirm.RemoveAllListeners();
            popup.OnExit.RemoveAllListeners();

            // Mode selection (just tracks selected mode). A drawn box has no Confirm: Capture opens the card itself.
            popup.OnAffordanceVolumeSelected.AddListener(() => SetSelectedMode(0));
            popup.OnReconstructSelected.AddListener(() =>
            {
                SetSelectedMode(1);
                if (TargetBoxAnnotation != null) ExecuteSelectedMode();
            });
            popup.OnGizmoSelected.AddListener(() => SetSelectedMode(2));
            popup.OnDeleteSelected.AddListener(() => SetSelectedMode(3));
            // Actions
            popup.OnConfirm.AddListener(OnConfirmPressed);
            popup.OnExit.AddListener(OnExitPressed);
        }

        #endregion

        #region Mode Execution

        /// <summary>
        /// Set the currently selected mode.
        /// </summary>
        public void SetSelectedMode(int modeIndex)
        {
            _selectedModeIndex = modeIndex;
            OnModeSelected?.Invoke(this, modeIndex);
        }

        /// <summary>
        /// Execute the currently selected mode.
        /// </summary>
        /// <param name="onComplete">Callback when mode execution completes.</param>
        public void ExecuteSelectedMode(Action<bool> onComplete = null)
        {
            HidePanel();
            ExecuteMode(_selectedModeIndex, onComplete);
        }

        /// <summary>
        /// Execute a specific mode.
        /// </summary>
        /// <param name="modeIndex">Mode index (0=AffordanceVolume, 1=Reconstruct, 2=Gizmo).</param>
        /// <param name="onComplete">Callback when mode execution completes.</param>
        public void ExecuteMode(int modeIndex, Action<bool> onComplete = null)
        {
            if (modeIndex < 0)
            {
                onComplete?.Invoke(false);
                return;
            }

            _onModeComplete = onComplete;

            switch (modeIndex)
            {
                case 0: // AffordanceVolume
                    ExecuteAffordanceVolumeMode();
                    break;

                case 1: // Reconstruct
                    ExecuteReconstructMode();
                    break;

                case 2: // Gizmo
                    ExecuteGizmoMode();
                    break;

                case 3: // Delete
                    ExecuteDeleteMode();
                    break;

                default:
                    CompleteMode(false);
                    break;
            }
        }

        private void ExecuteAffordanceVolumeMode()
        {
            // An annotated box is already its own region declaration (BoxAnnotationAuthoring, T5.2).
            // The floor-polygon motif below never runs on it, so a box is never given a volume component.
            if (_targetType == ManipulationTargetType.BoxAnnotationInstance)
            {
                CompleteMode(false);
                return;
            }

            // Create AffordanceAnnotationMotif directly
            var go = new GameObject("AffordanceAnnotationMotif");
            go.transform.SetParent(transform);
            var motif = go.AddComponent<AffordanceAnnotationMotif>();

            // Configure
            motif.SetAnnotationRoot(_targetObject?.transform.parent);
            motif.SetContext(this);
            motif.OnAnnotationComplete += OnAnnotationComplete;

            // Activate the motif to start waiting for floor clicks
            motif.Activate();

            // Track
            AddMotif(motif);
        }

        private void OnAnnotationComplete(AnnotationMotifBase motif, bool success)
        {
            motif.OnAnnotationComplete -= OnAnnotationComplete;
            RemoveMotif(motif);

            if (motif.gameObject != null)
            {
                Destroy(motif.gameObject);
            }

            CompleteMode(success);
        }

        /// <summary>
        /// Reconstruct 3D: the capture card on the box (scan UI Concept A), in place of the floating camera
        /// panel. The card owns its lifetime: it stays (with its ring) while the model is built even after this context is gone.
        /// </summary>
        private void ExecuteReconstructMode()
        {
            if (_targetBoxAnnotation == null || _targetBoxAnnotation.GetComponent<BoxCollider>() == null)
            {
                Debug.LogError("[ManipulationContext] Reconstruct 3D needs a drawn box.");
                CompleteMode(false);
                return;
            }
            _scanCard = ScanCard.Open(_targetBoxAnnotation, ResolveCenterEyeAnchor());
            if (_scanCard == null)
            {
                CompleteMode(false);
                return;
            }
            _scanCard.Closed += OnScanCardClosed;
            CompleteMode(true);
        }

        private void OnScanCardClosed()
        {
            _scanCard = null;
        }

        private Transform ResolveCenterEyeAnchor()
        {
            if (_centerEyeAnchor != null)
            {
                return _centerEyeAnchor;
            }

            var cameraRig = FindAnyObjectByType<OVRCameraRig>();
            if (cameraRig != null)
            {
                _centerEyeAnchor = cameraRig.centerEyeAnchor;
            }

            if (_centerEyeAnchor == null && Camera.main != null)
            {
                _centerEyeAnchor = Camera.main.transform;
            }

            return _centerEyeAnchor;
        }

        private void OnPassthroughPanelClosed()
        {
            _passthroughPanelInstance = null;
        }

        private void ExecuteGizmoMode()
        {
            HidePanel();
            CompleteMode(false);
        }

        // Reached only from the Delete question's yes (ManipulationPopUp.ConfirmDelete): Delete never runs unasked.
        private void ExecuteDeleteMode()
        {
            // Withdraw first, destroy after. Destroy lands at the end of the
            // frame; until then the target must not stay shared. Detach unregisters the entity (a source unshares its
            // element, so every peer's replica goes) and a box annotation withdraws its declaration.
            if (_targetSbEntity != null) _targetSbEntity.Detach();
            if (_targetObject != null) BoxAnnotationAuthoring.Withdraw(_targetObject);
            Destroy(_targetObject);
            CompleteMode(true);
        }

        private void CompleteMode(bool success)
        {
            var modeIndex = _selectedModeIndex;
            _onModeComplete?.Invoke(success);
            _onModeComplete = null;
            OnModeExecuted?.Invoke(this, modeIndex, success);
        }

        /// <summary>
        /// Called when mode execution finishes (e.g., annotation completed).
        /// </summary>
        public void NotifyModeComplete(bool success)
        {
            CompleteMode(success);
        }

        private void OnConfirmPressed()
        {
            ExecuteSelectedMode();
        }

        private void OnExitPressed()
        {
            HidePanel();
        }

        #endregion

        #region Child Motif Management

        /// <summary>
        /// Register a child motif with this context.
        /// </summary>
        public void AddMotif(MonoBehaviour motif)
        {
            if (motif == null) return;
            if (_activeMotifs.Contains(motif)) return;

            _activeMotifs.Add(motif);
            OnMotifAdded?.Invoke(this, motif);
        }

        /// <summary>
        /// Unregister a child motif from this context.
        /// </summary>
        public void RemoveMotif(MonoBehaviour motif)
        {
            if (motif == null) return;
            if (!_activeMotifs.Contains(motif)) return;

            _activeMotifs.Remove(motif);
            OnMotifRemoved?.Invoke(this, motif);
        }

        /// <summary>
        /// Get all active motifs of a specific type.
        /// </summary>
        public List<T> GetMotifs<T>() where T : MonoBehaviour
        {
            var result = new List<T>();
            foreach (var motif in _activeMotifs)
            {
                if (motif is T typed)
                    result.Add(typed);
            }
            return result;
        }

        /// <summary>
        /// Cancel all active motifs.
        /// </summary>
        public void CancelAllMotifs()
        {
            // Copy list to avoid modification during iteration
            var motifsCopy = new List<MonoBehaviour>(_activeMotifs);
            foreach (var motif in motifsCopy)
            {
                if (motif != null)
                {
                    // Try to call CancelAnnotation if it's an annotation motif
                    if (motif is AnnotationMotifBase annotationMotif)
                    {
                        annotationMotif.CancelAnnotation();
                    }
                    else
                    {
                        // For other motifs, just destroy
                        Destroy(motif.gameObject);
                    }
                }
            }
            _activeMotifs.Clear();
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Destroy this context and cleanup all child motifs.
        /// </summary>
        public void Dispose()
        {
            OnContextDestroyed?.Invoke(this);

            if (_passthroughPanelInstance != null)
            {
                Destroy(_passthroughPanelInstance);
                _passthroughPanelInstance = null;
            }

            CancelAllMotifs();

            // Destroy panel instance
            if (_panelInstance != null)
            {
                Destroy(_panelInstance);
                _panelInstance = null;
            }

            if (_panelMotif != null)
            {
                // Don't destroy panel motif - it's a component on the target
                _panelMotif = null;
            }

            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_passthroughPanelInstance != null)
            {
                Destroy(_passthroughPanelInstance);
                _passthroughPanelInstance = null;
            }

            // Cleanup any remaining references
            _activeMotifs.Clear();
            _panelMotif = null;
            _targetObject = null;
            _targetSbEntity = null;
            _targetBoxAnnotation = null;

            if (_panelInstance != null)
            {
                Destroy(_panelInstance);
                _panelInstance = null;
            }
        }

        #endregion
    }
}
