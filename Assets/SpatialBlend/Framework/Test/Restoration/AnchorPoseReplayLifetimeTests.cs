// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FreeformRestoration")]
    public class AnchorPoseReplayLifetimeTests
    {
        private sealed class Sink : IAnchorPoseFrameReplaySink
        {
            public readonly List<long> Frames = new List<long>();
            public int Resets;
            public Action OnApply;
            public bool TryApplyWorldFrame(AnchorPoseFrame frame)
            {
                Frames.Add(frame.sequence);
                OnApply?.Invoke();
                return true;
            }
            public void ClearWorldAnchor(string anchorId) { }
            public void ResetWorldAnchorReplay() { ++Resets; }
        }

        private sealed class CheckedSink : IAnchorPoseFrameCheckedReplaySink
        {
            public bool RejectReset;
            public int ResetAttempts;
            public bool TryApplyWorldFrame(AnchorPoseFrame frame) => true;
            public void ClearWorldAnchor(string anchorId) { }
            public void ResetWorldAnchorReplay() { }
            public bool TryResetWorldAnchorReplay()
            {
                ++ResetAttempts;
                return !RejectReset;
            }
        }

        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in _objects)
                UnityEngine.Object.DestroyImmediate(gameObject);
            _objects.Clear();
        }

        [Test]
        public void ReplaySinkLease_AcquiresExactOwnerAndRejectsForeignOrInvalidInputs()
        {
            RecordedAnchorPoseSource source = NewSource("lease-source");
            var sink = new Sink();
            Assert.That(source.TryAcquireReplaySink(sink, new[] { "root" }, out var lease, out string error),
                        Is.True, error);
            Assert.That(lease.IsCurrent, Is.True);
            Assert.That(source.TryAcquireReplaySink(new Sink(), new[] { "root" }, out _, out _), Is.False,
                        "a live lease must retain sole ownership");

            RecordedAnchorPoseSource occupied = NewSource("occupied-source");
            var foreign = new Sink();
            Assert.That(occupied.TrySetSink(foreign, out error), Is.True, error);
            Assert.That(occupied.TryAcquireReplaySink(new Sink(), new[] { "root" }, out _, out _), Is.False,
                        "an explicitly installed sink is not silently displaced");
            Assert.That(occupied.TryAcquireReplaySink(new Sink(), new[] { "bad\0id" }, out _, out _), Is.False,
                        "invalid candidate IDs must fail before source state changes");
            Assert.That(occupied.TrySetKnownAnchorIds(new[] { "root" }, out error), Is.True, error);
            Assert.That(occupied.TrySubmit(Frame(0), out error), Is.True, error);
            Assert.That(foreign.Frames, Is.EqualTo(new[] { 0L }));
        }

        [Test]
        public void ReplaySinkLease_StaleDisposeCannotClearExplicitReplacement()
        {
            RecordedAnchorPoseSource source = NewSource("replacement-source");
            var first = new Sink();
            Assert.That(source.TryAcquireReplaySink(first, new[] { "root" }, out var lease, out string error),
                        Is.True, error);
            var replacement = new Sink();
            Assert.That(source.TrySetSink(replacement, out error), Is.True, error);
            Assert.That(lease.IsCurrent, Is.False);
            lease.Dispose();
            Assert.That(source.TrySubmit(Frame(0), out error), Is.True, error);
            Assert.That(replacement.Frames, Is.EqualTo(new[] { 0L }));
            Assert.That(first.Resets, Is.EqualTo(1));
        }

        [Test]
        public void ReplaySinkLease_CheckedResetRefusalPreservesLeaseUntilRetry()
        {
            RecordedAnchorPoseSource source = NewSource("checked-release-source");
            var sink = new CheckedSink { RejectReset = true };
            Assert.That(source.TryAcquireReplaySink(sink, new[] { "root" }, out var lease, out string error),
                        Is.True, error);
            Assert.That(lease.TryRelease(out _), Is.False);
            Assert.That(lease.IsCurrent, Is.True, "a rejected reset cannot detach a live owner");
            sink.RejectReset = false;
            Assert.That(lease.TryRelease(out error), Is.True, error);
            Assert.That(lease.IsCurrent, Is.False);
            Assert.That(sink.ResetAttempts, Is.EqualTo(2));
            Assert.That(source.TrySubmit(Frame(0), out _), Is.False, "release must detach the receiver");
        }

        [Test]
        public void ReplaySinkLease_CallbackReleaseIsRejectedWithoutCorruptingAcceptedFrame()
        {
            RecordedAnchorPoseSource source = NewSource("reentrant-release-source");
            var sink = new Sink();
            Assert.That(source.TryAcquireReplaySink(sink, new[] { "root" }, out var lease, out string error),
                        Is.True, error);
            bool callbackReleased = true;
            sink.OnApply = () => callbackReleased = lease.TryRelease(out _);

            Assert.That(source.TrySubmit(Frame(0), out error), Is.True, error);
            Assert.That(callbackReleased, Is.False);
            Assert.That(lease.IsCurrent, Is.True);
            Assert.That(source.TrySubmit(Frame(1), out error), Is.True, error,
                        "the rejected callback release must preserve the source fence");
            Assert.That(lease.TryRelease(out error), Is.True, error);
        }

        [Test]
        public void ReplayConfigurationVersion_RejectsStaleReleaseAndRetainsReplacement()
        {
            AnchorPoseReplayPlayer player = NewObject("replay-player").AddComponent<AnchorPoseReplayPlayer>();
            RecordedAnchorPoseSource firstSource = NewSource("first-source");
            RecordedAnchorPoseSource secondSource = NewSource("second-source");
            AnchorPoseRecording first = LoadRecording(0);
            AnchorPoseRecording second = LoadRecording(10);
            try
            {
                Assert.That(player.Configure(firstSource, first, out string error), Is.True, error);
                long firstVersion = player.ConfigurationVersion;
                Assert.That(player.HasConfiguration, Is.True);
                Assert.That(player.Configure(secondSource, second, out error), Is.True, error);
                long secondVersion = player.ConfigurationVersion;
                Assert.That(secondVersion, Is.GreaterThan(firstVersion));

                Assert.That(player.TryReleaseConfiguration(firstVersion, out _), Is.False);
                Assert.That(player.HasConfiguration, Is.True);
                Assert.That(player.ConfigurationVersion, Is.EqualTo(secondVersion));
                Assert.That(player.TryReleaseConfiguration(secondVersion, out error), Is.True, error);
                Assert.That(player.HasConfiguration, Is.False);
                Assert.That(player.ConfigurationVersion, Is.GreaterThan(secondVersion));
            }
            finally
            {
                DisposeRecordingFiles();
            }
        }

        [Test]
        public void ReplayConfigurationVersion_RejectsCallbackReleaseWhileAdvancing()
        {
            AnchorPoseReplayPlayer player = NewObject("advancing-player").AddComponent<AnchorPoseReplayPlayer>();
            RecordedAnchorPoseSource source = NewSource("advancing-source");
            var sink = new Sink();
            Assert.That(source.TrySetSink(sink, out string error), Is.True, error);
            AnchorPoseRecording recording = LoadRecording(0);
            try
            {
                Assert.That(player.Configure(source, recording, out error), Is.True, error);
                long version = player.ConfigurationVersion;
                Assert.That(player.Begin(out error), Is.True, error);
                bool callbackReleased = true;
                sink.OnApply = () => callbackReleased = player.TryReleaseConfiguration(version, out _);
                Assert.That(player.TryAdvanceTo(0d, out error), Is.True, error);
                Assert.That(callbackReleased, Is.False);
                Assert.That(player.HasConfiguration, Is.True);
                Assert.That(player.ConfigurationVersion, Is.EqualTo(version));
            }
            finally
            {
                DisposeRecordingFiles();
            }
        }

        private readonly List<string> _recordingFiles = new List<string>();

        private GameObject NewObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        private RecordedAnchorPoseSource NewSource(string name) =>
            NewObject(name).AddComponent<RecordedAnchorPoseSource>();

        private AnchorPoseRecording LoadRecording(long sequence)
        {
            string data = Path.GetTempFileName();
            string manifest = Path.GetTempFileName();
            _recordingFiles.Add(data);
            _recordingFiles.Add(manifest);
            File.WriteAllText(data, FrameJson(sequence));
            byte[] bytes = File.ReadAllBytes(data);
            File.WriteAllText(manifest,
                "{\"provenance\":{\"orientation_mode\":\"s6-canonical-spatial-anchor-yaw-only: Unity yaw = pi/2 - S6 yaw; raw tracker quaternions retained as evidence, not relabeled as Unity anchor rotation\"},\"output\":{\"publication_state\":\"committed\",\"jsonl_path\":\"" +
                Path.GetFullPath(data).Replace("\\", "\\\\") + "\",\"jsonl_byte_count\":" + bytes.Length +
                ",\"jsonl_sha256\":\"" + Hash(bytes) + "\",\"anchor_pose_schema\":1,\"frame\":\"unity-world\"}}");
            var recording = new AnchorPoseRecording();
            Assert.That(recording.TryLoadCommitted(data, manifest, new[] { "root" }, out string error), Is.True, error);
            return recording;
        }

        private void DisposeRecordingFiles()
        {
            foreach (string path in _recordingFiles)
                if (File.Exists(path)) File.Delete(path);
            _recordingFiles.Clear();
        }

        private static AnchorPoseFrame Frame(long sequence) => new AnchorPoseFrame
        {
            sequence = sequence,
            timeSeconds = sequence,
            anchors = new[]
            {
                new AnchorPoseSample
                {
                    anchorId = "root", worldPosition = new Vector3(1f, 2f, 3f),
                    worldRotation = Quaternion.identity
                }
            }
        };

        private static string FrameJson(long sequence) =>
            "{\"schema\":1,\"frame\":\"unity-world\",\"sequence\":" + sequence +
            ",\"timeSeconds\":" + sequence +
            ",\"anchors\":[{\"anchorId\":\"root\",\"worldPosition\":{\"x\":1,\"y\":2,\"z\":3},\"worldRotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}]}";

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
#endif
