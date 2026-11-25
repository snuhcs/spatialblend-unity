// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Meta.XR.MRUtilityKit;
using Fusion;
using SpatialBlend.Samples.Objects;
using SpatialBlend.Interactions;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;
using SpatialBlend.Samples.Scanning;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using SpatialBlend.Samples;
using SpatialBlend;
using Oculus.Interaction.Surfaces;
#if UNITY_EDITOR
using UnityEditor;
#endif



namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    public class ControlPanel : MonoBehaviour
    {
        [SerializeField] private Button _connect;
        [SerializeField] private Button _rebuildNavMesh;
        [SerializeField] private Toggle _sceneAnchorVisible;
        [SerializeField] private Toggle _enableManipulationToggle;
        [SerializeField] private Toggle _libraryLoaded;
        [SerializeField] private Slider _passthroughBrightnessSlider;
        [SerializeField] private OVRPassthroughLayer _passthroughLayer;
        [SerializeField] private SceneNavigation _sceneNavigation;


        [SerializeField] private EffectMesh _effectMesh;
        [SerializeField] private FusionBootstrap _fusionBootstrap;
        [SerializeField] private FusionConnectionManager _fusionConnectionManager;
        [SerializeField, Tooltip("Optional; created on the FusionBootstrap object when empty.")]
        private FusionSessionBrowser _sessionBrowser;
        [SerializeField] private FreeformCollaborationApp _freeformApp;
        [SerializeField, Tooltip("Visible only for Foundation Local. This is deliberately not a network-session label.")]
        private GameObject _foundationLocalLabel;
        [SerializeField] private TMP_Text _connectLabel;
        [SerializeField] private Toggle _meshControllableToggle;
        [SerializeField] private bool _debugLog = false;

        [Header("Canvas")]
        [SerializeField] private GameObject _ControlPanelCanvas;
        [SerializeField] private GameObject _InteractionModeCanvas;
        [SerializeField] private SpatialBlendManager _spaceManager;

        [Header("Look")]
        [SerializeField, Tooltip("The UI typeface (Pretendard). Registered on Awake so every generated surface uses it.")]
        private UiFontSet _fontSet;

        [SerializeField, Tooltip("Show the Diagnostics block (link state, carrier, native library, NavMesh rebuild) in a release build. It always shows in a development build; the default panel for users has no developer information.")]
        private bool _showDiagnostics;

        [Header("Generated layout")]
        [SerializeField, Tooltip("Build the one-page, two-column panel over the canvas and hide the legacy rows.")]
        private bool _useGeneratedLayout = true;

        private SpatialBlendSettings _settings;
        private AvatarSettingsPresenter _avatars;
        private SceneControlsPresenter _scene;
        private ControlPanelView _view;
        private readonly List<GameObject> _hiddenLegacyRows = new();
        private string _lastStatusKey;
        private float _nextStatusPoll;
        private const float StatusPollSeconds = 0.25f;
        private bool _subscribedToConnectionState;
        private FreeformSceneLocalBackend _subscribedLocalBackend;
        private string _legacyConnectLabel;
        private SessionListPanel _sessionListPanel;

        /// <summary>Raised after every avatar behaviour was refreshed; the argument is how many there were.</summary>
        public event Action<int> AvatarsRefreshed;

        public AvatarSettingsPresenter AvatarPresenter => _avatars;
        public SceneControlsPresenter ScenePresenter => _scene;
        public ControlPanelView View => _view;
        /// <summary>
        /// Diagnostics are for us: on in a development build (and the Editor), off in a release build unless
        /// <c>_showDiagnostics</c> is set. A development build is where the team debugs, so no setting is needed there;
        /// a release build is what users get, so it stays quiet unless someone opts in for field debugging.
        /// </summary>
        public bool DiagnosticsVisible => DiagnosticsVisibleFor(Debug.isDebugBuild, _showDiagnostics);

        public static bool DiagnosticsVisibleFor(bool developmentBuild, bool explicitSetting) => developmentBuild || explicitSetting;
        public bool UsePresetAvatar => _avatars != null && _avatars.UsePreset;
        public int SelectedAvatarPreset => _avatars != null ? _avatars.PresetIndex : 0;

        private SpatialBlendSettings CurrentSettings => _settings != null ? _settings : SpatialBlendManager.CurrentSettings;

        private void EnsurePresenters()
        {
            if (_avatars != null) return;
            _avatars = new AvatarSettingsPresenter(() => CurrentSettings, RefreshAllAvatarBehaviours);
            _scene = new SceneControlsPresenter(OnSceneAnchorVisibleToggled, OnSceneManipulationToggled,
                OnPassthroughBrightnessChanged, RebuildNavMeshAndReport);
            _avatars.Changed += () => _view?.SyncAvatars(_avatars);
            _scene.Changed += () => _view?.SyncScene(_scene);
        }

        private void Awake()
        {
            UiFonts.Register(_fontSet);
            EnsurePresenters();
            // The NavMesh is baked once per actual room change, never by SceneNavigation's own triggers (SceneNavMeshBaker).
            SceneNavMeshBaker.Ensure(_sceneNavigation);
            if (_connect != null)
            {
                _connect.onClick.AddListener(OnConnectClicked);
            }

            if (_rebuildNavMesh != null)
            {
                _rebuildNavMesh.onClick.AddListener(OnRebuildNavMeshClicked);
            }

            if (_sceneAnchorVisible != null)
            {
                _sceneAnchorVisible.onValueChanged.AddListener(_scene.SetAnchorsVisible);
            }

            if (_enableManipulationToggle != null)
            {
                _enableManipulationToggle.onValueChanged.AddListener(_scene.SetManipulation);
            }

            if (_passthroughBrightnessSlider != null)
            {
                _passthroughBrightnessSlider.onValueChanged.AddListener(_scene.SetBrightness);
            }

            // Sync initial state
            if (_enableManipulationToggle != null)
            {
                _scene.SetManipulation(_enableManipulationToggle.isOn);
            }
        }

        private void Start()
        {
            EnsurePresenters();
            BuildGeneratedLayout();
            ShowCredentialNotices();
            BindSpaceManager();
            BindLocalBackend();
            RefreshConnectionUi();
            ShowPendingJoinNotice();
            // Meshes made from now on are drawn in the real-anchor colour; the toggle covers the ones that already exist.
            SceneAnchorStyle.Apply(_effectMesh, ref _anchorMaterial);
            ApplyDefaultAnchorsOff();
        }

        /// <summary>
        /// The scene anchors start OFF: the presenter, the button, the legacy toggle and the effect mesh (meshes made later too) all say
        /// the same thing from the start.
        /// </summary>
        public void ApplyDefaultAnchorsOff()
        {
            EnsurePresenters();
            if (_sceneAnchorVisible != null) _sceneAnchorVisible.SetIsOnWithoutNotify(false);
            _scene.SetAnchorsVisible(false);   // applies to the mesh and refreshes the button through Changed
        }

        /// <summary>
        /// A join that failed (the room was full, closed or gone, the join timed out, the network failed) returned the player to the lobby
        /// through the stock reset, which reloads the scene (<see cref="FusionSessionBrowser.ReportStartFailed"/>). The sentence for the user was
        /// carried across the reload; this shows it once, as a toast, on the panel that came up.
        /// </summary>
        public void ShowPendingJoinNotice()
        {
            if (_toasts == null || !JoinFailure.HasNotice) return;
            _toasts.Show(StatusTone.Bad, JoinFailure.Title, JoinFailure.Take(), 8f);
        }

        private void OnEnable()
        {
            EnsurePresenters();
            BindSpaceManager();
            BindLocalBackend();
            RefreshConnectionUi();
            SpatialBlend.Interactions.Annotation.AffordanceAnnotationMotif.RegionRefused += OnRegionRefused;
        }

        /// <summary>A refused region polygon is shown as a toast.</summary>
        private void OnRegionRefused(string message)
        {
            // Plain words for the user; the technical reason is already in the log (the motif logs it when it refuses the outline).
            if (_toasts != null) _toasts.Show(StatusTone.Bad, RegionRefusalText.Title, RegionRefusalText.ToUserMessage(message));
        }

        private bool _scanNoticeSettled;

        private void Update()
        {
            if (_view == null) return;
            // SessionManager.Initialize runs from the app's Start, after this panel's Start, so the first notice can
            // still say the server is fine before a refused connection is known. Refresh once that attempt has finished.
            if (!_scanNoticeSettled && SessionManager.Instance != null &&
                (SessionManager.Instance.IsInitialized || !string.IsNullOrEmpty(SessionManager.Instance.LastError)))
            {
                _scanNoticeSettled = true;
                ShowCredentialNotices(toast: false);
            }
            _view.TickBusy(Time.unscaledTime);
            FitInteractionSurfaces();
            if (Time.unscaledTime < _nextStatusPoll) return;
            _nextStatusPoll = Time.unscaledTime + StatusPollSeconds;
            // The advertised address arrives by RPC after the link state last changed,
            // so the status is polled rather than driven by state events alone.
            RefreshSessionStatus();
        }

        private void OnDisable()
        {
            SpatialBlend.Interactions.Annotation.AffordanceAnnotationMotif.RegionRefused -= OnRegionRefused;
            UnbindSpaceManager();
            UnbindLocalBackend();
        }

        private Material _anchorMaterial;

        private void OnDestroy()
        {
            // The tutorial's abort can still toggle the anchors, so it goes first; then the original material goes back and the copy is destroyed.
            if (_tutorial != null) _tutorial.Abort();
            if (_anchorMaterial != null)
            {
                SceneAnchorStyle.Restore(_effectMesh, _anchorMaterial);
                if (Application.isPlaying) Destroy(_anchorMaterial);
                else DestroyImmediate(_anchorMaterial);
                _anchorMaterial = null;
            }
            if (_toastOverlay != null)
            {
                if (Application.isPlaying) Destroy(_toastOverlay.gameObject);
                else DestroyImmediate(_toastOverlay.gameObject);
            }
            UnbindSpaceManager();
            UnbindLocalBackend();
        }

        /// <summary>
        /// Builds the two-column layout under the canvas that hosts the legacy rows and
        /// hides those rows. Returns false when there is no canvas or it already exists.
        /// </summary>
        public bool BuildGeneratedLayout()
        {
            if (!_useGeneratedLayout || _view != null) return false;
            EnsurePresenters();
            Canvas canvas = _connect != null ? _connect.GetComponentInParent<Canvas>(true) : null;
            if (canvas == null && _ControlPanelCanvas != null)
                canvas = _ControlPanelCanvas.GetComponentInParent<Canvas>(true);
            if (canvas == null) return false;
            var parent = (RectTransform)canvas.transform;
            if (parent.IsChildOf(transform))
            {
                ApplyHandPlacement(transform, parent);
                // On the left hand instead of the left controller when hands are the input.
                if (transform.parent != null && GetComponent<ControlPanelAnchor>() == null) gameObject.AddComponent<ControlPanelAnchor>();
            }
            // The panel is wider than the prefab's legacy canvas (two columns on one page); the canvas follows it,
            // and the Interaction SDK surfaces are fitted to the canvas rectangle below.
            parent.sizeDelta = new Vector2(UiTheme.PanelWidth, parent.sizeDelta.y);
            float width = UiTheme.PanelWidth;
            _view = ControlPanelView.Build(parent, width, new ControlPanelActions
            {
                Connect = OnConnectClicked,
                Tutorial = OnTutorialClicked,
                Leave = OnLeaveClicked,
                JoinAsObserver = OnJoinAsObserverClicked,
            }, _avatars, _scene, DiagnosticsVisible);
            // The preview (and so the mirror) lives only while the picker can be seen: not under the tutorial card, not on a hidden panel.
            _view.Preview.Allowed = () => PickerVisible;
            // The lobby preview avatar is made from the same original, non-networked prefab the local actors use.
            _view.Preview.PrefabResolver = () =>
                _freeformApp != null && _freeformApp.FoundationLocalBackend != null ? _freeformApp.FoundationLocalBackend.LocalAvatarPrefab : null;
            foreach (Transform child in parent)
            {
                if (child == _view.Root || !child.gameObject.activeSelf || !IsLegacyRow(child)) continue;
                _hiddenLegacyRows.Add(child.gameObject);
                child.gameObject.SetActive(false);
            }
            // Toasts are an overlay in front of the user, not on the wrist panel.
            _toastOverlay = UiToastOverlay.Create(_view.Root.gameObject.layer);
            _toasts = _toastOverlay.Host;
            _interactionCanvas = parent;
            FitInteractionSurfaces(true);
            _view.SetNavMeshNote(_sceneNavigation != null ? string.Empty : "NavMesh source not assigned.");
            RefreshTutorialHint();
            _lastStatusKey = null;
            return true;
        }

        /// <summary>
        /// Places the panel on the left controller from <see cref="UiTheme"/> (the one source of its offset, scale and tilt): the root sits at
        /// <see cref="UiTheme.HandPanelOffset"/>, the canvas is pivoted on its bottom edge so that point stays fixed while the content grows
        /// upward, tilted by <see cref="UiTheme.HandPanelTiltDegrees"/>, and the root's scale makes one canvas unit
        /// <see cref="UiTheme.MetresPerUnit"/> (<see cref="UiMetrics.Hand"/>) in the world whatever scale the canvas carries itself.
        /// </summary>
        public static void ApplyHandPlacement(Transform root, RectTransform canvas)
        {
            canvas.pivot = new Vector2(0.5f, 0f);
            canvas.anchoredPosition = Vector2.zero;
            canvas.localRotation = Quaternion.Euler(UiTheme.HandPanelTiltDegrees, 0f, 0f);
            float inner = canvas.localScale.x > 0f ? canvas.localScale.x : 1f;
            root.localPosition = UiTheme.HandPanelOffset;
            root.localRotation = Quaternion.identity;
            root.localScale = Vector3.one * (UiMetrics.Hand.MetresPerUnit / inner);
            // The canvas is the Hand surface (one unit is one dmm at 0.583 m); its own scale stays the prefab's, the root makes up the rest.
            var tag = canvas.GetComponent<UiSurfaceTag>();
            if (tag == null) tag = canvas.gameObject.AddComponent<UiSurfaceTag>();
            tag.Surface = UiMetrics.Hand.Name;
        }

        /// <summary>
        /// A legacy row is a visual row of the old layout: something that draws or reacts (a uGUI <see cref="Graphic"/> or
        /// <see cref="Selectable"/>) anywhere below the child. The canvas also hosts the Interaction SDK's canvas interaction
        /// objects (the <c>PointableCanvas</c>, the ray and poke interactables and their surfaces), which draw nothing:
        /// they are how controller rays and pokes reach ANY control of the canvas, so they are never hidden.
        /// </summary>
        public static bool IsLegacyRow(Transform child) =>
            child.GetComponentInChildren<Graphic>(true) != null || child.GetComponentInChildren<Selectable>(true) != null;

        private RectTransform _interactionCanvas;
        private UiToastHost _toasts;
        private UiToastOverlay _toastOverlay;
        private SessionStatusView _lastStatus;
        private bool _hasLastStatus;
        private Vector2 _fittedSize = new Vector2(float.NaN, float.NaN);

        /// <summary>
        /// Sizes the Interaction SDK's clipped surfaces (their <see cref="BoundsClipper"/>s were authored for the legacy layout's
        /// rectangle) to the canvas rectangle as it is now, so a ray or poke hits every control of the generated panel and nothing
        /// outside it. The canvas grows and shrinks with its content (a status line wraps), so this runs whenever the rectangle changes.
        /// Returns the number of clippers fitted.
        /// </summary>
        public int FitInteractionSurfaces(bool force = false)
        {
            if (_interactionCanvas == null) return 0;
            Vector2 size = _interactionCanvas.rect.size;
            if (!force && size == _fittedSize) return 0;
            int fitted = 0;
            foreach (BoundsClipper clipper in _interactionCanvas.GetComponentsInChildren<BoundsClipper>(true))
            {
                Rect rect = clipper.transform is RectTransform clipperRect ? clipperRect.rect : _interactionCanvas.rect;
                clipper.Position = new Vector3(rect.center.x, rect.center.y, clipper.Position.z);
                clipper.Size = new Vector3(rect.width, rect.height, clipper.Size.z);
                ++fitted;
            }
            _fittedSize = size;
            return fitted;
        }

        /// <summary>
        /// "Join as observer" (desktop lobby): starts the desktop spectator app, with the room when the lobby lists exactly one.
        /// The spectator is a separate client (SpectatorAppLauncher); this player stays in the lobby.
        /// </summary>
        private void OnJoinAsObserverClicked()
        {
            string room = SpectatorAppLauncher.RoomToPass(_sessionBrowser != null ? _sessionBrowser.Sessions : null);
            if (SpectatorAppLauncher.TryLaunch(room, out var message)) return;
            Debug.LogWarning("[ControlPanel] " + message);
            if (_toasts != null) _toasts.Show(StatusTone.Bad, "Join as observer", message);
        }

        private TutorialController _tutorial;
        private bool _tutorialActive;

        /// <summary>True while the lobby tutorial runs. The panel's own actions are inert then: the tutorial owns the screen and creates no room.</summary>
        public bool TutorialActive => _tutorialActive;

        /// <summary>The preset picker can be seen: the panel is shown (not covered by the tutorial, not alpha 0) and active.</summary>
        public bool PickerVisible
        {
            get
            {
                if (_tutorialActive || _view == null || _view.Root == null || !_view.Root.gameObject.activeInHierarchy) return false;
                var group = _view.Root.GetComponent<CanvasGroup>();
                return group == null || group.alpha > 0.01f;
            }
        }
        public TutorialController Tutorial => _tutorial;
        /// <summary>The canvas the panel and the tutorial card live on (the card is an overlay on it); null before the generated layout exists.</summary>
        public RectTransform PanelCanvas => _interactionCanvas;

        /// <summary>The lobby's Tutorial button: starts the tutorial from the lobby, before connecting.</summary>
        private void OnTutorialClicked()
        {
            if (_tutorialActive || _tutorial != null) return;
            if (_spaceManager != null && _spaceManager.ConnectionState == SpatialBlendConnectionState.Connected) return;
            _tutorial = TutorialController.Begin(this);
            if (_tutorial == null) Debug.LogWarning("[ControlPanel] The tutorial could not start (no panel canvas yet).");
        }

        /// <summary>Called by the tutorial: it starts and ends its run. While it runs the lobby panel is covered by the card, hidden and inert; at the end it comes back and the hint dot is redrawn from the done mark.</summary>
        public void SetTutorialActive(bool active)
        {
            _tutorialActive = active;
            if (!active) _tutorial = null;
            if (_view == null || _view.Root == null) return;   // the panel's own teardown can destroy the root first
            var group = _view.Root.GetComponent<CanvasGroup>();
            if (group == null) group = _view.Root.gameObject.AddComponent<CanvasGroup>();
            // Hidden and inert, but still laid out, so the canvas keeps its size under the tutorial card.
            group.alpha = active ? 0f : 1f;
            group.blocksRaycasts = !active;
            group.interactable = !active;
            if (!active) RefreshTutorialHint();
        }

        /// <summary>The hint dot on the Tutorial button shows until the tutorial was gone through once (the mark is local to the device).</summary>
        public void RefreshTutorialHint()
        {
            if (_view != null) _view.SetTutorialHint(!TutorialDoneMark.Shared.IsDone);
        }

        /// <summary>
        /// Says what is off in this build: a notice line on the panel for every required ID or credential the build lacks, and a
        /// neutral line when scanning is off that says why (no server, camera permission, or unreachable). One longer-lived toast per
        /// missing required credential. The same credential list is logged at
        /// startup (<see cref="RequiredCredentialsRuntime"/>). Returns the notices shown. Pass <paramref name="toast"/> false to refresh
        /// the lines without repeating those toasts.
        /// </summary>
        public IReadOnlyList<CredentialNotice> ShowCredentialNotices(bool toast = true)
        {
            if (_view == null) return Array.Empty<CredentialNotice>();
            var settings = CurrentSettings;
            var local = LocalSessionConfig.Load(Application.persistentDataPath, null);
            bool scanRunning = SessionManager.Instance != null && SessionManager.Instance.IsInitialized;
            string lastError = SessionManager.Instance != null ? SessionManager.Instance.LastError : null;
            bool configured = CredentialNoticePresenter.ScanServerConfigured(settings, local, scanRunning);
            bool cameraDenied = PassthroughCameraPermissions.HasCameraPermission == false;
            var reason = CredentialNoticePresenter.Classify(configured, scanRunning, lastError, cameraDenied);
            var notices = CredentialNoticePresenter.Describe(RequiredCredentialsRuntime.ProfileFor(Application.platform),
                RequiredCredentialsRuntime.Read(), reason, lastError);
            _view.SetNotices(notices);
            if (!toast) return notices;
            foreach (var notice in notices)
            {
                if (notice.Tone == StatusTone.Bad && _toasts != null) _toasts.Show(notice.Tone, notice.Title, notice.Message, CredentialToastSeconds);
            }
            return notices;
        }

        /// <summary>A config file pushed while the panel was open (or after a reinstall) is read again when the app is focused.</summary>
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _view != null) ShowCredentialNotices(toast: false);
        }

        private const float CredentialToastSeconds = 10f;

        /// <summary>A short message in front of the user (the same overlay the panel's own toasts use).</summary>
        public void ShowToast(StatusTone tone, string title, string message)
        {
            if (_toasts != null) _toasts.Show(tone, title, message);
        }

        /// <summary>
        /// Shows or hides the interaction mode panel. Before this the panel appeared only after connecting; the tutorial can show it
        /// in the lobby. (The mode-switch chip replaces this panel.)
        /// </summary>
        public void SetModePanelVisible(bool visible)
        {
            if (_InteractionModeCanvas != null) _InteractionModeCanvas.SetActive(visible);
        }

        public bool ModePanelVisible => _InteractionModeCanvas != null && _InteractionModeCanvas.activeSelf;

        /// <summary>
        /// Opens the real room list read-only: the rooms are read from the lobby, but a tap on a room or on New room goes to the given
        /// handlers and joins or creates nothing. Returns false when the list cannot be opened (no network stack, or a session is
        /// already starting); the tutorial then shows sample rooms.
        /// </summary>
        public bool ShowRoomListReadOnly(Action<string> onRoomTapped, Action onNewRoomTapped)
        {
            if (_view == null || _fusionBootstrap == null) return false;
            if (_fusionBootstrap.CurrentStage != FusionBootstrap.Stage.Disconnected) return false;
            if (!EnsureSessionListPanel(out SessionListPanel panel)) return false;
            panel.SetReadOnly(onRoomTapped, onNewRoomTapped);
            panel.Show();
            return true;
        }

        /// <summary>Closes the room list and gives it its normal behaviour back.</summary>
        public void HideRoomList()
        {
            if (_sessionListPanel == null) return;
            _sessionListPanel.SetReadOnly(null, null);
            _sessionListPanel.Hide();
        }

        /// <summary>
        /// Leaves the room with the paths the app already has. The matchmaker's own-leave decision tears the core link down
        /// without reporting a lost host (<see cref="FusionSessionBrowser.LeaveSession"/>, or
        /// <see cref="FusionConnectionManager.AdoptMatchmakingDecision"/> directly when there is no browser). Then
        /// <c>FusionBootstrap.ShutdownAll</c> runs. That is Photon's stock reset: it shuts every runner down, reloads the
        /// initial scene and destroys the bootstrap, so Leave is a full return to the lobby (the panel is rebuilt there, and
        /// nothing after the call touches the old panel). That behaviour still needs a device check.
        /// </summary>
        public void OnLeaveClicked()
        {
            if (_tutorialActive) return;
            var browser = _sessionBrowser != null ? _sessionBrowser
                : _fusionBootstrap != null ? _fusionBootstrap.GetComponent<FusionSessionBrowser>() : null;
            // The browser's leave already tears the core link down when it had the player in a session; otherwise do it here.
            bool browserLeft = browser != null && browser.InSession;
            if (browser != null) browser.LeaveSession();
            if (!browserLeft && _fusionConnectionManager != null)
                _fusionConnectionManager.AdoptMatchmakingDecision(new MatchmakingDecision(true, MatchmakingPhase.Browsing,
                    CoreParticipantRole.None, null, null, null));
            if (_fusionBootstrap != null) _fusionBootstrap.ShutdownAll();
            else RefreshConnectionUi();
        }

        private string RoomName(out int people)
        {
            people = 0;
            var runner = _fusionConnectionManager != null ? _fusionConnectionManager.Runner : null;
            if (runner == null || !runner.IsRunning || !runner.SessionInfo.IsValid) return null;
            people = runner.SessionInfo.PlayerCount;
            return runner.SessionInfo.Name;
        }

        /// <summary>Copies the live connection facts into the session section.</summary>
        public SessionStatusView RefreshSessionStatus()
        {
            var link = _fusionConnectionManager != null
                ? _fusionConnectionManager.CoreLinkState
                : _spaceManager != null ? _spaceManager.ConnectionState : SpatialBlendConnectionState.Disconnected;
            var input = new SessionStatusInput
            {
                LinkState = link,
                Role = _fusionConnectionManager != null ? _fusionConnectionManager.ParticipantRole : CoreParticipantRole.None,
                CarrierText = _fusionConnectionManager != null ? _fusionConnectionManager.CarrierText : string.Empty,
                LibraryCompatible = _spaceManager != null && _spaceManager.NativeLibraryCompatible,
                RoomName = RoomName(out int people),
                PeopleCount = people,
                CanRetry = _fusionConnectionManager != null && _fusionConnectionManager.CanRetry,
                HostLeft = _fusionConnectionManager != null && _fusionConnectionManager.HostLeft,
                LocalMode = UsesFoundationLocal(),
                LocalRunning = UsesFoundationLocal() && _freeformApp.FoundationLocalBackend != null &&
                               _freeformApp.FoundationLocalBackend.IsRunning,
            };
            SessionStatusView status = SessionStatusPresenter.Build(input);
            string key = string.Join("|", status.Headline, status.Detail, status.LinkStateText, status.CarrierText,
                status.LibraryText, status.ConnectLabel, status.ConnectEnabled, status.Busy, status.Tone);
            if (_view != null && key != _lastStatusKey)
            {
                _lastStatusKey = key;
                _view.ApplySession(status);
                // The technical state goes to the log (and the Diagnostics block), not the user's panel.
                Debug.Log("[ControlPanel] " + status.Headline + " | " + status.DiagnosticsLine);
                var toast = SessionToastPresenter.Describe(_hasLastStatus, _lastStatus, status);
                if (toast.Show && _toasts != null) _toasts.Show(toast.Tone, toast.Title, toast.Message);
                _lastStatus = status;
                _hasLastStatus = true;
            }
            return status;
        }

        private void OnConnectClicked()
        {
            // The tutorial never connects: it reads the room list itself, read-only, and simulates the rest.
            if (_tutorialActive) return;
            // After the host left, the runner may still be up, so a new room cannot start until it is shut down: the primary
            // action ("Back to the lobby") is a leave.
            if (_fusionConnectionManager != null && _fusionConnectionManager.HostLeft && !UsesFoundationLocal())
            {
                OnLeaveClicked();
                return;
            }
            if (UsesFoundationLocal())
            {
                if (!_freeformApp.TryStartFoundationLocal(out string localError) && _debugLog)
                    Debug.LogWarning("[ControlPanel] Foundation Local did not start: " + localError);
                RefreshConnectionUi();
                return;
            }
            BindSpaceManager();
            if (_fusionConnectionManager != null && _fusionConnectionManager.CanRetry)
            {
                _fusionConnectionManager.RetryConnection();
                RefreshConnectionUi();
                return;
            }
            if (_fusionBootstrap != null)
            {
                if (!TryShowSessionList())
                    _fusionBootstrap.StartSharedClient();
            }
            else if (_debugLog)
            {
                Debug.LogWarning("[ControlPanel] FusionBootstrap reference is missing");
            }

            // Fusion startup is not SpatialBlend readiness. Keep the retry UI visible
            // until the manager reports a successful connection.
            RefreshConnectionUi();
        }

        // Before Fusion starts, Connect opens the room list; a chosen or new
        // room then starts the same bootstrap under that session name.
        // The session section shows CoreLinkState from FusionConnectionManager. No address is shown: the
        // core link rides the Photon session, so a room is found and joined by its name only.
        private bool TryShowSessionList()
        {
            if (_fusionBootstrap.CurrentStage != FusionBootstrap.Stage.Disconnected) return false;
            if (!EnsureSessionListPanel(out SessionListPanel panel)) return false;
            panel.Show();
            return true;
        }

        private bool EnsureSessionListPanel(out SessionListPanel panel)
        {
            panel = null;
            Button template = _view != null ? _view.Connect : _connect;
            if (template == null || _fusionBootstrap == null) return false;
            if (_sessionBrowser == null)
            {
                _sessionBrowser = _fusionBootstrap.GetComponent<FusionSessionBrowser>();
                if (_sessionBrowser == null)
                    _sessionBrowser = _fusionBootstrap.gameObject.AddComponent<FusionSessionBrowser>();
            }
            _sessionBrowser.Configure(_fusionBootstrap, _fusionConnectionManager);
            if (_sessionListPanel == null)
                _sessionListPanel = SessionListPanel.Create(template, _sessionBrowser);
            panel = _sessionListPanel;
            return true;
        }

        private void OnRebuildNavMeshClicked()
        {
            _scene.RebuildNavMesh();
        }

        private void RebuildNavMeshAndReport()
        {
            bool rebuilt = RebuildNavMesh();
            _view?.SetNavMeshNote(rebuilt ? "NavMesh rebuilt." : "NavMesh source not assigned.");
        }

        private bool RebuildNavMesh()
        {
            if (_sceneNavigation != null)
            {
                SceneNavMeshBaker.Ensure(_sceneNavigation).BakeNow("Rebuild NavMesh button");
				// _navMeshVisualizer?.Rebuild(); // optional immediate refresh
                NavMeshExporter.TrySaveCurrentNavMeshObj(out _);
            }
            else
            {
                if (_debugLog) Debug.LogWarning("[ControlPanel] SceneNavigation reference is missing");
                return false;
            }
            return true;
        }

        // Avatar display mode and fade are read by every avatar behaviour, so each
        // change tells all of them to re-evaluate their visibility.
        private void RefreshAllAvatarBehaviours()
        {
            var behaviours = FindObjectsByType<SbAvatarBehaviourFusion>(FindObjectsSortMode.None);
            foreach (var behaviour in behaviours)
            {
                behaviour.RefreshAvatarDisplayMode();
            }
            AvatarsRefreshed?.Invoke(behaviours.Length);
        }

        private void OnSceneAnchorVisibleToggled(bool isOn)
        {
            if (_effectMesh != null)
            {
                // Hide or show the scene-anchor meshes themselves (as v1); swapping a
                // material left a mesh in the scene while connected.
                // HideMesh also decides whether meshes made LATER (the room loads after the panel) start hidden: without it the
                // anchors drew while the button said off.
                _effectMesh.HideMesh = !isOn;
                _effectMesh.ToggleEffectMeshVisibility(isOn);
                // Real furniture has its own outline colour (UiTheme RealAnchor), also for meshes made after this call.
                if (isOn) SceneAnchorStyle.Apply(_effectMesh, ref _anchorMaterial);
            }
        }

        private void OnSceneManipulationToggled(bool isOn)
        {
            var modeManager = InteractionModeManager.Instance;
            if (modeManager != null)
            {
                modeManager.SetMode(isOn ? InteractionMode.SceneEdit : InteractionMode.Interaction);
            }
        }

        private void OnPassthroughBrightnessChanged(float brightness)
        {
            if (_passthroughLayer != null)
            {
                _passthroughLayer.SetBrightnessContrastSaturation(brightness);
            }
            else
            {
                if (_debugLog) Debug.LogWarning("[ControlPanel] OVRPassthroughLayer reference is missing");
            }
            // Extend passthrough adjustments here if needed (contrast/saturation)
        }

        private void ApplySceneAndMeshToggles(bool enabled, bool touchCanvas = true)
        {
            if (touchCanvas)
            {
                if (enabled) ShowUICanvas();
                else HideUICanvas();
            }
            
            if (_meshControllableToggle != null)
            {
                _meshControllableToggle.SetIsOnWithoutNotify(enabled);
            }
            OnMeshControllableToggled(enabled);

            if (_sceneAnchorVisible != null)
            {
                _sceneAnchorVisible.SetIsOnWithoutNotify(enabled);
            }
            OnSceneAnchorVisibleToggled(enabled);
            _scene?.Mirror(enabled, _scene.ManipulationEnabled);
        }

        private void OnMeshControllableToggled(bool isOn)
        {
            // Placeholder for future mesh controllability logic.
            // Currently no-op to keep behavior unchanged.
        }

        private void EnterReadySessionUi()
        {
            // The control panel stays visible in a room. It is attached to the left controller,
            // so it does not block the view, and it carries the room headline and Leave room.
            ApplySceneAndMeshToggles(false, false);
            OnSceneManipulationToggled(false);
            _scene?.Mirror(false, false);
            // Modes matter once the session is ready: the hover prompt, the idle return and the mode chip start here. The chip replaces
            // the mode panel's mode row, so the panel stays hidden while the chip is present.
            var modeSwitch = ModeSwitchAssist.Instance;
            bool chipReplacesPanel = modeSwitch != null && modeSwitch.ReplacesModePanel;
            if (_InteractionModeCanvas != null)
            {
                _InteractionModeCanvas.SetActive(!chipReplacesPanel);
            }
            // The lobby was Editing. A room starts in Interaction, before the assist is on, so this makes no cue.
            if (InteractionModeManager.Instance != null)
                InteractionModeManager.Instance.SetMode(InteractionMode.Interaction);
            if (modeSwitch != null) modeSwitch.SetActive(true);
            _roomMode.EnteredRoom();
        }

        // The lobby is Editing. A room may end in either mode, so coming back puts Editing again, once.
        private readonly RoomModeReset _roomMode = new RoomModeReset();

        private void ShowRetryUi()
        {
            ShowUICanvas();
            if (_InteractionModeCanvas != null)
            {
                _InteractionModeCanvas.SetActive(false);
            }
            var modeSwitchOff = ModeSwitchAssist.Instance;
            if (modeSwitchOff != null) modeSwitchOff.SetActive(false);
            // After the assist is off, so putting the mode back makes no cue. The lobby tutorial sets the mode itself and keeps it.
            if (!_tutorialActive) _roomMode.Apply(InteractionModeManager.Instance);
        }

        private void BindSpaceManager()
        {
            if (_spaceManager == null)
            {
                _spaceManager = FindFirstObjectByType<SpatialBlendManager>();
            }
            if (_spaceManager != null && !_subscribedToConnectionState)
            {
                _spaceManager.ConnectionStateChanged += OnConnectionStateChanged;
                _subscribedToConnectionState = true;
            }
        }

        private void UnbindSpaceManager()
        {
            if (_spaceManager != null && _subscribedToConnectionState)
            {
                _spaceManager.ConnectionStateChanged -= OnConnectionStateChanged;
            }
            _subscribedToConnectionState = false;
        }

        private void OnConnectionStateChanged(SpatialBlendConnectionState state)
        {
            RefreshConnectionUi();
        }

        private void RefreshConnectionUi()
        {
            RefreshConnectionUiCore();
            RefreshSessionStatus();
        }

        private void RefreshConnectionUiCore()
        {
            _view?.SyncAvatars(_avatars);
            if (UsesFoundationLocal())
            {
                SetConnectLabel(_freeformApp.FoundationLocalBackend != null &&
                    _freeformApp.FoundationLocalBackend.IsRunning
                    ? "Local Content Running" : "Start Local Content");
                if (_foundationLocalLabel != null)
                    _foundationLocalLabel.SetActive(true);
                if (_freeformApp.FoundationLocalBackend != null &&
                    _freeformApp.FoundationLocalBackend.IsRunning)
                {
                    EnterReadySessionUi();
                    return;
                }
                ShowRetryUi();
                return;
            }
            if (_foundationLocalLabel != null)
                _foundationLocalLabel.SetActive(false);
            RestoreLegacyConnectLabel();
            if (_libraryLoaded != null)
            {
                _libraryLoaded.SetIsOnWithoutNotify(_spaceManager != null &&
                    _spaceManager.NativeLibraryCompatible);
            }
            if (_spaceManager != null &&
                _spaceManager.ConnectionState == SpatialBlendConnectionState.Connected &&
                _spaceManager.Initialized && _spaceManager.NativeLibraryCompatible)
            {
                EnterReadySessionUi();
                return;
            }

            // Disconnected, connecting, and failed are all recoverable UI states.
            // In particular, failure must not strand users behind a hidden canvas.
            ShowRetryUi();
        }

        private bool UsesFoundationLocal()
        {
            return _freeformApp != null && _freeformApp.UsesFoundationLocal;
        }

        private void BindLocalBackend()
        {
            FreeformSceneLocalBackend candidate = _freeformApp == null ? null :
                _freeformApp.FoundationLocalBackend;
            if (candidate == _subscribedLocalBackend)
                return;
            UnbindLocalBackend();
            _subscribedLocalBackend = candidate;
            if (_subscribedLocalBackend != null)
                _subscribedLocalBackend.StateChanged += OnLocalBackendStateChanged;
        }

        private void UnbindLocalBackend()
        {
            if (_subscribedLocalBackend != null)
                _subscribedLocalBackend.StateChanged -= OnLocalBackendStateChanged;
            _subscribedLocalBackend = null;
        }

        private void OnLocalBackendStateChanged()
        {
            RefreshConnectionUi();
        }

        private void SetConnectLabel(string value)
        {
            if (_connectLabel == null && _connect != null)
                _connectLabel = _connect.GetComponentInChildren<TMP_Text>(true);
            if (_connectLabel == null)
                return;
            _legacyConnectLabel ??= _connectLabel.text;
            _connectLabel.text = value + " (no Internet session)";
        }

        private void RestoreLegacyConnectLabel()
        {
            if (_connectLabel != null && _legacyConnectLabel != null)
                _connectLabel.text = _legacyConnectLabel;
        }

        private void HideUICanvas()
        {
            if (_ControlPanelCanvas != null)
            {
                _ControlPanelCanvas.SetActive(false);
            }
        }

        private void ShowUICanvas()
        {
            if (_ControlPanelCanvas != null)
            {
                _ControlPanelCanvas.SetActive(true);
            }
        }

        public void LoadGltfModel()
        {
            // TODO: Get the object name from the Classification Model
            var loader = GltfLoader.EnsureInstance();
            if (loader == null)
            {
                if (_debugLog) Debug.LogError("[ControlPanel] GltfLoader is not available.");
                return;
            }
            loader.LoadModelFromName("reconstruction.glb", "Chair");
        }

#if UNITY_EDITOR
        public void ShowSceneAnchorInEditor()
        {
            OnSceneAnchorVisibleToggled(true);
        }

        public void HideSceneAnchorInEditor()
        {
            OnSceneAnchorVisibleToggled(false);
        }
#endif
    }
}
