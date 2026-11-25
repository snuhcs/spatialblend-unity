// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using SpatialBlend.Freeform;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration
{
    /// <summary>Serialized, instance-resolving bindings for the authored whiteboard prefab.</summary>
    [DisallowMultipleComponent]
    public sealed class FreeformSceneWhiteboardDefinition : MonoBehaviour
    {
        [SerializeField] private Transform _movableRoot;
        [SerializeField] private Renderer _surfaceRenderer;
        [SerializeField] private PointerHandlerMotif _pointer;
        [SerializeField] private InteractionStateManagerMotif _interactionState;
        [SerializeField, Min(1)] private int _textureWidth = 1024;
        [SerializeField, Min(1)] private int _textureHeight = 1024;
        [SerializeField] private Color _initialColor = Color.white;
        [Header("Explicit Surface Chart")]
        [SerializeField] private int _surfaceChartVersion = WhiteboardSurfaceChart.CurrentVersion;
        [SerializeField] private Vector2 _surfaceChartLowerLeft = new Vector2(-.5f, -.5f);
        [SerializeField] private Vector2 _surfaceChartUpperRight = new Vector2(.5f, .5f);
        [SerializeField] private ColorSelectorMotif[] _palette = Array.Empty<ColorSelectorMotif>();
        [SerializeField] private Behaviour[] _legacyContentBehaviours = Array.Empty<Behaviour>();

        public Transform MovableRoot => _movableRoot;
        public Renderer SurfaceRenderer => _surfaceRenderer;
        public PointerHandlerMotif Pointer => _pointer;
        public InteractionStateManagerMotif InteractionState => _interactionState;
        public int TextureWidth => _textureWidth;
        public int TextureHeight => _textureHeight;
        public Color InitialColor => _initialColor;
        public int SurfaceChartVersion => HasUnserializedLegacyChart
            ? WhiteboardSurfaceChart.CurrentVersion : _surfaceChartVersion;
        public Vector2 SurfaceChartLowerLeft => HasUnserializedLegacyChart
            ? WhiteboardSurfaceChart.CenteredUnitQuad.LowerLeft : _surfaceChartLowerLeft;
        public Vector2 SurfaceChartUpperRight => HasUnserializedLegacyChart
            ? WhiteboardSurfaceChart.CenteredUnitQuad.UpperRight : _surfaceChartUpperRight;
        public ColorSelectorMotif[] Palette => _palette;
        public Behaviour[] LegacyContentBehaviours => _legacyContentBehaviours;

        /// <summary>
        /// Resolves the explicit, versioned local XY chart against the authored
        /// renderer plane. It intentionally never infers bounds from a mesh,
        /// renderer bounds, or movable root.
        /// </summary>
        public bool TryCreateSurfaceFrame(out WhiteboardSurfaceFrame frame, out string error)
        {
            frame = default;
            if (_surfaceRenderer == null)
            {
                error = "A surface renderer is required for the whiteboard chart.";
                return false;
            }
            if (!WhiteboardSurfaceChart.TryCreate(SurfaceChartVersion, SurfaceChartLowerLeft,
                    SurfaceChartUpperRight, out WhiteboardSurfaceChart chart, out error))
                return false;
            return chart.TryCreateFrame(_surfaceRenderer.transform, out frame, out error);
        }

        // Existing prefabs predate these serialized fields. Their absent all-zero
        // payload is deliberately the known centred-unit-quad profile, not an
        // inferred mesh chart. Any other invalid authored payload still rejects.
        private bool HasUnserializedLegacyChart => _surfaceChartVersion == 0 &&
            _surfaceChartLowerLeft == Vector2.zero && _surfaceChartUpperRight == Vector2.zero;
    }
}
