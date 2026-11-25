# WebRTC core link through a TURN relay (optional)

The core link between two players rides a WebRTC data channel (`com.unity.webrtc` 3.0.0, `WebRtcPeerTransport`), peer to peer.
Photon's relay (about 16 KB/s) is the fallback. On venue Wi-Fi that blocks UDP or sits behind a symmetric NAT the direct
channel cannot connect with STUN alone. A TURN relay keeps the WebRTC speed there; TURN over TLS on 443 passes most such
networks. Credentials come from a Cloudflare Worker (`tools/turn-credentials-worker`, README there).

**TURN is off unless you deploy a credentials Worker** and configure both its URL and its client secret (below). Without them
nothing is fetched: each link uses STUN, then Photon's relay, and the app works the same, only slower on networks that block a
direct connection. The Worker refuses callers without its client secret, so a Worker URL alone (for example one copied from another
project) turns nothing on.

## What happens at run time

1. App start (`TurnIceService.Boot`, after the first scene loads): if a Worker URL and a client secret are present, the
   process-wide `TurnIceProvider` POSTs `{"session": "<random 16 hex>"}` with header `X-SpatialBlend-Client`, once. Timeout:
   0.8 of the ICE timeout (4 s), derived from the measured fetch times below.
2. The answer is cached for its ttl (4 h) less one connection round (15 s). `TurnIceDriver` re-ensures it every frame, so an
   expired answer is refreshed in the background. A room join (`Spawned`, `NewPump`) finds a valid cache and makes no fetch and
   waits for nothing. Only if the cache is missing or expired at join does the join start the bounded fetch (the fallback): the link then
   waits for it, up to the 4 s bound, and its direct-path budget starts after the wait.
3. Each link's `RTCConfiguration` has Google STUN (always), then the Worker's STUN and TURN servers with username and
   credential. `SpatialBlendSettings.turnTransports` can narrow the TURN transports (udp, tcp, tls).
4. ICE prefers a direct pair; TURN carries the link only when no direct pair works. `webRtcRelayOnly` forces relay.
5. If the fetch fails, times out, returns non-200 or an unusable body, the link is STUN-only (the behaviour before TURN
   existed), then Photon if that does not connect either. If a link is made while the fetch is still pending, it waits for it,
   and the direct path's 5 s budget starts after the wait.
6. Nothing configured (no secret) means nothing is fetched and nothing changes.

No credential, username or client secret is logged; log lines name counts, ttl and timing only.

## Path visibility

Once the data channel opens, the transport reads `GetStats` (`transport.selectedCandidatePairId`, then the pair's local and
remote candidates) into `CoreLinkPath`:

| Path | Meaning |
|---|---|
| `Direct` | host or srflx (STUN) pair, peer to peer |
| `TurnRelay(udp / tcp / tls)` | relayed by TURN; the transport is the local candidate's `relayProtocol` (unknown when only the remote side is relayed) |
| `Photon` | the fallback carrier |
| `Unknown` | still negotiating, or the pair has not been read yet |

`FusionConnectionManager.ActivePath` exposes it. The session panel's carrier line shows `Carrier: direct (WebRTC)`,
`Carrier: WebRTC via TURN relay (tls)` or `Carrier: Photon relay (fallback: ...)`. The manager logs once per link:
`core link to player N: path=TurnRelay(udp), direct channel opened in 2.34 s`.

## Turn TURN on

1. Deploy the Worker with your own Cloudflare TURN key (`tools/turn-credentials-worker/README.md`) and pick its client secret.
2. Point the app at it: `python3 tools/setup_sdks.py --turn-worker-url https://<your worker>/ice` (or `SPATIALBLEND_TURN_WORKER_URL`)
   writes the git-ignored `Assets/Resources/SpatialBlendTurnWorkerUrl.txt`, which wins over `SpatialBlendSettings.turnCredentialsUrl`
   (`Assets/Resources/SpatialBlendSettings.asset`). An empty URL turns TURN off. Leave `useWebRtc` on.
3. Put the client secret in `~/.config/spatialblend/turn-client-secret` (mode 600): `python3 tools/setup_sdks.py --turn-secret-file <file>`.
   Never commit it, never paste it.
4. Build. `SpatialBlend.Editor.TurnClientSecretSource` runs before every build and copies the secret to
   `Assets/Resources/SpatialBlendTurnClientSecret.txt`, which is git-ignored (`.gitignore`; also
   `SpatialBlend > TURN > Write Client Secret`). Check `git status` before every commit.
5. The secret ships inside the app. Anyone who unpacks the APK can read it; it keeps casual callers out of the Worker and
   nothing more. Rotate it when you stop distributing a build (`wrangler secret put CLIENT_SECRET`, then write the new file).

Once a Worker URL is configured, the build guard requires the client secret too (`docs/required-credentials.md`); a build that
should stay without TURN clears the URL, or passes `SPATIALBLEND_ALLOW_NO_TURN=1`.

For editor runs and tests the secret may come from the file named by `SPATIALBLEND_TURN_CLIENT_SECRET_FILE` or the variable
`SPATIALBLEND_TURN_CLIENT_SECRET`. Prefer the file: Unity writes the whole environment of the process into its log.

## Tests

- EditMode (`FreeformRestoration`): `TurnIceServersTests` (response parsing, fallback on failure, timeout and non-200, ttl
  cache, no credential in logs, ICE configuration, path from candidates), plus the carrier and manager cases in
  `PeerCarrierTests` and `FusionConnectionContractTests`.
