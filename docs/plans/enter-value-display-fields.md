# Plan: Enter value — fields picked on the display

Status: implemented in PRs #21–#25.

Design mock-up: https://claude.ai/artifact/A2S5XAdi4zc5Ehmd2stLa6

## Goal

Today a wheel is set up with numbers: place, width, read-back output and scale. Instead, the user picks
**one read-back display**, sees its characters as cells, and selects cells to make a field. Place and width
come from the selection.

## Decisions (agreed)

| Topic | Decision |
|---|---|
| Picking | Click a cell. Shift+click widens the selection. |
| Place / width | Always from the selection. `read_scale` is dropped. |
| Read back | One display per command. The same display is checked after entering. |
| Method | Switches/dials **or** keypad, never both in one command. |
| Wheels | Each field is a wheel or read-only. `range` is per wheel (`0-9`, `22-39`, or a list like `X,Y`). |
| List ranges | The list order is the click order. |
| Advanced | Click up/down, delay and max clicks are shared by all wheels. |
| Keypad | Exactly one keypad field, typed as one line. Text sits left or right. Never cleared automatically. |
| Second keypad field | Lat then long = two Enter value steps in the main window. One key table per step. |
| Read-only | Marked by the user (e.g. CDU `[` and `]`, the decimal point). |
| No DCS | Warning. Cells come from the DCS-BIOS `max_length`. Values show when DCS runs. |
| Fill / Suggest | Suggests fields only with a live connection; offline it only finds controls. |

## 1. Profile format

New fields on `enter_value` (snake_case JSON as today):

```json
{
  "type": "enter_value", "value": "{freq}", "method": "wheels",
  "display": "PLT_UHF_DISP", "sample": "225.025", "timeout_ms": 1500,
  "fields": [
    { "at": 0, "len": 2, "kind": "wheel", "control": "PLT_UHF1_110_DIAL", "range": "22-39", "mode": "click" },
    { "at": 2, "len": 1, "kind": "wheel", "control": "PLT_UHF1_1_DIAL",   "range": "0-9",   "mode": "click" },
    { "at": 3, "len": 1, "kind": "read_only" },
    { "at": 4, "len": 1, "kind": "wheel", "control": "PLT_UHF1_01_DIAL",  "range": "0-9",   "mode": "click" },
    { "at": 5, "len": 2, "kind": "wheel", "control": "PLT_UHF1_025_DIAL", "range": "00,25,50,75", "mode": "click" }
  ],
  "up": ["2", "1"], "down": ["0", "1"], "delay_ms": 80, "max_steps": 40
}
```

```json
{
  "type": "enter_value", "value": "{text}", "method": "keypad",
  "display": "CDU_LINE9", "sample": "[N4126.5E04155.3       ]", "timeout_ms": 1500,
  "fields": [
    { "at": 0,  "len": 1,  "kind": "read_only" },
    { "at": 1,  "len": 22, "kind": "keypad", "align": "left" },
    { "at": 23, "len": 1,  "kind": "read_only" }
  ],
  "keys": { "A": "CDU_A", "...": "..." }, "press": ["1", "0"]
}
```

- `display`: the read-back string output. Replaces `verify.output` and every wheel's `read`.
- `sample`: the display text when the step was saved. It gives the length, the decimal point position and
  the read-only characters, so the editor works without DCS. Offline it is spaces (length from `max_length`).
- `fields`: `at` = first cell (0-based), `len` = cells. `kind`: `wheel`, `keypad`, `read_only`.
  Cells in no field are ignored.
- Wheel: `control`, `range`, `mode` (`click` / `step` / `set`), `flip` (as today).
- Keypad: `align` (`left` / `right`).
- `timeout_ms` moves up from `verify`.
- `decimals` (optional): implied decimals for a display without a point (`2513` = 251.3).
- Removed from new steps: `min`, `max`, `wheels`, `verify`. `before`/`after` still work but are not shown
  in the editor.

