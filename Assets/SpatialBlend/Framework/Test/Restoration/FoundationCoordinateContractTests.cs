// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FoundationCoordinateContractTests
    {
        [Test]
        public void FoundationAssemblyHasNoPhotonMetaOrLegacyAssemblyDependency()
        {
            var assembly = typeof(AnchorPoseFrame).Assembly;
            Assert.That(assembly.GetName().Name, Is.EqualTo("SpatialBlend.Foundation"));
            foreach (var dependency in assembly.GetReferencedAssemblies())
            {
                string name = dependency.Name;
                Assert.That(name, Is.Not.EqualTo("SpatialBlend"));
                Assert.That(name, Does.Not.StartWith("Fusion"));
                Assert.That(name, Does.Not.StartWith("Photon"));
                Assert.That(name, Does.Not.StartWith("Oculus"));
                Assert.That(name, Does.Not.StartWith("Meta"));
            }
        }

        [Test]
        public void GeometryIdentityStaysIdentity()
        {
            AssertMatrix(CoordinateConverter.UnityToSpatialBlendMatrix(Matrix4x4.identity),
                Matrix4x4.identity);
        }

        [Test]
        public void HierarchyAndGeometryTransportCommuteWithBasisChange()
        {
            Matrix4x4 parent = Matrix4x4.TRS(new Vector3(3, -2, 5),
                Quaternion.Euler(19, 71, -23), new Vector3(2, 3, 0.7f));
            Matrix4x4 child = Matrix4x4.TRS(new Vector3(-1, 0.6f, 2),
                Quaternion.Euler(-31, 17, 41), new Vector3(-0.8f, 1.2f, 2));
            // Nonuniform scaled rotations produce shear; decomposing TRS loses it.
            Matrix4x4 world = parent * child;
            Vector3 local = new Vector3(0.17f, -0.43f, 0.89f);
            Vector3 expected = Swap(world.MultiplyPoint3x4(local));
            Matrix4x4 nativeWorld = CoordinateConverter.UnityToSpatialBlendMatrix(parent) *
                CoordinateConverter.UnityToSpatialBlendMatrix(child);
            AssertVector(nativeWorld.MultiplyPoint3x4(Swap(local)), expected);
            AssertMatrix(nativeWorld, CoordinateConverter.UnityToSpatialBlendMatrix(world));
        }

        [TestCase(0f)]
        [TestCase(90f)]
        [TestCase(-37f)]
        public void AnchorForwardMatchesHeadingButDoesNotRotateGeometry(float yaw)
        {
            Matrix4x4 pose = Matrix4x4.TRS(new Vector3(2, 1.4f, -3),
                Quaternion.Euler(0, yaw, 0), Vector3.one);
            Matrix4x4 anchor = CoordinateConverter.UnityToSpatialBlendAnchorMatrix(pose);
            AssertVector(anchor.MultiplyVector(Vector3.right),
                Swap(pose.MultiplyVector(Vector3.forward)));
            AssertVector(anchor.MultiplyPoint3x4(Vector3.zero), new Vector3(2, -3, 1.4f));
            Matrix4x4 geometry = CoordinateConverter.UnityToSpatialBlendMatrix(pose);
            AssertVector(geometry.MultiplyVector(Vector3.right),
                Swap(pose.MultiplyVector(Vector3.right)));
        }

        [Test]
        public void AnchorLocalBasisOffsetIsAppliedOnceUnderRotatedParent()
        {
            Matrix4x4 parent = Matrix4x4.TRS(new Vector3(5, 1, -2),
                Quaternion.Euler(17, 62, 11), Vector3.one);
            Matrix4x4 local = Matrix4x4.TRS(new Vector3(0.4f, 0.8f, 0.1f),
                Quaternion.Euler(-9, 28, 33), Vector3.one);
            Matrix4x4 native = CoordinateConverter.UnityToSpatialBlendMatrix(parent) *
                CoordinateConverter.UnityToSpatialBlendAnchorMatrix(local);
            AssertVector(native.MultiplyVector(Vector3.right),
                Swap((parent * local).MultiplyVector(Vector3.forward)));
            AssertMatrix(native, CoordinateConverter.UnityToSpatialBlendAnchorMatrix(parent * local));
        }

        private static Vector3 Swap(Vector3 value) => new Vector3(value.x, value.z, value.y);

        private static void AssertVector(Vector3 actual, Vector3 expected) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-5f));

        private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected)
        {
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                    Assert.That(actual[row, column], Is.EqualTo(expected[row, column]).Within(1e-5f));
        }
    }
}
#endif
