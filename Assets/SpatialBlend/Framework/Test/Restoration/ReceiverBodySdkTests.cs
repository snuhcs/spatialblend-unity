// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using Oculus.Avatar2;
using UnityEngine;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend.Tests
{
    internal static class ReceiverSdkFixture
    {
        internal static ReceiverBodySdkBone[] Bones()
        {
            var ids = new[] { CAPI.ovrAvatar2JointType.Hips, CAPI.ovrAvatar2JointType.Chest,
                CAPI.ovrAvatar2JointType.Head, CAPI.ovrAvatar2JointType.LeftArmUpper,
                CAPI.ovrAvatar2JointType.LeftArmLower, CAPI.ovrAvatar2JointType.LeftHandWrist,
                CAPI.ovrAvatar2JointType.RightArmUpper, CAPI.ovrAvatar2JointType.RightArmLower,
                CAPI.ovrAvatar2JointType.RightHandWrist, CAPI.ovrAvatar2JointType.LeftLegUpper,
                CAPI.ovrAvatar2JointType.LeftLegLower, CAPI.ovrAvatar2JointType.LeftFootAnkle,
                CAPI.ovrAvatar2JointType.RightLegUpper, CAPI.ovrAvatar2JointType.RightLegLower,
                CAPI.ovrAvatar2JointType.RightFootAnkle };
            return ReceiverBodyFixture.Definition().Select((joint, i) => new ReceiverBodySdkBone(joint.Name, i,
                ids[i], ReceiverBodyFixture.Pose(new Vector3(.001f * i, .002f * i, -.003f * i),
                    new Vector3(3 * i, -2 * i, i)))).ToArray();
        }
        internal static Matrix4x4 Calibration => ReceiverBodyFixture.Pose(new Vector3(-3, .2f, 4), new Vector3(7, -27, 13));
        internal static ReceiverBodySdkProfile Profile(ReceiverBodyProfile body)
        {
            Assert.That(ReceiverBodySdkProfile.TryCreate(body, "synthetic-sdk-calibration", 11, Calibration,
                Vector3.forward, Bones(), out var profile, out string error), Is.True, error);
            return profile;
        }
        internal static ReceiverBodySdkSample Sample(ReceiverBodyFrame body, bool malformed = false) =>
            new ReceiverBodySdkSample(body, CAPI.ovrAvatar2TrackingBodyModality.Sitting,
                new CAPI.ovrAvatar2InputTrackingState { headsetActive = true,
                    headset = new CAPI.ovrAvatar2Transform(new Vector3(8, 9, 10), Quaternion.Euler(13, 27, 41), Vector3.one) },
                new CAPI.ovrAvatar2InputControlState { type = CAPI.ovrAvatar2ControllerType.Quest3,
                    leftControllerState = new CAPI.ovrAvatar2ControllerState { joystickX = malformed ? float.NaN : .25f } },
                CAPI.ovrAvatar2HandInputType.Custom, CAPI.ovrAvatar2HandInputType.Controller, 1.2f, .9f);
        internal static SbArticulatedBodyTrackingContext.Snapshot Prepare(ReceiverBodySdkProfile profile,
            ReceiverBodySdkSample sample, Func<bool> validity = null)
        {
            object[] args = { sample, validity, null, null };
            Assert.That((bool)typeof(ReceiverBodySdkProfile).GetMethod("TryPrepare", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(profile, args), Is.True, args[3] as string);
            return (SbArticulatedBodyTrackingContext.Snapshot)args[2];
        }
        internal static bool ReadState(SbArticulatedBodyTrackingContext context, out OvrAvatarTrackingBodyState state)
        {
            state = new OvrAvatarTrackingBodyState();
            return (bool)typeof(SbArticulatedBodyTrackingContext).GetMethod("GetBodyState",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(context, new object[] { state });
        }

        // Execute the PINNED SDK's actual registry callbacks with native-layout
        // buffers. Emit only bridges the SDK-internal ref-struct signature; it
        // does not implement a callback or replace the SDK's span/copy logic.
        [StructLayout(LayoutKind.Sequential)]
        internal struct PoseBuffer
        { public uint Count; public CAPI.ovrAvatar2Space Space; public IntPtr Transforms; public float LeftScale, RightScale; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SkeletonBuffer
        { public uint Count; public CAPI.ovrAvatar2Vector3f Forward; public IntPtr Bones; public PoseBuffer Reference; }
        private static bool Call(string name, IntPtr payload, SbArticulatedBodyTrackingContext context, Type layout)
        {
            var method = typeof(OvrAvatarBodyTrackingContextBase).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Type native = method.GetParameters()[0].ParameterType.GetElementType();
            var size = new DynamicMethod("NativeTrackingSize", typeof(int), Type.EmptyTypes, true);
            var sizeIl = size.GetILGenerator(); sizeIl.Emit(OpCodes.Sizeof, native); sizeIl.Emit(OpCodes.Ret);
            Assert.That(((Func<int>)size.CreateDelegate(typeof(Func<int>)))(), Is.EqualTo(Marshal.SizeOf(layout)));
            var call = new DynamicMethod("InvokePinnedSdkCallback", typeof(bool), new[] { typeof(IntPtr), typeof(IntPtr) },
                typeof(OvrAvatarBodyTrackingContextBase), true);
            var il = call.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
            var callback = (Func<IntPtr, IntPtr, bool>)call.CreateDelegate(typeof(Func<IntPtr, IntPtr, bool>));
            int id = (int)typeof(OvrAvatarCallbackContextBase).GetField("id", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
            return callback(payload, new IntPtr(id));
        }
        internal static bool ReadPose(SbArticulatedBodyTrackingContext context, int count,
            out CAPI.ovrAvatar2Transform[] poses, bool skeleton = false, bool nullBuffer = false)
        {
            int stride = Marshal.SizeOf(typeof(CAPI.ovrAvatar2Transform));
            int boneStride = Marshal.SizeOf(typeof(CAPI.ovrAvatar2Bone));
            IntPtr transforms = Marshal.AllocHGlobal(stride * Math.Max(1, count));
            IntPtr bones = Marshal.AllocHGlobal(boneStride * Math.Max(1, count));
            IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SkeletonBuffer)));
            try
            {
                poses = null;
                var pose = new PoseBuffer { Count = (uint)count, Space = CAPI.ovrAvatar2Space.Unknown,
                    Transforms = nullBuffer ? IntPtr.Zero : transforms, LeftScale = 1, RightScale = 1 };
                if (skeleton) Marshal.StructureToPtr(new SkeletonBuffer { Count = (uint)count, Bones = bones, Reference = pose }, header, false);
                else Marshal.StructureToPtr(pose, header, false);
                if (!Call(skeleton ? "BodySkeletonCallback" : "BodyPoseCallback", header, context,
                    skeleton ? typeof(SkeletonBuffer) : typeof(PoseBuffer))) return false;
                var result = skeleton ? Marshal.PtrToStructure<SkeletonBuffer>(header).Reference : Marshal.PtrToStructure<PoseBuffer>(header);
                Assert.That(result.Space, Is.EqualTo(CAPI.ovrAvatar2Space.Local));
                poses = new CAPI.ovrAvatar2Transform[count];
                for (int i = 0; i < count; ++i) poses[i] = Marshal.PtrToStructure<CAPI.ovrAvatar2Transform>(IntPtr.Add(transforms, stride * i));
                if (skeleton)
                {
                    Assert.That(Marshal.PtrToStructure<SkeletonBuffer>(header).Forward.z, Is.EqualTo(-1));
                    Assert.That(Marshal.PtrToStructure<CAPI.ovrAvatar2Bone>(bones).parentBoneIndex, Is.EqualTo(-1));
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(bones); Marshal.FreeHGlobal(transforms); }
        }
        internal static void PoseEquals(CAPI.ovrAvatar2Transform actual, Matrix4x4 unity)
        {
            // Independent matrix conjugation oracle, not production ConvertSpace.
            var reflection = Matrix4x4.Scale(new Vector3(1, 1, -1));
            Matrix4x4 expected = reflection * unity * reflection;
            var observed = Matrix4x4.TRS(actual.position, actual.orientation, actual.scale);
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                    Assert.That(observed[row, column], Is.EqualTo(expected[row, column]).Within(3e-5), "SDK pose cell " + row + "," + column);
        }
    }

    [Category("FreeformRestoration")]
    public sealed class ReceiverBodySdkProfileTests
    {
        [Test]
        public void ExactBoneMappingAndCalibrationAreFrozenAndFingerprinted()
        {
            var body = ReceiverBodyFixture.Profile(); var bones = ReceiverSdkFixture.Bones();
            Assert.That(ReceiverBodySdkProfile.TryCreate(body, "calibration", 11, ReceiverSdkFixture.Calibration,
                Vector3.forward, bones, out var first, out string error), Is.True, error);
            Assert.That(ReceiverBodySdkProfile.TryCreate(body, "calibration", 11, ReceiverSdkFixture.Calibration,
                Vector3.forward, bones, out var same, out error), Is.True, error);
            Assert.That(first.Fingerprint, Is.EqualTo(same.Fingerprint));
            var original = bones[1]; bones[1] = new ReceiverBodySdkBone("chest", 1, original.BoneId, Matrix4x4.identity);
            Assert.That(ReceiverBodySdkProfile.TryCreate(body, "calibration", 11, ReceiverSdkFixture.Calibration,
                Vector3.forward, bones, out var changed, out error), Is.True, error);
            Assert.That(first.Bones[1].JointFromSdkBone, Is.EqualTo(original.JointFromSdkBone));
            Assert.That(first.Fingerprint, Is.Not.EqualTo(changed.Fingerprint));
            var definition = first.CreateSkeletonDefinition(); definition.LocalReferencePose[0] = default;
            Assert.That(first.CreateSkeletonDefinition().LocalReferencePose[0].scale.x, Is.EqualTo(1));
        }

        [TestCase("name")][TestCase("index")][TestCase("duplicate")][TestCase("invalid-id")]
        [TestCase("scale")][TestCase("missing")][TestCase("entity-calibration")][TestCase("forward")]
        public void IncompleteOrGuessedMappingRejects(string field)
        {
            var body = ReceiverBodyFixture.Profile(); var bones = ReceiverSdkFixture.Bones();
            var entity = ReceiverSdkFixture.Calibration; var forward = Vector3.forward;
            var old = bones[1];
            switch (field)
            {
                case "name": bones[1] = new ReceiverBodySdkBone("head", 1, old.BoneId, old.JointFromSdkBone); break;
                case "index": bones[1] = new ReceiverBodySdkBone(old.JointName, 2, old.BoneId, old.JointFromSdkBone); break;
                case "duplicate": bones[1] = new ReceiverBodySdkBone(old.JointName, 1, bones[0].BoneId, old.JointFromSdkBone); break;
                case "invalid-id": bones[1] = new ReceiverBodySdkBone(old.JointName, 1, CAPI.ovrAvatar2JointType.Count, old.JointFromSdkBone); break;
                case "scale": bones[1] = new ReceiverBodySdkBone(old.JointName, 1, old.BoneId, Matrix4x4.Scale(Vector3.one * 2)); break;
                case "missing": bones = bones.Take(14).ToArray(); break;
                case "entity-calibration": entity.m01 = .5f; break;
                case "forward": forward.z = float.NaN; break;
            }
            Assert.That(ReceiverBodySdkProfile.TryCreate(body, "calibration", 11, entity, forward, bones, out _, out _), Is.False);
        }

        [Test]
        public void ActualSdkCallbacksReceiveWholeCalibratedLocalHierarchyAndExplicitControls()
        {
            var body = ReceiverBodyFixture.Frame(ReceiverBodyFixture.Profile()); var profile = ReceiverSdkFixture.Profile(body.Profile);
            var sample = ReceiverSdkFixture.Sample(body); var prepared = ReceiverSdkFixture.Prepare(profile, sample);
            using (var context = new SbArticulatedBodyTrackingContext())
            {
                Assert.That(context.TryConfigure(profile.CreateSkeletonDefinition(), out string error), Is.True, error);
                Assert.That(context.TryPublish(prepared, out error), Is.True, error);
                prepared.LocalTransforms[2] = default; // context already owns its copy
                Assert.That(ReceiverSdkFixture.ReadState(context, out var state), Is.True);
                Assert.That(state.inputTrackingState.headset.position.x, Is.EqualTo(8), "Do not derive head input from goal/joint positions.");
                Assert.That(state.inputTrackingState.leftControllerActive, Is.False);
                Assert.That(state.inputControlState.leftControllerState.joystickX, Is.EqualTo(.25f));
                Assert.That(state.handScale[0], Is.EqualTo(1.2f));
                Assert.That(ReceiverSdkFixture.ReadPose(context, 15, out var poses), Is.True);
                var bones = ReceiverSdkFixture.Bones(); var world = new Matrix4x4[15];
                for (int i = 0; i < 15; ++i)
                {
                    int parent = body.Profile.Skeleton.Joints[i].ParentIndex;
                    world[i] = body.WorldJoints[i].WorldPose * bones[i].JointFromSdkBone;
                    var expected = parent < 0 ? ReceiverSdkFixture.Calibration * world[i] : world[parent].inverse * world[i];
                    ReceiverSdkFixture.PoseEquals(poses[i], expected);
                }
                Assert.That(ReceiverSdkFixture.ReadPose(context, 15, out var reference, skeleton: true), Is.True);
                var expectedReference = profile.CreateSkeletonDefinition().LocalReferencePose;
                for (int i = 0; i < 15; ++i) Assert.That(reference[i].position.x, Is.EqualTo(expectedReference[i].position.x));
                Assert.That(ReceiverSdkFixture.ReadPose(context, 14, out _), Is.False);
                Assert.That(ReceiverSdkFixture.ReadPose(context, 15, out _, nullBuffer: true), Is.False);
                Assert.That(ReceiverSdkFixture.ReadPose(context, 14, out _, skeleton: true), Is.False);
            }
        }

        [Test]
        public void CallbackTimeFenceExpiresAllSdkCallbacksWithoutUpdateAndDoesNotRevive()
        {
            var body = ReceiverBodyFixture.Frame(ReceiverBodyFixture.Profile()); var profile = ReceiverSdkFixture.Profile(body.Profile);
            long clock = 100; int probes = 0;
            var prepared = ReceiverSdkFixture.Prepare(profile, ReceiverSdkFixture.Sample(body),
                () => { Interlocked.Increment(ref probes); return Volatile.Read(ref clock) < 200; });
            using (var context = new SbArticulatedBodyTrackingContext())
            {
                Assert.That(context.TryConfigure(profile.CreateSkeletonDefinition(), out _), Is.True);
                Assert.That(context.TryPublish(prepared, out _), Is.True);
                bool first = false, expired = true, skeleton = true, state = true; Exception unexpected = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        first = ReceiverSdkFixture.ReadPose(context, 15, out _);
                        Volatile.Write(ref clock, 200);
                        expired = ReceiverSdkFixture.ReadPose(context, 15, out _);
                        skeleton = ReceiverSdkFixture.ReadPose(context, 15, out _, skeleton: true);
                        state = ReceiverSdkFixture.ReadState(context, out _);
                    }
                    catch (Exception e) { unexpected = e; }
                });
                worker.Start(); Assert.That(worker.Join(5000), Is.True); Assert.That(unexpected, Is.Null);
                Assert.That(first, Is.True); Assert.That(expired || skeleton || state, Is.False);
                Assert.That(probes, Is.GreaterThanOrEqualTo(3));
                Volatile.Write(ref clock, 100); Assert.That(context.HasPublishedPose, Is.False);
                Assert.That(context.TryPublish(prepared, out _), Is.False, "Expiry cannot reset the sequence fence.");
            }
        }
    }
}
#endif
