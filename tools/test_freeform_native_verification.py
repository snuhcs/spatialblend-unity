# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Stdlib tests for native-gate discovery/provenance; never starts Unity."""
import importlib.util
from html import escape
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent / "freeform-native-verification"
NAMES = (
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
    "AScannedObjectRegisteredWithOrWithoutPropagateReachesThePeerWithItsContent(True)",
    "AScannedObjectRegisteredWithOrWithoutPropagateReachesThePeerWithItsContent(False)",
)


class NativeGateResultsTests(unittest.TestCase):
    def run_xml(self, missing=None, bad=None, prefix="SpatialBlend.Tests.NativeManagerIntegrationTests.",
                require_graphics=False, graphics_result=None, graphics_prefix="SpatialBlend.Tests.",
                lifecycle_result="Passed", lifecycle_prefix="SpatialBlend.Tests.",
                require_sdk=False, sdk_missing=None, sdk_result="Passed",
                require_source_head=False, source_head_missing=None, source_head_result="Passed",
                failed_name=None, root_result="Passed"):
        def outcome(name):
            if name == failed_name:
                return "Failed"
            return bad or "Passed"

        cases = "".join(
            f'<test-case fullname="{prefix}{name}" result="{outcome(name)}" />'
            for name in NAMES if name != missing
        )
        if lifecycle_result is not None:
            cases += (
                f'<test-case fullname="{lifecycle_prefix}SbLocalPresetAvatarPlayModeTests.'
                'FailedStagedCloneNeverActivatesLegacyAvatarInPlayMode" '
                f'result="{lifecycle_result}" />'
            )
        if graphics_result is not None:
            cases += (
                f'<test-case fullname="{graphics_prefix}NativeFreeformSceneGraphicsTests.'
                'OriginalFreeformScene_LocalBackend_RendersAuthoredWhiteboardAndChess" '
                f'result="{graphics_result}" />'
            )
        if require_sdk and sdk_result is not None:
            spec = importlib.util.spec_from_file_location("native_gate_verify", ROOT / "verify-results.py")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            for name in module.SDK_CASES:
                if name != sdk_missing:
                    cases += (f'<test-case fullname="SpatialBlend.Tests.ReceiverBodySdkNativeTests.{escape(name, quote=True)}" '
                              f'result="{sdk_result}" />')
        if require_source_head and source_head_result is not None:
            spec = importlib.util.spec_from_file_location("native_gate_verify_head", ROOT / "verify-results.py")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            for name in module.SOURCE_HEAD_CASES:
                if name != source_head_missing:
                    cases += (f'<test-case fullname="SpatialBlend.Tests.SourceHeadAnchorNativeTests.{escape(name, quote=True)}" '
                              f'result="{source_head_result}" />')
        with tempfile.TemporaryDirectory() as directory:
            xml = Path(directory) / "results.xml"
            xml.write_text(f'<test-run result="{root_result}">{cases}</test-run>')
            command = [sys.executable, str(ROOT / "verify-results.py"), str(xml)]
            if require_graphics:
                command.append("--graphics")
            if require_sdk:
                command.append("--receiver-body-sdk")
            if require_source_head:
                command.append("--source-head")
            return subprocess.run(command,
                                  capture_output=True, text=True)

    def test_all_required_native_cases_pass(self):
        result = self.run_xml()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"passed:{len(NAMES) + 1} failed:0 skipped:0", result.stdout)

    def test_each_case_is_required(self):
        for name in NAMES:
            with self.subTest(name=name):
                self.assertNotEqual(self.run_xml(missing=name).returncode, 0)

    def test_similarly_named_suite_is_not_native_coverage(self):
        self.assertNotEqual(self.run_xml(prefix="NotNative.").returncode, 0)

    def test_loopback_reconnect_failure_is_refused(self):
        result = self.run_xml(
            failed_name="RealManagersConnectDisconnectAndReconnectWithoutPhotonRunner",
            root_result="Failed")
        self.assertNotEqual(result.returncode, 0)

    def test_other_single_native_failure_is_refused(self):
        result = self.run_xml(
            failed_name="WalkableFourByFourWithTwoByTwoHoleRegistersOneObstacleChildAndReregisterRemovesIt",
            root_result="Failed")
        self.assertNotEqual(result.returncode, 0)

    def test_skipped_or_failed_native_execution_is_not_a_pass(self):
        for bad in ("Skipped", "Failed"):
            with self.subTest(result=bad):
                self.assertNotEqual(self.run_xml(bad=bad).returncode, 0)

    def test_graphics_requires_exact_executed_passing_case(self):
        passing = self.run_xml(require_graphics=True, graphics_result="Passed")
        self.assertEqual(passing.returncode, 0, passing.stderr)
        self.assertIn(f"passed:{len(NAMES) + 2} failed:0 skipped:0", passing.stdout)
        for result in (None, "Skipped", "Failed"):
            with self.subTest(result=result):
                self.assertNotEqual(self.run_xml(require_graphics=True,
                                                graphics_result=result).returncode, 0)
        self.assertNotEqual(self.run_xml(require_graphics=True, graphics_result="Passed",
                                        graphics_prefix="NotNative.").returncode, 0)

    def test_actual_playmode_lifecycle_case_is_required_and_must_pass(self):
        for result in (None, "Skipped", "Failed"):
            with self.subTest(result=result):
                self.assertNotEqual(self.run_xml(lifecycle_result=result).returncode, 0)
        self.assertNotEqual(self.run_xml(lifecycle_prefix="NotNative.").returncode, 0)

    def test_receiver_body_sdk_requires_executed_complete_matrix(self):
        passing = self.run_xml(require_sdk=True)
        self.assertEqual(passing.returncode, 0, passing.stderr)
        for result in (None, "Skipped", "Failed"):
            self.assertNotEqual(self.run_xml(require_sdk=True, sdk_result=result).returncode, 0)
        for missing in (
            "OneBodyFeedsRealNativeFkAndActualSdkCallbacksWithNoLegacyProviderOrActorWrite",
            'ManagerLifecycleWithdrawsNativeAndSdkAndNeverFallsBack("disable")',
            "ExplicitJointOrRootLossReachesNativeAndWithdrawsWholeSdkPose(False)",
            "ExplicitJointOrRootLossReachesNativeAndWithdrawsWholeSdkPose(True)",
        ):
            self.assertNotEqual(self.run_xml(require_sdk=True, sdk_missing=missing).returncode, 0)


    def test_source_head_requires_executed_cases_only_when_asked(self):
        passing = self.run_xml(require_source_head=True)
        self.assertEqual(passing.returncode, 0, passing.stderr)
        for result in (None, "Skipped", "Failed"):
            self.assertNotEqual(self.run_xml(require_source_head=True, source_head_result=result).returncode, 0)
        self.assertNotEqual(self.run_xml(require_source_head=True,
                                         source_head_missing="ASeatedSourceBehindACounterDoesNotSeeThePersonAndAStandingOneDoes"
                                         ).returncode, 0)
        self.assertEqual(self.run_xml(source_head_result=None).returncode, 0, "not required without --source-head")


class NativeGateProvenanceTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("native_gate_provenance", ROOT / "write-provenance.py")
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.repo, self.project, self.native = root / "repo", root / "copy", root / "selected.dylib"
        inputs = {
            "Assets/SpatialBlend/Test.cs": "runtime source",
            "Assets/SpatialBlend/Test.cs.meta": "guid: stable",
            "Assets/SpatialBlend/Motion/Foundation.asmref": '{"reference":"Foundation"}',
            "Packages/manifest.json": "{}",
            "Packages/packages-lock.json": "{}",
            "ProjectSettings/TagManager.asset": "tags: Avatar",
            "Assets/Plugins/OSX/libspatialblend.dylib.meta": "plugin importer",
        }
        for relative in self.module.FREEFORM_AUTHORED_INPUTS:
            inputs[relative] = "authored content: " + relative
        for relative, text in inputs.items():
            for directory in (self.repo, self.project):
                self.write(directory / relative, text)
        self.write(self.native, "new native")
        self.write(self.project / "Assets/Plugins/OSX/libspatialblend.dylib", "new native")
        self.write(self.project / "PACKAGE_CLOSURE_SHA256.json", "package closure")
        for fixture in self.module.INJECTED_NATIVE_FIXTURES:
            self.write(self.repo / "tools/freeform-native-verification" / fixture, "actual fixture")
            self.write(self.project / "Assets/SpatialBlend/Framework/Test/Restoration" / fixture,
                       "actual fixture")

    @staticmethod
    def write(path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def test_frozen_manifest_covers_copied_inputs_and_selected_native(self):
        self.module.main(self.repo, self.project, self.native)
        text = (self.project / "INPUT_SHA256.txt").read_text()
        self.assertIn("ProjectSettings/TagManager.asset", text)
        self.assertIn("libspatialblend.dylib.meta", text)
        self.assertIn("Motion/Foundation.asmref", text)
        self.assertIn("NativeManagerIntegrationTests.cs", text)
        self.assertIn("NativeFreeformSceneGraphicsTests.cs", text)
        for relative in self.module.FREEFORM_AUTHORED_INPUTS:
            self.assertIn(relative, text)
        self.assertIn(self.module.digest(self.native), text)

    def test_changed_copied_source_is_rejected_before_execution(self):
        self.write(self.project / "Assets/SpatialBlend/Test.cs", "not the current source")
        with self.assertRaisesRegex(SystemExit, "Source changed"):
            self.module.main(self.repo, self.project, self.native)

    def test_changed_authored_scene_or_prefab_is_rejected_before_execution(self):
        for relative in self.module.FREEFORM_AUTHORED_INPUTS:
            with self.subTest(asset=relative):
                self.write(self.project / relative, "different authored content")
                with self.assertRaisesRegex(SystemExit, "Source changed"):
                    self.module.main(self.repo, self.project, self.native)
                self.write(self.project / relative, "authored content: " + relative)

    def test_substituted_native_is_rejected_before_execution(self):
        self.write(self.project / "Assets/Plugins/OSX/libspatialblend.dylib", "wrong binary")
        with self.assertRaisesRegex(SystemExit, "Native copy differs"):
            self.module.main(self.repo, self.project, self.native)

    def test_substituted_graphics_fixture_is_rejected_before_execution(self):
        self.write(self.project / "Assets/SpatialBlend/Framework/Test/Restoration/NativeFreeformSceneGraphicsTests.cs",
                   "not the selected fixture")
        with self.assertRaisesRegex(SystemExit, "Native fixture copy differs"):
            self.module.main(self.repo, self.project, self.native)

    def test_missing_package_preflight_receipt_is_rejected_before_execution(self):
        (self.project / "PACKAGE_CLOSURE_SHA256.json").unlink()
        with self.assertRaisesRegex(SystemExit, "Offline package preflight"):
            self.module.main(self.repo, self.project, self.native)


if __name__ == "__main__":
    unittest.main()
