// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Sit transfer: a MRUK COUCH anchor with a measured
    /// plane is a shared Sittable interaction entity registered through the table's path, linked to the user's chosen local seat
    /// when it is shared, with a front the user confirms once and a pelvis relation the app declares. No test here uses a
    /// native library. The shapes below are TEST DATA (a trapezoid seat with a backrest volume, so no test passes by a rectangle's
    /// symmetry); the assertions are derived from them, not from constants.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SeatRegistrationTests
    {
        // A trapezoid seat plane in the MRUK anchor's local XY (counter-clockwise from +z), metres.
        private static readonly Vector2[] SeatOutline =
        {
            new Vector2(-0.30f, -0.22f), new Vector2(0.34f, -0.22f), new Vector2(0.26f, 0.21f), new Vector2(-0.22f, 0.21f),
        };
        // The anchor's volume in its local frame: the box from the floor (local z = -0.45) over the seat to the top of the backrest.
        private static readonly Vector3 VolumeCenter = new Vector3(0.02f, 0f, 0.07f);
        private static readonly Vector3 VolumeSize = new Vector3(0.70f, 0.50f, 1.04f);

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void Destroy()
        {
            for (int i = _objects.Count - 1; i >= 0; --i) if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        // An MRUK-style anchor: local +Z is the plane normal, so the anchor stands at Rx(-90) with its origin on the seat.
        private GameObject Anchor(string name, Vector3 position, float yawDegrees = 0f)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f) * Quaternion.Euler(-90f, 0f, 0f));
            return go;
        }

        private static SceneAnchorRecord Record(GameObject anchor, string anchorId, Vector2[] outline, string label = "COUCH") =>
            new SceneAnchorRecord
            {
                Label = label, Anchor = anchor, AnchorId = anchorId, SourceKey = anchor.GetInstanceID(), Size = 1f,
                LocalCenter = VolumeCenter, LocalSize = VolumeSize, PlaneBoundary = outline,
            };

        private static SpatialAuthoringPlan Compile(SbSpatialAuthoring authoring)
        {
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error), Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error), Is.True, error);
            return plan;
        }

        private static Vector2[] ChartOf(Vector2[] outline) => SceneGeometryChart.PlanePolygon(outline);

        // ------------------------------------------------------------------ registration at the measured plane

        [Test]
        public void MrukCouchRegistersASharedSittableSeatAtItsMeasuredPlane()
        {
            GameObject anchor = Anchor("couch", new Vector3(2.5f, 0.43f, -1.2f), 35f);
            var entity = anchor.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel("COUCH");
            Assert.That(entity.ApplySeatSupport(VolumeCenter, VolumeSize, SeatOutline, "1", null), Is.True);

            SbSpatialAuthoring authoring = anchor.GetComponent<SbSpatialAuthoring>();
            Assert.That(authoring, Is.Not.Null, "the seat goes through the table's path: an SbSpatialAuthoring declaration");
            SpatialAuthoringPlan plan = Compile(authoring);

            SpatialElementMapping seat = plan.Mappings.Single(m => m.role == SpatialElementRole.Surface);
            Assert.That(seat.tag, Is.EqualTo("Sittable"));
            Assert.That(plan.Mappings.Length, Is.EqualTo(3), "region, object and exactly one Sittable surface");

            // The seat polygon is the measured outline, vertex for vertex (the chart basis change, no re-fit to a rectangle) ...
            Assert.That(seat.surface.Length, Is.EqualTo(SeatOutline.Length));
            Vector2[] chart = ChartOf(SeatOutline);
            for (int i = 0; i < chart.Length; ++i)
            {
                Assert.That(seat.surface[i].x, Is.EqualTo(chart[i].x).Within(1e-6f));
                Assert.That(seat.surface[i].z, Is.EqualTo(chart[i].y).Within(1e-6f));
            }
            // ... and every vertex stands at the anchor's plane in the world, whatever yaw the anchor was scanned at.
            Matrix4x4 frameWorld = anchor.transform.localToWorldMatrix * plan.FrameToObject;
            foreach (Vector3 vertex in seat.surface)
            {
                Vector3 world = frameWorld.MultiplyPoint3x4(vertex);
                Assert.That(world.y, Is.EqualTo(0.43f).Within(1e-5f), "seat height = the anchor plane");
            }
            // Each outline vertex lands where the anchor itself puts it (independent oracle: the anchor's own transform).
            foreach (Vector2 p in SeatOutline)
            {
                Vector3 expected = anchor.transform.TransformPoint(new Vector3(p.x, p.y, 0f));
                Assert.That(seat.surface.Any(v => Vector3.Distance(frameWorld.MultiplyPoint3x4(v), expected) < 1e-4f), Is.True, p.ToString());
            }

            // The part: one, named seat, covering the whole surface.
            Assert.That(seat.subRegions, Has.Length.EqualTo(1));
            Assert.That(seat.subRegions[0].name, Is.EqualTo("seat"));
            Assert.That(seat.subRegions[0].tag, Is.EqualTo("seat"));
            Assert.That(seat.subRegions[0].localPolygon.Length, Is.EqualTo(chart.Length));
            for (int i = 0; i < chart.Length; ++i)
                Assert.That(Vector2.Distance(seat.subRegions[0].localPolygon[i], chart[i]), Is.LessThan(1e-6f));
            Assert.That(seat.subRegions[0].rotationDegrees, Is.EqualTo(0f), "no confirmed front: the plane's own frame");

            // Shared ids: the seat is its own object (label + link key), and the region and the surface derive from it.
            long id = SeatIdentity.SharedId("1");
            Assert.That(ResolveSharedId(anchor), Is.EqualTo(id));
            Assert.That(SeatIdentity.SharedId("2"), Is.Not.EqualTo(id), "two seats of one room are two objects");
            Assert.That(SpatialAuthoringCompiler.AffordanceSharedId(id, 0), Is.Not.EqualTo(SpatialAuthoringCompiler.RegionSharedId(id)));
            Assert.That(authoring.correspondenceKey, Is.EqualTo("1"), "the user's link replaces the table's fixed correspondence key");
        }

        [Test]
        public void TheSeatSurfaceStaysInsideItsRegionAndTheRegionUsesTheAuthoringStandOff()
        {
            // A plane that sticks out of the volume's footprint widens the footprint: the surface must stand inside its object.
            Vector2[] wide = SeatOutline.Select(p => new Vector2(p.x * 2f, p.y)).ToArray();
            Assert.That(SceneAnchorSpatialAuthoring.TryBuildSeatGeometry(VolumeCenter, VolumeSize, wide, null,
                out Vector2[] footprint, out Vector2[] extent, out Vector3[] surface, out _), Is.True);
            float stand = SceneAnchorSpatialAuthoring.StandOffMeters;
            Assert.That(footprint.Max(p => p.x) - footprint.Min(p => p.x), Is.GreaterThanOrEqualTo(surface.Max(v => v.x) - surface.Min(v => v.x) - 1e-5f));
            Assert.That(extent.Max(p => p.x) - extent.Min(p => p.x),
                Is.EqualTo(footprint.Max(p => p.x) - footprint.Min(p => p.x) + 2f * stand).Within(1e-4f));
            foreach (Vector3 v in surface)
            {
                Assert.That(v.x, Is.InRange(footprint.Min(p => p.x) - 1e-5f, footprint.Max(p => p.x) + 1e-5f));
                Assert.That(v.z, Is.InRange(footprint.Min(p => p.y) - 1e-5f, footprint.Max(p => p.y) + 1e-5f));
            }
        }

        [Test]
        public void ACouchWithoutAPlaneIsNotRegisteredAsSittable()
        {
            GameObject anchor = Anchor("couch-no-plane", Vector3.zero);
            var noPlane = Record(anchor, "no-plane", null);
            Assert.That(FreeformCollaborationApp.IsSeatRecord(noPlane), Is.False);
            Assert.That(FreeformCollaborationApp.IsSeatRecord(Record(anchor, "tiny", new[] { Vector2.zero, Vector2.right })), Is.False);
            Assert.That(FreeformCollaborationApp.IsSeatRecord(Record(anchor, "table", SeatOutline, "TABLE")), Is.False,
                "a table is not a seat");

            var app = App(new ListSceneAnchorCatalog(), out var published);
            Assert.That(app.TryRegisterSeat(noPlane, "1", true), Is.False);
            Assert.That(anchor.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "no seat entity, no guessed height");
            Assert.That(published.Anchor, Is.Null);
            Assert.That(SceneAnchorSpatialAuthoring.ApplySeat(anchor, VolumeCenter, VolumeSize, null, "1", null), Is.Null);
            Assert.That(SceneAnchorSpatialAuthoring.ApplySeat(anchor, VolumeCenter, VolumeSize,
                new[] { Vector2.zero, Vector2.right, 2f * Vector2.right }, "1", null), Is.Null, "a degenerate outline has no area");
        }

        // ------------------------------------------------------------------ linking

        [Test]
        public void ASharedSeatLinksToTheRoomsSimilarSeatByTheSharersKeyAndNeverToTheNearestChair()
        {
            // Room 2 has a near seat much smaller than the shared one (a stool-sized couch) and a far one of about its size.
            GameObject near = Anchor("near-couch", new Vector3(0.2f, 0.45f, 0f));
            GameObject far = Anchor("far-couch", new Vector3(4.0f, 0.55f, 3f));
            var catalog = new ListSceneAnchorCatalog();
            SceneAnchorRecord small = Record(near, "uuid-near", SeatOutline);
            small.LocalSize = new Vector3(0.4f, 0.3f, 0.5f);
            catalog.Add(small);
            catalog.Add(Record(far, "uuid-far", SeatOutline));
            FreeformCollaborationApp app = App(catalog, out var published);

            // The share of the source's seat arrives (a replica next to the NEAR seat); it carries the sharer's key and measured box.
            GameObject replica = Replica("7", frontConfirmed: false);
            replica.transform.position = near.transform.position;
            Assert.That(app.OnShare(replica).result, Is.EqualTo(OnShareResponse.Result.Defer), "the closest-sized seat is registering");
            Assert.That(published.Anchor, Is.SameAs(far), "the largest free seat (here also the one closest in size), not the nearest chair");
            Assert.That(near.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "the other seat is not registered or linked");
            Assert.That(app.ShareStateOf(catalog.Anchors[1]), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(app.PendingSeatKeys, Is.EquivalentTo(new[] { "7" }));
            MarkAttached(far);
            GameObject again = Replica("7", frontConfirmed: false);
            OnShareResponse linked = app.OnShare(again);
            Assert.That(linked.result, Is.EqualTo(OnShareResponse.Result.LinkToExisting));
            Assert.That(linked.gameObject, Is.SameAs(far));
            Assert.That(app.SeatLinks.TryGetPair(SeatIdentity.SharedId("7"), out var pair), Is.True);
            Assert.That(pair.FrontConfirmedOnBoth, Is.False, "neither front is confirmed yet: Facing decides the yaw");

            // The next shared seat links to the room's remaining COUCH, whatever its size (link by label).
            OnShareResponse other = app.OnShare(Replica("8", false));
            Assert.That(other.result, Is.EqualTo(OnShareResponse.Result.Defer), "the small near seat is registering under key 8");
            Assert.That(published.Anchor, Is.SameAs(near));
            Assert.That(app.ShareStateOf(catalog.Anchors[0]), Is.EqualTo(AnchorShareState.Linked));
            Assert.That(app.PendingSeatKeys, Is.EquivalentTo(new[] { "8" }), "the link of seat 7 completed; seat 8 waits for its anchor");
            Assert.That(app.SeatLinks.FrontConfirmed(SeatIdentity.SharedId("8")), Is.False);

            // A third: every COUCH of this room is taken, so it arrives as a box to install (nothing is refused).
            Assert.That(app.OnShare(Replica("9", false)).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
        }

        [Test]
        public void ALinkKeyNamesOneSeatAndAnAttachedSeatKeepsIt()
        {
            GameObject a = Anchor("a", new Vector3(0f, 0.45f, 0f));
            GameObject b = Anchor("b", new Vector3(3f, 0.45f, 0f));
            var catalog = new ListSceneAnchorCatalog();
            catalog.Add(Record(a, "ua", SeatOutline));
            catalog.Add(Record(b, "ub", SeatOutline));
            FreeformCollaborationApp app = App(catalog, out _);
            Assert.That(app.TryRegisterSeat(catalog.Anchors[0], "1", true), Is.True);
            Assert.That(app.SeatLinks.TryGetLocal("1", out GameObject holder), Is.True);
            Assert.That(holder, Is.SameAs(a));
            Assert.That(SeatIdentity.IsValidKey("a#b"), Is.False);
            Assert.That(app.TryRegisterSeat(catalog.Anchors[1], "a#b", true), Is.False);
        }

        // ------------------------------------------------------------------ the front

        [Test]
        public void TheFrontIsConfirmedOnceByAimingAndTurnsThePartToIt()
        {
            GameObject anchor = Anchor("couch", new Vector3(1f, 0.45f, 1f), 70f);
            var catalog = new ListSceneAnchorCatalog();
            catalog.Add(Record(anchor, "uuid", SeatOutline));
            FreeformCollaborationApp app = App(catalog, out _);
            SceneAnchorRecord record = catalog.Anchors[0];
            Assert.That(app.SeatFront(record), Is.Null, "nothing confirmed yet");

            // The user aims at a point on the seat's +y side (in the anchor's frame) from the centre.
            Vector3 aimed = anchor.transform.TransformPoint(new Vector3(0.04f, 0.5f, 0f));
            Assert.That(app.TryConfirmSeatFront(record, aimed), Is.True);
            float? front = app.SeatFront(record);
            Assert.That(front.HasValue, Is.True);
            Vector2 centre = SceneAnchorSpatialAuthoring.PolygonCentroid(SeatOutline); // the area centroid: the core's radial chart origin
            Vector2 expectedDirection = (new Vector2(0.04f, 0.5f) - centre).normalized;
            Assert.That(Mathf.DeltaAngle(front.Value, Mathf.Atan2(expectedDirection.y, expectedDirection.x) * Mathf.Rad2Deg),
                Is.EqualTo(0f).Within(1e-3f));

            Assert.That(app.TryRegisterSeat(record, "1", false), Is.True);
            var entity = anchor.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(entity.SeatFrontConfirmed, Is.True);
            Assert.That(anchor.name, Does.StartWith("COUCH#1#front@"), "the front rides on the name the peer already parses");
            Assert.That(SharedAnchorName.TryParse(anchor.name, out _, out _, out Vector3 sentSize, out float? sentFront), Is.True);
            Assert.That(sentSize, Is.EqualTo(VolumeSize), "the measured box rides with it");
            Assert.That(sentFront.Value, Is.EqualTo(front.Value).Within(1e-4f), "and the confirmed front itself, in the anchor's plane");
            SpatialElementMapping seat = Compile(anchor.GetComponent<SbSpatialAuthoring>()).Mappings.Single(m => m.role == SpatialElementRole.Surface);
            SpatialSubRegion part = seat.subRegions[0];

            // The part's +x, seen in the anchor's plane, is the confirmed front; and its polygon, carried into the surface frame, is
            // the whole seat again.
            float partX = part.rotationDegrees * Mathf.Deg2Rad;
            Vector2 xInPolygonPlane = new Vector2(Mathf.Cos(partX), Mathf.Sin(partX));
            Vector2 xInAnchorPlane = new Vector2(xInPolygonPlane.x, -xInPolygonPlane.y); // polygon plane = (x, -y) of the anchor's
            Assert.That(Mathf.DeltaAngle(Mathf.Atan2(xInAnchorPlane.y, xInAnchorPlane.x) * Mathf.Rad2Deg, front.Value), Is.EqualTo(0f).Within(1e-3f));
            Vector2[] chart = ChartOf(SeatOutline);
            for (int i = 0; i < chart.Length; ++i)
            {
                Vector2 q = part.localPolygon[i];
                var carried = new Vector2(Mathf.Cos(partX) * q.x - Mathf.Sin(partX) * q.y, Mathf.Sin(partX) * q.x + Mathf.Cos(partX) * q.y);
                Assert.That(Vector2.Distance(carried, chart[i]), Is.LessThan(1e-4f));
            }

            // Once: a peer that runs again with the same store registers the seat with the front it already has.
            var second = Anchor("couch2", new Vector3(1f, 0.45f, 1f), 70f);
            var catalog2 = new ListSceneAnchorCatalog();
            catalog2.Add(Record(second, "uuid", SeatOutline));
            FreeformCollaborationApp again = App(catalog2, out _);
            again.UseSeatPreferences(app.SeatPreferences);
            Assert.That(again.SeatFront(catalog2.Anchors[0]), Is.EqualTo(front));
        }

        [Test]
        public void AFrontIsConfirmedOnBothSeatsOrTheYawIsFacings()
        {
            GameObject local = Anchor("local", new Vector3(0f, 0.45f, 0f));
            var catalog = new ListSceneAnchorCatalog();
            catalog.Add(Record(local, "ul", SeatOutline));
            FreeformCollaborationApp app = App(catalog, out _);
            Assert.That(app.TryConfirmSeatFront(catalog.Anchors[0], local.transform.TransformPoint(new Vector3(0.5f, 0f, 0f))), Is.True);
            Assert.That(app.TryRegisterSeat(catalog.Anchors[0], "5", false), Is.True);
            MarkAttached(local);

            app.OnShare(Replica("5", frontConfirmed: false));
            Assert.That(app.SeatLinks.FrontConfirmed(SeatIdentity.SharedId("5")), Is.False, "the source seat has no confirmed front");
            app.OnShare(Replica("5", frontConfirmed: true));
            Assert.That(app.SeatLinks.FrontConfirmed(SeatIdentity.SharedId("5")), Is.True);
            long affordance = SpatialAuthoringCompiler.AffordanceSharedId(SeatIdentity.SharedId("5"), 0);
            Assert.That(app.SeatLinks.FrontConfirmed(affordance), Is.True, "the pelvis proposal names the seat's surface element");
        }

        [Test]
        public void TheAimedPointOverTheCentreConfirmsNothing()
        {
            GameObject anchor = Anchor("couch", new Vector3(0f, 0.45f, 0f));
            Vector2 centre = SceneAnchorSpatialAuthoring.PolygonCentroid(SeatOutline); // the area centroid: the core's radial chart origin
            Assert.That(SceneAnchorSpatialAuthoring.TryFrontFromWorldPoint(anchor.transform.localToWorldMatrix, SeatOutline,
                anchor.transform.TransformPoint(new Vector3(centre.x, centre.y, 0.3f)), out _), Is.False);
        }

        // ------------------------------------------------------------------ identity, binding

        [Test]
        public void ASeatNameCarriesItsKeyAndItsFrontAndNothingElse()
        {
            Assert.That(SeatIdentity.Compose("12", false), Is.EqualTo("COUCH#12"));
            Assert.That(SeatIdentity.Compose("12", true), Is.EqualTo("COUCH#12#front"));
            Assert.That(SeatIdentity.TryParse("COUCH#12#front", out string key, out bool front), Is.True);
            Assert.That((key, front), Is.EqualTo(("12", true)));
            Assert.That(SeatIdentity.TryParse("COUCH#12", out key, out front), Is.True);
            Assert.That(front, Is.False);
            Assert.That(SeatIdentity.TryParse("TABLE", out _, out _), Is.False);
            Assert.That(SeatIdentity.TryParse("COUCH#", out _, out _), Is.False);
            Assert.That(SeatIdentity.TryParse("COUCH#1#x", out _, out _), Is.False);
            Assert.Throws<ArgumentException>(() => SeatIdentity.Compose("a#b", false));
        }

        [Test]
        public void ThePelvisIsBoundToSittable()
        {
            var binding = FreeformCollaborationApp.SittingRelationBinding;
            Assert.That(binding.Anchor, Is.EqualTo("pelvis"));
            Assert.That(binding.Kind, Is.EqualTo(RelationKind.AffordancePose));
            Assert.That(binding.Tag, Is.EqualTo(SceneAnchorSpatialAuthoring.SittableTag));
            RelationBinding planned = SpatialAuthoringCompiler.PlanRelation(binding.Kind, binding.Anchor, binding.Tag);
            Assert.That(planned.Anchor, Is.EqualTo("pelvis"));
            Assert.That(planned.Tag, Is.EqualTo("Sittable"), "the tag is the AffordancePose binding's only");
            // The relation the app registers for the user's avatar is that binding; it waits for the pelvis anchor to exist.
            GameObject avatar = Anchor("avatar", Vector3.zero);
            avatar.AddComponent<SbMRUKAnchorEntity>();
            SpatialRelation relation = FreeformCollaborationApp.SittingRelation(avatar);
            Assert.That((relation.AnchorName, relation.Kind, relation.Tag), Is.EqualTo(("pelvis", RelationKind.AffordancePose, "Sittable")));
            Assert.That(relation.IsValid(), Is.True);
            FreeformCollaborationApp app = App(new ListSceneAnchorCatalog(), out _);
            Assert.That(app.TryRegisterSittingRelation(), Is.False, "no avatar and no registered embodiment yet");
            // The Spacing and Facing bindings are unchanged and carry no tag.
            foreach ((string anchor, RelationKind kind) in FreeformCollaborationApp.AvatarRelationBindings)
                Assert.That(SpatialAuthoringCompiler.PlanRelation(kind, anchor, "x").Tag, Is.Null);
        }

        [Test]
        public void TheSittingRelationIsRegisteredOnceWhenTheAvatarAndItsBodyAreRegistered()
        {
            FreeformCollaborationApp app = App(new ListSceneAnchorCatalog(), out _);
            var registered = new List<SpatialRelation>();
            app.RelationRegistrar = registered.Add;
            GameObject avatar = Anchor("avatar", Vector3.zero);
            avatar.AddComponent<SbMRUKAnchorEntity>();
            typeof(FreeformCollaborationApp).GetField("_userAvatar", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, avatar);

            app.NotifySourceBodyRegistered();
            Assert.That(registered, Is.Empty, "the avatar's own relations are not registered yet");
            typeof(FreeformCollaborationApp).GetField("_avatarRegistrationComplete", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, true);
            Assert.That(app.TryRegisterSittingRelation(), Is.True);
            app.NotifySourceBodyRegistered();
            Assert.That(app.TryRegisterSittingRelation(), Is.False, "once");
            Assert.That(registered.Count, Is.EqualTo(1));
            Assert.That((registered[0].AnchorName, registered[0].Kind, registered[0].Tag),
                Is.EqualTo(("pelvis", RelationKind.AffordancePose, "Sittable")));
            Assert.That(registered[0].Source, Is.SameAs(avatar));
        }

        [Test]
        public void ThePartsPlusXInTheWorldIsTheConfirmedFrontThroughTheCoresOwnElementFrame()
        {
            // Independent of the (x, -y) derivation the production code uses: the core places a sub-region by the element's world matrix
            // (the registered plane element is localToWorld * Rx(+90), converted to the core's Z-up world) times the patch's transform16.
            // The patch's +x is that product applied to the core-local x axis; it must be the confirmed front, in the anchor's own plane.
            foreach (float yawDegrees in new[] { 0f, 35f, 140f, 280f })
            {
                foreach (float front in new[] { 0f, 30f, 90f, -120f })
                {
                    GameObject anchor = Anchor("couch", new Vector3(1f, 0.45f, -2f), yawDegrees);
                    Assert.That(SceneAnchorSpatialAuthoring.TryBuildSeatGeometry(VolumeCenter, VolumeSize, SeatOutline, front,
                        out _, out _, out _, out AuthoredSubRegion seat), Is.True);
                    // transform16 exactly as SpatialBlendAPI.AddSubRegion sends it (row-major rotation about the core's +z).
                    double radians = seat.rotationDegrees * Math.PI / 180.0, c = Math.Cos(radians), s = Math.Sin(radians);
                    Matrix4x4 patch = Matrix4x4.identity;
                    patch.SetRow(0, new Vector4((float)c, (float)-s, 0f, 0f));
                    patch.SetRow(1, new Vector4((float)s, (float)c, 0f, 0f));
                    Matrix4x4 element = CoordinateConverter.UnityToSpatialBlendMatrix(SceneGeometryChart.ElementTransform(anchor.transform.localToWorldMatrix));
                    Vector3 coreX = element.MultiplyVector(patch.MultiplyVector(Vector3.right)).normalized;
                    Vector3 worldX = CoordinateConverter.SpatialBlendToUnity3D(coreX.x, coreX.y, coreX.z).normalized;
                    Vector3 expected = anchor.transform.TransformDirection(new Vector3(Mathf.Cos(front * Mathf.Deg2Rad), Mathf.Sin(front * Mathf.Deg2Rad), 0f)).normalized;
                    Assert.That(Vector3.Distance(worldX, expected), Is.LessThan(1e-4f), "yaw " + yawDegrees + ", front " + front);
                    // And the patch polygon, carried by the same product, lies on the seat surface in the world (the part covers the seat).
                    Vector3 chart0 = new Vector3(seat.polygon[0].x, seat.polygon[0].y, 0f);
                    Vector3 core0 = element.MultiplyPoint3x4(patch.MultiplyPoint3x4(chart0));
                    Vector3 world0 = CoordinateConverter.SpatialBlendToUnity3D(core0.x, core0.y, core0.z);
                    Assert.That(SeatOutline.Any(p => Vector3.Distance(anchor.transform.TransformPoint(new Vector3(p.x, p.y, 0f)), world0) < 1e-4f), Is.True,
                        "the patch's first vertex is one of the seat outline's");
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private sealed class RecordingPublisher : ISceneAnchorPublisher
        {
            public GameObject Anchor;
            public bool ShareWithPeers;
            public void Publish(GameObject anchor, bool shareWithPeers) { Anchor = anchor; ShareWithPeers = shareWithPeers; }
        }

        private FreeformCollaborationApp App(ISceneAnchorCatalog catalog, out RecordingPublisher published)
        {
            var go = new GameObject("freeform-app");
            _objects.Add(go);
            var app = go.AddComponent<FreeformCollaborationApp>();
            app.UseSceneAnchorCatalog(catalog);
            published = new RecordingPublisher();
            app.UseSceneAnchorPublisher(published);
            app.UseSeatPreferences(new MemorySeatPreferences());
            return app;
        }

        // The replica of a peer's seat, as SbMRUKAnchorEntity.SyncRemote rebuilds it from the wire (the name and the label): the
        // sharer's key, the box it measured (the same couch as the local ones here) and the front it confirmed.
        private GameObject Replica(string key, bool frontConfirmed)
        {
            string name = SharedAnchorName.Compose(SeatIdentity.Compose(key, frontConfirmed), VolumeCenter, VolumeSize,
                frontConfirmed ? 25f : (float?)null);
            var go = new GameObject(name);
            _objects.Add(go);
            var entity = go.AddComponent<SbMRUKAnchorEntity>();
            entity.SetSceneLabel("COUCH");
            entity.AdoptWireName(name, "COUCH");
            return go;
        }

        // A seat the manager attached: the entity holds its manager and an element id (EditMode has no native session).
        private void MarkAttached(GameObject seat)
        {
            var managerObject = new GameObject("fake-manager");
            managerObject.SetActive(false);
            _objects.Add(managerObject);
            var manager = managerObject.AddComponent<SpatialBlendManager>();
            var entity = seat.GetComponent<SbEntity>();
            typeof(SbEntity).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entity, manager);
            typeof(SbEntity).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entity, 5L);
            Assert.That(entity.IsAttached(), Is.True);
        }

        private static long ResolveSharedId(GameObject go) =>
            (long)typeof(SpatialBlendManager).GetMethod("ResolveSharedElementId", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { go });
    }
}
#endif
