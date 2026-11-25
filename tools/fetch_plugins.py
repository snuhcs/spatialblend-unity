#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Fetch libspatialblend and libspatialblend_session into the gitignored Assets/Plugins directories.

Libraries, chosen with --library (repeatable, default: both):

  core      the SpatialBlend core (sb_* C API), published as release files of
            https://github.com/snuhcs/spatialblend-core (CORE_RELEASE_TAG).
  session   the scan session library (spatialblend_* C API), published as release files of
            https://github.com/snuhcs/spatialblend-reconstruction (SESSION_RELEASE_TAG). It is not the
            core and shares no symbols with it.

Sources:

  (default) the GitHub release: <release-url>/<file> plus a SHA256SUMS file (or <file>.sha256)
            next to it. --release-url / --session-release-url (or SPATIALBLEND_PLUGIN_RELEASE_URL /
            SPATIALBLEND_SESSION_PLUGIN_RELEASE_URL) point at another release or a mirror.
  --from-dir DIR  a local directory holding the library files and a SHA256SUMS (or <file>.sha256);
            the files may also sit under lib/.

Every library is checked against its published SHA-256 before anything is written. --pin
platform=<hex> (core) or --pin session:platform=<hex> adds a second, locally configured hash that
must match as well. Nothing is installed if any selected plugin fails.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import platform
import re
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path


CORE = "core"
SESSION = "session"
LIBRARIES = (CORE, SESSION)

# The public releases the setup downloads.
CORE_RELEASE_REPO = "snuhcs/spatialblend-core"
CORE_RELEASE_TAG = "v1.0.0"
SESSION_RELEASE_REPO = "snuhcs/spatialblend-reconstruction"
SESSION_RELEASE_TAG = "v1.0.0"
CHECKSUM_FILE = "SHA256SUMS"


def release_base_url(repo: str, tag: str) -> str:
    return f"https://github.com/{repo}/releases/download/{tag}"


CORE_RELEASE_URL = release_base_url(CORE_RELEASE_REPO, CORE_RELEASE_TAG)
SESSION_RELEASE_URL = release_base_url(SESSION_RELEASE_REPO, SESSION_RELEASE_TAG)
DEFAULT_RELEASE_URLS = {CORE: CORE_RELEASE_URL, SESSION: SESSION_RELEASE_URL}


@dataclass(frozen=True)
class Plugin:
    platform: str
    filename: str  # the release asset name, and the installed file name
    dest: str
    library: str = CORE

    @property
    def pin_key(self) -> str:
        """--pin key: bare platform for core, library:platform otherwise."""
        return self.platform if self.library == CORE else f"{self.library}:{self.platform}"


PLUGINS: tuple[Plugin, ...] = (
    Plugin("android", "libspatialblend.so", "Assets/Plugins/Android/arm64-v8a/libspatialblend.so"),
    Plugin("osx", "libspatialblend.dylib", "Assets/Plugins/OSX/libspatialblend.dylib"),
    Plugin("windows", "libspatialblend.dll", "Assets/Plugins/Windows/libspatialblend.dll"),
)

SESSION_PLUGINS: tuple[Plugin, ...] = (
    Plugin("android", "libspatialblend_session.so", "Assets/Plugins/Android/arm64-v8a/libspatialblend_session.so", SESSION),
    Plugin("osx", "libspatialblend_session.dylib", "Assets/Plugins/OSX/libspatialblend_session.dylib", SESSION),
    Plugin("windows", "libspatialblend_session.dll", "Assets/Plugins/Windows/libspatialblend_session.dll", SESSION),
)

PLUGINS_BY_LIBRARY = {CORE: PLUGINS, SESSION: SESSION_PLUGINS}

SHA256_LINE = re.compile(r"^([0-9a-fA-F]{64})(?:\s+\*?(\S+))?$")
PIN = re.compile(r"^(?:([a-z0-9]+):)?([A-Za-z0-9_]+)=([0-9a-fA-F]{64})$")


class ChecksumError(Exception):
    """The bytes do not match the published or pinned SHA-256."""


class FetchError(Exception):
    """A plugin could not be downloaded or had no checksum."""


class MissingChecksumError(FetchError, KeyError):
    """The checksum file has no line for the file. A KeyError too, for callers that catch the old exception."""

    def __init__(self, filename: str) -> None:
        super().__init__(filename)
        self.filename = filename

    def __str__(self) -> str:
        return f"{CHECKSUM_FILE} has no checksum for {self.filename}"


