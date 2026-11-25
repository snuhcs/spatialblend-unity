#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# A desktop spectator against a participant host, in two Unity processes joined to one room by name over the real Photon Cloud
# (see Assets/SpatialBlend/Framework/Test/PhotonCloud/FreeformSpectatorTwoProcessCloudTests.cs). Usage: run-spectator-two-process-test.sh MODE
# with MODE one of WebRtc, Photon, WebRtcTurn. SPATIALBLEND_TP_SOURCE_ACTOR=1 also registers a source actor on the participant's object.
# Needs SPATIALBLEND_PHOTON_CLOUD=1. WebRtcTurn reads the TURN client secret from SPATIALBLEND_TURN_SECRET_FILE (default
# ~/.config/spatialblend/turn-client-secret), passed to Unity by path; the secret is never printed and the logs are checked for it.
# SPATIALBLEND_TP_JOINER=participant runs a normal Freeform participant in place of the spectator, to compare link-up times.
# With SPATIALBLEND_TP_PARTICIPANT_VISITS=2 that participant also leaves (runner shut down, scene reloaded) and joins again, and must
# receive the host's object on both visits (a participant's rejoin).
# Each process runs from its own APFS clone of the project (one Unity per project path); the clones are removed at the end.
# Bounded: gives up after SPATIALBLEND_TP_TIMEOUT_S (default 2000: above the test's own 1800 s timeout, and every phase inside it waits
# at most 600 s, so a hang fails with its phase named before the run is killed). Process ids are tracked, nothing is killed by name.
if [[ "${SPATIALBLEND_PHOTON_CLOUD:-}" != 1 ]]; then echo "SKIPPED: set SPATIALBLEND_PHOTON_CLOUD=1." >&2; exit 0; fi
# Disk: the project clones are APFS copy-on-write clones (cp -cR: near-zero space until a file is written), but a Unity run rewrites
# parts of Library, so free space is checked: a run starts only with at least 40 GB free, is aborted below 30 GB, and its clones
# are always removed afterwards, after its logs are copied out. SPATIALBLEND_TP_LOG_DIR (default ${TMPDIR:-/tmp}/spatialblend-spectator-runs).
free_gb() { df -g "${TMPDIR:-/tmp}" | awk 'NR==2 {print $4}'; }
min_free_gb=${SPATIALBLEND_TP_MIN_FREE_GB:-30}
if (( $(free_gb) < min_free_gb + 10 )); then echo "NOT STARTED: only $(free_gb) GB free, a run needs $((min_free_gb + 10))" >&2; exit 4; fi
mode=${1:?mode: WebRtc | Photon | WebRtcTurn}
turn_secret=
if [[ "$mode" == WebRtcTurn ]]; then
    secret_file=${SPATIALBLEND_TURN_SECRET_FILE:-$HOME/.config/spatialblend/turn-client-secret}
    [[ -s "$secret_file" ]] || { echo "$mode needs the TURN client secret file: $secret_file" >&2; exit 2; }
    turn_secret=$(<"$secret_file")
    export SPATIALBLEND_TURN_CLIENT_SECRET_FILE=$secret_file
fi
# Batchmode on a desktop has no headset. The host (and a participant joiner) load FreeformCollaboration and would
# otherwise join as observers. The spectator process uses the spectator scene and ignores the flag.
export SPATIALBLEND_FORCE_PARTICIPANT=1
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
work=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-spectator.XXXXXX")
room="spatialblend-spec-$(date +%s)-$RANDOM"
for role in host spectator; do
    mkdir -p "$work/$role"
    for d in Assets Packages ProjectSettings Library; do cp -cR "$repo_root/$d" "$work/$role/"; done
done
# The runner names the scene by build index, and Fusion names a scene object by its position among the scene's NetworkObjects.
# Host clone: the real Freeform scene stays build index 0 (the project's setting), with its serialized Foundation Local profile
# (which only the Editor honours) switched to the Fusion profile a player always runs. Spectator clone: the spectator scene is the
# only, first scene of the build settings. Neither edit touches the shared project.
sed -i.bak 's/^  _runtimeProfile: 1$/  _runtimeProfile: 0/' "$work/host/Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity"
rm -f "$work/host/Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity.bak"
grep -q '^  _runtimeProfile: 0$' "$work/host/Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity" || { echo "could not switch the Freeform scene to the Fusion profile" >&2; exit 2; }
if [[ "${SPATIALBLEND_TP_JOINER:-}" == participant ]]; then
    # A normal participant instead of the spectator, for comparison of link-up times: the same Freeform scene as the host.
    sed -i.bak 's/^  _runtimeProfile: 1$/  _runtimeProfile: 0/' "$work/spectator/Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity"
    rm -f "$work/spectator/Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity.bak"
