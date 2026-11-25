# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Source guards for the SAM3 / SAM3D scan port (T5.1). None of these start Unity or open a socket."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets/SpatialBlend"

SCAN_RUNTIME = (
    sorted((ASSETS / "Freeform/Scanning").glob("*.cs"))
    + [
        ASSETS / "Framework/Internal/SessionLibrary.cs",
        ASSETS / "Framework/Internal/NativeScanSessionTransport.cs",
        ASSETS / "Framework/Scripts/SessionManager.cs",
        ASSETS / "Samples/Scanning/Scripts/GltfScanResultSink.cs",
        ASSETS / "Samples/Scanning/Scripts/Sam3dPendingIndicatorManager.cs",
        ASSETS / "Samples/Scanning/Scripts/PassthroughPanelManager.cs",
    ]
)
SCAN_TESTS = sorted((ASSETS / "Framework/Test/Restoration").glob("Scan*.cs")) + [
    ASSETS / "Framework/Test/Restoration/SessionManagerConfigTests.cs"
]
NEW_FILES = SCAN_RUNTIME + SCAN_TESTS
# These two were CRLF on the base. Stage 3 normalized them to LF (repo policy: keep LF), so they must not
# regress to CRLF or mix endings.
LF_NORMALIZED_FILES = tuple(path for path in (
    ASSETS / "Framework/Test/Restoration/FreeformV1ParityTests.cs",
    ROOT / "docs/freeform-v1-parity.md",   # a private design note: absent from the public tree
) if path.is_file() or path.suffix != ".md")

IPV4 = re.compile(r"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")
NETWORK_API = re.compile(
    r"System\.Net|Socket|UnityWebRequest|HttpClient|WebClient|TcpClient|Grpc|DllImport|Process\.Start"
)


def read(path):
    return path.read_text(encoding="utf-8")


def code_lines(path):
    """Yield (number, code) with // and /// comments removed. A string holding // is not expected here."""
    for number, line in enumerate(read(path).splitlines(), 1):
        code = line.split("//")[0]
        if code.strip():
            yield number, code


def code_text(path):
    return "\n".join(code for _, code in code_lines(path))


