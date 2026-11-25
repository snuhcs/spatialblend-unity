// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions.Annotation
{
    /// <summary>
    /// The geometry of the annotation graphics, with no rendering: the box's corners and edges, which faces look toward the
    /// viewer, which edges and corners are hidden behind the box itself, clipping a segment to the near plane, and the corner
    /// brackets. Kept pure so it can be tested directly.
    /// </summary>
    public static class AnnotationGeometry
    {
        // Corner order: 0..3 are the base (-x,-z), (+x,-z), (+x,+z), (-x,+z); 4..7 are the same corners at the top.
        // Faces: 0 bottom, 1 top, 2 -x, 3 +x, 4 -z, 5 +z.
        /// <summary>The 12 box edges: {corner, corner, face, face}.</summary>
        public static readonly int[,] BoxEdges =
        {
            {0,1,0,4},{1,2,0,3},{2,3,0,5},{3,0,0,2},
            {4,5,1,4},{5,6,1,3},{6,7,1,5},{7,4,1,2},
            {0,4,2,4},{1,5,3,4},{2,6,3,5},{3,7,2,5},
        };

        public static Vector3[] BoxCorners(Vector3 bottomCentre, Quaternion rotation, Vector3 size)
        {
            var c = new Vector3[8];
            BoxCorners(bottomCentre, rotation, size, c);
            return c;
        }

        /// <summary>Fills <paramref name="c"/> (8 entries) with the corners, without allocating.</summary>
        public static void BoxCorners(Vector3 bottomCentre, Quaternion rotation, Vector3 size, Vector3[] c)
        {
            for (int i = 0; i < 8; i++)
            {
                float sx = (i == 1 || i == 2 || i == 5 || i == 6) ? 1f : -1f;
                float sz = (i == 2 || i == 3 || i == 6 || i == 7) ? 1f : -1f;
                c[i] = bottomCentre + rotation * new Vector3(sx * size.x * 0.5f, i >= 4 ? size.y : 0f, sz * size.z * 0.5f);
            }
        }

        /// <summary>The three corners joined to <paramref name="corner"/> by an edge (two on its level, one above or below).</summary>
        public static void CornerNeighbours(int corner, out int ringNext, out int ringPrev, out int vertical)
        {
            int level = corner >= 4 ? 4 : 0;
            int i = corner - level;
            ringNext = level + (i + 1) % 4;
            ringPrev = level + (i + 3) % 4;
            vertical = corner >= 4 ? corner - 4 : corner + 4;
        }

        /// <summary>The index of the corner closest to a point.</summary>
        public static int NearestCorner(Vector3[] corners, Vector3 point)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < corners.Length; i++)
            {
                float d = (corners[i] - point).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Which of the six faces look toward the eye (a face looks toward it when the eye is on its outer side).</summary>
        public static void FacesToward(Vector3 centre, Quaternion rotation, Vector3 size, Vector3 eye, bool[] facing)
        {
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = f == 0 ? Vector3.down : f == 1 ? Vector3.up : f == 2 ? Vector3.left : f == 3 ? Vector3.right : f == 4 ? Vector3.back : Vector3.forward;
                float d = (f < 2 ? size.y : f < 4 ? size.x : size.z) * 0.5f;
                n = rotation * n;
                facing[f] = Vector3.Dot(n, eye - (centre + n * d)) > 0f;
            }
        }

        /// <summary>An edge is hidden behind the box when neither of its two faces looks toward the viewer (the box is convex).</summary>
        public static bool EdgeHidden(int edge, bool[] facing) => !facing[BoxEdges[edge, 2]] && !facing[BoxEdges[edge, 3]];

        /// <summary>A corner is hidden when none of its three faces looks toward the viewer.</summary>
        public static bool CornerHidden(int corner, bool[] facing)
        {
            int fy = corner >= 4 ? 1 : 0;
            int fx = (corner == 1 || corner == 2 || corner == 5 || corner == 6) ? 3 : 2;
            int fz = (corner == 2 || corner == 3 || corner == 6 || corner == 7) ? 5 : 4;
            return !facing[fy] && !facing[fx] && !facing[fz];
        }

        /// <summary>
        /// Clips a segment to the part at least <paramref name="near"/> metres in front of the camera. Returns false when nothing
        /// is left. <paramref name="t0"/> and <paramref name="t1"/> are where the kept part starts and ends on the original (0..1).
        /// </summary>
        public static bool ClipToNear(Matrix4x4 worldToCamera, float near, ref Vector3 a, ref Vector3 b, out float t0, out float t1)
        {
            t0 = 0f; t1 = 1f;
            float za = -worldToCamera.MultiplyPoint3x4(a).z, zb = -worldToCamera.MultiplyPoint3x4(b).z;
            if (za < near && zb < near) return false;
            var a0 = a; var b0 = b;
            if (za < near) { t0 = (near - za) / (zb - za); a = Vector3.Lerp(a0, b0, t0); }
            else if (zb < near) { t1 = (za - near) / (za - zb); b = Vector3.Lerp(a0, b0, t1); }
            return true;
        }

        /// <summary>The length of a corner bracket arm along an edge of the given length.</summary>
        public static float BracketLength(float edgeLength, AnnotationLook look)
        {
            return Mathf.Clamp(edgeLength * look.BracketFraction, Mathf.Min(look.BracketMin, edgeLength * 0.5f), look.BracketMax);
        }
    }
}
