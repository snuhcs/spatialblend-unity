#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Check the contents of a built Quest APK (T4.5).

  python3 tools/check_apk_contents.py PATH.apk [--aapt PATH_TO_AAPT]

The APK must be arm64-only for the native plugins: lib/arm64-v8a/ holds
libspatialblend.so, libspatialblend_session.so and the WebRTC libraries
(libwebrtc.so, libjingle_peerconnection_so.so), no lib/ entry exists for another ABI,
and no debug libspatialblend_d.so is packaged. The v1 core had the file name
libspatialblend.so too, so it is told apart by content: the packaged libspatialblend.so
must not contain the v1 symbol spatialblend_create_space (the current core exports
sb_* only). With --aapt, the manifest permissions must include the v1 list.
Exit code 0 means every check passed.

Two build-parity checks guard the fresh-checkout failures seen on a real Quest 3:
- assets/SpatialBlendBuildStamp.json must exist and name the TextMesh Pro settings and default font asset. The Editor's
  TmpEssentialsBuildGuard writes it into StreamingAssets only after verifying TMP_Settings (so an APK built without TMP
  Essential Resources, whose control panel never builds, has no stamp or a failing build).
- the same stamp must say every required credential is in the build (RequiredCredentialsBuildGuard in the Editor refuses to build
  without them; this checks the finished APK from outside, so a build made with an SPATIALBLEND_ALLOW_NO_* opt-out is rejected):
  voiceAppIdConfigured (no Voice App ID: nobody hears anybody), photonAppIdConfigured (no Fusion App ID: no room can be joined) and
  turnClientSecretConfigured (no TURN secret: no relay; not required when the stamp says turnRequired is false, TURN deliberately off).
  Each key must be exactly true. A stamp with no such key (a build from before the guard) is rejected as well. docs/required-credentials.md.
- the game data must be one stored (uncompressed outer container) assets/bin/Data/data.unity3d, with no
  sharedassets*.assets.split* entries (the default deflate build splits it and the first scene load takes minutes).
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path

# libwebrtc.so and libjingle_peerconnection_so.so come from com.unity.webrtc (the direct WebRTC carrier of the core link).
REQUIRED_LIBS = ("libspatialblend.so", "libspatialblend_session.so", "libwebrtc.so", "libjingle_peerconnection_so.so")
FORBIDDEN_LIBS = ("libspatialblend_d.so",)
CORE_LIB = "libspatialblend.so"
# Exported by the v1 core and by nothing in the current one.
V1_CORE_SYMBOL = b"spatialblend_create_space"
ABI = "lib/arm64-v8a/"
STAMP_ENTRY = "assets/SpatialBlendBuildStamp.json"
VOICE_STAMP_KEY = "voiceAppIdConfigured"
FUSION_STAMP_KEY = "photonAppIdConfigured"
TURN_STAMP_KEY = "turnClientSecretConfigured"
TURN_REQUIRED_STAMP_KEY = "turnRequired"
DATA_ENTRY = "assets/bin/Data/data.unity3d"
V1_PERMISSIONS = (
    "android.permission.CAMERA",
    "horizonos.permission.HEADSET_CAMERA",
    "android.permission.READ_EXTERNAL_STORAGE",
    "android.permission.WRITE_EXTERNAL_STORAGE",
    "android.permission.RECORD_AUDIO",
    "com.oculus.permission.USE_SCENE",
)


def check_libs(names: list[str], read=None) -> list[str]:
    """read(name) returns the bytes of an APK entry; without it the v1 content check is skipped."""
    problems: list[str] = []
    present = {n for n in names if n.startswith("lib/")}
    for lib in REQUIRED_LIBS:
        if ABI + lib not in present:
            problems.append(f"missing {ABI}{lib}")
    for name in sorted(present):
        if not name.startswith(ABI):
            problems.append(f"native library outside {ABI}: {name}")
        base = name.rsplit("/", 1)[-1]
        if base in FORBIDDEN_LIBS:
            problems.append(f"forbidden debug library packaged: {name}")
    if read is not None and ABI + CORE_LIB in present:
        if V1_CORE_SYMBOL in read(ABI + CORE_LIB):
            problems.append(f"{ABI}{CORE_LIB} exports the v1 core API ({V1_CORE_SYMBOL.decode()})")
    return problems


