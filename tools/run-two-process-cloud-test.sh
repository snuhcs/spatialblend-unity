#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# The core link between two Unity processes (one runner each) joined to one room by name over the real Photon Cloud,
# using only the PlayerRef Fusion's OnReliableDataReceived reports. Usage: run-two-process-cloud-test.sh MODE
# with MODE one of WebRtc, Photon, WebRtcThatCannotConnect, WebRtcRelayed. Needs SPATIALBLEND_PHOTON_CLOUD=1.
# WebRtcRelayed forces iceTransportPolicy=relay with the TURN servers of the credentials Worker, to prove the TURN relay carries the
# link. The Worker's client secret is read by the two Unity processes from SPATIALBLEND_TURN_SECRET_FILE (default
# ~/.config/spatialblend/turn-client-secret), which is passed by path: Unity logs the whole environment of a process, so the secret itself
# must not be in it. The secret is never printed, and the run's logs are checked for it afterwards.
# WebRtcTurn has the same TURN servers with iceTransportPolicy=all (what the product does); SPATIALBLEND_TP_TURN_PREFETCH=0 makes the
# credentials be fetched at room join instead of at app start. Unity runs with a scrubbed environment (tools/lib/unity-env.sh).
# SPATIALBLEND_TP_TURN_TRANSPORTS=udp|tcp|tls|all narrows the TURN transports offered. Each process runs from its
# own APFS clone of the project (one Unity per project path). Bounded: gives up after SPATIALBLEND_TP_TIMEOUT_S (default 900).
# SPATIALBLEND_TP_SCENARIO=rejoin runs the leave/rejoin scenario instead (a participant leaves a started room and joins again; its apps must
# start). SPATIALBLEND_TP_LEAVE=graceful|abrupt, SPATIALBLEND_TP_SHARE=before (late-join control), SPATIALBLEND_TP_ASSERT_CONTENT=1 (also assert content).
# SPATIALBLEND_TP_SCENARIO=avatar runs the avatar-motion scenario (Assets/SpatialBlend/Framework/Test/PhotonCloud/FusionAvatarMotionTwoProcessCloudTests.cs):
# each process is a participant in a DIFFERENT room layout (host A, joiner B), drives its own avatar through a scripted walk/reach/gaze
# motion and logs the other's avatar. Afterwards tools/avatar-multiuser/analyze_avatar_log.py measures both directions from the logs.
# SPATIALBLEND_TP_GRAPHICS=1 runs the Unity processes with graphics (no -nographics, still no window) and captures a PNG sequence of each
# observer's room into SPATIALBLEND_TP_OUT (default ${TMPDIR:-/tmp}/spatialblend-avatar-multiuser/RUN); ffmpeg, when installed, makes an mp4 of each.
if [[ "${SPATIALBLEND_PHOTON_CLOUD:-}" != 1 ]]; then echo "SKIPPED: set SPATIALBLEND_PHOTON_CLOUD=1." >&2; exit 0; fi
mode=${1:?mode: WebRtc | Photon | WebRtcThatCannotConnect | WebRtcTurn | WebRtcRelayed}
turn_secret=
if [[ "$mode" == WebRtcRelayed || "$mode" == WebRtcTurn ]]; then
    secret_file=${SPATIALBLEND_TURN_SECRET_FILE:-$HOME/.config/spatialblend/turn-client-secret}
    [[ -s "$secret_file" ]] || { echo "$mode needs the TURN client secret file: $secret_file" >&2; exit 2; }
    turn_secret=$(<"$secret_file")
    export SPATIALBLEND_TURN_CLIENT_SECRET_FILE=$secret_file
fi
# A process that loads the freeform scene in batchmode has no headset and would join as an observer.
export SPATIALBLEND_FORCE_PARTICIPANT=1
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
work=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-two-process.XXXXXX")
room="spatialblend-2p-$(date +%s)-$RANDOM"
for role in host joiner; do
    mkdir -p "$work/$role"
    for d in Assets Packages ProjectSettings Library; do cp -cR "$repo_root/$d" "$work/$role/"; done
