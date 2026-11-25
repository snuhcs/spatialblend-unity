// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Oculus.Avatar2;
using UnityEngine;
using CAPI = Oculus.Avatar2.CAPI;

namespace SpatialBlend
{
    public readonly struct ReceiverBodySdkBone
    {
        public ReceiverBodySdkBone(string jointName, int jointIndex, CAPI.ovrAvatar2JointType boneId,
            Matrix4x4 jointFromSdkBone)
        { JointName = jointName; JointIndex = jointIndex; BoneId = boneId; JointFromSdkBone = jointFromSdkBone; }
        public string JointName { get; }
        public int JointIndex { get; }
        public CAPI.ovrAvatar2JointType BoneId { get; }
        public Matrix4x4 JointFromSdkBone { get; }
    }

    /// <summary>
    /// Explicit same-topology body→SDK input calibration. Not avatar retargeting
    /// or output DOF ownership. Every declared source joint has one actual SDK
    /// bone ID, in source order; joint frame offsets are supplied, never guessed.
    /// Matrices are Unity Y-up/metres. SDK Local output uses the pinned Avatar
    /// SDK standard ConvertSpace basis (Z reflection), NOT ConvertSpaceRT.
    /// </summary>
    public sealed class ReceiverBodySdkProfile
    {
        private readonly CAPI.ovrAvatar2Transform[] _reference;
        private ReceiverBodySdkProfile(ReceiverBodyProfile body, string calibrationEvidence, int version,
            Matrix4x4 entityFromReceiverWorld, Vector3 entityForward, ReceiverBodySdkBone[] bones,
            CAPI.ovrAvatar2Transform[] reference)
        {
            Body = body; CalibrationEvidence = calibrationEvidence; SkeletonVersion = version;
            EntityFromReceiverWorld = entityFromReceiverWorld; EntityForward = entityForward;
            Bones = Array.AsReadOnly(bones); _reference = reference;
            Fingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-receiver-body-sdk-v1-standard-z-reflection-local");
                writer.Write(body.Fingerprint); writer.Write(calibrationEvidence); writer.Write(version);
                ReceiverBodyHash.WriteMatrix(writer, entityFromReceiverWorld);
                writer.Write(entityForward.x); writer.Write(entityForward.y); writer.Write(entityForward.z);
                writer.Write(bones.Length);
                foreach (var bone in bones)
                {
                    writer.Write(bone.JointName); writer.Write(bone.JointIndex); writer.Write((int)bone.BoneId);
                    ReceiverBodyHash.WriteMatrix(writer, bone.JointFromSdkBone);
                }
            });
        }
        public ReceiverBodyProfile Body { get; }
        public string CalibrationEvidence { get; }
        public string Fingerprint { get; }
        public int SkeletonVersion { get; }
        public Matrix4x4 EntityFromReceiverWorld { get; }
        public Vector3 EntityForward { get; }
        public IReadOnlyList<ReceiverBodySdkBone> Bones { get; }

        public static bool TryCreate(ReceiverBodyProfile body, string calibrationEvidence, int skeletonVersion,
            Matrix4x4 entityFromReceiverWorld, Vector3 entityForward, IReadOnlyList<ReceiverBodySdkBone> bones,
            out ReceiverBodySdkProfile profile, out string error)
        {
            profile = null; error = null;
            if (body == null || !ReceiverInputValidation.ValidName(calibrationEvidence) || skeletonVersion < 0 ||
                !ReceiverInputValidation.TryNativePose(entityFromReceiverWorld, out _) ||
                !RichKnownDouble.Finite(entityForward.sqrMagnitude) || Mathf.Abs(entityForward.sqrMagnitude - 1) > .0001f ||
                bones == null || bones.Count != body.Skeleton.Joints.Count)
                return ReceiverInputValidation.Fail("Explicit SDK calibration, unit forward and one bone per declared body joint are required.", out error);
            var copy = new ReceiverBodySdkBone[bones.Count];
            var ids = new HashSet<CAPI.ovrAvatar2JointType>();
            for (int i = 0; i < copy.Length; ++i)
            {
                var bone = bones[i];
                if (bone.JointIndex != i || bone.JointName != body.Skeleton.Joints[i].Name ||
                    bone.BoneId <= CAPI.ovrAvatar2JointType.Invalid || bone.BoneId >= CAPI.ovrAvatar2JointType.Count ||
                    !ids.Add(bone.BoneId) || !ReceiverInputValidation.TryNativePose(bone.JointFromSdkBone, out _))
                    return ReceiverInputValidation.Fail("SDK mapping requires exact ordered joint names/indices, unique real bone IDs and rigid joint-frame calibration.", out error);
                copy[i] = bone;
            }
            var local = new Matrix4x4[copy.Length];
            for (int i = 0; i < local.Length; ++i) local[i] = body.Skeleton.Joints[i].ParentFromReferenceJoint;
            if (!TryConvert(body, entityFromReceiverWorld, copy, local, out var reference, out error)) return false;
            profile = new ReceiverBodySdkProfile(body, calibrationEvidence, skeletonVersion,
                entityFromReceiverWorld, entityForward, copy, reference);
            return true;
        }

        public SbArticulatedBodyTrackingContext.SkeletonDefinition CreateSkeletonDefinition()
        {
            var bones = new CAPI.ovrAvatar2Bone[Bones.Count];
            for (int i = 0; i < bones.Length; ++i)
                bones[i] = new CAPI.ovrAvatar2Bone { boneId = Bones[i].BoneId,
                    parentBoneIndex = (short)Body.Skeleton.Joints[i].ParentIndex };
            var forward = new CAPI.ovrAvatar2Vector3f(EntityForward.x, EntityForward.y, EntityForward.z).ConvertSpace();
            return new SbArticulatedBodyTrackingContext.SkeletonDefinition(SkeletonVersion, forward,
                bones, (CAPI.ovrAvatar2Transform[])_reference.Clone());
        }

        internal bool TryPrepare(ReceiverBodySdkSample sample, Func<bool> validity,
            out SbArticulatedBodyTrackingContext.Snapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            if (sample?.Body == null || !ReferenceEquals(sample.Body.Profile, Body) || !sample.Body.HasAllJointPoses)
                return ReceiverInputValidation.Fail("SDK preparation requires this profile's exact complete available body input.", out error);
            var local = new Matrix4x4[Bones.Count];
            for (int i = 0; i < local.Length; ++i) local[i] = sample.Body.Joints[i].ParentFromJoint;
            if (!TryConvert(Body, EntityFromReceiverWorld, Bones, local, out var poses, out error)) return false;
            // Source sequence is already strictly increasing in this one frozen
            // provider/context lifetime, including across Clear. Never reuse this
            // SDK context for a seek/replay epoch or another profile.
            snapshot = new SbArticulatedBodyTrackingContext.Snapshot(sample.Body.SourceSequence,
                sample.Modality, sample.InputTrackingState, sample.InputControlState,
                sample.LeftHandInputType, sample.RightHandInputType, sample.LeftHandScale,
                sample.RightHandScale, poses, validity);
            return true;
        }

        private static bool TryConvert(ReceiverBodyProfile body, Matrix4x4 entityFromWorld,
            IReadOnlyList<ReceiverBodySdkBone> bones, Matrix4x4[] local,
            out CAPI.ovrAvatar2Transform[] poses, out string error)
        {
            poses = new CAPI.ovrAvatar2Transform[local.Length]; error = null;
            var joints = new Matrix4x4[local.Length]; var sdkBones = new Matrix4x4[local.Length];
            for (int i = 0; i < local.Length; ++i)
            {
                int parent = body.Skeleton.Joints[i].ParentIndex;
                joints[i] = Compose(parent < 0 ? body.ReceiverWorldFromBody : joints[parent], local[i]);
                sdkBones[i] = Compose(joints[i], bones[i].JointFromSdkBone);
                Matrix4x4 pose = parent < 0 ? Compose(entityFromWorld, sdkBones[i]) :
                    Compose(sdkBones[parent].inverse, sdkBones[i]);
                if (!ReceiverInputValidation.TryNativePose(pose, out _))
                { poses = null; return ReceiverInputValidation.Fail("SDK calibrated local pose is not finite rigid geometry.", out error); }
                poses[i] = new CAPI.ovrAvatar2Transform((Vector3)pose.GetColumn(3),
                    Quaternion.Normalize(pose.rotation), Vector3.one).ConvertSpace();
            }
            return true;
        }

        private static Matrix4x4 Compose(Matrix4x4 parent, Matrix4x4 child)
        {
            Quaternion rotation = Quaternion.Normalize(parent.rotation);
            return Matrix4x4.TRS((Vector3)parent.GetColumn(3) + rotation * (Vector3)child.GetColumn(3),
                Quaternion.Normalize(rotation * Quaternion.Normalize(child.rotation)), Vector3.one);
        }
    }

    /// <summary>
    /// One source sample's immutable body AND explicit input observations. CAPI
    /// tracking transforms are already SDK entity-space/metres; they are not
    /// inferred from joints or passed through calibration a second time. The
    /// caller is responsible for same-source acquisition/calibration provenance.
    /// </summary>
    public sealed class ReceiverBodySdkSample
    {
        public ReceiverBodySdkSample(ReceiverBodyFrame body, CAPI.ovrAvatar2TrackingBodyModality modality,
            CAPI.ovrAvatar2InputTrackingState tracking, CAPI.ovrAvatar2InputControlState controls,
            CAPI.ovrAvatar2HandInputType leftHand, CAPI.ovrAvatar2HandInputType rightHand,
            float leftHandScale, float rightHandScale)
        {
            Body = body; Modality = modality; InputTrackingState = tracking; InputControlState = controls;
            LeftHandInputType = leftHand; RightHandInputType = rightHand;
            LeftHandScale = leftHandScale; RightHandScale = rightHandScale;
        }
        public ReceiverBodyFrame Body { get; }
        public CAPI.ovrAvatar2TrackingBodyModality Modality { get; }
        public CAPI.ovrAvatar2InputTrackingState InputTrackingState { get; }
        public CAPI.ovrAvatar2InputControlState InputControlState { get; }
        public CAPI.ovrAvatar2HandInputType LeftHandInputType { get; }
        public CAPI.ovrAvatar2HandInputType RightHandInputType { get; }
        public float LeftHandScale { get; }
        public float RightHandScale { get; }
    }
}
