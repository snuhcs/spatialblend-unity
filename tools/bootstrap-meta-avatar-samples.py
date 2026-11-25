#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Offline, pinned import of the official Meta Avatars 35.2.0 sample scripts.

This intentionally imports only Samples~/Scripts, not the 285 MB scene sample.
It is an explicit bootstrap: use --apply to write files.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
from typing import Iterable


PACKAGE_NAME = "com.meta.xr.sdk.avatars"
PACKAGE_VERSION = "35.2.0"
PACKAGE_FINGERPRINT = "99a67d4aa1f5fd0259dde4e56e3f984347fe1862"
SAMPLE_ROOT = Path("Samples~") / "Scripts"
DEFAULT_DESTINATION = Path("Assets/Samples/Meta Avatars SDK/35.2.0/Sample Scenes/Scripts")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project_relative(value: str, option: str) -> Path:
    path = Path(value)
    if str(path) in ("", ".") or not path.parts or path.is_absolute() or ".." in path.parts:
        raise ValueError(f"{option} must be a project-relative path without '..': {value}")
    return path


def package_cache(project_root: Path, supplied: str | None) -> Path:
    if supplied:
        candidate = Path(supplied).resolve()
        candidates = [candidate]
    else:
        candidates = sorted((project_root / "Library/PackageCache").glob(f"{PACKAGE_NAME}@*"))
    if len(candidates) != 1:
        raise ValueError(f"expected exactly one cached {PACKAGE_NAME} package, found {len(candidates)}")
    package = candidates[0]
    metadata_path = package / "package.json"
    if not metadata_path.is_file():
        raise ValueError(f"cached package is missing package.json: {package}")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if metadata.get("name") != PACKAGE_NAME or metadata.get("version") != PACKAGE_VERSION:
        raise ValueError(f"cached package is not pinned {PACKAGE_NAME} {PACKAGE_VERSION}: {metadata_path}")
    if metadata.get("_fingerprint") != PACKAGE_FINGERPRINT:
        raise ValueError(f"cached package fingerprint does not match pinned {PACKAGE_VERSION}: {metadata_path}")
    if not (package / SAMPLE_ROOT / "Oculus.AvatarSDK2.Samples.asmdef").is_file():
        raise ValueError(f"cached package lacks official sample scripts: {package / SAMPLE_ROOT}")
    return package


def source_files(source: Path) -> list[Path]:
    root_meta = source.parent / f"{source.name}.meta"
    files = [root_meta] + sorted(path for path in source.rglob("*") if path.is_file())
    if not files:
        raise ValueError(f"official sample script directory is empty: {source}")
    return files


def ensure_no_symlink_ancestor(path: Path, root: Path) -> None:
    """Reject every lexical component under root, including dangling links."""
    try:
        parts = path.relative_to(root).parts
    except ValueError as error:
        raise ValueError(f"path escapes its allowed root: {path}") from error
    current = root
    if current.is_symlink():
        raise ValueError(f"refusing symlinked destination path: {current}")
    for part in parts:
        current /= part
        if current.is_symlink():
            raise ValueError(f"refusing symlinked destination path: {current}")


def plan_import(source: Path, destination: Path, project_root: Path) -> tuple[list[tuple[Path, Path]], list[Path]]:
    copies: list[tuple[Path, Path]] = []
    existing: list[Path] = []
    for original in source_files(source):
        relative = Path(original.name) if original.parent == source.parent else original.relative_to(source)
        target = destination.parent / f"{destination.name}.meta" if original == source.parent / f"{source.name}.meta" else destination / relative
        ensure_no_symlink_ancestor(target, project_root)
        if target.exists():
            if not target.is_file() or sha256(target) != sha256(original):
                raise ValueError(f"refusing to overwrite differing user file: {target}")
            existing.append(relative)
        else:
            copies.append((original, target))
    return copies, existing


def provenance(package: Path, source: Path, destination: Path, files: Iterable[Path]) -> dict:
    return {
        "bootstrap": "official-meta-avatar-sample-scripts",
        "package": {
            "name": PACKAGE_NAME,
            "version": PACKAGE_VERSION,
            "fingerprint": PACKAGE_FINGERPRINT,
            "package_json_sha256": sha256(package / "package.json"),
            "cache_path": str(package),
        },
        "source": str(source),
        "destination": str(destination),
        "files": {str(Path(path.name) if path.parent == source.parent else path.relative_to(source)): sha256(path) for path in files},
    }


def write_provenance(path: Path, artifact: dict) -> None:
    payload = json.dumps(artifact, indent=2, sort_keys=True) + "\n"
    if path.exists() and path.read_text(encoding="utf-8") != payload:
        raise ValueError(f"refusing to overwrite differing provenance file: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(payload, encoding="utf-8")


def unresolved_output_path(value: str) -> Path:
    """Make an absolute lexical path without resolving a user-controlled link."""
    raw = Path(value).expanduser()
    return Path(os.path.abspath(raw))


def ensure_no_symlink_output_path(path: Path) -> None:
    anchor = Path(path.anchor)
    current = anchor
    for part in path.relative_to(anchor).parts:
        current /= part
        if current.is_symlink():
            raise ValueError(f"refusing symlinked provenance path: {current}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", default=Path(__file__).resolve().parents[1])
    parser.add_argument("--package-cache", help="Explicit cached com.meta.xr.sdk.avatars directory; no download occurs")
    parser.add_argument("--destination", default=str(DEFAULT_DESTINATION),
                        help="Project-relative destination for official Samples~/Scripts")
    parser.add_argument("--provenance", help="Optional JSON output path; existing differing content is never replaced")
    parser.add_argument("--apply", action="store_true", help="Perform the import; otherwise print the verified plan only")
    args = parser.parse_args(argv)

    try:
        project_root = Path(args.project_root).resolve()
        destination = project_root / project_relative(args.destination, "--destination")
        package = package_cache(project_root, args.package_cache)
        source = package / SAMPLE_ROOT
        files = source_files(source)
        copies, existing = plan_import(source, destination, project_root)
        artifact = provenance(package, source, destination, files)
        if args.provenance:
            provenance_path = unresolved_output_path(args.provenance)
            ensure_no_symlink_output_path(provenance_path)
        else:
            provenance_path = None
        if args.apply:
            if provenance_path:
                # Guard external output before mutating the destination.
                write_provenance(provenance_path, artifact)
            for original, target in copies:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(original, target)
            artifact["status"] = "applied"
        else:
            artifact["status"] = "dry-run"
        artifact["files_to_copy"] = len(copies)
        artifact["already_matching"] = len(existing)
        print(json.dumps(artifact, indent=2, sort_keys=True))
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"bootstrap-meta-avatar-samples: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
