// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using SpatialBlend.Internal;

namespace SpatialBlend
{
    public sealed partial class SpatialBlendLocalWorld
    {
        private readonly List<InteractionHierarchyLease> _interactionHierarchies = new List<InteractionHierarchyLease>();
        private sealed class HierarchyElementRetirement
        {
            internal long NativeSpace, NativeElement;
            internal int Generation;
        }
        private readonly List<HierarchyElementRetirement> _hierarchyRemovalRetries = new List<HierarchyElementRetirement>();

        /// <summary>
        /// Explicit Space correspondence plus 1..127 authored node pairs. The
        /// projected ancestor relation must match in both spaces; intermediate
        /// unmapped nodes may differ. This declares correspondence, not Sittable
        /// semantics, contact, calibrated motion, or a receiver achievement.
        /// </summary>
        public bool TryRegisterInteractionHierarchy(string sourceSpaceId, string targetSpaceId,
            IReadOnlyList<InteractionHierarchyPair> pairs, out InteractionHierarchyLease lease, out string error)
        {
            lease = null; error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return HierarchyFail("Hierarchy registration requires the owning Unity thread.", out error);
            if (!IsInitialized || _shuttingDown || !IsValidId(sourceSpaceId) || !IsValidId(targetSpaceId) ||
                sourceSpaceId == targetSpaceId || pairs == null)
                return HierarchyFail("Two distinct live spaces and explicit hierarchy pairs are required.", out error);
            long session = _session;
            int generation = _generation;
            try
            {
                // User collections may throw or reenter. Freeze before resolving
                // registrations, then fence the original world lifetime again.
                int count = pairs.Count;
                if (count < 1 || count > 127) return HierarchyFail("Hierarchy supports 1..127 authored pairs plus Space roots.", out error);
                var copy = new InteractionHierarchyPair[count];
                for (int i = 0; i < count; ++i) copy[i] = pairs[i];
                if (pairs.Count != count || _session != session || _generation != generation || !IsInitialized)
                    return HierarchyFail("Hierarchy input changed the world lifetime.", out error);
                DrainInteractionHierarchyCleanup();
                if (!_spaces.TryGetValue(sourceSpaceId, out long sourceSpace) ||
                    !_spaces.TryGetValue(targetSpaceId, out long targetSpace) ||
                    !_nativeSpaceRoots.TryGetValue(sourceSpaceId, out var sourceRoot) ||
                    !_nativeSpaceRoots.TryGetValue(targetSpaceId, out var targetRoot) ||
                    HasHierarchyRemovalRetry(sourceSpace) || HasHierarchyRemovalRetry(targetSpace))
                    return HierarchyFail("Both spaces must exist and prior hierarchy cleanup must finish.", out error);
                var sources = new LocalObjectRegistration[count];
                var targets = new LocalObjectRegistration[count];
                var uniqueSource = new HashSet<LocalObjectRegistration>();
                var uniqueTarget = new HashSet<LocalObjectRegistration>();
                var batch = new SbSupportCorrespondence[count + 1];
                batch[0] = new SbSupportCorrespondence(sourceSpace, sourceRoot.Element, targetSpace, targetRoot.Element);
                for (int i = 0; i < count; ++i)
                {
                    var a = copy[i].Source == null ? null : copy[i].Source.Registration;
                    var b = copy[i].Target == null ? null : copy[i].Target.Registration;
                    if (!IsExactHierarchyEndpoint(a) || !IsExactHierarchyEndpoint(b) || a.NativeSpace != sourceSpace ||
                        b.NativeSpace != targetSpace || a.Kind != b.Kind || !uniqueSource.Add(a) || !uniqueTarget.Add(b))
                        return HierarchyFail("Hierarchy pairs require unique, exact live endpoints of matching authored kind in the selected spaces.", out error);
                    sources[i] = a; targets[i] = b;
                    batch[i + 1] = new SbSupportCorrespondence(sourceSpace, a.NativeElement, targetSpace, b.NativeElement);
                }
                // Preserve both ancestor and sibling relations after projection;
                // equal kinds/depths alone cannot detect crossing branches.
                for (int i = 0; i < count; ++i)
                    for (int j = 0; j < count; ++j)
                        if (IsHierarchyAncestor(sources[i], sources[j]) != IsHierarchyAncestor(targets[i], targets[j]))
                            return HierarchyFail("Hierarchy pairs cross ancestor or sibling relationships.", out error);
                if (!SpatialBlendAPI.TryGetOwnedCorrespondenceFeature(out error)) return false;
                long handle = SpatialBlendAPI.TryRegisterOwnedCorrespondences(session, batch);
                if (handle <= 0) return HierarchyFail("Native hierarchy registration is busy, conflicting, or unavailable; nothing was installed.", out error);
                var created = new InteractionHierarchyLease(this, session, generation, _mainThread, handle,
                    sourceSpaceId, targetSpaceId, sourceSpace, targetSpace, sourceRoot.Element, targetRoot.Element, sources, targets);
                _interactionHierarchies.Add(created);
                if (!TryValidateInteractionHierarchy(created, out error))
                { TryCloseInteractionHierarchy(created, out _); return false; }
                lease = created;
                return true;
            }
            catch (Exception e) { return HierarchyFail(e.Message, out error); }
        }

