#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Fail closed on empty, skipped, or missing Foundation verification fixtures."""
import argparse
import sys
import xml.etree.ElementTree as ET

REQUIRED_FIXTURES = {
    "AnchorTargetControllerTests",
    "ArticulatedTargetControllerTests",
    "FoundationCoordinateContractTests",
    "FoundationNativeClockMappingTests",
    "FoundationSceneGeometryTests",
    "NavMeshWalkableExtractorTests",
    "WalkablePlacementSnapTests",
    "RecordedAnchorPoseReplayTests",
    "AnchorPoseReplayLifetimeTests",
    "SpatialBlendObjectContractTests",
    "SpatialBlendObjectNativeTests",
    "NativeSupportIntegrationTests",
    "FoundationActorNativeLifecycleTests",
    "FoundationActorMotionInputTests",
    "FoundationRelationEventTests",
    "ContainmentInteractionMapperTests",
    "FoundationCommittedMotionTests",
    "FoundationFreeformContentTests",
    "FoundationFreeformContentNativeTests",
    "AnchorTargetLeaseTests",
    "FreeformLocalAppTests",
    "FreeformLocalAppPlayModeTests",
    "RemoteAvatarVisibilityPolicyTests",
    "RemoteAvatarControllerTests",
    "AvatarStateUpdateFilterTests",
    "SbPermissionManagerTests",
    "EmbodiedDriverTests",
    "FoundationSceneStatusTests",
    "FoundationEmbodiedPortBindingTests",
    "FoundationEmbodiedPortNativeTests",
    "NativeEntityReadyToShareTrackerTests",
    "FreeformContentWriterContractTests",
    "DeclaredRegionZoneTests",
    "ScanSessionConfigTests",
    "ScanRequestBuilderTests",
    "ScanResponseParserTests",
    "ScanPendingTrackerTests",
    "ScanServiceTests",
    "ScanPromptTests",
    "ScanFlowTests",
    "ScanBoxRegionTests",
}
GRAPHICS_FIXTURES = {"FreeformLocalAppGraphicsTests"}
INTERACTION_HIERARCHY_CASES = {
    "HierarchyOwnsRootsAndPreservesProjectedDepthBeforeContact",
    "HierarchyRejectsCrossedDuplicateWrongKindAndForeignEndpointsAtomically",
    "HierarchyBoundsAndReentrantCollectionsCannotChangePinnedWorld",
    "HierarchyCannotBorrowLegacyPairOrPartiallyOwnRoots",
    "HierarchyMappedEndpointAndUnrelatedSpaceRetirementCloseMap",
    "HierarchyPathRejectsDetachedRowMissingAncestorAndDifferentLifetime",
    "HierarchyWorkerCallsFailWithoutRetiringOrReadingUnityState",
    "HierarchyBusyCallbackRevokesAndRetriesExactRetirement",
    "HierarchyShutdownCannotRetireFreshWorldMap",
    "HierarchyTransformRefreshDoesNotCloseButObservedGeometryRetirementDoes",
}
SOURCE_EVIDENCE_CASES = {
    "SourceEvidenceNegotiatesEveryLayoutAndRejectsMismatches",
    "SourceEvidenceCopiesExactFrameHierarchyPolygonAndOriginalReceipt",
    "SourceEvidenceSubscriptionIsFutureOnlyAndLastLeaseDisablesCapture",
    "SourceEvidenceBodyOnlyAndEmptyFramesNeverInventRootOrContact",
    "SourceEvidenceControlsRevokeWithoutSampleClocksAndResetKeepsLease",
    "SourceEvidenceWriterLegacyAndRegistrationRetirementFenceOldLeases",
    "SourceEvidenceReentrantNewFrameSuppressesOlderDeliveryAcrossBothStreams",
    "SourceEvidenceSceneMutationBetweenObserversWithdrawsLaterEvidence",
    "SourceEvidenceRejectedInputPublishesNothingAndCallbacksCannotUndoAdmission",
    "SourceEvidenceWorkerAndGeneratedTransformsCannotObserveOrRetireInput",
    "SourceEvidenceReentrantResetAndReplacementCannotDeliverOldFrame",
    "SourceEvidenceMalformedRowsRejectWholeValue",
    "SourceEvidenceNativeCallbackBusyDisposalRetriesWithoutAffectingReplacement",
}
COMMITTED_MOTION_CASES = {
    "CommittedMotionCopiesAdmittedCoordinatesAndCompleteAvailability",
    "CommittedMotionReceiptPrecedesNativeAdmissionAndNeverUsesSourceTime",
    "CommittedMotionRejectedFramesPublishNothingOrConsumeSequence",
    "CommittedMotionClearAndResetAreTimelessControls",
    "CommittedMotionWriterReplacementAndRetirementFenceExactLeases",
    "CommittedMotionObserverMutationAndExceptionsCannotChangeAdmission",
    "CommittedMotionReentrantNewFrameSuppressesOlderDelivery",
    "CommittedMotionReentrantResetSuppressesOlderDelivery",
    "CommittedMotionReentrantWriterReplacementSuppressesOlderDelivery",
    "CommittedMotionLegacyTransitionsRetireBeforeMutation",
    "CommittedMotionWorkerAndRenderedTransformsCannotPublishOrRetire",
    "CommittedMotionRetirementCallbackReplacementCannotBeClearedByOldToken",
}
RICH_FIXTURES = {"FoundationRichRealizationTests", "FoundationRichRealizationNativeTests",
                 "FoundationRichMailboxTests", "FoundationRichSurfaceNativeTests",
                 "FoundationRichCallbackDispatchTests", "FoundationRichAdapterTests"}
