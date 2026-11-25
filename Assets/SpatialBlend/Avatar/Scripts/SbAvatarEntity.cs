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
// Modified by the SpatialBlend authors; this file stays under the license above (see NOTICE).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

using Oculus.Avatar2;
using CAPI = Oculus.Avatar2.CAPI;
using Meta.XR.MultiplayerBlocks.Shared;
using Oculus.Platform;

using UnityEngine;

namespace SpatialBlend
{ 
    public class SbAvatarEntity : OvrAvatarEntity
    {
        private const string logScope = "sbAvatar";

        /// <summary>
        /// State for the deliberately opt-in local-preset path.  The default remains
        /// <see cref="Legacy"/> so the authored Networked Avatar/Fusion flow is not
        /// changed by merely updating this component.
        /// </summary>
        public enum LocalPresetStatus
        {
            Legacy,
            Configured,
            Initialized,
            Unavailable,
            Failed
        }

        public MetaAvatarEntity _metaAvatar;
        public SbRetargetTransform _retargetTransform;

        /// <summary>
        /// Invoked once the SbAvatar skeleton has finished loading.
        /// Subscribe before the skeleton loads (right after Instantiate).
        /// </summary>
        public Action OnSkeletonLoadedCallback;

        [Header("Explicit Local Preset (opt-in)")]
        [SerializeField]
        private bool _localPresetMode;
        [SerializeField]
        private SbTransformInputManager _localPresetInput;
        [SerializeField]
        private SbRetargetTransform _localPresetRetarget;
        [SerializeField]
        private int _localPresetIndex;
        [NonSerialized]
        private LocalPresetStatus _localPresetStatus = LocalPresetStatus.Legacy;
        [NonSerialized]
        private string _localPresetError;

        private SbAvatarBehaviourFusion _behaviourFusion;

        [NonSerialized]
        private bool _localPresetThirdPerson;

        /// <summary>
        /// For a local preset that is looked at from outside (the lobby mirror): the SDK's third-person view, which draws the head. The
        /// default first-person view hides it for the owner's own eyes. Call before activation. The view is applied right after the SDK entity is created (local-preset Start) and again when the skeleton loads, so it does not depend on one callback's timing; it never changes a networked avatar.
        /// </summary>
        public void RequestLocalPresetThirdPersonView() => _localPresetThirdPerson = true;
        public bool LocalPresetThirdPersonRequested => _localPresetThirdPerson;

        public bool IsLocalPresetMode => _localPresetMode;
        public LocalPresetStatus CurrentLocalPresetStatus => _localPresetStatus;
        public string LocalPresetError => _localPresetError;
        public SbTransformInputManager LocalPresetInput => _localPresetInput;
        public SbRetargetTransform LocalPresetRetarget => _localPresetRetarget;
        public bool IsLocalPresetRuntimeAvailable =>
            IsLocalPresetPlatformSupported(UnityEngine.Application.platform);
        public string LocalPresetAvailabilityMessage => IsLocalPresetRuntimeAvailable ? null :
            "Local Meta Avatar preset loading is unavailable on this platform; " +
            "the transform retarget remains usable without an avatar entity.";

        // Configuration is intentionally staged before activation.  This prevents the
        // SDK's normal Awake implementation from creating a native entity before the
        // explicit, non-Fusion input provenance has been validated.
        protected override bool CreateEntityOnAwake => !_localPresetMode;

        protected override void Awake() {
            // Before the SDK legs behaviour's Start subscribes to the load events: a missing legs rig is one error, not an
            // exception inside OnUserAvatarLoaded that leaves the legs frozen (SdkLegsRig).
            SdkLegsRig.GuardBehaviour(GetComponent<Oculus.Avatar2.Experimental.OvrAvatarAnimationBehavior>());
            // The installed Avatar SDK has no macOS native library.  A staged local
            // entity must retain its retarget on that host without entering even the
            // base AvatarSDK Awake lifecycle.
            if (_localPresetMode && !IsLocalPresetPlatformSupported(UnityEngine.Application.platform))
            {
                _retargetTransform = GetComponent<SbRetargetTransform>();
                return;
            }
            base.Awake();
            _retargetTransform = GetComponent<SbRetargetTransform>();
        }

