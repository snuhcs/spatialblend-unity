// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    /// <summary>
    /// Detached numeric geometry as of a native query, NOT a currentness or
    /// contact certificate. Chart (u,v,0,1) maps through PlaneToWorld into Core
    /// Z-up metres. Ordered doubles are retained without a Unity float roundtrip.
    /// </summary>
    public sealed class PlanarSupportGeometry
    {
        private readonly ReadOnlyCollection<double> _boundary;
        internal PlanarSupportGeometry(SpatialBlendAPI.SbPlanarSupportHeader header, double[] xy)
        {
            NativeSpace = header.space; NativeElement = header.element;
            TreeRevision = header.tree_revision; SupportRevision = header.support_revision;
            DefinitionKind = header.definition_kind; NativeGeometryRole = header.geometry_role;
            PlaneToWorld = new RichMatrix4x4(header.plane_to_world_row_major);
            _boundary = Array.AsReadOnly((double[])xy.Clone());
        }
        public long NativeSpace { get; }
        public long NativeElement { get; }
        public ulong TreeRevision { get; }
        public ulong SupportRevision { get; }
        public uint DefinitionKind { get; } // 1=Surface, 2=Navigable.
        public uint NativeGeometryRole { get; }
        public RichMatrix4x4 PlaneToWorld { get; }
        public IReadOnlyList<double> BoundaryXY => _boundary;
        public int PointCount => _boundary.Count / 2;
    }

    /// <summary>
    /// One-use owner-bound geometry read for one exact rich contributor. Consume
    /// on its owner thread. Revalidate the original checkpoint again immediately
    /// before actuation; consuming geometry admits no body DOFs or contact.
    /// </summary>
    public sealed class OwnedAffordanceSupportRead
    {
        internal readonly SpatialBlendLocalWorld World;
        internal readonly LocalObjectRegistration Registration;
        internal readonly long Session;
        internal readonly int Generation;
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly PlanarSupportGeometry _geometry;
        private bool _consumed;
        internal OwnedAffordanceSupportRead(SpatialBlendLocalWorld world, LocalObjectRegistration registration,
            long session, int generation, ReceiverBodyRealizationCheckpoint checkpoint,
            RichRealizationContributor contributor, PlanarSupportGeometry geometry)
        {
            World = world; Registration = registration; Session = session; Generation = generation;
            Checkpoint = checkpoint; Contributor = contributor; _geometry = geometry;
        }
        public ReceiverBodyRealizationCheckpoint Checkpoint { get; }
        public RichRealizationContributor Contributor { get; }
        // Internal only: a receipt issuer may retain exact revision/identity
        // after consuming this one-use read, but cannot expose or mutate the
        // numeric support geometry as a general scene query.
        internal PlanarSupportGeometry Geometry => _geometry;
        public bool TryConsume(out PlanarSupportGeometry geometry, out string error)
        {
            geometry = null;
            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
                return ReceiverInputValidation.Fail("Support consumption requires its owner thread.", out error);
            if (_consumed) return ReceiverInputValidation.Fail("Support read has already been consumed or retired.", out error);
            _consumed = true;
            if (World == null) return ReceiverInputValidation.Fail("Support read's world retired.", out error);
            if (!World.TryValidateSupportRead(this, out error)) return false;
            geometry = _geometry; error = null; return true;
        }
    }

    public sealed partial class SpatialBlendLocalWorld
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<OwnedAffordanceSupportRead, object>
            _issuedSupportReads = new System.Runtime.CompilerServices.ConditionalWeakTable<OwnedAffordanceSupportRead, object>();
        /// <summary>
        /// Join an explicitly supplied local affordance object to the exact
        /// native contributor. SupportIdentifier is a mode label, NOT object
        /// identity. Named subregions/legacy fallback are intentionally unsupported.
        /// Does not read the registration-time managed polygon cache.
        /// </summary>
        public bool TryReadAffordanceSupport(ReceiverBodyRealizationCheckpoint checkpoint,
            RichRealizationContributor contributor, SpatialBlendObject supportObject,
            out OwnedAffordanceSupportRead read, out string error)
        {
            read = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return ReceiverInputValidation.Fail("Support read requires the Unity owner thread.", out error);
            if (supportObject == null || checkpoint == null || contributor == null)
                return ReceiverInputValidation.Fail("Explicit checkpoint, contributor and local support object are required.", out error);
            LocalObjectRegistration registration = supportObject.Registration;
            long session = _session;
            int generation = _generation;
            if (!CheckSupportJoin(registration, session, generation, checkpoint, contributor, out error)) return false;
            var h = checkpoint.Snapshot.Header;
            if (!SpatialBlendAPI.TryCopyPlanarSupport(session, registration.NativeSpace, registration.NativeElement,
                    h.ReceiverTreeRevision.Value, h.SupportRevision.Value, out var geometry, out error)) return false;
            if ((geometry.DefinitionKind == 1 && geometry.NativeGeometryRole != 4) ||
                (geometry.DefinitionKind == 2 && geometry.NativeGeometryRole != 1) ||
                !SpatialBlendAPI.SamePlanarMatrix(geometry.PlaneToWorld.Values, contributor.SupportPlaneToWorld.Values))
                return ReceiverInputValidation.Fail("Queried support role/plane does not exactly match the frozen whole-support contributor.", out error);
            if (!CheckSupportJoin(registration, session, generation, checkpoint, contributor, out error)) return false;
            read = new OwnedAffordanceSupportRead(this, registration, session, generation, checkpoint, contributor, geometry);
            _issuedSupportReads.Add(read, this);
            return true;
        }

        internal bool TryValidateSupportRead(OwnedAffordanceSupportRead read, out string error)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return ReceiverInputValidation.Fail("Support validation requires the Unity owner thread.", out error);
            if (read == null || !ReferenceEquals(read.World, this) || !_issuedSupportReads.TryGetValue(read, out _))
                return ReceiverInputValidation.Fail("Support read belongs to another owner.", out error);
            return CheckSupportJoin(read.Registration, read.Session, read.Generation, read.Checkpoint, read.Contributor, out error);
        }

        private bool CheckSupportJoin(LocalObjectRegistration registration, long session, int generation,
            ReceiverBodyRealizationCheckpoint checkpoint, RichRealizationContributor contributor, out string error)
        {
            error = null;
            if (!IsInitialized || session != _session || generation != _generation || !IsLiveEndpoint(registration) ||
                registration.Owner == null || !ReferenceEquals(registration.Owner.Registration, registration) ||
                registration.Kind != SpatialBlendObjectKind.Affordance ||
                !_registrations.TryGetValue((registration.SpaceId, registration.ObjectId), out var current) ||
                !ReferenceEquals(current, registration))
                return ReceiverInputValidation.Fail("Support registration or native world lifetime is stale or is not an affordance.", out error);
            RichRealizationHeader h = checkpoint.Snapshot.Header;
            if (h.ReceiverSpaceId != registration.NativeSpace || !h.ReceiverTreeRevision.Known || h.ReceiverTreeRevision.Value == 0 ||
                !h.SupportRevision.Known || h.SupportRevision.Value == 0 ||
                !contributor.ResolvedLocalElement.Known || contributor.ResolvedLocalElement.Value != registration.NativeElement ||
                contributor.Disposition != RichContributorDisposition.Included || !contributor.HasMappedSupport ||
                !contributor.SupportPlaneKnown || contributor.GroundingMode != RichGroundingMode.WholePlanarBoundary ||
                !string.Equals(contributor.SupportIdentifier, "whole-planar-boundary", StringComparison.Ordinal))
                return ReceiverInputValidation.Fail("Only an included exact whole-planar contributor with known local scene identity can join geometry.", out error);
            bool found = false;
            foreach (RichRealizationChannel channel in checkpoint.Snapshot.Channels)
                if (channel.Family == RichRelationFamily.AffordancePose)
                    foreach (RichRealizationContributor candidate in channel.Contributors)
                        if (ReferenceEquals(candidate, contributor)) found = true;
            if (!found) return ReceiverInputValidation.Fail("Support contributor is not the exact object in this checkpoint's affordance channel.", out error);
            return checkpoint.TryRevalidate(out _, out error);
        }
    }
}
