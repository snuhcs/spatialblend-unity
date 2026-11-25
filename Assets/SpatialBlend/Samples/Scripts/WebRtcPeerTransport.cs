// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.WebRTC;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// <see cref="IPeerDataTransport"/> over a WebRTC data channel (com.unity.webrtc): reliable and ordered (the data
    /// channel defaults). NAT traversal is by STUN, plus TURN (UDP, TCP or TLS) when a <see cref="TurnIceProvider"/> has
    /// credentials; a pair that cannot connect either way fails and the caller falls back to the Photon carrier. SDP and
    /// ICE candidates leave through <see cref="Signal"/> as small JSON strings. Once open, the selected candidate pair is
    /// read from <c>GetStats</c> into <see cref="Path"/>.
    /// </summary>
    public sealed class WebRtcPeerTransport : IPeerDataTransport, IPathReporting
    {
        public const string ChannelLabel = "spatialblend-core";

        [Serializable]
        private sealed class SignalMessage
        {
            public string t;      // "offer", "answer" or "ice"
            public string sdp;    // offer / answer
            public string cand;   // ice
            public string mid;
            public int line = -1;
        }

        /// <summary>The most signals kept while the TURN credentials are still being fetched (an offer, an answer and the candidates of a link are a few dozen).</summary>
        private const int MaxEarlySignals = 128;
        /// <summary>Reads of the selected pair before the path stays Unknown, half a second apart.</summary>
        private const int MaxPathReads = 10;
        private const double PathReadSpacingSeconds = 0.5;

        private readonly string[] _stunServers;
        private readonly bool _relayOnly;
        private readonly TurnIceProvider _turn;
        private readonly TurnTransports _turnTransports;
        private bool _waitingForIce;
        private readonly List<string> _earlySignals = new List<string>();
        private RTCStatsReportAsyncOperation _statsOp;
        private int _pathReads;
        private double _nextPathRead;
        private RTCPeerConnection _pc;
        private RTCDataChannel _channel;
        private bool _offerer;
        private RTCSessionDescriptionAsyncOperation _createOp;
        private RTCSetSessionDescriptionAsyncOperation _setOp;
        private Action _afterSet;
        private bool _remoteSet;
        private readonly List<RTCIceCandidateInit> _earlyIce = new List<RTCIceCandidateInit>();
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly Queue<string> _outbox = new Queue<string>();
        private bool _disposed;
        private bool _released;

        /// <summary>True once the native peer connection and channel have been closed and disposed (a failed connection is not kept alive until the link closes).</summary>
        internal bool NativeReleased => _released;

        /// <param name="relayOnly">ICE may only use relay candidates (SpatialBlendSettings.webRtcRelayOnly). With no TURN server none exist, so ICE fails.</param>
        /// <param name="turn">Where the TURN credentials come from; null or without credentials leaves STUN alone. While its fetch is pending the connection waits for it (the caller bounds that wait).</param>
        /// <param name="turnTransports">The TURN transports to offer.</param>
        public WebRtcPeerTransport(IEnumerable<string> stunServers, bool relayOnly = false, TurnIceProvider turn = null,
            TurnTransports turnTransports = TurnTransports.All)
        {
            _stunServers = new List<string>(stunServers ?? Array.Empty<string>()).ToArray();
            _relayOnly = relayOnly;
            _turn = turn;
            _turnTransports = turnTransports;
        }

        /// <summary>The selected candidate pair as a path: Unknown until read, which happens once, soon after the channel opens.</summary>
        public CoreLinkPath Path { get; private set; } = CoreLinkPath.Unknown;

        /// <summary>
        /// The configuration for one connection: the STUN list, then the TURN servers (with their credentials) the
        /// <paramref name="transports"/> allow, and the candidate policy. Public so the shape can be checked without a native connection.
        /// </summary>
        public static RTCConfiguration BuildConfiguration(IEnumerable<string> stunServers, IEnumerable<IceServerConfig> turnServers,
            TurnTransports transports, bool relayOnly)
        {
            var servers = new List<RTCIceServer>();
            var stun = (stunServers ?? Array.Empty<string>()).ToArray();
            if (stun.Length > 0) servers.Add(new RTCIceServer { urls = stun });
            if (turnServers != null)
                foreach (var s in TurnIceResponse.Filter(turnServers, transports))
                    servers.Add(new RTCIceServer { urls = s.Urls, username = s.Username, credential = s.Credential });
            return new RTCConfiguration
            {
                iceServers = servers.ToArray(),
                iceTransportPolicy = relayOnly ? RTCIceTransportPolicy.Relay : RTCIceTransportPolicy.All,
            };
        }

        public PeerTransportState State { get; private set; } = PeerTransportState.Idle;
        public long BufferedBytes => _channel != null && State == PeerTransportState.Open ? (long)_channel.BufferedAmount : 0;
        public string Failure { get; private set; }
        public event Action<string> Signal;
        public event Action<byte[]> Message;

        public void Start(bool offerer)
        {
            _offerer = offerer;
            State = PeerTransportState.Negotiating;
            if (_turn != null && _turn.Pending) { _waitingForIce = true; return; }
            BeginConnection();
        }

        private void BeginConnection()
        {
            try
            {
                var config = BuildConfiguration(_stunServers, _turn?.Servers, _turnTransports, _relayOnly);
                _pc = new RTCPeerConnection(ref config);
                _pc.OnIceCandidate = candidate =>
                {
                    if (candidate == null || string.IsNullOrEmpty(candidate.Candidate)) return;
                    _outbox.Enqueue(JsonUtility.ToJson(new SignalMessage
                    {
                        t = "ice", cand = candidate.Candidate, mid = candidate.SdpMid, line = candidate.SdpMLineIndex ?? -1,
                    }));
                };
                _pc.OnIceConnectionChange = state =>
                {
                    if (state == RTCIceConnectionState.Failed) Fail("ICE failed");
                };
                _pc.OnDataChannel = channel => Attach(channel);
                if (_offerer)
                {
                    Attach(_pc.CreateDataChannel(ChannelLabel, new RTCDataChannelInit { ordered = true }));
                    _createOp = _pc.CreateOffer();
                }
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        private void Attach(RTCDataChannel channel)
        {
            _channel = channel;
            channel.OnMessage = bytes => _inbox.Enqueue(bytes);
            channel.OnOpen = () => { if (State == PeerTransportState.Negotiating) State = PeerTransportState.Open; };
            channel.OnClose = () =>
            {
                if (State == PeerTransportState.Open || State == PeerTransportState.Negotiating) State = PeerTransportState.Closed;
            };
            channel.OnError = error => Fail(error.message);
            if (channel.ReadyState == RTCDataChannelState.Open && State == PeerTransportState.Negotiating) State = PeerTransportState.Open;
        }

        public void ReceiveSignal(string json)
        {
            if (State == PeerTransportState.Failed || State == PeerTransportState.Closed) return;
            if (_pc == null)
            {
                // The peer started first while the TURN credentials are still on their way: keep its signals, bounded.
                if (_waitingForIce && _earlySignals.Count < MaxEarlySignals) _earlySignals.Add(json);
                return;
            }
            SignalMessage message;
            try { message = JsonUtility.FromJson<SignalMessage>(json); }
            catch (Exception) { return; }
            if (message == null) return;
            try
            {
                switch (message.t)
                {
                    case "offer" when !_offerer:
                        BeginSetRemote(RTCSdpType.Offer, message.sdp, () => { _createOp = _pc.CreateAnswer(); });
                        break;
                    case "answer" when _offerer:
                        BeginSetRemote(RTCSdpType.Answer, message.sdp, null);
                        break;
                    case "ice":
                        var init = new RTCIceCandidateInit
                        {
                            candidate = message.cand, sdpMid = message.mid, sdpMLineIndex = message.line < 0 ? (int?)null : message.line,
                        };
                        if (_remoteSet) _pc.AddIceCandidate(new RTCIceCandidate(init));
                        else _earlyIce.Add(init);
                        break;
                }
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        private void BeginSetRemote(RTCSdpType type, string sdp, Action then)
        {
            var desc = new RTCSessionDescription { type = type, sdp = sdp };
            _setOp = _pc.SetRemoteDescription(ref desc);
            _afterSet = () =>
            {
                _remoteSet = true;
                foreach (var init in _earlyIce) _pc.AddIceCandidate(new RTCIceCandidate(init));
                _earlyIce.Clear();
                then?.Invoke();
            };
        }

        public void Tick()
        {
            if (_disposed) return;
            if (_waitingForIce && !_turn.Pending)
            {
                _waitingForIce = false;
                BeginConnection();
                var early = _earlySignals.ToArray();
                _earlySignals.Clear();
                foreach (var signal in early) ReceiveSignal(signal);
            }
            // A connection that failed or closed has nothing left to do: free the native objects now, off the library's
            // own callback (this is the main thread), instead of keeping them alive until the link closes.
            if ((State == PeerTransportState.Failed || State == PeerTransportState.Closed) && !_released)
            {
                ReleaseNative();
                return;
            }
            if (_released) return;
            try
            {
                if (_createOp != null && _createOp.IsDone)
                {
                    var op = _createOp;
                    _createOp = null;
                    if (op.IsError) { Fail(op.Error.message); }
                    else
                    {
                        var desc = op.Desc;
                        var type = desc.type == RTCSdpType.Offer ? "offer" : "answer";
                        var sdp = desc.sdp;
                        _setOp = _pc.SetLocalDescription(ref desc);
                        _afterSet = () => _outbox.Enqueue(JsonUtility.ToJson(new SignalMessage { t = type, sdp = sdp }));
                    }
                }
                if (_setOp != null && _setOp.IsDone)
                {
                    var op = _setOp;
                    var after = _afterSet;
                    _setOp = null;
                    _afterSet = null;
                    if (op.IsError) Fail(op.Error.message);
                    else after?.Invoke();
                }
                while (_outbox.Count > 0) Signal?.Invoke(_outbox.Dequeue());
                while (_inbox.Count > 0) Message?.Invoke(_inbox.Dequeue());
                if (State == PeerTransportState.Negotiating && _pc != null &&
                    _pc.IceConnectionState == RTCIceConnectionState.Failed) Fail("ICE failed");
                ReadPath();
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        /// <summary>Reads the selected candidate pair once the channel is open: transport.selectedCandidatePairId, then that pair's two candidates.</summary>
        private void ReadPath()
        {
            if (State != PeerTransportState.Open || _pc == null || Path.Kind != CoreLinkPathKind.Unknown) return;
            if (_statsOp != null)
            {
                if (!_statsOp.IsDone) return;
                var op = _statsOp;
                _statsOp = null;
                if (!op.IsError && op.Value != null)
                {
                    try { Path = PathOf(op.Value); }
                    finally { op.Value.Dispose(); }
                }
                if (Path.Kind != CoreLinkPathKind.Unknown) return;
                _nextPathRead = Time.realtimeSinceStartupAsDouble + PathReadSpacingSeconds;
                return;
            }
            if (_pathReads >= MaxPathReads || Time.realtimeSinceStartupAsDouble < _nextPathRead) return;
            _pathReads++;
            _statsOp = _pc.GetStats();
        }

        private static CoreLinkPath PathOf(RTCStatsReport report)
        {
            RTCIceCandidatePairStats pair = null;
            foreach (var stat in report.Stats.Values)
            {
                if (stat is RTCTransportStats transport && !string.IsNullOrEmpty(transport.selectedCandidatePairId) &&
                    report.TryGetValue(transport.selectedCandidatePairId, out var selected))
                {
                    pair = selected as RTCIceCandidatePairStats;
                    if (pair != null) break;
                }
            }
            if (pair == null)
                foreach (var stat in report.Stats.Values)
                    if (stat is RTCIceCandidatePairStats p && p.nominated && p.state == "succeeded") { pair = p; break; }
            if (pair == null) return CoreLinkPath.Unknown;
            report.TryGetValue(pair.localCandidateId ?? string.Empty, out var local);
            report.TryGetValue(pair.remoteCandidateId ?? string.Empty, out var remote);
            var localCandidate = local as RTCIceCandidateStats;
            var remoteCandidate = remote as RTCIceCandidateStats;
            return CoreLinkPath.FromCandidates(localCandidate?.candidateType, localCandidate?.relayProtocol, remoteCandidate?.candidateType);
        }

        public void Send(byte[] message)
        {
            if (State != PeerTransportState.Open) return;
            _channel.Send(message);
        }

        private void Fail(string reason)
        {
            if (State == PeerTransportState.Failed || State == PeerTransportState.Closed) return;
            Failure = reason;
            State = PeerTransportState.Failed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (State != PeerTransportState.Failed) State = PeerTransportState.Closed;
            ReleaseNative();
        }

        private void ReleaseNative()
        {
            _released = true;
            try { _channel?.Close(); } catch (Exception) { }
            try { _channel?.Dispose(); } catch (Exception) { }
            try { _pc?.Close(); } catch (Exception) { }
            try { _pc?.Dispose(); } catch (Exception) { }
            _channel = null;
            _pc = null;
            _statsOp = null;
        }
    }
}
