// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>How an observer's camera is driven. Follow reads a head pose inside the selected room.</summary>
    public enum ObserverCameraMode
    {
        FreeFly,
        FollowHead,
    }

    /// <summary>One piece of furniture as the participant has it in their room (an MRUK anchor's world pose and measured size).</summary>
    public readonly struct ObserverFurniture
    {
        public ObserverFurniture(string id, string label, Vector3 position, Quaternion rotation, Vector3 size)
        {
            Id = id ?? string.Empty;
            Label = label ?? string.Empty;
            Position = position;
            Rotation = rotation;
            Size = size;
        }

        public string Id { get; }
        public string Label { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Size { get; }
    }

    /// <summary>A placed virtual object (whiteboard, chess, scan) at its pose in one participant's room.</summary>
    public readonly struct ObserverPlacedObject
    {
        public ObserverPlacedObject(string key, Vector3 position, Quaternion rotation)
        {
            Key = key ?? string.Empty;
            Position = position;
            Rotation = rotation;
        }

        public string Key { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }

    /// <summary>One avatar as realized in a participant's room, including that avatar's head pose in that room.</summary>
    public readonly struct ObserverAvatarPose
    {
        public ObserverAvatarPose(string participantId, string displayName, Vector3 rootPosition, Quaternion rootRotation,
                                  bool hasHead, Vector3 headPosition, Quaternion headRotation)
        {
            ParticipantId = participantId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            RootPosition = rootPosition;
            RootRotation = rootRotation;
            HasHead = hasHead;
            HeadPosition = headPosition;
            HeadRotation = headRotation;
        }

        public string ParticipantId { get; }
        public string DisplayName { get; }
        public Vector3 RootPosition { get; }
        public Quaternion RootRotation { get; }
        public bool HasHead { get; }
        public Vector3 HeadPosition { get; }
        public Quaternion HeadRotation { get; }
    }

    /// <summary>
    /// One participant's virtual space, sampled on their machine: their SpatialBlend space id, the HMD pose in that
    /// room, furniture, placed objects, and every avatar as the core realized it there.
    /// </summary>
    public sealed class ParticipantRoomSnapshot
    {
        public const int MaxFurniture = 48;
        public const int MaxObjects = 64;
        public const int MaxAvatars = 16;
        public const int MaxText = 80;

        private ParticipantRoomSnapshot(string participantId, string displayName, long spaceId, bool hasHead,
                                        Vector3 headPosition, Quaternion headRotation, bool includesFurniture,
                                        ObserverFurniture[] furniture, ObserverPlacedObject[] objects, ObserverAvatarPose[] avatars)
        {
            ParticipantId = participantId;
            DisplayName = displayName;
            SpaceId = spaceId;
            HasHead = hasHead;
            HeadPosition = headPosition;
            HeadRotation = headRotation;
            IncludesFurniture = includesFurniture;
            Furniture = furniture;
            Objects = objects;
            Avatars = avatars;
        }

        public string ParticipantId { get; }
        public string DisplayName { get; }
        public long SpaceId { get; }
        public bool HasHead { get; }
        public Vector3 HeadPosition { get; }
        public Quaternion HeadRotation { get; }
        /// <summary>False on a motion update that left furniture out. The catalog keeps the last furniture sample.</summary>
        public bool IncludesFurniture { get; }
        public IReadOnlyList<ObserverFurniture> Furniture { get; }
        public IReadOnlyList<ObserverPlacedObject> Objects { get; }
        public IReadOnlyList<ObserverAvatarPose> Avatars { get; }

        public static ParticipantRoomSnapshot Create(string participantId, string displayName, long spaceId,
                                                     bool hasHead, Vector3 headPosition, Quaternion headRotation,
                                                     IReadOnlyList<ObserverFurniture> furniture,
                                                     IReadOnlyList<ObserverPlacedObject> objects,
                                                     IReadOnlyList<ObserverAvatarPose> avatars,
                                                     bool includesFurniture = true)
        {
            participantId = Clip(participantId);
            if (participantId.Length == 0) return null;
            if (hasHead && !FinitePose(headPosition, headRotation)) hasHead = false;
            if (!hasHead)
            {
                headPosition = Vector3.zero;
                headRotation = Quaternion.identity;
            }
            string name = Clip(displayName);
            return new ParticipantRoomSnapshot(participantId, name.Length == 0 ? "Participant " + participantId : name,
                spaceId, hasHead, headPosition, headRotation, includesFurniture,
                includesFurniture ? TakeFurniture(furniture) : Array.Empty<ObserverFurniture>(),
                TakeObjects(objects), TakeAvatars(avatars));
        }

        /// <summary>A motion update that omitted furniture, filled back in from the previous sample of this room.</summary>
        public ParticipantRoomSnapshot KeepingFurniture(IReadOnlyList<ObserverFurniture> furniture) =>
            Create(ParticipantId, DisplayName, SpaceId, HasHead, HeadPosition, HeadRotation, furniture, Objects, Avatars, true);

        /// <summary>
        /// The head to lock the camera to while viewing THIS room. An avatar entry is that person as realized here.
        /// The room owner's own HMD is the fallback when their avatar has not been sampled yet.
        /// </summary>
        public bool TryViewpoint(string participantId, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (string.IsNullOrEmpty(participantId)) return false;
            for (int i = 0; i < Avatars.Count; ++i)
            {
                ObserverAvatarPose avatar = Avatars[i];
                if (!string.Equals(avatar.ParticipantId, participantId, StringComparison.Ordinal) || !avatar.HasHead) continue;
                position = avatar.HeadPosition;
                rotation = avatar.HeadRotation;
                return true;
            }
            if (HasHead && string.Equals(participantId, ParticipantId, StringComparison.Ordinal))
            {
                position = HeadPosition;
                rotation = HeadRotation;
                return true;
            }
            return false;
        }

        public bool ContainsViewpoint(string participantId)
        {
            if (string.IsNullOrEmpty(participantId)) return false;
            if (string.Equals(participantId, ParticipantId, StringComparison.Ordinal)) return true;
            for (int i = 0; i < Avatars.Count; ++i)
            {
                if (string.Equals(Avatars[i].ParticipantId, participantId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static ObserverFurniture[] TakeFurniture(IReadOnlyList<ObserverFurniture> source)
        {
            var kept = new List<ObserverFurniture>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (source == null) return Array.Empty<ObserverFurniture>();
            for (int i = 0; i < source.Count && kept.Count < MaxFurniture; ++i)
            {
                ObserverFurniture item = source[i];
                string id = Clip(item.Id);
                if (id.Length == 0 || !FinitePose(item.Position, item.Rotation) || !Finite(item.Size) || !seen.Add(id)) continue;
                kept.Add(new ObserverFurniture(id, Clip(item.Label), item.Position, item.Rotation, item.Size));
            }
            return kept.ToArray();
        }

        private static ObserverPlacedObject[] TakeObjects(IReadOnlyList<ObserverPlacedObject> source)
        {
            var kept = new List<ObserverPlacedObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (source == null) return Array.Empty<ObserverPlacedObject>();
            for (int i = 0; i < source.Count && kept.Count < MaxObjects; ++i)
            {
                ObserverPlacedObject item = source[i];
                string key = Clip(item.Key);
                if (key.Length == 0 || !FinitePose(item.Position, item.Rotation) || !seen.Add(key)) continue;
                kept.Add(new ObserverPlacedObject(key, item.Position, item.Rotation));
            }
            return kept.ToArray();
        }

        private static ObserverAvatarPose[] TakeAvatars(IReadOnlyList<ObserverAvatarPose> source)
        {
            var kept = new List<ObserverAvatarPose>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (source == null) return Array.Empty<ObserverAvatarPose>();
            for (int i = 0; i < source.Count && kept.Count < MaxAvatars; ++i)
            {
                ObserverAvatarPose item = source[i];
                string id = Clip(item.ParticipantId);
                if (id.Length == 0 || !FinitePose(item.RootPosition, item.RootRotation) || !seen.Add(id)) continue;
                bool head = item.HasHead && FinitePose(item.HeadPosition, item.HeadRotation);
                kept.Add(new ObserverAvatarPose(id, Clip(item.DisplayName), item.RootPosition, item.RootRotation,
                    head, head ? item.HeadPosition : Vector3.zero, head ? item.HeadRotation : Quaternion.identity));
            }
            return kept.ToArray();
        }

        internal static string Clip(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            return trimmed.Length <= MaxText ? trimmed : trimmed.Substring(0, MaxText);
        }

        private static bool Finite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool FinitePose(Vector3 position, Quaternion rotation) =>
            Finite(position) && IsFinite(rotation.x) && IsFinite(rotation.y) && IsFinite(rotation.z) && IsFinite(rotation.w) &&
            rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > 1e-8f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// Keys that tie a snapshot back onto objects the session already replicates. A placed object's key is its Fusion
    /// network id. A participant's id is their Fusion player id. Furniture keeps the MRUK anchor id, because furniture
    /// is not a networked object: the observer draws it from the snapshot.
    /// </summary>
    public static class ObserverSceneMap
    {
        public const string NetworkKeyPrefix = "net:";
        public const string NativeKeyPrefix = "native:";

        public static string ParticipantId(int playerId) => playerId.ToString(CultureInfo.InvariantCulture);

        public static Vector3 FurnitureCenter(Vector3 anchorPosition, Quaternion anchorRotation, Vector3 localCenter) =>
            anchorPosition + anchorRotation * localCenter;

        public static string NetworkKey(uint raw) => NetworkKeyPrefix + raw.ToString(CultureInfo.InvariantCulture);

        public static string NativeKey(string objectName) => NativeKeyPrefix + (objectName ?? string.Empty).Trim();

        public static bool TryParseNetworkKey(string key, out uint raw)
        {
            raw = 0;
            if (string.IsNullOrEmpty(key) || !key.StartsWith(NetworkKeyPrefix, StringComparison.Ordinal)) return false;
            return uint.TryParse(key.Substring(NetworkKeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out raw);
        }
    }

    /// <summary>The rooms an observer can pick. One entry per participant who has published their space.</summary>
    public sealed class ObserverRoomCatalog
    {
        private readonly Dictionary<string, ParticipantRoomSnapshot> _rooms =
            new Dictionary<string, ParticipantRoomSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _seen = new Dictionary<string, double>(StringComparer.Ordinal);
        private ParticipantRoomSnapshot[] _sorted = Array.Empty<ParticipantRoomSnapshot>();

        public int ListRevision { get; private set; }
        public int PoseRevision { get; private set; }
        public string SelectedParticipantId { get; private set; }
        public ParticipantRoomSnapshot Selected =>
            SelectedParticipantId != null && _rooms.TryGetValue(SelectedParticipantId, out ParticipantRoomSnapshot room) ? room : null;

        public IReadOnlyList<ParticipantRoomSnapshot> Rooms => _sorted;

        public bool Upsert(ParticipantRoomSnapshot snapshot, double timeSeconds)
        {
            if (snapshot == null) return false;
            bool known = _rooms.TryGetValue(snapshot.ParticipantId, out ParticipantRoomSnapshot previous);
            if (known && !snapshot.IncludesFurniture)
                snapshot = snapshot.KeepingFurniture(previous.Furniture);
            _rooms[snapshot.ParticipantId] = snapshot;
            _seen[snapshot.ParticipantId] = timeSeconds;
            PoseRevision++;
            bool list = !known || previous.DisplayName != snapshot.DisplayName || previous.SpaceId != snapshot.SpaceId;
            if (SelectedParticipantId == null) { SelectedParticipantId = snapshot.ParticipantId; list = true; }
            if (list) ListRevision++;
            Rebuild();
            return true;
        }

        public bool Select(string participantId)
        {
            if (string.IsNullOrEmpty(participantId) || !_rooms.ContainsKey(participantId)) return false;
            if (SelectedParticipantId == participantId) return true;
            SelectedParticipantId = participantId;
            ListRevision++;
            return true;
        }

        public bool Remove(string participantId)
        {
            if (string.IsNullOrEmpty(participantId) || !_rooms.Remove(participantId)) return false;
            _seen.Remove(participantId);
            if (SelectedParticipantId == participantId) SelectedParticipantId = FirstId();
            ListRevision++;
            PoseRevision++;
            Rebuild();
            return true;
        }

        /// <summary>Drops rooms not updated since <paramref name="timeSeconds"/> minus <paramref name="maxAgeSeconds"/>.</summary>
        public int Expire(double timeSeconds, double maxAgeSeconds)
        {
            if (maxAgeSeconds < 0d) return 0;
            var stale = new List<string>();
            foreach (KeyValuePair<string, double> pair in _seen)
            {
                if (timeSeconds - pair.Value > maxAgeSeconds) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; ++i) Remove(stale[i]);
            return stale.Count;
        }

        private string FirstId()
        {
            Rebuild();
            return _sorted.Length == 0 ? null : _sorted[0].ParticipantId;
        }

        private void Rebuild()
        {
            var list = new List<ParticipantRoomSnapshot>(_rooms.Values);
            list.Sort((a, b) =>
            {
                int name = string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
                return name != 0 ? name : string.Compare(a.ParticipantId, b.ParticipantId, StringComparison.Ordinal);
            });
            _sorted = list.ToArray();
        }
    }

    /// <summary>WASD fly, mouse look, Q/E or Space/Ctrl vertical, and a speed modifier. No device reads.</summary>
    public static class ObserverFly
    {
        public const float BaseSpeed = 3f;
        public const float FastMultiplier = 3f;
        public const float LookSensitivity = 2f;
        public const float PitchLimit = 85f;

        public struct State
        {
            public Vector3 Position;
            public float Yaw;
            public float Pitch;
        }

        public struct Input
        {
            public bool Forward, Back, Left, Right, Up, Down, Fast;
            public float MouseX, MouseY;
            public float DeltaTime;
        }

        public static State Step(State state, Input input)
        {
            float dt = input.DeltaTime > 0f && IsFinite(input.DeltaTime) ? input.DeltaTime : 0f;
            float mouseX = IsFinite(input.MouseX) ? input.MouseX : 0f;
            float mouseY = IsFinite(input.MouseY) ? input.MouseY : 0f;
            state.Yaw += mouseX * LookSensitivity;
            state.Pitch = Mathf.Clamp(state.Pitch - mouseY * LookSensitivity, -PitchLimit, PitchLimit);
            Quaternion look = Quaternion.Euler(state.Pitch, state.Yaw, 0f);
            Vector3 move = Vector3.zero;
            if (input.Forward) move += look * Vector3.forward;
            if (input.Back) move -= look * Vector3.forward;
            if (input.Right) move += look * Vector3.right;
            if (input.Left) move -= look * Vector3.right;
            if (input.Up) move += Vector3.up;
            if (input.Down) move -= Vector3.up;
            if (move.sqrMagnitude > 1f) move.Normalize();
            float speed = BaseSpeed * (input.Fast ? FastMultiplier : 1f);
            state.Position += move * speed * dt;
            return state;
        }

        public static State FromPose(Vector3 position, Quaternion rotation)
        {
            Vector3 euler = rotation.eulerAngles;
            float pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            return new State
            {
                Position = position,
                Yaw = euler.y,
                Pitch = Mathf.Clamp(pitch, -PitchLimit, PitchLimit),
            };
        }

        public static Quaternion Look(State state) => Quaternion.Euler(state.Pitch, state.Yaw, 0f);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Player ids that have announced themselves as observers, and when that announcement was last seen.</summary>
    public sealed class ObserverAudience
    {
        public const double TimeoutSeconds = 3d;

        private readonly Dictionary<int, double> _seen = new Dictionary<int, double>();

        public int Count => _seen.Count;

        public bool Contains(int playerId) => _seen.ContainsKey(playerId);

        public void Note(int playerId, double timeSeconds) => _seen[playerId] = timeSeconds;

        public bool Forget(int playerId) => _seen.Remove(playerId);

        public int Expire(double timeSeconds, double maxAgeSeconds)
        {
            if (maxAgeSeconds < 0d) return 0;
            List<int> stale = null;
            foreach (KeyValuePair<int, double> pair in _seen)
            {
                if (timeSeconds - pair.Value > maxAgeSeconds) (stale ??= new List<int>()).Add(pair.Key);
            }
            if (stale == null) return 0;
            for (int i = 0; i < stale.Count; ++i) _seen.Remove(stale[i]);
            return stale.Count;
        }
    }

    /// <summary>
    /// How often a participant may put room bytes on the Fusion reliable channel, and the token bucket that keeps
    /// that channel free for the core link. Presence is separate and tiny; it is not charged here.
    /// </summary>
    public static class ObserverTraffic
    {
        public const float PresenceIntervalSeconds = 1f;
        public const float MotionIntervalSeconds = 0.5f;
        public const float FurnitureIntervalSeconds = 2f;
        public const int BytesPerSecond = 4096;
        public const int BudgetCapBytes = 8192;
        /// <summary>One message to one observer. At most the bucket cap, so a full bucket can always send it.</summary>
        public const int MaxSnapshotBytes = 4096;

        public static float Refill(float allowance, float deltaSeconds)
        {
            if (deltaSeconds < 0f || float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds)) return allowance;
            if (float.IsNaN(allowance) || float.IsInfinity(allowance) || allowance < 0f) allowance = 0f;
            allowance += deltaSeconds * BytesPerSecond;
            if (allowance > BudgetCapBytes) allowance = BudgetCapBytes;
            return allowance;
        }

        public static bool TrySpend(ref float allowance, int bytes)
        {
            if (bytes <= 0) return true;
            if (allowance < bytes) return false;
            allowance -= bytes;
            return true;
        }

        /// <summary>
        /// Spends <paramref name="payloadBytes"/> once per recipient, and stops when the bucket cannot pay the next one.
        /// A payload larger than <see cref="BudgetCapBytes"/> is refused: it could never be sent. The caller keeps
        /// capturing and passes the latest bytes; nothing here retains a previous payload.
        /// </summary>
        public static int SendPerRecipient(ref float allowance, int payloadBytes, int recipientCount)
        {
            int cursor = 0;
            return SendPerRecipient(ref allowance, payloadBytes, recipientCount, ref cursor, out _);
        }

        /// <summary>
        /// Same spend as <see cref="SendPerRecipient(ref float, int, int)"/>, walking recipients from <paramref name="cursor"/>.
        /// <paramref name="start"/> is the index of the first recipient this call would pay. When at least one is paid,
        /// <paramref name="cursor"/> advances by one so the next call starts at the next recipient.
        /// </summary>
        public static int SendPerRecipient(ref float allowance, int payloadBytes, int recipientCount, ref int cursor, out int start)
        {
            start = 0;
            if (payloadBytes <= 0 || payloadBytes > BudgetCapBytes || recipientCount <= 0) return 0;
            start = cursor % recipientCount;
            if (start < 0) start += recipientCount;
            int sent = 0;
            for (int i = 0; i < recipientCount; ++i)
            {
                if (!TrySpend(ref allowance, payloadBytes)) break;
                sent++;
            }
            cursor = sent > 0 ? (start + 1) % recipientCount : start;
            return sent;
        }
    }

    /// <summary>Room choice plus camera mode. Follow always reads the head inside the selected room.</summary>
    public sealed class ObserverSessionState
    {
        public ObserverRoomCatalog Rooms { get; } = new ObserverRoomCatalog();
        public ObserverCameraMode Mode { get; private set; } = ObserverCameraMode.FreeFly;
        public string FollowParticipantId { get; private set; }
        public ObserverFly.State Fly { get; private set; }

        public bool UpsertRoom(ParticipantRoomSnapshot snapshot, double timeSeconds)
        {
            if (!Rooms.Upsert(snapshot, timeSeconds)) return false;
            EnsureFollow();
            return true;
        }

        public int Expire(double timeSeconds, double maxAgeSeconds)
        {
            int removed = Rooms.Expire(timeSeconds, maxAgeSeconds);
            if (removed > 0) EnsureFollow();
            return removed;
        }

        public bool SelectRoom(string participantId)
        {
            if (!Rooms.Select(participantId)) return false;
            EnsureFollow();
            return true;
        }

        public bool SetMode(ObserverCameraMode mode)
        {
            if (Mode == mode) return false;
            if (mode == ObserverCameraMode.FreeFly &&
                TryCameraPose(out Vector3 position, out Quaternion rotation))
            {
                Fly = ObserverFly.FromPose(position, rotation);
            }
            Mode = mode;
            return true;
        }

        public bool SetFollow(string participantId)
        {
            ParticipantRoomSnapshot room = Rooms.Selected;
            if (room == null || !room.ContainsViewpoint(participantId)) return false;
            FollowParticipantId = participantId;
            return true;
        }

        public void StepFly(ObserverFly.Input input)
        {
            if (Mode != ObserverCameraMode.FreeFly) return;
            Fly = ObserverFly.Step(Fly, input);
        }

        public bool TryCameraPose(out Vector3 position, out Quaternion rotation)
        {
            position = Fly.Position;
            rotation = ObserverFly.Look(Fly);
            if (Mode == ObserverCameraMode.FreeFly) return true;
            ParticipantRoomSnapshot room = Rooms.Selected;
            if (room == null) return false;
            return room.TryViewpoint(FollowParticipantId, out position, out rotation);
        }

        private void EnsureFollow()
        {
            ParticipantRoomSnapshot room = Rooms.Selected;
            if (room == null)
            {
                FollowParticipantId = null;
                return;
            }
            if (string.IsNullOrEmpty(FollowParticipantId) || !room.ContainsViewpoint(FollowParticipantId))
                FollowParticipantId = room.ParticipantId;
        }
    }
}