VALIDITY_FIXTURES = {"FoundationRichValidityTests"}
RECEIVER_INPUT_FIXTURES = {"FoundationReceiverInputFrameTests", "FoundationReceiverInputNativeTests",
                           "FoundationReceiverInputRuntimeTests", "FoundationReceiverBodyFrameTests",
                           "FoundationReceiverBodyNativeTests", "FoundationReceiverBodyClockTests",
                           "FoundationBufferedBodySourceTests", "FoundationBodyRecordingTests",
                           "FoundationBodyRecordingReplayTests", "FoundationEmbodimentDispatcherTests"}
RECEIVER_APPROACH_FIXTURES = {"FoundationReceiverApproachTests"}
RECEIVER_APPROACH_CASES = {
    "EveryC3cAndC3aLayoutFieldNegotiatesAndDescriptorMismatchRejects",
    "CompleteC3cRegistrationAndPinnedTwoPassC3aCopyDeepOwnRouteAndProvenance",
    "ChangedInputOrContextIdentityRejectsBeforeAnyDetachedReceipt",
}
ROOM_SESSION_CASES = {
    "HostPreservesOpaqueN0BytesAndNeverCreatesLocalBindingOrFreshness",
    "CrossRoomLateCallbackAndReconnectCannotReplaceMembershipOrAuthority",
    "AuthorityAndExpiryRequireExactMembershipFence",
    "LeaveAndDestroyCancelOnlyTheirExactOperationAndQueuedCallbacksAreInert",
    "InvalidOrDeniedRequestsRemainInertAndTransportNeverReceivesThem",
    "TransportThreadDispatchAndBoundedOverflowFailClosedWithoutDeliveringPartialTail",
    "SceneWrapperIsInertUntilExplicitlyBoundAndDestroyRetiresOnlyItsClient",
}
SHARED_LOCAL_BINDING_CASES = {
    "ExplicitChairToSofaBindingPreservesLocalPlacementAndIssuesDetachedReceipt",
    "LocalMapSupportAndCalibrationChangesInvalidateBeforeNewGeneration",
    "StaleCrossRoomTombstoneUnavailableAndAmbiguousInputsFailClosed",
    "AuthorityEpochAndRemoteRevisionCannotBeInheritedFromReconnect",
    "WorkerCallsAndCallbackFreeReentryCannotRetireOrMutateOwnerBinding",
    "RegistryDoesNotDependOnFusionSocketOrSourceFreshnessTypes",
}
LOGICAL_CONTENT_BINDING_CASES = {
    "SnapshotTailRaceReorderAndDuplicateRecoverOneExactProjection",
    "BoardStrokeAndKeyedGameSchemasRemainLogicalAndBounded",
    "StaleBindingCrossRoomAndCompactedPrefixFailBeforeSink",
    "TombstoneAndLocalInvalidationCannotLeakOldContentToRecreatedBinding",
    "SinkFailureAndReentryCannotPublishPartialOrReplacementContent",
    "NativeIngressCopyKeepsN2BytesDetachedBeforeTheU1ReducerRuns",
    "NativeMailboxPollCopiesThroughTheU1ReducerWithoutGrantingManagedSubmission",
}
OBSERVED_COLLABORATOR_CASES = {
    "NativeBatchOnlyIssuesBoundedReadOnlyLeaseForIndependentActors",
    "RejectsSelfAndAmbiguousEvidenceBeforeAnyLease",
    "BoardRequiresExactAuthoredHierarchyPairAndWithdrawsOnReplacement",
}
OBSERVED_COLLABORATOR_BARRIER_CASES = {
    "ExactBatchIdStagesEveryParticipantBeforeOneAcknowledgement",
    "DuplicateOrInvalidMemberWithdrawsWholePendingBatchWithoutSequenceConsumption",
    "AcknowledgedParticipantClocksRejectReplayAndOwnerThreadMutation",
    "AbortedReadyTokenCannotAcknowledgeAReplacementWithTheSameBatchId",
    "MissingDeclaredAnchorRejectsTheWholePendingBatchAndSnapshotsAreDetached",
}
OBSERVED_COLLABORATOR_NATIVE_CASES = {
    "NativeC1BatchPinsNestedRowsAndRejectsWholeMalformedOrRetiredBatch",
    "PassivePointDeclarationLeavesExistingLegacySourceWriterUsable",
    "RejectsActorOwnerGeometryWrongAnchorCoreAgeChildAndActorAncestor",
    "ActorPointAndWorldReplacementMakeCapturedDeclarationStale",
}
REVIEWED_RECORDING_INGRESS_CASES = {
    "FullyReviewedMeasuredReceiptBindsOnlyItsInjectedReplaySource",
    "SyntheticUnreviewedOrIncompleteMeasuredReceiptCannotBecomeReviewedIngress",
    "ReceiptPinsEveryReplayCalibrationAndClockIdentityAndHasNoPublicReceiptDto",
}
BODY_RECORDING_REPLAY_CASES = {
    "RecordingReplayApprovedExactBindingPreservesAllRowsAndOriginalClocks",
    "RecordingReplayMissingDeniedThrowingApprovalNeverExposesFrames",
    "RecordingReplayRejectsPinsLabelsAndExactReferenceMismatch",
    "RecordingReplayRejectsClockSequenceAndCompleteExpiryWindowChanges",
    "RecordingReplayProfileAndEpochCannotReplaceApprovedIdentity",
    "RecordingReplayInvalidFinalFkNeverPublishesApprovedPrefix",
    "RecordingReplayDelegatesWaitBacklogExpiryWithoutRestamping",
    "RecordingReplayThreadAndApprovalReentryCannotPublishOrConsume",
    "RecordingReplayAdmittedEpochAgeIsCheckedBeforeApproval",
    "RecordingReplayFingerprintBindsSourceDefinitionAndWindowPolicy",
    "RecordingReplayApprovalBindsNativeOwnerAndAgeBeyondProfileFingerprint",
}
BODY_RECORDING_REPLAY_NATIVE_CASES = {
    "RecordingReplayNativeFullRowsNondefaultSequenceAndIndependentRootLoss",
    "RecordingReplayNativeReplacementRequiresNewExactApprovalAndSource",
}
BODY_RECORDING_CASES = {
    "BodyRecordingPreservesWholeSourceRowsReferencesAndExplicitLoss",
    "BodyRecordingExternalPinsAndOwnedBytesCannotBeReplaced",
    "BodyRecordingStrictManifestCannotSelfApproveCalibrationOrRuntime",
    "BodyRecordingMalformedFinalFrameAndSkeletonNeverExposePrefix",
    "BodyRecordingCastRejectsOverflowUnderflowScaleAndReflection",
    "BodyRecordingExactFractionUsesNearestEvenNotFpsOrAccumulatedTime",
    "BodyRecordingQuantizationIsAdditionalToIndependentClockUncertainty",
    "BodyRecordingClockIdentityBoundsAndSourceOriginCannotBeRewritten",
}
BODY_RECORDING_SOURCE_CASE = "SpatialBlend.Tests.FoundationBodyRecordingSourceTests.FullBodyRecordingsMatchIndependentSourceOracleForEverySelectedFrame"
BUFFERED_BODY_CASES = {
    "BufferedBodyFreezesCompleteFramesWithoutRestampOrRootInference",
    "BufferedBodyWaitsForWholeMappedAcquisitionInterval",
    "BufferedBodyEmptyGapsAndCompletionNeverHoldPastExpiry",
    "BufferedBodyDrainsLiveBacklogInOrderWithoutCoalescing",
    "BufferedBodyExpiredUndeliveredFrameFaultsWithoutSkipping",
    "BufferedBodyRejectsInvalidFinalRowBeforeReturningAnySource",
    "BufferedBodyRejectsUnboundedChangingAndThrowingCollections",
    "BufferedBodyWrongProfileAndRegressedClockCannotConsumeOrFaultOwner",
    "BufferedBodyWorkerCannotConsumeOrPoisonOwner",
    "BufferedBodyAcquisitionIsAtMostOnceNotNativeAcknowledgement",
}
BUFFERED_BODY_NATIVE_CASES = {"BufferedBodyNativeExactJoinAndIndependentRootLoss"}
PREPARATION_CASES = {
    "PreparationPreservesFullNativeSnapshotBodyAndIndependentSequenceDomains",
    "PreparationRejectsCopiedOrAlteredPayloadWithSameNativeHeader",
    "PreparationChecksSceneTransformBeforeAnotherSourceSample",
    "PreparationChecksPolicyBeforeAnotherSourceSample",
    "PreparationChecksCorrespondenceRemovalBeforeAnotherSourceSample",
    "PreparationRejectsNewNativeRevisionBeforeMailboxDrain",
    "PreparationRejectsIncompleteBodyDespiteReducedNativeGoals",
    "PreparationExpiresFromOriginalNativeReceiptWithoutUnityTick",
    "PreparationRetirementCannotWithdrawReplacement",
    "PreparationRejectsExpiredPinnedRichClockCalibrationBeforeReceiptAge",
    "PreparationWorkerThreadCannotReadOrPoisonOwnerState",
    "PreparationClearAndNewBodyCannotReviveOldCheckpoint",
    "PreparationPinsAgeBoundAndNeverExtendsBodyExpiry",
    "PreparationRejectsSourceClearWithoutOriginalSampleAndReset",
    "PreparationExplicitWithdrawalLeavesBodyAndOtherPreparationIntact",
    "PreparationInsideCallbackRejectsReentrantSceneChangeForLaterObserver",
}
RIG_OWNER_CASES = {
    "RigOwnerActualWorldBoundToleratesFloatCompositionOnly",
    "RigOwnerDoesNotRevokeItselfWhenFloatCompositionExceedsStrictGramBound",
    "RigOwnerAcceptsAStrictProposalWhoseFloatRebuildExceedsTheStrictGramBound",
}
RIG_OUTPUT_CASES = {
    "RigOutputCalibratesReferencePoseAndFingerprintsExactBinding",
    "RigOutputRejectsWrongCalibrationNamesTopologyAndScale",
    "RigOutputRejectsWritersWithoutDisablingAndAllowsStaticRenderGeometry",
    "RigOutputRegistryRejectsSameAndNestedHierarchyOwners",
    "RigOutputDestroyedRegistryOwnerRevokesOldHandles",
    "RigOutputParentEffectsConflictWithDescendantsInBothOrders",
    "RigOutputDisjointSiblingsAndLeafDofsHaveSeparateLeases",
    "RigOutputReservedWorldJointBlocksAncestorButNotSibling",
    "RigOutputBatchAppliesFullLocalPoseAndPreservesInputAndUnownedTransforms",
    "RigOutputSingleComposerCanOwnCoupledAncestorAndDescendant",
    "RigOutputRejectsUnownedLocalDofsWithoutDiscardingThem",
    "RigOutputMalformedFinalRowNeverPartiallyWritesOrConsumesSequence",
    "RigOutputRejectsComposedWorldOverflowBeforeAnyWrite",
    "RigOutputRefreshesBasisBetweenDisjointWritersAndRejectsForeignReadback",
    "RigOutputGenerationSequenceAndRetiredLeaseCannotAffectReplacement",
    "RigOutputDeadlineRevokesWithoutUnityTickOrResettingLastPose",
    "RigOutputRejectsUnboundedMalformedClaimsAndFreezesValidClaims",
    "RigOutputInvalidatedNativeSceneRejectsWholeBatchWithoutClearingBody",
    "RigOutputNewBodyRequiresNewPreparationWithoutExtendingLease",
    "RigOutputBodyClearOrClosedInputCannotAuthorizeWrites",
    "RigOutputChangedHierarchyConfigurationOrOriginRevokesWithoutRollback",
    "RigOutputPinsAncestorsEvenWhenWorldPoseIsPreserved",
    "RigOutputExternalPoseWriterIsDetectedButNeverOverwritten",
    "RigOutputWorkerCallsFailBeforeUnityAccessWithoutPoisoningOwner",
    "RigOutputReentrantClaimsCannotEscapeRetirementOrCollisionChecks",
    "RigOutputReentrantProposalCannotWriteAfterLeaseReplacement",
    "RigOutputReentrantBindCannotRetainClosedInput",
    "RigOutputUnissuedLeaseAndCopiedReadbackCannotForgeAuthority",
}


