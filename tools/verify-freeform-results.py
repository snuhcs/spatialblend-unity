#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Strictly validate the EditMode FreeformRestoration NUnit XML artifact."""
import sys
import xml.etree.ElementTree as ET


# Manually run and out-of-scope parity rows stay [Ignore]. A category run reports
# them as skipped. Unfinished rows stay [Explicit]. Unity's -testCategory
# filter still executes those rows, so a Failed result with runstate Explicit
# on FreeformV1ParityTests is expected. Every other skip or failure refuses
# the artifact.
DOCUMENTED_IGNORES = {
    "A6_FullBodyOsMotion_IsOutOfParityScope",
    "P2_WindowsStandalone_IsOutOfScope",
    "T46_DeviceAcceptance_IsOwnerRun",
}

REQUIRED = (
    "WhiteboardRestorationTests",
    "LocalSessionConfigTests",
    "WhiteboardSurfaceChartTests",
    "ChessboardPrefabContractTests",
    "FreeformSceneRestorationTests",
    "FreeformArrivalContractTests",
    "EntityLifecycleContractTests",
    "ManagerLifecycleContractTests",
    "RemovalCallbackTests",
    "ManagerConnectionContractTests",
    "ManagerContainmentContractTests",
    "SourceHeadAnchorTests",
    "ContainmentTransferBindingTests",
    "ContainmentInteractionMapperTests",
    "ContinuousCompensationTests",
    "FusionConnectionContractTests",
    "FusionSessionBrowserTests",
    "FreeformSpectatorTests", "FreeformSpectatorSessionTests", "FreeformSpectatorSceneIdentityTests", "FreeformSpectatorLauncherTests",
    "FreeformMatchmakingContractTests",
    "FreeformVoiceStackTests",
    "RequiredCredentialsTests",
    "FreeformUIContractTests",
    "TutorialFlowTests",
    "TutorialRunnerTests",
    "TutorialNarratorTests",
    "TutorialDoneMarkTests",
    "TutorialScanTests",
    "TutorialHookTests",
    "TutorialUiTests",
    "SceneGeometryContractTests",
    "ReplicaLifecycleComponentTests",
    "SpatialHierarchyAndAnchorPoseContractTests",
    "SpatialAuthoringContractTests",
    "SpatialAuthoringPrefabContractTests",
    "AnchorPoseInputContractTests",
    "DynamicSceneGeometryContractTests",
    "FoundationSceneGeometryTests",
    "NavMeshWalkableExtractorTests",
    "WalkablePlacementSnapTests",
    "AffordanceVolumeMigrationTests",
    "WalkableRegistrationContractTests",
    "FreeformSceneRoomGeometryTests",
    "NativeContentLoadContractTests",
    "ArticulatedTargetControllerTests",
    "SeatRegistrationTests",
    "RoomShareTests",
    "RecordedAnchorPoseReplayTests",
    "FoundationCoordinateContractTests",
    "SpatialBlendObjectContractTests",
    "FoundationFreeformContentTests",
    "AnchorTargetLeaseTests",
    "SbRetargetAnchorTargetBridgeTests",
    "FreeformSceneInputTests",
    "FreeformSceneLocalBackendTests",
    "AnchorPoseReplayLifetimeTests",
    "FoundationAvatarRetargetEntityTests",
    "SbArticulatedBodyTrackingContextTests",
    "SbArticulatedInputBindingTests",
    "ReceiverBodySdkProfileTests",
    "FreeformSceneActorDefinitionTests",
    "SbLocalPresetAvatarTests",
    "RemoteAvatarVisibilityPolicyTests",
    "RemoteAvatarControllerTests",
    "NetworkedAvatarControllerPrefabTests",
    "AvatarStateUpdateFilterTests",
    "HandTransformDataTests",
    "SbPermissionManagerTests",
    "CaptureCameraAndStyleTests",
    "SbPermissionSceneHookTests",
    "DeclaredRegionZoneTests",
    "ZoneBasedDeclaredRegionContractTests",
    "AvatarAlphaFadeShaderTests",
    "EmbodiedDriverTests",
    "SceneFrameRulesTests",
    "FoundationSceneStatusTests",
    "EmbodiedSingleOwnerTests",
    "FoundationEmbodiedPortBindingTests",
    "FreeformV1ParityTests",
    "DepthOcclusionTests",
    "NativeEntityReadyToShareTrackerTests",
    "FreeformContentWriterContractTests",
    "SceneAnchorLinkContractTests",
    "ScanSessionConfigTests",
    "ScanRequestBuilderTests",
    "ScanResponseParserTests",
    "ScanPendingTrackerTests",
    "ScanServiceTests",
    "ScanPromptTests",
    "ScanFlowTests",
    "SessionLibraryLabelTests",
    "ScanCardTests",
    "ScanAimingTests",
    "ScanBoxRegionTests",
    "SessionManagerConfigTests",
    "BoxAnnotationAuthoringContractTests",
    "PresentationPortContractTests",
    "ReconstructedObjectFitTests",
    "BoxAnnotationHeightTests",
    "AvatarLoadDecisionTests",
    "ObjectPlacementSnapTests",
    "MrukPrefabRoomTests",
    "BoxAnnotationPointSelectionReleaseTests",
    "PlacementModeReleaseTests",
    "EnvironmentSurfaceModeTests",
    "EntityLayerTests",
    "ChessboardTableSpawnTests",
    "DeclaredRegionOutlineModeTests",
    "SceneAnchorVisibilityToggleTests",
    "DarkThemePanelPrefabTests", "UiConformanceGuardTests", "UiDesignSystemTests",
    "AvatarSendThresholdPrefabTests",
    "ReconstructedPreviewTests", "ReceivedScanPlacementTests", "OwnScanStaysPutTests",
    "RemoteStagingTests",
    "ManipulationPopupDeleteAndGizmoTests",
    "ManipulationPopupDeleteConfirmTests",
    "RegionPolygonRefusalTests",
    "RoomModeResetTests", "BoxUndoHintTests", "ReceivedCopyMenuTests",
    "SpectatorOverlayTests",
    "SharedBoxLifecycleTests",
)


def main(path: str) -> int:
    try:
        result = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise SystemExit(f"No usable Freeform test XML: {path}: {error}")
    cases = result.findall(".//test-case")
    for name in REQUIRED:
        if not any(name in case.get("fullname", "").split(".")[:-1] for case in cases):
            raise SystemExit(f"Required Freeform suite was not executed: {name}")
    passed = skipped = 0
    bad = []
    for case in cases:
        outcome = case.get("result")
        fullname = case.get("fullname", "")
        # An Explicit test that failed is a failed test: there is no Explicit tolerance.
        if outcome == "Passed":
            passed += 1
            continue
        method = fullname.split(".")[-1]
        if (outcome in {"Skipped", "Ignored"} and method in DOCUMENTED_IGNORES and
                "FreeformV1ParityTests" in fullname.split(".")):
            skipped += 1
            continue
        bad.append(fullname)
    # The documented ignores alone make Unity report the root as "Skipped:Ignored"; every case was classified
    # above, so that root is accepted. Any other root outcome is a failure.
    root_ok = result.get("result") in {"Passed", "Skipped:Ignored"}
    if not cases or bad or not root_ok:
        raise SystemExit(f"Freeform tests did not all pass: {bad}; root={result.attrib}")
    print(f"FREEFORM_COUNTS=passed:{passed} failed:0 skipped:{skipped}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: verify-freeform-results.py RESULTS.xml")
    raise SystemExit(main(sys.argv[1]))
