// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend
{
    public struct StateVector
    {
        public double[] Values { get; }
        public ulong NumValues { get; }
        public StateType StateType { get; }

        internal StateVector(Internal.SbStateVector native)
        {
            Values = native.values;
            NumValues = native.num_values;
            StateType = (StateType)native.state_type;
        }
    }
}