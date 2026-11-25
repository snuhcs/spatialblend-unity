// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct EntityCounts
    {
        public ulong TotalEntities { get; }
        public ulong LocalEntities { get; }
        public ulong RemoteEntities { get; }
        public ulong DirtyEntities { get; }

        internal EntityCounts(Internal.SbEntityCounts native)
        {
            TotalEntities = native.total_entities;
            LocalEntities = native.local_entities;
            RemoteEntities = native.remote_entities;
            DirtyEntities = native.dirty_entities;
        }
    }
}