// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using NUnit.Framework;
using SpatialBlend.Internal;
using SpatialBlend.Samples;
using UnityEngine;
using UnityEngine.TestTools;
using NUnitAssert = NUnit.Framework.Assert;
using Debug = UnityEngine.Debug;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The acceptance test of hosting and joining a room with no address, against the
    /// real Photon Cloud. Two Shared-mode runners in one Editor process join one room by NAME ONLY. The first is the
    /// master and hosts the core with no listening socket; the second joins over a core link of its own. The link's
    /// bytes cross between them as Fusion reliable data on the Photon session, pumped by the product's own
    /// <see cref="CoreLinkPump"/> over the product's own <see cref="FusionCoreLinkWire"/> and
    /// <see cref="FusionCoreLinkReceiver"/>. An object is then shared end to end, in both directions, with payloads sized by what
    /// Fusion reliable data carries in a minute (see the throughput line this test prints). No TCP listener is opened and no address is dialed anywhere. Needs the
    /// network and PhotonAppSettings.AppIdFusion; ignored without an App ID. Run with SPATIALBLEND_PHOTON_CLOUD=1
    /// (tools/run-photon-cloud-tests.sh).
    /// </summary>
    [TestFixture]
    [Category("PhotonCloud")]
    public class FusionCoreLinkPhotonCloudTests
    {
        private const float StepTimeoutSeconds = 90f;
        private const int Epoch = 1;
        private const long RoomShared = 9100;
        private const long FromHostShared = 9101;
        private const long FromJoinerShared = 9102;

        private readonly List<NetworkRunner> _runners = new();
        private readonly List<GameObject> _objects = new();
        private NetworkProjectConfig.PeerModes _previousPeerMode;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!PhotonAppSettings.TryGetGlobal(out var settings) ||
                !Guid.TryParse(settings.AppSettings.AppIdFusion, out _))
                NUnitAssert.Ignore("No Photon Fusion App ID configured.");
            _previousPeerMode = NetworkProjectConfig.Global.PeerMode;
            NetworkProjectConfig.Global.PeerMode = NetworkProjectConfig.PeerModes.Multiple;
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var go in _objects.Where(o => o != null))
                go.GetComponent<SpatialBlendManager>()?.Disconnect();
            foreach (var runner in _runners.Where(r => r != null))
                yield return WaitFor(runner.Shutdown(), "runner shutdown");
            _runners.Clear();
            foreach (var go in _objects.Where(o => o != null)) UnityEngine.Object.Destroy(go);
            _objects.Clear();
            NetworkProjectConfig.Global.PeerMode = _previousPeerMode;
        }

        private sealed class Side
        {
            public NetworkRunner Runner;
            public SpatialBlendManager Manager;
            public CoreLinkPump Pump;
            public PeerCoreLinkCarrier Carrier;
            public FusionCoreLinkReceiver Receiver;
            public readonly LinkTokens Tokens = new LinkTokens();
            public readonly List<(long SharedId, long Local, byte[] Payload)> Added = new();
        }

        private enum Carrying { PhotonOnly, WebRtc, WebRtcThatCannotConnect }

        [UnityTest]
        public IEnumerator TwoRunnersJoinOneRoomByNameAndShareAnObjectOverTheCoreLink() =>
            Scenario(Carrying.PhotonOnly, 256 * 1024, 128 * 1024);

        // The direct carrier: the same core link over a WebRTC data channel, peer to peer, negotiated through signals that
        // ride the Photon session. A 3 MiB scan (down) and a 1 MiB object (up) are shared end to end.
        [UnityTest, Timeout(600000)]
        public IEnumerator TheCoreLinkComesUpOverWebRtcAndSharesAThreeMegabyteObject() =>
            Scenario(Carrying.WebRtc, 3 * 1024 * 1024, 1024 * 1024);

        // ICE cannot connect (relay candidates only, and there is no TURN server): the Photon carrier takes over by itself
        // after the declared timeout and the object still arrives.
        [UnityTest, Timeout(600000)]
        public IEnumerator WhenIceFailsTheCoreLinkFallsBackToThePhotonCarrier() =>
            Scenario(Carrying.WebRtcThatCannotConnect, 48 * 1024, 32 * 1024);

        private IEnumerator Scenario(Carrying carrying, int downBytes, int upBytes)
        {
            var room = "spatialblend-link-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var host = new Side { Runner = CreateRunner("link-host") };
            var joiner = new Side { Runner = CreateRunner("link-joiner") };

            // The room is found and joined by its name. Nothing else is exchanged out of band.
            var players = SpatialBlendSettings.DefaultMaxPlayers;
            yield return WaitFor(host.Runner.StartGame(Args(host.Runner, room, players)), "host StartGame");
            yield return WaitFor(joiner.Runner.StartGame(Args(joiner.Runner, room, players)), "joiner StartGame");
            yield return WaitUntil(() => host.Runner.ActivePlayers.Count() == 2 && joiner.Runner.ActivePlayers.Count() == 2,
                "both runners see both players");
            NUnitAssert.That(host.Runner.IsSharedModeMasterClient, Is.True, "the first to join is the master, so it hosts the core");
            NUnitAssert.That(joiner.Runner.IsSharedModeMasterClient, Is.False);
            var hostPlayer = host.Runner.LocalPlayer;
            var joinerPlayer = joiner.Runner.LocalPlayer;

            host.Manager = CreateManager("link-host-core");
            joiner.Manager = CreateManager("link-joiner-core");

            // Each runner delivers the other's chunks to the pump of that player's link.
            host.Receiver = new FusionCoreLinkReceiver((from, epoch, seq, bytes) =>
            {
                if (from == joinerPlayer && host.Pump != null) host.Pump.Receive(epoch, seq, bytes);
            }, (from, epoch, json) => { if (from == joinerPlayer && epoch == Epoch) host.Carrier?.Transport?.ReceiveSignal(json); }, host.Tokens);
            joiner.Receiver = new FusionCoreLinkReceiver((from, epoch, seq, bytes) =>
            {
                if (from == hostPlayer && joiner.Pump != null) joiner.Pump.Receive(epoch, seq, bytes);
            }, (from, epoch, json) => { if (from == hostPlayer && epoch == Epoch) joiner.Carrier?.Transport?.ReceiveSignal(json); }, joiner.Tokens);
            // Both runners live in this one process, so the harness itself is the authenticated channel the product gets
            // from a targeted RPC (see the two-process test): each side is handed the token the other issued for it.
            host.Tokens.AcceptOffer(Epoch, joinerPlayer.RawEncoded, joiner.Tokens.IssueFor(Epoch, hostPlayer.RawEncoded));
            joiner.Tokens.AcceptOffer(Epoch, hostPlayer.RawEncoded, host.Tokens.IssueFor(Epoch, joinerPlayer.RawEncoded));
            host.Runner.AddCallbacks(host.Receiver);
            joiner.Runner.AddCallbacks(joiner.Receiver);
            TestContext.Out.WriteLine($"PHOTON_CLOUD_LINK reliable_data_send_rate={host.Runner.ReliableDataSendRate} tick_rate={host.Runner.TickRate}");

            // The master hosts with no listening socket and attaches one link for the joining player.
            host.Manager.HostTunneled();
            NUnitAssert.That(host.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), host.Manager.ConnectionError);
            NUnitAssert.That(host.Manager.Port, Is.Zero, "no port was bound or advertised");
            CoreLink hostEnd = host.Manager.AcceptCoreLink();
            NUnitAssert.That(hostEnd, Is.Not.Null);
            var settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            var hostWire = new FusionCoreLinkWire(host.Runner, host.Tokens);
            var joinWire = new FusionCoreLinkWire(joiner.Runner, joiner.Tokens);
            // The joiner answers, so its carrier exists before the host's offer is sent.
            joiner.Carrier = NewCarrier(carrying, joinWire, joiner.Runner, hostPlayer, settings, offerer: false);
            host.Carrier = NewCarrier(carrying, hostWire, host.Runner, joinerPlayer, settings, offerer: true);
            host.Pump = new CoreLinkPump(hostEnd, joinerPlayer.RawEncoded, Epoch, host.Carrier, holdUntilPeerSpeaks: true);

            host.Carrier.Chunk += (e, sq, b) => host.Pump.Receive(e, sq, b);
            // The joiner joins over a link of its own. The join completes only while the link is pumped.
            CoreLink joinEnd = joiner.Manager.JoinTunneled();
            NUnitAssert.That(joinEnd, Is.Not.Null);
            joiner.Pump = new CoreLinkPump(joinEnd, hostPlayer.RawEncoded, Epoch, joiner.Carrier, holdUntilPeerSpeaks: false);
            joiner.Carrier.Chunk += (e, sq, b) => joiner.Pump.Receive(e, sq, b);

            var joinStarted = Stopwatch.StartNew();
            yield return WaitUntil(() =>
            {
                Pump(host, joiner);
                return joiner.Manager.ConnectionState != SpatialBlendConnectionState.Connecting;
            }, "the core join over Fusion reliable data", () => Describe(host, joiner));
            NUnitAssert.That(joiner.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected), joiner.Manager.ConnectionError);
            var joinMs = joinStarted.ElapsedMilliseconds;
            TestContext.Out.WriteLine($"PHOTON_CLOUD_LINK mode={carrying} join_ms={joinMs} host_carrier={host.Carrier.Active} joiner_carrier={joiner.Carrier.Active} " +
                                      $"host_fell_back={host.Carrier.FellBack} ({host.Carrier.FallbackReason}) joiner_fell_back={joiner.Carrier.FellBack} ({joiner.Carrier.FallbackReason}) " +
                                      $"host_bytes_sent={host.Pump.BytesSent} joiner_bytes_sent={joiner.Pump.BytesSent}");
            if (carrying == Carrying.WebRtc)
            {
                NUnitAssert.That(host.Carrier.Active, Is.EqualTo(CoreLinkCarrier.WebRtc), "the host's carrier is the direct channel: " + host.Carrier.FallbackReason);
                NUnitAssert.That(joiner.Carrier.Active, Is.EqualTo(CoreLinkCarrier.WebRtc), "the joiner's carrier is the direct channel: " + joiner.Carrier.FallbackReason);
            }
            else if (carrying == Carrying.WebRtcThatCannotConnect)
            {
                NUnitAssert.That(host.Carrier.FellBack, Is.True, "ICE cannot connect, so the host fell back");
                NUnitAssert.That(joiner.Carrier.FellBack, Is.True);
                NUnitAssert.That(host.Carrier.Active, Is.EqualTo(CoreLinkCarrier.Photon));
                NUnitAssert.That(joiner.Carrier.Active, Is.EqualTo(CoreLinkCarrier.Photon));
            }

            RegisterReceive(host);
            RegisterReceive(joiner);

            // A room both sides know, with its geometry, then an object from each side with a payload of hundreds of KiB.
            long hostRoom = SpatialBlendAPI.AddElement(host.Manager.ID, 0);
            SpatialBlendAPI.SetElementSharedId(host.Manager.ID, hostRoom, RoomShared);
            Square(host.Manager.ID, hostRoom);
            SpatialBlendAPI.ShareElement(host.Manager.ID, hostRoom);
            yield return WaitUntil(() => { Pump(host, joiner); return Has(joiner, RoomShared); }, "the room reaches the joiner", () => Describe(host, joiner));
            long joinerRoom = Get(joiner, RoomShared).Local;
            SpatialBlendAPI.ResolveSharedElement(joiner.Manager.ID, joinerRoom, joinerRoom);
            Square(joiner.Manager.ID, joinerRoom);

            var down = Payload(downBytes, 11);
            var up = Payload(upBytes, 22);

            long fromHost = SpatialBlendAPI.AddElement(host.Manager.ID, hostRoom);
            SpatialBlendAPI.SetElementSharedId(host.Manager.ID, fromHost, FromHostShared);
            var downTimer = Stopwatch.StartNew();
            Share(host.Manager.ID, fromHost, down);
            yield return WaitUntil(() => { Pump(host, joiner); return Has(joiner, FromHostShared); }, "the host's object reaches the joiner", () => Describe(host, joiner));
            var downMs = downTimer.ElapsedMilliseconds;
            var gotDown = Get(joiner, FromHostShared).Payload;
            NUnitAssert.That(gotDown.Length, Is.EqualTo(down.Length));
            NUnitAssert.That(Hash(gotDown), Is.EqualTo(Hash(down)), "the downward payload arrived byte for byte");

            long fromJoiner = SpatialBlendAPI.AddElement(joiner.Manager.ID, joinerRoom);
            SpatialBlendAPI.SetElementSharedId(joiner.Manager.ID, fromJoiner, FromJoinerShared);
            var upTimer = Stopwatch.StartNew();
            Share(joiner.Manager.ID, fromJoiner, up);
            yield return WaitUntil(() => { Pump(host, joiner); return Has(host, FromJoinerShared); }, "the joiner's object reaches the host", () => Describe(host, joiner));
            var upMs = upTimer.ElapsedMilliseconds;
            var gotUp = Get(host, FromJoinerShared).Payload;
            NUnitAssert.That(gotUp.Length, Is.EqualTo(up.Length));
            NUnitAssert.That(Hash(gotUp), Is.EqualTo(Hash(up)), "the upward payload arrived byte for byte");

            TestContext.Out.WriteLine(
                $"PHOTON_CLOUD_LINK mode={carrying} down_bytes={down.Length} down_ms={downMs} down_MBps={down.Length / 1048576.0 / (downMs / 1000.0):0.000} " +
                $"up_bytes={up.Length} up_ms={upMs} up_MBps={up.Length / 1048576.0 / (upMs / 1000.0):0.000} " +
                $"host_chunks_sent={host.Pump.ChunksSent} joiner_chunks_sent={joiner.Pump.ChunksSent} " +
                $"host_bytes_sent={host.Pump.BytesSent} joiner_bytes_sent={joiner.Pump.BytesSent} chunk_bytes={host.Pump.MaxChunkBytes}");

            // The joiner leaves: the host closes that player's link and stays up.
            joiner.Manager.Disconnect();
            host.Pump.Close();
            host.Carrier.Dispose();
            joiner.Carrier.Dispose();
            UnityEngine.Object.DestroyImmediate(settings);
            NUnitAssert.That(host.Manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
            host.Runner.RemoveCallbacks(host.Receiver);
            joiner.Runner.RemoveCallbacks(joiner.Receiver);
        }

        // PhotonOnly has no direct transport at all; the other two carry the link over WebRTC with the declared STUN list, the
        // second restricted to relay candidates so that ICE cannot succeed (no TURN server is configured).
        private static PeerCoreLinkCarrier NewCarrier(Carrying carrying, FusionCoreLinkWire wire, NetworkRunner runner, PlayerRef peer,
            SpatialBlendSettings settings, bool offerer)
        {
            IPeerDataTransport transport = null;
            if (carrying != Carrying.PhotonOnly)
            {
                transport = new WebRtcPeerTransport(settings.stunServers, relayOnly: carrying == Carrying.WebRtcThatCannotConnect);
                transport.Signal += json => wire.SendSignal(peer.RawEncoded, Epoch, json);
            }
            float timeout = carrying == Carrying.WebRtcThatCannotConnect ? 3f : settings.webRtcIceTimeoutSeconds;
            return new PeerCoreLinkCarrier(new PhotonWireAdapter(wire), transport, () => Time.realtimeSinceStartupAsDouble, timeout, offerer);
        }

        private sealed class PhotonWireAdapter : ICoreLinkWire
        {
            private readonly FusionCoreLinkWire _wire;
            public PhotonWireAdapter(FusionCoreLinkWire wire) => _wire = wire;
            public void Send(int peer, int epoch, int sequence, byte[] bytes) => _wire.Send(peer, epoch, sequence, bytes);
        }

        private static string Describe(Side host, Side joiner) =>
            $"host pump sent {host.Pump?.BytesSent}B/{host.Pump?.ChunksSent} chunks, received {host.Pump?.BytesReceived}B (waiting {host.Pump?.ChunksWaiting}, " +
            $"peer spoke {host.Pump?.PeerHasSpoken}, ended {host.Pump?.Ended}); joiner pump sent {joiner.Pump?.BytesSent}B/{joiner.Pump?.ChunksSent} chunks, " +
            $"received {joiner.Pump?.BytesReceived}B (waiting {joiner.Pump?.ChunksWaiting}, ended {joiner.Pump?.Ended}); joiner state {joiner.Manager.ConnectionState}; " +
            $"host runner got {host.Receiver?.ChunksReceived} chunks (foreign {host.Receiver?.ForeignReceived}, last sender {host.Receiver?.LastSender}), joiner runner got {joiner.Receiver?.ChunksReceived} chunks " +
            $"(foreign {joiner.Receiver?.ForeignReceived}, last sender {joiner.Receiver?.LastSender}); players host={host.Runner.LocalPlayer.RawEncoded} joiner={joiner.Runner.LocalPlayer.RawEncoded}";

        // Native callbacks append from their own threads.
        private static bool Has(Side side, long sharedId) { lock (side.Added) return side.Added.Any(a => a.SharedId == sharedId); }
        private static (long SharedId, long Local, byte[] Payload) Get(Side side, long sharedId) { lock (side.Added) return side.Added.First(a => a.SharedId == sharedId); }

        private static void Pump(Side host, Side joiner)
        {
            host.Carrier?.Tick();
            joiner.Carrier?.Tick();
            host.Pump?.Pump();
            joiner.Pump?.Pump();
        }

        private static StartGameArgs Args(NetworkRunner runner, string room, int playerCount) => new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = room,
            PlayerCount = playerCount,
            SceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>(),
        };

        private SpatialBlendManager CreateManager(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            var manager = go.AddComponent<SpatialBlendManager>();
            // The Fusion round decides when the core starts; no TCP host or connect on Awake.
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, false);
            go.SetActive(true);
            _objects.Add(go);
            return manager;
        }

        private static void RegisterReceive(Side side)
        {
            SpatialBlendAPI.RegisterOnElementAdded(side.Manager.ID, (space, local, sharedId, tag, userData, size) =>
            {
                var bytes = new byte[size];
                if (size > 0) Marshal.Copy(userData, bytes, 0, (int)size);
                lock (side.Added) side.Added.Add((sharedId, local, bytes));
            });
        }

        private static void Share(long space, long element, byte[] payload)
        {
            var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try { SpatialBlendAPI.ShareElement(space, element, handle.AddrOfPinnedObject(), payload.Length); }
            finally { handle.Free(); }
        }

        private static void Square(long space, long element) =>
            SpatialBlendAPI.SetElementPolygon(space, element, SbGeometryRole.Navigable, new[]
            {
                new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100),
            });

        private static byte[] Payload(int size, uint seed)
        {
            var bytes = new byte[size];
            uint x = seed * 2654435761u + 1;
            for (int i = 0; i < size; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                bytes[i] = (byte)(x >> 11);
            }
            return bytes;
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes));
        }

        private NetworkRunner CreateRunner(string name)
        {
            var go = new GameObject("photon-cloud-" + name);
            _objects.Add(go);
            var runner = go.AddComponent<NetworkRunner>();
            _runners.Add(runner);
            return runner;
        }

        private static IEnumerator WaitFor(Task task, string what)
        {
            yield return WaitUntil(() => task.IsCompleted, what);
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static IEnumerator WaitFor<T>(Task<T> task, string what)
        {
            yield return WaitUntil(() => task.IsCompleted, what);
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            var result = task.Result as StartGameResult;
            if (result != null) NUnitAssert.That(result.Ok, Is.True, $"{what} failed: {result.ShutdownReason}");
        }

        private static IEnumerator WaitUntil(Func<bool> done, string what, Func<string> describe = null)
        {
            var start = Time.realtimeSinceStartup;
            var deadline = start + StepTimeoutSeconds;
            var nextProgress = start + 5f;
            while (!done())
            {
                var now = Time.realtimeSinceStartup;
                if (now > deadline)
                    NUnitAssert.Fail($"Timed out after {StepTimeoutSeconds}s waiting for {what}. {describe?.Invoke()}");
                if (describe != null && now >= nextProgress)
                {
                    nextProgress += 5f;
                    TestContext.Out.WriteLine($"PHOTON_CLOUD_LINK progress t={now - start:0.0}s waiting for {what}: {describe()}");
                }
                yield return null;
            }
        }
    }
}