done
run_role() {
    local rc=0
    local gfx=-nographics
    if [[ "${SPATIALBLEND_TP_GRAPHICS:-}" == 1 ]]; then gfx=; fi
    SPATIALBLEND_TP_ROLE=$1 SPATIALBLEND_TP_ROOM=$room SPATIALBLEND_TP_MODE=$mode SPATIALBLEND_TP_DIR=$work \
    unity_clean_env "$unity_bin" -batchmode $gfx -participant -projectPath "$work/$1" -runTests -testPlatform PlayMode \
        -testCategory TwoProcessCloud -testResults "$work/$1.xml" -logFile "$work/$1.log" </dev/null >/dev/null 2>&1 || rc=$?
    # A failing role still writes its rc (set -e would end the function before it); otherwise the wait below runs to the timeout.
    echo "$rc" > "$work/$1.rc"
}
# Each role is a background job of its own process group (set -m): the group is the role and the Unity it started. A timeout, an
# interrupt or a signal ends exactly those two groups, never another run's processes (no kill by name, see lib/scoped-kill.sh).
source "$(dirname "${BASH_SOURCE[0]}")/lib/scoped-kill.sh"
set -m
run_role host & host_pid=$!
run_role joiner & joiner_pid=$!
set +m
trap 'kill_process_groups "$host_pid" "$joiner_pid"; exit 143' INT TERM
deadline=$((SECONDS + ${SPATIALBLEND_TP_TIMEOUT_S:-900}))
while [[ ! -s "$work/host.rc" || ! -s "$work/joiner.rc" ]]; do
    if (( SECONDS > deadline )); then echo "TIMEOUT; see $work" >&2; kill_process_groups "$host_pid" "$joiner_pid"; exit 3; fi
    sleep 5
done
trap - INT TERM
echo "TWOPROC_DIR=$work host_rc=$(cat "$work/host.rc") joiner_rc=$(cat "$work/joiner.rc")"
if [[ -n "$turn_secret" ]]; then
    if grep -rqF -- "$turn_secret" "$work"/*.log "$work"/*.xml 2>/dev/null; then echo "TURN client secret FOUND in the run's logs" >&2; secret_leaked=1; else echo "TURN client secret in logs: no"; secret_leaked=0; fi
fi
status=0
"${PYTHON_BIN:-python3}" - "$work" <<'PY' || status=$?
import sys, xml.etree.ElementTree as ET
ok = True
for role in ("host", "joiner"):
    for c in ET.parse(f"{sys.argv[1]}/{role}.xml").getroot().findall(".//test-case"):
        print(f"{role}: {c.get('name')} {c.get('result')}")
        if c.get("result") not in ("Passed", "Skipped") and not str(c.get("result")).startswith("Skipped"): ok = False
        m = c.find("failure/message")
        if m is not None: print("  ", (m.text or "")[:600])
        o = c.find("output")
        for line in ((o.text if o is not None else "") or "").splitlines():
            if line.startswith("TWOPROC"): print(line)
sys.exit(0 if ok else 1)
PY
if [[ "${secret_leaked:-0}" == 1 ]]; then exit 4; fi
if [[ "${SPATIALBLEND_TP_SCENARIO:-}" == avatar ]]; then
    out=${SPATIALBLEND_TP_OUT:-${TMPDIR:-/tmp}/spatialblend-avatar-multiuser/$(date +%Y%m%dT%H%M%S)}
    mkdir -p "$out"
    cp "$work"/avatar-*.log "$work"/host.log "$work"/joiner.log "$out"/ 2>/dev/null || true
    for r in host joiner; do [[ -d "$work/frames-$r" ]] && cp -R "$work/frames-$r" "$out/" || true; done
    echo "AVATAR_RUN_OUT=$out"
    # host is layout A and acts in A; the joiner observes it (device log pairing: the actor's local avatar with the observer's remote).
    "${PYTHON_BIN:-python3}" -I "$repo_root/tools/avatar-multiuser/analyze_avatar_log.py" --device "$out/avatar-host.log" "$out/avatar-joiner.log" > "$out/analysis-A-seen-in-B.txt" || status=$?
    "${PYTHON_BIN:-python3}" -I "$repo_root/tools/avatar-multiuser/analyze_avatar_log.py" --device "$out/avatar-joiner.log" "$out/avatar-host.log" > "$out/analysis-B-seen-in-A.txt" || status=$?
    echo "--- A acts, B observes"; cat "$out/analysis-A-seen-in-B.txt"; echo "--- B acts, A observes"; cat "$out/analysis-B-seen-in-A.txt"
    if command -v ffmpeg >/dev/null; then
        for r in host joiner; do
            [[ -d "$out/frames-$r" ]] && ffmpeg -y -loglevel error -framerate 4 -i "$out/frames-$r/f%05d.png" -pix_fmt yuv420p "$out/observer-$r.mp4" || true
        done
    fi
fi
exit $status
