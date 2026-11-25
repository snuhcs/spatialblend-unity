// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    /// <summary>
    /// Renders one local whiteboard model at this transform. Many views may bind the
    /// same model; each creates its own Texture2D and maps world hits through its own
    /// transform, so moving one view does not affect another.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WhiteboardContentView : MonoBehaviour
    {
        [SerializeField] private Renderer _renderer;

        private WhiteboardContentModel _model;
        private Texture2D _texture;
        private MaterialPropertyBlock _originalPropertyBlock;
        private MaterialPropertyBlock _propertyBlock;
        private bool _hasTextureOverride;
        private Renderer _leasedRenderer;
        private WhiteboardSurfaceFrame? _surfaceFrame;
        private static readonly int MainTexture = Shader.PropertyToID("_MainTex");
        private static readonly Dictionary<Renderer, WhiteboardContentView> RendererLeases =
            new Dictionary<Renderer, WhiteboardContentView>();

        public WhiteboardContentModel Model => _model;
        public Texture2D Texture => _texture;

        public bool Configure(Renderer renderer, out string error)
        {
            // Existing callers retain the historic view-transform centred-unit-quad
            // projection. New spatial bindings must opt in to a surface frame.
            return ConfigureLegacy(renderer, out error);
        }

        /// <summary>
        /// Configures an explicit renderer-plane chart. Unlike the legacy overload,
        /// world drawing is mapped through the renderer's actual plane transform.
        /// </summary>
        public bool Configure(Renderer renderer, WhiteboardSurfaceChart chart, out string error)
        {
            if (renderer == null)
            {
                error = "A renderer is required for a whiteboard view.";
                return false;
            }
            if (!chart.TryCreateFrame(renderer.transform, out WhiteboardSurfaceFrame frame, out error))
                return false;
            return Configure(renderer, frame, out error);
        }

        /// <summary>Configures a previously validated frame for this exact renderer plane.</summary>
        public bool Configure(Renderer renderer, WhiteboardSurfaceFrame frame, out string error)
        {
            if (renderer == null || !frame.IsValid || frame.PlaneTransform != renderer.transform)
            {
                error = "A valid frame from the configured renderer plane is required.";
                return false;
            }
            if (!ConfigureRenderer(renderer, out error))
                return false;
            _surfaceFrame = frame;
            return true;
        }

        private bool ConfigureLegacy(Renderer renderer, out string error)
        {
            if (!ConfigureRenderer(renderer, out error))
                return false;
            _surfaceFrame = null;
            return true;
        }

        private bool ConfigureRenderer(Renderer renderer, out string error)
        {
            if (renderer == null)
            {
                error = "A renderer is required for a whiteboard view.";
                return false;
            }
            if (_model != null && renderer != _renderer)
            {
                error = "Unbind before changing a live whiteboard renderer.";
                return false;
            }
            _renderer = renderer;
            error = null;
            return true;
        }

        public bool TryBind(WhiteboardContentModel model, out string error)
        {
            Renderer renderer = _renderer;
            if (model == null || !model.IsLive || renderer == null)
            {
                error = "A live whiteboard model and configured renderer are required.";
                return false;
            }
            if (_model == model)
            {
                error = null;
                return true;
            }
            if (!RendererLeaseAvailable(renderer))
            {
                error = "The configured renderer is already bound by another whiteboard view.";
                return false;
            }

            // Allocate and render the candidate first: a bad bind never displaces a
            // valid existing model/view relationship.
            Texture2D candidate = new Texture2D(model.Width, model.Height, TextureFormat.RGBA32, false);
            candidate.SetPixels32(model.CopyPixels());
            candidate.Apply(false, false);
            if (!TryAcquireRendererLease(renderer))
            {
                DestroyTexture(candidate);
                error = "The configured renderer is already bound by another whiteboard view.";
                return false;
            }
            WhiteboardContentModel previousModel = _model;
            Texture2D previousTexture = _texture;
            if (previousModel == null)
            {
                // Keep the authored override intact in a separate block. Mutating
                // the block used for rendering would otherwise make an absent
                // _MainTex indistinguishable from an explicit null texture.
                _originalPropertyBlock ??= new MaterialPropertyBlock();
                renderer.GetPropertyBlock(_originalPropertyBlock);
                _propertyBlock ??= new MaterialPropertyBlock();
                renderer.GetPropertyBlock(_propertyBlock);
                _hasTextureOverride = true;
            }
            if (previousModel != null)
            {
                previousModel.Changed -= OnModelChanged;
                previousModel.Retired -= OnModelRetired;
            }
            _model = model;
            _texture = candidate;
            _leasedRenderer = renderer;
            _propertyBlock.SetTexture(MainTexture, candidate);
            renderer.SetPropertyBlock(_propertyBlock);
            _model.Changed += OnModelChanged;
            _model.Retired += OnModelRetired;
            if (previousTexture != null)
                DestroyTexture(previousTexture);
            error = null;
            return true;
        }

        public void Unbind()
        {
            WhiteboardContentModel model = _model;
            Texture2D texture = _texture;
            Renderer leasedRenderer = _leasedRenderer;
            bool restorePropertyBlock = _hasTextureOverride;
            _model = null;
            _texture = null;
            _hasTextureOverride = false;
            _leasedRenderer = null;
            if (model != null)
            {
                model.Changed -= OnModelChanged;
                model.Retired -= OnModelRetired;
            }
            try
            {
                if (leasedRenderer != null && restorePropertyBlock)
                    leasedRenderer.SetPropertyBlock(_originalPropertyBlock);
            }
            finally
            {
                ReleaseRendererLease(leasedRenderer);
                if (texture != null)
                    DestroyTexture(texture);
            }
        }

        /// <summary>Legacy-compatible local XY board projection.</summary>
        public Vector2 WorldToUv(Vector3 worldPosition)
        {
            if (_surfaceFrame.HasValue)
                return _surfaceFrame.Value.WorldToUv(worldPosition);
            Vector3 local = transform.InverseTransformPoint(worldPosition);
            return new Vector2(local.x + .5f, local.y + .5f);
        }

        public bool TryDrawWorldLine(Vector3 worldStart, Vector3 worldEnd, Color32 color,
                                     int brushRadius, out string error)
        {
            if (_model == null)
            {
                error = "Bind a whiteboard model before drawing.";
                return false;
            }
            if (!FreeformValidation.IsFinite(worldStart) || !FreeformValidation.IsFinite(worldEnd) ||
                !ValidProjectionTransform())
            {
                error = "Drawing requires finite world points and a finite, nonsingular projection transform.";
                return false;
            }
            return _model.DrawLine(WorldToUv(worldStart), WorldToUv(worldEnd), color,
                                   brushRadius, out error);
        }

        private bool ValidProjectionTransform() => _surfaceFrame.HasValue
            ? _surfaceFrame.Value.IsValid
            : FreeformValidation.ValidTransform(transform);

        private void OnDisable() => Unbind();
        private void OnDestroy() => Unbind();

        private void OnModelChanged(FreeformContentModel changed)
        {
            if (_model == null || changed != _model || _texture == null)
                return;
            _texture.SetPixels32(_model.CopyPixels());
            _texture.Apply(false, false);
        }

        private void OnModelRetired(FreeformContentModel retired)
        {
            if (retired == _model)
                Unbind();
        }

        private bool RendererLeaseAvailable(Renderer renderer)
        {
            if (!RendererLeases.TryGetValue(renderer, out WhiteboardContentView current))
                return true;
            if (ReferenceEquals(current, this))
                return true;
            if (current != null)
                return false;
            RendererLeases.Remove(renderer);
            return true;
        }

        private bool TryAcquireRendererLease(Renderer renderer)
        {
            if (!RendererLeaseAvailable(renderer))
                return false;
            RendererLeases[renderer] = this;
            return true;
        }

        private void ReleaseRendererLease(Renderer renderer)
        {
            if (ReferenceEquals(renderer, null))
                return;
            if (RendererLeases.TryGetValue(renderer, out WhiteboardContentView current) &&
                ReferenceEquals(current, this))
                RendererLeases.Remove(renderer);
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (Application.isPlaying)
                Destroy(texture);
            else
                DestroyImmediate(texture);
        }
    }
}
