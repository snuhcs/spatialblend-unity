// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct InteractionTimeline
    {
        public ExpectedInteractionTime[] Interactions { get; }
        public ulong NumInteractions { get; }
        public long SourceEntityId { get; }
        public int ProximityType { get; }

        internal InteractionTimeline(Internal.SbInteractionTimeline native)
        {
            // Convert the native array to our wrapper array
            Interactions = new ExpectedInteractionTime[native.num_interactions];
            for (ulong i = 0; i < native.num_interactions && i < (ulong)native.interactions.Length; i++)
            {
                Interactions[i] = new ExpectedInteractionTime(native.interactions[i]);
            }
            NumInteractions = native.num_interactions;
            SourceEntityId = native.source_entity_id;
            ProximityType = native.proximity_type;
        }
    }
}
