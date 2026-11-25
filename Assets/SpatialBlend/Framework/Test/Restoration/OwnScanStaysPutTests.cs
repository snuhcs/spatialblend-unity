// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Internal;
using SpatialBlend.Objects;
using SpatialBlend.Samples.Objects;
using SpatialBlend.Samples.Scanning;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A scan is a REAL object, so it is not snapped: the box and the object stay exactly where they are. If it touches
    /// slightly outside the room, the core enlarges the room to include it. The user's own scan, through the product's
    /// install (GltfLoader.SetupLoadedObject with the drawn box, the sink's region binding) and share (ShareGameObject) against the
    /// REAL core library: a room with its walls and a piece of furniture, and boxes drawn over a wall (inside the room's enlarged
    /// ring, and past it) and against the furniture. Each scan stays exactly at the drawn pose and its region is the drawn box, at
    /// that pose, accepted by the core (a region past the ring extends the room's scope: containment_layout.h).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class OwnScanStaysPutTests
    {
        private static readonly FieldInfo ConnectOnAwake = typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InitLibrary = typeof(SpatialBlendManager).GetMethod("TryInitializeNativeLibrary", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AuthoredStates = typeof(SpatialBlendManager).GetField("_authoredStateByObject", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo DispatchUpdate = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);

        // A 4 m square room; a piece of furniture 0.8 x 0.6 m at x 0.5..1.3, z 0.5..1.1.
        private static readonly Vector2[] Floor = { new Vector2(-2f, -2f), new Vector2(2f, -2f), new Vector2(2f, 2f), new Vector2(-2f, 2f) };
        private static readonly Vector2[] FurnitureFoot = { new Vector2(0.5f, 0.5f), new Vector2(1.3f, 0.5f), new Vector2(1.3f, 1.1f), new Vector2(0.5f, 1.1f) };

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private WallTestManager _manager;

        // The real manager, with one seam: it can be told to have the core refuse the next wall piece(s).
        private sealed class WallTestManager : SpatialBlendManager
        {
            public int RefuseObstacleWrites;

            protected override bool SetWalkablePolygon(long element, int role, IReadOnlyList<Vector2> points)
            {
                if (role == (int)SbGeometryRole.Obstacle && RefuseObstacleWrites > 0)
                {
                    --RefuseObstacleWrites;
                    return false;
                }
                return base.SetWalkablePolygon(element, role, points);
            }
        }
        private GltfLoader _loader;
        private bool _ignoreFailing;
        private GameObject _scan;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            UnityMainThreadDispatcher.Initialize();
            // EditMode runs no Awake or Update: the dispatcher learns the main thread here and is pumped with the manager.
            typeof(UnityMainThreadDispatcher).GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            var managerGo = new GameObject("manager-scan");
            managerGo.SetActive(false);
            _objects.Add(managerGo);
            _manager = managerGo.AddComponent<WallTestManager>();
            ConnectOnAwake.SetValue(_manager, false);
            Assert.That((bool)InitLibrary.Invoke(_manager, null), Is.True, "the native library");
            _manager.HostTunneled();
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), _manager.ConnectionError);
            Assert.That(_manager.RegisterRoomRegion(RoomRegion.Build(Floor, null, new[] { FurnitureFoot })), Is.True, "the room");
            Assert.That(SceneGeometryDescriptor.TryCreate(7001, Matrix4x4.Translate(new Vector3(0.9f, 0.4f, 0.8f)),
                new Bounds(Vector3.zero, new Vector3(0.8f, 0.8f, 0.6f)), null, null, out SceneGeometryDescriptor furniture), Is.True);
            Assert.That(_manager.RegisterLocalSceneGeometry(furniture), Is.True, "the furniture");
            _manager.MarkLocalSceneGeometryRegistered();
            var loaderGo = new GameObject("scan-loader");
            _objects.Add(loaderGo);
            _loader = loaderGo.AddComponent<GltfLoader>();
        }

        [TearDown]
        public void TearDown()
        {
            try { _manager.Disconnect(); } catch (Exception e) { Debug.LogWarning("disconnect: " + e.Message); }
            try { _manager.OnDestroy(); } catch (Exception e) { Debug.LogWarning("destroy: " + e.Message); }
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            // What the session made that this test did not track (the partner's replica of a shared scan) goes too, so no later
            // test finds an entity in the scene.
            foreach (SbEntity left in UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (left != null) UnityEngine.Object.DestroyImmediate(left.transform.root.gameObject);
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        // 10 cm over the wall at x = 2, turned 20 degrees: inside the room's ring (the scanned room enlarged by 0.3 m). No wall
        // is cut there: the band behind the wall stays closed to avatars.
        [UnityTest]
        public IEnumerator ABoxDrawn10cmOverAWallStaysExactlyThereAndCutsNoWall()
        {
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(1.9f, 0.3f, -1f), new Vector3(0.4f, 0.6f, 0.4f), 20f);
            Assert.That(WholeWalls(), Is.EqualTo(4), "inside the ring: no wall is cut");
        }

        // Half a metre over the wall: past the room's ring. The wall gives way where the scan crosses it (RetrimWalls), so the
        // scan's region is taken and extends the room's scope (the core enlarges the room to include it).
        [UnityTest]
        public IEnumerator ABoxDrawnPastTheRoomsRingStaysExactlyThereAndTheCoreTakesItsRegion() =>
            AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.3f, 0.3f, 0f), new Vector3(0.4f, 0.6f, 0.4f), 0f);

        // Clear of the room's ring altogether (x 2.6..3.0, the ring ends at 2.3): the core joins it to the room by a bridge, which
        // the wall gives way to as well; the region is taken.
        [UnityTest]
        public IEnumerator ABoxDrawnClearOfTheRoomIsBridgedThroughTheWall() =>
            AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.8f, 0.3f, -0.5f), new Vector3(0.4f, 0.6f, 0.4f), 0f);

        // Turned 45 degrees and clear of the outline by about 14 cm: the core's bridge inflates the box with mitred corners, whose
        // tip reaches √2 times the gap along an axis; the wall gives way to all of it.
        [UnityTest]
        public IEnumerator ATurnedBoxClearOfTheRoomIsBridgedThroughTheWall() =>
            AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.65f, 0.3f, -0.5f), new Vector3(0.3f, 0.6f, 0.3f), 45f);

        // Against the furniture's edge (x = 0.5), on the floor.
        [UnityTest]
        public IEnumerator ABoxDrawnAgainstFurnitureStaysExactlyThere() =>
            AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(0.3f, 0.3f, 0.8f), new Vector3(0.4f, 0.6f, 0.4f), 0f);

        private IEnumerator AnOwnScanStaysExactlyAtTheDrawnBox(Vector3 center, Vector3 size, float yaw)
        {
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            _scan = null;
            var hint = new ScanRegionHint(center, size, rotation: turn);
            GameObject scan = Install(hint);
            Vector3 installedAt = scan.transform.position;
            Quaternion installedTurn = scan.transform.rotation;
            Assert.That(Vector3.Distance(installedAt, center), Is.LessThan(1e-3f), "installed at the drawn box");

            // The declared footprint is the drawn box exactly (its turn included).
            Vector2[] drawn = BoxCorners(center, size, turn);
            Vector2[] declared = FootprintOf(scan);
            AssertSameRing(declared, drawn, "the declared footprint is the drawn box");

            _manager.ShareGameObject(scan, true, true);
            for (int f = 0; f < 120; ++f) { Pump(); yield return null; }

            Assert.That(Vector3.Distance(scan.transform.position, installedAt), Is.LessThan(1e-3f), "never moved");
            Assert.That(Quaternion.Angle(scan.transform.rotation, installedTurn), Is.LessThan(0.1f), "never turned");
            SbEntity entity = scan.GetComponent<SbEntity>();
            Assert.That(entity.IsAttached(), Is.True, "shared");
            Assert.That(_manager.TryGetAuthoredRegion(entity.ID, out long region) && region > 0, Is.True,
                "the core took its region (" + (SpatialBlendAPI.TryGetSceneError(_manager.ID, out string err) ? err : "no error") + ")");
            _scan = scan;
            Vector2[] written = RegisteredRegion(entity.ID);
            Assert.That(written, Is.Not.Null);
            foreach (Vector2 corner in drawn)
                Assert.That(Inside(corner, written, 1e-3f), Is.True, $"the region written holds the drawn box ({corner})");
        }

        // The wall the scan crosses is cut by the scan's own region (and no more); the other walls stay whole; when the scan goes,
        // the wall is whole again. A virtual object put into the gap is not let through: the room rule pulls it back onto the floor,
        // clear of the scan.
        [UnityTest]
        public IEnumerator TheWallGivesWayOnlyWhereTheScanIsAndAVirtualObjectCannotUseTheGap()
        {
            int whole = WholeWalls();
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.3f, 0.3f, 0f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            Vector2[] scanRegion = RegisteredRegion(_scan.GetComponent<SbEntity>().ID);
            Assert.That(WholeWalls(), Is.EqualTo(whole - 1), "only the wall the scan crosses is cut");
            foreach (Vector2[][] pieces in _manager.WallPieces)
                foreach (Vector2[] piece in pieces)
                    Assert.That(SceneFootprintLayout.IntersectionArea(piece, scanRegion), Is.LessThan(1e-6), "no wall piece overlaps the scan's region");
            Assert.That(_manager.RoomWalls.Count, Is.EqualTo(4), "the room rule's walls (Unity's) are the full walls");

            // A virtual object put into the gap, half over the scan's region: pulled back inside the room, clear of the scan.
            var hint = new ScanRegionHint(new Vector3(2.3f, 0.3f, 0.35f), new Vector3(0.3f, 0.4f, 0.3f));
            GameObject virtualObject = VirtualBox(hint);
            _manager.ShareGameObject(virtualObject, true, true);
            for (int f = 0; f < 60; ++f) { Pump(); yield return null; }
            Vector2[] foot = FootprintOf(virtualObject);
            foreach (Vector2 v in foot) Assert.That(v.x, Is.LessThanOrEqualTo(2f + 1e-3f), "on the room's floor, not through the gap");
            Assert.That(SceneFootprintLayout.IntersectionArea(foot, scanRegion), Is.LessThan(1e-6), "clear of the scan");

            // The scan goes: its region leaves the core with it (unshared, removed), and the wall is whole again.
            SbEntity scanEntity = _scan.GetComponent<SbEntity>();
            Assert.That(_manager.TryGetAuthoredRegion(scanEntity.ID, out long scanRegionElement), Is.True);
            scanEntity.Detach();
            bool regionGone = !SpatialBlendAPI.TryGetElementParent(_manager.ID, scanRegionElement, out _);
            TestContext.WriteLine($"after the scan's removal the core {(regionGone ? "no longer holds" : "still holds")} its region element");
            Assert.That(regionGone, Is.True, "the removed scan's region left the core");
            Assert.That(WholeWalls(), Is.EqualTo(whole), "the wall is whole again");
        }

        // A pose the core refuses for an own scan's region (here: over another scan's region, which cannot be
        // trimmed) withdraws the region (no identity, no transfer leaf, not the object's region here), and the object element is
        // offered to the room at the pose the object really has. For the move the region is shrunk to the object's footprint under
        // the same parent, so the core refuses the object there for the same reason: that is logged, and the object element stays
        // under the withdrawn region at the pose the core holds. The Unity object stays put; a later pose the core takes declares
        // the region again. With the old rule (the old region kept as the object's) this fails at "no region is kept".
        [UnityTest]
        public IEnumerator ARefusedPoseWithdrawsTheRegionAndALaterPoseDeclaresItAgain()
        {
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(-1f, 0.3f, -1f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            SbEntity entity = _scan.GetComponent<SbEntity>();
            Assert.That(_manager.TryGetAuthoredRegion(entity.ID, out long oldRegion) && oldRegion > 0, Is.True);
            Vector3 home = _scan.transform.position;

            // A second own scan stands at B; its region is its footprint, which cannot be trimmed: the core refuses the first
            // scan's region over it (sibling regions may only touch).
            var overOther = new Vector3(-1f, 0.3f, 1f);
            GameObject other = Install(new ScanRegionHint(overOther, new Vector3(0.4f, 0.6f, 0.4f), rotation: Quaternion.identity));
            _manager.ShareGameObject(other, true, true);
            for (int f = 0; f < 60; ++f) { Pump(); yield return null; }
            Assert.That(_manager.TryGetAuthoredRegion(other.GetComponent<SbEntity>().ID, out long otherRegion) && otherRegion > 0, Is.True);
            _scan.transform.position = overOther;
            // The refusals are the point here (the core's overlap, the other scan's untrimmable region).
            LogAssert.ignoreFailingMessages = true;
            Assert.That(_manager.RefitPhysicalAtPose(entity.ID, _scan.transform.localToWorldMatrix), Is.False, "the core refused it");
            Assert.That(Vector3.Distance(_scan.transform.position, overOther), Is.LessThan(1e-4f), "the Unity object stays put");
            Assert.That(_manager.TryGetAuthoredRegion(entity.ID, out _), Is.False, "no region is kept at a pose it is not at");
            Assert.That(_manager.RegionWithdrawn(entity.ID), Is.True, "withdrawn: no identity, no transfer leaf");
            Assert.That(_manager.ObjectMovedOutOfRegion(entity.ID), Is.False, "the core refused the object there too");
            Assert.That(SpatialBlendAPI.TryGetElementParent(_manager.ID, entity.ID, out long parent) && parent == oldRegion, Is.True,
                "the object element stays at the pose the core holds");

            // Back where the core takes it: the region is declared again.
            _scan.transform.position = home;
            _scan.transform.hasChanged = false;
            Assert.That(_manager.RefitPhysicalAtPose(entity.ID, _scan.transform.localToWorldMatrix), Is.True);
            Assert.That(_manager.TryGetAuthoredRegion(entity.ID, out long again) && again == oldRegion, Is.True, "declared again");
            Assert.That(_manager.RegionWithdrawn(entity.ID), Is.False);
        }


        // Two scans past the ring on one wall: each cuts its own gap; both regions are taken; no piece overlaps either.
        [UnityTest]
        public IEnumerator TwoScansOnOneWallEachCutTheirOwnGap()
        {
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.3f, 0.3f, -0.8f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            GameObject first = _scan;
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.3f, 0.3f, 0.8f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            GameObject second = _scan;
            Assert.That(WholeWalls(), Is.EqualTo(3), "one wall cut, twice");
            Vector2[] a = RegisteredRegion(first.GetComponent<SbEntity>().ID), b = RegisteredRegion(second.GetComponent<SbEntity>().ID);
            int cut = 0;
            foreach (Vector2[][] pieces in _manager.WallPieces)
            {
                if (pieces.Length > 1) cut = pieces.Length;
                foreach (Vector2[] piece in pieces)
                {
                    Assert.That(SceneFootprintLayout.IntersectionArea(piece, a), Is.LessThan(1e-6));
                    Assert.That(SceneFootprintLayout.IntersectionArea(piece, b), Is.LessThan(1e-6));
                }
            }
            Assert.That(cut, Is.GreaterThanOrEqualTo(3), "the wall in at least three pieces around two gaps");
        }

        // A scan past the room's corner crosses two walls: both give way; its region is taken.
        [UnityTest]
        public IEnumerator AScanPastACornerCutsBothWalls()
        {
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.25f, 0.3f, 2.25f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            Assert.That(WholeWalls(), Is.LessThanOrEqualTo(2), "both walls at the corner are cut");
            Vector2[] region = RegisteredRegion(_scan.GetComponent<SbEntity>().ID);
            foreach (Vector2[][] pieces in _manager.WallPieces)
                foreach (Vector2[] piece in pieces)
                    Assert.That(SceneFootprintLayout.IntersectionArea(piece, region), Is.LessThan(1e-6));
        }

        // A retrim that changes nothing writes nothing: again by itself, and at a refit to the same pose.
        [UnityTest]
        public IEnumerator ARecutThatChangesNothingRewritesNoWall()
        {
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.3f, 0.3f, 0f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            int writes = _manager.WallPieceWrites;
            _manager.RetrimWalls();
            Assert.That(_manager.RefitPhysicalAtPose(_scan.GetComponent<SbEntity>().ID, _scan.transform.localToWorldMatrix), Is.True);
            _manager.RetrimWalls();
            Assert.That(_manager.WallPieceWrites, Is.EqualTo(writes), "no wall written again");
        }

        // A wall piece the core refuses is not taken as standing: the next retrim writes it again.
        [UnityTest]
        public IEnumerator ARefusedWallPieceIsWrittenAgainByTheNextRetrim()
        {
            _manager.RefuseObstacleWrites = 1;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("A wall obstacle was not registered"));
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(2.3f, 0.3f, 0f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            Assert.That(_manager.RefuseObstacleWrites, Is.EqualTo(0), "a piece was refused");
            Assert.That(_manager.WallPieces.Count(p => p == null), Is.EqualTo(0).Or.EqualTo(1));
            _manager.RetrimWalls();
            Assert.That(_manager.WallPieces.All(p => p != null), Is.True, "every wall stands as computed");
            int writes = _manager.WallPieceWrites;
            _manager.RetrimWalls();
            Assert.That(_manager.WallPieceWrites, Is.EqualTo(writes), "and is not written again after that");
        }

        // Removing a virtual object and a board takes their regions (and the board's surfaces) out of the core and
        // touches nothing else (an own scan's region stays).
        [UnityTest]
        public IEnumerator RemovingAVirtualObjectAndABoardTakesTheirRegionsAndNothingElse()
        {
            yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(-1f, 0.3f, -1f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
            long scanRegion = Region(_scan);

            GameObject box = VirtualBox(new ScanRegionHint(new Vector3(-1f, 0.3f, 1f), new Vector3(0.3f, 0.4f, 0.3f)));
            GameObject board = Board(new Vector3(0f, 0.5f, -0.5f));
            _manager.ShareGameObject(box, true, true);
            _manager.ShareGameObject(board, true, true);
            for (int f = 0; f < 60; ++f) { Pump(); yield return null; }
            long boxRegion = Region(box), boardRegion = Region(board);
            long[] boardSurfaces = Affordances(board);
            Assert.That(boardSurfaces, Is.Not.Empty, "the board has surfaces");

            box.GetComponent<SbEntity>().Detach();
            Assert.That(InCore(boxRegion), Is.False, "the virtual object's region left the core");
            Assert.That(InCore(boardRegion) && InCore(scanRegion), Is.True, "nothing else was touched");

            board.GetComponent<SbEntity>().Detach();
            Assert.That(InCore(boardRegion), Is.False, "the board's region left the core");
            foreach (long surface in boardSurfaces) Assert.That(InCore(surface), Is.False, "and its surfaces");
            Assert.That(InCore(scanRegion), Is.True, "the scan's region stays");
            Assert.That(_manager.RemovedRegionsStripped, Is.Zero);
        }

        // With a partner connected (it received the scan's region), removing the scan takes the region out of this
        // space (unshared, then deleted; not stripped in place) and out of the partner's.
        [UnityTest]
        public IEnumerator WithAPartnerConnectedTheRemovedScansRegionLeavesBothSpaces()
        {
            var joinerGo = new GameObject("manager-partner");
            joinerGo.SetActive(false);
            _objects.Add(joinerGo);
            SpatialBlendManager partner = joinerGo.AddComponent<SpatialBlendManager>();
            ConnectOnAwake.SetValue(partner, false);
            Assert.That((bool)InitLibrary.Invoke(partner, null), Is.True);
            CoreLink hostLink = _manager.AcceptCoreLink(), joinLink = partner.JoinTunneled();
            Assert.That(hostLink, Is.Not.Null);
            Assert.That(joinLink, Is.Not.Null);
            var toJoiner = new List<byte>();
            var toHost = new List<byte>();
            void Shuttle()
            {
                Move(hostLink, joinLink, toJoiner);
                Move(joinLink, hostLink, toHost);
            }
            for (int f = 0; f < 3000 && partner.ConnectionState != SpatialBlendConnectionState.Connected; ++f)
            {
                Shuttle();
                partner.PollTunneledJoin();
                yield return null;
            }
            Assert.That(partner.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), partner.ConnectionError);
            try
            {
                yield return AnOwnScanStaysExactlyAtTheDrawnBox(new Vector3(-1f, 0.3f, -1f), new Vector3(0.4f, 0.6f, 0.4f), 0f);
                long region = Region(_scan);
                long regionShared = RegionSharedId(_scan);
                long received = 0;
                for (int f = 0; f < 600 && received <= 0; ++f)
                {
                    Shuttle();
                    partner.Update();
                    Pump();
                    received = SpatialBlendAPI.FindElementBySharedId(partner.ID, regionShared);
                    yield return null;
                    LogAssert.ignoreFailingMessages = true;   // the partner has no app for the scan's payload: only its region is read
                }
                Assert.That(received, Is.GreaterThan(0), "the partner received the scan's region");

                _scan.GetComponent<SbEntity>().Detach();
                for (int f = 0; f < 300; ++f)
                {
                    Shuttle();
                    partner.Update();
                    Pump();
                    yield return null;
                    LogAssert.ignoreFailingMessages = true;
                }
                TestContext.WriteLine($"removal: stripped in place {_manager.RemovedRegionsStripped}, in this space {InCore(region)}, " +
                                      $"at the partner {SpatialBlendAPI.FindElementBySharedId(partner.ID, regionShared)}");
                Assert.That(_manager.RemovedRegionsStripped, Is.Zero, "removed, not stripped in place (the core kept no source link)");
                Assert.That(InCore(region), Is.False, "gone from this space");
                Assert.That(SpatialBlendAPI.FindElementBySharedId(partner.ID, regionShared), Is.LessThanOrEqualTo(0), "and from the partner's");
            }
            finally
            {
                try { partner.Disconnect(); } catch (Exception e) { Debug.LogWarning("partner disconnect: " + e.Message); }
                try { partner.OnDestroy(); } catch (Exception e) { Debug.LogWarning("partner destroy: " + e.Message); }
            }
        }

        private static readonly byte[] s_buffer = new byte[1 << 16];

        private static void Move(CoreLink from, CoreLink to, List<byte> pending)
        {
            for (int guard = 0; guard < 64 && from.Read(s_buffer, out int n) == CoreLinkRead.Open && n > 0; ++guard)
                for (int i = 0; i < n; ++i) pending.Add(s_buffer[i]);
            if (pending.Count == 0) return;
            byte[] bytes = pending.ToArray();
            if (to.Write(bytes, bytes.Length) == CoreLinkWrite.Accepted) pending.Clear();
        }

        private bool InCore(long element) => element > 0 && SpatialBlendAPI.TryGetElementParent(_manager.ID, element, out _);

        private long Region(GameObject root)
        {
            Assert.That(_manager.TryGetAuthoredRegion(root.GetComponent<SbEntity>().ID, out long region) && region > 0, Is.True,
                root.name + " has its region");
            return region;
        }

        private object State(GameObject root)
        {
            var states = (IDictionary)AuthoredStates.GetValue(_manager);
            return states[root.GetComponent<SbEntity>().ID];
        }

        private long[] Affordances(GameObject root)
        {
            object st = State(root);
            return (long[])st.GetType().GetField("AffordanceElements").GetValue(st);
        }

        private long RegionSharedId(GameObject root)
        {
            object st = State(root);
            return SpatialAuthoringCompiler.RegionSharedId((long)st.GetType().GetField("ObjectSharedId").GetValue(st));
        }

        // The scene's Chess board (a network object with its region and its top surface), with a network id of its own.
        private GameObject Board(Vector3 at)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            GameObject go = UnityEngine.Object.Instantiate(prefab);
            _objects.Add(go);
            SbEntity entity = go.GetComponentInChildren<SbEntity>(true);
            GameObject root = entity.gameObject;
            root.transform.position = at;
            SetNetworkId(root, 7);
            return root;
        }

        private static void SetNetworkId(GameObject entity, uint raw)
        {
            var networkObject = entity.GetComponent<Fusion.NetworkObject>();
            Assert.That(networkObject, Is.Not.Null, entity.name + " is a network object");
            object id = Activator.CreateInstance(typeof(Fusion.NetworkId));
            typeof(Fusion.NetworkId).GetField("Raw").SetValue(id, raw);
            PropertyInfo property = typeof(Fusion.NetworkObject).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property.GetSetMethod(true) != null) { property.GetSetMethod(true).Invoke(networkObject, new[] { id }); return; }
            IntPtr header = System.Runtime.InteropServices.Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i += 4) System.Runtime.InteropServices.Marshal.WriteInt32(header, i, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(header, 0, (int)raw);
            FieldInfo ptr = typeof(Fusion.NetworkObject).GetField("Ptr", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = new System.Reflection.Emit.DynamicMethod("SetPtr", typeof(void),
                new[] { typeof(Fusion.NetworkObject), typeof(IntPtr) }, typeof(Fusion.NetworkObject), true);
            System.Reflection.Emit.ILGenerator il = setter.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Stfld, ptr);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            setter.Invoke(null, new object[] { networkObject, header });
        }

        private int WholeWalls() => _manager.WallPieces.Count(p => p != null && p.Length == 1);

        // A virtual object with the drawn box as its region (no physical counterpart): the room rule places it.
        private GameObject VirtualBox(ScanRegionHint hint)
        {
            var root = new GameObject("virtual-box");
            _objects.Add(root);
            root.transform.position = hint.BoxCenter;
            var collider = root.AddComponent<BoxCollider>();
            collider.size = hint.BoxSize;
            var entity = root.AddComponent<SbNativeEntity>();
            typeof(SbNativeEntity).GetMethod("EnsureSerializationBlocks", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
            Assert.That(BoxAnnotationAuthoring.Declare(root, hint), Is.Not.Null);
            return root;
        }

        // The product's own-scan install: the template's parts (a collider, the entity), the model under it, the loader's fit into
        // the drawn box, then the sink's binding of the box as the region (GltfScanResultSink.Bind).
        private GameObject Install(ScanRegionHint hint)
        {
            var root = new GameObject("own-scan");
            _objects.Add(root);
            root.AddComponent<BoxCollider>();
            var entity = root.AddComponent<SbNativeEntity>();
            typeof(SbNativeEntity).GetMethod("EnsureSerializationBlocks", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
            var geometry = new GameObject(ScanObjectFit.GeometryNodeName);
            geometry.transform.SetParent(root.transform, false);
            geometry.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            geometry.AddComponent<MeshRenderer>();
            _loader.SetupLoadedObject(root, true, null, hint, useServerPose: false);
            Assert.That(root.GetComponent<PhysicalCounterpart>(), Is.Not.Null);
            var result = new ScanReconstructionResult { ObjectName = "own-scan", HasRegionHint = true, RegionHint = hint };
            Assert.That(GltfScanResultSink.Bind(result, root), Is.Not.Null, "the drawn box is declared");
            return root;
        }

        private void Pump()
        {
            DispatchUpdate.Invoke(UnityMainThreadDispatcher.Instance, null);
            _manager.Update();
            _manager.LateUpdate();
        }

        private Vector2[] RegisteredRegion(long id)
        {
            var states = (IDictionary)AuthoredStates.GetValue(_manager);
            if (!states.Contains(id)) return null;
            object st = states[id];
            var polygon = (Vector2[])st.GetType().GetField("RegionPolygon").GetValue(st);
            var frame = (Matrix4x4)st.GetType().GetField("RegionFrameWorld").GetValue(st);
            return polygon?.Select(v => { Vector3 w = frame.MultiplyPoint3x4(new Vector3(v.x, 0f, v.y)); return new Vector2(w.x, w.z); }).ToArray();
        }

        private static Vector2[] FootprintOf(GameObject root)
        {
            SbSpatialAuthoring authoring = root.GetComponent<SbSpatialAuthoring>();
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration d, out string e1), Is.True, e1);
            Assert.That(SpatialAuthoringCompiler.TryCompile(d, out SpatialAuthoringPlan plan, out string e2), Is.True, e2);
            return SpatialBlendManager.WorldGroundRing(plan, root.transform.localToWorldMatrix * plan.FrameToObject, SpatialHierarchyNodeKind.Object);
        }

        private static Vector2[] BoxCorners(Vector3 c, Vector3 s, Quaternion turn)
        {
            var corners = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) };
            return corners.Select(k => { Vector3 w = c + turn * Vector3.Scale(k, s * 0.5f); return new Vector2(w.x, w.z); }).ToArray();
        }

        private static void AssertSameRing(Vector2[] actual, Vector2[] expected, string what)
        {
            Assert.That(actual, Is.Not.Null, what);
            foreach (Vector2 e in expected)
                Assert.That(actual.Min(a => Vector2.Distance(a, e)), Is.LessThan(1e-3f), $"{what}: corner {e} (got {string.Join(" ", actual)})");
        }

        private static bool Inside(Vector2 p, Vector2[] ring, float tolerance)
        {
            if (WalkablePlacementSnap.Contains(ring, p)) return true;
            for (int i = 0; i < ring.Length; ++i)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Length], ab = b - a;
                float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                if (Vector2.Distance(p, a + ab * t) <= tolerance) return true;
            }
            return false;
        }
    }
}
#endif
