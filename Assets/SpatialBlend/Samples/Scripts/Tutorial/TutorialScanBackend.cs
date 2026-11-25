// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.Tutorial
{
    public enum TutorialScanMode
    {
        /// <summary>Not prepared, or no scan could be started at all.</summary>
        None,
        /// <summary>No scan server answers: a sample photo and a sample model with a short fake wait.</summary>
        Canned,
        /// <summary>The app's scan service is connected: the scan is real and the result is loaded locally.</summary>
        Real,
    }

    /// <summary>
    /// Chapter 3's scan: the real server when the app's scan service is connected to one, the canned server
    /// otherwise, and the canned server again the moment the real one fails at any point (see <see cref="FallBackToCanned"/>), so
    /// the tutorial never depends on a server. In every case the tutorial takes the result sink for its own run: the result
    /// becomes a local practice object, never a shared one.
    /// </summary>
    public sealed class TutorialScanBackend : IScanResultSink
    {
        private readonly ITutorialClock _clock;
        private readonly Func<byte[]> _modelBytes;
        private readonly string _outputDirectory;
        private readonly Func<CannedSegmenter> _segmenter;
        private SessionManager _manager;
        private IScanResultSink _previousSink;
        private bool _hadSink;
        private CannedScanTransport _canned;
        private ScanService _service;
        private readonly List<long> _handles = new List<long>();
        // The tutorial's own canned service, used when the app's service is absent, half-connected, or failed during the run.
        private ScanService _ownService;
        private CannedScanTransport _ownCanned;
        private bool _fellBack;
        private readonly List<(ScanService service, long handle)> _ownHandles = new List<(ScanService, long)>();

        public TutorialScanBackend(ITutorialClock clock, Func<byte[]> modelBytes, string outputDirectory, Func<CannedSegmenter> segmenter = null)
        {
            _segmenter = segmenter;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _modelBytes = modelBytes ?? throw new ArgumentNullException(nameof(modelBytes));
            _outputDirectory = outputDirectory ?? string.Empty;
        }

        public TutorialScanMode Mode { get; private set; }
        public CannedScanTransport Canned => _ownCanned ?? _canned;
        /// <summary>True once a real scan failed and the run moved to the canned server (or the app's service was unusable from the start).</summary>
        public bool FellBack => _fellBack;

        /// <summary>The service the scan card must use: the tutorial's own canned one after a fallback, else the app's.</summary>
        public ScanService ActiveService => _ownService ?? (_manager != null ? _manager.Service : null);

        public event Action<ScanReconstructionResult> ResultReady;
        public event Action<ScanFailure> Failed;

        /// <summary>Takes the result sink and makes sure a scan service runs. Returns the mode.</summary>
        public TutorialScanMode Prepare(SessionManager manager)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            if (_manager != null) return Mode;
            _manager = manager;
            _previousSink = manager.ResultSink;
            _hadSink = true;
            manager.ResultSink = this;

            if (manager.IsInitialized)
            {
                Mode = TutorialScanMode.Real;
            }
            else if (manager.Service != null)
            {
                // A service exists but connected no pipeline (a config is present and the server is down): it cannot scan, and
                // InitializeWith would keep it. The tutorial runs its own canned service beside it and leaves the app's alone.
                Mode = StartOwnCanned() ? TutorialScanMode.Canned : TutorialScanMode.None;
                _fellBack = Mode == TutorialScanMode.Canned;
            }
            else
            {
                _canned = new CannedScanTransport(_clock, _modelBytes, segmenter: _segmenter != null ? _segmenter() : null);
                Mode = manager.InitializeWith(_canned, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_outputDirectory))
                    ? TutorialScanMode.Canned
                    : TutorialScanMode.None;
            }
            _service = manager.Service;
            // Every request made during the tutorial is remembered, so none of them can outlive it (see Release).
            if (_service != null) _service.Submitted += OnSubmitted;
            return Mode;
        }

        /// <summary>Call every frame: lets the canned "server" answer.</summary>
        public void Tick() => Tick(Time.realtimeSinceStartupAsDouble);

        /// <summary><paramref name="serviceNow"/> is the time base the scan card uses for its requests (realtime since startup).</summary>
        public void Tick(double serviceNow)
        {
            if (_canned != null) _canned.Tick();
            if (_ownCanned != null) _ownCanned.Tick();
            if (_ownService != null) _ownService.Update(serviceNow);
        }

        private bool StartOwnCanned()
        {
            _ownCanned = new CannedScanTransport(_clock, _modelBytes, segmenter: _segmenter != null ? _segmenter() : null);
            var own = new ScanService(_ownCanned, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_outputDirectory), new OwnSink(this));
            if (!own.Initialize(out _))
            {
                own.Dispose();
                _ownCanned = null;
                return false;
            }
            own.Submitted += submission => _ownHandles.Add((own, submission.EventHandle));
            _ownService = own;
            return true;
        }

        /// <summary>
        /// The real scan failed (a submit error, a connect or HTTP failure, the request timeout, an empty or failed result): the run
        /// moves to the canned server so the user can go on. Requests still open on the real service are cancelled and anything they
        /// report later is dropped. Returns true when the canned service now runs (also when it already did).
        /// </summary>
        public bool FallBackToCanned()
        {
            if (_manager == null) return false;
            if (_fellBack || Mode == TutorialScanMode.Canned) return _ownService != null || _canned != null;
            // Mark first: the cancellations below report through the app's service to this sink, and are dropped.
            _fellBack = true;
            CancelInFlight();
            if (!StartOwnCanned()) { _fellBack = false; return false; }
            Mode = TutorialScanMode.Canned;
            return true;
        }

        private void OnSubmitted(ScanSubmission submission) => _handles.Add(submission.EventHandle);

        private sealed class OwnSink : IScanResultSink
        {
            private readonly TutorialScanBackend _owner;
            public OwnSink(TutorialScanBackend owner) => _owner = owner;
            public void OnScanReconstructed(ScanReconstructionResult result) => _owner.Accept(result);
            public void OnScanFailed(ScanFailure failure) => _owner.Reject(failure);
        }

        /// <summary>Cancels the requests this run made that are still in flight (leaving the scan task, Try again), so a late result cannot show up in a later visit.</summary>
        public void CancelInFlight()
        {
            if (_service != null)
            {
                foreach (long handle in _handles) _service.Cancel(handle);
                _handles.Clear();
            }
            foreach (var (service, handle) in _ownHandles.ToArray()) service.Cancel(handle);
            _ownHandles.Clear();
        }

        /// <summary>
        /// Gives the sink back and drops the canned service, so the app is exactly as the tutorial found it. A request still in flight
        /// is cancelled first, while this backend is still the sink: a late result must never reach the app's own sink, which would
        /// load it as a real object that the app then shares. (The app's real service keeps running; only the tutorial's requests end.)
        /// </summary>
        public void Release()
        {
            if (_manager == null) return;
            if (_service != null)
            {
                _service.Submitted -= OnSubmitted;
                foreach (long handle in _handles) _service.Cancel(handle);
            }
            _handles.Clear();
            foreach (var (service, handle) in _ownHandles) service.Cancel(handle);
            _ownHandles.Clear();
            if (_ownService != null) { _ownService.Dispose(); _ownService = null; }
            _ownCanned = null;
            _fellBack = false;
            _service = null;
            if (_manager.ResultSink == (IScanResultSink)this) _manager.ResultSink = _hadSink ? _previousSink : null;
            if (_canned != null) _manager.ResetService();
            _canned = null;
            _manager = null;
            _previousSink = null;
            Mode = TutorialScanMode.None;
        }

        // The manager's sink: the app's service (real, or the canned one started through InitializeWith). After a fallback the
        // app's service is abandoned, and whatever it still reports is not the tutorial's business.
        public void OnScanReconstructed(ScanReconstructionResult result)
        {
            if (_fellBack) return;
            Accept(result);
        }

        public void OnScanFailed(ScanFailure failure)
        {
            if (_fellBack) return;
            Reject(failure);
        }

        private void Accept(ScanReconstructionResult result)
        {
            if (result != null && ResultReady != null) ResultReady(result);
        }

        private void Reject(ScanFailure failure)
        {
            if (failure != null && Failed != null) Failed(failure);
        }
    }
}
