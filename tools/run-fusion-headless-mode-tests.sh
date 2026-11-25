#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Pins that no Fusion mode runs without a Photon server. PlayMode, batchmode,
# and a sandbox that denies every non-loopback IP egress, so the cloud cannot be
# contacted even by mistake.
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
python_bin=${PYTHON_BIN:-/usr/bin/python3}
[[ -x "$unity_bin" ]] || { echo "Missing Unity editor: $unity_bin" >&2; exit 2; }
result_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-fusion-headless.XXXXXX")
profile='(version 1) (allow default) (deny network-outbound (remote ip)) (allow network-outbound (remote ip "localhost:*"))'
set +e
unity_clean_env /usr/bin/sandbox-exec -p "$profile" "$unity_bin" -batchmode -nographics -projectPath "$repo_root" -runTests \
    -testPlatform PlayMode -testCategory FusionHeadlessModes \
    -testResults "$result_dir/results.xml" -logFile "$result_dir/editor.log"
unity_status=$?
set -e
"$python_bin" - "$result_dir/results.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
cases = ET.parse(sys.argv[1]).getroot().findall(".//test-case")
bad = [c.get("fullname") for c in cases if c.get("result") != "Passed"]
if len(cases) != 3 or bad:
    raise SystemExit(f"expected 3 passing tests, got {len(cases)}; not passed: {bad}")
print(f"FUSION_HEADLESS_PASSED={len(cases)}")
PY
[[ "$unity_status" -eq 0 || "$unity_status" -eq 2 ]] || { echo "Unity exit $unity_status; see $result_dir/editor.log" >&2; exit "$unity_status"; }
echo "FUSION_HEADLESS_RESULTS=$result_dir/results.xml"