BODY_PREFLIGHT_CASES = {
    "BodySpecMaterializesOnlyMatchingAdmittedLayoutClockAndRevisions",
    "BodySpecFreezesDefinitionAndRejectsGeneratedOrMissingCalibration",
    "BodySpecChangingOrThrowingCollectionsRejectWithoutEscapingOrMutation",
    "RigPreflightChecksReferenceWithoutReservingOrRequiringARecordedBody",
    "RigPreflightRejectsBadTopologyScaleAndComponentsBeforeActivation",
    "RigPreflightWrongThreadRejectsBeforeTransformAccess",
}

RECEIVER_REEVALUATION_CASES = {
    "ReevaluationFeatureRejectsIncompatibleDescriptorAndEveryOffset",
    "ReevaluationJoinsLatestBodyWithoutNewSourceOrAutomaticActuation",
    "ReevaluationRejectsForeignRetiredAndWrongThreadOwners",
    "ReevaluationWaitsForAdmittedSourceAndNeverRevivesClearedBody",
}


RIG_CARRIER_CASES = {
    "RigCarrierCalibratesIndependentRootAndActualNestedBodyFrame",
    "RigCarrierRejectsWrongCalibrationAndMalformedOrWithdrawnEvidence",
    "RigCarrierMalformedApplyEvidenceRejectsWithoutThrowOrPartialWrite",
    "RigCarrierRejectsPelvisAliasDetachedMarkerAndUndeclaredTopology",
    "RigCarrierRequiresPlanarClaimAndReservesWholeSubtreeEffects",
    "RigCarrierReservedWorldJointBlocksCarrierWithoutTakeover",
    "RigCarrierPlanarBatchMovesActualBodyAndKeepsObservedInputSeparate",
    "RigCarrierRejectsVerticalOrYawChangesBeforeAnyBatchWrite",
    "RigCarrierCalibrationIsHistoricalButEachApplyNeedsFreshInput",
    "RigCarrierExternalWriterRevokesWithoutResettingOrClearingInput",
    "RigCarrierWrongThreadAndForgedReadbackCannotGainAuthority",
    "RigCarrierRetiredLeaseCannotPartiallyWriteOrReleaseReplacement",
    "RigCarrierOriginalLeaseDeadlineAndTargetWindowFenceAllSetters",
}


