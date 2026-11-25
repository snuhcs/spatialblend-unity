// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EntityTargetInfo
    {
        [MarshalAs(UnmanagedType.I1)] public bool has_target;
        public long target_entity_id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public double[] target_position_2d;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public double[] target_position_3d;
        public double steps_to_target;
        public int proximity_type;
        public double distance_to_target;

        public bool HasTarget => has_target;
        public long TargetEntityId => target_entity_id;
        public double[] TargetPosition2D => target_position_2d;
        public double[] TargetPosition3D => target_position_3d;
        public double StepsToTarget => steps_to_target;
        public int ProximityType => proximity_type;
        public double DistanceToTarget => distance_to_target;

        internal EntityTargetInfo(SbEntityTargetInfo native)
        {
            has_target = native.has_target;
            target_entity_id = native.target_entity_id;
            target_position_2d = native.target_position_2d;
            target_position_3d = native.target_position_3d;
            steps_to_target = native.steps_to_target;
            proximity_type = native.proximity_type;
            distance_to_target = native.distance_to_target;
        }
    }
}


