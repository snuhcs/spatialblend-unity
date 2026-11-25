// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FreeformRestoration")]
    public class RecordedAnchorPoseReplayTests
    {
        private sealed class Sink : IAnchorPoseFrameReplaySink
        {
            public readonly List<AnchorPoseFrame> Frames = new List<AnchorPoseFrame>();
            public readonly List<long> Attempts = new List<long>();
            public bool Reject;
            public int Resets;
            public Action OnApply;
            public bool TryApplyWorldFrame(AnchorPoseFrame frame)
            {
                Attempts.Add(frame.sequence);
                if (Reject)
                    return false;
                Frames.Add(frame);
                OnApply?.Invoke();
                return true;
            }
            public void ClearWorldAnchor(string id)
            {
            }
            public void ResetWorldAnchorReplay()
            {
                ++Resets;
            }
        }
        private sealed class CheckedSink : IAnchorPoseFrameCheckedReplaySink
        {
            public bool RejectReset;
            public int ResetAttempts;
            public readonly List<long> Frames = new List<long>();
            public bool TryApplyWorldFrame(AnchorPoseFrame frame) { Frames.Add(frame.sequence); return true; }
            public void ClearWorldAnchor(string id) { }
            public void ResetWorldAnchorReplay() { }
            public bool TryResetWorldAnchorReplay() { ++ResetAttempts; return !RejectReset; }
        }
        private GameObject _object;
        private RecordedAnchorPoseSource _source;
        private Sink _sink;
        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("anchor-replay");
            _source = _object.AddComponent<RecordedAnchorPoseSource>();
            _sink = new Sink();
            _source.SetSink(_sink);
            Assert.That(_source.TrySetKnownAnchorIds(new[] { "root" }, out _), Is.True);
        }
        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_object);
        }
        private static string Frame(
            long sequence, double time,
            string anchors = "[{\"anchorId\":\"root\",\"worldPosition\":{\"x\":1,\"y\":2," +
                             "\"z\":3},\"worldRotation\":{\"x\":0,\"y\":0.7071067811865476," +
                             "\"z\":0,\"w\":0.7071067811865476}}]") =>
            $"{{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":{sequence},\"timeSeconds\":{time.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"anchors\":{anchors}}}";
        [Test]
        public void StrictDecoder_RequiresEveryFieldAndAllowsEmptySnapshot()
        {
            Assert.That(
                AnchorPoseJson.TryDecode(Frame(0, 0, "[]"), out AnchorPoseFrame empty, out _),
                Is.True);
            Assert.That(empty.anchors.Length, Is.EqualTo(0));
            Assert.That(AnchorPoseJson.TryDecode(Frame(long.MaxValue, 0), out _, out _), Is.True);
            foreach (string bad in new[] {
                         "{}", Frame(0, 0).Replace("\"schema\":1,", ""),
                         Frame(0, 0).Replace("\"sequence\":0", "\"sequence\":null"),
                         Frame(0, 0).Replace(
                             "\"sequence\":0",
                             "\"sequence\":0.0000000000000000000000000000000000001"),
                         Frame(0, 0).Replace("\"sequence\":0",
                                             "\"sequence\":1.000000000000000000000000000000000001"),
                         Frame(0, 0).Replace("\"sequence\":0", "\"sequence\":1e0"),
                         Frame(0, 0).Replace("\"sequence\":0", "\"sequence\":9223372036854775808"),
                         Frame(0, 0).Replace("\"x\":1", "\"x\":1,\"x\":1"),
                         Frame(0, 0).Replace("\"worldPosition\":{\"x\":1,\"y\":2,\"z\":3}",
                                             "\"worldPosition\":{\"x\":1,\"y\":2}"),
                         Frame(0, 0).Replace("\"w\":0.7071067811865476", "\"w\":\"bad\"")
                     })
                Assert.That(AnchorPoseJson.TryDecode(bad, out _, out _), Is.False);
        }

        [Test]
        public void CheckedResetFailure_PreservesSourceFencesAndBeginUsesOneAcknowledgement()
        {
            var oldSink = new CheckedSink();
            _source.SetSink(oldSink);
            var candidateSource = _object.AddComponent<RecordedAnchorPoseSource>();
            var candidateSink = new Sink();
            candidateSource.SetSink(candidateSink);
            Assert.That(candidateSource.TrySetKnownAnchorIds(new[] { "root" }, out _), Is.True);

            string oldData = Path.GetTempFileName(), oldManifest = Path.GetTempFileName();
            string candidateData = Path.GetTempFileName(), candidateManifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(oldData, Frame(0, 0) + "\n" + Frame(1, 1));
                WriteManifest(oldData, oldManifest);
                var oldRecording = new AnchorPoseRecording();
                Assert.That(oldRecording.TryLoadCommitted(oldData, oldManifest, new[] { "root" }, out _), Is.True);
                File.WriteAllText(candidateData, Frame(99, 5));
                WriteManifest(candidateData, candidateManifest);
                var candidateRecording = new AnchorPoseRecording();
                Assert.That(candidateRecording.TryLoadCommitted(candidateData, candidateManifest,
                    new[] { "root" }, out _), Is.True);

                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                Assert.That(player.Configure(_source, oldRecording, out _), Is.True);
                Assert.That(player.Begin(out _), Is.True);
                Assert.That(player.TryAdvanceTo(0d, out _), Is.True);
                Assert.That(player.NextFrameIndex, Is.EqualTo(1));

                oldSink.RejectReset = true;
                Assert.That(player.Configure(candidateSource, candidateRecording, out _), Is.False,
                    "Configure must retain its old source/recording when checked stop reset fails");
                Assert.That(player.NextFrameIndex, Is.EqualTo(1),
                    "failed Configure must retain the old nonzero replay cursor");
                Assert.That(player.Begin(out _), Is.False,
                    "a rejected checked reset must also prevent Begin from clearing source fences");
                Assert.That(_source.TrySubmitJson(Frame(0, 0), out _), Is.False,
                    "failed Begin reset must retain the old source sequence/time fence");

                oldSink.RejectReset = false;
                oldSink.Frames.Clear();
                int before = oldSink.ResetAttempts;
                Assert.That(player.Begin(out _), Is.True);
                Assert.That(oldSink.ResetAttempts, Is.EqualTo(before + 1),
                    "Begin must use the successful known-anchor reset exactly once");
                Assert.That(player.TryAdvanceTo(1d, out _), Is.True);
                Assert.That(oldSink.Frames, Is.EqualTo(new[] { 0L, 1L }),
                    "recovery must replay the retained old recording through the old source/sink");
                Assert.That(candidateSink.Frames, Is.Empty,
                    "the rejected candidate source/recording must not receive replay frames");
            }
            finally
            {
                File.Delete(oldData); File.Delete(oldManifest);
                File.Delete(candidateData); File.Delete(candidateManifest);
            }
        }
        [Test]
        public void SourceJsonAndReplay_UseFullSnapshotsAndRetryRejectedFrame()
        {
            Assert.That(_source.TrySubmitJson(Frame(0, 0, "[]"), out _), Is.True);
            Assert.That(_sink.Frames[0].anchors.Length, Is.EqualTo(0));
            var recording = new AnchorPoseRecording();
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 0) + "\n" + Frame(1, 1));
                WriteManifest(data, manifest);
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _),
                            Is.True);
                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                Assert.That(player.Configure(_source, recording, out _), Is.True);
                Assert.That(player.Begin(out _), Is.True);
                _sink.Reject = true;
                Assert.That(player.TryAdvanceTo(0, out _), Is.False);
                Assert.That(player.NextFrameIndex, Is.EqualTo(0));
                _sink.Reject = false;
                Assert.That(player.TryAdvanceTo(1, out _), Is.True);
                Assert.That(_sink.Frames.Count, Is.GreaterThanOrEqualTo(2));
                player.Stop();
                Assert.That(_sink.Resets, Is.GreaterThan(0));
            }
            finally
            {
                File.Delete(data);
                File.Delete(manifest);
            }
        }

        [Test]
        public void ExplicitElapsedZeroIsRelativeToNonzeroClipOrigin()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 10) + "\n" + Frame(1, 11));
                WriteManifest(data, manifest);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _),
                            Is.True);
                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                Assert.That(player.Configure(_source, recording, out _), Is.True);
                Assert.That(player.Begin(out _), Is.True);
                Assert.That(player.TryAdvanceTo(0d, out _), Is.True);
                Assert.That(_sink.Frames[_sink.Frames.Count - 1].sequence, Is.EqualTo(0));
                Assert.That(player.TryAdvanceTo(.999d, out _), Is.True);
                Assert.That(player.NextFrameIndex, Is.EqualTo(1));
            }
            finally
            {
                File.Delete(data);
                File.Delete(manifest);
            }
        }
        [Test]
        public void CommitMarkerRejectsOrphanHashMismatchAndNonmonotonicRows()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                var r = new AnchorPoseRecording();
                File.WriteAllText(data, Frame(0, 0));
                Assert.That(r.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.False);
                WriteManifest(data, manifest, "bad");
                Assert.That(r.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.False);
                File.WriteAllText(data, Frame(1, 0) + "\n" + Frame(0, 1));
                WriteManifest(data, manifest);
                Assert.That(r.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.False);
            }
            finally
            {
                File.Delete(data);
                File.Delete(manifest);
            }
        }
        [Test]
        public void CheckedInCorrectedYawLiteralFacesMappedS6Forward()
        {
            const string source =
                "S6 corrected exporter p1.jsonl, SHA-256 " +
                "ced7fb619245dda850e26dc371b0afad5a190d9442a7afbb85d3f520edfe66e8";
            const string json =
                "{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":0," +
                "\"timeSeconds\":0.0,\"anchors\":[{\"anchorId\":\"root\"," +
                "\"worldPosition\":{\"x\":0.1058882474899292,\"y\":0." +
                "8082262607291341,\"z\":5.719246864318848},\"worldRotation\":{\"x\":" +
                "0.0,\"y\":-0.45502603282941145,\"z\":0.0,\"w\":0.8904781353000911}}]}";
            Assert.That(source, Does.Contain("SHA-256"));
            Assert.That(AnchorPoseJson.TryDecode(json, out AnchorPoseFrame frame, out _), Is.True);
            Vector3 forward = frame.anchors[0].worldRotation * Vector3.forward;
            Assert.That(forward.x, Is.EqualTo(-.8107f).Within(.001f));
            Assert.That(forward.z, Is.EqualTo(.5855f).Within(.001f));
        }

        [Test]
        public void RecordingReturnsDeepCopiesAndLoadsOneByteSnapshot()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            Func<string, byte[]> prior = AnchorPoseRecording.ReadBytesForTesting;
            try
            {
                File.WriteAllText(data, Frame(0, 0));
                WriteManifest(data, manifest);
                byte[] snapshot = File.ReadAllBytes(data);
                AnchorPoseRecording.ReadBytesForTesting = path =>
                {
                    if (path == data)
                    {
                        File.WriteAllText(data, Frame(1, 1));
                        return snapshot;
                    }
                    return File.ReadAllBytes(path);
                };
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _),
                            Is.True);
                Assert.That(recording.TryGetFrame(0, out AnchorPoseFrame first), Is.True);
                first.anchors[0].anchorId = "mutated";
                Assert.That(recording.TryGetFrame(0, out AnchorPoseFrame reread), Is.True);
                Assert.That(reread.sequence, Is.EqualTo(0));
                Assert.That(reread.anchors[0].anchorId, Is.EqualTo("root"));
            }
            finally
            {
                AnchorPoseRecording.ReadBytesForTesting = prior;
                File.Delete(data);
                File.Delete(manifest);
            }
        }

        [Test]
        public void FailedConfigureAndExplicitStopDuringSinkCallbackLeaveNoPartialReplay()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 0));
                WriteManifest(data, manifest);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _),
                            Is.True);
                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                Assert.That(player.Configure(_source, recording, out _), Is.True);
                Assert.That(player.Begin(out _), Is.True);
                Assert.That(player.Configure(null, recording, out _), Is.False);
                Assert.That(player.IsRunning, Is.True);
                _sink.OnApply = () =>
                {
                    string ignored;
                    Assert.That(player.Configure(_source, recording, out ignored), Is.False);
                    player.Stop();
                };
                Assert.That(player.TryAdvanceTo(0, out _), Is.False);
                Assert.That(player.IsRunning, Is.False);
                Assert.That(player.NextFrameIndex, Is.EqualTo(0));
                Assert.That(_sink.Resets, Is.GreaterThan(0));
            }
            finally
            {
                File.Delete(data);
                File.Delete(manifest);
            }
        }

        [Test]
        public void BeginStopDisableRebeginAndConfigureFreezeHaveExplicitLifecycleSemantics()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 0));
                WriteManifest(data, manifest);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.True);
                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                Assert.That(player.Configure(_source, recording, out _), Is.True);

                // Configure snapshots the committed frames.  Reloading the caller's recording
                // after Configure must not change the replay that was already configured.
                File.WriteAllText(data, Frame(0, 0).Replace("\"x\":1", "\"x\":9"));
                WriteManifest(data, manifest);
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.True);

                Assert.That(player.Begin(out _), Is.True);
                int resetsBeforeSecondBegin = _sink.Resets;
                Assert.That(player.Begin(out _), Is.False, "Begin must not restart a live replay");
                Assert.That(player.IsRunning, Is.True);
                Assert.That(_sink.Resets, Is.EqualTo(resetsBeforeSecondBegin));

                bool reentrantConfigureSucceeded = true;
                _sink.OnApply = () =>
                {
                    reentrantConfigureSucceeded = player.Configure(_source, recording, out _);
                };
                Assert.That(player.TryAdvanceTo(0, out _), Is.True);
                Assert.That(reentrantConfigureSucceeded, Is.False);
                Assert.That(_sink.Frames[0].anchors[0].worldPosition.x, Is.EqualTo(1f));

                player.Stop();
                Assert.That(player.IsRunning, Is.False);
                Assert.That(player.Begin(out _), Is.True);
                int resetsBeforeDisable = _sink.Resets;
                player.enabled = false;
                // Non-ExecuteAlways runtime hooks do not run automatically in
                // Edit Mode. Exercise the actual disable hook explicitly.
                MethodInfo disable = typeof(AnchorPoseReplayPlayer).GetMethod("OnDisable",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(disable, Is.Not.Null);
                disable.Invoke(player, null);
                Assert.That(player.IsRunning, Is.False);
                Assert.That(_sink.Resets, Is.GreaterThan(resetsBeforeDisable));
                player.enabled = true;
                Assert.That(player.Begin(out _), Is.True);
            }
            finally { File.Delete(data); File.Delete(manifest); }
        }

        [Test]
        public void ManifestRelativeLocatorLoadsMovedBundleWithNullLegacyPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "anchor-bundle-" + Guid.NewGuid());
            string moved = root + "-moved";
            Directory.CreateDirectory(root);
            string data = Path.Combine(root, "p1.jsonl"), manifest = Path.Combine(root, "p1.manifest.json");
            try
            {
                File.WriteAllText(data, Frame(0, 0));
                byte[] bytes = File.ReadAllBytes(data);
                string expectedHash = Hash(bytes);
                WriteLocatorManifest(manifest, bytes, QuoteJson("p1.jsonl"), data);
                Directory.Move(root, moved);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(null, Path.Combine(moved, "p1.manifest.json"), new[] { "root" }, out _), Is.True);
                Assert.That(Hash(File.ReadAllBytes(Path.Combine(moved, "p1.jsonl"))), Is.EqualTo(expectedHash));
                Assert.That(recording.TryGetFrame(0, out AnchorPoseFrame frame), Is.True);
                Assert.That(frame.sequence, Is.EqualTo(0));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(moved)) Directory.Delete(moved, true); }
        }

        [Test]
        public void PresentLocatorRejectsMalformedValueEvenWhenLegacyPathIsValid()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 0));
                byte[] bytes = File.ReadAllBytes(data);
                var recording = new AnchorPoseRecording();
                string[] locators =
                {
                    "null",
                    "1",
                    QuoteJson(string.Empty),
                    QuoteJson("../outside.jsonl"),
                    QuoteJson(Path.GetFullPath(data)),
                    QuoteJson("nested\\file.jsonl"),
                    QuoteJson("nested//file.jsonl")
                };
                foreach (string locator in locators)
                {
                    WriteLocatorManifest(manifest, bytes, locator, data);
                    Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _),
                                Is.False, "locator " + locator + " must override legacy jsonl_path");
                }
            }
            finally { File.Delete(data); File.Delete(manifest); }
        }

        [Test]
        public void ManifestRelativeLocatorRejectsFileAndDirectorySymlinks()
        {
            string root = Path.Combine(Path.GetTempPath(), "anchor-links-" + Guid.NewGuid());
            string bundle = Path.Combine(root, "bundle");
            string outside = Path.Combine(root, "outside");
            string fileLink = Path.Combine(bundle, "linked.jsonl");
            string directoryLink = Path.Combine(bundle, "linked-directory");
            try
            {
                Directory.CreateDirectory(bundle);
                Directory.CreateDirectory(outside);
                string outsideFile = Path.Combine(outside, "outside.jsonl");
                File.WriteAllText(outsideFile, Frame(0, 0));
                byte[] bytes = File.ReadAllBytes(outsideFile);
                string manifest = Path.Combine(bundle, "p1.manifest.json");

                CreateUnixSymlink(outsideFile, fileLink);
                Assert.That((File.GetAttributes(fileLink) & FileAttributes.ReparsePoint) != 0,
                            Is.True, "fixture must be a real file symlink");
                WriteLocatorManifest(manifest, bytes, QuoteJson("linked.jsonl"));
                Assert.That(new AnchorPoseRecording().TryLoadCommitted(null, manifest, new[] { "root" }, out _),
                            Is.False);

                CreateUnixSymlink(outside, directoryLink);
                Assert.That((File.GetAttributes(directoryLink) & FileAttributes.ReparsePoint) != 0,
                            Is.True, "fixture must be a real directory symlink");
                WriteLocatorManifest(manifest, bytes, QuoteJson("linked-directory/outside.jsonl"));
                Assert.That(new AnchorPoseRecording().TryLoadCommitted(null, manifest, new[] { "root" }, out _),
                            Is.False);
            }
            finally
            {
                if (File.Exists(fileLink)) File.Delete(fileLink);
                if (Directory.Exists(directoryLink)) Directory.Delete(directoryLink);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void LegacyManifestRequiresExactSuppliedJsonlPath()
        {
            string data = Path.GetTempFileName();
            string sameBytesElsewhere = Path.GetTempFileName();
            string manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 0));
                File.WriteAllText(sameBytesElsewhere, Frame(0, 0));
                var recording = new AnchorPoseRecording();
                WriteManifest(data, manifest);
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.True);
                WriteManifest(sameBytesElsewhere, manifest);
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.False);
            }
            finally
            {
                File.Delete(data);
                File.Delete(sameBytesElsewhere);
                File.Delete(manifest);
            }
        }

        [Test]
        public void AutomaticPlaybackUsesInjectedClockAndRecordingTimeOrigin()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 100) + "\n" + Frame(1, 101));
                WriteManifest(data, manifest);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.True);
                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                double clock = 400d;
                player.ClockForTesting = () => clock;
                player.PlayOnUpdateForTesting = true;
                Assert.That(player.PlayOnUpdateForTesting, Is.True);
                Assert.That(player.enabled, Is.True);
                Assert.That(player.Configure(_source, recording, out _), Is.True);
                Assert.That(player.Begin(out _), Is.True);
                player.TickUpdateForTesting();
                Assert.That(_sink.Frames.Count, Is.EqualTo(1));
                Assert.That(_sink.Frames[0].sequence, Is.EqualTo(0));
                clock += .999d;
                player.TickUpdateForTesting();
                Assert.That(_sink.Frames.Count, Is.EqualTo(1));
                clock += .001d;
                player.TickUpdateForTesting();
                Assert.That(_sink.Frames.Count, Is.EqualTo(2));
                Assert.That(_sink.Frames[1].sequence, Is.EqualTo(1));
            }
            finally { File.Delete(data); File.Delete(manifest); }
        }

        [Test]
        public void AutomaticPlaybackRetriesRejectedCursorAndFailsClosedOnClockRollback()
        {
            string data = Path.GetTempFileName(), manifest = Path.GetTempFileName();
            try
            {
                File.WriteAllText(data, Frame(0, 100) + "\n" + Frame(1, 101) + "\n" + Frame(2, 102));
                WriteManifest(data, manifest);
                var recording = new AnchorPoseRecording();
                Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out _), Is.True);
                var player = _object.AddComponent<AnchorPoseReplayPlayer>();
                double clock = 0d;
                player.ClockForTesting = () => clock;
                player.PlayOnUpdateForTesting = true;
                Assert.That(player.Configure(_source, recording, out _), Is.True);
                Assert.That(player.Begin(out _), Is.True);
                player.TickUpdateForTesting();
                Assert.That(player.NextFrameIndex, Is.EqualTo(1));
                _sink.Reject = true;
                clock = 1d;
                player.TickUpdateForTesting();
                Assert.That(player.NextFrameIndex, Is.EqualTo(1));
                Assert.That(_sink.Attempts, Is.EqualTo(new[] { 0L, 1L }));
                _sink.Reject = false;
                player.TickUpdateForTesting();
                Assert.That(player.NextFrameIndex, Is.EqualTo(2));
                Assert.That(_sink.Attempts, Is.EqualTo(new[] { 0L, 1L, 1L }));
                clock = .5d;
                player.TickUpdateForTesting();
                Assert.That(player.NextFrameIndex, Is.EqualTo(2));
                Assert.That(_sink.Frames.Count, Is.EqualTo(2));
                Assert.That(_sink.Attempts, Is.EqualTo(new[] { 0L, 1L, 1L }),
                            "rollback must not replay accepted frames or resurrect the cursor");
                clock = 2d;
                player.TickUpdateForTesting();
                Assert.That(_sink.Frames.Count, Is.EqualTo(3));
                Assert.That(_sink.Frames[2].sequence, Is.EqualTo(2));
            }
            finally { File.Delete(data); File.Delete(manifest); }
        }

        [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "symlink")]
        private static extern int UnixSymlink(string target, string linkPath);

        private static void CreateUnixSymlink(string target, string linkPath)
        {
            int result = UnixSymlink(target, linkPath);
            Assert.That(result, Is.EqualTo(0), "Could not create symlink fixture: " +
                        Marshal.GetLastWin32Error());
        }

        private static void WriteLocatorManifest(string manifest, byte[] bytes, string locatorJson,
                                                 string legacyPath = null)
        {
            string legacy = legacyPath == null ? string.Empty : ",\"jsonl_path\":" +
                QuoteJson(Path.GetFullPath(legacyPath));
            File.WriteAllText(
                manifest,
                "{\"provenance\":{\"orientation_mode\":\"s6-canonical-spatial-anchor-" +
                "yaw-only: Unity yaw = pi/2 - S6 yaw; raw tracker quaternions retained as " +
                "evidence, not relabeled as Unity anchor rotation\"},\"output\":{\"publication_" +
                "state\":\"committed\",\"jsonl_locator\":" + locatorJson + legacy +
                ",\"jsonl_byte_count\":" + bytes.Length + ",\"jsonl_sha256\":\"" + Hash(bytes) +
                "\",\"anchor_pose_schema\":1,\"frame\":\"unity-world\"}}");
        }

        private static string QuoteJson(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void WriteManifest(string data, string manifest, string forcedHash = null)
        {
            byte[] bytes = File.ReadAllBytes(data);
            string hash = forcedHash ?? Hash(bytes);
            File.WriteAllText(
                manifest,
                "{\"provenance\":{\"orientation_mode\":\"s6-canonical-spatial-anchor-" +
                    "yaw-only: Unity yaw = pi/2 - S6 yaw; raw tracker quaternions " +
                    "retained as evidence, not relabeled as Unity anchor " +
                    "rotation\"},\"output\":{\"publication_state\":\"committed\",\"jsonl_" +
                    "path\":\"" + Path.GetFullPath(data).Replace("\\", "\\\\") +
                    "\",\"jsonl_byte_count\":" + bytes.Length + ",\"jsonl_sha256\":\"" + hash +
                    "\",\"anchor_pose_schema\":1,\"frame\":\"unity-world\"}}");
        }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(bytes))
                .Replace("-", "")
                .ToLowerInvariant();
        }
    }
}
#endif
