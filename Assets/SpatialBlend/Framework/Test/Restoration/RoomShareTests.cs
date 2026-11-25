// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Room objects: the host shares its marked furniture, and the receiver links each to its own largest free anchor of the
    /// same label or gets a box to install. The shapes are TEST DATA; the
    /// assertions are derived from them. No native library is used.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RoomShareTests
    {
        private static readonly Vector2[] SeatOutline =
        {
            new Vector2(-0.3f, -0.2f), new Vector2(0.3f, -0.2f), new Vector2(0.3f, 0.2f), new Vector2(-0.3f, 0.2f),
        };
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void Destroy()
        {
            SceneAnchorHighlight.Clear();
            for (int i = _objects.Count - 1; i >= 0; --i) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        private sealed class Publisher : ISceneAnchorPublisher
        {
            public readonly List<(GameObject anchor, bool share)> Published = new List<(GameObject, bool)>();
            public void Publish(GameObject anchor, bool shareWithPeers) => Published.Add((anchor, shareWithPeers));
        }

        // An MRUK-style anchor: local +Z up, origin on the top face, the volume extends backward along local Z.
        private SceneAnchorRecord Add(ListSceneAnchorCatalog catalog, string label, string id, Vector3 position, Vector3 size, bool plane = false)
        {
            var go = new GameObject(label + "-" + id);
            _objects.Add(go);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(-90f, 0f, 0f));
            var record = new SceneAnchorRecord
            {
                Label = label, Anchor = go, AnchorId = id, SourceKey = go.GetInstanceID(), Size = size.x * size.y * size.z,
                LocalCenter = new Vector3(0f, 0f, -size.z * 0.5f), LocalSize = size,
                PlaneBoundary = plane ? SeatOutline : null,
            };
            catalog.Add(record);
            return record;
        }

        private FreeformCollaborationApp NewApp(ListSceneAnchorCatalog catalog, out Publisher publisher)
        {
            var go = new GameObject("app");
            _objects.Add(go);
            var app = go.AddComponent<FreeformCollaborationApp>();
            app.UseSceneAnchorCatalog(catalog);
            publisher = new Publisher();
            app.UseSceneAnchorPublisher(publisher);
            app.UseSeatPreferences(new MemorySeatPreferences());
            app.UserViewProvider = () => (new Vector3(0f, 1.6f, 0f), new Vector3(0f, 0.2f, 1f));
            app.FloorHeightProvider = () => 0.05f;
            return app;
        }

        private ListSceneAnchorCatalog Room(params (string label, string id, Vector3 size, bool plane)[] anchors)
        {
            var catalog = new ListSceneAnchorCatalog();
            for (int i = 0; i < anchors.Length; ++i)
                Add(catalog, anchors[i].label, anchors[i].id, new Vector3(i * 3f, 0.45f, 0f), anchors[i].size, anchors[i].plane);
            return catalog;
        }

        // The replica of the other user's shared object, as the manager rebuilds it from the wire: the sender's own wire name.
        private GameObject ReplicaOf(GameObject sentAnchor)
        {
            var go = new GameObject(sentAnchor.name);
            _objects.Add(go);
            var entity = go.AddComponent<SbMRUKAnchorEntity>();
            string label = sentAnchor.GetComponent<SbMRUKAnchorEntity>().SceneLabel;
            entity.SetSceneLabel(label);
            entity.AdoptWireName(go.name, label);
            return go;
        }

        private void MarkAttached(GameObject anchor, long id = 5L)
        {
            var managerObject = new GameObject("fake-manager");
            managerObject.SetActive(false);
            _objects.Add(managerObject);
            var manager = managerObject.AddComponent<SpatialBlendManager>();
            var entity = anchor.GetComponent<SbEntity>();
            typeof(SbEntity).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entity, manager);
            typeof(SbEntity).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entity, id);
        }

        // ------------------------------------------------------------------ the sender: nothing is shared by itself

        // A drop or a recenter never pulls a physical object (an own scan, a scene anchor, a box).
        [Test]
        public void ADropNeverPullsAPhysicalObject()
        {
            FreeformCollaborationApp app = NewApp(new ListSceneAnchorCatalog(), out _);
            var room = RoomRegion.Build(new[] { new Vector2(-2f, -2f), new Vector2(2f, -2f), new Vector2(2f, 2f), new Vector2(-2f, 2f) }, null, null);
            typeof(FreeformCollaborationApp).GetField("_localRoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(app, room);
            foreach (System.Type kind in new[] { typeof(PhysicalCounterpart), typeof(SbMRUKAnchorEntity) })
            {
                var go = new GameObject("physical-" + kind.Name);
                _objects.Add(go);
                go.AddComponent(kind);
                go.transform.position = new Vector3(9f, 0f, 9f);   // far outside the room
                Assert.That(app.TryPullPlacement(go, Vector2.zero, 0f, out Vector2 shift), Is.True, kind.Name);
                Assert.That(shift, Is.EqualTo(Vector2.zero), kind.Name + " stays where the user put it");
                Assert.That(app.TryPlaceOnWall(go, null, 0f), Is.False, kind.Name + " is never moved onto a wall");
            }
        }

        // An older sharer's wire name with a desk rank still parses; the rank is ignored.
        [Test]
        public void AnOlderWireNameWithADeskRankStillParsesAndTheRankIsIgnored()
        {
            Assert.That(SharedAnchorName.TryParse("TABLE#k1#obj@0,0,-0.3,0.8,2.1,0.6|1", out string head, out Vector3 center, out Vector3 size, out float? front), Is.True);
            Assert.That(head, Is.EqualTo("TABLE#k1#obj"));
            Assert.That(center.z, Is.EqualTo(-0.3f).Within(1e-5f));
            Assert.That(size, Is.EqualTo(new Vector3(0.8f, 2.1f, 0.6f)));
            Assert.That(front, Is.Null);
            Assert.That(SharedAnchorName.TryParse("TABLE#k1#obj@0,0,0,1,1,1|x", out _, out _, out _, out _), Is.False, "a malformed rank is refused");
        }

        [Test]
        public void NothingUnsharedReachesTheCore()
        {
            var catalog = Room(("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true), ("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false),
                                ("STORAGE", "s1", new Vector3(1f, 0.4f, 1.8f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher publisher);
            FurnitureSharePolicy.ApplyDefaults(app.ShareableAnchors());

            Assert.That(publisher.Published, Is.Empty, "marking a desk shares nothing until the session publishes it");
            foreach (SceneAnchorRecord record in catalog.Anchors)
            {
                Assert.That(record.Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "an unshared anchor is not an interaction entity");
                Assert.That(app.ShareStateOf(record), Is.EqualTo(AnchorShareState.None));
            }
            // The paths that used to publish a table and the linked seats by themselves are gone.
            Assert.That(typeof(FreeformCollaborationApp).GetMethod("TryRegisterBiggestTableAnchor"), Is.Null);
            Assert.That(typeof(FreeformCollaborationApp).GetMethod("TryRegisterLinkedSeats"), Is.Null);
        }

        [Test]
        public void TheSelectionRegistersExactlyTheChosenAnchors()
        {
            var catalog = Room(("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true), ("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false),
                                ("STORAGE", "s1", new Vector3(1f, 0.4f, 1.8f), false), ("BED", "b1", new Vector3(2f, 1.4f, 0.5f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher publisher);
            Assert.That(app.TryShareAnchor(catalog.Anchors[1]), Is.True);
            Assert.That(app.TryShareAnchor(catalog.Anchors[3]), Is.True);

            Assert.That(publisher.Published.Select(p => p.anchor), Is.EquivalentTo(new[] { catalog.Anchors[1].Anchor, catalog.Anchors[3].Anchor }));
            Assert.That(publisher.Published.All(p => p.share), Is.True);
            Assert.That(catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "the unmarked couch stays a local obstacle");
            Assert.That(catalog.Anchors[2].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            var table = catalog.Anchors[1].Anchor.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(table.ShareKey, Is.EqualTo("t1"), "the platform's anchor id is the key");
            SharedAnchorName.TryParse(table.gameObject.name, out _, out Vector3 center, out Vector3 size, out _);
            Assert.That(size, Is.EqualTo(new Vector3(1.4f, 0.8f, 0.75f)), "the measured size travels with it");
            Assert.That(app.ShareStateOf(catalog.Anchors[1]), Is.EqualTo(AnchorShareState.Shared));
        }

        [Test]
        public void ASeatIsSharedWithItsMeasuredPlaneAndTheFrontConfirmedOnce()
        {
            var catalog = Room(("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher publisher);
            Vector3 aimed = catalog.Anchors[0].Anchor.transform.TransformPoint(new Vector3(0.05f, 0.4f, 0f));
            Assert.That(app.TryConfirmSeatFront(catalog.Anchors[0], aimed), Is.True);
            Assert.That(app.TryShareAnchor(catalog.Anchors[0]), Is.True);

            var entity = catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(entity.IsSeat, Is.True, "a COUCH with a measured plane is a Sittable seat");
            Assert.That(entity.SeatFrontConfirmed, Is.True, "the front confirmed once is part of the registration");
            Assert.That(entity.SeatKey, Is.EqualTo("c1"));
            Assert.That(entity.GetComponent<SbSpatialAuthoring>().affordances.Single().tag, Is.EqualTo("Sittable"));
            Assert.That(publisher.Published.Single().share, Is.True);
            Assert.That(app.SeatLinks.TryGetLocal("c1", out GameObject holder) && holder == entity.gameObject, Is.True);
        }

        [Test]
        public void UnsharingAnAnchorCleansUp()
        {
            var catalog = Room(("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true), ("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);
            Assert.That(app.TryShareAnchor(catalog.Anchors[0]), Is.True);
            Assert.That(app.TryShareAnchor(catalog.Anchors[1]), Is.True);
            GameObject couch = catalog.Anchors[0].Anchor, table = catalog.Anchors[1].Anchor;
            MarkAttached(couch);
            MarkAttached(table, 6L);
            Assert.That(couch.GetComponentsInChildren<LineRenderer>().Length, Is.EqualTo(9), "the shared box: its edges and eight corner brackets");

            Assert.That(app.TryUnshareAnchor(catalog.Anchors[0]), Is.True);

            Assert.That(couch.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "the entity is gone, so the core unshares the element");
            Assert.That(couch.GetComponent<SbSpatialAuthoring>(), Is.Null);
            Assert.That(couch.GetComponent<BoxCollider>(), Is.Null, "the collider it added goes");
            Assert.That(couch.GetComponentsInChildren<LineRenderer>().Length, Is.EqualTo(0), "and its wires");
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.None));
            Assert.That(app.SeatLinks.TryGetLocal("c1", out _), Is.False);
            Assert.That(table.GetComponent<SbMRUKAnchorEntity>(), Is.Not.Null, "the other shared anchor is untouched");
        }

        // ------------------------------------------------------------------ the receiver: link to existing

        private (FreeformCollaborationApp sender, GameObject sentCouch) SenderSharedCouch(Vector3 size)
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord record = Add(senderCatalog, "COUCH", "sender-couch", new Vector3(10f, 0.45f, 10f), size, true);
            FreeformCollaborationApp sender = NewApp(senderCatalog, out _);
            Assert.That(sender.TryShareAnchor(record), Is.True);
            return (sender, record.Anchor);
        }

        [Test]
        public void ASimilarLocalAnchorOfTheSameLabelIsLinkedWithNoPrompt()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            var catalog = Room(("TABLE", "t-local", new Vector3(2.0f, 0.9f, 0.85f), false), ("COUCH", "c-local", new Vector3(1.9f, 0.95f, 0.8f), true));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);

            OnShareResponse first = app.OnShare(ReplicaOf(sent));
            Assert.That(first.result, Is.EqualTo(OnShareResponse.Result.Defer), "the local couch registers under the sharer's key, then the link completes");
            Assert.That(published.Published.Single().anchor, Is.SameAs(catalog.Anchors[1].Anchor), "the couch, never the table of the same size");
            var local = catalog.Anchors[1].Anchor.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(local.SeatKey, Is.EqualTo("sender-couch"));
            Assert.That(app.ShareStateOf(catalog.Anchors[1]), Is.EqualTo(AnchorShareState.Linked));

            MarkAttached(catalog.Anchors[1].Anchor);
            OnShareResponse linked = app.OnShare(ReplicaOf(sent));
            Assert.That(linked.result, Is.EqualTo(OnShareResponse.Result.LinkToExisting));
            Assert.That(linked.gameObject, Is.SameAs(catalog.Anchors[1].Anchor));
            Assert.That(app.SharedBoxes, Is.Empty, "no box: the room has the thing");
            Assert.That(app.SeatLinks.TryGetPair(SeatIdentity.SharedId("sender-couch"), out _), Is.True);
        }

        // If B receives A's TABLE, COUCH and STORAGE before B's own room is registered, makes a box of each (AddAsNew), then
        // shares its own anchors under the same ids, the boxes' geometry collides with B's. A share that arrives before this
        // room's anchors can be matched waits for them instead.
        [Test]
        public void AnAnchorShareBeforeThisRoomsAnchorsWaitsInsteadOfBecomingABox()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            var catalog = Room(("COUCH", "c-local", new Vector3(1.9f, 0.95f, 0.8f), true));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            app.UseSceneAnchorCatalog(null);   // no room yet: the MRUK catalog, which this test has no room for

            OnShareResponse early = app.OnShare(ReplicaOf(sent));

            Assert.That(early.result, Is.EqualTo(OnShareResponse.Result.Defer));
            Assert.That(app.SharedBoxes, Is.Empty, "no box for a thing this room may well have");
            Assert.That(published.Published, Is.Empty);

            app.UseSceneAnchorCatalog(catalog);   // the room's anchors are known
            OnShareResponse decided = app.OnShare(ReplicaOf(sent));
            Assert.That(decided.result, Is.EqualTo(OnShareResponse.Result.Defer), "the local couch registers under the sharer's key");
            Assert.That(published.Published.Single().anchor, Is.SameAs(catalog.Anchors[0].Anchor));
        }

        // SbApp.StartInSpace registers the walkable region before OnAppStart, so the app waits for its scene geometry from Start.
        [Test]
        public void TheAppWaitsForItsSceneGeometry()
        {
            // SbApp.StartInSpace raises the manager's flag from this before it registers the walkable region.
            PropertyInfo waits = typeof(FreeformCollaborationApp).GetProperty("WaitsForLocalSceneGeometry",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FreeformCollaborationApp app = NewApp(Room(), out _);
            Assert.That(waits.DeclaringType, Is.EqualTo(typeof(FreeformCollaborationApp)));
            Assert.That((bool)waits.GetValue(app), Is.EqualTo(!app.UsesFoundationLocal));
        }

        [Test]
        public void ADifferentLabelDoesNotLinkAndIsAddedAsNew()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            var catalog = Room(("BED", "b-local", new Vector3(2.0f, 0.9f, 0.85f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            Assert.That(app.OnShare(ReplicaOf(sent)).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            Assert.That(published.Published, Is.Empty);
            Assert.That(catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
        }

        [Test]
        public void TwoIncomingAnchorsNeverLinkTheSameLocalOne()
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord a = Add(senderCatalog, "TABLE", "ta", new Vector3(10f, 0.8f, 0f), new Vector3(1.6f, 0.8f, 0.75f));
            SceneAnchorRecord b = Add(senderCatalog, "TABLE", "tb", new Vector3(13f, 0.8f, 0f), new Vector3(1.5f, 0.8f, 0.75f));
            FreeformCollaborationApp sender = NewApp(senderCatalog, out _);
            sender.TryShareAnchor(a);
            sender.TryShareAnchor(b);
            var catalog = Room(("TABLE", "only", new Vector3(1.55f, 0.8f, 0.75f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);

            OnShareResponse first = app.OnShare(ReplicaOf(a.Anchor));
            OnShareResponse second = app.OnShare(ReplicaOf(b.Anchor));

            Assert.That(first.result, Is.EqualTo(OnShareResponse.Result.Defer), "the first arrival takes the only table");
            Assert.That(second.result, Is.EqualTo(OnShareResponse.Result.AddAsNew),
                "the second finds the room's only table taken: nothing is refused, it arrives as a box to install");
            Assert.That(KeyOf(catalog.Anchors[0]), Is.EqualTo("ta"), "the only table stays linked to the first");
        }

        // A's TABLE (0.80 x 1.81) and B's (1.57 x 0.58) differ by more than a 0.2 size bound would allow; they still link.
        [Test]
        public void ASameLabelAnchorLinksWhateverItsSize()
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord table = Add(senderCatalog, "TABLE", "simtable", new Vector3(10f, 0.8f, 0f), new Vector3(0.80f, 1.81f, 1.06f));
            NewApp(senderCatalog, out _).TryShareAnchor(table);
            var catalog = Room(("TABLE", "b-table", new Vector3(1.57f, 0.58f, 0.75f), false), ("STORAGE", "b-store", new Vector3(0.8f, 1.81f, 1.06f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);

            Assert.That(app.OnShare(ReplicaOf(table.Anchor)).result, Is.EqualTo(OnShareResponse.Result.Defer), "the table registers, then links");
            Assert.That(published.Published.Single().anchor, Is.SameAs(catalog.Anchors[0].Anchor), "the TABLE, never the STORAGE of the same size");
            Assert.That(catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>().ShareKey, Is.EqualTo("simtable"));
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(app.SharedBoxes, Is.Empty);
        }

        // A box stands for the other user's TABLE (this room had none). This room then has a TABLE of its own, under the very key
        // the box carries (B shares its TABLE as 'simtable', the key of A's TABLE box). One shared id, one element.
        [Test]
        public void AfterABoxThisRoomRegistersNoSecondElementUnderItsKeyOrLabel()
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord table = Add(senderCatalog, "TABLE", "simtable", new Vector3(10f, 0.8f, 0f), new Vector3(0.8f, 1.8f, 1f));
            NewApp(senderCatalog, out _).TryShareAnchor(table);
            var catalog = Room(("BED", "b-bed", new Vector3(2f, 1.4f, 0.5f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            GameObject replica = ReplicaOf(table.Anchor);
            Assert.That(app.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.AddAsNew), "no TABLE here: a box");
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            Assert.That(app.SharedBoxes.Count, Is.EqualTo(1));

            SceneAnchorRecord mine = Add(catalog, "TABLE", "simtable", new Vector3(6f, 0.45f, 0f), new Vector3(1.57f, 0.58f, 0.75f));
            Assert.That(app.TryShareAnchor(mine), Is.False, "the box already stands for the TABLE: sharing a second one is refused");
            Assert.That(app.TryRegisterSharedAnchor(mine, "simtable", true), Is.False, "the box holds the key: no second element under it");
            Assert.That(published.Published, Is.Empty);
            Assert.That(mine.Anchor.GetComponent<SbEntity>() == null || !mine.Anchor.GetComponent<SbEntity>().IsAttached(), Is.True);
            Assert.That(app.SharedBoxes.Count, Is.EqualTo(1));
        }

        // Another label under a box's key (a platform id that collides): the user's share goes out under a fresh key.
        [Test]
        public void AnAnchorWhosePlatformIdABoxHoldsIsSharedUnderAFreshKey()
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord table = Add(senderCatalog, "TABLE", "simtable", new Vector3(10f, 0.8f, 0f), new Vector3(0.8f, 1.8f, 1f));
            NewApp(senderCatalog, out _).TryShareAnchor(table);
            var catalog = Room(("STORAGE", "simtable", new Vector3(0.8f, 1.8f, 1f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            GameObject replica = ReplicaOf(table.Anchor);
            Assert.That(app.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.AddAsNew), "no TABLE here: a box");
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);

            Assert.That(app.TryShareAnchor(catalog.Anchors[0]), Is.True);
            string key = catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>().ShareKey;
            Assert.That(published.Published.Single().anchor, Is.SameAs(catalog.Anchors[0].Anchor));
            Assert.That(key, Is.Not.EqualTo("simtable").And.Not.Empty, "re-keyed: the box keeps 'simtable' to itself");
            Assert.That(app.ShareKeyOf(catalog.Anchors[0]), Is.EqualTo(key));
        }

        // ------------------------------------------------------------------ shared furniture: largest links to largest

        private static string KeyOf(SceneAnchorRecord record) => record.Anchor.GetComponent<SbMRUKAnchorEntity>()?.ShareKey;

        // The host shares its marked tables largest first (FreeformCollaborationApp.ShareChosenRoomAnchors).
        private (SceneAnchorRecord big, SceneAnchorRecord mid) HostSharesTwoDesks()
        {
            var hostCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord mid = Add(hostCatalog, "TABLE", "h-mid", new Vector3(2.4f, 0.4f, 0f), new Vector3(1.2f, 0.8f, 0.7f));
            SceneAnchorRecord big = Add(hostCatalog, "TABLE", "h-big", new Vector3(0f, 0.4f, 0f), new Vector3(2f, 1f, 0.75f));
            FreeformCollaborationApp host = NewApp(hostCatalog, out _);
            Assert.That(host.TryShareAnchor(big), Is.True);
            Assert.That(host.TryShareAnchor(mid), Is.True);
            return (big, mid);
        }

        private ListSceneAnchorCatalog JoinerRoom(out SceneAnchorRecord big, out SceneAnchorRecord small)
        {
            var catalog = new ListSceneAnchorCatalog();
            small = Add(catalog, "TABLE", "j-small", new Vector3(2.4f, 0.4f, 0.4f), new Vector3(1.05f, 0.62f, 0.75f));
            big = Add(catalog, "TABLE", "j-big", new Vector3(0.2f, 0.4f, 0.4f), new Vector3(1.65f, 0.8f, 0.75f));
            return catalog;
        }

        // Each incoming table links to this room's largest still-free table: shared largest first, the host's largest links to the
        // joiner's largest and the second to the second.
        [Test]
        public void TheHostsTablesSharedLargestFirstLinkToTheJoinersLargestFirst()
        {
            var (hostBig, hostMid) = HostSharesTwoDesks();
            ListSceneAnchorCatalog catalog = JoinerRoom(out SceneAnchorRecord big, out SceneAnchorRecord small);
            FreeformCollaborationApp joiner = NewApp(catalog, out Publisher published);

            Assert.That(joiner.OnShare(ReplicaOf(hostBig.Anchor)).result, Is.EqualTo(OnShareResponse.Result.Defer));
            Assert.That(joiner.OnShare(ReplicaOf(hostMid.Anchor)).result, Is.EqualTo(OnShareResponse.Result.Defer));

            Assert.That(KeyOf(big), Is.EqualTo("h-big"), "the largest links to the largest, re-keyed under the host's key");
            Assert.That(KeyOf(small), Is.EqualTo("h-mid"));
            Assert.That(joiner.ShareStateOf(big), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(joiner.ShareStateOf(small), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(published.Published.Select(p => p.anchor), Is.EqualTo(new[] { big.Anchor, small.Anchor }));
            Assert.That(joiner.SharedBoxes, Is.Empty);
        }

        // Unequal counts, host more: the host's table past the joiner's count arrives as a box to install.
        [Test]
        public void AHostTablePastTheJoinersCountArrivesAsABox()
        {
            var (hostBig, hostMid) = HostSharesTwoDesks();
            var catalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord only = Add(catalog, "TABLE", "j-only", new Vector3(0.2f, 0.4f, 0.2f), new Vector3(1.5f, 0.8f, 0.75f));
            FreeformCollaborationApp joiner = NewApp(catalog, out Publisher published);

            joiner.OnShare(ReplicaOf(hostBig.Anchor));
            GameObject replica = ReplicaOf(hostMid.Anchor);
            Assert.That(joiner.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            joiner.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);

            Assert.That(KeyOf(only), Is.EqualTo("h-big"), "the one local table links to the host's largest");
            SharedRoomObjectBox box = joiner.SharedBoxes.Single();
            Assert.That(box.Label, Is.EqualTo("TABLE"));
            Assert.That(box.Size, Is.EqualTo(new Vector3(1.2f, 0.8f, 0.7f)), "the host table's measured size");
            Assert.That(published.Published.Select(p => p.anchor), Is.EqualTo(new[] { only.Anchor }), "the box is not an anchor share");
        }

        // Unequal counts, joiner more: the joiner's table nothing links to is not shared and stays a local obstacle.
        [Test]
        public void AJoinerTablePastTheHostsCountIsNotShared()
        {
            var hostCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord hostOnly = Add(hostCatalog, "TABLE", "h-only", new Vector3(0f, 0.4f, 0f), new Vector3(2f, 1f, 0.75f));
            Assert.That(NewApp(hostCatalog, out _).TryShareAnchor(hostOnly), Is.True);
            ListSceneAnchorCatalog catalog = JoinerRoom(out SceneAnchorRecord big, out SceneAnchorRecord small);
            FreeformCollaborationApp joiner = NewApp(catalog, out Publisher published);

            joiner.OnShare(ReplicaOf(hostOnly.Anchor));

            Assert.That(KeyOf(big), Is.EqualTo("h-only"));
            Assert.That(joiner.ShareStateOf(small), Is.EqualTo(AnchorShareState.None), "never shared");
            Assert.That(small.Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            Assert.That(published.Published.Single().anchor, Is.SameAs(big.Anchor));
        }

        // Only a TABLE is a desk, and only the single biggest table starts marked (as v1); a couch, a sideboard
        // or a bed is not marked by default.
        [Test]
        public void OnlyTheBiggestTableStartsMarked()
        {
            var catalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord big = Add(catalog, "TABLE", "big", new Vector3(0f, 0.4f, 0f), new Vector3(2f, 1f, 0.75f));
            SceneAnchorRecord mid = Add(catalog, "TABLE", "mid", new Vector3(2.2f, 0.4f, 0.4f), new Vector3(1.2f, 0.8f, 0.75f));
            SceneAnchorRecord small = Add(catalog, "TABLE", "small", new Vector3(30f, 0.4f, 0f), new Vector3(0.5f, 0.4f, 0.7f));
            SceneAnchorRecord couch = Add(catalog, "COUCH", "c", new Vector3(3f, 0.4f, 0f), new Vector3(2.2f, 0.9f, 0.7f), true);
            SceneAnchorRecord sideboard = Add(catalog, "STORAGE", "s", new Vector3(0f, 0.4f, 3f), new Vector3(1.8f, 0.5f, 0.75f));
            SceneAnchorRecord bed = Add(catalog, "BED", "b", new Vector3(3f, 0.3f, 3f), new Vector3(2f, 1.6f, 0.65f));
            Assert.That(FurnitureSharePolicy.IsDesk(big), Is.True);
            Assert.That(FurnitureSharePolicy.IsDesk(couch), Is.False);
            Assert.That(FurnitureSharePolicy.IsDesk(sideboard), Is.False);
            Assert.That(FurnitureSharePolicy.IsDesk(bed), Is.False);

            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            FurnitureSharePolicy.ApplyDefaults(app.ShareableAnchors());
            Assert.That(FurnitureSharePolicy.WillShare(big), Is.True);
            Assert.That(FurnitureSharePolicy.WillShare(mid), Is.False, "only the biggest");
            Assert.That(FurnitureSharePolicy.WillShare(small), Is.False, "only the biggest");
            Assert.That(FurnitureSharePolicy.WillShare(couch), Is.False);
            Assert.That(FurnitureSharePolicy.WillShare(sideboard), Is.False);
            Assert.That(FurnitureSharePolicy.WillShare(bed), Is.False);
            Assert.That(published.Published, Is.Empty, "marking shares nothing by itself");

            // The user's choice on an object survives a later default pass.
            FurnitureSharePolicy.SetShare(small, true);
            FurnitureSharePolicy.SetShare(big, false);
            FurnitureSharePolicy.ApplyDefaults(app.ShareableAnchors());
            Assert.That(FurnitureSharePolicy.WillShare(small), Is.True);
            Assert.That(FurnitureSharePolicy.WillShare(big), Is.False);
        }

        // A tie in top area is broken by the anchor id, the same on every call.
        [Test]
        public void ATieInDeskAreaIsBrokenDeterministically()
        {
            var catalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord b = Add(catalog, "TABLE", "b-desk", new Vector3(0f, 0.4f, 0f), new Vector3(1.2f, 0.7f, 0.75f));
            SceneAnchorRecord a = Add(catalog, "TABLE", "a-desk", new Vector3(3f, 0.4f, 0f), new Vector3(0.7f, 1.2f, 0.75f));
            List<SceneAnchorRecord> first = catalog.Anchors.ToList();
            List<SceneAnchorRecord> reversed = catalog.Anchors.Reverse().ToList();
            first.Sort(FurnitureSharePolicy.CompareDesks);
            reversed.Sort(FurnitureSharePolicy.CompareDesks);
            Assert.That(first.Select(r => r.AnchorId), Is.EqualTo(new[] { "a-desk", "b-desk" }));
            Assert.That(reversed.Select(r => r.AnchorId), Is.EqualTo(new[] { "a-desk", "b-desk" }));
        }

        // A receiver links the sharer's items to its own furniture of that label whether it marked it or not, and registers it
        // under the sharer's key exactly like a marked one.
        [Test]
        public void AReceiverLinksTheSharersItemsToItsUnmarkedFurnitureToo()
        {
            var hostCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord hostCouch = Add(hostCatalog, "COUCH", "h-couch", new Vector3(0f, 0.45f, 0f), new Vector3(2f, 0.9f, 0.85f), true);
            SceneAnchorRecord hostDesk = Add(hostCatalog, "TABLE", "h-desk", new Vector3(3f, 0.4f, 0f), new Vector3(1.4f, 0.8f, 0.75f));
            FreeformCollaborationApp host = NewApp(hostCatalog, out _);
            Assert.That(host.TryShareAnchor(hostCouch), Is.True);
            Assert.That(host.TryShareAnchor(hostDesk), Is.True);

            var catalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord couch = Add(catalog, "COUCH", "j-couch", new Vector3(0.2f, 0.45f, 0.2f), new Vector3(1.8f, 0.9f, 0.8f), true);
            SceneAnchorRecord desk = Add(catalog, "TABLE", "j-desk", new Vector3(3f, 0.4f, 0.4f), new Vector3(1.2f, 0.7f, 0.75f));
            FurnitureSharePolicy.SetShare(desk, false);   // the joiner unmarked its desk; it is still linked
            FreeformCollaborationApp joiner = NewApp(catalog, out _);
            Assert.That(FurnitureSharePolicy.WillShare(couch), Is.False, "a couch is not marked by default");

            joiner.OnShare(ReplicaOf(hostCouch.Anchor));
            joiner.OnShare(ReplicaOf(hostDesk.Anchor));

            SbMRUKAnchorEntity seat = couch.Anchor.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(seat.IsSeat, Is.True);
            Assert.That(seat.SeatKey, Is.EqualTo("h-couch"), "the unmarked couch is linked, as a seat, under the host's key");
            Assert.That(joiner.ShareStateOf(couch), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(KeyOf(desk), Is.EqualTo("h-desk"), "the unmarked desk too");
            Assert.That(joiner.SharedBoxes, Is.Empty, "nothing arrives as a box to install");
        }

        // The sharer's couch is a seat and this room's couch has no seat plane: a seat links only to a seat, so it arrives as a box.
        [Test]
        public void ASharedSeatDoesNotLinkToACouchWithoutASeatPlaneAndArrivesAsABox()
        {
            var hostCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord hostCouch = Add(hostCatalog, "COUCH", "h-couch", new Vector3(0f, 0.45f, 0f), new Vector3(2f, 0.9f, 0.85f), true);
            Assert.That(NewApp(hostCatalog, out _).TryShareAnchor(hostCouch), Is.True);
            var catalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord couch = Add(catalog, "COUCH", "j-couch", new Vector3(0.2f, 0.45f, 0.2f), new Vector3(1.8f, 0.9f, 0.8f));
            FreeformCollaborationApp joiner = NewApp(catalog, out Publisher published);

            GameObject replica = ReplicaOf(hostCouch.Anchor);
            Assert.That(joiner.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            joiner.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);

            Assert.That(couch.Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "the plain couch stays a local obstacle");
            Assert.That(joiner.ShareStateOf(couch), Is.EqualTo(AnchorShareState.None));
            Assert.That(published.Published, Is.Empty);
            Assert.That(joiner.SharedBoxes.Single().Label, Is.EqualTo("COUCH"));
        }

        [Test]
        public void TheSharersEchoOfALinkedAnchorLinksToTheSharersOwnAnchor()
        {
            // Sharing is symmetric: the receiver's registered anchor reaches the sharer, which must link it to the one it shared.
            var (sender, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            MarkAttached(sent);
            var catalog = Room(("COUCH", "c-local", new Vector3(2.0f, 0.9f, 0.85f), true));
            FreeformCollaborationApp receiver = NewApp(catalog, out _);
            receiver.OnShare(ReplicaOf(sent));
            GameObject echo = ReplicaOf(catalog.Anchors[0].Anchor);

            OnShareResponse response = sender.OnShare(echo);

            Assert.That(response.result, Is.EqualTo(OnShareResponse.Result.LinkToExisting));
            Assert.That(response.gameObject, Is.SameAs(sent));
            Assert.That(sender.SharedBoxes, Is.Empty);
        }

        // ------------------------------------------------------------------ the receiver: add as new, a box to install

        [Test]
        public void NoAnchorOfTheLabelArrivesAsAPlaceableBoxInFrontOfTheUserWithReceiverPresentationHidden()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            var catalog = Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            GameObject replica = ReplicaOf(sent);

            Assert.That(app.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);

            SharedRoomObjectBox box = app.SharedBoxes.Single();
            Assert.That(box.gameObject, Is.SameAs(replica));
            Assert.That(box.Label, Is.EqualTo("COUCH"));
            Assert.That(box.Size, Is.EqualTo(new Vector3(2.0f, 0.9f, 0.85f)), "the sharer's measured size");
            Assert.That(box.IsPlaced, Is.False);
            var entity = replica.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(entity.AllowManipulation, Is.True, "the ordinary placement and manipulation flow");
            Assert.That(entity.PlacementMotifTypeValue, Is.EqualTo(PlacementMotifType.Object));
            BoxCollider collider = replica.GetComponent<BoxCollider>();
            Assert.That(collider.size, Is.EqualTo(new Vector3(2.0f, 0.9f, 0.85f)));
            Transform outline = replica.transform.Find("SharedBoxOutline");
            Assert.That(outline, Is.Not.Null, "the placement flow remains intact");
            Assert.That(replica.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Edges"), Is.EqualTo(1),
                "the pending look is an emphasis over the one box, not a second box");
            Assert.That(outline.Find("Edges"), Is.Null);
            Assert.That(replica.GetComponentsInChildren<LineRenderer>(true).All(l => !l.enabled), Is.True,
                "the unmatched remote box's wires and pending outline are receiver-only presentation and stay hidden");
            Assert.That(entity.AffordanceHost, Is.Not.Null, "the affordance host remains live for placement");
            // In front of the user, on the floor, local +Z up.
            Vector3 flat = new Vector3(0f, 0f, 1f);
            Vector3 centre = replica.transform.TransformPoint(new Vector3(0f, 0f, -0.425f));
            Assert.That(Vector3.Dot(centre - new Vector3(0f, 1.6f, 0f), flat), Is.EqualTo(SharedRoomObjectBox.ArrivalDistance).Within(1e-3f));
            Assert.That(replica.transform.TransformPoint(new Vector3(0f, 0f, -0.85f)).y, Is.EqualTo(0.05f).Within(1e-4f), "its bottom is on the floor");
            Assert.That(Vector3.Dot(replica.transform.TransformDirection(Vector3.forward), Vector3.up), Is.GreaterThan(0.999f), "local +Z is up");
            Assert.That(published.Published, Is.Empty, "the receiver shares nothing back for a box");
        }

        [Test]
        public void ReceiverPresentationPolicySurvivesTheActualBoxPlacementAndOutlineRefreshLifecycle()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            FreeformCollaborationApp app = NewApp(Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false)), out _);
            GameObject replica = ReplicaOf(sent);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            SharedRoomObjectBox box = app.SharedBoxes.Single();
            var entity = replica.GetComponent<SbMRUKAnchorEntity>();
            BoxCollider support = replica.GetComponent<BoxCollider>();
            BoxCollider affordance = entity.AffordanceHost.GetComponent<BoxCollider>();

            Assert.That(sent.GetComponentsInChildren<LineRenderer>().All(l => l.enabled), Is.True,
                "the sharer's local/source support presentation remains visible");
            Assert.That(replica.GetComponentsInChildren<LineRenderer>(true).All(l => !l.enabled), Is.True,
                "only the receiver's unmatched box presentation is hidden");

            box.Place();
            replica.transform.position += Vector3.right * 0.3f;
            box.Tick(); // drives the production move-to-unplaced path and rebuilds the pending outline.
            Transform rebuiltOutline = replica.transform.Find("SharedBoxOutline");
            Assert.That(rebuiltOutline, Is.Not.Null);
            Assert.That(rebuiltOutline.GetComponentsInChildren<LineRenderer>(true).All(l => !l.enabled), Is.True,
                "the policy is applied as the outline is rebuilt, not by a per-frame pass");
            Assert.That(typeof(SbMRUKAnchorEntity).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null,
                "no per-frame presentation pass");
            entity.ApplyReplicaBox(); // real support-refresh path

            Assert.That(replica.GetComponentsInChildren<LineRenderer>(true).All(l => !l.enabled), Is.True,
                "the policy hides both Tick-rebuilt outline and support presentation again");
            Assert.That(support.enabled, Is.True);
            Assert.That(affordance.enabled, Is.True);
            Assert.That(entity.AllowManipulation, Is.True);
            box.Place();
            Assert.That(box.IsPlaced, Is.True, "placement remains available after the presentation refresh");
        }

        [Test]
        public void APlacedBoxIsTheCounterpartAndASeatBoxBecomesTheReceiversSeat()
        {
            // The sharer confirmed the seat's front before sharing it.
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord record = Add(senderCatalog, "COUCH", "sender-couch", new Vector3(10f, 0.45f, 10f), new Vector3(2.0f, 0.9f, 0.85f), true);
            FreeformCollaborationApp sender = NewApp(senderCatalog, out _);
            Assert.That(sender.TryConfirmSeatFront(record, record.Anchor.transform.TransformPoint(new Vector3(0f, 0.5f, 0f))), Is.True);
            sender.TryShareAnchor(record);
            var catalog = Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);
            GameObject replica = ReplicaOf(record.Anchor);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            SharedRoomObjectBox box = app.SharedBoxes.Single();
            var entity = replica.GetComponent<SbMRUKAnchorEntity>();
            long id = SeatIdentity.SharedId("sender-couch");
            Assert.That(entity.IsSeat, Is.True, "the box is a Sittable seat with the sharer's measured box");
            Assert.That(entity.GetComponent<SbSpatialAuthoring>().affordances.Single().tag, Is.EqualTo("Sittable"));
            Assert.That(app.SeatLinks.TryGetPair(id, out _), Is.False, "not the receiver's seat until it is placed");

            var placed = new List<SharedRoomObjectBox>();
            app.BoxPlaced += placed.Add;
            box.Place();

            Assert.That(box.IsPlaced, Is.True);
            Assert.That(placed, Is.EqualTo(new[] { box }));
            Assert.That(app.SeatLinks.TryGetPair(id, out SeatLinkBook.SeatPair pair), Is.True);
            Assert.That(pair.FrontConfirmedOnBoth, Is.True, "the front the sharer confirmed travelled with the box");
            Assert.That(replica.transform.Find("SharedBoxOutline"), Is.Null, "the pending outline goes once placed");
        }

        [Test]
        public void ABoxCanBeTurnedAQuarterAndPlacedAgain()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            FreeformCollaborationApp app = NewApp(Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false)), out _);
            GameObject replica = ReplicaOf(sent);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            SharedRoomObjectBox box = app.SharedBoxes.Single();
            box.Place();
            Quaternion before = replica.transform.rotation;
            box.RotateQuarterTurn();
            Assert.That(Quaternion.Angle(before, replica.transform.rotation), Is.EqualTo(90f).Within(0.01f));
            Assert.That(Vector3.Dot(replica.transform.TransformDirection(Vector3.forward), Vector3.up), Is.GreaterThan(0.999f), "still upright");
            Assert.That(box.IsPlaced, Is.False, "moved, so it is placed again");
        }

        [Test]
        public void AnUnsharedSeatProducesNoSitTransferReferent()
        {
            var catalog = Room(("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher publisher);
            // A couch is not a desk, so it is unmarked and unpublished.
            Assert.That(catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            Assert.That(catalog.Anchors[0].Anchor.GetComponent<SbSpatialAuthoring>(), Is.Null);
            Assert.That(app.SeatLinks.TryGetLocal("c1", out _), Is.False);
            Assert.That(app.SeatLinks.FrontConfirmed(SeatIdentity.SharedId("c1")), Is.False);
            Assert.That(publisher.Published, Is.Empty);
        }

        // ------------------------------------------------------------------ unshare and leave

        [Test]
        public void WhenTheSharerUnsharesTheBoxGoesAndTheLinkedAnchorIsReleasedBySignal()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            var catalog = Room(("COUCH", "c-local", new Vector3(1.9f, 0.9f, 0.85f), true), ("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);

            // A linked anchor: the core tells the receiver when the sharer unshares (link-removed, PeerUnshared, core ABI 4.9); the
            // element survives there, and the app releases its counterpart.
            app.OnShare(ReplicaOf(sent));
            GameObject local = catalog.Anchors[0].Anchor;
            MarkAttached(local);
            app.OnShare(ReplicaOf(sent));
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.Linked));
            app.OnRemove(local);
            Assert.That(local.GetComponent<SbMRUKAnchorEntity>(), Is.Not.Null, "OnRemove is not the signal for a linked anchor");
            app.OnLinkRemoved(local, LinkRemovedReason.PeerUnshared);
            Assert.That(local.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "the link-removed signal released the counterpart");
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.None));
            Assert.That(app.SeatLinks.TryGetPair(SeatIdentity.SharedId("sender-couch"), out _), Is.False, "and its pair is forgotten");
            Assert.That(local.name, Is.EqualTo("COUCH-c-local"), "the MRUK anchor's own name is back");

            // A box: destroyed with its pair.
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord other = Add(senderCatalog, "STORAGE", "st", new Vector3(10f, 0.45f, 10f), new Vector3(1f, 0.4f, 1.8f));
            NewApp(senderCatalog, out _).TryShareAnchor(other);
            GameObject replica = ReplicaOf(other.Anchor);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            Assert.That(app.SharedBoxes.Count, Is.EqualTo(1));
            app.OnRemove(replica);
            Assert.That(app.SharedBoxes, Is.Empty);
            Assert.That(replica == null, Is.True, "the box is destroyed");
        }

        [Test]
        public void WhenTheReceiverUnlinksTheSharedAnchorStaysSharedAndTheListSaysNothingThereHoldsIt()
        {
            var catalog = Room(("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false), ("BED", "b1", new Vector3(2f, 1.4f, 0.5f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);
            Assert.That(app.TryShareAnchor(catalog.Anchors[0]), Is.True);
            Assert.That(app.TryShareAnchor(catalog.Anchors[1]), Is.True);
            GameObject table = catalog.Anchors[0].Anchor;
            Assert.That(app.PeerUnlinkedOf(catalog.Anchors[0]), Is.False);

            app.OnLinkRemoved(table, LinkRemovedReason.PeerUnlinked);   // the other room dropped its counterpart

            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.Shared), "still shared here");
            Assert.That(table.GetComponent<SbMRUKAnchorEntity>(), Is.Not.Null);
            Assert.That(app.PeerUnlinkedOf(catalog.Anchors[0]), Is.True);
            Assert.That(app.PeerUnlinkedOf(catalog.Anchors[1]), Is.False, "only that anchor");

            Assert.That(app.TryUnshareAnchor(catalog.Anchors[0]), Is.True);
            Assert.That(app.TryShareAnchor(catalog.Anchors[0]), Is.True);
            Assert.That(app.PeerUnlinkedOf(catalog.Anchors[0]), Is.False);
        }

        [Test]
        public void AnEchoOfAnAnchorThisUserUnsharedIsNotAdoptedAgain()
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord record = Add(senderCatalog, "TABLE", "t1", new Vector3(0f, 0.8f, 0f), new Vector3(1.4f, 0.8f, 0.75f));
            FreeformCollaborationApp sharer = NewApp(senderCatalog, out _);
            Assert.That(sharer.TryShareAnchor(record), Is.True);
            GameObject echo = ReplicaOf(record.Anchor);   // the other room's echo of this very anchor, arriving after the unshare
            Assert.That(sharer.TryUnshareAnchor(record), Is.True);

            Assert.That(sharer.OnShare(echo).result, Is.EqualTo(OnShareResponse.Result.Reject), "it would re-share the anchor without consent");
            Assert.That(record.Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            Assert.That(sharer.TryShareAnchor(record), Is.True, "sharing it again on purpose clears the tombstone");
            Assert.That(sharer.ShareStateOf(record), Is.EqualTo(AnchorShareState.Shared));
        }

        [Test]
        public void ASignalForAnAnchorThisUserDoesNotHoldIsIgnoredAndUnlinkIsAnOptionalCoreFeature()
        {
            var catalog = Room(("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);
            app.OnLinkRemoved(catalog.Anchors[0].Anchor, LinkRemovedReason.PeerUnshared);
            app.OnLinkRemoved(null, LinkRemovedReason.PeerUnlinked);
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.None));
            // Core ABI 4.9 added the unlink. The minimum (4.11, the pursued-target proposals) now covers it, so the entry points are always there.
            Assert.That(SpatialBlendAPI.LinkRemovalAbiMinor, Is.EqualTo(9u));
            Assert.That(SpatialBlendAPI.RequiredAbiMinor, Is.GreaterThanOrEqualTo(SpatialBlendAPI.LinkRemovalAbiMinor));
        }

        [Test]
        public void APlainCouchWithNoPlaneIsAPlainAnchorOnBothSidesNeverASeat()
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord plain = Add(senderCatalog, "COUCH", "volume-only", new Vector3(10f, 0.45f, 10f), new Vector3(2.0f, 0.9f, 0.85f), false);
            FreeformCollaborationApp sender = NewApp(senderCatalog, out _);
            Assert.That(sender.TryShareAnchor(plain), Is.True);
            Assert.That(plain.Anchor.GetComponent<SbMRUKAnchorEntity>().IsSeat, Is.False);

            var catalog = Room(("COUCH", "c-local", new Vector3(1.9f, 0.9f, 0.85f), true));
            FreeformCollaborationApp app = NewApp(catalog, out Publisher published);
            GameObject replica = ReplicaOf(plain.Anchor);
            Assert.That(replica.GetComponent<SbMRUKAnchorEntity>().IsSeat, Is.False, "the wire name of a plain couch does not read as a seat");

            app.OnShare(replica);
            var local = catalog.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(local.IsSeat, Is.False, "registered as the sharer registered its own: a plain anchor, even though this couch has a plane");
            Assert.That(app.SeatLinks.LocalKeys, Is.Empty);
        }

        [Test]
        public void AFailedPublishLeavesNothingOnTheAnchor()
        {
            var catalog = Room(("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false), ("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true));
            FreeformCollaborationApp app = NewApp(catalog, out _);
            app.UseSceneAnchorPublisher(null);   // no publisher and no manager: nothing can be published
            Assert.That(app.TryShareAnchor(catalog.Anchors[0]), Is.False);
            Assert.That(app.TryShareAnchor(catalog.Anchors[1]), Is.False);
            foreach (SceneAnchorRecord record in catalog.Anchors)
            {
                Assert.That(record.Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
                Assert.That(record.Anchor.GetComponentsInChildren<LineRenderer>().Length, Is.EqualTo(0));
                Assert.That(record.Anchor.name, Does.Not.Contain("@"));
            }
            Assert.That(app.SeatLinks.LocalKeys, Is.Empty);
        }

        [Test]
        public void ThePlacedBoxCentreArrivesInFrontOfTheUserAndAMovedBoxIsNoLongerPlaced()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            FreeformCollaborationApp app = NewApp(Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false)), out _);
            GameObject replica = ReplicaOf(sent);
            replica.GetComponent<SbMRUKAnchorEntity>().TryGetMeasuredBox(out Vector3 center, out Vector3 size);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            // The box's centre, not the anchor's origin, is one ArrivalDistance ahead of the user (forward is tilted: the flat direction counts).
            Vector3 worldCentre = replica.transform.TransformPoint(center);
            Vector3 flatToCentre = new Vector3(worldCentre.x, 0f, worldCentre.z);
            Assert.That(flatToCentre.magnitude, Is.EqualTo(SharedRoomObjectBox.ArrivalDistance).Within(1e-3f));

            SharedRoomObjectBox box = app.SharedBoxes.Single();
            box.Place();
            box.Tick();
            Assert.That(box.IsPlaced, Is.True, "untouched: still placed");
            replica.transform.position += new Vector3(0.3f, 0f, 0f);
            box.Tick();
            Assert.That(box.IsPlaced, Is.False, "moved after placing: to be placed again");
            Assert.That(replica.transform.Find("SharedBoxOutline"), Is.Not.Null, "and outlined again");
            box.Place();
            Assert.That(box.IsPlaced, Is.True);
        }

        [Test]
        public void LeavingTheRoomCleansUpEveryLinkAndBox()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            var catalog = Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false), ("BED", "b", new Vector3(2f, 1.4f, 0.5f), false));
            FreeformCollaborationApp app = NewApp(catalog, out _);
            GameObject replica = ReplicaOf(sent);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            Assert.That(app.SharedBoxes.Count, Is.EqualTo(1));
            Assert.That(app.TryShareAnchor(catalog.Anchors[1]), Is.True);
            Assert.That(catalog.Anchors[1].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Not.Null);

            app.OnAppStop();

            Assert.That(catalog.Anchors[1].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "this user's registrations leave the room's anchors");
            Assert.That(catalog.Anchors[1].Anchor.GetComponentsInChildren<LineRenderer>().Length, Is.EqualTo(0));

            Assert.That(app.SharedBoxes, Is.Empty);
            Assert.That(replica == null, Is.True);
            Assert.That(app.SeatLinks.LocalKeys, Is.Empty);
            Assert.That(app.PendingSeatKeys, Is.Empty);
        }

        private sealed class RoomKeyCatalog : ISceneAnchorCatalog, IRoomScopedCatalog
        {
            public readonly ListSceneAnchorCatalog Inner;
            public string Key = "room-a";
            public RoomKeyCatalog(ListSceneAnchorCatalog inner) { Inner = inner; }
            public IReadOnlyList<SceneAnchorRecord> Anchors => Inner.Anchors;
            public string RoomKey => Key;
        }

        [Test]
        public void WhenTheConfirmedRoomChangesTheShareAndTheSeatLinksOfTheOldRoomAreDropped()
        {
            ListSceneAnchorCatalog inner = Room(("COUCH", "c1", new Vector3(1.9f, 0.9f, 0.85f), true), ("TABLE", "t1", new Vector3(1.4f, 0.8f, 0.75f), false));
            var catalog = new RoomKeyCatalog(inner);
            FreeformCollaborationApp app = NewApp(inner, out _);
            app.UseSceneAnchorCatalog(catalog);   // the confirmed-room catalog: its anchors are the room's, its key scopes this user's state
            Assert.That(app.ShareableAnchors().Count, Is.EqualTo(2));
            Assert.That(app.TryShareAnchor(inner.Anchors[0]), Is.True);
            Assert.That(app.TryShareAnchor(inner.Anchors[1]), Is.True);
            Assert.That(app.SeatLinks.TryGetLocal("c1", out _), Is.True);
            Assert.That(inner.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Not.Null);

            catalog.Key = "room-b";   // a re-scan, another space
            app.ShareableAnchors();

            Assert.That(inner.Anchors[0].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "what was shared from the old room is released");
            Assert.That(inner.Anchors[1].Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            Assert.That(app.ShareStateOf(inner.Anchors[0]), Is.EqualTo(AnchorShareState.None));
            Assert.That(app.SeatLinks.LocalKeys, Is.Empty);
            Assert.That(app.SeatLinks.RoomKey, Is.EqualTo("room-b"));
            Assert.That(app.TryShareAnchor(inner.Anchors[1]), Is.True, "and the new room shares afresh");
        }

        [Test]
        public void APlacedSeatBoxKeepsItsPairWhenTheRoomScopeResets()
        {
            var (_, sent) = SenderSharedCouch(new Vector3(2.0f, 0.9f, 0.85f));
            ListSceneAnchorCatalog inner = Room(("TABLE", "t", new Vector3(1.2f, 0.7f, 0.7f), false));
            var catalog = new RoomKeyCatalog(inner);
            FreeformCollaborationApp app = NewApp(inner, out _);
            app.UseSceneAnchorCatalog(catalog);
            app.ShareableAnchors();   // the scope is room-a
            GameObject replica = ReplicaOf(sent);
            app.OnShare(replica);
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            SharedRoomObjectBox box = app.SharedBoxes.Single();
            box.Place();
            long id = SeatIdentity.SharedId("sender-couch");
            Assert.That(app.SeatLinks.TryGetPair(id, out _), Is.True);

            catalog.Key = "room-b";
            app.ShareableAnchors();

            Assert.That(app.SeatLinks.RoomKey, Is.EqualTo("room-b"));
            Assert.That(app.SeatLinks.TryGetPair(id, out _), Is.True, "a placed box is still the counterpart: its pair is recorded again after the reset");
            Assert.That(box.IsPlaced, Is.True);
        }
    }
}
#endif
