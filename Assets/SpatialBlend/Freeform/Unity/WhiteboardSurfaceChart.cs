// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Freeform
{
    /// <summary>
    /// Immutable, versioned board chart in the local XY plane of its explicitly
    /// supplied surface transform. UV (0,0) is the lower-left corner and UV
    /// (1,1) is the upper-right corner when viewed along local +Z.
    /// </summary>
    public readonly struct WhiteboardSurfaceChart
    {
        public const int CurrentVersion = 1;

        private readonly int _version;
        private readonly Vector2 _lowerLeft;
        private readonly Vector2 _upperRight;

        public int Version => _version;
        public Vector2 LowerLeft => _lowerLeft;
        public Vector2 UpperRight => _upperRight;
        public Vector3 LocalNormal => Vector3.forward;

        /// <summary>The historic local XY unit quad used by legacy whiteboards.</summary>
        public static WhiteboardSurfaceChart CenteredUnitQuad => new WhiteboardSurfaceChart(
            CurrentVersion, new Vector2(-.5f, -.5f), new Vector2(.5f, .5f));

        private WhiteboardSurfaceChart(int version, Vector2 lowerLeft, Vector2 upperRight)
        {
            _version = version;
            _lowerLeft = lowerLeft;
            _upperRight = upperRight;
        }

        public static bool TryCreate(int version, Vector2 lowerLeft, Vector2 upperRight,
                                     out WhiteboardSurfaceChart chart, out string error)
        {
            chart = default;
            if (version != CurrentVersion)
            {
                error = "Whiteboard surface chart version is not supported.";
                return false;
            }
            if (!IsFinite(lowerLeft) || !IsFinite(upperRight) ||
                !(lowerLeft.x < upperRight.x) || !(lowerLeft.y < upperRight.y))
            {
                error = "Whiteboard surface chart bounds must be finite, ordered, and nondegenerate.";
                return false;
            }
            chart = new WhiteboardSurfaceChart(version, lowerLeft, upperRight);
            error = null;
            return true;
        }

        /// <summary>Returns corners ordered counter-clockwise when viewed from local +Z.</summary>
        public Vector3[] GetLocalCorners() => new[]
        {
            new Vector3(_lowerLeft.x, _lowerLeft.y, 0f),
            new Vector3(_upperRight.x, _lowerLeft.y, 0f),
            new Vector3(_upperRight.x, _upperRight.y, 0f),
            new Vector3(_lowerLeft.x, _upperRight.y, 0f)
        };

        public bool TryCreateFrame(Transform planeTransform, out WhiteboardSurfaceFrame frame,
                                   out string error)
        {
            frame = default;
            if (!TryCreate(_version, _lowerLeft, _upperRight, out _, out error))
                return false;
            if (planeTransform == null || !IsFiniteInvertible(planeTransform.localToWorldMatrix))
            {
                error = "Whiteboard surface plane requires a finite, invertible affine transform.";
                return false;
            }
            frame = new WhiteboardSurfaceFrame(planeTransform, this);
            error = null;
            return true;
        }

        internal static bool IsFiniteInvertible(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; ++index)
                if (!IsFinite(matrix[index]))
                    return false;
            return IsFinite(matrix.determinant) && Mathf.Abs(matrix.determinant) > 1e-8f;
        }

        private static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// A chart coupled to one renderer-plane transform. The transform is retained
    /// rather than copied so drawing and native support refresh track the same
    /// moved or non-uniformly scaled authored surface.
    /// </summary>
    public readonly struct WhiteboardSurfaceFrame
    {
        private readonly Transform _planeTransform;
        private readonly WhiteboardSurfaceChart _chart;

        internal WhiteboardSurfaceFrame(Transform planeTransform, WhiteboardSurfaceChart chart)
        {
            _planeTransform = planeTransform;
            _chart = chart;
        }

        public Transform PlaneTransform => _planeTransform;
        public WhiteboardSurfaceChart Chart => _chart;

        public bool IsValid => _planeTransform != null &&
                               WhiteboardSurfaceChart.TryCreate(_chart.Version,
                                   _chart.LowerLeft, _chart.UpperRight, out _, out _) &&
                               WhiteboardSurfaceChart.IsFiniteInvertible(
                                   _planeTransform.localToWorldMatrix);

        public bool TryWorldToUv(Vector3 worldPosition, out Vector2 uv)
        {
            uv = default;
            if (!IsValid || !IsFinite(worldPosition))
                return false;
            Vector3 local = _planeTransform.worldToLocalMatrix.MultiplyPoint3x4(worldPosition);
            float width = _chart.UpperRight.x - _chart.LowerLeft.x;
            float height = _chart.UpperRight.y - _chart.LowerLeft.y;
            uv = new Vector2((local.x - _chart.LowerLeft.x) / width,
                             (local.y - _chart.LowerLeft.y) / height);
            return IsFinite(uv);
        }

        public Vector2 WorldToUv(Vector3 worldPosition)
        {
            return TryWorldToUv(worldPosition, out Vector2 uv) ? uv : new Vector2(float.NaN, float.NaN);
        }

        public Vector3[] GetWorldCorners()
        {
            if (!IsValid)
                return Array.Empty<Vector3>();
            Vector3[] local = _chart.GetLocalCorners();
            Matrix4x4 matrix = _planeTransform.localToWorldMatrix;
            for (int index = 0; index < local.Length; ++index)
                local[index] = matrix.MultiplyPoint3x4(local[index]);
            return local;
        }

        private static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);
        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
