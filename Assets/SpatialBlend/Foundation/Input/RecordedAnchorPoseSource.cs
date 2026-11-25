// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>Optional reset acknowledgment for a source beginning a new replay epoch.</summary>
    public interface IAnchorPoseFrameReplaySink : IAnchorPoseFrameSink
    {
        void ResetWorldAnchorReplay();
    }
    public interface IAnchorPoseFrameCheckedReplaySink : IAnchorPoseFrameReplaySink
    {
        bool TryResetWorldAnchorReplay();
    }
    internal interface IAnchorPoseFrameRetiredReplaySink
    {
        bool IsReplaySinkRetired { get; }
    }

    /// <summary>
    /// Replays recorded or synthetic Unity-world anchor observations. Each accepted
    /// frame is a complete snapshot; omitted anchors are no longer observed by the
    /// sink. This is deliberately an ingress seam, not an avatar rig or IK driver.
    /// </summary>
    public sealed class RecordedAnchorPoseSource : MonoBehaviour
    {
        [SerializeField] private string[] _knownAnchorIds = Array.Empty<string>();

        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _tracked = new HashSet<string>(StringComparer.Ordinal);
        private IAnchorPoseFrameSink _sink;
        private ReplaySinkLease _replaySinkLease;
        private long _nextReplaySinkLeaseId;
        private long _lastSequence = -1;
        private double _lastTime = double.NegativeInfinity;
        private bool _isSubmitting;
        private bool _isResetting;
        private bool _resetRequested;

        private void Awake()
        {
            TrySetKnownAnchorIds(_knownAnchorIds, out _);
        }

        private void OnDisable() => RequestReplayReset();
        private void OnDestroy() => RequestReplayReset();

        /// <summary>
        /// Replaces the receiver only after explicitly clearing this source's last
        /// accepted observations from the previous receiver.
        /// </summary>
        public void SetSink(IAnchorPoseFrameSink sink)
        {
            TrySetSink(sink, out _);
        }
        public bool TrySetSink(IAnchorPoseFrameSink sink, out string error)
        {
            error = null;
            if (_isSubmitting || _isResetting) { error = "Cannot replace sink while active."; return false; }
            if (ReferenceEquals(_sink, sink)) return true;
            if (!TryReplayReset())
            {
                if (!(_sink is IAnchorPoseFrameRetiredReplaySink retired && retired.IsReplaySinkRetired))
                { error = "Existing replay sink rejected reset acknowledgement."; return false; }
                // The replacement native epoch already retired this lease; detach
                // managed source fences without issuing any stale native operation.
                _tracked.Clear(); _lastSequence = -1; _lastTime = double.NegativeInfinity;
            }
            _sink = sink;
            InvalidateReplaySinkLease();
            return true;
        }

        /// <summary>
        /// Acquires the sole replay receiver for a local replay lifetime. The supplied
        /// identifiers are copied before this source is changed, and accepting the
        /// lease starts with empty observation and ordering fences.
        /// </summary>
        public bool TryAcquireReplaySink(IAnchorPoseFrameSink sink,
                                         IEnumerable<string> knownAnchorIds,
                                         out ReplaySinkLease lease,
                                         out string error)
        {
            lease = null;
            error = null;
            if (sink == null)
            {
                error = "A replay sink is required.";
                return false;
            }
            if (!TryBuildKnownAnchorIds(knownAnchorIds, out HashSet<string> candidate, out error))
                return false;
            if (_isSubmitting || _isResetting)
            {
                error = "Cannot acquire a replay sink while active.";
                return false;
            }
            if (_sink != null || _replaySinkLease != null)
            {
                error = "A replay sink is already configured.";
                return false;
            }

            _known.Clear();
            _known.UnionWith(candidate);
            _tracked.Clear();
            _lastSequence = -1;
            _lastTime = double.NegativeInfinity;
            _sink = sink;
            lease = new ReplaySinkLease(this, ++_nextReplaySinkLeaseId);
            _replaySinkLease = lease;
            return true;
        }

        public void SetKnownAnchorIds(IEnumerable<string> anchorIds)
        {
            TrySetKnownAnchorIds(anchorIds, out _);
        }

        public bool TrySetKnownAnchorIds(IEnumerable<string> anchorIds, out string error)
        {
            error = null;
            if (!TryBuildKnownAnchorIds(anchorIds, out HashSet<string> candidate, out error))
                return false;
            if (_isSubmitting || _isResetting)
            {
                error = "Cannot change known anchors while submitting a frame.";
                return false;
            }
            if (!TryReplayReset())
            {
                error = "Existing replay sink rejected reset acknowledgement.";
                return false;
            }
            _known.Clear();
            _known.UnionWith(candidate);
            return true;
        }

        /// <summary>Clears this source's active observations and accepts a new replay epoch.</summary>
        public void ReplayReset() { TryReplayReset(); }
        public bool TryReplayReset()
        {
            if (_isSubmitting || _isResetting) return false;
            IAnchorPoseFrameSink previousSink = _sink;
            _isResetting = true;
            try
            {
                if (previousSink != null)
                {
                    if (previousSink is IAnchorPoseFrameCheckedReplaySink checkedSink)
                    {
                        if (!checkedSink.TryResetWorldAnchorReplay()) return false;
                    }
                    else
                    {
                        var previousIds = new List<string>(_tracked);
                        previousIds.Sort(StringComparer.Ordinal);
                        foreach (string id in previousIds) previousSink.ClearWorldAnchor(id);
                        if (previousSink is IAnchorPoseFrameReplaySink replaySink)
                            replaySink.ResetWorldAnchorReplay();
                    }
                }
                _tracked.Clear();
                _lastSequence = -1;
                _lastTime = double.NegativeInfinity;
                return true;
            }
            finally
            {
                _isResetting = false;
            }
        }

        private void RequestReplayReset()
        {
            if (_isSubmitting || _isResetting)
            {
                _resetRequested = true;
                return;
            }
            TryReplayReset();
        }

        public bool TrySubmit(AnchorPoseFrame frame, out string error)
        {
            error = null;
            if (_isSubmitting || _isResetting)
            {
                error = "Reentrant anchor-pose submission is rejected.";
                return false;
            }
            IAnchorPoseFrameSink sink = _sink;
            if (sink == null)
            {
                error = "No anchor-pose sink is configured.";
                return false;
            }
            if (!AnchorPoseFrameValidation.TryValidateFrame(frame, _lastSequence, _lastTime, _known,
                out error)) return false;

            // Never pass a caller-owned DTO across the external callback boundary.
            AnchorPoseFrame snapshot = AnchorPoseFrameValidation.DeepCopy(frame);
            var acceptedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (AnchorPoseSample sample in frame.anchors) acceptedIds.Add(sample.anchorId);
            long acceptedSequence = frame.sequence;
            double acceptedTime = frame.timeSeconds;
            _isSubmitting = true;
            try
            {
                if (!sink.TryApplyWorldFrame(snapshot))
                {
                    error = "Anchor-pose sink rejected the validated frame.";
                    return false;
                }
                _tracked.Clear();
                _tracked.UnionWith(acceptedIds);
                _lastSequence = acceptedSequence;
                _lastTime = acceptedTime;
                return true;
            }
            finally
            {
                _isSubmitting = false;
                if (_resetRequested)
                {
                    _resetRequested = false;
                    TryReplayReset();
                }
            }
        }

        public bool TrySubmitJson(string json, out string error)
        {
            return AnchorPoseJson.TryDecode(json, out AnchorPoseFrame frame, out error) &&
                TrySubmit(frame, out error);
        }

        private static bool TryBuildKnownAnchorIds(IEnumerable<string> anchorIds,
                                                   out HashSet<string> candidate,
                                                   out string error)
        {
            candidate = new HashSet<string>(StringComparer.Ordinal);
            error = null;
            if (anchorIds == null)
                return true;
            foreach (string id in anchorIds)
            {
                if (!AnchorPoseFrameValidation.IsValidAnchorId(id) || !candidate.Add(id))
                {
                    error = "Known anchor IDs must be unique, nonempty, and contain no NUL.";
                    return false;
                }
            }
            return true;
        }

        private bool IsCurrentReplaySinkLease(ReplaySinkLease lease, long leaseId)
        {
            return lease != null && leaseId != 0 && ReferenceEquals(_replaySinkLease, lease) &&
                leaseId == lease.LeaseId;
        }

        private bool TryReleaseReplaySink(ReplaySinkLease lease, long leaseId, out string error)
        {
            error = null;
            if (!IsCurrentReplaySinkLease(lease, leaseId))
            {
                error = "Replay sink lease is no longer current.";
                return false;
            }
            if (_isSubmitting || _isResetting)
            {
                error = "Cannot release a replay sink while a callback is active.";
                return false;
            }

            if (!TryReplayReset())
            {
                if (!(_sink is IAnchorPoseFrameRetiredReplaySink retired && retired.IsReplaySinkRetired))
                {
                    error = "Replay sink rejected reset acknowledgement.";
                    return false;
                }
                // A retired native actor cannot acknowledge another reset. It is safe
                // to detach only the managed fences owned by this exact lease.
                _tracked.Clear();
                _lastSequence = -1;
                _lastTime = double.NegativeInfinity;
            }

            _sink = null;
            _known.Clear();
            InvalidateReplaySinkLease();
            return true;
        }

        private void InvalidateReplaySinkLease()
        {
            ReplaySinkLease lease = _replaySinkLease;
            _replaySinkLease = null;
            lease?.Retire();
        }

        /// <summary>
        /// Exact ownership token returned by <see cref="TryAcquireReplaySink"/>.
        /// A stale token can never clear a sink installed by a later owner.
        /// </summary>
        public sealed class ReplaySinkLease : IDisposable
        {
            private RecordedAnchorPoseSource _owner;
            private readonly long _leaseId;

            internal ReplaySinkLease(RecordedAnchorPoseSource owner, long leaseId)
            {
                _owner = owner;
                _leaseId = leaseId;
            }

            internal long LeaseId => _leaseId;
            public bool IsCurrent => _owner != null && _owner.IsCurrentReplaySinkLease(this, _leaseId);

            public bool TryRelease(out string error)
            {
                RecordedAnchorPoseSource owner = _owner;
                if (owner == null)
                {
                    error = "Replay sink lease is no longer current.";
                    return false;
                }
                return owner.TryReleaseReplaySink(this, _leaseId, out error);
            }

            public void Dispose()
            {
                TryRelease(out _);
            }

            internal void Retire()
            {
                _owner = null;
            }
        }
    }
}
