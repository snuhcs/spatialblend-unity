// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class NavMeshWalkableExtractorTests
    {
        [Test]
        public void Square_IsTheOuterRingWithoutHoles()
        {
            Vector3[] square =
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f)
            };
            AssertExtracted(square, new[] { 0, 1, 2, 0, 2, 3 },
                new[] { new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f) },
                new Vector2[0][]);

            Vector3[] withMidpoint =
            {
                new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f)
            };
            AssertExtracted(withMidpoint, new[] { 0, 1, 3, 1, 2, 3, 0, 3, 4 },
                new[] { new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f) },
                new Vector2[0][]);
        }

        [Test]
        public void SquareWithOneHole_KeepsTheHoleInsideTheBoundary()
        {
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f),
                new Vector3(1f, 0f, 1f), new Vector3(3f, 0f, 1f),
                new Vector3(3f, 0f, 3f), new Vector3(1f, 0f, 3f)
            };
            int[] indices =
            {
                0, 1, 5, 0, 5, 4,
                1, 2, 6, 1, 6, 5,
                2, 3, 7, 2, 7, 6,
                3, 0, 4, 3, 4, 7
            };
            AssertExtracted(vertices, indices,
                new[] { new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f) },
                new[]
                {
                    new[] { new Vector2(1f, 1f), new Vector2(1f, 3f), new Vector2(3f, 3f), new Vector2(3f, 1f) }
                });
        }

        [Test]
        public void TwoHoles_AreBothInsideTheBoundary()
        {
            var vertices = new Vector3[24];
            for (int y = 0; y < 4; ++y)
            for (int x = 0; x < 6; ++x)
                vertices[y * 6 + x] = new Vector3(x, 0f, y);

            var indices = new System.Collections.Generic.List<int>();
            for (int y = 0; y < 3; ++y)
            for (int x = 0; x < 5; ++x)
            {
                if ((x == 1 && y == 1) || (x == 3 && y == 1))
                    continue;
                int a = y * 6 + x;
                int b = a + 1;
                int c = a + 6 + 1;
                int d = a + 6;
                indices.Add(a); indices.Add(b); indices.Add(c);
                indices.Add(a); indices.Add(c); indices.Add(d);
            }

            AssertExtracted(vertices, indices.ToArray(),
                new[] { new Vector2(0f, 0f), new Vector2(5f, 0f), new Vector2(5f, 3f), new Vector2(0f, 3f) },
                new[]
                {
                    new[] { new Vector2(1f, 1f), new Vector2(1f, 2f), new Vector2(2f, 2f), new Vector2(2f, 1f) },
                    new[] { new Vector2(3f, 1f), new Vector2(3f, 2f), new Vector2(4f, 2f), new Vector2(4f, 1f) }
                });
        }

        [Test]
        public void LShape_KeepsTheReflexCorner()
        {
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 1f),
                new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, 2f), new Vector3(0f, 0f, 2f)
            };
            AssertExtracted(vertices, new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5 },
                new[]
                {
                    new Vector2(0f, 0f), new Vector2(2f, 0f), new Vector2(2f, 1f),
                    new Vector2(1f, 1f), new Vector2(1f, 2f), new Vector2(0f, 2f)
                },
                new Vector2[0][]);
        }

        [Test]
        public void SeamDuplicates_CancelTheSharedEdge()
        {
            // The second triangle repeats the diagonal endpoints 0.4 mm away.
            // Index-only cancellation would leave both copies of that edge.
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f),
                new Vector3(0.0004f, 0f, 0f), new Vector3(4.0004f, 0f, 4f)
            };
            AssertExtracted(vertices, new[] { 0, 1, 2, 4, 5, 3 },
                new[] { new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f) },
                new Vector2[0][]);
        }

        [Test]
        public void DegenerateInput_ProducesNoRegion()
        {
            Assert.That(NavMeshWalkableExtractor.TryExtract(null, new[] { 0, 1, 2 }, out _), Is.False);
            Assert.That(NavMeshWalkableExtractor.TryExtract(new[] { Vector3.zero }, null, out _), Is.False);
            Assert.That(NavMeshWalkableExtractor.TryExtract(new Vector3[0], new int[0], out _), Is.False);
            Assert.That(NavMeshWalkableExtractor.TryExtract(
                new[] { Vector3.zero, Vector3.right }, new[] { 0, 1 }, out _), Is.False);
            Assert.That(NavMeshWalkableExtractor.TryExtract(
                new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f) },
                new[] { 0, 1, 2 }, out _), Is.False);
            Assert.That(NavMeshWalkableExtractor.TryExtract(
                new[] { Vector3.zero, Vector3.right, new Vector3(0f, 0f, 1f) },
                new[] { 0, 1, 3 }, out _), Is.False);
        }

        private static void AssertExtracted(Vector3[] vertices, int[] indices, Vector2[] boundary, Vector2[][] holes)
        {
            Assert.That(NavMeshWalkableExtractor.TryExtract(vertices, indices, out WalkableRegion region), Is.True);
            AssertRing(region.Boundary, boundary);
            Assert.That(region.Holes, Has.Length.EqualTo(holes.Length));
            for (int i = 0; i < holes.Length; ++i)
                AssertRing(region.Holes[i], holes[i]);
        }

        private static void AssertRing(Vector2[] actual, Vector2[] expected)
        {
            Assert.That(actual, Has.Length.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.That(actual[i].x, Is.EqualTo(expected[i].x).Within(1e-4f));
                Assert.That(actual[i].y, Is.EqualTo(expected[i].y).Within(1e-4f));
            }
        }
    }
}
#endif
