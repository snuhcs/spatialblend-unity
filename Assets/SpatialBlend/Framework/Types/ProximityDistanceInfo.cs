// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct ProximityDistanceInfo
    {
        public bool HasInfo { get; }
        public long SpaceId { get; }
        public long SourceEntityId { get; }
        public long TargetEntityId { get; }
        public int ProximityType { get; }
        public bool HasRemainingDistance { get; }
        public double RemainingDistance { get; }
        public bool HasProgressDistance { get; }
        public double ProgressDistance { get; }
        public bool HasPathDistance { get; }
        public double PathDistance { get; }
        public bool HasAngularDistance { get; }
        public double AngularDistance { get; }
        public bool HasAngularStep { get; }
        public double AngularStep { get; }

        internal ProximityDistanceInfo(Internal.SbProximityDistanceInfo native)
        {
            HasInfo = native.has_info;
            SpaceId = native.space_id;
            SourceEntityId = native.source_entity_id;
            TargetEntityId = native.target_entity_id;
            ProximityType = native.proximity_type;
            HasRemainingDistance = native.has_remaining_distance;
            RemainingDistance = native.remaining_distance;
            HasProgressDistance = native.has_progress_distance;
            ProgressDistance = native.progress_distance;
            HasPathDistance = native.has_path_distance;
            PathDistance = native.path_distance;
            HasAngularDistance = native.has_angular_distance;
            AngularDistance = native.angular_distance;
            HasAngularStep = native.has_angular_step;
            AngularStep = native.angular_step;
        }
    }
}

