// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

#if FUSION2
using Fusion;
#endif

/// <summary>
/// Relays move/teleport requests for networked objects so clients without authority
/// can request the StateAuthority to apply the final pose.
/// Attach this to networked objects that may be moved by MeshPopupUI.
/// Never attach it to an authored shared object (one with SbSpatialAuthoring, e.g. Chess or Whiteboard): each room
/// places its own copy of those, and this relay would send a world pose between rooms.
/// </summary>
#if FUSION2
public sealed class NetworkMoveRelay : NetworkBehaviour
#else
public sealed class NetworkMoveRelay : MonoBehaviour
#endif
{
#if FUSION2
    public bool CanMoveLocally => Object != null && Object.HasStateAuthority;
#else
    public bool CanMoveLocally => true;
#endif

    /// <summary>
    /// Apply a final authoritative pose. If not authority, sends a request to StateAuthority.
    /// </summary>
    public void ApplyFinalPose(Vector3 position, Quaternion rotation)
    {
#if FUSION2
        if (CanMoveLocally)
        {
            transform.SetPositionAndRotation(position, rotation);
        }
        else
        {
            RPC_RequestMove(position, rotation);
        }
#else
        transform.SetPositionAndRotation(position, rotation);
#endif
    }

#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestMove(Vector3 position, Quaternion rotation, RpcInfo info = default)
    {
        transform.SetPositionAndRotation(position, rotation);
    }
#endif
}


