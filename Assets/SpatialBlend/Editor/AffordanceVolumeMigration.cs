// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Editor-only migration for old content. Nothing at runtime reads <see cref="AffordanceVolume"/>
    /// (T3.6); an object's geometry is declared by <see cref="SbSpatialAuthoring"/>. This converts a
    /// volume left on old content into that declaration, then removes the volume.
    ///
    /// The volume polygon becomes the object's declared extent, which is the Containment Region.
    /// The volume's source collider, or the object's own collider, becomes the
    /// footprint when the object has no authoring yet. An existing authoring keeps its footprint and any
    /// extent it already declares.
    ///
    /// A CUSTOM volume built at runtime kept its polygon on a transient child collider that was never
    /// serialized, so there is nothing to convert from saved content. Only a volume that still has a
    /// polygon is converted.
    /// </summary>
    public static class AffordanceVolumeMigration
    {
        private const string MenuPath = "SpatialBlend/Migrate AffordanceVolume to SbSpatialAuthoring";

        /// <summary>
        /// Maps a volume polygon to the authoring frame, which is the object's local XZ. v1 placed the
        /// polygon at <paramref name="centerOffset"/> and scaled it by <paramref name="volumeScale"/>
        /// (the entity's lossy scale for a PRESET volume, one otherwise), in metres. The authoring
        /// frame is in the transform's local units, so the result is divided by the lossy scale.
        /// </summary>
        public static Vector2[] ToDeclaredExtent(IReadOnlyList<Vector2> points, Vector2 centerOffset,
                                                 Vector2 volumeScale, Vector2 lossyScale)
        {
            var extent = new Vector2[points.Count];
            for (int i = 0; i < points.Count; ++i)
            {
                float x = centerOffset.x + volumeScale.x * points[i].x;
                float z = centerOffset.y + volumeScale.y * points[i].y;
                extent[i] = new Vector2(
                    Mathf.Abs(lossyScale.x) > 1e-6f ? x / lossyScale.x : x,
                    Mathf.Abs(lossyScale.y) > 1e-6f ? z / lossyScale.y : z);
            }
            return extent;
        }

        /// <summary>
        /// Adds or fills an <see cref="SbSpatialAuthoring"/> on the volume's GameObject from the
        /// volume. The volume is left in place.
        /// </summary>
        public static bool TryConvert(AffordanceVolume volume, out SbSpatialAuthoring authoring, out string error)
        {
            authoring = null;
            error = null;
            if (volume == null)
            {
                error = "No AffordanceVolume.";
                return false;
            }

            if (!TryReadPolygon(volume, out Vector2[] points, out error))
                return false;

            GameObject target = volume.gameObject;
            Vector3 lossy = target.transform.lossyScale;
            Vector3 center = volume.EntityToAffordanceCenterTransform;
            Vector3 volumeScale = volume.type == AffordanceVolume.AffordanceVolumeType.PRESET ? lossy : Vector3.one;
            Vector2[] extent = ToDeclaredExtent(points, new Vector2(center.x, center.z),
                new Vector2(volumeScale.x, volumeScale.z), new Vector2(lossy.x, lossy.z));

            authoring = target.GetComponent<SbSpatialAuthoring>();
            if (authoring == null)
            {
                authoring = Undo.AddComponent<SbSpatialAuthoring>(target);
                authoring.footprint = volume.SourceCollider != null
                    ? volume.SourceCollider
                    : target.GetComponent<Collider>();
            }
            else
            {
                Undo.RecordObject(authoring, "Migrate AffordanceVolume");
            }

            if (authoring.declaredExtent == null || authoring.declaredExtent.Length < 3)
                authoring.declaredExtent = extent;
            authoring.InvalidateRegion();
            EditorUtility.SetDirty(authoring);
            return true;
        }

        private static bool TryReadPolygon(AffordanceVolume volume, out Vector2[] points, out string error)
        {
            points = null;
            error = null;
            if (volume.type == AffordanceVolume.AffordanceVolumeType.PRESET)
            {
                Vector2[] custom = volume.CustomPoints;
                if (custom == null || custom.Length < 3)
                {
                    error = "The PRESET volume has fewer than 3 points.";
                    return false;
                }
                points = new Vector2[custom.Length];
                for (int i = 0; i < custom.Length; ++i)
                    points[i] = custom[i] + volume.CustomCenterOffset;
                return true;
            }

            volume.GenerateVolume();
            PolygonCollider2D collider = volume.PolygonCollider;
            if (collider == null || collider.pathCount == 0 || collider.GetPath(0).Length < 3)
            {
                error = "The " + volume.type + " volume has no polygon to convert.";
                return false;
            }
            points = collider.GetPath(0);
            return true;
        }

        [MenuItem(MenuPath)]
        private static void MigrateSelection()
        {
            int converted = 0;
            foreach (GameObject selected in Selection.gameObjects)
            {
                foreach (AffordanceVolume volume in selected.GetComponentsInChildren<AffordanceVolume>(true))
                {
                    if (TryConvert(volume, out SbSpatialAuthoring authoring, out string error))
                    {
                        Undo.DestroyObjectImmediate(volume);
                        ++converted;
                    }
                    else
                    {
                        Debug.LogWarning($"[AffordanceVolumeMigration] '{volume.name}': {error}", volume);
                    }
                }
            }
            Debug.Log($"[AffordanceVolumeMigration] Converted {converted} AffordanceVolume component(s).");
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateMigrateSelection() => Selection.gameObjects.Length > 0;
    }
}
