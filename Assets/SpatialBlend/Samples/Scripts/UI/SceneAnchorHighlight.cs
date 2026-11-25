// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// Shows which scanned anchor a row of the room-object list stands for: the shared real-object box (<see cref="SharedAnchorBox"/>,
    /// violet) around the anchor's measured volume, the look it takes once shared. One highlight at a time; <see cref="Clear"/> removes it.
    /// </summary>
    public static class SceneAnchorHighlight
    {
        public const string ObjectName = "RoomShareHighlight";
        private static GameObject s_current;

        public static GameObject Current => s_current;

        private static Material s_lineMaterial;

        /// <summary>
        /// The one material every anchor outline draws with (Sprites/Default, vertex-coloured: each line carries its own colour), made
        /// once per session; an outline built again reuses it instead of making a material per edge.
        /// </summary>
        public static Material LineMaterial
        {
            get
            {
                if (s_lineMaterial != null) return s_lineMaterial;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) return null;
                s_lineMaterial = new Material(shader) { name = "AnchorOutline (shared)", hideFlags = HideFlags.DontSave };
                ++RuntimeWorkCounters.OutlineMaterialsCreated;
                return s_lineMaterial;
            }
        }

        public static void Show(SceneAnchorRecord record)
        {
            Clear();
            if (record == null || record.Anchor == null) return;
            s_current = Build(record.Anchor.transform, record.LocalCenter, record.LocalSize);
        }

        public static void Clear()
        {
            if (s_current != null)
            {
                if (Application.isPlaying) Object.Destroy(s_current);
                else Object.DestroyImmediate(s_current);
            }
            s_current = null;
        }

        /// <summary>
        /// The emphasis of a shared real-object box (<see cref="SharedAnchorBox.BuildEmphasis"/>) around an anchor's measured volume: the
        /// row's highlight, and the box a joiner has still to place. Over an anchor that already draws its calm box (a shared one), only the
        /// emphasis is added (brighter, thicker brackets and a stronger fill); over one that does not, the emphasis carries the edges.
        /// </summary>
        internal static GameObject Build(Transform parent, Vector3 center, Vector3 size)
        {
            ++RuntimeWorkCounters.AnchorOutlineBuilds;
            RuntimeWorkCounters.Log($"anchor outline built under '{(parent != null ? parent.name : "none")}'");
            bool calmBoxThere = parent != null && parent.Find(SbMRUKAnchorEntity.SharedBoxName) != null;
            return SharedAnchorBox.BuildEmphasis(parent, center, size, ObjectName, !calmBoxThere);
        }
    }
}
