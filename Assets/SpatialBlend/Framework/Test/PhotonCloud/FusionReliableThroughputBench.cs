// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
    /// Measures what Fusion reliable data carries between two Shared-mode runners on the real cloud, against chunk size,
    /// chunks in flight, and ReliableDataSendRate. Evidence for the carrier's throughput; not part of the acceptance gate
    /// (category ReliableDataBench, run with SPATIALBLEND_PHOTON_CLOUD=1 and -testCategory ReliableDataBench).
    /// </summary>
    [TestFixture]
    [Category("ReliableDataBench")]
    public class FusionReliableThroughputBench
    {
        private readonly List<NetworkRunner> _runners = new();
        private readonly List<GameObject> _objects = new();
        private NetworkProjectConfig.PeerModes _previous;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!PhotonAppSettings.TryGetGlobal(out var settings) || !Guid.TryParse(settings.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            _previous = NetworkProjectConfig.Global.PeerMode;
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Multiple;
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var r in _runners.Where(r => r != null)) { var t = r.Shutdown(); while (!t.IsCompleted) yield return null; }
            foreach (var go in _objects.Where(o => o != null)) UnityEngine.Object.Destroy(go);
            NetworkProjectConfig.Global.PeerMode = _previous;
        }

        private sealed class Sink : INetworkRunnerCallbacks
        {
            public long Bytes; public int Chunks;
            public void OnReliableDataReceived(NetworkRunner r, PlayerRef p, ReliableKey k, ArraySegment<byte> d) { Bytes += d.Count; Chunks++; }
            public void OnReliableDataProgress(NetworkRunner r, PlayerRef p, ReliableKey k, float f) { }
            public void OnPlayerJoined(NetworkRunner r, PlayerRef p) { }
            public void OnPlayerLeft(NetworkRunner r, PlayerRef p) { }
            public void OnInput(NetworkRunner r, NetworkInput i) { }
            public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput i) { }
            public void OnShutdown(NetworkRunner r, ShutdownReason s) { }
            public void OnConnectedToServer(NetworkRunner r) { }
            public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason s) { }
            public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest q, byte[] t) { }
            public void OnConnectFailed(NetworkRunner r, NetAddress a, NetConnectFailedReason s) { }
            public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr m) { }
            public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> l) { }
            public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> d) { }
            public void OnHostMigration(NetworkRunner r, HostMigrationToken t) { }
            public void OnSceneLoadDone(NetworkRunner r) { }
            public void OnSceneLoadStart(NetworkRunner r) { }
            public void OnObjectExitAOI(NetworkRunner r, NetworkObject o, PlayerRef p) { }
            public void OnObjectEnterAOI(NetworkRunner r, NetworkObject o, PlayerRef p) { }
        }

        private NetworkRunner NewRunner(string n)
        {
            var go = new GameObject("bench-" + n); _objects.Add(go);
            var r = go.AddComponent<NetworkRunner>(); _runners.Add(r); return r;
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator ThreeMiBGlbInCarrierChunks() => Run(3 * 1024 * 1024, new (string, int, int, int)[] { ("glb3MiB_chunk64K", 65536, 0, 0) }, 400);

        [UnityTest, Timeout(900000)]
        public IEnumerator Throughput() => Run(128 * 1024, null, 40);

        private IEnumerator Run(int total, (string, int, int, int)[] only, int deadlineSeconds)
        {
            var room = "spatialblend-bench-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var a = NewRunner("a"); var b = NewRunner("b");
            foreach (var r in new[] { a, b })
            {
                var t = r.StartGame(new StartGameArgs { GameMode = GameMode.Shared, SessionName = room, PlayerCount = 9,
                    SceneManager = r.gameObject.AddComponent<NetworkSceneManagerDefault>() });
                while (!t.IsCompleted) yield return null;
                NUnitAssert.That(t.Result.Ok, Is.True);
            }
            while (a.ActivePlayers.Count() < 2 || b.ActivePlayers.Count() < 2) yield return null;
            var sink = new Sink(); a.AddCallbacks(sink);
            var target = a.LocalPlayer;
            int Total = total;
            int defaultRate = b.ReliableDataSendRate;
            int key = 0;
            // name, chunk bytes, in flight (0 = all at once), send rate (0 = leave)
            var configs = only ?? new (string, int, int, int)[]
            {
                ("chunk1K_all", 1024, 0, 0), ("chunk4K_all", 4096, 0, 0), ("chunk16K_all", 16384, 0, 0),
                ("chunk64K_all", 65536, 0, 0), ("chunk128K_all", 131072, 0, 0),
                ("chunk16K_inflight1", 16384, 1, 0), ("chunk16K_inflight4", 16384, 4, 0),
                ("chunk64K_rate16", 65536, 0, 16), ("chunk64K_rate8", 65536, 0, 8), ("chunk64K_rate4", 65536, 0, 4),
                ("chunk64K_rate1", 65536, 0, 1),
            };
            foreach (var (name, chunk, inflight, rate) in configs)
            {
                b.ReliableDataSendRate = rate > 0 ? rate : defaultRate;
                sink.Bytes = 0; sink.Chunks = 0;
                int n = Total / chunk; int sent = 0;
                var sw = Stopwatch.StartNew();
                int deadlineS = deadlineSeconds;
                while (sink.Bytes < Total && sw.Elapsed.TotalSeconds < deadlineS)
                {
                    while (sent < n && (inflight == 0 || sent - sink.Chunks < inflight))
                    {
                        b.SendReliableDataToPlayer(target, ReliableKey.FromInts(77, ++key, 0, 0), new byte[chunk]);
                        sent++;
                    }
                    yield return null;
                }
                double s = sw.Elapsed.TotalSeconds;
                TestContext.Out.WriteLine($"BENCH {name} rate={b.ReliableDataSendRate} chunk={chunk} inflight={inflight} bytes={sink.Bytes}/{Total} seconds={s:0.00} KBps={sink.Bytes / 1024.0 / s:0.0} fps={Time.frameCount}");
            }
            b.ReliableDataSendRate = defaultRate;
        }
    }
}
