// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SpatialBlend.Internal
{
    /// <summary>Mirrors <c>sb_scene_status_t</c>: what a scene mutator returned.</summary>
    internal enum SbSceneStatus : int
    {
        Ok = 0,
        InvalidArgument = 1,
        /// <summary>Two sibling Containment/Navigable regions overlap.</summary>
        ContainmentOverlap = 2,
        /// <summary>A child sticks out of its Containment/Navigable parent.</summary>
        ContainmentEscapesParent = 3
    }

    internal static partial class SpatialBlendAPI
    {
        private static class SceneStatusRelease
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_scene_status(long space, [Out] byte[] message, int capacity);
        }

        private static class SceneStatusDebug
        {
            [DllImport(SpatialBlendLibraryConfig.LibraryNames.DEBUG_LIBRARY, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sb_get_scene_status(long space, [Out] byte[] message, int capacity);
        }

        private static readonly object SceneStatusLock = new object();
        private static readonly Dictionary<long, string> SceneErrors = new Dictionary<long, string>();
        private static bool _sceneStatusProbed;
        private static bool _sceneStatusAvailable;

        /// <summary>
        /// True when the loaded core reports scene-mutator status. Its mutators return <c>sb_scene_status_t</c> only
        /// from the same core that has <c>sb_get_scene_status</c>; an older core's void mutators leave the return
        /// register undefined, so a status is read only when this is true.
        /// </summary>
        internal static bool HasSceneStatus()
        {
            lock (SceneStatusLock)
            {
                if (!_sceneStatusProbed)
                {
                    try
                    {
                        if (useDebugLibrary) SceneStatusDebug.sb_get_scene_status(0, null, 0);
                        else SceneStatusRelease.sb_get_scene_status(0, null, 0);
                        _sceneStatusAvailable = true;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _sceneStatusAvailable = false;
                    }
                    _sceneStatusProbed = true;
                }
                return _sceneStatusAvailable;
            }
        }

        /// <summary>
        /// A scene mutator's result. A change that is not OK was NOT registered, which is never silent: it is logged as an
        /// error with the core's message (naming the elements involved), and kept for
        /// <see cref="TryGetSceneError"/> so the manager can show it.
        /// </summary>
        internal static SbSceneStatus NoteSceneStatus(long space, int rawStatus, string call, long element)
        {
            if (!HasSceneStatus())
            {
                return SbSceneStatus.Ok;
            }
            return ReportSceneStatus(space, rawStatus, call, element, () => ReadSceneStatusMessage(space));
        }

        /// <summary>Pure core of <see cref="NoteSceneStatus"/>: classifies the status and records and logs a rejection.</summary>
        internal static SbSceneStatus ReportSceneStatus(long space, int rawStatus, string call, long element,
                                                        Func<string> message)
        {
            var status = Enum.IsDefined(typeof(SbSceneStatus), rawStatus) ? (SbSceneStatus)rawStatus : SbSceneStatus.InvalidArgument;
            if (status == SbSceneStatus.Ok)
            {
                return status;
            }
            string detail = message?.Invoke();
            string text = $"{call} on element {element} was not registered ({status}" +
                          (string.IsNullOrEmpty(detail) ? ")." : "): " + detail);
            lock (SceneStatusLock)
            {
                SceneErrors[space] = text;
            }
            Debug.LogError("[SpatialBlend] " + text);
            return status;
        }

        /// <summary>The last scene change the core refused on <paramref name="space"/>, or false when none was.</summary>
        internal static bool TryGetSceneError(long space, out string error)
        {
            lock (SceneStatusLock)
            {
                return SceneErrors.TryGetValue(space, out error);
            }
        }

        /// <summary>Forgets the refusals of a space (a new session, or after the app has shown them).</summary>
        internal static void ClearSceneError(long space)
        {
            lock (SceneStatusLock)
            {
                SceneErrors.Remove(space);
            }
        }

        private static string ReadSceneStatusMessage(long space)
        {
            var buffer = new byte[256];
            try
            {
                if (useDebugLibrary) SceneStatusDebug.sb_get_scene_status(space, buffer, buffer.Length);
                else SceneStatusRelease.sb_get_scene_status(space, buffer, buffer.Length);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            int length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }
    }
}
