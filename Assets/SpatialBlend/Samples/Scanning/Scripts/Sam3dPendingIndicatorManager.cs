// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>
    /// Manages the "SAM3D pending" indicators and the scan sounds, keyed by scan event handle.
    /// The panel spawns an indicator when it sends a request. ScanService.PendingChanged drives the
    /// rest: sounds on Started, and a fade-out plus the completion sound on the terminal event.
    /// A failed, timed-out or cancelled request fades the indicator the same way, so none is left behind.
    /// </summary>
    [DisallowMultipleComponent]
    public class Sam3dPendingIndicatorManager : MonoBehaviour
    {
        [SerializeField, Tooltip("Prefab shown while SAM3D inference request is pending. Assign in inspector.")]
        private GameObject _sam3dPendingIndicatorPrefab;
        [SerializeField, Tooltip("Prefab shown while shared object is loading. Assign in inspector.")]
        private GameObject _sharedObjectPendingIndicatorPrefab;
        [SerializeField, Min(0f), Tooltip("Fade-out duration (seconds) for shared-object loading indicator.")]
        private float _fadeOutDuration = 1.0f;
        [SerializeField, Min(0f), Tooltip("Minimum visible duration (seconds) for shared-object loading.")]
        private float _sharedObjectMinVisibleDuration = 4.0f;
        [SerializeField] private bool _debugLog = false;

        [Header("Scan SFX")]
        [SerializeField] private AudioClip _scanTriggerClip;
        [SerializeField] private AudioClip _scanInProgressClip;
        [SerializeField] private AudioClip _scanCompleteClip;
        [SerializeField, Range(0f, 1f)] private float _sfxVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] private float _scanInProgressVolume = 0.15f;

        private readonly Dictionary<long, IndicatorFader> _pendingIndicatorsByEventHandle = new();
        private readonly Dictionary<int, IndicatorFader> _pendingIndicatorsByInstanceId = new();
        private readonly Dictionary<long, AudioSource> _loopingSources = new();
        private ScanService _boundService;

        private void Awake()
        {
            // Not ??=: that bypasses Unity's overloaded null check, so a destroyed reference would be kept.
            if (_scanTriggerClip == null) _scanTriggerClip = Resources.Load<AudioClip>("SFX/ScanTrigger");
            if (_scanInProgressClip == null) _scanInProgressClip = Resources.Load<AudioClip>("SFX/ScanInProgress");
            if (_scanCompleteClip == null) _scanCompleteClip = Resources.Load<AudioClip>("SFX/ScanComplete");
        }

        private void OnDisable()
        {
            Unbind();
        }

        private void OnDestroy()
        {
            Unbind();
            CleanupPendingIndicators();
            StopAllLoops();
        }

        // The service is created when SessionManager initializes, which can be after this component wakes.
        private void Update()
        {
            ScanService current = SessionManager.Instance != null ? SessionManager.Instance.Service : null;
            if (current == _boundService) return;
            Unbind();
            if (current != null)
            {
                _boundService = current;
                _boundService.PendingChanged += OnPendingChanged;
            }
        }

        private void Unbind()
        {
            if (_boundService == null) return;
            _boundService.PendingChanged -= OnPendingChanged;
            _boundService = null;
        }

        public bool TrySpawnPendingIndicator(
            long eventHandle,
            Vector3? spawnPosition = null,
            Quaternion? spawnRotation = null,
            Vector3? spawnScale = null)
        {
            if (eventHandle <= 0)
            {
                return false;
            }

            var prefab = _sam3dPendingIndicatorPrefab;
            if (prefab == null)
            {
                if (_debugLog)
                {
                    Debug.LogWarning("[Sam3dPendingIndicatorManager] Pending indicator prefab is not assigned.");
                }
                return false;
            }

            Vector3 resolvedPosition = spawnPosition ?? Vector3.zero;
            Vector3 resolvedScale = spawnScale ?? Vector3.one;

            // Only position and scale are applied to the indicator, not rotation.
            var instance = Instantiate(prefab);
            instance.transform.position = resolvedPosition;
            instance.transform.localScale = Vector3.Scale(instance.transform.localScale, resolvedScale);
            var fader = instance.GetComponent<IndicatorFader>();
            if (fader == null)
            {
                fader = instance.AddComponent<IndicatorFader>();
            }

            return TryRegisterPendingIndicator(eventHandle, fader);
        }

        public bool TrySpawnSharedObjectPendingIndicator(
            int ownerInstanceId,
            Vector3? spawnPosition = null,
            Quaternion? spawnRotation = null,
            Vector3? spawnScale = null)
        {
            return SpawnSharedObjectPendingIndicator(ownerInstanceId, spawnPosition, spawnRotation, spawnScale) != null;
        }

        /// <summary>
        /// Shows the shared-object portal for an owner and returns it as the token to pass to
        /// <see cref="OnSharedObjectLoadCompleted"/>. A later spawn for the same owner replaces this one, and the
        /// replaced token then completes nothing. Null when no portal was shown.
        /// </summary>
        public IndicatorFader SpawnSharedObjectPendingIndicator(
            int ownerInstanceId,
            Vector3? spawnPosition = null,
            Quaternion? spawnRotation = null,
            Vector3? spawnScale = null)
        {
            if (ownerInstanceId == 0)
            {
                return null;
            }

            var prefab = _sharedObjectPendingIndicatorPrefab;
            if (prefab == null)
            {
                if (_debugLog)
                {
                    Debug.LogWarning("[Sam3dPendingIndicatorManager] Pending indicator prefab is not assigned.");
                }
                return null;
            }

            Vector3 resolvedPosition = spawnPosition ?? Vector3.zero;
            Vector3 resolvedScale = spawnScale ?? Vector3.one;

            var instance = Instantiate(prefab);
            instance.transform.position = resolvedPosition;
            instance.transform.localScale = Vector3.Scale(instance.transform.localScale, resolvedScale);
            var fader = instance.GetComponent<IndicatorFader>();
            if (fader == null)
            {
                fader = instance.AddComponent<IndicatorFader>();
            }

            if (_pendingIndicatorsByInstanceId.TryGetValue(ownerInstanceId, out var existing) && existing != null)
            {
                DestroyPortal(existing.gameObject);
            }
            _pendingIndicatorsByInstanceId[ownerInstanceId] = fader;
            return fader;
        }

        /// <summary>True while a portal registered for this owner has not completed.</summary>
        public bool HasSharedObjectPendingIndicator(int ownerInstanceId) =>
            _pendingIndicatorsByInstanceId.TryGetValue(ownerInstanceId, out var fader) && fader != null;

        /// <summary>
        /// Fades the portal the token names. It does nothing when the owner's entry is a different portal, so a
        /// stale load that finishes after a newer one started cannot fade the newer portal.
        /// </summary>
        public void OnSharedObjectLoadCompleted(int ownerInstanceId, IndicatorFader token, string ownerName = null)
        {
            if (!_pendingIndicatorsByInstanceId.TryGetValue(ownerInstanceId, out var pendingFader) ||
                !ReferenceEquals(pendingFader, token))
            {
                if (_debugLog)
                {
                    string resolvedOwnerName = string.IsNullOrEmpty(ownerName) ? "unknown" : ownerName;
                    Debug.LogWarning($"[Sam3dPendingIndicatorManager] Shared-object pending indicator is not current for owner={resolvedOwnerName}({ownerInstanceId}).");
                }
                return;
            }

            _pendingIndicatorsByInstanceId.Remove(ownerInstanceId);
            if (pendingFader == null) return;
            pendingFader.StartFadeOut(_fadeOutDuration + _sharedObjectMinVisibleDuration);
        }

        /// <summary>
        /// Runs a content load under the shared-object portal. The portal is shown first and completed in a
        /// finally, so a load that throws or returns false still clears it.
        /// </summary>
        public async Task<bool> RunWithSharedObjectPortal(
            int ownerInstanceId, string ownerName, Vector3 position, Quaternion rotation, Func<Task<bool>> load)
        {
            IndicatorFader token = SpawnSharedObjectPendingIndicator(ownerInstanceId, position, rotation);
            try
            {
                return await load();
            }
            finally
            {
                if (token != null) OnSharedObjectLoadCompleted(ownerInstanceId, token, ownerName);
            }
        }

        // Destroy is not allowed in EditMode tests; the portal is a plain scene object either way.
        private static void DestroyPortal(GameObject portal)
        {
            if (Application.isPlaying) Destroy(portal);
            else DestroyImmediate(portal);
        }

        public bool TryRegisterPendingIndicator(long eventHandle, IndicatorFader fader)
        {
            if (eventHandle <= 0 || fader == null)
            {
                return false;
            }

            if (_pendingIndicatorsByEventHandle.TryGetValue(eventHandle, out var existing) && existing != null)
            {
                Destroy(existing.gameObject);
            }

            _pendingIndicatorsByEventHandle[eventHandle] = fader;

            return true;
        }

        public void CleanupPendingIndicators()
        {
            foreach (var pair in _pendingIndicatorsByEventHandle)
            {
                if (pair.Value != null)
                {
                    Destroy(pair.Value.gameObject);
                }
            }

            _pendingIndicatorsByEventHandle.Clear();

            foreach (var pair in _pendingIndicatorsByInstanceId)
            {
                if (pair.Value != null)
                {
                    Destroy(pair.Value.gameObject);
                }
            }
            _pendingIndicatorsByInstanceId.Clear();
        }

        private void OnPendingChanged(ScanPendingEvent pending)
        {
            // A segmentation request has no indicator and no sound in v1.
            if (pending.Kind == ScanKind.Segment) return;

            Vector3 position = pending.HasAnchor
                ? pending.Anchor.Center
                : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);

            if (pending.Phase == ScanPendingPhase.Started)
            {
                PlayAt(_scanTriggerClip, position);
                StartLoop(pending.EventHandle, position);
                return;
            }

            StopLoop(pending.EventHandle);
            if (pending.Phase == ScanPendingPhase.Succeeded) PlayAt(_scanCompleteClip, position);
            FadeIndicator(pending);
        }

        private void FadeIndicator(ScanPendingEvent pending)
        {
            if (!_pendingIndicatorsByEventHandle.TryGetValue(pending.EventHandle, out var pendingFader))
            {
                if (_debugLog)
                {
                    Debug.LogWarning($"[Sam3dPendingIndicatorManager] Pending indicator fader not found for event={pending.EventHandle}.");
                }
                return;
            }

            _pendingIndicatorsByEventHandle.Remove(pending.EventHandle);
            if (pendingFader == null) return;

            pendingFader.StartFadeOut(_fadeOutDuration);
        }

        private void PlayAt(AudioClip clip, Vector3 position)
        {
            if (clip != null) AudioSource.PlayClipAtPoint(clip, position, _sfxVolume);
        }

        private void StartLoop(long eventHandle, Vector3 position)
        {
            if (_scanInProgressClip == null) return;

            var go = new GameObject($"ScanLoop_{eventHandle}");
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.clip = _scanInProgressClip;
            source.loop = true;
            source.volume = _scanInProgressVolume;
            source.spatialBlend = 1f;
            source.Play();
            _loopingSources[eventHandle] = source;
        }

        private void StopLoop(long eventHandle)
        {
            if (!_loopingSources.TryGetValue(eventHandle, out var source)) return;
            _loopingSources.Remove(eventHandle);
            if (source != null)
            {
                source.Stop();
                Destroy(source.gameObject);
            }
        }

        private void StopAllLoops()
        {
            foreach (var pair in _loopingSources)
            {
                if (pair.Value != null)
                {
                    pair.Value.Stop();
                    Destroy(pair.Value.gameObject);
                }
            }
            _loopingSources.Clear();
        }
    }
}
