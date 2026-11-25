// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System;
using System.Runtime.InteropServices;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;
using Fusion;

namespace SpatialBlend
{
    using EntityID = System.Int64;
    public class SbNetworkEntity : SbEntity
    {
        public override EntityType EntityType { get => EntityType.Network; }

        internal override ulong GetSyncSerializedLength()
        {
            // We need space for the NetworkObject's NetworkId (4 bytes) + position data (12 bytes: x, y, z as floats)
            return sizeof(uint) + (sizeof(float) * 3);
        }

        internal override void PrepareSync(IntPtr objectData)
        {
            NetworkObject networkObject = GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                SpatialBlendDebug.LogError(LogCategory.Scene, $"NetworkEntity::PrepareSync - NetworkObject not found in {gameObject.name}");
                return;
            }
            uint networkId = networkObject.Id.Raw;
            
            // Get the TRUE world position of the source entity (not local position)
            Vector3 sourceWorldPosition = gameObject.transform.position;
            
            // Store the source entity's position in the object data
            Marshal.WriteInt32(objectData, 0, (int)networkId);
            Marshal.WriteInt32(objectData, 4, BitConverter.ToInt32(BitConverter.GetBytes(sourceWorldPosition.x), 0));
            Marshal.WriteInt32(objectData, 8, BitConverter.ToInt32(BitConverter.GetBytes(sourceWorldPosition.y), 0));
            Marshal.WriteInt32(objectData, 12, BitConverter.ToInt32(BitConverter.GetBytes(sourceWorldPosition.z), 0));
            
            SpatialBlendDebug.Log(LogCategory.Scene, $"NetworkEntity::PrepareSync - Preparing to sync {gameObject.name} with NetworkId {networkId} at world position {sourceWorldPosition}");
        }

        internal static (GameObject, SbEntity) SyncRemote(IntPtr objectData)
        {
            uint receivedNetworkId = (uint)Marshal.ReadInt32(objectData, 0);
            float sourceX = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(objectData, 4)), 0);
            float sourceY = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(objectData, 8)), 0);
            float sourceZ = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(objectData, 12)), 0);
            Vector3 sourcePosition = new Vector3(sourceX, sourceY, sourceZ);
            
            GameObject newObject = null;
            SbEntity newEntity = null;

            UnityMainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                newObject = FindObjectByNetworkId(receivedNetworkId);
                if (newObject == null)
                {
                    Debug.LogWarning($"[FCShare] SyncRemote netId={receivedNetworkId} matched nothing. sourcePos={sourcePosition}");
                }
                else
                {
                    newEntity = newObject.GetComponent<SbEntity>();
                    // The network id is the correspondence. sourcePos is not applied.
                    SbTrace.Line($"[FCShare] SyncRemote netId={receivedNetworkId} matched '{newObject.name}' " +
                              $"localPos={newObject.transform.position} sourcePos={sourcePosition}");
                }
            }).Wait();
            return (newObject, newEntity);
        }

        private static GameObject FindObjectByNetworkId(uint networkId)
        {
            // No method that finds NetworkObject by NetworkId directly, so we have to iterate through all NetworkObjects
            // Maybe use FusionID for performance?
            NetworkObject[] allObjects = FindObjectsByType<NetworkObject>(FindObjectsSortMode.None);
            foreach (NetworkObject netobj in allObjects)
            {
                if (netobj.Id.Raw == networkId)
                {
                    return netobj.gameObject;
                }
            }
            return null;
        }
    }
}