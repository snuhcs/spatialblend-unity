// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using FakeTunnelManager = SpatialBlend.Tests.TunnelFixtures.FakeTunnelManager;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The tunneled core link (ABI 4.5) at the manager and pump level: hosting and joining need no address, a link
    /// is one ordered byte stream to one peer, and the pump carries it over whatever the carrier is. The native
    /// library is replaced by <see cref="FakeTunnelManager"/>'s in-memory pipes.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class CoreLinkTests
    {
        private GameObject _object;
        private FakeTunnelManager _manager;
        private SpatialBlendConnectionState _lastState;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("core-link-manager");
            _object.SetActive(false);
            _manager = _object.AddComponent<FakeTunnelManager>();
            typeof(SpatialBlendManager).GetField("_connectOnAwake", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_manager, false);
            _manager.ConnectionStateChanged += state => _lastState = state;
        }

        [TearDown]
        public void TearDown()
        {
            _object.GetComponent<SpatialBlendManager>()?.OnDestroy();
            UnityEngine.Object.DestroyImmediate(_object);
        }

        // ---- The manager ---------------------------------------------------------------------------------------

        [Test]
        public void HostTunneledNeedsNoPortAndNoAddress()
        {
            _manager.HostTunneled();
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
            Assert.That(_manager.Initialized, Is.True);
            Assert.That(_manager.IsHost(), Is.True);
            Assert.That(_manager.Port, Is.Zero, "no port was bound or advertised");
            Assert.That(_manager.HostTunneledCalls, Is.EqualTo(1));
        }

        [Test]
        public void AFailedTunneledHostFailsTheConnection()
        {
            _manager.HostFails = true;
            _manager.HostTunneled();
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Failed));
            Assert.That(_manager.Initialized, Is.False);
            Assert.That(_manager.DestroyedSessions.Count, Is.EqualTo(1), "the session it created is released");
        }

        [Test]
        public void LinksAreAcceptedOnlyByATunneledHost()
        {
            Assert.That(_manager.AcceptCoreLink(), Is.Null, "nothing is hosted yet");
            _manager.HostTunneled();
            CoreLink a = _manager.AcceptCoreLink();
            CoreLink b = _manager.AcceptCoreLink();
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Id, Is.Not.EqualTo(b.Id));
            _manager.AcceptFails = true;
            Assert.That(_manager.AcceptCoreLink(), Is.Null);
        }

        [Test]
        public void AJoinStaysConnectingUntilItsHandleCompletes()
        {
            CoreLink link = _manager.JoinTunneled();
            Assert.That(link, Is.Not.Null, "the link is returned at once so it can be pumped");
            Assert.That(_manager.IsJoining, Is.True);
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connecting));
            Assert.That(_manager.Initialized, Is.False);

            _manager.PollTunneledJoin();
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connecting), "the host has not answered");

            _manager.JoinCompleted = true;
            _manager.PollTunneledJoin();
            Assert.That(_manager.IsJoining, Is.False);
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
            Assert.That(_manager.Initialized, Is.True);
            Assert.That(_manager.IsHost(), Is.False);
            Assert.That(_manager.JoinLink, Is.SameAs(link));
        }

        [Test]
        public void ASecondJoinOrHostWhileJoiningIsRefused()
        {
            Assert.That(_manager.JoinTunneled(), Is.Not.Null);
            Assert.That(_manager.JoinTunneled(), Is.Null);
            _manager.HostTunneled();
            Assert.That(_manager.HostTunneledCalls, Is.EqualTo(0));
            Assert.That(_manager.JoinTunneledCalls, Is.EqualTo(1));
        }

        [Test]
        public void AJoinThatFailsFailsTheConnectionAndReleasesItsSession()
        {
            _manager.JoinTunneled();
            _manager.JoinError = "The host did not complete the core link over the tunnel.";
            _manager.PollTunneledJoin();
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Failed));
            Assert.That(_manager.ConnectionError, Does.Contain("did not complete"));
            Assert.That(_manager.IsJoining, Is.False);
            Assert.That(_manager.Initialized, Is.False);
            Assert.That(_manager.DestroyedSessions.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheManagersUpdatePollsAPendingJoin()
        {
            _manager.JoinTunneled();
            _manager.JoinCompleted = true;
            _manager.Update();
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
        }

        [Test]
        public void ALinkReadsAndWritesTheNativeStreamAndEndsWithItsConnection()
        {
            _manager.HostTunneled();
            CoreLink link = _manager.AcceptCoreLink();
            var pipe = _manager.Pipes[link.Id];

            pipe.ToPeer.AddRange(new byte[] { 1, 2, 3, 4, 5 });
            var buffer = new byte[3];
            Assert.That(link.Read(buffer, out int n), Is.EqualTo(CoreLinkRead.Open));
            Assert.That(n, Is.EqualTo(3));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(link.Read(buffer, out n), Is.EqualTo(CoreLinkRead.Open));
            Assert.That(buffer.Take(n), Is.EqualTo(new byte[] { 4, 5 }));
            Assert.That(link.Read(buffer, out n), Is.EqualTo(CoreLinkRead.Open));
            Assert.That(n, Is.Zero, "nothing yet is not the end");

            Assert.That(link.Write(new byte[] { 9, 8, 7 }, 3), Is.EqualTo(CoreLinkWrite.Accepted));
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 9, 8, 7 }));

            _manager.Disconnect();
            Assert.That(link.Read(buffer, out n), Is.EqualTo(CoreLinkRead.Ended));
            Assert.That(link.Write(new byte[] { 1 }, 1), Is.EqualTo(CoreLinkWrite.Closed));
            Assert.That(link.IsClosed, Is.True);
        }

        [Test]
        public void ClosingALinkClosesItNatively()
        {
            _manager.HostTunneled();
            CoreLink link = _manager.AcceptCoreLink();
            link.Close();
            link.Close();
            Assert.That(_manager.ClosedLinks, Is.EqualTo(new[] { link.Id }), "once, however often it is closed");
            Assert.That(link.Read(new byte[4], out _), Is.EqualTo(CoreLinkRead.Ended));
        }

        [Test]
        public void ALinkEndedByCoreReportsEndedAfterItsBytes()
        {
            _manager.HostTunneled();
            CoreLink link = _manager.AcceptCoreLink();
            var pipe = _manager.Pipes[link.Id];
            pipe.ToPeer.AddRange(new byte[] { 7 });
            pipe.Ended = true;
            var buffer = new byte[8];
            Assert.That(link.Read(buffer, out int n), Is.EqualTo(CoreLinkRead.Open));
            Assert.That(n, Is.EqualTo(1));
            Assert.That(link.Read(buffer, out n), Is.EqualTo(CoreLinkRead.Ended));
        }

        // ---- The pump ------------------------------------------------------------------------------------------

        private sealed class Wire : ICoreLinkWire
        {
            public readonly List<(int Peer, int Epoch, int Sequence, byte[] Bytes)> Sent = new();
            public void Send(int peer, int epoch, int sequence, byte[] bytes) => Sent.Add((peer, epoch, sequence, bytes));
        }

        private CoreLinkPump NewPump(out Wire wire, out FakeTunnelManager.Pipe pipe, bool hold = false, int chunk = 4, int epoch = 3)
        {
            _manager.HostTunneled();
            CoreLink link = _manager.AcceptCoreLink();
            pipe = _manager.Pipes[link.Id];
            wire = new Wire();
            return new CoreLinkPump(link, peer: 7, epoch: epoch, wire, hold, chunk);
        }

        [Test]
        public void ThePumpSendsCoreOutputAsNumberedChunksNoLargerThanTheBound()
        {
            var pump = NewPump(out var wire, out var pipe);
            pipe.ToPeer.AddRange(Enumerable.Range(0, 10).Select(i => (byte)i));
            pump.Pump();
            Assert.That(wire.Sent.Select(s => s.Sequence), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(wire.Sent.Select(s => s.Bytes.Length), Is.EqualTo(new[] { 4, 4, 2 }));
            Assert.That(wire.Sent.SelectMany(s => s.Bytes), Is.EqualTo(Enumerable.Range(0, 10).Select(i => (byte)i)));
            Assert.That(wire.Sent.All(s => s.Peer == 7 && s.Epoch == 3), Is.True);
            Assert.That(pump.BytesSent, Is.EqualTo(10));
            pump.Pump();
            Assert.That(wire.Sent.Count, Is.EqualTo(3), "nothing new, nothing sent");
        }

        [Test]
        public void ThePumpWritesChunksIntoCoreInSequence()
        {
            var pump = NewPump(out _, out var pipe);
            Assert.That(pump.Receive(3, 0, new byte[] { 1, 2 }), Is.True);
            Assert.That(pump.Receive(3, 1, new byte[] { 3, 4 }), Is.True);
            Assert.That(pump.Receive(3, 2, new byte[] { 5, 6 }), Is.True);
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
            Assert.That(pump.ChunksWaiting, Is.Zero);
            Assert.That(pump.BytesReceived, Is.EqualTo(6));
        }

        // No reorder buffer, because no carrier documents a window for one. A chunk ahead of the next
        // expected is a lost chunk: the link ends, instead of waiting at the gap and growing a buffer.
        [Test]
        public void AChunkAheadOfTheNextExpectedIsAGapAndEndsTheLinkWithNothingBuffered()
        {
            var pump = NewPump(out _, out var pipe);
            Assert.That(pump.Receive(3, 0, new byte[] { 1, 2 }), Is.True);
            Assert.That(pump.Receive(3, 2, new byte[] { 5, 6 }), Is.False, "chunk 1 never arrived");
            Assert.That(pump.Ended, Is.True);
            Assert.That(pump.CarrierLost, Is.True);
            Assert.That(pump.ChunksWaiting, Is.Zero);
            Assert.That(_manager.ClosedLinks.Count, Is.EqualTo(1), "the link is closed: the loss signal");
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 1, 2 }), "what arrived in order was delivered; nothing after the gap");
        }

        [Test]
        public void AWriteThatWouldBlockTakesNothingAndIsRetriedAfterTheNextRead()
        {
            _manager.HostTunneled();
            CoreLink link = _manager.AcceptCoreLink();
            var pipe = _manager.Pipes[link.Id];
            pipe.WouldBlockWrites = 1;
            Assert.That(link.Write(new byte[] { 1, 2, 3 }, 3), Is.EqualTo(CoreLinkWrite.WouldBlock));
            Assert.That(pipe.FromPeer, Is.Empty, "nothing was taken");
            Assert.That(link.IsClosed, Is.False, "would block is not closed");
            Assert.That(link.Write(new byte[] { 1, 2, 3 }, 3), Is.EqualTo(CoreLinkWrite.Accepted));
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void ThePumpHoldsAChunkCoreWouldNotTakeAndDeliversItOnceInOrderAfterTheNextPump()
        {
            var pump = NewPump(out _, out var pipe);
            pipe.WouldBlockWrites = 3; // each attempt to write the first chunk is blocked once
            Assert.That(pump.Receive(3, 0, new byte[] { 1, 2 }), Is.True, "queued, not lost, and the link is still open");
            Assert.That(pump.Receive(3, 1, new byte[] { 3, 4 }), Is.True);
            Assert.That(pipe.FromPeer, Is.Empty);
            Assert.That(pump.BytesReceived, Is.Zero);
            Assert.That(pump.Ended, Is.False);
            Assert.That(pump.ChunksWaiting, Is.EqualTo(2));
            pump.Pump(); // still blocked once more
            Assert.That(pipe.FromPeer, Is.Empty);
            pump.Pump();
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 1, 2, 3, 4 }), "each chunk went in once, in order");
            Assert.That(pump.BytesReceived, Is.EqualTo(4));
            Assert.That(pump.ChunksWaiting, Is.Zero);
            pump.Pump();
            Assert.That(pipe.FromPeer.Count, Is.EqualTo(4), "nothing is written twice");
        }

        [Test]
        public void ABlockedChunkIsAlwaysWrittenBeforeTheOnesBehindIt()
        {
            var pump = NewPump(out _, out var pipe);
            pipe.WouldBlockWrites = 1;
            pump.Receive(3, 0, new byte[] { 1 });
            Assert.That(pipe.FromPeer, Is.Empty);
            Assert.That(pump.Receive(3, 1, new byte[] { 2 }), Is.True);
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 1, 2 }), "chunk 0 retried first, then chunk 1");
        }

        [Test]
        public void ADuplicateChunkAndAnotherRoundsChunkAreNotDelivered()
        {
            var pump = NewPump(out _, out var pipe);
            Assert.That(pump.Receive(3, 0, new byte[] { 1 }), Is.True);
            Assert.That(pump.Receive(3, 0, new byte[] { 1 }), Is.False, "already delivered");
            Assert.That(pump.Receive(2, 1, new byte[] { 9 }), Is.False, "an older round");
            Assert.That(pump.Receive(4, 1, new byte[] { 9 }), Is.False, "a newer round is not this link's");
            Assert.That(pipe.FromPeer, Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void AHostSidePumpSendsNothingUntilThePeerSpeaks()
        {
            var pump = NewPump(out var wire, out var pipe, hold: true);
            pipe.ToPeer.AddRange(new byte[] { 1, 2 });
            pump.Pump();
            Assert.That(wire.Sent, Is.Empty);
            Assert.That(pump.PeerHasSpoken, Is.False);
            pump.Receive(3, 0, new byte[] { 0 });
            pump.Pump();
            Assert.That(wire.Sent.Count, Is.EqualTo(1));
            Assert.That(pipe.ToPeer, Is.Empty);
        }

        [Test]
        public void ThePumpEndsWhenCoreEndsTheLinkAndDeliversWhatCoreSentFirst()
        {
            var pump = NewPump(out var wire, out var pipe);
            pipe.ToPeer.AddRange(new byte[] { 1, 2, 3 });
            pipe.Ended = true;
            pump.Pump();
            Assert.That(wire.Sent.SelectMany(s => s.Bytes), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(pump.Ended, Is.True);
            Assert.That(pump.Receive(3, 0, new byte[] { 1 }), Is.False, "an ended pump takes nothing");
        }

        [Test]
        public void ClosingThePumpClosesTheLink()
        {
            var pump = NewPump(out _, out var pipe);
            pump.Close();
            Assert.That(pump.Ended, Is.True);
            Assert.That(pipe.Ended, Is.True);
            Assert.That(_manager.ClosedLinks.Count, Is.EqualTo(1));
        }

        [Test]
        public void TwoPumpsLoopTheirBytesThroughOneAnotherWithNoAddressAnywhere()
        {
            // Two managers: a tunneled host and a tunneled joiner, joined only through their pumps.
            var hostObject = new GameObject("loop-host");
            hostObject.SetActive(false);
            var host = hostObject.AddComponent<FakeTunnelManager>();
            try
            {
                host.HostTunneled();
                CoreLink hostEnd = host.AcceptCoreLink();
                CoreLink joinEnd = _manager.JoinTunneled();
                var toJoin = new Queue<(int, int, byte[])>();
                var toHost = new Queue<(int, int, byte[])>();
                var hostPump = new CoreLinkPump(hostEnd, 2, 1, new QueueWire(toJoin), true, 5);
                var joinPump = new CoreLinkPump(joinEnd, 1, 1, new QueueWire(toHost), false, 5);

                var hello = Enumerable.Range(0, 23).Select(i => (byte)(i * 7)).ToArray();
                var reply = Enumerable.Range(0, 11).Select(i => (byte)(200 - i)).ToArray();
                _manager.Pipes[joinEnd.Id].ToPeer.AddRange(hello);   // what the joiner's core says
                host.Pipes[hostEnd.Id].ToPeer.AddRange(reply);        // what the host's core says (held until the joiner spoke)

                for (int frame = 0; frame < 20; frame++)
                {
                    hostPump.Pump();
                    joinPump.Pump();
                    while (toHost.Count > 0) { var c = toHost.Dequeue(); hostPump.Receive(c.Item1, c.Item2, c.Item3); }
                    while (toJoin.Count > 0) { var c = toJoin.Dequeue(); joinPump.Receive(c.Item1, c.Item2, c.Item3); }
                }

                Assert.That(host.Pipes[hostEnd.Id].FromPeer, Is.EqualTo(hello));
                Assert.That(_manager.Pipes[joinEnd.Id].FromPeer, Is.EqualTo(reply));
            }
            finally
            {
                hostObject.GetComponent<SpatialBlendManager>()?.OnDestroy();
                UnityEngine.Object.DestroyImmediate(hostObject);
            }
        }

        private sealed class QueueWire : ICoreLinkWire
        {
            private readonly Queue<(int, int, byte[])> _queue;
            public QueueWire(Queue<(int, int, byte[])> queue) => _queue = queue;
            public void Send(int peer, int epoch, int sequence, byte[] bytes) => _queue.Enqueue((epoch, sequence, bytes));
        }
    }
}
#endif
