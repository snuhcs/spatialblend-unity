// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct EntityInfo
    {
        public long EntityId { get; }
        public bool IsLocal { get; }
        public bool IsValid { get; }
        public ulong UserDataSize { get; }
        public double[] Position2D { get; }
        public double[] Position3D { get; }
        public double RotationYaw { get; }
        public double RotationPitch { get; }

        internal EntityInfo(Internal.SbEntityInfo native)
        {
            EntityId = native.entity_id;
            IsLocal = native.is_local;
            IsValid = native.is_valid;
            UserDataSize = native.user_data_size;
            Position2D = native.position_2d;
            Position3D = native.position_3d;
            RotationYaw = native.rotation_yaw;
            RotationPitch = native.rotation_pitch;
        }
    }
}