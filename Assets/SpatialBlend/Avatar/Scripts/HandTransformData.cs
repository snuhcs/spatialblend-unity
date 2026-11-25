// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using Fusion;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Left and right hand pose relative to the head, as in v1.
    /// Replacing the four separate networked hand properties with this struct
    /// changes the Fusion networked layout of <see cref="SbAvatarBehaviourFusion"/>.
    /// A v1 build and a v2 build cannot share a room.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct HandTransformData : INetworkStruct
    {
        public Vector3 LeftPosition;
        public Vector3 RightPosition;
        public Quaternion LeftRotation;
        public Quaternion RightRotation;
    }
}
