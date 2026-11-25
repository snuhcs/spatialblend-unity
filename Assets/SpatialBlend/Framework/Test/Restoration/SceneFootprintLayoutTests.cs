// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Policy C: a footprint entirely inside a larger one nests under it; a partial overlap keeps the two
    /// as siblings and clips the smaller by the larger. Over the eight scanned rooms Meta ships with MR Utility Kit.
    /// </summary>
    [Category("FreeformRestoration")]
    public sealed class SceneFootprintLayoutTests
    {
        private static Vector2[] Rect(float x0, float y0, float x1, float y1) =>
            new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

        [Test]
        public void AFootprintInsideALargerOneNestsAndIsLeftWhole()
        {
            var plan = SceneFootprintLayout.Compute(new[]
            {
                new SceneFootprintLayout.Input(1, Rect(0, 0, 2, 1)),      // table
                new SceneFootprintLayout.Input(2, Rect(0.5f, 0.2f, 0.9f, 0.6f)), // lamp on it
            });
            Assert.That(plan[1].ParentKey, Is.EqualTo(0));
            Assert.That(plan[2].ParentKey, Is.EqualTo(1));
            Assert.That(plan[2].Pieces, Is.Null);
            Assert.That(plan[1].Pieces, Is.Null);
        }

        [Test]
        public void ATouchingFootprintIsLeftAloneAndAPartialOneIsClippedByTheLarger()
        {
            var touching = SceneFootprintLayout.Compute(new[]
            {
                new SceneFootprintLayout.Input(1, Rect(0, 0, 2, 1)), new SceneFootprintLayout.Input(2, Rect(2, 0, 3, 1)),
            });
            Assert.That(touching[2].ParentKey, Is.EqualTo(0));
            Assert.That(touching[2].Pieces, Is.Null, "edge contact is not an overlap");

            var partial = SceneFootprintLayout.Compute(new[]
            {
                new SceneFootprintLayout.Input(1, Rect(0, 0, 2, 1)), new SceneFootprintLayout.Input(2, Rect(1.5f, 0.5f, 3f, 1.5f)),
            });
            Assert.That(partial[2].ParentKey, Is.EqualTo(0), "a partial overlap stays a sibling");
            Assert.That(partial[2].Pieces, Is.Not.Null);
            Assert.That(partial[1].Pieces, Is.Null, "the larger footprint is kept whole");
            double clipped = partial[2].Pieces.Sum(p => SceneFootprintLayout.Area(p));
            Assert.That(clipped, Is.EqualTo(1.5 * 1.0 - 0.5 * 0.5).Within(1e-4));
        }

        [Test]
        public void TheEightSampleRoomsHaveNoOverlappingSiblingsAndTheUnionOfTheirFootprintsIsUnchanged()
        {
            List<MrukSampleRoomFixtures.Room> rooms = MrukSampleRoomFixtures.Load();
            Assert.That(rooms.Count, Is.EqualTo(8));
            int nested = 0, clipped = 0, anchors = 0;
            foreach (MrukSampleRoomFixtures.Room room in rooms)
            {
                var inputs = new List<SceneFootprintLayout.Input>();
                var rings = new Dictionary<int, Vector2[]>();
                foreach (SceneGeometryDescriptor d in room.Anchors)
                {
                    Vector2[] ring = null;
                    if (SceneGeometryFootprint.IsUpright(d)) SceneGeometryFootprint.TryRing(d, out ring);
                    inputs.Add(new SceneFootprintLayout.Input(d.SourceKey, ring));
                    if (ring != null) rings[d.SourceKey] = ring;
                }
                anchors += rings.Count;
                Dictionary<int, SceneFootprintLayout.Placement> plan = SceneFootprintLayout.Compute(inputs);

                // 1. No two siblings overlap (beyond the core's data precision).
                foreach (var group in plan.Where(p => rings.ContainsKey(p.Key)).GroupBy(p => p.Value.ParentKey))
                {
                    var members = group.ToList();
                    for (int i = 0; i < members.Count; ++i)
                        for (int j = i + 1; j < members.Count; ++j)
                            foreach (Vector2[] a in Pieces(members[i], rings))
                                foreach (Vector2[] b in Pieces(members[j], rings))
                                    Assert.That(SceneFootprintLayout.IntersectionArea(a, b),
                                        Is.LessThanOrEqualTo(SceneFootprintLayout.DataPrecision * System.Math.Min(SceneFootprintLayout.Area(a), SceneFootprintLayout.Area(b))),
                                        room.Name + ": siblings " + members[i].Key + " and " + members[j].Key);
                }

                // 2. A nested footprint is inside its parent's, whole.
                foreach (var pair in plan.Where(p => p.Value.ParentKey != 0))
                {
                    ++nested;
                    Assert.That(SceneFootprintLayout.Contains(rings[pair.Value.ParentKey], rings[pair.Key], SceneFootprintLayout.Area(rings[pair.Key])), Is.True, room.Name);
                }
                clipped += plan.Count(p => p.Value.Pieces != null);

                // 3. The union of the footprints, so the free space, is unchanged: sampled on a 2 cm grid.
                Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
                foreach (Vector2[] ring in rings.Values)
                    foreach (Vector2 p in ring) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
                long before = 0, after = 0, differ = 0;
                for (float x = lo.x; x <= hi.x; x += 0.02f)
                    for (float y = lo.y; y <= hi.y; y += 0.02f)
                    {
                        var point = new Vector2(x, y);
                        bool original = rings.Values.Any(r => Inside(r, point));
                        bool laidOut = plan.Where(p => rings.ContainsKey(p.Key)).Any(p => Pieces(p, rings).Any(r => Inside(r, point)));
                        if (original) ++before;
                        if (laidOut) ++after;
                        if (original != laidOut) ++differ;
                    }
                Assert.That(differ, Is.LessThanOrEqualTo(System.Math.Max(2, before / 2000)), room.Name + ": " + before + " cells before, " + after + " after");
            }
            Assert.That(anchors, Is.GreaterThan(50));
            Assert.That(nested, Is.GreaterThan(0), "a lamp or screen stands on a table in these rooms");
            Assert.That(clipped, Is.GreaterThan(0), "couches and tables overlap partially in these rooms");
        }

        // The clearance's derivation (SceneFootprintLayout.Clearance) bounds the deviation of a vertex on its way to the core:
        // stored as float32 in the element frame, placed by the element's float32 transform (the core composes them in double).
        [Test]
        public void ClearanceCoversTheRoundingOfTheSinglePrecisionPathToTheCore()
        {
            double worst = 0.0, bound = 0.0;
            foreach (MrukSampleRoomFixtures.Room room in MrukSampleRoomFixtures.Load())
            {
                var inputs = new List<SceneFootprintLayout.Input>();
                var byKey = new Dictionary<int, SceneGeometryDescriptor>();
                float largest = 1f;
                foreach (SceneGeometryDescriptor d in room.Anchors)
                {
                    Vector2[] ring = null;
                    if (SceneGeometryFootprint.IsUpright(d)) SceneGeometryFootprint.TryRing(d, out ring);
                    inputs.Add(new SceneFootprintLayout.Input(d.SourceKey, ring));
                    byKey[d.SourceKey] = d;
                    if (ring != null) foreach (Vector2 v in ring) largest = Mathf.Max(largest, Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y)));
                }
                bound = System.Math.Max(bound, SceneFootprintLayout.Clearance(largest));
                foreach (var pair in SceneFootprintLayout.Compute(inputs).Where(p => p.Value.Pieces != null))
                {
                    SceneGeometryDescriptor d = byKey[pair.Key];
                    Matrix4x4 e = SceneGeometryFootprint.Element(d);
                    foreach (Vector2[] piece in pair.Value.Pieces)
                        foreach (Vector2 planned in piece)
                        {
                            Vector2 local = SceneGeometryFootprint.ToElementLocal(d, planned);   // float32, as handed over
                            // the core: the float32 transform (as double) applied to the float32 local vertex, in double
                            double x = (double)e.m00 * local.x + (double)e.m02 * local.y + e.m03;
                            double z = (double)e.m20 * local.x + (double)e.m22 * local.y + e.m23;
                            worst = System.Math.Max(worst, System.Math.Max(System.Math.Abs(x - planned.x), System.Math.Abs(z - planned.y)));
                        }
                }
            }
            Assert.That(worst, Is.LessThanOrEqualTo(bound / 2.0), "a vertex deviates by less than half the clearance, so two touching pieces cannot overlap");
        }

        private static IEnumerable<Vector2[]> Pieces(KeyValuePair<int, SceneFootprintLayout.Placement> p, Dictionary<int, Vector2[]> rings) =>
            p.Value.Pieces ?? new List<Vector2[]> { rings[p.Key] };

        private static bool Inside(Vector2[] ring, Vector2 p)
        {
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
                if ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) < 0f) return false;
            }
            return true;
        }
    }
}
#endif
