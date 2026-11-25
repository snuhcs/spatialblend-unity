#!/usr/bin/env bash
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

set -euo pipefail

# Tests the actual Foundation asmdef, not an arbitrary recompiled subset under
# the legacy SpatialBlend assembly. Copies only Unity/System-only sources and a
# deliberately supplied current native library into an isolated temporary project.
if [[ $# -lt 2 ]] || [[ "$1" != "--native-library" || "$2" != /* || ! -f "$2" || "$(basename "$2")" != libspatialblend.dylib ]]; then
    echo "Usage: $0 --native-library /absolute/path/libspatialblend.dylib [--room-session] [--shared-local-binding] [--logical-content-binding] [--interaction-hierarchy [--observed-collaborator [--observed-collaborator-native]]] [--observed-collaborator-compatibility] [--source-evidence] [--receiver-approach] [--graphics] [--rich [--validity [--receiver-input [--reviewed-recording-ingress] [--body-recordings /absolute/path/cases.json]]]]" >&2
    exit 2
fi
native_library=$2
graphics=false
rich=false
validity=false
receiver_input=false
reviewed_recording_ingress=false
source_evidence=false
interaction_hierarchy=false
observed_collaborator=false
observed_collaborator_native=false
observed_collaborator_compatibility=false
receiver_approach=false
room_session=false
shared_local_binding=false
logical_content_binding=false
body_recordings=
shift 2
while [[ $# -gt 0 ]]; do
    option=$1
    case "$option" in
        --interaction-hierarchy)
            if [[ "$interaction_hierarchy" == true ]]; then echo "Duplicate --interaction-hierarchy option." >&2; exit 2; fi
            interaction_hierarchy=true ;;
        --observed-collaborator)
            if [[ "$observed_collaborator" == true ]]; then echo "Duplicate --observed-collaborator option." >&2; exit 2; fi
            observed_collaborator=true ;;
        --observed-collaborator-native)
            if [[ "$observed_collaborator_native" == true ]]; then echo "Duplicate --observed-collaborator-native option." >&2; exit 2; fi
            observed_collaborator_native=true ;;
        --observed-collaborator-compatibility)
            if [[ "$observed_collaborator_compatibility" == true ]]; then echo "Duplicate --observed-collaborator-compatibility option." >&2; exit 2; fi
            observed_collaborator_compatibility=true ;;
        --receiver-approach)
            if [[ "$receiver_approach" == true ]]; then echo "Duplicate --receiver-approach option." >&2; exit 2; fi
            receiver_approach=true ;;
        --room-session)
            if [[ "$room_session" == true ]]; then echo "Duplicate --room-session option." >&2; exit 2; fi
            room_session=true ;;
        --shared-local-binding)
            if [[ "$shared_local_binding" == true ]]; then echo "Duplicate --shared-local-binding option." >&2; exit 2; fi
            shared_local_binding=true ;;
        --logical-content-binding)
            if [[ "$logical_content_binding" == true ]]; then echo "Duplicate --logical-content-binding option." >&2; exit 2; fi
            logical_content_binding=true ;;
        --source-evidence)
            if [[ "$source_evidence" == true ]]; then echo "Duplicate --source-evidence option." >&2; exit 2; fi
            source_evidence=true ;;
        --graphics)
            if [[ "$graphics" == true ]]; then echo "Duplicate --graphics option." >&2; exit 2; fi
            graphics=true ;;
        --rich)
            if [[ "$rich" == true ]]; then echo "Duplicate --rich option." >&2; exit 2; fi
            rich=true ;;
        --validity)
            if [[ "$validity" == true ]]; then echo "Duplicate --validity option." >&2; exit 2; fi
            validity=true ;;
        --receiver-input)
            if [[ "$receiver_input" == true ]]; then echo "Duplicate --receiver-input option." >&2; exit 2; fi
            receiver_input=true ;;
        --reviewed-recording-ingress)
            if [[ "$reviewed_recording_ingress" == true ]]; then echo "Duplicate --reviewed-recording-ingress option." >&2; exit 2; fi
            reviewed_recording_ingress=true ;;
        --body-recordings)
            if [[ -n "$body_recordings" || $# -lt 2 || "$2" != /* || ! -f "$2" ]]; then
                echo "--body-recordings requires one explicit absolute existing selection path." >&2; exit 2
            fi
            body_recordings=$2
            shift ;;
        *) echo "Unknown Foundation verification option: $option" >&2; exit 2 ;;
    esac
    shift
done
if [[ "$validity" == true && "$rich" != true ]]; then
    echo "--validity requires --rich and its separately negotiated native validity feature." >&2
    exit 2
fi
if [[ "$receiver_input" == true && "$validity" != true ]]; then
    echo "--receiver-input requires --rich --validity and the independent native receiver-input feature." >&2
    exit 2
fi
if [[ "$reviewed_recording_ingress" == true && "$receiver_input" != true ]]; then
    echo "--reviewed-recording-ingress requires --rich --validity --receiver-input." >&2
    exit 2
fi
if [[ "$receiver_approach" == true && "$receiver_input" != true ]]; then
    echo "--receiver-approach requires --rich --validity --receiver-input." >&2
    exit 2
fi
if [[ -n "$body_recordings" && "$receiver_input" != true ]]; then
    echo "--body-recordings requires --rich --validity --receiver-input." >&2
    exit 2
fi
if [[ "$logical_content_binding" == true && "$shared_local_binding" != true ]]; then
    echo "--logical-content-binding requires --shared-local-binding." >&2
    exit 2
fi
if [[ "$observed_collaborator" == true && "$interaction_hierarchy" != true ]]; then
    echo "--observed-collaborator requires --interaction-hierarchy; an authored exact hierarchy pair is its only board correspondence authority." >&2
    exit 2
fi
if [[ "$observed_collaborator_native" == true && "$observed_collaborator" != true ]]; then
    echo "--observed-collaborator-native requires --observed-collaborator and its authored hierarchy prerequisite." >&2
    exit 2
fi
if [[ "$observed_collaborator_compatibility" == true && ( "$observed_collaborator" == true || "$observed_collaborator_native" == true ) ]]; then
    echo "--observed-collaborator-compatibility requires a pre-C1 library and cannot be combined with C1-positive flags." >&2
    exit 2
fi
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)

