# Copy of src/whistleattack/matching.py from https://github.com/nicelifeBS/whistleAttack, for tools/eval/evaluate.py.
"""Turn a Whistle transcript into a VoiceAttack command name.

Pipeline: normalise -> word mappings -> number words to digits -> score against every
command phrase -> accept the best if it clears the threshold and beats the best
*other* command by the margin (so near-ties between commands never fire either).

A phrase scores the better of two similarities: plain spelling, and a sound-class
key in which consonants that ASR confuses share a class and vowels drop out
(so "Get your down", "Cheer Dell" and "Keer up" land near "Gear down/up").
"""
from __future__ import annotations

import logging
import re
import unicodedata
from dataclasses import dataclass
from pathlib import Path

from rapidfuzz import fuzz

log = logging.getLogger(__name__)

# Tuned on whistleAttack eval/va5 (2026-10-24): with the command phrases as Whistle keywords, 85 runs all 50
# command takes and none of 36 non-command takes ("Get down", "Give it up", "Look out" fired at 70-80).
DEFAULT_THRESHOLD = 85
DEFAULT_MARGIN = 10
# Near-ties are settled on the words that differ ("on" vs "off"): the winner's own words must match
# the transcript this much better than each rival's. A phrase with no words of its own (a rival's
# phrase contains all of them) counts as neutral, so the rival's extra words decide.
TIEBREAK_MARGIN = 20
TIEBREAK_MIN = 70
NO_DISTINCT_WORDS = 70.0
# Every content word of a matched phrase must be heard at least this well (spelling or sound).
WORD_MIN = 60


# --- normalisation -------------------------------------------------------------

def normalise(text: str) -> str:
    """Lowercase, punctuation and hyphens to spaces, letters split from digits, whitespace collapsed.

    Keeps umlauts and ß. "F10" -> "f 10", so a function key reads the same however it's written.
    """
    text = unicodedata.normalize("NFC", text).lower()
    text = re.sub(r"[^\w\s]|_", " ", text)
    text = re.sub(r"(?<=[^\W\d_])(?=\d)|(?<=\d)(?=[^\W\d_])", " ", text)
    return re.sub(r"\s+", " ", text).strip()


# Letters spelled out as words, the way VoiceAttack profiles name keys for Windows speech
# recognition ("eff 1" for F1). Only unambiguous ones: "see", "bee" or "tea" are words.
LETTER_NAMES = {"eff": "f"}


def strip_edges(text: str) -> str:
    """'Gear down.' -> 'Gear down': the transcript as a verbatim command name."""
    return re.sub(r"\s+", " ", text).strip().strip(".,!?;:\"'").strip()


# --- numbers -------------------------------------------------------------------

_UNITS = {
    "zero": 0, "oh": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
    "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14,
    "fifteen": 15, "sixteen": 16, "seventeen": 17, "eighteen": 18, "nineteen": 19, "niner": 9,
    # German; "ein"/"eine" are left alone: usually an article or "on" ("Waffen ein"), not a number.
    "null": 0, "eins": 1, "zwei": 2, "zwo": 2, "drei": 3, "vier": 4, "fünf": 5, "sechs": 6, "sieben": 7,
    "acht": 8, "neun": 9, "zehn": 10, "elf": 11, "zwölf": 12, "dreizehn": 13, "vierzehn": 14,
    "fünfzehn": 15, "sechzehn": 16, "siebzehn": 17, "achtzehn": 18, "neunzehn": 19,
}
_TENS = {
    "twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80,
    "ninety": 90, "zwanzig": 20, "dreißig": 30, "vierzig": 40, "fünfzig": 50, "sechzig": 60,
    "siebzig": 70, "achtzig": 80, "neunzig": 90,
}
_DE_COMPOUND = re.compile(r"^(ein|zwei|drei|vier|fünf|sechs|sieben|acht|neun)und(" + "|".join(
    t for t in _TENS if not t.isascii() or t.endswith("zig")) + r")$")


