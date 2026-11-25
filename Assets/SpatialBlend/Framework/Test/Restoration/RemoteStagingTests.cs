// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// As v1, an arriving shared object waits
    /// at a random floor point of the room until its state arrives. MRUK's point can be mid-air, so it is dropped to
    /// the floor plus 0.05 m; with no room the object waits at the origin.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RemoteStagingTests
    {
        [Test]
        public void TheStagingPointIsDroppedToTheFloorPlusAFewCentimetres()
        {
            Vector3 staged = SbNativeEntity.StagingPosition(new Vector3(2.5f, 1.7f, -3f), floorY: 0.12f);
            Assert.That(staged.x, Is.EqualTo(2.5f));
            Assert.That(staged.z, Is.EqualTo(-3f));
            Assert.That(staged.y, Is.EqualTo(0.17f).Within(1e-6f), "floor 0.12 + 0.05, not the mid-air 1.7");
        }

        [Test]
        public void WithoutARoomPointTheObjectWaitsAtTheOrigin()
        {
            Assert.That(SbNativeEntity.StagingPosition(null, floorY: 0.4f), Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void TheV1StagingConstantsAreKept()
        {
            Assert.That(SbNativeEntity.RemoteStagingMinDistanceToSurface, Is.EqualTo(0.3f));
            Assert.That(SbNativeEntity.RemoteStagingFloorLift, Is.EqualTo(0.05f));
        }

        [Test]
        public void WithNoMrukInstanceTheComputedStagingPointIsTheOrigin()
        {
            Assert.That(Meta.XR.MRUtilityKit.MRUK.Instance == null, Is.True, "EditMode has no MRUK");
            MethodInfo compute = typeof(SbNativeEntity).GetMethod("ComputeRandomStagingPosition",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(compute, Is.Not.Null);
            Assert.That((Vector3)compute.Invoke(null, null), Is.EqualTo(Vector3.zero));
        }
    }
}
#endif
