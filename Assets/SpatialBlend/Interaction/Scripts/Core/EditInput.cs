// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Interactions.UI;
using Oculus.Interaction;
using UnityEngine;

namespace SpatialBlend.Interactions
{
    /// <summary>
    /// The one place the editing tools read their input from, so a hands-only user can edit too. The right controller's ray and the
    /// right hand's ray (pinch to
    /// select) are two <see cref="EditPointer"/>s behind one set of events, and the controller's trigger, B and thumbstick have hand
    /// equivalents here:
    /// <list type="table">
    /// <item><term>Select, grab, drag</term><description>Controller: trigger on the ray. Hand: index pinch on the hand ray. Both are the Interaction SDK's own select, so a motif sees the same event.</description></item>
    /// <item><term>Accept a box point</term><description><see cref="AcceptDown"/>/<see cref="AcceptUp"/>: trigger, or pinch.</description></item>
    /// <item><term>Undo</term><description><see cref="UndoDown"/>: B, or the Undo button the mode UI shows to hands.</description></item>
    /// <item><term>Turn / push-pull a grabbed object</term><description><see cref="GrabAdjust"/>: thumbstick, or wrist twist / reach while pinching (<see cref="HandGrabAdjust"/>).</description></item>
    /// <item><term>Object menu</term><description>A button, or the Menu button the mode UI floats over a hovered object.</description></item>
    /// </list>
    /// <para><b>One input at a time.</b> Meta: "Input devices generally take precedence over the human body" and ray casting, touch and grab "remain
    /// consistent regardless of which modality is active". So exactly one source is active (<see cref="ActiveSource"/>): the only one present, or when both are
    /// (a controller in one hand and tracked hands, say) the one used last, the controller on a tie. Events of the other pointer are dropped, and while a
    /// pointer holds a selection nothing else can select, so one gesture is never handled twice and a grab never changes hands.</para>
    /// </summary>
    public static class EditInput
    {
        private static readonly List<EditPointer> s_Pointers = new List<EditPointer>();
        private static readonly Dictionary<EditPointer, RayInteractable> s_ForwardedHover = new Dictionary<EditPointer, RayInteractable>();
        private static readonly Dictionary<EditPointer, RayInteractable> s_Selections = new Dictionary<EditPointer, RayInteractable>();

        /// <summary>A pointer hovers / stops hovering / selects / stops selecting an interactable. Only the active source's pointer raises these.</summary>
        public static event Action<EditPointer, RayInteractable> Hovered, Unhovered, Selected, Unselected;

        /// <summary>The frame count. Replaced by a test.</summary>
        public static Func<int> Frame { get; set; } = () => Time.frameCount;
        /// <summary>The clock in seconds. Replaced by a test.</summary>
        public static Func<float> Clock { get; set; } = () => Time.unscaledTime;
        /// <summary>Whether the right controller is in use. Replaced by a test; the default asks OVRInput (the Editor, with no headset, counts as a controller).</summary>
        public static Func<bool> ControllerPresent { get; set; } = DefaultControllerPresent;
        /// <summary>Whether the right hand is the active input. Replaced by a test; the default asks OVRInput.</summary>
        public static Func<bool> HandPresent { get; set; } = DefaultHandPresent;
        /// <summary>The right trigger, held. Replaced by a test.</summary>
        public static Func<bool> ControllerAcceptSource { get; set; } = () => OVRInput.Get(OVRInput.RawButton.RIndexTrigger);
        /// <summary>B, pressed this frame. Replaced by a test.</summary>
        public static Func<bool> ControllerUndoSource { get; set; } = () => OVRInput.GetDown(OVRInput.RawButton.B);
        /// <summary>The right thumbstick. Replaced by a test.</summary>
        public static Func<Vector2> ControllerStickSource { get; set; } = () => OVRInput.Get(OVRInput.RawAxis2D.RThumbstick);
        /// <summary>Whether an interactable is a mode-UI widget (a pinch on one is a button press, not a box point). Replaced by a test.</summary>
        public static Func<RayInteractable, bool> IsWidget { get; set; } = ModeSwitchUi.IsWidget;

