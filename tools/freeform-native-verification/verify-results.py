# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Require real manager and original-scene native cases, not Editor exit 0."""
import sys
import xml.etree.ElementTree as ET

SDK_CASES = (
    "OneBodyFeedsRealNativeFkAndActualSdkCallbacksWithNoLegacyProviderOrActorWrite",
    "MalformedSdkPayloadAndNativeRejectionPreservePriorFrameWithoutConsumingSourceSequence",
    "ExplicitJointOrRootLossReachesNativeAndWithdrawsWholeSdkPose(False)",
    "ExplicitJointOrRootLossReachesNativeAndWithdrawsWholeSdkPose(True)",
    'ManagerLifecycleWithdrawsNativeAndSdkAndNeverFallsBack("clear")',
    'ManagerLifecycleWithdrawsNativeAndSdkAndNeverFallsBack("disable")',
    'ManagerLifecycleWithdrawsNativeAndSdkAndNeverFallsBack("detach")',
    'ManagerLifecycleWithdrawsNativeAndSdkAndNeverFallsBack("destroy")',
    "NativeAndSdkFailureCleanupRetriesAndCannotLeaveOldBodyAvailable",
    "FailedAttachReturnsExactCleanupHandleAndDoesNotDetachAnExistingOwner",
    "DestroyedManagerListenerFailureRetainsClosedInputAndExplicitRetryBeforeSdkDisposal",
    "ReplacementEpochInvalidatesCallbacksImmediatelyAndOldCleanupCannotTouchReplacement",
    "WorkerCannotSubmitClearOrCloseButCallbackUsesActualNativeExpiryWithoutUpdate",
)


# The source head anchor against a core that reports person body tops. Opt-in (--source-head): the
# fixture is [Explicit] so an unfiltered Run All against an older fetched library never runs it.
SOURCE_HEAD_CASES = (
    "ASeatedSourceBehindACounterDoesNotSeeThePersonAndAStandingOneDoes",
    "ReleasingTheSourceGivesItsHeadAnchorBackItsRegisteredHeight",
)


