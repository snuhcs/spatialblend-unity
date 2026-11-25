#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""The DllImport names of the core and the session library resolve to the files tools/fetch_plugins.py installs, per platform.

Unity resolves a DllImport name to lib<name>.so / lib<name>.dylib on Android, Linux and macOS, but to <name>.dll on Windows
(no "lib" prefix). The shipped files are libspatialblend.* and libspatialblend_session.* everywhere, so the Windows branch of each
binding names the prefix itself.
"""
from __future__ import annotations

from pathlib import Path
import re
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

import fetch_plugins as fetch  # noqa: E402

CONFIG = ROOT / "Assets/SpatialBlend/Foundation/Internal/SpatialBlendLibraryConfig.cs"
SESSION = ROOT / "Assets/SpatialBlend/Framework/Internal/SessionLibrary.cs"
WINDOWS_DEFINE = "#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN"


def branch_values(text: str, constant: str) -> dict[str, str]:
    """{'windows': value, 'other': value} of `const string <constant> = "..."` inside the Windows #if / #else."""
    start = text.index(WINDOWS_DEFINE)
    else_at = text.index("#else", start)
    end = text.index("#endif", else_at)
    pattern = re.compile(r'const string ' + constant + r' = "([^"]+)";')
    windows = pattern.findall(text[start:else_at])
    other = pattern.findall(text[else_at:end])
    assert len(windows) == 1 and len(other) == 1, (constant, windows, other)
    return {"windows": windows[0], "other": other[0]}


def resolved_file(name: str, platform: str) -> str:
    """The file Unity looks for, for a DllImport name on a platform."""
    if platform == "windows":
        return name + ".dll"
    return "lib" + name + {"android": ".so", "osx": ".dylib"}[platform]


class NativeLibraryNamesTest(unittest.TestCase):
    def check(self, names: dict[str, str], plugins) -> None:
        for plugin in plugins:
            name = names["windows" if plugin.platform == "windows" else "other"]
            self.assertEqual(resolved_file(name, plugin.platform), plugin.filename, plugin.platform)

    def test_core_names_resolve_to_the_installed_files(self) -> None:
        self.check(branch_values(CONFIG.read_text(encoding="utf-8"), "RELEASE_LIBRARY"), fetch.PLUGINS)

    def test_session_names_resolve_to_the_installed_files(self) -> None:
        self.check(branch_values(SESSION.read_text(encoding="utf-8"), "LIB_NAME"), fetch.SESSION_PLUGINS)

    def test_the_debug_library_follows_the_same_rule(self) -> None:
        names = branch_values(CONFIG.read_text(encoding="utf-8"), "DEBUG_LIBRARY")
        self.assertEqual(names, {"windows": "libspatialblend_d", "other": "spatialblend_d"})

    def test_no_binding_hardcodes_the_core_name(self) -> None:
        # Every core DllImport goes through SpatialBlendLibraryConfig.LibraryNames, so the Windows branch applies to all of them.
        offenders = []
        for path in (ROOT / "Assets/SpatialBlend").rglob("*.cs"):
            text = path.read_text(encoding="utf-8", errors="ignore")
            if 'DllImport("spatialblend' in text or 'DllImport("libspatialblend' in text:
                offenders.append(str(path.relative_to(ROOT)))
        self.assertEqual(offenders, [])


if __name__ == "__main__":
    unittest.main()
