// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using ProximityType = SpatialBlend.ProximityType;

namespace SpatialBlend
{
    [System.Serializable]
    public class Interaction
    {
        [SerializeField]
        private GameObject sourceObject;

        [SerializeField]
        private ProximityType interactionType;

        public GameObject Source => sourceObject;
        public ProximityType Type => interactionType;

        public Interaction(GameObject source, ProximityType type)
        {
            sourceObject = source;
            interactionType = type;
        }

        public bool IsValid()
        {
            if (sourceObject == null)
            {
                return false;
            }

            // each should have entity component
            if (sourceObject.GetComponent<SbEntity>() == null)
            {
                return false;
            }

            return true;
        }

        // Optional: ToString override for debugging
        public override string ToString()
        {
            return $"Interaction: {sourceObject?.name} ({interactionType})";
        }
    }

    /// <summary>
    /// SpatialBlend relation families. Each kind selects the
    /// kernel used to convert the actor's embodied behavior into a portable,
    /// support-relative spatial relation that is reconstructed in remote spaces.
    /// </summary>
    public enum RelationKind
    {
        // Preserves geodesic distance / approach toward a nearby referent (root anchor).
        Spacing,
        // Preserves directional engagement toward a nearby referent (chest / head anchor).
        Facing,
        // Preserves local pose relative to a tagged affordance surface (hips / hand anchor).
        // Affordance wiring is deferred; the kind exists so the API is forward-looking.
        AffordancePose,
    }

    /// <summary>
    /// Declares a v2 spatial relation: bind one of the actor's subject anchors
    /// to referents in the shared interaction hierarchy through a relation kernel.
    /// This is the v2 replacement for the proximity-based <see cref="Interaction"/>.
    /// </summary>
    [System.Serializable]
    public class SpatialRelation
    {
        [SerializeField]
        private GameObject sourceObject;

        [SerializeField]
        private string anchorName;

        [SerializeField]
        private RelationKind kind;

        [SerializeField]
        [Tooltip("Optional affordance tag, honoured by AffordancePose only. Leave empty for untagged binding.")]
        private string tag;

        public GameObject Source => sourceObject;
        public string AnchorName => anchorName;
        public RelationKind Kind => kind;
        public string Tag => tag;

        public SpatialRelation(GameObject source, string anchorName, RelationKind kind, string tag = null)
        {
            sourceObject = source;
            this.anchorName = anchorName;
            this.kind = kind;
            this.tag = tag;
        }

        public bool IsValid()
        {
            if (sourceObject == null || string.IsNullOrEmpty(anchorName))
            {
                return false;
            }

            return sourceObject.GetComponent<SbEntity>() != null;
        }

        public override string ToString()
        {
            string scope = string.IsNullOrEmpty(tag) ? "*" : tag;
            return $"SpatialRelation: {sourceObject?.name}.{anchorName} -> {kind} [{scope}]";
        }
    }

    // You can also create a MonoBehaviour wrapper if needed
    public class InteractionBehaviour : MonoBehaviour
    {
        [SerializeField]
        private Interaction interaction;

        public Interaction Interaction => interaction;
    }
}