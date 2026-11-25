// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace SpatialBlend
{
    /// <summary>Which transports of the TURN servers the direct carrier may use (a venue diagnostic: forbid UDP to prove TLS carries).</summary>
    [Flags]
    public enum TurnTransports
    {
        None = 0,
        Udp = 1,
        Tcp = 2,
        Tls = 4,
        All = Udp | Tcp | Tls,
    }

    /// <summary>The path the core link's bytes take, as seen at the venue.</summary>
    public enum CoreLinkPathKind
    {
        /// <summary>Not known yet: the link is still negotiating, or the selected candidate pair has not been read.</summary>
        Unknown,
        /// <summary>A WebRTC data channel on a host or server-reflexive (STUN) pair: peer to peer.</summary>
        Direct,
        /// <summary>A WebRTC data channel relayed by a TURN server.</summary>
        TurnRelay,
        /// <summary>Fusion reliable data through the Photon relay (the fallback).</summary>
        Photon,
    }

    /// <summary>The transport between this peer and the TURN server, when the path is a relay.</summary>
    public enum RelayTransport
    {
        Unknown,
        Udp,
        Tcp,
        Tls,
    }

    public readonly struct CoreLinkPath : IEquatable<CoreLinkPath>
    {
        public CoreLinkPath(CoreLinkPathKind kind, RelayTransport relay = RelayTransport.Unknown)
        {
            Kind = kind;
            Relay = kind == CoreLinkPathKind.TurnRelay ? relay : RelayTransport.Unknown;
        }

        public CoreLinkPathKind Kind { get; }

        /// <summary>The TURN transport; only meaningful for <see cref="CoreLinkPathKind.TurnRelay"/>.</summary>
        public RelayTransport Relay { get; }

        public static readonly CoreLinkPath Unknown = new CoreLinkPath(CoreLinkPathKind.Unknown);
        public static readonly CoreLinkPath Photon = new CoreLinkPath(CoreLinkPathKind.Photon);

        public bool Equals(CoreLinkPath other) => Kind == other.Kind && Relay == other.Relay;
        public override bool Equals(object obj) => obj is CoreLinkPath other && Equals(other);
        public override int GetHashCode() => ((int)Kind * 397) ^ (int)Relay;

        public override string ToString() =>
            Kind == CoreLinkPathKind.TurnRelay ? "TurnRelay(" + Relay.ToString().ToLowerInvariant() + ")" : Kind.ToString();

        /// <summary>
        /// The path of a selected ICE candidate pair, from the candidate types and the local relay protocol that
        /// <c>getStats</c> reports ("host", "srflx", "prflx" or "relay"; "udp", "tcp" or "tls"). A relay on either side
        /// is a TURN path. The remote side's relay protocol is not reported to this peer, so a path that is relayed only
        /// by the remote candidate has an unknown transport.
        /// </summary>
        public static CoreLinkPath FromCandidates(string localType, string localRelayProtocol, string remoteType)
        {
            bool localRelay = string.Equals(localType, "relay", StringComparison.OrdinalIgnoreCase);
            bool remoteRelay = string.Equals(remoteType, "relay", StringComparison.OrdinalIgnoreCase);
            if (localRelay || remoteRelay)
                return new CoreLinkPath(CoreLinkPathKind.TurnRelay, localRelay ? ParseRelay(localRelayProtocol) : RelayTransport.Unknown);
            if (IsDirectType(localType) && IsDirectType(remoteType)) return new CoreLinkPath(CoreLinkPathKind.Direct);
            return Unknown;
        }

        private static bool IsDirectType(string type) =>
            string.Equals(type, "host", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "srflx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "prflx", StringComparison.OrdinalIgnoreCase);

        private static RelayTransport ParseRelay(string protocol)
        {
            switch ((protocol ?? string.Empty).ToLowerInvariant())
            {
                case "udp": return RelayTransport.Udp;
                case "tcp": return RelayTransport.Tcp;
                case "tls": return RelayTransport.Tls;
                default: return RelayTransport.Unknown;
            }
        }
    }

    /// <summary>One entry of the WebRTC <c>iceServers</c> list. Its <see cref="ToString"/> never shows the credential.</summary>
    public sealed class IceServerConfig
    {
        public string[] Urls = Array.Empty<string>();
        public string Username;
        public string Credential;

        public bool IsTurn => Urls.Any(u => IsTurnUrl(u));
        public static bool IsTurnUrl(string url) => url != null && (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));

        /// <summary>The transport a TURN URL asks for: <c>turns:</c> is TLS, <c>?transport=tcp</c> is TCP, anything else UDP.</summary>
        public static TurnTransports TransportOf(string turnUrl)
        {
            if (turnUrl.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)) return TurnTransports.Tls;
            return turnUrl.IndexOf("transport=tcp", StringComparison.OrdinalIgnoreCase) >= 0 ? TurnTransports.Tcp : TurnTransports.Udp;
        }

        public override string ToString() => "ice[" + string.Join(",", Urls) + (Credential != null ? " with credential" : string.Empty) + "]";
    }

    /// <summary>The Worker's answer: <c>{"iceServers":[{"urls":[...],"username":"..","credential":".."}],"ttl":14400}</c>.</summary>
    public sealed class TurnIceResponse
    {
        public IReadOnlyList<IceServerConfig> Servers;
        public double TtlSeconds;

        [Serializable] private sealed class WireServer { public string[] urls; public string username; public string credential; }
        [Serializable] private sealed class WireResponse { public WireServer[] iceServers; public double ttl; }

        /// <summary>
        /// Parses and validates. A server without a usable URL is dropped, and so is a TURN server without a username
        /// and a credential (it could only fail to allocate). No server left, or no positive ttl, is an error. The error
        /// text never carries any part of the response.
        /// </summary>
        public static bool TryParse(string json, out TurnIceResponse response, out string error)
        {
            response = null;
            WireResponse wire;
            try { wire = JsonUtility.FromJson<WireResponse>(json); }
            catch (Exception) { error = "the response is not JSON"; return false; }
            if (wire == null || wire.iceServers == null) { error = "the response has no iceServers"; return false; }
            if (!(wire.ttl > 0)) { error = "the response has no positive ttl"; return false; }
            var servers = new List<IceServerConfig>();
            foreach (var s in wire.iceServers)
            {
                if (s == null || s.urls == null) continue;
                var urls = s.urls.Where(u => IsIceUrl(u)).ToArray();
                if (urls.Length == 0) continue;
                var server = new IceServerConfig { Urls = urls, Username = s.username, Credential = s.credential };
                if (server.IsTurn && (string.IsNullOrEmpty(s.username) || string.IsNullOrEmpty(s.credential))) continue;
                servers.Add(server);
            }
            if (servers.Count == 0) { error = "the response has no usable ICE server"; return false; }
            response = new TurnIceResponse { Servers = servers, TtlSeconds = wire.ttl };
            error = null;
            return true;
        }

        private static bool IsIceUrl(string u) =>
            u != null && (u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("stuns:", StringComparison.OrdinalIgnoreCase) || IceServerConfig.IsTurnUrl(u));

        /// <summary>The servers with only the TURN transports in <paramref name="allowed"/>; STUN entries stay. An entry left without a URL is dropped.</summary>
        public static IReadOnlyList<IceServerConfig> Filter(IEnumerable<IceServerConfig> servers, TurnTransports allowed)
        {
            var kept = new List<IceServerConfig>();
            foreach (var s in servers)
            {
                var urls = s.Urls.Where(u => !IceServerConfig.IsTurnUrl(u) || (IceServerConfig.TransportOf(u) & allowed) != 0).ToArray();
                if (urls.Length > 0) kept.Add(new IceServerConfig { Urls = urls, Username = s.Username, Credential = s.Credential });
            }
            return kept;
        }
    }

    /// <summary>One in-flight POST to the credentials Worker.</summary>
    public interface ITurnRequest
    {
        bool IsDone { get; }
        /// <summary>HTTP status, or 0 when none arrived.</summary>
        long Status { get; }
        string Text { get; }
        /// <summary>A transport error, without the URL or any header.</summary>
        string Error { get; }
        void Abort();
    }

    public interface ITurnHttp
    {
        ITurnRequest Post(string url, string clientSecret, string jsonBody);
    }

    /// <summary>The shipped HTTP: a <see cref="UnityWebRequest"/> polled from the main thread.</summary>
    public sealed class UnityTurnHttp : ITurnHttp
    {
        public ITurnRequest Post(string url, string clientSecret, string jsonBody)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(jsonBody)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-SpatialBlend-Client", clientSecret);
            request.SendWebRequest();
            return new Pending(request);
        }

        private sealed class Pending : ITurnRequest
        {
            private readonly UnityWebRequest _request;
            public Pending(UnityWebRequest request) { _request = request; }
            public bool IsDone => _request.isDone;
            public long Status => _request.responseCode;
            public string Text => _request.downloadHandler?.text;
            public string Error => _request.result == UnityWebRequest.Result.ConnectionError ? "connection error" : _request.result == UnityWebRequest.Result.DataProcessingError ? "data error" : null;
            public void Abort()
            {
                try { _request.Abort(); } catch (Exception) { }
                try { _request.Dispose(); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Short-lived TURN credentials from the Cloudflare Worker (tools/turn-credentials-worker), fetched once and kept for
    /// their ttl. Main thread: <see cref="Tick"/> every frame. Nothing here can fail the core link: a failed, slow or
    /// refused fetch leaves <see cref="Servers"/> empty and the carrier on STUN alone, then on Photon as before. No
    /// credential, and not the client secret, is ever logged or put in an error text.
    /// </summary>
    public sealed class TurnIceProvider
    {
        public enum FetchState { Idle, Pending, Ready, Failed }

        private readonly string _url;
        private readonly string _secret;
        private readonly ITurnHttp _http;
        private readonly Func<double> _now;
        private readonly float _timeoutSeconds;
        private readonly float _failureCooldownSeconds;
        private readonly float _refreshMarginSeconds;
        private readonly Action<string> _log;
        private readonly string _session = Guid.NewGuid().ToString("N").Substring(0, 16);
        private ITurnRequest _request;
        private double _startedAt, _failedAt, _validUntil;
        private IReadOnlyList<IceServerConfig> _servers = Array.Empty<IceServerConfig>();

        /// <param name="timeoutSeconds">How long one fetch may take. The caller derives it from the ICE budget.</param>
        /// <param name="failureCooldownSeconds">After a failed fetch, no new one starts for this long (a dead Worker is not retried each frame).</param>
        /// <param name="refreshMarginSeconds">A cached answer is replaced this long before its ttl ends, so a link set up now still holds valid credentials while it connects.</param>
        public TurnIceProvider(string url, string clientSecret, ITurnHttp http, Func<double> now, float timeoutSeconds,
            float failureCooldownSeconds, float refreshMarginSeconds, Action<string> log = null)
        {
            _url = url;
            _secret = clientSecret;
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _timeoutSeconds = timeoutSeconds;
            _failureCooldownSeconds = failureCooldownSeconds;
            _refreshMarginSeconds = refreshMarginSeconds;
            _log = log;
        }

        /// <summary>The URL is https (or loopback, for a local Worker) and a client secret is present: otherwise nothing is fetched.</summary>
        public static bool IsConfigured(string url, string clientSecret)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(clientSecret)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        }

        public FetchState State { get; private set; } = FetchState.Idle;
        public bool Pending => State == FetchState.Pending;
        public string LastError { get; private set; }
        /// <summary>Seconds the last completed fetch took (successful or not); 0 before any.</summary>
        public double LastFetchSeconds { get; private set; }
        public float TimeoutSeconds => _timeoutSeconds;

        /// <summary>Seconds left before the pending fetch gives up; 0 when none is pending.</summary>
        public double PendingRemainingSeconds => Pending ? Math.Max(0, _startedAt + _timeoutSeconds - _now()) : 0;

        /// <summary>The cached servers while their ttl holds, otherwise none.</summary>
        public IReadOnlyList<IceServerConfig> Servers => State == FetchState.Ready && _now() < _validUntil ? _servers : Array.Empty<IceServerConfig>();

        /// <summary>Starts a fetch unless one is pending, the cache still holds, or the last one failed a moment ago.</summary>
        public void Ensure()
        {
            if (State == FetchState.Pending) return;
            if (State == FetchState.Ready && _now() < _validUntil) return;
            if (State == FetchState.Failed && _now() < _failedAt + _failureCooldownSeconds) return;
            _startedAt = _now();
            try
            {
                _request = _http.Post(_url, _secret, JsonUtility.ToJson(new SessionBody { session = _session }));
                State = FetchState.Pending;
            }
            catch (Exception)
            {
                Fail("the request could not start");
            }
        }

        [Serializable] private sealed class SessionBody { public string session; }

        public void Tick()
        {
            if (State != FetchState.Pending) return;
            if (_request.IsDone) { Finish(); return; }
            if (_now() >= _startedAt + _timeoutSeconds)
            {
                try { _request.Abort(); } catch (Exception) { }
                Fail("timed out after " + _timeoutSeconds.ToString("0.#") + " s");
            }
        }

        private void Finish()
        {
            var request = _request;
            _request = null;
            LastFetchSeconds = _now() - _startedAt;
            if (request.Status != 200)
            {
                Fail(request.Status == 0 ? (request.Error ?? "no response") : "HTTP " + request.Status);
                return;
            }
            if (!TurnIceResponse.TryParse(request.Text, out var response, out var error)) { Fail(error); return; }
            _servers = response.Servers;
            double margin = Math.Min(_refreshMarginSeconds, response.TtlSeconds / 2);
            _validUntil = _now() + response.TtlSeconds - margin;
            State = FetchState.Ready;
            LastError = null;
            _log?.Invoke("TURN: " + _servers.Count + " ICE servers, ttl " + response.TtlSeconds.ToString("0") + " s, fetched in " + LastFetchSeconds.ToString("0.00") + " s");
        }

        private void Fail(string reason)
        {
            _request = null;
            LastFetchSeconds = _now() - _startedAt;
            _failedAt = _now();
            State = FetchState.Failed;
            LastError = reason;
            _servers = Array.Empty<IceServerConfig>();
            _log?.Invoke("TURN: unavailable (" + reason + "); STUN only");
        }
    }

    /// <summary>
    /// The Worker's client secret, from a source that is not in git: the file named by <c>SPATIALBLEND_TURN_CLIENT_SECRET_FILE</c> or
    /// the <c>SPATIALBLEND_TURN_CLIENT_SECRET</c> environment variable (Editor, tests, a desktop run), or the TextAsset <c>Resources/SpatialBlendTurnClientSecret</c> that the Editor
    /// writes from <c>~/.config/spatialblend/turn-client-secret</c> (SpatialBlend > TURN > Write Client Secret) and before each build.
    /// Absent, the carrier behaves as it did before TURN existed.
    /// </summary>
    public static class TurnClientSecret
    {
        public const string EnvironmentVariable = "SPATIALBLEND_TURN_CLIENT_SECRET";
        /// <summary>
        /// Names a file that holds the secret. Prefer it to <see cref="EnvironmentVariable"/> for a test run: Unity writes the
        /// whole environment of the process into its log, so a secret in the environment ends up in the log, a path does not.
        /// </summary>
        public const string FileEnvironmentVariable = "SPATIALBLEND_TURN_CLIENT_SECRET_FILE";
        public const string ResourceName = "SpatialBlendTurnClientSecret";

        public static string Load()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
            var file = Environment.GetEnvironmentVariable(FileEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(file))
            {
                try
                {
                    var text = System.IO.File.ReadAllText(file).Trim();
                    if (text.Length > 0) return text;
                }
                catch (Exception) { /* unreadable: the same as absent */ }
            }
            var asset = Resources.Load<TextAsset>(ResourceName);
            return asset != null && !string.IsNullOrWhiteSpace(asset.text) ? asset.text.Trim() : null;
        }
    }

    /// <summary>
    /// The credentials Worker's URL for this machine. The committed default is <see cref="SpatialBlendSettings.DefaultTurnCredentialsUrl"/>; a person with their
    /// own Worker (tools/setup_sdks.py writes it) overrides it with the <c>SPATIALBLEND_TURN_WORKER_URL</c> environment variable or the git-ignored TextAsset
    /// <c>Resources/SpatialBlendTurnWorkerUrl</c>. The URL is public; the secret that opens it is not here.
    /// </summary>
    public static class TurnWorkerUrl
    {
        public const string EnvironmentVariable = "SPATIALBLEND_TURN_WORKER_URL";
        public const string ResourceName = "SpatialBlendTurnWorkerUrl";

        /// <summary>The override if present, else <paramref name="declared"/> (the URL the settings asset names).</summary>
        public static string Resolve(string declared)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
            var asset = Resources.Load<TextAsset>(ResourceName);
            return asset != null && !string.IsNullOrWhiteSpace(asset.text) ? asset.text.Trim() : declared;
        }
    }

    /// <summary>
    /// One <see cref="TurnIceProvider"/> for the whole process, started early: at app start (<see cref="Boot"/>), or by the
    /// first caller. The credentials are fetched once and reused until their ttl less one connection round, so a room join
    /// finds a warm cache and never waits on the Worker; a cold or expired cache is refreshed in the background
    /// (<see cref="TurnIceDriver"/> ticks and re-ensures every frame) and a link that is made meanwhile uses the bounded
    /// fetch-at-join wait of <see cref="WebRtcPeerTransport"/>. A different URL or secret replaces the provider.
    /// </summary>
    public static class TurnIceService
    {
        private static TurnIceProvider _shared;
        private static string _url, _secret;
        private static GameObject _driver;

        /// <summary>The process's provider, or null when TURN is not configured.</summary>
        public static TurnIceProvider Current => _shared;

        public static TurnIceProvider GetOrCreate(string url, string clientSecret, float fetchTimeoutSeconds, float cooldownSeconds,
            float refreshMarginSeconds, Action<string> log = null)
        {
            if (_shared != null && _url == url && _secret == clientSecret) return _shared;
            _url = url;
            _secret = clientSecret;
            _shared = new TurnIceProvider(url, clientSecret, new UnityTurnHttp(), () => Time.realtimeSinceStartupAsDouble,
                fetchTimeoutSeconds, cooldownSeconds, refreshMarginSeconds, log);
            if (Application.isPlaying && _driver == null)
            {
                _driver = new GameObject("TurnIceDriver") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_driver);
                _driver.AddComponent<TurnIceDriver>();
            }
            return _shared;
        }

        /// <summary>Drops the cache (tests, and a run that measures the fetch-at-join path).</summary>
        public static void Reset()
        {
            _shared = null;
            _url = _secret = null;
        }

        internal static void Tick()
        {
            if (_shared == null) return;
            _shared.Ensure(); // refreshes an expired answer in the background; a no-op while the cache holds or a fetch is pending
            _shared.Tick();
        }

        /// <summary>
        /// Starts the fetch at app start, from the declared settings, before any room flow. Nothing happens without WebRTC,
        /// a Worker URL and a client secret.
        /// </summary>
        public static void Prefetch(SpatialBlendSettings settings)
        {
            if (settings == null || !settings.useWebRtc) return;
            var secret = TurnClientSecret.Load();
            var url = TurnWorkerUrl.Resolve(settings.turnCredentialsUrl);
            if (!TurnIceProvider.IsConfigured(url, secret)) return;
            float ice = settings.webRtcIceTimeoutSeconds >= 1f ? settings.webRtcIceTimeoutSeconds : SpatialBlendSettings.DefaultWebRtcIceTimeoutSeconds;
            float round = settings.connectDeadlineSeconds >= 1f ? settings.connectDeadlineSeconds : SpatialBlendSettings.DefaultConnectDeadlineSeconds;
            GetOrCreate(url, secret, ice * 0.8f, round, round, message => Debug.Log("[TURN] " + message)).Ensure();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot() => Prefetch(Resources.Load<SpatialBlendSettings>("SpatialBlendSettings"));
    }

    /// <summary>Ticks <see cref="TurnIceService"/> every frame.</summary>
    public sealed class TurnIceDriver : MonoBehaviour
    {
        private void Update() => TurnIceService.Tick();
    }
}