        protected override void OnSkeletonLoaded()
        {
            base.OnSkeletonLoaded();
            if (_localPresetMode && _localPresetThirdPerson && IsCreated) SetActiveView(CAPI.ovrAvatar2EntityViewFlags.ThirdPerson);
            OnSkeletonLoadedCallback?.Invoke();
        }

        /// <summary>
        /// Configures this inactive original SbAvatarPrefab clone to consume one
        /// explicitly supplied transform input manager.  It never discovers a global
        /// manager, a Fusion behaviour, or a CDN identity.
        /// </summary>
        public bool TryConfigureLocalPreset(SbTransformInputManager input,
                                            SbRetargetTransform retarget,
                                            int presetIndex,
                                            out string error)
        {
            error = null;
            if (gameObject.activeInHierarchy || IsCreated)
            {
                error = "Local preset configuration must happen on an inactive, uncreated avatar clone.";
                return false;
            }
            if (_localPresetMode &&
                (!ReferenceEquals(_localPresetInput, input) ||
                 !ReferenceEquals(_localPresetRetarget, retarget)))
            {
                error = "This avatar already owns a different explicit local input/retarget binding.";
                return false;
            }
            if (!ValidateLocalPresetConfiguration(input, retarget, presetIndex, false, out error))
            {
                return false;
            }

            _retargetTransform = retarget;
            _localPresetMode = true;
            _localPresetInput = input;
            _localPresetRetarget = retarget;
            _localPresetIndex = presetIndex;
            input.SbTransform = retarget;
            _localPresetStatus = IsLocalPresetRuntimeAvailable
                ? LocalPresetStatus.Configured : LocalPresetStatus.Unavailable;
            _localPresetError = null;
            return true;
        }

        /// <summary>Releases an uncreated local binding so a staged clone may retry.</summary>
        public bool TryReleaseLocalPresetConfiguration(out string error)
        {
            error = null;
            if (!_localPresetMode)
            {
                return true;
            }
            if (gameObject.activeInHierarchy || IsCreated)
            {
                error = "Local preset configuration can be released only while the avatar is inactive and uncreated.";
                return false;
            }
            if (_localPresetInput != null &&
                !_localPresetInput.TryDetachArticulatedBodyTracking(out error))
            {
                // Do not turn this clone back into a legacy binding while a listener
                // may still have the manager-owned context installed.
                return false;
            }
            if (_localPresetInput != null &&
                ReferenceEquals(_localPresetInput.SbTransform, _localPresetRetarget))
            {
                _localPresetInput.SbTransform = null;
            }
            _localPresetMode = false;
            _localPresetInput = null;
            _localPresetRetarget = null;
            _localPresetStatus = LocalPresetStatus.Legacy;
            _localPresetError = null;
            return true;
        }

        public static bool IsLocalPresetPlatformSupported(RuntimePlatform platform)
        {
            return platform == RuntimePlatform.WindowsEditor ||
                   platform == RuntimePlatform.WindowsPlayer ||
                   platform == RuntimePlatform.Android;
        }

        [System.Serializable]
        protected struct AssetData
        {
            public AssetSource source;
            public string path;

            public AssetData(AssetSource source, string path)
            {
                this.source = source;
                this.path = path;
            }
        }
        [Header("Network ID")]
        [SerializeField]
        public uint _networkId = 0;

        [Header("Sb Avatar Entity")]
        [Tooltip("Attempt to load the Avatar model file from the Content Delivery Network (CDN) based on a userID, as opposed to loading from disk.")]
        [SerializeField]
        private bool _loadUserFromCdn = true;

        [Tooltip("If set to 0, attempt to load the currently logged in user avatar. Can also be set to an oculus user ID. Fallback to preset (on disk) avatar if fetch fails.")]
        [SerializeField]
        public UInt64 _cdnUserID = 0;

        [HideInInspector]
        [SerializeField]
    #pragma warning disable CS0414 // disable "not used" warning
        private int _style2DropdownIndex = 0;
    #pragma warning restore CS0414

        [Tooltip("Make initial requests for avatar and then defer loading until other avatars can make their requests.")]
        [SerializeField]
        private bool _deferLoading = false;

