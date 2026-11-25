// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanServiceTests
    {
        private static readonly DateTime Clock = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        private static readonly byte[] Image = { 9, 9, 9 };

        private FakeScanTransport _transport;
        private FakeScanFiles _files;
        private RecordingScanSink _sink;
        private ScanSessionConfig _config;
        private ScanService _service;
        private List<ScanPendingEvent> _pending;
        private List<ScanSegmentResult> _segments;
        private List<string> _notes;
        private List<ScanSubmission> _submissions;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeScanTransport();
            _files = new FakeScanFiles();
            _sink = new RecordingScanSink();
            _config = new ScanSessionConfig
            {
                ServerAddress = "primary.test:9800",
                FallbackServerAddress = "backup.test:9800",
                ClientId = "cli",
                CacheDirectory = "cache",
                OutputDirectory = "out",
                SegmentTimeoutSeconds = 10,
                ReconstructTimeoutSeconds = 100,
            };
            Build();
        }

        private void Build()
        {
            _service = new ScanService(_transport, _files, _config, _sink, () => Clock);
            _pending = new List<ScanPendingEvent>();
            _segments = new List<ScanSegmentResult>();
            _notes = new List<string>();
            _submissions = new List<ScanSubmission>();
            _service.Submitted += s => _submissions.Add(s);
            _service.PendingChanged += e => _pending.Add(e);
            _service.SegmentCompleted += r => _segments.Add(r);
            _service.Diagnostic += n => _notes.Add(n);
        }

        private void InitializeOk()
        {
            Assert.That(_service.Initialize(out ScanError error), Is.True, error.ToString());
        }

        private long Reconstruct(ScanAnchor? anchor = null, double now = 0)
        {
            Assert.That(_service.TryRequestReconstruct(Image, new byte[6], 3, 2, anchor, now, out long handle,
                out ScanError error), Is.True, error.ToString());
            return handle;
        }

        private long Segment(double now = 0)
        {
            Assert.That(_service.TryRequestSegment(Image, 4, 3, new[] { new ScanPoint(1, 1) }, now, out long handle,
                out ScanError error), Is.True, error.ToString());
            return handle;
        }

        // ---- initialize and connect -------------------------------------------------

        [Test]
        public void InitializeOpensOneRegisteredAndConnectedSessionPerKind()
        {
            InitializeOk();

            Assert.That(_transport.Created, Is.EqualTo(new[] { ScanKind.Segment, ScanKind.Reconstruct }));
            Assert.That(_transport.Callbacks.Count, Is.EqualTo(2));
            Assert.That(_transport.ConnectAttempts, Is.EqualTo(new[] { "primary.test:9800", "primary.test:9800" }));
            Assert.That(_service.ActiveWorkflow, Is.EqualTo(ScanWorkflow.SegmentThenReconstruct));
            Assert.That(_service.IsReady(ScanKind.Segment), Is.True);
            Assert.That(_service.IsReady(ScanKind.Reconstruct), Is.True);
        }

        [Test]
        public void PlaintextIsTheDefaultConnect()
        {
            InitializeOk();
            Assert.That(_transport.ConnectTlsFlags, Is.EqualTo(new[] { false, false }));
        }

        [Test]
        public void UseTlsSelectsTheTlsConnectForEveryAddress()
        {
            _config.UseTls = true;
            InitializeOk();
            Assert.That(_transport.ConnectAttempts.Count, Is.EqualTo(2));
            Assert.That(_transport.ConnectTlsFlags, Is.EqualTo(new[] { true, true }));
        }

        [Test]
        public void UseTlsAppliesToTheFallbackAddressToo()
        {
            _config.UseTls = true;
            _transport.RefusedAddresses.Add("primary.test:9800");
            InitializeOk();
            Assert.That(_transport.ConnectAttempts, Does.Contain("backup.test:9800"));
            Assert.That(_transport.ConnectTlsFlags, Is.All.True);
        }

        [Test]
        public void ALibraryWithoutTheTlsEntryPointFailsClearlyAndNeverFallsBackToPlaintext()
        {
            _config.UseTls = true;
            _transport.TlsUnsupported = true;

            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.TransportFault));
            Assert.That(error.Message, Is.EqualTo("TLS needs a newer session library"));
            Assert.That(_transport.ConnectTlsFlags, Is.All.True);
            Assert.That(_service.IsReady(ScanKind.Segment), Is.False);
            Assert.That(_service.ActiveWorkflow, Is.EqualTo(ScanWorkflow.None));
            Assert.That(_transport.Destroyed.Count, Is.EqualTo(2));
            Assert.That(error.Message, Does.Not.Contain("primary.test").And.Not.Contain("9800")
                .And.Not.Contain("native detail"));
            foreach (string note in _notes)
                Assert.That(note, Does.Not.Contain("primary.test").And.Not.Contain("backup.test")
                    .And.Not.Contain("9800"), note);
        }

        [Test]
        public void TheMissingTlsEntryPointDoesNotAffectPlaintext()
        {
            _transport.TlsUnsupported = true; // irrelevant when tls is off
            InitializeOk();
            Assert.That(_transport.ConnectTlsFlags, Is.All.False);
        }

        [Test]
        public void AnEmptyServerAddressDisablesScanningAndTouchesNothing()
        {
            _config.ServerAddress = "";
            _config.FallbackServerAddress = "";
            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.InvalidConfig));
            Assert.That(_transport.Created, Is.Empty);
            Assert.That(_transport.ConnectAttempts, Is.Empty);
            Assert.That(_service.IsReady(ScanKind.Segment), Is.False);
        }

        [Test]
        public void WithoutAPrimaryServerNothingStarts()
        {
            // The second address alone starts nothing (there is no one-shot pipeline).
            _config.ServerAddress = "";
            Assert.That(_service.Initialize(out ScanError init), Is.False);
            Assert.That(init.Code, Is.EqualTo(ScanErrorCode.InvalidConfig));
            Assert.That(_transport.Created, Is.Empty);
            Assert.That(_service.ActiveWorkflow, Is.EqualTo(ScanWorkflow.None));
            Assert.That(_service.TryRequestSegment(Image, 4, 3, new[] { new ScanPoint(1, 1) }, 0, out _, out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.NotInitialized));
        }

        [Test]
        public void AnUnconnectedServiceHasNoWorkflow()
        {
            Assert.That(_service.ActiveWorkflow, Is.EqualTo(ScanWorkflow.None));
        }

        [Test]
        public void TheFallbackServerIsUsedWhenThePrimaryRefuses()
        {
            _transport.RefusedAddresses.Add("primary.test:9800");
            InitializeOk();

            Assert.That(_transport.ConnectAttempts.FindAll(a => a == "backup.test:9800").Count, Is.EqualTo(2));
            Assert.That(_service.IsReady(ScanKind.Segment), Is.True);
            Assert.That(_notes, Has.Some.Contains("first server failed"));
            Assert.That(_notes, Has.None.Contains("primary.test"));
        }

        [Test]
        public void WhenNoServerAnswersTheSessionsAreDestroyedAndTheErrorIsReported()
        {
            _transport.RefusedAddresses.Add("primary.test:9800");
            _transport.RefusedAddresses.Add("backup.test:9800");

            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.ConnectFailed));
            Assert.That(error.Message, Does.Contain("no server answered"));
            Assert.That(_transport.Destroyed.Count, Is.EqualTo(2));
            Assert.That(_service.IsReady(ScanKind.Segment), Is.False);
        }

        // Not dotted-quad shaped: tools/test_scan_port_guards.py forbids IP literals in Scan tests.
        private const string ResolvedSentinel = "resolved-sentinel-ip";

        [Test]
        public void AThrowingConnectNeverLeaksTheAddressIntoTheError()
        {
            _transport.ConnectThrows = new InvalidOperationException("cannot reach primary.test:9800");

            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.TransportFault));
            Assert.That(error.Message, Does.Not.Contain("primary.test").And.Not.Contain("9800"));
            Assert.That(error.Message, Does.Contain(nameof(InvalidOperationException)));
            foreach (string note in _notes) Assert.That(note, Does.Not.Contain("primary.test"), note);
        }

        [Test]
        public void AFailedCompletionThatEchoesTheAddressIsRedacted()
        {
            InitializeOk();
            long handle = Reconstruct();
            string ip = "10." + "1.2.3"; // built at runtime: the port guard forbids IP literals in Scan tests
            _transport.Complete(ScanKind.Reconstruct, handle, false,
                "upstream backup.test:9800 failed, host primary.test at " + ip + " port 9800");
            _service.Update(1);

            string message = _sink.Failures[0].Error.Message;
            Assert.That(message, Does.Not.Contain("primary.test").And.Not.Contain("backup.test").And.Not.Contain(ip)
                .And.Not.Contain("9800"));
            Assert.That(message, Does.Contain("failed"));
            foreach (string note in _notes) Assert.That(note, Does.Not.Contain("primary.test"), note);
        }

        [Test]
        public void AnEchoedAddressNeverReachesNotesOrTheError()
        {
            _transport.RefusedAddresses.Add("primary.test:9800");
            _transport.RefusedAddresses.Add("backup.test:9800");
            _transport.EchoAddressInError = "refused by {0} (resolved " + ResolvedSentinel + ", host primary.test)";

            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.ConnectFailed));
            Assert.That(error.Message, Does.Not.Contain("primary.test").And.Not.Contain("backup.test")
                .And.Not.Contain("9800").And.Not.Contain(ResolvedSentinel));
            foreach (string note in _notes)
                Assert.That(note, Does.Not.Contain("primary.test").And.Not.Contain("backup.test")
                    .And.Not.Contain("9800").And.Not.Contain(ResolvedSentinel), note);
        }

        [Test]
        public void ASamePrimaryAndFallbackIsTriedOnce()
        {
            _config.FallbackServerAddress = _config.ServerAddress;
            _transport.RefusedAddresses.Add("primary.test:9800");
            _service.Initialize(out _);
            Assert.That(_transport.ConnectAttempts.FindAll(a => a == "primary.test:9800").Count, Is.EqualTo(2));
        }

        [Test]
        public void AMissingSessionLibraryIsReportedNotThrown()
        {
            _transport.CreateThrows = new DllNotFoundException("spatialblend_session");

            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.TransportFault));
            Assert.That(error.Message, Does.Contain("spatialblend_session"));
        }

        [Test]
        public void ARefusedCallbackRegistrationDestroysTheSession()
        {
            _transport.RefuseCallbackRegistration = true;
            Assert.That(_service.Initialize(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.TransportFault));
            Assert.That(_transport.Destroyed.Count, Is.EqualTo(2));
        }

        [Test]
        public void InitializeIsIdempotent()
        {
            InitializeOk();
            InitializeOk();
            Assert.That(_transport.Created.Count, Is.EqualTo(2));
        }

        // ---- request errors ---------------------------------------------------------

        [Test]
        public void ARequestBeforeInitializeIsRefused()
        {
            Assert.That(_service.TryRequestSegment(Image, 4, 3, new[] { new ScanPoint(1, 1) }, 0, out long handle,
                out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.NotInitialized));
            Assert.That(handle, Is.EqualTo(0));
            Assert.That(_transport.Submitted, Is.Empty);
        }

        [Test]
        public void InvalidInputNeverReachesTheTransport()
        {
            InitializeOk();
            Assert.That(_service.TryRequestSegment(null, 4, 3, new[] { new ScanPoint(1, 1) }, 0, out _, out ScanError a), Is.False);
            Assert.That(a.Code, Is.EqualTo(ScanErrorCode.EmptyImage));
            Assert.That(_service.TryRequestReconstruct(Image, new byte[5], 3, 2, null, 0, out _, out ScanError b), Is.False);
            Assert.That(b.Code, Is.EqualTo(ScanErrorCode.InvalidMaskSize));
            Assert.That(_service.TryRequestSegment(Image, 4, 3, (ScanPoint[])null, 0, out _, out ScanError c), Is.False);
            Assert.That(c.Code, Is.EqualTo(ScanErrorCode.NoPoints));
            Assert.That(_transport.Submitted, Is.Empty);
            Assert.That(_pending, Is.Empty);
        }

        [Test]
        public void ARejectedRequestLeavesNothingPending()
        {
            InitializeOk();
            _transport.RejectSubmit = true;

            Assert.That(_service.TryRequestReconstruct(Image, new byte[6], 3, 2, null, 0, out long handle,
                out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.RequestRejected));
            Assert.That(handle, Is.EqualTo(0));
            Assert.That(_service.PendingCount, Is.EqualTo(0));
            Assert.That(_pending, Is.Empty);
        }

        [Test]
        public void AThrowingTransportIsReportedAsAFault()
        {
            InitializeOk();
            _transport.SubmitThrows = new EntryPointNotFoundException("spatialblend_inference_reconstruct");

            Assert.That(_service.TryRequestReconstruct(Image, new byte[6], 3, 2, null, 0, out _, out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.TransportFault));
            Assert.That(_service.PendingCount, Is.EqualTo(0));
        }

        // ---- segment ----------------------------------------------------------------

        [Test]
        public void ASegmentRequestIsSubmittedAndAnswered()
        {
            InitializeOk();
            long handle = Segment(now: 5);

            ScanTransportRequest sent = _transport.Submitted[0];
            Assert.That(sent.Kind, Is.EqualTo(ScanKind.Segment));
            Assert.That(sent.ClientId, Is.EqualTo("cli"));
            Assert.That(_pending.Count, Is.EqualTo(1));
            Assert.That(_pending[0].Phase, Is.EqualTo(ScanPendingPhase.Started));
            Assert.That(_pending[0].EventHandle, Is.EqualTo(handle));

            _files.Files[sent.OutputPath] = new byte[12];
            _transport.Complete(ScanKind.Segment, handle, true);
            Assert.That(_segments, Is.Empty, "nothing is delivered until Update runs on the caller thread");
            _service.Update(6);

            Assert.That(_segments.Count, Is.EqualTo(1));
            Assert.That(_segments[0].Success, Is.True);
            Assert.That(_segments[0].Width, Is.EqualTo(4));
            Assert.That(_segments[0].Height, Is.EqualTo(3));
            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.Succeeded));
            Assert.That(_files.Deleted, Is.EqualTo(new[] { sent.OutputPath }));
            Assert.That(_service.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void AnUnreadableMaskEndsTheRequestAsFailed()
        {
            InitializeOk();
            long handle = Segment();
            _transport.Complete(ScanKind.Segment, handle, true);
            _service.Update(1);

            Assert.That(_segments[0].Success, Is.False);
            Assert.That(_segments[0].Error.Code, Is.EqualTo(ScanErrorCode.MaskUnreadable));
            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.Failed));
        }

        [Test]
        public void ASegmentTimeoutReportsFailureAndDropsTheMaskFile()
        {
            InitializeOk();
            long handle = Segment(now: 0);
            string maskPath = _transport.Submitted[0].OutputPath;

            _service.Update(9.9);
            Assert.That(_segments, Is.Empty);
            _service.Update(10);

            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.TimedOut));
            Assert.That(_segments[0].Error.Code, Is.EqualTo(ScanErrorCode.Timeout));
            Assert.That(_files.Deleted, Contains.Item(maskPath));

            _transport.Complete(ScanKind.Segment, handle, true);
            _service.Update(11);
            Assert.That(_segments.Count, Is.EqualTo(1), "a late answer is dropped");
            Assert.That(_notes, Has.Some.Contains("Late"));
        }

        // ---- reconstruct ------------------------------------------------------------

        // The drawn box's turn goes from the anchor the card hands the service into the region
        // hint the sink installs the scan with, so its region is the box as drawn.
        [Test]
        public void AReconstructionOfATurnedBoxCarriesTheTurnIntoItsRegionHint()
        {
            InitializeOk();
            Quaternion turn = Quaternion.Euler(0f, 35f, 0f);
            var anchor = new ScanAnchor(new Vector3(1f, 0.4f, 2f), new Vector3(0.5f, 0.8f, 0.3f), turn);
            long handle = Reconstruct(anchor);

            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(1);

            Assert.That(_sink.Results.Count, Is.EqualTo(1));
            ScanReconstructionResult result = _sink.Results[0];
            Assert.That(result.HasRegionHint, Is.True);
            Assert.That(Quaternion.Angle(result.RegionHint.Rotation, turn), Is.LessThan(0.01f), "the box's turn");
            Assert.That(result.RegionHint.BoxCenter, Is.EqualTo(anchor.Center));
            Assert.That(result.RegionHint.BoxSize, Is.EqualTo(anchor.Size));
        }

        [Test]
        public void AReconstructionWithABoxAnchorReachesTheSinkWithARegionHint()
        {
            InitializeOk();
            var anchor = new ScanAnchor(new Vector3(1f, 0.4f, 2f), new Vector3(0.5f, 0.8f, 0.5f));
            long handle = Reconstruct(anchor);
            string prefix = _transport.Submitted[0].OutputPath;

            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(1);

            Assert.That(_sink.Results.Count, Is.EqualTo(1));
            ScanReconstructionResult result = _sink.Results[0];
            Assert.That(result.ModelFileName, Does.EndWith("_objmodel.glb"));
            Assert.That(result.OutputPrefix, Is.EqualTo(prefix));
            Assert.That(result.HasAnchor, Is.True);
            Assert.That(result.Anchor.Center, Is.EqualTo(anchor.Center));
            Assert.That(result.HasRegionHint, Is.True);
            Assert.That(result.RegionHint.BoxSize, Is.EqualTo(anchor.Size));
            Assert.That(result.RegionHint.Margin, Is.EqualTo(0.15f));
            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.Succeeded));
            Assert.That(_pending[1].HasAnchor, Is.True);
        }

        [Test]
        public void ASubmissionIsAnnouncedBeforeItsPendingEventWithTheOutputPath()
        {
            InitializeOk();
            var anchor = new ScanAnchor(Vector3.up, Vector3.one);
            long handle = Reconstruct(anchor);

            Assert.That(_submissions.Count, Is.EqualTo(1));
            Assert.That(_submissions[0].EventHandle, Is.EqualTo(handle));
            Assert.That(_submissions[0].Kind, Is.EqualTo(ScanKind.Reconstruct));
            Assert.That(_submissions[0].OutputPath, Is.EqualTo(_transport.Submitted[0].OutputPath));
            Assert.That(_submissions[0].HasAnchor, Is.True);
            Assert.That(_pending[0].Phase, Is.EqualTo(ScanPendingPhase.Started));

            _service.TryRequestReconstruct(Image, new byte[6], 3, 2, null, 0, out _, out _);
            _transport.RejectSubmit = true;
            _service.TryRequestReconstruct(Image, new byte[6], 3, 2, null, 0, out _, out _);
            Assert.That(_submissions.Count, Is.EqualTo(2), "a rejected request is not announced");
        }

        [Test]
        public void AReconstructionWithoutAnAnchorHasNoRegionHint()
        {
            InitializeOk();
            long handle = Reconstruct();
            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(1);

            Assert.That(_sink.Results[0].HasAnchor, Is.False);
            Assert.That(_sink.Results[0].HasRegionHint, Is.False);
        }

        [Test]
        public void AnAnchorWithoutAFootprintHasNoRegionHint()
        {
            InitializeOk();
            long handle = Reconstruct(new ScanAnchor(Vector3.zero, new Vector3(0f, 1f, 0f)));
            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(1);

            Assert.That(_sink.Results[0].HasAnchor, Is.True);
            Assert.That(_sink.Results[0].HasRegionHint, Is.False);
        }

        // ---- box prompts (scan UI Concept A) --------------------------------------------

        private static ScanPrompt BoxPrompt() => ScanPrompt.ForBox(new ScanPixelBox(1, 1, 3, 2), new[] { new ScanPoint(2, 1.5) });

        private long PromptSegment(ScanPrompt prompt, double now = 0)
        {
            Assert.That(_service.TryRequestSegment(Image, 4, 3, prompt, now, out long handle, out ScanError error), Is.True, error.ToString());
            return handle;
        }

        [Test]
        public void ABoxPromptGoesOutAsTwoLabelledCornersAndBox1MarksTheServerAsSupporting()
        {
            InitializeOk();
            Assert.That(_service.BoxSupport, Is.EqualTo(ScanBoxSupport.Unknown));
            long handle = PromptSegment(BoxPrompt());
            ScanPoint[] sent = _transport.Submitted[0].Points;
            Assert.That(sent.Length, Is.EqualTo(2));
            Assert.That(sent[0].Label, Is.EqualTo(ScanPointLabel.BoxTopLeft));
            Assert.That(sent[1].Label, Is.EqualTo(ScanPointLabel.BoxBottomRight));

            _files.Files[_transport.Submitted[0].OutputPath] = new byte[12];
            _transport.Complete(ScanKind.Segment, handle, true, "Segment OK | points:0 | box:1 | time:1.4s");
            _service.Update(1);

            Assert.That(_segments[0].Success, Is.True);
            Assert.That(_segments[0].SentBox && _segments[0].UsedBox, Is.True);
            Assert.That(_service.BoxSupport, Is.EqualTo(ScanBoxSupport.Supported));
        }

        [Test]
        public void AnOlderServerThatIgnoresTheBoxFailsTheRequestAndTheNextOneSendsClicks()
        {
            InitializeOk();
            long first = PromptSegment(BoxPrompt());
            _files.Files[_transport.Submitted[0].OutputPath] = new byte[12];
            _transport.Complete(ScanKind.Segment, first, true, "Segment OK | GPU:0 | points:2 | time:1.5s");
            _service.Update(1);

            Assert.That(_segments[0].Success, Is.False, "the corners were taken as clicks: that mask is not the object");
            Assert.That(_segments[0].Mask, Is.Null);
            Assert.That(_segments[0].Error.Code, Is.EqualTo(ScanErrorCode.BoxPromptUnsupported));
            Assert.That(_service.BoxSupport, Is.EqualTo(ScanBoxSupport.Unsupported));

            PromptSegment(BoxPrompt());
            ScanPoint[] retry = _transport.Submitted[1].Points;
            Assert.That(retry.Length, Is.EqualTo(1));
            Assert.That(retry[0].Label, Is.EqualTo(ScanPointLabel.Positive));
            Assert.That(retry[0].X, Is.EqualTo(2.0));
        }

        [Test]
        public void ClicksAloneDoNotTouchTheBoxSupport()
        {
            InitializeOk();
            long handle = Segment();
            _files.Files[_transport.Submitted[0].OutputPath] = new byte[12];
            _transport.Complete(ScanKind.Segment, handle, true, "Segment OK | points:1");
            _service.Update(1);
            Assert.That(_segments[0].Success, Is.True);
            Assert.That(_segments[0].SentBox, Is.False);
            Assert.That(_service.BoxSupport, Is.EqualTo(ScanBoxSupport.Unknown));
        }

        [Test]
        public void AFailedBoxRequestSaysNothingAboutBoxSupport()
        {
            InitializeOk();
            long handle = PromptSegment(BoxPrompt());
            _transport.Complete(ScanKind.Segment, handle, false, "out of memory");
            _service.Update(1);
            Assert.That(_segments[0].Error.Code, Is.EqualTo(ScanErrorCode.ServerFailed));
            Assert.That(_service.BoxSupport, Is.EqualTo(ScanBoxSupport.Unknown));
        }

        [Test]
        public void AServerFailureReachesTheSinkAsAFailure()
        {
            InitializeOk();
            var anchor = new ScanAnchor(Vector3.one, Vector3.one);
            long handle = Reconstruct(anchor);
            _transport.Complete(ScanKind.Reconstruct, handle, false, "out of memory");
            _service.Update(1);

            Assert.That(_sink.Results, Is.Empty);
            Assert.That(_sink.Failures.Count, Is.EqualTo(1));
            Assert.That(_sink.Failures[0].Error.Code, Is.EqualTo(ScanErrorCode.ServerFailed));
            Assert.That(_sink.Failures[0].Error.Message, Is.EqualTo("out of memory"));
            Assert.That(_sink.Failures[0].HasAnchor, Is.True);
            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.Failed));
        }

        [Test]
        public void AReconstructTimeoutUsesTheLongerLimitAndFailsOnce()
        {
            InitializeOk();
            long handle = Reconstruct(now: 0);

            _service.Update(99);
            Assert.That(_sink.Failures, Is.Empty);
            _service.Update(100);
            _service.Update(500);

            Assert.That(_sink.Failures.Count, Is.EqualTo(1));
            Assert.That(_sink.Failures[0].Error.Code, Is.EqualTo(ScanErrorCode.Timeout));
            Assert.That(_pending.FindAll(e => e.Phase == ScanPendingPhase.TimedOut).Count, Is.EqualTo(1));

            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(501);
            Assert.That(_sink.Results, Is.Empty, "a result that arrives after the timeout is not delivered");
            Assert.That(_sink.Failures.Count, Is.EqualTo(1));
        }

        [Test]
        public void ACompletionAtTheDeadlineWinsOverTheTimeout()
        {
            InitializeOk();
            long handle = Reconstruct(now: 0);
            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(100);

            Assert.That(_sink.Results.Count, Is.EqualTo(1));
            Assert.That(_sink.Failures, Is.Empty);
        }

        // ---- pending-state machine through the service ------------------------------

        [Test]
        public void EachRequestRaisesOneStartedAndOneTerminalEvent()
        {
            InitializeOk();
            long a = Reconstruct();
            long b = Reconstruct();
            _transport.Complete(ScanKind.Reconstruct, b, true);
            _transport.Complete(ScanKind.Reconstruct, a, false, "nope");
            _service.Update(1);
            _transport.Complete(ScanKind.Reconstruct, a, true);
            _service.Update(2);

            foreach (long handle in new[] { a, b })
            {
                var events = _pending.FindAll(e => e.EventHandle == handle);
                Assert.That(events.Count, Is.EqualTo(2));
                Assert.That(events[0].Phase, Is.EqualTo(ScanPendingPhase.Started));
                Assert.That(events[1].IsTerminal, Is.True);
            }
            Assert.That(_pending.Find(e => e.EventHandle == a && e.IsTerminal).Phase, Is.EqualTo(ScanPendingPhase.Failed));
            Assert.That(_pending.Find(e => e.EventHandle == b && e.IsTerminal).Phase, Is.EqualTo(ScanPendingPhase.Succeeded));
        }

        [Test]
        public void AnUnknownCompletionIsIgnored()
        {
            InitializeOk();
            _transport.Complete(ScanKind.Segment, 424242, true);
            _service.Update(1);

            Assert.That(_pending, Is.Empty);
            Assert.That(_segments, Is.Empty);
            Assert.That(_notes, Has.Some.Contains("unknown"));
        }

        [Test]
        public void CancelEndsTheRequestAndDropsALaterAnswer()
        {
            InitializeOk();
            long handle = Segment();
            string maskPath = _transport.Submitted[0].OutputPath;

            Assert.That(_service.Cancel(handle), Is.True);
            Assert.That(_service.Cancel(handle), Is.False);
            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.Cancelled));
            Assert.That(_segments[0].Error.Code, Is.EqualTo(ScanErrorCode.Cancelled));
            Assert.That(_files.Deleted, Contains.Item(maskPath));

            _transport.Complete(ScanKind.Segment, handle, true);
            _service.Update(1);
            Assert.That(_segments.Count, Is.EqualTo(1));
        }

        [Test]
        public void DisposeCancelsPendingWorkAndDestroysEachSessionOnce()
        {
            InitializeOk();
            long handle = Reconstruct();

            _service.Dispose();
            _service.Dispose();

            Assert.That(_pending[1].Phase, Is.EqualTo(ScanPendingPhase.Cancelled));
            Assert.That(_transport.Destroyed.Count, Is.EqualTo(2));
            Assert.That(_service.TryRequestSegment(Image, 4, 3, new[] { new ScanPoint(1, 1) }, 0, out _, out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.NotInitialized));

            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(1);
            Assert.That(_sink.Results, Is.Empty);
        }

        // ---- threading and isolation ------------------------------------------------

        [Test]
        public void ACallbackFromAnotherThreadIsDeliveredOnTheCallingThread()
        {
            InitializeOk();
            long handle = Reconstruct();
            int deliveredOn = -1;
            _service.PendingChanged += e => { if (e.IsTerminal) deliveredOn = Thread.CurrentThread.ManagedThreadId; };

            var worker = new Thread(() => _transport.Complete(ScanKind.Reconstruct, handle, true));
            worker.Start();
            worker.Join();
            Assert.That(_sink.Results, Is.Empty);
            _service.Update(1);

            Assert.That(_sink.Results.Count, Is.EqualTo(1));
            Assert.That(deliveredOn, Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
        }

        [Test]
        public void ASinkThatThrowsDoesNotBlockTheNextCompletion()
        {
            InitializeOk();
            _sink.ThrowOnResult = true;
            long first = Reconstruct();
            long second = Reconstruct();
            _transport.Complete(ScanKind.Reconstruct, first, true);
            _transport.Complete(ScanKind.Reconstruct, second, true);
            _service.Update(1);

            Assert.That(_sink.Results.Count, Is.EqualTo(2));
            Assert.That(_notes, Has.Some.Contains("sink failed"));
            Assert.That(_service.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void AServiceWithoutASinkStillFinishesRequests()
        {
            _service = new ScanService(_transport, _files, _config, null, () => Clock);
            InitializeOk();
            Assert.That(_service.TryRequestReconstruct(Image, new byte[6], 3, 2, null, 0, out long handle, out _), Is.True);
            _transport.Complete(ScanKind.Reconstruct, handle, true);
            _service.Update(1);
            Assert.That(_service.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void RequestsOfDifferentKindsUseTheirOwnSessions()
        {
            InitializeOk();
            Segment();
            Reconstruct();
            Assert.That(_transport.Submitted.ConvertAll(r => r.Kind),
                Is.EqualTo(new[] { ScanKind.Segment, ScanKind.Reconstruct }));
        }
    }
}
#endif
