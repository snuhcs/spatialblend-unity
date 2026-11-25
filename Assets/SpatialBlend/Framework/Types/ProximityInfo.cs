// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct ProximityInfo
    {
        public int ProximityType { get; }
        public bool IsActive { get; }
        public double CurrentValue { get; }
        public double Threshold { get; }

        internal ProximityInfo(Internal.SbProximityInfo native)
        {
            ProximityType = native.proximity_type;
            IsActive = native.is_active;
            CurrentValue = native.current_value;
            Threshold = native.threshold;
        }
    }
}