        [Header("Assets")]
        [Tooltip("Asset paths to load, and whether each asset comes from a preloaded zip file or directly from StreamingAssets. See Preset Asset settings on OvrAvatarManager for how this maps to the real file name.")]
        [SerializeField]
        protected List<AssetData> _assets = new List<AssetData>();

        [Tooltip("Adds an underscore between the path and the postfix.")]
        [SerializeField]
        private bool _underscorePostfix = true;

        [Tooltip("Filename Postfix (WARNING: Typically the postfix is Platform specific, such as \"_rift.glb\")")]
        [SerializeField]
        private string _overridePostfix = String.Empty;

        [Header("CDN")]
        [Tooltip("Automatically retry LoadUser download request on failure")]
        [SerializeField]
        protected bool _autoCdnRetry = true;

        [Tooltip("During the initial load, the user may be valid but have no avatar. If checked, do a background poll and load the avatar if the user creates one, replacing the fallback avatar.")]
        [SerializeField]
        protected bool _enablePollingForAvatarCreation = true;

        [Tooltip("How frequently to check for avatar changes")]
        [SerializeField]
        [Range(4.0f, 320.0f)]
        private float _changeCheckInterval = 8.0f;


        protected bool HasLocalAvatarConfigured => _assets.Count > 0;

        private Stopwatch _loadTime = new Stopwatch();

        private bool _isPollingAvatarChanges = false;

        private bool _stopCurrentAvatarChangePoll = false;

        private bool _isPollingAvatarCreation = false;

        private bool _stopCurrentAvatarCreationPoll = false;

        private OvrAvatarInputManagerBehavior m_bodyTracking;

        public OvrAvatarInputManagerBehavior BodyTracking
        {
            get => m_bodyTracking;
            protected set
            {
                m_bodyTracking = value;
                SetInputManager(value);
            }
        }

        /// <summary>
        /// The input manager of one networked remote avatar: its own, never a scene-wide one. A manager holds one
        /// retarget transform and one Fusion behaviour, so two remote avatars sharing a manager would drive each
        /// other's bodies. It is found in the avatar's own hierarchy, and created on the avatar when it has none.
        /// </summary>
        internal static SbTransformInputManager ResolveOwnInputManager(GameObject avatar)
        {
            var own = avatar.GetComponentInChildren<SbTransformInputManager>(true);
            return own != null ? own : avatar.AddComponent<SbTransformInputManager>();
        }

        protected virtual IEnumerator Start()
        {
            if (_localPresetMode)
            {
                yield return StartExplicitLocalPreset();
                yield break;
            }

            // If we have not passed in a local glb path, set the assets loaded to be the default path
            if (_assets.Count == 0)
            {
                OvrAvatarLog.LogWarning("No local assets have been specified. Setting fallback avatar to default preset.");
                SetDefaultAssets();
            }

            var body = ResolveOwnInputManager(gameObject);
            if (body != null)
            {
                BodyTracking = body;
            }
            else
            {
                OvrAvatarLog.LogError("No SbTransform Input Manager found. Cannot set up body tracking.", logScope, this);
            }

            var behaviourFusion = FindObjectsByType<SbAvatarBehaviourFusion>(FindObjectsSortMode.None);
            foreach (var bf in behaviourFusion)
            {
                if (bf.Object.Id.Raw == _networkId)
                {
                    ((SbTransformInputManager)body).SbAvatarBehaviourFusion = bf;
                    _behaviourFusion = bf;
                    break;
                }
            }
            if (((SbTransformInputManager)body).SbAvatarBehaviourFusion == null)
            {
                OvrAvatarLog.LogError("SbAvatarBehaviourFusion is not set for remote avatar: " + name, logScope, this);
            }

            if (_retargetTransform != null)
            {
                ((SbTransformInputManager)body).SbTransform = _retargetTransform;
            }
            else
            {
                OvrAvatarLog.LogError("SbRetargetTransform is not set for remote avatar: " + name, logScope, this);
            }

            if (!_deferLoading)
            {
                if (AvatarLoadDecision.SbAvatarLoadsPreset(_behaviourFusion != null,
                        _behaviourFusion != null && _behaviourFusion.UsePresetAvatar))
                {
                    LoadPreset(_behaviourFusion.LocalAvatarIndex);
                }
                else if (_loadUserFromCdn)
                {
                    _userId = _cdnUserID;
                    yield return LoadCdnAvatar();
                }
                else
                {
                    LoadLocalAvatar();
                }
            }
        }

