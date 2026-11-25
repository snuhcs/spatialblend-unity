// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Where a user's seat choices are kept between sessions: the confirmed front of a seat, keyed by the MRUK anchor's own stable id.
    /// A user confirms a front once. (Which seats are shared is chosen again each session; nothing about sharing is remembered.)
    /// </summary>
    public interface ISeatPreferences
    {
        bool TryGetFront(string anchorId, out float frontDegrees);
        void SetFront(string anchorId, float frontDegrees);
    }

    /// <summary>PlayerPrefs-backed preferences. Every access is guarded: a device without them behaves as one that remembers nothing.</summary>
    public sealed class PlayerPrefsSeatPreferences : ISeatPreferences
    {
        private const string Prefix = "spatialblend.seat.";

        /// <summary>
        /// The MRUK room UUID a confirmed front is remembered in. A front is a fact about one scan of one room, so it is
        /// not carried into another room even if the platform reuses an anchor id. Empty means unscoped (tests, no room).
        /// </summary>
        public string RoomScope { get; set; } = string.Empty;

        private string FrontName(string anchorId) =>
            Prefix + "front." + (string.IsNullOrEmpty(RoomScope) ? string.Empty : RoomScope + ".") + anchorId;

        public bool TryGetFront(string anchorId, out float frontDegrees)
        {
            frontDegrees = 0f;
            if (string.IsNullOrEmpty(anchorId)) return false;
            try
            {
                string name = FrontName(anchorId);
                if (!PlayerPrefs.HasKey(name)) return false;
                frontDegrees = PlayerPrefs.GetFloat(name);
                return !float.IsNaN(frontDegrees) && !float.IsInfinity(frontDegrees);
            }
            catch (Exception) { return false; }
        }

        public void SetFront(string anchorId, float frontDegrees)
        {
            if (string.IsNullOrEmpty(anchorId)) return;
            try { PlayerPrefs.SetFloat(FrontName(anchorId), frontDegrees); PlayerPrefs.Save(); }
            catch (Exception) { }
        }
    }

    /// <summary>Preferences that remember nothing beyond the session (tests, and a device whose store is unavailable).</summary>
    public sealed class MemorySeatPreferences : ISeatPreferences
    {
        private readonly Dictionary<string, float> _fronts = new Dictionary<string, float>();
        public bool TryGetFront(string anchorId, out float frontDegrees) => _fronts.TryGetValue(anchorId ?? string.Empty, out frontDegrees);
        public void SetFront(string anchorId, float frontDegrees) { if (!string.IsNullOrEmpty(anchorId)) _fronts[anchorId] = frontDegrees; }
    }

    /// <summary>
    /// The seats this peer has registered as shared <c>Sittable</c> interaction entities, by the link key the user gave them, and
    /// what is known of each pair once it is linked. A seat transfers sitting only when it is linked (design section 2.0): the pair
    /// is the user's choice at share time, as the table's is, and the receiver never searches the room for a chair.
    /// </summary>
    public sealed class SeatLinkBook
    {
        private readonly Dictionary<string, GameObject> _local = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<long, SeatPair> _pairs = new Dictionary<long, SeatPair>();

        /// <summary>One linked pair, as this peer knows it: whether both fronts were confirmed.</summary>
        public readonly struct SeatPair
        {
            public readonly string Key;
            public readonly bool LocalFrontConfirmed;
            public readonly bool SourceFrontConfirmed;
            public SeatPair(string key, bool local, bool source) { Key = key; LocalFrontConfirmed = local; SourceFrontConfirmed = source; }
            /// <summary>A pelvis yaw measured from a seat's front is meaningful only if the front is confirmed on both seats.</summary>
            public bool FrontConfirmedOnBoth => LocalFrontConfirmed && SourceFrontConfirmed;
        }

        public IReadOnlyCollection<string> LocalKeys => _local.Keys;

        public bool TryGetLocal(string key, out GameObject seat)
        {
            seat = null;
            if (key == null || !_local.TryGetValue(key, out GameObject found) || found == null) return false;
            seat = found;
            return true;
        }

        /// <summary>Registers a local seat under a key. A key names one seat: giving it to another seat takes it from the first.</summary>
        public void SetLocal(string key, GameObject seat)
        {
            if (!SeatIdentity.IsValidKey(key) || seat == null) return;
            foreach (string existing in new List<string>(_local.Keys))
            {
                if (_local[existing] == seat) _local.Remove(existing);
            }
            _local[key] = seat;
        }

        public void RemoveLocal(GameObject seat)
        {
            foreach (string existing in new List<string>(_local.Keys))
            {
                if (_local[existing] == seat) _local.Remove(existing);
            }
        }

        /// <summary>Records a link the core made: the shared id of the pair and what is known of both fronts.</summary>
        public void RecordLink(long pairSharedId, string key, bool localFrontConfirmed, bool sourceFrontConfirmed) =>
            _pairs[pairSharedId] = new SeatPair(key, localFrontConfirmed, sourceFrontConfirmed);

        /// <summary>Forgets a pair (the other side unshared its seat).</summary>
        public void RemovePair(long pairSharedId) => _pairs.Remove(pairSharedId);

        public void Clear()
        {
            _local.Clear();
            _pairs.Clear();
        }

        private string _roomKey = string.Empty;

        /// <summary>The MRUK room UUID the links in this book were made in. Empty while no room is confirmed.</summary>
        public string RoomKey => _roomKey;

        /// <summary>
        /// Scopes the book to a room. A seat and its link belong to the room they were made in, so a different room
        /// (a re-scan, another space) starts with no links. Returns true when the book was reset.
        /// </summary>
        public bool EnterRoom(string roomKey)
        {
            roomKey = roomKey ?? string.Empty;
            if (string.Equals(roomKey, _roomKey, StringComparison.Ordinal)) return false;
            _roomKey = roomKey;
            Clear();
            return true;
        }

        /// <summary>The pair a shared id belongs to: the pair's object id, or any element id derived from it (region, affordance).</summary>
        public bool TryGetPair(long sharedId, out SeatPair pair)
        {
            if (_pairs.TryGetValue(sharedId, out pair)) return true;
            // Region and affordance ids derive from the object's: id * 64 + n, with 0 <= n < 64.
            long objectId = sharedId / 64L;
            return _pairs.TryGetValue(objectId, out pair);
        }

        /// <summary>
        /// Whether a seat the core proposed a pelvis for has its front confirmed on both sides. An unknown seat is not: its yaw is
        /// the Facing yaw's to decide.
        /// </summary>
        public bool FrontConfirmed(long referentSharedId) =>
            TryGetPair(referentSharedId, out SeatPair pair) && pair.FrontConfirmedOnBoth;
    }
}
