// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The core link's bytes as Fusion reliable data: the key that names one chunk. The first int marks the data as
    /// ours ("OLK1") because other components may send reliable data on the same runner; then the connection round
    /// (epoch) and the chunk's sequence number within (peer, epoch), which is how <see cref="CoreLinkPump"/> restores
    /// order. The key says nothing about who sent it: Fusion's reliable-data callback reports the receiving player, not
    /// the sender, so identity is the capability token that opens the payload (see <see cref="LinkTokens"/>). Fusion
    /// fragments the data itself, so nothing here depends on a message size.
    /// </summary>
    public static class FusionCoreLinkKey
    {
        public const int Magic = 0x4F4C4B31;

        public static ReliableKey Make(int epoch, int sequence) => ReliableKey.FromInts(Magic, epoch, sequence, 0);

        public static bool TryRead(ReliableKey key, out int epoch, out int sequence)
        {
            key.GetInts(out int magic, out epoch, out sequence, out _);
            return magic == Magic;
        }
    }

    /// <summary>
    /// The key of one signaling message for the direct (WebRTC) carrier: SDP and ICE candidates as JSON, carried on the
    /// Photon session. First int "OLS1", then the connection round and a per-sender counter. The payload starts with the
    /// sender's capability token, as for chunks.
    /// </summary>
    public static class FusionCoreLinkSignalKey
    {
        public const int Magic = 0x4F4C5331;

        public static ReliableKey Make(int epoch, int counter) => ReliableKey.FromInts(Magic, epoch, counter, 0);

        public static bool TryRead(ReliableKey key, out int epoch)
        {
            key.GetInts(out int magic, out epoch, out _, out _);
            return magic == Magic;
        }
    }

    /// <summary>
    /// Sends a <see cref="CoreLinkPump"/>'s chunks to a player of the runner, on the Photon session both already share,
    /// each behind the token that player offered for the round (nothing goes out before it has).
    /// </summary>
    public sealed class FusionCoreLinkWire : ICoreLinkWire
    {
        private readonly NetworkRunner _runner;
        private readonly LinkTokens _tokens;

        public FusionCoreLinkWire(NetworkRunner runner, LinkTokens tokens)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        private int _signals;

        public void Send(int peer, int epoch, int sequence, byte[] bytes) =>
            _tokens.WithToken(epoch, peer, token =>
                _runner.SendReliableDataToPlayer(PlayerRef.FromEncoded(peer), FusionCoreLinkKey.Make(epoch, sequence),
                    LinkTokens.Frame(token, bytes)));

        /// <summary>One signaling message (SDP or ICE, as JSON) for the direct carrier of the link to <paramref name="peer"/>.</summary>
        public void SendSignal(int peer, int epoch, string json) =>
            _tokens.WithToken(epoch, peer, token =>
                _runner.SendReliableDataToPlayer(PlayerRef.FromEncoded(peer), FusionCoreLinkSignalKey.Make(epoch, ++_signals),
                    LinkTokens.Frame(token, System.Text.Encoding.UTF8.GetBytes(json))));
    }

    /// <summary>
    /// Receives the core link's chunks from the runner and hands each to <paramref name="onChunk"/> on the main
    /// thread, with the peer its token was issued to as the sender. Everything else the runner reports is another
    /// component's business.
    /// </summary>
    public sealed class FusionCoreLinkReceiver : INetworkRunnerCallbacks
    {
        private readonly Action<PlayerRef, int, int, byte[]> _onChunk;
        private readonly Action<PlayerRef, int, string> _onSignal;
        private readonly LinkTokens _tokens;

        /// <summary>Chunks of ours received so far (for diagnostics), and the reliable data that was not ours.</summary>
        public int ChunksReceived { get; private set; }
        public int ForeignReceived { get; private set; }
        /// <summary>Signals accepted (diagnostics) and every reliable message of ours that reached the callback, accepted or not.</summary>
        public int SignalsReceived { get; private set; }
        public int OursReceived { get; private set; }
        public int LastSender { get; private set; } = -1;
        /// <summary>The PlayerRef the callback reported on the latest message of ours. Diagnostic only: it is the receiver, never trusted.</summary>
        public int LastReported { get; private set; } = -1;

        /// <summary>Chunks and signals dropped because their token is not one issued for that round (a spoof, a replay, or a stale round).</summary>
        public int SpoofedDropped { get; private set; }

        public FusionCoreLinkReceiver(Action<PlayerRef, int, int, byte[]> onChunk, Action<PlayerRef, int, string> onSignal, LinkTokens tokens)
        {
            _onChunk = onChunk ?? throw new ArgumentNullException(nameof(onChunk));
            _onSignal = onSignal;
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
            bool isSignal = FusionCoreLinkSignalKey.TryRead(key, out int epoch);
            int sequence = 0;
            if (!isSignal && !FusionCoreLinkKey.TryRead(key, out epoch, out sequence)) { ForeignReceived++; return; }
            LastReported = player.RawEncoded;
            OursReceived++;
            // The sender is whoever this round's token was issued to. Nothing in the key, the callback's player or the body names it.
            if (data.Array == null || !_tokens.TryResolve(epoch, data.Array, data.Offset, data.Count, out int peer))
            {
                SpoofedDropped++;
                return;
            }
            var sender = PlayerRef.FromEncoded(peer);
            int bodyOffset = data.Offset + LinkTokens.Size, bodyCount = data.Count - LinkTokens.Size;
            if (isSignal)
            {
                SignalsReceived++;
                _onSignal?.Invoke(sender, epoch, System.Text.Encoding.UTF8.GetString(data.Array, bodyOffset, bodyCount));
                return;
            }
            ChunksReceived++;
            LastSender = peer;
            // The segment is only valid during this call.
            var bytes = new byte[bodyCount];
            if (bodyCount > 0) Buffer.BlockCopy(data.Array, bodyOffset, bytes, 0, bodyCount);
            _onChunk(sender, epoch, sequence, bytes);
        }

        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    }
}
