// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace SpatialBlend.Internal
{
    using SpaceID = System.Int64;
    using EventHandle = System.Int64;

    /// <summary>
    /// The tunneled core link (ABI 4.5, spatialblend.h): hosting and joining a room with no address. The core's
    /// gRPC link runs over a byte stream this app carries between the two peers; no peer ever dials another's
    /// address. One link is one stream to one remote peer. Bytes are an ordered stream with no framing: the
    /// carrier must deliver them in order and without loss, and may split or join them at any boundary.
    /// </summary>
    internal static partial class SpatialBlendAPI
    {
        private static class TunnelRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_host_space_tunneled(SpaceID space);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_accept_tunnel(SpaceID space, out long link);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_connect_space_tunneled(SpaceID space, out long link);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_tunnel_write(long link, byte[] bytes, uint n);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_tunnel_read(long link, byte[] buffer, uint capacity, out uint n);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_tunnel_close(long link);
        }

        private static class TunnelDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_host_space_tunneled(SpaceID space);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_accept_tunnel(SpaceID space, out long link);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern EventHandle sb_connect_space_tunneled(SpaceID space, out long link);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_tunnel_write(long link, byte[] bytes, uint n);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_tunnel_read(long link, byte[] buffer, uint capacity, out uint n);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_tunnel_close(long link);
        }

        /// <summary>Host a space with no listening socket. 0 on failure; otherwise a handle <see cref="TryWait"/> confirms.</summary>
        public static EventHandle HostSpaceTunneled(SpaceID space) =>
            useDebugLibrary ? TunnelDebug.sb_host_space_tunneled(space) : TunnelRelease.sb_host_space_tunneled(space);

        /// <summary>Host side: attach one joining peer. False when the host is not tunneled or is stopping.</summary>
        public static bool AcceptTunnel(SpaceID space, out long link)
        {
            int rc = useDebugLibrary ? TunnelDebug.sb_accept_tunnel(space, out link) : TunnelRelease.sb_accept_tunnel(space, out link);
            return rc == 1;
        }

        /// <summary>
        /// Join over a tunneled link. The link is returned at once and must be pumped while the returned handle is
        /// pending: <see cref="TryWait"/> with a zero timeout polls it.
        /// </summary>
        public static EventHandle ConnectSpaceTunneled(SpaceID space, out long link) =>
            useDebugLibrary ? TunnelDebug.sb_connect_space_tunneled(space, out link) : TunnelRelease.sb_connect_space_tunneled(space, out link);

        /// <summary>Bytes from the remote peer into core: the first <paramref name="count"/> of <paramref name="bytes"/>. 1 accepted, 2 would block (nothing taken: retry the same bytes after the next read), 0 closed, -1 invalid.</summary>
        public static int TunnelWrite(long link, byte[] bytes, int count)
        {
            if (count < 0 || (bytes == null ? count != 0 : count > bytes.Length)) return -1;
            return useDebugLibrary ? TunnelDebug.sb_tunnel_write(link, bytes, (uint)count) : TunnelRelease.sb_tunnel_write(link, bytes, (uint)count);
        }

        /// <summary>Bytes core has for the remote peer, up to <paramref name="capacity"/> (non-blocking). 1 ok (count may be 0), 0 the link ended and everything it sent was read, -1 invalid.</summary>
        public static int TunnelRead(long link, byte[] buffer, int capacity, out int count)
        {
            count = 0;
            if (buffer == null || capacity < 0 || capacity > buffer.Length) return -1;
            uint n;
            int rc = useDebugLibrary ? TunnelDebug.sb_tunnel_read(link, buffer, (uint)capacity, out n) : TunnelRelease.sb_tunnel_read(link, buffer, (uint)capacity, out n);
            count = (int)n;
            return rc;
        }

        /// <summary>Close a link and forget it. Safe twice and on an unknown link.</summary>
        public static void TunnelClose(long link)
        {
            if (useDebugLibrary) TunnelDebug.sb_tunnel_close(link);
            else TunnelRelease.sb_tunnel_close(link);
        }
    }
}
