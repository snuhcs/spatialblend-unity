// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialBlend.Samples.Scanning
{
    /// <summary>Where the scan camera's permissions stand.</summary>
    public enum CameraPermissionState
    {
        /// <summary>Not checked yet.</summary>
        Unknown,
        /// <summary>A system prompt is up (or about to be); the answer arrives later.</summary>
        Requesting,
        Granted,
        /// <summary>The user said no, or the system answered without granting. <see cref="CameraPermissionGate.Retry"/> asks again.</summary>
        Denied,
    }

    /// <summary>The platform side of permissions, so the gate is testable without a headset.</summary>
    public interface ICameraPermissionBackend
    {
        bool IsGranted(string permission);
        /// <summary>Starts one system request for these permissions. <paramref name="finished"/> is called once, when the request ends.</summary>
        void Request(string[] permissions, Action finished);
        /// <summary>True while another component's request is still showing its prompt (a second request would clobber its callbacks).</summary>
        bool OtherRequestPending { get; }
    }

    /// <summary>
    /// The camera permission state machine. The truth is always <see cref="ICameraPermissionBackend.IsGranted"/>, polled by
    /// <see cref="Tick"/>; the callbacks only say a request ended. So a grant made by another component's prompt, in the system
    /// settings, or after a denial is seen, and an unrelated denial (the scene permission asked in the same batch) can never
    /// switch the camera off.
    /// </summary>
    public sealed class CameraPermissionGate
    {
        private readonly ICameraPermissionBackend _backend;
        private readonly string[] _required;
        private bool _requestOpen;
        private int _requestId;
        private double _requestedAt;

        /// <summary>A prompt whose callback never arrives is treated as ended after this long.</summary>
        public const double RequestTimeoutSeconds = 30.0;

        public CameraPermissionGate(ICameraPermissionBackend backend, IEnumerable<string> required)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _required = required.ToArray();
        }

        public CameraPermissionState State { get; private set; }
        public bool IsGranted => State == CameraPermissionState.Granted;
        public int RequestCount { get; private set; }

        /// <summary>The required permissions not granted right now.</summary>
        public string[] Missing() => _required.Where(p => !_backend.IsGranted(p)).ToArray();

        /// <summary>Call every frame (or every half second). Re-reads the grants and asks for what is missing when nothing is in flight.</summary>
        public void Tick(double now = 0.0)
        {
            if (_requestOpen && now - _requestedAt > RequestTimeoutSeconds) EndRequest(_requestId);
            if (Missing().Length == 0)
            {
                State = CameraPermissionState.Granted;
                return;
            }
            if (State == CameraPermissionState.Granted) State = CameraPermissionState.Unknown; // revoked in the system settings
            if (State == CameraPermissionState.Unknown && !_requestOpen && !_backend.OtherRequestPending) StartRequest(now);
            else if (State == CameraPermissionState.Unknown && _backend.OtherRequestPending) State = CameraPermissionState.Requesting;
            else if (State == CameraPermissionState.Requesting && !_requestOpen && !_backend.OtherRequestPending) State = CameraPermissionState.Denied;
        }

        /// <summary>The user pressed "Allow camera" after a denial: ask again.</summary>
        public void Retry(double now = 0.0)
        {
            if (Missing().Length == 0) { State = CameraPermissionState.Granted; return; }
            if (_requestOpen || _backend.OtherRequestPending) return;
            StartRequest(now);
        }

        private void StartRequest(double now)
        {
            var missing = Missing();
            _requestOpen = true;
            _requestedAt = now;
            int id = ++_requestId;
            RequestCount++;
            State = CameraPermissionState.Requesting;
            _backend.Request(missing, () => EndRequest(id));
        }

        private void EndRequest(int id)
        {
            if (id != _requestId || !_requestOpen) return; // a late callback of a request already closed by the timeout
            _requestOpen = false;
            State = Missing().Length == 0 ? CameraPermissionState.Granted : CameraPermissionState.Denied;
        }
    }
}
