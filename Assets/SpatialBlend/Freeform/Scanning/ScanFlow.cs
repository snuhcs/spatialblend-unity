// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SpatialBlend.Freeform.Scanning
{
    /// <summary>Where the capture of one real object is (scan UI Concept A).</summary>
    public enum ScanFlowState
    {
        /// <summary>Nothing is being captured.</summary>
        Idle = 0,
        /// <summary>The card is up on the box: the user looks at the object (the live view) and captures.</summary>
        Aiming,
        /// <summary>
        /// The photo is held; the user adds points on it (positive or negative), each one asks the segmenter for the mask of all of
        /// them (the points alone: never the drawn box), and the latest answer is shown until the user confirms it.
        /// </summary>
        Selecting,
        /// <summary>The user confirmed the mask; the reconstruction starts only when the user asks for it (<see cref="ScanFlow.Build"/>).</summary>
        Confirming,
        /// <summary>The server is building the model.</summary>
        Building,
        /// <summary>The model came back (the result sink places it).</summary>
        Done,
        /// <summary>Something failed; <see cref="ScanFlow.Error"/> says what. Retake goes back to Aiming.</summary>
        Failed,
    }

    /// <summary>
    /// The capture flow as a plain state machine over <see cref="ScanService"/>: capture, the user's points and the mask refined
    /// with each of them, the user's confirmation, then the reconstruction when the user asks for it. Nothing happens by a timer
    /// One segmentation request is in flight at a time: points added meanwhile are coalesced into one request
    /// sent when it returns, and an answer for points that have changed since is dropped (latest wins). It knows nothing about
    /// cameras or UI; the view feeds it a photo and points and shows its state. All calls belong to the main thread (the service
    /// raises its events from its Update).
    /// </summary>
    public sealed class ScanFlow : IDisposable
    {
        private readonly Func<ScanService> _service;
        private readonly List<ScanPoint> _points = new List<ScanPoint>();
        private ScanService _subscribed;
        private long _segmentHandle, _buildHandle;
        private bool _stale;

        public ScanFlow(Func<ScanService> service)
        {
            _service = service ?? (() => null);
        }

        public ScanFlowState State { get; private set; } = ScanFlowState.Idle;
        public ScanError Error { get; private set; }
        /// <summary>
        /// The captured photo as the server gets it (encoded JPEG or PNG; the server decodes either) and its size, from Capture until
        /// the flow goes back to Aiming or Idle. Encoded once at Capture; every Segment and the Reconstruct send these same bytes.
        /// </summary>
        public byte[] Photo { get; private set; }
        public int PhotoWidth { get; private set; }
        public int PhotoHeight { get; private set; }
        /// <summary>The user's points on the photo (server pixels, labelled positive or negative), in the order they were added.</summary>
        public IReadOnlyList<ScanPoint> Points => _points;
        /// <summary>The prompt of the last request sent.</summary>
        public ScanPrompt Prompt { get; private set; }
        /// <summary>The latest mask SAM returned for the current points (one byte per photo pixel, row 0 at the top), or null.</summary>
        public byte[] Mask { get; private set; }
        public int MaskWidth { get; private set; }
        public int MaskHeight { get; private set; }
        /// <summary>Whether the server said it used a box prompt (never, now that no box is sent).</summary>
        public bool MaskFromBox { get; private set; }
        public ScanAnchor? Anchor { get; private set; }
        /// <summary>The reconstruction's event handle while Building (the pending indicator and the sink key on it).</summary>
        public long BuildHandle => _buildHandle;
        /// <summary>A segmentation request is in flight.</summary>
        public bool Refining => _segmentHandle != 0;
        /// <summary>How many segmentation requests went out since the capture (the tests read it).</summary>
        public int SegmentRequests { get; private set; }
        /// <summary>
        /// Why the latest refinement failed (a timeout, a server error, a point on nothing), or none. The flow stays in Selecting with
        /// the points and the last good mask (one missed point must not throw the photo away); the next point,
        /// Undo or Clear asks again and clears it.
        /// </summary>
        public ScanError RefineError { get; private set; }
        /// <summary>Confirm does something: there is a mask for exactly the current points and nothing is in flight.</summary>
        public bool CanConfirm => State == ScanFlowState.Selecting && Mask != null && !Refining && !_stale && !RefineError.IsError;

        /// <summary>Raised after every state change.</summary>
        public event Action<ScanFlowState> Changed;
        /// <summary>Raised while Selecting when the points, the mask or the request in flight change.</summary>
        public event Action SelectionChanged;
        /// <summary>Raised when a refinement failed (the flow stays in Selecting; <see cref="RefineError"/> says why).</summary>
        public event Action<ScanError> RefineFailed;
        /// <summary>Raised when the Reconstruct request went out (the tutorial's "submitted" signal).</summary>
        public event Action<long> BuildStarted;

        /// <summary>Opens the flow for one box: Aiming.</summary>
        public void Begin(ScanAnchor? anchor)
        {
            Subscribe();
            CancelSegment();
            _buildHandle = 0;   // a reconstruction in flight keeps going for the result sink; this flow no longer follows it
            Clear();
            Anchor = anchor;
            Set(ScanFlowState.Aiming);
        }

        /// <summary>
        /// Holds the photo for the user's points: Selecting. Nothing is sent yet: segmentation starts at the first positive point,
        /// and every request carries the user's points alone (no box prompt, no box-only request after the photo).
        /// Nothing about the drawn box is checked. Returns false (and fails the flow) when scanning is off.
        /// </summary>
        public bool Capture(byte[] photo, int width, int height)
        {
            if (State != ScanFlowState.Aiming && State != ScanFlowState.Failed) return false;
            Subscribe();
            CancelSegment();
            Clear();
            Photo = photo;
            PhotoWidth = width;
            PhotoHeight = height;
            Set(ScanFlowState.Selecting);
            ScanService service = _service();
            if (service == null || service.ActiveWorkflow == ScanWorkflow.None)
                return Fail(new ScanError(ScanErrorCode.NotInitialized, "The scan service is not connected."));
            return true;
        }

        /// <summary>Adds a point (server pixels; its label says positive or negative) and asks for the mask of all of them.</summary>
        public bool AddPoint(ScanPoint point, double now)
        {
            if (State != ScanFlowState.Selecting) return false;
            if (point.Label != ScanPointLabel.Positive && point.Label != ScanPointLabel.Negative) return false;
            if (point.X < 0 || point.Y < 0 || point.X >= PhotoWidth || point.Y >= PhotoHeight) return false;
            _points.Add(point);
            Ask(now);
            return true;
        }

        /// <summary>Takes back the last point.</summary>
        public bool UndoPoint(double now)
        {
            if (State != ScanFlowState.Selecting || _points.Count == 0) return false;
            _points.RemoveAt(_points.Count - 1);
            Ask(now);
            return true;
        }

        /// <summary>Takes back every point (no point: no mask).</summary>
        public bool ClearPoints(double now)
        {
            if (State != ScanFlowState.Selecting || _points.Count == 0) return false;
            _points.Clear();
            Ask(now);
            return true;
        }

        /// <summary>Commits the mask shown: Confirming. Only when <see cref="CanConfirm"/>.</summary>
        public bool Confirm()
        {
            if (!CanConfirm) return false;
            Set(ScanFlowState.Confirming);
            return true;
        }

        /// <summary>Back from Confirming to the points (they and the mask are kept).</summary>
        public bool Back()
        {
            if (State != ScanFlowState.Confirming) return false;
            Set(ScanFlowState.Selecting);
            return true;
        }

        /// <summary>Back to Aiming with nothing kept (Retake).</summary>
        public void Retake()
        {
            if (State == ScanFlowState.Idle || State == ScanFlowState.Building || State == ScanFlowState.Done) return;
            CancelSegment();
            Clear();
            Set(ScanFlowState.Aiming);
        }

        /// <summary>Starts the reconstruction of the confirmed object (the card's 3D copy button). Only while Confirming.</summary>
        public void Build(double now)
        {
            if (State != ScanFlowState.Confirming) return;
            StartBuild(now);
        }

        /// <summary>Stops a reconstruction in flight (the server is not told; its answer is ignored). Back to Idle.</summary>
        public void CancelBuild()
        {
            if (State != ScanFlowState.Building) return;
            // Forget the handle first: the service raises the cancellation synchronously, and OnPending must not act on it.
            long handle = _buildHandle;
            _buildHandle = 0;
            ScanService service = _service();
            if (service != null && handle > 0) service.Cancel(handle);
            Clear();
            Set(ScanFlowState.Idle);
        }

        /// <summary>Closes the flow (the card closed, the box went away). A reconstruction in flight keeps going.</summary>
        public void Close()
        {
            CancelSegment();
            Clear();
            _buildHandle = 0;
            Set(ScanFlowState.Idle);
        }

        public void Dispose()
        {
            Unsubscribe();
        }

        // ------------------------------------------------------------------

        /// <summary>The prompt for the current points: the user's clicks alone (no box); null when it would select nothing.</summary>
        private ScanPrompt CurrentPrompt()
        {
            bool positive = false;
            foreach (ScanPoint p in _points) positive |= p.Label == ScanPointLabel.Positive;
            return positive ? ScanPrompt.ForClicks(_points) : null;
        }

        /// <summary>The points changed: ask for their mask now, or once the request in flight returns (its answer is then stale).</summary>
        private void Ask(double now)
        {
            if (CurrentPrompt() == null)
            {
                // Nothing to ask (no point, or negatives alone): no mask, and an answer in flight is not wanted.
                CancelSegment();
                _stale = false;
                RefineError = ScanError.None;
                SetMask(null, 0, 0, false);
                return;
            }
            if (Refining)
            {
                _stale = true;
                SelectionChanged?.Invoke();
                return;
            }
            SendSegment(now);
        }

        private bool SendSegment(double now)
        {
            ScanService service = _service();
            if (service == null || service.ActiveWorkflow == ScanWorkflow.None)
                return Fail(new ScanError(ScanErrorCode.NotInitialized, "The scan service is not connected."));
            ScanPrompt prompt = CurrentPrompt();
            if (prompt == null) return false;
            if (!service.TryRequestSegment(Photo, PhotoWidth, PhotoHeight, prompt, now, out long handle, out ScanError error))
            {
                RefineError = error.IsError ? error : new ScanError(ScanErrorCode.ServerFailed, "The request could not be sent.");
                SelectionChanged?.Invoke();
                RefineFailed?.Invoke(RefineError);
                return false;
            }
            Prompt = prompt;
            _segmentHandle = handle;
            _stale = false;
            RefineError = ScanError.None;
            SegmentRequests++;
            SelectionChanged?.Invoke();
            return true;
        }

        private void SetMask(byte[] mask, int width, int height, bool fromBox)
        {
            Mask = mask;
            MaskWidth = width;
            MaskHeight = height;
            MaskFromBox = fromBox;
            SelectionChanged?.Invoke();
        }

        private void StartBuild(double now)
        {
            ScanService service = _service();
            if (service == null)
            {
                Fail(new ScanError(ScanErrorCode.NotInitialized, "The scan service is not connected."));
                return;
            }
            if (!service.TryRequestReconstruct(Photo, Mask, MaskWidth, MaskHeight, Anchor, now, out long handle, out ScanError error))
            {
                Fail(error);
                return;
            }
            _buildHandle = handle;
            Set(ScanFlowState.Building);
            BuildStarted?.Invoke(handle);
        }

        private void OnSegment(ScanSegmentResult result)
        {
            if (result == null || result.EventHandle != _segmentHandle || _segmentHandle == 0 || State != ScanFlowState.Selecting) return;
            _segmentHandle = 0;
            // Sent from the service's Update, which raised this result: its time is now.
            ScanService service = _service();
            double now = service != null ? service.UpdateTime : 0.0;
            if (_stale)
            {
                // The points changed while it was out: its mask is for old points. The current points are asked for instead.
                _stale = false;
                Ask(now);
                return;
            }
            if (!result.Success)
            {
                // Stay with the points: the last good mask stays, the card says what went wrong, and the next point asks again.
                RefineError = result.Error.IsError ? result.Error : new ScanError(ScanErrorCode.ServerFailed, "The object could not be found.");
                SelectionChanged?.Invoke();
                RefineFailed?.Invoke(RefineError);
                return;
            }
            SetMask(result.Mask, result.Width, result.Height, result.UsedBox);
        }

        private void OnPending(ScanPendingEvent pending)
        {
            if (pending.Kind != ScanKind.Reconstruct || pending.EventHandle != _buildHandle || !pending.IsTerminal) return;
            if (State != ScanFlowState.Building) return;
            _buildHandle = 0;
            switch (pending.Phase)
            {
                case ScanPendingPhase.Succeeded:
                    Set(ScanFlowState.Done);
                    break;
                case ScanPendingPhase.Cancelled:
                    Clear();
                    Set(ScanFlowState.Idle);
                    break;
                default:
                    Fail(pending.Error);
                    break;
            }
        }

        private bool Fail(ScanError error)
        {
            Error = error.IsError ? error : new ScanError(ScanErrorCode.ServerFailed, "The scan failed.");
            Set(ScanFlowState.Failed);
            return false;
        }

        private void CancelSegment()
        {
            if (_segmentHandle <= 0) return;
            // Forget the handle first: the service raises the cancelled result synchronously, and OnSegment must not take it
            // for a failure (Retake and Close would flash Failed before Aiming or Idle).
            long handle = _segmentHandle;
            _segmentHandle = 0;
            ScanService service = _service();
            if (service != null) service.Cancel(handle);
        }

        private void Clear()
        {
            Error = ScanError.None;
            Photo = null;
            PhotoWidth = PhotoHeight = 0;
            _points.Clear();
            _stale = false;
            SegmentRequests = 0;
            RefineError = ScanError.None;
            Prompt = null;
            Mask = null;
            MaskWidth = MaskHeight = 0;
            MaskFromBox = false;
        }

        private void Set(ScanFlowState state)
        {
            State = state;
            Changed?.Invoke(state);
        }

        private void Subscribe()
        {
            ScanService service = _service();
            if (service == _subscribed) return;
            Unsubscribe();
            _subscribed = service;
            if (_subscribed == null) return;
            _subscribed.SegmentCompleted += OnSegment;
            _subscribed.PendingChanged += OnPending;
        }

        private void Unsubscribe()
        {
            if (_subscribed == null) return;
            _subscribed.SegmentCompleted -= OnSegment;
            _subscribed.PendingChanged -= OnPending;
            _subscribed = null;
        }
    }
}
