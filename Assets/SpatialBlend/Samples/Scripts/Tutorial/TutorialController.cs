// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Objects;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Objects.Whiteboard;
using SpatialBlend.Samples.Scanning;
using MRMotifs.SharedActivities.ChessSample;
using UnityEngine;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    /// <summary>
    /// The overlay controller of the lobby tutorial: one component in the Freeform Collaboration scene's runtime (created by the
    /// lobby's Tutorial button, not a second scene). It owns the <see cref="TutorialRunner"/>, turns real events into signals,
    /// plays the narration through a 2D AudioSource, and is the runner's host: the card replaces the lobby panel, the scene state is
    /// snapshotted and put back, and every prop is local only.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class TutorialController : MonoBehaviour, ITutorialHost
    {
        private ControlPanel _panel;
        private ITutorialClock _clock;
        private TutorialRunner _runner;
        private TutorialView _view;
        private AudioSource _audio;
        private TutorialScanBackend _scan;
        // The real scan failed: the next Update moves the run to the canned server (not inside the failing event).
        private bool _fallbackWanted;
        // The captures already counted before a fallback re-opened the capture: only a later one is the user's new Capture.
        private int _captureBaseline;
        private SessionManager _manager;
        private readonly TutorialPracticeProps _props = new TutorialPracticeProps();
        private BoxAnnotationManager _boxManager;
        private ScanCard _openPanel;
        // The box the user drew, remembered when the card opens. The copy captures its own snapshot of this when it is
        // created, so a later card does not move a copy that is still loading.
        private ScanAnchor _copyBox;
        private Quaternion _copyBoxRotation;
        private bool _hasCopyBox;
        private CopySeat _copySeat;
        private ScanService _service;
        private bool _submitted, _copyReady;
        private AudioSource _sfx;
        private AudioClip _chime;
        // The grabbable the current task watches and what its release means.
        private readonly List<WatchedGrab> _watched = new List<WatchedGrab>();
        private bool _ended;
        private HashSet<BoxAnnotationInstance> _boxesBefore;
        // Task 1.2: the scene's real boards (found when the task starts) and the hint that says which way to turn to see one.
        private PlacementMotif _chess, _whiteboard;
        private string _directionHint;

        public TutorialRunner Runner => _runner;
        public TutorialView View => _view;
        public TutorialScanBackend Scan => _scan;
        public TutorialPracticeProps Props => _props;
        public bool CopyReady => _copyReady;
        public AudioSource Audio => _audio;
        public bool Running => _runner != null && _runner.Flow.Running;

        /// <summary>Starts the tutorial over the lobby panel. Returns null when the panel has no canvas to carry the card.</summary>
        public static TutorialController Begin(ControlPanel panel)
        {
            if (panel == null || panel.PanelCanvas == null) return null;
            var go = new GameObject("TutorialController");
            var controller = go.AddComponent<TutorialController>();
            controller.Initialize(panel);
            return controller;
        }

        /// <summary>Explicit set-up (not Awake), so the controller also runs where Unity does not call Awake (the Editor tests).</summary>
        private void Initialize(ControlPanel panel)
        {
            _panel = panel;
            _clock = new FuncTutorialClock(() => Time.realtimeSinceStartupAsDouble);
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = false;
            _audio.spatialBlend = 0f;   // 2D: stays audible while the user walks around placing things
            _sfx = gameObject.AddComponent<AudioSource>();   // the success chime has its own source, so it never cuts the narration
            _sfx.playOnAwake = false;
            _sfx.spatialBlend = 0f;
            _runner = new TutorialRunner(this, _clock, TutorialDoneMark.Shared);
            _view = TutorialView.Create(panel.PanelCanvas);
            _view.Bind(_runner, new TutorialCardActions
            {
                PressButton = OnPressButton,
                ShowAnchors = OnShowAnchors,
                FindRoom = OnFindRoom,
                RetryScan = OnRetryScan,
                JoinSampleRoom = name => OnRoomTapped(name),
                HostSampleRoom = OnNewRoomTapped,
            });
            ScanCard.Opening += OnScanPanelOpening;
            SpatialBlend.Interactions.UI.ManipulationPopUp.ReconstructPicked += OnReconstructPicked;
            OVRManager.InputFocusLost += OnFocusLost;
            OVRManager.InputFocusAcquired += OnFocusAcquired;
            // Boxes the user draws during the tutorial are practice: they go with it, the ones that were there before stay.
            _boxesBefore = new HashSet<BoxAnnotationInstance>(FindObjectsByType<BoxAnnotationInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            _boxManager = FindAnyObjectByType<BoxAnnotationManager>();
            if (_boxManager != null) _boxManager.BoxCompleted += OnBoxCompleted;
            PlacementMotif.Dropped += OnBoardDropped;
            _runner.Begin();
        }

        // ---------------------------------------------------------------- per frame

        private void Update()
        {
            if (_runner == null || _ended) return;
            _runner.Tick();
            if (_view != null) _view.Tick();
            if (_scan != null) _scan.Tick();
            if (_fallbackWanted) RecoverWithCanned();
            PollSignals();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (_runner != null) _runner.SetFocus(focused);
        }

        private void OnFocusLost() { if (_runner != null) _runner.SetFocus(false); }
        private void OnFocusAcquired() { if (_runner != null) _runner.SetFocus(true); }

        /// <summary>
        /// The signals that are states rather than moments are read each frame, so a step done during the wait for the end of the
        /// previous sentence is not lost: the vase is in view, a photo is captured, the request went out, the copy is in. The flow
        /// only counts the one the current step waits for, so reporting a later one early does nothing.
        /// </summary>
        private void PollSignals()
        {
            if (!_runner.Flow.Running) return;
            switch (_runner.Flow.Current.Id)
            {
                case "1.2":
                    UpdateBoardView();
                    break;
                case "2.1":
                    PollSharedBox();
                    break;
                case "2.2":
                    PollMode();
                    break;
                case "3.1":
                    if (_openPanel != null && _openPanel.Progress.CaptureCount > _captureBaseline) _runner.Report(TutorialSignal.PhotoCaptured);
                    if (_openPanel != null && _openPanel.Flow != null && MaskConfirmed(_openPanel.Flow.State))
                        _runner.Report(TutorialSignal.MaskReady);
                    if (_submitted) _runner.Report(TutorialSignal.ScanSubmitted);
                    if (_copyReady) _runner.Report(TutorialSignal.ScanCopyReady);
                    // The vase is hidden only while the copy stands in its place. Deleting the copy brings it back.
                    if (_props.Copy == null) RevealPracticeVase();
                    break;
            }
        }

        /// <summary>The user confirmed the mask (the card's Confirm): the photo step is done, whatever came after.</summary>
        internal static bool MaskConfirmed(ScanFlowState state) =>
            state == ScanFlowState.Confirming || state == ScanFlowState.Building || state == ScanFlowState.Done;

        // The mode task is verified by the mode itself, read back from the manager: the user pressed [Edit] on the cube, or tapped the chip, or
        // the assist's idle auto-return ran. The tutorial never sets the mode in this task, so a change is always the user's (or the assist's).
        private void PollMode()
        {
            if (Modes == null) return;
            if (Modes.CurrentMode == InteractionMode.SceneEdit) _runner.Report(TutorialSignal.ModeEditing);
            else _runner.Report(TutorialSignal.ModeInteraction);
        }

        // ---------------------------------------------------------------- task 2.1: the shared real-object box

        public const string SharedBoxExampleName = "TutorialSharedBoxExample";
        private GameObject _sharedBoxExample;

        /// <summary>The example box (null outside its step).</summary>
        internal GameObject SharedBoxExample => _sharedBoxExample;

        /// <summary>The ray the user points with: the active editing pointer's, else the gaze. Replaced by a test.</summary>
        internal Func<Ray?> PointerRay { get; set; }

        private Ray? CurrentPointerRay()
        {
            if (PointerRay != null) return PointerRay();
            EditPointer pointer = EditInput.ActivePointer;
            if (pointer != null) return pointer.Ray;
            var eye = Eye;
            return eye != null ? new Ray(eye.position, eye.forward) : (Ray?)null;
        }

        /// <summary>
        /// The example of a shared real object's box: the box the app draws for one (SharedAnchorBox), on the largest
        /// piece of furniture in the user's room (an example only: nothing is shared), or, with no room setup, a demo box in front.
        /// </summary>
        private void ShowSharedBoxExample()
        {
            if (_sharedBoxExample != null) return;
            SceneAnchorRecord best = null;
            var app = FindAnyObjectByType<FreeformCollaborationApp>();
            if (app != null)
                foreach (SceneAnchorRecord record in app.ShareableAnchors())
                    if (record != null && record.Anchor != null && (best == null || record.Size > best.Size)) best = record;
            if (best != null)
                _sharedBoxExample = SharedAnchorBox.Build(best.Anchor.transform, best.LocalCenter, best.LocalSize, SharedBoxExampleName);
            else
            {
                Vector3 spot = TutorialPracticeProps.SpotAhead(Eye, 1.6f);
                var size = new Vector3(0.9f, 0.75f, 0.6f);
                _sharedBoxExample = SharedAnchorBox.Build(null, new Vector3(spot.x, size.y * 0.5f, spot.z), size, SharedBoxExampleName);
            }
            _props.AddTarget(_sharedBoxExample);
            _sharedBoxFill = _sharedBoxExample.transform.Find("Fill")?.GetComponent<Renderer>();
        }

        private Renderer _sharedBoxFill;

        // The step is done when the user points at the box: the pointer's ray (or the gaze) meets its volume.
        private void PollSharedBox()
        {
            if (_runner.Flow.ActionDone || _sharedBoxExample == null) return;
            if (_runner.Flow.Current.Steps[_runner.Flow.StepIndex].Signal != TutorialSignal.SharedBoxPointed) return;
            Renderer fill = _sharedBoxFill;
            Ray? ray = CurrentPointerRay();
            if (fill == null || !ray.HasValue) return;
            if (fill.bounds.IntersectRay(ray.Value)) _runner.Report(TutorialSignal.SharedBoxPointed);
        }

        // ---------------------------------------------------------------- task 1.2: the real boards

        public const string NoBoardText = "It isn't in your room yet. You can skip this step.";
        public const string LowerItText = "Point it at the open floor, then let go.";
        public const string NoSpotText = "There's no free spot for it in this room. You can skip this step.";
        public const string HangItText = "Point at a wall, then let go to hang it.";

        /// <summary>The scene's chessboard (an object motif under the chess handler) and whiteboard (a panel motif under the whiteboard manager).</summary>
        internal static void FindBoards(out PlacementMotif chess, out PlacementMotif whiteboard)
        {
            chess = whiteboard = null;
            foreach (var motif in FindObjectsByType<PlacementMotif>(FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID))
            {
                var entity = motif.GetComponentInParent<SbEntity>();
                GameObject root = entity != null ? entity.gameObject : motif.gameObject;
                if (SpatialBlendManager.IsPhysicalObject(root)) continue;
                if (chess == null && motif is ObjectPlacementMotif && root.GetComponentInChildren<ChessBoardHandlerMotif>(true) != null) chess = motif;
                else if (whiteboard == null && motif is PanelPlacementMotif && root.GetComponentInChildren<WhiteboardManagerMotif>(true) != null) whiteboard = motif;
            }
        }

        /// <summary>The board the step is about, outlined (the outline goes with the step).</summary>
        private void ShowBoard(TutorialSignal signal)
        {
            _props.ClearTargets();
            _directionHint = null;
            PlacementMotif board = signal == TutorialSignal.ChessPlaced ? _chess : signal == TutorialSignal.WhiteboardPlaced ? _whiteboard : null;
            if (board == null) { _runner.SetHint(NoBoardText); return; }
            _props.Highlight(BoardRoot(board));
        }

        private static GameObject BoardRoot(PlacementMotif motif)
        {
            var entity = motif.GetComponentInParent<SbEntity>();
            return entity != null ? entity.gameObject : motif.gameObject;
        }

        // While the board is out of view the hint says which way to turn (over any other hint: nothing else can be done until it is
        // seen); once it is in view, the direction is cleared and the step's own note shows again.
        internal void UpdateBoardView()
        {
            if (!_runner.Flow.Running || _runner.Flow.ActionDone) return;
            TutorialSignal signal = _runner.Flow.Current.Steps[_runner.Flow.StepIndex].Signal;
            PlacementMotif board = signal == TutorialSignal.ChessPlaced ? _chess : _whiteboard;
            var eye = Eye;
            if (board == null || eye == null) return;
            string hint = TutorialPracticeProps.DirectionHint(eye.position, eye.forward, BoardRoot(board).transform.position);
            if (hint != null)
            {
                if (_runner.Hint != hint) _runner.SetHint(hint);
                _directionHint = hint;
            }
            else if (_directionHint != null)
            {
                // Cleared only while the hint shown is still the direction (a drop's hint since then stays).
                if (_runner.Hint == _directionHint) _runner.SetHint(null);
                _directionHint = null;
            }
        }

        /// <summary>What a board's drop means for the step: the signal it completes, or the hint to show instead.</summary>
        internal static (TutorialSignal? signal, string hint) JudgeBoardDrop(bool isChess, bool isWhiteboard, in PlacementDrop drop, Func<bool> anyWallFits)
        {
            if (!isChess && !isWhiteboard) return (null, null);
            if (!drop.Settles || drop.Reverted && drop.OffFloor) return (null, LowerItText);
            if (!drop.Placed) return (null, NoSpotText);
            if (isChess) return (TutorialSignal.ChessPlaced, null);
            // The whiteboard hangs on a wall; on the floor it counts only where no wall fits it (the rule's floor fallback).
            if (drop.OnWall || anyWallFits == null || !anyWallFits()) return (TutorialSignal.WhiteboardPlaced, null);
            return (null, HangItText);
        }

        /// <summary>A board was let go (the placement motif's drop result): a valid drop completes its step.</summary>
        private void OnBoardDropped(PlacementDrop drop)
        {
            if (_runner == null || _ended || !_runner.Flow.Running || _runner.Flow.Current.Id != "1.2" || _runner.Flow.ActionDone) return;
            TutorialSignal want = _runner.Flow.Current.Steps[_runner.Flow.StepIndex].Signal;
            bool isChess = drop.Motif != null && drop.Motif == _chess && want == TutorialSignal.ChessPlaced;
            bool isWhiteboard = drop.Motif != null && drop.Motif == _whiteboard && want == TutorialSignal.WhiteboardPlaced;
            var app = FindAnyObjectByType<SbApp>();
            (TutorialSignal? signal, string hint) = JudgeBoardDrop(isChess, isWhiteboard, drop,
                () => app != null && app.AnyWallFits(BoardRoot(drop.Motif), drop.Motif, SpatialBlendManager.RegionFitMarginMeters));
            if (signal.HasValue) { _directionHint = null; _runner.Report(signal.Value); }
            else if (hint != null) { _directionHint = null; _runner.SetHint(hint); }
        }

        // ---------------------------------------------------------------- ITutorialHost

        private InteractionModeManager Modes => InteractionModeManager.Instance;

        // The panel can already be gone when the run ends because the panel itself was destroyed; then there is nothing to put back there.
        private SceneControlsPresenter Presenter => _panel != null ? _panel.ScenePresenter : null;

        public bool SceneEdit
        {
            get => Modes != null && Modes.CurrentMode == InteractionMode.SceneEdit;
            set
            {
                if (Modes != null) Modes.SetMode(value ? InteractionMode.SceneEdit : InteractionMode.Interaction);
                var presenter = Presenter;
                if (presenter != null) presenter.Mirror(presenter.AnchorsVisible, value);
            }
        }

        public bool AnchorsVisible
        {
            get { var presenter = Presenter; return presenter != null && presenter.AnchorsVisible; }
            set
            {
                var presenter = Presenter;
                if (presenter != null) presenter.SetAnchorsVisible(value);
                if (_view != null) _view.AnchorsRow.Set(value);
            }
        }

        public float Brightness
        {
            get { var presenter = Presenter; return presenter != null ? presenter.Brightness : 0f; }
            set { var presenter = Presenter; if (presenter != null) presenter.SetBrightness(value); }
        }

        public bool HandTrackingActive => OVRInput.GetActiveController() == OVRInput.Controller.Hands;

        public bool RoomAnchorsAvailable => MRUK.Instance != null && MRUK.Instance.Rooms != null && MRUK.Instance.Rooms.Count > 0;

        public void SetLobbyHidden(bool hidden)
        {
            if (_panel != null) _panel.SetTutorialActive(hidden);
        }

        public void FrameEntered(TutorialFrame frame)
        {
            _watched.Clear();
            _props.ClearTargets();
            var eye = Eye;
            if (!frame.Id.StartsWith("4.") && _panel != null) _panel.HideRoomList();
            switch (frame.Id)
            {
                case "1.2":
                    FindBoards(out _chess, out _whiteboard);
                    _directionHint = null;
                    break;
                case "2.2":
                {
                    // A cube to point at: the assist offers "Edit this object?" over a marked object, and the cube is the one to mark.
                    var cube = _props.EnsureCube(eye);
                    if (cube.GetComponent<ModeSwitchEditableMarker>() == null) cube.gameObject.AddComponent<ModeSwitchEditableMarker>();
                    _props.Highlight(cube.gameObject);
                    break;
                }
                case "3.1":
                    ResetScanProgress();
                    EnsureScan();
                    break;
                case "4.1":
                    if (_panel != null) _panel.HideRoomList();
                    _runner.NoteRoomList(false, false);
                    break;
            }
        }

        private sealed class WatchedGrab
        {
            public TutorialGrabbable Grab;
            public TutorialSignal? Grabbed;
            public TutorialSignal Placed;
            public Action OnGrabbed;
            public Action<Vector3> OnReleased;
        }

        private void Watch(TutorialGrabbable grab, TutorialSignal? grabbed, TutorialSignal placed)
        {
            var watch = new WatchedGrab { Grab = grab, Grabbed = grabbed, Placed = placed };
            watch.OnGrabbed = () => { if (watch.Grabbed.HasValue) _runner.Report(watch.Grabbed.Value); };
            watch.OnReleased = position => OnGrabbableReleased(watch, position);
            grab.Grabbed += watch.OnGrabbed;
            grab.Released += watch.OnReleased;
            _watched.Add(watch);
        }

        private void Unwatch()
        {
            foreach (var watch in _watched)
            {
                if (watch.Grab == null) continue;
                watch.Grab.Grabbed -= watch.OnGrabbed;
                watch.Grab.Released -= watch.OnReleased;
            }
            _watched.Clear();
        }

        /// <summary>
        /// Let go anywhere: it is set down on the floor, as a floor object's drop snaps
        /// (never left floating), and the one validity rule places it (the drop result: <see cref="SbApp.TryPullPlacement"/>, the
        /// object's collider standing in for a region it does not author). Valid, or pulled to the nearest valid spot, the step is done;
        /// where no spot in the room fits, it says so and stays where it was let go. With no room known there is nothing to check
        /// against: it is set on the floor (at 0) and the drop counts.
        /// </summary>
        private void OnGrabbableReleased(WatchedGrab watch, Vector3 position)
        {
            if (_runner == null || _ended || watch.Grab == null) return;
            GameObject go = watch.Grab.gameObject;
            // The grab moved it this frame: the rule reads its colliders where it is now (once per drop).
            Physics.SyncTransforms();
            var app = FindAnyObjectByType<SbApp>();
            if (app == null || app.PlacementRoom == null)
            {
                SetOnFloor(go, app != null ? app.PlacementFloorY : 0f);
                _runner.Report(watch.Placed);
                return;
            }
            if (app.TryPullPlacement(go, Vector2.zero, SpatialBlendManager.RegionFitMarginMeters, out Vector2 shift))
            {
                go.transform.position += new Vector3(shift.x, 0f, shift.y);
                SetOnFloor(go, app.PlacementFloorY);
                _runner.Report(watch.Placed);
            }
            else _runner.SetHint(NoSpotText);
        }

        /// <summary>Moves <paramref name="go"/> down (or up) so the bottom of its colliders rests at <paramref name="floorY"/>.</summary>
        internal static void SetOnFloor(GameObject go, float floorY)
        {
            Physics.SyncTransforms();   // moved by the pull just now: its colliders' bounds are read where it is
            Bounds? bounds = null;
            foreach (Collider c in go.GetComponentsInChildren<Collider>())
            {
                if (c.isTrigger) continue;
                if (bounds == null) bounds = c.bounds; else { Bounds b = bounds.Value; b.Encapsulate(c.bounds); bounds = b; }
            }
            if (bounds.HasValue) go.transform.position += Vector3.up * (floorY - bounds.Value.min.y);
        }

        /// <param name="standOnY">When set, the loaded model is moved so its lowest point is at this height (whatever its origin).</param>
        /// <param name="seat">When set, the loaded model is fitted to this box and rested on its bottom face. The value is the box
        /// captured when the copy was created.</param>
        private async void LoadModelInto(GameObject holder, byte[] bytes, float metres, float? standOnY = null, CopySeat? seat = null)
        {
            try
            {
                // The glTF loader needs the player loop; outside play mode (the Editor tests) the plain body stands in.
                if (bytes == null || !Application.isPlaying) return;
                var body = holder.transform.Find("PlaceholderBody");
                bool ok = await GltfRuntimeLoader.LoadModelFromBytes(holder, bytes, () => holder != null && !_ended);
                if (!ok || holder == null) return;
                // Deactivated first: Destroy is deferred in play mode and the placeholder must not be measured.
                if (body != null) { body.gameObject.SetActive(false); Kill(body.gameObject); }
                if (seat.HasValue) FitCopyToBox(holder, seat.Value);
                else
                {
                    FitToSize(holder, metres);
                    if (standOnY.HasValue) StandOn(holder, standOnY.Value);
                }
                TutorialPracticeProps.FitCollider(holder);
                TutorialPracticeProps.FitRealAnchorBox(holder);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tutorial] Could not load the sample model; the plain body stays: " + e.Message);
            }
        }

        private void ResetScanProgress()
        {
            _submitted = false;
            _copyReady = false;
            _runner.NoteScanInFlight(false);
        }

        public void FrameLeft(TutorialFrame frame)
        {
            Unwatch();
            _props.ClearTargets();
            _sharedBoxExample = null;   // a target: cleared with them
            switch (frame.Id)
            {
                case "1.2":
                    // The boards are the user's own and stay where they were put.
                    _chess = _whiteboard = null;
                    _directionHint = null;
                    break;
                case "2.2":
                    _props.DestroyCube();
                    break;
                case "3.1":
                    _fallbackWanted = false;
                    _hasCopyBox = false;
                    // A scan panel left open does not follow the user into the next task, and the copy goes with its task.
                    CloseScanPanel();
                    DestroyCopy();
                    if (_scan != null) _scan.CancelInFlight();
                    ResetScanProgress();
                    break;
            }
        }

        /// <summary>The scan's practice objects: the copy, the virtual object and the hologram vase in the user's box.</summary>
        private void DestroyCopy()
        {
            _props.DestroyCopy();
            _props.DestroyShared();
            _props.DestroyHologram();
        }

        public void StepStarted(TutorialFrame frame, int stepIndex)
        {
            if (frame.Steps[stepIndex].Signal == TutorialSignal.SharedBoxPointed) ShowSharedBoxExample();
            if (frame.Steps[stepIndex].Signal == TutorialSignal.CopyPlaced) ShowVirtualObject();
            if (frame.Id == "1.2") ShowBoard(frame.Steps[stepIndex].Signal);
        }

        public void StepCompleted(TutorialFrame frame, int stepIndex)
        {
            PlaySuccess();
            switch (frame.Steps[stepIndex].Signal)
            {
                // The pad and the outline go away once the object is on it.
                case TutorialSignal.ChessPlaced:
                case TutorialSignal.WhiteboardPlaced:
                case TutorialSignal.SharedBoxPointed:
                case TutorialSignal.CopyPlaced:
                    _props.ClearTargets();
                    break;
            }
        }

        /// <summary>
        /// The last step's object: a different object from the scanned vase (a lamp), standing in a box in the real-anchor colour, the look of
        /// a shared object that has no counterpart in the room. It arrives separately, once the scanned copy is in, and the user places it
        /// wherever they like in the room (a valid drop, <see cref="OnGrabbableReleased"/>).
        /// </summary>
        private void ShowVirtualObject()
        {
            if (_ended || _runner == null || !_runner.Flow.Running || _runner.Flow.Current.Id != "3.1" || _props.Shared != null) return;
            var eye = Eye;
            var shared = _props.EnsureShared(TutorialPracticeProps.SpotAhead(eye, 1.0f, 0.35f, 0.05f));
            TutorialPracticeProps.AddPlaceholderBody(shared.gameObject, TutorialPracticeProps.SharedSize);
            _props.AddRealAnchorBox(shared.gameObject, TutorialPracticeProps.SharedSize);
            Watch(shared, null, TutorialSignal.CopyPlaced);
            LoadModelInto(shared.gameObject, TutorialAssets.LoadSharedModel(), 0.3f);
        }

        /// <summary>The success chime. The Editor outside play mode has no audio output; the call is then a no-op.</summary>
        public void PlaySuccess()
        {
            if (_sfx == null || !Application.isPlaying) return;
            if (_chime == null) _chime = TutorialFeedback.BuildChime();
            _sfx.PlayOneShot(_chime);
        }

        public double PlayVoice(string clipName)
        {
            var clip = TutorialAssets.LoadClip(clipName);
            // No bundled clip (the public repository ships none): the line is a subtitle, up for its reading time, so the pacing,
            // the sentence order and a step that completes when heard behave as with a voice.
            if (clip == null) return TutorialNarrator.ReadingSeconds(TutorialScript.LineFor(clipName));
            if (_audio == null) return 0.0;
            _audio.Stop();
            _audio.clip = clip;
            // The Editor outside play mode has no audio output; the length still drives the subtitles.
            if (Application.isPlaying) _audio.Play();
            return clip.length;
        }

        public void StopVoice()
        {
            if (_audio != null) _audio.Stop();
        }

        public void PauseVoice(bool paused)
        {
            if (_audio == null) return;
            if (paused) _audio.Pause();
            else _audio.UnPause();
        }

        public void Ended(TutorialEnd end)
        {
            if (_ended) return;
            _ended = true;
            Teardown();
            Kill(gameObject);
        }

        /// <summary>The panel went away or the app is closing: the run ends as an exit, and everything is put back.</summary>
        public void Abort()
        {
            if (_runner != null && !_ended) _runner.Abort();
            if (!_ended) { _ended = true; Teardown(); }
        }

        private void OnDestroy()
        {
            if (!_ended)
            {
                _ended = true;
                if (_runner != null && _runner.Flow.Running) _runner.Abort();
                Teardown();
            }
        }

        private void Teardown()
        {
            ScanCard.Opening -= OnScanPanelOpening;
            SpatialBlend.Interactions.UI.ManipulationPopUp.ReconstructPicked -= OnReconstructPicked;
            OVRManager.InputFocusLost -= OnFocusLost;
            OVRManager.InputFocusAcquired -= OnFocusAcquired;
            if (_boxManager != null) _boxManager.BoxCompleted -= OnBoxCompleted;
            PlacementMotif.Dropped -= OnBoardDropped;
            Unwatch();
            UnsubscribeService();
            CloseScanPanel();
            if (_panel != null) _panel.HideRoomList();
            if (_scan != null) { _scan.ResultReady -= OnScanResult; _scan.Failed -= OnScanFailed; _scan.Release(); }
            _props.DestroyAll();
            RemovePracticeBoxes();
            if (_chime != null) { Kill(_chime); _chime = null; }
            if (_view != null)
            {
                _view.Unbind();
                Kill(_view.gameObject);
                _view = null;
            }
            if (_audio != null) _audio.Stop();
        }

        // ---------------------------------------------------------------- the world's events

        private void RemovePracticeBoxes()
        {
            if (_boxesBefore == null) return;
            foreach (var box in FindObjectsByType<BoxAnnotationInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (box != null && !_boxesBefore.Contains(box)) Kill(box.gameObject);
            _boxesBefore = null;
        }

        private static void Kill(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        private Transform Eye
        {
            get
            {
                var rig = FindAnyObjectByType<OVRCameraRig>();
                if (rig != null && rig.centerEyeAnchor != null) return rig.centerEyeAnchor;
                return Camera.main != null ? Camera.main.transform : null;
            }
        }

        private void OnBoxCompleted(bool success)
        {
            if (success) _runner.Report(TutorialSignal.BoxCompleted);
        }

        private void OnPressButton() => _runner.Report(TutorialSignal.ButtonPressed);

        /// <summary>Capture on a drawn box was switched on. That toggle opens the scan card; the flow still reports it as <see cref="TutorialSignal.ReconstructChosen"/>.</summary>
        private void OnReconstructPicked() => _runner.Report(TutorialSignal.ReconstructChosen);

        private void OnShowAnchors(bool on)
        {
            AnchorsVisible = on;
            // The task asks for a change of the real setting, so it is read back from the app, not taken from the click.
            if (AnchorsVisible) _runner.Report(TutorialSignal.AnchorsOn);
        }

        // ---------------------------------------------------------------- scanning

        private void EnsureScan()
        {
            if (_scan == null)
            {
                byte[] model = TutorialAssets.LoadSampleModel();
                _scan = new TutorialScanBackend(_clock, () => model, Application.temporaryCachePath, CannedSegmenter.LoadSample);
                _scan.ResultReady += OnScanResult;
                _scan.Failed += OnScanFailed;
            }
            if (_scan.Mode != TutorialScanMode.None) return;
            _manager = SessionManager.EnsureInstance();
            _scan.Prepare(_manager);
            SubscribeService();
            if (_scan.Mode == TutorialScanMode.None)
                _runner.ReportScanFailure(ScanFailureText.ToUserMessage(new ScanError(ScanErrorCode.NotInitialized, "no scan service")));
        }

        private void SubscribeService()
        {
            var service = _scan != null && _scan.ActiveService != null ? _scan.ActiveService : _manager != null ? _manager.Service : null;
            if (service == null || service == _service) return;
            UnsubscribeService();
            _service = service;
            _service.Submitted += OnSubmitted;
            _service.PendingChanged += OnPendingChanged;
        }

        private void UnsubscribeService()
        {
            if (_service == null) return;
            _service.Submitted -= OnSubmitted;
            _service.PendingChanged -= OnPendingChanged;
            _service = null;
        }

        private void OnSubmitted(ScanSubmission submission)
        {
            // A segmentation request (one per tap on a server with the two-step pipeline) is not "Reconstruct 3D".
            if (submission.Kind == ScanKind.Segment) return;
            _submitted = true;
            _runner.ClearScanProblem();
            _runner.NoteScanInFlight(true);
        }

        private void OnPendingChanged(ScanPendingEvent pending)
        {
            // A late failure of a segmentation request (a tap's mask) says nothing about the reconstruction.
            if (pending.Kind == ScanKind.Segment) return;
            if (pending.Phase != ScanPendingPhase.Failed && pending.Phase != ScanPendingPhase.TimedOut) return;
            if (WantFallback(pending.Error)) return;
            _runner.ReportScanFailure(ScanFailureText.ToUserMessage(pending.Error));
        }

        private void OnScanFailed(ScanFailure failure)
        {
            if (WantFallback(failure.Error)) return;
            _runner.ReportScanFailure(ScanFailureText.ToUserMessage(failure.Error));
        }

        private void OnScanPanelOpening(ScanCard panel)
        {
            if (!Running) return;
            EnsureScan();
            if (_openPanel != null && _openPanel != panel) CloseScanPanel();
            _openPanel = panel;
            _captureBaseline = 0;
            _copyBox = panel.ScannedBox;
            _copyBoxRotation = panel.BoxRotation;
            _hasCopyBox = _copyBox.HasSize;
            panel.RequestFailed += OnRequestFailed;
            panel.ServiceSource = () => _scan != null && _scan.ActiveService != null ? _scan.ActiveService : _manager != null ? _manager.Service : null;
            // The user adds points on the photo, confirms the mask and presses 3D copy, over the real server as over the canned one.
            // The practice server's mask comes from the user's own point, never from the box alone.
            _runner.PracticeObjectShown = _scan != null && _scan.Mode == TutorialScanMode.Canned;
            panel.PromptWithBox = !_runner.PracticeObjectShown;
            if (_runner.PracticeObjectShown) InstallHologram(panel);
        }

        /// <summary>
        /// A real scan failed at any point (no server, an error, the request's own timeout, an empty result): the run goes on over the
        /// canned server instead. True when the failure was taken for a fallback and must not be shown as a failure.
        /// </summary>
        private bool WantFallback(ScanError error = default)
        {
            // A cancel is the user's (Cancel on the card, Try again, leaving the task), not the server's: the real path stays.
            if (error.Code == ScanErrorCode.Cancelled) return false;
            if (_scan == null || _scan.Mode != TutorialScanMode.Real || _scan.FellBack || _ended) return false;
            if (!_runner.Flow.Running || _runner.Flow.Current.Id != "3.1") return false;
            _fallbackWanted = true;
            return true;
        }

        private void RecoverWithCanned()
        {
            _fallbackWanted = false;
            if (_ended || !_runner.Flow.Running || _scan == null) return;
            if (!_scan.FallBackToCanned())
            {
                _runner.ReportScanFailure(ScanFailureText.ToUserMessage(new ScanError(ScanErrorCode.NotInitialized, "no scan service")));
                return;
            }
            SubscribeService();
            var panel = _openPanel;
            if (_runner.Flow.Current.Id != "3.1") return;
            if (panel != null && panel.IsOpen && panel.Flow != null &&
                (panel.Flow.State == ScanFlowState.Failed || panel.Flow.State == ScanFlowState.Selecting && panel.Flow.RefineError.IsError))
            {
                // Stay in the capture: the practice vase is put in the box and the card is ready for another Capture.
                _submitted = false;
                _runner.NoteScanInFlight(false);
                _captureBaseline = panel.Progress.CaptureCount;   // the capture that failed is not the user's next Capture
                var old = panel.FrameSource;
                if (old != null) old.Stop();
                panel.PromptWithBox = false;
                _runner.PracticeObjectShown = true;
                InstallHologram(panel);
                panel.FrameSource.Start();
                panel.Flow.Retake();
                if (_runner.Flow.StepIndex > 2) _runner.RewindTo(2);
            }
            else if (panel != null || _runner.Flow.StepIndex > 1)
            {
                // The card is gone or not at a failure: back to pressing Capture, which now opens a canned card.
                OnRetryScan();
            }
        }

        /// <summary>A practice vase stands in the user's box (the canned server cannot segment the user's own object), and the photo and the mask are rendered from the user's eye.</summary>
        private void InstallHologram(ScanCard panel)
        {
            // A practice vase stands in the user's box (the canned server cannot segment the user's own object), and the photo and
            // the mask are rendered from the user's eye.
            ScanAnchor box = panel.ScannedBox;
            Vector3 basePosition = box.Center - Vector3.up * box.Size.y * 0.5f;
            // A new card may be on a different box: the vase moves to it (a fresh holder, loaded once).
            _props.DestroyHologram();
            var hologram = _props.EnsureHologram(basePosition, box.Size);
            LoadModelInto(hologram.gameObject, TutorialAssets.LoadSampleModel(), Mathf.Min(box.Size.y * 0.85f, Mathf.Min(box.Size.x, box.Size.z) * 1.2f),
                standOnY: basePosition.y);
            var canned = _scan.Canned;
            panel.FrameSource = new TutorialHologramFrameSource(() => panel != null ? panel.Eye : null, hologram.gameObject,
                mask => { if (canned != null) canned.Segmenter = mask; });
            // A copy already in the box keeps the vase hidden: the new card must not pop it back in front of the copy.
            if (_props.Copy != null) ConcealPracticeVase();
        }

        private void OnRequestFailed(ScanError error)
        {
            if (WantFallback(error)) return;
            // A point that found nothing (the practice server's "No object at those points") keeps the photo and the points: the card
            // says so and the user points again. It is not the step failing.
            if (_openPanel != null && _openPanel.Flow != null && _openPanel.Flow.State == ScanFlowState.Selecting) return;
            _runner.ReportScanFailure(ScanFailureText.ToUserMessage(error));
        }

        private void CloseScanPanel()
        {
            if (_openPanel == null) { _openPanel = null; return; }
            _openPanel.RequestFailed -= OnRequestFailed;
            var panel = _openPanel;
            _openPanel = null;
            panel.Close();
        }

        /// <summary>"Try again" after a scan failure: closes the panel and goes back to pressing Capture on the box.</summary>
        private void OnRetryScan()
        {
            CloseScanPanel();
            if (_scan != null) _scan.CancelInFlight();
            ResetScanProgress();
            // The copy and the virtual object go. The practice vase, hidden while the copy stood in its place, is shown again.
            _props.DestroyCopy();
            _props.DestroyShared();
            RevealPracticeVase();
            // The virtual object's pad (and its watch) go with it, so a second scan makes exactly one pad again.
            Unwatch();
            _props.ClearTargets();
            _runner.ClearScanProblem();
            _runner.RewindTo(1);
        }

        private void OnScanResult(ScanReconstructionResult result)
        {
            try
            {
                // A late result after the user left the scan task is dropped: it must not appear in a later task.
                // Nor is one that this visit did not ask for (a request cancelled by Try again, or a second result of one request).
                if (_ended || !_runner.Flow.Running || _runner.Flow.Current.Id != "3.1" || !_submitted || _copyReady)
                {
                    try { File.Delete(result.ModelPath); } catch { /* best effort */ }
                    return;
                }
                if (!File.Exists(result.ModelPath) || new FileInfo(result.ModelPath).Length == 0)
                {
                    // An empty result is a failure (the server could not segment the vase): the canned server takes over.
                    try { File.Delete(result.ModelPath); } catch { /* best effort */ }
                    if (WantFallback()) return;
                }
                byte[] bytes = File.ReadAllBytes(result.ModelPath);
                try { File.Delete(result.ModelPath); } catch { /* best effort */ }
                // The user's own scanned object stays in the box they drew for the rest of the task (it is theirs; it is not what the last
                // step places). The box is captured now: the model loads over several frames, and a later card must not move this copy.
                // The stand-in and the loaded mesh are both scaled to that box. A grab already holding the copy is left where it is.
                _copySeat = CopySeat.Capture(_hasCopyBox, _copyBox, _copyBoxRotation);
                var copy = _props.EnsureCopy(Vector3.zero);
                TutorialPracticeProps.AddPlaceholderBody(copy.gameObject, Vector3.one);
                FitCopyToBox(copy.gameObject, _copySeat);
                _copyReady = true;
                // The copy stays where the practice vase stood. Hiding the vase makes the copy read as that vase becoming solid.
                ConcealPracticeVase();
                _runner.NoteScanInFlight(false);
                LoadModelInto(copy.gameObject, bytes, 0f, seat: _copySeat);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tutorial] Could not show the scan result: " + e.Message);
                if (WantFallback()) return;
                if (_runner != null) _runner.ReportScanFailure(ScanFailureText.Fallback);
            }
        }

        /// <summary>
        /// Hides the practice vase once the canned copy is in the box, so the two do not occupy the same place. The copy
        /// is already seated there. Real scans have no practice vase.
        /// </summary>
        private void ConcealPracticeVase()
        {
            if (_scan == null || _scan.Mode != TutorialScanMode.Canned) return;
            var hologram = _props.Hologram;
            if (hologram != null) hologram.gameObject.SetActive(false);
        }

        /// <summary>Shows the practice vase again after the copy that replaced it is gone.</summary>
        private void RevealPracticeVase()
        {
            var hologram = _props.Hologram;
            if (hologram != null) hologram.gameObject.SetActive(true);
        }

        /// <summary>
        /// The box a copy was made for. Taken by value when the copy is created, so the load that finishes later seats
        /// that box even if the user has opened another card since.
        /// </summary>
        private struct CopySeat
        {
            public bool Has;
            public ScanAnchor Box;
            public Quaternion Rotation;

            public static CopySeat Capture(bool has, ScanAnchor box, Quaternion rotation)
            {
                return new CopySeat { Has = has && box.HasSize, Box = box, Rotation = rotation };
            }
        }

        /// <summary>
        /// Scales the holder so its content fits the captured box, then centers it on the footprint and rests it on the
        /// bottom face. Without a box the holder goes to the old spot in front of the user. A copy the user is already
        /// holding keeps its place: the scale still updates, the pose does not.
        /// </summary>
        private void FitCopyToBox(GameObject holder, CopySeat seat)
        {
            if (holder == null) return;
            if (!seat.Has)
            {
                holder.transform.position = TutorialPracticeProps.SpotAhead(Eye, 1.0f, 0.35f, -0.5f);
                return;
            }
            if (!ScanBoxRest.TryContentBounds(holder.transform, out Bounds local))
                local = new Bounds(Vector3.zero, Vector3.zero);
            Vector3 extent = Vector3.Scale(new Vector3(
                Mathf.Abs(holder.transform.localScale.x),
                Mathf.Abs(holder.transform.localScale.y),
                Mathf.Abs(holder.transform.localScale.z)), local.size);
            holder.transform.localScale *= ScanBoxRest.UniformFit(extent, seat.Box.Size);
            var grab = holder.GetComponent<TutorialGrabbable>();
            if (grab != null && grab.Held) return;
            holder.transform.SetPositionAndRotation(
                ScanBoxRest.HolderPosition(seat.Box.Center, seat.Box.Size, seat.Rotation, local, holder.transform.localScale),
                seat.Rotation);
        }

        /// <summary>Moves the holder so its renderers' lowest point is at <paramref name="y"/>.</summary>
        private static void StandOn(GameObject holder, float y)
        {
            Bounds? measured = null;
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>())
            {
                if (renderer is LineRenderer || renderer is TrailRenderer) continue;
                if (measured == null) measured = renderer.bounds; else { var b = measured.Value; b.Encapsulate(renderer.bounds); measured = b; }
            }
            if (measured == null) return;
            holder.transform.position += Vector3.up * (y - measured.Value.min.y);
        }

        private static void FitToSize(GameObject holder, float metres)
        {
            Bounds? measured = null;
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>())
            {
                // The wire box of a virtual object is not part of the model.
                if (renderer is LineRenderer || renderer is TrailRenderer) continue;
                if (measured == null) measured = renderer.bounds; else { var b = measured.Value; b.Encapsulate(renderer.bounds); measured = b; }
            }
            if (measured == null) return;
            Bounds bounds = measured.Value;
            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (largest <= 1e-4f) return;
            holder.transform.localScale *= metres / largest;
        }

        // ---------------------------------------------------------------- chapter 4

        private void OnFindRoom()
        {
            bool real = _panel.ShowRoomListReadOnly(OnRoomTapped, OnNewRoomTapped);
            _runner.NoteRoomList(true, !real);
        }

        private void OnRoomTapped(string roomName)
        {
            _runner.ChooseRoom(roomName, false);
            _panel.HideRoomList();
            _runner.NoteRoomList(false, false);
            _panel.ShowToast(StatusTone.Neutral, "Demo only", TutorialRunner.DemoChoiceText);
        }

        private void OnNewRoomTapped()
        {
            _runner.ChooseRoom(null, true);
            _panel.HideRoomList();
            _runner.NoteRoomList(false, false);
            _panel.ShowToast(StatusTone.Neutral, "Demo only", TutorialRunner.DemoChoiceText);
        }
    }
}
