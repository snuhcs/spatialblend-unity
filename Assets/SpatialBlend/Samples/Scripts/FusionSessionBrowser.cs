// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace SpatialBlend.Samples
{
    public enum FusionSessionBrowserState
    {
        Idle,
        JoiningLobby,
        InLobby,
        Failed,
        Starting,
    }

    /// <summary>
    /// Lists Shared-mode Fusion sessions and starts <see cref="FusionBootstrap"/>
    /// for a host or join decision from <see cref="FreeformMatchmaking"/>.
    /// Hosting a free name makes this player the core host. Joining makes this
    /// player a core client of that room's host. The runner still
    /// owns the Fusion roster.
    /// </summary>
    public class FusionSessionBrowser : MonoBehaviour, INetworkRunnerCallbacks
    {
        [SerializeField] private FusionBootstrap _bootstrap;
        [SerializeField] private FusionConnectionManager _connectionManager;

        private readonly FreeformMatchmaking _matchmaking = new();
        private NetworkRunner _lobbyRunner;
        private bool _closingLobby;
        private int _lobbyAttempt;
        private List<FusionSessionEntry> _sessions = new();

        public event Action Changed;

        public FusionSessionBrowserState State { get; private set; } = FusionSessionBrowserState.Idle;
        public string LastError { get; private set; }
        public IReadOnlyList<FusionSessionEntry> Sessions => _sessions;
        /// <summary>True once the lobby has delivered a list for the current attempt.</summary>
        public bool HasSessionList { get; private set; }
        public CoreParticipantRole ParticipantRole => _matchmaking.Role;
        public MatchmakingPhase MatchmakingPhase => _matchmaking.Phase;
        public bool RoomListIsEmpty => _matchmaking.IsEmpty;
        public MatchmakingDecision LastDecision { get; private set; }

        public void Configure(FusionBootstrap bootstrap, FusionConnectionManager connection = null)
        {
            _bootstrap = bootstrap;
            if (connection != null) _connectionManager = connection;
        }

        /// <summary>Joins the Shared lobby; the list arrives through <see cref="Changed"/>.</summary>
        public async void OpenLobby()
        {
            try
            {
                await OpenLobbyAsync();
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        public async Task OpenLobbyAsync()
        {
            if (State == FusionSessionBrowserState.JoiningLobby || State == FusionSessionBrowserState.InLobby ||
                State == FusionSessionBrowserState.Starting) return;
            var attempt = ++_lobbyAttempt;
            LastError = null;
            HasSessionList = false;
            _sessions = new List<FusionSessionEntry>();
            _matchmaking.ClearList();
            SetState(FusionSessionBrowserState.JoiningLobby);
            var error = await JoinLobbyAsync();
            // A Back or a newer attempt while this one was pending owns the state now.
            if (attempt != _lobbyAttempt || State != FusionSessionBrowserState.JoiningLobby) return;
            if (error == null) SetState(FusionSessionBrowserState.InLobby);
            else
            {
                await CloseLobbyAsync();
                Fail(error);
            }
        }

        public async void CloseLobby()
        {
            _lobbyAttempt++;
            await CloseLobbyAsync();
            if (State != FusionSessionBrowserState.Starting) SetState(FusionSessionBrowserState.Idle);
        }

        public async void Join(string sessionName)
        {
            try
            {
                await JoinAsync(sessionName);
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        /// <summary>False when this process is a desktop observer: New room is refused.</summary>
        public bool HostingAllowed => _matchmaking.HostingAllowed;

        public void SetHostingAllowed(bool allowed) => _matchmaking.SetHostingAllowed(allowed);

        /// <summary>
        /// Hosts the smallest unlisted "Room N". Refused until the lobby has
        /// answered, since an unseen room with that name would be joined instead.
        /// </summary>
        public void CreateRoom()
        {
            if (State == FusionSessionBrowserState.InLobby && HasSessionList)
                HostRoom(_matchmaking.NextFreeName());
        }

        /// <summary>Hosts <paramref name="sessionName"/> when it is not already listed.</summary>
        public void HostRoom(string sessionName)
        {
            if (State != FusionSessionBrowserState.InLobby || !HasSessionList) return;
            if (LocalObserverForbidsHosting()) SetHostingAllowed(false);
            if (!HostingAllowed)
            {
                Fail(FreeformMatchmaking.ObserverHostError);
                return;
            }
            var decision = _matchmaking.TryHost(sessionName);
            LastDecision = decision;
            if (!decision.Accepted)
            {
                Fail(decision.Error);
                return;
            }

            CompleteStart(decision, decision.SessionName);
        }

        private static bool LocalObserverForbidsHosting() => LocalIsDesktopObserver();

        /// <summary>True when this process runs the freeform app as a desktop observer.</summary>
        internal static bool LocalIsDesktopObserver()
        {
            FreeformCollaborationApp app = UnityEngine.Object.FindFirstObjectByType<FreeformCollaborationApp>();
            return app != null && app.IsDesktopObserver;
        }

        /// <summary>
        /// True when the lobby's current list holds a joinable room named <paramref name="sessionName"/>. An observer starts
        /// only such a room (<see cref="SbFusionBootstrap.StartSharedClient"/>): a Shared start of an unlisted name would
        /// create that room.
        /// </summary>
        public bool ListsJoinable(string sessionName)
        {
            if (!HasSessionList || string.IsNullOrWhiteSpace(sessionName)) return false;
            string name = sessionName.Trim();
            for (int i = 0; i < _sessions.Count; ++i)
                if (string.Equals(_sessions[i].Name, name, StringComparison.Ordinal) && _sessions[i].CanJoin) return true;
            return false;
        }

        /// <summary>
        /// The runner start that <see cref="StartSession"/> began ended without the player in its room (the room is full, closed or gone,
        /// the join timed out, the network failed). Nothing else reports this: the bootstrap leaves its stage at AllConnected and the runner is
        /// already shut down, so without this the browser stayed in Starting (the room list refused to open again), the matchmaker kept the
        /// player InSession and the connection manager kept its client role. This clears all three, keeps the user's sentence for the
        /// control panel that follows, and resets the network stack (<see cref="ResetAfterFailedStart"/>) so Find a room starts clean.
        /// Ignored when the start is no longer the browser's current one (the player left in the meantime).
        /// </summary>
        public void ReportStartFailed(ShutdownReason reason)
        {
            if (State != FusionSessionBrowserState.Starting) return;
            Debug.LogWarning($"[FusionSessionBrowser] Start failed ({reason}); returning to a clean lobby.");
            var message = JoinFailure.ToUserMessage(reason);
            // The core role and the matchmaker go back first; the player's own leave is not a lost host.
            LeaveSession();
            _lobbyAttempt++;
            HasSessionList = false;
            _sessions = new List<FusionSessionEntry>();
            _matchmaking.ClearList();
            LastError = message;
            JoinFailure.Carry(message);
            SetState(FusionSessionBrowserState.Idle);
            ResetAfterFailedStart();
        }

        /// <summary>
        /// Brings the network stack back to where Find a room can start again. The bootstrap cannot be restarted in place (its stage only
        /// moves forward), so this is the stock reset Leave uses: every runner is shut down, the scene reloads and the bootstrap is replaced.
        /// </summary>
        protected virtual void ResetAfterFailedStart()
        {
            if (_bootstrap != null) _bootstrap.ShutdownAll();
        }

        /// <summary>True while the matchmaker has this player in a session.</summary>
        public bool InSession => _matchmaking.Phase == MatchmakingPhase.InSession;

        /// <summary>Leaves the core role. An in-session player also tears the core link down.</summary>
        public void LeaveSession()
        {
            var inSession = _matchmaking.Phase == MatchmakingPhase.InSession;
            LastDecision = _matchmaking.Leave();
            if (inSession) Connection?.AdoptMatchmakingDecision(LastDecision);
        }

        /// <summary>Leaves the lobby and starts the bootstrap on <paramref name="sessionName"/>.</summary>
        public async Task<bool> JoinAsync(string sessionName)
        {
            if (string.IsNullOrWhiteSpace(sessionName) || State == FusionSessionBrowserState.Starting) return false;
            if (!HasSessionList)
            {
                // Without a list the name cannot be checked, and a Shared start of a room nobody made creates it. An observer
                // only watches rooms the lobby listed.
                if (!HostingAllowed || LocalIsDesktopObserver()) return false;
                return await StartWithoutDecision(sessionName);
            }

            var decision = _matchmaking.TryJoin(sessionName);
            LastDecision = decision;
            if (!decision.Accepted)
            {
                if (decision.Phase == MatchmakingPhase.Error) Fail(decision.Error);
                return false;
            }

            return await StartWithDecision(decision, sessionName);
        }

        /// <summary>Called from the lobby callback; public for tests.</summary>
        public void ApplySessions(IEnumerable<FusionSessionEntry> sessions)
        {
            var source = (sessions ?? Enumerable.Empty<FusionSessionEntry>()).ToList();
            _sessions = FusionSessionList.Build(source);
            LastDecision = _matchmaking.Refresh(source.Select(MatchmakingRoom.FromEntry));
            HasSessionList = true;
            Changed?.Invoke();
        }

        private FusionConnectionManager Connection
        {
            get
            {
                if (_connectionManager == null)
                    _connectionManager = GetComponent<FusionConnectionManager>();
                return _connectionManager;
            }
        }

        private void CompleteStart(MatchmakingDecision decision, string sessionName)
        {
            var task = StartWithDecision(decision, sessionName);
            if (task.IsFaulted)
            {
                LeaveSession();
                Fail(task.Exception.GetBaseException().Message);
            }
            else if (!task.IsCompleted) _ = Observe(task);
        }

        private async Task Observe(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                LeaveSession();
                Fail(e.Message);
            }
        }

        // A join issued before any list arrives keeps the previous start path.
        private Task<bool> StartWithoutDecision(string sessionName) =>
            BeginStart(sessionName, decision: null);

        private Task<bool> StartWithDecision(MatchmakingDecision decision, string sessionName) =>
            BeginStart(sessionName, decision);

        private Task<bool> BeginStart(string sessionName, MatchmakingDecision? decision)
        {
            if (!CanStartSession)
            {
                if (decision.HasValue) LeaveSession();
                Fail("Fusion is already running or the bootstrap is missing.");
                return Task.FromResult(false);
            }

            SetState(FusionSessionBrowserState.Starting);
            _lobbyAttempt++;
            var closing = CloseLobbyAsync();
            if (!closing.IsCompleted) return FinishStart(closing, sessionName, decision);
            if (closing.IsFaulted)
            {
                if (decision.HasValue) LeaveSession();
                return Task.FromException<bool>(closing.Exception.GetBaseException());
            }

            return FinishAcceptedStart(sessionName, decision);
        }

        private async Task<bool> FinishStart(Task closing, string sessionName, MatchmakingDecision? decision)
        {
            try
            {
                await closing;
            }
            catch (Exception e)
            {
                if (decision.HasValue) LeaveSession();
                Fail(e.Message);
                return false;
            }

            return FinishAcceptedStart(sessionName, decision).GetAwaiter().GetResult();
        }

        private Task<bool> FinishAcceptedStart(string sessionName, MatchmakingDecision? decision)
        {
            try
            {
                if (decision.HasValue) Connection?.AdoptMatchmakingDecision(decision.Value);
                StartSession(sessionName);
                return Task.FromResult(true);
            }
            catch (Exception e)
            {
                if (decision.HasValue) LeaveSession();
                Fail(e.Message);
                return Task.FromResult(false);
            }
        }

        private void OnDestroy()
        {
            if (_bootstrap is SbFusionBootstrap sb) sb.StartFailed -= ReportStartFailed;
            _ = CloseLobbyAsync();
        }

        private void SetState(FusionSessionBrowserState state)
        {
            State = state;
            Changed?.Invoke();
        }

        private void Fail(string error)
        {
            LastError = error;
            SetState(FusionSessionBrowserState.Failed);
        }

        /// <summary>Listens for the start's outcome: <see cref="SbFusionBootstrap.StartFailed"/> reaches <see cref="ReportStartFailed"/>.</summary>
        protected void ObserveStart(FusionBootstrap bootstrap)
        {
            if (bootstrap is not SbFusionBootstrap sb) return;
            sb.StartFailed -= ReportStartFailed;
            sb.StartFailed += ReportStartFailed;
        }

        protected virtual bool CanStartSession =>
            _bootstrap != null && _bootstrap.CurrentStage == FusionBootstrap.Stage.Disconnected;

        protected virtual void StartSession(string sessionName)
        {
            _bootstrap.DefaultRoomName = sessionName;
            ObserveStart(_bootstrap);
            _bootstrap.StartSharedClient();
        }

        /// <summary>The Photon settings of the room list; null uses the project's (the app). The desktop spectator pins its region here.</summary>
        protected virtual Fusion.Photon.Realtime.FusionAppSettings LobbyAppSettings => null;

        protected virtual async Task<string> JoinLobbyAsync()
        {
            var runnerObject = new GameObject("FusionSessionLobbyRunner");
            _lobbyRunner = runnerObject.AddComponent<NetworkRunner>();
            _lobbyRunner.AddCallbacks(this);
            var result = await _lobbyRunner.JoinSessionLobby(SessionLobby.Shared, customAppSettings: LobbyAppSettings);
            return result.Ok ? null : $"Could not reach the room list ({result.ShutdownReason}).";
        }

        protected virtual async Task CloseLobbyAsync()
        {
            var runner = _lobbyRunner;
            _lobbyRunner = null;
            if (runner == null) return;
            _closingLobby = true;
            try
            {
                runner.RemoveCallbacks(this);
                // A lobby-only runner never reports IsRunning (it has no simulation),
                // so shut down unconditionally; Shutdown also destroys its object.
                await runner.Shutdown();
            }
            finally
            {
                _closingLobby = false;
            }
        }

        void INetworkRunnerCallbacks.OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            if (runner != _lobbyRunner) return;
            // The room list carries only what the lobby knows (name, occupancy, limit). Nothing about the
            // core link is advertised: it rides the Photon session once the player is in the room.
            ApplySessions(sessionList
                .Where(s => s != null && s.IsValid)
                .Select(s => new FusionSessionEntry(s.Name, s.PlayerCount, s.MaxPlayers, s.IsOpen, s.IsVisible)));
        }

        void INetworkRunnerCallbacks.OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (runner != _lobbyRunner || _closingLobby) return;
            _lobbyRunner = null;
            if (State == FusionSessionBrowserState.InLobby) Fail($"Room list disconnected ({shutdownReason}).");
        }

        void INetworkRunnerCallbacks.OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        void INetworkRunnerCallbacks.OnInput(NetworkRunner runner, NetworkInput input) { }
        void INetworkRunnerCallbacks.OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        void INetworkRunnerCallbacks.OnConnectedToServer(NetworkRunner runner) { }
        void INetworkRunnerCallbacks.OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        void INetworkRunnerCallbacks.OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        void INetworkRunnerCallbacks.OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        void INetworkRunnerCallbacks.OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        void INetworkRunnerCallbacks.OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        void INetworkRunnerCallbacks.OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        void INetworkRunnerCallbacks.OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        void INetworkRunnerCallbacks.OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        void INetworkRunnerCallbacks.OnSceneLoadDone(NetworkRunner runner) { }
        void INetworkRunnerCallbacks.OnSceneLoadStart(NetworkRunner runner) { }
    }
}
