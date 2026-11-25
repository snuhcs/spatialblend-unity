// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using Meta.XR.BuildingBlocks;
using UnityEngine;

using Oculus.Avatar2;
using Oculus.Avatar2.Experimental;
using CAPI = Oculus.Avatar2.CAPI;
using Meta.XR.MultiplayerBlocks.Shared;

namespace SpatialBlend
{
    /// <summary>
    /// The Avatar Entity implementation for Networked Avatar, which loads a remote/local avatar according to <see cref="IAvatarBehaviour"/>.
    /// It also provides a fallback solution to a local zip avatar with a randomized preloaded avatar from sample assets
    /// when the user is not entitled (no Meta Id) or has no avatar setup.
    /// For more information on the Meta Avatars SDK, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    [DefaultExecutionOrder(50)] // after GpuSkinningConfiguration initializes
    public class MetaAvatarEntity : OvrAvatarEntity, IAvatarStreamConfig
    {
        public static event Action<MetaAvatarEntity> OnSpawned;
        public SbAvatarEntity _sbAvatar;

        [Header("Face Pose Input")]
        [SerializeField]
        private OvrAvatarFacePoseBehavior m_facePoseProvider;
        [SerializeField]
        private OvrAvatarEyePoseBehavior m_eyePoseProvider;

        private byte[] _streamedData;
        private float _cycleStartTime;
        private bool _isSkeletonLoaded;
        private bool _initialAvatarLoaded;
        private SbAvatarBehaviourFusion _avatarBehaviour;
        private StreamLOD _streamLevel = StreamLOD.Medium;
        private float _intervalToSendDataInSec = 0.08f;

        // T2.6: keeps the raw Meta body on the realized root once the hide window is over.
        private readonly ContinuousCompensation _compensation = new ContinuousCompensation();

        /// <summary>
        /// Could be triggered by any changes like oculus id, local avatar index, network connection etc.
        /// </summary>
        public void ReloadAvatarManually()
        {
            if (!_initialAvatarLoaded)
            {
                return;
            }

            _isSkeletonLoaded = false;
            EntityActive = false;
            Teardown();
            CreateEntity();
            LoadAvatar();
        }

        protected override void Awake()
        {
            _avatarBehaviour = this.GetComponent<SbAvatarBehaviourFusion>();
            if (_avatarBehaviour == null)
            {
                throw new InvalidOperationException("Using AvatarEntity without an IAvatarBehaviour");
            }

            OnSpawned?.Invoke(this);
        }

        private void Start()
        {
            if (_avatarBehaviour == null)
            {
                return;
            }

            ConfigureAvatar();
            base.Awake(); // creating avatar entity here

            SetActiveView(_avatarBehaviour.HasInputAuthority ? CAPI.ovrAvatar2EntityViewFlags.FirstPerson : CAPI.ovrAvatar2EntityViewFlags.ThirdPerson);

            LoadAvatar();
            _initialAvatarLoaded = true;
        }

        private OvrAvatarInputManagerBehavior m_bodyTracking;
        public OvrAvatarInputManagerBehavior BodyTracking
        {
            get => m_bodyTracking;
            protected set
            {
                m_bodyTracking = value;
                SetInputManager(value);
                if (value is MetaTrackingInputManager && _avatarBehaviour != null)
                {
                    ((MetaTrackingInputManager)value).SetAvatarBehaviour(_avatarBehaviour);
                }
            }
        }

        private OvrAvatarInputManagerBehavior FindAvatarInputManagerBehavior()
        {
            foreach (var b in FindObjectsByType(typeof(MetaTrackingInputManager), FindObjectsSortMode.None))
            {
                if (((MonoBehaviour)b).isActiveAndEnabled)
                {
                    return (OvrAvatarInputManagerBehavior)b;
                }
            }

            return null;
        }
        private void ConfigureAvatar()
        {
            var isLocal = _avatarBehaviour.HasInputAuthority;
            SetIsLocal(isLocal);
            gameObject.name = isLocal ? "LocalAvatar" : "RemoteAvatar";

            if (isLocal)
            {
                // based on Preset_Default we remove hand scaling for local
                _creationInfo.features &= ~CAPI.ovrAvatar2EntityFeatures.HandScaling;

                var body = FindAvatarInputManagerBehavior();
                BodyTracking = body;

                SetFacePoseProvider(m_facePoseProvider);
                SetEyePoseProvider(m_eyePoseProvider);

                AvatarLODManager.Instance.firstPersonAvatarLod = AvatarLOD;
                AdjustAvatarLOD(_streamLevel);
#if !UNITY_EDITOR
                var lipSyncInput = FindObjectOfType<OvrAvatarLipSyncContext>();
                SetLipSync(lipSyncInput);
#endif
            }
            else
            {
                _creationInfo.features = CAPI.ovrAvatar2EntityFeatures.Preset_Remote;
                _creationInfo.features &= ~CAPI.ovrAvatar2EntityFeatures.UseDefaultAnimHierarchy;

                SetInputManager(null);
                SetFacePoseProvider(null);
                SetEyePoseProvider(null);
#if !UNITY_EDITOR
                SetLipSync(null);
#endif
            }
        }

