/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Fusion;
using Meta.XR.MultiplayerBlocks.Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using Oculus.Avatar2.Experimental;
using System;
using System.Collections;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;
using UnityEngine;
using Photon.Voice.Unity;
using SpatialBlend.Interactions;

namespace SpatialBlend
{
    /// <summary>
    /// An implementation of the <see cref="IAvatarBehaviour"/> interface to network Avatar states using Photon Fusion.
    /// This class gets or creates an <see cref="AvatarEntity"/> in the same game object and will handle the networking for it.
    /// For more information on the Meta Avatars SDK, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    public class SbAvatarBehaviourFusion : NetworkBehaviour, IAvatarBehaviour
    {
        private const float LERP_TIME = 0.5f;
        private const int AvatarDataStreamMaxCapacity = 900; // Enough storage for an avatar in high LOD
        private Transform _cameraRig;
        private byte[] _buffer;
        private SbRetargetTransform _sbRetargetTransform;
        private OvrAvatarAnimationBehavior _animBehavior;
        public static bool IsLocalInteracting { get; private set; }
        private AvatarDisplayMode _avatarDisplayMode = AvatarDisplayMode.Default;
        private bool _isRemoteInteracting;
        private bool _warnedHandsWithoutAuthority;
        private float _fadeAvatarDuration;
        private Coroutine _fadeCoroutine;
        private Coroutine _hideCoroutine;
        private float _hideElapsed;
        private float _activeHideSeconds;
        private bool _hideRunning;

        internal AvatarDisplayMode DisplayModeForDiagnostics => _avatarDisplayMode;
        internal bool IsInteractingForDiagnostics => _isRemoteInteracting;

        [Networked, OnChangedRender(nameof(OnAvatarIdChanged))] public ulong OculusId { get; set; }
        [Networked, OnChangedRender(nameof(OnAvatarIdChanged))] public int LocalAvatarIndex { get; set; }
        [Networked, OnChangedRender(nameof(OnAvatarIdChanged))] public bool UsePresetAvatar { get; set; }
        [Networked] private int AvatarDataStreamLength { get; set; }

        /// <summary>
        /// Whether this participant is in Editing (Scene Edit). Written by the avatar's owner from its own mode, shown to the others as
        /// an "Editing" badge over the head (<see cref="EditPresenceTag"/>).
        /// </summary>
        [Networked] public NetworkBool IsEditing { get; set; }

        /// <summary>How far over the head the "Editing" badge floats, metres.</summary>
        public const float EditingBadgeAboveHeadMetres = 0.32f;
        private EditPresenceTag _editingBadge;
        public EditPresenceTag EditingBadge => _editingBadge;

        [Networked]
        [Capacity(AvatarDataStreamMaxCapacity)]
        [OnChangedRender(nameof(OnAvatarDataStreamChanged))]
        private NetworkArray<byte> AvatarDataStream => default;
        
        [Header("Sb Avatar")]
        public GameObject sbAvatarPrefab;
        private SbAvatarEntity _sbAvatar;

        private MetaAvatarEntity _metaAvatar;

        [Header("Controllers")]
        public GameObject leftControllerPrefab;
        public GameObject rightControllerPrefab;
        private readonly RemoteAvatarControllerAttachment _controllers = new RemoteAvatarControllerAttachment();

        // HandTransformData replaces the four hand properties and changes the Fusion layout.
        // A v1 build and a v2 build cannot share a room.
        [Networked, OnChangedRender(nameof(OnHandPositionsChanged))]
        public HandTransformData HandData { get; set; }

        /// <summary>
        /// Called by the Photon Fusion networking framework when the network object is spawned.
        /// This method does the required setup for the Avatar state synchronization.
        /// </summary>
        public override void Spawned()
        {
            base.Spawned();
            LoadAvatarDisplaySettings();

            if (OVRManager.instance)
            {
                _cameraRig = OVRManager.instance.GetComponentInChildren<OVRCameraRig>().transform;
            }
            _metaAvatar = GetComponent<MetaAvatarEntity>();
            if (_metaAvatar == null)
            {
                _metaAvatar = gameObject.AddComponent<MetaAvatarEntity>();
            }
            _animBehavior = GetComponent<OvrAvatarAnimationBehavior>();

            // Start visibility coroutine for remote avatars
            if (!HasInputAuthority)
            {
                StartRemoteHide(RemoteAvatarVisibilityPolicy.SpawnedHideSeconds);
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            base.Despawned(runner, hasState);
            if (_editingBadge != null)
            {
                Destroy(_editingBadge.gameObject);
                _editingBadge = null;
            }
            if (_metaAvatar != null)
            {
                _metaAvatar.StopContinuousCompensation();
            }
            if (_sbAvatar != null)
            {
                Destroy(_sbAvatar.gameObject);
            }
        }

        public void SetInteractionState(bool isSource, bool isInteracting)
        {
            if (isSource)
            {
                SetLocalInteractionState(isInteracting);
            }
            else
            {
                SetRemoteInteractionState(isInteracting);
            }
        }

        private void SetLocalInteractionState(bool isInteracting)
        {
            if (!HasInputAuthority)
            {
                return;
            }
            
            IsLocalInteracting = isInteracting;
            
            if (isInteracting)
            {
                _animBehavior.enabled = true;
            }
            else
            {
                _animBehavior.enabled = false;
            }
        }

        private void SetRemoteInteractionState(bool isInteracting)
        {
            if (HasInputAuthority)
            {
                return;
            }
            _isRemoteInteracting = isInteracting;
            ApplyRemoteAvatarVisibility();
        }

        public void RefreshAvatarDisplayMode()
        {
            LoadAvatarDisplaySettings();
            ApplyRemoteAvatarVisibility();
        }

        private void LoadAvatarDisplaySettings()
        {
            if (SpatialBlendManager.CurrentSettings == null)
            {
                return;
            }
            _avatarDisplayMode = SpatialBlendManager.CurrentSettings.avatarDisplayMode;
            _fadeAvatarDuration = SpatialBlendManager.CurrentSettings.fadeAvatarDuration;
        }

        /// <summary>
        /// Called by the Photon Fusion networking framework on every network update tick.
        /// This method syncs the Avatar position to the player's Camera Rig.
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority)
            {
                return;
            }

            if (_cameraRig == null)
            {
                return;
            }

            // var t = transform;
            // transform.position = Vector3.Lerp(t.position, _cameraRig.position, Runner.DeltaTime / LERP_TIME);
            // transform.rotation = Quaternion.Lerp(t.rotation, _cameraRig.rotation, Runner.DeltaTime / LERP_TIME);
            // set left & right hand position & rotation for sbavatar in here
        }

