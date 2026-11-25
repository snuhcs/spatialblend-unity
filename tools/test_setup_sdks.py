#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Tests for tools/setup_sdks.py. Filesystem and git only; no Unity, no network, no real SDK."""
from __future__ import annotations

import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import unittest.mock


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("setup_sdks", ROOT / "tools/setup_sdks.py")
sdk = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(sdk)

BOOTSTRAP_SPEC = importlib.util.spec_from_file_location("bootstrap_photon_sdk_t", ROOT / "tools/bootstrap-photon-sdk.py")
bootstrap = importlib.util.module_from_spec(BOOTSTRAP_SPEC)
assert BOOTSTRAP_SPEC and BOOTSTRAP_SPEC.loader
BOOTSTRAP_SPEC.loader.exec_module(bootstrap)

FAKE_APP_ID = "11111111-2222-3333-4444-555555555555"
FAKE_VOICE_ID = "66666666-7777-8888-9999-aaaaaaaaaaaa"
FAKE_SECRET = "not-a-real-secret-0123456789"


def sha(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()


def real_lock() -> dict:
    return json.loads((ROOT / "SDKS.lock").read_text(encoding="utf-8"))


def run_quiet(argv: list[str], **kwargs) -> tuple[int, str]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = sdk.run(argv, **kwargs)
    return code, out.getvalue() + err.getvalue()


class LoadToolTests(unittest.TestCase):
    def test_a_tool_with_a_dataclass_loads(self):
        # fetch_plugins.py defines a @dataclass; loading it without registering the module in sys.modules crashed on Python 3.9 and 3.14.
        module = sdk.load_tool("fetch_plugins.py", "fetch_plugins_load_tool_test")
        self.assertTrue(hasattr(module, "Plugin"))
        import sys
        self.assertIn("fetch_plugins_load_tool_test", sys.modules)
        sys.modules.pop("fetch_plugins_load_tool_test", None)


class LockParsingTests(unittest.TestCase):
    def test_real_lock_parses_and_covers_every_manifest_package(self) -> None:
        lock = sdk.parse_lock((ROOT / "SDKS.lock").read_text(encoding="utf-8"))
        manifest = json.loads((ROOT / "Packages/manifest.json").read_text(encoding="utf-8"))["dependencies"]
        locked = {entry["name"] for entry in sdk.entries_of(lock, "upm", "upm-git")}
        self.assertEqual(locked, set(manifest))

    def test_real_lock_covers_the_named_non_package_dependencies(self) -> None:
        lock = real_lock()
        names = {entry["name"] for entry in lock["entries"]}
        for wanted in ("photon-fusion", "photon-realtime", "photon-voice", "photon-libs", "photon-pun", "meta-avatars-samples", "meta-avatars-assets", "oculus-project-config", "libspatialblend"):
            self.assertIn(wanted, names)
        fusion = next(entry for entry in lock["entries"] if entry["name"] == "photon-fusion")
        self.assertEqual(fusion["version"], "2.0.9 Stable 1566")
        self.assertEqual(fusion["redistributable"], "no")
        self.assertTrue(all(entry.get("private_only") for entry in lock["entries"] if entry["kind"] in ("asset-store", "tracked-third-party")))

    def test_depth_api_is_pinned_to_an_exact_commit_everywhere(self) -> None:
        lock = real_lock()
        entry = next(entry for entry in lock["entries"] if entry["name"] == "com.meta.xr.depthapi")
        revision = entry["source"]["revision"]
        self.assertRegex(revision, r"^[0-9a-f]{40}$")
        manifest = json.loads((ROOT / "Packages/manifest.json").read_text(encoding="utf-8"))["dependencies"]
        resolved = json.loads((ROOT / "Packages/packages-lock.json").read_text(encoding="utf-8"))["dependencies"]
        self.assertTrue(manifest["com.meta.xr.depthapi"].endswith("#" + revision))
        self.assertEqual(resolved["com.meta.xr.depthapi"]["hash"], revision)

    def test_every_entry_has_a_license_and_a_redistribution_flag(self) -> None:
        for entry in real_lock()["entries"]:
            self.assertTrue(entry["license"].get("name"), entry["name"])
            self.assertIn(entry["redistributable"], sdk.REDISTRIBUTABLE)

    def test_rejects_bad_locks(self) -> None:
        good = real_lock()
        cases = {
            "not json": "{",
            "wrong schema": json.dumps({**good, "schema": 2}),
            "no entries": json.dumps({**good, "entries": []}),
        }
        for label, text in cases.items():
            with self.subTest(label), self.assertRaises(sdk.SetupError):
                sdk.parse_lock(text)
        bad = copy.deepcopy(good)
        bad["entries"][0]["redistributable"] = "maybe"
        with self.assertRaises(sdk.SetupError):
            sdk.parse_lock(json.dumps(bad))
        bad = copy.deepcopy(good)
        bad["entries"].append(copy.deepcopy(bad["entries"][0]))
        with self.assertRaises(sdk.SetupError):
            sdk.parse_lock(json.dumps(bad))
        bad = copy.deepcopy(good)
        next(e for e in bad["entries"] if e["kind"] == "upm-git")["source"]["revision"] = "main"
        with self.assertRaises(sdk.SetupError):
            sdk.parse_lock(json.dumps(bad))
        bad = copy.deepcopy(good)
        bad["entries"][0]["sha256"] = "abc"
        with self.assertRaises(sdk.SetupError):
            sdk.parse_lock(json.dumps(bad))


class Fixture(unittest.TestCase):
    """A small synthetic project plus a lock that describes it."""

    def setUp(self) -> None:
        # A developer's shell may waive a credential for a compile-only build; no test depends on that.
        for name in (sdk.ALLOW_NO_PHOTON_APP_ID, sdk.ALLOW_NO_VOICE_APP_ID, sdk.ALLOW_NO_TURN):
            saved = os.environ.pop(name, None)
            if saved is not None:
                self.addCleanup(os.environ.__setitem__, name, saved)
        self.temp = tempfile.TemporaryDirectory()
        base = Path(self.temp.name).resolve()
        self.root = base / "project"
        self.home = base / "home"
        self.home.mkdir()
        self.root.mkdir()
        (self.root / "Packages").mkdir()
        (self.root / "ProjectSettings").mkdir()
        (self.root / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 6000.0.56f1\n")
        (self.root / "Packages/manifest.json").write_text(json.dumps({"dependencies": {
            "com.unity.inputsystem": "1.14.2",
            "com.meta.xr.depthapi": "https://example.invalid/d.git?path=/p#" + "a" * 40}}))
        (self.root / "Packages/packages-lock.json").write_text(json.dumps({"dependencies": {
            "com.unity.inputsystem": {"version": "1.14.2"},
            "com.meta.xr.depthapi": {"version": "https://example.invalid/d.git?path=/p#" + "a" * 40, "hash": "a" * 40}}}))
        shutil.copytree(ROOT / "tools/templates", self.root / "tools/templates")
        (self.root / ".gitignore").write_text((ROOT / ".gitignore").read_text(encoding="utf-8"))
        # The "official download": a folder in the shape bootstrap-photon-sdk expects.
        self.source = base / "official" / "Assets" / "Photon"
        self.make_photon_source()
        markers = {str(rel): sha256_of(self.source / rel) for rel in bootstrap.MARKER_PATHS}
        self.lock = {
            "schema": 1,
            "unity_editor": {"version": "6000.0.56f1"},
            "entries": [
                {"name": "com.unity.inputsystem", "kind": "upm", "version": "1.14.2", "manifest_value": "1.14.2", "source": {"type": "upm-registry"},
                 "license": {"name": "UCL"}, "redistributable": "no"},
                {"name": "com.meta.xr.depthapi", "kind": "upm-git", "version": "77.0.0", "manifest_value": "https://example.invalid/d.git?path=/p#" + "a" * 40,
                 "source": {"type": "git", "revision": "a" * 40}, "license": {"name": "OSL"}, "redistributable": "no"},
                {"name": "photon-fusion", "kind": "photon", "version": "2.0.9 Stable 1566", "sha256": None, "install_dir": "Assets/Photon/Fusion",
                 "marker_sha256": {k: v for k, v in markers.items() if k.startswith("Fusion/")}, "source": {"type": "user-download"},
                 "license": {"name": "Photon"}, "redistributable": "no", "required": True},
                {"name": "photon-voice", "kind": "photon", "version": "2.63", "sha256": None, "install_dir": "Assets/Photon/PhotonVoice",
                 "marker_sha256": {k: v for k, v in markers.items() if not k.startswith("Fusion/")}, "source": {"type": "user-download"},
                 "license": {"name": "Photon"}, "redistributable": "no", "required": True},
                {"name": "meta-avatars-samples", "kind": "samples", "version": "35.2.0", "install_dir": "Assets/Samples/S", "source": {"type": "package-cache"},
                 "license": {"name": "MPT"}, "redistributable": "samples-with-notice"},
                {"name": "meta-avatars-assets", "kind": "avatar-assets", "version": "35.2.0", "required": True,
                 "source": {"type": "package-cache", "core_assets_build": "111", "sample_assets_build": "111",
                            "packaged_presets": ["PresetAvatars_Quest.zip", "PresetAvatars_Rift.zip"]},
                 "install_dirs": {"core": "Assets/Oculus/Avatar2/CoreAssets", "samples": "Assets/Oculus/Avatar2_SampleAssets/SampleAssets"},
                 "license": {"name": "MPT"}, "redistributable": "no"},
                {"name": "oculus-project-config", "kind": "project-config", "version": "x", "required": True,
                 "source": {"type": "committed-template", "template": "tools/templates/OculusProjectConfig.asset.template",
                            "meta_template": "tools/templates/OculusProjectConfig.asset.meta.template"},
                 "install_path": "Assets/Oculus/OculusProjectConfig.asset",
                 "license": {"name": "own"}, "redistributable": "yes"},
                {"name": "libspatialblend", "kind": "native", "version": "x", "required": True, "source": {"type": "ci"},
                 "license": {"name": "own"}, "redistributable": "no"},
            ],
        }
        self.write_lock()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def write_lock(self) -> None:
        (self.root / "SDKS.lock").write_text(json.dumps(self.lock))

    def make_photon_source(self) -> None:
        (self.source / "Fusion").mkdir(parents=True)
        (self.source / "Fusion/package.json").write_text(json.dumps({"name": "com.exitgames.photonfusion"}))
        (self.source / "Fusion/build_info.txt").write_text("build: 2.0.9 Stable 1566\n")
        for relative in bootstrap.REQUIRED_FUSION_PATHS:
            path = self.source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"official {relative}\n")
        for name in bootstrap.COPIED_ROOTS:
            (self.source / name).mkdir(parents=True, exist_ok=True)
            (self.source / f"{name}.meta").write_text(f"guid: {name}\n")
        (self.source / "PhotonVoice/changes-voice.txt").write_text("Photon Voice 2 Change Log\nv2.63\n")
        (self.source / "PhotonRealtime/Code/changes-realtime.txt").write_text("Version 4.1.8.17\n")

    def install_fake_native(self) -> None:
        for dest in sdk.native_required_files():
            path = self.root / dest
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"x")

    def install_fake_samples(self) -> None:
        directory = self.root / "Assets/Samples/S"
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "Oculus.AvatarSDK2.Samples.asmdef").write_text("{}")

    def install_fake_avatar_assets(self, build: str = "111") -> None:
        entry = next(e for e in self.lock["entries"] if e["kind"] == "avatar-assets")
        core = self.root / entry["install_dirs"]["core"]
        samples = self.root / entry["install_dirs"]["samples"]
        (samples / "SampleAssets").mkdir(parents=True, exist_ok=True)
        (samples / "SampleAssetsUnzipped").mkdir(parents=True, exist_ok=True)
        core.mkdir(parents=True, exist_ok=True)
        (core / ".avatar_sdk_assets_version.txt").write_bytes((build + "\n").encode("utf-16"))  # Unity wrote UTF-16
        (samples / "SampleAssetsUnzipped/.avatar_sdk_assets_version.txt").write_text(build + "\n")
        for name in entry["source"]["packaged_presets"]:
            (samples / "SampleAssets" / name).write_bytes(b"zip")

    def use_turn_worker(self) -> None:
        """A configured TURN Worker (your own; there is none by default), which makes the TURN client secret required."""
        self.assertEqual(sdk.write_turn_url(self.root, "https://turn.example.invalid/ice")[0], sdk.OK)

    def give_credentials(self) -> None:
        """The three required credentials: both Photon IDs in the (git-ignored) settings asset and the TURN secret under the user's home."""
        self.assertEqual(sdk.write_photon_ids(self.root, FAKE_APP_ID, FAKE_VOICE_ID)[0], sdk.OK)
        secret = self.home / sdk.TURN_SECRET_HOME_RELATIVE
        secret.parent.mkdir(parents=True, exist_ok=True)
        secret.write_text("fake-turn-secret-do-not-print\n")

    def full_install(self) -> None:
        shutil.copytree(self.source, self.root / "Assets/Photon", dirs_exist_ok=True)
        self.install_fake_native()
        self.install_fake_samples()
        self.install_fake_avatar_assets()
        sdk.install_project_config(self.root, sdk.parse_lock(json.dumps(self.lock)))
        (self.root / sdk.TMP_SETTINGS_RELATIVE).parent.mkdir(parents=True, exist_ok=True)
        (self.root / sdk.TMP_SETTINGS_RELATIVE).write_text("tmp\n")

    def run_cli(self, *argv: str) -> tuple[int, str]:
        return run_quiet(["--root-ignored-placeholder"][:0] + list(argv), root=self.root, home=self.home)


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class ManifestVersusLockTests(Fixture):
    def rows(self) -> dict[str, tuple[str, str]]:
        return {name: (state, detail) for name, state, detail in sdk.check_upm(self.root, sdk.parse_lock(json.dumps(self.lock)))}

    def test_matching_project_has_no_mismatch(self) -> None:
        self.assertTrue(all(state == sdk.OK for state, _ in self.rows().values()))

    def test_manifest_version_drift_is_a_mismatch(self) -> None:
        manifest = json.loads((self.root / "Packages/manifest.json").read_text())
        manifest["dependencies"]["com.unity.inputsystem"] = "1.13.0"
        (self.root / "Packages/manifest.json").write_text(json.dumps(manifest))
        self.assertEqual(self.rows()["com.unity.inputsystem"][0], sdk.MISMATCH)

    def test_resolved_version_drift_is_a_mismatch(self) -> None:
        resolved = json.loads((self.root / "Packages/packages-lock.json").read_text())
        resolved["dependencies"]["com.unity.inputsystem"]["version"] = "1.13.0"
        (self.root / "Packages/packages-lock.json").write_text(json.dumps(resolved))
        self.assertEqual(self.rows()["com.unity.inputsystem"][0], sdk.MISMATCH)

    def test_unpinned_git_dependency_is_a_mismatch(self) -> None:
        manifest = json.loads((self.root / "Packages/manifest.json").read_text())
        manifest["dependencies"]["com.meta.xr.depthapi"] = "https://example.invalid/d.git?path=/p"
        (self.root / "Packages/manifest.json").write_text(json.dumps(manifest))
        self.assertEqual(self.rows()["com.meta.xr.depthapi"][0], sdk.MISMATCH)

    def test_resolved_git_revision_drift_is_a_mismatch(self) -> None:
        resolved = json.loads((self.root / "Packages/packages-lock.json").read_text())
        resolved["dependencies"]["com.meta.xr.depthapi"]["hash"] = "b" * 40
        (self.root / "Packages/packages-lock.json").write_text(json.dumps(resolved))
        self.assertEqual(self.rows()["com.meta.xr.depthapi"][0], sdk.MISMATCH)

    def test_package_missing_from_the_lock_is_a_mismatch(self) -> None:
        manifest = json.loads((self.root / "Packages/manifest.json").read_text())
        manifest["dependencies"]["com.example.new"] = "1.0.0"
        (self.root / "Packages/manifest.json").write_text(json.dumps(manifest))
        self.assertEqual(self.rows()["com.example.new"][0], sdk.MISMATCH)

    def test_editor_version_drift(self) -> None:
        (self.root / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 6000.0.1f1\n")
        state, _ = sdk.check_editor(self.root, sdk.parse_lock(json.dumps(self.lock)))
        self.assertEqual(state, sdk.MISMATCH)


class Sha256Tests(Fixture):
    def test_check_sha256(self) -> None:
        file = self.root / "f.bin"
        file.write_bytes(b"abc")
        self.assertEqual(sdk.check_sha256(file, hashlib.sha256(b"abc").hexdigest()), hashlib.sha256(b"abc").hexdigest())
        self.assertEqual(sdk.check_sha256(file, None), hashlib.sha256(b"abc").hexdigest())
        with self.assertRaises(sdk.SetupError):
            sdk.check_sha256(file, "0" * 64)

    def make_package(self, destination: Path, marker_override: str | None = None, keep=None, with_dirs: bool = True) -> Path:
        """Pack self.source into a .unitypackage (gzip tar of <guid>/{asset,pathname}). ``keep`` filters by path relative to
        Assets/Photon; ``with_dirs=False`` leaves out folder entries, as the Asset Store packages do."""
        entries = []
        for index, path in enumerate(sorted(self.source.rglob("*"))):
            relative = path.relative_to(self.source.parent.parent)
            if keep is not None and not keep(path.relative_to(self.source).as_posix()):
                continue
            if path.is_dir() and not with_dirs:
                continue
            entries.append((f"{index:032x}", relative.as_posix(), path))
        destination.parent.mkdir(parents=True, exist_ok=True)
        with tarfile.open(destination, "w:gz") as archive:
            for guid, pathname, path in entries:
                data = pathname.encode()
                info = tarfile.TarInfo(f"{guid}/pathname")
                info.size = len(data)
                archive.addfile(info, io.BytesIO(data))
                if path.is_file():
                    content = path.read_bytes()
                    if marker_override and pathname.endswith("build_info.txt"):
                        content = marker_override.encode()
                    info = tarfile.TarInfo(f"{guid}/asset")
                    info.size = len(content)
                    archive.addfile(info, io.BytesIO(content))
        return destination

    def make_asset_store_cache(self, with_dirs: bool = False) -> tuple[Path, Path, Path]:
        """A fake Asset Store cache with the two official packages, and the lock entries that name them."""
        cache = self.root.parent / "cache" / "Photon Engine"
        fusion = self.make_package(cache / "ScriptingNetwork" / "Photon Fusion.unitypackage",
                                   keep=lambda r: r.startswith(("Fusion", "PhotonLibs")), with_dirs=with_dirs)
        voice = self.make_package(cache / "Editor ExtensionsAudio" / "Photon Voice 2.unitypackage",
                                  keep=lambda r: not r.startswith(("Fusion", "PhotonLibs")) and r != "", with_dirs=with_dirs)
        for entry in self.lock["entries"]:
            if entry["name"] == "photon-fusion":
                entry.update(asset_store_name="Photon Fusion", asset_store_file=fusion.name, sha256=sha256_of(fusion), size=fusion.stat().st_size)
            if entry["name"] == "photon-voice":
                entry.update(asset_store_name="Photon Voice 2", asset_store_file=voice.name, sha256=sha256_of(voice), size=voice.stat().st_size)
        self.write_lock()
        return cache, fusion, voice

    def test_unitypackage_with_the_locked_content_installs(self) -> None:
        package = self.make_package(self.root.parent / "photon.unitypackage")
        code, _ = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--photon-source", str(package))
        self.assertTrue((self.root / "Assets/Photon/Fusion/build_info.txt").is_file())
        photon_rows = sdk.check_photon_installed(self.root, sdk.parse_lock(json.dumps(self.lock)))
        self.assertTrue(all(state == sdk.OK for _n, state, _d in photon_rows), photon_rows)
        self.assertIn(code, (sdk.EXIT_OK, sdk.EXIT_MISSING))

    def test_package_sha256_in_the_lock_is_enforced(self) -> None:
        cache, fusion, _voice = self.make_asset_store_cache()
        fusion.write_bytes(fusion.read_bytes() + b"tampered")
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--asset-store-dir", str(cache))
        self.assertEqual(code, sdk.EXIT_MISMATCH, output)
        self.assertIn("sha256", output)
        self.assertFalse((self.root / "Assets/Photon").exists())

    def test_a_package_with_an_unlisted_hash_is_refused_even_when_given_explicitly(self) -> None:
        cache, _fusion, _voice = self.make_asset_store_cache()
        stranger = self.make_package(self.root.parent / "other.unitypackage")
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--photon-source", str(stranger))
        self.assertEqual(code, sdk.EXIT_MISMATCH, output)
        self.assertFalse((self.root / "Assets/Photon").exists())

    def test_asset_store_cache_is_found_verified_and_installed(self) -> None:
        cache, _fusion, _voice = self.make_asset_store_cache(with_dirs=False)
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--asset-store-dir", str(cache))
        self.assertIn("Photon Fusion sha256 matches the lock", output)
        self.assertIn("Photon Voice 2 sha256 matches the lock", output)
        for name in bootstrap.COPIED_ROOTS:
            self.assertTrue((self.root / "Assets/Photon" / name).is_dir(), name)
            self.assertTrue((self.root / "Assets/Photon" / f"{name}.meta").is_file(), name)  # made for the folder-less package
        rows = sdk.check_photon_installed(self.root, sdk.parse_lock(json.dumps(self.lock)))
        self.assertTrue(all(state == sdk.OK for _n, state, _d in rows), rows)
        before = {str(p): sha256_of(p) for p in (self.root / "Assets").rglob("*") if p.is_file()}
        self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--asset-store-dir", str(cache))
        self.assertEqual(before, {str(p): sha256_of(p) for p in (self.root / "Assets").rglob("*") if p.is_file()})

    def test_check_verifies_the_cache_without_installing(self) -> None:
        cache, _f, _v = self.make_asset_store_cache()
        code, output = self.run_cli("--check", "--asset-store-dir", str(cache))
        self.assertIn("download verified", output)
        self.assertFalse((self.root / "Assets/Photon").exists())

    def test_missing_cache_tells_the_user_what_to_do(self) -> None:
        self.make_asset_store_cache()
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--asset-store-dir", str(self.root.parent / "nowhere"))
        self.assertEqual(code, sdk.EXIT_MISSING)
        self.assertIn("Package Manager > My Assets", output)
        self.assertIn("Photon Fusion", output)
        self.assertIn("Photon Voice 2", output)

    def test_only_one_of_the_two_packages_is_not_enough(self) -> None:
        cache, _fusion, voice = self.make_asset_store_cache()
        voice.unlink()
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--asset-store-dir", str(cache))
        self.assertEqual(code, sdk.EXIT_MISSING)
        self.assertFalse((self.root / "Assets/Photon").exists())

    def test_cache_locations_per_platform(self) -> None:
        home = Path("/h")
        self.assertEqual(sdk.asset_store_cache_dirs({}, home, "darwin"), [home / "Library/Unity/Asset Store-5.x/Photon Engine"])
        self.assertEqual(sdk.asset_store_cache_dirs({}, home, "linux"), [home / ".local/share/unity3d/Asset Store-5.x/Photon Engine"])
        self.assertEqual(sdk.asset_store_cache_dirs({"APPDATA": "C:/Users/u/AppData/Roaming"}, home, "win32"),
                         [Path("C:/Users/u/AppData/Roaming") / "Unity/Asset Store-5.x/Photon Engine"])
        self.assertEqual(sdk.asset_store_cache_dirs({}, home, "win32"), [])

    def test_real_lock_names_both_official_packages(self) -> None:
        by_name = {e["name"]: e for e in real_lock()["entries"]}
        self.assertEqual(by_name["photon-fusion"]["sha256"], "7c7dea3845e3886d0a7c3a2bdceeb0817e5b2f8daf6961dff72ecbb671353435")
        self.assertEqual(by_name["photon-fusion"]["size"], 11378667)
        self.assertEqual(by_name["photon-voice"]["sha256"], "68d7caf1a9d637d1adeffe8cecddf8d0936a8e873a1dc8fc78c6a8c53f8d102f")
        self.assertEqual(by_name["photon-voice"]["size"], 95210477)
        self.assertEqual(by_name["photon-fusion"]["source"]["type"], "unity-asset-store")

    def test_download_with_other_content_is_a_mismatch_and_installs_nothing(self) -> None:
        (self.source / "PhotonVoice/changes-voice.txt").write_text("Photon Voice 2 Change Log\nv2.99\n")
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--photon-source", str(self.source))
        self.assertEqual(code, sdk.EXIT_MISMATCH, output)
        self.assertFalse((self.root / "Assets/Photon").exists())

    def test_wrong_fusion_build_is_refused(self) -> None:
        (self.source / "Fusion/build_info.txt").write_text("build: 2.0.8 Stable 1500\n")
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--photon-source", str(self.source))
        self.assertNotEqual(code, sdk.EXIT_OK)
        self.assertFalse((self.root / "Assets/Photon").exists())

    def test_path_traversal_in_a_package_is_rejected(self) -> None:
        package = self.root.parent / "evil.unitypackage"
        with tarfile.open(package, "w:gz") as archive:
            data = b"Assets/Photon/../../escape.txt"
            info = tarfile.TarInfo(f"{'0' * 32}/pathname")
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
            info = tarfile.TarInfo(f"{'0' * 32}/asset")
            info.size = 1
            archive.addfile(info, io.BytesIO(b"x"))
        with self.assertRaises(sdk.SetupError), tempfile.TemporaryDirectory() as scratch:
            sdk.extract_unitypackage(package, Path(scratch))
        self.assertFalse((self.root.parent / "escape.txt").exists())


class NativeInstalledTests(Fixture):
    def test_requires_core_and_session_for_android_and_the_host_desktop(self) -> None:
        names = {Path(dest).name for dest in sdk.native_required_files(["osx", "android"])}
        self.assertEqual(names, {"libspatialblend.so", "libspatialblend_session.so", "libspatialblend.dylib", "libspatialblend_session.dylib"})
        names = {Path(dest).name for dest in sdk.native_required_files(["windows", "android"])}
        self.assertEqual(names, {"libspatialblend.so", "libspatialblend_session.so", "libspatialblend.dll", "libspatialblend_session.dll"})

    def test_a_half_done_install_is_not_installed(self) -> None:
        required = sdk.native_required_files()
        self.assertFalse(sdk.native_installed(self.root))
        self.install_fake_native()
        self.assertTrue(sdk.native_installed(self.root))
        for dest in required:
            (self.root / dest).unlink()
            self.assertFalse(sdk.native_installed(self.root), dest)
            rows = sdk.check_native(self.root, self.lock)
            self.assertTrue(rows and all(state == sdk.MISSING for _n, state, _d in rows), rows)
            self.assertIn(Path(dest).name, rows[0][2])
            (self.root / dest).write_bytes(b"x")
        self.assertTrue(sdk.native_installed(self.root))

    def test_a_meta_file_alone_does_not_count(self) -> None:
        for dest in sdk.native_required_files():
            path = self.root / (dest + ".meta")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("guid: x\n")
        self.assertFalse(sdk.native_installed(self.root))


class CheckExitCodeTests(Fixture):
    def test_everything_installed_exits_zero(self) -> None:
        self.full_install()
        self.give_credentials()
        code, output = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_OK, output)
        self.assertIn("SDK setup summary", output)

    def test_missing_photon_exits_two(self) -> None:
        self.install_fake_native()
        self.install_fake_samples()
        code, _ = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING)

    def test_pin_drift_exits_one_even_with_something_missing(self) -> None:
        self.install_fake_native()
        manifest = json.loads((self.root / "Packages/manifest.json").read_text())
        manifest["dependencies"]["com.unity.inputsystem"] = "9.9.9"
        (self.root / "Packages/manifest.json").write_text(json.dumps(manifest))
        code, _ = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISMATCH)

    def test_installed_photon_of_another_version_exits_one(self) -> None:
        self.full_install()
        (self.root / "Assets/Photon/PhotonVoice/changes-voice.txt").write_text("v1.0\n")
        code, _ = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISMATCH)

    def test_check_changes_nothing_and_never_writes_user_values(self) -> None:
        self.full_install()
        before = sorted(str(p) for p in self.root.rglob("*"))
        os.environ[sdk.ENV_APP_ID] = FAKE_APP_ID
        try:
            self.run_cli("--check", "--photon-app-id", FAKE_APP_ID)
        finally:
            os.environ.pop(sdk.ENV_APP_ID, None)
        self.assertEqual(before, sorted(str(p) for p in self.root.rglob("*")))
        self.assertEqual(list(self.home.rglob("*")), [])

    def test_check_reports_every_required_credential_and_exits_two_when_one_is_missing(self) -> None:
        # Issue 197 (after issue 167): a missing required ID or credential fails --check, with how to supply each one.
        self.full_install()
        self.use_turn_worker()
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING, out)
        for name, hint in (("photon-app-id", "--photon-app-id"), ("photon-voice-app-id", "--photon-voice-app-id"),
                           ("turn-client-secret", "--turn-secret-file")):
            rows = [line for line in out.splitlines() if line.strip().startswith(name)]
            self.assertEqual(len(rows), 1, (name, out))
            self.assertIn("missing", rows[0])
            self.assertIn(hint, rows[0])
        self.assertIn("no voice", out)
        self.assertNotIn("fake-turn-secret", out)

    def test_each_missing_credential_alone_fails_check_and_all_present_passes(self) -> None:
        self.full_install()
        self.use_turn_worker()
        self.give_credentials()
        self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_OK)
        # no Voice App ID
        self.assertEqual(sdk.write_photon_ids(self.root, FAKE_APP_ID, None)[0], sdk.OK)
        (self.root / sdk.PHOTON_SETTINGS_RELATIVE).write_text(
            (self.root / sdk.PHOTON_SETTINGS_RELATIVE).read_text().replace(FAKE_VOICE_ID, ""))
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING, out)
        self.assertEqual(len([ln for ln in out.splitlines() if "missing" in ln and ln.strip().startswith("photon-voice-app-id")]), 1)
        # no TURN secret
        self.give_credentials()
        (self.home / sdk.TURN_SECRET_HOME_RELATIVE).write_text("")
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING, out)
        self.assertEqual(len([ln for ln in out.splitlines() if "missing" in ln and ln.strip().startswith("turn-client-secret")]), 1)

    def test_the_opt_out_variables_waive_one_credential_each_for_a_compile_only_check(self) -> None:
        self.full_install()
        self.use_turn_worker()
        env = {sdk.ALLOW_NO_PHOTON_APP_ID: "1", sdk.ALLOW_NO_VOICE_APP_ID: "1"}
        with unittest.mock.patch.dict(os.environ, env):
            code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING, "the TURN secret is still missing: " + out)
        with unittest.mock.patch.dict(os.environ, {**env, sdk.ALLOW_NO_TURN: "1"}):
            code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_OK, out)
        self.assertIn(sdk.ALLOW_NO_TURN, out)
        self.assertIn("waived", out)
        with unittest.mock.patch.dict(os.environ, {**env, sdk.ALLOW_NO_TURN: "true"}):
            self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_MISSING, "only the value 1 waives")

    def test_a_non_guid_fusion_id_and_a_blank_turn_secret_do_not_count_as_set(self) -> None:
        self.full_install()
        self.use_turn_worker()
        self.give_credentials()
        path = self.root / sdk.PHOTON_SETTINGS_RELATIVE
        path.write_text(path.read_text().replace(FAKE_APP_ID, "not-a-guid"))
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING, out)
        self.assertIn("not a GUID", out)
        self.give_credentials()
        (self.home / sdk.TURN_SECRET_HOME_RELATIVE).write_text(" \n")
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING, out)

    def test_the_scan_server_is_optional_and_never_fails_a_run(self) -> None:
        self.full_install()
        self.give_credentials()
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_OK, out)
        row = [line for line in out.splitlines() if line.strip().startswith("scan-server")]
        self.assertEqual(len(row), 1)
        self.assertIn("info", row[0])
        self.assertIn("no scan server by default", row[0])
        self.assertIn("spatialblend-local.json", row[0])

    def test_without_a_turn_worker_the_turn_secret_is_not_needed(self) -> None:
        # There is no default Worker: TURN is off, as the Editor guard sees it (RequiredCredentialsRuntime.TurnIsWanted).
        self.full_install()
        self.assertEqual(sdk.write_photon_ids(self.root, FAKE_APP_ID, FAKE_VOICE_ID)[0], sdk.OK)
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_OK, out)
        row = [line for line in out.splitlines() if line.strip().startswith("turn-client-secret")]
        self.assertEqual(len(row), 1, out)
        self.assertIn("not needed", row[0])
        # A Worker URL in the settings asset counts too (as for the Editor guard), unless WebRTC is off.
        asset = self.root / "Assets/Resources/SpatialBlendSettings.asset"
        asset.parent.mkdir(parents=True, exist_ok=True)
        asset.write_text("  useWebRtc: 1\n  turnCredentialsUrl: https://turn.example.invalid/ice\n")
        self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_MISSING, "a Worker in the settings asset needs its secret")
        asset.write_text("  useWebRtc: 0\n  turnCredentialsUrl: https://turn.example.invalid/ice\n")
        self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_OK, "WebRTC off: no TURN")
        asset.unlink()
        self.use_turn_worker()
        self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_MISSING, "a configured Worker needs its secret")

    def test_an_applying_run_reports_missing_credentials_but_only_require_user_vars_fails_it(self) -> None:
        self.full_install()
        code, out = self.run_cli("--non-interactive", "--skip-tmp")
        self.assertEqual(code, sdk.EXIT_OK, out)
        self.assertIn("photon-voice-app-id", out)
        self.assertEqual(self.run_cli("--non-interactive", "--skip-tmp", "--require-user-vars")[0], sdk.EXIT_MISSING)

    def test_missing_voice_app_id_is_reported_with_the_fusion_id_present(self) -> None:
        self.full_install()
        self.give_credentials()
        text = (self.root / sdk.PHOTON_SETTINGS_RELATIVE).read_text().replace(FAKE_VOICE_ID, "")
        (self.root / sdk.PHOTON_SETTINGS_RELATIVE).write_text(text)
        code, out = self.run_cli("--check")
        self.assertEqual(code, sdk.EXIT_MISSING)
        voice_row = [line for line in out.splitlines() if line.strip().startswith("photon-voice-app-id")]
        self.assertEqual(len(voice_row), 1)
        self.assertIn("missing", voice_row[0])
        self.assertIn("no voice", voice_row[0])
        self.assertEqual(sdk.write_photon_ids(self.root, None, FAKE_VOICE_ID)[0], sdk.OK)
        self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_OK)

    def test_bad_lock_is_a_usage_error(self) -> None:
        (self.root / "SDKS.lock").write_text("{")
        self.assertEqual(self.run_cli("--check")[0], sdk.EXIT_MISSING)


