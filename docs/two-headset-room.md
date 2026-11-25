# Two headsets in one room

A walkthrough of a Freeform Collaboration session between two Meta Quest headsets, each in its own physical room. It assumes the
project is set up (`python3 tools/setup_sdks.py`) and builds (README, "Build for Quest").

## 1. Build once, install on both

Both headsets must run builds with the **same Photon Fusion App ID and Photon Voice App ID** (`setup_sdks.py --photon-app-id ...
--photon-voice-app-id ...`, `docs/required-credentials.md`): a room is visible only to players of the same Photon application. The
simplest way is one APK installed on both headsets (`Build And Run` on the first, `adb install` or `Build And Run` on the second).

Optional services:

- **Scanning** uses the scan server named by `SpatialBlendSettings.sessionServerAddress` or a device `spatialblend-local.json`
  (`docs/local-session-config.md`). With no server set, the panel says "Scanning is off" and everything else works.
- **TURN** helps on networks that block a direct connection (`docs/webrtc-turn-relay.md`). Without it the core link uses a direct
  WebRTC channel and falls back to Photon's relay.

## 2. Set up each room

On each headset, run the Quest's own Space Setup once for the room it is in (walls, floor and furniture). The app reads that room
through MRUK at start; the furniture becomes the room's obstacles and seats. Rooms do not have to match: SpatialBlend links rooms, not
coordinates.

## 3. Host and join

1. On headset A, start the app. The control panel's lobby shows the session block; press **Find a room**. The room list opens
   ("Looking for rooms…", then "No rooms yet — create one." or the listed rooms).
2. Press **New room** on A. A room named `Room N` (the smallest number not listed) is created and A waits in it.
3. On headset B, start the app, press **Find a room**, and tap A's room in the list.
4. The apps start once the room has the players it needs (`SpatialBlendSettings.playersToStartApps`, 2 in the committed settings): each
   participant sees the other's avatar, the shared Whiteboard and Chess are placed in each room by its own placement rule, and voice
   starts.

To leave, press **Leave room** on the panel; the room disappears when its last player leaves. A desktop spectator can watch a
room as well (`docs/desktop-spectator.md`).

## Photon region

A Photon room is listed only in its own region. By default every device picks its **best region by ping**
(`PhotonAppSettings.FixedRegion` empty), so two headsets in the same building land in the same region. Two devices can still pick
different regions when they are near two regions of similar ping, or far apart; then B does not see A's room.

- To pin one region for every device, set **Fixed Region** in `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset` (for example
  `asia`, `eu`, `jp`, `kr`, `us`; the Photon dashboard lists the codes) before building, and install that build on both headsets.
  The asset is git-ignored and local to your checkout.
- The desktop spectator follows the same rule and can be pointed at a region with `-photon-region CODE` or `SPATIALBLEND_PHOTON_REGION`
  (`SpectatorJoinPolicy.ResolveRegion`).

## When it does not connect

- B's list stays empty: the builds use different Photon App IDs, or the devices are in different regions (pin one, above).
- The room is listed but the apps never start: fewer players than `playersToStartApps` are in the room.
- No voice: the build has no Photon Voice App ID (the control panel says so; `docs/required-credentials.md`).
- Slow or stuttering motion on venue Wi-Fi: the link fell back to Photon's relay; the session panel's carrier line shows it
  (`docs/webrtc-turn-relay.md`, "Path visibility").
