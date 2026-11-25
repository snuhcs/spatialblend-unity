#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""One command to bring a checkout's external SDKs in line with SDKS.lock.

    python3 tools/setup_sdks.py                      # install what is missing, ask for your own values
    python3 tools/setup_sdks.py --check              # verify only, change nothing
    python3 tools/setup_sdks.py --non-interactive    # never prompt (CI, lab machines)

What it does, in order:

1. Reads SDKS.lock (JSON) and checks the Unity Editor version.
2. Verifies that Packages/manifest.json and Packages/packages-lock.json carry the pins in the lock. It never edits them.
3. Photon (Fusion, Realtime, Voice, Libs, PUN). Photon forbids redistribution, so this tool never downloads it. Give it
   your own download (--photon-source: a .unitypackage or an extracted Assets/Photon folder). The content markers are
   checked against the lock, then tools/bootstrap-photon-sdk.py does the install.
4. Meta Avatars sample scripts, from the package cache (tools/bootstrap-meta-avatar-samples.py).
5. Meta Avatars core assets and packaged preset avatars (Assets/Oculus/Avatar2, Avatar2_SampleAssets), from the package
   cache (tools/bootstrap-meta-avatar-assets.py), and Assets/Oculus/OculusProjectConfig.asset from a committed template
   (only when absent). All three are git-ignored; a fresh worktree needs them before its first Editor start.
   Then the Meta Avatars legs rig (Assets/SpatialBlend/Avatar/LegsRig, git-ignored: Meta sample material that is not
   redistributed here), copied from the same package cache by tools/vendor-meta-avatar-legs-rig.py.
   In a worktree without a Library folder, point --package-cache-dir at another checkout's Library/PackageCache.
6. Native libraries (tools/fetch_plugins.py).
7. TextMesh Pro Essential Resources (Assets/TextMesh Pro, git-ignored), imported in Unity batchmode from the project's own
   pinned com.unity.ugui package (SpatialBlend.Editor.TmpEssentialsImport). Skipped when Assets/TextMesh Pro/Resources/
   TMP Settings.asset exists. Without it a built app has no TMP_Settings and never gets past the splash. Needs the Unity
   Editor (--unity-bin or $UNITY_BIN, default: the Hub install of the locked version) and no other Editor open on the project.
8. Your own values, written ONLY to git-ignored local files (or to ~/.config, outside the repository):
     Photon App ID (Fusion) and Voice App ID  -> Assets/Photon/Fusion/Resources/PhotonAppSettings.asset
     TURN Worker URL (none by default: TURN off) -> Assets/Resources/SpatialBlendTurnWorkerUrl.txt  (your own Worker)
     TURN client secret                        -> ~/.config/spatialblend/turn-client-secret (mode 600)
   Values come from flags, environment variables or a prompt. The secret is read from a file or a hidden prompt, never
   from an argument or an environment value, and no value is ever printed.

