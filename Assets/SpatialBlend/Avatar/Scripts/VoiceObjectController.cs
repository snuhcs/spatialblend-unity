// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using Fusion;
using Photon.Voice.Unity;

namespace SpatialBlend.Avatar
{
    /// <summary>
    /// This component should be attached to the Voice prefab.
    /// Its purpose is to register the speaker with the RemoteAvatarVoiceManager when it's spawned on a remote client.
    /// </summary>
    [RequireComponent(typeof(LipSyncSpeaker))]
    public class VoiceObjectController : NetworkBehaviour
    {
        public override void Spawned()
        {
            if (!Object.HasInputAuthority)
            {
                var speaker = GetComponent<LipSyncSpeaker>();
                if (speaker != null)
                {
                    // Register this remote speaker with the central manager.
                    RemoteAvatarVoiceManager.Instance?.RegisterRemoteSpeaker(speaker, Object.StateAuthority);
                }
            }
        }
    }
}
