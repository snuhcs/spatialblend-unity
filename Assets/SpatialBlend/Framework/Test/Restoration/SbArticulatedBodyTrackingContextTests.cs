// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Oculus.Avatar2;
using UnityEngine;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SbArticulatedBodyTrackingContextTests
    {
        private readonly List<SbArticulatedBodyTrackingContext> _contexts =
            new List<SbArticulatedBodyTrackingContext>();

        [TearDown]
        public void DisposeOwnedContexts()
        {
            foreach (var context in _contexts) context.Dispose();
            _contexts.Clear();
        }

        private SbArticulatedBodyTrackingContext CreateContext()
        {
            var context = new SbArticulatedBodyTrackingContext();
            _contexts.Add(context);
            return context;
        }

        [Test]
        public void Configure_FreezesOneValidatedLocalSkeletonForContextLifetime()
        {
            var context = CreateContext();
            CAPI.ovrAvatar2Bone[] bones = Bones();
            CAPI.ovrAvatar2Transform[] reference = Transforms();
            Assert.That(context.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                7, new CAPI.ovrAvatar2Vector3f { x = 0f, y = 0f, z = 1f }, bones, reference),
                out string error), Is.True, error);
            bones[1].parentBoneIndex = -1;
            reference[0].position.x = 99f;
            var frozen = PrivateField<SbArticulatedBodyTrackingContext.SkeletonDefinition>(context, "_skeleton");
            Assert.That(frozen.Bones[1].parentBoneIndex, Is.EqualTo(0));
            Assert.That(frozen.LocalReferencePose[0].position.x, Is.EqualTo(0f));
            Assert.That(context.TryPublish(Snapshot(0, Transforms()), out error), Is.True, error);
            var body = new OvrAvatarTrackingBodyState();
            Assert.That(ReadBodyState(context, body), Is.True);
            Assert.That(body.skeletonVersion, Is.EqualTo(7));
            Assert.That(body.numBones, Is.EqualTo(2));
            Assert.That(context.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                8, new CAPI.ovrAvatar2Vector3f { z = 1f }, Bones(), Transforms()), out error), Is.False);
        }

        [Test]
        public void Configure_RejectsUndefinedBoneIdsAndInvalidHierarchyBeforePublication()
        {
            var context = CreateContext();
            CAPI.ovrAvatar2Bone[] bones = Bones();
            bones[1].boneId = CAPI.ovrAvatar2JointType.Count;
            Assert.That(context.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                1, new CAPI.ovrAvatar2Vector3f { z = 1f }, bones, Transforms()), out _), Is.False);

            context = CreateContext();
            bones = Bones();
            bones[1].parentBoneIndex = 1;
            Assert.That(context.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                1, new CAPI.ovrAvatar2Vector3f { z = 1f }, bones, Transforms()), out _), Is.False);
        }

        [Test]
        public void Publish_RequiresCompleteAtomicExplicitSnapshotAndPreservesMonotonicSequenceAcrossClear()
        {
            var context = Configured();
            var first = Snapshot(3, Transforms());
            Assert.That(context.TryPublish(first, out string error), Is.True, error);
            Assert.That(context.HasPublishedPose, Is.True);
            Assert.That(context.ClearPose(out error), Is.True, error);
            Assert.That(context.HasPublishedPose, Is.False,
                "clearing must stop all body callbacks from claiming prior input");
            Assert.That(ReadBodyState(context, new OvrAvatarTrackingBodyState()), Is.False);
            Assert.That(context.TryPublish(Snapshot(3, Transforms()), out error), Is.False,
                "clearing input must not reset the context's publication epoch");
            Assert.That(context.TryPublish(Snapshot(4, new[] { Transform() }), out error), Is.False,
                "partial lower-body poses must never become body input");
            Assert.That(context.TryPublish(Snapshot(4, Transforms()), out error), Is.True, error);
            context.Dispose();
        }

        [Test]
        public void Publish_RejectsBadClaimedInputAndForwardsFrozenExplicitStateWithoutChangingOldSnapshot()
        {
            var context = Configured();
            CAPI.ovrAvatar2InputTrackingState input = InputState();
            CAPI.ovrAvatar2InputControlState controls = ControlState();
            CAPI.ovrAvatar2Transform[] pose = Transforms();
            pose[1].position.y = -.4f;
            Assert.That(context.TryPublish(Snapshot(1, pose, input, controls), out string error), Is.True,
                error);
            pose[1].position.y = 99f;
            input.headset.orientation.w = float.NaN;
            Assert.That(context.TryPublish(Snapshot(2, Transforms(), input, controls), out error), Is.False);
            controls.leftControllerState.joystickX = float.NaN;
            Assert.That(context.TryPublish(Snapshot(2, Transforms(), InputState(), controls), out error), Is.False);

            var state = new OvrAvatarTrackingBodyState();
            Assert.That(ReadBodyState(context, state), Is.True);
            Assert.That(state.inputTrackingState.headset.orientation.w, Is.EqualTo(1f));
            Assert.That(state.inputTrackingState.headsetActive, Is.True);
            Assert.That(state.inputTrackingState.leftControllerActive, Is.True);
            Assert.That(state.inputTrackingState.rightControllerActive, Is.True);
            Assert.That(state.inputTrackingState.leftController.position.x, Is.EqualTo(-1f));
            Assert.That(state.inputTrackingState.rightController.position.x, Is.EqualTo(1f));
            Assert.That(state.inputTrackingState.leftController.orientation.y,
                Is.EqualTo(Mathf.Sin(15f * Mathf.Deg2Rad)).Within(1e-6f));
            Assert.That(state.inputTrackingState.rightController.orientation.z, Is.EqualTo(.5f).Within(1e-6f));
            Assert.That(state.inputControlState.leftControllerState.joystickX, Is.EqualTo(.4f));
            Assert.That(state.inputControlState.rightControllerState.handTrigger, Is.EqualTo(.7f));
            Assert.That(state.bodyModality, Is.EqualTo(CAPI.ovrAvatar2TrackingBodyModality.Sitting));
            Assert.That(state.handScale[(int)CAPI.ovrAvatar2Side.Left], Is.EqualTo(1f));
            Assert.That(state.handInputType[(int)CAPI.ovrAvatar2Side.Right],
                Is.EqualTo(CAPI.ovrAvatar2HandInputType.Controller));
            var frozen = PrivateField<SbArticulatedBodyTrackingContext.Snapshot>(context, "_snapshot");
            Assert.That(frozen.LocalTransforms[1].position.y, Is.EqualTo(-.4f));
            Assert.That(frozen.Sequence, Is.EqualTo(1));
            Assert.That(context.TryPublish(Snapshot(2, Transforms(), InputState(), ControlState()),
                out error), Is.True, "Rejected inputs must allow the same sequence to retry.");
            context.Dispose();
            Assert.That(ReadBodyState(context, state), Is.False);
            Assert.That(context.ClearPose(out error), Is.False);
            context.Dispose();
        }

        [TestCase("left-position")]
        [TestCase("right-orientation")]
        [TestCase("visible-pose")]
        [TestCase("inactive-nan")]
        [TestCase("control-type")]
        [TestCase("control-mask")]
        [TestCase("trigger")]
        [TestCase("bone")]
        public void MalformedSnapshotCannotReplacePriorInputOrConsumeSequence(string field)
        {
            var context = Configured();
            Assert.That(context.TryPublish(Snapshot(0, Transforms()), out _), Is.True);
            var input = InputState();
            var controls = ControlState();
            var pose = Transforms();
            switch (field)
            {
                case "left-position": input.leftController.position.x = float.PositiveInfinity; break;
                case "right-orientation": input.rightController.orientation.w = 0f; break;
                case "visible-pose":
                    input.leftControllerActive = false;
                    input.leftController.orientation = default;
                    break;
                case "inactive-nan":
                    input.headsetActive = false;
                    input.headset.position.y = float.NaN;
                    break;
                case "control-type": controls.type = (CAPI.ovrAvatar2ControllerType)999; break;
                case "control-mask": controls.leftControllerState.buttonMask = (CAPI.ovrAvatar2Button)128; break;
                case "trigger": controls.rightControllerState.indexTrigger = float.NaN; break;
                case "bone": pose[1].scale.x = 0f; break;
            }
            Assert.That(context.TryPublish(Snapshot(1, pose, input, controls), out _), Is.False);
            var body = new OvrAvatarTrackingBodyState();
            Assert.That(ReadBodyState(context, body), Is.True);
            Assert.That(body.inputTrackingState.headsetActive, Is.False,
                "Rejected claimed observations cannot replace the prior absent observation.");
            Assert.That(context.TryPublish(Snapshot(1, Transforms(), InputState(), ControlState()), out _), Is.True);
        }

        [Test]
        public void DisposeRetiresOnlyItsRegistryEntryAndCannotBeRepublished()
        {
            var context = Configured();
            var other = Configured();
            int id = (int)typeof(OvrAvatarCallbackContextBase).GetField("id",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
            int otherId = (int)typeof(OvrAvatarCallbackContextBase).GetField("id",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(other);
            var registry = (IDictionary)typeof(OvrAvatarCallbackContextBase).GetField("instanceMap_",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert.That(registry.Contains(id), Is.True);
            Assert.That(context.TryPublish(Snapshot(0, Transforms()), out _), Is.True);
            context.Dispose();
            context.Dispose();
            Assert.That(registry.Contains(id), Is.False);
            Assert.That(registry.Contains(otherId), Is.True);
            Assert.That(context.IsConfigured || context.HasPublishedPose, Is.False);
            Assert.That(ReadBodyState(context, new OvrAvatarTrackingBodyState()), Is.False);
            Assert.That(context.TryPublish(Snapshot(1, Transforms()), out _), Is.False);
            Assert.That(context.ClearPose(out _), Is.False);
            Assert.That(context.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                8, new CAPI.ovrAvatar2Vector3f { z = 1f }, Bones(), Transforms()), out _), Is.False);
        }

        [Test]
        public void NonOwnerCannotPublishClearConfigureOrDisposeButCanReadSnapshot()
        {
            var context = Configured();
            var notConfigured = CreateContext();
            Assert.That(context.TryPublish(Snapshot(0, Transforms()), out _), Is.True);
            bool published = true, cleared = true, configured = true, read = false;
            Exception disposal = null, unexpected = null;
            var thread = new Thread(() =>
            {
                try
                {
                    published = context.TryPublish(Snapshot(1, Transforms()), out _);
                    cleared = context.ClearPose(out _);
                    configured = notConfigured.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                        5, new CAPI.ovrAvatar2Vector3f { z = 1f }, Bones(), Transforms()), out _);
                    read = ReadBodyState(context, new OvrAvatarTrackingBodyState());
                    try { context.Dispose(); } catch (Exception error) { disposal = error; }
                }
                catch (Exception error) { unexpected = error; }
            });
            thread.Start();
            Assert.That(thread.Join(5000), Is.True);
            Assert.That(unexpected, Is.Null);
            Assert.That(published || cleared || configured, Is.False);
            Assert.That(read, Is.True);
            Assert.That(disposal, Is.TypeOf<InvalidOperationException>());
            Assert.That(context.HasPublishedPose, Is.True);
        }

        private static T PrivateField<T>(object instance, string name) =>
            (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);

        private static bool ReadBodyState(SbArticulatedBodyTrackingContext context,
                                          OvrAvatarTrackingBodyState state)
        {
            MethodInfo callback = typeof(SbArticulatedBodyTrackingContext).GetMethod("GetBodyState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)callback.Invoke(context, new object[] { state });
        }

        private SbArticulatedBodyTrackingContext Configured()
        {
            var context = CreateContext();
            Assert.That(context.TryConfigure(new SbArticulatedBodyTrackingContext.SkeletonDefinition(
                5, new CAPI.ovrAvatar2Vector3f { z = 1f }, Bones(), Transforms()), out string error),
                Is.True, error);
            return context;
        }

        private static SbArticulatedBodyTrackingContext.Snapshot Snapshot(
            long sequence, CAPI.ovrAvatar2Transform[] transforms)
        {
            return Snapshot(sequence, transforms, new CAPI.ovrAvatar2InputTrackingState(),
                new CAPI.ovrAvatar2InputControlState());
        }

        private static SbArticulatedBodyTrackingContext.Snapshot Snapshot(long sequence,
            CAPI.ovrAvatar2Transform[] transforms, CAPI.ovrAvatar2InputTrackingState input,
            CAPI.ovrAvatar2InputControlState controls)
        {
            return new SbArticulatedBodyTrackingContext.Snapshot(sequence,
                CAPI.ovrAvatar2TrackingBodyModality.Sitting,
                input, controls,
                CAPI.ovrAvatar2HandInputType.Controller, CAPI.ovrAvatar2HandInputType.Controller,
                1f, 1f, transforms);
        }

        private static CAPI.ovrAvatar2InputTrackingState InputState()
        {
            return new CAPI.ovrAvatar2InputTrackingState
            {
                headsetActive = true, leftControllerActive = true, rightControllerActive = true,
                leftControllerVisible = true, rightControllerVisible = true,
                headset = Transform(),
                leftController = new CAPI.ovrAvatar2Transform(Vector3.left, Quaternion.Euler(0f, 30f, 0f), Vector3.one),
                rightController = new CAPI.ovrAvatar2Transform(Vector3.right, Quaternion.Euler(0f, 0f, 60f), Vector3.one)
            };
        }

        private static CAPI.ovrAvatar2InputControlState ControlState()
        {
            return new CAPI.ovrAvatar2InputControlState
            {
                type = CAPI.ovrAvatar2ControllerType.Quest3,
                leftControllerState = new CAPI.ovrAvatar2ControllerState { joystickX = .4f },
                rightControllerState = new CAPI.ovrAvatar2ControllerState { handTrigger = .7f }
            };
        }

        private static CAPI.ovrAvatar2Bone[] Bones()
        {
            return new[]
            {
                new CAPI.ovrAvatar2Bone { boneId = CAPI.ovrAvatar2JointType.Hips, parentBoneIndex = -1 },
                new CAPI.ovrAvatar2Bone { boneId = CAPI.ovrAvatar2JointType.LeftLegUpper, parentBoneIndex = 0 }
            };
        }

        private static CAPI.ovrAvatar2Transform[] Transforms() => new[] { Transform(), Transform() };

        private static CAPI.ovrAvatar2Transform Transform()
        {
            return new CAPI.ovrAvatar2Transform(Vector3.zero, Quaternion.identity, Vector3.one);
        }
    }
}
#endif
