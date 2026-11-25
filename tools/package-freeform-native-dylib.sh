#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Build the exact v2 Core package with Bazel downloads disabled, then install
# only its macOS dylib at an explicit path for run-freeform-native-tests.sh.
if [[ $# -ne 4 || "$1" != --core-root || "$2" != /* || "$3" != --install-to || "$4" != /* || "$(basename "$4")" != libspatialblend.dylib ]]; then
    echo "Usage: $0 --core-root /absolute/path/to/spatialblend-core --install-to /absolute/path/libspatialblend.dylib" >&2
    exit 2
fi
core_root=$2
install_to=$4
if [[ "$(uname -s)" != Darwin || ! -d "$core_root" || ! -f "$core_root/MODULE.bazel" ]]; then
    echo "This packaging step requires a local macOS spatialblend-core checkout." >&2
    exit 2
fi
for required in bazel tar shasum; do
    command -v "$required" >/dev/null || { echo "Missing required local tool: $required" >&2; exit 2; }
done

# repository_disable_download makes a missing Bazel external repository fail
# locally instead of making this native gate provision anything from a network.
(cd "$core_root" && bazel build --repository_disable_download -c opt //spatialblend/c:libspatialblend_c)
package_tar="$core_root/bazel-bin/spatialblend/c/libspatialblend_c.tar"
if [[ ! -f "$package_tar" ]]; then
    echo "Core build completed without its declared C package: $package_tar" >&2
    exit 1
fi
stage_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-v2-package.XXXXXX")
trap 'rm -rf "$stage_dir"' EXIT
tar -xf "$package_tar" -C "$stage_dir"
built_dylib="$stage_dir/lib/libspatialblend.dylib"
if [[ ! -f "$built_dylib" ]]; then
    echo "Core package lacks lib/libspatialblend.dylib" >&2
    exit 1
fi
mkdir -p "$(dirname "$install_to")"
cp "$built_dylib" "$install_to"
cmp -s "$built_dylib" "$install_to" || { echo "Installed dylib differs from Core package" >&2; exit 1; }

artifact_sha256=$(shasum -a 256 "$install_to" | awk '{print $1}')
package_sha256=$(shasum -a 256 "$package_tar" | awk '{print $1}')
core_revision=$(git -C "$core_root" rev-parse HEAD)
provenance="$install_to.provenance.json"
printf '{\n  "artifact_sha256": "%s",\n  "core_revision": "%s",\n  "core_root": "%s",\n  "package_sha256": "%s",\n  "target": "//spatialblend/c:libspatialblend_c"\n}\n' \
    "$artifact_sha256" "$core_revision" "$core_root" "$package_sha256" > "$provenance"
echo "FREEFORM_NATIVE_LIBRARY=$install_to"
echo "FREEFORM_NATIVE_LIBRARY_SHA256=$artifact_sha256"
echo "FREEFORM_NATIVE_CORE_PROVENANCE=$provenance"
