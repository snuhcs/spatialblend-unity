// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Covers the local actor-to-original-retarget seam without constructing a Meta
    /// avatar, a mesh, or a native Foundation registration.  Root delivery is
    /// invoked at the consumer callback boundary so its owner/generation fence can
    /// be tested without pretending an unregistered actor produced native output.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FoundationAvatarRetargetEntityTests
    {
        private GameObject _root;
        private SpatialBlendActor _receiver;
        private AnchorTargetController _targets;
        private SbRetargetTransform _retarget;
        private FoundationAvatarRetargetEntity _consumer;
        private SbTransformInputManager _input;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("foundation-avatar-retarget");
            _receiver = _root.AddComponent<SpatialBlendActor>();
            _targets = _root.AddComponent<AnchorTargetController>();
            _retarget = _root.AddComponent<SbRetargetTransform>();
            _retarget.headTransform = NewPart("head");
            _retarget.leftHandTransform = NewPart("left");
            _retarget.rightHandTransform = NewPart("right");
            _consumer = _root.AddComponent<FoundationAvatarRetargetEntity>();
            _input = _root.AddComponent<SbTransformInputManager>();
            _input.SbTransform = _retarget;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void AcceptedFrame_MapsSourceRootChartAndDrivesIndependentSdkChannels()
        {
            Pose sourceReference = new Pose(new Vector3(10f, 0f, 3f), Quaternion.Euler(0f, 20f, 0f));
            Pose receiverReference = new Pose(new Vector3(-4f, 1f, 8f), Quaternion.Euler(0f, 140f, 0f));
            Configure(sourceReference, receiverReference);

            Pose sourceRoot = new Pose(new Vector3(14f, 2f, -1f), Quaternion.Euler(0f, 70f, 0f));
            Pose head = new Pose(new Vector3(15f, 3.5f, 1f), Quaternion.Euler(15f, 35f, 5f));
            Pose left = new Pose(new Vector3(13f, 2.7f, -2f), Quaternion.Euler(5f, -30f, 10f));
            Pose right = new Pose(new Vector3(16f, 2.2f, -3f), Quaternion.Euler(-10f, 75f, -20f));
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("root", sourceRoot), Sample("head", head), Sample("hand_l", left),
                Sample("hand_r", right)), out string error), Is.True, error);

            AssertPose(_retarget.headTransform, Map(receiverReference, sourceRoot, head));
            AssertPose(_retarget.leftHandTransform, Map(receiverReference, sourceRoot, left));
            AssertPose(_retarget.rightHandTransform, Map(receiverReference, sourceRoot, right));
            Assert.That(HeadsetActive(), Is.True);
            Assert.That(LeftActive(), Is.True);
            Assert.That(RightActive(), Is.True);

            // The next full snapshot deliberately omits root.  It must retain the
            // last source chart (rather than fall back to sourceReference), while
            // still accepting independently updated observed channels.
            Pose laterHead = new Pose(new Vector3(14.5f, 4f, .5f), Quaternion.Euler(0f, 10f, 0f));
            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", laterHead)), out error), Is.True, error);
            AssertPose(_retarget.headTransform, Map(receiverReference, sourceRoot, laterHead));
            Assert.That(LeftActive(), Is.False);
            Assert.That(RightActive(), Is.False);
        }

        [Test]
        public void DefaultChartRejectsSeatButExplicitFrozenFiveAnchorChartAdmitsItWithoutChangingSdkMapping()
        {
            Configure(Pose.identity, Pose.identity);
            AnchorPoseFrame fiveAnchorFrame = Frame(0, 0d,
                Sample("root", new Pose(Vector3.zero, Quaternion.identity)),
                Sample("head", new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(0f, 15f, 0f))),
                Sample("hand_l", new Pose(new Vector3(-.2f, 1.2f, 0f), Quaternion.identity)),
                Sample("hand_r", new Pose(new Vector3(.2f, 1.2f, 0f), Quaternion.identity)),
                Sample("seat_contact", new Pose(new Vector3(0f, .5f, .3f), Quaternion.identity)));
            Assert.That(_consumer.TryApplyObservationFrame(fiveAnchorFrame, out string error), Is.False);
            Assert.That(HeadsetActive(), Is.False, "rejected unknown channels must not partially apply head input");

            string[] admitted = { "root", "head", "hand_l", "hand_r", "seat_contact" };
            Assert.That(_consumer.TryConfigure(_receiver, _targets, _retarget, Pose.identity,
                Pose.identity, "root", admitted, out error), Is.True, error);
            admitted[4] = "mutated_after_configure";
            Assert.That(_consumer.TryApplyObservationFrame(fiveAnchorFrame, out error), Is.True, error);
            AssertPose(_retarget.headTransform, new Pose(new Vector3(0f, 1.6f, 0f),
                Quaternion.Euler(0f, 15f, 0f)));
            AssertPose(_retarget.leftHandTransform, new Pose(new Vector3(-.2f, 1.2f, 0f),
                Quaternion.identity));
            AssertPose(_retarget.rightHandTransform, new Pose(new Vector3(.2f, 1.2f, 0f),
                Quaternion.identity));
            Assert.That(HeadsetActive() && LeftActive() && RightActive(), Is.True);

            Pose headBeforeUnknown = new Pose(_retarget.headTransform.position, _retarget.headTransform.rotation);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("not_declared", new Pose(Vector3.one, Quaternion.identity))), out error), Is.False);
            AssertPose(_retarget.headTransform, headBeforeUnknown,
                "unknown admission failure must not advance or partially mutate the SDK inputs");
            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", new Pose(Vector3.up * 2f, Quaternion.identity))), out error), Is.True,
                "the same sequence must retry after an atomic unknown-ID rejection");
            Assert.That(HeadsetActive(), Is.True);
            Assert.That(LeftActive() || RightActive(), Is.False,
                "omitted measured channels clear independently even when seat is admitted");
            Assert.That(_consumer.TryApplyObservationFrame(Frame(2, 2d), out error), Is.True, error);
            Assert.That(HeadsetActive() || LeftActive() || RightActive(), Is.False,
                "an empty full frame clears measured channels; it does not reinterpret seat as input");
        }

        [Test]
        public void InvalidAdmittedChartDoesNotReleaseTheLiveOwner()
        {
            Configure(Pose.identity, Pose.identity);
            string[] invalid = { "root", "head", "hand_l", "hand_l" };
            Assert.That(_consumer.TryConfigure(_receiver, _targets, _retarget, Pose.identity,
                Pose.identity, "root", invalid, out string error), Is.False);
            Assert.That(_consumer.IsConfigured, Is.True);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(Vector3.up, Quaternion.identity))), out error), Is.True, error);
            Assert.That(HeadsetActive(), Is.True);
        }

        [Test]
        public void TargetOverrides_DoNotBecomeTrackingAndPartialWeightDoesNotAccumulate()
        {
            Configure(Pose.identity, Pose.identity);
            _targets.ApplyTarget("head", new Vector3(10f, 0f, 0f), 0f, true, .5d, 1d);
            Assert.That(HeadsetActive(), Is.False, "a C target is not a headset observation");

            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(Vector3.zero, Quaternion.identity)),
                Sample("hand_r", new Pose(Vector3.right, Quaternion.Euler(0f, 35f, 0f)))),
                out string error), Is.True, error);
            AssertPose(_retarget.headTransform, new Pose(new Vector3(5f, 0f, 0f), Quaternion.identity));
            AssertPose(_retarget.rightHandTransform,
                new Pose(Vector3.right, Quaternion.Euler(0f, 35f, 0f)));
            Assert.That(HeadsetActive(), Is.True);

            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", new Pose(Vector3.zero, Quaternion.identity)),
                Sample("hand_r", new Pose(Vector3.right, Quaternion.Euler(0f, 35f, 0f)))),
                out error), Is.True, error);
            _targets.ApplyTarget("head", new Vector3(10f, 0f, 0f), 0f, true, .5d, 2d);
            AssertPose(_retarget.headTransform, new Pose(new Vector3(5f, 0f, 0f), Quaternion.identity),
                "target output must never become the next tracked baseline");
            AssertPose(_retarget.rightHandTransform,
                new Pose(Vector3.right, Quaternion.Euler(0f, 35f, 0f)),
                "a head target must not rotate an independently observed hand");
        }

        [Test]
        public void PartialAndEmptyFrames_ClearOnlyOmittedChannelsAndResetFrameClock()
        {
            Configure(Pose.identity, Pose.identity);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(Vector3.up, Quaternion.identity)),
                Sample("hand_l", new Pose(Vector3.left, Quaternion.identity)),
                Sample("hand_r", new Pose(Vector3.right, Quaternion.identity))), out string error), Is.True, error);

            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", new Pose(Vector3.up * 2f, Quaternion.identity)),
                Sample("hand_r", new Pose(Vector3.right * 2f, Quaternion.Euler(0f, 40f, 0f)))), out error), Is.True, error);
            Assert.That(HeadsetActive(), Is.True);
            Assert.That(LeftActive(), Is.False, "omitted left channel must clear without touching right");
            Assert.That(RightActive(), Is.True);

            Assert.That(_consumer.TryApplyObservationFrame(Frame(2, 2d), out error), Is.True, error);
            Assert.That(HeadsetActive(), Is.False);
            Assert.That(RightActive(), Is.False);
            _consumer.ClearObservations();
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("hand_l", new Pose(new Vector3(-2f, 0f, 0f), Quaternion.identity))), out error),
                Is.True, "reset must clear the sequence/time baseline");
            Assert.That(LeftActive(), Is.True);
        }

        [Test]
        public void InvalidFrameIsAtomicAndRootDeliveryMovesChartWithoutFabricatingHeadInput()
        {
            Configure(Pose.identity, new Pose(new Vector3(1f, 0f, 0f), Quaternion.identity));
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f)))),
                out string error), Is.True, error);
            Pose before = new Pose(_retarget.headTransform.position, _retarget.headTransform.rotation);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", new Pose(Vector3.zero, Quaternion.identity)),
                Sample("head", new Pose(Vector3.one, Quaternion.identity))), out error), Is.False);
            AssertPose(_retarget.headTransform, before);

            InvokeRoot(_consumer, _receiver, CurrentGeneration(_consumer),
                new SpatialBlendRootTarget(new Vector3(10f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f), 0d, 0d));
            Assert.That(Vector3.Distance(before.position, _retarget.headTransform.position), Is.GreaterThan(.01f),
                "a receiver realization changes the retained root chart, not the observation flag");
            Assert.That(HeadsetActive(), Is.True);
            _consumer.ClearObservations();
            Assert.That(HeadsetActive(), Is.False);
        }

        [Test]
        public void DestroyedRetargetDetachesConsumerAndRejectsLaterFramesWithoutThrowing()
        {
            Configure(Pose.identity, Pose.identity);
            Object.DestroyImmediate(_retarget);
            bool accepted = true;
            Assert.DoesNotThrow(() => accepted = _consumer.TryApplyObservationFrame(Frame(0, 0d), out _));
            Assert.That(accepted, Is.False);
            Assert.That(_consumer.IsConfigured, Is.False);
        }

        [Test]
        public void Release_KeepsFoundationPresenceExplicitWhenSdkInputRemainsLive()
        {
            Configure(Pose.identity, Pose.identity);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(Vector3.up, Quaternion.identity)),
                Sample("hand_l", new Pose(Vector3.left, Quaternion.identity)),
                Sample("hand_r", new Pose(Vector3.right, Quaternion.identity))), out string error),
                Is.True, error);
            _consumer.Release();

            Assert.That(HeadsetActive(), Is.False);
            Assert.That(LeftActive(), Is.False);
            Assert.That(RightActive(), Is.False,
                "removing the local bridge must not revive legacy transform-presence flags");
        }

        [Test]
        public void LateForeignBridgeAndItsDestroy_CannotClearLeasedFoundationOutputs()
        {
            Configure(Pose.identity, Pose.identity);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(new Vector3(2f, 1f, 0f), Quaternion.Euler(0f, 25f, 0f))),
                Sample("hand_l", new Pose(Vector3.left, Quaternion.identity)),
                Sample("hand_r", new Pose(Vector3.right, Quaternion.identity))), out string error),
                Is.True, error);
            Pose before = new Pose(_retarget.headTransform.position, _retarget.headTransform.rotation);
            var foreignRoot = new GameObject("late-foreign-bridge");
            try
            {
                var foreignTargets = foreignRoot.AddComponent<AnchorTargetController>();
                var foreign = foreignRoot.AddComponent<SbRetargetAnchorTargetBridge>();
                foreign.Configure(foreignTargets, _retarget);
                foreignTargets.ApplyTarget("head", new Vector3(99f, 0f, 0f), 0f,
                    true, 1d, 0d);
                Object.DestroyImmediate(foreignRoot);

                AssertPose(_retarget.headTransform, before);
                Assert.That(HeadsetActive(), Is.True);
                Assert.That(LeftActive(), Is.True);
                Assert.That(RightActive(), Is.True);
            }
            finally
            {
                if (foreignRoot != null) Object.DestroyImmediate(foreignRoot);
            }
        }

        [Test]
        public void ReconfigureThenOldBridgeDestroy_CannotClearReplacementOwner()
        {
            Configure(Pose.identity, Pose.identity);
            SbRetargetAnchorTargetBridge retired =
                _retarget.GetComponent<SbRetargetAnchorTargetBridge>();
            Assert.That(retired, Is.Not.Null);
            Assert.That(_consumer.TryConfigure(_receiver, _targets, _retarget, Pose.identity,
                Pose.identity, "root", out string error), Is.True, error);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", new Pose(Vector3.up, Quaternion.identity))), out error), Is.True, error);
            Pose before = new Pose(_retarget.headTransform.position, _retarget.headTransform.rotation);

            // EditMode destruction is immediate, unlike a deferred play-frame
            // destroy. Invoke the retired component's cleanup at the equivalent
            // late point; its explicit detach and retarget identity gate must make
            // this a no-op for the replacement lease.
            InvokeBridgeDestroy(retired);
            AssertPose(_retarget.headTransform, before);
            Assert.That(HeadsetActive(), Is.True,
                "a stale bridge cleanup must be fenced from the replacement bridge lease");
        }

        [Test]
        public void MappingOverflowOrDestroyedDestination_IsAtomicBeforeHeadMutation()
        {
            Pose hugeSource = new Pose(new Vector3(-float.MaxValue, 0f, 0f), Quaternion.identity);
            Configure(hugeSource, Pose.identity);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                Sample("head", hugeSource)), out string error), Is.True, error);
            Pose before = new Pose(_retarget.headTransform.position, _retarget.headTransform.rotation);

            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", hugeSource),
                Sample("hand_l", new Pose(new Vector3(float.MaxValue, 0f, 0f), Quaternion.identity))),
                out error), Is.False);
            AssertPose(_retarget.headTransform, before, "overflow in another channel must not commit head");
            Assert.That(_consumer.TryApplyObservationFrame(Frame(1, 1d,
                Sample("head", hugeSource)), out error), Is.True,
                "a rejected frame must not consume its sequence/time watermark: " + error);

            Object.DestroyImmediate(_retarget.leftHandTransform.gameObject);
            Assert.That(_consumer.TryApplyObservationFrame(Frame(2, 2d,
                Sample("head", new Pose(new Vector3(-float.MaxValue, 1f, 0f), Quaternion.identity))),
                out error), Is.False);
            AssertPose(_retarget.headTransform, before, "a missing hand destination must reject before head write");
        }

        [Test]
        public void ReconfigureRejectsStaleRootCallbackAndReleasesOnlyOwnedBridge()
        {
            Configure(Pose.identity, Pose.identity);
            long oldGeneration = CurrentGeneration(_consumer);
            SpatialBlendActor oldReceiver = _receiver;
            var replacement = new GameObject("replacement-receiver");
            var replacementTargets = replacement.AddComponent<AnchorTargetController>();
            var replacementRetarget = replacement.AddComponent<SbRetargetTransform>();
            replacementRetarget.headTransform = NewPartOn(replacement.transform, "head");
            replacementRetarget.leftHandTransform = NewPartOn(replacement.transform, "left");
            replacementRetarget.rightHandTransform = NewPartOn(replacement.transform, "right");
            var replacementReceiver = replacement.AddComponent<SpatialBlendActor>();
            try
            {
                Assert.That(_consumer.TryConfigure(replacementReceiver, replacementTargets,
                    replacementRetarget, Pose.identity,
                    new Pose(new Vector3(3f, 0f, 0f), Quaternion.identity), "root", out string error),
                    Is.True, error);
                Assert.That(_retarget.GetComponent<SbRetargetAnchorTargetBridge>(), Is.Null,
                    "reconfiguration must remove only the consumer-owned old attachment");
                InvokeRoot(_consumer, oldReceiver, oldGeneration,
                    new SpatialBlendRootTarget(new Vector3(99f, 0f, 0f), Quaternion.identity, 0d, 0d));
                AssertPose(replacementRetarget.headTransform,
                    new Pose(new Vector3(3f, 0f, 0f), Quaternion.identity));
            }
            finally
            {
                Object.DestroyImmediate(replacement);
            }
        }

        [Test]
        public void OwnershipRejectsSecondConsumerAndAnActiveForeignBridgeWithoutDisturbingLiveOwner()
        {
            Configure(Pose.identity, Pose.identity);
            var secondObject = new GameObject("second-foundation-consumer");
            var second = secondObject.AddComponent<FoundationAvatarRetargetEntity>();
            try
            {
                Assert.That(second.TryConfigure(_receiver, _targets, _retarget, Pose.identity,
                    Pose.identity, "root", out string error), Is.False);
                Assert.That(_consumer.IsConfigured, Is.True);

                Assert.That(_consumer.TryConfigure(null, _targets, _retarget, Pose.identity,
                    Pose.identity, "root", out error), Is.False,
                    "a rejected prospective configuration must preserve the live owner");
                Assert.That(_consumer.TryApplyObservationFrame(Frame(0, 0d,
                    Sample("head", new Pose(Vector3.zero, Quaternion.identity))), out error), Is.True, error);

                _consumer.Release();
                Assert.That(second.TryConfigure(_receiver, _targets, _retarget, Pose.identity,
                    Pose.identity, "root", out error), Is.True, error);
                _consumer.Release();
                Assert.That(second.TryApplyObservationFrame(Frame(0, 0d,
                    Sample("hand_r", new Pose(Vector3.right, Quaternion.identity))), out error), Is.True,
                    "an old component's repeat Release must not clear its replacement");
            }
            finally
            {
                Object.DestroyImmediate(secondObject);
            }

            var foreignObject = new GameObject("foreign-retarget-bridge");
            var foreign = foreignObject.AddComponent<SbRetargetAnchorTargetBridge>();
            var replacement = new GameObject("foreign-bridge-consumer");
            var replacementConsumer = replacement.AddComponent<FoundationAvatarRetargetEntity>();
            try
            {
                foreign.Configure(_targets, _retarget);
                Assert.That(replacementConsumer.TryConfigure(_receiver, _targets, _retarget,
                    Pose.identity, Pose.identity, "root", out string error), Is.False,
                    "an active bridge on another object owns retarget overrides too");
            }
            finally
            {
                Object.DestroyImmediate(replacement);
                Object.DestroyImmediate(foreignObject);
            }
        }

        private void Configure(Pose source, Pose receiver)
        {
            Assert.That(_consumer.TryConfigure(_receiver, _targets, _retarget, source, receiver,
                "root", out string error), Is.True, error);
        }

        private Transform NewPart(string name) => NewPartOn(_root.transform, name);

        private static Transform NewPartOn(Transform parent, string name)
        {
            var part = new GameObject(name).transform;
            part.SetParent(parent, false);
            return part;
        }

        private static AnchorPoseFrame Frame(long sequence, double time,
                                             params AnchorPoseSample[] samples)
        {
            return new AnchorPoseFrame { sequence = sequence, timeSeconds = time, anchors = samples };
        }

        private static AnchorPoseSample Sample(string id, Pose pose)
        {
            return new AnchorPoseSample
            {
                anchorId = id,
                worldPosition = pose.position,
                worldRotation = pose.rotation
            };
        }

        private static Pose Map(Pose placement, Pose sourceChart, Pose observation)
        {
            Quaternion inverse = Quaternion.Inverse(sourceChart.rotation);
            return new Pose(placement.position + placement.rotation *
                (inverse * (observation.position - sourceChart.position)),
                placement.rotation * inverse * observation.rotation);
        }

        private bool HeadsetActive()
        {
            var tracking = new SbTransformTrackingDelegate(_input);
            tracking.GetRawInputTrackingState(out var state);
            return state.headsetActive;
        }

        private bool LeftActive()
        {
            var tracking = new SbTransformTrackingDelegate(_input);
            tracking.GetRawInputTrackingState(out var state);
            return state.leftControllerActive;
        }

        private bool RightActive()
        {
            var tracking = new SbTransformTrackingDelegate(_input);
            tracking.GetRawInputTrackingState(out var state);
            return state.rightControllerActive;
        }

        private static long CurrentGeneration(FoundationAvatarRetargetEntity consumer)
        {
            FieldInfo field = typeof(FoundationAvatarRetargetEntity).GetField("_generation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)field.GetValue(consumer);
        }

        private static void InvokeRoot(FoundationAvatarRetargetEntity consumer, SpatialBlendActor receiver,
                                       long generation, SpatialBlendRootTarget target)
        {
            MethodInfo method = typeof(FoundationAvatarRetargetEntity).GetMethod("OnRootTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(consumer, new object[] { receiver, generation, target });
        }

        private static void InvokeBridgeDestroy(SbRetargetAnchorTargetBridge bridge)
        {
            MethodInfo method = typeof(SbRetargetAnchorTargetBridge).GetMethod("OnDestroy",
                BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(bridge, null);
        }

        private static void AssertPose(Transform actual, Pose expected, string message = null)
        {
            AssertPose(new Pose(actual.position, actual.rotation), expected, message);
        }

        private static void AssertPose(Pose actual, Pose expected, string message = null)
        {
            Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThan(1e-4f), message);
            Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThan(.01f), message);
        }
    }
}
#endif
