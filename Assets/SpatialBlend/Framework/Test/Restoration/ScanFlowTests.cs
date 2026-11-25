// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The capture flow of scan UI Concept A over a real ScanService and a fake transport: capture (nothing sent until the first
    /// point), point-only segmentation (never the drawn box), the mask, the Reconstruct only on the user's Build
    /// (no timer), and the failures.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanFlowTests
    {
        private static readonly DateTime Clock = new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);
        private static readonly byte[] Photo = { 1, 2, 3 };

        private FakeScanTransport _transport;
        private FakeScanFiles _files;
        private RecordingScanSink _sink;
        private ScanService _service;
        private ScanFlow _flow;
        private List<ScanFlowState> _states;
        private List<long> _builds;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeScanTransport();
            _files = new FakeScanFiles();
            _sink = new RecordingScanSink();
            _service = new ScanService(_transport, _files, new ScanSessionConfig
            {
                ServerAddress = "primary.test:9800", ClientId = "cli", CacheDirectory = "c", OutputDirectory = "o",
            }, _sink, () => Clock);
            Assert.That(_service.Initialize(out ScanError error), Is.True, error.ToString());
            _flow = new ScanFlow(() => _service);
            _states = new List<ScanFlowState>();
            _builds = new List<long>();
            _flow.Changed += s => _states.Add(s);
            _flow.BuildStarted += h => _builds.Add(h);
        }

        [TearDown]
        public void TearDown()
        {
            _flow.Dispose();
            _service.Dispose();
        }

        private ScanTransportRequest LastRequest => _transport.Submitted[_transport.Submitted.Count - 1];

        /// <summary>The server answers the last Segment with a 4 x 3 mask and this message.</summary>
        private void AnswerSegment(string message = "Segment OK | points:1", bool success = true)
        {
            ScanTransportRequest request = LastRequest;
            Assert.That(request.Kind, Is.EqualTo(ScanKind.Segment));
            if (success) _files.Files[request.OutputPath] = new byte[] { 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0 };
            _transport.Complete(ScanKind.Segment, _transport.Handles[_transport.Handles.Count - 1], success, message);
        }

        private void Run(double now)
        {
            _service.Update(now);
        }

        private void CaptureAndMask(double now = 0)
        {
            _flow.Begin(new ScanAnchor(new Vector3(0, 0.5f, 1), new Vector3(0.3f, 0.4f, 0.3f)));
            Assert.That(_flow.Capture(Photo, 4, 3), Is.True, _flow.Error.ToString());
            Assert.That(_flow.AddPoint(new ScanPoint(1, 1), now), Is.True);
            AnswerSegment();
            Run(now + 0.5);
            Assert.That(_flow.Confirm(), Is.True, "the point's mask, confirmed");
        }

        // No box-only request after the photo; segmentation starts at the first point, with the point alone.
        [Test]
        public void CaptureSendsNothing_TheFirstPointAsksWithThatPointAlone_TheUserConfirmsIt()
        {
            _flow.Begin(null);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Aiming));
            Assert.That(_flow.Capture(Photo, 4, 3), Is.True);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Selecting));
            Assert.That(_flow.Refining, Is.False, "nothing is asked at capture");
            Assert.That(_transport.Submitted, Is.Empty, "no box-only request after the photo");
            Assert.That(_flow.CanConfirm, Is.False);

            Assert.That(_flow.AddPoint(new ScanPoint(2, 1), 0.1), Is.True);
            Assert.That(_flow.Refining, Is.True);
            ScanPoint[] sent = LastRequest.Points;
            Assert.That(sent.Length, Is.EqualTo(1), "the tapped point and nothing else");
            Assert.That(sent[0].X, Is.EqualTo(2)); Assert.That(sent[0].Y, Is.EqualTo(1));
            Assert.That(sent[0].Label, Is.EqualTo(ScanPointLabel.Positive));
            Assert.That(_flow.Prompt.HasBox, Is.False);
            Assert.That(_flow.CanConfirm, Is.False, "nothing to confirm while the mask is on its way");

            AnswerSegment();
            Run(0.5);

            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Selecting), "the mask is shown; the user decides");
            Assert.That(_flow.Mask.Length, Is.EqualTo(12));
            Assert.That(_flow.MaskFromBox, Is.False);
            Assert.That(_flow.Confirm(), Is.True);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Confirming));
        }

        // Every Segment request carries only the user's points, through adds and undo: never a box corner
        // (z = 2/3) and never a box-centre fallback click. The Reconstruct sends the photo and the point-based mask, no box.
        [Test]
        public void EveryRequestCarriesOnlyTheUsersPoints_AndTheReconstructTheirMask()
        {
            _flow.Begin(new ScanAnchor(new Vector3(0, 0.5f, 1), new Vector3(0.3f, 0.4f, 0.3f)));
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);
            AnswerSegment();
            Run(0.2);
            _flow.AddPoint(new ScanPoint(3, 2, ScanPointLabel.Negative), 0.3);
            AnswerSegment();
            Run(0.4);
            _flow.AddPoint(new ScanPoint(2, 2), 0.5);
            AnswerSegment();
            Run(0.6);
            _flow.UndoPoint(0.7);
            AnswerSegment();
            Run(0.8);
            List<ScanTransportRequest> segments = _transport.Submitted.FindAll(r => r.Kind == ScanKind.Segment);
            Assert.That(segments.Count, Is.EqualTo(4));
            Assert.That(segments.SelectMany(r => r.Points).Any(p => p.IsBoxCorner), Is.False, "no box corner in any request");
            Assert.That(segments.Select(r => r.Points.Length), Is.EqualTo(new[] { 1, 2, 3, 2 }), "exactly the points at each step");
            Assert.That(segments.Last().Points.Select(p => (p.X, p.Y, p.Label)),
                Is.EqualTo(new[] { (1.0, 1.0, ScanPointLabel.Positive), (3.0, 2.0, ScanPointLabel.Negative) }));
            Assert.That(segments.All(r => ReferenceEquals(r.ImageBytes, Photo)), Is.True, "every tap re-sends the bytes encoded once at capture");
            Assert.That(_flow.Confirm(), Is.True);
            _flow.Build(1.0);
            Assert.That(LastRequest.Kind, Is.EqualTo(ScanKind.Reconstruct));
            Assert.That(LastRequest.ImageBytes, Is.SameAs(Photo), "the same photo bytes, not re-encoded");
            Assert.That(LastRequest.MaskBytes, Is.SameAs(_flow.Mask), "the point-based mask");
            Assert.That(LastRequest.Points, Is.Null.Or.Empty, "no box with the reconstruction");
        }

        [Test]
        public void TheReconstructionStartsOnlyWhenTheUserAsks_NeverByATimer()
        {
            CaptureAndMask();
            Run(3.6);
            Run(600.0);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Confirming), "ten minutes later the mask still waits: there is no timer");
            Assert.That(_transport.Submitted.FindAll(r => r.Kind == ScanKind.Reconstruct), Is.Empty);
            _flow.Build(600.5);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Building));
            Assert.That(LastRequest.Kind, Is.EqualTo(ScanKind.Reconstruct));
            Assert.That(LastRequest.MaskBytes.Length, Is.EqualTo(12));
            Assert.That(LastRequest.ImageBytes, Is.SameAs(Photo));
            Assert.That(_builds.Count, Is.EqualTo(1));
            Assert.That(_flow.BuildHandle, Is.EqualTo(_builds[0]));
        }

        [Test]
        public void BackReturnsToThePoints_RetakeToAiming_AndNothingIsSent()
        {
            CaptureAndMask();
            Run(1.0);
            Assert.That(_flow.Back(), Is.True);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Selecting));
            Assert.That(_flow.Mask, Is.Not.Null, "the mask is kept");
            _flow.Retake();
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Aiming));
            Assert.That(_flow.Mask, Is.Null);
            Assert.That(_flow.Photo, Is.Null);
            Run(10.0);
            Assert.That(_transport.Submitted.FindAll(r => r.Kind == ScanKind.Reconstruct), Is.Empty);
            Assert.That(_flow.Back(), Is.False, "nothing to go back to from Aiming");
        }

        // ---- interactive points

        private int Segments => _transport.Submitted.FindAll(r => r.Kind == ScanKind.Segment).Count;

        [Test]
        public void PointsAccumulate_EachAsksForTheMaskOfAllOfThem_WithTheirLabels_AndNoBox()
        {
            CaptureAndMask();
            _flow.Back();
            Assert.That(_flow.AddPoint(new ScanPoint(2, 1), 1.0), Is.True);
            Assert.That(LastRequest.Points.Count(p => p.Label == ScanPointLabel.Positive), Is.EqualTo(2));
            Assert.That(LastRequest.Points.Count(p => p.IsBoxCorner), Is.EqualTo(0), "no box goes with the points");
            Assert.That(_flow.CanConfirm, Is.False, "the mask shown is for the old points");
            AnswerSegment();
            Run(1.1);
            Assert.That(_flow.AddPoint(new ScanPoint(3, 2, ScanPointLabel.Negative), 1.2), Is.True);
            ScanPoint[] sent = LastRequest.Points;
            Assert.That(sent.Select(p => p.Label),
                Is.EqualTo(new[] { ScanPointLabel.Positive, ScanPointLabel.Positive, ScanPointLabel.Negative }), "every point, in order, with its label");
            Assert.That(_flow.Points.Count, Is.EqualTo(3));
            AnswerSegment();
            Run(1.3);
            Assert.That(_flow.CanConfirm, Is.True);
            Assert.That(_flow.AddPoint(new ScanPoint(9, 9), 1.4), Is.False, "off the photo");
        }

        [Test]
        public void UndoTakesTheLastPointBack_ClearTakesAll_AndEachAsksAgain()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            Assert.That(Segments, Is.EqualTo(0), "nothing is asked before a point");
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);
            AnswerSegment();
            Run(0.2);
            _flow.AddPoint(new ScanPoint(2, 1, ScanPointLabel.Negative), 0.3);
            AnswerSegment();
            Run(0.4);
            Assert.That(_flow.UndoPoint(0.5), Is.True);
            Assert.That(_flow.Points.Count, Is.EqualTo(1));
            Assert.That(LastRequest.Points.Single().Label, Is.EqualTo(ScanPointLabel.Positive), "asked again for the point left");
            AnswerSegment();
            Run(0.6);
            Assert.That(_flow.Mask, Is.Not.Null);
            Assert.That(_flow.ClearPoints(0.7), Is.True);
            Assert.That(_flow.Points, Is.Empty);
            Assert.That(_flow.Mask, Is.Null, "no point: no mask");
            Assert.That(_flow.Refining, Is.False);
            Assert.That(_flow.CanConfirm, Is.False);
            Assert.That(_flow.Confirm(), Is.False, "Confirm gates on a mask");
            Assert.That(_flow.UndoPoint(0.8), Is.False);
        }

        [Test]
        public void NegativePointsAloneAskNothing()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1, ScanPointLabel.Negative), 0.1);
            Assert.That(Segments, Is.EqualTo(0), "a negative alone selects nothing");
            Assert.That(_flow.Mask, Is.Null);
        }

        // One request in flight; points added meanwhile are coalesced into one request sent when it returns, and its answer (for the
        // old points) is never shown: the latest points win.
        [Test]
        public void PointsWhileARequestIsOut_AreCoalesced_AndTheStaleAnswerIsDropped()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);
            Assert.That(Segments, Is.EqualTo(1));
            _flow.AddPoint(new ScanPoint(2, 1), 0.2);
            _flow.AddPoint(new ScanPoint(3, 1), 0.3);
            Assert.That(Segments, Is.EqualTo(1), "no request storm: one in flight");
            Assert.That(_flow.CanConfirm, Is.False);
            AnswerSegment();
            Run(0.4);
            Assert.That(_flow.Mask, Is.Null, "the answer for the first point alone is stale and not shown");
            Assert.That(Segments, Is.EqualTo(2), "one request for the three points");
            Assert.That(LastRequest.Points.Length, Is.EqualTo(3));
            Assert.That(_flow.Refining, Is.True);
            AnswerSegment();
            Run(0.5);
            Assert.That(_flow.Mask, Is.Not.Null);
            Assert.That(_flow.CanConfirm, Is.True);
        }

        [Test]
        public void ConfirmGatesTheBuild()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);
            AnswerSegment();
            Run(0.2);
            _flow.Build(0.3);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Selecting), "no build before Confirm");
            Assert.That(_transport.Submitted.Any(r => r.Kind == ScanKind.Reconstruct), Is.False);
            Assert.That(_flow.Confirm(), Is.True);
            _flow.Build(0.4);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Building));
        }

        [Test]
        public void BuildOutsideTheMaskDoesNothing()
        {
            _flow.Begin(null);
            _flow.Build(0.1);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Aiming));
            Assert.That(_transport.Submitted, Is.Empty);
        }

        [Test]
        public void TheBuiltModelEndsTheFlowAndReachesTheSink()
        {
            CaptureAndMask();
            _flow.Build(4.0);
            Run(4.0);
            _transport.Complete(ScanKind.Reconstruct, _flow.BuildHandle, true);
            Run(5.0);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Done));
            Assert.That(_sink.Results.Count, Is.EqualTo(1));
            Assert.That(_sink.Results[0].HasAnchor, Is.True);
        }

        [Test]
        public void AFailedBuildFailsTheFlowWithTheServersReason()
        {
            CaptureAndMask();
            _flow.Build(4.0);
            Run(4.0);
            _transport.Complete(ScanKind.Reconstruct, _flow.BuildHandle, false, "the model could not be built");
            Run(5.0);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Failed));
            Assert.That(_flow.Error.Code, Is.EqualTo(ScanErrorCode.ServerFailed));
            Assert.That(_flow.Error.Message, Does.Contain("could not be built"));
        }

        // A failed refinement (a server error, a timeout, a point on nothing) keeps the photo, the points and the
        // last good mask; the next point, Undo or Clear asks again.
        [Test]
        public void AFailedMask_KeepsThePointsAndTheLastGoodMask_AndTheNextPointAsksAgain()
        {
            var errors = new List<ScanError>();
            _flow.RefineFailed += e => errors.Add(e);
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);
            AnswerSegment();
            Run(0.2);
            byte[] good = _flow.Mask;
            _flow.AddPoint(new ScanPoint(3, 2), 0.3);
            AnswerSegment("No object was found at those points.", success: false);
            Run(0.4);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Selecting));
            Assert.That(_flow.Points.Count, Is.EqualTo(2), "the points stay");
            Assert.That(_flow.Mask, Is.SameAs(good), "the last good mask stays");
            Assert.That(_flow.RefineError.Message, Does.Contain("No object"));
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That(_flow.CanConfirm, Is.False, "the mask is not for these points");
            Assert.That(_flow.UndoPoint(0.5), Is.True);
            Assert.That(_flow.RefineError.IsError, Is.False);
            AnswerSegment();
            Run(0.6);
            Assert.That(_flow.CanConfirm, Is.True);
            _flow.Retake();
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Aiming));
        }

        [Test]
        public void WithoutAScanServiceCaptureFailsAtOnce()
        {
            var flow = new ScanFlow(() => null);
            flow.Begin(null);
            Assert.That(flow.Capture(Photo, 4, 3), Is.False);
            Assert.That(flow.State, Is.EqualTo(ScanFlowState.Failed));
            Assert.That(flow.Error.Code, Is.EqualTo(ScanErrorCode.NotInitialized));
        }

        [Test]
        public void RetakeWhileTheMaskIsOnItsWayGoesStraightBackToAiming_AndDropsTheRequest()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);   // a mask on its way
            _states.Clear();
            _flow.Retake();
            Assert.That(_states, Is.EqualTo(new[] { ScanFlowState.Aiming }), "no Failed flash from the cancelled request");
            Assert.That(_service.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void BeginAgainCancelsAMaskStillOnItsWay()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);   // a mask on its way
            _states.Clear();
            _flow.Begin(null);
            Assert.That(_states, Is.EqualTo(new[] { ScanFlowState.Aiming }));
            Assert.That(_service.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void CloseDuringTheMaskDropsItsAnswer()
        {
            _flow.Begin(null);
            _flow.Capture(Photo, 4, 3);
            _flow.AddPoint(new ScanPoint(1, 1), 0.1);   // a mask on its way
            _states.Clear();
            _flow.Close();
            Assert.That(_states, Is.EqualTo(new[] { ScanFlowState.Idle }), "no Failed flash from the cancelled request");
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Idle));
            Run(1.0);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Idle));
            Assert.That(_transport.Submitted.FindAll(r => r.Kind == ScanKind.Reconstruct), Is.Empty);
        }

        [Test]
        public void CancelBuildStopsWaitingAndReturnsToIdle()
        {
            CaptureAndMask();
            _flow.Build(4.0);
            Run(4.0);
            _states.Clear();
            _flow.CancelBuild();
            Assert.That(_states, Is.EqualTo(new[] { ScanFlowState.Idle }), "Idle once");
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Idle));
            _transport.Complete(ScanKind.Reconstruct, _builds[0], true);
            Run(5.0);
            Assert.That(_flow.State, Is.EqualTo(ScanFlowState.Idle), "a late answer does not reopen the flow");
        }
    }
}
#endif
