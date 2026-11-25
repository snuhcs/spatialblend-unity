// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// The lobby is Editing: that is where a scan is made, saved, and a virtual object is moved, and there is no chip to switch
    /// with. A room starts in Interaction. Coming back to the lobby puts Editing back, once per stay, so a refresh while connecting
    /// does not undo a mode the lobby tutorial set itself.
    /// </summary>
    public sealed class RoomModeReset
    {
        /// <summary>True until the mode has been put back since the last room (or since the lobby first showed).</summary>
        public bool Pending { get; private set; } = true;

        /// <summary>A room became ready: the user may change the mode in it, and the lobby must undo that when it comes back.</summary>
        public void EnteredRoom() => Pending = true;

        /// <summary>
        /// Puts <paramref name="manager"/> in Editing if a reset is pending, and returns whether it was pending. A null manager
        /// (an app without modes) still consumes the reset, so nothing is applied late.
        /// </summary>
        public bool Apply(InteractionModeManager manager)
        {
            if (!Pending) return false;
            Pending = false;
            if (manager != null) manager.SetMode(InteractionMode.SceneEdit);
            return true;
        }
    }
}
