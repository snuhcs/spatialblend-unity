#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# macOS-only, real full-project integration gate. The original project, plugins,
# settings and SDK pins are read-only inputs. No Photon runner or XR device starts.
# Provision a reproducible local dylib first with package-freeform-native-dylib.sh.
if [[ $# -lt 2 || $# -gt 5 ]] || [[ "$1" != --native-library || "$2" != /* || ! -f "$2" || "$(basename "$2")" != libspatialblend.dylib ]]; then
    echo "Usage: $0 --native-library /absolute/path/libspatialblend.dylib [--graphics] [--receiver-body-sdk] [--source-head]" >&2
    exit 2
fi
native_library=$2
graphics=false
receiver_body_sdk=false
source_head=false
shift 2
for option in "$@"; do
    case "$option" in
        --graphics)
            if [[ "$graphics" == true ]]; then echo "Duplicate --graphics." >&2; exit 2; fi
            graphics=true ;;
        --receiver-body-sdk)
            if [[ "$receiver_body_sdk" == true ]]; then echo "Duplicate --receiver-body-sdk." >&2; exit 2; fi
            receiver_body_sdk=true ;;
        --source-head)
            # The source head anchor against the real core: needs a core that reports a person's body top.
            if [[ "$source_head" == true ]]; then echo "Duplicate --source-head." >&2; exit 2; fi
            source_head=true ;;
        *) echo "Unknown native gate option: $option" >&2; exit 2 ;;
    esac
done
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
python_bin=${PYTHON_BIN:-/usr/bin/python3}
network_sandbox=/usr/bin/sandbox-exec
if [[ "$(uname -s)" != Darwin || ! -x "$unity_bin" || ! -x "$python_bin" || ! -x "$network_sandbox" ]]; then
    echo "This gate requires macOS, the pinned Unity editor and a working Python XML parser." >&2
    exit 2
fi
"$python_bin" -c 'import xml.etree.ElementTree as ET; ET.fromstring("<probe/>")'
project_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-freeform-native.XXXXXX")
echo "FREEFORM_NATIVE_PROJECT=$project_dir"

# APFS clone copies preserve all authored assets, GUIDs and SDK files without
# altering originals or sharing writable Library caches with the live project.
for directory in Assets Packages ProjectSettings; do
    cp -cR "$repo_root/$directory" "$project_dir/"
done
# Preserve the complete resolved package cache in the isolated project. The
# verifier below rejects a missing or substituted lock artifact before Unity or
# UPM is ever launched; this clone has no network behavior.
if [[ ! -d "$repo_root/Library/PackageCache" ]]; then
    echo "Offline package preflight requires the repository Library/PackageCache." >&2
    exit 1
fi
mkdir -p "$project_dir/Library"
cp -cR "$repo_root/Library/PackageCache" "$project_dir/Library/"
# Repository plugins can be read-only build artifacts. Only the validated
# temporary copy is made writable; never chmod or replace the source plugin.
chmod u+w "$project_dir/Assets/Plugins/OSX/libspatialblend.dylib"
cp "$native_library" "$project_dir/Assets/Plugins/OSX/libspatialblend.dylib"
cmp "$native_library" "$project_dir/Assets/Plugins/OSX/libspatialblend.dylib"
for fixture in NativeManagerIntegrationTests.cs NativeEntitySharePeerTests.cs NativeFreeformSceneGraphicsTests.cs NativeAvatarSemanticTransferTests.cs NativeSeatLinkTests.cs; do
    cp "$repo_root/tools/freeform-native-verification/$fixture" \
        "$project_dir/Assets/SpatialBlend/Framework/Test/Restoration/$fixture"
done

# Verify the copied manifest/lock graph, each local PackageCache artifact, and
# editor-builtins before Unity can start UPM. It writes a package provenance
# manifest whose hashes are included in the overall input provenance below.
"$python_bin" "$repo_root/tools/freeform-native-verification/verify-offline-package-closure.py" \
    "$repo_root" "$project_dir" "$unity_bin"

# Freeze managed source/test bytes + metadata, original Freeform scene/content
# prefab bytes, ProjectSettings, native/importer inputs, Photon assemblies and
# the package graph before import. The remaining SDK/resource payload is not
# claimed by this scoped provenance manifest.
"$python_bin" "$repo_root/tools/freeform-native-verification/write-provenance.py" \
    "$repo_root" "$project_dir" "$native_library"