**Model** (`src/DcsVoiceCom.Core/Profiles/Steps.cs`): add `Display`, `Sample`, `TimeoutMs`, `Fields`
(`List<DisplayField>?`) to `EnterValueStep`. Add `DisplayField` and `enum FieldKind { Wheel, Keypad, ReadOnly }`,
`enum TextAlign { Left, Right }`. Keep the old properties for now (see 5. Old profiles).

## 2. Layout logic (new, pure, shared by engine and editor)

New `src/DcsVoiceCom.Core/Engine/DisplayLayout.cs`, next to `EnterValue.cs`:

- `ParseRange("0-9" | "22-39" | "X,Y" | "00,25,50,75")` → numeric span or ordered list.
- `IndexOf(range, text)` → position in the range (number value, or list index). Used for click direction.
- `Target(step, value)` → the text each field must show:
  - Wheels, numeric value, sample has a `.`: format the value with the sample's decimals and line up the
    decimal points (`251.325` on `225.025`).
  - Wheels otherwise: right-align the value over the non-read-only cells (`73Y` on `109X` → ` 73Y`).
  - Wheels, numeric value, sample has no `.` but `decimals` > 0: format with `decimals`, drop the point,
    right-align (`251.3` → `2513`).
  - Keypad: the text placed left or right inside its one field.
- `Check(step, value)` → why the value can't be entered: too long for the field, a wheel target outside its
  `range`, a character with no key. Replaces `EnterValue.CheckNumber` for new steps.
- `Read(step, displayText, field)` → the current text of a field.
- `Place(sample, field)` → place shown in the editor (10, 1, 0.1…). Display only; not stored.
- `Validate(step)` → overlapping fields, a field over the end, a wheel across the decimal point,
  keypad method with ≠ 1 keypad field, wheel fields in keypad method (and the other way).

## 3. Engine (`src/DcsVoiceCom.Core/Engine/CommandEngine.cs`)

`RunEnterValueAsync` takes the new path when `Fields` is set:

1. `Target` + `Check`; a problem → `StepException` (refused before anything is sent).
2. Skip when the display already shows every target.
3. Wheels: for each wheel field, left to right, run the click loop on `Read(display, field)` instead of
   `TryReadWheel`. Direction and wrap use `IndexOf` and the field's range (`TurnDirection` stays).
   `set` mode sends the target text.
4. Keypad: press `before` keys (old steps only), the key for each character, then `after` keys.
5. Check: wait up to `timeout_ms` until every wheel/keypad field shows its target. This is stricter than
   `DisplayMatches`, which stays for old steps.

Dry-run trace keeps its style: `enter 251.325: PLT_UHF1_110_DIAL → 25, …`.

## 4. Validation and catalog checks (`CompiledProfile.cs`)

- `CheckEnterValue`: new-path checks from `DisplayLayout.Validate`, plus "needs a display" and
  "a wheel has no control".
- `FindMissingControls`: check `display` and each field's `control`.

## 5. Old profiles

- Steps without `fields` keep running on the old code (`wheels`, `min`/`max`, `verify`). No breaking change.
- `EnterValueUpgrade.TryUpgrade(step, catalog, liveText?)` (in `Editing/`) converts an old step when the
  editor opens it:
  - display = `verify.output`, else a wheel `read` that is a string output.
  - sample = live text, else `max` formatted with the smallest wheel place, right-aligned to `max_length`
    (this is the one place an offline sample is guessed; the user can re-pick fields once DCS runs).
  - each wheel's `place`/`width` → `at`/`len` by lining up decimal points.
  - top wheel range from `min`/`max` (reuse `EnterValue.Range`), others `0` to `9…9`.
  - keypad: one field over the whole display, left-aligned.
  - If it can't be converted, the editor shows the old step read-only with a "Convert" hint.
- Convert `profiles/F-14BU.json` by hand to the new format: UHF frequency (`PLT_UHF_DISP` is `251` + `.` + `000`,
  7 characters, from the DCS-BIOS F-14 module). The UHF preset step stays in the old format: how the display
  shows a channel is not known without DCS, so it is converted in the editor with a live display.
- Keypad steps are not converted automatically: the field is picked on the live display.

## 6. Editor view model (`src/DcsVoiceCom.ViewModels/EnterValueViewModel.cs`)

