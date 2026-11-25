#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Filesystem-only tests for bootstrap-photon-sdk.py; does not invoke Unity."""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("bootstrap_photon_sdk", ROOT / "tools/bootstrap-photon-sdk.py")
bootstrap = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(bootstrap)


class BootstrapPhotonSdkTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name).resolve()
        self.project = root / "project"
        self.project.mkdir()
        self.source = root / "official" / "Assets" / "Photon"
        self.make_source()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def make_source(self) -> None:
        (self.source / "Fusion").mkdir(parents=True)
        (self.source / "Fusion/package.json").write_text(json.dumps({"name": "com.exitgames.photonfusion"}))
        (self.source / "Fusion/build_info.txt").write_text("build: 2.0.9 Stable 1566\n")
        for relative in bootstrap.REQUIRED_FUSION_PATHS:
            path = self.source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"official {relative}\n")
        for root_name in bootstrap.COPIED_ROOTS:
            (self.source / root_name).mkdir(parents=True, exist_ok=True)
            (self.source / f"{root_name}.meta").write_text(f"guid: {root_name}\n")
            (self.source / root_name / "file.txt").write_text(f"official {root_name}\n")
        (self.source / "PhotonVoice/changes-voice.txt").write_text("Photon Voice 2 Change Log\nv2.63\n")
        (self.source / "PhotonRealtime/Code/changes-realtime.txt").write_text("Version 4.1.8.17\n")
        demo = self.source / "PhotonVoice/Demos/secret-demo.txt"
        demo.parent.mkdir(parents=True)
        demo.write_text("demo must not copy\n")
        (self.source / "PhotonVoice/Demos.meta").write_text("guid: demo-dir\n")
        config = self.source / "Fusion/Resources/PhotonAppSettings.asset"
        config.parent.mkdir(parents=True, exist_ok=True)
        config.write_text("source account config\n")
        (config.with_name(config.name + ".meta")).write_text("guid: source-config\n")
        network_config = self.source / "Fusion/Resources/NetworkProjectConfig.fusion"
        network_config.write_text("source fusion connection config\n")
        network_config.with_name(network_config.name + ".meta").write_text("guid: source-network-config\n")

    def run_bootstrap(self, *extra: str) -> int:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return bootstrap.main(["--project-root", str(self.project), "--source-photon", str(self.source), *extra])

    def test_fresh_import_preserves_bytes_and_target_account_config(self) -> None:
        target_config = self.project / "Assets/Photon/Fusion/Resources/PhotonAppSettings.asset"
        target_config.parent.mkdir(parents=True)
        target_config.write_text("target account config\n")
        target_network_config = self.project / "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion"
        target_network_config.write_text("target fusion connection config\n")
        target_network_meta = target_network_config.with_name(target_network_config.name + ".meta")
        target_network_meta.write_text("guid: target-network-config\n")
        folder_meta = self.project / "Assets/Photon/Fusion.meta"
        folder_meta.write_text("guid: target-fusion-folder\n")
        provenance = self.project.parent / "provenance.json"
        self.assertEqual(0, self.run_bootstrap("--apply", "--provenance", str(provenance)))
        for original in bootstrap.source_files(self.source):
            target = self.project / "Assets/Photon" / original.relative_to(self.source)
            if original.relative_to(self.source) == Path("Fusion.meta"):
                continue
            self.assertEqual(original.read_bytes(), target.read_bytes())
        self.assertEqual("target account config\n", target_config.read_text())
        self.assertEqual("target fusion connection config\n", target_network_config.read_text())
        self.assertEqual("guid: target-network-config\n", target_network_meta.read_text())
        self.assertEqual("guid: target-fusion-folder\n", folder_meta.read_text())
        artifact = json.loads(provenance.read_text())
        self.assertEqual(bootstrap.FUSION_VERSION, artifact["fusion"]["version"])
        self.assertIn("*.asset", artifact["excluded_source_file_kind"])
        self.assertIn("Existing target directory", artifact["preserved_target_directory_meta"])
        self.assertIn("Fusion/Resources/NetworkProjectConfig.fusion", artifact["preserved_target_files"])
        self.assertIn("PhotonVoice/changes-voice.txt", artifact["fusion"]["source_marker_sha256"])
        self.assertFalse((self.project / "Assets/Photon/PhotonVoice/Demos").exists())
        self.assertFalse((self.project / "Assets/Photon/PhotonVoice/Demos.meta").exists())

    def test_second_run_is_idempotent(self) -> None:
        self.assertEqual(0, self.run_bootstrap("--apply"))
        self.assertEqual(0, self.run_bootstrap("--apply"))

    def test_conflict_fails_without_writing_any_file(self) -> None:
        conflict = self.project / "Assets/Photon/Fusion/Runtime/Fusion.Unity.asmdef"
        conflict.parent.mkdir(parents=True)
        conflict.write_text("user modification\n")
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertEqual("user modification\n", conflict.read_text())
        self.assertFalse((self.project / "Assets/Photon/PhotonVoice").exists())

    def test_wrong_version_fails_without_writes(self) -> None:
        (self.source / "Fusion/build_info.txt").write_text("build: 2.0.8 Stable 1\n")
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertFalse((self.project / "Assets").exists())

    def test_nonexact_build_line_fails_without_writes(self) -> None:
        (self.source / "Fusion/build_info.txt").write_text("build: 2.0.9 Stable 15660\n")
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertFalse((self.project / "Assets").exists())

    def test_symlinked_selected_source_root_or_meta_is_rejected(self) -> None:
        outside = self.project.parent / "outside-source"
        outside.mkdir()
        shutil.rmtree(self.source / "PhotonVoice")
        (self.source / "PhotonVoice").symlink_to(outside, target_is_directory=True)
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertFalse((self.project / "Assets").exists())

        self.tearDown()
        self.setUp()
        meta = self.source / "PhotonVoice.meta"
        meta.unlink()
        meta.symlink_to(outside / "PhotonVoice.meta")
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertFalse((self.project / "Assets").exists())

    def test_symlinked_assets_or_provenance_is_rejected_without_writes(self) -> None:
        outside = self.project.parent / "outside"
        outside.mkdir()
        (self.project / "Assets").symlink_to(outside, target_is_directory=True)
        self.assertEqual(2, self.run_bootstrap("--apply"))
        self.assertEqual([], list(outside.iterdir()))

        clean = self.project.parent / "clean-project"
        clean.mkdir()
        link = clean / "provenance.json"
        link.symlink_to(outside / "provenance.json")
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            result = bootstrap.main(["--project-root", str(clean), "--source-photon", str(self.source),
                                     "--apply", "--provenance", str(link)])
        self.assertEqual(2, result)
        self.assertFalse((clean / "Assets").exists())


if __name__ == "__main__":
    unittest.main()
