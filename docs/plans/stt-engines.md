# Plan: other speech-to-text engines

Status: Zipformer built in as a choice in Settings (Whistle stays the default). Benchmark: `tools/stt-bench/`.

## Context
Speech runs on Whistle (Cactus needle, in process). The question: is there an engine that gets more commands
right, on the CPU, in **≤ 500 MB of RAM**? Disk size and CPU use may grow.

Decisions:
- English only for now.
- "Plugin" = engines built into the app behind one interface, picked in Settings. No DLL loading from a
  folder, no separate process.
- An engine wins only with **more right than Whistle and 0 wrong** (the rule of `WhistleEndToEndTests`).
- Real-voice clips decide; the synthetic clips are a first check.

## Candidates

| Engine | .NET | Disk | RAM | Biasing to the profile words | Verdict |
|---|---|---|---|---|---|
| Whistle 2.0.0 (today) | libneedle, own P/Invoke | 17 MB | ~105 MB | Keywords | Baseline |
| Zipformer transducer, GigaSpeech, int8 | sherpa-onnx (`org.k2fsa.sherpa.onnx`) | 74 MB | ~250 MB | Hotwords (native) | **Round 1: best** |
| Whisper small.en q5_1 | whisper.cpp (`Whisper.net`) | 190 MB | ~990 MB | Initial prompt | Round 1: out (RAM, speed) |
| Moonshine v2 Small / Medium | sherpa-onnx | 120–250 MB | ~250–500 MB (not measured) | None | Next round |
| Vosk small + grammar | `Vosk` | ~50 MB | ~100–300 MB (not measured) | Hard grammar | Next round; slots need grammar |
| Parakeet TDT 0.6B int8 | sherpa-onnx | ~650 MB | ~1.4 GB (reported) | None | Out (RAM) |
| Whisper medium / large-v3-turbo | whisper.cpp | 0.5–1.5 GB | 1.5–3.9 GB | Prompt | Out (RAM) |

Why sherpa-onnx: one NuGet package runs many model families (Zipformer, Moonshine, Whisper ONNX, Paraformer,
SenseVoice), so one integration opens several engines. Hotwords work for transducer models only.

## Benchmark (round 1)
`tools/stt-bench`, synthetic clips (`tests/DcsVoiceCom.Core.Tests/data/clips`, 20 English commands × 4 takes =
80 clips), Linux, 4-core VM (AVX-512), 2 engine threads unless noted. Same `SpeechService` + `PhraseMatcher` as
the app.

| Engine | Right | Wrong | Missed | Peak RAM | Latency mean / p95 |
|---|---|---|---|---|---|
| Whistle | 66 | 0 | 14 | 105 MB | 79 / 153 ms |
| Zipformer, no hotwords | 67 | 0 | 13 | 244 MB | |
| Zipformer, hotwords boost 1.5 | 68 | 0 | 12 | 247 MB | 114 / 148 ms |
| Zipformer, hotwords boost 4 | **70** | 0 | 10 | 246 MB | |
| Whisper small.en q5_1 (4 threads) | 70 | 0 | 10 | 986 MB | 5729 / 5975 ms |

Notes:
- Zipformer's misses: "AWACS" (heard "a wax"), "Shkval" (spelled "s h k"), "cage uncage" in 2 takes, and all 4
  "251 decimal 000" takes. Those last 4 are a matcher gap, not the engine: it says "decimal zero" and the phrase
  wants "000". Spoken-number output ("two hundred and fifty one") already matches.
- Hotwords must be in the model's alphabet: GigaSpeech has no digits, so "TACAN CHANNEL 73" gives no bias to "73".
  An integration should spell numbers as words for hotwords (`NumberWords`).
- Whisper encodes a 30 s window for every clip, so a 2 s command takes ~5 s on this CPU. A smaller audio context
  (`WithAudioContextSize(384)`) cut it to ~3.7 s and 421 MB but lost accuracy. Whisper small ties Zipformer on
  right answers, but at ~4× the RAM and ~50× the latency, and it once heard "navigation lights **off**" for "on"
  (the matcher refused it). Whisper is not a fit here. faster-whisper has the same 30 s window, ~1 GB for small
  int8, and no .NET binding.

