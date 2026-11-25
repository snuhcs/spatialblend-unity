// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using NUnit.Framework;
using NUnitAssert = NUnit.Framework.Assert;
using SpatialBlend.Samples;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Who sent a message is whoever the capability token it carries was issued to
    /// (Fusion's reliable-data callback reports the receiving player, so it names nobody). Neither the key, the
    /// body nor the callback's PlayerRef is ever the identity.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class SpoofedSenderTests
    {
        private static readonly PlayerRef Host = PlayerRef.FromEncoded(2);
        private static readonly PlayerRef Honest = PlayerRef.FromEncoded(3);
        private static readonly PlayerRef Attacker = PlayerRef.FromEncoded(4);

        private readonly List<(PlayerRef From, int Epoch, int Seq, byte[] Bytes)> _chunks = new();
        private readonly List<(PlayerRef From, int Epoch, string Json)> _signals = new();
        private LinkTokens _tokens;

        [SetUp]
        public void SetUp() { _chunks.Clear(); _signals.Clear(); _tokens = new LinkTokens(); }

        private FusionCoreLinkReceiver Receiver() =>
            new FusionCoreLinkReceiver((f, e, s, b) => _chunks.Add((f, e, s, b)), (f, e, j) => _signals.Add((f, e, j)), _tokens);

        private static void Chunk(FusionCoreLinkReceiver r, PlayerRef callbackPlayer, int epoch, byte[] token, byte[] body) =>
            r.OnReliableDataReceived(null, callbackPlayer, FusionCoreLinkKey.Make(epoch, 0), new ArraySegment<byte>(LinkTokens.Frame(token, body)));

        private static void Signal(FusionCoreLinkReceiver r, PlayerRef callbackPlayer, int epoch, byte[] token, string json) =>
            r.OnReliableDataReceived(null, callbackPlayer, FusionCoreLinkSignalKey.Make(epoch, 1),
                new ArraySegment<byte>(LinkTokens.Frame(token, System.Text.Encoding.UTF8.GetBytes(json))));

        [Test]
        public void AChunkWithTheTokenIssuedToAPeerIsDeliveredAsFromThatPeer()
        {
            var r = Receiver();
            var token = _tokens.IssueFor(1, Honest.RawEncoded);
            Chunk(r, Host, 1, token, new byte[] { 1, 2 });
            NUnitAssert.That(_chunks.Single().From, Is.EqualTo(Honest));
            NUnitAssert.That(_chunks.Single().Bytes, Is.EqualTo(new byte[] { 1, 2 }));
            NUnitAssert.That(r.SpoofedDropped, Is.Zero);
        }

        [Test]
        public void TheCallbacksPlayerIsNeverTheIdentity()
        {
            var r = Receiver();
            var honestToken = _tokens.IssueFor(1, Honest.RawEncoded);
            _tokens.IssueFor(1, Attacker.RawEncoded);
            // Whatever PlayerRef the callback reports (the receiver, the attacker, nobody), the token decides.
            foreach (var reported in new[] { Host, Attacker, Honest, PlayerRef.None })
                Chunk(r, reported, 1, honestToken, new byte[] { 5 });
            NUnitAssert.That(_chunks.Select(c => c.From).Distinct().Single(), Is.EqualTo(Honest));
            NUnitAssert.That(_chunks.Count, Is.EqualTo(4));
        }

        [Test]
        public void AChunkWithNoTokenOrAShortOneIsDropped()
        {
            var r = Receiver();
            _tokens.IssueFor(1, Honest.RawEncoded);
            r.OnReliableDataReceived(null, Honest, FusionCoreLinkKey.Make(1, 0), new ArraySegment<byte>(new byte[0]));
            r.OnReliableDataReceived(null, Honest, FusionCoreLinkKey.Make(1, 0), new ArraySegment<byte>(new byte[LinkTokens.Size - 1]));
            NUnitAssert.That(_chunks, Is.Empty);
            NUnitAssert.That(r.SpoofedDropped, Is.EqualTo(2));
        }

        [Test]
        public void AChunkWithAWrongTokenIsDropped()
        {
            var r = Receiver();
            var token = _tokens.IssueFor(1, Honest.RawEncoded);
            var wrong = (byte[])token.Clone(); wrong[LinkTokens.Size - 1] ^= 1;
            Chunk(r, Honest, 1, wrong, new byte[] { 9 });
            Chunk(r, Honest, 1, new byte[LinkTokens.Size], new byte[] { 9 });
            NUnitAssert.That(_chunks, Is.Empty);
            NUnitAssert.That(r.SpoofedDropped, Is.EqualTo(2));
        }

        [Test]
        public void ATokenFromAnotherRoundIsDropped()
        {
            var r = Receiver();
            var old = _tokens.IssueFor(1, Honest.RawEncoded);
            _tokens.IssueFor(2, Honest.RawEncoded);
            Chunk(r, Honest, 2, old, new byte[] { 3 }); // round 1's token replayed in round 2
            NUnitAssert.That(_chunks, Is.Empty);
            _tokens.RetireBelow(2);
            Chunk(r, Honest, 1, old, new byte[] { 3 }); // and after round 1 was retired
            NUnitAssert.That(_chunks, Is.Empty);
            NUnitAssert.That(r.SpoofedDropped, Is.EqualTo(2));
        }

        [Test]
        public void ATokenIssuedToAnotherPeerNeverMakesAMessageComeFromThisOne()
        {
            var r = Receiver();
            var honestToken = _tokens.IssueFor(1, Honest.RawEncoded);
            var attackerToken = _tokens.IssueFor(1, Attacker.RawEncoded);
            NUnitAssert.That(honestToken, Is.Not.EqualTo(attackerToken));
            Chunk(r, Attacker, 1, attackerToken, new byte[] { 7 });
            NUnitAssert.That(_chunks.Single().From, Is.EqualTo(Attacker), "the attacker can only ever speak as the attacker");
        }

        [Test]
        public void ASignalFollowsTheSameRule()
        {
            var r = Receiver();
            var token = _tokens.IssueFor(1, Host.RawEncoded);
            Signal(r, Attacker, 1, new byte[LinkTokens.Size], "{\"t\":\"offer\"}");
            NUnitAssert.That(_signals, Is.Empty);
            NUnitAssert.That(r.SpoofedDropped, Is.EqualTo(1));
            Signal(r, Attacker, 1, token, "{\"t\":\"offer\"}");
            NUnitAssert.That(_signals.Single().From, Is.EqualTo(Host));
        }

        [Test]
        public void ReliableDataThatIsNotOursIsIgnoredNotCountedAsASpoof()
        {
            var r = Receiver();
            r.OnReliableDataReceived(null, Honest, Fusion.Sockets.ReliableKey.FromInts(1234, 0, 0, 0), new ArraySegment<byte>(new byte[] { 1 }));
            NUnitAssert.That(_chunks, Is.Empty);
            NUnitAssert.That(r.ForeignReceived, Is.EqualTo(1));
            NUnitAssert.That(r.SpoofedDropped, Is.Zero);
        }

        [Test]
        public void TokensAreRandomAndTheRightSize()
        {
            var a = _tokens.IssueFor(1, 3); var b = _tokens.IssueFor(1, 4); var c = _tokens.IssueFor(2, 3);
            NUnitAssert.That(a.Length, Is.EqualTo(16));
            NUnitAssert.That(new[] { Convert.ToBase64String(a), Convert.ToBase64String(b), Convert.ToBase64String(c) }.Distinct().Count(), Is.EqualTo(3));
            NUnitAssert.That(_tokens.IssueFor(1, 3), Is.EqualTo(a), "asking again for the same (round, peer) gives the same token");
        }

        // Sending: nothing goes to a peer before it has offered a token, and an offer only ever opens the door to its own source.
        [Test]
        public void NothingIsSentToAPeerUntilItHasOfferedAToken()
        {
            var sent = new List<(int Peer, byte[] Token)>();
            _tokens.WithToken(1, Honest.RawEncoded, t => sent.Add((Honest.RawEncoded, t)));
            NUnitAssert.That(sent, Is.Empty);
            _tokens.AcceptOffer(1, Attacker.RawEncoded, new byte[LinkTokens.Size]); // someone else's offer
            NUnitAssert.That(sent, Is.Empty, "another peer's offer does not release a send to this peer");
            var offered = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
            _tokens.AcceptOffer(1, Honest.RawEncoded, offered);
            NUnitAssert.That(sent.Single().Token, Is.EqualTo(offered));
            _tokens.WithToken(1, Honest.RawEncoded, t => sent.Add((Honest.RawEncoded, t)));
            NUnitAssert.That(sent.Count, Is.EqualTo(2), "once offered, later sends go straight out");
        }

        [Test]
        public void MalformedOrStaleOffersAreRejected()
        {
            NUnitAssert.That(_tokens.AcceptOffer(1, Honest.RawEncoded, new byte[5]), Is.False);
            NUnitAssert.That(_tokens.AcceptOffer(1, Honest.RawEncoded, null), Is.False);
            _tokens.RetireBelow(5);
            NUnitAssert.That(_tokens.AcceptOffer(4, Honest.RawEncoded, new byte[LinkTokens.Size]), Is.False);
            NUnitAssert.That(_tokens.AcceptOffer(5, Honest.RawEncoded, new byte[LinkTokens.Size]), Is.True);
        }

        [Test]
        public void AStrangerCannotGrowTheTableByNamingManyRounds()
        {
            for (int e = 1; e <= 100; e++) _tokens.AcceptOffer(e, Attacker.RawEncoded, new byte[LinkTokens.Size]);
            int released = 0;
            _tokens.WithToken(1, Attacker.RawEncoded, t => released++);
            NUnitAssert.That(released, Is.Zero, "the oldest offers were evicted");
            _tokens.WithToken(100, Attacker.RawEncoded, t => released++);
            NUnitAssert.That(released, Is.EqualTo(1));
        }

        [Test]
        public void AnOfferTravelsOnlyAsTheMessageOfItsStampedSource()
        {
            // The manager takes the offerer from info.Source: here the seam it uses, with the source named by the transport.
            var tokens = new LinkTokens();
            tokens.AcceptOffer(1, Attacker.RawEncoded, Enumerable.Repeat((byte)9, 16).ToArray());
            int toHonest = 0;
            tokens.WithToken(1, Honest.RawEncoded, t => toHonest++);
            NUnitAssert.That(toHonest, Is.Zero, "an offer stamped as the attacker's can never give the attacker's token the honest peer's slot");
        }

        // A data channel is bound to the peer it was negotiated with: it can only feed that peer's link.
        [Test]
        public void APeersDirectChannelOnlyFeedsThatPeersLink()
        {
            var made = new Dictionary<PlayerRef, PeerCarrierTests.FakeTransport>();
            var go = new GameObject("spoof-manager");
            var core = new GameObject("spoof-core"); core.SetActive(false);
            try
            {
                var connection = go.AddComponent<DirectConnection>();
                connection.Roster.Add(connection.Local); connection.Roster.Add(Honest); connection.Roster.Add(Attacker);
                connection.Factory = p => made[p] = new PeerCarrierTests.FakeTransport();
                var manager = core.AddComponent<TunnelFixtures.FakeTunnelManager>();
                typeof(SpatialBlendManager).GetField("_connectOnAwake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(manager, false);
                connection.Core = manager;
                connection.OnPlayerJoined(null, connection.Local);
                var epoch = (int)connection.CurrentEpoch;
                var pipes = manager.Pipes.Values.ToList();
                NUnitAssert.That(made.Count, Is.EqualTo(2));

                // Honest's channel delivers a frame (epoch, sequence 0, payload).
                made[Honest].State = PeerTransportState.Open;
                made[Honest].Deliver(new byte[] { (byte)epoch, 0, 0, 0, 0, 0, 0, 0, 7 });
                var total = pipes.Select(p => p.FromPeer.Count).ToList();
                NUnitAssert.That(total.Sum(), Is.EqualTo(1), "one byte arrived, in exactly one link");
                // The other peer's link got nothing from Honest's channel.
                made[Attacker].State = PeerTransportState.Open;
                NUnitAssert.That(manager.Pipes.Values.Count(p => p.FromPeer.Count == 1), Is.EqualTo(1));
                manager.OnDestroy();
            }
            finally { UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(core); }
        }

        private sealed class DirectConnection : FusionConnectionManager
        {
            public readonly PlayerRef Local = PlayerRef.FromEncoded(1);
            public readonly List<PlayerRef> Roster = new();
            public TunnelFixtures.FakeTunnelManager Core;
            public Func<PlayerRef, IPeerDataTransport> Factory;
            public long CurrentEpoch => (long)typeof(FusionConnectionManager).GetField("_epoch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(this);
            protected override bool IsAuthority => true;
            protected override PlayerRef LocalPlayer => Local;
            protected override List<PlayerRef> ActiveRoster => Roster;
            protected override SpatialBlendConnectionState ConnectionState => Core.ConnectionState;
            protected override void RequestHostTunneled() => Core.HostTunneled();
            protected override CoreLink RequestAcceptLink() => Core.AcceptCoreLink();
            protected override IPeerDataTransport CreatePeerTransport(PlayerRef peer) => Factory(peer);
            protected override void SendSignal(PlayerRef peer, int epoch, byte[] token, string json) { }
            protected override void SendTokenOffer(PlayerRef peer, int epoch, long high, long low) { }
            protected override void BroadcastJoinRound(long epoch) { }
            protected override void BroadcastRetire(long epoch) { }
            protected override void BroadcastStartApps(long epoch) { }
            protected override void RequestDisconnect() { }
        }
    }
}
#endif
