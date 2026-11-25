// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // Actual Unity Transform + native preparation boundary. Every rig/pose here
    // is explicitly synthetic. These tests do NOT certify contact, a motion
    // model, real recorded body motion, SDK output, or Freeform restoration.
    public sealed partial class FoundationReceiverBodyNativeTests
    {
        private readonly List<CalibratedTransformRigOutputOwner> _rigOwners =
            new List<CalibratedTransformRigOutputOwner>();

        private sealed class OutputRig
        {
            internal Transform Origin;
            internal Transform[] Joints;
            internal Matrix4x4 WorldFromReceiver;
            internal CalibratedTransformRigOutputOwner Owner;
        }

        [TearDown]
        public void CloseRigOutputs()
        {
            // May run before/after the original native fixture teardown. This
            // cleanup has no dependency on a live body input or Unity objects.
            foreach (var owner in _rigOwners) Assert.That(owner.TryClose(out _), Is.True);
            _rigOwners.Clear();
        }

        private OutputRig NewRig(Transform parent = null)
        {
            var root = new GameObject("synthetic-manual-output"); _created.Add(root);
            var rig = new OutputRig {
                Origin = root.transform,
                Joints = new Transform[_binding.Profile.Skeleton.Joints.Count],
                WorldFromReceiver = ReceiverBodyFixture.Pose(new Vector3(-2, 1, 3), new Vector3(7, 23, -5))
            };
            rig.Origin.SetParent(parent, false);
            Matrix4x4 world = rig.WorldFromReceiver * _binding.Profile.ReceiverWorldFromBody;
            rig.Origin.SetPositionAndRotation(world.GetColumn(3), Quaternion.Normalize(world.rotation));
            for (int i = 0; i < rig.Joints.Length; ++i)
            {
                var definition = _binding.Profile.Skeleton.Joints[i];
                var joint = new GameObject(definition.Name).transform;
                joint.SetParent(definition.ParentIndex < 0 ? rig.Origin : rig.Joints[definition.ParentIndex], false);
                joint.SetLocalPositionAndRotation(definition.ParentFromReferenceJoint.GetColumn(3),
                    Quaternion.Normalize(definition.ParentFromReferenceJoint.rotation));
                rig.Joints[i] = joint;
            }
            return rig;
        }

        private CalibratedTransformRigOutputOwner BindRig(OutputRig rig, params int[] reserved)
        {
            Assert.That(CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, rig.Joints, reserved,
                rig.WorldFromReceiver, "synthetic-output-calibration-v1", out var owner, out string error), Is.True, error);
            _rigOwners.Add(owner); rig.Owner = owner; return owner;
        }

        private RigJointClaim Claim(int index, RigPoseDofs dofs = RigPoseDofs.Pose) =>
            new RigJointClaim(_binding.Profile.Skeleton.Joints[index].Name, index, dofs);

        private long RigDeadline(long interval = 750000000)
        {
            Assert.That(_binding.TryReadAcquisitionClock(out long now, out string error), Is.True, error);
            return now + interval;
        }

        private CalibratedTransformRigOutputOwner.RigWriteLease Lease(OutputRig rig, params RigJointClaim[] claims)
        {
            Assert.That(rig.Owner.TryAcquire("synthetic-controller", 7, RigDeadline(), claims,
                out var lease, out string error), Is.True, error); return lease;
        }

        private RigPoseReadback ReadRig(OutputRig rig)
        {
            Assert.That(rig.Owner.TryReadPose(out var pose, out string error), Is.True, error); return pose;
        }

        private static Matrix4x4 Local(Transform joint) =>
            Matrix4x4.TRS(joint.localPosition, joint.localRotation, joint.localScale);

        private static RigJointPose Proposed(RigPoseReadback basis, int index,
            Vector3 translation = default, Vector3 rotation = default)
        {
            var old = basis.LocalPoses[index];
            return new RigJointPose(old.Name, index, Matrix4x4.TRS(
                (Vector3)old.ParentFromJoint.GetColumn(3) + translation,
                old.ParentFromJoint.rotation * Quaternion.Euler(rotation), Vector3.one));
        }

        private static void AssertMatrixNear(Matrix4x4 actual, Matrix4x4 expected)
        {
            for (int i = 0; i < 16; ++i) Assert.That(actual[i], Is.EqualTo(expected[i]).Within(.00002f), "matrix element " + i);
        }

        private static void AssertUnchanged(OutputRig rig, RigPoseReadback before)
        {
            for (int i = 0; i < rig.Joints.Length; ++i)
            {
                Assert.That(Local(rig.Joints[i]), Is.EqualTo(before.LocalPoses[i].ParentFromJoint), "local " + i);
                Assert.That(rig.Joints[i].localToWorldMatrix, Is.EqualTo(before.WorldPoses[i]), "world " + i);
            }
        }

        [Test]
        public void RigOutputCalibratesReferencePoseAndFingerprintsExactBinding()
        {
            var checkpoint = Prepare(out var snapshot);
            var rig = NewRig(); var owner = BindRig(rig); var before = ReadRig(rig);
            Assert.That(owner.InputProfile, Is.SameAs(_binding.Profile));
            Assert.That(owner.WorldFromReceiverWorld, Is.EqualTo(rig.WorldFromReceiver));
            Assert.That(owner.ProfileFingerprint.Length, Is.EqualTo(64));
            var world = new Matrix4x4[rig.Joints.Length];
            for (int i = 0; i < world.Length; ++i)
            {
                var definition = _binding.Profile.Skeleton.Joints[i];
                world[i] = (definition.ParentIndex < 0 ? rig.WorldFromReceiver * _binding.Profile.ReceiverWorldFromBody :
                    world[definition.ParentIndex]) * definition.ParentFromReferenceJoint;
                AssertMatrixNear(before.WorldPoses[i], world[i]);
            }
            string fingerprint = owner.ProfileFingerprint; owner.Dispose();
            Assert.That(BindRig(rig).ProfileFingerprint, Is.EqualTo(fingerprint)); rig.Owner.Dispose();
            Assert.That(BindRig(rig, 5).ProfileFingerprint, Is.Not.EqualTo(fingerprint));
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var retained, out string error), Is.True, error);
            Assert.That(retained, Is.SameAs(checkpoint.Body), "Output binding never republishes reference pose as body input.");
            Assert.That(before.LocalPoses[0].ParentFromJoint, Is.Not.EqualTo(retained.Joints[0].ParentFromJoint));
        }

        [Test]
        public void RigOutputRejectsWrongCalibrationNamesTopologyAndScale()
        {
            var rig = NewRig();
            bool TryBind(Matrix4x4 map) => CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, rig.Joints,
                Array.Empty<int>(), map, "synthetic", out _, out _);
            Assert.That(TryBind(Matrix4x4.identity), Is.False, "A proper but unrelated transform is not calibration.");
            Assert.That(TryBind(Matrix4x4.Scale(Vector3.one * 2)), Is.False);
            rig.Joints[2].name = "wrong"; Assert.That(TryBind(rig.WorldFromReceiver), Is.False); rig.Joints[2].name = "head";
            rig.Joints[2].SetParent(rig.Joints[0], false); Assert.That(TryBind(rig.WorldFromReceiver), Is.False);
            rig.Joints[2].SetParent(rig.Joints[1], false);
            rig.Joints[2].localScale = Vector3.one * 2; Assert.That(TryBind(rig.WorldFromReceiver), Is.False);
            rig.Joints[2].localScale = Vector3.one;
            rig.Joints[2].localPosition += Vector3.up; Assert.That(TryBind(rig.WorldFromReceiver), Is.False);
            rig.Joints[2].localPosition -= Vector3.up;
            var duplicate = (Transform[])rig.Joints.Clone(); duplicate[3] = duplicate[2];
            Assert.That(CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, duplicate, Array.Empty<int>(),
                rig.WorldFromReceiver, "synthetic", out _, out _), Is.False);
            BindRig(rig);
        }

        [Test]
        public void RigOutputRejectsWritersWithoutDisablingAndAllowsStaticRenderGeometry()
        {
            foreach (Type writer in new[] { typeof(Animator), typeof(Animation), typeof(Rigidbody), typeof(RigOutputMarker) })
            {
                var rig = NewRig(); var component = rig.Joints[1].gameObject.AddComponent(writer);
                Assert.That(CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, rig.Joints, Array.Empty<int>(),
                    rig.WorldFromReceiver, "synthetic", out _, out _), Is.False, writer.Name);
                Assert.That(component, Is.Not.Null);
                if (component is Behaviour behaviour) Assert.That(behaviour.enabled, Is.True);
            }
            var supported = NewRig(); supported.Joints[0].gameObject.AddComponent<MeshFilter>();
            supported.Joints[0].gameObject.AddComponent<MeshRenderer>(); supported.Joints[0].gameObject.AddComponent<BoxCollider>();
            BindRig(supported);
        }

        [Test]
        public void RigOutputRegistryRejectsSameAndNestedHierarchyOwners()
        {
            var rig = NewRig(); BindRig(rig);
            Assert.That(CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, rig.Joints, Array.Empty<int>(),
                rig.WorldFromReceiver, "synthetic", out _, out _), Is.False);
            var nested = NewRig(rig.Joints[2]);
            Assert.That(CalibratedTransformRigOutputOwner.TryBind(_binding, nested.Origin, nested.Joints, Array.Empty<int>(),
                nested.WorldFromReceiver, "synthetic", out _, out _), Is.False);
            rig.Owner.Dispose(); BindRig(nested);
            Assert.That(nested.Owner.TryRefreshOwnership(out string error), Is.True, error);
        }

        [Test]
        public void RigOutputDestroyedRegistryOwnerRevokesOldHandles()
        {
            var rig = NewRig(); BindRig(rig); var lease = Lease(rig, Claim(2));
            UnityEngine.Object.DestroyImmediate(rig.Origin.gameObject);
            var replacement = NewRig(); BindRig(replacement);
            Assert.That(rig.Owner.IsClosed, Is.True); Assert.That(lease.IsClosed, Is.True);
            Assert.That(replacement.Owner.TryRefreshOwnership(out string error), Is.True, error);
        }

        [Test]
        public void RigOutputParentEffectsConflictWithDescendantsInBothOrders()
        {
            var rig = NewRig(); BindRig(rig); var child = Lease(rig, Claim(2, RigPoseDofs.Position));
            foreach (var dofs in new[] { RigPoseDofs.Position, RigPoseDofs.Rotation })
                Assert.That(rig.Owner.TryAcquire("parent", 1, RigDeadline(), new[] { Claim(0, dofs) }, out _, out _), Is.False);
            child.Dispose(); var parent = Lease(rig, Claim(0, RigPoseDofs.Rotation));
            Assert.That(rig.Owner.TryAcquire("child", 1, RigDeadline(), new[] { Claim(5) }, out _, out _), Is.False);
            parent.Dispose(); Lease(rig, Claim(0, RigPoseDofs.Position));
            Lease(rig, Claim(2, RigPoseDofs.Rotation)); // Root translation does not own child's world rotation.
        }

        [Test]
        public void RigOutputDisjointSiblingsAndLeafDofsHaveSeparateLeases()
        {
            var rig = NewRig(); BindRig(rig);
            Lease(rig, Claim(2, RigPoseDofs.Position)); Lease(rig, Claim(2, RigPoseDofs.Rotation));
            Lease(rig, Claim(5)); Lease(rig, Claim(8));
            Assert.That(rig.Owner.TryAcquire("duplicate", 2, RigDeadline(), new[] { Claim(2) }, out _, out _), Is.False);
        }

        [Test]
        public void RigOutputReservedWorldJointBlocksAncestorButNotSibling()
        {
            var rig = NewRig(); BindRig(rig, 5);
            foreach (int index in new[] { 0, 1, 3, 4, 5 })
                foreach (var dofs in new[] { RigPoseDofs.Position, RigPoseDofs.Rotation })
                    Assert.That(rig.Owner.TryAcquire("tracked-conflict", 1, RigDeadline(), new[] { Claim(index, dofs) },
                        out _, out _), Is.False, index + " " + dofs);
            Lease(rig, Claim(6)); Lease(rig, Claim(2));
        }

        [Test]
        public void RigOutputBatchAppliesFullLocalPoseAndPreservesInputAndUnownedTransforms()
        {
            var checkpoint = Prepare(out var snapshot); var rig = NewRig(); BindRig(rig, 5);
            var lease = Lease(rig, Claim(2), Claim(8)); var before = ReadRig(rig);
            var head = Proposed(before, 2, new Vector3(.01f, .02f, -.03f), new Vector3(13, 29, -7));
            var hand = Proposed(before, 8, new Vector3(-.03f, .01f, .02f), new Vector3(-9, 4, 19));
            Assert.That(lease.TryApply(7, 1, before, new[] { hand, head }, checkpoint, out string error), Is.True, error);
            var after = ReadRig(rig); Assert.That(after.Revision, Is.EqualTo(before.Revision + 1));
            AssertMatrixNear(Local(rig.Joints[2]), head.ParentFromJoint); AssertMatrixNear(Local(rig.Joints[8]), hand.ParentFromJoint);
            for (int i = 0; i < rig.Joints.Length; ++i)
                if (i != 2 && i != 8)
                {
                    Assert.That(Local(rig.Joints[i]), Is.EqualTo(before.LocalPoses[i].ParentFromJoint));
                    Assert.That(rig.Joints[i].localToWorldMatrix, Is.EqualTo(before.WorldPoses[i]));
                }
            Assert.That(before.LocalPoses[2].ParentFromJoint, Is.Not.EqualTo(after.LocalPoses[2].ParentFromJoint));
            Assert.Throws<NotSupportedException>(() => ((IList<RigJointPose>)before.LocalPoses)[0] = head);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(checkpoint.Body));
        }

        [Test]
        public void RigOutputSingleComposerCanOwnCoupledAncestorAndDescendant()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(0), Claim(1), Claim(2)); var before = ReadRig(rig);
            var pelvis = Proposed(before, 0, new Vector3(.1f, -.2f, .3f), new Vector3(0, 21, 0));
            var chest = Proposed(before, 1, rotation: new Vector3(11, 0, -7));
            var head = Proposed(before, 2, rotation: new Vector3(-5, 9, 2));
            Assert.That(lease.TryApply(7, 1, before, new[] { head, chest, pelvis }, checkpoint, out string error), Is.True, error);
            AssertMatrixNear(rig.Joints[2].localToWorldMatrix, rig.Origin.localToWorldMatrix *
                pelvis.ParentFromJoint * chest.ParentFromJoint * head.ParentFromJoint);
            Assert.That(Local(rig.Joints[4]), Is.EqualTo(before.LocalPoses[4].ParentFromJoint));
            Assert.That(rig.Joints[4].localToWorldMatrix, Is.Not.EqualTo(before.WorldPoses[4]), "Ancestor world effects are real.");
        }

        [Test]
        public void RigOutputRejectsUnownedLocalDofsWithoutDiscardingThem()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var position = Lease(rig, Claim(2, RigPoseDofs.Position)); var before = ReadRig(rig);
            Assert.That(position.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up, new Vector3(10, 20, 30)) },
                checkpoint, out _), Is.False); AssertUnchanged(rig, before);
            position.Dispose(); var rotation = Lease(rig, Claim(2, RigPoseDofs.Rotation));
            Assert.That(rotation.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out _), Is.False);
            AssertUnchanged(rig, before);
            Assert.That(rotation.TryApply(7, 1, before, new[] { Proposed(before, 2, rotation: new Vector3(10, 20, 30)) },
                checkpoint, out string error), Is.True, error);
            Assert.That(rig.Joints[2].localPosition, Is.EqualTo((Vector3)before.LocalPoses[2].ParentFromJoint.GetColumn(3)));
        }

        [Test]
        public void RigOutputMalformedFinalRowNeverPartiallyWritesOrConsumesSequence()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2), Claim(8)); var before = ReadRig(rig);
            var head = Proposed(before, 2, Vector3.up); var hand = Proposed(before, 8, Vector3.left);
            Matrix4x4 nan = hand.ParentFromJoint; nan.m03 = float.NaN;
            foreach (var rows in new[] {
                new[] { head }, new[] { head, head },
                new[] { head, new RigJointPose("wrong-name", 8, hand.ParentFromJoint) },
                new[] { head, new RigJointPose(hand.Name, 8, nan) },
                new[] { head, new RigJointPose(hand.Name, 8, Matrix4x4.Scale(Vector3.one * 2)) }
            })
            {
                Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out _), Is.False);
                AssertUnchanged(rig, before); Assert.That(ReadRig(rig).Revision, Is.EqualTo(before.Revision));
            }
            Assert.That(lease.TryApply(7, 1, before, new[] { head, hand }, checkpoint, out string error), Is.True, error);
        }

        [Test]
        public void RigOutputRejectsComposedWorldOverflowBeforeAnyWrite()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(0), Claim(1)); var before = ReadRig(rig);
            var huge = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 0, huge), Proposed(before, 1, huge) },
                checkpoint, out _), Is.False); AssertUnchanged(rig, before);
        }

        [Test]
        public void RigOutputRefreshesBasisBetweenDisjointWritersAndRejectsForeignReadback()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var head = Lease(rig, Claim(2)); var hand = Lease(rig, Claim(8)); var before = ReadRig(rig);
            var other = NewRig(); BindRig(other);
            Assert.That(head.TryApply(7, 1, ReadRig(other), new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out _), Is.False);
            Assert.That(head.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out string error), Is.True, error);
            var after = ReadRig(rig);
            Assert.That(hand.TryApply(7, 1, before, new[] { Proposed(before, 8, Vector3.left) }, checkpoint, out _), Is.False);
            AssertUnchanged(rig, after);
            Assert.That(hand.TryApply(7, 1, after, new[] { Proposed(after, 8, Vector3.left) }, checkpoint, out error), Is.True, error);
        }

        [Test]
        public void RigOutputGenerationSequenceAndRetiredLeaseCannotAffectReplacement()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var old = Lease(rig, Claim(2)); var before = ReadRig(rig); var rows = new[] { Proposed(before, 2, Vector3.up) };
            Assert.That(old.TryApply(8, 1, before, rows, checkpoint, out _), Is.False);
            Assert.That(old.TryApply(7, 0, before, rows, checkpoint, out _), Is.False);
            Assert.That(old.TryApply(7, 1, before, rows, checkpoint, out string error), Is.True, error);
            var after = ReadRig(rig);
            Assert.That(old.TryApply(7, 1, after, new[] { Proposed(after, 2) }, checkpoint, out _), Is.False);
            old.Dispose(); var next = Lease(rig, Claim(2)); old.Dispose();
            Assert.That(old.TryApply(7, 2, after, new[] { Proposed(after, 2) }, checkpoint, out _), Is.False);
            Assert.That(next.TryApply(7, 1, after, new[] { Proposed(after, 2, Vector3.down) }, checkpoint, out error), Is.True, error);
        }

        [Test]
        public void RigOutputDeadlineRevokesWithoutUnityTickOrResettingLastPose()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig); var before = ReadRig(rig);
            Assert.That(rig.Owner.TryAcquire("short", 1, RigDeadline(150000000), new[] { Claim(2) }, out var old, out string error), Is.True, error);
            Assert.That(old.TryApply(1, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out error), Is.True, error);
            var held = ReadRig(rig); Thread.Sleep(180);
            ReadRig(rig); Assert.That(old.IsClosed, Is.True); AssertUnchanged(rig, held);
            Assert.That(old.TryApply(1, 2, held, new[] { Proposed(held, 2) }, checkpoint, out _), Is.False);
            Assert.That(rig.Owner.TryAcquire("replacement", 2, RigDeadline(), new[] { Claim(2) }, out var next, out error), Is.True, error);
            old.Dispose(); Assert.That(next.IsClosed, Is.False);
        }

        [Test]
        public void RigOutputRejectsUnboundedMalformedClaimsAndFreezesValidClaims()
        {
            var rig = NewRig(); BindRig(rig);
            foreach (var claims in new[] { Array.Empty<RigJointClaim>(), new[] { Claim(2), Claim(2) },
                new[] { new RigJointClaim("head", 3, RigPoseDofs.Pose) }, new[] { new RigJointClaim("origin", -1, RigPoseDofs.Pose) },
                new[] { Claim(2, 0) }, new[] { Claim(2, (RigPoseDofs)4) } })
                Assert.That(rig.Owner.TryAcquire("bad", 1, RigDeadline(), claims, out _, out _), Is.False);
            foreach (long deadline in new[] { 0L, -1L, long.MaxValue, RigDeadline(2000000000) })
                Assert.That(rig.Owner.TryAcquire("bad", 1, deadline, new[] { Claim(2) }, out _, out _), Is.False);
            Assert.That(rig.Owner.TryAcquire("bad", 0, RigDeadline(), new[] { Claim(2) }, out _, out _), Is.False);
            var original = new[] { Claim(2) }; var lease = Lease(rig, original); original[0] = Claim(0);
            Assert.That(lease.Claims[0].Index, Is.EqualTo(2));
            Assert.Throws<NotSupportedException>(() => ((IList<RigJointClaim>)lease.Claims)[0] = Claim(0));
        }

        [Test]
        public void RigOutputInvalidatedNativeSceneRejectsWholeBatchWithoutClearingBody()
        {
            var checkpoint = Prepare(out var snapshot); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2), Claim(8)); var before = ReadRig(rig);
            Assert.That(SpatialBlendAPI.TrySetRichValidityPolicyForTest(_receiver.Registration.NativeSpace, 1, out string error), Is.True, error);
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up), Proposed(before, 8, Vector3.left) },
                checkpoint, out _), Is.False); AssertUnchanged(rig, before);
            Assert.That(checkpoint.IsWithdrawn, Is.True); Assert.That(rig.Owner.IsClosed, Is.False);
            Assert.That(_binding.TryGetBodyForRealization(snapshot, out var retained, out error), Is.True, error);
            Assert.That(retained, Is.SameAs(checkpoint.Body));
        }

        [Test]
        public void RigOutputNewBodyRequiresNewPreparationWithoutExtendingLease()
        {
            var old = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2)); var before = ReadRig(rig); long deadline = lease.ValidUntilNativeNanoseconds;
            Assert.That(_binding.TrySubmit(Frame(2, 1, 0), out string error), Is.True, error);
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, old, out _), Is.False);
            AssertUnchanged(rig, before);
            Assert.That(_binding.TryPrepareRealization(Solve(), 1000000000, out var fresh, out error), Is.True, error);
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, fresh, out error), Is.True, error);
            Assert.That(lease.ValidUntilNativeNanoseconds, Is.EqualTo(deadline));
        }

        [Test]
        public void RigOutputBodyClearOrClosedInputCannotAuthorizeWrites()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2)); var before = ReadRig(rig);
            Assert.That(_binding.TryClear(out string error), Is.True, error);
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out _), Is.False);
            AssertUnchanged(rig, before);
            Assert.That(_binding.TryClose(out error), Is.True, error);
            Assert.That(rig.Owner.TryRefreshOwnership(out _), Is.False);
            Assert.That(rig.Owner.IsClosed && lease.IsClosed, Is.True); AssertUnchanged(rig, before);
        }

        [Test]
        public void RigOutputChangedHierarchyConfigurationOrOriginRevokesWithoutRollback()
        {
            foreach (Action<OutputRig> change in new Action<OutputRig>[] {
                rig => rig.Joints[2].name = "renamed",
                rig => rig.Joints[2].localScale = Vector3.one * 2,
                rig => rig.Joints[2].SetParent(rig.Joints[0], true),
                rig => new GameObject("undeclared").transform.SetParent(rig.Origin, false),
                rig => rig.Joints[2].gameObject.AddComponent<RigOutputMarker>(),
                rig => rig.Origin.position += Vector3.up
            })
            {
                var rig = NewRig(); BindRig(rig); var lease = Lease(rig, Claim(2)); change(rig);
                var changed = Local(rig.Joints[2]);
                Assert.That(rig.Owner.TryRefreshOwnership(out _), Is.False);
                Assert.That(rig.Owner.IsClosed && lease.IsClosed, Is.True);
                Assert.That(Local(rig.Joints[2]), Is.EqualTo(changed), "No rollback over an external change.");
            }
        }

        [Test]
        public void RigOutputPinsAncestorsEvenWhenWorldPoseIsPreserved()
        {
            var firstParent = new GameObject("manual-parent-a"); _created.Add(firstParent);
            var secondParent = new GameObject("manual-parent-b"); _created.Add(secondParent);
            var rig = NewRig(firstParent.transform); BindRig(rig); var lease = Lease(rig, Claim(2));
            Matrix4x4 world = rig.Origin.localToWorldMatrix;
            rig.Origin.SetParent(secondParent.transform, true); AssertMatrixNear(rig.Origin.localToWorldMatrix, world);
            Assert.That(rig.Owner.TryRefreshOwnership(out _), Is.False); Assert.That(lease.IsClosed, Is.True);
            var another = NewRig(firstParent.transform); BindRig(another);
            firstParent.AddComponent<RigOutputMarker>();
            Assert.That(another.Owner.TryRefreshOwnership(out _), Is.False);
        }

        [Test]
        public void RigOutputExternalPoseWriterIsDetectedButNeverOverwritten()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2)); var before = ReadRig(rig);
            rig.Joints[5].localRotation = Quaternion.Euler(11, 22, 33);
            Matrix4x4 foreign = Local(rig.Joints[5]);
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out _), Is.False);
            Assert.That(rig.Owner.IsClosed, Is.True); Assert.That(Local(rig.Joints[5]), Is.EqualTo(foreign));
            Assert.That(Local(rig.Joints[2]), Is.EqualTo(before.LocalPoses[2].ParentFromJoint));
        }

        [Test]
        public void RigOutputWorkerCallsFailBeforeUnityAccessWithoutPoisoningOwner()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2)); var before = ReadRig(rig); var rows = new[] { Proposed(before, 2, Vector3.up) };
            bool bind = true, read = true, acquire = true, apply = true, close = true, ownerClose = true, refresh = true;
            Exception exception = null;
            var thread = new Thread(() => {
                try {
                    bind = CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, rig.Joints, Array.Empty<int>(),
                        rig.WorldFromReceiver, "synthetic", out _, out _);
                    read = rig.Owner.TryReadPose(out _, out _);
                    acquire = rig.Owner.TryAcquire("worker", 1, 1, new[] { Claim(8) }, out _, out _);
                    apply = lease.TryApply(7, 1, before, rows, checkpoint, out _);
                    close = lease.TryClose(out _); ownerClose = rig.Owner.TryClose(out _); refresh = rig.Owner.TryRefreshOwnership(out _);
                } catch (Exception caught) { exception = caught; }
            });
            thread.Start(); Assert.That(thread.Join(2000), Is.True);
            Assert.That(exception, Is.Null); Assert.That(bind || read || acquire || apply || close || ownerClose || refresh, Is.False);
            Assert.That(rig.Owner.IsClosed || lease.IsClosed || checkpoint.IsWithdrawn, Is.False);
            Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out string error), Is.True, error);
        }

        private sealed class ReentrantRigList<T> : IReadOnlyList<T>
        {
            private readonly T[] _items; private Action _callback;
            internal ReentrantRigList(T[] items, Action callback) { _items = items; _callback = callback; }
            public int Count => _items.Length;
            public T this[int index] { get { var callback = _callback; _callback = null; callback?.Invoke(); return _items[index]; } }
            public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Test]
        public void RigOutputReentrantClaimsCannotEscapeRetirementOrCollisionChecks()
        {
            var rig = NewRig(); BindRig(rig); CalibratedTransformRigOutputOwner.RigWriteLease winner = null;
            var claims = new ReentrantRigList<RigJointClaim>(new[] { Claim(2) }, () => winner = Lease(rig, Claim(2)));
            Assert.That(rig.Owner.TryAcquire("outer", 8, RigDeadline(), claims, out _, out _), Is.False);
            Assert.That(winner.IsClosed, Is.False); winner.Dispose();
            claims = new ReentrantRigList<RigJointClaim>(new[] { Claim(2) }, () => rig.Owner.Dispose());
            Assert.That(rig.Owner.TryAcquire("outer", 8, RigDeadline(), claims, out _, out _), Is.False);
            Assert.That(rig.Owner.IsClosed, Is.True);
        }

        [Test]
        public void RigOutputReentrantProposalCannotWriteAfterLeaseReplacement()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig);
            var lease = Lease(rig, Claim(2)); var before = ReadRig(rig);
            CalibratedTransformRigOutputOwner.RigWriteLease replacement = null;
            var rows = new ReentrantRigList<RigJointPose>(new[] { Proposed(before, 2, Vector3.up) }, () => {
                lease.Dispose(); replacement = Lease(rig, Claim(2));
            });
            Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out _), Is.False); AssertUnchanged(rig, before);
            Assert.That(replacement.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out string error), Is.True, error);
        }

        [Test]
        public void RigOutputReentrantBindCannotRetainClosedInput()
        {
            var rig = NewRig();
            var joints = new ReentrantRigList<Transform>(rig.Joints, () => _binding.TryClose(out _));
            Assert.That(CalibratedTransformRigOutputOwner.TryBind(_binding, rig.Origin, joints, Array.Empty<int>(),
                rig.WorldFromReceiver, "synthetic", out var rejected, out _), Is.False);
            Assert.That(rejected, Is.Null);
        }

        [Test]
        public void RigOutputUnissuedLeaseAndCopiedReadbackCannotForgeAuthority()
        {
            var checkpoint = Prepare(out _); var rig = NewRig(); BindRig(rig, 5); var before = ReadRig(rig);
            var fake = new CalibratedTransformRigOutputOwner.RigWriteLease(rig.Owner, "forged", 7, RigDeadline(),
                new[] { Claim(0) }, new HashSet<(int, RigPoseDofs)>());
            Assert.That(fake.TryApply(7, 1, before, new[] { Proposed(before, 0, Vector3.up) }, checkpoint, out _), Is.False);
            Assert.That(fake.TryClose(out _), Is.False); AssertUnchanged(rig, before);
            var valid = Lease(rig, Claim(2));
            var copied = new RigPoseReadback(rig.Owner, before.Revision, before.LocalPoses.ToArray(), before.WorldPoses.ToArray());
            Assert.That(valid.TryApply(7, 1, copied, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out _), Is.False);
            AssertUnchanged(rig, before);
            Assert.That(valid.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up) }, checkpoint, out string error), Is.True, error);
        }

        [Test]
        public void RigOwnerActualWorldBoundToleratesFloatCompositionOnly()
        {
            Assert.That(CalibratedTransformRigOutputOwner.ActualWorldProper(Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.Euler(10, 20, 30), Vector3.one)), Is.True);
            var nearlyRigid = Matrix4x4.identity; nearlyRigid.m00 = 1 + 3e-7f;
            Assert.That(ReceiverInputValidation.TryNativePose(nearlyRigid, out _), Is.True);
            nearlyRigid.m00 = 1 + 1.2e-6f;
            Assert.That(ReceiverInputValidation.TryNativePose(nearlyRigid, out _), Is.False, "strict native gate");
            Assert.That(CalibratedTransformRigOutputOwner.ActualWorldProper(nearlyRigid), Is.True, "float-composition bound");
            Assert.That(CalibratedTransformRigOutputOwner.ActualWorldProper(Matrix4x4.Scale(new Vector3(1.0001f, 1, 1))), Is.False);
            Assert.That(CalibratedTransformRigOutputOwner.ActualWorldProper(Matrix4x4.Scale(new Vector3(-1, 1, 1))), Is.False);
            var shear = Matrix4x4.identity; shear.m01 = 1e-4f;
            Assert.That(CalibratedTransformRigOutputOwner.ActualWorldProper(shear), Is.False);
        }

        [Test]
        public void RigOwnerDoesNotRevokeItselfWhenFloatCompositionExceedsStrictGramBound()
        {
            // Regression: an owner revoked itself after its own float-composed write. Search, on an unowned copy of the biped, for
            // admissible local rotations of the 5-deep chain pelvis→chest→
            // left_shoulder→left_elbow→left_wrist whose float-composed world
            // matrix fails the strict native gate (Gram > 1e-12). Then write the
            // same locals through a real lease. Before the fix the next owner
            // check revoked ownership ("Manual rig name, topology, lifetime or
            // scale changed"); now the owner stays open.
            int[] chain = { 0, 1, 3, 4 };
            var rig = NewRig();
            var reference = rig.Joints.Select(j => (j.localPosition, j.localRotation)).ToArray();
            var random = new System.Random(20260925);
            Quaternion[] found = null; string strictJoint = null;
            for (int attempt = 0; attempt < 200000 && found == null; ++attempt)
            {
                var candidate = new Quaternion[chain.Length]; bool admissible = true;
                for (int k = 0; k < chain.Length; ++k)
                {
                    var q = Quaternion.Euler((float)(random.NextDouble() * 360 - 180), (float)(random.NextDouble() * 360 - 180), (float)(random.NextDouble() * 360 - 180));
                    // Exactly the rotation the owner will set from an admitted proposal matrix.
                    candidate[k] = Quaternion.Normalize(RigPoseMath.DerivedRigid(reference[chain[k]].localPosition, q).rotation);
                    rig.Joints[chain[k]].localRotation = candidate[k];
                    // The owner re-checks each written local as TRS(position, rotation) with the strict gate.
                    admissible &= ReceiverInputValidation.TryNativePose(Matrix4x4.TRS(reference[chain[k]].localPosition, candidate[k], Vector3.one), out _);
                }
                // Violation only at depth ≥ 4 (left_elbow: origin→pelvis→chest→left_shoulder→left_elbow,
                // or left_wrist), with every shallower chain joint passing the strict gate.
                bool shallowStrict = admissible && new[] { 0, 1, 3 }.All(j => ReceiverInputValidation.TryNativePose(rig.Joints[j].localToWorldMatrix, out _));
                foreach (int j in new[] { 4, 5 })
                    if (shallowStrict && !ReceiverInputValidation.TryNativePose(rig.Joints[j].localToWorldMatrix, out _) &&
                        rig.Joints.All(t => CalibratedTransformRigOutputOwner.ActualWorldProper(t.localToWorldMatrix)))
                    { found = candidate; strictJoint = rig.Joints[j].name; break; }
            }
            for (int j = 0; j < rig.Joints.Length; ++j) rig.Joints[j].SetLocalPositionAndRotation(reference[j].localPosition, reference[j].localRotation);
            Assert.That(found, Is.Not.Null, "no strict-gate violation found; the regression is not exercised");
            TestContext.WriteLine("strict Gram violation at " + strictJoint);
            BindRig(rig); var checkpoint = Prepare(out _); var before = ReadRig(rig);
            var lease = Lease(rig, chain.Select(i => Claim(i, RigPoseDofs.Rotation)).ToArray());
            var proposal = chain.Select((i, k) => new RigJointPose(before.LocalPoses[i].Name, i,
                RigPoseMath.DerivedRigid(reference[i].localPosition, found[k]))).ToArray();
            Assert.That(lease.TryApply(7, 1, before, proposal, checkpoint, out string error), Is.True, error);
            Assert.That(ReceiverInputValidation.TryNativePose(rig.Joints.Single(t => t.name == strictJoint).localToWorldMatrix, out _), Is.False,
                "the written rig really exceeds the strict gate");
            Assert.That(rig.Owner.TryReadPose(out _, out error), Is.True, error);
            Assert.That(rig.Owner.IsClosed, Is.False);
        }

        [Test]
        public void RigOwnerAcceptsAStrictProposalWhoseFloatRebuildExceedsTheStrictGramBound()
        {
            // Real K2 recording: the hips
            // proposal passed the strict native gate (Gram 6e-15) but the owner's own
            // TRS(normalised extracted rotation) rebuild measured 1.3e-12 and the write
            // was rejected ("Controller local pose must be finite and proper before
            // composition"). The owner now rebuilds with RigPoseMath.DerivedRigid and keeps
            // the strict gate. Search for such a rotation, write it through a real lease.
            var rig = NewRig();
            var position = rig.Joints[1].localPosition;
            var random = new System.Random(20260926);
            Matrix4x4? found = null;
            for (int attempt = 0; attempt < 200000 && found == null; ++attempt)
            {
                var q = Quaternion.Euler((float)(random.NextDouble() * 360 - 180), (float)(random.NextDouble() * 360 - 180), (float)(random.NextDouble() * 360 - 180));
                Matrix4x4 proposal = RigPoseMath.DerivedRigid(position, q);
                Matrix4x4 rebuilt = Matrix4x4.TRS(position, Quaternion.Normalize(proposal.rotation), Vector3.one);
                if (ReceiverInputValidation.TryNativePose(proposal, out _) && !ReceiverInputValidation.TryNativePose(rebuilt, out _) &&
                    CalibratedTransformRigOutputOwner.ActualWorldProper(rebuilt))
                    found = proposal;
            }
            Assert.That(found, Is.Not.Null, "no strict proposal with a strict-failing float rebuild found; the regression is not exercised");
            Assert.That(ReceiverInputValidation.TryNativePose(RigPoseMath.DerivedRigid(position, Quaternion.Normalize(found.Value.rotation)), out _), Is.True,
                "the owner's DerivedRigid rebuild keeps the strict gate");
            BindRig(rig); var checkpoint = Prepare(out _); var before = ReadRig(rig);
            var lease = Lease(rig, new[] { Claim(1, RigPoseDofs.Rotation) });
            var pose = new RigJointPose(before.LocalPoses[1].Name, 1, found.Value);
            Assert.That(lease.TryApply(7, 1, before, new[] { pose }, checkpoint, out string error), Is.True, error);
            Assert.That(Quaternion.Angle(rig.Joints[1].localRotation, found.Value.rotation), Is.LessThan(.001f));
            Assert.That(rig.Owner.TryReadPose(out _, out error), Is.True, error);
            Assert.That(rig.Owner.IsClosed, Is.False);
        }
    }

    public sealed class RigOutputMarker : MonoBehaviour { }
}
#endif