        internal bool TryValidateInteractionHierarchy(InteractionHierarchyLease lease, out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return HierarchyFail("Hierarchy validation requires the owning Unity thread.", out error);
            if (lease == null || !ReferenceEquals(lease.World, this) || lease.Closed || lease.NativeHandle <= 0 ||
                !IsInitialized || lease.Session != _session || lease.Generation != _generation || !_interactionHierarchies.Contains(lease))
                return HierarchyFail("Hierarchy lease is closed or belongs to a retired world lifetime.", out error);
            for (int i = 0; i < lease.Sources.Length; ++i)
                if (!IsExactHierarchyEndpoint(lease.Sources[i]) || !IsExactHierarchyEndpoint(lease.Targets[i]))
                    return HierarchyFail("Hierarchy endpoint registration was replaced or retired.", out error);
            try
            {
                int result = SpatialBlendAPI.TryValidateOwnedCorrespondences(_session, lease.NativeHandle);
                return result == 1 || HierarchyFail(result == -4 ? "Native hierarchy is busy." : "Native hierarchy is no longer exact and unambiguous.", out error);
            }
            catch (Exception e) { return HierarchyFail(e.Message, out error); }
        }

        internal bool TryMapInteractionSourcePath(InteractionHierarchyLease lease, SourceInteractionEvidence evidence,
            SourceInteractionRelation relation, out InteractionHierarchyPath path, out string error)
        {
            path = null;
            if (!TryValidateInteractionHierarchy(lease, out error)) return false;
            if (evidence == null || relation == null || evidence.NativeSpace != lease.SourceNativeSpace)
                return HierarchyFail("Source evidence must belong to this hierarchy's source space.", out error);
            bool exactRow = false;
            foreach (var row in evidence.Relations) if (ReferenceEquals(row, relation)) exactRow = true;
            if (!exactRow || relation.Hierarchy.Count == 0 || relation.Hierarchy[0].NativeElement != lease.SourceNativeRoot)
                return HierarchyFail("An exact relation row and complete native-root path are required.", out error);
            long leafId = relation.Hierarchy[relation.Hierarchy.Count - 1].NativeElement;
            if (leafId == lease.SourceNativeRoot && relation.Hierarchy.Count == 1 && relation.Hierarchy[0].NativeParent == -1)
            {
                if (!TryValidateInteractionHierarchy(lease, out error)) return false;
                path = new InteractionHierarchyPath(lease, evidence, relation, new List<InteractionHierarchyMappedNode> {
                    new InteractionHierarchyMappedNode(lease.SourceNativeRoot, lease.TargetNativeRoot, InteractionHierarchyNodeKind.Space) });
                return true;
            }
            int leafIndex = Array.FindIndex(lease.Sources, r => r.NativeElement == leafId);
            if (leafIndex < 0) return HierarchyFail("Source referent has no explicit counterpart in this map.", out error);
            var ancestors = new List<LocalObjectRegistration>();
            for (var current = lease.Sources[leafIndex]; current != null; current = current.Parent) ancestors.Add(current);
            ancestors.Reverse();
            if (relation.Hierarchy.Count != ancestors.Count + 1 || relation.Hierarchy[0].NativeParent != -1)
                return HierarchyFail("Source path no longer matches the registered hierarchy.", out error);
            var mapped = new List<InteractionHierarchyMappedNode> {
                new InteractionHierarchyMappedNode(lease.SourceNativeRoot, lease.TargetNativeRoot, InteractionHierarchyNodeKind.Space)
            };
            for (int i = 0; i < ancestors.Count; ++i)
            {
                var row = relation.Hierarchy[i + 1]; var endpoint = ancestors[i];
                long parent = i == 0 ? lease.SourceNativeRoot : ancestors[i - 1].NativeElement;
                if (!IsExactHierarchyEndpoint(endpoint) || row.NativeElement != endpoint.NativeElement || row.NativeParent != parent)
                    return HierarchyFail("Source evidence contains a different hierarchy lifetime or parent path.", out error);
                int pair = Array.IndexOf(lease.Sources, endpoint);
                if (pair >= 0) mapped.Add(new InteractionHierarchyMappedNode(endpoint.NativeElement,
                    lease.Targets[pair].NativeElement, (InteractionHierarchyNodeKind)((int)endpoint.Kind + 1)));
            }
            if (!TryValidateInteractionHierarchy(lease, out error)) return false;
            path = new InteractionHierarchyPath(lease, evidence, relation, mapped);
            return true;
        }