Exit status: 0 everything matches; 1 a mismatch (a pin, a version or a checksum differs); 2 something required is
missing, or a usage error. The required credentials (Photon Fusion App ID, Photon Voice App ID, TURN client secret) are
listed with how to set each; with --check a missing one exits 2 (waive one for a compile-only check with
SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID / SPATIALBLEND_ALLOW_NO_VOICE_APP_ID / SPATIALBLEND_ALLOW_NO_TURN set to 1), and an
applying run fails on them only with --require-user-vars. The TURN client secret is required only when a TURN Worker URL
is configured (none by default). There is no default scan server: name yours in SpatialBlendSettings or a device
spatialblend-local.json. It is not a build input and never fails a run.
"""
from __future__ import annotations

import argparse
import contextlib
import getpass
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
LOCK_NAME = "SDKS.lock"

EXIT_OK, EXIT_MISMATCH, EXIT_MISSING = 0, 1, 2

# The committed default; it must equal SpatialBlendSettings.DefaultTurnCredentialsUrl (a test checks that). Empty: no Worker, TURN off.
DEFAULT_TURN_WORKER_URL = ""

PHOTON_SETTINGS_RELATIVE = Path("Assets/Photon/Fusion/Resources/PhotonAppSettings.asset")
TURN_URL_RELATIVE = Path("Assets/Resources/SpatialBlendTurnWorkerUrl.txt")
TURN_SECRET_HOME_RELATIVE = Path(".config/spatialblend/turn-client-secret")
TMP_SETTINGS_RELATIVE = Path("Assets/TextMesh Pro/Resources/TMP Settings.asset")
TMP_IMPORT_SCRIPT = Path("Assets/SpatialBlend/Editor/TmpEssentialsImport.cs")
TMP_IMPORT_METHOD = "SpatialBlend.Editor.TmpEssentialsImport.Run"
TMP_PACKAGES = ("com.unity.ugui", "com.unity.textmeshpro")
ENV_UNITY_BIN = "UNITY_BIN"
TEMPLATE_DIR = Path("tools/templates")
PHOTON_SETTINGS_TEMPLATE = TEMPLATE_DIR / "PhotonAppSettings.asset.template"
PHOTON_SETTINGS_META_TEMPLATE = TEMPLATE_DIR / "PhotonAppSettings.asset.meta.template"
TEMPLATE_SECRET_MARKER = "REPLACE_WITH_THE_WORKER_CLIENT_SECRET"

ENV_APP_ID = "SPATIALBLEND_PHOTON_APP_ID"
ENV_VOICE_APP_ID = "SPATIALBLEND_PHOTON_VOICE_APP_ID"
ENV_TURN_URL = "SPATIALBLEND_TURN_WORKER_URL"
ENV_TURN_SECRET_FILE = "SPATIALBLEND_TURN_CLIENT_SECRET_FILE"

# Required credentials of a Quest/Windows build (the Editor's RequiredCredentialsBuildGuard enforces the same list and
# spells the same opt-outs; docs/required-credentials.md is the inventory). Each can be waived for a compile-only check
# with its SPATIALBLEND_ALLOW_NO_* variable set to 1; the APK of such a build is rejected by tools/check_apk_contents.py.
ALLOW_NO_PHOTON_APP_ID = "SPATIALBLEND_ALLOW_NO_PHOTON_APP_ID"
ALLOW_NO_VOICE_APP_ID = "SPATIALBLEND_ALLOW_NO_VOICE_APP_ID"
ALLOW_NO_TURN = "SPATIALBLEND_ALLOW_NO_TURN"
REQUIRED_CREDENTIAL_ROWS = ("photon-app-id", "photon-voice-app-id", "turn-client-secret")

GUID_RE = re.compile(r"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$")

OK, MISSING, MISMATCH, SKIPPED, INFO = "ok", "missing", "MISMATCH", "skipped", "info"


class SetupError(Exception):
    """A usage or input error (not a state of the checkout)."""


# --------------------------------------------------------------------------------------------------------------------
# Lock parsing
# --------------------------------------------------------------------------------------------------------------------

REQUIRED_ENTRY_KEYS = ("name", "kind", "version", "source", "license", "redistributable")
REDISTRIBUTABLE = ("yes", "no", "samples-with-notice")
KINDS = ("upm", "upm-git", "photon", "samples", "avatar-assets", "legs-rig", "project-config", "native", "asset-store", "tracked-third-party")


def parse_lock(text: str) -> dict:
    """Parse and validate SDKS.lock. Raises SetupError with a message naming the first problem."""
    try:
        lock = json.loads(text)
    except json.JSONDecodeError as error:
        raise SetupError(f"{LOCK_NAME} is not valid JSON: {error}") from error
    if not isinstance(lock, dict) or lock.get("schema") != 1:
        raise SetupError(f"{LOCK_NAME}: expected an object with schema 1")
    editor = lock.get("unity_editor")
    if not isinstance(editor, dict) or not editor.get("version"):
        raise SetupError(f"{LOCK_NAME}: unity_editor.version is required")
    entries = lock.get("entries")
    if not isinstance(entries, list) or not entries:
        raise SetupError(f"{LOCK_NAME}: entries must be a non-empty list")
    seen: set[str] = set()
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise SetupError(f"{LOCK_NAME}: entry {index} is not an object")
        for key in REQUIRED_ENTRY_KEYS:
            if key not in entry:
                raise SetupError(f"{LOCK_NAME}: entry {entry.get('name', index)!r} lacks {key!r}")
        name = entry["name"]
        if name in seen:
            raise SetupError(f"{LOCK_NAME}: duplicate entry {name!r}")
        seen.add(name)
        if entry["kind"] not in KINDS:
            raise SetupError(f"{LOCK_NAME}: entry {name!r} has unknown kind {entry['kind']!r}")
        if entry["redistributable"] not in REDISTRIBUTABLE:
            raise SetupError(f"{LOCK_NAME}: entry {name!r} has redistributable {entry['redistributable']!r}")
        sha = entry.get("sha256")
        if sha is not None and not re.fullmatch(r"[0-9a-f]{64}", str(sha)):
            raise SetupError(f"{LOCK_NAME}: entry {name!r} has a malformed sha256")
        if entry["kind"] == "upm-git" and not re.fullmatch(r"[0-9a-f]{40}", str(entry["source"].get("revision", ""))):
            raise SetupError(f"{LOCK_NAME}: git entry {name!r} must pin a 40-hex revision")
    return lock


def load_lock(root: Path) -> dict:
    path = root / LOCK_NAME
    if not path.is_file():
        raise SetupError(f"{path} not found")
    return parse_lock(path.read_text(encoding="utf-8"))


def entries_of(lock: dict, *kinds: str) -> list[dict]:
    return [entry for entry in lock["entries"] if entry["kind"] in kinds]


# --------------------------------------------------------------------------------------------------------------------
# Verification (no writes)
# --------------------------------------------------------------------------------------------------------------------

def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def check_sha256(path: Path, expected: str | None) -> str:
    """Return the actual sha256; raise SetupError if an expected value differs."""
    actual = sha256_file(path)
    if expected is not None and actual != expected:
        raise SetupError(f"sha256 of {path.name} is {actual[:12]}..., the lock expects {expected[:12]}...")
    return actual


def check_editor(root: Path, lock: dict) -> tuple[str, str]:
    path = root / "ProjectSettings/ProjectVersion.txt"
    wanted = lock["unity_editor"]["version"]
    if not path.is_file():
        return MISSING, f"{path.relative_to(root)} not found"
    match = re.search(r"^m_EditorVersion:\s*(\S+)", path.read_text(encoding="utf-8"), re.MULTILINE)
    actual = match.group(1) if match else "?"
    if actual != wanted:
        return MISMATCH, f"project is on {actual}, the lock wants {wanted}"
    return OK, wanted


def check_upm(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    """Compare the UPM entries with Packages/manifest.json and Packages/packages-lock.json."""
    rows: list[tuple[str, str, str]] = []
    try:
        manifest = json.loads((root / "Packages/manifest.json").read_text(encoding="utf-8"))["dependencies"]
    except (OSError, ValueError, KeyError) as error:
        return [("Packages/manifest.json", MISSING, str(error))]
    try:
        resolved = json.loads((root / "Packages/packages-lock.json").read_text(encoding="utf-8"))["dependencies"]
    except (OSError, ValueError, KeyError) as error:
        resolved = None
        rows.append(("Packages/packages-lock.json", MISSING, str(error)))
    locked_names = set()
    for entry in entries_of(lock, "upm", "upm-git"):
        name = entry["name"]
        locked_names.add(name)
        wanted = entry.get("manifest_value", entry["version"])
        if name not in manifest:
            rows.append((name, MISSING, "not in Packages/manifest.json"))
            continue
        if manifest[name] != wanted:
            rows.append((name, MISMATCH, "manifest.json pins a different value than the lock"))
            continue
        if resolved is not None:
            res = resolved.get(name)
            if res is None:
                rows.append((name, MISSING, "not resolved in packages-lock.json (open the project once)"))
                continue
            if entry["kind"] == "upm-git":
                if res.get("hash") != entry["source"]["revision"]:
                    rows.append((name, MISMATCH, "packages-lock.json resolves a different git revision"))
                    continue
            elif res.get("version") != entry["version"]:
                rows.append((name, MISMATCH, f"packages-lock.json resolves {res.get('version')}, the lock wants {entry['version']}"))
                continue
        rows.append((name, OK, entry["version"] if entry["kind"] == "upm" else "git " + entry["source"]["revision"][:10]))
    for name in sorted(set(manifest) - locked_names):
        rows.append((name, MISMATCH, "in manifest.json but not in SDKS.lock"))
    return rows


def photon_marker_pins(lock: dict) -> dict[str, str]:
    pins: dict[str, str] = {}
    for entry in entries_of(lock, "photon"):
        pins.update(entry.get("marker_sha256") or {})
    return pins


def check_photon_installed(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    rows = []
    pins = photon_marker_pins(lock)
    base = root / "Assets/Photon"
    for entry in entries_of(lock, "photon"):
        name = entry["name"]
        directory = root / entry["install_dir"]
        if not directory.is_dir():
            rows.append((name, MISSING, f"{entry['install_dir']} not installed"))
            continue
        state, detail = OK, entry["version"]
        for relative, expected in (entry.get("marker_sha256") or {}).items():
            marker = base / relative
            if not marker.is_file():
                state, detail = MISSING, f"{relative} not found"
                break
            if sha256_file(marker) != expected:
                state, detail = MISMATCH, f"{relative} differs from the lock (installed version is not {entry['version']})"
                break
        rows.append((name, state, detail))
    del pins
    return rows


def check_samples(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    rows = []
    for entry in entries_of(lock, "samples"):
        directory = root / entry["install_dir"]
        if (directory / "Oculus.AvatarSDK2.Samples.asmdef").is_file():
            rows.append((entry["name"], OK, entry["version"]))
        else:
            rows.append((entry["name"], MISSING, f"{entry['install_dir']} not installed"))
    return rows


LEGS_RIG_MARKER = "Resources/RTR00003.prefab"


def check_legs_rig(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    """The vendored legs rig is present (its prefab marks the copy; vendor-meta-avatar-legs-rig.py --check compares bytes)."""
    rows = []
    for entry in entries_of(lock, "legs-rig"):
        folder = entry["install_dir"]
        if (root / folder / LEGS_RIG_MARKER).is_file():
            rows.append((entry["name"], OK, entry["version"]))
        else:
            rows.append((entry["name"], MISSING, f"{folder} not installed (tools/vendor-meta-avatar-legs-rig.py --apply)"))
    return rows


def check_avatar_assets(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    rows = []
    for entry in entries_of(lock, "avatar-assets"):
        source = entry["source"]
        problems = []
        for relative, key in ((entry["install_dirs"]["core"] + "/.avatar_sdk_assets_version.txt", "core_assets_build"),
                              (entry["install_dirs"]["samples"] + "/SampleAssetsUnzipped/.avatar_sdk_assets_version.txt", "sample_assets_build")):
            path = root / relative
            if not path.is_file():
                problems.append(f"{relative} not installed")
            elif read_text_any(path) != source[key]:
                problems.append(f"{relative} is build {read_text_any(path)}, lock pins {source[key]}")
        for name in source["packaged_presets"]:
            if not (root / entry["install_dirs"]["samples"] / "SampleAssets" / name).is_file():
                problems.append(f"packaged preset {name} missing")
        mismatch = any("is build" in problem for problem in problems)
        rows.append((entry["name"], MISMATCH if mismatch else MISSING if problems else OK, "; ".join(problems[:3]) or entry["version"]))
    return rows


def project_config_target(entry: dict) -> Path:
    return Path(entry["install_path"])


def check_project_config(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    rows = []
    for entry in entries_of(lock, "project-config"):
        target = project_config_target(entry)
        present = (root / target).is_file()
        rows.append((entry["name"], OK if present else MISSING, str(target) if present else f"{target} not installed"))
    return rows


def install_project_config(root: Path, lock: dict) -> None:
    """Write each project-config asset from its committed template, only when absent; an existing file is never touched."""
    for entry in entries_of(lock, "project-config"):
        target = root / project_config_target(entry)
        template = root / entry["source"]["template"]
        meta_template = root / entry["source"]["meta_template"]
        if target.exists():
            continue
        if not template.is_file() or not meta_template.is_file():
            raise SetupError(f"{entry['name']}: template {entry['source']['template']} is missing")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(template, target)
        meta = target.with_name(target.name + ".meta")
        if not meta.exists():
            shutil.copyfile(meta_template, meta)


def read_text_any(path: Path) -> str:
    """Unity wrote some version files as UTF-16 with a BOM; accept that and UTF-8."""
    raw = path.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16").strip()
    return raw.decode("utf-8-sig").strip()


def native_required_files(platforms: list[str] | None = None) -> list[str]:
    """Every library file tools/fetch_plugins.py installs by default (as setup runs it): core and session, for Android and the host desktop."""
    tool = load_tool("fetch_plugins.py", "fetch_plugins")
    return [plugin.dest for library in tool.select_libraries(None) for plugin in tool.select_plugins(platforms, library)]


def native_missing(root: Path, platforms: list[str] | None = None) -> list[str]:
    return [dest for dest in native_required_files(platforms) if not (root / dest).is_file()]


def native_installed(root: Path, platforms: list[str] | None = None) -> bool:
    """True only when every library the fetcher installs is present; a half-done install is fetched again."""
    return not native_missing(root, platforms)


def check_native(root: Path, lock: dict) -> list[tuple[str, str, str]]:
    missing = native_missing(root)
    detail = "installed" if not missing else f"{', '.join(Path(m).name for m in missing)} missing; run without --check to fetch"
    return [(entry["name"], OK if not missing else MISSING, detail) for entry in entries_of(lock, "native") if entry.get("required")]


# --------------------------------------------------------------------------------------------------------------------
# Reuse of the existing bootstrap tools
# --------------------------------------------------------------------------------------------------------------------

def load_tool(filename: str, alias: str):
    spec = importlib.util.spec_from_file_location(alias, Path(__file__).resolve().parent / filename)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    # A module that defines a @dataclass must be findable in sys.modules while it executes (dataclasses looks its own module up
    # by name), or Python 3.9 and 3.14 raise "'NoneType' object has no attribute '__dict__'" on import.
    sys.modules[alias] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(alias, None)
        raise
    return module


def run_tool_main(module, argv: list[str]) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = module.main(argv)
    return code, out.getvalue(), err.getvalue()


# --------------------------------------------------------------------------------------------------------------------
# Photon install from the user's own download
# --------------------------------------------------------------------------------------------------------------------

def extract_unitypackage(package: Path, destination: Path) -> Path:
    """Rebuild the Assets/Photon tree of a .unitypackage under ``destination``; return that Assets/Photon path.

    A .unitypackage is a gzip tar of ``<guid>/{asset,asset.meta,pathname}``. Only paths below Assets/Photon are used and
    every path is checked, so a crafted archive cannot write outside ``destination``.
    """
    guid_re = re.compile(r"^[0-9a-f]{32}$")
    members: dict[str, dict[str, bytes]] = {}
    with tarfile.open(package, "r:*") as archive:
        for member in archive:
            if not member.isfile():
                continue
            parts = Path(member.name).parts
            if len(parts) != 2 or not guid_re.match(parts[0]) or parts[1] not in ("asset", "asset.meta", "pathname"):
                continue
            handle = archive.extractfile(member)
            if handle is not None:
                members.setdefault(parts[0], {})[parts[1]] = handle.read()
    photon_prefix = Path("Assets/Photon")
    for files in members.values():
        if "pathname" not in files:
            continue
        pathname = files["pathname"].decode("utf-8", "replace").splitlines()[0].strip()
        relative = Path(pathname)
        if relative.is_absolute() or ".." in relative.parts:
            raise SetupError("the package holds an unsafe path")
        try:
            relative.relative_to(photon_prefix)
        except ValueError:
            continue
        target = destination / relative
        if "asset" in files:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(files["asset"])
        else:
            target.mkdir(parents=True, exist_ok=True)
        if "asset.meta" in files:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.with_name(target.name + ".meta").write_bytes(files["asset.meta"])
    result = destination / photon_prefix
    if not result.is_dir():
        raise SetupError("the package holds no Assets/Photon folder")
    return result


ASSET_STORE_NOT_FOUND = ("Photon packages not found in the Unity Asset Store cache. Add \"Photon Fusion\" and \"Photon Voice 2\" (Photon Engine, free) "
                         "to your Asset Store account, download both in Unity's Package Manager > My Assets, then re-run this command "
                         "(or pass --photon-source for each .unitypackage).")


def asset_store_cache_dirs(env: dict | None = None, home: Path | None = None, platform: str | None = None) -> list[Path]:
    """Where Unity caches Asset Store downloads: ``Asset Store-5.x/Photon Engine`` under the per-OS Unity data folder."""
    env = os.environ if env is None else env
    home = home or Path.home()
    platform = platform or sys.platform
    if platform == "darwin":
        bases = [home / "Library/Unity"]
    elif platform.startswith("win"):
        bases = [Path(env["APPDATA"]) / "Unity"] if env.get("APPDATA") else []
    else:
        bases = [home / ".local/share/unity3d"]
    if env.get("ASSETSTORE_CACHE_PATH"):  # Unity's own override for the cache location
        bases.insert(0, Path(env["ASSETSTORE_CACHE_PATH"]))
        return [base / "Photon Engine" for base in bases[:1]] + [base / "Asset Store-5.x/Photon Engine" for base in bases[1:]]
    return [base / "Asset Store-5.x/Photon Engine" for base in bases]


def photon_package_entries(lock: dict) -> list[dict]:
    """Photon lock entries that name an official Asset Store package file."""
    return [entry for entry in entries_of(lock, "photon") if entry.get("asset_store_file")]


def locate_asset_store_packages(lock: dict, cache_dirs: list[Path]) -> dict[str, Path]:
    """Find each official package file by name under the cache dirs. Returns {entry name: path} for those found."""
    found: dict[str, Path] = {}
    for entry in photon_package_entries(lock):
        for base in cache_dirs:
            if not base.is_dir():
                continue
            matches = sorted(path for path in base.rglob(entry["asset_store_file"]) if path.is_file())
            if matches:
                found[entry["name"]] = matches[0]
                break
    return found


def verify_package(path: Path, lock: dict) -> tuple[str | None, str]:
    """Match a .unitypackage with the lock by size and sha256. Returns (entry name or None, detail)."""
    candidates = [entry for entry in photon_package_entries(lock) if entry.get("sha256")]
    if not candidates:
        return None, f"the lock records no package hash; sha256 is {sha256_file(path)[:12]}..."
    size = path.stat().st_size
    actual = None
    for entry in candidates:
        if entry.get("size") not in (None, size):
            continue
        actual = actual or sha256_file(path)
        if actual == entry["sha256"]:
            return entry["name"], f"{entry['asset_store_name']} sha256 matches the lock"
    raise SetupError(f"{path.name} (sha256 {sha256_file(path)[:12]}...) is not one of the official packages in the lock")


def ensure_root_folder_metas(photon: Path, roots) -> None:
    """The Asset Store packages carry no entry for a plain folder such as Assets/Photon/Fusion, so it has no .meta in the extracted
    tree. Unity makes one on import; the bootstrap wants one beside every copied root, so write the same kind of file with a
    GUID derived from the folder name (the same on every machine)."""
    for name in roots:
        meta = photon / f"{name}.meta"
        if (photon / name).is_dir() and not meta.exists():
            guid = hashlib.md5(f"spatialblend-photon-root:{name}".encode()).hexdigest()
            meta.write_text(f"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n"
                            "  userData: \n  assetBundleName: \n  assetBundleVariant: \n", encoding="utf-8")


def install_photon(root: Path, lock: dict, sources: list[str | Path], apply: bool) -> list[tuple[str, str, str]]:
    """Verify the user's Photon packages (or an extracted folder) against the lock and install them with bootstrap-photon-sdk."""
    bootstrap = load_tool("bootstrap-photon-sdk.py", "bootstrap_photon_sdk")
    pins = photon_marker_pins(lock)
    with tempfile.TemporaryDirectory(prefix="spatialblend-photon-") as scratch:
        notes: list[str] = []
        folder: Path | None = None
        merged = Path(scratch).resolve()
        for source_arg in sources:
            source = Path(os.path.expanduser(str(source_arg)))
            if not source.exists():
                raise SetupError(f"Photon source {source} does not exist")
            if source.is_file():
                if source.suffix != ".unitypackage":
                    raise SetupError("a Photon source must be a .unitypackage or an extracted Assets/Photon folder")
                try:
                    _name, detail = verify_package(source, lock)
                except SetupError as error:
                    return [("photon", MISMATCH, str(error))]
                notes.append(detail)
                folder = extract_unitypackage(source, merged)
            else:
                folder = source
        if folder is None:
            raise SetupError("no Photon source given")
        if folder.is_relative_to(merged):
            ensure_root_folder_metas(folder, bootstrap.COPIED_ROOTS)
        try:
            markers = bootstrap.read_source(folder)
        except ValueError as error:
            raise SetupError(f"not the pinned Photon download: {error}") from error
        for relative, actual in markers.items():
            wanted = pins.get(relative)
            if wanted is not None and actual != wanted:
                return [("photon", MISMATCH, f"{relative} of your download differs from the lock")]
        if not apply:
            return [("photon", INFO, "download verified; run without --check to install")]
        code, _out, err = run_tool_main(bootstrap, ["--project-root", str(root), "--source-photon", str(folder), "--apply"])
        if code != 0:
            return [("photon", MISMATCH, err.strip().splitlines()[-1] if err.strip() else "bootstrap-photon-sdk failed")]
        return [("photon", OK, "installed" + ("; " + "; ".join(notes) if notes else ""))]


