// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Opt-in dataset-independent consumer proof. No dataset assets, BVH parser,
    /// FC app, runtime body profile, native epochs, or rig are constructed here.
    /// </summary>
    public sealed class FoundationBodyRecordingSourceTests
    {
        private const string Contract = "spatialblend-body-reader-cases-v1";
        private const string OracleContract = "spatialblend-body-reader-f32-v1";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        [Serializable]
        private sealed class CaseResult
        {
            public string case_id, manifest_sha256, reader_sha256, declared_source_evidence_kind, error;
            public bool passed;
            public bool missing_calibration_replay_rejected;
            public int checked_frame_count, checked_joint_row_count;
            public long time_quantization_ns;
        }

        [Serializable]
        private sealed class Result
        {
            public int schema_version = 1;
            public string contract = "spatialblend-body-reader-result-v1";
            public string selection_sha256, oracle_source_sha256;
            public string calibration_status = "unreviewed_authored_mapping";
            public bool runtime_admission_supported = false;
            public int selected, passed, failed, checked_frame_count, checked_joint_row_count;
            public CaseResult[] results;
        }

        [Test]
        public void FullBodyRecordingsMatchIndependentSourceOracleForEverySelectedFrame()
        {
            string path = Environment.GetEnvironmentVariable("SPATIALBLEND_BODY_RECORDING_CASES");
            string pin = Environment.GetEnvironmentVariable("SPATIALBLEND_BODY_RECORDING_SELECTION_SHA256");
            // Only tools/run-foundation-tests.sh --body-recordings selects this fixture, and it sets both variables. Outside
            // that launcher (a whole-project EditMode run) there is no selection to verify. The launcher's verifier
            // (verify-foundation-results.py) fails a skipped case, so this is never a silent pass where it is selected.
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(pin))
                Assert.Ignore("Needs SPATIALBLEND_BODY_RECORDING_CASES and its pin, set by tools/run-foundation-tests.sh --body-recordings.");
            Assert.That(!string.IsNullOrEmpty(path) && Path.IsPathRooted(path), Is.True, "Explicit absolute selection required; no implicit skip.");
            byte[] selection = ReadBounded(path, 1024 * 1024);
            Assert.That(Hash(selection), Is.EqualTo(pin), "Selection differs from its launcher pin.");
            Assert.That(AnchorPoseJson.TryParse(Utf8.GetString(selection), out var root, out string error), Is.True, error);
            Assert.That(Text(root, "contract"), Is.EqualTo(Contract));
            Assert.That(Number(root, "schema_version"), Is.EqualTo(1));
            Assert.That(Text(root, "oracle_contract"), Is.EqualTo(OracleContract));
            Assert.That(Text(root, "selection"), Is.EqualTo("explicit_members_only"));
            Assert.That(Text(root, "calibration_status"), Is.EqualTo("unreviewed_authored_mapping"));
            Assert.That(Field(root, "runtime_admission_supported").Boolean, Is.EqualTo(false));
            var cases = Field(root, "results").Array;
            var members = Field(root, "selected_members").Array;
            Assert.That(cases, Is.Not.Null);
            Assert.That(cases.Count, Is.InRange(1, 32));
            Assert.That(members, Is.Not.Null);
            Assert.That(members.Count, Is.EqualTo(cases.Count));
            Assert.That(members.All(member => !string.IsNullOrEmpty(member.String)) &&
                members.Select(member => member.String).Distinct(StringComparer.Ordinal).Count() == members.Count, Is.True);
            var counts = Field(root, "member_counts");
            Assert.That(Number(counts, "selected"), Is.EqualTo(cases.Count));
            int prepared = cases.Count(item => Field(item, "passed").Boolean == true);
            Assert.That(Number(counts, "passed"), Is.EqualTo(prepared));
            Assert.That(Number(counts, "failed"), Is.EqualTo(cases.Count - prepared));
            Assert.That(Field(root, "preparation_passed").Boolean, Is.EqualTo(prepared == cases.Count));
            var report = new Result { selection_sha256 = pin, oracle_source_sha256 = Text(root, "oracle_source_sha256"),
                selected = cases.Count, results = new CaseResult[cases.Count] };
            string directory = Path.GetDirectoryName(path);
            for (int i = 0; i < cases.Count; ++i)
            {
                var row = new CaseResult { case_id = "recording-" + i.ToString("D3") };
                report.results[i] = row;
                try
                {
                    var item = cases[i];
                    Assert.That(Text(item, "case_id"), Is.EqualTo(row.case_id));
                    Assert.That(Text(item, "member_path"), Is.EqualTo(members[i].String));
                    Assert.That(Field(item, "passed").Boolean, Is.EqualTo(true), "Selected preparation failed.");
                    row.manifest_sha256 = Text(item, "manifest_sha256");
                    // Never take filesystem paths from selection rows.
                    string bodyDirectory = Path.Combine(directory, row.case_id);
                    Assert.That(ReceiverBodyRecording.TryRead(ReadBounded(Path.Combine(bodyDirectory, "recording.json"), 1024*1024),
                        ReadBounded(Path.Combine(bodyDirectory, "source-receipt.json"), 1024*1024),
                        ReadBounded(Path.Combine(bodyDirectory, "body.bin"), ReceiverBodyRecording.MaximumPayloadBytes),
                        row.manifest_sha256, out var recording, out error), Is.True, error);
                    Assert.That(recording.RuntimeAdmissionSupported, Is.False);
                    Assert.That(recording.CalibrationStatus, Is.EqualTo("unreviewed_authored_mapping"));
                    // Actual data has no independently approved runtime body
                    // calibration here. Do not derive one from recording rows.
                    row.missing_calibration_replay_rejected = !ReceiverBodyRecordingReplayDefinition.TryCreate(recording,
                        row.manifest_sha256, recording.TargetBasisLabel, recording.TargetLengthUnitLabel,
                        null, 1, 100000000, 0, out var unapproved, out _);
                    Assert.That(row.missing_calibration_replay_rejected, Is.True);
                    Assert.That(unapproved, Is.Null);
                    row.declared_source_evidence_kind = recording.DeclaredSourceEvidenceKind;
                    Assert.That(row.declared_source_evidence_kind, Is.EqualTo(Text(item, "expected_source_evidence_kind")));
                    Assert.That(recording.FrameCount, Is.EqualTo(Number(item, "expected_frame_count")));
                    row.reader_sha256 = ReaderHash(recording);
                    Assert.That(row.reader_sha256, Is.EqualTo(Text(item, "expected_reader_sha256")), "Full public reader values differ from independent source oracle.");
                    Assert.That(recording.TryGetTimeQuantizationNanoseconds(1e9, out long allowance, out error), Is.True, error);
                    row.time_quantization_ns = allowance;
                    Assert.That(allowance, Is.EqualTo(Number(item, "expected_time_quantization_ns")));
                    // Pure bounded numerical compatibility, not a live native
                    // epoch or a physical clock-calibration claim.
                    Assert.That(ReceiverBodyClockMapping.TryCreate(recording.SourceClock, recording.SourceClockEpoch, 1,
                        ReceiverBodyClockMappingKind.ReplayTimeline, "body_reader_numeric_check", 1, 1, 0, 1000000000L,
                        1e9, allowance, 1, 1000000000000000L, out var clock, out error), Is.True, error);
                    Assert.That(recording.TryValidateReplayClock(clock, 0, out error), Is.True, error);
                    row.checked_frame_count = recording.FrameCount;
                    row.checked_joint_row_count = checked(recording.FrameCount * recording.ReferenceJoints.Count);
                    row.passed = true;
                    ++report.passed;
                    report.checked_frame_count += row.checked_frame_count;
                    report.checked_joint_row_count += row.checked_joint_row_count;
                }
                catch (Exception failure)
                {
                    // Keep all selected failures and continue the denominator.
                    // Never include private transforms or assertion values.
                    row.error = failure.GetType().Name;
                    ++report.failed;
                }
            }
            string resultPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "body-recording-reader-result.json");
            using (var stream = new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8)) writer.Write(JsonUtility.ToJson(report, true));
            Assert.That(report.failed, Is.Zero, "Selected body-reader cases failed; inspect metadata-only result receipt.");
            Assert.That(report.passed, Is.EqualTo(report.selected));
        }

        private static string ReaderHash(ReceiverBodyRecording recording)
        {
            using (var hash = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            {
                using (var writer = new BinaryWriter(stream, Utf8, true))
                {
                    writer.Write(Encoding.ASCII.GetBytes(OracleContract + "\0"));
                    writer.Write(recording.ReferenceJoints.Count); writer.Write(recording.FrameCount); writer.Write(recording.EndSites.Count);
                    writer.Write(recording.FrameIntervalNumerator); writer.Write(recording.FrameIntervalDenominator);
                    foreach (var joint in recording.ReferenceJoints)
                    {
                        WriteName(writer, joint.Name); writer.Write(joint.ParentIndex); WriteMatrix(writer, joint.ParentFromReferenceJoint);
                    }
                    foreach (var end in recording.EndSites)
                    {
                        writer.Write(end.ParentIndex);
                        for (int k = 0; k < 3; ++k) WriteFloat(writer, end.Offset[k]);
                    }
                    for (int i = 0; i < recording.FrameCount; ++i)
                    {
                        Assert.That(recording.TryGetSample(i, out var sample), Is.True);
                        writer.Write(sample.SourceSequence); writer.Write(sample.SourceTimeSeconds);
                        writer.Write((byte)(sample.LocomotionRootAvailable ? 1 : 0)); WriteMatrix(writer, sample.BodyFromLocomotionRoot);
                        foreach (var joint in sample.Joints)
                        {
                            writer.Write(joint.Index); WriteName(writer, joint.Name);
                            writer.Write((byte)(joint.Available ? 1 : 0)); WriteMatrix(writer, joint.ParentFromJoint);
                        }
                    }
                }
                stream.FlushFinalBlock();
                return Hex(hash.Hash);
            }
        }
        private static void WriteName(BinaryWriter writer, string value)
        { byte[] bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        private static void WriteFloat(BinaryWriter writer, float value) => writer.Write(value == 0 ? 0f : value);
        private static void WriteMatrix(BinaryWriter writer, Matrix4x4 pose)
        { for (int r = 0; r < 4; ++r) for (int c = 0; c < 4; ++c) WriteFloat(writer, pose[r, c]); }
        private static byte[] ReadBounded(string path, int maximum)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(stream.Length, Is.InRange(1L, (long)maximum));
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length-offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                Assert.That(stream.ReadByte(), Is.EqualTo(-1));
                return bytes;
            }
        }
        private static AnchorPoseJson.JsonValue Field(AnchorPoseJson.JsonValue parent, string key)
        {
            Assert.That(AnchorPoseJson.Required(parent, key, out var value), Is.True, "Required selection field: " + key);
            return value;
        }
        private static string Text(AnchorPoseJson.JsonValue parent, string key)
        { string value = Field(parent, key).String; Assert.That(value, Is.Not.Null); return value; }
        private static long Number(AnchorPoseJson.JsonValue parent, string key)
        { Assert.That(AnchorPoseJson.Int64(Field(parent, key), out long value), Is.True); return value; }
        private static string Hash(byte[] bytes)
        { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
