# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""No Unity process is started by these verifier regression tests."""
import subprocess
import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

VERIFY = Path(__file__).with_name("verify-foundation-results.py")
SPEC = importlib.util.spec_from_file_location("foundation_verifier", VERIFY)
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)
NAMES = (
    "AnchorTargetControllerTests", "ArticulatedTargetControllerTests",
    "FoundationCoordinateContractTests", "RecordedAnchorPoseReplayTests",
    "FoundationNativeClockMappingTests",
    "FoundationSceneGeometryTests",
    "NavMeshWalkableExtractorTests",
    "WalkablePlacementSnapTests",
    "AnchorPoseReplayLifetimeTests",
    "SpatialBlendObjectContractTests", "SpatialBlendObjectNativeTests",
    "NativeSupportIntegrationTests",
    "FoundationActorNativeLifecycleTests", "FoundationFreeformContentTests",
    "FoundationFreeformContentNativeTests",
    "AnchorTargetLeaseTests", "FreeformLocalAppTests",
    "FreeformLocalAppPlayModeTests", "FoundationActorMotionInputTests",
    "FoundationRelationEventTests", "ContainmentInteractionMapperTests",
    "FoundationCommittedMotionTests",
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
)
GRAPHICS = ("FreeformLocalAppGraphicsTests",)
RICH = ("FoundationRichRealizationTests", "FoundationRichRealizationNativeTests",
        "FoundationRichMailboxTests", "FoundationRichSurfaceNativeTests",
        "FoundationRichCallbackDispatchTests", "FoundationRichAdapterTests")
VALIDITY = ("FoundationRichValidityTests",)
RECEIVER_INPUT = ("FoundationReceiverInputFrameTests", "FoundationReceiverInputNativeTests",
                  "FoundationReceiverInputRuntimeTests", "FoundationReceiverBodyFrameTests",
                  "FoundationReceiverBodyNativeTests", "FoundationReceiverBodyClockTests",
                  "FoundationBufferedBodySourceTests", "FoundationBodyRecordingTests", "FoundationBodyRecordingReplayTests",
                  "FoundationEmbodimentDispatcherTests")


