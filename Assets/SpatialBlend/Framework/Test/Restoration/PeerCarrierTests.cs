// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Samples;
using UnityEngine;
using FakeTunnelManager = SpatialBlend.Tests.TunnelFixtures.FakeTunnelManager;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The direct carrier (WebRTC data channel) behind <see cref="IPeerDataTransport"/>, with a fake transport: the
    /// negotiation gate, the bounded wait, the fallback to the Photon wire, backpressure, and that mixing paths cannot
    /// reorder the core stream.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class PeerCarrierTests
    {
        public class FakeTransport : IPeerDataTransport
        {
            public PeerTransportState State { get; set; } = PeerTransportState.Idle;
            public long BufferedBytes { get; set; }
            public bool Offerer;
            public bool Started, Disposed;
            public readonly List<byte[]> Sent = new List<byte[]>();
            public readonly List<string> SignalsReceived = new List<string>();
            public FakeTransport Remote;   // when set, signals and messages loop to it
            public event Action<string> Signal;
            public event Action<byte[]> Message;
            public void Start(bool offerer)
            {
                Offerer = offerer; Started = true;
                if (State != PeerTransportState.Open) State = PeerTransportState.Negotiating;
                if (offerer) Signal?.Invoke("{\"t\":\"offer\"}");
            }
            public void ReceiveSignal(string json)
            {
                SignalsReceived.Add(json);
                if (!Offerer && json.Contains("offer")) Signal?.Invoke("{\"t\":\"answer\"}");
                if (Offerer && json.Contains("answer")) { State = PeerTransportState.Open; if (Remote != null) Remote.State = PeerTransportState.Open; }
            }
            public void Send(byte[] message) { Sent.Add(message); Remote?.Message?.Invoke(message); }
            public void Tick() { }
            public void Dispose() { Disposed = true; State = PeerTransportState.Closed; }
            public void Deliver(byte[] message) => Message?.Invoke(message);
        }

        private sealed class PhotonWire : ICoreLinkWire
        {
            public readonly List<(int Epoch, int Seq, byte[] Bytes)> Sent = new List<(int, int, byte[])>();
            public void Send(int peer, int epoch, int sequence, byte[] bytes) => Sent.Add((epoch, sequence, bytes));
        }

        private double _now;

        private PeerCoreLinkCarrier Carrier(PhotonWire photon, FakeTransport t, double timeout = 5) =>
            new PeerCoreLinkCarrier(photon, t, () => _now, timeout, offerer: true);

        [SetUp]
        public void SetUp() => _now = 100;

        [Test]
        public void NothingIsSentWhileTheDirectPathNegotiates()
        {
            var t = new FakeTransport();
            var c = Carrier(new PhotonWire(), t);
            Assert.That(t.Started, Is.True);
            Assert.That(c.Negotiating, Is.True);
            Assert.That(c.CanAccept, Is.False, "a large payload must not start on the slow path");
            Assert.That(c.Active, Is.EqualTo(CoreLinkCarrier.Photon));
        }

        [Test]
        public void ADirectPathThatOpensCarriesEveryChunkWithItsEpochAndSequence()
        {
            var photon = new PhotonWire();
            var t = new FakeTransport();
            var c = Carrier(photon, t);
            t.State = PeerTransportState.Open;
            c.Tick();
            Assert.That(c.Active, Is.EqualTo(CoreLinkCarrier.WebRtc));
            Assert.That(c.CanAccept, Is.True);
            c.Send(3, 7, 42, new byte[] { 9, 8 });
            Assert.That(photon.Sent, Is.Empty);
            Assert.That(t.Sent.Single(), Is.EqualTo(new byte[] { 7, 0, 0, 0, 42, 0, 0, 0, 9, 8 }));
        }

        [Test]
        public void AnInboundDirectMessageBecomesAChunk()
        {
            var t = new FakeTransport();
            var c = Carrier(new PhotonWire(), t);
            (int, int, byte[]) got = default;
            c.Chunk += (e, s, b) => got = (e, s, b);
            t.Deliver(new byte[] { 2, 0, 0, 0, 5, 0, 0, 0, 1, 2, 3 });
            Assert.That(got.Item1, Is.EqualTo(2));
            Assert.That(got.Item2, Is.EqualTo(5));
            Assert.That(got.Item3, Is.EqualTo(new byte[] { 1, 2, 3 }));
            t.Deliver(new byte[] { 1, 2 });  // shorter than a frame header: ignored
        }

        [Test]
        public void ADirectPathThatDoesNotOpenInTimeFallsBackToPhoton()
        {
            var photon = new PhotonWire();
            var t = new FakeTransport();
            var c = Carrier(photon, t, timeout: 5);
            _now = 104.9; c.Tick();
            Assert.That(c.Negotiating, Is.True);
            _now = 105; c.Tick();
            Assert.That(c.Negotiating, Is.False);
            Assert.That(c.FellBack, Is.True);
            Assert.That(c.FallbackReason, Does.Contain("in time"));
            Assert.That(c.Active, Is.EqualTo(CoreLinkCarrier.Photon));
            Assert.That(c.CanAccept, Is.True);
            c.Send(3, 1, 0, new byte[] { 4 });
            Assert.That(photon.Sent.Single().Bytes, Is.EqualTo(new byte[] { 4 }));
            Assert.That(t.Sent, Is.Empty);
        }

        [Test]
        public void ADirectPathThatFailsFallsBackAtOnce()
        {
            var t = new FakeTransport();
            var c = Carrier(new PhotonWire(), t);
            t.State = PeerTransportState.Failed;
            c.Tick();
            Assert.That(c.FellBack, Is.True);
            Assert.That(c.FallbackReason, Does.Contain("failed"));
            Assert.That(c.Active, Is.EqualTo(CoreLinkCarrier.Photon));
        }

        // A channel that fails after it opened must not quietly hand the rest to Photon (the chunks it already
        // took are lost, and the peer would wait at the gap for ever). It is reported as lost, and nothing is sent.
        [Test]
        public void ADirectPathThatFailsAfterItOpenedIsLostAndNothingMoreIsSent()
        {
            var photon = new PhotonWire();
            var t = new FakeTransport();
            var c = Carrier(photon, t);
            t.State = PeerTransportState.Open; c.Tick();
            Assert.That(c.Lost, Is.False);
            t.State = PeerTransportState.Closed; c.Tick();
            Assert.That(c.Lost, Is.True);
            Assert.That(c.LostReason, Does.Contain("closed"));
            Assert.That(c.FellBack, Is.False, "it did not fall back: the link is over");
            Assert.That(c.CanAccept, Is.False);
        }

        [Test]
        public void ALostCarrierEndsItsPump()
        {
            var go = new GameObject("carrier-lost"); go.SetActive(false);
            var manager = go.AddComponent<FakeTunnelManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            try
            {
                manager.HostTunneled();
                var link = manager.AcceptCoreLink();
                var pump = new CoreLinkPump(link, 1, 1, new PhotonWire(), false, 16);
                var pipe = manager.Pipes[link.Id];
                pipe.WouldBlockWrites = 5; // core is full: an accepted chunk waits for it
                pump.Receive(1, 0, new byte[] { 1 });
                Assert.That(pump.ChunksWaiting, Is.EqualTo(1));
                pump.Lose();
                Assert.That(pump.Ended, Is.True);
                Assert.That(pump.CarrierLost, Is.True);
                Assert.That(pump.ChunksWaiting, Is.EqualTo(0), "nothing keeps waiting at the gap");
                Assert.That(manager.ClosedLinks, Does.Contain(link.Id));
            }
            finally { manager.OnDestroy(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // Regression: a failed RTCPeerConnection must not be kept alive until the link closes.
        [Test]
        public void AFailedWebRtcTransportReleasesItsNativeConnectionOnTheNextTick()
        {
            var transport = new WebRtcPeerTransport(new string[0]);
            typeof(WebRtcPeerTransport).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(transport, new object[] { "ICE failed" });
            Assert.That(transport.State, Is.EqualTo(PeerTransportState.Failed));
            Assert.That(transport.NativeReleased, Is.False, "Fail runs inside the library's callback: nothing is freed there");
            transport.Tick();
            Assert.That(transport.NativeReleased, Is.True);
            transport.Dispose();
        }

        [Test]
        public void ThePhotonPathAcceptsOnlyWhileItsWireCan()
        {
            var wire = new GatedWire();
            var c = new PeerCoreLinkCarrier(wire, null, () => _now, 5, offerer: false);
            Assert.That(c.CanAccept, Is.False);
            wire.Open = true;
            Assert.That(c.CanAccept, Is.True);
        }

        private sealed class GatedWire : IBackpressuredWire
        {
            public bool Open;
            public bool CanAccept => Open;
            public void Send(int peer, int epoch, int sequence, byte[] bytes) { }
        }

        [Test]
        public void NoTransportMeansThePhotonWireFromTheStart()
        {
            var photon = new PhotonWire();
            var c = new PeerCoreLinkCarrier(photon, null, () => _now, 5, offerer: false);
            Assert.That(c.Negotiating, Is.False);
            Assert.That(c.CanAccept, Is.True);
            Assert.That(c.FellBack, Is.False, "nothing was attempted, so nothing fell back");
            c.Send(1, 1, 0, new byte[] { 1 });
            Assert.That(photon.Sent.Count, Is.EqualTo(1));
        }

        [Test]
        public void ABufferedChannelStopsThePumpReadingCore()
        {
            var photon = new PhotonWire();
            var t = new FakeTransport();
            var c = Carrier(photon, t);
            t.State = PeerTransportState.Open; c.Tick();
            // The window is libwebrtc's own queue limit, and one more frame past it is refused (not an invented number).
            Assert.That(PeerCoreLinkCarrier.MaxQueuedSendDataBytes, Is.EqualTo(16L * 1024 * 1024));
            t.BufferedBytes = PeerCoreLinkCarrier.MaxQueuedSendDataBytes - (CoreLinkPump.DefaultMaxChunkBytes + 8);
            Assert.That(c.CanAccept, Is.True, "one more frame still fits");
            t.BufferedBytes += 1;
            Assert.That(c.CanAccept, Is.False, "one more frame would pass the channel's queue limit");

            var go = new GameObject("carrier-backpressure"); go.SetActive(false);
            var manager = go.AddComponent<FakeTunnelManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            try
            {
                manager.HostTunneled();
                var link = manager.AcceptCoreLink();
                var pipe = manager.Pipes[link.Id];
                pipe.ToPeer.AddRange(new byte[100]);
                var pump = new CoreLinkPump(link, 1, 1, c, false, 16);
                pump.Pump();
                Assert.That(t.Sent, Is.Empty);
                Assert.That(pipe.ToPeer.Count, Is.EqualTo(100), "the bytes stay in core");
                t.BufferedBytes = 0;
                pump.Pump();
                Assert.That(t.Sent.Count, Is.GreaterThan(0));
                Assert.That(pipe.ToPeer, Is.Empty);
            }
            finally { manager.OnDestroy(); UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ChunksThatTookDifferentPathsReachCoreInOrderAndAGapEndsTheLink()
        {
            var go = new GameObject("carrier-mixed"); go.SetActive(false);
            var manager = go.AddComponent<FakeTunnelManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
            try
            {
                manager.HostTunneled();
                var link = manager.AcceptCoreLink();
                var pump = new CoreLinkPump(link, 1, 1, new PhotonWire(), false, 16);
                // Chunk 0 over Photon, chunk 1 over the direct path: in order, so the stream is whole.
                pump.Receive(1, 0, new byte[] { 1, 2 });
                pump.Receive(1, 1, new byte[] { 3, 4 });
                Assert.That(manager.Pipes[link.Id].FromPeer, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                // Chunk 3 before chunk 2: a hole. There is no reorder buffer; the link ends.
                pump.Receive(1, 3, new byte[] { 7 });
                Assert.That(pump.Ended, Is.True);
                Assert.That(manager.Pipes[link.Id].FromPeer, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            }
            finally { manager.OnDestroy(); UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void TwoCarriersNegotiateThroughTheSignalsTheCallerCarries()
        {
            var host = new FakeTransport(); var join = new FakeTransport();
            host.Remote = join; join.Remote = host;
            host.Signal += json => join.ReceiveSignal(json);
            join.Signal += json => host.ReceiveSignal(json);
            var hc = new PeerCoreLinkCarrier(new PhotonWire(), host, () => _now, 5, offerer: true);
            var jc = new PeerCoreLinkCarrier(new PhotonWire(), join, () => _now, 5, offerer: false);
            hc.Tick(); jc.Tick();
            Assert.That(hc.Active, Is.EqualTo(CoreLinkCarrier.WebRtc));
            Assert.That(jc.Active, Is.EqualTo(CoreLinkCarrier.WebRtc));
            (int, int, byte[]) got = default;
            jc.Chunk += (e, s, b) => got = (e, s, b);
            hc.Send(2, 1, 0, new byte[] { 7 });
            Assert.That(got.Item3, Is.EqualTo(new byte[] { 7 }));
        }

        [Test]
        public void DisposingTheCarrierClosesTheTransport()
        {
            var t = new FakeTransport();
            var c = Carrier(new PhotonWire(), t);
            c.Dispose();
            Assert.That(t.Disposed, Is.True);
        }

        [Test]
        public void TheDeclaredDefaultsAreTheCitedOnes()
        {
            var s = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(s.useWebRtc, Is.True);
                Assert.That(s.stunServers, Is.EqualTo(new[] { "stun:stun.l.google.com:19302" }));
                Assert.That(s.webRtcIceTimeoutSeconds, Is.EqualTo(5f));
                Assert.That(CoreLinkPump.DefaultMaxChunkBytes, Is.EqualTo(16 * 1024));
            }
            finally { UnityEngine.Object.DestroyImmediate(s); }
        }
    }
}
#endif
