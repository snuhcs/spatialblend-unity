// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace SpatialBlend
{
    /// <summary>An explicit authored correspondence, never inferred from names or contact.</summary>
    public readonly struct InteractionHierarchyPair
    {
        public InteractionHierarchyPair(SpatialBlendObject source, SpatialBlendObject target)
        { Source = source; Target = target; }
        public SpatialBlendObject Source { get; }
        public SpatialBlendObject Target { get; }
    }

    public enum InteractionHierarchyNodeKind { Space, Region, Object, Affordance }

    public sealed class InteractionHierarchyMappedNode
    {
        internal InteractionHierarchyMappedNode(long source, long target, InteractionHierarchyNodeKind kind)
        { SourceNativeElement = source; TargetNativeElement = target; Kind = kind; }
        public long SourceNativeElement { get; }
        public long TargetNativeElement { get; }
        public InteractionHierarchyNodeKind Kind { get; }
    }

    /// <summary>
    /// Authored projection of one exact source path into a receiver hierarchy.
    /// Unpaired intermediate nodes stay in SourceRelation.Hierarchy. Not a live
    /// source sample, receiver checkpoint, contact verdict, or actuation permit.
    /// </summary>
    public sealed class InteractionHierarchyPath
    {
        internal InteractionHierarchyPath(InteractionHierarchyLease lease, SourceInteractionEvidence evidence,
            SourceInteractionRelation relation, List<InteractionHierarchyMappedNode> nodes)
        {
            Lease = lease; SourceEvidence = evidence; SourceRelation = relation;
            Nodes = Array.AsReadOnly(nodes.ToArray());
        }
        public InteractionHierarchyLease Lease { get; }
        public SourceInteractionEvidence SourceEvidence { get; }
        public SourceInteractionRelation SourceRelation { get; }
        public IReadOnlyList<InteractionHierarchyMappedNode> Nodes { get; }
    }

    /// <summary>
    /// Owns a complete native batch, including the two explicitly selected Space
    /// roots. Ancestor order (not equal hierarchy depth) is preserved. Validate
    /// before each use; an open managed handle alone is not current evidence.
    /// Retiring any object in either mapped space closes this map because Core
    /// protects descendants of linked roots. Busy cleanup remains world-owned.
    /// </summary>
    public sealed class InteractionHierarchyLease : IDisposable
    {
        internal readonly SpatialBlendLocalWorld World;
        internal readonly long Session;
        internal readonly int Generation;
        internal readonly int OwnerThread;
        internal readonly LocalObjectRegistration[] Sources, Targets;
        internal long NativeHandle;
        internal bool Closed;

        internal InteractionHierarchyLease(SpatialBlendLocalWorld world, long session, int generation,
            int thread, long handle, string sourceSpaceId, string targetSpaceId, long sourceSpace,
            long targetSpace, long sourceRoot, long targetRoot,
            LocalObjectRegistration[] sources, LocalObjectRegistration[] targets)
        {
            World = world; Session = session; Generation = generation; OwnerThread = thread;
            NativeHandle = handle; Sources = sources; Targets = targets;
            SourceSpaceId = sourceSpaceId; TargetSpaceId = targetSpaceId;
            SourceNativeSpace = sourceSpace; TargetNativeSpace = targetSpace;
            SourceNativeRoot = sourceRoot; TargetNativeRoot = targetRoot;
        }
        public string SourceSpaceId { get; }
        public string TargetSpaceId { get; }
        public long SourceNativeSpace { get; }
        public long TargetNativeSpace { get; }
        internal long SourceNativeRoot { get; }
        internal long TargetNativeRoot { get; }
        public bool IsOpen => Thread.CurrentThread.ManagedThreadId == OwnerThread && !Closed;
        public bool TryRevalidate(out string error) => World.TryValidateInteractionHierarchy(this, out error);
        public bool TryMapSourcePath(SourceInteractionEvidence evidence, SourceInteractionRelation relation,
            out InteractionHierarchyPath path, out string error)
            => World.TryMapInteractionSourcePath(this, evidence, relation, out path, out error);
        public bool TryClose(out string error) => World.TryCloseInteractionHierarchy(this, out error);
        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != OwnerThread)
                throw new InvalidOperationException("Hierarchy disposal requires the owning Unity thread.");
            TryClose(out _);
        }
    }
}
