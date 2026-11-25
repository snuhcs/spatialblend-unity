// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System;
using System.Runtime.InteropServices;
using LogCategory = SpatialBlend.SpatialBlendDebug.LogCategory;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;

namespace SpatialBlend
{
    public class SbLocalEntity : SbEntity
    {
        public override EntityType EntityType { get => EntityType.Local; }

        private static string FormatDataAsHex(IntPtr data, ulong length)
        {
            if (data == IntPtr.Zero || length == 0)
                return "Empty";

            byte[] bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return BitConverter.ToString(bytes).Replace("-", " ");
        }

        internal override ulong GetSyncSerializedLength()
        {
            // We only need space for the UID (4 bytes)
            return sizeof(int);
        }

        internal override void PrepareSync(IntPtr objectData)
        {
            GameObject newObject = Instantiate(gameObject);
            var sbEntity = newObject.GetComponent<SbEntity>();
            sbEntity?.Detach();

            Marshal.WriteInt32(objectData, newObject.GetInstanceID());
        }

        internal static (GameObject, SbEntity) SyncRemote(IntPtr objectData)
        {
            int receivedUID = Marshal.ReadInt32(objectData);
            GameObject newObject = null;
            SbEntity newEntity = null;
            // FindObjectByUID and GetComponent must be read on the main thread
            UnityMainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                newObject = FindObjectByUID(receivedUID);

                if (newObject != null)
                {
                    newEntity = newObject.GetComponent<SbEntity>();
                }
            }).Wait();
            return (newObject, newEntity);
        }

        // This method must be called on the main thread (dependency: FindObjectsByType, GetInstanceID)
        private static GameObject FindObjectByUID(int uid)
        {
            GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject obj in allObjects)
            {
                if (obj.GetInstanceID() == uid)
                {
                    return obj;
                }
            }
            return null;
        }
    }
}
