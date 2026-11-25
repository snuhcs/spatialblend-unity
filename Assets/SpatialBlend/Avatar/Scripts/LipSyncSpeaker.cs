// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using Oculus.Avatar2;
using Photon.Voice.Unity;
using Photon.Voice;

namespace SpatialBlend.Avatar
{
    [RequireComponent(typeof(AudioSource))]
    public class LipSyncSpeaker : Speaker
    {
        [SerializeField]
        [Tooltip("Lip sync context component for processing audio samples")]
        private OvrAvatarLipSyncContext lipSyncContext;
        
        private const int bitsPerSample = 32;
        private LipSyncAudioOutWrapper audioOutWrapper;

        protected override IAudioOut<float> CreateAudioOut()
        {
            // Create base audio output
            var baseAudioOut = base.CreateAudioOut();
            
            // Always create wrapper, context can be set later
            audioOutWrapper = new LipSyncAudioOutWrapper(baseAudioOut, lipSyncContext, this.Logger);
            return audioOutWrapper;
        }

        public void SetLipSyncContext(OvrAvatarLipSyncContext context)
        {
            lipSyncContext = context;
            
            // Update wrapper's context if it exists
            if (audioOutWrapper != null)
            {
                audioOutWrapper.SetLipSyncContext(context);
            }
        }
    }

    /// <summary>
    /// Wrapper class that forwards audio data to lip sync while wrapping IAudioOut
    /// </summary>
    internal class LipSyncAudioOutWrapper : IAudioOut<float>
    {
        private const int bitsPerSample = 32;
        private readonly IAudioOut<float> wrappedOutput;
        private OvrAvatarLipSyncContext lipSyncContext;
        private readonly Photon.Voice.ILogger logger;
        private int channels = 0;

        public LipSyncAudioOutWrapper(
            IAudioOut<float> wrappedOutput, 
            OvrAvatarLipSyncContext lipSyncContext,
            Photon.Voice.ILogger logger)
        {
            this.wrappedOutput = wrappedOutput;
            this.lipSyncContext = lipSyncContext;
            this.logger = logger;
        }

        public bool IsPlaying => wrappedOutput.IsPlaying;

        public int Lag => wrappedOutput.Lag;

        public void Start(int frequency, int channels, int frameSamples)
        {
            this.channels = channels;
            wrappedOutput.Start(frequency, channels, frameSamples);
        }

        public void Push(float[] frame)
        {
            if (lipSyncContext != null && frame != null && frame.Length > 0)
            {
                try
                {
                    lipSyncContext.ProcessAudioSamples(frame, channels);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Error processing lip sync: {e.Message}");
                }
            }

            // Forward to original audio output
            wrappedOutput.Push(frame);
        }

        public void Flush()
        {
            wrappedOutput.Flush();
        }

        public void Stop()
        {
            wrappedOutput.Stop();
        }

        public void Service()
        {
            wrappedOutput.Service();
        }

        public void SetLipSyncContext(OvrAvatarLipSyncContext context)
        {
            lipSyncContext = context;
        }
    }
}