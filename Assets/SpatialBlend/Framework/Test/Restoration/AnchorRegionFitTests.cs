// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The Containment region of a scene anchor (physical objects register a region automatically, like
    /// v1's SURROUND volume). The box is 1.2 m by 0.6 m at the origin of its frame; the stand-off is 0.5 m.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AnchorRegionFitTests
    {
        private const float Margin = 0.005f;
        private static readonly Vector2 BoxMin = new Vector2(-0.6f, -0.3f), BoxMax = new Vector2(0.6f, 0.3f);

        private static Vector2[] Rect(float x0, float y0, float x1, float y1) =>
            new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

        private static void Bounds(Vector2[] ring, out Vector2 min, out Vector2 max)
        {
            min = ring[0];
            max = ring[0];
            foreach (Vector2 p in ring) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        }

        [Test]
        public void OnOpenFloorTheRegionIsTheBoxWithTheFullStandOff()
        {
            Assert.That(AnchorRegionFit.TryFit(BoxMin, BoxMax, 0.5f, Rect(-5f, -5f, 5f, 5f), null, Margin,
                out Vector2[] region, out float[] offsets), Is.True);
            Assert.That(offsets, Is.EqualTo(new[] { 0.5f, 0.5f, 0.5f, 0.5f }));
            Bounds(region, out Vector2 min, out Vector2 max);
            Assert.That(min.x, Is.EqualTo(-1.1f).Within(1e-4f));
            Assert.That(max.y, Is.EqualTo(0.8f).Within(1e-4f));
        }

        // The ring ends 0.2 m past the box's +x face (a wall): that side keeps what lies on the floor.
        [Test]
        public void AWallCutsTheStandOffOnItsSide()
        {
            Assert.That(AnchorRegionFit.TryFit(BoxMin, BoxMax, 0.5f, Rect(-5f, -5f, 0.8f, 5f), null, Margin,
                out Vector2[] region, out float[] offsets), Is.True);
            Bounds(region, out Vector2 min, out Vector2 max);
            Assert.That(max.x, Is.LessThanOrEqualTo(0.8f - Margin + 1e-4f));
            Assert.That(offsets[1], Is.EqualTo(0.5f), "The other sides keep the stand-off.");
        }

        // A box that sticks out of the ring (furniture at a wall): its outer side has no strip on the floor, so the region's
        // edge lies inside the box, which covers it under the core's ring-or-furniture rule.
        [Test]
        public void ABoxPastTheRingKeepsItsOuterEdgeInsideTheBox()
        {
            Assert.That(AnchorRegionFit.TryFit(BoxMin, BoxMax, 0.5f, Rect(-5f, -5f, 0.3f, 5f), null, Margin,
                out Vector2[] region, out float[] offsets), Is.True);
            Bounds(region, out Vector2 min, out Vector2 max);
            Assert.That(max.x, Is.LessThanOrEqualTo(BoxMax.x - AnchorRegionFit.CoverInset + 1e-4f));
            Assert.That(offsets[1], Is.EqualTo(0.5f));
            // The strips beside it reach only the part of the face that is on the floor.
            Assert.That(WalkablePlacementSnap.Satisfies(Rect(min.x, BoxMax.y, max.x, max.y), Vector2.zero,
                new WalkableRegion(Rect(-5f, -5f, 0.3f, 5f), null), Margin), Is.True);
        }

        [Test]
        public void ANeighbourCutsTheStandOffAndStaysClear()
        {
            var neighbour = Rect(-1f, 0.5f, 1f, 1.2f);
            Assert.That(AnchorRegionFit.TryFit(BoxMin, BoxMax, 0.5f, Rect(-5f, -5f, 5f, 5f),
                new List<Vector2[]> { neighbour }, Margin, out Vector2[] region, out _), Is.True);
            Bounds(region, out _, out Vector2 max);
            Assert.That(max.y, Is.LessThanOrEqualTo(0.5f - Margin + 1e-4f));
        }

        // The residue case: the box itself overlaps its neighbour (the layout clipped its staged pieces). The region may not,
        // so that side goes inside the box.
        [Test]
        public void ABoxOverlappingItsNeighbourPullsThatSideIntoTheBox()
        {
            var neighbour = Rect(0.5f, -1f, 1.5f, 1f);
            Assert.That(AnchorRegionFit.TryFit(BoxMin, BoxMax, 0.5f, Rect(-5f, -5f, 5f, 5f),
                new List<Vector2[]> { neighbour }, Margin, out Vector2[] region, out float[] offsets), Is.True);
            Bounds(region, out _, out Vector2 max);
            Assert.That(max.x, Is.LessThanOrEqualTo(0.5f - Margin + 1e-4f));
            Assert.That(offsets[0], Is.LessThan(0f));
        }

        [Test]
        public void ABoxWithNoFloorBesideItGetsNoRegion()
        {
            Assert.That(AnchorRegionFit.TryFit(BoxMin, BoxMax, 0.5f, Rect(5f, 5f, 9f, 9f), null, Margin,
                out Vector2[] region, out _), Is.False);
            Assert.That(region, Is.Null);
        }
    }
}
#endif
