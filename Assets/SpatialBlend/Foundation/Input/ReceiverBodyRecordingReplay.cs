// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Caller-owned trust policy for an exact recording/calibration/profile
    /// binding. No default allow policy is supplied. Production implementations
    /// must independently verify source, units/basis, rig and anchor calibration
    /// and the bounded replay schedule; strings/hashes alone are not review.
    /// This authorizes replay, not measured-body or physical-contact claims.
    /// </summary>
    public interface IReceiverBodyRecordingReplayApproval
    {
        bool TryApprove(ReceiverBodyRecordingReplayApprovalRequest request,
            out string reviewReference, out string error);
    }

    /// <summary>Immutable request; approval cannot substitute a profile or source.</summary>
    public sealed class ReceiverBodyRecordingReplayApprovalRequest
    {
        internal ReceiverBodyRecordingReplayApprovalRequest(ReceiverBodyRecordingReplayDefinition definition,
            ReceiverBodyProfile profile)
        {
            Definition = definition; Profile = profile;
            Fingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-body-recording-replay-approval-v1");
                writer.Write(definition.Fingerprint); writer.Write(profile.Fingerprint);
                // Profile's content hash intentionally is not the native owner
                // identity. Bind the request to that owner's full numeric epoch
                // as well; the exact Profile/Input objects remain the authority.
                writer.Write(profile.Input.NativeSpace); writer.Write(profile.Input.NativeActor);
                writer.Write(profile.Input.Epoch); writer.Write(profile.Input.ClockId); writer.Write(profile.Input.ClockEpoch);
                writer.Write(profile.Input.MaximumAgeNanoseconds);
            });
        }
        public ReceiverBodyRecordingReplayDefinition Definition { get; }
        public ReceiverBodyProfile Profile { get; }
        public string Fingerprint { get; }
    }

    /// <summary>
    /// Pure recording-bound replay preflight. Does not authorize calibration,
    /// construct an input epoch or infer a skeleton/profile from recording labels.
    /// All coordinates must already agree with the independently supplied body
    /// definition; no conversion, retargeting, resampling or root inference here.
    /// </summary>
    public sealed class ReceiverBodyRecordingReplayDefinition
    {
        private ReceiverBodyRecordingReplayDefinition(ReceiverBodyRecording recording,
            ReceiverBodyProfileSpec body, string basis, string unit, ulong first, long ttl, long uncertainty)
        {
            Recording = recording; Body = body; ExpectedTargetBasisLabel = basis; ExpectedTargetLengthUnitLabel = unit;
            FirstNativeInputSequence = first; TimeToLiveAfterAcquisitionUpperNanoseconds = ttl;
            AdditionalSourceUncertaintyNanoseconds = uncertainty;
            Fingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-body-recording-replay-definition-v1");
                writer.Write(recording.ManifestSha256); writer.Write(recording.SourceReceiptSha256);
                writer.Write(recording.ProducerSourceSha256); writer.Write(recording.DeclaredSourceEvidenceKind);
                writer.Write(recording.CalibrationStatus); writer.Write(recording.RuntimeAdmissionSupported);
                writer.Write(basis); writer.Write(unit);
                writer.Write(body.RootAnchor); writer.Write(body.AnchorNames.Count);
                foreach (string anchor in body.AnchorNames) writer.Write(anchor);
                writer.Write(body.RigRevision); writer.Write(body.CalibrationRevision);
                writer.Write(body.ProviderId); writer.Write(body.ProviderEpoch);
                writer.Write((int)body.EvidenceKind); writer.Write(body.EvidenceReference);
                writer.Write(body.ClockMapping.Fingerprint); writer.Write(body.Skeleton.Fingerprint);
                ReceiverBodyHash.WriteMatrix(writer, body.ReceiverWorldFromBody);
                writer.Write(body.Anchors.Count);
                foreach (var anchor in body.Anchors)
                {
                    writer.Write(anchor.Anchor); writer.Write(anchor.JointName); writer.Write(anchor.JointIndex);
                    ReceiverBodyHash.WriteMatrix(writer, anchor.JointFromAnchor);
                }
                writer.Write(first); writer.Write(ttl); writer.Write(uncertainty);
            });
        }
        public ReceiverBodyRecording Recording { get; }
        public ReceiverBodyProfileSpec Body { get; }
        public string ExpectedTargetBasisLabel { get; }
        public string ExpectedTargetLengthUnitLabel { get; }
        public ulong FirstNativeInputSequence { get; }
        public long TimeToLiveAfterAcquisitionUpperNanoseconds { get; }
        public long AdditionalSourceUncertaintyNanoseconds { get; }
        public string Fingerprint { get; }

        public static bool TryCreate(ReceiverBodyRecording recording, string expectedManifestSha256,
            string expectedTargetBasisLabel, string expectedTargetLengthUnitLabel, ReceiverBodyProfileSpec body,
            ulong firstNativeInputSequence, long timeToLiveAfterAcquisitionUpperNanoseconds,
            long additionalSourceUncertaintyNanoseconds,
            out ReceiverBodyRecordingReplayDefinition definition, out string error)
        {
            definition = null; error = null;
            if (recording == null || body == null || expectedManifestSha256 != recording.ManifestSha256 ||
                expectedTargetBasisLabel != recording.TargetBasisLabel || expectedTargetLengthUnitLabel != recording.TargetLengthUnitLabel)
                return ReceiverInputValidation.Fail("Exact externally selected recording, coordinate labels and independent body definition are required.", out error);
            // Recorded acquisition is orthogonal to declared source lineage. Do
            // not relabel a derived/recorded clip SyntheticFixture to admit it.
            if (body.EvidenceKind != ReceiverBodyEvidenceKind.RecordedBody ||
                body.ClockMapping.Kind != ReceiverBodyClockMappingKind.ReplayTimeline ||
                recording.DeclaredSourceEvidenceKind == "unknown" ||
                !recording.TryValidateReplayClock(body.ClockMapping, additionalSourceUncertaintyNanoseconds, out error))
                return ReceiverInputValidation.Fail(error ?? "Replay requires RecordedBody/ReplayTimeline and explicit known source lineage.", out error);
            if (!SameReferences(recording, body.Skeleton))
                return ReceiverInputValidation.Fail("Independent skeleton must match every recorded reference name, parent and float matrix bit in order.", out error);
            if (firstNativeInputSequence == 0 || firstNativeInputSequence > ulong.MaxValue - (ulong)(recording.FrameCount-1) ||
                timeToLiveAfterAcquisitionUpperNanoseconds <= 0 ||
                timeToLiveAfterAcquisitionUpperNanoseconds > ReceiverInputValidation.MaximumAgeNanoseconds)
                return ReceiverInputValidation.Fail("A positive bounded replay TTL and non-overflowing native input sequence range are required.", out error);
            for (int i = 0; i < recording.FrameCount; ++i)
            {
                if (!recording.TryGetSample(i, out var sample) ||
                    !TryTimes(body.ClockMapping, sample.SourceTimeSeconds, timeToLiveAfterAcquisitionUpperNanoseconds,
                        ReceiverInputValidation.MaximumAgeNanoseconds, out _, out _, out error))
                    return ReceiverInputValidation.Fail(error ?? "Every original source sample must have a valid replay window.", out error);
            }
            definition = new ReceiverBodyRecordingReplayDefinition(recording, body, expectedTargetBasisLabel,
                expectedTargetLengthUnitLabel, firstNativeInputSequence, timeToLiveAfterAcquisitionUpperNanoseconds,
                additionalSourceUncertaintyNanoseconds);
            return true;
        }

        internal static bool TryTimes(ReceiverBodyClockMapping clock, double sourceTime, long ttl, long maximumAge,
            out long observed, out long expiry, out string error)
        {
            observed = expiry = 0;
            if (!clock.TryMap(sourceTime, out long lower, out long upper, out error)) return false;
            try
            {
                long end = checked(upper + ttl);
                if (end > clock.ValidUntilNativeNanoseconds || checked(end-lower) > maximumAge)
                    return ReceiverInputValidation.Fail("Full acquisition uncertainty plus TTL must fit the original clock window and admitted maximum age.", out error);
                observed = lower; expiry = end; return true;
            }
            catch (OverflowException)
            { return ReceiverInputValidation.Fail("Replay acquisition/expiry overflows native nanoseconds.", out error); }
        }

        private static bool SameReferences(ReceiverBodyRecording recording, ReceiverBodySkeleton skeleton)
        {
            if (recording.ReferenceJoints.Count != skeleton.Joints.Count) return false;
            for (int i = 0; i < recording.ReferenceJoints.Count; ++i)
            {
                var a = recording.ReferenceJoints[i]; var b = skeleton.Joints[i];
                if (a.Name != b.Name || a.ParentIndex != b.ParentIndex) return false;
                for (int row = 0; row < 4; ++row)
                    for (int column = 0; column < 4; ++column)
                        if (BitConverter.ToInt32(BitConverter.GetBytes(a.ParentFromReferenceJoint[row, column]), 0) !=
                            BitConverter.ToInt32(BitConverter.GetBytes(b.ParentFromReferenceJoint[row, column]), 0)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// App-independent, one-profile replay source. A host configures this unbound
    /// source, establishes its receiver body profile, then explicitly calls
    /// TryBind(profile, approval) before any acquisition. A failed bind leaves it
    /// unbound; the host releases what it set up. No implicit approval/lazy bind.
    /// </summary>
    public sealed class ReceiverBodyRecordingReplaySource : IReceiverBodySource
    {
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private BufferedReceiverBodySource _buffer;
        private bool _busy;
        public ReceiverBodyRecordingReplaySource(ReceiverBodyRecordingReplayDefinition definition)
        { Definition = definition ?? throw new ArgumentNullException(nameof(definition)); }
        public ReceiverBodyRecordingReplayDefinition Definition { get; }
        public ReceiverBodyProfile Profile => _buffer?.Profile;
        public bool IsBound => _buffer != null;
        public bool IsFaulted => _buffer != null && _buffer.IsFaulted;
        public int DeliveredFrameCount => _buffer?.DeliveredFrameCount ?? 0;
        public string ApprovalRequestFingerprint { get; private set; }
        public string ReviewReference { get; private set; }

        public bool TryBind(ReceiverBodyProfile profile, IReceiverBodyRecordingReplayApproval approval, out string error)
        {
            error = null;
            if (!CheckThread(out error)) return false;
            if (_busy || _buffer != null || profile == null || approval == null)
                return ReceiverInputValidation.Fail("One idle unbound source, an exact materialized profile and an external approval policy are required.", out error);
            _busy = true;
            try
            {
                if (!Definition.Body.TryMaterialize(profile.Input, out var expected, out error) ||
                    !ReferenceEquals(expected.Input, profile.Input) || expected.Fingerprint != profile.Fingerprint)
                    return ReceiverInputValidation.Fail(error ?? "Actual profile differs from the recording-bound independent definition.", out error);
                // Check epoch-specific age before invoking caller trust policy.
                var recording = Definition.Recording;
                for (int i = 0; i < recording.FrameCount; ++i)
                {
                    recording.TryGetSample(i, out var sample);
                    if (!ReceiverBodyRecordingReplayDefinition.TryTimes(profile.ClockMapping, sample.SourceTimeSeconds,
                        Definition.TimeToLiveAfterAcquisitionUpperNanoseconds, profile.Input.MaximumAgeNanoseconds,
                        out _, out _, out error)) return false;
                }
                var request = new ReceiverBodyRecordingReplayApprovalRequest(Definition, profile);
                string review;
                try
                {
                    if (!approval.TryApprove(request, out review, out error))
                        return ReceiverInputValidation.Fail(error ?? "External recording replay approval was denied.", out error);
                }
                catch (Exception)
                { return ReceiverInputValidation.Fail("External recording replay approval failed.", out error); }
                if (!ReceiverInputValidation.ValidName(review))
                    return ReceiverInputValidation.Fail("External approval must identify its independent review record.", out error);
                var frames = new ReceiverBodyFrame[recording.FrameCount];
                for (int i = 0; i < frames.Length; ++i)
                {
                    recording.TryGetSample(i, out var sample);
                    if (!ReceiverBodyRecordingReplayDefinition.TryTimes(profile.ClockMapping, sample.SourceTimeSeconds,
                        Definition.TimeToLiveAfterAcquisitionUpperNanoseconds, profile.Input.MaximumAgeNanoseconds,
                        out long observed, out long expiry, out error) ||
                        !ReceiverBodyFrame.TryCreate(profile, checked(Definition.FirstNativeInputSequence + (ulong)i),
                            sample.SourceSequence, sample.SourceTimeSeconds, observed, expiry,
                            sample.LocomotionRootAvailable, sample.BodyFromLocomotionRoot, sample.Joints, out frames[i], out error)) return false;
                }
                if (!BufferedReceiverBodySource.TryCreate(profile, frames, out var buffer, out error)) return false;
                // Publish only once, after all frame/FK/buffer validation succeeds.
                ApprovalRequestFingerprint = request.Fingerprint; ReviewReference = review;
                _buffer = buffer; return true;
            }
            finally { _busy = false; }
        }

        public bool TryAcquire(ReceiverBodyProfile profile, long nativeAcquisitionFloor,
            out ReceiverBodySampleStatus status, out ReceiverBodyFrame frame, out string error)
        {
            status = ReceiverBodySampleStatus.Unavailable; frame = null;
            if (!CheckThread(out error)) return false;
            if (_busy || _buffer == null)
                return ReceiverInputValidation.Fail("Recording source must be idle and explicitly approved/bound before acquisition.", out error);
            return _buffer.TryAcquire(profile, nativeAcquisitionFloor, out status, out frame, out error);
        }
        private bool CheckThread(out string error)
        {
            error = null;
            return Thread.CurrentThread.ManagedThreadId == _ownerThread ||
                ReceiverInputValidation.Fail("Recording replay binding/acquisition requires its construction owner thread.", out error);
        }
    }
}
