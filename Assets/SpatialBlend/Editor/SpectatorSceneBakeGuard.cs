// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Fusion.Editor;
using UnityEditor;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Keeps Fusion's edit-time baker (on scene save and on entering Play Mode) from re-baking the spectator scene. The baker
    /// sets each scene NetworkObject's SortKey to a hash of its own object ids; the spectator scene carries the Freeform
    /// scene's SortKeys on purpose (see <see cref="SpectatorBuild.CreateScene"/>), and a re-bake would replace them.
    /// </summary>
    [InitializeOnLoad]
    public static class SpectatorSceneBakeGuard
    {
        /// <summary>Set only while the scene is first written, so the baker fills in everything but the keys once.</summary>
        public static bool Bake;

        static SpectatorSceneBakeGuard()
        {
            NetworkObjectPostprocessor.OnBakeScene += args =>
            {
                if (!Bake && args.Scene.path == SpectatorBuild.ScenePath) args.Handled = true;
            };
        }
    }
}