# Data-hygiene guard (T6.0): no real dataset clip/scene names in tracked files. Cheap, python only.
/usr/bin/python3 "$repo_root/tools/test_no_real_clip_names.py" >&2 || { echo "data-hygiene guard failed: real clip names are tracked" >&2; exit 1; }
source "$(dirname "${BASH_SOURCE[0]}")/lib/unity-env.sh"
unity_bin=${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.0.56f1/Unity.app/Contents/MacOS/Unity}
python_bin=${PYTHON_BIN:-/usr/bin/python3}
utf_dir=$(find "$repo_root/Library/PackageCache" -maxdepth 1 -type d -name 'com.unity.test-framework@*' -print -quit)
nunit_dir=$(find "$repo_root/Library/PackageCache" -maxdepth 1 -type d -name 'com.unity.ext.nunit@*' -print -quit)
for required in "$unity_bin" "$python_bin" "$utf_dir" "$nunit_dir"; do
    if [[ -z "$required" || ! -e "$required" ]]; then
        echo "Missing local Foundation verification input: ${required:-unset}" >&2
        exit 2
    fi
done

project_dir=$(mktemp -d "${TMPDIR:-/tmp}/spatialblend-foundation.XXXXXX")
mkdir -p "$project_dir/Assets/Tests" "$project_dir/Assets/Plugins" "$project_dir/Packages" "$project_dir/ProjectSettings"
echo "FOUNDATION_PROJECT=$project_dir"
sed -e "s|@UTF_DIR@|$utf_dir|g" -e "s|@NUNIT_DIR@|$nunit_dir|g" \
    "$repo_root/tools/anchor-target-controller-verification/manifest.json.in" > "$project_dir/Packages/manifest.json"
cp "$repo_root/tools/anchor-target-controller-verification/ProjectVersion.txt" "$project_dir/ProjectSettings/ProjectVersion.txt"
cp "$repo_root/tools/foundation-verification/SpatialBlend.Foundation.Tests.asmdef" "$project_dir/Assets/Tests/"

copy_input() {
    local source=$1 relative=$2 destination
    destination="$project_dir/$relative"
    mkdir -p "$(dirname "$destination")"
    cp "$source" "$destination"
    cmp -s "$source" "$destination" || { echo "Source byte mismatch: $relative" >&2; exit 2; }
    shasum -a 256 "$source" >> "$project_dir/SOURCE_SHA256.txt"
}
for subtree in Assets/SpatialBlend/Foundation Assets/SpatialBlend/Framework/Scripts/Motion Assets/SpatialBlend/Freeform; do
    while IFS= read -r source; do
        copy_input "$source" "${source#"$repo_root/"}"
    done < <(find "$repo_root/$subtree" -type f | LC_ALL=C sort)
done

