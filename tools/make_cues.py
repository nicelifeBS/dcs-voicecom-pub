"""Generates the default audio cues in assets/cues (original tones, no third-party audio).

Run: python tools/make_cues.py
"""
import math
import struct
import wave
from pathlib import Path

RATE = 22050
OUT = Path(__file__).resolve().parent.parent / "assets" / "cues"


def tone(freq_start, freq_end, ms, volume=0.35):
    n = int(RATE * ms / 1000)
    fade = int(RATE * 0.008)
    samples = []
    phase = 0.0
    for i in range(n):
        freq = freq_start + (freq_end - freq_start) * i / max(1, n - 1)
        phase += 2 * math.pi * freq / RATE
        env = min(1.0, i / fade, (n - 1 - i) / fade) if fade else 1.0
        samples.append(volume * env * math.sin(phase))
    return samples


def silence(ms):
    return [0.0] * int(RATE * ms / 1000)


def write(name, samples):
    OUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUT / name), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 32767)) for s in samples))


if __name__ == "__main__":
    write("accepted.wav", tone(880, 1320, 90))
    write("no_match.wav", tone(440, 440, 80) + silence(60) + tone(440, 440, 80))
    write("refused.wav", tone(330, 180, 260))
    print(f"Wrote cues to {OUT}")
