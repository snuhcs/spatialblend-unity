// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend.Internal
{
    // One geometry/identity contract for component and descriptor authoring.
    // Validation has no native, scene-object, or session side effects.
    internal static class SpatialAuthoringValidation
    {
        internal static bool ValidId(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;

        internal static bool IsFiniteAffine(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; ++index)
                if (!AnchorPoseFrameValidation.IsFinite(matrix[index])) return false;
            return Mathf.Abs(matrix.m30) < 1e-6f && Mathf.Abs(matrix.m31) < 1e-6f &&
                   Mathf.Abs(matrix.m32) < 1e-6f && Mathf.Abs(matrix.m33 - 1f) < 1e-6f &&
                   AnchorPoseFrameValidation.IsFinite(matrix.determinant) &&
                   Mathf.Abs(matrix.determinant) > 1e-8f;
        }

        internal static bool TryCopyValidPolygon(IReadOnlyList<Vector3> vertices,
                                                 out Vector3[] copy, out string error)
        {
            copy = null;
            error = "Support must be an ordered, finite, planar, simple polygon with nonzero area.";
            if (vertices == null || vertices.Count < 3) return false;
            var candidate = new Vector3[vertices.Count];
            Vector3 normal = Vector3.zero;
            for (int index = 0; index < candidate.Length; ++index)
            {
                candidate[index] = vertices[index];
                if (!AnchorPoseFrameValidation.IsFinite(candidate[index])) return false;
            }
            Vector3 origin = candidate[0];
            for (int index = 1; index < candidate.Length - 1; ++index)
                normal += Vector3.Cross(candidate[index] - origin, candidate[index + 1] - origin);
            if (!AnchorPoseFrameValidation.IsFinite(normal) || normal.sqrMagnitude < 1e-10f ||
                !AnchorPoseFrameValidation.IsFinite(normal.sqrMagnitude)) return false;
            normal.Normalize();
            int axis = Mathf.Abs(normal.x) > Mathf.Abs(normal.y) ? 0 : 1;
            if (Mathf.Abs(normal.z) > Mathf.Abs(normal[axis])) axis = 2;
            for (int index = 0; index < candidate.Length; ++index)
            {
                if (Mathf.Abs(Vector3.Dot(normal, candidate[index] - origin)) > 1e-4f ||
                    (candidate[index] - candidate[(index + 1) % candidate.Length]).sqrMagnitude < 1e-10f)
                    return false;
                for (int other = index + 1; other < candidate.Length; ++other)
                {
                    if (other == index + 1 || (index == 0 && other == candidate.Length - 1))
                        continue;
                    if (Intersects(Project(candidate[index], axis),
                                   Project(candidate[(index + 1) % candidate.Length], axis),
                                   Project(candidate[other], axis),
                                   Project(candidate[(other + 1) % candidate.Length], axis)))
                        return false;
                }
            }
            copy = candidate;
            error = null;
            return true;
        }

        private static Vector2 Project(Vector3 point, int omittedAxis) =>
            omittedAxis == 0 ? new Vector2(point.y, point.z) :
            omittedAxis == 1 ? new Vector2(point.x, point.z) : new Vector2(point.x, point.y);

        private static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
            ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

        private static bool Intersects(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            if (Mathf.Max(a.x, b.x) < Mathf.Min(c.x, d.x) ||
                Mathf.Max(c.x, d.x) < Mathf.Min(a.x, b.x) ||
                Mathf.Max(a.y, b.y) < Mathf.Min(c.y, d.y) ||
                Mathf.Max(c.y, d.y) < Mathf.Min(a.y, b.y)) return false;
            return Cross(a, b, c) * Cross(a, b, d) <= 0d && Cross(c, d, a) * Cross(c, d, b) <= 0d;
        }
    }
}
