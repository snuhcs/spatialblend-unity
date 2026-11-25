// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    public sealed class FoundationRichMailboxTests
    {
        private const ulong NativeReferenceNanoseconds = 1000000000UL;

        private static SpatialBlendNativeClockMapping Mapping(double validForSeconds = 100)
        {
            SpatialBlendNativeClockMapping mapping;
            string error;
            Assert.IsTrue(SpatialBlendNativeClockMapping.TryCreate(4, 5,
                NativeReferenceNanoseconds, 10, 10.001, .01, 1,
                validForSeconds, out mapping, out error), error);
            return mapping;
        }

        // This intentionally builds a real immutable complete snapshot with a
        // channel/contributor rather than testing mailbox plumbing with a null
        // or DTO-invalid stand-in.
        private static RichRealizationSnapshot Snapshot(ulong lifetime, ulong revision,
                                                         RichSnapshotStatus status,
                                                         ulong receiptNanoseconds = NativeReferenceNanoseconds,
                                                         bool clockKnown = true,
                                                         bool receiptKnown = true,
                                                         ulong clockId = 4,
                                                         ulong clockEpoch = 5,
                                                         long receiverSpace = 1,
                                                         long receiverActor = 2)
        {
            var knownClockId = new RichKnownUInt64(clockKnown, clockId);
            var knownClockEpoch = new RichKnownUInt64(clockKnown, clockEpoch);
            var knownReceipt = new RichKnownUInt64(receiptKnown, receiptNanoseconds);
            var knownSolve = new RichKnownUInt64(receiptKnown, receiptNanoseconds + 1);
            var header = new RichRealizationHeader(receiverSpace, receiverActor,
                lifetime, revision, status, RichSourceProvenanceMode.LegacyUnknown,
                new RichKnownUInt64(false), new RichKnownInt64(false),
                new RichKnownInt64(false), new RichKnownDouble(false),
                new RichKnownDouble(false), knownClockId, knownClockEpoch,
                knownReceipt, knownSolve, new RichKnownInt64(false),
                new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false),
                new RichKnownUInt64(false), new RichKnownUInt64(false), 0, 15,
                status == RichSnapshotStatus.Failed ? 1u : 0u);
            if (status != RichSnapshotStatus.Complete)
                return new RichRealizationSnapshot(header,
                    new RichRealizationChannel[0]);

            var matrix = new RichMatrix4x4(new double[]
            {
                1, 0, 0, 0, 0, 1, 0, 0,
                0, 0, 1, 0, 0, 0, 0, 1
            });
            var contributor = new RichRealizationContributor(
                new RichKnownInt64(true, 9), new RichKnownInt64(false),
                new RichKnownInt64(false), new RichKnownInt64(false),
                new RichKnownUInt64(false), 0, 1, false,
                new RichPosition(0, 0, 0), RichGroundingMode.ExplicitSubRegion,
                RichContributorDisposition.Included, "", false, false, "", false,
                matrix);
            var channel = new RichRealizationChannel("root",
                RichRelationFamily.Spacing, RichSolverStatus.Converged,
                RichGeometry.NotApplicable, RichGoalKind.RootPosition, true,
                new RichKnownInt64(true, 9), 0, new RichPosition(1, 2, 0),
                false, 0, 1, false, 0, 0, false, 0,
                new[] { contributor });
            return new RichRealizationSnapshot(header, new[] { channel });
        }

        [Test]
        public void AdmissionIsExplicitBoundedAndRetirementMustDrain()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 3, 4, 5, 1, Mapping(), out error), error);
            // A different actor also consumes the global active-or-undrained
            // slot budget; the bound is not merely one token per actor.
            Assert.IsFalse(mailbox.TryAdmit(1, 99, 4, 4, 5, 1, Mapping(), out error));
            Assert.IsFalse(mailbox.TryAdmit(1, 2, 6, 4, 5, 1, Mapping(), out error));
            Assert.IsTrue(mailbox.Retire(3, 10.01, out error), error);
            Assert.IsFalse(mailbox.TryAdmit(1, 2, 6, 4, 5, 1, Mapping(), out error));

            var drained = mailbox.Drain(10.02);
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual(RichMailboxReason.LocalRetirement, drained[0].Reason);
            Assert.IsNull(drained[0].Snapshot);
            Assert.AreEqual(1, drained[0].SpaceId);
            Assert.AreEqual(2, drained[0].ActorId);
            Assert.AreEqual((ulong)3, drained[0].ProducerLifetime);
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 6, 4, 5, 1, Mapping(), out error), error);
        }

        [Test]
        public void DuplicateSlowFirstPollDoesNotRefreshOriginalIngressOrAge()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 7, 4, 5, .1, Mapping(), out error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(7, 1, RichSnapshotStatus.Complete),
                10.01, out error), error);

            // A duplicate poll/callback arrives much later but is an exact
            // revision duplicate; it cannot relabel the old native receipt as
            // recent or update the adapter ingress diagnostic.
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(7, 1, RichSnapshotStatus.Complete),
                10.09, out error), error);
            var drained = mailbox.Drain(10.11);
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual(RichMailboxReason.NativeAgeExpired, drained[0].Reason);
            Assert.IsNull(drained[0].Snapshot);
            Assert.AreEqual(10.01, drained[0].AdapterIngressSeconds);
            Assert.IsTrue(drained[0].HasLocalReceiptInterval);
        }

        [Test]
        public void NewerRevisionCoalescesUsingItsOwnReceiptAndIngress()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 8, 4, 5, 5, Mapping(), out error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(8, 1, RichSnapshotStatus.Complete),
                10.01, out error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(8, 2, RichSnapshotStatus.Complete,
                NativeReferenceNanoseconds + 50), 10.02, out error), error);

            var drained = mailbox.Drain(10.03);
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual((ulong)2, drained[0].Snapshot.Header.Revision);
            Assert.AreEqual(10.02, drained[0].AdapterIngressSeconds);
            Assert.AreEqual(NativeReferenceNanoseconds + 50,
                drained[0].Snapshot.Header.NativeReceiptNanoseconds.Value);
        }

        [Test]
        public void UnknownMismatchedAndExpiredClockMappingsFailClosed()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 9, 4, 5, 1, Mapping(), out error), error);

            Assert.IsFalse(mailbox.TryEnqueue(Snapshot(9, 1, RichSnapshotStatus.Complete,
                clockKnown: false, receiptKnown: false), 10.01, out error));
            var unknown = mailbox.Drain(10.01);
            Assert.AreEqual(RichMailboxReason.ClockBridgeRejected, unknown[0].Reason);
            Assert.IsNull(unknown[0].Snapshot);

            Assert.IsFalse(mailbox.TryEnqueue(Snapshot(9, 2, RichSnapshotStatus.Complete,
                clockId: 99), 10.02, out error));
            var mismatch = mailbox.Drain(10.02);
            Assert.AreEqual(RichMailboxReason.ClockBridgeRejected, mismatch[0].Reason);

            var expiring = new RichRealizationMailbox(1);
            Assert.IsTrue(expiring.TryAdmit(1, 2, 10, 4, 5, 1, Mapping(.02), out error), error);
            Assert.IsFalse(expiring.TryEnqueue(Snapshot(10, 1, RichSnapshotStatus.Complete),
                10.03, out error));
            var expired = expiring.Drain(10.03);
            Assert.AreEqual(RichMailboxReason.ClockBridgeRejected, expired[0].Reason);
        }

        [Test]
        public void AgeExpiryOccursAtDrainRatherThanInventingFreshness()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 11, 4, 5, .1, Mapping(), out error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(11, 1, RichSnapshotStatus.Complete),
                10.01, out error), error);

            var drained = mailbox.Drain(10.11);
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual(RichMailboxReason.NativeAgeExpired, drained[0].Reason);
            Assert.IsNull(drained[0].Snapshot);
            Assert.AreEqual(10.01, drained[0].AdapterIngressSeconds);
        }

        [Test]
        public void ClocklessNativeEmptyAndFailedAreAuthoritativeReleases()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 12, 4, 5, 1, Mapping(), out error), error);

            // Native releases can retain the process clock identity while the
            // native ingress/solve receipt is unavailable (zero/unknown).
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(12, 1, RichSnapshotStatus.Empty,
                receiptKnown: false), 10.01, out error), error);
            var empty = mailbox.Drain(10.01);
            Assert.AreEqual(RichMailboxReason.NativeRelease, empty[0].Reason);
            Assert.AreEqual(RichSnapshotStatus.Empty, empty[0].Snapshot.Header.SnapshotStatus);
            Assert.IsFalse(empty[0].HasLocalReceiptInterval);

            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(12, 2, RichSnapshotStatus.Failed,
                receiptKnown: false), 10.02, out error), error);
            var failed = mailbox.Drain(10.02);
            Assert.AreEqual(RichMailboxReason.NativeRelease, failed[0].Reason);
            Assert.AreEqual(RichSnapshotStatus.Failed, failed[0].Snapshot.Header.SnapshotStatus);
            Assert.AreEqual((uint)1, failed[0].Snapshot.Header.FailureCode);
        }

        [Test]
        public void RetirementIsABarrierAndStaleLifetimeCannotClearReplacement()
        {
            var mailbox = new RichRealizationMailbox(2);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 13, 4, 5, 1, Mapping(), out error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(13, 1, RichSnapshotStatus.Complete),
                10.01, out error), error);
            Assert.IsTrue(mailbox.Retire(13, 10.02, out error), error);
            Assert.IsFalse(mailbox.TryAdmit(1, 2, 14, 4, 5, 1, Mapping(), out error));

            var retired = mailbox.Drain(10.03);
            Assert.AreEqual(1, retired.Count);
            Assert.AreEqual(RichMailboxReason.LocalRetirement, retired[0].Reason);
            Assert.IsNull(retired[0].Snapshot);
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 14, 4, 5, 1, Mapping(), out error), error);

            Assert.IsFalse(mailbox.TryEnqueue(Snapshot(13, 2, RichSnapshotStatus.Complete),
                10.04, out error));
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(14, 1, RichSnapshotStatus.Complete),
                10.04, out error), error);
            var replacement = mailbox.Drain(10.05);
            Assert.AreEqual(1, replacement.Count);
            Assert.AreEqual((ulong)14, replacement[0].ProducerLifetime);
            Assert.AreEqual(RichMailboxReason.NativeSnapshot, replacement[0].Reason);
        }

        [Test]
        public void RejectPreservesIdentityAndInvalidDrainIsAtomic()
        {
            var mailbox = new RichRealizationMailbox(2);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 15, 4, 5, 1, Mapping(), out error), error);
            Assert.IsFalse(mailbox.Reject(15, RichMailboxReason.MalformedSnapshot,
                "bad native payload", 12, out error));
            Assert.Throws<ArgumentOutOfRangeException>(() => mailbox.Drain(11));

            var retried = mailbox.Drain(12);
            Assert.AreEqual(1, retried.Count);
            Assert.AreEqual(RichMailboxReason.MalformedSnapshot, retried[0].Reason);
            Assert.AreEqual(1, retried[0].SpaceId);
            Assert.AreEqual(2, retried[0].ActorId);
            Assert.AreEqual((ulong)15, retried[0].ProducerLifetime);
            Assert.IsNull(retried[0].Snapshot);
            Assert.IsFalse(mailbox.Reject(15, (RichMailboxReason)999, "bad", 13, out error));
        }

        [Test]
        public void CurrentTimeDrainKeepsConcurrentIngressInTheNextBatch()
        {
            var mailbox = new RichRealizationMailbox(1);
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 17, 4, 5, 5, Mapping(), out string error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(17, 1, RichSnapshotStatus.Complete),
                10.01, out error), error);
            using (var attempted = new ManualResetEventSlim())
            {
                bool retired = false;
                var worker = new Thread(() =>
                {
                    attempted.Set();
                    retired = mailbox.Retire(17, 10.03, out _);
                });
                var batch = mailbox.DrainAtCurrentTime(() =>
                {
                    worker.Start();
                    Assert.IsTrue(attempted.Wait(2000));
                    return 10.02;
                });
                Assert.IsTrue(worker.Join(2000));
                Assert.IsTrue(retired);
                Assert.AreEqual(1, batch.Count);
                Assert.AreEqual(RichMailboxReason.NativeSnapshot, batch[0].Reason);
                Assert.AreEqual(10.02, batch[0].DeliverySeconds);
                var terminal = mailbox.DrainAtCurrentTime(() => 10.04);
                Assert.AreEqual(1, terminal.Count);
                Assert.AreEqual(RichMailboxReason.LocalRetirement, terminal[0].Reason);
                Assert.AreEqual(10.03, terminal[0].AdapterIngressSeconds);
            }
        }

        [Test]
        public void AgeWithdrawalCannotOverwriteANewerPendingRevision()
        {
            var mailbox = new RichRealizationMailbox(1);
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 18, 4, 5, 5, Mapping(), out string error), error);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(18, 1, RichSnapshotStatus.Complete),
                10.01, out error), error);
            mailbox.Drain(10.02);
            Assert.IsTrue(mailbox.TryEnqueue(Snapshot(18, 2, RichSnapshotStatus.Complete,
                NativeReferenceNanoseconds + 100000000UL), 10.11, out error), error);
            Assert.IsFalse(mailbox.TryExpireIfIdle(18, 10.12));
            var newer = mailbox.Drain(10.13);
            Assert.AreEqual(2UL, newer[0].Snapshot.Header.Revision);
            Assert.IsTrue(mailbox.TryExpireIfIdle(18, 10.14));
            var expired = mailbox.Drain(10.15);
            Assert.AreEqual(RichMailboxReason.NativeAgeExpired, expired[0].Reason);
        }

        [Test]
        public void BackgroundEnqueueCanBeDrainedByTheCallerThread()
        {
            var mailbox = new RichRealizationMailbox(1);
            string error;
            Assert.IsTrue(mailbox.TryAdmit(1, 2, 16, 4, 5, 1, Mapping(), out error), error);
            bool accepted = false;
            string workerError = null;
            var worker = new Thread(() =>
            {
                accepted = mailbox.TryEnqueue(Snapshot(16, 1,
                    RichSnapshotStatus.Complete), 10.01, out workerError);
            });
            worker.Start();
            Assert.IsTrue(worker.Join(1000), "Background enqueue did not finish.");
            Assert.IsTrue(accepted, workerError);

            var drained = mailbox.Drain(10.02);
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual(RichMailboxReason.NativeSnapshot, drained[0].Reason);
            Assert.AreEqual((ulong)16, drained[0].ProducerLifetime);
        }
    }
}
