// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// What a shared scene anchor says about itself on the wire. <see cref="SbMRUKAnchorEntity"/> carries its object name and its
    /// scene label, so the name carries the rest: <c>head@cx,cy,cz,sx,sy,sz[;frontDegrees]</c>.
    /// <list type="bullet">
    /// <item><c>head</c> is <c>LABEL#key#obj</c> for an anchor that is not a seat, or the seat identity (<see cref="SeatIdentity"/>,
    /// <c>COUCH#key[#front]</c>) for a seat. The <c>#obj</c> marker keeps a COUCH that has no measured plane (not a seat) from reading as one.</item>
    /// <item>The six numbers are the anchor's measured volume box in its own local frame (centre, then size), metres: exactly what
    /// <c>MRUKAnchor.VolumeBounds</c> gave the sharer. The receiver ranks same-label candidates by the sorted sizes and, when it has no anchor of the label,
    /// builds the box from all six.</item>
    /// <item><c>frontDegrees</c> is a seat's confirmed front in the anchor's local plane, present only once confirmed.</item>
    /// </list>
    /// The key is chosen by the sharer and names one anchor of one room (the anchor's platform id), so two rooms that each share a sofa
    /// never collide. The payload stays the one a table has, so no wire format changes.
    /// </summary>
    public static class SharedAnchorName
    {
        public const char KeySeparator = '#';
        public const char MeasureMarker = '@';
        public const char FrontMarker = ';';
        /// <summary>The desk rank an older sharer appended after the measure (<c>|n</c>); parsed and ignored.</summary>
        public const char LegacyRankMarker = '|';
        /// <summary>The third part of the head of an anchor that is not a seat.</summary>
        public const string ObjectMarker = "obj";

        /// <summary>A key is a non-empty name without the separators of the wire name.</summary>
        public static bool IsValidKey(string key) =>
            !string.IsNullOrWhiteSpace(key) && key.Trim().Length == key.Length &&
            key.IndexOf(KeySeparator) < 0 && key.IndexOf(MeasureMarker) < 0 && key.IndexOf(FrontMarker) < 0;

        /// <summary>The head of a non-seat anchor.</summary>
        public static string Head(string label, string key)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("An anchor has a label.", nameof(label));
            if (!IsValidKey(key)) throw new ArgumentException("A share key is a non-empty name without '#', '@' or ';'.", nameof(key));
            return label + KeySeparator + key + KeySeparator + ObjectMarker;
        }

        /// <summary>The shared identity of a non-seat anchor: the table's stable id rule applied to the label and the key.</summary>
        public static long SharedId(string label, string key) => SpatialBlendManager.SceneAnchorSharedId(Head(label, key));

        public static string Compose(string head, Vector3 center, Vector3 size, float? frontDegrees)
        {
            string name = head + MeasureMarker + Number(center.x) + "," + Number(center.y) + "," + Number(center.z) + "," +
                          Number(size.x) + "," + Number(size.y) + "," + Number(size.z);
            return frontDegrees.HasValue ? name + FrontMarker + Number(frontDegrees.Value) : name;
        }

        /// <summary>Splits a wire name into its head, the measured box and the confirmed front. False when it has no measure part.</summary>
        public static bool TryParse(string name, out string head, out Vector3 center, out Vector3 size, out float? frontDegrees)
        {
            head = name;
            center = size = default;
            frontDegrees = null;
            if (string.IsNullOrEmpty(name)) return false;
            int at = name.IndexOf(MeasureMarker);
            if (at <= 0) return false;
            head = name.Substring(0, at);
            string tail = name.Substring(at + 1);
            // An older sharer appended a desk rank ("|n"): it still parses, and the rank is ignored.
            int bar = tail.LastIndexOf(LegacyRankMarker);
            if (bar >= 0)
            {
                if (!int.TryParse(tail.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rank) || rank < 0)
                    return false;
                tail = tail.Substring(0, bar);
            }
            int semicolon = tail.IndexOf(FrontMarker);
            if (semicolon >= 0)
            {
                if (!TryNumber(tail.Substring(semicolon + 1), out float front)) return false;
                frontDegrees = front;
                tail = tail.Substring(0, semicolon);
            }
            string[] parts = tail.Split(',');
            if (parts.Length != 6) return false;
            var values = new float[6];
            for (int i = 0; i < 6; ++i) if (!TryNumber(parts[i], out values[i])) return false;
            center = new Vector3(values[0], values[1], values[2]);
            size = new Vector3(values[3], values[4], values[5]);
            return size.x > 0f && size.y > 0f && size.z > 0f;
        }

        /// <summary>Reads the head of an anchor that is not a seat: its label and key.</summary>
        public static bool TryParseHead(string head, out string label, out string key)
        {
            label = key = null;
            if (string.IsNullOrEmpty(head)) return false;
            string[] parts = head.Split(KeySeparator);
            if (parts.Length != 3 || parts[0].Length == 0 || !IsValidKey(parts[1]) || parts[2] != ObjectMarker) return false;
            label = parts[0];
            key = parts[1];
            return true;
        }

        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static bool TryNumber(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