- Two real Unity processes over the Photon Cloud: `SPATIALBLEND_PHOTON_CLOUD=1 tools/run-two-process-cloud-test.sh MODE` with
  MODE `WebRtc`, `Photon`, `WebRtcThatCannotConnect` or `WebRtcRelayed`. `WebRtcRelayed` sets `iceTransportPolicy = relay`
  with the Worker's servers and asserts the selected pair is a TURN relay while the 3 MiB / 1 MiB payloads arrive byte for byte.
  `SPATIALBLEND_TP_TURN_TRANSPORTS=udp|tcp|tls` narrows the relay transport (forbid UDP to prove TLS carries).
  `WebRtcTurn` is TURN configured but not forced; `SPATIALBLEND_TP_TURN_PREFETCH=0` fetches at join instead of at app start.
- Every `tools/run-*.sh` starts Unity through `unity_clean_env` (`tools/lib/unity-env.sh`): an empty environment plus an allowlist
  (HOME, PATH, USER, LOGNAME, TMPDIR, LANG, LC_ALL, SHELL, ANDROID_HOME / SDK_ROOT / NDK_ROOT, JAVA_HOME, and `SPATIALBLEND_*` knobs
  except names with SECRET, TOKEN, PASSWORD or KEY; a secret is passed as `*_FILE`). Unity writes its whole environment into its log,
  so the shell's API keys never reach one.

## ICE timeout

Forced relay, measured on the Mac (Seoul network, Cloudflare anycast, Photon Cloud signaling), two Unity processes, 6 relay runs of 2 links,
2026-10-18. "Opened" is the time from the start of the link's negotiation to the data channel opening, including the signaling
round trips over Photon:

| Path | Opened (s) | 3 MiB host to joiner |
|---|---|---|
| Direct (STUN, no TURN configured) | 0.59, 0.76 | 83 ms |
| TURN relay, all transports offered (took UDP) | 2.25, 2.40 | 721 ms |
| TURN relay, UDP forbidden (TCP 3478/80) | 2.11, 2.39 | 641 ms |
| TURN relay, only TLS (5349/443) | 1.12, 1.29 | 1178 ms |

A repeat run after the fetch bound changed (UDP relay, all transports) opened in 1.16 and 1.33 s, so one transport varies by about 2x between runs. A third relay run, with the credentials prefetched, opened in 0.60 and 0.73 s, so the spread over runs is 0.6 to 2.4 s. The slowest open of all was 2.40 s, so the 5 s budget has 2.1x headroom here and is kept. TURN allocation adds round trips (the
relay-only runs open 1.5 to 4 times later than the direct pair), but not enough on this network to need a change. This is one
network, in Korea, to a nearby Cloudflare point of presence; a US venue has longer round trips on every leg. The manager
logs the figure for each link (`direct channel opened in N s`), so read it at the venue: if it is near 5 s, raise
`webRtcIceTimeoutSeconds` (the 15 s connection round leaves room up to about 8 s) and cite the new measurement.

The credentials fetch took 0.87 to 2.01 s over the same six runs (an HTTPS request plus the Worker's call to Cloudflare), which is
why its bound is 4 s and not the 2.5 s that half the ICE budget would give.

## What the product does: TURN configured, not forced

`iceTransportPolicy = all` is the product's setting, so TURN is only the way out when no direct pair works. Same two-process
setup, 3 runs per row (6 links), medians, 2026-10-18:

| Config | Selected path | Channel open (host / joiner) | 3 MiB to joiner | Link up (joiner, from players joined) |
|---|---|---|---|---|
| No TURN (STUN only) | `Direct` x6 | 0.79 / 0.59 s | 77 ms (74, 77, 78) | 0.78 s |
| TURN, prefetched at app start | `Direct` x6 | 0.83 / 0.63 s | 91 ms (76, 91, 93) | 0.82 s |
| TURN, fetched at join (cold cache) | `Direct` x6 | 0.80 / 0.60 s | 98 ms (75, 98, 124) | 0.79 s |

TURN configured costs no measurable time and never replaced the direct pair: the selected path was `Direct` in every run. The
host's channel opens later than the joiner's because it starts first and waits for the joiner's answer.

Prefetch against fetch-at-join: the fetch at join took 0.22 to 0.59 s in these runs and overlapped the join (the host fetches at
spawn, before the joiner arrives; the joiner's takes less time than its link setup), so no difference in link time is measurable here
(0.51 to 0.84 s cold, 0.77 to 0.87 s warm). The prefetch is still the safer path because the Worker's latency varies: the app-start
fetches in the warm runs took 0.80 to 2.64 s, and earlier runs 0.87 to 2.01 s. With a warm cache the join does not depend on it at all.

**These two machines are one Mac.** Both processes run on the same host, so the `Direct` pair is a loopback / local-interface pair.
Its 0.6 to 0.8 s and its 77 to 98 ms for 3 MiB say nothing about a real two-device direct path (Wi-Fi, NAT hairpin, a different
network) and must not be compared with the forced-relay timings above, which cross the Internet to a TURN server and back. Measure two
devices at the venue.

## To verify on Quest (not covered by the Mac tests)

- TURN over TLS (`turns:` on 443 and 5349) with the pinned `com.unity.webrtc` 3.0.0 on Android (IL2CPP, arm64): force
  it with `turnTransports = Tls` and `webRtcRelayOnly = true`, and confirm the path line reads `TurnRelay(tls)`.
- The same on the venue network, with UDP blocked, and with the headset on a symmetric NAT.
- That `GetStats` returns `selectedCandidatePairId` on Android; if it does not, the path stays `Unknown` and the link still works.
- That the 5 s ICE timeout holds on the headset's radio (see above for the Mac figures).
