// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpatialBlend.Internal
{
    internal static partial class SpatialBlendAPI
    {
        private static class ReparentRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_reparent_element(long space, long elem, long newParent, double[] matrix4x4);

            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_element_parent(long space, long elem, out long parent);
        }

        private static class ReparentDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_reparent_element(long space, long elem, long newParent, double[] matrix4x4);

            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_element_parent(long space, long elem, out long parent);
        }

        /// <summary>The element's parent in the core; false when it has none or the core does not know it.</summary>
        internal static bool TryGetElementParent(long space, long element, out long parent)
        {
            parent = 0;
            try
            {
                int ok = useDebugLibrary ? ReparentDebug.sb_get_element_parent(space, element, out parent)
                    : ReparentRelease.sb_get_element_parent(space, element, out parent);
                return ok == 1 && parent > 0;
            }
            catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
            {
                return false;
            }
        }

        private static bool _reparentProbed;
        private static bool _reparentAvailable;
        private static bool _reparentMissingReported;

        /// <summary>True when the loaded core has <c>sb_reparent_element</c> (ABI 4.8).</summary>
        internal static bool HasReparentElement()
        {
            lock (SceneStatusLock)
            {
                if (!_reparentProbed)
                {
                    try
                    {
                        // A null space is an invalid argument; only the entry point's presence is probed.
                        if (useDebugLibrary) ReparentDebug.sb_reparent_element(0, 0, 0, null);
                        else ReparentRelease.sb_reparent_element(0, 0, 0, null);
                        _reparentAvailable = true;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _reparentAvailable = false;
                    }
                    _reparentProbed = true;
                }
                return _reparentAvailable;
            }
        }

        /// <summary>
        /// Moves an element, with its subtree, identity, shared id and links, under another parent (ABI 4.8). The new
        /// parent-local transform (Unity space) is applied with the move so the core's overlap check sees the placement
        /// the element will have. A refused move leaves the element where it was and is logged and kept like any scene change.
        /// </summary>
        internal static bool TryReparentElement(long space, long element, long newParent, Matrix4x4 unityLocalTransform)
        {
            if (!HasReparentElement())
            {
                // Said once, with the cause: without ABI 4.8 content on observed geometry cannot be nested by the hierarchy.
                if (!_reparentMissingReported)
                {
                    _reparentMissingReported = true;
                    Debug.LogWarning("[SpatialBlend] the loaded core has no sb_reparent_element (ABI 4.8): content that stands on observed " +
                                     "geometry is not re-nested when the geometry changes. Rebuild the native libraries from a core with ABI 4.8.");
                }
                return false;
            }
            double[] matrix = ToRowMajorSbMatrix(unityLocalTransform);
            int raw = useDebugLibrary ? ReparentDebug.sb_reparent_element(space, element, newParent, matrix)
                : ReparentRelease.sb_reparent_element(space, element, newParent, matrix);
            return NoteSceneStatus(space, raw, "sb_reparent_element", element) == SbSceneStatus.Ok;
        }
    }
}
