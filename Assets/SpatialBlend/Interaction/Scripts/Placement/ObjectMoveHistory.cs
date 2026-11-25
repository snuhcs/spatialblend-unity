// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Samples.Scanning;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// Undo for object moves: one slot holding the last grabbed object and where it was before that grab. In Scene Edit ("Editing"),
    /// B puts it back, unless a box or a region is being drawn (B is that tool's own undo there) or an object menu is open. One level only:
    /// a second B does nothing until the next grab. The restored pose goes out like any move (the source sends it).
    /// </summary>
    public static class ObjectMoveHistory
    {
        private static ObjectPlacementMotif s_Motif;
        private static Pose s_Before;

        /// <summary>The motif of the last grab, while it can still be undone (null once undone, or when the object is gone).</summary>
        public static ObjectPlacementMotif Last => s_Motif;

        /// <summary>The pose the last grabbed object had before the grab.</summary>
        public static Pose Before => s_Before;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Clear() => s_Motif = null;

        /// <summary>A grab that moved or turned the object ended: remember where it was before. Replaces whatever was remembered.</summary>
        public static void Record(ObjectPlacementMotif motif, Pose before)
        {
            if (motif == null) return;
            s_Motif = motif;
            s_Before = before;
        }

        /// <summary>Forgets <paramref name="motif"/>'s move (a recenter placed it anew: undoing the earlier grab would also undo the recenter).</summary>
        public static void Forget(ObjectPlacementMotif motif)
        {
            if (motif != null && s_Motif == motif) s_Motif = null;
        }

        /// <summary>Whether a grab changed the pose enough to be worth an undo: more than 1 cm or 1 degree, the kept-object save thresholds.</summary>
        public static bool Moved(Pose before, Pose after) =>
            Vector3.Distance(before.position, after.position) > 0.01f || Quaternion.Angle(before.rotation, after.rotation) > 1f;

        /// <summary>Whether B would undo now: there is a move to undo, the mode is Scene Edit, nothing is held, and no tool that owns B is at work.</summary>
        public static bool CanUndoNow()
        {
            if (s_Motif == null) return false;
            var modes = InteractionModeManager.Instance;
            if (modes == null || modes.CurrentMode != InteractionMode.SceneEdit) return false;
            // Nothing is being held or dragged: an object, a panel or a box.
            foreach (var motif in Object.FindObjectsByType<InteractionMotif>(FindObjectsSortMode.None))
                if (motif.IsClaimed) return false;
            // Box drawing uses B as its own undo, and an idle box motif turns B into its Delete question; the manager destroys a box motif when
            // it completes, so its existence is the test. A region motif can be left idle in the scene (a mode change before its first point),
            // so it holds B only while collecting points, or on the frame B cancelled it (it is idle then, but this same press was its).
            if (Object.FindAnyObjectByType<BoxAnnotationMotif>() != null) return false;
            foreach (var region in Object.FindObjectsByType<FloorVertexCollectionMotif>(FindObjectsSortMode.None))
                if (region.OwnsUndoButtonThisFrame) return false;
            // An open object menu or scan panel is work in progress (the same rule as the idle return's).
            foreach (var context in Object.FindObjectsByType<ManipulationContext>(FindObjectsSortMode.None))
                if (context.IsPanelShowing || context.IsPassthroughPanelOpen) return false;
            foreach (var panel in Object.FindObjectsByType<PassthroughPanelManager>(FindObjectsSortMode.None))
                if (panel.IsPanelOpen) return false;
            foreach (var panel in Object.FindObjectsByType<GltfObjectControlPanelMotif>(FindObjectsSortMode.None))
                if (panel.IsPanelShowing) return false;
            return true;
        }

        /// <summary>Puts the last grabbed object back where it was before its grab. False when there is nothing to undo (or it was destroyed).</summary>
        public static bool TryUndo()
        {
            if (s_Motif == null) return false;
            ObjectPlacementMotif motif = s_Motif;
            s_Motif = null;
            motif.RestorePose(s_Before);
            return true;
        }
    }
}
