// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Interface for editable annotations that support vertex manipulation.
    /// </summary>
    public interface IEditableAnnotation
    {
        /// <summary>
        /// Number of vertices in the annotation.
        /// </summary>
        int VertexCount { get; }

        /// <summary>
        /// Get a vertex position in world space.
        /// </summary>
        Vector3 GetVertex(int index);

        /// <summary>
        /// Set a vertex position in world space.
        /// </summary>
        void SetVertex(int index, Vector3 worldPos);

        /// <summary>
        /// Whether this annotation supports adding/removing vertices.
        /// </summary>
        bool SupportsAddRemove { get; }

        /// <summary>
        /// Insert a new vertex after the specified index.
        /// </summary>
        void InsertVertex(int afterIndex, Vector3 worldPos);

        /// <summary>
        /// Remove the vertex at the specified index.
        /// </summary>
        void RemoveVertex(int index);

        /// <summary>
        /// Find the closest vertex to a world position within maxDistance.
        /// Returns -1 if no vertex is close enough.
        /// </summary>
        int FindClosestVertex(Vector3 worldPos, float maxDistance);

        /// <summary>
        /// Find the closest edge to a world position within maxDistance.
        /// Returns the index of the edge's starting vertex, or -1 if none found.
        /// </summary>
        int FindClosestEdge(Vector3 worldPos, float maxDistance);
    }
}
