// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Objects
{
    /// <summary>
    /// Serialization helpers for <see cref="SbNativeEntity"/> content blocks.
    /// Runtime copy of the Samples implementation (kept in SpatialBlend assembly).
    /// </summary>
    public static class ObjectSerializer
    {
        public const int HEADER_ID_CONTENT = 777;
        public const int HEADER_ID_BOX_REGION = 778;
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
                Marshal.WriteInt32(data, offset, HEADER_ID_CONTENT);
                offset += sizeof(int);

                // 2. Write file size and data
                Marshal.WriteInt32(data, offset, bytesLength);
                offset += sizeof(int);
                Marshal.Copy(contentBytes, 0, IntPtr.Add(data, offset), bytesLength);
            }

            void Deserialize(IntPtr data, ulong length)
            {
                int offset = 0;

                int headerId = Marshal.ReadInt32(data, offset);
                offset += sizeof(int);
                if (headerId != HEADER_ID_CONTENT)
                {
                    if (_debugLog) Debug.LogError("[ContentBlock] ID mismatch. Skipping.");
                    return;
                }

                int fileSize = Marshal.ReadInt32(data, offset);
                offset += sizeof(int);
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

        /// <summary>
        /// The annotated box a native object was scanned from: [ID: 4] + [Size: 4] + the
        /// <see cref="ScanBoxRegion"/> wire form. Zero length when the object has no box. A payload that does
        /// not decode is ignored, so a bad peer cannot write a declaration.
        /// </summary>
        public static SerializationBlock CreateBoxRegionBlock(SbNativeEntity entity)
        {
            byte[] encoded = null;

            ulong GetLength()
            {
                ScanBoxRegion region = entity.AuthoredBox;
                encoded = region == null ? null : region.Encode();
                return encoded == null ? 0UL : (ulong)(sizeof(int) + sizeof(int) + encoded.Length);
            }

            void Serialize(IntPtr data)
            {
                if (encoded == null) GetLength();
                if (encoded == null) return;
                Marshal.WriteInt32(data, 0, HEADER_ID_BOX_REGION);
                Marshal.WriteInt32(data, sizeof(int), encoded.Length);
                Marshal.Copy(encoded, 0, IntPtr.Add(data, 2 * sizeof(int)), encoded.Length);
            }

            void Deserialize(IntPtr data, ulong length)
            {
                if (length < 2 * sizeof(int)) return;
                if (Marshal.ReadInt32(data, 0) != HEADER_ID_BOX_REGION) return;
                int size = Marshal.ReadInt32(data, sizeof(int));
                if (size <= 0 || (ulong)size != length - 2 * sizeof(int)) return;
                byte[] received = new byte[size];
                Marshal.Copy(IntPtr.Add(data, 2 * sizeof(int)), received, 0, size);
                if (ScanBoxRegion.TryDecode(received, out ScanBoxRegion region))
                {
                    entity.SetAuthoredBox(region);
                }
            }

            return new SerializationBlock(GetLength, Serialize, Deserialize);
        }
    }
}