        private IEnumerator StartExplicitLocalPreset()
        {
            if (!ValidateLocalPresetConfiguration(_localPresetInput, _localPresetRetarget,
                                                  _localPresetIndex, true,
                                                  out string validationError))
            {
                SetLocalPresetFailure(validationError);
                yield break;
            }
            if (!IsLocalPresetPlatformSupported(UnityEngine.Application.platform))
            {
                _localPresetStatus = LocalPresetStatus.Unavailable;
                yield break;
            }

            // The local path deliberately opts out of microphone/lip-sync attachment.
            // These setters update the SDK's serialized provider fields before
            // CreateEntity calls InitializeTrackingProviders.
            DisableLocalLipSync();
            BodyTracking = _localPresetInput;
            SetFacePoseProvider(null);
            SetEyePoseProvider(null);
            SetLipSync(null);
            CreateEntity();
            if (!IsCreated)
            {
                SetLocalPresetFailure("The Meta Avatar SDK did not create the local preset entity.");
                yield break;
            }
            if (!LoadPreset(_localPresetIndex))
            {
                SetLocalPresetFailure("The Meta Avatar SDK rejected the requested local preset asset.");
                yield break;
            }
            if (_localPresetThirdPerson) SetActiveView(CAPI.ovrAvatar2EntityViewFlags.ThirdPerson);
            _localPresetStatus = LocalPresetStatus.Initialized;
            _localPresetError = null;
            yield break;
        }

        private void DisableLocalLipSync()
        {
            foreach (OvrAvatarLipSyncContext lipSync in
                     GetComponentsInChildren<OvrAvatarLipSyncContext>(true))
            {
                lipSync.SetAudioSourceType(LipSyncAudioSourceType.None);
            }
        }

        private void SetLocalPresetFailure(string error)
        {
            _localPresetStatus = LocalPresetStatus.Failed;
            _localPresetError = error;
        }

        private bool ValidateLocalPresetConfiguration(SbTransformInputManager input,
                                                      SbRetargetTransform retarget,
                                                      int presetIndex,
                                                      bool requireCurrentBinding,
                                                      out string error)
        {
            error = null;
            if (input == null || retarget == null)
            {
                error = "Local preset mode requires explicit input-manager and retarget references.";
                return false;
            }
            if (input.gameObject != gameObject || retarget.gameObject != gameObject)
            {
                error = "The explicit input manager and retarget must belong to this avatar instance.";
                return false;
            }
            if (!IsOwnTrackingTransform(retarget.headTransform) ||
                !IsOwnTrackingTransform(retarget.leftHandTransform) ||
                !IsOwnTrackingTransform(retarget.rightHandTransform))
            {
                error = "The local avatar retarget requires head, left-hand, and right-hand transforms under this avatar instance.";
                return false;
            }
            if (presetIndex < 0)
            {
                error = "Local preset index must be non-negative.";
                return false;
            }
            if (GetComponent<SbAvatarBehaviourFusion>() != null ||
                input.SbAvatarBehaviourFusion != null)
            {
                error = "A local preset avatar cannot use Fusion avatar ownership.";
                return false;
            }
            if (input.SbTransform != null && !ReferenceEquals(input.SbTransform, retarget))
            {
                error = "The supplied input manager is already bound to another avatar retarget.";
                return false;
            }
            if (requireCurrentBinding &&
                (!ReferenceEquals(_retargetTransform, retarget) ||
                 !ReferenceEquals(input.SbTransform, retarget)))
            {
                error = "The explicit local input/retarget binding is no longer valid.";
                return false;
            }
            return true;
        }

        private bool IsOwnTrackingTransform(Transform trackingTransform)
        {
            return trackingTransform != null &&
                   (trackingTransform == transform || trackingTransform.IsChildOf(transform));
        }

