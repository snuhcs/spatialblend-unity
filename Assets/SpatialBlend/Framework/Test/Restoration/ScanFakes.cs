// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend.Tests
{
    /// <summary>A scan session library that never leaves the process. No socket, no DllImport.</summary>
    internal sealed class FakeScanTransport : IScanSessionTransport
    {
        public readonly List<ScanKind> Created = new List<ScanKind>();
        public readonly List<string> ConnectAttempts = new List<string>();
        public readonly List<bool> ConnectTlsFlags = new List<bool>();
        public bool TlsUnsupported; // models a session library without the TLS entry point
        public readonly List<long> Destroyed = new List<long>();
        public readonly List<ScanTransportRequest> Submitted = new List<ScanTransportRequest>();
        /// <summary>The event handle returned for each entry of <see cref="Submitted"/>.</summary>
        public readonly List<long> Handles = new List<long>();
        public readonly Dictionary<long, Action<ScanCompletion>> Callbacks = new Dictionary<long, Action<ScanCompletion>>();
        public readonly HashSet<string> RefusedAddresses = new HashSet<string>();
        public string EchoAddressInError; // format with {0} = address, to model a leaking transport
        public Exception ConnectThrows;
        public Exception CreateThrows;
        public Exception SubmitThrows;
        public bool RefuseCallbackRegistration;
        public bool RejectSubmit;
        private long _nextSession = 100;
        private long _nextEvent = 1000;

        public long LastSessionFor(ScanKind kind) => _sessionsByKind[kind];
        private readonly Dictionary<ScanKind, long> _sessionsByKind = new Dictionary<ScanKind, long>();
        private readonly Dictionary<long, ScanKind> _kindBySession = new Dictionary<long, ScanKind>();

        public long CreateSession(ScanKind kind)
        {
            if (CreateThrows != null) throw CreateThrows;
            Created.Add(kind);
            long handle = _nextSession++;
            _sessionsByKind[kind] = handle;
            _kindBySession[handle] = kind;
            return handle;
        }

        public bool RegisterCompletion(long session, Action<ScanCompletion> onCompletion)
        {
            if (RefuseCallbackRegistration) return false;
            Callbacks[session] = onCompletion;
            return true;
        }

        public bool Connect(long session, string address, bool useTls, out string error)
        {
            ConnectAttempts.Add(address);
            ConnectTlsFlags.Add(useTls);
            if (useTls && TlsUnsupported) throw new ScanTransportUnsupportedException("native detail");
            if (ConnectThrows != null) throw ConnectThrows;
            if (RefusedAddresses.Contains(address))
            {
                error = EchoAddressInError != null ? string.Format(EchoAddressInError, address) : "connection refused";
                return false;
            }
            error = null;
            return true;
        }

        public long Submit(long session, ScanTransportRequest request)
        {
            if (SubmitThrows != null) throw SubmitThrows;
            if (RejectSubmit) return 0;
            Submitted.Add(request);
            Handles.Add(_nextEvent);
            return _nextEvent++;
        }

        public void DestroySession(long session) => Destroyed.Add(session);

        /// <summary>The server answers: runs the callback the service registered for the request's session.</summary>
        public void Complete(ScanKind kind, long eventHandle, bool success, string message = "")
        {
            long session = _sessionsByKind[kind];
            Callbacks[session](new ScanCompletion(session, eventHandle, success, message));
        }
    }

    internal sealed class FakeScanFiles : IScanFileSystem
    {
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        public readonly List<string> Deleted = new List<string>();

        public byte[] ReadAllBytes(string path)
        {
            if (!Files.TryGetValue(path, out byte[] bytes)) throw new System.IO.FileNotFoundException(path);
            return bytes;
        }

        public void Delete(string path)
        {
            Deleted.Add(path);
            Files.Remove(path);
        }
    }

    internal sealed class RecordingScanSink : IScanResultSink
    {
        public readonly List<ScanReconstructionResult> Results = new List<ScanReconstructionResult>();
        public readonly List<ScanFailure> Failures = new List<ScanFailure>();
        public bool ThrowOnResult;

        public void OnScanReconstructed(ScanReconstructionResult result)
        {
            Results.Add(result);
            if (ThrowOnResult) throw new InvalidOperationException("sink failed");
        }

        public void OnScanFailed(ScanFailure failure) => Failures.Add(failure);
    }
}
#endif
