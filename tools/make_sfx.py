#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Synthesize the app's sound effects (Assets/SpatialBlend/Samples/Resources/SFX/*.wav).

Our own work (CC0 1.0): soft sine chimes and pulses made from the parameters below, no samples, no third-party sound.
Deterministic: running it again reproduces the same bytes (tools/test_sfx.py checks the committed files against it).

    python3 tools/make_sfx.py OUT_DIR
"""

import math
import struct
import sys
import wave
from pathlib import Path

RATE = 44100
PEAK = 0.45  # of full scale


def note(freq, start, length, attack=0.01, decay=None, partials=((1.0, 1.0), (2.0, 0.25), (3.0, 0.08))):
    """A bell-like tone: partials (ratio, gain), a short attack and an exponential decay."""
    decay = decay if decay is not None else length / 4.0
    return ("note", freq, start, length, attack, decay, partials)


SOUNDS = {
    # name: (seconds, [notes], loop)
    "ScanTrigger": (1.4, [note(660, 0.0, 0.35), note(990, 0.12, 0.9, decay=0.3)], False),
    "ScanInProgress": (7.2, [note(440 if i % 2 == 0 else 554.37, i * 0.9, 0.9, attack=0.2, decay=0.35,
                                  partials=((1.0, 1.0), (2.0, 0.1))) for i in range(8)], True),
    "ScanComplete": (3.0, [note(523.25, 0.0, 1.2), note(659.25, 0.15, 1.2), note(783.99, 0.3, 2.6, decay=0.8)], False),
    "RemoteAvatarArrived": (4.5, [note(392.0, 0.0, 1.5, decay=0.6), note(523.25, 0.35, 1.5, decay=0.6),
                                  note(659.25, 0.7, 3.6, decay=1.1)], False),
    "RemoteObjectArrived": (2.2, [note(587.33, 0.0, 0.9, decay=0.3), note(880.0, 0.2, 1.9, decay=0.55)], False),
    "AnnotationCreated": (1.8, [note(698.46, 0.0, 0.6, decay=0.2), note(1046.5, 0.1, 1.6, decay=0.45)], False),
    "AnnotationPointPlaced": (0.9, [note(1174.66, 0.0, 0.85, decay=0.15)], False),
    "ObjectPlaced": (0.8, [note(329.63, 0.0, 0.75, decay=0.12, partials=((1.0, 1.0), (2.0, 0.4), (4.0, 0.1)))], False),
}


def render(seconds, notes, loop):
    count = int(round(seconds * RATE))
    samples = [0.0] * count
    for _kind, freq, start, length, attack, decay, partials in notes:
        first = int(start * RATE)
        for i in range(int(length * RATE)):
            n = first + i
            if loop:
                n %= count
            elif n >= count:
                break
            t = i / RATE
            envelope = min(1.0, t / attack) * math.exp(-t / decay)
            value = sum(gain * math.sin(2 * math.pi * freq * ratio * t) for ratio, gain in partials)
            samples[n] += envelope * value
    peak = max(1e-9, max(abs(s) for s in samples))
    scale = PEAK / peak
    if not loop:
        # A 10 ms fade out, so the clip ends at silence.
        fade = int(0.01 * RATE)
        for i in range(fade):
            samples[count - 1 - i] *= i / fade
    return [int(round(s * scale * 32767)) for s in samples]


def write(path: Path, pcm):
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(b"".join(struct.pack("<h", max(-32768, min(32767, v))) for v in pcm))


def main(argv):
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[-1].strip(), file=sys.stderr)
        return 2
    out = Path(argv[1])
    out.mkdir(parents=True, exist_ok=True)
    for name, (seconds, notes, loop) in SOUNDS.items():
        write(out / f"{name}.wav", render(seconds, notes, loop))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
