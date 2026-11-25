// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Which furniture the host shares. A desk is an MRUK anchor the platform classified as TABLE (a semantic label of the scene
    /// model, not app behaviour); the single largest by top-surface area starts marked, and the user changes that on the object
    /// itself ("Share when connected"). Unmarked furniture is a local obstacle. The host shares its marked items largest first
    /// (<see cref="CompareDesks"/>), and a joiner links each to its own largest free one of the same label.
    /// </summary>
    public static class FurnitureSharePolicy
    {
        /// <summary>The platform's semantic label of a desk.</summary>
        public const string DeskLabel = "TABLE";

        /// <summary>How many of the largest desks start marked: the single biggest TABLE (as v1 shared only the biggest).</summary>
        public const int DefaultDeskCount = 1;

        /// <summary>
        /// The tabletop. An MRUK volume has local +Z up and hangs below its top face, so the top is local X by Y.
        /// </summary>
        public static float TopSurfaceArea(Vector3 localSize) => Mathf.Abs(localSize.x * localSize.y);

        public static bool IsDesk(string label) => string.Equals(label, DeskLabel, StringComparison.Ordinal);

        public static bool IsDesk(SceneAnchorRecord record) => record != null && record.Anchor != null && IsDesk(record.Label);

        public static bool IsDesk(SbMRUKAnchorEntity entity) => entity != null && IsDesk(entity.SceneLabel);

        /// <summary>
        /// Size order: largest top first. A tie is broken by the platform anchor id, then by the source key, so the order is
        /// the same on every call and on every device that holds the same room data.
        /// </summary>
        public static int CompareDesks(SceneAnchorRecord a, SceneAnchorRecord b)
        {
            int area = TopSurfaceArea(b.LocalSize).CompareTo(TopSurfaceArea(a.LocalSize));
            if (area != 0) return area;
            int id = string.CompareOrdinal(a.AnchorId ?? string.Empty, b.AnchorId ?? string.Empty);
            return id != 0 ? id : a.SourceKey.CompareTo(b.SourceKey);
        }

        public static bool WillShare(SceneAnchorRecord record)
        {
            if (record == null || record.Anchor == null) return false;
            FurnitureShareMark mark = record.Anchor.GetComponent<FurnitureShareMark>();
            return mark != null && mark.ShareWhenConnected;
        }

        /// <summary>The user's choice on this anchor. A later default pass leaves it alone.</summary>
        public static void SetShare(SceneAnchorRecord record, bool share)
        {
            if (record == null || record.Anchor == null) return;
            FurnitureShareMark mark = record.Anchor.GetComponent<FurnitureShareMark>();
            if (mark == null) mark = record.Anchor.AddComponent<FurnitureShareMark>();
            mark.UserChose = true;
            mark.ShareWhenConnected = share;
        }

        /// <summary>Marks the largest desk (DefaultDeskCount) that the user has not already chosen. Other furniture stays off.</summary>
        public static void ApplyDefaults(IReadOnlyList<SceneAnchorRecord> shareable)
        {
            if (shareable == null) return;
            var desks = new List<SceneAnchorRecord>();
            var seen = new HashSet<GameObject>();
            for (int i = 0; i < shareable.Count; ++i)
            {
                SceneAnchorRecord record = shareable[i];
                if (IsDesk(record) && seen.Add(record.Anchor)) desks.Add(record);
            }
            desks.Sort(CompareDesks);
            for (int i = 0; i < desks.Count; ++i)
            {
                FurnitureShareMark mark = desks[i].Anchor.GetComponent<FurnitureShareMark>();
                if (mark != null && mark.UserChose) continue;
                if (mark == null) mark = desks[i].Anchor.AddComponent<FurnitureShareMark>();
                mark.ShareWhenConnected = i < DefaultDeskCount;
            }
        }
    }
}
