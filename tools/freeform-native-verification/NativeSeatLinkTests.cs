// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    // Only copied into the explicitly provisioned full-project native gate, as a part of NativeManagerIntegrationTests (its managers,
    // loopback and helpers are REAL and shared). Sit transfer: a COUCH anchor with a measured plane is registered as a shared
    // Sittable seat on the MRUK scene geometry the adapter already owns, shared, and linked to the receiver's seat by the user's key.
    public partial class NativeManagerIntegrationTests
    {
        private static readonly Vector2[] HostSeatOutline =
        {
            new Vector2(-0.30f, -0.22f), new Vector2(0.34f, -0.22f), new Vector2(0.26f, 0.21f), new Vector2(-0.22f, 0.21f),
        };
        private static readonly Vector2[] ClientSeatOutline =
        {
            new Vector2(-0.45f, -0.40f), new Vector2(0.45f, -0.40f), new Vector2(0.45f, 0.38f), new Vector2(-0.45f, 0.38f),
        };

        // An MRUK-style seat: local +Z is the plane normal (the anchor stands at Rx(-90)), the origin is on the seat plane, and the
        // volume runs from the floor over the seat to the top of the backrest.
        private GameObject SeatAnchor(string name, Vector3 position, float yawDegrees)
        {
            GameObject anchor = ActiveAnchor(name);
            anchor.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f) * Quaternion.Euler(-90f, 0f, 0f));
            return anchor;
        }

        // PumpUntil with a name, so a timeout says which step it was.
        private static void PumpUntilNamed(string what, Func<bool> done, params SpatialBlendManager[] managers)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!done() && DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < managers.Length; ++i) managers[i].Update();
                System.Threading.Thread.Sleep(10);
            }
            Assert.That(done(), Is.True, "timed out waiting for: " + what);
        }

        private static SceneAnchorRecord SeatRecord(GameObject anchor, int key, string anchorId, Vector2[] outline, Vector3 volumeCenter,
                                                    Vector3 volumeSize)
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(key, anchor.transform.localToWorldMatrix,
                new Bounds(volumeCenter, volumeSize), outline, null, out _), Is.True);
            return new SceneAnchorRecord
            {
                Label = "COUCH", Anchor = anchor, AnchorId = anchorId, SourceKey = key, Size = 1f,
                LocalCenter = volumeCenter, LocalSize = volumeSize, PlaneBoundary = outline,
            };
        }

        private static void RegisterSeatGeometry(SpatialBlendManager manager, SceneAnchorRecord seat)
        {
            Assert.That(SceneGeometryDescriptor.TryCreate(seat.SourceKey, seat.Anchor.transform.localToWorldMatrix,
                new Bounds(seat.LocalCenter, seat.LocalSize), seat.PlaneBoundary, null, out SceneGeometryDescriptor descriptor), Is.True);
            Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
        }

        [Test]
        public void SharedSeatsLinkToTheRoomsSimilarSeatOrArriveAsABoxToInstall()
        {
            var host = CreateManager("seat-host");
            var client = CreateManager("seat-client");
            int port = FreePort();
            host.Host(port);
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);

            // Two different rooms: different seats in size, shape, height and yaw. The client's decoy is the nearer one to where the
            // host's seat stands but is a different size: among seats the one closest in size links (SharedAnchorMatching), never the nearest chair.
            Vector3 hostCenter = new Vector3(0f, 0.12f, 0f), hostSize = new Vector3(0.70f, 0.50f, 1.04f);
            Vector3 clientCenter = new Vector3(0f, 0f, 0.1f), clientSize = new Vector3(0.80f, 0.60f, 0.95f);
            Vector3 decoySize = new Vector3(0.40f, 0.30f, 0.45f);
            SceneAnchorRecord hostLinked = SeatRecord(SeatAnchor("host-seat-7", new Vector3(2f, 0.45f, 0f), 20f), 7101, "h7",
                HostSeatOutline, hostCenter, hostSize);
            SceneAnchorRecord hostOther = SeatRecord(SeatAnchor("host-seat-8", new Vector3(2f, 0.45f, 4f), 200f), 7102, "h8",
                HostSeatOutline, hostCenter, hostSize);
            SceneAnchorRecord clientLinked = SeatRecord(SeatAnchor("client-seat-7", new Vector3(-6f, 0.58f, 1f), 75f), 7103, "c7",
                ClientSeatOutline, clientCenter, clientSize);
            SceneAnchorRecord clientDecoy = SeatRecord(SeatAnchor("client-decoy", new Vector3(2.5f, 0.40f, 0.5f), 0f), 7104, "cd",
                ClientSeatOutline, clientCenter, decoySize);
            foreach (SceneAnchorRecord seat in new[] { hostLinked, hostOther }) RegisterSeatGeometry(host, seat);
            foreach (SceneAnchorRecord seat in new[] { clientLinked, clientDecoy }) RegisterSeatGeometry(client, seat);
            var hostApp = ActiveApp("seat-host-app", Catalog(hostLinked, hostOther));
            var clientApp = ActiveApp("seat-client-app", Catalog(clientLinked, clientDecoy));
            hostApp.UseSeatPreferences(new MemorySeatPreferences());
            clientApp.UseSeatPreferences(new MemorySeatPreferences());
            clientApp.UserViewProvider = () => (new Vector3(-6f, 1.6f, 4f), Vector3.forward);
            SetAppManager(hostApp, host);
            SetAppManager(clientApp, client);
            Invoke(host, "RegisterApp", hostApp);
            Invoke(client, "RegisterApp", clientApp);

            // Nothing is shared by itself: both rooms' seats are local obstacle geometry only.
            Pump(100, host, client);
            Assert.That(clientLinked.Anchor.GetComponent<SbEntity>(), Is.Null);
            Assert.That(hostLinked.Anchor.GetComponent<SbEntity>(), Is.Null);

            // The host's user shares seat 7 from the room-object list. The client has a similar seat of its own: it is registered under the
            // sharer's key and linked, with no prompt (the decoy next to it is not touched).
            Assert.That(hostApp.TryShareAnchor(hostLinked), Is.True);
            SbMRUKAnchorEntity clientSeat = clientLinked.Anchor.GetComponent<SbMRUKAnchorEntity>();
            PumpUntilNamed("step 1", () => clientApp.ShareStateOf(clientLinked) == AnchorShareState.Linked, host, client);
            clientSeat = clientLinked.Anchor.GetComponent<SbMRUKAnchorEntity>();
            PumpUntilNamed("step 2", () => clientSeat.IsAttached(), host, client);
            PumpUntilNamed("step 2b", () => { clientApp.PollPendingSeatShares(); hostApp.PollPendingSeatShares(); return host.DeferredShareCount == 0 && client.DeferredShareCount == 0; }, host, client);
            Pump(200, host, client);
            Assert.That(clientDecoy.Anchor.GetComponent<SbEntity>(), Is.Null, "the other seat is not registered");
            Assert.That(client.TryGetLocalSceneGeometryElements(7103, out long clientParent, out long clientPlane), Is.True);
            Assert.That(clientSeat.ID, Is.EqualTo(clientParent), "the seat is attached to the adapter's element, not a second one");
            Assert.That(clientPlane, Is.GreaterThan(0), "the adapter measured a plane for this seat");
            long sharedId = SeatIdentity.SharedId("h7");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, sharedId), Is.EqualTo(clientParent),
                "resolving LinkToExisting removes the received temp; one element per seat remains");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, SpatialAuthoringCompiler.AffordanceSharedId(sharedId, 0)),
                Is.EqualTo(clientPlane), "the adapter's plane is the Sittable surface; no second Surface was added");
            Assert.That(client.SceneRegistrationError, Is.Null, "the core accepted the seat part: " + client.SceneRegistrationError);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
            Assert.That(clientApp.SeatLinks.TryGetPair(sharedId, out SeatLinkBook.SeatPair pair), Is.True);
            Assert.That(pair.Key, Is.EqualTo("h7"));
            Assert.That(pair.FrontConfirmedOnBoth, Is.False, "no front was confirmed on either seat");
            Assert.That(clientApp.SharedBoxes, Is.Empty);
            Assert.That(CountAnchors("COUCH"), Is.EqualTo(2), "the host's seat and the client's seat; no reconstructed clone");

            // The host shares its other seat (8). The client's remaining COUCH is the decoy, much smaller: it links all the same (link
            // by label; the chart mapping carries one shape onto the other). No box, and one element per shared id.
            Assert.That(hostApp.TryShareAnchor(hostOther), Is.True);
            PumpUntilNamed("step 3", () => clientApp.ShareStateOf(clientDecoy) == AnchorShareState.Linked, host, client);
            SbMRUKAnchorEntity decoySeat = clientDecoy.Anchor.GetComponent<SbMRUKAnchorEntity>();
            PumpUntilNamed("step 3b", () => { clientApp.PollPendingSeatShares(); return decoySeat.IsAttached() && client.DeferredShareCount == 0; }, host, client);
            Pump(200, host, client);
            long decoySharedId = SeatIdentity.SharedId("h8");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, decoySharedId), Is.EqualTo(decoySeat.ID),
                "resolving LinkToExisting removes the received temp; one element per seat remains");
            Assert.That(clientApp.SharedBoxes, Is.Empty, "the room has a COUCH: no box");
            Assert.That(clientApp.SeatLinks.TryGetPair(decoySharedId, out _), Is.True);
            Assert.That(client.SceneRegistrationError, Is.Null, client.SceneRegistrationError);

            // The host takes seat 8 back: the decoy's link and pair go, the decoy stays a local obstacle.
            Assert.That(hostApp.TryUnshareAnchor(hostOther), Is.True);
            PumpUntilNamed("step 4", () => clientApp.ShareStateOf(clientDecoy) == AnchorShareState.None, host, client);
            Assert.That(clientApp.SeatLinks.TryGetPair(decoySharedId, out _), Is.False);

            // The host takes seat 7 back. Nothing of the registration is left on the host's element: no shared id, no Sittable
            // affordance, no region; the seat is a local obstacle again (its geometry is staged afresh).
            long hostAffordance = SpatialAuthoringCompiler.AffordanceSharedId(sharedId, 0);
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID, sharedId), Is.GreaterThan(0));
            Assert.That(hostApp.TryUnshareAnchor(hostLinked), Is.True);
            Pump(300, host, client);
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID, sharedId), Is.LessThanOrEqualTo(0), "the shared id is gone from the host's element");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID, hostAffordance), Is.LessThanOrEqualTo(0), "and the Sittable affordance");
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID, SpatialAuthoringCompiler.RegionSharedId(sharedId)), Is.LessThanOrEqualTo(0), "and the region");
            Assert.That(host.TryGetLocalSceneGeometryElements(7101, out long hostParentAgain, out long hostPlaneAgain), Is.True, "the obstacle geometry is back");
            Assert.That(hostParentAgain, Is.GreaterThan(0));
            Assert.That(hostPlaneAgain, Is.GreaterThan(0));
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
            Assert.That(hostLinked.Anchor.name, Is.EqualTo("host-seat-7"), "the MRUK anchor's own name is back");

            // The core tells the client when the host unshares (link-removed, PeerUnshared, ABI 4.9): its linked seat is released by itself,
            // and it is clean too.
            PumpUntilNamed("step 4c", () => clientApp.ShareStateOf(clientLinked) == AnchorShareState.None, host, client);
            Pump(300, host, client);
            Assert.That(clientLinked.Anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null);
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, sharedId), Is.LessThanOrEqualTo(0));
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, hostAffordance), Is.LessThanOrEqualTo(0));
            Assert.That(client.TryGetLocalSceneGeometryElements(7103, out long clientAgain, out _), Is.True);
            Assert.That(clientAgain, Is.GreaterThan(0));
            Assert.That(clientApp.SeatLinks.TryGetPair(sharedId, out _), Is.False);
            Assert.That(client.SceneRegistrationError, Is.Null, client.SceneRegistrationError);

            // Sharing again works from the clean state: the host shares seat 7 once more and the client links it again.
            Assert.That(hostApp.TryShareAnchor(hostLinked), Is.True);
            PumpUntilNamed("step 5", () => clientApp.ShareStateOf(clientLinked) == AnchorShareState.Linked, host, client);
            PumpUntilNamed("step 5b", () => { clientApp.PollPendingSeatShares(); hostApp.PollPendingSeatShares(); return host.DeferredShareCount == 0 && client.DeferredShareCount == 0; }, host, client);
            Pump(200, host, client);
            Assert.That(clientApp.SeatLinks.TryGetPair(sharedId, out _), Is.True);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
            Assert.That(client.SceneRegistrationError, Is.Null, client.SceneRegistrationError);

            // The other order: the client unlinks while the host still shares. sb_unlink removes the link on both sides, the host is told
            // (PeerUnlinked) and its seat stays shared; the client's seat is a local obstacle again.
            Assert.That(client.CanUnlink, Is.True, "the native library is core ABI 4.9 or newer");
            Assert.That(hostApp.PeerUnlinkedOf(hostLinked), Is.False);
            Assert.That(clientApp.TryUnshareAnchor(clientLinked), Is.True);
            PumpUntilNamed("step 6", () => hostApp.PeerUnlinkedOf(hostLinked), host, client);
            Assert.That(hostApp.ShareStateOf(hostLinked), Is.EqualTo(AnchorShareState.Shared), "still shared on the host");
            Assert.That(hostLinked.Anchor.GetComponent<SbMRUKAnchorEntity>().IsAttached(), Is.True);
            Assert.That(SpatialBlendAPI.FindElementBySharedId(client.ID, sharedId), Is.LessThanOrEqualTo(0));
            Assert.That(clientApp.ShareStateOf(clientLinked), Is.EqualTo(AnchorShareState.None));
            Pump(200, host, client);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
            Assert.That(client.SceneRegistrationError, Is.Null, client.SceneRegistrationError);
        }

        private sealed class RoomKeyCatalog : ISceneAnchorCatalog, IRoomScopedCatalog
        {
            private readonly ISceneAnchorCatalog _inner;
            public string Key = "room-a";
            public RoomKeyCatalog(ISceneAnchorCatalog inner) { _inner = inner; }
            public IReadOnlyList<SceneAnchorRecord> Anchors => _inner.Anchors;
            public string RoomKey => Key;
        }

        [Test]
        public void ARoomChangeWhileLinkedUnlinksTheSharersSeat() => RoomChangeWhileLinked(false);

        [Test]
        public void ARoomChangeUnlinksEvenWhenMrukAlreadyDestroyedTheAnchor() => RoomChangeWhileLinked(true);

        // The confirmed room changes while the client's seat is linked to the host's: the client's shares go with the old room, and the
        // link is dropped on both sides (sb_unlink, by the GameObject or, when MRUK already destroyed it, by the remembered element id),
        // so the host's seat stays shared and is told nothing in the other room holds it.
        private void RoomChangeWhileLinked(bool destroyTheAnchorFirst)
        {
            var host = CreateManager("rc-host");
            var client = CreateManager("rc-client");
            int port = FreePort();
            host.Host(port);
            AssertConnected(host);
            client.Connect(LoopbackHost, port);
            AssertConnected(client);
            Vector3 hostCenter = new Vector3(0f, 0.12f, 0f), hostSize = new Vector3(0.70f, 0.50f, 1.04f);
            Vector3 clientCenter = new Vector3(0f, 0f, 0.1f), clientSize = new Vector3(0.80f, 0.60f, 0.95f);
            SceneAnchorRecord hostSeat = SeatRecord(SeatAnchor("rc-host-seat", new Vector3(2f, 0.45f, 0f), 20f), 7301, "rc-h",
                HostSeatOutline, hostCenter, hostSize);
            SceneAnchorRecord clientSeat = SeatRecord(SeatAnchor("rc-client-seat", new Vector3(-6f, 0.58f, 1f), 75f), 7302, "rc-c",
                ClientSeatOutline, clientCenter, clientSize);
            RegisterSeatGeometry(host, hostSeat);
            RegisterSeatGeometry(client, clientSeat);
            var hostApp = ActiveApp("rc-host-app", Catalog(hostSeat));
            var clientApp = ActiveApp("rc-client-app", Catalog(clientSeat));
            var rooms = new RoomKeyCatalog(Catalog(clientSeat));
            clientApp.UseSceneAnchorCatalog(rooms);
            hostApp.UseSeatPreferences(new MemorySeatPreferences());
            clientApp.UseSeatPreferences(new MemorySeatPreferences());
            SetAppManager(hostApp, host);
            SetAppManager(clientApp, client);
            Invoke(host, "RegisterApp", hostApp);
            Invoke(client, "RegisterApp", clientApp);

            Assert.That(clientApp.ShareableAnchors().Count, Is.EqualTo(1));   // the scope is room-a
            Assert.That(hostApp.TryShareAnchor(hostSeat), Is.True);
            PumpUntilNamed("rc 1", () => clientApp.ShareStateOf(clientSeat) == AnchorShareState.Linked, host, client);
            PumpUntilNamed("rc 2", () => { clientApp.PollPendingSeatShares(); hostApp.PollPendingSeatShares(); return host.DeferredShareCount == 0 && client.DeferredShareCount == 0; }, host, client);
            Pump(200, host, client);
            clientApp.RememberLinkedElementIds();
            Assert.That(hostApp.PeerUnlinkedOf(hostSeat), Is.False);
            GameObject clientAnchor = clientSeat.Anchor;
            if (destroyTheAnchorFirst) UnityEngine.Object.DestroyImmediate(clientAnchor);   // MRUK took the old room's anchors away

            rooms.Key = "room-b";
            clientApp.ShareableAnchors();   // the room scope brings the app up to date: the old room's shares are released

            PumpUntilNamed("rc 3", () => hostApp.PeerUnlinkedOf(hostSeat), host, client);
            Assert.That(hostApp.ShareStateOf(hostSeat), Is.EqualTo(AnchorShareState.Shared), "still shared on the host");
            Assert.That(clientApp.SeatLinks.RoomKey, Is.EqualTo("room-b"));
            Assert.That(clientApp.ShareStateOf(clientSeat), Is.EqualTo(AnchorShareState.None));
            Pump(200, host, client);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
            Assert.That(client.SceneRegistrationError, Is.Null, client.SceneRegistrationError);
        }

        [Test]
        public void AConfirmedFrontSeatRegistersItsRegionAndAffordance()
        {
            // A confirmed front turns the part about the surface normal through transform16 of sb_add_sub_region. The core must accept it.
            var host = CreateManager("front-host");
            host.Host(FreePort());
            AssertConnected(host);
            SceneAnchorRecord seat = SeatRecord(SeatAnchor("front-seat", new Vector3(1f, 0.45f, 2f), 33f), 7201, "f1",
                HostSeatOutline, new Vector3(0f, 0.12f, 0f), new Vector3(0.70f, 0.50f, 1.04f));
            RegisterSeatGeometry(host, seat);
            var app = ActiveApp("front-app", Catalog(seat));
            app.UseSeatPreferences(new MemorySeatPreferences());
            SetAppManager(app, host);
            Invoke(host, "RegisterApp", app);
            Assert.That(app.TryConfirmSeatFront(seat, seat.Anchor.transform.TransformPoint(new Vector3(0.1f, 0.8f, 0f))), Is.True);
            Assert.That(app.TryRegisterSeat(seat, "1", false), Is.True);
            SbMRUKAnchorEntity entity = seat.Anchor.GetComponent<SbMRUKAnchorEntity>();
            PumpUntilNamed("step 7", () => entity.IsAttached(), host);
            Assert.That(entity.SeatFrontConfirmed, Is.True);
            Assert.That(host.SceneRegistrationError, Is.Null, host.SceneRegistrationError);
            // Only what the core reports is asserted: the registration is accepted and the affordance exists. The turned part's orientation
            // is checked by the EditMode oracle (SeatRegistrationTests); its pelvis yaw by the sit-transfer native fixture.
            Assert.That(SpatialBlendAPI.FindElementBySharedId(host.ID,
                SpatialAuthoringCompiler.AffordanceSharedId(SeatIdentity.SharedId("1"), 0)), Is.GreaterThan(0));
        }
    }
}
#endif
