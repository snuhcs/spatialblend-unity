// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Scanning;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SpatialBlend.Interactions.UI
{
    /// <summary>
    /// UI controller for manipulation popup panel.
    /// A drawn box offers Capture, Delete and Exit. Other targets keep their own menus
    /// (affordance volume and Confirm on an ordinary object; Delete on a scan; Hide for me and Close on a received copy).
    /// Assign toggle/button references and the script auto-subscribes to events.
    /// </summary>
    public class ManipulationPopUp : MonoBehaviour
    {
        [Header("Mode Toggles")]
        [SerializeField] private Toggle _affordanceVolumeToggle;
        [SerializeField] private Toggle _reconstructToggle;
        [SerializeField] private Toggle _gizmoToggle;
        [SerializeField] private Toggle _deleteToggle;

        [Header("Action Toggles")]
        [SerializeField] private Toggle _confirmToggle;
        [SerializeField] private Toggle _exitToggle;

        [Header("Events - Mode Selection")]
        public UnityEvent OnAffordanceVolumeSelected;
        public UnityEvent OnReconstructSelected;

        /// <summary>Raised when the Reconstruct 3D toggle of any popup is switched on. The lobby tutorial reads it to verify the step.</summary>
        public static event Action ReconstructPicked;
        public UnityEvent OnGizmoSelected;
        public UnityEvent OnDeleteSelected;

        [Header("Events - Actions")]
        public UnityEvent OnConfirm;
        public UnityEvent OnExit;

        // Context reference (set by ManipulationPanelMotif)
        private ManipulationContext _context;

        /// <summary>
        /// The ManipulationContext this popup is for.
        /// </summary>
        public ManipulationContext Context => _context;

        /// <summary>
        /// The target GameObject (convenience accessor via context).
        /// </summary>
        public GameObject TargetObject => _context?.TargetObject;

        /// <summary>
        /// The type of target (convenience accessor via context).
        /// </summary>
        public ManipulationTargetType TargetType => _context?.TargetType ?? ManipulationTargetType.None;

        /// <summary>
        /// The target SbEntity (convenience accessor via context).
        /// </summary>
        public SbEntity TargetSbEntity => _context?.TargetSbEntity;

        /// <summary>
        /// The target BoxAnnotationInstance (convenience accessor via context).
        /// </summary>
        public Annotation.BoxAnnotationInstance TargetBoxAnnotation => _context?.TargetBoxAnnotation;

        /// <summary>
        /// Set the ManipulationContext for this popup.
        /// </summary>
        public void SetContext(ManipulationContext context)
        {
            // A different target never inherits a confirmation asked for the previous one.
            if (_context != context) CancelDeleteConfirm();
            _context = context;
            RefreshMenu();
        }

        /// <summary>
        /// Legacy: Set the target object directly (creates/finds context automatically).
        /// Prefer SetContext() when context is already available.
        /// </summary>
        public void SetTarget(GameObject target)
        {
            if (target == null)
            {
                CancelDeleteConfirm();
                _context = null;
                RefreshMenu();
                return;
            }

            // Find or create context for target
            CancelDeleteConfirm();
            _context = ManipulationContext.FindContextForTarget(target);
            if (_context == null)
            {
                _context = ManipulationContext.Create(target);
            }
            RefreshMenu();
        }

        private void OnEnable()
        {
            HideUnimplementedGizmoToggle();
            // Remove first: a second enable (or a test that enables by hand) must not double a handler.
            if (_affordanceVolumeToggle != null)
            {
                _affordanceVolumeToggle.onValueChanged.RemoveListener(OnAffordanceVolumeToggleChanged);
                _affordanceVolumeToggle.onValueChanged.AddListener(OnAffordanceVolumeToggleChanged);
            }
            if (_reconstructToggle != null)
            {
                _reconstructToggle.onValueChanged.RemoveListener(OnReconstructToggleChanged);
                _reconstructToggle.onValueChanged.AddListener(OnReconstructToggleChanged);
            }
            if (_gizmoToggle != null)
            {
                _gizmoToggle.onValueChanged.RemoveListener(OnGizmoToggleChanged);
                _gizmoToggle.onValueChanged.AddListener(OnGizmoToggleChanged);
            }
            if (_deleteToggle != null)
            {
                _deleteToggle.onValueChanged.RemoveListener(OnDeleteToggleChanged);
                _deleteToggle.onValueChanged.AddListener(OnDeleteToggleChanged);
            }
            if (_confirmToggle != null)
            {
                _confirmToggle.onValueChanged.RemoveListener(OnConfirmToggleChanged);
                _confirmToggle.onValueChanged.AddListener(OnConfirmToggleChanged);
            }
            if (_exitToggle != null)
            {
                _exitToggle.onValueChanged.RemoveListener(OnExitToggleChanged);
                _exitToggle.onValueChanged.AddListener(OnExitToggleChanged);
            }

            // Add hover visuals to action buttons if not already present
            EnsureHoverVisual(_confirmToggle);
            EnsureHoverVisual(_exitToggle);
            RefreshMenu();
        }

        public const string CaptureLabel = "Capture";
        public const string ExitLabel = "Exit";

        /// <summary>Lays the menu out for the current target (an ordinary object, a drawn box, a scan, a received copy).</summary>
        public void RefreshMenu()
        {
            GameObject target = TargetObject;
            // A scan's menu is Delete. Region and Reconstruct are for box annotations: on a scan, Region would overwrite
            // the region its box declared, and Reconstruct would start a re-scan with no box.
            bool isScan = target != null && target.GetComponent<ScannedObjectOrigin>() != null;
            // A copy received from a peer has the smallest menu: who it is from, Hide for me, Close.
            bool isReceived = ReceivedCopy.Is(target);
            // A drawn box is a scan target: Capture, Delete, Exit. No affordance volume and no Confirm.
            bool isBox = !isScan && !isReceived && target != null &&
                         target.GetComponent<Annotation.BoxAnnotationInstance>() != null;
            if (_affordanceVolumeToggle != null) _affordanceVolumeToggle.gameObject.SetActive(!isScan && !isReceived && !isBox);
            if (_reconstructToggle != null) _reconstructToggle.gameObject.SetActive(!isScan && !isReceived);
            ApplyScannedObjectMenu(isScan, isReceived ? target.GetComponent<ReceivedCopy>() : null, isBox);
            // A pending Delete question owns the menu: the layout above is for the target, this narrows it.
            if (_confirmingDelete) ApplyDeleteConfirmLayout();
        }

        /// <summary>
        /// Shows the menu of a scan with no target, so a screenshot or a layout test can see it. The runtime path is
        /// <see cref="RefreshMenu"/>.
        /// </summary>
        public void PreviewScanMenu()
        {
            // The menu of a scan: Delete only.
            if (_affordanceVolumeToggle != null) _affordanceVolumeToggle.gameObject.SetActive(false);
            if (_reconstructToggle != null) _reconstructToggle.gameObject.SetActive(false);
            ApplyScannedObjectMenu(true, null, false);
        }

        /// <summary>
        /// Shows the menu of a received copy with no target, so a screenshot or a layout test can see it. The runtime path is
        /// <see cref="RefreshMenu"/>.
        /// </summary>
        public void PreviewReceivedMenu(string ownerName)
        {
            if (_affordanceVolumeToggle != null) _affordanceVolumeToggle.gameObject.SetActive(false);
            if (_reconstructToggle != null) _reconstructToggle.gameObject.SetActive(false);
            var holder = new GameObject("PreviewReceivedCopy");
            holder.hideFlags = HideFlags.HideAndDontSave;
            var copy = holder.AddComponent<ReceivedCopy>();
            copy.SetOwner(ownerName);
            ApplyScannedObjectMenu(false, copy, false);
            if (Application.isPlaying) Destroy(holder); else DestroyImmediate(holder);
        }

        public const string ScannedObjectTitle = "Scanned object";
        public const string CloseLabel = "Close";
        private string _boxMenuTitle;
        private string _boxMenuExitLabel;
        private string _boxMenuCaptureLabel;

        // The menu of an already-scanned object is not the box-scanning flow's: it has its own title, no Confirm (Delete
        // asks its own question), and Cancel reads Close. A box being scanned offers Capture, Delete and Exit only.
        // An ordinary object keeps the prefab's texts, which are remembered here.
        private void ApplyScannedObjectMenu(bool isScan, ReceivedCopy received, bool isBox)
        {
            bool isReceived = received != null;
            bool trimmed = isScan || isReceived;
            TMP_Text title = FindTitle();
            TMP_Text exit = ShownLabel(_exitToggle);
            TMP_Text capture = ShownLabel(_reconstructToggle);
            if (_boxMenuTitle == null && title != null) _boxMenuTitle = title.text;
            if (_boxMenuExitLabel == null && exit != null) _boxMenuExitLabel = exit.text;
            if (_boxMenuCaptureLabel == null && capture != null) _boxMenuCaptureLabel = capture.text;
            if (title != null)
                title.text = isReceived ? ReceivedCopy.HiddenTitle + "\n" + received.OwnerLine : isScan ? ScannedObjectTitle : _boxMenuTitle;
            if (exit != null) foreach (TMP_Text label in _exitToggle.GetComponentsInChildren<TMP_Text>(true))
                label.text = isBox ? ExitLabel : trimmed ? CloseLabel : _boxMenuExitLabel;
            if (_confirmToggle != null) _confirmToggle.gameObject.SetActive(!trimmed && !isBox);
            if (_reconstructToggle != null && _boxMenuCaptureLabel != null)
                SetToggleLabel(_reconstructToggle, isBox ? CaptureLabel : _boxMenuCaptureLabel);
            ApplyHideForMe(isReceived);
        }

        /// <summary>The Delete button of a received copy is "Hide for me": local, undoable by nobody's data, so it asks nothing.</summary>
        public Toggle HideForMeToggle => _showingHideForMe ? _deleteToggle : null;
        private bool _showingHideForMe;

        private void ApplyHideForMe(bool on)
        {
            _showingHideForMe = on;
            if (_deleteToggle == null || _confirmingDelete) return;   // an open Delete question owns the button's label
            SetToggleLabel(_deleteToggle, on ? ReceivedCopy.HideForMeLabel : DeleteLabel);
            // The bin means Delete; Hide for me has no icon.
            foreach (Transform child in _deleteToggle.GetComponentsInChildren<Transform>(true))
                if (child != _deleteToggle.transform && child.name == "Icon") child.gameObject.SetActive(!on);
        }

        private TMP_Text FindTitle()
        {
            foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.gameObject.name == "Title") return text;
            }
            return null;
        }

        /// <summary>
        /// Gizmo mode is not implemented (v1 logged "not implemented" as well), so its toggle is not offered.
        /// The event and the field stay for the day it is.
        /// </summary>
        private void HideUnimplementedGizmoToggle()
        {
            if (_gizmoToggle != null) _gizmoToggle.gameObject.SetActive(false);
        }

        private void EnsureHoverVisual(Toggle toggle)
        {
            if (toggle == null) return;
            if (toggle.GetComponent<ToggleHoverVisual>() == null)
            {
                toggle.gameObject.AddComponent<ToggleHoverVisual>();
            }
        }

        private void OnDisable()
        {
            // Closing the popup abandons a pending Delete question: nothing is deleted. OnEnable lays the menu out again.
            if (_confirmingDelete) EndDeleteConfirmState();
            if (_affordanceVolumeToggle != null)
                _affordanceVolumeToggle.onValueChanged.RemoveListener(OnAffordanceVolumeToggleChanged);
            if (_reconstructToggle != null)
                _reconstructToggle.onValueChanged.RemoveListener(OnReconstructToggleChanged);
            if (_gizmoToggle != null)
                _gizmoToggle.onValueChanged.RemoveListener(OnGizmoToggleChanged);
            if (_deleteToggle != null)
                _deleteToggle.onValueChanged.RemoveListener(OnDeleteToggleChanged);
            if (_confirmToggle != null)
                _confirmToggle.onValueChanged.RemoveListener(OnConfirmToggleChanged);
            if (_exitToggle != null)
                _exitToggle.onValueChanged.RemoveListener(OnExitToggleChanged);
        }

        private void OnAffordanceVolumeToggleChanged(bool isOn)
        {
            if (!isOn) return;
            CancelDeleteConfirm();

            // Turn off other mode toggles
            SetToggleOff(_reconstructToggle);
            SetToggleOff(_gizmoToggle);
            SetToggleOff(_deleteToggle);

            OnAffordanceVolumeSelected?.Invoke();
        }

        private void OnReconstructToggleChanged(bool isOn)
        {
            if (!isOn) return;
            CancelDeleteConfirm();

            // Turn off other mode toggles
            SetToggleOff(_affordanceVolumeToggle);
            SetToggleOff(_gizmoToggle);
            SetToggleOff(_deleteToggle);

            OnReconstructSelected?.Invoke();
            ReconstructPicked?.Invoke();
        }

        private void OnGizmoToggleChanged(bool isOn)
        {
            if (!isOn) return;
            CancelDeleteConfirm();

            // Turn off other mode toggles
            SetToggleOff(_affordanceVolumeToggle);
            SetToggleOff(_reconstructToggle);
            SetToggleOff(_deleteToggle);

            OnGizmoSelected?.Invoke();
        }

        private void OnDeleteToggleChanged(bool isOn)
        {
            if (!isOn) return;
            // A received copy is not the receiver's to delete: the button hides it from this user only, and asks nothing.
            if (_showingHideForMe && ReceivedCopy.Is(TargetObject))
            {
                HideReceivedCopy();
                return;
            }
            // Every Delete asks first. The first press turns the button into "Delete?" with
            // a Cancel beside it; nothing is selected or deleted until it is pressed again.
            if (!_confirmingDelete)
            {
                BeginDeleteConfirm();
                return;
            }
            ConfirmDelete();
        }

        /// <summary>Hide for me: the target is taken out of this user's view and the menu closes. Nothing is sent.</summary>
        public void HideReceivedCopy()
        {
            GameObject target = TargetObject;
            if (_deleteToggle != null) _deleteToggle.SetIsOnWithoutNotify(false);
            if (!ReceivedCopy.Is(target)) return;
            target.GetComponent<ReceivedCopy>().HideForMe();
            _context?.HidePanel();
        }

        // === Delete confirmation ===

        public const string DeleteLabel = "Delete";
        public const string ConfirmDeleteLabel = "Delete?";
        public const string CancelDeleteLabel = "Cancel";
        public const string DeletePrompt = "Delete this object?";

        private bool _confirmingDelete;
        private GameObject _confirmTarget;
        private Toggle _cancelDeleteToggle;
        private TMP_Text _deletePrompt;
        private string _deleteLabelBeforeConfirm;

        /// <summary>True while the menu is asking "Delete this object?" and waiting for an answer.</summary>
        public bool IsConfirmingDelete => _confirmingDelete;

        /// <summary>The question being asked, or null when none is.</summary>
        public string DeletePromptText => _confirmingDelete ? (_deletePrompt != null ? _deletePrompt.text : DeletePrompt) : null;

        /// <summary>The Cancel button of the Delete question, once it exists.</summary>
        public Toggle CancelDeleteToggle => _cancelDeleteToggle;

        /// <summary>The Delete button itself (it reads "Delete?" while the question is open).</summary>
        public Toggle DeleteToggle => _deleteToggle;


        /// <summary>
        /// Opens the Delete question for the current target. Nothing is deleted and no mode is selected. Ignored with
        /// no target or no Delete button.
        /// </summary>
        public void BeginDeleteConfirm()
        {
            GameObject target = TargetObject;
            if (_deleteToggle == null) return;
            if (target == null)
            {
                _deleteToggle.SetIsOnWithoutNotify(false); // nothing to ask about; the next press must fire again
                return;
            }
            if (_confirmingDelete) CancelDeleteConfirm();
            SetToggleOff(_affordanceVolumeToggle);
            SetToggleOff(_reconstructToggle);
            SetToggleOff(_gizmoToggle);
            EnsureCancelDeleteToggle();
            EnsureDeletePrompt();
            _confirmingDelete = true;
            _confirmTarget = target;
            _deleteLabelBeforeConfirm = DeleteLabel;
            // The next press of the same button is the answer, so it must read as off now.
            _deleteToggle.SetIsOnWithoutNotify(false);
            SetToggleLabel(_deleteToggle, ConfirmDeleteLabel);
            if (_deletePrompt != null) _deletePrompt.text = DeletePrompt;
            ApplyDeleteConfirmLayout();
        }

        /// <summary>
        /// Shows the Delete question with no target, so a screenshot or a layout test can see it. <paramref name="scan"/>
        /// picks a scanned object's menu behind it. The runtime path is <see cref="BeginDeleteConfirm"/>.
        /// </summary>
        public void PreviewDeleteConfirm(bool scan)
        {
            CancelDeleteConfirm();
            if (_deleteToggle == null) return;
            if (scan) PreviewScanMenu();
            EnsureCancelDeleteToggle();
            EnsureDeletePrompt();
            _confirmingDelete = true;
            _confirmTarget = null;
            _deleteLabelBeforeConfirm = DeleteLabel;
            _deleteToggle.SetIsOnWithoutNotify(false);
            SetToggleLabel(_deleteToggle, ConfirmDeleteLabel);
            if (_deletePrompt != null) _deletePrompt.text = DeletePrompt;
            ApplyDeleteConfirmLayout();
        }

        /// <summary>The answer yes: the selected mode becomes Delete and runs. Aborts if the target changed meanwhile.</summary>
        private void ConfirmDelete()
        {
            ManipulationContext context = _context;
            GameObject asked = _confirmTarget;
            bool sameTarget = context != null && asked != null && context.TargetObject == asked;
            EndDeleteConfirmState();
            if (!sameTarget)
            {
                RefreshMenu();
                return;
            }
            SetToggleOff(_affordanceVolumeToggle);
            SetToggleOff(_reconstructToggle);
            SetToggleOff(_gizmoToggle);
            OnDeleteSelected?.Invoke();
            context.ExecuteSelectedMode();
        }

        /// <summary>The answer no (Cancel, Close, closing the popup, another target): back to the menu, no side effects.</summary>
        public void CancelDeleteConfirm()
        {
            if (!_confirmingDelete) return;
            EndDeleteConfirmState();
            RefreshMenu();
        }

        private void EndDeleteConfirmState()
        {
            _confirmingDelete = false;
            _confirmTarget = null;
            if (_deleteToggle != null)
            {
                _deleteToggle.SetIsOnWithoutNotify(false);
                SetToggleLabel(_deleteToggle, _deleteLabelBeforeConfirm ?? DeleteLabel);
                SetRowHeight(_deleteToggle, false);
            }
            if (_cancelDeleteToggle != null) _cancelDeleteToggle.gameObject.SetActive(false);
            if (_deletePrompt != null) _deletePrompt.gameObject.SetActive(false);
        }

        // While the question is open the menu is the question: Delete? and Cancel, plus the menu's Close. Region,
        // Reconstruct and the box menu's Confirm are out of the way, so nothing else can be pressed by mistake.
        private void ApplyDeleteConfirmLayout()
        {
            // The answers are the theme's row height at least, so they are as easy to hit as any row of the kit.
            SetRowHeight(_deleteToggle, true);
            SetRowHeight(_cancelDeleteToggle, true);
            if (_affordanceVolumeToggle != null) _affordanceVolumeToggle.gameObject.SetActive(false);
            if (_reconstructToggle != null) _reconstructToggle.gameObject.SetActive(false);
            if (_confirmToggle != null) _confirmToggle.gameObject.SetActive(false);
            if (_deleteToggle != null) _deleteToggle.gameObject.SetActive(true);
            if (_cancelDeleteToggle != null)
            {
                _cancelDeleteToggle.gameObject.SetActive(true);
                _cancelDeleteToggle.transform.SetSiblingIndex(_deleteToggle.transform.GetSiblingIndex() + 1);
            }
            if (_deletePrompt != null)
            {
                // The question is a full-width line above the row of buttons, not one more button in it.
                Transform row = _deleteToggle.transform.parent;
                Transform column = row != null && row.parent != null ? row.parent : row;
                if (_deletePrompt.transform.parent != column) _deletePrompt.transform.SetParent(column, false);
                _deletePrompt.gameObject.SetActive(true);
                if (column == row)
                {
                    _deletePrompt.transform.SetSiblingIndex(_deleteToggle.transform.GetSiblingIndex());
                }
                else
                {
                    _deletePrompt.transform.SetAsLastSibling();      // so the row's index is its own again
                    _deletePrompt.transform.SetSiblingIndex(row.GetSiblingIndex());
                    // A column of fixed-width rows does not stretch a text: give it the row's width so it wraps inside it.
                    if (column is RectTransform columnRect) LayoutRebuilder.ForceRebuildLayoutImmediate(columnRect);
                    float width = ((RectTransform)row).rect.width;
                    var layout = _deletePrompt.GetComponent<LayoutElement>();
                    if (width > 0f)
                    {
                        // This column does not control widths, so the text is sized to the row directly.
                        var rect = (RectTransform)_deletePrompt.transform;
                        rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
                        if (layout != null) { layout.minWidth = width; layout.preferredWidth = width; }
                    }
                }
            }
            // Cancel (the box menu's name for it) would read twice; the menu's own button is Close while asking.
            if (_exitToggle != null)
                foreach (TMP_Text label in _exitToggle.GetComponentsInChildren<TMP_Text>(true)) label.text = CloseLabel;
        }

        private void EnsureCancelDeleteToggle()
        {
            if (_cancelDeleteToggle != null || _deleteToggle == null) return;
            // A copy of the Delete toggle: the same 84-high row, skin, font and hover feedback, with its own label.
            GameObject clone = Instantiate(_deleteToggle.gameObject, _deleteToggle.transform.parent, false);
            clone.name = "CancelDeleteToggle";
            foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
            {
                if (child != clone.transform && child.name == "Icon") child.gameObject.SetActive(false);
            }
            foreach (LayoutGroup group in clone.GetComponentsInChildren<LayoutGroup>(true))
            {
                if (group.GetComponentInChildren<TMP_Text>(true) != null) group.childAlignment = TextAnchor.MiddleCenter;
            }
            _cancelDeleteToggle = clone.GetComponent<Toggle>();
            _cancelDeleteToggle.SetIsOnWithoutNotify(false);
            _cancelDeleteToggle.onValueChanged.AddListener(OnCancelDeleteToggleChanged);
            SetToggleLabel(_cancelDeleteToggle, CancelDeleteLabel);
            clone.SetActive(false);
        }

        private void OnCancelDeleteToggleChanged(bool isOn)
        {
            if (!isOn) return;
            _cancelDeleteToggle.SetIsOnWithoutNotify(false);
            CancelDeleteConfirm();
        }

        // The question is a line of text in the menu's own title style, above Delete?, in the theme's text colour.
        private void EnsureDeletePrompt()
        {
            if (_deletePrompt != null || _deleteToggle == null) return;
            TMP_Text title = FindTitle();
            var go = new GameObject("DeletePrompt", typeof(RectTransform));
            go.transform.SetParent(_deleteToggle.transform.parent, false); // moved above the row when shown
            _deletePrompt = go.AddComponent<TextMeshProUGUI>();
            if (title != null)
            {
                _deletePrompt.font = title.font;
                _deletePrompt.fontSharedMaterial = title.fontSharedMaterial;
            }
            _deletePrompt.fontSize = UiTheme.FontLabel;
            _deletePrompt.color = UiTheme.Palette.Text;
            _deletePrompt.alignment = TextAlignmentOptions.Center;
            _deletePrompt.textWrappingMode = TextWrappingModes.Normal;
            _deletePrompt.overflowMode = TextOverflowModes.Overflow;
            _deletePrompt.raycastTarget = false;
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = UiTheme.RowHeight;
            layout.flexibleWidth = 1f;
            go.SetActive(false);
        }

        private float? _deleteRowMin, _deleteRowPreferred;

        private void SetRowHeight(Toggle toggle, bool atLeastThemeRow)
        {
            if (toggle == null) return;
            LayoutElement element = toggle.GetComponent<LayoutElement>();
            bool isDelete = toggle == _deleteToggle;
            if (atLeastThemeRow)
            {
                if (element == null) element = toggle.gameObject.AddComponent<LayoutElement>();
                if (isDelete && _deleteRowMin == null) { _deleteRowMin = element.minHeight; _deleteRowPreferred = element.preferredHeight; }
                element.minHeight = Mathf.Max(element.minHeight, UiTheme.RowHeight);
                element.preferredHeight = Mathf.Max(element.preferredHeight, UiTheme.RowHeight);
            }
            else if (isDelete && element != null && _deleteRowMin != null)
            {
                element.minHeight = _deleteRowMin.Value;
                element.preferredHeight = _deleteRowPreferred.Value;
                _deleteRowMin = _deleteRowPreferred = null;
            }
        }

        // The skinned button has an inactive and an active label; every label says the same, so the visible one does.
        // The label the toggle shows (Capture/Reconstruct and Exit): the ISDK shelf toggle carries an inactive placeholder "Label" before the shown "Label (1)",
        // so the first child label is not the button's text. A label is shown when it and its parents up to the toggle are active.
        private static TMP_Text ShownLabel(Toggle toggle)
        {
            if (toggle == null) return null;
            TMP_Text first = null;
            foreach (TMP_Text label in toggle.GetComponentsInChildren<TMP_Text>(true))
            {
                if (first == null) first = label;
                bool shown = true;
                for (Transform t = label.transform; t != null && t != toggle.transform; t = t.parent)
                    if (!t.gameObject.activeSelf) { shown = false; break; }
                if (shown) return label;
            }
            return first;
        }

        private static void SetToggleLabel(Toggle toggle, string text)
        {
            if (toggle == null) return;
            // Pressed or not, the button is a button: it must look off, whatever the press just did to its look.
            if (toggle.TryGetComponent(out UiToggleVisual look)) look.Apply(toggle.isOn);
            foreach (TMP_Text label in toggle.GetComponentsInChildren<TMP_Text>(true)) label.text = text;
            foreach (Text legacy in toggle.GetComponentsInChildren<Text>(true)) legacy.text = text;
        }

        private void SetToggleOff(Toggle toggle)
        {
            if (toggle != null && toggle.isOn)
            {
                // Use isOn directly to trigger visual update
                // The callback will exit early due to if (!isOn) return;
                toggle.isOn = false;
            }
        }

        /// <summary>
        /// Reset all mode toggle selections to off state.
        /// Called when panel is shown to clear previous selection history.
        /// </summary>
        public void ResetSelection()
        {
            CancelDeleteConfirm();
            SetToggleOff(_affordanceVolumeToggle);
            SetToggleOff(_reconstructToggle);
            SetToggleOff(_gizmoToggle);
            SetToggleOff(_deleteToggle);
        }

        private void OnConfirmToggleChanged(bool isOn)
        {
            if (!isOn) return;
            CancelDeleteConfirm(); // the Delete question has its own answer; this Confirm is not it
            OnConfirm?.Invoke();
        }

        private void OnExitToggleChanged(bool isOn)
        {
            if (!isOn) return;
            CancelDeleteConfirm(); // Close abandons the question
            OnExit?.Invoke();
        }
    }
}
