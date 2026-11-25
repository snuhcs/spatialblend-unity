// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class DynamicSceneGeometryContractTests
    {
        [Test]
        public void RegisterDuplicate_IsNativeNoOp()
        {
            using (var fixture = new ManagerFixture())
            {
                var descriptor = Volume(1, Vector3.zero);
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                Assert.That(fixture.Manager.AddParents, Is.EqualTo(new[] { 1L }));
                Assert.That(fixture.Manager.Boxes, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void Upsert_RetiresTheOwnedParentBeforeStagingItsReplacement()
        {
            using (var fixture = new ManagerFixture())
            {
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Volume(2, Vector3.zero)), Is.True);
                Assert.That(fixture.Manager.UpsertLocalSceneGeometry(Volume(2, new Vector3(2f, 0f, 0f))), Is.True);
                CollectionAssert.AreEqual(new[] { 1L, 1L }, fixture.Manager.AddParents);
                CollectionAssert.AreEqual(new[] { 10L }, fixture.Manager.Removed);
                Assert.That(fixture.Manager.Boxes, Has.Count.EqualTo(2));
            }
        }

        [Test]
        public void Remove_RetiresExactOwnedParentAndThenIsIdempotent()
        {
            using (var fixture = new ManagerFixture())
            {
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Volume(3, Vector3.zero)), Is.True);
                Assert.That(fixture.Manager.RemoveLocalSceneGeometry(3), Is.True);
                Assert.That(fixture.Manager.RemoveLocalSceneGeometry(3), Is.True);
                CollectionAssert.AreEqual(new[] { 10L }, fixture.Manager.Removed);
            }
        }

        // A replacement retires the old tree BEFORE it stages the new one: the two footprints are the same, and two siblings
        // may only touch. A failed removal therefore stages nothing, keeps the old registration, and the
        // next attempt retires it and registers the replacement.
        [Test]
        public void FailedOldRemoval_StagesNothingKeepsTheOldRegistrationAndRetries()
        {
            using (var fixture = new ManagerFixture())
            {
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Volume(4, Vector3.zero)), Is.True);
                fixture.Manager.FailRemoval = true;
                LogAssert.Expect(LogType.Error,
                    "[SpatialBlend][Geometry] Failed to remove owned local scene geometry 10 in space 10.");
                Assert.That(fixture.Manager.UpsertLocalSceneGeometry(Volume(4, Vector3.one)), Is.False);
                CollectionAssert.AreEqual(new[] { 10L }, fixture.Manager.Removed, "Only the old tree's removal is attempted; nothing was staged.");
                Assert.That(fixture.Manager.TryGetLocalSceneGeometryElements(4, out long keptParent, out _), Is.True, "the old registration is still there");
                Assert.That(keptParent, Is.EqualTo(10L));
                fixture.Manager.FailRemoval = false;
                Assert.That(fixture.Manager.UpsertLocalSceneGeometry(Volume(4, Vector3.one * 2f)), Is.True);
                Assert.That(fixture.Manager.Removed[1], Is.EqualTo(10L));
            }
        }

        [Test]
        public void InvalidDefault_MutatesNoNativeHooks()
        {
            using (var fixture = new ManagerFixture())
            {
                Assert.That(fixture.Manager.UpsertLocalSceneGeometry(default), Is.False);
                Assert.That(fixture.Manager.AddParents, Is.Empty);
                Assert.That(fixture.Manager.Transforms, Is.Empty);
            }
        }

        [Test]
        public void PlanePartialRetry_ReusesParentBeforeAllocatingMissingChild()
        {
            using (var fixture = new ManagerFixture())
            {
                fixture.Manager.FailAddAttempt = 2;
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Plane(6)), Is.False);
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Plane(6)), Is.True);
                CollectionAssert.AreEqual(new[] { 1L, 10L, 10L }, fixture.Manager.AddParents);
                Assert.That(fixture.Manager.Polygons, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void RemovingPartialAllocation_RetiresItsAllocatedParent()
        {
            using (var fixture = new ManagerFixture())
            {
                fixture.Manager.FailAddAttempt = 2;
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Plane(61)), Is.False);
                Assert.That(fixture.Manager.RemoveLocalSceneGeometry(61), Is.True);
                CollectionAssert.AreEqual(new[] { 10L }, fixture.Manager.Removed);
            }
        }

        [Test]
        public void NewGenerationSameKey_DoesNotRemoveOrReuseOldOwnedHandle()
        {
            using (var fixture = new ManagerFixture())
            {
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Volume(62, Vector3.zero)), Is.True);
                fixture.Manager.Configure(10, 2);
                Assert.That(fixture.Manager.RemoveLocalSceneGeometry(62), Is.True);
                Assert.That(fixture.Manager.Removed, Is.Empty);
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Volume(62, Vector3.one)), Is.True);
                CollectionAssert.AreEqual(new[] { 1L, 1L }, fixture.Manager.AddParents);
            }
        }

        [Test]
        public void ReentrantGenerationChange_NeverUsesReplacementSpace()
        {
            using (var fixture = new ManagerFixture())
            {
                fixture.Manager.OnAdd = () => fixture.Manager.Configure(99, 2);
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(Volume(7, Vector3.zero)), Is.False);
                CollectionAssert.AreEqual(new[] { 10L }, fixture.Manager.AddSpaces);
                Assert.That(fixture.Manager.RemoveSpaces, Is.Empty,
                    "A retired generation must not receive native rollback calls.");
                Assert.That(fixture.Manager.Transforms, Is.Empty);
            }
        }

        [Test]
        public void Watcher_CreateUpdateRemove_UsesMutableSinkAndNoLabels()
        {
            var roomGo = new GameObject("room"); roomGo.SetActive(false);
            var anchorGo = new GameObject("anchor"); anchorGo.SetActive(false);
            var room = roomGo.AddComponent<MRUKRoom>();
            var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.COUCH);
                var sink = new RecordingMutableSink(); var watcher = new MRUKSceneGeometryWatcher();
                watcher.Bind(room, sink);
                room.AnchorCreatedEvent.Invoke(anchor);
                room.AnchorUpdatedEvent.Invoke(anchor);
                room.AnchorRemovedEvent.Invoke(anchor);
                Assert.That(sink.Upserts, Has.Count.EqualTo(2));
                Assert.That(sink.Removed, Is.EqualTo(new[] { MRUKSceneGeometryAdapter.StableKey(anchor) }));
                watcher.Dispose();
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(roomGo); }
        }

        [Test]
        public void Watcher_RoomReplacementAndDispose_UnsubscribeOldRooms()
        {
            var aGo = new GameObject("room-a"); var bGo = new GameObject("room-b");
            var anchorGo = new GameObject("anchor");
            aGo.SetActive(false); bGo.SetActive(false); anchorGo.SetActive(false);
            var a = aGo.AddComponent<MRUKRoom>(); var b = bGo.AddComponent<MRUKRoom>();
            var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                var sink = new RecordingMutableSink(); var watcher = new MRUKSceneGeometryWatcher();
                Assert.That(watcher.Bind(a, sink), Is.True);
                a.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(sink.Upserts, Has.Count.EqualTo(1));
                Assert.That(watcher.Bind(b, sink), Is.True);
                CollectionAssert.AreEqual(new[] { MRUKSceneGeometryAdapter.StableKey(anchor) }, sink.Removed);
                int afterOldRoom = sink.Upserts.Count;
                a.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(sink.Upserts, Has.Count.EqualTo(afterOldRoom));
                b.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(sink.Upserts, Has.Count.EqualTo(afterOldRoom + 1));
                Assert.That(watcher.Dispose(), Is.True); b.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(sink.Removed, Has.Count.EqualTo(2));
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(bGo); UnityEngine.Object.DestroyImmediate(aGo); }
        }

        [Test]
        public void Watcher_StaleGenerationPredicateSuppressesSameKeyCallbacks()
        {
            var roomGo = new GameObject("room-stale"); var anchorGo = new GameObject("anchor-stale");
            roomGo.SetActive(false); anchorGo.SetActive(false);
            var room = roomGo.AddComponent<MRUKRoom>(); var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                bool live = true; var sink = new RecordingMutableSink(); var watcher = new MRUKSceneGeometryWatcher();
                watcher.Bind(room, sink, () => live);
                room.AnchorCreatedEvent.Invoke(anchor);
                live = false; room.AnchorRemovedEvent.Invoke(anchor);
                Assert.That(sink.Upserts, Has.Count.EqualTo(1));
                Assert.That(sink.Removed, Is.Empty);
                watcher.Dispose();
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(roomGo); }
        }

        [Test]
        public void Watcher_FailedRetirementRetainsKeysUntilRetry()
        {
            var roomGo = new GameObject("room-fail"); var anchorGo = new GameObject("anchor-fail");
            roomGo.SetActive(false); anchorGo.SetActive(false);
            var room = roomGo.AddComponent<MRUKRoom>(); var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                var sink = new RecordingMutableSink { FailRemove = true };
                var watcher = new MRUKSceneGeometryWatcher(); watcher.Bind(room, sink);
                room.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(watcher.Dispose(), Is.False);
                sink.FailRemove = false;
                Assert.That(watcher.Dispose(), Is.True);
                CollectionAssert.AreEqual(new[] { MRUKSceneGeometryAdapter.StableKey(anchor), MRUKSceneGeometryAdapter.StableKey(anchor) }, sink.Removed);
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(roomGo); }
        }

        [Test]
        public void Watcher_DefaultBindIsIdempotent()
        {
            var roomGo = new GameObject("room-idempotent"); var anchorGo = new GameObject("anchor-idempotent");
            roomGo.SetActive(false); anchorGo.SetActive(false);
            var room = roomGo.AddComponent<MRUKRoom>(); var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                var sink = new RecordingMutableSink(); var watcher = new MRUKSceneGeometryWatcher();
                Assert.That(watcher.Bind(room, sink), Is.True);
                room.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(watcher.Bind(room, sink), Is.True);
                Assert.That(sink.Removed, Is.Empty, "An omitted predicate is the same default binding.");
                room.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(sink.Upserts, Has.Count.EqualTo(2), "The original listener remains exactly once.");
                Assert.That(watcher.Dispose(), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(roomGo); }
        }

        [Test]
        public void Watcher_StaleRoomListenerCannotMutateReplacementBinding()
        {
            var aGo = new GameObject("room-listener-a"); var bGo = new GameObject("room-listener-b");
            var anchorGo = new GameObject("anchor-listener");
            aGo.SetActive(false); bGo.SetActive(false); anchorGo.SetActive(false);
            var a = aGo.AddComponent<MRUKRoom>(); var b = bGo.AddComponent<MRUKRoom>();
            var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                var aSink = new RecordingMutableSink(); var bSink = new RecordingMutableSink();
                var watcher = new MRUKSceneGeometryWatcher();
                a.AnchorCreatedEvent.AddListener(_ => Assert.That(watcher.Bind(b, bSink), Is.True));
                Assert.That(watcher.Bind(a, aSink), Is.True);
                // The external listener executes first; the now-stale A callback
                // must not add this anchor to B's owned-key set or sink.
                a.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(aSink.Upserts, Is.Empty);
                Assert.That(bSink.Upserts, Is.Empty);
                b.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(bSink.Upserts, Has.Count.EqualTo(1));
                Assert.That(watcher.Dispose(), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(bGo); UnityEngine.Object.DestroyImmediate(aGo); }
        }

        [Test]
        public void Watcher_ReentrantSinkAndPredicateCannotReplaceLiveBinding()
        {
            var aGo = new GameObject("room-reentrant-a"); var bGo = new GameObject("room-reentrant-b");
            var anchorGo = new GameObject("anchor-reentrant");
            aGo.SetActive(false); bGo.SetActive(false); anchorGo.SetActive(false);
            var a = aGo.AddComponent<MRUKRoom>(); var b = bGo.AddComponent<MRUKRoom>();
            var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                var aSink = new RecordingMutableSink(); var bSink = new RecordingMutableSink();
                var watcher = new MRUKSceneGeometryWatcher();
                bool predicateAttempted = false;
                Assert.That(watcher.Bind(a, aSink, () =>
                {
                    predicateAttempted = true;
                    Assert.That(watcher.Dispose(), Is.False);
                    return true;
                }), Is.True);
                aSink.OnUpsert = _ =>
                {
                    Assert.That(watcher.Bind(b, bSink), Is.False);
                    Assert.That(watcher.Dispose(), Is.False);
                };
                a.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(predicateAttempted, Is.True);
                Assert.That(aSink.Upserts, Has.Count.EqualTo(1));
                Assert.That(watcher.Bind(b, bSink), Is.True, "Replacement succeeds after the callback unwinds.");
                CollectionAssert.AreEqual(new[] { MRUKSceneGeometryAdapter.StableKey(anchor) }, aSink.Removed);
                Assert.That(watcher.Dispose(), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(bGo); UnityEngine.Object.DestroyImmediate(aGo); }
        }

        [Test]
        public void Watcher_FailedAnchorRemovalCanRetryAndFreshUpsertCancelsPendingKey()
        {
            var roomGo = new GameObject("room-pending"); var anchorGo = new GameObject("anchor-pending");
            roomGo.SetActive(false); anchorGo.SetActive(false);
            var room = roomGo.AddComponent<MRUKRoom>(); var anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureAnchor(anchor, MRUKAnchor.SceneLabels.OTHER);
                var sink = new RecordingMutableSink(); var watcher = new MRUKSceneGeometryWatcher();
                Assert.That(watcher.Bind(room, sink), Is.True);
                room.AnchorCreatedEvent.Invoke(anchor);
                sink.FailRemove = true;
                room.AnchorRemovedEvent.Invoke(anchor);
                Assert.That(watcher.RetryPendingRemovals(), Is.False);
                Assert.That(sink.Removed, Has.Count.EqualTo(2));

                sink.FailRemove = false;
                room.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(watcher.RetryPendingRemovals(), Is.True,
                    "A successful same-key upsert owns the fresh anchor and cancels stale retirement.");
                Assert.That(sink.Removed, Has.Count.EqualTo(2));
                Assert.That(watcher.Dispose(), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(anchorGo); UnityEngine.Object.DestroyImmediate(roomGo); }
        }

        // An MRUK plane anchor has its normal along local Z (its own frame is tilted: Rx(-90) turns the local XY
        // outline horizontal). The core is planar, so what it receives must be a polygon that lies in the floor
        // frame: a parent placed by position and yaw only, the volume box and the outline expressed in that
        // frame. Proved by taking the received data back to the Unity world and comparing with the anchor's own.
        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(-110f)]
        public void MrukTableAnchor_ReachesTheCoreAsAHorizontalPolygonInTheFloorFrame(float yawDegrees)
        {
            Matrix4x4 anchor = Matrix4x4.TRS(new Vector3(2f, 0.74f, 3f),
                Quaternion.Euler(0f, yawDegrees, 0f) * Quaternion.Euler(-90f, 0f, 0f), Vector3.one);
            // The measured outline, asymmetric about the anchor origin (local XY); the volume hangs below the top plane.
            var outline = new[] { new Vector2(-0.5f, -0.3f), new Vector2(0.7f, -0.3f), new Vector2(0.7f, 0.5f),
                                  new Vector2(-0.5f, 0.5f) };
            var volume = new Bounds(new Vector3(0.1f, 0.1f, -0.37f), new Vector3(1.2f, 0.8f, 0.74f));
            Assert.That(SceneGeometryDescriptor.TryCreate(7, anchor, volume, outline, null, out var descriptor), Is.True);
            using (var fixture = new ManagerFixture())
            {
                Assert.That(fixture.Manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                var m = fixture.Manager;
                Assert.That(m.Transforms, Has.Count.EqualTo(1), "one transform: the element's own; the plane child has none");
                Matrix4x4 floor = m.Transforms[0];
                // The parent's frame only turns about the vertical axis.
                Assert.That(Vector3.Distance(floor.MultiplyVector(Vector3.up), Vector3.up), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(floor.GetColumn(3), anchor.GetColumn(3)), Is.LessThan(1e-5f));
                // The footprint of the volume is its extent in that frame: width x depth, height 0.74.
                Bounds box = m.BoxBounds[0];
                Assert.That(box.size.y, Is.EqualTo(0.74f).Within(1e-4f));
                Assert.That(box.size.x, Is.EqualTo(1.2f).Within(1e-4f));
                Assert.That(box.size.z, Is.EqualTo(0.8f).Within(1e-4f));
                // The polygon core receives is (x, z) of the frame, and lies at the plane's height in it: every vertex,
                // taken back through the parent's frame (and the plane child's height), is where the anchor put it.
                Vector2[] received = m.PolygonPoints[0];
                Assert.That(received.Length, Is.EqualTo(outline.Length));
                for (int i = 0; i < outline.Length; ++i)
                {
                    Vector3 expected = anchor.MultiplyPoint3x4(new Vector3(outline[i].x, outline[i].y, 0f));
                    Vector2 local = received[outline.Length - 1 - i]; // the registered polygon is reversed, y negated
                    Vector3 back = floor.MultiplyPoint3x4(new Vector3(local.x, 0f, local.y));
                    Assert.That(Vector3.Distance(back, expected), Is.LessThan(1e-4f), "vertex " + i);
                }
                // The volume's corners likewise land on the anchor's own corners' world extent.
                Bounds worldVolume = default;
                for (int i = 0; i < 8; ++i)
                {
                    Vector3 corner = anchor.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? volume.min.x : volume.max.x,
                        (i & 2) == 0 ? volume.min.y : volume.max.y, (i & 4) == 0 ? volume.min.z : volume.max.z));
                    if (i == 0) worldVolume = new Bounds(corner, Vector3.zero); else worldVolume.Encapsulate(corner);
                }
                Assert.That(worldVolume.size.y, Is.EqualTo(box.size.y).Within(1e-4f), "same height");
                Assert.That(Vector3.Distance(floor.MultiplyPoint3x4(box.center), anchor.MultiplyPoint3x4(volume.center)),
                    Is.LessThan(1e-4f), "same centre");
            }
        }

        private static SceneGeometryDescriptor Volume(int key, Vector3 position)
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(key, Matrix4x4.Translate(position),
                new Bounds(Vector3.zero, Vector3.one), null, null, out var descriptor), Is.True);
            return descriptor;
        }
        private static SceneGeometryDescriptor Plane(int key)
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(key, Matrix4x4.identity, null, new[]
            { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(0, 1) }, null, out var descriptor), Is.True);
            return descriptor;
        }
        private static void ConfigureAnchor(MRUKAnchor anchor, MRUKAnchor.SceneLabels label)
        {
            Set(anchor, "Label", label); Set(anchor, "VolumeBounds", (Bounds?)new Bounds(Vector3.zero, Vector3.one));
            // MRUK puts every anchor in a room before it raises its events; a UUID-less anchor is not staged before that.
            Set(anchor, "Room", AnchorRoom);
        }

        private static MRUKRoom s_anchorRoom;
        private static MRUKRoom AnchorRoom
        {
            get
            {
                if (s_anchorRoom == null)
                {
                    var go = new GameObject("anchor-room");
                    go.SetActive(false);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    s_anchorRoom = go.AddComponent<MRUKRoom>();
                }
                return s_anchorRoom;
            }
        }
        private static void Set(MRUKAnchor anchor, string name, object value) => typeof(MRUKAnchor).GetProperty(name, BindingFlags.Instance | BindingFlags.Public).SetValue(anchor, value);

        private sealed class ManagerFixture : IDisposable
        {
            public readonly GameObject Go = new GameObject("dynamic-geometry-manager");
            public readonly RecordingManager Manager;
            public ManagerFixture() { Manager = Go.AddComponent<RecordingManager>(); Manager.Configure(10, 1); }
            public void Dispose() => UnityEngine.Object.DestroyImmediate(Go);
        }
        private sealed class RecordingManager : SpatialBlendManager
        {
            public readonly List<long> AddParents = new List<long>(); public readonly List<long> AddSpaces = new List<long>();
            public readonly List<long> Removed = new List<long>(); public readonly List<long> RemoveSpaces = new List<long>();
            public readonly List<Matrix4x4> Transforms = new List<Matrix4x4>(); public readonly List<int> Boxes = new List<int>(); public readonly List<int> Polygons = new List<int>();
            public int FailAddAttempt = -1; public bool FailRemoval; public Action OnAdd; private int _attempt; private long _next = 10;
            protected override void InitializeNativeLibrary() { } public override void Host(int port) { }
            public void Configure(long space, long generation) { Field("_initialized").SetValue(this, true); Field("_connectionState").SetValue(this, SpatialBlendConnectionState.Connected); Field("_spaceID").SetValue(this, space); Field("_lifecycleGeneration").SetValue(this, generation); }
            protected override long EnsureLocalSceneGeometryRoom(long space) => 1;
            protected override long AddLocalSceneGeometryElement(long space, long parent) { AddSpaces.Add(space); AddParents.Add(parent); ++_attempt; OnAdd?.Invoke(); OnAdd = null; return _attempt == FailAddAttempt ? -1 : _next++; }
            protected override void SetLocalSceneGeometryTransform(long space, long e, Matrix4x4 t) { Transforms.Add(t); TransformElements.Add(e); }
            public readonly List<long> TransformElements = new List<long>(); public readonly List<Bounds> BoxBounds = new List<Bounds>(); public readonly List<Vector2[]> PolygonPoints = new List<Vector2[]>();
            protected override void SetLocalSceneGeometryBox(long space, long e, int r, Vector3 min, Vector3 max) { Boxes.Add(r); var b = new Bounds(); b.SetMinMax(min, max); BoxBounds.Add(b); }
            protected override void SetLocalSceneGeometryPolygon(long space, long e, int r, IReadOnlyList<Vector2> p) { Polygons.Add(r); PolygonPoints.Add(new List<Vector2>(p).ToArray()); }
            protected override bool RemoveLocalSceneGeometryElement(long space, long e) { RemoveSpaces.Add(space); Removed.Add(e); return !FailRemoval; }
            private static FieldInfo Field(string name) => typeof(SpatialBlendManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        }
        private sealed class RecordingMutableSink : IMutableLocalSceneGeometrySink
        {
            public readonly List<SceneGeometryDescriptor> Upserts = new List<SceneGeometryDescriptor>(); public readonly List<int> Removed = new List<int>();
            public bool FailRemove;
            public Action<SceneGeometryDescriptor> OnUpsert;
            public Action<int> OnRemove;
            public bool RegisterLocalSceneGeometry(SceneGeometryDescriptor d) { Upserts.Add(d); return true; }
            public bool UpsertLocalSceneGeometry(SceneGeometryDescriptor d) { Upserts.Add(d); OnUpsert?.Invoke(d); return true; }
            public bool RemoveLocalSceneGeometry(int id) { Removed.Add(id); OnRemove?.Invoke(id); return !FailRemove; }
        }
    }
}
#endif
