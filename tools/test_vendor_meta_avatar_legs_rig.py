#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Tests for vendor-meta-avatar-legs-rig.py (no Unity). The package cache is machine-local: those tests are skipped, visibly,
where it is not resolved (set SPATIALBLEND_AVATARS_PACKAGE to a cached com.meta.xr.sdk.avatars@* directory to run them)."""
from __future__ import annotations

import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("vendor_legs_rig", ROOT / "tools/vendor-meta-avatar-legs-rig.py")
vendor = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(vendor)
_found = sorted((ROOT / "Library/PackageCache").glob("com.meta.xr.sdk.avatars@*"))
PACKAGE = Path(os.environ["SPATIALBLEND_AVATARS_PACKAGE"]) if os.environ.get("SPATIALBLEND_AVATARS_PACKAGE") else (_found[0] if _found else None)
needs_package = unittest.skipUnless(PACKAGE is not None and PACKAGE.is_dir(), "the Meta Avatars package cache is not resolved here")
VENDORED = ROOT / vendor.DESTINATION
# The rig is Meta sample material: git-ignored, installed per checkout by tools/setup_sdks.py from the user's own package cache.
needs_installed = unittest.skipUnless((VENDORED / "Resources/RTR00003.prefab").is_file(),
                                      "the legs rig is not installed in this checkout (python3 tools/setup_sdks.py)")


def quiet(argv: list[str]) -> int:
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return vendor.main(argv)


class VendoredLegsRigTests(unittest.TestCase):
    def test_the_rig_is_git_ignored_and_not_tracked(self) -> None:
        tracked = subprocess.run(["git", "-C", str(ROOT), "ls-files", str(vendor.DESTINATION)], capture_output=True, text=True, check=True).stdout
        self.assertEqual("", tracked, "the Meta legs rig is not redistributed in this repository")
        ignored = subprocess.run(["git", "-C", str(ROOT), "check-ignore", "-q", "--no-index", str(vendor.DESTINATION / "Resources/RTR00003.prefab")])
        self.assertEqual(0, ignored.returncode, "the installed copy must stay out of git")

    @needs_installed
    def test_the_installed_rig_has_its_prefab_controller_licence_and_notice(self) -> None:
        for relative in ("Resources/RTR00003.prefab", "Resources/RTR00003.prefab.meta", "DefaultAnimations/RT_MecanimLegs.controller",
                         "DefaultAnimations/RTR00003.fbx", "LICENSE.md", "NOTICE.md"):
            self.assertTrue((VENDORED / relative).is_file(), relative)
        self.assertIn("Copyright (c) Meta Platform Technologies", (VENDORED / "NOTICE.md").read_text())
        # The prefab references the controller and the FBX by the package GUIDs their .meta files carry.
        prefab = (VENDORED / "Resources/RTR00003.prefab").read_text()
        for meta in ("DefaultAnimations/RT_MecanimLegs.controller.meta", "DefaultAnimations/RTR00003.fbx.meta"):
            guid = vendor._OWN_GUID.search((VENDORED / meta).read_text()).group(1)
            self.assertIn(guid, prefab, meta)

    @needs_installed
    def test_every_controller_reference_is_installed(self) -> None:
        guids = {vendor._OWN_GUID.search(m.read_text()).group(1) for m in VENDORED.rglob("*.meta")}
        controller = VENDORED / "DefaultAnimations/RT_MecanimLegs.controller"
        missing = vendor._references(controller) - guids - vendor.KNOWN_DANGLING_GUIDS
        self.assertEqual(set(), missing, "the controller's clips are all in the installed rig")

    @needs_package
    @needs_installed
    def test_the_installed_copy_matches_the_package_closure(self) -> None:
        self.assertEqual([], vendor.check(ROOT, PACKAGE))

    @needs_package
    def test_apply_into_an_empty_project_then_check(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            project = Path(temp)
            self.assertEqual(0, quiet(["--project-root", str(project), "--package-cache", str(PACKAGE), "--apply"]))
            self.assertEqual(0, quiet(["--project-root", str(project), "--package-cache", str(PACKAGE), "--check"]))
            relatives, dangling = vendor.closure(PACKAGE)
            self.assertEqual(sorted(vendor.KNOWN_DANGLING_GUIDS), dangling)
            self.assertGreaterEqual(sum(1 for r in relatives if str(r).endswith(".fbx")), 30)
            clip = project / vendor.DESTINATION / "DefaultAnimations/loco_neutral_walk_fwd_anim.fbx"
            clip.write_bytes(b"changed")
            self.assertEqual(1, quiet(["--project-root", str(project), "--package-cache", str(PACKAGE), "--check"]))
            self.assertEqual(2, quiet(["--project-root", str(project), "--package-cache", str(PACKAGE), "--apply"]))
            self.assertEqual(0, quiet(["--project-root", str(project), "--package-cache", str(PACKAGE), "--apply", "--force"]))
            shutil.rmtree(project / vendor.DESTINATION / "Resources")
            self.assertEqual(1, quiet(["--project-root", str(project), "--package-cache", str(PACKAGE), "--check"]))


if __name__ == "__main__":
    unittest.main()
