// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace SpatialBlend.Tests
{
    /// <summary>In-memory tunneled-link fixtures. The manager is nested: a top-level MonoBehaviour in an editor-only assembly cannot be added to a GameObject.</summary>
    public static class TunnelFixtures
    {
    /// <summary>
    /// An <see cref="SpatialBlendManager"/> whose tunneled core links are in-memory pipes, so the link seam
    /// (host, accept, join, read, write, close) is testable without a native library or a network. Each pipe is the
    /// native link: <see cref="Pipe.ToPeer"/> is what core has for the remote peer, <see cref="Pipe.FromPeer"/> what the
    /// carrier wrote into core.
    /// </summary>
    public sealed class FakeTunnelManager : SpatialBlendManager
    {
        public sealed class Pipe
        {
            public readonly List<byte> ToPeer = new List<byte>();
            public readonly List<byte> FromPeer = new List<byte>();
            public bool Ended;
            /// <summary>Writes that answer 2 (would block, nothing taken) before the pipe takes one.</summary>
            public int WouldBlockWrites;
            public int WouldBlockedBytes;
        }

        private static long _nextHandle = 910000;
        public readonly Dictionary<long, Pipe> Pipes = new Dictionary<long, Pipe>();
        public readonly List<long> ClosedLinks = new List<long>();
        public readonly List<long> DestroyedSessions = new List<long>();
        public int HostTunneledCalls;
        public int JoinTunneledCalls;
        public bool AcceptFails;
        public bool HostFails;
        /// <summary>The join's outcome as the native handle reports it: pending until set.</summary>
        public bool JoinCompleted;
        public string JoinError = string.Empty;
        public long LastJoinLink;

        protected override void InitializeNativeLibrary() { }
        protected override long CreateNativeSession() => ++_nextHandle;
        protected override long AddNativeSpace(long session) => ++_nextHandle;
        protected override void ConfigureNativeSpace(long space, long generation) { }
        protected override void UnregisterNativeSpace(long space) { }
        protected override void DestroyNativeSession(long session) => DestroyedSessions.Add(session);

        protected override long HostNativeSpaceTunneled(long space)
        {
            HostTunneledCalls++;
            return HostFails ? 0 : -1;
        }

        protected override bool WaitForNativeConnection(long handle, out string error)
        {
            error = string.Empty;
            return handle != 0;
        }

        protected override long ConnectNativeSpaceTunneled(long space, out long link)
        {
            JoinTunneledCalls++;
            link = ++_nextHandle;
            Pipes[link] = new Pipe();
            LastJoinLink = link;
            return ++_nextHandle;
        }

        protected override bool PollNativeJoin(long handle, out string error)
        {
            error = JoinCompleted ? string.Empty : JoinError;
            return JoinCompleted;
        }

        protected override bool AcceptNativeTunnel(long space, out long link)
        {
            link = 0;
            if (AcceptFails) return false;
            link = ++_nextHandle;
            Pipes[link] = new Pipe();
            return true;
        }

        protected override int ReadNativeTunnel(long link, byte[] buffer, int capacity, out int count)
        {
            count = 0;
            if (!Pipes.TryGetValue(link, out var pipe)) return -1;
            count = Math.Min(capacity, pipe.ToPeer.Count);
            pipe.ToPeer.CopyTo(0, buffer, 0, count);
            pipe.ToPeer.RemoveRange(0, count);
            return pipe.Ended && pipe.ToPeer.Count == 0 && count == 0 ? 0 : 1;
        }

        protected override int WriteNativeTunnel(long link, byte[] bytes, int count)
        {
            if (!Pipes.TryGetValue(link, out var pipe)) return -1;
            if (pipe.Ended) return 0;
            // ABI 4.5: a full input takes nothing and answers 2; the same bytes go in after the next read.
            if (pipe.WouldBlockWrites > 0) { pipe.WouldBlockWrites--; pipe.WouldBlockedBytes += count; return 2; }
            for (int i = 0; i < count; i++) pipe.FromPeer.Add(bytes[i]);
            return 1;
        }

        protected override void CloseNativeTunnel(long link)
        {
            ClosedLinks.Add(link);
            if (Pipes.TryGetValue(link, out var pipe)) pipe.Ended = true;
        }
    }
    }
}
#endif
