// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    // C4 owns a receiver-local, main-thread handle only. It neither receives
    // source causality (S3) nor supplies a controller/route consumer (S4).
    internal sealed class ReceiverApproachContextLease
    {
        internal ReceiverApproachContextLease(ReceiverInputWriter writer, long session, int generation,
            ReceiverInputFrame frame, ReceiverApproachContextInput input, ulong revision)
        { Writer = writer; Session = session; Generation = generation; Frame = frame; Input = input; ContextRevision = revision; OwnerThread = Thread.CurrentThread.ManagedThreadId; }
        internal ReceiverInputWriter Writer { get; }
        internal long Session { get; }
        internal int Generation { get; }
        internal ReceiverInputFrame Frame { get; }
        internal ReceiverApproachContextInput Input { get; }
        internal ulong ContextRevision { get; }
        internal int OwnerThread { get; }
        internal ulong RegistrationGeneration { get; private set; }
        internal bool Retired { get; private set; }
        internal void Retire() { Retired = true; }
        internal bool TryCopy(ulong fixedCalculationRevision, out ReceiverApproachFeasibilityReceipt receipt, out int nativeResult, out string error)
        {
            receipt = null; nativeResult = -1; error = null;
            if (!TryCheck(out error)) return false;
            var registration = Writer.Registration;
            var expected = new ReceiverApproachExpectedIdentity(Session, registration.NativeSpace, registration.NativeActor,
                ContextRevision, Frame.Input.Epoch, Frame.Sequence, Input.GeometryRevision, Input.SupportRevision,
                Math.Min(Input.RequestedValidUntilNanoseconds, Frame.ValidUntilNanoseconds));
            if (!SpatialBlendAPI.TryCopyReceiverApproachFeasibility(Session, registration.NativeSpace, registration.NativeActor,
                ContextRevision, expected, fixedCalculationRevision, out var native, out nativeResult, out error)) return false;
            if (!TryCheck(out error)) return false;
            if (RegistrationGeneration != 0 && native.Header.registration_generation != RegistrationGeneration)
                return ReceiverInputValidation.Fail("Receiver approach registration changed during an exact copy.", out error);
            RegistrationGeneration = native.Header.registration_generation;
            receipt = new ReceiverApproachFeasibilityReceipt(this, native);
            return true;
        }
        internal bool TryCheck(out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != OwnerThread || Retired || Writer == null ||
                !Writer.Registration.World.CheckReceiverInputWriter(Writer, out error) ||
                !ReferenceEquals(Writer.CurrentApproachFrame, Frame) ||
                !ReferenceEquals(Frame.Input, Writer.Input) || Frame.Input.Epoch != Input.InputEpoch || Frame.Sequence != Input.InputSequence ||
                Writer.Registration.World.CurrentReceiverApproach(Writer) != this)
                return ReceiverInputValidation.Fail("Receiver approach owner/input lifetime is stale or replaced.", out error);
            return true;
        }
    }

    /// <summary>Immutable C3 route receipt. It remains an availability proof,
    /// not a locomotion command, source cue, body admission, or output lease.</summary>
    public sealed class ReceiverApproachFeasibilityReceipt
    {
        private readonly ReceiverApproachContextLease _lease;
        private readonly double[] _route;
        internal ReceiverApproachFeasibilityReceipt(ReceiverApproachContextLease lease, ReceiverApproachNativeCopy native)
        {
            _lease = lease; var h = native.Header; FeasibilityStatus = h.feasibility_status;
            RegistrationGeneration = h.registration_generation; ReceiverInputEpoch = h.receiver_input_epoch;
            ReceiverInputSequence = h.receiver_input_sequence; ReceiverInputRevision = h.receiver_input_revision;
            GeometryRevision = h.geometry_revision; SupportRevision = h.support_revision; ContextRevision = h.context_revision;
            CalculationRevision = h.calculation_revision; OwnerLifetime = h.owner_lifetime; NativeClockDomain = h.native_clock_domain;
            IssuedAtNanoseconds = h.issued_at_ns; ValidUntilNanoseconds = h.valid_until_ns;
            StartX = h.start_x; StartY = h.start_y; SafeApproachX = h.safe_approach_x; SafeApproachY = h.safe_approach_y; RouteLengthMeters = h.route_length_m;
            // These scalars are part of the C3 registration that this immutable
            // receipt copied.  They expose receiver-local destination identity
            // to a later read-only composition layer; they are not a lookup,
            // a Transform, or a source/H identity substitute.
            ReceiverNativeSpace = lease.Writer.Registration.NativeSpace;
            ReceiverNativeActor = lease.Writer.Registration.NativeActor;
            DestinationNativeSpace = lease.Input.DestinationSpace;
            DestinationRegionId = lease.Input.DestinationRegion;
            DestinationNativeObject = lease.Input.DestinationObject;
            DestinationAffordanceId = lease.Input.DestinationAffordance;
            var values = native.RouteXY; _route = new double[values.Count]; for (int i = 0; i < _route.Length; ++i) _route[i] = values[i];
        }
        public int FeasibilityStatus { get; }
        public ulong RegistrationGeneration { get; } public ulong ReceiverInputEpoch { get; }
        public ulong ReceiverInputSequence { get; } public ulong ReceiverInputRevision { get; }
        public ulong GeometryRevision { get; } public ulong SupportRevision { get; } public ulong ContextRevision { get; }
        public ulong CalculationRevision { get; } public ulong OwnerLifetime { get; } public ulong NativeClockDomain { get; }
        public long IssuedAtNanoseconds { get; } public long ValidUntilNanoseconds { get; }
        public double StartX { get; } public double StartY { get; } public double SafeApproachX { get; } public double SafeApproachY { get; }
        public double RouteLengthMeters { get; }
        public long ReceiverNativeSpace { get; }
        public long ReceiverNativeActor { get; }
        public long DestinationNativeSpace { get; }
        public ulong DestinationRegionId { get; }
        public long DestinationNativeObject { get; }
        public ulong DestinationAffordanceId { get; }
        public IReadOnlyList<double> RouteXY => Array.AsReadOnly((double[])_route.Clone());

        // Revalidates the exact frozen calculation, never re-probes or extends
        // a deadline. All route/header identity must match bit-for-bit.
        public bool TryRevalidate(out string error)
        {
            error = null;
            if (_lease == null || !_lease.TryCopy(CalculationRevision, out var current, out _, out error)) return false;
            if (current.CalculationRevision != CalculationRevision || current.OwnerLifetime != OwnerLifetime ||
                current.ValidUntilNanoseconds != ValidUntilNanoseconds || current.RegistrationGeneration != RegistrationGeneration ||
                current.ReceiverInputEpoch != ReceiverInputEpoch || current.ReceiverInputSequence != ReceiverInputSequence ||
                current.ReceiverInputRevision != ReceiverInputRevision || current.GeometryRevision != GeometryRevision ||
                current.SupportRevision != SupportRevision || current.ContextRevision != ContextRevision || current.NativeClockDomain != NativeClockDomain ||
                current.FeasibilityStatus != FeasibilityStatus || current.RouteXY.Count != _route.Length)
                return ReceiverInputValidation.Fail("Receiver approach receipt was replaced or changed.", out error);
            var route = current.RouteXY;
            for (int i = 0; i < _route.Length; ++i)
                if (BitConverter.DoubleToInt64Bits(route[i]) != BitConverter.DoubleToInt64Bits(_route[i]))
                    return ReceiverInputValidation.Fail("Receiver approach route changed during revalidation.", out error);
            return true;
        }
    }

    public sealed partial class ReceiverInputWriter
    {
        private ReceiverInputFrame _currentApproachFrame;
        internal ReceiverInputFrame CurrentApproachFrame => _currentApproachFrame;

        // Internal until the C3b receiver-local context producer is exposed as
        // its own framework module. This keeps raw native-coordinate context
        // from becoming a Transform-based public convenience API.
        internal bool TryRegisterReceiverApproach(ReceiverApproachContextInput input,
            out ReceiverApproachContextLease lease, out int nativeResult, out string error)
            => Registration.World.TryRegisterReceiverApproach(this, input, out lease, out nativeResult, out error);
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        private readonly Dictionary<ReceiverInputWriter, ReceiverApproachContextLease> _receiverApproaches =
            new Dictionary<ReceiverInputWriter, ReceiverApproachContextLease>();

        internal ReceiverApproachContextLease CurrentReceiverApproach(ReceiverInputWriter writer)
        {
            _receiverApproaches.TryGetValue(writer, out var lease); return lease;
        }
        internal bool TryRegisterReceiverApproach(ReceiverInputWriter writer, ReceiverApproachContextInput input,
            out ReceiverApproachContextLease lease, out int nativeResult, out string error)
        {
            lease = null; nativeResult = -1; error = null;
            if (!CheckReceiverInputWriter(writer, out error) || input == null || writer.CurrentApproachFrame == null ||
                !ReferenceEquals(writer.CurrentApproachFrame.Input, writer.Input) || input.InputEpoch != writer.Input.Epoch ||
                input.InputSequence != writer.CurrentApproachFrame.Sequence || input.RequestedValidUntilNanoseconds > writer.CurrentApproachFrame.ValidUntilNanoseconds ||
                !string.Equals(input.NavigationRoot, writer.Input.RootAnchor, StringComparison.Ordinal))
                return ReceiverInputValidation.Fail("Receiver approach needs this writer's exact current observed navigation-root frame.", out error);
            if (_receiverApproaches.TryGetValue(writer, out var previous) &&
                input.ExpectedRegistrationGeneration != previous.RegistrationGeneration)
                return ReceiverInputValidation.Fail("Receiver approach replacement needs the exact prior native registration generation.", out error);
            if (!_receiverApproaches.ContainsKey(writer) && input.ExpectedRegistrationGeneration != 0)
                return ReceiverInputValidation.Fail("The first receiver approach registration must not cite a prior generation.", out error);
            long session = _session; int generation = _generation; ReceiverInputFrame frame = writer.CurrentApproachFrame;
            if (!SpatialBlendAPI.TryRegisterReceiverApproachContext(session, writer.Registration.NativeSpace, writer.Registration.NativeActor,
                input, out ulong revision, out nativeResult, out error)) return false;
            if (!CheckReceiverInputWriter(writer, out error) || session != _session || generation != _generation ||
                !ReferenceEquals(writer.CurrentApproachFrame, frame))
                return ReceiverInputValidation.Fail("Receiver approach ownership retired during native registration.", out error);
            previous?.Retire();
            lease = new ReceiverApproachContextLease(writer, session, generation, frame, input, revision);
            _receiverApproaches[writer] = lease;
            return true;
        }
        internal void RetireReceiverApproach(ReceiverInputWriter writer)
        {
            if (writer != null && _receiverApproaches.TryGetValue(writer, out var lease)) { lease.Retire(); _receiverApproaches.Remove(writer); }
        }
    }
}
