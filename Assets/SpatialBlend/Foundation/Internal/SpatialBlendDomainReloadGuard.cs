// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using UnityEditor;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// The Editor never unloads the native library, so sessions and callback
    /// slots created in one managed domain outlive it. After a reload the slots
    /// point at unloaded code and the next native delivery (teardown, a poll, a
    /// network event) crashes the Editor. Release everything this domain still
    /// holds before it unloads; components that own sessions have already torn
    /// theirs down in OnDisable by then.
    /// </summary>
    [InitializeOnLoad]
    internal static class SpatialBlendDomainReloadGuard
    {
        static SpatialBlendDomainReloadGuard()
        {
            AssemblyReloadEvents.beforeAssemblyReload += TearDown;
            EditorApplication.quitting += TearDown;
        }

        internal static void TearDown()
        {
            if (SpatialBlendAPI.HasLiveNativeState) SpatialBlendAPI.CleanupAll();
        }
    }
}
#endif