test_sources=(
    Assets/SpatialBlend/Framework/Test/AnchorTargetControllerTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ArticulatedTargetControllerTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationCoordinateContractTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationNativeClockMappingTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationSceneGeometryTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/NavMeshWalkableExtractorTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/WalkablePlacementSnapTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/RecordedAnchorPoseReplayTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/AnchorPoseReplayLifetimeTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/SpatialBlendObjectContractTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationActorNativeLifecycleTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationActorMotionInputTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationRelationEventTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ContainmentInteractionMapperTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationCommittedMotionTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationFreeformContentTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/AnchorTargetLeaseTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FreeformLocalAppTests.cs
    tools/anchor-target-controller-verification/NativeSupportIntegrationTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/RemoteAvatarVisibilityPolicyTests.cs
    Assets/SpatialBlend/Avatar/Scripts/RemoteAvatarVisibilityPolicy.cs
    Assets/SpatialBlend/Framework/Test/Restoration/RemoteAvatarControllerTests.cs
    Assets/SpatialBlend/Avatar/Scripts/RemoteAvatarControllerAttachment.cs
    Assets/SpatialBlend/Framework/Test/Restoration/AvatarStateUpdateFilterTests.cs
    Assets/SpatialBlend/Framework/Scripts/AvatarStateUpdateFilter.cs
    Assets/SpatialBlend/Framework/Types/AvatarDisplayMode.cs
    Assets/SpatialBlend/Framework/Test/Restoration/SbPermissionManagerTests.cs
    Assets/SpatialBlend/Framework/Scripts/SbPermissionManager.cs
    Assets/SpatialBlend/Framework/Test/Restoration/EmbodiedDriverTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationSceneStatusTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FoundationEmbodiedPortBindingTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/NativeEntityReadyToShareTrackerTests.cs
    Assets/SpatialBlend/Framework/Scripts/NativeContent/NativeEntityReadyToShareTracker.cs
    Assets/SpatialBlend/Framework/Test/Restoration/FreeformContentWriterContractTests.cs
    Assets/SpatialBlend/Framework/Scripts/FreeformContentWriterPolicy.cs
    Assets/SpatialBlend/Framework/Test/Restoration/DeclaredRegionZoneTests.cs
    Assets/SpatialBlend/Avatar/Scripts/DeclaredRegionZone.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ScanFakes.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ScanPureTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ScanServiceTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ScanPromptTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ScanFlowTests.cs
    Assets/SpatialBlend/Framework/Test/Restoration/ScanBoxRegionTests.cs
)
if [[ "$source_evidence" == true ]]; then
    # Independent optional feature; baseline ABI2 tests still run against older libraries.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationSourceInteractionTests.cs)
