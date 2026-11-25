#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Opens the real project in EditMode, never PlayMode or an XR device.
# Bootstrap pinned official dependencies first; failures are not skipped.
if [[ $# -ne 0 ]]; then
    echo "Usage: $0 (optionally set UNITY_BIN and PYTHON_BIN)" >&2
    exit 2
fi
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
if [[ ! -x "$unity_bin" ]]; then
    echo "Missing executable Unity editor: $unity_bin" >&2
    exit 2
fi
select_python() {
    local candidate
    local candidates=()
    if [[ -n "${PYTHON_BIN:-}" ]]; then
        candidates=("$PYTHON_BIN")
    else
        candidates=("$(command -v python3 || true)" "/usr/bin/python3")
    fi
    for candidate in "${candidates[@]}"; do
        if [[ "$candidate" != */* ]]; then
            candidate=$(command -v "$candidate" 2>/dev/null || true)
        fi
        [[ -n "$candidate" && -x "$candidate" ]] || continue
        if "$candidate" -c 'import xml.etree.ElementTree as ET; ET.fromstring("<probe/>")' >/dev/null 2>&1; then
            echo "$candidate"
            return 0
        fi
    done
    if [[ -n "${PYTHON_BIN:-}" ]]; then
        echo "PYTHON_BIN cannot import and parse xml.etree.ElementTree: $PYTHON_BIN" >&2
    else
        echo "No usable Python XML parser; tried python3 and /usr/bin/python3" >&2
    fi
    return 1
}
python_bin=$(select_python) || exit 2
echo "FREEFORM_PYTHON=$python_bin"
result_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-freeform-components.XXXXXX")
xml_path="$result_dir/results.xml"
log_path="$result_dir/editor.log"
echo "FREEFORM_RESULTS_DIR=$result_dir"

set +e
unity_clean_env "$unity_bin" -batchmode -nographics -projectPath "$repo_root" -runTests \
    -testPlatform EditMode -testCategory FreeformRestoration \
    -testResults "$xml_path" -logFile "$log_path"
unity_status=$?
set -e

# Parse actual test output; a successful editor exit without discovery is not
# a pass. Require prefab, scene, input/arrival, lifecycle/callback, connection,
# UI and scene-geometry suites, including all component-first service gates.
"$python_bin" "$repo_root/tools/verify-freeform-results.py" "$xml_path"
# Exit 2 is Unity's "one or more tests failed". The verifier already accepted
# the artifact, including Explicit parity rows the category filter executes.
# Any other editor status is still a failed gate.
if [[ "$unity_status" -ne 0 && "$unity_status" -ne 2 ]]; then
    echo "Unity returned $unity_status despite passing XML; see $log_path" >&2
    exit "$unity_status"
fi
echo "FREEFORM_RESULTS=$xml_path"
echo "FREEFORM_LOG=$log_path"
