# Profile format

One JSON file per jet, in `%LOCALAPPDATA%\DcsVoiceCom\profiles\`. Variants with the same DCS-BIOS control files
share one profile, named after the variant that matches the control file (`F-16C_50.json` also covers the F-16D),
else after the control file itself (`F-14.json` covers every Tomcat, `MirageF1.json` every Mirage F1).

For one variant only, name the file after the `_ACFT_NAME` DCS-BIOS reports (`F-14BU.json`). The app looks for that
first, then the shared profile.

`common.json` has the same format and is merged under every aircraft profile: its commands are added unless the
aircraft profile has a command with the same name.

**Server profiles** (`server-<name>.json`) have the same format plus `servers`: the multiplayer servers they are
for. While you're on a matching server they are merged in between: aircraft, then server profiles, then common.
The DCS hook (Settings → DCS hook → Install) tells the app which server you're on.

```json
{ "aircraft": "server-ShadowReapers", "servers": ["ShadowReapers*", "65.109.30.115:10321"], "commands": [ ] }
```

A `servers` line is a server name or address, ignoring case and outer spaces. `*` stands for any text, anywhere:
`ShadowReapers*`, `*Reapers*`, `*PvE`. Without `*` the whole name must match.

```json
{
  "aircraft": "F-14",
  "keywords": ["TACAN"],
  "commands": [
    {
      "name": "Gear up",
      "phrases": ["[gear;landing gear] up"],
      "steps": [ { "type": "bios", "control": "PLT_GEAR_LEVER", "arg": "0", "skip_if_already": true } ]
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `keywords` | Domain words passed to the recognizer; add only words it gets wrong |
| `module_version` | Optional: the DCS-BIOS version the control IDs were checked against |
| command `enabled` | `false` keeps the command but leaves its phrases out of matching |
| command `seeded` | Marks commands generated from DCS-BIOS definitions that you have not edited or enabled yet (see [Seeding](#seeding)) |

## Schema reference

### File rules

- UTF-8 JSON. Property names are `snake_case` and read case-insensitively. Enum values are `snake_case` strings
  (`"wheels"`, `"no_match"`).
- `//` comments and trailing commas are accepted when reading; the app writes plain, indented JSON without them.
- Unknown properties are ignored. Match thresholds are app settings (Settings), not profile fields.
- A missing optional property takes its default; properties with the default value or `null` may be left out.
- A step is an object with a `"type"` (any position in the object). A missing or unknown `type` makes the file fail
  to load.
- Problems that do not stop loading (an unknown slot type, two commands with the same phrase, a button press with equal
  `arg` and `release`) are listed in the app's log as `Profile problem: …`; the affected phrase is left out or the first command wins.

```json
{
  // comments and trailing commas are fine
  "aircraft": "F-14",
  "commands": [
    { "name": "Wait", "phrases": ["hold on"], "steps": [ { "type": "wait", "ms": 500, }, ] }
  ]
}
```

### Profile (top level)

| Field | Type | Default | Meaning |
|---|---|---|---|
| `aircraft` | string | `""` | DCS-BIOS `_ACFT_NAME`, `"common"` for `common.json`, `"server-…"` for a server profile |
| `servers` | string[] | none | Server profiles only: names or addresses it turns on for (`*` = any text) |
| `module_version` | string | none | DCS-BIOS version the control IDs were checked against |
| `keywords` | string[] | `[]` | Extra words for the recognizer |
| `commands` | Command[] | `[]` | The commands |

```json
{
  "aircraft": "F-16C_50",
  "module_version": "0.9.1",
  "keywords": ["TACAN", "bullseye"],
  "commands": [ ]
}
```

A server profile adds `servers`:

```json
{ "aircraft": "server-ShadowReapers", "servers": ["ShadowReapers*"], "commands": [ ] }
```

### Command

| Field | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | `""` | Shown in the app and the log. Unique per profile: when merging, the first command with a name wins |
| `description` | string | none | Free text for you |
| `phrases` | string[] | `[]` | Voice lines, see [Phrases](#phrases) |
| `steps` | Step[] | `[]` | Run in order when a phrase matches |
| `critical` | bool | `false` | Needs the critical threshold from Settings |
| `enabled` | bool | `true` | `false` leaves the phrases out of matching |
| `seeded` | bool | `false` | Generated and not yet edited or enabled |

```json
{
  "name": "Master arm on",
  "description": "Checked on the F-16C",
  "phrases": ["master arm on", "arm the jet"],
  "critical": true,
  "enabled": true,
  "steps": [ { "type": "bios", "control": "MASTER_ARM_SW", "arg": "1" } ]
}
```

### Steps

Every step has `type`. Defaults are used when a field is left out.

| `type` | Field | Type | Default |
|---|---|---|---|
| `bios` | `control` | string | `""` |
| | `arg` | string | `""` |
| | `skip_if_already` | bool | `false` |
| | `press_ms` | int | none (plain line) |
| | `release` | string | `"0"` |
| `bios_until` | `control` | string | `""` |
| | `output` | string | `control` |
| | `target` | string | `""` (may be `{slot}`) |
| | `scale` | number | `1` |
| | `tolerance` | int | `0` |
| | `inc`, `dec` | string | `"INC"`, `"DEC"` |
| | `max_steps` | int | `60` |
| | `delay_ms` | int | `80` |
| `key` | `keys` | string | `""` |
| | `hold_ms` | int | none (app setting) |
| `wait` | `ms` | int | `0` |
| `wait_for` | `until` | Condition | empty |
| | `timeout_ms` | int | `10000` |
| `if` | `condition` | Condition | empty |
| | `then`, `else` | Step[] | `[]` |
| `cue` | `cue` | `accepted` \| `no_match` \| `refused` | `accepted` |
| `foreach_char` | `value` | string | `""` |
| | `map` | object (string → string) | `{}` |
| | `steps` | Step[] | `[]` |
| `enter_value` | see below | | |

One small example per step type (control names are examples; use the exact DCS-BIOS names for your jet):

```json
{ "type": "bios", "control": "PLT_GEAR_LEVER", "arg": "0", "skip_if_already": true }
{ "type": "bios", "control": "UFC_ENT", "arg": "1", "press_ms": 50, "release": "0" }
{ "type": "bios_until", "control": "HSI_HDG_KNOB", "target": "{deg}", "scale": 182, "tolerance": 2, "max_steps": 80 }
{ "type": "key", "keys": "RCtrl+RShift+G", "hold_ms": 100 }
{ "type": "wait", "ms": 300 }
{ "type": "wait_for", "until": { "output": "ENGINE_RPM", "op": ">=", "value": "60" }, "timeout_ms": 20000 }
{ "type": "if", "condition": { "output": "GEAR_LEVER", "op": "=", "value": "1" },
  "then": [ { "type": "cue", "cue": "accepted" } ],
  "else": [ { "type": "bios", "control": "GEAR_LEVER", "arg": "1" } ] }
{ "type": "cue", "cue": "refused" }
{ "type": "foreach_char", "value": "{code}", "map": { ".": "DOT" },
  "steps": [ { "type": "bios", "control": "UFC_{char}", "arg": "1", "press_ms": 50 } ] }
```

**Condition**: `{ "output": "PLT_GEAR_LEVER", "op": "=", "value": "1" }`. `op` is `=`, `!=`, `<`, `<=`, `>`
or `>=` (default `=`). Integers compare as numbers; strings only with `=` and `!=`.

```json
{ "output": "APU_STATE", "op": "!=", "value": "RUNNING" }
```

`{slot}` placeholders work in `control`, `arg`, `target`, `keys` and `value` fields. `{char}` is available inside
`foreach_char`.

### `enter_value` fields

| Field | Type | Default | Notes |
|---|---|---|---|
| `value` | string | `""` | Required, e.g. `{freq}` |
| `method` | `wheels` \| `keypad` | `wheels` | |
| `display` | string | none | DCS-BIOS string output |
| `sample` | string | none | Display text at set-up time |
| `fields` | Field[] | none | Display layout (current format) |
| `decimals` | int | none | Implied decimals |
| `timeout_ms` | int | `1500` | Wait for the display |
| `up`, `down` | string[] | `["2","1"]`, `["0","1"]` | One click, wheels |
| `max_steps` | int | `40` | Most clicks per wheel |
| `delay_ms` | int | `80` | Delay after each line sent |
| `press` | string[] | `["1","0"]` | One key press, keypad |
| `keys` | object (char → control) | none | Keypad; `""` skips the character |

Smallest `enter_value` (a keypad, no fields):

```json
{ "type": "enter_value", "value": "{text}", "method": "keypad", "keys": { "A": "CDU_A", "1": "CDU_1" } }
```

With a display and fields, see the full examples under [`enter_value`](#enter_value).

Field (in `fields`):

| Field | Type | Default | Notes |
|---|---|---|---|
| `at` | int | `0` | First character, from 0 |
| `len` | int | `1` | Characters |
| `kind` | `wheel` \| `keypad` \| `read_only` | `wheel` | |
| `control` | string | none | Wheel |
| `range` | string | none | Wheel: `0-9`, `22-39` or `X,Y` |
| `mode` | `click` \| `step` \| `set` | `click` | Wheel |
| `flip` | bool | `false` | Wheel: swap up and down (set by test clicks) |
| `align` | `left` \| `right` | `left` | Keypad |

```json
{ "at": 0, "len": 2, "kind": "wheel", "control": "PLT_UHF1_110_DIAL", "range": "22-39", "mode": "click" }
{ "at": 3, "len": 1, "kind": "read_only" }
{ "at": 1, "len": 22, "kind": "keypad", "align": "left" }
```

Older `enter_value` fields (still read, see [Older steps](#enter_value)): `min`, `max`, `wheels[]`
(`control`, `place`, `width`, `read`, `read_scale`, `mode`, `flip`), `verify` (`output`, `timeout_ms`), `before`,
`after`.

## Phrases

- `gear up;landing gear up` — alternatives separated by `;`
- `[gear;landing gear] up` — inline group; `[please;]` makes a word optional
- Phrases are case- and punctuation-insensitive.

### Spoken parameters (slots)

The slot name sets its type. There are three: `{number}`, `{freq}` and `{text}`. Any other name needs a type
after a colon (`{alt:number}`, `{com2:freq}`, `{wp:text}`, a range or options). Use that when a phrase has two
slots of the same type. A slot without a known type (`{num}`) is reported as a problem and the phrase is left out.

| Slot | Accepts | Value |
|---|---|---|
| `{number}`, `{name:number}` | whole number: "seventy three", "one two six" | `73` |
| `{freq}`, `{name:freq}` | any number, decimals allowed: "two five one decimal zero", "251.0" | `251.0` |
| `{text}`, `{name:text}` | words joined without spaces, upper-cased. NATO words become letters and number words digits ("decimal" → `.`); "slash", "dash" and "space" give `/`, `-` and a space | "TGT 2" → `TGT2`, "tango golf tango two" → `TGT2` |
| `{channel:1-126}` | whole number in range: "seventy three", "one hundred nine" | `73` |
| `{band:X\|Y}` | one option, NATO words for letters: "x-ray" | `X` |
| `{band:X=0\|Y=1}` | same, but the step receives the value after `=` | `0` |

Numbers work in English and German ("zwo fünf eins komma null", "dreiundsiebzig"). An out-of-range or
unparseable value plays *no match* and runs nothing. A phrase needs at least one word besides its slots.

In steps, `{name}` inserts the value. Whole numbers also take one operation: `{channel/10}`, `{channel%10}`,
`{deg*182}`, `{n+1}`, `{n-1}`.

## Steps

| `type` | Fields | Notes |
|---|---|---|
| `bios` | `control`, `arg`, `skip_if_already`, `press_ms`, `release` | Any DCS-BIOS argument: value, `INC`, `DEC`, `TOGGLE`, `+3200`. `skip_if_already` skips when the output already equals a numeric `arg`. With `press_ms` it is a **button press**: sends `arg` (push in), waits `press_ms`, then sends `release` (default `0`). The release is sent even when the command is cancelled. `arg` and `release` must differ, and `press_ms` must be 0 or more. |
| `bios_until` | `control`, `target`, `output`, `scale`, `tolerance`, `inc`, `dec`, `max_steps`, `delay_ms` | Steps a knob and reads back until `output` (default `control`) is within `tolerance` of `target × scale`; stops when it overshoots; refused after `max_steps`. |
| `key` | `keys`, `hold_ms` | Scan-code chord such as `RCtrl+RShift+G`; left/right modifiers are distinct. DCS must have focus. |
| `wait` | `ms` | Fixed delay |
| `wait_for` | `until: {output, op, value}`, `timeout_ms` | Waits on cockpit state; on timeout the sequence stops and plays *refused*. |
| `if` | `condition: {output, op, value}`, `then`, `else` | Ops: `=`, `!=`, `<`, `<=`, `>`, `>=` (strings: `=` and `!=` only). |
| `cue` | `cue`: `accepted`, `no_match`, `refused` | Plays a cue mid-sequence |
| `foreach_char` | `value`, `map`, `steps` | Runs `steps` per character of `value` with `{char}` bound, e.g. one UFC key per digit; `map` renames characters (`"." → "DOT"`). |
| `enter_value` | `value`, `method`, `display`, `fields`, and the fields below | Puts a spoken value into the cockpit: a number on radio wheels, or text on a keypad, checked on one display. Set it up in the app (*+ Enter value*, then *Edit*). |

### `enter_value`

An `enter_value` step names one **display** (`display`) and splits it into **fields** by character position. Every
wheel is read back from its own characters of that display, and the same display is checked at the end. The step is
either **wheels** (switches and dials) or **keypad**, never both.

| Field | Meaning |
|---|---|
| `value` | The spoken slot, e.g. `{freq}` or `{text}` |
| `method` | `wheels` or `keypad` |
| `display` | A DCS-BIOS string output that shows the value, e.g. `PLT_UHF_DISP` |
| `sample` | The display's text when the step was set up (`"251.000"`): its length, decimal point and read-only characters |
| `fields` | The parts of the display (below) |
| `timeout_ms` | How long to wait for the display to show the value (default `1500`) |
| `decimals` | Implied decimals for a display without a point: `1` when it shows `2513` for 251.3 |
| `up`, `down`, `delay_ms`, `max_steps` | Wheels: one click up / down (default `2,1` and `0,1`), the delay after each line, and the most clicks per wheel (default `40`). Shared by all wheels. |
| `keys`, `press` | Keypad: character → key control (`""` skips the character), and the arguments of one key press (default `1,0`) |

Each field has `at` (first character, counting from 0), `len` (number of characters) and `kind`:

- `wheel`: `control`, `range` and `mode`. `range` is what the wheel can show, in click order: a span such as `0-9`
  or `22-39`, or a list such as `X,Y` or `00,25,50,75`. Without a range it is `0` to `9…9` for its width.
- `keypad`: `align` (`left` or `right`), where the typed text sits in the field. A keypad step has exactly one
  keypad field and types the whole value into it as one line. For a second field (latitude, then longitude), use a
  second step.
- `read_only`: text that never changes, such as the decimal point or the `[` `]` around a CDU line. Characters in no
  field are ignored.

Place and width are not stored: they follow from where a field sits. The value is lined up on the display by its
decimal point (`263.175` on `251.000`: the wheel on `25` gets `26`, the wheel on `000`'s last two characters gets
`75`). Without a decimal point it is right-aligned over the wheels (`73Y` on `109X` → ` 73Y`). A blank character
counts as `0` on a wheel whose range starts at 0, for displays that hide leading zeros.

Wheel modes:

- `click`: a spring wheel. One click up sends `up`, down sends `down`. The F-14 ARC-159 wheels work like this: their
  `set_state 0..2` is down / rest / up, not a digit.
- `step`: `INC` / `DEC` per click.
- `set`: sends the target as the `set_state` argument: the number for a span, the position in the list for a list.
  Only for wheels whose `set_state` takes that directly.

The app turns the wheels from left to right until the display shows your value. If a click turns a wheel the wrong
way, the app flips up and down for that wheel. If a wheel doesn't move, goes past its target, or needs more than
`max_steps` clicks, the command stops with *refused*. A jump over the end of the range (39 → 22) counts as one
click. If the value can't be shown, nothing is sent. That happens when it is too long, has more decimals than the
display, is outside a wheel's range, or has a character with no key. If the display already shows the value, the step
does nothing. Keypads are never cleared first. Add a CLR press as its own step if you need it.

In a voice line, a text slot stops at the next word of the phrase. When a number slot follows right after
(`callsign {cs:text} {chan:number}`), the trailing number words go to that slot: "alpha bravo two five" → AB, 25.
A text slot takes any word, so a misheard word is typed too; the display check catches that.

```json
{ "type": "enter_value", "value": "{freq}", "method": "wheels",
  "display": "PLT_UHF_DISP", "sample": "225.025",
  "fields": [
    { "at": 0, "len": 2, "kind": "wheel", "control": "PLT_UHF1_110_DIAL", "range": "22-39", "mode": "click" },
    { "at": 2, "len": 1, "kind": "wheel", "control": "PLT_UHF1_1_DIAL", "range": "0-9", "mode": "click" },
    { "at": 3, "len": 1, "kind": "read_only" },
    { "at": 4, "len": 1, "kind": "wheel", "control": "PLT_UHF1_01_DIAL", "range": "0-9", "mode": "click" },
    { "at": 5, "len": 2, "kind": "wheel", "control": "PLT_UHF1_025_DIAL", "range": "00,25,50,75", "mode": "click" } ] }

{ "type": "enter_value", "value": "{text}", "method": "keypad",
  "display": "CDU_LINE9", "sample": "[N4126.5E04155.3       ]",
  "fields": [
    { "at": 0, "len": 1, "kind": "read_only" },
    { "at": 1, "len": 22, "kind": "keypad", "align": "left" },
    { "at": 23, "len": 1, "kind": "read_only" } ],
  "keys": { "A": "CDU_A", "B": "CDU_B", "1": "CDU_1", ".": "CDU_POINT", " ": "" } }
```

**Preset channels** use the same step: one wheel in `step` mode on the preset selector, with range `1-20`, on the
characters where the display shows the channel. Set it up while the radio is in preset mode, so the display shows a
channel. *Fill from radio…* offers the selector as "… presets".

Control names must match DCS-BIOS exactly; the app corrects the case of known names (`ARC210_100kHZ_SEL` →
`ARC210_100KHZ_SEL`) when it sends them.

**Older steps** (before beta 5) without `fields` still run as they did: `wheels` (each with `control`, `place`,
`width`, `read`, `read_scale`, `mode`), `min`/`max`, `verify: {output, timeout_ms}`, and for keypads `before`/`after`
keys. The Enter value window converts a wheels step when it opens it, using the live display (or, without DCS, a
layout guessed from `max`). Check the fields before saving: the display may be in another mode (a preset step opened
while the radio shows a frequency). A keypad step stays as it is until its field is picked on the live display; its
`before`/`after` keys keep working but are no longer edited in the window.

A new push-to-talk press cancels a running sequence.

Key names: `LCtrl RCtrl LShift RShift LAlt RAlt LWin RWin`, `A`-`Z`, `0`-`9`, `F1`-`F12`, `Esc Tab Enter Space Backspace
CapsLock ScrollLock Insert Delete Home End PageUp PageDown Up Down Left Right Minus Equals LBracket RBracket Semicolon
Apostrophe Grave Backslash Comma Period Slash`, `Num0`-`Num9 NumDecimal NumMultiply NumMinus NumPlus NumDivide NumEnter`.

## Seeding

**Seed…** above the command list writes starter commands from the aircraft's DCS-BIOS definitions (read from your
own DCS-BIOS install; nothing is bundled). Tick the panels you want, look through the preview, and add them.

| Control | Commands |
|---|---|
| Switch whose definition names its positions | One per position: “Hook extension handle up”, “… down”; *skip if already* is on |
| Two-position switch without names | “… on” (1) and “… off” (0); the description says to check which is which |
| Number wheel or counter (positions `0`-`9`, `2 4 6 8`) | One command with the number as a parameter: “Weapon quantity 1s {position:0-9}” |
| More than 12 named positions, or unnamed positions | One command with a spoken parameter: “TACAN mode position {position:0-4}” |
| Push button | “Press …”: one button press step (1 in, 50 ms, 0 out) |
| Action-only control | “Press …” with its argument (usually `TOGGLE`) |
| Knobs, dials, lights, displays, deprecated controls | None: write these by hand with `bios_until` or INC/DEC |

Voice lines come from the control's description: the crew station prefix (PILOT, RIO), control-kind words
(switch, knob, button) and how-to hints (“- Push to …”) are dropped, and numbers get a spelled-out alternative
(“UFC keyboard 1” / “UFC keyboard one”). When two controls would share a voice line they get their crew station
(“Pilot canopy jettison” / “RIO canopy jettison”), then their panel, then their DCS-BIOS ID. Single-key buttons
(“3”, “DIR”) always get their panel: “Press CDNU 3”. Jettison, ejection, fire, dump, cut-off, canopy, emergency
and master arm controls are marked critical.

Every seeded command starts **disabled** with `"seeded": true`. Check its voice line and steps, test it, then tick
it. Editing or enabling a command clears `seeded`: it is yours from then on. Seeding again replaces only commands
that are still marked seeded, and skips any voice line or exact input (control and argument) that one of your
commands or the common profile already has; the preview shows those greyed out with the command that covers them.
*Remove seeded commands* deletes the rest in one go.

