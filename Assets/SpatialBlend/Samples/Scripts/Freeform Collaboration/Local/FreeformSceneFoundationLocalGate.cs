// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Fusion;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>
    /// Scene-level admission gate for the explicit FoundationLocal profile. It
    /// disables the authored legacy bootstrap during Awake, before any Start-mode
    /// or virtual-instance transport work can run. It never starts that bootstrap.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    [DisallowMultipleComponent]
    public sealed class FreeformSceneFoundationLocalGate : MonoBehaviour
    {
        [SerializeField] private FreeformCollaborationApp _app;
        [SerializeField] private FusionBootstrap _fusionBootstrap;
        [SerializeField] private SbAvatarSpawnerFusion _avatarSpawner;
        private bool _hasSuppressed;
        private bool _bootstrapEnabledBeforeSuppression;
        private bool _avatarSpawnerEnabledBeforeSuppression;

        public bool FoundationTransportSuppressed => HasLoadedSceneReferences() && _app.UsesFoundationLocal &&
                                                     _fusionBootstrap != null &&
                                                     !_fusionBootstrap.enabled &&
                                                     _avatarSpawner != null &&
                                                     !_avatarSpawner.enabled;

        /// <summary>
        /// True only in FoundationLocal mode (T3.5). Fusion mode keeps the Fusion
        /// motifs as the content writer, so the local backend must never bind.
        /// </summary>
        public bool PermitsLocalContentWriter => HasLoadedSceneReferences() &&
            FreeformContentWriterPolicy.AllowsLocalBackendToBind(_app.UsesFoundationLocal);

        private void Awake() => SuppressFoundationLocalTransport();
        private void OnEnable() => SuppressFoundationLocalTransport();

        public bool SuppressFoundationLocalTransport()
        {
            if (!HasLoadedSceneReferences())
                return false;
            if (!_app.UsesFoundationLocal)
            {
                // Do not invent a legacy bootstrap state.  Only restore a value
                // this gate actually captured from the authored scene.
                if (_hasSuppressed)
                {
                    _fusionBootstrap.enabled = _bootstrapEnabledBeforeSuppression;
                    _avatarSpawner.enabled = _avatarSpawnerEnabledBeforeSuppression;
                }
                _hasSuppressed = false;
                return false;
            }
            if (!_hasSuppressed)
            {
                _bootstrapEnabledBeforeSuppression = _fusionBootstrap.enabled;
                _avatarSpawnerEnabledBeforeSuppression = _avatarSpawner.enabled;
                _hasSuppressed = true;
            }
            _fusionBootstrap.enabled = false;
            _avatarSpawner.enabled = false;
            return true;
        }

        private bool HasLoadedSceneReferences()
        {
            return IsInOwningScene(_app) && IsInOwningScene(_fusionBootstrap) &&
                   IsInOwningScene(_avatarSpawner);
        }

        private bool IsInOwningScene(Component component)
        {
            return component != null && component.gameObject.scene.IsValid() &&
                   component.gameObject.scene.isLoaded && component.gameObject.scene == gameObject.scene;
        }
    }
}
