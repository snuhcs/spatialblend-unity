// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEditor;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Switches the run mode of the Freeform scene in the Editor. Player builds always run
    /// Fusion mode (FreeformCollaborationApp.ResolveProfile). The Editor default is the
    /// scene's serialized profile, FoundationLocal. The choice is stored in EditorPrefs, so
    /// the scene file is never touched.
    /// </summary>
    public static class FreeformProfileMenu
    {
        // Must equal FreeformCollaborationApp.EditorProfilePrefKey (this assembly cannot reference it).
        public const string PrefKey = "SpatialBlend.Freeform.EditorRuntimeProfile";
        private const string DefaultPath = "SpatialBlend/Freeform Profile/Editor Default (Foundation Local)";
        private const string LocalPath = "SpatialBlend/Freeform Profile/Foundation Local";
        private const string FusionPath = "SpatialBlend/Freeform Profile/Fusion";

        // FreeformCollaborationRuntimeProfile values: Fusion = 0, FoundationLocal = 1.
        private const int Fusion = 0;
        private const int Local = 1;

        [MenuItem(DefaultPath)] private static void SetDefault() => EditorPrefs.DeleteKey(PrefKey);
        [MenuItem(LocalPath)] private static void SetLocal() => EditorPrefs.SetInt(PrefKey, Local);
        [MenuItem(FusionPath)] private static void SetFusion() => EditorPrefs.SetInt(PrefKey, Fusion);

        [MenuItem(DefaultPath, true)]
        private static bool ValidateDefault() => Mark(DefaultPath, !EditorPrefs.HasKey(PrefKey));
        [MenuItem(LocalPath, true)]
        private static bool ValidateLocal() => Mark(LocalPath, EditorPrefs.GetInt(PrefKey, -1) == Local);
        [MenuItem(FusionPath, true)]
        private static bool ValidateFusion() => Mark(FusionPath, EditorPrefs.GetInt(PrefKey, -1) == Fusion);

        private static bool Mark(string path, bool on)
        {
            Menu.SetChecked(path, on);
            return true;
        }
    }
}
