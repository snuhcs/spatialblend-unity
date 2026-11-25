// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>
    /// Owner-thread scheduling of already validated immutable whole-body frames.
    /// No dataset decoding, calibration, clock mapping, resampling or input/output
    /// authority is granted here. The exact frame/profile objects are retained.
    ///
    /// One due frame is consumed per successful Sample return, not per downstream
    /// acknowledgement (IReceiverBodySource has no acknowledgement). Due backlogs
    /// drain in original order; an expired undelivered frame faults this source
    /// permanently instead of skipping/coalescing or refreshing its timestamp.
    /// There is no rewind, seek, clock reset or final-pose hold past expiry.
    /// </summary>
    public sealed class BufferedReceiverBodySource : IReceiverBodySource
    {
        public const int MaximumFrames = 100000;
        public const int MaximumJointSamples = 1000000;
        private readonly ReceiverBodyFrame[] _frames;
        private readonly int _ownerThread;
        private int _cursor;
        private long _lastAcquisitionFloor;
        private ReceiverBodyFrame _lastDelivered;
        private string _fault;

        private BufferedReceiverBodySource(ReceiverBodyProfile profile, ReceiverBodyFrame[] frames)
        {
            Profile = profile; _frames = frames;
            _ownerThread = Thread.CurrentThread.ManagedThreadId;
        }
        public ReceiverBodyProfile Profile { get; }
        public int FrameCount => _frames.Length;
        /// <summary>Acquired frames, NOT native acknowledgements or accepted dataset records.</summary>
        public int DeliveredFrameCount => _cursor;
        public bool IsFaulted => _fault != null;

        public static bool TryCreate(ReceiverBodyProfile profile, IReadOnlyList<ReceiverBodyFrame> frames,
            out BufferedReceiverBodySource source, out string error)
        {
            source = null; error = null;
            if (profile == null)
                return ReceiverInputValidation.Fail("An exact validated body profile is required.", out error);
            try
            {
                int count = frames == null ? -1 : frames.Count;
                if (count < 0 || count > MaximumFrames || count > MaximumJointSamples / profile.Skeleton.Joints.Count)
                    return ReceiverInputValidation.Fail("A bounded body frame buffer is required; an empty buffer is explicit loss.", out error);
                var copy = new ReceiverBodyFrame[count];
                ReceiverBodyFrame previous = null;
                for (int i = 0; i < count; ++i)
                {
                    ReceiverBodyFrame frame = frames[i];
                    if (frame == null || !ReferenceEquals(frame.Profile, profile))
                        return ReceiverInputValidation.Fail("Every buffered frame must belong to the exact profile object.", out error);
                    if (previous != null &&
                        (frame.SourceSequence <= previous.SourceSequence ||
                         frame.GeometricInput.Sequence <= previous.GeometricInput.Sequence ||
                         frame.SourceTimeSeconds < previous.SourceTimeSeconds ||
                         frame.AcquisitionUpperNanoseconds < previous.AcquisitionUpperNanoseconds))
                        return ReceiverInputValidation.Fail("Buffered source/native sequences must increase and original source/mapped time must not go backwards.", out error);
                    copy[i] = frame; previous = frame;
                }
                if (frames.Count != count)
                    return ReceiverInputValidation.Fail("Body frame collection changed while copying.", out error);
                source = new BufferedReceiverBodySource(profile, copy);
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IndexOutOfRangeException || exception is InvalidOperationException)
            { return ReceiverInputValidation.Fail("Body frame collection could not be frozen.", out error); }
        }

        public bool TryAcquire(ReceiverBodyProfile profile, long nativeAcquisitionFloor,
            out ReceiverBodySampleStatus status, out ReceiverBodyFrame frame, out string error)
        {
            status = ReceiverBodySampleStatus.Unavailable; frame = null; error = null;
            // Check call identity before advancing any state. Wrong-thread/stale
            // callers cannot consume or fault the legitimate owner's buffer.
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                return ReceiverInputValidation.Fail("Buffered body acquisition requires its construction owner thread.", out error);
            if (!ReferenceEquals(profile, Profile))
                return ReceiverInputValidation.Fail("Buffered body acquisition requires the exact profile object.", out error);
            if (nativeAcquisitionFloor <= 0 || nativeAcquisitionFloor < _lastAcquisitionFloor)
                return ReceiverInputValidation.Fail("The original native acquisition floor must be positive and monotonic.", out error);
            if (_fault != null) return ReceiverInputValidation.Fail(_fault, out error);
            _lastAcquisitionFloor = nativeAcquisitionFloor;
            if (_cursor < _frames.Length)
            {
                ReceiverBodyFrame next = _frames[_cursor];
                if (next.AcquisitionUpperNanoseconds <= nativeAcquisitionFloor)
                {
                    if (next.GeometricInput.ValidUntilNanoseconds <= nativeAcquisitionFloor)
                    {
                        _fault = "An undelivered body frame expired; this buffer cannot skip, restamp or resume past it.";
                        return ReceiverInputValidation.Fail(_fault, out error);
                    }
                    _lastDelivered = frame = next;
                    ++_cursor;
                    status = ReceiverBodySampleStatus.Sample;
                    return true;
                }
            }
            if (_lastDelivered != null && nativeAcquisitionFloor < _lastDelivered.GeometricInput.ValidUntilNanoseconds)
                status = ReceiverBodySampleStatus.NoChange;
            return true;
        }
    }
}
