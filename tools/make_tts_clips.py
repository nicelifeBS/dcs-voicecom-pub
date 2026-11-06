"""Synthetic voice-line pipeline: commands.csv (+ optional negatives.csv) -> test clips, with no real voice.

For every row (slug,lang,phrase) it writes <slug>__NN.wav, 16 kHz mono 16-bit, NN = take number.
Each take varies the voice, speaking speed, band-pass edges, level and noise, so a set of several takes
tests more than one voice. The output layout is the same as the recorded sets in
https://github.com/nicelifeBS/whistleAttack (eval/*/recordings), so its eval/evaluate.py can score them.

English uses Kokoro (Apache-2.0). Kokoro has no German voice, so German uses Piper (de_DE-thorsten).
Take 1 is always the baseline voice at normal speed; takes 2+ rotate through the voice pool.
A clip's settings (voice, speed, filter, noise level) depend only on --seed, slug and take, so adding
commands or takes never changes the others. The waveform is not bit-identical between runs, because the TTS
models add their own random noise. tts_manifest.csv in the output folder records what each clip was made with.

Setup (Kokoro needs Python < 3.13 and espeak-ng):
    sudo apt-get install espeak-ng
    uv venv --python 3.12 .venv-tts
    uv pip install --python .venv-tts/bin/python kokoro piper-tts soundfile numpy scipy \
        en_core_web_sm@https://github.com/explosion/spacy-models/releases/download/en_core_web_sm-3.8.0/en_core_web_sm-3.8.0-py3-none-any.whl
Run:
    .venv-tts/bin/python tools/make_tts_clips.py                      # the committed test clips, 1 take each
    .venv-tts/bin/python tools/make_tts_clips.py --takes 6 --out /tmp/synth/recordings
    .venv-tts/bin/python tools/make_tts_clips.py --commands set/commands.csv --negatives set/negatives.csv \
        --out set/recordings --takes 10 --only gear_up flaps_down
"""
import argparse
import csv
import urllib.request
import wave
import zlib
from pathlib import Path

import numpy as np
from scipy import signal

RATE = 16000
ROOT = Path(__file__).resolve().parent.parent
CLIPS = ROOT / "tests" / "DcsVoiceCom.Core.Tests" / "data" / "clips"
CACHE = Path.home() / ".cache" / "dcs-voicecom-tts"
PIPER_URL = "https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/medium/de_DE-thorsten-medium"
# First entry is the baseline (take 1). Mostly male US English, plus British and two female voices.
KOKORO_VOICES = ["am_michael", "am_adam", "bm_george", "am_eric", "af_heart", "am_liam", "bm_lewis", "am_onyx",
                 "bm_daniel", "af_bella"]


def rng_for(seed, slug, take):
    return np.random.default_rng(zlib.crc32(f"{seed}:{slug}:{take}".encode()))


def pick_params(take, rng):
    """Voice and speed for one take; take 1 is the plain baseline."""
    if take == 1:
        return {"voice": KOKORO_VOICES[0], "speed": 1.0}
    return {"voice": KOKORO_VOICES[(take - 1) % len(KOKORO_VOICES)], "speed": round(float(rng.uniform(0.88, 1.15)), 2)}


class Kokoro:
    def __init__(self):
        from kokoro import KPipeline
        self.pipe = KPipeline(lang_code="a", repo_id="hexgrad/Kokoro-82M")

    def __call__(self, text, params):
        parts = [np.asarray(a) for _, _, a in self.pipe(text, voice=params["voice"], speed=params["speed"])]
        return np.concatenate(parts), 24000


class Piper:
    def __init__(self):
        from piper import PiperVoice
        CACHE.mkdir(parents=True, exist_ok=True)
        for ext in (".onnx", ".onnx.json"):
            f = CACHE / ("de_DE-thorsten-medium" + ext)
            if not f.exists():
                urllib.request.urlretrieve(PIPER_URL + ext, f)
        self.voice = PiperVoice.load(str(CACHE / "de_DE-thorsten-medium.onnx"))

    def __call__(self, text, params):
        from piper import SynthesisConfig
        # length_scale > 1 is slower, so invert the shared speed value
        cfg = SynthesisConfig(length_scale=1 / params["speed"], noise_scale=params["noise_scale"])
        chunks = [np.frombuffer(c.audio_int16_bytes, dtype=np.int16) for c in self.voice.synthesize(text, cfg)]
        return np.concatenate(chunks).astype(np.float32) / 32768, self.voice.config.sample_rate