        public override void Render()
        {
            base.Render();
            if (Object == null || !Object.IsValid) return;
            if (HasStateAuthority)
            {
                // The owner publishes its own mode; nobody else writes it.
                bool editing = InteractionModeManager.IsMode(InteractionMode.SceneEdit);
                if (IsEditing != editing) IsEditing = editing;
                return;
            }
            UpdateEditingBadge();
        }

        // The badge over a remote participant's head while that participant is in Editing (hidden while the avatar itself is hidden).
        private void UpdateEditingBadge()
        {
            Transform head = _sbRetargetTransform != null ? _sbRetargetTransform.headTransform : null;
            if (!ShowsEditingBadge(HasStateAuthority, IsEditing, head != null, _hideRunning))
            {
                if (_editingBadge != null) _editingBadge.Hide();
                return;
            }
            if (_editingBadge == null)
                _editingBadge = EditPresenceTag.Create(name + "-EditingBadge", head, Vector3.up * EditingBadgeAboveHeadMetres);
            _editingBadge.Show(EditLeaseText.Editing);
        }

        /// <summary>The badge rule: a remote participant in Editing, with a head to show it over, while its avatar is shown.</summary>
        public static bool ShowsEditingBadge(bool isLocal, bool isEditing, bool hasHead, bool avatarHidden) =>
            !isLocal && isEditing && hasHead && !avatarHidden;

        public void OnSkeletonLoaded()
        {
            // Instantiate the SbAvatar for remote players
            if (!HasInputAuthority && sbAvatarPrefab != null)
            {
                GameObject _sbAvatarInstance = Instantiate(sbAvatarPrefab);
                _sbAvatarInstance.name = $"{gameObject.name}_sb";
                _sbAvatar = _sbAvatarInstance.GetComponent<SbAvatarEntity>();
                _sbAvatar._networkId = Object.Id.Raw;
                _sbAvatar._cdnUserID = OculusId;
                _sbRetargetTransform = _sbAvatar._retargetTransform;
                if (_sbRetargetTransform == null)
                {
                    SpatialBlendDebug.LogError(LogCategory.Scene, "SbAvatarBehaviourFusion::OnSkeletonLoaded - SbRetargetTransform not found");
                    return;
                }
                _sbAvatar._metaAvatar = _metaAvatar;
                _metaAvatar._sbAvatar = _sbAvatar;

                // Meta wrists are ready now. Sb wrists wait for OnSkeletonLoadedCallback.
                BindRemoteControllers();

                // Call TryLazySync() for late sbavatar/metavatar creation
                var managers = FindObjectsByType<SpatialBlendManager>(FindObjectsSortMode.None);
                foreach (var manager in managers)
                {
                    manager.TryLazySync();
                }

                ApplyRemoteAvatarVisibility();
            }
        }

