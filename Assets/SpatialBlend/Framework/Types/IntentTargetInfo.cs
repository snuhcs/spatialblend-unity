// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    [StructLayout(LayoutKind.Sequential)]
    public struct IntentTargetInfo
    {
        public long target_entity_id;
        public double remaining_steps;
        [MarshalAs(UnmanagedType.I1)]
        public bool has_approach;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public float[] approach_position;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public float[] approach_direction;
        public int proximity_type; // ProximityType enum as int

        public long TargetEntityId => target_entity_id;
        public double RemainingSteps => remaining_steps;
        public bool HasApproach => has_approach;
        public float[] ApproachPosition => approach_position;
        public float[] ApproachDirection => approach_direction;
        public int ProximityType => proximity_type;

        internal IntentTargetInfo(Internal.SbIntentTargetInfo native)
        {
            target_entity_id = native.target_entity_id;
            remaining_steps = native.remaining_steps;
            has_approach = native.has_approach;
            approach_position = native.approach_position;
            approach_direction = native.approach_direction;
            proximity_type = native.proximity_type;
        }
    }
}

