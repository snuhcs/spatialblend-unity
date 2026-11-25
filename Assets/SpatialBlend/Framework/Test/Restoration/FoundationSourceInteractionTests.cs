// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationSourceEvidence")]
    public sealed partial class FoundationSourceInteractionTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private SpatialBlendLocalWorld _world;
        private SpatialBlendActor _actor;
        private SpatialBlendObject _support;
        private IAnchorPoseFrameSink _sink;
        private const long Age = 1000000000;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeRelationCallback(IntPtr relationEvent, IntPtr user);
        [DllImport(SpatialBlendLibraryConfig.LibraryNames.RELEASE_LIBRARY, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sb_register_on_relation_event")]
        private static extern void RegisterNativeRelationCallback(long space, NativeRelationCallback callback, IntPtr user);
        [SetUp] public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("source-evidence-world");
            // ABI 4: scope belongs to the relation family. The
            // chair's support is a descendant of the actor's own element (the region), where the AffordancePose family's
            // implicit scope starts; the actor's own body-sample binding (SpatialBlendActor binds every Body anchor) is the only one,
            // and an app-declared second binding on the same anchor would only duplicate the relation.
            var region = Obj("region", null, SpatialBlendObjectKind.Region);
            var chair = Obj("chair", region, SpatialBlendObjectKind.Object);
            _support = Obj("bounded-support", chair, SpatialBlendObjectKind.Affordance, true);
            _support.transform.position = new Vector3(0, .6f, 0);
            Assert.That(_support.TryRefresh(out string error), Is.True, error);
            _actor = Create<SpatialBlendActor>("actor");
            _actor.Configure(region, "person", true, new[] {
                new SpatialBlendActorAnchor("navigation", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("body-sample", SpatialBlendActorAnchorRole.Body, Vector3.zero),
                new SpatialBlendActorAnchor("other-body", SpatialBlendActorAnchorRole.Body, Vector3.zero)
            });
            Assert.That(_actor.TryRegister(out error), Is.True, error);
            Assert.That(_actor.TryCreateMotionInput(out _sink), Is.True);
        }
        [TearDown] public void TearDown()
        {
            _world.Shutdown();
            for (int i = _created.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        [Test] public void SourceEvidenceNegotiatesEveryLayoutAndRejectsMismatches()
        {
            Assert.That(SpatialBlendAPI.TryGetSourceEvidenceFeature(out var f, out string error), Is.True, error);
            uint Count(uint k) => (uint)SpatialBlendAPI.SourceEvidenceFields[k - 1].Length;
            uint Offset(uint k, uint i) => (uint)Marshal.OffsetOf(SpatialBlendAPI.SourceEvidenceTypes[k - 1],
                SpatialBlendAPI.SourceEvidenceFields[k - 1][i]).ToInt64();
            Assert.That(SpatialBlendAPI.ValidateSourceEvidenceFeature(f, Count, Offset, out error), Is.True, error);
            for (uint k = 1; k <= 6; ++k)
            {
                uint selected = k;
                Assert.That(SpatialBlendAPI.ValidateSourceEvidenceFeature(f, x => Count(x) + (x == selected ? 1u : 0), Offset, out _), Is.False);
                for (uint i = 0; i < Count(k); ++i)
                {
                    uint field = i;
                    Assert.That(SpatialBlendAPI.ValidateSourceEvidenceFeature(f, Count,
                        (x, y) => Offset(x, y) + (x == selected && y == field ? 1u : 0), out _), Is.False);
                }
            }
            var changed = f; changed.coordinate_convention++;
            Assert.That(SpatialBlendAPI.ValidateSourceEvidenceFeature(changed, Count, Offset, out _), Is.False);
            changed = f; changed.max_anchors++;
            Assert.That(SpatialBlendAPI.ValidateSourceEvidenceFeature(changed, Count, Offset, out _), Is.False);
            changed = f; changed.monotonic_clock_epoch = 0;
            Assert.That(SpatialBlendAPI.ValidateSourceEvidenceFeature(changed, Count, Offset, out _), Is.False);
            Assert.That((int)SourceInteractionKernel.Spacing, Is.EqualTo((int)SbKernelType.Spacing));
            Assert.That((int)SourceInteractionKernel.Facing, Is.EqualTo((int)SbKernelType.Facing));
        }

        [Test] public void SourceEvidenceCopiesExactFrameHierarchyPolygonAndOriginalReceipt()
        {
            var events = Subscribe(out var lease);
            var frame = Frame(7, .9f); frame.timeSeconds = 12000;
            Assert.That(_sink.TryApplyWorldFrame(frame), Is.True);
            var value = events.Single(); var e = value.Evidence;
            Assert.That(value.Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available));
            Assert.That(e, Is.Not.Null); Assert.That(e.Epoch, Is.EqualTo(value.Input.Epoch));
            Assert.That(e.Sequence, Is.EqualTo(7)); Assert.That(e.SourceTimeSeconds, Is.EqualTo(12000));
            Assert.That(e.NativeReceiptNanoseconds, Is.GreaterThan(0)); Assert.That(e.NativeClockEpoch, Is.GreaterThan(0));
            Assert.That(e.NativeActor, Is.EqualTo(_actor.Registration.NativeActor));
            Assert.That(e.Anchors.Select(a => a.Name), Does.Contain("navigation"));
            var body = e.Anchors.Single(a => a.Name == "body-sample");
            Assert.That(body.Observed && !body.IsLocomotionRoot, Is.True);
            Assert.That(body.PositionDimension, Is.EqualTo(3));
            Assert.That(body.CorePosition.Z, Is.EqualTo(.9).Within(1e-6));
            Assert.That(body.CorePosition.Y, Is.EqualTo(.2).Within(1e-6));
            Assert.That(body.CoreYawRadians, Is.EqualTo(CoordinateConverter.UnityToSpatialBlendAngle(37) * Math.PI / 180).Within(1e-6));
            Assert.That(e.Anchors.Single(a => a.IsLocomotionRoot).CorePosition.Z, Is.Zero);
            var relation = e.Relations.Single(r => r.Kernel == SourceInteractionKernel.AffordancePose);
            Assert.That(e.Anchors[(int)relation.AnchorOrdinal], Is.SameAs(body));
            Assert.That(relation.HasProjection && relation.BoundedPlanarSupport, Is.True);
            Assert.That(relation.AnchorSupportDistance, Is.EqualTo(.3).Within(1e-6), "Projection alone is not body contact.");
            Assert.That(relation.CoreProjectedPoint.Z, Is.EqualTo(.6).Within(1e-6));
            Assert.That(relation.Hierarchy.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(relation.Hierarchy[0].NativeParent, Is.EqualTo(-1));
            Assert.That(relation.Hierarchy.Last().NativeElement, Is.EqualTo(_support.Registration.NativeElement));
            Assert.That(relation.Hierarchy.Last().SharedId, Is.GreaterThan(0),
                "Local objects carry a process-local shared id: the core never makes an element without one a referent.");
            Assert.That(relation.BoundaryXY.Count, Is.EqualTo(8));
            Assert.Throws<NotSupportedException>(() => ((IList<double>)relation.BoundaryXY)[0] = 99);
            Assert.Throws<NotSupportedException>(() => ((IList<SourceInteractionAnchor>)e.Anchors)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<SourceInteractionHierarchyNode>)relation.Hierarchy)[0] = null);
            Assert.That(lease.TryRevalidate(value, Age, out _), Is.True);
            frame.anchors[1].worldPosition = Vector3.one * 90;
            Assert.That(body.CorePosition.Z, Is.EqualTo(.9).Within(1e-6));
            Assert.That(lease.TryRevalidate(value, 1, out var status), Is.False);
            Assert.That(status, Is.EqualTo(SourceInteractionEvidenceStatus.Stale), "Age uses original native receipt, not source replay time.");
        }

        [Test] public void SourceEvidenceSubscriptionIsFutureOnlyAndLastLeaseDisablesCapture()
        {
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True);
            var events = Subscribe(out var first); var other = Subscribe(out var second);
            Assert.That(events, Is.Empty);
            Assert.That(_world.TryGetActorObservationClock(_actor.Registration, out var clock), Is.True);
            Assert.That(_world.TryCopySourceEvidence(_actor.Registration, clock.Epoch, 0, out _), Is.EqualTo(SourceInteractionEvidenceStatus.Unavailable));
            first.Dispose(); Assert.That(_sink.TryApplyWorldFrame(Frame(1)), Is.True);
            Assert.That(events, Is.Empty); Assert.That(other.Single().Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available));
            var old = other.Single(); second.Dispose();
            Assert.That(second.TryRevalidate(old, Age, out _), Is.False);
            Assert.That(_world.TryCopySourceEvidence(_actor.Registration, old.Input.Epoch, 1, out _), Is.EqualTo(SourceInteractionEvidenceStatus.Unavailable));
            var fresh = Subscribe(out var next); first.Dispose(); second.Dispose();
            Assert.That(fresh, Is.Empty); Assert.That(_sink.TryApplyWorldFrame(Frame(2)), Is.True);
            Assert.That(fresh.Single().Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available)); Assert.That(next.IsActive, Is.True);
        }

        [Test] public void SourceEvidenceBodyOnlyAndEmptyFramesNeverInventRootOrContact()
        {
            var events = Subscribe(out _); var frame = Frame(0);
            frame.anchors = new[] { frame.anchors[1] };
            Assert.That(_sink.TryApplyWorldFrame(frame), Is.True);
            var e = events.Single().Evidence;
            Assert.That(e, Is.Not.Null); Assert.That(e.Anchors.Single(a => a.IsLocomotionRoot).Observed, Is.False);
            Assert.That(e.Relations, Is.Not.Empty);
            Assert.That(events[0].Input.UnavailableAnchorIds, Does.Contain("navigation"));
            Assert.That(_sink.TryApplyWorldFrame(new AnchorPoseFrame { sequence = 1, timeSeconds = 1, anchors = Array.Empty<AnchorPoseSample>() }), Is.True);
            Assert.That(events[1].Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available));
            Assert.That(events[1].Evidence.Relations, Is.Empty);
            Assert.That(events[1].Evidence.Anchors.All(a => !a.Observed), Is.True);
        }

        [Test] public void SourceEvidenceControlsRevokeWithoutSampleClocksAndResetKeepsLease()
        {
            var events = Subscribe(out var lease); Assert.That(_sink.TryApplyWorldFrame(Frame(4)), Is.True);
            var old = events[0]; _sink.ClearWorldAnchor("body-sample");
            AssertControl(events[1], CommittedMotionObservationKind.Cleared);
            Assert.That(lease.TryRevalidate(old, Age, out _), Is.False);
            Assert.That(((IAnchorPoseFrameCheckedReplaySink)_sink).TryResetWorldAnchorReplay(), Is.True);
            AssertControl(events[2], CommittedMotionObservationKind.Reset); Assert.That(lease.IsActive, Is.True);
            Assert.That(events[2].Input.Epoch, Is.Not.EqualTo(old.Input.Epoch));
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True);
            Assert.That(events[3].Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available));
            Assert.That(lease.TryRevalidate(events[3], Age, out _), Is.True);
        }

        [Test] public void SourceEvidenceWriterLegacyAndRegistrationRetirementFenceOldLeases()
        {
            var events = Subscribe(out var oldLease); var oldSink = _sink;
            Assert.That(_actor.TryCreateMotionInput(out _sink), Is.True);
            AssertControl(events.Single(), CommittedMotionObservationKind.Retired);
            Assert.That(events[0].Input.RetirementReason, Is.EqualTo(CommittedMotionRetirementReason.WriterReplaced));
            Assert.That(oldLease.IsActive, Is.False); Assert.That(oldSink.TryApplyWorldFrame(Frame(0)), Is.False);
            var fresh = Subscribe(out var freshLease); oldLease.Dispose();
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True); Assert.That(fresh[0].Evidence, Is.Not.Null);
            Assert.That(_actor.TryCreateAnchorPoseInput(out var legacy), Is.True);
            // Existing legacy body-only admission tries to clear the old planar
            // root and returns false after mutation. Retirement must still happen.
            Assert.That(legacy.TryApplyWorldFrame(Frame(1)), Is.False);
            Assert.That(fresh.Last().Input.RetirementReason, Is.EqualTo(CommittedMotionRetirementReason.LegacyMutation));
            Assert.That(freshLease.IsActive, Is.False);
            Assert.That(_actor.TryCreateMotionInput(out _sink), Is.True);
            var retiring = Subscribe(out var retiringLease); _actor.Unregister();
            Assert.That(retiring.Single().Input.RetirementReason, Is.EqualTo(CommittedMotionRetirementReason.RegistrationRetired));
            Assert.That(retiringLease.IsActive, Is.False);
        }

        [Test] public void SourceEvidenceReentrantNewFrameSuppressesOlderDeliveryAcrossBothStreams()
        {
            var motion = new List<CommittedMotionObservation>();
            Assert.That(_actor.TrySubscribeCommittedMotion(motion.Add, out _), Is.True); // Subscribe first intentionally.
            bool nested = false;
            Assert.That(_actor.TrySubscribeCommittedSourceInteraction(value => {
                if (value.Input.Sequence == 0) nested = _sink.TryApplyWorldFrame(Frame(1));
            }, out _), Is.True);
            var events = Subscribe(out var lease);
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True); Assert.That(nested, Is.True);
            Assert.That(events.Select(e => e.Input.Sequence), Is.EqualTo(new long?[] { 1 }));
            Assert.That(motion.Select(e => e.Sequence), Is.EqualTo(new long?[] { 1 }));
            Assert.That(events[0].Evidence.Sequence, Is.EqualTo(1)); Assert.That(lease.TryRevalidate(events[0], Age, out _), Is.True);
        }

        [Test] public void SourceEvidenceSceneMutationBetweenObserversWithdrawsLaterEvidence()
        {
            CommittedSourceInteractionObservation first = null;
            Assert.That(_actor.TrySubscribeCommittedSourceInteraction(value => {
                first = value;
                _support.transform.position += Vector3.up;
                _support.TryRefresh(out _);
            }, out var firstLease), Is.True);
            var events = Subscribe(out var secondLease); Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True);
            Assert.That(first.Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available));
            Assert.That(events.Single().Status, Is.EqualTo(SourceInteractionEvidenceStatus.Stale));
            Assert.That(events[0].Evidence, Is.Null); Assert.That(firstLease.TryRevalidate(first, Age, out _), Is.False);
            Assert.That(secondLease.TryRevalidate(first, Age, out _), Is.False, "Another lease's receipt is not issued by this lease.");
            // The evidence also carries the navigation anchor's Spacing relation to the chair (the containing node's
            // children are Spacing referents), so the relation of interest is selected, not taken by position.
            var support = first.Evidence.Relations.First(r => r.Kernel == SourceInteractionKernel.AffordancePose && r.HasProjection);
            Assert.That(support.CoreProjectedPoint.Z, Is.EqualTo(.6).Within(1e-6), "Historical owned geometry remains unchanged.");
        }

        [Test] public void SourceEvidenceRejectedInputPublishesNothingAndCallbacksCannotUndoAdmission()
        {
            var events = Subscribe(out _); var invalid = Frame(0); invalid.anchors[1].anchorId = "unknown";
            Assert.That(_sink.TryApplyWorldFrame(invalid), Is.False); Assert.That(events, Is.Empty);
            Assert.That(_actor.TrySubscribeCommittedSourceInteraction(_ => throw new InvalidOperationException("observer"), out _), Is.True);
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True); Assert.That(events.Count, Is.EqualTo(1));
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.False); Assert.That(events.Count, Is.EqualTo(1));
            Assert.That(_world.TryBeginActorObservationEpoch(_actor.Registration, "navigation", out _, out _), Is.True);
            Assert.That(_sink.TryApplyWorldFrame(Frame(1)), Is.False, "A managed-valid frame rejected by native must not publish.");
            Assert.That(events.Count, Is.EqualTo(1));
        }

        [Test] public void SourceEvidenceNativeCallbackBusyDisposalRetriesWithoutAffectingReplacement()
        {
            var events = Subscribe(out var lease); bool called = false;
            Assert.That(_world.TryGetActorObservationClock(_actor.Registration, out var before), Is.True);
            var during = SourceInteractionEvidenceStatus.Available;
            NativeRelationCallback callback = (value, user) => {
                if (called) return;
                called = true;
                // The actual native callback runs inside the Space solve lock.
                during = _world.TryEnableSourceEvidence(_actor.Registration, before.Epoch, false);
                lease.Dispose(); // Managed retirement is immediate, native disable is Busy.
            };
            long space = _actor.Registration.NativeSpace;
            RegisterNativeRelationCallback(space, callback, IntPtr.Zero);
            try { Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True); }
            finally { RegisterNativeRelationCallback(space, null, IntPtr.Zero); GC.KeepAlive(callback); }
            Assert.That(called, Is.True); Assert.That(during, Is.EqualTo(SourceInteractionEvidenceStatus.Busy));
            Assert.That(lease.IsActive, Is.False); Assert.That(events, Is.Empty);
            Assert.That(_world.TryGetActorObservationClock(_actor.Registration, out var clock), Is.True);
            Assert.That(_world.TryCopySourceEvidence(_actor.Registration, clock.Epoch, 0, out _), Is.EqualTo(SourceInteractionEvidenceStatus.Unavailable));
            var fresh = Subscribe(out var replacement); lease.Dispose();
            Assert.That(_sink.TryApplyWorldFrame(Frame(1)), Is.True);
            Assert.That(fresh.Single().Status, Is.EqualTo(SourceInteractionEvidenceStatus.Available)); Assert.That(replacement.IsActive, Is.True);
        }

        [Test] public void SourceEvidenceWorkerAndGeneratedTransformsCannotObserveOrRetireInput()
        {
            var events = Subscribe(out var lease); Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True);
            var value = events[0]; bool subscribed = true, validated = true; Exception error = null;
            var thread = new Thread(() => {
                try { subscribed = _actor.TrySubscribeCommittedSourceInteraction(_ => { }, out _);
                    validated = lease.TryRevalidate(value, Age, out _); lease.Dispose(); }
                catch (Exception e) { error = e; }
            });
            thread.Start(); Assert.That(thread.Join(5000), Is.True);
            Assert.That(error, Is.Null); Assert.That(subscribed || validated, Is.False); Assert.That(lease.IsActive, Is.True);
            _actor.transform.position = Vector3.one * 90; _world.DrainRealizations(1);
            Assert.That(events.Count, Is.EqualTo(1)); Assert.That(value.Evidence.Anchors.Single(a => a.Name == "body-sample").CorePosition.Z, Is.EqualTo(.9).Within(1e-6));
            Assert.That(lease.TryRevalidate(value, Age, out _), Is.True);
        }

        [Test] public void SourceEvidenceReentrantResetAndReplacementCannotDeliverOldFrame()
        {
            bool reset = false;
            Assert.That(_actor.TrySubscribeCommittedSourceInteraction(value => {
                if (value.Input.Kind == CommittedMotionObservationKind.Frame)
                    reset = ((IAnchorPoseFrameCheckedReplaySink)_sink).TryResetWorldAnchorReplay();
            }, out var first), Is.True);
            var events = Subscribe(out var second); Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True);
            Assert.That(reset, Is.True); AssertControl(events.Single(), CommittedMotionObservationKind.Reset);
            first.Dispose(); second.Dispose(); events.Clear();
            bool replaced = false;
            Assert.That(_actor.TrySubscribeCommittedSourceInteraction(value => {
                if (value.Input.Kind == CommittedMotionObservationKind.Frame) replaced = _actor.TryCreateMotionInput(out _sink);
            }, out _), Is.True);
            events = Subscribe(out var oldLease); Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True);
            Assert.That(replaced, Is.True); AssertControl(events.Single(), CommittedMotionObservationKind.Retired);
            var fresh = Subscribe(out var freshLease); oldLease.Dispose();
            Assert.That(_sink.TryApplyWorldFrame(Frame(0)), Is.True); Assert.That(fresh.Single().Evidence, Is.Not.Null);
            Assert.That(freshLease.IsActive, Is.True);
        }

        [Test] public void SourceEvidenceMalformedRowsRejectWholeValue()
        {
            var bytes = Encoding.UTF8.GetBytes("rootbody");
            var anchors = new[] {
                new SpatialBlendAPI.SbSourceAnchor { name_length = 4, observed = 1, locomotion_root = 1, position_dimension = 2 },
                new SpatialBlendAPI.SbSourceAnchor { name_offset = 4, name_length = 4, observed = 1, position_dimension = 3 }
            };
            var emptyRelations = Array.Empty<SpatialBlendAPI.SbSourceRelation>(); var emptyNodes = Array.Empty<SpatialBlendAPI.SbSourceNode>();
            SourceInteractionEvidence Decode() => SpatialBlendAPI.DecodeSourceEvidence(default, anchors, emptyRelations, emptyNodes, Array.Empty<double>(), bytes);
            Assert.That(Decode().Anchors.Count, Is.EqualTo(2));
            var good = anchors[1]; anchors[1].name_offset = 0;
            Assert.Throws<InvalidDataException>(() => Decode()); anchors[1] = good;
            anchors[1].observed = 2; Assert.Throws<InvalidDataException>(() => Decode()); anchors[1] = good;
            anchors[1].position_z = double.NaN; Assert.Throws<InvalidDataException>(() => Decode()); anchors[1] = good;
            anchors[0].locomotion_root = 0; Assert.Throws<InvalidDataException>(() => Decode()); anchors[0].locomotion_root = 1;
            anchors[1].name_length = 128; Assert.Throws<InvalidDataException>(() => Decode()); anchors[1] = good;
            bytes[7] = 0; Assert.Throws<InvalidDataException>(() => Decode()); bytes[7] = 0xff;
            Assert.Throws<DecoderFallbackException>(() => Decode()); bytes[7] = (byte)'y';
            Assert.Throws<InvalidDataException>(() => SpatialBlendAPI.DecodeSourceEvidence(default, anchors,
                new[] { new SpatialBlendAPI.SbSourceRelation { anchor_ordinal = 500 } }, emptyNodes, Array.Empty<double>(), bytes));
            Assert.Throws<InvalidDataException>(() => SpatialBlendAPI.DecodeSourceEvidence(default, anchors,
                emptyRelations, emptyNodes, new double[] { 0, 0 }, bytes));
        }

        private List<CommittedSourceInteractionObservation> Subscribe(out CommittedSourceInteractionObservationLease lease)
        {
            var events = new List<CommittedSourceInteractionObservation>();
            Assert.That(_actor.TrySubscribeCommittedSourceInteraction(events.Add, out lease), Is.True); return events;
        }
        private static void AssertControl(CommittedSourceInteractionObservation value, CommittedMotionObservationKind kind)
        {
            Assert.That(value.Input.Kind, Is.EqualTo(kind)); Assert.That(value.Evidence, Is.Null);
            Assert.That(value.Status, Is.EqualTo(SourceInteractionEvidenceStatus.Unavailable));
            Assert.That(value.Input.Sequence, Is.Null); Assert.That(value.Input.SourceTimeSeconds, Is.Null); Assert.That(value.Input.ReceiptTimeSeconds, Is.Null);
        }
        private SpatialBlendObject Obj(string id, SpatialBlendObject parent, SpatialBlendObjectKind kind, bool polygon = false)
        {
            var obj = Create<SpatialBlendObject>(id); obj.Configure(_world, "source", id, parent, kind);
            // An AffordancePose referent is in scope while the anchor is inside the
            // footprint of its parent object. The chair therefore has a footprint (a box wider than its 2 m seat), and the
            // actor stands inside it, so the chair, which offers no affordance itself, is the deepest container the actor has
            // entered and only its descendants are candidates.
            if (kind == SpatialBlendObjectKind.Object)
            {
                var footprint = obj.gameObject.AddComponent<BoxCollider>();
                footprint.size = new Vector3(6, 2, 6); footprint.center = new Vector3(0, 1, 0);
            }
            if (polygon) Assert.That(obj.TrySetSupportPolygon(new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
                new Vector3(1, 0, 1), new Vector3(-1, 0, 1) }, out _), Is.True);
            Assert.That(obj.TryRegister(out string error), Is.True, error); return obj;
        }
        private T Create<T>(string name) where T : Component
        {
            var go = new GameObject(name); _created.Add(go); T component = go.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null); return component;
        }
        private static AnchorPoseFrame Frame(long sequence, float bodyHeight = .9f) => new AnchorPoseFrame {
            sequence = sequence, timeSeconds = sequence, anchors = new[] {
                new AnchorPoseSample { anchorId = "navigation", worldPosition = new Vector3(.1f, 9, .2f), worldRotation = Quaternion.Euler(0, 37, 0) },
                new AnchorPoseSample { anchorId = "body-sample", worldPosition = new Vector3(.1f, bodyHeight, .2f), worldRotation = Quaternion.Euler(0, 37, 0) }
            }
        };
    }
}
#endif