def plugins_by_platform(library: str = CORE) -> dict[str, Plugin]:
    return {plugin.platform: plugin for plugin in PLUGINS_BY_LIBRARY[library]}


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_sha256sums(text: str) -> dict[str, str]:
    """Parse sha256sum lines. A file that is only a digest maps to ''."""
    found: dict[str, str] = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        match = SHA256_LINE.fullmatch(line)
        if not match:
            raise ValueError(f"bad checksum line: {line}")
        digest = match.group(1).lower()
        name = match.group(2) or ""
        found[name] = digest
    if not found:
        raise ValueError("checksum file is empty")
    return found


def checksum_for(sums: dict[str, str], filename: str) -> str:
    if filename in sums:
        return sums[filename]
    if set(sums) == {""}:
        return sums[""]
    raise MissingChecksumError(filename)


def parse_pins(values: list[str]) -> dict[str, str]:
    pins: dict[str, str] = {}
    for value in values:
        match = PIN.fullmatch(value.strip())
        if not match:
            raise ValueError(
                f"pin must be [library:]platform=<64 hex chars>, got {value!r}"
            )
        library = match.group(1) or CORE
        platform_name, digest = match.group(2), match.group(3).lower()
        if library not in PLUGINS_BY_LIBRARY:
            raise ValueError(f"unknown library in pin: {library}")
        if platform_name not in plugins_by_platform(library):
            raise ValueError(f"unknown platform in pin: {platform_name}")
        pins[platform_name if library == CORE else f"{library}:{platform_name}"] = digest
    return pins


def verify_bytes(data: bytes, expected: str, pin: str | None, label: str) -> str:
    actual = sha256_hex(data)
    expected = expected.lower()
    if actual != expected:
        raise ChecksumError(
            f"{label} sha256 {actual} does not match published {expected}"
        )
    if pin is not None and actual != pin.lower():
        raise ChecksumError(f"{label} sha256 {actual} does not match pin {pin.lower()}")
    return actual


def install_bytes(project_root: Path, plugin: Plugin, data: bytes) -> Path:
    root = project_root.resolve()
    dest = (root / plugin.dest).resolve()
    if dest.parent == dest or root not in dest.parents:
        raise ValueError(f"refusing to write outside the project: {plugin.dest}")
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(data)
    return dest


def release_urls(base_url: str, plugin: Plugin) -> tuple[str, str, str]:
    base = base_url.rstrip("/")
    library = f"{base}/{plugin.filename}"
    return library, f"{base}/{CHECKSUM_FILE}", f"{library}.sha256"


def host_plugin_platform() -> str | None:
    """Desktop plugin for this machine. Linux has no Editor plugin."""
    system = platform.system()
    if system == "Darwin":
        return "osx"
    if system == "Windows":
        return "windows"
    return None


def default_platform_names() -> list[str]:
    """Host desktop plugin, plus Android. Never both OSX and Windows."""
    names: list[str] = []
    host = host_plugin_platform()
    if host:
        names.append(host)
    names.append("android")
    return names


def select_plugins(
    platforms: list[str] | None, library: str = CORE
) -> tuple[Plugin, ...]:
    if not platforms:
        platforms = default_platform_names()
    known = plugins_by_platform(library)
    chosen: list[Plugin] = []
    for name in platforms:
        if name not in known:
            raise ValueError(f"unknown platform: {name}")
        chosen.append(known[name])
    return tuple(chosen)


def select_libraries(values: list[str] | None) -> tuple[str, ...]:
    if not values:
        return LIBRARIES
    for value in values:
        if value not in LIBRARIES:
            raise ValueError(f"unknown library: {value}")
    return tuple(dict.fromkeys(values))


def locate_library(directory: Path, filename: str, subdirs: tuple[str, ...] = ()) -> Path:
    candidates = [directory / filename, directory / "lib" / filename]
    for sub in subdirs:
        candidates += [directory / sub / filename, directory / sub / "lib" / filename]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FetchError(f"missing {filename} under {directory}")


def published_digest(directory: Path, library: Path) -> str:
    sums_path = directory / CHECKSUM_FILE
    sidecar = library.with_name(library.name + ".sha256")
    if not sums_path.is_file():
        nested = library.parent / CHECKSUM_FILE
        if nested.is_file():
            sums_path = nested
    if sums_path.is_file():
        return checksum_for(
            parse_sha256sums(sums_path.read_text(encoding="utf-8")), library.name
        )
    if sidecar.is_file():
        return checksum_for(
            parse_sha256sums(sidecar.read_text(encoding="utf-8")), library.name
        )
    raise FetchError(f"no {CHECKSUM_FILE} or {sidecar.name} for {library.name}")


