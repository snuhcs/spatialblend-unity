// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using UnityEngine;

namespace SpatialBlend
{
    /// <summary>
    /// Immutable, native-free declaration of one authored C1 source point.
    /// A coordinator may use it only after it has explicitly selected the C1
    /// admission path. It is deliberately not a native lease or an actor
    /// observation epoch, so adding this component cannot alter a legacy
    /// source writer, body target, or network/session mode.
    /// </summary>
    internal sealed class ObservedSourceAnchorPointDeclaration
    {
        internal ObservedSourceAnchorPointDeclaration(LocalObjectRegistration point,
            LocalActorRegistration actor, string anchorId, long maximumAgeNanoseconds)
        {
            Point = point;
            Actor = actor;
            AnchorId = anchorId;
            MaximumAgeNanoseconds = maximumAgeNanoseconds;
            Generation = point.Generation;
        }

        internal LocalObjectRegistration Point { get; }
        internal LocalActorRegistration Actor { get; }
        internal string AnchorId { get; }
        internal long MaximumAgeNanoseconds { get; }
        internal int Generation { get; }
    }

    /// <summary>
    /// Explicit Unity Object declaration of one geometry-free C1 source point.
    /// It is passive: no Start, enable, or update callback opens a native lease
    /// or advances an actor epoch. The future coherent admission coordinator
    /// owns those transient native resources in one explicit mode transition.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ObservedSourceAnchorPoint : MonoBehaviour
    {
        [SerializeField] private SpatialBlendObject _point;
        [SerializeField] private SpatialBlendActor _source;
        [SerializeField] private string _anchorId;
        [SerializeField] private long _maximumAgeNanoseconds = 250000000L;

        private int _mainThread;
        public string LastError { get; private set; }

        private void Awake() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        public void Configure(SpatialBlendObject point, SpatialBlendActor source, string anchorId,
            long maximumAgeNanoseconds = 250000000L)
        {
            if (!OnOwnerThread())
                throw new InvalidOperationException(
                    "Observed source-anchor point configuration requires its owning Unity thread.");
            if (!TryTerminateCurrentSetup(out string error))
                throw new InvalidOperationException(error ??
                    "The current C1 source setup could not be ended before reconfiguration.");
            _point = point;
            _source = source;
            _anchorId = anchorId;
            _maximumAgeNanoseconds = maximumAgeNanoseconds;
            LastError = null;
        }

        // Disabling/destroying the declaration is an explicit framework
        // lifetime boundary.  The component still never creates a lease; it
        // only asks its owning world to truthfully retire/release an existing
        // setup that selected this point earlier.
        private void OnDisable() => TerminateCurrentSetupOnLifecycleBoundary();
        private void OnDestroy() => TerminateCurrentSetupOnLifecycleBoundary();

        /// <summary>
        /// Returns only current authored identities. This method cannot begin
        /// an actor epoch, acquire a point lease, publish a source frame, or
        /// select a transport/receiver path.
        /// </summary>
        internal bool TryGetCurrentDeclaration(out ObservedSourceAnchorPointDeclaration declaration,
            out string error)
        {
            declaration = null;
            error = null;
            if (!OnOwnerThread())
                return Fail("Observed source-anchor point access requires its owning Unity thread.", out error);
            LocalObjectRegistration point = _point == null ? null : _point.Registration;
            LocalActorRegistration actor = _source == null ? null : _source.Registration;
            SpatialBlendLocalWorld world = point == null ? null : point.World;
            if (world == null || !world.TryValidateObservedSourceAnchorPointDeclaration(
                    point, actor, _point, _source, _anchorId, _maximumAgeNanoseconds, out error))
                return Fail(error ?? "Observed source-anchor point declaration is not current.", out error);
            declaration = new ObservedSourceAnchorPointDeclaration(point, actor, _anchorId,
                _maximumAgeNanoseconds);
            LastError = null;
            return true;
        }

        /// <summary>Fences a declaration captured by a future coordinator before it can acquire a native lease.</summary>
        internal bool TryRevalidateDeclaration(ObservedSourceAnchorPointDeclaration declaration,
            out string error)
        {
            error = null;
            if (!OnOwnerThread() || declaration == null || _point == null || _source == null ||
                !ReferenceEquals(declaration.Point.Owner, _point) ||
                !ReferenceEquals(declaration.Actor.Owner, _source) ||
                !string.Equals(declaration.AnchorId, _anchorId, StringComparison.Ordinal) ||
                declaration.MaximumAgeNanoseconds != _maximumAgeNanoseconds)
                return Fail("Observed source-anchor point declaration is no longer current.", out error);
            SpatialBlendLocalWorld world = declaration.Point.World;
            if (world == null || !world.TryValidateObservedSourceAnchorPointDeclaration(
                    declaration.Point, declaration.Actor, _point, _source,
                    declaration.AnchorId, declaration.MaximumAgeNanoseconds, out error))
                return Fail(error ?? "Observed source-anchor point declaration is no longer current.", out error);
            return true;
        }

        private bool OnOwnerThread() => _mainThread != 0 &&
            Thread.CurrentThread.ManagedThreadId == _mainThread;

        private bool TryTerminateCurrentSetup(out string error)
        {
            error = null;
            LocalObjectRegistration point = _point == null ? null : _point.Registration;
            SpatialBlendLocalWorld world = point == null ? null : point.World;
            return world == null || world.TryTerminateObservedSourceSetupForPoint(point, out error);
        }

        private void TerminateCurrentSetupOnLifecycleBoundary()
        {
            if (!OnOwnerThread()) return;
            if (!TryTerminateCurrentSetup(out string error)) LastError = error;
        }

        private bool Fail(string message, out string error)
        {
            error = LastError = message;
            return false;
        }
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        // Mirrors C1's published maximum before any coordinator can select an
        // observation epoch. Keeping this declaration-only check side-effect
        // free prevents malformed scene authoring from resetting live ingress.
        internal const long ObservedSourceAnchorPointMaximumAgeNanoseconds = 1000000000L;

        internal bool TryValidateObservedSourceAnchorPointDeclaration(
            LocalObjectRegistration point, LocalActorRegistration actor,
            SpatialBlendObject pointObject, SpatialBlendActor source, string anchorId,
            long maximumAgeNanoseconds, out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || _shuttingDown || !IsInitialized ||
                point == null || actor == null || pointObject == null || source == null ||
                point.Generation != _generation || actor.Generation != _generation ||
                !IsLiveEndpoint(point) || !actor.IsLive || !ReferenceEquals(actor.World, this) ||
                !ReferenceEquals(point.Owner, pointObject) ||
                !ReferenceEquals(pointObject.Registration, point) ||
                !ReferenceEquals(actor.Owner, source) || !ReferenceEquals(source.Registration, actor) ||
                !IsDeclaredSourceAnchor(source, anchorId) ||
                point.Kind != SpatialBlendObjectKind.Object || pointObject.ColliderGeometryEnabled ||
                point.NativeSpace != actor.NativeSpace || maximumAgeNanoseconds <= 0 ||
                maximumAgeNanoseconds > ObservedSourceAnchorPointMaximumAgeNanoseconds)
                return Fail("Observed source point requires a geometry-free Object and a current source actor/anchor in one local space.", out error);

            // Core refuses an observed point with native children, or any point
            // below an actor-owned element. Establish both conditions locally
            // before the coordinator can mutate a source observation epoch.
            foreach (LocalObjectRegistration candidate in _registrations.Values)
                if (candidate.IsLive && ReferenceEquals(candidate.Parent, point))
                    return Fail("Observed source point cannot have registered child elements.", out error);
            for (LocalObjectRegistration current = point; current != null; current = current.Parent)
                if (_actorsByOwnerObject.ContainsKey(current))
                    return Fail("Observed source point cannot be owned by or descend from an actor object.", out error);
            return true;
        }

        private static bool IsDeclaredSourceAnchor(SpatialBlendActor source, string anchorId) =>
            source != null && source.IsRegistered && source.ActorId != null &&
            source.Registration != null && source.Registration.Owner == source &&
            source.IsOwnerThread && source.IsDeclaredSourceAnchorForC1(anchorId);
    }
}
