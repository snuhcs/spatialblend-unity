// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;

namespace SpatialBlend
{
    /// <summary>
    /// What a shared seat says about itself on the wire. <see cref="SbMRUKAnchorEntity"/> carries its object name and its scene
    /// label; a seat's name is <c>COUCH#key</c> (the user's link key, see <see cref="SceneAnchorSpatialAuthoring.ApplySeat"/>)
    /// followed by <c>#front</c> once its sharer confirmed the seat's front. The wire payload is the one a table has, so a peer that
    /// knows nothing of seats still parses it.
    /// </summary>
    public static class SeatIdentity
    {
        public const char Separator = '#';
        public const string FrontMarker = "front";

        public static bool IsValidKey(string key) => SharedAnchorName.IsValidKey(key);

        public static string Compose(string key, bool frontConfirmed)
        {
            if (!IsValidKey(key)) throw new ArgumentException("A seat link key is a non-empty name without '#'.", nameof(key));
            string name = SceneAnchorSpatialAuthoring.CouchLabel + Separator + key;
            return frontConfirmed ? name + Separator + FrontMarker : name;
        }

        public static bool TryParse(string objectName, out string key, out bool frontConfirmed)
        {
            key = null;
            frontConfirmed = false;
            if (string.IsNullOrEmpty(objectName)) return false;
            string[] parts = objectName.Split(Separator);
            if (parts.Length < 2 || parts.Length > 3 || parts[0] != SceneAnchorSpatialAuthoring.CouchLabel ||
                !IsValidKey(parts[1]))
            {
                return false;
            }
            if (parts.Length == 3)
            {
                if (parts[2] != FrontMarker) return false;
                frontConfirmed = true;
            }
            key = parts[1];
            return true;
        }

        /// <summary>The identity every device derives for a seat: the table's stable id rule, applied to the label and the link key.</summary>
        public static long SharedId(string key) =>
            SpatialBlendManager.SceneAnchorSharedId(SceneAnchorSpatialAuthoring.CouchLabel + Separator + key);
    }
}
