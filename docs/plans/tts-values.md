# Plan: Say live cockpit values (TTS values)

Status: planned, not built.

## Goal

A Say step can speak a live DCS-BIOS value, such as the clock or the throttle position. The raw value is often not
easy to speak (`203422`, `52428`). The user turns it into speakable text once, as a named **value**, and uses it in
any Say step: `Time is {clock}`, `Throttle {throttle} percent`.

## Decisions (agreed)

| Topic | Decision |
|---|---|
| Where the format lives | A named value, not inline in the Say text. The Say text only holds `{name}`. |
| Scope | Per profile (one aircraft). Any command can use it. |
| Source | One DCS-BIOS output per value. Picked with the same control search the other steps use. |
| Modes | One mode per value: **slice** or **remap**. |
| Slice | Strings and integers. A template like `{0-1}:{2-3}:{4-5}`, 0-based, end included. Built by clicking characters of a sample. |
| Pad | Integers in slice mode can be padded with zeros to N digits (`509` → `000509`). Filled in from the sample. |
| Remap | Integers only. Linear: in-min/in-max → out-min/out-max, then round to 0–2 decimals (default 0). Result is clamped to the out range. In-max is filled in from `max_value`. |
| Units / words | Go in the Say text, not the value. |
| Speaking | The text goes to the voice as is. No special number reading. |
| Name clash | A spoken slot of the same name wins inside that command. The editor shows a warning. |
| No data | No DCS connection or no value: the Say step is skipped and a line goes to the trace/log. |
| No DCS while editing | The user types a sample by hand. Same UI. |
| Preview | The editor shows raw → result, live while DCS runs, plus **Play**. |
| Rename | Updates `{name}` in all Say steps. |
| Delete | Warns "used in N commands", then deletes. |

## 1. Profile format

A new top-level `values` list in the profile:

```json
"values": [
  { "name": "clock", "output": "CLOCK", "mode": "slice", "pad": 6, "template": "{0-1}:{2-3}:{4-5}" },
  { "name": "throttle", "output": "THROTTLE_L", "mode": "remap",
    "in": [0, 65535], "out": [0, 100], "decimals": 0 }
]
```

The Say step stays `{ "type": "say", "text": "Throttle {throttle} percent" }`.

## 2. Core

- `TtsValue` model and a `TtsValueFormatter`: raw value (int or string) → text. Pure code, unit tested.
- Slice template parser: `{a-b}` pieces and literal text. A range past the end of the value is an error that shows in
  the editor.
- When a Say step runs: spoken slots first, then profile values read from the live buffer. A missing value or no
  connection → skip and log.

## 3. UI

- **Values editor** (new window, "TTS" button next to Settings in the main window):
  - List of the profile's values: new, rename, delete.
  - Right side: name, source output (control search), mode, then slice or remap settings.
  - Slice: the sample as clickable cells (live value, or typed). Selecting cells inserts `{a-b}` into the template.
  - Preview line raw → result, and **Play**.
- **Say editor**: an "Insert value…" button opens the Values editor in pick mode. Pick one or make a new one;
  `{name}` goes into the text at the cursor.
- The main window gets no new column.

## 4. Help and tests

- Update `docs/help/04-actions.md` (Say) and add a short help page for the Values editor with a screenshot.
- Tests: formatter (slice, pad, remap, clamp, decimals), template parser, engine skip/log, profile round trip,
  rename/delete, view models.

## Open points

- Exact look of the Values editor (mock-up before building?).
- Button name "TTS" is for now.
