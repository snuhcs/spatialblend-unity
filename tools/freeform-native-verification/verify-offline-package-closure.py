# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Validate and record the locally resolved UPM closure without starting Unity.

The Freeform native gate deliberately uses the project's checked-in manifest and
lockfile.  UPM may only start after every locked non-builtin package is present
in the copied project's PackageCache and every builtin package is present in the
selected editor.  This script only reads files; it never invokes UPM or the
network.
"""
import hashlib
import json
from pathlib import Path
import plistlib
import sys


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree_digest(root):
    """Return a deterministic SHA-256 over a package's regular files."""
    hasher = hashlib.sha256()
    for path in sorted(path for path in root.rglob("*") if path.is_file()):
        relative = path.relative_to(root).as_posix().encode("utf-8")
        hasher.update(len(relative).to_bytes(8, "big"))
        hasher.update(relative)
        hasher.update(path.stat().st_size.to_bytes(8, "big"))
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                hasher.update(chunk)
    return hasher.hexdigest()


def load_json(path, label):
    try:
        return json.loads(path.read_text())
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"Unreadable {label}: {path}: {error}") from error


def resolution_by_name(path):
    resolution = load_json(path, "UPM project resolution")
    outputs = resolution.get("outputs")
    if not isinstance(outputs, dict):
        raise ValueError("UPM project resolution has no outputs map")
    result = {}
    for package_id, record in outputs.items():
        name = record.get("name")
        if not isinstance(name, str) or name in result:
            raise ValueError(f"UPM project resolution has ambiguous package: {package_id}")
        result[name] = record
    return result


def unity_app_root(unity_path):
    """Resolve either a Unity.app bundle or its Contents/MacOS/Unity binary."""
    candidates = [unity_path]
    if len(unity_path.parents) >= 3:
        candidates.append(unity_path.parents[2])
    for candidate in candidates:
        if candidate.suffix == ".app" and (
                candidate / "Contents/Resources/PackageManager/BuiltInPackages").is_dir():
            return candidate
    raise ValueError(f"Unity app bundle has no BuiltInPackages directory: {unity_path}")


def unity_version(unity_app):
    info_path = unity_app / "Contents/Info.plist"
    try:
        with info_path.open("rb") as stream:
            info = plistlib.load(stream)
    except (OSError, plistlib.InvalidFileException) as error:
        raise ValueError(f"Unreadable Unity Info.plist: {info_path}: {error}") from error
    version = info.get("CFBundleShortVersionString") or info.get("CFBundleVersion")
    if not isinstance(version, str) or not version:
        raise ValueError(f"Unity Info.plist has no bundle version: {info_path}")
    return version


def package_cache_path(cache, name):
    matches = sorted(path for path in cache.glob(name + "@*") if path.is_dir())
    if len(matches) != 1:
        found = ", ".join(path.name for path in matches) or "none"
        raise ValueError(f"Missing or ambiguous local package cache artifact for {name}: {found}")
    return matches[0]


