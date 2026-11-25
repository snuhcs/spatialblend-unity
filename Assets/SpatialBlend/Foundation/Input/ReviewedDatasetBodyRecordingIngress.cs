// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SpatialBlend
{
    /// <summary>
    /// Supplies one canonical, externally pinned D2/D3 review artifact. This
    /// deliberately supplies bytes rather than a caller-constructed receipt
    /// object: Foundation parses, validates and owns the immutable receipt.
    /// The source has no path, transport, body rows, Unity object or runtime
    /// authority.
    /// </summary>
    public interface IReviewedBodyDatasetReceiptSource
    {
        // This source supplies only canonical receipt bytes.  The SHA-256 pin
        // is a separate construction input owned by the review/configuration
        // boundary; one source object must not self-certify both values.
        bool TryRead(out byte[] canonicalReceiptBytes, out string error);
    }

    /// <summary>
    /// D4's narrow reviewed-dataset bridge. It validates a complete immutable
    /// review receipt against one existing replay definition, then itself is an
    /// IReceiverBodySource that delegates only to that injected replay source.
    /// It neither reads datasets nor creates profiles, clocks, frames, support
    /// geometry, transforms, output rigs, Fusion state or backend execution.
    /// </summary>
    public sealed class ReviewedDatasetBodyRecordingIngress : IReceiverBodySource, IReceiverBodyRecordingReplayApproval
    {
        public const string Contract = "spatialblend-reviewed-body-dataset-receipt-v1";
        public const int MaximumReceiptBytes = 1024 * 1024;
        private readonly ReceiverBodyRecordingReplaySource _replay;
        private readonly Receipt _receipt;
        private bool _binding;
        private ReceiverBodyProfile _boundProfile;

        private ReviewedDatasetBodyRecordingIngress(ReceiverBodyRecordingReplaySource replay, Receipt receipt)
        {
            _replay = replay;
            _receipt = receipt;
        }

        public ReceiverBodyRecordingReplaySource ReplaySource => _replay;
        public bool IsBound => _boundProfile != null && _replay.IsBound;
        public string ReceiptSha256 => _receipt.Sha256;
        public string ReceiptFingerprint => _receipt.Fingerprint;

        /// <summary>
        /// Reads exactly once from an injected receipt source. A source can
        /// supply only canonical bytes and an out-of-band byte pin; no public
        /// DTO/factory exists that can label arbitrary data as reviewed.
        /// </summary>
        public static bool TryCreate(IReviewedBodyDatasetReceiptSource receiptSource,
            string expectedReceiptSha256, ReceiverBodyRecordingReplaySource replaySource,
            out ReviewedDatasetBodyRecordingIngress ingress, out string error)
        {
            ingress = null;
            error = null;
            if (receiptSource == null || replaySource == null)
                return ReceiverInputValidation.Fail("An injected review-receipt source and existing replay source are required.", out error);
            byte[] supplied = null;
            try
            {
                if (!receiptSource.TryRead(out supplied, out error))
                    return ReceiverInputValidation.Fail(error ?? "The reviewed dataset receipt source declined to provide a receipt.", out error);
            }
            catch (Exception)
            {
                return ReceiverInputValidation.Fail("The reviewed dataset receipt source failed.", out error);
            }
            if (supplied == null || supplied.Length == 0 || supplied.Length > MaximumReceiptBytes || !HashText(expectedReceiptSha256))
                return ReceiverInputValidation.Fail("A bounded nonempty canonical receipt and external SHA-256 pin are required.", out error);
            byte[] frozen = (byte[])supplied.Clone();
            if (Hash(frozen) != expectedReceiptSha256)
                return ReceiverInputValidation.Fail("Reviewed dataset receipt differs from its external SHA-256 pin.", out error);
            if (!TryRead(frozen, expectedReceiptSha256, out Receipt receipt, out error) || !receipt.TryMatch(replaySource.Definition, out error)) return false;
            ingress = new ReviewedDatasetBodyRecordingIngress(replaySource, receipt);
            return true;
        }

        /// <summary>
        /// Binds the already admitted receiver profile through the existing
        /// replay source. The receipt is this source's non-replaceable approval;
        /// callers cannot supply a permissive approval DTO or substitute source.
        /// </summary>
        public bool TryBind(ReceiverBodyProfile profile, out string error)
        {
            error = null;
            if (_binding || _boundProfile != null || profile == null)
                return ReceiverInputValidation.Fail("One idle reviewed ingress and its exact unbound profile are required.", out error);
            _binding = true;
            try
            {
                if (!_receipt.TryMatch(_replay.Definition, out error)) return false;
                if (!_replay.TryBind(profile, this, out error)) return false;
                _boundProfile = profile;
                return true;
            }
            finally
            {
                _binding = false;
            }
        }

        public bool TryAcquire(ReceiverBodyProfile profile, long nativeAcquisitionFloor,
            out ReceiverBodySampleStatus status, out ReceiverBodyFrame frame, out string error)
        {
            status = ReceiverBodySampleStatus.Unavailable;
            frame = null;
            error = null;
            if (!ReferenceEquals(profile, _boundProfile))
                return ReceiverInputValidation.Fail("Reviewed dataset ingress requires its exact successfully bound profile.", out error);
            return _replay.TryAcquire(profile, nativeAcquisitionFloor, out status, out frame, out error);
        }

        bool IReceiverBodyRecordingReplayApproval.TryApprove(ReceiverBodyRecordingReplayApprovalRequest request,
            out string reviewReference, out string error)
        {
            reviewReference = null;
            error = null;
            if (!_binding || request == null || !ReferenceEquals(request.Definition, _replay.Definition) ||
                request.Profile == null || !_receipt.TryMatch(request.Definition, out error))
                return ReceiverInputValidation.Fail(error ?? "The review receipt is not bound to this exact replay definition.", out error);
            reviewReference = "d4_" + _receipt.Sha256;
            return true;
        }

        /// <summary>Stable calibration identity consumed by the reviewed receipt.</summary>
        public static string ComputeCalibrationSha256(ReceiverBodyProfileSpec body)
        {
            if (body == null) return null;
            return ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-reviewed-body-dataset-calibration-v1");
                writer.Write(body.RootAnchor); writer.Write(body.AnchorNames.Count);
                foreach (string anchor in body.AnchorNames) writer.Write(anchor);
                writer.Write(body.RigRevision); writer.Write(body.CalibrationRevision);
                writer.Write(body.ProviderId); writer.Write(body.ProviderEpoch);
                writer.Write((int)body.EvidenceKind); writer.Write(body.EvidenceReference);
                writer.Write(body.Skeleton.Fingerprint); ReceiverBodyHash.WriteMatrix(writer, body.ReceiverWorldFromBody);
                writer.Write(body.Anchors.Count);
                foreach (ReceiverBodyAnchorBinding anchor in body.Anchors)
                {
                    writer.Write(anchor.Anchor); writer.Write(anchor.JointName); writer.Write(anchor.JointIndex);
                    ReceiverBodyHash.WriteMatrix(writer, anchor.JointFromAnchor);
                }
            });
        }

        private sealed class Receipt
        {
            internal string Sha256, Fingerprint, DatasetSelection, Member, Profile, Calibration, Frame, Clock, Support;

            internal Receipt() { }

            internal bool TryMatch(ReceiverBodyRecordingReplayDefinition definition, out string error)
            {
                error = null;
                if (definition == null || definition.Recording == null || definition.Body == null ||
                    definition.Recording.DeclaredSourceEvidenceKind != "measured" ||
                    definition.Body.EvidenceKind != ReceiverBodyEvidenceKind.RecordedBody ||
                    definition.Body.ClockMapping == null || definition.Body.ClockMapping.Kind != ReceiverBodyClockMappingKind.ReplayTimeline ||
                    Member != definition.Recording.ProducerSourceSha256 || Frame != definition.Recording.ManifestSha256 ||
                    Profile != definition.Fingerprint || Calibration != ComputeCalibrationSha256(definition.Body) ||
                    Clock != definition.Body.ClockMapping.Fingerprint)
                    return ReceiverInputValidation.Fail("Reviewed dataset receipt does not exactly bind the measured recording, replay profile, calibration or clock.", out error);
                return true;
            }
        }

        private static bool TryRead(byte[] bytes, string sha256, out Receipt receipt, out string error)
        {
            receipt = null;
            error = null;
            if (!AnchorPoseJson.TryParse(ReceiverInputValidation.Utf8.GetString(bytes), out var root, out error)) return false;
            string[] fields = {
                "schema_version", "contract", "dataset_selection_sha256", "member_sha256", "profile_sha256",
                "calibration_sha256", "frame_sha256", "clock_sha256", "support_sha256", "source_evidence_label",
                "calibration_evidence_label", "clock_evidence_label", "support_evidence_label", "dataset_selection_reviewed",
                "member_reviewed", "profile_reviewed", "calibration_reviewed", "frame_reviewed", "clock_reviewed",
                "support_reviewed", "runtime_admission_reviewed"
            };
            if (root.Object == null || root.Object.Count != fields.Length || fields.Any(field => !root.Object.ContainsKey(field)))
                return ReceiverInputValidation.Fail("Reviewed dataset receipt fields must match the exact D4 schema.", out error);
            var values = root.Object;
            string[] hashes = { "dataset_selection_sha256", "member_sha256", "profile_sha256", "calibration_sha256",
                "frame_sha256", "clock_sha256", "support_sha256" };
            string[] gates = { "dataset_selection_reviewed", "member_reviewed", "profile_reviewed", "calibration_reviewed",
                "frame_reviewed", "clock_reviewed", "support_reviewed", "runtime_admission_reviewed" };
            if (!AnchorPoseJson.Int32(values["schema_version"], out int version) || version != 1 ||
                values["contract"].String != Contract || values["source_evidence_label"].String != "independently_reviewed_measured_source" ||
                values["calibration_evidence_label"].String != "independently_reviewed" ||
                values["clock_evidence_label"].String != "independently_reviewed" ||
                values["support_evidence_label"].String != "independently_reviewed" ||
                hashes.Any(field => !HashText(values[field].String)) || gates.Any(field => values[field].Boolean != true))
                return ReceiverInputValidation.Fail("Reviewed dataset receipt requires every source, calibration, clock, support and runtime-admission gate for independently reviewed measured data.", out error);
            var parsed = new Receipt
            {
                Sha256 = sha256,
                DatasetSelection = values["dataset_selection_sha256"].String,
                Member = values["member_sha256"].String,
                Profile = values["profile_sha256"].String,
                Calibration = values["calibration_sha256"].String,
                Frame = values["frame_sha256"].String,
                Clock = values["clock_sha256"].String,
                Support = values["support_sha256"].String
            };
            parsed.Fingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-reviewed-body-dataset-receipt-fingerprint-v1");
                writer.Write(parsed.Sha256); writer.Write(parsed.DatasetSelection); writer.Write(parsed.Member);
                writer.Write(parsed.Profile); writer.Write(parsed.Calibration); writer.Write(parsed.Frame);
                writer.Write(parsed.Clock); writer.Write(parsed.Support);
            });
            receipt = parsed;
            return true;
        }

        private static bool HashText(string value) => value != null && value.Length == 64 &&
            value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
