// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct InteractionInfo
    {
        public int ProximityType { get; }
        public long SourceEntityId { get; }
        public long[] TargetEntityIds { get; }
        public ulong NumTargets { get; }
        public bool IsWildcard { get; }

        internal InteractionInfo(Internal.SbInteractionInfo native)
        {
            ProximityType = native.proximity_type;
            SourceEntityId = native.source_entity_id;
            TargetEntityIds = native.target_entity_ids;
            NumTargets = native.num_targets;
            IsWildcard = native.is_wildcard;
        }
    }
}