ENGINES = {"en": Kokoro, "de": Piper}


def degrade(audio, src_rate, rng, take):
    """Resample to 16 kHz, band-pass like a radio/headset mic, pad with silence, add light noise."""
    audio = signal.resample_poly(audio, RATE, src_rate)
    low, high = (300, 3400) if take == 1 else (rng.uniform(200, 450), rng.uniform(2800, 3900))
    sos = signal.butter(4, [low, high], btype="bandpass", fs=RATE, output="sos")
    audio = signal.sosfilt(sos, audio)
    peak = 0.7 if take == 1 else rng.uniform(0.4, 0.9)
    audio = audio / max(1e-6, np.abs(audio).max()) * peak
    pad = lambda s: np.zeros(int(RATE * s))
    audio = np.concatenate([pad(rng.uniform(0.3, 0.5)), audio, pad(rng.uniform(0.3, 0.5))])
    snr = 31.0 if take == 1 else rng.uniform(22, 40)  # dB below the speech peak
    noise = signal.sosfilt(sos, rng.normal(0, 1, len(audio)))
    noise = noise / noise.std() * peak * 10 ** (-snr / 20)
    return np.clip(audio + noise, -1, 1), {"band_hz": f"{low:.0f}-{high:.0f}", "peak": round(float(peak), 2),
                                           "snr_db": round(float(snr), 1)}


def read_rows(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--commands", type=Path, default=CLIPS / "commands.csv", help="slug,lang,phrase")
    ap.add_argument("--negatives", type=Path, help="same format; phrases that are not commands")
    ap.add_argument("--out", type=Path, default=CLIPS)
    ap.add_argument("--takes", type=int, default=1, help="takes per phrase (default 1)")
    ap.add_argument("--only", nargs="*", help="slugs to generate (default: all)")
    ap.add_argument("--seed", type=int, default=12)
    args = ap.parse_args()

    rows = read_rows(args.commands) + (read_rows(args.negatives) if args.negatives else [])
    rows = [r for r in rows if not args.only or r["slug"] in args.only]
    unknown = {r["lang"] for r in rows} - ENGINES.keys()
    if unknown:
        raise SystemExit(f"no TTS voice for language(s): {', '.join(sorted(unknown))} (have: {', '.join(ENGINES)})")
    args.out.mkdir(parents=True, exist_ok=True)

    manifest = []
    for lang, engine_cls in ENGINES.items():
        sel = [r for r in rows if r["lang"] == lang]
        if not sel:
            continue
        engine = engine_cls()
        for r in sel:
            for take in range(1, args.takes + 1):
                rng = rng_for(args.seed, r["slug"], take)
                params = pick_params(take, rng)
                if lang == "de":
                    params = {"voice": "thorsten", "speed": params["speed"],
                              "noise_scale": 0.667 if take == 1 else round(float(rng.uniform(0.5, 0.9)), 2)}
                audio, rate = engine(r["phrase"], params)
                audio, fx = degrade(audio, rate, rng, take)
                name = f"{r['slug']}__{take:02d}.wav"
                with wave.open(str(args.out / name), "wb") as w:
                    w.setnchannels(1)
                    w.setsampwidth(2)
                    w.setframerate(RATE)
                    w.writeframes((audio * 32767).astype("<i2").tobytes())
                manifest.append({"file": name, "lang": lang, "phrase": r["phrase"], "voice": params["voice"],
                                 "speed": params["speed"], **fx, "seconds": round(len(audio) / RATE, 2)})
                print(name, params["voice"], f"{len(audio) / RATE:.2f}s")

    with open(args.out / "tts_manifest.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(manifest[0]))
        w.writeheader()
        w.writerows(manifest)


if __name__ == "__main__":
    main()
