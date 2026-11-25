// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>
    /// Where a holder sits so its content is centered in a drawn box and rests on the box's bottom face.
    /// The glTF pivot is the holder's origin and is not the mesh center: the sample vase's origin is its foot,
    /// and a center pivot would plant that foot at mid-height and stick out of the top. The content bounds are
    /// measured in the holder's local frame, then the holder is moved in the box's frame.
    /// </summary>
    public static class ScanBoxRest
    {
        /// <summary>
        /// The holder's world position. <paramref name="localBounds"/> is the content in the holder's local frame
        /// (not including <paramref name="holderScale"/>). The holder is then rotated to <paramref name="boxRotation"/>,
        /// whose local Y is the box's up: the annotation root sits on the base and the collider center is half a height
        /// above it, and <paramref name="boxCenter"/> is that collider's world center.
        /// </summary>
        public static Vector3 HolderPosition(Vector3 boxCenter, Vector3 boxSize, Quaternion boxRotation,
                                             Bounds localBounds, Vector3 holderScale)
        {
            Vector3 bottomCenter = new Vector3(localBounds.center.x, localBounds.min.y, localBounds.center.z);
            Vector3 boxBottom = boxCenter + boxRotation * new Vector3(0f, -Mathf.Abs(boxSize.y) * 0.5f, 0f);
            return boxBottom - boxRotation * Vector3.Scale(holderScale, bottomCenter);
        }

        /// <summary>
        /// Uniform multiplier that makes a content of <paramref name="contentSize"/> fit inside <paramref name="boxSize"/>.
        /// Each axis of the box limits it and the tightest wins, so the scale follows the box's own extents. A zero axis
        /// is ignored. Returns 1 when nothing can be compared.
        /// </summary>
        public static float UniformFit(Vector3 contentSize, Vector3 boxSize)
        {
            float fit = float.PositiveInfinity;
            Consider(contentSize.x, boxSize.x, ref fit);
            Consider(contentSize.y, boxSize.y, ref fit);
            Consider(contentSize.z, boxSize.z, ref fit);
            return float.IsPositiveInfinity(fit) ? 1f : fit;
        }

        private static void Consider(float content, float box, ref float fit)
        {
            content = Mathf.Abs(content);
            box = Mathf.Abs(box);
            if (content < 1e-6f || box < 1e-6f) return;
            fit = Mathf.Min(fit, box / content);
        }

        /// <summary>
        /// The renderers' bounds in <paramref name="holder"/>'s local frame. A missing holder or a holder with no mesh
        /// reports nothing. Lines, trails and particles are not the object. A skinned mesh is included from its shared
        /// mesh and from its posed world bounds; a stale local box is used only when neither has a size.
        /// </summary>
        public static bool TryContentBounds(Transform holder, out Bounds local)
        {
            local = default;
            if (holder == null) return false;
            bool any = false;
            Matrix4x4 worldToHolder = holder.worldToLocalMatrix;
            foreach (Renderer renderer in holder.GetComponentsInChildren<Renderer>())
            {
                if (renderer is LineRenderer || renderer is TrailRenderer || renderer is ParticleSystemRenderer) continue;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    // localBounds is a bind box and is often empty until the bones update. The shared mesh is the
                    // geometry; the world bounds add a pose that extends past it. A stale local box is only a fallback.
                    bool posed = false;
                    if (skinned.sharedMesh != null && HasExtent(skinned.sharedMesh.bounds))
                    {
                        Encapsulate(skinned.sharedMesh.bounds, worldToHolder * skinned.localToWorldMatrix, ref local, ref any);
                        posed = true;
                    }
                    if (HasExtent(skinned.bounds))
                    {
                        Encapsulate(skinned.bounds, worldToHolder, ref local, ref any);
                        posed = true;
                    }
                    if (!posed && HasExtent(skinned.localBounds))
                        Encapsulate(skinned.localBounds, worldToHolder * skinned.localToWorldMatrix, ref local, ref any);
                    continue;
                }
                Encapsulate(renderer.localBounds, worldToHolder * renderer.localToWorldMatrix, ref local, ref any);
            }
            return any;
        }

        private static bool HasExtent(Bounds bounds)
        {
            Vector3 size = bounds.size;
            return size.sqrMagnitude > 1e-8f &&
                   !float.IsNaN(size.x) && !float.IsNaN(size.y) && !float.IsNaN(size.z);
        }

        private static void Encapsulate(Bounds source, Matrix4x4 toSpace, ref Bounds local, ref bool any)
        {
            Vector3 centre = source.center;
            Vector3 extents = source.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = centre + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 localPoint = toSpace.MultiplyPoint3x4(point);
                if (!any)
                {
                    local = new Bounds(localPoint, Vector3.zero);
                    any = true;
                }
                else local.Encapsulate(localPoint);
            }
        }
    }
}
