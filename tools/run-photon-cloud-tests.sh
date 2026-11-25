#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Real Photon Cloud tests (SpatialBlend.PhotonCloudTests, category PhotonCloud).
# They contact the cloud with the project's own App ID, so they are skipped
# unless SPATIALBLEND_PHOTON_CLOUD=1. The assembly has includePlatforms=[] so it is
# discovered by the PlayMode runner only (an EditMode run finds 0 tests).
if [[ "${SPATIALBLEND_PHOTON_CLOUD:-}" != 1 ]]; then
    echo "SKIPPED: set SPATIALBLEND_PHOTON_CLOUD=1 to run the real Photon Cloud tests." >&2
    exit 0
fi
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
python_bin=${PYTHON_BIN:-/usr/bin/python3}
[[ -x "$unity_bin" ]] || { echo "Missing Unity editor: $unity_bin" >&2; exit 2; }
result_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-photon-cloud.XXXXXX")
set +e
unity_clean_env "$unity_bin" -batchmode -nographics -projectPath "$repo_root" -runTests -testPlatform PlayMode \
    -testCategory PhotonCloud -testResults "$result_dir/results.xml" -logFile "$result_dir/editor.log"
unity_status=$?
set -e
# A run that discovers nothing, or ignores a test (no App ID), is not a pass. The seven tests are the two
# session-browser tests, the two failed-join tests (a room that filled up or closed since the list was read) and the no-address room link (Photon carrier, WebRTC carrier, forced-ICE-failure fallback) (FusionCoreLinkPhotonCloudTests: two Shared-mode runners join
# one room by name, host and join the core over Fusion reliable data, and share an object end to end).
"$python_bin" - "$result_dir/results.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
cases = root.findall(".//test-case")
bad = [c.get("fullname") for c in cases if c.get("result") != "Passed"]
if len(cases) != 7 or bad:
    raise SystemExit(f"PhotonCloud gate: expected 7 passing tests, got {len(cases)}; not passed: {bad}")
names = {c.get("name") for c in cases}
for required in ("JoiningAFullRoomFails_AndFindARoomWorksAgain",
                 "JoiningAClosedRoomFails_AndFindARoomWorksAgain",
                 "TwoRunnersJoinOneRoomByNameAndShareAnObjectOverTheCoreLink",
                 "TheCoreLinkComesUpOverWebRtcAndSharesAThreeMegabyteObject",
                 "WhenIceFailsTheCoreLinkFallsBackToThePhotonCarrier"):
    if required not in names:
        raise SystemExit(f"PhotonCloud gate: {required} did not run")
for c in cases:
    for out in c.findall("output"):
        for line in (out.text or "").splitlines():
            if line.startswith("PHOTON_CLOUD_LINK"):
                print(line)
print(f"PHOTON_CLOUD_PASSED={len(cases)}")
PY
[[ "$unity_status" -eq 0 || "$unity_status" -eq 2 ]] || { echo "Unity exit $unity_status; see $result_dir/editor.log" >&2; exit "$unity_status"; }
echo "PHOTON_CLOUD_RESULTS=$result_dir/results.xml"
