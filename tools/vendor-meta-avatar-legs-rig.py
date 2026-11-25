#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Vendor (or check) the Meta Avatars 35.2.0 SDK legs rig in Assets/SpatialBlend/Avatar/LegsRig.

OvrAvatarAnimationBehavior (on SbAvatarPrefab, the received avatar shown while it walks) loads its default rig with
Resources.Load("RTR00003") when an avatar loads. The rig ships only in the package's LegsNetworkLoopback scene sample
(Samples~), so a project without it throws "The Object you want to instantiate is null" (OvrAvatarAnimationBehavior.cs:856)
and the legs never walk. The rig is Meta sample material and is not redistributed in this repository: the folder is
git-ignored and tools/setup_sdks.py runs this tool (--apply) to copy it from the user's own package cache.

What is copied: the asset closure of Samples~/Scenes/LegsNetworkLoopback/Resources/RTR00003.prefab, computed from GUID
references (the prefab, RT_MecanimLegs.controller, RTR00003.fbx and every clip the controller plays, about 66 MB), each with
its own .meta so the GUIDs, and so the prefab's references, are the package's. The sample scripts it references
(MecanimLegsAnimationController, OvrAvatarRigRootIdentifier) come from Samples~/Scripts (tools/bootstrap-meta-avatar-samples.py).
Folder .meta files are the repository's own (not the package's), and the package LICENSE.md is copied next to the rig
with a NOTICE.md (Meta SDK License section 1.3).

  --check   (default) every vendored file equals the package's bytes, and nothing of the closure is missing or extra
  --apply   write the closure (an existing differing file is an error unless --force)

One reference in Meta's own controller points at a controller the package does not ship (transition destinations); it
is reported as dangling, as Unity ignores it in the official sample.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import shutil
import sys

PACKAGE_NAME = "com.meta.xr.sdk.avatars"
PACKAGE_VERSION = "35.2.0"
PACKAGE_FINGERPRINT = "99a67d4aa1f5fd0259dde4e56e3f984347fe1862"
SAMPLES_ROOT = Path("Samples~")
SCRIPTS_ROOT = SAMPLES_ROOT / "Scripts"
RIG_ROOT = SAMPLES_ROOT / "Scenes" / "LegsNetworkLoopback"
RIG_PREFAB = RIG_ROOT / "Resources" / "RTR00003.prefab"
DESTINATION = Path("Assets/SpatialBlend/Avatar/LegsRig")
KNOWN_DANGLING_GUIDS = frozenset({"652766ec281e14941864b5a6e18265eb"})
NOTICE = ("Copyright (c) Meta Platform Technologies, LLC and its affiliates. All rights reserved.\n\n"
          "The files under this folder (except NOTICE.md) are unmodified copies from the Meta Avatars SDK " + PACKAGE_VERSION +
          " package (" + PACKAGE_NAME + ", Samples~/Scenes/LegsNetworkLoopback), redistributed under the Meta Platform Technologies "
          "SDK License Agreement (LICENSE.md, sections 1.1.2 and 1.3). Regenerate or check with tools/vendor-meta-avatar-legs-rig.py.\n")
_REFERENCE = re.compile(r"guid: ([0-9a-f]{32}),\s*type")
_OWN_GUID = re.compile(r"^guid: ([0-9a-f]{32})\s*$", re.M)


def package_cache(project_root: Path, supplied: str | None) -> Path:
    candidates = [Path(supplied).resolve()] if supplied else sorted((project_root / "Library/PackageCache").glob(f"{PACKAGE_NAME}@*"))
    if len(candidates) != 1:
        raise ValueError(f"expected exactly one cached {PACKAGE_NAME} package, found {len(candidates)} (pass --package-cache)")
    package = candidates[0]
    metadata = json.loads((package / "package.json").read_text(encoding="utf-8"))
    if (metadata.get("name"), metadata.get("version"), metadata.get("_fingerprint")) != (PACKAGE_NAME, PACKAGE_VERSION, PACKAGE_FINGERPRINT):
        raise ValueError(f"cached package is not pinned {PACKAGE_NAME} {PACKAGE_VERSION} ({PACKAGE_FINGERPRINT}): {package}")
    return package


def _package_guids(package: Path) -> dict[str, Path]:
    guids: dict[str, Path] = {}
    for meta in package.rglob("*.meta"):
        match = _OWN_GUID.search(meta.read_text(encoding="utf-8", errors="ignore"))
        if match:
            guids[match.group(1)] = meta.with_suffix("")
    return guids


