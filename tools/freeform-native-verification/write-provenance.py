# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Check copied inputs before Unity runs; freeze hashes of the tested copies."""
import hashlib
from pathlib import Path
import sys


FREEFORM_AUTHORED_INPUTS = (
    "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity",
    "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab",
    "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab",
    "Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab",
    "Assets/SpatialBlend/Samples/Prefabs/UI/InteractionModePanel.prefab",
    "Assets/SpatialBlend/Avatar/Prefabs/SbAvatarPrefab.prefab",
)
INJECTED_NATIVE_FIXTURES = (
    "NativeManagerIntegrationTests.cs",
    "NativeFreeformSceneGraphicsTests.cs",
)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main(repo, project, native):
    repo, project, native = Path(repo), Path(project), Path(native)
    paths = sorted((repo / "Assets/SpatialBlend").rglob("*.cs"))
    paths += sorted((repo / "Assets/SpatialBlend").rglob("*.asmdef"))
    paths += sorted((repo / "Assets/SpatialBlend").rglob("*.asmref"))
    paths += sorted((repo / "Assets/SpatialBlend").rglob("*.meta"))
    # These are actual native-gate inputs now, not merely metadata references.
    # Keep the authored scene/prefab bytes in the pre-import manifest as well.
    paths += [repo / relative for relative in FREEFORM_AUTHORED_INPUTS]
    paths += sorted(path for path in (repo / "ProjectSettings").rglob("*") if path.is_file())
    paths += sorted(path for path in (repo / "Assets/Plugins").rglob("*")
                    if path.is_file() and path.suffix in {".meta", ".dll", ".dylib", ".so"}
                    and path != repo / "Assets/Plugins/OSX/libspatialblend.dylib")
    paths += sorted((repo / "Assets/Photon").rglob("*.dll"))
    paths += [repo / "Packages/manifest.json", repo / "Packages/packages-lock.json"]
    rows = []
    for path in paths:
        relative = path.relative_to(repo)
        copied = project / relative
        original_hash = digest(path)
        copied_hash = digest(copied)
        if original_hash != copied_hash:
            raise SystemExit(f"Source changed during verification copy: {relative}")
        rows.append(f"{copied_hash}  {relative.as_posix()}")
    copied_native = project / "Assets/Plugins/OSX/libspatialblend.dylib"
    if digest(native) != digest(copied_native):
        raise SystemExit("Native copy differs from explicitly selected artifact")
    rows.append(f"{digest(copied_native)}  Assets/Plugins/OSX/libspatialblend.dylib")
    for fixture in INJECTED_NATIVE_FIXTURES:
        test = Path("Assets/SpatialBlend/Framework/Test/Restoration") / fixture
        if digest(project / test) != digest(repo / "tools/freeform-native-verification" / fixture):
            raise SystemExit(f"Native fixture copy differs: {fixture}")
        rows.append(f"{digest(project / test)}  {test.as_posix()}")
    package_closure = project / "PACKAGE_CLOSURE_SHA256.json"
    if not package_closure.is_file():
        raise SystemExit("Offline package preflight did not write PACKAGE_CLOSURE_SHA256.json")
    rows.append(f"{digest(package_closure)}  PACKAGE_CLOSURE_SHA256.json")
    (project / "INPUT_SHA256.txt").write_text("\n".join(rows) + "\n")
    print(f"FREEFORM_NATIVE_PROVENANCE_INPUTS={len(rows)}")


if __name__ == "__main__":
    main(*sys.argv[1:])
