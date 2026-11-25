// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Internal;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The SOURCE avatar's head anchor follows the tracked head. The actor's root is the 2D floor point under the head
    /// (UpdateV2EntityState hands the core the head transform, and the core keeps x/y), and the "head" anchor was registered
    /// at a fixed 1.58 m above it and never updated, so the core saw every source's head at 1.58 m whatever the user's
    /// height or posture. On every source update the manager now writes the head anchor's own world pose
    /// (sb_set_anchor_transform) before the actor state, and clears it when the actor is released.
    /// The native cases (<see cref="SourceHeadAnchorNativeTests"/>) show what it changes in the core: the Facing sightline.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SourceHeadAnchorTests
    {
        private const long Actor = 41L;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [TestCase(1.10f, TestName = "ASeatedSourcesHeadAnchorIsItsTrackedHead")]
        [TestCase(1.75f, TestName = "AStandingSourcesHeadAnchorIsItsTrackedHead")]
        public void TheHeadAnchorIsTheTrackedHeadAndTheRootStaysOnTheFloor(float eyeHeight)
        {
            HeadManager manager = NewManager();
            SbLocalEntity avatar = Bind(manager, "Avatar", source: true, headAnchor: true);
            avatar.transform.SetPositionAndRotation(new Vector3(1.5f, eyeHeight, -2f), Quaternion.Euler(12f, 35f, 0f));

            manager.UpdateV2EntityState(avatar);

            Assert.That(manager.Calls, Is.EqualTo(new[] { "head", "state" }),
                "the head is written before the update that grounds its relations");
            Assert.That(manager.Heads.Count, Is.EqualTo(1));
            Assert.That(manager.Heads[0].actor, Is.EqualTo(Actor));
            Assert.That(Vector3.Distance(manager.Heads[0].position, new Vector3(1.5f, eyeHeight, -2f)), Is.LessThan(1e-5f));
            Assert.That(manager.Heads[0].yaw, Is.EqualTo(35f).Within(1e-3f));

            // The next frame follows the head (here the user stands up or sits down), every frame.
            avatar.transform.position = new Vector3(1.6f, eyeHeight + 0.4f, -2f);
            manager.UpdateV2EntityState(avatar);
            Assert.That(manager.Heads.Count, Is.EqualTo(2));
            Assert.That(manager.Heads[1].position.y, Is.EqualTo(eyeHeight + 0.4f).Within(1e-5f));
        }

        // Every anchor write is an observation change to the core, which sends the relations past its movement gate; a head
        // that moves less than that gate is not written again, so a still head does not send every frame.
        [Test]
        public void AHeadThatMovesLessThanTheCoresGateIsNotWrittenAgain()
        {
            HeadManager manager = NewManager();
            SbLocalEntity avatar = Bind(manager, "Avatar", source: true, headAnchor: true);
            avatar.transform.SetPositionAndRotation(new Vector3(0f, 1.1f, 0f), Quaternion.Euler(0f, 20f, 0f));
            manager.UpdateV2EntityState(avatar);

            avatar.transform.SetPositionAndRotation(new Vector3(0.01f, 1.12f, 0f), Quaternion.Euler(0f, 20.02f, 0f));
            manager.UpdateV2EntityState(avatar);
            Assert.That(manager.Heads.Count, Is.EqualTo(1), "1 cm and 0.02 degrees are inside the gate");
            Assert.That(manager.StateUpdates, Is.EqualTo(2), "the actor state still ticks every frame");

            avatar.transform.rotation = Quaternion.Euler(0f, 20.2f, 0f);
            manager.UpdateV2EntityState(avatar);
            Assert.That(manager.Heads.Count, Is.EqualTo(2), "a turn past the gate is written");

            avatar.transform.position = new Vector3(0.01f, 1.18f, 0f);
            manager.UpdateV2EntityState(avatar);
            Assert.That(manager.Heads.Count, Is.EqualTo(3), "rising 6 cm (from the last write) is written");
            Assert.That(manager.Heads[2].position.y, Is.EqualTo(1.18f).Within(1e-5f));
        }

        [Test]
        public void AnObjectActorAReceivedAvatarAndAnActorWithNoHeadAnchorKeepTheRegisteredOffset()
        {
            HeadManager manager = NewManager();
            SbLocalEntity thing = Bind(manager, "Untagged", source: true, headAnchor: true, id: 8, actor: 51);
            SbLocalEntity received = Bind(manager, "Avatar", source: false, headAnchor: true, id: 9, actor: 52);
            SbLocalEntity headless = Bind(manager, "Avatar", source: true, headAnchor: false, id: 10, actor: 53);

            manager.UpdateV2EntityState(thing);
            manager.UpdateV2EntityState(received);
            manager.UpdateV2EntityState(headless);

            Assert.That(manager.Heads, Is.Empty);
            Assert.That(manager.StateUpdates, Is.EqualTo(3), "each still updates its actor state");
        }

        [Test]
        public void ReleasingTheActorClearsItsHeadOnceAndOnlyAfterItWasWritten()
        {
            HeadManager manager = NewManager();
            SbLocalEntity written = Bind(manager, "Avatar", source: true, headAnchor: true);
            SbLocalEntity never = Bind(manager, "Avatar", source: true, headAnchor: true, id: 12, actor: 62);
            written.transform.position = new Vector3(0f, 1.2f, 0f);
            manager.UpdateV2EntityState(written);
            SetField(manager, "_spaceID", 5L);

            Detach(written);
            Detach(never);

            Assert.That(manager.Clears, Is.EqualTo(new[] { (5L, Actor) }));
            Assert.That(TrackedHeads(manager), Is.Empty);
        }

        // A re-share of the same avatar (released, then shared again) finds its actor in the core (FindActorBySharedId) and does not
        // add its anchors again. The manager still knows them (they live as long as the native actor), so the head is written
        // again on the same actor and a relation on the head finds it declared. The real manager and library, a tunneled host.
        [Test]
        public void AReSharedAvatarsHeadIsWrittenAgainOnTheSameActor()
        {
            bool ignoring = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            UnityMainThreadDispatcher.Initialize();
            typeof(UnityMainThreadDispatcher).GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            var managerObject = new GameObject("source-head-reshare-manager");
            managerObject.SetActive(false);
            _objects.Add(managerObject);
            var manager = managerObject.AddComponent<RecordingNativeManager>();
            SetField(manager, "_connectOnAwake", false);
            try
            {
                Assert.That((bool)typeof(SpatialBlendManager).GetMethod("TryInitializeNativeLibrary",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null), Is.True, "the native library");
                manager.HostTunneled();
                Assert.That(manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), manager.ConnectionError);

                var avatarObject = new GameObject("source-head-reshare-avatar") { tag = "Avatar" };
                _objects.Add(avatarObject);
                var avatar = avatarObject.AddComponent<SbLocalEntity>();
                avatar.AllowManipulation = false;
                avatarObject.transform.position = new Vector3(0.5f, 1.2f, 0.5f);

                manager.ShareGameObject(avatarObject, true, true);
                Pump(manager);
                Assert.That(avatar.IsAttached(), Is.True, "shared");
                long actor = ActorOf(manager, avatar);
                Assert.That(actor, Is.GreaterThan(0));
                Assert.That(manager.Heads, Has.Some.Matches<(long actor, Vector3 position, float yaw)>(
                    h => h.actor == actor && Mathf.Abs(h.position.y - 1.2f) < 1e-4f), "the first share writes the head");

                typeof(SbEntity).GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(avatar, null);
                Assert.That(manager.Clears, Has.Member(actor), "the release clears the head");

                avatarObject.transform.position = new Vector3(0.5f, 1.7f, 0.5f);
                manager.Heads.Clear();
                manager.ShareGameObject(avatarObject, true, true);
                Pump(manager);
                Assert.That(avatar.IsAttached(), Is.True, "shared again");
                Assert.That(ActorOf(manager, avatar), Is.EqualTo(actor), "the re-share finds the same actor in the core");
                var anchors = (HashSet<(long actor, string name)>)GetField(manager, "_v2ActorAnchors");
                Assert.That(anchors.Contains((actor, "head")) && anchors.Contains((actor, "root")), Is.True,
                    "its anchors are known again (RegisterRelation finds the head declared; RegisterAnchor adds no duplicate)");
                Assert.That(manager.Heads, Has.Some.Matches<(long actor, Vector3 position, float yaw)>(
                    h => h.actor == actor && Mathf.Abs(h.position.y - 1.7f) < 1e-4f), "the re-shared avatar's head is written again");
            }
            finally
            {
                try { manager.Disconnect(); } catch (Exception e) { Debug.LogWarning("disconnect: " + e.Message); }
                try { manager.OnDestroy(); } catch (Exception e) { Debug.LogWarning("destroy: " + e.Message); }
                // The session's replicas of the shared avatar (its local peer's copies) go too, so no later test finds an entity.
                foreach (SbEntity left in Object.FindObjectsByType<SbEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (left != null) Object.DestroyImmediate(left.transform.root.gameObject);
                LogAssert.ignoreFailingMessages = ignoring;
            }
        }

        private static void Pump(SpatialBlendManager manager)
        {
            MethodInfo dispatch = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int frame = 0; frame < 3; ++frame)
            {
                dispatch.Invoke(UnityMainThreadDispatcher.Instance, null);
                manager.Update();
                manager.LateUpdate();
            }
        }

        private static long ActorOf(SpatialBlendManager manager, SbEntity entity)
        {
            var map = (IDictionary)GetField(manager, "_v2ActorByEntity");
            return map.Contains(entity.ID) ? (long)map[entity.ID] : 0;
        }

        // ------------------------------------------------------------------ helpers

        private HeadManager NewManager()
        {
            var go = new GameObject("source-head-manager");
            go.SetActive(false);
            _objects.Add(go);
            var manager = go.AddComponent<HeadManager>();
            SetField(manager, "_connectOnAwake", false);
            return manager;
        }

        private SbLocalEntity Bind(HeadManager manager, string tag, bool source, bool headAnchor, long id = 7,
                                     long actor = Actor)
        {
            var go = new GameObject("source-head-entity-" + id);
            go.tag = tag;
            go.SetActive(false);
            _objects.Add(go);
            var entity = go.AddComponent<SbLocalEntity>();
            entity.AllowManipulation = false;
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(entity, new object[] { manager, id, source });
            ((IDictionary)GetField(manager, "_v2ActorByEntity")).Add(id, actor);
            ((IDictionary)GetField(manager, "_v2EntityByActor")).Add(actor, id);
            var anchors = (HashSet<(long actor, string name)>)GetField(manager, "_v2ActorAnchors");
            anchors.Add((actor, "root"));
            if (headAnchor) anchors.Add((actor, "head"));
            return entity;
        }

        private static void Detach(SbEntity entity) =>
            typeof(SbEntity).GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);

        internal static IDictionary TrackedHeads(SpatialBlendManager manager) => (IDictionary)GetField(manager, "_v2TrackedHeads");

        internal static object GetField(SpatialBlendManager manager, string name) =>
            typeof(SpatialBlendManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);

        internal static void SetField(SpatialBlendManager manager, string name, object value) =>
            typeof(SpatialBlendManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, value);

        /// <summary>The real native writes (no seam overridden); never awoken, so it opens no session.</summary>
        public sealed class HeadManagerNative : SpatialBlendManager { }

        /// <summary>The real manager and native writes, recording the head writes and clears it makes.</summary>
        public sealed class RecordingNativeManager : SpatialBlendManager
        {
            public readonly List<(long actor, Vector3 position, float yaw)> Heads = new List<(long, Vector3, float)>();
            public readonly List<long> Clears = new List<long>();

            protected override void SetNativeSourceHead(long actor, Vector3 worldPosition, float yawDegrees)
            {
                Heads.Add((actor, worldPosition, yawDegrees));
                base.SetNativeSourceHead(actor, worldPosition, yawDegrees);
            }

            protected override void ClearNativeSourceHead(long space, long actor)
            {
                Clears.Add(actor);
                base.ClearNativeSourceHead(space, actor);
            }
        }

        public sealed class HeadManager : SpatialBlendManager
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<(long actor, Vector3 position, float yaw)> Heads = new List<(long, Vector3, float)>();
            public readonly List<(long space, long actor)> Clears = new List<(long, long)>();
            public int StateUpdates;

            protected override void SetNativeSourceHead(long actor, Vector3 worldPosition, float yawDegrees)
            {
                Calls.Add("head");
                Heads.Add((actor, worldPosition, yawDegrees));
            }

            protected override void ClearNativeSourceHead(long space, long actor) => Clears.Add((space, actor));

            protected override void UpdateNativeActorState(long actor, Vector3 position, float yawDegrees, float? headPitchDegrees)
            {
                Calls.Add("state");
                ++StateUpdates;
            }
        }
    }

    /// <summary>
    /// The same path against the real core (a libspatialblend that reports a person's body top): a source
    /// avatar sits or stands 0.3 m behind a 1.2 m counter and looks at another person 4 m ahead. The core sees the person at
    /// its body's top (its highest registered anchor, 1.58 m) from the source's eye, the head anchor. A seated eye (1.1 m)
    /// cannot see over the counter, a standing one (1.75 m) can. Before the fix the eye was 1.58 m in both postures, so the
    /// seated source "saw" the person. Released, the head anchor is the registered 1.58 m again.
    /// </summary>
    [TestFixture]
    // Explicit: needs a core that reports person body tops, so an unfiltered Run All against an older fetched
    // library never runs it. tools/run-freeform-native-tests.sh --source-head selects the category with the supplied library.
    [Explicit("Needs a core that reports person body tops: run through tools/run-freeform-native-tests.sh --source-head")]
    [Category("SourceHeadNative")]
    public sealed class SourceHeadAnchorNativeTests
    {
        private const long RoomShared = 500;
        private const long SourceShared = 600;
        private const long PersonShared = 700;
        private const long CounterShared = 800;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private SourceHeadAnchorTests.HeadManagerNative _manager;
        private long _space;

        [SetUp]
        public void SetUp() => SpatialBlendAPI.Initialize(false);

        [TearDown]
        public void TearDown()
        {
            if (_manager != null) SourceHeadAnchorTests.SetField(_manager, "_spaceID", -1L);
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            SpatialBlendAPI.CleanupAll();
        }

        [Test]
        public void ASeatedSourceBehindACounterDoesNotSeeThePersonAndAStandingOneDoes()
        {
            SbEntity avatar = BuildScene(out long sourceActor);

            Assert.That(SeesPerson(avatar, sourceActor, 1.75f), Is.True, "standing, the eye (1.75 m) clears the 1.2 m counter");
            Assert.That(SeesPerson(avatar, sourceActor, 1.10f), Is.False, "seated, the eye (1.1 m) is below the counter's top");
            Assert.That(SeesPerson(avatar, sourceActor, 1.75f), Is.True, "standing up again");
        }

        [Test]
        public void ReleasingTheSourceGivesItsHeadAnchorBackItsRegisteredHeight()
        {
            SbEntity avatar = BuildScene(out long sourceActor);
            Assert.That(SeesPerson(avatar, sourceActor, 1.10f), Is.False);

            typeof(SbEntity).GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(avatar, null);
            Assert.That(SourceHeadAnchorTests.TrackedHeads(_manager), Is.Empty);

            // The actor stays in the core; ticked as before, its head is the registered 1.58 m again, over the counter.
            for (int i = 0; i < 2; ++i) SpatialBlendAPI.UpdateActorState(_space, sourceActor, new Vector3(0f, 1.1f, 0f), 0f);
            Assert.That(PersonIsFacingReferent(sourceActor), Is.True);
        }

        private bool SeesPerson(SbEntity avatar, long sourceActor, float eyeHeight)
        {
            for (int i = 0; i < 2; ++i)
            {
                avatar.transform.SetPositionAndRotation(new Vector3(0f, eyeHeight, 0f), Quaternion.Euler(0f, i * 0.5f, 0f));
                _manager.UpdateV2EntityState(avatar);
            }
            return PersonIsFacingReferent(sourceActor);
        }

        private bool PersonIsFacingReferent(long sourceActor)
        {
            var rows = new List<RelationStateV2Row>();
            Assert.That(SpatialBlendAPI.GetRelationStatesV2(_space, sourceActor, rows), Is.GreaterThanOrEqualTo(0));
            foreach (RelationStateV2Row row in rows)
                if (row.Kernel == SbKernelType.Facing && row.ReferentSharedId == PersonShared) return true;
            return false;
        }

        private SbEntity BuildScene(out long sourceActor)
        {
            long session = SpatialBlendAPI.CreateSession();
            _space = SpatialBlendAPI.AddSpace(session);
            Assert.That(_space, Is.GreaterThan(0));
            long room = SpatialBlendAPI.AddElement(_space);
            SpatialBlendAPI.SetElementSharedId(_space, room, RoomShared);
            Assert.That(SpatialBlendAPI.SetElementPolygon(_space, room, SbGeometryRole.Navigable, new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 8f), new Vector2(-5f, 8f)
            }), Is.True);

            // A 1.2 m counter just in front of the source (Unity z 0.3..0.6), across the sightline.
            long counter = SpatialBlendAPI.AddElement(_space, room);
            SpatialBlendAPI.SetElementSharedId(_space, counter, CounterShared);
            SpatialBlendAPI.SetElementBox(_space, counter, SbGeometryRole.Obstacle, new Vector3(-1f, 0f, 0.3f), new Vector3(1f, 1.2f, 0.6f));

            // The other person, 4 m ahead, registered as the app registers every avatar actor (root, head at 1.58 m).
            long personElement = SpatialBlendAPI.AddElement(_space, room);
            SpatialBlendAPI.SetElementSharedId(_space, personElement, PersonShared);
            long person = SpatialBlendAPI.CreateActor(_space);
            SpatialBlendAPI.AddAnchor(_space, person, "root", Vector3.zero);
            SpatialBlendAPI.AddAnchor(_space, person, "head", new Vector3(0f, SpatialBlendManager.HeadAnchorHeightMeters, 0f));
            SpatialBlendAPI.SetActorElement(_space, person, personElement);
            SpatialBlendAPI.UpdateActorState(_space, person, new Vector3(0f, 0f, 4f), 180f);

            // The source avatar, with the anchors ShareGameObject gives it and Facing on its head.
            long sourceElement = SpatialBlendAPI.AddElement(_space, room);
            SpatialBlendAPI.SetElementSharedId(_space, sourceElement, SourceShared);
            sourceActor = SpatialBlendAPI.CreateActor(_space);
            SpatialBlendAPI.AddAnchor(_space, sourceActor, "root", Vector3.zero);
            SpatialBlendAPI.AddAnchor(_space, sourceActor, "head", new Vector3(0f, SpatialBlendManager.HeadAnchorHeightMeters, 0f));
            SpatialBlendAPI.SetActorElement(_space, sourceActor, sourceElement);
            Assert.That(SpatialBlendAPI.Bind(_space, sourceActor, "head", SbKernelType.Facing), Is.True);

            var managerObject = new GameObject("source-head-native-manager");
            managerObject.SetActive(false);
            _objects.Add(managerObject);
            _manager = managerObject.AddComponent<SourceHeadAnchorTests.HeadManagerNative>();
            SourceHeadAnchorTests.SetField(_manager, "_connectOnAwake", false);

            var go = new GameObject("source-head-native-avatar") { tag = "Avatar" };
            go.SetActive(false);
            _objects.Add(go);
            var entity = go.AddComponent<SbLocalEntity>();
            entity.AllowManipulation = false;
            typeof(SbEntity).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(entity, new object[] { _manager, sourceElement, true });
            ((IDictionary)SourceHeadAnchorTests.GetField(_manager, "_v2ActorByEntity")).Add(sourceElement, sourceActor);
            ((IDictionary)SourceHeadAnchorTests.GetField(_manager, "_v2EntityByActor")).Add(sourceActor, sourceElement);
            var anchors = (HashSet<(long actor, string name)>)SourceHeadAnchorTests.GetField(_manager, "_v2ActorAnchors");
            anchors.Add((sourceActor, "root"));
            anchors.Add((sourceActor, "head"));
            SourceHeadAnchorTests.SetField(_manager, "_spaceID", _space);
            return entity;
        }
    }
}
#endif