        private void BindRemoteControllers()
        {
            if (_metaAvatar == null || _sbAvatar == null)
            {
                return;
            }

            _controllers.Bind(
                leftControllerPrefab,
                rightControllerPrefab,
                _metaAvatar.GetSkeletonTransform(Oculus.Avatar2.CAPI.ovrAvatar2JointType.LeftHandWrist),
                _metaAvatar.GetSkeletonTransform(Oculus.Avatar2.CAPI.ovrAvatar2JointType.RightHandWrist),
                callback => _sbAvatar.OnSkeletonLoadedCallback += callback,
                () => _sbAvatar.GetSkeletonTransform(Oculus.Avatar2.CAPI.ovrAvatar2JointType.LeftHandWrist),
                () => _sbAvatar.GetSkeletonTransform(Oculus.Avatar2.CAPI.ovrAvatar2JointType.RightHandWrist),
                () => _controllers.Apply(RemoteAvatarVisibilityPolicy.Evaluate(
                    VisibilityInput(RemoteAvatarVisibilityPolicy.NoFadeSample))));
        }

        public void OnUserAvatarLoaded()
        {
            if (!HasInputAuthority)
            {
                StartRemoteHide(RemoteAvatarVisibilityPolicy.UserAvatarLoadedHideSeconds);
                return;
            }

            if (_metaAvatar == null)
            {
                return;
            }

            RemoteAvatarVisibilityDecision local = RemoteAvatarVisibilityPolicy.Evaluate(VisibilityInput(RemoteAvatarVisibilityPolicy.NoFadeSample));
            _metaAvatar.MakeVisibleAvatar(local.MetaVisible);
        }

        private void StartRemoteHide(float seconds)
        {
            if (HasInputAuthority)
            {
                return;
            }

            if (_hideCoroutine != null)
            {
                StopCoroutine(_hideCoroutine);
            }

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            _hideCoroutine = StartCoroutine(KeepAvatarsInvisibleThenShow(seconds));
        }

        private IEnumerator KeepAvatarsInvisibleThenShow(float duration)
        {
            _activeHideSeconds = duration;
            _hideElapsed = 0f;
            _hideRunning = true;
            while (_hideElapsed < duration)
            {
                ApplyRemoteAvatarVisibility();
                _hideElapsed += Time.deltaTime;
                yield return null;
            }

            _hideRunning = false;
            _hideElapsed = duration;
            _hideCoroutine = null;
            ApplyRemoteAvatarVisibility();

            // v1 (T2.6): after the hide window the Meta body is aligned to the realized Sb
            // avatar every frame, so the raw Meta stream stays on the realized root.
            if (_metaAvatar != null && _sbAvatar != null)
            {
                _metaAvatar.StartContinuousCompensation();
            }
        }

        private void ApplyRemoteAvatarVisibility()
        {
            if (_metaAvatar == null)
            {
                return;
            }

            RemoteAvatarVisibilityDecision decision = RemoteAvatarVisibilityPolicy.Evaluate(
                VisibilityInput(RemoteAvatarVisibilityPolicy.NoFadeSample));
            if (!decision.ApplyRemoteRules)
            {
                return;
            }

            _controllers.Apply(decision);

            if (decision.ApplySbVisibility && _sbAvatar != null)
            {
                _sbAvatar.MakeVisibleAvatar(decision.SbVisible);
            }

            if (decision.StartFade)
            {
                if (_fadeCoroutine != null)
                {
                    StopCoroutine(_fadeCoroutine);
                }

                _fadeCoroutine = StartCoroutine(FadeAvatarTransition());
                return;
            }

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (decision.ApplyMetaVisibility)
            {
                _metaAvatar.MakeVisibleAvatar(decision.MetaVisible);
            }
        }

