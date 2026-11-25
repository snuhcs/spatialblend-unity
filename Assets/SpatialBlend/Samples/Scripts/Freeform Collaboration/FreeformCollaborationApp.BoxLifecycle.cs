// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Interactions;
using UnityEngine;

namespace SpatialBlend.Samples
{
    /// <summary>
    /// The life of the boxes the other user's shared objects became (<see cref="SharedRoomObjectBox"/>), beyond placing them:
    /// a box destroyed by any route leaves <see cref="SharedBoxes"/>, and an unplaced box keeps the idle return of the Editing mode
    /// from switching the user away while they are still deciding where it goes.
    /// </summary>
    public partial class FreeformCollaborationApp
    {
        private Func<bool> _boxBusyProbe;

        /// <summary>True while any box the other user shared is here and not yet placed. Editing is busy then (the assist's busy probe).</summary>
        public bool HasUnplacedSharedBox()
        {
            foreach (SharedRoomObjectBox box in _boxes.Values)
                if (box != null && !box.IsPlaced) return true;
            return false;
        }

        private IReadOnlyCollection<SharedRoomObjectBox> LiveSharedBoxes()
        {
            var live = new List<SharedRoomObjectBox>(_boxes.Count);
            foreach (SharedRoomObjectBox box in _boxes.Values)
                if (box != null) live.Add(box);
            return live;
        }

        private void EnsureBoxBusyProbe()
        {
            if (_boxBusyProbe != null) return;
            // A probe of a destroyed app answers false, so a stale one never holds Editing.
            _boxBusyProbe = () => this != null && HasUnplacedSharedBox();
            ModeSwitchAssist.AddBusyProbe(_boxBusyProbe);
        }

        private void RemoveBoxBusyProbe()
        {
            if (_boxBusyProbe == null) return;
            ModeSwitchAssist.RemoveBusyProbe(_boxBusyProbe);
            _boxBusyProbe = null;
        }

        // A box destroyed outside the app's own release (the generic object menu's Delete, ManipulationContext.ExecuteDeleteMode) must not
        // leave its entry, or its seat pair, behind until the next remove or leave.
        private void OnBoxDestroyed(SharedRoomObjectBox box)
        {
            if (this == null) return;
            GameObject key = null;
            bool found = false;
            foreach (KeyValuePair<GameObject, SharedRoomObjectBox> pair in _boxes)
            {
                if (!ReferenceEquals(pair.Value, box)) continue;
                key = pair.Key;
                found = true;
                break;
            }
            if (!found) return;   // the app released it itself, or is clearing the room
            _boxes.Remove(key);
            if (!string.IsNullOrEmpty(box.SeatKey)) _seatLinks.RemovePair(SeatIdentity.SharedId(box.SeatKey));
        }
    }
}
