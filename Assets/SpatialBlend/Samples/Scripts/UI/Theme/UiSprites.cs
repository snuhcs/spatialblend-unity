// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Rounded-rectangle sprites generated from a signed-distance function, so no image asset is needed and the corners
    /// stay crisp at any panel size. Each sprite is 9-sliced and baked at <see cref="Supersample"/> texels per canvas
    /// unit. The result is cached per radius.
    /// </summary>
    public static class UiSprites
    {
        public const int Supersample = 4;
        public const float ReferencePpu = 100f;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        public static Sprite RoundedRect(float radius) => Get("fill" + radius, radius, 0f, Mode.Fill, 0f);

        public static Sprite Ring(float radius, float thickness) => Get("ring" + radius + "_" + thickness, radius, 0f, Mode.Ring, thickness);

        /// <summary>A soft shadow: the shape plus a blurred margin of <paramref name="blur"/> units on every side.</summary>
        public static Sprite Shadow(float radius, float blur) => Get("shadow" + radius + "_" + blur, radius, blur, Mode.Shadow, 0f);

        /// <summary>Applies a generated sprite as a 9-sliced Image at canvas-unit radius.</summary>
        public static void Apply(Image image, Sprite sprite)
        {
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.fillCenter = true;
            var canvas = image.canvas;
            image.pixelsPerUnitMultiplier = canvas != null ? canvas.referencePixelsPerUnit / ReferencePpu : 1f;
        }

        public enum Mode { Fill, Ring, Shadow }

        /// <summary>Sprite size in texels and 9-slice border (texels) for a shape; shared by the runtime and asset bakers.</summary>
        public static int Size(float radius, float margin) => (Mathf.CeilToInt((radius + margin) * 2f) + 2) * Supersample;

        public static float Border(float radius, float margin) => (radius + margin) * Supersample;

        /// <summary>The texels of a rounded-rectangle sprite (white, alpha = coverage), row 0 at the bottom.</summary>
        public static Color32[] Bake(float radius, float margin, Mode mode, float thickness)
        {
            int n = Size(radius, margin);
            var pixels = new Color32[n * n];
            float half = n / (2f * Supersample);
            float extent = half - margin;           // half size of the shape itself, in units
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float px = ((x + 0.5f) / Supersample) - half;
                    float py = ((y + 0.5f) / Supersample) - half;
                    // signed distance to a rounded rect of half-extent `extent` and corner radius `radius`
                    float qx = Mathf.Abs(px) - (extent - radius);
                    float qy = Mathf.Abs(py) - (extent - radius);
                    float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float a;
                    switch (mode)
                    {
                        case Mode.Ring:
                            a = Mathf.Clamp01(0.5f - d * Supersample) * Mathf.Clamp01(0.5f + (d + thickness) * Supersample);
                            break;
                        case Mode.Shadow:
                            a = 1f - Smooth(-0.3f * margin, 0.7f * margin, d);
                            break;
                        default:
                            a = Mathf.Clamp01(0.5f - d * Supersample);
                            break;
                    }
                    pixels[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            return pixels;
        }

        private static Sprite Get(string key, float radius, float margin, Mode mode, float thickness)
        {
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;
            int n = Size(radius, margin);
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "UiSprite_" + key,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(Bake(radius, margin, mode, thickness));
            texture.Apply(false, true);
            float border = Border(radius, margin);
            var sprite = Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f),
                Supersample * ReferencePpu, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Cache[key] = sprite;
            return sprite;
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
