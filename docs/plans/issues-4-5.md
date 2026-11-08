# Plan: sorted command list (issue 4) and shifting displays (issue 5)

Status: plan, not started.

## Issue 4 — sort the command list

**Problem.** The Commands list in the main window shows commands in the order they were added. Hard to find one.
**Rule.** Sort in the UI only. The profile file keeps its order; new commands are still appended.

Assumption: "action list" = the Commands list (panel 3). The steps of a command are *not* sorted: they run top
to bottom and ↑ ↓ reorder them.

### Steps

1. **Sort key.** The voice line shown in the list (`CommandItemViewModel.Phrase`), case-insensitive, with
   `StringComparer.OrdinalIgnoreCase`; ties by `Name`. "(no phrase)" goes last.
2. **View model** (`src/DcsVoiceCom.ViewModels/MainViewModel.cs`):
   - `RebuildCommands()`: add items ordered by the sort key. `_profile.Commands` is not touched.
   - `AddCommand()` / `Duplicate()`: append to `_profile.Commands` as today, but insert the item into
     `Commands` at its sorted index (small `InsertSorted` helper). Select it.
   - `Delete()`: keep selecting the item that now sits at the old index (works as is).
   - Phrase edited: don't re-sort while typing (the item would jump). Re-sort when the command editor loses
     focus / another command is selected, or on Save. Keep the selection.
3. Filter, *Hide seeded* and the count work on `IsVisible` and need no change.
4. **Tests** (`tests/DcsVoiceCom.ViewModels.Tests`): list is sorted after load; new and duplicated commands
   land in place; profile file order unchanged after Save (append still at the end); edited phrase re-sorts on
   leave, not per key.
5. **Docs.** `docs/help/02-main-window.md` item 3: "sorted by voice line". Release notes.

Small change. No new UI. Screenshot not needed.

## Issue 5 — a wheel that pushes the display text (A-10C II FM radio)

**What works already** (beta 6, `fa5891d`): a 0-15 dial shown in a *fixed* 2-cell field (` 9`, `10`).

**What is still broken.** The display has no padding. When the first dial goes from 9 to 10 the rest of the text
moves one cell (`9.250` → `10.250`). Fields are fixed cells (`at`, `len`), so after the jump every field reads
the wrong characters. The engine clicks the wrong wheel or never finishes; Learn drops the dial
("changes the same characters as …").

### Idea: anchor the fields

Fields keep `at`/`len` as measured on the sample, but are read relative to an **anchor** that moves with the text:

- `anchor: "point"` — the decimal point. Offset = live point position − sample point position.
- `anchor: "right"` — the end of the trimmed text (displays without a point).
- No `anchor` (default) — fixed cells, as today. Old profiles don't change.

The leftmost wheel is the one that grows. Its field reads everything left of the next field (up to its range's
widest value), so `9` and `10` both read right.

### Steps

1. **Model** (`src/DcsVoiceCom.Core/Profiles/Steps.cs`): `EnterValueStep.Anchor` (`enum DisplayAnchor { None, Point, Right }`),
   omitted from JSON when `None`.
2. **Layout** (`src/DcsVoiceCom.Core/Engine/DisplayLayout.cs`):
   - `Offset(step, display)` → how far the live text moved from the sample (0 for `None`; null if the anchor
     isn't found, e.g. display blank).
   - `Read`, `Shows`, `ShowsAll` take the step (or the offset) and shift the field. The growing field is
     widened to the left by its range's widest value minus `len`.
   - `Targets`: target text per field stays the same (line up on the point already); only reading changes.
   - `Validate`: `Point` needs a `.` in the sample; anchored step must be wheels method.
3. **Engine** (`CommandEngine.RunEnterValueAsync`): re-read the offset after each click (the text may jump mid-loop).
   Offset null → wait/retry until `timeout_ms`, then fail with a clear message.
4. **Learn** (`src/DcsVoiceCom.Core/Editing/DisplayProbe.cs`):
   - While stepping a wheel, check if a reading is the old text with one char inserted/removed left of the point
     (or at the left end). If so, mark the step `Point` (or `Right`) instead of dropping the dial.
   - Measure all other wheels with the offset applied, so their cells don't clash.
   - Replace the "can't be learned" note with "display moves when … gets wider: fields follow the decimal point".
5. **Editor** (`EnterValueEditorViewModel.cs`, `EnterValueWindow.xaml`): an *Anchor* choice under Advanced
   (Fixed cells / Decimal point / Right end). Live preview uses the offset. Learn sets it.
6. **Tests:**
   - `DisplayLayoutTests`: offset for point and right; `9.250` / `10.250` read 9 and 10; other fields still right;
     no anchor = old behaviour; validate rules.
   - `EnterValueTests` (engine, fake cockpit): dial 0-15 unpadded, enter `9.250` from `12.500` and back.
   - `DisplayProbeTests`: a `ShiftingDial` cockpit (like `WideDial` but no padding) → all wheels learned, anchor set.
   - Profile round-trip with and without `anchor`.
7. **Docs:** `docs/profiles.md` (`anchor` field), `docs/help/06-enter-value.md` (one line), release notes; remove the
   known limit from the next notes.

### Open questions (need the user / live DCS)

1. The issue screenshots can't be opened here. What exactly does the display show at 9 and at 10 — text, length,
   padding? Which DCS-BIOS output is picked as display?
2. Does the text move left (right-aligned) or right (left-aligned)? Both are covered by the plan, but the test
   cockpit should match the real one.
3. Is it really 0-15, or 3-15 / 30-87 MHz with a gap? This sets the range Learn should find.

## Order of work (one PR each)

1. Issue 4: sorted command list + tests + help line.
2. Issue 5: model + layout + engine + tests.
3. Issue 5: Learn + editor + tests + docs, then a check in DCS with the A-10C II.
