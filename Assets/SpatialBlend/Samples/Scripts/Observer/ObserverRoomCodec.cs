// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// Little-endian payload a participant sends on the Fusion session (key magic "OBS1", not a core-link key).
    /// The snapshot names its own participant: Fusion's reliable-data callback does not identify the sender.
    /// </summary>
    public static class ObserverRoomCodec
    {
        public const byte Version = 1;
        private const int MaxStringBytes = 512;
        /// <summary>ushort id, ushort label, three position floats, four rotation floats, three size floats.</summary>
        public const int MinFurnitureBytes = 44;
        /// <summary>ushort key, three position floats, four rotation floats.</summary>
        public const int MinObjectBytes = 30;
        /// <summary>two ushort strings, three position floats, four rotation floats, one has-head byte.</summary>
        public const int MinAvatarBytes = 33;

        public static byte[] Write(ParticipantRoomSnapshot snapshot)
        {
            if (snapshot == null) return null;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                WriteString(writer, snapshot.ParticipantId);
                WriteString(writer, snapshot.DisplayName);
                writer.Write(snapshot.SpaceId);
                WritePose(writer, snapshot.HasHead, snapshot.HeadPosition, snapshot.HeadRotation);
                writer.Write(snapshot.IncludesFurniture);
                if (snapshot.IncludesFurniture)
                {
                    writer.Write((ushort)snapshot.Furniture.Count);
                    for (int i = 0; i < snapshot.Furniture.Count; ++i)
                    {
                        ObserverFurniture item = snapshot.Furniture[i];
                        WriteString(writer, item.Id);
                        WriteString(writer, item.Label);
                        WriteVector(writer, item.Position);
                        WriteQuaternion(writer, item.Rotation);
                        WriteVector(writer, item.Size);
                    }
                }
                writer.Write((ushort)snapshot.Objects.Count);
                for (int i = 0; i < snapshot.Objects.Count; ++i)
                {
                    ObserverPlacedObject item = snapshot.Objects[i];
                    WriteString(writer, item.Key);
                    WriteVector(writer, item.Position);
                    WriteQuaternion(writer, item.Rotation);
                }
                writer.Write((ushort)snapshot.Avatars.Count);
                for (int i = 0; i < snapshot.Avatars.Count; ++i)
                {
                    ObserverAvatarPose item = snapshot.Avatars[i];
                    WriteString(writer, item.ParticipantId);
                    WriteString(writer, item.DisplayName);
                    WriteVector(writer, item.RootPosition);
                    WriteQuaternion(writer, item.RootRotation);
                    WritePose(writer, item.HasHead, item.HeadPosition, item.HeadRotation);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// A snapshot that fits <paramref name="maxBytes"/>. Furniture is dropped first. Objects, then avatars, are
        /// dropped from the end until the bytes fit. The result is what a throttled sender transmits; it does not
        /// keep a larger payload for later.
        /// </summary>
        public static byte[] WriteWithin(ParticipantRoomSnapshot snapshot, int maxBytes)
        {
            if (snapshot == null || maxBytes <= 0) return null;
            byte[] bytes = Write(snapshot);
            if (bytes == null || bytes.Length <= maxBytes) return bytes;
            if (snapshot.IncludesFurniture)
            {
                ParticipantRoomSnapshot motion = ParticipantRoomSnapshot.Create(snapshot.ParticipantId, snapshot.DisplayName,
                    snapshot.SpaceId, snapshot.HasHead, snapshot.HeadPosition, snapshot.HeadRotation,
                    null, snapshot.Objects, snapshot.Avatars, false);
                bytes = Write(motion);
                if (bytes != null && bytes.Length <= maxBytes) return bytes;
                snapshot = motion ?? snapshot;
            }
            return TrimToFit(snapshot, maxBytes);
        }

        private static byte[] TrimToFit(ParticipantRoomSnapshot snapshot, int maxBytes)
        {
            IReadOnlyList<ObserverPlacedObject> objects = snapshot.Objects;
            IReadOnlyList<ObserverAvatarPose> avatars = snapshot.Avatars;
            byte[] best = null;
            for (int objectCount = objects.Count; objectCount >= 0; --objectCount)
            {
                best = Write(Rebuild(snapshot, Slice(objects, objectCount), avatars));
                if (best != null && best.Length <= maxBytes) return best;
            }
            for (int avatarCount = avatars.Count - 1; avatarCount >= 0; --avatarCount)
            {
                best = Write(Rebuild(snapshot, Array.Empty<ObserverPlacedObject>(), Slice(avatars, avatarCount)));
                if (best != null && best.Length <= maxBytes) return best;
            }
            return best;
        }

        private static ParticipantRoomSnapshot Rebuild(ParticipantRoomSnapshot snapshot,
            IReadOnlyList<ObserverPlacedObject> objects, IReadOnlyList<ObserverAvatarPose> avatars) =>
            ParticipantRoomSnapshot.Create(snapshot.ParticipantId, snapshot.DisplayName, snapshot.SpaceId,
                snapshot.HasHead, snapshot.HeadPosition, snapshot.HeadRotation,
                snapshot.IncludesFurniture ? snapshot.Furniture : null, objects, avatars, snapshot.IncludesFurniture);

        private static T[] Slice<T>(IReadOnlyList<T> source, int count)
        {
            if (count <= 0 || source == null) return Array.Empty<T>();
            if (count >= source.Count && source is T[] array) return array;
            var copy = new T[Math.Min(count, source.Count)];
            for (int i = 0; i < copy.Length; ++i) copy[i] = source[i];
            return copy;
        }

        public static bool TryRead(byte[] bytes, out ParticipantRoomSnapshot snapshot) =>
            TryRead(bytes, 0, bytes != null ? bytes.Length : 0, out snapshot);

        public static bool TryRead(byte[] bytes, int offset, int count, out ParticipantRoomSnapshot snapshot)
        {
            snapshot = null;
            if (bytes == null || offset < 0 || count < 1 || offset + count > bytes.Length) return false;
            try
            {
                using (var stream = new MemoryStream(bytes, offset, count, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != Version) return false;
                    string participantId = ReadString(reader);
                    string displayName = ReadString(reader);
                    long spaceId = reader.ReadInt64();
                    ReadPose(reader, out bool hasHead, out Vector3 headPosition, out Quaternion headRotation);
                    bool includesFurniture = reader.ReadBoolean();
                    ObserverFurniture[] furniture;
                    if (!includesFurniture) furniture = Array.Empty<ObserverFurniture>();
                    else if (!TryReadCount(reader, stream, ParticipantRoomSnapshot.MaxFurniture, MinFurnitureBytes, out int furnitureCount)) return false;
                    else
                    {
                        furniture = new ObserverFurniture[furnitureCount];
                        for (int i = 0; i < furnitureCount; ++i)
                        {
                            string id = ReadString(reader);
                            string label = ReadString(reader);
                            Vector3 position = ReadVector(reader);
                            Quaternion rotation = ReadQuaternion(reader);
                            Vector3 size = ReadVector(reader);
                            furniture[i] = new ObserverFurniture(id, label, position, rotation, size);
                        }
                    }
                    if (!TryReadCount(reader, stream, ParticipantRoomSnapshot.MaxObjects, MinObjectBytes, out int objectCount)) return false;
                    var objects = new ObserverPlacedObject[objectCount];
                    for (int i = 0; i < objectCount; ++i)
                    {
                        string key = ReadString(reader);
                        objects[i] = new ObserverPlacedObject(key, ReadVector(reader), ReadQuaternion(reader));
                    }
                    if (!TryReadCount(reader, stream, ParticipantRoomSnapshot.MaxAvatars, MinAvatarBytes, out int avatarCount)) return false;
                    var avatars = new ObserverAvatarPose[avatarCount];
                    for (int i = 0; i < avatarCount; ++i)
                    {
                        string id = ReadString(reader);
                        string name = ReadString(reader);
                        Vector3 rootPosition = ReadVector(reader);
                        Quaternion rootRotation = ReadQuaternion(reader);
                        ReadPose(reader, out bool avatarHead, out Vector3 avatarHeadPosition, out Quaternion avatarHeadRotation);
                        avatars[i] = new ObserverAvatarPose(id, name, rootPosition, rootRotation, avatarHead, avatarHeadPosition, avatarHeadRotation);
                    }
                    if (stream.Position != stream.Length) return false;
                    snapshot = ParticipantRoomSnapshot.Create(participantId, displayName, spaceId, hasHead, headPosition, headRotation,
                        furniture, objects, avatars, includesFurniture);
                    return snapshot != null;
                }
            }
            catch (EndOfStreamException) { return false; }
            catch (IOException) { return false; }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (encoded.Length > MaxStringBytes) throw new InvalidOperationException("observer string is too long");
            writer.Write((ushort)encoded.Length);
            writer.Write(encoded);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > MaxStringBytes) throw new IOException("observer string is too long");
            if (length == 0) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static void WritePose(BinaryWriter writer, bool has, Vector3 position, Quaternion rotation)
        {
            writer.Write(has);
            if (!has) return;
            WriteVector(writer, position);
            WriteQuaternion(writer, rotation);
        }

        private static void ReadPose(BinaryReader reader, out bool has, out Vector3 position, out Quaternion rotation)
        {
            has = reader.ReadBoolean();
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!has) return;
            position = ReadVector(reader);
            rotation = ReadQuaternion(reader);
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector(BinaryReader reader) =>
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader reader) =>
            new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        /// <summary>
        /// The count is allocated only when it fits <paramref name="max"/> and the bytes still in
        /// <paramref name="stream"/> at <paramref name="minBytes"/> each. A larger count is refused.
        /// </summary>
        public static bool TryReadCount(BinaryReader reader, Stream stream, int max, int minBytes, out int count)
        {
            count = 0;
            int declared = reader.ReadUInt16();
            int remaining = stream == null ? 0 : (int)(stream.Length - stream.Position);
            if (remaining < 0) remaining = 0;
            int affordable = minBytes > 0 ? remaining / minBytes : 0;
            int limit = Math.Min(max, affordable);
            if (declared > limit) return false;
            count = declared;
            return true;
        }
    }
}
