# Desktop observer mode

When the Freeform Collaboration app runs on a desktop (a Windows, macOS or Linux player, or the Editor) and no headset is active,
joining a room makes that process an **observer**: it watches the participants' rooms without taking part. A Quest build, or any run
with an active headset or XR device, is always a participant. This mode is the Freeform app itself; the separate macOS spectator app
is described in [desktop-spectator.md](desktop-spectator.md).

## Choosing the mode

| Option | Effect |
|---|---|
| (none) | A desktop without an active headset joins as an observer |
| `-observer` / `SPATIALBLEND_FORCE_OBSERVER=1` | Observer on a desktop (an active headset still wins) |
| `-participant` / `SPATIALBLEND_FORCE_PARTICIPANT=1` | Participant on a desktop (as the batchmode test harnesses do) |

## Joining

Use the lobby as a participant would and pick a listed room. An observer:

- joins only rooms that already exist (New room is disabled: "Observers can only watch existing rooms");
- needs no room setup, and shares no room, avatar, furniture or scan;
- does not grab, draw or place anything;
- can join before or after the participants' apps have started; it does not count toward the players a room needs to start, and its
  leave does not disturb the participants.

An observer still occupies one of the room's player slots.

## Whose room is on screen

Each participant sends observers a snapshot of their room about twice a second: the furniture (as boxes of the measured size), the
placed objects, their own headset and every avatar as placed in that room. The panel lists one row per participant (`Participant
{player id}`); pick a row to show that room. A room that has not sent anything for 3 seconds is dropped from the list.

## Cameras

The panel switches two modes.

1. **Free fly.** W A S D move along the look direction, the right mouse button looks around, Q/E or Ctrl/Space move down and up, and
   Shift moves three times faster. Keys are ignored while a text field is focused or the pointer is over the panel.
2. **Follow head.** The camera follows the chosen person's head in the selected room: the room's owner (their headset) or another
   participant's avatar as placed in that room. Switching back to free fly starts from that pose.

The field of view is 72 degrees.

## Tests

`DesktopObserverTests` (EditMode, category `FreeformRestoration`).
