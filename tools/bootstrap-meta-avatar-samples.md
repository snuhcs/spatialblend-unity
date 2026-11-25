# Offline Meta Avatar sample bootstrap

This bootstraps the official cached Meta Avatars `35.2.0` sample **scripts only**:

```sh
python3 tools/bootstrap-meta-avatar-samples.py --apply \
  --provenance "$(pwd -P)/../meta-avatar-sample-scripts-35.2.0.json"
```

It requires the locally cached package fingerprint `99a67d4aa1f5fd0259dde4e56e3f984347fe1862`; it never downloads, changes `Packages/`, or writes `Library/PackageCache`. It copies `Samples~/Scripts` (about 0.8 MiB, including the official `Oculus.AvatarSDK2.Samples.asmdef`, all sources, and the root/contained `.meta` GUID files) to Unity's conventional sample location:

`Assets/Samples/Meta Avatars SDK/35.2.0/Sample Scenes/Scripts`

The entire official scripts directory is the smallest honest source closure for `OvrAvatarAnimationBehavior`: it includes its base class and `MecanimLegsAnimationController`, rather than a replacement type or edited assembly definition. Existing destination files must be byte-identical; conflicting user files cause a failure before any sample file is copied. The bootstrap rejects any destination or provenance path containing a symlink (including dangling links) before writing, and a second run is idempotent.

Run its filesystem-only checks without Unity:

```sh
python3 tools/test_bootstrap_meta_avatar_samples.py
```

Runtime boundary: this fixes the missing compile-time `Oculus.AvatarSDK2.Samples` assembly. The official animation behavior (on `SbAvatarPrefab`, and on the local avatar while it interacts) then loads `Resources/RTR00003` and the behavior zips under `Application.streamingAssetsPath/BehaviorAssets`. Neither is committed: the Meta Avatars editor (`PackageAssetsPostProcessor`) packages the zips into `Assets/StreamingAssets/BehaviorAssets` from the installed core assets, and `tools/setup_sdks.py` installs the legs rig (the asset closure of `Samples~/Scenes/LegsNetworkLoopback/Resources/RTR00003.prefab`: the prefab, its Mecanim controller and clips, about 63 MB, package GUIDs) into the git-ignored `Assets/SpatialBlend/Avatar/LegsRig` with `tools/vendor-meta-avatar-legs-rig.py`. Do not also import the LegsNetworkLoopback scene sample into `Assets/Samples`: its files carry the same GUIDs and a second `RTR00003` resource. Without the rig, the behaviour throws "The Object you want to instantiate is null" in `OnUserAvatarLoaded` and received avatars walk with frozen legs.