def validate_package(root, copy_root, editor, name, locked, resolution):
    source = locked.get("source")
    version = locked.get("version")
    if not isinstance(source, str) or not isinstance(version, str):
        raise ValueError(f"Lock record is incomplete for {name}")
    resolved = resolution.get(name)
    if resolved is None:
        raise ValueError(f"No locally resolved package artifact for {name}")
    if resolved.get("source") != source:
        raise ValueError(f"UPM source differs from lock for {name}: {resolved.get('source')} != {source}")

    if source == "builtin":
        source_path = editor / "Contents/Resources/PackageManager/BuiltInPackages" / name
        copied_path = source_path
    else:
        source_path = package_cache_path(root / "Library/PackageCache", name)
        copied_path = package_cache_path(copy_root / "Library/PackageCache", name)
        if source_path.name != copied_path.name:
            raise ValueError(f"Copied package cache artifact differs for {name}")

    package_manifest = source_path / "package.json"
    copied_manifest = copied_path / "package.json"
    package = load_json(package_manifest, f"package manifest for {name}")
    copied = load_json(copied_manifest, f"copied package manifest for {name}")
    if package.get("name") != name or copied.get("name") != name:
        raise ValueError(f"Package artifact has the wrong name for {name}")
    if package.get("version") != resolved.get("version"):
        raise ValueError(f"Package artifact version differs from UPM resolution for {name}")
    if digest(package_manifest) != digest(copied_manifest):
        raise ValueError(f"Copied package manifest differs for {name}")

    source_tree = tree_digest(source_path)
    copied_tree = tree_digest(copied_path)
    if source_tree != copied_tree:
        raise ValueError(f"Copied package artifact differs for {name}")
    return {
        "name": name,
        "source": source,
        "lock_version": version,
        "resolved_version": resolved.get("version"),
        "resolution_fingerprint": resolved.get("fingerprint", "builtin"),
        "artifact": source_path.name if source != "builtin" else str(source_path),
        "package_json_sha256": digest(package_manifest),
        "tree_sha256": source_tree,
    }


def main(root, copied_project, unity_bin):
    root, copied_project, unity_bin = Path(root), Path(copied_project), Path(unity_bin)
    unity_app = unity_app_root(unity_bin)
    manifest_path = root / "Packages/manifest.json"
    lock_path = root / "Packages/packages-lock.json"
    copied_manifest = copied_project / "Packages/manifest.json"
    copied_lock = copied_project / "Packages/packages-lock.json"
    if digest(manifest_path) != digest(copied_manifest) or digest(lock_path) != digest(copied_lock):
        raise ValueError("Copied manifest or lockfile differs before package preflight")
    manifest = load_json(manifest_path, "manifest")
    lock = load_json(lock_path, "lockfile")
    direct = manifest.get("dependencies")
    packages = lock.get("dependencies")
    if not isinstance(direct, dict) or not isinstance(packages, dict):
        raise ValueError("Manifest or lockfile has no dependencies map")
    missing_direct = sorted(set(direct) - set(packages))
    if missing_direct:
        raise ValueError("Lockfile lacks direct dependencies: " + ", ".join(missing_direct))

    # A lock dependency selects a concrete artifact by package name.  Ranges in
    # package manifests intentionally need not equal the selected lock version.
    unresolved = sorted(
        f"{name} -> {dependency}"
        for name, record in packages.items()
        for dependency in record.get("dependencies", {})
        if dependency not in packages
    )
    if unresolved:
        raise ValueError("Lockfile dependency is absent: " + ", ".join(unresolved))
    resolution = resolution_by_name(root / "Library/PackageManager/projectResolution.json")
    if set(resolution) != set(packages):
        missing = sorted(set(packages) - set(resolution))
        extra = sorted(set(resolution) - set(packages))
        raise ValueError("UPM resolution does not match lockfile; missing=" +
                         ",".join(missing) + " extra=" + ",".join(extra))

    rows = [validate_package(root, copied_project, unity_app, name, packages[name], resolution)
            for name in sorted(packages)]
    output = copied_project / "PACKAGE_CLOSURE_SHA256.json"
    output.write_text(json.dumps({
        "manifest_sha256": digest(manifest_path),
        "packages_lock_sha256": digest(lock_path),
        "unity_app": str(unity_app),
        "unity_binary": str(unity_bin),
        "unity_version": unity_version(unity_app),
        "packages": rows,
    }, indent=2, sort_keys=True) + "\n")
    print(f"FREEFORM_OFFLINE_PACKAGE_CLOSURE={len(rows)}")
    print(f"FREEFORM_OFFLINE_PACKAGE_PROVENANCE={output}")


if __name__ == "__main__":
    try:
        main(*sys.argv[1:])
    except (TypeError, ValueError) as error:
        raise SystemExit(f"Offline package preflight failed: {error}")