        internal bool TryCloseInteractionHierarchy(InteractionHierarchyLease lease, out string error)
        {
            error = null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                return HierarchyFail("Hierarchy cleanup requires the owning Unity thread.", out error);
            if (lease == null || !ReferenceEquals(lease.World, this)) return HierarchyFail("Foreign hierarchy lease.", out error);
            lease.Closed = true; // Revoke managed use before any potentially Busy cleanup.
            if (lease.NativeHandle <= 0) return true;
            if (lease.Session != _session || lease.Generation != _generation || _session <= 0)
            { ForgetInteractionHierarchy(lease); return true; }
            try
            {
                int result = SpatialBlendAPI.TryDisposeOwnedCorrespondences(lease.Session, lease.NativeHandle);
                if (result != 1) return HierarchyFail("Native hierarchy cleanup is pending; retry on the owning thread.", out error);
                ForgetInteractionHierarchy(lease);
                return true;
            }
            catch (Exception e) { return HierarchyFail(e.Message, out error); }
        }

        private void ForgetInteractionHierarchy(InteractionHierarchyLease lease)
        { lease.Closed = true; lease.NativeHandle = 0; _interactionHierarchies.Remove(lease); }
        private bool IsExactHierarchyEndpoint(LocalObjectRegistration endpoint)
            => IsLiveEndpoint(endpoint) && endpoint.Owner != null && ReferenceEquals(endpoint.Owner.Registration, endpoint);
        private static bool IsHierarchyAncestor(LocalObjectRegistration ancestor, LocalObjectRegistration child)
        {
            for (var current = child.Parent; current != null; current = current.Parent)
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }
        private static bool HierarchyFail(string message, out string error) { error = message; return false; }
        private bool HasHierarchyRemovalRetry(long space)
            => _hierarchyRemovalRetries.Exists(r => r.Generation == _generation && r.NativeSpace == space);
        private void QueueHierarchyRemoval(long space, long element, int generation)
        {
            if (!_hierarchyRemovalRetries.Exists(r => r.Generation == generation && r.NativeSpace == space && r.NativeElement == element))
                _hierarchyRemovalRetries.Add(new HierarchyElementRetirement { NativeSpace = space, NativeElement = element, Generation = generation });
        }
        private bool HasPendingHierarchyInSpace(long space)
            => _interactionHierarchies.Exists(l => l.NativeHandle > 0 &&
                (l.SourceNativeSpace == space || l.TargetNativeSpace == space));
        private void RetireInteractionHierarchiesInSpace(long space)
        {
            foreach (var lease in _interactionHierarchies.ToArray())
                if (lease.SourceNativeSpace == space || lease.TargetNativeSpace == space) lease.Dispose();
        }
        private void DrainInteractionHierarchyCleanup()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            foreach (var lease in _interactionHierarchies.ToArray())
                if (lease.Closed) TryCloseInteractionHierarchy(lease, out _);
            foreach (var registration in _hierarchyRemovalRetries.ToArray())
            {
                if (registration.Generation != _generation || _session <= 0)
                { _hierarchyRemovalRetries.Remove(registration); continue; }
                if (HasPendingHierarchyInSpace(registration.NativeSpace)) continue;
                try
                {
                    if (SpatialBlendAPI.RemoveLocalElement(registration.NativeSpace, registration.NativeElement))
                        _hierarchyRemovalRetries.Remove(registration);
                }
                catch (Exception e) { LastError = e.Message; }
            }
        }
    }
}
