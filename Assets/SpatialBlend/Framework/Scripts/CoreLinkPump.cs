// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend
{
    /// <summary>
    /// The carrier between two peers of one <see cref="CoreLink"/>: sends one numbered chunk of the link's byte
    /// stream to the remote peer. The shipped carrier is Fusion reliable data (<c>FusionConnectionManager</c>).
    /// </summary>
    public interface ICoreLinkWire
    {
        /// <param name="peer">The remote peer (the carrier's own id for it).</param>
        /// <param name="epoch">The connection round the chunk belongs to.</param>
        /// <param name="sequence">0, 1, 2, ... within (peer, epoch).</param>
        /// <param name="bytes">Exactly the chunk. Ownership passes to the carrier.</param>
        void Send(int peer, int epoch, int sequence, byte[] bytes);
    }

    /// <summary>
    /// Moves one link's bytes between core and a carrier, every frame, on the main thread. Outbound: whatever core has
    /// for the remote peer is read and sent as numbered chunks of at most <see cref="MaxChunkBytes"/>. Inbound: chunks
    /// are written into core in sequence order. There is no reorder buffer: no carrier here documents a window within
    /// which chunks may arrive out of order (WebRTC's channel is ordered and reliable, RFC 8831; Fusion documents none for
    /// reliable data), so a chunk ahead of the next expected one is a gap, which is a lost chunk, and the link ends
    /// (the same ending as a channel lost after it opened). Core's stream has no
    /// framing, so the chunk size is only a bound on one message, not a protocol.
    /// </summary>
    public sealed class CoreLinkPump
    {
        /// <summary>
        /// Upper bound of one carrier message. 16 KiB is the largest message every WebRTC implementation can exchange
        /// (RFC 8831 section 6.6: senders should not exceed 16 KiB so that interoperable); Fusion fragments its reliable
        /// data itself, so for the Photon path it only bounds the memory and head-of-line delay of one message.
        /// </summary>
        public const int DefaultMaxChunkBytes = 16 * 1024;

        private readonly ICoreLinkWire _wire;
        private readonly byte[] _scratch;
        // Chunks accepted in order that core has not taken yet (its input was full). Never a gap: see Receive.
        private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
        private int _nextSend;
        private int _nextAccept;
        private bool _peerSpoke;
        private readonly bool _holdUntilPeerSpeaks;

        /// <param name="holdUntilPeerSpeaks">
        /// Host side: send nothing until the first chunk arrived from the peer. Core's server speaks first, and the peer
        /// may not have joined this round yet; the joiner's first chunk is proof it has.
        /// </param>
        public CoreLinkPump(CoreLink link, int peer, int epoch, ICoreLinkWire wire, bool holdUntilPeerSpeaks,
            int maxChunkBytes = DefaultMaxChunkBytes)
        {
            Link = link ?? throw new ArgumentNullException(nameof(link));
            Peer = peer;
            Epoch = epoch;
            _wire = wire ?? throw new ArgumentNullException(nameof(wire));
            _holdUntilPeerSpeaks = holdUntilPeerSpeaks;
            MaxChunkBytes = maxChunkBytes > 0 ? maxChunkBytes : DefaultMaxChunkBytes;
            _scratch = new byte[MaxChunkBytes];
        }

        public CoreLink Link { get; }
        public int Peer { get; }
        public int Epoch { get; }
        public int MaxChunkBytes { get; }

        /// <summary>Core ended the link (or it was closed): nothing more will flow.</summary>
        public bool Ended { get; private set; }
        public long BytesSent { get; private set; }
        public long BytesReceived { get; private set; }
        public int ChunksSent => _nextSend;
        /// <summary>Chunks received in order that core has not taken yet, and wait.</summary>
        public int ChunksWaiting => _inbound.Count;
        public bool PeerHasSpoken => _peerSpoke;

        /// <summary>
        /// Main thread, every frame: send what core has for the peer, then retry the chunk core could not take (its input
        /// was full; ABI write 2 means retry the same bytes after the next read, which is what this order does).
        /// </summary>
        public void Pump()
        {
            if (Ended) return;
            SendOutbound();
            if (!Ended) DrainInbound();
        }

        private void SendOutbound()
        {
            if (_wire is IBackpressuredWire pressured && !pressured.CanAccept) return;
            if (_holdUntilPeerSpeaks && !_peerSpoke)
            {
                // Still notice a link that ended before the peer ever spoke.
                if (Link.IsClosed) Ended = true;
                return;
            }
            for (;;)
            {
                if (_wire is IBackpressuredWire full && !full.CanAccept) return;
                CoreLinkRead result = Link.Read(_scratch, out int count);
                if (count > 0)
                {
                    var chunk = new byte[count];
                    Buffer.BlockCopy(_scratch, 0, chunk, 0, count);
                    BytesSent += count;
                    _wire.Send(Peer, Epoch, _nextSend++, chunk);
                }
                if (result == CoreLinkRead.Ended) { Ended = true; return; }
                if (count == 0) return;
            }
        }

        /// <summary>
        /// A chunk from the peer. False when it belongs to another round, repeats one already received, or the link is
        /// closed. A chunk AHEAD of the next expected one means chunks were lost: the link ends (<see cref="CarrierLost"/>)
        /// instead of waiting at the gap for ever.
        /// </summary>
        public bool Receive(int epoch, int sequence, byte[] bytes)
        {
            if (Ended || epoch != Epoch || bytes == null || sequence < _nextAccept) return false;
            _peerSpoke = true;
            if (sequence > _nextAccept)
            {
                Lose();
                return false;
            }
            _inbound.Enqueue(bytes);
            _nextAccept++;
            return DrainInbound();
        }

        /// <summary>
        /// Writes the queued chunks into core in order. A chunk core would not take stays queued (nothing was taken) and is
        /// retried by the next <see cref="Pump"/>; chunks behind it wait. False only when the link closed.
        /// </summary>
        private bool DrainInbound()
        {
            while (_inbound.Count > 0)
            {
                byte[] next = _inbound.Peek();
                if (next.Length > 0)
                {
                    CoreLinkWrite result = Link.Write(next, next.Length);
                    if (result == CoreLinkWrite.WouldBlock) return true;
                    if (result == CoreLinkWrite.Closed) { Ended = true; return false; }
                    BytesReceived += next.Length;
                }
                _inbound.Dequeue();
            }
            return true;
        }

        /// <summary>The link ended because its carrier lost chunks (see <see cref="PeerCoreLinkCarrier.Lost"/>), not because core closed it.</summary>
        public bool CarrierLost { get; private set; }

        /// <summary>
        /// The carrier lost chunks of this link: it cannot continue, since core's stream has no framing and no hole can
        /// be filled. Closes the link, which is the loss signal for the peer's core, and ends the pump.
        /// </summary>
        public void Lose()
        {
            if (Ended) return;
            CarrierLost = true;
            Close();
        }

        /// <summary>Close the link. The peer left, or the round ended.</summary>
        public void Close()
        {
            Ended = true;
            _inbound.Clear();
            Link.Close();
        }
    }
}
