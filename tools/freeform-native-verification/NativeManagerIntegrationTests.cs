// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Freeform;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Tests
{
    // Only copied into the explicitly provisioned full-project native gate.
    // Managers, wrapper, native shared library and gRPC host/connect are REAL.
    // No Fusion runner, microphone, tracking device, or mocked native hooks.
    [TestFixture]
    [Category("NativeManagerIntegration")]
    public partial class NativeManagerIntegrationTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private IDisposable _worldAwakeLease;

        // ABI 4: there is no tracking-gap dwell. An anchor missing from a
        // committed frame withdraws its relations at once, like explicit deactivation; there is no hold on the omission
        // frame and no release time. This is only the clock step between consecutive frames of the fixtures below.
        private const double NextFrameSeconds = 0.5d;

        [SetUp]
        public void RequireExplicitReleaseArtifact()
        {
            _worldAwakeLease = InstallLocalWorldAwakeForEditMode();
            // The gate provisions the selected RELEASE dylib, not a debug
            // binary. Direct independent query imports must observe that same
            // native registry; do not silently query a different library.
            Assert.That(SpatialBlendManager.CurrentSettings == null ||
                !SpatialBlendManager.CurrentSettings.useDebugLibraries, Is.True,
                "This gate requires release native settings for its selected artifact.");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
            // Explicitly retire inactive EditMode managers; Unity does not promise
            // OnDestroy for GameObjects that have never been active.
            foreach (GameObject item in _objects)
                if (item != null && item.TryGetComponent(out SpatialBlendManager manager)) manager.Disconnect();
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            }
            finally
            {
                _worldAwakeLease?.Dispose();
                _worldAwakeLease = null;
            }
        }

        // The production backend still creates the real local world and executes
        // the full native startup path. This scoped Editor-only seam supplies
        // Unity's omitted EditMode Awake; no thread guard/session is substituted.
        internal static IDisposable InstallLocalWorldAwakeForEditMode() => new LocalWorldAwakeLease();

        private sealed class LocalWorldAwakeLease : IDisposable
        {
            private readonly FieldInfo _field = typeof(FreeformSceneLocalBackend).GetField(
                "TestOnlyAfterLocalWorldCreated", BindingFlags.Static | BindingFlags.NonPublic);
            private readonly Action<SpatialBlendLocalWorld> _callback;
            private bool _disposed;

            private readonly FieldInfo _floorField = typeof(FreeformSceneLocalBackend).GetField(
                "TestOnlyFloorSource", BindingFlags.Static | BindingFlags.NonPublic);

            // The measurement a device would provide (EditMode has no MRUK room and no runtime NavMesh): a measured
            // square floor at the ground, large enough to contain every authored actor root of the original scene.
            internal static bool MeasuredFloor(out WalkableRegion region, out float height)
            {
                region = new WalkableRegion(new[]
                {
                    new Vector2(-200f, -200f), new Vector2(200f, -200f), new Vector2(200f, 200f), new Vector2(-200f, 200f)
                }, null);
                height = 0f;
                return true;
            }

            public LocalWorldAwakeLease()
            {
                Assert.That(_floorField, Is.Not.Null);
                _floorField.SetValue(null, Delegate.CreateDelegate(_floorField.FieldType,
                    typeof(LocalWorldAwakeLease).GetMethod(nameof(MeasuredFloor),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                Assert.That(_field, Is.Not.Null);
                Assert.That(_field.GetValue(null), Is.Null, "A test must not replace another lifecycle hook owner.");
                Assert.That(SpatialBlendLocalWorld.TestOnlySceneGeometryNativeOperations, Is.Null,
                    "The native acceptance gate must not use a fake geometry backend.");
                _callback = world => InvokePrivate(world, "Awake");
                _field.SetValue(null, _callback);
            }

            public void Dispose()
            {
                if (_disposed) return;
                Assert.That(_field.GetValue(null), Is.SameAs(_callback));
                _field.SetValue(null, null);
                _floorField.SetValue(null, null);
                _disposed = true;
            }
        }

        [Test]
        public void RealManagersConnectDisconnectAndReconnectWithoutPhotonRunner()
        {
            var host = CreateManager("native-host");
            var client = CreateManager("native-client");
            int port = FreePort();
            host.Host(port);
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);
            long oldClientGeneration = client.ConnectionGeneration;

            client.Disconnect();
            Assert.That(client.Initialized, Is.False);
            Assert.That(client.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Disconnected));
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);
            Assert.That(client.ConnectionGeneration, Is.GreaterThan(oldClientGeneration));

            client.Disconnect();
            Assert.That(host.RegisterLocalSpatialHierarchy(SeatDescriptor(), out var registration), Is.True);
            Assert.That(registration.IsLive, Is.True, "Client cleanup must not destroy the independent host session.");
        }

        [Test]
        public void WalkableFourByFourWithTwoByTwoHoleRegistersOneObstacleChildAndReregisterRemovesIt()
        {
            var manager = CreateManager("native-walkable-hole");
            manager.Host(FreePort());
            AssertConnected(manager);

            // 4x4 outer ring, 2x2 hole from (1,1) to (3,3). The real manager
            // adds that hole as one obstacle child of the room.
            Vector3[] holed =
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f),
                new Vector3(1f, 0f, 1f), new Vector3(3f, 0f, 1f),
                new Vector3(3f, 0f, 3f), new Vector3(1f, 0f, 3f)
            };
            Assert.That(manager.RegisterWalkableMesh(holed, new[]
            {
                0, 1, 5, 0, 5, 4,
                1, 2, 6, 1, 6, 5,
                2, 3, 7, 2, 7, 6,
                3, 0, 4, 3, 4, 7
            }), Is.True);

            var holes = (IList)Field(manager, "_walkableHoleElementIDs");
            Assert.That(holes.Count, Is.EqualTo(1));
            long hole = Convert.ToInt64(holes[0]);
            long room = (long)Field(manager, "_roomElementID");
            Assert.That(hole, Is.GreaterThan(0));
            Assert.That(hole, Is.Not.EqualTo(room));
            const long sharedId = 424242;
            Api("SetElementSharedId", manager.ID, hole, sharedId);
            Assert.That(Convert.ToInt64(Api("FindElementBySharedId", manager.ID, sharedId)), Is.EqualTo(hole),
                "The obstacle child must be a live native element.");

            Vector3[] solid =
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f)
            };
            Assert.That(manager.RegisterWalkableMesh(solid, new[] { 0, 1, 2, 0, 2, 3 }), Is.True);
            Assert.That(holes.Count, Is.EqualTo(0));
            Assert.That(Convert.ToInt64(Api("FindElementBySharedId", manager.ID, sharedId)), Is.EqualTo(0));
            Assert.That((bool)Api("RemoveLocalElement", manager.ID, hole), Is.False,
                "Re-registering must already have removed the obstacle child.");
        }

        // Two sibling elements whose footprints overlap are refused. The walkable's holes are the NavMesh's
        // carving around the furniture the MRUK adapter registers as boxes, so a hole and the box it was carved for overlap
        // by construction: they must not be siblings. The holes hang under one parent of their own (the holes never overlap
        // each other), and the anchors keep the room as theirs.
        [Test]
        public void WalkableHolesAndTheMrukBoxesOfTheSameFurnitureRegisterTogetherInEitherOrder()
        {
            Vector3[] holed =
            {
                new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f),
                new Vector3(1f, 0f, 1f), new Vector3(3f, 0f, 1f),
                new Vector3(3f, 0f, 3f), new Vector3(1f, 0f, 3f)
            };
            int[] triangles =
            {
                0, 1, 5, 0, 5, 4,
                1, 2, 6, 1, 6, 5,
                2, 3, 7, 2, 7, 6,
                3, 0, 4, 3, 4, 7
            };
            foreach (bool geometryFirst in new[] { false, true })
            {
                string order = geometryFirst ? "anchors then walkable" : "walkable then anchors";
                var manager = CreateManager("native-walkable-and-anchors-" + geometryFirst);
                manager.Host(FreePort());
                AssertConnected(manager);
                GameObject table = ActiveAnchor("hole-table-" + geometryFirst);
                table.transform.position = new Vector3(2f, 0f, 2f);
                if (geometryFirst) RegisterTableGeometry(manager, table, 6201, new Vector3(1.2f, 0.74f, 0.8f));
                Assert.That(manager.RegisterWalkableMesh(holed, triangles), Is.True, order);
                if (!geometryFirst) RegisterTableGeometry(manager, table, 6201, new Vector3(1.2f, 0.74f, 0.8f));
                Assert.That(manager.SceneRegistrationError, Is.Null, order + ": " + manager.SceneRegistrationError);
                Assert.That(((IList)Field(manager, "_walkableHoleElementIDs")).Count, Is.EqualTo(1), order);
                Assert.That(manager.TryGetLocalSceneGeometryElements(6201, out long tableElement, out _), Is.True, order);
                Assert.That(tableElement, Is.GreaterThan(0), order);
            }
        }

        // Every scanned room Meta ships with MR Utility Kit is registered through the real
        // manager and the real core: a footprint inside a larger one nests under it, a partial overlap is clipped by
        // the larger, and the core refuses nothing. Removing an anchor that carries nested ones takes them down natively,
        // so the layout stages them again; replacing an anchor leaves every other one registered.
        [Test]
        public void TheEightScannedSampleRoomsRegisterWithoutARefusedOverlapAndSurviveARemovalAndAReplacement()
        {
            var rooms = MrukSampleRoomFixtures.Load();
            Assert.That(rooms.Count, Is.EqualTo(8));
            foreach (var room in rooms)
            {
                var manager = CreateManager("native-sample-room-" + room.Name);
                manager.Host(FreePort());
                AssertConnected(manager);
                foreach (SceneGeometryDescriptor d in room.Anchors)
                    Assert.That(manager.RegisterLocalSceneGeometry(d), Is.True, room.Name + " anchor " + d.SourceKey + ": " + manager.SceneRegistrationError);
                Assert.That(manager.SceneRegistrationError, Is.Null, room.Name + ": " + manager.SceneRegistrationError);
                foreach (SceneGeometryDescriptor d in room.Anchors)
                    Assert.That(manager.TryGetLocalSceneGeometryElements(d.SourceKey, out long parent, out _), Is.True, room.Name + " key " + d.SourceKey);

                // The anchor with the largest volume footprint carries the most (a table with a lamp on it).
                SceneGeometryDescriptor largest = room.Anchors.Where(d => d.HasVolume)
                    .OrderByDescending(d => d.VolumeBounds.size.x * d.VolumeBounds.size.z).First();
                Assert.That(manager.RemoveLocalSceneGeometry(largest.SourceKey), Is.True, room.Name);
                Assert.That(manager.TryGetLocalSceneGeometryElements(largest.SourceKey, out _, out _), Is.False, room.Name);
                foreach (SceneGeometryDescriptor d in room.Anchors.Where(d => d.SourceKey != largest.SourceKey))
                    Assert.That(manager.TryGetLocalSceneGeometryElements(d.SourceKey, out _, out _), Is.True,
                        room.Name + ": key " + d.SourceKey + " was lost with the removed anchor");
                Assert.That(manager.RegisterLocalSceneGeometry(largest), Is.True, room.Name);
                Assert.That(manager.UpsertLocalSceneGeometry(largest), Is.True, room.Name + " replacement");
                foreach (SceneGeometryDescriptor d in room.Anchors)
                    Assert.That(manager.TryGetLocalSceneGeometryElements(d.SourceKey, out _, out _), Is.True, room.Name + " key " + d.SourceKey);
                Assert.That(manager.SceneRegistrationError, Is.Null, room.Name + " after removal and replacement: " + manager.SceneRegistrationError);
            }
        }

        // Content whose footprint lies entirely inside an observed anchor's (the Chess on the MRUK
        // table) is registered as a child of the anchor's top surface, not as a sibling of the anchor under the room, where the
        // core would refuse it. It follows the object when it moves, and is refused nowhere.
        [Test]
        public void ChessContentOnTheMrukTableRegistersUnderTheTablesTopAndFollowsItsMoves()
        {
            var host = CreateManager("chess-on-table-host");
            host.Host(FreePort());
            AssertConnected(host);
            GameObject table = ActiveAnchor("table-under-chess");
            RegisterTableGeometry(host, table, 6301, new Vector3(1.6f, 0.74f, 0.9f));
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);

            SpatialAuthoringPlan plan = CompileChessPlan();
            Matrix4x4 onTable = Matrix4x4.TRS(new Vector3(0.1f, 0.74f, 0f), Quaternion.identity, Vector3.one);
            Assert.That(host.TryApplySpatialAuthoring(plan, 9800, 0, onTable, null, out SpatialAuthoringBinding binding), Is.True,
                host.SceneRegistrationError);
            Assert.That(host.SceneRegistrationError, Is.Null, "the chess on the table was refused: " + host.SceneRegistrationError);
            Assert.That(binding.RegionElement, Is.GreaterThan(0));

            // It follows the object (its region's transform is relative to the table's top) and nothing is refused.
            Matrix4x4 moved = Matrix4x4.TRS(new Vector3(-0.2f, 0.74f, 0f), Quaternion.Euler(0f, 90f, 0f), Vector3.one);
            Assert.That(host.TryUpdateAuthoredPose(binding.ObjectElement, moved), Is.True);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);

            // The region keeps its world place: a point inside the chess region is inside it, one outside is not.
            long chessAffordance = Convert.ToInt64(Api("FindElementBySharedId", host.ID, SpatialAuthoringCompiler.AffordanceSharedId(9800, 0)));
            Assert.That(chessAffordance, Is.GreaterThan(0));
            Vector3 top = moved.MultiplyPoint3x4(new Vector3(0f, 0.478f, 0f));
            RelationEvaluation atTop = EvaluateAffordanceContact(host.ID, chessAffordance, top);
            Assert.That(atTop.has_contact, Is.EqualTo(1), "the chess surface is where the moved object is, wherever its parent is");

            // Control: the same chess in free floor space, away from every anchor, registers as a child of the room.
            Matrix4x4 away = Matrix4x4.TRS(new Vector3(4f, 0f, 4f), Quaternion.identity, Vector3.one);
            Assert.That(host.TryApplySpatialAuthoring(plan, 9801, 0, away, null, out _), Is.True);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
        }

        // The non-authored path: an object with only a collider (loaded GLB content) whose footprint lies inside an anchor's is
        // registered under it. Were it a sibling of the table under the room, the core would refuse its footprint.
        [Test]
        public void ColliderContentOnTheMrukTableRegistersUnderTheTable()
        {
            var host = CreateManager("collider-on-table-host");
            host.Host(FreePort());
            AssertConnected(host);
            GameObject table = ActiveAnchor("table-under-collider");
            RegisterTableGeometry(host, table, 6501, new Vector3(1.6f, 0.74f, 0.9f));
            GameObject onTable = InactiveObject("collider-on-table");
            onTable.transform.position = new Vector3(0.2f, 0.84f, 0f);
            var box = onTable.AddComponent<BoxCollider>();
            box.size = new Vector3(0.4f, 0.2f, 0.4f);
            var entity = onTable.AddComponent<SbNativeEntity>();
            entity.AllowManipulation = false;
            host.ShareGameObject(onTable, false);
            Pump(300, host);
            Assert.That(entity.IsAttached(), Is.True);
            Assert.That(host.SceneRegistrationError, Is.Null, "the collider content on the table was refused: " + host.SceneRegistrationError);

            // Control, INTERIM: the same collider content straddling the table's edge is not inside it, so it stays under the room and
            // the core refuses it as a sibling of the table (a partial overlap of content is not nested or clipped). Nesting and clipping
            // cover observed furniture; nothing covers a partial overlap of CONTENT, so this pins the current behaviour.
            GameObject straddling = InactiveObject("collider-straddling");
            straddling.transform.position = new Vector3(0.9f, 0.84f, 0f);
            straddling.AddComponent<BoxCollider>().size = new Vector3(0.4f, 0.2f, 0.4f);
            var other = straddling.AddComponent<SbNativeEntity>();
            other.AllowManipulation = false;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                host.ShareGameObject(straddling, false);
                Pump(300, host);
            }
            finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
            Assert.That(host.SceneRegistrationError, Is.Not.Null, "a footprint that is neither inside nor apart from the table is still refused");
        }

        // Content standing on an anchor (the Chess, a collider object) survives the anchor being restaged or removed: the same
        // element, shared ids and world pose, nothing refused. A removed anchor's content goes to the next containing
        // ancestor (the rug here), then to the room, and stands on an anchor that arrives later.
        [Test]
        public void ContentOnAnAnchorSurvivesTheAnchorBeingRestagedAndRemoved()
        {
            var host = CreateManager("content-survival-host");
            host.Host(FreePort());
            AssertConnected(host);
            GameObject rug = ActiveAnchor("rug-under-table");
            GameObject table = ActiveAnchor("table-under-content");
            Vector3 rugSize = new Vector3(5f, 0.05f, 5f), tableSize = new Vector3(2.4f, 0.74f, 0.9f);
            RegisterTableGeometry(host, rug, 6400, rugSize);
            RegisterTableGeometry(host, table, 6401, tableSize);

            // The Chess on the table (authored) and an object with a collider footprint (not authored), both on it.
            SpatialAuthoringPlan plan = CompileChessPlan();
            Matrix4x4 onTable = Matrix4x4.TRS(new Vector3(0.1f, 0.74f, 0f), Quaternion.identity, Vector3.one);
            Assert.That(host.TryApplySpatialAuthoring(plan, 9900, 0, onTable, null, out SpatialAuthoringBinding binding), Is.True, host.SceneRegistrationError);
            GameObject glb = InactiveObject("collider-content");
            glb.transform.position = new Vector3(-0.9f, 0.8f, 0f);
            var box = glb.AddComponent<BoxCollider>();
            box.size = new Vector3(0.3f, 0.2f, 0.3f);
            var entity = glb.AddComponent<SbNativeEntity>();
            entity.AllowManipulation = false;
            host.ShareGameObject(glb, false);
            Pump(300, host);
            Assert.That(entity.IsAttached(), Is.True, "the collider content was not shared");
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);

            long regionShared = SpatialAuthoringCompiler.RegionSharedId(9900);
            long affordanceShared = SpatialAuthoringCompiler.AffordanceSharedId(9900, 0);
            long region = Convert.ToInt64(Api("FindElementBySharedId", host.ID, regionShared));
            long affordance = Convert.ToInt64(Api("FindElementBySharedId", host.ID, affordanceShared));
            long contentElement = entity.ID;
            Vector3 top = onTable.MultiplyPoint3x4(new Vector3(0f, 0.478f, 0f));
            Assert.That(region, Is.EqualTo(binding.RegionElement));

            void AssertSurvived(string step)
            {
                Assert.That(host.SceneRegistrationError, Is.Null, step + ": " + host.SceneRegistrationError);
                Assert.That(Convert.ToInt64(Api("FindElementBySharedId", host.ID, regionShared)), Is.EqualTo(region), step + ": the chess region is the same element");
                Assert.That(Convert.ToInt64(Api("FindElementBySharedId", host.ID, affordanceShared)), Is.EqualTo(affordance), step + ": and its surface");
                Assert.That(entity.ID, Is.EqualTo(contentElement), step + ": the collider content keeps its element");
                RelationEvaluation at = EvaluateAffordanceContact(host.ID, affordance, top);
                Assert.That(at.has_contact, Is.EqualTo(1), step + ": the chess surface is where it was in the world");
                Assert.That(at.contact_point_world_x, Is.EqualTo(top.x).Within(1e-4), step);
                Assert.That(at.contact_point_world_y, Is.EqualTo(top.z).Within(1e-4), step);
                // The element still takes writes: it follows its object.
                Assert.That(host.TryUpdateAuthoredPose(binding.ObjectElement, onTable), Is.True, step);
                Assert.That(host.SceneRegistrationError, Is.Null, step + " after a move: " + host.SceneRegistrationError);
            }
            AssertSurvived("registered");

            // 1. The table is scanned again: its tree is replaced, the content stands on the new one.
            SceneGeometryDescriptor again = DescriptorOf(table, 6401, tableSize);
            Assert.That(host.UpsertLocalSceneGeometry(again), Is.True, host.SceneRegistrationError);
            AssertSurvived("table restaged");

            // 2. The table is removed for good: the rug contains the chess, so the content moves under it.
            Assert.That(host.RemoveLocalSceneGeometry(6401), Is.True, host.SceneRegistrationError);
            AssertSurvived("table removed");

            // 3. The rug is removed too: the content moves to the room.
            Assert.That(host.RemoveLocalSceneGeometry(6400), Is.True, host.SceneRegistrationError);
            AssertSurvived("rug removed");

            // 4. A table arrives later and contains the content, which now stands on it instead of beside it.
            RegisterTableGeometry(host, table, 6401, tableSize);
            AssertSurvived("table arrived later");

            // 5. The chess is carried off the table and put back on it: a move is a placement, so the hierarchy nests it under the
            // anchor that contains it where it is now (policy C) and the core refuses nothing, on the table and off it, twice.
            Matrix4x4 beside = Matrix4x4.TRS(new Vector3(2.8f, 0.74f, 0f), Quaternion.identity, Vector3.one);
            for (int round = 0; round < 2; ++round)
            {
                Assert.That(host.TryUpdateAuthoredPose(binding.ObjectElement, beside), Is.True, "round " + round + " off the table");
                Assert.That(host.SceneRegistrationError, Is.Null, "round " + round + " off the table: " + host.SceneRegistrationError);
                Assert.That(host.TryUpdateAuthoredPose(binding.ObjectElement, onTable), Is.True, "round " + round + " back on the table");
                Assert.That(host.SceneRegistrationError, Is.Null, "round " + round + " back on the table: " + host.SceneRegistrationError);
            }
            AssertSurvived("moved off the table and back");
        }

        [Test]
        public void RealNativeHierarchyAndRecordedAnchorGroundingPreserveRootPose()
        {
            var manager = CreateManager("native-observation-host");
            manager.Host(FreePort());
            AssertConnected(manager);
            Assert.That(manager.RegisterLocalSpatialHierarchy(SeatDescriptor(), out var hierarchy), Is.True);
            Assert.That(hierarchy.TryGetSupportElement("seat.top", out long seat), Is.True);
            long room = (long)Field(manager, "_roomElementID");
            Api("SetElementPolygon", manager.ID, room, EnumValue("SbGeometryRole", 1),
                new Vector2[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 20), new Vector2(0, 20) });
            Api("SetElementTag", manager.ID, seat, "ExplicitSeat");
            // An element with no shared id is never a referent. The seat is one the receiver would have.
            Api("SetElementSharedId", manager.ID, seat, 700001L);

            var actorObject = InactiveObject("recorded-source");
            actorObject.tag = "Avatar";
            actorObject.transform.SetPositionAndRotation(new Vector3(5, 0, 8), Quaternion.Euler(0, 90, 0));
            var entity = actorObject.AddComponent<SbNativeEntity>();
            // This is a tracked observation source, not an ISDK-manipulable
            // content object; no collider or placement motif is part of it.
            entity.AllowManipulation = false;
            manager.ShareGameObject(actorObject, false);
            manager.Update();
            manager.RegisterAnchor(actorObject, "hand", new Vector3(2, 2, 1));
            manager.Update();
            Assert.That(entity.IsAttached() && entity.IsSource, Is.True);
            long actor = (long)((IDictionary)Field(manager, "_v2ActorByEntity"))[entity.ID];
            Api("BindAffordance", manager.ID, actor, "hand", "ExplicitSeat");
            UpdateSource(manager.ID, actor);
            RelationState before = ReadHand(manager.ID, actor);
            Assert.That(before.proximity, Is.LessThan(.95));
            ActorState rootBefore = sb_get_actor_state(manager.ID, actor);
            Assert.That(manager.TryCreateAnchorPoseInput(entity, out IAnchorPoseFrameSink sink), Is.True);

            var source = InactiveObject("recorded-input").AddComponent<RecordedAnchorPoseSource>();
            source.SetKnownAnchorIds(new[] { "hand" });
            source.SetSink(sink);
            var frame = new AnchorPoseFrame
            {
                schema = 1, frame = "unity-world", sequence = 1, timeSeconds = 0,
                anchors = new[] { new AnchorPoseSample
                {
                    // Local support point (.3, 0, -.15), yaw +90 at (5,0,8):
                    // independently computed world = (4.85, .8, 7.7).
                    anchorId = "hand", worldPosition = new Vector3(4.85f, .8f, 7.7f),
                    worldRotation = Quaternion.Euler(0, 37, 0)
                } }
            };
            Assert.That(source.TrySubmit(frame, out string error), Is.True, error);
            // Ingress is an observation only; extraction is advanced explicitly.
            UpdateSource(manager.ID, actor);
            RelationState after = ReadHand(manager.ID, actor);
            Assert.That(after.referent_element_id, Is.EqualTo(seat));
            Assert.That(after.proximity, Is.EqualTo(1d).Within(1e-6));
            Assert.That(after.has_contact, Is.EqualTo(1));
            Assert.That(after.contact_point_world_x, Is.EqualTo(4.85).Within(1e-5));
            Assert.That(after.contact_point_world_y, Is.EqualTo(7.7).Within(1e-5));
            Assert.That(after.contact_point_world_z, Is.EqualTo(.8).Within(1e-5),
                "Unity Y-up height must arrive as native world Z, with parent-local transforms composed.");
            ActorState rootAfter = sb_get_actor_state(manager.ID, actor);
            Assert.That(rootAfter.position_x, Is.EqualTo(rootBefore.position_x));
            Assert.That(rootAfter.position_y, Is.EqualTo(rootBefore.position_y));
            Assert.That(rootAfter.yaw, Is.EqualTo(rootBefore.yaw));

            sink.ClearWorldAnchor("hand");
            UpdateSource(manager.ID, actor);
            Assert.That(ReadHand(manager.ID, actor).proximity, Is.EqualTo(before.proximity).Within(1e-6));
            // New additive deletion ABI is exercised, not merely symbol-resolved.
            Assert.That((bool)Api("RemoveLocalElement", manager.ID, seat), Is.True);
            Assert.That((bool)Api("RemoveLocalElement", manager.ID, seat), Is.False);
            var remaining = new RelationState[16];
            int remainingCount = sb_get_relation_states(manager.ID, actor, remaining, remaining.Length);
            Assert.That(remainingCount, Is.InRange(0, remaining.Length));
            for (int i = 0; i < remainingCount; ++i)
                Assert.That(remaining[i].referent_element_id, Is.Not.EqualTo(seat),
                    "Local retirement must not leave a cached relation to the removed support.");
            long generation = manager.ConnectionGeneration;
            manager.Disconnect();
            Assert.That(hierarchy.IsLive, Is.False);
            manager.Host(FreePort());
            AssertConnected(manager);
            Assert.That(manager.ConnectionGeneration, Is.GreaterThan(generation));
            frame.sequence = 2;
            Assert.That(sink.TryApplyWorldFrame(frame), Is.False, "Old native sink cannot write into a replacement session.");
        }

        [Test]
        public void OriginalFreeformScene_LocalBackend_StartsContentRefreshesPlacementAndStops()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            try
            {
                FreeformCollaborationApp app = SceneComponent<FreeformCollaborationApp>(scene);
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                FreeformSceneWhiteboardDefinition whiteboardDefinition =
                    SceneComponent<FreeformSceneWhiteboardDefinition>(scene);
                FreeformSceneChessDefinition chessDefinition =
                    SceneComponent<FreeformSceneChessDefinition>(scene);
                ControlPanel panel = EnabledSceneControlPanel(scene, app);
                Assert.That(app, Is.Not.Null);
                Assert.That(backend, Is.Not.Null);
                Assert.That(panel, Is.Not.Null);
                Transform[] authoredChildren = backend.transform.Cast<Transform>().ToArray();
                // EditMode scene loading does not run MonoBehaviour lifecycle
                // methods. Invoke the actual Awake (which registers Button.onClick)
                // and OnEnable before clicking the serialized Connect Button.
                InvokePrivate(panel, "Awake");
                InvokePrivate(panel, "OnEnable");
                var connect = (UnityEngine.UI.Button)PrivateField(panel, "_connect");
                var localLabel = (TMPro.TMP_Text)PrivateField(panel, "_connectLabel");
                Assert.That(connect, Is.Not.Null);
                Assert.That(localLabel, Is.Not.Null);
                connect.onClick.Invoke();
                string error = backend.LastError;
                Assert.That(backend.IsRunning, Is.True, error);
                Assert.That(localLabel.text, Does.Contain("Local Content Running"));
                Assert.That(localLabel.text, Does.Contain("no Internet session"));
                Assert.That(backend.LocalWorld, Is.Not.Null);
                Assert.That(backend.transform.Cast<Transform>().Count(), Is.EqualTo(authoredChildren.Length + 1),
                    "A live Foundation attempt adds exactly one world root beside the authored actor charts.");
                Assert.That(backend.transform.Cast<Transform>().Except(authoredChildren).Single(),
                    Is.SameAs(backend.LocalWorld.transform));
                Assert.That(backend.ContentCoordinator.Catalog.TryGetWhiteboard(
                    FreeformSceneLocalBackend.WhiteboardContentId, out WhiteboardContentModel whiteboard), Is.True);
                Color32[] beforePixels = whiteboard.CopyPixels();
                FreeformSceneWhiteboardInput whiteboardInput = whiteboardDefinition.MovableRoot
                    .GetComponent<FreeformSceneWhiteboardInput>();
                Assert.That(whiteboardInput, Is.Not.Null);
                // This calls an original selector's local lease before driving the
                // original surface projection; no model-only drawing shortcut.
                whiteboardDefinition.Palette[0].ChangePenColor();
                Transform surface = whiteboardDefinition.SurfaceRenderer.transform;
                Vector3 start = surface.TransformPoint(new Vector3(-.25f, -.25f, 0));
                Vector3 end = surface.TransformPoint(new Vector3(.25f, .25f, 0));
                Assert.That(whiteboardInput.ProcessPointer(start, true, true, out error), Is.True, error);
                Assert.That(whiteboardInput.ProcessPointer(end, true, true, out error), Is.True, error);
                Assert.That(whiteboardInput.ProcessPointer(end, true, false, out error), Is.True, error);
                Assert.That(whiteboard.CopyPixels().SequenceEqual(beforePixels), Is.False,
                    "The serialized palette and pointer adapter must draw into the live local canvas.");
                Assert.That(backend.ContentCoordinator.Catalog.TryGetChess(
                    FreeformSceneLocalBackend.ChessContentId, out ChessContentModel chess), Is.True);
                FreeformSceneChessPieceReference firstBinding = chessDefinition.Pieces[0];
                Assert.That(chess.TryGetPiecePose(firstBinding.PieceId, out ChessPiecePose originalPose), Is.True);
                Vector3 expectedWorldDestination = firstBinding.Piece.position + Vector3.right * .01f;
                Vector3 expectedBoardDestination = chessDefinition.Board.InverseTransformPoint(
                    expectedWorldDestination);
                firstBinding.Interaction.WhenSelect.Invoke();
                firstBinding.Piece.position = expectedWorldDestination;
                firstBinding.Interaction.WhenUnselect.Invoke();
                Assert.That(chess.TryGetPiecePose(firstBinding.PieceId, out ChessPiecePose captured), Is.True);
                Assert.That(Vector3.Distance(captured.BoardLocalPosition, expectedBoardDestination),
                    Is.LessThan(1e-5f),
                    "The original ISDK wrapper selection must capture the authored piece transform.");
                Assert.That(Vector3.Distance(captured.BoardLocalPosition, originalPose.BoardLocalPosition),
                    Is.GreaterThan(1e-4f));
                Assert.That(Vector3.Distance(firstBinding.Piece.position,
                    chessDefinition.Board.TransformPoint(expectedBoardDestination)), Is.LessThan(1e-5f));

                SpatialBlendObject authoredRoot = whiteboardDefinition.MovableRoot
                    .GetComponent<SpatialBlendObject>();
                Assert.That(authoredRoot.Registration.NativeSpace, Is.GreaterThan(0));
                Assert.That(authoredRoot.Registration.NativeElement, Is.GreaterThan(0));
                AssertRefreshPropagatesThroughNativeScopedProbe(backend.LocalWorld, authoredRoot);
                backend.Stop();
                Assert.That(backend.IsRunning, Is.False);
                Assert.That(backend.LocalWorld, Is.Null);
                Assert.That(backend.transform.Cast<Transform>(), Is.EquivalentTo(authoredChildren),
                    "Stop detaches only the retired native root and preserves every authored actor chart.");
            }
            finally
            {
                backend?.Stop();
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_LocalBackend_StopAndRetryRestoresAuthoredViews()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            Texture2D sentinel = null;
            try
            {
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                FreeformCollaborationApp app = SceneComponent<FreeformCollaborationApp>(scene);
                FreeformSceneWhiteboardDefinition whiteboard =
                    SceneComponent<FreeformSceneWhiteboardDefinition>(scene);
                FreeformSceneChessDefinition chess = SceneComponent<FreeformSceneChessDefinition>(scene);
                Material[] originalMaterials = whiteboard.SurfaceRenderer.sharedMaterials;
                var originalPoses = chess.Pieces.ToDictionary(binding => binding.PieceId,
                    binding => (position: binding.Piece.position, rotation: binding.Piece.rotation));
                sentinel = new Texture2D(1, 1);
                sentinel.SetPixel(0, 0, Color.magenta);
                sentinel.Apply();
                var block = new MaterialPropertyBlock();
                block.SetTexture("_MainTex", sentinel);
                whiteboard.SurfaceRenderer.SetPropertyBlock(block);
                int worldsBefore = UnityEngine.Object.FindObjectsByType<SpatialBlendLocalWorld>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                int objectAttachments = whiteboard.MovableRoot.GetComponents<SpatialBlendObject>().Length;
                int inputAttachments = whiteboard.MovableRoot.GetComponents<FreeformSceneWhiteboardInput>().Length;

                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                Assert.That(UnityEngine.Object.FindObjectsByType<SpatialBlendLocalWorld>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(worldsBefore + 1));
                backend.Stop();
                Assert.That(whiteboard.SurfaceRenderer, Is.Not.Null,
                    "Stopping must retain the original authored renderer.");
                whiteboard.SurfaceRenderer.GetPropertyBlock(block);
                Assert.That(block.GetTexture("_MainTex"), Is.SameAs(sentinel),
                    "Unbind must restore the precise authored MaterialPropertyBlock texture.");
                CollectionAssert.AreEqual(originalMaterials, whiteboard.SurfaceRenderer.sharedMaterials);
                foreach (FreeformSceneChessPieceReference binding in chess.Pieces)
                {
                    Assert.That(Vector3.Distance(binding.Piece.position,
                        originalPoses[binding.PieceId].position), Is.LessThan(1e-5f));
                    Assert.That(Quaternion.Angle(binding.Piece.rotation,
                        originalPoses[binding.PieceId].rotation), Is.LessThan(1e-4f));
                }
                Assert.That(UnityEngine.Object.FindObjectsByType<SpatialBlendLocalWorld>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(worldsBefore),
                    "Stop must retire the actual native world, not merely detach its Transform.");
                Assert.That(whiteboard.MovableRoot.GetComponents<SpatialBlendObject>().Length,
                    Is.EqualTo(objectAttachments + 1));
                Assert.That(whiteboard.MovableRoot.GetComponents<FreeformSceneWhiteboardInput>().Length,
                    Is.EqualTo(inputAttachments + 1));

                Assert.That(app.TryStartFoundationLocal(out error), Is.True, error);
                Assert.That(backend.IsRunning, Is.True);
                Assert.That(UnityEngine.Object.FindObjectsByType<SpatialBlendLocalWorld>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(worldsBefore + 1));
                Assert.That(whiteboard.MovableRoot.GetComponents<SpatialBlendObject>().Length,
                    Is.EqualTo(objectAttachments + 1), "Retry must reuse, not duplicate, owned attachments.");
                backend.Stop();
                Assert.That(UnityEngine.Object.FindObjectsByType<SpatialBlendLocalWorld>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(worldsBefore));
            }
            finally
            {
                backend?.Stop();
                if (sentinel != null) UnityEngine.Object.DestroyImmediate(sentinel);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_WhiteboardReceiverSupportRegistersRefreshesAndRestarts()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            Vector3 originalPosition = default;
            bool moved = false;
            try
            {
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                FreeformCollaborationApp app = SceneComponent<FreeformCollaborationApp>(scene);
                FreeformSceneWhiteboardDefinition whiteboard =
                    SceneComponent<FreeformSceneWhiteboardDefinition>(scene);
                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                SpatialBlendObject receiver = whiteboard.MovableRoot.GetComponent<SpatialBlendObject>();
                SpatialBlendObject support = backend.WhiteboardSupport;
                Assert.That(support, Is.Not.Null);
                Assert.That(support.Kind, Is.EqualTo(SpatialBlendObjectKind.Affordance));
                Assert.That(support.ColliderGeometryEnabled, Is.False);
                Assert.That(support.GetComponent<Collider>(), Is.Null);
                Assert.That(support.transform.parent, Is.SameAs(whiteboard.SurfaceRenderer.transform),
                    "The native support must inherit the actual renderer-plane affine.");
                Assert.That(support.Registration.Parent, Is.SameAs(receiver.Registration));
                Assert.That(support.Registration.Parent.NativeElement,
                    Is.EqualTo(receiver.Registration.NativeElement),
                    "The Affordance must be a real native child of the receiver board Object.");
                Assert.That(whiteboard.TryCreateSurfaceFrame(out WhiteboardSurfaceFrame frame,
                    out error), Is.True, error);
                foreach (Vector3 corner in frame.GetWorldCorners())
                    AssertNativeSurfaceContact(backend.LocalWorld, support, corner);

                // Move the authored board only, then force the production refresh
                // path that LateUpdate invokes at runtime. The contact oracle reads
                // the real native WorldSurface rather than private Unity state.
                var priorRegistration = support.Registration;
                originalPosition = whiteboard.MovableRoot.position;
                whiteboard.MovableRoot.position += new Vector3(.37f, -.13f, .29f);
                moved = true;
                Assert.That(support.TryRefresh(out error), Is.True, error);
                Assert.That(whiteboard.TryCreateSurfaceFrame(out frame, out error), Is.True, error);
                foreach (Vector3 corner in frame.GetWorldCorners())
                    AssertNativeSurfaceContact(backend.LocalWorld, support, corner);

                backend.Stop();
                Assert.That(support.IsRegistered, Is.False);
                Assert.That(priorRegistration.IsLive, Is.False,
                    "Stop must retire the prior Affordance lifetime before its parent/world.");
                Assert.That(app.TryStartFoundationLocal(out error), Is.True, error);
                Assert.That(backend.WhiteboardSupport, Is.SameAs(support),
                    "Restart reuses the owned Unity attachment but not its native lifetime.");
                Assert.That(support.IsRegistered, Is.True);
                Assert.That(support.Registration, Is.Not.SameAs(priorRegistration));
                Assert.That(support.Registration.Parent.NativeElement,
                    Is.EqualTo(receiver.Registration.NativeElement));
                backend.Stop();
            }
            finally
            {
                if (moved && scene.isLoaded)
                {
                    FreeformSceneWhiteboardDefinition whiteboard =
                        SceneComponent<FreeformSceneWhiteboardDefinition>(scene);
                    if (whiteboard != null)
                        whiteboard.MovableRoot.position = originalPosition;
                }
                backend?.Stop();
                if (scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_LocalBackend_ReentrantObserverStopFailsStartThenRetries()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            try
            {
                FreeformCollaborationApp app = SceneComponent<FreeformCollaborationApp>(scene);
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                Assert.That(app, Is.Not.Null);
                Assert.That(backend, Is.Not.Null);
                Action stopDuringStartup = () => backend.Stop();
                backend.StateChanged += stopDuringStartup;
                Assert.That(app.TryStartFoundationLocal(out string error), Is.False,
                    "A StateChanged observer may retire a just-created local lifetime.");
                Assert.That(backend.IsRunning, Is.False);
                Assert.That(error, Does.Contain("stopped by a state observer"));
                backend.StateChanged -= stopDuringStartup;
                Assert.That(app.TryStartFoundationLocal(out error), Is.True, error);
            }
            finally
            {
                backend?.Stop();
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_RecordedActorFramesReachOriginalAvatarTrackingConsumer()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            string recordingDirectory = Path.Combine(Path.GetTempPath(),
                "spatialblend-original-actor-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recordingDirectory);
            try
            {
                var definition = SceneComponent<FreeformSceneActorDefinition>(scene);
                Assert.That(definition, Is.Not.Null,
                    "The actual original scene must explicitly author its local actor chart.");
                definition.SourceRoot.SetPositionAndRotation(new Vector3(1f, 0f, 2f),
                    Quaternion.Euler(0f, 90f, 0f));
                definition.ReceiverRoot.SetPositionAndRotation(new Vector3(11f, 0f, -3f),
                    Quaternion.Euler(0f, -90f, 0f));
                var app = SceneComponent<FreeformCollaborationApp>(scene);
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                Assert.That(backend.ActorRuntimes.Count, Is.EqualTo(1));
                FreeformSceneActorRuntime runtime = backend.ActorRuntimes[0];
                Assert.That(runtime.SourceActor.IsRegistered, Is.True);
                Assert.That(runtime.ReceiverActor.IsRegistered, Is.True);
                Assert.That(runtime.SourceActor.Registration.NativeSpace,
                    Is.Not.EqualTo(runtime.ReceiverActor.Registration.NativeSpace));
                Assert.That(runtime.AvatarConsumer, Is.Not.Null, runtime.LastConsumerError);
                Assert.That(runtime.AvatarConsumer.IsConfigured, Is.True, runtime.LastConsumerError);
                Assert.That(runtime.LocalAvatar, Is.Not.Null, runtime.LastConsumerError);
                Assert.That(runtime.LocalAvatar.IsLocalPresetMode, Is.True);
                Assert.That(runtime.LocalAvatar.CurrentLocalPresetStatus,
                    Is.EqualTo(SbAvatarEntity.LocalPresetStatus.Unavailable),
                    "macOS is a transform-consumer gate, not Meta native mesh acceptance.");
                Assert.That(runtime.LocalAvatar.IsCreated, Is.False);
                Assert.That(runtime.LocalAvatar.GetComponent<SbAvatarBehaviourFusion>(), Is.Null);
                var tracking = new SbTransformTrackingDelegate(runtime.LocalAvatar.LocalPresetInput);
                Assert.That(tracking.GetRawInputTrackingState(out var before), Is.True);
                Assert.That(before.headsetActive || before.leftControllerActive ||
                    before.rightControllerActive, Is.False,
                    "Registering an actor must not fabricate tracked observations.");

                AnchorPoseFrame first = OriginalActorFrame(0, 100d);
                var headOnly = new AnchorPoseFrame
                {
                    sequence = 1, timeSeconds = 101d,
                    anchors = new[] { new AnchorPoseSample
                    {
                        anchorId = "head", worldPosition = new Vector3(1f, 1.7f, 2f),
                        worldRotation = Quaternion.Euler(0f, 45f, 0f)
                    } }
                };
                var empty = new AnchorPoseFrame { sequence = 2, timeSeconds = 102d };
                WriteSyntheticCommittedActorRecording(recordingDirectory,
                    new[] { first, headOnly, empty }, out string jsonl, out string manifest);
                Assert.That(runtime.TryLoadCommittedReplay(jsonl, manifest, out error), Is.True, error);
                Assert.That(runtime.TryBeginReplay(1000d, out error), Is.True, error);
                Assert.That(runtime.Tick(0d, 1000d, out error), Is.True, error);
                Assert.That(runtime.LastConsumerError, Is.Null);
                Assert.That(tracking.GetRawInputTrackingState(out var observed), Is.True);
                Assert.That(observed.headsetActive && observed.leftControllerActive &&
                    observed.rightControllerActive, Is.True);
                // Independent oracle: source yaw +90 to receiver yaw -90 is a
                // 180-degree rotation, so X/Z offsets change sign; height stays.
                AssertActorVector(observed.headset.position, new Vector3(11f, 1.6f, -3f));
                AssertActorVector(observed.leftController.position, new Vector3(10.8f, 1.2f, -3.3f));
                AssertActorVector(observed.rightController.position, new Vector3(11.2f, 1.3f, -3.4f));
                Assert.That(Quaternion.Angle(observed.headset.orientation,
                    Quaternion.Euler(0f, -135f, 0f)), Is.LessThan(.01f));

                Assert.That(runtime.Tick(double.NaN, 1001d, out error), Is.False);
                Assert.That(runtime.Replay.NextFrameIndex, Is.EqualTo(1),
                    "An invalid caller clock must not advance a committed frame.");
                Assert.That(runtime.Tick(1d, 1001d, out error), Is.True, error);
                Assert.That(tracking.GetRawInputTrackingState(out var partial), Is.True);
                Assert.That(partial.headsetActive, Is.True);
                Assert.That(partial.leftControllerActive || partial.rightControllerActive, Is.False);
                AssertActorVector(partial.headset.position, new Vector3(11f, 1.7f, -3f));

                Assert.That(runtime.Tick(2d, 1002d, out error), Is.True, error);
                Assert.That(tracking.GetRawInputTrackingState(out var absent), Is.True);
                Assert.That(absent.headsetActive || absent.leftControllerActive ||
                    absent.rightControllerActive, Is.False);
                long configuredVersion = runtime.Replay.ConfigurationVersion;
                Assert.That(runtime.TryLoadCommittedReplay(jsonl + ".missing", manifest, out error),
                    Is.False, "A malformed replacement must preserve the valid replay.");
                Assert.That(runtime.Replay.HasConfiguration, Is.True);
                Assert.That(runtime.Replay.ConfigurationVersion, Is.EqualTo(configuredVersion));
                Assert.That(runtime.TryBeginReplay(2000d, out error), Is.True, error);
                Assert.That(runtime.Tick(0d, 2000d, out error), Is.True, error);
                Assert.That(tracking.GetRawInputTrackingState(out var replayed), Is.True);
                Assert.That(replayed.headsetActive && replayed.leftControllerActive &&
                    replayed.rightControllerActive, Is.True);
                AssertActorVector(replayed.headset.position, new Vector3(11f, 1.6f, -3f));
                Assert.That(runtime.TryBeginReplay(1999d, out error), Is.False);
                Assert.That(runtime.Replay.NextFrameIndex, Is.EqualTo(1));
                Assert.That(tracking.GetRawInputTrackingState(out var afterBackwardClock), Is.True);
                Assert.That(afterBackwardClock.headsetActive && afterBackwardClock.leftControllerActive &&
                    afterBackwardClock.rightControllerActive, Is.True,
                    "Rejecting a backward replay clock must not reset accepted observations.");
            }
            finally
            {
                backend?.Stop();
                EditorSceneManager.CloseScene(scene, true);
                Directory.Delete(recordingDirectory, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_RetiredActorRuntimeCannotClearReplacementLifetime()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            try
            {
                var app = SceneComponent<FreeformCollaborationApp>(scene);
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                Assert.That(backend.ActorRuntimes.Count, Is.EqualTo(1));
                FreeformSceneActorRuntime retired = backend.ActorRuntimes[0];
                var oldSink = (IAnchorPoseFrameSink)PrivateField(retired.Input, "_sink");
                Assert.That(retired.Input.TrySubmit(OriginalActorFrame(0, 0d), out error), Is.True, error);
                backend.LocalWorld.DrainRealizations(1d);
                backend.Stop();
                Assert.That(retired.IsRunning, Is.False);
                Assert.That(app.TryStartFoundationLocal(out error), Is.True, error);
                FreeformSceneActorRuntime replacement = backend.ActorRuntimes[0];
                Assert.That(replacement, Is.Not.SameAs(retired));
                Assert.That(replacement.SourceActor, Is.SameAs(retired.SourceActor),
                    "The authored actor component survives while its native registration is replaced.");
                Assert.That(replacement.Input.TrySubmit(OriginalActorFrame(0, 0d), out error), Is.True, error);
                backend.LocalWorld.DrainRealizations(2d);
                var tracking = new SbTransformTrackingDelegate(replacement.LocalAvatar.LocalPresetInput);
                Assert.That(tracking.GetRawInputTrackingState(out var before), Is.True);
                Assert.That(before.headsetActive, Is.True);

                retired.Stop();
                Assert.That(retired.Tick(out error), Is.False);
                Assert.That(oldSink.TryApplyWorldFrame(OriginalActorFrame(1, 1d)), Is.False);
                Assert.That(((IAnchorPoseFrameCheckedReplaySink)oldSink).TryResetWorldAnchorReplay(), Is.False);
                Assert.That(replacement.IsRunning && replacement.SourceActor.IsRegistered &&
                    replacement.ReceiverActor.IsRegistered, Is.True);
                Assert.That(tracking.GetRawInputTrackingState(out var after), Is.True);
                Assert.That(after.headsetActive && after.leftControllerActive && after.rightControllerActive,
                    Is.True, "Old Stop/reset callbacks cannot clear replacement observations.");
                AssertActorVector(after.headset.position, before.headset.position);
                Assert.That(replacement.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 1, timeSeconds = 1d
                }, out error), Is.True, error);
                backend.LocalWorld.DrainRealizations(3d);
                Assert.That(tracking.GetRawInputTrackingState(out var empty), Is.True);
                Assert.That(empty.headsetActive || empty.leftControllerActive ||
                    empty.rightControllerActive, Is.False);
            }
            finally
            {
                backend?.Stop();
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_ExternalInputReplacementFencesRuntimeAndPreservesSource()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            string recordingDirectory = Path.Combine(Path.GetTempPath(),
                "spatialblend-external-input-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recordingDirectory);
            try
            {
                var app = SceneComponent<FreeformCollaborationApp>(scene);
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                FreeformSceneActorRuntime runtime = backend.ActorRuntime;
                var oldSink = (IAnchorPoseFrameSink)PrivateField(runtime.Input, "_sink");
                WriteSyntheticCommittedActorRecording(recordingDirectory,
                    new[] { OriginalActorFrame(0, 0d), OriginalActorFrame(1, 1d) },
                    out string jsonl, out string manifest);
                Assert.That(runtime.TryLoadCommittedReplay(jsonl, manifest, out error), Is.True, error);
                Assert.That(runtime.TryBeginReplay(100d, out error), Is.True, error);
                Assert.That(runtime.Tick(0d, 100d, out error), Is.True, error);
                long configurationVersion = runtime.Replay.ConfigurationVersion;

                var external = new ExternalReplaySink();
                Assert.That(runtime.Input.TrySetSink(external, out error), Is.True, error);
                Assert.That(runtime.Input.TrySubmit(OriginalActorFrame(0, 0d), out error), Is.True, error);
                Assert.That(external.Frames, Is.EqualTo(1));
                Assert.That(external.Resets, Is.Zero);
                Assert.That(runtime.Tick(1d, 101d, out error), Is.False);
                Assert.That(oldSink.TryApplyWorldFrame(OriginalActorFrame(1, 1d)), Is.False);
                Assert.That(((IAnchorPoseFrameCheckedReplaySink)oldSink).TryResetWorldAnchorReplay(),
                    Is.False, "An adapter is stale immediately on input replacement, before Stop.");
                oldSink.ClearWorldAnchor("head");

                backend.Stop();
                Assert.That(runtime.IsRunning, Is.False);
                Assert.That(external.Frames, Is.EqualTo(1));
                Assert.That(external.Resets, Is.Zero,
                    "Runtime Stop cannot reset a sink installed by an external owner.");
                Assert.That(external.Clears, Is.Zero);
                Assert.That(PrivateField(runtime.Input, "_sink"), Is.SameAs(external));
                Assert.That(runtime.Replay.ConfigurationVersion, Is.EqualTo(configurationVersion));
                Assert.That(runtime.Replay.HasConfiguration, Is.True,
                    "Owned replay release must be withheld when it would reset a foreign source.");
                Assert.That(runtime.Input.TrySubmit(OriginalActorFrame(0, 0d), out error), Is.False,
                    "Stop must also preserve the replacement source's sequence fence.");
                Assert.That(runtime.Input.TrySubmit(OriginalActorFrame(1, 1d), out error), Is.True, error);
                Assert.That(external.Frames, Is.EqualTo(2));
            }
            finally
            {
                backend?.Stop();
                EditorSceneManager.CloseScene(scene, true);
                Directory.Delete(recordingDirectory, true);
            }
        }

        private sealed class ExternalReplaySink : IAnchorPoseFrameCheckedReplaySink
        {
            public int Frames { get; private set; }
            public int Resets { get; private set; }
            public int Clears { get; private set; }
            public bool TryApplyWorldFrame(AnchorPoseFrame frame) { ++Frames; return true; }
            public void ClearWorldAnchor(string anchor) { ++Clears; }
            public void ResetWorldAnchorReplay() { ++Resets; }
            public bool TryResetWorldAnchorReplay() { ++Resets; return true; }
        }

        [Test]
        public void OriginalFreeformScene_StopDuringNativeAdmissionRetriesAfterSubmissionUnwinds()
        {
            foreach (bool useReplay in new[] { false, true })
            {
                Scene scene = OpenOriginalFreeformSceneForNativeTest();
                FreeformSceneLocalBackend backend = null;
                string directory = Path.Combine(Path.GetTempPath(),
                    "spatialblend-stop-admission-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var app = SceneComponent<FreeformCollaborationApp>(scene);
                    backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                    Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                    FreeformSceneActorRuntime retired = backend.ActorRuntime;
                    var forwarder = (IAnchorPoseFrameSink)PrivateField(retired.Input, "_sink");
                    var native = (IAnchorPoseFrameCheckedReplaySink)PrivateField(forwarder, "_native");
                    int stops = 0;
                    var boundary = new NativeAdmissionCallbackSink(native, () =>
                    {
                        ++stops;
                        Assert.That(PrivateField(retired.Input, "_isSubmitting"), Is.EqualTo(true));
                        backend.Stop();
                        Assert.That(app.TryStartFoundationLocal(out _), Is.False,
                            "A callback-active source cannot transfer its exact lease yet.");
                    });
                    // Instrument only the callback boundary after REAL native
                    // admission. The production forwarder/source/player are intact;
                    // no main-thread guard, source ownership or reset is bypassed.
                    forwarder.GetType().GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(forwarder, boundary);
                    if (useReplay)
                    {
                        WriteSyntheticCommittedActorRecording(directory,
                            new[] { OriginalActorFrame(0, 0d), OriginalActorFrame(1, 1d) },
                            out string jsonl, out string manifest);
                        Assert.That(retired.TryLoadCommittedReplay(jsonl, manifest, out error), Is.True, error);
                        Assert.That(retired.TryBeginReplay(100d, out error), Is.True, error);
                        Assert.That(retired.Tick(0d, 100d, out error), Is.False,
                            "The replay must report its reentrant stop, not keep advancing.");
                    }
                    else
                    {
                        Assert.That(retired.Input.TrySubmit(OriginalActorFrame(0, 0d), out error),
                            Is.True, error); // the native frame was already committed.
                    }
                    Assert.That(stops, Is.EqualTo(1));
                    Assert.That(retired.IsRunning, Is.False);
                    Assert.That(app.TryStartFoundationLocal(out error), Is.True, error);
                    FreeformSceneActorRuntime replacement = backend.ActorRuntime;
                    Assert.That(replacement, Is.Not.SameAs(retired));
                    Assert.That(replacement.Replay.HasConfiguration, Is.False);
                    Assert.That(replacement.Input.TrySubmit(OriginalActorFrame(0, 0d), out error),
                        Is.True, error);
                    var tracking = new SbTransformTrackingDelegate(replacement.LocalAvatar.LocalPresetInput);
                    retired.Stop();
                    Assert.That(tracking.GetRawInputTrackingState(out var state), Is.True);
                    Assert.That(state.headsetActive && state.leftControllerActive && state.rightControllerActive,
                        Is.True, "A retired pending-teardown owner cannot clear the replacement.");
                }
                finally
                {
                    backend?.Stop();
                    EditorSceneManager.CloseScene(scene, true);
                    Directory.Delete(directory, true);
                }
            }
        }

        private sealed class NativeAdmissionCallbackSink : IAnchorPoseFrameCheckedReplaySink,
                                                            IAnchorPoseFrameRetiredReplaySink
        {
            private readonly IAnchorPoseFrameCheckedReplaySink _native;
            private Action _afterAdmission;
            public NativeAdmissionCallbackSink(IAnchorPoseFrameCheckedReplaySink native, Action afterAdmission)
            { _native = native; _afterAdmission = afterAdmission; }
            public bool TryApplyWorldFrame(AnchorPoseFrame frame)
            {
                if (!_native.TryApplyWorldFrame(frame)) return false;
                Action callback = _afterAdmission;
                _afterAdmission = null;
                callback?.Invoke();
                return true;
            }
            public void ClearWorldAnchor(string anchor) => _native.ClearWorldAnchor(anchor);
            public void ResetWorldAnchorReplay() => _native.ResetWorldAnchorReplay();
            public bool TryResetWorldAnchorReplay() => _native.TryResetWorldAnchorReplay();
            public bool IsReplaySinkRetired =>
                _native is IAnchorPoseFrameRetiredReplaySink retired && retired.IsReplaySinkRetired;
        }

        private static AnchorPoseFrame OriginalActorFrame(long sequence, double time) => new AnchorPoseFrame
        {
            sequence = sequence, timeSeconds = time,
            anchors = new[]
            {
                new AnchorPoseSample { anchorId = "root", worldPosition = new Vector3(1f, 0f, 2f),
                    worldRotation = Quaternion.Euler(0f, 90f, 0f) },
                new AnchorPoseSample { anchorId = "head", worldPosition = new Vector3(1f, 1.6f, 2f),
                    worldRotation = Quaternion.Euler(0f, 45f, 0f) },
                new AnchorPoseSample { anchorId = "hand_l", worldPosition = new Vector3(1.2f, 1.2f, 2.3f),
                    worldRotation = Quaternion.Euler(0f, 10f, 0f) },
                new AnchorPoseSample { anchorId = "hand_r", worldPosition = new Vector3(.8f, 1.3f, 2.4f),
                    worldRotation = Quaternion.Euler(0f, -15f, 0f) }
            }
        };

        [Test]
        public void OriginalFreeformScene_ExplicitSupportMapDrivesNativeHandTargetAndTracking()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            GameObject diagnosticRoot = null;
            SpatialBlendLocalWorld world = null;
            FreeformSceneActorRuntime runtime = null;
            try
            {
                var definition = SceneComponent<FreeformSceneActorDefinition>(scene);
                Assert.That(definition, Is.Not.Null);
                definition.SourceRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                definition.ReceiverRoot.SetPositionAndRotation(new Vector3(20f, 0f, 0f),
                    Quaternion.identity);
                diagnosticRoot = new GameObject("Explicit actor support diagnostic");
                SceneManager.MoveGameObjectToScene(diagnosticRoot, scene);
                world = diagnosticRoot.AddComponent<SpatialBlendLocalWorld>();
                // Runtime AddComponent executes Awake in Play Mode, not in this
                // EditMode fixture. Initialize the REAL main-thread dispatcher
                // lifecycle before expecting queued native targets to drain.
                InvokePrivate(world, "Awake");
                Assert.That(world.TryInitialize(out string error), Is.True, error);
                // Component oracle, not inferred MRUK/seat semantics: explicitly
                // authored region -> object -> affordance trees and a known 1.3 m
                // support-height difference are supplied to the original runtime.
                SpatialBlendObject sourceSupport = ActorDiagnosticSupport(world, diagnosticRoot.transform,
                    definition.SourceSpaceId, "source-pad", Vector3.zero);
                SpatialBlendObject receiverSupport = ActorDiagnosticSupport(world, diagnosticRoot.transform,
                    definition.ReceiverSpaceId, "receiver-pad", new Vector3(20f, 1.3f, 0f));
                foreach (string field in new[] { "_sourceRootAnchor", "_sourceLeftHandAnchor" })
                    SetActorDiagnosticScope(definition, field, sourceSupport);
                foreach (string field in new[] { "_receiverRootAnchor", "_receiverLeftHandAnchor" })
                    SetActorDiagnosticScope(definition, field, receiverSupport);
                Assert.That(FreeformSceneActorRuntime.TryStart(definition, world,
                    diagnosticRoot.transform, RootOf(sourceSupport), RootOf(receiverSupport), out runtime, out error),
                    Is.True, error);
                Assert.That(runtime.AvatarConsumer, Is.Not.Null, runtime.LastConsumerError);
                var frame = new AnchorPoseFrame
                {
                    sequence = 0, timeSeconds = 0d,
                    anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero,
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_l", worldPosition = new Vector3(.25f, 0f, 0f),
                            worldRotation = Quaternion.identity }
                    }
                };
                Assert.That(runtime.Input.TrySubmit(frame, out error), Is.True, error);
                Assert.That(runtime.Tick(0d, 10d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("hand_l", out var target), Is.True,
                    "A native support correspondence must actually deliver the body target.");
                Assert.That(target.HasHeight, Is.True);
                Assert.That(target.Weight, Is.GreaterThan(0d));
                Assert.That(target.Position.x, Is.EqualTo(20.25f).Within(.02f));
                Assert.That(target.Position.y, Is.EqualTo(1.3f).Within(.001f));
                Assert.That(target.Position.z, Is.EqualTo(0f).Within(.02f));
                var tracking = new SbTransformTrackingDelegate(runtime.LocalAvatar.LocalPresetInput);
                Assert.That(tracking.GetRawInputTrackingState(out var state), Is.True);
                Assert.That(state.leftControllerActive, Is.True);
                Assert.That(state.headsetActive || state.rightControllerActive, Is.False,
                    "A realized hand does not invent the absent head/right-hand observations.");
                // The source baseline has y=0. Seeing y=1.3 at the SDK boundary
                // establishes target consumption, not merely replayed source poses.
                Assert.That(state.leftController.position.y, Is.EqualTo(1.3f).Within(.001f));
                AssertActorVector(state.leftController.position, target.Position);
                Assert.That(runtime.LastConsumerError, Is.Null);
                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 1, timeSeconds = 1d
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(1d, 11d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("hand_l", out _), Is.False,
                    "An omitted anchor withdraws its relation on the omission frame: " +
                    "there is no tracking-gap dwell.");
                Assert.That(tracking.GetRawInputTrackingState(out var heldTracking), Is.True);
                Assert.That(heldTracking.leftControllerActive, Is.False,
                    "The omission is not a current hand observation.");

                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 2, timeSeconds = 1d + NextFrameSeconds
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(1d + NextFrameSeconds, 12d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("hand_l", out _), Is.False,
                    "A hand that stays omitted stays withdrawn.");
                Assert.That(tracking.GetRawInputTrackingState(out var cleared), Is.True);
                Assert.That(cleared.leftControllerActive, Is.False);
            }
            finally
            {
                runtime?.Stop();
                world?.Shutdown();
                if (diagnosticRoot != null) UnityEngine.Object.DestroyImmediate(diagnosticRoot);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        // The actor element is the person leaf, never a region, and it does not move.
        // Scope descent starts at the tree's navigable root, so the root crossing a region changes nothing.
        private static void AssertActorElementsArePersonLeaves(FreeformSceneActorRuntime runtime)
        {
            foreach (SpatialBlendActor actor in new[] { runtime.SourceActor, runtime.ReceiverActor })
            {
                SpatialBlendObject element = actor.Element;
                Assert.That(element, Is.Not.Null);
                Assert.That(element.Kind, Is.EqualTo(SpatialBlendObjectKind.Object), "never a Region and never an Affordance");
                    // When the scene authors a navigable root, a detached leaf (no parent) is a defect, not a
                    // pass. The leaf hangs directly under the root: a Region with footprint geometry and no parent.
                    Assert.That(element.ParentObject, Is.Not.Null, "the person leaf is never detached");
                    Assert.That(element.ParentObject.Kind, Is.EqualTo(SpatialBlendObjectKind.Region), "its parent is the navigable root, a Region");
                    Assert.That(element.ParentObject.HasFootprintGeometry, Is.True, "a navigable root has footprint geometry");
                    Assert.That(element.ParentObject.ParentObject, Is.Null, "the navigable root is the top of the tree, so the leaf hangs directly under it");
                Assert.That(element.name, Does.Contain("Owner"), "it is the runtime's own person element");
                Assert.That(actor.Registration.OwnerObject, Is.SameAs(element.Registration), "the registered native element is that person element");
            }
            Assert.That(runtime.SourceActor.Element, Is.Not.SameAs(runtime.ReceiverActor.Element));
        }

        [Test]
        public void OriginalFreeformScene_ActorElementIsThePersonLeafAndNeverFollowsARegion()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            GameObject diagnosticRoot = null;
            SpatialBlendLocalWorld world = null;
            FreeformSceneActorRuntime runtime = null;
            try
            {
                var definition = SceneComponent<FreeformSceneActorDefinition>(scene);
                definition.SourceRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                definition.ReceiverRoot.SetPositionAndRotation(new Vector3(20f, 0f, 0f), Quaternion.identity);
                diagnosticRoot = new GameObject("Actor element diagnostic");
                SceneManager.MoveGameObjectToScene(diagnosticRoot, scene);
                world = diagnosticRoot.AddComponent<SpatialBlendLocalWorld>();
                InvokePrivate(world, "Awake");
                Assert.That(world.TryInitialize(out string error), Is.True, error);
                SpatialBlendObject first = ActorDiagnosticSupport(world, diagnosticRoot.transform, definition.SourceSpaceId,
                    "walk-a", Vector3.zero);
                SpatialBlendObject second = ActorDiagnosticSupport(world, diagnosticRoot.transform, definition.SourceSpaceId,
                    "walk-b", new Vector3(10f, 0f, 0f));
                SpatialBlendObject receiverWalk = ActorDiagnosticSupport(world, diagnosticRoot.transform, definition.ReceiverSpaceId,
                    "walk-r", new Vector3(20f, 0f, 0f));
                // The scene authors a navigable root: the root anchors are mapped to a support under a Region with footprint.
                SetActorDiagnosticScope(definition, "_sourceRootAnchor", first);
                SetActorDiagnosticScope(definition, "_receiverRootAnchor", receiverWalk);
                Assert.That(FreeformSceneActorRuntime.TryStart(definition, world, diagnosticRoot.transform,
                    RootOf(first), RootOf(receiverWalk), out runtime, out error), Is.True, error);
                AssertActorElementsArePersonLeaves(runtime);
                SpatialBlendObject sourceElement = runtime.SourceActor.Element;
                SpatialBlendObject receiverElement = runtime.ReceiverActor.Element;
                AnchorPoseFrame RootAt(long sequence, Vector3 position) => new AnchorPoseFrame
                {
                    sequence = sequence, timeSeconds = sequence,
                    anchors = new[] { new AnchorPoseSample { anchorId = "root", worldPosition = position,
                        worldRotation = Quaternion.identity } }
                };
                // Inside region A, inside region B, back, and outside every region: the element never changes.
                long sequence = 0;
                foreach (Vector3 position in new[] { Vector3.zero, new Vector3(10f, 0f, 0f), Vector3.zero, new Vector3(5f, 0f, 30f) })
                {
                    Assert.That(runtime.Input.TrySubmit(RootAt(sequence++, position), out error), Is.True, error);
                    Assert.That(runtime.Tick(sequence, 10d + sequence, out error), Is.True, error);
                    Assert.That(runtime.SourceActor.Element, Is.SameAs(sourceElement));
                    Assert.That(runtime.ReceiverActor.Element, Is.SameAs(receiverElement));
                    Assert.That(runtime.SourceActor.Element, Is.Not.SameAs(first.ParentObject.ParentObject)
                        .And.Not.SameAs(second.ParentObject.ParentObject));
                    AssertActorElementsArePersonLeaves(runtime);
                }
            }
            finally
            {
                runtime?.Stop();
                world?.Shutdown();
                if (diagnosticRoot != null) UnityEngine.Object.DestroyImmediate(diagnosticRoot);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        // No registered walkable: local authored content still starts, the actors wait and say so, and they start when it exists.
        [Test]
        public void OriginalFreeformScene_ActorsWaitForTheRoomsWalkableFootprintWithoutBlockingContent()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            FieldInfo floor = typeof(FreeformSceneLocalBackend).GetField("TestOnlyFloorSource",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(floor, Is.Not.Null);
            object measured = floor.GetValue(null);
            Assert.That(measured, Is.Not.Null, "the fixture supplies a measured floor");
            try
            {
                var app = SceneComponent<FreeformCollaborationApp>(scene);
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                floor.SetValue(null, null); // no NavMesh is built and no MRUK room exists in this EditMode scene
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                    new System.Text.RegularExpressions.Regex("waiting for the room's walkable footprint"));
                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                Assert.That(backend.IsRunning, Is.True, "local authored content is not blocked");
                Assert.That(backend.ContentCoordinator, Is.Not.Null);
                Assert.That(backend.ActorRuntime, Is.Null);
                Assert.That(backend.ActorsWaitingForRoom, Is.True);
                Assert.That(backend.ActorWaitState, Does.Contain("waiting for the room's walkable footprint"));
                floor.SetValue(null, measured);
                InvokePrivate(backend, "Update");
                Assert.That(backend.ActorsWaitingForRoom, Is.False);
                Assert.That(backend.ActorRuntime, Is.Not.Null);
                AssertActorElementsArePersonLeaves(backend.ActorRuntime);
            }
            finally
            {
                floor.SetValue(null, measured);
                backend?.Stop();
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void OriginalFreeformScene_OptionalSeatChannelMapsAndOmissionReleasesTarget()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            GameObject diagnosticRoot = null;
            GameObject sourceSeatReference = null;
            GameObject receiverSeatReference = null;
            SpatialBlendLocalWorld world = null;
            FreeformSceneActorRuntime runtime = null;
            FreeformSceneActorDefinition definition = null;
            FreeformSceneActorAnchorReference previousSourceSeat = null;
            FreeformSceneActorAnchorReference previousReceiverSeat = null;
            SpatialBlendObject previousSourceRootScope = null;
            SpatialBlendObject previousReceiverRootScope = null;
            Vector3 previousSourceRootPosition = Vector3.zero;
            Quaternion previousSourceRootRotation = Quaternion.identity;
            Vector3 previousReceiverRootPosition = Vector3.zero;
            Quaternion previousReceiverRootRotation = Quaternion.identity;
            bool capturedRootTransforms = false;
            try
            {
                definition = SceneComponent<FreeformSceneActorDefinition>(scene);
                Assert.That(definition, Is.Not.Null);
                previousSourceSeat = PrivateField(definition,
                    "_sourceSeatContactAnchor") as FreeformSceneActorAnchorReference;
                previousReceiverSeat = PrivateField(definition,
                    "_receiverSeatContactAnchor") as FreeformSceneActorAnchorReference;
                previousSourceRootScope = ((FreeformSceneActorAnchorReference)PrivateField(
                    definition, "_sourceRootAnchor")).Support;
                previousReceiverRootScope = ((FreeformSceneActorAnchorReference)PrivateField(
                    definition, "_receiverRootAnchor")).Support;
                previousSourceRootPosition = definition.SourceRoot.position;
                previousSourceRootRotation = definition.SourceRoot.rotation;
                previousReceiverRootPosition = definition.ReceiverRoot.position;
                previousReceiverRootRotation = definition.ReceiverRoot.rotation;
                capturedRootTransforms = true;
                definition.SourceRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                definition.ReceiverRoot.SetPositionAndRotation(new Vector3(20f, 0f, 0f),
                    Quaternion.identity);

                diagnosticRoot = new GameObject("Optional seat support diagnostic");
                SceneManager.MoveGameObjectToScene(diagnosticRoot, scene);
                world = diagnosticRoot.AddComponent<SpatialBlendLocalWorld>();
                InvokePrivate(world, "Awake");
                Assert.That(world.TryInitialize(out string error), Is.True, error);
                SpatialBlendObject sourceSupport = ActorDiagnosticSupport(world, diagnosticRoot.transform,
                    definition.SourceSpaceId, "source-seat", Vector3.zero, .5f);
                SpatialBlendObject receiverSupport = ActorDiagnosticSupport(world, diagnosticRoot.transform,
                    definition.ReceiverSpaceId, "receiver-seat", new Vector3(20f, 1.3f, 0f), .5f);
                // Seat mapping does not authorize moving an unrelated root. Supply
                // its own explicit correspondence so the target is within the
                // receiver's unchanged native reach policy, as in the hand case.
                SetActorDiagnosticScope(definition, "_sourceRootAnchor", sourceSupport);
                SetActorDiagnosticScope(definition, "_receiverRootAnchor", receiverSupport);
                sourceSeatReference = new GameObject("temporary source seat_contact reference");
                receiverSeatReference = new GameObject("temporary receiver seat_contact reference");
                SceneManager.MoveGameObjectToScene(sourceSeatReference, scene);
                SceneManager.MoveGameObjectToScene(receiverSeatReference, scene);
                sourceSeatReference.transform.SetParent(definition.SourceRoot, false);
                receiverSeatReference.transform.SetParent(definition.ReceiverRoot, false);
                SetActorDiagnosticSeatPair(definition,
                    CreateActorDiagnosticSeatReference(sourceSeatReference.transform, sourceSupport),
                    CreateActorDiagnosticSeatReference(receiverSeatReference.transform, receiverSupport));

                Assert.That(FreeformSceneActorRuntime.TryStart(definition, world,
                    diagnosticRoot.transform, RootOf(sourceSupport), RootOf(receiverSupport), out runtime, out error),
                    Is.True, error);
                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 0,
                    timeSeconds = 0d,
                    anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero,
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "head", worldPosition = new Vector3(0f, 1.6f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_l", worldPosition = new Vector3(.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_r", worldPosition = new Vector3(-.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "seat_contact", worldPosition = new Vector3(.25f, 0f, 0f),
                            worldRotation = Quaternion.identity }
                    }
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(0d, 10d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("seat_contact", out var mappedSeat), Is.True,
                    "The optional source/receiver seat pair must reach the actual native actor target.");
                Assert.That(SpatialBlend.Internal.SpatialBlendAPI.TryReadActorState(runtime.ReceiverActor.Registration.NativeSpace,
                    runtime.ReceiverActor.Registration.NativeActor, out Vector3 receiverRoot,
                    out float receiverYaw), Is.True);
                Assert.That(receiverRoot.x, Is.EqualTo(20f).Within(.02f),
                    "The receiver root must be mapped before the body's reach-constrained target.");
                Assert.That(receiverRoot.z, Is.EqualTo(0f).Within(.02f));
                Assert.That(mappedSeat.Position.x, Is.EqualTo(20.25f).Within(.02f));
                Assert.That(mappedSeat.Position.y, Is.EqualTo(1.3f).Within(.001f));
                Assert.That(mappedSeat.Position.z, Is.EqualTo(0f).Within(.02f));
                Assert.That(runtime.LastConsumerError, Is.Null,
                    "the original local avatar consumer must admit the declared optional seat channel");
                var tracking = new SbTransformTrackingDelegate(runtime.LocalAvatar.LocalPresetInput);
                Assert.That(tracking.GetRawInputTrackingState(out var full), Is.True);
                Assert.That(full.headsetActive && full.leftControllerActive && full.rightControllerActive,
                    Is.True, "seat observation must not replace the measured SDK head/hand inputs");
                // This is the observation-consumer boundary: independently read
                // the accepted native root pose, then rigidly compose the authored
                // measurements. Root facing is not constrained to identity by a
                // seat correspondence. The seat R1 oracle above remains literal.
                Quaternion receiverRotation = Quaternion.Euler(0f, receiverYaw, 0f);
                AssertActorVector(full.headset.position,
                    receiverRoot + receiverRotation * new Vector3(0f, 1.6f, 0f));
                AssertActorVector(full.leftController.position,
                    receiverRoot + receiverRotation * new Vector3(.2f, 1.2f, 0f));
                AssertActorVector(full.rightController.position,
                    receiverRoot + receiverRotation * new Vector3(-.2f, 1.2f, 0f));

                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 1,
                    timeSeconds = 1d,
                    anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero,
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "head", worldPosition = new Vector3(0f, 1.6f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_l", worldPosition = new Vector3(.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_r", worldPosition = new Vector3(-.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity }
                    }
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(1d, 11d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("seat_contact", out _), Is.False,
                    "Omitting the optional seat withdraws its target on the omission frame " +
                    "(there is no tracking-gap dwell).");
                Assert.That(runtime.LastConsumerError, Is.Null,
                    "omitting optional seat must still deliver measured head and hands to the original avatar");
                Assert.That(tracking.GetRawInputTrackingState(out var withoutSeat), Is.True);
                Assert.That(withoutSeat.headsetActive && withoutSeat.leftControllerActive &&
                    withoutSeat.rightControllerActive, Is.True);
                Assert.That(SpatialBlend.Internal.SpatialBlendAPI.TryReadActorState(runtime.ReceiverActor.Registration.NativeSpace,
                    runtime.ReceiverActor.Registration.NativeActor, out receiverRoot,
                    out receiverYaw), Is.True);
                receiverRotation = Quaternion.Euler(0f, receiverYaw, 0f);
                AssertActorVector(withoutSeat.headset.position,
                    receiverRoot + receiverRotation * new Vector3(0f, 1.6f, 0f));
                AssertActorVector(withoutSeat.leftController.position,
                    receiverRoot + receiverRotation * new Vector3(.2f, 1.2f, 0f));
                AssertActorVector(withoutSeat.rightController.position,
                    receiverRoot + receiverRotation * new Vector3(-.2f, 1.2f, 0f));

                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 2,
                    timeSeconds = 1d + NextFrameSeconds,
                    anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero,
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "head", worldPosition = new Vector3(0f, 1.6f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_l", worldPosition = new Vector3(.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_r", worldPosition = new Vector3(-.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity }
                    }
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(1d + NextFrameSeconds, 11.5d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("seat_contact", out _), Is.False,
                    "A seat that stays omitted stays withdrawn.");
                Assert.That(tracking.GetRawInputTrackingState(out withoutSeat), Is.True);
                Assert.That(withoutSeat.headsetActive && withoutSeat.leftControllerActive &&
                    withoutSeat.rightControllerActive, Is.True,
                    "Releasing the omitted seat must still deliver measured head and hands.");

                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 3,
                    timeSeconds = 2d,
                    anchors = new[]
                    {
                        new AnchorPoseSample { anchorId = "root", worldPosition = Vector3.zero,
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "head", worldPosition = new Vector3(0f, 1.6f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_l", worldPosition = new Vector3(.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "hand_r", worldPosition = new Vector3(-.2f, 1.2f, 0f),
                            worldRotation = Quaternion.identity },
                        new AnchorPoseSample { anchorId = "seat_contact", worldPosition = new Vector3(.25f, 0f, 0f),
                            worldRotation = Quaternion.identity }
                    }
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(2d, 12d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("seat_contact", out var freshSeat), Is.True,
                    "The empty-frame release check needs a fresh five-channel native seat target.");

                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 4,
                    timeSeconds = 3d,
                    anchors = Array.Empty<AnchorPoseSample>()
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(3d, 13d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("seat_contact", out _), Is.False,
                    "An empty frame withdraws seat_contact on that frame (the tracking-gap dwell is deleted).");
                Assert.That(tracking.GetRawInputTrackingState(out var heldEmpty), Is.True);
                Assert.That(heldEmpty.headsetActive || heldEmpty.leftControllerActive ||
                    heldEmpty.rightControllerActive, Is.False,
                    "An empty frame is not a current SDK observation. Held targets are not reported as measured channels.");

                Assert.That(runtime.Input.TrySubmit(new AnchorPoseFrame
                {
                    sequence = 5,
                    timeSeconds = 3d + NextFrameSeconds,
                    anchors = Array.Empty<AnchorPoseSample>()
                }, out error), Is.True, error);
                Assert.That(runtime.Tick(3d + NextFrameSeconds, 14d, out error), Is.True, error);
                Assert.That(runtime.Targets.TryGetTarget("seat_contact", out _), Is.False,
                    "A seat that stays omitted stays withdrawn.");
                Assert.That(runtime.LastConsumerError, Is.Null);
                Assert.That(tracking.GetRawInputTrackingState(out var empty), Is.True);
                Assert.That(empty.headsetActive || empty.leftControllerActive || empty.rightControllerActive,
                    Is.False, "Past the dwell, the empty snapshot clears every measured SDK channel.");
            }
            finally
            {
                runtime?.Stop();
                if (definition != null)
                    SetActorDiagnosticSeatPair(definition, previousSourceSeat, previousReceiverSeat);
                if (capturedRootTransforms)
                {
                    SetActorDiagnosticScope(definition, "_sourceRootAnchor", previousSourceRootScope);
                    SetActorDiagnosticScope(definition, "_receiverRootAnchor", previousReceiverRootScope);
                    definition.SourceRoot.SetPositionAndRotation(previousSourceRootPosition,
                                                                 previousSourceRootRotation);
                    definition.ReceiverRoot.SetPositionAndRotation(previousReceiverRootPosition,
                                                                   previousReceiverRootRotation);
                }
                world?.Shutdown();
                if (sourceSeatReference != null) UnityEngine.Object.DestroyImmediate(sourceSeatReference);
                if (receiverSeatReference != null) UnityEngine.Object.DestroyImmediate(receiverSeatReference);
                if (diagnosticRoot != null) UnityEngine.Object.DestroyImmediate(diagnosticRoot);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        // The Region at the top of an ActorDiagnosticSupport tree: the navigable root handed to the actor runtime.
        private static SpatialBlendObject RootOf(SpatialBlendObject support) => support.ParentObject.ParentObject;

        private static SpatialBlendObject ActorDiagnosticSupport(SpatialBlendLocalWorld world,
            Transform parent, string spaceId, string id, Vector3 position, float halfExtent = 1f)
        {
            // The region is the navigable root of its tree: scope descent starts there, so it has a
            // footprint of its own, and the actor's person leaf hangs under it.
            SpatialBlendObject region = ActorDiagnosticNode(world, parent, spaceId,
                id + "-region", SpatialBlendObjectKind.Region, null, position);
            SpatialBlendObject item = ActorDiagnosticNode(world, region.transform, spaceId,
                id + "-object", SpatialBlendObjectKind.Object, region, position);
            SpatialBlendObject support = ActorDiagnosticNode(world, item.transform, spaceId,
                id, SpatialBlendObjectKind.Affordance, item, position, register: false);
            Assert.That(support.TrySetSupportPolygon(new[]
            {
                new Vector3(-halfExtent, 0f, -halfExtent), new Vector3(halfExtent, 0f, -halfExtent),
                new Vector3(halfExtent, 0f, halfExtent), new Vector3(-halfExtent, 0f, halfExtent)
            }, out string error), Is.True, error);
            Assert.That(support.TryRegister(out error), Is.True, error);
            return support;
        }

        private static SpatialBlendObject ActorDiagnosticNode(SpatialBlendLocalWorld world,
            Transform parent, string spaceId, string id, SpatialBlendObjectKind kind,
            SpatialBlendObject typedParent, Vector3 worldPosition, bool register = true)
        {
            var root = new GameObject(id);
            root.transform.SetParent(parent, false);
            // A region's footprint is baked in world coordinates: its frame is the world origin.
            root.transform.position = kind == SpatialBlendObjectKind.Region ? Vector3.zero : worldPosition;
            var node = root.AddComponent<SpatialBlendObject>();
            Assert.That(node.TryConfigure(world, spaceId, id, typedParent, kind, out string error), Is.True, error);
            // An object has a footprint, and the actor that stands inside it has
            // entered it. The object's footprint is wider than its support, so the actor at the support's centre is
            // inside, and the object is both the deepest container and the Spacing referent the root relates to.
            bool footprint = kind != SpatialBlendObjectKind.Affordance;
            if (footprint)
            {
                var box = root.AddComponent<BoxCollider>();
                box.size = kind == SpatialBlendObjectKind.Region ? new Vector3(8f, 2f, 8f) : new Vector3(4f, 2f, 4f);
                box.center = (kind == SpatialBlendObjectKind.Region ? worldPosition : Vector3.zero) + new Vector3(0f, 1f, 0f);
            }
            Assert.That(node.TrySetColliderGeometryEnabled(footprint, out error), Is.True, error);
            if (register) Assert.That(node.TryRegister(out error), Is.True, error);
            return node;
        }

        private static void SetActorDiagnosticScope(FreeformSceneActorDefinition definition,
            string anchorField, SpatialBlendObject scope)
        {
            object anchor = PrivateField(definition, anchorField);
            Assert.That(anchor, Is.Not.Null);
            anchor.GetType().GetField("_support", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(anchor, scope);
        }

        private static FreeformSceneActorAnchorReference CreateActorDiagnosticSeatReference(
            Transform reference, SpatialBlendObject scope)
        {
            var value = new FreeformSceneActorAnchorReference();
            value.GetType().GetField("_anchorId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(value, FreeformSceneActorDefinition.SeatContactAnchorId);
            value.GetType().GetField("_reference", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(value, reference);
            value.GetType().GetField("_support", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(value, scope);
            return value;
        }

        private static void SetActorDiagnosticSeatPair(FreeformSceneActorDefinition definition,
            FreeformSceneActorAnchorReference source, FreeformSceneActorAnchorReference receiver)
        {
            definition.GetType().GetField("_sourceSeatContactAnchor",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(definition, source);
            definition.GetType().GetField("_receiverSeatContactAnchor",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(definition, receiver);
        }

        private static void AssertActorVector(Vector3 actual, Vector3 expected) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-4f));

        private static void WriteSyntheticCommittedActorRecording(string directory,
            IEnumerable<AnchorPoseFrame> frames, out string jsonl, out string manifest)
        {
            jsonl = Path.Combine(directory, "synthetic-actor.jsonl");
            manifest = Path.Combine(directory, "synthetic-actor.manifest.json");
            File.WriteAllText(jsonl, string.Join("\n", frames.Select(frame => JsonUtility.ToJson(frame))) + "\n");
            byte[] bytes = File.ReadAllBytes(jsonl);
            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            // This loader currently accepts the canonical S6-compatible yaw-only
            // orientation contract. These generated Unity poses satisfy that
            // contract but are NOT a real S6/COUCH recording or dataset acceptance.
            File.WriteAllText(manifest,
                "{\"provenance\":{\"source_kind\":\"synthetic-component-fixture\"," +
                "\"orientation_mode\":\"s6-canonical-spatial-anchor-yaw-only: Unity yaw = pi/2 - S6 yaw; " +
                "raw tracker quaternions retained as evidence, not relabeled as Unity anchor rotation\"}," +
                "\"output\":{\"publication_state\":\"committed\",\"jsonl_path\":\"" +
                jsonl.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"jsonl_byte_count\":" +
                bytes.Length + ",\"jsonl_sha256\":\"" + hash +
                "\",\"anchor_pose_schema\":1,\"frame\":\"unity-world\"}}");
        }

        [Test]
        public void OriginalFreeformScene_MrukGeometryUsesReceiverChartAndExactRoomLifetime()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            FreeformSceneLocalBackend backend = null;
            MRUK mruk = null;
            GameObject roomAGo = null;
            GameObject roomBGo = null;
            try
            {
                backend = SceneComponent<FreeformSceneLocalBackend>(scene);
                var app = SceneComponent<FreeformCollaborationApp>(scene);
                var definition = SceneComponent<FreeformSceneRoomGeometryDefinition>(scene);
                mruk = definition.Mruk;
                Assert.That(mruk, Is.Not.Null);
                Assert.That(mruk.Rooms, Is.Empty,
                    "This EditMode fixture supplies SDK primitives, not device/prefab loading or a real room scan.");
                Assert.That(app.TryStartFoundationLocal(out string error), Is.True, error);
                var runtime = backend.RoomGeometryRuntime;
                Assert.That(runtime.IsWaitingForRoom, Is.True);
                LocalSceneGeometryScope scope = runtime.Scope;
                Assert.That(scope.NativeSpace, Is.EqualTo(
                    backend.ActorRuntime.ReceiverActor.Registration.NativeSpace));
                Assert.That(scope.NativeSpace, Is.Not.EqualTo(
                    backend.ActorRuntime.SourceActor.Registration.NativeSpace));

                roomAGo = new GameObject("synthetic SDK room A");
                roomBGo = new GameObject("synthetic SDK room B");
                roomAGo.SetActive(false);
                roomBGo.SetActive(false);
                SceneManager.MoveGameObjectToScene(roomAGo, scene);
                SceneManager.MoveGameObjectToScene(roomBGo, scene);
                var roomA = roomAGo.AddComponent<MRUKRoom>();
                var roomB = roomBGo.AddComponent<MRUKRoom>();
                // A real MRUK room has a measured floor. Without one, the adapter's label-free floor/ceiling rule takes the
                // anchor's own horizontal plane (below) for the room's floor and does not stage it.
                SetMrukMeasuredFloor(roomA);
                SetMrukMeasuredFloor(roomB);
                // An MRUK room is yawed and uniformly scaled; its plane anchors are tilted (normal along local Z). The
                // core is planar, so the adapter places a volume by the anchor's floor frame (position and yaw): an
                // arbitrary tilt or shear of a box has no ground-frame box, and is not what MRUK delivers.
                roomAGo.transform.SetPositionAndRotation(new Vector3(3f, 2f, -4f),
                    Quaternion.Euler(0f, 31f, 0f));
                roomAGo.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
                var anchorGo = new GameObject("synthetic SDK measured primitive");
                anchorGo.transform.SetParent(roomAGo.transform, false);
                anchorGo.transform.localPosition = new Vector3(.3f, .2f, -.1f);
                anchorGo.transform.localRotation = Quaternion.Euler(-90f, -23f, 0f);
                anchorGo.transform.localScale = new Vector3(.8f, 1.2f, .9f);
                var anchor = anchorGo.AddComponent<MRUKAnchor>();
                SetMrukPrimitive(anchor, "Label", MRUKAnchor.SceneLabels.COUCH);
                SetMrukPrimitive(anchor, "VolumeBounds", (Bounds?)new Bounds(Vector3.zero,
                    new Vector3(2f, 1f, 3f)));
                // MRUK puts every anchor in its room before it raises the room's events; the adapter does not stage a
                // UUID-less anchor before that, and keys it by its pose in the room (StableKey), not its instance id.
                SetMrukPrimitive(anchor, "Room", roomA);
                roomA.Anchors.Add(anchor);
                mruk.Rooms.Add(roomA);
                mruk.RoomCreatedEvent.Invoke(roomA);
                Assert.That(runtime.CurrentRoom, Is.SameAs(roomA), runtime.LastError);
                int key = MRUKSceneGeometryAdapter.StableKey(anchor);
                Assert.That(scope.TryGetNativeGeometry(key, out long volume,
                    out long absentPlane), Is.True, scope.LastError);
                Assert.That(absentPlane, Is.Zero);
                // Full parent/child affine composition, including induced shear.
                Vector3 volumeContact = anchorGo.transform.TransformPoint(new Vector3(1f, 0f, 0f));
                AssertNativeObservedContact(backend.LocalWorld, scope, volume,
                    volumeContact, volumeContact, true);
                Assert.That(backend.LocalWorld.TryCreateSceneGeometryScope(scope.SpaceId,
                    "competing-observer", out var competitor, out error), Is.True, error);
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(anchor, out var descriptor), Is.True);
                Assert.That(competitor.UpsertLocalSceneGeometry(descriptor), Is.False,
                    "One native receiver space cannot contain duplicate ownership of the same source key.");
                Assert.That(competitor.TryClose(out error), Is.True, error);

                // Replace volume with an irregular measured XY plane on the same
                // real MRUK anchor. An intentionally larger PlaneRect must not win.
                SetMrukPrimitive(anchor, "VolumeBounds", null);
                SetMrukPrimitive(anchor, "PlaneRect", (Rect?)new Rect(-1f, -1f, 2f, 2f));
                SetMrukPrimitive(anchor, "PlaneBoundary2D", new List<Vector2>
                {
                    new Vector2(-.4f, -.2f), new Vector2(.6f, -.2f), new Vector2(.1f, .7f)
                });
                roomA.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(scope.TryGetNativeGeometry(key, out long parent,
                    out long plane), Is.True, scope.LastError);
                Assert.That(parent, Is.Not.EqualTo(volume));
                Assert.That(plane, Is.GreaterThan(0));
                Assert.That((bool)Api("RemoveLocalElement", scope.NativeSpace, volume), Is.False,
                    "Successful replacement must already have retired the exact former tree.");
                AssertMeasuredPlaneProjection(backend.LocalWorld, scope, plane, anchorGo.transform);

                anchorGo.transform.position += new Vector3(.7f, -.2f, .5f);
                roomA.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(scope.TryGetNativeGeometry(key, out _,
                    out long movedPlane), Is.True, scope.LastError);
                Assert.That(movedPlane, Is.Not.EqualTo(plane));
                Assert.That((bool)Api("RemoveLocalElement", scope.NativeSpace, plane), Is.False);
                AssertMeasuredPlaneProjection(backend.LocalWorld, scope, movedPlane, anchorGo.transform);

                // Match the SDK's real event order: removed notification precedes
                // Rooms.Remove. Tick must not re-adopt that cached removed room.
                mruk.RoomRemovedEvent.Invoke(roomA);
                Assert.That(runtime.CurrentRoom, Is.Null);
                Assert.That(scope.TryGetNativeGeometry(key, out _, out _), Is.False);
                Assert.That((bool)Api("RemoveLocalElement", scope.NativeSpace, movedPlane), Is.False,
                    "Room removal must retire native geometry, not only forget its managed source key.");
                Assert.That(runtime.Tick(out error), Is.True, error);
                Assert.That(runtime.IsWaitingForRoom, Is.True);
                roomA.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(scope.TryGetNativeGeometry(key, out _, out _), Is.False);
                mruk.Rooms.Remove(roomA);
                anchorGo.transform.SetParent(roomBGo.transform, true);
                SetMrukPrimitive(anchor, "Room", roomB);
                roomB.Anchors.Add(anchor);
                mruk.Rooms.Add(roomB);
                mruk.RoomCreatedEvent.Invoke(roomB);
                Assert.That(runtime.CurrentRoom, Is.SameAs(roomB));
                Assert.That(scope.TryGetNativeGeometry(key, out _, out long roomBPlane), Is.True);
                roomA.AnchorRemovedEvent.Invoke(anchor);
                Assert.That(scope.TryGetNativeGeometry(key, out _, out long stillRoomBPlane), Is.True);
                Assert.That(stillRoomBPlane, Is.EqualTo(roomBPlane));

                backend.Stop();
                Assert.That(scope.IsLive, Is.False);
                Assert.That(app.TryStartFoundationLocal(out error), Is.True, error);
                LocalSceneGeometryScope replacement = backend.RoomGeometryRuntime.Scope;
                Assert.That(replacement, Is.Not.SameAs(scope));
                Assert.That(replacement.TryGetNativeGeometry(key, out _, out long freshPlane), Is.True);
                runtime.Stop();
                Assert.That(scope.RemoveLocalSceneGeometry(key), Is.False);
                Assert.That(replacement.TryGetNativeGeometry(key, out _, out long preservedPlane), Is.True);
                Assert.That(preservedPlane, Is.EqualTo(freshPlane),
                    "A retired runtime/scope cannot remove observations from the replacement local world.");
            }
            finally
            {
                backend?.Stop();
                if (mruk != null) mruk.Rooms.Clear();
                if (roomAGo != null) UnityEngine.Object.DestroyImmediate(roomAGo);
                if (roomBGo != null) UnityEngine.Object.DestroyImmediate(roomBGo);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void SetMrukPrimitive(MRUKAnchor anchor, string property, object value) =>
            typeof(MRUKAnchor).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)
                .SetValue(anchor, value);

        // An 8 m x 8 m floor plane (normal along local Z, as MRUK's plane anchors) set as the room's FloorAnchor.
        private static void SetMrukMeasuredFloor(MRUKRoom room)
        {
            var floorGo = new GameObject("synthetic SDK measured floor");
            floorGo.transform.SetParent(room.transform, false);
            floorGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var floor = floorGo.AddComponent<MRUKAnchor>();
            SetMrukPrimitive(floor, "Label", MRUKAnchor.SceneLabels.FLOOR);
            SetMrukPrimitive(floor, "PlaneBoundary2D", new List<Vector2>
            {
                new Vector2(-4f, -4f), new Vector2(4f, -4f), new Vector2(4f, 4f), new Vector2(-4f, 4f)
            });
            SetMrukPrimitive(floor, "Room", room);
            typeof(MRUKRoom).GetProperty("FloorAnchor", BindingFlags.Instance | BindingFlags.Public).SetValue(room, floor);
        }

        private static void AssertMeasuredPlaneProjection(SpatialBlendLocalWorld world,
            LocalSceneGeometryScope scope, long plane, Transform chart)
        {
            Vector3 a = chart.TransformPoint(new Vector3(-.4f, -.2f, 0f));
            Vector3 b = chart.TransformPoint(new Vector3(.6f, -.2f, 0f));
            Vector3 c = chart.TransformPoint(new Vector3(.1f, .7f, 0f));
            Vector3 center = (a + b + c) / 3f;
            AssertNativeObservedContact(world, scope, plane, center, center, true);
            Vector3 midpoint = (a + b) * .5f;
            Vector3 edge = (b - a).normalized;
            Vector3 outward = Vector3.ProjectOnPlane(midpoint - c, edge).normalized;
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            // The exact nearest point is the measured triangle's AB midpoint,
            // not the larger Rect, chart origin, or a flattened world XY plane.
            // The core owns the ground image (one floor contract): the nearest point is the
            // triangle's AB midpoint on the ground, and the planar contact keeps the observation's own height.
            Vector3 probe = midpoint + outward * .2f + normal * .3f;
            AssertNativeObservedContact(world, scope, plane, probe,
                new Vector3(midpoint.x, probe.y, midpoint.z), false);
        }

        // ABI 4: scope belongs to the relation family, so a probe can no
        // longer ask "the contact on THIS referent" by binding an actor to it. sb_evaluate_relation is that question
        // exactly: the pure Extract_p of one (kernel, anchor pose, referent), with no binding and no scope.
        private static RelationEvaluation EvaluateAffordanceContact(long space, long referent, Vector3 observation)
        {
            var query = new RelationQuery
            {
                struct_size = (uint)Marshal.SizeOf<RelationQuery>(), version = 1,
                anchor_x = observation.x, anchor_y = observation.z, anchor_z = observation.y, anchor_yaw = 0d,
                referent = referent, scope_root = 0, kernel_type = 2, position_dimension = 3, explicit_scope = 0
            };
            var evaluation = new RelationEvaluation { struct_size = (uint)Marshal.SizeOf<RelationEvaluation>() };
            Assert.That(sb_evaluate_relation(space, ref query, ref evaluation), Is.EqualTo(1),
                "The native pure relation evaluation was refused.");
            return evaluation;
        }

        private static void AssertNativeObservedContact(SpatialBlendLocalWorld world,
            LocalSceneGeometryScope scope, long referent, Vector3 observation,
            Vector3 expectedContact, bool atContact)
        {
            RelationEvaluation relation = EvaluateAffordanceContact(scope.NativeSpace, referent, observation);
            Assert.That(relation.has_contact, Is.EqualTo(1));
            Assert.That(relation.contact_point_world_x, Is.EqualTo(expectedContact.x).Within(1e-4));
            Assert.That(relation.contact_point_world_y, Is.EqualTo(expectedContact.z).Within(1e-4));
            Assert.That(relation.contact_point_world_z, Is.EqualTo(expectedContact.y).Within(1e-4));
            if (atContact) Assert.That(relation.proximity, Is.EqualTo(1d).Within(1e-5));
            else Assert.That(relation.proximity, Is.LessThan(.99d));
        }

        private static void AssertNativeSurfaceContact(SpatialBlendLocalWorld world,
            SpatialBlendObject support, Vector3 expectedContact)
        {
            Assert.That(support, Is.Not.Null);
            Assert.That(support.IsRegistered, Is.True);
            RelationEvaluation relation = EvaluateAffordanceContact(support.Registration.NativeSpace,
                support.Registration.NativeElement, expectedContact);
            Assert.That(relation.has_contact, Is.EqualTo(1));
            Assert.That(relation.proximity, Is.EqualTo(1d).Within(1e-5));
            Assert.That(relation.contact_point_world_x, Is.EqualTo(expectedContact.x).Within(1e-4));
            Assert.That(relation.contact_point_world_y, Is.EqualTo(expectedContact.z).Within(1e-4));
            Assert.That(relation.contact_point_world_z, Is.EqualTo(expectedContact.y).Within(1e-4));
        }

        internal static Scene OpenOriginalFreeformSceneForNativeTest()
        {
            Scene scene = EditorSceneManager.OpenScene(
                "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity", OpenSceneMode.Additive);
            try
            {
                // Loading an authored scene in EditMode does not call Awake.
                // Invoke the real actor lifecycle before the production backend
                // starts; do not weaken or replace its main-thread admission.
                foreach (SpatialBlendActor actor in scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<SpatialBlendActor>(true)))
                    InvokePrivate(actor, "Awake");
                return scene;
            }
            catch
            {
                EditorSceneManager.CloseScene(scene, true);
                throw;
            }
        }

        private static T SceneComponent<T>(Scene scene) where T : Component => scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).SingleOrDefault();

        private static ControlPanel EnabledSceneControlPanel(Scene scene, FreeformCollaborationApp app) =>
            scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<ControlPanel>(true))
                .Single(panel => panel.enabled && ReferenceEquals(PrivateField(panel, "_freeformApp"), app));

        private static void AssertRefreshPropagatesThroughNativeScopedProbe(
            SpatialBlendLocalWorld world, SpatialBlendObject authoredRoot)
        {
            // Diagnostic-only: this Unity child does not replace authored MRUK or
            // collider geometry. Its separate native child is a local Containment
            // polytope, deliberately not a baked WorldSurface or Navigable polygon.
            // Native grounding composes this local box through authoredRoot's
            // element tree. All diagnostic native setup is complete before the
            // baseline; after it, the only geometry write is authoredRoot.TryRefresh().
            // ABI 4: the probe asks the pure relation evaluation for the contact on the box (the question a scoped
            // binding used to answer), because scope belongs to the relation family.
            GameObject supportObject = new GameObject("Freeform Refresh Support Probe");
            Vector3 originalPosition = authoredRoot.transform.position;
            long diagnosticSpace = 0;
            long diagnosticElement = 0;
            try
            {
                supportObject.transform.SetParent(authoredRoot.transform, false);
                supportObject.transform.localPosition = new Vector3(.1f, 0, 0);
                diagnosticSpace = authoredRoot.Registration.NativeSpace;
                diagnosticElement = Convert.ToInt64(Api("AddElement", diagnosticSpace,
                    authoredRoot.Registration.NativeElement));
                Assert.That(diagnosticElement, Is.GreaterThan(0),
                    "The local diagnostic child element was not admitted.");
                Api("SetElementTransform", diagnosticSpace, diagnosticElement,
                    Matrix4x4.TRS(supportObject.transform.localPosition,
                        supportObject.transform.localRotation, supportObject.transform.localScale));
                // Containment is the local polytope path; neither this nor its
                // transform is rewritten after the baseline.
                Api("SetElementBox", diagnosticSpace, diagnosticElement,
                    EnumValue("SbGeometryRole", 2), new Vector3(-.05f, -.02f, -.05f),
                    new Vector3(.05f, .02f, .05f));

                Matrix4x4 oldSupportWorld = supportObject.transform.localToWorldMatrix;
                Vector3 expectedBefore = supportObject.transform.TransformPoint(new Vector3(.05f, 0, 0));
                RelationEvaluation before = EvaluateAffordanceContact(diagnosticSpace, diagnosticElement, expectedBefore);
                Assert.That(before.has_contact, Is.EqualTo(1));
                Assert.That(before.proximity, Is.EqualTo(1d).Within(1e-6));
                Assert.That(before.contact_point_world_x, Is.EqualTo(expectedBefore.x).Within(1e-4));
                Assert.That(before.contact_point_world_y, Is.EqualTo(expectedBefore.z).Within(1e-4));
                Assert.That(before.contact_point_world_z, Is.EqualTo(expectedBefore.y).Within(1e-4));

                // Deliberately move in a literal world direction. The stale-native
                // expectation below is derived through the pre-move local frame, so
                // this remains valid if the authored root has non-identity yaw.
                Vector3 worldDisplacement = Vector3.right * .37f;
                authoredRoot.transform.position = originalPosition + worldDisplacement;
                Vector3 expectedAfter = supportObject.transform.TransformPoint(new Vector3(.05f, 0, 0));
                Assert.That(expectedAfter.x - expectedBefore.x, Is.EqualTo(worldDisplacement.x).Within(1e-5));
                Assert.That(expectedAfter.y - expectedBefore.y, Is.EqualTo(worldDisplacement.y).Within(1e-5));
                Assert.That(expectedAfter.z - expectedBefore.z, Is.EqualTo(worldDisplacement.z).Within(1e-5));
                // Negative control: changing only Unity transforms is not a
                // geometry refresh. Native grounding must still project to the
                // closest point on the old local box before TryRefresh(). This
                // explicit local-frame clamp covers the real authored transform
                // chain rather than assuming world +X is local +X.
                RelationEvaluation stale = EvaluateAffordanceContact(diagnosticSpace, diagnosticElement, expectedAfter);
                Vector3 staleLocal = oldSupportWorld.inverse.MultiplyPoint3x4(expectedAfter);
                Vector3 expectedStale = oldSupportWorld.MultiplyPoint3x4(new Vector3(
                    Mathf.Clamp(staleLocal.x, -.05f, .05f),
                    Mathf.Clamp(staleLocal.y, -.02f, .02f),
                    Mathf.Clamp(staleLocal.z, -.05f, .05f)));
                Assert.That(stale.contact_point_world_x, Is.EqualTo(expectedStale.x).Within(1e-4));
                Assert.That(stale.contact_point_world_y, Is.EqualTo(expectedStale.z).Within(1e-4));
                Assert.That(stale.contact_point_world_z, Is.EqualTo(expectedStale.y).Within(1e-4));
                Assert.That(Vector3.Distance(new Vector3((float)stale.contact_point_world_x,
                    (float)stale.contact_point_world_z, (float)stale.contact_point_world_y), expectedAfter),
                    Is.GreaterThan(.1f));

                Assert.That(authoredRoot.TryRefresh(out string error), Is.True, error);
                RelationEvaluation after = EvaluateAffordanceContact(diagnosticSpace, diagnosticElement, expectedAfter);
                Assert.That(after.has_contact, Is.EqualTo(1));
                Assert.That(after.proximity, Is.EqualTo(1d).Within(1e-6));
                Assert.That(after.contact_point_world_x,
                    Is.EqualTo(expectedAfter.x).Within(1e-4),
                    "The real root refresh must be visible through native scoped geometry.");
                Assert.That(after.contact_point_world_y, Is.EqualTo(expectedAfter.z).Within(1e-4));
                Assert.That(after.contact_point_world_z, Is.EqualTo(expectedAfter.y).Within(1e-4));
                Assert.That(after.contact_point_world_x - before.contact_point_world_x,
                    Is.EqualTo(worldDisplacement.x).Within(1e-4));
                Assert.That(after.contact_point_world_y - before.contact_point_world_y,
                    Is.EqualTo(worldDisplacement.z).Within(1e-4));
                Assert.That(after.contact_point_world_z - before.contact_point_world_z,
                    Is.EqualTo(worldDisplacement.y).Within(1e-4));
            }
            finally
            {
                try
                {
                    authoredRoot.transform.position = originalPosition;
                    bool elementRetired = diagnosticElement <= 0 ||
                        (bool)Api("RemoveLocalElement", diagnosticSpace, diagnosticElement);
                    Assert.That(elementRetired, Is.True,
                        "The temporary local diagnostic child must retire before the scene closes.");
                }
                finally
                {
                    // Never let a diagnostic retirement assertion leak Unity probe
                    // children into the next native fixture case.
                    if (supportObject != null) UnityEngine.Object.DestroyImmediate(supportObject);
                }
            }
        }

        [Test]
        public void TableAnchorHostSharesTheChosenTableReceiverLinksTheSimilarOneAndUnshareReleasesTheLink()
        {
            var host = CreateManager("table-host");
            var client = CreateManager("table-client");
            int port = FreePort();
            host.Host(port);
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);

            // Two sibling anchors of one room may only touch, so the two tables of a peer stand apart
            // (a real room never has two tables in the same place).
            GameObject hostSmall = ActiveAnchor("host-small-table");
            GameObject hostBig = ActiveAnchor("host-big-table");
            GameObject clientSmall = ActiveAnchor("client-small-table");
            GameObject clientBig = ActiveAnchor("client-big-table");
            hostSmall.transform.position = new Vector3(-2f, 0f, 0f);
            hostBig.transform.position = new Vector3(2f, 0f, 0f);
            clientSmall.transform.position = new Vector3(-2f, 0f, 0f);
            clientBig.transform.position = new Vector3(2f, 0f, 0f);
            // The scene-geometry adapter owns one element tree per anchor. The shared
            // TABLE is attached to it; nothing else may create an element for the anchor.
            RegisterTableGeometry(host, hostSmall, 6001, new Vector3(1f, 0.74f, 0.6f));
            RegisterTableGeometry(host, hostBig, 6002, new Vector3(1.2f, 0.74f, 0.8f));
            RegisterTableGeometry(client, clientSmall, 6003, new Vector3(1f, 0.74f, 0.6f));
            RegisterTableGeometry(client, clientBig, 6004, new Vector3(1.3f, 0.74f, 0.85f));
            var hostApp = ActiveApp("host-app", Catalog(
                Record(hostSmall, 1f, 6001, new Vector3(1f, 0.74f, 0.6f)),
                Record(hostBig, 4f, 6002, new Vector3(1.2f, 0.74f, 0.8f))));
            var clientApp = ActiveApp("client-app", Catalog(
                Record(clientSmall, 1f, 6003, new Vector3(1f, 0.74f, 0.6f)),
                Record(clientBig, 8f, 6004, new Vector3(1.3f, 0.74f, 0.85f))));
            SetAppManager(hostApp, host);
            SetAppManager(clientApp, client);
            Invoke(host, "RegisterApp", hostApp);
            Invoke(client, "RegisterApp", clientApp);

            // Nothing is shared by itself: the room's tables are local obstacle geometry only.
            Pump(100, host, client);
            Assert.That(hostBig.GetComponent<SbEntity>(), Is.Null);
            Assert.That(clientBig.GetComponent<SbEntity>(), Is.Null);

            // The host's user shares the big table from the list. The client's most similar table (not "the biggest") is registered under
            // the sharer's key and linked; its other table is untouched.
            Assert.That(hostApp.TryShareAnchor(hostApp.ShareableAnchors().Single(r => r.Anchor == hostBig)), Is.True);
            PumpUntil(() => clientBig.GetComponent<SbMRUKAnchorEntity>() != null &&
                            clientBig.GetComponent<SbMRUKAnchorEntity>().IsAttached(), host, client);
            SbMRUKAnchorEntity clientTable = clientBig.GetComponent<SbMRUKAnchorEntity>();
            long clientTableId = clientTable.ID;
            Assert.That(clientSmall.GetComponent<SbEntity>(), Is.Null);
            Assert.That(client.TryGetLocalSceneGeometryElements(6004, out long clientParent, out long clientPlane),
                Is.True);
            Assert.That(clientTableId, Is.EqualTo(clientParent),
                "The receiver's TABLE entity is attached to the adapter's element, not a second one.");
            long tableSharedId = SharedAnchorName.SharedId(SbMRUKAnchorEntity.TableLabel, clientTable.ShareKey);
            Assert.That(clientTable.ShareKey, Is.EqualTo(hostBig.GetComponent<SbMRUKAnchorEntity>().ShareKey));
            PumpUntil(() => { clientApp.PollPendingSeatShares(); hostApp.PollPendingSeatShares(); return host.DeferredShareCount == 0 && client.DeferredShareCount == 0; }, host, client);
            Pump(300, host, client);
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, tableSharedId), Is.EqualTo(clientParent));
            Assert.That(clientPlane, Is.GreaterThan(0), "The adapter measured a plane for this table.");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID,
                SpatialAuthoringCompiler.AffordanceSharedId(tableSharedId, 0)), Is.EqualTo(clientPlane),
                "The adapter's Surface plane is the Tabletop affordance; no second Surface was added.");

            SbMRUKAnchorEntity hostTable = hostBig.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(hostTable, Is.Not.Null);
            PumpUntil(() => hostTable.IsAttached(), host, client);
            Assert.That(host.TryGetLocalSceneGeometryElements(6002, out long hostParent, out _), Is.True);
            Assert.That(hostTable.ID, Is.EqualTo(hostParent));
            Assert.That(hostSmall.GetComponent<SbEntity>(), Is.Null);
            Assert.That(clientApp.SharedBoxes, Is.Empty, "no box: the room has a table");
            Assert.That(clientTable.IsAttached(), Is.True);
            Assert.That(clientTable.ID, Is.EqualTo(clientTableId));
            Assert.That(CountAnchors("TABLE"), Is.EqualTo(2),
                "The reconstructed table clone must be dropped; each peer keeps its own table.");

            // The sharer unshares. The core tells the client (link-removed, PeerUnshared, ABI 4.9) and the app releases its linked table; the
            // element stays, as a local obstacle again.
            Assert.That(hostApp.TryUnshareAnchor(hostApp.ShareableAnchors().Single(r => r.Anchor == hostBig)), Is.True);
            PumpUntil(() => clientBig.GetComponent<SbMRUKAnchorEntity>() == null, host, client);
            Pump(200, host, client);
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID, tableSharedId), Is.LessThanOrEqualTo(0), "nothing of the registration is left on the host's element");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID, SpatialAuthoringCompiler.RegionSharedId(tableSharedId)), Is.LessThanOrEqualTo(0));
            Assert.That(clientApp.ShareStateOf(clientApp.ShareableAnchors().Single(r => r.Anchor == clientBig)), Is.EqualTo(AnchorShareState.None));
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, tableSharedId), Is.LessThanOrEqualTo(0), "and none on the client's");
            Assert.That(clientBig, Is.Not.Null, "the room's own table stays");
            Assert.That(clientApp.ShareStateOf(clientApp.ShareableAnchors().Single(r => r.Anchor == clientBig)), Is.EqualTo(AnchorShareState.None));
            Pump(200, host, client);
            Assert.That(client.TryGetLocalSceneGeometryElements(6004, out long stillThere, out _), Is.True, "its geometry is still registered as an obstacle");
            Assert.That(stillThere, Is.GreaterThan(0));
        }

        // Two real managers over loopback: the cross-device path. The core's resolve records the
        // host-to-receiver element link; no owned correspondence is registered (that API needs both
        // spaces in one session and a free slot, so it is refused here). A source actor stands just
        // outside the host table. The receiver's realized target must land on the receiver's own
        // table at the same normalized radial-chart position, not at the source's absolute offset.
        [Test]
        public void CrossDeviceTableChartIsPreservedForDifferentTables()
        {
            var host = CreateManager("chart-host");
            var client = CreateManager("chart-client");
            int port = FreePort();
            host.Host(port);
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);

            // Different size and different shape (aspect 1.5 against 1.51), different pose; similar enough to link (SharedAnchorMatching).
            Vector3 hostSize = new Vector3(1.2f, 0.74f, 0.8f);
            Vector3 clientSize = new Vector3(1.48f, 0.74f, 0.98f);
            Vector2 hostCenter = new Vector2(2f, 3f);
            Vector2 clientCenter = new Vector2(-4f, 1f);
            GameObject hostTableObject = ActiveAnchor("chart-host-table");
            hostTableObject.transform.position = new Vector3(hostCenter.x, 0f, hostCenter.y);
            GameObject clientTableObject = ActiveAnchor("chart-client-table");
            clientTableObject.transform.position = new Vector3(clientCenter.x, 0f, clientCenter.y);
            RegisterTableGeometry(host, hostTableObject, 6101, hostSize);
            RegisterTableGeometry(client, clientTableObject, 6102, clientSize);
            var hostApp = ActiveApp("chart-host-app", Catalog(Record(hostTableObject, 4f, 6101, hostSize)));
            var clientApp = ActiveApp("chart-client-app", Catalog(Record(clientTableObject, 4f, 6102, clientSize)));
            SetAppManager(hostApp, host);
            SetAppManager(clientApp, client);
            Invoke(host, "RegisterApp", hostApp);
            Invoke(client, "RegisterApp", clientApp);

            Assert.That(hostApp.TryShareAnchor(hostApp.ShareableAnchors().Single()), Is.True);
            PumpUntil(() => clientTableObject.GetComponent<SbMRUKAnchorEntity>() != null &&
                            clientTableObject.GetComponent<SbMRUKAnchorEntity>().IsAttached(), host, client);
            SbMRUKAnchorEntity clientTable = clientTableObject.GetComponent<SbMRUKAnchorEntity>();
            PumpUntil(() => { clientApp.PollPendingSeatShares(); hostApp.PollPendingSeatShares(); return host.DeferredShareCount == 0 && client.DeferredShareCount == 0; }, host, client);
            Pump(400, host, client);
            Assert.That(client.TryGetLocalSceneGeometryElements(6102, out long clientParent, out _), Is.True);
            Assert.That(clientTable.ID, Is.EqualTo(clientParent));

            long hostRoom = Convert.ToInt64(Field(host, "_roomElementID"));
            const long avatarSharedId = 91001;
            // The source avatar is a real entity: the manager's LateUpdate emits its updates and polls the
            // receiver's targets. Its root carries the scene-level Spacing relation, whose only referent here
            // is the host table. A scope filtered on the label would match nothing: the element tag is the
            // Unity tag of the anchor (object), and the label is only the anchor's name.
            GiveTheRoomAFloor(host);
            GiveTheRoomAFloor(client);
            AvatarRig avatars = CreateAvatarActors("table-avatar", avatarSharedId, host, client,
                actor => Api("Bind", host.ID, actor, "root", EnumValue("SbKernelType", 0)));
            var targets = new List<RealizationTargetSample>();
            var targetsLock = new object();
            SpatialBlendAPI.RegisterOnTargets(client.ID, (_, __, list) =>
            {
                lock (targetsLock)
                {
                    for (int i = 0; i < list.Count; ++i)
                    {
                        if (list[i].KernelType == SbKernelType.Spacing)
                        {
                            targets.Add(new RealizationTargetSample(list[i].Position, list[i].ReferentId));
                        }
                    }
                }
            });

            // Stand 3 cm inside the host table's +X face, at chart angle 30 degrees. ABI 4 contact is the exact fact that
            // the source is on or inside the referent (R = 1; ABI 4 section 4), and there is
            // one F: a root 3 cm OUTSIDE is not in contact, so the table's footprint and
            // clearance ring stay closed to the receiver and its target is clamped 0.22 m out of them (traced: snap_m
            // 0.2198). The ABI 3.1 core opened the footprint within 0.30 m of the edge; that opening is deleted.
            Vector2 hostHalf = new Vector2(hostSize.x, hostSize.z) * 0.5f;
            Vector2 clientHalf = new Vector2(clientSize.x, clientSize.z) * 0.5f;
            Vector2 direction = new Vector2(Mathf.Cos(30f * Mathf.Deg2Rad), Mathf.Sin(30f * Mathf.Deg2Rad));
            Vector2 hostBoundary = hostCenter + direction * RayToRectangle(hostHalf, direction);
            Vector2 standing = new Vector2(hostBoundary.x - 0.03f, hostBoundary.y);
            // The support is the nearest boundary point. Its chart angle is read from the source table.
            Vector2 supportLocal = new Vector2(hostBoundary.x - hostCenter.x, standing.y - hostCenter.y);
            Vector2 supportDirection = supportLocal.normalized;
            Vector2 expected = clientCenter + supportDirection * RayToRectangle(clientHalf, supportDirection);
            Vector2 naive = clientCenter + supportLocal;
            Assert.That(Vector2.Distance(naive, expected), Is.GreaterThan(0.15f),
                "The fixture must tell a chart map from copying the source offset.");

            for (int step = 0; step < 12; ++step)
            {
                DriveAvatar(avatars, new Vector3(standing.x, 0f, standing.y), 0f);
            }

            RealizationTargetSample last;
            lock (targetsLock)
            {
                Assert.That(targets, Is.Not.Empty, "The receiver realized no Spacing target for the remote actor.");
                last = targets[targets.Count - 1];
            }
            // The target's referent id is a shared id (or invalid for a blended target), never the receiver's
            // element id, so the proof that the link resolves is the position below: it lands on the receiver's
            // own table chart, not at the source's offset.
            Vector2 landed = new Vector2(last.Position.x, last.Position.z);
            Assert.That(Vector2.Distance(landed, expected), Is.LessThan(0.10f),
                "The source position did not land at the same normalized chart position on the receiver table. " +
                "landed=" + landed + " expected=" + expected + " naive=" + naive);
        }

        // The core rejects two sibling elements whose footprints overlap. The shared objects of the
        // shipped Freeform scene are authored regions (a region per object, every region a child of the room), so the
        // scene itself must register them side by side without a refusal: this opens the real scene and applies every
        // SbSpatialAuthoring declaration in it, at the pose the scene gives it, to one space.
        [Test]
        public void TheFreeformSceneAuthoredObjectsRegisterWithoutSiblingOverlap()
        {
            Scene scene = OpenOriginalFreeformSceneForNativeTest();
            try
            {
                SbSpatialAuthoring[] authored = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<SbSpatialAuthoring>(true)).ToArray();
                Assert.That(authored.Length, Is.GreaterThanOrEqualTo(2), "The scene carries the Chess and the Whiteboard.");
                var host = CreateManager("scene-authoring-host");
                host.Host(FreePort());
                AssertConnected(host);
                long sharedId = 9700;
                foreach (SbSpatialAuthoring authoring in authored)
                {
                    Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                        Is.True, authoring.name + ": " + error);
                    Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                        Is.True, authoring.name + ": " + error);
                    Assert.That(host.TryApplySpatialAuthoring(plan, ++sharedId, 0,
                        authoring.transform.localToWorldMatrix, null, out _), Is.True,
                        authoring.name + " was refused: " + host.SceneRegistrationError);
                    Assert.That(host.SceneRegistrationError, Is.Null,
                        authoring.name + " was registered with a refused change: " + host.SceneRegistrationError);
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private readonly struct RealizationTargetSample
        {
            public readonly Vector3 Position;
            public readonly long ReferentId;
            public RealizationTargetSample(Vector3 position, long referentId)
            {
                Position = position;
                ReferentId = referentId;
            }
        }

        // Distance from a rectangle's centre to its boundary along a unit direction (X, Z plane).
        private static float RayToRectangle(Vector2 half, Vector2 direction)
        {
            float tx = Mathf.Abs(direction.x) < 1e-6f ? float.PositiveInfinity : half.x / Mathf.Abs(direction.x);
            float tz = Mathf.Abs(direction.y) < 1e-6f ? float.PositiveInfinity : half.y / Mathf.Abs(direction.y);
            return Mathf.Min(tx, tz);
        }

        private static SceneGeometryDescriptor DescriptorOf(GameObject anchor, int key, Vector3 size)
        {
            float hx = size.x * 0.5f;
            float hz = size.z * 0.5f;
            var outline = new[] { new Vector2(-hx, hz), new Vector2(hx, hz), new Vector2(hx, -hz), new Vector2(-hx, -hz) };
            Matrix4x4 mrukFrame = anchor.transform.localToWorldMatrix * Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f));
            Assert.That(SceneGeometryDescriptor.TryCreate(key, mrukFrame, new Bounds(Vector3.zero, new Vector3(size.x, size.z, size.y)),
                outline, null, out SceneGeometryDescriptor descriptor), Is.True);
            return descriptor;
        }

        private static void RegisterTableGeometry(SpatialBlendManager manager, GameObject anchor, int key, Vector3 size)
        {
            // The adapter's own input, MRUK-style: a plane anchor has its normal along local Z, so its frame is the
            // table's Y-up frame turned by Rx(-90); the measured outline is in that frame's XY (local Y = -world Z)
            // and the volume is (width, depth, thickness) in it, centred on the table's own centre.
            float hx = size.x * 0.5f;
            float hz = size.z * 0.5f;
            var outline = new[]
            {
                new Vector2(-hx, hz), new Vector2(hx, hz), new Vector2(hx, -hz), new Vector2(-hx, -hz)
            };
            Matrix4x4 mrukFrame = anchor.transform.localToWorldMatrix * Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f));
            Assert.That(SceneGeometryDescriptor.TryCreate(key, mrukFrame,
                new Bounds(Vector3.zero, new Vector3(size.x, size.z, size.y)), outline, null,
                out SceneGeometryDescriptor descriptor), Is.True);
            Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
        }

        private static void RegisterFloorGeometry(SpatialBlendManager manager, GameObject anchor, int key, Vector3 size)
        {
            float hx = size.x * 0.5f;
            float hz = size.z * 0.5f;
            var outline = new[]
            {
                new Vector2(-hx, hz), new Vector2(hx, hz), new Vector2(hx, -hz), new Vector2(-hx, -hz)
            };
            Matrix4x4 mrukFrame = anchor.transform.localToWorldMatrix * Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f));
            Assert.That(SceneGeometryDescriptor.TryCreate(key, mrukFrame, null, outline, null,
                out SceneGeometryDescriptor descriptor), Is.True);
            Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
        }

        private static void Pump(int milliseconds, params SpatialBlendManager[] managers)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                for (int i = 0; i < managers.Length; ++i) managers[i].Update();
                System.Threading.Thread.Sleep(10);
            }
        }

        private GameObject ActiveAnchor(string name)
        {
            var anchor = new GameObject(name);
            _objects.Add(anchor);
            return anchor;
        }

        private FreeformCollaborationApp ActiveApp(string name, ListSceneAnchorCatalog catalog)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var app = go.AddComponent<FreeformCollaborationApp>();
            app.UseSceneAnchorCatalog(catalog);
            return app;
        }

        private static ListSceneAnchorCatalog Catalog(params SceneAnchorRecord[] records)
        {
            var catalog = new ListSceneAnchorCatalog();
            foreach (SceneAnchorRecord record in records) catalog.Add(record);
            return catalog;
        }

        private static SceneAnchorRecord Record(GameObject anchor, float size, int key, Vector3 localSize) =>
            new SceneAnchorRecord
            {
                Label = SbMRUKAnchorEntity.TableLabel,
                Anchor = anchor,
                Size = size,
                SourceKey = key,
                LocalCenter = Vector3.zero,
                LocalSize = localSize
            };

        private static void SetAppManager(FreeformCollaborationApp app, SpatialBlendManager manager)
        {
            typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(app, manager);
        }

        private static void Invoke(object instance, string name, params object[] args)
        {
            instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(instance, args);
        }

        private static void PumpUntil(Func<bool> done, params SpatialBlendManager[] managers)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (!done() && DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < managers.Length; ++i) managers[i].Update();
                System.Threading.Thread.Sleep(10);
            }
            Assert.That(done(), Is.True);
        }

        private static int CountAnchors(string label)
        {
            int count = 0;
            foreach (SbMRUKAnchorEntity anchor in UnityEngine.Object.FindObjectsByType<SbMRUKAnchorEntity>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (anchor != null && anchor.SceneLabel == label) count++;
            }
            return count;
        }

        [Test]
        public void ContainmentTransferBindingsMatchTheCoreLayoutAndOffsets()
        {
            var manager = CreateManager("containment-abi");
            manager.Host(FreePort());
            AssertConnected(manager);

            // The managed binding negotiates the feature the way the app does ...
            MethodInfo validate = NativeApi.GetMethod("TryValidateContainmentAbi",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(validate, Is.Not.Null, "SpatialBlendAPI.TryValidateContainmentAbi");
            var arguments = new object[] { null };
            Assert.That((bool)validate.Invoke(null, arguments), Is.True, arguments[0] as string);
            Assert.That(manager.ContainmentTransferAvailable, Is.True,
                "Connecting negotiates the feature on a library that has it.");

            // ... and this fixture checks the same layout independently, with its own mirror.
            var feature = new ContainmentFeature();
            uint size = (uint)Marshal.SizeOf<ContainmentFeature>();
            Assert.That(sb_get_containment_transfer_feature(ref feature, size), Is.EqualTo(size));
            Assert.That(feature.descriptor_size, Is.EqualTo(size));
            Assert.That(feature.feature_version, Is.EqualTo(4u), "ABI 4.4: no contained pose, no blend band, no state struct, no contained event; adds the source-side fact.");
            Assert.That(feature.coordinate_convention, Is.EqualTo(1u), "Z-up metres, yaw radians about Z.");
            AssertContainmentFields(1u, typeof(ContainmentFeature), new[]
            {
                "descriptor_size", "feature_version", "coordinate_convention", "reserved"
            });
            Assert.That(sb_get_containment_transfer_field_count(2u), Is.EqualTo(0u), "The per-actor state kind is removed.");
            Assert.That(Marshal.SizeOf<ContainmentFeature>(), Is.EqualTo(16));
        }

        private static void AssertContainmentFields(uint kind, Type managed, string[] fields)
        {
            Assert.That(sb_get_containment_transfer_field_count(kind), Is.EqualTo((uint)fields.Length), managed.Name);
            for (uint index = 0; index < fields.Length; ++index)
            {
                Assert.That(sb_get_containment_transfer_field_offset(kind, index),
                    Is.EqualTo((uint)Marshal.OffsetOf(managed, fields[(int)index]).ToInt32()),
                    managed.Name + "." + fields[(int)index]);
            }
        }

        // ---------------------------------------------------------------------------------------
        // While a remote avatar is contained in a shared object's region,
        // its pose in the object's frame is what crosses. The receiver maps it through its own copy
        // of the object, which for identical copies (Chess) is the identity, so the avatar's
        // object-relative transform is preserved wherever the receiver placed its copy.
        //
        // Source and receiver are real managers in one process over loopback. Each applies the same
        // SbSpatialAuthoring declaration of the real Chess prefab to its own copy, at its own pose,
        // and flags the region for containment transfer. The source owns the avatar. The receiver
        // maps the avatar's actor the way EnsureV2Actor maps an element (root anchor, element
        // binding, actor dictionaries) and its manager realizes the root exactly as the app does
        // (LateUpdate polls the realization targets, Update applies them to the entity).
        // ---------------------------------------------------------------------------------------

        private const string ChessPrefabPath = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private const double PositionToleranceMeters = 0.01;
        private const double YawToleranceDegrees = 2.0;
        private static readonly Vector3[] ReceiverPositions =
        {
            new Vector3(4f, 0f, -3f), new Vector3(-2.5f, 0f, 6f), new Vector3(7.5f, 0f, 2f),
        };

        [Test]
        public void IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw0() =>
            AssertRemoteAvatarKeepsItsObjectRelativeTransform(0f, poseIdentity: false);

        [Test]
        public void IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw90() =>
            AssertRemoteAvatarKeepsItsObjectRelativeTransform(90f, poseIdentity: false);

        [Test]
        public void IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw200() =>
            AssertRemoteAvatarKeepsItsObjectRelativeTransform(200f, poseIdentity: false);

        // An authored region that carries its object is a transfer leaf, so the
        // identity mapping of the object-relative pose holds for the Chess (identical copies).
        [Test]
        public void IdenticalChessRemoteAvatarObjectRelativeIdentityHoldsOnlyIfTheRegionIsATransferLeaf() =>
            AssertRemoteAvatarKeepsItsObjectRelativeTransform(200f, poseIdentity: true);

        // Each room places its own copy, so a receiver may move its copy after it arrived (the user drags it in Editing). The
        // copy is not the source, and its move must reach the receiver's own space, or the core would go on mapping the
        // contained remote avatar onto where the copy had been. The copy here is a real received entity (not the source), moved
        // by its transform as the placement motif moves it; the manager's LateUpdate is the only thing that writes it.
        [Test]
        public void AReceiverThatMovesItsCopyAfterArrivalSeesTheRemoteAvatarOnTheMovedCopy()
        {
            Matrix4x4 sourceWorld = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 arrivedWorld = Matrix4x4.TRS(new Vector3(4f, 0f, -3f), Quaternion.Euler(0f, 90f, 0f), Vector3.one);
            Matrix4x4 movedWorld = Matrix4x4.TRS(new Vector3(-2.5f, 0f, 6f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);
            const long sharedId = 9500;
            AvatarRig rig = CreateAvatarRig("copy-moved", sharedId, sourceWorld, arrivedWorld, receiverHasTheObject: true);

            // The receiver's copy as the app has it after the arrival: an attached entity of this room that is not the source.
            long copyElement = Convert.ToInt64(Api("FindElementBySharedId", rig.Receiver.ID, sharedId));
            Assert.That(copyElement, Is.GreaterThan(0));
            GameObject copy = InactiveObject("copy-moved-receiver-chess");
            copy.transform.SetPositionAndRotation(arrivedWorld.GetColumn(3), arrivedWorld.rotation);
            var copyEntity = copy.AddComponent<SbNativeEntity>();
            copyEntity.AllowManipulation = false;
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(copyEntity, new object[] { rig.Receiver, copyElement, false });
            Assert.That(copyEntity.IsSource, Is.False);

            var pose = (position: new Vector3(0.12f, 0f, -0.35f), yaw: 40f);
            Vector3 sourcePosition = sourceWorld.MultiplyPoint3x4(pose.position);
            float sourceYaw = 30f + pose.yaw;
            AnchorProposal MappedRoot(string label)
            {
                SbAnchorProposal[] scratch = null;
                var proposals = new ProposalSet();
                Assert.That(SpatialBlendAPI.TryReadProposals(rig.Receiver.ID, rig.ReceiverActor, ref scratch, proposals), Is.True, label);
                Assert.That(proposals.HasRoot, Is.True, label);
                Assert.That(ReadRoot(rig).Transfer, Is.True, "contained and mapped: " + label);
                return proposals.Root;
            }
            void AssertOn(Matrix4x4 copyWorld, float copyYaw, string label)
            {
                AnchorProposal mapped = MappedRoot(label);
                Vector3 expected = copyWorld.MultiplyPoint3x4(pose.position);
                TestContext.Out.WriteLine($"COPY_MOVED {label} mapped=({mapped.Position.x:0.0000},{mapped.Position.z:0.0000}) " +
                    $"expected=({expected.x:0.0000},{expected.z:0.0000}) yaw={mapped.YawDegrees:0.00} expectedYaw={copyYaw + pose.yaw:0.00}");
                Assert.That(mapped.Position.x, Is.EqualTo(expected.x).Within(PositionToleranceMeters), label);
                Assert.That(mapped.Position.z, Is.EqualTo(expected.z).Within(PositionToleranceMeters), label);
                Assert.That(DegreesApart(mapped.YawDegrees, copyYaw + pose.yaw), Is.LessThan(YawToleranceDegrees), label);
            }

            // Enter from outside and settle inside: the avatar is mapped onto the copy where it arrived.
            DriveAvatar(rig, sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), 30f);
            for (int step = 1; step <= 6; ++step)
                DriveAvatar(rig, Vector3.Lerp(sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), sourcePosition, step / 6f), sourceYaw);
            for (int hold = 0; hold < 3; ++hold) DriveAvatar(rig, sourcePosition, sourceYaw);
            AssertOn(arrivedWorld, 90f, "before the move");

            // The receiver's user moves the copy (as the placement motif does: the transform only). The source never knows.
            copy.transform.SetPositionAndRotation(movedWorld.GetColumn(3), movedWorld.rotation);
            for (int hold = 0; hold < 3; ++hold) DriveAvatar(rig, sourcePosition, sourceYaw);
            AssertOn(movedWorld, 200f, "after the receiver moved its copy");
            Assert.That(Vector3.Distance(arrivedWorld.MultiplyPoint3x4(pose.position), movedWorld.MultiplyPoint3x4(pose.position)),
                Is.GreaterThan(1f), "the move is large enough that the stale copy pose would fail the check");
        }

        private void AssertRemoteAvatarKeepsItsObjectRelativeTransform(float receiverYawDegrees, bool poseIdentity)
        {
            Matrix4x4 sourceWorld = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);

            // Object-local avatar poses inside the 1 m by 1.8 m region. The yaw is relative to the object.
            var poses = new[]
            {
                (position: new Vector3(0.12f, 0f, -0.35f), yaw: 40f),
                (position: new Vector3(-0.2f, 0f, 0.5f), yaw: -130f),
            };

            long sharedId = 9300 + (long)receiverYawDegrees;
            foreach (Vector3 receiverPosition in ReceiverPositions)
            {
                ++sharedId;
                Matrix4x4 receiverWorld = Matrix4x4.TRS(receiverPosition, Quaternion.Euler(0f, receiverYawDegrees, 0f), Vector3.one);
                AvatarRig rig = CreateAvatarRig("object-relative-" + sharedId, sharedId, sourceWorld, receiverWorld,
                    receiverHasTheObject: true);
                SbAnchorProposal[] scratch = null;
                var proposals = new ProposalSet();

                foreach (var pose in poses)
                {
                    string label = $"receiver copy at {receiverPosition}, yaw {receiverYawDegrees}, avatar {pose.position}, yaw {pose.yaw}";
                    Vector3 sourcePosition = sourceWorld.MultiplyPoint3x4(pose.position);
                    float sourceYaw = 30f + pose.yaw;
                    Vector3 expectedPosition = receiverWorld.MultiplyPoint3x4(pose.position);
                    float expectedYaw = receiverYawDegrees + pose.yaw;

                    // Enter from outside, then settle inside so the source's contained state is CONTAINED.
                    DriveAvatar(rig, sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), 30f);
                    for (int step = 1; step <= 6; ++step)
                    {
                        DriveAvatar(rig, Vector3.Lerp(sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)),
                            sourcePosition, step / 6f), sourceYaw);
                    }
                    for (int hold = 0; hold < 3; ++hold) DriveAvatar(rig, sourcePosition, sourceYaw);

                    // The receiver holds the object, so its root proposal says it IS the transfer mapping (the ABI 4.3 flag).
                    Assert.That(ReadRoot(rig).Transfer, Is.True, "Contained and mapped, not contained at the source only: " + label);

                    // The receiver maps it onto its own copy: the root PROPOSAL is the same object-relative pose (the
                    // leaf is the actor's whole scope and the empty-scope mapping maps the root).
                    Assert.That(SpatialBlendAPI.TryReadProposals(rig.Receiver.ID, rig.ReceiverActor, ref scratch, proposals), Is.True, label);
                    Assert.That(proposals.HasRoot, Is.True, label);
                    AnchorProposal mapped = proposals.Root;
                    Assert.That(mapped.Status == EmbodiedStatus.Exact || mapped.Status == EmbodiedStatus.Inexact, Is.True, label);
                    if (!poseIdentity)
                    {
                        // The followed root is where the driver wrote it, within its own bounds of the proposal.
                        Vector3 followed = rig.ReceiverAvatar.transform.position;
                        Assert.That(FlatDistance(followed, mapped.Position), Is.LessThanOrEqualTo(2f * 1f + 1e-3f), label);
                        continue;
                    }
                    Assert.That(mapped.Position.x, Is.EqualTo(expectedPosition.x).Within(PositionToleranceMeters), label);
                    Assert.That(mapped.Position.z, Is.EqualTo(expectedPosition.z).Within(PositionToleranceMeters), label);
                    Assert.That(DegreesApart(mapped.YawDegrees, expectedYaw), Is.LessThan(YawToleranceDegrees), label);

                    // The realized root is that pose.
                    Vector3 realized = rig.ReceiverAvatar.transform.position;
                    Assert.That(realized.x, Is.EqualTo(expectedPosition.x).Within(PositionToleranceMeters), "realized " + label);
                    Assert.That(realized.z, Is.EqualTo(expectedPosition.z).Within(PositionToleranceMeters), "realized " + label);
                    Assert.That(DegreesApart(rig.ReceiverAvatar.transform.eulerAngles.y, expectedYaw),
                        Is.LessThan(YawToleranceDegrees), "realized " + label);

                    // Control: the same avatar at the source's world pose is nowhere near the receiver's copy.
                    Assert.That(Vector3.Distance(new Vector3(realized.x, 0f, realized.z),
                        new Vector3(sourcePosition.x, 0f, sourcePosition.z)), Is.GreaterThan(0.5f),
                        "The pose follows the receiver's placement, not the source's: " + label);
                }
            }
        }

        [Test]
        public void TransferFlagRaisesStartThenTerminateWhenTheClientAvatarEntersAndLeavesTheObjectRegion()
        {
            // Default-mode "interacting" is the interaction value of a received avatar: the root proposal's TRANSFER flag
            // AND that anchor's ARRIVED. The receiver's
            // manager raises Start when the value turns true, which is when the realised body is in the leaf, not when the
            // source crosses the edge, and Terminate when it turns false.
            Matrix4x4 sourceWorld = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 receiverWorld = Matrix4x4.TRS(new Vector3(-6f, 0f, 5f), Quaternion.Euler(0f, 90f, 0f), Vector3.one);
            AvatarRig rig = CreateAvatarRig("containment-events", 9400, sourceWorld, receiverWorld,
                receiverHasTheObject: true);
            var events = new List<InteractionEventType>();
            var referents = new List<long>();
            rig.ReceiverEntity.SetOnInteractionCallback((type, referent, proximity, __) =>
            {
                Assert.That(proximity, Is.EqualTo(ProximityType.Euclidean2D));
                events.Add(type);
                referents.Add(referent);
            });

            // The local avatar's value is the core's source-side transfer fact (ABI 4.4): it
            // switches on the very update the source crosses the edge, with no receiver in the loop, and it names the leaf.
            var sourceEvents = new List<InteractionEventType>();
            rig.SourceAvatar.GetComponent<SbNativeEntity>().SetOnInteractionCallback((type, referent, _, __) =>
            {
                Assert.That(referent, Is.EqualTo(SpatialAuthoringCompiler.RegionSharedId(9400)),
                    "The source fact names the containing region.");
                sourceEvents.Add(type);
            });
            Vector3 Local(float x, float z) => sourceWorld.MultiplyPoint3x4(new Vector3(x, 0f, z));
            // Outside the region (it ends 0.9 m from the centre along z): nothing.
            DriveAvatar(rig, Local(0f, -3f), 30f);
            DriveAvatar(rig, Local(0f, -3f), 30f);
            Assert.That(events, Is.Empty, "Outside every region there is nothing to be in.");
            Assert.That(sourceEvents, Is.Empty);
            Assert.That(ReadRoot(rig).Transfer, Is.False);

            // Entering sets the TRANSFER flag on the very update that crossed the edge, but the receiver's body is not
            // there yet: it walks at its own bound. The display does not switch on the proposal.
            DriveAvatar(rig, Local(0f, -0.95f), 30f);
            Assert.That(ReadRoot(rig).Transfer, Is.False, "Still outside by 5 cm.");
            DriveAvatar(rig, Local(0f, -0.85f), 30f);
            Assert.That(ReadRoot(rig).Transfer, Is.True, "The flag is set on the update that crosses the edge.");
            Assert.That(events, Is.Empty, "No switch while the body is still walking.");
            Assert.That(sourceEvents, Is.EqualTo(new[] { InteractionEventType.Start }),
                "The local avatar switches on the crossing update: its value is the core's source-side fact.");
            Assert.That(SpatialBlendAPI.TryGetSourceTransfer(rig.Source.ID, rig.SourceActor, out long leaf), Is.True);
            Assert.That(leaf, Is.EqualTo(SpatialAuthoringCompiler.RegionSharedId(9400)));
            Assert.That(rig.Source.IsTransferInteracting(rig.SourceAvatar.GetComponent<SbNativeEntity>()), Is.True);

            // The source holds still; the body arrives and the display switches once, on the realised body.
            WalkTheReceiverUntil(rig, Local(0f, -0.85f), 30f, () => events.Count > 0, 20);
            Assert.That(events, Is.EqualTo(new[] { InteractionEventType.Start }),
                "Start when the realised body is in the leaf.");
            Assert.That(ReadRoot(rig).TransferArrived, Is.True);

            // Walking through the leaf to the middle and back is one engagement.
            foreach (float z in new[] { -0.7f, -0.3f, 0.2f, 0.6f, 0.75f })
            {
                DriveAvatar(rig, Local(0.1f, z), 30f);
                Assert.That(ReadRoot(rig).Transfer, Is.True, "Inside the leaf the flag stays set, z=" + z);
            }
            WalkTheReceiverUntil(rig, Local(0.1f, 0.75f), 30f, () => ReadRoot(rig).TransferArrived, 20);
            Assert.That(events, Is.EqualTo(new[] { InteractionEventType.Start }),
                "Moving inside the region does not repeat Start or raise Terminate.");

            // Leaving clears the flag on the crossing update, and Terminate follows on the same driver frame.
            DriveAvatar(rig, Local(0f, 0.85f), 30f);
            Assert.That(ReadRoot(rig).Transfer, Is.True);
            DriveAvatar(rig, Local(0f, 0.95f), 30f);
            Assert.That(ReadRoot(rig).Transfer, Is.False, "The flag is clear on the update that crosses the edge.");
            Assert.That(events, Is.EqualTo(new[] { InteractionEventType.Start, InteractionEventType.Terminate }),
                "Terminate on the read that clears the value.");
            Assert.That(sourceEvents, Is.EqualTo(new[] { InteractionEventType.Start, InteractionEventType.Terminate }),
                "The local avatar's Terminate is on the same crossing update.");
            Assert.That(SpatialBlendAPI.TryGetSourceTransfer(rig.Source.ID, rig.SourceActor, out leaf), Is.True);
            Assert.That(leaf, Is.EqualTo(0L), "Outside every leaf the source fact is 0.");

            // Back in and out again: a new engagement each time, and nothing extra.
            DriveAvatar(rig, Local(0f, 0.3f), 30f);
            WalkTheReceiverUntil(rig, Local(0f, 0.3f), 30f, () => events.Count == 3, 20);
            DriveAvatar(rig, Local(0f, 3f), 30f);
            Assert.That(events, Is.EqualTo(new[]
            {
                InteractionEventType.Start, InteractionEventType.Terminate,
                InteractionEventType.Start, InteractionEventType.Terminate,
            }));
            // The flag carries no scope, so the label is the root proposal's driving referent; a Terminate names the one its
            // Start did.
            Assert.That(referents[1], Is.EqualTo(referents[0]));
            Assert.That(referents[3], Is.EqualTo(referents[2]));
        }

        [Test]
        public void AvatarCrossingTheRegionEdgeIsFollowedWithinTheDriversBoundsAndTheTransferFlagSwitchesAtTheEdge()
        {
            // ABI 4 has no blend band and no HANDOVER: the leaf is the actor's whole scope, so the root proposal
            // changes from the scene-level pose to the mapped pose at the edge and the driver carries the change at its
            // own pace (lag is accepted). The transfer flag switches instantly: it is set on
            // the very update the source crosses the edge, whatever the body has done.
            Matrix4x4 sourceWorld = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 receiverWorld = Matrix4x4.TRS(new Vector3(4f, 0f, -3f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);
            AvatarRig rig = CreateAvatarRig("containment-handover", 9450, sourceWorld, receiverWorld,
                receiverHasTheObject: true);

            Vector3 previous = default;
            bool havePrevious = false;
            float largestStep = 0f;
            // 2 cm per step along the object's z axis, from 0.4 m outside to 0.6 m inside the edge and back.
            var zs = new List<float>();
            for (float z = -1.31f; z <= -0.3f; z += 0.02f) zs.Add(z);
            for (float z = -0.31f; z >= -1.31f; z -= 0.02f) zs.Add(z);
            bool sawInside = false;
            foreach (float z in zs)
            {
                DriveAvatar(rig, sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, z)), 30f);
                bool inside = ReadRoot(rig).Transfer;
                Assert.That(inside, Is.EqualTo(Mathf.Abs(z) < 0.9f), "The transfer flag switches at the edge, z=" + z);
                sawInside |= inside;
                Vector3 realized = rig.ReceiverAvatar.transform.position;
                if (havePrevious)
                {
                    largestStep = Mathf.Max(largestStep, Vector3.Distance(
                        new Vector3(realized.x, 0f, realized.z), new Vector3(previous.x, 0f, previous.z)));
                }
                previous = realized;
                havePrevious = true;
            }
            // One driver frame is one second long here (EditMode has no frame time): the realized root never moves more
            // than the driver's speed bound over it.
            Assert.That(sawInside, Is.True, "The region was entered.");
            Assert.That(largestStep, Is.LessThanOrEqualTo(2f * 1f + 1e-3f),
                "The realized root moves within the driver's own speed bound (largest step " + largestStep + " m).");
        }

        // Containment is evaluated statelessly on every update against the current
        // entity set (v1), so a counterpart that appears after the avatar is already inside the region is used on the
        // next source update. The receiver first has no copy of the object (the root proposal is the scene-level one);
        // once its copy exists, the very next source update makes the root proposal the object-relative pose on it and sets
        // the TRANSFER flag. The display then switches once the body arrives,
        // not on that update.
        [Test]
        public void ACounterpartThatAppearsLateSetsTheTransferFlagOnTheNextUpdateAndTheDisplaySwitchesOnceTheBodyArrives()
        {
            Matrix4x4 sourceWorld = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 receiverWorld = Matrix4x4.TRS(new Vector3(4f, 0f, -3f), Quaternion.Euler(0f, 200f, 0f), Vector3.one);
            AvatarRig rig = CreateAvatarRig("containment-late-counterpart", 9600, sourceWorld, receiverWorld,
                receiverHasTheObject: false);
            var events = new List<InteractionEventType>();
            rig.ReceiverEntity.SetOnInteractionCallback((type, _, __, ___) => events.Add(type));

            var local = new Vector3(0.12f, 0f, -0.35f);
            Vector3 inside = sourceWorld.MultiplyPoint3x4(local);
            Vector3 expectedPosition = receiverWorld.MultiplyPoint3x4(local);
            const float sourceYaw = 70f;   // 40 degrees relative to the source copy (yaw 30)
            float expectedYaw = 200f + (sourceYaw - 30f);

            DriveAvatar(rig, sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), 30f);
            for (int step = 1; step <= 6; ++step)
            {
                DriveAvatar(rig, Vector3.Lerp(sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), inside, step / 6f), sourceYaw);
            }
            for (int hold = 0; hold < 3; ++hold) DriveAvatar(rig, inside, sourceYaw);

            SbAnchorProposal[] scratch = null;
            var proposals = new ProposalSet();
            Assert.That(SpatialBlendAPI.TryReadProposals(rig.Receiver.ID, rig.ReceiverActor, ref scratch, proposals), Is.True);
            Assert.That(proposals.HasRoot, Is.True);
            Vector3 before = proposals.Root.Position;
            Assert.That(FlatDistance(before, expectedPosition), Is.GreaterThan(0.5f),
                "Without a counterpart the root proposal is not the object-relative pose on a copy that does not exist yet.");
            Assert.That(proposals.Root.Transfer, Is.False, "A missing counterpart leaves the flag clear.");
            Assert.That(rig.Receiver.IsTransferInteracting(rig.ReceiverEntity), Is.False);
            Assert.That(events, Is.Empty);

            // The counterpart appears late, while the avatar stays where it is. The next update uses it.
            GiveTheReceiverTheObject(rig.Source, rig.Receiver, rig.Plan, rig.SharedId, rig.SourceBinding, rig.ReceiverWorld);
            DriveAvatar(rig, inside, sourceYaw);

            Assert.That(SpatialBlendAPI.TryReadProposals(rig.Receiver.ID, rig.ReceiverActor, ref scratch, proposals), Is.True);
            Assert.That(proposals.HasRoot, Is.True);
            AnchorProposal mapped = proposals.Root;
            Assert.That(mapped.Status == EmbodiedStatus.Exact || mapped.Status == EmbodiedStatus.Inexact, Is.True);
            Assert.That(mapped.Transfer, Is.True, "The late counterpart sets the flag on the next update.");
            Assert.That(mapped.Position.x, Is.EqualTo(expectedPosition.x).Within(PositionToleranceMeters));
            Assert.That(mapped.Position.z, Is.EqualTo(expectedPosition.z).Within(PositionToleranceMeters));
            Assert.That(DegreesApart(mapped.YawDegrees, expectedYaw), Is.LessThan(YawToleranceDegrees));

            // The body is still on its way: the representation has not switched on the proposal.
            Vector3 realizedNow = rig.ReceiverAvatar.transform.position;
            if (!mapped.Arrived)
            {
                Assert.That(events, Is.Empty, "No switch while the body is still walking.");
                Assert.That(rig.Receiver.IsTransferInteracting(rig.ReceiverEntity), Is.False);
            }
            // The display switches once the body arrives.
            WalkTheReceiverUntil(rig, inside, sourceYaw, () => events.Count > 0, 20);
            Assert.That(events, Is.EqualTo(new[] { InteractionEventType.Start }), "The display switches once.");
            Assert.That(rig.Receiver.IsTransferInteracting(rig.ReceiverEntity), Is.True);
            Vector3 realized = rig.ReceiverAvatar.transform.position;
            Assert.That(FlatDistance(realized, mapped.Position), Is.LessThan(0.5f),
                "The switch happens with the body at the mapped pose (it was " + FlatDistance(realizedNow, mapped.Position) +
                " m away when the flag was set).");
        }

        [Test]
        public void ReceiverWithoutTheObjectFallsBackToTheSceneLevelRootAndRaisesNoInteraction()
        {
            Matrix4x4 sourceWorld = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            AvatarRig rig = CreateAvatarRig("containment-fallback", 9500, sourceWorld, Matrix4x4.identity,
                receiverHasTheObject: false);
            var events = new List<InteractionEventType>();
            rig.ReceiverEntity.SetOnInteractionCallback((type, _, __, ___) => events.Add(type));

            Vector3 inside = sourceWorld.MultiplyPoint3x4(new Vector3(0.12f, 0f, -0.35f));
            DriveAvatar(rig, sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), 30f);
            for (int step = 1; step <= 6; ++step)
            {
                DriveAvatar(rig, Vector3.Lerp(sourceWorld.MultiplyPoint3x4(new Vector3(0f, 0f, -3f)), inside, step / 6f), 70f);
            }
            for (int hold = 0; hold < 3; ++hold) DriveAvatar(rig, inside, 70f);

            // The receiver has no counterpart for the object: contained at the source only.
            Assert.That(ReadRoot(rig).Transfer, Is.False, "A missing counterpart leaves the transfer flag clear.");
            Assert.That(rig.Receiver.IsTransferInteracting(rig.ReceiverEntity), Is.False,
                "ZoneBased reads the same value: no counterpart, no switch, and no pure-region fallback.");
            Assert.That(events, Is.Empty, "Without the object this peer is not interacting with it.");
        }

        // Source and receiver managers, one avatar owned by the source, and the Chess authored on both sides.
        private sealed class AvatarRig
        {
            public SpatialBlendManager Source, Receiver;
            public GameObject SourceAvatar, ReceiverAvatar;
            public SbNativeEntity ReceiverEntity;
            public long SourceActor, ReceiverActor;
            // What a receiver that did not hold the object needs to be given it later.
            public SpatialAuthoringPlan Plan;
            public long SharedId;
            public SpatialAuthoringBinding SourceBinding;
            public Matrix4x4 ReceiverWorld;
        }

        // The core finds containment by descending from the nearest ancestor of the actor's element that has
        // containment geometry. In the app that is the room, which RegisterWalkable gives its floor. These
        // fixtures build the room by hand, so they give it a floor that covers every pose they use.
        private static void GiveTheRoomAFloor(SpatialBlendManager manager)
        {
            long room = (long)Field(manager, "_roomElementID");
            Api("SetElementPolygon", manager.ID, room, EnumValue("SbGeometryRole", 1), new Vector2[]
            {
                new Vector2(-50f, -50f), new Vector2(50f, -50f), new Vector2(50f, 50f), new Vector2(-50f, 50f)
            });
        }

        private static void AddBoardReferent(SpatialBlendManager manager)
        {
            long room = (long)Field(manager, "_roomElementID");
            long board = Convert.ToInt64(Api("AddElement", manager.ID, room));
            Assert.That(board, Is.GreaterThan(0));
            Api("SetElementSharedId", manager.ID, board, 200L);
            Api("SetElementTag", manager.ID, board, "Board");
            Api("SetElementPolygon", manager.ID, board, EnumValue("SbGeometryRole", 2), new[]
            {
                new Vector2(30f, 30f), new Vector2(34f, 30f), new Vector2(34f, 34f), new Vector2(30f, 34f)
            });
        }

        private AvatarRig CreateAvatarRig(string name, long sharedId, Matrix4x4 sourceWorld, Matrix4x4 receiverWorld,
                                          bool receiverHasTheObject)
        {
            var source = CreateManager(name + "-source");
            var receiver = CreateManager(name + "-receiver");
            int port = FreePort();
            receiver.Host(port);
            AssertConnected(receiver);
            source.Connect(LoopbackHost, port);
            AssertConnected(source);
            Assert.That(source.ContainmentTransferAvailable && receiver.ContainmentTransferAvailable, Is.True,
                "This fixture needs a core with the containment transfer header.");
            // Each side owns its room before anything is shared, as it does once the app has started.
            InvokePrivate(source, "EnsureRoomElement");
            InvokePrivate(receiver, "EnsureRoomElement");
            GiveTheRoomAFloor(source);
            GiveTheRoomAFloor(receiver);
            // ABI 4: a source whose relations are all out of scope sends nothing, so each side holds one Spacing
            // referent the avatar always reaches (the Board, linked by shared id as BuildRemoteAvatarRig does).
            foreach (SpatialBlendManager side in new[] { source, receiver }) AddBoardReferent(side);

            // The source authors the Chess and shares its region and object. A receiver that holds the
            // object authors its own copy at its own pose. One that does not never receives them.
            SpatialAuthoringPlan plan = CompileChessPlan();
            Assert.That(source.TryApplySpatialAuthoring(plan, sharedId, 0, sourceWorld, null, out SpatialAuthoringBinding sourceBinding), Is.True);
            if (receiverHasTheObject)
            {
                GiveTheReceiverTheObject(source, receiver, plan, sharedId, sourceBinding, receiverWorld);
            }
            Assert.That(source.ContainmentRegionCount, Is.EqualTo(1));

            AvatarRig rig = CreateAvatarActors(name, sharedId, source, receiver,
                actor => Api("Bind", source.ID, actor, "root", EnumValue("SbKernelType", 0)));
            rig.Plan = plan;
            rig.SharedId = sharedId;
            rig.SourceBinding = sourceBinding;
            rig.ReceiverWorld = receiverWorld;
            return rig;
        }

        // The receiver authors its own copy of the object the source shared, at its own pose.
        private void GiveTheReceiverTheObject(SpatialBlendManager source, SpatialBlendManager receiver,
                                                     SpatialAuthoringPlan plan, long sharedId,
                                                     SpatialAuthoringBinding sourceBinding, Matrix4x4 receiverWorld)
        {
            ShareWithoutPayload(source.ID, sourceBinding.RegionElement);
            ShareWithoutPayload(source.ID, sourceBinding.ObjectElement);
            long receivedRegion = WaitForSharedElement(receiver, SpatialAuthoringCompiler.RegionSharedId(sharedId), source, receiver);
            long receivedObject = WaitForSharedElement(receiver, sharedId, source, receiver);
            Assert.That(receivedRegion, Is.GreaterThan(0), "The shared region did not reach the receiver.");
            Assert.That(receivedObject, Is.GreaterThan(0), "The shared object did not reach the receiver.");
            Assert.That(receiver.TryApplySpatialAuthoring(plan, sharedId, receivedObject, receiverWorld, null,
                out SpatialAuthoringBinding received), Is.True);
            Assert.That(received.RegionElement, Is.EqualTo(receivedRegion), "The receiver reuses the region the source shared.");
            Api("ResolveSharedElement", receiver.ID, receivedObject, receivedObject);
            Assert.That(receiver.ContainmentRegionCount, Is.EqualTo(1), "The receiver flagged its copy too.");
        }

        // One avatar owned by the source, mapped onto an actor on the receiver. The manager's own LateUpdate is
        // what emits the source's updates and polls the receiver's targets, so the avatar has to be a real entity.
        // bindSource binds the source actor's relations before the avatar's element is shared.
        private AvatarRig CreateAvatarActors(string name, long sharedId, SpatialBlendManager source,
                                             SpatialBlendManager receiver, Action<long> bindSource)
        {
            // The avatar belongs to the source. Its actor has a root anchor and the app's scene-level
            // Spacing relation on it, so the source also sends the fallback relation.
            const long actorShared = 300;
            long room = (long)Field(source, "_roomElementID");
            long actorElement = Convert.ToInt64(Api("AddElement", source.ID, room));
            Api("SetElementSharedId", source.ID, actorElement, actorShared + sharedId);
            var sourceAvatar = InactiveObject(name + "-source-avatar");
            sourceAvatar.tag = "Avatar";
            var sourceEntity = sourceAvatar.AddComponent<SbNativeEntity>();
            sourceEntity.AllowManipulation = false;
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(sourceEntity, new object[] { source, actorElement, true });
            long sourceActor = Convert.ToInt64(typeof(SpatialBlendManager).GetMethod("EnsureV2Actor",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, new object[] { sourceEntity }));
            Assert.That(sourceActor, Is.GreaterThan(0));
            bindSource(sourceActor);
            Share(source.ID, actorElement);

            // The receiver maps the avatar's actor onto its own entity.
            long receivedActorElement = WaitForShared(receiver.ID, actorShared + sharedId);
            Api("ResolveSharedElement", receiver.ID, receivedActorElement, receivedActorElement);
            var receiverAvatar = InactiveObject(name + "-receiver-avatar");
            receiverAvatar.tag = "Avatar";
            var receiverEntity = receiverAvatar.AddComponent<SbNativeEntity>();
            receiverEntity.AllowManipulation = false;
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(receiverEntity, new object[] { receiver, receivedActorElement, false });
            long receiverActor = Convert.ToInt64(Api("CreateActor", receiver.ID));
            Api("AddAnchor", receiver.ID, receiverActor, "root", Vector3.zero, null);
            Api("SetActorElement", receiver.ID, receiverActor, receivedActorElement);
            ((IDictionary)Field(receiver, "_v2ActorByEntity")).Add(receiverEntity.ID, receiverActor);
            ((IDictionary)Field(receiver, "_v2EntityByActor")).Add(receiverActor, receiverEntity.ID);
            // The receiver's body belongs to the app's embodied driver: it publishes the arrival pose, so the
            // core can solve, and each LateUpdate moves the body toward the proposal. EditMode has no frame
            // time, so one driver frame is long enough to cover the walk (2 m/s, 150 deg/s for 1 s).
            receiver.TestOnlyDriverSeconds = 1f;
            receiver.ConfigureEmbodiedDriver(receiverEntity, receiverActor);

            return new AvatarRig
            {
                Source = source,
                Receiver = receiver,
                SourceAvatar = sourceAvatar,
                ReceiverAvatar = receiverAvatar,
                ReceiverEntity = receiverEntity,
                SourceActor = sourceActor,
                ReceiverActor = receiverActor,
            };
        }

        // One source frame: the avatar moves, the source emits its update, the receiver applies it, then
        // the receiver runs its own frame (Update drains the queue, LateUpdate polls the realization
        // targets and the containment state, Update applies what the poll delivered). The core limits how
        // often a source emits, so the frame repeats until one update really went out. That makes one call
        // exactly one sample of the avatar's walk.
        private static void DriveAvatar(AvatarRig rig, Vector3 unityPosition, float yawDegrees)
        {
            long before = sb_get_last_emitted_update_sequence(rig.Source.ID, rig.SourceActor);
            long after = before;
            DateTime emitDeadline = DateTime.UtcNow.AddSeconds(3);
            while (after == before && DateTime.UtcNow < emitDeadline)
            {
                System.Threading.Thread.Sleep(20);
                rig.SourceAvatar.transform.SetPositionAndRotation(unityPosition, Quaternion.Euler(0f, yawDegrees, 0f));
                rig.SourceAvatar.transform.hasChanged = true;
                rig.Source.LateUpdate();
                after = sb_get_last_emitted_update_sequence(rig.Source.ID, rig.SourceActor);
            }
            Assert.That(after, Is.GreaterThan(before), "The source did not emit an update for this sample.");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline &&
                   sb_get_last_applied_remote_sequence(rig.Receiver.ID, rig.Source.ID, rig.SourceActor) < after)
            {
                System.Threading.Thread.Sleep(2);
            }
            Assert.That(sb_get_last_applied_remote_sequence(rig.Receiver.ID, rig.Source.ID, rig.SourceActor),
                Is.GreaterThanOrEqualTo(after), "The receiver did not apply the avatar update.");
            rig.Receiver.Update();
            rig.Receiver.LateUpdate();
            rig.Receiver.Update();
        }

        // The root proposal the core holds for the receiver's avatar right now.
        private static AnchorProposal ReadRoot(AvatarRig rig)
        {
            SbAnchorProposal[] scratch = null;
            var proposals = new ProposalSet();
            Assert.That(SpatialBlendAPI.TryReadProposals(rig.Receiver.ID, rig.ReceiverActor, ref scratch, proposals), Is.True);
            Assert.That(proposals.HasRoot, Is.True);
            return proposals.Root;
        }

        // The source holds still while the receiver's body walks at the driver's bound toward the proposal. The core solves
        // when a source update arrives, from the latest published sample, so each frame is one more source update at the same
        // pose (the heartbeat) followed by the receiver's frame. Bounded: the condition must hold within `maxFrames` frames.
        private static void WalkTheReceiverUntil(AvatarRig rig, Vector3 sourcePosition, float sourceYawDegrees,
                                                 Func<bool> done, int maxFrames)
        {
            for (int frame = 0; frame < maxFrames && !done(); ++frame)
            {
                DriveAvatar(rig, sourcePosition, sourceYawDegrees);
            }
            Assert.That(done(), Is.True, "The condition did not hold within " + maxFrames + " frames.");
        }

        private static double UnityYawFromSbRadians(double radians) => -(radians * 180.0 / Math.PI) + 90.0;

        private static double DegreesApart(double a, double b)
        {
            double difference = Math.Abs(a - b) % 360.0;
            return difference > 180.0 ? 360.0 - difference : difference;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ContainmentFeature
        {
            public uint descriptor_size, feature_version, coordinate_convention, reserved;
        }

        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_get_containment_transfer_feature(ref ContainmentFeature feature, uint size);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_get_containment_transfer_field_count(uint kind);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint sb_get_containment_transfer_field_offset(uint kind, uint index);

        [Test]
        public void ChessAnchorLeavingTheRegionHandsBackToSceneLevelRelations()
        {
            AssertPoseAbiMatchesTheCore();
            SpatialAuthoringPlan plan = CompileChessPlan();
            var host = CreateManager("region-exit-host");
            host.Host(FreePort());
            AssertConnected(host);
            Matrix4x4 world = Matrix4x4.TRS(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Assert.That(host.TryApplySpatialAuthoring(plan, 9500, 0, world, null, out SpatialAuthoringBinding binding), Is.True);
            long room = (long)Field(host, "_roomElementID");
            GiveTheRoomAFloor(host);
            long region = binding.RegionElement;
            long affordance = Convert.ToInt64(Api("FindElementBySharedId", host.ID, SpatialAuthoringCompiler.AffordanceSharedId(9500, 0)));
            Assert.That(affordance, Is.GreaterThan(0));
            Assert.That(sb_get_element_parent(host.ID, region, out long regionParent), Is.EqualTo(1));
            Assert.That(regionParent, Is.EqualTo(room), "The region is a child of the room.");
            Assert.That(sb_get_element_parent(host.ID, binding.ObjectElement, out long objectParent), Is.EqualTo(1));
            Assert.That(objectParent, Is.EqualTo(region), "The object is a child of its region.");

            // A native actor with a scene-level Spacing binding on root and an AffordancePose binding
            // on hand, tagged for the chess surface. Neither names the room as a scope.
            long actorElement = Convert.ToInt64(Api("AddElement", host.ID, room));
            long actor = Convert.ToInt64(Api("CreateActor", host.ID));
            Assert.That(actorElement, Is.GreaterThan(0));
            Assert.That(actor, Is.GreaterThan(0));
            Api("SetActorElement", host.ID, actor, actorElement);
            Assert.That((bool)Api("AddAnchorWithPositionDimension", host.ID, actor, "root", Vector3.zero, null, 2), Is.True);
            Assert.That((bool)Api("AddAnchorWithPositionDimension", host.ID, actor, "hand", Vector3.zero, "root", 3), Is.True);
            Api("Bind", host.ID, actor, "root", EnumValue("SbKernelType", 0));
            Api("BindAffordance", host.ID, actor, "hand", "Playable");
            Assert.That(sb_set_observability_capture(host.ID, 1), Is.EqualTo(1));
            try
            {
                // Inside: the hand is over the board and the root stands in the region, outside the footprint.
                DriveActor(host.ID, actor, world, new Vector3(0.3f, 0f, -0.7f), new Vector3(0f, 0.508f, 0f));
                // The region polygon is element-local, so the object-local point (0.3, -0.7) is inside it
                // wherever the object stands (the T3.7 polygon was read as world coordinates before).
                ScopeRecord insideSpacing = ScopeRecordFor(host.ID, actor, 0);
                Assert.That(insideSpacing.deepest_containing, Is.EqualTo(region),
                    "The root at object-local (0.3, -0.7) is contained in the region of a chess standing at (1, 0, 2) yaw 30.");
                ScopeRecord insideAffordance = ScopeRecordFor(host.ID, actor, 2);
                // ABI 4: a flagged region is the actor's scope like any other container, for every anchor:
                // there is no longer a blend band to protect, so the root's Spacing scope is the region too.
                Assert.That(insideSpacing.deepest_containing, Is.EqualTo(region),
                    "Inside the region the root's Spacing scope is the region.");
                Assert.That(IsAtOrUnder(host.ID, insideAffordance.deepest_containing, region), Is.True,
                    "Inside the region the AffordancePose scope is the region subtree.");
                ScopeCandidate[] insideCandidates = ScopeCandidatesFor(host.ID, actor, insideAffordance.binding_index);
                Assert.That(insideCandidates.Any(candidate => candidate.element_id == affordance &&
                    (candidate.reason == 0 || candidate.reason == 11)), Is.True,
                    "Inside the region the chess surface is evaluated for the hand.");

                // Outside: 3 m past the region. The scope is the scene again.
                DriveActor(host.ID, actor, world, new Vector3(0f, 0f, 3f), new Vector3(0f, 0.508f, 3f));
                ScopeRecord outsideSpacing = ScopeRecordFor(host.ID, actor, 0);
                Assert.That(outsideSpacing.deepest_containing, Is.Not.EqualTo(region), "3 m outside is not contained.");
                ScopeRecord outsideAffordance = ScopeRecordFor(host.ID, actor, 2);
                Assert.That(IsAtOrUnder(host.ID, outsideSpacing.deepest_containing, region), Is.False,
                    "Outside the region the Spacing relation is a scene-level relation again.");
                Assert.That(IsAtOrUnder(host.ID, region, outsideSpacing.deepest_containing), Is.True,
                    "The scene-level container holds the region.");
                Assert.That(IsAtOrUnder(host.ID, outsideAffordance.deepest_containing, region), Is.False);
                Assert.That(IsAtOrUnder(host.ID, region, outsideAffordance.deepest_containing), Is.True);
                // The surface is optional metadata. Outside the region the hand's scope is the
                // scene, where the core still lists the surface as a candidate, but only as a distant one.
                ScopeCandidate outsideSurface = ScopeCandidatesFor(host.ID, actor, outsideAffordance.binding_index)
                    .Single(candidate => candidate.element_id == affordance);
                ScopeCandidate insideSurface = insideCandidates.First(candidate => candidate.element_id == affordance);
                Assert.That(outsideSurface.proximity, Is.LessThan(insideSurface.proximity),
                    "Outside the region the chess surface is farther from the hand than it was inside.");
                Assert.That(outsideSurface.proximity, Is.LessThan(0.5),
                    "Outside the region the chess surface is only a distant scene-level candidate.");

                // And back in: the object-relative relation returns.
                DriveActor(host.ID, actor, world, new Vector3(0.3f, 0f, -0.7f), new Vector3(0f, 0.508f, 0f));
                Assert.That(IsAtOrUnder(host.ID, ScopeRecordFor(host.ID, actor, 2).deepest_containing, region), Is.True);
                Assert.That(ScopeRecordFor(host.ID, actor, 0).deepest_containing, Is.EqualTo(region));
            }
            finally
            {
                Api("DestroyActor", host.ID, actor);
                Api("RemoveLocalElement", host.ID, actorElement);
            }
        }

        private static SpatialAuthoringPlan CompileChessPlan()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ChessPrefabPath);
            Assert.That(prefab, Is.Not.Null, ChessPrefabPath);
            SbSpatialAuthoring authoring = prefab.GetComponent<SbSpatialAuthoring>();
            Assert.That(authoring, Is.Not.Null, "The Chess prefab carries SbSpatialAuthoring.");
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error), Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error), Is.True, error);
            Assert.That(plan.PosePreserving, Is.True);
            Assert.That(plan.ExtentIsDeclared, Is.True);
            return plan;
        }

        private static void ShareWithoutPayload(long space, long element)
        {
            var share = NativeApi.GetMethod("ShareElement", BindingFlags.Static | BindingFlags.Public, null,
                new[] { typeof(long), typeof(long) }, null);
            Assert.That(share, Is.Not.Null, "SpatialBlendAPI.ShareElement(space, element)");
            share.Invoke(null, new object[] { space, element });
        }

        private static long WaitForSharedElement(SpatialBlendManager receiver, long sharedId, params SpatialBlendManager[] pump)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                long element = Convert.ToInt64(Api("FindElementBySharedId", receiver.ID, sharedId));
                if (element > 0) return element;
                foreach (SpatialBlendManager manager in pump) manager.Update();
                System.Threading.Thread.Sleep(10);
            }
            return 0;
        }

        private static void DriveActor(long space, long actor, Matrix4x4 objectWorld, Vector3 rootLocal, Vector3 handLocal)
        {
            Vector3 root = objectWorld.MultiplyPoint3x4(rootLocal);
            Vector3 hand = objectWorld.MultiplyPoint3x4(handLocal);
            Api("SetAnchorTransform", space, actor, "hand", hand, 30f);
            Assert.That((bool)Api("SetAnchorActive", space, actor, "hand", true), Is.True);
            UpdateSource(space, actor, root, 30f);
        }

        private static ScopeRecord ScopeRecordFor(long space, long actor, int kernelType)
        {
            var records = new ScopeRecord[8];
            for (int index = 0; index < records.Length; ++index)
                records[index].struct_size = (uint)Marshal.SizeOf<ScopeRecord>();
            int count = sb_get_scope_records(space, actor, records, records.Length, (uint)Marshal.SizeOf<ScopeRecord>());
            Assert.That(count, Is.GreaterThan(0).And.LessThanOrEqualTo(records.Length), "sb_get_scope_records");
            for (int index = 0; index < count; ++index)
            {
                if (records[index].kernel_type == kernelType)
                {
                    Assert.That(records[index].valid, Is.EqualTo(1), "The binding was evaluated.");
                    return records[index];
                }
            }
            Assert.Fail("No scope record for kernel " + kernelType);
            return default;
        }

        private static ScopeCandidate[] ScopeCandidatesFor(long space, long actor, long bindingIndex)
        {
            var candidates = new ScopeCandidate[32];
            for (int index = 0; index < candidates.Length; ++index)
                candidates[index].struct_size = (uint)Marshal.SizeOf<ScopeCandidate>();
            int count = sb_get_scope_candidates(space, actor, (int)bindingIndex, candidates, candidates.Length,
                (uint)Marshal.SizeOf<ScopeCandidate>());
            Assert.That(count, Is.GreaterThanOrEqualTo(0), "sb_get_scope_candidates");
            return candidates.Take(Math.Min(count, candidates.Length)).ToArray();
        }

        private static bool IsAtOrUnder(long space, long element, long ancestor)
        {
            for (int depth = 0; depth < 32 && element > 0; ++depth)
            {
                if (element == ancestor) return true;
                if (sb_get_element_parent(space, element, out long parent) != 1) return false;
                element = parent;
            }
            return false;
        }

        // A layout mistake here would corrupt memory instead of failing, so refuse to run on a mismatch.
        private static void AssertPoseAbiMatchesTheCore()
        {
            AssertStructSize<RelationPayload>(12);
            AssertStructSize<RelationQuery>(13);
            AssertStructSize<RelationEvaluation>(14);
            AssertStructSize<ScopeRecord>(5);
            AssertStructSize<ScopeCandidate>(6);
            AssertStructSize<TargetRelationEvaluation>(18);
            AssertStructSize<SbAnchorPose>(22);
            AssertStructSize<SbAnchorProposal>(23);
            AssertStructSize<SbProposalSet>(24);
            AssertStructSize<SbActorEvent>(25);
        }

        private static void AssertStructSize<T>(int structId) where T : struct
        {
            Assert.That((long)(ulong)sb_sizeof(structId), Is.EqualTo(Marshal.SizeOf<T>()),
                typeof(T).Name + " must have the core's layout (sb_struct_id " + structId + ").");
        }

        // ---- T2.5 / design A*: remote avatar moved by the app's embodied driver ----
        //
        // Host and client are two real managers in this process, linked over the real
        // core transport. The host shares an avatar actor; the client reconstructs it
        // through the app's OnShare, places it 2 m in front of the viewer as v1 did,
        // and the manager gives it an embodied driver with the config defaults
        // (2 m/s, 150 deg/s, the v1 prefab's shipped values). Every frame the driver
        // publishes the head's realized pose through the core's sampling port, reads
        // the core's proposal and writes the pose it moved the body to. The core
        // holds no pose and paces nothing. The avatar's head is a real
        // SbRetargetTransform head, reached through the same root path
        // SbNetworkAvatarEntity uses.

        private const float MaxMetersPerSecond = 2f;
        private const float MaxDegreesPerSecond = 150f;
        private const float DriverTickSeconds = 0.1f;

        [Test]
        public void RemoteAvatarTargetReachesTheHeadWithinTheDriverBounds()
        {
            using (new MainThreadLease())
            {
                RemoteAvatarRig rig = BuildRemoteAvatarRig();
                RemoteAvatarProbeEntity remote = rig.Remote;

                // v1 arrival: 2 m in front of the viewer at the origin, facing them.
                AssertHeadAt(remote, new Vector3(0f, 1.6f, 2f), "arrival pose");
                Assert.That(Vector3.Dot(remote.Head.forward, Vector3.back), Is.GreaterThan(0.999f),
                    "The arrived avatar must face the viewer.");

                // The driver is configured on the client's actor with the declared defaults.
                EmbodiedDriverConfig config = DriverOf(rig).Config;
                Assert.That(config.MaxLinearSpeedMetersPerSecond, Is.EqualTo(MaxMetersPerSecond));
                Assert.That(config.MaxAngularSpeedDegreesPerSecond, Is.EqualTo(MaxDegreesPerSecond));

                rig.StartSourceWalk();
                Assert.That(rig.WaitFor(() => remote.Writes.Count >= 30), Is.True,
                    "The remote avatar's head was never driven.");
                Assert.That(TryReadClientProposal(rig, out AnchorProposal proposal), Is.True);
                Assert.That(proposal.Status == EmbodiedStatus.Exact || proposal.Status == EmbodiedStatus.Inexact, Is.True,
                    "The core proposed nothing: " + proposal.Status);
                Vector3 target = proposal.Position;

                // The first update of a source actor carries the join exception (SB_EXCEPTION_JOIN), and the app's
                // default realization of an exception is a teleport to the proposal: the first write is that placement,
                // not a walk. Every later write is one tick of DriverTickSeconds: no step may exceed the caps.
                Pose previous = remote.Writes[0];
                float maxStep = MaxMetersPerSecond * DriverTickSeconds + 1e-3f;
                float maxTurn = MaxDegreesPerSecond * DriverTickSeconds + 0.1f;
                for (int i = 1; i < remote.Writes.Count; ++i)
                {
                    Pose written = remote.Writes[i];
                    Assert.That(Vector3.Distance(previous.position, written.position), Is.LessThanOrEqualTo(maxStep),
                        "Head moved faster than the driver's speed bound.");
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(previous.rotation.eulerAngles.y,
                        written.rotation.eulerAngles.y)), Is.LessThanOrEqualTo(maxTurn),
                        "Head turned faster than the driver's angular bound.");
                    previous = written;
                }

                // The source walks on, a long way from where the join placed the head: the driver follows the
                // core's proposals at its own bounds and the head is where it wrote.
                int writes = remote.Writes.Count;
                Vector3 before = remote.Head.position;
                rig.StartSourceWalk();
                Assert.That(rig.WaitFor(() => remote.Writes.Count >= writes + 10), Is.True, "The head stopped being driven.");
                for (int i = writes; i < remote.Writes.Count; ++i)
                {
                    Assert.That(Vector3.Distance(remote.Writes[i - 1].position, remote.Writes[i].position),
                        Is.LessThanOrEqualTo(maxStep), "Head moved faster than the driver's speed bound after the join.");
                }
                Assert.That(TryReadClientProposal(rig, out AnchorProposal later), Is.True);
                Assert.That(FlatDistance(remote.Head.position, later.Position),
                    Is.LessThanOrEqualTo(FlatDistance(before, later.Position) + 1e-3f),
                    "The head does not move away from the core's proposal.");
                AssertHeadAt(remote, remote.Writes[remote.Writes.Count - 1].position, "last write");
            }
        }

        [Test]
        public void RemoteAvatarEmptySnapshotReleasesTheTarget()
        {
            using (new MainThreadLease())
            {
                RemoteAvatarRig rig = BuildRemoteAvatarRig();
                RemoteAvatarProbeEntity remote = rig.Remote;
                rig.StartSourceWalk();
                Assert.That(rig.WaitFor(() => remote.Writes.Count >= 5), Is.True);
                Assert.That(TryReadClientProposal(rig, out _), Is.True);
                int cancelsBefore = remote.CancelCalls;

                // An inactive source root makes the next relation snapshot empty.
                Assert.That((bool)Api("SetAnchorActive", rig.Host.ID, rig.HostActor, "root", false), Is.True);
                UpdateSource(rig.Host.ID, rig.HostActor, new Vector3(17f, 0f, 17f), 0f);
                Assert.That(rig.WaitFor(() => TryReadClientProposal(rig, out AnchorProposal released) &&
                                              released.Status == EmbodiedStatus.None), Is.True,
                    "The empty snapshot did not release the proposal.");
                // The driver sees the release on its next frame and tells the entity.
                Assert.That(rig.WaitFor(() => remote.CancelCalls > cancelsBefore), Is.True,
                    "The entity was not told the root target was released.");

                // Released: the head is not written again while the snapshot stays empty.
                int writes = remote.Writes.Count;
                Vector3 head = remote.Head.position;
                rig.Spin(20);
                Assert.That(remote.Writes.Count, Is.EqualTo(writes));
                Assert.That(remote.Head.position, Is.EqualTo(head));

                // A new root target resumes the walk, still within the cap.
                Assert.That((bool)Api("SetAnchorActive", rig.Host.ID, rig.HostActor, "root", true), Is.True);
                UpdateSource(rig.Host.ID, rig.HostActor, new Vector3(6f, 0f, 6f), 0f);
                Assert.That(rig.WaitFor(() => remote.Writes.Count > writes), Is.True,
                    "A new root target did not resume the walk.");
                Assert.That(Vector3.Distance(head, remote.Writes[writes].position),
                    Is.LessThanOrEqualTo(MaxMetersPerSecond * DriverTickSeconds + 1e-3f));
            }
        }

        // ABI 3.1: the source publishes its head's yaw and pitch (sb_update_actor_pose); the core passes the pitch through
        // to the receiver's proposal, and the driver writes it to the head with the root, at once.
        [Test]
        public void RemoteAvatarHeadPitchTravelsFromTheSourceThroughTheProposalToTheHead()
        {
            using (new MainThreadLease())
            {
                RemoteAvatarRig rig = BuildRemoteAvatarRig();
                RemoteAvatarProbeEntity remote = rig.Remote;

                for (int step = 0; step <= 3; ++step)
                {
                    float at = 15f + 0.5f * step;
                    long update = SpatialBlendAPI.UpdateActorPose(rig.Host.ID, rig.HostActor, new Vector3(at, 0f, at), 0f, 25f);
                    if (update > 0) Assert.That((bool)Api("TryWait", update, 3000), Is.True);
                    rig.Spin(2);
                }

                Assert.That(rig.WaitFor(() => TryReadClientProposal(rig, out AnchorProposal p) && p.HasPitch), Is.True,
                    "The source's pitch never reached the receiver's proposal.");
                TryReadClientProposal(rig, out AnchorProposal proposal);
                Assert.That(proposal.PitchDegrees, Is.EqualTo(25f).Within(1e-3f));
                Assert.That(rig.WaitFor(() => remote.Writes.Count > 0 &&
                    Mathf.Abs(Mathf.DeltaAngle(remote.Writes[remote.Writes.Count - 1].rotation.eulerAngles.x, 25f)) < 1e-2f), Is.True,
                    "The head was not given the pitch.");
            }
        }


        [Test]
        public void RemoteAvatarIsNotWrittenAfterOnRemove()
        {
            using (new MainThreadLease())
            {
                RemoteAvatarRig rig = BuildRemoteAvatarRig();
                RemoteAvatarProbeEntity remote = rig.Remote;
                int writesAtRemove = -1;
                Vector3 headAtRemove = default;
                remote.SetOnRemoveCallback(() =>
                {
                    writesAtRemove = remote.Writes.Count;
                    headAtRemove = remote.Head.position;
                });
                rig.StartSourceWalk();
                Assert.That(rig.WaitFor(() => remote.Writes.Count >= 5), Is.True);
                Assert.That(TryReadClientProposal(rig, out _), Is.True,
                    "The avatar must still be walking when its source goes away.");

                // The host source leaves; the client receives the removal.
                rig.HostApp.DeregisterGameObject(rig.Source.gameObject);
                Assert.That(rig.WaitFor(() => writesAtRemove >= 0), Is.True, "OnRemove never ran.");
                Assert.That(remote.IsAttached(), Is.False);
                Assert.That(((IDictionary)Field(rig.Client, "_v2EmbodiedDrivers")).Contains(rig.ClientActor), Is.False,
                    "The removed avatar's driver must be forgotten.");

                rig.Spin(30);
                Assert.That(remote.Writes.Count, Is.EqualTo(writesAtRemove),
                    "The head was written after OnRemove.");
                Assert.That(remote.Head.position, Is.EqualTo(headAtRemove));
            }
        }

        private static float FlatDistance(Vector3 a, Vector3 b) =>
            Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        private static void AssertHeadAt(RemoteAvatarProbeEntity avatar, Vector3 expected, string what) =>
            Assert.That(Vector3.Distance(avatar.Head.position, expected), Is.LessThan(1e-3f), what);

        private RemoteAvatarRig BuildRemoteAvatarRig()
        {
            SpatialBlendManager host = CreateManager("avatar-host");
            SpatialBlendManager client = CreateManager("avatar-client");
            int port = FreePort();
            host.Host(port);
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);
            // EditMode has no frame time; the driver step takes a fixed step instead.
            client.TestOnlyDriverSeconds = DriverTickSeconds;

            // The same room, a navigable floor and one referent on both sides. The
            // referents link by shared id, as the core's own two-space fixtures do.
            Vector3[] floor =
            {
                new Vector3(0f, 0f, 0f), new Vector3(20f, 0f, 0f),
                new Vector3(20f, 0f, 20f), new Vector3(0f, 0f, 20f)
            };
            foreach (SpatialBlendManager manager in new[] { host, client })
            {
                Assert.That(manager.RegisterWalkableMesh(floor, new[] { 0, 1, 2, 0, 2, 3 }), Is.True);
                long room = (long)Field(manager, "_roomElementID");
                long board = Convert.ToInt64(Api("AddElement", manager.ID, room));
                Assert.That(board, Is.GreaterThan(0));
                Api("SetElementSharedId", manager.ID, board, 200L);
                Api("SetElementTag", manager.ID, board, "Board");
                Api("SetElementPolygon", manager.ID, board, EnumValue("SbGeometryRole", 2), new[]
                {
                    new Vector2(8f, 8f), new Vector2(12f, 8f), new Vector2(12f, 12f), new Vector2(8f, 12f)
                });
            }

            var hostApp = InactiveObject("avatar-host-app").AddComponent<RemoteAvatarProbeApp>();
            var clientApp = InactiveObject("avatar-client-app").AddComponent<RemoteAvatarProbeApp>();
            typeof(SpatialBlendManager).GetMethod("RegisterApp", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(host, new object[] { hostApp });
            typeof(SpatialBlendManager).GetMethod("RegisterApp", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(client, new object[] { clientApp });

            RemoteAvatarProbeEntity source = CreateAvatarProbe("avatar-source", new Vector3(15f, 0f, 15f));
            var rig = new RemoteAvatarRig { Host = host, Client = client, HostApp = hostApp, Source = source };
            host.ShareGameObject(source.gameObject, true);
            Assert.That(rig.WaitFor(() => clientApp.Added.Count == 1), Is.True,
                "The client never reconstructed the shared avatar.");
            rig.Remote = clientApp.Added[0].GetComponent<RemoteAvatarProbeEntity>();
            Assert.That(rig.Remote, Is.Not.Null);
            _objects.Add(rig.Remote.gameObject);
            rig.Remote.Writes.Clear();

            rig.HostActor = (long)((IDictionary)Field(host, "_v2ActorByEntity"))[source.ID];
            rig.ClientActor = (long)((IDictionary)Field(client, "_v2ActorByEntity"))[rig.Remote.ID];
            Assert.That(((IDictionary)Field(client, "_v2EmbodiedDrivers")).Contains(rig.ClientActor), Is.True,
                "The manager did not configure the remote avatar's embodied driver.");

            // The source's root is bound to the shared room's referents (Spacing).
            long hostRoom = (long)Field(host, "_roomElementID");
            Api("Bind", host.ID, rig.HostActor, "root", EnumValue("SbKernelType", 0));
            return rig;
        }

        private RemoteAvatarProbeEntity CreateAvatarProbe(string name, Vector3 position)
        {
            // Active, unlike the managers: the in-process reconstruction of a Local
            // entity finds the instantiated clone by instance id among active objects.
            var avatar = new GameObject(name);
            _objects.Add(avatar);
            avatar.tag = "Avatar";
            avatar.transform.position = position;
            Transform head = new GameObject("head").transform;
            Transform left = new GameObject("left").transform;
            Transform right = new GameObject("right").transform;
            foreach (Transform part in new[] { head, left, right })
            {
                part.SetParent(avatar.transform, false);
            }
            var retarget = avatar.AddComponent<SbRetargetTransform>();
            retarget.headTransform = head;
            retarget.leftHandTransform = left;
            retarget.rightHandTransform = right;
            var probe = avatar.AddComponent<RemoteAvatarProbeEntity>();
            // EditMode never runs Awake, and the probe has no collider for the ISDK motifs.
            probe.AllowManipulation = false;
            probe.Head = head;
            probe.Retarget = retarget;
            return probe;
        }

        private sealed class RemoteAvatarRig
        {
            public SpatialBlendManager Host, Client;
            public RemoteAvatarProbeApp HostApp;
            public RemoteAvatarProbeEntity Source, Remote;
            public long HostActor, ClientActor;

            // One frame on both managers; the client's LateUpdate paces and polls.
            public void Spin(int frames)
            {
                for (int i = 0; i < frames; ++i)
                {
                    Host.Update();
                    Client.Update();
                    Client.LateUpdate();
                    System.Threading.Thread.Sleep(10);
                }
            }

            public bool WaitFor(Func<bool> done, int timeoutMs = 8000)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    if (done()) return true;
                    Spin(1);
                }
                return done();
            }

            // The source walks from (15, 15) to (16.5, 16.5), a long way from the arrival.
            public void StartSourceWalk()
            {
                for (int step = 0; step <= 3; ++step)
                {
                    float at = 15f + 0.5f * step;
                    UpdateSource(Host.ID, HostActor, new Vector3(at, 0f, at), 0f);
                    Spin(2);
                }
            }
        }

        // The Unity entry points the avatar path needs in EditMode: the shared-entity
        // reconstruction waits on the main-thread dispatcher, which only learns its
        // thread from Awake, and EditMode never runs that.
        private sealed class MainThreadLease : IDisposable
        {
            private static readonly FieldInfo ThreadId = typeof(SpatialBlend.Internal.UnityMainThreadDispatcher)
                .GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic);
            private readonly int _previous;

            public MainThreadLease()
            {
                Assert.That(ThreadId, Is.Not.Null);
                _previous = (int)ThreadId.GetValue(null);
                ThreadId.SetValue(null, Environment.CurrentManagedThreadId);
            }

            public void Dispose() => ThreadId.SetValue(null, _previous);
        }

        // Stands in for SbNetworkAvatarEntity, which needs Fusion. A Local entity is
        // reconstructed in process by instance id, and its realization target takes the
        // same route: the retarget head through ApplyRootRealizationTransform.
        public sealed class RemoteAvatarProbeEntity : SbLocalEntity
        {
            public Transform Head;
            public SbRetargetTransform Retarget;
            public readonly List<Pose> Writes = new List<Pose>();
            public int CancelCalls;

            private static readonly MethodInfo SetEntityTransform =
                typeof(SbEntity).GetMethod("SetTransform", BindingFlags.Instance | BindingFlags.NonPublic);

            // v1 FreeformCollaborationApp.OnObjectAdded: 2 m in front of the viewer,
            // facing them. The viewer here stands at the origin looking along +Z.
            public void PlaceForArrival()
            {
                SetEntityTransform.Invoke(this, new object[] { Head });
                Retarget.SetHeadTransform(new Vector3(0f, 1.6f, 2f), Quaternion.LookRotation(Vector3.back, Vector3.up));
            }

            public override void ApplyRealizationTarget(Vector3 worldPosition, Quaternion worldRotation)
            {
                Retarget.ApplyRootRealizationTransform(worldPosition, worldRotation);
                Writes.Add(new Pose(Head.position, Head.rotation));
            }

            public override void CancelRealizationTarget()
            {
                ++CancelCalls;
                base.CancelRealizationTarget();
            }
        }

        public sealed class RemoteAvatarProbeApp : SbApp
        {
            public readonly List<GameObject> Added = new List<GameObject>();
            public override void OnAppStart() { }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType type)
            {
                if (type != OnObjectAddedType.Added) return;
                Added.Add(gameObject);
                gameObject.GetComponent<RemoteAvatarProbeEntity>().PlaceForArrival();
            }
            public override void OnRemove(GameObject gameObject) { }
        }

        // The driver of the client's remote avatar, and the core's current proposal for its actor, read
        // through the same ABI the driver reads it with.
        private static OvrAvatarEmbodiedDriver DriverOf(RemoteAvatarRig rig)
        {
            object driven = ((IDictionary)Field(rig.Client, "_v2EmbodiedDrivers"))[rig.ClientActor];
            Assert.That(driven, Is.InstanceOf<OvrAvatarEmbodiedDriver>(), "No embodied driver is configured on the remote avatar actor.");
            return (OvrAvatarEmbodiedDriver)driven;
        }

        // The root's proposal of the client's avatar actor, read through the same ABI the driver reads it with.
        private static bool TryReadClientProposal(RemoteAvatarRig rig, out AnchorProposal proposal)
        {
            proposal = default;
            SbAnchorProposal[] scratch = null;
            var set = new ProposalSet();
            if (!SpatialBlendAPI.TryReadProposals(rig.Client.ID, rig.ClientActor, ref scratch, set) || !set.HasRoot)
            {
                return false;
            }
            proposal = set.Root;
            return true;
        }

        private static void Share(long space, long element)
        {
            NativeApi.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Single(method => method.Name == "ShareElement" && method.GetParameters().Length == 2)
                .Invoke(null, new object[] { space, element });
        }

        private static long WaitForShared(long space, long sharedId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            long found = 0;
            while (DateTime.UtcNow < deadline)
            {
                found = Convert.ToInt64(Api("FindElementBySharedId", space, sharedId));
                if (found > 0) return found;
                System.Threading.Thread.Sleep(10);
            }
            Assert.Fail("Shared element " + sharedId + " was not received.");
            return 0;
        }

        private SpatialBlendManager CreateManager(string name) => InactiveObject(name).AddComponent<SpatialBlendManager>();
        private static object PrivateField(object instance, string name) => instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        private static void InvokePrivate(object instance, string name) => instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, null);
        private GameObject InactiveObject(string name)
        {
            var item = new GameObject(name);
            item.SetActive(false);
            _objects.Add(item);
            return item;
        }
        private static void AssertConnected(SpatialBlendManager manager)
        {
            Assert.That(manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), manager.ConnectionError);
            Assert.That(manager.Initialized && manager.NativeLibraryCompatible && manager.ID > 0, Is.True);
            Assert.That((bool)NativeApi.GetField("useDebugLibrary",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.False,
                "Manager and independent native queries must use the same selected release artifact.");
        }
        // The core's host listens on [::] and its dual-stack client dials an IPv4
        // literal as ::ffff:127.0.0.1, which the gate's macOS sandbox does not
        // classify as loopback. "localhost" is classified, so the sandbox can
        // deny all non-loopback egress without blocking these connections.
        private const string LoopbackHost = "localhost";
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        private static SpatialHierarchyDescriptor SeatDescriptor() => new SpatialHierarchyDescriptor
        {
            spaceId = "recorded-room",
            nodes = new[]
            {
                new SpatialHierarchyNode { id = "region", kind = SpatialHierarchyNodeKind.Region,
                    localTransform = Matrix4x4.TRS(new Vector3(5, 0, 8), Quaternion.Euler(0, 90, 0), Vector3.one) },
                new SpatialHierarchyNode { id = "chair", kind = SpatialHierarchyNodeKind.Object, parentId = "region",
                    localTransform = Matrix4x4.Translate(new Vector3(0, .8f, 0)) },
                new SpatialHierarchyNode { id = "seat", kind = SpatialHierarchyNodeKind.Affordance,
                    parentId = "chair", supportId = "seat.top", localSupportPolygon = new[]
                    {
                        new Vector3(.2f, 0, -.25f), new Vector3(.6f, 0, -.25f),
                        new Vector3(.5f, 0, 0), new Vector3(.2f, 0, -.05f)
                    } }
            }
        };
        private static readonly Type NativeApi = typeof(AnchorPoseFrame).Assembly.GetType("SpatialBlend.Internal.SpatialBlendAPI", true);
        private static object Api(string name, params object[] args) => NativeApi.GetMethod(name,
            BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
        private static object EnumValue(string name, int value) => Enum.ToObject(
            NativeApi.Assembly.GetType("SpatialBlend.Internal." + name, true), value);
        private static object Field(object value, string name) => typeof(SpatialBlendManager)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static void UpdateSource(long space, long actor) =>
            UpdateSource(space, actor, new Vector3(5, 0, 8), 90f);

        private static void UpdateSource(long space, long actor, Vector3 position, float yaw)
        {
            long update = Convert.ToInt64(Api("UpdateActorState", space, actor, position, yaw));
            // The release C ABI may complete actor extraction synchronously and
            // return its documented kSyncDoneHandle (-1). Positive handles are
            // asynchronous and must be awaited before querying their snapshot.
            if (update > 0)
                Assert.That((bool)Api("TryWait", update, 3000), Is.True,
                    "Native actor extraction did not complete before querying relations: " +
                    Api("GetError", update));
            else
                Assert.That(update, Is.EqualTo(-1),
                    "Native actor update was neither synchronously completed nor admitted.");
        }
        private static RelationState ReadHand(long space, long actor)
            => ReadRelation(space, actor, "hand");

        private static RelationState ReadRelation(long space, long actor, string anchorName)
        {
            var relations = new RelationState[16];
            int count = sb_get_relation_states(space, actor, relations, relations.Length);
            Assert.That(count, Is.GreaterThan(0).And.LessThanOrEqualTo(relations.Length));
            for (int i = 0; i < count; ++i)
                if (Marshal.PtrToStringAnsi(relations[i].anchor_name) == anchorName) return relations[i];
            Assert.Fail("Actual native extraction did not produce the declared " + anchorName + " relation.");
            return default;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ActorState { public double position_x, position_y, yaw; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RelationState
        {
            public IntPtr anchor_name;
            public double proximity, contact_point_world_x, contact_point_world_y, contact_point_world_z;
            public double normalized_angle, normalized_radial_ratio, normalized_height_ratio;
            public long referent_element_id, referent_shared_id;
            public int kernel_type, normalized_strategy, has_contact, active;
        }
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern ActorState sb_get_actor_state(long space, long actor);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_get_relation_states(long space, long actor, [Out] RelationState[] states, int capacity);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr sb_sizeof(int structId);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_set_observability_capture(long space, int enabled);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_evaluate_relation(long space, ref RelationQuery query, ref RelationEvaluation evaluation);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_evaluate_target_relation(long space, ref RelationPayload payload, uint payloadStructSize,
            ref RelationQuery candidate, ref TargetRelationEvaluation evaluation);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_get_scope_records(long space, long actor, [Out] ScopeRecord[] records, int capacity, uint structSize);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_get_scope_candidates(long space, long actor, int bindingIndex,
            [Out] ScopeCandidate[] candidates, int capacity, uint structSize);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sb_get_element_parent(long space, long element, out long parent);

        // Mirrors of spatialblend_observability.h. encoded_state[8] is eight doubles so the structs stay
        // blittable. AssertPoseAbiMatchesTheCore compares every size with sb_sizeof before use.
        [StructLayout(LayoutKind.Sequential)]
        private struct RelationQuery
        {
            public uint struct_size, version;
            public double anchor_x, anchor_y, anchor_z, anchor_yaw;
            public long referent, scope_root;
            public int kernel_type, position_dimension, explicit_scope, reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RelationEvaluation
        {
            public uint struct_size, version;
            public IntPtr sub_region_name;
            public double proximity, support_distance_m, source_scope_diameter_m;
            public double contact_point_world_x, contact_point_world_y, contact_point_world_z;
            public double normalized_angle, normalized_radial_ratio, normalized_height_ratio;
            public double interior_u, interior_v, interior_w;
            public double encoded_state0, encoded_state1, encoded_state2, encoded_state3;
            public double encoded_state4, encoded_state5, encoded_state6, encoded_state7;
            public long deepest_containing, navigable_element;
            public int normalized_strategy, encoded_state_size, has_contact, has_interior;
            public double ap_offset_u_m, ap_offset_v_m, ap_height_m;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RelationPayload
        {
            public uint struct_size, version;
            public IntPtr anchor_name, sub_region_name;
            public double proximity, estimated_time, support_distance_m, source_scope_diameter_m;
            public double contact_point_local_x, contact_point_local_y;
            public double contact_point_world_x, contact_point_world_y, contact_point_world_z;
            public double normalized_angle, normalized_radial_ratio, normalized_height_ratio;
            public double interior_u, interior_v, interior_w;
            public double encoded_state0, encoded_state1, encoded_state2, encoded_state3;
            public double encoded_state4, encoded_state5, encoded_state6, encoded_state7;
            public long referent_shared_id;
            public int kernel_type, normalized_strategy, encoded_state_size, has_contact, has_interior, enabled;
            public uint chart_version;
            public int reserved;
            public long referent_source_space_id, referent_source_element_id, scope_shared_id;
            public long scope_source_space_id, scope_source_element_id;
            public int has_referent_source, has_scope_shared_id, has_scope_source, reserved2;
            public ulong source_binding_ordinal, binding_revision;
            public int has_binding_revision, r_formula_version;
            public double ap_offset_u_m, ap_offset_v_m, ap_height_m;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetRelationEvaluation
        {
            public uint struct_size, version;
            public IntPtr reason, sub_region_name;
            public double r_tgt, r_src, error, support_distance_m, normalizing_scope_diameter_m;
            public double contact_point_world_x, contact_point_world_y, contact_point_world_z;
            public double mapped_support_x, mapped_support_y, mapped_support_z;
            public double encoded_state0, encoded_state1, encoded_state2, encoded_state3;
            public double encoded_state4, encoded_state5, encoded_state6, encoded_state7;
            public long local_referent, normalizing_scope;
            public int disposition, encoded_state_size, has_contact, has_mapped_support;
            public double ap_offset_u_m, ap_offset_v_m, ap_height_m;
            public int support_basis, r_formula_version;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScopeRecord
        {
            public uint struct_size, version;
            public IntPtr anchor_name, tag_filter;
            public double anchor_x, anchor_y, anchor_z, anchor_yaw, time_seconds;
            public long binding_index, scope_root, kernel_scope_origin, deepest_containing, navigable_element;
            public long candidate_count, included_count;
            public int kernel_type, valid, anchor_active, explicit_scope, scope_live, reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScopeCandidate
        {
            public uint struct_size, version;
            public double proximity;
            public long element_id, shared_id;
            public int reason, reserved;
        }
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sb_get_last_emitted_update_sequence(long space, long actor);
        [DllImport("spatialblend", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sb_get_last_applied_remote_sequence(long receiverSpace, long sourceSpace, long sourceActor);
    }
}
#endif
