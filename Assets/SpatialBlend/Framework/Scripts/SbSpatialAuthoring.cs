// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace SpatialBlend
{
    [Serializable]
    public sealed class AuthoredSubRegion
    {
        public string name;
        public string tag;
        /// <summary>The patch in the surface's plane chart, relative to the surface centroid: see <see cref="SurfaceChart"/>.</summary>
        public Vector2[] polygon;
        [Tooltip("Rotation of the patch's frame about the surface normal, in degrees. Zero for a patch whose +x is the surface's.")]
        public float rotationDegrees;
    }

    [Serializable]
    public sealed class AuthoredAffordanceSurface
    {
        public string id;
        [Tooltip("Element tag. AffordancePose filters on this string, for example Writable or Playable.")]
        public string tag;
        public string supportId;
        public Collider surface;
        public Vector3[] surfacePolygon;
        public AuthoredSubRegion[] subRegions;
    }

    /// <summary>
    /// Per shared object: footprint, the declared extent of the object's spatial definition
    /// (the Containment Region), tagged affordance surfaces, and sub-regions. Both the source and
    /// the receiver compile this declaration onto the shared element. Geometry is not copied across
    /// the wire. Everything is written in the object's local frame, so two copies of the same
    /// prefab carry identical geometry whatever position and yaw each space gave its copy.
    /// </summary>
    [DisallowMultipleComponent]
    public class SbSpatialAuthoring : MonoBehaviour, IDeclaredRegion
    {
        public const float DefaultRegionMarginMeters = SpatialAuthoringCompiler.DefaultRegionMarginMeters;

        [Tooltip("Hierarchy id of the shared object. Defaults to the GameObject name.")]
        public string objectId;

        [Tooltip("Hierarchy id of the Containment Region. Defaults to the object id plus \"Area\".")]
        public string regionId;

        [Tooltip("Collider whose local XZ bounds are the physical footprint. Spacing and Facing use this, not the region.")]
        public Collider footprint;

        [Tooltip("Explicit footprint in local XZ. Used when Footprint is empty.")]
        public Vector2[] footprintPolygon;

        [Tooltip("The extent of this object's own spatial definition, in object-local XZ. This is the Containment Region. It must contain the footprint and every affordance surface. Leave it empty only when the object has no authored extent.")]
        [FormerlySerializedAs("explicitRegionPolygon")]
        public Vector2[] declaredExtent;

        [Tooltip("Fallback only. Metres added around the footprint when no extent is declared. The authoring default is 0.5 m.")]
        [FormerlySerializedAs("regionMarginMeters")]
        public float fallbackMarginMeters = DefaultRegionMarginMeters;

        public AuthoredAffordanceSurface[] affordances = Array.Empty<AuthoredAffordanceSurface>();

        [Tooltip("Also compile child SbAffordance components as tagged surfaces.")]
        public bool includeChildAffordances = true;

        [Tooltip("App key for a later owned correspondence. A label alone does not create one.")]
        public string correspondenceKey;

        [Tooltip("Rotation of the authoring frame inside this object, in degrees. All polygons are written in that frame, whose +Y is up. Zero when the object's local Y is up. An MRUK anchor has local +Z up, so its frame is (90, 0, 0).")]
        public Vector3 frameEulerAngles;

        private static readonly List<IDeclaredRegion> ActiveDeclaredRegions = new List<IDeclaredRegion>();

        /// <summary>
        /// Every enabled authoring component, as the declared regions the fallback
        /// region test reads. The core containment state is preferred whenever the
        /// shared object exists on this peer.
        /// </summary>
        public static IReadOnlyList<IDeclaredRegion> ActiveRegions => ActiveDeclaredRegions;

        // The footprint can change after load (a GLB collider is fitted to the mesh),
        // so the resolved region is kept for a short time and not for good.
        private const float RegionCacheSeconds = 0.25f;
        private Vector2[] _cachedRegion;
        private float _cachedRegionTime = float.NegativeInfinity;
        private SbEntity _entity;

        private void OnEnable()
        {
            if (!ActiveDeclaredRegions.Contains(this)) ActiveDeclaredRegions.Add(this);
            InvalidateRegion();
        }

        private void OnDisable()
        {
            ActiveDeclaredRegions.Remove(this);
            InvalidateRegion();
        }

        private void OnValidate() => InvalidateRegion();

        /// <summary>Drops the cached region after the footprint, extent or margin is edited.</summary>
        public void InvalidateRegion()
        {
            _cachedRegion = null;
            _cachedRegionTime = float.NegativeInfinity;
        }

        /// <summary>
        /// The declared Containment Region in the object's authoring frame (local XZ). It comes from
        /// <see cref="SpatialAuthoringCompiler.TryResolveRegionPolygon"/>, the same
        /// function the compiler uses for the element the core scopes by.
        /// </summary>
        public bool TryGetRegionPolygon(out Vector2[] polygon)
        {
            float now = Time.realtimeSinceStartup;
            if (_cachedRegion != null && now - _cachedRegionTime < RegionCacheSeconds)
            {
                polygon = _cachedRegion;
                return true;
            }
            polygon = null;
            if (!TryResolveFootprint(out Vector2[] footprintPoints, out _)) return false;
            float margin = fallbackMarginMeters;
            if (float.IsNaN(margin) || float.IsInfinity(margin) || margin < 0f)
            {
                margin = DefaultRegionMarginMeters;
            }
            if (!SpatialAuthoringCompiler.TryResolveRegionPolygon(footprintPoints, margin,
                    declaredExtent, out Vector2[] resolved, out _, out _))
            {
                return false;
            }
            _cachedRegion = resolved;
            _cachedRegionTime = now;
            polygon = resolved;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> may become this object's declared extent: a simple polygon that
        /// contains the object's footprint (the compiler refuses anything else when the object is shared, and until
        /// now the user was not told). The message is the compiler's.
        /// </summary>
        public bool TryValidateDeclaredExtent(Vector2[] candidate, out string error)
        {
            if (!TryResolveFootprint(out Vector2[] footprintPoints, out error)) return false;
            if (candidate == null || candidate.Length < 3)
            {
                error = "A declared extent needs at least three points.";
                return false;
            }
            float margin = float.IsNaN(fallbackMarginMeters) || float.IsInfinity(fallbackMarginMeters) || fallbackMarginMeters < 0f
                ? DefaultRegionMarginMeters : fallbackMarginMeters;
            if (!SpatialAuthoringCompiler.TryResolveRegionPolygon(footprintPoints, margin, candidate,
                    out Vector2[] region, out _, out error))
            {
                return false;
            }
            if (!SpatialAuthoringCompiler.ContainsAll(region, footprintPoints))
            {
                error = "The declared extent must contain the object footprint.";
                return false;
            }
            return true;
        }

        bool IDeclaredRegion.TryGetRegion(out Vector2[] localPolygon, out Matrix4x4 worldToLocal)
        {
            worldToLocal = Matrix4x4.identity;
            if (!TryGetRegionPolygon(out localPolygon)) return false;
            if (_entity == null) _entity = GetComponent<SbEntity>();
            Transform pose = _entity != null && _entity.EntityTransform != null
                ? _entity.EntityTransform
                : transform;
            // The polygon lives in the authoring frame, which is the object rotated by frameEulerAngles.
            worldToLocal = Matrix4x4.Rotate(Quaternion.Inverse(Quaternion.Euler(frameEulerAngles))) *
                           pose.worldToLocalMatrix;
            return true;
        }

        public bool TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error)
        {
            declaration = null;
            error = null;
            string resolvedObjectId = string.IsNullOrWhiteSpace(objectId) ? gameObject.name : objectId.Trim();
            string resolvedRegionId = string.IsNullOrWhiteSpace(regionId)
                ? resolvedObjectId + "Area"
                : regionId.Trim();
            if (!TryResolveFootprint(out Vector2[] footprintPoints, out error)) return false;

            var surfaces = new System.Collections.Generic.List<SpatialAffordanceDeclaration>();
            if (affordances != null)
            {
                foreach (AuthoredAffordanceSurface surface in affordances)
                {
                    if (surface == null) continue;
                    if (!TryResolveSurface(surface, out Vector3[] polygon, out error)) return false;
                    surfaces.Add(new SpatialAffordanceDeclaration
                    {
                        id = string.IsNullOrWhiteSpace(surface.id) ? surface.tag : surface.id.Trim(),
                        tag = surface.tag == null ? null : surface.tag.Trim(),
                        supportId = string.IsNullOrWhiteSpace(surface.supportId) ? surface.tag : surface.supportId.Trim(),
                        surface = polygon,
                        subRegions = CopySubRegions(surface.subRegions),
                    });
                }
            }
            if (includeChildAffordances)
            {
                foreach (SbAffordance child in GetComponentsInChildren<SbAffordance>(false))
                {
                    if (child == null) continue;
                    Collider boundary = child.ResolveBoundary();
                    if (!TryPolygonFromCollider(boundary, true, out Vector3[] surface, out error))
                    {
                        error = "SbAffordance '" + child.gameObject.name + "' has no surface collider.";
                        return false;
                    }
                    string tag = string.IsNullOrWhiteSpace(child.affordanceTag) ? "Affordance" : child.affordanceTag.Trim();
                    surfaces.Add(new SpatialAffordanceDeclaration
                    {
                        id = child.gameObject.name,
                        tag = tag,
                        supportId = child.gameObject.name,
                        surface = surface,
                    });
                }
            }

            float margin = fallbackMarginMeters;
            if (float.IsNaN(margin) || float.IsInfinity(margin) || margin < 0f)
            {
                margin = DefaultRegionMarginMeters;
            }
            declaration = new SpatialAuthoringDeclaration
            {
                objectId = resolvedObjectId,
                regionId = resolvedRegionId,
                footprint = footprintPoints,
                fallbackMarginMeters = margin,
                declaredExtent = declaredExtent,
                affordances = surfaces.ToArray(),
                correspondenceKey = correspondenceKey,
                frameRotation = Quaternion.Euler(frameEulerAngles),
            };
            return true;
        }

        private bool TryResolveFootprint(out Vector2[] polygon, out string error)
        {
            polygon = null;
            error = null;
            if (footprintPolygon != null && footprintPolygon.Length >= 3)
            {
                polygon = (Vector2[])footprintPolygon.Clone();
                return true;
            }
            Collider source = footprint != null ? footprint : GetComponent<Collider>();
            if (source == null)
            {
                error = "SbSpatialAuthoring requires a footprint polygon or a collider.";
                return false;
            }
            if (!TryPolygonFromCollider(source, false, out Vector3[] corners, out error)) return false;
            polygon = new Vector2[corners.Length];
            for (int index = 0; index < corners.Length; ++index)
            {
                polygon[index] = new Vector2(corners[index].x, corners[index].z);
            }
            return true;
        }

        private bool TryResolveSurface(AuthoredAffordanceSurface surface, out Vector3[] polygon, out string error)
        {
            polygon = null;
            error = null;
            if (surface.surfacePolygon != null && surface.surfacePolygon.Length >= 3)
            {
                polygon = (Vector3[])surface.surfacePolygon.Clone();
                return true;
            }
            return TryPolygonFromCollider(surface.surface, true, out polygon, out error);
        }

        private bool TryPolygonFromCollider(Collider source, bool atTop, out Vector3[] polygon, out string error)
        {
            polygon = null;
            error = null;
            if (source == null)
            {
                error = "A collider is required to derive the polygon.";
                return false;
            }
            if (!TryColliderLocalBox(source, out Vector3 localCenter, out Vector3 localSize))
            {
                error = "Collider '" + source.name + "' has no local box. Declare a polygon instead.";
                return false;
            }

            // The box is taken in the collider's own frame, then carried into this object's authoring frame.
            // Nothing here reads a world-space bound, so the result does not depend on the pose of
            // this copy of the object and is identical on the source and on every receiver.
            Matrix4x4 colliderToObject = Matrix4x4.Rotate(Quaternion.Inverse(Quaternion.Euler(frameEulerAngles))) *
                                         transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
            Vector3 half = localSize * 0.5f;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int corner = 0; corner < 8; ++corner)
            {
                Vector3 local = localCenter + new Vector3(
                    (corner & 1) == 0 ? -half.x : half.x,
                    (corner & 2) == 0 ? -half.y : half.y,
                    (corner & 4) == 0 ? -half.z : half.z);
                Vector3 inObject = colliderToObject.MultiplyPoint3x4(local);
                min = Vector3.Min(min, inObject);
                max = Vector3.Max(max, inObject);
            }
            float hx = (max.x - min.x) * 0.5f;
            float hz = (max.z - min.z) * 0.5f;
            if (hx < 1e-4f || hz < 1e-4f)
            {
                error = "The collider footprint is degenerate.";
                return false;
            }
            float cx = (max.x + min.x) * 0.5f;
            float cz = (max.z + min.z) * 0.5f;
            float y = atTop ? max.y : 0f;
            polygon = new[]
            {
                new Vector3(cx - hx, y, cz - hz),
                new Vector3(cx + hx, y, cz - hz),
                new Vector3(cx + hx, y, cz + hz),
                new Vector3(cx - hx, y, cz + hz),
            };
            return true;
        }

        private static bool TryColliderLocalBox(Collider source, out Vector3 center, out Vector3 size)
        {
            center = Vector3.zero;
            size = Vector3.zero;
            switch (source)
            {
                case BoxCollider box:
                    center = box.center;
                    size = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z));
                    return true;
                case SphereCollider sphere:
                    center = sphere.center;
                    size = Vector3.one * (2f * Mathf.Abs(sphere.radius));
                    return true;
                case CapsuleCollider capsule:
                {
                    center = capsule.center;
                    float diameter = 2f * Mathf.Abs(capsule.radius);
                    float length = Mathf.Max(Mathf.Abs(capsule.height), diameter);
                    size = new Vector3(diameter, diameter, diameter);
                    size[Mathf.Clamp(capsule.direction, 0, 2)] = length;
                    return true;
                }
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                    center = meshCollider.sharedMesh.bounds.center;
                    size = meshCollider.sharedMesh.bounds.size;
                    return true;
                default:
                    return false;
            }
        }

        private static SpatialSubRegion[] CopySubRegions(AuthoredSubRegion[] subRegions)
        {
            if (subRegions == null || subRegions.Length == 0) return null;
            var copy = new System.Collections.Generic.List<SpatialSubRegion>(subRegions.Length);
            foreach (AuthoredSubRegion subRegion in subRegions)
            {
                if (subRegion == null) continue;
                copy.Add(new SpatialSubRegion
                {
                    name = subRegion.name,
                    tag = subRegion.tag,
                    localPolygon = subRegion.polygon == null ? null : (Vector2[])subRegion.polygon.Clone(),
                    rotationDegrees = subRegion.rotationDegrees,
                });
            }
            return copy.Count == 0 ? null : copy.ToArray();
        }
    }
}
