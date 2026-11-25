// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using SpatialBlend;
using UnityMainThreadDispatcher = SpatialBlend.Internal.UnityMainThreadDispatcher;

namespace SpatialBlend.Samples.Objects
{
    public static class ObjectSerializer
    {
        public const int HEADER_ID_CONTENT = 777; // for further block identification
        private static bool _debugLog = false;

        public static SerializationBlock CreateContentBlock(SbNativeEntity entity)
        {
            byte[] contentBytes = null;
            int bytesLength = 0;
            ulong GetLength()
            {
                contentBytes = entity.GetContentData();
                bytesLength = contentBytes != null ? contentBytes.Length : 0;
                if (bytesLength == 0)
                {
                    return 0;
                }
                // [ID: 4] + [Size: 4] + [Data: N]
                return sizeof(int) + sizeof(int) + (ulong)bytesLength;
            }

            void Serialize(IntPtr data)
            {
                // flow: block.getLength() -> block.serialize()
                if (bytesLength == 0)
                {
                    contentBytes = entity.GetContentData();
                    bytesLength = contentBytes != null ? contentBytes.Length : 0;
                }
                int offset = 0;
                if (bytesLength == 0)
                {
                    return;
                }
                
                // 1. Header ID
                Marshal.WriteInt32(data, offset, HEADER_ID_CONTENT); offset += 4;
                
                // 2. Write file size and data
                Marshal.WriteInt32(data, offset, bytesLength); offset += 4;
                Marshal.Copy(contentBytes, 0, IntPtr.Add(data, offset), bytesLength);
            }

            void Deserialize(IntPtr data, ulong length)
            {
                int offset = 0;
                
                int headerId = Marshal.ReadInt32(data, offset); offset += 4;
                if (headerId != HEADER_ID_CONTENT)
                {
                    if (_debugLog) Debug.LogError($"[ContentBlock] ID Mismatch! Skipping.");
                    return;
                }

                int fileSize = Marshal.ReadInt32(data, offset); offset += 4;
                if (fileSize > 0)
                {
                    byte[] receivedBytes = new byte[fileSize];
                    Marshal.Copy(IntPtr.Add(data, offset), receivedBytes, 0, fileSize);
                    entity.SetContentData(receivedBytes);
                    entity.LoadContentAsync();
                }
            }
            return new SerializationBlock(GetLength, Serialize, Deserialize);
        }
    }
}