# Unity 6000.0.56f1 segfaults in UnwrapImpl::GenerateSecondaryUVSetInternal
# during the initial asset refresh, before EditMode. -nographics does not skip
# that importer. UnwrapCL talks to the editor on 127.0.0.1; this gate denies
# all IP egress, the helper never connects, and the in-process unwrapper
# crashes. MRDesk.fbx was only the first sample with generateSecondaryUV
# enabled. The next one, mugMesh.fbx, crashed the same way once the desk flag
# was off. None of these Meta Interaction samples are instanced by the
# Freeform scene, and none need generated lightmap UVs. The closure digest
# above already recorded the vendor bytes; only the isolated copy is changed.
"$python_bin" - "$project_dir" <<'PY'
import sys
from pathlib import Path

project = Path(sys.argv[1])
needle = "    generateSecondaryUV: 1\n"
replacement = "    generateSecondaryUV: 0\n"
patched = []
for root_name in ("Assets", "Library/PackageCache"):
    root = project / root_name
    if not root.is_dir():
        continue
    for meta in sorted(root.rglob("*.meta")):
        text = meta.read_text()
        count = text.count(needle)
        if count == 0:
            continue
        if count != 1:
            raise SystemExit(f"Ambiguous generateSecondaryUV flag in {meta}")
        meta.write_text(text.replace(needle, replacement, 1))
        patched.append(meta)
if not any(path.name == "MRDesk.fbx.meta" for path in patched):
    raise SystemExit("MRDesk.fbx.meta was not among the models requesting secondary UVs")
for path in patched:
    print(f"FREEFORM_NATIVE_DISABLED_SECONDARY_UV={path}")
PY

unity_args=(-batchmode -projectPath "$project_dir" -runTests -testPlatform EditMode
    -testResults "$project_dir/results.xml" -logFile "$project_dir/editor.log")
verify_args=("$project_dir/results.xml")
categories=NativeManagerIntegration
if [[ "$receiver_body_sdk" == true ]]; then
    categories+=';ReceiverBodySdkNative'
    verify_args+=(--receiver-body-sdk)
fi
if [[ "$source_head" == true ]]; then
    categories+=';SourceHeadNative'
    verify_args+=(--source-head)
fi
if [[ "$graphics" == true ]]; then
    categories+=';NativeFreeformGraphics'
    verify_args+=(--graphics)
else
    unity_args+=(-nographics)
fi
unity_args+=(-testCategory "$categories")
# Deny every IP egress except loopback (127.0.0.1 and ::1, any port): the
# in-process host and client of the real-manager cases talk over loopback.
# Later rules win, so the loopback allow follows the blanket deny.  A complete
# local closure has already been checked above.  UPM still needs its private
# Unix-domain IPC socket (not an IP socket, so not denied), but neither a
# package registry nor a git remote can be reached.  Any attempted acquisition
# is a failed gate, never a download.
sandbox_profile='(version 1) (allow default) (deny network-outbound (remote ip)) (allow network-outbound (remote ip "localhost:*"))'
# Before Unity starts, prove inside this same profile that loopback works and
# that external TCP v4/v6 and UDP connects are still denied.
"$network_sandbox" -p "$sandbox_profile" "$python_bin" \
    "$repo_root/tools/freeform-native-verification/probe-sandbox-network.py"
set +e
unity_clean_env "$network_sandbox" -p "$sandbox_profile" \
    "$unity_bin" "${unity_args[@]}"
unity_status=$?
set -e
if [[ ! -s "$project_dir/results.xml" ]]; then
    echo "Unity exited $unity_status without a test result; see $project_dir/editor.log" >&2
    exit 1
fi
"$python_bin" "$repo_root/tools/freeform-native-verification/verify-results.py" "${verify_args[@]}"
# Exit 2 is Unity's test-failure status. The verifier allows the sandbox
# loopback reconnect case. Any other editor status is still a failed gate.
if [[ "$unity_status" -ne 0 && "$unity_status" -ne 2 ]]; then
    echo "Unity exited $unity_status; see $project_dir/editor.log" >&2
    exit "$unity_status"
fi
if [[ "$graphics" == true && ! -s "$project_dir/freeform-scene-local-graphics-overview.png" ]]; then
    echo "Original Freeform graphics fixture did not write its overview PNG." >&2
    exit 1
fi
echo "FREEFORM_NATIVE_RESULTS=$project_dir/results.xml"
if [[ "$graphics" == true ]]; then
    echo "FREEFORM_NATIVE_GRAPHICS_PNG=$project_dir/freeform-scene-local-graphics-overview.png"
fi
