// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanSessionConfigTests
    {
        [Test]
        public void ADefaultConfigIsDisabledAndHasNoCompiledInServer()
        {
            var config = new ScanSessionConfig();
            Assert.That(config.IsEnabled, Is.False);
            Assert.That(config.ServerAddress, Is.Empty);
            Assert.That(config.FallbackServerAddress, Is.Empty);
            Assert.That(config.Validate(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.InvalidConfig));
        }

        [TestCase("lab.example:9800", true)]
        [TestCase("scan-host.test:1", true)]
        [TestCase("host:65535", true)]
        [TestCase("host", false)]
        [TestCase("host:", false)]
        [TestCase(":9800", false)]
        [TestCase("host:0", false)]
        [TestCase("host:65536", false)]
        [TestCase("host:abc", false)]
        [TestCase("http://host:9800", false)]
        [TestCase("ho st:9800", false)]
        [TestCase("", false)]
        [TestCase("   ", false)]
        public void AddressMustBeHostAndPort(string address, bool valid)
        {
            Assert.That(ScanSessionConfig.TryParseAddress(address, out string reason), Is.EqualTo(valid), reason);
        }

        [Test]
        public void ASecondAddressAloneEnablesNothing()
        {
            // A fallback address alone enables nothing (there is no one-shot pipeline).
            var config = new ScanSessionConfig { FallbackServerAddress = "b:2" };
            Assert.That(config.IsEnabled, Is.False);
            Assert.That(config.Validate(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.InvalidConfig));
            Assert.That(config.AddressesFor(ScanKind.Segment), Is.Empty);
            Assert.That(config.AddressesFor(ScanKind.Reconstruct), Is.Empty);
            Assert.That(new ScanSessionConfig().AddressesFor(ScanKind.Segment), Is.Empty);
        }

        [Test]
        public void AddressOrderIsPrimaryThenSecond()
        {
            var config = new ScanSessionConfig { ServerAddress = "a:1", FallbackServerAddress = "b:2" };
            Assert.That(config.AddressesFor(ScanKind.Segment), Is.EqualTo(new[] { "a:1", "b:2" }));
            Assert.That(config.AddressesFor(ScanKind.Reconstruct), Is.EqualTo(new[] { "a:1", "b:2" }));
            config.FallbackServerAddress = "a:1";
            Assert.That(config.AddressesFor(ScanKind.Segment), Is.EqualTo(new[] { "a:1" }));
        }

        [Test]
        public void TheOneShotKindIsGone()
        {
            Assert.That(Enum.GetNames(typeof(ScanKind)), Is.EqualTo(new[] { "Segment", "Reconstruct" }));
            Assert.That(Enum.GetNames(typeof(ScanWorkflow)), Is.EqualTo(new[] { "None", "SegmentThenReconstruct" }));
        }

        [Test]
        public void AnInvalidFallbackIsRefusedButAnEmptyOneIsAllowed()
        {
            var config = new ScanSessionConfig { ServerAddress = "a:1", FallbackServerAddress = "" };
            Assert.That(config.Validate(out _), Is.True);
            config.FallbackServerAddress = "nonsense";
            Assert.That(config.Validate(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.InvalidConfig));
        }

        [Test]
        public void TimeoutsMustBePositive()
        {
            var config = new ScanSessionConfig { ServerAddress = "a:1", ReconstructTimeoutSeconds = 0 };
            Assert.That(config.Validate(out ScanError error), Is.False);
            Assert.That(error.Code, Is.EqualTo(ScanErrorCode.InvalidConfig));
        }

        [Test]
        public void TimeoutDependsOnTheKind()
        {
            var config = new ScanSessionConfig { SegmentTimeoutSeconds = 5, ReconstructTimeoutSeconds = 50 };
            Assert.That(config.TimeoutFor(ScanKind.Segment), Is.EqualTo(5));
            Assert.That(config.TimeoutFor(ScanKind.Reconstruct), Is.EqualTo(50));
        }

        [Test]
        public void ClientIdFollowsTheV1Rule()
        {
            Func<string, string> hasher = id => "0123456789abcdef";
            Assert.That(ScanSessionConfig.ResolveClientId("app", false, "dev", hasher), Is.EqualTo("app"));
            Assert.That(ScanSessionConfig.ResolveClientId("app", true, "dev", hasher), Is.EqualTo("app-01234567"));
            Assert.That(ScanSessionConfig.ResolveClientId("app", true, "unknown", hasher), Is.EqualTo("app"));
            Assert.That(ScanSessionConfig.ResolveClientId("app", true, "N/A", hasher), Is.EqualTo("app"));
            Assert.That(ScanSessionConfig.ResolveClientId("app", true, "", hasher), Is.EqualTo("app"));
            Assert.That(ScanSessionConfig.ResolveClientId(" ", false, "dev", hasher), Is.EqualTo("unity-client"));
            Assert.That(ScanSessionConfig.ResolveClientId("app", true, "dev", id => "abc"), Is.EqualTo("app-abc"));
            Assert.That(ScanSessionConfig.ResolveClientId("app", true, "dev", null), Is.EqualTo("app"));
        }
    }

    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanRequestBuilderTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 2, 9, 30, 15, 123, DateTimeKind.Utc);
        private static readonly byte[] Image = { 1, 2, 3 };

        private static ScanSessionConfig Config() => new ScanSessionConfig
        {
            ServerAddress = "host:9800",
            ClientId = "cli-1",
            CacheDirectory = "cache",
            OutputDirectory = "out",
        };

        [Test]
        public void SegmentRequestCarriesTheImagePointsAndAMaskPath()
        {
            var points = new[] { new ScanPoint(10, 20), new ScanPoint(30.5, 40.5) };
            ScanBuildResult build = ScanRequestBuilder.BuildSegment(Config(), Image, points, Now);

            Assert.That(build.Ok, Is.True, build.Error.ToString());
            Assert.That(build.Request.Kind, Is.EqualTo(ScanKind.Segment));
            Assert.That(build.Request.ClientId, Is.EqualTo("cli-1"));
            Assert.That(build.Request.ImageBytes, Is.SameAs(Image));
            Assert.That(build.Request.Points, Is.EqualTo(points));
            Assert.That(build.Request.OutputPath,
                Is.EqualTo(Path.Combine("cache", "seg_mask_cli-1_20261002_0930151230.bin")));
        }

        [Test]
        public void ReconstructRequestCarriesTheMaskAndAnOutputPrefix()
        {
            var mask = new byte[6];
            ScanBuildResult build = ScanRequestBuilder.BuildReconstruct(Config(), Image, mask, 3, 2, Now);

            Assert.That(build.Ok, Is.True, build.Error.ToString());
            Assert.That(build.Request.Kind, Is.EqualTo(ScanKind.Reconstruct));
            Assert.That(build.Request.MaskBytes, Is.SameAs(mask));
            Assert.That(build.Request.MaskWidth, Is.EqualTo(3));
            Assert.That(build.Request.MaskHeight, Is.EqualTo(2));
            Assert.That(build.Request.OutputPath,
                Is.EqualTo(Path.Combine("out", "sam3d_cli-1_20261002_0930151230")));
        }

        [Test]
        public void ABoxAloneIsAPrompt()
        {
            var points = new[] { new ScanPoint(1, 2, ScanPointLabel.BoxTopLeft), new ScanPoint(30, 40, ScanPointLabel.BoxBottomRight) };
            ScanBuildResult build = ScanRequestBuilder.BuildSegment(Config(), Image, points, Now);
            Assert.That(build.Ok, Is.True, build.Error.ToString());
            Assert.That(build.Request.Points, Is.EqualTo(points));
        }

        [Test]
        public void ABoxWithANegativeClickIsAPrompt()
        {
            var points = new[]
            {
                new ScanPoint(1, 2, ScanPointLabel.BoxTopLeft), new ScanPoint(30, 40, ScanPointLabel.BoxBottomRight),
                new ScanPoint(5, 5, ScanPointLabel.Negative),
            };
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, points, Now).Ok, Is.True);
        }

        [Test]
        public void ABoxWithItsCornersSwappedIsRefused()
        {
            var points = new[] { new ScanPoint(30, 40, ScanPointLabel.BoxTopLeft), new ScanPoint(1, 2, ScanPointLabel.BoxBottomRight) };
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, points, Now).Error.Code, Is.EqualTo(ScanErrorCode.InvalidPoint));
        }

        [Test]
        public void NegativeClicksAloneOrAHalfBoxAreRefused()
        {
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, new[] { new ScanPoint(5, 5, ScanPointLabel.Negative) }, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.NoPoints));
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, new[] { new ScanPoint(1, 1, ScanPointLabel.BoxTopLeft), new ScanPoint(5, 5) }, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.InvalidPoint));
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, new[]
            {
                new ScanPoint(1, 1, ScanPointLabel.BoxTopLeft), new ScanPoint(5, 5, ScanPointLabel.BoxBottomRight),
                new ScanPoint(6, 6, ScanPointLabel.BoxBottomRight),
            }, Now).Error.Code, Is.EqualTo(ScanErrorCode.InvalidPoint));
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, new[] { new ScanPoint(5, 5, (ScanPointLabel)7) }, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.InvalidPoint));
        }

        [Test]
        public void TwoRequestsInTheSameSecondGetDistinctPaths()
        {
            string a = ScanRequestBuilder.OutputPrefix(Config(), Now);
            string b = ScanRequestBuilder.OutputPrefix(Config(), Now.AddMilliseconds(5));
            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void EmptyOrMissingImagesAreRefused()
        {
            var points = new[] { new ScanPoint(1, 1) };
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), null, points, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.EmptyImage));
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), new byte[0], points, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.EmptyImage));
            Assert.That(ScanRequestBuilder.BuildReconstruct(Config(), null, new byte[4], 2, 2, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.EmptyImage));
        }

        [Test]
        public void SegmentNeedsAPointOrABox()
        {
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, null, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.NoPoints));
            Assert.That(ScanRequestBuilder.BuildSegment(Config(), Image, new ScanPoint[0], Now).Error.Code,
                Is.EqualTo(ScanErrorCode.NoPoints));
        }

        [Test]
        public void NonFiniteOrNegativePointsAreRefused()
        {
            foreach (var bad in new[]
            {
                new ScanPoint(double.NaN, 1), new ScanPoint(1, double.PositiveInfinity), new ScanPoint(-1, 1),
            })
            {
                ScanBuildResult build = ScanRequestBuilder.BuildSegment(Config(), Image, new[] { bad }, Now);
                Assert.That(build.Ok, Is.False);
                Assert.That(build.Error.Code, Is.EqualTo(ScanErrorCode.InvalidPoint));
            }
        }

        [Test]
        public void ReconstructRefusesAMissingOrMismatchedMask()
        {
            Assert.That(ScanRequestBuilder.BuildReconstruct(Config(), Image, null, 2, 2, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.EmptyMask));
            Assert.That(ScanRequestBuilder.BuildReconstruct(Config(), Image, new byte[5], 2, 2, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.InvalidMaskSize));
            Assert.That(ScanRequestBuilder.BuildReconstruct(Config(), Image, new byte[4], 0, 4, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.InvalidMaskSize));
        }

        [Test]
        public void ANullConfigOrBlankClientIdIsRefused()
        {
            var points = new[] { new ScanPoint(1, 1) };
            Assert.That(ScanRequestBuilder.BuildSegment(null, Image, points, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.InvalidConfig));
            ScanSessionConfig config = Config();
            config.ClientId = " ";
            Assert.That(ScanRequestBuilder.BuildSegment(config, Image, points, Now).Error.Code,
                Is.EqualTo(ScanErrorCode.InvalidConfig));
        }
    }

    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanResponseParserTests
    {
        private static ScanCompletion Done(bool ok, string message = "") => new ScanCompletion(1, 7, ok, message);

        [Test]
        public void SegmentReadsTheMaskAndDerivesItsHeight()
        {
            var files = new FakeScanFiles();
            files.Files["m.bin"] = new byte[12];
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(true), "m.bin", 4, 99, files);

            Assert.That(result.Success, Is.True, result.Error.ToString());
            Assert.That(result.EventHandle, Is.EqualTo(7));
            Assert.That(result.Width, Is.EqualTo(4));
            Assert.That(result.Height, Is.EqualTo(3));
            Assert.That(result.Mask.Length, Is.EqualTo(12));
            Assert.That(files.Deleted, Is.EqualTo(new[] { "m.bin" }));
        }

        [Test]
        public void SegmentFallsBackToTheImageHeightWhenTheLengthDoesNotDivide()
        {
            var files = new FakeScanFiles();
            files.Files["m.bin"] = new byte[10];
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(true), "m.bin", 4, 3, files);
            Assert.That(result.Success, Is.True);
            Assert.That(result.Width, Is.EqualTo(4));
            Assert.That(result.Height, Is.EqualTo(3));
        }

        [Test]
        public void SegmentReportsAServerFailureAndDropsAPartialFile()
        {
            var files = new FakeScanFiles();
            files.Files["m.bin"] = new byte[4];
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(false, "no GPU"), "m.bin", 2, 2, files);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Mask, Is.Null);
            Assert.That(result.Error.Code, Is.EqualTo(ScanErrorCode.ServerFailed));
            Assert.That(result.Error.Message, Is.EqualTo("no GPU"));
            Assert.That(files.Deleted, Is.EqualTo(new[] { "m.bin" }));
        }

        [TestCase("bad\nhost:80")]
        [TestCase("bad\u0007host:80")]
        [TestCase("bad\u00a0host:80")]
        [TestCase("bad\thost:80")]
        public void AHostWithWhitespaceOrControlCharactersIsRejected(string address)
        {
            Assert.That(ScanSessionConfig.TryParseAddress(address, out string reason), Is.False);
            Assert.That(reason, Does.Not.Contain("bad"));
        }

        [Test]
        public void ABlankServerMessageGetsADefault()
        {
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(false, "  "), "m.bin", 2, 2, new FakeScanFiles());
            Assert.That(result.Error.Message, Is.Not.Empty);
        }

        [Test]
        public void SegmentReportsAnUnreadableMaskAndStillTriesToCleanUp()
        {
            var files = new FakeScanFiles();
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(true), "gone.bin", 2, 2, files);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error.Code, Is.EqualTo(ScanErrorCode.MaskUnreadable));
            Assert.That(files.Deleted, Is.EqualTo(new[] { "gone.bin" }));
        }

        [Test]
        public void SegmentRefusesAnEmptyMask()
        {
            var files = new FakeScanFiles();
            files.Files["m.bin"] = new byte[0];
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(true), "m.bin", 2, 2, files);
            Assert.That(result.Error.Code, Is.EqualTo(ScanErrorCode.MissingOutput));
        }

        [Test]
        public void SegmentRefusesANonPositiveImageSize()
        {
            var files = new FakeScanFiles();
            files.Files["m.bin"] = new byte[4];
            ScanSegmentResult result = ScanResponseParser.ParseSegment(Done(true), "m.bin", 0, 0, files);
            Assert.That(result.Error.Code, Is.EqualTo(ScanErrorCode.InvalidMaskSize));
        }

        [Test]
        public void ReconstructionNamesTheServerOutputsAfterThePrefix()
        {
            string prefix = Path.Combine("out", "sam3d_cli_20261002_0930151230");
            bool ok = ScanResponseParser.TryParseReconstruction(Done(true), ScanKind.Reconstruct, prefix,
                out ScanReconstructionResult result, out ScanError error);

            Assert.That(ok, Is.True, error.ToString());
            Assert.That(result.ObjectName, Is.EqualTo("sam3d_cli_20261002_0930151230"));
            Assert.That(result.ModelFileName, Is.EqualTo("sam3d_cli_20261002_0930151230_objmodel.glb"));
            Assert.That(result.InfoFileName, Is.EqualTo("sam3d_cli_20261002_0930151230_objinfo.json"));
            Assert.That(result.Kind, Is.EqualTo(ScanKind.Reconstruct));
            Assert.That(result.EventHandle, Is.EqualTo(7));
            Assert.That(result.ModelPath, Is.EqualTo(prefix + "_objmodel.glb"));
        }

        [Test]
        public void ReconstructionFailsOnAServerFailureOrABlankPrefix()
        {
            Assert.That(ScanResponseParser.TryParseReconstruction(Done(false, "bad"), ScanKind.Reconstruct, "p",
                out _, out ScanError serverError), Is.False);
            Assert.That(serverError.Code, Is.EqualTo(ScanErrorCode.ServerFailed));

            Assert.That(ScanResponseParser.TryParseReconstruction(Done(true), ScanKind.Reconstruct, " ",
                out _, out ScanError blankError), Is.False);
            Assert.That(blankError.Code, Is.EqualTo(ScanErrorCode.MissingOutput));

            Assert.That(ScanResponseParser.TryParseReconstruction(Done(true), ScanKind.Reconstruct, "dir" + Path.DirectorySeparatorChar,
                out _, out ScanError dirError), Is.False);
            Assert.That(dirError.Code, Is.EqualTo(ScanErrorCode.MissingOutput));
        }

        [Test]
        public void TheRegionHintKeepsTheV1FootprintAndMargin()
        {
            var hint = new ScanRegionHint(new Vector3(1f, 0.5f, 2f), new Vector3(0.4f, 1f, 0.6f));
            Vector3[] corners = hint.FloorCorners();

            Assert.That(hint.Margin, Is.EqualTo(0.15f));
            Assert.That(corners.Length, Is.EqualTo(4));
            Assert.That(corners[0].x, Is.EqualTo(1f - 0.35f).Within(1e-5f));
            Assert.That(corners[0].z, Is.EqualTo(2f - 0.45f).Within(1e-5f));
            Assert.That(corners[2].x, Is.EqualTo(1f + 0.35f).Within(1e-5f));
            Assert.That(corners[2].z, Is.EqualTo(2f + 0.45f).Within(1e-5f));
            Assert.That(corners, Has.All.Matches<Vector3>(c => c.y == 0f));
            Assert.That(hint.YBottom, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(hint.YTop, Is.EqualTo(1f).Within(1e-5f));
        }
    }

    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanPendingTrackerTests
    {
        private static ScanPendingEntry Begin(ScanPendingTracker tracker, long handle, double now = 0, double timeout = 10)
        {
            Assert.That(tracker.Begin(handle, ScanKind.Reconstruct, now, timeout, "ctx", out ScanPendingEntry entry), Is.True);
            return entry;
        }

        [Test]
        public void BeginRejectsBadHandlesTimeoutsAndDuplicates()
        {
            var tracker = new ScanPendingTracker();
            Assert.That(tracker.Begin(0, ScanKind.Segment, 0, 5, null, out _), Is.False);
            Assert.That(tracker.Begin(-3, ScanKind.Segment, 0, 5, null, out _), Is.False);
            Assert.That(tracker.Begin(1, ScanKind.Segment, 0, 0, null, out _), Is.False);
            Assert.That(tracker.Begin(1, ScanKind.Segment, 0, 5, null, out _), Is.True);
            Assert.That(tracker.Begin(1, ScanKind.Segment, 0, 5, null, out _), Is.False);
            Assert.That(tracker.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void ARequestStartsPendingAndKeepsItsContext()
        {
            var tracker = new ScanPendingTracker();
            ScanPendingEntry entry = Begin(tracker, 4, now: 2, timeout: 8);

            Assert.That(entry.State, Is.EqualTo(ScanState.Pending));
            Assert.That(entry.Deadline, Is.EqualTo(10));
            Assert.That(entry.Context, Is.EqualTo("ctx"));
            Assert.That(tracker.IsPending(4), Is.True);
        }

        [Test]
        public void ACompletionIsAcceptedOnceAndThenLate()
        {
            var tracker = new ScanPendingTracker();
            Begin(tracker, 4);

            Assert.That(tracker.Complete(4, true, out ScanPendingEntry entry), Is.EqualTo(ScanCompleteOutcome.Accepted));
            Assert.That(entry.State, Is.EqualTo(ScanState.Succeeded));
            Assert.That(tracker.Complete(4, true, out _), Is.EqualTo(ScanCompleteOutcome.Late));
            Assert.That(tracker.Complete(99, true, out _), Is.EqualTo(ScanCompleteOutcome.Unknown));
            Assert.That(tracker.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void AFailedCompletionEndsInFailed()
        {
            var tracker = new ScanPendingTracker();
            Begin(tracker, 4);
            tracker.Complete(4, false, out ScanPendingEntry entry);
            Assert.That(entry.State, Is.EqualTo(ScanState.Failed));
            Assert.That(tracker.TryGetTerminalState(4, out ScanState state), Is.True);
            Assert.That(state, Is.EqualTo(ScanState.Failed));
        }

        [Test]
        public void ExpireMovesOnlyOverdueEntriesAndOrdersThemByHandle()
        {
            var tracker = new ScanPendingTracker();
            Begin(tracker, 9, now: 0, timeout: 5);
            Begin(tracker, 3, now: 0, timeout: 5);
            Begin(tracker, 5, now: 0, timeout: 50);

            Assert.That(tracker.Expire(4.99), Is.Empty);
            var expired = tracker.Expire(5);

            Assert.That(expired.ConvertAll(e => e.EventHandle), Is.EqualTo(new long[] { 3, 9 }));
            Assert.That(expired, Has.All.Matches<ScanPendingEntry>(e => e.State == ScanState.TimedOut));
            Assert.That(tracker.IsPending(5), Is.True);
            Assert.That(tracker.Expire(5), Is.Empty, "a timed-out entry does not expire twice");
        }

        [Test]
        public void ACompletionAfterTheTimeoutIsLate()
        {
            var tracker = new ScanPendingTracker();
            Begin(tracker, 4, timeout: 1);
            tracker.Expire(2);
            Assert.That(tracker.Complete(4, true, out _), Is.EqualTo(ScanCompleteOutcome.Late));
        }

        [Test]
        public void CancelEndsOneRequestAndCancelAllEndsTheRest()
        {
            var tracker = new ScanPendingTracker();
            Begin(tracker, 1);
            Begin(tracker, 2);
            Begin(tracker, 3);

            Assert.That(tracker.Cancel(2, out ScanPendingEntry cancelled), Is.True);
            Assert.That(cancelled.State, Is.EqualTo(ScanState.Cancelled));
            Assert.That(tracker.Cancel(2, out _), Is.False);
            Assert.That(tracker.Cancel(77, out _), Is.False);
            Assert.That(tracker.CancelAll().ConvertAll(e => e.EventHandle), Is.EqualTo(new long[] { 1, 3 }));
            Assert.That(tracker.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void TerminalMemoryIsBounded()
        {
            var tracker = new ScanPendingTracker();
            int total = ScanPendingTracker.TerminalMemory + 6;
            for (int i = 1; i <= total; i++)
            {
                Begin(tracker, i);
                tracker.Complete(i, true, out _);
            }

            Assert.That(tracker.Complete(1, true, out _), Is.EqualTo(ScanCompleteOutcome.Unknown));
            Assert.That(tracker.Complete(total, true, out _), Is.EqualTo(ScanCompleteOutcome.Late));
        }

        [Test]
        public void AHandleCanBeTrackedAgainAfterItEnded()
        {
            var tracker = new ScanPendingTracker();
            Begin(tracker, 4);
            tracker.Complete(4, true, out _);
            ScanPendingEntry again = Begin(tracker, 4);
            Assert.That(again.State, Is.EqualTo(ScanState.Pending));
            Assert.That(tracker.TryGetTerminalState(4, out _), Is.False);
        }
    }
}
#endif
