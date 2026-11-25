// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>
    /// A declared observed actor point.  The numeric identities are native
    /// identities, not Unity object handles, names, transforms, or a person
    /// authentication claim.
    /// </summary>
    public sealed class ObservedCollaboratorPoint
    {
        internal ObservedCollaboratorPoint(string collaboratorId, long nativeActor,
            long nativeActorElement, long nativePoint, string anchorId,
            RichPosition corePosition, double coreYawRadians)
        {
            CollaboratorId = collaboratorId;
            NativeActor = nativeActor;
            NativeActorElement = nativeActorElement;
            NativePoint = nativePoint;
            AnchorId = anchorId;
            CorePosition = corePosition;
            CoreYawRadians = coreYawRadians;
        }

        public string CollaboratorId { get; }
        public long NativeActor { get; }
        public long NativeActorElement { get; }
        public long NativePoint { get; }
        public string AnchorId { get; }
        /// <summary>Core Z-up metres from the native observation batch.</summary>
        public RichPosition CorePosition { get; }
        public double CoreYawRadians { get; }
    }

    /// <summary>
    /// Immutable evidence copied by the C1 native-batch decoder.  Its
    /// constructor is deliberately internal: managed callbacks, realized
    /// poses, source interaction evidence, and app state cannot manufacture a
    /// collaborator batch.
    /// </summary>
    public sealed class ObservedCollaboratorBatchEvidence
    {
        internal ObservedCollaboratorBatchEvidence(string spaceId, long nativeSpace,
            ulong epoch, long frame, ulong clockId, ulong clockEpoch,
            ulong nativeReceiptNanoseconds, ulong validUntilNanoseconds,
            ObservedCollaboratorPoint[] points)
        {
            SpaceId = spaceId;
            NativeSpace = nativeSpace;
            Epoch = epoch;
            Frame = frame;
            NativeClockId = clockId;
            NativeClockEpoch = clockEpoch;
            NativeReceiptNanoseconds = nativeReceiptNanoseconds;
            ValidUntilNanoseconds = validUntilNanoseconds;
            Points = Array.AsReadOnly((ObservedCollaboratorPoint[])points.Clone());
        }

        public string SpaceId { get; }
        public long NativeSpace { get; }
        public ulong Epoch { get; }
        public long Frame { get; }
        public ulong NativeClockId { get; }
        public ulong NativeClockEpoch { get; }
        public ulong NativeReceiptNanoseconds { get; }
        public ulong ValidUntilNanoseconds { get; }
        public IReadOnlyList<ObservedCollaboratorPoint> Points { get; }
    }

    /// <summary>
    /// Explicit authored endpoint for a board support.  It carries no chart,
    /// geometry, transform, name lookup, or automatic correspondence.  A board
    /// is usable only when its exact Affordance registration remains current.
    /// </summary>
    public sealed class SharedBoardSupportEndpoint
    {
        internal readonly SpatialBlendLocalWorld World;
        internal readonly LocalObjectRegistration Registration;
        internal readonly int Generation;

        internal SharedBoardSupportEndpoint(SpatialBlendLocalWorld world,
            LocalObjectRegistration registration, int generation, string boardId)
        {
            World = world;
            Registration = registration;
            Generation = generation;
            BoardId = boardId;
        }

        public string BoardId { get; }
        public string SpaceId => Registration.SpaceId;
        public long NativeSpace => Registration.NativeSpace;
        public long NativeElement => Registration.NativeElement;

        public static bool TryCreate(string boardId, SpatialBlendObject support,
            out SharedBoardSupportEndpoint endpoint, out string error)
        {
            endpoint = null;
            var registration = support == null ? null : support.Registration;
            if (string.IsNullOrWhiteSpace(boardId) || registration == null ||
                registration.World == null || registration.Kind != SpatialBlendObjectKind.Affordance)
                return ObservedCollaboratorBinding.Fail("An explicit board ID and registered Affordance support are required.", out error);
            if (!registration.World.TryCreateSharedBoardSupportEndpoint(boardId, registration, out endpoint, out error))
                return false;
            return endpoint != null;
        }

        public bool TryRevalidate(out string error)
        {
            if (World == null)
                return ObservedCollaboratorBinding.Fail("Board support endpoint has no owning world.", out error);
            return World.TryValidateSharedBoardSupportEndpoint(this, out error);
        }
    }

    /// <summary>
    /// Explicit source/receiver board correspondence.  The existing hierarchy
    /// lease is the sole correspondence authority; equal board labels alone
    /// never create a shared surface.
    /// </summary>
    public sealed class SharedBoardSupportBinding
    {
        private SharedBoardSupportBinding(SharedBoardSupportEndpoint source,
            SharedBoardSupportEndpoint target, InteractionHierarchyLease hierarchy)
        {
            Source = source;
            Target = target;
            Hierarchy = hierarchy;
        }

        public SharedBoardSupportEndpoint Source { get; }
        public SharedBoardSupportEndpoint Target { get; }
        public InteractionHierarchyLease Hierarchy { get; }

        public static bool TryCreate(SharedBoardSupportEndpoint source,
            SharedBoardSupportEndpoint target, InteractionHierarchyLease hierarchy,
            out SharedBoardSupportBinding binding, out string error)
        {
            binding = null;
            if (source == null || target == null || hierarchy == null ||
                !string.Equals(source.BoardId, target.BoardId, StringComparison.Ordinal))
                return ObservedCollaboratorBinding.Fail("Two explicitly authored endpoints with the same board ID and an authored hierarchy are required.", out error);
            if (!source.TryRevalidate(out error) || !target.TryRevalidate(out error) ||
                !hierarchy.TryRevalidate(out error)) return false;
            if (!ReferenceEquals(source.World, target.World) || !ReferenceEquals(source.World, hierarchy.World) ||
                source.NativeSpace != hierarchy.SourceNativeSpace || target.NativeSpace != hierarchy.TargetNativeSpace ||
                !ObservedCollaboratorBinding.IsExactHierarchyPair(hierarchy, source.Registration, target.Registration))
                return ObservedCollaboratorBinding.Fail("Board supports must be an exact registered source/target hierarchy pair.", out error);
            binding = new SharedBoardSupportBinding(source, target, hierarchy);
            return true;
        }

        public bool TryRevalidate(out string error)
        {
            if (Source == null || Target == null || Hierarchy == null)
                return ObservedCollaboratorBinding.Fail("Shared board support is incomplete.", out error);
            if (!Source.TryRevalidate(out error) || !Target.TryRevalidate(out error) ||
                !Hierarchy.TryRevalidate(out error)) return false;
            if (!ObservedCollaboratorBinding.IsExactHierarchyPair(Hierarchy, Source.Registration, Target.Registration))
                return ObservedCollaboratorBinding.Fail("Shared board support is no longer an exact hierarchy pair.", out error);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Read-only, bounded pre-solve input.  It contains two independently
    /// observed points and may optionally carry an explicit board binding.  It
    /// neither subscribes to source input nor publishes realization output.
    /// </summary>
    public sealed class ObservedCollaboratorSolveInputLease : IDisposable
    {
        internal readonly SpatialBlendLocalWorld World;
        internal readonly int Generation;
        internal readonly int OwnerThread;
        private bool _closed;

        internal ObservedCollaboratorSolveInputLease(SpatialBlendLocalWorld world,
            int generation, ObservedCollaboratorBatchEvidence batch,
            ObservedCollaboratorPoint first, ObservedCollaboratorPoint second,
            SharedBoardSupportBinding board)
        {
            World = world;
            Generation = generation;
            OwnerThread = Thread.CurrentThread.ManagedThreadId;
            Batch = batch;
            First = first;
            Second = second;
            Board = board;
        }

        public ObservedCollaboratorBatchEvidence Batch { get; }
        public ObservedCollaboratorPoint First { get; }
        public ObservedCollaboratorPoint Second { get; }
        public SharedBoardSupportBinding Board { get; }
        public bool IsOpen => !_closed && Thread.CurrentThread.ManagedThreadId == OwnerThread;

        public bool TryRevalidate(ulong nativeNowNanoseconds, out string error)
        {
            if (World == null)
                return ObservedCollaboratorBinding.Fail("Observed solve lease has no owning world.", out error);
            return World.TryValidateObservedCollaboratorSolveInput(this, nativeNowNanoseconds, out error);
        }

        public bool TryClose(out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != OwnerThread)
                return ObservedCollaboratorBinding.Fail("Observed solve lease cleanup requires its owner thread.", out error);
            _closed = true;
            return true;
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != OwnerThread)
                throw new InvalidOperationException("Observed solve lease disposal requires its owner thread.");
            TryClose(out _);
        }

        internal bool Closed => _closed;
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        /// <summary>
        /// Issues only a short read-only lease over one complete C1 native batch.
        /// The two points must be distinct collaborator/actor/point identities;
        /// accepting a realized pose, an individual callback, or an assembled
        /// post-admission set is intentionally impossible through this API.
        /// </summary>
        public bool TryIssueObservedCollaboratorSolveInput(
            ObservedCollaboratorBatchEvidence batch, string firstCollaboratorId,
            string firstAnchorId, string secondCollaboratorId, string secondAnchorId,
            ulong nativeNowNanoseconds, SharedBoardSupportBinding board,
            out ObservedCollaboratorSolveInputLease lease, out string error)
        {
            lease = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return ObservedCollaboratorBinding.Fail("Observed solve input requires the owning Unity thread.", out error);
            int generation = _generation;
            if (!TryValidateObservedCollaboratorBatch(batch, nativeNowNanoseconds, out error) ||
                !ObservedCollaboratorBinding.TryFindExactPoint(batch, firstCollaboratorId, firstAnchorId, out var first) ||
                !ObservedCollaboratorBinding.TryFindExactPoint(batch, secondCollaboratorId, secondAnchorId, out var second) ||
                ObservedCollaboratorBinding.SameObservedIdentity(first, second))
                return ObservedCollaboratorBinding.Fail(error ?? "Two distinct, exact observed collaborator points are required.", out error);
            if (board != null && !board.TryRevalidate(out error)) return false;
            if (generation != _generation || !IsInitialized)
                return ObservedCollaboratorBinding.Fail("World lifetime changed while issuing observed solve input.", out error);
            lease = new ObservedCollaboratorSolveInputLease(this, generation, batch, first, second, board);
            return true;
        }

        internal bool TryCreateSharedBoardSupportEndpoint(string boardId,
            LocalObjectRegistration registration, out SharedBoardSupportEndpoint endpoint, out string error)
        {
            endpoint = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || !IsInitialized ||
                !IsLiveEndpoint(registration) || registration.Owner == null ||
                !ReferenceEquals(registration.Owner.Registration, registration) ||
                registration.Kind != SpatialBlendObjectKind.Affordance)
                return ObservedCollaboratorBinding.Fail("Board support registration is stale, foreign, or not an Affordance.", out error);
            endpoint = new SharedBoardSupportEndpoint(this, registration, _generation, boardId);
            error = null;
            return true;
        }

        internal bool TryValidateSharedBoardSupportEndpoint(SharedBoardSupportEndpoint endpoint, out string error)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || endpoint == null ||
                !ReferenceEquals(endpoint.World, this) || endpoint.Generation != _generation || !IsInitialized ||
                !IsLiveEndpoint(endpoint.Registration) || endpoint.Registration.Owner == null ||
                !ReferenceEquals(endpoint.Registration.Owner.Registration, endpoint.Registration) ||
                endpoint.Registration.Kind != SpatialBlendObjectKind.Affordance)
                return ObservedCollaboratorBinding.Fail("Board support endpoint is stale, replaced, foreign, or unregistered.", out error);
            error = null;
            return true;
        }

        internal bool TryValidateObservedCollaboratorSolveInput(
            ObservedCollaboratorSolveInputLease lease, ulong nativeNowNanoseconds, out string error)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || lease == null || lease.Closed ||
                !ReferenceEquals(lease.World, this) || lease.Generation != _generation)
                return ObservedCollaboratorBinding.Fail("Observed solve lease is closed, foreign, or stale.", out error);
            if (!TryValidateObservedCollaboratorBatch(lease.Batch, nativeNowNanoseconds, out error)) return false;
            if (!ObservedCollaboratorBinding.ContainsExactPoint(lease.Batch, lease.First) ||
                !ObservedCollaboratorBinding.ContainsExactPoint(lease.Batch, lease.Second) ||
                ObservedCollaboratorBinding.SameObservedIdentity(lease.First, lease.Second))
                return ObservedCollaboratorBinding.Fail("Observed solve lease no longer has two exact independent points.", out error);
            return lease.Board == null || lease.Board.TryRevalidate(out error);
        }

        private bool TryValidateObservedCollaboratorBatch(ObservedCollaboratorBatchEvidence batch,
            ulong nativeNowNanoseconds, out string error)
        {
            error = null;
            if (!IsInitialized || batch == null || !IsValidId(batch.SpaceId) || batch.NativeSpace <= 0 ||
                !_spaces.TryGetValue(batch.SpaceId, out var localSpace) || localSpace != batch.NativeSpace ||
                batch.Epoch == 0 || batch.Frame < 0 || batch.NativeClockId == 0 || batch.NativeClockEpoch == 0 ||
                batch.NativeReceiptNanoseconds == 0 || batch.ValidUntilNanoseconds <= batch.NativeReceiptNanoseconds ||
                batch.ValidUntilNanoseconds - batch.NativeReceiptNanoseconds > 250000000UL ||
                nativeNowNanoseconds < batch.NativeReceiptNanoseconds || nativeNowNanoseconds > batch.ValidUntilNanoseconds ||
                batch.Points == null || batch.Points.Count < 2 || batch.Points.Count > 64)
                return ObservedCollaboratorBinding.Fail("Native batch space, epoch, frame, clock, bounded lifetime, and complete point set are required.", out error);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var point in batch.Points)
            {
                if (!ObservedCollaboratorBinding.ValidPoint(point) ||
                    !identities.Add(ObservedCollaboratorBinding.IdentityKey(point)))
                    return ObservedCollaboratorBinding.Fail("Native observed batch has malformed or ambiguous collaborator points.", out error);
            }
            return true;
        }
    }

    internal static class ObservedCollaboratorBinding
    {
        internal static bool TryDecodeNativeBatch(string spaceId, long nativeSpace, ulong epoch,
            long frame, ulong clockId, ulong clockEpoch, ulong receiptNanoseconds,
            ulong validUntilNanoseconds, IReadOnlyList<NativeObservedCollaboratorPoint> nativePoints,
            out ObservedCollaboratorBatchEvidence batch, out string error)
        {
            batch = null;
            if (nativePoints == null || nativePoints.Count < 2 || nativePoints.Count > 64)
                return Fail("Native observed batch requires 2..64 complete points.", out error);
            var points = new ObservedCollaboratorPoint[nativePoints.Count];
            try
            {
                for (int i = 0; i < points.Length; ++i)
                {
                    var point = nativePoints[i];
                    points[i] = new ObservedCollaboratorPoint(point.CollaboratorId, point.NativeActor,
                        point.NativeActorElement, point.NativePoint, point.AnchorId,
                        new RichPosition(point.X, point.Y, point.Z), point.YawRadians);
                }
            }
            catch (Exception exception) { return Fail(exception.Message, out error); }
            batch = new ObservedCollaboratorBatchEvidence(spaceId, nativeSpace, epoch, frame,
                clockId, clockEpoch, receiptNanoseconds, validUntilNanoseconds, points);
            error = null;
            return true;
        }

        internal static bool IsExactHierarchyPair(InteractionHierarchyLease hierarchy,
            LocalObjectRegistration source, LocalObjectRegistration target)
        {
            if (hierarchy == null || source == null || target == null ||
                hierarchy.Sources.Length != hierarchy.Targets.Length) return false;
            for (int i = 0; i < hierarchy.Sources.Length; ++i)
                if (ReferenceEquals(hierarchy.Sources[i], source) && ReferenceEquals(hierarchy.Targets[i], target))
                    return true;
            return false;
        }

        internal static bool TryFindExactPoint(ObservedCollaboratorBatchEvidence batch,
            string collaboratorId, string anchorId, out ObservedCollaboratorPoint selected)
        {
            selected = null;
            if (string.IsNullOrWhiteSpace(collaboratorId) || string.IsNullOrWhiteSpace(anchorId)) return false;
            foreach (var point in batch.Points)
                if (string.Equals(point.CollaboratorId, collaboratorId, StringComparison.Ordinal) &&
                    string.Equals(point.AnchorId, anchorId, StringComparison.Ordinal))
                {
                    if (selected != null) return false;
                    selected = point;
                }
            return selected != null;
        }

        internal static bool ContainsExactPoint(ObservedCollaboratorBatchEvidence batch,
            ObservedCollaboratorPoint point)
        {
            foreach (var candidate in batch.Points)
                if (ReferenceEquals(candidate, point)) return true;
            return false;
        }

        internal static bool SameObservedIdentity(ObservedCollaboratorPoint first,
            ObservedCollaboratorPoint second) => first == null || second == null ||
            first.NativeActor == second.NativeActor || first.NativeActorElement == second.NativeActorElement ||
            first.NativePoint == second.NativePoint || string.Equals(first.CollaboratorId, second.CollaboratorId,
                StringComparison.Ordinal);

        internal static bool ValidPoint(ObservedCollaboratorPoint point) => point != null &&
            !string.IsNullOrWhiteSpace(point.CollaboratorId) && !string.IsNullOrWhiteSpace(point.AnchorId) &&
            point.NativeActor > 0 && point.NativeActorElement > 0 && point.NativePoint > 0 &&
            RichKnownDouble.Finite(point.CoreYawRadians);

        internal static string IdentityKey(ObservedCollaboratorPoint point) => point.CollaboratorId + "\n" +
            point.NativeActor + "\n" + point.NativeActorElement + "\n" + point.NativePoint + "\n" + point.AnchorId;

        internal static bool Fail(string message, out string error)
        {
            error = string.IsNullOrWhiteSpace(message) ? "Observed collaborator binding failed." : message;
            return false;
        }
    }

    /// <summary>Internal C1 decoder row.  It is not an app-level input DTO.</summary>
    internal readonly struct NativeObservedCollaboratorPoint
    {
        internal NativeObservedCollaboratorPoint(string collaboratorId, long nativeActor,
            long nativeActorElement, long nativePoint, string anchorId, double x, double y,
            double z, double yawRadians)
        {
            CollaboratorId = collaboratorId;
            NativeActor = nativeActor;
            NativeActorElement = nativeActorElement;
            NativePoint = nativePoint;
            AnchorId = anchorId;
            X = x;
            Y = y;
            Z = z;
            YawRadians = yawRadians;
        }

        internal string CollaboratorId { get; }
        internal long NativeActor { get; }
        internal long NativeActorElement { get; }
        internal long NativePoint { get; }
        internal string AnchorId { get; }
        internal double X { get; }
        internal double Y { get; }
        internal double Z { get; }
        internal double YawRadians { get; }
    }
}
