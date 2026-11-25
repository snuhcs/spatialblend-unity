# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""stdlib-only regression checks; no Unity process is launched."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent
VERIFY = ROOT / "verify-freeform-results.py"
NAMES = ("WhiteboardRestorationTests", "LocalSessionConfigTests", "WhiteboardSurfaceChartTests", "ChessboardPrefabContractTests",
         "FreeformSceneRestorationTests", "FreeformArrivalContractTests",
         "EntityLifecycleContractTests", "ManagerLifecycleContractTests",
         "RemovalCallbackTests", "ManagerConnectionContractTests",
         "ManagerContainmentContractTests", "SourceHeadAnchorTests", "ContainmentTransferBindingTests",
         "ContainmentInteractionMapperTests", "ContinuousCompensationTests",
         "FusionConnectionContractTests", "FusionSessionBrowserTests", "FreeformSpectatorTests", "FreeformSpectatorSessionTests", "FreeformSpectatorSceneIdentityTests", "FreeformSpectatorLauncherTests",
         "FreeformMatchmakingContractTests", "FreeformVoiceStackTests", "RequiredCredentialsTests", "FreeformUIContractTests",
         "TutorialFlowTests", "TutorialRunnerTests", "TutorialNarratorTests", "TutorialDoneMarkTests",
         "TutorialScanTests", "TutorialHookTests", "TutorialUiTests",
         "SceneGeometryContractTests", "ReplicaLifecycleComponentTests",
         "SpatialHierarchyAndAnchorPoseContractTests", "SpatialAuthoringContractTests",
         "SpatialAuthoringPrefabContractTests",
         "AnchorPoseInputContractTests",
         "DynamicSceneGeometryContractTests", "NativeContentLoadContractTests",
         "FoundationSceneGeometryTests", "NavMeshWalkableExtractorTests",
         "WalkablePlacementSnapTests", "AffordanceVolumeMigrationTests",
         "WalkableRegistrationContractTests",
         "FreeformSceneRoomGeometryTests",
         "ArticulatedTargetControllerTests", "SeatRegistrationTests", "RoomShareTests", "RecordedAnchorPoseReplayTests",
         "FoundationCoordinateContractTests", "SpatialBlendObjectContractTests",
         "FoundationFreeformContentTests", "AnchorTargetLeaseTests",
         "SbRetargetAnchorTargetBridgeTests", "FreeformSceneInputTests",
         "FreeformSceneLocalBackendTests", "AnchorPoseReplayLifetimeTests",
         "FoundationAvatarRetargetEntityTests", "FreeformSceneActorDefinitionTests",
         "SbLocalPresetAvatarTests", "SbArticulatedBodyTrackingContextTests",
         "SbArticulatedInputBindingTests", "ReceiverBodySdkProfileTests",
         "RemoteAvatarVisibilityPolicyTests", "RemoteAvatarControllerTests",
         "NetworkedAvatarControllerPrefabTests", "AvatarStateUpdateFilterTests",
         "HandTransformDataTests", "SbPermissionManagerTests", "CaptureCameraAndStyleTests",
         "SbPermissionSceneHookTests", "DeclaredRegionZoneTests", "ZoneBasedDeclaredRegionContractTests",
         "AvatarAlphaFadeShaderTests", "EmbodiedDriverTests", "SceneFrameRulesTests", "FoundationSceneStatusTests", "EmbodiedSingleOwnerTests", "FoundationEmbodiedPortBindingTests",
         "FreeformV1ParityTests",
         "NativeEntityReadyToShareTrackerTests", "FreeformContentWriterContractTests",
         "SceneAnchorLinkContractTests",
         "ScanSessionConfigTests", "ScanRequestBuilderTests", "ScanResponseParserTests",
         "ScanPendingTrackerTests", "ScanServiceTests", "ScanPromptTests", "ScanFlowTests", "SessionLibraryLabelTests", "ScanCardTests", "ScanAimingTests", "ScanBoxRegionTests", "SessionManagerConfigTests",
         "BoxAnnotationAuthoringContractTests", "PresentationPortContractTests",
         "ReconstructedObjectFitTests", "BoxAnnotationHeightTests",
         "AvatarLoadDecisionTests", "ObjectPlacementSnapTests", "MrukPrefabRoomTests",
         "BoxAnnotationPointSelectionReleaseTests", "PlacementModeReleaseTests", "EnvironmentSurfaceModeTests",
         "EntityLayerTests", "ChessboardTableSpawnTests", "DeclaredRegionOutlineModeTests",
         "SceneAnchorVisibilityToggleTests", "DarkThemePanelPrefabTests", "UiConformanceGuardTests", "UiDesignSystemTests", "AvatarSendThresholdPrefabTests",
         "ReconstructedPreviewTests", "ReceivedScanPlacementTests", "OwnScanStaysPutTests", "RemoteStagingTests", "ManipulationPopupDeleteAndGizmoTests",
         "RegionPolygonRefusalTests", "FreeformVoiceStackTests",
         "ManipulationPopupDeleteConfirmTests", "RoomModeResetTests", "BoxUndoHintTests", "ReceivedCopyMenuTests", "SpectatorOverlayTests", "SharedBoxLifecycleTests", "DepthOcclusionTests")


