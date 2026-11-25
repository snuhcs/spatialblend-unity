// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// Explicit original-scene declaration for optional local room observations.
    /// The host is an authored scene root; availability of a live MRUK room remains
    /// device/runtime dependent and never prevents local authored content starting.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneRoomGeometryDefinition : MonoBehaviour
    {
        // This is deliberately a component reference, rather than a global lookup
        // or arbitrary named Transform. The original scene owns the configured
        // MR Utility Kit building block (including its prefab fallback settings).
        [SerializeField] private MRUK _mruk;
        [SerializeField] private string _scopeId = "freeform-scene-local-room";

        public MRUK Mruk => _mruk;
        public string ScopeId => _scopeId;

        public bool ValidateForLoadedScene(Scene scene, out string error)
        {
            error = null;
            if (!IsSceneInstance(this, scene) || !IsSceneInstance(_mruk, scene) ||
                string.IsNullOrWhiteSpace(_scopeId) || _scopeId.IndexOf('\0') >= 0)
            {
                error = "Foundation Local room geometry requires an explicit loaded-scene MRUK component and valid scope ID.";
                return false;
            }
            return true;
        }

        private static bool IsSceneInstance(Component value, Scene scene) =>
            value != null && IsSceneInstance(value.transform, scene);

        private static bool IsSceneInstance(Transform value, Scene scene) =>
            value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded &&
            value.gameObject.scene == scene;
    }
}