def check_stamp_credentials(stamp: dict) -> list[str]:
    """One problem per required credential the build stamp does not say is in the build. Values are never in the stamp."""
    problems: list[str] = []
    if stamp.get(FUSION_STAMP_KEY) is not True:
        problems.append(f"{STAMP_ENTRY} says the build has no Photon Fusion App ID ({FUSION_STAMP_KEY} is not true): no room could be "
                        "created or joined. Set it (python3 tools/setup_sdks.py --photon-app-id <id>) and rebuild")
    if stamp.get(VOICE_STAMP_KEY) is not True:
        problems.append(f"{STAMP_ENTRY} says the build has no Photon Voice App ID ({VOICE_STAMP_KEY} is not true): the app would send and "
                        "receive no voice. Set PhotonAppSettings.AppIdVoice (python3 tools/setup_sdks.py --photon-voice-app-id <id>) and rebuild")
    if stamp.get(TURN_STAMP_KEY) is not True and stamp.get(TURN_REQUIRED_STAMP_KEY) is not False:
        problems.append(f"{STAMP_ENTRY} says the build has no TURN client secret ({TURN_STAMP_KEY} is not true): no relay on networks that "
                        "block a direct connection. Provide it (python3 tools/setup_sdks.py --turn-secret-file <file>) and rebuild")
    return problems


def check_build_parity(infos: dict[str, "zipfile.ZipInfo"], read=None) -> list[str]:
    """infos maps APK entry name to its ZipInfo; read(name) returns bytes (without it the stamp content is not checked)."""
    problems: list[str] = []
    data = infos.get(DATA_ENTRY)
    if data is None:
        problems.append(f"missing {DATA_ENTRY} (game data is not one LZ4 data.unity3d)")
    elif data.compress_type != zipfile.ZIP_STORED:
        problems.append(f"{DATA_ENTRY} is Deflate-compressed; build with BuildOptions.CompressWithLz4HC")
    split = sorted(n for n in infos if re.search(r"(^|/)sharedassets\d*\.assets\.split\d+$", n))
    if split:
        problems.append(f"{len(split)} split sharedassets entries (e.g. {split[0]}): the game data was deflated, not LZ4")
    if STAMP_ENTRY not in infos:
        problems.append(f"missing {STAMP_ENTRY}: TextMesh Pro Essential Resources were not verified at build time (run python3 tools/setup_sdks.py)")
    elif read is not None:
        try:
            stamp = json.loads(read(STAMP_ENTRY).decode("utf-8-sig"))
        except ValueError:
            stamp = {}
        if not stamp.get("tmpSettings") or not stamp.get("tmpDefaultFontAsset"):
            problems.append(f"{STAMP_ENTRY} does not name the TMP settings and default font asset")
        problems.extend(check_stamp_credentials(stamp))
    return problems


def parse_permissions(aapt_output: str) -> set[str]:
    perms = set(re.findall(r"uses-permission(?:-sdk-\d+)?: name='([^']+)'", aapt_output))
    perms |= set(re.findall(r"uses-permission:\s+(?:\w+=\S+\s+)*name=\"?([\w.]+)", aapt_output))
    return perms


def check_permissions(perms: set[str]) -> list[str]:
    return [f"missing permission {p}" for p in V1_PERMISSIONS if p not in perms]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path)
    parser.add_argument("--aapt", default=None, help="aapt or aapt2 binary for the manifest check")
    args = parser.parse_args(argv)
    if not args.apk.is_file():
        print(f"APK not found: {args.apk}", file=sys.stderr)
        return 2
    with zipfile.ZipFile(args.apk) as apk:
        names = apk.namelist()
        problems = check_libs(names, apk.read)
        problems += check_build_parity({i.filename: i for i in apk.infolist()}, apk.read)
    for name in sorted(n for n in names if n.startswith("lib/")):
        print(name)
    if args.aapt:
        out = subprocess.run(
            [args.aapt, "dump", "permissions", str(args.apk)],
            capture_output=True, text=True, check=True,
        ).stdout
        perms = parse_permissions(out)
        problems += check_permissions(perms)
    for problem in problems:
        print("FAIL:", problem, file=sys.stderr)
    print("apk contents:", "FAIL" if problems else "OK")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
