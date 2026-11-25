// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Which MRUK room the app treats as the current one. <c>MRUK.GetCurrentRoom()</c> is not that on its own: when the
    /// headset is inside no room it returns the last room it was in, and failing that the first room of the list, and the
    /// device keeps every room the user ever scanned loaded. Anything keyed by "the current room" (kept scans, the
    /// scene-anchor catalog, seat links) would then attach to an old or unrelated space.
    ///
    /// A room is accepted when it has a device UUID and the headset is inside it. The last accepted room is held while
    /// the headset is briefly outside it, until MRUK unloads it. A room that is not loaded from the device (an editor prefab
    /// or JSON room, which MRUK gives a random UUID) is not a space with an identity: it is taken as <c>GetCurrentRoom()</c>
    /// reports it and has no key.
    /// </summary>
    public static class MrukRoomTracker
    {
        private static MRUKRoom _held;
        private static Transform _eye;
        private static string _loggedAmbiguity = string.Empty;

        /// <summary>The room to use now, or null when MRUK has not confirmed one.</summary>
        public static MRUKRoom Current(MRUK mruk)
        {
            if (mruk == null) { _held = null; return null; }
            MRUKRoom candidate = mruk.GetCurrentRoom();
            // Only a room loaded from this device is a space with an identity; an editor prefab or JSON room is taken as reported.
            string key = candidate != null && candidate.IsLocal ? MrukSceneAnchorCatalog.RoomUuid(candidate) : string.Empty;
            bool heldLoaded = _held != null && mruk.Rooms != null && mruk.Rooms.Contains(_held);
            bool inside = candidate != null && key.Length > 0 && HeadsetInside(candidate);
            MRUKRoom pick = Pick(candidate, key, inside, _held, heldLoaded);
            if (pick != null && pick.IsLocal && MrukSceneAnchorCatalog.RoomUuid(pick).Length > 0) _held = pick; else if (!heldLoaded) _held = null;
            if (pick != null && ReferenceEquals(pick, candidate)) WarnWhenAmbiguous(mruk, pick);
            return pick;
        }

        /// <summary>The UUID of <see cref="Current"/>, or empty.</summary>
        public static string CurrentKey(MRUK mruk)
        {
            // Only a room loaded from the device is a space with an identity. MRUK gives a prefab or JSON room a fresh random
            // UUID on every load (the fallback room of an unscanned headset), which no later session could ever match.
            MRUKRoom room = Current(mruk);
            return room != null && room.IsLocal ? MrukSceneAnchorCatalog.RoomUuid(room) : string.Empty;
        }

        /// <summary>Whether a room with this UUID is loaded in MRUK now.</summary>
        public static bool IsLoaded(MRUK mruk, string roomKey)
        {
            if (mruk == null || mruk.Rooms == null || string.IsNullOrEmpty(roomKey)) return false;
            foreach (MRUKRoom room in mruk.Rooms)
            {
                if (room != null && string.Equals(MrukSceneAnchorCatalog.RoomUuid(room), roomKey, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// The pure rule. <paramref name="candidate"/> is what MRUK reports as current. A room with a UUID is taken only when
        /// the headset is inside it; otherwise the held room stands while it is still loaded, else there is none.
        /// </summary>
        internal static T Pick<T>(T candidate, string candidateKey, bool headsetInside, T held, bool heldStillLoaded) where T : class
        {
            if (candidate != null && string.IsNullOrEmpty(candidateKey)) return candidate;
            if (candidate != null && headsetInside) return candidate;
            return held != null && heldStillLoaded ? held : null;
        }

        internal static void ResetForTests() { _held = null; _eye = null; _loggedAmbiguity = string.Empty; }

        private static bool HeadsetInside(MRUKRoom room)
        {
            Transform eye = Eye();
            return eye != null && room.IsPositionInRoom(eye.position, false);
        }

        private static Transform Eye()
        {
            if (_eye != null) return _eye;
            OVRCameraRig rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null) _eye = rig.centerEyeAnchor;
            else if (Camera.main != null) _eye = Camera.main.transform;
            return _eye;
        }

        // MRUK picks the first local room that contains the eye. Two overlapping rooms on the device (an old scan and its
        // re-scan) are indistinguishable to the app, so say so once instead of silently choosing.
        private static void WarnWhenAmbiguous(MRUK mruk, MRUKRoom chosen)
        {
            Transform eye = Eye();
            if (eye == null || mruk.Rooms == null) return;
            var keys = new List<string>();
            foreach (MRUKRoom room in mruk.Rooms)
            {
                if (room != null && room.IsPositionInRoom(eye.position, false)) keys.Add(MrukSceneAnchorCatalog.RoomUuid(room));
            }
            if (keys.Count < 2) { _loggedAmbiguity = string.Empty; return; }
            string joined = string.Join(",", keys);
            if (joined == _loggedAmbiguity) return;
            _loggedAmbiguity = joined;
            Debug.LogWarning($"[Room] {keys.Count} MRUK rooms contain the headset ({joined}); using {MrukSceneAnchorCatalog.RoomUuid(chosen)}. " +
                             "Delete the old scan in Space Setup if the wrong room is used.");
        }
    }
}
