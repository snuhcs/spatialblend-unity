#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Filesystem tests for bootstrap-meta-avatar-assets.py. A fake package cache stands in for Meta's packages; no Unity, no network."""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("bootstrap_meta_avatar_assets", ROOT / "tools/bootstrap-meta-avatar-assets.py")
tool = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(tool)


def write_package(cache: Path, pin: dict, build: str, *, utf16_build: bool = False, fingerprint: str | None = None) -> Path:
    package = cache / f"{pin['name']}@{pin['fingerprint'][:12]}"
    package.mkdir(parents=True)
    (package / "package.json").write_text(json.dumps({"name": pin["name"], "version": pin["version"],
                                                      "_fingerprint": fingerprint or pin["fingerprint"]}))
    (package / "build_number.txt").write_bytes((build + "\n").encode("utf-16") if utf16_build else (build + "\n").encode())
    return package


def make_cache(base: Path, *, build: str = "111", utf16_build: bool = False) -> Path:
    cache = base / "cache"
    avatars = write_package(cache, tool.AVATARS, build, utf16_build=utf16_build)
    (avatars / "CoreAssets/Textures").mkdir(parents=True)
    (avatars / "CoreAssets/Textures/lut.png").write_bytes(b"png")
    (avatars / "CoreAssets/Textures.meta").write_text("guid: a\n")
    samples = write_package(cache, tool.SAMPLES, build, utf16_build=utf16_build)
    for name in ("PresetAvatars_Quest", "PresetAvatars_Rift"):
        folder = samples / "SampleAssets/SampleAssetsUnzipped" / name
        folder.mkdir(parents=True)
        (folder / "0_x.glb").write_bytes(name.encode())
        (folder / "0_x.glb.meta").write_text("guid: m\n")
        (folder.parent / f"{name}.meta").write_text("guid: f\n")
    return cache


class BootstrapAvatarAssetsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.project = self.base / "project"
        self.project.mkdir()
        self.cache = make_cache(self.base)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def main(self, *extra: str, cache: Path | None = None) -> tuple[int, str]:
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = tool.main(["--project-root", str(self.project), "--package-cache-dir", str(cache or self.cache), *extra])
        return code, out.getvalue() + err.getvalue()

    def test_plan_without_apply_writes_nothing(self) -> None:
        code, output = self.main()
        self.assertEqual(code, 0)
        self.assertIn("would-install", output)
        self.assertFalse((self.project / "Assets").exists())

    def test_apply_installs_core_assets_samples_and_zips(self) -> None:
        code, _ = self.main("--apply")
        self.assertEqual(code, 0)
        core = self.project / tool.CORE_DEST
        self.assertEqual((core / "Textures/lut.png").read_bytes(), b"png")
        self.assertTrue((core / "Textures.meta").is_file(), "the package's .meta files are copied too")
        self.assertEqual((core / tool.VERSION_FILE).read_text().strip(), "111")
        samples = self.project / tool.SAMPLES_DEST
        self.assertEqual((samples / "SampleAssetsUnzipped" / tool.VERSION_FILE).read_text().strip(), "111")
        for name in ("PresetAvatars_Quest", "PresetAvatars_Rift"):
            archive = zipfile.ZipFile(samples / "SampleAssets" / f"{name}.zip")
            self.assertEqual(archive.namelist(), ["0_x.glb"], "only the .glb, never the .meta")
            self.assertEqual(archive.read("0_x.glb"), name.encode())

    def test_second_run_is_idempotent_and_touches_nothing(self) -> None:
        self.main("--apply")
        marker = self.project / tool.CORE_DEST / "Textures/lut.png"
        marker.write_bytes(b"edited")
        zip_path = self.project / tool.SAMPLES_DEST / "SampleAssets/PresetAvatars_Rift.zip"
        before = zip_path.stat().st_mtime_ns
        code, output = self.main("--apply")
        self.assertEqual(code, 0)
        self.assertIn('"core_assets": "present"', output)
        self.assertEqual(marker.read_bytes(), b"edited")
        self.assertEqual(zip_path.stat().st_mtime_ns, before)

    def test_a_missing_zip_is_repackaged_without_touching_the_rest(self) -> None:
        self.main("--apply")
        zip_path = self.project / tool.SAMPLES_DEST / "SampleAssets/PresetAvatars_Rift.zip"
        zip_path.unlink()
        self.assertEqual(self.main("--apply")[0], 0)
        self.assertTrue(zip_path.is_file())

    def test_an_install_of_another_build_is_not_replaced(self) -> None:
        self.main("--apply")
        other = make_cache(self.base / "other", build="222")
        code, output = self.main("--apply", cache=other)
        self.assertEqual(code, 2)
        self.assertIn("not replacing", output)
        self.assertEqual((self.project / tool.CORE_DEST / tool.VERSION_FILE).read_text().strip(), "111")

    def test_utf16_build_numbers_are_read(self) -> None:
        cache = make_cache(self.base / "u16", build="333", utf16_build=True)
        code, _ = self.main("--apply", cache=cache)
        self.assertEqual(code, 0)
        self.assertEqual((self.project / tool.CORE_DEST / tool.VERSION_FILE).read_text().strip(), "333")

    def test_wrong_fingerprint_is_refused_and_installs_nothing(self) -> None:
        (self.cache / f"{tool.AVATARS['name']}@{tool.AVATARS['fingerprint'][:12]}/package.json").write_text(
            json.dumps({"name": tool.AVATARS["name"], "version": tool.AVATARS["version"], "_fingerprint": "0" * 40}))
        code, output = self.main("--apply")
        self.assertEqual(code, 2)
        self.assertIn("fingerprint", output)
        self.assertFalse((self.project / "Assets").exists())

    def test_missing_cache_is_an_error(self) -> None:
        code, output = self.main("--apply", cache=self.base / "nowhere")
        self.assertEqual(code, 2)
        self.assertIn("expected exactly one cached", output)

    def test_avatars_glob_does_not_match_the_sample_assets_package(self) -> None:
        # com.meta.xr.sdk.avatars@* must not also match com.meta.xr.sdk.avatars.sample.assets@*
        self.assertEqual(tool.find_package(self.cache, tool.AVATARS).name.split("@")[0], tool.AVATARS["name"])

    def test_a_symlinked_destination_is_refused(self) -> None:
        elsewhere = self.base / "elsewhere"
        elsewhere.mkdir()
        (self.project / "Assets").mkdir()
        (self.project / "Assets/Oculus").symlink_to(elsewhere)
        code, output = self.main("--apply")
        self.assertEqual(code, 2)
        self.assertIn("symlink", output)
        self.assertEqual(list(elsewhere.iterdir()), [])

    def test_pins_match_the_lock(self) -> None:
        lock = json.loads((ROOT / "SDKS.lock").read_text(encoding="utf-8"))
        entry = next(e for e in lock["entries"] if e["name"] == "meta-avatars-assets")
        pins = {p["package"]: p["fingerprint"] for p in entry["source"]["packages"]}
        self.assertEqual(pins, {tool.AVATARS["name"]: tool.AVATARS["fingerprint"], tool.SAMPLES["name"]: tool.SAMPLES["fingerprint"]})
        self.assertEqual(entry["version"], tool.AVATARS["version"])


if __name__ == "__main__":
    unittest.main()
