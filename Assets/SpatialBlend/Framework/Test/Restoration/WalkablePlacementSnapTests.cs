// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T3.3b, parity row AV10. The placement snap is a pure function over the authored region and the
    /// walkable region the app registered with the core (boundary plus holes).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class WalkablePlacementSnapTests
    {
        private const float Margin = 0.2f;
        private const float Tolerance = 1e-3f;

        private static Vector2[] Rect(float x0, float z0, float x1, float z1) =>
            new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };

        private static WalkableRegion Room(params Vector2[][] holes) =>
            new WalkableRegion(Rect(0f, 0f, 10f, 10f), holes);

        private static Vector2[] Moved(Vector2[] region, Vector2 translation) =>
            region.Select(point => point + translation).ToArray();

        private static void AssertWalkable(Vector2[] placed, WalkableRegion walkable, float margin)
        {
            foreach (Vector2 point in placed)
            {
                Assert.That(WalkablePlacementSnap.Contains(walkable.Boundary, point), Is.True, "vertex " + point);
                foreach (Vector2[] hole in walkable.Holes)
                    Assert.That(WalkablePlacementSnap.Contains(hole, point), Is.False, "vertex " + point);
            }
            foreach (Vector2[] hole in walkable.Holes)
                foreach (Vector2 corner in hole)
                    Assert.That(WalkablePlacementSnap.Contains(placed, corner), Is.False, "hole corner " + corner);
            AssertClearance(placed, walkable.Boundary, margin);
            foreach (Vector2[] hole in walkable.Holes)
                AssertClearance(placed, hole, margin);
        }

        // Every vertex stays margin metres from the ring's edges.
        private static void AssertClearance(Vector2[] placed, Vector2[] ring, float margin)
        {
            foreach (Vector2 point in placed)
            {
                float nearest = float.MaxValue;
                for (int i = 0; i < ring.Length; ++i)
                {
                    Vector2 a = ring[i];
                    Vector2 b = ring[(i + 1) % ring.Length];
                    Vector2 edge = b - a;
                    float t = Mathf.Clamp01(Vector2.Dot(point - a, edge) / edge.sqrMagnitude);
                    nearest = Mathf.Min(nearest, (point - (a + edge * t)).magnitude);
                }
                Assert.That(nearest, Is.GreaterThanOrEqualTo(margin - Tolerance), "vertex " + point);
            }
        }

        [Test]
        public void InsideRegion_IsUnchanged()
        {
            Vector2[] region = Rect(3f, 3f, 5f, 5f);
            Vector2 shift = WalkablePlacementSnap.Snap(region, Room(), Margin, out bool satisfied);
            Assert.That(shift, Is.EqualTo(Vector2.zero));
            Assert.That(satisfied, Is.True);
        }

        [Test]
        public void PartlyOutside_MovesInwardByTheMinimumAmountPlusTheMargin()
        {
            // The right edge is 1 m past the boundary, so the move is 1 m plus the margin, along x only.
            Vector2 shift = WalkablePlacementSnap.Snap(Rect(9f, 4f, 11f, 6f), Room(), Margin);
            Assert.That(shift.x, Is.EqualTo(-1.2f).Within(Tolerance));
            Assert.That(shift.y, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void OutsideACorner_MovesAlongBothAxes()
        {
            Vector2[] region = Rect(-1f, -1f, 1f, 1f);
            Vector2 shift = WalkablePlacementSnap.Snap(region, Room(), Margin);
            Assert.That(shift.x, Is.EqualTo(1.2f).Within(Tolerance));
            Assert.That(shift.y, Is.EqualTo(1.2f).Within(Tolerance));
            AssertWalkable(Moved(region, shift), Room(), Margin);
        }

        [Test]
        public void InsideButWithinTheMargin_KeepsTheInwardMargin()
        {
            // The right edge is 0.1 m from the boundary. Only the missing 0.1 m is added.
            Vector2 shift = WalkablePlacementSnap.Snap(Rect(8.9f, 4f, 9.9f, 6f), Room(), Margin);
            Assert.That(shift.x, Is.EqualTo(-0.1f).Within(Tolerance));
            Assert.That(shift.y, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(WalkablePlacementSnap.Snap(Rect(8.9f, 4f, 9.9f, 6f), Room(), 0f), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void OverlappingAHole_IsPushedOutOfTheHole()
        {
            WalkableRegion walkable = Room(Rect(4f, 4f, 6f, 6f));
            // The region's right edge is 0.5 m into the hole, so the move is 0.5 m plus the margin, along -x.
            Vector2[] edgeOverlap = Rect(2f, 4.8f, 4.5f, 5.2f);
            Vector2 shift = WalkablePlacementSnap.Snap(edgeOverlap, walkable, Margin, out bool satisfied);
            Assert.That(satisfied, Is.True);
            Assert.That(shift.x, Is.EqualTo(-0.7f).Within(Tolerance));
            Assert.That(shift.y, Is.EqualTo(0f).Within(Tolerance));

            Vector2[] cornerOverlap = Rect(3f, 3f, 5f, 5f);
            shift = WalkablePlacementSnap.Snap(cornerOverlap, walkable, Margin, out satisfied);
            Assert.That(satisfied, Is.True);
            AssertWalkable(Moved(cornerOverlap, shift), walkable, Margin);
        }

        [Test]
        public void ARegionAroundAHole_IsPushedOffTheHoleCorners()
        {
            // No region vertex is in the hole, but the hole sits inside the region.
            WalkableRegion walkable = Room(Rect(4.5f, 4.5f, 5.5f, 5.5f));
            Vector2[] region = Rect(3f, 4f, 7f, 6f);
            Vector2 shift = WalkablePlacementSnap.Snap(region, walkable, Margin, out bool satisfied);
            Assert.That(satisfied, Is.True);
            Assert.That(shift, Is.Not.EqualTo(Vector2.zero));
            AssertWalkable(Moved(region, shift), walkable, Margin);
        }

        [Test]
        public void HoleAndBoundaryTogether_AreBothSatisfied()
        {
            WalkableRegion walkable = Room(Rect(6f, 3f, 8f, 5f));
            // The only free space is the 1.6 m strip between the hole and the boundary, and the region is 1.2 m wide.
            Vector2[] region = Rect(7.5f, 3.5f, 8.7f, 5.5f);
            Vector2 shift = WalkablePlacementSnap.Snap(region, walkable, Margin, out bool satisfied);
            Assert.That(satisfied, Is.True);
            Assert.That(shift.x, Is.EqualTo(0.7f).Within(Tolerance));
            AssertWalkable(Moved(region, shift), walkable, Margin);
        }

        [Test]
        public void ARegionThatCannotFit_ReportsItWithoutHanging()
        {
            // 4 m wide against 3.6 m of free width either side of a 2 m hole.
            Vector2 shift = WalkablePlacementSnap.Snap(Rect(3f, 3f, 7f, 7f), Room(Rect(4f, 4f, 6f, 6f)), Margin,
                out bool satisfied);
            Assert.That(satisfied, Is.False);
            Assert.That(float.IsNaN(shift.x) || float.IsNaN(shift.y), Is.False);
        }

        [Test]
        public void UnusableInput_ReturnsNoMove()
        {
            Assert.That(WalkablePlacementSnap.Snap(null, Room(), Margin), Is.EqualTo(Vector2.zero));
            Assert.That(WalkablePlacementSnap.Snap(new[] { Vector2.zero, Vector2.one }, Room(), Margin),
                Is.EqualTo(Vector2.zero));
            Assert.That(WalkablePlacementSnap.Snap(Rect(9f, 4f, 11f, 6f), null, Margin), Is.EqualTo(Vector2.zero));
            Assert.That(WalkablePlacementSnap.Snap(Rect(9f, 4f, 11f, 6f),
                new WalkableRegion(null, null), Margin), Is.EqualTo(Vector2.zero));
        }
    }
}
#endif
