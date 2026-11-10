# Profile format

One JSON file per aircraft, named after its DCS-BIOS `_ACFT_NAME` (`F-16C_50.json`, `FA-18C_hornet.json`), in
`%LOCALAPPDATA%\DcsVoiceCom\profiles\`. Variants with the same DCS-BIOS control files share a profile: `F-14.json`
covers every Tomcat, and `F-16C_50.json` also covers the F-16D. A file named after the variant (`F-14BU.json`) comes first. `common.json` has the same format and is merged under every aircraft
profile: its commands are added unless the aircraft profile has a command with the same name.

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
  "threshold": 85,
  "critical_threshold": 95,
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
| `threshold` | Minimum match score (0-100) for any command |
| `critical_threshold` | Minimum score for commands with `"critical": true`; between the two the command is refused |
| `keywords` | Domain words passed to the recognizer; add only words it gets wrong |
| `module_version` | Optional: the DCS-BIOS version the control IDs were checked against |
| command `enabled` | `false` keeps the command but leaves its phrases out of matching |
| command `seeded` | Marks commands generated from DCS-BIOS definitions that you have not edited or enabled yet (see [Seeding](#seeding)) |

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

The wheels are turned left to right until their characters show the target. A click that turns the wrong way swaps up
and down; a wheel that doesn't move, passes its target or runs out of `max_steps` stops the command with *refused*.
A jump across the end of the range (39 → 22) counts as one click. A value that can't be shown (too long, more
decimals than the display, outside a wheel's range, a character without a key) stops the command before anything is
sent. If the display already shows the value, the step does nothing. Keypad text is never cleared first: add a CLR
press as its own step when needed.

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

