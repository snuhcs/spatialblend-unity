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

using UnityEngine;

using Fusion;
using Oculus.Avatar2;

using Meta.XR.MultiplayerBlocks.Shared;
using Meta.XR.MultiplayerBlocks.Fusion;
using System.Collections;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The class responsible for spawning an Avatar when using Photon Fusion.
    /// The spawning may happen on <c>Awake()</c> if <see cref="loadAvatarWhenConnected"/> is set to <c>true</c> or at any
    /// time chosen by the developer by calling <see cref="SpawnAvatar"/>.
    /// For more information on the Meta Avatars, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    public class SbAvatarSpawnerFusion : MonoBehaviour
    {
        [SerializeField]
        private SbApp sbApp;
#pragma warning disable CS0414 // If Avatar SDK not installed these fields are not used, disable warning but retain serialization
        [Tooltip("Control when you want to load the avatar.")]
        [SerializeField]
        private bool loadAvatarWhenConnected = true;
        [SerializeField] private GameObject avatar;
        [Tooltip("Specify the number of available avatars. This value depends on the SDK version.")]
        // Developers might want to delete some avatars from the sample asset zip
        // e.g. the game has a maximum player count, they won't need more unique sample avatars
        [SerializeField] private int preloadedSampleAvatarSize = 6;

        [Tooltip("Adjust the level of detail used when streaming the avatars.")]
        [SerializeField] private AvatarStreamLOD avatarStreamLOD = AvatarStreamLOD.Medium;

        [Tooltip("Adjust the update interval used when streaming the avatars.")]
        [SerializeField] private float avatarUpdateIntervalInSec = 0.08f;

        [SerializeField] private GameObject voice;
#pragma warning restore CS0414

        private NetworkRunner _networkRunner;
        private bool _sceneLoaded;
        private bool _spaceCreated;

        private void OnValidate()
        {
            const int maxAllowedValue = 17;
            preloadedSampleAvatarSize = Mathf.Clamp(preloadedSampleAvatarSize, 1, maxAllowedValue);
        }

        private void HandleAvatarSpawned(IAvatarStreamConfig streamConfig)
        {
            streamConfig.SetAvatarStreamLOD(avatarStreamLOD);
            streamConfig.SetAvatarUpdateIntervalInS(avatarUpdateIntervalInSec);
        }

        private void Awake()
        {
            if (loadAvatarWhenConnected)
            {
                SpawnAvatar();
            }
        }

        private void OnEnable()
        {
            FusionBBEvents.OnSceneLoadDone += OnLoaded;
            MetaAvatarEntity.OnSpawned += HandleAvatarSpawned;
            SpatialBlendManager.OnSpaceCreated += OnSpaceCreated;
        }

        private void OnDisable()
        {
            FusionBBEvents.OnSceneLoadDone -= OnLoaded;
            MetaAvatarEntity.OnSpawned -= HandleAvatarSpawned;
            SpatialBlendManager.OnSpaceCreated -= OnSpaceCreated;
        }

        private void OnLoaded(NetworkRunner networkRunner)
        {
            _sceneLoaded = true;
            _networkRunner = networkRunner;
        }

        private void OnSpaceCreated(SpatialBlendManager spaceManager)
        {
            _spaceCreated = true;
        }

        // ReSharper disable once MemberCanBePrivate.Global
        /// <summary>
        /// Spawns the Avatar.
        /// </summary>
        /// <remarks>This is called automatically on <c>Awake()</c> if <see cref="loadAvatarWhenConnected"/> is set to <c>true</c>.</remarks>
        public void SpawnAvatar()
        {
            StartCoroutine(SpawnAvatarRoutine());
        }

        private IEnumerator SpawnAvatarRoutine()
        {
            while (_networkRunner == null || !_sceneLoaded || !_spaceCreated || AvatarRolePending())
            {
                yield return null;
            }
            if (!AvatarSpawnAllowed())
            {
                yield break;
            }

            // Spawn Avatar
            var localAvatar = _networkRunner.Spawn(
                avatar,
                Vector3.zero,
                Quaternion.identity,
                _networkRunner.LocalPlayer,
                (_, obj) => // onBeforeSpawned
                {
                    var avatarBehaviourFusion = obj.GetComponent<SbAvatarBehaviourFusion>();
                    // v1: always the preset avatar, the panel's selected preset.
                    var panel = FindFirstObjectByType<SpatialBlend.Samples.FreeformCollaboration.UI.ControlPanel>(
                        FindObjectsInactive.Include);
                    AvatarLoadDecision.SpawnChoice choice = AvatarLoadDecision.ChooseForSpawn(
                        panel != null, panel != null ? panel.SelectedAvatarPreset : 0,
                        preloadedSampleAvatarSize, Random.Range);
                    avatarBehaviourFusion.UsePresetAvatar = choice.UsePresetAvatar;
                    avatarBehaviourFusion.LocalAvatarIndex = choice.LocalAvatarIndex;
                    if (obj.HasStateAuthority)
                    {
                        if (sbApp is FreeformCollaborationApp freeformApp)
                        {
                            freeformApp.SetUserAvatar(obj.gameObject);
                        }
                    }
                }
            );

            var localVoice = _networkRunner.Spawn(
                voice,
                Vector3.zero,
                Quaternion.identity,
                _networkRunner.LocalPlayer);
            localVoice.transform.SetParent(localAvatar.transform);
        }

        private FreeformCollaborationApp FreeformApp()
        {
            if (sbApp is FreeformCollaborationApp wired) return wired;
            return FindFirstObjectByType<FreeformCollaborationApp>();
        }

        private bool AvatarRolePending()
        {
            FreeformCollaborationApp app = FreeformApp();
            return app != null && !app.ObserverRoleResolved;
        }

        private bool AvatarSpawnAllowed()
        {
            FreeformCollaborationApp app = FreeformApp();
            return app == null || !app.IsDesktopObserver;
        }
    }
}
