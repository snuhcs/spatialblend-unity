// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using SpatialBlend;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.Scanning;
using System;
using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;
using Unity.AI.Navigation;
using Meta.XR.MRUtilityKit;
using SpatialBlend.Motion;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// How the Freeform app runs. Fusion is the shipping mode (Photon Fusion rooms, every player build); FoundationLocal runs
    /// the authored Whiteboard and Chess content on the local core with no Internet session (an Editor-only mode). The values
    /// are serialized in the scene and stored in EditorPrefs: keep them.
    /// </summary>
    public enum FreeformCollaborationRuntimeProfile
    {
        Fusion = 0,
        FoundationLocal = 1
    }

    public partial class FreeformCollaborationApp : SbApp
    {
        [Header("Runtime Profile")]
        [SerializeField]
        [Tooltip("Foundation Local uses only the authored local Whiteboard and Chess content; it does not create an Internet session.")]
        private FreeformCollaborationRuntimeProfile _runtimeProfile =
            FreeformCollaborationRuntimeProfile.Fusion;
        [SerializeField] private FreeformCollaboration.FreeformSceneLocalBackend _foundationLocalBackend;
        [SerializeField] private FreeformCollaboration.FreeformSceneFoundationLocalGate _foundationLocalGate;
        [SerializeField]
        [Tooltip("MRUK SceneLabels to auto-share from the current room. Only the first matching anchor per label will be used.")]
        private MRUKAnchor.SceneLabels _sharedLabels;
        
        [SerializeField]
        private GameObject _userAvatar;

        private bool _desktopObserver;

        /// <summary>
        /// This run joined as a desktop observer: no local avatar, no actor, no content writes. Set from the
        /// platform and XR state in <see cref="Start"/>, before the app registers with the manager.
        /// </summary>
        public bool IsDesktopObserver => _desktopObserver;

        /// <summary>True after <see cref="Start"/> has decided the role, so avatar spawn can wait for it.</summary>
        public bool ObserverRoleResolved { get; private set; }

        [SerializeField]
        private SceneNavigation _sceneNavigation;

        [SerializeField]
        private SpatialBlend.Samples.FreeformCollaboration.UI.ControlPanel _controlPanel;

        [Header("SpatialBlend")]
        [SerializeField]
        [Tooltip("Attach the non-generative per-anchor goal/event seam to received avatars. " +
                 "It does not replace tracked Meta Avatar ownership or provide IK by itself.")]
        private bool _enableAnchorTargetController = true;

        private readonly List<GameObject> _remoteAvatars = new List<GameObject>();
        private readonly HashSet<GameObject> _remoteObjects = new HashSet<GameObject>();
        private readonly HashSet<GameObject> _notifiedRemoteAvatars = new HashSet<GameObject>();
        private readonly HashSet<AnchorTargetController> _ownedAnchorTargetControllers =
            new HashSet<AnchorTargetController>();
        private readonly HashSet<SbRetargetAnchorTargetBridge> _ownedRetargetAnchorBridges =
            new HashSet<SbRetargetAnchorTargetBridge>();
        private readonly NativeEntityReadyToShareTracker<SbNativeEntity> _nativeShareTracker =
            new NativeEntityReadyToShareTracker<SbNativeEntity>();
        private Coroutine _registerAvatarCoroutine;
        private bool _navigationListenerRegistered;
        private bool _appStarted;
        private bool _avatarRegistrationComplete;
        private bool _isDestroying;
        private MRUK _sceneLoadedCallbackOwner;
        private bool _connectionStateListenerRegistered;
        private readonly MRUKSceneGeometryWatcher _sceneGeometryWatcher = new MRUKSceneGeometryWatcher();
        private ISceneAnchorCatalog _sceneAnchorCatalog;
        private ISceneAnchorPublisher _sceneAnchorPublisher;
        private readonly MrukSceneAnchorCatalog _mrukSceneAnchors = new MrukSceneAnchorCatalog();

        [Header("Remote Arrival SFX")]
        [SerializeField] private AudioClip _remoteAvatarClip;
        [SerializeField] private AudioClip _remoteObjectClip;
        [SerializeField, Range(0f, 1f)] private float _arrivalSfxVolume = 1f;

        [Serializable]
        struct SharedObject
        {
            public GameObject gameObject;
            public string tag; // Use tag as a classifer
        }

        [SerializeField]
        private List<SharedObject> _sharedObjects = new List<SharedObject>();
        private readonly ArrivalNarration _arrivals = new ArrivalNarration();
        private AudioSource _narration;

        public static event Action<GameObject, PlayerRef> OnRemoteAvatarAdded;
        public static event Action<GameObject> OnRemoteObjectAdded;

        private void Awake()
        {
            if (_remoteAvatarClip == null) _remoteAvatarClip = Resources.Load<AudioClip>("SFX/RemoteAvatarArrived");
            if (_remoteObjectClip == null) _remoteObjectClip = Resources.Load<AudioClip>("SFX/RemoteObjectArrived");
            if (_foundationLocalGate != null) _foundationLocalGate.SuppressFoundationLocalTransport();
            // Before SceneNavigation's Start: its own bake triggers (every scene load and every room created or updated) are
            // replaced by one bake per actual room change.
            SceneNavMeshBaker.Ensure(_sceneNavigation);
        }

        private void OnEnable()
        {
            if (UsesFoundationLocal)
            {
                // This explicit profile has no transport prerequisite. A failed
                // local start remains visible to the ControlPanel retry action;
                // it never falls through to legacy registration or Fusion.
                if (_foundationLocalGate != null &&
                    _foundationLocalGate.SuppressFoundationLocalTransport())
                    TryStartFoundationLocal(out _);
                return;
            }
            EnforceSingleContentWriter();
            RegisterConnectionStateListener();
            RegisterMRUKSceneLoadedCallback();
            StartAvatarRegistrationIfNeeded();
            TryRegisterCurrentRoomGeometry();
            // ContentReady listeners are deliberately removed while disabled.
            // Re-scan only once this app has passed its manager start gate, so
            // bytes that arrived while disabled are registered on re-enable.
            if (CanRegisterNativeContent())
            {
                RegisterNativeEntities();
            }
        }

        private void OnDisable()
        {
            if (UsesFoundationLocal)
            {
                if (_foundationLocalBackend != null) _foundationLocalBackend.Stop();
                return;
            }
            UnregisterConnectionStateListener();
            UnregisterMRUKSceneLoadedCallback();
            _sceneGeometryWatcher.Dispose();
            StopAvatarRegistration();
            ClearPendingNativeEntities();
        }

        private void OnDestroy()
        {
            if (UsesFoundationLocal)
            {
                if (_foundationLocalBackend != null) _foundationLocalBackend.Stop();
                return;
            }
            _isDestroying = true;
            RemoveBoxBusyProbe();
            UnregisterConnectionStateListener();
            UnregisterMRUKSceneLoadedCallback();
            _sceneGeometryWatcher.Dispose();
            StopAvatarRegistration();
            var ownedControllers = new List<AnchorTargetController>(_ownedAnchorTargetControllers);
            _ownedAnchorTargetControllers.Clear();
            foreach (var controller in ownedControllers)
            {
                if (controller != null)
                {
                    controller.ReleaseAllTargets();
                    controller.enabled = false;
                }
            }
            DisableOwnedRetargetAnchorBridges(true);
            _remoteAvatars.Clear();
            _remoteObjects.Clear();
            _notifiedRemoteAvatars.Clear();
            ClearPendingNativeEntities();
            if (_spaceManager != null)
            {
                base.OnDestroy();
            }
        }

        public void SetUserAvatar(GameObject avatar)
        {
            _userAvatar = avatar;
        }

        // Authored objects are fitted against the room's furniture as well as its walkable ring: they wait for the MRUK
        // scene geometry (TryRegisterCurrentRoomGeometry marks it registered). A desktop observer has no room and never
        // registers geometry (TryRegisterCurrentRoomGeometry returns at once), so it must not wait for it.
        protected override bool WaitsForLocalSceneGeometry => !UsesFoundationLocal && !IsDesktopObserver;

        /// <summary>
        /// Records the startup role. Tests pass the decision directly. Production calls it from <see cref="Start"/>
        /// with <see cref="DesktopObserverDecision.FromRuntime"/>.
        /// </summary>
        public void ApplyRole(bool isObserver)
        {
            _desktopObserver = isObserver;
            ObserverRoleResolved = true;
            DesktopObserverRuntime.Ensure(this);
            FusionConnectionManager connection = FindFirstObjectByType<FusionConnectionManager>();
            if (connection != null) connection.DeclareLocalObserver(isObserver);
            FusionSessionBrowser browser = FindFirstObjectByType<FusionSessionBrowser>();
            if (browser != null) browser.SetHostingAllowed(!isObserver);
        }

        public override void Start()
        {
            // Do not register this SbApp with the manager in local mode. In
            // particular, that keeps its automatic app-start path from opening
            // transport-backed Freeform registration before the local backend.
            if (UsesFoundationLocal)
            {
                ObserverRoleResolved = true;
                return;
            }
            ApplyRole(DesktopObserverDecision.FromRuntime(Application.platform, DesktopObserverXr.IsActive()));
            base.Start();
            if (IsDesktopObserver)
            {
                return;
            }
            // The settings default names the public lab server over TLS. A device spatialblend-local.json can replace it. An empty address sends nothing.
            SessionManager scan = SessionManager.EnsureInstance();
            if (scan.ResultSink == null) scan.ResultSink = new SpatialBlend.Samples.Scanning.GltfScanResultSink();
            scan.Initialize();
            // Save was removed: its library is deleted once (best effort; nothing reads it).
            SpatialBlend.Samples.Scanning.LegacySavedObjects.DeleteIfPresent(Application.persistentDataPath);
        }

        /// <summary>
        /// T3.5: a run has exactly one content writer. In Fusion mode the Fusion
        /// motifs write Chess and Whiteboard, so a local backend that is somehow
        /// running is stopped, which also restores the motifs it suppressed.
        /// </summary>
        private void EnforceSingleContentWriter()
        {
            if (FreeformContentWriterPolicy.AllowsLocalBackendToBind(UsesFoundationLocal))
                return;
            if (_foundationLocalBackend != null && _foundationLocalBackend.IsRunning)
            {
                Debug.LogError("[FreeformCollaborationApp] Fusion mode forbids the Foundation Local " +
                               "content backend. Stopping it so the Fusion motifs stay the only writer.");
                _foundationLocalBackend.Stop();
            }
        }

        public override bool IsAppStarted => _appStarted;

        /// <summary>EditorPrefs key the Editor profile menu writes (FreeformProfileMenu).</summary>
        public const string EditorProfilePrefKey = "SpatialBlend.Freeform.EditorRuntimeProfile";

        /// <summary>
        /// The profile this run uses. A player build always runs Fusion mode. The Editor
        /// uses the scene's serialized profile (FoundationLocal), unless the Editor profile
        /// menu has stored an override.
        /// </summary>
        public bool UsesFoundationLocal =>
            RuntimeProfile == FreeformCollaborationRuntimeProfile.FoundationLocal;

        public FreeformCollaborationRuntimeProfile RuntimeProfile
        {
            get
            {
#if UNITY_EDITOR
                int stored = UnityEditor.EditorPrefs.GetInt(EditorProfilePrefKey, -1);
                return ResolveProfile(_runtimeProfile, true, stored);
#else
                return ResolveProfile(_runtimeProfile, false, -1);
#endif
            }
        }

        /// <summary>
        /// Pure profile resolution. A player build is Fusion whatever the scene serializes.
        /// In the Editor a valid stored override wins; otherwise the serialized profile.
        /// </summary>
        public static FreeformCollaborationRuntimeProfile ResolveProfile(
            FreeformCollaborationRuntimeProfile serialized, bool isEditor, int editorOverride)
        {
            if (!isEditor)
                return FreeformCollaborationRuntimeProfile.Fusion;
            if (Enum.IsDefined(typeof(FreeformCollaborationRuntimeProfile), editorOverride))
                return (FreeformCollaborationRuntimeProfile)editorOverride;
            return serialized;
        }

        public FreeformCollaboration.FreeformSceneLocalBackend FoundationLocalBackend =>
            _foundationLocalBackend;
        public FreeformCollaboration.FreeformSceneFoundationLocalGate FoundationLocalGate =>
            _foundationLocalGate;

        public bool TryStartFoundationLocal(out string error)
        {
            if (!TryGetFoundationLocalBackend(out var backend, out error))
                return false;
            if (_foundationLocalGate == null ||
                !_foundationLocalGate.SuppressFoundationLocalTransport())
            {
                error = "Foundation Local requires the scene transport suppression gate.";
                return false;
            }
            return backend.TryStart(out error);
        }

        private bool TryGetFoundationLocalBackend(
            out FreeformCollaboration.FreeformSceneLocalBackend backend, out string error)
        {
            backend = null;
            if (!UsesFoundationLocal)
            {
                error = "The Freeform app is configured for the Fusion profile.";
                return false;
            }
            if (_foundationLocalBackend == null)
            {
                error = "Foundation Local is selected but its scene backend is not wired.";
                return false;
            }
            backend = _foundationLocalBackend;
            // The actors' navigable root is the room's walkable: this app's NavMesh, the one the Fusion path registers.
            backend.SetFloorSource(ReadSceneNavigationWalkable);
            error = null;
            return true;
        }

        private bool ReadSceneNavigationWalkable(out WalkableRegion region, out float height)
        {
            region = null;
            height = 0f;
            NavMeshSurface surface = _sceneNavigation != null ? _sceneNavigation.GetComponent<NavMeshSurface>() : null;
            return NavMeshSurfaceWalkable.TryRead(surface, out region, out height);
        }

        public void StopFoundationLocal()
        {
            if (_foundationLocalBackend != null) _foundationLocalBackend.Stop();
        }

        private void RegisterConnectionStateListener()
        {
            if (!_connectionStateListenerRegistered && _spaceManager != null)
            {
                _spaceManager.ConnectionStateChanged += OnConnectionStateChanged;
                _connectionStateListenerRegistered = true;
            }
        }

        private void UnregisterConnectionStateListener()
        {
            if (_connectionStateListenerRegistered && _spaceManager != null)
            {
                _spaceManager.ConnectionStateChanged -= OnConnectionStateChanged;
            }
            _connectionStateListenerRegistered = false;
        }

        private void OnConnectionStateChanged(SpatialBlendConnectionState state)
        {
            // StartApps owns application start ordering. A connection transition on
            // its own must never begin avatar/native registration.
            if (state == SpatialBlendConnectionState.Connected && _appStarted)
            {
                TryRegisterCurrentRoomGeometry();
            }
            // Back in the lobby: the Share-when-connected menu offers this room's furniture again (closed at registration).
            else if (state == SpatialBlendConnectionState.Disconnected || state == SpatialBlendConnectionState.Failed)
            {
                OnReturnedToLobby();
            }
        }

        private void StopAvatarRegistration()
        {
            if (_registerAvatarCoroutine != null)
            {
                StopCoroutine(_registerAvatarCoroutine);
                _registerAvatarCoroutine = null;
            }
        }

        private void StartAvatarRegistrationIfNeeded()
        {
            if (IsDesktopObserver) return;
            if (_appStarted && !_avatarRegistrationComplete &&
                _registerAvatarCoroutine == null && isActiveAndEnabled)
            {
                _registerAvatarCoroutine = StartCoroutine(RegisterAvatarWhenReady());
            }
        }


        public override void OnAppStart()
        {
            if (UsesFoundationLocal)
                return;
            if (IsDesktopObserver)
            {
                // Joined, and that is all. No walkable, no avatar, no scene publish, no room geometry.
                _appStarted = true;
                _nativeShareTracker.Reset();
                return;
            }
            _appStarted = true;
            _nativeShareTracker.Reset();
            EnforceSingleContentWriter();
            // The room (not the NavMesh) is registered with the core: TryRegisterCurrentRoomGeometry below.
            // Wait for avatar to be set before registering it
            StopAvatarRegistration();
            StartAvatarRegistrationIfNeeded();
            
            // A board still at a scene/prefab pose the one rule rejects gets its initial placement now, before it is shared.
            PlaceInvalidVirtualObjects();
            // Chess and the whiteboard are already placed, with Fusion network ids.
            // Every peer attaches its own copy. Only the host publishes them into
            // the session set; a joiner links that set onto the copy it placed.
            RegisterSceneObjects();
            RegisterNativeEntities();

            RegisterMRUKSceneLoadedCallback();
            // The room's anchors become local obstacle geometry only. Nothing of the room is shared until the user shares it.
            TryRegisterCurrentRoomGeometry();
        }

        /// <summary>
        /// Replaces the MRUK room scan. Tests pass synthetic anchors here.
        /// </summary>
        public void UseSceneAnchorCatalog(ISceneAnchorCatalog catalog) => _sceneAnchorCatalog = catalog;

        /// <summary>
        /// Replaces <see cref="RegisterGameObject"/> so a contract test can see
        /// the host share without a native session.
        /// </summary>
        public void UseSceneAnchorPublisher(ISceneAnchorPublisher publisher) =>
            _sceneAnchorPublisher = publisher;

        public OnShareResponse LatestShareResponse { get; private set; }

        /// <summary>
        /// Registers pre-existing authored native content separately from local
        /// room geometry. Empty/in-flight content is intentionally deferred rather
        /// than shared as a name-only placeholder.
        /// </summary>
        protected void RegisterNativeEntities()
        {
            foreach (var nativeEntity in FindObjectsByType<SbNativeEntity>(FindObjectsSortMode.None))
            {
                if (nativeEntity == null)
                {
                    continue;
                }
                switch (_nativeShareTracker.Evaluate(nativeEntity, ReadShareState(nativeEntity)))
                {
                    case NativeShareDecision.Defer:
                        WatchNativeContent(nativeEntity);
                        break;
                    case NativeShareDecision.Share:
                        RegisterReadyNativeEntity(nativeEntity);
                        break;
                }
            }
        }

        private static NativeEntityShareState ReadShareState(SbNativeEntity nativeEntity)
        {
            byte[] content = nativeEntity.GetContentData();
            return new NativeEntityShareState(
                nativeEntity.IsRemoteReplica, nativeEntity.isActiveAndEnabled,
                nativeEntity.IsAttached(), content != null && content.Length > 0);
        }

        private void WatchNativeContent(SbNativeEntity nativeEntity)
        {
            // Subscribe once even when a scan defers the same entity twice.
            nativeEntity.ContentReady -= OnNativeContentReady;
            nativeEntity.ContentReady += OnNativeContentReady;
        }

        private void OnNativeContentReady(SbNativeEntity nativeEntity)
        {
            if (nativeEntity == null)
            {
                return;
            }
            nativeEntity.ContentReady -= OnNativeContentReady;
            if (_nativeShareTracker.OnContentReady(nativeEntity, ReadShareState(nativeEntity),
                    CanRegisterNativeContent()) == NativeShareDecision.Share)
            {
                RegisterReadyNativeEntity(nativeEntity);
            }
        }

        private bool CanRegisterNativeContent()
        {
            if (IsDesktopObserver) return false;
            return !_isDestroying && isActiveAndEnabled && _appStarted &&
                   _spaceManager != null && _spaceManager.Initialized &&
                   _spaceManager.ConnectionState == SpatialBlendConnectionState.Connected;
        }

        protected virtual void RegisterReadyNativeEntity(SbNativeEntity nativeEntity)
        {
            if (nativeEntity != null && !nativeEntity.IsAttached())
            {
                RegisterGameObject(nativeEntity.gameObject, false);
                if (!nativeEntity.IsAttached())
                {
                    // Registration did not take; let a later scan try again.
                    _nativeShareTracker.Forget(nativeEntity);
                }
            }
        }

        private void ClearPendingNativeEntities()
        {
            foreach (var nativeEntity in new List<SbNativeEntity>(_nativeShareTracker.Pending))
            {
                if (nativeEntity != null)
                {
                    nativeEntity.ContentReady -= OnNativeContentReady;
                }
            }
            _nativeShareTracker.ClearPending();
        }

        private void RegisterSceneObjects()
        {
            bool publish = IsHost();
            var networkEntities = FindObjectsByType<SbNetworkEntity>(FindObjectsSortMode.None);
            foreach (var networkEntity in networkEntities)
            {
                if (networkEntity == null || networkEntity.IsAttached())
                    continue;
                NetworkObject networkObject = networkEntity.GetComponent<NetworkObject>();
                uint networkId = networkObject != null ? networkObject.Id.Raw : 0;
                SbTrace.Line($"[FCShare] register go='{networkEntity.name}' role={(publish ? "host" : "client")} " +
                          $"netId={networkId} pos={networkEntity.transform.position} yaw={networkEntity.transform.eulerAngles.y:0.0} " +
                          $"publish={publish}");
                _spaceManager.ShareGameObject(networkEntity.gameObject, false, publish);
            }
        }

        private void PopulateSharedObjectsFromMRUK()
        {
            if (MRUK.Instance == null)
            {
                Debug.LogWarning("[PopulateSOFromMRUK] MRUK.Instance is null. Skipping shared object population.");
                return;
            }

            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null)
            {
                Debug.LogWarning("[PopulateSOFromMRUK] MRUK current room is null. Skipping shared object population.");
                return;
            }

            foreach (var anchor in room.Anchors)
            {
                if (anchor == null || anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                {
                    continue;
                }
                
                if (anchor.HasAnyLabel(_sharedLabels))
                {
                    _sharedObjects.Add(new SharedObject { gameObject = anchor.gameObject, tag = anchor.Label.ToString() });
                }
            }
        }

        private bool IsAvatar(GameObject gameObject)
        {
            return gameObject != null && gameObject.CompareTag("Avatar");
        }

        private void RegisterMRUKSceneLoadedCallback()
        {
            MRUK current = MRUK.Instance;
            if (current == null || _sceneLoadedCallbackOwner == current)
            {
                return;
            }

            UnregisterMRUKSceneLoadedCallback();

            // MRUK invokes immediately when already initialized, providing both
            // catch-up and future scene-loaded notification through one callback.
            current.RegisterSceneLoadedCallback(OnMrukSceneLoaded);
            _sceneLoadedCallbackOwner = current;
        }

        private void UnregisterMRUKSceneLoadedCallback()
        {
            if (_sceneLoadedCallbackOwner != null)
            {
                _sceneLoadedCallbackOwner.SceneLoadedEvent.RemoveListener(OnMrukSceneLoaded);
            }
            _sceneLoadedCallbackOwner = null;
        }

        private MRUKRoom _roomRegisteredFor;
        private long _roomRegisteredGeneration = long.MinValue;

        private void OnMrukSceneLoaded()
        {
            OnRoomLoaded();
            TryRegisterCurrentRoomGeometry();
        }

        private void TryRegisterCurrentRoomGeometry()
        {
            if (IsDesktopObserver) return;
            if (_isDestroying || !_appStarted || _spaceManager == null ||
                !_spaceManager.Initialized ||
                _spaceManager.ConnectionState != SpatialBlendConnectionState.Connected)
            {
                return;
            }

            MRUKRoom room = CurrentRoom();
            if (room == null)
            {
                // No room to wait for: authored objects are fitted now, and furniture that arrives later moves them clear.
                _spaceManager.MarkLocalSceneGeometryRegistered();
                ResolveAnchorSharesAwaitingRoom();
                return;
            }

            // The adapter emits only local Geometry descriptors: the room's anchors are obstacles of the free space and nothing else
            // (no shared id, no relation, transparent to scope). It never creates SbEntity components or participates in
            // cross-space sharing; what the user shares is registered by RoomShare, and only then.
            long geometryGeneration = _spaceManager.ConnectionGeneration;
            SpatialBlendManager geometryManager = _spaceManager;
            // The room first (the enlarged MRUK room and its walls), then the furniture boxes, then what is shared in it. Once per
            // room and connection: this runs on enable, at app start, on connect and when MRUK loads a room, and binding the
            // furniture again would retire and restage every box, which a shared anchor's box cannot be.
            // The furniture is bound once per room and connection, and only then: a second Bind (even with the same room) would
            // retire and restage every furniture box. A room region the core refused is tried again on the next call, alone.
            bool newRoom = _roomRegisteredFor != room || _roomRegisteredGeneration != geometryGeneration;
            if (newRoom || geometryManager.RegisteredRoom == null)
            {
                long started = RuntimeWorkCounters.Start();
                RoomRegion region = MrukRoomRegion.Build(room);
                bool registered = region != null && geometryManager.RegisterRoomRegion(region);
                if (registered)
                {
                    ++RuntimeWorkCounters.RoomRegistrations;
                    RuntimeWorkCounters.Log("room registered", started);
                }
                else Debug.LogError("[FreeformCollaborationApp] The MRUK room could not be registered as the room's region.");
            }
            if (newRoom)
            {
                _roomRegisteredFor = room;
                _roomRegisteredGeneration = geometryGeneration;
                long started = RuntimeWorkCounters.Start();
                // Bind registers the room's current anchors before it returns.
                _sceneGeometryWatcher.Bind(room, geometryManager, () =>
                    !_isDestroying && _appStarted && geometryManager == _spaceManager &&
                    geometryManager.Initialized &&
                    geometryManager.ConnectionState == SpatialBlendConnectionState.Connected &&
                    geometryManager.ConnectionGeneration == geometryGeneration);
                ++RuntimeWorkCounters.SceneGeometryBinds;
                RuntimeWorkCounters.Log("room furniture bound", started);
            }
            geometryManager.MarkLocalSceneGeometryRegistered();
            ResolveAnchorSharesAwaitingRoom();
            if (_furnitureMenu != null) _furnitureMenu.Close();
            ShareChosenRoomAnchors();
        }

        /// <summary>
        /// Computes the local-room arrival pose without depending on an avatar rig.
        /// The remote head remains at the viewer's head height and exactly two metres
        /// away in the horizontal viewing direction. A vertical/degenerate forward
        /// vector has no yaw, so use world-forward deterministically instead of ever
        /// constructing a zero-vector LookRotation.
        /// </summary>
        public static bool TryComputeRemoteArrivalPose(Vector3 viewerPosition,
                                                       Vector3 viewerForward,
                                                       out Vector3 spawnPosition,
                                                       out Quaternion spawnRotation)
        {
            spawnPosition = Vector3.zero;
            spawnRotation = Quaternion.identity;
            if (!IsFinite(viewerPosition) || !IsFinite(viewerForward))
            {
                return false;
            }

            // Do not use ProjectOnPlane/sqrMagnitude here: both can overflow on
            // finite tracking values near float.MaxValue. Scale before normalizing.
            float horizontalScale = Mathf.Max(Mathf.Abs(viewerForward.x),
                                              Mathf.Abs(viewerForward.z));
            Vector3 flatForward;
            if (horizontalScale < 1e-4f)
            {
                flatForward = Vector3.forward;
            }
            else
            {
                float scaledX = viewerForward.x / horizontalScale;
                float scaledZ = viewerForward.z / horizontalScale;
                float inverseLength = 1f / Mathf.Sqrt(scaledX * scaledX + scaledZ * scaledZ);
                flatForward = new Vector3(scaledX * inverseLength, 0f,
                                          scaledZ * inverseLength);
            }

            spawnPosition = viewerPosition + flatForward * 2f;
            if (!IsFinite(spawnPosition))
            {
                spawnPosition = Vector3.zero;
                return false;
            }
            spawnRotation = Quaternion.LookRotation(-flatForward, Vector3.up);
            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType onObjectAddedType)
        {
            if (_isDestroying || gameObject == null)
            {
                return;
            }
            if (onObjectAddedType == OnObjectAddedType.Linked)
            {
                // The core recorded the host-to-local link when LinkToExisting resolved. That
                // link is the support correspondence; the app registers nothing further.
                gameObject.GetComponent<SbMRUKAnchorEntity>()?.EnsureSupportVisuals();
                return;
            }
            if (onObjectAddedType != OnObjectAddedType.Added)
            {
                return;
            }

            if (IsAvatar(gameObject))
            {
                HandleRemoteAvatarArrival(gameObject);
            }
            else if (gameObject.GetComponent<SbMRUKAnchorEntity>() != null)
            {
                // A real object the other user shared and this room had no anchor of its label for: a box to install.
                CreateSharedBox(gameObject.GetComponent<SbMRUKAnchorEntity>());
                _arrivals.NoteObject();
                PlayArrivalSfx(_remoteObjectClip, gameObject.transform.position);
            }
            else if (_remoteObjects.Add(gameObject))
            {
                // A received virtual copy is placed by the initial rule before its region is written (never moved after).
                PlaceReceivedCopy(gameObject);
                // A copy of a peer's native object (a scan) gets the receiver's menu: who it is from, and Hide for me.
                var nativeReplica = gameObject.GetComponent<SbNativeEntity>();
                if (nativeReplica != null && nativeReplica.IsRemoteReplica)
                    SpatialBlend.Samples.Scanning.ReceivedCopy.Attach(gameObject);
                _arrivals.NoteObject();
                OnRemoteObjectAdded?.Invoke(gameObject);
                PlayArrivalSfx(_remoteObjectClip, gameObject.transform.position);
            }
        }

        private void HandleRemoteAvatarArrival(GameObject gameObject)
        {
            if (!_remoteAvatars.Contains(gameObject))
            {
                _remoteAvatars.Add(gameObject);
                _arrivals.NotePerson();
                EnsureAnchorTargetController(gameObject);
                TryPlaceRemoteAvatar(gameObject);
                PlayArrivalSfx(_remoteAvatarClip, gameObject.transform.position);
            }

            // Voice routing requires only a stable remote NetworkObject. It must not
            // be held behind optional Meta-avatar/retarget readiness checks.
            var networkObject = gameObject.GetComponent<NetworkObject>();
            if (networkObject != null && !networkObject.HasStateAuthority &&
                _notifiedRemoteAvatars.Add(gameObject))
            {
                OnRemoteAvatarAdded?.Invoke(gameObject, networkObject.StateAuthority);
            }
        }

        private void TryPlaceRemoteAvatar(GameObject gameObject)
        {
            var remoteAvatar = gameObject.GetComponent<MetaAvatarEntity>();
            var viewerEntity = _userAvatar != null ? _userAvatar.GetComponent<SbEntity>() : null;
            var viewerTransform = viewerEntity != null ? viewerEntity.EntityTransform : null;
            if (remoteAvatar == null || remoteAvatar._sbAvatar == null ||
                remoteAvatar._sbAvatar._retargetTransform == null || viewerTransform == null ||
                !TryComputeRemoteArrivalPose(viewerTransform.position,
                                              viewerTransform.forward,
                                              out Vector3 spawnPosition,
                                              out Quaternion spawnRotation))
            {
                Debug.LogWarning("[FreeformCollaborationApp] Remote avatar arrived before its " +
                                 "optional retarget/viewer setup; skipped initial retarget placement.");
                return;
            }
            // Placed through the driver: at the v1 spawn height, and the core learns the placement.
            SbEntity remoteEntity = gameObject.GetComponent<SbEntity>();
            if (_spaceManager != null)
            {
                _spaceManager.PlaceReceivedAvatar(remoteEntity, spawnPosition, spawnRotation);
            }
            else
            {
                spawnPosition.y = SpatialBlend.Internal.EmbodiedDriverConfig.SpawnHeightMeters;
                remoteAvatar._sbAvatar._retargetTransform.SetHeadTransform(spawnPosition, spawnRotation);
            }
        }

        private void EnsureAnchorTargetController(GameObject remoteAvatar)
        {
            if (!_enableAnchorTargetController || remoteAvatar == null)
            {
                return;
            }

            if (remoteAvatar.GetComponent<SbEntity>() == null)
            {
                Debug.LogWarning("[FreeformCollaborationApp] Received avatar has no SbEntity; " +
                                 "cannot route anchor targets.");
                return;
            }

            var controller = remoteAvatar.GetComponent<AnchorTargetController>();
            if (controller == null)
            {
                controller = remoteAvatar.AddComponent<AnchorTargetController>();
                _ownedAnchorTargetControllers.Add(controller);
            }
            else if (_ownedAnchorTargetControllers.Contains(controller) && !controller.enabled)
            {
                // OnRemove released only this app-created controller's goals. A
                // subsequent authoritative add must restore that same ownership.
                controller.enabled = true;
            }
            EnsureRetargetAnchorTargetBridge(remoteAvatar, controller);
        }

        private void EnsureRetargetAnchorTargetBridge(GameObject remoteAvatar,
                                                       AnchorTargetController controller)
        {
            if (remoteAvatar == null || controller == null)
            {
                return;
            }

            var bridge = remoteAvatar.GetComponent<SbRetargetAnchorTargetBridge>();
            if (bridge == null)
            {
                bridge = remoteAvatar.AddComponent<SbRetargetAnchorTargetBridge>();
                _ownedRetargetAnchorBridges.Add(bridge);
            }
            else if (!_ownedRetargetAnchorBridges.Contains(bridge))
            {
                // A scene/application may have bound this bridge to a different
                // controller or retarget policy.  The Freeform opt-in owns only the
                // component it created; do not replace an external configuration.
                return;
            }
            else if (!bridge.enabled)
            {
                bridge.enabled = true;
            }
            // The bridge resolves a late Meta retarget itself. Configure immediately
            // so controller replacement/re-add cannot leave a stale subscription.
            bridge.Configure(controller);
        }

        public override OnShareResponse OnShare(GameObject gameObject)
        {
            if (IsDesktopObserver)
            {
                bool isAnchor = gameObject != null && gameObject.GetComponent<SbMRUKAnchorEntity>() != null;
                return RememberShare(ObserverSharePolicy.Response(ObserverSharePolicy.Classify(IsAvatar(gameObject), isAnchor)));
            }

            // Avatars are new replicas. A shared scene anchor links to this peer's own anchor of the same label
            // (the largest free one of its label) or is added as a box. Everything else is a new object, as in v1.
            if (IsAvatar(gameObject))
            {
                return RememberShare(OnShareResponse.AddAsNew());
            }

            var placed = gameObject != null ? gameObject.GetComponent<SbNetworkEntity>() : null;
            if (placed != null)
            {
                // The correspondence is the Fusion network id. The object is already
                // placed in this room, so a share does not move it.
                NetworkObject networkObject = placed.GetComponent<NetworkObject>();
                uint networkId = networkObject != null ? networkObject.Id.Raw : 0;
                bool link = placed.IsAttached();
                SbTrace.Line($"[FCShare] receive go='{placed.name}' role={(IsHost() ? "host" : "client")} " +
                          $"netId={networkId} placedPos={placed.transform.position} yaw={placed.transform.eulerAngles.y:0.0} " +
                          $"decision={(link ? "LinkToExisting" : "AddAsNew")}");
                return RememberShare(link
                    ? OnShareResponse.LinkToExisting(placed.gameObject)
                    : OnShareResponse.AddAsNew());
            }

            var anchor = gameObject != null ? gameObject.GetComponent<SbMRUKAnchorEntity>() : null;
            if (anchor != null)
            {
                // A shared room object: link to this room's anchor of its label, or add it as a box to install (RoomShare).
                return RememberShare(ResolveAnchorShare(anchor));
            }

            // v1 (FreeformCollaborationApp.OnShare) accepted every other shared object:
            // scanned GLB objects and network entities arrive as new objects. Rejecting them dropped every
            // object registered with propagate=false.
            return RememberShare(OnShareResponse.AddAsNew());
        }

        private OnShareResponse RememberShare(OnShareResponse response)
        {
            LatestShareResponse = response;
            return response;
        }

        public override void OnRemove(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            _remoteAvatars.Remove(gameObject);
            _remoteObjects.Remove(gameObject);
            _notifiedRemoteAvatars.Remove(gameObject);

            var controller = gameObject.GetComponent<AnchorTargetController>();
            if (controller != null && _ownedAnchorTargetControllers.Contains(controller))
            {
                // Explicitly release before disable: a controller can already be
                // disabled in an EditMode fixture or by another lifecycle path, in
                // which case OnDisable would not run again. Never touch an external
                // controller's tracked-avatar ownership.
                controller.ReleaseAllTargets();
                // A release observer can synchronously re-add this avatar. Its new
                // attachment owns this controller now; old removal must not disable it.
                if (!_remoteAvatars.Contains(gameObject))
                {
                    controller.enabled = false;
                }
            }
            DisableOwnedRetargetAnchorBridge(gameObject);
            // Last: a box is destroyed with its object.
            ReleaseSharedAnchor(gameObject);
        }

        public override void OnAppStop()
        {
            StopAvatarRegistration();
            _appStarted = false;
            _avatarRegistrationComplete = false;
            _sourceBodyRegistered = false;
            _sittingRelationRegistered = false;
            UnregisterMRUKSceneLoadedCallback();
            _sceneGeometryWatcher.Dispose();
            ClearPendingNativeEntities();
            _remoteAvatars.Clear();
            _remoteObjects.Clear();
            _notifiedRemoteAvatars.Clear();
            _seatLinks.Clear();
            _pendingSeatShares.Clear();
            _anchorSharesAwaitingRoom.Clear();
            _roomAnchorsReady = false;
            ClearRoomShares();

            foreach (var controller in new List<AnchorTargetController>(_ownedAnchorTargetControllers))
            {
                if (controller != null)
                {
                    controller.ReleaseAllTargets();
                    // TargetReleased can synchronously route a new authoritative
                    // arrival. Old-session teardown must not disable that re-add.
                    if (!_remoteAvatars.Contains(controller.gameObject))
                    {
                        controller.enabled = false;
                    }
                }
            }
            DisableOwnedRetargetAnchorBridges();
            base.OnAppStop();
        }

        private void DisableOwnedRetargetAnchorBridge(GameObject gameObject)
        {
            var bridge = gameObject != null
                ? gameObject.GetComponent<SbRetargetAnchorTargetBridge>()
                : null;
            if (bridge != null && _ownedRetargetAnchorBridges.Contains(bridge) &&
                !_remoteAvatars.Contains(gameObject))
            {
                bridge.enabled = false;
            }
        }

        private void DisableOwnedRetargetAnchorBridges(bool force = false)
        {
            foreach (var bridge in new List<SbRetargetAnchorTargetBridge>(
                         _ownedRetargetAnchorBridges))
            {
                if (bridge != null && (force || !_remoteAvatars.Contains(bridge.gameObject)))
                {
                    bridge.enabled = false;
                }
            }
        }

        private void NarrateArrivals()
        {
            string line = _arrivals.Flush(Time.unscaledTime);
            if (line == null) return;
            ModeSwitchAssist.ShowNotice(line);
            PlayNarration(ArrivalNarration.ClipFor(line));
        }

        // The same 2D source the tutorial uses for a spoken line: it stays audible while the user walks, and a new line
        // replaces the one still playing. A missing clip leaves the notice, which is what the tutorial does too.
        private void PlayNarration(string clipName)
        {
            if (!Application.isPlaying || string.IsNullOrEmpty(clipName)) return;
            AudioClip clip = Resources.Load<AudioClip>("TutorialAssets/Arrival/" + clipName);
            if (clip == null) return;
            if (_narration == null)
            {
                _narration = gameObject.AddComponent<AudioSource>();
                _narration.playOnAwake = false;
                _narration.loop = false;
                _narration.spatialBlend = 0f;
            }
            _narration.Stop();
            _narration.clip = clip;
            _narration.Play();
        }

        private void PlayArrivalSfx(AudioClip clip, Vector3 position)
        {
            if (clip != null && IsFinite(position))
            {
                AudioSource.PlayClipAtPoint(clip, position, _arrivalSfxVolume);
            }
        }

        /// <summary>
        /// The relations the user's avatar is registered with: Spacing on the root, Facing on the head. Each anchor is
        /// one the received avatar samples (SbNetworkAvatarEntity.RealizedAnchorNames plus the root), so the core
        /// answers every relation with a proposal the embodied driver can follow.
        /// </summary>
        public static readonly (string Anchor, RelationKind Kind)[] AvatarRelationBindings =
        {
            (SpatialAuthoringCompiler.SpacingAnchor, RelationKind.Spacing),
            (SpatialAuthoringCompiler.FacingAnchor, RelationKind.Facing),
        };

        /// <summary>
        /// The pelvis relation the app declares for sitting (design section 2.0): the pelvis anchor is bound to AffordancePose, and the
        /// referent query is the scope's <see cref="SceneAnchorSpatialAuthoring.SittableTag"/> affordance. The pelvis is not an anchor
        /// the entity samples: it exists through the embodiment registration (a bone and a measured sit-bone offset), so it is bound
        /// here and not part of <see cref="AvatarRelationBindings"/>, whose anchors must all be sampled by the entity.
        /// </summary>
        public static readonly (string Anchor, RelationKind Kind, string Tag) SittingRelationBinding =
            (SpatialAuthoringCompiler.PelvisAnchor, RelationKind.AffordancePose, SceneAnchorSpatialAuthoring.SittableTag);

        private IEnumerator RegisterAvatarWhenReady()
        {
            // Wait until the user avatar is spawned and set
            while (_userAvatar == null)
            {
                yield return null;
            }
            // The avatar hangs under the room, the navigable root, which has footprint geometry only once the walkable
            // region is registered. The registration re-runs whenever the navigation mesh updates.
            bool warned = false;
            while (_spaceManager != null && _spaceManager.RegisteredWalkable == null)
            {
                if (!warned)
                {
                    Debug.LogError("[FreeformCollaborationApp] Waiting for the room's walkable footprint before registering the avatar.");
                    warned = true;
                }
                yield return null;
            }

            RegisterGameObject(_userAvatar, true);

            // v2 (SpatialBlend) relations: reconstruct the remote avatar's behavior
            // under the viewer's local room. Spacing preserves geodesic approach
            // toward referents (root anchor); Facing preserves directional engagement
            // (head anchor: the anchor the received avatar samples, so the core can
            // answer the relation; see AvatarRelationBindings). Interaction is not a relation binding: it is the core's
            // containment state (T3.8). While the avatar's root is contained in a shared
            // object's declared region, that object is its scope, "interacting" is true, and
            // the object-relative pose passes through. The manager reads it for every
            // registered avatar, so there is nothing to register here.
            // "root" and "head" are added automatically when the avatar is registered.
            foreach ((string anchor, RelationKind kind) in AvatarRelationBindings)
            {
                RegisterRelation(new SpatialRelation(_userAvatar, anchor, kind));
            }
            _avatarRegistrationComplete = true;
            // The pelvis relation waits for the pelvis anchor, which the source's embodiment registration declares.
            TryRegisterSittingRelation();
            _registerAvatarCoroutine = null;
        }
    }
}
