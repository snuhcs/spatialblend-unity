// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct EntityState
    {
        public long EntityId { get; }
        public StateVector[] StateVectors { get; }
        public ulong NumStateVectors { get; }
        public byte[] UserData { get; }
        public ulong UserDataSize { get; }

        internal EntityState(Internal.SbEntityState native)
        {
            EntityId = native.entity_id;
            NumStateVectors = native.num_state_vectors;
            UserData = native.user_data;
            UserDataSize = native.user_data_size;

            StateVectors = new StateVector[NumStateVectors];
            if (native.state_vectors != null)
            {
                for (ulong i = 0; i < NumStateVectors; i++)
                {
                    StateVectors[i] = new StateVector(native.state_vectors[i]);
                }
            }
        }
    }
}