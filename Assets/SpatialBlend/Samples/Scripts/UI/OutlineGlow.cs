// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples
{
    [DisallowMultipleComponent]
    public class OutlineGlow : MonoBehaviour
    {
        [SerializeField]
        private Color outlineColor = new Color(0.0f, 0.8f, 1.0f, 1.0f);

        [SerializeField, Range(0f, 0.2f)]
        private float thickness = 0.05f;

        [SerializeField, Range(0f, 1f)]
        private float alpha = 1f;

        [SerializeField]
        private bool applyOnStart = true;

        private Material outlineMaterial;

        // Pulse animation controls
        [SerializeField]
        private bool animate = true;

        [SerializeField]
        private float pulseSpeed = 2.0f;

        [SerializeField, Range(0f, 1f)]
        private float minAlpha = 0.35f;

        [SerializeField, Range(0f, 1f)]
        private float maxAlpha = 1.0f;

        [SerializeField, Range(0f, 0.2f)]
        private float minThickness = 0.04f;

        [SerializeField, Range(0f, 0.2f)]
        private float maxThickness = 0.07f;

        private float phaseOffset;

        private struct TrackedRenderer
        {
            public Renderer renderer;
            public Material[] originalMaterials;
            public bool added;
        }

        private bool _applied;

        private System.Collections.Generic.List<TrackedRenderer> trackedRenderers = new System.Collections.Generic.List<TrackedRenderer>();

        private static readonly int ThicknessId = Shader.PropertyToID("_Thickness");
        private static readonly int ColorId = Shader.PropertyToID("_OutlineColor");
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");

        private void Awake()
        {
            Shader shader = Shader.Find("Sb/OutlineGlow");
            if (shader == null)
            {
                Debug.LogError("OutlineGlow shader not found: Sb/OutlineGlow");
                enabled = false;
                return;
            }
            outlineMaterial = new Material(shader);
            // Different instances get different phase to avoid perfect sync
            phaseOffset = (transform.GetInstanceID() % 1000) * 0.001f * Mathf.PI * 2f;
            SyncMaterialProps();
        }

        private void Start()
        {
            if (applyOnStart)
            {
                Apply();
            }
        }

        private void OnDestroy()
        {
            Remove();
            if (outlineMaterial != null)
            {
                Destroy(outlineMaterial);
                outlineMaterial = null;
            }
        }

        private void Update()
        {
            if (outlineMaterial == null)
            {
                return;
            }

            if (animate)
            {
                float t = 0.5f + 0.5f * Mathf.Sin((Time.time + phaseOffset) * pulseSpeed);
                float animatedAlpha = Mathf.Lerp(minAlpha, maxAlpha, t);
                float animatedThickness = Mathf.Lerp(minThickness, maxThickness, t);
                outlineMaterial.SetFloat(AlphaId, animatedAlpha);
                outlineMaterial.SetFloat(ThicknessId, animatedThickness);
            }
        }

        public void SetColor(Color color)
        {
            outlineColor = color;
            SyncMaterialProps();
        }

        public void SetThickness(float worldUnits)
        {
            thickness = Mathf.Clamp(worldUnits, 0f, 0.1f);
            SyncMaterialProps();
        }

        public void SetAlpha(float value)
        {
            alpha = Mathf.Clamp01(value);
            SyncMaterialProps();
        }

        public Color GetColor()
        {
            return outlineColor;
        }

        public void EnablePulse(bool enable)
        {
            animate = enable;
            if (!animate)
            {
                // Ensure material reflects static values when pulse is disabled
                SyncMaterialProps();
            }
        }

        public void SetPulseParams(float speed, float minAlphaValue, float maxAlphaValue, float minThicknessValue, float maxThicknessValue)
        {
            pulseSpeed = Mathf.Max(0f, speed);
            minAlpha = Mathf.Clamp01(minAlphaValue);
            maxAlpha = Mathf.Clamp01(Mathf.Max(minAlpha, maxAlphaValue));
            minThickness = Mathf.Max(0f, minThicknessValue);
            maxThickness = Mathf.Max(minThickness, maxThicknessValue);
        }

        public void Apply()
        {
            if (outlineMaterial == null) return;
            SyncMaterialProps();
            if (_applied) return;

            var renderers = GetComponentsInChildren<Renderer>(includeInactive: false);
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer || r is LineRenderer) continue;
                var original = r.sharedMaterials;
                bool present = false;
                for (int i = 0; i < original.Length; ++i)
                    if (original[i] == outlineMaterial) { present = true; break; }
                trackedRenderers.Add(new TrackedRenderer { renderer = r, originalMaterials = original, added = !present });
                if (present) continue;
                var newMats = new Material[original.Length + 1];
                for (int i = 0; i < original.Length; i++) newMats[i] = original[i];
                newMats[newMats.Length - 1] = outlineMaterial;
                r.materials = newMats;
            }
            _applied = true;
        }

        public void Remove()
        {
            for (int i = 0; i < trackedRenderers.Count; i++)
            {
                var entry = trackedRenderers[i];
                if (entry.renderer == null || !entry.added) continue;
                entry.renderer.materials = entry.originalMaterials;
            }
            trackedRenderers.Clear();
            _applied = false;
        }

        private void SyncMaterialProps()
        {
            if (outlineMaterial == null)
            {
                return;
            }
            outlineMaterial.SetColor(ColorId, outlineColor);
            outlineMaterial.SetFloat(ThicknessId, thickness);
            outlineMaterial.SetFloat(AlphaId, alpha);
        }
    }
}


