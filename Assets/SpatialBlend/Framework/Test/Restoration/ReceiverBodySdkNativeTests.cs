// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Oculus.Avatar2;
using SpatialBlend.Internal;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    // Only run with the full-project gate's explicitly supplied receiver-v1
    // native library. No native stubs, device, avatar service or Fusion runner.
    [Category("ReceiverBodySdkNative")]
    public sealed class ReceiverBodySdkNativeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<ReceiverBodySdkBinding> _bindings = new List<ReceiverBodySdkBinding>();
        private readonly List<RichRealizationMailboxEnvelope> _delivered = new List<RichRealizationMailboxEnvelope>();
        private SpatialBlendLocalWorld _world;
        private SpatialBlendActor _source, _receiver;
        private SbTransformInputManager _manager;
        private ReceiverInputWriter _writer;
        private ReceiverBodyInputBinding _input;
        private ReceiverBodySdkBinding _sdk;
        private IAnchorPoseFrameSink _sourceInput;
        private IDisposable _correspondence;
        private long _sequence;

        [SetUp]
        public void SetUp()
        {
            _world = Create<SpatialBlendLocalWorld>("sdk-body-world"); _sequence = 0;
            var sourceSupport = Support("source", "chair"); var receiverSupport = Support("receiver", "sofa");
            Assert.That(_world.TryRegisterSupportCorrespondence(sourceSupport, receiverSupport, out var link, out string error), Is.True, error);
            _correspondence = link;
            _source = Actor("source", true, sourceSupport); _receiver = Actor("receiver", false, receiverSupport);
            Assert.That(_source.TryCreateMotionInput(out _sourceInput), Is.True);
            // Facing is global: no scope is named.
            SpatialBlendAPI.Bind(_source.Registration.NativeSpace, _source.Registration.NativeActor,
                "head_facing", SbKernelType.Facing);
            var go = new GameObject("sdk-input-manager"); _objects.Add(go); go.SetActive(false);
            _manager = go.AddComponent<SbTransformInputManager>();
            Begin();
            _receiver.RichRealizationAvailable += _delivered.Add;
            Assert.That(_receiver.TryEnableRichRealization(new RichRealizationSubscriptionOptions {
                MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1 }, out error), Is.True, error);
        }

        [TearDown]
        public void TearDown()
        {
            _world.TestOnlyRejectReceiverClear = null;
            SetPublishFailure(null);
            _manager.OnBodyTrackingContextContextChanged.RemoveAllListeners();
            foreach (var binding in _bindings) binding.TryClose(out _);
            _input?.TryClose(out _); _manager.TryDetachArticulatedBodyTracking(out _);
            _correspondence?.Dispose(); _world.Shutdown();
            for (int i = _objects.Count - 1; i >= 0; --i) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear(); _bindings.Clear(); _delivered.Clear();
        }
        private void Begin()
        {
            Assert.That(_receiver.TryBeginReceiverInput(17, 19, out _writer, out string error), Is.True, error);
            Assert.That(_writer.TryReadAcquisitionClock(out long now, out error), Is.True, error);
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, ReceiverBodyFixture.Profile(_writer.Input, now),
                out _input, out error), Is.True, error);
        }
        private bool Bind(out string error)
        {
            bool bound = ReceiverBodySdkBinding.TryBind(_input, ReceiverSdkFixture.Profile(_input.Profile), _manager, out _sdk, out error);
            if (_sdk != null) _bindings.Add(_sdk);
            return bound;
        }
        private ReceiverBodyFrame Frame(ulong input = 1, long source = 0, bool root = true,
            ReceiverBodyJointPose[] joints = null) => ReceiverBodyFixture.Frame(_input.Profile, input, source, root: root, joints: joints);
        private RichRealizationSnapshot Solve()
        {
            int count = _delivered.Count;
            Assert.That(_sourceInput.TryApplyWorldFrame(new AnchorPoseFrame { sequence = _sequence++, timeSeconds = 10 + _sequence,
                anchors = new[] { new AnchorPoseSample { anchorId = "locomotion", worldPosition = new Vector3(.1f * _sequence, 0, .1f) },
                    new AnchorPoseSample { anchorId = "seat_contact", worldPosition = new Vector3(.1f, .05f, .2f) },
                    new AnchorPoseSample { anchorId = "head_facing", worldPosition = new Vector3(.3f, 1.6f, .1f) } } }), Is.True);
            _world.DrainRealizations(0);
            return _delivered.Skip(count).Last(e => e.Snapshot != null &&
                (e.Reason == RichMailboxReason.NativeSnapshot || e.Reason == RichMailboxReason.NativeRelease)).Snapshot;
        }
        private void AssertNativeWithdrawn(RichRealizationSnapshot receipt)
        {
            Assert.That(SpatialBlendAPI.TryGetReceiverInputValidity(receipt.Header.ProducerLifetime, receipt.Header.Revision,
                out var validity, out _, out string error), Is.True, error);
            Assert.That(validity.Valid, Is.False);
            Assert.That(validity.Current.Known, Is.False, "Native receiver input must actually be cleared, not only managed SDK state.");
        }

        [Test]
        public void OneBodyFeedsRealNativeFkAndActualSdkCallbacksWithNoLegacyProviderOrActorWrite()
        {
            Assert.That(Bind(out string error), Is.True, error);
            var frame = Frame(); var actorBefore = _receiver.transform.position; int receipts = _delivered.Count;
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(frame), out error), Is.True, error);
            Assert.That(_delivered.Count, Is.EqualTo(receipts), "Receiver observation ingress must not create source solves.");
            var receipt = Solve();
            Assert.That(_input.TryGetBodyForRealization(receipt, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(frame));
            var facing = receipt.Channels.Single(c => c.Anchor == "head_facing" && c.Family == RichRelationFamily.Facing);
            var head = frame.GeometricInput.Anchors.Single(a => a.Name == "head_facing").WorldPose.GetColumn(3);
            Assert.That(facing.AdmittedPosition.X, Is.EqualTo(head.x).Within(1e-5));
            Assert.That(facing.AdmittedPosition.Y, Is.EqualTo(head.z).Within(1e-5));
            Assert.That(facing.AdmittedPosition.Z, Is.EqualTo(head.y).Within(1e-5));
            Assert.That(ReceiverSdkFixture.ReadPose(_manager.ArticulatedBodyTrackingContext, 15, out var poses), Is.True);
            ReceiverSdkFixture.PoseEquals(poses[0], ReceiverSdkFixture.Calibration * frame.WorldJoints[0].WorldPose * ReceiverSdkFixture.Bones()[0].JointFromSdkBone);
            Assert.That(ReceiverSdkFixture.ReadState(_manager.ArticulatedBodyTrackingContext, out var state), Is.True);
            Assert.That(state.inputTrackingState.headset.position.x, Is.EqualTo(8));
            Assert.That(_manager.InputTrackingProvider, Is.Null); Assert.That(_manager.InputControlProvider, Is.Null);
            Assert.That(_manager.HandTrackingProvider, Is.Null);
            Assert.That(_receiver.transform.position, Is.EqualTo(actorBefore));
            Assert.That(_input.TrySubmit(Frame(2, 1), out _), Is.False);
            Assert.That(_input.TryClear(out _), Is.False);
            Assert.That(_manager.TryPublishArticulatedBodyTracking(ReceiverSdkFixture.Prepare(_sdk.Profile, ReceiverSdkFixture.Sample(Frame(2, 1))), out _), Is.False);
            Assert.That(_manager.ArticulatedBodyTrackingContext.ClearPose(out _), Is.False);
            Assert.Throws<InvalidOperationException>(() => _manager.ArticulatedBodyTrackingContext.Dispose());
        }

        [Test]
        public void MalformedSdkPayloadAndNativeRejectionPreservePriorFrameWithoutConsumingSourceSequence()
        {
            Assert.That(Bind(out string error), Is.True, error); var first = Frame();
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(first), out error), Is.True, error); var receipt = Solve();
            var next = Frame(2, 1);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(next, malformed: true), out _), Is.False);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(1, 1)), out _), Is.False, "Real native sequence gate rejects.");
            Assert.That(_input.TryGetBodyForRealization(receipt, out var retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(first)); Assert.That(_manager.ArticulatedBodyTrackingContext.HasPublishedPose, Is.True);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(next), out error), Is.True, error);
            Assert.That(_input.TryGetBodyForRealization(receipt, out _, out _), Is.False);
            Assert.That(_input.TryGetBodyForRealization(Solve(), out retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(next));
        }

        [TestCase(false)][TestCase(true)]
        public void ExplicitJointOrRootLossReachesNativeAndWithdrawsWholeSdkPose(bool rootLoss)
        {
            Assert.That(Bind(out string error), Is.True, error);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame()), out error), Is.True, error);
            var joints = ReceiverBodyFixture.Joints();
            if (!rootLoss) joints[1] = new ReceiverBodyJointPose("chest", 1, false, Matrix4x4.identity);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(2, 1, root: !rootLoss, joints: joints), malformed: true), out error), Is.True, error);
            Assert.That(_manager.ArticulatedBodyTrackingContext.HasPublishedPose, Is.False);
            Assert.That(ReceiverSdkFixture.ReadPose(_manager.ArticulatedBodyTrackingContext, 15, out _), Is.False);
            var lost = Solve();
            Assert.That(lost.Channels.Any(c => c.Anchor == "head_facing" && c.HasGoal), Is.False);
            if (rootLoss) Assert.That(lost.Header.SnapshotStatus, Is.EqualTo(RichSnapshotStatus.Failed));
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(3, 2)), out error), Is.True, error);
            Assert.That(_manager.ArticulatedBodyTrackingContext.HasPublishedPose, Is.True);
        }

        [TestCase("clear")][TestCase("disable")][TestCase("detach")][TestCase("destroy")]
        public void ManagerLifecycleWithdrawsNativeAndSdkAndNeverFallsBack(string operation)
        {
            Assert.That(Bind(out string error), Is.True, error);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame()), out error), Is.True, error); var receipt = Solve();
            var context = _manager.ArticulatedBodyTrackingContext;
            switch (operation)
            {
                case "clear": Assert.That(_manager.TryClearArticulatedBodyTracking(out error), Is.True, error); break;
                case "disable": typeof(SbTransformInputManager).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_manager, null); break;
                case "detach": Assert.That(_manager.TryDetachArticulatedBodyTracking(out error), Is.True, error); break;
                case "destroy": typeof(SbTransformInputManager).GetMethod("OnDestroyCalled", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_manager, null); break;
            }
            Assert.That(context.HasPublishedPose, Is.False); AssertNativeWithdrawn(receipt);
            Assert.That(_manager.InputControlProvider, Is.Null); Assert.That(_manager.InputTrackingProvider, Is.Null);
            Assert.That(_manager.HandTrackingProvider, Is.Null);
            if (operation == "clear" || operation == "disable")
            {
                Assert.That(_sdk.IsReady, Is.True);
                Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(2, 0)), out _), Is.False);
                Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(2, 1)), out error), Is.True, error);
            }
            else { Assert.That(_sdk.IsClosed, Is.True); Assert.That(_manager.BodyTrackingContext, Is.Null); }
        }

        [Test]
        public void NativeAndSdkFailureCleanupRetriesAndCannotLeaveOldBodyAvailable()
        {
            Assert.That(Bind(out string error), Is.True, error);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame()), out error), Is.True, error); var receipt = Solve();
            var context = _manager.ArticulatedBodyTrackingContext;
            SetPublishFailure(() => true);
            int attempts = 0; _world.TestOnlyRejectReceiverClear = () => ++attempts <= 2;
            UnityAction<OvrAvatarInputManagerBehavior> listener = _ => throw new InvalidOperationException("intentional detach rejection");
            _manager.OnBodyTrackingContextContextChanged.AddListener(listener);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(2, 1)), out error), Is.False);
            Assert.That(_sdk.IsReady || _sdk.IsClosed, Is.False); Assert.That(context.HasPublishedPose, Is.False);
            Assert.That(_input.TryGetBodyForRealization(receipt, out _, out _), Is.False);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame(3, 2)), out _), Is.False);
            _world.TestOnlyRejectReceiverClear = null; SetPublishFailure(null);
            _manager.OnBodyTrackingContextContextChanged.RemoveListener(listener);
            Assert.That(_sdk.TryClose(out error), Is.True, error); Assert.That(_sdk.IsClosed, Is.True);
            Assert.That(_manager.ArticulatedBodyTrackingContext, Is.Null); AssertNativeWithdrawn(receipt);
        }

        [Test]
        public void FailedAttachReturnsExactCleanupHandleAndDoesNotDetachAnExistingOwner()
        {
            UnityAction<OvrAvatarInputManagerBehavior> listener = _ => throw new InvalidOperationException("intentional attach rejection");
            _manager.OnBodyTrackingContextContextChanged.AddListener(listener);
            Assert.That(Bind(out _), Is.False); Assert.That(_sdk, Is.Not.Null);
            Assert.That(_sdk.IsReady || _sdk.IsClosed, Is.False);
            _manager.OnBodyTrackingContextContextChanged.RemoveListener(listener);
            Assert.That(_sdk.TryClose(out string error), Is.True, error);
            Begin(); Assert.That(Bind(out error), Is.True, error); var owner = _sdk; var context = _manager.ArticulatedBodyTrackingContext;
            // Distinct fresh body binding, same occupied SDK manager: failed
            // configuration must not clear/dispose the existing context.
            Begin(); Assert.That(Bind(out _), Is.False); Assert.That(_sdk.IsClosed, Is.True);
            Assert.That(_manager.ArticulatedBodyTrackingContext, Is.SameAs(context));
            Assert.That(context.IsConfigured, Is.True); Assert.That(owner.TryClose(out error), Is.True, error);
        }

        [Test]
        public void DestroyedManagerListenerFailureRetainsClosedInputAndExplicitRetryBeforeSdkDisposal()
        {
            Assert.That(Bind(out string error), Is.True, error);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(Frame()), out error), Is.True, error); var receipt = Solve();
            var context = _manager.ArticulatedBodyTrackingContext;
            bool laterListenerDetached = false;
            UnityAction<OvrAvatarInputManagerBehavior> throwing = _ => throw new InvalidOperationException("intentional destroy listener failure");
            UnityAction<OvrAvatarInputManagerBehavior> later = value =>
            {
                Assert.That(value.BodyTrackingContext, Is.Null);
                Assert.That(context.IsConfigured, Is.True, "A possibly installed callback registry must survive until this detach observer executes.");
                Assert.That(context.HasPublishedPose, Is.False);
                laterListenerDetached = true;
            };
            _manager.OnBodyTrackingContextContextChanged.AddListener(throwing);
            _manager.OnBodyTrackingContextContextChanged.AddListener(later);
            LogAssert.Expect(LogType.Warning, new Regex("Retained articulated body-tracking context during input-manager destruction:"));
            // Inactive EditMode objects omit Unity's OnDestroy. Invoke the real
            // SDK base callback, then actually destroy its Unity object. No native
            // avatar entity is claimed by the later observer below.
            typeof(OvrAvatarInputManager).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_manager, null);
            UnityEngine.Object.DestroyImmediate(_manager.gameObject);
            Assert.That(_manager == null, Is.True, "The Unity manager is actually destroyed; its owned C# cleanup handle survives.");
            Assert.That(_sdk.IsReady || _sdk.IsClosed, Is.False);
            Assert.That(laterListenerDetached, Is.False); Assert.That(context.IsConfigured, Is.True);
            Assert.That(context.HasPublishedPose, Is.False); AssertNativeWithdrawn(receipt);
            Assert.That(ReceiverSdkFixture.ReadPose(context, 15, out _), Is.False);
            _manager.OnBodyTrackingContextContextChanged.RemoveListener(throwing);
            Assert.That(_sdk.TryClose(out error), Is.True, error);
            Assert.That(laterListenerDetached, Is.True); Assert.That(context.IsConfigured, Is.False);
            Assert.That(_sdk.IsClosed, Is.True); Assert.That(_sdk.TryClose(out error), Is.True, error);
        }

        [Test]
        public void ReplacementEpochInvalidatesCallbacksImmediatelyAndOldCleanupCannotTouchReplacement()
        {
            Assert.That(Bind(out string error), Is.True, error); var old = _sdk;
            Assert.That(old.TrySubmit(ReceiverSdkFixture.Sample(Frame()), out error), Is.True, error);
            var oldContext = _manager.ArticulatedBodyTrackingContext;
            Begin(); Assert.That(oldContext.HasPublishedPose, Is.False, "Native Begin closes the old writer before another SDK publication.");
            Assert.That(_manager.TryDetachArticulatedBodyTracking(out error), Is.True, error);
            Assert.That(Bind(out error), Is.True, error); var frame = Frame();
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(frame), out error), Is.True, error); var receipt = Solve();
            var current = _manager.ArticulatedBodyTrackingContext;
            Assert.That(old.TryClose(out error), Is.True, error);
            Assert.That(_manager.ArticulatedBodyTrackingContext, Is.SameAs(current)); Assert.That(current.HasPublishedPose, Is.True);
            Assert.That(_input.TryGetBodyForRealization(receipt, out var body, out error), Is.True, error); Assert.That(body, Is.SameAs(frame));
        }

        [Test]
        public void WorkerCannotSubmitClearOrCloseButCallbackUsesActualNativeExpiryWithoutUpdate()
        {
            Assert.That(Bind(out string error), Is.True, error);
            // Use 150ms validity; it is still measured from the original source
            // mapping, never from a later SDK poll or test delay.
            long observed = _input.Profile.ClockMapping.NativeOriginNanoseconds;
            var frame = ReceiverBodyFixture.Frame(_input.Profile, expiry: observed + 150000000);
            Assert.That(_sdk.TrySubmit(ReceiverSdkFixture.Sample(frame), out error), Is.True, error);
            var context = _manager.ArticulatedBodyTrackingContext; bool submit = true, clear = true, close = true, first = false, late = true;
            var next = ReceiverSdkFixture.Sample(Frame(2, 1)); Exception unexpected = null;
            var worker = new Thread(() =>
            {
                try
                {
                    submit = _sdk.TrySubmit(next, out _); clear = _sdk.TryClear(out _); close = _sdk.TryClose(out _);
                    first = ReceiverSdkFixture.ReadPose(context, 15, out _);
                    Thread.Sleep(180); // Bounded test delay, no Unity Update or source delivery.
                    late = ReceiverSdkFixture.ReadPose(context, 15, out _);
                }
                catch (Exception e) { unexpected = e; }
            });
            worker.Start(); Assert.That(worker.Join(5000), Is.True); Assert.That(unexpected, Is.Null);
            Assert.That(submit || clear || close, Is.False); Assert.That(first, Is.True); Assert.That(late, Is.False);
            Assert.That(context.HasPublishedPose, Is.False); Assert.That(_sdk.IsReady, Is.True);
        }

        private void SetPublishFailure(Func<bool> failure) => typeof(SbTransformInputManager).GetField(
            "TestOnlyRejectReceiverBodyPublish", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_manager, failure);
        // ABI 4: scope belongs to the relation family, so the
        // support is a descendant of the actor's own element (its owner object), not an app-declared scope.
        private readonly System.Collections.Generic.Dictionary<SpatialBlendObject, SpatialBlendObject> _supportOwner =
            new System.Collections.Generic.Dictionary<SpatialBlendObject, SpatialBlendObject>();
        private SpatialBlendActor Actor(string space, bool source, SpatialBlendObject support)
        {
            SpatialBlendObject owner = _supportOwner[support];
            var actor = Create<SpatialBlendActor>(space + "-actor");
            actor.Configure(owner, "participant", source, new[] {
                new SpatialBlendActorAnchor("locomotion", SpatialBlendActorAnchorRole.Root, Vector3.zero),
                new SpatialBlendActorAnchor("head_facing", SpatialBlendActorAnchorRole.Body, Vector3.zero),
                new SpatialBlendActorAnchor("seat_contact", SpatialBlendActorAnchorRole.Body, Vector3.zero) });
            Assert.That(actor.TryRegister(out string error), Is.True, error); return actor;
        }
        private SpatialBlendObject Support(string space, string id)
        {
            var owner = Create<SpatialBlendObject>(space + "-owner"); owner.Configure(_world, space, space + "-actor-owner");
            var support = Create<SpatialBlendObject>(id); support.Configure(_world, space, id, owner, SpatialBlendObjectKind.Affordance);
            Assert.That(support.TrySetSupportPolygon(new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1),
                new Vector3(1, 0, 1), new Vector3(-1, 0, 1) }, out string error), Is.True, error);
            Assert.That(support.TryRegister(out error), Is.True, error);
            _supportOwner[support] = owner; return support;
        }
        private T Create<T>(string name) where T : Component
        {
            var go = new GameObject(name); _objects.Add(go); T component = go.AddComponent<T>();
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            return component;
        }
    }
}
#endif