class IdempotenceTests(Fixture):
    def snapshot(self) -> dict[str, str]:
        return {str(p.relative_to(self.root)): sha256_of(p) for p in sorted(self.root.rglob("*")) if p.is_file()}

    def test_second_run_changes_nothing(self) -> None:
        secret_file = self.root.parent / "secret.txt"
        secret_file.write_text(FAKE_SECRET + "\n")
        args = ["--non-interactive", "--skip-native", "--skip-samples", "--photon-source", str(self.source),
                "--photon-app-id", FAKE_APP_ID, "--photon-voice-app-id", FAKE_VOICE_ID,
                "--turn-worker-url", "https://turn.example.invalid/ice", "--turn-secret-file", str(secret_file)]
        self.run_cli(*args)
        first = self.snapshot()
        first_home = {str(p): sha256_of(p) for p in self.home.rglob("*") if p.is_file()}
        self.assertIn("Assets/Photon/Fusion/Resources/PhotonAppSettings.asset", first)
        self.run_cli(*args)
        self.assertEqual(first, self.snapshot())
        self.assertEqual(first_home, {str(p): sha256_of(p) for p in self.home.rglob("*") if p.is_file()})

    def test_app_id_lands_in_the_asset_and_keeps_its_meta_guid(self) -> None:
        self.full_install()
        self.assertEqual(sdk.write_photon_ids(self.root, FAKE_APP_ID, None)[0], sdk.OK)
        meta = (self.root / sdk.PHOTON_SETTINGS_RELATIVE).with_name("PhotonAppSettings.asset.meta")
        guid = meta.read_text()
        text = (self.root / sdk.PHOTON_SETTINGS_RELATIVE).read_text()
        self.assertEqual(sdk.read_asset_field(text, "AppIdFusion"), FAKE_APP_ID)
        self.assertEqual(sdk.read_asset_field(text, "AppIdVoice"), "")
        self.assertIn("FusionDefaultGlobal", guid)
        sdk.write_photon_ids(self.root, "99999999-2222-3333-4444-555555555555", FAKE_VOICE_ID)
        self.assertEqual(meta.read_text(), guid)
        text = (self.root / sdk.PHOTON_SETTINGS_RELATIVE).read_text()
        self.assertEqual(sdk.read_asset_field(text, "AppIdVoice"), FAKE_VOICE_ID)

    def test_app_id_needs_photon_installed(self) -> None:
        self.assertEqual(sdk.write_photon_ids(self.root, FAKE_APP_ID, None)[0], sdk.SKIPPED)
        self.assertFalse((self.root / "Assets").exists())

    def test_default_worker_url_writes_no_file(self) -> None:
        self.assertEqual(sdk.write_turn_url(self.root, sdk.DEFAULT_TURN_WORKER_URL)[0], sdk.OK)
        self.assertFalse((self.root / sdk.TURN_URL_RELATIVE).exists())

    def test_default_url_matches_the_unity_constant(self) -> None:
        source = (ROOT / "Assets/SpatialBlend/Framework/Scripts/SpatialBlendSettings.cs").read_text(encoding="utf-8")
        match = re.search(r'DefaultTurnCredentialsUrl\s*=\s*"([^"]*)"', source)
        self.assertIsNotNone(match)
        self.assertEqual(match.group(1), sdk.DEFAULT_TURN_WORKER_URL)


