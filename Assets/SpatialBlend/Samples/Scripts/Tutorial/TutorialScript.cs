// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// What the tutorial can observe. The Unity side turns real events (a button press, a board's drop, a grab and a release on a pad, the box
    /// manager's completion, the drawn box's Capture, the scan panel's capture, the scan service's submission and result,
    /// the anchors setting, the room list) into these signals, and the flow only reads signals, so the logic needs no scene.
    /// </summary>
    public enum TutorialSignal
    {
        ButtonPressed,
        /// <summary>The real chessboard was dropped and the room rule placed it at a valid pose on the open floor (<c>PlacementMotif.Dropped</c>).</summary>
        ChessPlaced,
        /// <summary>The real whiteboard was dropped at a valid pose on a wall (or on the floor in a room where no wall fits it).</summary>
        WhiteboardPlaced,
        AnchorsOn,
        /// <summary>The user pointed at the example box of a shared real object (its ray, or the gaze without one, meets the box).</summary>
        SharedBoxPointed,
        BoxCompleted,
        ReconstructChosen,
        PhotoCaptured,
        /// <summary>The user's points on the photo gave a mask and the user confirmed it (the card's Confirm).</summary>
        MaskReady,
        ScanSubmitted,
        ScanCopyReady,
        /// <summary>An explanation the user only hears: the step ends when its line ends.</summary>
        LineHeard,
        CopyPlaced,
        FindRoomPressed,
        RoomPicked,
        /// <summary>The mode became Scene Edit ("Editing") while the mode-switch assist was live: the user pressed [Edit] or tapped the chip.</summary>
        ModeEditing,
        /// <summary>The mode became Interaction while the assist was live: the chip was tapped or the idle auto-return ran.</summary>
        ModeInteraction,
    }

    /// <summary>One thing the user does inside a task, verified by one signal. It carries its own narration line.</summary>
    public sealed class TutorialStep
    {
        /// <summary>The imperative shown in the task's checklist ("Capture the object").</summary>
        public string Label;
        public TutorialSignal Signal;
        public string Voice;
        public string ClipName;
        /// <summary>A line under the task while this step is the one to do (an explanation, not a command). Null for none.</summary>
        public string Note;
        /// <summary>
        /// A note shown instead of <see cref="Note"/> only while the scan runs on the practice server (no scan server: a practice
        /// object stands in the user's box, <see cref="TutorialRunner.PracticeObjectShown"/>). Null for none.
        /// </summary>
        public string PracticeNote;
        /// <summary>The step only counts while the scene is in Scene Edit ("Editing"): the box can be drawn only there.</summary>
        public bool RequiresEditing;
        /// <summary>Nothing to do: the step ends when its own line has been said.</summary>
        public bool CompletesWhenHeard;
    }

    /// <summary>
    /// One task of the tour: a task line, the steps that complete it (each verified by a real event, never a timer), and the
    /// feedback shown when it is done. Id is "chapter.task".
    /// </summary>
    public sealed class TutorialFrame
    {
        public string Id;
        public int Chapter;
        /// <summary>The short name of the task ("Press a button").</summary>
        public string Title;
        /// <summary>The imperative that tells the user what to do ("Point at the glowing button and press the trigger").</summary>
        public string TaskLine;
        public TutorialStep[] Steps = Array.Empty<TutorialStep>();
        /// <summary>Shown with a check mark when the task is done.</summary>
        public string SuccessText;
        /// <summary>Move on by itself once the task is done, its sentence has ended and the feedback has been on screen for a moment.</summary>
        public bool AutoAdvance = true;
        /// <summary>The task needs something only a controller has (none does: the box, the object menu and every grab have a hand equivalent). A marked task shows a notice on hand tracking.</summary>
        public bool NeedsController;
        /// <summary>The tutorial puts the scene in Scene Edit for this task (the user is never asked to).</summary>
        public bool SceneEdit;
        /// <summary>
        /// The user has to be in Scene Edit ("Editing") for this task and the tutorial does not put them there. While they are in Interaction the card says
        /// "Switch to Editing first" and the mode-switch assist is live (so the chip and the [Edit] prompt work); once they are in Editing the assist is
        /// suspended again so its idle auto-return cannot pull them out of the task.
        /// </summary>
        public bool NeedsEditing;
        /// <summary>The task is about the mode switch itself: the tutorial leaves the mode-switch assist live for the whole task and suspends it again after.</summary>
        public bool ModeAssistLive;
        /// <summary>Said first when a <see cref="NeedsEditing"/> task starts in Interaction (then the first step's line follows).</summary>
        public string ModeVoice;
        public string ModeClipName;
        /// <summary>The scene anchors start off when the task begins, so that turning them on is a change the app can see.</summary>
        public bool AnchorsStartOff;
        /// <summary>True for a frame the user only reads and listens to (Next is enabled at once). The tour has none.</summary>
        public bool InfoOnly => Steps.Length == 0;
        /// <summary>The narration line that opens the task: its first step's.</summary>
        public string Voice => Steps.Length > 0 ? Steps[0].Voice : string.Empty;
        public string ClipName => Steps.Length > 0 ? Steps[0].ClipName : string.Empty;
    }

    /// <summary>The tasks of the task-driven tour. All text is English.</summary>
    public static class TutorialScript
    {
        public static readonly string[] ChapterNames =
        {
            "Interact",
            "Virtual and real objects",
            "Scan a real object",
            "Join a room",
        };

        /// <summary>
        /// Shown under the card only when narration clips are bundled (<see cref="TutorialAssets.HasNarration"/>). The public
        /// repository ships none: the lines are subtitles, timed by <see cref="TutorialNarrator.ReadingSeconds"/>.
        /// </summary>
        public const string AttributionLine = "Narration voice generated with ElevenLabs";

        /// <summary>Under the capture step when there is no scan server: the object in the box is the practice one.</summary>
        public const string PracticeObjectNote = "Without a scan server, a practice object stands in for yours.";

        /// <summary>
        /// Clips whose line changed and whose new audio is not delivered yet (mark them pending in ASSETS.md's clip table). Remove
        /// their old files so no stale line plays (the step then runs without a voice), and remove a name here when its new file is
        /// added.
        /// </summary>
        public static readonly IReadOnlyList<string> PendingNarration = new string[0];

        /// <summary>
        /// The only automatic pause: when a finished task moves on by itself into the next chapter. A step's next line, and the next
        /// task in the same chapter, start as soon as the line being said has ended.
        /// </summary>
        public const double ChapterGapSeconds = 2.0;

        /// <summary>
        /// After "the object you scanned is shared", this long passes before the object someone else shared arrives. Read when that
        /// line is about to play, so a test can set it to 0.
        /// </summary>
        public static double IncomingShareSeconds = 4.0;

        /// <summary>Shown under the task while the user is in Interaction and the task needs Editing.</summary>
        public const string SwitchToEditingText = "Switch to Editing first: tap the mode chip.";

        private static TutorialStep S(string clip, string label, TutorialSignal signal, string voice, string note = null, bool requiresEditing = false, bool completesWhenHeard = false,
                                      string practiceNote = null) =>
            new TutorialStep { Label = label, Signal = signal, Voice = voice, ClipName = clip, Note = note, RequiresEditing = requiresEditing, CompletesWhenHeard = completesWhenHeard,
                               PracticeNote = practiceNote };

        private static TutorialFrame T(int chapter, int index, string title, string taskLine, string success, TutorialStep[] steps,
                                       bool auto = true, bool controller = false, bool sceneEdit = false, bool anchorsOff = false,
                                       bool needsEditing = false, bool modeAssist = false, string modeClip = null, string modeVoice = null)
        {
            return new TutorialFrame
            {
                Id = chapter + "." + index,
                Chapter = chapter,
                Title = title,
                TaskLine = taskLine,
                SuccessText = success,
                Steps = steps,
                AutoAdvance = auto,
                NeedsController = controller,
                SceneEdit = sceneEdit,
                AnchorsStartOff = anchorsOff,
                NeedsEditing = needsEditing,
                ModeAssistLive = modeAssist,
                ModeClipName = modeClip,
                ModeVoice = modeVoice,
            };
        }

        public static readonly IReadOnlyList<TutorialFrame> Frames = new[]
        {
            T(1, 1, "Press a button", "Point at the glowing button and press the trigger, or pinch", "Button pressed",
                new[] { S("t1_1", "Press the glowing button", TutorialSignal.ButtonPressed,
                    "Welcome to SpatialBlend. Point at the glowing button and press the trigger. You can also pinch it.") }),
            // The real boards are placed here (there is no on-connect line to place them). A step counts on the board's
            // drop at a valid pose; the outline and the ghost show where a drop lands before letting go.
            T(1, 2, "Place the boards", "Put the chessboard on the floor and hang the whiteboard on a wall", "The chessboard and the whiteboard are in place",
                new[]
                {
                    S("t1_2_chess", "Put the chessboard on the floor", TutorialSignal.ChessPlaced,
                        "Now set up your room. Grab the chessboard and lower it onto an open spot on the floor. The outline shows where it will land.",
                        note: "Let go near the floor. Green: it lands there. Orange: it moves to the nearest free spot."),
                    S("t1_2_board", "Hang the whiteboard on a wall", TutorialSignal.WhiteboardPlaced,
                        "Now the whiteboard. Grab it and point at a wall to hang it. Let go when the outline is on the wall.",
                        note: "Point the ray at a wall. The outline shows where it will hang."),
                }, sceneEdit: true),
            T(2, 1, "See your real objects", "Open Settings and turn on Show anchors", "Now you can see your real furniture, and a shared one",
                new[]
                {
                    S("t2_1", "Turn on Show anchors", TutorialSignal.AnchorsOn,
                        "Real objects are your actual furniture. Open Settings and turn on Show anchors to see them outlined."),
                    // The box of a real object shared with the other room, shown on the user's own furniture (or a demo).
                    S("t2_1_shared", "Point at the violet box", TutorialSignal.SharedBoxPointed,
                        "A violet box marks a real object shared with your partner's room, so you both work around it. Point at it.",
                        note: "This box marks a real object in your room that is shared with your partner's room."),
                },
                anchorsOff: true),
            T(2, 2, "Switch modes", "Point at the cube for a moment, then press Edit", "Back in Interaction. The chip switches modes any time",
                new[]
                {
                    S("t2_2", "Point at the cube, then press Edit", TutorialSignal.ModeEditing,
                        "Point at the cube for a moment, then press Edit.",
                        note: "Hold the ray on the cube for a second. An Edit button floats above it: press it, or pinch it."),
                    S("t2_2b", "Tap the mode chip to go back (or wait)", TutorialSignal.ModeInteraction,
                        "You're in Editing now. Here you move things and capture real objects. To go back to Interaction, tap the mode chip, or just wait.",
                        note: "Editing is where you move things and capture real objects. The chip shows the mode."),
                }, modeAssist: true),
            T(3, 1, "Scan a real object", "Scan a real object, then place one your partner shared", "Placed. It's now part of the shared space",
                new[]
                {
                    // Any small real object near the user (not a vase), boxed from the floor, captured on the
                    // user's own Capture and 3D copy (no timer). The photo step is points on the photo and Confirm.
                    S("t3_1", "Draw a box around a small object on the floor", TutorialSignal.BoxCompleted,
                        "Pick a small object on the floor near you, like a cup or a book. Draw a box around it: a corner, then its width, depth and height.",
                        requiresEditing: true),
                    S("t3_2", "Open the object menu and press Capture", TutorialSignal.ReconstructChosen,
                        "Good. Point at your box and press A, or pinch Menu. Press Capture.", requiresEditing: true),
                    S("t3_3", "Capture the object in your box", TutorialSignal.PhotoCaptured,
                        "Look at the object in your box, then press Capture.",
                        practiceNote: PracticeObjectNote, requiresEditing: true),
                    S("t3_4", "Add points on your object, then press Confirm", TutorialSignal.MaskReady,
                        "Point at your object in the photo and pull the trigger to add points. When the highlight covers it, press Confirm.",
                        note: "Each point refines the highlight. Remove marks what isn't your object; Undo takes a point back.",
                        requiresEditing: true),
                    S("t3_5", "Press 3D copy", TutorialSignal.ScanSubmitted,
                        "Your object is highlighted. Press 3D copy to build it.", requiresEditing: true),
                    S("t3_6", "Wait for your 3D copy", TutorialSignal.ScanCopyReady,
                        "Your 3D copy is being built. This takes a moment."),
                    S("t3_7", "Your scan is shared", TutorialSignal.LineHeard,
                        "The object you scanned is now shared with another space.",
                        completesWhenHeard: true),
                    // Say why it is placed (a partner's real object arrives here as a virtual one; placing it connects it).
                    S("t3_8_partner", "Place your partner's object", TutorialSignal.CopyPlaced,
                        "Your partner scanned this object in their room and shared it with you. Place it wherever you like in your room. That connects it to the interaction.",
                        note: "It isn't in your room, so it arrived as a virtual object. Placing it connects it to the interaction."),
                }, needsEditing: true, modeClip: "t3_0", modeVoice: "Switch to Editing first. Tap the mode chip."),
            T(4, 1, "Find a room", "Press Find a room", "Rooms found",
                new[] { S("t4_1", "Press Find a room", TutorialSignal.FindRoomPressed,
                    "To meet others, press Find a room.") }),
            T(4, 2, "Pick a room", "Pick a room", "Room picked. This is a demo, nothing was joined",
                new[] { S("t4_2", "Pick a room", TutorialSignal.RoomPicked,
                    "Pick a room to join. This is only a demo. That's the tour. Have fun.") },
                auto: false),
        };

        public static int IndexOf(string id)
        {
            for (int i = 0; i < Frames.Count; i++) if (Frames[i].Id == id) return i;
            return -1;
        }

        /// <summary>The line a clip name says (a step's line, or a task's lead-in), or null for a name the script does not use.</summary>
        public static string LineFor(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return null;
            foreach (var frame in Frames)
            {
                if (frame.ModeClipName == clipName) return frame.ModeVoice;
                foreach (var step in frame.Steps)
                    if (step.ClipName == clipName) return step.Voice;
            }
            return null;
        }

        /// <summary>Every narration clip the script names, in order: the one each step plays.</summary>
        public static IEnumerable<TutorialStep> AllSteps()
        {
            foreach (var frame in Frames)
                foreach (var step in frame.Steps)
                    yield return step;
        }
    }
}
