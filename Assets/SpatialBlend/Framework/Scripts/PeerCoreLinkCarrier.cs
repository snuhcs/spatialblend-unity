// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend
{
    /// <summary>Which path the core link's bytes take between two peers.</summary>
    public enum CoreLinkCarrier
    {
        /// <summary>Fusion reliable data through the Photon relay: always available, about 16 KB/s per direction.</summary>
        Photon,
        /// <summary>A WebRTC data channel, peer to peer: reliable and ordered, megabytes per second.</summary>
        WebRtc,
    }

    public enum PeerTransportState
    {
        Idle,
        Negotiating,
        Open,
        Failed,
        Closed,
    }

    /// <summary>
    /// A peer-to-peer, reliable, ordered message channel that is negotiated through signals the caller carries (SDP and
    /// ICE candidates as JSON strings). The shipped implementation is a WebRTC data channel (<c>WebRtcPeerTransport</c>);
    /// tests use a fake. Main thread only.
    /// </summary>
    public interface IPeerDataTransport : IDisposable
    {
        PeerTransportState State { get; }
        /// <summary>Bytes accepted by <see cref="Send"/> and not yet on the wire.</summary>
        long BufferedBytes { get; }
        /// <summary>A signal for the remote peer, to be delivered to its <see cref="ReceiveSignal"/>.</summary>
        event Action<string> Signal;
        /// <summary>One message from the remote peer.</summary>
        event Action<byte[]> Message;
        /// <param name="offerer">The side that creates the data channel and the offer.</param>
        void Start(bool offerer);
        void ReceiveSignal(string json);
        void Send(byte[] message);
        /// <summary>Advances the negotiation. Called every frame.</summary>
        void Tick();
    }

    /// <summary>A transport that can say which path its connection took (<see cref="WebRtcPeerTransport"/> reads the selected candidate pair).</summary>
    public interface IPathReporting
    {
        CoreLinkPath Path { get; }
    }

    /// <summary>A wire that can say it has no room for another chunk. <see cref="CoreLinkPump"/> then leaves the bytes in core.</summary>
    public interface IBackpressuredWire : ICoreLinkWire
    {
        bool CanAccept { get; }
    }

    /// <summary>
    /// One peer's carrier: a direct <see cref="IPeerDataTransport"/> when it comes up, the Photon wire otherwise. Chunks
    /// carry (epoch, sequence), so <see cref="CoreLinkPump"/> restores order whichever path a chunk took, and switching
    /// paths cannot reorder the core stream.
    /// <para>
    /// While the direct path is negotiating nothing is sent (<see cref="CanAccept"/> is false), so a large payload never
    /// starts on the slow path. The negotiation is bounded: if the transport has not opened by
    /// <c>iceTimeoutSeconds</c>, or it fails, the Photon path takes over and the pump resumes.
    /// </para>
    /// </summary>
    public sealed class PeerCoreLinkCarrier : IBackpressuredWire, IDisposable
    {
        /// <summary>
        /// The most libwebrtc will queue in one data channel: <c>kMaxQueuedSendDataBytes = 16 * 1024 * 1024</c> in its
        /// SCTP data channel (pc/sctp_data_channel.cc, the library under com.unity.webrtc); a send that would pass it
        /// closes the channel. That is the protocol's own window, so the carrier stops reading core exactly when one more
        /// message would pass it (<see cref="CanAccept"/>), and not at a number of its own (the 1 MiB that stood here had no
        /// source). The constant is upstream's and cannot be verified from the pinned package: Library/PackageCache/
        /// com.unity.webrtc@9605a106619b (3.0.0) ships no C++ source (only Runtime/Plugins/*/libwebrtc binaries), its C# exposes the
        /// queue only as RTCDataChannel.BufferedAmount (Runtime/Scripts/RTCDataChannel.cs:659, WebRTC.cs:1635) with no limit
        /// property, and the native strings carry only a receive-side "Queued received data exceeds the max buffer size.". The build
        /// exposes no value to use instead, so this is upstream's constant, cited and unverified here.
        /// </summary>
        public const long MaxQueuedSendDataBytes = 16L * 1024 * 1024;

        private const int FrameHeaderBytes = 8; // epoch and sequence
        private readonly long _maxFrameBytes;
        private readonly ICoreLinkWire _photon;
        private readonly IPeerDataTransport _transport;
        private readonly Func<double> _now;
        private readonly double _deadline;
        private readonly double _startedAt;
        private bool _decided;

        public PeerCoreLinkCarrier(ICoreLinkWire photon, IPeerDataTransport transport, Func<double> now,
            double iceTimeoutSeconds, bool offerer, int maxMessageBytes = CoreLinkPump.DefaultMaxChunkBytes)
        {
            _maxFrameBytes = (long)maxMessageBytes + FrameHeaderBytes;
            _photon = photon ?? throw new ArgumentNullException(nameof(photon));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _transport = transport;
            if (_transport == null)
            {
                _decided = true;
                return;
            }
            _startedAt = _now();
            _deadline = _startedAt + iceTimeoutSeconds;
            _transport.Message += OnMessage;
            _transport.Start(offerer);
        }

        public CoreLinkCarrier Active { get; private set; } = CoreLinkCarrier.Photon;

        /// <summary>True once the direct path failed or timed out and the Photon path took over.</summary>
        public bool FellBack { get; private set; }

        public string FallbackReason { get; private set; }

        public bool Negotiating => !_decided;

        /// <summary>Seconds from the start of the negotiation to the direct channel opening; 0 until it has. The measure for the ICE timeout.</summary>
        public double OpenedAfterSeconds { get; private set; }

        /// <summary>
        /// The path the bytes take: Photon when that carrier is active, the transport's own report when the direct channel
        /// is, Unknown while negotiating (or while the selected pair has not been read yet).
        /// </summary>
        public CoreLinkPath Path =>
            !_decided ? CoreLinkPath.Unknown
            : Active == CoreLinkCarrier.Photon ? CoreLinkPath.Photon
            : _transport is IPathReporting reporting ? reporting.Path : CoreLinkPath.Unknown;

        /// <summary>True once the manager has logged this link's path.</summary>
        public bool PathReported { get; set; }

        /// <summary>
        /// True once the direct channel failed AFTER it had opened. Chunks already handed to it are gone for good (the
        /// carrier keeps no copy and adds no retransmission), so the link above has ended (the link ending is the
        /// loss signal), and the owner of the link rejoins over the Photon carrier. Nothing is sent after this.
        /// </summary>
        public bool Lost { get; private set; }

        public string LostReason { get; private set; }

        public IPeerDataTransport Transport => _transport;

        /// <summary>A chunk that arrived on the direct path: (epoch, sequence, bytes).</summary>
        public event Action<int, int, byte[]> Chunk;

        /// <summary>
        /// Whether the pump may read another chunk from core. On the direct path: while one more frame fits in the
        /// channel's queue (<see cref="MaxQueuedSendDataBytes"/>). On the Photon path: while the wire can take it (the peer
        /// has offered the token its chunks need), so a chunk is never parked in a queue of ours.
        /// </summary>
        public bool CanAccept =>
            !Lost && _decided && (Active == CoreLinkCarrier.Photon
                ? !(_photon is IBackpressuredWire wire) || wire.CanAccept
                : _transport.BufferedBytes + _maxFrameBytes <= MaxQueuedSendDataBytes);

        /// <summary>Main thread, every frame.</summary>
        public void Tick()
        {
            if (_transport == null) return;
            _transport.Tick();
            if (!_decided)
            {
                switch (_transport.State)
                {
                    case PeerTransportState.Open:
                        _decided = true;
                        Active = CoreLinkCarrier.WebRtc;
                        OpenedAfterSeconds = _now() - _startedAt;
                        return;
                    case PeerTransportState.Failed:
                    case PeerTransportState.Closed:
                        Fallback("the direct path failed");
                        return;
                }
                if (_now() >= _deadline) Fallback("the direct path did not open in time");
                return;
            }
            if (Active == CoreLinkCarrier.WebRtc && !Lost && _transport.State != PeerTransportState.Open)
            {
                // Chunks already handed to the channel are not recoverable and there is no retransmission layer: the
                // link has ended. Switching to the Photon wire here would leave the peer waiting at a gap forever.
                Lost = true;
                LostReason = "the direct path closed after it opened";
            }
        }

        private void Fallback(string reason)
        {
            _decided = true;
            FellBack = true;
            FallbackReason = reason;
            Active = CoreLinkCarrier.Photon;
        }

        public void Send(int peer, int epoch, int sequence, byte[] bytes)
        {
            if (Active == CoreLinkCarrier.WebRtc && _transport.State == PeerTransportState.Open)
            {
                var frame = new byte[FrameHeaderBytes + bytes.Length];
                WriteInt(frame, 0, epoch);
                WriteInt(frame, 4, sequence);
                Buffer.BlockCopy(bytes, 0, frame, 8, bytes.Length);
                _transport.Send(frame);
                return;
            }
            _photon.Send(peer, epoch, sequence, bytes);
        }

        private void OnMessage(byte[] frame)
        {
            if (frame == null || frame.Length < 8) return;
            var payload = new byte[frame.Length - 8];
            Buffer.BlockCopy(frame, 8, payload, 0, payload.Length);
            Chunk?.Invoke(ReadInt(frame, 0), ReadInt(frame, 4), payload);
        }

        private static void WriteInt(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static int ReadInt(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        public void Dispose()
        {
            if (_transport == null) return;
            _transport.Message -= OnMessage;
            _transport.Dispose();
        }
    }
}
