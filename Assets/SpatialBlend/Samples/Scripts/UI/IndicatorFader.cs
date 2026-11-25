// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Fades renderer brightness to black, then destroys this GameObject.
    /// Uses MaterialPropertyBlock so shared materials are not modified.
    /// </summary>
    [DisallowMultipleComponent]
    public class IndicatorFader : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private sealed class MaterialSlotCache
        {
            public bool HasBaseColor;
            public bool HasColor;
            public bool HasEmissionColor;
            public Color BaseColor;
            public Color Color;
            public Color EmissionColor;
        }

        private sealed class RendererCache
        {
            public Renderer Renderer;
            public MaterialSlotCache[] Slots;
        }

        private readonly List<RendererCache> _rendererCaches = new();
        private readonly List<CanvasGroup> _canvasGroups = new();
        private MaterialPropertyBlock _propertyBlock;

        private Coroutine _fadeCoroutine;
        private bool _isCached;

        private void Awake()
        {
            // Unity objects should be created in Unity lifecycle methods, not field initializers.
            _propertyBlock = new MaterialPropertyBlock();
        }

        public void StartFadeOut(float durationSeconds)
        {
            if (_fadeCoroutine != null)
            {
                return;
            }

            EnsureCached();
            _fadeCoroutine = StartCoroutine(FadeOutAndDestroy(durationSeconds));
        }

        private IEnumerator FadeOutAndDestroy(float durationSeconds)
        {
            if (durationSeconds <= 0f)
            {
                ApplyBrightness(0f);
                Destroy(gameObject);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < durationSeconds)
            {
                float t = Mathf.Clamp01(elapsed / durationSeconds);
                ApplyBrightness(1f - t);
                elapsed += Time.deltaTime;
                yield return null;
            }
            ApplyBrightness(0f);
            Destroy(gameObject);
        }

        private void EnsureCached()
        {
            if (_propertyBlock == null)
            {
                _propertyBlock = new MaterialPropertyBlock();
            }

            if (_isCached)
            {
                return;
            }

            _rendererCaches.Clear();
            _canvasGroups.Clear();

            var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                var sharedMaterials = renderer.sharedMaterials;
                if (sharedMaterials == null || sharedMaterials.Length == 0)
                {
                    continue;
                }

                var slotCaches = new MaterialSlotCache[sharedMaterials.Length];
                for (int slot = 0; slot < sharedMaterials.Length; slot++)
                {
                    var material = sharedMaterials[slot];
                    if (material == null)
                    {
                        slotCaches[slot] = new MaterialSlotCache();
                        continue;
                    }

                    var cache = new MaterialSlotCache();
                    cache.HasBaseColor = material.HasProperty(BaseColorId);
                    cache.HasColor = material.HasProperty(ColorId);
                    cache.HasEmissionColor = material.HasProperty(EmissionColorId);

                    if (cache.HasBaseColor)
                    {
                        cache.BaseColor = material.GetColor(BaseColorId);
                    }
                    if (cache.HasColor)
                    {
                        cache.Color = material.GetColor(ColorId);
                    }
                    if (cache.HasEmissionColor)
                    {
                        cache.EmissionColor = material.GetColor(EmissionColorId);
                    }

                    slotCaches[slot] = cache;
                }

                _rendererCaches.Add(new RendererCache
                {
                    Renderer = renderer,
                    Slots = slotCaches
                });
            }

            _canvasGroups.AddRange(GetComponentsInChildren<CanvasGroup>(includeInactive: true));
            _isCached = true;
        }

        private void ApplyBrightness(float brightness)
        {
            if (_propertyBlock == null)
            {
                _propertyBlock = new MaterialPropertyBlock();
            }

            float clamped = Mathf.Clamp01(brightness);

            for (int i = 0; i < _rendererCaches.Count; i++)
            {
                var rendererCache = _rendererCaches[i];
                var renderer = rendererCache.Renderer;
                if (renderer == null || rendererCache.Slots == null)
                {
                    continue;
                }

                for (int slot = 0; slot < rendererCache.Slots.Length; slot++)
                {
                    var slotCache = rendererCache.Slots[slot];
                    renderer.GetPropertyBlock(_propertyBlock, slot);

                    if (slotCache.HasBaseColor)
                    {
                        _propertyBlock.SetColor(BaseColorId, slotCache.BaseColor * clamped);
                    }
                    if (slotCache.HasColor)
                    {
                        _propertyBlock.SetColor(ColorId, slotCache.Color * clamped);
                    }
                    if (slotCache.HasEmissionColor)
                    {
                        _propertyBlock.SetColor(EmissionColorId, slotCache.EmissionColor * clamped);
                    }

                    renderer.SetPropertyBlock(_propertyBlock, slot);
                    _propertyBlock.Clear();
                }
            }

            for (int i = 0; i < _canvasGroups.Count; i++)
            {
                var canvasGroup = _canvasGroups[i];
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = clamped;
                }
            }
        }
    }
}
