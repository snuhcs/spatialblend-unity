// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// v2 (SpatialBlend) affordance surface — the lightweight replacement for the
    /// v1 <see cref="AffordanceVolume"/>.
    ///
    /// Attach this to the SPECIFIC interactive surface GameObject — a chalkboard's
    /// writable face, a sofa's seat, a desk's worktop — NOT the whole furniture.
    /// When the surface's parent <see cref="SbEntity"/> is registered, the manager
    /// registers this as an InteractionRegion CHILD element of the furniture's v2
    /// element (the AffordancePose relation's referent), deriving the footprint and
    /// height from a Collider on (or assigned to) this object.
    ///
    /// Because the v2 affordance kernel evaluates at any distance (proximity-scaled)
    /// and kProximal returns the whole container subtree, the surface does NOT need
    /// any geometry on the parent furniture — only this child element.
    /// </summary>
    public class SbAffordance : MonoBehaviour
    {
        [Tooltip("Affordance kind. The avatar's AffordancePose relation tag-filters " +
                 "on this string, so it must match the relation's tag (e.g. \"Writable\").")]
        public string affordanceTag = "Affordance";

        [Tooltip("Collider whose WORLD bounds define the surface footprint (XZ) and " +
                 "height (Y). Defaults to a Collider on this GameObject if left empty.")]
        public Collider boundarySource;

        /// <summary>The collider that defines this affordance's extent, or null.</summary>
        public Collider ResolveBoundary()
        {
            return boundarySource != null ? boundarySource : GetComponent<Collider>();
        }
    }
}