else
scene_guid=$(sed -n 's/^guid: //p' "$repo_root/Assets/SpatialBlend/Samples/Scenes/FreeformSpectator.unity.meta")
"${PYTHON_BIN:-python3}" - "$work/spectator/ProjectSettings/EditorBuildSettings.asset" "$scene_guid" <<'PY'
import re, sys
path, guid = sys.argv[1], sys.argv[2]
text = open(path).read()
scenes = "  m_Scenes:\n  - enabled: 1\n    path: Assets/SpatialBlend/Samples/Scenes/FreeformSpectator.unity\n    guid: " + guid + "\n"
text = re.sub(r"  m_Scenes:\n(?:  - .*\n|    .*\n)*", lambda m: scenes, text, count=1)
open(path, "w").write(text)
PY
fi
run_role() {
    local rc=0
    SPATIALBLEND_TP_ROLE=$1 SPATIALBLEND_TP_ROOM=$room SPATIALBLEND_TP_MODE=$mode SPATIALBLEND_TP_DIR=$work \
    unity_clean_env "$unity_bin" -batchmode -nographics -participant -projectPath "$work/$1" -runTests -testPlatform PlayMode \
        -testCategory TwoProcessSpectator -testResults "$work/$1.xml" -logFile "$work/$1.log" </dev/null >/dev/null 2>&1 || rc=$?
    echo $rc > "$work/$1.rc"
}
# Whatever the outcome, the logs go to the log dir and the clones are removed (SPATIALBLEND_TP_KEEP keeps them).
log_dir=${SPATIALBLEND_TP_LOG_DIR:-${TMPDIR:-/tmp}/spatialblend-spectator-runs}/$(basename "$work")
cleanup() {
    local rc=$?
    mkdir -p "$log_dir"
    for r in host spectator; do
        cp "$work/$r.xml" "$work/$r.rc" "$log_dir/" 2>/dev/null || true
        grep -a "TWOPROC" "$work/$r.log" > "$log_dir/$r.twoproc.txt" 2>/dev/null || true
        tail -n 200 "$work/$r.log" > "$log_dir/$r.log.tail.txt" 2>/dev/null || true
    done
    if [[ -z "${SPATIALBLEND_TP_KEEP:-}" ]]; then rm -rf "$work"; else echo "work dir kept: $work" >&2; fi
    echo "logs: $log_dir (exit $rc, free ${min_seen:-?} GB at the lowest)" >&2
}
trap cleanup EXIT
# Each role is a background job of its own process group (set -m): the group is the role and the Unity it started. A timeout, an
# abort, an interrupt or a signal ends exactly those two groups, never another run's processes (no kill by name: lib/scoped-kill.sh).
source "$(dirname "${BASH_SOURCE[0]}")/lib/scoped-kill.sh"
set -m
run_role host & host_pid=$!
run_role spectator & spectator_pid=$!
set +m
stop_all() { kill_process_groups "$host_pid" "$spectator_pid"; }
trap 'stop_all; exit 143' INT TERM
deadline=$((SECONDS + ${SPATIALBLEND_TP_TIMEOUT_S:-2000}))
min_seen=$(free_gb)
while [[ ! -s "$work/host.rc" || ! -s "$work/spectator.rc" ]]; do
    now_free=$(free_gb); (( now_free < min_seen )) && min_seen=$now_free
    if (( now_free < min_free_gb )); then echo "ABORTED: free space fell to $now_free GB (floor $min_free_gb)" >&2; stop_all; exit 5; fi
    if (( SECONDS > deadline )); then echo "TIMEOUT; see $work" >&2; stop_all; exit 3; fi
    sleep 5
done
trap - INT TERM
echo "TWOPROC_DIR=$work host_rc=$(cat "$work/host.rc") spectator_rc=$(cat "$work/spectator.rc")"
secret_leaked=0
if [[ -n "$turn_secret" ]]; then
    if grep -rqF -- "$turn_secret" "$work"/*.log "$work"/*.xml 2>/dev/null; then echo "TURN client secret FOUND in the run's logs" >&2; secret_leaked=1; else echo "TURN client secret in logs: no"; fi
fi
status=0
"${PYTHON_BIN:-python3}" - "$work" <<'PY' || status=$?
import sys, xml.etree.ElementTree as ET
ok = True
for role in ("host", "spectator"):
    for c in ET.parse(f"{sys.argv[1]}/{role}.xml").getroot().findall(".//test-case"):
        print(f"{role}: {c.get('name')} {c.get('result')}")
        if c.get("result") not in ("Passed", "Skipped") and not str(c.get("result")).startswith("Skipped"): ok = False
        m = c.find("failure/message")
        if m is not None: print("  ", (m.text or "")[:800])
        o = c.find("output")
        for line in ((o.text if o is not None else "") or "").splitlines():
            if line.startswith("TWOPROC"): print(line)
sys.exit(0 if ok else 1)
PY
if [[ "$secret_leaked" == 1 ]]; then exit 4; fi
exit $status