def load_directory(directory: Path, plugin: Plugin, subdirs: tuple[str, ...] = ()) -> tuple[bytes, str]:
    """Read the library and its published digest from a local directory."""
    library = locate_library(directory, plugin.filename, subdirs)
    return library.read_bytes(), published_digest(directory, library)


def default_fetch_url(url: str) -> bytes:
    try:
        with urllib.request.urlopen(url) as response:
            return response.read()
    except Exception as exc:  # noqa: BLE001 - surface the URL, not the traceback
        raise FetchError(f"GET {url} failed: {exc}") from exc


def fetch_release(base_url: str, plugin: Plugin, fetch_url) -> tuple[bytes, str]:
    """GET the library and its digest (SHA256SUMS, else <file>.sha256)."""
    library_url, sums_url, sidecar_url = release_urls(base_url, plugin)
    data = fetch_url(library_url)
    try:
        sums = fetch_url(sums_url)
        digest = checksum_for(parse_sha256sums(sums.decode("utf-8")), plugin.filename)
    except (FetchError, KeyError):
        sidecar = fetch_url(sidecar_url)
        digest = checksum_for(
            parse_sha256sums(sidecar.decode("utf-8")), plugin.filename
        )
    return data, digest


def collect(
    plugins: tuple[Plugin, ...],
    obtain,
    pins: dict[str, str],
) -> list[tuple[Plugin, bytes]]:
    """Download and verify every plugin before writing any of them."""
    staged: list[tuple[Plugin, bytes]] = []
    for plugin in plugins:
        data, digest = obtain(plugin)
        verify_bytes(data, digest, pins.get(plugin.pin_key), plugin.filename)
        staged.append((plugin, data))
    return staged


def install_staged(
    project_root: Path, staged: list[tuple[Plugin, bytes]]
) -> list[Path]:
    return [install_bytes(project_root, plugin, data) for plugin, data in staged]


def selection(libraries: list[str] | None, platforms: list[str] | None, pin_values: list[str]):
    """The plugins and pins a command line asks for. Raises ValueError."""
    chosen = select_libraries(libraries)
    plugins = tuple(plugin for library in chosen for plugin in select_plugins(platforms, library))
    return chosen, plugins, parse_pins(pin_values)


def run_install(project_root: Path, plugins: tuple[Plugin, ...], obtain, pins: dict[str, str]) -> int:
    """Collect, verify and install; print the result. Shared with the maintainers' CI fetcher."""
    try:
        staged = collect(plugins, obtain, pins)
        written = install_staged(project_root, staged)
    except (ChecksumError, FetchError, ValueError, KeyError) as exc:
        print(str(exc), file=sys.stderr)
        return 1
    for path in written:
        print(f"installed {path}")
    return 0


def add_selection_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--project-root", type=Path, default=Path("."))
    parser.add_argument(
        "--library",
        action="append",
        dest="libraries",
        default=None,
        help="Repeat to choose libraries (core, session). Default: both.",
    )
    parser.add_argument(
        "--platform",
        action="append",
        dest="platforms",
        default=None,
        help="Repeat to choose plugins (android, osx, windows). Default: the host desktop plugin plus Android.",
    )
    parser.add_argument(
        "--pin",
        action="append",
        default=[],
        help="[session:]platform=<sha256>. Repeatable extra pin.",
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_selection_arguments(parser)
    parser.add_argument(
        "--release-url",
        default=os.environ.get("SPATIALBLEND_PLUGIN_RELEASE_URL", CORE_RELEASE_URL),
        help=f"Core release download base. Default: {CORE_RELEASE_URL}",
    )
    parser.add_argument(
        "--session-release-url",
        default=os.environ.get("SPATIALBLEND_SESSION_PLUGIN_RELEASE_URL", SESSION_RELEASE_URL),
        help=f"Session release download base. Default: {SESSION_RELEASE_URL}",
    )
    parser.add_argument(
        "--from-dir",
        type=Path,
        default=None,
        help="Install from a local directory instead of the network.",
    )
    args = parser.parse_args(argv)

    try:
        _libraries, plugins, pins = selection(args.libraries, args.platforms, args.pin)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    urls = {CORE: args.release_url, SESSION: args.session_release_url}

    def obtain(plugin: Plugin) -> tuple[bytes, str]:
        if args.from_dir is not None:
            return load_directory(args.from_dir, plugin)
        url = urls[plugin.library]
        if not url:
            flag = "--release-url" if plugin.library == CORE else "--session-release-url"
            raise FetchError(f"{flag} is empty")
        return fetch_release(url, plugin, default_fetch_url)

    return run_install(args.project_root, plugins, obtain, pins)


if __name__ == "__main__":
    raise SystemExit(main())
