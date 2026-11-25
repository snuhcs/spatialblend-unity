// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct ExpectedInteractionTime
    {
        public long TargetId { get; }
        public double Steps { get; }
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

        internal ExpectedInteractionTime(Internal.SbExpectedInteractionTime native)
        {
            TargetId = native.target_id;
            Steps = native.steps;
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
