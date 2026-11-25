// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    public enum SourceInteractionEvidenceStatus { Available = 1, Unavailable = 0, Incompatible = -1, Stale = -2, Busy = -4 }
    public enum SourceInteractionKernel { Spacing = 0, Facing = 1, AffordancePose = 2 }
    // Generic relation lifecycle, NOT a qualified body-to-Sittable contact verdict.
    public enum SourceInteractionPhase { Dormant = 0, Active = 1, Driving = 2, Contact = 3 }

    public sealed class SourceInteractionAnchor
    {
        internal SourceInteractionAnchor(string name, SpatialBlendAPI.SbSourceAnchor row)
        {
            Name = name; Observed = row.observed != 0; IsLocomotionRoot = row.locomotion_root != 0;
            PositionDimension = (int)row.position_dimension;
            CorePosition = new RichPosition(row.position_x, row.position_y, row.position_z);
            CoreYawRadians = row.yaw;
        }
        public string Name { get; }
        public bool Observed { get; }
        public bool IsLocomotionRoot { get; }
        public int PositionDimension { get; }
        /// <summary>Core Z-up metres; undefined as an observation when Observed is false.</summary>
        public RichPosition CorePosition { get; }
        public double CoreYawRadians { get; }
    }

    public sealed class SourceInteractionHierarchyNode
    {
        internal SourceInteractionHierarchyNode(string tag, SpatialBlendAPI.SbSourceNode row)
        {
            NativeElement = row.element; NativeParent = row.parent; SharedId = row.shared;
            Tag = tag; NativeGeometryRole = row.geometry_role;
            CoreWorldTransform = new RichMatrix4x4(row.world_transform_row_major);
        }
        public long NativeElement { get; }
        public long NativeParent { get; }
        /// <summary>Actual shared identity; -1 means unknown. Never a local-ID fallback.</summary>
        public long SharedId { get; }
        public string Tag { get; }
        public uint NativeGeometryRole { get; }
        public RichMatrix4x4 CoreWorldTransform { get; }
    }

    public sealed class SourceInteractionRelation
    {
        internal SourceInteractionRelation(SpatialBlendAPI.SbSourceRelation row, string filter,
            string subRegion, SourceInteractionHierarchyNode[] path, double[] xy)
        {
            BindingOrdinal = row.binding_ordinal; AnchorOrdinal = row.anchor_ordinal;
            Kernel = (SourceInteractionKernel)row.kernel; ExplicitScope = row.explicit_scope;
            TagFilter = filter; Proximity = row.proximity; NormalizedWeight = row.normalized_weight;
            Phase = (SourceInteractionPhase)row.phase;
            PhaseIdentityUnambiguous = row.phase_identity_unambiguous != 0;
            PhaseReferentId = row.phase_referent_id;
            Hierarchy = Array.AsReadOnly((SourceInteractionHierarchyNode[])path.Clone());
            HasProjection = row.has_projection != 0; BoundedPlanarSupport = row.bounded_planar_support != 0;
            CoreProjectedPoint = new RichPosition(row.projected_x, row.projected_y, row.projected_z);
            AnchorSupportDistance = row.anchor_support_distance;
            CoreSupportToWorld = new RichMatrix4x4(row.support_to_world_row_major);
            BoundaryXY = Array.AsReadOnly((double[])xy.Clone()); SubRegion = subRegion;
            SupportAngle = row.support_angle; SupportRadialRatio = row.support_radial_ratio;
        }
        public uint BindingOrdinal { get; }
        public uint AnchorOrdinal { get; }
        public SourceInteractionKernel Kernel { get; }
        public long ExplicitScope { get; }
        public string TagFilter { get; }
        public double Proximity { get; }
        public double NormalizedWeight { get; }
        public SourceInteractionPhase Phase { get; }
        public bool PhaseIdentityUnambiguous { get; }
        /// <summary>Generic phase key; may use a local-ID fallback. Not a shared identity.</summary>
        public long PhaseReferentId { get; }
        /// <summary>Ordered hierarchy root through the exact local referent.</summary>
        public IReadOnlyList<SourceInteractionHierarchyNode> Hierarchy { get; }
        public bool HasProjection { get; }
        public bool BoundedPlanarSupport { get; }
        public RichPosition CoreProjectedPoint { get; }
        public double AnchorSupportDistance { get; }
        /// <summary>Chart (u,v,0,1) to Core Z-up world, retaining native doubles.</summary>
        public RichMatrix4x4 CoreSupportToWorld { get; }
        public IReadOnlyList<double> BoundaryXY { get; }
        public string SubRegion { get; }
        public double SupportAngle { get; }
        public double SupportRadialRatio { get; }
    }

    /// <summary>
    /// Detached exact-frame evidence captured by native atomic admission. Not a
    /// live scene view, calibrated sensor certificate, seat verdict, or receiver
    /// accomplishment. Root/body qualification and hierarchy matching are policy.
    /// Native receipt bounds local age only; upstream sensor age remains unknown.
    /// </summary>
    public sealed class SourceInteractionEvidence
    {
        internal readonly SpatialBlendAPI.SbSourceHeader NativeHeader;
        internal SourceInteractionEvidence(SpatialBlendAPI.SbSourceHeader header,
            SourceInteractionAnchor[] anchors, SourceInteractionRelation[] relations)
        {
            NativeHeader = header;
            Anchors = Array.AsReadOnly((SourceInteractionAnchor[])anchors.Clone());
            Relations = Array.AsReadOnly((SourceInteractionRelation[])relations.Clone());
        }
        public long NativeSpace => NativeHeader.space;
        public long NativeActor => NativeHeader.actor;
        public long NativeActorElement => NativeHeader.actor_element;
        public long ActorSharedId => NativeHeader.actor_shared;
        public ulong Epoch => NativeHeader.epoch;
        public long Sequence => NativeHeader.sequence;
        public double SourceTimeSeconds => NativeHeader.source_time_seconds;
        public long NativeReceiptNanoseconds => NativeHeader.receipt_monotonic_ns;
        public ulong NativeClockId => NativeHeader.monotonic_clock_id;
        public ulong NativeClockEpoch => NativeHeader.monotonic_clock_epoch;
        public ulong TreeRevision => NativeHeader.tree_revision;
        public ulong SupportRevision => NativeHeader.support_revision;
        public IReadOnlyList<SourceInteractionAnchor> Anchors { get; }
        public IReadOnlyList<SourceInteractionRelation> Relations { get; }
    }

    /// <summary>
    /// Successful input receipt plus optional evidence for that SAME native frame.
    /// Controls have no evidence or sample clocks. Available with zero relations
    /// is valid, not contact. Retained values are historical; revalidate through
    /// the exact issuing lease before using them as a current source cue.
    /// </summary>
    public sealed class CommittedSourceInteractionObservation
    {
        internal CommittedSourceInteractionObservation(CommittedMotionObservation input,
            SourceInteractionEvidence evidence, SourceInteractionEvidenceStatus status, long revision)
        { Input = input; Evidence = evidence; Status = status; Revision = revision; }
        internal long Revision { get; }
        public CommittedMotionObservation Input { get; }
        public SourceInteractionEvidence Evidence { get; }
        public SourceInteractionEvidenceStatus Status { get; }
    }
}
