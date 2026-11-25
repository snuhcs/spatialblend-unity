// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using NUnit.Framework;

namespace SpatialBlend.Tests
{
    public sealed class FoundationNativeClockMappingTests
    {
        private static SpatialBlendNativeClockMapping Mapping(double rateErrorPpm = 0,
                                                            double width = 0.02,
                                                            double lifetime = 10)
        {
            Assert.IsTrue(SpatialBlendNativeClockMapping.TryCreate(7, 11, 2000000000UL,
                100, 100.01, width, rateErrorPpm, lifetime, out var mapping, out var error), error);
            return mapping;
        }

        [Test]
        public void LateFirstPollDoesNotRefreshNativeReceipt()
        {
            var mapping = Mapping();
            Assert.IsTrue(mapping.TryMap(7, 11, 1000000000UL, 100.02, out var receipt, out var error), error);
            Assert.That(receipt.EarliestSeconds, Is.EqualTo(99).Within(1e-8));
            Assert.That(receipt.LatestSeconds, Is.EqualTo(99.01).Within(1e-8));
            Assert.IsFalse(receipt.IsFreshAt(100.02, 0.5));
        }

        [Test]
        public void DuplicatePollPreservesTheOriginalAgeBound()
        {
            var mapping = Mapping();
            Assert.IsTrue(mapping.TryMap(7, 11, 2000000000UL, 100.02, out var first, out _));
            Assert.IsTrue(mapping.TryMap(7, 11, 2000000000UL, 100.2, out var second, out _));
            Assert.AreEqual(first.EarliestSeconds, second.EarliestSeconds);
            Assert.AreEqual(first.LatestSeconds, second.LatestSeconds);
            Assert.IsTrue(first.IsFreshAt(100.02, 0.1));
            Assert.IsFalse(second.IsFreshAt(100.2, 0.1));
        }

        [Test]
        public void ExactExpiryAndInvalidClockReject()
        {
            var mapping = Mapping();
            Assert.IsFalse(mapping.TryMap(7, 11, 2000000000UL, mapping.ExpiresAtSeconds, out _, out _));
            Assert.IsFalse(mapping.TryMap(8, 11, 2000000000UL, 100.02, out _, out _));
            Assert.IsFalse(mapping.TryMap(7, 11, 2000000000UL, 99, out _, out _));
            Assert.IsFalse(mapping.TryMap(7, 11, 9000000000UL, 100.02, out _, out _));
        }

        [Test]
        public void CalibrationAndFutureDriftBothRespectUncertaintyBound()
        {
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 11, 0, 100, 100.1,
                0.02, 0, 10, out _, out _));
            var mapping = Mapping(1000, 0.02, 20);
            Assert.IsTrue(mapping.TryMap(7, 11, 2000000000UL, 100.02, out _, out _));
            Assert.IsFalse(mapping.TryMap(7, 11, 12000000000UL, 111, out _, out _));
        }

        [Test]
        public void BackwardNativeDeltaGetsSymmetricDriftBounds()
        {
            var mapping = Mapping(1000, 0.1);
            Assert.IsTrue(mapping.TryMap(7, 11, 1000000000UL, 100.02, out var receipt, out var error), error);
            Assert.That(receipt.EarliestSeconds, Is.EqualTo(98.999).Within(1e-8));
            Assert.That(receipt.LatestSeconds, Is.EqualTo(99.011).Within(1e-8));
        }

        [Test]
        public void LargeUnsignedClockCounterPreservesSmallIntegerDelta()
        {
            ulong reference = ulong.MaxValue - 1000000UL;
            Assert.IsTrue(SpatialBlendNativeClockMapping.TryCreate(7, 11, reference,
                10, 10, 0.001, 0, 1, out var mapping, out var error), error);
            Assert.IsTrue(mapping.TryMap(7, 11, reference - 100UL, 10.1, out var receipt, out error), error);
            Assert.That(receipt.EarliestSeconds, Is.EqualTo(10 - 100e-9 - 2e-9).Within(1e-12));
            Assert.That(receipt.LatestSeconds, Is.EqualTo(10 - 100e-9 + 2e-9).Within(1e-12));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(-1)]
        public void InvalidLocalCalibrationReadingRejects(double before)
        {
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 11, 0, before,
                100, 0.02, 0, 1, out _, out _));
        }

        [Test]
        public void InvalidBoundsAndUnrepresentableLifetimeReject()
        {
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(0, 11, 0, 0, 0, 1, 0, 1, out _, out _));
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 11, 0, 0, 0, 1, -1, 1, out _, out _));
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 11, 0, 0, 0, 1, 1000000, 1, out _, out _));
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 11, 0, 0, 0, 1, 0, 0, out _, out _));
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 11, 0, double.MaxValue,
                double.MaxValue, 1, 0, 1, out _, out _));
        }

        [Test]
        public void FreshnessRejectsExactDeadlineAndMalformedNowOrBudget()
        {
            Assert.IsFalse(default(SpatialBlendLocalTimeInterval).IsFreshAt(0.1, 1));
            var mapping = Mapping();
            Assert.IsTrue(mapping.TryMap(7, 11, 2000000000UL, 100.02, out var receipt, out _));
            Assert.IsFalse(receipt.IsFreshAt(receipt.EarliestSeconds + 0.25, 0.25));
            Assert.IsFalse(receipt.IsFreshAt(double.NaN, 1));
            Assert.IsFalse(receipt.IsFreshAt(100.02, double.PositiveInfinity));
            Assert.IsFalse(receipt.IsFreshAt(100.02, -1));
        }

        [Test]
        public void UnknownOrReusedClockIdentityRejects()
        {
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(0, 11, 0,
                0, 0, 1, 0, 1, out _, out _));
            Assert.IsFalse(SpatialBlendNativeClockMapping.TryCreate(7, 0, 0,
                0, 0, 1, 0, 1, out _, out _));

            var mapping = Mapping();
            Assert.AreEqual(11UL, mapping.ClockEpoch);
            Assert.IsFalse(mapping.TryMap(0, 11, 2000000000UL, 100.02, out _, out _));
            Assert.IsFalse(mapping.TryMap(7, 0, 2000000000UL, 100.02, out _, out _));
            Assert.IsFalse(mapping.TryMap(7, 12, 2000000000UL, 100.02, out _, out _));
        }
    }
}
