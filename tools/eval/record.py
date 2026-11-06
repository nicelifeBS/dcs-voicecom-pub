# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy>=2.0", "sounddevice>=0.5", "soundfile>=0.13"]
# ///
"""Record real voice clips from a microphone: one 16 kHz mono WAV per take, named <slug>__NN.wav.

Copied from https://github.com/nicelifeBS/whistleAttack (spikes/whistle/record.py).
Walks commands.csv, shows each phrase, records between two Enter presses.
Clips go to private-clips/ (git-ignored) by default: real voice recordings must not be committed (issue #12).
Score them with tools/eval/evaluate.py.

  uv run tools/eval/record.py                     # every command, 1 take each
  uv run tools/eval/record.py --takes 3 --only gear_up flaps_down
  uv run tools/eval/record.py --list-devices
  uv run tools/eval/record.py --takes 10 --shuffle --device "headset"
"""
import argparse
import csv
import queue
import random
from pathlib import Path

import numpy as np
import sounddevice as sd
import soundfile as sf

SAMPLE_RATE = 16000
ROOT = Path(__file__).resolve().parents[2]


def record_clip(device) -> np.ndarray:
    chunks: queue.Queue = queue.Queue()
    with sd.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32", device=device,
                        callback=lambda data, *_: chunks.put(data.copy())):
        input("    recording... Enter to stop ")
    return np.concatenate(list(chunks.queue) or [np.zeros((0, 1), np.float32)])[:, 0]


def next_take(out: Path, slug: str) -> Path:
    n = 1
    while (out / f"{slug}__{n:02d}.wav").exists():
        n += 1
    return out / f"{slug}__{n:02d}.wav"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--commands", type=Path, default=ROOT / "tests" / "DcsVoiceCom.Core.Tests" / "data" / "clips" / "commands.csv")
    ap.add_argument("--out", type=Path, default=ROOT / "private-clips")
    ap.add_argument("--takes", type=int, default=1)
    ap.add_argument("--only", nargs="*", help="slugs to record (default: all)")
    ap.add_argument("--shuffle", action="store_true", help="prompt all takes of all commands in random order")
    ap.add_argument("--device", help="input device index or name substring (default: system default)")
    ap.add_argument("--list-devices", action="store_true")
    args = ap.parse_args()

    if args.list_devices:
        print(sd.query_devices())
        return
    device = int(args.device) if args.device and args.device.isdigit() else args.device
    args.out.mkdir(parents=True, exist_ok=True)

    with open(args.commands, encoding="utf-8") as f:
        rows = [r for r in csv.DictReader(f) if not args.only or r["slug"] in args.only]
    prompts = [row for row in rows for _ in range(args.takes)]
    if args.shuffle:
        random.shuffle(prompts)  # mixed order, like real use, instead of ten in a row
    for i, row in enumerate(prompts, 1):
        print(f"[{i}/{len(prompts)}] ({row['lang']}) say: \"{row['phrase']}\"")
        reply = input("    Enter to start, s to skip, q to quit ").strip().lower()
        if reply == "q":
            return
        if reply == "s":
            continue
        audio = record_clip(device)
        path = next_take(args.out, row["slug"])
        sf.write(path, audio, SAMPLE_RATE, subtype="PCM_16")
        print(f"    saved {path.name} ({len(audio) / SAMPLE_RATE:.1f} s, peak {np.abs(audio).max(initial=0):.2f})")


if __name__ == "__main__":
    main()