def _references(asset: Path) -> set[str]:
    """GUIDs an asset references: from its own YAML (prefab, controller) and from its .meta (an FBX's importer settings)."""
    found: set[str] = set()
    meta = asset.parent / f"{asset.name}.meta"
    for path in (asset, meta):
        if path.is_file():
            raw = path.read_bytes()
            if path is meta or raw.startswith(b"%YAML"):
                found |= set(_REFERENCE.findall(raw.decode("utf-8", errors="ignore")))
    return found


def closure(package: Path) -> tuple[list[Path], list[str]]:
    """The rig prefab's asset closure inside RIG_ROOT, each asset with its .meta, as paths relative to RIG_ROOT."""
    prefab = package / RIG_PREFAB
    if not prefab.is_file():
        raise ValueError(f"cached package lacks the legs rig prefab: {prefab}")
    guids = _package_guids(package)
    rig_root, scripts = package / RIG_ROOT, package / SCRIPTS_ROOT
    seen: set[Path] = set()
    dangling: set[str] = set()
    pending = [prefab]
    while pending:
        asset = pending.pop()
        if asset in seen:
            continue
        seen.add(asset)
        for guid in _references(asset):
            target = guids.get(guid)
            if target is None:
                if guid not in KNOWN_DANGLING_GUIDS:
                    raise ValueError(f"{asset.relative_to(package)} references {guid}, which is not in the cached package")
                dangling.add(guid)
            elif rig_root in target.parents:
                pending.append(target)
            elif (package / SAMPLES_ROOT) in target.parents and scripts not in target.parents:
                raise ValueError(f"{asset.relative_to(package)} needs {target.relative_to(package)}, outside the rig and the sample scripts")
    files = sorted({p for asset in seen for p in (asset, asset.parent / f"{asset.name}.meta")})
    missing = [p for p in files if not p.is_file()]
    if missing:
        raise ValueError(f"cached package lacks rig files: {[str(p.relative_to(package)) for p in missing]}")
    return [p.relative_to(rig_root) for p in files], sorted(dangling)


def check(project_root: Path, package: Path) -> list[str]:
    relatives, _ = closure(package)
    destination = project_root / DESTINATION
    problems = []
    for relative in relatives:
        target = destination / relative
        if not target.is_file():
            problems.append(f"missing {DESTINATION / relative}")
        elif target.read_bytes() != (package / RIG_ROOT / relative).read_bytes():
            problems.append(f"differs from the package: {DESTINATION / relative}")
    expected = {str(r) for r in relatives}
    for path in destination.rglob("*"):
        rel = path.relative_to(destination)
        if path.is_file() and not path.name.endswith(".meta") and path.name not in ("LICENSE.md", "NOTICE.md") and str(rel) not in expected:
            problems.append(f"not in the closure: {DESTINATION / rel}")
    for name in ("LICENSE.md", "NOTICE.md"):
        if not (destination / name).is_file():
            problems.append(f"missing {DESTINATION / name}")
    return problems


def apply(project_root: Path, package: Path, force: bool) -> int:
    relatives, _ = closure(package)
    destination = project_root / DESTINATION
    copied = 0
    for relative in relatives:
        source, target = package / RIG_ROOT / relative, destination / relative
        if target.exists() and target.read_bytes() == source.read_bytes():
            continue
        if target.exists() and not force:
            raise ValueError(f"refusing to overwrite differing file (use --force): {target}")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        copied += 1
    shutil.copyfile(package / "LICENSE.md", destination / "LICENSE.md")
    (destination / "NOTICE.md").write_text(NOTICE, encoding="utf-8")
    return copied


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--project-root", default=Path(__file__).resolve().parents[1])
    parser.add_argument("--package-cache", help="Explicit cached com.meta.xr.sdk.avatars directory; no download occurs")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--apply", action="store_true")
    mode.add_argument("--check", action="store_true")
    parser.add_argument("--force", action="store_true", help="with --apply, replace differing files")
    args = parser.parse_args(argv)
    try:
        root = Path(args.project_root).resolve()
        package = package_cache(root, args.package_cache)
        if args.apply:
            print(f"vendor-meta-avatar-legs-rig: copied {apply(root, package, args.force)} file(s) to {DESTINATION}")
        problems = check(root, package)
        for problem in problems:
            print(f"vendor-meta-avatar-legs-rig: {problem}", file=sys.stderr)
        if not problems:
            relatives, dangling = closure(package)
            print(f"vendor-meta-avatar-legs-rig: {DESTINATION} matches the package ({len(relatives)} files; dangling {dangling})")
        return 1 if problems else 0
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"vendor-meta-avatar-legs-rig: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
