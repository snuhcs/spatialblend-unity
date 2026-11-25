// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>Result of staging one explicitly identified C1 source frame.</summary>
    public enum ObservedCollaboratorStageResult { Rejected, Pending, Ready }

    /// <summary>
    /// Immutable owner-thread batch acquired from an admission barrier. It is
    /// source input only: no receiver pose, output, Transform, network state,
    /// or authority is present. The owner must acknowledge exactly one native
    /// transaction or abort; retaining a batch cannot advance a participant.
    /// </summary>
    public sealed class ObservedCollaboratorStagedBatch
    {
        internal ObservedCollaboratorStagedBatch(ulong batchId,
            IReadOnlyDictionary<string, AnchorPoseFrame> frames)
        {
            BatchId = batchId;
            _frames = new Dictionary<string, AnchorPoseFrame>(StringComparer.Ordinal);
            foreach (var item in frames)
                _frames.Add(item.Key, AnchorPoseFrameValidation.DeepCopy(item.Value));
        }
        private readonly Dictionary<string, AnchorPoseFrame> _frames;
        public ulong BatchId { get; }

        // The source frames are intentionally not exposed as public mutable
        // Unity DTOs. The framework coordinator receives fresh detached copies
        // only while materialising its one native C1 request.
        internal IReadOnlyDictionary<string, AnchorPoseFrame> CopyFramesForNative()
        {
            var copied = new Dictionary<string, AnchorPoseFrame>(StringComparer.Ordinal);
            foreach (var item in _frames)
                copied.Add(item.Key, AnchorPoseFrameValidation.DeepCopy(item.Value));
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, AnchorPoseFrame>(copied);
        }
    }

    /// <summary>
    /// Small C1 pre-solve transaction barrier. Participants are explicit stable
    /// source IDs and each carries its authored anchor set. A caller cannot
    /// assemble a batch from post-admission callbacks or silently use a newer
    /// frame for one peer: every member must stage exactly one validated frame
    /// under the same monotonically increasing batch ID before it becomes ready.
    /// </summary>
    public sealed class ObservedCollaboratorAdmissionBarrier : IDisposable
    {
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<string, HashSet<string>> _anchors;
        private readonly Dictionary<string, AnchorPoseFrame> _pending =
            new Dictionary<string, AnchorPoseFrame>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _lastSequences =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _lastTimes =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private ulong _lastAcknowledged;
        private ulong _pendingBatch;
        private ObservedCollaboratorStagedBatch _readyBatch;
        private bool _ready, _disposed;

        public ObservedCollaboratorAdmissionBarrier(
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> participants)
        {
            if (participants == null || participants.Count < 2 || participants.Count > 64)
                throw new ArgumentException("C1 requires 2..64 explicit source participants.", nameof(participants));
            _anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var participant in participants)
            {
                if (string.IsNullOrWhiteSpace(participant.Key) || participant.Key.IndexOf('\0') >= 0 ||
                    participant.Value == null || participant.Value.Count == 0)
                    throw new ArgumentException("Every C1 participant needs an explicit ID and anchors.", nameof(participants));
                var anchors = new HashSet<string>(StringComparer.Ordinal);
                foreach (string anchor in participant.Value)
                    if (!AnchorPoseFrameValidation.IsValidAnchorId(anchor) || !anchors.Add(anchor))
                        throw new ArgumentException("C1 participant anchors must be valid and unique.", nameof(participants));
                _anchors.Add(participant.Key, anchors);
                _lastSequences.Add(participant.Key, -1);
                _lastTimes.Add(participant.Key, double.NegativeInfinity);
            }
        }

        public ulong LastAcknowledgedBatchId => _lastAcknowledged;
        public bool HasPendingBatch => _pendingBatch != 0;
        public bool IsOwnerThread => !_disposed && Thread.CurrentThread.ManagedThreadId == _ownerThread;

        public ObservedCollaboratorStageResult TryStage(ulong batchId, string participantId,
            AnchorPoseFrame frame, out ObservedCollaboratorStagedBatch batch, out string error)
        {
            batch = null; error = null;
            if (!IsOwnerThread) return FailStage("C1 batch staging requires its owner thread.", out error);
            if (batchId == 0 || batchId <= _lastAcknowledged || string.IsNullOrWhiteSpace(participantId) ||
                !_anchors.TryGetValue(participantId, out var anchors) || frame == null ||
                !AnchorPoseFrameValidation.TryValidateFrame(frame, _lastSequences[participantId],
                    _lastTimes[participantId], anchors, out error) ||
                !HasExactAnchors(frame, anchors))
            {
                RejectCurrent(batchId);
                return FailStage("C1 batch identity, participant, or complete source frame is invalid.", out error);
            }
            if (_pendingBatch != 0 && _pendingBatch != batchId)
                return FailStage("A C1 batch is already pending; it must be acknowledged or explicitly aborted.", out error);
            if (_ready || _pending.ContainsKey(participantId))
            {
                RejectCurrent(batchId);
                return FailStage("C1 accepts one frame per participant in one batch.", out error);
            }
            _pendingBatch = batchId;
            _pending.Add(participantId, AnchorPoseFrameValidation.DeepCopy(frame));
            if (_pending.Count != _anchors.Count) return ObservedCollaboratorStageResult.Pending;
            _ready = true;
            _readyBatch = new ObservedCollaboratorStagedBatch(_pendingBatch, _pending);
            batch = _readyBatch;
            return ObservedCollaboratorStageResult.Ready;
        }

        /// <summary>Commits only after the one native C1 call accepted this exact batch.</summary>
        public bool TryAcknowledgeNativeAdmission(ObservedCollaboratorStagedBatch batch, out string error)
        {
            error = null;
            if (!IsOwnerThread || !_ready || batch == null || !ReferenceEquals(batch, _readyBatch))
                return FailBool("Only the current ready C1 batch may acknowledge native admission.", out error);
            foreach (var item in _pending)
            {
                _lastSequences[item.Key] = item.Value.sequence;
                _lastTimes[item.Key] = item.Value.timeSeconds;
            }
            _lastAcknowledged = _pendingBatch;
            ClearPending();
            return true;
        }

        /// <summary>Discards all staged copies without consuming a batch ID or frame sequence.</summary>
        public bool TryAbort(ulong batchId, out string error)
        {
            error = null;
            if (!IsOwnerThread || _pendingBatch == 0 || batchId != _pendingBatch)
                return FailBool("Only the current C1 batch may be aborted.", out error);
            ClearPending();
            return true;
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                throw new InvalidOperationException("C1 admission barrier disposal requires its owner thread.");
            _disposed = true; ClearPending();
        }

        private void ClearPending() { _pending.Clear(); _pendingBatch = 0; _ready = false; _readyBatch = null; }
        private void RejectCurrent(ulong batchId) { if (_pendingBatch == batchId) ClearPending(); }
        private static bool HasExactAnchors(AnchorPoseFrame frame, HashSet<string> anchors)
        {
            if (frame.anchors == null || frame.anchors.Length != anchors.Count) return false;
            var supplied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sample in frame.anchors)
                if (sample == null || !supplied.Add(sample.anchorId)) return false;
            return supplied.SetEquals(anchors);
        }
        private static ObservedCollaboratorStageResult FailStage(string message, out string error)
        { error = message; return ObservedCollaboratorStageResult.Rejected; }
        private static bool FailBool(string message, out string error)
        { error = message; return false; }
    }
}
