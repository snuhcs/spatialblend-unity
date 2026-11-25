// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Objects;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Seen on device: a peer that LEAVES a session and JOINS it again receives the host's boards but not its shared scan
    /// (a native glTF object). The host and the peers here are real managers on the REAL core library, joined over tunneled links
    /// that the test shuttles; a peer leaves as the app's Leave does (its received objects are destroyed with its scene, then it
    /// disconnects) and a new peer joins the same host. The scan is made from the scan template (ObjectPreset, as the app's
    /// <see cref="SpatialBlend.Objects.GltfNativeLoader.TemplateFactory"/> makes it) and, as a control, as a bare object.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SharedScanRejoinTests
    {
        private const string ScanTemplate = "Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab";
        private static readonly FieldInfo ConnectOnAwake = typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo InitLibrary = typeof(SpatialBlendManager).GetMethod("TryInitializeNativeLibrary", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo DispatchUpdate = typeof(UnityMainThreadDispatcher).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AppManager = typeof(SbApp).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Vector2[] Floor = { new Vector2(-2f, -2f), new Vector2(2f, -2f), new Vector2(2f, 2f), new Vector2(-2f, 2f) };

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private readonly List<SpatialBlendManager> _managers = new List<SpatialBlendManager>();
        private Func<string, GameObject> _factoryWas;
        private bool _ignoreFailing;
        private SpatialBlendManager _host;

        private sealed class PeerApp : SbApp
        {
            public readonly List<GameObject> Received = new List<GameObject>();
            public override void OnAppStart() { }
            public override OnShareResponse OnShare(GameObject value) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject value, OnObjectAddedType type)
            {
                if (type == OnObjectAddedType.Added) Received.Add(value);
            }
            public override void OnRemove(GameObject value) => Received.Remove(value);
        }

        private sealed class Peer
        {
            public SpatialBlendManager Manager;
            public PeerApp App;
            public CoreLink HostSide, PeerSide;
            public readonly List<byte> ToPeer = new List<byte>(), ToHost = new List<byte>();
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _factoryWas = SpatialBlend.Objects.GltfNativeLoader.TemplateFactory;
            UnityMainThreadDispatcher.Initialize();
            typeof(UnityMainThreadDispatcher).GetField("_mainThreadId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            _host = NewManager("rejoin-host");
            _host.HostTunneled();
            Assert.That(_host.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), _host.ConnectionError);
            RegisterRoom(_host);
        }

        [TearDown]
        public void TearDown()
        {
            // The peers' received copies first: a disconnect retires them with Destroy, which EditMode refuses.
            foreach (SbEntity left in UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (left != null && left.IsAttached() && !left.IsSource) UnityEngine.Object.DestroyImmediate(left.transform.root.gameObject);
            foreach (SpatialBlendManager m in Enumerable.Reverse(_managers))
            {
                try { m.Disconnect(); } catch (Exception e) { Debug.LogWarning("disconnect: " + e.Message); }
                try { m.OnDestroy(); } catch (Exception e) { Debug.LogWarning("destroy: " + e.Message); }
            }
            _managers.Clear();
            for (int i = _objects.Count - 1; i >= 0; --i)
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            foreach (SbEntity left in UnityEngine.Object.FindObjectsByType<SbEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (left != null) UnityEngine.Object.DestroyImmediate(left.transform.root.gameObject);
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = _factoryWas;
            // Unity checks the teardown's log after it returns: the replicas' cleanup logs EditMode's Destroy warning there.
            LogAssert.ignoreFailingMessages = true;
        }

        [OneTimeSetUp]
        public void KeepLogAssert() => _ignoreFailing = LogAssert.ignoreFailingMessages;

        [OneTimeTearDown]
        public void RestoreLogAssert() => LogAssert.ignoreFailingMessages = _ignoreFailing;

        private SpatialBlendManager NewManager(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            _objects.Add(go);
            SpatialBlendManager manager = go.AddComponent<SpatialBlendManager>();
            ConnectOnAwake.SetValue(manager, false);
            Assert.That((bool)InitLibrary.Invoke(manager, null), Is.True, "the native library");
            _managers.Add(manager);
            return manager;
        }

        private static void RegisterRoom(SpatialBlendManager manager)
        {
            Assert.That(manager.RegisterRoomRegion(RoomRegion.Build(Floor, null, Array.Empty<Vector2[]>())), Is.True, "the room");
            manager.MarkLocalSceneGeometryRegistered();
        }

        // The scan template, as the app's hook makes it (EditMode has no Addressables: the prefab is loaded directly).
        private GameObject ScanTemplateObject(string name)
        {
            GameObject go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ScanTemplate));
            go.name = name;
            return go;
        }

        private GameObject ShareScan(bool fromScanTemplate)
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = fromScanTemplate ? ScanTemplateObject : (Func<string, GameObject>)null;
            GameObject scan = NativeLoader.GetLoader(NativeContentType.GLTF).CreateTemplate("partner-scan");
            _objects.Add(scan);
            scan.transform.position = new Vector3(-1f, 0f, -1f);
            var entity = scan.GetComponent<SbNativeEntity>();
            // EditMode runs no Awake: the content and box blocks are added as Awake adds them.
            typeof(SbNativeEntity).GetMethod("EnsureSerializationBlocks", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
            entity.SetContentData(new byte[] { 0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0 });
            _host.ShareGameObject(scan, true);
            for (int f = 0; f < 10 && !entity.IsAttached(); ++f) Pump(_host);
            Assert.That(entity.IsAttached(), Is.True, "the host shared its scan");
            return scan;
        }

        private Peer Join(string name)
        {
            var peer = new Peer { Manager = NewManager(name) };
            var appGo = new GameObject(name + "-app");
            appGo.SetActive(false);
            _objects.Add(appGo);
            peer.App = appGo.AddComponent<PeerApp>();
            AppManager.SetValue(peer.App, peer.Manager);
            peer.Manager.RegisterApp(peer.App);
            peer.HostSide = _host.AcceptCoreLink();
            peer.PeerSide = peer.Manager.JoinTunneled();
            Assert.That(peer.HostSide, Is.Not.Null);
            Assert.That(peer.PeerSide, Is.Not.Null);
            return peer;
        }

        private IEnumerator Run(Peer peer, int frames, Func<bool> until = null)
        {
            for (int f = 0; f < frames && (until == null || !until()); ++f)
            {
                Move(peer.HostSide, peer.PeerSide, peer.ToPeer);
                Move(peer.PeerSide, peer.HostSide, peer.ToHost);
                if (peer.Manager.IsJoining) peer.Manager.PollTunneledJoin();
                Pump(_host);
                Pump(peer.Manager);
                LogAssert.ignoreFailingMessages = true;
                yield return null;
            }
        }

        private static GameObject ReceivedScan(Peer peer) =>
            peer.App.Received.FirstOrDefault(g => g != null && g.name == "partner-scan");

        // What the app's Leave does to a participant: its scene (and so every object it received) is destroyed, its core disconnects,
        // and the host closes the leaver's link.
        private void Leave(Peer peer)
        {
            foreach (GameObject received in peer.App.Received.ToList())
                if (received != null) UnityEngine.Object.DestroyImmediate(received);
            peer.Manager.Disconnect();
            peer.Manager.OnDestroy();
            _managers.Remove(peer.Manager);
            peer.HostSide.Close();
        }

        private IEnumerator AJoinerThatLeavesAndJoinsAgainReceivesTheScan(bool fromScanTemplate)
        {
            GameObject scan = ShareScan(fromScanTemplate);
            Peer first = Join("rejoin-peer-1");
            RegisterRoomWhenConnected(first);
            yield return Run(first, 3000, () => first.Manager.ConnectionState == SpatialBlendConnectionState.Connected && ReceivedScan(first) != null);
            Assert.That(first.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), first.Manager.ConnectionError);
            Assert.That(ReceivedScan(first), Is.Not.Null, "the first visit receives the host's scan");

            Leave(first);
            for (int f = 0; f < 30; ++f) { Pump(_host); yield return null; }
            Assert.That(scan.GetComponent<SbNativeEntity>().IsAttached(), Is.True, "the host's scan is intact after the leave");

            Peer again = Join("rejoin-peer-2");
            RegisterRoomWhenConnected(again);
            yield return Run(again, 3000, () => again.Manager.ConnectionState == SpatialBlendConnectionState.Connected && ReceivedScan(again) != null);
            Assert.That(again.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), again.Manager.ConnectionError);
            TestContext.WriteLine($"rejoin: received {string.Join(", ", again.App.Received.Where(g => g != null).Select(g => g.name))}; " +
                                  $"entities {again.Manager.Entities.Count}");
            Assert.That(ReceivedScan(again), Is.Not.Null, "the joiner receives the host's scan again after it left and rejoined");
        }

        // A participant registers its own room once it is connected (the app's start).
        private void RegisterRoomWhenConnected(Peer peer)
        {
            void OnState(SpatialBlendConnectionState state)
            {
                if (state != SpatialBlendConnectionState.Connected) return;
                peer.Manager.ConnectionStateChanged -= OnState;
                RegisterRoom(peer.Manager);
            }
            peer.Manager.ConnectionStateChanged += OnState;
        }

        [UnityTest]
        public IEnumerator ARejoinerReceivesAScanMadeFromTheScanTemplate() => AJoinerThatLeavesAndJoinsAgainReceivesTheScan(true);

        [UnityTest]
        public IEnumerator ARejoinerReceivesABareScan() => AJoinerThatLeavesAndJoinsAgainReceivesTheScan(false);

        // The receiver's own template hook fails (as the app's did after a Leave: a new loader with no preset key threw inside
        // Addressables). The peer's object still arrives, bare; before, the share was dropped and never came back.
        [UnityTest]
        public IEnumerator AReceiverWhoseAppTemplateFailsStillReceivesTheScan()
        {
            ShareScan(false);
            Peer peer = Join("failing-template-peer");
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = name => throw new ArgumentNullException("key");
            yield return Run(peer, 3000, () => peer.Manager.ConnectionState == SpatialBlendConnectionState.Connected && ReceivedScan(peer) != null);
            Assert.That(peer.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), peer.Manager.ConnectionError);
            Assert.That(ReceivedScan(peer), Is.Not.Null, "the scan arrives although the app's template failed");
            Assert.That(ReceivedScan(peer).GetComponent<SbNativeEntity>(), Is.Not.Null);
        }

        [Test]
        public void AThrowingAppTemplateGivesABareObject()
        {
            SpatialBlend.Objects.GltfNativeLoader.TemplateFactory = name => throw new InvalidOperationException("no template");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("(?s)template for 'throwing-template' failed; a bare object is used.*InvalidOperationException: no template"));
            GameObject made = new SpatialBlend.Objects.GltfNativeLoader().CreateTemplate("throwing-template");
            _objects.Add(made);
            Assert.That(made, Is.Not.Null);
            Assert.That(made.GetComponent<SbNativeEntity>(), Is.Not.Null, "the bare copy");
            Assert.That(made.name, Is.EqualTo("throwing-template"));
        }

        // The scan loader is made on demand in the scene, so a Leave (which reloads the scene) destroys it and the next scan or
        // received copy makes a new one. That one read no settings (an earlier loader had set the shared field), so it had no
        // preset key and could not make the scan template.
        [Test]
        public void ALoaderMadeAfterTheFirstWasDestroyedStillHasThePresetKey()
        {
            PropertyInfo current = typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public);
            FieldInfo shared = typeof(SpatialBlend.Samples.Objects.GltfLoader).GetField("_settings", BindingFlags.Static | BindingFlags.NonPublic);
            FieldInfo key = typeof(SpatialBlend.Samples.Objects.GltfLoader).GetField("_presetAddressKey", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo awake = typeof(SpatialBlend.Samples.Objects.GltfLoader).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            var settingsWas = (SpatialBlendSettings)current.GetValue(null);
            object sharedWas = shared.GetValue(null);
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            settings.objectPresetAddressKey = "scan-template-key";
            try
            {
                current.SetValue(null, settings);
                shared.SetValue(null, null);
                for (int visit = 1; visit <= 2; ++visit)
                {
                    var go = new GameObject("Object Loader " + visit);
                    var loader = go.AddComponent<SpatialBlend.Samples.Objects.GltfLoader>();
                    awake.Invoke(loader, null);   // EditMode runs no Awake
                    Assert.That(key.GetValue(loader), Is.EqualTo("scan-template-key"), $"loader {visit} has the preset key");
                    UnityEngine.Object.DestroyImmediate(go);   // the scene reloads (a Leave)
                }
            }
            finally
            {
                current.SetValue(null, settingsWas);
                shared.SetValue(null, sharedWas);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // ---- The app's real hook: Samples' GltfNativeLoader -> GltfLoader (Addressables, the ObjectPreset scan template) ----

        private static readonly FieldInfo LoaderInstance = typeof(SpatialBlend.Samples.Objects.GltfLoader).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo LoaderSettings = typeof(SpatialBlend.Samples.Objects.GltfLoader).GetField("_settings", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly PropertyInfo CurrentSettings = typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public);

        // A received copy the user can grab and place: the scan template's collider, ray interactable and placement motif.
        private static void AssertGrabbable(GameObject copy, string what)
        {
            Assert.That(copy, Is.Not.Null, what);
            Assert.That(copy.GetComponentInChildren<Collider>(true), Is.Not.Null, what + ": a collider");
            Assert.That(copy.GetComponentInChildren<Oculus.Interaction.RayInteractable>(true), Is.Not.Null, what + ": the ray interactable");
            Assert.That(copy.GetComponentInChildren<SpatialBlend.Interactions.ObjectPlacementMotif>(true), Is.Not.Null, what + ": the placement motif");
        }

        // The scene's loader goes with the scene (a Leave reloads it); the next copy makes a new one.
        private static void DestroyTheScenesLoader()
        {
            var loader = (SpatialBlend.Samples.Objects.GltfLoader)LoaderInstance.GetValue(null);
            if (loader != null) UnityEngine.Object.DestroyImmediate(loader.gameObject);
        }

        // The scene's loader as play mode makes it on demand (GltfLoader.EnsureInstance: AddComponent, which runs Awake there;
        // EditMode runs no Awake, so it is invoked here).
        private void MakeTheScenesLoader()
        {
            var go = new GameObject("Object Loader");
            var loader = go.AddComponent<SpatialBlend.Samples.Objects.GltfLoader>();
            typeof(SpatialBlend.Samples.Objects.GltfLoader).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loader, null);
            Assert.That(LoaderInstance.GetValue(null), Is.SameAs(loader));
        }

        // Runs `body` with the app's own hook and settings, as a fresh app start: no loader yet, the settings not yet read.
        private IEnumerator WithTheAppsHook(Func<IEnumerator> body)
        {
            var settingsWas = (SpatialBlendSettings)CurrentSettings.GetValue(null);
            object loaderSettingsWas = LoaderSettings.GetValue(null);
            var settings = Resources.Load<SpatialBlendSettings>("SpatialBlendSettings");
            Assert.That(settings, Is.Not.Null, "the project's SpatialBlendSettings");
            Assert.That(settings.objectPresetAddressKey, Is.Not.Empty);
            DestroyTheScenesLoader();
            CurrentSettings.SetValue(null, settings);
            LoaderSettings.SetValue(null, null);
            SpatialBlend.Samples.Objects.GltfNativeLoader.RegisterReceivedCopyTemplate();
            try
            {
                yield return body();
            }
            finally
            {
                DestroyTheScenesLoader();
                CurrentSettings.SetValue(null, settingsWas);
                LoaderSettings.SetValue(null, loaderSettingsWas);
            }
        }

        // The copy the app's hook makes is the scan template on the first visit and again after the scene reloaded.
        [UnityTest]
        public IEnumerator TheAppsHookMakesAGrabbableCopyBeforeAndAfterTheSceneReloads() => WithTheAppsHook(() => BothVisitsGrabbable());

        private IEnumerator BothVisitsGrabbable()
        {
            var errors = new List<string>();
            Application.LogCallback note = (message, stack, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(message); };
            Application.logMessageReceived += note;
            try
            {
                for (int visit = 1; visit <= 2; ++visit)
                {
                    MakeTheScenesLoader();
                    GameObject copy = new SpatialBlend.Objects.GltfNativeLoader().CreateTemplate("partner-scan-" + visit);
                    _objects.Add(copy);
                    AssertGrabbable(copy, $"visit {visit}");
                    Assert.That(errors, Is.Empty, $"visit {visit}: no template error (a bare copy, an Addressables failure)");
                    DestroyTheScenesLoader();   // the Leave
                    yield return null;
                }
            }
            finally
            {
                Application.logMessageReceived -= note;
            }
        }

        // End to end on the real core: a peer receives the host's scan through the app's hook, leaves (its scene, the scan loader
        // with it, is destroyed), and a peer joining again receives it as the grabbable scan template, with no bare-copy error.
        [UnityTest]
        public IEnumerator ARejoinerReceivesTheScanAsTheGrabbableTemplateThroughTheAppsHook() => WithTheAppsHook(() => RejoinThroughTheAppsHook());

        private IEnumerator RejoinThroughTheAppsHook()
        {
            var bare = new List<string>();
            Application.LogCallback note = (message, stack, type) => { if (message.Contains("a bare object is used")) bare.Add(message); };
            Application.logMessageReceived += note;
            try
            {
                MakeTheScenesLoader();
                GameObject scan = NativeLoader.GetLoader(NativeContentType.GLTF).CreateTemplate("partner-scan");
                _objects.Add(scan);
                scan.transform.position = new Vector3(-1f, 0f, -1f);
                var entity = scan.GetComponent<SbNativeEntity>();
                typeof(SbNativeEntity).GetMethod("EnsureSerializationBlocks", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entity, null);
                entity.SetContentData(new byte[] { 0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0 });
                _host.ShareGameObject(scan, true);
                for (int f = 0; f < 10 && !entity.IsAttached(); ++f) Pump(_host);
                Assert.That(entity.IsAttached(), Is.True, "the host shared its scan");

                Peer first = Join("hook-peer-1");
                RegisterRoomWhenConnected(first);
                yield return Run(first, 3000, () => first.Manager.ConnectionState == SpatialBlendConnectionState.Connected && ReceivedScan(first) != null);
                AssertGrabbable(ReceivedScan(first), "the first visit's copy");

                Leave(first);
                DestroyTheScenesLoader();
                MakeTheScenesLoader();   // the reloaded scene's loader, as the next copy makes it
                for (int f = 0; f < 30; ++f) { Pump(_host); yield return null; }

                Peer again = Join("hook-peer-2");
                RegisterRoomWhenConnected(again);
                yield return Run(again, 3000, () => again.Manager.ConnectionState == SpatialBlendConnectionState.Connected && ReceivedScan(again) != null);
                Assert.That(again.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), again.Manager.ConnectionError);
                AssertGrabbable(ReceivedScan(again), "the rejoined peer's copy");
                Assert.That(bare, Is.Empty, "no copy fell back to a bare object");
            }
            finally
            {
                Application.logMessageReceived -= note;
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

        private static void Pump(SpatialBlendManager manager)
        {
            DispatchUpdate.Invoke(UnityMainThreadDispatcher.Instance, null);
            manager.Update();
            manager.LateUpdate();
        }
    }
}
#endif