        private static float s_LastControllerUse = -1f, s_LastHandUse = -1f;
        private static bool s_PreviousAccept;
        private static EditInputSource s_PreviousAcceptSource;
        private static int s_HandUndoRequestFrame = int.MinValue;

        public static IReadOnlyList<EditPointer> Pointers => s_Pointers;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetForTests()
        {
            s_Pointers.Clear();
            s_ForwardedHover.Clear();
            s_Selections.Clear();
            Hovered = Unhovered = Selected = Unselected = null;
            Frame = () => Time.frameCount;
            Clock = () => Time.unscaledTime;
            ControllerPresent = DefaultControllerPresent;
            HandPresent = DefaultHandPresent;
            ControllerAcceptSource = () => OVRInput.Get(OVRInput.RawButton.RIndexTrigger);
            ControllerUndoSource = () => OVRInput.GetDown(OVRInput.RawButton.B);
            ControllerStickSource = () => OVRInput.Get(OVRInput.RawAxis2D.RThumbstick);
            IsWidget = ModeSwitchUi.IsWidget;
            s_LastControllerUse = s_LastHandUse = -1f;
            s_PreviousAccept = false;
            s_PreviousAcceptSource = EditInputSource.Controller;
            s_HandUndoRequestFrame = int.MinValue;
            ControllerOnlyFeatures.Clear();
        }

        private static bool DefaultControllerPresent()
        {
            if (!OVRManager.isHmdPresent) return true;
            return (OVRInput.GetActiveController() & OVRInput.Controller.RTouch) != 0;
        }

        private static bool DefaultHandPresent()
        {
            if (!OVRManager.isHmdPresent) return false;
            return (OVRInput.GetActiveController() & OVRInput.Controller.RHand) != 0;
        }

        // ---- pointers ----

        public static void Register(EditPointer pointer)
        {
            if (pointer != null && !s_Pointers.Contains(pointer)) s_Pointers.Add(pointer);
        }

        public static void Unregister(EditPointer pointer)
        {
            if (pointer == null) return;
            // A pointer that goes away (the rig is destroyed) lets go of what it holds, so no motif stays grabbed or lit.
            if (s_Selections.TryGetValue(pointer, out var selected)) RawUnselect(pointer, selected);
            if (s_ForwardedHover.TryGetValue(pointer, out var hovered)) RawUnhover(pointer, hovered);
            s_Pointers.Remove(pointer);
            s_ForwardedHover.Remove(pointer);
            s_Selections.Remove(pointer);
        }

        public static EditPointer PointerFor(EditInputSource source)
        {
            foreach (var pointer in s_Pointers) if (pointer.Source == source) return pointer;
            return null;
        }

        /// <summary>The pointer that holds a selection now, if any.</summary>
        public static EditPointer SelectingPointer
        {
            get
            {
                foreach (var pair in s_Selections) return pair.Key;
                return null;
            }
        }

        /// <summary>
        /// The source in use. A held selection decides it; else the only source present; else the one used last (the controller on a tie, or
        /// when neither has been used); with nothing present, the controller (a Foundation or desktop scene).
        /// </summary>
        public static EditInputSource ActiveSource
        {
            get
            {
                var selecting = SelectingPointer;
                if (selecting != null) return selecting.Source;
                bool controller = ControllerPresent();
                bool hand = HandPresent();
                if (hand && !controller) return EditInputSource.Hand;
                if (controller && !hand) return EditInputSource.Controller;
                if (controller && s_LastHandUse > s_LastControllerUse) return EditInputSource.Hand;
                return EditInputSource.Controller;
            }
        }

        /// <summary>The active source's pointer, or whichever pointer exists when that source has none; null with no pointers.</summary>
        public static EditPointer ActivePointer
        {
            get
            {
                var selecting = SelectingPointer;
                if (selecting != null) return selecting;
                return PointerFor(ActiveSource) ?? (s_Pointers.Count > 0 ? s_Pointers[0] : null);
            }
        }

        public static bool IsActive(EditPointer pointer) => pointer != null && pointer == ActivePointer && pointer.Source == ActiveSource;