# --------------------------------------------------------------------------------------------------------------------
# Per-user values (written only to git-ignored files)
# --------------------------------------------------------------------------------------------------------------------

def validate_guid(value: str, label: str) -> str:
    value = value.strip()
    if not GUID_RE.match(value):
        raise SetupError(f"{label} must be a GUID like 00000000-0000-0000-0000-000000000000")
    return value.lower()


def validate_url(value: str) -> str:
    value = value.strip()
    if not re.match(r"^https://[^\s/]+(/\S*)?$", value):
        raise SetupError("the TURN Worker URL must be an https:// URL")
    return value


def read_asset_field(text: str, field: str) -> str:
    match = re.search(rf"^    {field}: ?(.*)$", text, re.MULTILINE)
    return match.group(1).strip() if match else ""


def render_photon_settings(template: str, app_id: str, voice_id: str) -> str:
    return template.replace("@APP_ID_FUSION@", app_id).replace("@APP_ID_VOICE@", voice_id)


def write_photon_ids(root: Path, app_id: str | None, voice_id: str | None) -> tuple[str, str]:
    """Write the App IDs into the git-ignored Photon settings asset. Returns (state, detail); never the values."""
    photon = root / "Assets/Photon/Fusion"
    if not photon.is_dir():
        return SKIPPED, "Photon is not installed yet"
    path = root / PHOTON_SETTINGS_RELATIVE
    if path.is_file():
        text = path.read_text(encoding="utf-8")
    else:
        text = render_photon_settings((root / PHOTON_SETTINGS_TEMPLATE).read_text(encoding="utf-8"), "", "")
    new = text
    for field, value in (("AppIdFusion", app_id), ("AppIdVoice", voice_id)):
        if value is not None:
            new = re.sub(rf"^(    {field}:).*$", lambda m, v=value: f"{m.group(1)} {v}" if v else f"{m.group(1)} ", new, flags=re.MULTILINE)
    changed = new != text or not path.is_file()
    if changed:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(new, encoding="utf-8")
        meta = path.with_name(path.name + ".meta")
        if not meta.exists():
            meta.write_text((root / PHOTON_SETTINGS_META_TEMPLATE).read_text(encoding="utf-8").replace("@GUID@", uuid.uuid4().hex), encoding="utf-8")
    return OK, "written" if changed else "already set"


