// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Avatar
{
    /// <summary>
    /// Keeps the local microphone out of the headset speakers. The scene's <c>LipSyncInput</c> object plays the Unity microphone
    /// clip through an AudioSource at full volume so that <c>OvrAvatarLipSyncContext.OnAudioFilterRead</c> can read it for the local
    /// avatar's mouth. The context silences that audio itself (<c>CaptureAudio</c>), but only while it is enabled, active and the
    /// Avatars SDK is initialized; whenever it is not (a preset avatar disabled it, the SDK is still starting) the user hears their
    /// own voice with a delay. This component sits after the lip-sync context on the same object, so the context reads the samples
    /// first, and then clears whatever is left. The remote voice path (Photon Speaker) is a different AudioSource and is not touched.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public class LocalMicMonitorSilencer : MonoBehaviour
    {
        /// <summary>Zeroes the block. The lip-sync context has already read it when this runs after it.</summary>
        public static void Silence(float[] data)
        {
            if (data != null) System.Array.Clear(data, 0, data.Length);
        }

        private void OnAudioFilterRead(float[] data, int channels) => Silence(data);
    }
}