class FoundationResultVerifierTests(unittest.TestCase):
    def run_xml(self, text, graphics=False, rich=False, validity=False, receiver_input=False, reviewed_recording_ingress=False, receiver_approach=False, shared_local_binding=False, logical_content_binding=False, observed_collaborator=False, observed_collaborator_native=False, observed_collaborator_compatibility=False, body_recordings=False, source_evidence=False, interaction_hierarchy=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.xml"
            path.write_text(text)
            command = [sys.executable, str(VERIFY), str(path)]
            if graphics:
                command.append("--graphics")
            if rich:
                command.append("--rich")
            if validity:
                command.append("--validity")
            if receiver_input:
                command.append("--receiver-input")
            if reviewed_recording_ingress:
                command.append("--reviewed-recording-ingress")
            if receiver_approach:
                command.append("--receiver-approach")
            if shared_local_binding:
                command.append("--shared-local-binding")
            if logical_content_binding:
                command.append("--logical-content-binding")
            if observed_collaborator:
                command.append("--observed-collaborator")
            if observed_collaborator_native:
                command.append("--observed-collaborator-native")
            if observed_collaborator_compatibility:
                command.append("--observed-collaborator-compatibility")
            if body_recordings:
                command.append("--body-recordings")
            if source_evidence:
                command.append("--source-evidence")
            if interaction_hierarchy:
                command.append("--interaction-hierarchy")
            return subprocess.run(command,
                                  capture_output=True, text=True)

    @staticmethod
    def xml(names=NAMES, preparation_cases=None, rig_output_cases=None, planar_support_cases=None, rig_owner_cases=None,
            rig_carrier_cases=None, body_preflight_cases=None, receiver_reevaluation_cases=None,
            buffered_body_cases=None, buffered_body_native_cases=None, body_recording_cases=None,
            body_replay_cases=None, body_replay_native_cases=None, committed_motion_cases=None, source_evidence_cases=None,
            hierarchy_cases=None, receiver_approach_cases=None,
            shared_local_binding_cases=None, logical_content_binding_cases=None,
            observed_collaborator_cases=None, observed_collaborator_barrier_cases=None, observed_collaborator_native_cases=None,
            reviewed_recording_ingress_cases=None):
        hierarchy_cases = VERIFIER.INTERACTION_HIERARCHY_CASES if hierarchy_cases is None else hierarchy_cases
        source_evidence_cases = VERIFIER.SOURCE_EVIDENCE_CASES if source_evidence_cases is None else source_evidence_cases
        receiver_approach_cases = VERIFIER.RECEIVER_APPROACH_CASES if receiver_approach_cases is None else receiver_approach_cases
        reviewed_recording_ingress_cases = (VERIFIER.REVIEWED_RECORDING_INGRESS_CASES
                                            if reviewed_recording_ingress_cases is None
                                            else reviewed_recording_ingress_cases)
        shared_local_binding_cases = (VERIFIER.SHARED_LOCAL_BINDING_CASES if shared_local_binding_cases is None
                                      else shared_local_binding_cases)
        logical_content_binding_cases = (VERIFIER.LOGICAL_CONTENT_BINDING_CASES if logical_content_binding_cases is None
                                         else logical_content_binding_cases)
        observed_collaborator_cases = (VERIFIER.OBSERVED_COLLABORATOR_CASES if observed_collaborator_cases is None
                                       else observed_collaborator_cases)
        observed_collaborator_native_cases = (VERIFIER.OBSERVED_COLLABORATOR_NATIVE_CASES if observed_collaborator_native_cases is None
                                              else observed_collaborator_native_cases)
        observed_collaborator_barrier_cases = (VERIFIER.OBSERVED_COLLABORATOR_BARRIER_CASES if observed_collaborator_barrier_cases is None
                                               else observed_collaborator_barrier_cases)
        committed_motion_cases = VERIFIER.COMMITTED_MOTION_CASES if committed_motion_cases is None else committed_motion_cases
        body_replay_cases = VERIFIER.BODY_RECORDING_REPLAY_CASES if body_replay_cases is None else body_replay_cases
        body_replay_native_cases = VERIFIER.BODY_RECORDING_REPLAY_NATIVE_CASES if body_replay_native_cases is None else body_replay_native_cases
        body_recording_cases = VERIFIER.BODY_RECORDING_CASES if body_recording_cases is None else body_recording_cases
        buffered_body_cases = VERIFIER.BUFFERED_BODY_CASES if buffered_body_cases is None else buffered_body_cases
        buffered_body_native_cases = VERIFIER.BUFFERED_BODY_NATIVE_CASES if buffered_body_native_cases is None else buffered_body_native_cases
        preparation_cases = VERIFIER.PREPARATION_CASES if preparation_cases is None else preparation_cases
        rig_output_cases = VERIFIER.RIG_OUTPUT_CASES if rig_output_cases is None else rig_output_cases
        rig_carrier_cases = VERIFIER.RIG_CARRIER_CASES if rig_carrier_cases is None else rig_carrier_cases
        planar_support_cases = VERIFIER.PLANAR_SUPPORT_CASES if planar_support_cases is None else planar_support_cases
        body_preflight_cases = VERIFIER.BODY_PREFLIGHT_CASES if body_preflight_cases is None else body_preflight_cases
        receiver_reevaluation_cases = VERIFIER.RECEIVER_REEVALUATION_CASES if receiver_reevaluation_cases is None else receiver_reevaluation_cases
        body_cases = (preparation_cases | rig_output_cases | rig_carrier_cases | planar_support_cases | body_preflight_cases |
                      receiver_reevaluation_cases | buffered_body_native_cases | body_replay_native_cases |
                      (VERIFIER.RIG_OWNER_CASES if rig_owner_cases is None else rig_owner_cases))
        fixtures = "".join(
            f'<test-suite type="TestFixture" name="{name}">'
            f'<test-case fullname="SpatialBlend.Tests.{name}.Test" result="Passed" />'
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(body_cases)) if name == "FoundationReceiverBodyNativeTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(buffered_body_cases)) if name == "FoundationBufferedBodySourceTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(body_recording_cases)) if name == "FoundationBodyRecordingTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(body_replay_cases)) if name == "FoundationBodyRecordingReplayTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(committed_motion_cases)) if name == "FoundationCommittedMotionTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(source_evidence_cases)) if name == "FoundationSourceInteractionTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(hierarchy_cases)) if name == "FoundationInteractionHierarchyTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(receiver_approach_cases)) if name == "FoundationReceiverApproachTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(shared_local_binding_cases)) if name == "FoundationSharedToLocalBindingTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(logical_content_binding_cases)) if name == "FoundationLogicalContentBindingTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(observed_collaborator_cases)) if name == "FoundationObservedCollaboratorTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(observed_collaborator_barrier_cases)) if name == "FoundationObservedCollaboratorAdmissionBarrierTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(observed_collaborator_native_cases)) if name == "FoundationObservedCollaboratorNativeTests" else "")
            + ('<test-case fullname="SpatialBlend.Tests.FoundationObservedCollaboratorCompatibilityTests.MissingOptionalC1SymbolsLeaveLegacyObservationFramesUsable" result="Passed" />'
               if name == "FoundationObservedCollaboratorCompatibilityTests" else "")
            + ("".join(f'<test-case fullname="SpatialBlend.Tests.{name}.{method}" result="Passed" />'
                       for method in sorted(reviewed_recording_ingress_cases)) if name == "ReviewedDatasetBodyRecordingIngressTests" else "")
            + '</test-suite>' for name in names
        )
        count = (len(names) + (len(body_cases) if "FoundationReceiverBodyNativeTests" in names else 0)
                 + (len(buffered_body_cases) if "FoundationBufferedBodySourceTests" in names else 0)
                 + (len(body_recording_cases) if "FoundationBodyRecordingTests" in names else 0)
                 + (len(body_replay_cases) if "FoundationBodyRecordingReplayTests" in names else 0)
                 + (len(committed_motion_cases) if "FoundationCommittedMotionTests" in names else 0)
                 + (len(source_evidence_cases) if "FoundationSourceInteractionTests" in names else 0)
                 + (len(hierarchy_cases) if "FoundationInteractionHierarchyTests" in names else 0)
                 + (len(receiver_approach_cases) if "FoundationReceiverApproachTests" in names else 0)
                 + (len(shared_local_binding_cases) if "FoundationSharedToLocalBindingTests" in names else 0)
                 + (len(logical_content_binding_cases) if "FoundationLogicalContentBindingTests" in names else 0)
                 + (len(observed_collaborator_cases) if "FoundationObservedCollaboratorTests" in names else 0)
                 + (len(observed_collaborator_barrier_cases) if "FoundationObservedCollaboratorAdmissionBarrierTests" in names else 0)
                 + (len(observed_collaborator_native_cases) if "FoundationObservedCollaboratorNativeTests" in names else 0)
                 + (1 if "FoundationObservedCollaboratorCompatibilityTests" in names else 0)
                 + (len(reviewed_recording_ingress_cases) if "ReviewedDatasetBodyRecordingIngressTests" in names else 0))
        return (f'<test-run result="Passed" total="{count}" passed="{count}" '
                f'failed="0" skipped="0">{fixtures}</test-run>')

    def test_hierarchy_is_independent_optional_and_requires_all_cases(self):
        names = NAMES + ("FoundationInteractionHierarchyTests",)
        self.assertEqual(self.run_xml(self.xml()).returncode, 0)
        self.assertNotEqual(self.run_xml(self.xml(), interaction_hierarchy=True).returncode, 0)
        result = self.run_xml(self.xml(names), interaction_hierarchy=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        for missing in VERIFIER.INTERACTION_HIERARCHY_CASES:
            result = self.run_xml(self.xml(names, hierarchy_cases=VERIFIER.INTERACTION_HIERARCHY_CASES - {missing}), interaction_hierarchy=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("interaction hierarchy cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_shared_local_binding_is_independent_and_requires_every_case(self):
        names = NAMES + ("FoundationSharedToLocalBindingTests",)
        self.assertEqual(self.run_xml(self.xml()).returncode, 0)
        self.assertNotEqual(self.run_xml(self.xml(), shared_local_binding=True).returncode, 0)
        result = self.run_xml(self.xml(names), shared_local_binding=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        for missing in VERIFIER.SHARED_LOCAL_BINDING_CASES:
            result = self.run_xml(self.xml(names, shared_local_binding_cases=
                VERIFIER.SHARED_LOCAL_BINDING_CASES - {missing}), shared_local_binding=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("shared-local-binding cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_logical_content_binding_requires_u1_and_every_case(self):
        names = NAMES + ("FoundationSharedToLocalBindingTests", "FoundationLogicalContentBindingTests")
        self.assertNotEqual(self.run_xml(self.xml(), logical_content_binding=True).returncode, 0)
        result = self.run_xml(self.xml(names), shared_local_binding=True, logical_content_binding=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        for missing in VERIFIER.LOGICAL_CONTENT_BINDING_CASES:
            result = self.run_xml(self.xml(names, logical_content_binding_cases=
                VERIFIER.LOGICAL_CONTENT_BINDING_CASES - {missing}), shared_local_binding=True,
                logical_content_binding=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("logical-content-binding cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_observed_collaborator_requires_hierarchy_and_every_case(self):
        names = NAMES + ("FoundationInteractionHierarchyTests", "FoundationObservedCollaboratorTests", "FoundationObservedCollaboratorAdmissionBarrierTests")
        self.assertNotEqual(self.run_xml(self.xml(names), observed_collaborator=True).returncode, 0)
        args = dict(interaction_hierarchy=True, observed_collaborator=True)
        result = self.run_xml(self.xml(names), **args)
        self.assertEqual(result.returncode, 0, result.stderr)
        for missing in VERIFIER.OBSERVED_COLLABORATOR_CASES:
            result = self.run_xml(self.xml(
                names, observed_collaborator_cases=VERIFIER.OBSERVED_COLLABORATOR_CASES - {missing}), **args)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("observed collaborator cases", result.stderr)
            self.assertIn(missing, result.stderr)
        for missing in VERIFIER.OBSERVED_COLLABORATOR_BARRIER_CASES:
            result = self.run_xml(self.xml(
                names, observed_collaborator_barrier_cases=VERIFIER.OBSERVED_COLLABORATOR_BARRIER_CASES - {missing}), **args)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("observed collaborator barrier cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_observed_collaborator_native_requires_c1_and_every_case(self):
        names = NAMES + ("FoundationInteractionHierarchyTests", "FoundationObservedCollaboratorTests", "FoundationObservedCollaboratorAdmissionBarrierTests", "FoundationObservedCollaboratorNativeTests")
        self.assertNotEqual(self.run_xml(self.xml(names), observed_collaborator_native=True).returncode, 0)
        args = dict(interaction_hierarchy=True, observed_collaborator=True, observed_collaborator_native=True)
        result = self.run_xml(self.xml(names), **args)
        self.assertEqual(result.returncode, 0, result.stderr)
        for missing in VERIFIER.OBSERVED_COLLABORATOR_NATIVE_CASES:
            result = self.run_xml(self.xml(names,
                observed_collaborator_native_cases=VERIFIER.OBSERVED_COLLABORATOR_NATIVE_CASES - {missing}), **args)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("observed-collaborator native cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_pre_c1_compatibility_requires_exact_negative_feature_case(self):
        names = NAMES + ("FoundationObservedCollaboratorCompatibilityTests",)
        self.assertNotEqual(self.run_xml(self.xml(),
            observed_collaborator_compatibility=True).returncode, 0)
        result = self.run_xml(self.xml(names), observed_collaborator_compatibility=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        missing = self.xml(names).replace(
            'FoundationObservedCollaboratorCompatibilityTests.MissingOptionalC1SymbolsLeaveLegacyObservationFramesUsable" result="Passed"',
            'FoundationObservedCollaboratorCompatibilityTests.MissingOptionalC1SymbolsLeaveLegacyObservationFramesUsable" result="Skipped"')
        result = self.run_xml(missing, observed_collaborator_compatibility=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Foundation test cases are absent, failed, or skipped", result.stderr)
        result = self.run_xml(self.xml(names), interaction_hierarchy=True,
            observed_collaborator=True, observed_collaborator_compatibility=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("cannot be combined", result.stderr)

    def test_hierarchy_skip_rejects_and_optional_features_compose(self):
        names = NAMES + ("FoundationInteractionHierarchyTests", "FoundationSourceInteractionTests") + RICH + VALIDITY + RECEIVER_INPUT
        xml = self.xml(names)
        result = self.run_xml(xml, source_evidence=True, interaction_hierarchy=True, rich=True, validity=True, receiver_input=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        missing = sorted(VERIFIER.INTERACTION_HIERARCHY_CASES)[0]
        skipped = xml.replace(f'FoundationInteractionHierarchyTests.{missing}" result="Passed"',
                              f'FoundationInteractionHierarchyTests.{missing}" result="Skipped"')
        self.assertNotEqual(self.run_xml(skipped, interaction_hierarchy=True).returncode, 0)

    def test_source_evidence_is_independent_optional_and_requires_all_cases(self):
        names = NAMES + ("FoundationSourceInteractionTests",)
        self.assertEqual(self.run_xml(self.xml()).returncode, 0)
        self.assertNotEqual(self.run_xml(self.xml(), source_evidence=True).returncode, 0)
        result = self.run_xml(self.xml(names), source_evidence=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        for missing in VERIFIER.SOURCE_EVIDENCE_CASES:
            result = self.run_xml(self.xml(names, source_evidence_cases=VERIFIER.SOURCE_EVIDENCE_CASES - {missing}), source_evidence=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("source evidence cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_source_evidence_skips_fail_and_other_feature_selections_compose(self):
        names = NAMES + ("FoundationSourceInteractionTests",) + RICH + VALIDITY + RECEIVER_INPUT
        xml = self.xml(names)
        result = self.run_xml(xml, source_evidence=True, rich=True, validity=True, receiver_input=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        missing = sorted(VERIFIER.SOURCE_EVIDENCE_CASES)[0]
        skipped = xml.replace(f'FoundationSourceInteractionTests.{missing}" result="Passed"',
                              f'FoundationSourceInteractionTests.{missing}" result="Skipped"')
        self.assertNotEqual(self.run_xml(skipped, source_evidence=True).returncode, 0)

    def test_missing_each_committed_motion_case_fails(self):
        for missing in VERIFIER.COMMITTED_MOTION_CASES:
            with self.subTest(missing=missing):
                result = self.run_xml(self.xml(committed_motion_cases=VERIFIER.COMMITTED_MOTION_CASES - {missing}))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("committed motion cases", result.stderr)
                self.assertIn(missing, result.stderr)

    def test_reviewed_recording_ingress_requires_receiver_input_and_every_case(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT + ("ReviewedDatasetBodyRecordingIngressTests",)
        self.assertNotEqual(self.run_xml(self.xml(names), reviewed_recording_ingress=True).returncode, 0)
        args = dict(rich=True, validity=True, receiver_input=True, reviewed_recording_ingress=True)
        self.assertEqual(self.run_xml(self.xml(names), **args).returncode, 0)
        for missing in VERIFIER.REVIEWED_RECORDING_INGRESS_CASES:
            result = self.run_xml(self.xml(
                names, reviewed_recording_ingress_cases=VERIFIER.REVIEWED_RECORDING_INGRESS_CASES - {missing}), **args)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("reviewed-recording ingress cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_missing_each_buffered_body_case_fails(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for field, required in (("buffered_body_cases", VERIFIER.BUFFERED_BODY_CASES),
                                ("buffered_body_native_cases", VERIFIER.BUFFERED_BODY_NATIVE_CASES)):
            for missing in required:
                with self.subTest(missing=missing):
                    result = self.run_xml(self.xml(names, **{field: required - {missing}}),
                                          rich=True, validity=True, receiver_input=True)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("buffered body cases", result.stderr)
                    self.assertIn(missing, result.stderr)

    def test_missing_each_body_recording_case_fails(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.BODY_RECORDING_CASES:
            result = self.run_xml(self.xml(names, body_recording_cases=VERIFIER.BODY_RECORDING_CASES - {missing}),
                                  rich=True, validity=True, receiver_input=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("body recording cases", result.stderr)
            self.assertIn(missing, result.stderr)

    def test_missing_each_body_recording_replay_case_fails(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for field, required in (("body_replay_cases", VERIFIER.BODY_RECORDING_REPLAY_CASES),
                                ("body_replay_native_cases", VERIFIER.BODY_RECORDING_REPLAY_NATIVE_CASES)):
            for missing in required:
                result = self.run_xml(self.xml(names, **{field: required - {missing}}),
                                      rich=True, validity=True, receiver_input=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("body recording replay cases", result.stderr)
                self.assertIn(missing, result.stderr)

    def test_source_recording_selection_requires_receiver_fixture_and_exact_method(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        self.assertNotEqual(self.run_xml(self.xml(names), body_recordings=True).returncode, 0)
        args = dict(rich=True, validity=True, receiver_input=True, body_recordings=True)
        missing = self.run_xml(self.xml(names), **args)
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn("FoundationBodyRecordingSourceTests", missing.stderr)
        fixture_only = self.xml(names + ("FoundationBodyRecordingSourceTests",))
        self.assertNotEqual(self.run_xml(fixture_only, **args).returncode, 0)
        complete = fixture_only.replace("SpatialBlend.Tests.FoundationBodyRecordingSourceTests.Test", VERIFIER.BODY_RECORDING_SOURCE_CASE)
        self.assertEqual(self.run_xml(complete, **args).returncode, 0)
        skipped = complete.replace(VERIFIER.BODY_RECORDING_SOURCE_CASE + '" result="Passed"',
                                   VERIFIER.BODY_RECORDING_SOURCE_CASE + '" result="Skipped"')
        self.assertNotEqual(self.run_xml(skipped, **args).returncode, 0)

    def test_body_recording_launcher_rejects_missing_relative_duplicate_and_unqualified_selection(self):
        launcher = Path(__file__).with_name("run-foundation-tests.sh")
        with tempfile.TemporaryDirectory() as directory:
            library = Path(directory) / "libspatialblend.dylib"
            selection = Path(directory) / "cases.json"
            library.write_bytes(b"not-loaded")
            selection.write_text("{}")
            for options in (("--body-recordings",), ("--body-recordings", "relative.json"),
                            ("--body-recordings", str(selection)),
                            ("--body-recordings", str(selection), "--body-recordings", str(selection))):
                result = subprocess.run(["bash", str(launcher), "--native-library", str(library), *options],
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 2)
                self.assertIn("--body-recordings", result.stderr)

    def test_complete_actual_fixture_set_passes(self):
        result = self.run_xml(self.xml())
        self.assertEqual(result.returncode, 0, result.stderr)
        count = len(NAMES) + len(VERIFIER.COMMITTED_MOTION_CASES)
        self.assertIn(f"passed:{count} failed:0 skipped:0", result.stdout)

    def test_every_planar_support_case_is_required(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.PLANAR_SUPPORT_CASES:
            with self.subTest(missing=missing):
                result = self.run_xml(self.xml(names, planar_support_cases=VERIFIER.PLANAR_SUPPORT_CASES - {missing}),
                                      rich=True, validity=True, receiver_input=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(missing, result.stderr)

    def test_every_rig_carrier_case_is_required(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.RIG_CARRIER_CASES:
            with self.subTest(missing=missing):
                result = self.run_xml(self.xml(names, rig_carrier_cases=VERIFIER.RIG_CARRIER_CASES - {missing}),
                                      rich=True, validity=True, receiver_input=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(missing, result.stderr)

    def test_every_receiver_reevaluation_case_is_required(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.RECEIVER_REEVALUATION_CASES:
            with self.subTest(missing=missing):
                result = self.run_xml(self.xml(names, receiver_reevaluation_cases=VERIFIER.RECEIVER_REEVALUATION_CASES - {missing}),
                                      rich=True, validity=True, receiver_input=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(missing, result.stderr)

    def test_every_body_preflight_case_is_required(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.BODY_PREFLIGHT_CASES:
            with self.subTest(missing=missing):
                result = self.run_xml(self.xml(names, body_preflight_cases=VERIFIER.BODY_PREFLIGHT_CASES - {missing}),
                                      rich=True, validity=True, receiver_input=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(missing, result.stderr)

    def test_graphics_selection_requires_its_fixture_but_headless_keeps_it_optional(self):
        graphics_names = NAMES + GRAPHICS
        self.assertEqual(self.run_xml(self.xml()).returncode, 0)
        # A graphics fixture is optional in the normal headless result; the
        # runner excludes it there, while the graphics selection requires it.
        self.assertEqual(self.run_xml(self.xml(graphics_names)).returncode, 0)
        missing = self.run_xml(self.xml(), graphics=True)
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn(GRAPHICS[0], missing.stderr)
        selected = self.run_xml(self.xml(graphics_names), graphics=True)
        self.assertEqual(selected.returncode, 0, selected.stderr)
        skipped = self.run_xml(self.xml(graphics_names).replace(
            f'fullname="SpatialBlend.Tests.{GRAPHICS[0]}.Test" result="Passed"',
            f'fullname="SpatialBlend.Tests.{GRAPHICS[0]}.Test" result="Skipped"'), graphics=True)
        self.assertNotEqual(skipped.returncode, 0)

    def test_every_fixture_is_required(self):
        for missing in NAMES:
            with self.subTest(missing=missing):
                result = self.run_xml(self.xml(tuple(name for name in NAMES if name != missing)))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(missing, result.stderr)

    def test_rich_selection_requires_pure_and_real_native_fixtures(self):
        self.assertEqual(self.run_xml(self.xml()).returncode, 0)
        for missing in RICH:
            names = NAMES + tuple(name for name in RICH if name != missing)
            result = self.run_xml(self.xml(names), rich=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(missing, result.stderr)
        result = self.run_xml(self.xml(NAMES + RICH), rich=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_rich_and_graphics_selections_compose(self):
        result = self.run_xml(self.xml(NAMES + RICH + GRAPHICS), graphics=True, rich=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotEqual(self.run_xml(self.xml(NAMES + RICH), graphics=True, rich=True).returncode, 0)
        self.assertNotEqual(self.run_xml(self.xml(NAMES + GRAPHICS), graphics=True, rich=True).returncode, 0)

    def test_skipped_rich_case_cannot_satisfy_feature_gate(self):
        result = self.run_xml(self.xml(NAMES + RICH).replace(
            f'fullname="SpatialBlend.Tests.{RICH[1]}.Test" result="Passed"',
            f'fullname="SpatialBlend.Tests.{RICH[1]}.Test" result="Skipped"'), rich=True)
        self.assertNotEqual(result.returncode, 0)

    def test_validity_selection_requires_rich_and_its_independent_fixture(self):
        self.assertEqual(self.run_xml(self.xml(NAMES + RICH), rich=True).returncode, 0)
        missing = self.run_xml(self.xml(NAMES + RICH), rich=True, validity=True)
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn(VALIDITY[0], missing.stderr)
        names = NAMES + RICH + VALIDITY
        self.assertNotEqual(self.run_xml(self.xml(names), validity=True).returncode, 0)
        result = self.run_xml(self.xml(names), rich=True, validity=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        skipped = self.xml(names).replace(
            f'fullname="SpatialBlend.Tests.{VALIDITY[0]}.Test" result="Passed"',
            f'fullname="SpatialBlend.Tests.{VALIDITY[0]}.Test" result="Skipped"')
        self.assertNotEqual(self.run_xml(skipped, rich=True, validity=True).returncode, 0)

    def test_validity_graphics_and_rich_selections_compose(self):
        result = self.run_xml(self.xml(NAMES + RICH + VALIDITY + GRAPHICS),
                              graphics=True, rich=True, validity=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_receiver_selection_requires_each_fixture_and_prior_features(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for rich, validity in ((False, False), (True, False), (False, True)):
            self.assertNotEqual(self.run_xml(self.xml(names), rich=rich, validity=validity,
                                            receiver_input=True).returncode, 0)
        for missing in RECEIVER_INPUT:
            selected = tuple(name for name in names if name != missing)
            result = self.run_xml(self.xml(selected), rich=True, validity=True, receiver_input=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(missing, result.stderr)
        self.assertEqual(self.run_xml(self.xml(names), rich=True, validity=True,
                                      receiver_input=True).returncode, 0)

    def test_receiver_skipped_case_fails_and_graphics_selection_composes(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT + GRAPHICS
        result = self.run_xml(self.xml(names), rich=True, validity=True, receiver_input=True, graphics=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        skipped = self.xml(names).replace(
            f'fullname="SpatialBlend.Tests.{RECEIVER_INPUT[1]}.Test" result="Passed"',
            f'fullname="SpatialBlend.Tests.{RECEIVER_INPUT[1]}.Test" result="Skipped"')
        self.assertNotEqual(self.run_xml(skipped, rich=True, validity=True, receiver_input=True).returncode, 0)

    def test_receiver_approach_requires_exact_optional_fixture_and_cases(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT + ("FoundationReceiverApproachTests",)
        self.assertNotEqual(self.run_xml(self.xml(names), rich=True, validity=True,
                                         receiver_approach=True).returncode, 0)
        complete = self.run_xml(self.xml(names), rich=True, validity=True, receiver_input=True,
                                receiver_approach=True)
        self.assertEqual(complete.returncode, 0, complete.stderr)
        for missing in VERIFIER.RECEIVER_APPROACH_CASES:
            result = self.run_xml(self.xml(names, receiver_approach_cases=VERIFIER.RECEIVER_APPROACH_CASES - {missing}),
                                  rich=True, validity=True, receiver_input=True, receiver_approach=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(missing, result.stderr)

    def test_every_preparation_regression_is_required_without_count_shortcuts(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.PREPARATION_CASES:
            result = self.run_xml(self.xml(names, VERIFIER.PREPARATION_CASES - {missing}),
                                  rich=True, validity=True, receiver_input=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(missing, result.stderr)

    def test_every_rig_output_regression_is_required_without_count_shortcuts(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.RIG_OUTPUT_CASES:
            result = self.run_xml(self.xml(names, rig_output_cases=VERIFIER.RIG_OUTPUT_CASES - {missing}),
                                  rich=True, validity=True, receiver_input=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(missing, result.stderr)

    def test_every_rig_owner_case_is_required_with_receiver_input(self):
        names = NAMES + RICH + VALIDITY + RECEIVER_INPUT
        for missing in VERIFIER.RIG_OWNER_CASES:
            result = self.run_xml(self.xml(names, rig_owner_cases=VERIFIER.RIG_OWNER_CASES - {missing}),
                                  rich=True, validity=True, receiver_input=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(missing, result.stderr)

    def test_empty_failed_skipped_or_wrong_totals_fail(self):
        passing = self.xml()
        count = len(NAMES) + len(VERIFIER.COMMITTED_MOTION_CASES)
        for broken in (
            '<test-run result="Passed" total="0" passed="0" />',
            passing.replace('result="Passed"', 'result="Failed"', 1),
            passing.replace('result="Passed" />', 'result="Skipped" />', 1),
            passing.replace(f'total="{count}"', f'total="{count + 1}"'),
            passing.replace(f'passed="{count}"', f'passed="{count - 1}"'),
            passing.replace('skipped="0"', 'skipped="1"'),
        ):
            self.assertNotEqual(self.run_xml(broken).returncode, 0)

    def test_similarly_named_fixtures_do_not_satisfy_discovery(self):
        self.assertNotEqual(self.run_xml(self.xml(tuple("Not" + name for name in NAMES))).returncode, 0)

    def test_malformed_xml_is_rejected(self):
        self.assertNotEqual(self.run_xml("<not-closed").returncode, 0)


if __name__ == "__main__":
    unittest.main()