def _word_value(word: str) -> int | None:
    if word.isdigit():
        return int(word)
    if word in _UNITS:
        return _UNITS[word]
    if word in _TENS:
        return _TENS[word]
    m = _DE_COMPOUND.match(word)  # dreiundsiebzig -> 73
    if m:
        unit = 1 if m[1] == "ein" else _UNITS[m[1]]
        return _TENS[m[2]] + unit
    return None


def numbers_to_digits(text: str) -> str:
    """'tacan channel seventy three' -> 'tacan channel 73'; 'two five one' -> '251'.

    Tens followed by a unit combine (seventy three -> 73); any run of number tokens
    then joins into one digit string, so spoken digit-by-digit numbers match "251".
    """
    out: list[str] = []
    run: list[str] = []
    words = text.split()
    i = 0
    while i < len(words):
        value = _word_value(words[i])
        if value is None:
            if run:
                out.append("".join(run))
                run = []
            out.append(words[i])
            i += 1
            continue
        if words[i] in _TENS and i + 1 < len(words) and words[i + 1] in _UNITS and _UNITS[words[i + 1]] < 10:
            value += _UNITS[words[i + 1]]
            i += 1
        run.append(str(value))
        i += 1
    if run:
        out.append("".join(run))
    return " ".join(out)


# --- sound-class key -----------------------------------------------------------

_DIGRAPHS = [("sch", "S"), ("ch", "K"), ("ck", "K"), ("ph", "F"), ("sh", "S"), ("th", "T"), ("qu", "K")]
_CLASS = {**dict.fromkeys("gkcq", "K"), **dict.fromkeys("dt", "T"), **dict.fromkeys("bp", "P"),
          **dict.fromkeys("fv", "F"), **dict.fromkeys("szxß", "S"), **dict.fromkeys("mn", "N"),
          "j": "J", "l": "L", "r": "R"}
_VOWELISH = set("aeiouyäöüwh")  # w and h colour the vowel ("down", "hear") more than they sound


def sound_key(word: str) -> str:
    """Consonant skeleton with confusable sounds merged: gear/care/keer/cheer -> KR, down -> TN.

    Digits are kept as they are. A word starting with a vowel sound keeps an 'A' marker.
    """
    if word.isdigit():
        return word
    for digraph, cls in _DIGRAPHS:
        word = word.replace(digraph, cls)
    key: list[str] = []
    for i, ch in enumerate(word):
        if ch.isupper() or ch.isdigit():  # a digraph class from above, or a digit: kept as is
            cls = ch
        elif ch in _VOWELISH or ch not in _CLASS and not ch.isalpha():
            if i == 0 and ch not in "wh":
                key.append("A")
            continue
        else:
            cls = _CLASS.get(ch, ch.upper())
        if not key or key[-1] != cls:
            key.append(cls)
    return "".join(key) or "A"


# Short words ASR inserts or splits off ("Give you down", "Get your down", "A lock abandoned"). They add
# noise to the sound key only; the spelling score still sees every word.
FILLERS = frozenset("a an the you your it to her i oh uh um".split())


def phrase_key(text: str) -> str:
    return " ".join(sound_key(w) for w in text.split() if w not in FILLERS) or "A"


# --- word mappings and command list ---------------------------------------------

def _read_lines(path: Path | None) -> list[str]:
    if path is None or not path.exists():
        return []
    lines = (line.strip() for line in path.read_text(encoding="utf-8-sig").splitlines())
    return [line for line in lines if line and not line.startswith("#")]


def read_word_mappings(path: Path | None) -> dict[str, str]:
    """WhisperAttack format, one per line: alias;alias=Replacement."""
    mappings = {}
    for line in _read_lines(path):
        if "=" in line:
            aliases, target = line.split("=", 1)
            mappings.update({normalise(a): normalise(target) for a in aliases.split(";") if normalise(a)})
    return mappings


ALL_PROFILES = "*"
_SECTION = re.compile(r"^\[([^;\]]+)\]$")


