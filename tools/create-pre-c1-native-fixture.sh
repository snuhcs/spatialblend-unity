#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Creates a macOS ABI-compatibility fixture from a real Core dylib.  It keeps
# every legacy export byte-for-byte in the selected input while removing only
# the additive C1 symbols from its dynamic export table.  The result is useful
# for proving that a Unity client preserves baseline functionality when a
# deployed native library predates C1.
if [[ $# -ne 4 || "$1" != --native-library || "$2" != /* || "$3" != --output ||
      "$4" != /* || "$(basename "$2")" != libspatialblend.dylib ||
      "$(basename "$4")" != libspatialblend.dylib || ! -f "$2" ]]; then
    echo "Usage: $0 --native-library /absolute/path/libspatialblend.dylib --output /absolute/path/libspatialblend.dylib" >&2
    exit 2
fi
if [[ "$(uname -s)" != Darwin ]]; then
    echo "This fixture uses macOS Mach-O nmedit and must run on Darwin." >&2
    exit 2
fi
for required in cp dirname mktemp nmedit nm shasum codesign; do
    command -v "$required" >/dev/null || { echo "Missing required tool: $required" >&2; exit 2; }
done

input=$2
output=$4
symbols_file=$(mktemp "${TMPDIR:-/tmp}/spatialblend-pre-c1-symbols.XXXXXX")
trap 'rm -f "$symbols_file"' EXIT

cat > "$symbols_file" <<'EOF'
_sb_get_observed_collaborator_feature
_sb_get_observed_collaborator_field_count
_sb_get_observed_collaborator_field_offset
_sb_begin_observed_source_anchor_point
_sb_retire_observed_source_anchor_point
_sb_apply_observed_collaborator_batch
_sb_get_observed_source_setup_feature
_sb_get_observed_source_setup_field_count
_sb_get_observed_source_setup_field_offset
_sb_begin_observed_source_setup
_sb_retire_observed_source_setup
_sb_release_observed_source_setup
EOF

for symbol in $(cat "$symbols_file"); do
    nm -gU "$input" | awk '{print $3}' | grep -Fx "$symbol" >/dev/null || {
        echo "Input does not export expected C1 symbol: $symbol" >&2
        exit 1
    }
done
for symbol in _sb_create_session _sb_add_space _sb_destroy_session; do
    nm -gU "$input" | awk '{print $3}' | grep -Fx "$symbol" >/dev/null || {
        echo "Input lacks required legacy symbol: $symbol" >&2
        exit 1
    }
done

mkdir -p "$(dirname "$output")"
cp "$input" "$output"
nmedit -R "$symbols_file" "$output"
codesign --verify --strict "$output"
for symbol in $(cat "$symbols_file"); do
    if nm -gU "$output" | awk '{print $3}' | grep -Fx "$symbol" >/dev/null; then
        echo "Fixture still exports C1 symbol: $symbol" >&2
        exit 1
    fi
done
for symbol in _sb_create_session _sb_add_space _sb_destroy_session; do
    nm -gU "$output" | awk '{print $3}' | grep -Fx "$symbol" >/dev/null || {
        echo "Fixture lost required legacy symbol: $symbol" >&2
        exit 1
    }
done

input_sha256=$(shasum -a 256 "$input" | awk '{print $1}')
output_sha256=$(shasum -a 256 "$output" | awk '{print $1}')
provenance="$output.pre-c1-fixture.json"
printf '{\n  "fixture": "macos-pre-c1-symbol-stripped",\n  "input_sha256": "%s",\n  "output_sha256": "%s",\n  "removed_symbol_count": 12\n}\n' \
    "$input_sha256" "$output_sha256" > "$provenance"
echo "PRE_C1_NATIVE_FIXTURE=$output"
echo "PRE_C1_NATIVE_FIXTURE_SHA256=$output_sha256"
echo "PRE_C1_NATIVE_FIXTURE_PROVENANCE=$provenance"
