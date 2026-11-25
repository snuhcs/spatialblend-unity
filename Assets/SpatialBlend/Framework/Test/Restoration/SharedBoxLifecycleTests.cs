// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The boxes the other user's shared objects became, beyond placing them. While one is unplaced the idle return of Editing
    /// is held (a receiver deciding where it goes must not be switched away). A box deleted through the generic object menu leaves
    /// <see cref="FreeformCollaborationApp.SharedBoxes"/> (and its seat pair) at once, not at the next remove or leave.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SharedBoxLifecycleTests
    {
        private static readonly Vector2[] SeatOutline =
        {
            new Vector2(-0.3f, -0.2f), new Vector2(0.3f, -0.2f), new Vector2(0.3f, 0.2f), new Vector2(-0.3f, 0.2f),
        };
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void Destroy()
        {
            for (int i = _objects.Count - 1; i >= 0; --i) if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        private sealed class Publisher : ISceneAnchorPublisher
        {
            public void Publish(GameObject anchor, bool shareWithPeers) { }
        }

        private SceneAnchorRecord Add(ListSceneAnchorCatalog catalog, string label, string id, Vector3 position, Vector3 size, bool plane)
        {
            var go = new GameObject(label + "-" + id);
            _objects.Add(go);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(-90f, 0f, 0f));
            var record = new SceneAnchorRecord
            {
                Label = label, Anchor = go, AnchorId = id, SourceKey = go.GetInstanceID(), Size = size.x * size.y * size.z,
                LocalCenter = new Vector3(0f, 0f, -size.z * 0.5f), LocalSize = size, PlaneBoundary = plane ? SeatOutline : null,
            };
            catalog.Add(record);
            return record;
        }

        private FreeformCollaborationApp NewApp(ListSceneAnchorCatalog catalog)
        {
            var go = new GameObject("app");
            _objects.Add(go);
            var app = go.AddComponent<FreeformCollaborationApp>();
            app.UseSceneAnchorCatalog(catalog);
            app.UseSceneAnchorPublisher(new Publisher());
            app.UseSeatPreferences(new MemorySeatPreferences());
            app.UserViewProvider = () => (new Vector3(0f, 1.6f, 0f), new Vector3(0f, 0.2f, 1f));
            app.FloorHeightProvider = () => 0.05f;
            return app;
        }

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

        // A box with nothing similar in the receiver's room: the sharer's couch (or a plain storage unit) arrives as a box.
        private (FreeformCollaborationApp app, GameObject replica, SharedRoomObjectBox box) ArrivedBox(bool seat)
        {
            var senderCatalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord record = seat
                ? Add(senderCatalog, "COUCH", "sender-couch", new Vector3(10f, 0.45f, 10f), new Vector3(2.0f, 0.9f, 0.85f), true)
                : Add(senderCatalog, "STORAGE", "st", new Vector3(10f, 0.45f, 10f), new Vector3(1f, 0.4f, 1.8f), false);
            NewApp(senderCatalog).TryShareAnchor(record);
            var catalog = new ListSceneAnchorCatalog();
            Add(catalog, "TABLE", "t", new Vector3(0f, 0.45f, 0f), new Vector3(0.4f, 0.3f, 0.3f), false);
            FreeformCollaborationApp app = NewApp(catalog);
            GameObject replica = ReplicaOf(record.Anchor);
            Assert.That(app.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
            app.OnObjectAdded(replica, SbApp.OnObjectAddedType.Added);
            return (app, replica, app.SharedBoxes.Single());
        }

        private static bool AnyBusyProbe()
        {
            var probes = (List<Func<bool>>)typeof(ModeSwitchAssist).GetField("s_BusyProbes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            return probes.ToArray().Any(p => p());
        }

        [SetUp]
        public void ClearProbes()
        {
            ((List<Func<bool>>)typeof(ModeSwitchAssist).GetField("s_BusyProbes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Clear();
        }

        // ---- the idle return is held while a box is unplaced ----

        [Test]
        public void AnUnplacedSharedBoxHoldsEditingBusyAndPlacingItReleasesIt()
        {
            var (app, _, box) = ArrivedBox(false);
            Assert.That(app.HasUnplacedSharedBox(), Is.True);
            Assert.That(AnyBusyProbe(), Is.True, "the assist's busy probe is true while the box is unplaced");

            box.Place();

            Assert.That(app.HasUnplacedSharedBox(), Is.False);
            Assert.That(AnyBusyProbe(), Is.False);
        }

        [Test]
        public void ABoxMovedAfterPlacingIsUnplacedAgainAndHoldsEditingAgain()
        {
            var (app, replica, box) = ArrivedBox(false);
            box.Place();
            replica.transform.position += Vector3.right;
            box.Tick();
            Assert.That(AnyBusyProbe(), Is.True);
        }

        [Test]
        public void WithNoSharedBoxEditingIsNotHeldAndADestroyedAppHoldsNothing()
        {
            var catalog = new ListSceneAnchorCatalog();
            FreeformCollaborationApp app = NewApp(catalog);
            Assert.That(AnyBusyProbe(), Is.False);

            var (second, _, _) = ArrivedBox(false);
            Assert.That(AnyBusyProbe(), Is.True);
            UnityEngine.Object.DestroyImmediate(second.gameObject);
            Assert.That(AnyBusyProbe(), Is.False, "a stale probe of a destroyed app answers false");
            Assert.That(app.HasUnplacedSharedBox(), Is.False);
        }

        [Test]
        public void ABoxThatIsGoneNoLongerHoldsEditing()
        {
            var (_, replica, _) = ArrivedBox(false);
            UnityEngine.Object.DestroyImmediate(replica);
            Assert.That(AnyBusyProbe(), Is.False);
        }

        // ---- a deleted box leaves SharedBoxes at once ----

        [Test]
        public void ABoxDeletedThroughTheObjectMenuLeavesSharedBoxesAtOnce()
        {
            var (app, replica, _) = ArrivedBox(false);
            Assert.That(app.SharedBoxes.Count, Is.EqualTo(1));

            // ManipulationContext.ExecuteDeleteMode: Detach, then Destroy.
            UnityEngine.Object.DestroyImmediate(replica);

            Assert.That(app.SharedBoxes, Is.Empty);
        }

        [Test]
        public void SharedBoxesNeverListsADestroyedBoxEvenBeforeTheEntryIsPruned()
        {
            var (app, replica, _) = ArrivedBox(false);
            var field = typeof(FreeformCollaborationApp).GetField("_boxes", BindingFlags.Instance | BindingFlags.NonPublic);
            var dictionary = (Dictionary<GameObject, SharedRoomObjectBox>)field.GetValue(app);
            var box = replica.GetComponent<SharedRoomObjectBox>();
            box.Destroyed -= (Action<SharedRoomObjectBox>)Delegate.CreateDelegate(typeof(Action<SharedRoomObjectBox>), app,
                typeof(FreeformCollaborationApp).GetMethod("OnBoxDestroyed", BindingFlags.Instance | BindingFlags.NonPublic));

            UnityEngine.Object.DestroyImmediate(replica);

            Assert.That(dictionary.Count, Is.EqualTo(1), "the entry is still there (nothing pruned it)");
            Assert.That(app.SharedBoxes, Is.Empty, "but the list the UI reads does not show it");
        }

        // EditMode does not call OnDestroy of a MonoBehaviour that is not ExecuteAlways, but a player does. Raise it the way a player would
        // (before the object goes), so the box's Destroyed event is exercised.
        private static void DestroyAsPlayerDoes(GameObject target)
        {
            foreach (SharedRoomObjectBox box in target.GetComponents<SharedRoomObjectBox>())
                typeof(SharedRoomObjectBox).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(box, null);
            UnityEngine.Object.DestroyImmediate(target);
        }

        [Test]
        public void ADeletedSeatBoxForgetsItsPairToo()
        {
            var (app, replica, box) = ArrivedBox(true);
            box.Place();
            long id = SeatIdentity.SharedId("sender-couch");
            Assert.That(app.SeatLinks.TryGetPair(id, out _), Is.True);

            DestroyAsPlayerDoes(replica);

            Assert.That(app.SharedBoxes, Is.Empty);
            Assert.That(app.SeatLinks.TryGetPair(id, out _), Is.False);
        }

        [Test]
        public void TheAppsOwnReleaseStillRemovesTheBoxOnce()
        {
            var (app, replica, _) = ArrivedBox(false);
            app.OnRemove(replica);
            Assert.That(app.SharedBoxes, Is.Empty);
            Assert.That(replica == null, Is.True);
        }
    }
}
#endif