### Run it
```
dotnet run -c Release --project tools/stt-bench -- --engine zipformer --boost 4
dotnet run -c Release --project tools/stt-bench -- --engine whistle --real
```
`--real` uses `DCS_REAL_CLIPS`, else `private-clips` in the repo; `--clips <folder>` takes any folder. Engines: `whistle`, `zipformer`, `whisper`. Models are downloaded on first run to
`%LOCALAPPDATA%\DcsVoiceCom\bench-models` (SHA-256 pinned). `--verbose` prints every clip.

### Real voice (Windows, 8 threads, 20 English clips, one take each)

| Engine | Right | Wrong | Missed | Peak RAM | Latency mean / p95 |
|---|---|---|---|---|---|
| Whistle | **18** | 0 | 2 | 91 MB | 35 / 40 ms |
| Zipformer, boost 4 | **18** | 0 | 2 | 235 MB | 93 / 133 ms |

Zipformer's misses: "inbound for landing" heard "in boundfort landing", and "251" heard "um one" (the hotword
"251" has no effect: the model has no digits, see above). "AWACS", "Shkval", "TACAN" and "cage uncage" were all
right. The first clip took 829 ms: the recognizer is rebuilt with the hotwords on the first command; the app
should build it when the profile loads.

Whistle missed "AWACS bogey dope" ("A wax bowie dope") and "gear up" ("Kirapa"). **A tie, 18 to 18**, with
different misses; Whistle is ~3× faster and uses ~40% of the RAM. 20 clips are too few to call it.

### Real voice, F-14 Jester set (28 clips, full F-14 profile, 47 commands)
`dcs-voicecom-voicelines/recordings/take_02` with `clips.csv` (file, lang, command, said) and the user's F-14
profile (`--profile`), so every other command in the profile can be confused with the right one. 2 threads.

| Engine | Right | Wrong | Missed | Peak RAM | Latency mean / p95 |
|---|---|---|---|---|---|
| Whistle | 20 | 0 | 8 | 107 MB | 64 / 105 ms |
| Zipformer, boost 1.5 | **26** | 0 | 2 | ~220 MB | |
| Zipformer, boost 4 | 25 | 0 | 3 | 221 MB | 83 / 68 ms |

Whistle's 20 is exactly what the app did when the clips were recorded, so the bench matches the app. Whistle
hears "Jester" as "Just" in almost every clip; Zipformer hears "jester". Zipformer's misses at boost 1.5: "jester
go active" heard "just a active" (Whistle heard "Just active" too) and "jester rws" heard "jester r ws" (too close
to TWS). Boost 4 also turned "jester radar on" into "jester right". **Boost 1.5 is the better setting here.**

## Next steps
1. **More real voice:** record 3–5 takes per command (`tools/eval/record.py`), plus some radio noise and a few
   non-commands, and rerun both. 20 clips gave a tie.
2. ~~Spell numbers as words for hotwords~~ done: "251" now also gives "two fifty one", "two five one" and
   "two hundred (and) fifty one". On the synthetic set Zipformer now hears "two hundred and fifty one decimal zero
   zero zero"; the matcher still wants "000" (step 4).
3. If Zipformer wins, build it in (below). If not, round 2: Moonshine v2 Small and Vosk + grammar.
4. Either way: let the matcher accept "zero" for "000" style digits (separate change).

## Integration design (when an engine wins)
- `ISpeechEngine { Id, DisplayName, Languages, OpenAsync(ctx) → ISpeechToText }` in `Core/Speech`.
  `WhistleEngine` wraps today's `SpeechService.OpenAsync` load and download code.
- `SpeechSettings.Engine` (default `"whistle"`) and engine options (hotword boost, threads). If the chosen engine
  fails to load, log why and fall back to Whistle.
- `ZipformerRecognizer` and `BpeVocab` move from the bench into Core as they are; model download through a
  pinned downloader like `ModelDownloader`.
- Settings: engine dropdown. Help: `docs/help/10-settings.md`. `THIRD-PARTY-NOTICES.md`: sherpa-onnx and
  onnxruntime (Apache-2.0 / MIT), the Zipformer model (Apache-2.0).
- RAM: the engine stays loaded for the session (~250 MB), like Whistle today.
