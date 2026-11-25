// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using AOT;
using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend.Internal
{
    /// <summary>
    /// IScanSessionTransport over libspatialblend_session. It is the only place that calls
    /// SessionLibrary. A missing library surfaces as the DllNotFoundException that
    /// ScanService reports as a TransportFault. A library without the TLS entry point becomes a
    /// ScanTransportUnsupportedException when tls is requested.
    /// </summary>
    internal sealed class NativeScanSessionTransport : IScanSessionTransport
    {
        // IL2CPP needs a static callback. A static field also keeps the delegate alive.
        private static readonly OnInferenceCompleteDelegate NativeCallback = OnNativeComplete;
        private static readonly ConcurrentDictionary<long, Action<ScanCompletion>> Subscribers =
            new ConcurrentDictionary<long, Action<ScanCompletion>>();

        private readonly int _connectTimeoutMs;

        public NativeScanSessionTransport(int connectTimeoutMs)
        {
            _connectTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : 5000;
        }

        public long CreateSession(ScanKind kind)
        {
            switch (kind)
            {
                case ScanKind.Segment:
                    return SessionLibrary.spatialblend_inference_session_create(InferenceType.Sam3SegOnly);
                case ScanKind.Reconstruct:
                    return SessionLibrary.spatialblend_inference_session_create(InferenceType.Sam3dObjectOnly);
                default:
                    return 0;
            }
        }

        public bool RegisterCompletion(long session, Action<ScanCompletion> onCompletion)
        {
            if (onCompletion == null) return false;
            Subscribers[session] = onCompletion;
            if (SessionLibrary.spatialblend_register_on_inference_complete(session, NativeCallback)) return true;
            Subscribers.TryRemove(session, out _);
            return false;
        }

        public bool Connect(long session, string address, bool useTls, out string error)
        {
            long eventHandle;
            if (useTls)
            {
                try
                {
                    eventHandle = SessionLibrary.spatialblend_inference_session_connect_tls(session, address);
                }
                catch (EntryPointNotFoundException)
                {
                    // An older library has no TLS symbol. Never fall back to plaintext.
                    throw new ScanTransportUnsupportedException(ScanTransportUnsupportedException.TlsNeedsNewerLibrary);
                }
            }
            else
            {
                eventHandle = SessionLibrary.spatialblend_inference_session_connect(session, address);
            }
            if (SessionLibrary.spatialblend_try_wait_for_event(eventHandle, _connectTimeoutMs))
            {
                error = null;
                return true;
            }
            string reason = SessionLibrary.spatialblend_get_error_message(eventHandle);
            error = string.IsNullOrWhiteSpace(reason) ? $"no answer from {address} within {_connectTimeoutMs} ms" : reason;
            return false;
        }

        public long Submit(long session, ScanTransportRequest request)
        {
            switch (request.Kind)
            {
                case ScanKind.Segment:
                    return SessionLibrary.InferenceSegment(session, request.ClientId, request.ImageBytes,
                        request.Points, request.OutputPath);
                case ScanKind.Reconstruct:
                    return SessionLibrary.InferenceReconstruct(session, request.ClientId, request.ImageBytes,
                        request.MaskBytes, request.MaskWidth, request.MaskHeight, request.OutputPath);
                default:
                    return 0;
            }
        }

        public void DestroySession(long session)
        {
            Subscribers.TryRemove(session, out _);
            SessionLibrary.spatialblend_inference_session_destroy(session);
        }

        [MonoPInvokeCallback(typeof(OnInferenceCompleteDelegate))]
        private static void OnNativeComplete(long sessionHandle, long eventHandle, bool success, string message)
        {
            // Runs on a native thread. ScanService only enqueues.
            if (Subscribers.TryGetValue(sessionHandle, out Action<ScanCompletion> subscriber))
                subscriber(new ScanCompletion(sessionHandle, eventHandle, success, message));
        }
    }
}
