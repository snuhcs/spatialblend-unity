// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpatialBlend;

namespace SpatialBlend.Internal
{
    using SpaceID = System.Int64;
    using EntityID = System.Int64;

    // Managed callback shapes still stored by SpatialBlendManager. They are not
    // registered with a native library.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate EntityID OnShareDelegate(SpaceID spaceId, EntityID entityId, IntPtr userData, System.UInt64 userDataSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnRemoveDelegate(SpaceID spaceId, EntityID entityId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnUpdateStateDelegate(SpaceID spaceId, EntityID entityId, ProximityType originProximityType, StateType stateType,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)][In] double[] stateArr,
        System.UInt64 arrSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnUpdateDataDelegate(SpaceID spaceId, EntityID entityId, IntPtr userData, System.UInt64 userDataSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnLogDelegate(LogSeverity severity, [MarshalAs(UnmanagedType.LPStr)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnInteractionDelegate(SpaceID spaceId, InteractionEventType eventType, EntityID fromEntityId, EntityID toEntityId, ProximityType proximityType, System.UInt64 interactionTime);

    /// <summary>
    /// Managed representation of one state update in a batch.
    /// </summary>
    public struct StateUpdateBatchItem
    {
        public StateType Type;
        public double[] State;
    }

    public delegate void OnUpdateStateBatchDelegate(SpaceID spaceId, EntityID entityId, ProximityType proximityType, IReadOnlyList<StateUpdateBatchItem> updates);
}