class ScanPortGuards(unittest.TestCase):
    def test_every_scan_source_exists(self):
        missing = [p.relative_to(ROOT).as_posix() for p in SCAN_RUNTIME + SCAN_TESTS if not p.is_file()]
        self.assertEqual(missing, [])
        self.assertGreaterEqual(len(list((ASSETS / "Freeform/Scanning").glob("*.cs"))), 6)

    def test_no_server_address_is_compiled_in(self):
        # The public hostname is the SpatialBlendSettings default, checked separately. This guard rejects an IPv4 literal in the scan runtime.
        hits = []
        for path in SCAN_RUNTIME + SCAN_TESTS:
            for number, line in code_lines(path):
                if IPV4.search(line):
                    hits.append(f"{path.relative_to(ROOT)}:{number}:{line.strip()}")
        self.assertEqual(hits, [], "IPv4 literal in scan code:\n" + "\n".join(hits))

    def test_settings_name_no_scan_server_by_default(self):
        # The public repository names no scan server: scanning is off until one is configured (docs/local-session-config.md).
        settings = read(ASSETS / "Framework/Scripts/SpatialBlendSettings.cs")
        self.assertRegex(settings, r'public const string DefaultSessionServerAddress = "";')
        self.assertRegex(settings, r"public string sessionServerAddress = DefaultSessionServerAddress;")
        self.assertRegex(settings, r"public bool sessionUseTls = true;")
        self.assertRegex(settings, r'public string sessionFallbackServerAddress = "";')
        asset = read(ROOT / "Assets/Resources/SpatialBlendSettings.asset")
        self.assertRegex(asset, r"(?m)^  sessionServerAddress: ?$")
        self.assertNotIn("snu.ac.kr", asset)
        self.assertRegex(asset, r"(?m)^  sessionUseTls: 1$")

    def test_the_stale_sam3_web_setting_is_gone(self):
        # v1 removed the old SAM3 web URL setting and the button that opened it. The scan runs through
        # spatialblend_session, so a server URL in the settings asset or a ControlPanel launcher is dead.
        hits = []
        for pattern in ("*.cs", "*.asset", "*.prefab", "*.unity"):
            for path in sorted((ROOT / "Assets").rglob(pattern)):
                text = path.read_text(encoding="utf-8", errors="ignore")
                for needle in ("sam3ServerUrl", "LaunchSAM3Website", "aidemos.meta.com"):
                    if needle in text:
                        hits.append(f"{path.relative_to(ROOT)}: {needle}")
        self.assertEqual(hits, [])

    def test_tests_and_the_pure_layer_never_touch_the_network_or_the_library(self):
        pure = sorted((ASSETS / "Freeform/Scanning").glob("*.cs")) + SCAN_TESTS
        hits = []
        for path in pure:
            for number, line in code_lines(path):
                if NETWORK_API.search(line):
                    hits.append(f"{path.relative_to(ROOT)}:{number}:{line.strip()}")
        self.assertEqual(hits, [], "network or native call in pure scan code:\n" + "\n".join(hits))

    def test_only_the_session_binding_imports_native_code(self):
        importers = []
        for path in SCAN_RUNTIME:
            text = code_text(path)
            if "DllImport" in text:
                importers.append(path.name)
                for match in re.finditer(r"DllImport\(([^,)]+)", text):
                    self.assertEqual(match.group(1).strip(), "LIB_NAME")
        self.assertEqual(importers, ["SessionLibrary.cs"])
        self.assertIn('LIB_NAME = "spatialblend_session"', read(ASSETS / "Framework/Internal/SessionLibrary.cs"))

    def test_only_the_native_transport_calls_the_session_binding(self):
        callers = [
            p.name for p in SCAN_RUNTIME if "SessionLibrary." in read(p) and p.name != "SessionLibrary.cs"
        ]
        self.assertEqual(callers, ["NativeScanSessionTransport.cs"])

    def test_the_pure_layer_does_not_depend_on_the_legacy_assembly(self):
        # SpatialBlend.Freeform references only SpatialBlend.Foundation.
        for path in (ASSETS / "Freeform/Scanning").glob("*.cs"):
            text = code_text(path)
            self.assertNotIn("SpatialBlendSettings", text, path.name)
            self.assertNotIn("SpatialBlendManager", text, path.name)
            self.assertNotIn("Debug.Log", text, path.name)

    def test_no_affordance_volume_on_the_scan_runtime_path(self):
        for path in SCAN_RUNTIME:
            self.assertNotIn("AffordanceVolume", code_text(path), path.name)

    def test_every_scan_fixture_is_required_by_the_verifiers(self):
        fixtures = set()
        for path in SCAN_TESTS:
            fixtures.update(re.findall(r"public (?:sealed )?class (\w+)", read(path)))
        fixtures = {name for name in fixtures if name.endswith("Tests")}
        self.assertGreaterEqual(len(fixtures), 6)
        freeform = read(ROOT / "tools/verify-freeform-results.py")
        foundation = read(ROOT / "tools/verify-foundation-results.py")
        sources = read(ROOT / "tools/run-foundation-tests.sh")
        for name in sorted(fixtures):
            self.assertIn(f'"{name}"', freeform, name)
            if name != "SessionManagerConfigTests":  # needs SpatialBlendSettings, so the full-project run only
                self.assertIn(f'"{name}"', foundation, name)
        for path in SCAN_TESTS:
            if path.name != "SessionManagerConfigTests.cs":
                self.assertIn(path.name, sources, path.name)

    def test_new_files_use_lf_and_end_with_a_newline(self):
        for path in NEW_FILES:
            data = path.read_bytes()
            self.assertNotIn(b"\r", data, path.name)
            self.assertTrue(data.endswith(b"\n"), path.name)

    def test_normalized_files_stay_lf(self):
        for path in LF_NORMALIZED_FILES:
            data = path.read_bytes()
            self.assertNotIn(b"\r", data, path.name)

    def test_the_parity_matrix_has_the_scan_rows(self):
        parity = ROOT / "docs/freeform-v1-parity.md"
        if not parity.is_file():
            self.skipTest("the parity record (docs/freeform-v1-parity.md) is a private design note, absent from the public tree")
        doc = read(parity)
        self.assertRegex(doc, r"\| S1 \|.*\| Active \|")
        self.assertRegex(doc, r"\| S2 \|.*\| Active \|")
        self.assertRegex(doc, r"\| AV12 \|.*\| Active \|")
        tests = read(ASSETS / "Framework/Test/Restoration/FreeformV1ParityTests.cs")
        self.assertIn("S1_ScanSessionsRunBehindTheSessionSeam", tests)
        self.assertIn("S2_ScanBoxBecomesAnExplicitRegion", tests)

    def test_the_session_library_is_not_tracked(self):
        import subprocess

        tracked = subprocess.check_output(["git", "ls-files", "Assets/Plugins"], cwd=ROOT, text=True).splitlines()
        self.assertEqual([p for p in tracked if "libspatialblend_session" in p and not p.endswith(".meta")], [])


if __name__ == "__main__":
    unittest.main()