        private void LoadAvatar()
        {
            if (AvatarLoadDecision.MetaAvatarLoadsPreset(_avatarBehaviour.UsePresetAvatar, _avatarBehaviour.OculusId))
            {
                LoadLocalAvatar();
            }
            else
            {
                StartCoroutine(TryToLoadUserAvatar());
            }
        }

        private IEnumerator TryToLoadUserAvatar()
        {
            while (!OvrAvatarEntitlement.AccessTokenIsValid())
            {
                yield return null;
            }
            _userId = _avatarBehaviour.OculusId;
            var hasAvatarRequest = OvrAvatarManager.Instance.UserHasAvatarAsync(_userId);
            while (hasAvatarRequest.IsCompleted == false)
            {
                yield return null;
            }
            if (hasAvatarRequest.Result == OvrAvatarManager.HasAvatarRequestResultCode.HasAvatar)
            {
                LoadUser();
            }
            else // fallback to local avatar
            {
                LoadLocalAvatar();
            }
        }

        private void LoadLocalAvatar()
        {
            // we only load local avatar from zip after Avatar Sample Assets is installed
            var assetPath = $"{_avatarBehaviour.LocalAvatarIndex}{GetAssetPostfix()}";
            LoadAssets(new[] { assetPath }, AssetSource.Zip);
            Debug.LogWarning("Meta Avatar Sample Assets used for local avatar");
        }

        private string GetAssetPostfix(bool isFromZip = true)
        {
            return "_" + OvrAvatarManager.Instance.GetPlatformGLBPostfix(_creationInfo.renderFilters.quality, isFromZip)
                       + OvrAvatarManager.Instance.GetPlatformGLBVersion(_creationInfo.renderFilters.quality, isFromZip)
                       + OvrAvatarManager.Instance.GetPlatformGLBExtension(isFromZip);
        }

        protected override void OnSkeletonLoaded()
        {
            base.OnSkeletonLoaded();
            _isSkeletonLoaded = true;
            _avatarBehaviour.OnSkeletonLoaded();
        }

        protected override void OnUserAvatarLoaded()
        {
            base.OnUserAvatarLoaded();
            _avatarBehaviour.OnUserAvatarLoaded();
        }

        private void Update()
        {
            if (!_isSkeletonLoaded || _streamedData == null || IsLocal)
            {
                return;
            }

            if (_streamedData == null)
            {
                return;
            }

            //Apply the remote avatar state and smooth the animation
            ApplyStreamData(_streamedData);
            SetPlaybackTimeDelay(_intervalToSendDataInSec / 2);
            _streamedData = null;
        }

        private void LateUpdate()
        {
            if (!_isSkeletonLoaded)
            {
                return;
            }

            // Continuous compensation: align the Meta body to the Sb avatar every frame (v1).
            _compensation.Apply();

            var elapsedTime = Time.time - _cycleStartTime;
            if (elapsedTime < _intervalToSendDataInSec)
            {
                return;
            }

            RecordAndSendStreamDataIfHasAuthority();
            _cycleStartTime = Time.time;
        }

        private void RecordAndSendStreamDataIfHasAuthority()
        {
            if (!IsLocal || _avatarBehaviour == null)
            {
                return;
            }

            var bytes = RecordStreamData(_streamLevel);
            _avatarBehaviour.ReceiveStreamData(bytes);
        }

        /// <summary>
        /// Syncs the current Avatar with the one passed in <paramref name="bytes"/>.
        /// </summary>
        /// <param name="bytes">Byte stream containing the serialized avatar state.</param>
        public void SetStreamData(byte[] bytes)
        {
            _streamedData = bytes;
        }