Rewrite around cells instead of wheel rows:

- `Display`, `Sample`, `IsLive`, `Cells` (`CellViewModel`: char, kind, selected, field label, target char).
- `PickCell(int index, bool shift)`: select / widen / select an existing field. Errors for overlap and the
  decimal point.
- `Selected` (`FieldViewModel`): kind, control, range, mode, align; read-only `Place`, `Width`, preview
  (`'2' → '5'`, `3 clicks up`, `out of range`).
- `AddField`, `RemoveField`, `SetKind`; keypad method blocks a second keypad field.
- `Fields` for the table; range editable in the row.
- `Method` switch converts wheel ↔ keypad fields, keeps read-only.
- `SuggestFields`: live → `EnterValueGuess` wheels/keys placed on the live text; offline → message only.
- `Keys` (keypad table) and advanced values as today. Remove Place, Width, Read, ReadScale, Min/Max,
  Before/After from the UI.
- Field picking is disabled while offline (first setup needs DCS); other values stay editable.
- `Decimals` (implied decimals) under Advanced, visible when the sample has no `.`.
- `TryValue` drives the target row and previews via `DisplayLayout.Target`.
- `ToStep()` writes the new format.

`EnterValueGuess` (`Editing/EnterValueGuess.cs`): add `PlaceFields(sample, wheels)` that turns guessed
`place`/`width` into `at`/`len` on the live text; reused by the upgrade.

## 7. Window (`src/DcsVoiceCom.App/EnterValueWindow.xaml`)

Match the mock-up:

1. Top card: spoken value, read-back display (search box over string outputs), check timeout,
   Switches/dials | Keypad switch.
2. Display card: live/offline status, offline warning, cell row (`ItemsControl` of buttons; code-behind
   reads `Keyboard.Modifiers` for Shift and calls `PickCell`), field bar, target row, "Try a spoken value",
   editor panel, field table, Fill from radio / Suggest fields.
3. Keypad keys card (keypad method only).
4. Advanced (wheels method only).
5. Cancel / Save.

Cells shrink when the display is longer than ~10 characters (CDU line = 24).

## 8. Tests

- `tests/DcsVoiceCom.Core.Tests`: new `DisplayLayoutTests` (ranges, list ranges, decimal alignment,
  right/left align, too long, overlap, keypad rules); `EnterValueTests` for the new engine path with the fake
  state (click loop on a display, list wheel X/Y, wrap 39→22, skip when already shown, check timeout);
  upgrade tests incl. the old F-14 steps; profile round-trip.
- `tests/DcsVoiceCom.ViewModels.Tests/EnterValueTests.cs`: pick/shift/overlap, place/width from the
  selection, method switch, one keypad field, suggest live vs offline, `ToStep()` output.
- Window: screenshot check with the `run-dcs-voicecom` skill (Wine + Xvfb).

## 9. Docs

- `docs/profiles.md`: new `enter_value` format and examples; note that old fields still load.
- `docs/app.md`: the new Enter value window.
- Release notes for the next beta.

## Order of work (one PR each)

1. Model + `DisplayLayout` + engine new path + validation (`CheckEnterValue`, `FindMissingControls`) + core tests.
2. Upgrade of old steps, F-14 profile conversion + tests.
3. View model + tests.
4. Window XAML + screenshot check.
5. Docs + release notes.

## Answered questions

1. **Old keypad `before`/`after` keys:** keep them working but hidden. The engine still presses them. The
   editor shows a note when a step has them and does not offer them for new steps.
2. **Scaled displays without a decimal point** (`2513` for 251.3): a real case, usually one switch per digit.
   Add an optional `decimals` (implied decimals, default 0) to the step, under Advanced, shown only when the
   sample has no `.`. `Target` formats the value with that many decimals, removes the point and right-aligns
   it (`251.3`, decimals 1 → `2513`). Each digit is then its own one-cell wheel field.
3. **First setup needs DCS.** Adding or changing fields needs a live display (that gives the sample).
   Without DCS the editor opens existing steps and shows the offline warning, but field picking is
   disabled. Range, mode, control and keys can still be edited.
