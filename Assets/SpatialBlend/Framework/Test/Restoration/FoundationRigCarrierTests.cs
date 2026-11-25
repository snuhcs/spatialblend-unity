// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Motion;
using UnityEngine;

namespace SpatialBlend.Tests
{
    public sealed partial class FoundationReceiverBodyNativeTests
    {
        private ReceiverBodyRealizationCheckpoint CarrierFixture(out OutputRig rig, out Transform carrier,
            bool bind = true, int[] reserved = null)
        {
            Assert.That(_binding.TryClose(out string error), Is.True, error);
            Assert.That(_receiver.TryBeginReceiverInput(17, 19, out _writer, out error), Is.True, error);
            Assert.That(_writer.TryReadAcquisitionClock(out long now, out error), Is.True, error);
            // Tilted BODY calibration as well as a different scene calibration:
            // receiver-plane motion must not mean local-Y or scene-Y clamping.
            Assert.That(ReceiverBodyProfile.TryCreate(_writer.Input, "fixture-reader", 11, ReceiverBodyFixture.Clock(_writer.Input, now),
                ReceiverBodyEvidenceKind.SyntheticFixture, "carrier-body-reference", ReceiverBodyFixture.Skeleton(),
                ReceiverBodyFixture.Pose(new Vector3(4, .2f, -2), new Vector3(11, 47, -9)), ReceiverBodyFixture.Anchors(),
                out var profile, out error), Is.True, error);
            Assert.That(ReceiverBodyInputBinding.TryBind(_writer, profile, out _binding, out error), Is.True, error);
            var checkpoint = Prepare(out _);
            rig = NewRig(); carrier = new GameObject(profile.Input.RootAnchor).transform;
            carrier.SetParent(rig.Origin, false); rig.Joints[0].SetParent(carrier, false);
            if (bind) BindCarrier(rig, carrier, checkpoint, reserved ?? Array.Empty<int>());
            return checkpoint;
        }
        private Matrix4x4 CarrierCalibration(ReceiverBodyRealizationCheckpoint checkpoint) =>
            checkpoint.Profile.ReceiverWorldFromBody.inverse * checkpoint.Body.GeometricInput.Anchors.Single(
                row => row.Name == checkpoint.Profile.Input.RootAnchor).WorldPose;
        private void BindCarrier(OutputRig rig, Transform carrier, ReceiverBodyRealizationCheckpoint checkpoint, params int[] reserved)
        {
            Assert.That(CalibratedTransformRigOutputOwner.TryBindWithLocomotion(_binding, rig.Origin, carrier,
                CarrierCalibration(checkpoint), checkpoint, rig.Joints, reserved, rig.WorldFromReceiver,
                "synthetic-carrier-calibration", out var owner, out string error), Is.True, error);
            rig.Owner = owner; _rigOwners.Add(owner);
        }
        private RigJointClaim CarrierClaim(RigPoseDofs dofs = RigPoseDofs.ReceiverPlanarPosition) =>
            new RigJointClaim(_binding.Profile.Input.RootAnchor, CalibratedTransformRigOutputOwner.LocomotionCarrierIndex, dofs);
        private RigJointPose CarrierProposal(RigPoseReadback basis, Vector3 receiverDelta)
        {
            Matrix4x4 parent = basis.ReceiverWorldFromRigBody * basis.CarrierLocalPose.inverse;
            Vector3 local = (Vector3)basis.CarrierLocalPose.GetColumn(3) + parent.inverse.MultiplyVector(receiverDelta);
            return new RigJointPose(_binding.Profile.Input.RootAnchor, -1,
                Matrix4x4.TRS(local, basis.CarrierLocalPose.rotation, Vector3.one));
        }

        [Test]
        public void RigCarrierCalibratesIndependentRootAndActualNestedBodyFrame()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var actual = ReadRig(rig);
            Assert.That(rig.Owner.HasLocomotionCarrier && actual.HasLocomotionCarrier, Is.True);
            Assert.That(rig.Owner.LocomotionCalibration, Is.SameAs(checkpoint));
            AssertMatrixNear(actual.ReceiverWorldFromRigBody, rig.WorldFromReceiver.inverse * carrier.localToWorldMatrix);
            AssertMatrixNear(actual.ReceiverWorldFromLocomotionRoot, checkpoint.Body.GeometricInput.Anchors.Single(
                row => row.Name == _binding.Profile.Input.RootAnchor).WorldPose);
            Assert.That(Vector3.Distance(actual.ReceiverWorldFromLocomotionRoot.GetColumn(3),
                (rig.WorldFromReceiver.inverse * rig.Joints[0].localToWorldMatrix).GetColumn(3)), Is.GreaterThan(.5));
            Assert.That(rig.Owner.ProfileFingerprint.Length, Is.EqualTo(64));
        }

