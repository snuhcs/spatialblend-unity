#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Guards for the fresh-checkout build parity requirements (versionCode policy)."""
from __future__ import annotations

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
# Devices already hold vc36 (another signing key). Release codes are monotonic and must exceed every installed code.
LAST_INSTALLED_VERSION_CODE = 36


class VersionCodeTests(unittest.TestCase):
    def test_android_bundle_version_code_exceeds_every_installed_code(self) -> None:
        text = (ROOT / "ProjectSettings/ProjectSettings.asset").read_text(encoding="utf-8")
        match = re.search(r"^\s*AndroidBundleVersionCode:\s*(\d+)\s*$", text, re.MULTILINE)
        self.assertIsNotNone(match, "AndroidBundleVersionCode not found in ProjectSettings.asset")
        self.assertGreater(int(match.group(1)), LAST_INSTALLED_VERSION_CODE)

    def test_apk_build_uses_lz4hc_and_tmp_guard_exists(self) -> None:
        build = (ROOT / "Assets/SpatialBlend/Editor/Il2CppBuildCheck.cs").read_text(encoding="utf-8")
        self.assertIn("BuildOptions.CompressWithLz4HC", build)
        guard = (ROOT / "Assets/SpatialBlend/Editor/TmpEssentialsBuildGuard.cs").read_text(encoding="utf-8")
        self.assertIn("IPreprocessBuildWithReport", guard)
        self.assertIn("setup_sdks.py", guard)


if __name__ == "__main__":
    unittest.main()
