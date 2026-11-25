# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Stdlib tests for the Freeform native gate's no-UPM package preflight."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent / "freeform-native-verification"


class OfflinePackageClosureTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("offline_package_closure", ROOT / "verify-offline-package-closure.py")
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        base = Path(self.temp.name)
        self.root, self.copy, self.editor = base / "repo", base / "copy", base / "Unity.app"
        self.unity_binary = self.editor / "Contents/MacOS/Unity"
        self.write_json(self.root / "Packages/manifest.json", {"dependencies": {"example.registry": "1.2.3", "example.builtin": "1.0.0"}})
        self.write_json(self.root / "Packages/packages-lock.json", {"dependencies": {
            "example.registry": {"version": "1.2.3", "depth": 0, "source": "registry", "dependencies": {}},
            "example.builtin": {"version": "1.0.0", "depth": 0, "source": "builtin", "dependencies": {}},
        }})
        self.copy_file("Packages/manifest.json")
        self.copy_file("Packages/packages-lock.json")
        self.package(self.root / "Library/PackageCache/example.registry@hash", "example.registry", "1.2.3", "registry bytes")
        self.package(self.copy / "Library/PackageCache/example.registry@hash", "example.registry", "1.2.3", "registry bytes")
        builtin = self.editor / "Contents/Resources/PackageManager/BuiltInPackages/example.builtin"
        self.package(builtin, "example.builtin", "1.0.0", "builtin bytes")
        info = self.editor / "Contents/Info.plist"
        info.parent.mkdir(parents=True, exist_ok=True)
        info.write_bytes(b'<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleShortVersionString</key><string>test-unity-1</string></dict></plist>')
        self.unity_binary.parent.mkdir(parents=True, exist_ok=True)
        self.unity_binary.write_text("test binary")
        self.write_json(self.root / "Library/PackageManager/projectResolution.json", {"outputs": {
            "example.registry@1.2.3": {"name": "example.registry", "source": "registry", "version": "1.2.3", "fingerprint": "local-registry"},
            "example.builtin@1.0.0": {"name": "example.builtin", "source": "builtin", "version": "1.0.0"},
        }})

    @staticmethod
    def write_json(path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))

    @staticmethod
    def package(path, name, version, content):
        path.mkdir(parents=True, exist_ok=True)
        (path / "package.json").write_text(json.dumps({"name": name, "version": version}))
        (path / "payload.bin").write_text(content)

    def copy_file(self, relative):
        destination = self.copy / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes((self.root / relative).read_bytes())

    def test_writes_hashed_local_closure(self):
        self.module.main(self.root, self.copy, self.unity_binary)
        result = json.loads((self.copy / "PACKAGE_CLOSURE_SHA256.json").read_text())
        self.assertEqual(result["unity_version"], "test-unity-1")
        self.assertEqual(result["unity_binary"], str(self.unity_binary))
        self.assertEqual([package["name"] for package in result["packages"]], ["example.builtin", "example.registry"])
        self.assertEqual(result["packages"][1]["tree_sha256"], self.module.tree_digest(self.root / "Library/PackageCache/example.registry@hash"))

    def test_missing_cached_artifact_fails_before_any_unity_process(self):
        for path in (self.root / "Library/PackageCache").iterdir():
            path.rename(path.with_name("not-the-locked-package"))
        with self.assertRaisesRegex(ValueError, "Missing or ambiguous local package cache artifact"):
            self.module.main(self.root, self.copy, self.editor)

    def test_substituted_copied_artifact_is_rejected(self):
        (self.copy / "Library/PackageCache/example.registry@hash/payload.bin").write_text("wrong")
        with self.assertRaisesRegex(ValueError, "Copied package artifact differs"):
            self.module.main(self.root, self.copy, self.editor)

    def test_lock_dependency_must_be_declared_locally(self):
        lock_path = self.root / "Packages/packages-lock.json"
        lock = json.loads(lock_path.read_text())
        lock["dependencies"]["example.registry"]["dependencies"] = {"missing.package": "1.0.0"}
        lock_path.write_text(json.dumps(lock))
        self.copy_file("Packages/packages-lock.json")
        with self.assertRaisesRegex(ValueError, "Lockfile dependency is absent"):
            self.module.main(self.root, self.copy, self.editor)


if __name__ == "__main__":
    unittest.main()