        // ---- events from the Interaction SDK's interactors (the router calls these; a test calls them with a simulated pointer) ----

        public static void RawHover(EditPointer pointer, RayInteractable interactable)
        {
            if (pointer == null || interactable == null || !IsActive(pointer)) return;
            if (s_ForwardedHover.TryGetValue(pointer, out var current))
            {
                if (current == interactable) return;
                // The ray moved straight from one object to the next: the first is let go of before the second is taken.
                s_ForwardedHover.Remove(pointer);
                Unhovered?.Invoke(pointer, current);
            }
            s_ForwardedHover[pointer] = interactable;
            Hovered?.Invoke(pointer, interactable);
        }

        public static void RawUnhover(EditPointer pointer, RayInteractable interactable)
        {
            if (pointer == null || !s_ForwardedHover.TryGetValue(pointer, out var current) || current != interactable) return;
            s_ForwardedHover.Remove(pointer);
            Unhovered?.Invoke(pointer, interactable);
        }

        public static void RawSelect(EditPointer pointer, RayInteractable interactable)
        {
            if (pointer == null || interactable == null) return;
            // Another pointer holds a selection: the gesture is not ours. (A selection by the active input is never interrupted.)
            if (s_Selections.Count > 0) return;
            // A select is a use of its input, so with both present the one used last becomes the active one; a select by the other
            // input that does not make it the active one is dropped.
            NoteUse(pointer.Source);
            if (!IsActive(pointer)) return;
            s_Selections[pointer] = interactable;
            if (pointer.Source == EditInputSource.Hand) BeginHandGrabAdjust(pointer);
            // The select may be the first sign of this pointer's hover (the hover event can come in the same frame); make sure it is known.
            RawHover(pointer, interactable);
            Selected?.Invoke(pointer, interactable);
        }

        public static void RawUnselect(EditPointer pointer, RayInteractable interactable)
        {
            if (pointer == null || !s_Selections.TryGetValue(pointer, out var current) || current != interactable) return;
            s_Selections.Remove(pointer);
            pointer.Tracker.End();
            Unselected?.Invoke(pointer, interactable);
        }

        /// <summary>
        /// Once a frame: pointers that stopped being the active input let go of their hover (a motif must not keep a highlight a hidden pointer
        /// put on), and the active one reports what it is on now (the input just changed, or the pointer was registered while on something).
        /// </summary>
        public static void Tick()
        {
            if (ControllerAcceptSourceUsed()) NoteUse(EditInputSource.Controller);
            if (s_Selections.Count == 0)
            {
                foreach (var pointer in new List<EditPointer>(s_ForwardedHover.Keys))
                    if (!IsActive(pointer)) RawUnhover(pointer, s_ForwardedHover[pointer]);
                var active = ActivePointer;
                if (active != null)
                {
                    var now = active.Hovered;
                    s_ForwardedHover.TryGetValue(active, out var forwarded);
                    if (now != forwarded)
                    {
                        if (forwarded != null) RawUnhover(active, forwarded);
                        if (now != null) RawHover(active, now);
                    }
                }
            }
        }

        private static bool ControllerAcceptSourceUsed()
        {
            try { return ControllerPresent() && (ControllerAcceptSource() || ControllerStickSource().sqrMagnitude > 0.25f * 0.25f); }
            catch (Exception) { return false; }
        }

        /// <summary>Marks the end of a frame: the accept state it saw becomes the "previous" of the next, so down and up are edges whatever order the scripts run in.</summary>
        public static void EndFrame()
        {
            s_PreviousAccept = AcceptHeld;
            s_PreviousAcceptSource = ActiveSource;
        }

        private static void NoteUse(EditInputSource source)
        {
            if (source == EditInputSource.Hand) s_LastHandUse = Clock();
            else s_LastControllerUse = Clock();
        }

        // ---- the trigger / pinch ----

        /// <summary>The ray the tools aim with: the active pointer's. Null when there is no pointer.</summary>
        public static Ray? ActiveRay => ActivePointer?.Ray;

