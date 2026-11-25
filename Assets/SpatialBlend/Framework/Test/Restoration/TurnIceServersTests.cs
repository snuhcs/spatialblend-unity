// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples;
using Unity.WebRTC;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The TURN credentials in the direct carrier: the Worker's response, the fetch with its timeout and fallback, the
    /// ttl cache, that no credential or client secret reaches a log, the ICE configuration, and the path read from the
    /// selected candidate pair. Native WebRTC is not needed; the end-to-end relay is the two-process test (WebRtcRelayed).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class TurnIceServersTests
    {
        // Obviously fake values: a test must never hold a real credential or client secret.
        private const string User = "fake-user-1234";
        private const string Credential = "fake-credential-5678";
        private const string ClientSecret = "fake-client-secret-9abc";

        public const string Good =
            "{\"iceServers\":[{\"urls\":[\"stun:stun.cloudflare.com:3478\",\"stun:stun.cloudflare.com:443\"]}," +
            "{\"urls\":[\"turn:turn.cloudflare.com:3478?transport=udp\",\"turn:turn.cloudflare.com:443?transport=udp\"," +
            "\"turn:turn.cloudflare.com:3478?transport=tcp\",\"turn:turn.cloudflare.com:80?transport=tcp\"," +
            "\"turns:turn.cloudflare.com:5349?transport=tcp\",\"turns:turn.cloudflare.com:443?transport=tcp\"]," +
            "\"username\":\"" + User + "\",\"credential\":\"" + Credential + "\"}],\"ttl\":14400}";

        public sealed class FakeRequest : ITurnRequest
        {
            public bool IsDone { get; set; }
            public long Status { get; set; }
            public string Text { get; set; }
            public string Error { get; set; }
            public bool Aborted;
            public void Abort() { Aborted = true; }
        }

        public sealed class FakeHttp : ITurnHttp
        {
            public readonly List<(string Url, string Secret, string Body, FakeRequest Request)> Posts = new();
            public ITurnRequest Post(string url, string clientSecret, string jsonBody)
            {
                var request = new FakeRequest();
                Posts.Add((url, clientSecret, jsonBody, request));
                return request;
            }
            public FakeRequest Last => Posts[Posts.Count - 1].Request;
            public void Answer(long status, string text = null, string error = null)
            {
                var r = Last; r.Status = status; r.Text = text; r.Error = error; r.IsDone = true;
            }
        }

        private double _now;
        private readonly List<string> _logs = new();
        private FakeHttp _http;

        private TurnIceProvider Provider(float timeout = 2.5f, float cooldown = 15f, float margin = 15f)
        {
            _now = 100; _logs.Clear(); _http = new FakeHttp();
            return new TurnIceProvider("https://worker.example/ice", ClientSecret, _http, () => _now, timeout, cooldown, margin, _logs.Add);
        }

        // ---- the response ------------------------------------------------------------------------------------------

        [Test]
        public void TheWorkersResponseIsParsedIntoStunAndTurnServers()
        {
            Assert.That(TurnIceResponse.TryParse(Good, out var r, out var error), Is.True, error);
            Assert.That(r.TtlSeconds, Is.EqualTo(14400));
            Assert.That(r.Servers.Count, Is.EqualTo(2));
            Assert.That(r.Servers[0].IsTurn, Is.False);
            Assert.That(r.Servers[0].Username, Is.Null.Or.Empty);
            Assert.That(r.Servers[1].IsTurn, Is.True);
            Assert.That(r.Servers[1].Urls.Length, Is.EqualTo(6));
            Assert.That(r.Servers[1].Username, Is.EqualTo(User));
            Assert.That(r.Servers[1].Credential, Is.EqualTo(Credential));
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{}")]
        [TestCase("{\"iceServers\":[],\"ttl\":10}")]
        [TestCase("{\"iceServers\":[{\"urls\":[\"stun:a:1\"]}]}")]
        [TestCase("{\"iceServers\":[{\"urls\":[\"stun:a:1\"]}],\"ttl\":0}")]
        [TestCase("{\"iceServers\":[{\"urls\":[\"http://not-ice\"]}],\"ttl\":10}")]
        public void AnUnusableResponseIsRejectedWithoutQuotingIt(string json)
        {
            Assert.That(TurnIceResponse.TryParse(json, out var r, out var error), Is.False);
            Assert.That(r, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ATurnServerWithoutCredentialsIsDroppedAndStunKept()
        {
            const string json = "{\"iceServers\":[{\"urls\":[\"stun:a:1\"]},{\"urls\":[\"turn:t:3478\"]},{\"urls\":[\"turn:t2:3478\"],\"username\":\"u\"}],\"ttl\":60}";
            Assert.That(TurnIceResponse.TryParse(json, out var r, out _), Is.True);
            Assert.That(r.Servers.Select(s => s.Urls[0]), Is.EqualTo(new[] { "stun:a:1" }));
        }

        // ---- the fetch, its timeout and its fallback --------------------------------------------------------------

        [Test]
        public void TheFetchPostsTheSessionAndTheSecretToTheWorkerAndKeepsWhatItReturns()
        {
            var p = Provider();
            p.Ensure();
            Assert.That(p.State, Is.EqualTo(TurnIceProvider.FetchState.Pending));
            Assert.That(_http.Posts.Count, Is.EqualTo(1));
            Assert.That(_http.Posts[0].Url, Is.EqualTo("https://worker.example/ice"));
            Assert.That(_http.Posts[0].Secret, Is.EqualTo(ClientSecret));
            Assert.That(_http.Posts[0].Body, Does.Match("^\\{\"session\":\"[0-9a-f]{16}\"\\}$"), "an opaque session id, nothing about the user or the room");
            Assert.That(p.Servers, Is.Empty, "nothing until it answers");
            _now = 100.4; _http.Answer(200, Good); p.Tick();
            Assert.That(p.State, Is.EqualTo(TurnIceProvider.FetchState.Ready));
            Assert.That(p.Servers.Count, Is.EqualTo(2));
            Assert.That(p.LastFetchSeconds, Is.EqualTo(0.4).Within(1e-9));
        }

        [TestCase(401L)]
        [TestCase(403L)]
        [TestCase(502L)]
        [TestCase(503L)]
        [TestCase(204L)]
        public void ANon200AnswerLeavesStunOnly(long status)
        {
            var p = Provider();
            p.Ensure();
            _http.Answer(status, Good); // even a body that would parse is not used on a non-200
            p.Tick();
            Assert.That(p.State, Is.EqualTo(TurnIceProvider.FetchState.Failed));
            Assert.That(p.Servers, Is.Empty);
            Assert.That(p.LastError, Is.EqualTo("HTTP " + status));
        }

        [Test]
        public void AnAnswerThatDoesNotParseLeavesStunOnly()
        {
            var p = Provider();
            p.Ensure();
            _http.Answer(200, "<html>captive portal</html>"); p.Tick();
            Assert.That(p.State, Is.EqualTo(TurnIceProvider.FetchState.Failed));
            Assert.That(p.Servers, Is.Empty);
        }

        [Test]
        public void ATransportErrorLeavesStunOnly()
        {
            var p = Provider();
            p.Ensure();
            _http.Answer(0, null, "connection error"); p.Tick();
            Assert.That(p.State, Is.EqualTo(TurnIceProvider.FetchState.Failed));
            Assert.That(p.LastError, Is.EqualTo("connection error"));
            Assert.That(p.Servers, Is.Empty);
        }

        [Test]
        public void AFetchThatTakesLongerThanItsTimeoutIsAbortedAndLeavesStunOnly()
        {
            var p = Provider(timeout: 2.5f);
            p.Ensure();
            _now = 102.4; p.Tick();
            Assert.That(p.Pending, Is.True);
            Assert.That(p.PendingRemainingSeconds, Is.EqualTo(0.1).Within(1e-9));
            _now = 102.5; p.Tick();
            Assert.That(p.State, Is.EqualTo(TurnIceProvider.FetchState.Failed));
            Assert.That(_http.Last.Aborted, Is.True);
            Assert.That(p.Servers, Is.Empty);
            Assert.That(p.PendingRemainingSeconds, Is.EqualTo(0));
        }

        [Test]
        public void ADeadWorkerIsNotRetriedEveryFrameButAfterTheCooldown()
        {
            var p = Provider(cooldown: 15f);
            p.Ensure(); _http.Answer(503); p.Tick();
            _now = 110; p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(1), "inside the cooldown");
            _now = 115.1; p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(2), "after it");
        }

        [Test]
        public void ServersAreNotServedPastTheirTtl()
        {
            var p = Provider(margin: 0);
            p.Ensure(); _http.Answer(200, Good.Replace("14400", "60")); p.Tick();
            _now = 161; // ttl over: Servers is empty at once, before any refresh answers
            Assert.That(p.Servers, Is.Empty);
        }

        // ---- the cache --------------------------------------------------------------------------------------------

        [Test]
        public void TheAnswerIsKeptForItsTtlLessTheMarginAndThenFetchedAgain()
        {
            var p = Provider(margin: 15f);
            p.Ensure(); _http.Answer(200, Good.Replace("14400", "100")); p.Tick();
            _now = 100 + 84.9; p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(1), "still cached");
            Assert.That(p.Servers.Count, Is.EqualTo(2));
            _now = 100 + 85; p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(2), "refreshed a margin before the ttl ends");
            Assert.That(p.Servers, Is.Empty, "and the old answer is not served while the refresh is pending");
        }

        [Test]
        public void TheMarginNeverExceedsHalfTheTtl()
        {
            var p = Provider(margin: 15f);
            p.Ensure(); _http.Answer(200, Good.Replace("14400", "20")); p.Tick();
            _now = 100 + 9.9; p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(1));
            _now = 100 + 10; p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(2));
        }

        [Test]
        public void AFetchInFlightIsNotStartedTwice()
        {
            var p = Provider();
            p.Ensure(); p.Ensure(); p.Ensure();
            Assert.That(_http.Posts.Count, Is.EqualTo(1));
        }

        // ---- nothing secret in a log ------------------------------------------------------------------------------

        [Test]
        public void NoCredentialUsernameOrClientSecretIsEverLogged()
        {
            // success, every failure kind, and a server that echoes the secret back in an error body
            var scenarios = new Action<TurnIceProvider>[]
            {
                p => { p.Ensure(); _http.Answer(200, Good); p.Tick(); },
                p => { p.Ensure(); _http.Answer(401, "{\"error\":\"" + ClientSecret + " " + Credential + "\"}"); p.Tick(); },
                p => { p.Ensure(); _http.Answer(200, "{\"leak\":\"" + ClientSecret + "\"" ); p.Tick(); },
                p => { p.Ensure(); _http.Answer(0, null, "connection error"); p.Tick(); },
                p => { p.Ensure(); _now += 99; p.Tick(); },
            };
            foreach (var scenario in scenarios)
            {
                var p = Provider();
                scenario(p);
                Assert.That(_logs, Is.Not.Empty);
                foreach (var line in _logs.Concat(new[] { p.LastError ?? string.Empty }))
                {
                    Assert.That(line, Does.Not.Contain(Credential));
                    Assert.That(line, Does.Not.Contain(User));
                    Assert.That(line, Does.Not.Contain(ClientSecret));
                }
            }
        }

        [Test]
        public void AnIceServerPrintsWithoutItsCredential()
        {
            TurnIceResponse.TryParse(Good, out var r, out _);
            var text = string.Join(" ", r.Servers.Select(s => s.ToString()));
            Assert.That(text, Does.Not.Contain(Credential));
            Assert.That(text, Does.Not.Contain(User));
            Assert.That(text, Does.Contain("turn.cloudflare.com"));
        }

        [Test]
        public void TheClientSecretIsNotAFieldOfTheSettingsAsset()
        {
            // The secret reaches the build only through the environment or the git-ignored Resources TextAsset.
            var fields = typeof(SpatialBlendSettings).GetFields().Select(f => f.Name.ToLowerInvariant());
            Assert.That(fields, Has.None.Contain("secret"));
        }

        // ---- configuration: nothing set means nothing changes ---------------------------------------------------

        [TestCase(null, "s", false)]
        [TestCase("", "s", false)]
        [TestCase("https://w/ice", null, false)]
        [TestCase("https://w/ice", "", false)]
        [TestCase("http://w.example/ice", "s", false)]
        [TestCase("not a url", "s", false)]
        [TestCase("https://w.example/ice", "s", true)]
        [TestCase("http://localhost:8787/ice", "s", true)]
        public void TurnIsOnOnlyWithAnHttpsUrlAndASecret(string url, string secret, bool expected)
        {
            Assert.That(TurnIceProvider.IsConfigured(url, secret), Is.EqualTo(expected));
        }

        [Test]
        public void TheDefaultSettingsNameNoWorkerAndAllTransports()
        {
            var s = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            try
            {
                Assert.That(s.turnCredentialsUrl, Is.EqualTo(SpatialBlendSettings.DefaultTurnCredentialsUrl));
                Assert.That(SpatialBlendSettings.DefaultTurnCredentialsUrl, Is.Empty, "no TURN Worker by default: you deploy your own");
                Assert.That(TurnIceProvider.IsConfigured(s.turnCredentialsUrl, "x"), Is.False, "no Worker, no TURN");
                Assert.That(TurnIceProvider.IsConfigured("https://turn.example.invalid/ice", "x"), Is.True);
                Assert.That(s.turnTransports, Is.EqualTo(TurnTransports.All));
                Assert.That(s.webRtcRelayOnly, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(s); }
        }

        // ---- the ICE configuration ------------------------------------------------------------------------------

        private static IReadOnlyList<IceServerConfig> Servers()
        {
            TurnIceResponse.TryParse(Good, out var r, out _);
            return r.Servers;
        }

        [Test]
        public void WithoutTurnTheConfigurationIsTheStunOnlyOneItAlwaysWas()
        {
            var c = WebRtcPeerTransport.BuildConfiguration(new[] { SpatialBlendSettings.DefaultStunServer }, null, TurnTransports.All, relayOnly: false);
            Assert.That(c.iceServers.Length, Is.EqualTo(1));
            Assert.That(c.iceServers[0].urls, Is.EqualTo(new[] { SpatialBlendSettings.DefaultStunServer }));
            Assert.That(c.iceServers[0].username, Is.Null.Or.Empty);
            Assert.That(c.iceTransportPolicy, Is.EqualTo(RTCIceTransportPolicy.All));
            Assert.That(WebRtcPeerTransport.BuildConfiguration(new string[0], null, TurnTransports.All, false).iceServers, Is.Empty);
        }

        [Test]
        public void WithTurnGoogleStunStaysAndTheWorkersServersAreAddedWithTheirCredentials()
        {
            var c = WebRtcPeerTransport.BuildConfiguration(new[] { SpatialBlendSettings.DefaultStunServer }, Servers(), TurnTransports.All, false);
            Assert.That(c.iceServers.Length, Is.EqualTo(3));
            Assert.That(c.iceServers[0].urls, Is.EqualTo(new[] { SpatialBlendSettings.DefaultStunServer }), "Google STUN first, kept as an extra entry");
            Assert.That(c.iceServers[1].urls, Does.Contain("stun:stun.cloudflare.com:3478"));
            var turn = c.iceServers[2];
            Assert.That(turn.urls.Length, Is.EqualTo(6));
            Assert.That(turn.username, Is.EqualTo(User));
            Assert.That(turn.credential, Is.EqualTo(Credential));
        }

        [Test]
        public void TheTransportFilterKeepsStunAndOnlyTheAllowedTurnTransports()
        {
            var tls = WebRtcPeerTransport.BuildConfiguration(new string[0], Servers(), TurnTransports.Tls, false);
            var turn = tls.iceServers.Single(s => s.username == User);
            Assert.That(turn.urls, Is.EqualTo(new[] { "turns:turn.cloudflare.com:5349?transport=tcp", "turns:turn.cloudflare.com:443?transport=tcp" }));
            Assert.That(tls.iceServers.Any(s => s.urls.Contains("stun:stun.cloudflare.com:3478")), Is.True);

            var udp = WebRtcPeerTransport.BuildConfiguration(new string[0], Servers(), TurnTransports.Udp, false).iceServers.Single(s => s.username == User);
            Assert.That(udp.urls.All(u => u.EndsWith("transport=udp")), Is.True);
            var tcp = WebRtcPeerTransport.BuildConfiguration(new string[0], Servers(), TurnTransports.Tcp, false).iceServers.Single(s => s.username == User);
            Assert.That(tcp.urls.All(u => u.StartsWith("turn:") && u.EndsWith("transport=tcp")), Is.True);

            var none = WebRtcPeerTransport.BuildConfiguration(new string[0], Servers(), TurnTransports.None, false);
            Assert.That(none.iceServers.Any(s => s.username == User), Is.False, "no TURN transport allowed: no TURN server");
        }

        [Test]
        public void ForcedRelayIsTheIceTransportPolicy()
        {
            Assert.That(WebRtcPeerTransport.BuildConfiguration(new string[0], Servers(), TurnTransports.All, true).iceTransportPolicy, Is.EqualTo(RTCIceTransportPolicy.Relay));
        }

        [Test]
        public void ATransportWaitsForAPendingFetchAndKeepsThePeersEarlySignalsWithoutANativeConnection()
        {
            var p = Provider();
            p.Ensure();
            var t = new WebRtcPeerTransport(new[] { SpatialBlendSettings.DefaultStunServer }, false, p);
            try
            {
                t.Start(offerer: false);
                Assert.That(t.State, Is.EqualTo(PeerTransportState.Negotiating));
                Assert.That(t.NativeReleased, Is.False);
                t.ReceiveSignal("{\"t\":\"offer\",\"sdp\":\"x\"}"); // kept, not lost and not an error
                Assert.That(t.State, Is.EqualTo(PeerTransportState.Negotiating));
                t.Tick(); // the fetch is still pending: still waiting
                Assert.That(t.State, Is.EqualTo(PeerTransportState.Negotiating));
            }
            finally { t.Dispose(); }
        }

        // ---- the path ---------------------------------------------------------------------------------------------

        [TestCase("host", null, "host", CoreLinkPathKind.Direct, RelayTransport.Unknown)]
        [TestCase("srflx", null, "srflx", CoreLinkPathKind.Direct, RelayTransport.Unknown)]
        [TestCase("prflx", null, "host", CoreLinkPathKind.Direct, RelayTransport.Unknown)]
        [TestCase("relay", "udp", "relay", CoreLinkPathKind.TurnRelay, RelayTransport.Udp)]
        [TestCase("relay", "tcp", "host", CoreLinkPathKind.TurnRelay, RelayTransport.Tcp)]
        [TestCase("relay", "tls", "srflx", CoreLinkPathKind.TurnRelay, RelayTransport.Tls)]
        [TestCase("host", null, "relay", CoreLinkPathKind.TurnRelay, RelayTransport.Unknown)]
        [TestCase("relay", null, "relay", CoreLinkPathKind.TurnRelay, RelayTransport.Unknown)]
        [TestCase(null, null, null, CoreLinkPathKind.Unknown, RelayTransport.Unknown)]
        [TestCase("host", null, null, CoreLinkPathKind.Unknown, RelayTransport.Unknown)]
        public void TheSelectedPairBecomesAPath(string local, string relayProtocol, string remote, CoreLinkPathKind kind, RelayTransport relay)
        {
            var path = CoreLinkPath.FromCandidates(local, relayProtocol, remote);
            Assert.That(path.Kind, Is.EqualTo(kind));
            Assert.That(path.Relay, Is.EqualTo(relay));
        }

        [Test]
        public void ThePathPrintsItsKindAndRelayTransport()
        {
            Assert.That(new CoreLinkPath(CoreLinkPathKind.TurnRelay, RelayTransport.Tls).ToString(), Is.EqualTo("TurnRelay(tls)"));
            Assert.That(CoreLinkPath.Photon.ToString(), Is.EqualTo("Photon"));
            Assert.That(new CoreLinkPath(CoreLinkPathKind.Direct, RelayTransport.Udp).Relay, Is.EqualTo(RelayTransport.Unknown), "a transport only goes with a relay");
        }

        private sealed class PathTransport : IPeerDataTransport, IPathReporting
        {
            public PeerTransportState State { get; set; } = PeerTransportState.Idle;
            public long BufferedBytes => 0;
            public CoreLinkPath Path { get; set; } = CoreLinkPath.Unknown;
            public event Action<string> Signal { add { } remove { } }
            public event Action<byte[]> Message { add { } remove { } }
            public void Start(bool offerer) { State = PeerTransportState.Negotiating; }
            public void ReceiveSignal(string json) { }
            public void Send(byte[] message) { }
            public void Tick() { }
            public void Dispose() { }
        }

        private sealed class NoWire : ICoreLinkWire { public void Send(int peer, int epoch, int sequence, byte[] bytes) { } }

        [Test]
        public void TheCarrierReportsUnknownWhileNegotiatingTheTransportsPathWhenOpenAndPhotonAfterAFallback()
        {
            double now = 10;
            var t = new PathTransport();
            var c = new PeerCoreLinkCarrier(new NoWire(), t, () => now, 5, offerer: true);
            Assert.That(c.Path, Is.EqualTo(CoreLinkPath.Unknown));
            now = 11.25; t.State = PeerTransportState.Open; t.Path = new CoreLinkPath(CoreLinkPathKind.TurnRelay, RelayTransport.Tls);
            c.Tick();
            Assert.That(c.Active, Is.EqualTo(CoreLinkCarrier.WebRtc));
            Assert.That(c.Path.ToString(), Is.EqualTo("TurnRelay(tls)"));
            Assert.That(c.OpenedAfterSeconds, Is.EqualTo(1.25).Within(1e-9), "the measure for the ICE timeout");

            var failing = new PathTransport();
            var f = new PeerCoreLinkCarrier(new NoWire(), failing, () => now, 5, offerer: true);
            now += 5; f.Tick();
            Assert.That(f.FellBack, Is.True);
            Assert.That(f.Path, Is.EqualTo(CoreLinkPath.Photon));
            Assert.That(new PeerCoreLinkCarrier(new NoWire(), null, () => now, 5, offerer: false).Path, Is.EqualTo(CoreLinkPath.Photon), "no direct transport: Photon from the start");
        }
    }
}
#endif