@dataclass(frozen=True)
class CommandPhrase:
    phrase: str           # normalised spoken phrase
    command: str          # what to execute in VoiceAttack when this phrase is heard
    profile: str | None   # VoiceAttack profile it belongs to; None or "*" = always active
    group: str = ""       # phrases of one command share a group and never compete; default: command

    def __post_init__(self):
        if not self.group:
            object.__setattr__(self, "group", self.command)


def read_command_entries(path: Path | None) -> list[CommandPhrase]:
    """commands.txt, one command per line:

      Gear up                        say it, run it
      tecken;tech ken=TACAN          aliases: say any, run "TACAN"
      Jester music on|Jester music off
                                     one VoiceAttack command's alternatives: run the one said

    `[Profile name]` lines start a profile's section; `[*]` (and anything before the first
    section) is active whatever the profile, like VoiceAttack's global profiles.
    """
    entries, profile = [], None
    for line in _read_lines(path):
        section = _SECTION.match(line)
        if section:
            profile = section[1].strip()
            continue
        if "|" in line and "=" not in line:
            alternatives = [a.strip() for a in line.split("|") if a.strip()]
            entries += [CommandPhrase(normalise(a), a, profile, alternatives[0]) for a in alternatives]
            continue
        aliases, command = line.split("=", 1) if "=" in line else (line, line)
        for alias in aliases.split(";"):
            if alias.strip():
                entries.append(CommandPhrase(normalise(alias), command.strip(), profile))
    return entries


def read_commands(path: Path | None) -> dict[str, str]:
    """{spoken phrase (normalised): VoiceAttack command name}, all profiles together."""
    return {e.phrase: e.command for e in read_command_entries(path)}


# --- matcher -------------------------------------------------------------------

@dataclass(frozen=True)
class Match:
    command: str | None  # the command to run, None if nothing cleared the bar
    text: str            # transcript after normalisation, mappings and numbers
    best: str | None     # best-scoring command even when rejected, for logs
    score: float
    runner_up: float     # best score of any *other* command


@dataclass(frozen=True)
class _Target:
    command: str
    group: str
    phrase: str
    profile: str | None
    text: str
    key: str


