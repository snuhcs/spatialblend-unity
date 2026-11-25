// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Oculus.Interaction;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Interactions.UI;
using SpatialBlend.Samples.Scanning;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Makes moving between Interaction and Scene Edit effortless without changing the two modes. It adds three things; the mode panel
    /// and everything else stay.
    /// <list type="bullet">
    /// <item><b>Hover prompt.</b> In Interaction mode, when the ray rests on a SAM3D scan a share dropped on the floor before anyone
    /// placed it, an "Edit this object?" prompt with an [Edit] button floats above it. [Edit] switches to Scene Edit. A placed scan,
    /// a scene object and the user's own scan do not raise it.</item>
    /// <item><b>Idle auto-return.</b> In Scene Edit, with no editing activity for <see cref="SpatialBlendSettings.modeSwitchIdleSeconds"/> (10 s), a
    /// toast says "Back to Interaction in N..." with a [Keep editing] button; after <see cref="SpatialBlendSettings.modeSwitchCountdownSeconds"/>
    /// (5 s) the mode switches. Activity or [Keep editing] cancels it. It never switches under a grab, a box being drawn, an open
    /// object menu or an open scan panel.</item>
    /// <item><b>Mode chip.</b> A small chip, always in view, shows the mode; tapping it switches.</item>
    /// <item><b>Mode-change cue</b>. Every change of mode, whatever made it, plays a right-controller pulse (two for the idle return), a
    /// rising or falling chime, and a caption under the chip for 3 s that says what the mode is for. Entering Editing without the right
    /// controller says that its tools need one.</item>
    /// </list>
    /// <item><b>Hands' tools</b>. A hand has no A or B. While the active input is the hand, Scene Edit floats a [Menu] button over a hovered
    /// object that has an object menu (A's stand-in) and shows an [Undo] button by the chip when there is something to undo (B's).</item>
    /// The mode is local to each user; nothing here is networked. Everything reads the active editing ray through <see cref="EditInput"/>:
    /// the right controller's, or the right hand's when hands are the input. The two timing values are settings; the prompt linger
    /// (<see cref="_promptLingerSeconds"/>, 0.4 s) is only long enough to cross to the [Edit] button. The hands' Menu button has its own linger
    /// (<see cref="_handMenuLingerSeconds"/>, <see cref="DefaultHandMenuLingerSeconds"/> s): the button floats above the object, and a hand
    /// takes longer than 0.4 s to reach it. A hand ray also jitters, so a moment off the object must not hide the button. Controllers keep
    /// the 0.4 s Edit prompt and do not show the Menu button.
    /// <para>A tutorial that sets modes itself calls <see cref="Suspend"/> with its own object and <see cref="Resume"/> when done.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ModeSwitchAssist : MonoBehaviour
    {
        public static ModeSwitchAssist Instance { get; private set; }

        private static readonly HashSet<object> s_Suspenders = new HashSet<object>();
        private static readonly List<Func<bool>> s_BusyProbes = new List<Func<bool>>();

        [Header("Parts")]
        [SerializeField] private bool _showHoverPrompt = true;
        [SerializeField] private bool _showIdleCountdown = true;
        [SerializeField] private bool _showModeChip = true;
        [SerializeField, Tooltip("The pulse, chime and caption on every mode change.")]
        private bool _showModeCue = true;
        [SerializeField, Tooltip("The Menu button over a hovered object and the Undo button, shown to hands in Editing.")]
        private bool _showHandTools = true;

        [Header("Activation")]
        [SerializeField, Tooltip("Off: nothing shows and nothing counts until SetActive(true). An app that has a session to wait for (Freeform Collaboration, which has no use for modes before it is connected) turns this off and calls SetActive when the session is ready.")]
        private bool _startActive = true;

        [Header("Hover prompt")]
        [SerializeField, Min(0f), Tooltip("Seconds the prompt stays after the ray leaves the object, so the ray can cross to the [Edit] button floating above it. Default 0.4 s; it is only long enough to cross that gap. Controllers use this. The hands' Menu button uses Hand Menu Linger instead.")]
        private float _promptLingerSeconds = 0.4f;
        [SerializeField, Min(0f), Tooltip("Seconds the hands' Menu button stays after the hand ray leaves the object, so the hand can reach the button floating above it. A hand is slower than a controller ray, and the ray jitters, so this is longer than the Edit prompt's 0.4 s. Hovering the object or the button again cancels the hide. Controllers do not show this button.")]
        private float _handMenuLingerSeconds = DefaultHandMenuLingerSeconds;
        [SerializeField, Min(0f), Tooltip("Metres between the top of the object and the prompt.")]
        private float _promptGapMetres = 0.15f;

        [Header("Debug")]
        [SerializeField] private bool _debugLog;

        private ModeSwitchIdleTimer _timer;
        private ModeSwitchHoverModel _hover;
        private ModeSwitchHoverModel _menuHover;
        private Action _menuToggle;
        private bool _polledUndoable;
        private float _nextUndoPoll;
        private ModeSwitchUi _ui;
        private bool _active;
        private SpatialBlendSettings _settings;

        private RayInteractable _lastSelected;
        private float _nextBusyPoll;
        private bool _polledBusy;
        private InteractionMode _shownChipMode;
        private bool _chipShown;

        private bool _subscribedToButton;

        // The mode the last cue was for (null until the first step after activation sets the baseline), whether the next change is the
        // idle return, and when the caption hides.
        private InteractionMode? _cuedMode;
        private bool _idleSwitchPending;
        private float _captionHideAt = -1f;
        private bool _wasSuspended;
        // A change made while the assist was live, seen at the moment it happened (InteractionModeManager.ModeChanged is synchronous). The
        // tutorial can suspend the assist later in the same frame the user reaches Editing; that change is still the user's and is cued.
        private bool _liveChangePending;
        private bool _subscribedToModes;
        private static AudioClip s_ChimeIntoEditing, s_ChimeIntoInteraction;

        // ---- mode-change cue ----

        public const string EditingCaption = "Editing: move things, capture real objects. Tap the chip when done.";
        /// <summary>The Editing caption of an app that has no mode chip.</summary>
        public const string EditingCaptionNoChip = "Editing: move things, capture real objects.";
        public const string InteractionCaption = "Interaction: use and play.";
        public const string IdleReturnCaption = "Back to Interaction (no edits for a while).";
        /// <summary>What Editing says on hands: the two things a hand does that a controller does with its stick.</summary>
        public const string HandsEditingCaption = "Editing: pinch to move things, twist your wrist to turn them.";
        public const float CaptionSeconds = 3f;
        public const float HandsCaptionSeconds = 5f;

        /// <summary>Plays the controller pulse: 1 for a change the user made, 2 for the idle return. Replaced by a test; null is the right Touch controller.</summary>
        public Action<int> Haptic { get; set; }

        /// <summary>Plays the chime: true rises (into Editing), false falls (into Interaction). Replaced by a test; null is the synthesized two-tone at the eye.</summary>
        public Action<bool> Chime { get; set; }

        /// <summary>Whether the hand is the active editing input. Replaced by a test; null asks <see cref="EditInput.ActiveSource"/>.</summary>
        public Func<bool> UsesHands { get; set; }

        /// <summary>The names of Editing features that still need a controller and are in play. Replaced by a test; null asks <see cref="EditInput.ControllerOnlyFeatures"/>.</summary>
        public Func<List<string>> ControllerOnlyInPlay { get; set; }

        /// <summary>How many mode-change cues have played, and the last one's caption. For tests and renders.</summary>
        public int CueCount { get; private set; }
        public string LastCueCaption { get; private set; }

        /// <summary>The clock, in seconds. Replaced by a test; the default is unscaled time, so a paused game does not stop the countdown.</summary>
        public Func<float> Clock { get; set; } = () => Time.unscaledTime;

        /// <summary>What the active editing ray is on (hovering or selecting). Replaced by a test; the default reads <see cref="EditInput.ActivePointer"/>.</summary>
        public Func<RayInteractable> HoveredSource { get; set; } = () => EditInput.ActivePointer?.Hovered;

        /// <summary>What the active editing ray has selected (a grab). Replaced by a test; the default reads <see cref="EditInput.ActivePointer"/>.</summary>
        public Func<RayInteractable> SelectedSource { get; set; } = () => EditInput.ActivePointer?.Selected;

        /// <summary>Whether the user is in the app (not in the system menu, not with the headset off). The timer is held while not. Replaced by a test.</summary>
        public Func<bool> HasInputFocus { get; set; } = () => !OVRManager.isHmdPresent || OVRManager.hasInputFocus;

        /// <summary>The widgets, for tests and renders.</summary>
        public ModeSwitchUi Ui => _ui;
        public ModeSwitchIdleTimer Timer => _timer;
        public bool IsActive => _active;

        /// <summary>
        /// Whether the mode chip stands in for the mode panel's toggles (the mode chip replaces the panel's mode row). An app
        /// that has such a panel keeps it hidden while this is true.
        /// </summary>
        public bool ReplacesModePanel => _showModeChip && isActiveAndEnabled;

        // ---- tutorial hook ----

        /// <summary>
        /// Pauses the assist (no prompt, no toast, no chip, no auto-return) until every owner that suspended it has called
        /// <see cref="Resume"/>. The owner is any object; suspending twice with the same owner counts once.
        /// </summary>
        public static void Suspend(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            s_Suspenders.Add(owner);
        }

        /// <summary>Releases <paramref name="owner"/>'s suspension. The assist resumes when the last owner has resumed; the idle wait then starts afresh.</summary>
        public static void Resume(object owner)
        {
            if (owner != null) s_Suspenders.Remove(owner);
        }

        /// <summary>Whether any owner holds the assist suspended. An owner that is a Unity object and was destroyed without resuming no longer counts.</summary>
        public static bool IsSuspended
        {
            get
            {
                if (s_Suspenders.Count == 0) return false;
                s_Suspenders.RemoveWhere(owner => owner is UnityEngine.Object unityOwner && unityOwner == null);
                return s_Suspenders.Count > 0;
            }
        }

        /// <summary>
        /// Registers a source of "editing is in progress" that the assist does not know (for example a pending tutorial step). While any probe
        /// returns true, Scene Edit counts as busy: the timer is held and nothing switches.
        /// </summary>
        public static void AddBusyProbe(Func<bool> probe)
        {
            if (probe != null && !s_BusyProbes.Contains(probe)) s_BusyProbes.Add(probe);
        }

        public static void RemoveBusyProbe(Func<bool> probe) => s_BusyProbes.Remove(probe);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Suspenders.Clear();
            s_BusyProbes.Clear();
            Instance = null;
        }

        /// <summary>Turns the assist on or off (see the <c>Start Active</c> setting). Turning it off hides the widgets; turning it on starts afresh.</summary>
        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            // A mode set while the assist was off (the session start sets Interaction just before activating it) is not a change to announce.
            _cuedMode = null;
            _idleSwitchPending = false;
            if (!active) HideAll();
            else _timer?.NotifyActivity(Clock());
        }

        // ---- lifecycle ----

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                Destroy(this);
                return;
            }
            Instance = this;
            _active = _startActive;
            ResolveSettings();
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
        }

        private void OnDisable()
        {
            // A pulse cut short must not leave the controller buzzing.
            if (!Application.isPlaying) return;
            StopAllCoroutines();
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        }

        private void OnModeChanged(InteractionMode mode)
        {
            if (this == null) return; // a destroyed assist whose handler outlived it (EditMode destroys without OnDestroy)
            if (_active && !IsSuspended) _liveChangePending = true;
        }

        private void SubscribeToModes()
        {
            if (_subscribedToModes) return;
            InteractionModeManager.ModeChanged += OnModeChanged;
            _subscribedToModes = true;
        }

        private void OnDestroy()
        {
            if (_subscribedToModes) InteractionModeManager.ModeChanged -= OnModeChanged;
            _subscribedToModes = false;
            UnsubscribeFromButton();
            if (Instance == this) Instance = null;
            if (_ui != null && Application.isPlaying) Destroy(_ui.gameObject);
        }

        private void ResolveSettings()
        {
            _settings = SpatialBlendManager.CurrentSettings != null
                ? SpatialBlendManager.CurrentSettings
                : Resources.Load<SpatialBlendSettings>("SpatialBlendSettings");
            float idle = _settings != null && _settings.modeSwitchIdleSeconds > 0f ? _settings.modeSwitchIdleSeconds : SpatialBlendSettings.DefaultModeSwitchIdleSeconds;
            float countdown = _settings != null && _settings.modeSwitchCountdownSeconds > 0f ? _settings.modeSwitchCountdownSeconds : SpatialBlendSettings.DefaultModeSwitchCountdownSeconds;
            _timer = new ModeSwitchIdleTimer(idle, countdown);
            float dwell = _settings != null ? _settings.modeSwitchHoverDwellSeconds : SpatialBlendSettings.DefaultModeSwitchHoverDwellSeconds;
            ConfigureHoverDwell(dwell);
        }

        /// <summary>Sets how long the ray must stay on one editable object before the prompt shows (1 s by default, from <see cref="SpatialBlendSettings"/>).</summary>
        public void ConfigureHoverDwell(float seconds)
        {
            _hover = new ModeSwitchHoverModel(_promptLingerSeconds, seconds);
            // Hands only. The Edit prompt keeps _promptLingerSeconds (0.4 s, controllers). The Menu button floats above the object;
            // 0.4 s hides it while the hand is still crossing the gap, and a jittery hand ray can drop the hover for a frame on the way.
            _menuHover = new ModeSwitchHoverModel(Mathf.Max(0f, _handMenuLingerSeconds), HandMenuDwellSeconds, InteractionMode.SceneEdit);
        }

        /// <summary>Seconds the hand ray rests on an object before its Menu button shows: long enough that a pass over it shows nothing, short enough to feel like a hover state.</summary>
        public const float HandMenuDwellSeconds = 0.3f;

        /// <summary>
        /// Seconds the hands' Menu button stays after the ray leaves the object. Long enough to reach a button floating above it
        /// (3 s); a one-frame hover drop is inside this window and returning to the object or the button cancels the hide.
        /// Controllers do not show this button.
        /// </summary>
        public const float DefaultHandMenuLingerSeconds = 3.0f;

        private void Start()
        {
            // Built when first needed, so an app that registers its typeface late (FC: the control panel) is read after it.
            if (_active) EnsureUi();
        }

        private void EnsureUi()
        {
            if (_ui != null) return;
            var go = new GameObject("ModeSwitchUi");
            go.transform.SetParent(transform, false);
            _ui = go.AddComponent<ModeSwitchUi>();
            _ui.Build(FindEye());
            _ui.EditClicked += OnEditClicked;
            _ui.KeepEditingClicked += OnKeepEditingClicked;
            _ui.ChipClicked += OnChipClicked;
            _ui.MenuClicked += OnMenuClicked;
            _ui.UndoClicked += OnUndoClicked;
            _ui.SnapHud();
            _chipShown = false;
        }

        private static Transform FindEye()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null) return rig.centerEyeAnchor;
            return Camera.main != null ? Camera.main.transform : null;
        }

        private void Update()
        {
            if (_timer == null) ResolveSettings();
            if (!_active && _ui == null && !(InteractionModeManager.Instance != null && WantsHandTools(InteractionModeManager.Instance.CurrentMode))) return;
            EnsureUi();
            if (!_ui.HasEye)
            {
                // The camera rig can appear after this component starts.
                Transform eye = FindEye();
                if (eye != null)
                {
                    _ui.SetEye(eye);
                    _ui.SnapHud();
                }
            }
            Step(Clock());
        }

        // ---- one step of the assist; public so a test can drive it with its own clock ----

        public void Step(float now)
        {
            var manager = InteractionModeManager.Instance;
            if (manager == null || _timer == null) return;
            if (_ui == null)
            {
                // An inactive assist builds its widgets only for the hands' tools, which work whether or not the app has the rest on.
                if (!_active && !WantsHandTools(manager.CurrentMode)) return;
                EnsureUi();
            }

            bool suspended = IsSuspended || !_active;
            InteractionMode mode = manager.CurrentMode;
            RayInteractable hovered = HoveredSource();
            RayInteractable selected = SelectedSource();
            SubscribeToButton();
            SubscribeToModes();

            // A select or unselect on an editable object is editing activity.
            if (selected != _lastSelected)
            {
                if (mode == InteractionMode.SceneEdit && (IsEditableInteractable(selected) || IsEditableInteractable(_lastSelected)))
                    _timer.NotifyActivity(now);
                _lastSelected = selected;
            }

            UpdateCue(now, mode, suspended);
            UpdateChip(mode, suspended);
            UpdatePrompt(now, mode, hovered, selected, suspended);
            UpdateHandTools(now, mode, hovered, selected);
            UpdateIdleReturn(now, manager, mode, selected != null, suspended);
        }

        private void UpdateChip(InteractionMode mode, bool suspended)
        {
            bool visible = _showModeChip && !suspended;
            _ui.SetChipVisible(visible);
            if (!visible) return;
            if (!_chipShown || _shownChipMode != mode)
            {
                _ui.SetChipMode(mode);
                _shownChipMode = mode;
                _chipShown = true;
            }
        }

        private void UpdatePrompt(float now, InteractionMode mode, RayInteractable hovered, RayInteractable selected, bool suspended)
        {
            int hoveredEditable = ModeSwitchHoverModel.NoTarget;
            bool promptHovered = false;
            Transform root = null;
            if (_showHoverPrompt && hovered != null)
            {
                if (_ui.IsPromptButton(hovered)) promptHovered = true;
                else if (!ModeSwitchUi.IsWidget(hovered) && TryResolveHoverEdit(hovered, out root)) hoveredEditable = root.GetInstanceID();
            }

            // The user is working an object with the trigger (not pressing the prompt's button): the prompt gets out of the way.
            bool interacting = selected != null && !_ui.IsPromptButton(selected);
            int target = _hover.Tick(now, mode, hoveredEditable, promptHovered, suspended || interacting || !_showHoverPrompt);
            if (target == ModeSwitchHoverModel.NoTarget)
            {
                _ui.HidePrompt();
                return;
            }

            // Draw at the hovered object only once the model has adopted it (after its dwell); until then the prompt stays where it is.
            Transform shownRoot = hoveredEditable != ModeSwitchHoverModel.NoTarget && hoveredEditable == target ? root : _ui.PromptRoot;
            if (shownRoot == null)
            {
                _hover.Clear();
                _ui.HidePrompt();
                return;
            }
            if (_ui.PromptRoot != shownRoot) _ui.ShowPrompt(shownRoot, ObjectBounds(shownRoot), _promptGapMetres);
        }

        // ---- the hands' tools ----

        private bool HandsActive() => (UsesHands ?? DefaultUsesHands)();

        private static bool DefaultUsesHands() => EditInput.ActiveSource == EditInputSource.Hand;

        /// <summary>
        /// A hand has no A button and no B. In Editing, while hands are the input: the Menu button floats over a hovered object that has an object
        /// menu, and the Undo button shows by the chip when something can be undone (a box point being drawn, or the last move).
        /// </summary>
        private bool WantsHandTools(InteractionMode mode) => _showHandTools && mode == InteractionMode.SceneEdit && HandsActive();

        // The hands' tools do not depend on the rest of the assist: the tutorial suspends it for most tasks (the scan task needs the Menu button), and an
        // app turns it off until its session is ready. They show whenever the mode is Editing and the input is a hand.
        private void UpdateHandTools(float now, InteractionMode mode, RayInteractable hovered, RayInteractable selected)
        {
            bool tools = WantsHandTools(mode);
            if (tools && now >= _nextUndoPoll)
            {
                _nextUndoPoll = now + 0.25f;
                _polledUndoable = Undoable();
            }
            _ui.SetUndoVisible(tools && _polledUndoable);

            int hoveredMenu = ModeSwitchHoverModel.NoTarget;
            bool buttonHovered = false;
            Transform root = null;
            Action toggle = null;
            if (tools && hovered != null)
            {
                if (_ui.IsMenuButton(hovered)) buttonHovered = true;
                else if (TryResolveMenuTarget(hovered, out root, out toggle, out bool open) && !open) hoveredMenu = root.GetInstanceID();
            }

            bool interacting = selected != null && !_ui.IsMenuButton(selected);
            int target = _menuHover.Tick(now, mode, hoveredMenu, buttonHovered, !tools || interacting);
            if (target == ModeSwitchHoverModel.NoTarget)
            {
                _ui.HideMenuPrompt();
                _menuToggle = null;
                return;
            }
            Transform shown = hoveredMenu != ModeSwitchHoverModel.NoTarget && hoveredMenu == target ? root : _ui.MenuPromptRoot;
            if (shown == null)
            {
                _menuHover.Clear();
                _ui.HideMenuPrompt();
                _menuToggle = null;
                return;
            }
            if (hoveredMenu == target && toggle != null) _menuToggle = toggle;
            if (_ui.MenuPromptRoot != shown) _ui.ShowMenuPrompt(shown, ObjectBounds(shown), _promptGapMetres);
        }

        /// <summary>
        /// Whether the object under <paramref name="interactable"/> has an object menu that A opens (a scan, a box, a glTF object), and how to toggle
        /// it. <paramref name="open"/> is whether that menu is showing now (the Menu button is then not offered: the menu is the thing to use).
        /// </summary>
        public static bool TryResolveMenuTarget(RayInteractable interactable, out Transform root, out Action toggle, out bool open)
        {
            root = null;
            toggle = null;
            open = false;
            if (interactable == null || ModeSwitchUi.IsWidget(interactable)) return false;

            var panelMotif = interactable.GetComponentInParent<ManipulationPanelMotif>();
            var gltfMotif = panelMotif == null ? interactable.GetComponentInParent<GltfObjectControlPanelMotif>() : null;
            if (panelMotif == null && gltfMotif == null)
            {
                var entity = interactable.GetComponentInParent<SbEntity>();
                if (entity != null)
                {
                    panelMotif = entity.GetComponentInChildren<ManipulationPanelMotif>(true);
                    if (panelMotif == null) gltfMotif = entity.GetComponentInChildren<GltfObjectControlPanelMotif>(true);
                }
            }
            if (panelMotif != null && panelMotif.IsGatedTo(InteractionMode.SceneEdit))
            {
                root = panelMotif.transform;
                toggle = panelMotif.ToggleMenu;
                open = panelMotif.IsMenuOpen;
                return true;
            }
            if (gltfMotif != null)
            {
                root = gltfMotif.transform;
                toggle = gltfMotif.ToggleMenu;
                open = gltfMotif.IsPanelShowing;
                return true;
            }
            return false;
        }

        private void OnMenuClicked()
        {
            var toggle = _menuToggle;
            _menuHover.Clear();
            _menuToggle = null;
            _ui.HideMenuPrompt();
            _timer?.NotifyActivity(Clock());
            toggle?.Invoke();
        }

        private void OnUndoClicked()
        {
            _timer?.NotifyActivity(Clock());
            EditInput.RequestUndo();
        }

        private void UpdateIdleReturn(float now, InteractionModeManager manager, InteractionMode mode, bool selecting, bool suspended)
        {
            bool edit = mode == InteractionMode.SceneEdit;
            // During the countdown the open work is looked for every frame: the A button opens the object menu without an ISDK select,
            // and a switch must never land on the frame after it opened.
            bool busy = edit && (selecting || !HasInputFocus() || PollBusy(now, _timer.Phase == ModeSwitchPhase.Countdown));
            ModeSwitchTick tick = _timer.Tick(now, edit, busy, suspended || !_showIdleCountdown);
            if (tick.Phase == ModeSwitchPhase.Countdown)
            {
                // The toast and the caption share the upper band of the view: the countdown is the news now.
                if (_captionHideAt >= 0f)
                {
                    _ui.HideCaption();
                    _captionHideAt = -1f;
                }
                _ui.ShowToast(tick.CountdownDisplaySeconds, tick.CountdownRemaining / _timer.CountdownSeconds);
            }
            else
            {
                _ui.HideToast();
            }
            if (tick.SwitchNow)
            {
                _idleSwitchPending = true;
                manager.SetMode(InteractionMode.Interaction);
            }
        }

        // ---- the mode-change cue ----

        // Every change of mode while the assist is live, whoever made it (the chip, [Edit], the idle return, the control panel), gets a pulse, a
        // chime and a caption that says what the mode is for: the chip alone is one peripheral indicator (NN/g: at least two), and hands users
        // have no haptics at all. A suspended assist (the tutorial narrates its own changes) or an inactive one cues nothing.
        private void UpdateCue(float now, InteractionMode mode, bool suspended)
        {
            bool liveChange = _liveChangePending;
            _liveChangePending = false;
            if (_cuedMode == null || (_wasSuspended && !suspended && !liveChange))
            {
                // The first step, or the first after a suspension ends: the mode now is the baseline. A change the suspender made (the
                // tutorial restoring the user's mode as it ends) is its own to explain, not a cue.
                _cuedMode = mode;
            }
            else if (_cuedMode.Value != mode)
            {
                _cuedMode = mode;
                // Cued when the assist is live now, or was live when the change happened (the suspension came after it, in the same frame).
                if ((!suspended || liveChange) && _showModeCue) Cue(now, mode, _idleSwitchPending, chipShown: !suspended && _showModeChip);
            }
            // The idle return sets the flag in the step before; whatever this step saw, it is spent.
            _idleSwitchPending = false;
            _wasSuspended = suspended;

            // A caption already up when a suspension begins runs out its time: the tutorial suspends the assist the frame the user reaches
            // Editing, and that is when the caption matters.
            if (_captionHideAt >= 0f && now >= _captionHideAt)
            {
                _ui.HideCaption();
                _captionHideAt = -1f;
            }
        }

        private void Cue(float now, InteractionMode mode, bool idleReturn, bool chipShown)
        {
            bool editing = mode == InteractionMode.SceneEdit;
            (Haptic ?? DefaultHaptic)(idleReturn ? 2 : 1);
            (Chime ?? DefaultChime)(editing);

            // A suspended assist hides the chip, so the caption does not point at it.
            string caption = CaptionFor(mode, idleReturn, HandsActive(), chipShown, (ControllerOnlyInPlay ?? DefaultControllerOnlyInPlay)());
            _ui.ShowCaption(caption);
            _captionHideAt = now + (editing && HandsActive() ? HandsCaptionSeconds : CaptionSeconds);
            CueCount++;
            LastCueCaption = caption;
        }

        /// <summary>The last notice asked for (a refused grab), shown or not. For tests and logs.</summary>
        public string LastNotice { get; private set; }

        /// <summary>
        /// A one-line notice in the caption slot beside the chip, for <see cref="CaptionSeconds"/>: what a refused grab says ("Player 2 is
        /// moving this"). False when there is no live assist to show it (the lobby, a test without one).
        /// </summary>
        public static bool ShowNotice(string text)
        {
            ModeSwitchAssist assist = Instance;
            if (assist == null || string.IsNullOrEmpty(text)) return false;
            assist.LastNotice = text;
            if (!assist._active || assist._ui == null) return false;
            assist._ui.ShowCaption(text);
            assist._captionHideAt = assist.Clock() + CaptionSeconds;
            return true;
        }

        /// <summary>
        /// The caption for a change into <paramref name="mode"/>. On hands, Editing says how a hand does what a controller does with its stick.
        /// If Editing features that still need a controller are in play (<paramref name="controllerOnly"/>), the caption names them instead: that is
        /// the one case a hands user is told to pick a controller up.
        /// </summary>
        public static string CaptionFor(InteractionMode mode, bool idleReturn, bool usesHands, bool hasChip = true, List<string> controllerOnly = null)
        {
            if (mode == InteractionMode.SceneEdit)
            {
                if (usesHands && controllerOnly != null && controllerOnly.Count > 0)
                    return "Editing: " + string.Join(", ", controllerOnly) + " needs a controller. Pick one up for that.";
                if (usesHands) return HandsEditingCaption;
                return hasChip ? EditingCaption : EditingCaptionNoChip;
            }
            return idleReturn ? IdleReturnCaption : InteractionCaption;
        }

        private static List<string> DefaultControllerOnlyInPlay() => EditInput.ControllerOnlyFeatures.InPlay();

        private void DefaultHaptic(int pulses)
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return;
            StartCoroutine(PulseRightController(pulses));
        }

        private static System.Collections.IEnumerator PulseRightController(int pulses)
        {
            for (int i = 0; i < pulses; i++)
            {
                OVRInput.SetControllerVibration(1f, 0.5f, OVRInput.Controller.RTouch);
                yield return new WaitForSecondsRealtime(0.06f);
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
                if (i + 1 < pulses) yield return new WaitForSecondsRealtime(0.08f);
            }
        }

        private void DefaultChime(bool intoEditing)
        {
            if (!Application.isPlaying) return;
            ref AudioClip clip = ref intoEditing ? ref s_ChimeIntoEditing : ref s_ChimeIntoInteraction;
            if (clip == null)
            {
                float[] samples = ModeSwitchChime.Samples(intoEditing, ModeSwitchChime.SampleRate);
                clip = AudioClip.Create(intoEditing ? "ModeChimeEditing" : "ModeChimeInteraction", samples.Length, 1, ModeSwitchChime.SampleRate, false);
                clip.SetData(samples, 0);
            }
            Transform eye = FindEye();
            AudioSource.PlayClipAtPoint(clip, eye != null ? eye.position : transform.position, 0.35f);
        }

        // ---- the A button ----

        // The A button opens the object menu in Scene Edit. It is not an ISDK select, so it is its own editing activity.
        private void SubscribeToButton()
        {
            if (_subscribedToButton) return;
            var selector = InteractionModeManager.CustomButtonSelector;
            if (selector == null) return;
            selector.WhenSelected += OnObjectMenuButton;
            _subscribedToButton = true;
        }

        private void UnsubscribeFromButton()
        {
            if (!_subscribedToButton) return;
            var selector = InteractionModeManager.CustomButtonSelector;
            if (selector != null) selector.WhenSelected -= OnObjectMenuButton;
            _subscribedToButton = false;
        }

        private void OnObjectMenuButton()
        {
            if (InteractionModeManager.IsMode(InteractionMode.SceneEdit)) _timer?.NotifyActivity(Clock());
        }

        // ---- button handlers ----

        private void OnEditClicked()
        {
            var manager = InteractionModeManager.Instance;
            if (manager == null) return;
            _hover.Clear();
            _ui.HidePrompt();
            manager.SetMode(InteractionMode.SceneEdit);
        }

        private void OnKeepEditingClicked()
        {
            _timer.KeepEditing(Clock());
            _ui.HideToast();
        }

        private void OnChipClicked()
        {
            var manager = InteractionModeManager.Instance;
            if (manager == null) return;
            manager.SetMode(manager.CurrentMode == InteractionMode.SceneEdit ? InteractionMode.Interaction : InteractionMode.SceneEdit);
        }

        private void HideAll()
        {
            if (_ui == null) return;
            _ui.HidePrompt();
            _ui.HideToast();
            _ui.HideCaption();
            _captionHideAt = -1f;
            _ui.SetChipVisible(false);
            _hover?.Clear();
            _chipShown = false;
        }

        // ---- what counts as editing work in progress ----

        private bool PollBusy(float now, bool force)
        {
            if (force || now >= _nextBusyPoll)
            {
                _nextBusyPoll = now + 0.25f;
                _polledBusy = ScanBusy();
            }
            for (int i = 0; i < s_BusyProbes.Count; i++)
            {
                if (s_BusyProbes[i]()) return true;
            }
            return _polledBusy;
        }

        /// <summary>Whether B would undo something: a box point or an edit being drawn, a region collecting points, or the last object move.</summary>
        private static bool Undoable()
        {
            foreach (var box in FindObjectsByType<BoxAnnotationMotif>(FindObjectsSortMode.None))
                if (!box.IsIdleState) return true;
            foreach (var motif in FindObjectsByType<FloorVertexCollectionMotif>(FindObjectsSortMode.None))
                if (motif.IsActive) return true;
            return ObjectMoveHistory.CanUndoNow();
        }

        /// <summary>An object menu open, a scan panel or capture card open, or a box annotation being drawn or dragged.</summary>
        private static bool ScanBusy()
        {
            foreach (var context in FindObjectsByType<ManipulationContext>(FindObjectsSortMode.None))
            {
                if (context.IsPanelShowing || context.IsPassthroughPanelOpen) return true;
            }
            foreach (var panel in FindObjectsByType<PassthroughPanelManager>(FindObjectsSortMode.None))
            {
                if (panel.IsPanelOpen) return true;
            }
            if (ScanCard.AnyCapturing) return true;
            foreach (var box in FindObjectsByType<BoxAnnotationMotif>(FindObjectsSortMode.None))
            {
                if (!box.IsIdleState) return true;
            }
            // Region (affordance) and other floor-point annotations collecting their points: a long pause between two points is still work.
            foreach (var motif in FindObjectsByType<FloorVertexCollectionMotif>(FindObjectsSortMode.None))
            {
                if (motif.IsActive) return true;
            }
            return false;
        }

        // ---- which objects can be edited ----

        /// <summary>
        /// Whether the object under <paramref name="interactable"/> can be edited: it has a placement motif or a manipulation motif that is gated to
        /// Scene Edit. <paramref name="root"/> is the object (the entity, or the annotation) the prompt belongs to. The widgets' own buttons are never editable.
        /// </summary>
        public static bool TryResolveEditable(RayInteractable interactable, out Transform root)
        {
            root = null;
            if (interactable == null || ModeSwitchUi.IsWidget(interactable)) return false;

            var panelMotif = interactable.GetComponentInParent<ManipulationPanelMotif>();
            if (panelMotif != null && panelMotif.IsGatedTo(InteractionMode.SceneEdit))
            {
                root = panelMotif.transform;
                return true;
            }

            var marker = interactable.GetComponentInParent<ModeSwitchEditableMarker>();
            if (marker != null)
            {
                root = marker.transform;
                return true;
            }

            var entity = interactable.GetComponentInParent<SbEntity>();
            if (entity != null)
            {
                foreach (var motif in entity.GetComponentsInChildren<PlacementMotif>(true))
                {
                    if (!motif.IsGatedTo(InteractionMode.SceneEdit)) continue;
                    root = entity.transform;
                    return true;
                }
                foreach (var motif in entity.GetComponentsInChildren<ManipulationPanelMotif>(true))
                {
                    if (!motif.IsGatedTo(InteractionMode.SceneEdit)) continue;
                    root = entity.transform;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The hover Edit prompt. Interaction mode only, and only over a SAM3D scan the share dropped on the floor
        /// before anyone placed it, or over an object that explicitly opts in (<see cref="ModeSwitchEditableMarker"/>,
        /// the tutorial cube). A chess board, a whiteboard, a placed scan and the user's own scan do not raise it.
        /// </summary>
        public static bool TryResolveHoverEdit(RayInteractable interactable, out Transform root)
        {
            root = null;
            if (interactable == null || ModeSwitchUi.IsWidget(interactable)) return false;

            var pending = interactable.GetComponentInParent<UnplacedSharedScan>();
            if (pending != null && !pending.PositionSpecified)
            {
                root = pending.transform;
                return true;
            }

            var marker = interactable.GetComponentInParent<ModeSwitchEditableMarker>();
            if (marker != null)
            {
                root = marker.transform;
                return true;
            }
            return false;
        }

        /// <summary>The prompt's rule: Interaction mode, and only an unplaced shared scan (or the tutorial marker).</summary>
        public static bool ShouldPrompt(InteractionMode mode, RayInteractable interactable) =>
            mode == InteractionMode.Interaction && TryResolveHoverEdit(interactable, out _);

        private static bool IsEditableInteractable(RayInteractable interactable) => TryResolveEditable(interactable, out _);

        private static Bounds ObjectBounds(Transform root)
        {
            var bounds = new Bounds(root.position, Vector3.zero);
            bool any = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (renderer is TrailRenderer || renderer is LineRenderer || renderer is ParticleSystemRenderer) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (any) return bounds;
            foreach (var collider in root.GetComponentsInChildren<Collider>())
            {
                if (!any) { bounds = collider.bounds; any = true; }
                else bounds.Encapsulate(collider.bounds);
            }
            return bounds;
        }
    }
}
