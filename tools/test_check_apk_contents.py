#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Tests for check_apk_contents. Builds synthetic APK zips; no Unity needed."""

from __future__ import annotations

import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_apk_contents as check  # noqa: E402


STAMP = (b'{"tmpSettings":"3f5b5ff68a1f4b5b8b7fd1d0b4b1c7a2","tmpDefaultFontAsset":"LiberationSans SDF",'
         b'"voiceAppIdConfigured":true,"photonAppIdConfigured":true,"turnClientSecretConfigured":true,"turnRequired":true}')


def _apk(root: Path, files: list[str], core: bytes = b"sb_create_session", parity: bool = True,
         data_compression: int = zipfile.ZIP_STORED, stamp: bytes | None = STAMP, split: bool = False) -> Path:
    path = root / "t.apk"
    with zipfile.ZipFile(path, "w") as z:
        for f in files:
            z.writestr(f, core if f.endswith("/libspatialblend.so") else b"x")
        if parity:
            z.writestr(zipfile.ZipInfo(check.DATA_ENTRY), b"UnityFS" * 100, compress_type=data_compression)
            if stamp is not None:
                z.writestr(check.STAMP_ENTRY, stamp)
        if split:
            z.writestr("assets/bin/Data/sharedassets0.assets.split0", b"x", compress_type=zipfile.ZIP_DEFLATED)
    return path


GOOD = [
    "lib/arm64-v8a/libspatialblend.so",
    "lib/arm64-v8a/libspatialblend_session.so",
    "lib/arm64-v8a/libwebrtc.so",
    "lib/arm64-v8a/libjingle_peerconnection_so.so",
    "lib/arm64-v8a/libil2cpp.so",
]


class ApkContentsTests(unittest.TestCase):
    def test_good_apk_passes(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(check.main([str(_apk(Path(d), GOOD))]), 0)

    def test_deflated_data_unity3d_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD, data_compression=zipfile.ZIP_DEFLATED)
            self.assertEqual(check.main([str(apk)]), 1)

    def test_split_sharedassets_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD, parity=False, split=True)
            problems = check.check_build_parity({i.filename: i for i in zipfile.ZipFile(apk).infolist()})
            self.assertTrue(any("data.unity3d" in p for p in problems))
            self.assertTrue(any("split sharedassets" in p for p in problems))

    def test_missing_tmp_stamp_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD, stamp=None)
            self.assertEqual(check.main([str(apk)]), 1)

    def test_empty_tmp_stamp_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD, stamp=b'{"tmpSettings":""}')
            self.assertEqual(check.main([str(apk)]), 1)

    BASE = b'{"tmpSettings":"3f5b5ff68a1f4b5b8b7fd1d0b4b1c7a2","tmpDefaultFontAsset":"LiberationSans SDF"'

    def _stamp(self, **members) -> bytes:
        import json
        values = {"voiceAppIdConfigured": True, "photonAppIdConfigured": True, "turnClientSecretConfigured": True, "turnRequired": True}
        values.update(members)
        return self.BASE + b"," + json.dumps(values)[1:-1].encode() + b"}"

    def _problems(self, stamp: bytes) -> list[str]:
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD, stamp=stamp)
            with zipfile.ZipFile(apk) as z:
                return check.check_build_parity({i.filename: i for i in z.infolist()}, z.read)

    def test_stamp_without_voice_app_id_fails(self):
        base = self.BASE
        for stamp in (base + b"}", base + b',"voiceAppIdConfigured":false}', base + b',"voiceAppIdConfigured":"true"}'):
            with tempfile.TemporaryDirectory() as d:
                apk = _apk(Path(d), GOOD, stamp=stamp)
                self.assertEqual(check.main([str(apk)]), 1, stamp)

    def test_complete_stamp_passes(self):
        self.assertEqual(self._problems(self._stamp()), [])

    def test_each_missing_required_credential_is_rejected_and_named(self):
        for key, word in (("voiceAppIdConfigured", "Voice App ID"), ("photonAppIdConfigured", "Fusion App ID"),
                          ("turnClientSecretConfigured", "TURN client secret")):
            for bad in (False, "true", 1, None):
                problems = self._problems(self._stamp(**{key: bad}))
                self.assertEqual(len(problems), 1, (key, bad, problems))
                self.assertIn(word, problems[0])
                self.assertIn(key, problems[0])
            with tempfile.TemporaryDirectory() as d:
                self.assertEqual(check.main([str(_apk(Path(d), GOOD, stamp=self._stamp(**{key: False})))]), 1, key)

    def test_every_missing_credential_is_listed_at_once(self):
        problems = self._problems(self._stamp(voiceAppIdConfigured=False, photonAppIdConfigured=False, turnClientSecretConfigured=False))
        self.assertEqual(len(problems), 3, problems)

    def test_a_stamp_from_before_the_guard_is_rejected(self):
        # Only the voice key (a stamp from before the guard): the Fusion and TURN keys are missing, so the APK cannot be shown to have them.
        problems = self._problems(self.BASE + b',"voiceAppIdConfigured":true}')
        self.assertEqual(len(problems), 2, problems)

    def test_turn_deliberately_off_does_not_need_the_secret(self):
        self.assertEqual(self._problems(self._stamp(turnClientSecretConfigured=False, turnRequired=False)), [])
        self.assertEqual(len(self._problems(self._stamp(turnClientSecretConfigured=False, turnRequired="false"))), 1)

    def test_the_problem_text_never_contains_a_value(self):
        text = " ".join(self._problems(self._stamp(voiceAppIdConfigured=False, photonAppIdConfigured=False, turnClientSecretConfigured=False)))
        self.assertNotRegex(text, r"[0-9a-f]{8}-[0-9a-f]{4}-")

    def test_v1_core_by_content_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD, core=b"\0spatialblend_create_space\0")
            self.assertEqual(check.main([str(apk)]), 1)

    def test_debug_library_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), GOOD + ["lib/arm64-v8a/libspatialblend_d.so"])
            self.assertEqual(check.main([str(apk)]), 1)

    def test_missing_session_fails(self):
        with tempfile.TemporaryDirectory() as d:
            apk = _apk(Path(d), [g for g in GOOD if "session" not in g])
            self.assertEqual(check.main([str(apk)]), 1)

    def test_missing_core_fails(self):
        problems = check.check_libs([g for g in GOOD if not g.endswith("/libspatialblend.so")])
        self.assertEqual(problems, ["missing lib/arm64-v8a/libspatialblend.so"])

    def test_missing_webrtc_fails(self):
        problems = check.check_libs([g for g in GOOD if "libwebrtc" not in g])
        self.assertEqual(problems, ["missing lib/arm64-v8a/libwebrtc.so"])

    def test_another_abi_fails(self):
        problems = check.check_libs(GOOD + ["lib/x86_64/libwebrtc.so"])
        self.assertEqual(problems, ["native library outside lib/arm64-v8a/: lib/x86_64/libwebrtc.so"])

    def test_permissions_parse_and_compare(self):
        out = "\n".join(f"uses-permission: name='{p}'" for p in check.V1_PERMISSIONS)
        self.assertEqual(check.check_permissions(check.parse_permissions(out)), [])
        out2 = "uses-permission: name='android.permission.CAMERA'"
        self.assertEqual(len(check.check_permissions(check.parse_permissions(out2))), 5)


if __name__ == "__main__":
    unittest.main()
