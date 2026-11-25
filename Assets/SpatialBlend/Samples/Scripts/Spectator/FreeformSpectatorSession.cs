// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Samples
{
    public enum SpectatorPhase
    {
        /// <summary>No room chosen yet: the text field and Join button are shown.</summary>
        Idle,
        /// <summary>In the room list, waiting for the named room to appear.</summary>
        Searching,
        /// <summary>The room is listed; the runner is starting as a core client.</summary>
        Joining,
        /// <summary>In the room and linking, or linked, to its host.</summary>
        Spectating,
        Failed,
    }

    /// <summary>
    /// Joins an existing FC room by name as a spectator, and tells the user how it is going. It is the launcher of
    /// <see cref="FreeformSpectatorApp"/>: the room list, the Shared-mode runner started as a core client, and the three
    /// runner events <see cref="FusionConnectionManager"/> needs (the Freeform scene wires the same three with a NetworkEvents
    /// component). It never hosts: a room that is not listed is waited for until the timeout and then reported; a runner that
    /// nevertheless finds itself the only member of a room it created leaves it again.
    ///
    /// Every way out (Leave, a timeout, a failed or lost runner, a core link that does not come up) goes through one teardown:
    /// the core connection is told the participant is leaving, the room list component is discarded, the runner is shut down,
    /// and a join that was still starting is cancelled. The room counts a spectator as a player, and the host waits for every
    /// player's ready acknowledgement, so a spectator whose core link does not come up within the connect deadline leaves the
    /// room rather than hold the participants' apps back.
    ///
    /// Fusion consumes the scene's NetworkObjects when a runner shuts down (the room's connection manager is one), which is why
    /// the Freeform app's bootstrap reloads its scene after a shutdown. The spectator does the same: after a runner has been shut
    /// down the scene is reloaded, a new session takes over (the notice of what ended the last one is carried across), and only
    /// then can the spectator join again. A join that never got as far as a runner needs no reload.
    ///
    /// The scene must be the first scene of the build and carry the same scene NetworkObjects, in the same order, as the
    /// Freeform scene: Fusion binds a client to the host's scene objects by their position in SortKey order
    /// (SpatialBlend.Editor.SpectatorBuild.CreateScene writes the scene and checks the order).
    /// </summary>
    public class FreeformSpectatorSession : MonoBehaviour, INetworkRunnerCallbacks
    {
        public const float DefaultRoomWaitSeconds = 25f;
        public const float DefaultJoinSeconds = 30f;
        public const string CoreLinkDeadlineError = "The core link to the room's host did not come up in time. The spectator left the room so as not to hold up the participants.";
        public const string HostLeftError = "The room's host left.";
        public const string JoinTimedOutError = "Joining the room took too long.";

        public readonly struct RunnerStart
        {
            private RunnerStart(bool ok, string error, bool createdRoom) { Ok = ok; Error = error; CreatedRoom = createdRoom; }
            public bool Ok { get; }
            public string Error { get; }
            /// <summary>Fusion's Shared mode made the room because it did not exist: this runner is its only member.</summary>
            public bool CreatedRoom { get; }
            public static RunnerStart Success() => new RunnerStart(true, null, false);
            public static RunnerStart Fail(string error) => new RunnerStart(false, error, false);
            public static RunnerStart Created() => new RunnerStart(true, null, true);
        }

        [SerializeField] private SpatialBlendManager _spaceManager;
        [SerializeField] private FusionConnectionManager _connection;
        [SerializeField] private FreeformSpectatorApp _app;
        [SerializeField] private SpectatorCamera _camera;
        [SerializeField, Tooltip("Join the room named by -spectate-room / SPATIALBLEND_SPECTATE_ROOM at start.")]
        private bool _autoJoinFromLaunch = true;
        [SerializeField] private float _roomWaitSeconds = DefaultRoomWaitSeconds;
        [SerializeField] private float _joinSeconds = DefaultJoinSeconds;
        [SerializeField, Tooltip("The UI typeface (Pretendard). Carried by the scene so a player build includes it; the window is built from it.")]
        private SpatialBlend.Samples.FreeformCollaboration.UI.UiFontSet _fonts;

        // Carried across the scene reload that follows a shut-down runner.
        private static string s_notice;
        private static string s_lastRoom = "";
        private static bool s_hasStarted;

        private SpectatorSessionBrowser _browser;
        private bool _resetting;
        private NetworkRunner _runner;
        private bool _runnerLive;
        private int _generation;
        private string _room = "";
        private string _typedRoom = "";
        private double _deadline;
        private double _coreDeadline;
        private bool _coreLinked;
        private bool _framed;
        private bool _quitting;
        private SpectatorJoinPolicy.WaitReason _waitReason;
        private double _nextPresence;
        private bool _loggedFirstRoom;

        public SpectatorPhase Phase { get; private set; } = SpectatorPhase.Idle;
        /// <summary>
        /// The participants' rooms as they publish them to observers (the desktop observer's OBS1 snapshots: furniture, placed
        /// objects, the owner's headset and every avatar as realized in that room). The minimap draws the selected one.
        /// </summary>
        public ObserverSessionState Rooms { get; } = new ObserverSessionState();
        /// <summary>The minimap; null until Start.</summary>
        public SpectatorMinimap Minimap { get; private set; }
        /// <summary>The viewed room drawn in 3-D (walls, furniture, people as proxies); null until Start.</summary>
        public SpectatorRoomView RoomView { get; private set; }
        /// <summary>The Photon region the room list and the runner use (<see cref="SpectatorJoinPolicy.ResolveRegion"/>).</summary>
        /// <summary>Empty for the best region by ping (<see cref="SpectatorJoinPolicy.BestRegion"/>).</summary>
        public string Region { get; private set; } = SpectatorJoinPolicy.BestRegion;
        public string RegionSource { get; private set; } = "best";
        /// <summary>The region as the status line and the minimap show it.</summary>
        public string RegionLabel => SpectatorJoinPolicy.RegionLabel(Region);
        /// <summary>The room the window's field starts with: the last one joined in this process.</summary>
        public string TypedRoom => _typedRoom;
        /// <summary>The link line of the connection (which carrier is up), or empty.</summary>
        public string CarrierText => _connection != null ? _connection.CarrierText : null;
        /// <summary>The window; null until Start.</summary>
        public SpectatorOverlay Overlay { get; private set; }
        public string Error { get; private set; }
        public string Room => _room;
        /// <summary>What the spectator is waiting for while it searches; null otherwise.</summary>
        public string Notice { get; private set; }
        public NetworkRunner Runner => _runner;
        public FusionConnectionManager Connection => _connection;
        public FreeformSpectatorApp App => _app;
        public SpatialBlendManager SpaceManager => _spaceManager;

        /// <summary>Test seam and scene wiring.</summary>
        public void Configure(SpatialBlendManager manager, FusionConnectionManager connection, FreeformSpectatorApp app, SpectatorCamera camera)
        {
            _spaceManager = manager;
            _connection = connection;
            _app = app;
            _camera = camera;
        }

        // ---- seams (overridden by tests; the defaults are Unity's and Fusion's) ---------------------------------------

        protected virtual double Now => Time.realtimeSinceStartupAsDouble;
        protected virtual SpatialBlendConnectionState CoreState => _spaceManager != null ? _spaceManager.ConnectionState : SpatialBlendConnectionState.Disconnected;
        protected virtual string CoreError => _spaceManager != null ? _spaceManager.ConnectionError : string.Empty;
        protected virtual bool HostHasLeft => _connection != null && _connection.HostLeft;

        /// <summary>The longest a core link may take to come up: the declared connect deadline (15 s by default), as for a participant.</summary>
        protected virtual float ConnectDeadlineSeconds
        {
            get
            {
                var settings = SpatialBlendManager.CurrentSettings;
                return settings != null && settings.connectDeadlineSeconds > 0f ? settings.connectDeadlineSeconds : SpatialBlendSettings.DefaultConnectDeadlineSeconds;
            }
        }

        /// <summary>Reloads the active scene, which gives the next join a fresh connection manager (see the class comment).</summary>
        protected virtual void ReloadScene() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

        /// <summary>The players a room needs before its apps start: the declared <c>playersToStartApps</c>, as the connection manager reads it.</summary>
        protected virtual int ParticipantsRequired
        {
            get
            {
                var settings = SpatialBlendManager.CurrentSettings;
                return settings != null && settings.playersToStartApps >= 1 ? settings.playersToStartApps : SpatialBlendSettings.DefaultPlayersToStartApps;
            }
        }

        private void ResolveRegion()
        {
            string configured = null;
            if (Fusion.Photon.Realtime.PhotonAppSettings.TryGetGlobal(out var photon) && photon.AppSettings != null) configured = photon.AppSettings.FixedRegion;
            Region = SpectatorJoinPolicy.ResolveRegion(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable, configured, out string source);
            RegionSource = source;
            Debug.Log($"[Spectator] Photon region={RegionLabel} ({source})");
        }

        /// <summary>The project's Photon settings with the region set to <see cref="Region"/> (empty: best region); null without project settings.</summary>
        internal Fusion.Photon.Realtime.FusionAppSettings PinnedAppSettings()
        {
            if (!Fusion.Photon.Realtime.PhotonAppSettings.TryGetGlobal(out var photon) || photon.AppSettings == null) return null;
            var copy = photon.AppSettings.GetCopy();
            copy.FixedRegion = Region;
            return copy;
        }

        /// <summary>
        /// Whether <paramref name="participantId"/> is a player in the room who is not an announced observer. True without a running
        /// runner (tests, and a snapshot that races the start): the guard narrows, it does not authenticate.
        /// </summary>
        protected virtual bool IsActiveParticipant(string participantId)
        {
            if (_runner == null || !_runner.IsRunning) return true;
            foreach (PlayerRef player in _runner.ActivePlayers)
            {
                if (ObserverSceneMap.ParticipantId(player.PlayerId) != participantId) continue;
                return player != _runner.LocalPlayer && (_connection == null || !_connection.IsKnownObserver(player));
            }
            return false;
        }

        /// <summary>Tells the room this spectator is watching (the observer presence RPC). A seam: tests count the calls.</summary>
        protected virtual void AnnouncePresence()
        {
            if (_connection != null) _connection.AnnounceLocalObserver();
        }

        protected virtual SpectatorSessionBrowser CreateBrowser() => gameObject.AddComponent<SpectatorSessionBrowser>();

        /// <summary>Creates the runner and starts it as a Shared-mode client of <paramref name="sessionName"/>.</summary>
        protected virtual async Task<RunnerStart> StartRunnerCore(string sessionName)
        {
            var runnerObject = new GameObject("SpectatorRunner");
            runnerObject.transform.SetParent(transform, false);
            var runner = runnerObject.AddComponent<NetworkRunner>();
            _runner = runner;
            runner.AddCallbacks(this);
            var sceneManager = runnerObject.AddComponent<NetworkSceneManagerDefault>();
            var objectProvider = runnerObject.AddComponent<NetworkObjectProviderDefault>();
            var sceneInfo = new NetworkSceneInfo();
            var active = SceneManager.GetActiveScene();
            if (active.buildIndex >= 0 && active.buildIndex < SceneManager.sceneCountInBuildSettings)
                sceneInfo.AddSceneRef(SceneRef.FromIndex(active.buildIndex), LoadSceneMode.Additive);
            var settings = SpatialBlendManager.CurrentSettings != null ? SpatialBlendManager.CurrentSettings : Resources.Load<SpatialBlendSettings>("SpatialBlendSettings");
            var result = await runner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = SbFusionBootstrap.ResolvePlayerCount(settings),
                Scene = sceneInfo,
                SceneManager = sceneManager,
                ObjectProvider = objectProvider,
                // The join-only switch: Shared mode refuses a start on a room nobody made (GameNotFound), verified by the two-process
                // test. The CreatedRoom check below stays as the net under it.
                EnableClientSessionCreation = false,
                // The room list's region (SpectatorSessionBrowser): a room is listed only in its own region.
                CustomPhotonAppSettings = PinnedAppSettings(),
            });
            if (!result.Ok) return RunnerStart.Fail($"Could not join the room ({result.ShutdownReason}).");
            return SpectatorJoinPolicy.CreatedTheRoom(runner.IsSharedModeMasterClient, runner.ActivePlayers.Count())
                ? RunnerStart.Created() : RunnerStart.Success();
        }

        protected virtual async Task ShutdownRunnerCore()
        {
            var runner = _runner;
            _runner = null;
            if (runner == null) return;
            runner.RemoveCallbacks(this);
            try { await runner.Shutdown(); }
            catch (Exception e) { Debug.LogWarning("[Spectator] runner shutdown: " + e.Message); }
        }

        /// <summary>The players in the room, for the minimap's waiting line.</summary>
        public int PlayerCountForMap => PlayerCount;

        protected virtual int PlayerCount => _runner != null && _runner.IsRunning ? _runner.ActivePlayers.Count() : 0;

        // ---- lifecycle ---------------------------------------------------------------------------------------------------

        private void Start()
        {
            // One runner per process, as in the product.
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Single;
            // The spectator is a declared desktop observer to the room: a core client that never hosts, gives no
            // ready acknowledgement, does not count toward the participants' start, and is sent their room snapshots once it announces.
            if (_connection != null) _connection.DeclareLocalObserver(true);
            ResolveRegion();
            _typedRoom = s_lastRoom;
            Overlay = SpectatorOverlay.Create(this, _fonts);
            Minimap = SpectatorMinimap.Create(this, Overlay.transform);
            RoomView = SpectatorRoomView.Create(this, _camera);
            if (s_notice != null) { Error = s_notice; s_notice = null; SetPhase(SpectatorPhase.Failed); }
            // The launch room is joined once per process, not again after a leave.
            bool first = !s_hasStarted;
            s_hasStarted = true;
            if (!_autoJoinFromLaunch || !first) return;
            var room = SpectatorJoinPolicy.ResolveRoom(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable);
            if (room != null) Join(room);
        }

        private void Update() => Tick();

        private void OnApplicationQuit() => _quitting = true;

        private void OnDestroy()
        {
            if (Phase != SpectatorPhase.Idle && Phase != SpectatorPhase.Failed) Teardown(null);
        }

        /// <summary>Starts looking for <paramref name="room"/> in the room list. Does nothing while a join is under way.</summary>
        public void Join(string room)
        {
            if (Phase == SpectatorPhase.Searching || Phase == SpectatorPhase.Joining || Phase == SpectatorPhase.Spectating || _resetting) return;
            var name = SpectatorJoinPolicy.Normalize(room);
            if (name == null) { Fail(SpectatorJoinPolicy.EmptyNameError); return; }
            _generation++;
            Error = null;
            Notice = null;
            _waitReason = SpectatorJoinPolicy.WaitReason.None;
            _room = name;
            _typedRoom = name;
            s_lastRoom = name;
            _coreLinked = false;
            DisposeBrowser();
            _browser = CreateBrowser();
            _browser.Attach(this, _connection);
            _browser.Changed += OnBrowserChanged;
            _deadline = Now + _roomWaitSeconds;
            SetPhase(SpectatorPhase.Searching);
            _browser.OpenLobby();
        }

        /// <summary>Leaves the room, or gives up a join that has not finished. The spectator may join again.</summary>
        public void Leave()
        {
            if (Phase == SpectatorPhase.Idle) return;
            Teardown(null);
        }

        /// <summary>One step of the state machine; Update calls it every frame.</summary>
        public void Tick()
        {
            switch (Phase)
            {
                case SpectatorPhase.Searching:
                    if (Now > _deadline)
                        Teardown(_waitReason == SpectatorJoinPolicy.WaitReason.TooFewParticipants
                            ? SpectatorJoinPolicy.WaitingForParticipantsError + $" ({Notice}; \"{_room}\", waited {_roomWaitSeconds:0}s)"
                            : SpectatorJoinPolicy.NotFoundError + $" (\"{_room}\", waited {_roomWaitSeconds:0}s)." + SpectatorJoinPolicy.RegionHint(Region));
                    break;
                case SpectatorPhase.Joining:
                    if (Now > _deadline) Teardown(JoinTimedOutError);
                    break;
                case SpectatorPhase.Spectating:
                    TickSpectating();
                    break;
            }
            if (!_framed && _app != null && _camera != null && _app.TryGetContentBounds(out var bounds))
            {
                // Once, on the first content that has geometry; F frames again on demand.
                _framed = true;
                _camera.Frame(bounds);
            }
        }

        private void TickSpectating()
        {
            if (Now >= _nextPresence)
            {
                // Announce once a second, as the desktop observer does; participants send room snapshots only to announced observers.
                _nextPresence = Now + ObserverTraffic.PresenceIntervalSeconds;
                try { AnnouncePresence(); }
                catch (Exception e) { Debug.LogWarning("[Spectator] observer presence: " + e.Message); }
                Rooms.Expire(Now, DesktopObserverRuntime.RoomTimeoutSeconds);
            }
            var state = CoreState;
            if (state == SpatialBlendConnectionState.Connected) _coreLinked = true;
            if (HostHasLeft) { Teardown(HostLeftError); return; }
            if (state == SpatialBlendConnectionState.Failed) { Teardown("The core link failed: " + CoreError); return; }
            if (!_coreLinked && Now > _coreDeadline) Teardown(CoreLinkDeadlineError);
        }

        // ---- the room list and the runner ----------------------------------------------------------------------------

        private void OnBrowserChanged()
        {
            if (_browser == null) return;
            if (Phase != SpectatorPhase.Searching && Phase != SpectatorPhase.Joining) return;
            if (_browser.State == FusionSessionBrowserState.Failed)
            {
                Teardown(_browser.LastError ?? "The room list could not be read.");
                return;
            }
            if (Phase != SpectatorPhase.Searching) return;
            var decision = SpectatorJoinPolicy.Evaluate(_room, _browser.HasSessionList, _browser.Sessions, ParticipantsRequired);
            _waitReason = decision.Reason;
            Notice = decision.Verdict == SpectatorJoinPolicy.Verdict.Wait ? decision.Notice : null;
            switch (decision.Verdict)
            {
                case SpectatorJoinPolicy.Verdict.Join:
                    _deadline = Now + _joinSeconds;
                    SetPhase(SpectatorPhase.Joining);
                    _browser.Join(_room);
                    break;
                case SpectatorJoinPolicy.Verdict.Refuse:
                    Teardown(decision.Error);
                    break;
            }
        }

        /// <summary>
        /// The browser has adopted the Client role and asks for the runner. False when this join was given up meanwhile (Leave,
        /// a timeout): the browser then undoes its adoption and nothing starts.
        /// </summary>
        internal bool AcceptBrowserStart(SpectatorSessionBrowser browser, string sessionName)
        {
            if (browser != _browser || Phase != SpectatorPhase.Joining) return false;
            BeginRunner(sessionName, _generation);
            return true;
        }

        private async void BeginRunner(string sessionName, int generation)
        {
            RunnerStart result;
            _runnerLive = true;
            try { result = await StartRunnerCore(sessionName); }
            catch (Exception e) { result = RunnerStart.Fail(e.Message); }
            // Given up while starting: that teardown has already shut this runner down.
            if (generation != _generation) return;
            if (!result.Ok) { Teardown(result.Error); return; }
            if (result.CreatedRoom) { Teardown(SpectatorJoinPolicy.RoomCreatedByUsError); return; }
            _coreDeadline = Now + ConnectDeadlineSeconds;
            SetPhase(SpectatorPhase.Spectating);
        }

        /// <summary>
        /// The one way out. Bumps the join generation (a start still in flight is cancelled), tells the core connection the
        /// participant is leaving by its own decision, discards the room list component and shuts the runner down.
        /// <paramref name="error"/> null is a plain leave.
        /// </summary>
        private void Teardown(string error)
        {
            _generation++;
            _nextPresence = 0;
            var hadRunner = _runnerLive;
            _runnerLive = false;
            if (_connection != null) _connection.AdoptMatchmakingDecision(LeaveDecision);
            DisposeBrowser();
            _coreLinked = false;
            Notice = null;
            Error = error;
            if (error != null) Debug.LogWarning("[Spectator] " + error);
            SetPhase(error == null ? SpectatorPhase.Idle : SpectatorPhase.Failed);
            if (hadRunner) ShutdownThenReload(error);
        }

        private async void ShutdownThenReload(string notice)
        {
            _resetting = true;
            try { await ShutdownRunnerCore(); }
            catch (Exception e) { Debug.LogWarning("[Spectator] runner shutdown: " + e.Message); }
            // Not when the application is quitting, or this session has been destroyed meanwhile (the scene is going away anyway).
            if (this == null || _quitting) return;
            s_notice = notice;
            ReloadScene();
        }

        private static readonly MatchmakingDecision LeaveDecision =
            new MatchmakingDecision(true, MatchmakingPhase.Browsing, CoreParticipantRole.None, null, null, null);

        private void Fail(string error)
        {
            Error = error;
            Debug.LogWarning("[Spectator] " + error);
            SetPhase(SpectatorPhase.Failed);
        }

        private void DisposeBrowser()
        {
            var browser = _browser;
            _browser = null;
            if (browser == null) return;
            browser.Changed -= OnBrowserChanged;
            browser.LeaveSession();
            if (Application.isPlaying) Destroy(browser); else DestroyImmediate(browser);
        }

        private void SetPhase(SpectatorPhase phase)
        {
            Phase = phase;
            Debug.Log($"[Spectator] phase={phase} room='{_room}'{(Error != null ? " error=" + Error : "")}");
        }

        // The three events FusionConnectionManager is handed (the Freeform scene's NetworkEvents does the same).
        private bool Ours(NetworkRunner runner) => runner == _runner;

        void INetworkRunnerCallbacks.OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (_connection != null && Ours(runner)) _connection.OnPlayerJoined(runner, player);
        }

        void INetworkRunnerCallbacks.OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (_connection != null && Ours(runner)) _connection.OnPlayerLeft(runner, player);
            if (Ours(runner)) Rooms.Rooms.Remove(ObserverSceneMap.ParticipantId(player.PlayerId));
        }

        void INetworkRunnerCallbacks.OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (!Ours(runner)) return;
            if (_connection != null) _connection.ShutdownConnection();
            if (Phase == SpectatorPhase.Spectating || Phase == SpectatorPhase.Joining)
            {
                _runner = null; // already shut down; the scene is still reloaded after it
                Teardown($"The room connection ended ({shutdownReason}).");
            }
        }

        void INetworkRunnerCallbacks.OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            if (Ours(runner) && (Phase == SpectatorPhase.Spectating || Phase == SpectatorPhase.Joining)) Teardown($"Disconnected from the room ({reason}).");
        }

        void INetworkRunnerCallbacks.OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            if (Ours(runner) && (Phase == SpectatorPhase.Spectating || Phase == SpectatorPhase.Joining)) Teardown($"Connection failed ({reason}).");
        }

        void INetworkRunnerCallbacks.OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnInput(NetworkRunner runner, NetworkInput input) { }
        void INetworkRunnerCallbacks.OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        void INetworkRunnerCallbacks.OnConnectedToServer(NetworkRunner runner) { }
        void INetworkRunnerCallbacks.OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        void INetworkRunnerCallbacks.OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        void INetworkRunnerCallbacks.OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        void INetworkRunnerCallbacks.OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        void INetworkRunnerCallbacks.OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        void INetworkRunnerCallbacks.OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
            if (!Ours(runner) || !ObserverRoomChannel.TryKind(key, out _) || data.Array == null || data.Count <= 0) return;
            ReceiveRoomSnapshot(player, data);
        }

        /// <summary>
        /// One participant's room snapshot (OBS1 kind 1); a malformed one is dropped. The sender is NOT checked against the snapshot's
        /// participant: in Shared mode Fusion's reliable-data callback does not name the sending peer (<paramref name="from"/> is not
        /// the publisher; a check on it dropped every snapshot, an empty minimap), so, as for the desktop observer,
        /// a peer can claim another participant's id: this channel is a view, not authority.
        /// </summary>
        internal bool ReceiveRoomSnapshot(PlayerRef from, ArraySegment<byte> data)
        {
            if (data.Array == null || !ObserverRoomCodec.TryRead(data.Array, data.Offset, data.Count, out ParticipantRoomSnapshot snapshot)) return false;
            // A cheap guard (not authentication): the snapshot must name a player who is in the room and is not an announced observer.
            if (!IsActiveParticipant(snapshot.ParticipantId)) return false;
            if (!_loggedFirstRoom)
            {
                _loggedFirstRoom = true;
                Debug.Log($"[Spectator] first room snapshot: participant {snapshot.ParticipantId}, callback player {from.PlayerId}");
            }
            return Rooms.UpsertRoom(snapshot, Now);
        }
        void INetworkRunnerCallbacks.OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        void INetworkRunnerCallbacks.OnSceneLoadDone(NetworkRunner runner) { }
        void INetworkRunnerCallbacks.OnSceneLoadStart(NetworkRunner runner) { }

        /// <summary>One line of status for the overlay and the logs.</summary>
        public string StatusLine()
        {
            var link = _connection != null ? _connection.ActiveCarrier.ToString() : "-";
            var state = _spaceManager != null ? _spaceManager.ConnectionState.ToString() : "-";
            var entities = _app != null ? _app.ReceivedCount : 0;
            var role = _connection != null ? _connection.ParticipantRole.ToString() : "-";
            return $"room '{_room}'  region {RegionLabel}  {Phase}  core {state} ({role})  carrier {link}  players {PlayerCount}  entities {entities}";
        }
    }

    /// <summary>
    /// The room list of a spectator: <see cref="FusionSessionBrowser"/> with its start hook pointed at the spectator's runner.
    /// Its join path is the matchmaker's TryJoin, so the role is Client; it has no host path that a spectator reaches. One
    /// instance serves one join: the session discards it on every way out, so no state carries over to the next join.
    /// </summary>
    public class SpectatorSessionBrowser : FusionSessionBrowser
    {
        private FreeformSpectatorSession _session;

        internal void Attach(FreeformSpectatorSession session, FusionConnectionManager connection)
        {
            _session = session;
            Configure(null, connection);
        }

        protected override bool CanStartSession => _session != null;

        protected override Fusion.Photon.Realtime.FusionAppSettings LobbyAppSettings => _session != null ? _session.PinnedAppSettings() : null;

        protected override void StartSession(string sessionName)
        {
            // The role was adopted just before this call. A join given up meanwhile undoes it and starts nothing.
            if (_session == null || !_session.AcceptBrowserStart(this, sessionName)) LeaveSession();
        }
    }
}