PLANAR_SUPPORT_CASES = {
    "PlanarSupportNegotiatesEveryManagedLayoutFieldAndRejectsMismatch",
    "PlanarSupportReadsNativeOrderedGeometryAndPreservesUnknownProvenance",
    "PlanarSupportRejectsMovedGeometryAndReadsFreshNativeInsteadOfCache",
    "PlanarSupportRejectsCopiedContributorOtherObjectAndOtherWorld",
    "PlanarSupportObjectReplacementCannotReviveOldRead",
    "PlanarSupportWorkerCallsFailBeforeUnityAndLeaveOwnerReadUsable",
    "PlanarSupportWithdrawnBodyOrCheckpointCannotSupplyGeometry",
    "PlanarSupportHeaderRejectsMalformedPlanesAndComparesExactDoubles",
}



def verify(path, graphics=False, rich=False, validity=False, receiver_input=False, reviewed_recording_ingress=False, receiver_approach=False, room_session=False, shared_local_binding=False, logical_content_binding=False, observed_collaborator=False, observed_collaborator_native=False, observed_collaborator_compatibility=False, body_recordings=False, source_evidence=False, interaction_hierarchy=False):
    if validity and not rich:
        raise ValueError("--validity requires --rich")
    if receiver_input and not (rich and validity):
        raise ValueError("--receiver-input requires --rich --validity")
    if reviewed_recording_ingress and not receiver_input:
        raise ValueError("--reviewed-recording-ingress requires --receiver-input")
    if receiver_approach and not receiver_input:
        raise ValueError("--receiver-approach requires --receiver-input")
    if body_recordings and not receiver_input:
        raise ValueError("--body-recordings requires --receiver-input")
    if logical_content_binding and not shared_local_binding:
        raise ValueError("--logical-content-binding requires --shared-local-binding")
    if observed_collaborator and not interaction_hierarchy:
        raise ValueError("--observed-collaborator requires --interaction-hierarchy")
    if observed_collaborator_native and not observed_collaborator:
        raise ValueError("--observed-collaborator-native requires --observed-collaborator")
    if observed_collaborator_compatibility and (observed_collaborator or observed_collaborator_native):
        raise ValueError("--observed-collaborator-compatibility cannot be combined with C1-positive flags")
    root = ET.parse(path).getroot()
    cases = root.findall(".//test-case")
    if root.get("result") != "Passed" or not cases or any(case.get("result") != "Passed" for case in cases):
        raise ValueError("Foundation test cases are absent, failed, or skipped")
    if int(root.get("total", "-1")) != len(cases) or int(root.get("passed", "-1")) != len(cases):
        raise ValueError("Foundation XML totals do not match executed passing cases")
    if int(root.get("failed", "0")) or int(root.get("skipped", "0")):
        raise ValueError("Foundation XML reports failed or skipped cases")
    fixtures = {
        suite.get("name")
        for suite in root.findall(".//test-suite")
        if suite.get("type") == "TestFixture" and suite.findall(".//test-case")
    }
    required = (REQUIRED_FIXTURES | (GRAPHICS_FIXTURES if graphics else set()) |
                (RICH_FIXTURES if rich else set()) |
                (VALIDITY_FIXTURES if validity else set()) |
                (RECEIVER_INPUT_FIXTURES if receiver_input else set()) |
                ({"ReviewedDatasetBodyRecordingIngressTests"} if reviewed_recording_ingress else set()) |
                (RECEIVER_APPROACH_FIXTURES if receiver_approach else set()) |
                ({"FoundationRoomSessionTests"} if room_session else set()) |
                ({"FoundationSharedToLocalBindingTests"} if shared_local_binding else set()) |
                ({"FoundationLogicalContentBindingTests"} if logical_content_binding else set()) |
                ({"FoundationObservedCollaboratorTests", "FoundationObservedCollaboratorAdmissionBarrierTests"} if observed_collaborator else set()) |
                ({"FoundationObservedCollaboratorNativeTests"} if observed_collaborator_native else set()) |
                ({"FoundationObservedCollaboratorCompatibilityTests"} if observed_collaborator_compatibility else set()) |
                ({"FoundationSourceInteractionTests"} if source_evidence else set()) |
                ({"FoundationInteractionHierarchyTests"} if interaction_hierarchy else set()) |
                ({"FoundationBodyRecordingSourceTests"} if body_recordings else set()))
    missing = required - fixtures
    if missing:
        raise ValueError("Missing required Foundation fixtures: " + ", ".join(sorted(missing)))
    required_motion = {"SpatialBlend.Tests.FoundationCommittedMotionTests." + name for name in COMMITTED_MOTION_CASES}
    missing_motion = required_motion - {case.get("fullname") for case in cases}
    if missing_motion:
        raise ValueError("Missing required committed motion cases: " + ", ".join(sorted(missing_motion)))
    if source_evidence:
        required_source = {"SpatialBlend.Tests.FoundationSourceInteractionTests." + name for name in SOURCE_EVIDENCE_CASES}
        missing_source = required_source - {case.get("fullname") for case in cases}
        if missing_source:
            raise ValueError("Missing required source evidence cases: " + ", ".join(sorted(missing_source)))
    if interaction_hierarchy:
        required_hierarchy = {"SpatialBlend.Tests.FoundationInteractionHierarchyTests." + name for name in INTERACTION_HIERARCHY_CASES}
        missing_hierarchy = required_hierarchy - {case.get("fullname") for case in cases}
        if missing_hierarchy:
            raise ValueError("Missing required interaction hierarchy cases: " + ", ".join(sorted(missing_hierarchy)))
    if body_recordings and BODY_RECORDING_SOURCE_CASE not in {case.get("fullname") for case in cases}:
        raise ValueError("Missing required independent body recording source case: " + BODY_RECORDING_SOURCE_CASE)
    if receiver_approach:
        required_approach_receipts = {"SpatialBlend.Tests.FoundationReceiverApproachTests." + name for name in RECEIVER_APPROACH_CASES}
        missing_approach_receipts = required_approach_receipts - {case.get("fullname") for case in cases}
        if missing_approach_receipts:
            raise ValueError("Missing required receiver approach receipt cases: " + ", ".join(sorted(missing_approach_receipts)))
    if reviewed_recording_ingress:
        required_reviewed_recording = {
            "SpatialBlend.Tests.ReviewedDatasetBodyRecordingIngressTests." + name
            for name in REVIEWED_RECORDING_INGRESS_CASES
        }
        missing_reviewed_recording = required_reviewed_recording - {
            case.get("fullname") for case in cases
        }
        if missing_reviewed_recording:
            raise ValueError("Missing required reviewed-recording ingress cases: " +
                             ", ".join(sorted(missing_reviewed_recording)))
    if room_session:
        required_room_session = {"SpatialBlend.Tests.FoundationRoomSessionTests." + name for name in ROOM_SESSION_CASES}
        missing_room_session = required_room_session - {case.get("fullname") for case in cases}
        if missing_room_session:
            raise ValueError("Missing required room-session cases: " + ", ".join(sorted(missing_room_session)))
    if shared_local_binding:
        required_shared_binding = {"SpatialBlend.Tests.FoundationSharedToLocalBindingTests." + name
                                   for name in SHARED_LOCAL_BINDING_CASES}
        missing_shared_binding = required_shared_binding - {case.get("fullname") for case in cases}
        if missing_shared_binding:
            raise ValueError("Missing required shared-local-binding cases: " +
                             ", ".join(sorted(missing_shared_binding)))
    if logical_content_binding:
        required_logical_content = {"SpatialBlend.Tests.FoundationLogicalContentBindingTests." + name
                                    for name in LOGICAL_CONTENT_BINDING_CASES}
        missing_logical_content = required_logical_content - {case.get("fullname") for case in cases}
        if missing_logical_content:
            raise ValueError("Missing required logical-content-binding cases: " +
                             ", ".join(sorted(missing_logical_content)))
    if observed_collaborator:
        required_observed_collaborator = {
            "SpatialBlend.Tests.FoundationObservedCollaboratorTests." + name
            for name in OBSERVED_COLLABORATOR_CASES
        }
        missing_observed_collaborator = required_observed_collaborator - {
            case.get("fullname") for case in cases
        }
        if missing_observed_collaborator:
            raise ValueError("Missing required observed collaborator cases: " +
                             ", ".join(sorted(missing_observed_collaborator)))
        required_observed_collaborator_barrier = {
            "SpatialBlend.Tests.FoundationObservedCollaboratorAdmissionBarrierTests." + name
            for name in OBSERVED_COLLABORATOR_BARRIER_CASES
        }
        missing_observed_collaborator_barrier = required_observed_collaborator_barrier - {
            case.get("fullname") for case in cases
        }
        if missing_observed_collaborator_barrier:
            raise ValueError("Missing required observed collaborator barrier cases: " +
                             ", ".join(sorted(missing_observed_collaborator_barrier)))
    if observed_collaborator_native:
        required_observed_collaborator_native = {
            "SpatialBlend.Tests.FoundationObservedCollaboratorNativeTests." + name
            for name in OBSERVED_COLLABORATOR_NATIVE_CASES
        }
        missing_observed_collaborator_native = required_observed_collaborator_native - {
            case.get("fullname") for case in cases
        }
        if missing_observed_collaborator_native:
            raise ValueError("Missing required observed-collaborator native cases: " +
                             ", ".join(sorted(missing_observed_collaborator_native)))
    if observed_collaborator_compatibility:
        compatibility_case = (
            "SpatialBlend.Tests.FoundationObservedCollaboratorCompatibilityTests."
            "MissingOptionalC1SymbolsLeaveLegacyObservationFramesUsable"
        )
        if compatibility_case not in {case.get("fullname") for case in cases}:
            raise ValueError("Missing required pre-C1 compatibility case: " + compatibility_case)
    if receiver_input:
        required_replay = {"SpatialBlend.Tests.FoundationBodyRecordingReplayTests." + name for name in BODY_RECORDING_REPLAY_CASES}
        required_replay |= {"SpatialBlend.Tests.FoundationReceiverBodyNativeTests." + name for name in BODY_RECORDING_REPLAY_NATIVE_CASES}
        missing_replay = required_replay - {case.get("fullname") for case in cases}
        if missing_replay:
            raise ValueError("Missing required body recording replay cases: " + ", ".join(sorted(missing_replay)))
        required_recording = {"SpatialBlend.Tests.FoundationBodyRecordingTests." + name for name in BODY_RECORDING_CASES}
        missing_recording = required_recording - {case.get("fullname") for case in cases}
        if missing_recording:
            raise ValueError("Missing required body recording cases: " + ", ".join(sorted(missing_recording)))
        required_buffer = {"SpatialBlend.Tests.FoundationBufferedBodySourceTests." + name for name in BUFFERED_BODY_CASES}
        required_buffer |= {"SpatialBlend.Tests.FoundationReceiverBodyNativeTests." + name for name in BUFFERED_BODY_NATIVE_CASES}
        missing_buffer = required_buffer - {case.get("fullname") for case in cases}
        if missing_buffer:
            raise ValueError("Missing required buffered body cases: " + ", ".join(sorted(missing_buffer)))
        for label, names in (("body preparation", PREPARATION_CASES), ("rig output", RIG_OUTPUT_CASES),
                             ("rig carrier", RIG_CARRIER_CASES), ("body preflight", BODY_PREFLIGHT_CASES),
                             ("receiver reevaluation", RECEIVER_REEVALUATION_CASES),
                             ("planar support", PLANAR_SUPPORT_CASES),
                             ("rig owner", RIG_OWNER_CASES)):
            required_cases = {"SpatialBlend.Tests.FoundationReceiverBodyNativeTests." + name for name in names}
            missing_cases = required_cases - {case.get("fullname") for case in cases}
            if missing_cases:
                raise ValueError("Missing required " + label + " cases: " + ", ".join(sorted(missing_cases)))
    return len(cases)


