// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SpatialBlend.Freeform.Scanning
{
    public enum ScanPendingPhase
    {
        Started = 0,
        Succeeded,
        Failed,
        TimedOut,
        Cancelled,
    }

    /// <summary>Which scan pipeline is available.</summary>
    public enum ScanWorkflow
    {
        None = 0,
        /// <summary>Segment first, show the mask, then reconstruct from image and mask (v1 "Seg").</summary>
        SegmentThenReconstruct,
        // 2 was OneShot (v1 image and points in one request), retired.
    }

    /// <summary>Whether the connected server takes a box prompt (known after the first Segment that carried one).</summary>
    public enum ScanBoxSupport
    {
        /// <summary>Not asked yet: the next prompt is sent with its box.</summary>
        Unknown = 0,
        /// <summary>The server answered "box:1".</summary>
        Supported,
        /// <summary>The server answered a box request without "box:1" (an older server took the corners as clicks).</summary>
        Unsupported,
    }

    /// <summary>Raised once when the session library accepted a request.</summary>
    public readonly struct ScanSubmission
    {
        public readonly long EventHandle;
        public readonly ScanKind Kind;
        /// <summary>Segment: the mask file. Reconstruct: the output path prefix.</summary>
        public readonly string OutputPath;
        public readonly bool HasAnchor;
        public readonly ScanAnchor Anchor;

        public ScanSubmission(long eventHandle, ScanKind kind, string outputPath, bool hasAnchor, ScanAnchor anchor)
        {
            EventHandle = eventHandle;
            Kind = kind;
            OutputPath = outputPath;
            HasAnchor = hasAnchor;
            Anchor = anchor;
        }
    }

    /// <summary>Drives the "pending" indicator and the scan sounds. One Started, then one terminal event.</summary>
    public readonly struct ScanPendingEvent
    {
        public readonly ScanPendingPhase Phase;
        public readonly long EventHandle;
        public readonly ScanKind Kind;
        public readonly bool HasAnchor;
        public readonly ScanAnchor Anchor;
        public readonly ScanError Error;

        public ScanPendingEvent(ScanPendingPhase phase, long eventHandle, ScanKind kind, bool hasAnchor,
                                ScanAnchor anchor, ScanError error)
        {
            Phase = phase;
            EventHandle = eventHandle;
            Kind = kind;
            HasAnchor = hasAnchor;
            Anchor = anchor;
            Error = error;
        }

        public bool IsTerminal => Phase != ScanPendingPhase.Started;
    }

    /// <summary>
    /// SAM3 segmentation and SAM3D reconstruction over the session library. It replaces v1
    /// SessionManager plus the three session handlers. All native work goes through
    /// IScanSessionTransport. Request, Update and Cancel belong to one thread (the main thread).
    /// Only the transport's completion callback may arrive on another thread.
    /// </summary>
    public sealed class ScanService : IDisposable
    {
        private sealed class RequestContext
        {
            public string OutputPath;
            public int ImageWidth;
            public int ImageHeight;
            public bool HasAnchor;
            public ScanAnchor Anchor;
            public bool SentBox;
        }

        private readonly IScanSessionTransport _transport;
        private readonly IScanFileSystem _files;
        private readonly ScanSessionConfig _config;
        private readonly IScanResultSink _sink;
        private readonly Func<DateTime> _utcNow;
        private readonly ScanPendingTracker _tracker = new ScanPendingTracker();
        private readonly ConcurrentQueue<ScanCompletion> _inbox = new ConcurrentQueue<ScanCompletion>();
        private readonly Dictionary<ScanKind, long> _sessions = new Dictionary<ScanKind, long>();
        private bool _disposed;

        public event Action<ScanSubmission> Submitted;
        public event Action<ScanPendingEvent> PendingChanged;
        public event Action<ScanSegmentResult> SegmentCompleted;
        public event Action<string> Diagnostic;

        public ScanService(IScanSessionTransport transport, IScanFileSystem files, ScanSessionConfig config,
                           IScanResultSink sink, Func<DateTime> utcNow = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _sink = sink;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public int PendingCount => _tracker.PendingCount;

        public bool IsReady(ScanKind kind) => _sessions.ContainsKey(kind);

        public bool IsPending(long eventHandle) => _tracker.IsPending(eventHandle);

        /// <summary>The two-step pipeline when both sessions are connected.</summary>
        public ScanWorkflow ActiveWorkflow =>
            IsReady(ScanKind.Segment) && IsReady(ScanKind.Reconstruct) ? ScanWorkflow.SegmentThenReconstruct
            : ScanWorkflow.None;

        /// <summary>
        /// Whether this server takes box prompts. Unknown until a Segment with a box comes back; then Supported when the
        /// answer says "box:1", Unsupported otherwise. Once Unsupported, prompts are sent with their fallback clicks.
        /// </summary>
        public ScanBoxSupport BoxSupport { get; private set; } = ScanBoxSupport.Unknown;

        /// <summary>
        /// Creates and connects one session per kind that has an address (see
        /// ScanSessionConfig.AddressesFor). Returns true when at least one kind is ready. A kind that
        /// fails stays unavailable.
        /// </summary>
        public bool Initialize(out ScanError error)
        {
            if (!_config.Validate(out error)) return false;

            ScanError lastFailure = ScanError.None;
            foreach (ScanKind kind in new[] { ScanKind.Segment, ScanKind.Reconstruct })
            {
                if (_sessions.ContainsKey(kind)) continue;
                if (!TryOpenSession(kind, out ScanError openError)) lastFailure = openError;
                // A new Segment session may be a different server: ask again whether it takes a box.
                else if (kind == ScanKind.Segment) BoxSupport = ScanBoxSupport.Unknown;
            }

            if (_sessions.Count > 0)
            {
                error = ScanError.None;
                return true;
            }
            error = lastFailure.IsError
                ? lastFailure
                : new ScanError(ScanErrorCode.ConnectFailed, "No session could be opened.");
            return false;
        }

        /// <summary>A Segment request with clicks only (labels as given).</summary>
        public bool TryRequestSegment(byte[] image, int imageWidth, int imageHeight, ScanPoint[] points, double now,
                                      out long eventHandle, out ScanError error)
        {
            ScanBuildResult build = ScanRequestBuilder.BuildSegment(_config, image, points, _utcNow());
            bool sentBox = points != null && Array.Exists(points, p => p.IsBoxCorner);
            return Submit(build, ScanKind.Segment, now, imageWidth, imageHeight, null, out eventHandle, out error, sentBox);
        }

        /// <summary>
        /// A Segment request from a prompt: its box is sent as a box unless this server is known not to take one, in which
        /// case the prompt's fallback clicks go instead. When a box answer comes back without "box:1", the result fails with
        /// <see cref="ScanErrorCode.BoxPromptUnsupported"/> and its mask is dropped; calling this again sends the clicks.
        /// </summary>
        public bool TryRequestSegment(byte[] image, int imageWidth, int imageHeight, ScanPrompt prompt, double now,
                                      out long eventHandle, out ScanError error)
        {
            if (prompt == null)
            {
                eventHandle = 0;
                error = new ScanError(ScanErrorCode.NoPoints, "No prompt.");
                return false;
            }
            return TryRequestSegment(image, imageWidth, imageHeight, prompt.ToWire(BoxSupport != ScanBoxSupport.Unsupported),
                now, out eventHandle, out error);
        }

        public bool TryRequestReconstruct(byte[] image, byte[] mask, int maskWidth, int maskHeight,
                                          ScanAnchor? anchor, double now, out long eventHandle, out ScanError error)
        {
            ScanBuildResult build = ScanRequestBuilder.BuildReconstruct(
                _config, image, mask, maskWidth, maskHeight, _utcNow());
            return Submit(build, ScanKind.Reconstruct, now, maskWidth, maskHeight, anchor, out eventHandle, out error);
        }

        /// <summary>The time of the latest <see cref="Update"/>: the completions it raises happen "now" (a retry sent from one uses it).</summary>
        public double UpdateTime { get; private set; }

        /// <summary>Drains completions, then expires overdue requests. Call once per frame.</summary>
        public void Update(double now)
        {
            if (_disposed) return;
            UpdateTime = now;

            while (_inbox.TryDequeue(out ScanCompletion completion))
                HandleCompletion(completion);

            foreach (ScanPendingEntry entry in _tracker.Expire(now))
            {
                DiscardMask(entry);
                var error = new ScanError(ScanErrorCode.Timeout,
                    $"No response within {entry.Deadline - entry.StartedAt:0.#} s.");
                Finish(entry, ScanPendingPhase.TimedOut, error);
            }
        }

        public bool Cancel(long eventHandle)
        {
            if (!_tracker.Cancel(eventHandle, out ScanPendingEntry entry)) return false;
            DiscardMask(entry);
            Finish(entry, ScanPendingPhase.Cancelled, new ScanError(ScanErrorCode.Cancelled, "Cancelled."));
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (ScanPendingEntry entry in _tracker.CancelAll())
            {
                DiscardMask(entry);
                RaisePending(entry, ScanPendingPhase.Cancelled, new ScanError(ScanErrorCode.Cancelled, "Disposed."));
            }
            foreach (long session in _sessions.Values)
            {
                try { _transport.DestroySession(session); }
                catch (Exception e) { Note($"DestroySession failed ({e.GetType().Name})."); }
            }
            _sessions.Clear();
        }

        private bool TryOpenSession(ScanKind kind, out ScanError error)
        {
            List<string> addresses = _config.AddressesFor(kind);
            if (addresses.Count == 0)
            {
                error = ScanError.None;
                return false;
            }

            long session;
            try
            {
                session = _transport.CreateSession(kind);
            }
            catch (Exception e)
            {
                error = new ScanError(ScanErrorCode.TransportFault, $"CreateSession({kind}) threw: {e.GetType().Name}: {Redact(e.Message)}");
                return false;
            }
            if (session <= 0)
            {
                error = new ScanError(ScanErrorCode.TransportFault, $"CreateSession({kind}) returned {session}.");
                return false;
            }

            try
            {
                if (!_transport.RegisterCompletion(session, _inbox.Enqueue))
                {
                    _transport.DestroySession(session);
                    error = new ScanError(ScanErrorCode.TransportFault, $"RegisterCompletion({kind}) was refused.");
                    return false;
                }

                for (int index = 0; index < addresses.Count; index++)
                {
                    string address = addresses[index];
                    if (_transport.Connect(session, address, _config.UseTls, out string reason))
                    {
                        _sessions[kind] = session;
                        error = ScanError.None;
                        return true;
                    }
                    // The address is deployment-private. The transport's reason text may echo it, or a
                    // host or resolved IP derived from it, so it is dropped: only a fixed message is kept.
                    Note($"{kind}: connect to {(index == 0 ? "first" : "second")} server failed.");
                }

                _transport.DestroySession(session);
                error = new ScanError(ScanErrorCode.ConnectFailed,
                    $"{kind}: no server answered.");
                return false;
            }
            catch (ScanTransportUnsupportedException)
            {
                // Fixed text only. With UseTls there is no plaintext retry: the kind stays unavailable.
                try { _transport.DestroySession(session); } catch { /* already failing */ }
                error = new ScanError(ScanErrorCode.TransportFault,
                    ScanTransportUnsupportedException.TlsNeedsNewerLibrary);
                return false;
            }
            catch (Exception e)
            {
                try { _transport.DestroySession(session); } catch { /* already failing */ }
                error = new ScanError(ScanErrorCode.TransportFault, $"{kind} session setup threw {e.GetType().Name}.");
                return false;
            }
        }

        private const string RedactedMarker = "[redacted]";

        /// <summary>
        /// Removes every configured address, each host part, each standalone port and any IPv4
        /// literal in ONE regex pass (longest alternatives first), so a replacement is never
        /// re-matched. A resolved name that is not configured cannot be known here.
        /// </summary>
        internal string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var literals = new List<string>();
            var ports = new List<string>();
            foreach (string address in new[] { _config.ServerAddress, _config.FallbackServerAddress })
            {
                string trimmed = address?.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                literals.Add(trimmed);
                int colon = trimmed.LastIndexOf(':');
                if (colon <= 0) continue;
                literals.Add(trimmed.Substring(0, colon));
                string port = trimmed.Substring(colon + 1);
                if (port.Length > 0 && !ports.Contains(port)) ports.Add(port);
            }
            literals.Sort((x, y) => y.Length.CompareTo(x.Length));

            var alternatives = new List<string>();
            foreach (string literal in literals)
                alternatives.Add(System.Text.RegularExpressions.Regex.Escape(literal));
            foreach (string port in ports)
                alternatives.Add("(?<![0-9])" + System.Text.RegularExpressions.Regex.Escape(port) + "(?![0-9])");
            alternatives.Add(@"(?<![0-9])[0-9]{1,3}(\.[0-9]{1,3}){3}(?![0-9])");

            var regex = new System.Text.RegularExpressions.Regex(string.Join("|", alternatives),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return regex.Replace(text, RedactedMarker);
        }

        private bool Submit(ScanBuildResult build, ScanKind kind, double now, int imageWidth, int imageHeight,
                            ScanAnchor? anchor, out long eventHandle, out ScanError error, bool sentBox = false)
        {
            eventHandle = 0;
            if (_disposed)
            {
                error = new ScanError(ScanErrorCode.NotInitialized, "The scan service was disposed.");
                return false;
            }
            if (!build.Ok)
            {
                error = build.Error;
                return false;
            }
            if (!_sessions.TryGetValue(kind, out long session))
            {
                error = new ScanError(ScanErrorCode.NotInitialized, $"No connected {kind} session.");
                return false;
            }

            long handle;
            try
            {
                handle = _transport.Submit(session, build.Request);
            }
            catch (Exception e)
            {
                error = new ScanError(ScanErrorCode.TransportFault, $"Submit({kind}) threw {e.GetType().Name}.");
                return false;
            }
            if (handle <= 0)
            {
                error = new ScanError(ScanErrorCode.RequestRejected, $"The session library rejected the {kind} request.");
                return false;
            }

            var context = new RequestContext
            {
                OutputPath = build.Request.OutputPath,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                HasAnchor = anchor.HasValue,
                Anchor = anchor ?? default,
                SentBox = sentBox,
            };
            if (!_tracker.Begin(handle, kind, now, _config.TimeoutFor(kind), context, out ScanPendingEntry entry))
            {
                error = new ScanError(ScanErrorCode.RequestRejected, $"Event handle {handle} is already tracked.");
                return false;
            }

            eventHandle = handle;
            error = ScanError.None;
            var submission = new ScanSubmission(handle, kind, context.OutputPath, context.HasAnchor, context.Anchor);
            Invoke(() => Submitted?.Invoke(submission));
            RaisePending(entry, ScanPendingPhase.Started, ScanError.None);
            return true;
        }

        private void HandleCompletion(ScanCompletion raw)
        {
            // The server's text can echo the deployment-private address. Redact before it is surfaced.
            var completion = new ScanCompletion(raw.SessionHandle, raw.EventHandle, raw.Success,
                Redact(raw.Message));
            ScanCompleteOutcome outcome = _tracker.Complete(completion.EventHandle, completion.Success, out ScanPendingEntry entry);
            if (outcome == ScanCompleteOutcome.Unknown)
            {
                Note($"Completion for unknown event {completion.EventHandle} ignored.");
                return;
            }
            if (outcome == ScanCompleteOutcome.Late)
            {
                Note($"Late completion for event {completion.EventHandle} ignored.");
                return;
            }

            var context = (RequestContext)entry.Context;
            if (entry.Kind == ScanKind.Segment)
            {
                ScanSegmentResult segment = ScanResponseParser.ParseSegment(
                    completion, context.OutputPath, context.ImageWidth, context.ImageHeight, _files);
                segment.SentBox = context.SentBox;
                // Read from the server's own text: redaction would take the marker's digit when the port is "1".
                segment.UsedBox = ScanResponseParser.ServerUsedBox(raw.Message);
                if (context.SentBox && completion.Success)
                {
                    BoxSupport = segment.UsedBox ? ScanBoxSupport.Supported : ScanBoxSupport.Unsupported;
                    if (!segment.UsedBox)
                    {
                        // An older server took the corners as clicks: that mask is not the object. Drop it.
                        Note("The server answered a box prompt without using the box; sending clicks from now on.");
                        segment.Success = false;
                        segment.Mask = null;
                        segment.Error = new ScanError(ScanErrorCode.BoxPromptUnsupported,
                            "This server does not take a box prompt.");
                    }
                }
                // The mask may fail to read after a successful callback. The state follows the parse.
                entry.State = segment.Success ? ScanState.Succeeded : ScanState.Failed;
                RaisePending(entry, segment.Success ? ScanPendingPhase.Succeeded : ScanPendingPhase.Failed, segment.Error);
                Invoke(() => SegmentCompleted?.Invoke(segment));
                return;
            }

            if (ScanResponseParser.TryParseReconstruction(completion, entry.Kind, context.OutputPath,
                    out ScanReconstructionResult reconstruction, out ScanError error))
            {
                reconstruction.HasAnchor = context.HasAnchor;
                reconstruction.Anchor = context.Anchor;
                if (context.HasAnchor && context.Anchor.HasSize)
                {
                    reconstruction.HasRegionHint = true;
                    reconstruction.RegionHint = new ScanRegionHint(context.Anchor.Center, context.Anchor.Size,
                        rotation: context.Anchor.Rotation);
                }
                RaisePending(entry, ScanPendingPhase.Succeeded, ScanError.None);
                Invoke(() => _sink?.OnScanReconstructed(reconstruction));
                return;
            }

            entry.State = ScanState.Failed;
            RaisePending(entry, ScanPendingPhase.Failed, error);
            Invoke(() => _sink?.OnScanFailed(new ScanFailure
            {
                EventHandle = entry.EventHandle,
                Kind = entry.Kind,
                Error = error,
                HasAnchor = context.HasAnchor,
                Anchor = context.Anchor,
            }));
        }

        private void Finish(ScanPendingEntry entry, ScanPendingPhase phase, ScanError error)
        {
            var context = (RequestContext)entry.Context;
            RaisePending(entry, phase, error);
            if (entry.Kind == ScanKind.Segment)
            {
                Invoke(() => SegmentCompleted?.Invoke(new ScanSegmentResult
                {
                    EventHandle = entry.EventHandle,
                    Success = false,
                    Error = error,
                }));
                return;
            }
            Invoke(() => _sink?.OnScanFailed(new ScanFailure
            {
                EventHandle = entry.EventHandle,
                Kind = entry.Kind,
                Error = error,
                HasAnchor = context.HasAnchor,
                Anchor = context.Anchor,
            }));
        }

        private void DiscardMask(ScanPendingEntry entry)
        {
            if (entry.Kind != ScanKind.Segment) return;
            var context = (RequestContext)entry.Context;
            try { _files.Delete(context.OutputPath); } catch { /* best-effort cleanup */ }
        }

        private void RaisePending(ScanPendingEntry entry, ScanPendingPhase phase, ScanError error)
        {
            var context = (RequestContext)entry.Context;
            var pending = new ScanPendingEvent(phase, entry.EventHandle, entry.Kind, context.HasAnchor, context.Anchor, error);
            Invoke(() => PendingChanged?.Invoke(pending));
        }

        // A subscriber that throws must not stop the drain loop or leave a request stuck pending.
        private void Invoke(Action action)
        {
            try { action(); }
            catch (Exception e) { Note($"Subscriber threw: {e.Message}"); }
        }

        private void Note(string message) => Diagnostic?.Invoke(message);
    }
}
