// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Turns an annotated box into an explicit <see cref="SbSpatialAuthoring"/> declaration (T5.2, parity
    /// rows S2 and AV12). v1 turned the same box into a volume component that only the zone baseline read.
    /// Here the box is spatial definition, written once in the object's local frame:
    /// <list type="bullet">
    /// <item>the Containment Region is the extent of the box footprint, with no margin of its own;</item>
    /// <item>the footprint is the box base;</item>
    /// <item>the box top is an optional surface tagged <see cref="ScanBoxRegion.TopSurfaceTag"/>.</item>
    /// </list>
    /// Two objects meet this class. The annotation box declares itself while it exists, and withdraws the
    /// declaration when it is deleted. The scanned GLB the box produced takes the same declaration, and shares
    /// it with the content so the receiver writes identical authoring.
    /// </summary>
    public static class BoxAnnotationAuthoring
    {
        /// <summary>
        /// Writes <paramref name="region"/> into the target's <see cref="SbSpatialAuthoring"/>, adding the
        /// component when the target has none. Whatever the target declared before, a prefab's footprint
        /// collider, extent or affordance surfaces, is replaced: the box is the object's definition now.
        /// Idempotent. Returns null when the target or the region is missing.
        /// </summary>
        public static SbSpatialAuthoring Apply(GameObject target, ScanBoxRegion region)
        {
            if (target == null || region == null) return null;
            SbSpatialAuthoring authoring = target.GetComponent<SbSpatialAuthoring>();
            if (authoring == null) authoring = target.AddComponent<SbSpatialAuthoring>();

            authoring.footprint = null;
            authoring.footprintPolygon = region.FootprintPolygon();
            authoring.declaredExtent = region.ExtentPolygon();
            authoring.frameEulerAngles = Vector3.zero;
            authoring.includeChildAffordances = false;
            authoring.affordances = region.HasTopSurface
                ? new[]
                {
                    new AuthoredAffordanceSurface
                    {
                        id = ScanBoxRegion.TopSurfaceId,
                        tag = ScanBoxRegion.TopSurfaceTag,
                        supportId = ScanBoxRegion.TopSurfaceId,
                        surfacePolygon = region.TopSurface(),
                    },
                }
                : System.Array.Empty<AuthoredAffordanceSurface>();
            authoring.InvalidateRegion();
            return authoring;
        }

        /// <summary>
        /// Declares the box a collider describes. The annotation box keeps its pose at the bottom of the box,
        /// so the collider's own centre and size are the box in the object's local frame.
        /// </summary>
        public static SbSpatialAuthoring Declare(GameObject box, BoxCollider collider)
        {
            if (box == null || collider == null) return null;
            if (!ScanBoxRegion.TryFromLocalBox(collider.center, collider.size, out ScanBoxRegion region))
            {
                // A degenerate box declares nothing, and must not keep an earlier declaration.
                Withdraw(box);
                return null;
            }
            return Apply(box, region);
        }

        /// <summary>
        /// Declares the box a scan result names, for the object that was loaded for it. The object's current
        /// pose takes the world box into its local frame, so the declaration is the same on every copy.
        /// </summary>
        public static SbSpatialAuthoring Declare(GameObject loadedObject, ScanRegionHint hint)
        {
            if (loadedObject == null) return null;
            if (!ScanBoxRegion.TryFromWorldBox(hint.BoxCenter, hint.BoxSize, hint.Rotation,
                    loadedObject.transform.worldToLocalMatrix, out ScanBoxRegion region))
            {
                return null;
            }
            SbSpatialAuthoring authoring = Apply(loadedObject, region);
            SbNativeEntity native = loadedObject.GetComponent<SbNativeEntity>();
            if (native != null) native.SetAuthoredBox(region);
            return authoring;
        }

        /// <summary>True while the target carries a declaration that came from a box.</summary>
        public static bool HasDeclaration(GameObject target)
        {
            SbSpatialAuthoring authoring = target == null ? null : target.GetComponent<SbSpatialAuthoring>();
            return authoring != null && authoring.declaredExtent != null && authoring.declaredExtent.Length >= 3;
        }

        /// <summary>Removes the box's declaration. Deleting the box calls it, so a deleted box scopes nothing.</summary>
        public static bool Withdraw(GameObject target)
        {
            SbSpatialAuthoring authoring = target == null ? null : target.GetComponent<SbSpatialAuthoring>();
            if (authoring == null) return false;
            if (Application.isPlaying) Object.Destroy(authoring);
            else Object.DestroyImmediate(authoring);
            return true;
        }
    }
}