        /// <summary>Whether the trigger (controller) or the pinch (hand) is down. A pinch on a mode-UI button is that button's press, not a tool's.</summary>
        public static bool AcceptHeld
        {
            get
            {
                var pointer = ActivePointer;
                if (pointer == null || pointer.Source == EditInputSource.Controller) return ControllerAcceptSource();
                if (!pointer.IsPinching) return false;
                var on = pointer.Selected ?? pointer.Hovered;
                return on == null || !IsWidget(on);
            }
        }

        /// <summary>The trigger or pinch went down this frame.</summary>
        public static bool AcceptDown => AcceptHeld && !PreviousAcceptHeld;

        /// <summary>The trigger or pinch went up this frame.</summary>
        public static bool AcceptUp => !AcceptHeld && PreviousAcceptHeld;

        // What the active input held last frame. A change of input in between is not a release or a press: the other input's state is not this one's.
        private static bool PreviousAcceptHeld => s_PreviousAccept && s_PreviousAcceptSource == ActiveSource;

        // ---- undo ----

        /// <summary>Asks for an undo, as B does (the Undo button hands see). It reaches every tool in the next frame, once.</summary>
        public static void RequestUndo() => s_HandUndoRequestFrame = Frame();

        /// <summary>B was pressed this frame (controller), or the Undo button was pressed the frame before (hand).</summary>
        public static bool UndoDown
        {
            get
            {
                if (Frame() == s_HandUndoRequestFrame + 1) return true;
                var pointer = ActivePointer;
                return (pointer == null || pointer.Source == EditInputSource.Controller) && ControllerUndoSource();
            }
        }

        // ---- turn and push/pull of a grabbed object ----

        private static void BeginHandGrabAdjust(EditPointer pointer)
        {
            var wrist = pointer.WristPose;
            if (!wrist.HasValue) return;
            pointer.Tracker.BodyYaw = pointer.HeadYaw();
            pointer.Tracker.LastSampleTime = Clock();
            pointer.Tracker.Begin(wrist.Value.rotation, pointer.FingerAxis, pointer.Forward, pointer.ShoulderDistance(pointer.Tracker.BodyYaw));
        }

        /// <summary>
        /// The stick-shaped adjustment of a grab by <paramref name="pointer"/>: x turns the object, y pushes (+) or pulls (-) it, as a rate in -1..1.
        /// The controller's right thumbstick, or for a hand the wrist twist and reach since the pinch (<see cref="HandGrabAdjust"/>).
        /// </summary>
        public static Vector2 GrabAdjust(EditPointer pointer)
        {
            if (pointer == null || pointer.Source == EditInputSource.Controller) return ControllerStickSource();
            var wrist = pointer.WristPose;
            if (!wrist.HasValue) return Vector2.zero;
            var tracker = pointer.Tracker;
            if (tracker.IsActive)
            {
                float now = Clock();
                float dt = Mathf.Max(0f, now - tracker.LastSampleTime);
                tracker.LastSampleTime = now;
                tracker.BodyYaw = Quaternion.Slerp(tracker.BodyYaw, pointer.HeadYaw(), 1f - Mathf.Exp(-dt / HandGrabAdjust.HeadYawFollowSeconds));
            }
            return tracker.Sample(wrist.Value.rotation, pointer.ShoulderDistance(tracker.BodyYaw));
        }

        // ---- what still needs a controller ----

        /// <summary>
        /// Editing features that still need a controller. A feature registers a name and a test of whether it is in play; the mode cue lists those
        /// in play when Editing starts on hands. Today nothing registers: every Editing tool has a hand equivalent above.
        /// </summary>
        public static class ControllerOnlyFeatures
        {
            private static readonly List<(string name, Func<bool> inPlay)> s_Features = new List<(string, Func<bool>)>();

            public static void Register(string name, Func<bool> inPlay)
            {
                if (!string.IsNullOrEmpty(name) && inPlay != null) s_Features.Add((name, inPlay));
            }

            public static void Clear() => s_Features.Clear();

            /// <summary>The names of the registered features that are in play now.</summary>
            public static List<string> InPlay()
            {
                var names = new List<string>();
                foreach (var feature in s_Features) if (feature.inPlay()) names.Add(feature.name);
                return names;
            }
        }
    }
}