def photon_ids_state(root: Path) -> tuple[str, str, str]:
    path = root / PHOTON_SETTINGS_RELATIVE
    if not path.is_file():
        return MISSING, MISSING, "no PhotonAppSettings.asset"
    text = path.read_text(encoding="utf-8")
    return (OK if read_asset_field(text, "AppIdFusion") else MISSING, OK if read_asset_field(text, "AppIdVoice") else MISSING, "")


def credential_rows(root: Path, home: Path, env: dict | None = None) -> list[tuple[str, str, str]]:
    """The required credentials as summary rows, read from disk. (Divergence from the Editor guard, on purpose: this tool assumes TURN is wanted;
    a project with TURN deliberately off passes SPATIALBLEND_ALLOW_NO_TURN=1 here. It also does not see a secret already copied into Resources
    or a self-hosted Voice server. docs/required-credentials.md.) A waived one (SPATIALBLEND_ALLOW_NO_*=1) is INFO, never a failure.
    Only presence is reported, never a value."""
    env = os.environ if env is None else env
    fusion_state, voice_state, why = photon_ids_state(root)
    # Same rules as the Editor guard: a Fusion App ID must be a GUID, and the secret file counts trimmed.
    settings = root / PHOTON_SETTINGS_RELATIVE
    if fusion_state == OK and settings.is_file() and not GUID_RE.match(read_asset_field(settings.read_text(encoding="utf-8"), "AppIdFusion").strip()):
        fusion_state, why = MISSING, "AppIdFusion is not a GUID"
    secret_path = home / TURN_SECRET_HOME_RELATIVE
    secret_state = OK if secret_path.is_file() and secret_path.read_text(encoding="utf-8", errors="ignore").strip() else MISSING
    # As the Editor guard (RequiredCredentialsRuntime.TurnIsWanted): the secret is needed only when a Worker URL is configured.
    url_file = root / TURN_URL_RELATIVE
    settings_asset = root / "Assets/Resources/SpatialBlendSettings.asset"
    asset_text = settings_asset.read_text(encoding="utf-8", errors="ignore") if settings_asset.is_file() else ""
    def setting(field: str) -> str | None:
        match = re.search(rf"^ +{field}: ?(.*)$", asset_text, re.MULTILINE)
        return match.group(1).strip() if match else None
    asset_url = setting("turnCredentialsUrl")
    asset_url = DEFAULT_TURN_WORKER_URL if asset_url is None else asset_url   # a field the asset does not carry has its default
    web_rtc_off = setting("useWebRtc") == "0"
    turn_wanted = not web_rtc_off and bool(asset_url.strip() or (env.get(ENV_TURN_URL) or "").strip()
                                          or (url_file.is_file() and url_file.read_text(encoding="utf-8", errors="ignore").strip()))
    spec = (
        ("photon-app-id (user)", fusion_state, ALLOW_NO_PHOTON_APP_ID, "set",
         f"not set: the app cannot join a room; pass --photon-app-id or ${ENV_APP_ID} (the Photon dashboard's Fusion app)"),
        ("photon-voice-app-id (user)", voice_state, ALLOW_NO_VOICE_APP_ID, "set",
         f"not set: the app would have no voice; pass --photon-voice-app-id or ${ENV_VOICE_APP_ID} (a separate 'Voice' app in the Photon dashboard)"),
        ("turn-client-secret (user)", secret_state, ALLOW_NO_TURN, "present",
         f"not set: no TURN relay, so no connection on networks that block direct WebRTC; pass --turn-secret-file or ${ENV_TURN_SECRET_FILE} "
         f"(copied to ~/{TURN_SECRET_HOME_RELATIVE})"),
    )
    rows = []
    for name, state, allow, ok_detail, missing_detail in spec:
        if allow == ALLOW_NO_TURN and not turn_wanted and state != OK:
            rows.append((name, INFO, "not needed: no TURN Worker URL is configured, so TURN is off (the Photon relay is the fallback)"))
            continue
        if state == OK:
            rows.append((name, OK, ok_detail))
        elif env.get(allow) == "1":
            rows.append((name, INFO, f"missing, waived by {allow}=1 (compile-only: check_apk_contents.py rejects such an APK)"))
        else:
            rows.append((name, MISSING, f"{missing_detail}; a Quest/Windows build is refused (compile-only check: {allow}=1)"
                         + (f" [{why}]" if why and name.startswith("photon-app-id") else "")))
    rows.append(("scan-server (optional)", INFO,
                 "not a build input: no scan server by default (scanning off); name yours in SpatialBlendSettings "
                 "or a device spatialblend-local.json (docs/local-session-config.md)"))
    return rows


