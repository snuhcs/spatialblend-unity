#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Safely plan or import a local, pinned Photon Fusion SDK without network access.

The SDK is intentionally ignored by git.  This tool only copies the runtime
closure needed by Meta's Fusion building block and never replaces a differing
project file.  It deliberately leaves the target NetworkProjectConfig.fusion
and its meta file alone: those contain project-specific connection settings.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys


FUSION_VERSION = "2.0.9"
FUSION_BUILD = "2.0.9 Stable 1566"
REQUIRED_FUSION_PATHS = (
    Path("Fusion/Assemblies/Fusion.Runtime.dll"),
    Path("Fusion/Runtime/Fusion.Unity.asmdef"),
    Path("Fusion/CodeGen/Fusion.CodeGen.asmdef"),
    Path("Fusion/Editor/Fusion.Unity.Editor.asmdef"),
    Path("PhotonRealtime/Code/PhotonRealtime.asmdef"),
    Path("PhotonVoice/PhotonVoiceApi/PhotonVoice.API.asmdef"),
    Path("PhotonVoice/Code/PhotonVoice.asmdef"),
    Path("PhotonVoice/Code/Fusion/PhotonVoice.Fusion.asmdef"),
    Path("PhotonLibs/netstandard2.0/Photon3Unity3D.dll"),
    # PhotonVoice.PUN has no define constraint in this pinned source and the
    # target already has the matching PUN scripting defines.
    Path("PhotonUnityNetworking/Code/PhotonUnityNetworking.asmdef"),
)
COPIED_ROOTS = ("Fusion", "PhotonRealtime", "PhotonVoice", "PhotonLibs", "PhotonUnityNetworking")
# The target already owns this folder because it contains its retained Fusion
# network configuration. Its folder GUID must not be replaced by a source SDK.
SKIP_SOURCE_PATHS = {
    Path("Fusion/Resources/NetworkProjectConfig.fusion"),
    Path("Fusion/Resources/NetworkProjectConfig.fusion.meta"),
}
MARKER_PATHS = (
    Path("Fusion/build_info.txt"),
    Path("Fusion/package.json"),
    Path("PhotonVoice/changes-voice.txt"),
    Path("PhotonRealtime/Code/changes-realtime.txt"),
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project_relative(value: str, option: str) -> Path:
    path = Path(value)
    if str(path) in ("", ".") or path.is_absolute() or ".." in path.parts:
        raise ValueError(f"{option} must be a non-root project-relative path without '..': {value}")
    return path


def absolute_lexical(value: str) -> Path:
    return Path(os.path.abspath(os.path.expanduser(value)))


def ensure_no_symlink_ancestor(path: Path, root: Path, label: str) -> None:
    """Reject lexical symlinks, including dangling links, below ``root``."""
    try:
        parts = path.relative_to(root).parts
    except ValueError as error:
        raise ValueError(f"{label} escapes its allowed root: {path}") from error
    current = root
    if current.is_symlink():
        raise ValueError(f"refusing symlinked {label}: {current}")
    for part in parts:
        current /= part
        if current.is_symlink():
            raise ValueError(f"refusing symlinked {label}: {current}")


def ensure_no_symlink_path(path: Path, label: str) -> None:
    anchor = Path(path.anchor)
    current = anchor
    for part in path.relative_to(anchor).parts:
        current /= part
        if current.is_symlink():
            raise ValueError(f"refusing symlinked {label}: {current}")


def read_source(source: Path) -> dict[str, str]:
    if source.is_symlink():
        raise ValueError(f"refusing symlinked Photon source: {source}")
    build_info = source / "Fusion/build_info.txt"
    package = source / "Fusion/package.json"
    if not build_info.is_file() or not package.is_file():
        raise ValueError("Photon source lacks Fusion build_info.txt or package.json")
    metadata = json.loads(package.read_text(encoding="utf-8"))
    if metadata.get("name") != "com.exitgames.photonfusion":
        raise ValueError("Photon source is not the official Fusion package")
    build_lines = build_info.read_text(encoding="utf-8").splitlines()
    if f"build: {FUSION_BUILD}" not in build_lines:
        raise ValueError(f"Photon source is not pinned Fusion {FUSION_BUILD}")
    for relative in REQUIRED_FUSION_PATHS:
        if not (source / relative).is_file():
            raise ValueError(f"Photon source lacks required closure file: {relative}")
    for marker in MARKER_PATHS:
        if not (source / marker).is_file():
            raise ValueError(f"Photon source lacks provenance marker: {marker}")
    return {str(marker): sha256(source / marker) for marker in MARKER_PATHS}


def source_files(source: Path) -> list[Path]:
    files: list[Path] = []
    for root_name in COPIED_ROOTS:
        root = source / root_name
        root_meta = source / f"{root_name}.meta"
        if root.is_symlink() or root_meta.is_symlink():
            raise ValueError(f"refusing symlinked Photon source root or meta: {root_name}")
        if not root.is_dir() or not root_meta.is_file():
            raise ValueError(f"Photon source lacks required root and meta: {root_name}")
        files.append(root_meta)
        for path in sorted(root.rglob("*")):
            if path.is_symlink():
                raise ValueError(f"refusing symlinked Photon source file: {path}")
            # SDK .asset files hold Photon account/server configuration.  The
            # source code (including PhotonAppSettings.cs) is retained, but an
            # account setting and its GUID meta never cross project boundaries.
            relative = path.relative_to(source)
            if path.is_file() and relative not in SKIP_SOURCE_PATHS and "Demos" not in relative.parts and relative.name != "Demos.meta" and not (
                relative.suffix == ".asset" or relative.name.endswith(".asset.meta")
            ):
                files.append(path)
    return files


def target_for(source: Path, original: Path, destination: Path) -> Path:
    return destination / original.relative_to(source)


def is_directory_meta(source: Path, original: Path) -> bool:
    """Whether a .meta belongs to a source directory rather than an asset."""
    if not original.name.endswith(".meta"):
        return False
    return (source / original.relative_to(source).with_name(original.name.removesuffix(".meta"))).is_dir()


def plan_import(source: Path, destination: Path, project_root: Path) -> tuple[list[tuple[Path, Path]], int, int]:
    copies: list[tuple[Path, Path]] = []
    matching = 0
    preserved = 0
    for original in source_files(source):
        target = target_for(source, original, destination)
        ensure_no_symlink_ancestor(target, project_root, "destination")
        if target.exists() or target.is_symlink():
            # Existing target directories may already contain retained project
            # config. Preserve their GUIDs rather than silently replacing them.
            directory = target.with_name(target.name.removesuffix(".meta"))
            if is_directory_meta(source, original) and directory.is_dir():
                if not target.is_file():
                    raise ValueError(f"refusing non-file preserved target: {target}")
                preserved += 1
                continue
            if not target.is_file() or sha256(target) != sha256(original):
                raise ValueError(f"refusing to overwrite differing user file: {target}")
            matching += 1
        else:
            copies.append((original, target))
    return copies, matching, preserved


def provenance(source: Path, destination: Path, markers: dict[str, str], files: list[Path]) -> dict:
    return {
        "bootstrap": "local-photon-fusion-runtime-closure",
        "fusion": {"version": FUSION_VERSION, "build": FUSION_BUILD,
                   "source_marker_sha256": markers},
        "source": str(source),
        "destination": str(destination),
        "excluded_source_file_kind": "*.asset and *.asset.meta (project-specific account/server settings)",
        "preserved_target_files": sorted(str(path) for path in SKIP_SOURCE_PATHS),
        "preserved_target_directory_meta": "Existing target directory GUID metas are retained.",
        "files": {str(path.relative_to(source)): sha256(path) for path in files},
    }


def write_provenance(path: Path, artifact: dict) -> None:
    payload = json.dumps(artifact, indent=2, sort_keys=True) + "\n"
    if path.exists() or path.is_symlink():
        if not path.is_file() or path.read_text(encoding="utf-8") != payload:
            raise ValueError(f"refusing to overwrite differing provenance file: {path}")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(payload, encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", default=Path(__file__).resolve().parents[1])
    parser.add_argument("--source-photon", required=True,
                        help="Local official Assets/Photon source; no download occurs")
    parser.add_argument("--destination", default="Assets/Photon",
                        help="Project-relative Photon destination")
    parser.add_argument("--provenance", help="Optional JSON output; differing content is never replaced")
    parser.add_argument("--apply", action="store_true", help="Copy after all validation passes")
    args = parser.parse_args(argv)
    try:
        project_root = Path(args.project_root).resolve()
        destination = project_root / project_relative(args.destination, "--destination")
        source = absolute_lexical(args.source_photon)
        ensure_no_symlink_path(source, "Photon source")
        markers = read_source(source)
        files = source_files(source)
        copies, matching, preserved = plan_import(source, destination, project_root)
        artifact = provenance(source, destination, markers, files)
        provenance_path = absolute_lexical(args.provenance) if args.provenance else None
        if provenance_path:
            ensure_no_symlink_path(provenance_path, "provenance path")
        if args.apply:
            if provenance_path:
                write_provenance(provenance_path, artifact)
            for original, target in copies:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(original, target)
            artifact["status"] = "applied"
        else:
            artifact["status"] = "dry-run"
        artifact["files_to_copy"] = len(copies)
        artifact["already_matching"] = matching
        artifact["preserved_existing"] = preserved
        print(json.dumps(artifact, indent=2, sort_keys=True))
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"bootstrap-photon-sdk: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