class Matcher:
    def __init__(self, commands: dict[str, str] | list[CommandPhrase] | None = None,
                 mappings: dict[str, str] | None = None,
                 threshold: float = DEFAULT_THRESHOLD, margin: float = DEFAULT_MARGIN):
        self.mappings = mappings or {}
        self.threshold = threshold
        self.margin = margin
        self._aliases = sorted(self.mappings, key=len, reverse=True)
        if isinstance(commands, dict):
            commands = [CommandPhrase(normalise(p), c, None) for p, c in commands.items()]
        self._targets = []
        for entry in commands or []:
            text = self.prepare(entry.phrase)
            profile = None if entry.profile in (None, ALL_PROFILES) else entry.profile
            self._targets.append(_Target(entry.command, entry.group, entry.phrase, profile, text, phrase_key(text)))
        self._profiles = {t.profile.casefold() for t in self._targets if t.profile}

    @classmethod
    def from_files(cls, commands: Path | None, mappings: Path | None, **kwargs) -> "Matcher":
        return cls(read_command_entries(commands), read_word_mappings(mappings), **kwargs)

    @property
    def commands(self) -> int:
        return len({(t.profile, t.group.casefold()) for t in self._targets})

    def _active(self, profile: str | None) -> list[_Target]:
        """The commands VoiceAttack has loaded for this profile: its own plus the always-active ones.

        Unknown or no profile: everything. A phrase the profile defines itself hides the same
        phrase from the always-active ones, as the active profile has priority in VoiceAttack.
        """
        if not profile or profile.casefold() not in self._profiles:
            return self._targets
        wanted = profile.casefold()
        own = [t for t in self._targets if t.profile and t.profile.casefold() == wanted]
        shadowed = {t.phrase for t in own}
        return own + [t for t in self._targets if t.profile is None and t.phrase not in shadowed]

    def keywords(self, profile: str | None = None) -> list[str]:
        """Every command name and spoken phrase active for the profile, for biasing Whistle."""
        seen: dict[str, None] = {}
        for t in self._active(profile):
            seen.setdefault(t.command)
            if t.phrase != normalise(t.command):
                seen.setdefault(t.phrase)
        return list(seen)

    def prepare(self, text: str) -> str:
        text = " ".join(LETTER_NAMES.get(w, w) for w in normalise(text).split())
        for alias in self._aliases:
            text = re.sub(rf"\b{re.escape(alias)}\b", self.mappings[alias], text)
        return numbers_to_digits(normalise(text))

    def score(self, text: str, key: str, target: _Target) -> float:
        return max(fuzz.ratio(text, target.text), fuzz.ratio(key, target.key))

    def match(self, raw: str, profile: str | None = None) -> Match:
        """Match a transcript against the commands active in `profile` (all commands if None)."""
        text = self.prepare(raw)
        if not text:
            return Match(None, text, None, 0.0, 0.0)
        if not self._targets:  # no command list: verbatim, as in the plugin MVP
            command = strip_edges(raw)
            return Match(command, text, command, 100.0, 0.0)
        key = phrase_key(text)
        # One entry per command (its alternatives grouped), case-insensitive as in VoiceAttack;
        # on equal scores the first target wins, and _active lists the profile's own commands first.
        best: dict[str, tuple[float, _Target]] = {}
        for target in self._active(profile):
            s = self.score(text, key, target)
            group = target.group.casefold()
            if s > best.get(group, (-1.0, target))[0]:
                best[group] = (s, target)
        ranked = sorted(best.values(), key=lambda st: st[0], reverse=True)
        top_score, top = ranked[0]
        runner_up = ranked[1][0] if len(ranked) > 1 else 0.0
        command = None
        if top_score >= self.threshold:
            heard = text.split()
            close = [t for s, t in ranked if top_score - s < self.margin and _covered(t.text, heard)]
            if len(close) == 1 and close[0] is top:
                command = top.command
            elif len(close) > 1:
                command = self._tiebreak(text, close)
        return Match(command, text, top.command, round(top_score, 1), round(runner_up, 1))

    def _tiebreak(self, text: str, close: list[_Target]) -> str | None:
        """Pick among commands that scored within the margin by the words that tell them apart.

        Whole-phrase scores can't separate "Jester music on" from "Jester music off": one short
        word differs, so the scores land within a few points. Comparing only the differing words
        ("on" vs "off") against the transcript can. A command wins if its own words beat every
        rival's own words by TIEBREAK_MARGIN; otherwise nothing runs.
        """
        heard = text.split()
        for candidate in close:
            rivals = [r for r in close if r is not candidate]
            own = [self._distinct_score(candidate, r, heard) for r in rivals]
            theirs = [self._distinct_score(r, candidate, heard) for r in rivals]
            # Its own words must actually be heard, and clearly better than each rival's.
            if all(o >= TIEBREAK_MIN and o - t >= TIEBREAK_MARGIN for o, t in zip(own, theirs)):
                return candidate.command
        return None

    @staticmethod
    def _distinct_score(target: _Target, rival: _Target, heard: list[str]) -> float:
        """How well the words only `target` has (not `rival`) show up in the transcript, 0-100."""
        rival_words = set(rival.text.split())
        own = [w for w in target.text.split() if w not in rival_words]
        if not own:
            return NO_DISTINCT_WORDS
        return sum(_word_heard(w, heard) for w in own) / len(own)


def _word_heard(word: str, heard: list[str]) -> float:
    """Best match of one phrase word among the transcript's words, by spelling or by sound, 0-100."""
    return max((max(fuzz.ratio(word, h), fuzz.ratio(sound_key(word), sound_key(h))) for h in heard), default=0.0)


def _covered(phrase: str, heard: list[str]) -> bool:
    """Every content word of the phrase shows up in the transcript somehow.

    A long phrase sharing most words with the transcript scores high as a whole even when the
    word that matters is missing: "jester radar high" scores 86 against "jester radar on".
    """
    return all(_word_heard(w, heard) >= WORD_MIN for w in phrase.split() if w not in FILLERS)
