// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The core link between TWO UNITY PROCESSES, one runner each, joined to one room by name, over the real Photon Cloud.
    /// Identity is the product's capability handshake: a token offered over a targeted RPC whose sender is Photon's
    /// RpcInfo.Source (the callback's PlayerRef names the receiver, so it is only logged). Run once per role by tools/run-two-process-cloud-test.sh; ignored otherwise.
    /// Environment: SPATIALBLEND_TP_ROLE=host|joiner, _ROOM, _MODE=WebRtc|Photon|WebRtcThatCannotConnect|WebRtcRelayed, _DIR (a directory both
    /// processes can see). WebRtcTurn is TURN configured but not forced (the product's setting; the path should be Direct); WebRtcRelayed forces iceTransportPolicy=relay with the TURN servers of the credentials Worker (the client
    /// secret read from the file SPATIALBLEND_TURN_CLIENT_SECRET_FILE names, never printed), and proves the TURN path carries the link; _TURN_TRANSPORTS=udp|tcp|tls|all narrows which.
    /// </summary>
    [TestFixture]
    [Category("TwoProcessCloud")]
    public class FusionCoreLinkTwoProcessCloudTests
    {
        private const int Epoch = 1;
        private const long RoomShared = 9300, FromHostShared = 9301, FromJoinerShared = 9302;
        private NetworkRunner _runner;
        private GameObject _managerObject;
        private GameObject _runnerObject;
        private SpatialBlendSettings _settings, _previousSettings;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // The runner's shutdown is the end of the run: the peer's Photon client logs the disconnect as an error, at any
            // point of either side's teardown. Left set, because the process ends with this test.
            LogAssert.ignoreFailingMessages = true;
            if (_managerObject != null) { _managerObject.GetComponent<SpatialBlendManager>()?.Disconnect(); UnityEngine.Object.Destroy(_managerObject); }
            if (_runner != null) { var t = _runner.Shutdown(); while (!t.IsCompleted) yield return null; }
            if (_runnerObject != null) UnityEngine.Object.Destroy(_runnerObject);
            if (_settings != null)
            {
                typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _previousSettings);
                UnityEngine.Object.DestroyImmediate(_settings);
            }
        }

        private static string Env(string name) => Environment.GetEnvironmentVariable("SPATIALBLEND_TP_" + name);

        private static IEnumerator Until(Func<bool> done, string what, float seconds, Func<string> describe = null)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!done())
            {
                if (Time.realtimeSinceStartup > end) NUnitAssert.Fail($"Timed out after {seconds}s waiting for {what}. {describe?.Invoke()}");
                yield return null;
            }
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator OneSideOfTheLink()
        {
            var role = Env("ROLE");
            if (string.IsNullOrEmpty(role) || Env("SCENARIO") == "rejoin" || Env("SCENARIO") == "avatar" || Env("SCENARIO") == "edit") NUnitAssert.Ignore("Not a two-process link run.");
            var room = Env("ROOM"); var mode = Env("MODE"); var dir = Env("DIR");
            bool host = role == "host";
            if (!PhotonAppSettings.TryGetGlobal(out var photon) || !Guid.TryParse(photon.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            // One runner per process: Single peer mode, the production configuration.
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Single;

            // The declared config of the run, as the product reads it (SpatialBlendManager.CurrentSettings): the carrier mode.
            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _settings.useWebRtc = mode != "Photon";
            _settings.webRtcRelayOnly = mode == "WebRtcThatCannotConnect";
            if (mode == "WebRtcThatCannotConnect") _settings.webRtcIceTimeoutSeconds = 3f;
            bool relayed = mode == "WebRtcRelayed";
            // WebRtcTurn: TURN configured but not forced (iceTransportPolicy all), the product's own setting.
            bool turnMode = relayed || mode == "WebRtcTurn";
            if (turnMode)
            {
                _settings.webRtcRelayOnly = relayed;
                NUnitAssert.That(TurnClientSecret.Load(), Is.Not.Null.And.Not.Empty, "WebRtcRelayed needs the TURN client secret (" + TurnClientSecret.FileEnvironmentVariable + ")");
                var only = Env("TURN_TRANSPORTS");
                if (!string.IsNullOrEmpty(only) && only != "all")
                    _settings.turnTransports = only == "udp" ? TurnTransports.Udp : only == "tcp" ? TurnTransports.Tcp : only == "tls" ? TurnTransports.Tls : throw new ArgumentException("TURN_TRANSPORTS " + only);
            }
            bool webrtcMode = mode == "WebRtc" || turnMode;
            if (turnMode)
            {
                // The app-start prefetch (TurnIceService.Boot) has normally run; TURN_PREFETCH=0 measures the fetch-at-join path instead.
                if (Env("TURN_PREFETCH") == "0") TurnIceService.Reset();
                else
                {
                    if (TurnIceService.Current == null) TurnIceService.Prefetch(_settings);
                    yield return Until(() => TurnIceService.Current != null && TurnIceService.Current.State == TurnIceProvider.FetchState.Ready, "the TURN prefetch", 60);
                }
            }

            // The product's own core link path: SpatialBlendManager, and FusionConnectionManager as a networked object.
            // Nothing here pumps, offers a token or announces a round: the product does, from its own Fusion events.
            _managerObject = new GameObject("tp-core-" + role); _managerObject.SetActive(false);
            var manager = _managerObject.AddComponent<SpatialBlendManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            _managerObject.SetActive(true);
            _previousSettings = SpatialBlendManager.CurrentSettings;
            typeof(SpatialBlendManager).GetProperty("CurrentSettings", BindingFlags.Static | BindingFlags.Public).SetValue(null, _settings);

            _runnerObject = new GameObject("tp-runner-" + role);
            _runner = _runnerObject.AddComponent<NetworkRunner>();
            // As the Freeform scene wires it: the runner's events hand player joined, player left and shutdown to the
            // connection manager (the scene does it with a NetworkEvents component; this is the same three calls). The
            // manager does not exist yet when the first events fire, so it is looked up late.
            FusionConnectionManager connection = null;
            _runner.AddCallbacks(new SceneEvents(
                (runner, player) => connection?.OnPlayerJoined(runner, player),
                (runner, player) => connection?.OnPlayerLeft(runner, player),
                () => connection?.ShutdownConnection()));
            var template = new GameObject("FusionConnectionManagerTemplate");
            template.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(template);
            template.AddComponent<NetworkObject>();
            var template2 = template.AddComponent<FusionConnectionManager>();
            typeof(FusionConnectionManager).GetField("_spaceManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(template2, manager);
            new NetworkObjectBaker().Bake(template);
            if (!host) yield return Until(() => File.Exists(Path.Combine(dir, "host-ready")), "the host to be in the room", 240);
            var start = _runner.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room,
                PlayerCount = SpatialBlendSettings.DefaultMaxPlayers, AuthValues = TwoProcessIdentity.AuthValues(), SceneManager = _runnerObject.AddComponent<NetworkSceneManagerDefault>() });
            yield return Until(() => start.IsCompleted, "StartGame", 60);
            TestContext.Out.WriteLine($"TWOPROC role={role} StartGame ok={start.Result.Ok} reason={start.Result.ShutdownReason} error={start.Result.ErrorMessage}");
            NUnitAssert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            // Registered the moment the runner exists (its prefab table does not before): the host's copy of the connection
            // manager replicates to the joiner as soon as it connects.
            NUnitAssert.That(_runner.Prefabs.TryAddSource(new NetworkPrefabSourceStatic
                { Object = template.GetComponent<NetworkObject>(), AssetGuid = NetworkObjectGuid.Parse("c0ffee00-0000-4000-8000-000000000044") }, out var prefabId), Is.True);
            NUnitAssert.That(_runner.IsSharedModeMasterClient, Is.EqualTo(host));
            TestContext.Out.WriteLine($"TWOPROC role={role} userid={TwoProcessIdentity.Describe(_runner)} pid={System.Diagnostics.Process.GetCurrentProcess().Id} LocalPlayer={_runner.LocalPlayer.RawEncoded} mode={mode} peerMode={NetworkProjectConfig.Global.PeerMode}");
            // The host spawns the connection manager before anyone else is here, as the scene has it: its Fusion player-joined
            // event then starts the round, and the joiner's copy of it is the replicated object.
            if (host)
            {
                connection = _runner.Spawn(prefabId).GetComponent<FusionConnectionManager>();
                connection.gameObject.SetActive(true); // the template was inactive so nothing ran before the manager was wired
                // The host's own join event came before the object existed; the scene's event would have handed it over.
                connection.OnPlayerJoined(_runner, _runner.LocalPlayer);
                File.WriteAllText(Path.Combine(dir, "host-ready"), "1");
            }
            yield return Until(() => _runner.ActivePlayers.Count() == 2, "both players", 240);
            var local = _runner.LocalPlayer;
            var remote = _runner.ActivePlayers.First(p => p != local);
            // The host holds the object it spawned; the joiner's is the replica (never the inactive template).
            Func<FusionConnectionManager> find = () => UnityEngine.Object.FindObjectsByType<FusionConnectionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(m => m != template2 && m.Runner == _runner);
            if (!host) yield return Until(() => find() != null, "the connection manager", 120);
            if (!host) { connection = find(); connection.gameObject.SetActive(true); }
            TestContext.Out.WriteLine($"TWOPROC role={role} players joined; connection manager present authority={connection.HasStateAuthority}");

            float nextLog = 0f; var t0 = Time.realtimeSinceStartup;
            Func<string> describe = () => $"role={role} state={manager.ConnectionState} id={manager.ID} role={connection.ParticipantRole} linked={connection.LinkedPeerCount} " +
                $"carrier={connection.ActiveCarrier} path={connection.ActivePath} text=({connection.CarrierText}) rejected={connection.RejectedLinkMessages} hostLeft={connection.HostLeft}";
            Func<bool> linked = () =>
            {
                if (Time.realtimeSinceStartup > nextLog) { nextLog = Time.realtimeSinceStartup + 5f; TestContext.Out.WriteLine($"TWOPROC t={Time.realtimeSinceStartup - t0:0.0} {describe()}"); }
                return manager.ConnectionState == SpatialBlendConnectionState.Connected && connection.LinkedPeerCount == 1;
            };
            // The core join: the product's own round (the host's player-joined event, the announcement RPC, the token offers).
            yield return Until(linked, "the product's core link", 120, describe);
            TestContext.Out.WriteLine($"TWOPROC role={role} linked {describe()}");
            TestContext.Out.WriteLine($"TWOPROC role={role} link_s={Time.realtimeSinceStartup - t0:0.00} turn_cache_at_join={(turnMode ? (Env("TURN_PREFETCH") == "0" ? "cold" : "warm") : "none")} turn_fetch_s={(TurnIceService.Current != null ? TurnIceService.Current.LastFetchSeconds : 0):0.00}");

            // The carrier the product settled on: direct when WebRTC connects, Photon when it is off or cannot connect.
            if (webrtcMode) yield return Until(() => connection.ActiveCarrier == CoreLinkCarrier.WebRtc, "the direct carrier", 60, describe);
            else if (mode == "WebRtcThatCannotConnect") yield return Until(() => connection.CarrierText.Contains("fallback"), "the Photon fallback", 60, describe);
            else NUnitAssert.That(connection.ActiveCarrier, Is.EqualTo(CoreLinkCarrier.Photon));
            NUnitAssert.That(connection.ActiveCarrier, Is.EqualTo(webrtcMode ? CoreLinkCarrier.WebRtc : CoreLinkCarrier.Photon), describe());
            if (webrtcMode)
            {
                // The path is read from the selected candidate pair a moment after the channel opens.
                yield return Until(() => connection.ActivePath.Kind != CoreLinkPathKind.Unknown, "the selected candidate pair", 30, describe);
                TestContext.Out.WriteLine($"TWOPROC role={role} path={connection.ActivePath} direct_open_s={connection.DirectOpenSeconds:0.00} turn_only={Env("TURN_TRANSPORTS")} policy={(relayed ? "relay" : "all")}");
                if (relayed) NUnitAssert.That(connection.ActivePath.Kind, Is.EqualTo(CoreLinkPathKind.TurnRelay), "forced relay: the TURN relay must carry the link. " + describe());
                else NUnitAssert.That(connection.ActivePath.Kind, Is.Not.EqualTo(CoreLinkPathKind.Photon), describe());
            }
            else TestContext.Out.WriteLine($"TWOPROC role={role} path={connection.ActivePath}");

            // Forged chunks: right key, a token nobody issued, sent until the peer's product receiver has dropped one.
            float nextForged = 0f; int forgedSent = 0;
            Action forge = () =>
            {
                if (Time.realtimeSinceStartup < nextForged) return;
                nextForged = Time.realtimeSinceStartup + 4f; forgedSent++;
                _runner.SendReliableDataToPlayer(remote, FusionCoreLinkKey.Make((int)connection.CurrentEpoch, 100000 + forgedSent), LinkTokens.Frame(new byte[LinkTokens.Size], new byte[] { 0x66 }));
            };
            forge();

            // Registered after the join completes, as in the single-process test; the host shares only after the joiner is subscribed.
            var added = new System.Collections.Generic.List<(long Id, long Local, byte[] Payload)>();
            SpatialBlendAPI.RegisterOnElementAdded(manager.ID, (space, l, sid, tag, ud, size) =>
            {
                var b = new byte[size]; if (size > 0) Marshal.Copy(ud, b, 0, (int)size);
                lock (added) added.Add((sid, l, b));
            });
            Func<long, bool> has = id => { lock (added) return added.Any(a => a.Id == id); };
            Func<long, (long Id, long Local, byte[] Payload)> get = id => { lock (added) return added.First(a => a.Id == id); };
            if (!host) File.WriteAllText(Path.Combine(dir, "joiner-linked"), "1");
            else yield return Until(() => { forge(); return File.Exists(Path.Combine(dir, "joiner-linked")); }, "the joiner's link", 240, describe);
            int down = webrtcMode ? 3 * 1024 * 1024 : 48 * 1024;
            int up = webrtcMode ? 1024 * 1024 : 32 * 1024;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (host)
            {
                long room1 = SpatialBlendAPI.AddElement(manager.ID, 0);
                SpatialBlendAPI.SetElementSharedId(manager.ID, room1, RoomShared);
                Square(manager.ID, room1);
                SpatialBlendAPI.ShareElement(manager.ID, room1);
                TestContext.Out.WriteLine($"TWOPROC role=host shared room local={room1} spaceId={manager.ID} findBySharedId={SpatialBlendAPI.FindElementBySharedId(manager.ID, RoomShared)} isHost={manager.IsHost()}");
                long e1 = SpatialBlendAPI.AddElement(manager.ID, room1);
                SpatialBlendAPI.SetElementSharedId(manager.ID, e1, FromHostShared);
                Share(manager.ID, e1, Payload(down, 31));
                yield return Until(() => { forge(); return has(FromJoinerShared); }, "the joiner's object", 240, describe);
                NUnitAssert.That(Hash(get(FromJoinerShared).Payload), Is.EqualTo(Hash(Payload(up, 32))), "the joiner's payload arrived byte for byte");
                File.WriteAllText(Path.Combine(dir, "host-done"), $"{sw.ElapsedMilliseconds}");
                yield return Until(() => { forge(); return File.Exists(Path.Combine(dir, "joiner-done")); }, "the joiner to finish", 240, describe);
            }
            else
            {
                yield return Until(() => has(RoomShared), "the room", 120, describe);
                long jroom = get(RoomShared).Local;
                SpatialBlendAPI.ResolveSharedElement(manager.ID, jroom, jroom);
                Square(manager.ID, jroom);
                yield return Until(() => has(FromHostShared), "the host's object", 240, describe);
                NUnitAssert.That(Hash(get(FromHostShared).Payload), Is.EqualTo(Hash(Payload(down, 31))), "the host's payload arrived byte for byte");
                TestContext.Out.WriteLine($"TWOPROC role=joiner received down_bytes={down} in {sw.ElapsedMilliseconds} ms (hash checked)");
                long e2 = SpatialBlendAPI.AddElement(manager.ID, jroom);
                SpatialBlendAPI.SetElementSharedId(manager.ID, e2, FromJoinerShared);
                Share(manager.ID, e2, Payload(up, 32));
                yield return Until(() => File.Exists(Path.Combine(dir, "host-done")), "the host to confirm", 240, describe);
                File.WriteAllText(Path.Combine(dir, "joiner-done"), "1");
            }
            yield return Until(() => { forge(); return connection.RejectedLinkMessages >= 1; }, "the peer's forged chunk to arrive and be dropped", 90, describe);
            File.WriteAllText(Path.Combine(dir, role + "-forged-dropped"), "1");
            yield return Until(() => { forge(); return File.Exists(Path.Combine(dir, (host ? "joiner" : "host") + "-forged-dropped")); }, "the peer to drop my forged chunk", 90, describe);
            TestContext.Out.WriteLine($"TWOPROC role={role} DONE {describe()} elapsed_ms={sw.ElapsedMilliseconds} down={down} up={up}");
            NUnitAssert.That(connection.RejectedLinkMessages, Is.GreaterThanOrEqualTo(1), "the peer's forged chunks were dropped, while all the link's own bytes (hash-checked above) got through on their tokens");
            NUnitAssert.That(connection.HostLeft, Is.False);
            // Both sides have verified everything. Whichever runner shuts down first makes the Photon client of the other log a
            // disconnect as an error; that is the end of the run, not a result, so it must not fail the teardown.
            LogAssert.ignoreFailingMessages = true;
        }

        /// <summary>The three runner events the scene's NetworkEvents component forwards to the connection manager.</summary>
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
            public void OnSessionListUpdated(NetworkRunner runner, System.Collections.Generic.List<SessionInfo> sessionList) { }
            public void OnCustomAuthenticationResponse(NetworkRunner runner, System.Collections.Generic.Dictionary<string, object> data) { }
            public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
            public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
            public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
            public void OnSceneLoadDone(NetworkRunner runner) { }
            public void OnSceneLoadStart(NetworkRunner runner) { }
            public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
            public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        }

        private static void Share(long space, long element, byte[] payload)
        {
            var h = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try { SpatialBlendAPI.ShareElement(space, element, h.AddrOfPinnedObject(), payload.Length); } finally { h.Free(); }
        }

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
    }
}
