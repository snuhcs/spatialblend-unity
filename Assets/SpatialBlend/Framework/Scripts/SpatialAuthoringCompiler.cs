// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    public enum SpatialElementRole
    {
        Containment,
        ColliderEnvelope,
        Surface,
    }

    /// <summary>
    /// One node of the element tree an <see cref="SpatialAuthoringDeclaration"/> compiles to.
    /// Polygons are in the object's local XZ. They do not depend on which space applies them.
    /// </summary>
    [Serializable]
    public sealed class SpatialElementMapping
    {
        public string id;
        public SpatialHierarchyNodeKind kind;
        public string parentId;
        public string tag;
        public SpatialElementRole role;
        public Vector2[] polygon;
        public Vector3[] surface;
        public SpatialSubRegion[] subRegions;
    }

    [Serializable]
    public sealed class SpatialAffordanceDeclaration
    {
        public string id;
        public string tag;
        public string supportId;
        public Vector3[] surface;
        public SpatialSubRegion[] subRegions;
    }

    /// <summary>
    /// Serialized authoring input. The same declaration is compiled on the source and the receiver.
    /// </summary>
    [Serializable]
    public sealed class SpatialAuthoringDeclaration
    {
        public string objectId;
        public string regionId;
        /// <summary>The physical footprint in object-local XZ. Spacing and Facing use this.</summary>
        public Vector2[] footprint;
        /// <summary>
        /// The extent of the object's own spatial definition in object-local XZ.
        /// This is the primary field: when it holds a polygon, it is the
        /// Containment Region as written.
        /// </summary>
        public Vector2[] declaredExtent;
        /// <summary>
        /// Used only when no extent is declared: the region is then the footprint expanded by
        /// this many metres.
        /// </summary>
        public float fallbackMarginMeters = SpatialAuthoringCompiler.DefaultRegionMarginMeters;
        public SpatialAffordanceDeclaration[] affordances = Array.Empty<SpatialAffordanceDeclaration>();
        public string correspondenceKey;
        /// <summary>
        /// Rotation of the authoring frame inside the object's own frame. Every polygon is written
        /// in the authoring frame, whose +Y is up. It is the identity for a prefab whose local Y is
        /// up. An MRUK anchor has local +Z up, so its frame is Euler(90, 0, 0).
        /// </summary>
        public Quaternion frameRotation = Quaternion.identity;
    }

    public enum SpatialExtentSource
    {
        /// <summary>The object declared its own extent.</summary>
        Declared,
        /// <summary>No extent was declared, so the region is the footprint plus the fallback margin.</summary>
        FootprintMargin,
    }

    public sealed class SpatialAuthoringPlan
    {
        public string ObjectId { get; internal set; }
        public string RegionId { get; internal set; }
        public string CorrespondenceKey { get; internal set; }
        public SpatialExtentSource ExtentSource { get; internal set; }
        public bool ExtentIsDeclared => ExtentSource == SpatialExtentSource.Declared;
        /// <summary>The fallback margin the declaration carried. It is not applied when the extent is declared.</summary>
        public float FallbackMarginMeters { get; internal set; }
        /// <summary>The margin actually added around the footprint: 0 for a declared extent.</summary>
        public float AppliedMarginMeters { get; internal set; }
        /// <summary>
        /// True when at least one affordance carries a bounded surface. A pose-fit relation
        /// payload (AffordancePose A4: ap_offset_u_m, ap_offset_v_m, ap_height_m,
        /// r_formula_version = 1) is only produced against such a surface.
        /// </summary>
        public bool PosePreserving { get; internal set; }
        /// <summary>The authoring frame inside the object frame. The identity unless the object's local Y is not up.</summary>
        public Matrix4x4 FrameToObject { get; internal set; } = Matrix4x4.identity;
        /// <summary>
        /// Hash of every local-frame polygon, tag and id the plan writes. It does not depend on the
        /// world pose of any copy, so equal signatures mean identical local geometry on both sides.
        /// </summary>
        public string LocalGeometrySignature { get; internal set; }
        public SpatialHierarchyDescriptor Descriptor { get; internal set; }
        public SpatialElementMapping[] Mappings { get; internal set; }
    }

    public readonly struct RelationBinding
    {
        /// <summary>
        /// Scope belongs to the relation family, so a binding names no scope. The tag is
        /// the AffordancePose binding's only: any other kind carries none.
        /// </summary>
        public RelationBinding(string anchor, RelationKind kind, string tag)
        {
            Anchor = anchor;
            Kind = kind;
            Tag = kind == RelationKind.AffordancePose ? tag : null;
        }

        public string Anchor { get; }
        public RelationKind Kind { get; }
        public string Tag { get; }
    }

    /// <summary>
    /// Compiles an explicit spatial declaration into the v2 element tree.
    /// The region is the object's parent. The footprint is the support. The margin band is only the region.
    /// </summary>
    public static class SpatialAuthoringCompiler
    {
        public const float DefaultRegionMarginMeters = 0.5f;
        public const string RoomParentId = "room";
        /// <summary>
        /// The anchor a Facing relation binds to: the head, which the received avatar samples (SbNetworkAvatarEntity
        /// reports the retarget head) and which every actor has from its first share. A Facing bound to an anchor the
        /// embodiment never samples gets NO_SAMPLE from the core and the avatar never turns toward the collaborator
        /// (v1 yawed the avatar's own transform, SbNetworkAvatarEntity.cs ApplyBatchYawTarget).
        /// </summary>
        public const string FacingAnchor = "head";
        public const string SpacingAnchor = "root";
        /// <summary>The pelvis anchor of the embodiment registration (bone Hips plus the measured sit-bone point): AffordancePose binds to it.</summary>
        public const string PelvisAnchor = "pelvis";

        public static long RegionSharedId(long objectSharedId) => checked(objectSharedId * 64L + 32L);

        public static long AffordanceSharedId(long objectSharedId, int index) =>
            checked(objectSharedId * 64L + 40L + index);

        public static bool TryCompile(SpatialAuthoringDeclaration declaration,
                                      out SpatialAuthoringPlan plan, out string error)
        {
            plan = null;
            error = null;
            if (declaration == null || !ValidId(declaration.objectId) || !ValidId(declaration.regionId))
            {
                error = "Spatial authoring requires an object id and a region id.";
                return false;
            }
            if (string.Equals(declaration.objectId, declaration.regionId, StringComparison.Ordinal))
            {
                error = "The region id and the object id must differ.";
                return false;
            }
            float margin = declaration.fallbackMarginMeters;
            if (float.IsNaN(margin) || float.IsInfinity(margin))
            {
                margin = DefaultRegionMarginMeters;
            }
            if (margin < 0f)
            {
                error = "The fallback region margin cannot be negative.";
                return false;
            }
            if (!TryCopyPolygon(declaration.footprint, out Vector2[] footprint, out error))
            {
                error = "The object footprint must be a simple polygon.";
                return false;
            }

            var affordances = declaration.affordances ?? Array.Empty<SpatialAffordanceDeclaration>();

            // The declared extent is the region. The footprint plus a margin
            // is only the fallback for an object that declares no extent. The same function
            // gives the fallback region test the polygon, so the two never disagree.
            if (!TryResolveRegionPolygon(footprint, margin, declaration.declaredExtent,
                    out Vector2[] regionPolygon, out bool declaredExtent, out error))
            {
                return false;
            }
            float appliedMargin = declaredExtent ? 0f : margin;
            if (declaredExtent)
            {
                if (!ContainsAll(regionPolygon, footprint))
                {
                    error = "The declared extent must contain the object footprint.";
                    return false;
                }
                for (int index = 0; index < affordances.Length; ++index)
                {
                    Vector3[] surface = affordances[index] == null ? null : affordances[index].surface;
                    if (surface == null) continue;
                    var projected = new Vector2[surface.Length];
                    for (int vertex = 0; vertex < surface.Length; ++vertex)
                    {
                        projected[vertex] = new Vector2(surface[vertex].x, surface[vertex].z);
                    }
                    if (!ContainsAll(regionPolygon, projected))
                    {
                        error = "The declared extent must contain every affordance surface.";
                        return false;
                    }
                }
            }

            var nodes = new List<SpatialHierarchyNode>(2 + affordances.Length);
            var mappings = new List<SpatialElementMapping>(2 + affordances.Length);
            nodes.Add(new SpatialHierarchyNode
            {
                id = declaration.regionId,
                kind = SpatialHierarchyNodeKind.Region,
                tag = declaration.regionId,
                localBoundaryPolygon = regionPolygon,
                localTransform = Matrix4x4.identity,
            });
            mappings.Add(new SpatialElementMapping
            {
                id = declaration.regionId,
                kind = SpatialHierarchyNodeKind.Region,
                parentId = RoomParentId,
                tag = declaration.regionId,
                role = SpatialElementRole.Containment,
                polygon = regionPolygon,
            });
            nodes.Add(new SpatialHierarchyNode
            {
                id = declaration.objectId,
                kind = SpatialHierarchyNodeKind.Object,
                parentId = declaration.regionId,
                tag = declaration.objectId,
                localFootprintPolygon = footprint,
                localTransform = Matrix4x4.identity,
            });
            mappings.Add(new SpatialElementMapping
            {
                id = declaration.objectId,
                kind = SpatialHierarchyNodeKind.Object,
                parentId = declaration.regionId,
                tag = declaration.objectId,
                role = SpatialElementRole.ColliderEnvelope,
                polygon = footprint,
            });

            var supportIds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < affordances.Length; ++index)
            {
                SpatialAffordanceDeclaration affordance = affordances[index];
                if (affordance == null || !ValidId(affordance.id) || !ValidId(affordance.tag) ||
                    !ValidId(affordance.supportId) || !supportIds.Add(affordance.supportId))
                {
                    error = "Each affordance requires an id, a tag, and a unique support id.";
                    return false;
                }
                if (!TryCopySurface(affordance.surface, out Vector3[] surface, out error))
                {
                    error = "Affordance '" + affordance.id + "' needs a simple surface polygon.";
                    return false;
                }
                if (!TryCopySubRegions(affordance.subRegions, out SpatialSubRegion[] subRegions, out error))
                {
                    return false;
                }
                nodes.Add(new SpatialHierarchyNode
                {
                    id = affordance.id,
                    kind = SpatialHierarchyNodeKind.Affordance,
                    parentId = declaration.objectId,
                    tag = affordance.tag,
                    supportId = affordance.supportId,
                    localSupportPolygon = surface,
                    localTransform = Matrix4x4.identity,
                    subRegions = subRegions,
                });
                mappings.Add(new SpatialElementMapping
                {
                    id = affordance.id,
                    kind = SpatialHierarchyNodeKind.Affordance,
                    parentId = declaration.objectId,
                    tag = affordance.tag,
                    role = SpatialElementRole.Surface,
                    surface = surface,
                    subRegions = subRegions,
                });
            }

            var descriptor = new SpatialHierarchyDescriptor
            {
                schema = SpatialHierarchyDescriptor.CurrentSchema,
                spaceId = "authored:" + declaration.objectId,
                nodes = nodes.ToArray(),
            };
            if (!descriptor.TryValidate(out error)) return false;
            SpatialElementMapping[] compiledMappings = mappings.ToArray();
            Quaternion frameRotation = declaration.frameRotation;
            float frameLength = frameRotation.x * frameRotation.x + frameRotation.y * frameRotation.y +
                                frameRotation.z * frameRotation.z + frameRotation.w * frameRotation.w;
            if (float.IsNaN(frameLength) || float.IsInfinity(frameLength) || frameLength < 1e-6f)
            {
                frameRotation = Quaternion.identity;
            }
            frameRotation = Quaternion.Normalize(frameRotation);
            plan = new SpatialAuthoringPlan
            {
                FrameToObject = Matrix4x4.Rotate(frameRotation),
                ObjectId = declaration.objectId,
                RegionId = declaration.regionId,
                CorrespondenceKey = declaration.correspondenceKey,
                ExtentSource = declaredExtent ? SpatialExtentSource.Declared : SpatialExtentSource.FootprintMargin,
                FallbackMarginMeters = margin,
                AppliedMarginMeters = appliedMargin,
                PosePreserving = affordances.Length > 0,
                LocalGeometrySignature = ComputeSignature(compiledMappings, frameRotation),
                Descriptor = descriptor,
                Mappings = compiledMappings,
            };
            return true;
        }

        /// <summary>
        /// Writes a local-frame surface into the world of one copy of the object. The surface is
        /// declared in the object's frame, so the support is the identity in that frame: the object
        /// pose alone places it, whichever position and yaw a space gave its copy.
        /// </summary>
        public static Vector3[] BakeSurface(Vector3[] localSurface, Matrix4x4 objectToWorld)
        {
            if (localSurface == null) return Array.Empty<Vector3>();
            var world = new Vector3[localSurface.Length];
            for (int index = 0; index < localSurface.Length; ++index)
            {
                world[index] = objectToWorld.MultiplyPoint3x4(localSurface[index]);
            }
            return world;
        }

        /// <summary>
        /// Heading of the surface frame about +Y, in degrees. The core builds that frame from the
        /// polygon's first edge (<c>Polygon3D::ComputeDerivedData</c>), so the relative yaw of an
        /// anchor depends only on the declared vertex order. A rigid copy of the object turns the
        /// heading by exactly the object's yaw.
        /// </summary>
        public static float SurfaceFrameHeadingDegrees(Vector3[] surface)
        {
            if (surface == null || surface.Length < 3) return float.NaN;
            Vector3 normal = Vector3.Cross(surface[1] - surface[0], surface[2] - surface[0]);
            if (normal.sqrMagnitude < 1e-12f) return float.NaN;
            normal.Normalize();
            Vector3 edge = surface[1] - surface[0];
            Vector3 xAxis = edge - Vector3.Dot(edge, normal) * normal;
            if (xAxis.sqrMagnitude < 1e-12f) return float.NaN;
            return Mathf.Atan2(xAxis.x, xAxis.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The normal the core derives from the vertex order, expressed in Unity axes. The Unity to core
        /// axis swap is a reflection, so it is the negated Unity cross product of the first two edges.
        /// A relation's height is measured along it, so it must point to the side the object is used from.
        /// </summary>
        public static Vector3 SurfaceNormalUnity(Vector3[] surface)
        {
            if (surface == null || surface.Length < 3) return Vector3.zero;
            Vector3 normal = -Vector3.Cross(surface[1] - surface[0], surface[2] - surface[0]);
            return normal.sqrMagnitude < 1e-12f ? Vector3.zero : normal.normalized;
        }

        internal static bool ContainsAll(Vector2[] polygon, Vector2[] points)
        {
            const float tolerance = 1e-4f;
            foreach (Vector2 point in points)
            {
                if (!ContainsOrTouches(polygon, point, tolerance)) return false;
            }
            return true;
        }

        private static bool ContainsOrTouches(Vector2[] polygon, Vector2 point, float tolerance)
        {
            bool inside = false;
            for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
            {
                Vector2 a = polygon[previous];
                Vector2 b = polygon[index];
                if (DistanceToSegment(point, a, b) <= tolerance) return true;
                if (((a.y > point.y) != (b.y > point.y)) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 edge = b - a;
            float lengthSquared = edge.sqrMagnitude;
            if (lengthSquared < 1e-12f) return (point - a).magnitude;
            float t = Mathf.Clamp01(Vector2.Dot(point - a, edge) / lengthSquared);
            return (point - (a + edge * t)).magnitude;
        }

        private static string ComputeSignature(SpatialElementMapping[] mappings, Quaternion frameRotation)
        {
            var text = new System.Text.StringBuilder();
            AppendSurface(text, new[] { frameRotation * Vector3.up, frameRotation * Vector3.forward });
            text.Append('\n');
            foreach (SpatialElementMapping mapping in mappings)
            {
                text.Append(mapping.kind).Append('|').Append(mapping.id).Append('|')
                    .Append(mapping.parentId).Append('|').Append(mapping.tag).Append('|')
                    .Append(mapping.role).Append('|');
                AppendPolygon(text, mapping.polygon);
                AppendSurface(text, mapping.surface);
                if (mapping.subRegions != null)
                {
                    foreach (SpatialSubRegion subRegion in mapping.subRegions)
                    {
                        text.Append("sub:").Append(subRegion.name).Append(':').Append(subRegion.tag).Append(':')
                            .Append(subRegion.rotationDegrees.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(':');
                        AppendPolygon(text, subRegion.localPolygon);
                    }
                }
                text.Append('\n');
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
                var hex = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte value in hash) hex.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static void AppendPolygon(System.Text.StringBuilder text, Vector2[] polygon)
        {
            if (polygon == null) return;
            foreach (Vector2 point in polygon)
            {
                text.Append(point.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
        }

        private static void AppendSurface(System.Text.StringBuilder text, Vector3[] surface)
        {
            if (surface == null) return;
            foreach (Vector3 point in surface)
            {
                text.Append(point.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
        }

        /// <summary>
        /// The Containment Region polygon in the object's authoring frame: the declared extent when
        /// it has three or more vertices, otherwise the footprint expanded by the fallback margin.
        /// The fallback region test reads the region through this function. It does not validate
        /// that the extent contains the footprint (<see cref="TryCompile"/> does).
        /// </summary>
        public static bool TryResolveRegionPolygon(Vector2[] footprint, float margin,
                                                   Vector2[] declaredExtent,
                                                   out Vector2[] regionPolygon, out bool isDeclared,
                                                   out string error)
        {
            error = null;
            isDeclared = declaredExtent != null && declaredExtent.Length >= 3;
            if (isDeclared)
            {
                if (!TryCopyPolygon(declaredExtent, out regionPolygon, out error))
                {
                    error = "The declared extent must be a simple polygon.";
                    return false;
                }
                return true;
            }
            regionPolygon = ExpandConvex(footprint, margin);
            if (!TryCopyPolygon(regionPolygon, out regionPolygon, out error))
            {
                error = "Expanding the footprint by the fallback margin did not produce a simple polygon.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Offsets a convex polygon outward by <paramref name="margin"/> metres.
        /// A rectangle grows by that amount on every side.
        /// </summary>
        public static Vector2[] ExpandConvex(Vector2[] polygon, float margin)
        {
            if (polygon == null) return Array.Empty<Vector2>();
            if (margin <= 0f || polygon.Length < 3) return (Vector2[])polygon.Clone();
            int count = polygon.Length;
            float twiceArea = 0f;
            for (int index = 0; index < count; ++index)
            {
                Vector2 current = polygon[index];
                Vector2 next = polygon[(index + 1) % count];
                twiceArea += current.x * next.y - next.x * current.y;
            }
            bool counterClockwise = twiceArea >= 0f;
            var expanded = new Vector2[count];
            for (int index = 0; index < count; ++index)
            {
                Vector2 previous = polygon[(index + count - 1) % count];
                Vector2 current = polygon[index];
                Vector2 next = polygon[(index + 1) % count];
                Vector2 outwardIn = OutwardNormal(current - previous, counterClockwise);
                Vector2 outwardOut = OutwardNormal(next - current, counterClockwise);
                Vector2 sum = outwardIn + outwardOut;
                float denominator = Vector2.Dot(sum, outwardIn);
                expanded[index] = Mathf.Abs(denominator) < 1e-5f
                    ? current + outwardIn * margin
                    : current + sum * (margin / denominator);
            }
            return expanded;
        }

        /// <summary>
        /// Legacy proximity binding. Angular kernels bind Facing on the head (the anchor the avatar samples).
        /// Spacing stays on root. The room id is not a scope.
        /// </summary>
        public static bool TryPlanLegacyInteraction(ProximityType proximityType, out RelationBinding binding)
        {
            switch (proximityType)
            {
                case ProximityType.Euclidean2D:
                case ProximityType.Euclidean3D:
                    binding = new RelationBinding(SpacingAnchor, RelationKind.Spacing, null);
                    return true;
                case ProximityType.Angular1D:
                case ProximityType.Angular2D:
                    binding = new RelationBinding(FacingAnchor, RelationKind.Facing, null);
                    return true;
                default:
                    binding = default;
                    return false;
            }
        }

        /// <summary>Plans one relation binding. The tag survives only on AffordancePose.</summary>
        public static RelationBinding PlanRelation(RelationKind kind, string anchor, string tag)
        {
            string resolvedAnchor = string.IsNullOrEmpty(anchor) ? SpacingAnchor : anchor;
            return new RelationBinding(resolvedAnchor, kind, string.IsNullOrEmpty(tag) ? null : tag);
        }

        private static Vector2 OutwardNormal(Vector2 edge, bool counterClockwise)
        {
            Vector2 perpendicular = counterClockwise
                ? new Vector2(edge.y, -edge.x)
                : new Vector2(-edge.y, edge.x);
            float magnitude = perpendicular.magnitude;
            return magnitude < 1e-8f ? Vector2.zero : perpendicular / magnitude;
        }

        private static bool ValidId(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;

        private static bool TryCopyPolygon(Vector2[] polygon, out Vector2[] copy, out string error)
        {
            copy = null;
            error = null;
            if (polygon == null || polygon.Length < 3)
            {
                error = "A polygon needs at least three vertices.";
                return false;
            }
            var lifted = new Vector3[polygon.Length];
            for (int index = 0; index < polygon.Length; ++index)
            {
                lifted[index] = new Vector3(polygon[index].x, 0f, polygon[index].y);
            }
            if (!SpatialBlend.Internal.SpatialAuthoringValidation.TryCopyValidPolygon(lifted, out _, out error))
            {
                return false;
            }
            copy = (Vector2[])polygon.Clone();
            return true;
        }

        private static bool TryCopySurface(Vector3[] surface, out Vector3[] copy, out string error)
        {
            copy = null;
            if (!SpatialBlend.Internal.SpatialAuthoringValidation.TryCopyValidPolygon(surface, out Vector3[] validated, out error))
            {
                return false;
            }
            copy = validated;
            return true;
        }

        private static bool TryCopySubRegions(SpatialSubRegion[] subRegions, out SpatialSubRegion[] copy, out string error)
        {
            error = null;
            if (subRegions == null || subRegions.Length == 0)
            {
                copy = null;
                return true;
            }
            copy = new SpatialSubRegion[subRegions.Length];
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < subRegions.Length; ++index)
            {
                SpatialSubRegion subRegion = subRegions[index];
                if (subRegion == null || !ValidId(subRegion.name) || !ValidId(subRegion.tag) ||
                    !names.Add(subRegion.name) ||
                    float.IsNaN(subRegion.rotationDegrees) || float.IsInfinity(subRegion.rotationDegrees) ||
                    !TryCopyPolygon(subRegion.localPolygon, out Vector2[] polygon, out error))
                {
                    error = "Each sub-region needs a unique name, a tag, and a simple polygon.";
                    copy = null;
                    return false;
                }
                copy[index] = new SpatialSubRegion
                {
                    name = subRegion.name,
                    tag = subRegion.tag,
                    localPolygon = polygon,
                    rotationDegrees = subRegion.rotationDegrees,
                };
            }
            return true;
        }
    }

    public sealed class SpatialAuthoringBinding
    {
        public long RegionElement { get; internal set; }
        public long ObjectElement { get; internal set; }
        public string RegionId { get; internal set; }
        public string ObjectId { get; internal set; }
    }

    /// <summary>
    /// Elements an app already owns for one authored object, keyed by affordance id.
    /// The MRUK scene-geometry adapter creates the object's Obstacle parent and its Surface
    /// plane child; authoring binds the plane as the tagged affordance rather than adding a
    /// second Surface element for the same anchor.
    /// </summary>
    public sealed class SpatialAuthoringExistingElements
    {
        /// <summary>The scene-geometry key of the anchor these elements are (its region is fitted around its box).</summary>
        public int SourceKey;
        public readonly Dictionary<string, long> Affordances =
            new Dictionary<string, long>(StringComparer.Ordinal);
    }
}
