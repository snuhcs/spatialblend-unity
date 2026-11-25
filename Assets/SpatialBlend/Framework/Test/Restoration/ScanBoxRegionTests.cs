// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The pure half of T5.2: what an annotated box declares, in the local frame of the object it scopes.
    /// No scene, no component, no native library.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanBoxRegionTests
    {
        private static ScanBoxRegion LocalBox(Vector3 center, Vector3 size)
        {
            Assert.That(ScanBoxRegion.TryFromLocalBox(center, size, out ScanBoxRegion region), Is.True);
            return region;
        }

        [Test]
        public void TheRegionIsTheFootprintExtentWithNoMarginOfItsOwn()
        {
            // The v1 volume added 0.15 m around the box. Here the extent is the definition.
            ScanBoxRegion region = LocalBox(new Vector3(0f, 0.3f, 0f), new Vector3(1f, 0.6f, 0.4f));
            Assert.That(region.Footprint.Min(point => point.x), Is.EqualTo(-0.5f).Within(1e-5f));
            Assert.That(region.Footprint.Max(point => point.x), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(region.Footprint.Min(point => point.y), Is.EqualTo(-0.2f).Within(1e-5f));
            Assert.That(region.Footprint.Max(point => point.y), Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(region.ExtentPolygon(), Is.EqualTo(region.FootprintPolygon()));
        }

        [Test]
        public void TheFootprintIsTheBoxBaseAndTheTopIsATaggedSurface()
        {
            ScanBoxRegion region = LocalBox(new Vector3(0.1f, 0.3f, -0.2f), new Vector3(1f, 0.6f, 0.4f));
            Assert.That(region.BaseY, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(region.TopY, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(region.HasTopSurface, Is.True);
            Vector3[] top = region.TopSurface();
            Assert.That(top, Has.Length.EqualTo(4));
            Assert.That(top.All(vertex => Mathf.Abs(vertex.y - 0.6f) < 1e-5f), Is.True);
            Assert.That(ScanBoxRegion.TopSurfaceTag, Is.EqualTo("Placeable"));
            Assert.That(UpNormalY(top), Is.GreaterThan(0f), "The core's normal for this vertex order points up.");
        }

        [Test]
        public void ABoxShallowerThanTheMinimumDeclaresNoTopSurface()
        {
            ScanBoxRegion flat = LocalBox(Vector3.zero, new Vector3(1f, ScanBoxRegion.MinTopHeightMetres * 0.5f, 1f));
            Assert.That(flat.HasTopSurface, Is.False);
            Assert.That(flat.Footprint.Count, Is.EqualTo(4), "The region and the footprint remain.");
        }

        [TestCase(0f)]
        [TestCase(90f)]
        [TestCase(180f)]
        [TestCase(270f)]
        public void TheDeclarationIsTheSameWhateverPoseThisCopyOfTheObjectHas(float yaw)
        {
            // The object is an identical shared asset. Each space gives its copy a different place and yaw.
            // The box is drawn once in a space, so it is a fixed local box under every one of those poses.
            Vector3 localCenter = new Vector3(0f, 0.3f, 0f);
            Vector3 localSize = new Vector3(1f, 0.6f, 0.4f);
            ScanBoxRegion expected = LocalBox(localCenter, localSize);
            Matrix4x4 objectToWorld = Matrix4x4.TRS(new Vector3(2f, 0.1f, -3f), Quaternion.Euler(0f, yaw, 0f), Vector3.one);

            // A world box is axis aligned. For a quarter turn it is the local box's own bounds.
            Vector3 worldCenter = objectToWorld.MultiplyPoint3x4(localCenter);
            Vector3 worldSize = Mathf.Approximately(Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad)), 1f)
                ? new Vector3(localSize.z, localSize.y, localSize.x)
                : localSize;
            Assert.That(ScanBoxRegion.TryFromWorldBox(worldCenter, worldSize, objectToWorld.inverse,
                out ScanBoxRegion actual), Is.True);

            Assert.That(actual.TopY - actual.BaseY, Is.EqualTo(expected.TopY - expected.BaseY).Within(1e-4f));
            Assert.That(actual.BaseY, Is.EqualTo(expected.BaseY).Within(1e-4f));
            AssertSameLoop(actual.FootprintPolygon(), expected.FootprintPolygon());
        }

        [Test]
        public void AMirroredObjectFrameStillWindsTheFootprintCounterClockwise()
        {
            Matrix4x4 mirrored = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            Assert.That(ScanBoxRegion.TryFromWorldBox(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 0.5f),
                mirrored, out ScanBoxRegion region), Is.True);
            Assert.That(UpNormalY(region.TopSurface()), Is.GreaterThan(0f));
        }

        [Test]
        public void AnObjectScaledAfterTheBoxWasDrawnCarriesTheBoxInItsOwnUnits()
        {
            // modelRoot.localScale is part of the object-to-world matrix, so the local footprint is in the
            // object's own units and two copies at different scales do not disagree after the world is mapped back.
            Matrix4x4 objectToWorld = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 2f);
            Assert.That(ScanBoxRegion.TryFromWorldBox(new Vector3(0f, 0.6f, 0f), new Vector3(1f, 1.2f, 0.8f),
                objectToWorld.inverse, out ScanBoxRegion region), Is.True);
            Assert.That(region.Footprint.Max(point => point.x), Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(region.Footprint.Max(point => point.y), Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(region.TopY, Is.EqualTo(0.6f).Within(1e-5f));
        }

        [Test]
        public void ADegenerateOrNonFiniteBoxDeclaresNothing()
        {
            Assert.That(ScanBoxRegion.TryFromLocalBox(Vector3.zero, new Vector3(0f, 1f, 1f), out _), Is.False);
            Assert.That(ScanBoxRegion.TryFromLocalBox(Vector3.zero, new Vector3(1f, 1f, 0f), out _), Is.False);
            Assert.That(ScanBoxRegion.TryFromLocalBox(new Vector3(float.NaN, 0f, 0f), Vector3.one, out _), Is.False);
            Assert.That(ScanBoxRegion.TryFromLocalBox(Vector3.zero, new Vector3(float.PositiveInfinity, 1f, 1f), out _),
                Is.False);
            Assert.That(ScanBoxRegion.TryFromWorldBox(Vector3.zero, new Vector3(1f, 1f, 0f), Matrix4x4.identity, out _),
                Is.False);
            Matrix4x4 collapsed = Matrix4x4.Scale(new Vector3(1f, 1f, 0f));
            Assert.That(ScanBoxRegion.TryFromWorldBox(Vector3.zero, Vector3.one, collapsed, out _), Is.False,
                "A frame that flattens the footprint has no area.");
            Matrix4x4 broken = Matrix4x4.identity;
            broken.m03 = float.NaN;
            Assert.That(ScanBoxRegion.TryFromWorldBox(Vector3.zero, Vector3.one, broken, out _), Is.False);
        }

        [Test]
        public void TheWireFormRoundTripsEveryField()
        {
            ScanBoxRegion region = LocalBox(new Vector3(0.1f, 0.3f, -0.2f), new Vector3(1f, 0.6f, 0.4f));
            byte[] wire = region.Encode();
            Assert.That(ScanBoxRegion.TryDecode(wire, out ScanBoxRegion decoded), Is.True);
            Assert.That(decoded.FootprintPolygon(), Is.EqualTo(region.FootprintPolygon()));
            Assert.That(decoded.BaseY, Is.EqualTo(region.BaseY));
            Assert.That(decoded.TopY, Is.EqualTo(region.TopY));
            Assert.That(decoded.Encode(), Is.EqualTo(wire), "Source and receiver write the same bytes.");
        }

        [Test]
        public void ABadPayloadNeverDecodes()
        {
            byte[] good = LocalBox(Vector3.zero, Vector3.one).Encode();
            Assert.That(ScanBoxRegion.TryDecode(null, out _), Is.False);
            Assert.That(ScanBoxRegion.TryDecode(Array.Empty<byte>(), out _), Is.False);
            Assert.That(ScanBoxRegion.TryDecode(good.Take(good.Length - 1).ToArray(), out _), Is.False, "truncated");
            Assert.That(ScanBoxRegion.TryDecode(good.Concat(new byte[] { 0 }).ToArray(), out _), Is.False, "trailing byte");

            byte[] wrongMagic = (byte[])good.Clone();
            wrongMagic[0] ^= 0xFF;
            Assert.That(ScanBoxRegion.TryDecode(wrongMagic, out _), Is.False);

            byte[] wrongVersion = (byte[])good.Clone();
            wrongVersion[4] = 9;
            Assert.That(ScanBoxRegion.TryDecode(wrongVersion, out _), Is.False);

            byte[] tooFewVertices = (byte[])good.Clone();
            tooFewVertices[8] = 2;
            Assert.That(ScanBoxRegion.TryDecode(tooFewVertices, out _), Is.False);

            byte[] hugeCount = (byte[])good.Clone();
            hugeCount[8] = 0xFF;
            Assert.That(ScanBoxRegion.TryDecode(hugeCount, out _), Is.False, "the count must match the length");

            byte[] notANumber = (byte[])good.Clone();
            BitConverter.GetBytes(float.NaN).CopyTo(notANumber, 12);
            Assert.That(ScanBoxRegion.TryDecode(notANumber, out _), Is.False);
        }

        [Test]
        public void AClockwiseOrEmptyFootprintOnTheWireIsRefused()
        {
            ScanBoxRegion region = LocalBox(Vector3.zero, Vector3.one);
            byte[] clockwise = (byte[])region.Encode().Clone();
            // Vertices start at byte 12. Swap vertices 1 and 3 to reverse the winding.
            byte[] first = clockwise.Skip(12 + 8).Take(8).ToArray();
            byte[] third = clockwise.Skip(12 + 24).Take(8).ToArray();
            third.CopyTo(clockwise, 12 + 8);
            first.CopyTo(clockwise, 12 + 24);
            Assert.That(ScanBoxRegion.TryDecode(clockwise, out _), Is.False);

            byte[] inverted = (byte[])region.Encode().Clone();
            BitConverter.GetBytes(2f).CopyTo(inverted, 12 + 32);
            BitConverter.GetBytes(1f).CopyTo(inverted, 12 + 36);
            Assert.That(ScanBoxRegion.TryDecode(inverted, out _), Is.False, "top below base");
        }

        // The core's surface normal in Unity axes is the negated Unity cross product of the first two edges
        // (SpatialAuthoringCompiler.SurfaceNormalUnity). The pure layer cannot call the compiler, so it repeats
        // that one line.
        private static float UpNormalY(Vector3[] surface) =>
            -Vector3.Cross(surface[1] - surface[0], surface[2] - surface[0]).y;

        private static void AssertSameLoop(Vector2[] actual, Vector2[] expected)
        {
            Assert.That(actual, Has.Length.EqualTo(expected.Length));
            foreach (Vector2 point in expected)
            {
                Assert.That(actual.Any(candidate => (candidate - point).magnitude < 1e-4f), Is.True,
                    $"missing vertex {point}");
            }
        }
    }
}
#endif
