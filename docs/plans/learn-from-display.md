# Plan: "Learn from display": build fields live from DCS-BIOS

Status: planned, not implemented yet.

## Context
Today, "Fill from radio…" (`EnterValueEditorViewModel.Suggest`, `EnterValueGuess.cs`) only guesses from control names: `<PREFIX>_<digits>_DIAL` for wheels, `<PREFIX>_<char>` for keypad keys (`EnterValueGuess.Keys`). It breaks when a module names things differently, uses digits it doesn't know, or has a display with extra text. It also never works out real ranges (e.g. `22-39`, `00,25,50,75`, `X,Y`), so the user fixes those by hand.

Goal: build the fields from what DCS-BIOS shows **while DCS runs**, for **any display device**: radios, TACAN, ILS, IFF, nav and comm panels, CDUs, UFCs and scratchpads. The app clicks or presses each candidate control once, sees which characters on the display change, and learns the field from that. Names become only a hint, so it works across airframes and device types.

Both methods are covered:
- **Wheels**: knobs, dials, rotary and toggle switches that step a part of the display.
- **Keypad**: keys that type characters into a scratchpad or entry line.

## Approach (hybrid: names to find candidates, live probing to confirm)

### 1. Pick the display
- Start from the chosen display (`Display`). If none is set, use `EnterValueGuess.Display`, or let the user pick **any string output** in the catalog, from any category.
- **Find noisy cells first.** Read the display 5 times over about 1 s without sending anything. Cells that change on their own (clocks, blinking cursors, timers) are marked noisy and ignored in every diff below.

### 2. Find candidates (no DCS needed), new `DisplayProbe.Candidates` in `src/DcsVoiceCom.Core/Editing/`
- Candidates are controls in the **same category** as the display, or with the same name stem, that have `fixed_step`, `set_state`, `variable_step` or `action` inputs. Some modules put the display in another category than its controls (e.g. CDU, UFC), so the user can add more categories.
- Names and descriptions only **rank** candidates, they no longer drop them. MODE, CHANNEL, X/Y and similar now count, because TACAN, IFF and preset wheels need them. Names today's guess finds (`_DIAL`, `EnterValueGuess.Keys`) go first, so the F-14 keeps working the same way.
- **Never-touch list**, dropped always, by identifier or description: POWER, MASTER, ARM, JETT, FIRE, EJECT, CANOPY, GEAR, FLAP, HOOK, EMERG, SELF-TEST, ZEROIZE, and keypad keys ENT, ENTER, EXEC, LOAD, CMD (they would act on the entry).
- **The user ticks candidates before probing.** The list shows each control and its description, with the likely ones ticked.

### 3. Probe each candidate live, new async `DisplayProbe.LearnAsync(context, display, candidates, progress, cancel)`
Wheels and keypad keys are told apart by input type: `fixed_step`, `variable_step` and multi-position `set_state` are wheels, `action` and 2-position push buttons are keys. If unsure, probe as a wheel; if it acts like a key (the display changes once and then stops), switch.

**Wheel**, for each candidate:
1. Read the display.
2. Send one "up". Reuse the click/step choice from `TestClickAsync` (`EnterValueEditorViewModel.cs:540`) and `EnterValueGuess.ModeOf`.
3. Wait about 150 ms and read the display again.
4. **Diff the two readings** (noisy cells ignored). Changed characters give the field's `At`/`Len`. No change means it doesn't drive this display, so drop it.
5. **Learn the range:** keep clicking up until the text repeats its first value, at most `MaxSteps` (40). Record each value in click order, which gives the range. A run of numbers like 22..39 is written `22-39`; anything else, including letters, is written as a list (`00,25,50,75`, `X,Y`, `A,B,C`), in `DisplayLayout.ParseRange` format.
6. If the values go down, set `Flip`. If the control doesn't wrap and stops at the end, click down to learn the other end.
7. Put the wheel back to where it started. Click back the number of clicks taken, or use `set_state` if it has one.
8. For set mode, use `Mode=Set` only when `set_state` max matches the number of learned values, which is the same rule as today.

If two controls change the same characters, keep the one with more values, or the bigger step.

