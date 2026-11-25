// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

// Issues short-lived Cloudflare TURN credentials to the SpatialBlend app, so the WebRTC core-link carrier can relay
// through TURN (UDP/TCP/TLS on 443) when a venue's network blocks a direct path. The TURN key's API token stays
// here as a Worker secret and never ships in the app.
//
// POST /ice   header X-SpatialBlend-Client: <CLIENT_SECRET>   body (optional): {"session": "<opaque id>"}
//   -> 200 {"iceServers": [...], "ttl": <seconds>}
//
// Secrets: TURN_KEY_ID, TURN_KEY_API_TOKEN, CLIENT_SECRET.  Var: TTL_SECONDS (Cloudflare's maximum is 48 h).

const GENERATE_URL = (keyId) =>
  `https://rtc.live.cloudflare.com/v1/turn/keys/${encodeURIComponent(keyId)}/credentials/generate-ice-servers`;

// Cloudflare's docs warn that alternate port 53 is blocked by browsers and can time out; it is never useful here.
const usableUrl = (url) => !/:53(\?|$)/.test(url);

function json(status, body) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store" },
  });
}

// Constant-time comparison of two strings, so the client secret is not leaked by timing.
function sameSecret(a, b) {
  const ea = new TextEncoder().encode(a ?? "");
  const eb = new TextEncoder().encode(b ?? "");
  let diff = ea.length ^ eb.length;
  for (let i = 0; i < Math.max(ea.length, eb.length); i++) diff |= (ea[i] ?? 0) ^ (eb[i] ?? 0);
  return diff === 0;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/ice") return json(404, { error: "not found" });
    if (request.method !== "POST") return json(405, { error: "POST only" });
    if (!env.TURN_KEY_ID || !env.TURN_KEY_API_TOKEN || !env.CLIENT_SECRET) {
      return json(503, { error: "worker secrets are not configured" });
    }
    if (!sameSecret(request.headers.get("X-SpatialBlend-Client"), env.CLIENT_SECRET)) {
      return json(401, { error: "unauthorized" });
    }

    let session = "";
    try {
      const body = await request.json();
      if (body && typeof body.session === "string") session = body.session.slice(0, 64);
    } catch (_) {
      // An empty or non-JSON body is allowed: the session id is optional.
    }

    const ttl = Math.min(Number(env.TTL_SECONDS) || 14400, 172800);
    const upstream = await fetch(GENERATE_URL(env.TURN_KEY_ID), {
      method: "POST",
      headers: {
        Authorization: `Bearer ${env.TURN_KEY_API_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify(session ? { ttl, customIdentifier: session } : { ttl }),
    });
    if (!upstream.ok) return json(502, { error: `cloudflare returned ${upstream.status}` });

    const issued = await upstream.json();
    const iceServers = (issued.iceServers ?? [])
      .map((s) => ({ ...s, urls: [].concat(s.urls ?? []).filter(usableUrl) }))
      .filter((s) => s.urls.length > 0);
    return json(200, { iceServers, ttl });
  },
};