        private void AdjustAvatarLOD(StreamLOD lod)
        {
            var avatarLod = GetComponent<AvatarLOD>();

            if (avatarLod != null)
            {
                avatarLod.dynamicStreamLod = lod;
            }
        }
        
        /// <summary>
        /// Inside an interaction the raw body replays the source exactly: it is placed by the head anchor's chart (the source head's own
        /// sample through the object, ABI 4.14) that the retarget holds, while the Sb avatar's head keeps the Facing yaw. No chart: the
        /// Sb head joint, as before. The one wiring of the compensation to the retarget, tested as such.
        /// </summary>
        internal static void WireChartReference(ContinuousCompensation compensation, SbRetargetTransform retarget)
        {
            if (compensation == null) return;
            if (retarget == null || retarget.headTransform == null) { compensation.UseChartReference(null, null); return; }
            compensation.UseChartReference(retarget.headTransform, retarget.TryGetHeadChart);
        }

        public void CompensateRotation(Vector3 rotationOffset) =>
            ContinuousCompensation.CompensateRotation(transform, rotationOffset);

        public void CompensatePosition(Vector3 positionOffset) =>
            ContinuousCompensation.CompensatePosition(transform, positionOffset);

        /// <summary>
        /// Starts continuous compensation to align the Meta avatar's position and rotation to the
        /// Sb avatar every frame. Called after the Meta avatar becomes visible (post-invisible
        /// period). Port of v1 <c>MetaAvatarEntity.StartContinuousCompensation</c> (T2.6).
        /// </summary>
        public void StartContinuousCompensation()
        {
            if (_sbAvatar == null)
            {
                Debug.LogError($"MetaAvatarEntity::StartContinuousCompensation - SbAvatar reference is null: {name}");
                return;
            }

            Transform sbHeadJoint = _sbAvatar.GetSkeletonTransform(CAPI.ovrAvatar2JointType.Head);
            if (sbHeadJoint == null)
            {
                Debug.LogError($"MetaAvatarEntity::StartContinuousCompensation - Head joint not found in SbAvatar: {_sbAvatar.name}");
                return;
            }

            Transform metaHeadJoint = GetSkeletonTransform(CAPI.ovrAvatar2JointType.Head);
            if (metaHeadJoint == null)
            {
                Debug.LogError($"MetaAvatarEntity::StartContinuousCompensation - Head joint not found in MetaAvatar: {name}");
                return;
            }

            if (_compensation.Start(transform, metaHeadJoint, sbHeadJoint))
            {
                WireChartReference(_compensation, _sbAvatar._retargetTransform);
            }
        }

        /// <summary>
        /// Stops continuous compensation.
        /// </summary>
        public void StopContinuousCompensation()
        {
            _compensation.Stop();
        }

        public void MakeVisibleAvatar(bool isVisible)
        {
            MeshRenderer[] meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer renderer in meshRenderers)
            {
                renderer.enabled = isVisible;
            }
        }

        public void SetAvatarAlpha(float alpha)
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            var block = new MaterialPropertyBlock();
            foreach (Renderer renderer in renderers)
            {
                renderer.enabled = alpha > 0f;
                renderer.GetPropertyBlock(block);
                block.SetFloat("_AlphaFade", alpha);
                renderer.SetPropertyBlock(block);
            }
        }

        #region IAvatarStreamLOD

        /// <summary>
        /// Sets the quality level to be used when serializing the Avatar state.
        /// An implementation of the <see cref="IAvatarStreamConfig"/> interface.
        /// </summary>
        public void SetAvatarStreamLOD(AvatarStreamLOD lod)
        {
            _streamLevel = lod switch
            {
                AvatarStreamLOD.Low => StreamLOD.Low,
                AvatarStreamLOD.Medium => StreamLOD.Medium,
                AvatarStreamLOD.High => StreamLOD.High,
                _ => StreamLOD.Medium
            };

            AdjustAvatarLOD(_streamLevel);
        }

        /// <summary>
        /// Sets the interval at which the Avatar state is synchronized.
        /// An implementation of the <see cref="IAvatarStreamConfig"/> interface.
        /// </summary>
        public void SetAvatarUpdateIntervalInS(float interval)
        {
            _intervalToSendDataInSec = interval;
        }

        #endregion
    }
}