        protected virtual void SetDefaultAssets()
        {
            // TODO: T195612718 Update to Style 2 default path after full deprecation for Style 1
            _assets = new List<AssetData> { new(source: AssetSource.Zip, path: "0") }; // Style 1 preset path
        }

        public void MakeVisibleAvatar(bool isVisible)
        {
            MeshRenderer[] meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer renderer in meshRenderers)
            {
                renderer.enabled = isVisible;
            }
        }

        #region Loading
        private IEnumerator LoadCdnAvatar()
        {
            // Ensure OvrPlatform is Initialized
            if (OvrPlatformInit.status == OvrPlatformInitStatus.NotStarted)
            {
                OvrPlatformInit.InitializeOvrPlatform();
            }

            while (OvrPlatformInit.status != OvrPlatformInitStatus.Succeeded)
            {
                if (OvrPlatformInit.status == OvrPlatformInitStatus.Failed)
                {
                    OvrAvatarLog.LogError($"Error initializing OvrPlatform. Falling back to local avatar", logScope);
                    LoadLocalAvatar();
                    yield break;
                }

                yield return null;
            }

            // user ID == 0 means we want to load logged in user avatar from CDN
            if (_userId == 0)
            {
                // Get User ID
                bool getUserIdComplete = false;
                Users.GetLoggedInUser().OnComplete(message =>
                {
                    if (!message.IsError)
                    {
                        _userId = message.Data.ID;
                    }
                    else
                    {
                        var e = message.GetError();
                        OvrAvatarLog.LogError($"Error loading CDN avatar: {e.Message}. Falling back to local avatar", logScope);
                    }

                    getUserIdComplete = true;
                });

                while (!getUserIdComplete) { yield return null; }
            }
            yield return LoadUserAvatar();
        }

        public void LoadRemoteUserCdnAvatar(ulong userId)
        {
            StartLoadTimeCounter();
            _userId = userId;
            StartCoroutine(LoadCdnAvatar());
        }

        private IEnumerator LoadUserAvatar()
        {
            if (_userId == 0)
            {
                LoadLocalAvatar();
                yield break;
            }

            yield return Retry_HasAvatarRequest();
        }

        private bool IsFromZip(AssetData asset)
        {
            return asset.source == AssetSource.Zip;
        }

        protected virtual void LoadLocalAvatar()
        {
            if (!HasLocalAvatarConfigured)
            {
                OvrAvatarLog.LogInfo("No local avatar asset configured", logScope, this);
                return;
            }

            // Zip asset paths are relative to the inside of the zip.
            // Zips can be loaded from the OvrAvatarManager at startup or by calling OvrAvatarManager.Instance.AddZipSource
            // Assets can also be loaded individually from Streaming assets
            foreach (var asset in _assets)
            {
                string assetPostfix = GetAssetPostfix(IsFromZip(asset));

                var assetPath = $"{asset.path}{assetPostfix}";
                LoadAssets(new[] { assetPath }, asset.source);
            }
        }

        public void ReloadAvatarManually(string newAssetPaths, AssetSource newAssetSource)
        {
            ReloadAvatarManually(new[] { newAssetPaths }, newAssetSource);
        }

        private string GetAssetPostfix(bool isFromZip)
        {
            string assetPostfix = (_underscorePostfix ? "_" : "")
                                  + OvrAvatarManager.Instance.GetPlatformGLBPostfix(_creationInfo.renderFilters.quality, isFromZip)
                                  + OvrAvatarManager.Instance.GetPlatformGLBVersion(_creationInfo.renderFilters.quality, isFromZip)
                                  + OvrAvatarManager.Instance.GetPlatformGLBExtension(isFromZip);
            if (!String.IsNullOrEmpty(_overridePostfix))
            {
                assetPostfix = _overridePostfix;
            }

            return assetPostfix;
        }

        public void ReloadAvatarManually(string[] newAssetPaths, AssetSource newAssetSource)
        {
            Teardown();
            CreateEntity();

            bool isFromZip = (newAssetSource == AssetSource.Zip);
            string assetPostfix = GetAssetPostfix(isFromZip);

            string[] combinedPaths = new string[newAssetPaths.Length];
            for (var index = 0; index < newAssetPaths.Length; index++)
            {
                combinedPaths[index] = $"{newAssetPaths[index]}{assetPostfix}";
            }

            LoadAssets(combinedPaths, newAssetSource);
        }

