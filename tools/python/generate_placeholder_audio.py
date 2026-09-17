#!/usr/bin/env python3
"""Generate deterministic placeholder audio clips for Domain 9 (audio bus +
pipeline) proof-of-stream-loading. Stdlib `wave` only, no randomness — every
re-run produces byte-identical output so the committed .wav files are a
reproducible build artifact, not a one-off asset.

Produces the original Domain 9 proof clips plus the Task 1.5 slice content
pack. Every clip is a short, deterministic procedural placeholder tone/noise
until the final mix is available:
  data/audio/sfx/*.wav       gameplay SFX and meta cues
  data/audio/ui/*.wav        panel/vitals UI cues
  data/audio/music/*.wav     exploration/tension/critical layers

Both are pure sine-wave synthesis with a linear fade-in/fade-out envelope so
the loop point (music clip) and the transient (sfx clip) do not click.
"""
from __future__ import annotations

import math
import os
import struct
import wave

SAMPLE_RATE = 22050
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _envelope(i: int, n: int, fade_samples: int) -> float:
    """Linear fade-in/fade-out envelope, 1.0 in the steady region."""
    if n <= 0:
        return 0.0
    if i < fade_samples:
        return i / float(fade_samples)
    if i >= n - fade_samples:
        return (n - 1 - i) / float(fade_samples)
    return 1.0


def _write_sine_wav(
    path: str,
    duration_s: float,
    frequencies: list[float],
    amplitude: float,
    fade_s: float,
    noise_level: float = 0.0,
    noise_seed: int = 0,
) -> None:
    n = int(SAMPLE_RATE * duration_s)
    fade_samples = max(1, int(SAMPLE_RATE * fade_s))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with wave.open(path, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)  # 16-bit
        wf.setframerate(SAMPLE_RATE)
        frames = bytearray()
        for i in range(n):
            t = i / float(SAMPLE_RATE)
            sample = 0.0
            for freq in frequencies:
                sample += math.sin(2.0 * math.pi * freq * t)
            sample /= float(len(frequencies))
            if noise_level > 0.0:
                # Deterministic integer hash: reproducible hiss/click texture
                # without importing random or depending on process state.
                noise_value = ((i * 1103515245 + noise_seed) >> 16) & 0x7FFF
                sample += ((noise_value / 16383.5) - 1.0) * noise_level
            sample *= amplitude * _envelope(i, n, fade_samples)
            clamped = max(-1.0, min(1.0, sample))
            frames += struct.pack("<h", int(clamped * 32767.0))
        wf.writeframes(bytes(frames))


def main() -> int:
    # SFX: a short two-tone "pickup" chirp (ascending interval), 0.25s.
    sfx_path = os.path.join(ROOT, "data", "audio", "sfx", "tool_pickup.wav")
    _write_sine_wav(sfx_path, duration_s=0.25, frequencies=[880.0, 1320.0], amplitude=0.6, fade_s=0.02)
    print(f"wrote {sfx_path}")

    # Music base layer: a low sustained drone, 1.5s, loop-friendly (full-cycle
    # fade-in/out at the same envelope on both ends so LOOP_FORWARD does not click).
    music_path = os.path.join(ROOT, "data", "audio", "music", "exploration_base.wav")
    _write_sine_wav(music_path, duration_s=1.5, frequencies=[110.0, 220.0], amplitude=0.4, fade_s=0.05)
    print(f"wrote {music_path}")

    # Task 1.5 slice pack. These are intentionally small, distinct procedural
    # placeholder clips: project-original and non-silent, but not final mix.
    # Keep the table in one place so the content pack is reproducible and easy
    # to replace with authored clips without changing the AudioManager catalog.
    clips = [
        ("sfx/footstep.wav", 0.12, [125.0, 250.0], 0.55, 0.015, 0.18, 101),
        ("ui/panel_open.wav", 0.10, [520.0, 780.0], 0.45, 0.012, 0.01, 103),
        ("ui/panel_close.wav", 0.10, [640.0, 960.0], 0.45, 0.012, 0.01, 107),
        ("sfx/fire_crackle.wav", 0.24, [180.0, 360.0], 0.42, 0.025, 0.30, 109),
        ("sfx/breach_alarm.wav", 0.42, [330.0, 660.0], 0.48, 0.035, 0.08, 113),
        ("sfx/combat_hit.wav", 0.11, [95.0, 190.0], 0.62, 0.012, 0.22, 127),
        ("sfx/threat_alert.wav", 0.32, [440.0, 880.0], 0.48, 0.025, 0.03, 131),
        ("sfx/door_open.wav", 0.18, [220.0, 440.0], 0.45, 0.020, 0.10, 137),
        ("sfx/door_close.wav", 0.18, [440.0, 220.0], 0.45, 0.020, 0.10, 139),
        ("sfx/dock_land.wav", 0.30, [72.0, 144.0], 0.55, 0.030, 0.15, 149),
        ("sfx/vitals_low.wav", 0.28, [210.0, 315.0], 0.45, 0.025, 0.04, 151),
        ("music/tension_drone.wav", 1.50, [146.83, 220.0], 0.34, 0.050, 0.015, 157),
        ("music/critical_pad.wav", 1.50, [82.41, 123.47], 0.34, 0.050, 0.015, 163),
    ]
    for relative_path, duration_s, frequencies, amplitude, fade_s, noise_level, seed in clips:
        path = os.path.join(ROOT, "data", "audio", relative_path)
        _write_sine_wav(
            path,
            duration_s=duration_s,
            frequencies=frequencies,
            amplitude=amplitude,
            fade_s=fade_s,
            noise_level=noise_level,
            noise_seed=seed,
        )
        print(f"wrote {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
