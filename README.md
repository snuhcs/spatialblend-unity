# SpatialBlend (Unity)

The Unity project of [SpatialBlend](https://github.com/snuhcs/spatialblend) (UIST '26): the SpatialBlend Unity plugin
(`Assets/SpatialBlend`) and *Freeform Collaboration*, a Meta Quest 3 app in which people in different rooms walk, talk and
interact with shared virtual and physical objects while each remote avatar is adapted to the local room. It runs on the
native core [snuhcs/spatialblend-core](https://github.com/snuhcs/spatialblend-core) and scans objects with
[snuhcs/spatialblend-reconstruction](https://github.com/snuhcs/spatialblend-reconstruction); the setup script downloads both
prebuilt libraries.

[Project page](https://github.com/snuhcs/spatialblend) · [Paper](https://doi.org/10.1145/3830398.3830690) ·
[Video](https://www.youtube.com/watch?v=3TWBkZOWY_4)

No prebuilt app is published: you build it with your own Photon App IDs.

## Requirements

- **Unity 6000.0.56f1** with Android Build Support (OpenJDK, Android SDK and NDK)
- **Meta Quest 3**; macOS (Apple silicon) or Windows for the Editor
- **Python 3.10+**
- A free **Photon** account. Photon may not be redistributed, so you download its SDKs yourself (below).

## Setup

1. **Photon App IDs.** At [dashboard.photonengine.com](https://dashboard.photonengine.com/) create a **Fusion 2**
   application and a **Voice** application and copy their App IDs.
2. **Photon SDKs.** In the Unity Asset Store add **Photon Fusion** (2.0.9 Stable 1566) and **Photon Voice 2** (2.63)
   to your account (both free), then in Unity `Window > Package Manager > My Assets` **Download** both. Do not import
   them.
3. **Open the project once** in Unity so the packages resolve (compile errors are expected until step 4), then close it.
4. **Run the setup script** from the repository root:

   ```bash
   python3 tools/setup_sdks.py --photon-app-id <Fusion App ID> --photon-voice-app-id <Voice App ID>
   ```

   It installs Photon from your download, the Meta Avatars samples and TextMesh Pro resources, the native libraries,
   and writes your App IDs to a git-ignored settings file. It is safe to run again; `--check` verifies without changing
   anything. See [docs/required-credentials.md](docs/required-credentials.md) for the IDs and how else to supply them.

## Build for Quest

Open `Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity`, then `File > Build Profiles` > Android >
`Build And Run` (IL2CPP, ARM64). A build without the Photon App IDs is refused with a message saying how to add them.
Every headset in a session must use the same App IDs.

## Optional

- **Object scanning** needs your own scan server
  ([snuhcs/spatialblend-reconstruction](https://github.com/snuhcs/spatialblend-reconstruction)). None is configured, so
  scanning is off until you set its address ([docs/local-session-config.md](docs/local-session-config.md)); the
  tutorial's scan task runs without one.
- **TURN relay** for networks that block direct WebRTC: off by default
  ([docs/webrtc-turn-relay.md](docs/webrtc-turn-relay.md)).

## Documentation

- [docs/two-headset-room.md](docs/two-headset-room.md): a session between two headsets in two rooms
- [docs/desktop-observer.md](docs/desktop-observer.md) and [docs/desktop-spectator.md](docs/desktop-spectator.md):
  watching a session from a desktop
- [docs/freeform-local-desktop.md](docs/freeform-local-desktop.md): an in-process desktop demo without Photon or a
  headset
- [CONTRIBUTING.md](CONTRIBUTING.md): tests (`python3 -m pytest tools -q` and the Unity Test Runner) and conventions

## License

MIT, Copyright (c) 2026 Seoul National University ([LICENSE](LICENSE)). Files under other terms are listed in
[NOTICE](NOTICE); SDKs you download yourself keep their own licences ([THIRD_PARTY.md](THIRD_PARTY.md), `SDKS.lock`).

## Citation

Please cite the SpatialBlend paper: see [CITATION.cff](CITATION.cff) or the
[project page](https://github.com/snuhcs/spatialblend#citation).

## Contact

Jingyu Lee (<jingyu.lee@hcs.snu.ac.kr>), Human-Centered Computer Systems Lab, Seoul National University.

## Contributors

- Jingyu Lee (Seoul National University)
- Dongho Han (Seoul National University)
- Jinmyeong Kim (Seoul National University)
