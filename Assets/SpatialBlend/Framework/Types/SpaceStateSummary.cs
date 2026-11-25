// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct SpaceStateSummary
    {
        public SpaceInfo SpaceInfo { get; }
        public EntityCounts EntityCounts { get; }
        public GeometryBounds WalkableBounds { get; }
        public GeometryBounds WallBounds { get; }
        public ulong NumProximityTypes { get; }
        public bool HasAnyInteractions { get; }

        internal SpaceStateSummary(Internal.SbSpaceStateSummary native)
        {
            SpaceInfo = new SpaceInfo(native.space_info);
            EntityCounts = new EntityCounts(native.entity_counts);
            WalkableBounds = new GeometryBounds(native.walkable_bounds);
            WallBounds = new GeometryBounds(native.wall_bounds);
            NumProximityTypes = native.num_proximity_types;
            HasAnyInteractions = native.has_any_interactions;
        }
    }
}