// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct SpaceInfo
    {
        public ulong NumEntities { get; }
        public ulong NumConnectedSpaces { get; }
        public bool IsHost { get; }
        public bool IsConnected { get; }
        public bool HasWalkableGeometry { get; }
        public bool HasWallGeometry { get; }

        internal SpaceInfo(Internal.SbSpaceInfo native)
        {
            NumEntities = native.num_entities;
            NumConnectedSpaces = native.num_connected_spaces;
            IsHost = native.is_host;
            IsConnected = native.is_connected;
            HasWalkableGeometry = native.has_walkable_geometry;
            HasWallGeometry = native.has_wall_geometry;
        }
    }
}