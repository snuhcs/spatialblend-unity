// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Coordinates one core connection round over the active Fusion roster. No peer ever dials another peer's address:
    /// the Shared-mode master hosts the core with no listening socket, accepts one core link per joining player, and
    /// every other player joins over a link of its own. The link's bytes are carried between the two peers as Fusion
    /// reliable data, on the Photon session they already share, so hosting and joining work across NAT, mobile
    /// networks and firewalls with no address, port or lookup. See <see cref="CoreLinkPump"/>.
    /// </summary>
    public class FusionConnectionManager : NetworkBehaviour, IPlayerJoined, IPlayerLeft, ICoreLinkWire
    {
        /// <summary>The first int of a core-link reliable key: "OLK1". Other reliable data on the runner is not ours.</summary>
        public const int CoreLinkKeyMagic = FusionCoreLinkKey.Magic;

        [SerializeField] private SpatialBlendManager _spaceManager;

        private readonly HashSet<PlayerRef> _readyPlayers = new();
        private readonly Dictionary<PlayerRef, CoreLinkPump> _pumps = new();
        private readonly Dictionary<PlayerRef, PeerCoreLinkCarrier> _carriers = new();
        private int _signalCounter;
        private long _epoch;
        private long _retiredEpoch;
        // One host-wide counter numbers rounds and links alike, so no number is ever used twice and a link of a later
        // round can never be mistaken for a link of an earlier one.
        private long _idCounter;
        // Joiner side: the id of the link to the host (the round's epoch for the first link, a number the host assigned
        // for a relink) and whether this link may try the direct carrier.
        private int _hostLinkId;
        private bool _hostLinkDirect = true;
        private long _startBroadcastEpoch = -1;
        // The members the last start announcement of _startBroadcastEpoch covered. The announcement is deduplicated on WHO
        // was announced to, never on how many: a leave and a join leave the count where it was but change the roster.
        private readonly HashSet<PlayerRef> _startBroadcastRoster = new HashSet<PlayerRef>();
        private long _appsStartedEpoch = -1;
        private double _deadline;
        private bool _awaitingConnection;
        private bool _reconnectAfterDisconnect;
        private bool _subscribed;
        private PlayerRef _hostPlayer = PlayerRef.None;
        private CoreParticipantRole _participantRole = CoreParticipantRole.None;
        private FusionCoreLinkReceiver _reliableReceiver;
        private LinkTokens _tokens = new LinkTokens();
        // Peers from whom an authenticated message (a chunk or a signal) has arrived on the current link.
        private readonly HashSet<PlayerRef> _heardFrom = new HashSet<PlayerRef>();
        private NetworkRunner _callbacksRunner;
        // Desktop observers announce themselves. They stay in the Fusion roster and off the set of participants
        // playersToStartApps waits for. The record sticks until their Fusion leave: a snapshot timeout does not
        // clear it, and neither does the order of the leave callbacks. Leaving does not retire the participants'
        // round. This process never creates a room and never hosts the core.
        private readonly HashSet<PlayerRef> _observers = new HashSet<PlayerRef>();
        private readonly HashSet<PlayerRef> _observerRecord = new HashSet<PlayerRef>();
        private readonly HashSet<PlayerRef> _observersLeft = new HashSet<PlayerRef>();
        // Epoch for which this host already sent the app-start to that observer. Presence repeats; the start does not.
        private readonly Dictionary<PlayerRef, long> _observerStartEpoch = new Dictionary<PlayerRef, long>();
        private readonly List<PlayerRef> _participants = new List<PlayerRef>();
        private bool _localObserver;
        private bool _observerYieldedMaster;
        private long _queuedObserverStart = -1;

        /// <summary>Raised with the Photon-stamped sender when an observer announces. Snapshot routing listens; the round does not.</summary>
        public event Action<PlayerRef> ObserverAnnounced;

        /// <summary>This process joined as a desktop observer: it is a core client and never the core host.</summary>
        public void DeclareLocalObserver(bool isObserver) => _localObserver = isObserver;

        public bool IsKnownObserver(PlayerRef player) => player != PlayerRef.None && _observerRecord.Contains(player);

        /// <summary>True after any observer has announced and not yet left. With none, scene authority is left alone.</summary>
        public bool HasKnownObserver => _observerRecord.Count > 0;

        /// <summary>
        /// A peer announced observer. They keep a core link so shared meshes still arrive, and they drop out of the
        /// set <see cref="TryStartApps"/> waits on. Once recorded, they stay recorded until <see cref="OnPlayerLeft"/>.
        /// </summary>
        public void NoteObserver(PlayerRef player)
        {
            if (!RememberObserver(player, out bool added)) return;
            if (_appsStartedEpoch == _epoch && _epoch > _retiredEpoch)
                SendObserverStartOnce(player);
            else if (added)
                TryStartApps();
        }

        /// <summary>Roster only: the player is an observer until they leave. No snapshot routing and no app-start.</summary>
        private bool RememberObserver(PlayerRef player, out bool added)
        {
            added = false;
            if (player == PlayerRef.None || player == LocalPlayer) return false;
            added = _observerRecord.Add(player);
            _observers.Add(player);
            _readyPlayers.Remove(player);
            return true;
        }

        /// <summary>
        /// The app-start RPC is host-only. Presence arrives on every peer, so only the authority that is not a
        /// client sends it, and only once per observer per round.
        /// </summary>
        private void SendObserverStartOnce(PlayerRef player)
        {
            if (!IsAuthority || _participantRole == CoreParticipantRole.Client) return;
            if (_observerStartEpoch.TryGetValue(player, out long sent) && sent == _epoch) return;
            _observerStartEpoch[player] = _epoch;
            SendObserverStart(player, _epoch);
        }

        /// <summary>Presence from <paramref name="source"/>, who Photon named. The payload is not consulted.</summary>
        public void ReceiveObserverPresence(PlayerRef source)
        {
            if (source == PlayerRef.None || source == LocalPlayer) return;
            // An observer only remembers the other observer. It does not route a snapshot or send an app-start,
            // so it never counts that peer as a participant.
            if (_localObserver)
            {
                RememberObserver(source, out _);
                return;
            }
            NoteObserver(source);
            ObserverAnnounced?.Invoke(source);
        }

        /// <summary>This observer tells the room it is watching. The sender is <c>RpcInfo.Source</c>.</summary>
        public void AnnounceLocalObserver()
        {
            if (!_localObserver) return;
            // An RPC before the scene object is spawned throws inside Fusion.
            if (Object == null || !Object.IsValid) return;
            RpcObserverPresence();
        }

        public CoreParticipantRole ParticipantRole => _participantRole;
        public SpatialBlendConnectionState CoreLinkState => ConnectionState;

        /// <summary>Peers this participant currently has a core link to: the joiners of a host, the host of a joiner.</summary>
        public int LinkedPeerCount => _pumps.Count;

        /// <summary>The connection round this side is in (0 before any).</summary>
        public long CurrentEpoch => _epoch;

        /// <summary>Link messages dropped because their token is not one issued for the round (a spoof, a replay, or a stale round). Diagnostics and tests.</summary>
        public int RejectedLinkMessages => _tokens.Rejected;

        /// <summary>
        /// True after the host of the room left (its runner shut down under a joined client, its player left the room,
        /// or the core link to it ended): the UI says so. Nothing re-hosts: v1 had no host migration
        /// either. It clears when the participant joins or hosts again, or
        /// leaves the room itself.
        /// </summary>
        public bool HostLeft { get; private set; }
        private bool _leaving;

        public bool HasActiveRunner => Runner != null && Runner.IsRunning;
        public bool CanRetry => HasActiveRunner &&
                                (ConnectionState == SpatialBlendConnectionState.Disconnected ||
                                 ConnectionState == SpatialBlendConnectionState.Failed);

        public override void Spawned()
        {
            ResetLinkState();
            Subscribe();
            AttachReliableData();
            // Room join: ask for the TURN credentials now, well before the first link needs them.
            TurnProvider?.Ensure();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            RetireLocalRound(true);
            Unsubscribe();
            DetachReliableData();
            ResetLinkState();
            base.Despawned(runner, hasState);
        }

        /// <summary>
        /// The link state belongs to one runner: the epoch and the retired epoch, the link counter, the tokens, the early
        /// signals. A new runner (a rejoin, or a room whose host starts again at epoch 1) must not meet the last runner's
        /// retired epoch, which would drop every round of it as old.
        /// </summary>
        private void ResetLinkState()
        {
            CloseAllPumps();
            _epoch = 0;
            _retiredEpoch = 0;
            _idCounter = 0;
            _hostLinkId = 0;
            _hostLinkDirect = true;
            _hostPlayer = PlayerRef.None;
            _startBroadcastEpoch = -1;
            _startBroadcastRoster.Clear();
            _appsStartedEpoch = -1;
            _readyPlayers.Clear();
            _heardFrom.Clear();
            _observers.Clear();
            _observerRecord.Clear();
            _observersLeft.Clear();
            _observerStartEpoch.Clear();
            _queuedObserverStart = -1;
            _observerYieldedMaster = false;
            _signalCounter = 0;
            _tokens = new LinkTokens();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable()
        {
            RetireLocalRound(true);
            Unsubscribe();
            DetachReliableData();
        }

        private void Update()
        {
            YieldMasterIfObserver();
            _turn?.Tick();
            PumpLinks();
            if (!_awaitingConnection) return;
            // Expiry wins over a late state transition or deferred reconnect. Native
            // synchronous call cancellation is owned by SpatialBlendManager separately.
            if (Now >= _deadline)
            {
                RetireLocalRound(ConnectionState != SpatialBlendConnectionState.Failed);
                return;
            }
            if (_reconnectAfterDisconnect)
            {
                if (ConnectionState == SpatialBlendConnectionState.Disconnected || ConnectionState == SpatialBlendConnectionState.Failed)
                {
                    _reconnectAfterDisconnect = false;
                    JoinHost();
                }
                return;
            }

            if (ConnectionState == SpatialBlendConnectionState.Connected)
            {
                _awaitingConnection = false;
                SendReadyAck();
                ApplyQueuedObserverStart();
            }
            else if (ConnectionState == SpatialBlendConnectionState.Failed)
            {
                // The join did not complete over its link. This link is gone and the failure stays visible; the round is
                // the host's and goes on. A retry asks the host for a new link for this player only (RetryConnection).
                _awaitingConnection = false;
                _reconnectAfterDisconnect = false;
                _deadline = 0;
                CloseAllPumps();
            }
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void RpcJoinRoundEpoch(long epoch, RpcInfo info = default) => BeginRemoteConnection(epoch, info.Source);

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RpcReadyForEpoch(long epoch, RpcInfo info = default) => AcceptReadyAck(epoch, info.Source);

        // A joiner whose link ended, or who retries after a failed join, asks the host for another link: its own only.
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RpcRequestRelink(long observedEpoch, int observedLink, bool direct, RpcInfo info = default) =>
            HandleRelinkRequest(observedEpoch, observedLink, direct, info.Source);

        // The host tells ONE joiner to rejoin the current round over a new link. Everyone else ignores it.
        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void RpcRelinkPeer([RpcTarget] PlayerRef target, long epoch, int link, bool direct, RpcInfo info = default)
        {
            if (target != LocalPlayer) return;
            BeginRelink(epoch, link, direct, info.Source);
        }

        private void HandleRelinkRequest(long observedEpoch, int observedLink, bool direct, PlayerRef source)
        {
            // A retry concerns the requesting peer's own link in the authority's current round, and nothing else. An old
            // queued request, or one about a link that has since been replaced, must never disturb a healthy link, and
            // no request resets the round or the host's session.
            if (!IsAuthority || _participantRole == CoreParticipantRole.Client || !ActiveRoster.Contains(source) ||
                observedEpoch != _epoch) return;
            if (_pumps.TryGetValue(source, out var current) && current.Epoch != observedLink) return;
            RelinkPeer(source, direct);
        }

        // The capability handshake of the core link (see LinkTokens): a token for the target alone, over a channel whose
        // sender Photon stamps. The offerer is info.Source, never a parameter. Offers addressed to anyone else are ignored.
        [Rpc(RpcSources.All, RpcTargets.All)]
        private void RpcOfferLinkToken([RpcTarget] PlayerRef target, int epoch, long high, long low, RpcInfo info = default)
        {
            if (target != LocalPlayer || info.Source == LocalPlayer || info.Source == PlayerRef.None) return;
            AcceptTokenOffer(info.Source, epoch, high, low);
        }

        private void AcceptTokenOffer(PlayerRef source, int epoch, long high, long low)
        {
            var token = new byte[LinkTokens.Size];
            BitConverter.GetBytes(high).CopyTo(token, 0);
            BitConverter.GetBytes(low).CopyTo(token, 8);
            _tokens.AcceptOffer(epoch, source.RawEncoded, token);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void RpcStartAppsEpoch(long epoch) => StartAppsForEpoch(epoch);

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void RpcStartAppsFor([RpcTarget] PlayerRef target, long epoch, RpcInfo info = default)
        {
            if (target != LocalPlayer) return;
            StartAppsForEpoch(epoch);
        }

        // The sender is Photon's stamp, not a player id written into the body.
        [Rpc(RpcSources.All, RpcTargets.All)]
        private void RpcObserverPresence(RpcInfo info = default) => ReceiveObserverPresence(info.Source);

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void RpcRetireEpoch(long epoch) => HandleRetirement(epoch);

        private void HandleRetirement(long epoch)
        {
            // Delayed, duplicate, and locally looped-back retirements must not retire
            // a newer active round or erase a locally retained failure state.
            if (epoch < _epoch || epoch <= _retiredEpoch) return;
            _epoch = Math.Max(_epoch, epoch);
            _retiredEpoch = Math.Max(_retiredEpoch, epoch);
            RetireLocalRound(true);
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (!IsAuthority) return;
            // A player who joined an existing room stays a core client of that
            // room's host, even if Fusion elected this peer master.
            if (_participantRole == CoreParticipantRole.Client || _localObserver) return;
            if (ConnectionState == SpatialBlendConnectionState.Disconnected)
            {
                BeginHostRound();
            }
            else if (ConnectionState == SpatialBlendConnectionState.Connected)
            {
                // The newcomer gets its own core link before the round is announced to it: its first bytes
                // would otherwise reach a host that has nobody to hand them to.
                AdmitPeer(player);
                BroadcastConnectionRound();
            }
        }

        /// <summary>Applies a matchmaking decision. Leave tears the core link down.</summary>
        public void AdoptMatchmakingDecision(MatchmakingDecision decision)
        {
            HostLeft = false;
            if (decision.Role == CoreParticipantRole.None)
            {
                // The participant's own leave is not a host loss.
                _leaving = true;
                try { ShutdownConnection(); }
                finally { _leaving = false; }
                return;
            }

            // SpectatorJoinPolicy: an observer is a client of someone else's room. It does not host the core.
            if (_localObserver && decision.Role == CoreParticipantRole.Host)
            {
                _participantRole = CoreParticipantRole.Client;
                return;
            }
            _participantRole = decision.Role;
        }

        // Fusion invokes these on a spawned NetworkBehaviour; NetworkEvents uses the
        // runner-bearing overload above. Both routes share the idempotent round logic.
        public void PlayerJoined(PlayerRef player) => OnPlayerJoined(Runner, player);

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            // Taken from this manager's record before any other callback can run, and kept in a tombstone so a second
            // delivery of the same leave (NetworkEvents and IPlayerLeft) is still an observer leave.
            bool observerLeft = _observerRecord.Remove(player) || _observersLeft.Contains(player);
            if (observerLeft) _observersLeft.Add(player);
            _observers.Remove(player);
            _observerStartEpoch.Remove(player);
            // Host loss is decided by WHO left, never by who is master afterwards: Fusion promotes a remaining player to
            // master as the host leaves, so that joiner is the authority at exactly the moment this must be noticed.
            if (_participantRole == CoreParticipantRole.Client)
            {
                // The host's player left the room: its core is gone with it. Any other departure is not this joiner's
                // to act on: it has no link to that player and never owns the host's round.
                if (_hostPlayer != PlayerRef.None && player == _hostPlayer) HandleHostLoss();
                return;
            }
            if (!IsAuthority) return;

            // The host closes the leaver's core link: nothing else may keep a stream to a player who is gone.
            ClosePump(player);
            // The leaver is neither ready nor announced to any longer: whoever takes the place (even a player with the same
            // ref) must ack on its own link and be announced to. The start announcement of the others is not repeated for them.
            _readyPlayers.Remove(player);
            _startBroadcastRoster.Remove(player);
            // A departed unready participant can never satisfy the original roster, so a round that has not started its
            // apps is retired. An observer is not one of those participants: their leave leaves the round alone,
            // including a second callback that arrives after the record was consumed.
            // A round that already started keeps serving everyone who is still here.
            if (!observerLeft && _epoch > _retiredEpoch && _appsStartedEpoch != _epoch) RetireAuthorityRound();
        }

        public void PlayerLeft(PlayerRef player) => OnPlayerLeft(Runner, player);

        public void RetryConnection()
        {
            // A joiner stays a client even if Fusion later reports this peer as master.
            if (_participantRole == CoreParticipantRole.Client || !IsAuthority)
            {
                RequestRelinkFromAuthority(_epoch, _hostLinkId, true);
                return;
            }

            RetireAuthorityRound();
            BeginHostRound();
        }

        /// <summary>
        /// The runner's OnShutdown event lands here (the scene wires it). Under a joined client that is not leaving
        /// by its own decision, the host is gone: <see cref="HostLeft"/> is set and the link comes down.
        /// </summary>
        public virtual void ShutdownConnection()
        {
            bool hostLost = !_leaving && _participantRole == CoreParticipantRole.Client;
            _participantRole = CoreParticipantRole.None;
            _hostPlayer = PlayerRef.None;
            _retiredEpoch = Math.Max(_retiredEpoch, _epoch);
            RetireLocalRound(true);
            if (IsAuthority) BroadcastRetire(_retiredEpoch);
            if (hostLost) HostLeft = true;
        }

        // The host of a joined room is gone (its player left, or the core link to it ended). Nothing re-hosts.
        private void HandleHostLoss()
        {
            _participantRole = CoreParticipantRole.None;
            _hostPlayer = PlayerRef.None;
            _retiredEpoch = Math.Max(_retiredEpoch, _epoch);
            RetireLocalRound(true);
            HostLeft = true;
        }

        private void BeginHostRound()
        {
            HostLeft = false;
            _epoch = NextId();
            _readyPlayers.Clear();
            _startBroadcastEpoch = -1;
            _startBroadcastRoster.Clear();
            _appsStartedEpoch = -1;

            RequestHostTunneled();
            if (ConnectionState != SpatialBlendConnectionState.Connected)
            {
                RetireAuthorityRound(false); // Failed host never counts as ready.
                return;
            }

            _readyPlayers.Add(LocalPlayer);
            foreach (var player in ActiveRoster) AdmitPeer(player);
            BroadcastConnectionRound();
            TryStartApps();
        }

        // The next number of the host-wide counter that numbers rounds and links.
        private long NextId()
        {
            _idCounter = Math.Max(_idCounter, Math.Max(_epoch, _retiredEpoch)) + 1;
            return _idCounter;
        }

        // Host side: one core link per other player of the room, keyed by that player. The first link of a round is
        // numbered by the round; a relink gets a number of its own (see RelinkPeer).
        private void AdmitPeer(PlayerRef player, int linkId = -1, bool direct = true)
        {
            if (player == PlayerRef.None || player == LocalPlayer || _pumps.ContainsKey(player)) return;
            CoreLink link = RequestAcceptLink();
            if (link == null)
            {
                Debug.LogError($"Unable to attach a core link for player {player.RawEncoded}.");
                return;
            }
            _pumps[player] = NewPump(link, player, holdUntilPeerSpeaks: true, linkId < 0 ? (int)_epoch : linkId, direct);
        }

        /// <summary>
        /// Host side: replace ONE player's core link inside the current round. The old link is closed, the player is no
        /// longer counted ready, a new link with a number of its own is attached and only then is that player told to
        /// rejoin. Every other player's link, the round, and its started apps are untouched. This is what a retry
        /// and a direct channel lost after it opened both come to; core supports a joiner
        /// rejoining the same session.
        /// </summary>
        private void RelinkPeer(PlayerRef player, bool direct)
        {
            if (!IsAuthority || _participantRole == CoreParticipantRole.Client || ConnectionState != SpatialBlendConnectionState.Connected ||
                _epoch <= _retiredEpoch || player == PlayerRef.None || player == LocalPlayer || !ActiveRoster.Contains(player)) return;
            ClosePump(player);
            _readyPlayers.Remove(player);
            // The apps of the others keep running; the next full roster of acks announces the start again for this player.
            _startBroadcastEpoch = -1;
            _startBroadcastRoster.Clear();
            int link = (int)NextId();
            AdmitPeer(player, link, direct);
            if (!_pumps.ContainsKey(player)) return;
            SendRelink(player, _epoch, link, direct);
        }

        // Joiner side: the host replaced my link. Rejoin the same round over the new one.
        private void BeginRelink(long epoch, int link, bool direct, PlayerRef source)
        {
            if (epoch != _epoch || epoch <= 0 || source == PlayerRef.None || source != _hostPlayer || link <= _hostLinkId) return;
            // The host naming this round proves it is alive, whatever this side retired on its own failure.
            _retiredEpoch = Math.Min(_retiredEpoch, _epoch - 1);
            HostLeft = false;
            _hostLinkId = link;
            _hostLinkDirect = direct;
            // The native session below is replaced, which stops the apps; the host's next start announcement restarts them.
            _appsStartedEpoch = -1;
            _awaitingConnection = true;
            _deadline = Now + ConnectDeadlineSeconds;
            CloseAllPumps();
            if (ConnectionState == SpatialBlendConnectionState.Disconnected || ConnectionState == SpatialBlendConnectionState.Failed)
            {
                _reconnectAfterDisconnect = false;
                JoinHost();
                return;
            }
            _reconnectAfterDisconnect = true;
            RequestDisconnect();
        }

        private void BroadcastConnectionRound()
        {
            if (!IsAuthority || ConnectionState != SpatialBlendConnectionState.Connected) return;
            // The token offer is one RPC, and an RPC can be dropped: every announcement of the round offers again to a
            // peer that has not yet been heard from. No period is invented: the events are the announcements.
            ReofferTokensToSilentPeers();
            BroadcastJoinRound(_epoch);
        }

        // A link whose peer has not sent anything authenticated yet may be waiting on a lost offer (or a lost answer to
        // it): offer the same token again. Once the peer has been heard, its side has the token and nothing is repeated.
        private void ReofferTokensToSilentPeers()
        {
            foreach (KeyValuePair<PlayerRef, CoreLinkPump> entry in _pumps)
            {
                if (_heardFrom.Contains(entry.Key) || entry.Value.PeerHasSpoken) continue;
                byte[] inbound = _tokens.IssueFor(entry.Value.Epoch, entry.Key.RawEncoded);
                SendTokenOffer(entry.Key, entry.Value.Epoch, BitConverter.ToInt64(inbound, 0), BitConverter.ToInt64(inbound, 8));
            }
        }

        private void BeginRemoteConnection(long epoch, PlayerRef host)
        {
            if (epoch <= _retiredEpoch || epoch <= 0) return;
            if (epoch == _epoch)
            {
                // Duplicate transport announcements must not reset app-start state. They do repeat the token offer to a
                // host not yet heard from: the first offer may have been the RPC that was dropped.
                if (ConnectionState == SpatialBlendConnectionState.Connected)
                {
                    SendReadyAck();
                    ApplyQueuedObserverStart();
                }
                if (host == _hostPlayer) ReofferTokensToSilentPeers();
                return;
            }
            if (epoch < _epoch) return;

            _epoch = epoch;
            _idCounter = Math.Max(_idCounter, epoch);
            _hostLinkId = (int)epoch;
            _hostLinkDirect = true;
            HostLeft = false;
            _hostPlayer = host;
            _startBroadcastEpoch = -1;
            _startBroadcastRoster.Clear();
            _appsStartedEpoch = -1;
            _awaitingConnection = true;
            _deadline = Now + ConnectDeadlineSeconds;

            if (ConnectionState == SpatialBlendConnectionState.Connected)
            {
                if (IsAuthority)
                {
                    _awaitingConnection = false;
                    SendReadyAck();
                    return;
                }

                // A new round must not acknowledge the old native connection.
                _reconnectAfterDisconnect = true;
                RequestDisconnect();
                return;
            }
            if (ConnectionState == SpatialBlendConnectionState.Connecting)
            {
                _reconnectAfterDisconnect = true;
                RequestDisconnect();
                return;
            }

            _reconnectAfterDisconnect = false;
            JoinHost();
            ApplyQueuedObserverStart();
        }

        // Joiner side: the core link to the host of this round. The join completes only while the link is pumped.
        private void JoinHost()
        {
            CloseAllPumps();
            CoreLink link = RequestJoinTunneled();
            if (link == null) return; // ConnectionState reports the failure
            _pumps[_hostPlayer] = NewPump(link, _hostPlayer, holdUntilPeerSpeaks: false, _hostLinkId, _hostLinkDirect);
        }

        private void SendReadyAck()
        {
            if (_localObserver) return;
            if (ConnectionState != SpatialBlendConnectionState.Connected || _epoch <= _retiredEpoch) return;
            if (IsAuthority) AcceptReadyAck(_epoch, LocalPlayer);
            else SendReadyToAuthority(_epoch);
        }

        private void AcceptReadyAck(long epoch, PlayerRef source)
        {
            if (!IsAuthority || epoch != _epoch || epoch <= _retiredEpoch ||
                ConnectionState != SpatialBlendConnectionState.Connected || !ActiveRoster.Contains(source)) return;
            if (_observers.Contains(source)) return;
            _readyPlayers.Add(source);
            TryStartApps();
        }

        private void TryStartApps()
        {
            if (!IsAuthority || _localObserver || ConnectionState != SpatialBlendConnectionState.Connected) return;
            FillParticipants(_participants);
            if (_participants.Count < PlayersToStartApps) return;
            for (int i = 0; i < _participants.Count; ++i)
            {
                if (!_readyPlayers.Contains(_participants[i])) return;
            }
            if (_startBroadcastEpoch == _epoch && _startBroadcastRoster.SetEquals(_participants)) return;
            _startBroadcastEpoch = _epoch;
            _startBroadcastRoster.Clear();
            _startBroadcastRoster.UnionWith(_participants);
            BroadcastStartApps(_epoch);
        }

        private void FillParticipants(List<PlayerRef> into)
        {
            into.Clear();
            List<PlayerRef> roster = ActiveRoster;
            for (int i = 0; i < roster.Count; ++i)
            {
                if (_observers.Contains(roster[i])) continue;
                into.Add(roster[i]);
            }
        }

        private void StartAppsForEpoch(long epoch)
        {
            if (epoch <= _retiredEpoch || _appsStartedEpoch == epoch) return;
            // A late observer can be told to start before its core link is up, or before the join round sets the epoch.
            // Hold that epoch and apply it once both match. A participant still needs both at the moment of the call.
            if (_localObserver && (epoch != _epoch || ConnectionState != SpatialBlendConnectionState.Connected))
            {
                _queuedObserverStart = epoch;
                return;
            }
            if (epoch != _epoch || ConnectionState != SpatialBlendConnectionState.Connected) return;
            _queuedObserverStart = -1;
            _appsStartedEpoch = epoch;
            StartManagedApps();
        }

        private void ApplyQueuedObserverStart()
        {
            if (!_localObserver || _queuedObserverStart < 0 || _queuedObserverStart != _epoch) return;
            if (ConnectionState != SpatialBlendConnectionState.Connected) return;
            long epoch = _queuedObserverStart;
            _queuedObserverStart = -1;
            StartAppsForEpoch(epoch);
        }

        private void YieldMasterIfObserver()
        {
            // Shared-mode master is the only peer who can send the core's state-authority RPCs. An observer who is
            // that master (they created the room, or Fusion promoted them) shuts the runner down. A participant
            // who becomes master does not re-host.
            if (!_localObserver || !IsAuthority)
            {
                _observerYieldedMaster = false;
                return;
            }
            if (_observerYieldedMaster) return;
            if (Runner != null && !Runner.IsRunning) return;
            _observerYieldedMaster = true;
            RequestRunnerShutdown();
        }

        private void RetireAuthorityRound(bool disconnect = true)
        {
            _epoch = NextId();
            _retiredEpoch = _epoch;
            _readyPlayers.Clear();
            BroadcastRetire(_retiredEpoch);
            RetireLocalRound(disconnect);
        }

        private void RetireLocalRound(bool disconnect)
        {
            _retiredEpoch = Math.Max(_retiredEpoch, _epoch);
            _awaitingConnection = false;
            _reconnectAfterDisconnect = false;
            _deadline = 0;
            _readyPlayers.Clear();
            _startBroadcastEpoch = -1;
            _startBroadcastRoster.Clear();
            _appsStartedEpoch = -1;
            _queuedObserverStart = -1;
            CloseAllPumps();
            if (disconnect) RequestDisconnect();
        }

        // ---- The carrier: core link bytes as Fusion reliable data ---------------------------------------------------

        // One pump per peer, over that peer's carrier: the direct WebRTC channel when it comes up, the Photon wire otherwise.
        // The host (holdUntilPeerSpeaks) offers; the joiner answers.
        // linkId names this link on the wire and in its tokens: the round's epoch for the first link, a number the host
        // assigned for a relink. direct=false (after a direct channel was lost) leaves the link on the Photon carrier.
        private CoreLinkPump NewPump(CoreLink link, PlayerRef peer, bool holdUntilPeerSpeaks, int linkId, bool direct)
        {
            _tokens.RetireBelow((int)_epoch);
            // This peer may send to me only with the token I give it now, by a message only it receives.
            var inbound = _tokens.IssueFor(linkId, peer.RawEncoded);
            SendTokenOffer(peer, linkId, BitConverter.ToInt64(inbound, 0), BitConverter.ToInt64(inbound, 8));
            if (direct) TurnProvider?.Ensure();
            IPeerDataTransport transport = direct ? CreatePeerTransport(peer) : null;
            if (transport != null)
                transport.Signal += json => _tokens.WithToken(linkId, peer.RawEncoded, token => SendSignal(peer, linkId, token, json));
            // A link made while the TURN fetch is still pending waits for it (the transport starts when it is done or has
            // timed out), so the direct path's own budget starts after that wait, not inside it.
            float iceBudget = IceTimeoutSeconds + (transport != null && _turn != null ? (float)_turn.PendingRemainingSeconds : 0f);
            var carrier = new PeerCoreLinkCarrier(new PeerWire(this, peer, linkId), transport, () => Now, iceBudget,
                offerer: holdUntilPeerSpeaks, MaxChunkBytes);
            carrier.Chunk += (epoch, sequence, bytes) => ReceiveLinkChunk(peer, epoch, sequence, bytes);
            _carriers[peer] = carrier;
            return new CoreLinkPump(link, peer.RawEncoded, linkId, carrier, holdUntilPeerSpeaks, MaxChunkBytes);
        }

        /// <summary>A signaling message (SDP or ICE) for the direct carrier of the link to <paramref name="from"/>.</summary>
        public void ReceiveSignal(PlayerRef from, int epoch, string json)
        {
            if (_pumps.TryGetValue(from, out var current) && current.Epoch == epoch) _heardFrom.Add(from);
            // A signal passed the token check, so this side issued that peer a token for this link, and the carrier is made
            // in the same step as the token (NewPump): it exists. A signal for a link that is not the current one, or for a
            // Photon-only link with no transport, has nowhere to go and is dropped: nothing is kept for a later round, so
            // nothing grows (a protocol bound, with no buffer to bound).
            if (_carriers.TryGetValue(from, out var carrier) && carrier.Transport != null &&
                _pumps.TryGetValue(from, out var linked) && epoch == linked.Epoch)
                carrier.Transport.ReceiveSignal(json);
        }

        /// <summary>The carrier the core link is on: direct when every linked peer's channel is open.</summary>
        public CoreLinkCarrier ActiveCarrier =>
            _carriers.Count > 0 && _carriers.Values.All(c => c.Active == CoreLinkCarrier.WebRtc)
                ? CoreLinkCarrier.WebRtc : CoreLinkCarrier.Photon;

        /// <summary>
        /// The path the core link's bytes take: Photon, a direct WebRTC pair, or a TURN relay (with its transport). Unknown
        /// while negotiating or before the selected pair is read. With several links, a relayed one is reported.
        /// </summary>
        public CoreLinkPath ActivePath
        {
            get
            {
                if (_carriers.Count == 0) return CoreLinkPath.Unknown;
                if (ActiveCarrier == CoreLinkCarrier.Photon) return _carriers.Values.Any(c => c.Negotiating) ? CoreLinkPath.Unknown : CoreLinkPath.Photon;
                var paths = _carriers.Values.Select(c => c.Path).ToList();
                var relayed = paths.FirstOrDefault(p => p.Kind == CoreLinkPathKind.TurnRelay);
                return relayed.Kind == CoreLinkPathKind.TurnRelay ? relayed : paths[0];
            }
        }

        /// <summary>Seconds the slowest direct channel took to open, from the start of its negotiation; 0 when none has opened.</summary>
        public double DirectOpenSeconds => _carriers.Count == 0 ? 0 : _carriers.Values.Max(c => c.OpenedAfterSeconds);

        /// <summary>One line for the session panel: which carrier is active, and why the direct one is not.</summary>
        public string CarrierText
        {
            get
            {
                if (_carriers.Count == 0) return string.Empty;
                if (ActiveCarrier == CoreLinkCarrier.WebRtc)
                {
                    var path = ActivePath;
                    return path.Kind == CoreLinkPathKind.TurnRelay
                        ? "Carrier: WebRTC via TURN relay (" + path.Relay.ToString().ToLowerInvariant() + ")"
                        : "Carrier: direct (WebRTC)";
                }
                if (_carriers.Values.Any(c => c.Negotiating)) return "Carrier: negotiating a direct path";
                var fell = _carriers.Values.FirstOrDefault(c => c.FellBack);
                return fell != null ? "Carrier: Photon relay (fallback: " + fell.FallbackReason + ")" : "Carrier: Photon relay";
            }
        }

        // One peer's link on the Photon wire: its chunks go out behind the token that peer offered for this link, and
        // the pump reads nothing more from core until the peer has offered one (CanAccept), so no chunk waits in a queue
        // of ours for an offer that may never come.
        private sealed class PeerWire : IBackpressuredWire
        {
            private readonly FusionConnectionManager _owner;
            private readonly PlayerRef _peer;
            private readonly int _link;
            public PeerWire(FusionConnectionManager owner, PlayerRef peer, int link) { _owner = owner; _peer = peer; _link = link; }
            public bool CanAccept => _owner._tokens.HasOffer(_link, _peer.RawEncoded);
            public void Send(int peer, int epoch, int sequence, byte[] bytes) => ((ICoreLinkWire)_owner).Send(peer, epoch, sequence, bytes);
        }

        // The path is logged once per link, for diagnosing a venue: no credential, address or candidate is in the line.
        private void ReportPathOnce(PlayerRef peer, PeerCoreLinkCarrier carrier)
        {
            if (carrier.PathReported) return;
            var path = carrier.Path;
            if (path.Kind == CoreLinkPathKind.Unknown) return;
            carrier.PathReported = true;
            string detail = path.Kind == CoreLinkPathKind.Photon
                ? (carrier.FellBack ? " (" + carrier.FallbackReason + ")" : string.Empty)
                : ", direct channel opened in " + carrier.OpenedAfterSeconds.ToString("0.00") + " s";
            Debug.Log("[FusionConnectionManager] core link to player " + peer.RawEncoded + ": path=" + path + detail);
        }

        private void PumpLinks()
        {
            foreach (var carrier in _carriers.Values.ToList()) carrier.Tick();
            foreach (var entry in _carriers) ReportPathOnce(entry.Key, entry.Value);
            if (_pumps.Count == 0) return;
            List<PlayerRef> ended = null;
            foreach (var entry in _pumps)
            {
                // A direct channel that failed after it opened took chunks with it: the link has ended.
                if (_carriers.TryGetValue(entry.Key, out var carrier) && carrier.Lost) entry.Value.Lose();
                entry.Value.Pump();
                if (entry.Value.Ended) (ended ??= new List<PlayerRef>()).Add(entry.Key);
            }
            if (ended == null) return;
            foreach (var player in ended)
            {
                if (!_pumps.TryGetValue(player, out var pump)) continue;
                int link = pump.Epoch;
                bool carrierLost = pump.CarrierLost;
                // The host's end of a joiner's link ending is that joiner gone (PlayerLeft closes it). The joiner's end
                // ending is the host gone, unless the carrier lost it.
                bool hostLink = _participantRole == CoreParticipantRole.Client && player == _hostPlayer;
                ClosePump(player);
                if (_leaving) continue;
                if (carrierLost)
                {
                    LinkCarrierLost(player, link, hostLink);
                    continue;
                }
                if (hostLink && ConnectionState == SpatialBlendConnectionState.Connected) HandleHostLoss();
            }
        }

        // The link to a peer ended because its direct channel failed after it opened. The link ending is the
        // loss signal and there is no retransmission layer: the joiner rejoins the same session over the Photon
        // carrier. The host replaces the link itself; a joiner asks the host, and says so within the connect deadline.
        private void LinkCarrierLost(PlayerRef player, int link, bool hostLink)
        {
            if (IsAuthority && _participantRole != CoreParticipantRole.Client)
            {
                RelinkPeer(player, direct: false);
                return;
            }
            if (!hostLink || ConnectionState != SpatialBlendConnectionState.Connected) return;
            _awaitingConnection = true;
            _deadline = Now + ConnectDeadlineSeconds;
            RequestRelinkFromAuthority(_epoch, link, false);
        }

        private void ClosePump(PlayerRef player)
        {
            _heardFrom.Remove(player);
            if (_carriers.TryGetValue(player, out var carrier)) { _carriers.Remove(player); carrier.Dispose(); }
            if (!_pumps.TryGetValue(player, out var pump)) return;
            _pumps.Remove(player);
            pump.Close();
        }

        private void CloseAllPumps()
        {
            _heardFrom.Clear();
            foreach (var carrier in _carriers.Values) carrier.Dispose();
            _carriers.Clear();
            if (_pumps.Count == 0) return;
            var pumps = _pumps.Values.ToList();
            _pumps.Clear();
            foreach (var pump in pumps) pump.Close();
        }

        /// <summary>One chunk from a peer, off the Fusion callback. Chunks of a past round, or from a stranger, are ignored.</summary>
        public void ReceiveLinkChunk(PlayerRef from, int epoch, int sequence, byte[] bytes)
        {
            if (_pumps.TryGetValue(from, out var pump) && pump.Epoch == epoch) _heardFrom.Add(from);
            if (_pumps.TryGetValue(from, out pump)) pump.Receive(epoch, sequence, bytes);
        }

        void ICoreLinkWire.Send(int peer, int epoch, int sequence, byte[] bytes) =>
            _tokens.WithToken(epoch, peer, token =>
                SendReliable(PlayerRef.FromEncoded(peer), FusionCoreLinkKey.Make(epoch, sequence), LinkTokens.Frame(token, bytes)));

        private void AttachReliableData()
        {
            var runner = Runner;
            if (runner == null || runner == _callbacksRunner) return;
            DetachReliableData();
            _reliableReceiver = new FusionCoreLinkReceiver(ReceiveLinkChunk, ReceiveSignal, _tokens);
            runner.AddCallbacks(_reliableReceiver);
            _callbacksRunner = runner;
        }

        private void DetachReliableData()
        {
            if (_callbacksRunner != null && _reliableReceiver != null) _callbacksRunner.RemoveCallbacks(_reliableReceiver);
            _callbacksRunner = null;
            _reliableReceiver = null;
        }

        private void Subscribe()
        {
            if (_subscribed || _spaceManager == null) return;
            _spaceManager.ConnectionStateChanged += OnConnectionStateChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _spaceManager == null) return;
            _spaceManager.ConnectionStateChanged -= OnConnectionStateChanged;
            _subscribed = false;
        }

        private void OnConnectionStateChanged(SpatialBlendConnectionState state)
        {
            // A failed join is handled in Update, in the same order as the round deadline.
        }

        protected virtual SpatialBlendConnectionState ConnectionState => _spaceManager == null ? SpatialBlendConnectionState.Disconnected : _spaceManager.ConnectionState;

        // Declared config: SpatialBlendSettings holds the values, with the
        // previous constants as their defaults. A missing or out-of-range value falls back to the default.
        protected virtual SpatialBlendSettings DeclaredConfig => SpatialBlendManager.CurrentSettings;
        // A value outside its range is a visible error naming the field, once, and the default is used so the room still
        // works; absent settings are not an error (the defaults are the declared config then). Before, it fell back silently.
        private readonly HashSet<string> _reportedConfigErrors = new HashSet<string>();
        private T InRange<T>(string field, T value, bool valid, T fallback)
        {
            if (valid) return value;
            if (_reportedConfigErrors.Add(field))
                Debug.LogError($"[FusionConnectionManager] SpatialBlendSettings.{field} = {value} is out of range; using the default {fallback}.");
            return fallback;
        }
        protected virtual float ConnectDeadlineSeconds =>
            DeclaredConfig == null ? SpatialBlendSettings.DefaultConnectDeadlineSeconds
                : InRange("connectDeadlineSeconds", DeclaredConfig.connectDeadlineSeconds,
                          DeclaredConfig.connectDeadlineSeconds >= 1f, SpatialBlendSettings.DefaultConnectDeadlineSeconds);
        protected virtual int PlayersToStartApps =>
            DeclaredConfig == null ? SpatialBlendSettings.DefaultPlayersToStartApps
                : InRange("playersToStartApps", DeclaredConfig.playersToStartApps,
                          DeclaredConfig.playersToStartApps >= 1, SpatialBlendSettings.DefaultPlayersToStartApps);
        protected virtual int MaxChunkBytes => CoreLinkPump.DefaultMaxChunkBytes;
        protected virtual bool UseWebRtc => DeclaredConfig == null || DeclaredConfig.useWebRtc;
        protected virtual float IceTimeoutSeconds =>
            DeclaredConfig == null ? SpatialBlendSettings.DefaultWebRtcIceTimeoutSeconds
                : InRange("webRtcIceTimeoutSeconds", DeclaredConfig.webRtcIceTimeoutSeconds,
                          DeclaredConfig.webRtcIceTimeoutSeconds >= 1f, SpatialBlendSettings.DefaultWebRtcIceTimeoutSeconds);
        protected virtual IEnumerable<string> StunServers =>
            DeclaredConfig != null && DeclaredConfig.stunServers != null
                ? DeclaredConfig.stunServers.Where(s => !string.IsNullOrWhiteSpace(s))
                : new[] { SpatialBlendSettings.DefaultStunServer };
        /// <summary>The direct transport for the link to <paramref name="peer"/>; null leaves the Photon carrier alone.</summary>
        protected virtual IPeerDataTransport CreatePeerTransport(PlayerRef peer) =>
            UseWebRtc ? new WebRtcPeerTransport(StunServers, RelayOnly, TurnProvider, TurnTransportsAllowed) : null;
        protected virtual TurnTransports TurnTransportsAllowed => DeclaredConfig != null ? DeclaredConfig.turnTransports : TurnTransports.All;

        // The TURN credentials (tools/turn-credentials-worker). Absent URL or client secret leaves the carrier on STUN as before.
        private TurnIceProvider _turn;
        private bool _turnResolved;
        protected TurnIceProvider TurnProvider
        {
            get
            {
                if (!_turnResolved && UseWebRtc)
                {
                    _turnResolved = true;
                    _turn = CreateTurnProvider();
                }
                return _turn;
            }
        }
        /// <summary>
        /// The fetch may take 0.8 of the ICE budget (4 s by default). Measured from the Mac to the deployed Worker, six
        /// fetches took 0.87 to 2.01 s (an HTTPS request: DNS, TCP, TLS, then the Worker's own call to Cloudflare), so 4 s is
        /// twice the slowest, the same headroom the 5 s ICE budget has over its measured 2.4 s. It runs from the room join,
        /// ahead of the first link, so it is normally done before a link needs it. A link made while it is still pending waits
        /// for it, and its direct-path budget is extended by that wait (at most 4 + 5 s inside the 15 s round). A failed
        /// fetch is not retried within a connection round, and a cached answer is replaced a round before its ttl ends.
        /// </summary>
        protected virtual TurnIceProvider CreateTurnProvider()
        {
            var url = TurnWorkerUrl.Resolve(DeclaredConfig != null ? DeclaredConfig.turnCredentialsUrl : SpatialBlendSettings.DefaultTurnCredentialsUrl);
            var secret = TurnClientSecret.Load();
            if (!TurnIceProvider.IsConfigured(url, secret)) return null;
            // The process-wide provider, normally already warm from the app-start prefetch (TurnIceService.Boot).
            return TurnIceService.GetOrCreate(url, secret, IceTimeoutSeconds * 0.8f, ConnectDeadlineSeconds, ConnectDeadlineSeconds,
                message => Debug.Log("[FusionConnectionManager] " + message));
        }
        protected virtual bool RelayOnly => DeclaredConfig != null && DeclaredConfig.webRtcRelayOnly;
        protected virtual void SendSignal(PlayerRef peer, int epoch, byte[] token, string json)
        {
            if (Runner == null) return;
            Runner.SendReliableDataToPlayer(peer, FusionCoreLinkSignalKey.Make(epoch, ++_signalCounter),
                LinkTokens.Frame(token, System.Text.Encoding.UTF8.GetBytes(json)));
        }
        protected virtual void SendTokenOffer(PlayerRef peer, int epoch, long high, long low) => RpcOfferLinkToken(peer, epoch, high, low);
        protected virtual double Now => Time.realtimeSinceStartupAsDouble;
        protected virtual bool IsAuthority => Runner != null && Runner.IsSharedModeMasterClient;
        protected virtual PlayerRef LocalPlayer => Runner == null ? PlayerRef.None : Runner.LocalPlayer;
        protected virtual List<PlayerRef> ActiveRoster => Runner == null ? new List<PlayerRef>() : Runner.ActivePlayers.ToList();
        protected virtual void RequestHostTunneled() { if (_spaceManager != null) _spaceManager.HostTunneled(); }
        protected virtual CoreLink RequestAcceptLink() => _spaceManager == null ? null : _spaceManager.AcceptCoreLink();
        protected virtual CoreLink RequestJoinTunneled() => _spaceManager == null ? null : _spaceManager.JoinTunneled();
        protected virtual void RequestDisconnect() { if (_spaceManager != null) _spaceManager.Disconnect(); }
        protected virtual void StartManagedApps() { if (_spaceManager != null) _spaceManager.StartApps(); }
        protected virtual void SendReliable(PlayerRef player, ReliableKey key, byte[] bytes)
        {
            if (Runner != null) Runner.SendReliableDataToPlayer(player, key, bytes);
        }
        protected virtual void BroadcastJoinRound(long epoch) => RpcJoinRoundEpoch(epoch);
        protected virtual void SendReadyToAuthority(long epoch) => RpcReadyForEpoch(epoch);
        protected virtual void RequestRelinkFromAuthority(long observedEpoch, int observedLink, bool direct) =>
            RpcRequestRelink(observedEpoch, observedLink, direct);
        protected virtual void SendRelink(PlayerRef player, long epoch, int link, bool direct) => RpcRelinkPeer(player, epoch, link, direct);
        protected virtual void BroadcastStartApps(long epoch) => RpcStartAppsEpoch(epoch);
        protected virtual void SendObserverStart(PlayerRef player, long epoch) => RpcStartAppsFor(player, epoch);
        protected virtual void RequestRunnerShutdown()
        {
            if (Runner == null || !Runner.IsRunning) return;
            Runner.Shutdown();
        }
        // A runner that is shutting down has no peers left to tell, and an RPC on it throws inside Fusion.
        protected virtual void BroadcastRetire(long epoch)
        {
            if (Runner == null || Runner.IsShutdown || !Runner.IsRunning) return;
            RpcRetireEpoch(epoch);
        }
    }
}
