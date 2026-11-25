#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Filesystem tests for the plugin fetcher. Does not call GitHub or Unity."""

from __future__ import annotations

import hashlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import fetch_plugins as fetch  # noqa: E402


ROOT = Path(__file__).resolve().parents[1]
CONFIG = (
    ROOT
    / "Assets/SpatialBlend/Foundation/Internal/SpatialBlendLibraryConfig.cs"
)


def _digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


class FetchCorePluginsTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.project = self.root / "project"
        self.project.mkdir()

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def _rel(self, path: Path) -> str:
        return path.resolve().relative_to(self.project.resolve()).as_posix()

    def test_default_is_host_plus_android(self) -> None:
        names = [plugin.platform for plugin in fetch.select_plugins(None)]
        self.assertEqual(names, fetch.default_platform_names())
        self.assertIn("android", names)
        self.assertFalse({"osx", "windows"} <= set(names))
        windows_only = [plugin.platform for plugin in fetch.select_plugins(["windows"])]
        self.assertEqual(windows_only, ["windows"])

    def test_config_names_match_the_fetcher(self) -> None:
        text = CONFIG.read_text(encoding="utf-8")
        for plugin in fetch.PLUGINS:
            self.assertIn(plugin.filename, text)
            self.assertIn(plugin.dest, text)
        self.assertIn("SHA256SUMS", text)
        self.assertIn(f'RELEASE_REPOSITORY = "{fetch.CORE_RELEASE_REPO}"', text)
        self.assertIn(f'RELEASE_TAG = "{fetch.CORE_RELEASE_TAG}"', text)

    def test_parse_sha256sums_and_bare_digest(self) -> None:
        plugin = fetch.plugins_by_platform()["android"]
        digest = "a" * 64
        sums = fetch.parse_sha256sums(f"{digest}  {plugin.filename}\n")
        self.assertEqual(fetch.checksum_for(sums, plugin.filename), digest)
        bare = fetch.parse_sha256sums(digest.upper() + "\n")
        self.assertEqual(fetch.checksum_for(bare, plugin.filename), digest)

    def test_bad_checksum_installs_nothing(self) -> None:
        plugins = fetch.PLUGINS
        payloads = {plugin.platform: f"bytes-{plugin.platform}".encode() for plugin in plugins}

        def obtain(plugin: fetch.Plugin):
            data = payloads[plugin.platform]
            published = _digest(data)
            if plugin.platform == "osx":
                published = "b" * 64
            return data, published

        with self.assertRaises(fetch.ChecksumError):
            fetch.collect(plugins, obtain, {})
        self.assertFalse((self.project / plugins[0].dest).exists())

    def test_pin_must_match_before_install(self) -> None:
        plugin = fetch.plugins_by_platform()["windows"]
        data = b"dll-bytes"
        digest = _digest(data)

        def obtain(item: fetch.Plugin):
            return data, digest

        with self.assertRaises(fetch.ChecksumError):
            fetch.collect((plugin,), obtain, {"windows": "c" * 64})
        staged = fetch.collect((plugin,), obtain, {"windows": digest})
        written = fetch.install_staged(self.project, staged)
        self.assertEqual(written[0].read_bytes(), data)
        self.assertEqual(self._rel(written[0]), plugin.dest)

    def test_installs_all_three_plugin_directories(self) -> None:
        staged = []
        for plugin in fetch.PLUGINS:
            data = plugin.filename.encode()
            fetch.verify_bytes(data, _digest(data), None, plugin.filename)
            staged.append((plugin, data))
        written = fetch.install_staged(self.project, staged)
        self.assertEqual(
            [self._rel(path) for path in written],
            [plugin.dest for plugin in fetch.PLUGINS],
        )

    def test_release_urls_and_sidecar_fallback(self) -> None:
        plugin = fetch.plugins_by_platform()["android"]
        library, sums, sidecar = fetch.release_urls(
            "https://example.test/releases/v2/", plugin
        )
        self.assertEqual(library, "https://example.test/releases/v2/libspatialblend.so")
        self.assertTrue(sums.endswith("/SHA256SUMS"))
        data = b"so-bytes"
        digest = _digest(data)

        def fetch_url(url: str) -> bytes:
            if url == library:
                return data
            if url == sums:
                raise fetch.FetchError("missing sums")
            if url == sidecar:
                return f"{digest}  {plugin.filename}\n".encode()
            raise AssertionError(url)

        got, published = fetch.fetch_release(
            "https://example.test/releases/v2", plugin, fetch_url
        )
        self.assertEqual(got, data)
        self.assertEqual(published, digest)

    def test_from_dir_cli_verifies_then_installs(self) -> None:
        plugin = fetch.plugins_by_platform()["android"]
        data = b"android-so"
        digest = _digest(data)
        source = self.root / "artifact"
        source.mkdir()
        (source / plugin.filename).write_bytes(data)
        (source / "SHA256SUMS").write_text(
            f"{digest}  {plugin.filename}\n", encoding="utf-8"
        )
        code = fetch.main(
            [
                "--project-root",
                str(self.project),
                "--library",
                "core",
                "--from-dir",
                str(source),
                "--platform",
                "android",
                "--pin",
                f"android={digest}",
            ]
        )
        self.assertEqual(code, 0)
        installed = self.project / plugin.dest
        self.assertEqual(installed.read_bytes(), data)
        self.assertFalse((self.project / "Assets/Plugins/OSX/libspatialblend.dylib").exists())

    def test_from_dir_rejects_a_tampered_library(self) -> None:
        plugin = fetch.plugins_by_platform()["osx"]
        source = self.root / "artifact"
        source.mkdir()
        (source / "lib" / plugin.filename).parent.mkdir()
        (source / "lib" / plugin.filename).write_bytes(b"tampered")
        (source / "SHA256SUMS").write_text(f"{'ab' * 32}  {plugin.filename}\n", encoding="utf-8")
        code = fetch.main(
            [
                "--project-root",
                str(self.project),
                "--library",
                "core",
                "--from-dir",
                str(source),
                "--platform",
                "osx",
            ]
        )
        self.assertEqual(code, 1)
        self.assertFalse((self.project / plugin.dest).exists())

    def test_an_empty_release_url_installs_nothing(self) -> None:
        code = fetch.main(
            [
                "--project-root",
                str(self.project),
                "--library",
                "core",
                "--release-url",
                "",
            ]
        )
        self.assertEqual(code, 1)

    def test_the_default_source_is_the_public_release(self) -> None:
        self.assertEqual(fetch.CORE_RELEASE_URL, "https://github.com/snuhcs/spatialblend-core/releases/download/" + fetch.CORE_RELEASE_TAG)
        self.assertEqual(
            fetch.SESSION_RELEASE_URL,
            "https://github.com/snuhcs/spatialblend-reconstruction/releases/download/" + fetch.SESSION_RELEASE_TAG,
        )
        seen = []

        def fake_get(url: str) -> bytes:
            seen.append(url)
            raise fetch.FetchError("offline")

        original = fetch.default_fetch_url
        fetch.default_fetch_url = fake_get
        try:
            code = fetch.main(["--project-root", str(self.project), "--platform", "android"])
        finally:
            fetch.default_fetch_url = original
        self.assertEqual(code, 1)
        self.assertEqual(seen[0], fetch.CORE_RELEASE_URL + "/libspatialblend.so")

    def test_a_release_without_sha256sums_uses_the_sidecar_and_installs(self) -> None:
        plugin = fetch.plugins_by_platform("session")["osx"]
        data = b"session-osx"
        files = {
            fetch.SESSION_RELEASE_URL + "/" + plugin.filename: data,
            fetch.SESSION_RELEASE_URL + "/" + plugin.filename + ".sha256": f"{_digest(data)}  {plugin.filename}\n".encode(),
        }

        def fake_get(url: str) -> bytes:
            if url in files:
                return files[url]
            raise fetch.FetchError("404 " + url)

        original = fetch.default_fetch_url
        fetch.default_fetch_url = fake_get
        try:
            code = fetch.main(["--project-root", str(self.project), "--library", "session", "--platform", "osx"])
        finally:
            fetch.default_fetch_url = original
        self.assertEqual(code, 0)
        self.assertEqual((self.project / plugin.dest).read_bytes(), data)

    def test_the_public_fetcher_has_no_ci_route(self) -> None:
        text = (ROOT / "tools/fetch_plugins.py").read_text(encoding="utf-8")
        for word in ("gh run", "--run-id", "--repo", "workflow", "actions"):
            self.assertNotIn(word, text)

    # --- spatialblend_session -------------------------------------------------

    def _session_dir(self, name: str, platforms=("android",)) -> Path:
        source = self.root / name
        source.mkdir()
        lines = []
        for platform in platforms:
            plugin = fetch.plugins_by_platform("session")[platform]
            data = f"session-{platform}".encode()
            (source / plugin.filename).write_bytes(data)
            lines.append(f"{_digest(data)}  {plugin.filename}")
        (source / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")
        return source

    def test_session_plugins_use_their_own_names(self) -> None:
        session = fetch.SESSION_PLUGINS
        self.assertEqual({p.platform for p in session}, {p.platform for p in fetch.PLUGINS})
        for plugin in session:
            self.assertEqual(plugin.library, "session")
            self.assertIn("libspatialblend_session", plugin.filename)
            self.assertIn("libspatialblend_session", plugin.dest)
            self.assertTrue(plugin.dest.startswith("Assets/Plugins/"))
        dests = [p.dest for p in fetch.PLUGINS + session]
        self.assertEqual(len(dests), len(set(dests)))
        names = [p.filename for p in fetch.PLUGINS + session]
        self.assertEqual(len(names), len(set(names)))

    def test_session_default_is_host_plus_android(self) -> None:
        names = [p.platform for p in fetch.select_plugins(None, "session")]
        self.assertEqual(names, fetch.default_platform_names())
        self.assertFalse({"osx", "windows"} <= set(names))
        self.assertTrue(all(p.library == "session" for p in fetch.select_plugins(None, "session")))

    def test_config_names_match_the_session_fetcher(self) -> None:
        text = CONFIG.read_text(encoding="utf-8")
        for plugin in fetch.SESSION_PLUGINS:
            self.assertIn(plugin.filename, text)
            self.assertIn(plugin.dest, text)
        self.assertIn(f'RELEASE_REPOSITORY = "{fetch.SESSION_RELEASE_REPO}"', text)
        self.assertIn(f'RELEASE_TAG = "{fetch.SESSION_RELEASE_TAG}"', text)

    def test_session_pin_is_namespaced(self) -> None:
        pins = fetch.parse_pins(["android=" + "a" * 64, "session:android=" + "B" * 64])
        self.assertEqual(pins["android"], "a" * 64)
        self.assertEqual(pins["session:android"], "b" * 64)
        with self.assertRaises(ValueError):
            fetch.parse_pins(["other:android=" + "a" * 64])
        with self.assertRaises(ValueError):
            fetch.parse_pins(["session:linux=" + "a" * 64])

    def test_session_pin_does_not_pin_core(self) -> None:
        data = b"x"
        digest = _digest(data)
        plugin = fetch.plugins_by_platform()["android"]
        fetch.collect((plugin,), lambda p: (data, digest), {"session:android": "c" * 64})
        session = fetch.plugins_by_platform("session")["android"]
        with self.assertRaises(fetch.ChecksumError):
            fetch.collect((session,), lambda p: (data, digest), {"session:android": "c" * 64})

    def test_default_fetches_both_libraries_from_one_dir(self) -> None:
        source = self.root / "both"
        source.mkdir()
        lines = []
        for plugin in fetch.select_plugins(["android"]) + fetch.select_plugins(["android"], "session"):
            data = plugin.filename.encode()
            (source / plugin.filename).write_bytes(data)
            lines.append(f"{_digest(data)}  {plugin.filename}")
        (source / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")
        code = fetch.main(
            ["--project-root", str(self.project), "--from-dir", str(source), "--platform", "android"]
        )
        self.assertEqual(code, 0)
        self.assertTrue((self.project / "Assets/Plugins/Android/arm64-v8a/libspatialblend.so").is_file())
        self.assertTrue((self.project / "Assets/Plugins/Android/arm64-v8a/libspatialblend_session.so").is_file())

    def test_a_bad_session_checksum_installs_neither_library(self) -> None:
        source = self.root / "bad"
        source.mkdir()
        lines = []
        for plugin in fetch.select_plugins(["android"]):
            data = plugin.filename.encode()
            (source / plugin.filename).write_bytes(data)
            lines.append(f"{_digest(data)}  {plugin.filename}")
        session = fetch.plugins_by_platform("session")["android"]
        (source / session.filename).write_bytes(b"tampered")
        lines.append(f"{'ab' * 32}  {session.filename}")
        (source / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")
        code = fetch.main(
            ["--project-root", str(self.project), "--from-dir", str(source), "--platform", "android"]
        )
        self.assertEqual(code, 1)
        self.assertFalse((self.project / "Assets/Plugins/Android/arm64-v8a/libspatialblend.so").exists())
        self.assertFalse((self.project / session.dest).exists())

    def test_session_release_requires_its_own_url(self) -> None:
        code = fetch.main(
            [
                "--project-root",
                str(self.project),
                "--library",
                "session",
                "--release-url",
                "https://example.test/v2",
                "--session-release-url",
                "",
            ]
        )
        self.assertEqual(code, 1)

    def test_session_from_dir_cli_verifies_then_installs(self) -> None:
        source = self._session_dir("session-art")
        session = fetch.plugins_by_platform("session")["android"]
        digest = _digest(b"session-android")
        code = fetch.main(
            [
                "--project-root", str(self.project),
                "--library", "session",
                "--from-dir", str(source),
                "--platform", "android",
                "--pin", f"session:android={digest}",
            ]
        )
        self.assertEqual(code, 0)
        self.assertEqual((self.project / session.dest).read_bytes(), b"session-android")

    def test_unknown_library_is_rejected(self) -> None:
        self.assertEqual(
            fetch.main(["--project-root", str(self.project), "--library", "bogus"]), 2
        )


class MissingChecksumMessageTest(unittest.TestCase):
    def test_a_name_missing_from_the_checksums_is_a_readable_error(self) -> None:
        sums = fetch.parse_sha256sums(f"{'ab' * 32}  other.so\n")
        with self.assertRaises(fetch.FetchError) as caught:
            fetch.checksum_for(sums, "libspatialblend.so")
        self.assertIsInstance(caught.exception, KeyError)
        self.assertEqual(str(caught.exception), "SHA256SUMS has no checksum for libspatialblend.so")

if __name__ == "__main__":
    unittest.main()