        private IEnumerator FadeAvatarTransition()
        {
            float elapsed = 0f;
            while (elapsed < _fadeAvatarDuration)
            {
                elapsed += Time.deltaTime;
                float progress = _fadeAvatarDuration > 0f ? Mathf.Clamp01(elapsed / _fadeAvatarDuration) : 1f;
                RemoteAvatarVisibilityDecision sample = RemoteAvatarVisibilityPolicy.Evaluate(VisibilityInput(progress));
                if (_metaAvatar != null && sample.ApplyMetaAlpha)
                {
                    _metaAvatar.SetAvatarAlpha(sample.MetaAlpha);
                }

                _controllers.Apply(sample);
                yield return null;
            }

            RemoteAvatarVisibilityDecision end = RemoteAvatarVisibilityPolicy.Evaluate(VisibilityInput(1f));
            _controllers.Apply(end);
            if (_metaAvatar != null)
            {
                if (end.ApplyMetaAlpha)
                {
                    _metaAvatar.SetAvatarAlpha(end.MetaAlpha);
                }

                if (end.ApplyMetaVisibility)
                {
                    _metaAvatar.MakeVisibleAvatar(end.MetaVisible);
                }
            }

            _fadeCoroutine = null;
        }

        private RemoteAvatarVisibilityInput VisibilityInput(float fadeProgress)
        {
            return new RemoteAvatarVisibilityInput(
                _avatarDisplayMode,
                _isRemoteInteracting,
                HasInputAuthority,
                _hideElapsed,
                _hideRunning ? _activeHideSeconds : 0f,
                _fadeAvatarDuration,
                fadeProgress);
        }

        private void OnAvatarIdChanged()
        {
            if (_metaAvatar != null)
            {
                _metaAvatar.ReloadAvatarManually();
            }
        }

        private void OnAvatarDataStreamChanged()
        {
            if (Object.HasStateAuthority)
            {
                return;
            }

            if (_buffer == null || _buffer.Length != AvatarDataStreamLength)
            {
                _buffer = new byte[AvatarDataStreamLength];
            }

            AvatarDataStream.CopyTo(_buffer);
            _metaAvatar.SetStreamData(_buffer);
        }


        #region sbAvatarHandPosition
        /// <summary>
        /// Updates hand positions through Fusion network using Networked Properties.
        /// This method should only be called by the local player with Input Authority.
        /// </summary>
        /// <param name="leftHandPos">Left hand position relative to head</param>
        /// <param name="rightHandPos">Right hand position relative to head</param>
        /// <param name="leftHandRot">Left hand rotation relative to head</param>
        /// <param name="rightHandRot">Right hand rotation relative to head</param>
        public void UpdateHandPositions(Vector3 leftHandPos, Vector3 rightHandPos, Quaternion leftHandRot, Quaternion rightHandRot)
        {
            if (!HasInputAuthority)
            {
                // Once: the caller runs every tracking frame.
                if (!_warnedHandsWithoutAuthority)
                    Debug.LogWarning("UpdateHandPositions called without input authority. Only local player can update hand positions.");
                _warnedHandsWithoutAuthority = true;
                return;
            }
            HandData = new HandTransformData
            {
                LeftPosition = leftHandPos,
                RightPosition = rightHandPos,
                LeftRotation = leftHandRot,
                RightRotation = rightHandRot
            };
        }

        /// <summary>
        /// Callback when hand positions change (triggered by OnChangedRender)
        /// </summary>
        private void OnHandPositionsChanged()
        {
            // Apply hand positions to remote avatar if it exists
            if (_sbAvatar != null && !HasInputAuthority)
            {
                HandTransformData hands = HandData;
                _sbRetargetTransform.SetHandTransform(
                    hands.LeftPosition,
                    hands.RightPosition,
                    hands.LeftRotation,
                    hands.RightRotation,
                    Runner.DeltaTime / LERP_TIME);
            }
        }
        #endregion

        #region IAvatarBehaviour

        /// <summary>
        /// Loads a serialized avatar state and update it in the local client.
        /// An implementation of the <see cref="IAvatarBehaviour"/> interface.
        /// </summary>
        /// <param name="bytes">A byte stream containing the serialized avatar state.</param>
        public void ReceiveStreamData(byte[] bytes)
        {
            if (bytes.Length > AvatarDataStreamMaxCapacity)
            {
                Debug.LogError($"[{nameof(SbAvatarBehaviourFusion)}] Cannot send a stream data of length {bytes.Length} greater than the max capacity of {AvatarDataStreamMaxCapacity}");
                return;
            }

            AvatarDataStreamLength = bytes.Length;
            AvatarDataStream.CopyFrom(bytes, 0, bytes.Length);
        }

        #endregion
    }
}
