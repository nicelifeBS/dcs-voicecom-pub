# Plan: "Learn from radio": build wheel fields live from DCS-BIOS

Status: planned, not implemented yet.

## Context
Today, "Fill from radio…" (`EnterValueEditorViewModel.Suggest`, `EnterValueGuess.cs`) only guesses from control names (`<PREFIX>_<digits>_DIAL`). It breaks when a module names things differently, uses digits it doesn't know, or has a display with extra text. It also never works out real ranges (e.g. `22-39`, `00,25,50,75`), so the user fixes those by hand.

Goal: build the fields from what DCS-BIOS shows **while DCS runs**. The app clicks each candidate control once, sees which characters on the display change, and learns the field from that. Names become only a hint, so it works across airframes.

## Approach (hybrid: names to find candidates, live probing to confirm)

### 1. Find candidates (no DCS needed), new `RadioProbe.Candidates` in `src/DcsVoiceCom.Core/Editing/`
- Start from the chosen display (`Display`). If none is set, use `EnterValueGuess.Display`, or let the user pick any string output.
- Candidates are controls in the **same category** as the display, or with the same name stem, that have `fixed_step`, `set_state` or `variable_step` inputs.
- Skip obvious non-frequency controls by identifier or **description** keywords: VOL, SQL, SQUELCH, BRT, TEST, POWER, MODE, FUNC, LIGHT, CHANNEL-unless-presets.
- Put the ones today's `_DIAL` regex finds first, so the F-14 keeps working the same way.

### 2. Probe each candidate live, new async `RadioProbe.LearnAsync(context, display, candidates, progress, cancel)`
For each candidate:
1. Read the display.
2. Send one "up". Reuse the click/step choice from `TestClickAsync` (`EnterValueEditorViewModel.cs:540`) and `EnterValueGuess.ModeOf`.
3. Wait about 150 ms and read the display again.
4. **Diff the two readings.** Changed characters give the field's `At`/`Len`. No change means it isn't a frequency wheel, so drop it.
5. **Learn the range:** keep clicking up until the text repeats its first value, at most `MaxSteps` (40). Record each value in click order, which gives the range. A run of numbers like 22..39 is written `22-39`, other lists like `00,25,50,75` are written as a list (`DisplayLayout.ParseRange` format).
6. If the values go down, set `Flip`. If the control doesn't wrap and stops at the end, click down to learn the other end.
7. Put the wheel back to where it started. Click back the number of clicks taken, or use `set_state` if it has one.
8. For set mode, use `Mode=Set` only when `set_state` max matches the number of learned values, which is the same rule as today.

If two controls change the same characters, keep the one with more values, or the bigger step.

### 3. Build the fields
- Sort the wheel fields by `At`.
- Add read-only fields for fixed characters between them (the decimal point), which reuses the logic at `EnterValueEditorViewModel.cs:651`.
- Remove the hardcoded `1-20` range for presets, because probing learns it.

### 4. UI (`EnterValueWindow.xaml`, `EnterValueEditorViewModel`)
- **Probing runs only when the user asks for it**, by pressing its button in the Enter value editor. The app never probes on its own: not when the editor opens, not when DCS connects, not during voice commands or at runtime. `CommandEngine` never calls `RadioProbe`.
- **"Learn from radio…" is its own button**, separate from "Fill from radio…". Fill keeps today's name guess and sends nothing to DCS. Learn is enabled only while DCS is live (`CanTest`), and it:
  1. Confirm "This turns the radio's knobs and puts them back".
  2. Show progress ("Testing PLT_UHF1_110_DIAL… 3/6").
  3. Offer Cancel.
- Closing the editor stops a probe that is running.
- An editor test checks that opening the editor, or connecting to DCS, sends nothing.
- Keep the result editable, with the message "Check them before saving".

## Files
- New: `src/DcsVoiceCom.Core/Editing/RadioProbe.cs` (candidates, diff, range learning). Keep the pure functions testable: `DiffSpan(before, after)`, `RangeFromValues(list)`.
- Edit: `src/DcsVoiceCom.ViewModels/EnterValueEditorViewModel.cs` (`Suggest` → async learn command, progress, cancel).
- Edit: `src/DcsVoiceCom.App/EnterValueWindow.xaml` (progress text, cancel).
- Reuse: `EnterValueContext.Send` / `LiveBuffer`, `ReadDisplay`, `EnterValueGuess.ModeOf` / `Display`, `DisplayLayout.ParseRange`, `CatalogBiosSender` (variable_step INC/DEC).
- Docs: `docs/app.md` (button description).

## Verification
- **Unit tests** in `tests/DcsVoiceCom.Core.Tests`: `DiffSpan`, `RangeFromValues` (`22..39` → `22-39`, `00,25,50,75` → list, a reversed list sets Flip), candidate filtering by description.
- **Probe tests** with a fake cockpit that simulates the F-14 UHF (`DisplayFieldEngineTests.Uhf` / `FakeCockpit`) plus one airframe with **non-`_DIAL` names** and no decimal point. Check that the fields match `profiles/F-14BU.json` (`22-39`, `0-9`, `0-9`, `00,25,50,75`) and that the knobs are back where they started.
- **Editor tests** in `tests/DcsVoiceCom.ViewModels.Tests/EnterValueEditorTests.cs`: learn, cancel, and the no-DCS fallback.
- Run `dotnet test`, then check the UI with the `run-dcs-voicecom` skill (screenshot of the progress and result).
