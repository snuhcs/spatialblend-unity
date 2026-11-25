# Desktop spectator

The desktop spectator is a separate macOS app that joins a Freeform Collaboration (FC) room by name and shows what the room shares
through a free/orbit camera. It is a networked client, not a participant: it publishes no avatar, registers nothing with the core
and cannot change what the participants see. Its code is under `Assets/SpatialBlend/Samples/Scripts/Spectator/`.

## Build

```
tools/build-spectator-mac.sh [--no-turn-secret] [OUT.app]
```

The script builds from a disposable clone with the macOS Unity editor (it runs `SpatialBlend.Editor.SpectatorBuild.BuildMac`) and drops the
Android-only `com.unity.xr.meta-openxr` package from the clone's manifest. The default output is the git-ignored `Builds/Spectator`.

- The build needs the Photon Fusion App ID (`docs/required-credentials.md`); the spectator needs no Voice App ID.
- When `~/.config/spatialblend/turn-client-secret` exists the build embeds the TURN client secret, as the Quest app does. Do not publish
  such an app, or build with `--no-turn-secret`.

## Run and join

```
SpatialBlendSpectator.app/Contents/MacOS/SpatialBlendSpectator -spectate-room "Room 1"
```

Or open the app, type the room name and press **Join as spectator**. The room can also be given with `SPATIALBLEND_SPECTATE_ROOM`.

- The spectator only joins a room that already exists and is listed; it never creates one. A room that is not found within 25 s, or
  that is full or closed, is refused with a message.
- It waits until the room has the players it needs to start its apps (`playersToStartApps`, 2 by default) before joining. The status
  line says "Waiting for the room's participants to start" meanwhile.
- **Leave** ends the visit; the spectator can join the same room again.

From the Freeform app's desktop lobby (Editor or desktop player), **Join as observer** launches the spectator as a separate process. The
app is found through `SPATIALBLEND_SPECTATOR_APP` (its path), else next to the player. The room is passed when the lobby lists exactly
one; otherwise the spectator's window asks for it.

## Photon region

A Photon room is listed only in its own region. The spectator uses `-photon-region CODE`, else `SPATIALBLEND_PHOTON_REGION`, else the
project's `PhotonAppSettings.FixedRegion`, else Photon's best region by ping, as a participant does. The status line shows the region.
If the participants and the spectator may pick different best regions, set the same `FixedRegion` everywhere or pass the
participants' region with `-photon-region` (see [two-headset-room.md](two-headset-room.md#photon-region)).

## What you see

- The content the participants share (GLB and scanned objects, the Whiteboard and Chess) at its live pose.
- **The viewed room in 3-D.** The room of one participant, in that room's own frame: floor, walls as light strips, furniture as boxes
  (shared real objects in violet), the placed objects at their poses there, and the people as simple humanoid proxies in their
  colour: a filled floor disc for the room's owner ("here"), a ring for another participant's avatar as placed in that room. The Meta
  avatars themselves are not shown (the Meta Avatars SDK has no macOS binary).
- **The minimap** (bottom right): a top-down view of the same room, with walls, furniture, placed objects, people (filled circle: the
  owner; ring: an avatar) and a wedge for the camera. Until a room arrives it says "Waiting for the participants' rooms".
- An overlay line with the room, phase, connection, carrier and player and entity counts.

Room snapshots arrive at about 2 Hz, so people move smoothly but not at headset rate; hands are not shown.

## Controls

| Input | Action |
|---|---|
| Right mouse drag | Orbit |
| W A S D | Fly |
| Q / E | Down / up |
| Scroll | Zoom |
| F | Frame the room (done once automatically) |
| Tab | Next participant's room (minimap and 3-D view) |
| M | Hide or show the minimap |

## Options

| Option | Meaning |
|---|---|
| `-spectate-room NAME` / `SPATIALBLEND_SPECTATE_ROOM` | Room to join at start |
| `-photon-region CODE` / `SPATIALBLEND_PHOTON_REGION` | Photon region (see above) |
| `SPATIALBLEND_SPECTATOR_APP` | Path of the spectator app, for the lobby's Join as observer |

Transports (Photon reliable data, WebRTC, WebRTC with TURN) are FC's own, from `Assets/Resources/SpatialBlendSettings.asset`.

## Limits

- A spectator occupies one of the room's player slots.
- Scene geometry is drawn from the participants' room snapshots (MRUK anchors, at most 48 furniture items), not from the room's exact
  floor polygon.
- After a leave and a re-join, what the room delivers again to the spectator depends on the room, as for any client that re-joins.

## Tests

- EditMode (`Category("FreeformRestoration")`): `FreeformSpectatorTests`, `FreeformSpectatorSceneIdentityTests` and
  `FreeformSpectatorSessionTests`.
- Two processes over the Photon Cloud: `tools/run-spectator-two-process-test.sh Photon|WebRtc|WebRtcTurn` (needs
  `SPATIALBLEND_PHOTON_CLOUD=1`).
