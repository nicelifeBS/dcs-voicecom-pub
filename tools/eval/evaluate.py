# /// script
# requires-python = ">=3.10"
# dependencies = ["cactus-needle>=3.2.8", "rapidfuzz>=3.14"]
# ///
r"""Score Whistle + the matcher on a recorded set, tuning on some takes and testing on the rest.

Copied from https://github.com/nicelifeBS/whistleAttack (eval/evaluate.py); matching.py is a copy of its matcher.
Use it to check that generated clips (tools/make_tts_clips.py) are understood before they become test data.

A set is a folder with commands.csv (slug,lang,phrase) and <slug>__NN.wav, either in the folder itself (as in
tests/DcsVoiceCom.Core.Tests/data/clips) or in a recordings/ subfolder (as in whistleAttack). An optional
negatives.csv in the same format lists things said that are *not* commands: any command they
trigger counts as wrong.
Odd-numbered takes tune threshold and margin (most right answers with zero wrong ones);
even-numbered takes are the held-out test the tuned values are reported on.

  uv run tools/eval/evaluate.py tests/DcsVoiceCom.Core.Tests/data/clips
  uv run tools/eval/evaluate.py <set> --keywords <set>/keywords.txt --mappings <set>/word_mappings.txt
  uv run tools/eval/evaluate.py <set> --distractors <other>/commands.csv   # a bigger profile to confuse

Transcripts are cached in <set>/transcripts.csv per keyword list; --retranscribe redoes them.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import itertools
import os
import sys
from dataclasses import dataclass
from pathlib import Path

os.environ.setdefault("NEEDLE_TELEMETRY", "0")
os.environ.setdefault("DO_NOT_TRACK", "1")
# Use the model and engine bundled in this repo (model/cactus-needle) instead of downloading them.
_MODEL = Path(__file__).resolve().parents[2] / "model" / "cactus-needle"
if (_MODEL / "whistle" / "2.0.0" / "whistle.cact").exists():
    os.environ.setdefault("NEEDLE_WHISTLE_WEIGHTS", str(_MODEL / "whistle" / "2.0.0" / "whistle.cact"))
if sys.platform.startswith("linux") and (_MODEL / "v3" / "3.3.3" / "libneedle.so").exists():
    os.environ.setdefault("NEEDLE3_LIB_PATH", str(_MODEL / "v3" / "3.3.3" / "libneedle.so"))
elif sys.platform == "win32" and (_MODEL / "v3" / "3.3.3" / "libneedle.dll").exists():
    os.environ.setdefault("NEEDLE3_LIB_PATH", str(_MODEL / "v3" / "3.3.3" / "libneedle.dll"))

sys.path.insert(0, str(Path(__file__).resolve().parent))
from matching import (DEFAULT_MARGIN, DEFAULT_THRESHOLD, Matcher, read_word_mappings,  # noqa: E402
                      strip_edges)

WEIGHTS_REPO, WEIGHTS_FILE = "Cactus-Compute/whistle", "whistle.cact"


def local_weights() -> str | None:
    """The Whistle weights already on disk, or None to let needle download them once.

    An explicit path stops needle contacting Hugging Face on every load. NEEDLE_WHISTLE_WEIGHTS overrides.
    """
    if os.environ.get("NEEDLE_WHISTLE_WEIGHTS"):
        return os.environ["NEEDLE_WHISTLE_WEIGHTS"]
    from huggingface_hub import try_to_load_from_cache

    cached = try_to_load_from_cache(WEIGHTS_REPO, WEIGHTS_FILE)
    return cached if isinstance(cached, str) and os.path.exists(cached) else None


def read_keywords(path: Path | None) -> list[str]:
    if path is None or not path.exists():
        return []
    lines = (line.strip() for line in path.read_text(encoding="utf-8").splitlines())
    return [line for line in lines if line and not line.startswith("#")]

THRESHOLDS = range(50, 95, 5)
MARGINS = (0, 5, 10, 15, 20)


@dataclass
class Clip:
    path: Path
    slug: str
    take: int
    lang: str
    expected: str | None  # None: a negative, must not trigger any command


def load_set(folder: Path) -> tuple[dict[str, dict], list[Clip]]:
    with open(folder / "commands.csv", encoding="utf-8") as f:
        commands = {row["slug"]: row for row in csv.DictReader(f)}
    negatives = {}
    if (folder / "negatives.csv").exists():
        with open(folder / "negatives.csv", encoding="utf-8") as f:
            negatives = {row["slug"]: row for row in csv.DictReader(f)}
    clips = []
    recordings = folder / "recordings" if (folder / "recordings").is_dir() else folder
    for path in sorted(recordings.glob("*.wav")):
        slug, _, take = path.stem.rpartition("__")
        if not take.isdigit():
            continue
        if slug in commands:
            clips.append(Clip(path, slug, int(take), commands[slug]["lang"], commands[slug]["phrase"]))
        elif slug in negatives:
            clips.append(Clip(path, slug, int(take), negatives[slug]["lang"], None))
    return commands, clips


def transcribe_all(folder: Path, clips: list[Clip], keywords: list[str], retranscribe: bool) -> dict[str, str]:
    """{clip file name: raw transcript} for this keyword list, from the cache where possible."""
    cache_file = folder / "transcripts.csv"
    label = hashlib.sha1("\n".join(keywords).encode()).hexdigest()[:8] if keywords else "none"
    cache: dict[tuple[str, str], str] = {}
    if cache_file.exists() and not retranscribe:
        with open(cache_file, encoding="utf-8") as f:
            cache = {(r["keywords"], r["clip"]): r["raw"] for r in csv.DictReader(f)}
    todo = [c for c in clips if (label, c.path.name) not in cache]
    if todo:
        from needle import Whistle

        whistle = Whistle(local_weights())
        print(f"transcribing {len(todo)} clips (keywords: {label}) ...", file=sys.stderr)
        for clip in todo:
            result = whistle.transcribe(clip.path, language=clip.lang, keywords=keywords or None)
            cache[(label, clip.path.name)] = result["text"].strip()
        with open(cache_file, "w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f)
            writer.writerow(["keywords", "clip", "raw"])
            writer.writerows([k, c, raw] for (k, c), raw in sorted(cache.items()))
    return {c.path.name: cache[(label, c.path.name)] for c in clips}


def score(clips: list[Clip], heard: dict[str, str], matcher: Matcher) -> tuple[int, int, int, list[tuple]]:
    """(right, wrong, miss, details). A negative that stays silent counts as right."""
    right = wrong = 0
    details = []
    for clip in clips:
        match = matcher.match(heard[clip.path.name])
        if match.command == clip.expected:
            right += 1
        elif match.command is not None:
            wrong += 1
        details.append((clip, heard[clip.path.name], match))
    return right, wrong, len(clips) - right - wrong, details


def verbatim(clips: list[Clip], heard: dict[str, str], commands: set[str]) -> tuple[int, int]:
    """(right, wrong) when the transcript is used as the command name, as in Phase 4."""
    right = wrong = 0
    for clip in clips:
        said = strip_edges(heard[clip.path.name]).lower()
        if said == (clip.expected or "").lower() or (clip.expected is None and said not in commands):
            right += 1
        elif said in commands:
            wrong += 1
    return right, wrong


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("set", type=Path, help="folder with commands.csv and the clips (or a recordings/ subfolder)")
    ap.add_argument("--keywords", type=Path, help="keyword bias list for Whistle (default: none)")
    ap.add_argument("--mappings", type=Path, help="word mappings file (default: none)")
    ap.add_argument("--distractors", type=Path, action="append", default=[],
                    help="another commands.csv whose phrases join the command list (wrong-command bait)")
    ap.add_argument("--retranscribe", action="store_true")
    ap.add_argument("-v", "--verbose", action="store_true", help="list every test clip")
    args = ap.parse_args()

    commands, clips = load_set(args.set)
    if not clips:
        raise SystemExit(f"no <slug>__NN.wav clips in {args.set} (or its recordings/) for {args.set / 'commands.csv'}")
    keywords = read_keywords(args.keywords)
    mappings = read_word_mappings(args.mappings)
    targets = {row["phrase"].lower(): row["phrase"] for row in commands.values()}
    for extra in args.distractors:
        with open(extra, encoding="utf-8") as f:
            targets.update({row["phrase"].lower(): row["phrase"] for row in csv.DictReader(f)})
    heard = transcribe_all(args.set, clips, keywords, args.retranscribe)

    tune = [c for c in clips if c.take % 2 == 1]
    test = [c for c in clips if c.take % 2 == 0] or tune
    print(f"{len(clips)} clips ({len(tune)} tune / {len(test)} test), {len(targets)} commands, "
          f"{len(keywords)} keywords, {len(mappings)} mappings")

    def matcher(threshold, margin):
        return Matcher(targets, mappings, threshold=threshold, margin=margin)

    # Tune: most right with no wrong; ties go to the stricter setting.
    best = None
    for threshold, margin in itertools.product(THRESHOLDS, MARGINS):
        right, wrong, _, _ = score(tune, heard, matcher(threshold, margin))
        if wrong == 0 and (best is None or right >= best[0]):  # later in the sweep = stricter
            best = (right, threshold, margin)
    if best is None:
        print("no threshold/margin gives zero wrong commands on the tune takes")
        best = (0, max(THRESHOLDS), max(MARGINS))
    _, threshold, margin = best

    vb_right, vb_wrong = verbatim(test, heard, set(targets))
    rows = [("verbatim (Phase 4)", vb_right, vb_wrong, len(test) - vb_right - vb_wrong),
            (f"defaults ({DEFAULT_THRESHOLD:g}/{DEFAULT_MARGIN:g})", *score(test, heard, matcher(DEFAULT_THRESHOLD, DEFAULT_MARGIN))[:3]),
            (f"tuned ({threshold}/{margin})", *score(test, heard, matcher(threshold, margin))[:3])]
    print(f"\nheld-out takes ({len(test)} clips)        right  wrong  miss")
    for name, right, wrong, miss in rows:
        print(f"  {name:32} {right:5}  {wrong:5}  {miss:4}   {right / len(test):.0%}")

    _, _, _, details = score(test, heard, matcher(threshold, margin))
    print("\nper command (tuned, held-out):")
    for slug, row in commands.items():
        mine = [d for d in details if d[0].slug == slug]
        if mine:
            ok = sum(m.command == c.expected for c, _, m in mine)
            print(f"  {row['phrase']:14} {ok}/{len(mine)}")
    negs = [d for d in details if d[0].expected is None]
    if negs:
        print(f"  {'(negatives)':14} {sum(m.command is None for _, _, m in negs)}/{len(negs)} stayed silent")
    shown = details if args.verbose else [d for d in details if d[2].command != d[0].expected]
    if shown:
        print("\n" + ("all held-out clips:" if args.verbose else "held-out misses and wrongs:"))
    for clip, raw, m in shown:
        flag = "OK   " if m.command == clip.expected else "WRONG" if m.command else "miss "
        print(f"  {flag} {clip.path.name:22} {raw!r:24} -> {m.best} {m.score:.0f}/{m.runner_up:.0f}")


if __name__ == "__main__":
    main()
