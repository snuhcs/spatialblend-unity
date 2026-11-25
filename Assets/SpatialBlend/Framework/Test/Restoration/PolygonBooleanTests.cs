// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The outer ring of a union, part of it clipped (the room's registered ring is the NavMesh's ring merged
    /// with the furniture it carved, the furniture kept inside the floor).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class PolygonBooleanTests
    {
        private static Vector2[] Rect(float x0, float y0, float x1, float y1) =>
            new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

        [Test]
        public void TheUnionOfARingAndAnOverlappingBoxIsOneRing()
        {
            Vector2[] ring = PolygonBoolean.OuterRing(new[] { Rect(0f, 0f, 2f, 2f) }, new[] { Rect(1.5f, 0.5f, 3f, 1f) }, null);
            Assert.That(ring, Is.Not.Null);
            Assert.That(PolygonBoolean.SignedArea(ring), Is.EqualTo(4f + 1.5f * 0.5f - 0.5f * 0.5f).Within(1e-4f));
            Assert.That(WalkablePlacementSnap.Contains(ring, new Vector2(2.8f, 0.75f)), Is.True);
            Assert.That(WalkablePlacementSnap.Contains(ring, new Vector2(2.8f, 1.5f)), Is.False);
        }

        // Only the furniture is clipped by the floor: the kept ring stays whole even where the floor outline cuts it.
        [Test]
        public void TheClipCutsOnlyTheClippedRings()
        {
            Vector2[] floor = Rect(-1f, -1f, 2.5f, 1.8f);
            Vector2[] ring = PolygonBoolean.OuterRing(new[] { Rect(0f, 0f, 2f, 2f) }, new[] { Rect(1.5f, 0.5f, 3f, 1f) }, floor);
            Assert.That(PolygonBoolean.SignedArea(ring), Is.EqualTo(4f + 0.5f * 0.5f).Within(1e-4f));
            Assert.That(WalkablePlacementSnap.Contains(ring, new Vector2(1f, 1.9f)), Is.True, "The kept ring past the floor stays.");
            Assert.That(WalkablePlacementSnap.Contains(ring, new Vector2(2.7f, 0.75f)), Is.False, "The furniture stops at the floor.");
        }

        // Shared and collinear edges, and a box touching the ring along an edge, need no case of their own.
        [Test]
        public void TouchingAndCollinearEdgesMergeCleanly()
        {
            Vector2[] ring = PolygonBoolean.OuterRing(new[] { Rect(0f, 0f, 2f, 2f) },
                new[] { Rect(2f, 0f, 3f, 2f), Rect(0f, 0f, 2f, 1f) }, null);
            Assert.That(ring, Has.Length.EqualTo(4), "One rectangle, its collinear vertices dropped.");
            Assert.That(PolygonBoolean.SignedArea(ring), Is.EqualTo(6f).Within(1e-4f));
        }

        [Test]
        public void ASeparatePieceIsNotPartOfTheOuterRing()
        {
            Vector2[] ring = PolygonBoolean.OuterRing(new[] { Rect(0f, 0f, 2f, 2f) }, new[] { Rect(5f, 5f, 6f, 6f) }, null);
            Assert.That(PolygonBoolean.SignedArea(ring), Is.EqualTo(4f).Within(1e-4f));
        }

        private static Vector2 Rot(Vector2 p, Vector2 c, float deg)
        {
            float a = deg * Mathf.Deg2Rad;
            Vector2 d = p - c;
            return c + new Vector2(d.x * Mathf.Cos(a) - d.y * Mathf.Sin(a), d.x * Mathf.Sin(a) + d.y * Mathf.Cos(a));
        }

        // A probe case (seed 7): a 6 x 5 m ring at room-scale coordinates, a 0.6 x 2 m box turned a few degrees with
        // its grown edge near the ring's x = 6 edge, a second one turned three times as much, a floor clip. These returned null:
        // an edge met a ring edge a fraction of a millimetre from the ring's corner, so the chain never closed.
        [TestCase(-2.2655756f, 5.5734982f, 1.6639184f)]
        [TestCase(1.8646078f, 5.451986f, 1.0781229f)]
        [TestCase(1.5660337f, 5.542203f, 1.1339706f)]
        [TestCase(-0.115765594f, 5.5123844f, 1.6918236f)]
        public void ACrossingNextToACornerStillClosesTheRing(float deg, float right, float y0)
        {
            var off = new Vector2(3.7f, -8.2f);
            Vector2[] ring = Rect(0f, 0f, 6f, 5f);
            for (int i = 0; i < 4; ++i) ring[i] += off;
            var c = new Vector2(right - 0.3f, y0 + 1f) + off;
            var box = Rect(right - 0.6f, y0, right, y0 + 2f);
            for (int i = 0; i < 4; ++i) box[i] = Rot(box[i] + off, c, deg);
            var box2 = new Vector2[4];
            for (int i = 0; i < 4; ++i) box2[i] = Rot(box[i] + new Vector2(-0.4f, 1.3f), c, deg * 3f);
            Vector2[] floor = Rect(-0.5f, -0.5f, 6.5f, 5.5f);
            for (int i = 0; i < 4; ++i) floor[i] += off;

            Vector2[] merged = PolygonBoolean.OuterRing(new[] { ring },
                new[] { PolygonBoolean.InflateConvex(box, 0.5f), PolygonBoolean.InflateConvex(box2, 0.5f) }, floor);

            Assert.That(merged, Is.Not.Null);
            Assert.That(WalkableRegionFit.Contains(merged, ring), Is.True);
        }

        // Stress (seed 12345): a box edge a tenth of a millimetre inside a floor edge that lies on the ring's own edge leaves a
        // sliver thinner than the probe, whose dangling piece led the chain into a dead end.
        [Test]
        public void ASliverThinnerThanTheProbeDoesNotOpenTheRing()
        {
            Vector2[] ring = new[]{new Vector2(4.05387f,6.47861f),new Vector2(10.05387f,6.47861f),new Vector2(10.05387f,11.47861f),new Vector2(4.05387f,11.47861f)};
            Vector2[] floor = new[]{new Vector2(3.5538702f,5.97861f),new Vector2(10.05387f,5.97861f),new Vector2(10.05387f,11.97861f),new Vector2(3.5538702f,11.97861f)};
            var grown = new[] { new[]{new Vector2(8.488246f,7.3464017f),new Vector2(10.088099f,7.324739f),new Vector2(10.121948f,9.824511f),new Vector2(8.522096f,9.846172f)}, new[]{new Vector2(8.453761f,11.874429f),new Vector2(8.455401f,9.37443f),new Vector2(10.055401f,9.37548f),new Vector2(10.053761f,11.875479f)} };

            Vector2[] merged = PolygonBoolean.OuterRing(new[] { ring }, grown, floor);

            Assert.That(merged, Is.Not.Null);
            Assert.That(WalkableRegionFit.Contains(merged, ring), Is.True);
        }

        [Test]
        public void InflatingABoxContainsEveryPointWithinTheDistance()
        {
            Vector2[] grown = PolygonBoolean.InflateConvex(Rect(0f, 0f, 1f, 0.5f), 0.5f);
            Assert.That(PolygonBoolean.SignedArea(grown), Is.EqualTo(2f * 1.5f).Within(1e-4f));
            Assert.That(WalkablePlacementSnap.Contains(grown, new Vector2(-0.45f, -0.45f)), Is.True);
            Assert.That(WalkablePlacementSnap.Contains(grown, new Vector2(1.49f, 0.25f)), Is.True);
        }
    }
}
#endif
