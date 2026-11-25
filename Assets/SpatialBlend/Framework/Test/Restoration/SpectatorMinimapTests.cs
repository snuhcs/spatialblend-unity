// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The spectator's minimap (<see cref="SpectatorMinimap"/>, <see cref="SpectatorMinimapLayout"/>): the world (Unity, Y-up) to map
    /// mapping (X/Z ground, +Z up, one scale, centred, margin), a marker per participant with the person (filled) versus avatar (ring)
    /// style, the furniture classes (wall, shared real object), the alignment of the spectator camera onto a room, the M toggle and the
    /// Tab room switch, and the session receiving a participant's room snapshot.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SpectatorMinimapTests
    {
        private const float Side = SpectatorMinimapLayout.ReferenceSide;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private EventSystem[] _eventSystemsBefore;

        [SetUp]
        public void SetUp() => _eventSystemsBefore = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None);

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var system in UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
                if (!_eventSystemsBefore.Contains(system)) UnityEngine.Object.DestroyImmediate(system.gameObject);
        }

        private static Quaternion Yaw(float degrees) => Quaternion.Euler(0f, degrees, 0f);

        private static ParticipantRoomSnapshot Room(string owner, bool hasHead, Vector3 head, Quaternion headRotation,
                                                    IReadOnlyList<ObserverFurniture> furniture = null, IReadOnlyList<ObserverPlacedObject> objects = null,
                                                    IReadOnlyList<ObserverAvatarPose> avatars = null) =>
            ParticipantRoomSnapshot.Create(owner, null, 7, hasHead, head, headRotation,
                furniture ?? Array.Empty<ObserverFurniture>(), objects ?? Array.Empty<ObserverPlacedObject>(), avatars ?? Array.Empty<ObserverAvatarPose>());

        private static ObserverAvatarPose Avatar(string id, Vector3 head, float yaw) =>
            new ObserverAvatarPose(id, "Participant " + id, new Vector3(head.x, 0f, head.z), Yaw(yaw), true, head, Yaw(yaw));

        // ---- mapping ----

        [Test]
        public void FitKeepsTheAspectCentresTheShortAxisAndLeavesTheMargin()
        {
            var layout = new SpectatorMinimapLayout();
            layout.Fit(new Vector2(-2f, -1.5f), new Vector2(4f, 1.5f), Side);   // 6 m by 3 m
            float scale = Side * (1f - 2f * SpectatorMinimapLayout.MarginFraction) / 6f;
            Assert.That(layout.Scale, Is.EqualTo(scale).Within(1e-4f), "one scale, from the longer side");
            Vector2 min = layout.ToPanel(new Vector2(-2f, -1.5f)), max = layout.ToPanel(new Vector2(4f, 1.5f));
            Assert.That(min.x, Is.EqualTo(Side * SpectatorMinimapLayout.MarginFraction).Within(1e-3f), "the margin on the long axis");
            Assert.That(max.x, Is.EqualTo(Side * (1f - SpectatorMinimapLayout.MarginFraction)).Within(1e-3f));
            Assert.That((min.y + max.y) * 0.5f, Is.EqualTo(Side * 0.5f).Within(1e-3f), "the short axis is centred");
            Assert.That((max.x - min.x) / (max.y - min.y), Is.EqualTo(2f).Within(1e-3f), "the aspect ratio is kept");
        }

        [Test]
        public void WorldIsYUpAndTheMapShowsXRightAndZUp()
        {
            var layout = new SpectatorMinimapLayout();
            layout.Fit(new Vector2(-5f, -5f), new Vector2(5f, 5f), Side);
            Vector2 origin = layout.ToPanel(new Vector3(0f, 0f, 0f));
            Assert.That(layout.ToPanel(new Vector3(0f, 2.5f, 0f)), Is.EqualTo(origin), "height does not move a point on the map");
            Assert.That(layout.ToPanel(new Vector3(1f, 0f, 0f)).x, Is.GreaterThan(origin.x), "world +X is right");
            Assert.That(layout.ToPanel(new Vector3(0f, 0f, 1f)).y, Is.GreaterThan(origin.y), "world +Z is up");
            Assert.That(SpectatorMinimapLayout.Heading(Yaw(90f)).x, Is.EqualTo(1f).Within(1e-5f), "a 90 degree yaw faces right (+X)");
            Assert.That(SpectatorMinimapLayout.Heading(Quaternion.Euler(-90f, 0f, 0f)), Is.EqualTo(Vector2.zero), "straight up has no heading");
        }

        [Test]
        public void ATinyRoomIsNotBlownUpToThePanel()
        {
            var layout = new SpectatorMinimapLayout();
            layout.Fit(Vector2.zero, Vector2.zero, Side);
            Assert.That(layout.Scale, Is.EqualTo(Side * (1f - 2f * SpectatorMinimapLayout.MarginFraction) / SpectatorMinimapLayout.MinimumSpanMeters).Within(1e-4f));
            Assert.That(layout.ToPanel(Vector2.zero), Is.EqualTo(new Vector2(Side * 0.5f, Side * 0.5f)), "a lone point is centred");
        }

        [Test]
        public void TheRoomIsFittedFromItsFurnitureAndItsPeople()
        {
            var walls = new[]
            {
                // An MRUK wall: a plane (no depth) standing up, its normal horizontal.
                new ObserverFurniture("w1", "WALL_FACE", new Vector3(0f, 1.25f, 3f), Quaternion.Euler(0f, 180f, 0f), new Vector3(6f, 2.5f, 0f)),
                new ObserverFurniture("w2", "WALL_FACE", new Vector3(0f, 1.25f, -3f), Quaternion.identity, new Vector3(6f, 2.5f, 0f)),
            };
            var layout = new SpectatorMinimapLayout();
            Assert.That(layout.Build(Room("1", true, new Vector3(0f, 1.6f, 0f), Quaternion.identity, walls), Side), Is.True);
            Vector2 a = layout.ToPanel(new Vector2(-3f, -3f)), b = layout.ToPanel(new Vector2(3f, 3f));
            Assert.That(a.x, Is.EqualTo(Side * SpectatorMinimapLayout.MarginFraction).Within(0.01f), "the walls span the panel");
            Assert.That(b.y, Is.EqualTo(Side * (1f - SpectatorMinimapLayout.MarginFraction)).Within(0.01f));
            var wall = layout.Footprints.Where(f => f.Kind == MinimapFootprintKind.Wall).ToArray();
            Assert.That(wall.Length, Is.EqualTo(2), "both walls are drawn as walls");
            Assert.That(Vector2.Distance(wall[0].A, wall[0].D), Is.LessThan(0.01f), "a wall is a line (no depth) on the map");
            Assert.That(Vector2.Distance(wall[0].A, wall[0].B), Is.EqualTo(6f * layout.Scale).Within(0.05f), "its width is the wall's");
        }

        [Test]
        public void NoRoomLaysOutNothing()
        {
            var layout = new SpectatorMinimapLayout();
            Assert.That(layout.Build(null, Side), Is.False);
            Assert.That(layout.HasRoom, Is.False);
            Assert.That(layout.Markers, Is.Empty);
            Assert.That(layout.Footprints, Is.Empty);
        }

        // ---- markers ----

        [Test]
        public void TheOwnerIsAFilledPersonAndEveryOtherAvatarIsARing()
        {
            var avatars = new[] { Avatar("1", new Vector3(0.1f, 1.6f, 0f), 0f), Avatar("2", new Vector3(2f, 1.6f, 1f), 90f), Avatar("3", new Vector3(-2f, 1.6f, -1f), 180f) };
            var layout = new SpectatorMinimapLayout();
            layout.Build(Room("1", true, new Vector3(0f, 1.6f, 0f), Quaternion.identity, avatars: avatars), Side);
            Assert.That(layout.Markers.Count, Is.EqualTo(3), "one marker per participant: the owner's own avatar is the person, not a second marker");
            var person = layout.Markers.Single(m => m.ParticipantId == "1");
            Assert.That(person.Kind, Is.EqualTo(MinimapMarkerKind.Person));
            Assert.That(person.Position, Is.EqualTo(layout.ToPanel(new Vector3(0f, 1.6f, 0f))), "the person is at their headset");
            Assert.That(layout.Markers.Where(m => m.ParticipantId != "1").All(m => m.Kind == MinimapMarkerKind.Avatar), Is.True);
            var p2 = layout.Markers.Single(m => m.ParticipantId == "2");
            Assert.That(p2.Heading.x, Is.EqualTo(1f).Within(1e-4f), "the heading tick follows the head");
            Assert.That(p2.Label, Is.EqualTo("P2"));
            Assert.That(layout.Markers.Select(m => m.Color).Distinct().Count(), Is.EqualTo(3), "a distinct colour per participant");
            Assert.That(SpectatorMinimapLayout.ParticipantColor("9"), Is.EqualTo(SpectatorMinimapLayout.ParticipantColor("1")), "the palette wraps by id modulo its size");
            Assert.That(SpectatorMinimapLayout.ParticipantColor("2"), Is.EqualTo(p2.Color), "the same colour for the same participant in every room");
        }

        [Test]
        public void WithoutAHeadsetSampleTheOwnersAvatarIsThePerson()
        {
            var layout = new SpectatorMinimapLayout();
            layout.Build(Room("1", false, Vector3.zero, Quaternion.identity, avatars: new[] { Avatar("1", new Vector3(1f, 1.6f, 1f), 0f) }), Side);
            Assert.That(layout.Markers.Count, Is.EqualTo(1));
            Assert.That(layout.Markers[0].Kind, Is.EqualTo(MinimapMarkerKind.Person));
        }

        [Test]
        public void SharedFurnitureAndPlacedObjectsAreTheirOwnClasses()
        {
            var furniture = new[]
            {
                new ObserverFurniture("tbl-shared", "TABLE", new Vector3(1f, 0.4f, 1f), Quaternion.identity, new Vector3(1.2f, 0.8f, 0.6f)),
                new ObserverFurniture("tbl-own", "TABLE", new Vector3(-1f, 0.4f, 1f), Quaternion.identity, new Vector3(1.2f, 0.8f, 0.6f)),
                new ObserverFurniture("floor", "FLOOR", Vector3.zero, Quaternion.Euler(-90f, 0f, 0f), new Vector3(5f, 4f, 0f)),
                new ObserverFurniture("ceiling", "CEILING", new Vector3(0f, 2.5f, 0f), Quaternion.Euler(90f, 0f, 0f), new Vector3(5f, 4f, 0f)),
            };
            var objects = new[] { new ObserverPlacedObject("net:5", new Vector3(0f, 0.5f, -1f), Quaternion.identity), new ObserverPlacedObject("net:9", Vector3.zero, Quaternion.identity) };
            var layout = new SpectatorMinimapLayout();
            layout.Build(Room("1", true, Vector3.zero, Quaternion.identity, furniture, objects), Side, f => f.Id == "tbl-shared", key => key == "net:5" ? "Chess" : null);
            var kinds = layout.Footprints.Select(f => f.Kind).ToArray();
            Assert.That(kinds[0], Is.EqualTo(MinimapFootprintKind.Floor), "the floor is drawn first");
            Assert.That(kinds.Count(k => k == MinimapFootprintKind.Floor), Is.EqualTo(1), "the ceiling is left out (it would cover the room)");
            Assert.That(kinds.Count(k => k == MinimapFootprintKind.SharedFurniture), Is.EqualTo(1), "the shared table, in the shared-real violet");
            Assert.That(kinds.Count(k => k == MinimapFootprintKind.Furniture), Is.EqualTo(1));
            var chess = layout.Footprints.Single(f => f.Kind == MinimapFootprintKind.VirtualObject);
            Assert.That(chess.Label, Is.EqualTo("Chess"), "an object the spectator cannot name is left out");
            var table = layout.Footprints.First(f => f.Kind == MinimapFootprintKind.SharedFurniture);
            Assert.That(Vector2.Distance(table.A, table.B), Is.EqualTo(1.2f * layout.Scale).Within(0.01f), "a table's footprint is its top (width by depth)");
            Assert.That(Vector2.Distance(table.B, table.C), Is.EqualTo(0.6f * layout.Scale).Within(0.01f));
        }

        [Test]
        public void ASharedObjectIsItsSharersKeyOrTheLinkingRoomsSameMeasure()
        {
            var size = new Vector3(1.81f, 0.8f, 1.06f);
            var own = new ObserverFurniture("key-A", "TABLE", Vector3.zero, Quaternion.identity, new Vector3(1.2f, 0.7f, 0.6f));
            Assert.That(SpectatorMinimapLayout.MatchesShared(own, "TABLE", "key-A", size), Is.True, "the sharer's own anchor carries the key");
            var linked = new ObserverFurniture("key-B", "TABLE", Vector3.zero, Quaternion.identity, new Vector3(1.06f, 0.81f, 1.80f));
            Assert.That(SpectatorMinimapLayout.MatchesShared(linked, "TABLE", "key-A", size), Is.True, "the linking room's anchor: same label, same measure (any order)");
            var other = new ObserverFurniture("key-C", "TABLE", Vector3.zero, Quaternion.identity, new Vector3(1.2f, 0.7f, 0.6f));
            Assert.That(SpectatorMinimapLayout.MatchesShared(other, "TABLE", "key-A", size), Is.False, "another table");
            var couch = new ObserverFurniture("key-D", "COUCH", Vector3.zero, Quaternion.identity, size);
            Assert.That(SpectatorMinimapLayout.MatchesShared(couch, "TABLE", "key-A", size), Is.False, "another label");
        }

        [Test]
        public void EveryParticipantColourKeepsClearOfTheSharedVioletAndTheAccent()
        {
            Color.RGBToHSV(UiTheme.Palette.SharedReal, out float sharedHue, out _, out _);
            Color.RGBToHSV(UiTheme.Palette.Accent, out float accentHue, out _, out _);
            Assert.That(SpectatorMinimapLayout.ParticipantPalette.Length, Is.InRange(6, 10));
            Assert.That(SpectatorMinimapLayout.ParticipantPalette.Distinct().Count(), Is.EqualTo(SpectatorMinimapLayout.ParticipantPalette.Length));
            foreach (Color c in SpectatorMinimapLayout.ParticipantPalette)
            {
                Color.RGBToHSV(c, out float h, out float sat, out _);
                if (sat < 0.15f) continue;   // white: no hue to confuse
                Assert.That(HueDistance(h, sharedHue), Is.GreaterThanOrEqualTo(30f), $"{c} vs the shared-real violet");
                Assert.That(HueDistance(h, accentHue), Is.GreaterThanOrEqualTo(30f), $"{c} vs the accent");
            }
        }

        private static float HueDistance(float a, float b)
        {
            float d = Mathf.Abs(a - b) * 360f % 360f;
            return d > 180f ? 360f - d : d;
        }

        [Test]
        public void RoomsAreOrderedByPlayerIdAsANumber()
        {
            var rooms = new List<ParticipantRoomSnapshot> { Room("10", true, Vector3.zero, Quaternion.identity), Room("2", true, Vector3.zero, Quaternion.identity), Room("x", true, Vector3.zero, Quaternion.identity) };
            rooms.Sort(SpectatorMinimapLayout.CompareRooms);
            Assert.That(rooms.Select(r => r.ParticipantId), Is.EqualTo(new[] { "2", "10", "x" }));
            Assert.That(ReferenceEquals(SpectatorMinimapLayout.ParticipantLabel("2"), SpectatorMinimapLayout.ParticipantLabel("2")), Is.True, "a label is made once per id");
        }

        [Test]
        public void ASharedSceneAnchorGetsNoGrayMarkerTheRoomViewDrawsIt()
        {
            var host = new GameObject("spectator-app");
            _objects.Add(host);
            var app = host.AddComponent<FreeformSpectatorApp>();
            var table = new GameObject(SharedAnchorName.Compose(SharedAnchorName.Head("TABLE", "k1"), Vector3.zero, new Vector3(1.8f, 0.8f, 1.0f), null));
            var plain = new GameObject("no-geometry");
            _objects.Add(table); _objects.Add(plain);
            app.OnObjectAdded(table, SbApp.OnObjectAddedType.Added);
            app.OnObjectAdded(plain, SbApp.OnObjectAddedType.Added);
            Assert.That(FreeformSpectatorApp.IsSharedSceneAnchor(table), Is.True);
            Assert.That(table.transform.Find("SpectatorMarker"), Is.Null, "no 15 cm cube at a meaningless pose");
            Assert.That(plain.transform.Find("SpectatorMarker"), Is.Not.Null, "other geometry-free content still gets its marker");
            Assert.That(app.ReceivedObjects, Does.Contain(table), "it is still received (the minimap and room view read it)");
        }

        // ---- the camera wedge ----

        [Test]
        public void TheSpectatorWorldIsAlignedOnACommonObject()
        {
            // The object is at (1, 1) facing +Z in the spectator's scene and at (5, 5) facing +X in the room.
            Assert.That(SpectatorMinimapLayout.TryAlign(new Vector3(1f, 0f, 1f), Quaternion.identity, new Vector3(5f, 0f, 5f), Yaw(90f), out GroundAlignment align), Is.True);
            Vector2 p = align.Point(new Vector2(1f, 2f));   // one metre in front of it in the spectator's scene
            Assert.That(p.x, Is.EqualTo(6f).Within(1e-4f), "one metre in front of it in the room, too");
            Assert.That(p.y, Is.EqualTo(5f).Within(1e-4f));
            Vector2 forward = align.Rotate(Vector2.up);
            Assert.That(forward.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(SpectatorMinimapLayout.TryAlign(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f), Vector3.zero, Quaternion.identity, out _), Is.False,
                "an object with no horizontal facing cannot align");
        }

        [Test]
        public void ThePhotonRegionIsTheFlagTheEnvironmentTheProjectsFixedRegionOrTheBestRegion()
        {
            string Env(string name) => null;
            Assert.That(SpectatorJoinPolicy.ResolveRegion(new[] { "app" }, Env, "", out string source), Is.EqualTo(SpectatorJoinPolicy.BestRegion));
            Assert.That(source, Is.EqualTo("best"), "no region anywhere: Photon's best region, as the participants pick it");
            Assert.That(SpectatorJoinPolicy.ResolveRegion(new[] { "app" }, Env, null, out source), Is.Empty);
            Assert.That(SpectatorJoinPolicy.ResolveRegion(new[] { "app" }, Env, "jp", out source), Is.EqualTo("jp"));
            Assert.That(source, Is.EqualTo("project"));
            Assert.That(SpectatorJoinPolicy.ResolveRegion(new[] { "app" }, n => n == SpectatorJoinPolicy.RegionEnvironmentVariable ? "EU" : null, "jp", out source), Is.EqualTo("eu"));
            Assert.That(source, Is.EqualTo("environment"));
            Assert.That(SpectatorJoinPolicy.ResolveRegion(new[] { "app", SpectatorJoinPolicy.RegionFlag, "us" }, n => "eu", "jp", out source), Is.EqualTo("us"));
            Assert.That(source, Is.EqualTo("flag"));
            Assert.That(SpectatorJoinPolicy.ResolveRegion(new[] { "app", SpectatorJoinPolicy.RegionFlag, "-batchmode" }, Env, "", out _), Is.EqualTo(SpectatorJoinPolicy.BestRegion), "a flag-like value is not a region");
            Assert.That(SpectatorJoinPolicy.RegionHint("kr"), Does.Contain("'kr'").And.Contain(SpectatorJoinPolicy.RegionFlag), "the not-found error says which region and how to change it");
            Assert.That(SpectatorJoinPolicy.RegionHint(SpectatorJoinPolicy.BestRegion), Does.Contain(SpectatorJoinPolicy.BestRegionLabel));
            Assert.That(SpectatorJoinPolicy.RegionLabel("kr"), Is.EqualTo("kr"));
        }

        // ---- the component: toggle and room switch ----

        private (FreeformSpectatorSessionTests.TestSession session, SpectatorMinimap map) NewMinimap()
        {
            var go = new GameObject("spectator-session");
            _objects.Add(go);
            var connection = go.AddComponent<FreeformSpectatorSessionTests.TestConnection>();
            var session = go.AddComponent<FreeformSpectatorSessionTests.TestSession>();
            session.Configure(null, connection, null, null);
            var overlay = SpectatorOverlay.Create(session, UiFonts.Set);
            _objects.Add(overlay.gameObject);
            var map = SpectatorMinimap.Create(session, overlay.transform);
            SetPhase(session, SpectatorPhase.Spectating);
            map.ApplyShown();
            return (session, map);
        }

        private static void SetPhase(FreeformSpectatorSession session, SpectatorPhase phase) =>
            typeof(FreeformSpectatorSession).GetProperty("Phase").GetSetMethod(true).Invoke(session, new object[] { phase });

        [Test]
        public void TheMapIsShownOnlyWhileSpectating()
        {
            var (session, map) = NewMinimap();
            Assert.That(map.Shown, Is.True, "spectating and toggled on");
            foreach (var phase in new[] { SpectatorPhase.Idle, SpectatorPhase.Searching, SpectatorPhase.Joining, SpectatorPhase.Failed })
            {
                SetPhase(session, phase);
                map.ApplyShown();
                Assert.That(map.Shown, Is.False, phase + ": no room to map");
                Assert.That(map.Visible, Is.True, "the user's toggle is kept");
            }
            SetPhase(session, SpectatorPhase.Spectating);
            map.ApplyShown();
            Assert.That(map.Shown, Is.True);
        }

        [Test]
        public void MHidesAndShowsTheMap()
        {
            var (_, map) = NewMinimap();
            Assert.That(map.Visible, Is.True, "the map starts shown");
            Assert.That(map.Shown, Is.True);
            map.HandleKeys(toggle: true, nextRoom: false);
            Assert.That(map.Visible, Is.False);
            Assert.That(map.Shown, Is.False);
            map.HandleKeys(toggle: true, nextRoom: false);
            Assert.That(map.Visible && map.Shown, Is.True);
            Assert.That(SpectatorMinimap.ToggleKey, Is.EqualTo(KeyCode.M));
        }

        [Test]
        public void ThePanelSitsInTheBottomRightCornerAtAQuarterOfTheHeight()
        {
            var (_, map) = NewMinimap();
            Assert.That(map.Panel.anchorMin, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(map.Panel.anchorMax, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(map.Panel.pivot, Is.EqualTo(new Vector2(1f, 0f)));
            float fraction = map.Panel.sizeDelta.x / SpectatorOverlay.ReferenceResolution.y;
            Assert.That(fraction, Is.InRange(0.22f, 0.25f));
        }

        [Test]
        public void TheSessionReceivesRoomsAndTabSwitchesBetweenThem()
        {
            var (session, map) = NewMinimap();
            map.Refresh();
            Assert.That(map.Title, Is.EqualTo(SpectatorMinimap.NoRoomText));
            Assert.That(map.WaitingStatus, Does.Contain("Waiting for the participants' rooms").And.Contain("region " + session.RegionLabel),
                "an empty map says what it waits for, never a dark square");
            Assert.That(map.Layout.HasRoom, Is.False);
            Fusion.PlayerRef p1 = Fusion.PlayerRef.FromEncoded(5), p2 = Fusion.PlayerRef.FromEncoded(6);
            string id1 = ObserverSceneMap.ParticipantId(p1.PlayerId), id2 = ObserverSceneMap.ParticipantId(p2.PlayerId);
            byte[] a = ObserverRoomCodec.Write(Room(id1, true, new Vector3(0f, 1.6f, 0f), Quaternion.identity, avatars: new[] { Avatar(id2, new Vector3(1f, 1.6f, 0f), 0f) }));
            byte[] b = ObserverRoomCodec.Write(Room(id2, true, new Vector3(3f, 1.6f, 3f), Quaternion.identity, avatars: new[] { Avatar(id1, new Vector3(2f, 1.6f, 3f), 0f) }));
            // Fusion's reliable-data callback does not name the publisher in Shared mode: a snapshot is kept whatever player the callback
            // gives (a sender check would drop every snapshot and leave the minimap black).
            Assert.That(session.ReceiveRoomSnapshot(Fusion.PlayerRef.None, new ArraySegment<byte>(a)), Is.True);
            Assert.That(session.ReceiveRoomSnapshot(p2, new ArraySegment<byte>(b)), Is.True);
            Assert.That(session.ReceiveRoomSnapshot(p1, new ArraySegment<byte>(new byte[] { 1, 2, 3 })), Is.False, "a malformed snapshot is dropped");
            map.Refresh();
            Assert.That(map.WaitingStatus, Is.Null, "the status goes once a room is drawn");
            string first = session.Rooms.Rooms.SelectedParticipantId;
            Assert.That(map.Layout.HasRoom, Is.True);
            Assert.That(map.Layout.Markers.Single(m => m.Kind == MinimapMarkerKind.Person).ParticipantId, Is.EqualTo(first), "the room's owner is the person");
            Assert.That(map.Title, Does.Contain("1/2"));
            map.HandleKeys(toggle: false, nextRoom: true);
            map.Refresh();
            Assert.That(session.Rooms.Rooms.SelectedParticipantId, Is.Not.EqualTo(first), "Tab shows the other room");
            Assert.That(map.Layout.Markers.Single(m => m.Kind == MinimapMarkerKind.Person).ParticipantId, Is.EqualTo(session.Rooms.Rooms.SelectedParticipantId));
            Assert.That(map.Title, Does.Contain("2/2"));
            map.HandleKeys(toggle: false, nextRoom: true);
            Assert.That(session.Rooms.Rooms.SelectedParticipantId, Is.EqualTo(first), "and back");
        }
    }
}
#endif
