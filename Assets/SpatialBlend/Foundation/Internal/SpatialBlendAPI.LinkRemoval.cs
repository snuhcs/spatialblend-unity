// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace SpatialBlend.Internal
{
    using SpaceID = System.Int64;
    using ElementID = System.Int64;

    /// <summary>Why the peer removed a link (<c>sb_link_removed_reason_t</c>, ABI 4.9).</summary>
    internal enum SbLinkRemovedReason : int
    {
        /// <summary>The sharer unshared the source this space linked a local element to; the local element survives.</summary>
        PeerUnshared = 1,
        /// <summary>A receiver called <c>sb_unlink</c> on its counterpart of an element this space shares; the element is still shared.</summary>
        PeerUnlinked = 2,
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnLinkRemovedNativeDelegate(SpaceID space, ElementID localElement, long peerSpace, int reason, IntPtr cbUserData);

    internal delegate void OnLinkRemovedDelegate(SpaceID space, ElementID localElement, long peerSpace, SbLinkRemovedReason reason);

    /// <summary>
    /// ABI 4.9: <c>sb_unlink</c> and the link-removed signal. A receiver removes the link of its counterpart of a shared element on both
    /// sides (<see cref="TryUnlink"/>); the peer learns through <see cref="TryRegisterOnLinkRemoved"/> when a link of its own ended
    /// (the sharer unshared, or the receiver unlinked). Both are optional: a core older than 4.9 has neither, and every entry here
    /// then reports it as unavailable instead of throwing.
    /// </summary>
    internal static partial class SpatialBlendAPI
    {
        /// <summary>The ABI minor that added <c>sb_unlink</c> and the link-removed callback.</summary>
        internal const uint LinkRemovalAbiMinor = 9;

        private static class LinkRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_unlink(SpaceID space, ElementID elem);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_link_removed(SpaceID space, OnLinkRemovedNativeDelegate callback, IntPtr userData);
        }

        private static class LinkDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_unlink(SpaceID space, ElementID elem);
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sb_register_on_link_removed(SpaceID space, OnLinkRemovedNativeDelegate callback, IntPtr userData);
        }

        private static readonly Dictionary<SpaceID, OnLinkRemovedDelegate> linkRemovedCallbacks = new Dictionary<SpaceID, OnLinkRemovedDelegate>();
        private static readonly Dictionary<SpaceID, long> linkRemovedCallbackTokens = new Dictionary<SpaceID, long>();
        private static readonly OnLinkRemovedNativeDelegate onLinkRemovedWrapper = OnLinkRemovedWrapper;
        private static bool _linkRemovalProbed;
        private static bool _linkRemovalAvailable;
        private static bool _linkRemovalProbedDebug;

        private static int LinkRemovedCallbackCount
        {
            get { lock (callbackLock) return linkRemovedCallbacks.Count; }
        }

        /// <summary>True when the loaded core has <c>sb_unlink</c> (ABI 4.9). Probed once with an unknown space (a no-op that returns 0).</summary>
        internal static bool HasLinkRemoval()
        {
            lock (callbackLock)
            {
                // The answer belongs to the library that was probed: the debug and the release library are different files.
                if (!_linkRemovalProbed || _linkRemovalProbedDebug != useDebugLibrary)
                {
                    try
                    {
                        if (useDebugLibrary) LinkDebug.sb_unlink(0, 0);
                        else LinkRelease.sb_unlink(0, 0);
                        _linkRemovalAvailable = true;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _linkRemovalAvailable = false;
                    }
                    catch (DllNotFoundException)
                    {
                        // No native library in this context (an EditMode test): unavailable, and not remembered, so a later load is probed again.
                        return false;
                    }
                    _linkRemovalProbed = true;
                    _linkRemovalProbedDebug = useDebugLibrary;
                }
                return _linkRemovalAvailable;
            }
        }

        /// <summary>
        /// The receiver drops the link of its counterpart <paramref name="element"/> on both sides (ABI 4.9). The element is not deleted.
        /// Returns the number of links removed: 0 when the element is unknown or is not the counterpart of a remote source, and -1 when
        /// the loaded core has no <c>sb_unlink</c>.
        /// </summary>
        internal static int TryUnlink(SpaceID space, ElementID element)
        {
            if (!HasLinkRemoval()) return -1;
            return useDebugLibrary ? LinkDebug.sb_unlink(space, element) : LinkRelease.sb_unlink(space, element);
        }

        /// <summary>
        /// Registers the per-space link-removed callback (ABI 4.9). The callback runs on a native thread; the caller marshals it.
        /// False when the callback is null or the loaded core has no such entry point.
        /// </summary>
        internal static bool TryRegisterOnLinkRemoved(SpaceID space, OnLinkRemovedDelegate callback)
        {
            if (callback == null || !HasLinkRemoval()) return false;
            long token = RegisterCallback(linkRemovedCallbacks, linkRemovedCallbackTokens, space, callback);
            try
            {
                if (useDebugLibrary) LinkDebug.sb_register_on_link_removed(space, onLinkRemovedWrapper, new IntPtr(token));
                else LinkRelease.sb_register_on_link_removed(space, onLinkRemovedWrapper, new IntPtr(token));
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                InvalidateCallback(linkRemovedCallbacks, linkRemovedCallbackTokens, space, token);
                return false;
            }
            catch
            {
                InvalidateCallback(linkRemovedCallbacks, linkRemovedCallbackTokens, space, token);
                throw;
            }
        }

        // Lifecycle hooks used by UnregisterCallbacks / CleanupAll (same shape as the element callbacks).
        private static bool DropLinkRemovedCallback(SpaceID space)
        {
            bool had = linkRemovedCallbacks.Remove(space);
            linkRemovedCallbackTokens.Remove(space);
            return had;
        }

        private static void ForgetLinkRemovedCallbacks()
        {
            linkRemovedCallbacks.Clear();
            linkRemovedCallbackTokens.Clear();
        }

        private static void ReleaseLinkRemovedNative(SpaceID space)
        {
            try
            {
                if (useDebugLibrary) LinkDebug.sb_register_on_link_removed(space, null, IntPtr.Zero);
                else LinkRelease.sb_register_on_link_removed(space, null, IntPtr.Zero);
            }
            catch (EntryPointNotFoundException) { }
        }

        [MonoPInvokeCallback(typeof(OnLinkRemovedNativeDelegate))]
        private static void OnLinkRemovedWrapper(SpaceID space, ElementID localElement, long peerSpace, int reason, IntPtr cbUserData)
        {
            if (!TryGetCallback(linkRemovedCallbacks, linkRemovedCallbackTokens, space, cbUserData, out OnLinkRemovedDelegate callback)) return;
            callback(space, localElement, peerSpace, (SbLinkRemovedReason)reason);
        }
    }
}
