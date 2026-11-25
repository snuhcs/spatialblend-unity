// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SpatialBlend
{

    [Serializable]
    public struct SerializationBlock
    {
        public SerializationBlock(GetLength getLength, Serialize serialize, Deserialize deserialize)
        {
            this.getLength = getLength;
            this.serialize = serialize;
            this.deserialize = deserialize;
        }

        public delegate ulong GetLength();
        public GetLength getLength;

        public delegate void Serialize(IntPtr data);
        public Serialize serialize;

        public delegate void Deserialize(IntPtr data, ulong length);
        public Deserialize deserialize;
    }

    public struct SerializationData
    {
        public SerializationData(IntPtr dataPtr, ulong length, bool ownsData)
        {
            DataPtr = dataPtr;
            Length = length;
            OwnsData = ownsData;
            BlockLengths = new List<ulong>();
            BlockOffsets = new List<int>();
            SyncOffset = 0;
        }

        public static SerializationData Serialize(SbEntity entity)
        {
            // Calculate total length based on memory layout:
            // [EntityType (1 byte) | # of serialization blocks (1 byte) | { Num bytes per block (4 byte) | {Actual serialized data} | ... } | {Entity type-specific data}]
            ulong totalLength = sizeof(byte) + sizeof(byte); // EntityType + num blocks
            totalLength += (ulong)(sizeof(int) * entity.serializationBlocks.Count); // Length of each block
            foreach (var block in entity.serializationBlocks)
            {
                totalLength += block.getLength(); // Actual serialized data
            }
            totalLength += entity.GetSyncSerializedLength(); // Entity type-specific data

            SerializationData data = new SerializationData(
                Marshal.AllocHGlobal((int)totalLength),
                totalLength,
                true
            );

            // Write entity type
            Marshal.WriteByte(data.DataPtr, 0, (byte)entity.EntityType);

            // Write number of serialization blocks
            Marshal.WriteByte(data.DataPtr, 1, (byte)entity.serializationBlocks.Count);

            // Write block lengths and calculate offsets
            int currentOffset = 2;
            foreach (var block in entity.serializationBlocks)
            {
                ulong blockLength = block.getLength();
                data.BlockLengths.Add(blockLength);
                Marshal.WriteInt32(data.DataPtr, currentOffset, (int)blockLength);
                currentOffset += sizeof(int);
            }

            // Calculate block offsets
            foreach (var block in entity.serializationBlocks)
            {
                data.BlockOffsets.Add(currentOffset);
                block.serialize(IntPtr.Add(data.DataPtr, currentOffset));
                currentOffset += (int)block.getLength();
            }

            data.SyncOffset = currentOffset;
            entity.PrepareSync(IntPtr.Add(data.DataPtr, currentOffset));
            return data;
        }

        public static SerializationData Read(IntPtr data, ulong length)
        {
            SerializationData serializationData = new SerializationData(data, length, false);

            int numBlocks = (int)Marshal.ReadByte(IntPtr.Add(data, 1));
            int currentOffset = 2;

            for (int i = 0; i < numBlocks; i++)
            {
                ulong blockLength = (ulong)Marshal.ReadInt32(IntPtr.Add(data, currentOffset));
                serializationData.BlockLengths.Add(blockLength);
                currentOffset += sizeof(int);
            }

            for (int i = 0; i < numBlocks; i++)
            {
                serializationData.BlockOffsets.Add(currentOffset);
                currentOffset += (int)serializationData.BlockLengths[i];
            }

            serializationData.SyncOffset = currentOffset;
            return serializationData;
        }

        public EntityType GetEntityType()
        {
            return (EntityType)Marshal.ReadByte(DataPtr);
        }


        public int GetNumBlocks()
        {
            return Marshal.ReadByte(IntPtr.Add(DataPtr, 1));
        }

        public ulong GetBlockLength(int index)
        {
            if (index < 0 || index >= BlockLengths.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");
            }
            return BlockLengths[index];
        }

        public IntPtr GetBlockData(int index)
        {
            if (index < 0 || index >= BlockLengths.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");
            }
            return IntPtr.Add(DataPtr, BlockOffsets[index]);
        }

        public IntPtr GetSyncData()
        {
            return IntPtr.Add(DataPtr, SyncOffset);
        }

        public ulong GetSyncDataLength()
        {
            return Length - (ulong)SyncOffset;
        }


        public void Free()
        {
            if (DataPtr != IntPtr.Zero && OwnsData)
            {
                Marshal.FreeHGlobal(DataPtr);
                DataPtr = IntPtr.Zero;
            }
        }

        public string ToString(string prefix)
        {
            string result = $"{prefix} SerializationData\n";
            result += $"\tLength = {Length}, OwnsData = {OwnsData}\n";
            result += $"\tEntityType: {GetEntityType()}\n";
            result += $"\tSyncData Length: {GetSyncDataLength()}\n";
            result += $"\tNumBlocks: {GetNumBlocks()}\n";
            for (int i = 0; i < GetNumBlocks(); i++)
            {
                result += $"\t\tBlock {i}: Length = {GetBlockLength(i)}, Offset = {BlockLengths[i]}\n";
            }
            return result;
        }

        public IntPtr DataPtr;
        public ulong Length;

        public bool OwnsData;

        public List<ulong> BlockLengths;
        public List<int> BlockOffsets;
        public int SyncOffset;
    }

}
