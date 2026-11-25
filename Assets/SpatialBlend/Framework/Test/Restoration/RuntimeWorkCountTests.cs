// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// On Quest 3, repeated work after connecting (the NavMesh apparently computed several times) made the shared
    /// TABLE's red box stutter. A simulated connect with anchor shares counts the expensive work (RuntimeWorkCounters):
    /// each NavMesh bake, anchor outline, room registration and furniture staging happens at most once per actual change.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RuntimeWorkCountTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [SetUp]
        public void ResetCounters() => RuntimeWorkCounters.Reset();

        [TearDown]
        public void Destroy()
        {
            for (int i = _objects.Count - 1; i >= 0; --i) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        private T Track<T>(T o) where T : Object { _objects.Add(o); return o; }

        // ------------------------------------------------------------------ the NavMesh

        [Test]
        public void TheBakerTakesSceneNavigationsOwnTriggersOver()
        {
            var go = Track(new GameObject("RoomNavMesh"));
            var navigation = go.AddComponent<SceneNavigation>();
            navigation.BuildOnSceneLoaded = MRUK.RoomFilter.AllRooms;
            SceneNavMeshBaker baker = SceneNavMeshBaker.Ensure(navigation);

            Assert.That(baker, Is.Not.Null);
            Assert.That(SceneNavMeshBaker.Ensure(navigation), Is.SameAs(baker), "one baker per SceneNavigation");
            Assert.That(baker.Filter, Is.EqualTo(MRUK.RoomFilter.AllRooms), "the rooms the scene asked for are kept");
            Assert.That(navigation.BuildOnSceneLoaded, Is.EqualTo(MRUK.RoomFilter.None), "SceneNavigation no longer bakes on a scene load");
            FieldInfo track = typeof(SceneNavigation).GetField("TrackUpdates", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(track, Is.Not.Null, "MRUK's TrackUpdates switch exists");
            Assert.That((bool)track.GetValue(navigation), Is.False, "nor on every room created, updated or removed");
        }

        [Test]
        public void ASimulatedConnectWithAnchorSharesBakesTheNavMeshOncePerActualChange()
        {
            var go = Track(new GameObject("RoomNavMesh"));
            var navigation = go.AddComponent<SceneNavigation>();
            navigation.BuildOnSceneLoaded = MRUK.RoomFilter.AllRooms;
            SceneNavMeshBaker baker = SceneNavMeshBaker.Ensure(navigation);
            long geometry = 1;
            int bakes = 0;
            baker.FingerprintSource = () => geometry;
            baker.BakeAction = () => ++bakes;

            // MRUK loads a device that keeps three scanned rooms: three rooms created, then the scene loaded (one frame each).
            for (int room = 0; room < 3; ++room) baker.Request("room created");
            baker.Flush();
            baker.Request("scene loaded");
            baker.Flush();
            // The connect and the anchor shares: the room is registered, the TABLE shared and linked; MRUK reports "significant"
            // room updates that change nothing a bake reads, over several frames.
            for (int frame = 0; frame < 5; ++frame)
            {
                baker.Request("room updated");
                baker.Flush();
            }
            int afterConnect = bakes;
            // A real change (an anchor re-measured): exactly one more bake.
            geometry = 2;
            baker.Request("room updated");
            baker.Request("room updated");
            baker.Flush();
            for (int frame = 0; frame < 30; ++frame) baker.Flush();   // idle frames: nothing pending, nothing baked

            // SceneNavigation alone baked once per trigger: 3 + 1 + 5 + 2 = 11.
            Assert.That(RuntimeWorkCounters.NavMeshBakeRequests, Is.EqualTo(11), "the triggers SceneNavigation used to bake on");
            Assert.That(afterConnect, Is.EqualTo(1), "one bake for the load and the connect: " + RuntimeWorkCounters.Summary());
            Assert.That(bakes, Is.EqualTo(2), "and one per actual change: " + RuntimeWorkCounters.Summary());
            Assert.That(RuntimeWorkCounters.NavMeshBakes, Is.EqualTo(2));

            Assert.That(baker.BakeNow("Rebuild NavMesh button"), Is.True, "the Rebuild button always bakes");
            Assert.That(bakes, Is.EqualTo(3));
        }

        [Test]
        public void TheRoomFingerprintChangesWithAnAnchorsPoseOrBoxOnly()
        {
            MRUKRoom room = NewRoom("fingerprint-room", out MRUKAnchor table);
            long a = SceneNavMeshBaker.Fingerprint(room, 17);
            Assert.That(SceneNavMeshBaker.Fingerprint(room, 17), Is.EqualTo(a), "the same room reads the same");
            table.transform.position += new Vector3(0.0002f, 0f, 0f);
            Assert.That(SceneNavMeshBaker.Fingerprint(room, 17), Is.EqualTo(a), "below a millimetre is no change");
            table.transform.position += new Vector3(0.05f, 0f, 0f);
            long moved = SceneNavMeshBaker.Fingerprint(room, 17);
            Assert.That(moved, Is.Not.EqualTo(a), "a moved anchor is a change");
            Set(table, "VolumeBounds", (Bounds?)new Bounds(Vector3.zero, new Vector3(1.5f, 0.8f, 0.7f)));
            Assert.That(SceneNavMeshBaker.Fingerprint(room, 17), Is.Not.EqualTo(moved), "a re-measured box is a change");
        }

        // ------------------------------------------------------------------ the shared anchor's red box

        [Test]
        public void AnchorSharesBuildEachOutlineOnceWithOneSharedMaterialAndNoPerFrameWork()
        {
            // Host: the TABLE is shared (and the furniture policy shares it again on every registration call: idempotent).
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord sentTable = AddAnchor(senderCatalog, "TABLE", "host-table", new Vector3(5f, 0.75f, 5f), new Vector3(1.2f, 0.7f, 0.75f));
            FreeformCollaborationApp host = NewApp(senderCatalog);
            for (int call = 0; call < 4; ++call) Assert.That(host.TryShareAnchor(sentTable), Is.True);
            Assert.That(RuntimeWorkCounters.AnchorOutlineBuilds, Is.EqualTo(1), "the host's TABLE wire box, once");

            // Joiner with no TABLE: the shared one arrives as a box with its pending outline.
            FreeformCollaborationApp joiner = NewApp(new ListSceneAnchorCatalog());
            var replica = Track(new GameObject(sentTable.Anchor.name));
            var entity = replica.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel("TABLE");
            entity.AdoptWireName(replica.name, "TABLE");
            LogAssert.ignoreFailingMessages = true;
            try
            {
                joiner.OnShare(replica);
                joiner.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            }
            finally { LogAssert.ignoreFailingMessages = false; }
            SharedRoomObjectBox box = joiner.SharedBoxes.Single();
            int afterShares = RuntimeWorkCounters.AnchorOutlineBuilds;
            Assert.That(afterShares, Is.EqualTo(3), "host wires, joiner box wires, joiner pending outline: " + RuntimeWorkCounters.Summary());

            // Frames go by with the box placed (the per-frame pose check runs): nothing is rebuilt, nothing is allocated.
            box.Place();
            box.Tick();
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 300; ++frame) box.Tick();
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(box.IsPlaced, Is.True);
            Assert.That(RuntimeWorkCounters.AnchorOutlineBuilds, Is.EqualTo(afterShares), "no rebuild while nothing changes");
            Assert.That(allocated, Is.EqualTo(0), "no per-frame allocation");
            Assert.That(typeof(SbMRUKAnchorEntity).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                Is.Null, "the anchor entity has no per-frame presentation pass");

            // A change: the placed box moved again: the outline comes back once.
            replica.transform.position += Vector3.right * 0.3f;
            for (int frame = 0; frame < 10; ++frame) box.Tick();
            Assert.That(RuntimeWorkCounters.AnchorOutlineBuilds, Is.EqualTo(afterShares + 1), "one rebuild for one change");
            Assert.That(replica.GetComponentsInChildren<LineRenderer>(true).All(l => !l.enabled), Is.True, "the receiver's box stays hidden");

            // Every line of every outline draws with the one shared material.
            Assert.That(RuntimeWorkCounters.OutlineMaterialsCreated, Is.LessThanOrEqualTo(1), RuntimeWorkCounters.Summary());
            var materials = sentTable.Anchor.GetComponentsInChildren<LineRenderer>(true)
                .Concat(replica.GetComponentsInChildren<LineRenderer>(true)).Select(l => l.sharedMaterial).Distinct().ToList();
            Assert.That(materials, Has.Count.EqualTo(1));
            Assert.That(materials[0], Is.SameAs(SceneAnchorHighlight.LineMaterial));
        }

        // ------------------------------------------------------------------ the room and its furniture

        [Test]
        public void RepeatedRegistrationCallsBindTheFurnitureOnce()
        {
            MRUKRoom room = NewRoom("registration-room", out _);
            var managerGo = Track(new GameObject("manager"));
            managerGo.SetActive(false);
            var manager = managerGo.AddComponent<GeometryManager>();
            manager.Configure(10, 1);
            FreeformCollaborationApp app = NewApp(new ListSceneAnchorCatalog());
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);
            typeof(FreeformCollaborationApp).GetField("_appStarted", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, true);
            app.RoomProvider = () => room;
            MethodInfo register = typeof(FreeformCollaborationApp).GetMethod("TryRegisterCurrentRoomGeometry", BindingFlags.Instance | BindingFlags.NonPublic);

            // OnEnable, OnAppStart, Connected, the MRUK scene-loaded catch-up and a scope tick: the same room, the same connection.
            // This room has no floor outline, so its region is refused every time (the case that used to rebind every call).
            LogAssert.ignoreFailingMessages = true;
            try
            {
                for (int call = 0; call < 5; ++call) register.Invoke(app, null);
            }
            finally { LogAssert.ignoreFailingMessages = false; }

            Assert.That(RuntimeWorkCounters.SceneGeometryBinds, Is.EqualTo(1), RuntimeWorkCounters.Summary());
            Assert.That(manager.Removed, Is.EqualTo(0), "no furniture box retired and restaged");
            Assert.That(RuntimeWorkCounters.SceneGeometryStages, Is.EqualTo(1), "the TABLE staged once: " + RuntimeWorkCounters.Summary());
        }

        [Test]
        public void AnUnchangedFurnitureUpdateIsNotRestaged()
        {
            MRUKRoom room = NewRoom("update-room", out MRUKAnchor table);
            var managerGo = Track(new GameObject("manager"));
            managerGo.SetActive(false);
            var manager = managerGo.AddComponent<GeometryManager>();
            manager.Configure(10, 1);
            var watcher = new MRUKSceneGeometryWatcher();
            Assert.That(watcher.Bind(room, manager), Is.True);
            int adds = manager.Adds;
            Assert.That(RuntimeWorkCounters.SceneGeometryStages, Is.EqualTo(1));

            // MRUK raises "significant" updates for an anchor whose pose and box did not change (a re-localisation pass).
            for (int update = 0; update < 10; ++update) room.AnchorUpdatedEvent.Invoke(table);
            Assert.That(RuntimeWorkCounters.SceneGeometryStages, Is.EqualTo(1), RuntimeWorkCounters.Summary());
            Assert.That(RuntimeWorkCounters.SceneGeometryUnchanged, Is.EqualTo(10));
            Assert.That(manager.Adds, Is.EqualTo(adds), "no element added again");
            Assert.That(manager.Removed, Is.EqualTo(0), "no element removed");

            // A real move is staged.
            table.transform.position += new Vector3(0.4f, 0f, 0f);
            room.AnchorUpdatedEvent.Invoke(table);
            Assert.That(RuntimeWorkCounters.SceneGeometryStages, Is.EqualTo(2), RuntimeWorkCounters.Summary());
            Assert.That(watcher.Dispose(), Is.True);
        }

        // ------------------------------------------------------------------ helpers

        private FreeformCollaborationApp NewApp(ListSceneAnchorCatalog catalog)
        {
            var go = Track(new GameObject("app"));
            var app = go.AddComponent<FreeformCollaborationApp>();
            app.UseSceneAnchorCatalog(catalog);
            app.UseSceneAnchorPublisher(new NullPublisher());
            app.UseSeatPreferences(new MemorySeatPreferences());
            app.UserViewProvider = () => (new Vector3(0f, 1.6f, 0f), new Vector3(0f, 0f, 1f));
            app.FloorHeightProvider = () => 0f;
            return app;
        }

        private sealed class NullPublisher : ISceneAnchorPublisher
        {
            public void Publish(GameObject anchor, bool shareWithPeers) { }
        }

        private SceneAnchorRecord AddAnchor(ListSceneAnchorCatalog catalog, string label, string id, Vector3 position, Vector3 size)
        {
            var go = Track(new GameObject(label + "-" + id));
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(-90f, 0f, 0f));
            var record = new SceneAnchorRecord
            {
                Label = label, Anchor = go, AnchorId = id, SourceKey = go.GetInstanceID(), Size = size.x * size.y * size.z,
                LocalCenter = new Vector3(0f, 0f, -size.z * 0.5f), LocalSize = size,
            };
            catalog.Add(record);
            return record;
        }

        private MRUKRoom NewRoom(string name, out MRUKAnchor table)
        {
            var roomGo = Track(new GameObject(name));
            roomGo.SetActive(false);
            var room = roomGo.AddComponent<MRUKRoom>();
            var anchorGo = Track(new GameObject(name + "-table"));
            anchorGo.SetActive(false);
            anchorGo.transform.SetPositionAndRotation(new Vector3(1f, 0.75f, 1f), Quaternion.Euler(-90f, 0f, 0f));
            table = anchorGo.AddComponent<MRUKAnchor>();
            Set(table, "Label", MRUKAnchor.SceneLabels.TABLE);
            Set(table, "VolumeBounds", (Bounds?)new Bounds(new Vector3(0f, 0f, -0.35f), new Vector3(1.2f, 0.8f, 0.7f)));
            Set(table, "Room", room);
            room.Anchors.Add(table);
            return room;
        }

        private static void Set(MRUKAnchor anchor, string name, object value) =>
            typeof(MRUKAnchor).GetProperty(name, BindingFlags.Instance | BindingFlags.Public).SetValue(anchor, value);

        private sealed class GeometryManager : SpatialBlendManager
        {
            public int Adds;
            public int Removed;
            private long _next = 10;
            protected override void InitializeNativeLibrary() { }
            public override void Host(int port) { }
            public void Configure(long space, long generation)
            {
                Field("_initialized").SetValue(this, true);
                Field("_connectionState").SetValue(this, SpatialBlendConnectionState.Connected);
                Field("_spaceID").SetValue(this, space);
                Field("_lifecycleGeneration").SetValue(this, generation);
            }
            protected override long EnsureLocalSceneGeometryRoom(long space) => 1;
            protected override long AddLocalSceneGeometryElement(long space, long parent) { ++Adds; return _next++; }
            protected override void SetLocalSceneGeometryTransform(long space, long e, Matrix4x4 t) { }
            protected override void SetLocalSceneGeometryBox(long space, long e, int r, Vector3 min, Vector3 max) { }
            protected override void SetLocalSceneGeometryPolygon(long space, long e, int r, IReadOnlyList<Vector2> p) { }
            protected override bool RemoveLocalSceneGeometryElement(long space, long e) { ++Removed; return true; }
            private static FieldInfo Field(string name) => typeof(SpatialBlendManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }
}
#endif