**Keypad**:
1. **Find the clear key first.** Press each CLR/CLEAR/RTN-like candidate after typing one key; the one that empties the entry is `Clear`. If none is found, ask the user to clear by hand between steps and warn that cleanup isn't automatic.
2. For each key candidate: clear, read, press once, read. The new character is that key's character in the key table. A key that changes nothing is dropped. A key that changes more than one character (a page or mode key) is dropped and listed as "changes the page".
3. **Learn the field:** type two keys and see where the text appears. That gives `At`/`Len` (the whole entry area) and `Align` (left if text grows to the right, right if it shifts left).
4. Clear again at the end, so the scratchpad is left empty. Never press ENT/EXEC.
5. Characters not found keep today's name guess (`EnterValueGuess.Keys`), marked "not tested".

### 4. Build the fields
- Wheels: sort the wheel fields by `At`. Add read-only fields for fixed characters between them (the decimal point, `[`, `]`), which reuses the logic at `EnterValueEditorViewModel.cs:651`. Remove the hardcoded `1-20` range for presets, because probing learns it.
- Keypad: one keypad field plus the learned key table and clear key (one keypad field per command, as today).
- If both wheels and keys change the display, ask the user which method to use (one method per command, as today).

### 5. UI (`EnterValueWindow.xaml`, `EnterValueEditorViewModel`)
- **Probing runs only when the user asks for it**, by pressing its button in the Enter value editor. The app never probes on its own: not when the editor opens, not when DCS connects, not during voice commands or at runtime. `CommandEngine` never calls `DisplayProbe`.
- **"Learn from display…" is its own button**, separate from "Fill from radio…". Fill keeps today's name guess and sends nothing to DCS. Learn is enabled only while DCS is live (`CanTest`), and it:
  1. Shows the candidate list to tick (step 2).
  2. Confirms "This turns knobs and presses keys on this panel, then puts them back".
  3. Shows progress ("Testing PLT_UHF1_110_DIAL… 3/6").
  4. Offers Cancel. Cancel still puts wheels back and clears the keypad entry.
- Closing the editor stops a probe that is running, with the same cleanup.
- An editor test checks that opening the editor, or connecting to DCS, sends nothing.
- Keep the result editable, with the message "Check them before saving". Dropped controls are listed with the reason ("no change", "changes the page", "never-touch").

## Files
- New: `src/DcsVoiceCom.Core/Editing/DisplayProbe.cs` (candidates, never-touch list, noise, diff, range and key learning). Keep the pure functions testable: `NoisyCells(readings)`, `DiffSpan(before, after, noisy)`, `RangeFromValues(list)`, `KeyFromDiff(before, after)`.
- Edit: `src/DcsVoiceCom.ViewModels/EnterValueEditorViewModel.cs` (async learn command, candidate list, progress, cancel).
- Edit: `src/DcsVoiceCom.App/EnterValueWindow.xaml` (button, candidate list, progress text, cancel).
- Reuse: `EnterValueContext.Send` / `LiveBuffer`, `ReadDisplay`, `EnterValueGuess.ModeOf` / `Display` / `Keys`, `DisplayLayout.ParseRange`, `CatalogBiosSender` (variable_step INC/DEC).
- Docs: `docs/app.md` and the Enter value help page in `docs/help/` (button description).

## Verification
- **Unit tests** in `tests/DcsVoiceCom.Core.Tests`: `NoisyCells`, `DiffSpan` with noisy cells, `RangeFromValues` (`22..39` → `22-39`, `00,25,50,75` → list, `X,Y` → list, a reversed list sets Flip), `KeyFromDiff`, candidate ranking and the never-touch list.
- **Probe tests** with a fake cockpit (`DisplayFieldEngineTests.Uhf` / `FakeCockpit`):
  - F-14 UHF: fields match `profiles/F-14BU.json` (`22-39`, `0-9`, `0-9`, `00,25,50,75`) and knobs are back where they started.
  - A wheel display with **non-`_DIAL` names**, no decimal point, and a letter wheel (TACAN-like `0-12`, `0-9`, `X,Y`).
  - A keypad scratchpad with odd key names, a clock cell (noise), a page key that must be dropped, and an ENT key that must never be pressed. Key table and `Align` match, scratchpad ends empty.
- **Editor tests** in `tests/DcsVoiceCom.ViewModels.Tests/EnterValueEditorTests.cs`: learn wheels, learn keypad, cancel with cleanup, and the no-DCS fallback.
- Run `dotnet test`, then check the UI with the `run-dcs-voicecom` skill (screenshot of the candidate list, progress and result).
