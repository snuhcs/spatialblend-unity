// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    [Category("FoundationReceiverInput")]
    public sealed class ReviewedDatasetBodyRecordingIngressTests
    {
        private sealed class ReceiptSource : IReviewedBodyDatasetReceiptSource
        {
            internal readonly byte[] Bytes;
            internal readonly string Pin;
            internal bool Deny, Throw;

            internal ReceiptSource(byte[] bytes)
            {
                Bytes = bytes;
                Pin = Hash(bytes);
            }

            public bool TryRead(out byte[] canonicalReceiptBytes, out string error)
            {
                if (Throw) throw new InvalidOperationException("receipt source failure");
                canonicalReceiptBytes = Deny ? null : Bytes;
                error = Deny ? "fixture denial" : null;
                return !Deny;
            }
        }

        [Test]
        public void FullyReviewedMeasuredReceiptBindsOnlyItsInjectedReplaySource()
        {
            var definition = MeasuredDefinition();
            var replay = new ReceiverBodyRecordingReplaySource(definition);
            byte[] bytes = Receipt(definition);
            var source = new ReceiptSource(bytes);

            Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(source, source.Pin, replay, out var ingress, out string error), Is.True, error);
            Assert.That(ingress.ReplaySource, Is.SameAs(replay));
            Assert.That(ingress.ReceiptSha256, Is.EqualTo(source.Pin));
            Assert.That(ingress.ReceiptFingerprint, Is.Not.Empty);
            Assert.That(definition.Recording.CalibrationStatus, Is.EqualTo("unreviewed_authored_mapping"),
                "The D4 review does not rewrite the immutable staging receipt.");
            Assert.That(definition.Recording.RuntimeAdmissionSupported, Is.False);

            var profile = BodyRecordingReplayFixture.Profile(definition);
            Assert.That(ingress.TryBind(profile, out error), Is.True, error);
            Assert.That(ingress.IsBound, Is.True);
            Assert.That(ingress.TryAcquire(profile, BodyRecordingReplayFixture.Origin + 100000000, out var status, out var frame, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverBodySampleStatus.Sample));
            Assert.That(frame.SourceSequence, Is.EqualTo(0));
            Assert.That(ingress.TryAcquire(BodyRecordingReplayFixture.Profile(definition), BodyRecordingReplayFixture.Origin + 200000000,
                out _, out _, out _), Is.False, "A matching-value profile cannot replace the one bound by this ingress.");
        }

        [Test]
        public void SyntheticUnreviewedOrIncompleteMeasuredReceiptCannotBecomeReviewedIngress()
        {
            var measured = MeasuredDefinition();
            foreach (byte[] receipt in new[]
            {
                Receipt(measured, "\"calibration_reviewed\":true", "\"calibration_reviewed\":false"),
                Receipt(measured, "independently_reviewed_measured_source", "synthetic"),
                Receipt(measured, "\"support_evidence_label\":\"independently_reviewed\"", "\"support_evidence_label\":\"unreviewed\""),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Receipt(measured)).Replace("}", ",\"unexpected\":true}")),
            })
            {
                var replay = new ReceiverBodyRecordingReplaySource(measured);
                var source = new ReceiptSource(receipt);
                Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(source, source.Pin, replay,
                    out var ingress, out _), Is.False);
                Assert.That(ingress, Is.Null);
                Assert.That(replay.IsBound, Is.False);
            }

            var synthetic = BodyRecordingReplayFixture.Definition(BodyRecordingReplayFixture.Recording(lineage: "synthetic"));
            var syntheticSource = new ReceiptSource(Receipt(synthetic));
            Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(syntheticSource, syntheticSource.Pin,
                new ReceiverBodyRecordingReplaySource(synthetic), out _, out _), Is.False,
                "D4 cannot relabel a synthetic recording merely because all receipt text gates say true.");
        }

        [Test]
        public void ReceiptPinsEveryReplayCalibrationAndClockIdentityAndHasNoPublicReceiptDto()
        {
            var definition = MeasuredDefinition();
            string[] substitutions =
            {
                "\"member_sha256\":\"" + definition.Recording.ProducerSourceSha256 + "\"",
                "\"profile_sha256\":\"" + definition.Fingerprint + "\"",
                "\"calibration_sha256\":\"" + ReviewedDatasetBodyRecordingIngress.ComputeCalibrationSha256(definition.Body) + "\"",
                "\"frame_sha256\":\"" + definition.Recording.ManifestSha256 + "\"",
                "\"clock_sha256\":\"" + definition.Body.ClockMapping.Fingerprint + "\""
            };
            foreach (string exact in substitutions)
            {
                string changed = exact.Substring(0, exact.Length - 2) + (exact[exact.Length - 2] == 'a' ? "b\"" : "a\"");
                byte[] receipt = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Receipt(definition)).Replace(exact, changed));
                var source = new ReceiptSource(receipt);
                Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(source, source.Pin,
                    new ReceiverBodyRecordingReplaySource(definition), out _, out _), Is.False, exact);
            }
            var denied = new ReceiptSource(Receipt(definition)) { Deny = true };
            Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(denied, denied.Pin,
                new ReceiverBodyRecordingReplaySource(definition), out _, out _), Is.False);
            var throwing = new ReceiptSource(Receipt(definition)) { Throw = true };
            Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(throwing, throwing.Pin,
                new ReceiverBodyRecordingReplaySource(definition), out _, out _), Is.False);
            var independentlyPinned = new ReceiptSource(Receipt(definition));
            Assert.That(ReviewedDatasetBodyRecordingIngress.TryCreate(independentlyPinned, new string('0', 64),
                new ReceiverBodyRecordingReplaySource(definition), out _, out _), Is.False,
                "A separately supplied review pin cannot be self-derived by the receipt source.");

            Type privateReceipt = typeof(ReviewedDatasetBodyRecordingIngress).GetNestedType("Receipt", BindingFlags.NonPublic);
            Assert.That(privateReceipt, Is.Not.Null);
            Assert.That(privateReceipt.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                "No caller-constructible review receipt DTO is exposed by the ingress.");
        }

        private static ReceiverBodyRecordingReplayDefinition MeasuredDefinition()
        {
            return BodyRecordingReplayFixture.Definition(BodyRecordingReplayFixture.Recording(lineage: "measured"));
        }

        private static byte[] Receipt(ReceiverBodyRecordingReplayDefinition definition, string from = null, string to = null)
        {
            string json = "{\"schema_version\":1,\"contract\":\"" + ReviewedDatasetBodyRecordingIngress.Contract + "\"," +
                "\"dataset_selection_sha256\":\"" + new string('a', 64) + "\"," +
                "\"member_sha256\":\"" + definition.Recording.ProducerSourceSha256 + "\"," +
                "\"profile_sha256\":\"" + definition.Fingerprint + "\"," +
                "\"calibration_sha256\":\"" + ReviewedDatasetBodyRecordingIngress.ComputeCalibrationSha256(definition.Body) + "\"," +
                "\"frame_sha256\":\"" + definition.Recording.ManifestSha256 + "\"," +
                "\"clock_sha256\":\"" + definition.Body.ClockMapping.Fingerprint + "\"," +
                "\"support_sha256\":\"" + new string('b', 64) + "\"," +
                "\"source_evidence_label\":\"independently_reviewed_measured_source\"," +
                "\"calibration_evidence_label\":\"independently_reviewed\"," +
                "\"clock_evidence_label\":\"independently_reviewed\"," +
                "\"support_evidence_label\":\"independently_reviewed\"," +
                "\"dataset_selection_reviewed\":true,\"member_reviewed\":true,\"profile_reviewed\":true," +
                "\"calibration_reviewed\":true,\"frame_reviewed\":true,\"clock_reviewed\":true," +
                "\"support_reviewed\":true,\"runtime_admission_reviewed\":true}";
            if (from != null) json = json.Replace(from, to);
            return Encoding.UTF8.GetBytes(json);
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
#endif