        [Test]
        public void RigCarrierRejectsWrongCalibrationAndMalformedOrWithdrawnEvidence()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier, false);
            bool Bind(Matrix4x4 calibration, ReceiverBodyRealizationCheckpoint cp) =>
                CalibratedTransformRigOutputOwner.TryBindWithLocomotion(_binding, rig.Origin, carrier, calibration, cp,
                    rig.Joints, Array.Empty<int>(), rig.WorldFromReceiver, "carrier", out _, out _);
            Assert.That(Bind(Matrix4x4.identity, checkpoint), Is.False);
            Assert.That(Bind(CarrierCalibration(checkpoint), null), Is.False);
            Assert.That(Bind(CarrierCalibration(checkpoint), new ReceiverBodyRealizationCheckpoint(null, null, null, null, 1, 2, 1)), Is.False);
            Assert.That(checkpoint.TryWithdraw(out _), Is.True);
            Assert.That(Bind(CarrierCalibration(checkpoint), checkpoint), Is.False);
            Assert.That(carrier.localPosition, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void RigCarrierMalformedApplyEvidenceRejectsWithoutThrowOrPartialWrite()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var before = ReadRig(rig);
            var lease = Lease(rig, CarrierClaim(), Claim(2));
            var rows = new[] { Proposed(before, 2, Vector3.up), CarrierProposal(before, Vector3.right * .04f) };
            foreach (var malformed in new[] {
                new ReceiverBodyRealizationCheckpoint(null, null, null, null, 1, 2, 1),
                new ReceiverBodyRealizationCheckpoint(_binding, null, checkpoint.Body, null, 1, 2, 1),
                new ReceiverBodyRealizationCheckpoint(null, checkpoint.Delivery, checkpoint.Body, checkpoint.PreparedValidity,
                    checkpoint.PreparedAtNativeNanoseconds, checkpoint.ValidUntilNativeNanoseconds, checkpoint.MaximumNativeAgeNanoseconds) })
            {
                Assert.DoesNotThrow(() => Assert.That(lease.TryApply(7, 1, before, rows, malformed, out _), Is.False));
                AssertUnchanged(rig, before); Assert.That(carrier.localPosition, Is.EqualTo(Vector3.zero));
                Assert.DoesNotThrow(() => Assert.That(malformed.TryRevalidate(out _, out _), Is.False));
            }
            Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out string error), Is.True, error);
            lease.TryClose(out _);
        }

        [Test]
        public void RigCarrierRejectsPelvisAliasDetachedMarkerAndUndeclaredTopology()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier, false);
            bool Bind(Transform selected) => CalibratedTransformRigOutputOwner.TryBindWithLocomotion(_binding, rig.Origin,
                selected, CarrierCalibration(checkpoint), checkpoint, rig.Joints, Array.Empty<int>(), rig.WorldFromReceiver,
                "carrier", out _, out _);
            Assert.That(Bind(rig.Joints[0]), Is.False); Assert.That(Bind(rig.Origin), Is.False);
            carrier.name = "decorative-marker"; Assert.That(Bind(carrier), Is.False); carrier.name = _binding.Profile.Input.RootAnchor;
            rig.Joints[0].SetParent(rig.Origin, false); Assert.That(Bind(carrier), Is.False); rig.Joints[0].SetParent(carrier, false);
            carrier.localScale = new Vector3(1, 2, 1); Assert.That(Bind(carrier), Is.False); carrier.localScale = Vector3.one;
            var extra = new GameObject("undeclared"); extra.transform.SetParent(carrier, false);
            Assert.That(Bind(carrier), Is.False); UnityEngine.Object.DestroyImmediate(extra);
            BindCarrier(rig, carrier, checkpoint);
        }

        [Test]
        public void RigCarrierRequiresPlanarClaimAndReservesWholeSubtreeEffects()
        {
            var checkpoint = CarrierFixture(out var rig, out _);
            foreach (var dofs in new[] { RigPoseDofs.Position, RigPoseDofs.Rotation, RigPoseDofs.Pose, (RigPoseDofs)0 })
                Assert.That(rig.Owner.TryAcquire("wrong-dofs", 7, RigDeadline(), new[] { CarrierClaim(dofs) }, out _, out _), Is.False);
            Assert.That(rig.Owner.TryAcquire("wrong-joint-dofs", 7, RigDeadline(), new[] { Claim(0, RigPoseDofs.ReceiverPlanarPosition) }, out _, out _), Is.False);
            var head = Lease(rig, Claim(2));
            Assert.That(rig.Owner.TryAcquire("carrier", 7, RigDeadline(), new[] { CarrierClaim() }, out _, out _), Is.False);
            head.TryClose(out _); var root = Lease(rig, CarrierClaim());
            Assert.That(rig.Owner.TryAcquire("pelvis", 7, RigDeadline(), new[] { Claim(0) }, out _, out _), Is.False);
            root.TryClose(out _);
            var composed = Lease(rig, CarrierClaim(), Claim(0), Claim(2)); composed.TryClose(out _);
            Assert.That(checkpoint.TryRevalidate(out _, out _), Is.True);
        }

        [Test]
        public void RigCarrierReservedWorldJointBlocksCarrierWithoutTakeover()
        {
            CarrierFixture(out var rig, out _, reserved: new[] { 2 }); var before = ReadRig(rig);
            Assert.That(rig.Owner.TryAcquire("carrier", 7, RigDeadline(), new[] { CarrierClaim() }, out _, out _), Is.False);
            AssertUnchanged(rig, before); Assert.That(rig.Owner.IsClosed, Is.False);
        }

        [Test]
        public void RigCarrierPlanarBatchMovesActualBodyAndKeepsObservedInputSeparate()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var before = ReadRig(rig);
            var lease = Lease(rig, CarrierClaim(), Claim(2, RigPoseDofs.Rotation));
            Vector3 delta = new Vector3(.12f, 0, -.08f);
            var proposal = CarrierProposal(before, delta);
            Assert.That(Math.Abs(proposal.ParentFromJoint.m13), Is.GreaterThan(.001), "Tilted calibration requires local vertical displacement for receiver-planar motion.");
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, rotation: new Vector3(0, 15, 0)), proposal },
                checkpoint, out string error), Is.True, error);
            var after = ReadRig(rig); Vector3 worldDelta = rig.WorldFromReceiver.MultiplyVector(delta);
            for (int i = 0; i < rig.Joints.Length; ++i)
                Assert.That(Vector3.Distance(rig.Joints[i].position, (Vector3)before.WorldPoses[i].GetColumn(3) + worldDelta), Is.LessThan(.00002));
            Assert.That(Vector3.Distance(after.ReceiverWorldFromLocomotionRoot.GetColumn(3),
                (Vector3)before.ReceiverWorldFromLocomotionRoot.GetColumn(3) + delta), Is.LessThan(.00002));
            Assert.That(after.ReceiverWorldFromLocomotionRoot.m13, Is.EqualTo(before.ReceiverWorldFromLocomotionRoot.m13).Within(.00001));
            Assert.That(_binding.TryGetBodyForRealization(checkpoint.Snapshot, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(checkpoint.Body));
            Assert.That(body.GeometricInput.Sequence, Is.EqualTo(1));
            Assert.That(carrier.localPosition, Is.Not.EqualTo(Vector3.zero)); lease.TryClose(out _);
        }

        [Test]
        public void RigCarrierRejectsVerticalOrYawChangesBeforeAnyBatchWrite()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var before = ReadRig(rig);
            var lease = Lease(rig, CarrierClaim(), Claim(2));
            var vertical = CarrierProposal(before, Vector3.up * .01f);
            var yaw = new RigJointPose(_binding.Profile.Input.RootAnchor, -1, Matrix4x4.Rotate(Quaternion.Euler(0, 2, 0)));
            foreach (var bad in new[] { vertical, yaw })
            {
                Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2, Vector3.up), bad }, checkpoint, out _), Is.False);
                AssertUnchanged(rig, before); Assert.That(carrier.localPosition, Is.EqualTo(Vector3.zero));
            }
            Assert.That(lease.TryApply(7, 1, before, new[] { Proposed(before, 2), CarrierProposal(before, new Vector3(.03f, 0, .02f)) },
                checkpoint, out string error), Is.True, error); lease.TryClose(out _);
        }

        [Test]
        public void RigCarrierCalibrationIsHistoricalButEachApplyNeedsFreshInput()
        {
            var old = CarrierFixture(out var rig, out _); var before = ReadRig(rig); var lease = Lease(rig, CarrierClaim());
            Assert.That(_binding.TrySubmit(Frame(2, 1), out string error), Is.True, error);
            var snapshot = Solve(); Assert.That(_binding.TryPrepareRealization(snapshot, 1000000000, out var current, out error), Is.True, error);
            Assert.That(old.TryRevalidate(out _, out _), Is.False); Assert.That(rig.Owner.TryReadPose(out _, out error), Is.True, error);
            Assert.That(lease.TryApply(7, 1, before, new[] { CarrierProposal(before, Vector3.right * .04f) }, old, out _), Is.False);
            Assert.That(lease.TryApply(7, 1, before, new[] { CarrierProposal(before, Vector3.right * .04f) }, current, out error), Is.True, error);
            Assert.That(rig.Owner.LocomotionCalibration, Is.SameAs(old)); lease.TryClose(out _);
        }

        [Test]
        public void RigCarrierExternalWriterRevokesWithoutResettingOrClearingInput()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var lease = Lease(rig, CarrierClaim());
            carrier.localPosition = new Vector3(.01f, .02f, .03f); var held = carrier.localPosition;
            Assert.That(rig.Owner.TryReadPose(out _, out _), Is.False); Assert.That(rig.Owner.IsClosed && lease.IsClosed, Is.True);
            Assert.That(carrier.localPosition, Is.EqualTo(held));
            Assert.That(_binding.TryGetBodyForRealization(checkpoint.Snapshot, out var body, out _), Is.True);
            Assert.That(body, Is.SameAs(checkpoint.Body));
        }

        [Test]
        public void RigCarrierWrongThreadAndForgedReadbackCannotGainAuthority()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var before = ReadRig(rig); var lease = Lease(rig, CarrierClaim());
            bool accepted = true; Exception failure = null;
            var worker = new Thread(() => { try { accepted = rig.Owner.TryReadPose(out _, out _); } catch (Exception e) { failure = e; } });
            worker.Start(); Assert.That(worker.Join(2000), Is.True); Assert.That(failure, Is.Null); Assert.That(accepted, Is.False);
            var copy = new RigPoseReadback(rig.Owner, before.Revision, before.LocalPoses.ToArray(), before.WorldPoses.ToArray(),
                true, before.CarrierLocalPose, before.ReceiverWorldFromRigBody, before.ReceiverWorldFromLocomotionRoot);
            var rows = new[] { CarrierProposal(before, Vector3.right * .04f) };
            Assert.That(lease.TryApply(7, 1, copy, rows, checkpoint, out _), Is.False); AssertUnchanged(rig, before);
            Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out string error), Is.True, error);
            Assert.That(carrier.localPosition, Is.Not.EqualTo(Vector3.zero)); lease.TryClose(out _);
        }

        [Test]
        public void RigCarrierRetiredLeaseCannotPartiallyWriteOrReleaseReplacement()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var before = ReadRig(rig);
            var lease = Lease(rig, CarrierClaim(), Claim(2));
            CalibratedTransformRigOutputOwner.RigWriteLease replacement = null;
            var rows = new ReentrantRigList<RigJointPose>(new[] { CarrierProposal(before, Vector3.right * .04f), Proposed(before, 2) }, () => {
                lease.TryClose(out _); replacement = Lease(rig, CarrierClaim(), Claim(2));
            });
            Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out _), Is.False); AssertUnchanged(rig, before);
            Assert.That(carrier.localPosition, Is.EqualTo(Vector3.zero)); Assert.That(lease.TryClose(out _), Is.True);
            Assert.That(replacement.IsClosed, Is.False); replacement.TryClose(out _);
        }

        [Test]
        public void RigCarrierOriginalLeaseDeadlineAndTargetWindowFenceAllSetters()
        {
            var checkpoint = CarrierFixture(out var rig, out var carrier); var before = ReadRig(rig);
            Assert.That(rig.Owner.TryAcquire("short-carrier", 7, RigDeadline(60000000), new[] { CarrierClaim() }, out var lease, out string error), Is.True, error);
            var rows = new[] { CarrierProposal(before, Vector3.right * .04f) };
            Assert.That(rig.Owner.TryReadAcquisitionClock(out long now, out error), Is.True, error);
            Assert.That(lease.TryApplyWithin(7, 1, before, rows, checkpoint, now - 2, now - 1, out _), Is.False);
            Thread.Sleep(80);
            Assert.That(lease.TryApply(7, 1, before, rows, checkpoint, out _), Is.False); AssertUnchanged(rig, before);
            Assert.That(carrier.localPosition, Is.EqualTo(Vector3.zero)); Assert.That(lease.IsClosed, Is.True);
            var replacement = Lease(rig, CarrierClaim()); replacement.TryClose(out _);
        }
    }
}
#endif
