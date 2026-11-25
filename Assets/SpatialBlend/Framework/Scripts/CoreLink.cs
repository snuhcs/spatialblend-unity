// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend
{
    /// <summary>What a pump learned from one read of a <see cref="CoreLink"/>.</summary>
    public enum CoreLinkRead
    {
        /// <summary>Zero or more bytes were stored; the link is open.</summary>
        Open,
        /// <summary>The link ended (core closed it, or this link was closed) and everything it sent was read.</summary>
        Ended,
    }

    /// <summary>What core did with bytes written into a <see cref="CoreLink"/>.</summary>
    public enum CoreLinkWrite
    {
        /// <summary>All of them were taken.</summary>
        Accepted,
        /// <summary>Core's input is full: nothing was taken. Retry the same bytes after the next <see cref="CoreLink.Read"/>.</summary>
        WouldBlock,
        /// <summary>The link is closed or invalid (ABI return 0 or -1).</summary>
        Closed,
    }

    /// <summary>
    /// One tunneled core link to one remote peer (ABI 4.5). Core speaks gRPC over it; the application
    /// carries its bytes between the two peers on a channel they already share (Fusion reliable data in the
    /// shipped app), so no peer ever dials another peer's address. The stream is ordered and has no framing:
    /// <see cref="Read"/> may return any prefix and <see cref="Write"/> accepts any chunking.
    /// Main thread only, like the manager that issued it.
    /// </summary>
    public sealed class CoreLink
    {
        private readonly SpatialBlendManager _owner;
        private readonly long _generation;

        internal CoreLink(SpatialBlendManager owner, long id, long generation)
        {
            _owner = owner;
            Id = id;
            _generation = generation;
        }

        /// <summary>The native link handle.</summary>
        public long Id { get; }

        /// <summary>True once this link was closed here, or its connection was retired.</summary>
        public bool IsClosed { get; private set; }

        /// <summary>Bytes core has for the remote peer (non-blocking), up to <paramref name="buffer"/>.Length.</summary>
        public CoreLinkRead Read(byte[] buffer, out int count)
        {
            count = 0;
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (IsClosed || !_owner.IsCurrentLifecycleGeneration(_generation)) { IsClosed = true; return CoreLinkRead.Ended; }
            int rc = _owner.ReadCoreLink(Id, buffer, buffer.Length, out count);
            if (rc < 0) { count = 0; IsClosed = true; return CoreLinkRead.Ended; }
            return rc == 0 ? CoreLinkRead.Ended : CoreLinkRead.Open;
        }

        /// <summary>
        /// Bytes from the remote peer into core: the first <paramref name="count"/> of <paramref name="bytes"/>. ABI 4.5 write
        /// returns 1 (taken), 2 (would block, nothing taken: retry the same bytes after the next read) or 0 / -1 (closed).
        /// </summary>
        public CoreLinkWrite Write(byte[] bytes, int count)
        {
            if (IsClosed || !_owner.IsCurrentLifecycleGeneration(_generation)) { IsClosed = true; return CoreLinkWrite.Closed; }
            int rc = _owner.WriteCoreLink(Id, bytes, count);
            if (rc == 1) return CoreLinkWrite.Accepted;
            if (rc == 2) return CoreLinkWrite.WouldBlock;
            IsClosed = true;
            return CoreLinkWrite.Closed;
        }

        /// <summary>Close the link (the peer left, or this participant is leaving). Idempotent.</summary>
        public void Close()
        {
            if (IsClosed) return;
            IsClosed = true;
            if (_owner.IsCurrentLifecycleGeneration(_generation)) _owner.CloseCoreLink(Id);
        }
    }
}
