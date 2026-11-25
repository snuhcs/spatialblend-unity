// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SpatialBlend.Internal;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A participant leaves a room whose apps have started and joins it again (the roster is the size it was), between TWO UNITY
    /// PROCESSES over the real Photon Cloud. The rejoined participant's core must link AND its apps must start: the host
    /// announces the start again to a roster whose membership changed. Run by tools/run-two-process-cloud-test.sh with
    /// SPATIALBLEND_TP_SCENARIO=rejoin (the other TwoProcessCloud test ignores that scenario, and this one ignores any other).
    /// Environment as for FusionCoreLinkTwoProcessCloudTests: SPATIALBLEND_TP_ROLE=host|joiner, _ROOM, _MODE=WebRtc|Photon, _DIR.
    /// No scene is loaded: nothing here opens a microphone.
    /// Controls: SPATIALBLEND_TP_SHARE=before has the host share the content while it is alone, so the first visit is a late join; the
    /// core hands a late joiner (and so a rejoiner) the element tree but not the shared payloads, so that control reports
    /// first_visit_content=NOT_DELIVERED by design (it is a report, not an assertion; SPATIALBLEND_TP_ASSERT_CONTENT=1 asserts it).
    /// </summary>
    [TestFixture]
    [Category("TwoProcessCloud")]
    public class FusionCoreLinkRejoinTwoProcessCloudTests
    {
        private sealed class Side
        {
            public NetworkRunner Runner;
            public GameObject ManagerObject, RunnerObject, Template;
            public SpatialBlendManager Manager;
            public FusionConnectionManager Connection;
        }

        private const long RoomShared = 9400, FromHostShared = 9401;
        private const int PayloadBytes = 32 * 1024;
        private readonly List<Side> _sides = new List<Side>();
        private SpatialBlendSettings _settings, _previousSettings;
        private GameObject _template;
        private readonly List<GameObject> _orphans = new List<GameObject>();
        private static readonly NetworkObjectGuid TemplateGuid = NetworkObjectGuid.Parse("c0ffee00-0000-4000-8000-000000000044");

        private static string Env(string name) => Environment.GetEnvironmentVariable("SPATIALBLEND_TP_" + name);

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            EndVisit();
            foreach (var orphan in _orphans) { if (orphan != null) { orphan.GetComponent<SpatialBlendManager>()?.Disconnect(); UnityEngine.Object.Destroy(orphan); } }
            foreach (var side in _sides.ToList()) yield return Leave(side);
            if (_template != null) UnityEngine.Object.Destroy(_template);
            if (_settings != null)
            {
                typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _previousSettings);
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        private static IEnumerator Until(Func<bool> done, string what, float seconds, Func<string> describe = null)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!done())
            {
                if (Time.realtimeSinceStartup > end) NUnitAssert.Fail($"Timed out after {seconds}s waiting for {what}. {describe?.Invoke()}");
                yield return null;
            }
        }

        // Private state read for the log and the apps-started check; a field a build does not have reads as the default.
        private static T Field<T>(object o, string name)
        {
            var field = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? default : (T)field.GetValue(o);
        }

        /// <summary>True when this participant's apps have started in the connection round it is in.</summary>
        private static bool AppsStarted(Side side) =>
            side.Connection != null && Field<long>(side.Connection, "_appsStartedEpoch") > 0 &&
            Field<long>(side.Connection, "_appsStartedEpoch") == side.Connection.CurrentEpoch;

        private static string Describe(Side side, string role)
        {
            var c = side.Connection;
            if (c == null) return $"role={role} (no connection manager)";
            var broadcast = Field<HashSet<PlayerRef>>(c, "_startBroadcastRoster") ?? new HashSet<PlayerRef>(); // a build before the roster key reads as empty
            var ready = Field<HashSet<PlayerRef>>(c, "_readyPlayers") ?? new HashSet<PlayerRef>();
            string structure;
            try { structure = $"room={SpatialBlendAPI.FindElementBySharedId(side.Manager.ID, RoomShared)} element={SpatialBlendAPI.FindElementBySharedId(side.Manager.ID, FromHostShared)}"; }
            catch (Exception e) { structure = "unreadable:" + e.GetType().Name; }
            return $"role={role} found[{structure}] core={side.Manager.ConnectionState} linked={c.LinkedPeerCount} epoch={c.CurrentEpoch} " +
                   $"appsStartedEpoch={Field<long>(c, "_appsStartedEpoch")} startBroadcastEpoch={Field<long>(c, "_startBroadcastEpoch")} " +
                   $"announcedTo=[{string.Join(",", broadcast.Select(p => p.RawEncoded))}] ready=[{string.Join(",", ready.Select(p => p.RawEncoded))}] " +
                   $"players=[{string.Join(",", side.Runner.ActivePlayers.Select(p => p.RawEncoded))}] local={side.Runner.LocalPlayer.RawEncoded}";
        }

        // The room's shared content, as the host shares it: a room element and one element carrying a payload.
        private static void HostShares(Side side)
        {
            var space = side.Manager.ID;
            long room = SpatialBlendAPI.AddElement(space, 0);
            SpatialBlendAPI.SetElementSharedId(space, room, RoomShared);
            Square(space, room);
            SpatialBlendAPI.ShareElement(space, room);
            long element = SpatialBlendAPI.AddElement(space, room);
            SpatialBlendAPI.SetElementSharedId(space, element, FromHostShared);
            var payload = Payload(PayloadBytes, 31);
            var h = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try { SpatialBlendAPI.ShareElement(space, element, h.AddrOfPinnedObject(), payload.Length); } finally { h.Free(); }
        }

        /// <summary>
        /// What the product handed this joiner of the room's content: the manager's own OnElementAdded callback logs every
        /// element it is given ("[V2Share] OnElementAdded ... sharedId=N ... payloadBytes=M"). The test installs no callback of
        /// its own (a second one would replace the product's), it reads that log, from the moment the visit begins.
        /// </summary>
        private sealed class Received
        {
            private static readonly System.Text.RegularExpressions.Regex Line =
                new System.Text.RegularExpressions.Regex(@"\[V2Share\] OnElementAdded .*sharedId=(\d+) .*payloadBytes=(\d+)");
            private readonly Dictionary<long, long> _bytes = new Dictionary<long, long>();
            public void OnLog(string condition, string stack, UnityEngine.LogType type)
            {
                var m = Line.Match(condition ?? "");
                if (m.Success) lock (_bytes) _bytes[long.Parse(m.Groups[1].Value)] = long.Parse(m.Groups[2].Value);
            }
            public bool Has(long id) { lock (_bytes) return _bytes.ContainsKey(id); }
            public bool HasPayload(long id, long bytes) { lock (_bytes) return _bytes.TryGetValue(id, out var b) && b == bytes; }
            public string Seen() { lock (_bytes) return string.Join(",", _bytes.Select(kv => kv.Key + ":" + kv.Value)); }
        }

        private Received _visit;

        private Received BeginVisit()
        {
            EndVisit();
            _visit = new Received();
            Application.logMessageReceivedThreaded += _visit.OnLog;
            return _visit;
        }

        private void EndVisit()
        {
            if (_visit != null) Application.logMessageReceivedThreaded -= _visit.OnLog;
            _visit = null;
        }

        /// <summary>True when the room element and the host's payload (its full size) have been handed to this participant.</summary>
        private static bool ContentArrived(Received received) =>
            received.Has(RoomShared) && received.HasPayload(FromHostShared, PayloadBytes);

        private static void Square(long space, long element) =>
            SpatialBlendAPI.SetElementPolygon(space, element, SbGeometryRole.Navigable, new[]
            { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100) });

        private static byte[] Payload(int size, uint seed)
        {
            var b = new byte[size]; uint x = seed * 2654435761u + 1;
            for (int i = 0; i < size; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; b[i] = (byte)(x >> 11); }
            return b;
        }

        private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)); }

        // SPATIALBLEND_TP_LEAVE=graceful (default): the product's own leave, the core link is closed first and then the runner shuts
        // down. abrupt: the runner goes first and the core link is never closed from this side (a headset that is taken off, an
        // app that is killed): the host learns of the departure from Fusion alone.
        private IEnumerator Leave(Side side)
        {
            _sides.Remove(side);
            bool abrupt = Env("LEAVE") == "abrupt";
            if (!abrupt && side.ManagerObject != null) side.ManagerObject.GetComponent<SpatialBlendManager>()?.Disconnect();
            if (side.Runner != null) { var t = side.Runner.Shutdown(); while (!t.IsCompleted) yield return null; }
            if (side.ManagerObject != null)
            {
                if (abrupt) _orphans.Add(side.ManagerObject); // its core session is left open: nothing closes the link from this side
                else UnityEngine.Object.Destroy(side.ManagerObject);
            }
            if (side.RunnerObject != null) UnityEngine.Object.Destroy(side.RunnerObject);
            side.Connection = null;
        }

        // One participant, as the product builds it: SpatialBlendManager, a runner, and the connection manager as a networked object.
        private IEnumerator Join(Side side, string role, string room, bool host, string dir)
        {
            var settings = _settings;
            side.ManagerObject = new GameObject("tp-core-" + role); side.ManagerObject.SetActive(false);
            side.Manager = side.ManagerObject.AddComponent<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(side.Manager, false);
            side.ManagerObject.SetActive(true);
            // The manager's Awake loads the project's own settings asset into CurrentSettings: this run's declared carrier mode is set after it.
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);

            side.RunnerObject = new GameObject("tp-runner-" + role);
            side.Runner = side.RunnerObject.AddComponent<NetworkRunner>();
            side.Runner.AddCallbacks(new SceneEvents(
                (runner, player) => { if (side.Connection != null) side.Connection.OnPlayerJoined(runner, player); },
                (runner, player) => { if (side.Connection != null) side.Connection.OnPlayerLeft(runner, player); },
                () => { if (side.Connection != null) side.Connection.ShutdownConnection(); }));
            // One baked template for the whole process (its prefab source is registered once for this GUID); each visit's copy of
            // the connection manager is pointed at that visit's SpatialBlendManager.
            if (_template == null)
            {
                _template = new GameObject("FusionConnectionManagerTemplate");
                _template.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_template);
                _template.AddComponent<NetworkObject>();
                _template.AddComponent<FusionConnectionManager>();
                new NetworkObjectBaker().Bake(_template);
            }
            side.Template = _template;
            var template = _template.GetComponent<FusionConnectionManager>();

            var start = side.Runner.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room,
                PlayerCount = SpatialBlendSettings.DefaultMaxPlayers, SceneManager = side.RunnerObject.AddComponent<NetworkSceneManagerDefault>() });
            yield return Until(() => start.IsCompleted, "StartGame", 60);
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            // The prefab table outlives a runner: the second visit finds the source the first one registered.
            if (!side.Runner.Prefabs.TryAddSource(new NetworkPrefabSourceStatic
                { Object = side.Template.GetComponent<NetworkObject>(), AssetGuid = TemplateGuid }, out var prefabId))
                prefabId = side.Runner.Prefabs.GetId(TemplateGuid);
            if (host)
            {
                side.Connection = side.Runner.Spawn(prefabId).GetComponent<FusionConnectionManager>();
                typeof(FusionConnectionManager).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(side.Connection, side.Manager);
                side.Connection.gameObject.SetActive(true);
                side.Connection.OnPlayerJoined(side.Runner, side.Runner.LocalPlayer);
            }
            else
            {
                yield return Until(() => (side.Connection = UnityEngine.Object.FindObjectsByType<FusionConnectionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(m => m != template && m.Runner == side.Runner)) != null, "the connection manager", 120);
                typeof(FusionConnectionManager).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(side.Connection, side.Manager);
                side.Connection.gameObject.SetActive(true);
            }
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator ALeaveAndARejoinOfTheSameRosterSizeStartsTheRejoinedAppsAgain()
        {
            var role = Env("ROLE");
            if (string.IsNullOrEmpty(role) || Env("SCENARIO") != "rejoin") NUnitAssert.Ignore("Not a rejoin two-process run.");
            var room = Env("ROOM"); var mode = Env("MODE"); var dir = Env("DIR");
            bool host = role == "host";
            // A leave is an event of this scenario: Fusion logs the departed player's disconnect as an error on both sides.
            LogAssert.ignoreFailingMessages = true;
            if (!PhotonAppSettings.TryGetGlobal(out var photon) || !Guid.TryParse(photon.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Single;
            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _settings.useWebRtc = mode != "Photon";
            _previousSettings = SpatialBlendManager.CurrentSettings;
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);
            Func<string, string> file = name => Path.Combine(dir, name);
            var t0 = Time.realtimeSinceStartup;
            Func<float> now = () => Time.realtimeSinceStartup - t0;

            var side = new Side(); _sides.Add(side);
            if (!host) BeginVisit();
            if (!host) yield return Until(() => File.Exists(file("host-ready")), "the host to be in the room", 240);
            yield return Join(side, role, room, host, dir);
            // SPATIALBLEND_TP_SHARE=before: the host shares the room's content while it is alone, so the first visit is itself a late
            // join (the control for the rejoin: is content replayed to a joiner who comes after it was shared?).
            bool shareBefore = Env("SHARE") == "before";
            bool assertContent = Env("ASSERT_CONTENT") == "1";
            if (host && shareBefore) HostShares(side);
            if (host) File.WriteAllText(file("host-ready"), "1");
            Func<string> describe = () => Describe(side, role);
            TestContext.Out.WriteLine($"TWOPROC role={role} pid={System.Diagnostics.Process.GetCurrentProcess().Id} mode={mode} scenario=rejoin joined t={now():0.00} {describe()}");

            // Round 1: both players here, the core links and the apps start.
            yield return Until(() => side.Runner.ActivePlayers.Count() == 2, "both players", 240, describe);
            yield return Until(() => side.Manager.ConnectionState == SpatialBlendConnectionState.Connected && side.Connection.LinkedPeerCount == 1, "the core link", 120, describe);
            TestContext.Out.WriteLine($"TWOPROC role={role} first_core_connected t={now():0.00}");
            yield return Until(() => AppsStarted(side), "the apps to start (first join)", 60, describe);
            TestContext.Out.WriteLine($"TWOPROC role={role} first_apps_started t={now():0.00} {describe()}");
            File.WriteAllText(file(role + "-started-1"), "1");
            yield return Until(() => File.Exists(file("host-started-1")) && File.Exists(file("joiner-started-1")), "the peer's first start", 60, describe);

            // The room's content, shared once the apps run on both sides. First visit: everything arrives.
            Received received = null;
            if (!host) received = _visit;
            File.WriteAllText(file(role + "-subscribed-1"), "1");
            yield return Until(() => File.Exists(file("host-subscribed-1")) && File.Exists(file("joiner-subscribed-1")), "both sides subscribed", 60, describe);
            if (host)
            {
                if (!shareBefore) HostShares(side);
                TestContext.Out.WriteLine($"TWOPROC role=host shared_content t={now():0.00}");
                yield return Until(() => File.Exists(file("joiner-content-1")), "the joiner's first-visit content", 120, describe);
            }
            else
            {
                if (shareBefore || !assertContent)
                {
                    // Content shared BEFORE the joiner came is the late-join case, which the core does not serve (core gap, see the rejoin step).
                    var softEnd = Time.realtimeSinceStartup + 20f;
                    while (!ContentArrived(received) && Time.realtimeSinceStartup < softEnd) yield return null;
                    TestContext.Out.WriteLine($"TWOPROC role=joiner first_visit_content={(ContentArrived(received) ? "arrived" : "NOT_DELIVERED")} shared_before_join={shareBefore} t={now():0.00} handed=[{received.Seen()}]");
                }
                else yield return Until(() => ContentArrived(received), "the host's content (first visit)", 90, describe);
                TestContext.Out.WriteLine($"TWOPROC role=joiner first_content_step_done t={now():0.00}");
                File.WriteAllText(file("joiner-content-1"), "1");
            }

            if (host)
            {
                // The joiner leaves; the host keeps its round (the apps already started) and sees the room at one.
                yield return Until(() => side.Runner.ActivePlayers.Count() == 1, "the joiner to leave", 120, describe);
                TestContext.Out.WriteLine($"TWOPROC role=host saw_leave t={now():0.00} {describe()}");
                File.WriteAllText(file("host-saw-leave"), "1");
                yield return Until(() => side.Runner.ActivePlayers.Count() == 2, "the joiner to come back (the roster is the size it was)", 120, describe);
                TestContext.Out.WriteLine($"TWOPROC role=host saw_rejoin t={now():0.00} {describe()}");
                // Non-empty: the joiner creates the file and then writes it, and a read in between sees nothing.
                yield return Until(() => File.Exists(file("joiner-rejoin-result")) && new FileInfo(file("joiner-rejoin-result")).Length > 0,
                    "the rejoined joiner's result", 200, describe);
                var result = File.ReadAllText(file("joiner-rejoin-result"));
                TestContext.Out.WriteLine($"TWOPROC role=host rejoin_result={result} t={now():0.00} {describe()}");
                NUnitAssert.That(result, Is.EqualTo("ok"), "the rejoined joiner's core linked; its apps must start and the room's shared content must arrive: " + describe());
            }
            else
            {
                float leftAt = now();
                yield return Leave(side);
                TestContext.Out.WriteLine($"TWOPROC role=joiner left t={now():0.00}");
                yield return Until(() => File.Exists(file("host-saw-leave")), "the host to see the leave", 120);
                side = new Side(); _sides.Add(side);
                BeginVisit();
                float rejoinAt = now();
                yield return Join(side, role, room, false, dir);
                TestContext.Out.WriteLine($"TWOPROC role=joiner rejoined_room t={now():0.00} {describe()}");
                yield return Until(() => side.Runner.ActivePlayers.Count() == 2, "both players again", 120, describe);
                float rejoinTimeout = 90f; var deadline = Time.realtimeSinceStartup + rejoinTimeout;
                while (!(side.Manager.ConnectionState == SpatialBlendConnectionState.Connected && side.Connection.LinkedPeerCount == 1))
                {
                    if (Time.realtimeSinceStartup > deadline) { File.WriteAllText(file("joiner-rejoin-result"), "core-not-connected"); NUnitAssert.Fail("core did not link after the rejoin. " + describe()); }
                    yield return null;
                }
                float coreAt = now();
                var rejoinReceived = _visit;
                TestContext.Out.WriteLine($"TWOPROC role=joiner rejoin_core_connected t={coreAt:0.00} since_rejoin_s={coreAt - rejoinAt:0.00} {describe()}");
                deadline = Time.realtimeSinceStartup + rejoinTimeout;
                while (!AppsStarted(side))
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        File.WriteAllText(file("joiner-rejoin-result"), "apps-never-started");
                        TestContext.Out.WriteLine($"TWOPROC role=joiner SYMPTOM core connected, apps never started after {rejoinTimeout}s. {describe()}");
                        NUnitAssert.Fail($"core connected but the apps never started within {rejoinTimeout}s of the rejoin. " + describe());
                    }
                    yield return null;
                }
                float appsAt = now();
                TestContext.Out.WriteLine($"TWOPROC role=joiner rejoin_apps_started t={appsAt:0.00} core_to_apps_s={appsAt - coreAt:0.00} since_rejoin_s={appsAt - rejoinAt:0.00} {describe()}");
                // The existing shared entities for the returning participant. The apps are the assertion of this scenario (the host's
                // start announcement). Content is reported always and asserted with SPATIALBLEND_TP_ASSERT_CONTENT=1: the core's bootstrap
                // carries the element tree and not the shared payloads, and a payload is handed only to a client subscribed when it is
                // shared, so a rejoiner (like any late joiner) is handed none. That is a core gap, not the connection manager's.
                float contentEnd = Time.realtimeSinceStartup + (assertContent ? rejoinTimeout : 20f);
                while (!ContentArrived(rejoinReceived) && Time.realtimeSinceStartup < contentEnd) yield return null;
                bool contentArrived = ContentArrived(rejoinReceived);
                TestContext.Out.WriteLine($"TWOPROC role=joiner rejoin_content={(contentArrived ? "arrived" : "NOT_DELIVERED")} t={now():0.00} since_apps_s={now() - appsAt:0.00} handed=[{rejoinReceived.Seen()}] {describe()}");
                if (!contentArrived && assertContent)
                {
                    File.WriteAllText(file("joiner-rejoin-result"), "content-never-arrived");
                    NUnitAssert.Fail($"the apps started but the host's shared content was not handed to the rejoined participant within {rejoinTimeout}s. " + describe());
                }
                File.WriteAllText(file("joiner-rejoin-result"), "ok");
            }
            TestContext.Out.WriteLine($"TWOPROC role={role} DONE {describe()}");
            LogAssert.ignoreFailingMessages = true;
        }

        private sealed class SceneEvents : INetworkRunnerCallbacks
        {
            private readonly Action<NetworkRunner, PlayerRef> _joined, _left;
            private readonly Action _shutdown;
            public SceneEvents(Action<NetworkRunner, PlayerRef> joined, Action<NetworkRunner, PlayerRef> left, Action shutdown)
            { _joined = joined; _left = left; _shutdown = shutdown; }
            public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) => _joined(runner, player);
            public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) => _left(runner, player);
            public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) => _shutdown();
            public void OnInput(NetworkRunner runner, NetworkInput input) { }
            public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
            public void OnConnectedToServer(NetworkRunner runner) { }
            public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
            public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
            public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
            public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
            public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
            public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
            public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
            public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
            public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
            public void OnSceneLoadDone(NetworkRunner runner) { }
            public void OnSceneLoadStart(NetworkRunner runner) { }
            public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
            public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        }
    }
}
