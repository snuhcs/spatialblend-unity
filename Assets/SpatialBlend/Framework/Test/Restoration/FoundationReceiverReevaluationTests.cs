// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    public sealed partial class FoundationReceiverBodyNativeTests
    {
        [Test]
        public void ReevaluationFeatureRejectsIncompatibleDescriptorAndEveryOffset()
        {
            Assert.That(SpatialBlendAPI.TryGetReceiverReevaluationFeature(out var feature, out string error), Is.True, error);
            Assert.That(feature.guarantees, Is.EqualTo(7));
            Func<uint, uint> count = _ => 6;
            Func<uint, uint, uint> offset = (_, index) => 4 * index;
            Assert.That(SpatialBlendAPI.ValidateReceiverReevaluationFeature(feature, count, offset, out error), Is.True, error);
            for (int field = 0; field < 6; ++field)
            {
                var changed = feature;
                if (field == 0) changed.descriptor_size++;
                if (field == 1) changed.feature_version++;
                if (field == 2) changed.required_receiver_input_version++;
                if (field == 3) changed.required_rich_version++;
                if (field == 4) changed.guarantees ^= 1;
                if (field == 5) changed.reserved++;
                Assert.That(SpatialBlendAPI.ValidateReceiverReevaluationFeature(changed, count, offset, out _), Is.False);
                int badIndex = field;
                Assert.That(SpatialBlendAPI.ValidateReceiverReevaluationFeature(feature, count,
                    (_, index) => index == badIndex ? uint.MaxValue : 4 * index, out _), Is.False);
            }
            Assert.That(SpatialBlendAPI.ValidateReceiverReevaluationFeature(feature, _ => 5, offset, out _), Is.False);
        }

        [Test]
        public void ReevaluationJoinsLatestBodyWithoutNewSourceOrAutomaticActuation()
        {
            var first = Frame();
            Assert.That(_binding.TrySubmit(first, out string error), Is.True, error);
            var original = Solve();
            Assert.That(_binding.TryPrepareRealization(original, 1000000000L, out var prepared, out error), Is.True, error);
            int count = _delivered.Count, legacy = 0;
            long sourceSequence = _sourceSequence;
            Vector3 position = _receiver.transform.position;
            Quaternion rotation = _receiver.transform.rotation;
            _receiver.RootTargetAvailable += _ => ++legacy;
            _receiver.RootTargetsCleared += () => ++legacy;
            _receiver.SpatialControlCueFrameAvailable += _ => ++legacy;
            var second = Frame(2, 1, 0);
            Assert.That(_binding.TrySubmit(second, out error), Is.True, error);
            Assert.That(_binding.TryReevaluateRichRealization(original.Header.ProducerLifetime, out var status, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverReevaluationStatus.Evaluated));
            Assert.That(_delivered.Count, Is.EqualTo(count), "Native reevaluation must not drain owner delivery.");
            _world.DrainRealizations(0);
            var next = _delivered.Skip(count).Single(e => e.Reason == RichMailboxReason.NativeSnapshot).Snapshot;
            Assert.That(next.Header.ProducerLifetime, Is.EqualTo(original.Header.ProducerLifetime));
            Assert.That(next.Header.Revision, Is.GreaterThan(original.Header.Revision));
            Assert.That(next.Header.SourceProvenanceMode, Is.EqualTo(original.Header.SourceProvenanceMode));
            Assert.That(next.Header.SourceEpoch, Is.EqualTo(original.Header.SourceEpoch));
            Assert.That(next.Header.SourceSequence, Is.EqualTo(original.Header.SourceSequence));
            Assert.That(next.Header.SourceObservationSequence, Is.EqualTo(original.Header.SourceObservationSequence));
            Assert.That(next.Header.SourceTimeSeconds, Is.EqualTo(original.Header.SourceTimeSeconds));
            Assert.That(next.Header.SourceClockSeconds, Is.EqualTo(original.Header.SourceClockSeconds));
            Assert.That(next.Header.SourceActorSpace, Is.EqualTo(original.Header.SourceActorSpace));
            Assert.That(next.Header.SourceActorElement, Is.EqualTo(original.Header.SourceActorElement));
            Assert.That(next.Header.RelaySourceSpace, Is.EqualTo(original.Header.RelaySourceSpace));
            Assert.That(next.Header.NativeReceiptNanoseconds, Is.EqualTo(original.Header.NativeReceiptNanoseconds));
            Assert.That(next.Header.NativeSolveNanoseconds.Value, Is.GreaterThanOrEqualTo(original.Header.NativeSolveNanoseconds.Value));
            Assert.That(_binding.TryGetBodyForRealization(next, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(second));
            Assert.That(_binding.TryPrepareRealization(next, 1000000000L, out var current, out error), Is.True, error);
            Assert.That(current.ValidUntilNativeNanoseconds, Is.EqualTo(prepared.ValidUntilNativeNanoseconds),
                "A solve revision must not renew the original source-age/body deadline.");
            Assert.That(prepared.TryRevalidate(out _, out _), Is.False);
            Assert.That(_sourceSequence, Is.EqualTo(sourceSequence));
            Assert.That(_receiver.transform.position, Is.EqualTo(position));
            Assert.That(_receiver.transform.rotation, Is.EqualTo(rotation));
            Assert.That(legacy, Is.Zero);
            count = _delivered.Count;
            Assert.That(_binding.TryReevaluateRichRealization(original.Header.ProducerLifetime, out status, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverReevaluationStatus.NoNewInput));
            _world.DrainRealizations(0);
            Assert.That(_delivered.Count, Is.EqualTo(count));
        }

        [Test]
        public void ReevaluationRejectsForeignRetiredAndWrongThreadOwners()
        {
            var foreign = Actor("reevaluation-foreign", false, Support("reevaluation-foreign", "seat"));
            int foreignDeliveries = 0;
            foreign.RichRealizationAvailable += _ => ++foreignDeliveries;
            Assert.That(foreign.TryEnableRichRealizationOwned(new RichRealizationSubscriptionOptions
                { MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1 }, out ulong foreignLifetime, out string error), Is.True, error);
            Assert.That(_binding.TrySubmit(Frame(), out error), Is.True, error);
            var original = Solve();
            // Normal source delivery also reaches the foreign receiver's shared
            // actor ID (and may publish a release for its unmapped support).
            // Only reevaluation must remain isolated from that receiver.
            int foreignBaseline = foreignDeliveries;
            var second = Frame(2, 1, 0);
            Assert.That(_binding.TrySubmit(second, out error), Is.True, error);
            int count = _delivered.Count;
            long sourceSequence = _sourceSequence;
            Assert.That(_binding.TryReevaluateRichRealization(ulong.MaxValue, out _, out _), Is.False);
            Assert.That(_binding.TryReevaluateRichRealization(foreignLifetime, out _, out _), Is.False);
            bool accepted = true;
            Exception failure = null;
            var worker = new Thread(() => {
                try { accepted = _binding.TryReevaluateRichRealization(original.Header.ProducerLifetime, out _, out _); }
                catch (Exception e) { failure = e; }
            }) { IsBackground = true };
            worker.Start(); Assert.That(worker.Join(2000), Is.True, "Wrong-thread reevaluation must not deadlock.");
            Assert.That(failure, Is.Null); Assert.That(accepted, Is.False);
            _world.DrainRealizations(0);
            Assert.That(_delivered.Count, Is.EqualTo(count));
            Assert.That(foreignDeliveries, Is.EqualTo(foreignBaseline));
            Assert.That(_binding.TryReevaluateRichRealization(original.Header.ProducerLifetime, out var status, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverReevaluationStatus.Evaluated));
            _world.DrainRealizations(0);
            var next = _delivered.Last(e => e.Reason == RichMailboxReason.NativeSnapshot).Snapshot;
            Assert.That(_binding.TryGetBodyForRealization(next, out var body, out error), Is.True, error);
            Assert.That(body, Is.SameAs(second));
            _receiver.DisableRichRealization(); _world.DrainRealizations(0);
            count = _delivered.Count;
            Assert.That(_binding.TryReevaluateRichRealization(original.Header.ProducerLifetime, out _, out _), Is.False);
            _world.DrainRealizations(0);
            Assert.That(_delivered.Count, Is.EqualTo(count));
            Assert.That(_sourceSequence, Is.EqualTo(sourceSequence));
            Assert.That(foreignDeliveries, Is.EqualTo(foreignBaseline));
            foreign.DisableRichRealization(); _world.DrainRealizations(0);
        }

        [Test]
        public void ReevaluationWaitsForAdmittedSourceAndNeverRevivesClearedBody()
        {
            _receiver.DisableRichRealization(); _world.DrainRealizations(0);
            Assert.That(_receiver.TryEnableRichRealizationOwned(new RichRealizationSubscriptionOptions
                { MaximumCalibrationIntervalSeconds = .1, MaximumNativeAgeSeconds = 1 }, out ulong lifetime, out string error), Is.True, error);
            Assert.That(_binding.TryReevaluateRichRealization(lifetime, out _, out _), Is.False);
            Assert.That(_binding.TrySubmit(Frame(), out error), Is.True, error);
            Assert.That(_binding.TryReevaluateRichRealization(lifetime, out var status, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverReevaluationStatus.Unavailable));
            Solve();
            Assert.That(_binding.TryReevaluateRichRealization(lifetime, out status, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverReevaluationStatus.NoNewInput));
            Assert.That(_binding.TrySubmit(Frame(2, 1, 0, root: false), out error), Is.True, error);
            Assert.That(_binding.TryReevaluateRichRealization(lifetime, out status, out error), Is.True, error);
            Assert.That(status, Is.EqualTo(ReceiverReevaluationStatus.Unavailable));
            Assert.That(_binding.TryClear(out error), Is.True, error);
            Assert.That(_binding.TryReevaluateRichRealization(lifetime, out _, out _), Is.False);
        }
    }
}
#endif