def write_turn_url(root: Path, url: str | None) -> tuple[str, str]:
    path = root / TURN_URL_RELATIVE
    if url is None or url == DEFAULT_TURN_WORKER_URL:
        if url == DEFAULT_TURN_WORKER_URL and path.is_file():
            path.unlink()
            return OK, "back to the committed default (override removed)"
        return OK, "committed default" if not path.is_file() else "override present"
    if path.is_file() and path.read_text(encoding="utf-8").strip() == url:
        return OK, "already set"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(url + "\n", encoding="utf-8")
    return OK, "override written"


def write_turn_secret(home: Path, secret_file: Path | None, secret_text: str | None) -> tuple[str, str]:
    """Copy the client secret to ~/.config/spatialblend/turn-client-secret (mode 600), which the Editor reads at build."""
    destination = home / TURN_SECRET_HOME_RELATIVE
    if secret_text is None and secret_file is not None:
        secret_text = secret_file.read_text(encoding="utf-8")
    if secret_text is None:
        return (OK, "present") if destination.is_file() and destination.stat().st_size else (MISSING, "no secret (optional: the build then has no TURN)")
    secret_text = secret_text.strip()
    if not secret_text or TEMPLATE_SECRET_MARKER in secret_text:
        raise SetupError("the client secret file is empty or still the template")
    if destination.is_file() and destination.read_text(encoding="utf-8").strip() == secret_text:
        os.chmod(destination, 0o600)
        return OK, "already set"
    destination.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    fd = os.open(destination, flags, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        handle.write(secret_text + "\n")
    os.chmod(destination, 0o600)
    return OK, "written (mode 600)"


def check_tmp(root: Path) -> tuple[str, str, str]:
    if (root / TMP_SETTINGS_RELATIVE).is_file():
        return ("tmp-essential-resources", OK, str(TMP_SETTINGS_RELATIVE))
    return ("tmp-essential-resources", MISSING, f"{TMP_SETTINGS_RELATIVE} not installed (the app would never leave the splash)")


def unity_binary(lock: dict, explicit: str | None, env: dict | None = None) -> Path | None:
    candidate = explicit or (env if env is not None else os.environ).get(ENV_UNITY_BIN)
    if candidate:
        return Path(candidate).expanduser()
    version = lock["unity_editor"]["version"]
    for path in (Path(f"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity"),
                 Path.home() / f"Unity/Hub/Editor/{version}/Editor/Unity"):
        if path.is_file():
            return path
    return None


def install_tmp(root: Path, lock: dict, explicit_unity: str | None) -> tuple[str, str, str]:
    """Import TMP Essential Resources in Unity batchmode. Returns the tmp row; never raises for an Editor failure."""
    name = "tmp-essential-resources"
    if (root / TMP_SETTINGS_RELATIVE).is_file():
        return check_tmp(root)
    try:
        manifest = json.loads((root / "Packages/manifest.json").read_text(encoding="utf-8"))["dependencies"]
    except (OSError, ValueError, KeyError):
        manifest = {}
    if not any(package in manifest for package in TMP_PACKAGES):
        return (name, MISSING, "neither com.unity.ugui nor com.unity.textmeshpro is pinned in Packages/manifest.json")
    if not (root / TMP_IMPORT_SCRIPT).is_file():
        return (name, MISSING, f"{TMP_IMPORT_SCRIPT} not found")
    unity = unity_binary(lock, explicit_unity)
    if unity is None or not unity.is_file():
        return (name, MISSING, f"Unity Editor not found; pass --unity-bin or set ${ENV_UNITY_BIN} (imports TMP in batchmode)")
    log = Path(tempfile.gettempdir()) / f"spatialblend-tmp-import-{os.getpid()}.log"
    # unity_clean_env keeps secrets out of the Editor log (batchmode prints the whole environment).
    command = ["bash", "-c", 'source "$1"; shift; unity_clean_env "$@"', "_", str(root / "tools/lib/unity-env.sh"), str(unity),
               "-batchmode", "-nographics", "-projectPath", str(root), "-executeMethod", TMP_IMPORT_METHOD, "-logFile", str(log)]
    print(f"importing TextMesh Pro Essential Resources with Unity (log: {log}); a fresh checkout's first import can take many minutes", file=sys.stderr)
    result = subprocess.run(command, cwd=root)
    if result.returncode != 0 or not (root / TMP_SETTINGS_RELATIVE).is_file():
        tail = ""
        with contextlib.suppress(OSError):
            tail = next((ln for ln in reversed(log.read_text(errors="ignore").splitlines()) if "TmpEssentialsImport" in ln or "rror" in ln), "")
        return (name, MISSING, f"Unity exited {result.returncode}; see {log}" + (f": {tail[:160]}" if tail else ""))
    return check_tmp(root)


def written_repo_paths() -> list[Path]:
    """Every repo-relative path this tool can write for a user's own values; all must be git-ignored."""
    asset = PHOTON_SETTINGS_RELATIVE
    url = TURN_URL_RELATIVE
    return [asset, asset.with_name(asset.name + ".meta"), url, url.with_name(url.name + ".meta"),
            Path("Assets/Resources/SpatialBlendTurnClientSecret.txt")]


# --------------------------------------------------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------------------------------------------------

def prompt(label: str, *, secret: bool = False) -> str:
    try:
        return (getpass.getpass(f"{label}: ") if secret else input(f"{label}: ")).strip()
    except EOFError:
        return ""


def print_summary(rows: list[tuple[str, str, str]]) -> None:
    # Passing registry packages are folded into one line; anything else is listed.
    folded = [row for row in rows if row[0].startswith("com.") and row[1] == OK]
    shown = [row for row in rows if row not in folded]
    if folded:
        shown.insert(1, (f"{len(folded)} UPM packages", OK, "match SDKS.lock (manifest.json and packages-lock.json)"))
    width = max((len(name) for name, _s, _d in shown), default=10)
    print("\nSDK setup summary")
    print("-----------------")
    for name, state, detail in shown:
        print(f"  {name.ljust(width)}  {state.ljust(8)}  {detail}")
    mismatches = sum(1 for _n, s, _d in rows if s == MISMATCH)
    missing = sum(1 for _n, s, _d in rows if s == MISSING)
    print(f"\n{len(rows)} items: {mismatches} mismatched, {missing} missing.")


def exit_code(rows: list[tuple[str, str, str]]) -> int:
    if any(state == MISMATCH for _n, state, _d in rows):
        return EXIT_MISMATCH
    if any(state == MISSING for _n, state, _d in rows):
        return EXIT_MISSING
    return EXIT_OK


def run(argv: list[str] | None = None, *, root: Path = ROOT, home: Path | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="verify only; change nothing, prompt for nothing")
    parser.add_argument("--non-interactive", action="store_true", help="never prompt (CI, lab machines)")
    parser.add_argument("--photon-source", action="append",
                        help="a Photon .unitypackage or an extracted Assets/Photon folder (repeat for Fusion and Voice 2). "
                             "Default: the Unity Asset Store cache")
    parser.add_argument("--asset-store-dir", help="Asset Store cache folder to search instead of the default for this OS")
    parser.add_argument("--photon-app-id", help=f"Photon Fusion App ID (or ${ENV_APP_ID})")
    parser.add_argument("--photon-voice-app-id", help=f"Photon Voice App ID, if used (or ${ENV_VOICE_APP_ID})")
    parser.add_argument("--turn-worker-url", help=f"your TURN credentials Worker URL (or ${ENV_TURN_URL}); none by default (TURN off)")
    parser.add_argument("--turn-secret-file", help=f"file that holds the TURN client secret (or ${ENV_TURN_SECRET_FILE})")
    parser.add_argument("--native-from-dir", help="install the native libraries from a local directory of library files with a SHA256SUMS file")
    parser.add_argument("--skip-native", action="store_true", help="do not fetch the native libraries")
    parser.add_argument("--skip-samples", action="store_true", help="do not import the Meta Avatars sample scripts")
    parser.add_argument("--package-cache-dir", help="a Library/PackageCache directory to read the Meta Avatars packages from "
                                                    "(default: this project's own; needed in a fresh worktree that has no Library yet)")
    parser.add_argument("--skip-avatar-assets", action="store_true", help="do not install the Meta Avatars core assets, packaged presets and OculusProjectConfig")
    parser.add_argument("--skip-tmp", action="store_true", help="do not import TextMesh Pro Essential Resources")
    parser.add_argument("--unity-bin", help=f"Unity Editor binary for the TMP import (or ${ENV_UNITY_BIN}; default: the Hub install of the locked version)")
    parser.add_argument("--require-user-vars", action="store_true", help="a missing required credential (Photon App ID, Voice App ID, TURN client secret) fails the run (always so with --check)")
    args = parser.parse_args(argv)
    root = root.resolve()
    home = home or Path.home()
    apply = not args.check
    interactive = apply and not args.non_interactive and sys.stdin.isatty()

    try:
        lock = load_lock(root)
        rows: list[tuple[str, str, str]] = []

        state, detail = check_editor(root, lock)
        rows.append(("unity-editor", state, detail))
        rows.extend(check_upm(root, lock))

        # Photon
        photon_rows = check_photon_installed(root, lock)
        photon_ok = all(state == OK for _n, state, _d in photon_rows)
        sources: list = list(args.photon_source or [])
        if not photon_ok and not sources:
            cache = [Path(args.asset_store_dir).expanduser()] if args.asset_store_dir else asset_store_cache_dirs()
            found = locate_asset_store_packages(lock, cache)
            if found and len(found) == len(photon_package_entries(lock)):
                sources = list(found.values())
                rows.append(("photon-asset-store-cache", OK, "found " + ", ".join(path.name for path in found.values())))
            elif interactive:
                answer = prompt("Photon packages not found in the Asset Store cache. Path to a .unitypackage or Assets/Photon folder (empty to skip)")
                sources = [answer] if answer else []
            if not sources:
                rows.append(("photon-asset-store-cache", MISSING, ASSET_STORE_NOT_FOUND))
        if sources and (apply or args.check) and not photon_ok:
            rows.extend(install_photon(root, lock, sources, apply))
            photon_rows = check_photon_installed(root, lock) if apply else photon_rows
        elif sources and args.photon_source:
            rows.extend(install_photon(root, lock, sources, apply))
        rows.extend(photon_rows)

        # Meta Avatars samples
        sample_rows = check_samples(root, lock)
        if apply and not args.skip_samples and any(state != OK for _n, state, _d in sample_rows):
            tool = load_tool("bootstrap-meta-avatar-samples.py", "bootstrap_meta_avatar_samples")
            tool_args = ["--project-root", str(root), "--apply"]
            if args.package_cache_dir:
                found = sorted(Path(args.package_cache_dir).expanduser().glob("com.meta.xr.sdk.avatars@*"))
                if len(found) == 1:
                    tool_args += ["--package-cache", str(found[0])]
            code, _out, err = run_tool_main(tool, tool_args)
            if code != 0:
                rows.append(("meta-avatars-samples", MISSING, (err.strip().splitlines() or ["bootstrap failed"])[-1]))
                sample_rows = []
            else:
                sample_rows = check_samples(root, lock)
        rows.extend(sample_rows)

        # Meta Avatars legs rig (git-ignored), from the same package cache
        legs_rows = check_legs_rig(root, lock)
        if apply and not args.skip_samples and any(state != OK for _n, state, _d in legs_rows):
            tool = load_tool("vendor-meta-avatar-legs-rig.py", "vendor_meta_avatar_legs_rig")
            tool_args = ["--project-root", str(root), "--apply"]
            if args.package_cache_dir:
                found = sorted(Path(args.package_cache_dir).expanduser().glob("com.meta.xr.sdk.avatars@*"))
                if len(found) == 1:
                    tool_args += ["--package-cache", str(found[0])]
            code, _out, err = run_tool_main(tool, tool_args)
            if code != 0:
                rows.append(("meta-avatars-legs-rig", MISSING, (err.strip().splitlines() or ["vendor failed"])[-1][:200]))
                legs_rows = []
            else:
                legs_rows = check_legs_rig(root, lock)
        rows.extend(legs_rows)

        # Meta Avatars core assets and packaged presets, and the Oculus project config
        asset_rows = check_avatar_assets(root, lock)
        if apply and not args.skip_avatar_assets and any(state != OK for _n, state, _d in asset_rows):
            tool = load_tool("bootstrap-meta-avatar-assets.py", "bootstrap_meta_avatar_assets")
            tool_args = ["--project-root", str(root), "--apply"]
            if args.package_cache_dir:
                tool_args += ["--package-cache-dir", args.package_cache_dir]
            code, _out, err = run_tool_main(tool, tool_args)
            if code != 0:
                rows.append(("meta-avatars-assets", MISSING, (err.strip().splitlines() or ["bootstrap failed"])[-1][:200]))
                asset_rows = []
            else:
                asset_rows = check_avatar_assets(root, lock)
        rows.extend(asset_rows)
        if apply and not args.skip_avatar_assets:
            install_project_config(root, lock)
        rows.extend(check_project_config(root, lock))

        # Native libraries
        native_rows = check_native(root, lock)
        if apply and not args.skip_native and any(state != OK for _n, state, _d in native_rows):
            fetch_args = ["--project-root", str(root)]
            if args.native_from_dir:
                fetch_args += ["--from-dir", args.native_from_dir]
            tool = load_tool("fetch_plugins.py", "fetch_plugins")
            try:
                code, _out, err = run_tool_main(tool, fetch_args)
            except SystemExit as error:
                code, err = int(error.code or 1), "fetch_plugins exited"
            if code != 0:
                rows.append(("libspatialblend", MISSING, (err.strip().splitlines() or ["fetch_plugins failed"])[-1][:200]))
                native_rows = []
            else:
                native_rows = check_native(root, lock)
        rows.extend(native_rows)

        # TextMesh Pro Essential Resources
        if args.skip_tmp:
            pass
        elif apply:
            rows.append(install_tmp(root, lock, args.unity_bin))
        else:
            rows.append(check_tmp(root))

        # Per-user values
        app_id = args.photon_app_id or os.environ.get(ENV_APP_ID)
        voice_id = args.photon_voice_app_id or os.environ.get(ENV_VOICE_APP_ID)
        turn_url = args.turn_worker_url or os.environ.get(ENV_TURN_URL)
        secret_file_arg = args.turn_secret_file or os.environ.get(ENV_TURN_SECRET_FILE)
        fusion_state, voice_state, _why = photon_ids_state(root)
        if interactive:
            if not app_id and fusion_state != OK:
                app_id = prompt("Your Photon Fusion App ID (dashboard.photonengine.com; empty to skip)", secret=True) or None
            if not voice_id and voice_state != OK:
                voice_id = prompt("Your Photon Voice App ID (a separate 'Voice' application in the dashboard; without it the app has no voice and "
                                  "a Quest build is refused; empty to skip)", secret=True) or None
            if not turn_url:
                answer = prompt("Your TURN Worker URL (tools/turn-credentials-worker; empty for none: TURN off)")
                turn_url = answer or None
            if turn_url and not secret_file_arg and not (home / TURN_SECRET_HOME_RELATIVE).is_file():
                secret_text = prompt("TURN client secret (hidden; empty to skip)", secret=True)
            else:
                secret_text = None
        else:
            secret_text = None
        if apply:
            ids = (validate_guid(app_id, "the Photon App ID") if app_id else None,
                   validate_guid(voice_id, "the Photon Voice App ID") if voice_id else None)
            if ids[0] is not None or ids[1] is not None:
                state, detail = write_photon_ids(root, *ids)
                rows.append(("photon-app-id (user)", state if state != SKIPPED else MISSING, detail))
            if turn_url:
                state, detail = write_turn_url(root, validate_url(turn_url))
                rows.append(("turn-worker-url (user)", state, detail))
            if secret_file_arg or secret_text:
                state, detail = write_turn_secret(home, Path(secret_file_arg).expanduser() if secret_file_arg else None, secret_text)
                rows.append(("turn-client-secret (user)", state, detail))
        # The required rows are re-read from disk, so they are the truth after anything this run just wrote.
        rows = [row for row in rows if not row[0].startswith(REQUIRED_CREDENTIAL_ROWS)]
        rows.extend(credential_rows(root, home))
    except SetupError as error:
        print(f"setup_sdks: {error}", file=sys.stderr)
        return EXIT_MISSING

    # Per-user values never fail a run unless asked to.
    # A --check run verifies a checkout, so a missing required credential fails it; an applying run reports them and
    # fails only with --require-user-vars.
    gating = [(n, s, d) for n, s, d in rows
              if ("(user)" not in n and "(optional)" not in n) or ((args.require_user_vars or args.check) and n.startswith(REQUIRED_CREDENTIAL_ROWS))]
    print_summary(rows)
    code = exit_code(gating)
    if code:
        print("\nFix the items above, then run: python3 tools/setup_sdks.py" + (" --check" if args.check else ""))
    return code


if __name__ == "__main__":
    raise SystemExit(run())
