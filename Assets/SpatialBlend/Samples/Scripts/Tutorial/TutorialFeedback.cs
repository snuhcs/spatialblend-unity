// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// The success feedback of a verified step, generated in code so no extra asset ships: a filled circle with a check mark cut out
    /// of it (tinted by the Image that shows it), and a short two-note chime.
    /// </summary>
    public static class TutorialFeedback
    {
        public const int SpriteSize = 64;
        public const int ChimeSampleRate = 44100;
        public const float ChimeSeconds = 0.28f;

        private static Sprite _check;
        private static Texture2D _checkTexture;

        /// <summary>The check-mark sprite: opaque circle, transparent check.</summary>
        public static Sprite CheckSprite
        {
            get
            {
                if (_check != null) return _check;
                _checkTexture = BuildCheckTexture();
                _check = Sprite.Create(_checkTexture, new Rect(0, 0, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f), 100f);
                _check.name = "TutorialCheck";
                return _check;
            }
        }

        /// <summary>The coverage (0..1) of the circle with the check cut out, at a texel centre. Pure, so the shape is tested.</summary>
        public static float Coverage(float x, float y)
        {
            float half = SpriteSize * 0.5f;
            float dc = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half));
            float circle = Mathf.Clamp01(half - 2f - dc + 0.5f);
            // The check: two segments, in texel space (y up).
            var a = new Vector2(18f, 33f); var b = new Vector2(28f, 22f); var c = new Vector2(47f, 43f);
            float d = Mathf.Min(DistanceToSegment(new Vector2(x, y), a, b), DistanceToSegment(new Vector2(x, y), b, c));
            float stroke = Mathf.Clamp01(3.2f - d + 0.5f);
            return circle * (1f - stroke);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        private static Texture2D BuildCheckTexture()
        {
            var texture = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false) { name = "TutorialCheckTexture", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[SpriteSize * SpriteSize];
            for (int y = 0; y < SpriteSize; y++)
                for (int x = 0; x < SpriteSize; x++)
                {
                    byte alpha = (byte)Mathf.RoundToInt(Coverage(x + 0.5f, y + 0.5f) * 255f);
                    pixels[y * SpriteSize + x] = new Color32(255, 255, 255, alpha);
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>A two-note rising chime (880 Hz then 1318 Hz) with a fast attack and a decaying tail, mono.</summary>
        public static AudioClip BuildChime()
        {
            int count = Mathf.RoundToInt(ChimeSampleRate * ChimeSeconds);
            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)ChimeSampleRate;
                float freq = t < ChimeSeconds * 0.4f ? 880f : 1318.5f;
                float local = t < ChimeSeconds * 0.4f ? t : t - ChimeSeconds * 0.4f;
                float attack = Mathf.Clamp01(local / 0.01f);
                float decay = Mathf.Exp(-local * 12f);
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * attack * decay * 0.35f;
            }
            var clip = AudioClip.Create("TutorialChime", count, 1, ChimeSampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
