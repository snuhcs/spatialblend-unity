// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>The engine that stages observed geometry in the planned layout, against a recording sink, over the sample rooms.</summary>
    [Category("FreeformRestoration")]
    public sealed class SceneGeometryLayoutEngineTests
    {
        private sealed class Sink
        {
            public readonly Dictionary<int, (int parent, int pieces)> Staged = new Dictionary<int, (int, int)>();
            public readonly List<string> Log = new List<string>();
            public bool Stage(SceneGeometryDescriptor d, int parent, Matrix4x4 local, IReadOnlyList<Vector2[]> pieces)
            {
                Assert.That(parent == 0 || Staged.ContainsKey(parent), Is.True, "a parent is staged before the tree nested under it");
                Staged[d.SourceKey] = (parent, pieces == null ? -1 : pieces.Count);
                Log.Add("stage " + d.SourceKey + " under " + parent);
                return true;
            }
            public bool Remove(int key)
            {
                Assert.That(Staged.Keys.Any(k => Staged[k].parent == key), Is.False, "a tree is removed after the trees nested under it");
                Staged.Remove(key);
                Log.Add("remove " + key);
                return true;
            }
        }

        [Test]
        public void EveryAnchorOfTheSampleRoomsIsStagedAfterItsParentAndRemovingAParentStagesItsChildrenAgain()
        {
            foreach (MrukSampleRoomFixtures.Room room in MrukSampleRoomFixtures.Load())
            {
                var sink = new Sink();
                var engine = new SceneGeometryLayoutEngine(sink.Stage, sink.Remove);
                foreach (SceneGeometryDescriptor d in room.Anchors) Assert.That(engine.Upsert(d), Is.True, room.Name);
                Assert.That(sink.Staged.Count, Is.EqualTo(room.Anchors.Count), room.Name);

                int nestedBefore = sink.Staged.Values.Count(v => v.parent != 0);
                var parentWithChild = sink.Staged.Values.Where(v => v.parent != 0).Select(v => v.parent).Distinct().ToList();
                foreach (int parent in parentWithChild)
                {
                    SceneGeometryDescriptor d = room.Anchors.First(a => a.SourceKey == parent);
                    Assert.That(engine.Remove(parent), Is.True, room.Name);
                    Assert.That(sink.Staged.ContainsKey(parent), Is.False, room.Name);
                    Assert.That(sink.Staged.Count, Is.EqualTo(room.Anchors.Count - 1), room.Name + ": the children are staged again");
                    Assert.That(engine.Upsert(d), Is.True, room.Name);
                    Assert.That(sink.Staged.Count, Is.EqualTo(room.Anchors.Count), room.Name);
                }
                Assert.That(sink.Staged.Values.Count(v => v.parent != 0), Is.EqualTo(nestedBefore), room.Name + ": the layout is the same after removing and adding back");
            }
        }

        [Test]
        public void ALampOnACouchInBedroomOneNestsUnderIt()
        {
            MrukSampleRoomFixtures.Room room = MrukSampleRoomFixtures.Load().First(r => r.Name == "MeshBedroom1");
            var sink = new Sink();
            var engine = new SceneGeometryLayoutEngine(sink.Stage, sink.Remove);
            foreach (SceneGeometryDescriptor d in room.Anchors) engine.Upsert(d);
            int couch = room.Anchors[room.Labels.IndexOf("COUCH")].SourceKey, lamp = room.Anchors[room.Labels.IndexOf("LAMP")].SourceKey;
            Assert.That(sink.Staged[lamp].parent, Is.EqualTo(couch), string.Join("; ", sink.Log));
        }

        // ---- content standing on anchors ----

        private static SceneGeometryDescriptor Box(int key, float cx, float cz, float sx, float sz)
        {
            // An MRUK-style volume anchor standing on the floor: frame Rx(-90) about its centre, volume (width, depth, height) in it.
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(cx, 0f, cz), Quaternion.Euler(-90f, 0f, 0f), Vector3.one);
            Assert.That(SceneGeometryDescriptor.TryCreate(key, world, new Bounds(Vector3.zero, new Vector3(sx, sz, 0.7f)), null,
                Rect.MinMaxRect(-sx / 2f, -sz / 2f, sx / 2f, sz / 2f), out SceneGeometryDescriptor d), Is.True);
            return d;
        }

        private sealed class ContentSink
        {
            public readonly List<string> Moves = new List<string>();
            public readonly Dictionary<long, (int anchor, Matrix4x4 local)> Where = new Dictionary<long, (int, Matrix4x4)>();
            public bool Reparent(long element, int anchor, Matrix4x4 local)
            {
                Moves.Add(element + "->" + (anchor == SceneGeometryLayoutEngine.Holding ? "holding" : anchor == SceneGeometryLayoutEngine.Room ? "room" : "anchor" + anchor));
                Where[element] = (anchor, local);
                return true;
            }
        }

        private static (SceneGeometryLayoutEngine engine, Sink sink, ContentSink content, SceneGeometryLayoutEngine.Content chess) OnATable()
        {
            var sink = new Sink();
            var content = new ContentSink();
            var engine = new SceneGeometryLayoutEngine(sink.Stage, sink.Remove, content.Reparent);
            // A rug (key 1) holds a table (key 2) holds the chess board standing on it.
            Assert.That(engine.Upsert(Box(1, 0f, 0f, 4f, 4f)), Is.True);
            Assert.That(engine.Upsert(Box(2, 0.2f, 0f, 1.6f, 0.9f)), Is.True);
            Matrix4x4 chessWorld = Matrix4x4.TRS(new Vector3(0.3f, 0.74f, 0f), Quaternion.identity, Vector3.one);
            Vector2[] ring = { new Vector2(-0.1f, -0.2f), new Vector2(0.7f, -0.2f), new Vector2(0.7f, 0.2f), new Vector2(-0.1f, 0.2f) };
            var chess = new SceneGeometryLayoutEngine.Content
            {
                Element = 77, AnchorKey = 2, World = () => chessWorld, Footprint = () => ring, Extent = () => ring,
                Moved = (key, world) => { },
            };
            engine.AddContent(chess);
            return (engine, sink, content, chess);
        }

        [Test]
        public void ContentOnAnAnchorIsParkedAndSetDownAgainWhenTheAnchorIsRestaged()
        {
            var (engine, sink, content, chess) = OnATable();
            Assert.That(engine.Upsert(Box(2, 0.2f, 0f, 1.6f, 0.9f)), Is.True, "the anchor is replaced by a fresh scan of itself");
            CollectionAssert.AreEqual(new[] { "77->holding", "77->anchor2" }, content.Moves, "parked before the table's tree goes, set down under the new one");
            Assert.That(chess.Parked, Is.False);
            Assert.That(chess.AnchorKey, Is.EqualTo(2));
            Matrix4x4 tableWorld = SceneGeometryFootprint.Element(Box(2, 0.2f, 0f, 1.6f, 0.9f));
            Assert.That(content.Where[77].local, Is.EqualTo(tableWorld.inverse * chess.World()), "its transform is relative to the new parent, its world pose unchanged");
        }

        [Test]
        public void ContentOnARemovedAnchorMovesToTheNextContainingAncestorOrTheRoom()
        {
            var (engine, sink, content, chess) = OnATable();
            Assert.That(engine.Remove(2), Is.True);
            CollectionAssert.AreEqual(new[] { "77->holding", "77->anchor1" }, content.Moves, "the rug contains the chess board, so it goes there");
            Matrix4x4 rugWorld = SceneGeometryFootprint.Element(Box(1, 0f, 0f, 4f, 4f));
            Assert.That(content.Where[77].local, Is.EqualTo(rugWorld.inverse * chess.World()));
            content.Moves.Clear();
            Assert.That(engine.Remove(1), Is.True);
            CollectionAssert.AreEqual(new[] { "77->holding", "77->room" }, content.Moves);
            Assert.That(content.Where[77].local, Is.EqualTo(chess.World()), "under the room the local transform is the world one");
            Assert.That(chess.AnchorKey, Is.EqualTo(SceneGeometryLayoutEngine.Room));
        }

        [Test]
        public void ContentIsSetDownUnderAnAnchorThatArrivesLaterAndContainsIt()
        {
            var sink = new Sink();
            var content = new ContentSink();
            var engine = new SceneGeometryLayoutEngine(sink.Stage, sink.Remove, content.Reparent);
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(0.3f, 0.74f, 0f), Quaternion.identity, Vector3.one);
            Vector2[] ring = { new Vector2(-0.1f, -0.2f), new Vector2(0.7f, -0.2f), new Vector2(0.7f, 0.2f), new Vector2(-0.1f, 0.2f) };
            engine.AddContent(new SceneGeometryLayoutEngine.Content { Element = 5, AnchorKey = 0, World = () => world, Footprint = () => ring, Extent = () => ring, Moved = (k, w) => { } });
            Assert.That(engine.Upsert(Box(9, 0.2f, 0f, 1.6f, 0.9f)), Is.True);
            CollectionAssert.AreEqual(new[] { "5->holding", "5->anchor9" }, content.Moves, "its extent overlaps the table, so it stands on it instead of beside it");
        }

        // a tree with two levels below it (a rug, a table on it, a lamp on the table) is removed deepest first. The
        // Sink asserts that no staged tree still has the removed one as its parent, which breadth-first order breaks.
        [Test]
        public void RemovingATreeWithTwoLevelsBelowItRemovesTheDeepestFirst()
        {
            var sink = new Sink();
            var engine = new SceneGeometryLayoutEngine(sink.Stage, sink.Remove);
            Assert.That(engine.Upsert(Box(1, 0f, 0f, 4f, 4f)), Is.True);
            Assert.That(engine.Upsert(Box(2, 0.2f, 0f, 1.6f, 0.9f)), Is.True);
            Assert.That(engine.Upsert(Box(3, 0.2f, 0f, 0.3f, 0.3f)), Is.True);
            Assert.That(sink.Staged[3].parent, Is.EqualTo(2));
            Assert.That(sink.Staged[2].parent, Is.EqualTo(1));
            sink.Log.Clear();
            Assert.That(engine.Remove(1), Is.True);
            Assert.That(sink.Log.IndexOf("remove 3"), Is.LessThan(sink.Log.IndexOf("remove 2")));
            Assert.That(sink.Log.IndexOf("remove 2"), Is.LessThan(sink.Log.IndexOf("remove 1")));
            Assert.That(sink.Staged.ContainsKey(1), Is.False);
            Assert.That(sink.Staged.ContainsKey(2) && sink.Staged.ContainsKey(3), Is.True, "the trees below are staged again");
        }

        // content that MOVES is nested by the hierarchy where it now is (policy C), on and off furniture.
        [Test]
        public void ContentMovedOntoATableNestsUnderItAndMovedOffIsSetDownUnderTheRoom()
        {
            var sink = new Sink();
            var content = new ContentSink();
            var engine = new SceneGeometryLayoutEngine(sink.Stage, sink.Remove, content.Reparent);
            Assert.That(engine.Upsert(Box(2, 0.2f, 0f, 1.6f, 0.9f)), Is.True);
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(3f, 0f, 0f), Quaternion.identity, Vector3.one);
            Vector2[] ring = { new Vector2(-0.2f, -0.2f), new Vector2(0.2f, -0.2f), new Vector2(0.2f, 0.2f), new Vector2(-0.2f, 0.2f) };
            Vector3 at = new Vector3(3f, 0f, 0f);
            Vector2[] Ring() => ring.Select(p => new Vector2(p.x + at.x, p.y + at.z)).ToArray();
            int movedTo = -1;
            var chess = new SceneGeometryLayoutEngine.Content
            {
                Element = 8, AnchorKey = 0, World = () => Matrix4x4.TRS(at, Quaternion.identity, Vector3.one), Footprint = Ring, Extent = Ring,
                Moved = (key, w) => movedTo = key,
            };
            engine.AddContent(chess);
            Assert.That(engine.TryRehome(8), Is.False, "beside the table: still under the room, nothing to do");
            at = new Vector3(0.2f, 0.74f, 0f);
            Assert.That(engine.TryRehome(8), Is.True, "now on the table");
            Assert.That(chess.AnchorKey, Is.EqualTo(2));
            Assert.That(movedTo, Is.EqualTo(2));
            Assert.That(content.Where[8].anchor, Is.EqualTo(2));
            Assert.That(engine.TryRehome(8), Is.False, "same anchor again: the caller writes the transform");
            at = new Vector3(3f, 0f, 0f);
            Assert.That(engine.TryRehome(8), Is.True, "off the table");
            Assert.That(chess.AnchorKey, Is.EqualTo(SceneGeometryLayoutEngine.Room));
            Assert.That(content.Where[8].anchor, Is.EqualTo(SceneGeometryLayoutEngine.Room));
            Assert.That(engine.TryRehome(404), Is.False, "untracked content");
        }

        // a failed stage leaves the siblings laid out against the registered geometry, not against the failed request.
        // A large box that would contain both fails to stage: the two it would have adopted must not be left retired under it.
        [Test]
        public void AFailedStageLeavesTheRegisteredSiblingsStagedWhereTheyWere()
        {
            var sink = new Sink();
            int failKey = -1;
            var engine = new SceneGeometryLayoutEngine((d, parent, local, pieces) => d.SourceKey != failKey && sink.Stage(d, parent, local, pieces), sink.Remove);
            Assert.That(engine.Upsert(Box(1, 0f, 0f, 2f, 2f)), Is.True);
            Assert.That(engine.Upsert(Box(2, 1.5f, 0f, 2f, 2f)), Is.True, "partial overlap: staged, clipped");
            int pieces = sink.Staged[2].pieces;
            failKey = 3;
            Assert.That(engine.Upsert(Box(3, 0.5f, 0.5f, 6f, 6f)), Is.False);
            Assert.That(engine.Has(3), Is.False);
            Assert.That(engine.Has(1) && engine.Has(2), Is.True, "the registered siblings are staged again");
            Assert.That(sink.Staged[1].parent, Is.EqualTo(0));
            Assert.That(sink.Staged[2], Is.EqualTo((0, pieces)));
        }
    }
}
#endif
