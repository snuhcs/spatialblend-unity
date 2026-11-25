// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using Fusion;
using Photon.Voice.Unity;
using System.Collections.Generic;
using SpatialBlend.Samples;
using Oculus.Avatar2;

namespace SpatialBlend.Avatar
{
    /// <summary>
    /// Manages the matching of remote avatars with their corresponding voice speakers.
    /// It listens for events and registers spawned objects independently to handle timing issues
    /// between different network and application-level callbacks.
    /// </summary>
    public class RemoteAvatarVoiceManager : MonoBehaviour
    {
        // Singleton instance for easy access from other scripts.
        public static RemoteAvatarVoiceManager Instance { get; private set; }

        // Dictionaries to hold objects that have spawned but not yet found their counterpart.
        // The PlayerRef of the owner is used as the key.
        private readonly Dictionary<PlayerRef, GameObject> _unmatchedAvatars = new Dictionary<PlayerRef, GameObject>();
        private readonly Dictionary<PlayerRef, LipSyncSpeaker> _unmatchedSpeakers = new Dictionary<PlayerRef, LipSyncSpeaker>();

        private void Awake()
        {
            // Setup Singleton pattern
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Subscribe to the static event that is fired when a remote avatar is added by the application logic.
            FreeformCollaborationApp.OnRemoteAvatarAdded += HandleRemoteAvatarAdded;
        }

        private void Start()
        {
            // A build with no Voice App ID looks like it has voice (mic opens, avatar lip-syncs) but sends and receives nothing.
            VoiceConfiguration.WarnIfMissing();
        }

        private void OnDestroy()
        {
            // Unsubscribe from the event when this manager is destroyed to prevent memory leaks.
            FreeformCollaborationApp.OnRemoteAvatarAdded -= HandleRemoteAvatarAdded;
        }

        /// <summary>
        /// This method is called when the OnRemoteAvatarAdded event is invoked.
        /// </summary>
        private void HandleRemoteAvatarAdded(GameObject avatar, PlayerRef authority)
        {
            // Check if a speaker for this avatar's owner is already waiting.
            if (_unmatchedSpeakers.TryGetValue(authority, out LipSyncSpeaker speaker))
            {
                // Match found, parent the speaker to the avatar and remove both from the waiting lists.
                MatchAvatarAndSpeaker(avatar, speaker);
                _unmatchedSpeakers.Remove(authority);
            }
            else
            {
                // No speaker found yet, add the avatar to the waiting list.
                _unmatchedAvatars[authority] = avatar;
            }
        }

        /// <summary>
        /// Called by VoiceObjectController when a remote speaker is spawned.
        /// </summary>
        public void RegisterRemoteSpeaker(LipSyncSpeaker speaker, PlayerRef authority)
        {
            // Check if an avatar for this speaker's owner is already waiting.
            if (_unmatchedAvatars.TryGetValue(authority, out GameObject avatar))
            {
                // Match found, parent the speaker to the avatar and remove both from the waiting lists.
                MatchAvatarAndSpeaker(avatar, speaker);
                _unmatchedAvatars.Remove(authority);
            }
            else
            {
                // No avatar found yet, add the speaker to the waiting list.
                _unmatchedSpeakers[authority] = speaker;
            }
        }

        /// <summary>
        /// Parents the speaker to the avatar's headTransform.
        /// </summary>
        private void MatchAvatarAndSpeaker(GameObject avatar, LipSyncSpeaker speaker)
        {
            speaker.SetLipSyncContext(avatar.GetComponent<MetaAvatarEntity>()._sbAvatar.GetComponent<OvrAvatarLipSyncContext>());

            // Find the headTransform within the avatar's hierarchy.
            Transform headTransform = avatar.GetComponent<MetaAvatarEntity>()._sbAvatar._retargetTransform.headTransform;
            if (headTransform != null)
            {
                speaker.transform.SetParent(headTransform);
                speaker.transform.localPosition = Vector3.zero;
                speaker.transform.localRotation = Quaternion.identity;
            }
            else
            {
                // Fallback: if no headTransform is found, parent to the avatar's root and set a default height.
                Debug.LogWarning($"headTransform not found on avatar for player {avatar.GetComponent<NetworkObject>().StateAuthority}. Parenting to root as a fallback.");
                speaker.transform.SetParent(avatar.transform);
                speaker.transform.localPosition = new Vector3(0, 1.6f, 0); // Approximate head height
            }
        }
    }
}
