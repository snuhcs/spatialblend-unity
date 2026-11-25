// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The registration fit of an authored region against the room's walkable ring (a host's core
    /// refuses ChessArea and WhiteboardArea with ContainmentEscapesParent when the regions cover the notch the NavMesh
    /// cuts around furniture at a wall). The ring here is a 4 m square with such a notch cut from its right wall.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class WalkableRegionFitTests
    {
        private static readonly Vector2[] NotchedRing =
        {
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 1f), new Vector2(3f, 1f),
            new Vector2(3f, 2f), new Vector2(4f, 2f), new Vector2(4f, 4f), new Vector2(0f, 4f),
        };

        private static Vector2[] Rect(float x0, float y0, float x1, float y1) =>
            new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

        private static Vector2[] Shifted(Vector2[] ring, Vector2 shift)
        {
            var moved = new Vector2[ring.Length];
            for (int i = 0; i < ring.Length; ++i) moved[i] = ring[i] + shift;
            return moved;
        }

        [Test]
        public void ANotchReachingIntoTheRegionIsNotContainedThoughEveryRegionVertexIsInside()
        {
            Vector2[] region = Rect(2f, 0.5f, 3.8f, 2.5f);
            foreach (Vector2 vertex in region)
                Assert.That(WalkablePlacementSnap.Contains(NotchedRing, vertex), Is.True, "Every vertex is on the floor.");

            Assert.That(WalkableRegionFit.Contains(NotchedRing, region), Is.False);
            Assert.That(WalkableRegionFit.Contains(NotchedRing, Rect(0.5f, 0.5f, 2.5f, 3.5f)), Is.True);
            Assert.That(WalkableRegionFit.Contains(NotchedRing, Rect(1f, 1f, 3f, 2f)), Is.True, "Touching the notch is fine.");
        }

        [Test]
        public void TheSnapMovesTheRegionOffTheNotch()
        {
            Vector2[] region = Rect(2f, 0.5f, 3.8f, 2.5f);
            Vector2 shift = WalkablePlacementSnap.Snap(region, new WalkableRegion(NotchedRing, null), 0f, out bool satisfied);

            Assert.That(satisfied, Is.True);
            Assert.That(shift.sqrMagnitude, Is.GreaterThan(0f), "The region used to be left over the notch.");
            Assert.That(WalkableRegionFit.Contains(NotchedRing, Shifted(region, shift)), Is.True);
        }

        // A thin wall through the region has no vertex inside it and holds no region vertex: the vertex rules alone accept
        // it. The verdict refuses an edge crossing, so the snap moves the region clear.
        [Test]
        public void TheSnapNeverAcceptsAThinHoleThroughTheRegion()
        {
            Vector2[] room = Rect(0f, 0f, 10f, 10f);
            Vector2[] wall = Rect(4.95f, 2f, 5.05f, 8f);
            Vector2[] region = Rect(3f, 4f, 7f, 6f);
            var walkable = new WalkableRegion(room, new[] { wall });
            Assert.That(WalkablePlacementSnap.Satisfies(region, Vector2.zero, walkable, 0f), Is.False);

            Vector2 shift = WalkablePlacementSnap.Snap(region, walkable, 0.01f, out bool satisfied);

            Assert.That(satisfied, Is.True);
            Assert.That(WalkableRegionFit.EdgesCross(Shifted(region, shift), wall), Is.False);
            foreach (Vector2 corner in wall)
                Assert.That(WalkablePlacementSnap.Contains(Shifted(region, shift), corner), Is.False);
        }
    }
}
#endif
