// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [Category("FoundationObservedCollaboratorBarrier")]
    public sealed class FoundationObservedCollaboratorAdmissionBarrierTests
    {
        [Test]
        public void ExactBatchIdStagesEveryParticipantBeforeOneAcknowledgement()
        {
            using (var barrier = NewBarrier())
            {
                Assert.That(barrier.TryStage(7, "alice", Frame(4, .4), out var first, out string error), Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(first, Is.Null);
                Assert.That(barrier.TryStage(7, "bob", Frame(9, .9), out var batch, out error), Is.EqualTo(ObservedCollaboratorStageResult.Ready), error);
                Assert.That(batch.BatchId, Is.EqualTo(7));
                var snapshots = batch.CopyFramesForNative();
                Assert.That(snapshots.Keys, Is.EquivalentTo(new[] { "alice", "bob" }));
                Assert.That(snapshots["alice"].sequence, Is.EqualTo(4));
                Assert.That(barrier.TryAcknowledgeNativeAdmission(batch, out error), Is.True, error);
                Assert.That(barrier.LastAcknowledgedBatchId, Is.EqualTo(7)); Assert.That(barrier.HasPendingBatch, Is.False);
            }
        }

        [Test]
        public void DuplicateOrInvalidMemberWithdrawsWholePendingBatchWithoutSequenceConsumption()
        {
            using (var barrier = NewBarrier())
            {
                Assert.That(barrier.TryStage(4, "alice", Frame(1, .1), out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Pending));
                Assert.That(barrier.TryStage(4, "alice", Frame(2, .2), out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Rejected));
                Assert.That(barrier.HasPendingBatch, Is.False); Assert.That(barrier.LastAcknowledgedBatchId, Is.EqualTo(0));
                Assert.That(barrier.TryStage(4, "alice", Frame(1, .1), out _, out string error), Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(barrier.TryAbort(4, out error), Is.True, error);
                Assert.That(barrier.TryStage(4, "alice", Frame(1, .1), out _, out error), Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
            }
        }

        [Test]
        public void AcknowledgedParticipantClocksRejectReplayAndOwnerThreadMutation()
        {
            using (var barrier = NewBarrier())
            {
                Assert.That(barrier.TryStage(1, "alice", Frame(3, .3), out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Pending));
                Assert.That(barrier.TryStage(1, "bob", Frame(5, .5), out var batch, out string error), Is.EqualTo(ObservedCollaboratorStageResult.Ready), error);
                Assert.That(barrier.TryAcknowledgeNativeAdmission(batch, out error), Is.True, error);
                Assert.That(barrier.TryStage(2, "alice", Frame(3, .6), out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Rejected));
                ObservedCollaboratorStageResult worker = Task.Run(() => barrier.TryStage(2, "alice", Frame(4, .6), out _, out _)).Result;
                Assert.That(worker, Is.EqualTo(ObservedCollaboratorStageResult.Rejected));
            }
        }

        [Test]
        public void AbortedReadyTokenCannotAcknowledgeAReplacementWithTheSameBatchId()
        {
            using (var barrier = NewBarrier())
            {
                Assert.That(barrier.TryStage(8, "alice", Frame(1, .1), out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Pending));
                Assert.That(barrier.TryStage(8, "bob", Frame(1, .1), out var abandoned, out string error), Is.EqualTo(ObservedCollaboratorStageResult.Ready), error);
                Assert.That(barrier.TryAbort(8, out error), Is.True, error);
                Assert.That(barrier.TryStage(8, "alice", Frame(1, .1), out _, out error), Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(barrier.TryStage(8, "bob", Frame(1, .1), out var replacement, out error), Is.EqualTo(ObservedCollaboratorStageResult.Ready), error);
                Assert.That(barrier.TryAcknowledgeNativeAdmission(abandoned, out error), Is.False);
                Assert.That(barrier.TryAcknowledgeNativeAdmission(replacement, out error), Is.True, error);
            }
        }

        [Test]
        public void MissingDeclaredAnchorRejectsTheWholePendingBatchAndSnapshotsAreDetached()
        {
            using (var barrier = NewBarrier())
            {
                Assert.That(barrier.TryStage(3, "alice", Frame(1, .1), out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Pending));
                var incomplete = Frame(1, .1); incomplete.anchors = new[] { incomplete.anchors[0] };
                Assert.That(barrier.TryStage(3, "bob", incomplete, out _, out _), Is.EqualTo(ObservedCollaboratorStageResult.Rejected));
                Assert.That(barrier.HasPendingBatch, Is.False);
                Assert.That(barrier.TryStage(3, "alice", Frame(1, .1), out _, out string error), Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(barrier.TryStage(3, "bob", Frame(1, .1), out var batch, out error), Is.EqualTo(ObservedCollaboratorStageResult.Ready), error);
                var firstCopy = batch.CopyFramesForNative();
                firstCopy["alice"].sequence = 999;
                Assert.That(batch.CopyFramesForNative()["alice"].sequence, Is.EqualTo(1));
                Assert.That(barrier.TryAcknowledgeNativeAdmission(batch, out error), Is.True, error);
            }
        }

        [Test]
        public void ParticipantBoundaryAcceptsSixtyFourAndRejectsSixtyFiveBeforeAnyBatchExists()
        {
            var maximum = new Dictionary<string, IReadOnlyCollection<string>>();
            for (int index = 0; index < 64; ++index)
                maximum.Add("actor-" + index, new[] { "root" });
            using (var barrier = new ObservedCollaboratorAdmissionBarrier(maximum))
            {
                Assert.That(barrier.TryStage(1, "actor-0", new AnchorPoseFrame {
                    sequence = 0, timeSeconds = 0d,
                    anchors = new[] { new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero,
                        worldRotation = Quaternion.identity } }
                }, out _, out string error), Is.EqualTo(ObservedCollaboratorStageResult.Pending), error);
                Assert.That(barrier.HasPendingBatch, Is.True);
            }
            maximum.Add("actor-64", new[] { "root" });
            Assert.Throws<ArgumentException>(() => new ObservedCollaboratorAdmissionBarrier(maximum));
        }

        private static ObservedCollaboratorAdmissionBarrier NewBarrier() =>
            new ObservedCollaboratorAdmissionBarrier(new Dictionary<string, IReadOnlyCollection<string>> {
                { "alice", new[] { "root", "head" } }, { "bob", new[] { "root", "head" } } });
        private static AnchorPoseFrame Frame(long sequence, double time) => new AnchorPoseFrame {
            sequence = sequence, timeSeconds = time,
            anchors = new[] { new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero, worldRotation = Quaternion.identity },
                              new AnchorPoseSample { anchorId = "head", worldPosition = Vector3.up, worldRotation = Quaternion.identity } } };
    }
}
#endif
