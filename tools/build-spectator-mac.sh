#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Builds the desktop spectator (macOS, Apple silicon, Mono) from a disposable APFS clone of this project, so the shared
# project's Library and active build target are never switched. Usage: build-spectator-mac.sh [--no-turn-secret] [OUT.app]
# (default Builds/Spectator/SpatialBlendSpectator.app in this checkout, git-ignored). OUT must end in .app. Never run git inside the clone; the clone is removed at the end.
# The clone drops com.unity.xr.meta-openxr from its manifest: that package is Android-only and the Meta XR Core's OpenXR depth
# provider does not compile for the standalone target when it is present. The shared project's manifest is not touched.
# The TURN client secret, when ~/.config/spatialblend/turn-client-secret exists, is embedded in the build as in the Quest app:
# do not publish the app. --no-turn-secret leaves it out (the app then has no TURN and no secret inside it).
# RequiredCredentialsBuildGuard refuses the build when the Photon Fusion App ID is missing, or the TURN client secret is missing and
# --no-turn-secret was not given (docs/required-credentials.md). SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID=1 / SPATIALBLEND_ALLOW_NO_TURN=1 waive one for a
# compile-only check. The spectator has no voice, so the Voice App ID is not needed.
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
unity_args=()
if [[ "${1:-}" == --no-turn-secret ]]; then unity_args+=(-spectatorNoTurnSecret); shift; fi
out=${1:-$repo_root/Builds/Spectator/SpatialBlendSpectator.app}
[[ $out == *.app ]] || { echo "OUT must be a path ending in .app: $out" >&2; exit 2; }
python_bin=${PYTHON_BIN:-/usr/bin/python3}
# Disk: the clone is an APFS copy-on-write clone (near-zero until Unity writes), but the build writes a lot to Library. It starts
# only with at least 40 GB free, and the clone is always removed; on failure build.log is copied to Builds/Spectator/runs.
free_gb=$(df -g "${TMPDIR:-/tmp}" | awk 'NR==2 {print $4}')
(( free_gb >= 40 )) || { echo "NOT STARTED: only $free_gb GB free, a build needs 40" >&2; exit 4; }
work=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-spectator-build.XXXXXX")
trap 'if [[ -f "$work/build.log" && ${status:-1} != 0 ]]; then mkdir -p "$repo_root/Builds/Spectator/runs"; cp "$work/build.log" "$repo_root/Builds/Spectator/runs/$(basename "$work").build.log"; echo "build.log kept in $repo_root/Builds/Spectator/runs" >&2; fi; rm -rf "$work"' EXIT
for d in Assets Packages ProjectSettings Library; do cp -cR "$repo_root/$d" "$work/"; done
"$python_bin" - "$work/Packages" <<'PY'
import json, sys
d = sys.argv[1]
for name, key in (("manifest.json", None), ("packages-lock.json", None)):
    p = f"{d}/{name}"
    data = json.load(open(p))
    data["dependencies"].pop("com.unity.xr.meta-openxr", None)
    json.dump(data, open(p, "w"), indent=2)
PY
mkdir -p "$(dirname "$out")"
rm -rf "$out"
set +e
unity_clean_env "$unity_bin" -batchmode -nographics -quit -projectPath "$work" -buildTarget StandaloneOSX \
    -executeMethod SpatialBlend.Editor.SpectatorBuild.BuildMac -spectatorOut "$out" ${unity_args[@]+"${unity_args[@]}"} -logFile "$work/build.log"
status=$?
set -e
grep -n "\[SpectatorBuild\]" "$work/build.log" || true
# A refused build says why in the log (every missing credential and how to supply each); show it here, without any value.
grep -A14 "\[RequiredCredentialsBuildGuard\] This " "$work/build.log" | head -40 || true
echo "SPECTATOR_BUILD_RC=$status out=$out"
if [[ $status != 0 ]]; then echo "the build failed" >&2; fi
exit $status
