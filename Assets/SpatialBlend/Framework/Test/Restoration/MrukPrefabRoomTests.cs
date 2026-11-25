// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Samples;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The Freeform app's use of a real MRUK room, loaded from one of MRUK's own prefab rooms (Bedroom01: a floor,
    /// ten wall faces and two TABLEs) through the real MRUK scene store. Nothing here is a synthetic anchor.
    /// The expectations are v1's: EnvironmentRaycastUtility is byte-identical to v1,
    /// MrukSceneAnchorCatalog reads what v1's FindBiggestAnchor read, and the release plan is v1's PerformRaycastAndSnap.
    ///
    /// The Editor cannot load MRUK's shared library for a package that lives in Library/PackageCache (its loader
    /// opens "Packages/com.meta.xr.mrutilitykit/..." relative to the project), and EditMode never runs MRUK's Awake
    /// or Update, so the fixture opens the library from the package's resolved path and calls both itself.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class MrukPrefabRoomTests
    {
        private const string RoomPrefab = "Packages/com.meta.xr.mrutilitykit/Core/Rooms/Prefabs/Bedroom/Bedroom01.prefab";
        private const float Tolerance = 1e-3f;

        [DllImport("libSystem.dylib")] private static extern IntPtr dlopen(string path, int flags);

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private MRUK _mruk;
        private MRUKRoom _room;

        // MRUK's native anchor store is process-wide and a second load in the same Editor process spins forever,
        // so the room is loaded once for the fixture and kept. Each test restores the table anchors it touched.
        private static MRUK s_mruk;
        private static MRUKRoom s_room;
        private readonly Dictionary<Transform, HashSet<Transform>> _originalChildren = new Dictionary<Transform, HashSet<Transform>>();

        [UnitySetUp]
        public IEnumerator LoadTheRoom()
        {
            if (s_room == null) yield return LoadOnce();
            _mruk = s_mruk;
            _room = s_room;
            EnvironmentRaycastUtility.ClearCache();
            foreach (MRUKAnchor a in _room.Anchors)
                _originalChildren[a.transform] = new HashSet<Transform>(a.transform.Cast<Transform>());
        }

        private static IEnumerator LoadOnce()
        {
            OpenSharedLibraryOnce();
            var go = new GameObject("mruk");
            go.SetActive(false);
            MRUK mruk = go.AddComponent<MRUK>();
            mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false };
            go.SetActive(true);

            // MRUK reports that no OVRCameraRig exists; that is the only error allowed.
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
            };
            Application.logMessageReceived += capture;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                typeof(MRUK).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mruk, null);
                MethodInfo update = typeof(MRUK).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefab);
                Assert.That(prefab, Is.Not.Null, RoomPrefab);
                var load = mruk.LoadSceneFromPrefab(prefab, false);
                while (!load.IsCompleted)
                {
                    update.Invoke(mruk, null);
                    yield return null;
                }
                Assert.That(load.Result, Is.EqualTo(MRUK.LoadDeviceResult.Success));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Application.logMessageReceived -= capture;
            }
            Assert.That(errors.Where(e => !Regex.IsMatch(e, "OVRCameraRig is not present")), Is.Empty);
            Assert.That(mruk.Rooms.Count, Is.EqualTo(1));
            s_mruk = mruk;
            s_room = mruk.GetCurrentRoom();
            Assert.That(s_room, Is.Not.Null);
        }

        [OneTimeTearDown]
        public void ReleaseTheRoom()
        {
            if (s_room != null) UnityEngine.Object.DestroyImmediate(s_room.gameObject);
            if (s_mruk != null) UnityEngine.Object.DestroyImmediate(s_mruk.gameObject);
            s_room = null;
            s_mruk = null;
        }

        [TearDown]
        public void Cleanup()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            foreach (var pair in _originalChildren)
            {
                if (pair.Key == null) continue;
                foreach (Transform child in pair.Key.Cast<Transform>().ToList())
                    if (!pair.Value.Contains(child)) UnityEngine.Object.DestroyImmediate(child.gameObject);
                foreach (var c in pair.Key.GetComponents<SbEntity>()) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in pair.Key.GetComponents<SbSpatialAuthoring>()) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in pair.Key.GetComponents<BoxCollider>()) UnityEngine.Object.DestroyImmediate(c);
            }
            _originalChildren.Clear();
        }

        private static bool s_libraryOpened;

        private static void OpenSharedLibraryOnce()
        {
            if (s_libraryOpened) return;
            if (Application.platform != RuntimePlatform.OSXEditor)
                Assert.Ignore("MRUK's shared library is opened from its macOS build here; this fixture runs in the macOS Editor only.");
            Assembly mruk = typeof(MRUK).Assembly;
            string root = UnityEditor.PackageManager.PackageInfo.FindForAssembly(mruk).resolvedPath;
            string folder = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "MacArm" : "Mac";
            IntPtr handle = dlopen(root + "/Plugins/" + folder + "/libmrutilitykitshared.dylib", 2);
            Assert.That(handle, Is.Not.EqualTo(IntPtr.Zero), "MRUK shared library did not open");
            mruk.GetType("Meta.XR.MRUtilityKit.MRUKNative", true)
                .GetField("_nativeLibraryPtr", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, handle);
            mruk.GetType("Meta.XR.MRUtilityKit.MRUKNativeFuncs", true)
                .GetMethod("LoadNativeFunctions", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Invoke(null, null);
            s_libraryOpened = true;
        }

        private IEnumerable<MRUKAnchor> Tables() =>
            _room.Anchors.Where(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE));

        // TABLE link

        [UnityTest]
        public IEnumerator TheRoomHasTwoTablesAndTheCatalogOffersEachOnce()
        {
            Assert.That(Tables().Count(), Is.EqualTo(2));
            var records = new MrukSceneAnchorCatalog().Anchors.Where(r => r.Label == "TABLE").ToList();
            Assert.That(records.Count, Is.EqualTo(2));
            foreach (SceneAnchorRecord record in records)
            {
                MRUKAnchor anchor = record.Anchor.GetComponent<MRUKAnchor>();
                Assert.That(anchor.VolumeBounds.HasValue, Is.True);
                Vector3 size = anchor.VolumeBounds.Value.size;
                Assert.That(record.Size, Is.EqualTo(size.x * size.y * size.z).Within(1e-6f), "the metric is the volume, as in v1");
                Assert.That(Vector3.Distance(record.LocalSize, size), Is.LessThan(1e-6f));
            }
            yield break;
        }

        [UnityTest]
        public IEnumerator TheHostSharesTheTableTheUserChoseAndTheReceiverLinksItsSimilarOne()
        {
            MRUKAnchor biggest = Tables().OrderByDescending(a =>
                a.VolumeBounds.Value.size.x * a.VolumeBounds.Value.size.y * a.VolumeBounds.Value.size.z).First();
            MRUKAnchor smaller = Tables().First(a => a != biggest);

            var hostObject = new GameObject("host-app");
            _objects.Add(hostObject);
            var host = hostObject.AddComponent<FreeformCollaborationApp>();
            var published = new RecordingPublisher();
            host.UseSceneAnchorPublisher(published);

            // Nothing is shared by itself; the user picks the table from the list.
            Assert.That(published.Anchor, Is.Null);
            SceneAnchorRecord chosen = host.ShareableAnchors().Single(r => r.Anchor == biggest.gameObject);
            Assert.That(host.TryShareAnchor(chosen), Is.True);

            Assert.That(published.Anchor, Is.SameAs(biggest.gameObject), "the anchor the user chose");
            Assert.That(published.ShareWithPeers, Is.True);
            var entity = biggest.GetComponent<SbMRUKAnchorEntity>();
            Assert.That(entity, Is.Not.Null);
            Assert.That(entity.SceneLabel, Is.EqualTo(SbMRUKAnchorEntity.TableLabel));
            Assert.That(smaller.GetComponent<SbMRUKAnchorEntity>(), Is.Null, "only the chosen TABLE is shared");
            BoxCollider box = biggest.GetComponent<BoxCollider>();
            Assert.That(Vector3.Distance(box.size, biggest.VolumeBounds.Value.size), Is.LessThan(Tolerance));
            Assert.That(Vector3.Distance(box.center, biggest.VolumeBounds.Value.center), Is.LessThan(Tolerance));

            // A peer's shared TABLE (its wire name carries the key and the measured box) links to this room's most similar TABLE, with
            // no prompt: this room is the same one, so that is the table of difference 0. The link waits for the local table to attach.
            var receiverObject = new GameObject("receiver-app");
            _objects.Add(receiverObject);
            var receiver = receiverObject.AddComponent<FreeformCollaborationApp>();
            receiver.UseSceneAnchorPublisher(new RecordingPublisher());
            var incoming = new GameObject(biggest.gameObject.name);
            _objects.Add(incoming);
            var replica = incoming.AddComponent<SbMRUKAnchorEntity>();
            replica.SetSceneLabel(SbMRUKAnchorEntity.TableLabel);
            replica.AdoptWireName(entity.gameObject.name, SbMRUKAnchorEntity.TableLabel);
            OnShareResponse response = receiver.OnShare(incoming);
            Assert.That(response.result, Is.EqualTo(OnShareResponse.Result.Defer));
            Assert.That(receiver.ShareStateOf(receiver.ShareableAnchors().Single(r => r.Anchor == biggest.gameObject)),
                Is.EqualTo(AnchorShareState.Linked));
            Assert.That(receiver.ShareStateOf(receiver.ShareableAnchors().Single(r => r.Anchor == smaller.gameObject)),
                Is.EqualTo(AnchorShareState.None), "one-to-one: the other table is not touched");
            yield break;
        }

        [Test]
        public void ARemoteAnchorWhoseLabelThisRoomLacksIsAddedAsANewBox()
        {
            var receiverObject = new GameObject("receiver-app");
            _objects.Add(receiverObject);
            var receiver = receiverObject.AddComponent<FreeformCollaborationApp>();
            var incoming = new GameObject("remote-lamp");
            _objects.Add(incoming);
            var replica = incoming.AddComponent<SbMRUKAnchorEntity>();
            replica.SetSceneLabel("LAMP");
            replica.AdoptWireName("LAMP#remote-1#obj@0,0,0,0.3,0.3,1.2", "LAMP");
            Assert.That(receiver.OnShare(incoming).result, Is.EqualTo(OnShareResponse.Result.AddAsNew));
        }

        // raycast

        private MRUKAnchor Floor => _room.Anchors.Single(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR));

        [Test]
        public void AnInsideDownRayHitsTheFloorWithAnUpNormalAtItsHeight()
        {
            Vector3 inside = Floor.transform.position + new Vector3(0f, 1.5f, 0f);
            Assert.That(_room.IsPositionInRoom(inside, false), Is.True);

            Assert.That(EnvironmentRaycastUtility.RaycastFloor(new Ray(inside, Vector3.down), out var hit), Is.True);

            Assert.That(hit.Source, Is.EqualTo(EnvironmentRaycastUtility.RaycastSource.MRUKFloor));
            Assert.That(hit.Point.y, Is.EqualTo(Floor.transform.position.y).Within(Tolerance));
            Assert.That(Vector3.Dot(hit.Normal, Vector3.up), Is.GreaterThan(0.99f));
            Assert.That(hit.Distance, Is.EqualTo(1.5f).Within(Tolerance));
        }

        [Test]
        public void ADownRayOutsideTheRoomOrBeyondTenMetresFindsNoFloor()
        {
            Vector3 inside = Floor.transform.position + new Vector3(0f, 1.5f, 0f);
            Assert.That(EnvironmentRaycastUtility.RaycastFloor(new Ray(inside + new Vector3(50f, 0f, 50f), Vector3.down), out _), Is.False);
            Assert.That(EnvironmentRaycastUtility.RaycastFloor(new Ray(inside + Vector3.up * 12f, Vector3.down), out _), Is.False,
                "max distance is 10 m");
        }

        [Test]
        public void AnUpwardRayDoesNotHitTheCeilingAsAFloorOrAWall()
        {
            Vector3 inside = Floor.transform.position + new Vector3(0f, 1.0f, 0f);
            Assert.That(EnvironmentRaycastUtility.RaycastFloor(new Ray(inside, Vector3.up), out _), Is.False);
            Assert.That(EnvironmentRaycastUtility.Raycast(new Ray(inside, Vector3.up), out _), Is.False,
                "the any-surface raycast asks for floor and wall faces only");
        }

        [Test]
        public void AHorizontalRayHitsAWallFaceWithAHorizontalNormalAndTheWallRaycastFindsItToo()
        {
            Vector3 inside = Floor.transform.position + new Vector3(0f, 1.0f, 0f);
            bool anyWall = false;
            foreach (MRUKAnchor wall in _room.Anchors.Where(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE)))
            {
                Vector3 toWall = Vector3.ProjectOnPlane(wall.transform.position - inside, Vector3.up);
                var ray = new Ray(inside, toWall.normalized);
                if (!EnvironmentRaycastUtility.RaycastWall(ray, out var hit)) continue;
                anyWall = true;
                Assert.That(hit.Source, Is.EqualTo(EnvironmentRaycastUtility.RaycastSource.MRUKWall));
                Assert.That(Mathf.Abs(hit.Normal.y), Is.LessThan(0.01f));
                Assert.That(hit.Distance, Is.LessThan(10f));
                Assert.That(EnvironmentRaycastUtility.Raycast(ray, out var any), Is.True);
                Assert.That(any.Distance, Is.EqualTo(hit.Distance).Within(Tolerance), "wall faces are in the any-surface filter");
            }
            Assert.That(anyWall, Is.True);
        }

        // Without depth the panel's wall pose comes from the MRUK wall fallback, and the drop must know it is on a wall.
        [Test]
        public void APanelAimedAtAnMrukWallWithoutDepthIsOnAWall_AndAimedAtTheFloorIsNot()
        {
            var go = new GameObject("panel");
            _objects.Add(go);
            var motif = go.AddComponent<PanelPlacementMotif>();
            var eye = new GameObject("panel-eye");
            _objects.Add(eye);
            SetField(motif, "_entityTransform", go.transform);
            SetField(motif, "_centerEyeAnchor", eye.transform);
            SetField(motif, "_raycastAnchor", eye.transform);
            MethodInfo pose = typeof(PanelPlacementMotif).GetMethod("TryGetEnvironmentPose", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo onWall = typeof(PanelPlacementMotif).GetField("_onWall", BindingFlags.Instance | BindingFlags.NonPublic);

            Vector3 inside = Floor.transform.position + new Vector3(0f, 1.0f, 0f);
            MRUKAnchor wall = _room.Anchors.First(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE) &&
                EnvironmentRaycastUtility.RaycastWall(new Ray(inside, Vector3.ProjectOnPlane(a.transform.position - inside, Vector3.up).normalized), out _));
            eye.transform.SetPositionAndRotation(inside, Quaternion.LookRotation(Vector3.ProjectOnPlane(wall.transform.position - inside, Vector3.up)));
            Assert.That(pose.Invoke(motif, null), Is.Not.Null, "the MRUK wall gives a pose");
            Assert.That((bool)onWall.GetValue(motif), Is.True, "and the panel is on a wall");

            eye.transform.SetPositionAndRotation(inside, Quaternion.LookRotation(Vector3.down + Vector3.forward * 0.1f));
            Assert.That(pose.Invoke(motif, null), Is.Not.Null, "the floor gives a pose");
            Assert.That((bool)onWall.GetValue(motif), Is.False, "and the panel is not on a wall any more");
        }

        // placement on the floor region

        private ObjectPlacementMotif PlacedAt(Vector3 position, bool snapOn)
        {
            var go = new GameObject("released-object");
            _objects.Add(go);
            go.transform.position = position;
            var motif = go.AddComponent<ObjectPlacementMotif>();
            SetField(motif, "_entityTransform", go.transform);
            SetField(motif, "_clampToWalkable", false);
            SetField(motif, "_snapOn", snapOn);
            var eye = new GameObject("eye");
            _objects.Add(eye);
            eye.transform.position = position + new Vector3(2f, 1.6f, 0f);
            SetField(motif, "_centerEyeAnchor", eye.transform);
            return motif;
        }

        private static bool Release(ObjectPlacementMotif motif) => (bool)typeof(ObjectPlacementMotif)
            .GetMethod("PerformRaycastAndSnap", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(motif, null);

        [Test]
        public void ASnapOnObjectReleasedNearTheRoomFloorSettlesOnItAtTheHoverDistance()
        {
            Vector3 floorPoint = Floor.transform.position;
            ObjectPlacementMotif motif = PlacedAt(floorPoint + new Vector3(0f, 0.3f, 0f), true);

            Assert.That(Release(motif), Is.True);

            Pose target = ((Pose?)Field(motif, "_targetPose")).Value;
            Assert.That(target.position.y, Is.EqualTo(0.1f).Within(Tolerance),
                "the absolute _hoverDistance (0.1), as v1; " +
                "it no longer follows the floor hit height (" + floorPoint.y + ") or the collider offset");
            Assert.That(target.position.x, Is.EqualTo(floorPoint.x).Within(Tolerance));
            Assert.That(target.position.z, Is.EqualTo(floorPoint.z).Within(Tolerance));
        }

        [Test]
        public void ASnapOnObjectReleasedHighSnapsToTheFloor_AndOutsideTheRoomDoesNotSnap()
        {
            // When the snap fails, the object is placed at the preview position: there is no snap distance limit (v1 had one); let go at
            // head height, it is set on the floor below. Only where there is no floor does it stay where it was let go.
            Vector3 floorPoint = Floor.transform.position;
            Assert.That(Release(PlacedAt(floorPoint + new Vector3(0f, 1.7f, 0f), true)), Is.True, "high above the floor: set on it");
            Assert.That(Release(PlacedAt(floorPoint + new Vector3(50f, 0.3f, 50f), true)), Is.False, "no floor under it");
        }

        [Test]
        public void ANonSnappingObjectReleasedInTheRoomStaysWhereItWas()
        {
            Vector3 released = Floor.transform.position + new Vector3(0f, 1.4f, 0f);
            ObjectPlacementMotif motif = PlacedAt(released, false);
            Assert.That(Release(motif), Is.False);
            Assert.That(Vector3.Distance(((Component)motif).transform.position, released), Is.LessThan(Tolerance));
        }

        // AnnotationMotifBase.TryRaycastGround does not
        // invent a floor at world y = 0 (v1 did). Inside the room the MRUK
        // floor answers; outside every room, with no physics ground, there is no ground point.
        [Test]
        public void AnnotationGroundRayUsesTheRoomFloorInsideAndInventsNoFloorOutside()
        {
            var go = new GameObject("annotation");
            _objects.Add(go);
            var motif = go.AddComponent<BoxAnnotationMotif>();
            MethodInfo ground = typeof(AnnotationMotifBase).GetMethod("TryRaycastGround", BindingFlags.Instance | BindingFlags.NonPublic);

            Vector3 inside = Floor.transform.position + new Vector3(0.2f, 1.5f, 0.1f);
            object[] args = { new Ray(inside, Vector3.down), null, null };
            Assert.That((bool)ground.Invoke(motif, args), Is.True);
            Assert.That(((Vector3)args[1]).y, Is.EqualTo(Floor.transform.position.y).Within(Tolerance));

            Vector3 outside = new Vector3(40f, 2f, 40f);
            args = new object[] { new Ray(outside, Vector3.down), null, null };
            Assert.That((bool)ground.Invoke(motif, args), Is.False, "no room and no ground there, so no ground point");
            Assert.That(((Vector3)args[1]), Is.EqualTo(Vector3.zero), "and no invented point is returned");
        }

        // A room MRUK loaded from a prefab (the editor's and an unscanned headset's fallback room, which has no device UUID) is a
        // ready room everywhere: the app registers it with the core at app start and the host shares its biggest TABLE. Nothing
        // waits for a device-scanned room (the retired lobby gate did, forever).
        [UnityTest]
        public IEnumerator APrefabLoadedRoomRegistersAndTheHostSharesItsBiggestTable()
        {
            Internal.UnityMainThreadDispatcher.Initialize();
            typeof(Internal.UnityMainThreadDispatcher).GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            int profile = EditorPrefs.GetInt(FreeformCollaborationApp.EditorProfilePrefKey, int.MinValue);
            EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, (int)FreeformCollaborationRuntimeProfile.Fusion);
            var managerGo = new GameObject("prefab-room-manager");
            managerGo.SetActive(false);
            _objects.Add(managerGo);
            var manager = managerGo.AddComponent<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            var appGo = new GameObject("prefab-room-app");
            appGo.SetActive(false);
            _objects.Add(appGo);
            var app = appGo.AddComponent<FreeformCollaborationApp>();
            try
            {
                Assert.That(MrukRoomTracker.Current(_mruk), Is.SameAs(_room), "a prefab room is the current room");
                Assert.That((bool)typeof(SpatialBlendManager).GetMethod("TryInitializeNativeLibrary", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null), Is.True);
                typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);
                manager.RegisterApp(app);
                manager.HostTunneled();
                Assert.That(manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
                manager.StartApps();
                MethodInfo dispatch = typeof(Internal.UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 30; ++i)
                {
                    dispatch.Invoke(Internal.UnityMainThreadDispatcher.Instance, null);
                    manager.Update();
                    manager.LateUpdate();
                    yield return null;
                }
                Assert.That(manager.RegisteredRoom, Is.Not.Null, "the prefab room is registered");
                Assert.That(manager.RegisteredRoom.Walls.Length, Is.GreaterThan(0));
                List<SceneAnchorRecord> tables = app.ShareableAnchors().Where(FurnitureSharePolicy.IsDesk).ToList();
                Assert.That(tables.Count, Is.EqualTo(2));
                SceneAnchorRecord biggest = tables.OrderBy(r => r, Comparer<SceneAnchorRecord>.Create(FurnitureSharePolicy.CompareDesks)).First();
                Assert.That(tables.Count(r => app.ShareStateOf(r) == AnchorShareState.Shared), Is.EqualTo(1), "exactly one TABLE is shared");
                Assert.That(app.ShareStateOf(biggest), Is.EqualTo(AnchorShareState.Shared), "the biggest");
                Assert.That(biggest.Anchor.GetComponent<SbMRUKAnchorEntity>().IsAttached(), Is.True);
            }
            finally
            {
                manager.Disconnect();
                manager.OnDestroy();
                if (profile == int.MinValue) EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
                else EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, profile);
            }
        }

        // The Share-when-connected menu closes when the room registers and comes back in the lobby.
        [UnityTest]
        public IEnumerator TheFurnitureMenuClosesInASessionAndComesBackInTheLobby()
        {
            Internal.UnityMainThreadDispatcher.Initialize();
            typeof(Internal.UnityMainThreadDispatcher).GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            int profile = EditorPrefs.GetInt(FreeformCollaborationApp.EditorProfilePrefKey, int.MinValue);
            EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, (int)FreeformCollaborationRuntimeProfile.Fusion);
            var managerGo = new GameObject("menu-manager");
            managerGo.SetActive(false);
            _objects.Add(managerGo);
            var manager = managerGo.AddComponent<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            var appGo = new GameObject("menu-app");
            appGo.SetActive(false);
            _objects.Add(appGo);
            var app = appGo.AddComponent<FreeformCollaborationApp>();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.That((bool)typeof(SpatialBlendManager).GetMethod("TryInitializeNativeLibrary", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null), Is.True);
                typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, manager);
                app.RoomProvider = () => _room;
                manager.RegisterApp(app);
                // OnEnable's connection listener (the app's object stays inactive in EditMode, so OnEnable does not run).
                typeof(FreeformCollaborationApp).GetMethod("RegisterConnectionStateListener", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                typeof(FreeformCollaborationApp).GetMethod("OnRoomLoaded", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                var menu = (SpatialBlend.Samples.FreeformCollaboration.UI.FurnitureShareMenu)Field(app, "_furnitureMenu");
                Assert.That(menu, Is.Not.Null, "the lobby offers the room's furniture");
                if (menu != null) _objects.Add(menu.gameObject);
                int offered = menu.AnchorCount;
                Assert.That(offered, Is.GreaterThan(0));
                manager.HostTunneled();
                manager.StartApps();
                MethodInfo dispatch = typeof(Internal.UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 30; ++i)
                {
                    dispatch.Invoke(Internal.UnityMainThreadDispatcher.Instance, null);
                    manager.Update();
                    manager.LateUpdate();
                    yield return null;
                }
                Assert.That(manager.RegisteredRoom, Is.Not.Null);
                Assert.That(menu.AnchorCount, Is.EqualTo(0), "closed in the session");
                manager.Disconnect();
                for (int i = 0; i < 5; ++i)
                {
                    dispatch.Invoke(Internal.UnityMainThreadDispatcher.Instance, null);
                    yield return null;
                }
                Assert.That(menu.AnchorCount, Is.EqualTo(offered), "offered again back in the lobby");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                manager.Disconnect();
                manager.OnDestroy();
                if (profile == int.MinValue) EditorPrefs.DeleteKey(FreeformCollaborationApp.EditorProfilePrefKey);
                else EditorPrefs.SetInt(FreeformCollaborationApp.EditorProfilePrefKey, profile);
            }
        }

        private static object Field(object target, string name) => Find(target.GetType(), name).GetValue(target);
        private static void SetField(object target, string name, object value) => Find(target.GetType(), name).SetValue(target, value);
        private static FieldInfo Find(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            throw new MissingFieldException(name);
        }

        private sealed class RecordingPublisher : ISceneAnchorPublisher
        {
            public GameObject Anchor;
            public bool ShareWithPeers;
            public void Publish(GameObject anchor, bool shareWithPeers) { Anchor = anchor; ShareWithPeers = shareWithPeers; }
        }
    }
}
#endif
