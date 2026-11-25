// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace SpatialBlend
{
    public enum ReceiverReevaluationStatus { Unavailable, NoNewInput, Evaluated }

    /// <summary>
    /// Explicit numeric body provider → actual native geometric ingress.
    /// Owns the supplied writer on successful Bind. Never selects a source, reads
    /// generated Transform state, publishes to AvatarSDK or acquires body DOFs.
    /// </summary>
    public sealed partial class ReceiverBodyInputBinding : IDisposable
    {
        private readonly ReceiverInputWriter _writer;
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private ReceiverBodyFrame _current;
        private long _lastSourceSequence = -1;
        private double _lastSourceTime = -1;
        private bool _closed;
        private object _publisher;

        internal ReceiverBodyInputBinding(ReceiverInputWriter writer, ReceiverBodyProfile profile)
        { _writer = writer; Profile = profile; }
        public ReceiverBodyProfile Profile { get; }

        public static bool TryBind(ReceiverInputWriter writer, ReceiverBodyProfile profile,
            out ReceiverBodyInputBinding binding, out string error)
        {
            binding = null; error = null;
            if (writer == null || profile == null || !ReferenceEquals(writer.Input, profile.Input))
                return ReceiverInputValidation.Fail("Body profile must belong to this exact receiver input epoch.", out error);
            return writer.TryBindBody(profile, out binding, out error);
        }

        /// <summary>Caller reads this BEFORE acquiring a fresh synchronous sample, never to restamp buffered data.</summary>
        public bool TryReadAcquisitionClock(out long nanoseconds, out string error)
        {
            nanoseconds = 0;
            if (!Check(out error)) return false;
            return _writer.TryReadAcquisitionClock(out nanoseconds, out error);
        }

        public bool TrySubmit(ReceiverBodyFrame frame, out string error)
            => TrySubmitOwned(null, frame, out error);

        /// <summary>
        /// Explicitly recomputes retained source cues for the latest acknowledged
        /// independent body, without resubmitting either stream. Preserves source
        /// receipt age/deadlines. Does not drain delivery or start/step an actuator.
        /// Unavailable is expected until a source packet exists or while input is
        /// unavailable; callers must still withdraw invalid body work.
        /// </summary>
        public bool TryReevaluateRichRealization(ulong producerLifetime,
            out ReceiverReevaluationStatus status, out string error)
        {
            status = ReceiverReevaluationStatus.Unavailable;
            if (!Check(out error)) return false;
            var body = Volatile.Read(ref _current);
            if (body == null)
                return ReceiverInputValidation.Fail("Reevaluation requires an acknowledged independent body frame.", out error);
            return _writer.TryReevaluateBody(this, producerLifetime, body.GeometricInput.Sequence, out status, out error);
        }

        // Optional SDK ingress owns this fresh binding, not the SDK's animated
        // output DOFs. Raw publication cannot bypass that coordinated ingress.
        internal bool TryClaimPublisher(object publisher, out string error)
        {
            if (!Check(out error)) return false;
            if (publisher == null || _publisher != null || _lastSourceSequence >= 0)
                return ReceiverInputValidation.Fail("An SDK publisher requires one fresh unused body binding.", out error);
            _publisher = publisher;
            return true;
        }

        internal bool IsCurrentForCallback(object publisher, ReceiverBodyFrame frame) =>
            ReferenceEquals(_publisher, publisher) && !_writer.Closed &&
            frame != null && ReferenceEquals(Volatile.Read(ref _current), frame);

        internal bool TrySubmitOwned(object publisher, ReceiverBodyFrame frame, out string error)
        {
            if (!Check(out error)) return false;
            if (!ReferenceEquals(_publisher, publisher))
                return ReceiverInputValidation.Fail("Submit through the exact owning body publisher.", out error);
            if (frame == null || !ReferenceEquals(frame.Profile, Profile) ||
                frame.SourceSequence <= _lastSourceSequence || frame.SourceTimeSeconds < _lastSourceTime)
                return ReceiverInputValidation.Fail("Body frame has another immutable profile or stale original source order/time.", out error);
            // No state/fence changes before actual native acknowledgement. Native
            // independently checks input sequence, acquisition, expiry and layout.
            if (!_writer.TryReadAcquisitionClock(out long now, out error)) return false;
            if (frame.AcquisitionUpperNanoseconds > now)
                return ReceiverInputValidation.Fail("The mapped body's entire acquisition interval must have occurred before submission.", out error);
            if (!_writer.TrySubmitBody(this, frame.GeometricInput, out error)) return false;
            Volatile.Write(ref _current, frame);
            _lastSourceSequence = frame.SourceSequence; _lastSourceTime = frame.SourceTimeSeconds;
            return true;
        }

        /// <summary>
        /// Returns the EXACT body that supplied both captured and current native
        /// input, as of the query. No nearest-time matching, revision invention,
        /// poll-cursor advancement or multi-domain actuation certificate.
        /// </summary>
        public bool TryGetBodyForRealization(RichRealizationSnapshot snapshot,
            out ReceiverBodyFrame body, out string error)
        {
            body = null;
            if (!Check(out error)) return false;
            ReceiverBodyFrame candidate = _current;
            if (candidate == null)
                return ReceiverInputValidation.Fail("No acknowledged body input is retained.", out error);
            if (!_writer.TryGetValidity(snapshot, out ReceiverInputValidity validity, out int result, out error)) return false;
            ReceiverInputFrame geometric = candidate.GeometricInput;
            if (result != 1 || validity == null || !validity.Valid || !validity.Captured.Known || !validity.Current.Known ||
                validity.Captured.Epoch != Profile.Input.Epoch || validity.Current.Epoch != Profile.Input.Epoch ||
                validity.Captured.Sequence != geometric.Sequence || validity.Current.Sequence != geometric.Sequence ||
                validity.Captured.RigRevision != Profile.Input.RigRevision || validity.Captured.CalibrationRevision != Profile.Input.CalibrationRevision ||
                validity.Current.RigRevision != Profile.Input.RigRevision || validity.Current.CalibrationRevision != Profile.Input.CalibrationRevision ||
                validity.Captured.ObservedAtNanoseconds != geometric.ObservedAtNanoseconds ||
                validity.Current.ObservedAtNanoseconds != geometric.ObservedAtNanoseconds ||
                validity.Captured.ValidUntilNanoseconds != geometric.ValidUntilNanoseconds ||
                validity.Current.ValidUntilNanoseconds != geometric.ValidUntilNanoseconds ||
                validity.Captured.Revision != validity.Current.Revision)
                return ReceiverInputValidation.Fail("Rich realization does not have this body's exact currently valid native input.", out error);
            body = candidate;
            return true;
        }

        public bool TryClear(out string error)
            => TryClearOwned(null, out error);

        internal bool TryClearOwned(object publisher, out string error)
        {
            if (!Check(out error)) return false;
            if (!ReferenceEquals(_publisher, publisher))
                return ReceiverInputValidation.Fail("Clear through the exact owning body publisher or close the binding.", out error);
            // A failed native clear must never leave a body available for
            // admission. Retain the writer and sequence fences for cleanup/retry.
            Volatile.Write(ref _current, null);
            return _writer.TryClearBody(this, out error);
        }
        public bool TryClose(out string error)
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                return ReceiverInputValidation.Fail("Body binding close requires the Unity owner thread.", out error);
            bool closed = _writer.TryClose(out error);
            if (_writer.Closed) { _closed = true; Volatile.Write(ref _current, null); }
            return closed;
        }
        public void Dispose()
        {
            if (!TryClose(out string error)) throw new InvalidOperationException(error);
        }
        private bool Check(out string error)
        {
            if (_closed) return ReceiverInputValidation.Fail("Body input binding is closed.", out error);
            return _writer.Registration.World.CheckReceiverInputWriter(_writer, out error);
        }
    }
}
