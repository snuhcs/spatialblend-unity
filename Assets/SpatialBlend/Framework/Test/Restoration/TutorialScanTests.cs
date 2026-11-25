// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>Chapter 3's scan: the canned transport, the injection seam on SessionManager, the backend that chooses canned or real.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class TutorialScanTests
    {
        private static readonly byte[] Model = { 1, 2, 3, 4, 5 };
        private FakeTutorialClock _clock;
        private string _dir;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeTutorialClock();
            _dir = Path.Combine(Path.GetTempPath(), "tutorial-scan-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            try { Directory.Delete(_dir, true); } catch { /* temp */ }
        }

        private SessionManager NewManager()
        {
            var go = new GameObject("session-manager");
            _objects.Add(go);
            return go.AddComponent<SessionManager>();
        }

        private static byte[] SomeImage => new byte[] { 9, 9, 9 };
        private static byte[] OneByOneMask => new byte[] { 1 };

        [Test]
        public void CannedTransport_Config_OffersTheTwoStepPipelineLikeARealPrimaryServer()
        {
            var config = CannedScanTransport.MakeConfig(_dir);
            Assert.That(config.Validate(out ScanError error), Is.True, error.ToString());
            Assert.That(config.AddressesFor(ScanKind.Segment), Is.EqualTo(new[] { CannedScanTransport.Address }));
            Assert.That(config.AddressesFor(ScanKind.Reconstruct), Is.EqualTo(new[] { CannedScanTransport.Address }));
        }

        [Test]
        public void SessionManager_InitializeWith_StartsTheServiceOverTheGivenTransport()
        {
            var manager = NewManager();
            var transport = new CannedScanTransport(_clock, () => Model);
            Assert.That(manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir)), Is.True);
            Assert.That(manager.IsInitialized, Is.True);
            Assert.That(manager.Service.ActiveWorkflow, Is.EqualTo(ScanWorkflow.SegmentThenReconstruct), "a mask per tap, then Reconstruct: the app's own two-step flow");
            Assert.That(transport.Connects, Is.EqualTo(2), "no native library, no socket: the canned transport answered, once per pipeline (mask, reconstruct)");
            Assert.That(manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir)), Is.True, "a second call keeps the running service");
            Assert.That(transport.Connects, Is.EqualTo(2));
        }

        [Test]
        public void SessionManager_InitializeWith_RefusesMissingPartsAndResetServiceDropsTheService()
        {
            var manager = NewManager();
            Assert.That(manager.InitializeWith(null, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir)), Is.False);
            Assert.That(manager.LastError, Is.Not.Empty);
            Assert.That(manager.InitializeWith(new CannedScanTransport(_clock, () => Model), SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir)), Is.True);
            manager.ResetService();
            Assert.That(manager.Service, Is.Null);
            Assert.That(manager.IsInitialized, Is.False);
            manager.ResetService();   // harmless twice
        }

        [Test]
        public void CannedScan_AfterTheFakeWait_WritesTheSampleModelAndReportsSuccess()
        {
            var manager = NewManager();
            var sink = new RecordingScanSink();
            manager.ResultSink = sink;
            var transport = new CannedScanTransport(_clock, () => Model, delaySeconds: 3.0);
            manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir));
            var pending = new List<ScanPendingPhase>();
            manager.Service.PendingChanged += e => pending.Add(e.Phase);
            ScanSubmission? submitted = null;
            manager.Service.Submitted += s => submitted = s;

            Assert.That(manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out long handle, out ScanError error), Is.True, error.ToString());
            Assert.That(submitted.HasValue, Is.True);
            Assert.That(transport.PendingCount, Is.EqualTo(1));
            Assert.That(manager.Service.IsPending(handle), Is.True, "the fake wait");

            _clock.Advance(2.9); transport.Tick(); manager.Service.Update(2.9);
            Assert.That(sink.Results, Is.Empty, "not yet");
            _clock.Advance(0.2); transport.Tick(); manager.Service.Update(3.1);
            Assert.That(sink.Results.Count, Is.EqualTo(1));
            var result = sink.Results[0];
            Assert.That(File.Exists(result.ModelPath), Is.True, "the 'server' wrote the model where a real one would");
            Assert.That(File.ReadAllBytes(result.ModelPath), Is.EqualTo(Model));
            Assert.That(result.ModelFileName, Does.EndWith(ScanResponseParser.ModelSuffix));
            Assert.That(pending, Is.EqualTo(new[] { ScanPendingPhase.Started, ScanPendingPhase.Succeeded }));
            Assert.That(transport.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void CannedScan_CanFail_AndTheServiceReportsItToTheSink()
        {
            var manager = NewManager();
            var sink = new RecordingScanSink();
            manager.ResultSink = sink;
            var transport = new CannedScanTransport(_clock, () => Model, delaySeconds: 0.0) { FailNext = true };
            manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir));
            manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out _, out _);
            transport.Tick(); manager.Service.Update(0.1);
            Assert.That(sink.Results, Is.Empty);
            Assert.That(sink.Failures.Count, Is.EqualTo(1));
            Assert.That(ScanFailureText.ToUserMessage(sink.Failures[0].Error), Is.EqualTo("The server couldn't build a 3D copy from this photo. Try another photo."));
            Assert.That(transport.FailNext, Is.False, "only the next one");
        }

        [Test]
        public void CannedScan_WithAnUnwritableModel_FailsInsteadOfThrowing()
        {
            var manager = NewManager();
            var sink = new RecordingScanSink();
            manager.ResultSink = sink;
            var transport = new CannedScanTransport(_clock, () => Model, (path, bytes) => throw new IOException("disk full"), 0.0);
            manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir));
            manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out _, out _);
            Assert.DoesNotThrow(() => { transport.Tick(); manager.Service.Update(0.1); });
            Assert.That(sink.Failures.Count, Is.EqualTo(1));
        }

        [Test]
        public void CannedScan_NeedsAPoint_ThePanelSaysSo()
        {
            var manager = NewManager();
            manager.InitializeWith(new CannedScanTransport(_clock, () => Model), SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir));
            Assert.That(manager.Service.TryRequestSegment(SomeImage, 3, 1, Array.Empty<ScanPoint>(), 0.0, out _, out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.NoPoints));
            Assert.That(ScanFailureText.ToUserMessage(error), Is.EqualTo("Tap the object on the photo first, then press Reconstruct 3D."));
        }

        [Test]
        public void Backend_WithNoRunningService_IsCanned_TakesTheSinkAndGivesItBack()
        {
            var manager = NewManager();
            var appSink = new RecordingScanSink();
            manager.ResultSink = appSink;
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            Assert.That(backend.Prepare(manager), Is.EqualTo(TutorialScanMode.Canned));
            Assert.That(backend.Mode, Is.EqualTo(TutorialScanMode.Canned));
            Assert.That(manager.ResultSink, Is.SameAs(backend), "the tutorial's result is a local practice object, not the app's");
            Assert.That(manager.Service.ActiveWorkflow, Is.EqualTo(ScanWorkflow.SegmentThenReconstruct));
            ScanReconstructionResult got = null;
            backend.ResultReady += r => got = r;
            backend.Canned.DelaySeconds = 0.0;
            manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out _, out _);
            backend.Tick(); manager.Service.Update(0.1);
            Assert.That(got, Is.Not.Null);
            Assert.That(appSink.Results, Is.Empty, "never shared with the app");

            backend.Release();
            Assert.That(manager.ResultSink, Is.SameAs(appSink), "the app's sink is back");
            Assert.That(manager.Service, Is.Null, "the canned service is gone, as the tutorial found it");
            Assert.That(backend.Mode, Is.EqualTo(TutorialScanMode.None));
        }

        [Test]
        public void Backend_CancelInFlight_EndsTheTutorialsRequests_SoNoLateResultComesBack()
        {
            var manager = NewManager();
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            backend.Prepare(manager);
            ScanReconstructionResult got = null;
            backend.ResultReady += r => got = r;
            backend.Canned.DelaySeconds = 0.0;
            manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out long handle, out _);
            Assert.That(manager.Service.IsPending(handle), Is.True);
            backend.CancelInFlight();
            Assert.That(manager.Service.IsPending(handle), Is.False, "cancelled");
            backend.Tick(); manager.Service.Update(0.1);
            Assert.That(got, Is.Null, "its result never arrives");
            backend.Release();
        }

        [Test]
        public void Backend_UpgradesToARealScanWhenAServiceAlreadyRuns_AndLeavesItRunning()
        {
            var manager = NewManager();
            var real = new FakeScanTransport();
            var config = new ScanSessionConfig { ServerAddress = "scan.example:50051", ClientId = "real", CacheDirectory = _dir, OutputDirectory = _dir };
            Assert.That(manager.InitializeWith(real, new FakeScanFiles(), config), Is.True);
            var service = manager.Service;
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            Assert.That(backend.Prepare(manager), Is.EqualTo(TutorialScanMode.Real));
            Assert.That(backend.Canned, Is.Null);
            Assert.That(manager.ResultSink, Is.SameAs(backend), "a real result is loaded locally too");
            backend.Release();
            Assert.That(manager.Service, Is.SameAs(service), "the app's own service is not touched");
        }

        [Test]
        public void Backend_RealMode_ARequestStillInFlightWhenTheTutorialEnds_IsCancelled_AndItsLateResultNeverReachesTheAppsSink()
        {
            var manager = NewManager();
            var appSink = new RecordingScanSink();
            manager.ResultSink = appSink;
            var real = new FakeScanTransport();
            var config = new ScanSessionConfig { ServerAddress = "scan.example:50051", ClientId = "real", CacheDirectory = _dir, OutputDirectory = _dir };
            Assert.That(manager.InitializeWith(real, new FakeScanFiles(), config), Is.True);
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            var seen = new List<ScanReconstructionResult>();
            var failures = new List<ScanFailure>();
            backend.ResultReady += seen.Add;
            backend.Failed += failures.Add;
            Assert.That(backend.Prepare(manager), Is.EqualTo(TutorialScanMode.Real));

            Assert.That(manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out long handle, out _), Is.True);
            backend.Release();      // the user left during "Building your 3D copy..."
            Assert.That(manager.Service.IsPending(handle), Is.False, "the request was cancelled");
            Assert.That(manager.ResultSink, Is.SameAs(appSink));

            real.Complete(ScanKind.Reconstruct, handle, true);   // the server answers anyway
            manager.Service.Update(1.0);
            Assert.That(appSink.Results, Is.Empty, "never loaded as an object the app could share");
            Assert.That(seen, Is.Empty);
            Assert.That(appSink.Failures, Is.Empty, "and the cancellation was not reported to the app either");
            Assert.That(manager.Service, Is.Not.Null, "the app's own service keeps running");
        }

        [Test]
        public void Backend_RealMode_AFinishedRequestIsNotCancelledAgain()
        {
            var manager = NewManager();
            var real = new FakeScanTransport();
            var config = new ScanSessionConfig { ServerAddress = "scan.example:50051", ClientId = "real", CacheDirectory = _dir, OutputDirectory = _dir };
            manager.InitializeWith(real, new FakeScanFiles(), config);
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            var seen = new List<ScanReconstructionResult>();
            backend.ResultReady += seen.Add;
            backend.Prepare(manager);
            manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out long handle, out _);
            real.Complete(ScanKind.Reconstruct, handle, true);
            manager.Service.Update(1.0);
            Assert.That(seen.Count, Is.EqualTo(1), "a real result reaches the tutorial's sink");
            Assert.DoesNotThrow(backend.Release);
        }

        [Test]
        public void Backend_ServerConfiguredButUnreachable_FallsBackToCanned_AndCompletes()
        {
            // spatialblend-local.json names a server that does not answer: no pipeline connects, the app has no scan service.
            var manager = NewManager();
            var down = new FakeScanTransport();
            down.RefusedAddresses.Add("scan.example:50051");
            var config = new ScanSessionConfig { ServerAddress = "scan.example:50051", ClientId = "real", CacheDirectory = _dir, OutputDirectory = _dir };
            Assert.That(manager.InitializeWith(down, new FakeScanFiles(), config), Is.False);
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            ScanReconstructionResult got = null;
            backend.ResultReady += r => got = r;
            Assert.That(backend.Prepare(manager), Is.EqualTo(TutorialScanMode.Canned));
            backend.Canned.DelaySeconds = 0.0;
            Assert.That(manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out _, out _), Is.True);
            backend.Tick(0.1); manager.Service.Update(0.1);
            Assert.That(got, Is.Not.Null, "the canned server completed the scan");
            Assert.That(down.Submitted, Is.Empty, "and no request went to the unreachable server");
            backend.Release();
        }

        [Test]
        public void Backend_ARealServerThatFailsOrTimesOut_FallsBackToCanned_AndTheRealOnesLateAnswersAreDropped()
        {
            var manager = NewManager();
            var real = new FakeScanTransport();
            var config = new ScanSessionConfig { ServerAddress = "scan.example:50051", ClientId = "real", CacheDirectory = _dir, OutputDirectory = _dir, ReconstructTimeoutSeconds = 5.0 };
            manager.InitializeWith(real, new FakeScanFiles(), config);
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            var seen = new List<ScanReconstructionResult>();
            var failures = new List<ScanFailure>();
            backend.ResultReady += seen.Add;
            backend.Failed += failures.Add;
            Assert.That(backend.Prepare(manager), Is.EqualTo(TutorialScanMode.Real));
            Assert.That(manager.Service.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 0.0, out long handle, out _), Is.True);
            Assert.That(real.Submitted.Count, Is.EqualTo(1), "the reachable server is the one used");
            Assert.That(backend.ActiveService, Is.SameAs(manager.Service));

            manager.Service.Update(6.0);                      // the request's own timeout is the failure signal: no new timer
            Assert.That(failures.Count, Is.EqualTo(1));
            Assert.That(failures[0].Error.Code, Is.EqualTo(ScanErrorCode.Timeout));

            Assert.That(backend.FallBackToCanned(), Is.True);
            Assert.That(backend.Mode, Is.EqualTo(TutorialScanMode.Canned));
            Assert.That(backend.ActiveService, Is.Not.SameAs(manager.Service));
            backend.Canned.DelaySeconds = 0.0;
            Assert.That(backend.ActiveService.TryRequestReconstruct(SomeImage, OneByOneMask, 1, 1, null, 7.0, out _, out _), Is.True);
            backend.Tick(7.1);
            Assert.That(seen.Count, Is.EqualTo(1), "the canned server completed it");
            Assert.That(real.Submitted.Count, Is.EqualTo(1), "nothing more went to the real server");

            real.Complete(ScanKind.Reconstruct, handle, true);   // the abandoned request answers late
            manager.Service.Update(8.0);
            Assert.That(seen.Count, Is.EqualTo(1));
            backend.Release();
            Assert.That(manager.Service, Is.Not.Null, "the app's own service is untouched");
        }

        [Test]
        public void Backend_ForwardsAFailureToItsListeners()
        {
            var backend = new TutorialScanBackend(_clock, () => Model, _dir);
            ScanFailure got = null;
            backend.Failed += f => got = f;
            backend.OnScanFailed(new ScanFailure { Error = new ScanError(ScanErrorCode.Timeout, "slow") });
            Assert.That(got, Is.Not.Null);
            Assert.That(ScanFailureText.ToUserMessage(got.Error), Is.EqualTo("The scan took too long. Try again, or skip this step."));
            Assert.DoesNotThrow(() => backend.OnScanFailed(null));
        }

        [Test]
        public void ScanFailureText_MapsEveryErrorCode_InPlainWords()
        {
            foreach (ScanErrorCode code in Enum.GetValues(typeof(ScanErrorCode)))
            {
                string text = ScanFailureText.ToUserMessage(new ScanError(code, "technical detail 0x1F"));
                if (code == ScanErrorCode.None) { Assert.That(text, Is.Empty); continue; }
                Assert.That(text, Is.Not.Empty, code.ToString());
                Assert.That(text, Does.Not.Contain("0x1F").And.Not.Contain(code.ToString()), "no technical words: " + code);
                Assert.That(text.Length, Is.LessThan(100), code.ToString());
            }
        }

        [Test]
        public void TheBundledSampleFiles_AreThere_AndTheModelIsAGlb_AndThePhotoDecodes()
        {
            byte[] model = TutorialAssets.LoadSampleModel();
            Assert.That(model, Is.Not.Null, "Resources/TutorialAssets/sample_object.bytes");
            Assert.That(System.Text.Encoding.ASCII.GetString(model, 0, 4), Is.EqualTo("glTF"));
            var photo = TutorialAssets.LoadSamplePhoto();
            try
            {
                Assert.That(photo, Is.Not.Null, "Resources/TutorialAssets/sample_photo.bytes");
                Assert.That(photo.width, Is.EqualTo(640));
                Assert.That(photo.height, Is.EqualTo(480));
                Assert.That(photo.GetPixels32().Length, Is.EqualTo(640 * 480), "readable, so the scan panel can capture it");
            }
            finally { if (photo != null) UnityEngine.Object.DestroyImmediate(photo); }
        }

        [Test]
        public void TheVirtualObjectsModel_IsAGlbOfItsOwn_NotTheVase()
        {
            byte[] shared = TutorialAssets.LoadSharedModel();
            Assert.That(shared, Is.Not.Null, "Resources/TutorialAssets/sample_shared.bytes");
            Assert.That(System.Text.Encoding.ASCII.GetString(shared, 0, 4), Is.EqualTo("glTF"));
            Assert.That(shared, Is.Not.EqualTo(TutorialAssets.LoadSampleModel()));
        }

        [Test]
        public void EveryStepIsSaidByItsClipOrShownForItsReadingTime()
        {
            var clips = TutorialScript.AllSteps().Select(step => step.ClipName)
                .Concat(TutorialScript.Frames.Where(f => f.ModeClipName != null).Select(f => f.ModeClipName)).ToArray();
            Assert.That(clips, Does.Contain("t3_0"), "the lead-in of the scan task is a clip too");
            foreach (string name in clips)
            {
                // Every clip name maps to its line, which is the subtitle (and its timing) when no clip is bundled.
                string line = TutorialScript.LineFor(name);
                Assert.That(line, Is.Not.Null.And.Not.Empty, name);
                Assert.That(TutorialNarrator.ReadingSeconds(line), Is.InRange(TutorialNarrator.MinReadingSeconds, TutorialNarrator.MaxReadingSeconds), name);
                var clip = TutorialAssets.LoadClip(name);
                if (TutorialScript.PendingNarration.Contains(name))
                {
                    // Its line changed and its new audio is not delivered: the stale file is gone, so no old line plays.
                    Assert.That(clip, Is.Null, name + " is pending: add its file and remove it from PendingNarration");
                    continue;
                }
                if (clip != null) Assert.That(clip.length, Is.InRange(1.5f, 15f), name);
            }
            Assert.That(TutorialScript.LineFor("ch9_f99"), Is.Null);
            Assert.That(TutorialNarrator.ReadingSeconds(""), Is.EqualTo(0.0));
            Assert.That(TutorialAssets.LoadClip("ch9_f99"), Is.Null);
            Assert.That(TutorialAssets.LoadClip(null), Is.Null);
        }

        // ---- the vase's mask (SAM3's real answer for the sample photo, see ASSETS.md)

        private static readonly ScanPoint OnVase1 = new ScanPoint(300, 200), OnVase2 = new ScanPoint(340, 300);
        private static readonly ScanPoint OnFloor = new ScanPoint(100, 420), OnWall = new ScanPoint(80, 60);

        private static byte[] SamplePhotoPng()
        {
            var photo = TutorialAssets.LoadSamplePhoto();
            try { return photo.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(photo); }
        }

        private sealed class SegmentResults
        {
            public readonly List<ScanSegmentResult> Results = new List<ScanSegmentResult>();
            public ScanSegmentResult Last => Results[Results.Count - 1];
        }

        private SegmentResults WatchSegments(SessionManager manager)
        {
            var seen = new SegmentResults();
            manager.Service.SegmentCompleted += seen.Results.Add;
            return seen;
        }

        // The panel finds the scan service through SessionManager.Instance, which Awake publishes (EditMode does not run Awake).
        private SessionManager NewPublishedManager()
        {
            var manager = SessionManager.EnsureInstance();
            _objects.Add(manager.gameObject);
            typeof(SessionManager).GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(manager, null);
            Assert.That(SessionManager.Instance, Is.SameAs(manager));
            return manager;
        }

        private TutorialScanBackend CannedBackend(SessionManager manager)
        {
            var backend = new TutorialScanBackend(_clock, () => Model, _dir, CannedSegmenter.LoadSample);
            Assert.That(backend.Prepare(manager), Is.EqualTo(TutorialScanMode.Canned));
            return backend;
        }

        private static int Foreground(byte[] mask) => mask.Count(b => b != 0);

        [Test]
        public void TheBundledSampleMask_IsTheSam3MaskOfTheVase_OneBytePerPixelOfThePhoto()
        {
            byte[] mask = TutorialAssets.LoadSampleMask();
            Assert.That(mask, Is.Not.Null, "Resources/TutorialAssets/sample_mask.bytes");
            Assert.That(mask.Length, Is.EqualTo(TutorialAssets.SampleImageWidth * TutorialAssets.SampleImageHeight));
            Assert.That(Foreground(mask), Is.EqualTo(34870), "the SAM3 answer recorded in ASSETS.md");
            Assert.That(mask.Distinct().OrderBy(b => b), Is.EqualTo(new byte[] { 0, 1 }));
            var segmenter = CannedSegmenter.LoadSample();
            Assert.That(segmenter.IsInside(OnVase1), Is.True);
            Assert.That(segmenter.IsInside(OnVase2), Is.True);
            Assert.That(segmenter.IsInside(OnFloor), Is.False);
            Assert.That(segmenter.IsInside(OnWall), Is.False);
            Assert.That(segmenter.IsInside(new ScanPoint(-1, 5)), Is.False, "off the image");
            Assert.That(segmenter.IsInside(new ScanPoint(640, 5)), Is.False);
        }

        [Test]
        public void CannedSegment_TapOnTheVase_ComesBackAsTheMask_ThroughTheServicesOwnSegmentPath()
        {
            var manager = NewManager();
            var backend = CannedBackend(manager);
            var seen = WatchSegments(manager);
            var pending = new List<(ScanKind, ScanPendingPhase)>();
            manager.Service.PendingChanged += e => pending.Add((e.Kind, e.Phase));
            Assert.That(manager.Service.IsReady(ScanKind.Segment), Is.True);

            Assert.That(manager.Service.TryRequestSegment(SamplePhotoPng(), 640, 480, new[] { OnVase1 }, 0.0, out long handle, out ScanError error), Is.True, error.ToString());
            Assert.That(manager.Service.IsPending(handle), Is.True, "a short fake wait, like a server round trip");
            _clock.Advance(0.5); backend.Tick(); manager.Service.Update(0.5);
            Assert.That(seen.Results, Is.Empty, "not yet");
            _clock.Advance(0.5); backend.Tick(); manager.Service.Update(1.0);

            Assert.That(seen.Results.Count, Is.EqualTo(1));
            var result = seen.Last;
            Assert.That(result.Success, Is.True);
            Assert.That(result.EventHandle, Is.EqualTo(handle));
            Assert.That((result.Width, result.Height), Is.EqualTo((640, 480)));
            Assert.That(result.Mask, Is.EqualTo(TutorialAssets.LoadSampleMask()), "the bundled SAM3 mask, byte for byte");
            Assert.That(pending.Select(p => p.Item2), Is.EqualTo(new[] { ScanPendingPhase.Started, ScanPendingPhase.Succeeded }));
            backend.Release();
        }

        [Test]
        public void CannedSegment_SeveralTaps_AnyOnTheVaseGivesTheMask_AllOffItSelectNothing()
        {
            var manager = NewManager();
            var backend = CannedBackend(manager);
            var seen = WatchSegments(manager);
            void Ask(params ScanPoint[] points)
            {
                Assert.That(manager.Service.TryRequestSegment(SamplePhotoPng(), 640, 480, points, 0.0, out _, out ScanError e), Is.True, e.ToString());
                _clock.Advance(1.0); backend.Tick(); manager.Service.Update(0.1);
            }

            Ask(OnVase1, OnVase2);
            Assert.That(seen.Last.Success, Is.True);
            Assert.That(Foreground(seen.Last.Mask), Is.EqualTo(34870));
            Ask(OnFloor, OnVase1);
            Assert.That(seen.Last.Success, Is.True, "the point on the vase decides");
            Ask(OnFloor);
            Assert.That(seen.Last.Success, Is.False, "a tap outside the vase selects nothing");
            Assert.That(seen.Last.Mask, Is.Null);
            Assert.That(seen.Last.Error.Code, Is.EqualTo(ScanErrorCode.ServerFailed));
            Ask(OnFloor, OnWall);
            Assert.That(seen.Last.Success, Is.False);
            Assert.That(seen.Results.Count, Is.EqualTo(4));
            Assert.That(Directory.GetFiles(_dir, "seg_mask_*"), Is.Empty, "the service removed each mask file after reading it");
            backend.Release();
        }

        [Test]
        public void CannedSegment_WithNoSampleMask_FailsInsteadOfThrowing()
        {
            var manager = NewManager();
            var transport = new CannedScanTransport(_clock, () => Model);
            manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir));
            var seen = WatchSegments(manager);
            manager.Service.TryRequestSegment(SomeImage, 640, 480, new[] { OnVase1 }, 0.0, out _, out _);
            _clock.Advance(1.0);
            Assert.DoesNotThrow(() => { transport.Tick(); manager.Service.Update(0.1); });
            Assert.That(seen.Last.Success, Is.False);
        }

        [Test]
        public void CannedSegment_DoesNotUseUpTheNextReconstructionFailure()
        {
            var manager = NewManager();
            var transport = new CannedScanTransport(_clock, () => Model, segmenter: CannedSegmenter.LoadSample()) { FailNext = true };
            manager.InitializeWith(transport, SessionManager.DiskFileSystem, CannedScanTransport.MakeConfig(_dir));
            var seen = WatchSegments(manager);
            manager.Service.TryRequestSegment(SomeImage, 640, 480, new[] { OnVase1 }, 0.0, out _, out _);
            _clock.Advance(1.0); transport.Tick(); manager.Service.Update(0.1);
            Assert.That(seen.Last.Success, Is.True);
            Assert.That(transport.FailNext, Is.True, "only a reconstruction is the one that fails");
        }

        [Test]
        public void ReconstructAfterTheMask_WorksAsBefore_TheSameSampleModelComesBack()
        {
            var manager = NewManager();
            var backend = CannedBackend(manager);
            var seen = WatchSegments(manager);
            var sink = new List<ScanReconstructionResult>();
            backend.ResultReady += sink.Add;
            backend.Canned.DelaySeconds = 0.0;
            manager.Service.TryRequestSegment(SamplePhotoPng(), 640, 480, new[] { OnVase1 }, 0.0, out _, out _);
            _clock.Advance(1.0); backend.Tick(); manager.Service.Update(0.1);
            var mask = seen.Last;
            Assert.That(mask.Success, Is.True);

            Assert.That(manager.Service.TryRequestReconstruct(SomeImage, mask.Mask, mask.Width, mask.Height, null, 0.2, out _, out ScanError error), Is.True, error.ToString());
            backend.Tick(); manager.Service.Update(0.3);

            Assert.That(sink.Count, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(sink[0].ModelPath), Is.EqualTo(Model));
            backend.Release();
        }

        private GameObject NewScanPanelFromPrefab(out PassthroughPanelManagerHandle handle, Texture2D photo)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Scanning/Prefabs/PassthroughPanel.prefab");
            Assert.That(prefab, Is.Not.Null, "the scan panel prefab");
            var instance = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            _objects.Add(instance);
            var panel = instance.GetComponent<SpatialBlend.Samples.Scanning.PassthroughPanelManager>();
            typeof(SpatialBlend.Samples.Scanning.PassthroughPanelManager).GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(panel, null);
            panel.StillOverride = photo;
            handle = new PassthroughPanelManagerHandle(panel);
            return instance;
        }

        private sealed class PassthroughPanelManagerHandle
        {
            public readonly SpatialBlend.Samples.Scanning.PassthroughPanelManager Panel;
            public PassthroughPanelManagerHandle(SpatialBlend.Samples.Scanning.PassthroughPanelManager panel) { Panel = panel; }

            private static System.Reflection.FieldInfo Field(string name) =>
                typeof(SpatialBlend.Samples.Scanning.PassthroughPanelManager).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            /// <summary>A tap on the photo at a point given as the server sees it (x right, y down), through the panel's own point handling.</summary>
            public void Tap(ScanPoint serverPoint)
            {
                var tex = (Texture2D)Field("_capturedTex").GetValue(Panel);
                var pixel = new Vector2((float)serverPoint.X, tex.height - 1f - (float)serverPoint.Y);   // the panel's pixels have y up
                typeof(SpatialBlend.Samples.Scanning.PassthroughPanelManager).GetMethod("AddPickedPoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(Panel, new object[] { pixel, new Vector2(pixel.x / tex.width, pixel.y / tex.height) });
            }

            public UnityEngine.UI.RawImage Overlay => (UnityEngine.UI.RawImage)Field("m_maskOverlayImage").GetValue(Panel);
            public bool OverlayShown => Overlay != null && Overlay.gameObject.activeSelf && Overlay.texture != null;
            public byte[] LatestMask => (byte[])Field("_latestMaskBytes").GetValue(Panel);

            public void Undo() => typeof(SpatialBlend.Samples.Scanning.PassthroughPanelManager).GetMethod("UndoLastPoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(Panel, null);
        }

        [Test]
        public void ThePanel_DrawsTheMaskOfTheVase_WithItsOwnOverlay_WhenTheVaseIsTapped_AndNotWhenTheFloorIs()
        {
            var manager = NewPublishedManager();
            var backend = CannedBackend(manager);
            var photo = TutorialAssets.LoadSamplePhoto();
            NewScanPanelFromPrefab(out var view, photo);
            try
            {
                Assert.That(view.Overlay, Is.Not.Null, "the prefab has the panel's mask overlay (RawImage/MaskImage)");
                view.Panel.OpenPanel();
                view.Panel.CaptureImage();
                view.Tap(OnFloor);
                _clock.Advance(1.0); backend.Tick(); manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(view.OverlayShown, Is.False, "a tap outside the vase draws nothing");
                Assert.That(view.LatestMask, Is.Null, "and the panel holds no mask, so Reconstruct 3D would ask for a tap on the object");

                view.Tap(OnVase1);
                Assert.That(view.OverlayShown, Is.False, "the mask is on its way");
                _clock.Advance(1.0); backend.Tick(); manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(view.OverlayShown, Is.True, "the same overlay a real server's answer draws");
                Assert.That(view.LatestMask, Is.EqualTo(TutorialAssets.LoadSampleMask()));
                var overlay = (Texture2D)view.Overlay.texture;
                Assert.That((overlay.width, overlay.height), Is.EqualTo((640, 480)));
                var pixels = overlay.GetPixels32();
                Assert.That(pixels.Count(c => c.a > 0), Is.EqualTo(34870), "exactly the vase");
                // the overlay is drawn bottom-up: the vase's centre (server row 250) is row 229 of the texture
                Assert.That(pixels[(480 - 1 - 250) * 640 + 320].a, Is.GreaterThan(0));
                Assert.That(pixels[(480 - 1 - 420) * 640 + 100].a, Is.EqualTo(0));

                view.Tap(OnFloor);                        // a second, stray tap: the vase still decides
                _clock.Advance(1.0); backend.Tick(); manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(view.OverlayShown, Is.True);
                // Undo removes the tap dots with Destroy, which EditMode refuses (the panel's code is written for play mode).
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                try { view.Undo(); view.Undo(); view.Undo(); }   // back to no points: the overlay goes
                finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
                Assert.That(view.OverlayShown, Is.False);
            }
            finally
            {
                // Closing the panel removes the tap dots with Destroy, which EditMode refuses to do (the app's panel code is written for play mode).
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                try { view.Panel.ClosePanel(); }
                finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
                backend.Release();
                UnityEngine.Object.DestroyImmediate(photo);
            }
        }

        [Test]
        public void ThePanel_ReconstructAfterTheMask_SendsItAndTheSampleModelComesBack_AndWithoutAMaskItAsksForATap()
        {
            var manager = NewPublishedManager();
            var backend = CannedBackend(manager);
            backend.Canned.DelaySeconds = 0.0;
            var results = new List<ScanReconstructionResult>();
            backend.ResultReady += results.Add;
            var photo = TutorialAssets.LoadSamplePhoto();
            NewScanPanelFromPrefab(out var view, photo);
            ScanError? failed = null;
            view.Panel.RequestFailed += e => failed = e;
            try
            {
                view.Panel.OpenPanel();
                view.Panel.CaptureImage();
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, "[PassthroughPanelManager] No mask available. Add points and wait for segment.");
                view.Panel.SendToServer();             // no tap yet
                Assert.That(failed.HasValue, Is.True);
                Assert.That(ScanFailureText.ToUserMessage(failed.Value), Is.EqualTo("Tap the object on the photo first, then press Reconstruct 3D."));

                failed = null;
                view.Tap(OnVase1);
                _clock.Advance(1.0); backend.Tick(); manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(view.OverlayShown, Is.True);
                // A sent request closes the panel, which removes the tap dots with Destroy; EditMode refuses that (the panel's code is written for play mode).
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                try { view.Panel.SendToServer(); }
                finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
                Assert.That(failed.HasValue, Is.False, "the request went out: " + (failed.HasValue ? failed.Value.ToString() : ""));
                backend.Tick(); manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(results.Count, Is.EqualTo(1), "Reconstruct after the mask works as before");
            }
            finally
            {
                backend.Release();
                UnityEngine.Object.DestroyImmediate(photo);
            }
        }

        // ---- box prompts (scan UI Concept A) --------------------------------------------

        /// <summary>The vase's box in the sample photo, as a projected 3D box would frame it (the vase spans 232..407 x 94..370).</summary>
        private static readonly ScanPixelBox VaseBox = new ScanPixelBox(215, 80, 425, 385);

        [Test]
        public void CannedSegment_ABoxAroundTheVase_GivesTheMask_AndSaysItUsedTheBox()
        {
            var manager = NewManager();
            var backend = CannedBackend(manager);
            var seen = WatchSegments(manager);
            Assert.That(manager.Service.TryRequestSegment(SamplePhotoPng(), 640, 480, ScanPrompt.ForBox(VaseBox, null), 0.0, out _, out ScanError error),
                Is.True, error.ToString());
            _clock.Advance(1.0); backend.Tick(); manager.Service.Update(0.1);

            Assert.That(seen.Last.Success, Is.True);
            Assert.That(seen.Last.UsedBox, Is.True);
            Assert.That(seen.Last.Mask, Is.EqualTo(TutorialAssets.LoadSampleMask()));
            Assert.That(manager.Service.BoxSupport, Is.EqualTo(ScanBoxSupport.Supported), "the canned server honours a box like the real one");
            backend.Release();
        }

        [Test]
        public void CannedSegment_ABoxOffTheVase_SelectsNothing()
        {
            var manager = NewManager();
            var backend = CannedBackend(manager);
            var seen = WatchSegments(manager);
            manager.Service.TryRequestSegment(SamplePhotoPng(), 640, 480, ScanPrompt.ForBox(new ScanPixelBox(0, 400, 120, 479), null), 0.0, out _, out _);
            _clock.Advance(1.0); backend.Tick(); manager.Service.Update(0.1);
            Assert.That(seen.Last.Success, Is.False);
            Assert.That(seen.Last.Error.Message, Does.Contain("in that box"));
            Assert.That(CannedSegmenter.LoadSample().BoxHolds(new ScanPixelBox(370, 80, 425, 385), 0.5), Is.False,
                "a box over only part of the vase does not select it");
        }

        [Test]
        public void ThePanel_WithABoxPrompt_ShowsTheMaskRightAfterCapture_WithoutATap()
        {
            var manager = NewPublishedManager();
            var backend = CannedBackend(manager);
            var photo = TutorialAssets.LoadSamplePhoto();
            NewScanPanelFromPrefab(out var view, photo);
            try
            {
                view.Panel.StillBoxPrompt = VaseBox;
                view.Panel.OpenPanel();
                view.Panel.CaptureImage();
                _clock.Advance(1.0); backend.Tick(); manager.Service.Update(Time.realtimeSinceStartupAsDouble);
                Assert.That(view.OverlayShown, Is.True, "the drawn box is the prompt: no tap needed");
                Assert.That(view.LatestMask, Is.EqualTo(TutorialAssets.LoadSampleMask()));
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                try { view.Panel.ClosePanel(); }
                finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
                backend.Release();
                UnityEngine.Object.DestroyImmediate(photo);
            }
        }

        // ---- the practice vase in the user's box (scan UI Concept A, task 3.1)

        [Test]
        public void TheHologramCapture_WithoutAGraphicsDevice_UsesTheVasesOutline_InsideItsProjectedBox()
        {
            var holder = new GameObject("hologram");
            _objects.Add(holder);
            holder.transform.position = new Vector3(0f, 0.7f, 1.2f);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.transform.SetParent(holder.transform, false);
            body.transform.localPosition = Vector3.up * 0.2f;
            body.transform.localScale = new Vector3(0.25f, 0.2f, 0.25f);
            CannedSegmenter got = null;
            var source = new TutorialHologramFrameSource(null, holder, m => got = m);
            try
            {
                Assert.That(source.TryCapture(out byte[] png, out int w, out int h, out ScanCameraModel camera, out Texture2D tex), Is.True);
                UnityEngine.Object.DestroyImmediate(tex);
                Assert.That((w, h), Is.EqualTo((640, 480)));
                Assert.That(got, Is.Not.Null, "the canned server gets the mask rendered with this photo");
                Assert.That(ScanBoxProjection.TryBuildPrompt(camera, new Vector3(0f, 0.95f, 1.2f), new Vector3(0.38f, 0.5f, 0.38f),
                    Quaternion.identity, out ScanPrompt prompt, out _), Is.True, "the user's box is in this photo");
                Assert.That(got.BoxHolds(prompt.Box.Value, 0.9), Is.True, "the vase's mask lies in the box's prompt");
                Assert.That(got.Answer(prompt.ToWire(true), out bool usedBox), Is.Not.Null);
                Assert.That(usedBox, Is.True);
            }
            finally
            {
                source.Stop();
            }
        }

        // The card's live view in the tutorial is the practice vase rendered from the eye, into ONE kept render
        // texture (the same each call), with the vase back on its own layer after each render; Stop frees it.
        [Test]
        public void TheHologramLiveView_RendersIntoOneKeptTexture_AndPutsTheLayersBack()
        {
            var holder = new GameObject("hologram");
            _objects.Add(holder);
            holder.transform.position = new Vector3(0f, 0.7f, 1.2f);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.transform.SetParent(holder.transform, false);
            int layer = body.layer;
            var source = new TutorialHologramFrameSource(null, holder, null);
            try
            {
                Assert.That(source.TryGetPreview(out Texture first, out ScanCameraModel camera), Is.True);
                Assert.That(first, Is.InstanceOf<RenderTexture>());
                Assert.That((first.width, first.height), Is.EqualTo((TutorialHologramFrameSource.PhotoWidth, TutorialHologramFrameSource.PhotoHeight)));
                Assert.That(camera.IsValid, Is.True, "the card crops the live view by the box with this camera");
                Assert.That(source.TryGetPreview(out Texture second, out _), Is.True);
                Assert.That(second, Is.SameAs(first), "the same texture every call");
                Assert.That(body.layer, Is.EqualTo(layer), "the vase is back on its layer");
                source.Stop();
                Assert.That(first == null, Is.True, "Stop frees the live view's texture");
            }
            finally
            {
                source.Stop();
            }
        }

        [Test]
        public void ADarkOrEmptyRender_IsNotKept_AndALitVaseOnGrayIs()
        {
            int n = TutorialHologramFrameSource.PhotoWidth * TutorialHologramFrameSource.PhotoHeight;
            var gray = new Color32[n];
            var background = (Color32)TutorialHologramFrameSource.PhotoBackground;
            for (int i = 0; i < n; i++) gray[i] = background;
            Assert.That(TutorialHologramFrameSource.KeepRenderedPhoto(gray), Is.False, "the clear colour alone is not a photo");
            Assert.That(TutorialHologramFrameSource.KeepRenderedPhoto(new Color32[n]), Is.False, "a black read-back is not a photo");

            var panel = (Color32)UiTheme.Palette.PanelBottom;
            var darkClear = new Color32[n];
            for (int i = 0; i < n; i++) darkClear[i] = panel;
            Assert.That(TutorialHologramFrameSource.KeepRenderedPhoto(darkClear), Is.False, "the dark panel colour is not a photo");

            var blackVase = (Color32[])gray.Clone();
            Fill(blackVase, 280, 200, 80, 80, new Color32(0, 0, 0, 255));
            Assert.That(TutorialHologramFrameSource.KeepRenderedPhoto(blackVase), Is.False, "a black vase on the gray clear is the unlit render");

            var lit = (Color32[])gray.Clone();
            Fill(lit, 280, 200, 80, 80, (Color32)TutorialHologramFrameSource.SyntheticVase);
            Assert.That(TutorialHologramFrameSource.KeepRenderedPhoto(lit), Is.True, "a lit vase on the gray clear is the photo");
        }

        [Test]
        public void TheSyntheticPhoto_PaintsTheVaseInTheCentreOfAGrayFrame()
        {
            var ink = (Color32)TutorialHologramFrameSource.SyntheticVase;
            var background = (Color32)TutorialHologramFrameSource.PhotoBackground;
            Color32[] empty = TutorialHologramFrameSource.SyntheticPhoto(640, 480, null);
            Assert.That(empty[240 * 640 + 320], Is.EqualTo(ink), "with no mask, the vase is an ellipse in the centre");
            Assert.That(empty[0], Is.EqualTo(background), "the frame around it is gray");
            AssertCentred(empty, ink, 640, 480, 2f);

            var cam = new ScanCameraModel(Vector3.zero, Quaternion.identity, new Vector2(400f, 400f), new Vector2(320f, 240f),
                new Vector2Int(640, 480), 640, 480);
            byte[] mask = TutorialHologramFrameSource.OutlineMask(cam, new Bounds(new Vector3(0f, 0f, 2f), new Vector3(0.4f, 0.6f, 0.01f)));
            Color32[] painted = TutorialHologramFrameSource.SyntheticPhoto(640, 480, mask);
            Assert.That(painted[239 * 640 + 320], Is.EqualTo(ink), "the projected vase, flipped into texture rows, is in the centre");
            Assert.That(painted[0], Is.EqualTo(background));
            AssertCentred(painted, ink, 640, 480, 8f);
        }

        [Test]
        public void TheHologramCamera_LooksAtTheVase_AndLightsAGrayFrame()
        {
            var holder = new GameObject("hologram");
            _objects.Add(holder);
            holder.transform.position = new Vector3(0f, 0.7f, 1.2f);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.transform.SetParent(holder.transform, false);
            body.transform.localPosition = Vector3.up * 0.2f;
            body.transform.localScale = new Vector3(0.25f, 0.2f, 0.25f);
            var eye = new GameObject("eye");
            _objects.Add(eye);
            eye.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, -0.2f), Quaternion.LookRotation(Vector3.right));

            var source = new TutorialHologramFrameSource(() => eye.transform, holder, null);
            try
            {
                Assert.That(source.TryGetLiveCamera(out ScanCameraModel camera), Is.True);
                Vector3 target = body.GetComponent<Renderer>().bounds.center;
                Vector3 forward = camera.Rotation * Vector3.forward;
                Assert.That(camera.Position, Is.EqualTo(eye.transform.position));
                Assert.That(Vector3.Angle(forward, target - camera.Position), Is.LessThan(1f),
                    "the vase is in the centre, even when the eye looks away");

                Camera cam = source.CaptureCamera;
                Assert.That(cam.backgroundColor, Is.EqualTo(TutorialHologramFrameSource.PhotoBackground));
                Assert.That(cam.backgroundColor.grayscale, Is.GreaterThan(0.3f).And.GreaterThan(UiTheme.Palette.PanelBottom.grayscale),
                    "the clear colour is the lit gray, not the dark panel colour");
                Light light = cam.GetComponent<Light>();
                Assert.That(light, Is.Not.Null, "the capture camera lights the vase itself");
                Assert.That(light.type, Is.EqualTo(LightType.Directional));
                Assert.That(light.transform, Is.SameAs(cam.transform), "the light shines along the view, at the vase");
                Assert.That(light.intensity, Is.GreaterThanOrEqualTo(1f));
                Assert.That((light.cullingMask & (1 << TutorialHologramFrameSource.CaptureLayer)) != 0, Is.True);
            }
            finally
            {
                source.Stop();
            }
        }

        private static void Fill(Color32[] pixels, int x0, int y0, int w, int h, Color32 color)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    pixels[y * TutorialHologramFrameSource.PhotoWidth + x] = color;
        }

        private static void AssertCentred(Color32[] pixels, Color32 ink, int width, int height, float tolerance)
        {
            long sx = 0, sy = 0;
            int count = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (!pixels[y * width + x].Equals(ink)) continue;
                    sx += x;
                    sy += y;
                    count++;
                }
            Assert.That(count, Is.GreaterThan(TutorialHologramFrameSource.MinSilhouettePixels));
            Assert.That(sx / (float)count, Is.EqualTo((width - 1) * 0.5f).Within(tolerance), "the vase's centre across the photo");
            Assert.That(sy / (float)count, Is.EqualTo((height - 1) * 0.5f).Within(tolerance), "the vase's centre down the photo");
        }

        [Test]
        public void TheHologramOutline_IsAnEllipseInTheProjectedBounds()
        {
            var cam = new ScanCameraModel(Vector3.zero, Quaternion.identity, new Vector2(400f, 400f), new Vector2(320f, 240f),
                new Vector2Int(640, 480), 640, 480);
            byte[] mask = TutorialHologramFrameSource.OutlineMask(cam, new Bounds(new Vector3(0f, 0f, 2f), new Vector3(0.4f, 0.6f, 0.01f)));
            Assert.That(mask[240 * 640 + 320], Is.EqualTo(1), "the centre");
            Assert.That(mask[0], Is.EqualTo(0), "a corner of the photo");
            int count = mask.Count(b => b != 0);
            Assert.That(count, Is.GreaterThan(0.7 * Math.PI * 40 * 60).And.LessThan(1.3 * Math.PI * 40 * 60), "about pi * 40 * 60 pixels");
        }
    }
}
#endif
