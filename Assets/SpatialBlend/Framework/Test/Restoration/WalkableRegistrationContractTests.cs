// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class WalkableRegistrationContractTests
    {
        [Test]
        public void Manager_RegistersTheLBoundaryInsteadOfItsConvexHull()
        {
            var go = new GameObject("walkable-l");
            var manager = go.AddComponent<RecordingWalkableManager>();
            try
            {
                Vector3[] vertices =
                {
                    new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 1f),
                    new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, 2f), new Vector3(0f, 0f, 2f)
                };
                Assert.That(manager.RegisterWalkableMesh(vertices,
                    new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5 }), Is.True);

                Assert.That(manager.Navigable, Has.Count.EqualTo(1));
                Assert.That(manager.Obstacles, Is.Empty);
                Assert.That(manager.NavigableElements, Is.EqualTo(new[] { 7L }));
                AssertRing(manager.Navigable[0], new[]
                {
                    new Vector2(0f, 0f), new Vector2(2f, 0f), new Vector2(2f, 1f),
                    new Vector2(1f, 1f), new Vector2(1f, 2f), new Vector2(0f, 2f)
                });
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // The "simple room": the ring is the room enlarged; each wall is an Obstacle under a footprint-less group of
        // its own (walls overlap at corners, and siblings may only touch), and a new room replaces the walls.
        [Test]
        public void Manager_RegistersTheRoomRingAndEachWallUnderItsOwnGroup()
        {
            var go = new GameObject("walkable-room");
            var manager = go.AddComponent<RecordingWalkableManager>();
            try
            {
                Vector2[] floor = { new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 3f), new Vector2(0f, 3f) };
                RoomRegion room = RoomRegion.Build(floor, null, null);
                Assert.That(room, Is.Not.Null);
                Assert.That(room.Walls, Has.Length.EqualTo(4));
                Assert.That(manager.RegisterRoomRegion(room), Is.True);
                Assert.That(manager.NavigableElements, Is.EqualTo(new[] { 7L }));
                Assert.That(manager.Navigable[0], Is.EqualTo(room.Ring));
                Assert.That(manager.ObstacleElements, Has.Count.EqualTo(4));
                Assert.That(manager.GroupParents, Is.EqualTo(new[] { 7L, 7L, 7L, 7L }), "one group per wall, under the room");
                Assert.That(manager.ObstacleParents.Distinct().Count(), Is.EqualTo(4), "each wall alone under its group");
                Assert.That(manager.RegisteredWalkable.Boundary, Is.EqualTo(room.Ring));
                Assert.That(manager.RegisteredRoom, Is.SameAs(room));
                List<long> groups = manager.ObstacleParents.ToList();

                manager.ClearRecord();
                Assert.That(manager.RegisterRoomRegion(RoomRegion.Build(floor, null, null)), Is.True);
                Assert.That(manager.Removed, Is.EqualTo(groups), "a new room replaces the walls");
                Assert.That(manager.ObstacleElements, Has.Count.EqualTo(4));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // A ring the core refuses is not the room's: the previous one stays registered, with its walls.
        [Test]
        public void Manager_KeepsThePreviousRoomWhenTheCoreRefusesTheNewOne()
        {
            var go = new GameObject("walkable-refused");
            var manager = go.AddComponent<RecordingWalkableManager>();
            try
            {
                Vector2[] floor = { new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 3f), new Vector2(0f, 3f) };
                Assert.That(manager.RegisterRoomRegion(RoomRegion.Build(floor, null, null)), Is.True);
                WalkableRegion previous = manager.RegisteredWalkable;
                RoomRegion previousRoom = manager.RegisteredRoom;
                manager.ClearRecord();
                manager.RefuseNavigable = true;

                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                    new System.Text.RegularExpressions.Regex("room's region was not registered"));
                Assert.That(manager.RegisterRoomRegion(RoomRegion.Build(floor, null, null)), Is.False);

                Assert.That(manager.RegisteredWalkable, Is.SameAs(previous));
                Assert.That(manager.RegisteredRoom, Is.SameAs(previousRoom));
                Assert.That(manager.Removed, Is.Empty, "The previous walls stay.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AnActorIsAdmittedUnderTheRoomOnlyOnceTheRoomHasItsFootprint()
        {
            var go = new GameObject("walkable-actor");
            go.SetActive(false);
            var manager = go.AddComponent<RecordingWalkableManager>();
            var avatar = new GameObject("walkable-avatar");
            avatar.SetActive(false);
            var entity = avatar.AddComponent<SbNetworkAvatarEntity>();
            var chair = new GameObject("walkable-chair");
            var chairEntity = chair.AddComponent<SbLocalEntity>();
            try
            {
                Assert.That(manager.TryAdmitActorShare(entity, out string error), Is.False);
                Assert.That(error, Does.Contain("no footprint"));
                Assert.That(manager.TryAdmitActorShare(chairEntity, out _), Is.True, "only actors need the room's footprint");

                Assert.That(manager.RegisterWalkableMesh(new[]
                {
                    new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f), new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f)
                }, new[] { 0, 1, 2, 0, 2, 3 }), Is.True);
                Assert.That(manager.TryAdmitActorShare(entity, out error), Is.True, error);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(chair);
            }
        }

        [Test]
        public void DegenerateMesh_DoesNotWriteAPolygon()
        {
            var go = new GameObject("walkable-degenerate");
            var manager = go.AddComponent<RecordingWalkableManager>();
            try
            {
                Assert.That(manager.RegisterWalkableMesh(
                    new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f) },
                    new[] { 0, 1, 2 }), Is.False);
                Assert.That(manager.Navigable, Is.Empty);
                Assert.That(manager.Removed, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
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

        private sealed class RecordingWalkableManager : SpatialBlendManager
        {
            public readonly List<Vector2[]> Navigable = new List<Vector2[]>();
            public readonly List<long> NavigableElements = new List<long>();
            public readonly List<Vector2[]> Obstacles = new List<Vector2[]>();
            public readonly List<long> ObstacleElements = new List<long>();
            public readonly List<long> ObstacleParents = new List<long>();
            public readonly List<long> GroupParents = new List<long>();
            public readonly List<int> ObstacleRoles = new List<int>();
            public readonly List<long> Removed = new List<long>();
            private long _nextHole = 20;
            private long _pendingParent;

            public void ClearRecord()
            {
                Navigable.Clear();
                NavigableElements.Clear();
                Obstacles.Clear();
                GroupParents.Clear();
                ObstacleElements.Clear();
                ObstacleParents.Clear();
                ObstacleRoles.Clear();
                Removed.Clear();
            }

            protected override void InitializeNativeLibrary() { }

            public override void Host(int port) { }

            protected override long EnsureWalkableRoom() => 7;

            protected override long AddWallElement(long parent)
            {
                _pendingParent = parent;
                if (parent == 7) GroupParents.Add(parent);
                return _nextHole++;
            }

            // The core refuses the room's ring (as it does when a registered region would stick out of it).
            public bool RefuseNavigable;

            protected override bool SetWalkablePolygon(long element, int role, IReadOnlyList<Vector2> points)
            {
                if (RefuseNavigable && role == (int)SbGeometryRole.Navigable) return false;
                var copy = new Vector2[points.Count];
                for (int i = 0; i < points.Count; ++i)
                    copy[i] = points[i];
                if (role == (int)SbGeometryRole.Navigable)
                {
                    NavigableElements.Add(element);
                    Navigable.Add(copy);
                }
                else if (role == (int)SbGeometryRole.Obstacle)
                {
                    ObstacleElements.Add(element);
                    ObstacleParents.Add(_pendingParent);
                    ObstacleRoles.Add(role);
                    Obstacles.Add(copy);
                }
                return true;
            }

            protected override bool RemoveWallElement(long element)
            {
                Removed.Add(element);
                return true;
            }
        }
    }
}
#endif
