// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend
{
    public enum SpatialHierarchyNodeKind { Region, Object, Affordance }

    /// <summary>
    /// Named patch on an affordance surface. Its tag names a correspondence, not a binding filter. The polygon is in the
    /// surface's plane chart (centroid origin, x along the first edge, y = normal x x; see <see cref="SurfaceChart"/>), because the
    /// core places a patch in its definition's own frame and Unity sends it with an identity transform.
    /// </summary>
    [Serializable]
    public sealed class SpatialSubRegion
    {
        public string name;
        public string tag;
        public Vector2[] localPolygon;
        /// <summary>
        /// Rotation of the patch's own frame about the owning definition's plane normal, in degrees (counter-clockwise in the
        /// definition's polygon plane). <c>sb_add_sub_region</c> places the patch in the definition's frame with this rotation
        /// as its <c>transform16</c>, so the patch's +x is the definition's +x turned by it. Zero is the identity. A seat
        /// uses it to make the patch's +x the seat's front (the confirmed front axis), which is the reference the
        /// core measures a pelvis' relative yaw from.
        /// </summary>
        public float rotationDegrees;
    }

    [Serializable]
    public sealed class SpatialHierarchyNode
    {
        public string id;
        public SpatialHierarchyNodeKind kind;
        public string parentId;
        public Matrix4x4 localTransform = Matrix4x4.identity;
        public string supportId;
        public Vector3[] localSupportPolygon;
        public string tag;
        /// <summary>Region containment polygon in the node's local XZ. Empty for other kinds.</summary>
        public Vector2[] localBoundaryPolygon;
        /// <summary>Object footprint in local XZ. This is the collider envelope, never the expanded region.</summary>
        public Vector2[] localFootprintPolygon;
        /// <summary>When positive, the Object node binds this already-created element instead of allocating one.</summary>
        public long existingElementId;
        public SpatialSubRegion[] subRegions;
    }

    /// <summary>Explicit endpoint names supplied separately from either space descriptor.</summary>
    [Serializable]
    public sealed class SpatialSupportCorrespondence
    {
        public string sourceSupportId;
        public string targetSupportId;
    }

    /// <summary>
    /// Data-only description of exactly one authored space. Cross-space support
    /// correspondence is deliberately separate because it needs two live spaces.
    /// </summary>
    [Serializable]
    public sealed class SpatialHierarchyDescriptor
    {
        public const int CurrentSchema = 1;
        public int schema = CurrentSchema;
        public string spaceId;
        public SpatialHierarchyNode[] nodes = Array.Empty<SpatialHierarchyNode>();

        public bool TryValidate(out string error) => TryCreateSnapshot(out _, out error);

        internal bool TryCreateSnapshot(out SpatialHierarchySnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;
            if (schema != CurrentSchema || !SpatialAuthoringValidation.ValidId(spaceId) ||
                nodes == null || nodes.Length == 0)
            {
                error = "Spatial hierarchy requires schema=1, a space ID, and nodes.";
                return false;
            }

            var byId = new Dictionary<string, SpatialHierarchyNode>(StringComparer.Ordinal);
            var supportIds = new HashSet<string>(StringComparer.Ordinal);
            var copies = new SpatialHierarchyNode[nodes.Length];
            for (int i = 0; i < nodes.Length; ++i)
            {
                SpatialHierarchyNode node = nodes[i];
                if (node == null || !SpatialAuthoringValidation.ValidId(node.id) ||
                    !Enum.IsDefined(typeof(SpatialHierarchyNodeKind), node.kind) ||
                    !byId.TryAdd(node.id, node) || !IsFiniteAffine(node.localTransform))
                {
                    error = "Node IDs/transforms must be unique, nonempty, finite affine matrices.";
                    return false;
                }
                if (!string.IsNullOrEmpty(node.tag) && !SpatialAuthoringValidation.ValidId(node.tag))
                {
                    error = "Element tags must be nonempty and must not contain NUL.";
                    return false;
                }
                if (node.kind == SpatialHierarchyNodeKind.Affordance)
                {
                    if (!SpatialAuthoringValidation.ValidId(node.supportId) || !supportIds.Add(node.supportId) ||
                        !IsValidSupportPolygon(node.localSupportPolygon))
                    {
                        error = "Each affordance requires one unique support ID and a finite nondegenerate local polygon.";
                        return false;
                    }
                    if (node.existingElementId != 0 || HasPolygon(node.localBoundaryPolygon) ||
                        HasPolygon(node.localFootprintPolygon))
                    {
                        error = "An affordance defines a support surface, not a region boundary, footprint, or existing element.";
                        return false;
                    }
                    if (!TryCopySubRegions(node.subRegions, out _, out error)) return false;
                }
                else if (node.kind == SpatialHierarchyNodeKind.Region)
                {
                    if (!string.IsNullOrEmpty(node.supportId) || HasPolygon(node.localSupportPolygon) ||
                        HasPolygon(node.localFootprintPolygon) || node.existingElementId != 0 ||
                        HasSubRegions(node.subRegions))
                    {
                        error = "A region may define only a containment boundary and a tag.";
                        return false;
                    }
                    if (!IsValidBoundary(node.localBoundaryPolygon, out error)) return false;
                }
                else
                {
                    if (!string.IsNullOrEmpty(node.supportId) || HasPolygon(node.localSupportPolygon) ||
                        HasPolygon(node.localBoundaryPolygon) || HasSubRegions(node.subRegions))
                    {
                        error = "An object may define a footprint and an existing element id.";
                        return false;
                    }
                    if (node.existingElementId < 0)
                    {
                        error = "An existing element id must be positive.";
                        return false;
                    }
                    if (!IsValidBoundary(node.localFootprintPolygon, out error)) return false;
                }
                copies[i] = CopyNode(node);
                byId[node.id] = copies[i];
            }

            // Validate the copied forest. Regions may nest, and an Object may
            // attach directly to the space when no semantic region was authored.
            // Affordances still require an Object parent; unknown IDs never imply
            // a synthetic region or an inferred support.
            foreach (SpatialHierarchyNode node in copies)
            {
                if (string.IsNullOrEmpty(node.parentId))
                {
                    if (node.kind == SpatialHierarchyNodeKind.Affordance)
                    {
                        error = "An affordance requires an Object parent.";
                        return false;
                    }
                }
                else if (!SpatialAuthoringValidation.ValidId(node.parentId) ||
                         !byId.TryGetValue(node.parentId, out SpatialHierarchyNode parent) ||
                         !IsAllowedParent(parent.kind, node.kind))
                {
                    error = "Regions/objects may nest under a Region; affordances require an Object parent.";
                    return false;
                }
            }
            if (!TryOrderByParents(copies, out SpatialHierarchyNode[] ordered))
            {
                error = "Hierarchy contains a parent cycle.";
                return false;
            }
            if (!TryCreateWorldSupportPolygons(ordered, out Dictionary<string, Vector3[]> worldSupports,
                out error)) return false;
            snapshot = new SpatialHierarchySnapshot(spaceId, ordered, worldSupports);
            return true;
        }

        private static bool IsAllowedParent(SpatialHierarchyNodeKind parent,
                                            SpatialHierarchyNodeKind child) =>
            (parent == SpatialHierarchyNodeKind.Region &&
             (child == SpatialHierarchyNodeKind.Region || child == SpatialHierarchyNodeKind.Object)) ||
            (parent == SpatialHierarchyNodeKind.Object && child == SpatialHierarchyNodeKind.Affordance);

        private static bool TryOrderByParents(SpatialHierarchyNode[] nodes,
                                              out SpatialHierarchyNode[] ordered)
        {
            var children = new Dictionary<string, List<SpatialHierarchyNode>>(StringComparer.Ordinal);
            var ready = new Queue<SpatialHierarchyNode>();
            foreach (SpatialHierarchyNode node in nodes)
            {
                if (string.IsNullOrEmpty(node.parentId)) ready.Enqueue(node);
                else
                {
                    if (!children.TryGetValue(node.parentId, out List<SpatialHierarchyNode> siblings))
                        children.Add(node.parentId, siblings = new List<SpatialHierarchyNode>());
                    siblings.Add(node);
                }
            }
            var result = new List<SpatialHierarchyNode>(nodes.Length);
            while (ready.Count > 0)
            {
                SpatialHierarchyNode node = ready.Dequeue();
                result.Add(node);
                if (children.TryGetValue(node.id, out List<SpatialHierarchyNode> descendants))
                    foreach (SpatialHierarchyNode child in descendants) ready.Enqueue(child);
            }
            ordered = result.Count == nodes.Length ? result.ToArray() : null;
            return ordered != null;
        }

        private static bool IsValidSupportPolygon(Vector3[] polygon) =>
            SpatialAuthoringValidation.TryCopyValidPolygon(polygon, out _, out _);

        private static bool HasPolygon(Vector2[] polygon) => polygon != null && polygon.Length != 0;

        private static bool HasPolygon(Vector3[] polygon) => polygon != null && polygon.Length != 0;

        private static bool HasSubRegions(SpatialSubRegion[] subRegions) =>
            subRegions != null && subRegions.Length != 0;

        private static bool IsValidBoundary(Vector2[] polygon, out string error)
        {
            error = null;
            if (!HasPolygon(polygon)) return true;
            var lifted = new Vector3[polygon.Length];
            for (int index = 0; index < polygon.Length; ++index)
            {
                if (!IsFinite(polygon[index].x) || !IsFinite(polygon[index].y))
                {
                    error = "A boundary polygon must be finite.";
                    return false;
                }
                lifted[index] = new Vector3(polygon[index].x, 0f, polygon[index].y);
            }
            if (!SpatialAuthoringValidation.TryCopyValidPolygon(lifted, out _, out error))
            {
                error = "A boundary must be an ordered, finite, simple polygon with nonzero area.";
                return false;
            }
            return true;
        }

        private static bool TryCopySubRegions(SpatialSubRegion[] subRegions,
                                              out SpatialSubRegion[] copy, out string error)
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
                if (subRegion == null || !SpatialAuthoringValidation.ValidId(subRegion.name) ||
                    !SpatialAuthoringValidation.ValidId(subRegion.tag) || !names.Add(subRegion.name) ||
                    !IsValidBoundary(subRegion.localPolygon, out error) || !HasPolygon(subRegion.localPolygon) ||
                    float.IsNaN(subRegion.rotationDegrees) || float.IsInfinity(subRegion.rotationDegrees))
                {
                    error = "Each sub-region needs a unique name, a tag, and a simple polygon.";
                    copy = null;
                    return false;
                }
                copy[index] = new SpatialSubRegion
                {
                    name = subRegion.name,
                    tag = subRegion.tag,
                    localPolygon = (Vector2[])subRegion.localPolygon.Clone(),
                    rotationDegrees = subRegion.rotationDegrees,
                };
            }
            return true;
        }

        internal static SpatialHierarchyNode CopyNode(SpatialHierarchyNode node)
        {
            TryCopySubRegions(node.subRegions, out SpatialSubRegion[] subRegions, out _);
            return new SpatialHierarchyNode
            {
                id = node.id,
                kind = node.kind,
                parentId = node.parentId,
                localTransform = node.localTransform,
                supportId = node.supportId,
                localSupportPolygon = node.localSupportPolygon == null ? null :
                    (Vector3[])node.localSupportPolygon.Clone(),
                tag = node.tag,
                localBoundaryPolygon = node.localBoundaryPolygon == null ? null :
                    (Vector2[])node.localBoundaryPolygon.Clone(),
                localFootprintPolygon = node.localFootprintPolygon == null ? null :
                    (Vector2[])node.localFootprintPolygon.Clone(),
                existingElementId = node.existingElementId,
                subRegions = subRegions,
            };
        }

        private static bool IsFiniteAffine(Matrix4x4 matrix) =>
            SpatialAuthoringValidation.IsFiniteAffine(matrix);

        // Native surface vertices are baked-world, while hierarchy nodes remain
        // locally authored. Compose an immutable world polygon before allocation
        // and reject overflow rather than exporting finite local fragments that
        // combine to an unrepresentable support.
        private static bool TryCreateWorldSupportPolygons(SpatialHierarchyNode[] nodes,
            out Dictionary<string, Vector3[]> worldSupports, out string error)
        {
            worldSupports = new Dictionary<string, Vector3[]>(StringComparer.Ordinal);
            var worlds = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            // The snapshot is topologically ordered. Computing iteratively also
            // keeps deeply nested recorded regions off the managed call stack.
            foreach (SpatialHierarchyNode node in nodes)
            {
                Matrix4x4 world = string.IsNullOrEmpty(node.parentId) ? node.localTransform :
                    worlds[node.parentId] * node.localTransform;
                if (!IsFiniteAffine(world))
                {
                    error = "Composed hierarchy transforms must remain finite affine matrices.";
                    return false;
                }
                worlds.Add(node.id, world);
                if (node.kind != SpatialHierarchyNodeKind.Affordance) continue;
                var vertices = new Vector3[node.localSupportPolygon.Length];
                for (int i = 0; i < vertices.Length; ++i)
                {
                    Vector3 vertex = world.MultiplyPoint3x4(node.localSupportPolygon[i]);
                    if (!IsFinite(vertex))
                    {
                        error = "Composed support vertices must remain finite.";
                        return false;
                    }
                    vertices[i] = vertex;
                }
                if (!SpatialAuthoringValidation.TryCopyValidPolygon(vertices, out Vector3[] validated,
                    out error)) return false;
                worldSupports.Add(node.id, validated);
            }
            error = null;
            return true;
        }

        internal static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        internal static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal sealed class SpatialHierarchySnapshot
    {
        public readonly string SpaceId;
        private readonly SpatialHierarchyNode[] _nodes;
        public SpatialHierarchyNode[] Nodes => CopyNodes(_nodes);
        private readonly Dictionary<string, Vector3[]> _worldSupportPolygons;
        public SpatialHierarchySnapshot(string spaceId, SpatialHierarchyNode[] nodes,
                                        Dictionary<string, Vector3[]> worldSupportPolygons)
        {
            SpaceId = spaceId;
            _nodes = CopyNodes(nodes);
            _worldSupportPolygons = new Dictionary<string, Vector3[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Vector3[]> pair in worldSupportPolygons)
                _worldSupportPolygons.Add(pair.Key, (Vector3[])pair.Value.Clone());
        }

        private static SpatialHierarchyNode[] CopyNodes(SpatialHierarchyNode[] nodes)
        {
            var copies = new SpatialHierarchyNode[nodes.Length];
            for (int index = 0; index < nodes.Length; ++index)
            {
                SpatialHierarchyNode node = nodes[index];
                copies[index] = SpatialHierarchyDescriptor.CopyNode(node);
            }
            return copies;
        }

        /// <summary>
        /// Returns a defensive copy of the baked-world native surface. It is
        /// immutable for this snapshot/generation; later authoring edits require
        /// a new registration to rebuild the native surface.
        /// </summary>
        public bool TryGetWorldSupportVertices(string nodeId, out Vector3[] vertices)
        {
            if (_worldSupportPolygons.TryGetValue(nodeId, out Vector3[] stored))
            {
                vertices = (Vector3[])stored.Clone();
                return true;
            }
            vertices = null;
            return false;
        }
    }

    public sealed class SpatialHierarchyRegistration
    {
        private readonly SpatialBlendManager _owner;
        private readonly Dictionary<string, long> _elements;
        private readonly Dictionary<string, long> _supports;

        internal SpatialHierarchyRegistration(SpatialBlendManager owner, string authoredSpaceId,
                                              long nativeSpace, long generation,
                                              Dictionary<string, long> elements,
                                              Dictionary<string, long> supports)
        {
            _owner = owner;
            AuthoredSpaceId = authoredSpaceId;
            NativeSpace = nativeSpace;
            Generation = generation;
            _elements = new Dictionary<string, long>(elements, StringComparer.Ordinal);
            _supports = new Dictionary<string, long>(supports, StringComparer.Ordinal);
        }

        public string AuthoredSpaceId { get; }
        public long NativeSpace { get; }
        public long Generation { get; }
        public bool IsLive => _owner != null &&
            _owner.IsSpatialHierarchyRegistrationCurrent(NativeSpace, Generation);
        public bool TryGetElement(string id, out long element) => _elements.TryGetValue(id, out element);
        public bool TryGetSupportElement(string supportId, out long element) => _supports.TryGetValue(supportId, out element);
    }

    public readonly struct ResolvedSpatialSupportCorrespondence
    {
        public ResolvedSpatialSupportCorrespondence(string sourceSupportId, string sourceAuthoredSpaceId,
                                                    long sourceNativeSpace, long sourceGeneration,
                                                    long sourceElementId, string targetSupportId,
                                                    string targetAuthoredSpaceId, long targetNativeSpace,
                                                    long targetGeneration, long targetElementId)
        {
            SourceSupportId = sourceSupportId; SourceAuthoredSpaceId = sourceAuthoredSpaceId;
            SourceNativeSpace = sourceNativeSpace; SourceGeneration = sourceGeneration;
            SourceElementId = sourceElementId; TargetSupportId = targetSupportId;
            TargetAuthoredSpaceId = targetAuthoredSpaceId; TargetNativeSpace = targetNativeSpace;
            TargetGeneration = targetGeneration; TargetElementId = targetElementId;
        }
        public string SourceSupportId { get; }
        public string SourceAuthoredSpaceId { get; }
        public long SourceNativeSpace { get; }
        public long SourceGeneration { get; }
        public long SourceElementId { get; }
        public string TargetSupportId { get; }
        public string TargetAuthoredSpaceId { get; }
        public long TargetNativeSpace { get; }
        public long TargetGeneration { get; }
        public long TargetElementId { get; }
    }

    /// <summary>Explicit export boundary; it does not imply native linking by itself.</summary>
    public interface ISpatialSupportCorrespondenceSink
    {
        bool TryPublish(IReadOnlyList<ResolvedSpatialSupportCorrespondence> correspondences);
    }
}
