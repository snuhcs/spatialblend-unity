// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SpatialBlend
{
    public enum ReceiverBodyEvidenceKind
    {
        RecordedBody = 1,
        LiveBodyTracking = 2,
        SyntheticFixture = 3,
        GeneratedOutput = 4
    }

    public readonly struct ReceiverBodyJoint
    {
        public ReceiverBodyJoint(string name, int parentIndex, Matrix4x4 parentFromReferenceJoint)
        { Name = name; ParentIndex = parentIndex; ParentFromReferenceJoint = parentFromReferenceJoint; }
        public string Name { get; }
        public int ParentIndex { get; }
        public Matrix4x4 ParentFromReferenceJoint { get; }
    }

    /// <summary>
    /// An exact ordered skeleton in metres, Unity Y-up/+Z-forward coordinates.
    /// Completeness means this declared layout, not verified human anatomy.
    /// Reference transforms identify calibration; they never fill missing input.
    /// </summary>
    public sealed class ReceiverBodySkeleton
    {
        public const int MaximumJoints = 128;
        private ReceiverBodySkeleton(string id, ulong revision, ReceiverBodyJoint[] joints)
        {
            Id = id; Revision = revision; Joints = Array.AsReadOnly(joints);
            // Versioned little-endian binary fingerprint of the exact ordered
            // names, parents and row-major float reference transforms.
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write("spatialblend-receiver-body-skeleton-v1-unity-metres");
                    writer.Write(id); writer.Write(revision); writer.Write(joints.Length);
                    foreach (ReceiverBodyJoint joint in joints)
                    {
                        writer.Write(joint.Name); writer.Write(joint.ParentIndex);
                        for (int row = 0; row < 4; ++row)
                            for (int col = 0; col < 4; ++col)
                                writer.Write(joint.ParentFromReferenceJoint[row, col]);
                    }
                }
                using (SHA256 hash = SHA256.Create())
                    Fingerprint = BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        public string Id { get; }
        public ulong Revision { get; }
        public string Fingerprint { get; }
        public IReadOnlyList<ReceiverBodyJoint> Joints { get; }

        public static bool TryCreate(string id, ulong revision, IReadOnlyList<ReceiverBodyJoint> joints,
            out ReceiverBodySkeleton skeleton, out string error)
        {
            skeleton = null; error = null;
            if (!ReceiverInputValidation.ValidName(id) || revision == 0 || joints == null ||
                joints.Count == 0 || joints.Count > MaximumJoints)
                return ReceiverInputValidation.Fail("A bounded, named and versioned body skeleton is required.", out error);
            var copy = new ReceiverBodyJoint[joints.Count];
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < copy.Length; ++i)
            {
                ReceiverBodyJoint joint = joints[i];
                if (!ReceiverInputValidation.ValidName(joint.Name) || !names.Add(joint.Name) ||
                    (i == 0 ? joint.ParentIndex != -1 : joint.ParentIndex < 0 || joint.ParentIndex >= i) ||
                    !ReceiverInputValidation.TryNativePose(joint.ParentFromReferenceJoint, out _))
                    return ReceiverInputValidation.Fail("Skeleton requires unique names, one root, earlier parent indices and rigid reference poses.", out error);
                copy[i] = joint;
            }
            skeleton = new ReceiverBodySkeleton(id, revision, copy);
            return true;
        }
    }

    public readonly struct ReceiverBodyAnchorBinding
    {
        public ReceiverBodyAnchorBinding(string anchor, string jointName, int jointIndex,
            Matrix4x4 jointFromAnchor)
        { Anchor = anchor; JointName = jointName; JointIndex = jointIndex; JointFromAnchor = jointFromAnchor; }
        public string Anchor { get; }
        public string JointName { get; }
        public int JointIndex { get; }
        public Matrix4x4 JointFromAnchor { get; }
    }

    /// <summary>
    /// Explicit provider/calibration binding to ONE receiver input epoch. Values
    /// are supplied provenance, not an attestation of measurement or rig DOFs.
    /// The locomotion root is a separate observation, never a pelvis alias.
    /// </summary>
    public sealed class ReceiverBodyProfile
    {
        private ReceiverBodyProfile(ReceiverInputEpoch input, string providerId, ulong providerEpoch,
            ReceiverBodyClockMapping clockMapping, ReceiverBodyEvidenceKind evidence, string evidenceReference,
            ReceiverBodySkeleton skeleton, Matrix4x4 receiverWorldFromBody,
            ReceiverBodyAnchorBinding[] anchors)
        {
            Input = input; ProviderId = providerId; ProviderEpoch = providerEpoch;
            ClockMapping = clockMapping; EvidenceKind = evidence; EvidenceReference = evidenceReference;
            Skeleton = skeleton; ReceiverWorldFromBody = receiverWorldFromBody;
            Anchors = Array.AsReadOnly(anchors);
            Fingerprint = ReceiverBodyHash.Compute(writer =>
            {
                writer.Write("spatialblend-receiver-body-profile-v1-unity-metres");
                writer.Write(providerId); writer.Write(providerEpoch); writer.Write((int)evidence); writer.Write(evidenceReference);
                writer.Write(skeleton.Fingerprint); writer.Write(clockMapping.Fingerprint);
                writer.Write(input.Epoch); writer.Write(input.RigRevision); writer.Write(input.CalibrationRevision);
                writer.Write(input.RootAnchor); writer.Write(input.AnchorNames.Count);
                foreach (string name in input.AnchorNames) writer.Write(name);
                ReceiverBodyHash.WriteMatrix(writer, receiverWorldFromBody); writer.Write(anchors.Length);
                foreach (ReceiverBodyAnchorBinding anchor in anchors)
                {
                    writer.Write(anchor.Anchor); writer.Write(anchor.JointName); writer.Write(anchor.JointIndex);
                    ReceiverBodyHash.WriteMatrix(writer, anchor.JointFromAnchor);
                }
            });
        }
        public ReceiverInputEpoch Input { get; }
        public string ProviderId { get; }
        public ulong ProviderEpoch { get; }
        public ReceiverBodyClockMapping ClockMapping { get; }
        public string SourceClock => ClockMapping.SourceClock;
        public string Fingerprint { get; }
        public ReceiverBodyEvidenceKind EvidenceKind { get; }
        public string EvidenceReference { get; }
        public ReceiverBodySkeleton Skeleton { get; }
        public Matrix4x4 ReceiverWorldFromBody { get; }
        public IReadOnlyList<ReceiverBodyAnchorBinding> Anchors { get; }

        public static bool TryCreate(ReceiverInputEpoch input, string providerId, ulong providerEpoch,
            ReceiverBodyClockMapping clockMapping, ReceiverBodyEvidenceKind evidence, string evidenceReference,
            ReceiverBodySkeleton skeleton, Matrix4x4 receiverWorldFromBody,
            IReadOnlyList<ReceiverBodyAnchorBinding> anchors,
            out ReceiverBodyProfile profile, out string error)
        {
            profile = null; error = null;
            if (input == null || clockMapping == null ||
                clockMapping.NativeClockId != input.ClockId || clockMapping.NativeClockEpoch != input.ClockEpoch)
                return ReceiverInputValidation.Fail("Body clock must match the exact admitted receiver epoch.", out error);
            if (!TryCopyDefinition(input.RootAnchor, input.AnchorNames, providerId, providerEpoch,
                clockMapping, evidence, evidenceReference, skeleton, receiverWorldFromBody, anchors,
                out var copy, out error)) return false;
            profile = new ReceiverBodyProfile(input, providerId, providerEpoch, clockMapping, evidence,
                evidenceReference, skeleton, receiverWorldFromBody, copy);
            return true;
        }

        // Shared pure definition validation. No fabricated receiver epoch and no
        // native activation is needed to reject incomplete authoring in advance.
        internal static bool TryCopyDefinition(string root, IReadOnlyList<string> layout,
            string providerId, ulong providerEpoch, ReceiverBodyClockMapping clockMapping,
            ReceiverBodyEvidenceKind evidence, string evidenceReference, ReceiverBodySkeleton skeleton,
            Matrix4x4 receiverWorldFromBody, IReadOnlyList<ReceiverBodyAnchorBinding> anchors,
            out ReceiverBodyAnchorBinding[] copy, out string error)
        {
            copy = null; error = null;
            if (skeleton == null || providerEpoch == 0 ||
                !ReceiverInputValidation.ValidName(providerId) || clockMapping == null ||
                (int)clockMapping.Kind != (evidence == ReceiverBodyEvidenceKind.RecordedBody ? 2 :
                    evidence == ReceiverBodyEvidenceKind.LiveBodyTracking ? 1 : 3) ||
                !ReceiverInputValidation.ValidName(evidenceReference) ||
                (evidence != ReceiverBodyEvidenceKind.RecordedBody && evidence != ReceiverBodyEvidenceKind.LiveBodyTracking &&
                 evidence != ReceiverBodyEvidenceKind.SyntheticFixture) ||
                !ReceiverInputValidation.TryNativePose(receiverWorldFromBody, out _))
                return ReceiverInputValidation.Fail("Explicit independent body provenance, immutable skeleton and rigid receiver calibration are required; generated output is not a provider.", out error);
            if (!ReceiverInputValidation.ValidName(root) || layout == null || layout.Count == 0 ||
                layout.Count > ReceiverInputValidation.MaximumAnchors)
                return ReceiverInputValidation.Fail("An explicit bounded root and receiver anchor layout are required.", out error);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in layout)
                if (!ReceiverInputValidation.ValidName(name) || !names.Add(name))
                    return ReceiverInputValidation.Fail("Receiver layout names must be valid and unique.", out error);
            if (!names.Remove(root))
                return ReceiverInputValidation.Fail("The declared root must occur exactly once in the receiver layout.", out error);
            if (anchors == null || anchors.Count > ReceiverInputValidation.MaximumAnchors ||
                anchors.Count != layout.Count - 1)
                return ReceiverInputValidation.Fail("Map every non-root receiver anchor exactly once; root uses its separate observation.", out error);
            copy = new ReceiverBodyAnchorBinding[anchors.Count];
            for (int i = 0; i < copy.Length; ++i)
            {
                ReceiverBodyAnchorBinding binding = anchors[i];
                if (!ReceiverInputValidation.ValidName(binding.Anchor) || !names.Remove(binding.Anchor) ||
                    binding.JointIndex < 0 || binding.JointIndex >= skeleton.Joints.Count ||
                    !string.Equals(skeleton.Joints[binding.JointIndex].Name, binding.JointName, StringComparison.Ordinal) ||
                    !ReceiverInputValidation.TryNativePose(binding.JointFromAnchor, out _))
                    return ReceiverInputValidation.Fail("Each anchor requires its actual skeleton joint name AND index and a rigid joint-local calibration.", out error);
                copy[i] = binding;
            }
            return true;
        }
    }

    public readonly struct ReceiverBodyJointPose
    {
        public ReceiverBodyJointPose(string name, int index, bool available, Matrix4x4 parentFromJoint)
        { Name = name; Index = index; Available = available; ParentFromJoint = parentFromJoint; }
        public string Name { get; }
        public int Index { get; }
        public bool Available { get; }
        public Matrix4x4 ParentFromJoint { get; }
    }

    /// <summary>
    /// Deep-owned whole-layout body input. Source time and mapped native
    /// acquisition/expiry are distinct; this class does not establish a clock
    /// mapping, calibrate a recording, infer availability, or read scene objects.
    /// </summary>
    public sealed class ReceiverBodyFrame
    {
        private ReceiverBodyFrame(ReceiverBodyProfile profile, long sourceSequence, double sourceTime,
            ReceiverBodyJointPose[] joints, ReceiverAnchorPose[] worldJoints, bool allJointsAvailable,
            ReceiverInputFrame geometric, long acquisitionUpper)
        {
            Profile = profile; SourceSequence = sourceSequence; SourceTimeSeconds = sourceTime;
            Joints = Array.AsReadOnly(joints); WorldJoints = Array.AsReadOnly(worldJoints);
            HasAllJointPoses = allJointsAvailable; GeometricInput = geometric;
            AcquisitionUpperNanoseconds = acquisitionUpper;
        }
        public ReceiverBodyProfile Profile { get; }
        public long SourceSequence { get; }
        public double SourceTimeSeconds { get; }
        public IReadOnlyList<ReceiverBodyJointPose> Joints { get; }
        public IReadOnlyList<ReceiverAnchorPose> WorldJoints { get; }
        public bool HasAllJointPoses { get; }
        public ReceiverInputFrame GeometricInput { get; }
        public long AcquisitionUpperNanoseconds { get; }

        public static bool TryCreate(ReceiverBodyProfile profile, ulong inputSequence,
            long sourceSequence, double sourceTimeSeconds, long observedAtNanoseconds,
            long validUntilNanoseconds, bool rootAvailable, Matrix4x4 bodyFromLocomotionRoot,
            IReadOnlyList<ReceiverBodyJointPose> joints, out ReceiverBodyFrame frame, out string error)
        {
            frame = null; error = null;
            if (profile == null || sourceSequence < 0 || !RichKnownDouble.Finite(sourceTimeSeconds) || sourceTimeSeconds < 0 ||
                !ReceiverInputValidation.TryNativePose(bodyFromLocomotionRoot, out _) ||
                joints == null || joints.Count == 0 || joints.Count > ReceiverBodySkeleton.MaximumJoints ||
                joints.Count != profile.Skeleton.Joints.Count)
                return ReceiverInputValidation.Fail("Body input requires original source time/order, an explicit root and every declared joint, including unavailable rows.", out error);
            if (!profile.ClockMapping.TryMap(sourceTimeSeconds, out long lower, out long upper, out error)) return false;
            if (observedAtNanoseconds != lower || validUntilNanoseconds <= upper ||
                validUntilNanoseconds > profile.ClockMapping.ValidUntilNativeNanoseconds)
                return ReceiverInputValidation.Fail("Body acquisition must equal its conservative mapped source time; expiry must stay inside the mapping interval.", out error);
            var copy = new ReceiverBodyJointPose[joints.Count];
            var seen = new bool[copy.Length];
            for (int i = 0; i < copy.Length; ++i)
            {
                ReceiverBodyJointPose pose = joints[i];
                if (pose.Index < 0 || pose.Index >= copy.Length || seen[pose.Index] ||
                    !string.Equals(pose.Name, profile.Skeleton.Joints[pose.Index].Name, StringComparison.Ordinal) ||
                    !ReceiverInputValidation.TryNativePose(pose.ParentFromJoint, out _))
                    return ReceiverInputValidation.Fail("Body rows require unique exact joint indices/names and finite rigid local poses, even when unavailable.", out error);
                seen[pose.Index] = true; copy[pose.Index] = pose;
            }
            var world = new ReceiverAnchorPose[copy.Length];
            bool all = true;
            for (int i = 0; i < copy.Length; ++i)
            {
                int parent = profile.Skeleton.Joints[i].ParentIndex;
                bool available = copy[i].Available && (parent < 0 || world[parent].Available);
                Matrix4x4 pose = available
                    ? ComposeValidatedRigid(parent < 0 ? profile.ReceiverWorldFromBody : world[parent].WorldPose,
                        copy[i].ParentFromJoint)
                    : Matrix4x4.identity;
                if (!ReceiverInputValidation.TryNativePose(pose, out _))
                    return ReceiverInputValidation.Fail("Composed body world pose is not finite proper rigid geometry.", out error);
                world[i] = new ReceiverAnchorPose(copy[i].Name, available, pose);
                all &= available;
            }
            var anchors = new ReceiverAnchorPose[profile.Input.AnchorNames.Count];
            anchors[0] = new ReceiverAnchorPose(profile.Input.RootAnchor, rootAvailable,
                ComposeValidatedRigid(profile.ReceiverWorldFromBody, bodyFromLocomotionRoot));
            for (int i = 0; i < profile.Anchors.Count; ++i)
            {
                ReceiverBodyAnchorBinding binding = profile.Anchors[i];
                ReceiverAnchorPose joint = world[binding.JointIndex];
                anchors[i + 1] = new ReceiverAnchorPose(binding.Anchor, joint.Available,
                    joint.Available ? ComposeValidatedRigid(joint.WorldPose, binding.JointFromAnchor) : Matrix4x4.identity);
            }
            if (!ReceiverInputFrame.TryCreate(profile.Input, inputSequence, observedAtNanoseconds,
                validUntilNanoseconds, anchors, out ReceiverInputFrame geometric, out error)) return false;
            frame = new ReceiverBodyFrame(profile, sourceSequence, sourceTimeSeconds, copy, world, all, geometric, upper);
            return true;
        }

        private static Matrix4x4 ComposeValidatedRigid(Matrix4x4 parent, Matrix4x4 child)
        {
            // Both inputs have ALREADY passed the finite/proper-rigid gate.
            // Compose rotation as unit quaternions so ordinary float matrix
            // roundoff cannot accumulate scale/shear over a deep hierarchy.
            // This does not strip scale/shear from unvalidated provider inputs;
            // the original accepted local matrices remain unchanged in Joints.
            Quaternion parentRotation = Quaternion.Normalize(parent.rotation);
            Quaternion childRotation = Quaternion.Normalize(child.rotation);
            Vector3 parentPosition = parent.GetColumn(3), childPosition = child.GetColumn(3);
            return Matrix4x4.TRS(parentPosition + parentRotation * childPosition,
                Quaternion.Normalize(parentRotation * childRotation), Vector3.one);
        }
    }
}