def verify(path, require_graphics=False, require_receiver_body_sdk=False, require_source_head=False):
    root = ET.parse(path).getroot()
    cases = root.findall(".//test-case")
    required = {
        "RealManagersConnectDisconnectAndReconnectWithoutPhotonRunner",
        "ContainmentTransferBindingsMatchTheCoreLayoutAndOffsets",
        "TransferFlagRaisesStartThenTerminateWhenTheClientAvatarEntersAndLeavesTheObjectRegion",
        "RealNativeHierarchyAndRecordedAnchorGroundingPreserveRootPose",
        "OriginalFreeformScene_LocalBackend_StartsContentRefreshesPlacementAndStops",
        "OriginalFreeformScene_LocalBackend_StopAndRetryRestoresAuthoredViews",
        "OriginalFreeformScene_WhiteboardReceiverSupportRegistersRefreshesAndRestarts",
        "OriginalFreeformScene_LocalBackend_ReentrantObserverStopFailsStartThenRetries",
        "OriginalFreeformScene_RecordedActorFramesReachOriginalAvatarTrackingConsumer",
        "OriginalFreeformScene_RetiredActorRuntimeCannotClearReplacementLifetime",
        "OriginalFreeformScene_ExternalInputReplacementFencesRuntimeAndPreservesSource",
        "OriginalFreeformScene_StopDuringNativeAdmissionRetriesAfterSubmissionUnwinds",
        "OriginalFreeformScene_ExplicitSupportMapDrivesNativeHandTargetAndTracking",
        "OriginalFreeformScene_ActorElementIsThePersonLeafAndNeverFollowsARegion",
        "OriginalFreeformScene_OptionalSeatChannelMapsAndOmissionReleasesTarget",
        "OriginalFreeformScene_MrukGeometryUsesReceiverChartAndExactRoomLifetime",
        "OriginalFreeformScene_ActorsWaitForTheRoomsWalkableFootprintWithoutBlockingContent",
        "WalkableFourByFourWithTwoByTwoHoleRegistersOneObstacleChildAndReregisterRemovesIt",
        "WalkableHolesAndTheMrukBoxesOfTheSameFurnitureRegisterTogetherInEitherOrder",
        "TheEightScannedSampleRoomsRegisterWithoutARefusedOverlapAndSurviveARemovalAndAReplacement",
        "ChessContentOnTheMrukTableRegistersUnderTheTablesTopAndFollowsItsMoves",
        "ContentOnAnAnchorSurvivesTheAnchorBeingRestagedAndRemoved",
        "ColliderContentOnTheMrukTableRegistersUnderTheTable",
        "RemoteAvatarTargetReachesTheHeadWithinTheDriverBounds",
        "RemoteAvatarEmptySnapshotReleasesTheTarget",
        "RemoteAvatarIsNotWrittenAfterOnRemove",
        "TableAnchorHostSharesTheChosenTableReceiverLinksTheSimilarOneAndUnshareReleasesTheLink",
        "SharedSeatsLinkToTheRoomsSimilarSeatOrArriveAsABoxToInstall",
        "ARoomChangeWhileLinkedUnlinksTheSharersSeat",
        "ARoomChangeUnlinksEvenWhenMrukAlreadyDestroyedTheAnchor",
        "AConfirmedFrontSeatRegistersItsRegionAndAffordance",
        "IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw0",
        "IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw90",
        "IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw200",
        "AReceiverThatMovesItsCopyAfterArrivalSeesTheRemoteAvatarOnTheMovedCopy",
        "IdenticalChessRemoteAvatarObjectRelativeIdentityHoldsOnlyIfTheRegionIsATransferLeaf",
        "AvatarCrossingTheRegionEdgeIsFollowedWithinTheDriversBoundsAndTheTransferFlagSwitchesAtTheEdge",
        "RemoteAvatarHeadPitchTravelsFromTheSourceThroughTheProposalToTheHead",
        "ReceiverWithoutTheObjectFallsBackToTheSceneLevelRootAndRaisesNoInteraction",
        "ACounterpartThatAppearsLateSetsTheTransferFlagOnTheNextUpdateAndTheDisplaySwitchesOnceTheBodyArrives",
        "ChessAnchorLeavingTheRegionHandsBackToSceneLevelRelations",
        "CrossDeviceTableChartIsPreservedForDifferentTables",
        "TheFreeformSceneAuthoredObjectsRegisterWithoutSiblingOverlap",
        "TwoPeersThatScanAtTheSameTimeEachKeepTheirOwnBoxAndScaleAndTheCopiesAgreeWithTheSources",
        "AScannedObjectReachesThePeerOverTheTunneledLinkWithItsLargeContent",
        "AvatarSemanticTransferBetweenDifferentRoomLayoutsIsMeasuredInBothDirections",
        "AvatarReachAndGazeAtAnAuthoredObjectArePreservedAcrossTwoRoomPosesInBothDirections",
    }
    required = {"SpatialBlend.Tests.NativeManagerIntegrationTests." + name for name in required}
    required.update(
        "SpatialBlend.Tests.NativeManagerIntegrationTests.AScannedObjectRegisteredWithOrWithoutPropagateReachesThePeerWithItsContent(" + flag + ")"
        for flag in ("True", "False"))
    if require_receiver_body_sdk:
        required.update("SpatialBlend.Tests.ReceiverBodySdkNativeTests." + name for name in SDK_CASES)
    if require_source_head:
        required.update("SpatialBlend.Tests.SourceHeadAnchorNativeTests." + name for name in SOURCE_HEAD_CASES)
    required.add(
        "SpatialBlend.Tests.SbLocalPresetAvatarPlayModeTests."
        "FailedStagedCloneNeverActivatesLegacyAvatarInPlayMode"
    )
    if require_graphics:
        required.add(
            "SpatialBlend.Tests.NativeFreeformSceneGraphicsTests."
            "OriginalFreeformScene_LocalBackend_RendersAuthoredWhiteboardAndChess"
        )
    names = {case.get("fullname") for case in cases}
    missing = required - names
    bad = []
    passed = 0
    for case in cases:
        if case.get("result") == "Passed":
            passed += 1
        else:
            bad.append(case.get("fullname"))
    if missing or bad or root.get("result") != "Passed":
        raise SystemExit(f"Native manager gate failed: missing={sorted(missing)}, bad={bad}, root={root.attrib}")
    print(f"FREEFORM_NATIVE_COUNTS=passed:{passed} failed:0 skipped:0")


if __name__ == "__main__":
    options = sys.argv[2:]
    if len(sys.argv) < 2 or len(set(options)) != len(options) or any(
            option not in ("--graphics", "--receiver-body-sdk", "--source-head") for option in options):
        raise SystemExit("Usage: verify-results.py RESULTS.xml [--graphics] [--receiver-body-sdk] [--source-head]")
    verify(sys.argv[1], require_graphics="--graphics" in options,
           require_receiver_body_sdk="--receiver-body-sdk" in options,
           require_source_head="--source-head" in options)
