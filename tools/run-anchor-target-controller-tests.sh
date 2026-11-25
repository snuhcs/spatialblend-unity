#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Runs real, byte-for-byte copied sources in a fresh Unity project. The default
# verifies only AnchorTargetController. --include-abi also verifies authored ABI
# layout, conversion, and pipeline tests. It never opens this repository's
# project. --native-library explicitly enables the real native ABI/removal gate
# in that temporary project only; original plugin assets are never replaced.

include_abi=false
native_library=""
if [[ "${1:-}" == "--include-abi" ]]; then
    include_abi=true
    shift
fi
if [[ "${1:-}" == "--native-library" && $# -ge 2 ]]; then
    include_abi=true
    native_library=$2
    shift 2
fi
if [[ $# -ne 0 ]]; then
    echo "Usage: $0 [--include-abi] [--native-library /absolute/path/libspatialblend.dylib]" >&2
    exit 2
fi
if [[ -n "$native_library" && ( "$native_library" != /* || ! -f "$native_library" || "$(basename "$native_library")" != libspatialblend.dylib ) ]]; then
    echo "Native verification requires an existing absolute libspatialblend.dylib path" >&2
    exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
utf_dir=$(find "$repo_root/Library/PackageCache" -maxdepth 1 -type d -name 'com.unity.test-framework@*' -print -quit)
nunit_dir=$(find "$repo_root/Library/PackageCache" -maxdepth 1 -type d -name 'com.unity.ext.nunit@*' -print -quit)
controller_source="$repo_root/Assets/SpatialBlend/Framework/Scripts/Motion/AnchorTargetController.cs"
controller_test_source="$repo_root/Assets/SpatialBlend/Framework/Test/AnchorTargetControllerTests.cs"
abi_test_source="$repo_root/Assets/SpatialBlend/Framework/Test/SpatialBlendAbiTests.cs"

for required in "$unity_bin" "$utf_dir" "$nunit_dir" "$controller_source" "$controller_test_source"; do
    if [[ -z "$required" || ! -e "$required" ]]; then
        echo "Missing required local verification input: ${required:-unset}" >&2
        exit 2
    fi
done
if "$include_abi" && [[ ! -f "$abi_test_source" ]]; then
    echo "Missing required ABI test source: $abi_test_source" >&2
    exit 2
fi

project_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-anchor-target-controller.XXXXXX")
mkdir -p "$project_dir/Assets/Verification/Tests" "$project_dir/Packages" "$project_dir/ProjectSettings" "$project_dir/TestResults"
sed -e "s|@UTF_DIR@|$utf_dir|g" -e "s|@NUNIT_DIR@|$nunit_dir|g" \
    "$repo_root/tools/anchor-target-controller-verification/manifest.json.in" > "$project_dir/Packages/manifest.json"
cp "$repo_root/tools/anchor-target-controller-verification/ProjectVersion.txt" "$project_dir/ProjectSettings/ProjectVersion.txt"
cp "$repo_root/tools/anchor-target-controller-verification/AnchorTargetVerification.asmdef" "$project_dir/Assets/Verification/AnchorTargetVerification.asmdef"
cp "$repo_root/tools/anchor-target-controller-verification/AnchorTargetVerification.Tests.asmdef" "$project_dir/Assets/Verification/Tests/AnchorTargetVerification.Tests.asmdef"

provenance_sources=()
copy_source() {
    local source=$1 destination=$2 relative
    relative=${source#"$repo_root/"}
    mkdir -p "$(dirname "$destination")"
    cp "$source" "$destination"
    if ! cmp -s "$source" "$destination"; then
        echo "Verification project did not preserve source bytes: $relative" >&2
        exit 2
    fi
    provenance_sources+=("$source")
}

copy_source "$controller_source" "$project_dir/Assets/Verification/AnchorTargetController.cs"
copy_source "$controller_test_source" "$project_dir/Assets/Verification/Tests/AnchorTargetControllerTests.cs"
if "$include_abi"; then
    abi_sources=(
        "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.cs"
        "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.SceneInputs.cs"
        "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.Supports.cs"
        "Assets/SpatialBlend/Foundation/Internal/SpatialBlendAPI.Actors.cs"
        "Assets/SpatialBlend/Foundation/Internal/CoordinateConverter.cs"
        "Assets/SpatialBlend/Foundation/Internal/SpatialBlendLibraryConfig.cs"
        "Assets/SpatialBlend/Framework/Internal/UnityMainThreadDispatcher.cs"
        "Assets/SpatialBlend/Framework/Scripts/SpatialBlendSettings.cs"
        "Assets/SpatialBlend/Framework/Scripts/SpatialBlendDebug.cs"
    )
    for source in "${abi_sources[@]}"; do
        copy_source "$repo_root/$source" "$project_dir/Assets/Verification/Abi/Source/$(basename "$source")"
    done
    while IFS= read -r source; do
        # Manager-bound descriptors belong to the full-project native gate,
        # not the intentionally minimal ABI/controller assembly.
        case "$source" in
            */OnShareResponse.cs|*/SpatialHierarchyDescriptor.cs|*/AnchorPoseFrame.cs) continue ;;
        esac
        copy_source "$source" "$project_dir/Assets/Verification/Abi/Source/$(basename "$source")"
    done < <(find "$repo_root/Assets/SpatialBlend/Framework/Types" -maxdepth 1 -type f -name '*.cs' | sort)
    cp "$repo_root/tools/anchor-target-controller-verification/AnchorTargetVerification.AbiTests.asmdef" "$project_dir/Assets/Verification/Abi/AnchorTargetVerification.AbiTests.asmdef"
    copy_source "$abi_test_source" "$project_dir/Assets/Verification/Abi/SpatialBlendAbiTests.cs"
    if [[ -n "$native_library" ]]; then
        copy_source "$repo_root/tools/anchor-target-controller-verification/NativeRemovalIntegrationTests.cs" \
            "$project_dir/Assets/Verification/Abi/NativeRemovalIntegrationTests.cs"
        copy_source "$repo_root/tools/anchor-target-controller-verification/NativeSupportIntegrationTests.cs" \
            "$project_dir/Assets/Verification/Abi/NativeSupportIntegrationTests.cs"
        copy_source "$native_library" "$project_dir/Assets/Plugins/libspatialblend.dylib"
    fi
fi

{
    echo "# SpatialBlend isolated verification provenance"
    echo
    echo "This temporary project copies, byte-for-byte, the following current repository files:"
    echo
    for source in "${provenance_sources[@]}"; do
        relative=${source#"$repo_root/"}
        hash=$(shasum -a 256 "$source" | awk '{print $1}')
        echo "- $relative ($hash)"
    done
    echo
    echo "The project references only local Unity Test Framework and NUnit PackageCache paths."
    echo "It imports no SpatialBlend scenes, packages, or Meta SDK assets."
    if "$include_abi"; then
        echo "ABI selection runs DeviceFree and Conversion separately."
        if [[ -n "$native_library" ]]; then
            echo "NativeIntegration is explicitly enabled using the byte-verified library above."
        else
            echo "NativeIntegration is explicitly excluded (no native library supplied)."
        fi
    fi
} > "$project_dir/Assets/Verification/source-provenance.txt"

validate_xml() {
    local label=$1 xml_path=$2 total passed failed skipped
    if [[ ! -s "$xml_path" ]]; then
        echo "Unity produced no $label test XML: $xml_path" >&2
        return 1
    fi
    total=$(sed -nE 's/.*total="([0-9]+)".*/\1/p' "$xml_path" | head -1)
    passed=$(sed -nE 's/.*passed="([0-9]+)".*/\1/p' "$xml_path" | head -1)
    failed=$(sed -nE 's/.*failed="([0-9]+)".*/\1/p' "$xml_path" | head -1)
    skipped=$(sed -nE 's/.*skipped="([0-9]+)".*/\1/p' "$xml_path" | head -1)
    if [[ -z "$total" || -z "$passed" || -z "$failed" || -z "$skipped" || "$total" -le 0 ]]; then
        echo "Unity $label XML has incomplete or empty counts: $xml_path" >&2
        return 1
    fi
    if [[ "$passed" -ne "$total" || "$failed" -ne 0 || "$skipped" -ne 0 ]]; then
        echo "Unity $label XML is not a clean complete pass: total=$total passed=$passed failed=$failed skipped=$skipped" >&2
        return 1
    fi
    echo "${label}_COUNTS=total:$total passed:$passed failed:$failed skipped:$skipped"
}

run_suite() {
    local label=$1 xml_path=$2 log_path=$3 unity_status
    shift 3
    set +e
    # -quit would stop after import, before the command-line test runner starts.
    unity_clean_env "$unity_bin" -batchmode -nographics -projectPath "$project_dir" -runTests \
        -testPlatform EditMode -testResults "$xml_path" -logFile "$log_path" "$@"
    unity_status=$?
    set -e
    if ! validate_xml "$label" "$xml_path"; then
        echo "Editor log: $log_path" >&2
        exit 1
    fi
    if [[ "$unity_status" -ne 0 ]]; then
        echo "Unity returned $unity_status despite clean $label XML. Editor log: $log_path" >&2
        exit "$unity_status"
    fi
}

controller_xml="$project_dir/TestResults/AnchorTargetController.xml"
controller_log="$project_dir/controller-editor.log"
run_suite "anchor_target" "$controller_xml" "$controller_log" \
    -testFilter "SpatialBlend.Tests.AnchorTargetControllerTests"
echo "ANCHOR_TARGET_PROJECT=$project_dir"
echo "ANCHOR_TARGET_RESULTS=$controller_xml"
echo "ANCHOR_TARGET_LOG=$controller_log"

if "$include_abi"; then
    device_free_xml="$project_dir/TestResults/SpatialBlendAbiDeviceFree.xml"
    device_free_log="$project_dir/abi-device-free-editor.log"
    conversion_xml="$project_dir/TestResults/SpatialBlendAbiConversion.xml"
    conversion_log="$project_dir/abi-conversion-editor.log"
    # These categories never select NativeIntegration implicitly.
    run_suite "abi_device_free" "$device_free_xml" "$device_free_log" -testCategory "DeviceFree"
    run_suite "abi_conversion" "$conversion_xml" "$conversion_log" -testCategory "Conversion"
    echo "ABI_DEVICE_FREE_RESULTS=$device_free_xml"
    echo "ABI_DEVICE_FREE_LOG=$device_free_log"
    echo "ABI_CONVERSION_RESULTS=$conversion_xml"
    echo "ABI_CONVERSION_LOG=$conversion_log"
    if [[ -n "$native_library" ]]; then
        native_xml="$project_dir/TestResults/SpatialBlendNativeIntegration.xml"
        native_log="$project_dir/native-integration-editor.log"
        run_suite "abi_native_integration" "$native_xml" "$native_log" -testCategory "NativeIntegration"
        for required_case in \
            "SpatialBlend.Tests.SpatialBlendAbiTests.RealizationTargetLayout_MatchesNativeLibrary" \
            "SpatialBlend.Tests.NativeRemovalIntegrationTests.RemovalTraversesRealNativeAndManagedBoundaryWithSpaceIsolation"; do
            if ! rg -Fq "fullname=\"$required_case\"" "$native_xml"; then
                echo "Required native integration case was not executed: $required_case" >&2
                exit 1
            fi
        done
        echo "ABI_NATIVE_RESULTS=$native_xml"
        echo "ABI_NATIVE_LOG=$native_log"
    else
        echo "ABI_EXCLUDED=NativeIntegration (no --native-library supplied)"
    fi
fi
