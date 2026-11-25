// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using Meta.XR.MRUtilityKit;
using SpatialBlend;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class SceneGeometryContractTests
    {
        [Test]
        public void Descriptor_PreservesLocalBoundsAndFullWorldChart()
        {
            Matrix4x4 chart = Matrix4x4.TRS(new Vector3(4f, 2f, -3f),
                Quaternion.Euler(0f, 90f, 0f), new Vector3(2f, 3f, 4f));
            Bounds volume = new Bounds(new Vector3(.25f, -.5f, .75f),
                new Vector3(2f, 1f, 3f));
            var boundary = new List<Vector2>
            {
                new Vector2(-1f, -.5f), new Vector2(1.25f, -.25f),
                new Vector2(.75f, 1.5f), new Vector2(-.75f, 1.25f)
            };

            bool created = SceneGeometryDescriptor.TryCreate(17, chart, volume,
                boundary, new Rect(-9f, -9f, 18f, 18f), out var descriptor);

            Assert.That(created, Is.True);
            Assert.That(descriptor.HasVolume, Is.True);
            Assert.That(descriptor.HasPlane, Is.True);
            Assert.That(descriptor.PlaneBoundaryIsRectApproximation, Is.False);
            Assert.That(descriptor.LocalToWorld, Is.EqualTo(chart));
            Assert.That(Vector3.Distance(descriptor.LocalToWorld.MultiplyPoint3x4(volume.center),
                new Vector3(7f, .5f, -3.5f)), Is.LessThan(1e-5f));
            Assert.That(descriptor.PlaneBoundary[2], Is.EqualTo(boundary[2]),
                "Measured PlaneBoundary2D must win over its enclosing PlaneRect.");
        }

        [Test]
        public void Descriptor_UsesExplicitRectangleFallbackOnlyWithoutBoundary()
        {
            var rect = new Rect(-2f, .5f, 3f, 4f);

            bool created = SceneGeometryDescriptor.TryCreate(42, Matrix4x4.identity,
                null, null, rect, out var descriptor);

            Assert.That(created, Is.True);
            Assert.That(descriptor.HasVolume, Is.False);
            Assert.That(descriptor.HasPlane, Is.True);
            Assert.That(descriptor.PlaneBoundaryIsRectApproximation, Is.True);
            CollectionAssert.AreEqual(new[]
            {
                new Vector2(-2f, .5f), new Vector2(1f, .5f),
                new Vector2(1f, 4.5f), new Vector2(-2f, 4.5f)
            }, descriptor.PlaneBoundary);
        }

        [Test]
        public void InvalidDescriptor_DoesNotReachGeometryOnlySink()
        {
            var sink = new RecordingSink();
            bool created = SceneGeometryDescriptor.TryCreate(3,
                Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.zero),
                new Bounds(Vector3.zero, Vector3.one), null, null, out var descriptor);
            if (created)
            {
                sink.RegisterLocalSceneGeometry(descriptor);
            }

            Assert.That(created, Is.False);
            Assert.That(sink.Descriptors, Is.Empty);
        }

        [Test]
        public void MalformedPresentPrimitive_IsRejectedRatherThanSilentlyDropped()
        {
            var sink = new RecordingSink();
            bool created = SceneGeometryDescriptor.TryCreate(8, Matrix4x4.identity,
                new Bounds(Vector3.zero, Vector3.one), new[]
                {
                    new Vector2(0f, 0f), new Vector2(float.NaN, 1f), new Vector2(1f, 0f)
                }, new Rect(0f, 0f, 1f, 1f), out var descriptor);
            if (created) sink.RegisterLocalSceneGeometry(descriptor);

            Assert.That(created, Is.False);
            Assert.That(sink.Descriptors, Is.Empty);
        }

        [Test]
        public void SinkReceivesGeometryWithoutSharingOrSemanticPayload()
        {
            bool created = SceneGeometryDescriptor.TryCreate(31, Matrix4x4.identity,
                new Bounds(Vector3.zero, Vector3.one), null, null, out var descriptor);
            var sink = new RecordingSink();

            Assert.That(created, Is.True);
            Assert.That(sink.RegisterLocalSceneGeometry(descriptor), Is.True);
            Assert.That(sink.Descriptors, Has.Count.EqualTo(1));
            Assert.That(sink.Descriptors[0].SourceKey, Is.EqualTo(31));
            Assert.That(sink.Descriptors[0].HasVolume, Is.True);
            Assert.That(sink.Descriptors[0].HasPlane, Is.False);
        }

        [Test]
        public void Manager_UsesAnchorForVolumeAndDedicatedPlaneChildChart()
        {
            var go = new GameObject("geometry-manager");
            var manager = go.AddComponent<RecordingGeometryManager>();
            try
            {
                manager.Configure(5);
                // An MRUK plane anchor: normal along local Z, so its local XY outline is horizontal (Rx(-90)), turned 90 degrees.
                Matrix4x4 anchor = Matrix4x4.TRS(new Vector3(2f, 3f, 5f),
                    Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(-90f, 0f, 0f), Vector3.one);
                bool made = SceneGeometryDescriptor.TryCreate(99, anchor,
                    new Bounds(new Vector3(.5f, .25f, -.5f), Vector3.one), new[]
                    {
                        new Vector2(-1f, -.5f), new Vector2(2f, -.5f), new Vector2(0f, 1f)
                    }, null, out var descriptor);

                Assert.That(made, Is.True);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                Assert.That(manager.AddParents, Is.EqualTo(new[] { 1L, 10L }));
                // The parent is placed by the anchor's FLOOR frame (position and yaw), the plane child by its height in it.
                Assert.That(manager.Transforms[0], Is.EqualTo(SceneGeometryChart.ElementTransform(anchor)));
                Assert.That(manager.Boxes, Has.Count.EqualTo(1));
                Assert.That(manager.Polygons, Has.Count.EqualTo(1));
                CollectionAssert.AreEqual(new[] { "add", "add", "transform", "box", "polygon" },
                    manager.NativeOperations, "Local geometry performs no share, tag, or affordance call.");

                // Each outline vertex, read as the native (x, z) of the floor frame at the plane's height, is where the
                // anchor's own local XY put it in the world.
                Matrix4x4 floor = manager.Transforms[0];
                var nativePoints = manager.PolygonPoints[0];
                var outline = descriptor.PlaneBoundary;
                for (int i = 0; i < outline.Count; ++i)
                {
                    Vector2 local = nativePoints[outline.Count - 1 - i]; // reversed, y negated
                    Vector3 actualWorld = floor.MultiplyPoint3x4(new Vector3(local.x, 0f, local.y));
                    Vector3 expectedMrukWorld = anchor.MultiplyPoint3x4(new Vector3(outline[i].x, outline[i].y, 0f));
                    Assert.That(Vector3.Distance(actualWorld, expectedMrukWorld), Is.LessThan(1e-5f), "vertex " + i);
                }

                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                Assert.That(manager.AddParents, Has.Count.EqualTo(2), "Duplicate must not mutate native geometry.");
                manager.Configure(6);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                Assert.That(manager.AddParents, Has.Count.EqualTo(4), "Fresh session gets local geometry again.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // The one floor contract: a wall is registered at its own transform and local polygon, exactly as the
        // app has it. Nothing flattens it to a floor frame; core's GroundFrame decides its image on the floor (a segment).
        [Test]
        public void AVerticalWallIsRegisteredAtItsOwnTransformAndPolygonNotFlattened()
        {
            var go = new GameObject("geometry-manager-wall");
            var manager = go.AddComponent<RecordingGeometryManager>();
            try
            {
                manager.Configure(5);
                Matrix4x4 wall = Matrix4x4.TRS(new Vector3(0f, 1.2f, 3f), Quaternion.Euler(0f, 20f, 0f), new Vector3(2f, 1f, 1f));
                Assert.That(SceneGeometryDescriptor.TryCreate(77, wall, null, new[]
                    { new Vector2(-1f, -.5f), new Vector2(1f, -.5f), new Vector2(1f, .5f), new Vector2(-1f, .5f) }, null, out var descriptor), Is.True);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                Assert.That(manager.Transforms[0], Is.EqualTo(SceneGeometryChart.ElementTransform(wall)), "the wall's own transform, scale included");
                Assert.That(manager.Polygons, Has.Count.EqualTo(1), "its polygon is registered; core decides it has no inside");
                // Through the registration every vertex is where the wall put it (nothing was flattened).
                Vector2[] registered = manager.PolygonPoints[0];
                for (int i = 0; i < 4; ++i)
                {
                    Vector2 local = registered[3 - i];
                    Vector3 viaRegistration = manager.Transforms[0].MultiplyPoint3x4(new Vector3(local.x, 0f, local.y));
                    Vector3 expected = wall.MultiplyPoint3x4(new Vector3(descriptor.PlaneBoundary[i].x, descriptor.PlaneBoundary[i].y, 0f));
                    Assert.That(Vector3.Distance(viaRegistration, expected), Is.LessThan(1e-4f), "vertex " + i);
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Manager_FailedAddIsNotLatchedAndInvalidInputHasNoNativeCalls()
        {
            var go = new GameObject("geometry-manager-failure");
            var manager = go.AddComponent<RecordingGeometryManager>();
            try
            {
                manager.Configure(4);
                Assert.That(manager.RegisterLocalSceneGeometry(default), Is.False);
                Assert.That(manager.AddParents, Is.Empty,
                    "Malformed/default input must not allocate native elements.");
                manager.FailNextAdd = true;
                bool made = SceneGeometryDescriptor.TryCreate(123, Matrix4x4.identity,
                    new Bounds(Vector3.zero, Vector3.one), null, null, out var descriptor);
                Assert.That(made, Is.True);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.False);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True,
                    "Failed native allocation must remain retryable.");
                Assert.That(manager.AddParents, Has.Count.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Manager_PlaneChildRetryReusesItsPreviouslyAllocatedParent()
        {
            var go = new GameObject("geometry-manager-child-retry");
            var manager = go.AddComponent<RecordingGeometryManager>();
            try
            {
                manager.Configure(12);
                manager.FailOnAddAttempt = 2;
                bool made = SceneGeometryDescriptor.TryCreate(124, Matrix4x4.identity,
                    new Bounds(Vector3.zero, Vector3.one), new[]
                    {
                        new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(0f, 1f)
                    }, null, out var descriptor);

                Assert.That(made, Is.True);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.False);
                Assert.That(manager.RegisterLocalSceneGeometry(descriptor), Is.True);
                CollectionAssert.AreEqual(new[] { 1L, 10L, 10L }, manager.AddParents,
                    "The retry must allocate only the missing plane child under the original parent.");
                Assert.That(manager.TransformElements, Is.EqualTo(new[] { 10L }),
                    "No geometry is assigned until every owned child has been allocated; the plane child has no transform of its own. " +
                    "(This identity-matrix plane stands upright: its image on the floor is a segment, which core GroundFrame never treats as having an inside.)");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // The floor and the ceiling are left out by geometry (a volumeless horizontal plane covering most of
        // the room), never by label; a table's small top is staged.
        [Test]
        public void MrukAdapter_LeavesOutAPlaneThatCoversTheRoomAndStagesASmallTableTop()
        {
            var roomObject = new GameObject("mruk-room");
            roomObject.SetActive(false);
            var room = roomObject.AddComponent<MRUKRoom>();
            MRUKAnchor Plane(string name, float w, float d, float y, bool up, Bounds? volume)
            {
                var go = new GameObject(name);
                go.SetActive(false);
                go.transform.SetParent(roomObject.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(0f, y, 0f), Quaternion.Euler(up ? -90f : 90f, 0f, 0f));
                var a = go.AddComponent<MRUKAnchor>();
                SetMrukProperty(a, "Label", MRUKAnchor.SceneLabels.OTHER);   // no label says what it is
                SetMrukProperty(a, "VolumeBounds", volume);
                SetMrukProperty(a, "PlaneBoundary2D", new List<Vector2> { new Vector2(-w / 2, -d / 2), new Vector2(w / 2, -d / 2), new Vector2(w / 2, d / 2), new Vector2(-w / 2, d / 2) });
                SetMrukProperty(a, "Room", room);
                room.Anchors.Add(a);
                return a;
            }
            try
            {
                MRUKAnchor floor = Plane("floor-like", 4f, 5f, 0f, true, null);
                MRUKAnchor ceiling = Plane("ceiling-like", 4f, 5f, 2.5f, false, null);
                MRUKAnchor table = Plane("table-top", 1.2f, 0.6f, 0.75f, true, new Bounds(new Vector3(0f, 0f, -0.375f), new Vector3(1.2f, 0.6f, 0.75f)));
                MRUKAnchor rug = Plane("small-plane", 1f, 1f, 0.01f, true, null);
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(floor, out _), Is.False, "the floor's plane covers the room");
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(ceiling, out _), Is.False, "the ceiling's too");
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(table, out SceneGeometryDescriptor d), Is.True, "a table top is staged");
                Assert.That(d.HasPlane && d.HasVolume, Is.True);
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(rug, out _), Is.True, "a small horizontal plane is staged");
            }
            finally
            {
                Object.DestroyImmediate(roomObject);
            }
        }

        // A small room (a 1.8 x 2 m bathroom) is compared with its own floor outline, not an absolute size; a
        // ceiling scanned 10 degrees off level is still the ceiling.
        [Test]
        public void MrukAdapter_LeavesOutTheFloorAndATiltedCeilingOfASmallRoom()
        {
            var roomObject = new GameObject("mruk-bathroom");
            roomObject.SetActive(false);
            var room = roomObject.AddComponent<MRUKRoom>();
            MRUKAnchor Plane(string name, float w, float d, float y, Quaternion rotation, Bounds? volume)
            {
                var go = new GameObject(name);
                go.SetActive(false);
                go.transform.SetParent(roomObject.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(0f, y, 0f), rotation);
                var a = go.AddComponent<MRUKAnchor>();
                SetMrukProperty(a, "Label", MRUKAnchor.SceneLabels.OTHER);
                SetMrukProperty(a, "VolumeBounds", volume);
                SetMrukProperty(a, "PlaneBoundary2D", new List<Vector2> { new Vector2(-w / 2, -d / 2), new Vector2(w / 2, -d / 2), new Vector2(w / 2, d / 2), new Vector2(-w / 2, d / 2) });
                SetMrukProperty(a, "Room", room);
                room.Anchors.Add(a);
                return a;
            }
            try
            {
                MRUKAnchor floor = Plane("floor", 1.8f, 2f, 0f, Quaternion.Euler(-90f, 0f, 0f), null);
                typeof(MRUKRoom).GetProperty("FloorAnchor").SetValue(room, floor);
                MRUKAnchor ceiling = Plane("ceiling", 1.8f, 2f, 2.4f, Quaternion.Euler(80f, 0f, 0f), null);
                MRUKAnchor sink = Plane("sink-top", 0.6f, 0.45f, 0.85f, Quaternion.Euler(-90f, 0f, 0f), new Bounds(new Vector3(0f, 0f, -0.425f), new Vector3(0.6f, 0.45f, 0.85f)));
                MRUKAnchor mat = Plane("bath-mat", 0.8f, 0.5f, 0.01f, Quaternion.Euler(-90f, 0f, 0f), null);
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(floor, out _), Is.False, "the floor of a 3.6 m^2 room covers it");
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(ceiling, out _), Is.False, "a ceiling 10 degrees off level covers it too");
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(sink, out _), Is.True, "furniture is staged");
                Assert.That(MRUKSceneGeometryAdapter.TryCreateDescriptor(mat, out _), Is.True, "a mat (11% of the floor) is staged");
            }
            finally
            {
                Object.DestroyImmediate(roomObject);
            }
        }

        [Test]
        public void MrukAdapter_RegistersOnlyConcreteLocalPrimitivesWithoutSemanticInference()
        {
            var roomObject = new GameObject("mruk-room");
            var globalObject = new GameObject("mruk-global");
            var validObject = new GameObject("mruk-valid");
            roomObject.SetActive(false);
            globalObject.SetActive(false);
            validObject.SetActive(false);
            var room = roomObject.AddComponent<MRUKRoom>();
            var global = globalObject.AddComponent<MRUKAnchor>();
            var valid = validObject.AddComponent<MRUKAnchor>();
            try
            {
                SetMrukProperty(global, "Label", MRUKAnchor.SceneLabels.GLOBAL_MESH);
                SetMrukProperty(valid, "Label", MRUKAnchor.SceneLabels.COUCH);
                SetMrukProperty(valid, "VolumeBounds", (Bounds?)new Bounds(
                    new Vector3(.25f, .5f, .75f), new Vector3(2f, 3f, 4f)));
                SetMrukProperty(valid, "PlaneBoundary2D", new List<Vector2>
                {
                    new Vector2(-2f, -1f), new Vector2(3f, -1f),
                    new Vector2(2f, 4f), new Vector2(-1f, 3f)
                });
                valid.transform.SetPositionAndRotation(new Vector3(4f, 5f, 6f),
                    Quaternion.Euler(10f, 20f, 30f));
                SetMrukProperty(valid, "Room", room);   // MRUK puts an anchor in its room before it is registered
                room.Anchors.Add(null);
                room.Anchors.Add(global);
                room.Anchors.Add(valid);
                var sink = new RecordingSink();

                Assert.That(MRUKSceneGeometryAdapter.RegisterCurrentRoom(room, sink), Is.EqualTo(1));
                Assert.That(sink.Descriptors, Has.Count.EqualTo(1));
                var descriptor = sink.Descriptors[0];
                Assert.That(descriptor.SourceKey, Is.EqualTo(MRUKSceneGeometryAdapter.StableKey(valid)), "the stable key, the same in every run");
                Assert.That(descriptor.HasVolume && descriptor.HasPlane, Is.True);
                Assert.That(descriptor.LocalToWorld, Is.EqualTo(valid.transform.localToWorldMatrix));
                CollectionAssert.AreEqual(valid.PlaneBoundary2D, descriptor.PlaneBoundary);
            }
            finally
            {
                Object.DestroyImmediate(validObject);
                Object.DestroyImmediate(globalObject);
                Object.DestroyImmediate(roomObject);
            }
        }

        [Test]
        public void NativeContentReady_EmitsForBytesThatBecomeAvailable()
        {
            var go = new GameObject("native-content");
            var entity = go.AddComponent<SbNativeEntity>();
            int calls = 0;
            try
            {
                entity.ContentReady += _ => ++calls;
                entity.SetContentData(new byte[] { 1, 2, 3 });
                Assert.That(calls, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // There is no Save, so the user's own scan is shared on connect like any native content;
        // a received replica is never registered as this user's content.
        [Test]
        public void TheUsersOwnScanIsSharedOnConnect_AndAReceivedReplicaIsSkipped()
        {
            var managerObject = new GameObject("scan-share-manager");
            var appObject = new GameObject("scan-share-app");
            var scanObject = new GameObject("own-scan");
            var receivedObject = new GameObject("received-copy");
            var manager = managerObject.AddComponent<RecordingGeometryManager>();
            var app = appObject.AddComponent<RecordingFreeformApp>();
            var scan = scanObject.AddComponent<SbNativeEntity>();
            var received = receivedObject.AddComponent<SbNativeEntity>();
            try
            {
                SpatialBlend.Samples.Scanning.ScannedObjectOrigin.Tag(scanObject);
                scan.SetContentData(new byte[] { 1, 2, 3 });
                SetPrivate(received, typeof(SbNativeEntity), "_isRemoteReplica", true);
                SpatialBlend.Samples.Scanning.ReceivedCopy.Attach(receivedObject, "Jiwon");
                received.SetContentData(new byte[] { 4, 5, 6 });
                manager.Configure(20);
                app.AttachManager(manager);
                SetPrivate(app, "_appStarted", true);

                app.BeginNativeContentRegistration();

                Assert.That(app.RegistrationCountFor(scan), Is.EqualTo(1), "the user's own scan is shared, with no Save step");
                Assert.That(app.RegistrationCountFor(received), Is.EqualTo(0), "a received replica is not this user's to share");
                app.BeginNativeContentRegistration();
                Assert.That(app.RegistrationCountFor(scan), Is.EqualTo(1), "once");
            }
            finally
            {
                Object.DestroyImmediate(scanObject);
                Object.DestroyImmediate(receivedObject);
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FreeformDisabledNativeContent_RescansReadyLocalBytesOnceAndExcludesRemoteReplica()
        {
            var managerObject = new GameObject("native-content-manager");
            var appObject = new GameObject("native-content-app");
            var contentObject = new GameObject("pending-native-content");
            var remoteObject = new GameObject("remote-native-content");
            var manager = managerObject.AddComponent<RecordingGeometryManager>();
            var app = appObject.AddComponent<RecordingFreeformApp>();
            var entity = contentObject.AddComponent<SbNativeEntity>();
            var remote = remoteObject.AddComponent<SbNativeEntity>();
            try
            {
                manager.Configure(20);
                app.AttachManager(manager);
                SetPrivate(app, "_appStarted", true);
                app.BeginNativeContentRegistration();
                Assert.That(app.RegistrationCountFor(entity), Is.EqualTo(0));
                Assert.That(app.RegistrationCountFor(remote), Is.EqualTo(0));

                // EditMode does not dispatch MonoBehaviour lifecycle callbacks
                // for this direct fixture, so invoke the production callbacks.
                InvokeLifecycle(app, "OnDisable");
                entity.SetContentData(new byte[] { 9 });
                SetPrivate(remote, typeof(SbNativeEntity), "_isRemoteReplica", true);
                remote.SetContentData(new byte[] { 7 });
                Assert.That(app.RegistrationCountFor(entity), Is.EqualTo(0),
                    "Disabled apps must not retain a ContentReady listener.");
                Assert.That(app.RegistrationCountFor(remote), Is.EqualTo(0),
                    "Remote replicas must never be registered as authored native content.");

                InvokeLifecycle(app, "OnEnable");
                Assert.That(app.RegistrationCountFor(entity), Is.EqualTo(1),
                    "Re-enable rescans locally authored bytes exactly once; remote replicas stay excluded.");
                Assert.That(app.RegistrationCountFor(remote), Is.EqualTo(0));
                entity.SetContentData(new byte[] { 8 });
                Assert.That(app.RegistrationCountFor(entity), Is.EqualTo(1),
                    "The ready registration seam is idempotent after the pending listener is removed.");
            }
            finally
            {
                Object.DestroyImmediate(remoteObject);
                Object.DestroyImmediate(contentObject);
                Object.DestroyImmediate(appObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void AppStop_ResetsSessionRegistrationGatesWithoutDeviceInput()
        {
            var go = new GameObject("scene-geometry-app");
            var app = go.AddComponent<FreeformCollaborationApp>();
            var type = typeof(FreeformCollaborationApp);
            try
            {
                type.GetField("_appStarted", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).SetValue(app, true);
                type.GetField("_avatarRegistrationComplete", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).SetValue(app, true);
                // Directly calling the production stop hook is intentional: this
                // EditMode fixture has no connection/device lifecycle dispatcher.
                app.OnAppStop();

                Assert.That(type.GetField("_appStarted", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).GetValue(app), Is.False);
                Assert.That(type.GetField("_avatarRegistrationComplete", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).GetValue(app), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AppStop_DoesNotDisableControllerReclaimedDuringRelease()
        {
            var appObject = new GameObject("stop-reentry-app");
            var avatar = new GameObject("stop-reentry-avatar") { tag = "Avatar" };
            avatar.AddComponent<SbLocalEntity>();
            var app = appObject.AddComponent<FreeformCollaborationApp>();
            try
            {
                app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                var controller = avatar.GetComponent<SpatialBlend.Motion.AnchorTargetController>();
                bool readded = false;
                controller.TargetReleased += _ =>
                {
                    if (readded) return;
                    readded = true;
                    app.OnObjectAdded(avatar, SbApp.OnObjectAddedType.Added);
                };
                controller.ApplyTarget("hand", Vector3.zero, 0f, true, 1d, 1d);

                app.OnAppStop();

                Assert.That(readded, Is.True);
                Assert.That(controller.enabled, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(appObject);
            }
        }

        private sealed class RecordingSink : ILocalSceneGeometrySink
        {
            public readonly List<SceneGeometryDescriptor> Descriptors =
                new List<SceneGeometryDescriptor>();

            public bool RegisterLocalSceneGeometry(SceneGeometryDescriptor descriptor)
            {
                Descriptors.Add(descriptor);
                return true;
            }
        }

        private sealed class RecordingGeometryManager : SpatialBlendManager
        {
            public readonly List<long> AddParents = new List<long>();
            public readonly List<Matrix4x4> Transforms = new List<Matrix4x4>();
            public readonly List<long> TransformElements = new List<long>();
            public readonly List<int> Boxes = new List<int>();
            public readonly List<int> Polygons = new List<int>();
            public readonly List<Vector2[]> PolygonPoints = new List<Vector2[]>();
            public readonly List<string> NativeOperations = new List<string>();
            public bool FailNextAdd;
            public int FailOnAddAttempt = -1;
            public int AddAttempts;
            private long _next = 10;

            protected override void InitializeNativeLibrary() { }
            public override void Host(int port) { }

            public void Configure(long generation)
            {
                SetPrivate("_initialized", true);
                SetPrivate("_connectionState", SpatialBlendConnectionState.Connected);
                SetPrivate("_lifecycleGeneration", generation);
            }

            protected override long EnsureLocalSceneGeometryRoom() => 1;
            protected override long AddLocalSceneGeometryElement(long parent)
            {
                AddParents.Add(parent);
                NativeOperations.Add("add");
                ++AddAttempts;
                if (AddAttempts == FailOnAddAttempt) return -1;
                if (FailNextAdd)
                {
                    FailNextAdd = false;
                    return -1;
                }
                return _next++;
            }
            protected override void SetLocalSceneGeometryTransform(long element, Matrix4x4 transform) =>
                RecordTransform(element, transform);
            protected override void SetLocalSceneGeometryBox(long element, int role, Vector3 min, Vector3 max) =>
                RecordBox(role);
            protected override void SetLocalSceneGeometryPolygon(long element, int role,
                System.Collections.Generic.IReadOnlyList<Vector2> points)
            {
                RecordPolygon(role);
                PolygonPoints.Add(new List<Vector2>(points).ToArray());
            }

            private void RecordTransform(long element, Matrix4x4 transform)
            {
                NativeOperations.Add("transform");
                TransformElements.Add(element);
                Transforms.Add(transform);
            }
            private void RecordBox(int role)
            {
                NativeOperations.Add("box");
                Boxes.Add(role);
            }
            private void RecordPolygon(int role)
            {
                NativeOperations.Add("polygon");
                Polygons.Add(role);
            }

            private void SetPrivate(string name, object value)
            {
                var field = typeof(SpatialBlendManager).GetField(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                field.SetValue(this, value);
            }
        }

        private sealed class RecordingFreeformApp : FreeformCollaborationApp
        {
            private readonly List<SbNativeEntity> _registeredNativeEntities =
                new List<SbNativeEntity>();

            public void AttachManager(SpatialBlendManager manager) => _spaceManager = manager;
            public void BeginNativeContentRegistration() => RegisterNativeEntities();
            public int RegistrationCountFor(SbNativeEntity entity)
            {
                int count = 0;
                foreach (SbNativeEntity registered in _registeredNativeEntities)
                    if (registered == entity) ++count;
                return count;
            }
            protected override void RegisterReadyNativeEntity(SbNativeEntity nativeEntity) =>
                _registeredNativeEntities.Add(nativeEntity);
        }

        private static void SetPrivate(object instance, string name, object value)
        {
            var field = instance.GetType().BaseType.GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(instance, value);
        }

        private static void SetPrivate(object instance, System.Type type, string name, object value)
        {
            var field = type.GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(instance, value);
        }

        private static void SetMrukProperty(MRUKAnchor anchor, string name, object value)
        {
            var property = typeof(MRUKAnchor).GetProperty(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            property.SetValue(anchor, value);
        }

        private static void InvokeLifecycle(object instance, string name)
        {
            System.Reflection.MethodInfo method = null;
            for (System.Type type = instance.GetType(); type != null && method == null;
                 type = type.BaseType)
            {
                method = type.GetMethod(name, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly);
            }
            Assert.That(method, Is.Not.Null, $"Missing production lifecycle seam: {name}");
            method.Invoke(instance, null);
        }
    }
}
#endif
