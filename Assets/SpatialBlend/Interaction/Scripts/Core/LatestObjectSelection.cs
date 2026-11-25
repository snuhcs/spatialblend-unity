// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Tracks the most recently selected PlacementMotif for global recenter operations.
    ///
    /// Note: This uses static state which can cause issues with multiple XR rigs or test scenes.
    /// Consider using a scoped singleton or dependency injection pattern for more complex scenarios.
    /// </summary>
    [MovedFrom("SpatialBlend.Samples.Objects")]
    public static class LatestObjectSelection
    {
        private static PlacementMotif s_current;

        /// <summary>
        /// Event fired when the selection changes.
        /// </summary>
        public static event Action<PlacementMotif> SelectionChanged;

        /// <summary>
        /// Gets the currently selected PlacementMotif, or null if none or destroyed.
        /// </summary>
        public static PlacementMotif Current
        {
            get
            {
                // Auto-clear if the object was destroyed
                if (s_current != null && s_current.Equals(null))
                {
                    s_current = null;
                }
                return s_current;
            }
        }

        /// <summary>
        /// Sets the current selection.
        /// </summary>
        public static void Set(PlacementMotif motif)
        {
            if (motif == null) return;
            if (s_current == motif) return;

            s_current = motif;
            SelectionChanged?.Invoke(motif);
        }

        /// <summary>
        /// Checks if the given motif is the current selection.
        /// </summary>
        public static bool IsCurrent(PlacementMotif motif)
        {
            return motif != null && motif == Current;
        }

        /// <summary>
        /// Clears the current selection.
        /// Useful for scene transitions or testing.
        /// </summary>
        public static void Clear()
        {
            if (s_current != null)
            {
                s_current = null;
                SelectionChanged?.Invoke(null);
            }
        }
    }
}
