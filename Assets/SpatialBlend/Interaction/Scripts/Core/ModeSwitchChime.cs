// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// The mode-change chime of <see cref="ModeSwitchAssist"/>, synthesized so it needs no audio asset: two short sine tones, 45 ms each,
    /// rising (660 Hz then 990 Hz) into Editing and falling (990 Hz then 660 Hz) into Interaction, with 5 ms fades so neither end clicks.
    /// </summary>
    public static class ModeSwitchChime
    {
        public const int SampleRate = 44100;
        public const float ToneSeconds = 0.045f;
        public const float LowHz = 660f;
        public const float HighHz = 990f;
        public const float Amplitude = 0.5f;
        private const float FadeSeconds = 0.005f;

        /// <summary>The chime's mono samples at <paramref name="sampleRate"/>.</summary>
        public static float[] Samples(bool rising, int sampleRate)
        {
            int perTone = Mathf.Max(1, Mathf.RoundToInt(ToneSeconds * sampleRate));
            int fade = Mathf.Max(1, Mathf.RoundToInt(FadeSeconds * sampleRate));
            var samples = new float[perTone * 2];
            for (int tone = 0; tone < 2; tone++)
            {
                float hz = (tone == 0) == rising ? LowHz : HighHz;
                for (int i = 0; i < perTone; i++)
                {
                    float envelope = Mathf.Min(1f, Mathf.Min(i, perTone - 1 - i) / (float)fade);
                    samples[tone * perTone + i] = Amplitude * envelope * Mathf.Sin(2f * Mathf.PI * hz * i / sampleRate);
                }
            }
            return samples;
        }
    }
}
