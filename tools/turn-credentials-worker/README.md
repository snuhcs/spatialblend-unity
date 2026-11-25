# TURN credentials Worker

Gives the SpatialBlend app short-lived [Cloudflare TURN](https://developers.cloudflare.com/realtime/turn/) credentials.
With them, the WebRTC core-link carrier can relay through TURN when a network blocks a direct path, for example
venue Wi-Fi that blocks UDP or sits behind a symmetric NAT. TURN over TLS on 443 passes most such networks. The
Photon fallback stays as it is.

## Setup (once)

1. In the Cloudflare dashboard, create a TURN key under **Realtime > TURN**. It gives you a key ID and an API token.
2. Set the Worker's secrets from this directory. Type the values at the prompt; never paste them into a file or a chat.
   ```
   wrangler secret put TURN_KEY_ID
   wrangler secret put TURN_KEY_API_TOKEN
   wrangler secret put CLIENT_SECRET      # e.g. the output of: openssl rand -hex 24
   ```
3. Deploy:
   ```
   wrangler deploy
   ```

## Use

```
curl -s -X POST https://<worker>.workers.dev/ice \
  -H "X-SpatialBlend-Client: $CLIENT_SECRET" -d '{"session":"demo-room"}'
```

It returns `{"iceServers": [...], "ttl": 14400}`. Pass `iceServers` to the WebRTC peer configuration as they are.

## In the Unity app

The core-link carrier (`TurnIceProvider`, `WebRtcPeerTransport`) calls this Worker at room join, caches the answer for its
ttl, and offers the servers next to Google STUN. If the call fails or times out, the link is STUN-only and then Photon, as before.
The client secret reaches a build from `~/.config/spatialblend/turn-client-secret` through a git-ignored
`Assets/Resources/SpatialBlendTurnClientSecret.txt`; it is never in git or in a log. See `docs/webrtc-turn-relay.md` in the
Unity project for the build steps, the path readout (`Direct`, `TurnRelay(udp|tcp|tls)`, `Photon`), the measured timings and
what to verify on Quest.

Measured on 2026-10-18 from a Mac: the Worker answers in 0.87 to 2.01 s (six cold fetches). Forced-relay runs carried the
3 MiB core payload through TURN over UDP, TCP and TLS.

## Notes

- **What the token can do.** The TURN key's API token can only issue credentials for that key, and it lives only in the
  Worker.
- **What `CLIENT_SECRET` stops.** It ships inside the app, so anyone who unpacks the APK can read it. It only keeps
  casual callers out. For a demo that is enough, combined with the short TTL and the per-session `customIdentifier`
  (usage shows per session in Cloudflare). Rotate it after the demo.
- **Billing.** TURN is billed on egress: 1,000 GB/month free, then $0.05/GB. A demo moves MBs.