class VerifyFreeformResultsTests(unittest.TestCase):
    def run_xml(self, xml):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.xml"
            path.write_text(xml)
            return subprocess.run([sys.executable, str(VERIFY), str(path)], text=True, capture_output=True)

    def test_valid_required_suites_pass(self):
        cases = "".join(f'<test-case fullname="X.{name}.Case" result="Passed" />' for name in NAMES)
        result = self.run_xml(f'<test-run result="Passed">{cases}</test-run>')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"FREEFORM_COUNTS=passed:{len(NAMES)} failed:0 skipped:0", result.stdout)

    def test_every_required_fixture_is_independently_required(self):
        for missing in NAMES:
            with self.subTest(missing=missing):
                cases = "".join(
                    f'<test-case fullname="X.{name}.Case" result="Passed" />'
                    for name in NAMES if name != missing
                )
                result = self.run_xml(f'<test-run result="Passed">{cases}</test-run>')
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(missing, result.stderr)

    def test_similarly_named_fixture_cannot_satisfy_discovery(self):
        cases = "".join(
            f'<test-case fullname="X.Not{name}.Case" result="Passed" />' for name in NAMES
        )
        self.assertNotEqual(self.run_xml(f'<test-run result="Passed">{cases}</test-run>').returncode, 0)

    def test_zero_missing_skipped_and_failed_are_refused(self):
        passing_cases = "".join(
            f'<test-case fullname="X.{name}.Case" result="Passed" />'
            for name in NAMES
        )
        skipped_cases = passing_cases.replace(
            'X.WhiteboardRestorationTests.Case" result="Passed"',
            'X.WhiteboardRestorationTests.Case" result="Skipped"',
        )
        failed_cases = passing_cases.replace(
            'X.WhiteboardRestorationTests.Case" result="Passed"',
            'X.WhiteboardRestorationTests.Case" result="Failed"',
        )
        for xml in (
            '<test-run result="Passed" />',
            '<test-run result="Passed"><test-case '
            'fullname="X.WhiteboardRestorationTests.Case" result="Passed" /></test-run>',
            f'<test-run result="Passed">{skipped_cases}</test-run>',
            f'<test-run result="Failed">{failed_cases}</test-run>',
        ):
            self.assertNotEqual(self.run_xml(xml).returncode, 0)

    def test_documented_parity_ignores_are_allowed(self):
        cases = "".join(f'<test-case fullname="X.{name}.Case" result="Passed" />' for name in NAMES)
        for method in (
            "A6_FullBodyOsMotion_IsOutOfParityScope",
            "P2_WindowsStandalone_IsOutOfScope",
            "T46_DeviceAcceptance_IsOwnerRun",
        ):
            cases += (
                f'<test-case fullname="SpatialBlend.Tests.FreeformV1ParityTests.{method}" '
                'result="Skipped" />'
            )
        result = self.run_xml(f'<test-run result="Passed">{cases}</test-run>')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"FREEFORM_COUNTS=passed:{len(NAMES)} failed:0 skipped:3", result.stdout)

    def test_ignored_root_is_accepted_only_when_every_case_is_classified(self):
        cases = "".join(f'<test-case fullname="X.{name}.Case" result="Passed" />' for name in NAMES)
        ok = cases + ('<test-case fullname="SpatialBlend.Tests.FreeformV1ParityTests.'
                      'P2_WindowsStandalone_IsOutOfScope" result="Skipped" />')
        result = self.run_xml(f'<test-run result="Skipped:Ignored">{ok}</test-run>')
        self.assertEqual(result.returncode, 0, result.stderr)
        bad = cases + ('<test-case fullname="SpatialBlend.Tests.FreeformV1ParityTests.'
                       'N1_SharedFusionSessionCanBeStarted" result="Skipped" />')
        self.assertNotEqual(self.run_xml(f'<test-run result="Skipped:Ignored">{bad}</test-run>').returncode, 0)

    def test_explicit_parity_failures_are_refused(self):
        # No Explicit tolerance. A failed Explicit parity test fails the gate.
        cases = "".join(f'<test-case fullname="X.{name}.Case" result="Passed" />' for name in NAMES)
        cases += (
            '<test-case fullname="SpatialBlend.Tests.FreeformV1ParityTests.A1_LocalPresetAvatarUiIsRestored" '
            'result="Failed" runstate="Explicit" />'
        )
        result = self.run_xml(f'<test-run result="Failed">{cases}</test-run>')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("A1_LocalPresetAvatarUiIsRestored", result.stderr)

    def test_explicit_failure_outside_parity_is_refused(self):
        cases = "".join(f'<test-case fullname="X.{name}.Case" result="Passed" />' for name in NAMES)
        cases += (
            '<test-case fullname="SpatialBlend.Tests.FusionConnectionContractTests.SomeCase" '
            'result="Failed" runstate="Explicit" />'
        )
        result = self.run_xml(f'<test-run result="Failed">{cases}</test-run>')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("FusionConnectionContractTests.SomeCase", result.stderr)

    def test_other_parity_skips_are_refused(self):
        cases = "".join(f'<test-case fullname="X.{name}.Case" result="Passed" />' for name in NAMES)
        cases += (
            '<test-case fullname="SpatialBlend.Tests.FreeformV1ParityTests.N1_SharedFusionSessionCanBeStarted" '
            'result="Skipped" />'
        )
        result = self.run_xml(f'<test-run result="Passed">{cases}</test-run>')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("N1_SharedFusionSessionCanBeStarted", result.stderr)

    def test_missing_fixture_xml_is_refused(self):
        result = subprocess.run([sys.executable, str(VERIFY), "/definitely/missing/results.xml"], text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