fi
if [[ "$interaction_hierarchy" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationInteractionHierarchyTests.cs)
fi
if [[ "$observed_collaborator" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationObservedCollaboratorTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationObservedCollaboratorAdmissionBarrierTests.cs)
fi
if [[ "$observed_collaborator_native" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationObservedCollaboratorNativeTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationObservedSourceAnchorPointTests.cs)
fi
if [[ "$observed_collaborator_compatibility" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationObservedCollaboratorCompatibilityTests.cs)
fi
if [[ "$rich" == true ]]; then
    # The additive feature has its own negotiated native requirement. Keeping
    # this explicit allows ABI2 regression runs against a pre-feature library
    # without counting skipped rich tests as a successful integration gate.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRichRealizationTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRichMailboxTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRichSurfaceNativeTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRichCallbackDispatchTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRichAdapterTests.cs)
fi
if [[ "$validity" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRichValidityTests.cs)
fi
if [[ "$receiver_input" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationReceiverInputTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationReceiverBodyInputTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationBufferedBodySourceTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationBodyRecordingTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationBodyRecordingReplayTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRigOutputTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRigCarrierTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationBodyPreflightTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationReceiverReevaluationTests.cs)
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationPlanarSupportTests.cs)
    # Embodiment routing by family / goal / scope level.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationEmbodimentDispatcherTests.cs)
fi
if [[ "$reviewed_recording_ingress" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/ReviewedDatasetBodyRecordingIngressTests.cs)
fi
if [[ "$receiver_approach" == true ]]; then
    # C4 has an independent native C3c/C3a feature gate. It must remain
    # optional so existing receiver-input regression runs work with old Core.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationReceiverApproachTests.cs)
fi
if [[ "$room_session" == true ]]; then
    # U0 uses a fake bounded adapter only.  It must compile as plain Foundation
    # and never depends on a selected socket/native room-client ABI.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationRoomSessionTests.cs)
fi
if [[ "$shared_local_binding" == true ]]; then
    # U1 is a plain Foundation receiver-local adapter.  It intentionally does
    # not select a socket/native client, require Fusion, or inherit U0 state.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationSharedToLocalBindingTests.cs)
fi
if [[ "$logical_content_binding" == true ]]; then
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationLogicalContentBindingTests.cs)
fi
if [[ -n "$body_recordings" ]]; then
    # No corpus files in the project/repository, and no missing-input skips.
    test_sources+=(Assets/SpatialBlend/Framework/Test/Restoration/FoundationBodyRecordingSourceTests.cs)
    export SPATIALBLEND_BODY_RECORDING_CASES="$body_recordings"
    SPATIALBLEND_BODY_RECORDING_SELECTION_SHA256=$(shasum -a 256 "$body_recordings" | cut -d ' ' -f 1)
    export SPATIALBLEND_BODY_RECORDING_SELECTION_SHA256
    echo "FOUNDATION_BODY_SELECTION_SHA256=$SPATIALBLEND_BODY_RECORDING_SELECTION_SHA256"
fi
for relative in "${test_sources[@]}"; do
    copy_input "$repo_root/$relative" "Assets/Tests/$(basename "$relative")"
done
copy_input "$native_library" "Assets/Plugins/libspatialblend.dylib"

# The graphics fixture writes its overview PNG here (SPATIALBLEND_* passes through unity_clean_env).
export SPATIALBLEND_FOUNDATION_ARTIFACTS_DIR="$project_dir"
unity_args=(-batchmode -projectPath "$project_dir" -runTests -testPlatform EditMode
    -testResults "$project_dir/results.xml" -logFile "$project_dir/editor.log")
verify_args=("$project_dir/results.xml")
if [[ "$interaction_hierarchy" == true ]]; then verify_args+=(--interaction-hierarchy); fi
if [[ "$observed_collaborator" == true ]]; then verify_args+=(--observed-collaborator); fi
if [[ "$observed_collaborator_native" == true ]]; then verify_args+=(--observed-collaborator-native); fi
if [[ "$observed_collaborator_compatibility" == true ]]; then verify_args+=(--observed-collaborator-compatibility); fi
if [[ "$source_evidence" == true ]]; then verify_args+=(--source-evidence); fi
if [[ "$rich" == true ]]; then verify_args+=(--rich); fi
if [[ "$validity" == true ]]; then verify_args+=(--validity); fi
if [[ "$receiver_input" == true ]]; then verify_args+=(--receiver-input); fi
if [[ "$reviewed_recording_ingress" == true ]]; then verify_args+=(--reviewed-recording-ingress); fi
if [[ "$receiver_approach" == true ]]; then verify_args+=(--receiver-approach); fi
if [[ "$room_session" == true ]]; then verify_args+=(--room-session); fi
if [[ "$shared_local_binding" == true ]]; then verify_args+=(--shared-local-binding); fi
if [[ "$logical_content_binding" == true ]]; then verify_args+=(--logical-content-binding); fi
if [[ -n "$body_recordings" ]]; then verify_args+=(--body-recordings); fi
if [[ "$graphics" == true ]]; then
    verify_args+=(--graphics)
else
    unity_args+=(-nographics -testCategory '!FoundationGraphics')
fi
set +e
unity_clean_env "$unity_bin" "${unity_args[@]}"
unity_status=$?
set -e
"$python_bin" "$repo_root/tools/verify-foundation-results.py" "${verify_args[@]}"
if [[ "$unity_status" -ne 0 ]]; then
    echo "Editor exit $unity_status; see $project_dir/editor.log" >&2
    exit "$unity_status"
fi
if [[ "$graphics" == true && ! -s "$project_dir/freeform-local-graphics-overview.png" ]]; then
    echo "Foundation graphics fixture did not write its overview PNG." >&2
    exit 1
fi
echo "FOUNDATION_RESULTS=$project_dir/results.xml"
if [[ -n "$body_recordings" ]]; then
    body_result="$project_dir/body-recording-reader-result.json"
    if [[ ! -s "$body_result" ]]; then
        echo "Body recording fixture did not write its metadata-only receipt." >&2
        exit 1
    fi
    echo "FOUNDATION_BODY_RESULT=$body_result"
fi
if [[ "$rich" == true ]]; then
    rich_manifest="$project_dir/rich-realization-fixtures/manifest.json"
    support_manifest="$project_dir/rich-realization-support-fixtures/native-support-manifest.json"
    for manifest in "$rich_manifest" "$support_manifest"; do
        if [[ ! -s "$manifest" ]]; then
            echo "Rich native fixture did not write its capture manifest: $manifest" >&2
            exit 1
        fi
    done
    echo "FOUNDATION_RICH_MANIFEST=$rich_manifest"
    echo "FOUNDATION_RICH_SUPPORT_MANIFEST=$support_manifest"
fi
if [[ "$graphics" == true ]]; then
    echo "FOUNDATION_GRAPHICS_PNG=$project_dir/freeform-local-graphics-overview.png"
fi
