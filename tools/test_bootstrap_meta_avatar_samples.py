#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Focused filesystem tests for bootstrap-meta-avatar-samples.py (no Unity)."""
from __future__ import annotations

import importlib.util
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools/bootstrap-meta-avatar-samples.py"
SPEC = importlib.util.spec_from_file_location("bootstrap_meta_avatar_samples", SCRIPT)
bootstrap = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(bootstrap)
REAL_PACKAGE = ROOT / "Library/PackageCache/com.meta.xr.sdk.avatars@99a67d4aa1f5"


# The pinned Meta Avatars package lives in the Unity package cache (Library/), which is not in git.
# Tests that read it run wherever the cache is resolved and are skipped, visibly, on a clean checkout.
needs_package_cache = unittest.skipUnless(REAL_PACKAGE.is_dir(), "Library/PackageCache is not resolved in this checkout")


class BootstrapMetaAvatarSamplesTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.project = Path(self.temp.name) / "project"
        self.project.mkdir()
        self.destination = "Assets/Samples/Meta Avatars SDK/35.2.0/Sample Scenes/Scripts"

    def tearDown(self) -> None:
        self.temp.cleanup()

    def run_bootstrap(self, *extra: str) -> int:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return bootstrap.main(["--project-root", str(self.project), "--package-cache", str(REAL_PACKAGE),
                                   "--destination", self.destination, *extra])

    @needs_package_cache
    def test_fresh_import_preserves_official_bytes_and_guids(self) -> None:
        provenance = self.project / "provenance.json"
        self.assertEqual(0, self.run_bootstrap("--apply", "--provenance", str(provenance.resolve())))
        source = REAL_PACKAGE / bootstrap.SAMPLE_ROOT
        destination = self.project / self.destination
        source_files = bootstrap.source_files(source)
        self.assertEqual(len(source_files), len(bootstrap.source_files(destination)))
        for original in source_files:
            target = (destination.parent / f"{destination.name}.meta" if original == source.parent / f"{source.name}.meta"
                      else destination / original.relative_to(source))
            self.assertEqual(original.read_bytes(), target.read_bytes())
        self.assertTrue((destination / "Oculus.AvatarSDK2.Samples.asmdef").is_file())
        self.assertTrue((destination / "LegsBehavior/OvrAvatarAnimationBehavior.cs.meta").is_file())
        self.assertEqual(bootstrap.PACKAGE_FINGERPRINT, json.loads(provenance.read_text())["package"]["fingerprint"])

    @needs_package_cache
    def test_matching_second_run_is_idempotent(self) -> None:
        self.assertEqual(0, self.run_bootstrap("--apply"))
        self.assertEqual(0, self.run_bootstrap("--apply"))

    def test_differing_destination_file_fails_without_overwrite(self) -> None:
        target = self.project / self.destination / "LegsBehavior/OvrAvatarAnimationBehavior.cs"
        target.parent.mkdir(parents=True)
        target.write_text("user content\n")
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertEqual("user content\n", target.read_text())

    @needs_package_cache
    def test_package_version_failure_writes_nothing(self) -> None:
        bad = self.project / "bad-package"
        bad.mkdir()
        metadata = json.loads((REAL_PACKAGE / "package.json").read_text())
        metadata["version"] = "35.2.1"
        (bad / "package.json").write_text(json.dumps(metadata))
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            result = bootstrap.main(["--project-root", str(self.project), "--package-cache", str(bad), "--apply"])
        self.assertEqual(2, result)
        self.assertFalse((self.project / bootstrap.DEFAULT_DESTINATION).exists())

    def test_assets_ancestor_symlink_is_rejected_without_escape_write(self) -> None:
        outside = self.project.parent / "outside"
        outside.mkdir()
        (self.project / "Assets").symlink_to(outside, target_is_directory=True)
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertEqual([], list(outside.iterdir()))

    def test_dangling_destination_ancestor_symlink_is_rejected(self) -> None:
        (self.project / "Assets").symlink_to(self.project.parent / "missing", target_is_directory=True)
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertFalse((self.project.parent / "missing").exists())

    def test_provenance_symlink_is_rejected_before_destination_write(self) -> None:
        target = self.project.parent / "outside-provenance.json"
        target.write_text("user provenance\n")
        link = self.project / "provenance-link.json"
        link.symlink_to(target)
        self.assertEqual(2, self.run_bootstrap("--apply", "--provenance", str(link.parent.resolve() / link.name)))
        self.assertEqual("user provenance\n", target.read_text())
        self.assertFalse((self.project / self.destination).exists())

    def test_project_root_destination_is_rejected_without_write(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            result = bootstrap.main(["--project-root", str(self.project), "--package-cache", str(REAL_PACKAGE),
                                     "--destination", ".", "--apply"])
        self.assertEqual(2, result)
        self.assertFalse((self.project / "Scripts.meta").exists())


if __name__ == "__main__":
    unittest.main()