class ValidationTests(unittest.TestCase):
    def test_guid_and_url_validation(self) -> None:
        self.assertEqual(sdk.validate_guid(FAKE_APP_ID.upper(), "id"), FAKE_APP_ID)
        for bad in ("", "abc", FAKE_APP_ID + "0", "not-a-guid-at-all"):
            with self.subTest(bad), self.assertRaises(sdk.SetupError):
                sdk.validate_guid(bad, "id")
        self.assertEqual(sdk.validate_url("https://a.example/ice"), "https://a.example/ice")
        for bad in ("http://a.example", "a.example", "https://", "https://a b"):
            with self.subTest(bad), self.assertRaises(sdk.SetupError):
                sdk.validate_url(bad)

    def test_template_secret_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as home:
            template = ROOT / "tools/templates/turn-client-secret.template"
            with self.assertRaises(sdk.SetupError):
                sdk.write_turn_secret(Path(home), template, None)
            with self.assertRaises(sdk.SetupError):
                sdk.write_turn_secret(Path(home), None, "   ")


class AvatarAssetsAndProjectConfigTests(Fixture):
    def rows(self) -> dict[str, tuple[str, str]]:
        lock = sdk.parse_lock(json.dumps(self.lock))
        return {name: (state, detail) for name, state, detail in sdk.check_avatar_assets(self.root, lock) + sdk.check_project_config(self.root, lock)}

    def test_nothing_installed_is_missing(self) -> None:
        rows = self.rows()
        self.assertEqual(rows["meta-avatars-assets"][0], sdk.MISSING)
        self.assertEqual(rows["oculus-project-config"][0], sdk.MISSING)

    def test_installed_assets_match_including_a_utf16_version_file(self) -> None:
        self.install_fake_avatar_assets()
        self.assertEqual(self.rows()["meta-avatars-assets"][0], sdk.OK)

    def test_another_build_is_a_mismatch(self) -> None:
        self.install_fake_avatar_assets(build="999")
        self.assertEqual(self.rows()["meta-avatars-assets"][0], sdk.MISMATCH)

    def test_a_missing_packaged_preset_is_missing(self) -> None:
        self.install_fake_avatar_assets()
        entry = next(e for e in self.lock["entries"] if e["kind"] == "avatar-assets")
        (self.root / entry["install_dirs"]["samples"] / "SampleAssets/PresetAvatars_Rift.zip").unlink()
        state, detail = self.rows()["meta-avatars-assets"]
        self.assertEqual(state, sdk.MISSING)
        self.assertIn("PresetAvatars_Rift.zip", detail)

    def test_project_config_is_written_from_the_template_when_absent(self) -> None:
        sdk.install_project_config(self.root, sdk.parse_lock(json.dumps(self.lock)))
        target = self.root / "Assets/Oculus/OculusProjectConfig.asset"
        self.assertEqual(target.read_text(), (ROOT / "tools/templates/OculusProjectConfig.asset.template").read_text())
        self.assertTrue(target.with_name(target.name + ".meta").is_file())
        self.assertEqual(self.rows()["oculus-project-config"][0], sdk.OK)

    def test_an_existing_project_config_is_never_overwritten(self) -> None:
        target = self.root / "Assets/Oculus/OculusProjectConfig.asset"
        target.parent.mkdir(parents=True)
        target.write_text("edited by the Meta tools\n")
        sdk.install_project_config(self.root, sdk.parse_lock(json.dumps(self.lock)))
        self.assertEqual(target.read_text(), "edited by the Meta tools\n")
        self.assertFalse(target.with_name(target.name + ".meta").exists())

    def test_check_does_not_install_either(self) -> None:
        self.run_cli("--check")
        self.assertFalse((self.root / "Assets/Oculus").exists())

    def test_apply_installs_the_project_config_and_reports_missing_assets_without_a_cache(self) -> None:
        empty_cache = self.root.parent / "no-cache"
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--package-cache-dir", str(empty_cache))
        self.assertTrue((self.root / "Assets/Oculus/OculusProjectConfig.asset").is_file())
        self.assertRegex(output, r"meta-avatars-assets\s+missing")
        self.assertNotEqual(code, 0)

    def test_skip_flag_leaves_both_alone(self) -> None:
        self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--skip-avatar-assets")
        self.assertFalse((self.root / "Assets/Oculus").exists())

    def test_real_lock_entries_are_consistent_with_the_repo(self) -> None:
        lock = real_lock()
        config = next(e for e in lock["entries"] if e["name"] == "oculus-project-config")
        for key in ("template", "meta_template"):
            self.assertTrue((ROOT / config["source"][key]).is_file(), config["source"][key])
        assets = next(e for e in lock["entries"] if e["name"] == "meta-avatars-assets")
        self.assertTrue((ROOT / assets["source"]["tool"]).is_file())
        self.assertEqual(assets["redistributable"], "no")
        # Everything these two entries install is git-ignored, so none of it can be committed by accident.
        paths = [config["install_path"], config["install_path"] + ".meta", assets["install_dirs"]["core"] + "/x", assets["install_dirs"]["samples"] + "/x"]
        for relative in paths:
            result = subprocess.run(["git", "check-ignore", "-q", "--no-index", relative], cwd=ROOT)
            self.assertEqual(result.returncode, 0, f"{relative} is not git-ignored")

    def test_project_config_template_holds_no_secret_or_id(self) -> None:
        text = (ROOT / "tools/templates/OculusProjectConfig.asset.template").read_text()
        self.assertNotRegex(text, r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
        self.assertNotRegex(text.lower(), r"secret|token|password|appid|app_id|api[_-]?key")
        self.assertEqual(subprocess.run(["git", "check-ignore", "-q", "--no-index", "tools/templates/OculusProjectConfig.asset.template"], cwd=ROOT).returncode, 1)


class TmpEssentialsTests(Fixture):
    def tmp_row(self, output: str) -> str:
        return next(line for line in output.splitlines() if "tmp-essential-resources" in line)

    def test_check_reports_missing_and_changes_nothing(self) -> None:
        code, output = self.run_cli("--check")
        self.assertRegex(self.tmp_row(output), r"missing")
        self.assertFalse((self.root / "Assets/TextMesh Pro").exists())

    def test_present_settings_are_ok_and_idempotent_without_unity(self) -> None:
        settings = self.root / sdk.TMP_SETTINGS_RELATIVE
        settings.parent.mkdir(parents=True)
        settings.write_text("x")
        for flags in (["--check"], ["--non-interactive", "--skip-native", "--skip-samples", "--skip-avatar-assets"]):
            _code, output = self.run_cli(*flags, "--unity-bin", "/nonexistent/Unity")
            self.assertRegex(self.tmp_row(output), r"\bok\b")

    def test_apply_without_a_pinned_package_does_not_start_unity(self) -> None:
        marker = self.root.parent / "unity-was-started"
        fake = self.root.parent / "fake-unity"
        fake.write_text(f"#!/bin/sh\ntouch '{marker}'\n")
        fake.chmod(0o755)
        _code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--skip-avatar-assets", "--unity-bin", str(fake))
        self.assertRegex(self.tmp_row(output), r"missing.*neither")
        self.assertFalse(marker.exists())

    def test_apply_runs_the_import_method_in_batchmode_through_the_clean_env(self) -> None:
        manifest = self.root / "Packages/manifest.json"
        data = json.loads(manifest.read_text())
        data["dependencies"]["com.unity.ugui"] = "2.0.0"
        manifest.write_text(json.dumps(data))
        shutil.copytree(ROOT / "tools/lib", self.root / "tools/lib")
        script = self.root / sdk.TMP_IMPORT_SCRIPT
        script.parent.mkdir(parents=True)
        script.write_text("// stub\n")
        args_file = self.root.parent / "unity-args"
        settings = self.root / sdk.TMP_SETTINGS_RELATIVE
        fake = self.root.parent / "fake-unity"
        fake.write_text(f"#!/bin/sh\necho \"$@\" > '{args_file}'\nmkdir -p '{settings.parent}'\ntouch '{settings}'\n")
        fake.chmod(0o755)
        _code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--skip-avatar-assets", "--unity-bin", str(fake))
        self.assertRegex(self.tmp_row(output), r"\bok\b")
        called = args_file.read_text()
        for expected in ("-batchmode", "-nographics", "-executeMethod " + sdk.TMP_IMPORT_METHOD, "-projectPath " + str(self.root)):
            self.assertIn(expected, called)

    def test_a_failing_import_is_reported_missing(self) -> None:
        manifest = self.root / "Packages/manifest.json"
        data = json.loads(manifest.read_text())
        data["dependencies"]["com.unity.ugui"] = "2.0.0"
        manifest.write_text(json.dumps(data))
        shutil.copytree(ROOT / "tools/lib", self.root / "tools/lib")
        script = self.root / sdk.TMP_IMPORT_SCRIPT
        script.parent.mkdir(parents=True)
        script.write_text("// stub\n")
        fake = self.root.parent / "fake-unity"
        fake.write_text("#!/bin/sh\nexit 1\n")
        fake.chmod(0o755)
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--skip-avatar-assets", "--unity-bin", str(fake))
        self.assertRegex(self.tmp_row(output), r"missing.*exited 1")
        self.assertNotEqual(code, 0)

    def test_the_import_script_and_its_method_exist_in_the_repo_and_assets_stay_ignored(self) -> None:
        self.assertIn("public static void Run", (ROOT / sdk.TMP_IMPORT_SCRIPT).read_text())
        self.assertIn("class TmpEssentialsImport", (ROOT / sdk.TMP_IMPORT_SCRIPT).read_text())
        result = subprocess.run(["git", "check-ignore", "-q", "--no-index", str(sdk.TMP_SETTINGS_RELATIVE)], cwd=ROOT)
        self.assertEqual(result.returncode, 0)
        # The pinned package must be one that ships the resources.
        manifest = json.loads((ROOT / "Packages/manifest.json").read_text())["dependencies"]
        self.assertTrue(any(package in manifest for package in sdk.TMP_PACKAGES))


class SecretsStayInIgnoredPathsTests(Fixture):
    def git(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(["git", "-c", "core.autocrlf=false", *args], cwd=self.root, capture_output=True, text=True)

    def test_every_path_the_tool_can_write_is_git_ignored_in_this_repo(self) -> None:
        for relative in sdk.written_repo_paths():
            result = subprocess.run(["git", "check-ignore", "-q", "--no-index", str(relative)], cwd=ROOT)
            self.assertEqual(result.returncode, 0, f"{relative} is not git-ignored")

    def test_templates_are_committed_not_ignored(self) -> None:
        for template in sdk.PHOTON_SETTINGS_TEMPLATE, sdk.PHOTON_SETTINGS_META_TEMPLATE, sdk.TEMPLATE_DIR / "turn-client-secret.template":
            result = subprocess.run(["git", "check-ignore", "-q", "--no-index", str(template)], cwd=ROOT)
            self.assertEqual(result.returncode, 1, f"{template} would be ignored")
            self.assertTrue((ROOT / template).is_file())

    def test_templates_hold_no_real_values(self) -> None:
        asset = (ROOT / sdk.PHOTON_SETTINGS_TEMPLATE).read_text()
        self.assertIn("@APP_ID_FUSION@", asset)
        self.assertNotRegex(asset, r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
        self.assertIn(sdk.TEMPLATE_SECRET_MARKER, (ROOT / sdk.TEMPLATE_DIR / "turn-client-secret.template").read_text())

    def test_after_a_full_run_git_sees_no_new_file_and_values_sit_where_expected(self) -> None:
        if self.git("init", "-q").returncode != 0:
            self.skipTest("git unavailable")
        self.git("add", "-A")
        self.git("-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "base")
        secret_file = self.root.parent / "secret.txt"
        secret_file.write_text(FAKE_SECRET + "\n")
        code, output = self.run_cli("--non-interactive", "--skip-native", "--skip-samples", "--photon-source", str(self.source),
                                    "--photon-app-id", FAKE_APP_ID, "--photon-voice-app-id", FAKE_VOICE_ID,
                                    "--turn-worker-url", "https://turn.example.invalid/ice", "--turn-secret-file", str(secret_file))
        self.assertNotIn(FAKE_APP_ID, output)
        self.assertNotIn(FAKE_SECRET, output)
        # SDK files and user values are all ignored: git sees nothing new.
        self.assertEqual(self.git("status", "--porcelain", "--untracked-files=all").stdout.strip(), "")
        # The values exist exactly where intended.
        self.assertIn(FAKE_APP_ID, (self.root / sdk.PHOTON_SETTINGS_RELATIVE).read_text())
        self.assertIn("turn.example.invalid", (self.root / sdk.TURN_URL_RELATIVE).read_text())
        stored = self.home / sdk.TURN_SECRET_HOME_RELATIVE
        self.assertEqual(stored.read_text().strip(), FAKE_SECRET)
        self.assertEqual(stored.stat().st_mode & 0o777, 0o600)
        # No file in the repo (ignored or not) other than the settings asset holds the App ID, and none holds the secret.
        for path in self.root.rglob("*"):
            if path.is_file() and ".git" not in path.parts:
                text = path.read_text(errors="ignore")
                self.assertNotIn(FAKE_SECRET, text, path)
                if FAKE_APP_ID in text:
                    self.assertEqual(path.relative_to(self.root), sdk.PHOTON_SETTINGS_RELATIVE)


if __name__ == "__main__":
    unittest.main()
