// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    /// <summary>Mode by writer contract for Chess and Whiteboard (T3.5, parity rows O1 and O2).</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FreeformContentWriterContractTests
    {
        [Test]
        public void FusionModeSelectsTheFusionMotifsAndForbidsTheLocalBackend()
        {
            Assert.That(FreeformContentWriterPolicy.WriterFor(false), Is.EqualTo(FreeformContentWriter.FusionMotifs));
            Assert.That(FreeformContentWriterPolicy.AllowsFusionMotifs(false), Is.True);
            Assert.That(FreeformContentWriterPolicy.AllowsLocalBackendToBind(false), Is.False);
        }

        [Test]
        public void FoundationLocalModeSelectsTheLocalBackendAndSuppressesTheMotifs()
        {
            Assert.That(FreeformContentWriterPolicy.WriterFor(true),
                Is.EqualTo(FreeformContentWriter.FoundationLocalBackend));
            Assert.That(FreeformContentWriterPolicy.AllowsFusionMotifs(true), Is.False);
            Assert.That(FreeformContentWriterPolicy.AllowsLocalBackendToBind(true), Is.True);
        }

        [Test]
        public void ModeTimesObservedStateAllowsExactlyOneWriter()
        {
            foreach (bool local in new[] { false, true })
            foreach (bool motifs in new[] { false, true })
            foreach (bool backend in new[] { false, true })
            {
                bool expected = (motifs ^ backend) && (local ? backend : motifs);
                Assert.That(FreeformContentWriterPolicy.HasExactlyTheModeWriter(local, motifs, backend),
                    Is.EqualTo(expected), $"local={local} motifs={motifs} backend={backend}");
            }
            Assert.That(FreeformContentWriterPolicy.ActiveWriterCount(true, true), Is.EqualTo(2));
            Assert.That(FreeformContentWriterPolicy.ActiveWriterCount(false, false), Is.EqualTo(0));
        }
    }
}
#endif
