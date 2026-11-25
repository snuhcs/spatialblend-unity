// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;

namespace SpatialBlend.Tests
{
    /// <summary>The session binding sends each point's label as SegPoint.z (the scan server reads it).</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SessionLibraryLabelTests
    {
        [Test]
        public void EveryLabelGoesOutAsZ()
        {
            SpatialBlend.Internal.SbPoint3d[] native = SpatialBlend.Internal.SessionLibrary.ToNativePoints(new[]
            {
                new ScanPoint(1, 2), new ScanPoint(3, 4, ScanPointLabel.Negative),
                new ScanPoint(5, 6, ScanPointLabel.BoxTopLeft), new ScanPoint(7, 8, ScanPointLabel.BoxBottomRight),
            });
            Assert.That(native.Select(p => p.z), Is.EqualTo(new[] { 0.0, -1.0, 2.0, 3.0 }));
            Assert.That(native.Select(p => p.x), Is.EqualTo(new[] { 1.0, 3.0, 5.0, 7.0 }));
        }
    }
}
#endif
