// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Geometry observed locally in a tracked room. This deliberately carries no
    /// semantic label, affordance, content, or sharing identity.
    /// </summary>
    public readonly struct SceneGeometryDescriptor
    {
        private const float MinimumExtent = 0.0001f;

        public int SourceKey { get; }
        public Matrix4x4 LocalToWorld { get; }
        public Bounds VolumeBounds { get; }
        public IReadOnlyList<Vector2> PlaneBoundary { get; }
        public bool HasVolume { get; }
        public bool HasPlane { get; }
        public bool PlaneBoundaryIsRectApproximation { get; }

        /// <summary>The plane's full 3D surface in world coordinates (see <see cref="SceneGeometryChart.PlaneSurface"/>); null without a plane.</summary>
        public Vector3[] BakedPlaneSurface => HasPlane ? SceneGeometryChart.PlaneSurface(LocalToWorld, PlaneBoundary) : null;

        private SceneGeometryDescriptor(int sourceKey, Matrix4x4 localToWorld,
                                        Bounds volumeBounds, bool hasVolume,
                                        Vector2[] planeBoundary, bool hasPlane,
                                        bool planeBoundaryIsRectApproximation)
        {
            SourceKey = sourceKey;
            LocalToWorld = localToWorld;
            VolumeBounds = volumeBounds;
            HasVolume = hasVolume;
            PlaneBoundary = planeBoundary == null ? null : Array.AsReadOnly(planeBoundary);
            HasPlane = hasPlane;
            PlaneBoundaryIsRectApproximation = planeBoundaryIsRectApproximation;
        }

        public static bool TryCreate(int sourceKey, Matrix4x4 localToWorld,
                                     Bounds? volumeBounds,
                                     IReadOnlyList<Vector2> planeBoundary,
                                     Rect? planeRect,
                                     out SceneGeometryDescriptor descriptor)
        {
            descriptor = default;
            if (sourceKey == 0 || !IsFinite(localToWorld) || !IsAffine(localToWorld) ||
                !IsNonSingular(localToWorld)) return false;
            if (volumeBounds.HasValue && !IsValid(volumeBounds.Value)) return false;
            if (planeRect.HasValue && !IsValid(planeRect.Value)) return false;

            bool hasVolume = volumeBounds.HasValue;
            bool hasMeasuredBoundary = planeBoundary != null && planeBoundary.Count > 0;
            Vector2[] boundary = CopyValidBoundary(planeBoundary);
            if (hasMeasuredBoundary && boundary == null) return false;
            bool rectApproximation = false;
            if (boundary == null && planeRect.HasValue)
            {
                boundary = RectBoundary(planeRect.Value);
                rectApproximation = true;
            }
            bool hasPlane = boundary != null;
            if (!hasVolume && !hasPlane ||
                !HasRepresentableWorldGeometry(localToWorld, volumeBounds, boundary)) return false;

            descriptor = new SceneGeometryDescriptor(sourceKey, localToWorld,
                volumeBounds.GetValueOrDefault(), hasVolume, boundary, hasPlane, rectApproximation);
            return true;
        }

        private static Vector2[] CopyValidBoundary(IReadOnlyList<Vector2> points)
        {
            if (points == null || points.Count < 3) return null;
            var copy = new Vector2[points.Count];
            double twiceArea = 0d;
            for (int i = 0; i < points.Count; ++i)
            {
                Vector2 point = points[i];
                Vector2 next = points[(i + 1) % points.Count];
                if (!IsFinite(point) || !IsFinite(next)) return null;
                copy[i] = point;
                twiceArea += (double)point.x * next.y - (double)next.x * point.y;
            }
            return !double.IsNaN(twiceArea) && !double.IsInfinity(twiceArea) &&
                   Math.Abs(twiceArea) > MinimumExtent ? copy : null;
        }

        private static Vector2[] RectBoundary(Rect rect) => new[]
        {
            new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
            new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax)
        };

        private static bool IsValid(Bounds bounds) => IsFinite(bounds.center) &&
            IsFinite(bounds.size) && IsFinite(bounds.min) && IsFinite(bounds.max) &&
            bounds.size.x > MinimumExtent && bounds.size.y > MinimumExtent &&
            bounds.size.z > MinimumExtent;

        private static bool HasRepresentableWorldGeometry(Matrix4x4 chart, Bounds? volume,
                                                           IReadOnlyList<Vector2> boundary)
        {
            if (volume.HasValue)
            {
                Vector3 min = volume.Value.min;
                Vector3 max = volume.Value.max;
                for (int x = 0; x < 2; ++x)
                for (int y = 0; y < 2; ++y)
                for (int z = 0; z < 2; ++z)
                    if (!IsFinite(chart.MultiplyPoint3x4(new Vector3(x == 0 ? min.x : max.x,
                        y == 0 ? min.y : max.y, z == 0 ? min.z : max.z)))) return false;
            }
            if (boundary != null)
                foreach (Vector2 point in boundary)
                    if (!IsFinite(chart.MultiplyPoint3x4(new Vector3(point.x, point.y, 0f)))) return false;
            return true;
        }

        private static bool IsValid(Rect rect) => IsFinite(rect.xMin) && IsFinite(rect.xMax) &&
            IsFinite(rect.yMin) && IsFinite(rect.yMax) && rect.width > MinimumExtent &&
            rect.height > MinimumExtent;
        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                    if (!IsFinite(matrix[row, column])) return false;
            return true;
        }
        private static bool IsNonSingular(Matrix4x4 matrix)
        {
            float determinant = matrix.determinant;
            return IsFinite(determinant) && Mathf.Abs(determinant) > MinimumExtent;
        }
        private static bool IsAffine(Matrix4x4 matrix)
        {
            const float epsilon = 0.00001f;
            return Mathf.Abs(matrix.m30) < epsilon && Mathf.Abs(matrix.m31) < epsilon &&
                   Mathf.Abs(matrix.m32) < epsilon && Mathf.Abs(matrix.m33 - 1f) < epsilon;
        }
        private static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);
        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// The one registration contract of observed scene geometry, shared by both adapters (the manager's and the local
    /// scope's): one floor contract. The core owns the ground image of an element-local polygon
    /// (<c>GroundFrame</c>, relation/support.h: the polygon's image on the floor, a segment when the polygon stands
    /// upright, which has no inside), so neither adapter flattens, drops a tilt or a scale, or re-frames to a floor: the
    /// element is registered at the app's own world transform and the outline is registered as the local polygon.
    ///
    /// The one convention is the plane's chart. An MRUK plane anchor has its normal along local Z and its measured outline
    /// in local XY, while the core's polygon plane is the Unity-local XZ plane (its x, y). So the element is registered at
    /// <c>localToWorld * Rx(+90)</c> and the outline as (x, -y) in reverse order (the same orientation): a pure change of
    /// basis that moves every point to exactly where the anchor put it. For a horizontal plane the registered transform
    /// is its position and yaw only; for a wall it is the wall's own transform, whose image on the floor is a segment.
    /// The full 3D surface (rendering, scan baking) is <see cref="PlaneSurface"/>, kept apart and never registered.
    /// </summary>
    public static class SceneGeometryChart
    {
        private static readonly Matrix4x4 ToElement = Matrix4x4.Rotate(Quaternion.Euler(90f, 0f, 0f));

        /// <summary>The transform to register the observed element at: the app's own, composed with the plane-chart basis change.</summary>
        public static Matrix4x4 ElementTransform(Matrix4x4 localToWorld) => localToWorld * ToElement;

        /// <summary>A volume's anchor-local bounds in the registered element's local frame (the basis change maps axis-aligned boxes to axis-aligned boxes).</summary>
        public static Bounds VolumeBounds(Bounds anchorLocal)
        {
            Matrix4x4 toLocal = ToElement.inverse;
            Vector3 min = anchorLocal.min, max = anchorLocal.max;
            Bounds result = default;
            for (int i = 0; i < 8; ++i)
            {
                Vector3 p = toLocal.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z));
                if (i == 0) result = new Bounds(p, Vector3.zero); else result.Encapsulate(p);
            }
            return result;
        }

        /// <summary>The measured outline (anchor-local XY) as the local polygon of the registered element: (x, -y), reversed so the orientation is kept.</summary>
        public static Vector2[] PlanePolygon(IReadOnlyList<Vector2> outline)
        {
            var polygon = new Vector2[outline.Count];
            for (int i = 0; i < outline.Count; ++i)
                polygon[outline.Count - 1 - i] = new Vector2(outline[i].x, -outline[i].y);
            return polygon;
        }

        /// <summary>
        /// The plane's full 3D surface in world coordinates, exactly through the app's transform, for the code that needs it
        /// (rendering, scan baking). It is not what is registered as the footprint.
        /// </summary>
        public static Vector3[] PlaneSurface(Matrix4x4 localToWorld, IReadOnlyList<Vector2> outline)
        {
            var world = new Vector3[outline.Count];
            for (int i = 0; i < outline.Count; ++i)
                world[i] = localToWorld.MultiplyPoint3x4(new Vector3(outline[i].x, outline[i].y, 0f));
            return world;
        }
    }

    /// <summary>Small injectable boundary used by local room observers and tests.</summary>
    public interface ILocalSceneGeometrySink
    {
        bool RegisterLocalSceneGeometry(SceneGeometryDescriptor descriptor);
    }

    /// <summary>Explicit mutation boundary for owned local scene observations.</summary>
    public interface IMutableLocalSceneGeometrySink : ILocalSceneGeometrySink
    {
        bool UpsertLocalSceneGeometry(SceneGeometryDescriptor descriptor);
        bool RemoveLocalSceneGeometry(int sourceKey);
    }

    /// <summary>Generation-scoped duplicate guard for observers that only add geometry.</summary>
    public sealed class SceneGeometryRegistrationTracker
    {
        private readonly HashSet<int> _registeredKeys = new HashSet<int>();
        private long _generation = long.MinValue;
        public bool IsRegistered(long generation, int sourceKey)
        {
            ResetForGeneration(generation); return _registeredKeys.Contains(sourceKey);
        }
        public void MarkRegistered(long generation, int sourceKey)
        {
            ResetForGeneration(generation); _registeredKeys.Add(sourceKey);
        }
        private void ResetForGeneration(long generation)
        {
            if (_generation == generation) return;
            _registeredKeys.Clear(); _generation = generation;
        }
    }
}
