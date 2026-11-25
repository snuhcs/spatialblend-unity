// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using StateType = SpatialBlend.StateType;

namespace SpatialBlend
{
    public enum ProximityType : int
    {
        Angular1D = 0,
        Angular2D = 1,
        Euclidean2D = 2,
        Euclidean3D = 3,
    }

    internal class ProximityTypeExtensions
    {
        public static List<StateType> GetStaticStateTypes(ProximityType proximityType)
        {
            // TODO: implement this in native code
            switch (proximityType)
            {
                case ProximityType.Angular1D:
                    return new List<StateType> { StateType.Position2D };
                case ProximityType.Angular2D:
                    return new List<StateType> { StateType.Position3D };
                default:
                    return new List<StateType>();
            }
        }

        public static List<StateType> GetDynamicStateTypes(ProximityType proximityType)
        {
            // TODO: implement this in native code
            switch (proximityType)
            {
                case ProximityType.Angular1D:
                    return new List<StateType> { StateType.RotationYaw };
                case ProximityType.Angular2D:
                    return new List<StateType> { StateType.RotationYaw, StateType.RotationPitch };
                case ProximityType.Euclidean2D:
                    return new List<StateType> { StateType.Position2D };
                case ProximityType.Euclidean3D:
                    return new List<StateType> { StateType.Position3D };
                default:
                    return new List<StateType>();
            }
        }
    }
}
