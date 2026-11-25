// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend.Internal;

namespace SpatialBlend.Tests
{
    // Copied only into the explicitly provisioned native verification project.
    // It exercises the real P/Invoke boundary; no mock/native substitute is a
    // passing result.
    public class NativeSupportIntegrationTests
    {
        [Test]
        [Category("NativeIntegration")]
        public void SupportCorrespondenceLayoutMatchesFourInt64NativeFields()
        {
            Assert.That(Marshal.SizeOf<SbSupportCorrespondence>(), Is.EqualTo(32));
            Assert.That(Marshal.OffsetOf<SbSupportCorrespondence>("SourceSpace").ToInt64(), Is.EqualTo(0));
            Assert.That(Marshal.OffsetOf<SbSupportCorrespondence>("SourceElement").ToInt64(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf<SbSupportCorrespondence>("TargetSpace").ToInt64(), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf<SbSupportCorrespondence>("TargetElement").ToInt64(), Is.EqualTo(24));
        }

        [Test]
        [Category("NativeIntegration")]
        public void NativeSupportBatchPreservesUnlikeEndpointsAndStaleLeaseCannotRemoveReplacement()
        {
            SpatialBlendAPI.Initialize();
            long session = SpatialBlendAPI.CreateSession();
            Assert.That(session, Is.GreaterThan(0));
            try
            {
                long source = SpatialBlendAPI.AddSpace(session);
                long target = SpatialBlendAPI.AddSpace(session);
                long chairSeat = SpatialBlendAPI.AddElement(source);
                long chairBack = SpatialBlendAPI.AddElement(source);
                long sofaSupport = SpatialBlendAPI.AddElement(target);
                long otherSupport = SpatialBlendAPI.AddElement(target);

                var seatToSofa = new List<SbSupportCorrespondence>
                {
                    new SbSupportCorrespondence(source, chairSeat, target, sofaSupport)
                };
                long firstLease = SpatialBlendAPI.RegisterSupportCorrespondences(
                    session, seatToSofa);
                Assert.That(firstLease, Is.GreaterThan(0));

                long conflict = SpatialBlendAPI.RegisterSupportCorrespondences(session,
                    new[] { new SbSupportCorrespondence(
                        source, chairSeat, target, otherSupport) });
                Assert.That(conflict, Is.EqualTo(0),
                    "a different target in the same source/target-space slot must fail closed");

                long manyToOne = SpatialBlendAPI.RegisterSupportCorrespondences(session,
                    new[] { new SbSupportCorrespondence(
                        source, chairBack, target, sofaSupport) });
                Assert.That(manyToOne, Is.GreaterThan(0),
                    "many source supports may correspond to one target support");

                SpatialBlendAPI.DisposeSupportCorrespondences(firstLease);
                long replacement = SpatialBlendAPI.RegisterSupportCorrespondences(
                    session, seatToSofa);
                Assert.That(replacement, Is.GreaterThan(0));

                // The old managed lease is stale. Its repeat disposal cannot
                // delete the replacement native correspondence.
                SpatialBlendAPI.DisposeSupportCorrespondences(firstLease);
                long replacementConflict = SpatialBlendAPI.RegisterSupportCorrespondences(session,
                    new[] { new SbSupportCorrespondence(
                        source, chairSeat, target, otherSupport) });
                Assert.That(replacementConflict, Is.EqualTo(0));

                SpatialBlendAPI.DisposeSupportCorrespondences(manyToOne);
                SpatialBlendAPI.DisposeSupportCorrespondences(replacement);
            }
            finally
            {
                SpatialBlendAPI.DestroySession(session);
            }
        }
    }
}
#endif
