// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Main-thread, exact-registration receiver geometric input owner. Closing
    /// withdraws the frame, not the sticky receiver role. Never drives a body.
    /// </summary>
    public sealed partial class ReceiverInputWriter : IDisposable
    {
        internal ReceiverInputWriter(LocalActorRegistration registration, ReceiverInputEpoch input)
        { Registration = registration; Input = input; }
        internal LocalActorRegistration Registration { get; }
        private int _closed;
        internal bool Closed => Volatile.Read(ref _closed) != 0;
        internal void MarkClosed() { Volatile.Write(ref _closed, 1); }
        private ReceiverBodyInputBinding _bodyBinding;
        private bool _unprofiledInputSubmitted;
        public ReceiverInputEpoch Input { get; }

        /// <summary>
        /// Read immediately before actual pose acquisition for a conservative
        /// bound, or supply a separately
        /// calibrated native-domain timestamp. This does not attest when a sensor
        /// acquired an old buffered sample. Do not restamp that sample here.
        /// </summary>
        public bool TryReadAcquisitionClock(out long nanoseconds, out string error)
        {
            nanoseconds = 0;
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            return SpatialBlendAPI.TryReadReceiverInputTime(Input, out nanoseconds, out error);
        }
        public bool TrySubmit(ReceiverInputFrame frame, out string error)
        {
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (_bodyBinding != null)
                return ReceiverInputValidation.Fail("This receiver epoch belongs to its explicit body input binding.", out error);
            if (frame == null || !ReferenceEquals(frame.Input, Input))
                return ReceiverInputValidation.Fail("Receiver frame belongs to another input lifetime/epoch.", out error);
            if (!SpatialBlendAPI.TrySubmitReceiverInput(frame, out error)) return false;
            _currentApproachFrame = frame;
            _unprofiledInputSubmitted = true;
            return true;
        }
        public bool TryClear(out string error)
        {
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (_bodyBinding != null)
                return ReceiverInputValidation.Fail("Clear through the owning body input binding or close this writer.", out error);
            if (!SpatialBlendAPI.TryClearReceiverInput(Input, out error)) return false;
            _currentApproachFrame = null;
            Registration.World.RetireReceiverApproach(this);
            return true;
        }
        internal bool TryBindBody(ReceiverBodyProfile profile, out ReceiverBodyInputBinding binding, out string error)
        {
            binding = null;
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (_bodyBinding != null || _unprofiledInputSubmitted || profile == null || !ReferenceEquals(profile.Input, Input))
                return ReceiverInputValidation.Fail("Begin a fresh receiver epoch for one immutable body profile; accepted unprofiled input cannot be relabeled.", out error);
            _bodyBinding = new ReceiverBodyInputBinding(this, profile);
            binding = _bodyBinding;
            return true;
        }
        internal bool TrySubmitBody(ReceiverBodyInputBinding binding, ReceiverInputFrame frame, out string error)
        {
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (!ReferenceEquals(binding, _bodyBinding) || frame == null || !ReferenceEquals(frame.Input, Input))
                return ReceiverInputValidation.Fail("Body publication requires this epoch's exact binding and input.", out error);
            if (!SpatialBlendAPI.TrySubmitReceiverInput(frame, out error)) return false;
            _currentApproachFrame = frame;
            return true;
        }
        internal bool TryClearBody(ReceiverBodyInputBinding binding, out string error)
        {
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (!ReferenceEquals(binding, _bodyBinding))
                return ReceiverInputValidation.Fail("Body withdrawal requires the exact owning binding.", out error);
            if (!SpatialBlendAPI.TryClearReceiverInput(Input, out error)) return false;
            _currentApproachFrame = null;
            Registration.World.RetireReceiverApproach(this);
            return true;
        }
        internal bool TryReevaluateBody(ReceiverBodyInputBinding binding, ulong lifetime,
            ulong sequence, out ReceiverReevaluationStatus status, out string error)
        {
            status = ReceiverReevaluationStatus.Unavailable;
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (!ReferenceEquals(binding, _bodyBinding) || lifetime == 0 ||
                !Registration.World.IsRichRealizationDeliveryCurrent(Registration, lifetime))
                return ReceiverInputValidation.Fail("Reevaluation requires this exact body binding and active rich lifetime.", out error);
            if (!SpatialBlendAPI.TryReevaluateReceiverRealization(lifetime, Input, sequence, out int result, out error)) return false;
            if (!Registration.World.CheckReceiverInputWriter(this, out error) ||
                !Registration.World.IsRichRealizationDeliveryCurrent(Registration, lifetime))
                return ReceiverInputValidation.Fail("Reevaluation ownership retired during the native operation.", out error);
            if (result != 1 && result != 0 && result != -5)
                return ReceiverInputValidation.Fail("Native receiver reevaluation rejected the exact input/lifetime (result " + result + ").", out error);
            status = result == 1 ? ReceiverReevaluationStatus.Evaluated : result == 0
                ? ReceiverReevaluationStatus.NoNewInput : ReceiverReevaluationStatus.Unavailable;
            return true;
        }
        public bool TryGetValidity(RichRealizationSnapshot snapshot, out ReceiverInputValidity validity,
            out int nativeResult, out string error)
        {
            validity = null; nativeResult = -1;
            if (!Registration.World.CheckReceiverInputWriter(this, out error)) return false;
            if (snapshot == null || snapshot.Header.ReceiverSpaceId != Registration.NativeSpace ||
                snapshot.Header.ReceiverActorId != Registration.NativeActor ||
                !Registration.World.IsRichRealizationDeliveryCurrent(Registration, snapshot.Header.ProducerLifetime))
                return ReceiverInputValidation.Fail("Receiver comparison requires this registration's active rich snapshot.", out error);
            return SpatialBlendAPI.TryGetReceiverInputValidity(snapshot.Header.ProducerLifetime,
                snapshot.Header.Revision, out validity, out nativeResult, out error);
        }
        public bool TryClose(out string error) => Registration.World.CloseReceiverInputWriter(this, out error);
        public void Dispose()
        {
            if (!TryClose(out string error)) throw new InvalidOperationException(error);
        }
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        private readonly Dictionary<LocalActorRegistration, ReceiverInputWriter> _receiverInputs =
            new Dictionary<LocalActorRegistration, ReceiverInputWriter>();
        private readonly HashSet<LocalActorRegistration> _receiverBeginInProgress =
            new HashSet<LocalActorRegistration>();
        // Failure-only seam for cleanup tests; it cannot admit input or fake a
        // successful native operation. Normal production paths leave it null.
        internal Func<bool> TestOnlyRejectReceiverClear;

        internal bool CheckReceiverInputWriter(ReceiverInputWriter writer, out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return ReceiverInputValidation.Fail("Receiver input requires the owning Unity main thread.", out error);
            LocalActorRegistration registration = writer?.Registration;
            if (writer == null || writer.Closed || registration == null || !registration.IsLive ||
                registration.World != this || registration.Generation != _generation || !IsInitialized ||
                !_receiverInputs.TryGetValue(registration, out ReceiverInputWriter current) ||
                !ReferenceEquals(current, writer))
                return ReceiverInputValidation.Fail("Receiver input registration/lifetime is retired or stale.", out error);
            return true;
        }

        internal bool TryBeginReceiverInput(LocalActorRegistration registration, string root,
            IReadOnlyList<string> layout, ulong rig, ulong calibration,
            out ReceiverInputWriter writer, out string error)
        {
            writer = null; error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || registration == null ||
                !registration.IsLive || registration.World != this || registration.Generation != _generation || !IsInitialized)
                return ReceiverInputValidation.Fail("Receiver Begin requires a live main-thread registration.", out error);
            if (!_receiverBeginInProgress.Add(registration))
                return ReceiverInputValidation.Fail("Receiver Begin is already in progress for this registration.", out error);
            try
            {
                return TryBeginReceiverInputGuarded(registration, root, layout, rig, calibration, out writer, out error);
            }
            finally { _receiverBeginInProgress.Remove(registration); }
        }

        private bool TryBeginReceiverInputGuarded(LocalActorRegistration registration, string root,
            IReadOnlyList<string> layout, ulong rig, ulong calibration,
            out ReceiverInputWriter writer, out string error)
        {
            writer = null; error = null;
            lock (_actorLock)
            {
                if (!_actorsByNative.TryGetValue((registration.NativeSpace, registration.NativeActor), out LocalActorRegistration live) ||
                    !ReferenceEquals(live, registration) ||
                    (_sourceActorsById.TryGetValue(registration.ActorId, out LocalActorRegistration source) && ReferenceEquals(source, registration)))
                    return ReceiverInputValidation.Fail("Receiver Begin cannot use a source or replaced actor registration.", out error);
            }
            if (!SpatialBlendAPI.TryBeginReceiverInput(registration.NativeSpace, registration.NativeActor,
                root, layout, rig, calibration, out ReceiverInputEpoch input, out error)) return false;
            if (_receiverInputs.TryGetValue(registration, out ReceiverInputWriter previous))
            {
                previous.MarkClosed();
                RetireReceiverApproach(previous);
            }
            var next = new ReceiverInputWriter(registration, input);
            _receiverInputs[registration] = next;
            lock (_actorLock)
            {
                registration.ReceiverInputTargetOnly = true;
                _queuedRealizations.Remove((registration.NativeSpace, registration.NativeActor, registration.Generation));
            }
            // Withdraw old reduced outputs only after the native role transition
            // succeeded. Subscriber reentry may retire or replace this exact slot.
            registration.Owner.EnterReceiverInputMode(registration);
            if (!CheckReceiverInputWriter(next, out error))
            {
                // Begin already committed. Retire only this exact writer after
                // subscriber reentry; closing is cleanup, not legacy rollback.
                // A replacement in the slot must remain untouched.
                CloseReceiverInputWriter(next, out _);
                return false;
            }
            writer = next;
            return true;
        }

        internal bool CloseReceiverInputWriter(ReceiverInputWriter writer, out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return ReceiverInputValidation.Fail("Receiver close requires the owning Unity main thread.", out error);
            if (writer == null || writer.Registration.World != this)
                return ReceiverInputValidation.Fail("Receiver writer belongs to another world.", out error);
            LocalActorRegistration registration = writer.Registration;
            if (!_receiverInputs.TryGetValue(registration, out ReceiverInputWriter current) ||
                !ReferenceEquals(current, writer))
            { writer.MarkClosed(); return true; }
            // Stop submissions immediately but retain exact cleanup ownership on
            // failure, so Close/Dispose can retry. A subsequent successful Begin
            // replaces the native epoch before dropping this closed slot.
            writer.MarkClosed();
            RetireReceiverApproach(writer);
            if (registration.IsLive && registration.Generation == _generation && _session != 0)
            {
                if (TestOnlyRejectReceiverClear != null && TestOnlyRejectReceiverClear())
                    return ReceiverInputValidation.Fail("Test-only native receiver clear rejection.", out error);
                if (!SpatialBlendAPI.TryClearReceiverInput(writer.Input, out error)) return false;
            }
            _receiverInputs.Remove(registration);
            return true;
        }

        private void RetireReceiverInput(LocalActorRegistration registration)
        {
            if (_receiverInputs.TryGetValue(registration, out ReceiverInputWriter writer) &&
                !CloseReceiverInputWriter(writer, out string error)) LastError = error;
            // Actor destruction immediately follows registration retirement and
            // is the terminal native cleanup if Clear was unavailable/stale.
            _receiverInputs.Remove(registration);
        }
    }
}