        public bool LoadPreset(int preset, string namePrefix = "")
        {
            StartLoadTimeCounter();
            const bool isFromZip = true;
            string assetPostfix = GetAssetPostfix(isFromZip);

            var assetPath = $"{namePrefix}{preset}{assetPostfix}";
            return LoadAssets(new[] { assetPath }, AssetSource.Zip);
        }
        #endregion // Loading

        #region Retry
        protected void UserHasNoAvatarFallback()
        {
            OvrAvatarLog.LogError(
                $"Unable to find user avatar with userId {_userId}. Falling back to local avatar.", logScope, this);

            LoadLocalAvatar();
        }

        protected virtual IEnumerator Retry_HasAvatarRequest()
        {
            const float HAS_AVATAR_RETRY_WAIT_TIME = 4.0f;
            const int HAS_AVATAR_RETRY_ATTEMPTS = 12;

            int totalAttempts = _autoCdnRetry ? HAS_AVATAR_RETRY_ATTEMPTS : 1;
            bool continueRetries = _autoCdnRetry;
            int retriesRemaining = totalAttempts;
            bool hasFoundAvatar = false;
            bool requestComplete = false;
            bool shouldPollForCustomAvatar = false;
            do
            {
                var hasAvatarRequest = OvrAvatarManager.Instance.UserHasAvatarAsync(_userId);
                while (!hasAvatarRequest.IsCompleted) { yield return null; }

                switch (hasAvatarRequest.Result)
                {
                    case OvrAvatarManager.HasAvatarRequestResultCode.HasAvatar:
                        hasFoundAvatar = true;
                        requestComplete = true;
                        continueRetries = false;

                        // Now attempt download
                        yield return AutoRetry_LoadUser(true);
                        // End coroutine - do not load default
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.HasNoAvatar:
                        requestComplete = true;
                        continueRetries = false;
                        shouldPollForCustomAvatar = true;
                        OvrAvatarLog.LogDebug("User has no avatar. Falling back to local avatar.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.SendFailed:
                        OvrAvatarLog.LogError("Unable to send avatar status request.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.RequestFailed:
                        OvrAvatarLog.LogError("An error occurred while querying avatar status.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.BadParameter:
                        continueRetries = false;
                        OvrAvatarLog.LogError("Attempted to load invalid userId.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.RequestCancelled:
                        continueRetries = false;
                        OvrAvatarLog.LogInfo("HasAvatar request cancelled.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.UnknownError:
                    default:
                        OvrAvatarLog.LogError(
                            $"An unknown error occurred {hasAvatarRequest.Result}. Falling back to local avatar."
                            , logScope, this);
                        break;
                }

                continueRetries &= --retriesRemaining > 0;
                if (continueRetries)
                {
                    yield return new WaitForSecondsRealtime(HAS_AVATAR_RETRY_WAIT_TIME);
                }
            } while (continueRetries);

            if (!requestComplete)
            {
                OvrAvatarLog.LogError($"Unable to query UserHasAvatar {totalAttempts} attempts", logScope, this);
            }

            if (!hasFoundAvatar)
            {
                // We cannot find an avatar, use local fallback
                UserHasNoAvatarFallback();

                if (shouldPollForCustomAvatar && _enablePollingForAvatarCreation)
                {
                    yield return PollForAvatarCreation();
                }
            }

            // Check for changes unless a local asset is configured, user could create one later
            // If a local asset is loaded, it will currently conflict w/ the CDN asset
            if (hasFoundAvatar || !HasLocalAvatarConfigured)
            {
                yield return PollForAvatarChange();
            }
        }

        protected virtual IEnumerator AutoRetry_LoadUser(bool loadFallbackOnFailure)
        {
            const float LOAD_USER_POLLING_INTERVAL = 4.0f;
            const float LOAD_USER_BACKOFF_FACTOR = 1.618033988f;
            const int CDN_RETRY_ATTEMPTS = 13;

            int totalAttempts = _autoCdnRetry ? CDN_RETRY_ATTEMPTS : 1;
            int remainingAttempts = totalAttempts;
            bool didLoadAvatar = false;
            var currentPollingInterval = LOAD_USER_POLLING_INTERVAL;
            do
            {
                // Initiate user spec load (ie: CDN Avatar)
                LoadUser();

                CAPI.ovrAvatar2Result status;
                do
                {
                    // Wait for retry interval before taking any action
                    yield return new WaitForSecondsRealtime(currentPollingInterval);

                    // Check current `entity` status
                    status = this.entityStatus;
                    if (status.IsSuccess() || HasNonDefaultAvatar)
                    {
                        didLoadAvatar = true;
                        // Finished downloading - no more retries
                        remainingAttempts = 0;

                        OvrAvatarLog.LogVerbose(
                            "Load user retry check found successful download, ending retry routine"
                            , logScope, this);
                        break;
                    }

                    // Increase backoff interval
                    currentPollingInterval *= LOAD_USER_BACKOFF_FACTOR;

                    // `while` status is still pending, keep polling the current attempt
                    // Do not start a new request - do not decrement retry attempts
                } while (status == CAPI.ovrAvatar2Result.Pending);
                // Decrement retry attempts now that load failure has been confirmed (status != Pending)
            } while (--remainingAttempts > 0);

            if (loadFallbackOnFailure && !didLoadAvatar)
            {
                OvrAvatarLog.LogError(
                    $"Unable to download user after {totalAttempts} retry attempts",
                    logScope, this);

                // We cannot download an avatar, use local fallback (ie: Preset Avatar)
                UserHasNoAvatarFallback();
            }
        }

        private void StartLoadTimeCounter()
        {
            _loadTime.Start();

            OnUserAvatarLoadedEvent.AddListener((OvrAvatarEntity entity) =>
            {
                _loadTime.Stop();
            });
        }

        public long GetLoadTimeMs()
        {
            return _loadTime.ElapsedMilliseconds;
        }

        #endregion // Retry

        #region Avatar Creation Polling
        protected virtual IEnumerator PollForAvatarCreation()
        {
            var waitForPollInterval = new WaitForSecondsRealtime(_changeCheckInterval);

            _isPollingAvatarCreation = true;
            bool continuePolling = _enablePollingForAvatarCreation;
            do
            {
                if (_stopCurrentAvatarCreationPoll)
                {
                    _stopCurrentAvatarCreationPoll = false;
                    _isPollingAvatarCreation = false;
                    break;
                }
                var hasAvatarRequest = OvrAvatarManager.Instance.UserHasAvatarAsync(_userId);
                while (!hasAvatarRequest.IsCompleted) { yield return null; }

                switch (hasAvatarRequest.Result)
                {
                    case OvrAvatarManager.HasAvatarRequestResultCode.HasAvatar:
                        continuePolling = false;

                        // Reload the avatar
                        ApplyConfig(GetAvatarConfig(), true);
                        yield break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.HasNoAvatar:
                        OvrAvatarLog.LogDebug("User has no avatar.  Continue polling in background.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.SendFailed:
                        OvrAvatarLog.LogError("Unable to send avatar status request.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.RequestFailed:
                        OvrAvatarLog.LogError("An error occurred while querying avatar status.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.BadParameter:
                        continuePolling = false;
                        OvrAvatarLog.LogError("Attempted to load invalid userId.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.RequestCancelled:
                        continuePolling = false;
                        OvrAvatarLog.LogInfo("HasAvatar request cancelled.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarRequestResultCode.UnknownError:
                    default:
                        OvrAvatarLog.LogError(
                            $"An unknown error occurred {hasAvatarRequest.Result}. Falling back to local avatar."
                            , logScope, this);
                        break;
                }

                if (continuePolling)
                {
                    yield return waitForPollInterval;
                }
            } while (continuePolling);
            _isPollingAvatarCreation = false;
        }
        #endregion // Avatar Creation Polling

        #region Change Check

        protected IEnumerator PollForAvatarChange()
        {
            var waitForPollInterval = new WaitForSecondsRealtime(_changeCheckInterval);

            _isPollingAvatarChanges = true;
            bool continueChecking = true;
            do
            {
                if (_stopCurrentAvatarChangePoll)
                {
                    _stopCurrentAvatarChangePoll = false;
                    _isPollingAvatarChanges = false;
                    break;
                }
                var checkTask = HasAvatarChangedAsync();
                while (!checkTask.IsCompleted) { yield return null; }

                switch (checkTask.Result)
                {
                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.UnknownError:
                        OvrAvatarLog.LogError("Check avatar changed unknown error, aborting.", logScope, this);
                        continueChecking = false; // Stop retrying or we'll just spam this error
                        break;

                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.BadParameter:
                        OvrAvatarLog.LogError("Check avatar changed invalid parameter, aborting.", logScope, this);
                        continueChecking = false; // Stop retrying or we'll just spam this error
                        break;

                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.SendFailed:
                        OvrAvatarLog.LogWarning("Check avatar changed send failed.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.RequestFailed:
                        OvrAvatarLog.LogError("Check avatar changed request failed.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.RequestCancelled:
                        OvrAvatarLog.LogInfo("Check avatar changed request cancelled.", logScope, this);
                        continueChecking = false; // Stop retrying, this entity has likely been destroyed
                        break;

                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.AvatarHasNotChanged:
                        OvrAvatarLog.LogVerbose("Avatar has not changed.", logScope, this);
                        break;

                    case OvrAvatarManager.HasAvatarChangedRequestResultCode.AvatarHasChanged:
                        OvrAvatarLog.LogInfo("Avatar has changed, loading new spec.", logScope, this);
                        yield return AutoRetry_LoadUser(false); // Load new avatar!
                        break;
                }

                if (continueChecking)
                {
                    yield return waitForPollInterval;
                }

            } while (continueChecking);
            _isPollingAvatarChanges = false;
        }

        #endregion // Change Check

        #region SDK UI

        public SampleAvatarConfig GetAvatarConfig()
        {
            var assetDataList = new List<SampleAvatarConfig.AssetData>();
            foreach (var asset in _assets)
            {
                assetDataList.Add(new SampleAvatarConfig.AssetData { source = asset.source, path = asset.path });
            }

            SampleAvatarConfig outConfig = new SampleAvatarConfig
            {
                CreationInfo = new CAPI.ovrAvatar2EntityCreateInfo()
                {
                    features = _creationInfo.features,
                    renderFilters = _creationInfo.renderFilters,
                    lodFlags = _creationInfo.lodFlags,
                },
                ActiveView = GetActiveView(),
                ActiveManifestation = GetActiveManifestation(),
                LoadUserFromCdn = _loadUserFromCdn,
                Assets = assetDataList,
            };

            return outConfig;
        }

        public void ApplyConfig(SampleAvatarConfig config, bool requiresTeardown = false)
        {
            _loadUserFromCdn = config.LoadUserFromCdn;

            SetActiveView(config.ActiveView);
            SetActiveManifestation(config.ActiveManifestation);

            if (!requiresTeardown)
            {
                OvrAvatarLog.LogInfo($"Applied the following settings to Avatar \"{name}\" :\n{config}", logScope);
                return;
            }

            _creationInfo = new CAPI.ovrAvatar2EntityCreateInfo()
            {
                features = config.CreationInfo.features,
                renderFilters = config.CreationInfo.renderFilters,
                lodFlags = config.CreationInfo.lodFlags,
            };

            if (config.Assets != null)
            {
                _assets.Clear();
                foreach (var asset in config.Assets)
                {
                    _assets.Add(new AssetData { source = asset.source, path = asset.path });
                }
            }

            if (_isPollingAvatarChanges)
            {
                _stopCurrentAvatarChangePoll = true;
            }
            if (_isPollingAvatarCreation)
            {
                _stopCurrentAvatarCreationPoll = true;
            }
            Teardown();
            CreateEntity();


            if (config.LoadUserFromCdn)
            {
                StartCoroutine(LoadCdnAvatar());
            }
            else
            {
                LoadLocalAvatar();
            }

            OvrAvatarLog.LogInfo($"Applied the following settings to Avatar \"{name}\" :\n{config}", logScope);
        }

        #endregion // SDK UI
    }
}