if __name__ == "__main__":
    try:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("results")
        parser.add_argument("--graphics", action="store_true")
        parser.add_argument("--rich", action="store_true")
        parser.add_argument("--validity", action="store_true")
        parser.add_argument("--receiver-input", action="store_true")
        parser.add_argument("--reviewed-recording-ingress", action="store_true")
        parser.add_argument("--receiver-approach", action="store_true")
        parser.add_argument("--room-session", action="store_true")
        parser.add_argument("--shared-local-binding", action="store_true")
        parser.add_argument("--logical-content-binding", action="store_true")
        parser.add_argument("--observed-collaborator", action="store_true")
        parser.add_argument("--observed-collaborator-native", action="store_true")
        parser.add_argument("--observed-collaborator-compatibility", action="store_true")
        parser.add_argument("--body-recordings", action="store_true")
        parser.add_argument("--source-evidence", action="store_true")
        parser.add_argument("--interaction-hierarchy", action="store_true")
        args = parser.parse_args()
        count = verify(args.results, graphics=args.graphics, rich=args.rich,
                       validity=args.validity, receiver_input=args.receiver_input, reviewed_recording_ingress=args.reviewed_recording_ingress, receiver_approach=args.receiver_approach, room_session=args.room_session, shared_local_binding=args.shared_local_binding, logical_content_binding=args.logical_content_binding, observed_collaborator=args.observed_collaborator, observed_collaborator_native=args.observed_collaborator_native, observed_collaborator_compatibility=args.observed_collaborator_compatibility, body_recordings=args.body_recordings,
                       source_evidence=args.source_evidence, interaction_hierarchy=args.interaction_hierarchy)
        print(f"FOUNDATION_COUNTS=passed:{count} failed:0 skipped:0")
    except (OSError, ValueError, ET.ParseError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
