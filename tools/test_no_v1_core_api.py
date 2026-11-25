# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Fail if the project still carries the v1 core API.

v1 is recognised by what it contains, never by a name: the current core is
libspatialblend (sb_* C API), the same file name v1 used. v1 has these
fingerprints:

  * C# that calls its entry points (spatialblend_create_space, ...) or the
    NativeAPI wrapper class;
  * the v1 headers (spatialblend_utility.h, libspatialblend.h);
  * a plugin binary that exports spatialblend_create_space.

libspatialblend_session is the SAM3 library and is not the v1 core. This test
does not start Unity.
"""
import re
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNTIME_ROOTS = (
    ROOT / "Assets/SpatialBlend/Framework",
    ROOT / "Assets/SpatialBlend/Foundation",
    ROOT / "Assets/SpatialBlend/Freeform",
    ROOT / "Assets/SpatialBlend/Samples",
)
V1_ENTRY_POINTS = (
    "create_space",
    "connect_to_space",
    "destroy_space",
    "cleanup_all",
    "create_entity",
    "get_entity_state",
)
V1_SYMBOL = re.compile(r"\bspatialblend_(?:%s)\b" % "|".join(V1_ENTRY_POINTS))
V1_CORE_API = re.compile(r"\bNativeAPI\.|" + V1_SYMBOL.pattern)
V1_HEADER = re.compile(r"(^|/)(spatialblend_utility|libspatialblend)\.h$")
V1_BINARY_SYMBOL = b"spatialblend_create_space"
PLUGIN_BINARY = re.compile(r"\.(so|dll|dylib)$")


def runtime_sources():
    for root in RUNTIME_ROOTS:
        if not root.is_dir():
            continue
        for path in root.rglob("*.cs"):
            # Editor tools and EditMode fixtures name the v1 API in order to
            # reject it. Player scripts are the surface this guard covers.
            if "Editor" in path.parts or "Test" in path.parts:
                continue
            yield path


def v1_core_hits(path):
    hits = []
    for number, line in enumerate(path.read_text(errors="replace").splitlines(), 1):
        if V1_CORE_API.search(line):
            hits.append(f"{path.relative_to(ROOT)}:{number}:{line.strip()}")
    return hits


def is_v1_binary(data: bytes) -> bool:
    return V1_BINARY_SYMBOL in data


def plugin_binaries():
    plugins = ROOT / "Assets/Plugins"
    if not plugins.is_dir():
        return
    for path in plugins.rglob("*"):
        if path.is_file() and PLUGIN_BINARY.search(path.name):
            yield path


class NoV1CoreApiTests(unittest.TestCase):
    def test_runtime_sources_have_no_v1_core_api(self):
        hits = []
        for path in runtime_sources():
            hits.extend(v1_core_hits(path))
        self.assertEqual(hits, [], "v1 core API reference in runtime source:\n" + "\n".join(hits))

    def test_no_binding_file_calls_v1_entry_points(self):
        # A v1 SpatialBlendAPI.cs / NativeAPI.cs coming back is caught by content,
        # whatever the file is called.
        bad = [
            path.relative_to(ROOT).as_posix()
            for root in RUNTIME_ROOTS
            for path in (root / "Internal").glob("*.cs")
            if root.joinpath("Internal").is_dir() and V1_CORE_API.search(path.read_text(errors="replace"))
        ]
        self.assertEqual(bad, [])
        self.assertFalse(
            [root for root in RUNTIME_ROOTS if (root / "Internal" / "NativeAPI.cs").is_file()]
        )

    def test_tracked_files_are_not_v1_headers(self):
        listed = subprocess.check_output(
            ["git", "ls-files", "Assets/Plugins"], cwd=ROOT, text=True
        ).splitlines()
        hits = [path for path in listed if V1_HEADER.search(path)]
        self.assertEqual(hits, [], "tracked v1 header:\n" + "\n".join(hits))

    def test_installed_plugins_are_not_the_v1_core(self):
        hits = [
            path.relative_to(ROOT).as_posix()
            for path in plugin_binaries()
            if is_v1_binary(path.read_bytes())
        ]
        self.assertEqual(hits, [], "plugin binary exports the v1 core API:\n" + "\n".join(hits))

    def test_pattern_flags_v1_and_allows_the_current_core(self):
        self.assertIsNotNone(V1_CORE_API.search("return NativeAPI.spatialblend_create_space();"))
        self.assertIsNotNone(V1_CORE_API.search("spatialblend_connect_to_space(id);"))
        # The current binding and library carry the names v1 used, so a name alone is not v1.
        self.assertIsNone(V1_CORE_API.search("SpatialBlendAPI.CreateSession();"))
        self.assertIsNone(V1_CORE_API.search('[DllImport("spatialblend")]'))
        self.assertIsNone(V1_CORE_API.search('public const string ANDROID_LIBRARY = "libspatialblend.so";'))
        self.assertIsNone(V1_CORE_API.search('[DllImport("spatialblend_session")]'))
        self.assertIsNone(V1_CORE_API.search("sb_create_session();"))
        self.assertIsNotNone(V1_HEADER.search("Assets/Plugins/include/spatialblend/libspatialblend.h"))
        self.assertIsNotNone(V1_HEADER.search("Assets/Plugins/include/spatialblend/spatialblend_utility.h"))
        self.assertIsNone(V1_HEADER.search("Assets/Plugins/include/spatialblend/spatialblend.h"))

    def test_binary_fingerprint(self):
        self.assertTrue(is_v1_binary(b"\x00spatialblend_create_space\x00spatialblend_cleanup_all\x00"))
        self.assertFalse(is_v1_binary(b"\x00os_create_session\x00os_create_space\x00"))
        self.assertFalse(is_v1_binary(b"\x00spatialblend_session_create\x00"))


if __name__ == "__main__":
    unittest.main()
