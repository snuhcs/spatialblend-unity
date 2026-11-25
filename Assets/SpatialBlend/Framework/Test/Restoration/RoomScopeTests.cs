// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// State kept per room is scoped to the UUID of the room MRUK confirms: which room counts as current (a stale or
    /// fallback room does not), the seat links, and the confirmed seat fronts.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RoomScopeTests
    {
        private const string RoomA = "aaaaaaaa-0000-0000-0000-000000000000";
        private const string RoomB = "bbbbbbbb-0000-0000-0000-000000000000";

        // The tracker's pure rule, with strings standing for rooms.

        [Test]
        public void ARoomTheHeadsetIsNotInIsNotTheCurrentRoom()
        {
            // GetCurrentRoom() returns the last or the first room when the headset is in none: that is a stale room.
            Assert.That(MrukRoomTracker.Pick("staleOrFirst", RoomA, false, null, false), Is.Null);
        }

        [Test]
        public void ARoomTheHeadsetIsInIsTheCurrentRoom()
        {
            Assert.That(MrukRoomTracker.Pick("here", RoomA, true, "old", true), Is.EqualTo("here"));
        }

        [Test]
        public void TheLastConfirmedRoomStandsWhileTheHeadsetIsOutsideUntilMrukUnloadsIt()
        {
            Assert.That(MrukRoomTracker.Pick("fallbackB", RoomB, false, "heldA", true), Is.EqualTo("heldA"),
                "The fallback room never replaces the confirmed one.");
            Assert.That(MrukRoomTracker.Pick("fallbackB", RoomB, false, "heldA", false), Is.Null,
                "A room MRUK dropped is not held.");
            Assert.That(MrukRoomTracker.Pick<string>(null, string.Empty, false, "heldA", true), Is.EqualTo("heldA"));
        }

        [Test]
        public void ARoomWithNoDeviceUuidIsTakenAsMrukReportsIt()
        {
            // An editor prefab or JSON room is not a device space (MRUK gives it a random UUID, IsLocal is false): it has no key.
            Assert.That(MrukRoomTracker.Pick("prefabRoom", string.Empty, false, null, false), Is.EqualTo("prefabRoom"));
            Assert.That(MrukSceneAnchorCatalog.RoomUuid(null), Is.Empty);
        }

        // seat links

        [Test]
        public void SeatLinksDoNotLeakAcrossRooms()
        {
            var book = new SeatLinkBook();
            var seat = new GameObject("seat");
            try
            {
                book.EnterRoom(RoomA);
                book.SetLocal("couch", seat);
                book.RecordLink(SeatIdentity.SharedId("couch"), "couch", true, true);
                Assert.That(book.TryGetLocal("couch", out _), Is.True);
                Assert.That(book.EnterRoom(RoomA), Is.False, "The same room keeps its links.");
                Assert.That(book.TryGetLocal("couch", out _), Is.True);

                Assert.That(book.EnterRoom(RoomB), Is.True);
                Assert.That(book.TryGetLocal("couch", out _), Is.False);
                Assert.That(book.TryGetPair(SeatIdentity.SharedId("couch"), out _), Is.False);
                Assert.That(book.RoomKey, Is.EqualTo(RoomB));
            }
            finally { Object.DestroyImmediate(seat); }
        }

        [Test]
        public void AConfirmedSeatFrontIsRememberedPerRoom()
        {
            string anchor = "anchor-" + System.Guid.NewGuid().ToString("N");
            var prefs = new PlayerPrefsSeatPreferences();
            try
            {
                prefs.RoomScope = RoomA;
                prefs.SetFront(anchor, 37f);
                Assert.That(prefs.TryGetFront(anchor, out float front), Is.True);
                Assert.That(front, Is.EqualTo(37f));

                prefs.RoomScope = RoomB; // same anchor id, a different (re-scanned) room
                Assert.That(prefs.TryGetFront(anchor, out _), Is.False);

                prefs.RoomScope = RoomA;
                Assert.That(prefs.TryGetFront(anchor, out _), Is.True);
            }
            finally
            {
                PlayerPrefs.DeleteKey("spatialblend.seat.front." + RoomA + "." + anchor);
                PlayerPrefs.DeleteKey("spatialblend.seat.front." + RoomB + "." + anchor);
            }
        }

        // catalog

        [Test]
        public void TheMrukCatalogIsScopedToTheRoomItReads()
        {
            // Seat links and the shared table are dropped when this key changes (FreeformCollaborationApp.ScopeSeatsToRoom).
            Assert.That(new MrukSceneAnchorCatalog(), Is.InstanceOf<IRoomScopedCatalog>());
        }
    }
}
#endif
