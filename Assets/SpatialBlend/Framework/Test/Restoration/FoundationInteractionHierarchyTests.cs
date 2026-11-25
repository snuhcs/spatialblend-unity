// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationInteractionHierarchy")]
    public sealed class FoundationInteractionHierarchyTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;
        private SpatialBlendObject _aRegion, _aObject, _aSeat, _bRegion, _bObject, _bSeat;
        private InteractionHierarchyPair[] Pairs => new[] {
            new InteractionHierarchyPair(_aRegion, _bRegion), new InteractionHierarchyPair(_aObject, _bObject),
            new InteractionHierarchyPair(_aSeat, _bSeat) };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeRelationCallback(IntPtr relation, IntPtr user);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sb_register_on_relation_event")]
        private static extern void NativeCallback(long space, NativeRelationCallback callback, IntPtr user);

        [SetUp] public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("hierarchy-world");
            _aRegion = Obj("source", "study", null, SpatialBlendObjectKind.Region);
            _aObject = Obj("source", "chair", _aRegion, SpatialBlendObjectKind.Object);
            _aSeat = Obj("source", "seat", _aObject, SpatialBlendObjectKind.Affordance);
            _bRegion = Obj("target", "lounge", null, SpatialBlendObjectKind.Region);
            var unmatched = Obj("target", "conversation-area", _bRegion, SpatialBlendObjectKind.Region);
            _bObject = Obj("target", "sofa", unmatched, SpatialBlendObjectKind.Object);
            _bSeat = Obj("target", "cushion", _bObject, SpatialBlendObjectKind.Affordance);
        }
        [TearDown] public void TearDown()
        {
            _world.Shutdown();
            for (int i = _created.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }
        [Test] public void HierarchyOwnsRootsAndPreservesProjectedDepthBeforeContact()
        {
            Assert.That(SpatialBlendAPI.TryGetOwnedCorrespondenceFeature(out string error), Is.True, error);
            var aBranch = Obj("source", "other-object", _aRegion, SpatialBlendObjectKind.Object);
            var bBranch = Obj("target", "different-object", _bRegion, SpatialBlendObjectKind.Object);
            var multipleBranches = Pairs.Concat(new[] { new InteractionHierarchyPair(aBranch, bBranch) }).ToArray();
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", multipleBranches, out var lease, out error), Is.True, error);
            var evidence = Evidence(lease, _aSeat, out var relation);
            Assert.That(relation.Phase, Is.EqualTo(SourceInteractionPhase.Active));
            Assert.That(lease.TryMapSourcePath(evidence, relation, out var path, out error), Is.True, error);
            Assert.That(path.Nodes.Select(n => n.Kind), Is.EqualTo(new[] { InteractionHierarchyNodeKind.Space,
                InteractionHierarchyNodeKind.Region, InteractionHierarchyNodeKind.Object, InteractionHierarchyNodeKind.Affordance }));
            Assert.That(path.Nodes.Last().TargetNativeElement, Is.EqualTo(_bSeat.Registration.NativeElement));
            Assert.That(path.SourceEvidence, Is.SameAs(evidence)); Assert.That(path.SourceRelation, Is.SameAs(relation));
            Assert.Throws<NotSupportedException>(() => ((IList<InteractionHierarchyMappedNode>)path.Nodes).Clear());
            Assert.That(lease.TryRevalidate(out error), Is.True, error);
            // Outer-region/root cues are usable without waiting for a seat leaf.
            evidence = Evidence(lease, _aRegion, out relation);
            Assert.That(lease.TryMapSourcePath(evidence, relation, out path, out error), Is.True, error);
            Assert.That(path.Nodes.Count, Is.EqualTo(2));
            var rootRow = Relation(new[] { relation.Hierarchy[0] });
            var rootEvidence = new SourceInteractionEvidence(evidence.NativeHeader, Array.Empty<SourceInteractionAnchor>(), new[] { rootRow });
            Assert.That(lease.TryMapSourcePath(rootEvidence, rootRow, out path, out error), Is.True, error);
            Assert.That(path.Nodes.Count, Is.EqualTo(1));
            evidence = Evidence(lease, aBranch, out relation);
            Assert.That(lease.TryMapSourcePath(evidence, relation, out path, out error), Is.True, error);
            Assert.That(path.Nodes.Last().TargetNativeElement, Is.EqualTo(bBranch.Registration.NativeElement));
            Assert.That(path.Nodes.Count, Is.EqualTo(3));
        }
        [Test] public void HierarchyRejectsCrossedDuplicateWrongKindAndForeignEndpointsAtomically()
        {
            var aOther = Obj("source", "other-region", null, SpatialBlendObjectKind.Region);
            var bOther = Obj("target", "other-region", null, SpatialBlendObjectKind.Region);
            var invalid = new[] {
                new[] { Pairs[0], Pairs[0] },
                new[] { new InteractionHierarchyPair(_aRegion, _bSeat) },
                new[] { new InteractionHierarchyPair(_aRegion, _aRegion) },
                new[] { Pairs[0], new InteractionHierarchyPair(aOther, _bRegion) },
                new[] { new InteractionHierarchyPair(aOther, _bRegion), new InteractionHierarchyPair(_aRegion, bOther), Pairs[1] },
                new[] { new InteractionHierarchyPair(null, _bSeat) }
            };
            foreach (var pairs in invalid)
                Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", pairs, out var failed, out _), Is.False);
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "source", Pairs, out _, out _), Is.False);
            var foreign = Create<SpatialBlendLocalWorld>("foreign-world");
            var obj = Create<SpatialBlendObject>("foreign"); obj.Configure(foreign, "source", "foreign", null, SpatialBlendObjectKind.Region);
            Assert.That(obj.TryRegister(out _), Is.True);
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", new[] { new InteractionHierarchyPair(obj, _bRegion) }, out _, out _), Is.False);
            foreign.Shutdown();
            Register().Dispose(); // All earlier failures left both implicit roots free.
        }
        [Test] public void HierarchyBoundsAndReentrantCollectionsCannotChangePinnedWorld()
        {
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", Array.Empty<InteractionHierarchyPair>(), out _, out _), Is.False);
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", new InteractionHierarchyPair[128], out _, out _), Is.False);
            var hostile = new HostilePairs(Pairs, () => throw new InvalidOperationException("collection failure"));
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", hostile, out _, out _), Is.False);
            hostile = new HostilePairs(Pairs, () => { _world.Shutdown(); Assert.That(_world.TryInitialize(out _), Is.True); });
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", hostile, out _, out _), Is.False);
            foreach (var obj in new[] { _aRegion, _aObject, _aSeat, _bRegion, _bObject, _bSeat })
                Assert.That(obj.TryRegister(out _), Is.True);
            Register().Dispose();
        }
        [Test] public void HierarchyCannotBorrowLegacyPairOrPartiallyOwnRoots()
        {
            Assert.That(_world.TryRegisterSupportCorrespondence(_aSeat, _bSeat, out var borrowed, out string error), Is.True, error);
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", Pairs, out _, out _), Is.False);
            borrowed.Dispose();
            var first = Register();
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", Pairs, out _, out _), Is.False);
            first.Dispose();
            var replacement = Register(); first.Dispose();
            Assert.That(replacement.TryRevalidate(out error), Is.True, error);
            replacement.Dispose();
        }
        [Test] public void HierarchyMappedEndpointAndUnrelatedSpaceRetirementCloseMap()
        {
            foreach (string space in new[] { "source", "target" })
            {
                var unrelated = Obj(space, "unrelated", null, SpatialBlendObjectKind.Object);
                var lease = Register(); unrelated.Unregister();
                Assert.That(lease.IsOpen, Is.False);
                Assert.That(lease.TryRevalidate(out _), Is.False);
                Assert.That(unrelated.IsRegistered, Is.False);
            }
            var endpointLease = Register(); _aObject.Unregister();
            Assert.That(_aSeat.IsRegistered, Is.False);
            Assert.That(endpointLease.TryRevalidate(out _), Is.False);
            Assert.That(_aObject.TryRegister(out _), Is.True); Assert.That(_aSeat.TryRegister(out _), Is.True);
            Register().Dispose();
        }
        [Test] public void HierarchyPathRejectsDetachedRowMissingAncestorAndDifferentLifetime()
        {
            var lease = Register(); var e = Evidence(lease, _aSeat, out var row);
            var other = Evidence(lease, _aSeat, out var otherRow);
            Assert.That(lease.TryMapSourcePath(e, otherRow, out _, out _), Is.False);
            var wrongHeader = e.NativeHeader; wrongHeader.space = lease.TargetNativeSpace;
            var wrongSpace = new SourceInteractionEvidence(wrongHeader, Array.Empty<SourceInteractionAnchor>(), new[] { row });
            Assert.That(lease.TryMapSourcePath(wrongSpace, row, out _, out _), Is.False);
            var badRow = Relation(row.Hierarchy.Skip(1).ToArray());
            var bad = new SourceInteractionEvidence(e.NativeHeader, Array.Empty<SourceInteractionAnchor>(), new[] { badRow });
            Assert.That(lease.TryMapSourcePath(bad, badRow, out _, out _), Is.False);
            _aSeat.Unregister(); Assert.That(_aSeat.TryRegister(out _), Is.True);
            var replacement = Register();
            Assert.That(replacement.TryMapSourcePath(e, row, out _, out _), Is.False);
            Assert.That(lease.TryMapSourcePath(other, otherRow, out _, out _), Is.False);
        }
        [Test] public void HierarchyWorkerCallsFailWithoutRetiringOrReadingUnityState()
        {
            var lease = Register();
            Task.Run(() => {
                Assert.That(lease.TryRevalidate(out _), Is.False);
                Assert.That(lease.TryClose(out _), Is.False);
                Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", Pairs, out _, out _), Is.False);
                Assert.Throws<InvalidOperationException>(() => lease.Dispose());
            }).GetAwaiter().GetResult();
            Assert.That(lease.TryRevalidate(out string error), Is.True, error);
        }
        [Test] public void HierarchyBusyCallbackRevokesAndRetriesExactRetirement()
        {
            // ABI 4: scope belongs to the relation family. The actor's
            // element is the source region that holds the chair and its seat, so the AffordancePose family's implicit
            // scope reaches the seat; the actor's own pelvis binding (SpatialBlendActor binds every Body anchor) is the only one.
            var actor = Create<SpatialBlendActor>("source-actor");
            actor.Configure(_aRegion, "person", true, new[] {
                new SpatialBlendActorAnchor("navigation", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("pelvis", SpatialBlendActorAnchorRole.Body, Vector3.zero) });
            Assert.That(actor.TryRegister(out _), Is.True);
            Assert.That(actor.TryCreateMotionInput(out var sink), Is.True);
            var lease = Register(); var retired = _aSeat.Registration;
            int callbacks = 0; bool closed = true; bool revalidated = true;
            NativeRelationCallback callback = (ev, user) => {
                if (++callbacks > 1) return;
                closed = lease.TryClose(out _);
                revalidated = lease.TryRevalidate(out _);
                _aSeat.Unregister();
            };
            NativeCallback(actor.Registration.NativeSpace, callback, IntPtr.Zero);
            try
            {
                Assert.That(sink.TryApplyWorldFrame(new AnchorPoseFrame { sequence = 0, timeSeconds = 0,
                    anchors = new[] { new AnchorPoseSample { anchorId = "pelvis", worldPosition = new Vector3(0, .01f, 0), worldRotation = Quaternion.identity } } }), Is.True);
            }
            finally { NativeCallback(actor.Registration.NativeSpace, null, IntPtr.Zero); GC.KeepAlive(callback); }
            Assert.That(callbacks, Is.GreaterThan(0)); Assert.That(closed, Is.False);
            Assert.That(revalidated, Is.False); Assert.That(lease.IsOpen, Is.False);
            _world.DrainRealizations(0);
            Assert.That(lease.NativeHandle, Is.Zero);
            Assert.That(SpatialBlendAPI.TryRegisterOwnedCorrespondences(lease.Session, new[] {
                new SbSupportCorrespondence(retired.NativeSpace, retired.NativeElement,
                    _bSeat.Registration.NativeSpace, _bSeat.Registration.NativeElement) }), Is.Zero, "Stale native element must be removed after Busy cleanup.");
            Assert.That(_aSeat.TryRegister(out _), Is.True);
            var fresh = Register(); lease.Dispose();
            Assert.That(fresh.TryRevalidate(out string error), Is.True, error);
        }
        [Test] public void HierarchyShutdownCannotRetireFreshWorldMap()
        {
            var old = Register(); _world.Shutdown();
            Assert.That(old.IsOpen, Is.False); Assert.That(old.TryRevalidate(out _), Is.False);
            foreach (var obj in new[] { _aRegion, _aObject, _aSeat, _bRegion, _bObject, _bSeat })
                Assert.That(obj.TryRegister(out _), Is.True);
            var fresh = Register(); old.Dispose();
            Assert.That(fresh.TryRevalidate(out string error), Is.True, error);
        }
        [Test] public void HierarchyTransformRefreshDoesNotCloseButObservedGeometryRetirementDoes()
        {
            Assert.That(_world.TryCreateSceneGeometryScope("target", "observed", out var scope, out string error), Is.True, error);
            var lease = Register(); _bSeat.transform.position = new Vector3(20, .5f, 1);
            Assert.That(_bSeat.TryRefresh(out error), Is.True, error);
            Assert.That(lease.TryRevalidate(out error), Is.True, error);
            scope.Dispose();
            Assert.That(lease.IsOpen, Is.False);
            Register().Dispose();
        }

        private InteractionHierarchyLease Register()
        {
            Assert.That(_world.TryRegisterInteractionHierarchy("source", "target", Pairs, out var lease, out string error), Is.True, error);
            return lease;
        }
        // Synthetic path witness: this test isolates hierarchy projection. It is
        // explicitly not a live source observation, phase/contact or motion claim.
        private static SourceInteractionEvidence Evidence(InteractionHierarchyLease lease, SpatialBlendObject leaf,
            out SourceInteractionRelation row)
        {
            var path = new List<SourceInteractionHierarchyNode>();
            for (var r = leaf.Registration; r != null; r = r.Parent)
                path.Add(new SourceInteractionHierarchyNode("not-a-semantic-match", new SpatialBlendAPI.SbSourceNode {
                    element = r.NativeElement, parent = r.Parent == null ? lease.SourceNativeRoot : r.Parent.NativeElement,
                    shared = -1, world_transform_row_major = Identity() }));
            path.Add(new SourceInteractionHierarchyNode("", new SpatialBlendAPI.SbSourceNode {
                element = lease.SourceNativeRoot, parent = -1, shared = -1, world_transform_row_major = Identity() }));
            path.Reverse(); row = Relation(path.ToArray());
            return new SourceInteractionEvidence(new SpatialBlendAPI.SbSourceHeader { space = lease.SourceNativeSpace },
                Array.Empty<SourceInteractionAnchor>(), new[] { row });
        }
        private static SourceInteractionRelation Relation(SourceInteractionHierarchyNode[] path)
            => new SourceInteractionRelation(new SpatialBlendAPI.SbSourceRelation { phase = 1, support_to_world_row_major = Identity() },
                "", "", path, Array.Empty<double>());
        private static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        private SpatialBlendObject Obj(string space, string id, SpatialBlendObject parent, SpatialBlendObjectKind kind)
        {
            var obj = Create<SpatialBlendObject>(id); obj.Configure(_world, space, id, parent, kind);
            // A Region's polygon is its navigable surface and sibling regions must not overlap (core containment), so only
            // the other kinds carry this shared square.
            if (kind != SpatialBlendObjectKind.Region)
                Assert.That(obj.TrySetSupportPolygon(new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
                    new Vector3(1, 0, 1), new Vector3(-1, 0, 1) }, out _), Is.True);
            Assert.That(obj.TryRegister(out string error), Is.True, error); return obj;
        }
        private T Create<T>(string name) where T : Component
        {
            var go = new GameObject(name); _created.Add(go); var value = go.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, null); return value;
        }
        private sealed class HostilePairs : IReadOnlyList<InteractionHierarchyPair>
        {
            private readonly InteractionHierarchyPair[] _values;
            private readonly Action _onRead;
            internal HostilePairs(InteractionHierarchyPair[] values, Action onRead) { _values = values; _onRead = onRead; }
            public int Count => _values.Length;
            public InteractionHierarchyPair this[int i] { get { if (i == 0) _onRead(); return _values[i]; } }
            public IEnumerator<InteractionHierarchyPair> GetEnumerator() => ((IEnumerable<InteractionHierarchyPair>)_values).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
#endif
