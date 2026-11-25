#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Offline, pinned install of the Meta Avatars SDK core assets and packaged preset avatars.

Meta's editor scripts normally create Assets/Oculus/Avatar2 (CoreAssetsUPMMover) and Assets/Oculus/Avatar2_SampleAssets
(the "Package Presets" menu) the first time the Editor opens a project. A build from a fresh worktree needs them before
that, and a batchmode build does not always get there. This tool does the same work from Unity's package cache:

  <cache>/com.meta.xr.sdk.avatars@*/CoreAssets                      -> Assets/Oculus/Avatar2/CoreAssets
  <cache>/com.meta.xr.sdk.avatars.sample.assets@*/SampleAssets      -> Assets/Oculus/Avatar2_SampleAssets/SampleAssets
  each PresetAvatars_* folder, *.glb only, zipped                   -> Assets/Oculus/Avatar2_SampleAssets/SampleAssets/SampleAssets/<folder>.zip

Both packages must be the pinned version and fingerprint. Nothing is downloaded. Both destinations are git-ignored
(**/Oculus/) and Meta's SDK license forbids committing them. An existing install is never replaced: a matching one is
reported as already installed, a different one is an error. Without --apply it only prints the plan.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import zipfile
from pathlib import Path

AVATARS = {"name": "com.meta.xr.sdk.avatars", "version": "35.2.0", "fingerprint": "99a67d4aa1f5fd0259dde4e56e3f984347fe1862"}
SAMPLES = {"name": "com.meta.xr.sdk.avatars.sample.assets", "version": "35.2.0", "fingerprint": "72c8e93d0bf263a2c90d250a0058a531206d48af"}
CORE_DEST = Path("Assets/Oculus/Avatar2/CoreAssets")
SAMPLES_DEST = Path("Assets/Oculus/Avatar2_SampleAssets/SampleAssets")
VERSION_FILE = ".avatar_sdk_assets_version.txt"
PRESET_PREFIX = "PresetAvatars"


def read_text_any(path: Path) -> str:
    """Unity wrote some of these version files as UTF-16 with a BOM; accept both."""
    raw = path.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16").strip()
    return raw.decode("utf-8-sig").strip()


def find_package(cache: Path, pin: dict) -> Path:
    candidates = sorted(cache.glob(f"{pin['name']}@*"))
    if len(candidates) != 1:
        raise ValueError(f"expected exactly one cached {pin['name']} in {cache}, found {len(candidates)}")
    package = candidates[0]
    try:
        metadata = json.loads((package / "package.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise ValueError(f"cached package is missing a readable package.json: {package}") from error
    if metadata.get("name") != pin["name"] or metadata.get("version") != pin["version"]:
        raise ValueError(f"cached package is not pinned {pin['name']} {pin['version']}: {package}")
    if metadata.get("_fingerprint") != pin["fingerprint"]:
        raise ValueError(f"cached package fingerprint does not match the pin for {pin['name']}: {package}")
    if not (package / "build_number.txt").is_file():
        raise ValueError(f"cached package has no build_number.txt: {package}")
    return package


def ensure_no_symlink(path: Path, root: Path) -> None:
    current = path
    while True:
        if current.is_symlink():
            raise ValueError(f"refusing to write through a symlink: {current}")
        if current == root or current.parent == current:
            return
        current = current.parent


def installed_build(destination: Path, version_relative: str) -> str | None:
    version_file = destination / version_relative
    return read_text_any(version_file) if version_file.is_file() else None


def preset_folders(samples_dir: Path) -> list[Path]:
    return sorted(path for path in (samples_dir / "SampleAssetsUnzipped").iterdir() if path.is_dir() and path.name.startswith(PRESET_PREFIX))


def zip_preset(folder: Path, target: Path) -> None:
    temporary = target.with_name(target.name + ".partial")
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as archive:
        for glb in sorted(folder.glob("*.glb")):
            archive.write(glb, glb.name)
    temporary.replace(target)


def install_tree(source: Path, destination: Path, build: str, version_relative: str, label: str, apply: bool, root: Path) -> str:
    """Copy source to destination atomically. Returns 'installed', 'present' or 'would-install'."""
    existing = installed_build(destination, version_relative)
    if destination.exists():
        if existing == build:
            return "present"
        raise ValueError(f"{label}: {destination} exists with build {existing!r}, expected {build}; not replacing it")
    if not apply:
        return "would-install"
    ensure_no_symlink(destination.parent, root)
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = destination.with_name(destination.name + ".bootstrap-partial")
    if staging.exists():
        shutil.rmtree(staging)
    shutil.copytree(source, staging, symlinks=False)
    (staging / version_relative).write_text(build + "\n", encoding="utf-8")
    staging.rename(destination)
    return "installed"


def run(project_root: Path, cache: Path, apply: bool) -> dict:
    avatars = find_package(cache, AVATARS)
    samples = find_package(cache, SAMPLES)
    result = {"avatars_package": str(avatars), "sample_assets_package": str(samples)}
    avatars_build = read_text_any(avatars / "build_number.txt")
    samples_build = read_text_any(samples / "build_number.txt")
    if not (avatars / "CoreAssets").is_dir() or not (samples / "SampleAssets/SampleAssetsUnzipped").is_dir():
        raise ValueError("a cached package lacks CoreAssets or SampleAssets/SampleAssetsUnzipped")

    result["core_assets"] = install_tree(avatars / "CoreAssets", project_root / CORE_DEST, avatars_build, VERSION_FILE,
                                         "CoreAssets", apply, project_root)
    sample_root = project_root / SAMPLES_DEST
    result["sample_assets"] = install_tree(samples / "SampleAssets", sample_root, samples_build,
                                           f"SampleAssetsUnzipped/{VERSION_FILE}", "SampleAssets", apply, project_root)
    zips: list[str] = []
    if apply or sample_root.exists():
        packaged = sample_root / "SampleAssets"
        for folder in preset_folders(sample_root):
            target = packaged / f"{folder.name}.zip"
            zips.append(target.name)
            if apply and not target.exists():
                packaged.mkdir(parents=True, exist_ok=True)
                zip_preset(folder, target)
    result["packaged_presets"] = zips
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--project-root", default=Path(__file__).resolve().parents[1])
    parser.add_argument("--package-cache-dir", help="a Library/PackageCache directory to read (default: this project's own)")
    parser.add_argument("--apply", action="store_true", help="install; otherwise print the verified plan only")
    args = parser.parse_args(argv)
    try:
        root = Path(args.project_root).resolve()
        cache = Path(args.package_cache_dir).expanduser().resolve() if args.package_cache_dir else root / "Library/PackageCache"
        result = run(root, cache, args.apply)
        print(json.dumps(result, indent=2, sort_keys=True))
        return 0
    except (OSError, ValueError) as error:
        print(f"bootstrap-meta-avatar-assets: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
